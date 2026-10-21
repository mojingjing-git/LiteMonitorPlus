using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Gpu.Foreground;

public enum ForegroundCommandKind
{
    Text,
    Rule,
    ProgressTrack,
    ProgressFill,
    StatusMark
}

public enum ForegroundTextRole
{
    Display,
    Title,
    Body,
    Caption
}

public enum ForegroundTone
{
    Primary,
    Muted,
    Success,
    Warning,
    Danger,
    Accent
}

public readonly record struct PixelRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;
}

public sealed record ForegroundCommand(
    ForegroundCommandKind Kind,
    string StableId,
    PixelRect Bounds,
    string Text,
    ForegroundTextRole TextRole,
    ForegroundTone Tone,
    float StrokeWidth,
    float Progress);

public sealed record ForegroundDrawList(
    int Dpi,
    float DpiScale,
    PixelRect ContentBounds,
    ImmutableArray<ForegroundCommand> Commands)
{
    public bool IsValid =>
        Dpi > 0 &&
        float.IsFinite(DpiScale) &&
        DpiScale > 0 &&
        ContentBounds.Width >= 0 &&
        ContentBounds.Height >= 0;
}

public static class ForegroundDrawListBuilder
{
    private const float GroupInset = 12;
    private const float TitleHeight = 22;
    private const float ProgressHeight = 3;

    public static ForegroundDrawList Build(
        SceneSnapshot scene,
        bool horizontalLayout = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!scene.IsValid)
        {
            throw new ArgumentException("Scene snapshot does not satisfy the frozen contract.", nameof(scene));
        }

        float scale = scene.Dpi / 96.0f;
        ImmutableArray<ForegroundCommand>.Builder commands =
            ImmutableArray.CreateBuilder<ForegroundCommand>();
        if (horizontalLayout)
        {
            foreach (SceneGroup group in scene.Groups.OrderBy(group => group.Order))
            {
                AppendHorizontalGroup(commands, group, scale);
            }
        }
        else
        {
            foreach (SceneGroup group in scene.Groups.OrderBy(group => group.Order))
            {
                AppendGroup(commands, group, scale);
            }
        }

        if (scene.Notices.Length > 0)
        {
            float y = ToPixel(
                scene.LogicalHeight - 28 - scene.Notices.Length * 18,
                0,
                (float)scene.LogicalHeight,
                scale);
            foreach (SceneNotice notice in scene.Notices)
            {
                commands.Add(new ForegroundCommand(
                    ForegroundCommandKind.Text,
                    $"notice:{notice.StableId}",
                    new PixelRect(12 * scale, y, Math.Max(0, (float)scene.LogicalWidth * scale - 24 * scale), 18 * scale),
                    notice.Text,
                    ForegroundTextRole.Caption,
                    ToneForStatus(notice.Severity),
                    StrokeWidth: 0,
                    Progress: 0));
                y += 18 * scale;
            }
        }

