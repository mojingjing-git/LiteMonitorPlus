using System.Collections.Immutable;
using LiteMonitor.Rendering.Contracts;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class RenderingContractTests
{
    [Fact]
    public void ContractVersionsAreFrozenAtOne()
    {
        Assert.Equal(1, RenderingContractVersion.Core);
        Assert.Equal(1, RenderingContractVersion.Capture);
        Assert.Equal("litemonitor.gpu-render/1", RenderingContractVersion.SchemaId);
    }

    [Fact]
    public void GenerationsRequireEveryFieldToMatch()
    {
        RenderGenerations expected = new(1, 2, 3, 4, 5);
        RenderGenerations same = new(1, 2, 3, 4, 5);
        RenderGenerations changedDevice = new(1, 2, 3, 4, 6);

        Assert.True(expected.CanAccept(same));
        Assert.False(expected.CanAccept(changedDevice));
    }

    [Fact]
    public void SceneSnapshotCanRepresentMissingAndNormalMetrics()
    {
        SceneSnapshot snapshot = new(
            RenderingContractVersion.Core,
            new RenderGenerations(1, 1, 1, 1, 1),
            1280,
            800,
            96,
            ImmutableArray.Create(
                new SceneGroup(
                    "gpu",
                    "GPU",
                    ImmutableArray.Create(
                        new SceneMetric("gpu.load", "GPU", "78%", "%", 0.78, MetricStatus.Normal, false, ImmutableArray<double>.Empty),
                        new SceneMetric("gpu.temp", "TEMP", "--", "C", null, MetricStatus.Missing, true, ImmutableArray<double>.Empty)),
                    new LogicalRect(0, 0, 640, 400),
                    0)),
            ImmutableArray<SceneNotice>.Empty);

        Assert.True(snapshot.IsValid);
        Assert.True(snapshot.Groups[0].Metrics[1].IsMissing);
    }

    [Fact]
    public void WindowExclusionIsAnExplicitCapturePolicy()
    {
        CaptureCapabilities capabilities = new(
            true,
            true,
            false,
            false,
            WindowCapturePolicy.WdaExcludeFromCapture,
            "desktop-duplication",
            "External capture omits the presenting window while policy is active.");

        Assert.True(capabilities.SupportsWindowExclusion);
        Assert.Equal(WindowCapturePolicy.WdaExcludeFromCapture, capabilities.ExclusionPolicy);
    }
}
