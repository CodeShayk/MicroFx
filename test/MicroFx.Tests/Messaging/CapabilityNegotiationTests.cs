using MicroFx.Features;
using MicroFx.Messaging;
using MicroFx.Messaging.Transport;

namespace MicroFx.Tests.Messaging;

/// <summary>
/// The negotiation matrix, one test per row.
/// </summary>
/// <remarks>
/// The rule under test throughout: the core emulates a <em>convenience</em> and reports it, and
/// refuses to start rather than silently downgrade a <em>correctness</em> guarantee.
/// </remarks>
[TestFixture]
internal sealed class CapabilityNegotiationTests
{
    private const TransportCapabilities Full =
        TransportCapabilities.PublisherConfirms |
        TransportCapabilities.ManualAcknowledgement |
        TransportCapabilities.NativeDeadLetter |
        TransportCapabilities.NativeDelayedDelivery |
        TransportCapabilities.OrderedDelivery |
        TransportCapabilities.ConsumerCancellation |
        TransportCapabilities.BrokerSideFiltering;

    private static TopologyManifest Manifest(Action<SubscriptionSpecBuilder>? configure = null)
    {
        var builder = new SubscriptionSpecBuilder();
        configure?.Invoke(builder);

        return new TopologyManifest(
            [new MessageDestination(DestinationKind.Event, "orders", "order.placed")],
            [builder.Build()]);
    }

    private static CapabilityNegotiation Negotiate(
        TransportCapabilities available,
        Action<SubscriptionSpecBuilder>? configure = null,
        bool allowUnconfirmed = false,
        bool hasScheduledStore = true) =>
        CapabilityNegotiator.Negotiate(
            Manifest(configure), available, allowUnconfirmed, hasScheduledStore);

    private static CapabilityDecision Find(CapabilityNegotiation negotiation, string requirement) =>
        negotiation.Decisions.First(d =>
            d.Requirement.Contains(requirement, StringComparison.Ordinal));

    // ---- Correctness guarantees: refuse rather than downgrade ------------------------------------

    [Test]
    public void At_least_once_without_acknowledgement_is_unsatisfiable()
    {
        // Cannot be faked: without explicit acknowledgement a crash mid-handling loses the message,
        // and no amount of platform code recovers it.
        var negotiation = Negotiate(Full & ~TransportCapabilities.ManualAcknowledgement);

        Assert.Multiple(() =>
        {
            Assert.That(negotiation.HasUnsatisfiable, Is.True);
            Assert.That(
                Find(negotiation, "at-least-once").Resolution,
                Is.EqualTo(CapabilityResolution.Unsatisfiable));
        });
    }

    [Test]
    public void At_most_once_without_acknowledgement_is_accepted()
    {
        // The service has explicitly said loss is acceptable, so there is nothing to refuse.
        var negotiation = Negotiate(
            Full & ~TransportCapabilities.ManualAcknowledgement,
            builder => builder.Guarantee = DeliveryGuarantee.AtMostOnce);

        Assert.That(negotiation.HasUnsatisfiable, Is.False);
    }

    [Test]
    public void Per_key_ordering_without_ordered_delivery_is_unsatisfiable()
    {
        // Order cannot be restored after interleaved delivery.
        var negotiation = Negotiate(
            Full & ~TransportCapabilities.OrderedDelivery,
            builder => builder.Ordering = OrderingScope.PerKey);

        Assert.That(
            Find(negotiation, "per-key ordering").Resolution,
            Is.EqualTo(CapabilityResolution.Unsatisfiable));
    }

    [Test]
    public void Publishing_without_confirms_is_unsatisfiable_by_default()
    {
        var negotiation = Negotiate(Full & ~TransportCapabilities.PublisherConfirms);

        Assert.Multiple(() =>
        {
            Assert.That(negotiation.HasUnsatisfiable, Is.True);
            Assert.That(
                Find(negotiation, "reliable publish").Resolution,
                Is.EqualTo(CapabilityResolution.Unsatisfiable));
        });
    }

    [Test]
    public void Publishing_without_confirms_degrades_when_explicitly_allowed()
    {
        // Accepting the risk is permitted, but it is recorded as a degradation rather than passing
        // silently — the service opted in and the catalog says so.
        var negotiation = Negotiate(
            Full & ~TransportCapabilities.PublisherConfirms, allowUnconfirmed: true);

        Assert.Multiple(() =>
        {
            Assert.That(negotiation.HasUnsatisfiable, Is.False);
            Assert.That(
                Find(negotiation, "reliable publish").Resolution,
                Is.EqualTo(CapabilityResolution.Degraded));
        });
    }

