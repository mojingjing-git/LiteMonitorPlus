using LiteMonitor.Contracts;
using LiteMonitor.Core;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class HardwareSnapshotValueIndexTests
{
    [Fact]
    public void GoodAndStaleValuesRemainVisibleWhenMissingAndErrorDoNot()
    {
        var index = CreateIndex(
            Sample("CPU.Load", 41.5f),
            Sample("CPU.Temp", 72.0f, SampleQuality.Stale),
            Sample("MEM.Load", null, SampleQuality.Missing, "sensor-missing"),
            Sample("GPU.Temp", null, SampleQuality.Error, "driver-error"));

        Assert.True(index.TryGetValue("cpu.load", out float load));
        Assert.Equal(41.5f, load);
        Assert.True(index.TryGetValue("CPU.Temp", out float temperature));
        Assert.Equal(72.0f, temperature);
        Assert.False(index.TryGetGoodValue("CPU.Temp", out _));
        Assert.True(index.TryGetGoodValue("CPU.Load", out float historyLoad));
        Assert.Equal(41.5f, historyLoad);
        Assert.False(index.TryGetValue("MEM.Load", out _));
        Assert.False(index.TryGetValue("GPU.Temp", out _));
        Assert.False(index.TryGetValue("DISK.Temp", out _));
    }

    [Fact]
    public void ExactDeviceLookupDoesNotReturnAValueFromAnotherDevice()
    {
        var index = CreateIndex(
            Sample("GPU.Temp", 61, deviceId: "gpu-a"),
            Sample("GPU.Temp", 75, deviceId: "gpu-b"));

        Assert.Contains("GPU.Temp", index.AmbiguousKeys);
        Assert.True(index.TryGetValue("GPU.Temp", "gpu-b", out float value));
        Assert.Equal(75, value);
        Assert.False(index.TryGetValue("GPU.Temp", "gpu-c", out _));

        Assert.True(index.TryGetSample("GPU.Temp", out MetricSample? first));
        Assert.Equal("gpu-a", first?.StableDeviceId);
    }

    [Fact]
    public void GpuCountUsesIndexedKeysAndLegacyAliasOnlyWhenItHasAValue()
    {
        var indexed = CreateIndex(
            Sample("GPU.0.Load", 10),
            Sample("GPU.1.Load", 20),
            Sample("GPU.Load", 10));
        var legacyGood = CreateIndex(Sample("GPU.Load", 10));
        var legacyMissing = CreateIndex(
            Sample("GPU.Load", null, SampleQuality.Missing, "no-sensor"));

        Assert.Equal(2, indexed.GpuCount);
        Assert.Equal(1, legacyGood.GpuCount);
        Assert.Equal(0, legacyMissing.GpuCount);
    }

    [Fact]
    public void EmptySnapshotDoesNotTurnMissingDataIntoZero()
    {
        var index = CreateIndex();

        Assert.Empty(index.AmbiguousKeys);
        Assert.Equal(0, index.GpuCount);
        Assert.False(index.TryGetValue("CPU.Load", out _));
        Assert.False(index.TryGetSample("CPU.Load", out _));
    }

    private static HardwareSnapshotValueIndex CreateIndex(params MetricSample[] samples) =>
        new(new HardwareSnapshot(
            BatchId: 7,
            CatalogVersion: 2,
            ConfigRevision: 3,
            Samples: samples,
            SamplingDuration: TimeSpan.FromMilliseconds(4),
            TimestampMonoMs: 1,
            SessionEpoch: 5));

    private static MetricSample Sample(
        string key,
        float? value,
        SampleQuality quality = SampleQuality.Good,
        string? reason = null,
        string? deviceId = null)
        => new(key, deviceId, value, null, quality, reason, 0);
}
