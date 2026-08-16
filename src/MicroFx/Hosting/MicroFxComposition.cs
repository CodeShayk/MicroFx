using MicroFx.Core;
using MicroFx.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace MicroFx.Hosting;

/// <summary>
/// The composition result, produced during <c>AddMicroFx</c> and consumed by the later passes.
/// Registered as a singleton so the pipeline, endpoint, and lifecycle passes all read the same
/// resolved graph — the reason the run phase cannot drift from the build phase.
/// </summary>
internal sealed class MicroFxComposition
{
    public required FeatureCatalog Catalog { get; init; }
    public required FeatureCompositionState State { get; init; }
    public required ServiceMetadata Metadata { get; init; }
    public required MicroFxHostOptions Options { get; init; }

    /// <summary>Set once the pipeline pass has run, so a missing <c>RunMicroFxAsync</c> is detectable.</summary>
    public bool PipelineApplied { get; set; }

    public IEnumerable<FeatureCatalogEntry> EnabledFor(HostKinds hostKind) =>
        Catalog.Enabled.Where(e => (e.Descriptor.SupportedHosts & hostKind) != 0);
}

/// <summary>Reads feature enablement overrides out of configuration.</summary>
internal static class FeatureConfigurationReader
{
    public const string FeaturesSection = "MicroFx:Features";

    /// <summary>
    /// Returns ids disabled by configuration, mapped to the configuration path responsible — so the
    /// catalog can report not just that a feature is off but exactly which key turned it off.
    /// </summary>
    public static Dictionary<string, string> ReadDisabled(IConfiguration configuration)
    {
        var disabled = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var child in configuration.GetSection(FeaturesSection).GetChildren())
        {
            var value = child["Enabled"];
            if (value is not null && bool.TryParse(value, out var enabled) && !enabled)
            {
                disabled[child.Key] = $"{FeaturesSection}:{child.Key}:Enabled";
            }
        }

        return disabled;
    }

    /// <summary>Returns ids explicitly enabled by configuration.</summary>
    public static HashSet<string> ReadEnabled(IConfiguration configuration)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in configuration.GetSection(FeaturesSection).GetChildren())
        {
            var value = child["Enabled"];
            if (value is not null && bool.TryParse(value, out var isEnabled) && isEnabled)
            {
                enabled.Add(child.Key);
            }
        }

        return enabled;
    }
}

/// <summary>Determines what kind of host is being composed.</summary>
internal static class HostKindDetector
{
    public static HostKinds Detect(object builder) =>
        builder is WebApplicationBuilder ? HostKinds.Web : HostKinds.Worker;
}
