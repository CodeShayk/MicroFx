using System.Collections.Concurrent;
using System.Text;
using MicroFx.Messaging.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace MicroFx.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ transport.
/// </summary>
/// <remarks>
/// <para>
/// The reference adapter, and the proof that the port is adequate. Everything transport-independent
/// — envelope, outbox, inbox, pipeline, dedupe, tracing, retry <em>policy</em> — lives in the core;
/// this maps destinations onto exchanges and queues, moves bytes, and realises the delay the core
/// asks for.
/// </para>
/// <para>
/// It advertises <see cref="TransportCapabilities.NativeDelayedDelivery"/> because it implements
/// <see cref="ITransportScheduler"/> with a TTL holding-queue ladder. The flag means "this
/// transport handles delay", not "the broker has a plugin" — which matters because Amazon MQ
/// forbids the delayed-message plugin outright.
/// </para>
/// </remarks>
public sealed partial class RabbitMqTransport : IMessageTransport, ITransportTopologyProvisioner,
    ITransportScheduler, IAsyncDisposable
{
    private readonly RabbitMqConnectionProvider _connections;
    private readonly RabbitMqTopologyMapper _mapper;
    private readonly RabbitMqOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RabbitMqTransport> _logger;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly ConcurrentBag<Subscription> _subscriptions = [];
    private IChannel? _publishChannel;
    private volatile bool _disposed;

    /// <summary>Creates the transport.</summary>
    internal RabbitMqTransport(
        RabbitMqConnectionProvider connections,
        IOptions<RabbitMqOptions> options,
        TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        _connections = connections;
        _options = options.Value;
        _mapper = new RabbitMqTopologyMapper(_options);
        _clock = clock;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RabbitMqTransport>();
    }

    /// <inheritdoc />
    public string Name => "rabbitmq";

    /// <inheritdoc />
    /// <remarks>
    /// Advertised honestly. <see cref="TransportCapabilities.Transactions"/> is absent because AMQP
    /// transactions are slow and do not span the database anyway, so the outbox is the right answer
    /// instead. <see cref="TransportCapabilities.Priority"/> is absent because it would force
    /// classic queues, giving up quorum replication for a rarely-needed feature.
    /// </remarks>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.PublisherConfirms |
        TransportCapabilities.ManualAcknowledgement |
        TransportCapabilities.NativeDeadLetter |
        TransportCapabilities.NativeDelayedDelivery |
        TransportCapabilities.OrderedDelivery |
        TransportCapabilities.TopologyProvisioning |
        TransportCapabilities.ConsumerCancellation |
        TransportCapabilities.BrokerSideFiltering |
        TransportCapabilities.MessageTtl;

    /// <inheritdoc />
    public async Task<PublishReceipt> PublishAsync(
        TransportMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var exchange = _mapper.ExchangeFor(message.Destination);
        var routingKey = RabbitMqTopologyMapper.RoutingKeyFor(message.Destination);

        return await PublishCoreAsync(
            exchange, routingKey, message, mandatory: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Realises the core's requested delay with a TTL holding queue. The message enters the rung
    /// carrying the <em>target queue name</em> as its routing key, so on expiry the default
    /// exchange routes it straight back — no consumer on the rung, no in-process sleep, and nothing
    /// holding a prefetch slot while it waits.
    /// </remarks>
    public async Task ScheduleAsync(
        TransportMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var delay = dueAt - _clock.GetUtcNow();

        if (delay <= TimeSpan.Zero)
        {
            await PublishAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        var rung = _mapper.SelectRung(delay);

        // Two callers, two shapes. A redelivery names the single consumer queue it came from, and
        // must return only there. A delayed publish names nothing, has never been delivered, and
        // must fan out to every subscriber when it matures — so it waits in a per-destination
        // holding queue that expires to the destination's exchange instead.
        if (message.Headers.TryGetValue(TargetQueueHeader, out var targetQueue) &&
            !string.IsNullOrEmpty(targetQueue))
        {
            await PublishCoreAsync(
                _mapper.RetryExchangeFor(rung),

                // The routing key the message will carry out of the rung on expiry.
                targetQueue,
                message,

                // Not mandatory: the fanout rung has exactly one binding, and an unroutable return
                // here would be a topology defect the provisioner already asserts against.
                mandatory: false,
                cancellationToken).ConfigureAwait(false);

            LogScheduled(_logger, targetQueue, rung.TotalSeconds);
            return;
        }

        var delayQueue = _mapper.DelayQueueFor(message.Destination, rung);

        await PublishCoreAsync(
            // The default exchange, which routes by queue name. The queue's own dead-letter
            // settings put the message on the destination exchange when the TTL expires.
            exchange: string.Empty,
            delayQueue,
            message,

            // Mandatory: if the holding queue does not exist the message is returned rather than
            // dropped, and a scheduled publish that silently vanished would be near-impossible to
            // diagnose — the send succeeds and the message simply never arrives.
            mandatory: true,
            cancellationToken).ConfigureAwait(false);

        LogScheduled(_logger, delayQueue, rung.TotalSeconds);
    }

    /// <inheritdoc />
    public async Task<ITransportSubscription> SubscribeAsync(
        SubscriptionSpec specification,
        TransportDeliveryHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // One channel per consumer, never shared: IChannel is not thread-safe, and sharing one
        // produces frame corruption that surfaces as unrelated protocol errors much later.
        var channel = await _connections
            .CreateChannelAsync(ConnectionRole.Consumer, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var queue = _mapper.QueueFor(specification);

        // Prefetch bounds how many unacknowledged messages this consumer holds. Unbounded prefetch
        // defeats fair dispatch and means a restart redelivers the whole held set at once.
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: (ushort)Math.Clamp(specification.PrefetchCount, 1, ushort.MaxValue),
            global: false,
            cancellationToken).ConfigureAwait(false);

        var subscription = new Subscription(
            this, channel, specification, queue, handler,
            _loggerFactory.CreateLogger<Subscription>());

        await subscription.StartAsync(cancellationToken).ConfigureAwait(false);
        _subscriptions.Add(subscription);

        return subscription;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Asserts by passive declare in production and creates only in Development. Application-side
    /// declaration is how estates acquire drifted, undocumented objects nobody dares delete — and
    /// how one typo silently creates a second queue that quietly receives nothing.
    /// </remarks>
    public async Task AssertAsync(
        TopologyManifest manifest, TopologyMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        await using var channel = await _connections
            .CreateChannelAsync(ConnectionRole.Publisher, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (mode == TopologyMode.Provision)
        {
            await ProvisionAsync(channel, manifest, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var destination in manifest.Destinations)
        {
            await AssertExchangeAsync(channel, _mapper.ExchangeFor(destination), cancellationToken)
                .ConfigureAwait(false);

            foreach (var rung in _options.RetryLadder)
            {
                await AssertQueueAsync(
                    channel, _mapper.DelayQueueFor(destination, rung), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (var subscription in manifest.Subscriptions)
        {
            await AssertQueueAsync(channel, _mapper.QueueFor(subscription), cancellationToken)
                .ConfigureAwait(false);
            await AssertQueueAsync(channel, _mapper.DeadLetterQueueFor(subscription), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var rung in _options.RetryLadder)
        {
            await AssertQueueAsync(channel, _mapper.RetryQueueFor(rung), cancellationToken)
                .ConfigureAwait(false);
        }

        LogTopologyAsserted(_logger, manifest.Destinations.Count, manifest.Subscriptions.Count);
    }

    /// <summary>Header naming the queue a scheduled message returns to.</summary>
    internal const string TargetQueueHeader = "microfx-target-queue";

    internal RabbitMqTopologyMapper Mapper => _mapper;

    private async Task<PublishReceipt> PublishCoreAsync(
        string exchange,
        string routingKey,
        TransportMessage message,
        bool mandatory,
        CancellationToken cancellationToken)
    {
        var channel = await GetPublishChannelAsync(cancellationToken).ConfigureAwait(false);

        var properties = new BasicProperties
        {
            // Persistent, so a broker restart does not discard business messages.
            DeliveryMode = message.Persistent ? DeliveryModes.Persistent : DeliveryModes.Transient,
            ContentType = "application/json",
            Headers = message.Headers.ToDictionary(
                pair => pair.Key, pair => (object?)Encoding.UTF8.GetBytes(pair.Value),
                StringComparer.Ordinal),
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.PublishConfirmTimeout);

            // Awaits the broker's confirmation. Returning before it would make the outbox's
            // "dispatched" mark a lie.
            await channel.BasicPublishAsync(
                exchange, routingKey, mandatory, properties, message.Body, timeout.Token)
                .ConfigureAwait(false);

            return new PublishReceipt(Confirmed: true);
        }
        catch (PublishException ex)
        {
            // Nacked or returned unroutable. Reported rather than thrown away, so the outbox keeps
            // the row and retries.
            LogPublishRejected(_logger, exchange, routingKey, ex.IsReturn);
            return new PublishReceipt(Confirmed: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogPublishTimedOut(_logger, exchange, routingKey);
            return new PublishReceipt(Confirmed: false);
        }
    }

    private async Task<IChannel> GetPublishChannelAsync(CancellationToken cancellationToken)
    {
        var existing = _publishChannel;
        if (existing is { IsOpen: true })
        {
            return existing;
        }

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_publishChannel is { IsOpen: true } current)
            {
                return current;
            }

            _publishChannel = await _connections
                .CreateChannelAsync(ConnectionRole.Publisher, publisherConfirms: true, cancellationToken)
                .ConfigureAwait(false);

            return _publishChannel;
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async Task ProvisionAsync(
        IChannel channel, TopologyManifest manifest, CancellationToken cancellationToken)
    {
        // Unroutable capture first: every exchange points its alternate here, so a message that
        // matches no binding is preserved for triage rather than silently dropped.
        await channel.ExchangeDeclareAsync(
            _mapper.AlternateExchange(), ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            _mapper.UnroutableQueue(), durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            _mapper.UnroutableQueue(), _mapper.AlternateExchange(), routingKey: string.Empty,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var exchangeArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["alternate-exchange"] = _mapper.AlternateExchange(),
        };

        foreach (var destination in manifest.Destinations)
        {
            await channel.ExchangeDeclareAsync(
                _mapper.ExchangeFor(destination),
                RabbitMqTopologyMapper.ExchangeTypeFor(destination.Kind),
                durable: true, autoDelete: false, exchangeArguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // One holding queue per rung, so a delayed publish has somewhere to wait that expires
            // back onto this destination's exchange.
            foreach (var rung in _options.RetryLadder)
            {
                await channel.QueueDeclareAsync(
                    _mapper.DelayQueueFor(destination, rung),
                    durable: true, exclusive: false, autoDelete: false,
                    _mapper.DelayQueueArguments(destination, rung),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var subscription in manifest.Subscriptions)
        {
            await ProvisionSubscriptionAsync(channel, subscription, exchangeArguments, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var rung in _options.RetryLadder)
        {
            await channel.ExchangeDeclareAsync(
                _mapper.RetryExchangeFor(rung), ExchangeType.Fanout, durable: true, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await channel.QueueDeclareAsync(
                _mapper.RetryQueueFor(rung), durable: true, exclusive: false, autoDelete: false,
                _mapper.RetryQueueArguments(rung), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await channel.QueueBindAsync(
                _mapper.RetryQueueFor(rung), _mapper.RetryExchangeFor(rung), routingKey: string.Empty,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        LogTopologyProvisioned(_logger, manifest.Destinations.Count, manifest.Subscriptions.Count);
    }

    private async Task ProvisionSubscriptionAsync(
        IChannel channel,
        SubscriptionSpec subscription,
        Dictionary<string, object?> exchangeArguments,
        CancellationToken cancellationToken)
    {
        var deadLetterExchange = _mapper.DeadLetterExchangeFor(subscription);
        var deadLetterQueue = _mapper.DeadLetterQueueFor(subscription);

        await channel.ExchangeDeclareAsync(
            deadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            deadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            _mapper.DeadLetterQueueArguments(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.QueueBindAsync(
            deadLetterQueue, deadLetterExchange, routingKey: string.Empty,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var queue = _mapper.QueueFor(subscription);

        await channel.QueueDeclareAsync(
            queue, durable: true, exclusive: false, autoDelete: false,
            _mapper.WorkQueueArguments(subscription), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var sourceExchange = _mapper.ExchangeFor(subscription.Source);

        await channel.ExchangeDeclareAsync(
            sourceExchange, RabbitMqTopologyMapper.ExchangeTypeFor(subscription.Source.Kind),
            durable: true, autoDelete: false, exchangeArguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // A declared filter becomes the binding pattern, so non-matching messages never reach the
        // queue at all rather than being delivered and discarded.
        var bindingKey = subscription.Filter ?? RabbitMqTopologyMapper.RoutingKeyFor(subscription.Source);

        await channel.QueueBindAsync(
            queue, sourceExchange, bindingKey, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AssertExchangeAsync(
        IChannel channel, string exchange, CancellationToken cancellationToken)
    {
        try
        {
            await channel.ExchangeDeclarePassiveAsync(exchange, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            throw new TopologyMismatchException(
                $"Exchange '{exchange}' is missing or differs from what this service expects. " +
                "Topology is provisioned by infrastructure-as-code, not by the application; run the " +
                "topology migration before deploying.", ex);
        }
    }

    private static async Task AssertQueueAsync(
        IChannel channel, string queue, CancellationToken cancellationToken)
    {
        try
        {
            await channel.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            throw new TopologyMismatchException(
                $"Queue '{queue}' is missing or differs from what this service expects. " +
                "Topology is provisioned by infrastructure-as-code, not by the application; run the " +
                "topology migration before deploying.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        if (_publishChannel is { } channel)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _publishGate.Dispose();
        await _connections.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 8010, Level = LogLevel.Warning,
        Message = "RabbitMQ rejected a publish to {Exchange}/{RoutingKey} (returned: {WasReturned}).")]
    private static partial void LogPublishRejected(
        ILogger logger, string exchange, string routingKey, bool wasReturned);

    [LoggerMessage(EventId = 8011, Level = LogLevel.Warning,
        Message = "Publish to {Exchange}/{RoutingKey} was not confirmed within the timeout.")]
    private static partial void LogPublishTimedOut(ILogger logger, string exchange, string routingKey);

    [LoggerMessage(EventId = 8012, Level = LogLevel.Information,
        Message = "Asserted RabbitMQ topology: {Destinations} destination(s), {Subscriptions} subscription(s).")]
    private static partial void LogTopologyAsserted(ILogger logger, int destinations, int subscriptions);

    [LoggerMessage(EventId = 8013, Level = LogLevel.Warning,
        Message = "Provisioned RabbitMQ topology ({Destinations} destinations, {Subscriptions} " +
                  "subscriptions). Development only; production asserts instead.")]
    private static partial void LogTopologyProvisioned(ILogger logger, int destinations, int subscriptions);

    [LoggerMessage(EventId = 8014, Level = LogLevel.Debug,
        Message = "Scheduled a redelivery to {TargetQueue} on the {RungSeconds}s rung.")]
    private static partial void LogScheduled(ILogger logger, string targetQueue, double rungSeconds);

    /// <summary>One consuming subscription, owning exactly one channel.</summary>
    private sealed partial class Subscription(
        RabbitMqTransport transport,
        IChannel channel,
        SubscriptionSpec specification,
        string queue,
        TransportDeliveryHandler handler,
        ILogger<Subscription> logger) : ITransportSubscription
    {
        private readonly SemaphoreSlim _inFlight = new(1, 1);
        private string? _consumerTag;
        private int _active;
        private int _disposed;

        public string ConsumerGroup => specification.ConsumerGroup;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnReceivedAsync;

            _consumerTag = await channel.BasicConsumeAsync(
                queue,

                // Manual acknowledgement is what makes at-least-once achievable: an unacknowledged
                // delivery returns to the queue if this consumer dies mid-handling.
                autoAck: false,
                consumer,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <c>basic.cancel</c> stops the broker dispatching immediately while leaving in-flight
        /// deliveries to finish and acknowledge. That is what makes the drain lossless: anything
        /// still unacknowledged when the channel closes is redelivered to a surviving replica.
        /// </remarks>
        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            if (_consumerTag is not { } tag || Interlocked.Exchange(ref _active, 1) == 1)
            {
                return;
            }

            try
            {
                await channel.BasicCancelAsync(tag, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCancelFailed(logger, ConsumerGroup, ex);
            }
        }

        private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs args)
        {
            var headers = ReadHeaders(args.BasicProperties.Headers);

            try
            {
                var outcome = await handler(
                    new TransportDelivery(
                        headers,
                        args.Body.ToArray(),
                        ConsumerGroup,
                        DeliveryCount: args.Redelivered ? 2 : 1,
                        IsRedelivery: args.Redelivered),
                    CancellationToken.None).ConfigureAwait(false);

                await SettleAsync(args, outcome).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The delivery must always be settled. An unsettled one holds a prefetch slot for
                // the life of the channel and is invisible until the consumer stalls entirely.
                LogSettleFailed(logger, ConsumerGroup, ex);

                await channel.BasicRejectAsync(args.DeliveryTag, requeue: false)
                    .ConfigureAwait(false);
            }
        }

        private async Task SettleAsync(BasicDeliverEventArgs args, DeliveryOutcome outcome)
        {
            switch (outcome.Disposition)
            {
                case DeliveryDisposition.Complete:
                    await channel.BasicAckAsync(args.DeliveryTag, multiple: false).ConfigureAwait(false);
                    break;

                case DeliveryDisposition.DeadLetter:
                    // requeue: false routes it through the queue's dead-letter exchange with its
                    // x-death history intact, rather than looping it back onto the work queue.
                    await channel.BasicRejectAsync(args.DeliveryTag, requeue: false).ConfigureAwait(false);
                    break;

                case DeliveryDisposition.Abandon:
                    await channel.BasicRejectAsync(args.DeliveryTag, requeue: true).ConfigureAwait(false);
                    break;

                case DeliveryDisposition.RetryLater:
                    await ScheduleRetryAsync(args, outcome).ConfigureAwait(false);
                    break;

                default:
                    await channel.BasicRejectAsync(args.DeliveryTag, requeue: false).ConfigureAwait(false);
                    break;
            }
        }

        private async Task ScheduleRetryAsync(BasicDeliverEventArgs args, DeliveryOutcome outcome)
        {
            // The core's updated headers carry the incremented attempt count. Republishing the
            // originals would replay attempt 1 forever and the retry policy would never exhaust.
            var headers = outcome.Headers is null
                ? ReadHeaders(args.BasicProperties.Headers)
                : new Dictionary<string, string>(outcome.Headers, StringComparer.Ordinal);

            headers[TargetQueueHeader] = queue;

            var message = new TransportMessage(
                specification.Source, headers, args.Body.ToArray());

            await transport.ScheduleAsync(
                message,
                transport._clock.GetUtcNow() + (outcome.RetryDelay ?? TimeSpan.Zero))
                .ConfigureAwait(false);

            // Acknowledged only after the copy is safely on a rung: acknowledging first would lose
            // the message if the rung publish then failed.
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false).ConfigureAwait(false);
        }

        /// <summary>Decodes AMQP headers, discarding anything that is not a well-formed string.</summary>
        private static Dictionary<string, string> ReadHeaders(IDictionary<string, object?>? headers)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            if (headers is null)
            {
                return result;
            }

            foreach (var (key, value) in headers)
            {
                // Bounded and type-checked. Header values arrive from a shared broker, so a
                // hostile or corrupt frame must not become an unbounded allocation.
                var text = value switch
                {
                    byte[] bytes when bytes.Length <= Envelope.MaxHeaderLength => Encoding.UTF8.GetString(bytes),
                    string s when s.Length <= Envelope.MaxHeaderLength => s,
                    _ => null,
                };

                if (text is not null && key.Length <= Envelope.MaxHeaderLength)
                {
                    result[key] = text;
                }
            }

            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await CancelAsync().ConfigureAwait(false);

            try
            {
                await channel.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The broker may already be gone; a clean stop must not become a crash.
            }
            finally
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                _inFlight.Dispose();
            }
        }

        [LoggerMessage(EventId = 8020, Level = LogLevel.Warning,
            Message = "Failed to cancel the RabbitMQ consumer for {ConsumerGroup}.")]
        private static partial void LogCancelFailed(
            ILogger logger, string consumerGroup, Exception exception);

        [LoggerMessage(EventId = 8021, Level = LogLevel.Error,
            Message = "Failed to settle a delivery on {ConsumerGroup}; it was dead-lettered.")]
        private static partial void LogSettleFailed(
            ILogger logger, string consumerGroup, Exception exception);
    }
}

/// <summary>Thrown when the broker's topology does not match what the service declared.</summary>
public sealed class TopologyMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TopologyMismatchException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public TopologyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public TopologyMismatchException()
    {
    }
}
