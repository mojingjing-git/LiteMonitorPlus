using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Foreground;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class ForegroundScaleTests
{
    [Theory]
    [InlineData(96, 1.0f, 1.0f)]
    [InlineData(144, 1.25f, 1.875f)]
    [InlineData(168, 1.5f, 2.625f)]
    [InlineData(192, 2.0f, 4.0f)]
    public void FontScaleUsesDpiAndUserScale(
        int dpi,
        float userScale,
        float expected)
    {
        Assert.Equal(
            expected,
            Direct2DForegroundPainter.ResolveFontScale(dpi, userScale),
            precision: 5);
    }

    [Fact]
    public void SameDpiAndUserScaleIsIndependentOfGroupCountAndOrientation()
    {
        SceneSnapshot oneGroup = Scene(1);
        SceneSnapshot fourGroups = Scene(4);

        ForegroundDrawList one = ForegroundDrawListBuilder.Build(oneGroup);
        ForegroundDrawList four = ForegroundDrawListBuilder.Build(fourGroups);
        ForegroundDrawList horizontal =
            ForegroundDrawListBuilder.Build(fourGroups, horizontalLayout: true);
        float expected =
            Direct2DForegroundPainter.ResolveFontScale(fourGroups.Dpi, 1.25f);

        Assert.Equal(expected, one.DpiScale * 1.25f);
        Assert.Equal(expected, four.DpiScale * 1.25f);
        Assert.Equal(expected, horizontal.DpiScale * 1.25f);
        Assert.Equal(1.25f, expected, precision: 2);
    }

    private static SceneSnapshot Scene(int groupCount)
    {
        SceneGroup[] groups = Enumerable.Range(0, groupCount)
            .Select(index => new SceneGroup(
                $"group-{index}",
                $"Group {index}",
                [
                    new SceneMetric(
                        $"metric-{index}",
                        "CPU",
                        "50",
                        "%",
                        0.5,
                        MetricStatus.Normal,
                        false,
                        [])
                ],
                new LogicalRect(10, 10 + index * 120, 320, 96),
                index))
            .ToArray();
        return new SceneSnapshot(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            360,
            550,
            96,
            groups.ToImmutableArray(),
            []);
    }
}
