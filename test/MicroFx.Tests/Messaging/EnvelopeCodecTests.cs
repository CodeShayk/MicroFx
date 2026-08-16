using MicroFx.Messaging;

namespace MicroFx.Tests.Messaging;

/// <summary>
/// The codec is the only component that sees raw broker input, so it is tested as a parser of
/// hostile data rather than as a round-trip convenience.
/// </summary>
[TestFixture]
internal sealed class EnvelopeCodecTests
{
    private static Envelope Sample(Action<Dictionary<string, string>>? headers = null)
    {
        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        headers?.Invoke(custom);

        return new Envelope
        {
            Id = "msg-1",
            Type = "orders.order-placed.v1",
            Source = "orders",
            Time = DateTimeOffset.UnixEpoch,
            Kind = MessageKind.Event,
            CorrelationId = "corr-1",
            Headers = custom,
        };
    }

    [Test]
    public void An_envelope_round_trips()
    {
        var original = Sample() with
        {
            CausationId = "cause-1",
            TenantId = "acme",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            Attempt = 3,
            IsReplay = true,
        };

        Assert.That(EnvelopeCodec.TryDecode(EnvelopeCodec.Encode(original), out var decoded, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(decoded!.Id, Is.EqualTo(original.Id));
            Assert.That(decoded.Type, Is.EqualTo(original.Type));
            Assert.That(decoded.Kind, Is.EqualTo(MessageKind.Event));
            Assert.That(decoded.TenantId, Is.EqualTo("acme"));
            Assert.That(decoded.Attempt, Is.EqualTo(3));
            Assert.That(decoded.IsReplay, Is.True);
            Assert.That(decoded.TraceParent, Is.EqualTo(original.TraceParent));
        });
    }

    // ---- Rejecting malformed input -------------------------------------------------------------

    [TestCase("microfx-id")]
    [TestCase("microfx-type")]
    [TestCase("microfx-source")]
    [TestCase("microfx-correlation-id")]
    public void A_missing_required_header_is_rejected(string missing)
    {
        var headers = EnvelopeCodec.Encode(Sample());
        headers.Remove(missing);

        Assert.Multiple(() =>
        {
            Assert.That(EnvelopeCodec.TryDecode(headers, out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("missing-required-header"));
        });
    }

    [TestCase("not-a-date")]
    [TestCase("")]
    [TestCase("99999-13-45")]
    public void An_invalid_timestamp_is_rejected(string value)
    {
        var headers = EnvelopeCodec.Encode(Sample());
        headers["microfx-time"] = value;

        Assert.That(EnvelopeCodec.TryDecode(headers, out _, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("invalid-time"));
    }

    [Test]
    public void An_unknown_message_kind_is_rejected()
    {
        var headers = EnvelopeCodec.Encode(Sample());
        headers["microfx-kind"] = "sudo";

        Assert.That(EnvelopeCodec.TryDecode(headers, out _, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("invalid-kind"));
    }

    [TestCase("0")]
    [TestCase("-5")]
    [TestCase("999999999")]
    [TestCase("not-a-number")]
    [TestCase("1e10")]
    public void A_forged_or_corrupted_attempt_count_is_rejected(string value)
    {
        // An unbounded attempt count could skip the retry ladder entirely or drive an endless loop,
        // so it is parsed and range-checked rather than trusted.
        var headers = EnvelopeCodec.Encode(Sample());
        headers["microfx-attempt"] = value;

        Assert.That(EnvelopeCodec.TryDecode(headers, out _, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("invalid-attempt"));
    }

    [Test]
    public void An_over_long_header_value_is_rejected()
    {
        var headers = EnvelopeCodec.Encode(Sample());
        headers["microfx-id"] = new string('a', Envelope.MaxHeaderLength + 1);

        Assert.That(EnvelopeCodec.TryDecode(headers, out _, out _), Is.False);
    }

    // ---- Platform header forgery ----------------------------------------------------------------

    [Test]
    public void A_custom_header_cannot_overwrite_a_platform_header()
    {
        // Otherwise a caller could forge a tenant, an attempt count, or an authorization token by
        // simply naming their header the same thing.
        var envelope = Sample(headers =>
        {
            headers["microfx-tenant-id"] = "victim-tenant";
            headers["microfx-attempt"] = "999";
        }) with
        { TenantId = "real-tenant" };

        var encoded = EnvelopeCodec.Encode(envelope);

        Assert.Multiple(() =>
        {
            Assert.That(encoded["microfx-tenant-id"], Is.EqualTo("real-tenant"));
            Assert.That(encoded["microfx-attempt"], Is.EqualTo("1"));
        });
    }

    [Test]
    public void Platform_headers_are_not_surfaced_as_custom_headers_on_decode()
    {
        var headers = EnvelopeCodec.Encode(Sample(custom => custom["x-tracking"] = "abc"));

        Assert.That(EnvelopeCodec.TryDecode(headers, out var decoded, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(decoded!.Headers, Does.ContainKey("x-tracking"));
            Assert.That(
                decoded.Headers.Keys.Any(k => k.StartsWith("microfx-", StringComparison.Ordinal)),
                Is.False);
        });
    }

    [Test]
    public void Custom_headers_are_capped_in_number()
    {
        var headers = EnvelopeCodec.Encode(Sample());
        for (var i = 0; i < Envelope.MaxHeaderCount + 20; i++)
        {
            headers[$"x-{i}"] = "v";
        }

        Assert.That(EnvelopeCodec.TryDecode(headers, out var decoded, out var reason),
            reason is null ? Is.True : Is.False);

        if (decoded is not null)
        {
            Assert.That(decoded.Headers, Has.Count.LessThanOrEqualTo(Envelope.MaxHeaderCount));
        }
    }

    // ---- Custom header validation ---------------------------------------------------------------

    [TestCase("microfx-tenant-id", "acme")]
    [TestCase("MICROFX-ATTEMPT", "9")]
    [TestCase("has space", "v")]
    [TestCase("semi;colon", "v")]
    [TestCase("", "v")]
    public void Unsafe_custom_headers_are_refused(string name, string value) =>
        Assert.That(EnvelopeCodec.IsValidCustomHeader(name, value), Is.False);

    [TestCase("x-tracking-id", "abc-123")]
    [TestCase("tenant_hint", "v1.2")]
    public void Ordinary_custom_headers_are_accepted(string name, string value) =>
        Assert.That(EnvelopeCodec.IsValidCustomHeader(name, value), Is.True);

    // ---- Expiry ----------------------------------------------------------------------------------

    [Test]
    public void Expiry_round_trips_and_is_evaluated_against_a_clock()
    {
        var envelope = Sample() with { ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(5) };

        Assert.That(EnvelopeCodec.TryDecode(EnvelopeCodec.Encode(envelope), out var decoded, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(decoded!.IsExpired(DateTimeOffset.UnixEpoch.AddMinutes(1)), Is.False);
            Assert.That(decoded.IsExpired(DateTimeOffset.UnixEpoch.AddMinutes(10)), Is.True);
        });
    }

    [Test]
    public void An_invalid_expiry_is_rejected_rather_than_ignored()
    {
        // Ignoring it would silently make an expiring message permanent.
        var headers = EnvelopeCodec.Encode(Sample());
        headers["microfx-expires-at"] = "soon";

        Assert.That(EnvelopeCodec.TryDecode(headers, out _, out var reason), Is.False);
        Assert.That(reason, Is.EqualTo("invalid-expiry"));
    }
}
