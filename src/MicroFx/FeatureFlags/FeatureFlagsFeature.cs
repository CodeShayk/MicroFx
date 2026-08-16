using System.Diagnostics.Metrics;
using MicroFx.Core;
using MicroFx.Features;
using MicroFx.MultiTenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenFeature.Constant;
using OpenFeature.Model;
using OpenFeatureApi = OpenFeature.Api;
using OpenFeatureProvider = OpenFeature.FeatureProvider;

namespace MicroFx.FeatureFlags;

/// <summary>Options for the feature flags feature, bound from <c>MicroFx:FeatureFlags</c>.</summary>
public sealed class FeatureFlagOptions
{
    /// <summary>Flag values for the in-box configuration provider.</summary>
    public IDictionary<string, string> Flags { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long an evaluation may take before the code default is used instead.</summary>
    public TimeSpan EvaluationTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>Evaluates feature flags with the caller's context applied.</summary>
public interface IFeatureFlags
{
    /// <summary>Evaluates a boolean flag.</summary>
    ValueTask<bool> IsEnabledAsync(
        string flag, bool defaultValue = false, CancellationToken cancellationToken = default);

    /// <summary>Evaluates a string flag.</summary>
    ValueTask<string> GetStringAsync(
        string flag, string defaultValue, CancellationToken cancellationToken = default);

    /// <summary>Evaluates an integer flag.</summary>
    ValueTask<int> GetIntegerAsync(
        string flag, int defaultValue, CancellationToken cancellationToken = default);
}

/// <summary>
/// Feature flags over OpenFeature.
/// </summary>
/// <remarks>
/// <para>
/// Standards-first: OpenFeature is the vendor-neutral evaluation API, so swapping AppConfig for
/// LaunchDarkly is a provider registration rather than a change to every call site.
/// </para>
/// <para>
/// Evaluation <b>never fails a request</b>. A provider outage, a timeout, or a malformed value all
/// resolve to the code default. A flag system that can take the service down with it is worse than
/// no flag system, because it adds a dependency to every code path it touches.
/// </para>
/// </remarks>
public sealed class FeatureFlagsFeature : IMicroFxFeature, IFeatureLifecycle
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.FeatureFlags,
        DisplayName = "Feature flags",
        Order = 370,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.MultiTenancy],
        EnabledByDefault = false,
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:FeatureFlags",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new FeatureFlagOptions();
        context.Configuration.GetSection("MicroFx:FeatureFlags").Bind(options);
        context.AddValidatedOptions<FeatureFlagOptions>();

        // The in-box provider reads from configuration, so a service has working flags — and a
        // working kill switch — before any vendor is chosen.
        context.Services.TryAddSingleton<OpenFeatureProvider>(provider =>
            new ConfigurationFeatureProvider(
                provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<FeatureFlagOptions>>()));

        context.Services.TryAddScoped<IFeatureFlags, OpenFeatureFlags>();
        context.AddMeter(FlagMetrics.MeterName);

        context.Report("provider", "configuration");
        context.Report("flags", options.Flags.Count);
    }

    /// <inheritdoc />
#pragma warning disable CA2016 // The OpenFeature API exposes no cancellation token on these calls.
    public async ValueTask StartingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var provider = context.Services.GetRequiredService<OpenFeatureProvider>();
        // OpenFeature's API takes no cancellation token here; the kernel already budgets this
        // lifecycle phase and fails startup by name if it overruns.
        await OpenFeatureApi.Instance.SetProviderAsync(provider).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StoppingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await OpenFeatureApi.Instance.ShutdownAsync().ConfigureAwait(false);
    }
#pragma warning restore CA2016
}

