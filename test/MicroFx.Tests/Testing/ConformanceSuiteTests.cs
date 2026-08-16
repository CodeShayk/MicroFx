using MicroFx.Messaging;
using MicroFx.Messaging.Transport;
using MicroFx.Messaging.Transport.InMemory;
using MicroFx.Testing;

namespace MicroFx.Tests.Testing;

/// <summary>
/// Runs the conformance suite against the in-memory transport.
/// </summary>
/// <remarks>
/// This is what keeps the suite itself honest. A conformance suite that has never been run against
/// a transport known to be correct cannot distinguish "the adapter is broken" from "the suite is
/// broken" — and the first time it runs against a real broker is the worst moment to find out.
/// </remarks>
[TestFixture]
internal sealed class ConformanceSuiteTests
{
    private static TransportConformanceSuite SuiteFor(IMessageTransport transport) =>
        new(transport) { Timeout = TimeSpan.FromSeconds(3) };

    [Test]
    public async Task A_fully_capable_transport_passes_every_check()
    {
        await using var transport = new InMemoryTransport();

        var report = await SuiteFor(transport).RunAsync();

        Assert.That(report.Passed, Is.True, report.ToString());
        Assert.That(report.Results.Any(r => r.Skipped), Is.False,
            "A fully capable transport should skip nothing.");
    }

    [Test]
    public async Task Checks_for_unadvertised_capabilities_are_skipped_not_failed()
    {
        // A transport is judged against what it claims, not against a fixed ideal. Failing a
        // check for a capability it never advertised would make the suite useless for any
        // deliberately limited transport.
        await using var transport = new InMemoryTransport(new InMemoryTransportOptions
        {
            Capabilities = TransportCapabilities.ManualAcknowledgement,
        });

        var report = await SuiteFor(transport).RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.Passed, Is.True, report.ToString());
            Assert.That(
                report.Results.Count(r => r.Skipped), Is.GreaterThanOrEqualTo(3),
                "Unadvertised capabilities should have been skipped.");
        });
    }

    [Test]
    public async Task An_unconfirmed_publish_from_a_transport_claiming_confirms_fails()
    {
        // The dishonest-flag case the suite exists to catch. The in-memory transport reports
        // Confirmed only when it advertises confirms, so claiming the flag while a publish goes
        // unconfirmed is caught here rather than by missing messages in production.
        await using var transport = new DishonestTransport();

        var report = await SuiteFor(transport).RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(report.Passed, Is.False);
            Assert.That(
                report.Failures.Any(f => f.Capability == TransportCapabilities.PublisherConfirms),
                Is.True,
                report.ToString());
        });
    }

    [Test]
    public async Task The_report_reads_as_a_diagnosis_rather_than_a_boolean()
    {
        await using var transport = new InMemoryTransport();

        var report = await SuiteFor(transport).RunAsync();
        var text = report.ToString();

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("in-memory"));
            Assert.That(text, Does.Contain("round trip"));
            Assert.That(text, Does.Contain("header fidelity"));
        });
    }

    /// <summary>A transport that advertises confirms but never confirms anything.</summary>
    private sealed class DishonestTransport : IMessageTransport, IAsyncDisposable
    {
        private readonly InMemoryTransport _inner = new();

        public string Name => "dishonest";

        public TransportCapabilities Capabilities =>
            _inner.Capabilities | TransportCapabilities.PublisherConfirms;

        public async Task<PublishReceipt> PublishAsync(
            TransportMessage message, CancellationToken cancellationToken = default)
        {
            await _inner.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            return new PublishReceipt(Confirmed: false);
        }

        public Task<ITransportSubscription> SubscribeAsync(
            SubscriptionSpec specification,
            TransportDeliveryHandler handler,
            CancellationToken cancellationToken = default) =>
            _inner.SubscribeAsync(specification, handler, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
