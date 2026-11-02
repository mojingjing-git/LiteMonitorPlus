using LiteMonitor.Contracts;
using LiteMonitor.SystemServices;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class HardwareCatalogOptionsTests
{
    [Fact]
    public void HostCatalogProjectsOnlyAvailableDeviceFamiliesAndKeepsStableIds()
    {
        var catalog = new HardwareCatalog(9, new[]
        {
            Entry("disk-a", null, "Storage", "Samsung SSD"),
            Entry("disk-b", null, "Storage", "Samsung SSD"),
            Entry("net-a", null, "Network", "Ethernet"),
            Entry("gpu-a", null, "GpuNvidia", "NVIDIA RTX"),
            Entry("mobo-a", null, "Motherboard", "Test Board"),
            Entry("fan-a", "mobo-a", "Motherboard", "CPU Fan", sensorType: "Fan"),
            Entry("mobo-temp-a", "mobo-a", "Motherboard", "System", sensorType: "Temperature"),
            Entry("cpu-temp-a", "cpu-a", "Cpu", "Core Average", sensorType: "Temperature")
        });

        HardwareSelectionOptions options = HardwareSelectionOptions.FromCatalog(catalog, hostLimited: true);

        Assert.True(options.IsHostLimited);
        Assert.Equal(2, options.Disks.Count);
        Assert.Equal("disk-a", options.Disks[0].Value);
        Assert.Contains("#1", options.Disks[0].Label, StringComparison.Ordinal);
        Assert.Contains("#2", options.Disks[1].Label, StringComparison.Ordinal);
        Assert.Equal(new[] { "Ethernet" }, options.Networks);
        Assert.Single(options.Gpus);
        Assert.Equal("gpu-a", options.Gpus[0].StableId);
        Assert.Equal(new[] { "CPU Fan [Test Board]" }, options.Fans);
        Assert.Equal(new[] { "System [Test Board]" }, options.MoboTemperatures);
        Assert.Empty(options.CpuVoltageCandidates);
    }

    private static HardwareCatalogEntry Entry(
        string stableId,
        string? parentId,
        string deviceType,
        string name,
        string? sensorType = null)
        => new(
            StableId: stableId,
            RuntimeId: stableId + "-runtime",
            ParentStableId: parentId,
            DeviceType: deviceType,
            SensorType: sensorType,
            Available: true,
            Name: name);
}
