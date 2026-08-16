using System.ComponentModel.DataAnnotations;
using System.Text;
using MicroFx.Features;
using MicroFx.MultiTenancy;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.Caching;

/// <summary>Options for the caching feature, bound from <c>MicroFx:Caching</c>.</summary>
public sealed class CachingOptions
{
    /// <summary>Default entry lifetime.</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Default in-process lifetime. Should not exceed <see cref="DefaultExpiration"/>.</summary>
    public TimeSpan DefaultLocalExpiration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Jitter applied to expirations, as a fraction of the lifetime. Spreads expiry so a population
    /// of entries written together does not all expire together and stampede the origin.
    /// </summary>
    [Range(0.0, 0.5)]
    public double ExpirationJitter { get; set; } = 0.15;

    /// <summary>Maximum serialized entry size. Larger values bypass the cache rather than fill it.</summary>
    [Range(1024, 10 * 1024 * 1024)]
    public int MaximumPayloadBytes { get; set; } = 1024 * 1024;

    /// <summary>Maximum key length after construction.</summary>
    [Range(64, 1024)]
    public int MaximumKeyLength { get; set; } = 512;
}

/// <summary>Supplies a distributed L2 tier.</summary>
/// <remarks>
/// Absent by default. In-memory L1 alone is a complete, correct cache; a distributed tier adds
/// cross-instance sharing and survival across restarts, which is a capacity decision rather than a
/// correctness one. An adapter such as Redis implements this to add L2 behind the same surface.
/// </remarks>
public interface IDistributedCacheProvider
{
    /// <summary>Short name for diagnostics: <c>redis</c>, <c>valkey</c>.</summary>
    string Name { get; }

    /// <summary>Registers the distributed cache this provider backs.</summary>
    void Register(IServiceCollection services);
}

/// <summary>Builds cache keys under the platform convention.</summary>
public interface ICacheKeyBuilder
{
    /// <summary>
    /// Builds a key of the form <c>{service}:{env}:{tenant}:{entity}:{version}:{id}</c>.
    /// </summary>
    string Build(string entity, string id, string? version = null);
}

/// <summary>
/// Applies the platform key convention, including tenant scoping.
/// </summary>
/// <remarks>
/// Tenant scoping is applied here rather than left to callers because a single forgotten prefix
/// leaks one tenant's data to another through a cache hit — a bug with no exception, no stack
/// trace, and no obvious symptom. Segments are sanitized because a caller-supplied id containing
/// the separator could otherwise forge a key in another tenant's namespace.
/// </remarks>
internal sealed class DefaultCacheKeyBuilder(
    Core.ServiceMetadata metadata,
    IServiceProvider services,
    Microsoft.Extensions.Options.IOptions<CachingOptions> options) : ICacheKeyBuilder
{
    private readonly CachingOptions _options = options.Value;

    public string Build(string entity, string id, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // Resolved lazily: the tenant feature is opt-in and may not be present at all.
        var tenant = services.GetService<ITenantContext>()?.Current ?? "-";

        var key = new StringBuilder()
            .Append(Sanitize(metadata.Name)).Append(':')
            .Append(Sanitize(metadata.Environment)).Append(':')
            .Append(Sanitize(tenant)).Append(':')
            .Append(Sanitize(entity)).Append(':')
            .Append(Sanitize(version ?? "v1")).Append(':')
            .Append(Sanitize(id))
            .ToString();

        if (key.Length > _options.MaximumKeyLength)
        {
            throw new ArgumentException(
                $"Cache key exceeds the {_options.MaximumKeyLength}-character limit.", nameof(id));
        }

        return key;
    }

    /// <summary>Replaces the separator and any control character, so a segment cannot span segments.</summary>
    private static string Sanitize(string segment)
    {
        Span<char> buffer = segment.Length <= 128 ? stackalloc char[segment.Length] : new char[segment.Length];

        for (var i = 0; i < segment.Length; i++)
        {
            var character = segment[i];
            buffer[i] = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_';
        }

        return new string(buffer);
    }
}

/// <summary>
/// In-memory L1 caching with an optional distributed L2, behind one <see cref="HybridCache"/>
/// surface.
/// </summary>
/// <remarks>
/// Adding L2 changes latency and cross-instance visibility and nothing else: key construction,
/// serialization, expiration, jitter, and stampede protection behave identically with and without
/// it, so no cache-calling code changes when Redis appears.
/// </remarks>
public sealed class CachingFeature : IMicroFxFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Caching,
        DisplayName = "Caching",
        Order = 300,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.MultiTenancy],
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Caching",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new CachingOptions();
        context.Configuration.GetSection("MicroFx:Caching").Bind(options);
        context.AddValidatedOptions<CachingOptions>();

        context.Services.TryAddSingleton<ICacheKeyBuilder, DefaultCacheKeyBuilder>();

        // Registered before AddHybridCache so an adapter's IDistributedCache is the one picked up.
        var provider = context.Services
            .Where(d => d.ServiceType == typeof(IDistributedCacheProvider))
            .Select(d => d.ImplementationInstance)
            .OfType<IDistributedCacheProvider>()
            .FirstOrDefault();

        provider?.Register(context.Services);

        var jittered = Jitter(options.DefaultExpiration, options.ExpirationJitter);

        context.Services.AddHybridCache(hybrid =>
        {
            hybrid.MaximumPayloadBytes = options.MaximumPayloadBytes;
            hybrid.MaximumKeyLength = options.MaximumKeyLength;
            hybrid.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = jittered,
                LocalCacheExpiration = Jitter(options.DefaultLocalExpiration, options.ExpirationJitter),
            };
        });

        context.Report("l1", "in-memory");
        context.Report("l2", provider?.Name ?? "none");
    }

    /// <summary>Spreads expiry so entries written together do not expire together.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Jitter shapes cache load. A predictable offset is no worse than no offset, " +
                        "and no security decision depends on this value.")]
    private static TimeSpan Jitter(TimeSpan value, double fraction)
    {
        if (fraction <= 0)
        {
            return value;
        }

        // Not security-sensitive: this shapes load, and a predictable offset would be no worse than
        // no offset. Random.Shared is the right tool and avoids the cost of a CSPRNG per entry.
        var offset = value.TotalMilliseconds * fraction * (Random.Shared.NextDouble() - 0.5) * 2;
        return TimeSpan.FromMilliseconds(Math.Max(1, value.TotalMilliseconds + offset));
    }
}
