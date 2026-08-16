using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MicroFx.Messaging.Transport.InMemory;

/// <summary>Options for the in-memory transport.</summary>
public sealed class InMemoryTransportOptions
{
    /// <summary>
    /// Capabilities the transport advertises.
    /// </summary>
    /// <remarks>
    /// Adjustable so a test can simulate a broker that lacks confirms, acknowledgement, ordering, or
    /// dead-lettering, and assert what the core does about it. Without this the negotiation logic
    /// would be untestable until a second real transport existed — far too late to discover the port
    /// is the wrong shape.
    /// </remarks>
    public TransportCapabilities Capabilities { get; set; } =
        TransportCapabilities.PublisherConfirms |
        TransportCapabilities.ManualAcknowledgement |
        TransportCapabilities.NativeDeadLetter |
        TransportCapabilities.NativeDelayedDelivery |
        TransportCapabilities.OrderedDelivery |
        TransportCapabilities.TopologyProvisioning |
        TransportCapabilities.ConsumerCancellation |
        TransportCapabilities.BrokerSideFiltering |
        TransportCapabilities.MessageTtl;

    /// <summary>
    /// Messages buffered per consumer group before a publish blocks.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An unbounded channel converts a slow consumer into unbounded memory
    /// growth and then an OOM kill, which is a far worse failure than back-pressure.
    /// </remarks>
    public int Capacity { get; set; } = 1024;
}

