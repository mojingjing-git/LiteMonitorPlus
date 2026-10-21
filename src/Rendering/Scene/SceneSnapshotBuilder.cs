using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Scene;

public sealed record SceneSourceMetric(
    string StableId,
    string Label,
    string DisplayValue,
    string Unit,
    double? NormalizedValue,
    MetricStatus Status,
    bool IsMissing,
    ImmutableArray<double> Trend)
{
    public MetricRowPresentationStyle PresentationStyle { get; init; } =
        MetricRowPresentationStyle.StandardBar;
}

public sealed record SceneSourceGroup(
    string StableId,
    string Title,
    LogicalRect Bounds,
    IReadOnlyList<SceneSourceMetric> Metrics);

public static class SceneSnapshotBuilder
{
    public static SceneSnapshot Build(
        RenderGenerations generations,
        double physicalWidth,
        double physicalHeight,
        int dpi,
        IEnumerable<SceneSourceGroup> sourceGroups,
        IEnumerable<SceneNotice>? notices = null)
    {
        ArgumentNullException.ThrowIfNull(sourceGroups);
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }

        if (!double.IsFinite(physicalWidth) || physicalWidth < 0 ||
            !double.IsFinite(physicalHeight) || physicalHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalWidth), "Physical dimensions must be finite and non-negative.");
        }

        double dpiScale = dpi / 96.0;
        SceneSourceGroup[] ordered = sourceGroups
            .Where(group => group is not null)
            .OrderBy(group => group.Bounds.Y)
            .ThenBy(group => group.Bounds.X)
            .ThenBy(group => group.StableId, StringComparer.Ordinal)
            .ToArray();
        HashSet<string> groupIds = new(StringComparer.Ordinal);

        ImmutableArray<SceneGroup>.Builder groups = ImmutableArray.CreateBuilder<SceneGroup>(ordered.Length);
        for (int groupIndex = 0; groupIndex < ordered.Length; groupIndex++)
        {
            SceneSourceGroup source = ordered[groupIndex];
            if (string.IsNullOrWhiteSpace(source.StableId))
            {
                throw new ArgumentException("Every scene group requires a stable ID.", nameof(sourceGroups));
            }

            if (!groupIds.Add(source.StableId))
            {
                throw new ArgumentException($"Duplicate scene group ID '{source.StableId}'.", nameof(sourceGroups));
            }

            if (!source.Bounds.IsValid || source.Bounds.Width <= 0 || source.Bounds.Height <= 0)
            {
                throw new ArgumentException(
                    $"Scene group '{source.StableId}' has invalid bounds.",
                    nameof(sourceGroups));
            }

            HashSet<string> metricIds = new(StringComparer.Ordinal);
            ImmutableArray<SceneMetric> metrics = source.Metrics
                .Where(metric => metric is not null && !string.IsNullOrWhiteSpace(metric.StableId))
                .Select(metric =>
                {
                    if (!metricIds.Add(metric.StableId))
                    {
                        throw new ArgumentException(
                            $"Duplicate metric ID '{metric.StableId}' in group '{source.StableId}'.",
                            nameof(sourceGroups));
                    }

                    return new SceneMetric(
                        metric.StableId,
                        metric.Label ?? string.Empty,
                        metric.DisplayValue ?? string.Empty,
                        metric.Unit ?? string.Empty,
                        NormalizeValue(metric.NormalizedValue),
                        metric.Status,
                        metric.IsMissing,
                        metric.Trend.IsDefault ? ImmutableArray<double>.Empty : metric.Trend)
                    {
                        PresentationStyle = metric.PresentationStyle
                    };
                })
                .ToImmutableArray();

            LogicalRect bounds = source.Bounds;
            LogicalRect logicalBounds = new(
                bounds.X / dpiScale,
                bounds.Y / dpiScale,
                bounds.Width / dpiScale,
                bounds.Height / dpiScale);
            groups.Add(new SceneGroup(
                source.StableId,
                source.Title ?? string.Empty,
                metrics,
                logicalBounds,
                groupIndex));
        }

        SceneSnapshot snapshot = new(
            RenderingContractVersion.Core,
            generations,
            physicalWidth / dpiScale,
            physicalHeight / dpiScale,
            dpi,
            groups.MoveToImmutable(),
            notices?.ToImmutableArray() ?? ImmutableArray<SceneNotice>.Empty);

        if (!snapshot.IsValid)
        {
            throw new ArgumentException("The constructed scene snapshot does not satisfy the frozen contract.", nameof(sourceGroups));
        }

        return snapshot;
    }

    private static double? NormalizeValue(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0.0, 1.0);
    }
}
