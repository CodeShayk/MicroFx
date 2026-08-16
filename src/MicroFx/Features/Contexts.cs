using MicroFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MicroFx.Features;

/// <summary>Shared state the kernel threads through every context.</summary>
internal sealed class FeatureCompositionState
{
    public required ServiceMetadata Metadata { get; init; }
    public required HostKinds HostKind { get; init; }
    public List<HealthContribution> HealthContributions { get; } = [];
    public HashSet<string> ActivitySources { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Meters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, object?>> Reports { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Passed to <see cref="IConfigurationFeature.AddConfigurationSources"/>. Runs before any options
/// bind, so a source added here is visible to every feature's configuration.
/// </summary>
public sealed class FeatureConfigurationContext
{
    internal FeatureConfigurationContext(IConfigurationBuilder builder, ServiceMetadata metadata, IHostEnvironment environment)
    {
        Sources = builder;
        Metadata = metadata;
        Environment = environment;
    }

    /// <summary>The configuration builder to add sources to.</summary>
    public IConfigurationBuilder Sources { get; }

    /// <summary>Identity of the running service.</summary>
    public ServiceMetadata Metadata { get; }

    /// <summary>The host environment.</summary>
    public IHostEnvironment Environment { get; }
}

/// <summary>
/// Passed to <see cref="IMicroFxFeature.Configure"/>. Mediates access to options, health, and
/// diagnostics so a feature can contribute to those subsystems without referencing them.
/// </summary>
public sealed class FeatureBuildContext
{
    private readonly FeatureCompositionState _state;
    private readonly string _featureId;

    internal FeatureBuildContext(
        IHostApplicationBuilder builder,
        FeatureCompositionState state,
        IFeatureCatalog catalog,
        string featureId)
    {
        Builder = builder;
        _state = state;
        Catalog = catalog;
        _featureId = featureId;
    }

    /// <summary>The underlying host builder.</summary>
    public IHostApplicationBuilder Builder { get; }

    /// <summary>The service collection.</summary>
    public IServiceCollection Services => Builder.Services;

    /// <summary>The composed configuration, including sources added in the configuration pass.</summary>
    public IConfiguration Configuration => Builder.Configuration;

    /// <summary>The host environment.</summary>
    public IHostEnvironment Environment => Builder.Environment;

    /// <summary>Identity of the running service.</summary>
    public ServiceMetadata Metadata => _state.Metadata;

    /// <summary>What kind of host is being composed, so a feature can branch without reflection.</summary>
    public HostKinds HostKind => _state.HostKind;

    /// <summary>Read-only view of the resolved graph: which features are enabled around this one.</summary>
    public IFeatureCatalog Catalog { get; }

    /// <summary>
    /// Binds an options type, applies data-annotation validation, and validates at startup rather
    /// than at first use — so bad configuration fails the deployment, not the first request.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <param name="sectionName">
    /// Configuration section. Defaults to the feature's declared
    /// <see cref="FeatureDescriptor.ConfigurationSection"/>.
    /// </param>
    public OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(string? sectionName = null)
        where TOptions : class
    {
        var section = sectionName ?? Catalog[_featureId]?.Descriptor.ConfigurationSection;

        var options = Services.AddOptions<TOptions>();
        if (!string.IsNullOrEmpty(section))
        {
            options.Bind(Configuration.GetSection(section));
        }

        return options.ValidateDataAnnotations().ValidateOnStart();
    }

    /// <summary>Declares a health check without referencing the health feature's types.</summary>
    public void AddHealthContribution(HealthContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _state.HealthContributions.Add(contribution);
    }

    /// <summary>Declares an <see cref="System.Diagnostics.ActivitySource"/> name for tracing to pick up.</summary>
    public void AddDiagnosticSource(string activitySourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activitySourceName);
        _state.ActivitySources.Add(activitySourceName);
    }

    /// <summary>Declares a <see cref="System.Diagnostics.Metrics.Meter"/> name for metrics to pick up.</summary>
    public void AddMeter(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        _state.Meters.Add(meterName);
    }

    /// <summary>
    /// Records a fact about this feature for the startup banner and the feature catalog endpoint.
    /// </summary>
    /// <remarks>
    /// Reported values are surfaced in logs and diagnostics. Never report a secret, a connection
    /// string, or a token — report its presence or its host, not its value.
    /// </remarks>
    public void Report(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_state.Reports.TryGetValue(_featureId, out var facts))
        {
            facts = new Dictionary<string, object?>(StringComparer.Ordinal);
            _state.Reports[_featureId] = facts;
        }

        facts[key] = value;
    }
}

/// <summary>Passed to <see cref="IFeatureValidator.ValidateAsync"/>.</summary>
public sealed class FeatureValidationContext(
    IServiceProvider services, ServiceMetadata metadata, IFeatureCatalog catalog)
{
    /// <summary>The fully built root service provider.</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>Identity of the running service.</summary>
    public ServiceMetadata Metadata { get; } = metadata;

    /// <summary>The resolved feature graph.</summary>
    public IFeatureCatalog Catalog { get; } = catalog;
}

/// <summary>Passed to the <see cref="IFeatureLifecycle"/> methods.</summary>
public sealed class FeatureLifecycleContext(
    IServiceProvider services, ServiceMetadata metadata, IFeatureCatalog catalog)
{
    /// <summary>The fully built root service provider.</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>Identity of the running service.</summary>
    public ServiceMetadata Metadata { get; } = metadata;

    /// <summary>The resolved feature graph.</summary>
    public IFeatureCatalog Catalog { get; } = catalog;
}

/// <summary>
/// Passed to <see cref="IPipelineFeature.UsePipeline"/>. Middleware is registered against a stage
/// and flattened by the kernel after every feature has contributed.
/// </summary>
public sealed class FeaturePipelineContext
{
    private readonly SortedDictionary<int, List<Action<IApplicationBuilder>>> _stages;
    private readonly int _featureOrder;

