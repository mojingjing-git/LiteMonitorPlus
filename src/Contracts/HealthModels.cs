namespace LiteMonitor.Contracts;

/// <summary>采集层健康状态快照。ProgressSeq 单调递增，用于判断采样是否仍有进展。</summary>
public sealed record HardwareHealth(
    HardwareState State,
    string? CurrentOperation,
    long ProgressSeq,
    string? DiagnosticReason);
