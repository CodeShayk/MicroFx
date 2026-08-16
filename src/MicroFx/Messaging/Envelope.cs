using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MicroFx.Messaging;

/// <summary>What kind of message an envelope carries.</summary>
public enum MessageKind
{
    /// <summary>A command: exactly one logical consumer.</summary>
    Command,

    /// <summary>An event: zero or more independent subscribers.</summary>
    Event,

    /// <summary>A request awaiting a correlated reply.</summary>
    Request,

    /// <summary>A reply to a request.</summary>
    Reply,
}

/// <summary>
/// The CloudEvents 1.0 envelope every message carries, whatever its kind and whatever the transport.
/// </summary>
/// <remarks>
/// One envelope for commands, events, requests, and replies means one place for tracing, tenancy,
/// authorization, dedupe, and expiry — rather than four subtly different implementations that drift.
/// </remarks>
public sealed record Envelope
{
    /// <summary>Header name prefixes reserved to the platform.</summary>
    public const string PlatformHeaderPrefix = "microfx-";

    /// <summary>Maximum length of a header name or value. Bounds untrusted broker input.</summary>
    public const int MaxHeaderLength = 1024;

    /// <summary>Maximum number of custom headers on one envelope.</summary>
    public const int MaxHeaderCount = 64;

    /// <summary>Unique message id. Stable across redeliveries; the dedupe key.</summary>
    public required string Id { get; init; }

    /// <summary>Logical type name, e.g. <c>acme.orders.order.placed.v1</c>. Resolved through a registry.</summary>
    public required string Type { get; init; }

    /// <summary>Publishing service.</summary>
    public required string Source { get; init; }

    /// <summary>When the message was produced.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>What kind of message this is. Set by the platform, never by the caller.</summary>
    public required MessageKind Kind { get; init; }

    /// <summary>Correlates this message with the work that caused it.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Id of the message that caused this one.</summary>
    public string? CausationId { get; init; }

    /// <summary>Tenant in scope.</summary>
    public string? TenantId { get; init; }

    /// <summary>W3C trace context, so producer and consumer spans join one trace.</summary>
    public string? TraceParent { get; init; }

    /// <summary>W3C trace state.</summary>
    public string? TraceState { get; init; }

    /// <summary>Content type of the payload.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>Payload encoding, when compressed.</summary>
    public string? ContentEncoding { get; init; }

    /// <summary>Where a reply should be sent, for the request/reply pattern.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>Discard rather than deliver after this instant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Partition key for ordered subscriptions.</summary>
    public string? PartitionKey { get; init; }

    /// <summary>Delivery attempt, starting at 1.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Whether this message was republished from an archive.</summary>
    public bool IsReplay { get; init; }

    /// <summary>Object-store reference when the payload was offloaded by the claim-check.</summary>
    public string? ClaimCheckReference { get; init; }

    /// <summary>Caller identity token, for message-level authorization.</summary>
    public string? AuthorizationToken { get; init; }

    /// <summary>Custom headers set by the caller.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the envelope has passed its expiry.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiry && now >= expiry;
}

/// <summary>
/// Encodes an envelope into transport headers and decodes it back.
/// </summary>
/// <remarks>
/// <para>
/// Metadata travels in headers rather than the body so an operator reading a broker's management UI,
/// or a tool moving messages between queues, sees routable metadata without deserializing a payload.
/// </para>
/// <para>
/// Decoding treats every header as <b>untrusted input from a shared broker</b>. Lengths and counts
/// are bounded, integers are parsed rather than cast, timestamps are validated, and a malformed
/// envelope is rejected as permanently bad rather than being partially trusted.
/// </para>
/// </remarks>
public static class EnvelopeCodec
{
    private const string IdKey = "microfx-id";
    private const string TypeKey = "microfx-type";
    private const string SourceKey = "microfx-source";
    private const string TimeKey = "microfx-time";
    private const string KindKey = "microfx-kind";
    private const string CorrelationKey = "microfx-correlation-id";
    private const string CausationKey = "microfx-causation-id";
    private const string TenantKey = "microfx-tenant-id";
    private const string TraceParentKey = "traceparent";
    private const string TraceStateKey = "tracestate";
    private const string ContentTypeKey = "microfx-content-type";
    private const string ContentEncodingKey = "microfx-content-encoding";
    private const string ReplyToKey = "microfx-reply-to";
    private const string ExpiresKey = "microfx-expires-at";
    private const string PartitionKey = "microfx-partition-key";
    private const string AttemptKey = "microfx-attempt";
    private const string ReplayKey = "microfx-replay";
    private const string ClaimCheckKey = "microfx-claim-check";
    private const string AuthorizationKey = "microfx-authorization";