    // ---- Conveniences: emulate and report -------------------------------------------------------

    [Test]
    public void Missing_delayed_delivery_is_emulated_by_the_scheduled_store()
    {
        var negotiation = Negotiate(Full & ~TransportCapabilities.NativeDelayedDelivery);

        Assert.Multiple(() =>
        {
            Assert.That(negotiation.HasUnsatisfiable, Is.False);
            Assert.That(
                Find(negotiation, "delayed retry").Resolution,
                Is.EqualTo(CapabilityResolution.Emulated));
            Assert.That(Find(negotiation, "delayed retry").Detail, Does.Contain("scheduled-message store"));
        });
    }

    [Test]
    public void Missing_delayed_delivery_with_no_store_is_unsatisfiable()
    {
        // In-process sleeping is never the fallback: it holds the delivery and stalls the consumer.
        var negotiation = Negotiate(
            Full & ~TransportCapabilities.NativeDelayedDelivery, hasScheduledStore: false);

        Assert.That(
            Find(negotiation, "delayed retry").Resolution,
            Is.EqualTo(CapabilityResolution.Unsatisfiable));
    }

    [Test]
    public void Missing_dead_letter_is_always_emulable()
    {
        var negotiation = Negotiate(Full & ~TransportCapabilities.NativeDeadLetter);

        Assert.Multiple(() =>
        {
            Assert.That(negotiation.HasUnsatisfiable, Is.False);
            Assert.That(
                Find(negotiation, "dead-lettering").Resolution,
                Is.EqualTo(CapabilityResolution.Emulated));
        });
    }

    [Test]
    public void Missing_broker_side_filtering_is_emulated_and_the_waste_is_named()
    {
        var negotiation = Negotiate(
            Full & ~TransportCapabilities.BrokerSideFiltering,
            builder => builder.Filter = "order.*");

        var decision = Find(negotiation, "broker-side filtering");

        Assert.Multiple(() =>
        {
            Assert.That(decision.Resolution, Is.EqualTo(CapabilityResolution.Emulated));
            Assert.That(decision.Detail, Does.Contain("messaging.filtered.count"));
        });
    }

    [Test]
    public void Missing_consumer_cancellation_degrades_the_drain_but_does_not_block_startup()
    {
        var negotiation = Negotiate(Full & ~TransportCapabilities.ConsumerCancellation);

        Assert.Multiple(() =>
        {
            Assert.That(negotiation.HasUnsatisfiable, Is.False);
            Assert.That(
                Find(negotiation, "graceful drain").Resolution,
                Is.EqualTo(CapabilityResolution.Emulated));
        });
    }

    // ---- Reporting ------------------------------------------------------------------------------

    [Test]
    public void A_fully_capable_transport_produces_no_findings()
    {
        var report = Negotiate(Full).ToValidationReport();

        Assert.That(report.Findings.Any(f => f.Severity != ValidationSeverity.Information), Is.False);
    }

    [Test]
    public void Unsatisfiable_requirements_become_validation_errors_naming_the_subscription()
    {
        var report = Negotiate(TransportCapabilities.None).ToValidationReport();

        Assert.Multiple(() =>
        {
            Assert.That(report.HasErrors, Is.True);
            Assert.That(
                report.Findings.Any(f => f.Message.Contains("subscriber", StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public void Every_gap_is_reported_in_one_pass()
    {
        // One startup should reveal every capability gap, not one per deploy.
        var report = Negotiate(
            TransportCapabilities.None, builder => builder.Ordering = OrderingScope.PerKey)
            .ToValidationReport();

        Assert.That(report.Findings.Count(f => f.Severity == ValidationSeverity.Error),
            Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Emulation_is_surfaced_as_information_not_silence()
    {
        var report = Negotiate(Full & ~TransportCapabilities.NativeDeadLetter).ToValidationReport();

        Assert.That(
            report.Findings.Any(f => f.Severity == ValidationSeverity.Information),
            Is.True);
    }

    private sealed class SubscriptionSpecBuilder
    {
        public DeliveryGuarantee Guarantee { get; set; } = DeliveryGuarantee.AtLeastOnce;

        public OrderingScope Ordering { get; set; } = OrderingScope.None;

        public string? Filter { get; set; }

        public RetryPolicy Retry { get; set; } = RetryPolicy.Default;

        public SubscriptionSpec Build() => new()
        {
            ConsumerGroup = "subscriber",
            Source = new MessageDestination(DestinationKind.Event, "orders", "order.placed"),
            Guarantee = Guarantee,
            Ordering = Ordering,
            Filter = Filter,
            Retry = Retry,
        };
    }
}
