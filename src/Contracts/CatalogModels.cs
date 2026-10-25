namespace LiteMonitor.Contracts;

/// <summary>
/// 目录条目：平台无关。StableId 为稳定身份（F07 语义，热插拔/换序不漂移），RuntimeId 为当前运行身份。
/// </summary>
/// <remarks>
/// 命名说明：契约类型刻意不叫 <c>DeviceCatalog</c>——主程序集中 F07 已有公开类型
/// <c>LiteMonitor.src.SystemServices.DeviceCatalog</c>（稳定身份目录，是本类型的数据源之一），
/// 同名会在两个命名空间同时导入时产生永久歧义（P-B1B2 实施时固定）。
/// </remarks>
public sealed record HardwareCatalogEntry(
    string StableId,
    string RuntimeId,
    string? ParentStableId,
    string DeviceType,
    string? SensorType,
    bool Available);

/// <summary>设备/传感器目录：整体版本化。旧版本不覆盖新数据。</summary>
public sealed record HardwareCatalog(long Version, IReadOnlyList<HardwareCatalogEntry> Entries);
