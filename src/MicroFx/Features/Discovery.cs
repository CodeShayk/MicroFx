using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyModel;

namespace MicroFx.Features;

/// <summary>
/// Marks an assembly as offering MicroFx features. Only assemblies carrying this attribute are
/// examined, so discovery costs a handful of metadata reads rather than a scan of every type.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class MicroFxFeatureAssemblyAttribute : Attribute;

/// <summary>
/// Declares a feature offered by this assembly. Adding a package reference is then sufficient to
/// make the feature available — no registration call.
/// </summary>
/// <param name="featureType">A type implementing <see cref="IMicroFxFeature"/> with a public parameterless constructor.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class MicroFxFeatureAttribute(Type featureType) : Attribute
{
    /// <summary>The declared feature type.</summary>
    public Type FeatureType { get; } = featureType;
}

/// <summary>
/// Finds features declared by opted-in assemblies.
/// </summary>
/// <remarks>
/// Deliberately narrow: only assemblies already loaded or listed in the dependency context, only
/// those carrying <see cref="MicroFxFeatureAssemblyAttribute"/>, and only the types they name. No
/// directory probing and no scanning of arbitrary paths — loading assemblies a process did not
/// already depend on is a code-execution surface, not a convenience.
/// </remarks>
internal static class AssemblyFeatureScanner
{
    public static IReadOnlyList<IMicroFxFeature> Scan(Action<string, Exception>? onError = null)
    {
        var features = new List<IMicroFxFeature>();
        var seen = new HashSet<Type>();

        foreach (var assembly in CandidateAssemblies())
        {
            if (assembly.GetCustomAttribute<MicroFxFeatureAssemblyAttribute>() is null)
            {
                continue;
            }

            foreach (var declaration in assembly.GetCustomAttributes<MicroFxFeatureAttribute>())
            {
                var type = declaration.FeatureType;

                if (!seen.Add(type))
                {
                    continue;
                }

                if (!typeof(IMicroFxFeature).IsAssignableFrom(type))
                {
                    onError?.Invoke(
                        $"[MicroFxFeature] in '{assembly.GetName().Name}' names '{type.FullName}', " +
                        "which does not implement IMicroFxFeature.",
                        new InvalidOperationException(type.FullName));
                    continue;
                }

                try
                {
                    features.Add((IMicroFxFeature)Activator.CreateInstance(type)!);
                }
                catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException)
                {
                    onError?.Invoke(
                        $"Feature '{type.FullName}' from '{assembly.GetName().Name}' could not be created. " +
                        "A discovered feature needs a public parameterless constructor.",
                        ex);
                }
            }
        }

        return features;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Only assemblies already in the dependency context are loaded; feature types are " +
                        "rooted by the [MicroFxFeature] attribute reference.")]
    private static IEnumerable<Assembly> CandidateAssemblies()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies();
        var seen = new HashSet<string>(
            loaded.Select(a => a.GetName().Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in loaded)
        {
            yield return assembly;
        }

        // Referenced-but-not-yet-loaded assemblies: a feature-only package may not have been
        // touched by any code path yet. Restricted to the dependency context, so nothing outside
        // the application's declared dependency closure is ever loaded.
        var context = DependencyContext.Default;
        if (context is null)
        {
            yield break;
        }

        foreach (var library in context.RuntimeLibraries)
        {
            if (!seen.Add(library.Name))
            {
                continue;
            }

            Assembly? assembly = null;
            try
            {
                assembly = Assembly.Load(new AssemblyName(library.Name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException
                                          or FileLoadException)
            {
                // Native or unresolvable library: not a feature source.
            }

            if (assembly is not null)
            {
                yield return assembly;
            }
        }
    }
}
