using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Scene;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class SceneSnapshotBuilderTests
{
    [Fact]
    public void PhysicalBoundsAreConvertedToLogicalUnitsExactlyOnce()
    {
        SceneSnapshot snapshot = SceneSnapshotBuilder.Build(
            new RenderGenerations(1, 2, 3, 4, 5),
            1920,
            1200,
            144,
            [
                new SceneSourceGroup(
                    "cpu",
                    "CPU",
                    new LogicalRect(180, 90, 600, 300),
                    [
                        new SceneSourceMetric(
                            "cpu.load",
                            "CPU",
                            "62",
                            "%",
                            0.62,
                            MetricStatus.Normal,
                            false,
                            ImmutableArray<double>.Empty)
                    ])
            ]);

        Assert.Equal(1280, snapshot.LogicalWidth);
        Assert.Equal(800, snapshot.LogicalHeight);
        Assert.Equal(120, snapshot.Groups[0].Bounds.X);
        Assert.Equal(60, snapshot.Groups[0].Bounds.Y);
        Assert.Equal(400, snapshot.Groups[0].Bounds.Width);
    }

    [Fact]
    public void SnapshotDoesNotRetainMutableSourceCollections()
    {
        List<SceneSourceMetric> metrics =
        [
            new SceneSourceMetric(
                "gpu.load",
                "GPU",
                "78",
                "%",
                0.78,
                MetricStatus.Warning,
                false,
                ImmutableArray.Create(0.4, 0.7))
        ];
        List<SceneSourceGroup> groups =
        [
            new SceneSourceGroup("gpu", "GPU", new LogicalRect(0, 0, 400, 200), metrics)
        ];

        SceneSnapshot snapshot = SceneSnapshotBuilder.Build(
            new RenderGenerations(1, 1, 1, 1, 1),
            400,
            200,
            96,
            groups);

        metrics.Clear();
        groups.Clear();

        Assert.Single(snapshot.Groups);
        Assert.Single(snapshot.Groups[0].Metrics);
        Assert.Equal(2, snapshot.Groups[0].Metrics[0].Trend.Length);
    }

    [Fact]
    public void ValuesOutsideProgressRangeAreClamped()
    {
        SceneSnapshot snapshot = SceneSnapshotBuilder.Build(
            new RenderGenerations(1, 1, 1, 1, 1),
            100,
            100,
            96,
            [
                new SceneSourceGroup(
                    "group",
                    "Group",
                    new LogicalRect(0, 0, 100, 100),
                    [
                        new SceneSourceMetric(
                            "high",
                            "High",
                            "120",
                            "%",
                            1.2,
                            MetricStatus.Danger,
                            false,
                            ImmutableArray<double>.Empty)
                    ])
            ]);

        Assert.Equal(1.0, snapshot.Groups[0].Metrics[0].NormalizedValue);
    }

    [Fact]
    public void DuplicateStableIdsFailClosed()
    {
        Assert.Throws<ArgumentException>(() =>
            SceneSnapshotBuilder.Build(
                new RenderGenerations(1, 1, 1, 1, 1),
                100,
                100,
                96,
                [
                    new SceneSourceGroup(
                        "group",
                        "First",
                        new LogicalRect(0, 0, 100, 50),
                        []),
                    new SceneSourceGroup(
                        "group",
                        "Second",
                        new LogicalRect(0, 50, 100, 50),
                        [])
                ]));
    }

    [Fact]
    public void DuplicateMetricIdsFailClosed()
    {
        Assert.Throws<ArgumentException>(() =>
            SceneSnapshotBuilder.Build(
                new RenderGenerations(1, 1, 1, 1, 1),
                100,
                100,
                96,
                [
                    new SceneSourceGroup(
                        "group",
                        "Group",
                        new LogicalRect(0, 0, 100, 100),
                        [
                            new SceneSourceMetric(
                                "duplicate",
                                "A",
                                "1",
                                string.Empty,
                                0.1,
                                MetricStatus.Normal,
                                false,
                                ImmutableArray<double>.Empty),
                            new SceneSourceMetric(
                                "duplicate",
                                "B",
                                "2",
                                string.Empty,
                                0.2,
                                MetricStatus.Normal,
                                false,
                                ImmutableArray<double>.Empty)
                        ])
                ]));
    }

    [Fact]
    public void HorizontalColumnsBecomeIndependentSceneGroups()
    {
        Column cpu = new()
        {
            Bounds = new Rectangle(10, 5, 180, 50),
            Top = new MetricItem
            {
                Key = "CPU.Load",
                Label = "CPU",
                DisplayValue = 62,
                Value = 62
            },
            Bottom = new MetricItem
            {
                Key = "CPU.Temp",
                Label = "TEMP",
                DisplayValue = 71,
                Value = 71
            }
        };
        Column gpu = new()
        {
            Bounds = new Rectangle(200, 5, 180, 50),
            Top = new MetricItem
            {
                Key = "GPU.Load",
                Label = "GPU",
                DisplayValue = 78,
                Value = 78
            }
        };

        SceneSnapshot snapshot = UiSceneSnapshotAdapter.BuildHorizontal(
            [cpu, gpu],
            new RenderGenerations(1, 1, 1, 1, 1),
            new Rectangle(0, 0, 400, 60),
            96);

        Assert.Equal(2, snapshot.Groups.Length);
        Assert.Equal(2, snapshot.Groups[0].Metrics.Length);
        Assert.Equal(1, snapshot.Groups[1].Metrics.Length);
        Assert.Equal(200, snapshot.Groups[1].Bounds.X);
        Assert.Equal(180, snapshot.Groups[1].Bounds.Width);
    }

    [Fact]
    public void SourceMetricStyleIsPreservedIndependentlyOfGroupName()
    {
        MetricItem standardInNetGroup = new()
        {
            Key = "NET.Down",
            Label = "DOWN",
            DisplayValue = 10,
            Value = 10,
            Style = MetricRenderStyle.StandardBar
        };
        MetricItem twoColumnInNeutralGroup = new()
        {
            Key = "HOST.Load",
            Label = "LOAD",
            DisplayValue = 20,
            Value = 20,
            Style = MetricRenderStyle.TwoColumn
        };

        SceneSnapshot snapshot = UiSceneSnapshotAdapter.Build(
            [
                new GroupLayoutInfo("NET", [standardInNetGroup])
                {
                    Label = "NET",
                    Bounds = new Rectangle(0, 0, 320, 96)
                },
                new GroupLayoutInfo("HOST", [twoColumnInNeutralGroup])
                {
                    Label = "HOST",
                    Bounds = new Rectangle(0, 100, 320, 96)
                }
            ],
            new RenderGenerations(1, 1, 1, 1, 1),
            new Rectangle(0, 0, 360, 220),
            96);

        Assert.Equal(
            MetricRowPresentationStyle.StandardBar,
            snapshot.Groups[0].Metrics[0].PresentationStyle);
        Assert.Equal(
            MetricRowPresentationStyle.TwoColumn,
            snapshot.Groups[1].Metrics[0].PresentationStyle);
    }
}
