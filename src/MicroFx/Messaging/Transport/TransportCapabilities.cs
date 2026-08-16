using MicroFx.Features;

namespace MicroFx.Messaging.Transport;

/// <summary>What a transport can actually do.</summary>
/// <remarks>
/// Advertised honestly by each adapter and checked at startup. The whole point of the flag set is
/// that a gap becomes a decision the platform makes explicitly — emulate, degrade, or refuse —
/// rather than a surprise discovered in production under load.
/// </remarks>
[Flags]
public enum TransportCapabilities
{
    /// <summary>Nothing beyond publish and subscribe.</summary>
    None = 0,

    /// <summary>A publish is not complete until the broker acknowledges it.</summary>
    PublisherConfirms = 1 << 0,

    /// <summary>Deliveries are acknowledged explicitly, making at-least-once achievable.</summary>
    ManualAcknowledgement = 1 << 1,

    /// <summary>The broker itself moves exhausted messages to a dead-letter destination.</summary>
    NativeDeadLetter = 1 << 2,

    /// <summary>The broker can hold a message until a future instant.</summary>
    NativeDelayedDelivery = 1 << 3,

    /// <summary>The broker provides correlated request/reply.</summary>
    NativeRequestReply = 1 << 4,

    /// <summary>Messages sharing a key are delivered in order.</summary>
    OrderedDelivery = 1 << 5,

    /// <summary>Messages carry a priority the broker honours.</summary>
    Priority = 1 << 6,

    /// <summary>Topology can be verified or created through the adapter.</summary>
    TopologyProvisioning = 1 << 7,

    /// <summary>A consumer can stop receiving without losing in-flight work.</summary>
    ConsumerCancellation = 1 << 8,

    /// <summary>Subscription filters are applied broker-side rather than after delivery.</summary>
    BrokerSideFiltering = 1 << 9,

    /// <summary>The broker expires messages past their TTL.</summary>
    MessageTtl = 1 << 10,

    /// <summary>Publishes can participate in a transaction.</summary>
    Transactions = 1 << 11,
}

/// <summary>How a required capability was satisfied.</summary>
public enum CapabilityResolution
{
    /// <summary>The transport provides it directly.</summary>
    Native,

    /// <summary>The core provides it another way, at some cost, and says so.</summary>
    Emulated,

    /// <summary>Requested, unavailable, and not emulable. The subscription proceeds without it.</summary>
    Degraded,

    /// <summary>Requested, unavailable, and not safely emulable. Startup fails.</summary>
    Unsatisfiable,
}

/// <summary>One capability decision.</summary>
/// <param name="Requirement">What was needed, in plain terms.</param>
/// <param name="Capability">The transport capability that would provide it.</param>
/// <param name="Resolution">How it was satisfied.</param>
/// <param name="Detail">What the platform did about it, and what that costs.</param>
/// <param name="Subscription">The subscription that required it, when it came from one.</param>
public readonly record struct CapabilityDecision(
    string Requirement,
    TransportCapabilities Capability,
    CapabilityResolution Resolution,
    string Detail,
    string? Subscription = null);

/// <summary>
/// Computes what the declared topology needs against what the transport advertises.
/// </summary>
/// <remarks>
/// <para>
/// The rule that keeps this honest: <b>the core emulates a convenience and reports it; it never
/// silently downgrades a correctness guarantee.</b> A missing delay or dead-letter is worked around
/// and logged. A missing acknowledgement means at-least-once is unachievable, so startup fails
/// rather than quietly delivering at-most-once — which would look fine until the day it lost a
/// payment.
/// </para>
/// <para>
/// Producing a report rather than throwing means a service with several capability gaps sees all of
/// them in one startup instead of one per deploy.
/// </para>
/// </remarks>
public static class CapabilityNegotiator
{
    /// <summary>Negotiates the manifest against a transport.</summary>
    public static CapabilityNegotiation Negotiate(
        TopologyManifest manifest,
        TransportCapabilities available,
        bool allowUnconfirmedPublish,
        bool hasScheduledMessageStore)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var decisions = new List<CapabilityDecision>();