/// <summary>Evaluates flags through OpenFeature, enriching the context and never failing.</summary>
internal sealed partial class OpenFeatureFlags(
    ServiceMetadata metadata,
    IServiceProvider services,
    Microsoft.Extensions.Options.IOptions<FeatureFlagOptions> options,
    ILogger<OpenFeatureFlags> logger) : IFeatureFlags
{
    private readonly FeatureFlagOptions _options = options.Value;

    public ValueTask<bool> IsEnabledAsync(
        string flag, bool defaultValue = false, CancellationToken cancellationToken = default) =>
        EvaluateAsync(
            flag, defaultValue,
            (client, evaluationContext, token) => client.GetBooleanValueAsync(flag, defaultValue, evaluationContext, cancellationToken: token),
            cancellationToken);

    public ValueTask<string> GetStringAsync(
        string flag, string defaultValue, CancellationToken cancellationToken = default) =>
        EvaluateAsync(
            flag, defaultValue,
            (client, evaluationContext, token) => client.GetStringValueAsync(flag, defaultValue, evaluationContext, cancellationToken: token),
            cancellationToken);

    public ValueTask<int> GetIntegerAsync(
        string flag, int defaultValue, CancellationToken cancellationToken = default) =>
        EvaluateAsync(
            flag, defaultValue,
            (client, evaluationContext, token) => client.GetIntegerValueAsync(flag, defaultValue, evaluationContext, cancellationToken: token),
            cancellationToken);

    private async ValueTask<T> EvaluateAsync<T>(
        string flag,
        T defaultValue,
        Func<OpenFeature.FeatureClient, EvaluationContext, CancellationToken, Task<T>> evaluate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Bounded: a hung provider must not hold a request open. The code default is always a
            // correct answer, just not necessarily the freshest one.
            timeout.CancelAfter(_options.EvaluationTimeout);

            var client = OpenFeatureApi.Instance.GetClient(metadata.Name, metadata.Version);
            var value = await evaluate(client, BuildContext(), timeout.Token).ConfigureAwait(false);

            FlagMetrics.Evaluated(flag, succeeded: true);
            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException ||
                                   !cancellationToken.IsCancellationRequested)
        {
            // Never fails the request. A flag system that can take the service down adds a
            // dependency to every code path it touches, which is worse than having no flags.
            FlagMetrics.Evaluated(flag, succeeded: false);
            LogEvaluationFailed(logger, flag, ex);
            return defaultValue;
        }
    }

    /// <summary>Enriches the evaluation context with tenant, environment, and service version.</summary>
    private EvaluationContext BuildContext()
    {
        var builder = EvaluationContext.Builder()
            .Set("environment", metadata.Environment)
            .Set("service", metadata.Name)
            .Set("version", metadata.Version);

        if (services.GetService<ITenantContext>()?.Current is { } tenant)
        {
            builder.SetTargetingKey(tenant);
            builder.Set("tenant", tenant);
        }

        return builder.Build();
    }

    [LoggerMessage(EventId = 7501, Level = LogLevel.Warning,
        Message = "Evaluating flag {Flag} failed; the code default was used.")]
    private static partial void LogEvaluationFailed(ILogger logger, string flag, Exception exception);
}

/// <summary>
/// Reads flags from configuration.
/// </summary>
/// <remarks>
/// The in-box provider. Because it reads through <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>,
/// a configuration reload changes a flag without a redeploy — which is what makes a kill switch
/// useful during an incident rather than after one.
/// </remarks>
internal sealed class ConfigurationFeatureProvider(
    Microsoft.Extensions.Options.IOptionsMonitor<FeatureFlagOptions> options) : OpenFeatureProvider
{
    private readonly Metadata _metadata = new("microfx-configuration");

    public override Metadata GetMetadata() => _metadata;

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(flagKey, defaultValue, bool.TryParse));

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve<string>(flagKey, defaultValue, (string? raw, out string parsed) =>
        {
            parsed = raw ?? string.Empty;
            return raw is not null;
        }));

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve<int>(flagKey, defaultValue, (string? raw, out int parsed) =>
            int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out parsed)));

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve<double>(flagKey, defaultValue, (string? raw, out double parsed) =>
            double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out parsed)));

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ResolutionDetails<Value>(
            flagKey, defaultValue, ErrorType.None, Reason.Default));

    private delegate bool Parser<T>(string? raw, out T value);

    private ResolutionDetails<T> Resolve<T>(string flagKey, T defaultValue, Parser<T> parse)
    {
        if (!options.CurrentValue.Flags.TryGetValue(flagKey, out var raw))
        {
            return new ResolutionDetails<T>(flagKey, defaultValue, ErrorType.FlagNotFound, Reason.Default);
        }

        // A malformed value resolves to the default rather than throwing: a typo in a flag value
        // must not be able to break the code path the flag guards.
        return parse(raw, out var parsed)
            ? new ResolutionDetails<T>(flagKey, parsed, ErrorType.None, Reason.Static)
            : new ResolutionDetails<T>(flagKey, defaultValue, ErrorType.TypeMismatch, Reason.Error);
    }
}

/// <summary>Flag evaluation metrics, for A/B analysis and provider health.</summary>
internal static class FlagMetrics
{
    public const string MeterName = "MicroFx.FeatureFlags";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Evaluations = Meter.CreateCounter<long>(
        "featureflags.evaluation.count", description: "Flag evaluations by outcome.");

    public static void Evaluated(string flag, bool succeeded) =>
        Evaluations.Add(1,
            new KeyValuePair<string, object?>("flag", flag),
            new KeyValuePair<string, object?>("outcome", succeeded ? "resolved" : "default"));
}
