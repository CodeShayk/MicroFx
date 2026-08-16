using System.Diagnostics.Metrics;

namespace MicroFx.Hosting;

/// <summary>
/// Metrics about the platform itself. Startup duration is tagged per feature and phase, so a slow
/// cold start is attributable to a name rather than being a mystery.
/// </summary>
internal static class MicroFxMetrics
{
    /// <summary>Meter name, registered with the observability feature.</summary>
    public const string MeterName = "MicroFx";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Histogram<double> FeatureStartupDuration =
        Meter.CreateHistogram<double>(
            "microfx.feature.startup.duration",
            unit: "s",
            description: "Duration of a feature's lifecycle phase.");

    private static readonly UpDownCounter<int> FeaturesEnabled =
        Meter.CreateUpDownCounter<int>(
            "microfx.feature.enabled",
            description: "Number of enabled MicroFx features.");

    public static void RecordFeatureStartup(string featureId, string phase, TimeSpan elapsed) =>
        FeatureStartupDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("feature", featureId),
            new KeyValuePair<string, object?>("phase", phase));

    public static void RecordEnabledCount(int count) => FeaturesEnabled.Add(count);
}
