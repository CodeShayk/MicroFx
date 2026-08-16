using MicroFx.Features;

namespace MicroFx.Hosting;

/// <summary>
/// Configures MicroFx composition: which features participate, and how each is configured.
/// </summary>
/// <remarks>
/// Every method here is an explicit, greppable act. Disabling or replacing a capability shows up in
/// review and at <c>/internal/features</c>; there is no way to quietly diverge from the defaults.
/// </remarks>
public sealed class MicroFxBuilder
{
    private readonly List<IMicroFxFeature> _explicitFeatures = [];
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _replacements = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, List<Action<object>>> _configurators = [];

    internal MicroFxBuilder()
    {
    }

    internal bool AssemblyScanningEnabled { get; private set; } = true;

    internal IReadOnlyList<IMicroFxFeature> ExplicitFeatures => _explicitFeatures;

    internal IReadOnlySet<string> DisabledIds => _disabled;

    internal IReadOnlySet<string> EnabledIds => _enabled;

    /// <summary>Adds a feature explicitly. Takes precedence over discovery.</summary>
    /// <typeparam name="TFeature">The feature type.</typeparam>
    public MicroFxBuilder AddFeature<TFeature>() where TFeature : IMicroFxFeature, new() =>
        AddFeature(new TFeature());

    /// <summary>Adds a pre-constructed feature. Use when the feature needs constructor arguments.</summary>
    public MicroFxBuilder AddFeature(IMicroFxFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        _explicitFeatures.Add(feature);
        return this;
    }

    /// <summary>
    /// Disables a feature. Configuration (<c>MicroFx:Features:{id}:Enabled</c>) overrides this, so an
    /// operator can re-enable without a rebuild. Kernel features refuse and fail startup.
    /// </summary>
    public MicroFxBuilder Disable(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        _disabled.Add(featureId);
        _enabled.Remove(featureId);
        return this;
    }

    /// <summary>Opts into a feature that declares <see cref="FeatureDescriptor.EnabledByDefault"/> false.</summary>
    public MicroFxBuilder Enable(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        _enabled.Add(featureId);
        _disabled.Remove(featureId);
        return this;
    }

    /// <summary>
    /// Replaces one feature with another. The replacement inherits the original's graph edges, so
    /// features ordered against the original keep working.
    /// </summary>
    /// <typeparam name="TExisting">The feature being replaced.</typeparam>
    /// <typeparam name="TReplacement">The replacement.</typeparam>
    public MicroFxBuilder Replace<TExisting, TReplacement>()
        where TExisting : IMicroFxFeature, new()
        where TReplacement : IMicroFxFeature, new()
    {
        var existingId = new TExisting().Descriptor.Id;
        var replacement = new TReplacement();

        _replacements[existingId] = replacement.Descriptor.Id;
        _explicitFeatures.Add(replacement);
        _disabled.Add(existingId);
        return this;
    }

    /// <summary>
    /// Configures a feature's options object. The delegate runs during that feature's build pass,
    /// before it registers anything.
    /// </summary>
    /// <typeparam name="TFeature">The feature to configure.</typeparam>
    public MicroFxBuilder Configure<TFeature>(Action<TFeature> configure) where TFeature : IMicroFxFeature
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (!_configurators.TryGetValue(typeof(TFeature), out var list))
        {
            list = [];
            _configurators[typeof(TFeature)] = list;
        }

        list.Add(f => configure((TFeature)f));
        return this;
    }

    /// <summary>
    /// Turns off assembly scanning, so only built-in and explicitly added features participate.
    /// A reasonable stance for a service that wants a fully auditable composition.
    /// </summary>
    public MicroFxBuilder DisableAssemblyScanning()
    {
        AssemblyScanningEnabled = false;
        return this;
    }

    internal void ApplyConfigurators(IMicroFxFeature feature)
    {
        // Walk the type hierarchy and interfaces so Configure<PersistenceFeature> still applies to a
        // subclass a service substituted in.
        foreach (var (type, actions) in _configurators)
        {
            if (!type.IsInstanceOfType(feature))
            {
                continue;
            }

            foreach (var action in actions)
            {
                action(feature);
            }
        }
    }
}
