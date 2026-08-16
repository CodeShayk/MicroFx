using MicroFx.Core;
using MicroFx.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MicroFx.Hosting;

/// <summary>Entry points for composing a service with MicroFx.</summary>
public static class MicroFxHostExtensions
{
    internal const string PlatformAssemblyName = "MicroFx";

    /// <summary>
    /// Composes the service: discovers features, resolves the graph, and runs the configuration and
    /// build passes. With no <paramref name="configure"/> delegate this yields a complete,
    /// production-grade service.
    /// </summary>
    /// <param name="builder">The host builder. Both web and generic hosts are supported.</param>
    /// <param name="configure">Optional composition changes: add, disable, replace, or configure features.</param>
    public static TBuilder AddMicroFx<TBuilder>(this TBuilder builder, Action<MicroFxBuilder>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var fx = new MicroFxBuilder();
        configure?.Invoke(fx);

        var metadata = ServiceMetadata.Create(builder.Configuration, builder.Environment);
        var hostKind = HostKindDetector.Detect(builder);

        // ---- Pass 1: discover -----------------------------------------------------------------
        var candidates = new List<IMicroFxFeature>(BuiltInFeatureRegistry.Create());

        if (fx.AssemblyScanningEnabled)
        {
            candidates.AddRange(AssemblyFeatureScanner.Scan());
        }

        candidates.AddRange(fx.ExplicitFeatures);

        // ---- Pass 2: resolve ------------------------------------------------------------------
        // Enablement is read from the configuration present now (files, environment, command line).
        // Sources contributed in pass 3 can change a feature's options but not its participation:
        // whether a capability exists is a deployment decision, not a runtime one.
        var catalog = FeatureGraphResolver.Resolve(new FeatureResolutionRequest
        {
            Candidates = candidates,
            DisabledByCode = fx.DisabledIds,
            EnabledByCode = new HashSet<string>(
                fx.EnabledIds.Concat(FeatureConfigurationReader.ReadEnabled(builder.Configuration)),
                StringComparer.Ordinal),
            DisabledByConfiguration = FeatureConfigurationReader.ReadDisabled(builder.Configuration),
            PlatformAssemblyName = PlatformAssemblyName,
        });

        var state = new FeatureCompositionState { Metadata = metadata, HostKind = hostKind };

        foreach (var entry in catalog.Enabled)
        {
            fx.ApplyConfigurators(entry.Feature);
        }

        // ---- Pass 3: configuration sources -----------------------------------------------------
        // Runs before any options bind, so a secret store added here is visible to every feature.
        foreach (var entry in catalog.Enabled)
        {
            if (entry.Feature is IConfigurationFeature configurationFeature)
            {
                configurationFeature.AddConfigurationSources(
                    new FeatureConfigurationContext(builder.Configuration, metadata, builder.Environment));
            }
        }

        // ---- Pass 4: build ---------------------------------------------------------------------
        var hostOptions = new MicroFxHostOptions();
        builder.Configuration.GetSection("MicroFx:Host").Bind(hostOptions);

        var composition = new MicroFxComposition
        {
            Catalog = catalog,
            State = state,
            Metadata = metadata,
            Options = hostOptions,
        };

        builder.Services.AddSingleton(composition);
        builder.Services.AddSingleton<IFeatureCatalog>(catalog);
        builder.Services.AddSingleton(metadata);
        builder.Services.AddOptions<MicroFxHostOptions>()
            .Bind(builder.Configuration.GetSection("MicroFx:Host"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        foreach (var entry in catalog.Enabled)
        {
            entry.Feature.Configure(
                new FeatureBuildContext(builder, state, catalog, entry.Id));
        }

        foreach (var entry in catalog.Enabled)
        {
            entry.Facts = state.Reports.TryGetValue(entry.Id, out var facts)
                ? facts
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        // Passes 5–8 (validate, starting, pipeline, started) run from the hosted service and
        // RunMicroFxAsync. Registered last so every feature's own hosted services start first and
        // stop last, which is what makes drain ordering work.
        builder.Services.AddHostedService<MicroFxLifecycleService>();

        if (hostKind == HostKinds.Web && hostOptions.ConfigureListeners)
        {
            ConfigureListeners(builder.Services, hostOptions);
        }

        return builder;
    }

    /// <summary>
    /// Applies the pipeline and endpoint passes, then runs the host. Replaces
    /// <c>app.Run()</c>.
    /// </summary>
    public static Task RunMicroFxAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMicroFx();
        return app.RunAsync(cancellationToken);
    }

    /// <summary>Runs a non-web host composed with MicroFx.</summary>
    public static Task RunMicroFxAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Applies the pipeline and endpoint passes without running the host. Use this when the host is
    /// started by something else, such as a test harness.
    /// </summary>
    public static WebApplication UseMicroFx(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var composition = app.Services.GetRequiredService<MicroFxComposition>();
        if (composition.PipelineApplied)
        {
            return app;
        }

        composition.PipelineApplied = true;
        var options = app.Services.GetRequiredService<IOptions<MicroFxHostOptions>>().Value;

        // ---- Pass 7a: pipeline -----------------------------------------------------------------
        // Every feature contributes before any middleware is materialised, so registering into an
        // already-emitted stage is impossible by construction.
        var stages = new SortedDictionary<int, List<Action<IApplicationBuilder>>>();

        foreach (var entry in composition.EnabledFor(HostKinds.Web))
        {
            if (entry.Feature is IPipelineFeature pipelineFeature)
            {
                pipelineFeature.UsePipeline(
                    new FeaturePipelineContext(app, stages, entry.ResolvedOrder, composition.Metadata));
            }
        }

        foreach (var actions in stages.Values)
        {
            foreach (var action in actions)
            {
                action(app);
            }
        }

        // ---- Pass 7b: endpoints ----------------------------------------------------------------
        // Management routes live behind a filter bound to the management port. Exposing them
        // publicly therefore requires deliberately mapping onto the wrong builder.
        IEndpointRouteBuilder management = app.MapGroup(string.Empty)
            .AddEndpointFilter(new ManagementPortFilter(options.ManagementPort));

        foreach (var entry in composition.EnabledFor(HostKinds.Web))
        {
            if (entry.Feature is IEndpointFeature endpointFeature)
            {
                endpointFeature.MapEndpoints(
                    new FeatureEndpointContext(app, management, composition.Metadata, composition.Catalog));
            }
        }

        return app;
    }

    private static void ConfigureListeners(IServiceCollection services, MicroFxHostOptions options)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<KestrelServerOptions>, ManagementListenerSetup>());
        services.AddSingleton(options);
    }
}
