using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace MicroFx.Core;

/// <summary>
/// Resolves the assembly that <em>is</em> the application, for conventions that scan it.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="Assembly.GetEntryAssembly"/>. Under a test host, a benchmark harness, or any
/// launcher that starts the application in-process, the entry assembly is the launcher — so
/// entry-assembly scanning silently finds nothing and the service comes up missing its endpoints
/// with no error to explain it.
/// </para>
/// <para>
/// <see cref="IHostEnvironment.ApplicationName"/> is the value ASP.NET Core itself uses for the same
/// purpose, and test hosts set it deliberately to the application under test.
/// </para>
/// </remarks>
internal static class ApplicationAssembly
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Loads only the application's own assembly, already present in the deployment.")]
    public static Assembly? Resolve(IHostEnvironment environment)
    {
        var name = environment.ApplicationName;

        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                return Assembly.Load(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException
                                          or BadImageFormatException)
            {
                // Falls through to the entry assembly below.
            }
        }

        return Assembly.GetEntryAssembly();
    }

    /// <summary>Public exported types, or an empty set when the assembly cannot be reflected over.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Convention-based discovery over the application's own assembly.")]
    public static IEnumerable<Type> ExportedTypes(Assembly? assembly)
    {
        if (assembly is null)
        {
            return [];
        }

        try
        {
            return assembly.GetExportedTypes();
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException)
        {
            return [];
        }
    }
}
