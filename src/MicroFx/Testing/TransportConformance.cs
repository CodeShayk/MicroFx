using System.Text;
using MicroFx.Messaging;
using MicroFx.Messaging.Transport;

namespace MicroFx.Testing;

/// <summary>The outcome of one conformance check.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="Capability">The capability it exercises, if any.</param>
/// <param name="Passed">Whether the transport behaved as advertised.</param>
/// <param name="Detail">What happened.</param>
/// <param name="Skipped">Whether the capability was not advertised, so the check did not apply.</param>
public readonly record struct ConformanceResult(
    string Name, TransportCapabilities Capability, bool Passed, string Detail, bool Skipped = false);

/// <summary>The full conformance report for one transport.</summary>
public sealed class ConformanceReport(string transportName, IReadOnlyList<ConformanceResult> results)
{
    /// <summary>The transport under test.</summary>
    public string TransportName { get; } = transportName;

    /// <summary>Every check, in the order it ran.</summary>
    public IReadOnlyList<ConformanceResult> Results { get; } = results;

    /// <summary>Whether every applicable check passed.</summary>
    public bool Passed => Results.All(r => r.Passed || r.Skipped);

    /// <summary>Checks that failed.</summary>
    public IEnumerable<ConformanceResult> Failures => Results.Where(r => !r.Passed && !r.Skipped);

    /// <summary>A human-readable summary, suitable for a test failure message.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append("Transport conformance for '").Append(TransportName).AppendLine("':");

