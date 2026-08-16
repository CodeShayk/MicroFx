using System.Collections.Concurrent;

namespace MicroFx.Jobs;

/// <summary>A held distributed lock. Disposing releases it.</summary>
public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>The resource this lock covers.</summary>
    string Resource { get; }

    /// <summary>When the lease expires if it is not renewed.</summary>
    DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Extends the lease. Returns false if the lock was lost, which the caller must treat as a
    /// signal to stop — another holder may already be doing the same work.
    /// </summary>
    ValueTask<bool> RenewAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Acquires mutual exclusion across replicas.
/// </summary>
/// <remarks>
/// Every lease <b>must expire</b>. A lock without an expiry becomes permanent the moment its holder
/// is killed without unwinding, and the work it guards never runs again — which is a worse outcome
/// than the duplicate execution the lock was preventing.
/// </remarks>
public interface IDistributedLock
{
    /// <summary>Short name for diagnostics: <c>in-process</c>, <c>dynamodb</c>, <c>redis</c>.</summary>
    string Name { get; }

    /// <summary>Whether the lock is honoured across processes.</summary>
    bool IsDistributed { get; }

    /// <summary>Tries to acquire the lock, returning null if another holder has it.</summary>
    ValueTask<IDistributedLockHandle?> TryAcquireAsync(
        string resource, TimeSpan leaseDuration, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process lock for local development and single-replica deployments.
/// </summary>
/// <remarks>
/// <b>Not distributed.</b> With more than one replica, every replica acquires its own copy and
/// scheduled work runs once per replica. The jobs feature reports this at startup outside
/// Development, because the failure is silent: the job appears to work, just N times.
/// </remarks>
public sealed class InProcessDistributedLock(TimeProvider clock) : IDistributedLock
{
    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public string Name => "in-process";

    /// <inheritdoc />
    public bool IsDistributed => false;

    /// <inheritdoc />
    public ValueTask<IDistributedLockHandle?> TryAcquireAsync(
        string resource, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        var now = _clock.GetUtcNow();
        var token = Guid.NewGuid().ToString("N");
        var lease = new Lease(token, now + leaseDuration);

        // AddOrUpdate is the atomic primitive: the existing lease is replaced only if it has
        // expired, so a live holder is never displaced.
        var winner = _leases.AddOrUpdate(
            resource,
            lease,
            (_, existing) => existing.ExpiresAt <= now ? lease : existing);

        return ValueTask.FromResult<IDistributedLockHandle?>(
            ReferenceEquals(winner, lease)
                ? new Handle(this, resource, token, lease.ExpiresAt)
                : null);
    }

    private bool TryRenew(string resource, string token, DateTimeOffset expiresAt)
    {
        // Renewal is conditional on still owning the lease. A holder whose lease expired and was
        // taken by someone else must learn that rather than silently extending a lock it lost.
        while (_leases.TryGetValue(resource, out var existing))
        {
            if (!string.Equals(existing.Token, token, StringComparison.Ordinal))
            {
                return false;
            }

            if (_leases.TryUpdate(resource, new Lease(token, expiresAt), existing))
            {
                return true;
            }
        }

        return false;
    }

    private void Release(string resource, string token)
    {
        if (_leases.TryGetValue(resource, out var existing) &&
            string.Equals(existing.Token, token, StringComparison.Ordinal))
        {
            // Only the owner releases: a stale handle disposing must not free a lock someone else
            // has since acquired.
            _leases.TryRemove(new KeyValuePair<string, Lease>(resource, existing));
        }
    }

    private sealed record Lease(string Token, DateTimeOffset ExpiresAt);

    private sealed class Handle(
        InProcessDistributedLock owner, string resource, string token, DateTimeOffset expiresAt)
        : IDistributedLockHandle
    {
        private int _released;

        public string Resource { get; } = resource;

        public DateTimeOffset ExpiresAt { get; private set; } = expiresAt;

        public ValueTask<bool> RenewAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _released) == 1)
            {
                return ValueTask.FromResult(false);
            }

            // The owner's TimeProvider, not the ambient clock: a lease that renews against wall
            // time cannot be tested without waiting in real time, and lease expiry is precisely the
            // behaviour worth testing.
            var next = owner._clock.GetUtcNow() + duration;

            if (!owner.TryRenew(Resource, token, next))
            {
                return ValueTask.FromResult(false);
            }

            ExpiresAt = next;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(Resource, token);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Elects one replica to run singleton work.
/// </summary>
/// <remarks>
/// Built on <see cref="IDistributedLock"/> with continuous renewal: leadership is a lease that must
/// be kept alive, not a flag that is set once. A leader that stalls loses the lease and another
/// replica takes over, which is the property that makes the whole thing survive a hung process.
/// </remarks>
public interface ILeaderElector
{
    /// <summary>Whether this replica currently holds leadership for the resource.</summary>
    bool IsLeader(string resource);

    /// <summary>
    /// Runs work only while this replica is the leader, releasing leadership when it stops.
    /// </summary>
    Task RunAsLeaderAsync(
        string resource,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}
