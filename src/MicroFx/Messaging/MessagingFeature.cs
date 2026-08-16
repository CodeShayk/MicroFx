using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MicroFx.Core;
using MicroFx.Features;
using MicroFx.Messaging.Pipeline;
using MicroFx.Messaging.Transport;
using MicroFx.Messaging.Transport.InMemory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MicroFx.Messaging;

/// <summary>Options for the messaging feature, bound from <c>MicroFx:Messaging</c>.</summary>
public sealed class MessagingOptions
{
    /// <summary>This service's name as it appears in destination ownership.</summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Whether a publish the transport did not confirm counts as success.
    /// </summary>
    /// <remarks>
    /// Off by default and reported as a degradation when on, because an unconfirmed publish means
    /// "possibly lost" while the calling code has already moved on. Turning it on should carry an
    /// ADR reference.
    /// </remarks>
    public bool AllowUnconfirmedPublish { get; set; }

    /// <summary>
    /// Whether the in-memory transport may be used outside Development.
    /// </summary>
    /// <remarks>
    /// Messages exist only inside one process, so a restart loses everything in flight. This is a
    /// startup error rather than a warning: it is silent data loss, not a degraded mode.
    /// </remarks>
    public bool AllowInMemoryTransportInProduction { get; set; }

    /// <summary>How long the inbox remembers a processed message id.</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Maximum serialized message size.</summary>
    [Range(1024, 32 * 1024 * 1024)]
    public long MaxMessageBytes { get; set; } = 256 * 1024;

    /// <summary>How often the scheduled-message store is drained.</summary>
    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Serializer options for message payloads.</summary>
    public JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>Custom pipeline middleware, run after the platform's checks.</summary>
    public IList<IMessageMiddleware> Middleware { get; } = [];

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        CoreFeature.ApplyJsonConventions(options);

        // Hostile input arrives from a shared broker, so the payload shape is bounded independently
        // of the HTTP stack's settings.
        options.MaxDepth = 24;
        return options;
    }
}

/// <summary>Declares what a service publishes, handles, and subscribes to.</summary>
public sealed class MessagingBuilder
{
    internal MessagingBuilder(string serviceName) => ServiceName = serviceName;

    internal string ServiceName { get; }

    internal Type? TransportType { get; private set; }

    internal IMessageTransport? TransportInstance { get; private set; }

    internal List<(Type MessageType, MessageDestination Destination, MessageKind Kind)> Published { get; } = [];

    internal List<(Type CommandType, Type HandlerType, SubscriptionSpec Spec)> CommandHandlers { get; } = [];

    internal List<(Type EventType, Type HandlerType, SubscriptionSpec Spec)> EventSubscriptions { get; } = [];

    /// <summary>Uses a transport registered in the service collection.</summary>
    /// <typeparam name="TTransport">The transport type.</typeparam>
    public MessagingBuilder UseTransport<TTransport>() where TTransport : class, IMessageTransport
    {
        TransportType = typeof(TTransport);
        return this;
    }

    /// <summary>Uses a pre-constructed transport. Intended for tests.</summary>
    public MessagingBuilder UseTransport(IMessageTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        TransportInstance = transport;
        return this;
    }

    /// <summary>Declares an event this service publishes.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    public MessagingBuilder PublishesEvent<TEvent>(string? name = null) where TEvent : IIntegrationEvent
    {
        Published.Add((
            typeof(TEvent),
            new MessageDestination(DestinationKind.Event, ServiceName, name ?? Derive<TEvent>()),
            MessageKind.Event));

        return this;
    }

    /// <summary>Declares a command this service sends to another service.</summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <param name="owner">The service that owns and handles the command.</param>
    /// <param name="name">Logical command name, derived from the type when omitted.</param>
    public MessagingBuilder SendsCommand<TCommand>(string owner, string? name = null)
        where TCommand : ICommand
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        Published.Add((
            typeof(TCommand),
            new MessageDestination(DestinationKind.Command, owner, name ?? Derive<TCommand>()),
            MessageKind.Command));

