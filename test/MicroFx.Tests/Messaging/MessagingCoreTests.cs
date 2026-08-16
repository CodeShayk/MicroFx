using MicroFx.Messaging;
using Microsoft.Extensions.Time.Testing;

namespace MicroFx.Tests.Messaging;

[TestFixture]
internal sealed class MessageTypeRegistryTests
{
    private sealed record OrderPlacedV1(string Id) : IIntegrationEvent;

    private sealed record ReserveInventory(string Id) : ICommand;

    private static MessageTypeRegistry Build(params Type[] types)
    {
        var builder = new MessageTypeRegistryBuilder();
        foreach (var type in types)
        {
            builder.Register(type);
        }

        return builder.Build();
    }

    [Test]
    public void A_registered_type_resolves_by_its_wire_name()
    {
        var registry = Build(typeof(OrderPlacedV1));
        var wireName = registry.RequireWireName(typeof(OrderPlacedV1));

        Assert.That(registry.TryResolve(wireName, out var resolved), Is.True);
        Assert.That(resolved, Is.EqualTo(typeof(OrderPlacedV1)));
    }

    [TestCase("System.String")]
    [TestCase("System.Diagnostics.Process, System.Diagnostics.Process")]
    [TestCase("System.IO.File")]
    [TestCase("MicroFx.Tests.Messaging.MessageTypeRegistryTests+OrderPlacedV1")]
    public void An_unregistered_name_never_resolves(string wireName)
    {
        // The whole point of the registry. Resolving an inbound name through reflection would let
        // anyone able to publish to the broker instantiate arbitrary types in this process.
        var registry = Build(typeof(OrderPlacedV1));

        Assert.That(registry.TryResolve(wireName, out _), Is.False);
    }

    [Test]
    public void An_empty_or_null_name_never_resolves()
    {
        var registry = Build(typeof(OrderPlacedV1));

        Assert.Multiple(() =>
        {
            Assert.That(registry.TryResolve(string.Empty, out _), Is.False);
            Assert.That(registry.TryResolve(null!, out _), Is.False);
        });
    }

    [Test]
    public void The_wire_name_carries_no_assembly_or_namespace_path()
    {
        // The wire name is a published contract, so it must survive an assembly rename — and must
        // give a consumer nothing it could use to locate a type by reflection.
        var registry = Build(typeof(OrderPlacedV1));
        var wireName = registry.RequireWireName(typeof(OrderPlacedV1));

        Assert.Multiple(() =>
        {
            Assert.That(wireName, Does.Not.Contain("MicroFx.Tests"));
            Assert.That(wireName, Does.Not.Contain(","));
            Assert.That(wireName, Does.Contain("order-placed"));
            Assert.That(wireName, Does.EndWith(".v1"));
        });
    }

    [Test]
    public void An_unregistered_type_fails_with_an_actionable_message()
    {
        var registry = Build(typeof(OrderPlacedV1));

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.RequireWireName(typeof(ReserveInventory)));

        Assert.That(exception!.Message, Does.Contain("not registered"));
    }

    [Test]
    public void Two_types_claiming_one_wire_name_is_refused()
    {
        // Otherwise one silently deserializes as the other.
        var builder = new MessageTypeRegistryBuilder();
        builder.Register(typeof(OrderPlacedV1), "shared.name");

        Assert.Throws<InvalidOperationException>(
            () => builder.Register(typeof(ReserveInventory), "shared.name"));
    }

    [Test]
    public void Registering_the_same_type_twice_is_idempotent()
    {
        var builder = new MessageTypeRegistryBuilder();
        var first = builder.Register(typeof(OrderPlacedV1));
        var second = builder.Register(typeof(OrderPlacedV1));

        Assert.That(second, Is.EqualTo(first));
    }
}

[TestFixture]
internal sealed class RetryPolicyTests
{
    [Test]
    public void Backoff_grows_exponentially()
    {
        var policy = new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(1), Jitter = 0 };

