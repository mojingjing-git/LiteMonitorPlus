namespace LiteMonitor.Contracts;

/// <summary>单个指标样本：不可变。Key 保持既有外部指标 Key（如 GPU.{index}.*），由 StableDeviceId 解释归属。</summary>
public sealed record MetricSample(
    string Key,
    string? StableDeviceId,
    float? Value,
    string? Unit,
    SampleQuality Quality,
    string? MissingReason,
    double SampleAgeMs);

/// <summary>
/// 完整采样批次：原子替换发布，值与设备名/目录不混代次。
/// CatalogVersion 对应 DeviceCatalog.Version；ConfigRevision 对应 SamplingConfig.Revision。
/// </summary>
public sealed record HardwareSnapshot(
    long BatchId,
    long CatalogVersion,
    int ConfigRevision,
    IReadOnlyList<MetricSample> Samples,
    TimeSpan SamplingDuration,
    double TimestampMonoMs,
    int SessionEpoch = 0);   // E01-A：会话纪元（Host 重启/采集器重建时递增；旧会话数据不得混入新会话）
