using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace MicroFx.Analyzers;

/// <summary>
/// Enforces the MicroFx conventions that cannot be enforced at runtime, or can only be enforced
/// once the damage is done.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MicroFxAnalyzer : DiagnosticAnalyzer
{
    private const string PlatformAssemblyName = "MicroFx";
    private const string FeatureInterface = "MicroFx.Features.IMicroFxFeature";
    private const string DomainEventInterface = "MicroFx.Messaging.IDomainEvent";
    private const string EventPublisherInterface = "MicroFx.Messaging.IEventPublisher";
    private const string ReservedPrefix = "microfx.";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            MicroFxDiagnostics.ReservedFeaturePrefix,
            MicroFxDiagnostics.BlockingWorkInConfigure,
            MicroFxDiagnostics.RawHttpClient,
            MicroFxDiagnostics.AmbientClock,
            MicroFxDiagnostics.DomainEventPublished,
            MicroFxDiagnostics.PlatformMustUseTryAdd);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        // Generated code is not the author's to fix, and analysing it would report the same issue
        // on every rebuild.
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationStart =>
        {
            var wellKnown = WellKnownTypes.From(compilationStart.Compilation);

            // Nothing to enforce in an assembly that does not reference the platform.
            if (!wellKnown.ReferencesMicroFx)
            {
                return;
            }

            var isPlatformAssembly = string.Equals(
                compilationStart.Compilation.AssemblyName, PlatformAssemblyName, System.StringComparison.Ordinal);

            compilationStart.RegisterOperationAction(
                operation => AnalyzeObjectCreation(operation, wellKnown),
                OperationKind.ObjectCreation);

            compilationStart.RegisterOperationAction(
                operation => AnalyzePropertyReference(operation, wellKnown),
                OperationKind.PropertyReference);

            compilationStart.RegisterOperationAction(
                operation => AnalyzeInvocation(operation, wellKnown, isPlatformAssembly),
                OperationKind.Invocation);

            compilationStart.RegisterOperationAction(
                operation => AnalyzeSimpleAssignment(operation, wellKnown, isPlatformAssembly),
                OperationKind.SimpleAssignment);
        });
    }

    /// <summary>MFX1010 — a directly constructed <c>HttpClient</c>.</summary>
    private static void AnalyzeObjectCreation(OperationAnalysisContext context, WellKnownTypes types)
    {
        var creation = (IObjectCreationOperation)context.Operation;

        if (types.HttpClient is not null &&
            SymbolEqualityComparer.Default.Equals(creation.Type, types.HttpClient))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(MicroFxDiagnostics.RawHttpClient, creation.Syntax.GetLocation()));
        }
    }

    /// <summary>MFX1011 — an ambient clock read.</summary>
    private static void AnalyzePropertyReference(OperationAnalysisContext context, WellKnownTypes types)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        var property = reference.Property;

        if (!property.IsStatic ||
            property.Name is not ("Now" or "UtcNow" or "Today"))
        {
            return;
        }

        var containing = property.ContainingType;

        if (SymbolEqualityComparer.Default.Equals(containing, types.DateTime) ||
            SymbolEqualityComparer.Default.Equals(containing, types.DateTimeOffset))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MicroFxDiagnostics.AmbientClock,
                reference.Syntax.GetLocation(),
                $"{containing.Name}.{property.Name}"));
        }
    }

    /// <summary>MFX1003, MFX1022, MFX2001 — invocation-shaped rules.</summary>
    private static void AnalyzeInvocation(
        OperationAnalysisContext context, WellKnownTypes types, bool isPlatformAssembly)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        AnalyzeDomainEventPublish(context, invocation, method, types);
        AnalyzeBlockingInConfigure(context, invocation, method, types);

        if (isPlatformAssembly)
        {
            AnalyzeTryAddDiscipline(context, invocation, method, types);
        }
    }

    /// <summary>MFX1022 — publishing something that is a domain event.</summary>
    private static void AnalyzeDomainEventPublish(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        IMethodSymbol method,
        WellKnownTypes types)
    {
        if (types.DomainEvent is null ||
            types.EventPublisher is null ||
            method.Name != "PublishAsync")
        {
            return;
        }

        var receiver = invocation.Instance?.Type;
        if (receiver is null || !ImplementsOrIs(receiver, types.EventPublisher))
        {
            return;
        }

        // The published argument is the first parameter; a domain event reaching it means an
        // internal detail is about to become a published contract.
        var argument = invocation.Arguments.FirstOrDefault();
        var argumentType = argument?.Value.Type ?? method.TypeArguments.FirstOrDefault();

        if (argumentType is not null && ImplementsOrIs(argumentType, types.DomainEvent))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MicroFxDiagnostics.DomainEventPublished,
                invocation.Syntax.GetLocation(),
                argumentType.Name));
        }
    }

    /// <summary>MFX1003 — blocking inside a feature's Configure.</summary>
    private static void AnalyzeBlockingInConfigure(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        IMethodSymbol method,
        WellKnownTypes types)
    {
        if (!IsInsideFeatureConfigure(context.ContainingSymbol, types))
        {
            return;
        }

        var containingType = method.ContainingType?.ToDisplayString();

        var blocking =
            (method.Name == "Sleep" && containingType == "System.Threading.Thread") ||
            (method.Name == "Wait" && containingType is "System.Threading.Tasks.Task") ||
            (method.Name == "GetResult" && containingType?.Contains("TaskAwaiter") == true) ||
            method.Name == "RunSynchronously";

        if (blocking)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MicroFxDiagnostics.BlockingWorkInConfigure,
                invocation.Syntax.GetLocation(),
                $"{containingType}.{method.Name}"));
        }
    }

    /// <summary>MFX2001 — a built-in feature registering without TryAdd.</summary>
    private static void AnalyzeTryAddDiscipline(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        IMethodSymbol method,
        WellKnownTypes types)
    {
        if (!IsInsideFeatureConfigure(context.ContainingSymbol, types))
        {
            return;
        }

        // AddHostedService and the Add*Options family are legitimately additive: several
        // registrations coexist rather than one overwriting another.
        var overwriting =
            method.Name is "AddSingleton" or "AddScoped" or "AddTransient" &&
            method.ContainingType?.ToDisplayString()
                == "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions";

        if (overwriting)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MicroFxDiagnostics.PlatformMustUseTryAdd,
                invocation.Syntax.GetLocation(),
                method.Name));
        }
    }

    /// <summary>MFX1001 — a reserved feature id in a non-platform assembly.</summary>
    private static void AnalyzeSimpleAssignment(
        OperationAnalysisContext context, WellKnownTypes types, bool isPlatformAssembly)
    {
        if (isPlatformAssembly || types.FeatureDescriptor is null)
        {
            return;
        }

        var assignment = (ISimpleAssignmentOperation)context.Operation;

        if (assignment.Target is not IPropertyReferenceOperation target ||
            target.Property.Name != "Id" ||
            !SymbolEqualityComparer.Default.Equals(target.Property.ContainingType, types.FeatureDescriptor))
        {
            return;
        }

        // Only a literal can be judged here. A computed id is checked by the kernel at startup,
        // which is the backstop this rule sits in front of.
        if (assignment.Value.ConstantValue is { HasValue: true, Value: string id } &&
            id.StartsWith(ReservedPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MicroFxDiagnostics.ReservedFeaturePrefix,
                assignment.Syntax.GetLocation(),
                id));
        }
    }

    private static bool IsInsideFeatureConfigure(ISymbol? containingSymbol, WellKnownTypes types)
    {
        if (types.Feature is null ||
            containingSymbol is not IMethodSymbol { Name: "Configure" } method ||
            method.ContainingType is not { } declaringType)
        {
            return false;
        }

        return ImplementsOrIs(declaringType, types.Feature);
    }

    private static bool ImplementsOrIs(ITypeSymbol type, INamedTypeSymbol target) =>
        SymbolEqualityComparer.Default.Equals(type, target) ||
        type.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i, target) ||
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target));

    /// <summary>Symbols resolved once per compilation rather than per operation.</summary>
    private sealed class WellKnownTypes
    {
        private WellKnownTypes(Compilation compilation)
        {
            Feature = compilation.GetTypeByMetadataName(FeatureInterface);
            DomainEvent = compilation.GetTypeByMetadataName(DomainEventInterface);
            EventPublisher = compilation.GetTypeByMetadataName(EventPublisherInterface);
            FeatureDescriptor = compilation.GetTypeByMetadataName("MicroFx.Features.FeatureDescriptor");
            HttpClient = compilation.GetTypeByMetadataName("System.Net.Http.HttpClient");
            DateTime = compilation.GetTypeByMetadataName("System.DateTime");
            DateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
        }

        public INamedTypeSymbol? Feature { get; }

        public INamedTypeSymbol? DomainEvent { get; }

        public INamedTypeSymbol? EventPublisher { get; }

        public INamedTypeSymbol? FeatureDescriptor { get; }

        public INamedTypeSymbol? HttpClient { get; }

        public INamedTypeSymbol? DateTime { get; }

        public INamedTypeSymbol? DateTimeOffset { get; }

        public bool ReferencesMicroFx => Feature is not null;

        public static WellKnownTypes From(Compilation compilation) => new(compilation);
    }
}
