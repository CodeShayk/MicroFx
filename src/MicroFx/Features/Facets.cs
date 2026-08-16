namespace MicroFx.Features;

/// <summary>
/// Contributes configuration sources. Runs in its own pass, before any options bind, so a secret
/// store or remote provider is present by the time another feature reads configuration.
/// </summary>
public interface IConfigurationFeature : IMicroFxFeature
{
    /// <summary>Adds configuration sources to the builder.</summary>
    void AddConfigurationSources(FeatureConfigurationContext context);
}

/// <summary>Contributes middleware to the HTTP pipeline. Skipped on non-Web hosts.</summary>
public interface IPipelineFeature : IMicroFxFeature
{
    /// <summary>
    /// Registers middleware against a <see cref="PipelineStage"/>. A feature names a stage, never a
    /// position, so ordering cannot be broken by rearranging statements.
    /// </summary>
    void UsePipeline(FeaturePipelineContext context);
}

/// <summary>Contributes endpoints to the business or management route builder. Skipped on non-Web hosts.</summary>
public interface IEndpointFeature : IMicroFxFeature
{
    /// <summary>Maps endpoints.</summary>
    void MapEndpoints(FeatureEndpointContext context);
}

/// <summary>
/// Ordered asynchronous lifecycle. Each call is wrapped in a span and a per-feature budget by the
/// kernel, so a slow or hanging feature is attributable by name.
/// </summary>
public interface IFeatureLifecycle : IMicroFxFeature
{
    /// <summary>
    /// Runs before the host accepts traffic, in dependency order. Preflight, topology assertion,
    /// warm-up, migration gate. Throwing here aborts startup, which is the point.
    /// </summary>
    ValueTask StartingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <summary>Runs after the host is listening. Post-ready registration, leader election kickoff.</summary>
    ValueTask StartedAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Runs on shutdown in <em>reverse</em> dependency order, within the drain budget. This ordering
    /// is what makes "cancel consumers, drain in-flight, close connections, flush telemetry" correct
    /// rather than coincidental.
    /// </summary>
    ValueTask StoppingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

/// <summary>
/// Startup self-check producing a structured report rather than an exception.
/// </summary>
/// <remarks>
/// Validators run before <see cref="IFeatureLifecycle.StartingAsync"/> and their results are
/// aggregated, so a service with three unrelated misconfigurations reports all three in one startup
/// instead of one per restart.
/// </remarks>
public interface IFeatureValidator : IMicroFxFeature
{
    /// <summary>Validates this feature's preconditions.</summary>
    ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken);
}