    internal FeaturePipelineContext(
        WebApplication application,
        SortedDictionary<int, List<Action<IApplicationBuilder>>> stages,
        int featureOrder,
        ServiceMetadata metadata)
    {
        Application = application;
        _stages = stages;
        _featureOrder = featureOrder;
        Metadata = metadata;
    }

    /// <summary>The application, for features needing more than middleware registration.</summary>
    public WebApplication Application { get; }

    /// <summary>Identity of the running service.</summary>
    public ServiceMetadata Metadata { get; }

    /// <summary>Registers middleware into a stage.</summary>
    /// <param name="stage">The stage that determines ordering relative to other features.</param>
    /// <param name="configure">Registers the middleware.</param>
    public void Use(PipelineStage stage, Action<IApplicationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Stage first, then graph position within the stage: ordering is fully determined and
        // reproducible, never dependent on which feature happened to register first.
        var key = ((int)stage * 100_000) + _featureOrder;
        if (!_stages.TryGetValue(key, out var actions))
        {
            actions = [];
            _stages[key] = actions;
        }

        actions.Add(configure);
    }
}

/// <summary>
/// Passed to <see cref="IEndpointFeature.MapEndpoints"/>.
/// </summary>
/// <remarks>
/// <see cref="Business"/> and <see cref="Management"/> are separate builders on purpose. The most
/// common security defect in this class of framework is a diagnostics endpoint reachable from the
/// internet; making the management surface a different object means exposing it requires
/// deliberately using the wrong one.
/// </remarks>
public sealed class FeatureEndpointContext(
    IEndpointRouteBuilder business,
    IEndpointRouteBuilder management,
    ServiceMetadata metadata,
    IFeatureCatalog catalog)
{
    /// <summary>Public routes, served on the traffic port.</summary>
    public IEndpointRouteBuilder Business { get; } = business;

    /// <summary>Management routes, served on the management port only. Never internet-exposed.</summary>
    public IEndpointRouteBuilder Management { get; } = management;

    /// <summary>Identity of the running service.</summary>
    public ServiceMetadata Metadata { get; } = metadata;

    /// <summary>The resolved feature graph.</summary>
    public IFeatureCatalog Catalog { get; } = catalog;
}
