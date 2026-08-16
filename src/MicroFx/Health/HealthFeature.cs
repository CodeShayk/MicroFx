using System.Text.Json;
using MicroFx.Features;
using MicroFx.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MicroFx.Health;

/// <summary>Options for the health feature.</summary>
public sealed class HealthOptions
{
    /// <summary>Path prefix for the probes. Served on the management port only.</summary>
    public string PathPrefix { get; set; } = "/health";

    /// <summary>
    /// Whether a failing check's exception detail is included in the response. Off by default, and
    /// off in production regardless: a probe response is a diagnostic aid, not a place to publish
    /// stack traces or connection strings that arrive in exception messages.
    /// </summary>
    public bool IncludeExceptionDetail { get; set; }
}

/// <summary>
/// Liveness, readiness, and startup probes, auto-registered from feature contributions.
/// </summary>
/// <remarks>
/// <para>
/// Liveness deliberately checks nothing external. A dependency outage that fails liveness restarts
/// every replica, turning one outage into an outage plus a restart storm — and the restarts do not
/// help, because the dependency is still down.
/// </para>
/// <para>
/// Probes are mapped onto the management route builder, so they are unreachable from the traffic
/// port even if the service is internet-facing.
/// </para>
/// </remarks>
public sealed class HealthFeature : IMicroFxFeature, IEndpointFeature
{
    /// <summary>Tag marking a check as participating in the liveness probe.</summary>
    public const string LiveTag = "live";

    /// <summary>Tag marking a check as participating in the readiness probe.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Tag marking a check as participating in the startup probe.</summary>
    public const string StartupTag = "startup";

    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Health,
        DisplayName = "Health",
        IsKernel = true,
        Order = 30,
        DependsOn = [BuiltIn.Core],
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Health",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<HealthOptions>();

        var checks = context.Services.AddHealthChecks();

        // Liveness has exactly one check: the process is responding. Anything else belongs to
        // readiness, and conflating them is the most common cause of restart storms.
        checks.AddCheck("self", () => HealthCheckResult.Healthy("Process is responding."), [LiveTag]);

        // Contributions arrive from features later in the graph, so registration is deferred until
        // every feature has had its build pass.
        context.Services.AddOptions<HealthCheckServiceOptions>()
            .Configure<MicroFxComposition, IServiceProvider>((options, composition, provider) =>
            {
                foreach (var contribution in composition.State.HealthContributions)
                {
                    options.Registrations.Add(new HealthCheckRegistration(
                        contribution.Name,
                        _ => contribution.Factory(provider),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: TagsFor(contribution.Probes),
                        timeout: contribution.EffectiveTimeout));
                }
            });

        context.Report("probes", "live,ready,startup");
    }

    /// <inheritdoc />
    public void MapEndpoints(FeatureEndpointContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Management.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthOptions>>().Value;

        var includeDetail = options.IncludeExceptionDetail && context.Metadata.IsDevelopment;
        var prefix = options.PathPrefix.TrimEnd('/');

        Map(context.Management, $"{prefix}/live", LiveTag, includeDetail);
        Map(context.Management, $"{prefix}/ready", ReadyTag, includeDetail);
        Map(context.Management, $"{prefix}/startup", StartupTag, includeDetail);
    }

    private static void Map(
        Microsoft.AspNetCore.Routing.IEndpointRouteBuilder routes,
        string path,
        string tag,
        bool includeDetail) =>
        routes.MapHealthChecks(path, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(tag),
            AllowCachingResponses = false,
            ResponseWriter = (httpContext, report) => WriteAsync(httpContext, report, includeDetail),
        })
        // Probes must never require a credential. Under deny-by-default authorization the fallback
        // policy would otherwise challenge them, an orchestrator would read 401 as unhealthy, and
        // every replica would be killed the moment security was switched on. The control protecting
        // these endpoints is management-port isolation, not authentication.
        .AllowAnonymous();

    private static IEnumerable<string> TagsFor(HealthProbe probes)
    {
        if (probes.HasFlag(HealthProbe.Live))
        {
            yield return LiveTag;
        }

        if (probes.HasFlag(HealthProbe.Ready))
        {
            yield return ReadyTag;
        }

        if (probes.HasFlag(HealthProbe.Startup))
        {
            yield return StartupTag;
        }
    }

    private static async Task WriteAsync(HttpContext httpContext, HealthReport report, bool includeDetail)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        // A probe response must never be cached: a stale "healthy" is worse than no answer.
        httpContext.Response.Headers.CacheControl = "no-store, no-cache";

        using var buffer = new MemoryStream();
        await using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("status", report.Status.ToString());
        writer.WriteNumber("durationMs", report.TotalDuration.TotalMilliseconds);
        writer.WriteStartArray("checks");

        foreach (var (name, entry) in report.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("status", entry.Status.ToString());
            writer.WriteNumber("durationMs", entry.Duration.TotalMilliseconds);

            if (entry.Description is { } description)
            {
                writer.WriteString("description", description);
            }

            // Exception text routinely carries connection strings and host names. It is withheld
            // unless a developer has explicitly asked for it in a Development environment.
            if (includeDetail && entry.Exception is { } exception)
            {
                writer.WriteString("error", exception.Message);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync().ConfigureAwait(false);

        await httpContext.Response.Body.WriteAsync(buffer.ToArray()).ConfigureAwait(false);
    }
}
