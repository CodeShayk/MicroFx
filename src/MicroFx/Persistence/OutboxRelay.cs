using System.Diagnostics.Metrics;
using System.Text.Json;
using MicroFx.Messaging;
using MicroFx.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MicroFx.Persistence;

/// <summary>
/// Dispatches outbox messages to the transport.
/// </summary>
/// <remarks>
/// <para>
/// The confirm precedes the "dispatched" update, so a crash between the two republishes rather than
/// loses. That is deliberate: duplicates are the consumer inbox's problem and it already solves
/// them, whereas a lost message has no recovery at all.
/// </para>
/// <para>
/// Several replicas may run concurrently. Rows are claimed under a lease rather than locked, so a
/// relay that dies mid-dispatch strands its rows only until the lease expires.
/// </para>
/// </remarks>
internal sealed partial class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    IMessageTransport transport,
    TimeProvider clock,
    IOptions<PersistenceOptions> options,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private readonly PersistenceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.OutboxPollInterval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Kept draining while a batch is full, so a backlog clears at dispatch speed
                // rather than at poll speed.
                while (await DispatchBatchAsync(stoppingToken).ConfigureAwait(false) ==
                       _options.OutboxBatchSize)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The relay must survive anything: a database blip or one poisonous row must not
                // stop every future message from being delivered.
                LogRelayFailed(logger, ex);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var batch = await store
            .ClaimPendingAsync(_options.OutboxBatchSize, _options.OutboxLeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in batch)
        {
            await DispatchAsync(store, message, cancellationToken).ConfigureAwait(false);
        }

        await ReportLagAsync(store, cancellationToken).ConfigureAwait(false);
        return batch.Count;
    }

    private async Task DispatchAsync(
        IOutboxStore store, OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryRehydrate(message, out var transportMessage, out var reason))
            {
                // A row that cannot be rehydrated will never dispatch, so retrying it forever would
                // block the aggregate's ordering behind a permanently broken message.
                LogUndispatchable(logger, message.Id, reason);
                await store.MarkDispatchedAsync(message.Id, cancellationToken).ConfigureAwait(false);
                return;
            }

            var receipt = await transport
                .PublishAsync(transportMessage, cancellationToken).ConfigureAwait(false);

            if (!receipt.Confirmed && !_options.AllowUnconfirmedOutboxDispatch)
            {
                // Not marking it dispatched is the entire point: an unconfirmed publish may not have
                // reached the broker, and the row is the only remaining record that it should.
                await FailAsync(store, message, "publish-not-confirmed", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await store.MarkDispatchedAsync(message.Id, cancellationToken).ConfigureAwait(false);
            OutboxMetrics.Dispatched();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDispatchFailed(logger, message.Id, ex);
            await FailAsync(store, message, ex.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FailAsync(
        IOutboxStore store, OutboxMessage message, string reason, CancellationToken cancellationToken)
    {
        var delay = _options.OutboxRetry.DelayFor(message.Attempts + 2);
        await store
            .MarkFailedAsync(message.Id, reason, clock.GetUtcNow().UtcDateTime + delay, cancellationToken)
            .ConfigureAwait(false);

        OutboxMetrics.DispatchFailed();
    }

    /// <summary>Rebuilds the transport message from the stored row.</summary>
    private static bool TryRehydrate(
        OutboxMessage message,
        out TransportMessage transportMessage,
        out string reason)
    {
        transportMessage = null!;
        reason = string.Empty;

        var destination = MessageDestinationCodec.Parse(message.Destination);
        if (destination is null)
        {
            reason = "unparsable-destination";
            return false;
        }

        Dictionary<string, string>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string>>(message.Headers);
        }
        catch (JsonException)
        {
            reason = "unparsable-headers";
            return false;
        }

        if (headers is null)
        {
            reason = "missing-headers";
            return false;
        }

        transportMessage = new TransportMessage(destination.Value, headers, message.Body);
        return true;
    }

    private async Task ReportLagAsync(IOutboxStore store, CancellationToken cancellationToken)
    {
        var (pending, oldestAge) = await store.GetLagAsync(cancellationToken).ConfigureAwait(false);
        OutboxMetrics.ReportLag(pending, oldestAge);

        // The leading indicator that events have stopped flowing, and the one worth paging on:
        // depth alone can be healthy under load, but age cannot.
        if (oldestAge > _options.OutboxLagAlertThreshold)
        {
            LogLagExceeded(logger, oldestAge!.Value.TotalSeconds, pending);
        }
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Error,
        Message = "The outbox relay failed; retrying on the next tick.")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Error,
        Message = "Outbox row {OutboxId} could not be dispatched and will be retried.")]
    private static partial void LogDispatchFailed(ILogger logger, long outboxId, Exception exception);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Error,
        Message = "Outbox row {OutboxId} is undispatchable ({Reason}); abandoning it.")]
    private static partial void LogUndispatchable(ILogger logger, long outboxId, string reason);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Warning,
        Message = "Outbox lag is {OldestAgeSeconds}s with {Pending} pending; events have stopped flowing.")]
    private static partial void LogLagExceeded(ILogger logger, double oldestAgeSeconds, int pending);
}

/// <summary>Serializes a destination for storage and parses it back.</summary>
/// <remarks>
/// A fixed four-part form rather than reflection or a type name, so a stored row can never resolve
/// to something the service did not declare.
/// </remarks>
internal static class MessageDestinationCodec
{
    public static string Format(MessageDestination destination) => destination.ToString();

    public static MessageDestination? Parse(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 4 ||
            !Enum.TryParse<DestinationKind>(parts[0], ignoreCase: true, out var kind))
        {
            return null;
        }

        return new MessageDestination(kind, parts[1], parts[2], parts[3]);
    }
}

/// <summary>Outbox metrics. Lag is the signal that matters.</summary>
internal static class OutboxMetrics
{
    public const string MeterName = "MicroFx.Outbox";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static int _pending;
    private static double _oldestAgeSeconds;

    private static readonly Counter<long> DispatchedCount = Meter.CreateCounter<long>(
        "outbox.dispatched.count", description: "Outbox messages confirmed by the transport.");

    private static readonly Counter<long> FailedCount = Meter.CreateCounter<long>(
        "outbox.dispatch.failed.count", description: "Outbox dispatch attempts that failed.");

    static OutboxMetrics()
    {
        Meter.CreateObservableGauge(
            "outbox.pending.count", () => Volatile.Read(ref _pending),
            description: "Outbox messages awaiting dispatch.");

        Meter.CreateObservableGauge(
            "outbox.oldest.age", () => Volatile.Read(ref _oldestAgeSeconds), unit: "s",
            description: "Age of the oldest undispatched outbox message.");
    }

    public static void Dispatched() => DispatchedCount.Add(1);

    public static void DispatchFailed() => FailedCount.Add(1);

    public static void ReportLag(int pending, TimeSpan? oldestAge)
    {
        Volatile.Write(ref _pending, pending);
        Volatile.Write(ref _oldestAgeSeconds, oldestAge?.TotalSeconds ?? 0);
    }
}
