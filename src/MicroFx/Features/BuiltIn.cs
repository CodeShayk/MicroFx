namespace MicroFx.Features;

/// <summary>
/// Ids of the built-in features. Use these constants rather than string literals so a rename is a
/// compile error rather than a silently-ignored ordering edge.
/// </summary>
public static class BuiltIn
{
    // ---- Kernel: cannot be disabled ----------------------------------------------------------

    /// <summary>Service metadata, <see cref="TimeProvider"/>, serialization, id generation.</summary>
    public const string Core = "microfx.core";

    /// <summary>Layered configuration, validated options, secret sources.</summary>
    public const string Configuration = "microfx.configuration";

    /// <summary>OpenTelemetry logs, traces, and metrics.</summary>
    public const string Observability = "microfx.observability";

    /// <summary>Liveness, readiness, and startup probes on the management port.</summary>
    public const string Health = "microfx.health";

    /// <summary>Internal diagnostics endpoints: info, features, configuration.</summary>
    public const string Diagnostics = "microfx.diagnostics";

    // ---- HTTP ---------------------------------------------------------------------------------

    /// <summary>Endpoint modules, versioning, problem details, OpenAPI.</summary>
    public const string Api = "microfx.api";

    /// <summary>Validation shared by the HTTP and messaging entry points.</summary>
    public const string Validation = "microfx.validation";

    /// <summary>Partitioned rate limiting.</summary>
    public const string RateLimiting = "microfx.ratelimiting";

    /// <summary>Idempotent replay of unsafe verbs.</summary>
    public const string Idempotency = "microfx.idempotency";

    // ---- Cross-cutting ------------------------------------------------------------------------

    /// <summary>Authentication, authorization, audit, field encryption.</summary>
    public const string Security = "microfx.security";

    /// <summary>Tenant resolution, scoping, and the cross-tenant write guard.</summary>
    public const string MultiTenancy = "microfx.multitenancy";

    /// <summary>Resilience pipelines for outbound calls.</summary>
    public const string Resilience = "microfx.resilience";

    /// <summary>In-memory L1 cache, with an optional distributed L2.</summary>
    public const string Caching = "microfx.caching";

    /// <summary>EF Core persistence, transactions, outbox, and inbox.</summary>
    public const string Persistence = "microfx.persistence";

    /// <summary>Transport-neutral commands, events, and request/reply.</summary>
    public const string Messaging = "microfx.messaging";

    /// <summary>Background work, scheduling, distributed locking, leader election.</summary>
    public const string Jobs = "microfx.jobs";

    /// <summary>Feature flags.</summary>
    public const string FeatureFlags = "microfx.featureflags";

    /// <summary>Object storage.</summary>
    public const string Storage = "microfx.storage";

    /// <summary>Typed HTTP clients for service-to-service calls.</summary>
    public const string ServiceClients = "microfx.serviceclients";
}
