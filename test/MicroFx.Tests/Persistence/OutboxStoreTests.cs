using MicroFx.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace MicroFx.Tests.Persistence;

/// <summary>A minimal context carrying only the platform's tables.</summary>
internal sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyMicroFxPersistence();
}

/// <summary>
/// Exercises the outbox against a real relational database.
/// </summary>
/// <remarks>
/// SQLite rather than a fake. The outbox is defined by transactional behaviour and by claim
/// semantics under concurrency, and neither survives being stubbed — a substitute would pass while
/// the real thing lost messages.
/// </remarks>
[TestFixture]
internal sealed class OutboxStoreTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestDbContext> _options = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        // A shared in-memory database lives as long as the connection, so several contexts can see
        // the same data — which is what makes the concurrency tests meaningful.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options;
        _clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

        using var context = new TestDbContext(_options);
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private TestDbContext NewContext() => new(_options);

    private EfOutboxStore<TestDbContext> NewStore(TestDbContext context) => new(context, _clock);

    private async Task<OutboxMessage> EnqueueAsync(string aggregateId = "order-1")
    {
        await using var context = NewContext();
        var store = NewStore(context);

        var message = new OutboxMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            AggregateId = aggregateId,
            Destination = "event:orders:order.placed:v1",
            Headers = "{}",
            Body = [1, 2, 3],
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
            NextAttemptAt = _clock.GetUtcNow().UtcDateTime,
        };

        await store.EnqueueAsync(message);
        await context.SaveChangesAsync();
        return message;
    }

    // ---- Claiming ------------------------------------------------------------------------------

    [Test]
    public async Task A_pending_message_is_claimed()
    {
        await EnqueueAsync();

        await using var context = NewContext();
        var claimed = await NewStore(context).ClaimPendingAsync(10, TimeSpan.FromMinutes(1));

        Assert.That(claimed, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task A_claimed_message_is_invisible_to_another_relay()
    {
        // The property that lets several relay replicas run without a distributed lock.
        await EnqueueAsync();

        await using var first = NewContext();
        await using var second = NewContext();

        var firstClaim = await NewStore(first).ClaimPendingAsync(10, TimeSpan.FromMinutes(1));
        var secondClaim = await NewStore(second).ClaimPendingAsync(10, TimeSpan.FromMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(firstClaim, Has.Count.EqualTo(1));
            Assert.That(secondClaim, Is.Empty);
        });
    }

    [Test]
    public async Task An_expired_claim_returns_the_message_to_the_pool()
    {
        // A relay that dies mid-dispatch must not strand its rows forever, which is exactly why the
        // claim is a lease rather than a lock.
        await EnqueueAsync();

        await using var first = NewContext();
        await NewStore(first).ClaimPendingAsync(10, TimeSpan.FromMinutes(1));

        _clock.Advance(TimeSpan.FromMinutes(2));

        await using var second = NewContext();
        var reclaimed = await NewStore(second).ClaimPendingAsync(10, TimeSpan.FromMinutes(1));

        Assert.That(reclaimed, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task A_dispatched_message_is_never_claimed_again()
    {
        var message = await EnqueueAsync();

        await using var context = NewContext();
        var store = NewStore(context);

        var claimed = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1));
        await store.MarkDispatchedAsync(claimed[0].Id);

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.That(await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1)), Is.Empty);
    }

    [Test]
    public async Task A_failed_message_is_not_reclaimed_before_its_next_attempt()
    {
        await EnqueueAsync();

        await using var context = NewContext();
        var store = NewStore(context);

        var claimed = await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1));
        await store.MarkFailedAsync(
            claimed[0].Id, "transport-down", _clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That(await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1)), Is.Empty);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.That(await store.ClaimPendingAsync(10, TimeSpan.FromMinutes(1)), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Concurrent_relays_never_claim_the_same_message()
    {
        // The correctness property the whole design rests on: a claimed row is owned by exactly one
        // relay, so a message is published once per dispatch rather than once per replica.
        for (var i = 0; i < 20; i++)
        {
            await EnqueueAsync($"order-{i}");
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = NewContext();
            var claimed = await NewStore(context).ClaimPendingAsync(20, TimeSpan.FromMinutes(1));
            return claimed.Select(m => m.Id).ToList();
        }));

        var all = claims.SelectMany(ids => ids).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(all, Has.Count.EqualTo(20), "Every message should have been claimed once.");
            Assert.That(all.Distinct().Count(), Is.EqualTo(all.Count), "A message was claimed twice.");
        });
    }

    [Test]
    public async Task Messages_are_claimed_in_write_order_within_an_aggregate()
    {
        var first = await EnqueueAsync("order-1");
        var second = await EnqueueAsync("order-1");

        await using var context = NewContext();
        var claimed = await NewStore(context).ClaimPendingAsync(10, TimeSpan.FromMinutes(1));

        Assert.That(
            claimed.Select(m => m.MessageId),
            Is.EqualTo(new[] { first.MessageId, second.MessageId }));
    }

    // ---- Lag and retention ----------------------------------------------------------------------

    [Test]
    public async Task Lag_reports_pending_count_and_oldest_age()
    {
        await EnqueueAsync();
        _clock.Advance(TimeSpan.FromMinutes(3));

        await using var context = NewContext();
        var (pending, oldestAge) = await NewStore(context).GetLagAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.EqualTo(1));
            Assert.That(oldestAge, Is.EqualTo(TimeSpan.FromMinutes(3)));
        });
    }

    [Test]
    public async Task Lag_is_zero_when_nothing_is_pending()
    {
        await using var context = NewContext();
        var (pending, oldestAge) = await NewStore(context).GetLagAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.Zero);
            Assert.That(oldestAge, Is.Null);
        });
    }

    [Test]
    public async Task Purge_removes_only_dispatched_messages_past_retention()
    {
        await EnqueueAsync("dispatched");
        await EnqueueAsync("pending");

        await using var context = NewContext();
        var store = NewStore(context);

        var claimed = await store.ClaimPendingAsync(1, TimeSpan.FromMinutes(1));
        await store.MarkDispatchedAsync(claimed[0].Id);

        _clock.Advance(TimeSpan.FromDays(4));
        var purged = await store.PurgeDispatchedAsync(TimeSpan.FromDays(3));

        Assert.Multiple(async () =>
        {
            Assert.That(purged, Is.EqualTo(1));

            // The undispatched one survives: retention must never discard a message that has not
            // been delivered.
            var (pending, _) = await store.GetLagAsync();
            Assert.That(pending, Is.EqualTo(1));
        });
    }

    // ---- Atomicity -------------------------------------------------------------------------------

    [Test]
    public async Task An_enqueued_message_is_invisible_until_the_transaction_commits()
    {
        // The entire point of the outbox: the row and the state change share a transaction, so a
        // rollback discards both and a commit publishes both.
        await using var writer = NewContext();
        var store = NewStore(writer);

        await using var transaction = await writer.Database.BeginTransactionAsync();

        await store.EnqueueAsync(new OutboxMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            AggregateId = "order-1",
            Destination = "event:orders:order.placed:v1",
            Headers = "{}",
            Body = [1],
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
            NextAttemptAt = _clock.GetUtcNow().UtcDateTime,
        });

        await writer.SaveChangesAsync();
        await transaction.RollbackAsync();

        await using var reader = NewContext();
        var (pending, _) = await NewStore(reader).GetLagAsync();

        Assert.That(pending, Is.Zero, "A rolled-back message must never be dispatched.");
    }
}

