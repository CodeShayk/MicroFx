using FluentValidation;
using FluentValidation.Results;
using MicroFx.Api;
using MicroFx.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.Validation;

/// <summary>Options for the validation feature.</summary>
public sealed class ValidationOptions
{
    /// <summary>
    /// Whether to scan the application assembly for validators. Turn off for a fully explicit
    /// composition, then register validators individually.
    /// </summary>
    public bool ScanApplicationAssembly { get; set; } = true;
}

/// <summary>
/// Request validation shared by the HTTP and messaging entry points, so a DTO validates identically
/// however it arrives.
/// </summary>
/// <remarks>
/// Validation failures are the one class of error where echoing detail back is both safe and
/// necessary — the caller needs to know which field is wrong. The detail is confined to property
/// names and validator-authored messages, never exception text.
/// </remarks>
public sealed class ValidationFeature : IMicroFxFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Validation,
        DisplayName = "Validation",
        Order = 110,
        DependsOn = [BuiltIn.Core],
        ConfigurationSection = "MicroFx:Validation",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = new ValidationOptions();
        context.Configuration.GetSection("MicroFx:Validation").Bind(options);
        context.AddValidatedOptions<ValidationOptions>();

        // Scoped, not singleton: FluentValidation registers validators as scoped, and a singleton
        // runner would capture the root provider and fail to resolve them at request time.
        context.Services.TryAddScoped<IValidationRunner, ValidationRunner>();

        var registered = 0;
        if (options.ScanApplicationAssembly &&
            Core.ApplicationAssembly.Resolve(context.Environment) is { } application)
        {
            // The application's own assembly only: a transitively-referenced package should not be
            // able to inject validation rules into a service that never asked for them.
            context.Services.AddValidatorsFromAssembly(application, includeInternalTypes: false);
            registered = Core.ApplicationAssembly.ExportedTypes(application).Count(IsValidator);
        }

        context.Report("validators", registered);
    }

    private static bool IsValidator(Type type) =>
        type is { IsAbstract: false, IsInterface: false } &&
        Array.Exists(
            type.GetInterfaces(),
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>));
}

/// <summary>Validates a value and turns any failure into a problem response.</summary>
public interface IValidationRunner
{
    /// <summary>
    /// Validates <paramref name="instance"/>. Returns null when valid, otherwise a 400 result
    /// carrying the per-field errors.
    /// </summary>
    ValueTask<IResult?> ValidateAsync<T>(
        T instance, HttpContext context, CancellationToken cancellationToken = default);
}

internal sealed class ValidationRunner(
    IServiceProvider services, ProblemDetailsBuilder problems) : IValidationRunner
{
    public async ValueTask<IResult?> ValidateAsync<T>(
        T instance, HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (services.GetService<IValidator<T>>() is not { } validator)
        {
            return null;
        }

        var result = await validator
            .ValidateAsync(instance!, cancellationToken)
            .ConfigureAwait(false);

        return result.IsValid ? null : Failure(result.Errors, context);
    }

    private IResult Failure(IEnumerable<ValidationFailure> failures, HttpContext context)
    {
        var problem = problems.Create(
            StatusCodes.Status400BadRequest,
            "One or more validation errors occurred",
            type: ProblemDetailsBuilder.TypeBase + "validation",
            context: context);

        // RFC 9457 'errors' shape: property name to messages. Only the property name and the
        // validator's own message cross the boundary; the attempted value never does, because it
        // may be the credential the caller got wrong.
        problem.Extensions["errors"] = failures
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(f => f.ErrorMessage).ToArray(),
                StringComparer.Ordinal);

        return Results.Problem(problem);
    }
}

/// <summary>Endpoint filter applying validation before the handler runs.</summary>
/// <typeparam name="T">The argument type to validate.</typeparam>
public sealed class ValidationFilter<T> : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return await next(context).ConfigureAwait(false);
        }

        var runner = context.HttpContext.RequestServices.GetRequiredService<IValidationRunner>();
        var failure = await runner
            .ValidateAsync(argument, context.HttpContext, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        // Validation runs before any handler logic, so a malformed request never reaches domain code.
        return failure ?? await next(context).ConfigureAwait(false);
    }
}

/// <summary>Route-builder helpers for validation.</summary>
public static class ValidationEndpointExtensions
{
    /// <summary>
    /// Validates an argument of type <typeparamref name="T"/> before the handler runs, returning
    /// 400 with per-field errors if it fails.
    /// </summary>
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddEndpointFilter<RouteHandlerBuilder, ValidationFilter<T>>()
            .ProducesValidationProblem();
    }
}
