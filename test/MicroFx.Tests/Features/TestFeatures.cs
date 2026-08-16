using MicroFx.Features;

namespace MicroFx.Tests.Features;

/// <summary>
/// A configurable synthetic feature. The resolver's correctness is almost entirely about edge
/// cases, so tests need to construct arbitrary graph shapes cheaply.
/// </summary>
internal sealed class TestFeature(FeatureDescriptor descriptor) : IMicroFxFeature
{
    public FeatureDescriptor Descriptor { get; } = descriptor;

    public List<string> ConfigureCalls { get; } = [];

    public void Configure(FeatureBuildContext context) => ConfigureCalls.Add(Descriptor.Id);

    public static TestFeature Create(
        string id,
        string[]? dependsOn = null,
        string[]? after = null,
        string[]? before = null,
        string? replaces = null,
        int order = FeatureDescriptor.DefaultOrder,
        bool isKernel = false,
        bool enabledByDefault = true) =>
        new(new FeatureDescriptor
        {
            Id = id,
            DependsOn = dependsOn ?? [],
            After = after ?? [],
            Before = before ?? [],
            Replaces = replaces,
            Order = order,
            IsKernel = isKernel,
            EnabledByDefault = enabledByDefault,
        });
}

/// <summary>Builds resolver requests without repeating the boilerplate in every test.</summary>
internal static class Resolve
{
    public const string PlatformAssembly = "MicroFx.Tests";

    public static FeatureCatalog Graph(
        IEnumerable<IMicroFxFeature> features,
        string[]? disabledByCode = null,
        string[]? enabledByCode = null,
        Dictionary<string, string>? disabledByConfiguration = null,
        string platformAssembly = PlatformAssembly) =>
        FeatureGraphResolver.Resolve(new FeatureResolutionRequest
        {
            Candidates = [.. features],
            DisabledByCode = new HashSet<string>(disabledByCode ?? [], StringComparer.Ordinal),
            EnabledByCode = new HashSet<string>(enabledByCode ?? [], StringComparer.Ordinal),
            DisabledByConfiguration = disabledByConfiguration ?? [],
            PlatformAssemblyName = platformAssembly,
        });

    public static string[] Order(FeatureCatalog catalog) =>
        [.. catalog.Enabled.OrderBy(e => e.ResolvedOrder).Select(e => e.Id)];
}
