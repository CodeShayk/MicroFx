using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MicroFx.Host.Service.E2E.Tests;

/// <summary>
/// Hosts the reference service in-process over its real middleware pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This is the fast lane. It exercises composition, routing, and behaviour in milliseconds, but it
/// runs on the in-memory test server and so cannot observe anything that depends on a real socket —
/// management-port isolation, container packaging, or process-level shutdown. Those belong to the
/// containerised lane, and to <see cref="ManagementPortIsolationTests"/> which binds real ports.
/// </para>
/// <para>
/// <b>Why configuration goes through environment variables, and why the window is so narrow.</b>
/// With the minimal-hosting model, <c>ConfigureWebHost</c> callbacks are applied <em>after</em>
/// <c>WebApplication.CreateBuilder</c> has run in <c>Program.cs</c> — and therefore after
/// <c>AddMicroFx</c> has resolved the feature graph and every feature has bound its options.
/// Settings supplied that way are visible at request time but not at composition time, which fails
/// silently for anything the platform reads while composing. Environment variables are read by
/// <c>CreateBuilder</c> itself, so they are the only mechanism that reaches composition.
/// </para>
/// <para>
/// They are also process-global, and <see cref="WebApplicationFactory{TEntryPoint}"/> builds its
/// host <em>lazily</em> on first use. Setting them in the constructor and restoring them on dispose
/// therefore leaves a window in which another factory's settings are the ones in force when this
/// host finally builds. So the host is built <b>eagerly, inside the constructor</b>, under a lock,
/// and the variables are restored the moment the build completes.
/// </para>
/// </remarks>
internal sealed class HostServiceFactory : WebApplicationFactory<Program>
{
    private static readonly Lock BuildLock = new();

    private readonly Dictionary<string, string?> _configuration;
    private readonly string _environment;

    public HostServiceFactory(
        Dictionary<string, string?>? configuration = null,
        string? environment = null)
    {
        _configuration = configuration ?? [];
        _environment = environment ?? Environments.Development;

        // One factory builds at a time, and the environment is restored before the lock is
        // released, so no two hosts can ever read each other's settings.
        lock (BuildLock)
        {
            var previous = new Dictionary<string, string?>(StringComparer.Ordinal);

            try
            {
                Set(previous, "ASPNETCORE_ENVIRONMENT", _environment);

                // The test server owns its own transport, so MicroFx must not bind Kestrel listeners.
                Set(previous, "MicroFx__Host__ConfigureListeners", "false");

                // A deterministic baseline. Polling in an asynchronous test would otherwise trip a
                // request limit that exists for a different test's benefit.
                Set(previous, "MicroFx__RateLimiting__PermitLimit", "100000");
                Set(previous, "MicroFx__Messaging__SchedulerInterval", "00:00:00.100");

                foreach (var (key, value) in _configuration)
                {
                    Set(previous, ToEnvironmentKey(key), value);
                }

                // Forces the host to build now, while these variables are the ones in force.
                _ = Services;
            }
            finally
            {
                foreach (var (key, value) in previous)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(_environment);

        // Applied again for anything read after the host is built.
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(_configuration));
    }

    /// <summary>Converts <c>A:B:C</c> into the <c>A__B__C</c> form the environment provider reads.</summary>
    private static string ToEnvironmentKey(string key) =>
        key.Replace(":", "__", StringComparison.Ordinal);

    private static void Set(Dictionary<string, string?> previous, string key, string? value)
    {
        previous.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }
}
