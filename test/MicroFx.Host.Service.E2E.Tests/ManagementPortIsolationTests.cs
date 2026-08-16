using System.Net;
using System.Net.Sockets;
using MicroFx.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MicroFx.Host.Service.E2E.Tests;

/// <summary>
/// Verifies that health and diagnostic endpoints are unreachable from the traffic port.
/// </summary>
/// <remarks>
/// This fixture binds real Kestrel sockets rather than using the in-memory test server, because
/// port isolation is precisely the property the in-memory server cannot express — it has no ports.
/// The most common security defect in this class of framework is a diagnostics endpoint that turns
/// out to be internet-reachable, so it is worth the cost of real sockets to assert it.
/// </remarks>
[TestFixture]
internal sealed class ManagementPortIsolationTests
{
    private WebApplication _app = null!;
    private HttpClient _traffic = null!;
    private HttpClient _management = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        var (trafficPort, managementPort) = FreePortPair();

        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MicroFx:Service:Name"] = "isolation-tests",

            // Stated explicitly: another fixture's factory may still be alive and have set
            // MicroFx__Host__ConfigureListeners=false in the environment. In-memory configuration
            // is added after the environment source, so this wins.
            ["MicroFx:Host:ConfigureListeners"] = "true",
            ["MicroFx:Host:TrafficPort"] = trafficPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MicroFx:Host:ManagementPort"] = managementPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        builder.AddMicroFx(fx => fx.DisableAssemblyScanning());

        _app = builder.Build();
        _app.MapGet("/business", () => Results.Ok("business"));
        _app.UseMicroFx();

        await _app.StartAsync();

        _traffic = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{trafficPort}") };
        _management = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{managementPort}") };
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        _traffic.Dispose();
        _management.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Test]
    public async Task Health_probes_are_served_on_the_management_port()
    {
        var response = await _management.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase("/health/live")]
    [TestCase("/health/ready")]
    [TestCase("/health/startup")]
    [TestCase("/internal/info")]
    [TestCase("/internal/features")]
    [TestCase("/internal/config")]
    public async Task Management_endpoints_are_unreachable_from_the_traffic_port(string path)
    {
        var response = await _traffic.GetAsync(new Uri(path, UriKind.Relative));

        // 404 rather than 403: a probe should not learn that the endpoint exists but is barred.
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Business_endpoints_are_served_on_the_traffic_port()
    {
        var response = await _traffic.GetAsync(new Uri("/business", UriKind.Relative));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>
    /// Reserves two distinct ephemeral ports.
    /// </summary>
    /// <remarks>
    /// Both listeners are held open together before either is released. Reserving them one at a
    /// time lets the operating system hand back the same port twice — which makes the traffic and
    /// management ports identical and the isolation assertions pass or fail at random.
    /// </remarks>
    private static (int Traffic, int Management) FreePortPair()
    {
        using var first = new TcpListener(IPAddress.Loopback, 0);
        using var second = new TcpListener(IPAddress.Loopback, 0);

        first.Start();
        second.Start();

        var traffic = ((IPEndPoint)first.LocalEndpoint).Port;
        var management = ((IPEndPoint)second.LocalEndpoint).Port;

        first.Stop();
        second.Stop();

        Assert.That(traffic, Is.Not.EqualTo(management));
        return (traffic, management);
    }
}
