using System;
using System.Linq;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core.Trends;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class ShortTermTrendBufferTests
{
    private static HardwareSnapshot Snapshot(long batchId, int sessionEpoch, params MetricSample[] samples)
        => new(batchId, 1, 1, samples, TimeSpan.Zero, batchId, sessionEpoch);

    private static MetricSample Sample(string key, float value, string? deviceId = "cpu-0",
        SampleQuality quality = SampleQuality.Good, double ageMs = 12)
        => new(key, deviceId, value, "%", quality, null, ageMs);

    [Fact]
    public void DuplicateBatch_IsRejectedWithoutAddingPoints()
    {
        var buffer = new ShortTermTrendBuffer();
        HardwareSnapshot snapshot = Snapshot(10, 1, Sample("cpu.load", 12));

        Assert.True(buffer.Ingest(snapshot, DateTimeOffset.UtcNow).Accepted);
        for (int i = 0; i < 99; i++)
            Assert.False(buffer.Ingest(snapshot, DateTimeOffset.UtcNow).Accepted);

        TrendBufferStats stats = buffer.GetStats();
        Assert.Equal(1, stats.AcceptedSnapshots);
        Assert.Equal(99, stats.DuplicateBatches);
        Assert.Equal(1, stats.PointCount);
    }

    [Fact]
    public void OlderBatch_IsRejectedAsOutOfOrder()
    {
        var buffer = new ShortTermTrendBuffer();
        buffer.Ingest(Snapshot(20, 1, Sample("cpu.load", 12)), DateTimeOffset.UtcNow);

        IngestResult result = buffer.Ingest(Snapshot(19, 1, Sample("cpu.load", 11)), DateTimeOffset.UtcNow);

        Assert.False(result.Accepted);
        Assert.Equal(1, buffer.GetStats().OutOfOrderBatches);
        Assert.Single(buffer.GetSeries("cpu.load", "cpu-0"));
    }

    [Fact]
    public void PreservesQualityAgeAndStableDeviceIdentity()
    {
        var buffer = new ShortTermTrendBuffer();
        DateTimeOffset now = new(2026, 10, 27, 3, 0, 0, TimeSpan.Zero);

        buffer.Ingest(Snapshot(1, 1,
            Sample("gpu.0.temp", 61, "gpu-abc", SampleQuality.Stale, 1200)), now);

        TrendBucket point = Assert.Single(buffer.GetSeries("gpu.0.temp", "gpu-abc"));
        Assert.Equal(61, point.Value);
        Assert.Equal(SampleQuality.Stale, point.Quality);
        Assert.Equal(1200, point.SampleAgeMs);
        Assert.Equal("gpu-abc", point.StableDeviceId);
        Assert.Equal(now, point.TimeUtc);
    }

    [Fact]
    public void CapacityIsBoundedAndDropsOldestPoint()
    {
        var buffer = new ShortTermTrendBuffer(capacityPerSeries: 2, maxSeries: 4);
        buffer.Ingest(Snapshot(1, 1, Sample("cpu.load", 1)), DateTimeOffset.UtcNow);
        buffer.Ingest(Snapshot(2, 1, Sample("cpu.load", 2)), DateTimeOffset.UtcNow);
        buffer.Ingest(Snapshot(3, 1, Sample("cpu.load", 3)), DateTimeOffset.UtcNow);

        TrendBucket[] points = buffer.GetSeries("cpu.load", "cpu-0").ToArray();
        Assert.Equal(2, points.Length);
        Assert.Equal(2, points[0].Value);
        Assert.Equal(3, points[1].Value);
        Assert.Equal(1, buffer.GetStats().DroppedPoints);
    }

    [Fact]
    public void SessionEpochChange_ClearsOldSessionAndRejectsOldBatchOrder()
    {
        var buffer = new ShortTermTrendBuffer();
        buffer.Ingest(Snapshot(50, 1, Sample("cpu.load", 10)), DateTimeOffset.UtcNow);

        IngestResult result = buffer.Ingest(Snapshot(1, 2, Sample("cpu.load", 20)), DateTimeOffset.UtcNow);

        Assert.True(result.Accepted);
        Assert.Equal(1, buffer.GetStats().SessionResets);
        Assert.Single(buffer.GetSeries("cpu.load", "cpu-0"));
        Assert.Equal(20, buffer.GetSeries("cpu.load", "cpu-0")[0].Value);
    }

    [Fact]
    public void ThirtyTwoSeriesAtFullHour_StayWithinDeclaredPointBudget()
    {
        var buffer = new ShortTermTrendBuffer();
        long batch = 1;
        DateTimeOffset start = new(2026, 10, 27, 3, 0, 0, TimeSpan.Zero);
        for (int series = 0; series < 32; series++)
        {
            for (int second = 0; second < 3600; second++)
            {
                buffer.Ingest(
                    Snapshot(batch++, 1, Sample($"metric.{series}", second, $"dev-{series}")),
                    start.AddSeconds(second));
            }
        }

        TrendBufferStats stats = buffer.GetStats();
        Assert.Equal(32, stats.SeriesCount);
        Assert.Equal(115_200, stats.PointCount);
        Assert.Equal(0, stats.RejectedSamples);
        Assert.Equal(0, stats.DroppedPoints);
    }

    [Fact]
    public void MaxSeries_IsEnforced()
    {
        var buffer = new ShortTermTrendBuffer(capacityPerSeries: 2, maxSeries: 2);
        buffer.Ingest(Snapshot(1, 1,
            Sample("a", 1, "dev-a"),
            Sample("b", 2, "dev-b"),
            Sample("c", 3, "dev-c")), DateTimeOffset.UtcNow);

        Assert.Equal(2, buffer.GetStats().SeriesCount);
        Assert.Equal(1, buffer.GetStats().RejectedSamples);
        Assert.Empty(buffer.GetSeries("c", "dev-c"));
    }
}