        return this;
    }

    /// <summary>Declares a command this service owns and handles.</summary>
    /// <typeparam name="TCommand">The command type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    public MessagingBuilder HandlesCommand<TCommand, THandler>(
        Action<SubscriptionBuilder>? configure = null)
        where TCommand : ICommand
        where THandler : class, IHandleCommand<TCommand>
    {
        var destination = new MessageDestination(
            DestinationKind.Command, ServiceName, Derive<TCommand>());

        // Exactly one consumer group, named for the destination itself: a command has one logical
        // consumer by definition, so there is nothing for a caller to choose here.
        var builder = new SubscriptionBuilder($"{ServiceName}.{destination.Name}", destination);
        configure?.Invoke(builder);

        Published.Add((typeof(TCommand), destination, MessageKind.Command));
        CommandHandlers.Add((typeof(TCommand), typeof(THandler), builder.Build()));
        return this;
    }

    /// <summary>Declares a subscription to another service's event.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="owner">The publishing service. Defaults to this service, for self-subscription.</param>
    /// <param name="configure">Adjusts concurrency, retry, ordering, and filtering.</param>
    public MessagingBuilder SubscribesToEvent<TEvent, THandler>(
        string? owner = null, Action<SubscriptionBuilder>? configure = null)
        where TEvent : IIntegrationEvent
        where THandler : class, IHandleEvent<TEvent>
    {
        var destination = new MessageDestination(
            DestinationKind.Event, owner ?? ServiceName, Derive<TEvent>());

        // The consumer group carries this service's name, so each subscriber gets its own backlog,
        // retry, and dead-letter. Two services sharing one group would turn pub/sub into competing
        // consumers and each would see only some of the events.
        var builder = new SubscriptionBuilder(
            $"{ServiceName}.{destination.Owner}.{destination.Name}", destination);

        configure?.Invoke(builder);

        EventSubscriptions.Add((typeof(TEvent), typeof(THandler), builder.Build()));
        return this;
    }

    private static string Derive<T>()
    {
        var builder = new MessageTypeRegistryBuilder();
        return builder.Register(typeof(T));
    }
}

/// <summary>Adjusts one subscription.</summary>
public sealed class SubscriptionBuilder
{
    private readonly string _consumerGroup;
    private readonly MessageDestination _source;
    private int _concurrency = 1;
    private int _prefetch = 10;
    private RetryPolicy _retry = RetryPolicy.Default;
    private DeadLetterPolicy _deadLetter = DeadLetterPolicy.Default;
    private OrderingScope _ordering = OrderingScope.None;
    private DeliveryGuarantee _guarantee = DeliveryGuarantee.AtLeastOnce;
    private string? _filter;
    private TimeSpan _handlerTimeout = TimeSpan.FromSeconds(30);

    internal SubscriptionBuilder(string consumerGroup, MessageDestination source)
    {
        _consumerGroup = consumerGroup;
        _source = source;
    }

    /// <summary>Sets concurrent handler invocations.</summary>
    public SubscriptionBuilder WithConcurrency(int concurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        _concurrency = concurrency;
        return this;
    }

    /// <summary>Sets how many unacknowledged messages the transport may hold.</summary>
    public SubscriptionBuilder WithPrefetch(int prefetch)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(prefetch, 1);
        _prefetch = prefetch;
        return this;
    }

    /// <summary>Sets the retry policy.</summary>
    public SubscriptionBuilder WithRetry(RetryPolicy retry)
    {
        _retry = retry ?? RetryPolicy.Default;
        return this;
    }

    /// <summary>Sets the dead-letter policy.</summary>
    public SubscriptionBuilder WithDeadLetter(DeadLetterPolicy deadLetter)
    {
        _deadLetter = deadLetter ?? DeadLetterPolicy.Default;
        return this;
    }

    /// <summary>
    /// Requires per-key ordering. Caps throughput and makes a retry block its partition — you get
    /// ordering or independent retry, not both.
    /// </summary>
    public SubscriptionBuilder WithOrdering(OrderingScope ordering)
    {
        _ordering = ordering;
        return this;
    }

    /// <summary>Sets the delivery guarantee.</summary>
    public SubscriptionBuilder WithGuarantee(DeliveryGuarantee guarantee)
    {
        _guarantee = guarantee;
        return this;
    }

    /// <summary>Sets a transport-neutral filter, pushed broker-side where supported.</summary>
    public SubscriptionBuilder WithFilter(string filter)
    {
        _filter = filter;
        return this;
    }

    /// <summary>Sets the wall-clock budget for one handler invocation.</summary>
    public SubscriptionBuilder WithHandlerTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _handlerTimeout = timeout;
        return this;
    }

    internal SubscriptionSpec Build() => new()
    {
        ConsumerGroup = _consumerGroup,
        Source = _source,
        Filter = _filter,
        Guarantee = _guarantee,
        Concurrency = _concurrency,
        PrefetchCount = _prefetch,
        Retry = _retry,
        DeadLetter = _deadLetter,
        Ordering = _ordering,
        HandlerTimeout = _handlerTimeout,
    };
}