    /// <summary>Maximum retry attempt accepted from a header, bounding a forged or corrupted value.</summary>
    public const int MaxAttempt = 1000;

    /// <summary>Encodes an envelope into a transport header dictionary.</summary>
    public static Dictionary<string, string> Encode(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IdKey] = envelope.Id,
            [TypeKey] = envelope.Type,
            [SourceKey] = envelope.Source,
            [TimeKey] = envelope.Time.ToString("O", CultureInfo.InvariantCulture),
            [KindKey] = envelope.Kind.ToString().ToLowerInvariant(),
            [CorrelationKey] = envelope.CorrelationId,
            [ContentTypeKey] = envelope.ContentType,
            [AttemptKey] = envelope.Attempt.ToString(CultureInfo.InvariantCulture),
        };

        AddIfPresent(headers, CausationKey, envelope.CausationId);
        AddIfPresent(headers, TenantKey, envelope.TenantId);
        AddIfPresent(headers, TraceParentKey, envelope.TraceParent);
        AddIfPresent(headers, TraceStateKey, envelope.TraceState);
        AddIfPresent(headers, ContentEncodingKey, envelope.ContentEncoding);
        AddIfPresent(headers, ReplyToKey, envelope.ReplyTo);
        AddIfPresent(headers, PartitionKey, envelope.PartitionKey);
        AddIfPresent(headers, ClaimCheckKey, envelope.ClaimCheckReference);
        AddIfPresent(headers, AuthorizationKey, envelope.AuthorizationToken);

        if (envelope.ExpiresAt is { } expires)
        {
            headers[ExpiresKey] = expires.ToString("O", CultureInfo.InvariantCulture);
        }

        if (envelope.IsReplay)
        {
            headers[ReplayKey] = "true";
        }

        foreach (var (key, value) in envelope.Headers)
        {
            // A custom header must never be able to overwrite a platform one — that would let a
            // caller forge a tenant, an attempt count, or an authorization token.
            if (key.StartsWith(Envelope.PlatformHeaderPrefix, StringComparison.OrdinalIgnoreCase) ||
                headers.ContainsKey(key))
            {
                continue;
            }

            headers[key] = value;
        }

        return headers;
    }

    /// <summary>
    /// Decodes an envelope from transport headers.
    /// </summary>
    /// <returns><see langword="true"/> when the envelope is well-formed.</returns>
    /// <remarks>
    /// Returns false rather than throwing, so the caller can dead-letter a malformed message with a
    /// structured reason instead of treating a hostile envelope as an infrastructure fault.
    /// </remarks>
    public static bool TryDecode(
        IReadOnlyDictionary<string, string> headers,
        [NotNullWhen(true)] out Envelope? envelope,
        [NotNullWhen(false)] out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(headers);

        envelope = null;
        failureReason = null;

        if (headers.Count > Envelope.MaxHeaderCount + 24)
        {
            failureReason = "header-count-exceeded";
            return false;
        }

        if (!TryGetBounded(headers, IdKey, out var id) ||
            !TryGetBounded(headers, TypeKey, out var type) ||
            !TryGetBounded(headers, SourceKey, out var source) ||
            !TryGetBounded(headers, CorrelationKey, out var correlationId))
        {
            failureReason = "missing-required-header";
            return false;
        }

        if (!headers.TryGetValue(TimeKey, out var timeText) ||
            !DateTimeOffset.TryParse(
                timeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
        {
            failureReason = "invalid-time";
            return false;
        }

        if (!headers.TryGetValue(KindKey, out var kindText) ||
            !Enum.TryParse<MessageKind>(kindText, ignoreCase: true, out var kind))
        {
            failureReason = "invalid-kind";
            return false;
        }

        // Parsed and clamped, never trusted. A corrupted or forged attempt count could otherwise
        // skip the retry ladder entirely or drive an unbounded loop.
        var attempt = 1;
        if (headers.TryGetValue(AttemptKey, out var attemptText) &&
            (!int.TryParse(attemptText, CultureInfo.InvariantCulture, out attempt) ||
             attempt is < 1 or > MaxAttempt))
        {
            failureReason = "invalid-attempt";
            return false;
        }

        DateTimeOffset? expiresAt = null;
        if (headers.TryGetValue(ExpiresKey, out var expiresText))
        {
            if (!DateTimeOffset.TryParse(
                    expiresText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                    out var parsedExpiry))
            {
                failureReason = "invalid-expiry";
                return false;
            }

            expiresAt = parsedExpiry;
        }

        var custom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in headers)
        {
            if (key.StartsWith(Envelope.PlatformHeaderPrefix, StringComparison.OrdinalIgnoreCase) ||
                key is TraceParentKey or TraceStateKey ||
                key.Length > Envelope.MaxHeaderLength || value.Length > Envelope.MaxHeaderLength ||
                custom.Count >= Envelope.MaxHeaderCount)
            {
                continue;
            }

            custom[key] = value;
        }

        envelope = new Envelope
        {
            Id = id,
            Type = type,
            Source = source,
            Time = time,
            Kind = kind,
            CorrelationId = correlationId,
            CausationId = GetOptional(headers, CausationKey),
            TenantId = GetOptional(headers, TenantKey),
            TraceParent = GetOptional(headers, TraceParentKey),
            TraceState = GetOptional(headers, TraceStateKey),
            ContentType = GetOptional(headers, ContentTypeKey) ?? "application/json",
            ContentEncoding = GetOptional(headers, ContentEncodingKey),
            ReplyTo = GetOptional(headers, ReplyToKey),
            ExpiresAt = expiresAt,
            PartitionKey = GetOptional(headers, PartitionKey),
            Attempt = attempt,
            IsReplay = headers.TryGetValue(ReplayKey, out var replay) &&
                       string.Equals(replay, "true", StringComparison.OrdinalIgnoreCase),
            ClaimCheckReference = GetOptional(headers, ClaimCheckKey),
            AuthorizationToken = GetOptional(headers, AuthorizationKey),
            Headers = custom,
        };

        return true;
    }

    /// <summary>Validates a caller-supplied header name and value before it is transmitted.</summary>
    public static bool IsValidCustomHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return !string.IsNullOrWhiteSpace(name) &&
        name.Length <= Envelope.MaxHeaderLength &&
        value.Length <= Envelope.MaxHeaderLength &&
        !name.StartsWith(Envelope.PlatformHeaderPrefix, StringComparison.OrdinalIgnoreCase) &&
            name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static void AddIfPresent(Dictionary<string, string> headers, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers[key] = value;
        }
    }

    private static bool TryGetBounded(
        IReadOnlyDictionary<string, string> headers,
        string key,
        [NotNullWhen(true)] out string? value)
    {
        value = null;

        if (!headers.TryGetValue(key, out var raw) ||
            string.IsNullOrWhiteSpace(raw) ||
            raw.Length > Envelope.MaxHeaderLength)
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static string? GetOptional(IReadOnlyDictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var value) &&
        !string.IsNullOrEmpty(value) &&
        value.Length <= Envelope.MaxHeaderLength
            ? value
            : null;
}
