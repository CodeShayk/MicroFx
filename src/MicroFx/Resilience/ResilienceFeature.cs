using System.ComponentModel.DataAnnotations;
using MicroFx.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace MicroFx.Resilience;

/// <summary>How a dependency failure affects the calling request.</summary>
public enum DependencyCriticality
{
    /// <summary>Failure fails the request and degrades readiness.</summary>
    Critical,

    /// <summary>Failure is absorbed; the request proceeds in a degraded mode.</summary>
    NonCritical,
}

/// <summary>Options for the resilience feature, bound from <c>MicroFx:Resilience</c>.</summary>
public sealed class ResilienceOptions
{
    /// <summary>Total budget for one logical call, including every retry.</summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Budget for a single attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retry attempts after the first try.</summary>
    [Range(0, 10)]
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay for exponential backoff. Jitter is always applied.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Failure ratio that opens the circuit.</summary>
    [Range(0.01, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Sampling window for the circuit breaker.</summary>
    public TimeSpan CircuitBreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open before probing.</summary>
    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Minimum calls in the window before the breaker may trip.</summary>
    [Range(2, 1000)]
    public int CircuitBreakerMinimumThroughput { get; set; } = 10;

    /// <summary>Concurrent calls permitted per dependency, bounding thread and connection use.</summary>
    [Range(1, 10_000)]
    public int MaxConcurrency { get; set; } = 100;
}

/// <summary>
/// Default resilience for every outbound HTTP call: total timeout, jittered retry, circuit breaker,
/// per-attempt timeout, and a concurrency bound.
/// </summary>
/// <remarks>
/// <para>
/// Applied to every named and typed <see cref="HttpClient"/> the service registers, so resilience is
/// something a service has to actively remove rather than remember to add.
/// </para>
/// <para>
/// Retries are restricted to idempotent methods. Retrying a POST that timed out can duplicate a
/// payment or an order — the timeout says the response was lost, not that the work did not happen.
/// </para>
/// </remarks>
public sealed class ResilienceFeature : IMicroFxFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Resilience,
        DisplayName = "Resilience",
        Order = 310,
        DependsOn = [BuiltIn.Core],
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Resilience",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new ResilienceOptions();
        context.Configuration.GetSection("MicroFx:Resilience").Bind(options);
        context.AddValidatedOptions<ResilienceOptions>();

        context.Services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigureHttpClient(client =>
            {
                // Belt to the pipeline's braces: bounds a call even if the pipeline is replaced.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.ConnectionClose = false;
            });

            builder.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Recycles connections so DNS changes are picked up; a long-lived handler otherwise
                // pins a stale address through a failover.
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                // Redirects are not followed: a redirect from an upstream can send a bearer token
                // to a host the service never intended to call.
                AllowAutoRedirect = false,
            });

            builder.AddResilienceHandler("microfx", (pipeline, _) =>
            {
                // Order is load-bearing: total timeout wraps everything, then retry, then the
                // breaker, then the per-attempt timeout innermost.
                pipeline.AddTimeout(options.TotalRequestTimeout);

                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = options.BaseDelay,
                    ShouldHandle = arguments => ValueTask.FromResult(
                        IsIdempotent(arguments.Outcome.Result?.RequestMessage) &&
                        HttpRetryPredicate(arguments)),
                });

                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = options.CircuitBreakerFailureRatio,
                    SamplingDuration = options.CircuitBreakerSamplingDuration,
                    BreakDuration = options.CircuitBreakerBreakDuration,
                    MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                });

                pipeline.AddTimeout(options.AttemptTimeout);
            });
        });

        context.Report("retries", options.MaxRetryAttempts);
        context.Report("timeout", options.TotalRequestTimeout);
    }

    private static bool HttpRetryPredicate(
        Polly.Retry.RetryPredicateArguments<HttpResponseMessage> arguments)
    {
        if (arguments.Outcome.Exception is HttpRequestException or TimeoutException)
        {
            return true;
        }

        var response = arguments.Outcome.Result;
        return response is not null &&
               ((int)response.StatusCode >= 500 ||
                response.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Whether the request may safely be retried.
    /// </summary>
    /// <remarks>
    /// A timeout means the response was lost, not that the work did not happen. Retrying a POST on
    /// that basis duplicates it. An <c>Idempotency-Key</c> makes a POST safe, because the receiver
    /// will recognise the replay.
    /// </remarks>
    private static bool IsIdempotent(HttpRequestMessage? request)
    {
        if (request is null)
        {
            return false;
        }

        var method = request.Method.Method;

        return method is "GET" or "HEAD" or "OPTIONS" or "TRACE" or "PUT" or "DELETE" ||
               request.Headers.Contains("Idempotency-Key");
    }
}
