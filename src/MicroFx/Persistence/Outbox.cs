using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace MicroFx.Persistence;

/// <summary>
/// A message persisted atomically with the state change that produced it.
/// </summary>
/// <remarks>
/// The outbox exists because "save the order" and "publish OrderPlaced" cannot both be made to
/// happen without a shared transaction. Writing the intent to publish into the same database
/// transaction as the state change turns two unreliable operations into one reliable one, at the
/// cost of at-least-once delivery — which the consumer inbox already handles.
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>Store-assigned sequence. Dispatch order within an aggregate follows it.</summary>
    public long Id { get; set; }

    /// <summary>Envelope id. Unique, and the consumer's deduplication key.</summary>
    [MaxLength(64)]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Ordering scope. Messages for one aggregate dispatch in the order they were written.</summary>
    [MaxLength(128)]
    public string AggregateId { get; set; } = string.Empty;

    /// <summary>Serialized destination, resolved back to a <see cref="Messaging.MessageDestination"/>.</summary>
    [MaxLength(512)]
    public string Destination { get; set; } = string.Empty;

    /// <summary>Envelope headers as JSON.</summary>
    public string Headers { get; set; } = "{}";

    /// <summary>Serialized payload.</summary>
    public byte[] Body { get; set; } = [];

    /// <summary>
    /// When the message was written, in UTC.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/> throughout this entity, and
    /// always UTC. SQLite cannot order or compare <c>DateTimeOffset</c> columns at all, so a
    /// portable outbox — one that works on the zero-config store as well as on PostgreSQL — cannot
    /// use it in any predicate. The offset would be redundant anyway: these are all instants.
    /// </remarks>
    public DateTime OccurredAt { get; set; }

    /// <summary>When the transport confirmed it, in UTC. Null while pending.</summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>Dispatch attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest next dispatch attempt, in UTC.</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>
    /// Last dispatch failure, truncated. Diagnostic only — never rendered to a caller, and never
    /// used to decide behaviour.
    /// </summary>
    [MaxLength(1024)]
    public string? LastError { get; set; }

    /// <summary>Identifies the relay that currently owns this row.</summary>
    [MaxLength(64)]
    public string? ClaimToken { get; set; }

    /// <summary>
    /// When the current claim expires. A relay that crashes mid-dispatch leaves a claim behind, and
    /// the lease is what lets another relay pick the row up instead of stranding it forever.
    /// </summary>
    /// <remarks>
    /// Non-nullable with <see cref="DateTimeOffset.MinValue"/> meaning "unclaimed". A nullable
    /// column would need <c>(ClaimedUntil == null || ClaimedUntil &lt; now)</c> in the claim
    /// predicate, which EF flattens into the wrong operator precedence on some providers and then
    /// fails to translate at all on others. A sentinel makes the predicate a single comparison.
    /// </remarks>
    public DateTime ClaimedUntil { get; set; }
}

/// <summary>Persists and dispatches outbox messages.</summary>
public interface IOutboxStore
{
    /// <summary>
    /// Enqueues a message. Must participate in the caller's transaction, so the row and the state
    /// change commit together or not at all.
    /// </summary>
    ValueTask EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="maxCount"/> pending messages for a lease period.
    /// </summary>
    /// <remarks>
    /// Claiming rather than merely reading is what lets several relay replicas run without a
    /// distributed lock: a row is owned by exactly one relay for the lease, and an expired lease
    /// returns it to the pool.
    /// </remarks>
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int maxCount, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Marks a message dispatched.</summary>
    Task MarkDispatchedAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt and schedules the next one.</summary>
    Task MarkFailedAsync(
        long id, string failureReason, DateTime nextAttemptAt, CancellationToken cancellationToken = default);

    /// <summary>Pending count and the age of the oldest pending message.</summary>
    Task<(int Pending, TimeSpan? OldestAge)> GetLagAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes dispatched messages older than the retention window.</summary>
    Task<int> PurgeDispatchedAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}

