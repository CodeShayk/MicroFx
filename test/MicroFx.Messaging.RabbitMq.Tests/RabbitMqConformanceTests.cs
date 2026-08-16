using MicroFx.Core;
using MicroFx.Messaging.Transport;
using MicroFx.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MicroFx.Messaging.RabbitMq.Tests;

/// <summary>
/// Runs the platform's transport conformance suite against a real RabbitMQ broker.
/// </summary>
/// <remarks>
/// <para>
/// The adapter's acceptance criterion. The messaging <em>semantics</em> are already proven against
/// the in-memory transport in milliseconds; what a broker adds is proof that this adapter honours
/// the capabilities it advertises. A transport that claims publisher confirms without honouring
/// them would make the outbox mark rows dispatched that never arrived.
/// </para>
/// <para>
/// Skips with an explicit message when no broker is reachable, so a developer without Docker gets a
/// clear "not run" rather than a red build or — worse — a green one that proved nothing.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class RabbitMqConformanceTests
{
    /// <summary>Set by CI's service container; absent on a developer machine without a broker.</summary>
    private const string BrokerUriVariable = "MICROFX_RABBITMQ_URI";

    private RabbitMqTransport _transport = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var uri = Environment.GetEnvironmentVariable(BrokerUriVariable);

        if (string.IsNullOrWhiteSpace(uri))
        {
            Assert.Ignore(
                $"No broker configured. Set {BrokerUriVariable} to run the RabbitMQ conformance " +
                "suite — for example amqp://guest:guest@localhost:5672/ against a local container.");
        }

        var options = new RabbitMqOptions
        {
            Uri = uri!,

            // Plaintext AMQP against a throwaway CI broker. Startup validation refuses this
            // outside Development, which is the behaviour under test elsewhere.
            AllowInsecureTransport = true,

            // A single-node CI broker cannot form a quorum, so classic queues are the only option
            // here. Production validation warns about exactly this.
            UseQuorumQueues = false,

            NamePrefix = "microfx-conformance",
        };

        var metadata = new ServiceMetadata
        {
            Name = "conformance",
            Version = "1.0.0",
            Environment = "Development",
            InstanceId = Guid.NewGuid().ToString("N")[..8],
        };

        var connections = new RabbitMqConnectionProvider(
            Options.Create(options), metadata, NullLogger<RabbitMqConnectionProvider>.Instance);

        _transport = new RabbitMqTransport(
            connections, Options.Create(options), TimeProvider.System, NullLoggerFactory.Instance);

        // Development mode: the broker is empty, so the topology has to be created before anything
        // can be published to it.
        await _transport.AssertAsync(
            new TopologyManifest([], []), TopologyMode.Provision, CancellationToken.None);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }
    }

    [Test]
    public async Task The_adapter_honours_every_capability_it_advertises()
    {
        var suite = new TransportConformanceSuite(_transport)
        {
            // Generous: a real broker involves real network round trips, and a flaky timeout would
            // teach people to ignore this suite.
            Timeout = TimeSpan.FromSeconds(30),
        };

        var report = await suite.RunAsync();

        Assert.That(report.Passed, Is.True, report.ToString());
    }

    [Test]
    public void The_advertised_capabilities_match_the_documented_set()
    {
        // Pinned deliberately. A capability silently gained or lost changes what the core decides
        // to emulate, degrade, or refuse — so a change here should be a reviewed diff.
        Assert.Multiple(() =>
        {
            Assert.That(_transport.Capabilities.HasFlag(TransportCapabilities.PublisherConfirms), Is.True);
            Assert.That(_transport.Capabilities.HasFlag(TransportCapabilities.ManualAcknowledgement), Is.True);
            Assert.That(_transport.Capabilities.HasFlag(TransportCapabilities.NativeDeadLetter), Is.True);
            Assert.That(_transport.Capabilities.HasFlag(TransportCapabilities.NativeDelayedDelivery), Is.True);
            Assert.That(_transport.Capabilities.HasFlag(TransportCapabilities.ConsumerCancellation), Is.True);

            // Absent on purpose: AMQP transactions are slow and do not span the database anyway,
            // which is what the outbox is for.
            Assert.That(_transport.Capabilities.HasFlag(TransportCapabilities.Transactions), Is.False);

            // Absent on purpose: priority would force classic queues, giving up quorum replication
            // for a rarely-needed feature.
            Assert.That(_transport.Capabilities.HasFlag(TransportCapabilities.Priority), Is.False);
        });
    }
}
