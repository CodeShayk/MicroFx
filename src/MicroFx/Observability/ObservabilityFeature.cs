using System.ComponentModel.DataAnnotations;
using MicroFx.Features;
using MicroFx.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MicroFx.Observability;

/// <summary>Options for the observability feature, bound from <c>MicroFx:Observability</c>.</summary>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// Head sampling ratio for traces. Defaults to 1.0 in Development and 0.1 elsewhere; errors are
    /// always sampled regardless.
    /// </summary>
    [Range(0.0, 1.0)]
    public double SampleRatio { get; set; } = 0.1;

    /// <summary>
    /// OTLP endpoint. When empty, no exporter is registered — the service still produces telemetry
    /// through the logging pipeline, it simply does not ship it. Also honours the standard
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>Whether health and diagnostic probes are traced. Off by default: in a low-traffic
    /// service, probes would otherwise be most of the spans.</summary>
    public bool TraceProbes { get; set; }

    /// <summary>Whether log records carry scopes.</summary>
    public bool IncludeScopes { get; set; } = true;

    /// <summary>
    /// Whether to include the formatted log message alongside its structured fields. Costs
    /// bandwidth and duplicates data already present in the template plus arguments.
    /// </summary>
    public bool IncludeFormattedMessage { get; set; } = true;
}

/// <summary>
/// OpenTelemetry logs, traces, and metrics with the platform's resource attributes.
/// </summary>
/// <remarks>
/// A kernel feature: a service that cannot emit telemetry cannot explain its own failures, so
/// "temporarily disable observability" is not offered.
/// </remarks>
public sealed class ObservabilityFeature : IMicroFxFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Observability,
        DisplayName = "Observability",
        IsKernel = true,
        Order = 20,
        DependsOn = [BuiltIn.Core, BuiltIn.Configuration],
        ConfigurationSection = "MicroFx:Observability",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<ObservabilityOptions>();

        var options = new ObservabilityOptions
        {
            SampleRatio = context.Metadata.IsDevelopment ? 1.0 : 0.1,
        };
        context.Configuration.GetSection("MicroFx:Observability").Bind(options);

        var endpoint = ResolveEndpoint(options, context.Configuration);
        var metadata = context.Metadata;

        // The platform's own startup spans and metrics. Registered here rather than by the kernel so
        // there is exactly one place that decides what is observed.
        context.AddDiagnosticSource(MicroFxLifecycleService.ActivitySourceName);
        context.AddMeter(MicroFxMetrics.MeterName);

        context.Builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeScopes = options.IncludeScopes;
            logging.IncludeFormattedMessage = options.IncludeFormattedMessage;
            logging.ParseStateValues = true;
            logging.SetResourceBuilder(BuildResource(context));
        });

        var otel = context.Services.AddOpenTelemetry()
            .ConfigureResource(resource => ConfigureResource(resource, context));

        var isWeb = context.HostKind == HostKinds.Web;

        otel.WithTracing(tracing =>
        {
            if (isWeb)
            {
                tracing.AddAspNetCoreInstrumentation(instrumentation =>
                {
                    if (!options.TraceProbes)
                    {
                        instrumentation.Filter = httpContext => !IsProbePath(httpContext.Request.Path);
                    }
                });
            }

            tracing
                .AddHttpClientInstrumentation()
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SampleRatio)));
        });

        otel.WithMetrics(metrics =>
        {
            if (isWeb)
            {
                metrics.AddAspNetCoreInstrumentation();
            }

            metrics
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation();
        });

        // Sources and meters declared by OTHER features are collected here, deferred to service
        // provider build time. Observability runs early in the graph — before messaging, persistence
        // or any adapter has declared anything — so reading the declarations now would see an empty
        // set. Deferring is what lets observability stay ignorant of every feature it instruments.
        context.Services.ConfigureOpenTelemetryTracerProvider((provider, tracing) =>
        {
            foreach (var source in Declarations(provider).ActivitySources)
            {
                tracing.AddSource(source);
            }
        });

        context.Services.ConfigureOpenTelemetryMeterProvider((provider, metrics) =>
        {
            foreach (var meter in Declarations(provider).Meters)
            {
                metrics.AddMeter(meter);
            }
        });

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            otel.UseOtlpExporter();
            context.Report("otlp", endpoint);
        }
        else
        {
            // Registering an exporter with nowhere to send would spam connection failures on every
            // export interval, which is worse than not exporting.
            context.Report("otlp", "not configured");
        }

        context.Report("sampleRatio", options.SampleRatio);
        context.Report("env", metadata.Environment);
    }

    private static string? ResolveEndpoint(ObservabilityOptions options, IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(options.OtlpEndpoint)
            ? options.OtlpEndpoint
            : configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

    private static ResourceBuilder BuildResource(FeatureBuildContext context)
    {
        var resource = ResourceBuilder.CreateDefault();
        ConfigureResource(resource, context);
        return resource;
    }

    private static void ConfigureResource(ResourceBuilder resource, FeatureBuildContext context)
    {
        var metadata = context.Metadata;

        resource.AddService(
            serviceName: metadata.Name,
            serviceVersion: metadata.Version,
            serviceInstanceId: metadata.InstanceId);

        var attributes = new List<KeyValuePair<string, object>>
        {
            new("deployment.environment.name", metadata.Environment),
            new("microfx.role", metadata.Role),
        };

        if (metadata.Team is { } team)
        {
            attributes.Add(new KeyValuePair<string, object>("team", team));
        }

        if (metadata.CostCenter is { } costCenter)
        {
            attributes.Add(new KeyValuePair<string, object>("cost_center", costCenter));
        }

        if (metadata.Commit is { } commit)
        {
            attributes.Add(new KeyValuePair<string, object>("service.commit", commit));
        }

        resource.AddAttributes(attributes);
    }

    private static bool IsProbePath(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase);

    private static FeatureCompositionState Declarations(IServiceProvider provider) =>
        provider.GetRequiredService<MicroFxComposition>().State;
}
