using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MicroFx.Host.Service.E2E.Tests;

/// <summary>
/// End-to-end coverage of the HTTP pipeline: problem details, validation, security headers,
/// correlation, idempotency, and rate limiting.
/// </summary>
[TestFixture]
internal sealed class HttpPipelineEndToEndTests
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

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static StringContent ValidOrder() =>
        Json("""{"sku":"ABC-123","quantity":2,"currency":"GBP"}""");

    // ---- Validation ---------------------------------------------------------------------------

    [Test]
    public async Task A_valid_request_is_accepted()
    {
        var response = await _client.PostAsync(new Uri("/v1/orders", UriKind.Relative), ValidOrder());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task An_invalid_request_returns_problem_details_with_per_field_errors()
    {
        var response = await _client.PostAsync(
            new Uri("/v1/orders", UriKind.Relative),
            Json("""{"sku":"bad sku!","quantity":0,"currency":"pounds"}"""));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = document.RootElement.GetProperty("errors");

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo("application/problem+json"));
            Assert.That(errors.TryGetProperty("Sku", out _), Is.True);
            Assert.That(errors.TryGetProperty("Quantity", out _), Is.True);
            Assert.That(errors.TryGetProperty("Currency", out _), Is.True);
        });
    }

    [Test]
    public async Task Every_problem_response_carries_a_trace_id()
    {
        // The trace id is what lets an operator find the request without the response carrying
        // anything an attacker can use.
        var response = await _client.PostAsync(
            new Uri("/v1/orders", UriKind.Relative), Json("""{"sku":"","quantity":0,"currency":""}"""));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(document.RootElement.GetProperty("traceId").GetString(), Is.Not.Empty);
    }

    // ---- Security headers ---------------------------------------------------------------------

    [TestCase("X-Content-Type-Options", "nosniff")]
    [TestCase("X-Frame-Options", "DENY")]
    [TestCase("Referrer-Policy", "no-referrer")]
    public async Task Security_headers_are_applied(string header, string expected)
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.That(response.Headers.GetValues(header).Single(), Is.EqualTo(expected));
    }

    [Test]
    public async Task The_content_security_policy_denies_everything_by_default()
    {
        // An API returns data, not markup. Denying everything makes an accidentally-served HTML
        // error page inert.
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.That(
            response.Headers.GetValues("Content-Security-Policy").Single(),
            Does.Contain("default-src 'none'"));
    }

    [Test]
    public async Task Fingerprinting_headers_are_not_emitted()
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Multiple(() =>
        {
            Assert.That(response.Headers.Contains("X-Powered-By"), Is.False);
            Assert.That(response.Headers.Contains("Server"), Is.False);
        });
    }

    // ---- Correlation --------------------------------------------------------------------------

    [Test]
    public async Task A_supplied_correlation_id_is_echoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Add("X-Correlation-Id", "my-trace-1");

        var response = await _client.SendAsync(request);

        Assert.That(response.Headers.GetValues("X-Correlation-Id").Single(), Is.EqualTo("my-trace-1"));
    }

    [Test]
    public async Task A_missing_correlation_id_is_generated()
    {
        var response = await _client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.That(response.Headers.GetValues("X-Correlation-Id").Single(), Is.Not.Empty);
    }

    [TestCase("has space")]
    [TestCase("has\ttab")]
    [TestCase("semi;colon")]
    [TestCase("<script>")]
    public async Task A_malformed_correlation_id_is_replaced_rather_than_echoed(string malformed)
    {
        // The inbound header reaches logs and response headers, so echoing it unvalidated is a
        // log-injection and response-splitting vector.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", malformed);

        var response = await _client.SendAsync(request);

        Assert.That(response.Headers.GetValues("X-Correlation-Id").Single(), Is.Not.EqualTo(malformed));
    }

    [Test]
    public async Task An_over_long_correlation_id_is_replaced()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", new string('a', 500));

        var response = await _client.SendAsync(request);

        Assert.That(response.Headers.GetValues("X-Correlation-Id").Single(), Has.Length.LessThan(500));
    }

    // ---- Idempotency --------------------------------------------------------------------------

    [Test]
    public async Task Replaying_a_request_with_the_same_key_returns_the_original_response()
    {
        var key = Guid.NewGuid().ToString("N");

        var first = await PostWithKeyAsync(key, ValidOrder());
        var second = await PostWithKeyAsync(key, ValidOrder());

        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second.StatusCode, Is.EqualTo(first.StatusCode));
            // Identical bodies prove the original response was replayed, not that the handler ran
            // again and happened to produce something similar — the order id is a fresh GUID.
            Assert.That(secondBody, Is.EqualTo(firstBody));
            Assert.That(second.Headers.Contains("Idempotency-Replayed"), Is.True);
        });
    }

    [Test]
    public async Task Reusing_a_key_with_a_different_payload_is_a_conflict()
    {
        // Answering with the recorded response here would be actively wrong: the caller asked a
        // different question.
        var key = Guid.NewGuid().ToString("N");

        await PostWithKeyAsync(key, ValidOrder());
        var second = await PostWithKeyAsync(
            key, Json("""{"sku":"XYZ-999","quantity":7,"currency":"USD"}"""));

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [TestCase("has space")]
    [TestCase("semi;colon")]
    [TestCase("<script>")]
    public async Task A_malformed_idempotency_key_is_rejected(string key)
    {
        var response = await PostWithKeyAsync(key, ValidOrder());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task A_failed_request_is_not_recorded_for_replay()
    {
        // Recording a failure would pin it for the whole retention window, making the retry that
        // would have succeeded impossible.
        var key = Guid.NewGuid().ToString("N");
        var invalid = Json("""{"sku":"","quantity":0,"currency":""}""");

        var first = await PostWithKeyAsync(key, invalid);
        var second = await PostWithKeyAsync(key, invalid);

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(second.Headers.Contains("Idempotency-Replayed"), Is.False);
        });
    }

    [Test]
    public async Task A_safe_method_is_unaffected_by_idempotency()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/v1/orders/abc123", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", "not applicable here");

        var response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---- Caching ------------------------------------------------------------------------------

    [Test]
    public async Task A_cached_read_is_stable_across_requests()
    {
        // Same key, so the second request is served from L1 rather than recomputed.
        var first = await _client.GetFromJsonAsync<JsonElement>(
            new Uri("/v1/orders/cache01", UriKind.Relative));
        var second = await _client.GetFromJsonAsync<JsonElement>(
            new Uri("/v1/orders/cache01", UriKind.Relative));

        Assert.That(
            second.GetProperty("placedAt").GetDateTimeOffset(),
            Is.EqualTo(first.GetProperty("placedAt").GetDateTimeOffset()));
    }

    [Test]
    public async Task Different_cache_keys_produce_different_entries()
    {
        var a = await _client.GetFromJsonAsync<JsonElement>(
            new Uri("/v1/orders/cache02", UriKind.Relative));
        var b = await _client.GetFromJsonAsync<JsonElement>(
            new Uri("/v1/orders/cache03", UriKind.Relative));

        Assert.That(a.GetProperty("id").GetString(), Is.Not.EqualTo(b.GetProperty("id").GetString()));
    }

    // ---- Rate limiting ------------------------------------------------------------------------

    [Test]
    public async Task Exceeding_the_limit_returns_429_with_retry_after()
    {
        using var factory = new HostServiceFactory(new Dictionary<string, string?>
        {
            ["MicroFx:RateLimiting:PermitLimit"] = "3",
            ["MicroFx:RateLimiting:Window"] = "00:01:00",
        });

        using var client = factory.CreateClient();

        HttpResponseMessage? limited = null;
        for (var attempt = 0; attempt < 10 && limited is null; attempt++)
        {
            var response = await client.GetAsync(new Uri("/", UriKind.Relative));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        Assert.That(limited, Is.Not.Null, "The limiter never rejected a request.");
        Assert.That(limited!.Headers.RetryAfter, Is.Not.Null,
            "Retry-After turns a rejection into something a client can act on rather than hot-loop.");
    }

    [Test]
    public async Task Probes_are_never_rate_limited()
    {
        // Throttling readiness is how a traffic spike becomes an orchestrator-driven outage.
        using var factory = new HostServiceFactory(new Dictionary<string, string?>
        {
            ["MicroFx:RateLimiting:PermitLimit"] = "1",
        });

        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"attempt {attempt}");
        }
    }

    // ---- OpenAPI ------------------------------------------------------------------------------

    [Test]
    public async Task The_openapi_document_is_served_on_the_management_surface()
    {
        // The schema inventory is an operational artefact, not something the public port advertises.
        var response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private Task<HttpResponseMessage> PostWithKeyAsync(string key, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/orders", UriKind.Relative))
        {
            Content = content,
        };

        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return _client.SendAsync(request);
    }
}
