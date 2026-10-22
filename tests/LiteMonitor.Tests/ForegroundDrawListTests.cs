using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Foreground;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class ForegroundDrawListTests
{
    [Fact]
    public void SceneIsMappedToPhysicalPixelsExactlyOnce()
    {
        SceneSnapshot scene = Scene(
            dpi: 144,
            new SceneMetric(
                "cpu.load",
                "CPU",
                "62",
                "%",
                0.62,
                MetricStatus.Normal,
                false,
                ImmutableArray<double>.Empty));

        ForegroundDrawList list = ForegroundDrawListBuilder.Build(scene);
        ForegroundCommand value = Assert.Single(
            list.Commands.Where(command => command.StableId == "cpu.load:value"));
        ForegroundCommand bar = Assert.Single(
            list.Commands.Where(command => command.StableId == "cpu.load:bar-fill"));

        Assert.Equal(1.5f, list.DpiScale);
        Assert.Equal(1920, list.ContentBounds.Width);
        Assert.Equal(1080, list.ContentBounds.Height);
        Assert.True(value.Bounds.X > 0);
        Assert.Equal(0.62f, bar.Progress);
    }

    [Fact]
    public void MetricsRemainDistinctAcrossStatuses()
    {
        SceneSnapshot scene = Scene(
            dpi: 96,
            new SceneMetric(
                "warn",
                "Warn",
                "80",
                "%",
                0.8,
                MetricStatus.Warning,
                false,
                ImmutableArray<double>.Empty),
            new SceneMetric(
                "danger",
                "Danger",
                "95",
                "%",
                0.95,
                MetricStatus.Danger,
                false,
                ImmutableArray<double>.Empty),
            new SceneMetric(
                "missing",
                "Missing",
                "--",
                "",
                null,
                MetricStatus.Missing,
                true,
                ImmutableArray<double>.Empty));

        ForegroundDrawList list = ForegroundDrawListBuilder.Build(scene);

        Assert.Contains(list.Commands, command =>
            command.StableId == "warn:value" && command.Tone == ForegroundTone.Warning);
        Assert.Contains(list.Commands, command =>
            command.StableId == "danger:value" && command.Tone == ForegroundTone.Danger);
        Assert.Contains(list.Commands, command =>
            command.StableId == "missing:value" &&
            command.Text == "--" &&
            command.Tone == ForegroundTone.Muted);
    }

    [Fact]
    public void EverySelectedMetricProducesLabelAndValueEvenWhenRowsAreCompressed()
    {
        SceneSnapshot scene = Scene(
            dpi: 96,
            Enumerable.Range(0, 8)
                .Select(index => new SceneMetric(
                    $"metric{index}",
                    $"Metric {index}",
                    index.ToString(),
                    string.Empty,
                    index / 8.0,
                    MetricStatus.Normal,
                    false,
                    ImmutableArray<double>.Empty))
                .ToArray());

        ForegroundDrawList list = ForegroundDrawListBuilder.Build(scene);
        int valueCommands = list.Commands.Count(command =>
            command.Kind == ForegroundCommandKind.Text &&
            command.StableId.EndsWith(":value", StringComparison.Ordinal));

        Assert.Equal(8, valueCommands);
        foreach (int index in Enumerable.Range(0, 8))
        {
            Assert.Contains(list.Commands, command =>
                command.StableId == $"metric{index}:label");
            Assert.Contains(list.Commands, command =>
                command.StableId == $"metric{index}:value");
        }
    }

    [Theory]
    [InlineData(MetricRowPresentationStyle.StandardBar)]
    [InlineData(MetricRowPresentationStyle.TwoColumn)]
    [InlineData(MetricRowPresentationStyle.TextOnly)]
    public void CommonTwoMetricGroupAtLogicalHeight96KeepsBothMetrics(
        MetricRowPresentationStyle style)
    {
        SceneMetric first = Metric("first", style);
        SceneMetric second = Metric("second", style);
        SceneSnapshot scene = new(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            240,
            140,
            96,
            [
                new SceneGroup(
                    "layout-independent",
                    "Selected metrics",
                    [first, second],
                    new LogicalRect(0, 0, 220, 96),
                    0)
            ],
            ImmutableArray<SceneNotice>.Empty);

        ForegroundDrawList list = ForegroundDrawListBuilder.Build(scene);

        Assert.Contains(list.Commands, command => command.StableId == "first:label");
        Assert.Contains(list.Commands, command => command.StableId == "first:value");
        Assert.Contains(list.Commands, command => command.StableId == "second:label");
        Assert.Contains(list.Commands, command => command.StableId == "second:value");
        if (style == MetricRowPresentationStyle.TwoColumn)
        {
            Assert.NotEqual(
                list.Commands.Single(command => command.StableId == "first:value").Bounds.X,
                list.Commands.Single(command => command.StableId == "second:value").Bounds.X);
        }
    }

    [Fact]
    public void TwoColumnUsesStyleRatherThanGroupName()
    {
        SceneSnapshot namedNetButStandard = new(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            240,
            140,
            96,
            [
                new SceneGroup(
                    "NET",
                    "Network",
                    [Metric("one", MetricRowPresentationStyle.StandardBar),
                     Metric("two", MetricRowPresentationStyle.StandardBar)],
                    new LogicalRect(0, 0, 220, 140),
                    0)
            ],
            ImmutableArray<SceneNotice>.Empty);
        SceneSnapshot neutralNameWithStyle = namedNetButStandard with
        {
            Groups =
            [
                namedNetButStandard.Groups[0] with
                {
                    StableId = "group",
                    Metrics =
                    [
                        Metric("one", MetricRowPresentationStyle.TwoColumn),
                        Metric("two", MetricRowPresentationStyle.TwoColumn)
                    ]
                }
            ]
        };

        ForegroundCommand standardSecond = ForegroundDrawListBuilder
            .Build(namedNetButStandard).Commands
            .Single(command => command.StableId == "two:value");
        ForegroundCommand styledSecond = ForegroundDrawListBuilder
            .Build(neutralNameWithStyle).Commands
            .Single(command => command.StableId == "two:value");

        Assert.Equal(
            ForegroundDrawListBuilder.Build(namedNetButStandard).Commands
                .Single(command => command.StableId == "one:value").Bounds.X,
            standardSecond.Bounds.X);
        Assert.NotEqual(
            ForegroundDrawListBuilder.Build(neutralNameWithStyle).Commands
                .Single(command => command.StableId == "one:value").Bounds.X,
            styledSecond.Bounds.X);
    }

    [Fact]
    public void HorizontalLayoutMapsColumnsWithoutGroupHeaders()
    {
        SceneSnapshot scene = new(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            600,
            80,
            96,
            [
                new SceneGroup(
                    "hx:cpu",
                    "CPU",
                    [
                        new SceneMetric(
                            "cpu.load",
                            "CPU",
                            "62",
                            "%",
                            0.62,
                            MetricStatus.Normal,
                            false,
                            ImmutableArray<double>.Empty),
                        new SceneMetric(
                            "cpu.temp",
                            "TEMP",
                            "71",
                            "C",
                            0.71,
                            MetricStatus.Warning,
                            false,
                            ImmutableArray<double>.Empty)
                    ],
                    new LogicalRect(10, 8, 220, 64),
                    0),
                new SceneGroup(
                    "hx:gpu",
                    "GPU",
                    [
                        new SceneMetric(
                            "gpu.load",
                            "GPU",
                            "78",
                            "%",
                            0.78,
                            MetricStatus.Normal,
                            false,
                            ImmutableArray<double>.Empty)
                    ],
                    new LogicalRect(250, 8, 220, 64),
                    1)
            ],
            ImmutableArray<SceneNotice>.Empty);

        ForegroundDrawList list =
            ForegroundDrawListBuilder.Build(scene, horizontalLayout: true);

        Assert.DoesNotContain(list.Commands, command =>
            command.StableId.EndsWith(":title", StringComparison.Ordinal));
        ForegroundCommand cpu = Assert.Single(
            list.Commands.Where(command => command.StableId == "cpu.load:label"));
        ForegroundCommand gpu = Assert.Single(
            list.Commands.Where(command => command.StableId == "gpu.load:label"));
        Assert.Equal(18, cpu.Bounds.X);
        Assert.Equal(250 + 8, gpu.Bounds.X);
        Assert.Contains(list.Commands, command =>
            command.StableId == "cpu.temp:bar-fill");
    }

    private static SceneSnapshot Scene(int dpi, params SceneMetric[] metrics) =>
        new(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            1280,
            720,
            dpi,
            [
                new SceneGroup(
                    "group",
                    "System",
                    metrics.ToImmutableArray(),
                    new LogicalRect(20, 20, 320, 200),
                    0)
            ],
            ImmutableArray<SceneNotice>.Empty);

    private static SceneMetric Metric(
        string id,
        MetricRowPresentationStyle style) =>
        new(
            id,
            id.ToUpperInvariant(),
            "10",
            "%",
            0.1,
            MetricStatus.Normal,
            false,
            ImmutableArray<double>.Empty)
        {
            PresentationStyle = style
        };
}
