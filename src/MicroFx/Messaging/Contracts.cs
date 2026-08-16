using System.Security.Claims;

namespace MicroFx.Messaging;

/// <summary>
/// A command: point-to-point, exactly one logical consumer, and the sender expects it to be done.
/// </summary>
/// <remarks>
/// Commands are owned by the <em>receiving</em> service, which publishes the contract. A sender
/// takes a dependency on it. The reverse — a sender defining commands for others to implement —
/// inverts ownership and couples the receiver to every caller.
/// </remarks>
public interface ICommand;

/// <summary>
/// An integration event: a fact that has already happened, published to zero or more independent
/// subscribers.
/// </summary>
/// <remarks>
/// The publisher must not know, enumerate, or depend on its subscribers. Events are named in the
/// past tense because they describe something that <em>is true</em>, not something to do.
/// </remarks>
public interface IIntegrationEvent;

/// <summary>
/// A domain event: raised and handled inside one service, never published to a transport.
/// </summary>
/// <remarks>
/// The distinction matters because a domain event is an internal implementation detail free to
/// change, while an integration event is a published contract subject to compatibility rules.
/// Publishing one to a transport is a defect the platform refuses at composition time.
/// </remarks>
public interface IDomainEvent;

/// <summary>What a handler decided about a message.</summary>
public enum HandlerOutcome
{
    /// <summary>Handled. Acknowledge and move on.</summary>
    Success,

    /// <summary>Failed for a reason that may pass — retry with backoff.</summary>
    Transient,

    /// <summary>Failed for a reason that will never pass — dead-letter without retrying.</summary>
    Permanent,

    /// <summary>Deliberately ignored. Acknowledge, count it, but do not treat it as an error.</summary>
    Discard,
}

/// <summary>
/// The result of handling a message.
/// </summary>
/// <remarks>
/// A return value rather than an exception, because "should this be retried?" is a <em>decision</em>,
/// and decisions read better where they are made than as an exception type caught three layers up.
/// Unhandled exceptions still work — they map to <see cref="HandlerOutcome.Transient"/> by default.
/// </remarks>
public readonly record struct HandlerResult
{
    private HandlerResult(HandlerOutcome outcome, string? reason, TimeSpan? retryAfter)
    {
        Outcome = outcome;
        Reason = reason;
        RetryAfter = retryAfter;
    }

    /// <summary>What was decided.</summary>
    public HandlerOutcome Outcome { get; }

    /// <summary>
    /// Short, stable reason token. Reaches logs, metrics tags, and the dead-letter record, so it must
    /// be low-cardinality and must never carry payload content or a credential.
    /// </summary>
    public string? Reason { get; }

    /// <summary>Requested delay before the next attempt. The retry policy caps this.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Whether the message was handled.</summary>
    public bool IsSuccess => Outcome is HandlerOutcome.Success or HandlerOutcome.Discard;

    /// <summary>Handled successfully.</summary>
    public static HandlerResult Success() => new(HandlerOutcome.Success, null, null);

    /// <summary>Failed transiently; retry with backoff.</summary>
    public static HandlerResult Transient(string reason, TimeSpan? retryAfter = null) =>
        new(HandlerOutcome.Transient, reason, retryAfter);

    /// <summary>Failed permanently; dead-letter without retrying.</summary>
    public static HandlerResult Permanent(string reason) => new(HandlerOutcome.Permanent, reason, null);

    /// <summary>Deliberately ignored.</summary>
    public static HandlerResult Discard(string reason) => new(HandlerOutcome.Discard, reason, null);
}

/// <summary>Everything a handler knows about the delivery it is processing.</summary>
/// <param name="MessageId">Envelope id. Stable across redeliveries; the dedupe key.</param>
/// <param name="CorrelationId">Correlates this message with the work that caused it.</param>
/// <param name="CausationId">Id of the message that caused this one.</param>
/// <param name="TenantId">Tenant in scope, when the envelope carried one.</param>
/// <param name="Principal">Caller identity, when the envelope carried a validated token.</param>
/// <param name="ConsumerGroup">The subscription processing this delivery.</param>
/// <param name="Attempt">1 on first delivery; incremented by the retry policy.</param>
/// <param name="EnqueuedAt">When the message was published.</param>
/// <param name="IsRedelivery">Whether the transport reports this as a redelivery.</param>
/// <param name="IsReplay">Whether the message was republished from an archive.</param>
/// <param name="Headers">Envelope headers, read-only.</param>
public sealed record MessageContext(
    string MessageId,
    string CorrelationId,
    string? CausationId,
    string? TenantId,
    ClaimsPrincipal? Principal,
    string ConsumerGroup,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    bool IsRedelivery,
    bool IsReplay,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>Handles a command.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
public interface IHandleCommand<in TCommand> where TCommand : ICommand
{
    /// <summary>Handles the command. Must be idempotent: delivery is at-least-once.</summary>
    Task<HandlerResult> HandleAsync(
        TCommand command, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>Subscribes to an integration event.</summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IHandleEvent<in TEvent> where TEvent : IIntegrationEvent
{
    /// <summary>Handles the event. Must be idempotent: delivery is at-least-once.</summary>
    Task<HandlerResult> HandleAsync(
        TEvent integrationEvent, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>Options for a single send.</summary>
public class SendOptions
{
    /// <summary>Overrides the correlation id, which otherwise flows from the ambient context.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Partition key for ordered subscriptions. Ignored where ordering is not requested.</summary>
    public string? PartitionKey { get; set; }

    /// <summary>Deliver no earlier than this. Realised natively or by the scheduled-message store.</summary>
    public DateTimeOffset? DeliverAt { get; set; }

    /// <summary>Discard rather than deliver after this instant.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Additional headers. Names and values are validated before transmission.</summary>
    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Options for a single publish.</summary>
public sealed class PublishOptions : SendOptions;

/// <summary>Sends commands to their owning service.</summary>
public interface ICommandSender
{
    /// <summary>
    /// Sends a command to its single logical consumer. The destination is resolved from the
    /// registered topology, never named by the caller.
    /// </summary>
    Task SendAsync<TCommand>(
        TCommand command, SendOptions? options = null, CancellationToken cancellationToken = default)
        where TCommand : ICommand;
}

/// <summary>Publishes integration events.</summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes an event to zero or more subscribers. Fire-and-forget with respect to who is
    /// listening — the publisher never learns, and must never depend on, its subscribers.
    /// </summary>
    Task PublishAsync<TEvent>(
        TEvent integrationEvent, PublishOptions? options = null, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent;
}