        Assert.Multiple(() =>
        {
            Assert.That(policy.DelayFor(2), Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(policy.DelayFor(3), Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(policy.DelayFor(4), Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(policy.DelayFor(5), Is.EqualTo(TimeSpan.FromSeconds(8)));
        });
    }

    [Test]
    public void Backoff_is_capped()
    {
        var policy = new RetryPolicy
        {
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(10),
            Jitter = 0,
        };

        Assert.That(policy.DelayFor(20), Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void An_absurd_attempt_number_does_not_overflow()
    {
        // The exponent is bounded before shifting; without that the multiplier overflows long
        // before it reaches the cap.
        var policy = new RetryPolicy { Jitter = 0 };

        Assert.That(policy.DelayFor(int.MaxValue), Is.EqualTo(policy.MaxDelay));
    }

    [Test]
    public void A_handler_requested_delay_is_clamped_not_honoured_outright()
    {
        // A handler asking for six hours would otherwise park a delivery far beyond any sensible
        // operational window.
        var policy = new RetryPolicy { MaxDelay = TimeSpan.FromMinutes(1) };

        Assert.That(policy.DelayFor(2, TimeSpan.FromHours(6)), Is.EqualTo(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void A_negative_requested_delay_becomes_zero() =>
        Assert.That(RetryPolicy.Default.DelayFor(2, TimeSpan.FromSeconds(-5)), Is.EqualTo(TimeSpan.Zero));

    [Test]
    public void Jitter_spreads_delays_without_exceeding_the_cap()
    {
        var policy = new RetryPolicy
        {
            BaseDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(30),
            Jitter = 0.5,
        };

        var delays = Enumerable.Range(0, 50).Select(_ => policy.DelayFor(2)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(delays.Distinct().Count(), Is.GreaterThan(1), "Jitter produced identical delays.");
            Assert.That(delays, Is.All.LessThanOrEqualTo(policy.MaxDelay));
            Assert.That(delays, Is.All.GreaterThanOrEqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public void The_none_policy_never_retries() =>
        Assert.That(RetryPolicy.None.MaxAttempts, Is.EqualTo(1));
}

[TestFixture]
internal sealed class InboxStoreTests
{
    private FakeTimeProvider _clock = null!;
    private InMemoryInboxStore _inbox = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        _inbox = new InMemoryInboxStore(_clock);
    }

    [Test]
    public async Task The_first_delivery_is_admitted_and_the_second_is_not()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await _inbox.TryBeginAsync("group", "msg-1"), Is.True);
            Assert.That(await _inbox.TryBeginAsync("group", "msg-1"), Is.False);
        });

        await Task.CompletedTask;
    }

    [Test]
    public async Task Each_consumer_group_processes_the_same_message_once()
    {
        // The same message legitimately reaches several subscribers, and each must handle it.
        Assert.Multiple(async () =>
        {
            Assert.That(await _inbox.TryBeginAsync("shipping", "msg-1"), Is.True);
            Assert.That(await _inbox.TryBeginAsync("billing", "msg-1"), Is.True);
            Assert.That(await _inbox.TryBeginAsync("shipping", "msg-1"), Is.False);
        });

        await Task.CompletedTask;
    }

    [Test]
    public async Task Releasing_lets_a_failed_message_be_retried()
    {
        await _inbox.TryBeginAsync("group", "msg-1");
        await _inbox.ReleaseAsync("group", "msg-1");

        Assert.That(await _inbox.TryBeginAsync("group", "msg-1"), Is.True);
    }

    [Test]
    public async Task Concurrent_admission_lets_exactly_one_caller_through()
    {
        // A check-then-insert would let two concurrent redeliveries both observe "not seen" and
        // both run the handler — precisely the duplicate the inbox exists to prevent.
        var admitted = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(async _ =>
        {
            if (await _inbox.TryBeginAsync("group", "contended"))
            {
                Interlocked.Increment(ref admitted);
            }
        }));

        Assert.That(admitted, Is.EqualTo(1));
    }

    [Test]
    public async Task Purge_removes_only_entries_past_the_retention_window()
    {
        await _inbox.TryBeginAsync("group", "old");
        _clock.Advance(TimeSpan.FromDays(8));
        await _inbox.TryBeginAsync("group", "recent");

        var removed = await _inbox.PurgeAsync(TimeSpan.FromDays(7));

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(_inbox.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_blank_identifier_is_refused() =>
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentException>(async () => await _inbox.TryBeginAsync("", "msg"));
            Assert.ThrowsAsync<ArgumentException>(async () => await _inbox.TryBeginAsync("group", " "));
        });
}
