using MicroFx.Api;
using MicroFx.Features;
using MicroFx.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.MultiTenancy;

/// <summary>Where a tenant identifier may be read from.</summary>
public enum TenantSource
{
    /// <summary>A claim on the validated token. The only source that cannot be forged.</summary>
    Claim,

    /// <summary>A request header. Trusted only when the caller is itself trusted.</summary>
    Header,

    /// <summary>A leading route segment.</summary>
    RouteSegment,
}

/// <summary>Options for the multi-tenancy feature, bound from <c>MicroFx:MultiTenancy</c>.</summary>
public sealed class MultiTenancyOptions
{
    /// <summary>
    /// Where the tenant is resolved from. <see cref="TenantSource.Claim"/> is the default and the
    /// only safe choice for a service reachable by untrusted callers.
    /// </summary>
    public TenantSource Source { get; set; } = TenantSource.Claim;

    /// <summary>Claim carrying the tenant when <see cref="Source"/> is <see cref="TenantSource.Claim"/>.</summary>
    public string ClaimType { get; set; } = "tenant_id";

    /// <summary>Header carrying the tenant when <see cref="Source"/> is <see cref="TenantSource.Header"/>.</summary>
    public string HeaderName { get; set; } = "X-Tenant-Id";

    /// <summary>
    /// Whether a request without a resolvable tenant is rejected. On by default: an unscoped query
    /// in a multi-tenant store returns everyone's data.
    /// </summary>
    public bool RequireTenant { get; set; } = true;

    /// <summary>Paths exempt from tenant resolution, such as probes and public metadata.</summary>
    public IList<string> AnonymousPaths { get; } = ["/health", "/internal"];
}

/// <summary>The tenant in scope for the current operation.</summary>
public interface ITenantContext
{
    /// <summary>Current tenant, or null when none is in scope.</summary>
    string? Current { get; }

    /// <summary>Whether a tenant is in scope.</summary>
    bool HasTenant => Current is not null;

    /// <summary>Current tenant, or throws when none is in scope.</summary>
    string Require() => Current ?? throw new InvalidOperationException(
        "No tenant is in scope. A tenant-scoped operation ran outside a tenant-resolved request.");
}

/// <summary>Resolves the tenant for a request.</summary>
public interface ITenantResolver
{
    /// <summary>Returns the tenant, or null when it cannot be determined.</summary>
    string? Resolve(HttpContext context);
}

internal sealed class TenantContext : ITenantContext
{
    public string? Current { get; internal set; }
}

internal sealed class DefaultTenantResolver(
    Microsoft.Extensions.Options.IOptions<MultiTenancyOptions> options) : ITenantResolver
{
    private readonly MultiTenancyOptions _options = options.Value;

    public string? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var raw = _options.Source switch
        {
            // Read after authentication, so this is a verified claim rather than a caller assertion.
            TenantSource.Claim => context.User.FindFirst(_options.ClaimType)?.Value,
            TenantSource.Header => context.Request.Headers[_options.HeaderName].ToString(),
            TenantSource.RouteSegment => FirstSegment(context.Request.Path),
            _ => null,
        };

        return Sanitize(raw);
    }

    private static string? FirstSegment(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : null;
    }

    /// <summary>
    /// Constrains the tenant identifier before it is used.
    /// </summary>
    /// <remarks>
    /// The tenant flows into cache keys, log scopes, storage prefixes, and query filters. An
    /// unconstrained value is therefore a key-injection vector — a tenant of <c>a:b</c> could
    /// collide with a legitimately-scoped key for tenant <c>a</c>. Restricting the alphabet closes
    /// that at the only place where every path converges.
    /// </remarks>
    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64)
        {
            return null;
        }

        foreach (var character in raw)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return null;
            }
        }

        return raw;
    }
}

/// <summary>
/// Resolves the tenant for each request and makes it available for scoping.
/// </summary>
/// <remarks>
/// Ordered after authentication so the tenant comes from a verified claim. Reading it from an
/// unverified header before authentication would let any caller name any tenant, which is the
/// single most damaging defect available in a multi-tenant service.
/// </remarks>
public sealed class MultiTenancyFeature : IMicroFxFeature, IPipelineFeature
{
    /// <summary>Key under which the resolved tenant is placed on <see cref="HttpContext.Items"/>.</summary>
    public const string TenantItemKey = "MicroFx.TenantId";

    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.MultiTenancy,
        DisplayName = "Multi-tenancy",
        Order = 210,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.Security],
        EnabledByDefault = false,   // most services are single-tenant; opting in is explicit
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:MultiTenancy",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new MultiTenancyOptions();
        context.Configuration.GetSection("MicroFx:MultiTenancy").Bind(options);
        context.AddValidatedOptions<MultiTenancyOptions>();

        context.Services.TryAddSingleton<ITenantResolver, DefaultTenantResolver>();
        context.Services.TryAddScoped<TenantContext>();
        context.Services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        context.Report("source", options.Source.ToString());
        context.Report("required", options.RequireTenant);
    }

    /// <inheritdoc />
    public void UsePipeline(FeaturePipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Use(PipelineStage.Tenancy, app => app.UseMiddleware<TenantResolutionMiddleware>());
    }
}

internal sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    ITenantResolver resolver,
    ProblemDetailsBuilder problems,
    Microsoft.Extensions.Options.IOptions<MultiTenancyOptions> options)
{
    private readonly MultiTenancyOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (IsExempt(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var tenant = resolver.Resolve(context);

        if (tenant is null && _options.RequireTenant)
        {
            await context.RequestServices
                .GetRequiredService<AuditRecorder>()
                .RecordAsync(
                    AuditEventKind.CrossTenantAccess, context, "tenant-unresolved",
                    context.RequestAborted)
                .ConfigureAwait(false);

            var problem = problems.Create(
                StatusCodes.Status403Forbidden,
                "Tenant not resolved",
                "The request could not be attributed to a tenant.",
                ProblemDetailsBuilder.TypeBase + "tenant",
                context);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        tenantContext.Current = tenant;
        context.Items[MultiTenancyFeature.TenantItemKey] = tenant;

        await next(context).ConfigureAwait(false);
    }

    private bool IsExempt(PathString path)
    {
        foreach (var exempt in _options.AnonymousPaths)
        {
            if (path.StartsWithSegments(exempt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
