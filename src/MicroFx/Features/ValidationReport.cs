namespace MicroFx.Features;

/// <summary>Severity of a single validation finding.</summary>
public enum ValidationSeverity
{
    /// <summary>Informational; startup proceeds.</summary>
    Information,

    /// <summary>A concern worth surfacing; startup proceeds.</summary>
    Warning,

    /// <summary>A precondition is unmet; startup aborts once all validators have run.</summary>
    Error,
}

/// <summary>A single validation finding.</summary>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Message">
/// What is wrong and what to do about it. Must not contain secrets — this text reaches logs and,
/// in non-production environments, the diagnostics endpoint.
/// </param>
public readonly record struct ValidationFinding(ValidationSeverity Severity, string Message);

/// <summary>
/// The result of one feature's validation. Reports are aggregated across all features so a
/// misconfigured service surfaces every problem in a single startup.
/// </summary>
public sealed class ValidationReport
{
    private static readonly ValidationReport OkInstance = new([]);

    private ValidationReport(IReadOnlyList<ValidationFinding> findings) => Findings = findings;

    /// <summary>The findings, in the order they were produced.</summary>
    public IReadOnlyList<ValidationFinding> Findings { get; }

    /// <summary>Whether any finding is an <see cref="ValidationSeverity.Error"/>.</summary>
    public bool HasErrors => Findings.Any(f => f.Severity == ValidationSeverity.Error);

    /// <summary>A report with no findings.</summary>
    public static ValidationReport Ok() => OkInstance;

    /// <summary>A report with a single error.</summary>
    public static ValidationReport Error(string message) =>
        new([new ValidationFinding(ValidationSeverity.Error, message)]);

    /// <summary>A report with a single warning.</summary>
    public static ValidationReport Warning(string message) =>
        new([new ValidationFinding(ValidationSeverity.Warning, message)]);

    /// <summary>A report with a single informational finding.</summary>
    public static ValidationReport Information(string message) =>
        new([new ValidationFinding(ValidationSeverity.Information, message)]);

    /// <summary>A report built from several findings.</summary>
    public static ValidationReport FromFindings(IEnumerable<ValidationFinding> findings) =>
        new([.. findings]);
}

/// <summary>
/// Thrown when aggregated validation produced at least one error. The message lists every finding,
/// so one startup attempt reveals every problem.
/// </summary>
public sealed class FeatureValidationException : Exception
{
    internal FeatureValidationException(string message, IReadOnlyList<string> failures)
        : base(message) => Failures = failures;

    /// <summary>The individual failures, each prefixed with the reporting feature's id.</summary>
    public IReadOnlyList<string> Failures { get; }
}
