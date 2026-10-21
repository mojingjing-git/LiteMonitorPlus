namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>事件代表什么，过滤器据此选择统计口径。</summary>
public enum NetworkTrafficEventKind
{
    /// <summary>新数据（SEND / RECV）。</summary>
    Data = 0,

    /// <summary>重传：字节已经被 SEND 计过一次。</summary>
    Retransmit = 1,

    /// <summary>拷贝路径（TCP copy-up）：**永远不是新数据**，禁止计入字节。</summary>
    CopyUp = 2,
}

/// <summary>一次观测到的传输。不可变，热路径免分配。</summary>
/// <remarks>
/// <see cref="Timestamp"/> 是事件的到达时间戳。产品侧的测量窗口必须使用**单调时钟**；
/// 这个字段只是事件的到达标记。
/// </remarks>
public readonly record struct NetworkTrafficEvent(
    DateTime Timestamp,
    NetworkTrafficProtocol Protocol,
    NetworkTrafficDirection Direction,
    IpKey RemoteAddress,
    IpKey LocalAddress,
    int RemotePort,
    int LocalPort,
    long SizeBytes,
    uint ProcessId,
    NetworkTrafficEventKind Kind = NetworkTrafficEventKind.Data);

/// <summary>重传字节是否计入总量。</summary>
public enum RetransmitPolicy
{
    /// <summary>只计 SEND/RECV；重传字节单独上报，不计入总量。</summary>
    ExcludeRetransmitFromTotal = 0,

    /// <summary>SEND + RETRANSMIT 都计，即真正推向线路的字节。</summary>
    IncludeRetransmitInTotal = 1,
}

/// <summary>事件源的生命周期状态。</summary>
public enum TrafficSourceStatus
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Degraded = 3,
    Failed = 4,
}

/// <summary>
/// 可注入的流量事件源。屏蔽全部 ETW 细节，让产品模块可以在没有真实 trace 会话的情况下被单元测试。
/// </summary>
public interface INetworkTrafficEventSource : IDisposable
{
    /// <summary>本事件源自己拥有的 ETW 会话名。**绝不**是共享/全局会话名。</summary>
    string SessionName { get; }

    TrafficSourceStatus Status { get; }

    /// <summary>Failed/Degraded 的原因，或最后一次错误。</summary>
    string? StatusDetail { get; }

    long EventsProcessed { get; }

    long EventsUndecodable { get; }

    /// <summary>ETW 报告的丢事件数（会话级）。</summary>
    long EventsLost { get; }

    long BuffersLost { get; }

    /// <summary>本进程生命周期内会话被（重）启动的次数。</summary>
    int Generation { get; }

    void Start();

    void Stop();

    /// <summary>每个解码成功的传输事件触发一次，运行在 ETW 消费线程上。</summary>
    event Action<NetworkTrafficEvent>? EventReceived;
}