/// <summary>EF Core outbox store.</summary>
internal sealed class EfOutboxStore<TContext>(TContext context, TimeProvider clock) : IOutboxStore
    where TContext : DbContext
{
    public ValueTask EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Added to the tracked graph only. The caller's SaveChanges commits it alongside the state
        // change — writing it immediately would break the atomicity the outbox exists to provide.
        context.Set<OutboxMessage>().Add(message);
        return ValueTask.CompletedTask;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int maxCount, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid().ToString("N");

        // Selected first, then claimed with a conditional update. Two statements rather than one
        // provider-specific "SELECT ... FOR UPDATE SKIP LOCKED", because the conditional update is
        // portable and equally safe: only one relay's update matches a given row.
        var candidates = await context.Set<OutboxMessage>()
            .Where(m => m.DispatchedAt == null
                        && m.NextAttemptAt <= now
                        && m.ClaimedUntil < now)
            .OrderBy(m => m.AggregateId)
            .ThenBy(m => m.Id)
            .Take(maxCount)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return [];
        }

        var claimedUntil = now + leaseDuration;

        var claimed = await context.Set<OutboxMessage>()
            .Where(m => candidates.Contains(m.Id)
                        && m.DispatchedAt == null
                        && m.ClaimedUntil < now)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(m => m.ClaimToken, token)
                    .SetProperty(m => m.ClaimedUntil, claimedUntil),
                cancellationToken).ConfigureAwait(false);

        if (claimed == 0)
        {
            return [];
        }

        return await context.Set<OutboxMessage>()
            .Where(m => m.ClaimToken == token && m.DispatchedAt == null)
            .OrderBy(m => m.AggregateId)
            .ThenBy(m => m.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkDispatchedAsync(long id, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        await context.Set<OutboxMessage>()
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(m => m.DispatchedAt, now)
                    .SetProperty(m => m.ClaimToken, (string?)null)
                    .SetProperty(m => m.ClaimedUntil, DateTime.MinValue),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        long id, string failureReason, DateTime nextAttemptAt, CancellationToken cancellationToken = default)
    {
        // Truncated: a provider exception can be very long, and the column is a diagnostic aid
        // rather than a log.
        var truncated = failureReason.Length > 1024 ? failureReason[..1024] : failureReason;

        await context.Set<OutboxMessage>()
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(m => m.Attempts, m => m.Attempts + 1)
                    .SetProperty(m => m.LastError, truncated)
                    .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                    .SetProperty(m => m.ClaimToken, (string?)null)
                    .SetProperty(m => m.ClaimedUntil, DateTime.MinValue),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int Pending, TimeSpan? OldestAge)> GetLagAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await context.Set<OutboxMessage>()
            .Where(m => m.DispatchedAt == null)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        if (pending == 0)
        {
            return (0, null);
        }

        var oldest = await context.Set<OutboxMessage>()
            .Where(m => m.DispatchedAt == null)
            .MinAsync(m => m.OccurredAt, cancellationToken).ConfigureAwait(false);

        return (pending, clock.GetUtcNow().UtcDateTime - oldest);
    }

    public async Task<int> PurgeDispatchedAsync(
        TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - retention;

        return await context.Set<OutboxMessage>()
            .Where(m => m.DispatchedAt != null && m.DispatchedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A message this consumer group has already processed.</summary>
public sealed class InboxMessage
{
    /// <summary>Consumer group. The same message legitimately reaches several, and each handles it once.</summary>
    [MaxLength(256)]
    public string ConsumerGroup { get; set; } = string.Empty;

    /// <summary>Envelope id.</summary>
    [MaxLength(64)]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>When it was recorded, in UTC. See <see cref="OutboxMessage.OccurredAt"/> on why not DateTimeOffset.</summary>
    public DateTime ProcessedAt { get; set; }
}
