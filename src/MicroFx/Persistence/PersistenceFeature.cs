using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MicroFx.Features;
using MicroFx.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace MicroFx.Persistence;

/// <summary>Options for the persistence feature, bound from <c>MicroFx:Persistence</c>.</summary>
public sealed class PersistenceOptions
{
    /// <summary>How often the outbox relay polls.</summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Rows claimed per relay batch.</summary>
    [Range(1, 1000)]
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>
    /// How long a relay owns a claimed row. Must exceed the worst-case dispatch time, or a slow
    /// dispatch will have its rows stolen and published twice.
    /// </summary>
    public TimeSpan OutboxLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Retry policy for failed dispatch attempts.</summary>
    public RetryPolicy OutboxRetry { get; set; } = RetryPolicy.Default;

    /// <summary>Oldest-pending age above which the relay warns that events have stopped flowing.</summary>
    public TimeSpan OutboxLagAlertThreshold { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long dispatched rows are retained before purging.</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(3);

    /// <summary>How long the inbox remembers a processed message id.</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether an unconfirmed publish marks an outbox row dispatched.
    /// </summary>
    /// <remarks>
    /// Off by default. The row is the only remaining record that the message should exist, so
    /// discarding it on an unconfirmed publish converts "possibly lost" into "definitely lost".
    /// </remarks>
    public bool AllowUnconfirmedOutboxDispatch { get; set; }

    /// <summary>Commands slower than this are logged.</summary>
    public TimeSpan SlowQueryThreshold { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether the schema may be created at startup.
    /// </summary>
    /// <remarks>
    /// Development only, and refused elsewhere. In production, migrations are a pipeline stage:
    /// running them from N racing replicas is how a rollback stops working.
    /// </remarks>
    public bool CreateSchemaOnStartup { get; set; }

    /// <summary>Whether the migration gate fails startup when applied migrations do not match.</summary>
    public bool AssertMigrations { get; set; } = true;
}

/// <summary>Declares what persistence the service uses.</summary>
public sealed class PersistenceBuilder
{
    internal Type? ContextType { get; private set; }

    internal Action<DbContextOptionsBuilder>? ConfigureContext { get; private set; }

    internal bool OutboxEnabled { get; private set; }

    internal bool InboxEnabled { get; private set; }

    /// <summary>Registers the service's <see cref="DbContext"/> and its provider.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    public PersistenceBuilder UseDbContext<TContext>(Action<DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        ContextType = typeof(TContext);
        ConfigureContext = configure;
        return this;
    }

    /// <summary>Enables the transactional outbox.</summary>
    public PersistenceBuilder UseOutbox()
    {
        OutboxEnabled = true;
        return this;
    }

    /// <summary>Enables the durable inbox.</summary>
    public PersistenceBuilder UseInbox()
    {
        InboxEnabled = true;
        return this;
    }
}

/// <summary>
/// EF Core persistence: transactions, the transactional outbox, the durable inbox, audit and tenant
/// interceptors, and the migration gate.
/// </summary>
/// <remarks>
/// EF Core is built in rather than reached through an adapter, because the outbox is <em>defined</em>
/// by committing atomically with a state change and a stubbed store demonstrates nothing. The core
/// references <c>EntityFrameworkCore.Relational</c> only — no driver — so the service supplies its
/// own provider and changing database engine needs no MicroFx package.
/// </remarks>
public sealed class PersistenceFeature : IMicroFxFeature, IFeatureLifecycle, IFeatureValidator
{
    private readonly List<Action<PersistenceBuilder>> _configurations = [];
    private PersistenceBuilder? _builder;

    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Persistence,
        DisplayName = "Persistence",
        Order = 350,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.Security, BuiltIn.MultiTenancy],
        Before = [BuiltIn.Messaging],
        EnabledByDefault = false,   // a stateless service should carry no data access
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Persistence",
    };

    /// <summary>Declares the service's persistence.</summary>
    public PersistenceFeature Configure(Action<PersistenceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configurations.Add(configure);
        return this;
    }

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new PersistenceOptions();
        context.Configuration.GetSection("MicroFx:Persistence").Bind(options);
        context.AddValidatedOptions<PersistenceOptions>();

        var builder = new PersistenceBuilder();
        foreach (var configure in _configurations)
        {
            configure(builder);
        }

        _builder = builder;

        if (builder.ContextType is null || builder.ConfigureContext is null)
        {
            // Enabled but not configured. Reported rather than silently doing nothing, so the gap
            // is visible in the catalog instead of surfacing as a missing service later.
            context.Report("store", "not configured");
            return;
        }

        RegisterContext(context, builder, options);

        context.Services.TryAddSingleton<OutboxDomainEventProjector>();

        if (builder.OutboxEnabled)
        {
            context.Services.AddHostedService<OutboxRelay>();
            context.Services.AddHostedService<OutboxMaintenance>();
            context.AddMeter(OutboxMetrics.MeterName);
        }

        context.AddHealthContribution(HealthContribution.Ready(
            "database",
            async (provider, cancellationToken) =>
            {
                await using var scope = provider.CreateAsyncScope();
                var database = (DbContext)scope.ServiceProvider.GetRequiredService(builder.ContextType);

                return await database.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy("Database is reachable.")
                    // The message is deliberately generic: a provider exception carries the
                    // connection string, and readiness output is not the place for it.
                    : HealthCheckResult.Unhealthy("Database is unreachable.");
            },
            TimeSpan.FromSeconds(2)));

        context.Report("store", "ef-core");
        context.Report("outbox", builder.OutboxEnabled);
        context.Report("inbox", builder.InboxEnabled);
    }

    /// <inheritdoc />
    public async ValueTask StartingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_builder?.ContextType is null)
        {
            return;
        }

        var options = context.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<PersistenceOptions>>().Value;

        await using var scope = context.Services.CreateAsyncScope();
        var database = (DbContext)scope.ServiceProvider.GetRequiredService(_builder.ContextType);

        var gate = new MigrationGate(
            database,
            context.Metadata,
            options,
            scope.ServiceProvider.GetRequiredService<ILogger<MigrationGate>>());

        await gate.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<ValidationFinding>();
        var options = context.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<PersistenceOptions>>().Value;

        if (_builder?.ContextType is null)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                "Persistence is enabled but no DbContext is configured. Call " +
                "Configure(p => p.UseDbContext<T>(...)) or disable the feature."));
        }

        // Creating a schema at startup races N replicas and leaves no migration history to roll
        // back to, so it is a Development-only convenience.
        if (options.CreateSchemaOnStartup && !context.Metadata.IsDevelopment)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                "CreateSchemaOnStartup is set outside Development. Schema changes belong to a " +
                "pipeline stage, not to N racing replicas."));
        }

        if (options.AllowUnconfirmedOutboxDispatch)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                "AllowUnconfirmedOutboxDispatch is set: an outbox row is discarded even when the " +
                "transport did not confirm the publish, so a lost message is not recoverable."));
        }

        // The lease is the only thing preventing two relays from dispatching the same row, so a
        // lease shorter than the poll interval is a duplicate-publish generator.
        if (options.OutboxLeaseDuration <= options.OutboxPollInterval)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                $"OutboxLeaseDuration ({options.OutboxLeaseDuration}) must exceed " +
                $"OutboxPollInterval ({options.OutboxPollInterval}), or two relays will claim and " +
                "publish the same row."));
        }

        return ValueTask.FromResult(
            findings.Count == 0 ? ValidationReport.Ok() : ValidationReport.FromFindings(findings));
    }

    private static void RegisterContext(
        FeatureBuildContext context, PersistenceBuilder builder, PersistenceOptions options)
    {
        var contextType = builder.ContextType!;

        context.Services.TryAddSingleton<AuditInterceptor>();
        context.Services.TryAddSingleton<TenantGuardInterceptor>();
        context.Services.TryAddSingleton(provider => new SlowQueryInterceptor(
            options.SlowQueryThreshold,
            provider.GetRequiredService<ILogger<SlowQueryInterceptor>>()));

        // AddDbContext with the service's own provider configuration, plus the platform's
        // interceptors. The service never has to remember to add them.
        var addDbContext = typeof(EntityFrameworkServiceCollectionExtensions)
            .GetMethods()
            .First(m => m.Name == nameof(EntityFrameworkServiceCollectionExtensions.AddDbContext) &&
                        m.GetGenericArguments().Length == 1 &&
                        m.GetParameters().Length == 4 &&
                        m.GetParameters()[1].ParameterType ==
                            typeof(Action<IServiceProvider, DbContextOptionsBuilder>))
            .MakeGenericMethod(contextType);

        Action<IServiceProvider, DbContextOptionsBuilder> configure = (provider, dbOptions) =>
        {
            builder.ConfigureContext!(dbOptions);

            dbOptions.AddInterceptors(
                provider.GetRequiredService<AuditInterceptor>(),
                provider.GetRequiredService<TenantGuardInterceptor>(),
                provider.GetRequiredService<SlowQueryInterceptor>());

            // Tracked entities are not shared across requests, so lazy-loading proxies and
            // detailed errors that echo parameter values stay off.
            dbOptions.EnableDetailedErrors(false);
            dbOptions.EnableSensitiveDataLogging(false);
            dbOptions.ConfigureWarnings(warnings =>
                warnings.Log(RelationalEventId.MultipleCollectionIncludeWarning));
        };

        addDbContext.Invoke(null, [context.Services, configure, ServiceLifetime.Scoped, ServiceLifetime.Scoped]);

        // Closed over the concrete context type, so the unit of work and stores bind to the
        // service's own context without the service naming them.
        var unitOfWorkType = typeof(EfUnitOfWork<>).MakeGenericType(contextType);
        var outboxStoreType = typeof(EfOutboxStore<>).MakeGenericType(contextType);
        var inboxStoreType = typeof(EfInboxStore<>).MakeGenericType(contextType);

        context.Services.TryAddScoped(typeof(IUnitOfWork), unitOfWorkType);

        if (builder.OutboxEnabled)
        {
            context.Services.TryAddScoped(typeof(IOutboxStore), outboxStoreType);
        }

        if (builder.InboxEnabled)
        {
            // Replaces the in-memory inbox the messaging feature registers, so dedupe survives a
            // restart instead of quietly reverting to at-least-once.
            context.Services.RemoveAll<IInboxStore>();
            context.Services.TryAddScoped(typeof(IInboxStore), inboxStoreType);
        }
    }
}