/// <summary>The durable inbox, against a real database.</summary>
[TestFixture]
internal sealed class EfInboxStoreTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestDbContext> _options = null!;
    private FakeTimeProvider _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options;
        _clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

        using var context = new TestDbContext(_options);
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private EfInboxStore<TestDbContext> NewStore(TestDbContext context) => new(context, _clock);

    [Test]
    public async Task The_first_delivery_is_admitted_and_the_second_is_not()
    {
        await using var first = new TestDbContext(_options);
        await using var second = new TestDbContext(_options);

        Assert.Multiple(async () =>
        {
            Assert.That(await NewStore(first).TryBeginAsync("group", "msg-1"), Is.True);
            Assert.That(await NewStore(second).TryBeginAsync("group", "msg-1"), Is.False);
        });
    }

    [Test]
    public async Task Deduplication_survives_a_new_context()
    {
        // The whole reason the durable inbox exists: an in-memory one forgets on restart, so a
        // message in flight across one is processed twice.
        await using (var context = new TestDbContext(_options))
        {
            await NewStore(context).TryBeginAsync("group", "msg-1");
        }

        await using var reopened = new TestDbContext(_options);

        Assert.That(await NewStore(reopened).TryBeginAsync("group", "msg-1"), Is.False);
    }

    [Test]
    public async Task Each_consumer_group_handles_the_same_message_once()
    {
        await using var context = new TestDbContext(_options);
        var store = NewStore(context);

        Assert.Multiple(async () =>
        {
            Assert.That(await store.TryBeginAsync("shipping", "msg-1"), Is.True);
            Assert.That(await store.TryBeginAsync("billing", "msg-1"), Is.True);
            Assert.That(await store.TryBeginAsync("shipping", "msg-1"), Is.False);
        });
    }

    [Test]
    public async Task Releasing_lets_a_failed_message_be_retried()
    {
        await using var context = new TestDbContext(_options);
        var store = NewStore(context);

        await store.TryBeginAsync("group", "msg-1");
        await store.ReleaseAsync("group", "msg-1");

        Assert.That(await store.TryBeginAsync("group", "msg-1"), Is.True);
    }

    [Test]
    public async Task Purge_removes_only_entries_past_retention()
    {
        await using var context = new TestDbContext(_options);
        var store = NewStore(context);

        await store.TryBeginAsync("group", "old");
        _clock.Advance(TimeSpan.FromDays(8));
        await store.TryBeginAsync("group", "recent");

        Assert.That(await store.PurgeAsync(TimeSpan.FromDays(7)), Is.EqualTo(1));
    }
}
