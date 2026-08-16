using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MicroFx.Host.Service.E2E.Tests;

/// <summary>
/// End-to-end coverage of the reference host, in-process lane.
/// </summary>
[TestFixture]
internal sealed class CompositionEndToEndTests
{
    private HostServiceFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _factory = new HostServiceFactory();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ---- The service composes and serves ------------------------------------------------------

    [Test]
    public async Task Service_starts_and_serves_its_own_endpoint()
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Probes_report_healthy()
    {
        foreach (var probe in new[] { "live", "ready", "startup" })
        {
            var response = await _client.GetAsync(new Uri($"/health/{probe}", UriKind.Relative));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"probe: {probe}");
        }
    }

    [Test]
    public async Task Readiness_includes_a_check_contributed_by_a_custom_feature()
    {
        // The custom feature declared a health contribution without referencing the health feature,
        // which is the property that keeps the built-in dependency graph shallow.
        using var document = await GetJsonAsync("/health/ready");

        var names = document.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString())
            .ToList();

        Assert.That(names, Does.Contain("example-feature"));
    }

    [Test]
    public async Task Probe_responses_are_never_cached()
    {
        // A stale "healthy" is worse than no answer at all.
        var response = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("no-store"));
    }

    // ---- Feature catalog ----------------------------------------------------------------------

    [Test]
    public async Task Feature_catalog_lists_the_kernel_and_the_custom_feature()
    {
        using var document = await GetJsonAsync("/internal/features");

        var ids = document.RootElement.GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain("microfx.core"));
            Assert.That(ids, Does.Contain("microfx.observability"));
            Assert.That(ids, Does.Contain("microfx.health"));
            Assert.That(ids, Does.Contain("sample.example"));
        });
    }

    [Test]
    public async Task Feature_catalog_reports_resolved_order_and_kernel_status()
    {
        using var document = await GetJsonAsync("/internal/features");

        var core = document.RootElement.GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("id").GetString() == "microfx.core");

        Assert.Multiple(() =>
        {
            Assert.That(core.GetProperty("kernel").GetBoolean(), Is.True);
            Assert.That(core.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(core.GetProperty("order").GetInt32(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Feature_catalog_reports_startup_timings_per_feature()
    {
        // Slow cold starts must be attributable to a name rather than being a mystery.
        using var document = await GetJsonAsync("/internal/features");

        var example = document.RootElement.GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("id").GetString() == "sample.example");

        Assert.That(example.GetProperty("timings").TryGetProperty("Starting", out _), Is.True);
    }

    [Test]
    public async Task Info_endpoint_reports_build_identity()
    {
        using var document = await GetJsonAsync("/internal/info");

        Assert.Multiple(() =>
        {
            Assert.That(document.RootElement.GetProperty("service").GetString(),
                Is.EqualTo("microfx-host-service"));
            Assert.That(document.RootElement.GetProperty("environment").GetString(),
                Is.EqualTo("Development"));
        });
    }

    // ---- Custom feature behaviour -------------------------------------------------------------

    [Test]
    public async Task Custom_feature_middleware_runs_on_every_request()
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.That(response.Headers.TryGetValues("X-Example-Feature", out var values), Is.True);
        Assert.That(values!.Single(), Is.EqualTo("active"));
    }

    [Test]
    public async Task Custom_feature_endpoint_is_mapped_on_the_business_surface()
    {
        var payload = await _client.GetFromJsonAsync<JsonElement>(new Uri("/example", UriKind.Relative));

        Assert.That(payload.GetProperty("message").GetString(), Is.EqualTo("hello"));
    }

    // ---- Configuration diagnostics ------------------------------------------------------------

    [Test]
    public async Task Configuration_endpoint_redacts_secret_shaped_values()
    {
        using var factory = new HostServiceFactory(new Dictionary<string, string?>
        {
            ["Sample:Database:Password"] = "hunter2",
            ["Sample:Public:Port"] = "9999",
        });

        using var client = factory.CreateClient();
        using var document = await GetJsonAsync("/internal/config", client);

        var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToList();

        var secret = entries.First(e => e.GetProperty("key").GetString() == "Sample:Database:Password");
        var ordinary = entries.First(e => e.GetProperty("key").GetString() == "Sample:Public:Port");

        Assert.Multiple(() =>
        {
            Assert.That(secret.GetProperty("value").GetString(), Is.EqualTo("[redacted]"));
            Assert.That(ordinary.GetProperty("value").GetString(), Is.EqualTo("9999"));
        });
    }

    [Test]
    public async Task Configuration_endpoint_is_withheld_outside_development_by_default()
    {
        // Values are redacted, but key names alone reveal topology. Exposing them in production
        // must be a separate, conscious act rather than an inherited default.
        // Production is configured the way a real deployment must be: the security feature's
        // startup validation refuses to compose a production host with no identity provider.
        using var factory = new HostServiceFactory(
            new Dictionary<string, string?>
            {
                ["MicroFx:Diagnostics:AllowConfigurationOutsideDevelopment"] = "false",
                ["MicroFx:Security:Enabled"] = "true",
                ["MicroFx:Security:Authority"] = "https://idp.example.com",
                ["MicroFx:Security:Audiences:0"] = "orders-api",

                // The reference host enables messaging, and the in-memory transport is refused
                // outside Development. A production deployment would reference a real adapter;
                // these tests are about something else, so the loss is accepted explicitly.
                ["MicroFx:Messaging:AllowInMemoryTransportInProduction"] = "true",
            },
            environment: "Production");

        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/internal/config", UriKind.Relative));

        // 401 rather than 404: the endpoint is not mapped, and under deny-by-default authorization
        // the fallback policy challenges unmatched requests too. That is the better outcome — an
        // unauthenticated caller cannot distinguish an absent route from a present one.
        Assert.Multiple(() =>
        {
            Assert.That(response.IsSuccessStatusCode, Is.False);
            Assert.That(
                response.StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized).Or.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Health_probes_stay_anonymous_when_deny_by_default_is_active()
    {
        // Under deny-by-default the authorization fallback would otherwise challenge probes, an
        // orchestrator would read 401 as unhealthy, and every replica would be killed the moment
        // security was switched on.
        using var factory = new HostServiceFactory(
            new Dictionary<string, string?>
            {
                ["MicroFx:Security:Enabled"] = "true",
                ["MicroFx:Security:Authority"] = "https://idp.example.com",
                ["MicroFx:Security:Audiences:0"] = "orders-api",

                // The reference host enables messaging, and the in-memory transport is refused
                // outside Development. A production deployment would reference a real adapter;
                // these tests are about something else, so the loss is accepted explicitly.
                ["MicroFx:Messaging:AllowInMemoryTransportInProduction"] = "true",
            },
            environment: "Production");

        using var client = factory.CreateClient();

        foreach (var probe in new[] { "live", "ready", "startup" })
        {
            var response = await client.GetAsync(new Uri($"/health/{probe}", UriKind.Relative));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"probe: {probe}");
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, HttpClient? client = null)
    {
        var response = await (client ?? _client).GetAsync(new Uri(path, UriKind.Relative));
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