        NegotiatePublishing(available, allowUnconfirmedPublish, decisions);

        foreach (var subscription in manifest.Subscriptions)
        {
            NegotiateSubscription(subscription, available, hasScheduledMessageStore, decisions);
        }

        if (!available.HasFlag(TransportCapabilities.ConsumerCancellation) &&
            manifest.Subscriptions.Count > 0)
        {
            decisions.Add(new CapabilityDecision(
                "graceful drain",
                TransportCapabilities.ConsumerCancellation,
                CapabilityResolution.Emulated,
                "The transport cannot cancel a consumer, so shutdown stops polling and waits for " +
                "in-flight work. Messages already delivered but unacknowledged are redelivered."));
        }

        return new CapabilityNegotiation(decisions);
    }

    private static void NegotiatePublishing(
        TransportCapabilities available, bool allowUnconfirmedPublish, List<CapabilityDecision> decisions)
    {
        if (available.HasFlag(TransportCapabilities.PublisherConfirms))
        {
            decisions.Add(new CapabilityDecision(
                "reliable publish",
                TransportCapabilities.PublisherConfirms,
                CapabilityResolution.Native,
                "Publishes are confirmed by the broker before being reported as complete."));
            return;
        }

        if (allowUnconfirmedPublish)
        {
            decisions.Add(new CapabilityDecision(
                "reliable publish",
                TransportCapabilities.PublisherConfirms,
                CapabilityResolution.Degraded,
                "AllowUnconfirmedPublish is set. A publish reported as successful may not have " +
                "reached the broker, so the outbox cannot guarantee at-least-once delivery."));
            return;
        }

        decisions.Add(new CapabilityDecision(
            "reliable publish",
            TransportCapabilities.PublisherConfirms,
            CapabilityResolution.Unsatisfiable,
            "The transport does not confirm publishes, so a publish reported as successful may " +
            "have been lost. Set MicroFx:Messaging:AllowUnconfirmedPublish=true to accept this " +
            "explicitly — it should carry an ADR reference."));
    }

    private static void NegotiateSubscription(
        SubscriptionSpec subscription,
        TransportCapabilities available,
        bool hasScheduledMessageStore,
        List<CapabilityDecision> decisions)
    {
        var group = subscription.ConsumerGroup;

        // At-least-once cannot be faked. Without explicit acknowledgement a crash mid-handling
        // loses the message, and no amount of platform code can recover it.
        if (subscription.Guarantee == DeliveryGuarantee.AtLeastOnce &&
            !available.HasFlag(TransportCapabilities.ManualAcknowledgement))
        {
            decisions.Add(new CapabilityDecision(
                "at-least-once delivery",
                TransportCapabilities.ManualAcknowledgement,
                CapabilityResolution.Unsatisfiable,
                "The transport does not support explicit acknowledgement, so a crash while handling " +
                "loses the message. Declare the subscription as AtMostOnce if that is genuinely " +
                "acceptable, or use a transport that acknowledges.",
                group));
        }

        // Ordering likewise: interleaved delivery cannot be reordered after the fact.
        if (subscription.Ordering == OrderingScope.PerKey &&
            !available.HasFlag(TransportCapabilities.OrderedDelivery))
        {
            decisions.Add(new CapabilityDecision(
                "per-key ordering",
                TransportCapabilities.OrderedDelivery,
                CapabilityResolution.Unsatisfiable,
                "The transport does not preserve per-key order, and order cannot be restored after " +
                "delivery.",
                group));
        }

        if (subscription.Retry.MaxAttempts > 1)
        {
            if (available.HasFlag(TransportCapabilities.NativeDelayedDelivery))
            {
                decisions.Add(new CapabilityDecision(
                    "delayed retry",
                    TransportCapabilities.NativeDelayedDelivery,
                    CapabilityResolution.Native,
                    "Retry backoff uses the transport's scheduler.",
                    group));
            }
            else if (hasScheduledMessageStore)
            {
                decisions.Add(new CapabilityDecision(
                    "delayed retry",
                    TransportCapabilities.NativeDelayedDelivery,
                    CapabilityResolution.Emulated,
                    "The transport cannot delay delivery, so retries go through the platform's " +
                    "scheduled-message store. In-process sleeping is never used: it would hold the " +
                    "delivery and stall the consumer.",
                    group));
            }
            else
            {
                decisions.Add(new CapabilityDecision(
                    "delayed retry",
                    TransportCapabilities.NativeDelayedDelivery,
                    CapabilityResolution.Unsatisfiable,
                    "The transport cannot delay delivery and no scheduled-message store is " +
                    "available. Enable persistence, or set the retry policy to None.",
                    group));
            }
        }

        if (subscription.DeadLetter.Enabled)
        {
            decisions.Add(available.HasFlag(TransportCapabilities.NativeDeadLetter)
                ? new CapabilityDecision(
                    "dead-lettering",
                    TransportCapabilities.NativeDeadLetter,
                    CapabilityResolution.Native,
                    "The broker moves exhausted messages to its dead-letter destination.",
                    group)
                : new CapabilityDecision(
                    "dead-lettering",
                    TransportCapabilities.NativeDeadLetter,
                    CapabilityResolution.Emulated,
                    "The transport has no dead-letter facility, so the platform republishes " +
                    "exhausted messages to a dead-letter destination with the failure history " +
                    "preserved in the envelope.",
                    group));
        }

        if (!string.IsNullOrEmpty(subscription.Filter) &&
            !available.HasFlag(TransportCapabilities.BrokerSideFiltering))
        {
            decisions.Add(new CapabilityDecision(
                "broker-side filtering",
                TransportCapabilities.BrokerSideFiltering,
                CapabilityResolution.Emulated,
                "The transport cannot filter, so non-matching messages are delivered and discarded " +
                "consumer-side. The waste is counted by messaging.filtered.count.",
                group));
        }
    }
}

