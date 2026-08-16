using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MicroFx.Security;

/// <summary>The kind of security-relevant event being recorded.</summary>
public enum AuditEventKind
{
    /// <summary>A caller failed to authenticate.</summary>
    AuthenticationFailed,

    /// <summary>An authenticated caller was denied access.</summary>
    AuthorizationDenied,

    /// <summary>A privileged operation was performed.</summary>
    PrivilegedAction,

    /// <summary>Configuration or a secret changed.</summary>
    ConfigurationChanged,

    /// <summary>A caller attempted to reach another tenant's data.</summary>
    CrossTenantAccess,
}

/// <summary>
/// A security audit record.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Subject">Authenticated subject, or null when unauthenticated.</param>
/// <param name="TenantId">Tenant in scope, when known.</param>
/// <param name="Resource">What was being accessed — a route, not a payload.</param>
/// <param name="Outcome">Short, stable outcome token.</param>
/// <param name="CorrelationId">Correlates the record with the request logs.</param>
/// <param name="TraceId">Correlates the record with the distributed trace.</param>
/// <param name="RemoteAddress">Caller address, when available.</param>
/// <param name="OccurredAt">When the event happened, from <see cref="TimeProvider"/>.</param>
public sealed record AuditEvent(
    AuditEventKind Kind,
    string? Subject,
    string? TenantId,
    string Resource,
    string Outcome,
    string? CorrelationId,
    string? TraceId,
    string? RemoteAddress,
    DateTimeOffset OccurredAt);

/// <summary>
/// Receives audit events.
/// </summary>
/// <remarks>
/// Audit is a separate stream from application logs on purpose: it has a different retention
/// policy, a different access-control posture, and must survive a service raising its own log level
/// to reduce noise. An adapter writing to an append-only sink replaces the default.
/// </remarks>
public interface IAuditSink
{
    /// <summary>Records an event. Must not throw: a failed audit write must not fail the request.</summary>
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes audit events to a dedicated logger category at Information.
/// </summary>
/// <remarks>
/// The default so that audit works out of the box, but it is a floor rather than a destination: a
/// regulated service should replace it with an append-only sink whose retention it controls. The
/// dedicated category (<c>MicroFx.Audit</c>) makes routing it to a separate stream a logging-config
/// change rather than a code change.
/// </remarks>
internal sealed partial class LoggerAuditSink(ILoggerFactory loggerFactory) : IAuditSink
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("MicroFx.Audit");

    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        LogAudit(
            _logger,
            auditEvent.Kind,
            auditEvent.Subject ?? "-",
            auditEvent.TenantId ?? "-",
            auditEvent.Resource,
            auditEvent.Outcome,
            auditEvent.CorrelationId ?? "-",
            auditEvent.TraceId ?? "-",
            auditEvent.RemoteAddress ?? "-",
            auditEvent.OccurredAt);

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 4900, Level = LogLevel.Information,
        Message = "AUDIT {Kind} subject={Subject} tenant={TenantId} resource={Resource} " +
                  "outcome={Outcome} correlation={CorrelationId} trace={TraceId} " +
                  "remote={RemoteAddress} at={OccurredAt:O}")]
    private static partial void LogAudit(
        ILogger logger, AuditEventKind kind, string subject, string tenantId, string resource,
        string outcome, string correlationId, string traceId, string remoteAddress,
        DateTimeOffset occurredAt);
}

/// <summary>Builds audit events from the ambient request.</summary>
public sealed class AuditRecorder(IAuditSink sink, TimeProvider clock)
{
    /// <summary>Records an event, deriving caller identity and correlation from the request.</summary>
    public async ValueTask RecordAsync(
        AuditEventKind kind,
        HttpContext context,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var auditEvent = new AuditEvent(
            kind,
            context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name,
            context.Items.TryGetValue("MicroFx.TenantId", out var tenant) ? tenant as string : null,
            // The route pattern, not the raw path: a path can contain caller-supplied identifiers
            // that do not belong in a long-retention audit stream.
            context.GetEndpoint()?.DisplayName ?? context.Request.Path.Value ?? "-",
            outcome,
            context.Items.TryGetValue("X-Correlation-Id", out var correlation) ? correlation as string : null,
            Activity.Current?.TraceId.ToString(),
            context.Connection.RemoteIpAddress?.ToString(),
            clock.GetUtcNow());

        try
        {
            await sink.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (kind != AuditEventKind.PrivilegedAction)
        {
            // A failed audit write must not turn a denied request into a 500 — the denial already
            // happened and the caller must still be told. Privileged actions are the exception:
            // there, losing the record is worse than failing the operation.
        }
    }
}
