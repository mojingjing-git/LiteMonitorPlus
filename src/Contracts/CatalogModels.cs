namespace LiteMonitor.Contracts;

/// <summary>目录条目：平台无关。StableId 为稳定身份（F07 语义，热插拔/换序不漂移），RuntimeId 为当前运行身份。</summary>
public sealed record DeviceCatalogEntry(
    string StableId,
    string RuntimeId,
    string? ParentStableId,
    string DeviceType,
    string? SensorType,
    bool Available);

/// <summary>设备/传感器目录：整体版本化。旧版本不覆盖新数据。</summary>
public sealed record DeviceCatalog(long Version, IReadOnlyList<DeviceCatalogEntry> Entries);
