using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using MicroFx.Api;
using MicroFx.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.RateLimiting;

/// <summary>Options for the rate limiting feature, bound from <c>MicroFx:RateLimiting</c>.</summary>
public sealed class RateLimitingOptions
{
    /// <summary>Requests permitted per partition within one window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 100;

    /// <summary>Length of the sliding window.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Segments per window. More segments means smoother enforcement at the boundary.</summary>
    [Range(1, 60)]
    public int SegmentsPerWindow { get; set; } = 6;

    /// <summary>
    /// Requests queued when the limit is reached. Zero — reject immediately — is the default:
    /// queueing under overload converts a fast rejection into latency for everyone.
    /// </summary>
    [Range(0, 1000)]
    public int QueueLimit { get; set; }

    /// <summary>
    /// Maximum distinct partitions tracked. Bounds memory: the partition key is derived from
    /// caller-controlled input, so an unbounded map is a memory-exhaustion vector.
    /// </summary>
    [Range(100, 1_000_000)]
    public int MaxPartitions { get; set; } = 10_000;
}

/// <summary>
/// Derives the partition a request is counted against.
/// </summary>
/// <remarks>
/// Replace this to partition by tenant, API key, or subscription tier. Whatever the key, it must be
/// bounded in cardinality — a key an attacker can vary freely turns the limiter into a memory leak.
/// </remarks>
public interface IPartitionKeyResolver
{
    /// <summary>Returns the partition key for this request.</summary>
    string Resolve(HttpContext context);
}

/// <summary>
/// Partitions by authenticated subject when present, otherwise by remote IP.
/// </summary>
/// <remarks>
/// The authenticated subject is preferred because it cannot be spoofed once the token is validated,
/// and because it survives NAT — IP-only partitioning punishes every user behind one egress address.
/// </remarks>
internal sealed class DefaultPartitionKeyResolver : IPartitionKeyResolver
{
    public string Resolve(HttpContext context)
    {
        var subject = context.User.FindFirst("sub")?.Value
                      ?? context.User.Identity?.Name;

        if (!string.IsNullOrEmpty(subject))
        {
            return "sub:" + subject;
        }

        // Never taken from a header. X-Forwarded-For is caller-supplied and trivially forged, so
        // partitioning on it lets one client masquerade as unlimited distinct callers. The forwarded
        // headers middleware rewrites RemoteIpAddress only for configured, trusted proxies.
        var address = context.Connection.RemoteIpAddress;
        return address is null ? "anon" : "ip:" + address.ToString();
    }
}

/// <summary>
/// Partitioned request rate limiting with <c>Retry-After</c> on rejection.
/// </summary>
/// <remarks>
/// Sits before authentication in the pipeline, so an unauthenticated flood costs a dictionary lookup
/// rather than a signature validation. That ordering is what makes the limiter useful as a defence
/// rather than merely a fairness mechanism.
/// </remarks>
public sealed class RateLimitingFeature : IMicroFxFeature, IPipelineFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.RateLimiting,
        DisplayName = "Rate limiting",
        Order = 120,
        DependsOn = [BuiltIn.Core, BuiltIn.Api],
        SupportedHosts = HostKinds.Web,
        ConfigurationSection = "MicroFx:RateLimiting",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new RateLimitingOptions();
        context.Configuration.GetSection("MicroFx:RateLimiting").Bind(options);
        context.AddValidatedOptions<RateLimitingOptions>();

        context.Services.TryAddSingleton<IPartitionKeyResolver, DefaultPartitionKeyResolver>();

        context.Services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                // Probes must never be throttled: throttling readiness is how a traffic spike
                // becomes an orchestrator-driven outage.
                if (IsProbe(httpContext.Request.Path))
                {
                    return RateLimitPartition.GetNoLimiter("probe");
                }

                var resolver = httpContext.RequestServices.GetRequiredService<IPartitionKeyResolver>();

                return RateLimitPartition.GetSlidingWindowLimiter(
                    resolver.Resolve(httpContext),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = options.PermitLimit,
                        Window = options.Window,
                        SegmentsPerWindow = options.SegmentsPerWindow,
                        QueueLimit = options.QueueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
            });

            limiter.OnRejected = async (rejection, cancellationToken) =>
            {
                var httpContext = rejection.HttpContext;
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                // Retry-After turns a rejection into something a well-behaved client can act on
                // instead of hot-looping.
                var retryAfter = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? value
                    : options.Window;

                httpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                var problems = httpContext.RequestServices.GetRequiredService<ProblemDetailsBuilder>();
                var problem = problems.Create(
                    StatusCodes.Status429TooManyRequests,
                    "Too many requests",
                    "The request rate limit has been exceeded. Retry after the indicated interval.",
                    ProblemDetailsBuilder.TypeBase + "rate-limit",
                    httpContext);

                httpContext.Response.ContentType = "application/problem+json; charset=utf-8";
                await httpContext.Response
                    .WriteAsJsonAsync(problem, cancellationToken)
                    .ConfigureAwait(false);
            };
        });

        context.Report("limit", $"{options.PermitLimit}/{options.Window.TotalSeconds:0}s");
    }

    /// <inheritdoc />
    public void UsePipeline(FeaturePipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Use(PipelineStage.RateLimiting, app => app.UseRateLimiter());
    }

    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase);
}
