namespace MicroFx.Messaging;

/// <summary>The role a destination plays.</summary>
public enum DestinationKind
{
    /// <summary>Point-to-point: exactly one logical consumer.</summary>
    Command,

    /// <summary>Publish/subscribe: zero or more independent consumer groups.</summary>
    Event,

    /// <summary>Requests awaiting a correlated reply.</summary>
    Request,

    /// <summary>Replies to requests.</summary>
    Reply,

    /// <summary>Terminal destination for messages that cannot be handled.</summary>
    DeadLetter,

    /// <summary>Durable capture of every event, so replay is possible.</summary>
    Archive,
}

/// <summary>
/// A transport-neutral destination.
/// </summary>
/// <remarks>
/// Deliberately vocabulary-free: no exchange, no topic, no queue, no ARN. Each adapter maps this to
/// its own objects, which is what lets handler and publisher code survive a change of broker.
/// </remarks>
/// <param name="Kind">The role this destination plays.</param>
/// <param name="Owner">The service that owns the contract.</param>
/// <param name="Name">Logical name, e.g. <c>reserve-inventory</c> or <c>order.placed</c>.</param>
/// <param name="Version">Contract version, e.g. <c>v1</c>.</param>
public readonly record struct MessageDestination(
    DestinationKind Kind, string Owner, string Name, string Version = "v1")
{
    /// <summary>A stable, adapter-independent identity for this destination.</summary>
    public override string ToString() =>
        $"{Kind.ToString().ToLowerInvariant()}:{Owner}:{Name}:{Version}";
}

/// <summary>Delivery semantics a subscription requires.</summary>
public enum DeliveryGuarantee
{
    /// <summary>
    /// Every message is delivered at least once; duplicates are possible and deduplicated by the
    /// inbox. Requires transport acknowledgement support.
    /// </summary>
    AtLeastOnce,

    /// <summary>
    /// A message may be lost if the consumer fails mid-handling. Only for telemetry-grade streams
    /// where loss is genuinely acceptable, and never the default.
    /// </summary>
    AtMostOnce,
}

/// <summary>Ordering a subscription requires.</summary>
public enum OrderingScope
{
    /// <summary>No ordering guarantee. Maximum throughput and independent retry.</summary>
    None,

    /// <summary>
    /// Messages sharing a partition key are delivered in order. Caps throughput for that key and
    /// means a retry blocks its partition — you get ordering or independent retry, not both.
    /// </summary>
    PerKey,
}

/// <summary>Retry policy for a subscription.</summary>
/// <remarks>
/// The <em>policy</em> is the platform's: attempt count, backoff curve, jitter. The
/// <em>mechanism</em> that realises the delay belongs to the transport or its emulation.
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>The platform default: four attempts over roughly two and a half minutes.</summary>
    public static readonly RetryPolicy Default = new();

    /// <summary>A policy that never retries. Suits a handler that is inherently non-idempotent.</summary>
    public static readonly RetryPolicy None = new() { MaxAttempts = 1 };

    /// <summary>Total attempts including the first. 1 means no retry.</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>Delay before the second attempt. Subsequent delays grow exponentially.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on any single delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Jitter as a fraction of the computed delay, spreading a correlated failure burst.</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>
    /// Computes the delay before the given attempt.
    /// </summary>
    /// <param name="attempt">The attempt about to be made; 2 is the first retry.</param>
    /// <param name="requested">A handler-requested delay, clamped to <see cref="MaxDelay"/>.</param>
    public TimeSpan DelayFor(int attempt, TimeSpan? requested = null)
    {
        if (requested is { } explicitDelay)
        {
            // Clamped rather than honoured outright: a handler asking for a six-hour delay would
            // otherwise park a delivery far beyond any sensible operational window.
            return Clamp(explicitDelay);
        }

        // Exponent is bounded before shifting: an unbounded attempt count would overflow the
        // multiplier long before it reached MaxDelay.
        var exponent = Math.Min(Math.Max(attempt - 2, 0), 16);
        var scaled = BaseDelay * Math.Pow(2, exponent);

        return Clamp(ApplyJitter(scaled));
    }

    private TimeSpan Clamp(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxDelay ? MaxDelay : value;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Jitter spreads a retry burst. A predictable offset is no worse than none, " +
                        "and no security decision depends on this value.")]
    private TimeSpan ApplyJitter(TimeSpan value)
    {
        if (Jitter <= 0)
        {
            return value;
        }

        var offset = value.TotalMilliseconds * Jitter * (Random.Shared.NextDouble() - 0.5) * 2;
        return TimeSpan.FromMilliseconds(Math.Max(0, value.TotalMilliseconds + offset));
    }
}

