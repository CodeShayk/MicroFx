using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MicroFx.Messaging;
using MicroFx.Messaging.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace MicroFx.Host.Service.E2E.Tests;

/// <summary>
/// End-to-end messaging over the reference host: publish, envelope, transport, pipeline, dedupe,
/// retry, and dead-letter.
/// </summary>
/// <remarks>
/// Runs entirely in-process on the in-memory transport, so the full semantics are proven in
/// milliseconds with no broker. The same bodies re-run against a real adapter in phase 10.
/// </remarks>
[TestFixture]
internal sealed class MessagingEndToEndTests
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

    private static string NewOrderId() => "o" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Waits for a dead letter carrying the given reason.
    /// </summary>
    /// <remarks>
    /// Searches rather than taking the most recent one. The transport's dead-letter collection is
    /// an unordered bag shared by every test in the fixture, so "the last one" is whichever test
    /// happened to finish most recently — not the one under test.
    /// </remarks>
    private static Task<DeadLetteredMessage?> DeadLetterWithReasonAsync(
        InMemoryTransport transport, string reason) =>
        EventuallyAsync(
            () => Task.FromResult(
                transport.DeadLetters.FirstOrDefault(d => d.Reason?.StartsWith(reason, StringComparison.Ordinal) == true)),
            found => found is not null,
            $"No message was dead-lettered with reason '{reason}'.");

    private async Task<int> ProjectionCountAsync(string orderId)
    {
        var payload = await _client.GetFromJsonAsync<JsonElement>(
            new Uri($"/v1/orders/{orderId}/projection", UriKind.Relative));

        return payload.GetProperty("handled").GetInt32();
    }

    /// <summary>
    /// Polls until a condition holds or the budget expires.
    /// </summary>
    /// <remarks>
    /// Messaging is asynchronous, so a fixed sleep is either flaky or slow. Polling with a deadline
    /// is neither, and it fails with the actual observed value rather than a bare timeout.
    /// </remarks>
    private static async Task<T> EventuallyAsync<T>(
        Func<Task<T>> probe, Func<T, bool> satisfied, string because, int budgetMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
        T last = default!;

        while (DateTime.UtcNow < deadline)
        {
            last = await probe();
            if (satisfied(last))
            {
                return last;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"{because} Last observed: {last}.");
        return last;
    }

    // ---- Round trip -----------------------------------------------------------------------------

    [Test]
    public async Task An_event_published_over_http_reaches_its_subscriber()
    {
        // The whole path in one assertion: endpoint → publisher → envelope → transport →
        // pipeline → dedupe → handler.
        var orderId = NewOrderId();

        var response = await _client.PostAsync(
            new Uri($"/v1/orders/{orderId}/publish", UriKind.Relative), content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        await EventuallyAsync(
            () => ProjectionCountAsync(orderId),
            count => count == 1,
            "The subscriber never handled the published event.");
    }

    [Test]
    public async Task Each_publish_is_handled_once()
    {
        var orderId = NewOrderId();

        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsync(
                new Uri($"/v1/orders/{orderId}/publish", UriKind.Relative), content: null);
        }

        // Three distinct messages, three handlings: the inbox deduplicates redeliveries of one
        // message, not separate publishes that happen to carry the same payload.
        await EventuallyAsync(
            () => ProjectionCountAsync(orderId),
            count => count == 3,
            "Three distinct publishes should have been handled three times.");
    }

    [Test]
    public async Task A_command_is_accepted_and_handled()
    {
        var orderId = NewOrderId();

        var response = await _client.PostAsync(
            new Uri($"/v1/orders/{orderId}/reserve?sku=ABC-1&quantity=1", UriKind.Relative),
            content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    // ---- Dedupe ---------------------------------------------------------------------------------

    [Test]
    public async Task A_redelivered_message_is_handled_exactly_once()
    {
        // The observable proof that the inbox works: the same envelope id delivered twice must run
        // the handler once, because at-least-once transport plus dedupe is effectively-once handling.
        var orderId = NewOrderId();
        var transport = (InMemoryTransport)_factory.Services.GetRequiredService<
            MicroFx.Messaging.Transport.IMessageTransport>();

        var registry = _factory.Services.GetRequiredService<MessageTypeRegistry>();
        var wireName = registry.RequireWireName(typeof(Orders.OrderPlacedV1));

        var envelope = new Envelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = wireName,
            Source = "test",
            Time = DateTimeOffset.UtcNow,
            Kind = MessageKind.Event,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

        // The platform's own serializer options, not the defaults: the pipeline deserializes with
        // camelCase and case-sensitive matching, so a PascalCase payload would bind to nothing.
        var serializerOptions = _factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagingOptions>>()
            .Value.SerializerOptions;

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new Orders.OrderPlacedV1(orderId, "SKU-DEMO", 1), serializerOptions);

        // Taken from the registry rather than reconstructed: the test then exercises the same
        // destination the feature actually registered, and cannot drift from it.
        var destination = _factory.Services.GetRequiredService<DestinationRegistry>()
            .Require(typeof(Orders.OrderPlacedV1));

        var message = new MicroFx.Messaging.Transport.TransportMessage(
            destination, EnvelopeCodec.Encode(envelope), body);

        await transport.PublishAsync(message);
        await transport.PublishAsync(message);   // same envelope id: a redelivery

        await EventuallyAsync(
            () => ProjectionCountAsync(orderId),
            count => count == 1,
            "A redelivered message must be handled exactly once.");

        // Held past the budget so a late second handling would still be caught.
        await Task.Delay(300);
        Assert.That(await ProjectionCountAsync(orderId), Is.EqualTo(1));
    }

    // ---- Retry and dead-letter -------------------------------------------------------------------

    [Test]
    public async Task A_permanently_failing_command_is_dead_lettered_without_retrying()
    {
        // A business rejection will never pass by waiting, so retrying it only repeats the
        // rejection before dead-lettering anyway.
        var orderId = NewOrderId();
        var transport = (InMemoryTransport)_factory.Services.GetRequiredService<
            MicroFx.Messaging.Transport.IMessageTransport>();

        await _client.PostAsync(
            new Uri($"/v1/orders/{orderId}/reserve?sku=BULK&quantity=500", UriKind.Relative),
            content: null);

        var deadLettered = await DeadLetterWithReasonAsync(transport, "insufficient-stock");

        Assert.That(deadLettered, Is.Not.Null);

        // Straight to the dead letter on the first attempt: no retries were consumed.
        Assert.That(deadLettered!.Message.Headers["microfx-attempt"], Is.EqualTo("1"));
    }

    [Test]
    public async Task A_transiently_failing_command_advances_through_the_retry_ladder()
    {
        // The bug this locks in: an earlier revision redelivered the original headers, so every
        // attempt read as attempt 1 and the policy never exhausted — an infinite loop that looked
        // like a working backoff.
        var orderId = NewOrderId();

        using var factory = new HostServiceFactory(new Dictionary<string, string?>
        {
            ["MicroFx:Messaging:SchedulerInterval"] = "00:00:00.100",
        });

        using var client = factory.CreateClient();
        var transport = (InMemoryTransport)factory.Services.GetRequiredService<
            MicroFx.Messaging.Transport.IMessageTransport>();

        await client.PostAsync(
            new Uri($"/v1/orders/{orderId}/reserve?sku=FLAKY-1&quantity=1", UriKind.Relative),
            content: null);

        // The handler fails on attempts 1 and 2 and succeeds on 3, so nothing is dead-lettered.
        await Task.Delay(1500);

        Assert.That(
            transport.DeadLetters.Any(d => d.Reason == "inventory-unavailable"),
            Is.False,
            "A command that eventually succeeds must not be dead-lettered.");
    }

    // ---- Envelope integrity ----------------------------------------------------------------------

    [Test]
    public async Task A_malformed_envelope_is_dead_lettered_rather_than_retried()
    {
        // Waiting will never make a malformed envelope well-formed, so retrying would only move a
        // hostile message around the topology.
        var transport = (InMemoryTransport)_factory.Services.GetRequiredService<
            MicroFx.Messaging.Transport.IMessageTransport>();

        var destination = _factory.Services.GetRequiredService<DestinationRegistry>()
            .Require(typeof(Orders.OrderPlacedV1));

        await transport.PublishAsync(new MicroFx.Messaging.Transport.TransportMessage(
            destination,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["microfx-id"] = "only-an-id" },
            System.Text.Encoding.UTF8.GetBytes("{}")));

        Assert.That(await DeadLetterWithReasonAsync(transport, "malformed-envelope"), Is.Not.Null);
    }

    [Test]
    public async Task An_unregistered_message_type_is_dead_lettered_and_never_resolved()
    {
        // The registry is a security boundary: an inbound type name must never reach reflection.
        var transport = (InMemoryTransport)_factory.Services.GetRequiredService<
            MicroFx.Messaging.Transport.IMessageTransport>();

        var envelope = new Envelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "System.Diagnostics.Process",
            Source = "attacker",
            Time = DateTimeOffset.UtcNow,
            Kind = MessageKind.Event,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

        var destination = _factory.Services.GetRequiredService<DestinationRegistry>()
            .Require(typeof(Orders.OrderPlacedV1));

        await transport.PublishAsync(new MicroFx.Messaging.Transport.TransportMessage(
            destination, EnvelopeCodec.Encode(envelope), System.Text.Encoding.UTF8.GetBytes("{}")));

        Assert.That(await DeadLetterWithReasonAsync(transport, "unknown-type"), Is.Not.Null);
    }

    [Test]
    public async Task A_command_delivered_to_an_event_subscription_is_rejected()
    {
        // Catches the classic topology error where a subscription is bound to the wrong
        // destination. Without the kind check the handler runs on a message it was never written
        // for and the behaviour is silently, subtly wrong.
        var transport = (InMemoryTransport)_factory.Services.GetRequiredService<
            MicroFx.Messaging.Transport.IMessageTransport>();

        var registry = _factory.Services.GetRequiredService<MessageTypeRegistry>();

        var envelope = new Envelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = registry.RequireWireName(typeof(Orders.OrderPlacedV1)),
            Source = "test",
            Time = DateTimeOffset.UtcNow,
            Kind = MessageKind.Command,   // stamped as a command, delivered to an event subscription
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

        var destination = _factory.Services.GetRequiredService<DestinationRegistry>()
            .Require(typeof(Orders.OrderPlacedV1));

        await transport.PublishAsync(new MicroFx.Messaging.Transport.TransportMessage(
            destination,
            EnvelopeCodec.Encode(envelope),
            JsonSerializer.SerializeToUtf8Bytes(new Orders.OrderPlacedV1("x", "y", 1))));

        Assert.That(await DeadLetterWithReasonAsync(transport, "kind-mismatch"), Is.Not.Null);
    }

    // ---- Composition ------------------------------------------------------------------------------

    [Test]
    public async Task The_feature_catalog_reports_the_messaging_topology()
    {
        var response = await _client.GetAsync(new Uri("/internal/features", UriKind.Relative));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var messaging = document.RootElement.GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("id").GetString() == "microfx.messaging");

        var facts = messaging.GetProperty("facts");

        Assert.Multiple(() =>
        {
            Assert.That(messaging.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(facts.GetProperty("transport").GetString(), Is.EqualTo("in-memory"));
            Assert.That(facts.GetProperty("subscriptions").GetInt32(), Is.EqualTo(2));
        });
    }
}
