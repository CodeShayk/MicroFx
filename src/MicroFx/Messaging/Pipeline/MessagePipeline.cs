using System.Diagnostics;
using System.Text.Json;
using MicroFx.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MicroFx.Messaging.Pipeline;

/// <summary>State threaded through the handler pipeline for one delivery.</summary>
public sealed class MessagePipelineContext
{
    internal MessagePipelineContext(
        TransportDelivery delivery, SubscriptionSpec subscription, IServiceProvider services)
    {
        Delivery = delivery;
        Subscription = subscription;
        Services = services;
    }

    /// <summary>The raw delivery as received.</summary>
    public TransportDelivery Delivery { get; }

    /// <summary>The subscription processing it.</summary>
    public SubscriptionSpec Subscription { get; }

    /// <summary>A scoped service provider for this delivery.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The decoded envelope, once the decode step has run.</summary>
    public Envelope? Envelope { get; internal set; }

    /// <summary>The resolved CLR message type, once type resolution has run.</summary>
    public Type? MessageType { get; internal set; }

    /// <summary>The deserialized message, once deserialization has run.</summary>
    public object? Message { get; internal set; }

    /// <summary>Handler-facing context, once it has been built.</summary>
    public MessageContext? MessageContext { get; internal set; }
}

/// <summary>The rest of the pipeline.</summary>
public delegate Task<HandlerResult> MessagePipelineStep(
    MessagePipelineContext context, CancellationToken cancellationToken);

/// <summary>
/// One ordered step in the handler pipeline.
/// </summary>
/// <remarks>
/// Mirrors the HTTP middleware pipeline deliberately, so a message entry point and an HTTP entry
/// point behave alike — the same validation, the same tenancy, the same authorization decisions.
/// </remarks>
public interface IMessageMiddleware
{
    /// <summary>Runs this step, calling <paramref name="continuation"/> to continue.</summary>
    Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken);
}

/// <summary>
/// Composes the middleware chain and runs it for each delivery.
/// </summary>
/// <remarks>
/// The order below is load-bearing and fixed by the platform:
/// decode → kind check → type resolution → expiry → deserialize → tenancy → authorize → dedupe →
/// handler. Cheap rejections of hostile or malformed input come first, so a malformed envelope
/// never reaches deserialization and an unauthorized message never reaches the inbox.
/// </remarks>
internal sealed partial class MessagePipeline(IReadOnlyList<IMessageMiddleware> middleware)
{
    private readonly MessagePipelineStep _chain = Build(middleware);

    public Task<HandlerResult> ProcessAsync(
        MessagePipelineContext context, CancellationToken cancellationToken) =>
        _chain(context, cancellationToken);

    private static MessagePipelineStep Build(IReadOnlyList<IMessageMiddleware> middleware)
    {
        // The terminal step: everything upstream must have produced a result by here.
        MessagePipelineStep chain = (_, _) => Task.FromResult(
            HandlerResult.Permanent("pipeline-incomplete"));

        for (var i = middleware.Count - 1; i >= 0; i--)
        {
            var step = middleware[i];
            var downstream = chain;
            chain = (context, token) => step.InvokeAsync(context, downstream, token);
        }

        return chain;
    }
}

/// <summary>Decodes the envelope from transport headers.</summary>
/// <remarks>
/// First in the chain, and the only step that sees raw broker input. A malformed envelope is
/// <see cref="HandlerOutcome.Permanent"/>: waiting will never make it well-formed, so retrying
/// would only move a hostile message around the topology.
/// </remarks>
internal sealed class EnvelopeDecodeMiddleware : IMessageMiddleware
{
    public Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        if (!EnvelopeCodec.TryDecode(context.Delivery.Headers, out var envelope, out var reason))
        {
            MessagingDiagnostics.Rejected(context.Subscription.ConsumerGroup, reason);
            return Task.FromResult(HandlerResult.Permanent($"malformed-envelope:{reason}"));
        }

        context.Envelope = envelope;
        return continuation(context, cancellationToken);
    }
}

/// <summary>
/// Verifies the message kind matches what the subscription expects.
/// </summary>
/// <remarks>
/// Catches the classic topology error where an event subscription is bound to a command
/// destination. Without this the handler runs on a message it was never written for and the
/// behaviour is silently, subtly wrong rather than loudly broken.
/// </remarks>
internal sealed class KindCheckMiddleware(MessageKind expected) : IMessageMiddleware
{
    public Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        var actual = context.Envelope!.Kind;

        if (actual != expected)
        {
            MessagingDiagnostics.Rejected(context.Subscription.ConsumerGroup, "kind-mismatch");
            return Task.FromResult(HandlerResult.Permanent(
                $"kind-mismatch:expected-{expected.ToString().ToLowerInvariant()}"));
        }

        return continuation(context, cancellationToken);
    }
}

