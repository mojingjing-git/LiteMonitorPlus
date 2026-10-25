using System.Text.Json;

namespace LiteMonitor.Contracts;

/// <summary>
/// P-I2 / Plan A §5.1 IPC 首版协议常量：长度前缀 + UTF-8 JSON；单条上限 1MiB；
/// 消息头含协议主/次版本、消息类型、会话令牌、请求 ID、序号。
/// 主版本不兼容必须拒绝连接（不依赖 JSON 忽略未知字段掩盖冲突）；兼容新增字段走次版本。
/// </summary>
public static class IpcProtocol
{
    public const int MajorVersion = 1;
    public const int MinorVersion = 0;

    /// <summary>单条消息（含头）上限：接收长度先校验再分配内存。</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public const int LengthPrefixBytes = 4;

    // 消息类型判别值（新增类型 = 次版本演进）。
    public const string TypeHello = "hello";           // Host → 主程序：握手身份
    public const string TypeWelcome = "welcome";       // 主程序 → Host：握手裁决
    public const string TypeSnapshot = "snapshot";     // Host → 主程序：完整快照批次
    public const string TypeHeartbeat = "heartbeat";   // Host → 主程序：存活与进度
    public const string TypeCommand = "command";       // 主程序 → Host：带应答命令
    public const string TypeResponse = "response";     // Host → 主程序：命令应答
    public const string TypeGoodbye = "goodbye";       // 双向：受控关闭预告

    // 命令操作名（IpcCommandMessage.Operation）。
    public const string OpApplyConfig = "apply-config";
    public const string OpSubmitDemand = "submit-demand";
    public const string OpRescan = "rescan";
    public const string OpGetCatalog = "get-catalog";
    public const string OpShutdown = "shutdown";

    /// <summary>命令默认应答期限（调用方超时不等于底层操作已取消，不得立即重开相同操作）。</summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>所有合法消息类型（未知类型 = 协议违规，接收侧断连）。</summary>
    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        TypeHello, TypeWelcome, TypeSnapshot, TypeHeartbeat, TypeCommand, TypeResponse, TypeGoodbye,
    };
}

/// <summary>消息头：每条消息携带。SessionId 为主程序生成的随机会话令牌（防伪造连接）；
/// RequestId 仅命令/应答使用（其余为 0）；Sequence 为发送方向上的单调序号。</summary>
public sealed record IpcHeader(int Major, int Minor, string Type, string SessionId, long RequestId, long Sequence);

/// <summary>
/// 传输信封：Header + Payload（编码时为具体消息 DTO；解码后为 JsonElement，由
/// <see cref="IpcCodec"/> 按类型转回 DTO）。纯数据，无传输/线程语义。
/// </summary>
public sealed record IpcEnvelope(IpcHeader Header, object? Payload)
{
    /// <summary>辅助方法不参与序列化（System.Text.Json 只序列化属性/字段）。</summary>
    public T PayloadAs<T>() => IpcCodec.DeserializePayload<T>(this);
}

// ── 具体消息 DTO ─────────────────────────────────────────────────────────────

/// <summary>Host 握手：进程身份（PID 由接收侧以管道 API 独立核验，不信载荷）。</summary>
public sealed record IpcHelloMessage(int HostProcessId, long HostStartTickMs, string HostVersion);

/// <summary>主程序握手裁决：拒绝时给出可解释原因（版本不匹配 / 会话不匹配 / 身份不匹配）。</summary>
public sealed record IpcWelcomeMessage(bool Accepted, string? Reason);

/// <summary>完整快照批次。PublishTickMs 为 Host 发布时的单调毫秒（接收侧结合本地单调钟算样本 age）。</summary>
public sealed record IpcSnapshotMessage(HardwareSnapshot Snapshot, long PublishTickMs);

/// <summary>心跳：状态、当前操作、进度序号（采样无进展判定用 ProgressSeq）。</summary>
public sealed record IpcHeartbeatMessage(
    HardwareState State, string? CurrentOperation, long ProgressSeq, string? DiagnosticReason, long TickMs);

/// <summary>主程序 → Host 命令：Operation 见 <see cref="IpcProtocol.Op*"/>，参数为具体 DTO 的 JSON。</summary>
public sealed record IpcCommandMessage(string Operation, string? ArgumentJson);

/// <summary>命令应答：ResultJson 为具体结果 DTO 的 JSON（目录/配置结果等）。</summary>
public sealed record IpcResponseMessage(bool Ok, string? Error, string? ResultJson);

/// <summary>受控关闭预告（正常关闭路径：先 goodbye，再退出）。</summary>
public sealed record IpcGoodbyeMessage(string Reason);
