using System.Collections.Concurrent;

namespace MicroFx.Messaging;

/// <summary>
/// Records which messages a consumer group has already processed, so at-least-once delivery
/// becomes effectively-once handling.
/// </summary>
/// <remarks>
/// Keyed by consumer group <em>and</em> message id, because the same message legitimately reaches
/// several consumer groups and each must process it once.
/// </remarks>
public interface IInboxStore
{
    /// <summary>
    /// Atomically records that this consumer group is processing this message.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this is the first time; <see langword="false"/> if already seen.
    /// </returns>
    /// <remarks>
    /// Must be atomic. A check-then-insert would let two concurrent redeliveries both observe
    /// "not seen" and both run the handler, which is exactly the duplicate this exists to prevent.
    /// </remarks>
    ValueTask<bool> TryBeginAsync(
        string consumerGroup, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Releases a reservation so a failed message can be retried.</summary>
    ValueTask ReleaseAsync(
        string consumerGroup, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Removes entries older than the retention window.</summary>
    ValueTask<int> PurgeAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory inbox for local development and test.
/// </summary>
/// <remarks>
/// <b>Not durable.</b> A restart forgets everything, so a message in flight across the restart is
/// processed twice. The messaging feature reports this at startup outside Development, because
/// "at-least-once with dedupe" quietly becomes "at-least-once" when the dedupe store is volatile.
/// </remarks>
public sealed class InMemoryInboxStore(TimeProvider clock) : IInboxStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);

    /// <summary>Number of retained entries.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public ValueTask<bool> TryBeginAsync(
        string consumerGroup, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        // TryAdd is the atomic primitive: exactly one caller observes true.
        return ValueTask.FromResult(_entries.TryAdd(Key(consumerGroup, messageId), clock.GetUtcNow()));
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(
        string consumerGroup, string messageId, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(Key(consumerGroup, messageId), out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - retention;
        var removed = 0;

        foreach (var (key, recordedAt) in _entries)
        {
            if (recordedAt < cutoff && _entries.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return ValueTask.FromResult(removed);
    }

    private static string Key(string consumerGroup, string messageId) =>
        consumerGroup + "" + messageId;
}
