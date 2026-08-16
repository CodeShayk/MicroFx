using MicroFx.Api;
using MicroFx.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.ServiceClients;

/// <summary>Options for the service clients feature, bound from <c>MicroFx:ServiceClients</c>.</summary>
public sealed class ServiceClientOptions
{
    /// <summary>Logical service name to base address. Endpoints are configuration, never hard-coded.</summary>
    public IDictionary<string, Uri> Endpoints { get; } =
        new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the caller's bearer token is forwarded to downstream services.
    /// </summary>
    /// <remarks>
    /// Off by default. Forwarding a token sends the caller's credential to another host, which is
    /// only safe when that host is in the same trust domain and the token's audience covers it.
    /// Enabling it is a deliberate decision about trust, not a convenience toggle.
    /// </remarks>
    public bool ForwardBearerToken { get; set; }

    /// <summary>Services permitted to receive a forwarded token, when forwarding is enabled.</summary>
    public IList<string> TokenForwardingAllowList { get; } = [];
}

/// <summary>Resolves a logical service name to a base address.</summary>
public interface IServiceEndpointResolver
{
    /// <summary>Returns the base address, or null when the service is unknown.</summary>
    Uri? Resolve(string serviceName);
}

internal sealed class ConfigurationServiceEndpointResolver(
    Microsoft.Extensions.Options.IOptions<ServiceClientOptions> options) : IServiceEndpointResolver
{
    public Uri? Resolve(string serviceName) =>
        options.Value.Endpoints.TryGetValue(serviceName, out var uri) ? uri : null;
}

/// <summary>
/// Propagates correlation, and optionally the caller's token, to downstream services.
/// </summary>
/// <remarks>
/// Correlation propagation is unconditional and harmless. Token propagation is gated on both a
/// global switch and a per-service allow list, because a handler that forwards credentials to
/// whatever host it is pointed at is one misconfiguration away from leaking them.
/// </remarks>
internal sealed class ServiceClientHandler(
    IHttpContextAccessor accessor,
    Microsoft.Extensions.Options.IOptions<ServiceClientOptions> options,
    string serviceName) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = accessor.HttpContext;
        if (context is null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var correlation) &&
            correlation is string correlationId)
        {
            request.Headers.TryAddWithoutValidation(
                CorrelationIdMiddleware.HeaderName, correlationId);
        }

        var settings = options.Value;
        var mayForward = settings.ForwardBearerToken &&
                         settings.TokenForwardingAllowList.Contains(serviceName, StringComparer.OrdinalIgnoreCase);

        if (mayForward && request.Headers.Authorization is null)
        {
            var inbound = context.Request.Headers.Authorization.ToString();
            if (inbound.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                request.Headers.TryAddWithoutValidation("Authorization", inbound);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Typed HTTP clients for service-to-service calls, preconfigured with resilience, telemetry, and
/// correlation propagation.
/// </summary>
public sealed class ServiceClientsFeature : IMicroFxFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.ServiceClients,
        DisplayName = "Service clients",
        Order = 330,
        DependsOn = [BuiltIn.Core, BuiltIn.Resilience],
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:ServiceClients",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new ServiceClientOptions();
        context.Configuration.GetSection("MicroFx:ServiceClients").Bind(options);
        context.AddValidatedOptions<ServiceClientOptions>();

        context.Services.AddHttpContextAccessor();
        context.Services.TryAddSingleton<IServiceEndpointResolver, ConfigurationServiceEndpointResolver>();

        context.Report("endpoints", options.Endpoints.Count);
        context.Report("forwardToken", options.ForwardBearerToken);
    }
}

/// <summary>Registration helpers for typed service clients.</summary>
public static class ServiceClientExtensions
{
    /// <summary>
    /// Registers a typed client bound to a logical service name, inheriting the platform's
    /// resilience pipeline, telemetry, and correlation propagation.
    /// </summary>
    /// <typeparam name="TClient">The typed client.</typeparam>
    /// <typeparam name="TImplementation">Its implementation.</typeparam>
    public static IHttpClientBuilder AddServiceClient<TClient, TImplementation>(
        this IServiceCollection services, string serviceName)
        where TClient : class
        where TImplementation : class, TClient
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        return services
            .AddHttpClient<TClient, TImplementation>(serviceName, (provider, client) =>
            {
                var resolver = provider.GetRequiredService<IServiceEndpointResolver>();

                client.BaseAddress = resolver.Resolve(serviceName)
                    ?? throw new InvalidOperationException(
                        $"No endpoint configured for service '{serviceName}'. Add " +
                        $"MicroFx:ServiceClients:Endpoints:{serviceName}.");
            })
            .AddHttpMessageHandler(provider => new ServiceClientHandler(
                provider.GetRequiredService<IHttpContextAccessor>(),
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceClientOptions>>(),
                serviceName));
    }
}
