using MicroFx.Messaging.Pipeline;
using MicroFx.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MicroFx.Messaging;

/// <summary>One registered subscription and the handler that serves it.</summary>
/// <param name="Specification">The subscription.</param>
/// <param name="MessageType">The CLR message type.</param>
/// <param name="HandlerType">The handler type, resolved per delivery from a scope.</param>
/// <param name="Kind">The message kind this subscription expects.</param>
internal sealed record SubscriptionRegistration(
    SubscriptionSpec Specification, Type MessageType, Type HandlerType, MessageKind Kind);

/// <summary>
/// Runs the registered subscriptions: builds a pipeline per subscription, translates handler results
/// into transport dispositions, and drains cleanly on shutdown.
/// </summary>
/// <remarks>
/// This is where the platform's retry <em>policy</em> meets the transport's retry <em>mechanism</em>.
/// The decision — retry, dead-letter, or complete, and after how long — is made here; realising the
/// delay is the transport's problem or the scheduled store's.
/// </remarks>
internal sealed partial class ConsumerHost(
    IMessageTransport transport,
    IReadOnlyList<SubscriptionRegistration> registrations,
    MessageTypeRegistry types,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILoggerFactory loggerFactory,
    Microsoft.Extensions.Options.IOptions<MessagingOptions> options)
{
    private readonly List<ITransportSubscription> _subscriptions = [];
    private readonly ILogger _logger = loggerFactory.CreateLogger<ConsumerHost>();
    private readonly MessagingOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in registrations)
        {
            var pipeline = BuildPipeline(registration);

            var subscription = await transport.SubscribeAsync(
                registration.Specification,
                (delivery, token) => HandleAsync(registration, pipeline, delivery, token),
                cancellationToken).ConfigureAwait(false);

            _subscriptions.Add(subscription);
            LogSubscribed(_logger, registration.Specification.ConsumerGroup, registration.MessageType.Name);
        }
    }

    /// <summary>
    /// Stops delivery, then lets in-flight handlers finish.
    /// </summary>
    /// <remarks>
    /// Cancel first, wait second. Reversing the order would let new deliveries arrive during the
    /// drain and never finish; cancelling without waiting would abandon work already started.
    /// Anything still unacknowledged at close is redelivered by the transport, so nothing is lost.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
        {
            try
            {
                await subscription.CancelAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCancelFailed(_logger, subscription.ConsumerGroup, ex);
            }
        }

        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
    }

    private MessagePipeline BuildPipeline(SubscriptionRegistration registration)
    {
        // Order is fixed by the platform. Cheap rejections of hostile input come first, so a
        // malformed envelope never reaches deserialization and an unauthorized message never
        // reaches the inbox.
        var middleware = new List<IMessageMiddleware>
        {
            new EnvelopeDecodeMiddleware(),
            new KindCheckMiddleware(registration.Kind),
            new TypeResolutionMiddleware(types),
            new ExpiryMiddleware(clock),
            new DeserializationMiddleware(_options.SerializerOptions),
            new MessageContextMiddleware(),
            new HandlerTimeoutMiddleware(),
        };

        // Custom middleware runs after the platform's checks and before dedupe, so a service can
        // observe or reject a message without having to re-validate it.
        middleware.AddRange(_options.Middleware);

        middleware.Add(new InboxMiddleware());
        middleware.Add(new HandlerInvocationMiddleware(registration));

        return new MessagePipeline(middleware);
    }

    private async Task<DeliveryOutcome> HandleAsync(
        SubscriptionRegistration registration,
        MessagePipeline pipeline,
        TransportDelivery delivery,
        CancellationToken cancellationToken)
    {
        var group = registration.Specification.ConsumerGroup;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // A scope per delivery, matching a request scope: scoped services a handler depends on are
        // created and disposed around exactly one message.
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = new MessagePipelineContext(delivery, registration.Specification, scope.ServiceProvider);

        var traceParent = delivery.Headers.GetValueOrDefault("traceparent");
        using var activity = MessagingDiagnostics.StartConsume(
            group, delivery.Headers.GetValueOrDefault("microfx-type") ?? "unknown", traceParent);

        HandlerResult result;
        try
        {
            result = await pipeline.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-handling. Abandon rather than fail: the message returns for
            // redelivery to a replica that is still serving.
            return new DeliveryOutcome(DeliveryDisposition.Abandon, Reason: "shutting-down");
        }
        catch (Exception ex)
        {
            // An unhandled exception is transient by default, because most are, and the retry
            // ladder plus the attempt cap bounds the cost of being wrong.
            LogHandlerFailed(_logger, group, ex);
            result = HandlerResult.Transient("unhandled-exception");
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var attempt = context.Envelope?.Attempt ?? 1;

        MessagingDiagnostics.Consumed(
            group,
            context.Envelope?.Type ?? "unknown",
            result.Outcome.ToString().ToLowerInvariant(),
            elapsed,
            attempt);

        return Settle(registration, context, result, attempt);
    }

    private DeliveryOutcome Settle(
        SubscriptionRegistration registration,
        MessagePipelineContext context,
        HandlerResult result,
        int attempt)
    {
        var specification = registration.Specification;
        var group = specification.ConsumerGroup;

        switch (result.Outcome)
        {
            case HandlerOutcome.Success:
            case HandlerOutcome.Discard:
                return new DeliveryOutcome(DeliveryDisposition.Complete, Reason: result.Reason);

            case HandlerOutcome.Permanent:
                // Straight to the dead letter with no retries. A validation or authorization
                // failure will never pass by waiting, and retrying it only repeats the rejection.
                MessagingDiagnostics.DeadLettered(group, result.Reason ?? "permanent");
                LogDeadLettered(_logger, group, result.Reason ?? "permanent", attempt);
                return new DeliveryOutcome(DeliveryDisposition.DeadLetter, Reason: result.Reason);

            case HandlerOutcome.Transient:
                if (attempt >= specification.Retry.MaxAttempts)
                {
                    MessagingDiagnostics.DeadLettered(group, result.Reason ?? "attempts-exhausted");
                    LogDeadLettered(_logger, group, result.Reason ?? "attempts-exhausted", attempt);
                    return new DeliveryOutcome(
                        DeliveryDisposition.DeadLetter, Reason: result.Reason ?? "attempts-exhausted");
                }

                var next = attempt + 1;
                var delay = specification.Retry.DelayFor(next, result.RetryAfter);
                MessagingDiagnostics.Retried(group, next);
                LogRetrying(_logger, group, next, delay.TotalSeconds, result.Reason ?? "transient");

                // The incremented attempt travels with the redelivery. Without this the next
                // delivery re-reads attempt 1 from the original headers and the policy never
                // exhausts — an infinite retry loop that looks like a working backoff.
                return new DeliveryOutcome(
                    DeliveryDisposition.RetryLater,
                    delay,
                    result.Reason,
                    WithAttempt(context, next));

            default:
                return new DeliveryOutcome(DeliveryDisposition.DeadLetter, Reason: "unknown-outcome");
        }
    }

    /// <summary>Copies the delivery headers with the attempt count advanced.</summary>
    private static Dictionary<string, string> WithAttempt(
        MessagePipelineContext context, int attempt)
    {
        return new Dictionary<string, string>(context.Delivery.Headers, StringComparer.Ordinal)
        {
            ["microfx-attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Information,
        Message = "Subscribed {ConsumerGroup} to {MessageType}.")]
    private static partial void LogSubscribed(ILogger logger, string consumerGroup, string messageType);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Error,
        Message = "Handler on {ConsumerGroup} threw; treating as transient.")]
    private static partial void LogHandlerFailed(ILogger logger, string consumerGroup, Exception exception);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning,
        Message = "Dead-lettered a message on {ConsumerGroup} after attempt {Attempt}: {Reason}")]
    private static partial void LogDeadLettered(
        ILogger logger, string consumerGroup, string reason, int attempt);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Information,
        Message = "Retrying on {ConsumerGroup} as attempt {Attempt} in {DelaySeconds}s: {Reason}")]
    private static partial void LogRetrying(
        ILogger logger, string consumerGroup, int attempt, double delaySeconds, string reason);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Warning,
        Message = "Failed to cancel the subscription on {ConsumerGroup} during drain.")]
    private static partial void LogCancelFailed(
        ILogger logger, string consumerGroup, Exception exception);
}

/// <summary>Resolves the handler from the delivery scope and invokes it.</summary>
internal sealed class HandlerInvocationMiddleware(SubscriptionRegistration registration) : IMessageMiddleware
{
    public async Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        var handler = context.Services.GetService(registration.HandlerType);

        if (handler is null)
        {
            // A registered subscription with no resolvable handler is a composition defect, not a
            // message defect, so it must not be dead-lettered as if the message were at fault.
            throw new InvalidOperationException(
                $"No handler of type '{registration.HandlerType.Name}' is registered for " +
                $"subscription '{registration.Specification.ConsumerGroup}'.");
        }

        var method = registration.HandlerType.GetMethod("HandleAsync")
            ?? throw new InvalidOperationException(
                $"Handler '{registration.HandlerType.Name}' has no HandleAsync method.");

        var task = (Task<HandlerResult>)method.Invoke(
            handler, [context.Message, context.MessageContext, cancellationToken])!;

        return await task.ConfigureAwait(false);
    }
}