        foreach (var result in Results)
        {
            builder
                .Append("  ")
                .Append(result.Skipped ? '-' : result.Passed ? '+' : 'x')
                .Append(' ')
                .Append(result.Name)
                .Append(" — ")
                .AppendLine(result.Detail);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Verifies that a transport actually does what its capability flags claim.
/// </summary>
/// <remarks>
/// <para>
/// The flags drive real decisions: the core refuses to start when a correctness guarantee is
/// unavailable, and emulates a convenience when a convenience is. A transport that advertises
/// <see cref="TransportCapabilities.PublisherConfirms"/> without honouring it would make the outbox
/// mark rows dispatched that never arrived — and nothing would notice until messages went missing.
/// </para>
/// <para>
/// So "advertises confirms" is treated as a claim to be tested rather than a fact to be trusted.
/// Every adapter runs this suite; the in-memory transport runs it too, which is what keeps the
/// suite itself honest.
/// </para>
/// </remarks>
public sealed class TransportConformanceSuite(IMessageTransport transport, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>How long a check waits for a delivery before deciding it will not arrive.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Runs every applicable check.</summary>
    public async Task<ConformanceReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ConformanceResult>
        {
            await RoundTripAsync(cancellationToken).ConfigureAwait(false),
            await FanOutAsync(cancellationToken).ConfigureAwait(false),
            await ConfirmsAsync(cancellationToken).ConfigureAwait(false),
            await AcknowledgementAsync(cancellationToken).ConfigureAwait(false),
            await DeadLetterAsync(cancellationToken).ConfigureAwait(false),
            await DelayedDeliveryAsync(cancellationToken).ConfigureAwait(false),
            await HeaderFidelityAsync(cancellationToken).ConfigureAwait(false),
            await CancellationAsync(cancellationToken).ConfigureAwait(false),
        };

        return new ConformanceReport(transport.Name, results);
    }

    // ---- Checks ---------------------------------------------------------------------------------

    private async Task<ConformanceResult> RoundTripAsync(CancellationToken cancellationToken)
    {
        var destination = NewDestination("roundtrip");
        var received = new TaskCompletionSource<TransportDelivery>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await SubscribeAsync(
            destination, "conformance.roundtrip",
            delivery =>
            {
                received.TrySetResult(delivery);
                return new DeliveryOutcome(DeliveryDisposition.Complete);
            },
            cancellationToken).ConfigureAwait(false);

        await transport.PublishAsync(Message(destination, "payload"), cancellationToken)
            .ConfigureAwait(false);

        var (arrived, _) = await WaitAsync(received.Task).ConfigureAwait(false);

        return !arrived
            ? Fail("round trip", TransportCapabilities.None, "A published message was never delivered.")
            : Pass("round trip", TransportCapabilities.None, "A published message reached its subscriber.");
    }

    private async Task<ConformanceResult> FanOutAsync(CancellationToken cancellationToken)
    {
        // The property that makes publish/subscribe independent: each consumer group gets its own
        // copy. If they share, subscribers become competing consumers and each sees only some.
        var destination = NewDestination("fanout");
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var one = await SubscribeAsync(
            destination, "conformance.fanout.one",
            _ => { first.TrySetResult(true); return new DeliveryOutcome(DeliveryDisposition.Complete); },
            cancellationToken).ConfigureAwait(false);

        await using var two = await SubscribeAsync(
            destination, "conformance.fanout.two",
            _ => { second.TrySetResult(true); return new DeliveryOutcome(DeliveryDisposition.Complete); },
            cancellationToken).ConfigureAwait(false);

        await transport.PublishAsync(Message(destination, "fanout"), cancellationToken)
            .ConfigureAwait(false);

        var (both, _) = await WaitAsync(Task.WhenAll(first.Task, second.Task)).ConfigureAwait(false);

        return !both
            ? Fail("consumer-group fan-out", TransportCapabilities.None,
                "Only one consumer group received the message; the other would silently miss events.")
            : Pass("consumer-group fan-out", TransportCapabilities.None,
                "Both consumer groups received an independent copy.");
    }

    private async Task<ConformanceResult> ConfirmsAsync(CancellationToken cancellationToken)
    {
        const TransportCapabilities Capability = TransportCapabilities.PublisherConfirms;

        if (!transport.Capabilities.HasFlag(Capability))
        {
            return Skip("publisher confirms", Capability);
        }

        var destination = NewDestination("confirms");

        await using var subscription = await SubscribeAsync(
            destination, "conformance.confirms",
            _ => new DeliveryOutcome(DeliveryDisposition.Complete),
            cancellationToken).ConfigureAwait(false);

        var receipt = await transport
            .PublishAsync(Message(destination, "confirmed"), cancellationToken).ConfigureAwait(false);

        return receipt.Confirmed
            ? Pass("publisher confirms", Capability, "The transport reported a confirmed publish.")
            : Fail("publisher confirms", Capability,
                "The transport advertises confirms but reported an unconfirmed publish. The outbox " +
                "would mark rows dispatched that may never have arrived.");
    }

    private async Task<ConformanceResult> AcknowledgementAsync(CancellationToken cancellationToken)
    {
        const TransportCapabilities Capability = TransportCapabilities.ManualAcknowledgement;

        if (!transport.Capabilities.HasFlag(Capability))
        {
            return Skip("manual acknowledgement", Capability);
        }

        // Abandoned once, then completed. Without real acknowledgement the abandon is lost and the
        // message never comes back — which is at-most-once wearing an at-least-once label.
        var destination = NewDestination("ack");
        var deliveries = 0;
        var redelivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await SubscribeAsync(
            destination, "conformance.ack",
            _ =>
            {
                if (Interlocked.Increment(ref deliveries) == 1)
                {
                    return new DeliveryOutcome(DeliveryDisposition.Abandon);
                }

                redelivered.TrySetResult(true);
                return new DeliveryOutcome(DeliveryDisposition.Complete);
            },
            cancellationToken).ConfigureAwait(false);

        await transport.PublishAsync(Message(destination, "ack"), cancellationToken).ConfigureAwait(false);

        var (redeliveredInTime, _) = await WaitAsync(redelivered.Task).ConfigureAwait(false);

        return !redeliveredInTime
            ? Fail("manual acknowledgement", Capability,
                "An abandoned delivery was never redelivered, so a crash mid-handling would lose it.")
            : Pass("manual acknowledgement", Capability, "An abandoned delivery was redelivered.");
    }

    private async Task<ConformanceResult> DeadLetterAsync(CancellationToken cancellationToken)
    {
        // Applies whether the broker dead-letters natively or the core emulates it. What matters is
        // that the message leaves the work queue and does not loop.
        var destination = NewDestination("deadletter");
        var deliveries = 0;

        await using var subscription = await SubscribeAsync(
            destination, "conformance.deadletter",
            _ =>
            {
                Interlocked.Increment(ref deliveries);
                return new DeliveryOutcome(DeliveryDisposition.DeadLetter, Reason: "conformance");
            },
            cancellationToken).ConfigureAwait(false);

        await transport.PublishAsync(Message(destination, "poison"), cancellationToken)
            .ConfigureAwait(false);

        await Task.Delay(TimeSpan.FromMilliseconds(500), _clock, cancellationToken).ConfigureAwait(false);

        return Volatile.Read(ref deliveries) <= 1
            ? Pass("dead-lettering", TransportCapabilities.NativeDeadLetter,
                "A dead-lettered message left the work queue and did not loop.")
            : Fail("dead-lettering", TransportCapabilities.NativeDeadLetter,
                $"A dead-lettered message was redelivered {deliveries} times; it is looping rather " +
                "than leaving the queue.");
    }

    private async Task<ConformanceResult> DelayedDeliveryAsync(CancellationToken cancellationToken)
    {
        const TransportCapabilities Capability = TransportCapabilities.NativeDelayedDelivery;

        if (transport is not ITransportScheduler scheduler ||
            !transport.Capabilities.HasFlag(Capability))
        {
            return Skip("delayed delivery", Capability);
        }

        var destination = NewDestination("delayed");
        var received = new TaskCompletionSource<DateTimeOffset>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await SubscribeAsync(
            destination, "conformance.delayed",
            _ =>
            {
                received.TrySetResult(_clock.GetUtcNow());
                return new DeliveryOutcome(DeliveryDisposition.Complete);
            },
            cancellationToken).ConfigureAwait(false);

        var delay = TimeSpan.FromMilliseconds(500);
        var scheduledAt = _clock.GetUtcNow();

        await scheduler.ScheduleAsync(
            Message(destination, "delayed"), scheduledAt + delay, cancellationToken)
            .ConfigureAwait(false);

        var (delivered, deliveredAt) = await WaitAsync(received.Task).ConfigureAwait(false);

        if (!delivered)
        {
            return Fail("delayed delivery", Capability, "A scheduled message was never delivered.");
        }

        // Early delivery defeats the backoff that exists to let a struggling dependency recover, so
        // a scheduler that fires immediately is not conformant even though the message arrives.
        var elapsed = deliveredAt - scheduledAt;

        return elapsed >= delay * 0.8
            ? Pass("delayed delivery", Capability, $"Delivered after {elapsed.TotalMilliseconds:0}ms.")
            : Fail("delayed delivery", Capability,
                $"Delivered after only {elapsed.TotalMilliseconds:0}ms of a requested " +
                $"{delay.TotalMilliseconds:0}ms; retry backoff would be defeated.");
    }

    private async Task<ConformanceResult> HeaderFidelityAsync(CancellationToken cancellationToken)
    {
        // The envelope lives entirely in headers, so a transport that drops, truncates, or mangles
        // one silently breaks tracing, tenancy, dedupe, and the retry counter at once.
        var destination = NewDestination("headers");
        var received = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await SubscribeAsync(
            destination, "conformance.headers",
            delivery =>
            {
                received.TrySetResult(delivery.Headers);
                return new DeliveryOutcome(DeliveryDisposition.Complete);
            },
            cancellationToken).ConfigureAwait(false);

        var sent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["microfx-id"] = "conformance-id",
            ["microfx-attempt"] = "3",
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["x-custom"] = "value with spaces",
        };

        await transport.PublishAsync(
            new TransportMessage(destination, sent, Encoding.UTF8.GetBytes("body")), cancellationToken)
            .ConfigureAwait(false);

        var (headersArrived, headers) = await WaitAsync(received.Task).ConfigureAwait(false);

        if (!headersArrived)
        {
            return Fail("header fidelity", TransportCapabilities.None, "The message was never delivered.");
        }

        var missing = sent.Where(pair =>
            !headers.TryGetValue(pair.Key, out var value) ||
            !string.Equals(value, pair.Value, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();

        return missing.Count == 0
            ? Pass("header fidelity", TransportCapabilities.None, "Every header survived the round trip.")
            : Fail("header fidelity", TransportCapabilities.None,
                $"Headers were lost or altered: {string.Join(", ", missing)}. Tracing, tenancy, " +
                "deduplication, and the retry counter all travel this way.");
    }

    private async Task<ConformanceResult> CancellationAsync(CancellationToken cancellationToken)
    {
        const TransportCapabilities Capability = TransportCapabilities.ConsumerCancellation;

        if (!transport.Capabilities.HasFlag(Capability))
        {
            return Skip("consumer cancellation", Capability);
        }

        var destination = NewDestination("cancel");
        var afterCancel = 0;

        var subscription = await SubscribeAsync(
            destination, "conformance.cancel",
            _ =>
            {
                Interlocked.Increment(ref afterCancel);
                return new DeliveryOutcome(DeliveryDisposition.Complete);
            },
            cancellationToken).ConfigureAwait(false);

        await subscription.CancelAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref afterCancel, 0);

        await transport.PublishAsync(Message(destination, "after-cancel"), cancellationToken)
            .ConfigureAwait(false);

        await Task.Delay(TimeSpan.FromMilliseconds(500), _clock, cancellationToken).ConfigureAwait(false);
        await subscription.DisposeAsync().ConfigureAwait(false);

        return Volatile.Read(ref afterCancel) == 0
            ? Pass("consumer cancellation", Capability,
                "No delivery arrived after cancellation, so a drain can finish cleanly.")
            : Fail("consumer cancellation", Capability,
                "Deliveries continued after cancellation, so a drain would never complete.");
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private Task<ITransportSubscription> SubscribeAsync(
        MessageDestination destination,
        string consumerGroup,
        Func<TransportDelivery, DeliveryOutcome> handler,
        CancellationToken cancellationToken) =>
        transport.SubscribeAsync(
            new SubscriptionSpec { ConsumerGroup = consumerGroup, Source = destination },
            (delivery, _) => Task.FromResult(handler(delivery)),
            cancellationToken);

    private static MessageDestination NewDestination(string name) =>
        new(DestinationKind.Event, "conformance", $"{name}.{Guid.NewGuid():N}"[..24]);

    private static TransportMessage Message(MessageDestination destination, string body) =>
        new(destination,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["microfx-id"] = Guid.NewGuid().ToString("N") },
            Encoding.UTF8.GetBytes(body));

    /// <summary>Waits for a result, returning the value and whether it arrived in time.</summary>
    /// <remarks>
    /// Returns a tuple rather than <c>default(T)</c>, because for a value type the default is
    /// indistinguishable from a legitimate result — a timed-out <c>bool</c> wait would read as a
    /// perfectly good <c>false</c>.
    /// </remarks>
    private async Task<(bool Completed, T Value)> WaitAsync<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false);

        return completed == task
            ? (true, await task.ConfigureAwait(false))
            : (false, default!);
    }

    private async Task<bool> WaitAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Timeout)).ConfigureAwait(false);
        return completed == task;
    }

    private static ConformanceResult Pass(string name, TransportCapabilities capability, string detail) =>
        new(name, capability, Passed: true, detail);

    private static ConformanceResult Fail(string name, TransportCapabilities capability, string detail) =>
        new(name, capability, Passed: false, detail);

    private static ConformanceResult Skip(string name, TransportCapabilities capability) =>
        new(name, capability, Passed: true,
            "Not advertised by this transport, so the check does not apply.", Skipped: true);
}
