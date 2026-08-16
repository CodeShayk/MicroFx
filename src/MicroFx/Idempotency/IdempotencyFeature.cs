using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MicroFx.Api;
using MicroFx.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.Idempotency;

/// <summary>Options for the idempotency feature, bound from <c>MicroFx:Idempotency</c>.</summary>
public sealed class IdempotencyOptions
{
    /// <summary>Header carrying the client-supplied idempotency key.</summary>
    [Required]
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>How long a recorded response is replayable.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum response size recorded for replay. A larger response is executed normally but not
    /// stored, so the cache cannot be used to park unbounded data.
    /// </summary>
    [Range(1024, 1024 * 1024)]
    public int MaxRecordedBytes { get; set; } = 64 * 1024;

    /// <summary>Whether an unsafe request without a key is rejected rather than executed.</summary>
    public bool RequireKey { get; set; }
}

/// <summary>A response recorded for idempotent replay.</summary>
/// <param name="StatusCode">Status of the original response.</param>
/// <param name="ContentType">Content type of the original response.</param>
/// <param name="Body">Body of the original response.</param>
/// <param name="RequestFingerprint">
/// Hash of the original request, so the same key submitted with different content is detected
/// rather than silently answered with the wrong response.
/// </param>
public sealed record IdempotentResponse(
    int StatusCode, string? ContentType, byte[] Body, string RequestFingerprint);

/// <summary>Stores recorded responses. Backed by the cache feature by default.</summary>
public interface IIdempotencyStore
{
    /// <summary>Returns a recorded response, or null.</summary>
    ValueTask<IdempotentResponse?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Records a response for later replay.</summary>
    ValueTask SetAsync(
        string key, IdempotentResponse response, TimeSpan retention, CancellationToken cancellationToken);
}

internal sealed class HybridCacheIdempotencyStore(HybridCache cache) : IIdempotencyStore
{
    private const string Prefix = "microfx:idem:";

