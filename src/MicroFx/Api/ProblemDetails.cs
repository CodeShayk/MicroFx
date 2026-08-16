using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MicroFx.Api;

/// <summary>
/// Maps an exception to an HTTP status code and a safe, client-facing title.
/// </summary>
/// <remarks>
/// Implementations are resolved in registration order; the first non-null result wins. Register a
/// custom mapper before <c>AddMicroFx</c>, or with <c>services.Replace</c>, to map a domain
/// exception taxonomy without touching the platform.
/// </remarks>
public interface IExceptionMapper
{
    /// <summary>Returns a mapping for this exception, or null to defer to the next mapper.</summary>
    ExceptionMapping? Map(Exception exception);
}

/// <summary>The outcome of mapping an exception.</summary>
/// <param name="StatusCode">HTTP status to return.</param>
/// <param name="Title">
/// Short, stable, client-facing summary. Must be safe to publish: it is returned to unauthenticated
/// callers and may be logged by intermediaries.
/// </param>
/// <param name="Detail">
/// Optional elaboration. Subject to the same rule as <paramref name="Title"/> — never put an
/// exception message here unless the exception type is one the service authored deliberately.
/// </param>
/// <param name="Type">Optional problem type URI. Defaults to a status-derived value.</param>
public readonly record struct ExceptionMapping(
    int StatusCode,
    string Title,
    string? Detail = null,
    string? Type = null);

/// <summary>Maps the framework exceptions every service can raise.</summary>
internal sealed class DefaultExceptionMapper : IExceptionMapper
{
    public ExceptionMapping? Map(Exception exception) => exception switch
    {
        // The client went away or the request timed out. 499 has no framework constant; it is
        // Nginx's convention and is what most log pipelines already understand.
        OperationCanceledException => new ExceptionMapping(499, "Client closed request"),

        BadHttpRequestException bad => new ExceptionMapping(
            bad.StatusCode, "Malformed request"),

        // Deliberately generic. The parameter name in the exception can reveal internal
        // structure, so it is logged rather than returned.
        ArgumentException or FormatException => new ExceptionMapping(
            StatusCodes.Status400BadRequest, "Invalid request"),

        NotImplementedException => new ExceptionMapping(
            StatusCodes.Status501NotImplemented, "Not implemented"),

        TimeoutException => new ExceptionMapping(
            StatusCodes.Status504GatewayTimeout, "Upstream timeout"),

        _ => null,
    };
}

/// <summary>Adds fields to a problem response before it is written.</summary>
public interface IProblemDetailsEnricher
{
    /// <summary>Mutates the problem details. Must not add anything caller-supplied or sensitive.</summary>
    void Enrich(ProblemDetails problem, HttpContext context);
}

/// <summary>
/// Builds RFC 9457 problem responses.
/// </summary>
/// <remarks>
/// One rule governs everything here: a problem response is <em>public output</em>. Exception
/// messages, stack traces, and inner-exception chains routinely carry connection strings, file
/// paths, and internal host names, so none of them cross this boundary outside Development. The
/// trace id is returned instead — it is useless to an attacker and sufficient for an operator.
/// </remarks>
public sealed class ProblemDetailsBuilder(
    IEnumerable<IExceptionMapper> mappers,
    IEnumerable<IProblemDetailsEnricher> enrichers,
    Core.ServiceMetadata metadata)
{
    private readonly IExceptionMapper[] _mappers = [.. mappers];
    private readonly IProblemDetailsEnricher[] _enrichers = [.. enrichers];

    /// <summary>Base URI for problem types the platform emits.</summary>
    public const string TypeBase = "https://problems.microfx.dev/";

    /// <summary>Builds a problem response for an unhandled exception.</summary>
    public ProblemDetails FromException(Exception exception, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        var mapping = MapExceptionCore(exception)
                      ?? new ExceptionMapping(
                          StatusCodes.Status500InternalServerError, "An unexpected error occurred");

        var problem = Create(mapping.StatusCode, mapping.Title, mapping.Detail, mapping.Type, context);

        // Development only, and gated on the environment rather than a flag someone can flip in
        // production config. An accidental "true" in a production appsettings must not be able to
        // start leaking stack traces.
        if (metadata.IsDevelopment)
        {
            problem.Extensions["exception"] = exception.GetType().FullName;
            problem.Extensions["exceptionMessage"] = exception.Message;
        }

        return problem;
    }

    /// <summary>Builds a problem response from a status code.</summary>
    public ProblemDetails Create(
        int statusCode,
        string title,
        string? detail = null,
        string? type = null,
        HttpContext? context = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = type ?? TypeBase + statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Instance = context?.Request.Path.Value,
        };

        // The trace id is the whole point: it lets an operator find the request in the logs without
        // the response carrying anything an attacker can use.
        var traceId = Activity.Current?.TraceId.ToString() ?? context?.TraceIdentifier;
        if (traceId is not null)
        {
            problem.Extensions["traceId"] = traceId;
        }

        if (context is not null)
        {
            foreach (var enricher in _enrichers)
            {
                enricher.Enrich(problem, context);
            }
        }

        return problem;
    }

    /// <summary>Resolves an exception to a status code without building a response.</summary>
    public ExceptionMapping? MapException(Exception exception) => MapExceptionCore(exception);

    private ExceptionMapping? MapExceptionCore(Exception exception)
    {
        foreach (var mapper in _mappers)
        {
            if (mapper.Map(exception) is { } mapping)
            {
                return mapping;
            }
        }

        return null;
    }
}