/// <summary>
/// Transport-neutral messaging: commands, events, envelope, handler pipeline, inbox, retry, and
/// dead-lettering.
/// </summary>
/// <remarks>
/// Everything valuable is here rather than in an adapter. A transport moves bytes and maps
/// destinations; the semantics that are hard to get right are written once and shared by every
/// transport, which is what makes a new broker an adapter rather than a rewrite.
/// </remarks>
public sealed class MessagingFeature : IMicroFxFeature, IFeatureLifecycle, IFeatureValidator
{
    private readonly List<Action<MessagingBuilder>> _configurations = [];
    private MessagingBuilder? _builder;
    private CapabilityNegotiation? _negotiation;

    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Messaging,
        DisplayName = "Messaging",
        Order = 400,
        DependsOn = [BuiltIn.Core],
        After = [BuiltIn.Security, BuiltIn.MultiTenancy, BuiltIn.Observability],
        EnabledByDefault = false,   // a service with no messaging should carry none of this
        SupportedHosts = HostKinds.Any,
        ConfigurationSection = "MicroFx:Messaging",
    };

    /// <summary>Declares what this service publishes, handles, and subscribes to.</summary>
    public MessagingFeature Configure(Action<MessagingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configurations.Add(configure);
        return this;
    }

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new MessagingOptions();
        context.Configuration.GetSection("MicroFx:Messaging").Bind(options);
        context.AddValidatedOptions<MessagingOptions>();

        var serviceName = options.ServiceName ?? context.Metadata.Name;
        var builder = new MessagingBuilder(serviceName);

        foreach (var configure in _configurations)
        {
            configure(builder);
        }

        _builder = builder;

        var types = new MessageTypeRegistryBuilder();
        var destinations = new DestinationRegistry();

        foreach (var (messageType, destination, _) in builder.Published)
        {
            types.Register(messageType);
            destinations.Register(messageType, destination);
        }

        var registrations = new List<SubscriptionRegistration>();

        foreach (var (commandType, handlerType, specification) in builder.CommandHandlers)
        {
            types.Register(commandType);
            context.Services.TryAddScoped(
                typeof(IHandleCommand<>).MakeGenericType(commandType), handlerType);
            registrations.Add(new SubscriptionRegistration(
                specification, commandType,
                typeof(IHandleCommand<>).MakeGenericType(commandType), MessageKind.Command));
        }

        foreach (var (eventType, handlerType, specification) in builder.EventSubscriptions)
        {
            types.Register(eventType);
            context.Services.TryAddScoped(
                typeof(IHandleEvent<>).MakeGenericType(eventType), handlerType);
            registrations.Add(new SubscriptionRegistration(
                specification, eventType,
                typeof(IHandleEvent<>).MakeGenericType(eventType), MessageKind.Event));
        }

        var registry = types.Build();
        var manifest = new TopologyManifest(destinations.All, [.. registrations.Select(r => r.Specification)]);

        context.Services.TryAddSingleton(registry);
        context.Services.TryAddSingleton(destinations);
        context.Services.TryAddSingleton(manifest);
        context.Services.TryAddSingleton<IReadOnlyList<SubscriptionRegistration>>(registrations);

        RegisterTransport(context, builder, options);

        context.Services.TryAddSingleton<IInboxStore>(provider =>
            new InMemoryInboxStore(provider.GetRequiredService<TimeProvider>()));
        context.Services.TryAddSingleton<IScheduledMessageStore>(provider =>
            new InMemoryScheduledMessageStore(provider.GetRequiredService<TimeProvider>()));

        context.Services.TryAddSingleton<MessagePublisher>();
        context.Services.TryAddSingleton<ICommandSender>(p => p.GetRequiredService<MessagePublisher>());
        context.Services.TryAddSingleton<IEventPublisher>(p => p.GetRequiredService<MessagePublisher>());
        context.Services.TryAddSingleton<ConsumerHost>();

        context.AddDiagnosticSource(MessagingDiagnostics.ActivitySourceName);
        context.AddMeter(MessagingDiagnostics.MeterName);

        // Readiness, never liveness: a broker outage must degrade traffic routing, not restart every
        // replica and turn an outage into an outage plus a restart storm.
        context.AddHealthContribution(HealthContribution.Ready(
            "messaging-transport",
            (provider, _) =>
            {
                var transport = provider.GetService<IMessageTransport>();
                return ValueTask.FromResult(transport is null
                    ? HealthCheckResult.Unhealthy("No message transport is registered.")
                    : HealthCheckResult.Healthy($"Transport '{transport.Name}' is available."));
            }));

        context.Report("transport", TransportName(builder));
        context.Report("publishes", builder.Published.Count);
        context.Report("subscriptions", registrations.Count);
    }

    /// <inheritdoc />
    public async ValueTask StartingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var transport = context.Services.GetRequiredService<IMessageTransport>();
        var manifest = context.Services.GetRequiredService<TopologyManifest>();

        // Assert in production, provision in Development. Auto-creating topology in production is
        // how estates acquire drifted, undocumented destinations nobody dares delete.
        if (transport is ITransportTopologyProvisioner provisioner)
        {
            var mode = context.Metadata.IsDevelopment ? TopologyMode.Provision : TopologyMode.Assert;
            await provisioner.AssertAsync(manifest, mode, cancellationToken).ConfigureAwait(false);
        }

        await context.Services.GetRequiredService<ConsumerHost>()
            .StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask StoppingAsync(FeatureLifecycleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Runs in reverse dependency order, so consumers stop before observability flushes and
        // before any transport connection the adapter holds is closed.
        await context.Services.GetRequiredService<ConsumerHost>()
            .StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ValidationReport> ValidateAsync(
        FeatureValidationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var transport = context.Services.GetRequiredService<IMessageTransport>();
        var manifest = context.Services.GetRequiredService<TopologyManifest>();
        var options = context.Services.GetRequiredService<IOptions<MessagingOptions>>().Value;

        var findings = new List<ValidationFinding>();

        // An in-memory transport in production is silent data loss, not a degraded mode.
        if (transport is InMemoryTransport &&
            !context.Metadata.IsDevelopment &&
            !options.AllowInMemoryTransportInProduction)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Error,
                "The in-memory transport is in use outside Development. Messages exist only inside " +
                "this process, so a restart loses everything in flight. Reference a transport " +
                "adapter, or set AllowInMemoryTransportInProduction to accept the loss explicitly."));
        }

        // Resolved through a scope: a durable inbox is scoped because it holds a DbContext, and
        // reaching for it on the root provider is exactly the mistake this check would hide.
        using var scope = context.Services.CreateScope();

        // A volatile inbox quietly turns "at-least-once with dedupe" into "at-least-once".
        if (scope.ServiceProvider.GetService<IInboxStore>() is InMemoryInboxStore &&
            !context.Metadata.IsDevelopment)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning,
                "The inbox is in-memory, so deduplication does not survive a restart and a message " +
                "in flight across one is processed twice."));
        }

        _negotiation = CapabilityNegotiator.Negotiate(
            manifest,
            transport.Capabilities,
            options.AllowUnconfirmedPublish,
            context.Services.GetService<IScheduledMessageStore>() is not null);

        findings.AddRange(_negotiation.ToValidationReport().Findings);

        return ValueTask.FromResult(
            findings.Count == 0 ? ValidationReport.Ok() : ValidationReport.FromFindings(findings));
    }

    private static string TransportName(MessagingBuilder builder) =>
        builder.TransportInstance?.Name ?? builder.TransportType?.Name ?? "in-memory";

    private static void RegisterTransport(
        FeatureBuildContext context, MessagingBuilder builder, MessagingOptions options)
    {
        if (builder.TransportInstance is { } instance)
        {
            context.Services.TryAddSingleton(instance);
            return;
        }

        if (builder.TransportType is { } transportType)
        {
            context.Services.TryAddSingleton(typeof(IMessageTransport), transportType);
            return;
        }

        // The in-box default, so a service composes and its messaging tests run with no
        // infrastructure at all. Startup validation refuses it outside Development.
        context.Services.TryAddSingleton<IMessageTransport>(provider =>
            new InMemoryTransport(
                new InMemoryTransportOptions(), provider.GetRequiredService<TimeProvider>()));
    }
}

/// <summary>Drains the scheduled-message store, delivering messages that have come due.</summary>
/// <remarks>
/// A background drainer rather than a timer per message: a timer holds the delivery and the memory
/// of everything it captured, and does not survive the restart the store exists to survive.
/// </remarks>
internal sealed partial class ScheduledMessageDrainer(
    IScheduledMessageStore store,
    IMessageTransport transport,
    IOptions<MessagingOptions> options,
    TimeProvider clock,
    Microsoft.Extensions.Logging.ILogger<ScheduledMessageDrainer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SchedulerInterval, clock);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var due = await store.ClaimDueAsync(100, stoppingToken).ConfigureAwait(false);

                foreach (var scheduled in due)
                {
                    await transport.PublishAsync(scheduled.Message, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad message must not stop the drainer; the next tick tries again.
                LogDrainFailed(logger, ex);
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        EventId = 5200, Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "Draining the scheduled-message store failed; retrying on the next tick.")]
    private static partial void LogDrainFailed(
        Microsoft.Extensions.Logging.ILogger logger, Exception exception);
}
