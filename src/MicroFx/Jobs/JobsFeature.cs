using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cronos;
using MicroFx.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MicroFx.Jobs;

/// <summary>A unit of scheduled work.</summary>
public interface IJob
{
    /// <summary>
    /// Runs the job.
    /// </summary>
    /// <remarks>
    /// Must be idempotent and safe to re-run. A lease can expire mid-execution and another replica
    /// can start the same job, so "runs exactly once" is not a guarantee any scheduler can make.
    /// </remarks>
    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>What a job knows about the run it is performing.</summary>
/// <param name="JobName">The job's registered name.</param>
/// <param name="ScheduledFor">When this run was due.</param>
/// <param name="StartedAt">When it actually started.</param>
/// <param name="IsSingleton">Whether it holds a distributed lock for the duration.</param>
public readonly record struct JobContext(
    string JobName, DateTimeOffset ScheduledFor, DateTimeOffset StartedAt, bool IsSingleton);

/// <summary>Options for the jobs feature, bound from <c>MicroFx:Jobs</c>.</summary>
public sealed class JobsOptions
{
    /// <summary>How often the scheduler evaluates due jobs.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Default lease held by a singleton job.</summary>
    public TimeSpan DefaultLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Default wall-clock budget for one run.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Concurrent job runs permitted across the whole scheduler.</summary>
    [Range(1, 1000)]
    public int MaxConcurrency { get; set; } = 4;
}

/// <summary>One registered job.</summary>
internal sealed record JobRegistration
{
    public required string Name { get; init; }
    public required Type JobType { get; init; }
    public CronExpression? Cron { get; init; }
    public TimeSpan? Interval { get; init; }
    public bool Singleton { get; init; } = true;
    public TimeSpan? Timeout { get; init; }
    public TimeSpan? LeaseDuration { get; init; }
    public TimeSpan? StalenessThreshold { get; init; }
    public DateTimeOffset? NextDueAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
}

/// <summary>Declares the service's scheduled work.</summary>
public sealed class JobsBuilder
{
    internal List<JobRegistration> Registrations { get; } = [];

    /// <summary>
    /// Registers a job on a cron schedule.
    /// </summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    /// <param name="name">Stable name. Used as the lock resource, so it must not change casually.</param>
    /// <param name="cron">Standard five- or six-field cron expression, evaluated in UTC.</param>
    /// <param name="configure">Adjusts singleton, timeout, lease, and staleness.</param>
    public JobsBuilder AddCronJob<TJob>(
        string name, string cron, Action<JobBuilder>? configure = null)
        where TJob : class, IJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);

        var format = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6
            ? CronFormat.IncludeSeconds
            : CronFormat.Standard;

        // Parsed at composition time, so an invalid expression fails the deployment rather than
        // silently never firing.
        var expression = CronExpression.Parse(cron, format);

        var builder = new JobBuilder();
        configure?.Invoke(builder);

        Registrations.Add(builder.Build(name, typeof(TJob)) with { Cron = expression });
        return this;
    }

    /// <summary>Registers a job on a fixed interval.</summary>
    /// <typeparam name="TJob">The job type.</typeparam>
    public JobsBuilder AddIntervalJob<TJob>(
        string name, TimeSpan interval, Action<JobBuilder>? configure = null)
        where TJob : class, IJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        var builder = new JobBuilder();
        configure?.Invoke(builder);

        Registrations.Add(builder.Build(name, typeof(TJob)) with { Interval = interval });
        return this;
    }
}

/// <summary>Adjusts one job.</summary>
public sealed class JobBuilder
{
    private bool _singleton = true;
    private TimeSpan? _timeout;
    private TimeSpan? _lease;
    private TimeSpan? _staleness;

    /// <summary>
    /// Whether the job runs on one replica at a time. On by default: a scheduled job that runs on
    /// every replica is the most common way a nightly task becomes a nightly incident.
    /// </summary>
    public JobBuilder AsSingleton(bool singleton = true)
    {
        _singleton = singleton;
        return this;
    }

    /// <summary>Sets the wall-clock budget for one run.</summary>
    public JobBuilder WithTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _timeout = timeout;
        return this;
    }

    /// <summary>Sets the lease held while running. Must exceed the expected run time.</summary>
    public JobBuilder WithLease(TimeSpan lease)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        _lease = lease;
        return this;
    }

    /// <summary>
    /// How long without a successful run before readiness reports the job as stale.
    /// </summary>
    /// <remarks>
    /// The "job did not run" alarm. A job that stops firing produces no errors at all, so silence
    /// has to be made observable or nobody notices until the work is missed.
    /// </remarks>
    public JobBuilder WithStalenessThreshold(TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        _staleness = threshold;
        return this;
    }

    internal JobRegistration Build(string name, Type jobType) => new()
    {
        Name = name,
        JobType = jobType,
        Singleton = _singleton,
        Timeout = _timeout,
        LeaseDuration = _lease,
        StalenessThreshold = _staleness,
    };
}

