using System.Globalization;
using System.Text;

namespace MicroFx.Messaging.RabbitMq;

/// <summary>
/// Maps the platform's transport-neutral topology onto RabbitMQ objects.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the adapter's vocabulary translation lives here. Service code never sees an
/// exchange, a queue, or a routing key — which is what lets the same declarations run on a
/// different broker.
/// </para>
/// <para>
/// Every produced name is sanitised and length-bounded. Destination names originate in service
/// declarations rather than in message content, but a name that reached the broker unchecked could
/// still create an object that collides with, or shadows, another service's.
/// </para>
/// </remarks>
internal sealed class RabbitMqTopologyMapper(RabbitMqOptions options)
{
    /// <summary>RabbitMQ's own limit on exchange and queue names.</summary>
    private const int MaxNameLength = 255;

    /// <summary>Exchange a destination publishes to.</summary>
    /// <remarks>
    /// Commands use a <c>direct</c> exchange bound to exactly one queue; events use a
    /// <c>topic</c> exchange with one queue per consumer group, which is what makes each subscriber
    /// independent rather than a competing consumer.
    /// </remarks>
    public string ExchangeFor(MessageDestination destination) =>
        Name(options.NamePrefix, destination.Owner, Suffix(destination.Kind));

    /// <summary>Exchange type for a destination kind.</summary>
    public static string ExchangeTypeFor(DestinationKind kind) => kind switch
    {
        DestinationKind.Event => "topic",
        DestinationKind.Archive => "topic",
        _ => "direct",
    };

    /// <summary>Routing key a message is published with.</summary>
    public static string RoutingKeyFor(MessageDestination destination) =>
        Sanitize($"{destination.Name}.{destination.Version}");

    /// <summary>Queue backing one consumer group.</summary>
    /// <remarks>
    /// One queue per consumer group is the load-bearing mapping: each subscriber gets its own
    /// backlog, retry, and dead-letter. Two services sharing a queue would turn publish/subscribe
    /// into competing consumers, and each would see only some of the events.
    /// </remarks>
    public string QueueFor(SubscriptionSpec subscription) =>
        Name(options.NamePrefix, subscription.ConsumerGroup);

    /// <summary>Dead-letter exchange for a consumer group.</summary>
    public string DeadLetterExchangeFor(SubscriptionSpec subscription) =>
        Name(options.NamePrefix, subscription.ConsumerGroup, "dlx");

    /// <summary>Dead-letter queue for a consumer group.</summary>
    public string DeadLetterQueueFor(SubscriptionSpec subscription) =>
        Name(options.NamePrefix, subscription.ConsumerGroup, "dlq");

    /// <summary>Fanout exchange fronting one retry rung.</summary>
    /// <remarks>
    /// Fanout rather than direct, deliberately. A message entering a rung carries the <em>target
    /// queue name</em> as its routing key, so that on TTL expiry the default exchange routes it
    /// straight back. A direct exchange would try to route on that key and the message would never
    /// reach the rung at all.
    /// </remarks>
    public string RetryExchangeFor(TimeSpan rung) =>
        Name(options.NamePrefix, "retry", RungToken(rung), "ex");

    /// <summary>Holding queue for one retry rung.</summary>
    public string RetryQueueFor(TimeSpan rung) =>
        Name(options.NamePrefix, "retry", RungToken(rung));

    /// <summary>Holding queue for a message delayed on its way to a destination.</summary>
    /// <remarks>
    /// Distinct from <see cref="RetryQueueFor"/>, because the two cases expire to different places.
    /// A retry returns to <em>one</em> consumer group's queue — only that consumer failed, so
    /// redelivering to every subscriber would duplicate work that already succeeded. A delayed
    /// publish has not been delivered to anyone yet and must fan out to <em>all</em> of them on
    /// expiry, which means expiring to the destination's exchange rather than to a queue. One
    /// holding queue per destination and rung is what lets the dead-letter settings differ.
    /// </remarks>
    public string DelayQueueFor(MessageDestination destination, TimeSpan rung) =>
        Name(options.NamePrefix, "delay", RungToken(rung), destination.Owner,
            $"{destination.Name}.{destination.Version}");

    /// <summary>Queue receiving messages that matched no binding.</summary>
    /// <remarks>
    /// An unroutable message is always a topology or naming defect, and dropping it silently is how
    /// that defect goes unnoticed for months.
    /// </remarks>
    public string UnroutableQueue() => Name(options.NamePrefix, "unroutable");

