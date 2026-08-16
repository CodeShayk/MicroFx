using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MicroFx.Core;

/// <summary>
/// Identity of the running service. Populated once at startup and used as the OpenTelemetry resource,
/// the log scope, and the cost-attribution tag set.
/// </summary>
public sealed record ServiceMetadata
{
    /// <summary>Service name, e.g. <c>orders</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Informational version, including the source-control suffix where present.</summary>
    public required string Version { get; init; }

    /// <summary>Deployment environment, e.g. <c>Production</c>.</summary>
    public required string Environment { get; init; }

    /// <summary>
    /// Unique per process. Distinguishes replicas in telemetry and in broker client names, which is
    /// what makes "which instance is wedged?" answerable.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>Owning team, for alert routing.</summary>
    public string? Team { get; init; }

    /// <summary>Cost centre, for spend attribution.</summary>
    public string? CostCenter { get; init; }

    /// <summary>Process role: <c>api</c>, <c>consumer</c>, <c>relay</c>, or <c>all</c>.</summary>
    public string Role { get; init; } = "all";

    /// <summary>Source-control commit, when stamped into the assembly at build time.</summary>
    public string? Commit { get; init; }

    /// <summary>Whether the host is running in the Development environment.</summary>
    public bool IsDevelopment =>
        string.Equals(Environment, Environments.Development, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds metadata from the entry assembly and configuration. Configuration wins, so a
    /// deployment can override the assembly's idea of its own name without a rebuild.
    /// </summary>
    [SuppressMessage("Design", "CA1062:Validate arguments of public methods",
        Justification = "Both arguments are dereferenced immediately; a null is a programming error, not input.")]
    public static ServiceMetadata Create(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var assembly = Assembly.GetEntryAssembly();
        var section = configuration.GetSection("MicroFx:Service");

        var informational = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return new ServiceMetadata
        {
            Name = section["Name"]
                   ?? environment.ApplicationName
                   ?? assembly?.GetName().Name
                   ?? "unknown",
            Version = section["Version"]
                      ?? informational
                      ?? assembly?.GetName().Version?.ToString()
                      ?? "0.0.0",
            Environment = environment.EnvironmentName,
            InstanceId = section["InstanceId"] ?? Guid.NewGuid().ToString("N")[..12],
            Team = section["Team"],
            CostCenter = section["CostCenter"],
            Role = configuration["MicroFx:Role"] ?? section["Role"] ?? "all",
            Commit = ExtractCommit(informational),
        };
    }

    /// <summary>Pulls the <c>+sha</c> suffix off an informational version, when the build stamped one.</summary>
    private static string? ExtractCommit(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
        {
            return null;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && plus < informationalVersion.Length - 1
            ? informationalVersion[(plus + 1)..]
            : null;
    }
}
