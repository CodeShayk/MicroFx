using Microsoft.CodeAnalysis;

namespace MicroFx.Analyzers;

/// <summary>The diagnostics MicroFx enforces at compile time.</summary>
/// <remarks>
/// Each rule encodes a convention the platform relies on but cannot enforce at runtime, or can only
/// enforce once the damage is done. A rule earns its place by describing a mistake that is easy to
/// make, hard to notice, and expensive once it reaches production.
/// </remarks>
internal static class MicroFxDiagnostics
{
    private const string Composition = "MicroFx.Composition";
    private const string Correctness = "MicroFx.Correctness";
    private const string Platform = "MicroFx.Platform";

    /// <summary>A non-platform assembly claimed a reserved feature id.</summary>
    public static readonly DiagnosticDescriptor ReservedFeaturePrefix = new(
        "MFX1001",
        "Feature id uses the reserved 'microfx.' prefix",
        "Feature id '{0}' uses the reserved 'microfx.' prefix. Prefix custom feature ids with your organisation instead.",
        Composition,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Operators trust the microfx. prefix when reading the feature catalog to tell platform " +
            "features from service ones. A third-party feature using it could impersonate a " +
            "built-in. The kernel also rejects this at startup; the analyzer catches it sooner.");

    /// <summary>Blocking or I/O work in a feature's build pass.</summary>
    public static readonly DiagnosticDescriptor BlockingWorkInConfigure = new(
        "MFX1003",
        "Blocking call in a feature's Configure method",
        "'{0}' blocks inside Configure. Move startup work to IFeatureLifecycle.StartingAsync.",
        Composition,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Configure is not cancellable, not traced, and not budgeted. Work done there hangs a " +
            "deployment with no attribution, whereas StartingAsync is ordered, budgeted, and fails " +
            "startup naming the feature responsible.");

    /// <summary>A directly constructed <c>HttpClient</c>.</summary>
    public static readonly DiagnosticDescriptor RawHttpClient = new(
        "MFX1010",
        "HttpClient constructed directly",
        "Construct HttpClient through IHttpClientFactory or a typed client so it inherits the platform's resilience pipeline",
        Correctness,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A directly constructed HttpClient has no timeout policy, no retry, no circuit breaker, " +
            "and no connection recycling — so it pins a stale DNS entry through a failover and " +
            "leaks sockets under load.");

    /// <summary>Ambient clock use instead of <c>TimeProvider</c>.</summary>
    public static readonly DiagnosticDescriptor AmbientClock = new(
        "MFX1011",
        "Ambient clock used instead of TimeProvider",
        "'{0}' reads the ambient clock. Inject TimeProvider so the behaviour is testable.",
        Correctness,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Code that reads the system clock directly cannot be tested without waiting in real " +
            "time, which is how retry ladders, schedules, and expiry windows end up either " +
            "untested or slow and flaky.");

    /// <summary>A domain event published to a transport.</summary>
    public static readonly DiagnosticDescriptor DomainEventPublished = new(
        "MFX1022",
        "Domain event published to a transport",
        "'{0}' is a domain event and must not be published to a transport. Publish an integration event instead.",
        Correctness,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A domain event is an internal implementation detail, free to change. An integration " +
            "event is a published contract subject to compatibility rules. Publishing the former " +
            "turns a refactor into a breaking change for every subscriber.");

    /// <summary>A built-in feature registered a service without <c>TryAdd</c>.</summary>
    public static readonly DiagnosticDescriptor PlatformMustUseTryAdd = new(
        "MFX2001",
        "Built-in feature must register services with TryAdd",
        "'{0}' overwrites any existing registration. Built-in features must use TryAdd so a service can substitute the implementation.",
        Platform,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TryAdd discipline is what preserves the DI-override escape hatch. A single stray Add " +
            "silently removes it for one interface, and nobody discovers that until they need it.");
}
