using MicroFx.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MicroFx.Configuration;

/// <summary>A configuration source contributed by an adapter, such as a secret store.</summary>
/// <remarks>
/// Implementations are registered before <c>AddMicroFx</c> and consumed during the configuration
/// pass, so a secret store is present before any feature binds options.
/// </remarks>
public interface IConfigurationSourceProvider
{
    /// <summary>A short name for diagnostics: <c>aws-ssm</c>, <c>aws-secrets</c>, <c>vault</c>.</summary>
    string Name { get; }

    /// <summary>Adds this provider's sources.</summary>
    void AddSources(IConfigurationBuilder builder, IHostContext context);
}

/// <summary>Context passed to an <see cref="IConfigurationSourceProvider"/>.</summary>
/// <param name="ServiceName">The service's name, for hierarchical key prefixes.</param>
/// <param name="EnvironmentName">The deployment environment.</param>
/// <param name="IsDevelopment">Whether the host is running in Development.</param>
public readonly record struct IHostContext(string ServiceName, string EnvironmentName, bool IsDevelopment);

/// <summary>Options for the configuration feature.</summary>
public sealed class ConfigurationOptions
{
    /// <summary>
    /// Environment-variable prefix layered over the standard sources. Double underscore separates
    /// levels, so <c>MICROFX_Host__ManagementPort</c> sets <c>MicroFx:Host:ManagementPort</c>.
    /// </summary>
    public string EnvironmentPrefix { get; set; } = "MICROFX_";
}

/// <summary>
/// Layered configuration with validated, strongly-typed options.
/// </summary>
/// <remarks>
/// A kernel feature, and the only one that runs a pass of its own. Sources it contributes must be
/// present before any other feature binds options, which is why configuration is a distinct
/// composition pass rather than "the call you must remember to make first".
/// </remarks>
public sealed class ConfigurationFeature : IMicroFxFeature, IConfigurationFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Configuration,
        DisplayName = "Configuration",
        IsKernel = true,
        Order = 10,
        DependsOn = [BuiltIn.Core],
        ConfigurationSection = "MicroFx:Configuration",
    };

    /// <inheritdoc />
    public void AddConfigurationSources(FeatureConfigurationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new ConfigurationOptions();
        (context.Sources as IConfiguration)?
            .GetSection("MicroFx:Configuration").Bind(options);

        var hostContext = new IHostContext(
            context.Metadata.Name,
            context.Environment.EnvironmentName,
            context.Environment.IsDevelopment());

        // Adapter-provided sources (secret stores, remote configuration) sit beneath the prefixed
        // environment variables, so a break-glass environment override always wins.
        foreach (var provider in ConfigurationSourceProviders.Registered)
        {
            provider.AddSources(context.Sources, hostContext);
        }

        if (!string.IsNullOrEmpty(options.EnvironmentPrefix))
        {
            context.Sources.AddEnvironmentVariables(options.EnvironmentPrefix);
        }
    }

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<ConfigurationOptions>();
        context.Services.TryAddSingleton<ConfigurationProvenance>();

        var providers = ConfigurationSourceProviders.Registered;
        context.Report(
            "sources",
            providers.Count == 0
                ? "default"
                : "default+" + string.Join('+', providers.Select(p => p.Name)));

        // Secrets in environment variables work, but forfeit rotation and audit. Say so rather than
        // letting a service reach production believing it has a secret-management story.
        if (providers.Count == 0 && !context.Metadata.IsDevelopment)
        {
            context.Report("secretStore", "environment (no adapter configured)");
        }
    }
}

/// <summary>
/// Registry of configuration source providers, populated before <c>AddMicroFx</c>.
/// </summary>
/// <remarks>
/// A static registry rather than DI, because the configuration pass runs before any service
/// provider exists — configuration is what services are built from.
/// </remarks>
public static class ConfigurationSourceProviders
{
    private static readonly List<IConfigurationSourceProvider> Providers = [];

    internal static IReadOnlyList<IConfigurationSourceProvider> Registered
    {
        get
        {
            lock (Providers)
            {
                return [.. Providers];
            }
        }
    }

    /// <summary>Registers a source provider. Call before <c>AddMicroFx</c>.</summary>
    public static void Register(IConfigurationSourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (Providers)
        {
            Providers.Add(provider);
        }
    }

    /// <summary>Clears the registry. Intended for tests.</summary>
    public static void Clear()
    {
        lock (Providers)
        {
            Providers.Clear();
        }
    }
}

/// <summary>
/// Answers "where did this configuration value come from?" — the question that turns a confusing
/// override into a one-line explanation.
/// </summary>
public sealed class ConfigurationProvenance(IConfiguration configuration)
{
    /// <summary>
    /// Returns every key with its redacted value and the provider that supplied the winning value.
    /// </summary>
    /// <remarks>Values are redacted by <see cref="SecretRedactor"/> before leaving this method.</remarks>
    public IReadOnlyList<ConfigurationEntry> Snapshot()
    {
        var entries = new List<ConfigurationEntry>();
        var root = configuration as IConfigurationRoot;

        foreach (var item in configuration.AsEnumerable().OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (item.Value is null)
            {
                continue;   // section node, not a leaf
            }

            entries.Add(new ConfigurationEntry(
                item.Key,
                SecretRedactor.Redact(item.Key, item.Value),
                root is null ? "unknown" : FindProvider(root, item.Key)));
        }

        return entries;
    }

    private static string FindProvider(IConfigurationRoot root, string key)
    {
        // Last provider wins in the configuration system, so search in reverse.
        for (var i = root.Providers.Count() - 1; i >= 0; i--)
        {
            var provider = root.Providers.ElementAt(i);
            if (provider.TryGet(key, out _))
            {
                return provider.GetType().Name;
            }
        }

        return "unknown";
    }
}

/// <summary>One configuration key, with its redacted value and originating provider.</summary>
/// <param name="Key">The full configuration key.</param>
/// <param name="Value">The value, redacted when the key or shape indicates a secret.</param>
/// <param name="Provider">The provider that supplied the winning value.</param>
public readonly record struct ConfigurationEntry(string Key, string? Value, string Provider);
