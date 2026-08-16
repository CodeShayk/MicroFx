namespace MicroFx.Messaging.Transport;

/// <summary>A message as the transport sees it: headers plus an opaque body.</summary>
/// <param name="Destination">Where it is going.</param>
/// <param name="Headers">Envelope headers, already encoded.</param>
/// <param name="Body">The serialized payload.</param>
/// <param name="Persistent">Whether the transport must durably store it before acknowledging.</param>
public sealed record TransportMessage(
    MessageDestination Destination,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    bool Persistent = true);

/// <summary>Confirmation that a publish reached the broker.</summary>
/// <param name="Confirmed">
/// Whether the broker acknowledged. A transport without publisher confirms reports false, and the
/// outbox must not mark a row dispatched on that basis.
/// </param>
/// <param name="TransportMessageId">Broker-assigned id, when one exists.</param>
public readonly record struct PublishReceipt(bool Confirmed, string? TransportMessageId = null);

/// <summary>One delivery handed to the consumer.</summary>
/// <param name="Headers">Envelope headers as received.</param>
/// <param name="Body">The serialized payload.</param>
/// <param name="ConsumerGroup">The subscription receiving it.</param>
/// <param name="DeliveryCount">
/// Transport-reported delivery count, where known. Advisory only — the authoritative attempt count
/// lives in the envelope, because not every transport tracks this and some reset it.
/// </param>
/// <param name="IsRedelivery">Whether the transport reports this as a redelivery.</param>
public sealed record TransportDelivery(
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    string ConsumerGroup,
    int DeliveryCount = 1,
    bool IsRedelivery = false);

/// <summary>What the core asks the transport to do with a delivery once the pipeline has run.</summary>
public enum DeliveryDisposition
{
    /// <summary>Handled. Acknowledge it.</summary>
    Complete,

    /// <summary>Deliver again later. The core supplies the delay.</summary>
    RetryLater,

    /// <summary>Terminal failure. Send to the dead-letter destination.</summary>
    DeadLetter,

    /// <summary>Return for immediate redelivery, typically because this consumer is shutting down.</summary>
    Abandon,
}

/// <summary>Handles one delivery and reports what should happen to it.</summary>
public delegate Task<DeliveryOutcome> TransportDeliveryHandler(
    TransportDelivery delivery, CancellationToken cancellationToken);

/// <summary>The disposition plus anything the transport needs to honour it.</summary>
/// <param name="Disposition">What to do with the delivery.</param>
/// <param name="RetryDelay">Delay before redelivery, for <see cref="DeliveryDisposition.RetryLater"/>.</param>
/// <param name="Reason">Short, stable reason token for diagnostics. Never payload content.</param>
/// <param name="Headers">
/// Headers a redelivery or dead-letter must carry, replacing the originals. This is how the
/// incremented attempt count reaches the next delivery: the core owns the counter, so a transport
/// that redelivered the original headers would replay attempt 1 forever and never exhaust the
/// retry policy.
/// </param>
public readonly record struct DeliveryOutcome(
    DeliveryDisposition Disposition,
    TimeSpan? RetryDelay = null,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>An active subscription.</summary>
public interface ITransportSubscription : IAsyncDisposable
{
    /// <summary>The consumer group this subscription serves.</summary>
    string ConsumerGroup { get; }

    /// <summary>
    /// Stops delivery without dropping in-flight work, so a drain can finish what it holds before
    /// closing. Unacknowledged messages return to the transport for redelivery elsewhere.
    /// </summary>
    Task CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The transport port. Everything above it is transport-neutral and written once.
/// </summary>
/// <remarks>
/// Kept deliberately small. Envelope, outbox, inbox, pipeline, dedupe, tracing, tenancy,
/// authorization, claim-check, and retry policy all live in the core; an adapter maps destinations
/// and moves bytes. That ratio is what makes a new broker an adapter rather than a rewrite.
/// </remarks>
public interface IMessageTransport
{
    /// <summary>Short name for diagnostics: <c>in-memory</c>, <c>rabbitmq</c>.</summary>
    string Name { get; }

    /// <summary>What this transport can actually do. Drives negotiation at startup.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>Publishes a message.</summary>
    Task<PublishReceipt> PublishAsync(
        TransportMessage message, CancellationToken cancellationToken = default);

    /// <summary>Begins consuming a subscription.</summary>
    Task<ITransportSubscription> SubscribeAsync(
        SubscriptionSpec specification,
        TransportDeliveryHandler handler,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional facet: verifies or creates topology.</summary>
public interface ITransportTopologyProvisioner
{
    /// <summary>Asserts, or in Development creates, the declared topology.</summary>
    Task AssertAsync(
        TopologyManifest manifest, TopologyMode mode, CancellationToken cancellationToken = default);
}

/// <summary>Optional facet: correlated request/reply.</summary>
public interface ITransportRequestReply
{
    /// <summary>Sends a request and awaits its correlated reply.</summary>
    Task<TransportDelivery> RequestAsync(
        TransportMessage request, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional facet: native delayed delivery.
/// </summary>
/// <remarks>
/// Absent on several brokers. When it is missing the core falls back to its scheduled-message
/// store — never to an in-process sleep, which would hold the delivery and stall the consumer.
/// </remarks>
public interface ITransportScheduler
{
    /// <summary>Schedules a message for delivery at or after the given instant.</summary>
    Task ScheduleAsync(
        TransportMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken = default);
}

/// <summary>Broker-side statistics for a destination.</summary>
/// <param name="Destination">Which destination.</param>
/// <param name="ConsumerGroup">Which consumer group, when applicable.</param>
/// <param name="ReadyCount">Messages awaiting delivery.</param>
/// <param name="UnacknowledgedCount">Messages delivered but not yet acknowledged.</param>
/// <param name="OldestMessageAge">Age of the oldest ready message — the best backlog signal.</param>
public readonly record struct DestinationStatistics(
    MessageDestination Destination,
    string? ConsumerGroup,
    long ReadyCount,
    long UnacknowledgedCount,
    TimeSpan? OldestMessageAge);

/// <summary>Optional facet: broker-side depth and age, for autoscaling and alerting.</summary>
public interface ITransportMetricsSource
{
    /// <summary>Returns current statistics.</summary>
    Task<IReadOnlyList<DestinationStatistics>> GetStatisticsAsync(
        CancellationToken cancellationToken = default);
}
