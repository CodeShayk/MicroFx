using System.Collections.Concurrent;
using MicroFx.Messaging.Transport;

namespace MicroFx.Messaging;

/// <summary>A message held until its due time.</summary>
/// <param name="Id">Store-assigned id.</param>
/// <param name="Message">The message to deliver.</param>
/// <param name="DueAt">Earliest delivery time.</param>
public sealed record ScheduledMessage(string Id, TransportMessage Message, DateTimeOffset DueAt);

/// <summary>
/// Holds messages until they are due.
/// </summary>
/// <remarks>
/// The universal fallback for delayed delivery when a transport has no scheduler. Deliberately a
/// <em>store</em> drained by a job rather than an in-process timer: a sleeping handler holds its
/// delivery, occupies a prefetch slot, and stalls the consumer — with prefetch 10 and a 30-second
/// backoff, ten poison messages stop the subscription entirely.
/// </remarks>
public interface IScheduledMessageStore
{
    /// <summary>Schedules a message.</summary>
    ValueTask ScheduleAsync(
        TransportMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="maxCount"/> messages that are now due, removing them from the
    /// store so a second drainer cannot claim the same ones.
    /// </summary>
    ValueTask<IReadOnlyList<ScheduledMessage>> ClaimDueAsync(
        int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Messages currently held.</summary>
    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory scheduled-message store for local development and test.
/// </summary>
/// <remarks>
/// <b>Not durable.</b> A restart loses every scheduled message, so a retry in flight across the
/// restart never happens. Reported at startup outside Development.
/// </remarks>
public sealed class InMemoryScheduledMessageStore(TimeProvider clock) : IScheduledMessageStore
{
    private readonly ConcurrentDictionary<string, ScheduledMessage> _messages = new(StringComparer.Ordinal);

    /// <summary>
    /// Cap on held messages. Bounded because a persistently-failing consumer would otherwise turn a
    /// retry policy into unbounded memory growth.
    /// </summary>
    public int Capacity { get; init; } = 10_000;

    /// <inheritdoc />
    public ValueTask ScheduleAsync(
        TransportMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_messages.Count >= Capacity)
        {
            throw new InvalidOperationException(
                $"The scheduled-message store is full ({Capacity}). A consumer is failing faster " +
                "than its retries drain.");
        }

        var id = Guid.NewGuid().ToString("N");
        _messages[id] = new ScheduledMessage(id, message, dueAt);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ScheduledMessage>> ClaimDueAsync(
        int maxCount, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var claimed = new List<ScheduledMessage>();

        foreach (var (id, scheduled) in _messages)
        {
            if (claimed.Count >= maxCount)
            {
                break;
            }

            // Removal is the claim: whoever wins TryRemove owns the message, so two drainers
            // never dispatch the same one.
            if (scheduled.DueAt <= now && _messages.TryRemove(id, out var owned))
            {
                claimed.Add(owned);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<ScheduledMessage>>(claimed);
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_messages.Count);
}
