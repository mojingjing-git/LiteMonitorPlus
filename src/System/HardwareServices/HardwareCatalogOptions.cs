using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.SystemServices;

/// <summary>UI selection options projected from one published hardware catalog.</summary>
internal sealed record HardwareSelectionOptions(
    IReadOnlyList<DeviceOption> Disks,
    IReadOnlyList<string> Networks,
    IReadOnlyList<HardwareScanner.GpuOption> Gpus,
    IReadOnlyList<string> Fans,
    IReadOnlyList<string> MoboTemperatures,
    IReadOnlyList<CpuVoltageCandidate> CpuVoltageCandidates,
    bool IsHostLimited)
{
    public static HardwareSelectionOptions Empty(bool hostLimited = false) =>
        new(
            Array.Empty<DeviceOption>(),
            Array.Empty<string>(),
            Array.Empty<HardwareScanner.GpuOption>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<CpuVoltageCandidate>(),
            hostLimited);

    public static HardwareSelectionOptions FromCatalog(HardwareCatalog catalog, bool hostLimited)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        Dictionary<string, HardwareCatalogEntry> devices = catalog.Entries
            .Where(entry => entry.SensorType is null)
            .GroupBy(entry => entry.StableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var disks = BuildDeviceOptions(
            catalog.Entries.Where(entry =>
                entry.SensorType is null &&
                entry.DeviceType.StartsWith("Storage", StringComparison.OrdinalIgnoreCase)),
            DeviceKind.Storage);

        var gpus = BuildGpuOptions(catalog.Entries.Where(entry =>
            entry.SensorType is null &&
            entry.DeviceType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)));

        string[] networks = catalog.Entries
            .Where(entry =>
                entry.SensorType is null &&
                entry.DeviceType.Equals("Network", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] fans = BuildSensorLabels(catalog.Entries, devices, "Fan");
        string[] moboTemperatures = BuildSensorLabels(
            catalog.Entries.Where(entry =>
            {
                if (!string.Equals(entry.SensorType, "Temperature", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(entry.ParentStableId) ||
                    !devices.TryGetValue(entry.ParentStableId, out HardwareCatalogEntry? parent))
                {
                    return false;
                }

                return parent.DeviceType.StartsWith("Motherboard", StringComparison.OrdinalIgnoreCase) ||
                       parent.DeviceType.StartsWith("SuperIO", StringComparison.OrdinalIgnoreCase);
            }),
            devices,
            expectedSensorType: null);

        return new HardwareSelectionOptions(
            disks,
            networks,
            gpus,
            fans,
            moboTemperatures,
            Array.Empty<CpuVoltageCandidate>(),
            hostLimited);
    }

    private static DeviceOption[] BuildDeviceOptions(
        IEnumerable<HardwareCatalogEntry> entries,
        DeviceKind kind)
    {
        HardwareCatalogEntry[] source = entries.ToArray();
        Dictionary<string, int> nameCounts = source
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        return source.Select(entry =>
        {
            seen.TryGetValue(entry.Name, out int ordinal);
            seen[entry.Name] = ++ordinal;
            string label = nameCounts[entry.Name] > 1
                ? $"{entry.Name} #{ordinal}"
                : entry.Name;
            string value = string.IsNullOrWhiteSpace(entry.StableId) ? entry.RuntimeId : entry.StableId;
            return new DeviceOption
            {
                Label = label,
                Name = entry.Name,
                Value = value,
                StableId = entry.StableId,
                RuntimeId = entry.RuntimeId,
                Kind = kind,
                IsPresent = entry.Available
            };
        }).ToArray();
    }

    private static HardwareScanner.GpuOption[] BuildGpuOptions(IEnumerable<HardwareCatalogEntry> entries)
    {
        HardwareCatalogEntry[] source = entries.ToArray();
        Dictionary<string, int> nameCounts = source
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        return source.Select(entry =>
        {
            seen.TryGetValue(entry.Name, out int ordinal);
            seen[entry.Name] = ++ordinal;
            string label = nameCounts[entry.Name] > 1
                ? $"{entry.Name} #{ordinal}"
                : entry.Name;
            string value = string.IsNullOrWhiteSpace(entry.StableId) ? entry.RuntimeId : entry.StableId;
            return new HardwareScanner.GpuOption
            {
                Label = label,
                Value = value,
                Name = entry.Name,
                StableId = entry.StableId,
                RuntimeId = entry.RuntimeId
            };
        }).ToArray();
    }

    private static string[] BuildSensorLabels(
        IEnumerable<HardwareCatalogEntry> entries,
        IReadOnlyDictionary<string, HardwareCatalogEntry> devices,
        string? expectedSensorType)
    {
        return entries
            .Where(entry => expectedSensorType is null ||
                            string.Equals(entry.SensorType, expectedSensorType, StringComparison.OrdinalIgnoreCase))
            .Select(entry =>
            {
                string parentName =
                    !string.IsNullOrWhiteSpace(entry.ParentStableId) &&
                    devices.TryGetValue(entry.ParentStableId, out HardwareCatalogEntry? parent)
                        ? parent.Name
                        : "";
                return string.IsNullOrWhiteSpace(parentName) ? entry.Name : $"{entry.Name} [{parentName}]";
            })
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
