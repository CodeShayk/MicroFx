using System.ComponentModel.DataAnnotations;
using MicroFx.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpFeatures = Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace MicroFx.Api;

/// <summary>
/// A group of related endpoints. Discovered from the entry assembly and mapped by the API feature,
/// so adding endpoints never means editing a central registration list.
/// </summary>
public interface IEndpointModule
{
    /// <summary>Maps this module's endpoints.</summary>
    void MapEndpoints(IEndpointRouteBuilder routes);
}

/// <summary>Options for the API feature, bound from <c>MicroFx:Api</c>.</summary>
public sealed class ApiOptions
{
    /// <summary>Default API version applied when an endpoint does not declare one.</summary>
    [Range(1, 99)]
    public int DefaultVersion { get; set; } = 1;

    /// <summary>Whether an OpenAPI document is served. Non-Development requires an explicit opt-in.</summary>
    public bool ExposeOpenApi { get; set; } = true;

    /// <summary>
    /// Whether the Scalar API reference UI is served alongside the document.
    /// </summary>
    /// <remarks>
    /// Scalar renders the OpenAPI document as browsable, executable documentation. It is gated by
    /// exactly the same rules as the document itself — there is no point protecting the schema and
    /// then serving a UI that reads it.
    /// </remarks>
    public bool ExposeApiReference { get; set; } = true;

    /// <summary>Route the API reference UI is served from, on the management surface.</summary>
    [Required]
    public string ApiReferencePath { get; set; } = "/openapi/reference";

    /// <summary>
    /// Opt-in required to serve the OpenAPI document outside Development. Publishing a full
    /// endpoint and schema inventory is a reconnaissance aid; it should be a conscious decision.
    /// </summary>
    public bool AllowOpenApiOutsideDevelopment { get; set; }

    /// <summary>Maximum request body size. Rejects oversized uploads before they are buffered.</summary>
    [Range(1024, long.MaxValue)]
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Server-side request timeout, applied to every endpoint.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>HSTS max-age. Zero disables the header. Only ever sent over HTTPS.</summary>
    public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Content-Security-Policy value. The default suits an API returning only data.</summary>
    [Required]
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'none'; frame-ancestors 'none'; sandbox";

    /// <summary>Permissions-Policy value. Null omits the header.</summary>
    public string? PermissionsPolicy { get; set; } =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), microphone=(), payment=(), usb=()";

    /// <summary>
    /// Allowed CORS origins. Empty means CORS is not configured and no cross-origin request is
    /// permitted — a wildcard is never applied implicitly.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>Whether credentialed cross-origin requests are permitted.</summary>
    public bool AllowCredentials { get; set; }
}

