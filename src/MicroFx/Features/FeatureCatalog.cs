using System.Collections;

namespace MicroFx.Features;

/// <summary>Why a feature is not active.</summary>
public enum DisabledReason
{
    /// <summary>The feature is active.</summary>
    None = 0,

    /// <summary>Disabled by a call to <c>Disable</c> during composition.</summary>
    DisabledByCode,

    /// <summary>Disabled by <c>MicroFx:Features:{id}:Enabled=false</c>.</summary>
    DisabledByConfiguration,

    /// <summary>Declares <see cref="FeatureDescriptor.EnabledByDefault"/> false and was not opted into.</summary>
    NotEnabledByDefault,

    /// <summary>Superseded by another feature declaring <see cref="FeatureDescriptor.Replaces"/>.</summary>
    Replaced,
}

/// <summary>One entry in the resolved feature graph.</summary>
public sealed class FeatureCatalogEntry
{
    internal FeatureCatalogEntry(IMicroFxFeature feature, int resolvedOrder)
    {
        Feature = feature;
        Descriptor = feature.Descriptor;
        ResolvedOrder = resolvedOrder;
        AssemblyName = feature.GetType().Assembly.GetName().Name ?? "unknown";
    }

    /// <summary>The feature instance.</summary>
    public IMicroFxFeature Feature { get; }

    /// <summary>The feature's declared metadata.</summary>
    public FeatureDescriptor Descriptor { get; }

    /// <summary>Feature id.</summary>
    public string Id => Descriptor.Id;

    /// <summary>Position in the resolved order. Lower runs first.</summary>
    public int ResolvedOrder { get; }

    /// <summary>Whether the feature is active.</summary>
    public bool IsEnabled => Reason == DisabledReason.None;

    /// <summary>Why the feature is inactive, if it is.</summary>
    public DisabledReason Reason { get; internal set; }

    /// <summary>
    /// Human-readable detail for <see cref="Reason"/> — the configuration path that disabled it, or
    /// the id of the feature that replaced it. This is what turns "why isn't caching on?" from an
    /// investigation into a lookup.
    /// </summary>
    public string? ReasonDetail { get; internal set; }

    /// <summary>Id of the feature this one replaces, if any.</summary>
    public string? Replaces => Descriptor.Replaces;

    /// <summary>Assembly that contributed this feature.</summary>
    public string AssemblyName { get; }

    /// <summary>Facts the feature reported during its build pass.</summary>
    public IReadOnlyDictionary<string, object?> Facts { get; internal set; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>How long each lifecycle phase took at the last startup.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Timings => _timings;

    private readonly Dictionary<string, TimeSpan> _timings = new(StringComparer.Ordinal);

    internal void RecordTiming(string phase, TimeSpan elapsed) => _timings[phase] = elapsed;
}

/// <summary>Read-only view of the resolved feature graph.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix",
    Justification = "'Catalog' is the domain term used throughout the platform and its diagnostics; " +
                    "'FeatureCollection' would collide with the ASP.NET Core type of that name.")]
public interface IFeatureCatalog : IReadOnlyCollection<FeatureCatalogEntry>
{
    /// <summary>All entries, enabled and not, in resolved order.</summary>
    IReadOnlyList<FeatureCatalogEntry> All { get; }

    /// <summary>Enabled entries, in resolved order.</summary>
    IReadOnlyList<FeatureCatalogEntry> Enabled { get; }

    /// <summary>Looks up an entry by id, enabled or not. Returns null when unknown.</summary>
    FeatureCatalogEntry? this[string id] { get; }

    /// <summary>Whether a feature with this id is present and enabled.</summary>
    bool IsEnabled(string id);
}

internal sealed class FeatureCatalog : IFeatureCatalog
{
    private readonly Dictionary<string, FeatureCatalogEntry> _byId;

    public FeatureCatalog(IReadOnlyList<FeatureCatalogEntry> entries)
    {
        All = entries;
        _byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<FeatureCatalogEntry> All { get; }

    public IReadOnlyList<FeatureCatalogEntry> Enabled => field ??= [.. All.Where(e => e.IsEnabled)];

    public FeatureCatalogEntry? this[string id] => _byId.GetValueOrDefault(id);

    public bool IsEnabled(string id) => _byId.TryGetValue(id, out var e) && e.IsEnabled;

    public int Count => All.Count;

    public IEnumerator<FeatureCatalogEntry> GetEnumerator() => All.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