    public async ValueTask<IdempotentResponse?> GetAsync(string key, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync<IdempotentResponse?>(
            Prefix + key,
            _ => ValueTask.FromResult<IdempotentResponse?>(null),
            new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableUnderlyingData },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async ValueTask SetAsync(
        string key, IdempotentResponse response, TimeSpan retention, CancellationToken cancellationToken) =>
        await cache.SetAsync(
            Prefix + key,
            response,
            new HybridCacheEntryOptions { Expiration = retention, LocalCacheExpiration = retention },
            cancellationToken: cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Replays the original response when an unsafe request is retried with the same
/// <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// The key is caller-supplied and therefore untrusted: it is length-capped, character-restricted,
/// and — critically — scoped by a fingerprint of the request itself. Without the fingerprint, a
/// caller reusing a key across different payloads would receive an answer to a question it did not
/// ask, and a caller could probe another tenant's recorded response by guessing keys.
/// </remarks>
public sealed class IdempotencyFeature : IMicroFxFeature, IPipelineFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Idempotency,
        DisplayName = "Idempotency",
        Order = 130,
        DependsOn = [BuiltIn.Core, BuiltIn.Api, BuiltIn.Caching],
        After = [BuiltIn.Security, BuiltIn.MultiTenancy],
        SupportedHosts = HostKinds.Web,
        ConfigurationSection = "MicroFx:Idempotency",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<IdempotencyOptions>();
        context.Services.TryAddSingleton<IIdempotencyStore, HybridCacheIdempotencyStore>();
        context.Report("retention", new IdempotencyOptions().Retention);
    }

    /// <inheritdoc />
    public void UsePipeline(FeaturePipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Use(PipelineStage.PreEndpoint, app => app.UseMiddleware<IdempotencyMiddleware>());
    }
}

internal sealed partial class IdempotencyMiddleware(
    RequestDelegate next,
    IIdempotencyStore store,
    ProblemDetailsBuilder problems,
    Microsoft.Extensions.Options.IOptions<IdempotencyOptions> options)
{
    private const int MaxKeyLength = 255;

    private readonly IdempotencyOptions _options = options.Value;

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeKey { get; }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsUnsafe(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var rawKey = context.Request.Headers[_options.HeaderName].ToString();

        if (string.IsNullOrEmpty(rawKey))
        {
            if (_options.RequireKey)
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Idempotency key required",
                    $"Unsafe requests must supply a '{_options.HeaderName}' header.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (rawKey.Length > MaxKeyLength || !SafeKey.IsMatch(rawKey))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid idempotency key",
                "The key must be 1-255 characters of letters, digits, dot, underscore, colon, or hyphen.")
                .ConfigureAwait(false);
            return;
        }

        // The stored key is scoped by caller identity, so one caller can never read back — or
        // collide with — another caller's recorded response by guessing keys.
        var storageKey = ScopeKey(context, rawKey);
        var fingerprint = await FingerprintAsync(context).ConfigureAwait(false);

        if (await store.GetAsync(storageKey, context.RequestAborted).ConfigureAwait(false) is { } recorded)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(recorded.RequestFingerprint),
                    Encoding.UTF8.GetBytes(fingerprint)))
            {
                // Same key, different request. Answering with the recorded response would be
                // actively wrong, so this is a conflict rather than a replay.
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "Idempotency key reused",
                    "This idempotency key was used with a different request payload.").ConfigureAwait(false);
                return;
            }

            await ReplayAsync(context, recorded).ConfigureAwait(false);
            return;
        }

        await ExecuteAndRecordAsync(context, storageKey, fingerprint).ConfigureAwait(false);
    }

    private async Task ExecuteAndRecordAsync(HttpContext context, string storageKey, string fingerprint)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);

        // Only successful responses are replayable. Recording a 500 would pin a transient failure
        // for the whole retention window, making the retry that would have succeeded impossible.
        var isSuccess = context.Response.StatusCode is >= 200 and < 300;
        if (!isSuccess || buffer.Length > _options.MaxRecordedBytes)
        {
            return;
        }

        await store.SetAsync(
            storageKey,
            new IdempotentResponse(
                context.Response.StatusCode,
                context.Response.ContentType,
                buffer.ToArray(),
                fingerprint),
            _options.Retention,
            context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ReplayAsync(HttpContext context, IdempotentResponse recorded)
    {
        context.Response.StatusCode = recorded.StatusCode;
        context.Response.ContentType = recorded.ContentType;
        context.Response.Headers["Idempotency-Replayed"] = "true";

        await context.Response.Body
            .WriteAsync(recorded.Body, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Scopes a caller-supplied key to the authenticated caller and the target route.</summary>
    private static string ScopeKey(HttpContext context, string rawKey)
    {
        var subject = context.User.FindFirst("sub")?.Value
                      ?? context.User.Identity?.Name
                      ?? "anon";

        var tenant = context.Items.TryGetValue("MicroFx.TenantId", out var value)
            ? value as string ?? "-"
            : "-";

        return Hash($"{tenant}|{subject}|{context.Request.Method}|{context.Request.Path}|{rawKey}");
    }

    /// <summary>Hashes the request so a reused key with different content is detectable.</summary>
    private static async Task<string> FingerprintAsync(HttpContext context)
    {
        context.Request.EnableBuffering();

        using var sha = SHA256.Create();
        var body = await sha.ComputeHashAsync(context.Request.Body, context.RequestAborted)
            .ConfigureAwait(false);
        context.Request.Body.Position = 0;

        return Hash($"{context.Request.Method}|{context.Request.Path}|{Convert.ToHexString(body)}");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsUnsafe(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private async Task WriteProblemAsync(HttpContext context, int status, string title, string detail)
    {
        var problem = problems.Create(
            status, title, detail, ProblemDetailsBuilder.TypeBase + "idempotency", context);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }
}
