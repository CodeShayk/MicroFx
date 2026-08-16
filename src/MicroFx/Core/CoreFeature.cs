using System.Text.Json;
using System.Text.Json.Serialization;
using MicroFx.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroFx.Core;

/// <summary>Options for the core feature.</summary>
public sealed class CoreOptions
{
    /// <summary>
    /// Whether to apply MicroFx JSON conventions to <see cref="JsonSerializerOptions"/> used by the
    /// HTTP stack: camelCase, no null properties, string enums, and strict number handling.
    /// </summary>
    public bool ApplyJsonConventions { get; set; } = true;
}

/// <summary>
/// The root kernel feature: service identity, time, and serialization conventions.
/// </summary>
/// <remarks>
/// Everything else depends on this, directly or transitively. It is a kernel feature because a
/// service that cannot say what it is or what time it thinks it is cannot be diagnosed.
/// </remarks>
public sealed class CoreFeature : IMicroFxFeature
{
    /// <inheritdoc />
    public FeatureDescriptor Descriptor { get; } = new()
    {
        Id = BuiltIn.Core,
        DisplayName = "Core",
        IsKernel = true,
        Order = 0,
        ConfigurationSection = "MicroFx:Core",
    };

    /// <inheritdoc />
    public void Configure(FeatureBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.AddValidatedOptions<CoreOptions>();

        // TryAdd throughout: a service registering its own TimeProvider — a fake, in tests — keeps it.
        context.Services.TryAddSingleton(TimeProvider.System);

        context.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            ApplyJsonConventions(options.SerializerOptions));

        context.Report("clock", "system");
        context.Report("instance", context.Metadata.InstanceId);

        if (context.Metadata.Commit is { } commit)
        {
            context.Report("commit", commit);
        }
    }

    /// <summary>
    /// Applies the platform's JSON conventions. Kept public so a feature serializing outside the HTTP
    /// stack produces an identical wire shape.
    /// </summary>
    public static void ApplyJsonConventions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // DictionaryKeyPolicy is deliberately NOT set. Property names are part of the contract and
        // are ours to shape; dictionary keys are data — tenant ids, header names, feature ids — and
        // silently rewriting them corrupts the payload in a way that is very hard to trace back.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        // Reject rather than coerce: silently accepting "5" for an int is how a contract drifts
        // between a producer and a consumer without either noticing.
        options.NumberHandling = JsonNumberHandling.Strict;
        options.PropertyNameCaseInsensitive = false;

        // Bound the payload shape. Deeply nested JSON is a cheap way to burn stack and CPU on an
        // unauthenticated endpoint; 32 levels is far beyond any legitimate DTO.
        options.MaxDepth = 32;
    }
}
