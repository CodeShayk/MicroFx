using System.Data.Common;
using MicroFx.MultiTenancy;
using MicroFx.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MicroFx.Persistence;

/// <summary>An entity carrying audit columns.</summary>
public interface IAuditable
{
    /// <summary>When the entity was created.</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who created it.</summary>
    string? CreatedBy { get; set; }

    /// <summary>When it was last changed.</summary>
    DateTimeOffset? ModifiedAt { get; set; }

    /// <summary>Who last changed it.</summary>
    string? ModifiedBy { get; set; }
}

/// <summary>An entity belonging to exactly one tenant.</summary>
public interface ITenantOwned
{
    /// <summary>Owning tenant.</summary>
    string TenantId { get; set; }
}

/// <summary>Fills audit columns from the ambient caller and clock.</summary>
/// <remarks>
/// Applied by an interceptor rather than by application code, because an audit column filled only
/// where someone remembered is worse than no audit column at all — it looks complete and is not.
/// </remarks>
internal sealed class AuditInterceptor(TimeProvider clock, IServiceProvider services)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context)
        {
            Apply(context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext context)
    {
        var now = clock.GetUtcNow();
        var actor = CurrentActor();

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = actor;
                    break;

                case EntityState.Modified:
                    entry.Entity.ModifiedAt = now;
                    entry.Entity.ModifiedBy = actor;

                    // Creation facts are immutable. Without this an update could rewrite who
                    // created a record, which is exactly what an audit trail must prevent.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    entry.Property(e => e.CreatedBy).IsModified = false;
                    break;

                default:
                    break;
            }
        }
    }

    private string? CurrentActor()
    {
        var accessor = services.GetService(typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor))
            as Microsoft.AspNetCore.Http.IHttpContextAccessor;

        var user = accessor?.HttpContext?.User;
        var subject = user?.FindFirst("sub")?.Value ?? user?.Identity?.Name;

        // Bounded, because it lands in a fixed-width column and originates in a token claim.
        return subject is { Length: > 128 } ? subject[..128] : subject;
    }
}

/// <summary>
/// Refuses to write an entity belonging to a different tenant than the one in scope.
/// </summary>
/// <remarks>
/// A global query filter protects <em>reads</em> only. Without a write-side guard, code that loaded
/// an entity outside a tenant scope — or constructed one with an attacker-supplied tenant id — can
/// write across the boundary, and nothing in the read path would ever notice.
/// </remarks>
internal sealed partial class TenantGuardInterceptor(
    IServiceProvider services, ILogger<TenantGuardInterceptor> logger) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context &&
            services.GetService(typeof(ITenantContext)) is ITenantContext { Current: { } tenant })
        {
            await GuardAsync(context, tenant, cancellationToken).ConfigureAwait(false);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask GuardAsync(DbContext context, string tenant, CancellationToken cancellationToken)
    {
        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            // Stamped rather than trusted on insert: a caller-supplied tenant id on a new entity is
            // an assertion, and the ambient tenant is the verified fact.
            if (entry.State == EntityState.Added && string.IsNullOrEmpty(entry.Entity.TenantId))
            {
                entry.Entity.TenantId = tenant;
                continue;
            }

            if (string.Equals(entry.Entity.TenantId, tenant, StringComparison.Ordinal))
            {
                continue;
            }

            LogCrossTenantWrite(logger, entry.Entity.TenantId, tenant, entry.Metadata.Name);

            if (services.GetService(typeof(AuditRecorder)) is AuditRecorder recorder &&
                services.GetService(typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor))
                    is Microsoft.AspNetCore.Http.IHttpContextAccessor { HttpContext: { } httpContext })
            {
                await recorder.RecordAsync(
                    AuditEventKind.CrossTenantAccess, httpContext, "cross-tenant-write", cancellationToken)
                    .ConfigureAwait(false);
            }

            // Almost always a bug or an attack, never routine, so it fails loudly rather than
            // silently dropping the change.
            throw new CrossTenantWriteException(entry.Entity.TenantId, tenant);
        }
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Critical,
        Message = "Cross-tenant write blocked: entity of {EntityType} belongs to {EntityTenant} " +
                  "but {AmbientTenant} is in scope.")]
    private static partial void LogCrossTenantWrite(
        ILogger logger, string entityTenant, string ambientTenant, string entityType);
}

/// <summary>Thrown when a write would cross a tenant boundary.</summary>
public sealed class CrossTenantWriteException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CrossTenantWriteException(string entityTenant, string ambientTenant)
        : base($"A write to an entity owned by tenant '{entityTenant}' was attempted while tenant " +
               $"'{ambientTenant}' was in scope.")
    {
        EntityTenant = entityTenant;
        AmbientTenant = ambientTenant;
    }

    /// <summary>Creates the exception.</summary>
    public CrossTenantWriteException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public CrossTenantWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public CrossTenantWriteException()
    {
    }

    /// <summary>Tenant the entity belongs to.</summary>
    public string? EntityTenant { get; }

    /// <summary>Tenant in scope at the time.</summary>
    public string? AmbientTenant { get; }
}

/// <summary>Logs commands slower than a threshold.</summary>
/// <remarks>
/// The SQL text is logged but its parameter <em>values</em> never are: parameters routinely carry
/// personal data, tokens, and identifiers that have no business in a log.
/// </remarks>
internal sealed partial class SlowQueryInterceptor(
    TimeSpan threshold, ILogger<SlowQueryInterceptor> logger) : DbCommandInterceptor
{
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Report(eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Report(eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void Report(CommandExecutedEventData eventData)
    {
        if (eventData.Duration < threshold)
        {
            return;
        }

        var text = eventData.Command.CommandText;
        LogSlowQuery(
            logger,
            eventData.Duration.TotalMilliseconds,
            text.Length > 512 ? text[..512] : text);
    }

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning,
        Message = "Slow database command took {DurationMs}ms: {CommandText}")]
    private static partial void LogSlowQuery(ILogger logger, double durationMs, string commandText);
}
