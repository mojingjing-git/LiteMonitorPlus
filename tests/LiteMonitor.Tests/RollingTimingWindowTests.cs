using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class RollingTimingWindowTests
{
    [Fact]
    public void PercentilesUseNearestRankOverSortedSamples()
    {
        RollingTimingWindow window = new(100);
        foreach (double sample in Enumerable.Range(1, 100).Reverse())
        {
            window.Add(sample);
        }

        TimingPercentiles snapshot = window.Snapshot();

        Assert.Equal(100, snapshot.SampleCount);
        Assert.Equal(50, snapshot.P50Milliseconds);
        Assert.Equal(95, snapshot.P95Milliseconds);
        Assert.Equal(99, snapshot.P99Milliseconds);
        Assert.Equal(100, snapshot.MaxMilliseconds);
    }

    [Fact]
    public void WindowIsBoundedAndRejectsInvalidSamples()
    {
        RollingTimingWindow window = new(3);
        window.Add(10);
        window.Add(20);
        window.Add(double.NaN);
        window.Add(-1);
        window.Add(30);
        window.Add(40);

        TimingPercentiles snapshot = window.Snapshot();

        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(30, snapshot.P50Milliseconds);
        Assert.Equal(40, snapshot.P99Milliseconds);
    }
}
