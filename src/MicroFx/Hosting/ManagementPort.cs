using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace MicroFx.Hosting;

/// <summary>
/// Rejects management endpoints reached on any port other than the management port.
/// </summary>
/// <remarks>
/// Responds 404 rather than 403: a probe should not learn that a diagnostics endpoint exists but is
/// barred from this port. Requests arriving with no real port — the in-memory test server — are
/// allowed, because that server has no network surface to protect.
/// </remarks>
internal sealed class ManagementPortFilter(int managementPort) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var localPort = context.HttpContext.Connection.LocalPort;

        return localPort is 0 || localPort == managementPort
            ? next(context)
            : ValueTask.FromResult<object?>(Results.NotFound());
    }
}

/// <summary>Binds the traffic and management ports as separate Kestrel listeners.</summary>
internal sealed class ManagementListenerSetup(MicroFxHostOptions options)
    : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions options1)
    {
        ArgumentNullException.ThrowIfNull(options1);
        options1.ListenAnyIP(options.TrafficPort);
        options1.ListenAnyIP(options.ManagementPort);
    }
}
