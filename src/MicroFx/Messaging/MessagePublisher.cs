using System.Diagnostics;
using System.Text.Json;
using MicroFx.Core;
using MicroFx.Messaging.Transport;
using MicroFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MicroFx.Messaging;

/// <summary>Maps a message type to the destination it belongs to.</summary>
public sealed class DestinationRegistry
{
    private readonly Dictionary<Type, MessageDestination> _destinations = [];

    internal void Register(Type messageType, MessageDestination destination) =>
        _destinations[messageType] = destination;

    /// <summary>Returns the destination for a message type, or null when unregistered.</summary>
    public MessageDestination? Resolve(Type messageType) =>
        _destinations.TryGetValue(messageType, out var destination) ? destination : null;

    /// <summary>Returns the destination, or throws with an actionable message.</summary>
    public MessageDestination Require(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        return Resolve(messageType) ?? throw new InvalidOperationException(
            $"No destination is registered for '{messageType.Name}'. Declare it during composition " +
            "with PublishesEvent or SendsCommand — a caller never names a destination directly.");
    }

    /// <summary>Every registered destination.</summary>
    public IReadOnlyList<MessageDestination> All => [.. _destinations.Values.Distinct()];
}

/// <summary>
/// Sends commands and publishes events over the configured transport.
/// </summary>
/// <remarks>
/// The single place where a message becomes bytes: envelope construction, tenancy propagation,
/// trace-context injection, serialization, and header validation all happen here, so no caller can
/// produce a message that skips any of them.
/// </remarks>
internal sealed class MessagePublisher(
    IMessageTransport transport,
    DestinationRegistry destinations,
    MessageTypeRegistry types,
    ServiceMetadata metadata,
    TimeProvider clock,
    IServiceProvider services,
    IOptions<MessagingOptions> options) : ICommandSender, IEventPublisher
{
    private readonly MessagingOptions _options = options.Value;

    public Task SendAsync<TCommand>(
        TCommand command, SendOptions? sendOptions = null, CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        return DispatchAsync(command, MessageKind.Command, sendOptions, cancellationToken);
    }

    public Task PublishAsync<TEvent>(
        TEvent integrationEvent,
        PublishOptions? publishOptions = null,
        CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return DispatchAsync(integrationEvent, MessageKind.Event, publishOptions, cancellationToken);
    }

    private async Task DispatchAsync(
        object message, MessageKind kind, SendOptions? sendOptions, CancellationToken cancellationToken)
    {
        var messageType = message.GetType();
        var destination = destinations.Require(messageType);
        var wireName = types.RequireWireName(messageType);

        using var activity = MessagingDiagnostics.StartPublish(destination, wireName);

        var envelope = BuildEnvelope(wireName, kind, sendOptions);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType, _options.SerializerOptions);

        if (body.Length > _options.MaxMessageBytes)
        {
            // Refused rather than truncated or silently offloaded: an oversized message usually
            // means a payload that should have been a reference in the first place.
            throw new InvalidOperationException(
                $"Message '{wireName}' is {body.Length} bytes, over the " +
                $"{_options.MaxMessageBytes}-byte limit. Send a reference rather than the payload.");
        }

        var transportMessage = new TransportMessage(
            destination, EnvelopeCodec.Encode(envelope), body, Persistent: true);

        var receipt = sendOptions?.DeliverAt is { } dueAt && dueAt > clock.GetUtcNow()
            ? await ScheduleAsync(transportMessage, dueAt, cancellationToken).ConfigureAwait(false)
            : await transport.PublishAsync(transportMessage, cancellationToken).ConfigureAwait(false);

        MessagingDiagnostics.Published(destination.ToString(), wireName, receipt.Confirmed);

        if (!receipt.Confirmed && !_options.AllowUnconfirmedPublish)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "unconfirmed");
            throw new MessagePublishException(
                $"Publish of '{wireName}' to {destination} was not confirmed by the transport.");
        }
    }

    private async Task<PublishReceipt> ScheduleAsync(
        TransportMessage message, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        // Native scheduling where the transport has it; otherwise the platform's store. Never an
        // in-process delay, which would tie the send to the lifetime of the calling request.
        if (transport is ITransportScheduler scheduler)
        {
            await scheduler.ScheduleAsync(message, dueAt, cancellationToken).ConfigureAwait(false);
            return new PublishReceipt(true);
        }

        var store = services.GetService<IScheduledMessageStore>()
            ?? throw new InvalidOperationException(
                "Delayed delivery was requested, the transport has no scheduler, and no " +
                "scheduled-message store is registered.");

        await store.ScheduleAsync(message, dueAt, cancellationToken).ConfigureAwait(false);
        return new PublishReceipt(true);
    }

    private Envelope BuildEnvelope(string wireName, MessageKind kind, SendOptions? sendOptions)
    {
        var activity = Activity.Current;

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sendOptions is not null)
        {
            foreach (var (name, value) in sendOptions.Headers)
            {
                // Validated rather than trusted: a caller must not be able to inject a platform
                // header and forge a tenant, an attempt count, or an authorization token.
                if (EnvelopeCodec.IsValidCustomHeader(name, value))
                {
                    headers[name] = value;
                }
            }
        }

        return new Envelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = wireName,
            Source = metadata.Name,
            Time = clock.GetUtcNow(),

            // Set by the platform, never by the caller. A caller-chosen kind would let a command be
            // published as an event and bypass the receiving side's kind check.
            Kind = kind,

            CorrelationId = sendOptions?.CorrelationId
                            ?? activity?.TraceId.ToString()
                            ?? Guid.NewGuid().ToString("N"),
            CausationId = activity?.SpanId.ToString(),

            // Read from the ambient tenant rather than accepted as a parameter, so a caller cannot
            // publish into another tenant's scope.
            TenantId = services.GetService<ITenantContext>()?.Current,

            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            PartitionKey = sendOptions?.PartitionKey,
            ExpiresAt = sendOptions?.ExpiresAt,
            Headers = headers,
        };
    }
}

/// <summary>Thrown when a publish could not be confirmed by the transport.</summary>
public sealed class MessagePublishException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MessagePublishException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MessagePublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public MessagePublishException()
    {
    }
}