/// <summary>Dead-letter policy for a subscription.</summary>
public sealed record DeadLetterPolicy
{
    /// <summary>The platform default: dead-letter on exhaustion, retaining the envelope.</summary>
    public static readonly DeadLetterPolicy Default = new();

    /// <summary>
    /// Whether exhausted or permanently-failed messages are dead-lettered. Disabling this drops
    /// them, so it requires a deliberate decision.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether the original payload is retained alongside the failure reason. Off for a subscription
    /// carrying regulated data, where a long-lived dead-letter store is the wrong place for it.
    /// </summary>
    public bool RetainPayload { get; init; } = true;
}

/// <summary>
/// A subscription, expressed without naming any transport object.
/// </summary>
/// <remarks>
/// <see cref="ConsumerGroup"/> is the load-bearing abstraction. It expresses "each subscriber gets
/// its own backlog, retry, and dead-letter" without saying "queue" — which is what lets the same
/// declaration map onto a RabbitMQ queue, a Kafka consumer group, or an SQS queue on an SNS topic.
/// </remarks>
public sealed record SubscriptionSpec
{
    /// <summary>Logical subscriber identity. Two services must never share one.</summary>
    public required string ConsumerGroup { get; init; }

    /// <summary>The destination subscribed to.</summary>
    public required MessageDestination Source { get; init; }

    /// <summary>Transport-neutral filter pattern, pushed broker-side where supported.</summary>
    public string? Filter { get; init; }

    /// <summary>Delivery semantics required.</summary>
    public DeliveryGuarantee Guarantee { get; init; } = DeliveryGuarantee.AtLeastOnce;

    /// <summary>Concurrent handler invocations for this subscription.</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>Unacknowledged messages the transport may hold for this subscription.</summary>
    public int PrefetchCount { get; init; } = 10;

    /// <summary>Retry policy.</summary>
    public RetryPolicy Retry { get; init; } = RetryPolicy.Default;

    /// <summary>Dead-letter policy.</summary>
    public DeadLetterPolicy DeadLetter { get; init; } = DeadLetterPolicy.Default;

    /// <summary>Ordering required.</summary>
    public OrderingScope Ordering { get; init; } = OrderingScope.None;

    /// <summary>Wall-clock budget for one handler invocation.</summary>
    public TimeSpan HandlerTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Everything the service publishes to and subscribes from.</summary>
/// <param name="Destinations">Destinations this service publishes to.</param>
/// <param name="Subscriptions">Subscriptions this service consumes.</param>
public sealed record TopologyManifest(
    IReadOnlyList<MessageDestination> Destinations,
    IReadOnlyList<SubscriptionSpec> Subscriptions);

/// <summary>How strictly a transport should treat the declared topology.</summary>
public enum TopologyMode
{
    /// <summary>
    /// Verify the topology exists and matches, and fail startup otherwise. The production posture:
    /// topology is provisioned by IaC, and drift should be loud rather than silently auto-created.
    /// </summary>
    Assert,

    /// <summary>
    /// Create anything missing. Permitted only in Development and test, where there is no IaC
    /// pipeline and a developer's first run should simply work.
    /// </summary>
    Provision,
}