        return new ForegroundDrawList(
            scene.Dpi,
            scale,
            new PixelRect(
                0,
                0,
                (float)scene.LogicalWidth * scale,
                (float)scene.LogicalHeight * scale),
            commands.ToImmutable());
    }

    private static void AppendGroup(
        ImmutableArray<ForegroundCommand>.Builder commands,
        SceneGroup group,
        float scale)
    {
        float x = ToPixel(group.Bounds.X, 0, int.MaxValue, scale);
        float y = ToPixel(group.Bounds.Y, 0, int.MaxValue, scale);
        float width = Math.Max(0, ToPixel(group.Bounds.Width, 0, int.MaxValue, scale));
        float height = Math.Max(0, ToPixel(group.Bounds.Height, 0, int.MaxValue, scale));
        float contentLeft = x + GroupInset * scale;
        float contentWidth = Math.Max(0, width - GroupInset * 2 * scale);

        float titleHeight = Math.Min(TitleHeight * scale, Math.Max(14 * scale, height * 0.22f));
        commands.Add(new ForegroundCommand(
            ForegroundCommandKind.Text,
            $"{group.StableId}:title",
            new PixelRect(contentLeft, y + 7 * scale, contentWidth, titleHeight),
            group.Title,
            ForegroundTextRole.Title,
            ForegroundTone.Primary,
            StrokeWidth: 0,
            Progress: 0));
        float ruleY = y + Math.Min(31 * scale, titleHeight + 10 * scale);
        commands.Add(new ForegroundCommand(
            ForegroundCommandKind.Rule,
            $"{group.StableId}:rule",
            new PixelRect(contentLeft, ruleY, contentWidth, 1),
            string.Empty,
            ForegroundTextRole.Caption,
            ForegroundTone.Muted,
            StrokeWidth: 1,
            Progress: 0));

        float rowTop = ruleY + 5 * scale;
        float remainingHeight = Math.Max(0, y + height - rowTop - 2 * scale);
        if (group.Metrics.Length == 0)
        {
            return;
        }

        List<int[]> rows = BuildRows(group.Metrics);
        float rowHeight = remainingHeight / Math.Max(1, rows.Count);
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            int[] columns = rows[rowIndex];
            bool twoColumn = columns.All(index =>
                group.Metrics[index].PresentationStyle ==
                MetricRowPresentationStyle.TwoColumn);
            int columnCount = twoColumn ? 2 : 1;
            float columnWidth = contentWidth / columnCount;
            float metricY = rowTop + rowIndex * rowHeight;
            for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
            {
                SceneMetric metric = group.Metrics[columns[columnIndex]];
                AppendMetricColumn(
                    commands,
                    metric,
                    contentLeft + columnWidth * columnIndex,
                    metricY,
                    columnWidth,
                    rowHeight,
                    scale);
            }
        }
    }

    private static void AppendHorizontalGroup(
        ImmutableArray<ForegroundCommand>.Builder commands,
        SceneGroup group,
        float scale)
    {
        float x = ToPixel(group.Bounds.X, 0, int.MaxValue, scale);
        float y = ToPixel(group.Bounds.Y, 0, int.MaxValue, scale);
        float width = Math.Max(0, ToPixel(group.Bounds.Width, 0, int.MaxValue, scale));
        float height = Math.Max(0, ToPixel(group.Bounds.Height, 0, int.MaxValue, scale));
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (group.Metrics.Length == 0)
        {
            return;
        }

        float slotHeight = height / group.Metrics.Length;
        for (int index = 0; index < group.Metrics.Length; index++)
        {
            SceneMetric metric = group.Metrics[index];
            float slotY = y + slotHeight * index;
            float contentHeight = Math.Max(12 * scale, slotHeight - 4 * scale);
            string display = metric.IsMissing
                ? "--"
                : string.IsNullOrEmpty(metric.Unit)
                    ? metric.DisplayValue
                    : $"{metric.DisplayValue} {metric.Unit}";
            ForegroundTone tone = ToneForStatus(metric.Status);

            commands.Add(new ForegroundCommand(
                ForegroundCommandKind.StatusMark,
                $"{metric.StableId}:status",
                new PixelRect(x, slotY + 5 * scale, 4 * scale, 4 * scale),
                string.Empty,
                ForegroundTextRole.Caption,
                tone,
                StrokeWidth: 0,
                Progress: 0));
            commands.Add(new ForegroundCommand(
                ForegroundCommandKind.Text,
                $"{metric.StableId}:label",
                new PixelRect(
                    x + 8 * scale,
                    slotY,
                    width * 0.43f,
                    contentHeight),
                metric.Label,
                ForegroundTextRole.Caption,
                ForegroundTone.Muted,
                StrokeWidth: 0,
                Progress: 0));
            commands.Add(new ForegroundCommand(
                ForegroundCommandKind.Text,
                $"{metric.StableId}:value",
                new PixelRect(
                    x + width * 0.43f,
                    slotY - 1 * scale,
                    width * 0.57f,
                    contentHeight),
                display,
                ForegroundTextRole.Body,
                tone,
                StrokeWidth: 0,
                Progress: 0));

            if (slotHeight >= 26 * scale)
            {
                float normalized = (float)(metric.NormalizedValue ?? 0);
                commands.Add(new ForegroundCommand(
                    ForegroundCommandKind.ProgressTrack,
                    $"{metric.StableId}:bar-track",
                    new PixelRect(
                        x + 8 * scale,
                        slotY + slotHeight - 5 * scale,
                        Math.Max(0, width - 12 * scale),
                        2 * scale),
                    string.Empty,
                    ForegroundTextRole.Caption,
                    ForegroundTone.Muted,
                    StrokeWidth: 0,
                    Progress: 0));
                commands.Add(new ForegroundCommand(
                    ForegroundCommandKind.ProgressFill,
                    $"{metric.StableId}:bar-fill",
                    new PixelRect(
                        x + 8 * scale,
                        slotY + slotHeight - 5 * scale,
                        Math.Max(0, width - 12 * scale),
                        2 * scale),
                    string.Empty,
                    ForegroundTextRole.Caption,
                    tone,
                    StrokeWidth: 0,
                    Progress: Math.Clamp(normalized, 0, 1)));
            }
        }
    }

    private static void AppendMetricColumn(
        ImmutableArray<ForegroundCommand>.Builder commands,
        SceneMetric metric,
        float metricX,
        float metricY,
        float columnWidth,
        float rowHeight,
        float scale)
    {
        ForegroundTone tone = ToneForStatus(metric.Status);
        string display = metric.IsMissing
            ? "--"
            : string.IsNullOrEmpty(metric.Unit)
                ? metric.DisplayValue
                : $"{metric.DisplayValue} {metric.Unit}";
        bool compact = metric.PresentationStyle == MetricRowPresentationStyle.TwoColumn;
        float labelWidth = compact
            ? Math.Max(0, columnWidth - 8 * scale)
            : Math.Max(0, columnWidth * 0.43f - 9 * scale);
        float valueX = compact
            ? metricX + 8 * scale
            : metricX + columnWidth * 0.43f;
        float labelY = metricY;
        float valueY = compact
            ? metricY + Math.Max(10 * scale, rowHeight * 0.48f)
            : metricY - 1 * scale;

        commands.Add(new ForegroundCommand(
            ForegroundCommandKind.StatusMark,
            $"{metric.StableId}:status",
            new PixelRect(
                metricX + 1 * scale,
                metricY + Math.Min(5 * scale, Math.Max(1, rowHeight) * 0.18f),
                Math.Min(5 * scale, Math.Max(1, rowHeight) * 0.15f),
                Math.Min(5 * scale, Math.Max(1, rowHeight) * 0.15f)),
            string.Empty,
            ForegroundTextRole.Caption,
            tone,
            StrokeWidth: 0,
            Progress: 0));
        commands.Add(new ForegroundCommand(
            ForegroundCommandKind.Text,
            $"{metric.StableId}:label",
            new PixelRect(
                metricX + 9 * scale,
                labelY,
                labelWidth,
                Math.Max(1, Math.Min(17 * scale, rowHeight * (compact ? 0.44f : 0.52f)))),
            metric.Label,
            ForegroundTextRole.Caption,
            ForegroundTone.Muted,
            StrokeWidth: 0,
            Progress: 0));
        commands.Add(new ForegroundCommand(
            ForegroundCommandKind.Text,
            $"{metric.StableId}:value",
            new PixelRect(
                valueX,
                valueY,
                compact
                    ? Math.Max(0, columnWidth - 8 * scale)
                    : columnWidth * 0.57f,
                Math.Max(1, Math.Min(20 * scale, rowHeight * (compact ? 0.44f : 0.62f)))),
            display,
            ForegroundTextRole.Body,
            tone,
            StrokeWidth: 0,
            Progress: 0));

        if (metric.PresentationStyle != MetricRowPresentationStyle.StandardBar ||
            rowHeight < 25 * scale)
        {
            return;
        }

        float normalized = (float)(metric.NormalizedValue ?? 0);
        float barY = metricY + rowHeight - 5 * scale;
        commands.Add(new ForegroundCommand(
            ForegroundCommandKind.ProgressTrack,
            $"{metric.StableId}:bar-track",
            new PixelRect(
                metricX,
                barY,
                Math.Max(0, columnWidth - 2 * scale),
                ProgressHeight * scale),
            string.Empty,
            ForegroundTextRole.Caption,
            ForegroundTone.Muted,
            StrokeWidth: 0,
            Progress: 0));
        commands.Add(new ForegroundCommand(
            ForegroundCommandKind.ProgressFill,
            $"{metric.StableId}:bar-fill",
            new PixelRect(
                metricX,
                barY,
                Math.Max(0, columnWidth - 2 * scale),
                ProgressHeight * scale),
            string.Empty,
            ForegroundTextRole.Caption,
            tone,
            StrokeWidth: 0,
            Progress: Math.Clamp(normalized, 0, 1)));
    }

    private static List<int[]> BuildRows(ImmutableArray<SceneMetric> metrics)
    {
        List<int[]> rows = new(metrics.Length);
        for (int index = 0; index < metrics.Length; index++)
        {
            SceneMetric metric = metrics[index];
            if (metric.PresentationStyle == MetricRowPresentationStyle.TwoColumn &&
                index + 1 < metrics.Length &&
                metrics[index + 1].PresentationStyle == MetricRowPresentationStyle.TwoColumn)
            {
                rows.Add([index, index + 1]);
                index++;
            }
            else
            {
                rows.Add([index]);
            }
        }

        return rows;
    }

    private static ForegroundTone ToneForStatus(MetricStatus status) =>
        status switch
        {
            MetricStatus.Warning => ForegroundTone.Warning,
            MetricStatus.Danger => ForegroundTone.Danger,
            MetricStatus.Missing => ForegroundTone.Muted,
            _ => ForegroundTone.Success
        };

    private static float ToPixel(double logical, double origin, double limit, float scale) =>
        (float)Math.Clamp(logical, origin, limit) * scale;
}