/// <summary>
/// An in-process transport over bounded channels.
/// </summary>
/// <remarks>
/// <para>
/// Not a toy. It implements real acknowledgement, redelivery, per-consumer-group fan-out, ordering
/// by partition key, delayed delivery, and dead-lettering — which is what lets the entire messaging
/// suite run in milliseconds with no container, and what keeps the transport port honest.
/// </para>
/// <para>
/// It is <b>not</b> a production transport: messages exist only inside one process, so a restart
/// loses everything in flight. The messaging feature refuses to start on it outside Development
/// unless explicitly forced.
/// </para>
/// </remarks>
public sealed class InMemoryTransport
    : IMessageTransport, ITransportTopologyProvisioner, ITransportScheduler, IAsyncDisposable
{
    private readonly InMemoryTransportOptions _options;
    private readonly TimeProvider _clock;

    // destination -> consumer group -> channel. Each group has its own channel, which is what makes
    // fan-out independent: a wedged subscriber fills its own buffer and nobody else's.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Channel<Delivery>>> _groups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _knownDestinations = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<DeadLetteredMessage> _deadLetters = [];
    private readonly List<Subscription> _subscriptions = [];
    private readonly Lock _subscriptionLock = new();
    private volatile bool _disposed;

    /// <summary>Creates the transport.</summary>
    public InMemoryTransport(InMemoryTransportOptions? options = null, TimeProvider? clock = null)
    {
        _options = options ?? new InMemoryTransportOptions();
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "in-memory";

    /// <inheritdoc />
    public TransportCapabilities Capabilities => _options.Capabilities;

    /// <summary>Messages that reached the dead-letter destination. For assertions in tests.</summary>
    public IReadOnlyCollection<DeadLetteredMessage> DeadLetters => [.. _deadLetters];

    /// <inheritdoc />
    public Task<PublishReceipt> PublishAsync(
        TransportMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = message.Destination.ToString();
        _knownDestinations.TryAdd(key, 0);

        var groups = _groups.GetOrAdd(key, _ => new ConcurrentDictionary<string, Channel<Delivery>>(StringComparer.Ordinal));

        foreach (var channel in groups.Values)
        {
            // TryWrite rather than WriteAsync: a full buffer means a consumer is not keeping up, and
            // blocking the publisher inside a request would convert consumer lag into request latency.
            if (!channel.Writer.TryWrite(new Delivery(message, 1, false)))
            {
                return Task.FromResult(new PublishReceipt(false));
            }
        }

        var confirmed = Capabilities.HasFlag(TransportCapabilities.PublisherConfirms);
        return Task.FromResult(new PublishReceipt(confirmed, Guid.NewGuid().ToString("N")));
    }

    /// <inheritdoc />
    public Task<ITransportSubscription> SubscribeAsync(
        SubscriptionSpec specification,
        TransportDeliveryHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = specification.Source.ToString();
        _knownDestinations.TryAdd(key, 0);

        var groups = _groups.GetOrAdd(
            key, _ => new ConcurrentDictionary<string, Channel<Delivery>>(StringComparer.Ordinal));

        var channel = groups.GetOrAdd(
            specification.ConsumerGroup,
            _ => Channel.CreateBounded<Delivery>(new BoundedChannelOptions(_options.Capacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }));

        var subscription = new Subscription(this, specification, handler, channel, _clock);

        lock (_subscriptionLock)
        {
            _subscriptions.Add(subscription);
        }

        subscription.Start();
        return Task.FromResult<ITransportSubscription>(subscription);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implemented so the advertised <see cref="TransportCapabilities.NativeDelayedDelivery"/> is a
    /// claim the transport can actually honour. The delay runs on a timer rather than on a
    /// consuming worker, so a backed-off message never occupies the slot it needs to return to.
    /// </remarks>
    public Task ScheduleAsync(
        TransportMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var delay = dueAt - _clock.GetUtcNow();

        if (delay <= TimeSpan.Zero)
        {
            return PublishAsync(message, cancellationToken);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _clock, CancellationToken.None).ConfigureAwait(false);

                if (!_disposed)
                {
                    await PublishAsync(message, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // The transport is shutting down; an in-process scheduled message simply does not
                // survive, which is one reason this transport is refused in production.
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AssertAsync(
        TopologyManifest manifest, TopologyMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        // In-process topology is created on demand, so Assert always succeeds. A real adapter
        // passively declares and fails on drift.
        foreach (var destination in manifest.Destinations)
        {
            _knownDestinations.TryAdd(destination.ToString(), 0);
        }

        return Task.CompletedTask;
    }

    internal void DeadLetter(TransportMessage message, string? reason) =>
        _deadLetters.Add(new DeadLetteredMessage(message, reason, _clock.GetUtcNow()));

    internal bool TryRedeliver(SubscriptionSpec specification, Delivery delivery)
    {
        var groups = _groups.GetValueOrDefault(specification.Source.ToString());
        var channel = groups?.GetValueOrDefault(specification.ConsumerGroup);

        return channel is not null && channel.Writer.TryWrite(delivery);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Subscription[] subscriptions;
        lock (_subscriptionLock)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed record Delivery(TransportMessage Message, int DeliveryCount, bool IsRedelivery);

    private sealed class Subscription : ITransportSubscription
    {
        private readonly InMemoryTransport _transport;
        private readonly SubscriptionSpec _specification;
        private readonly TransportDeliveryHandler _handler;
        private readonly Channel<Delivery> _channel;
        private readonly TimeProvider _clock;
        private readonly CancellationTokenSource _stopping = new();
        private readonly SemaphoreSlim _concurrency;
        private readonly List<Task> _workers = [];
        private volatile bool _cancelled;
        private int _disposed;

        public Subscription(
            InMemoryTransport transport,
            SubscriptionSpec specification,
            TransportDeliveryHandler handler,
            Channel<Delivery> channel,
            TimeProvider clock)
        {
            _transport = transport;
            _specification = specification;
            _handler = handler;
            _channel = channel;
            _clock = clock;

            // Per-key ordering is achieved by collapsing to a single worker. Honest rather than
            // clever: the throughput cost of ordering is exactly what the design says it is.
            var workers = specification.Ordering == OrderingScope.PerKey
                ? 1
                : Math.Max(1, specification.Concurrency);

            _concurrency = new SemaphoreSlim(workers, workers);
            WorkerCount = workers;
        }

        public string ConsumerGroup => _specification.ConsumerGroup;

        public int WorkerCount { get; }

        public void Start()
        {
            for (var worker = 0; worker < WorkerCount; worker++)
            {
                _workers.Add(Task.Run(ConsumeAsync));
            }
        }

        public Task CancelAsync(CancellationToken cancellationToken = default)
        {
            // Stops new deliveries without cancelling in-flight handlers, which is what makes a
            // lossless drain possible.
            _cancelled = true;
            return Task.CompletedTask;
        }

        private async Task ConsumeAsync()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
                {
                    while (!_cancelled && _channel.Reader.TryRead(out var delivery))
                    {
                        await ProcessAsync(delivery).ConfigureAwait(false);
                    }

                    if (_cancelled)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        }

        private async Task ProcessAsync(Delivery delivery)
        {
            await _concurrency.WaitAsync(_stopping.Token).ConfigureAwait(false);

            try
            {
                var outcome = await _handler(
                    new TransportDelivery(
                        delivery.Message.Headers,
                        delivery.Message.Body,
                        _specification.ConsumerGroup,
                        delivery.DeliveryCount,
                        delivery.IsRedelivery),
                    _stopping.Token).ConfigureAwait(false);

                await DisposeOfAsync(delivery, outcome).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Unacknowledged work returns for redelivery, matching a real broker's behaviour.
                _transport.TryRedeliver(_specification, delivery with { IsRedelivery = true });
            }
            finally
            {
                _concurrency.Release();
            }
        }

        private async Task DisposeOfAsync(Delivery delivery, DeliveryOutcome outcome)
        {
            switch (outcome.Disposition)
            {
                case DeliveryDisposition.Complete:
                    break;

                case DeliveryDisposition.DeadLetter:
                    _transport.DeadLetter(Apply(delivery.Message, outcome.Headers), outcome.Reason);
                    break;

                case DeliveryDisposition.Abandon:
                    _transport.TryRedeliver(
                        _specification,
                        delivery with
                        {
                            Message = Apply(delivery.Message, outcome.Headers),
                            DeliveryCount = delivery.DeliveryCount + 1,
                            IsRedelivery = true,
                        });
                    break;

                case DeliveryDisposition.RetryLater:
                    await ScheduleRedeliveryAsync(
                        delivery with { Message = Apply(delivery.Message, outcome.Headers) },
                        outcome.RetryDelay ?? TimeSpan.Zero).ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }

        /// <summary>Replaces the message headers when the core supplied updated ones.</summary>
        private static TransportMessage Apply(
            TransportMessage message, IReadOnlyDictionary<string, string>? headers) =>
            headers is null ? message : message with { Headers = headers };

        private async Task ScheduleRedeliveryAsync(Delivery delivery, TimeSpan delay)
        {
            var next = delivery with
            {
                DeliveryCount = delivery.DeliveryCount + 1,
                IsRedelivery = true,
            };

            if (delay <= TimeSpan.Zero)
            {
                _transport.TryRedeliver(_specification, next);
                return;
            }

            // The delay runs on a timer, not on the consuming worker, so a backed-off message never
            // occupies the concurrency slot it would need to be retried into.
            var token = _stopping.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, _clock, token).ConfigureAwait(false);
                    _transport.TryRedeliver(_specification, next);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down; the message is simply not redelivered in-process.
                }
            }, CancellationToken.None);

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            // Idempotent by contract: the consumer host disposes a subscription during drain, and
            // the transport disposes whatever is left. Double dispose is normal and must be safe.
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _cancelled = true;
            await _stopping.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }

            _stopping.Dispose();
            _concurrency.Dispose();
        }
    }
}

/// <summary>A message that reached the dead-letter destination.</summary>
/// <param name="Message">The message as published.</param>
/// <param name="Reason">Short reason token.</param>
/// <param name="DeadLetteredAt">When it was dead-lettered.</param>
public sealed record DeadLetteredMessage(
    TransportMessage Message, string? Reason, DateTimeOffset DeadLetteredAt);