/// <summary>
/// Background and scheduled work: cron and interval schedules, distributed locking, leader
/// election, and staleness detection.
/// </summary>
public sealed class JobsFeature : IMicroFxFeature, IFeatureValidator
{
    private readonly List<Action<JobsBuilder>> _configurations = [];

    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Jobs,
        DisplayName = "Jobs",
        Order = 360,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.Persistence, BuiltIn.Messaging],
        EnabledByDefault = false,   // a service with no scheduled work carries none of this
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Jobs",
    };

    /// <summary>Declares the service's scheduled work.</summary>
    public JobsFeature Configure(Action<JobsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configurations.Add(configure);
        return this;
    }

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<JobsOptions>();

        var builder = new JobsBuilder();
        foreach (var configure in _configurations)
        {
            configure(builder);
        }

        foreach (var registration in builder.Registrations)
        {
            context.Services.TryAddScoped(registration.JobType);
        }

        var registry = new JobRegistry(builder.Registrations);
        context.Services.TryAddSingleton(registry);
        context.Services.TryAddSingleton<IDistributedLock>(provider =>
            new InProcessDistributedLock(provider.GetRequiredService<TimeProvider>()));

        context.Services.AddHostedService<JobScheduler>();
        context.AddMeter(JobMetrics.MeterName);
        context.AddDiagnosticSource(JobMetrics.ActivitySourceName);

        // Staleness is readiness, not liveness: a job that has not run does not mean the process
        // is broken, and restarting it would not make the job run any sooner.
        context.AddHealthContribution(HealthContribution.Ready(
            "jobs-freshness",
            (provider, _) =>
            {
                var jobs = provider.GetRequiredService<JobRegistry>();
                var clock = provider.GetRequiredService<TimeProvider>();
                var stale = jobs.StaleJobs(clock.GetUtcNow()).ToList();

                return ValueTask.FromResult(stale.Count == 0
                    ? HealthCheckResult.Healthy("All jobs are within their staleness thresholds.")
                    : HealthCheckResult.Unhealthy(
                        $"Jobs have not run within their thresholds: {string.Join(", ", stale)}."));
            }));

        context.Report("jobs", builder.Registrations.Count);
        context.Report("singleton", builder.Registrations.Count(r => r.Singleton));
    }

    /// <inheritdoc />
    public ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<ValidationFinding>();
        var registry = context.Services.GetRequiredService<JobRegistry>();
        var distributedLock = context.Services.GetRequiredService<IDistributedLock>();
        var options = context.Services.GetRequiredService<IOptions<JobsOptions>>().Value;

        // The failure mode is silent: every replica acquires its own in-process lock, every replica
        // runs the job, and nothing reports an error. Saying so is the only defence.
        if (!distributedLock.IsDistributed &&
            registry.All.Any(job => job.Singleton) &&
            !context.Metadata.IsDevelopment)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                "Singleton jobs are registered but the distributed lock is in-process only, so each " +
                "replica will run them independently. Reference a lock adapter, or accept N runs."));
        }

        foreach (var job in registry.All)
        {
            var lease = job.LeaseDuration ?? options.DefaultLeaseDuration;
            var timeout = job.Timeout ?? options.DefaultTimeout;

            // A lease shorter than the run is a duplicate-execution generator: the lease expires
            // mid-run, another replica acquires it, and both are working at once.
            if (job.Singleton && lease <= timeout)
            {
                findings.Add(new ValidationFinding(
                    ValidationSeverity.Error,
                    $"Job '{job.Name}' has a lease ({lease}) no longer than its timeout ({timeout}). " +
                    "The lease would expire mid-run and a second replica would start the same job."));
            }
        }

        return ValueTask.FromResult(
            findings.Count == 0 ? ValidationReport.Ok() : ValidationReport.FromFindings(findings));
    }
}

/// <summary>The registered jobs and their run history.</summary>
internal sealed class JobRegistry(IReadOnlyList<JobRegistration> registrations)
{
    public IReadOnlyList<JobRegistration> All { get; } = registrations;

    public IEnumerable<string> StaleJobs(DateTimeOffset now) =>
        All.Where(job =>
            job.StalenessThreshold is { } threshold &&
            (job.LastSuccessAt is null || now - job.LastSuccessAt > threshold))
           .Select(job => job.Name);
}

/// <summary>Job metrics and traces.</summary>
internal static class JobMetrics
{
    public const string MeterName = "MicroFx.Jobs";
    public const string ActivitySourceName = "MicroFx.Jobs";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Runs = Meter.CreateCounter<long>(
        "jobs.run.count", description: "Job runs by outcome.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "jobs.run.duration", unit: "s", description: "Job run duration.");

    private static readonly Counter<long> Skipped = Meter.CreateCounter<long>(
        "jobs.skipped.count",
        description: "Runs skipped because another replica held the lock, or the previous run " +
                     "had not finished.");

    public static void Run(string job, string outcome, TimeSpan elapsed)
    {
        Runs.Add(1,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("outcome", outcome));

        Duration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("job", job));
    }