/// <summary>
/// HTTP conventions: problem details, exception mapping, correlation, security headers, request
/// limits, versioning, OpenAPI, and endpoint-module discovery.
/// </summary>
public sealed class ApiFeature : IMicroFxFeature, IPipelineFeature, IEndpointFeature
{
    /// <summary>CORS policy name applied to the business surface.</summary>
    public const string CorsPolicyName = "MicroFxDefault";

    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Api,
        DisplayName = "API",
        Order = 100,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.Observability, BuiltIn.Health],
        SupportedHosts = HostKinds.Web,
        ConfigurationSection = "MicroFx:Api",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new ApiOptions();
        context.Configuration.GetSection("MicroFx:Api").Bind(options);
        context.AddValidatedOptions<ApiOptions>();
        context.Services.TryAddSingleton(options);

        // TryAddEnumerable so a service-registered mapper is additive and the platform default is
        // not duplicated if AddMicroFx runs twice.
        context.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IExceptionMapper, DefaultExceptionMapper>());
        context.Services.TryAddSingleton<ProblemDetailsBuilder>();

        context.Services.Configure<RouteHandlerOptions>(routeOptions =>
            // Detailed model-binding errors name internal types and property paths.
            routeOptions.ThrowOnBadRequest = false);

        context.Services.Configure<HttpFeatures.FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = options.MaxRequestBodyBytes;
            form.ValueLengthLimit = int.MaxValue;
        });

        context.Services.AddRequestTimeouts(timeouts =>
            timeouts.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
            {
                Timeout = options.RequestTimeout,
                TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
            });

        context.Services.AddRouting(routing => routing.LowercaseUrls = true);

        if (options.AllowedOrigins.Count > 0)
        {
            ConfigureCors(context, options);
        }

        if (ShouldExposeOpenApi(options, context.Metadata.IsDevelopment))
        {
            context.Services.AddOpenApi();
        }

        RegisterEndpointModules(context);

        context.Report("version", $"v{options.DefaultVersion}");
        context.Report("openapi", ShouldExposeOpenApi(options, context.Metadata.IsDevelopment));
        context.Report(
            "apiReference",
            ShouldExposeOpenApi(options, context.Metadata.IsDevelopment) && options.ExposeApiReference
                ? options.ApiReferencePath
                : "disabled");
        context.Report("cors", options.AllowedOrigins.Count == 0 ? "none" : options.AllowedOrigins.Count);
    }

    /// <inheritdoc />
    public void UsePipeline(FeaturePipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Application.Services.GetRequiredService<ApiOptions>();

        context.Use(PipelineStage.Exception, app => app.UseMiddleware<ExceptionHandlingMiddleware>());
        context.Use(PipelineStage.Diagnostics, app => app.UseMiddleware<CorrelationIdMiddleware>());
        context.Use(PipelineStage.SecurityHeaders, app => app.UseMiddleware<SecurityHeadersMiddleware>());
        context.Use(PipelineStage.Timeout, app => app.UseRequestTimeouts());

        if (options.AllowedOrigins.Count > 0)
        {
            context.Use(PipelineStage.SecurityHeaders, app => app.UseCors(CorsPolicyName));
        }

        // Enforced at the server rather than only at the model binder, so an oversized body is
        // rejected before it is read into memory.
        context.Use(PipelineStage.Timeout, app => app.Use(async (httpContext, next) =>
        {
            var sizeFeature = httpContext.Features.Get<HttpFeatures.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = options.MaxRequestBodyBytes;
            }

            await next().ConfigureAwait(false);
        }));
    }

    /// <inheritdoc />
    public void MapEndpoints(FeatureEndpointContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Business.ServiceProvider;
        var options = services.GetRequiredService<IOptions<ApiOptions>>().Value;

        if (ShouldExposeOpenApi(options, context.Metadata.IsDevelopment))
        {
            // Mapped onto the management surface: the schema inventory is an operational artefact,
            // not something the public traffic port should advertise.
            context.Management.MapOpenApi();

            if (options.ExposeApiReference)
            {
                // Scalar renders the document as browsable, executable documentation. Same surface
                // and same gating as the document — protecting the schema and then serving a UI
                // that reads it would achieve nothing.
                context.Management
                    .MapScalarApiReference(options.ApiReferencePath, scalar =>
                    {
                        scalar.WithTitle($"{context.Metadata.Name} API")
                              .WithTheme(ScalarTheme.BluePlanet);

                        // "Try it" fires real requests from the browser. Useful locally; outside
                        // Development it invites someone to exercise a live service from a
                        // documentation page.
                        if (!context.Metadata.IsDevelopment)
                        {
                            scalar.HideTestRequestButton();
                        }
                    });
            }
        }

        foreach (var module in services.GetServices<IEndpointModule>())
        {
            module.MapEndpoints(context.Business);
        }
    }

    private static bool ShouldExposeOpenApi(ApiOptions options, bool isDevelopment) =>
        options.ExposeOpenApi && (isDevelopment || options.AllowOpenApiOutsideDevelopment);

    private static void ConfigureCors(FeatureBuildContext context, ApiOptions options)
    {
        context.Services.AddCors(cors => cors.AddPolicy(CorsPolicyName, policy =>
        {
            // Explicit origins only. AllowAnyOrigin combined with credentials is rejected by the
            // browser anyway, and without credentials it is still a needlessly open default.
            policy.WithOrigins([.. options.AllowedOrigins])
                  .WithHeaders("Content-Type", "Authorization", CorrelationIdMiddleware.HeaderName)
                  .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                  .WithExposedHeaders(CorrelationIdMiddleware.HeaderName);

            if (options.AllowCredentials)
            {
                policy.AllowCredentials();
            }
        }));
    }

    /// <summary>
    /// Registers <see cref="IEndpointModule"/> implementations from the application assembly.
    /// </summary>
    /// <remarks>
    /// Scoped to the application's own assembly and to concrete, public, parameterless types.
    /// Scanning every loaded assembly would let a transitively-referenced package contribute routes
    /// to a service that never asked for them.
    /// </remarks>
    private static void RegisterEndpointModules(FeatureBuildContext context)
    {
        var application = Core.ApplicationAssembly.Resolve(context.Environment);

        foreach (var type in Core.ApplicationAssembly.ExportedTypes(application))
        {
            if (!typeof(IEndpointModule).IsAssignableFrom(type) ||
                type is { IsAbstract: true } or { IsInterface: true } ||
                type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            context.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton(typeof(IEndpointModule), type));
        }
    }
}
