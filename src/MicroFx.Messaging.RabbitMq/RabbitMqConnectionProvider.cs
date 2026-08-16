using MicroFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MicroFx.Messaging.RabbitMq;

/// <summary>Which role a connection serves.</summary>
internal enum ConnectionRole
{
    /// <summary>Publishing.</summary>
    Publisher,

    /// <summary>Consuming.</summary>
    Consumer,
}

/// <summary>
/// Owns the broker connections and channels.
/// </summary>
/// <remarks>
/// <para>
/// One connection per role. When the broker applies flow control it blocks the publishing
/// connection; sharing one connection would stall consumers at the same moment — exactly when they
/// are most needed to drain the backlog that caused the block.
/// </para>
/// <para>
/// One channel per consumer, never shared across threads. An <c>IChannel</c> is not thread-safe,
/// and sharing one produces frame corruption that presents as unrelated protocol errors much later.
/// </para>
/// </remarks>
internal sealed partial class RabbitMqConnectionProvider : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ServiceMetadata _metadata;
    private readonly ILogger<RabbitMqConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ConnectionRole, IConnection> _connections = [];
    private volatile bool _disposed;

    public RabbitMqConnectionProvider(
        IOptions<RabbitMqOptions> options,
        ServiceMetadata metadata,
        ILogger<RabbitMqConnectionProvider> logger)
    {
        _options = options.Value;
        _metadata = metadata;
        _logger = logger;
    }

    /// <summary>Whether every established connection is currently open.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_connections)
            {
                return _connections.Count > 0 && _connections.Values.All(c => c.IsOpen);
            }
        }
    }

    /// <summary>Returns the connection for a role, opening it on first use.</summary>
    public async Task<IConnection> GetConnectionAsync(
        ConnectionRole role, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_connections)
        {
            if (_connections.TryGetValue(role, out var existing) && existing.IsOpen)
            {
                return existing;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_connections)
            {
                if (_connections.TryGetValue(role, out var existing) && existing.IsOpen)
                {
                    return existing;
                }
            }

            var connection = await CreateAsync(role, cancellationToken).ConfigureAwait(false);

            lock (_connections)
            {
                _connections[role] = connection;
            }

            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens a channel on the role's connection.</summary>
    public async Task<IChannel> CreateChannelAsync(
        ConnectionRole role,
        bool publisherConfirms = false,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(role, cancellationToken).ConfigureAwait(false);

        return await connection.CreateChannelAsync(
            new CreateChannelOptions(
                // Confirmations are what make a publish reportable as "done". Without them the
                // outbox would mark a row dispatched on a fire-and-forget write.
                publisherConfirmationsEnabled: publisherConfirms,
                publisherConfirmationTrackingEnabled: publisherConfirms),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IConnection> CreateAsync(ConnectionRole role, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(_options.Uri),
            VirtualHost = _options.VirtualHost,

            // Names the connection in the management UI, so "which replica is holding this
            // channel?" is answerable during an incident rather than a guess.
            ClientProvidedName = $"{_metadata.Name}:{role.ToString().ToLowerInvariant()}:{_metadata.InstanceId}",

            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = _options.NetworkRecoveryInterval,
            RequestedHeartbeat = _options.Heartbeat,
            ConsumerDispatchConcurrency = (ushort)_options.DispatchConcurrency,
        };

        if (!string.IsNullOrEmpty(_options.UserName))
        {
            factory.UserName = _options.UserName;
        }

        if (!string.IsNullOrEmpty(_options.Password))
        {
            factory.Password = _options.Password;
        }

        // Every cluster endpoint is offered, so the client fails over instead of pinning itself to
        // the node it first resolved.
        var endpoints = new List<AmqpTcpEndpoint> { new(new Uri(_options.Uri)) };
        foreach (var endpoint in _options.AdditionalEndpoints)
        {
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed))
            {
                endpoints.Add(new AmqpTcpEndpoint(parsed));
            }
        }

        var connection = await factory
            .CreateConnectionAsync(endpoints, cancellationToken).ConfigureAwait(false);

        var roleName = role.ToString();

        connection.ConnectionShutdownAsync += (_, args) =>
        {
            LogConnectionLost(_logger, roleName, args.ReplyText);
            return Task.CompletedTask;
        };

        connection.RecoverySucceededAsync += (_, _) =>
        {
            LogRecovered(_logger, roleName);
            return Task.CompletedTask;
        };

        connection.ConnectionRecoveryErrorAsync += (_, args) =>
        {
            LogRecoveryFailed(_logger, roleName, args.Exception);
            return Task.CompletedTask;
        };

        LogConnected(_logger, roleName, factory.ClientProvidedName);
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IConnection[] connections;
        lock (_connections)
        {
            connections = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var connection in connections)
        {
            try
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Shutting down. A broker that is already gone must not turn a clean stop into a
                // crash.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        _gate.Dispose();
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information,
        Message = "RabbitMQ {Role} connection established as {ClientName}.")]
    private static partial void LogConnected(ILogger logger, string role, string clientName);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Warning,
        Message = "RabbitMQ {Role} connection lost: {Reason}. Readiness degrades; liveness does not.")]
    private static partial void LogConnectionLost(ILogger logger, string role, string reason);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Information,
        Message = "RabbitMQ {Role} connection recovered.")]
    private static partial void LogRecovered(ILogger logger, string role);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Error,
        Message = "RabbitMQ {Role} connection recovery failed; will retry.")]
    private static partial void LogRecoveryFailed(ILogger logger, string role, Exception exception);
}
