using System.Globalization;
using System.Text;
using MicroFx.Features;
using Microsoft.Extensions.Logging;

namespace MicroFx.Hosting;

/// <summary>
/// Writes the resolved feature set once at startup.
/// </summary>
/// <remarks>
/// The feature graph is operational data. A service that will not do what you expect should be able
/// to answer "what is actually enabled, and what turned the rest off?" from its own logs.
/// </remarks>
internal static partial class StartupBanner
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging",
        Justification = "The whole method is guarded by an IsEnabled check on its first line.")]
    public static void Write(ILogger logger, MicroFxComposition composition)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var catalog = composition.Catalog;
        var enabled = catalog.Enabled.Count;
        var replaced = catalog.All.Count(e => e.Reason == DisabledReason.Replaced);
        var disabled = catalog.All.Count - enabled - replaced;

        MicroFxMetrics.RecordEnabledCount(enabled);

        var banner = new StringBuilder();
        banner.Append(CultureInfo.InvariantCulture, $"MicroFx composed: {composition.Metadata.Name} ");
        banner.Append(CultureInfo.InvariantCulture, $"{composition.Metadata.Version} ");
        banner.Append(CultureInfo.InvariantCulture, $"[{composition.Metadata.Environment}] ");
        banner.Append(CultureInfo.InvariantCulture, $"host={composition.State.HostKind} ");
        banner.Append(CultureInfo.InvariantCulture, $"role={composition.Metadata.Role}");
        banner.AppendLine();
        banner.Append(CultureInfo.InvariantCulture,
            $"  {enabled} enabled, {disabled} disabled, {replaced} replaced");

        foreach (var entry in catalog.All.OrderBy(e => e.ResolvedOrder).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            banner.AppendLine();
            banner.Append("  ");
            banner.Append(entry.IsEnabled ? '+' : entry.Reason == DisabledReason.Replaced ? '~' : '-');
            banner.Append(' ');
            banner.Append(entry.Id.PadRight(28));

            if (entry.Descriptor.IsKernel)
            {
                banner.Append(" kernel");
            }

            if (!entry.IsEnabled && entry.ReasonDetail is { } detail)
            {
                banner.Append(CultureInfo.InvariantCulture, $" ({detail})");
            }

            // Facts are feature-reported and must never contain secrets — Report() documents that.
            if (entry.Facts.Count > 0)
            {
                banner.Append(" [");
                banner.AppendJoin(", ", entry.Facts.Select(f => $"{f.Key}={f.Value}"));
                banner.Append(']');
            }
        }

        LogBanner(logger, banner.ToString());
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "{Banner}")]
    private static partial void LogBanner(ILogger logger, string banner);
}
