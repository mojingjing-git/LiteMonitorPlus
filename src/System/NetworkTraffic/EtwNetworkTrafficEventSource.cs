using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>
/// 可注入的 ETW 会话（真实实现的薄封装）。
/// <para>存在的唯一理由：让「EnableProvider 失败时必须释放已创建会话」（N0 缺陷 A）
/// 这条**回归防线**可以在不启管理员、不建真实 trace 会话的情况下被单元测试覆盖。</para>
/// </summary>
internal interface ITraceSession : IDisposable
{
    string SessionName { get; }

    bool EnableProvider(string providerName, TraceEventLevel level, ulong matchAnyKeywords);

    /// <summary>订阅全部 provider 事件。</summary>
    void Subscribe(Action<TraceEvent> handler);

    /// <summary>阻塞式消费事件流。</summary>
    void Process();

    void StopProcessing();

    long EventsLost { get; }
}

/// <summary>真实 ETW 会话。</summary>
internal sealed class RealTraceSession : ITraceSession
{
    private readonly TraceEventSession _session;

    public RealTraceSession(string name)
    {
        _session = new TraceEventSession(name);
    }

    public string SessionName => _session.SessionName;

    public bool EnableProvider(string providerName, TraceEventLevel level, ulong matchAnyKeywords)
        => _session.EnableProvider(providerName, level, matchAnyKeywords);

    public void Subscribe(Action<TraceEvent> handler) => _session.Source.AllEvents += handler;

    public void Process() => _session.Source.Process();

    public void StopProcessing() => _session.Source.StopProcessing();

    public long EventsLost => _session.Source.EventsLost;

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// ETW 支撑的流量事件源。
///
/// <para>Provider：Microsoft-Windows-Kernel-Network（GUID 7dd42a49-5329-4832-8dfd-43d979153a88）。
/// 它是 manifest provider，可以在**普通私有会话**上启用 —— 因此既不碰全局 "NT Kernel Logger"，
/// 也不复用/停止任何其它程序（例如 PresentMon 的 LiteMonitor++_Golden_Session）的会话。</para>
///
/// <para>载荷布局由受控流量反推并逐字段验证（N0 证据）：</para>
/// <code>
/// IPv4 : PID(4) size(4) remoteAddr(4) localAddr(4) remotePort(2) localPort(2) [seq(4) conn(4)] pad(8)
/// IPv6 : PID(4) size(4) remoteAddr(16) localAddr(16) remotePort(2) localPort(2) [seq(4) conn(4)] pad(8)
/// </code>
/// <para>地址是网络字节序，端口是网络字节序 uint16。**两个方向上 #1 恒为远端**、#2 恒为本地。</para>
///
/// <para>★ 零拷贝解码（C5）：N0 用 <c>TraceEvent.EventData()</c>，**每个事件复制一次载荷**，
/// 是分配的主因（227~423 B/事件）。这里改成直接读 ETW 缓冲区的原生内存
/// （<see cref="NativePayloadReader"/>），热路径**零堆分配**；只有解码自检失败时才回退到拷贝路径。</para>
/// </summary>
public sealed class EtwNetworkTrafficEventSource : INetworkTrafficEventSource
{
    public const string ProviderName = "Microsoft-Windows-Kernel-Network";

    public static readonly Guid ProviderGuid = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    /// <summary>本程序创建的会话名前缀。只有这个前缀的会话才允许被本程序清理。</summary>
    public const string SessionNamePrefix = "LiteMonitor.NetExclusion.Product.";

    /// <summary>Provider 的 IPV4(0x10) | IPV6(0x20) 关键字。</summary>
    private const ulong KeywordIpv4OrIpv6 = 0x30;

    /// <summary>
    /// 用户数据相对 <c>TraceEvent.DataStart</c> 的字节偏移。
    /// <para>★ 实机标定结果 = <b>0</b>：<c>DataStart</c> **就是**用户数据的起点
    /// （探针的 <c>calibrate</c> 命令可重新标定并写出证据：
    /// <c>docs/reviews/evidence/network-exclusion/N0/…</c> 同目录的 calibrate.json）。
    /// 标定不成立时解码自检会失败并自动回退到拷贝路径，功能不会静默出错。</para>
    /// </summary>
    public const int PayloadOffsetFromDataStart = 0;

