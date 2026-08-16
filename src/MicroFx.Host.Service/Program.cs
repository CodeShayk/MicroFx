using System.Globalization;
using MicroFx.Host.Service.Features;
using MicroFx.Host.Service.Orders;
using MicroFx.Features;
using MicroFx.Hosting;
using MicroFx.Jobs;
using MicroFx.Messaging;
using MicroFx.Persistence;
using Microsoft.EntityFrameworkCore;

// Container health probe. The chiselled base image has no shell and no curl, so the container
// probes itself by re-executing its own binary in a mode that exits 0 or 1. Handled before the
// host is built so the probe costs a process start rather than a full composition.
if (args.Contains("--health-check", StringComparer.Ordinal))
{
    return await HealthProbe.RunAsync().ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

// One call composes the service: configuration, observability, health, diagnostics, and the
// feature graph. Everything below the AddMicroFx line is this service's own concern.
builder.AddMicroFx(fx =>
{
    fx.AddFeature<ExampleCustomFeature>();

    // Messaging is opt-in: a service with no messaging carries none of it.
    fx.Enable(BuiltIn.Messaging);

    // Persistence, jobs, and flags are all opt-in.
    fx.Enable(BuiltIn.Persistence);
    fx.Enable(BuiltIn.Jobs);
    fx.Enable(BuiltIn.FeatureFlags);

    fx.Configure<PersistenceFeature>(persistence => persistence.Configure(p => p
        .UseDbContext<OrdersDbContext>(db => db.UseSqlite(
            builder.Configuration.GetConnectionString("Orders")
            ?? "Data Source=microfx-host.db"))
        .UseOutbox()
        .UseInbox()));

    fx.Configure<JobsFeature>(jobs => jobs.Configure(j => j
        .AddIntervalJob<OrderSweepJob>(
            "order-sweep",
            TimeSpan.FromSeconds(5),
            job => job
                .AsSingleton()
                .WithTimeout(TimeSpan.FromSeconds(10))
                .WithLease(TimeSpan.FromMinutes(1)))));

    fx.Configure<MessagingFeature>(messaging => messaging.Configure(m =>
    {
        m.PublishesEvent<OrderPlacedV1>();
        m.HandlesCommand<ReserveInventory, ReserveInventoryHandler>();

        // The service subscribes to its own event, which round-trips the entire path —
        // publish, envelope, transport, pipeline, dedupe, handler — with no infrastructure.
        m.SubscribesToEvent<OrderPlacedV1, OrderPlacedProjectionHandler>(
            configure: s => s.WithConcurrency(2).WithPrefetch(16));
    }));
});

builder.Services.AddSingleton<OrderProjection>();
builder.Services.AddSingleton<OrderSweepReport>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "MicroFx.Host.Service" }));

await app.RunMicroFxAsync().ConfigureAwait(false);
return 0;

/// <summary>Self-probe used by the container healthcheck.</summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("MICROFX_Host__ManagementPort") ?? "8081";
        var url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/health/ready");

        // Bounded so a wedged service fails the probe rather than hanging the healthcheck.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            using var response = await client.GetAsync(new Uri(url)).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}

/// <summary>
/// Entry point marker so the end-to-end suite can host this service in-process through
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
