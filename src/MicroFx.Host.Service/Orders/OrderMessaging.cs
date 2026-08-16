using MicroFx.Messaging;

namespace MicroFx.Host.Service.Orders;

/// <summary>Published when an order is placed. A fact, in the past tense.</summary>
/// <param name="OrderId">The order.</param>
/// <param name="Sku">What was ordered.</param>
/// <param name="Quantity">How many.</param>
public sealed record OrderPlacedV1(string OrderId, string Sku, int Quantity) : IIntegrationEvent;

/// <summary>Asks inventory to reserve stock. Imperative, and owned by the receiving service.</summary>
/// <param name="OrderId">The order the reservation is for.</param>
/// <param name="Sku">What to reserve.</param>
/// <param name="Quantity">How much.</param>
public sealed record ReserveInventory(string OrderId, string Sku, int Quantity) : ICommand;

/// <summary>
/// Handles <see cref="ReserveInventory"/>.
/// </summary>
/// <remarks>
/// Demonstrates the explicit failure taxonomy: a business rejection is permanent and goes straight
/// to the dead letter, while an infrastructure blip is transient and earns the retry ladder.
/// </remarks>
public sealed partial class ReserveInventoryHandler(ILogger<ReserveInventoryHandler> logger)
    : IHandleCommand<ReserveInventory>
{
    /// <inheritdoc />
    public Task<HandlerResult> HandleAsync(
        ReserveInventory command, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        LogReserving(logger, command.OrderId, command.Sku, command.Quantity, context.Attempt);

        // Business rejection: no amount of waiting turns "we do not stock this" into success, so
        // retrying would only repeat the rejection four times before dead-lettering anyway.
        if (command.Quantity > 100)
        {
            return Task.FromResult(HandlerResult.Permanent("insufficient-stock"));
        }

        // Simulated infrastructure fault, which is exactly what the retry ladder is for.
        if (command.Sku.StartsWith("FLAKY", StringComparison.Ordinal) && context.Attempt < 3)
        {
            return Task.FromResult(HandlerResult.Transient("inventory-unavailable"));
        }

        return Task.FromResult(HandlerResult.Success());
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information,
        Message = "Reserving {Quantity} of {Sku} for order {OrderId} (attempt {Attempt}).")]
    private static partial void LogReserving(
        ILogger logger, string orderId, string sku, int quantity, int attempt);
}

/// <summary>
/// Projects <see cref="OrderPlacedV1"/>.
/// </summary>
/// <remarks>
/// The reference host subscribes to its own event, which round-trips the whole path — publish,
/// envelope, transport, pipeline, dedupe, handler — inside one process and with no infrastructure.
/// </remarks>
public sealed partial class OrderPlacedProjectionHandler(
    ILogger<OrderPlacedProjectionHandler> logger, OrderProjection projection)
    : IHandleEvent<OrderPlacedV1>
{
    /// <inheritdoc />
    public Task<HandlerResult> HandleAsync(
        OrderPlacedV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(context);

        projection.Record(integrationEvent.OrderId);
        LogProjected(logger, integrationEvent.OrderId, context.MessageId);

        return Task.FromResult(HandlerResult.Success());
    }

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information,
        Message = "Projected order {OrderId} from message {MessageId}.")]
    private static partial void LogProjected(ILogger logger, string orderId, string messageId);
}

/// <summary>
/// Records which orders the projection handler has seen.
/// </summary>
/// <remarks>
/// A singleton counter so the end-to-end suite can assert that a duplicate delivery ran the handler
/// exactly once — the observable proof that the inbox is doing its job.
/// </remarks>
public sealed class OrderProjection
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _handled =
        new(StringComparer.Ordinal);

    /// <summary>Records one handled event for an order.</summary>
    public void Record(string orderId) =>
        _handled.AddOrUpdate(orderId, 1, (_, count) => count + 1);

    /// <summary>How many times this order's event was handled.</summary>
    public int CountFor(string orderId) => _handled.GetValueOrDefault(orderId);

    /// <summary>Distinct orders seen.</summary>
    public int DistinctOrders => _handled.Count;
}