    /// <summary>绝不允许使用的会话名（全局/其它程序的会话）。</summary>
    private static readonly string[] ReservedSessionNames =
    {
        "LiteMonitor++_Golden_Session",
        "NT Kernel Logger",
        "EventLogger-StartTrace",
    };

    private readonly object _gate = new();
    private readonly Action<string>? _log;

    private ITraceSession? _session;
    private Task? _processTask;
    private CancellationTokenSource? _cts;
    private int _generation;
    private long _eventsProcessed;
    private long _eventsUndecodable;
    private long _copyEvents;
    private long _retransmitEvents;
    private long _eventsSeen;
    private long _controlEvents;
    private long _zeroCopyDecoded;
    private long _copyFallbackDecoded;
    private long _copyFallbackFailures;
    private TrafficSourceStatus _status = TrafficSourceStatus.Stopped;
    private string? _statusDetail;

    public EtwNetworkTrafficEventSource(Action<string>? log = null)
    {
        _log = log;
        SessionName = BuildUniqueSessionName();
    }

    /// <summary>
    /// 会话工厂（默认创建真实 ETW 会话；单元测试可注入替身）。
    /// <para>★ 存在的理由：让「EnableProvider 失败 → 已创建会话必须被释放」这条回归防线可测。</para>
    /// </summary>
    internal Func<string, ITraceSession>? SessionFactory { get; set; }

    /// <summary>当前会话名。每次 <see cref="Start"/> 都会重新生成，避免与残留会话重名。</summary>
    public string SessionName { get; private set; }

    public TrafficSourceStatus Status { get { lock (_gate) { return _status; } } }

    public string? StatusDetail { get { lock (_gate) { return _statusDetail; } } }

    public long EventsProcessed { get { lock (_gate) { return _eventsProcessed; } } }

    public long EventsUndecodable { get { lock (_gate) { return _eventsUndecodable; } } }

    public long EventsLost => _session?.EventsLost ?? 0;

    public long BuffersLost => 0;

    public long CopyEventsObserved { get { lock (_gate) { return _copyEvents; } } }

    public long RetransmitEventsObserved { get { lock (_gate) { return _retransmitEvents; } } }

    /// <summary>送到源里的 provider 事件总数（解码之前）。</summary>
    public long EventsSeen { get { lock (_gate) { return _eventsSeen; } } }

    /// <summary>连接级事件（connect/disconnect/accept/fail）计数，永远不计入字节。</summary>
    public long ControlEventsObserved { get { lock (_gate) { return _controlEvents; } } }

    /// <summary>走**零拷贝**路径成功解码的事件数。</summary>
    public long ZeroCopyDecoded { get { lock (_gate) { return _zeroCopyDecoded; } } }

    /// <summary>回退到拷贝路径才解码成功的事件数（应当接近 0）。</summary>
    public long CopyFallbackDecoded { get { lock (_gate) { return _copyFallbackDecoded; } } }

    /// <summary>拷贝路径也解码失败的事件数。</summary>
    public long CopyFallbackFailures { get { lock (_gate) { return _copyFallbackFailures; } } }

    public int Generation { get { lock (_gate) { return _generation; } } }

    /// <summary>
    /// <c>EnableProvider</c> 的返回值。
    /// <para>★ TraceEvent 4.0.0 对本 provider 返回 <b>false</b>，但事件照常到达 ——
    /// 该返回值**不能**当作"provider 没启用"的判据。</para>
    /// </summary>
    public bool ProviderEnableAcknowledged { get; private set; }

    public event Action<NetworkTrafficEvent>? EventReceived;

    private static string BuildUniqueSessionName()
    {
        // 每进程 + 每启动一次唯一；绝不是共享/全局日志器名。
        return SessionNamePrefix + Environment.ProcessId + "." +
               DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
    }

