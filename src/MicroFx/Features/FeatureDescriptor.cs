namespace MicroFx.Features;

/// <summary>
/// Identity, ordering, and activation metadata for a <see cref="IMicroFxFeature"/>.
/// </summary>
/// <remarks>
/// Ids are strings rather than types so they can be referenced from configuration
/// (<c>MicroFx:Features:microfx.caching:Enabled=false</c>) and from assemblies that do not
/// reference each other. Unknown ids in <see cref="DependsOn"/> are startup errors; unknown ids in
/// <see cref="Before"/> or <see cref="After"/> are warnings.
/// </remarks>
public sealed record FeatureDescriptor
{
    /// <summary>Reserved id prefix. Only the MicroFx assembly may declare features under it.</summary>
    public const string ReservedPrefix = "microfx.";

    /// <summary>Stable, unique, lowercase dotted id. For example <c>acme.audit</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name for the startup banner and the feature catalog.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Id of a feature this one substitutes. The replaced feature is removed from the graph and
    /// this feature inherits its edges, so unrelated features that ordered themselves against the
    /// original keep working.
    /// </summary>
    public string? Replaces { get; init; }

    /// <summary>
    /// Hard dependencies. Resolution fails if a required id is absent or disabled — this feature
    /// cannot function without them.
    /// </summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Soft ordering: run after these, if they are present.</summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>Soft ordering: run before these, if they are present.</summary>
    public IReadOnlyList<string> Before { get; init; } = [];

    /// <summary>
    /// Deterministic tie-break among features with no ordering relationship; lower runs first.
    /// Built-ins occupy 0..999, custom features should use 1000 or above.
    /// </summary>
    public int Order { get; init; } = DefaultOrder;

    /// <summary>Default <see cref="Order"/> for features that do not specify one.</summary>
    public const int DefaultOrder = 1000;

    /// <summary>Whether the feature activates without explicit opt-in. Opt-out, not opt-in.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>
    /// Kernel features cannot be disabled. Each is a precondition for diagnosing the failure of
    /// anything else, so "turn it off temporarily" is not offered.
    /// </summary>
    public bool IsKernel { get; init; }

    /// <summary>Configuration section bound for this feature's options.</summary>
    public string? ConfigurationSection { get; init; }

    /// <summary>
    /// Host kinds this feature applies to. Facets that do not match the running host are skipped
    /// with a debug log rather than failing, so one feature set composes an API and a worker.
    /// </summary>
    public HostKinds SupportedHosts { get; init; } = HostKinds.Any;

    /// <summary>Name shown in diagnostics: <see cref="DisplayName"/> when set, otherwise <see cref="Id"/>.</summary>
    public string Name => DisplayName ?? Id;
}
