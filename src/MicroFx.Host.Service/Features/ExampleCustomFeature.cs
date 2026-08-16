using System.ComponentModel.DataAnnotations;
using MicroFx.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MicroFx.Host.Service.Features;

/// <summary>Options for <see cref="ExampleCustomFeature"/>.</summary>
public sealed class ExampleOptions
{
    /// <summary>Response header name stamped onto every request by this feature's middleware.</summary>
    [Required]
    [RegularExpression("^[A-Za-z0-9-]{1,64}$",
        ErrorMessage = "Header name must be 1-64 characters of letters, digits, or hyphens.")]
    public string HeaderName { get; set; } = "X-Example-Feature";

    /// <summary>Greeting returned by the sample endpoint.</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Greeting { get; set; } = "hello";
}

/// <summary>
/// A custom feature exercising the whole extension contract from outside the kernel: build-time
/// registration, a middleware stage, an endpoint, a health contribution, startup validation, and a
/// drain hook.
/// </summary>
/// <remarks>
/// This exists to apply dogfooding pressure. Any awkwardness in the feature contract shows up here
/// before it shows up in a consuming team's code.
/// </remarks>
public sealed partial class ExampleCustomFeature
    : IMicroFxFeature, IPipelineFeature, IEndpointFeature, IFeatureLifecycle, IFeatureValidator
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = "sample.example",
        DisplayName = "Example custom feature",
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.Observability],
        ConfigurationSection = "Sample:Example",
        SupportedHosts = HostKinds.Web | HostKinds.Worker,
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<ExampleOptions>();

        // Contributed without referencing the health feature's types.
        context.AddHealthContribution(HealthContribution.Ready(
            "example-feature",
            (_, _) => ValueTask.FromResult(HealthCheckResult.Healthy("Example feature is ready."))));

        context.Report("stage", nameof(PipelineStage.Telemetry));
    }

    /// <inheritdoc />
    public void UsePipeline(FeaturePipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Use(PipelineStage.Telemetry, app => app.Use(async (httpContext, next) =>
        {
            var options = httpContext.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<ExampleOptions>>().Value;

            // Set before calling next: response headers cannot be added once the body has started.
            httpContext.Response.Headers[options.HeaderName] = "active";
            await next().ConfigureAwait(false);
        }));
    }

    /// <inheritdoc />
    public void MapEndpoints(FeatureEndpointContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Business.MapGet("/example", (
            Microsoft.Extensions.Options.IOptions<ExampleOptions> options,
            TimeProvider clock) =>
            Results.Ok(new
            {
                message = options.Value.Greeting,
                // TimeProvider rather than DateTimeOffset.UtcNow, so a test can control it.
                at = clock.GetUtcNow(),
            }))
            .WithName("example");
    }

    /// <inheritdoc />
    public ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ValidationReport.Ok());

    /// <inheritdoc />
    public ValueTask StartingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Log(context, "starting");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StoppingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Runs in reverse dependency order, so this drains before observability flushes.
        Log(context, "draining");
        return ValueTask.CompletedTask;
    }

    private static void Log(FeatureLifecycleContext context, string phase)
    {
        var logger = context.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<ExampleCustomFeature>();

        LogPhase(logger, phase);
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Example feature {Phase}.")]
    private static partial void LogPhase(ILogger logger, string phase);
}