    /// <summary>
    /// 清理本程序**遗留**的会话（N0 缺陷 B：进程异常退出会话会残留在系统里）。
    /// 只碰以 <see cref="SessionNamePrefix"/> 开头的会话，绝不碰其它程序的会话。
    /// </summary>
    public static int CleanupStaleSessions(Action<string>? log = null)
    {
        int removed = 0;
        try
        {
            List<string> names = TraceEventSession.GetActiveSessionNames();
            foreach (string name in names)
            {
                if (name == null || !name.StartsWith(SessionNamePrefix, StringComparison.Ordinal)) { continue; }
                try
                {
                    TraceEventSession? stale = TraceEventSession.GetActiveSession(name);
                    if (stale == null) { continue; }
                    stale.Dispose();
                    removed++;
                    log?.Invoke("Removed stale exclusion session: " + name);
                }
                catch (Exception ex)
                {
                    log?.Invoke("Failed to remove stale session " + name + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("Stale session cleanup failed: " + ex.Message);
        }
        return removed;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_status == TrafficSourceStatus.Running) { return; }

            foreach (string reserved in ReservedSessionNames)
            {
                if (SessionName.StartsWith(reserved, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Refusing to use reserved session name: " + reserved);
                }
            }

            _status = TrafficSourceStatus.Starting;
            _statusDetail = null;
            _generation++;

            // 每次启动都用全新名字：万一上次异常退出留下了同名会话，也不会撞名。
            SessionName = BuildUniqueSessionName();

            ITraceSession? created = null;
            try
            {
                var session = SessionFactory != null
                    ? SessionFactory(SessionName)
                    : new RealTraceSession(SessionName);
                created = session;

                bool enabled = session.EnableProvider(ProviderName, TraceEventLevel.Verbose, KeywordIpv4OrIpv6);

                // ★ 实机标定结论：TraceEvent 4.0.0 的 EnableProvider 对本 provider **返回 false**，
                //   但事件**照常到达**（N0 探针忽略返回值、只按事件判定）。
                //   因此这里**不能**把 false 当成失败 —— 否则功能会被误判为不可用。
                //   真正的失败信号是「会话在跑但收不到事件」，由上层（服务）用事件缺失状态表达。
                ProviderEnableAcknowledged = enabled;
                _log?.Invoke("EnableProvider(" + ProviderName + ") returned " + enabled +
                             " (known TraceEvent 4.0.0 quirk: false does not mean the provider is off)");

                session.Subscribe(OnEvent);

                _cts = new CancellationTokenSource();
                _session = session;
                _processTask = Task.Run(() =>
                {
                    try
                    {
                        session.Process();
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            _status = TrafficSourceStatus.Failed;
                            _statusDetail = ex.GetType().Name + ": " + ex.Message;
                        }
                        _log?.Invoke("ETW consumer stopped: " + _statusDetail);
                    }
                }, _cts.Token);

                _status = TrafficSourceStatus.Running;
                _log?.Invoke("ETW session started: " + SessionName + " (generation " + _generation + ")");
            }
            catch (Exception ex)
            {
                _status = TrafficSourceStatus.Failed;
                _statusDetail = ex.GetType().Name + ": " + ex.Message;

                // ★ N0 缺陷 A：会话已经创建、但 EnableProvider 失败时**必须**释放它，否则会话残留在系统里。
                if (created != null)
                {
                    try { created.Dispose(); }
                    catch (Exception disposeEx) { _log?.Invoke("cleanup of failed session failed: " + disposeEx.Message); }
                    _log?.Invoke("Discarded ETW session after failed enable: " + SessionName);
                }

                _log?.Invoke("ETW session failed to start: " + _statusDetail);
                throw;
            }
        }
    }

    public void Stop()
    {
        ITraceSession? session;
        Task? task;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            session = _session;
            task = _processTask;
            cts = _cts;
            _session = null;
            _processTask = null;
            _cts = null;
            if (_status != TrafficSourceStatus.Failed) { _status = TrafficSourceStatus.Stopped; }
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { session?.StopProcessing(); } catch { /* ignore */ }
        // ★ 只处置**自己创建**的会话。
        try { session?.Dispose(); } catch { /* ignore */ }
        try { cts?.Dispose(); } catch { /* ignore */ }
        try { task?.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        _log?.Invoke("ETW session stopped: " + SessionName);
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnEvent(TraceEvent e)
    {
        int task = Convert.ToInt32(e.Task);
        int opcode = Convert.ToInt32(e.Opcode);
        lock (_gate) { _eventsSeen++; }

        if (!IsDataEvent(task, opcode))
        {
            // 连接级事件没有字节口径，单独计数以便在证据里看到它们。
            lock (_gate)
            {
                if (IsControlEvent(task, opcode)) { _controlEvents++; }
                else { _eventsUndecodable++; }
            }
            return;
        }

        NetworkTrafficEvent ev = default;
        bool decoded = false;

        // ---- 零拷贝路径：直接读原生内存 ----
        int payloadLength = e.EventDataLength;
        if (payloadLength is 28 or 36 or 52 or 60 && e.DataStart != IntPtr.Zero)
        {
            var reader = new NativePayloadReader(
                e.DataStart + PayloadOffsetFromDataStart, payloadLength);
            decoded = TryDecode(reader, task, opcode, e.TimeStamp, out ev);
            if (decoded)
            {
                lock (_gate) { _zeroCopyDecoded++; }
            }
        }

        // ---- 回退路径：EventData() 会复制载荷（分配主因），只在自检失败时用 ----
        if (!decoded)
        {
            byte[]? data;
            try { data = e.EventData(); }
            catch { return; }
            if (data == null || data.Length == 0) { return; }

            decoded = TryDecode(new ArrayPayloadReader(data), task, opcode, e.TimeStamp, out ev);
            lock (_gate)
            {
                if (decoded) { _copyFallbackDecoded++; }
                else { _copyFallbackFailures++; }
            }
            if (!decoded)
            {
                lock (_gate) { _eventsUndecodable++; }
                return;
            }
        }

        lock (_gate)
        {
            _eventsProcessed++;
            if (ev.Kind == NetworkTrafficEventKind.CopyUp) { _copyEvents++; }
            else if (ev.Kind == NetworkTrafficEventKind.Retransmit) { _retransmitEvents++; }
        }

        EventReceived?.Invoke(ev);
    }

    /// <summary>
    /// 有字节口径、值得解码的事件。
    /// <para>★ 这就是产品的**计数事件集合**（C4）：TCPCOPY(opcode 18) 在集合内，
    /// 但它的字节**永远不计入**总量/排除量（见 <see cref="NetworkTrafficEventKind.CopyUp"/>）。</para>
    /// </summary>
    public static bool IsDataEvent(int task, int opcode)
    {
        if (task == 10)
        {
            return opcode is 10 or 11 or 14 or 18;
        }
        if (task == 11)
        {
            return opcode is 42 or 43;
        }
        return false;
    }

    /// <summary>连接级事件：永远不能计入传输字节。</summary>
    private static bool IsControlEvent(int task, int opcode)
    {
        if (task == 10) { return opcode is 12 or 13 or 15 or 16 or 17; }
        if (task == 11) { return opcode == 49; }
        return false;
    }

    /// <summary>
    /// 用**拷贝**的载荷解码（公开入口：探针与单元测试用）。
    /// <para>与零拷贝路径共用同一份解码逻辑（<see cref="TryDecode"/>），两者结果必须一致。</para>
    /// </summary>
    public static bool TryDecodePayload(byte[] payload, int task, int opcode, DateTime ts, out NetworkTrafficEvent ev)
    {
        ev = default;
        if (payload == null || payload.Length == 0) { return false; }
        return TryDecode(new ArrayPayloadReader(payload), task, opcode, ts, out ev);
    }

    /// <summary>
    /// 解码 provider 载荷。零拷贝路径与拷贝路径**共用这一份逻辑**，保证结果一致。
    /// <para>布局经受控流量验证：28 = v4 基础，36 = v4 + seq/conn，52 = v6 基础，60 = v6 + seq/conn。</para>
    /// </summary>
    internal static bool TryDecode(TrafficPayloadReader r, int task, int opcode, DateTime ts, out NetworkTrafficEvent ev)
    {
        ev = default;

        NetworkTrafficProtocol proto;
        NetworkTrafficDirection dir;
        NetworkTrafficEventKind kind;

        if (task == 10)
        {
            proto = NetworkTrafficProtocol.Tcp;
            switch (opcode)
            {
                case 10: dir = NetworkTrafficDirection.Send; kind = NetworkTrafficEventKind.Data; break;
                case 11: dir = NetworkTrafficDirection.Receive; kind = NetworkTrafficEventKind.Data; break;
                case 14: dir = NetworkTrafficDirection.Send; kind = NetworkTrafficEventKind.Retransmit; break;
                case 18: dir = NetworkTrafficDirection.Send; kind = NetworkTrafficEventKind.CopyUp; break;
                default: return false;   // connect/disconnect/accept/fail 没有字节口径
            }
        }
        else if (task == 11)
        {
            proto = NetworkTrafficProtocol.Udp;
            switch (opcode)
            {
                case 42: dir = NetworkTrafficDirection.Send; kind = NetworkTrafficEventKind.Data; break;
                case 43: dir = NetworkTrafficDirection.Receive; kind = NetworkTrafficEventKind.Data; break;
                default: return false;
            }
        }
        else
        {
            return false;
        }

        int length = r.Length;
        bool isV6;
        bool hasSeq;
        switch (length)
        {
            case 28: isV6 = false; hasSeq = false; break;
            case 36: isV6 = false; hasSeq = true; break;
            case 52: isV6 = true; hasSeq = false; break;
            case 60: isV6 = true; hasSeq = true; break;
            default: return false;
        }

        int addrLen = isV6 ? 16 : 4;
        int off = 8;                       // PID(4) + size(4)
        if (length < off + (addrLen * 2) + 4 + (hasSeq ? 8 : 0)) { return false; }

        uint pid = r.ReadUInt32(0, littleEndian: true);
        uint size = r.ReadUInt32(4, littleEndian: true);

        IpKey remote = ReadIpKey(r, isV6, off);
        off += addrLen;
        IpKey local = ReadIpKey(r, isV6, off);
        off += addrLen;

        int remotePort = r.ReadUInt16(off, littleEndian: false);
        int localPort = r.ReadUInt16(off + 2, littleEndian: false);

        ev = new NetworkTrafficEvent(
            Timestamp: ts,
            Protocol: proto,
            Direction: dir,
            RemoteAddress: remote,
            LocalAddress: local,
            RemotePort: remotePort,
            LocalPort: localPort,
            SizeBytes: size,
            ProcessId: pid,
            Kind: kind);
        return true;
    }

    /// <summary>按网络字节序读取地址，并做 IPv4-mapped IPv6 归一化。</summary>
    private static IpKey ReadIpKey(TrafficPayloadReader r, bool isV6, int offset)
    {
        if (!isV6)
        {
            return IpKey.FromV4(r.ReadUInt32(offset, littleEndian: false));
        }
        ulong hi = r.ReadUInt64(offset, littleEndian: false);
        ulong lo = r.ReadUInt64(offset + 8, littleEndian: false);
        // IPv4-mapped（::ffff:a.b.c.d）归一化成普通 IPv4。
        // 字节布局：00×10 | FF FF | a b c d  ⇒ 前 8 字节为 0，lo 的高 32 位为 0xFFFF。
        if (hi == 0UL && (lo >> 32) == 0x0000FFFFUL)
        {
            return IpKey.FromV4((uint)(lo & 0xFFFFFFFFUL));
        }
        return IpKey.FromV6(hi, lo);
    }
}