/// <summary>The outcome of negotiation.</summary>
public sealed class CapabilityNegotiation(IReadOnlyList<CapabilityDecision> decisions)
{
    /// <summary>Every decision, in the order it was made.</summary>
    public IReadOnlyList<CapabilityDecision> Decisions { get; } = decisions;

    /// <summary>Whether any requirement could not be satisfied.</summary>
    public bool HasUnsatisfiable =>
        Decisions.Any(d => d.Resolution == CapabilityResolution.Unsatisfiable);

    /// <summary>Decisions where the platform worked around a gap.</summary>
    public IEnumerable<CapabilityDecision> Emulated =>
        Decisions.Where(d => d.Resolution == CapabilityResolution.Emulated);

    /// <summary>Converts the negotiation into a startup validation report.</summary>
    public ValidationReport ToValidationReport()
    {
        var findings = new List<ValidationFinding>();

        foreach (var decision in Decisions)
        {
            var severity = decision.Resolution switch
            {
                CapabilityResolution.Unsatisfiable => ValidationSeverity.Error,
                CapabilityResolution.Degraded => ValidationSeverity.Warning,
                CapabilityResolution.Emulated => ValidationSeverity.Information,
                _ => (ValidationSeverity?)null,
            };

            if (severity is null)
            {
                continue;
            }

            var scope = decision.Subscription is null ? string.Empty : $" [{decision.Subscription}]";
            findings.Add(new ValidationFinding(
                severity.Value, $"{decision.Requirement}{scope}: {decision.Detail}"));
        }

        return findings.Count == 0 ? ValidationReport.Ok() : ValidationReport.FromFindings(findings);
    }
}