/// <summary>
/// Resolves the wire type name to a registered CLR type.
/// </summary>
/// <remarks>
/// The security-critical step. Resolution goes through the registry populated at composition time,
/// never through reflection on a caller-supplied name — which would let anyone able to publish to
/// the broker instantiate arbitrary types in this process.
/// </remarks>
internal sealed class TypeResolutionMiddleware(MessageTypeRegistry registry) : IMessageMiddleware
{
    public Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        if (!registry.TryResolve(context.Envelope!.Type, out var type))
        {
            MessagingDiagnostics.Rejected(context.Subscription.ConsumerGroup, "unknown-type");
            return Task.FromResult(HandlerResult.Permanent("unknown-type"));
        }

        context.MessageType = type;
        return continuation(context, cancellationToken);
    }
}

/// <summary>Discards a message that arrived after its expiry.</summary>
/// <remarks>
/// Discard rather than dead-letter: an expired message is not a failure, it is a message whose
/// moment passed. Dead-lettering it would fill the failure store with things nobody needs to triage.
/// </remarks>
internal sealed class ExpiryMiddleware(TimeProvider clock) : IMessageMiddleware
{
    public Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        if (context.Envelope!.IsExpired(clock.GetUtcNow()))
        {
            MessagingDiagnostics.Expired(context.Subscription.ConsumerGroup);
            return Task.FromResult(HandlerResult.Discard("expired"));
        }

        return continuation(context, cancellationToken);
    }
}

/// <summary>Deserializes the payload into the resolved type.</summary>
/// <remarks>
/// Bounded depth and size, and a failure is permanent — a payload that will not parse now will not
/// parse in thirty seconds either.
/// </remarks>
internal sealed class DeserializationMiddleware(JsonSerializerOptions serializerOptions) : IMessageMiddleware
{
    public async Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        try
        {
            context.Message = JsonSerializer.Deserialize(
                context.Delivery.Body.Span, context.MessageType!, serializerOptions);
        }
        catch (JsonException)
        {
            // The exception message quotes the offending payload, so it is deliberately not
            // propagated into the reason token.
            MessagingDiagnostics.Rejected(context.Subscription.ConsumerGroup, "deserialization-failed");
            return HandlerResult.Permanent("deserialization-failed");
        }

        if (context.Message is null)
        {
            return HandlerResult.Permanent("null-payload");
        }

        return await continuation(context, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Builds the handler-facing context and establishes the log scope.</summary>
internal sealed class MessageContextMiddleware : IMessageMiddleware
{
    public async Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        var envelope = context.Envelope!;

        context.MessageContext = new MessageContext(
            envelope.Id,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.TenantId,
            Principal: null,
            context.Subscription.ConsumerGroup,
            envelope.Attempt,
            envelope.Time,
            context.Delivery.IsRedelivery,
            envelope.IsReplay,
            envelope.Headers);

        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MicroFx.Messaging");

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = envelope.Id,
            ["CorrelationId"] = envelope.CorrelationId,
            ["ConsumerGroup"] = context.Subscription.ConsumerGroup,
            ["MessageType"] = envelope.Type,
            ["Attempt"] = envelope.Attempt,
        });

        Activity.Current?.SetTag("messaging.message.id", envelope.Id);
        Activity.Current?.SetTag("microfx.attempt", envelope.Attempt);

        return await continuation(context, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Suppresses a duplicate delivery of a message this consumer group already processed.
/// </summary>
/// <remarks>
/// Placed after authorization and before the handler, so an unauthorized message never consumes a
/// dedupe slot. The reservation is released when handling fails, otherwise a transient failure
/// would make the message permanently un-retryable.
/// </remarks>
internal sealed class InboxMiddleware : IMessageMiddleware
{
    public async Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        // Resolved from the delivery scope, never captured. A durable inbox holds a DbContext, and
        // a pipeline built once would otherwise share one across every delivery for the life of
        // the process.
        var inbox = context.Services.GetRequiredService<IInboxStore>();

        var group = context.Subscription.ConsumerGroup;
        var messageId = context.Envelope!.Id;

        if (!await inbox.TryBeginAsync(group, messageId, cancellationToken).ConfigureAwait(false))
        {
            MessagingDiagnostics.Deduplicated(group);
            return HandlerResult.Success();
        }

        HandlerResult result;
        try
        {
            result = await continuation(context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await inbox.ReleaseAsync(group, messageId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (result.Outcome == HandlerOutcome.Transient)
        {
            // Only transient failures release: a permanent failure is dead-lettered and must not be
            // reprocessed if the same id somehow returns.
            await inbox.ReleaseAsync(group, messageId, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>Enforces the per-handler wall-clock budget.</summary>
/// <remarks>
/// Client-side by necessity: a broker holds a delivery until it is acknowledged and has no notion of
/// a handler deadline, so an unbounded handler would pin a prefetch slot indefinitely.
/// </remarks>
internal sealed class HandlerTimeoutMiddleware : IMessageMiddleware
{
    public async Task<HandlerResult> InvokeAsync(
        MessagePipelineContext context, MessagePipelineStep continuation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(context.Subscription.HandlerTimeout);

        try
        {
            return await continuation(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            return HandlerResult.Transient("handler-timeout");
        }
    }
}