    public static void Skip(string job, string reason) =>
        Skipped.Add(1,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("reason", reason));
}

/// <summary>Evaluates schedules and runs due jobs.</summary>
internal sealed partial class JobScheduler(
    JobRegistry registry,
    IDistributedLock distributedLock,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<JobsOptions> options,
    ILogger<JobScheduler> logger) : BackgroundService
{
    private readonly JobsOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private SemaphoreSlim? _concurrency;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (registry.All.Count == 0)
        {
            return;
        }

        _concurrency = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);

        var now = clock.GetUtcNow();
        foreach (var job in registry.All)
        {
            job.NextDueAt = NextOccurrence(job, now);
        }

        using var timer = new PeriodicTimer(_options.TickInterval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            now = clock.GetUtcNow();

            foreach (var job in registry.All)
            {
                if (job.NextDueAt is not { } due || due > now)
                {
                    continue;
                }

                job.NextDueAt = NextOccurrence(job, now);

                // Overlap prevention. A job still running when its next occurrence arrives is
                // behind, and starting a second copy makes it further behind, not less.
                if (!_running.TryAdd(job.Name, 0))
                {
                    JobMetrics.Skip(job.Name, "already-running");
                    LogOverlap(logger, job.Name);
                    continue;
                }

                _ = RunAsync(job, due, stoppingToken);
            }
        }
    }

    private async Task RunAsync(JobRegistration job, DateTimeOffset due, CancellationToken stoppingToken)
    {
        var started = clock.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();
        IDistributedLockHandle? handle = null;

        try
        {
            await _concurrency!.WaitAsync(stoppingToken).ConfigureAwait(false);

            if (job.Singleton)
            {
                var lease = job.LeaseDuration ?? _options.DefaultLeaseDuration;
                handle = await distributedLock
                    .TryAcquireAsync($"microfx.job.{job.Name}", lease, stoppingToken)
                    .ConfigureAwait(false);

                if (handle is null)
                {
                    // Expected on every replica but one. Not an error, and not logged as one.
                    JobMetrics.Skip(job.Name, "not-leader");
                    return;
                }
            }

            using var activity = JobMetrics.Source.StartActivity($"job {job.Name}", ActivityKind.Internal);
            activity?.SetTag("job.name", job.Name);
            activity?.SetTag("job.singleton", job.Singleton);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(job.Timeout ?? _options.DefaultTimeout);

            await using var scope = scopeFactory.CreateAsyncScope();
            var instance = (IJob)scope.ServiceProvider.GetRequiredService(job.JobType);

            await instance
                .ExecuteAsync(new JobContext(job.Name, due, started, job.Singleton), timeout.Token)
                .ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(stopwatch);
            job.LastSuccessAt = clock.GetUtcNow();
            JobMetrics.Run(job.Name, "success", elapsed);
            LogCompleted(logger, job.Name, elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            JobMetrics.Run(job.Name, "cancelled", Stopwatch.GetElapsedTime(stopwatch));
        }
        catch (OperationCanceledException)
        {
            JobMetrics.Run(job.Name, "timeout", Stopwatch.GetElapsedTime(stopwatch));
            LogTimedOut(logger, job.Name);
        }
        catch (Exception ex)
        {
            // One failing job must never stop the scheduler: every other job still has to run.
            JobMetrics.Run(job.Name, "failed", Stopwatch.GetElapsedTime(stopwatch));
            LogFailed(logger, job.Name, ex);
        }
        finally
        {
            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }

            _concurrency!.Release();
            _running.TryRemove(job.Name, out _);
        }
    }

    private static DateTimeOffset? NextOccurrence(JobRegistration job, DateTimeOffset now)
    {
        if (job.Interval is { } interval)
        {
            return now + interval;
        }

        // Evaluated in UTC on purpose: a cron schedule interpreted in local time silently shifts —
        // or runs twice, or not at all — across a daylight-saving transition.
        return job.Cron?.GetNextOccurrence(now, TimeZoneInfo.Utc);
    }

    public override void Dispose()
    {
        _concurrency?.Dispose();
        base.Dispose();
    }

    [LoggerMessage(EventId = 7401, Level = LogLevel.Information,
        Message = "Job {JobName} completed in {DurationSeconds}s.")]
    private static partial void LogCompleted(ILogger logger, string jobName, double durationSeconds);

    [LoggerMessage(EventId = 7402, Level = LogLevel.Error,
        Message = "Job {JobName} failed.")]
    private static partial void LogFailed(ILogger logger, string jobName, Exception exception);

    [LoggerMessage(EventId = 7403, Level = LogLevel.Warning,
        Message = "Job {JobName} exceeded its timeout and was cancelled.")]
    private static partial void LogTimedOut(ILogger logger, string jobName);

    [LoggerMessage(EventId = 7404, Level = LogLevel.Warning,
        Message = "Job {JobName} was still running when its next occurrence arrived; the run was skipped.")]
    private static partial void LogOverlap(ILogger logger, string jobName);
}
