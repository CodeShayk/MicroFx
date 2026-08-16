using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MicroFx.Host.Service.Orders;
using MicroFx.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MicroFx.Host.Service.E2E.Tests;

/// <summary>
/// End-to-end persistence: the transactional outbox, its relay, the durable inbox, and the job
/// scheduler, against a real SQLite database.
/// </summary>
/// <remarks>
/// A file-backed database rather than in-memory, because the headline test restarts the host and
/// asserts the outbox survived — which is meaningless if the store dies with the process.
/// </remarks>
[TestFixture]
internal sealed class PersistenceEndToEndTests
{
    private string _databasePath = null!;

    [SetUp]
    public void SetUp() =>
        _databasePath = Path.Combine(
            Path.GetTempPath(), $"microfx-e2e-{Guid.NewGuid():N}.db");

    [TearDown]
    public void TearDown()
    {
        foreach (var path in Directory.GetFiles(
                     Path.GetDirectoryName(_databasePath)!,
                     Path.GetFileName(_databasePath) + "*"))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // The file may still be held briefly after the host stops; a stale temp file is
                // not worth failing a test over.
            }
        }
    }

    private HostServiceFactory CreateHost(bool relayEnabled = true) =>
        new(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Orders"] = $"Data Source={_databasePath}",
            ["MicroFx:Persistence:CreateSchemaOnStartup"] = "true",

            // A long poll interval parks the relay so a message can be written and the host stopped
            // before anything dispatches it — which is how the crash window is created
            // deterministically rather than by racing a timer.
            ["MicroFx:Persistence:OutboxPollInterval"] = relayEnabled ? "00:00:00.250" : "00:10:00",

            // Raised alongside the parked poll interval. Startup validation refuses a lease no
            // longer than the poll, because that combination lets two relays claim one row.
            ["MicroFx:Persistence:OutboxLeaseDuration"] = relayEnabled ? "00:01:00" : "00:20:00",
            ["MicroFx:Jobs:TickInterval"] = "00:00:00.200",
        });

    /// <summary>
    /// Polls until a condition holds or the budget expires.
    /// </summary>
    /// <remarks>
    /// The budget is deliberately generous. These tests assert that work <em>survives</em>, not that
    /// it is fast, and a successful run returns the moment the condition holds — so a long deadline
    /// costs nothing while a tight one turns a loaded CI agent into a flaky failure.
    /// </remarks>
    private static async Task<T> EventuallyAsync<T>(
        Func<Task<T>> probe, Func<T, bool> satisfied, string because, int budgetMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
        T last = default!;

        while (DateTime.UtcNow < deadline)
        {
            last = await probe();
            if (satisfied(last))
            {
                return last;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"{because} Last observed: {last}.");
        return last;
    }

    private static StringContent Order(string sku = "DUR-1") =>
        new($$"""{"sku":"{{sku}}","quantity":2,"currency":"GBP"}""", Encoding.UTF8, "application/json");

    // ---- Atomicity -------------------------------------------------------------------------------

    [Test]
    public async Task An_order_and_its_event_commit_together()
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(new Uri("/v1/orders/durable", UriKind.Relative), Order());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = created.GetProperty("id").GetString()!;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        // The row exists and the event reached the subscriber, which together is the outbox
        // contract: state change and publication are inseparable.
        var stored = await client.GetAsync(new Uri($"/v1/orders/durable/{orderId}", UriKind.Relative));
        Assert.That(stored.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await EventuallyAsync(
            async () =>
            {
                var projection = await client.GetFromJsonAsync<JsonElement>(
                    new Uri($"/v1/orders/{orderId}/projection", UriKind.Relative));
                return projection.GetProperty("handled").GetInt32();
            },
            handled => handled == 1,
            "The outbox relay never delivered the event to its subscriber.");
    }

    [Test]
    public async Task Audit_columns_are_filled_without_the_service_asking()
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(new Uri("/v1/orders/durable", UriKind.Relative), Order());
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = created.GetProperty("id").GetString()!;

        var stored = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/v1/orders/durable/{orderId}", UriKind.Relative));

        // An audit column filled only where someone remembered is worse than none at all, so the
        // interceptor fills it and the endpoint says nothing about it.
        Assert.That(stored.GetProperty("createdAt").GetDateTimeOffset(), Is.Not.EqualTo(default(DateTimeOffset)));
    }

    // ---- Crash recovery — the test that cannot be faked --------------------------------------------

    [Test]
    public async Task An_event_survives_a_host_restart_between_commit_and_publish()
    {
        // The one guarantee the outbox exists to provide, and the one that quietly regresses.
        // The relay is parked so the message is committed but undispatched, the host is destroyed,
        // and a new host must find the row and deliver it.
        string orderId;

        using (var crashed = CreateHost(relayEnabled: false))
        {
            using var client = crashed.CreateClient();

            var response = await client.PostAsync(
                new Uri("/v1/orders/durable", UriKind.Relative), Order("CRASH-1"));

            var created = await response.Content.ReadFromJsonAsync<JsonElement>();
            orderId = created.GetProperty("id").GetString()!;

            // Committed but not dispatched: exactly the window a crash would land in.
            await using var scope = crashed.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var (pending, _) = await store.GetLagAsync();

            Assert.That(pending, Is.EqualTo(1), "The event should be pending, not yet dispatched.");
        }

        // The host is gone. Everything in memory went with it; only the database remains.
        using var recovered = CreateHost();
        using var recoveredClient = recovered.CreateClient();

        await EventuallyAsync(
            async () =>
            {
                var projection = await recoveredClient.GetFromJsonAsync<JsonElement>(
                    new Uri($"/v1/orders/{orderId}/projection", UriKind.Relative));
                return projection.GetProperty("handled").GetInt32();
            },
            handled => handled == 1,
            "The event did not survive the restart. This is the guarantee the outbox exists for.");

        await using var scope2 = recovered.Services.CreateAsyncScope();
        var recoveredStore = scope2.ServiceProvider.GetRequiredService<IOutboxStore>();
        var (remaining, _) = await recoveredStore.GetLagAsync();

        Assert.That(remaining, Is.Zero, "The recovered message should have been marked dispatched.");
    }

    [Test]
    public async Task The_order_written_before_the_restart_is_still_there()
    {
        // The state half of the same guarantee: the outbox is only meaningful if the state change
        // it accompanied also survived.
        string orderId;

        using (var crashed = CreateHost(relayEnabled: false))
        {
            using var client = crashed.CreateClient();
            var response = await client.PostAsync(
                new Uri("/v1/orders/durable", UriKind.Relative), Order("CRASH-2"));

            var created = await response.Content.ReadFromJsonAsync<JsonElement>();
            orderId = created.GetProperty("id").GetString()!;
        }

        using var recovered = CreateHost();
        using var recoveredClient = recovered.CreateClient();

        var stored = await recoveredClient.GetAsync(
            new Uri($"/v1/orders/durable/{orderId}", UriKind.Relative));

        Assert.That(stored.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---- Durable dedupe ----------------------------------------------------------------------------

    [Test]
    public async Task Deduplication_survives_a_restart()
    {
        // An in-memory inbox forgets on restart, so a message in flight across one is handled
        // twice. A durable one is what makes at-least-once delivery effectively-once handling.
        const string ConsumerGroup = "restart-test";
        var messageId = Guid.NewGuid().ToString("N");

        using (var first = CreateHost())
        {
            await using var scope = first.Services.CreateAsyncScope();
            var inbox = scope.ServiceProvider.GetRequiredService<MicroFx.Messaging.IInboxStore>();

            Assert.That(await inbox.TryBeginAsync(ConsumerGroup, messageId), Is.True);
        }

        using var second = CreateHost();
        await using var secondScope = second.Services.CreateAsyncScope();
        var reopenedInbox = secondScope.ServiceProvider.GetRequiredService<MicroFx.Messaging.IInboxStore>();

        Assert.That(
            await reopenedInbox.TryBeginAsync(ConsumerGroup, messageId), Is.False,
            "The inbox forgot a processed message across a restart.");
    }

    // ---- Transactions -------------------------------------------------------------------------------

    [Test]
    public async Task A_nested_scope_commits_once_with_the_outermost()
    {
        using var factory = CreateHost();
        await using var scope = factory.Services.CreateAsyncScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var database = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var orderId = Guid.NewGuid().ToString("N")[..12];

        await unitOfWork.ExecuteAsync(async token =>
        {
            database.Orders.Add(new OrderEntity { Id = orderId, Sku = "NEST-1", Quantity = 1 });

            // An inner scope joins rather than nesting a second transaction, and its commit is a
            // no-op: the outermost scope decides for everyone.
            await using var inner = await unitOfWork.BeginAsync(token);
            Assert.That(inner.IsAmbient, Is.True);
            await inner.CommitAsync(token);

            await unitOfWork.SaveChangesAsync(token);
        });

        Assert.That(await database.Orders.AnyAsync(o => o.Id == orderId), Is.True);
    }

    [Test]
    public async Task An_inner_rollback_discards_the_whole_transaction()
    {
        // Without this, a shared application service called from inside a handler would commit
        // half the handler's work.
        using var factory = CreateHost();
        await using var scope = factory.Services.CreateAsyncScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var database = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var orderId = Guid.NewGuid().ToString("N")[..12];

        Assert.ThrowsAsync<TransactionRolledBackException>(async () =>
            await unitOfWork.ExecuteAsync(async token =>
            {
                database.Orders.Add(new OrderEntity { Id = orderId, Sku = "ROLL-1", Quantity = 1 });
                await unitOfWork.SaveChangesAsync(token);

                await using var inner = await unitOfWork.BeginAsync(token);
                await inner.RollbackAsync(token);
            }));

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        Assert.That(await verify.Orders.AnyAsync(o => o.Id == orderId), Is.False);
    }

    // ---- Jobs -----------------------------------------------------------------------------------------

    [Test]
    public async Task A_scheduled_job_runs_and_reports_freshness()
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();

        var runs = await EventuallyAsync(
            async () =>
            {
                var report = await client.GetFromJsonAsync<JsonElement>(
                    new Uri("/v1/orders/sweep", UriKind.Relative));
                return report.GetProperty("runs").GetInt64();
            },
            count => count > 0,
            "The scheduled job never ran.");

        Assert.That(runs, Is.GreaterThan(0));
    }

    [Test]
    public async Task The_feature_catalog_reports_persistence_jobs_and_flags()
    {
        using var factory = CreateHost();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/internal/features", UriKind.Relative));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var features = document.RootElement.GetProperty("features").EnumerateArray().ToList();

        JsonElement Feature(string id) =>
            features.First(f => f.GetProperty("id").GetString() == id);

        Assert.Multiple(() =>
        {
            Assert.That(Feature("microfx.persistence").GetProperty("facts")
                .GetProperty("store").GetString(), Is.EqualTo("ef-core"));
            Assert.That(Feature("microfx.persistence").GetProperty("facts")
                .GetProperty("outbox").GetBoolean(), Is.True);
            Assert.That(Feature("microfx.jobs").GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(Feature("microfx.featureflags").GetProperty("enabled").GetBoolean(), Is.True);
        });
    }
}
