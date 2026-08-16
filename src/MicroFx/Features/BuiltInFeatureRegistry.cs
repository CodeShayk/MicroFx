using MicroFx.Api;
using MicroFx.Caching;
using MicroFx.Configuration;
using MicroFx.Core;
using MicroFx.Diagnostics;
using MicroFx.FeatureFlags;
using MicroFx.Jobs;
using MicroFx.Health;
using MicroFx.Messaging;
using MicroFx.Idempotency;
using MicroFx.MultiTenancy;
using MicroFx.Observability;
using MicroFx.Persistence;
using MicroFx.RateLimiting;
using MicroFx.Resilience;
using MicroFx.Security;
using MicroFx.ServiceClients;
using MicroFx.Storage;
using MicroFx.Validation;

namespace MicroFx.Features;

/// <summary>
/// The built-in feature set, listed explicitly rather than discovered.
/// </summary>
/// <remarks>
/// A static list is deterministic, reflection-free, and trim- and AOT-safe. It is also auditable:
/// what the platform ships is a list you can read, not the outcome of a scan.
/// </remarks>
internal static class BuiltInFeatureRegistry
{
    public static IReadOnlyList<IMicroFxFeature> Create() =>
    [
        // Kernel — cannot be disabled.
        new CoreFeature(),
        new ConfigurationFeature(),
        new ObservabilityFeature(),
        new HealthFeature(),
        new DiagnosticsFeature(),

        // Cross-cutting.
        new SecurityFeature(),
        new MultiTenancyFeature(),
        new ResilienceFeature(),
        new CachingFeature(),
        new StorageFeature(),
        new ServiceClientsFeature(),
        new PersistenceFeature(),
        new JobsFeature(),
        new FeatureFlagsFeature(),
        new MessagingFeature(),

        // HTTP.
        new ApiFeature(),
        new ValidationFeature(),
        new RateLimitingFeature(),
        new IdempotencyFeature(),
    ];
}
