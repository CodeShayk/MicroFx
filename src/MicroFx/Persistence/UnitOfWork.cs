using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MicroFx.Persistence;

/// <summary>A transaction scope. Disposing without committing rolls back.</summary>
public interface ITransactionScope : IAsyncDisposable
{
    /// <summary>
    /// Whether this scope joined an enclosing one. An ambient scope's commit is a no-op: the
    /// outermost scope decides the outcome for everyone.
    /// </summary>
    bool IsAmbient { get; }

    /// <summary>Commits, unless ambient.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back. On an ambient scope this marks the enclosing scope for rollback.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The transactional boundary for a service.
/// </summary>
/// <remarks>
/// Three subsystems must commit together — the aggregate's state change, the inbox record, and the
/// outbox rows — and none of them should have to know about the others. That is what this owns.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs work inside a transaction, retrying the whole unit if the provider's execution strategy
    /// says the failure was transient.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The primary API, and the only one that is retry-safe. EF Core refuses to combine a retrying
    /// execution strategy with a user-initiated transaction, because a retry cannot replay a
    /// transaction it did not start. Passing the work as a delegate is what lets the strategy own
    /// the whole unit and replay it correctly.
    /// </para>
    /// <para>
    /// The delegate may run more than once, so it must be free of side effects outside the
    /// transaction — no publishing, no file writes, no cache mutation.
    /// </para>
    /// </remarks>
    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken = default);

    /// <summary>Runs work inside a transaction with no result.</summary>
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a transaction scope, joining an enclosing one when present.
    /// </summary>
    /// <remarks>
    /// The lower-level form, for work that cannot be expressed as a delegate. It is <em>not</em>
    /// retry-safe: if the provider has a retrying execution strategy this throws with a message
    /// pointing at <see cref="ExecuteAsync{TResult}"/>, rather than letting EF fail later with a
    /// cryptic one or — worse — silently not retrying.
    /// </remarks>
    Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists changes, dispatching domain events and projecting integration events into the
    /// outbox inside the same transaction.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a transaction is currently in scope.</summary>
    bool HasActiveTransaction { get; }
}

/// <summary>Tracks the ambient scope for the current asynchronous flow.</summary>
/// <remarks>
/// <see cref="AsyncLocal{T}"/> rather than a scoped service, because the ambient transaction must
/// follow the logical call flow even across a service resolved from a different scope.
/// </remarks>
internal static class AmbientTransaction
{
    private static readonly AsyncLocal<TransactionState?> Current = new();

    public static TransactionState? State
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    internal sealed class TransactionState
    {
        public required IDbContextTransaction Transaction { get; init; }
        public bool RollbackOnly { get; set; }
        public int Depth { get; set; }
    }
}

/// <summary>EF Core unit of work.</summary>
internal sealed class EfUnitOfWork<TContext>(TContext context) : IUnitOfWork
    where TContext : DbContext
{
    public bool HasActiveTransaction => AmbientTransaction.State is not null;

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        // Already inside a transaction: join it rather than nesting a second one, so the outermost
        // caller still decides the outcome.
        if (AmbientTransaction.State is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(
            work,
            async (_, callback, token) =>
            {
                await using var transaction = await context.Database
                    .BeginTransactionAsync(token).ConfigureAwait(false);

                var state = new AmbientTransaction.TransactionState { Transaction = transaction };
                AmbientTransaction.State = state;

                try
                {
                    var result = await callback(token).ConfigureAwait(false);

                    if (state.RollbackOnly)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        throw new TransactionRolledBackException(
                            "The transaction was marked rollback-only by an inner scope.");
                    }

                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return result;
                }
                finally
                {
                    // Cleared before the retry loop can run again, so a replay starts with no
                    // stale ambient state from the failed attempt.
                    AmbientTransaction.State = null;
                }
            },
            verifySucceeded: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await ExecuteAsync<object?>(
            async token =>
            {
                await work(token).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        if (AmbientTransaction.State is { } ambient)
        {
            ambient.Depth++;
            return new AmbientScope(ambient);
        }

        // Caught here rather than left to EF, whose message names an internal strategy type and
        // does not tell the caller what to do instead.
        var strategy = context.Database.CreateExecutionStrategy();
        if (strategy.RetriesOnFailure)
        {
            throw new InvalidOperationException(
                $"A retrying execution strategy ({strategy.GetType().Name}) is configured, so a " +
                "user-initiated transaction cannot be retried safely. Use IUnitOfWork.ExecuteAsync, " +
                "which hands the whole unit of work to the strategy so it can replay it correctly.");
        }

        var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var state = new AmbientTransaction.TransactionState { Transaction = transaction };
        AmbientTransaction.State = state;

        return new RootScope(state);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    /// <summary>The outermost scope: it owns the transaction and decides the outcome.</summary>
    private sealed class RootScope(AmbientTransaction.TransactionState state) : ITransactionScope
    {
        private bool _settled;

        public bool IsAmbient => false;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_settled)
            {
                return;
            }

            _settled = true;

            if (state.RollbackOnly)
            {
                await state.Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new TransactionRolledBackException(
                    "The transaction was marked rollback-only by an inner scope.");
            }

            await state.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_settled)
            {
                return;
            }

            _settled = true;
            await state.Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            // Never committed: dispose must roll back rather than leave the transaction open, so a
            // forgotten commit fails closed.
            if (!_settled)
            {
                _settled = true;
                await state.Transaction.RollbackAsync().ConfigureAwait(false);
            }

            AmbientTransaction.State = null;
            await state.Transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>An inner scope that joined an enclosing transaction.</summary>
    /// <remarks>
    /// Commit is a no-op and rollback poisons the enclosing scope. Without this, a shared
    /// application service called from inside a handler would commit half the handler's work.
    /// </remarks>
    private sealed class AmbientScope(AmbientTransaction.TransactionState state) : ITransactionScope
    {
        public bool IsAmbient => true;

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            state.RollbackOnly = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            state.Depth--;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Thrown when a transaction was rolled back because an inner scope marked it so.</summary>
public sealed class TransactionRolledBackException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TransactionRolledBackException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public TransactionRolledBackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public TransactionRolledBackException()
    {
    }
}
