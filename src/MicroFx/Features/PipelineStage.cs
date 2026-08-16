namespace MicroFx.Features;

/// <summary>
/// Ordered stages of the HTTP pipeline. A feature declares a stage; the kernel emits stages in this
/// order and, within a stage, by the feature's position in the resolved graph.
/// </summary>
/// <remarks>
/// Two positions are deliberate and load-bearing. <see cref="RateLimiting"/> precedes
/// <see cref="Authentication"/> so an unauthenticated flood costs a dictionary lookup rather than a
/// signature validation. <see cref="Tenancy"/> follows <see cref="Authentication"/> because tenant
/// identity must come from a verified claim, never an unverified header.
/// </remarks>
public enum PipelineStage
{
    /// <summary>Exception handling. Outermost: nothing above it may throw unhandled.</summary>
    Exception = 100,

    /// <summary>Correlation id, <see cref="System.Diagnostics.Activity"/>, and log scope.</summary>
    Diagnostics = 200,

    /// <summary>Forwarded headers — the real client IP, before anything decides based on it.</summary>
    ForwardedHeaders = 300,

    /// <summary>Security response headers: HSTS, CSP, and friends.</summary>
    SecurityHeaders = 400,

    /// <summary>Management and diagnostic endpoints, which short-circuit here.</summary>
    Management = 500,

    /// <summary>Request timeout.</summary>
    Timeout = 600,

    /// <summary>Rate limiting. Deliberately before authentication.</summary>
    RateLimiting = 700,

    /// <summary>Authentication.</summary>
    Authentication = 800,

    /// <summary>Tenant resolution. Requires an authenticated principal.</summary>
    Tenancy = 900,

    /// <summary>Authorization.</summary>
    Authorization = 1000,

    /// <summary>Request logging and metrics, recording the authenticated and tenanted request.</summary>
    Telemetry = 1100,

    /// <summary>Idempotency and request buffering, immediately before endpoint execution.</summary>
    PreEndpoint = 1200,

    /// <summary>Endpoint execution.</summary>
    Endpoint = 1300,
}
