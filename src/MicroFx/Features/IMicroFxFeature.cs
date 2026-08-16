namespace MicroFx.Features;

/// <summary>
/// A unit of cross-cutting capability that hooks into service composition.
/// </summary>
/// <remarks>
/// <para>
/// Built-in and custom features implement the identical contract — the platform has no privileged
/// registration path a service author cannot use.
/// </para>
/// <para>
/// Additional behaviour is contributed through optional facets: <see cref="IConfigurationFeature"/>,
/// <see cref="IPipelineFeature"/>, <see cref="IEndpointFeature"/>, <see cref="IFeatureLifecycle"/>,
/// and <see cref="IFeatureValidator"/>. A feature implements only the facets it needs.
/// </para>
/// </remarks>
public interface IMicroFxFeature
{
    /// <summary>
    /// Identity, ordering, and activation metadata. Must be a pure property: the resolver reads it
    /// before any service exists, and may read it more than once.
    /// </summary>
    FeatureDescriptor Descriptor { get; }

    /// <summary>
    /// Build phase: register services, bind and validate options, contribute health and diagnostic
    /// declarations. Runs once, in dependency order.
    /// </summary>
    /// <remarks>
    /// Must be free of I/O and blocking calls. This method is not cancellable, not traced, and not
    /// budgeted; startup work belongs in <see cref="IFeatureLifecycle.StartingAsync"/> where it is
    /// all three.
    /// </remarks>
    void Configure(FeatureBuildContext context);
}
