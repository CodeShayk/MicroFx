using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MicroFx.Messaging;

/// <summary>
/// Messaging traces and metrics, following OpenTelemetry messaging semantic conventions so a
/// backend renders the topology without being told about it.
/// </summary>
internal static class MessagingDiagnostics
{
    public const string ActivitySourceName = "MicroFx.Messaging";
    public const string MeterName = "MicroFx.Messaging";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> PublishCount = Meter.CreateCounter<long>(
        "messaging.publish.count", description: "Messages published.");

    private static readonly Histogram<double> ConsumeDuration = Meter.CreateHistogram<double>(
        "messaging.consume.duration", unit: "s", description: "Handler duration.");

    private static readonly Histogram<int> ConsumeAttempts = Meter.CreateHistogram<int>(
        "messaging.consume.attempts", description: "Attempt number at which a message settled.");

    private static readonly Counter<long> DeadLetterCount = Meter.CreateCounter<long>(
        "messaging.deadletter.count", description: "Messages dead-lettered.");

    private static readonly Counter<long> DedupeCount = Meter.CreateCounter<long>(
        "messaging.dedupe.count", description: "Duplicate deliveries suppressed by the inbox.");

    private static readonly Counter<long> FilteredCount = Meter.CreateCounter<long>(
        "messaging.filtered.count",
        description: "Deliveries discarded by consumer-side filtering. Non-zero means the transport " +
                     "cannot filter broker-side and the waste is real.");

    private static readonly Counter<long> RetryCount = Meter.CreateCounter<long>(
        "messaging.retry.count", description: "Deliveries scheduled for retry.");

    private static readonly Counter<long> ExpiredCount = Meter.CreateCounter<long>(
        "messaging.expired.count", description: "Messages discarded for arriving past their expiry.");

    private static readonly Counter<long> RejectedCount = Meter.CreateCounter<long>(
        "messaging.rejected.count",
        description: "Deliveries rejected before handling: malformed, unknown type, wrong kind, " +
                     "or unauthorized.");

    public static void Published(string destination, string type, bool confirmed) =>
        PublishCount.Add(1,
            new KeyValuePair<string, object?>("messaging.destination.name", destination),
            new KeyValuePair<string, object?>("messaging.message.type", type),
            new KeyValuePair<string, object?>("outcome", confirmed ? "confirmed" : "unconfirmed"));

    public static void Consumed(string consumerGroup, string type, string outcome, TimeSpan duration, int attempt)
    {
        ConsumeDuration.Record(duration.TotalSeconds,
            new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup),
            new KeyValuePair<string, object?>("messaging.message.type", type),
            new KeyValuePair<string, object?>("outcome", outcome));

        ConsumeAttempts.Record(attempt,
            new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup),
            new KeyValuePair<string, object?>("messaging.message.type", type));
    }

    public static void DeadLettered(string consumerGroup, string reason) =>
        DeadLetterCount.Add(1,
            new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup),
            // The reason is a handler-authored token, kept low-cardinality by contract so it is
            // safe as a metric tag.
            new KeyValuePair<string, object?>("reason", reason));

    public static void Deduplicated(string consumerGroup) =>
        DedupeCount.Add(1, new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup));

    public static void Filtered(string consumerGroup) =>
        FilteredCount.Add(1, new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup));

    public static void Retried(string consumerGroup, int attempt) =>
        RetryCount.Add(1,
            new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup),
            new KeyValuePair<string, object?>("attempt", attempt));

    public static void Expired(string consumerGroup) =>
        ExpiredCount.Add(1, new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup));

    public static void Rejected(string consumerGroup, string reason) =>
        RejectedCount.Add(1,
            new KeyValuePair<string, object?>("messaging.consumer.group.name", consumerGroup),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Starts a producer span.</summary>
    public static Activity? StartPublish(MessageDestination destination, string type)
    {
        var activity = Source.StartActivity(
            $"{destination.Name} publish", ActivityKind.Producer);

        activity?.SetTag("messaging.operation.name", "publish");
        activity?.SetTag("messaging.destination.name", destination.ToString());
        activity?.SetTag("messaging.message.type", type);
        return activity;
    }

    /// <summary>
    /// Starts a consumer span parented to the producer's trace context.
    /// </summary>
    /// <remarks>
    /// Using the extracted context as the <em>parent</em> is what joins publish and consume into one
    /// trace. Batch processing would use a link instead, since a batch has many parents.
    /// </remarks>
    public static Activity? StartConsume(string consumerGroup, string type, string? traceParent)
    {
        ActivityContext.TryParse(traceParent, null, out var parent);

        var activity = Source.StartActivity(
            $"{consumerGroup} process", ActivityKind.Consumer, parent);

        activity?.SetTag("messaging.operation.name", "process");
        activity?.SetTag("messaging.consumer.group.name", consumerGroup);
        activity?.SetTag("messaging.message.type", type);
        return activity;
    }
}
