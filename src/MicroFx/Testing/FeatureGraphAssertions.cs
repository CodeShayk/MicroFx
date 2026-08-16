using System.Text;
using MicroFx.Features;

namespace MicroFx.Testing;

/// <summary>
/// Assertions over a resolved feature graph.
/// </summary>
/// <remarks>
/// Composition ordering is load-bearing and easy to change by accident: adding one
/// <see cref="FeatureDescriptor.After"/> edge can move authentication after tenancy and nothing
/// fails until a request arrives. These turn the graph into something a test can pin down.
/// </remarks>
public static class FeatureGraphAssertions
{
    /// <summary>Thrown when an assertion about the graph does not hold.</summary>
    public sealed class FeatureGraphAssertionException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public FeatureGraphAssertionException(string message) : base(message)
        {
        }

        /// <summary>Creates the exception.</summary>
        public FeatureGraphAssertionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>Creates the exception.</summary>
        public FeatureGraphAssertionException()
        {
        }
    }

    /// <summary>Asserts that the named features resolve in the given relative order.</summary>
    public static void AssertOrder(this IFeatureCatalog catalog, params string[] featureIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(featureIds);

        var order = catalog.Enabled.Select(e => e.Id).ToList();
        var previous = -1;

        foreach (var id in featureIds)
        {
            var position = order.IndexOf(id);

            if (position < 0)
            {
                throw new FeatureGraphAssertionException(
                    $"Feature '{id}' is not enabled. Resolved order: {Describe(order)}");
            }

            if (position < previous)
            {
                throw new FeatureGraphAssertionException(
                    $"Feature '{id}' resolved before a feature it should follow. " +
                    $"Resolved order: {Describe(order)}");
            }

            previous = position;
        }
    }

    /// <summary>Asserts that a feature is enabled.</summary>
    public static void AssertEnabled(this IFeatureCatalog catalog, string featureId)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.IsEnabled(featureId))
        {
            return;
        }

        var entry = catalog[featureId];

        throw new FeatureGraphAssertionException(
            entry is null
                ? $"Feature '{featureId}' is not registered at all."
                : $"Feature '{featureId}' is registered but disabled: {entry.Reason} " +
                  $"({entry.ReasonDetail ?? "no detail"}).");
    }

    /// <summary>Asserts that a feature is disabled, optionally for a specific reason.</summary>
    public static void AssertDisabled(
        this IFeatureCatalog catalog, string featureId, DisabledReason? expectedReason = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var entry = catalog[featureId]
            ?? throw new FeatureGraphAssertionException($"Feature '{featureId}' is not registered.");

        if (entry.IsEnabled)
        {
            throw new FeatureGraphAssertionException($"Feature '{featureId}' is enabled.");
        }

        if (expectedReason is { } expected && entry.Reason != expected)
        {
            throw new FeatureGraphAssertionException(
                $"Feature '{featureId}' is disabled for {entry.Reason}, not {expected}.");
        }
    }

    /// <summary>Asserts that a feature reported a fact with the given value.</summary>
    /// <remarks>
    /// Facts are what the startup banner and <c>/internal/features</c> show, so pinning one keeps
    /// an operator-facing detail from drifting silently.
    /// </remarks>
    public static void AssertReports(
        this IFeatureCatalog catalog, string featureId, string key, object? expectedValue)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var entry = catalog[featureId]
            ?? throw new FeatureGraphAssertionException($"Feature '{featureId}' is not registered.");

        if (!entry.Facts.TryGetValue(key, out var actual))
        {
            throw new FeatureGraphAssertionException(
                $"Feature '{featureId}' reported no fact '{key}'. Reported: " +
                $"{string.Join(", ", entry.Facts.Keys)}");
        }

        if (!Equals(actual?.ToString(), expectedValue?.ToString()))
        {
            throw new FeatureGraphAssertionException(
                $"Feature '{featureId}' reported {key}='{actual}', expected '{expectedValue}'.");
        }
    }

    /// <summary>
    /// Returns the resolved order as a stable, diffable snapshot.
    /// </summary>
    /// <remarks>
    /// Compare this against a checked-in golden file. An accidental ordering change then shows up
    /// as a reviewed diff rather than as a production surprise.
    /// </remarks>
    public static string Snapshot(this IFeatureCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var builder = new StringBuilder();

        foreach (var entry in catalog.Enabled)
        {
            builder
                .Append(entry.ResolvedOrder.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ')
                .AppendLine(entry.Id);
        }

        foreach (var entry in catalog.All.Where(e => !e.IsEnabled)
                     .OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            builder.Append("--- ").Append(entry.Id).Append(" (").Append(entry.Reason).AppendLine(")");
        }

        return builder.ToString();
    }

    private static string Describe(IEnumerable<string> order) => string.Join(" → ", order);
}
