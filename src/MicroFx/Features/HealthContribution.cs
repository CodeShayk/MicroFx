using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MicroFx.Features;

/// <summary>Which health probe a contribution participates in.</summary>
[Flags]
public enum HealthProbe
{
    /// <summary>Participates in no probe. Reported at <c>/internal/health</c> only.</summary>
    None = 0,

    /// <summary>
    /// Liveness. Must not check external dependencies — a dependency outage that restarts every
    /// replica turns an outage into an outage plus a restart storm.
    /// </summary>
    Live = 1,

    /// <summary>Readiness. Checks the dependencies required to serve traffic.</summary>
    Ready = 2,

    /// <summary>Startup. One-time preconditions, checked before the first readiness probe.</summary>
    Startup = 4,
}

/// <summary>
/// A health check declared by a feature. Features contribute health without referencing the health
/// feature's types, which is what keeps the built-in dependency graph shallow.
/// </summary>
/// <param name="Name">Unique check name, shown in the health response.</param>
/// <param name="Probes">Which probes this check participates in.</param>
/// <param name="Factory">Creates the check from the resolved service provider.</param>
/// <param name="Timeout">
/// Per-check timeout. A readiness probe that hangs is worse than one that fails, because an
/// orchestrator cannot tell the difference between slow and dead.
/// </param>
public sealed record HealthContribution(
    string Name,
    HealthProbe Probes,
    Func<IServiceProvider, IHealthCheck> Factory,
    TimeSpan? Timeout = null)
{
    /// <summary>Default per-check timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The effective timeout for this check.</summary>
    public TimeSpan EffectiveTimeout => Timeout ?? DefaultTimeout;

    /// <summary>Creates a readiness-only contribution from a delegate.</summary>
    public static HealthContribution Ready(
        string name,
        Func<IServiceProvider, CancellationToken, ValueTask<HealthCheckResult>> check,
        TimeSpan? timeout = null) =>
        new(name, HealthProbe.Ready, sp => new DelegateHealthCheck(sp, check), timeout);

    /// <summary>Creates a liveness-only contribution from a delegate.</summary>
    public static HealthContribution Live(
        string name,
        Func<IServiceProvider, CancellationToken, ValueTask<HealthCheckResult>> check,
        TimeSpan? timeout = null) =>
        new(name, HealthProbe.Live, sp => new DelegateHealthCheck(sp, check), timeout);

    /// <summary>Creates a startup-only contribution from a delegate.</summary>
    public static HealthContribution Startup(
        string name,
        Func<IServiceProvider, CancellationToken, ValueTask<HealthCheckResult>> check,
        TimeSpan? timeout = null) =>
        new(name, HealthProbe.Startup, sp => new DelegateHealthCheck(sp, check), timeout);

    private sealed class DelegateHealthCheck(
        IServiceProvider services,
        Func<IServiceProvider, CancellationToken, ValueTask<HealthCheckResult>> check) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await check(services, cancellationToken).ConfigureAwait(false);
    }
}