    /// <summary>Alternate exchange every exchange falls back to.</summary>
    public string AlternateExchange() => Name(options.NamePrefix, "unroutable", "ex");

    /// <summary>Selects the smallest rung that is at least the requested delay.</summary>
    /// <remarks>
    /// Rounds <em>up</em>: firing earlier than the retry policy asked for would defeat the backoff
    /// that exists to let a struggling dependency recover.
    /// </remarks>
    public TimeSpan SelectRung(TimeSpan delay)
    {
        foreach (var rung in options.RetryLadder)
        {
            if (rung >= delay)
            {
                return rung;
            }
        }

        return options.RetryLadder[^1];
    }

    /// <summary>Arguments for a work queue.</summary>
    public Dictionary<string, object?> WorkQueueArguments(SubscriptionSpec subscription)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-dead-letter-exchange"] = DeadLetterExchangeFor(subscription),
            ["x-max-length-bytes"] = options.MaxQueueLengthBytes,

            // reject-publish, never drop-head. Silently discarding the oldest business message to
            // make room for a newer one is data loss disguised as back-pressure.
            ["x-overflow"] = "reject-publish",
        };

        if (options.UseQuorumQueues)
        {
            arguments["x-queue-type"] = "quorum";
            arguments["x-quorum-initial-group-size"] = options.QuorumGroupSize;
            arguments["x-delivery-limit"] = options.DeliveryLimit;
        }

        // Single-active-consumer is how per-key ordering is honoured. It caps throughput at one
        // consumer, which is exactly the trade the ordering guarantee costs.
        if (subscription.Ordering == OrderingScope.PerKey)
        {
            arguments["x-single-active-consumer"] = true;
        }

        return arguments;
    }

    /// <summary>Arguments for a retry rung's holding queue.</summary>
    public Dictionary<string, object?> RetryQueueArguments(TimeSpan rung) => new(StringComparer.Ordinal)
    {
        ["x-message-ttl"] = (long)rung.TotalMilliseconds,

        // The default exchange, with no dead-letter routing key override, so the message's own
        // routing key — the target queue name — routes it straight back on expiry.
        ["x-dead-letter-exchange"] = string.Empty,
        ["x-queue-type"] = options.UseQuorumQueues ? "quorum" : "classic",
    };

    /// <summary>Arguments for a delayed-publish holding queue.</summary>
    /// <remarks>
    /// The routing key is overridden explicitly. Dead-lettering preserves the key the message was
    /// <em>published</em> with, and reaching this queue through the default exchange means that key
    /// is the queue's own name — which would match no binding on the destination exchange and land
    /// the message in the unroutable queue instead of being delivered.
    /// </remarks>
    public Dictionary<string, object?> DelayQueueArguments(
        MessageDestination destination, TimeSpan rung) => new(StringComparer.Ordinal)
    {
        ["x-message-ttl"] = (long)rung.TotalMilliseconds,
        ["x-dead-letter-exchange"] = ExchangeFor(destination),
        ["x-dead-letter-routing-key"] = RoutingKeyFor(destination),
        ["x-queue-type"] = options.UseQuorumQueues ? "quorum" : "classic",
    };

    /// <summary>Arguments for a dead-letter queue.</summary>
    public Dictionary<string, object?> DeadLetterQueueArguments() => new(StringComparer.Ordinal)
    {
        ["x-queue-type"] = options.UseQuorumQueues ? "quorum" : "classic",
    };

    private static string Suffix(DestinationKind kind) => kind switch
    {
        DestinationKind.Command => "cmd",
        DestinationKind.Event => "evt",
        DestinationKind.Request => "req",
        DestinationKind.Reply => "rep",
        DestinationKind.DeadLetter => "dlx",
        DestinationKind.Archive => "arc",
        _ => "unknown",
    };

    private static string RungToken(TimeSpan rung) =>
        ((long)rung.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    private static string Name(params string[] segments)
    {
        var name = string.Join('.', segments.Select(Sanitize).Where(s => s.Length > 0));

        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        // Truncating alone could make two distinct destinations collide on one queue, which would
        // silently merge two subscribers' traffic. The hash suffix keeps them distinct.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12];

        return string.Concat(name.AsSpan(0, MaxNameLength - 13), ".", hash);
    }

    /// <summary>Restricts a segment to characters that are unambiguous in a broker name.</summary>
    private static string Sanitize(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length);

        foreach (var character in segment)
        {
            builder.Append(
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? char.ToLowerInvariant(character)
                    : '_');
        }

        return builder.ToString();
    }
}
