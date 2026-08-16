using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MicroFx.Api;

/// <summary>
/// Accepts or generates a correlation id, echoes it, and puts it on the log scope and the current
/// <see cref="Activity"/>.
/// </summary>
/// <remarks>
/// The inbound header is <em>untrusted input</em> that ends up in logs, response headers, and
/// telemetry. It is therefore length-capped and character-restricted before use: an unvalidated
/// value is a log-injection and response-splitting vector, and a large one is a cheap way to bloat
/// every log line the request touches.
/// </remarks>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Header carrying the correlation id.</summary>
    public const string HeaderName = "X-Correlation-Id";

    private const int MaxLength = 128;

    [GeneratedRegex("^[A-Za-z0-9._:@-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCorrelationId { get; }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Sanitize(context.Request.Headers[HeaderName]);

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("microfx.correlation_id", correlationId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
        });

        await next(context).ConfigureAwait(false);
    }

    private static string Sanitize(string? inbound) =>
        !string.IsNullOrEmpty(inbound) &&
        inbound.Length <= MaxLength &&
        SafeCorrelationId.IsMatch(inbound)
            ? inbound
            : Guid.NewGuid().ToString("N");
}

/// <summary>Applies response security headers.</summary>
/// <remarks>
/// Defaults suit an API: a restrictive CSP, no framing, no sniffing, no referrer. A service serving
/// a browser UI will need to relax the CSP deliberately rather than inherit an unsuitable one.
/// </remarks>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next, ApiOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";

        // An API returns data, not markup or scripts. Denying everything is the correct default and
        // makes an accidentally-served HTML error page inert.
        headers["Content-Security-Policy"] = options.ContentSecurityPolicy;

        // Legacy fingerprinting surface; nothing downstream should depend on it.
        headers.Remove("X-Powered-By");
        headers.Remove("Server");

        if (options.PermissionsPolicy is { } permissions)
        {
            headers["Permissions-Policy"] = permissions;
        }

        if (context.Request.IsHttps && options.HstsMaxAge > TimeSpan.Zero)
        {
            headers["Strict-Transport-Security"] =
                $"max-age={(long)options.HstsMaxAge.TotalSeconds}; includeSubDomains";
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Converts an unhandled exception into an RFC 9457 problem response.
/// </summary>
/// <remarks>
/// Outermost in the pipeline, so nothing above it can throw unhandled. Logs the exception with its
/// full detail — that belongs in the log — and returns only the mapped status and trace id.
/// </remarks>
internal sealed partial class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ProblemDetailsBuilder problems,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Client disconnects are routine under load and are not defects; logging them at Error
            // trains people to ignore the error log.
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                LogClientAborted(logger, context.Request.Path.Value ?? "/");
                return;
            }

            if (context.Response.HasStarted)
            {
                // The status line is already on the wire. Anything written now would corrupt the
                // response body, so the only honest action is to log and abort the connection.
                LogAfterResponseStarted(logger, exception);
                context.Abort();
                return;
            }

            var problem = problems.FromException(exception, context);

            var status = problem.Status ?? StatusCodes.Status500InternalServerError;
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? "/";

            if (status >= StatusCodes.Status500InternalServerError)
            {
                LogUnhandled(logger, method, path, exception);
            }
            else
            {
                LogHandled(logger, method, path, status, exception);
            }

            context.Response.Clear();
            context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json; charset=utf-8";

            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error,
        Message = "Unhandled exception serving {Method} {Path}.")]
    private static partial void LogUnhandled(ILogger logger, string method, string path, Exception exception);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning,
        Message = "Request {Method} {Path} failed with {StatusCode}.")]
    private static partial void LogHandled(
        ILogger logger, string method, string path, int statusCode, Exception exception);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug,
        Message = "Client aborted request to {Path}.")]
    private static partial void LogClientAborted(ILogger logger, string path);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
        Message = "Exception thrown after the response had started; connection aborted.")]
    private static partial void LogAfterResponseStarted(ILogger logger, Exception exception);
}