/// <summary>Turns an integration event into an outbox row.</summary>
/// <remarks>
/// Used inside the caller's transaction, so the state change and the intent to publish commit
/// together. Domain events that are not integration events never reach a transport.
/// </remarks>
public sealed class OutboxDomainEventProjector(
    MessageTypeRegistry types,
    DestinationRegistry destinations,
    Core.ServiceMetadata metadata,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<MessagingOptions> messagingOptions)
{
    /// <summary>Builds the outbox row for an integration event.</summary>
    /// <param name="integrationEvent">The event to publish.</param>
    /// <param name="aggregateId">Ordering scope. Events for one aggregate dispatch in write order.</param>
    /// <param name="tenantId">Tenant to stamp on the envelope.</param>
    public OutboxMessage Project(object integrationEvent, string aggregateId, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var messageType = integrationEvent.GetType();
        var wireName = types.RequireWireName(messageType);
        var destination = destinations.Require(messageType);
        var now = clock.GetUtcNow();

        var envelope = new Envelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = wireName,
            Source = metadata.Name,
            Time = now,
            Kind = MessageKind.Event,
            CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString()
                            ?? Guid.NewGuid().ToString("N"),
            CausationId = System.Diagnostics.Activity.Current?.SpanId.ToString(),
            TenantId = tenantId,
            TraceParent = System.Diagnostics.Activity.Current?.Id,
        };

        return new OutboxMessage
        {
            MessageId = envelope.Id,
            AggregateId = aggregateId,
            Destination = MessageDestinationCodec.Format(destination),
            Headers = JsonSerializer.Serialize(EnvelopeCodec.Encode(envelope)),
            // The messaging feature's serializer options, not the defaults. The consumer
            // deserializes with camelCase and case-sensitive matching, so a PascalCase payload
            // would bind to nothing and the handler would receive an object of nulls.
            Body = JsonSerializer.SerializeToUtf8Bytes(
                integrationEvent, messageType, messagingOptions.Value.SerializerOptions),
            OccurredAt = now.UtcDateTime,
            NextAttemptAt = now.UtcDateTime,
        };
    }
}

/// <summary>Purges dispatched outbox rows and expired inbox entries.</summary>
/// <remarks>
/// Both tables grow without bound otherwise. The inbox in particular is written once per message
/// forever, so retention is not optional at any real volume.
/// </remarks>
internal sealed partial class OutboxMaintenance(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<PersistenceOptions> options,
    ILogger<OutboxMaintenance> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), clock);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                var purged = await outbox
                    .PurgeDispatchedAsync(options.Value.OutboxRetention, stoppingToken)
                    .ConfigureAwait(false);

                var inbox = scope.ServiceProvider.GetService<IInboxStore>();
                var expired = inbox is null
                    ? 0
                    : await inbox.PurgeAsync(options.Value.InboxRetention, stoppingToken)
                        .ConfigureAwait(false);

                if (purged + expired > 0)
                {
                    LogPurged(logger, purged, expired);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPurgeFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(EventId = 7201, Level = LogLevel.Information,
        Message = "Purged {OutboxRows} dispatched outbox rows and {InboxRows} expired inbox entries.")]
    private static partial void LogPurged(ILogger logger, int outboxRows, int inboxRows);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning,
        Message = "Outbox maintenance failed; retrying on the next tick.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);
}
