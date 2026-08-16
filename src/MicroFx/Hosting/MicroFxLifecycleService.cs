using System.Diagnostics;
using MicroFx.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MicroFx.Hosting;

/// <summary>
/// Drives passes 5, 6, and 8 — validate, starting, started — and the reverse-order drain.
/// </summary>
internal sealed partial class MicroFxLifecycleService(
    MicroFxComposition composition,
    IServiceProvider services,
    IOptions<MicroFxHostOptions> options,
    ILogger<MicroFxLifecycleService> logger) : IHostedLifecycleService
{
    /// <summary>Activity source for startup spans, so a slow cold start renders as a flame graph.</summary>
    internal const string ActivitySourceName = "MicroFx.Startup";

    private static readonly ActivitySource Activity = new(ActivitySourceName);

    private readonly MicroFxHostOptions _options = options.Value;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_options.LogStartupBanner)
        {
            StartupBanner.Write(logger, composition);
        }

        await ValidateAsync(cancellationToken).ConfigureAwait(false);
        await RunLifecycleAsync(
            "Starting",
            (feature, context, ct) => feature.StartingAsync(context, ct),
            reverse: false,
            cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) =>
        RunLifecycleAsync(
            "Started",
            (feature, context, ct) => feature.StartedAsync(context, ct),
            reverse: false,
            cancellationToken);

    public Task StoppingAsync(CancellationToken cancellationToken) =>
        // Reverse order: consumers cancel before the transport closes, telemetry flushes last.
        RunLifecycleAsync(
            "Stopping",
            (feature, context, ct) => feature.StoppingAsync(context, ct),
            reverse: true,
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs every validator and aggregates the findings, so a service with three unrelated
    /// misconfigurations reports all three in one startup rather than one per restart.
    /// </summary>
    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var validators = composition.Catalog.Enabled
            .Where(e => e.Feature is IFeatureValidator)
            .ToList();

        if (validators.Count == 0)
        {
            return;
        }

        using var activity = Activity.StartActivity("microfx.validate");
        var context = new FeatureValidationContext(services, composition.Metadata, composition.Catalog);

        var results = await Task.WhenAll(validators.Select(async entry =>
        {
            try
            {
                var report = await ((IFeatureValidator)entry.Feature)
                    .ValidateAsync(context, cancellationToken).ConfigureAwait(false);
                return (entry, report);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (entry, ValidationReport.Error($"Validation threw: {ex.Message}"));
            }
        })).ConfigureAwait(false);

        var failures = new List<string>();

        foreach (var (entry, report) in results)
        {
            foreach (var finding in report.Findings)
            {
                switch (finding.Severity)
                {
                    case ValidationSeverity.Error:
                        failures.Add($"[{entry.Id}] {finding.Message}");
                        LogValidationError(logger, entry.Id, finding.Message);
                        break;
                    case ValidationSeverity.Warning:
                        LogValidationWarning(logger, entry.Id, finding.Message);
                        break;
                    default:
                        LogValidationInformation(logger, entry.Id, finding.Message);
                        break;
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new FeatureValidationException(
                $"MicroFx startup validation failed with {failures.Count} error(s):{Environment.NewLine}" +
                string.Join(Environment.NewLine, failures.Select(f => "  - " + f)),
                failures);
        }
    }

    private async Task RunLifecycleAsync(
        string phase,
        Func<IFeatureLifecycle, FeatureLifecycleContext, CancellationToken, ValueTask> invoke,
        bool reverse,
        CancellationToken cancellationToken)
    {
        var entries = composition.Catalog.Enabled
            .Where(e => e.Feature is IFeatureLifecycle)
            .ToList();

        if (reverse)
        {
            entries.Reverse();
        }

        if (entries.Count == 0)
        {
            return;
        }

        var context = new FeatureLifecycleContext(services, composition.Metadata, composition.Catalog);
        var budget = reverse ? _options.DrainTimeout : _options.FeatureLifecycleTimeout;

        foreach (var entry in entries)
        {
            using var activity = Activity.StartActivity($"microfx.{phase.ToLowerInvariant()}");
            activity?.SetTag("microfx.feature", entry.Id);

            var stopwatch = ValueStopwatch.StartNew();

            // A per-feature budget turns "the deployment hangs" into "feature X exceeded its budget",
            // which is the difference between a bisect and a fix.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);

            try
            {
                await invoke((IFeatureLifecycle)entry.Feature, context, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                var message =
                    $"Feature '{entry.Id}' exceeded its {phase} budget of {budget.TotalSeconds:0.#}s.";
                activity?.SetStatus(ActivityStatusCode.Error, message);

                // A stalled drain must not prevent the remaining features from draining, but a
                // stalled start is a broken deployment and should fail loudly.
                if (reverse)
                {
                    LogLifecycleTimeoutDuringDrain(logger, entry.Id, budget.TotalSeconds);
                    continue;
                }

                throw new TimeoutException(message);
            }
            catch (Exception ex) when (reverse && ex is not OperationCanceledException)
            {
                // One feature failing to drain must not strand the rest.
                LogDrainFailed(logger, entry.Id, ex);
                continue;
            }
            finally
            {
                var elapsed = stopwatch.Elapsed;
                entry.RecordTiming(phase, elapsed);
                MicroFxMetrics.RecordFeatureStartup(entry.Id, phase, elapsed);
            }
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "MicroFx validation error in feature {FeatureId}: {Detail}")]
    private static partial void LogValidationError(ILogger logger, string featureId, string detail);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "MicroFx validation warning in feature {FeatureId}: {Detail}")]
    private static partial void LogValidationWarning(ILogger logger, string featureId, string detail);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "MicroFx validation note from feature {FeatureId}: {Detail}")]
    private static partial void LogValidationInformation(ILogger logger, string featureId, string detail);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "Feature {FeatureId} exceeded its {BudgetSeconds}s drain budget; continuing shutdown.")]
    private static partial void LogLifecycleTimeoutDuringDrain(
        ILogger logger, string featureId, double budgetSeconds);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Feature {FeatureId} failed to drain cleanly; continuing shutdown.")]
    private static partial void LogDrainFailed(ILogger logger, string featureId, Exception exception);
}

/// <summary>Allocation-free elapsed-time measurement for the startup path.</summary>
internal readonly struct ValueStopwatch
{
    private readonly long _start;

    private ValueStopwatch(long start) => _start = start;

    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);
}
