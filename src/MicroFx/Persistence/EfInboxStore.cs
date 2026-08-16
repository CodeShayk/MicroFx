using MicroFx.Core;
using MicroFx.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MicroFx.Persistence;

/// <summary>
/// A durable inbox: deduplication that survives a restart.
/// </summary>
/// <remarks>
/// The in-memory inbox forgets everything on restart, so a message in flight across one is
/// processed twice. This one persists, which is what makes "at-least-once delivery with dedupe"
/// actually mean effectively-once handling.
/// </remarks>
internal sealed class EfInboxStore<TContext>(TContext context, TimeProvider clock) : IInboxStore
    where TContext : DbContext
{
    public async ValueTask<bool> TryBeginAsync(
        string consumerGroup, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var entry = context.Set<InboxMessage>().Add(new InboxMessage
        {
            ConsumerGroup = consumerGroup,
            MessageId = messageId,
            ProcessedAt = clock.GetUtcNow().UtcDateTime,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // The composite primary key is the atomic primitive: exactly one concurrent inserter
            // wins and the rest see a constraint violation. A read-then-insert would let two
            // redeliveries both observe "not seen" and both run the handler.
            return false;
        }
        finally
        {
            // Detached either way. A tracked inbox row would make a later admission of the same key
            // throw an identity conflict instead of returning a clean "already seen", and would
            // pin one row's worth of state for the whole life of the scope.
            entry.State = EntityState.Detached;
        }
    }

    public async ValueTask ReleaseAsync(
        string consumerGroup, string messageId, CancellationToken cancellationToken = default)
    {
        Detach(consumerGroup, messageId);

        await context.Set<InboxMessage>()
            .Where(m => m.ConsumerGroup == consumerGroup && m.MessageId == messageId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes any tracked copy, so a later re-admission is not an identity conflict.</summary>
    private void Detach(string consumerGroup, string messageId)
    {
        foreach (var tracked in context.ChangeTracker.Entries<InboxMessage>())
        {
            if (string.Equals(tracked.Entity.ConsumerGroup, consumerGroup, StringComparison.Ordinal) &&
                string.Equals(tracked.Entity.MessageId, messageId, StringComparison.Ordinal))
            {
                tracked.State = EntityState.Detached;
            }
        }
    }

    public async ValueTask<int> PurgeAsync(
        TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - retention;

        return await context.Set<InboxMessage>()
            .Where(m => m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Decides whether the service may serve traffic given the schema it finds.
/// </summary>
/// <remarks>
/// <para>
/// Asserts rather than migrates. Applying migrations at startup races N replicas, and a rolling
/// deploy that migrates from the new version leaves the old version running against a schema it
/// was never written for — which is how a rollback stops being possible.
/// </para>
/// <para>
/// Schema creation is offered only in Development, where there is no pipeline and a developer's
/// first run should simply work.
/// </para>
/// </remarks>
internal sealed partial class MigrationGate(
    DbContext context,
    ServiceMetadata metadata,
    PersistenceOptions options,
    ILogger<MigrationGate> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (options.CreateSchemaOnStartup && metadata.IsDevelopment)
        {
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            LogSchemaCreated(logger);
            return;
        }

        if (!options.AssertMigrations)
        {
            return;
        }

        // A context with no migrations is a legitimate configuration — a service using
        // EnsureCreated in tests, or one whose schema is owned elsewhere entirely.
        IReadOnlyList<string> pending;
        try
        {
            pending = [.. await context.Database
                .GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)];
        }
        catch (InvalidOperationException)
        {
            LogMigrationsUnavailable(logger);
            return;
        }

        if (pending.Count == 0)
        {
            return;
        }

        // Failing startup is loud and cheap. Serving traffic against a schema the code does not
        // expect is quiet and expensive.
        throw new MigrationGateException(
            $"{pending.Count} migration(s) have not been applied: {string.Join(", ", pending)}. " +
            "Migrations are applied by a pipeline stage before the application deploys, so this " +
            "deployment would run against a schema it was not written for.");
    }

    [LoggerMessage(EventId = 7301, Level = LogLevel.Warning,
        Message = "Created the database schema at startup. Development only.")]
    private static partial void LogSchemaCreated(ILogger logger);

    [LoggerMessage(EventId = 7302, Level = LogLevel.Debug,
        Message = "The context defines no migrations; the migration gate is skipped.")]
    private static partial void LogMigrationsUnavailable(ILogger logger);
}

/// <summary>Thrown when the database schema does not match what the code expects.</summary>
public sealed class MigrationGateException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MigrationGateException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MigrationGateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MigrationGateException()
    {
    }
}

/// <summary>Registers the platform's own entities on a service's context.</summary>
public static class MicroFxModelBuilderExtensions
{
    /// <summary>
    /// Maps the outbox and inbox tables.
    /// </summary>
    /// <remarks>
    /// Called from the service's <c>OnModelCreating</c>. Explicit rather than magical, because the
    /// service owns its schema and should be able to see every table in it.
    /// </remarks>
    public static ModelBuilder ApplyMicroFxPersistence(this ModelBuilder modelBuilder, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("microfx_outbox", schema);
            entity.HasKey(m => m.Id);

            // Unique, so a double-write of the same envelope cannot produce two publishes.
            entity.HasIndex(m => m.MessageId).IsUnique();

            // Covers the relay's claim query, which is the hot path and runs every poll.
            entity.HasIndex(m => new { m.DispatchedAt, m.NextAttemptAt, m.AggregateId, m.Id });
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("microfx_inbox", schema);

            // The composite key is what makes admission atomic: the database refuses the second
            // insert rather than the application having to check first.
            entity.HasKey(m => new { m.ConsumerGroup, m.MessageId });
            entity.HasIndex(m => m.ProcessedAt);
        });

        return modelBuilder;
    }
}
