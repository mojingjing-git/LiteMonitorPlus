namespace LiteMonitor.Contracts;

/// <summary>样本质量。真实 0 = Good + Value=0；缺失 = Missing + 原因；过期 = Stale（不重置时效）。</summary>
public enum SampleQuality
{
    Good,
    Stale,
    Missing,
    Error,
}

/// <summary>采集层健康状态（Plan A §6.1 状态机的进程内首版子集）。</summary>
public enum HardwareState
{
    Stopped,
    Starting,
    Ready,
    Degraded,
    Unresponsive,
    Stopping,
}
