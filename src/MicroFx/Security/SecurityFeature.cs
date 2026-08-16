using System.ComponentModel.DataAnnotations;
using MicroFx.Features;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace MicroFx.Security;

/// <summary>Options for the security feature, bound from <c>MicroFx:Security</c>.</summary>
public sealed class SecurityOptions
{
    /// <summary>OIDC authority issuing tokens. Required unless <see cref="Enabled"/> is false.</summary>
    public string? Authority { get; set; }

    /// <summary>Accepted audiences. A token for another audience is rejected.</summary>
    public IList<string> Audiences { get; } = [];

    /// <summary>Accepted issuers. Defaults to <see cref="Authority"/> when empty.</summary>
    public IList<string> Issuers { get; } = [];

    /// <summary>
    /// Whether authentication is wired up. Off only for a service with no callers to authenticate;
    /// startup validation refuses this outside Development to prevent an accidental open service.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether every endpoint requires an authenticated caller unless it opts out with
    /// <c>AllowAnonymous</c>. Deny by default; turning this off is a deliberate, auditable act.
    /// </summary>
    public bool RequireAuthenticatedUser { get; set; } = true;

    /// <summary>
    /// Clock skew tolerated when validating token lifetimes. Five minutes is the framework default
    /// and is far too generous — it extends the usable life of a revoked or stolen token.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether HTTPS metadata is required when fetching signing keys. Disabling it permits a
    /// man-in-the-middle to supply their own signing keys, so it is refused outside Development.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Named scope policies: policy name to required scope value.</summary>
    public IDictionary<string, string> ScopePolicies { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Maximum accepted token size. Bounds parser work on unauthenticated input.</summary>
    [Range(512, 64 * 1024)]
    public int MaximumTokenSizeBytes { get; set; } = 8 * 1024;
}

/// <summary>
/// Authentication, deny-by-default authorization, and the security audit stream.
/// </summary>
/// <remarks>
/// The posture is closed: every endpoint requires an authenticated caller unless it explicitly opts
/// out, tokens are validated on every dimension the library offers, and both authentication failure
/// and authorization denial produce audit records.
/// </remarks>
public sealed class SecurityFeature : IMicroFxFeature, IPipelineFeature, IFeatureValidator
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Security,
        DisplayName = "Security",
        Order = 200,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.Api],
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Security",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new SecurityOptions();
        context.Configuration.GetSection("MicroFx:Security").Bind(options);
        context.AddValidatedOptions<SecurityOptions>();

        context.Services.TryAddSingleton<IAuditSink, LoggerAuditSink>();
        context.Services.TryAddSingleton<AuditRecorder>();

        if (!options.Enabled)
        {
            context.Report("auth", "disabled");
            return;
        }

        context.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;   // keep original claim names; do not rewrite to legacy URIs
                jwt.SaveToken = false;          // no need to keep the bearer token in memory after validation

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = options.Audiences.Count > 0,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = options.ClockSkew,
                    ValidAudiences = options.Audiences,
                    ValidIssuers = options.Issuers.Count > 0
                        ? options.Issuers
                        : options.Authority is null ? null : [options.Authority],

                    // 'none' must never be accepted, and neither should a symmetric algorithm on a
                    // bearer token validated against a published JWKS.
                    ValidAlgorithms =
                    [
                        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384,
                        SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256,
                        SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
                        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384,
                        SecurityAlgorithms.RsaSsaPssSha512,
                    ],
                };

                jwt.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = async failure =>
                    {
                        var recorder = failure.HttpContext.RequestServices
                            .GetRequiredService<AuditRecorder>();

                        await recorder.RecordAsync(
                            AuditEventKind.AuthenticationFailed,
                            failure.HttpContext,
                            // The exception type, never its message: token-validation messages can
                            // echo token contents back into the audit stream.
                            failure.Exception.GetType().Name,
                            failure.HttpContext.RequestAborted).ConfigureAwait(false);
                    },

                    OnChallenge = context1 =>
                    {
                        // Suppress the default WWW-Authenticate detail, which describes exactly why
                        // validation failed and is a free oracle for token forgery attempts.
                        context1.HandleResponse();
                        context1.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context1.Response.Headers.WWWAuthenticate = "Bearer";
                        return Task.CompletedTask;
                    },
                };
            });

        ConfigureAuthorization(context, options);

        context.Report("auth", "jwt");
        context.Report("authority", options.Authority ?? "not configured");
        context.Report("policy", options.RequireAuthenticatedUser ? "deny-by-default" : "allow-by-default");
    }

    /// <inheritdoc />
    public void UsePipeline(FeaturePipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Application.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value;

        if (!options.Enabled)
        {
            return;
        }

        context.Use(PipelineStage.Authentication, app => app.UseAuthentication());
        context.Use(PipelineStage.Authorization, app => app.UseAuthorization());

        // Records denials after authorization has run. A 403 that leaves no trace is invisible to
        // the people whose job is noticing them.
        context.Use(PipelineStage.Authorization, app => app.Use(async (httpContext, nextMiddleware) =>
        {
            await nextMiddleware().ConfigureAwait(false);

            if (httpContext.Response.StatusCode == StatusCodes.Status403Forbidden)
            {
                await httpContext.RequestServices
                    .GetRequiredService<AuditRecorder>()
                    .RecordAsync(
                        AuditEventKind.AuthorizationDenied, httpContext, "forbidden",
                        httpContext.RequestAborted)
                    .ConfigureAwait(false);
            }
        }));
    }

    /// <inheritdoc />
    public ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value;

        if (context.Metadata.IsDevelopment)
        {
            return ValueTask.FromResult(ValidationReport.Ok());
        }

        var findings = new List<ValidationFinding>();

        // Each of these is a way to ship an unintentionally open service. Failing startup is far
        // cheaper than discovering it from an incident.
        if (!options.Enabled)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                "Authentication is disabled outside Development. Set MicroFx:Security:Enabled=true, " +
                "or disable the security feature explicitly if this service genuinely has no callers."));
        }
        else if (string.IsNullOrWhiteSpace(options.Authority))
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                "MicroFx:Security:Authority is not configured; tokens cannot be validated."));
        }

        if (options.Enabled && !options.RequireHttpsMetadata)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                "RequireHttpsMetadata is false outside Development. Signing keys would be fetched " +
                "over plaintext, allowing an attacker to supply their own."));
        }

        if (options.Enabled && !options.RequireAuthenticatedUser)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                "Deny-by-default authorization is off; endpoints are anonymous unless they opt in."));
        }

        if (options.Enabled && options.Audiences.Count == 0)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                "No audiences configured; a token minted for any audience of this issuer is accepted."));
        }

        if (options.ClockSkew > TimeSpan.FromMinutes(2))
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                $"Clock skew of {options.ClockSkew.TotalSeconds:0}s extends the usable life of an " +
                "expired token."));
        }

        return ValueTask.FromResult(
            findings.Count == 0 ? ValidationReport.Ok() : ValidationReport.FromFindings(findings));
    }

    private static void ConfigureAuthorization(FeatureBuildContext context, SecurityOptions options)
    {
        var authorization = context.Services.AddAuthorizationBuilder();

        if (options.RequireAuthenticatedUser)
        {
            // The fallback policy applies to every endpoint with no authorization metadata of its
            // own, which is what makes "forgot to secure it" impossible rather than merely unlikely.
            authorization.SetFallbackPolicy(
                new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        }

        foreach (var (policyName, requiredScope) in options.ScopePolicies)
        {
            authorization.AddPolicy(policyName, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(handler => HasScope(handler.User, requiredScope)));
        }
    }

    /// <summary>Checks the space-delimited <c>scope</c> claim for an exact scope value.</summary>
    private static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string requiredScope)
    {
        foreach (var claim in user.FindAll("scope"))
        {
            // Exact segment match. A substring check would let "orders:read-only" satisfy
            // "orders:read", which is a quietly wrong authorization decision.
            foreach (var granted in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(granted, requiredScope, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
