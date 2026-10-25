namespace LiteMonitor.Contracts;

/// <summary>
/// 目录条目：平台无关。设备条目 SensorType 为 null，传感器条目带父设备 StableId 与当前值/峰值。
/// StableId 为稳定身份（F07 语义，热插拔/换序不漂移），RuntimeId 为当前运行身份；
/// 进程内适配器 v1 两者暂用 LHM Identifier 路径，F07 绑定随 B3 设置页重构接入。
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
    bool Available,
    string Name,
    float? Value = null,
    float? Max = null);

/// <summary>设备/传感器目录：整体版本化。旧版本不覆盖新数据。</summary>
public sealed record HardwareCatalog(long Version, IReadOnlyList<HardwareCatalogEntry> Entries);
