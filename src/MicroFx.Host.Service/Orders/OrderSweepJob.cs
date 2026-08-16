using MicroFx.FeatureFlags;
using MicroFx.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MicroFx.Host.Service.Orders;

/// <summary>
/// A scheduled job that counts orders, gated by a feature flag.
/// </summary>
/// <remarks>
/// Exercises the whole jobs path: interval scheduling, singleton leasing, a scoped
/// <see cref="OrdersDbContext"/>, and a flag-driven kill switch. Idempotent by construction — it
/// only reads — which is what a job must be, because a lease can expire mid-run and another replica
/// can start the same work.
/// </remarks>
public sealed partial class OrderSweepJob(
    OrdersDbContext database,
    IFeatureFlags flags,
    OrderSweepReport report,
    ILogger<OrderSweepJob> logger) : IJob
{
    /// <inheritdoc />
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        // A kill switch that works during an incident rather than after one: flipping the flag in
        // configuration stops the job without a redeploy.
        if (!await flags.IsEnabledAsync("order-sweep", defaultValue: true, cancellationToken)
                .ConfigureAwait(false))
        {
            LogDisabled(logger);
            return;
        }

        var count = await database.Orders.CountAsync(cancellationToken).ConfigureAwait(false);

        report.Record(count, context.StartedAt);
        LogSwept(logger, count, context.IsSingleton);
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Information,
        Message = "Order sweep counted {OrderCount} orders (singleton: {IsSingleton}).")]
    private static partial void LogSwept(ILogger logger, int orderCount, bool isSingleton);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Information,
        Message = "Order sweep is disabled by feature flag.")]
    private static partial void LogDisabled(ILogger logger);
}

/// <summary>Records the last sweep, so the end-to-end suite can observe that the job ran.</summary>
public sealed class OrderSweepReport
{
    private long _runs;

    /// <summary>Orders counted by the last run.</summary>
    public int LastCount { get; private set; }

    /// <summary>When the last run started.</summary>
    public DateTimeOffset? LastRunAt { get; private set; }

    /// <summary>How many times the job has run.</summary>
    public long Runs => Interlocked.Read(ref _runs);

    /// <summary>Records a completed run.</summary>
    public void Record(int count, DateTimeOffset startedAt)
    {
        LastCount = count;
        LastRunAt = startedAt;
        Interlocked.Increment(ref _runs);
    }
}
