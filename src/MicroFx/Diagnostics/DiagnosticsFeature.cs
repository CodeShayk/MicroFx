using MicroFx.Configuration;
using MicroFx.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MicroFx.Diagnostics;

/// <summary>Options for the diagnostics feature, bound from <c>MicroFx:Diagnostics</c>.</summary>
public sealed class DiagnosticsOptions
{
    /// <summary>Path prefix for the internal endpoints. Served on the management port only.</summary>
    public string PathPrefix { get; set; } = "/internal";

    /// <summary>
    /// Whether <c>/internal/config</c> is served. Off outside Development regardless of this
    /// setting unless <see cref="AllowConfigurationOutsideDevelopment"/> is also set.
    /// </summary>
    public bool ExposeConfiguration { get; set; } = true;

    /// <summary>
    /// Opt-in required to serve the effective configuration outside Development.
    /// </summary>
    /// <remarks>
    /// Two flags rather than one, deliberately. Values are redacted, but redaction is a heuristic
    /// and the key names themselves reveal topology. Turning this on in production should be a
    /// separate, conscious act rather than something inherited from a default.
    /// </remarks>
    public bool AllowConfigurationOutsideDevelopment { get; set; }
}

/// <summary>
/// Internal endpoints describing the running service: build info, the resolved feature graph, and
/// the effective configuration.
/// </summary>
/// <remarks>
/// The feature graph is operational data. "Why isn't caching on?" should be a lookup, not an
/// investigation — which is what <c>/internal/features</c> exists to make true.
/// </remarks>
public sealed class DiagnosticsFeature : IMicroFxFeature, IEndpointFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Diagnostics,
        DisplayName = "Diagnostics",
        IsKernel = true,
        Order = 40,
        DependsOn = [BuiltIn.Core, BuiltIn.Configuration],
        SupportedHosts = HostKinds.Web,
        ConfigurationSection = "MicroFx:Diagnostics",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AddValidatedOptions<DiagnosticsOptions>();
    }

    /// <inheritdoc />
    public void MapEndpoints(FeatureEndpointContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Management.ServiceProvider;
        var options = services.GetRequiredService<IOptions<DiagnosticsOptions>>().Value;
        var metadata = context.Metadata;
        var prefix = options.PathPrefix.TrimEnd('/');

        context.Management.MapGet($"{prefix}/info", () => Results.Ok(new
        {
            service = metadata.Name,
            version = metadata.Version,
            commit = metadata.Commit,
            environment = metadata.Environment,
            role = metadata.Role,
            instance = metadata.InstanceId,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            startedAt = ProcessStart,
        })).WithName("microfx-info");

        context.Management.MapGet($"{prefix}/features", () => Results.Ok(new
        {
            total = context.Catalog.All.Count,
            enabled = context.Catalog.Enabled.Count,
            features = context.Catalog.All
                .OrderBy(e => e.ResolvedOrder)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Select(entry => new
                {
                    id = entry.Id,
                    name = entry.Descriptor.Name,
                    enabled = entry.IsEnabled,
                    kernel = entry.Descriptor.IsKernel,
                    order = entry.ResolvedOrder == int.MaxValue ? null : (int?)entry.ResolvedOrder,
                    reason = entry.Reason == DisabledReason.None ? null : entry.Reason.ToString(),
                    reasonDetail = entry.ReasonDetail,
                    replaces = entry.Replaces,
                    assembly = entry.AssemblyName,
                    dependsOn = entry.Descriptor.DependsOn,
                    after = entry.Descriptor.After,
                    before = entry.Descriptor.Before,
                    facts = entry.Facts,
                    timings = entry.Timings.ToDictionary(t => t.Key, t => t.Value.TotalMilliseconds),
                }),
        })).WithName("microfx-features");

        var exposeConfiguration = options.ExposeConfiguration &&
                                  (metadata.IsDevelopment || options.AllowConfigurationOutsideDevelopment);

        if (!exposeConfiguration)
        {
            return;
        }

        context.Management.MapGet($"{prefix}/config", (ConfigurationProvenance provenance) =>
            Results.Ok(new
            {
                note = "Values matching secret heuristics are redacted. Redaction is defence in " +
                       "depth, not a substitute for a secret store.",
                entries = provenance.Snapshot()
                    .Select(e => new { key = e.Key, value = e.Value, provider = e.Provider }),
            })).WithName("microfx-config");
    }

    private static readonly DateTimeOffset ProcessStart =
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
}
