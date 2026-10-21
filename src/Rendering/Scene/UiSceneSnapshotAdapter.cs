using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.src.Core;

namespace LiteMonitor.Rendering.Scene;

public static class UiSceneSnapshotAdapter
{
    public static SceneSnapshot Build(
        IReadOnlyList<GroupLayoutInfo> groups,
        RenderGenerations generations,
        Rectangle clientBounds,
        int dpi,
        bool isHorizontal = false,
        IReadOnlyList<SceneNotice>? notices = null)
    {
        ArgumentNullException.ThrowIfNull(groups);

        List<SceneSourceGroup> sourceGroups = new(groups.Count);
        foreach (GroupLayoutInfo group in groups)
        {
            List<SceneSourceMetric> metrics = new(group.Items?.Count ?? 0);
            if (group.Items is not null)
            {
                foreach (MetricItem item in group.Items)
                {
                    if (item is null || string.IsNullOrWhiteSpace(item.Key))
                    {
                        continue;
                    }

                    metrics.Add(ToSourceMetric(item, isHorizontal));
                }
            }

            Rectangle bounds = group.Bounds;
            sourceGroups.Add(new SceneSourceGroup(
                group.GroupName,
                group.Label,
                new LogicalRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                metrics));
        }

        return SceneSnapshotBuilder.Build(
            generations,
            clientBounds.Width,
            clientBounds.Height,
            dpi,
            sourceGroups,
            notices);
    }

    public static SceneSnapshot BuildHorizontal(
        IReadOnlyList<Column> columns,
        RenderGenerations generations,
        Rectangle clientBounds,
        int dpi,
        IReadOnlyList<SceneNotice>? notices = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        List<SceneSourceGroup> groups = new(columns.Count);
        for (int index = 0; index < columns.Count; index++)
        {
            Column column = columns[index];
            List<SceneSourceMetric> metrics = new(2);
            if (column.Top is not null &&
                !string.IsNullOrWhiteSpace(column.Top.Key))
            {
                metrics.Add(ToSourceMetric(column.Top, isHorizontal: true));
            }

            if (column.Bottom is not null &&
                !string.IsNullOrWhiteSpace(column.Bottom.Key))
            {
                metrics.Add(ToSourceMetric(column.Bottom, isHorizontal: true));
            }

            Rectangle bounds = column.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0 || metrics.Count == 0)
            {
                continue;
            }

            string stableId =
                $"hx:{index}:{column.Top?.Key ?? column.Bottom?.Key ?? "empty"}";
            string title =
                column.Top?.Label ??
                column.Bottom?.Label ??
                string.Empty;
            groups.Add(new SceneSourceGroup(
                stableId,
                title,
                new LogicalRect(
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height),
                metrics));
        }

        return SceneSnapshotBuilder.Build(
            generations,
            clientBounds.Width,
            clientBounds.Height,
            dpi,
            groups,
            notices);
    }

    private static SceneSourceMetric ToSourceMetric(
        MetricItem item,
        bool isHorizontal)
    {
        string formatted = item.GetFormattedText(isHorizontal) ?? string.Empty;
        string value = item.CachedValueText ?? string.Empty;
        string unit = item.CachedUnitText ?? string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            value = formatted;
        }

        bool missing = !item.Value.HasValue && string.IsNullOrEmpty(item.TextValue);
        return new SceneSourceMetric(
            item.Key,
            item.Label,
            missing ? "--" : value,
            missing ? string.Empty : unit,
            missing ? null : item.CachedPercent,
            MapStatus(item.CachedColorState, missing),
            missing,
            ImmutableArray<double>.Empty)
        {
            PresentationStyle = item.Style switch
            {
                MetricRenderStyle.TwoColumn => MetricRowPresentationStyle.TwoColumn,
                MetricRenderStyle.TextOnly => MetricRowPresentationStyle.TextOnly,
                _ => MetricRowPresentationStyle.StandardBar
            }
        };
    }

    private static MetricStatus MapStatus(int cachedState, bool missing)
    {
        if (missing)
        {
            return MetricStatus.Missing;
        }

        return cachedState switch
        {
            >= MetricUtils.STATE_CRIT => MetricStatus.Danger,
            MetricUtils.STATE_WARN => MetricStatus.Warning,
            _ => MetricStatus.Normal
        };
    }
}
