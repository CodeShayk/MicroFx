using System.ComponentModel.DataAnnotations;

namespace MicroFx.Hosting;

/// <summary>Host-level MicroFx options, bound from <c>MicroFx:Host</c>.</summary>
public sealed class MicroFxHostOptions
{
    /// <summary>Port serving application traffic.</summary>
    [Range(1, 65535)]
    public int TrafficPort { get; set; } = 8080;

    /// <summary>
    /// Port serving health and diagnostic endpoints. Must never be internet-exposed: bind it to the
    /// pod or task network only, and do not add it to a load-balancer target group.
    /// </summary>
    [Range(1, 65535)]
    public int ManagementPort { get; set; } = 8081;

    /// <summary>
    /// Whether MicroFx configures Kestrel's listeners. Set false to keep control of binding, in
    /// which case management endpoints are served on whatever ports the host already listens on and
    /// port isolation no longer applies.
    /// </summary>
    public bool ConfigureListeners { get; set; } = true;

    /// <summary>
    /// Budget for a single feature's lifecycle phase. A feature exceeding it fails startup naming
    /// itself, rather than hanging a deployment with no attribution.
    /// </summary>
    public TimeSpan FeatureLifecycleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Drain window for in-flight work on shutdown. Must be shorter than the orchestrator's
    /// termination grace period, or the process is killed mid-drain.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether to log the resolved feature set at startup.</summary>
    public bool LogStartupBanner { get; set; } = true;
}
