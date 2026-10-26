using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;

namespace LiteMonitor.HardwareHost;

/// <summary>
/// Host 侧管道会话（Plan A §5：主程序为管道服务端，本进程为客户端）。
/// 职责：握手 → 设备初始化（心跳报进度）→ 采样循环（快照槽位新替旧）→
/// 命令处理（逐命令有应答）→ 受控关闭。发送侧单写线程串行编码：控制响应/心跳优先，
/// 快照槽位只保留最新一批（§5.1 不无限积压）。测试故障注入由 <see cref="HostOptions.Fault"/> 驱动。
///
/// E02（完善计划 §6）新增命令面：
/// <list type="bullet">
/// <item><c>sampling-due</c>：到期采样请求（共享唯一采样协调器，同周期合并）。</item>
/// <item><c>rescan-full-v2</c>：显式全量重扫（独立操作 ID、有界、状态可查）。</item>
/// <item><c>submit-demand-v2</c> / <c>release-demand</c>：按消费者登记/释放需求。</item>
/// <item><c>operation-status</c>：查询显式操作状态（超时调用方凭 OperationId 后询）。</item>
/// </list>
/// </summary>
internal sealed class HostPipeSession
{
    private const int HeartbeatIntervalMs = 2000;

    /// <summary>需求登记表容量（消费者数量上限；超限给出可解释拒绝）。</summary>
    private const int MaxDemandConsumers = 64;

    private readonly HostOptions _options;
    private readonly string _fault;
    private readonly long[] _seq = new long[1];

    // 会话可变状态（单次运行生命周期内）：
    private readonly ControlQueue _control = new(capacity: 256);
    private readonly SemaphoreSlim _writeSignal = new(0, int.MaxValue);
    private IpcSnapshotMessage? _slot;
    private long _progressSeq;
    private int _refreshMs;
    private int _configRevision = -1;
    private volatile bool _stopping;
    private volatile bool _inShutdown;
    private bool _firstSnapshot = true;
    private MinimalHardwareSampler? _sampler;

    // ── E02 命令面状态 ──
    /// <summary>采样闸门：采样与显式重扫互斥（不并发触碰采集器）。</summary>
    private readonly object _sampleGate = new();
    /// <summary>到期采样请求信号（合并：多次请求只触发一轮采样）。</summary>
    private readonly SemaphoreSlim _sampleSignal = new(0, int.MaxValue);
    private long _sampleTickets;      // 已受理的到期采样请求票号
    private long _publishedTickets;   // 最近一次采样发布时覆盖到的票号
    private readonly HostOperationRegistry _operations = new();
    private readonly object _rescanGate = new();
    private string? _activeRescanId;
    private readonly object _demandGate = new();
    private readonly Dictionary<string, SamplingDemand> _demands = new(StringComparer.Ordinal);

    private HostPipeSession(HostOptions options, int refreshMs)
    {
        _options = options;
        _fault = options.Fault ?? "";
        _refreshMs = Math.Max(50, refreshMs);
    }

    public static async Task<int> RunAsync(HostOptions options)
    {
        var session = new HostPipeSession(options, options.RefreshMs);
        return await session.RunCoreAsync().ConfigureAwait(false);
    }

    private long NextSeq() => Interlocked.Increment(ref _seq[0]);

    private async Task<int> RunCoreAsync()
    {
        if (_fault == "fail-fast")
        {
            Environment.FailFast("i2-fault: fail-fast");
            return 1; // 不可达
        }

        if (_fault == "delay-handshake")
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);

        using var cts = new CancellationTokenSource();
        // Asynchronous：读循环与写循环并发使用同一双工流，句柄必须为异步重叠模式
        //（同步句柄的 sync-over-async 在并发读写下会卡死写侧）。
        using var pipe = new NamedPipeClientStream(".", _options.PipeName!, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return 10; // 连不上服务端（超时/不存在）
        }

        var reader = new IpcFrameReader(pipe);
        var writer = new IpcFrameWriter(pipe);
        string session = _options.SessionToken!;

        // ── 握手：hello → welcome。bad-version/bad-session 故障在此注入头部 ──
        int major = _fault == "bad-version" ? IpcProtocol.MajorVersion + 98 : IpcProtocol.MajorVersion;
        string helloSession = _fault == "bad-session" ? session + "-forged" : session;
        await writer.WriteAsync(new IpcEnvelope(
            new IpcHeader(major, IpcProtocol.MinorVersion, IpcProtocol.TypeHello, helloSession, 0, NextSeq()),
            new IpcHelloMessage(Environment.ProcessId, Environment.TickCount64, "1.0.0")), cts.Token).ConfigureAwait(false);

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
        IpcEnvelope? welcome;
        try
        {
            welcome = await reader.ReadAsync(handshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return 11; }
        catch (IpcProtocolException) { return 12; }
        if (welcome is null) return 11;
        if (!welcome.PayloadAs<IpcWelcomeMessage>().Accepted)
            return 13; // 服务端拒绝（版本/会话/身份不匹配）

        if (_fault == "crash-after-handshake")
        {
            Environment.FailFast("i2-fault: crash-after-handshake");
            return 1;
        }

        if (_fault == "protocol-violation")
        {
            // R2 探针同型：合法握手完成后发送非法长度帧（长度前缀 = -1），然后**保持存活**——
            // 监督器必须把会话判失败、回收子进程并按退避重启，而不是退出循环/误报 Ready。
            await pipe.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, cts.Token).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
        }

        // ── 初始化：先报 Starting（设备 Open 属初始化预算，§6.1 上限 60s）──
        await writer.WriteAsync(Heartbeat(HardwareState.Starting, "open-hardware"), cts.Token).ConfigureAwait(false);

        using var sampler = _sampler = new MinimalHardwareSampler(_options.TestPattern);

        // ── 采样线程：按周期产快照入槽位（新替旧）。hang-sampling 只冻结本线程，
        //    心跳继续 —— 对应监督器"采样无进展 10s"路径。──
        var samplingDone = Task.Run(async () =>
        {
            try
            {
                int published = 0;
                while (!_stopping)
                {
                    if (_fault == "hang-sampling" && published >= 2)
                        Thread.Sleep(Timeout.Infinite); // 无限阻塞采样线程（测试注入）
                    PublishSample();
                    published++;
                    // 等待下一次：周期到期，或被"到期采样"请求提前唤醒（合并为一轮采样）。
                    try
                    {
                        await _sampleSignal.WaitAsync(Math.Max(50, Volatile.Read(ref _refreshMs)), cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (ObjectDisposedException) { /* 关闭竞态 */ }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // ── 心跳任务：2s 一发。stop-heartbeat 故障静默停发（采样继续）→ 主程序"心跳 2s×3 失联"路径。──
        int heartbeatMs = Math.Max(100, _options.HeartbeatMs);
        var heartbeatDone = Task.Run(async () =>
        {
            bool frozen = _fault == "stop-heartbeat";
            while (!_stopping)
            {
                await Task.Delay(heartbeatMs, cts.Token).ConfigureAwait(false);
                if (_stopping || frozen) continue;
                if (!EnqueueControl(Heartbeat(HardwareState.Ready, null))) break; // 有界队列满：不积压
            }
        }, cts.Token);

        // ── 单写线程：控制优先 → 快照槽位（取最新一次，未发出的旧批次被新替旧）──
        var writeDone = Task.Run(async () =>
        {
            try
            {
                while (!_stopping || _control.Count > 0)
                {
                    await _writeSignal.WaitAsync(100, cts.Token).ConfigureAwait(false);
                    while (_control.TryTake(out IpcEnvelope? env))
                        await writer.WriteAsync(env, cts.Token).ConfigureAwait(false);
                    IpcSnapshotMessage? pending = Interlocked.Exchange(ref _slot, null);
                    if (pending is not null)
                        await writer.WriteAsync(SnapshotEnvelope(pending), cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }, cts.Token);

        // ── 命令读循环：逐命令应答（late-response 把 rescan 应答推迟 10s）──
        int exitCode = 0;
        try
        {
            while (!_stopping)
            {
                IpcEnvelope? envelope = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
                if (envelope is null) break; // 服务端关闭
                if (envelope.Header.SessionId != session)
                    throw new IpcProtocolException("命令会话令牌不匹配");
                if (envelope.Header.Type != IpcProtocol.TypeCommand)
                    throw new IpcProtocolException($"意外消息类型 {envelope.Header.Type}");
                if (envelope.Header.Major != IpcProtocol.MajorVersion)
                    throw new IpcProtocolException("命令主版本不匹配");

                var command = envelope.PayloadAs<IpcCommandMessage>();
                HandleCommand(command, envelope.Header.RequestId);
            }
        }
        catch (IpcProtocolException) { exitCode = 12; }
        catch (OperationCanceledException) { }
        catch (IOException) { }

        _stopping = true;
        ReleaseWriteSignal();
        try
        {
            await Task.WhenAll(writeDone, samplingDone, heartbeatDone).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }

        return _inShutdown ? 0 : exitCode;
    }

    /// <summary>采样发布（采样闸门内调用；快照带会话纪元与当前生效配置 revision）。</summary>
    private void PublishSample()
    {
        lock (_sampleGate)
        {
            PublishSampleLocked();
        }
    }

    private void PublishSampleLocked()
    {
        MinimalHardwareSampler? sampler = _sampler;
        if (sampler is null) return;
        HardwareSnapshot snapshot = sampler.SampleOnce(Volatile.Read(ref _configRevision), _options.SessionEpoch);
        if (_firstSnapshot && _options.StaleFirst)
            snapshot = snapshot with { TimestampMonoMs = Environment.TickCount64 - 30_000 };
        _firstSnapshot = false;
        Interlocked.Increment(ref _progressSeq);
        // 覆盖已受理的到期采样票号：等待该票号的调用方由此被唤醒（合并为一轮采样）。
        Volatile.Write(ref _publishedTickets, Interlocked.Read(ref _sampleTickets));
        Volatile.Write(ref _slot, new IpcSnapshotMessage(snapshot, Environment.TickCount64));
        ReleaseWriteSignal();
    }

    private void HandleCommand(IpcCommandMessage command, long requestId)
    {
        string session = _options.SessionToken!;
        switch (command.Operation)
        {
            case IpcProtocol.OpApplyConfig:
            {
                var config = JsonSerializer.Deserialize<SamplingConfig>(command.ArgumentJson ?? "")!;
                _refreshMs = Math.Max(50, config.RefreshMs);
                Volatile.Write(ref _configRevision, config.Revision);
                // 设备选择/探测策略在最小采样器中无对应面（无控制器/磁盘设备），接受周期与
                // revision、其余记为无操作 —— 与生产采集器的差异见 I2 报告指标集差异节。
                Respond(requestId, new IpcResponseMessage(true, null, JsonSerializer.Serialize(
                    new SamplingApplyResult(true, config.Revision, null))));
                break;
            }
            case IpcProtocol.OpSubmitDemand:
                Respond(requestId, new IpcResponseMessage(true, null, "true"));
                break;
            case IpcProtocol.OpRescan:
            {
                if (_fault == "late-response")
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        Respond(requestId, new IpcResponseMessage(true, null, "true"));
                    });
                }
                else
                {
                    Respond(requestId, new IpcResponseMessage(true, null, "true"));
                }
                break;
            }
            case IpcProtocol.OpGetCatalog:
            {
                HardwareCatalog catalog = (_sampler ?? new MinimalHardwareSampler(_options.TestPattern)).BuildCatalog();
                Respond(requestId, new IpcResponseMessage(true, null, JsonSerializer.Serialize(catalog)));
                break;
            }
            // ── E02 命令面 ──
            case IpcProtocol.OpSamplingDue:
            {
                bool triggered = RequestSamplingDue(TimeSpan.FromMilliseconds(
                    Math.Max(500, Math.Min(5000, Volatile.Read(ref _refreshMs) * 3))));
                Respond(requestId, new IpcResponseMessage(true, null,
                    JsonSerializer.Serialize(new SamplingDueResult(triggered), HostCommandJson.Options)));
                break;
            }
            case IpcProtocol.OpRescanFull:
            {
                HardwareOperationResult result = EnsureRescanOperation();
                Respond(requestId, new IpcResponseMessage(true, null, JsonSerializer.Serialize(result)));
                break;
            }
            case IpcProtocol.OpSubmitDemandV2:
            {
                SubmitDemandArgs? args = TryDeserialize<SubmitDemandArgs>(command.ArgumentJson);
                if (args is null || string.IsNullOrEmpty(args.ConsumerId))
                {
                    Respond(requestId, new IpcResponseMessage(false, "invalid-arguments", null));
                    break;
                }
                bool registered = RegisterDemand(args.ConsumerId, args.Demand);
                Respond(requestId, new IpcResponseMessage(registered, registered ? null : "demand-capacity-exceeded",
                    JsonSerializer.Serialize(new DemandRegisterResult(registered), HostCommandJson.Options)));
                break;
            }
            case IpcProtocol.OpReleaseDemand:
            {
                ReleaseDemandArgs? args = TryDeserialize<ReleaseDemandArgs>(command.ArgumentJson);
                if (args is null || string.IsNullOrEmpty(args.ConsumerId))
                {
                    Respond(requestId, new IpcResponseMessage(false, "invalid-arguments", null));
                    break;
                }
                bool released = ReleaseDemand(args.ConsumerId);
                Respond(requestId, new IpcResponseMessage(true, null,
                    JsonSerializer.Serialize(new DemandReleaseResult(released), HostCommandJson.Options)));
                break;
            }
            case IpcProtocol.OpOperationStatus:
            {
                OperationStatusArgs? args = TryDeserialize<OperationStatusArgs>(command.ArgumentJson);
                if (args is null || string.IsNullOrEmpty(args.OperationId))
                {
                    Respond(requestId, new IpcResponseMessage(false, "invalid-arguments", null));
                    break;
                }
                HardwareOperationResult status = _operations.TryGet(args.OperationId)
                    ?? HardwareOperationResult.Reject("unknown-operation");
                Respond(requestId, new IpcResponseMessage(true, null, JsonSerializer.Serialize(status)));
                break;
            }
            case IpcProtocol.OpShutdown:
                _inShutdown = true;
                Respond(requestId, new IpcResponseMessage(true, null, "true"));
                EnqueueControl(new IpcEnvelope(
                    new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeGoodbye, session, 0, NextSeq()),
                    new IpcGoodbyeMessage("shutdown")));
                _stopping = true;
                break;
            default:
                Respond(requestId, new IpcResponseMessage(false, $"unknown-operation:{command.Operation}", null));
                break;
        }
    }

    private static T? TryDeserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, HostCommandJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // ── E02 命令面实现 ───────────────────────────────────────────────────────

    /// <summary>
    /// 到期采样请求：向唯一采样协调器发信号并等待本票号被覆盖（合并：同周期多请求只采一轮）。
    /// 返回值 = 是否确实触发了（含合并到的）一轮采样。
    /// </summary>
    private bool RequestSamplingDue(TimeSpan waitBudget)
    {
        long ticket = Interlocked.Increment(ref _sampleTickets);
        try { _sampleSignal.Release(); } catch (SemaphoreFullException) { }
        long deadline = Environment.TickCount64 + (long)waitBudget.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (Volatile.Read(ref _publishedTickets) >= ticket) return true;
            Thread.Sleep(5);
        }
        return Volatile.Read(ref _publishedTickets) >= ticket;
    }

    /// <summary>
    /// 显式全量重扫：独立操作 ID + 状态机；在途操作可被重复请求加入（**不重复重扫**）；
    /// 容量超限返回可解释 Rejected。
    /// </summary>
    private HardwareOperationResult EnsureRescanOperation()
    {
        lock (_rescanGate)
        {
            if (_activeRescanId is { } active)
            {
                HardwareOperationResult? current = _operations.TryGet(active);
                if (current is not null
                    && current.State is HardwareOperationState.Queued or HardwareOperationState.Running)
                {
                    return current; // 加入在途操作
                }
            }
            string id = "rescan-" + Guid.NewGuid().ToString("N");
            if (!_operations.TryCreate(id, out _))
                return HardwareOperationResult.Reject("operation-capacity-exceeded");
            _activeRescanId = id;
            _ = Task.Run(() => RunRescanAsync(id));
            return _operations.TryGet(id) ?? HardwareOperationResult.Reject("operation-create-failed");
        }
    }

    private async Task RunRescanAsync(string id)
    {
        try
        {
            _operations.TrySetState(id, HardwareOperationState.Running, null);
            if (_fault == "slow-rescan")
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            lock (_sampleGate)
            {
                _sampler?.Rescan();
                PublishSampleLocked();
            }
            _operations.TrySetState(id, HardwareOperationState.Completed, null);
        }
        catch (Exception ex)
        {
            _operations.TrySetState(id, HardwareOperationState.Failed, ex.GetType().Name);
        }
    }

    /// <summary>按消费者登记需求（同消费者覆盖为最新值；不覆盖其他消费者）。</summary>
    private bool RegisterDemand(string consumerId, SamplingDemand demand)
    {
        lock (_demandGate)
        {
            if (!_demands.ContainsKey(consumerId) && _demands.Count >= MaxDemandConsumers)
                return false;
            _demands[consumerId] = demand;
            return true;
        }
    }

    /// <summary>释放某消费者的需求；返回此前是否存在登记。</summary>
    private bool ReleaseDemand(string consumerId)
    {
        lock (_demandGate)
        {
            return _demands.Remove(consumerId);
        }
    }

    private void Respond(long requestId, IpcResponseMessage response) =>
        EnqueueControl(new IpcEnvelope(
            new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeResponse,
                _options.SessionToken!, requestId, NextSeq()),
            response));

    private bool EnqueueControl(IpcEnvelope envelope)
    {
        if (!_control.TryAdd(envelope)) return false;
        ReleaseWriteSignal();
        return true;
    }

    private IpcEnvelope Heartbeat(HardwareState state, string? operation) => new(
        new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeHeartbeat,
            _options.SessionToken!, 0, NextSeq()),
        new IpcHeartbeatMessage(state, operation, Interlocked.Read(ref _progressSeq), null, Environment.TickCount64));

    private IpcEnvelope SnapshotEnvelope(IpcSnapshotMessage message) => new(
        new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeSnapshot,
            _options.SessionToken!, 0, NextSeq()),
        message);

    private void ReleaseWriteSignal()
    {
        try { _writeSignal.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>有界控制队列（心跳/应答优先通道）：满时丢弃新项（不无限积压，§5.1）。</summary>
    private sealed class ControlQueue
    {
        private readonly ConcurrentQueue<IpcEnvelope> _queue = new();
        private readonly int _capacity;
        private int _count;

        public ControlQueue(int capacity) => _capacity = capacity;

        public int Count => Volatile.Read(ref _count);

        public bool TryAdd(IpcEnvelope envelope)
        {
            if (Interlocked.Increment(ref _count) > _capacity)
            {
                Interlocked.Decrement(ref _count);
                return false;
            }
            _queue.Enqueue(envelope);
            return true;
        }

        public bool TryTake(out IpcEnvelope? envelope)
        {
            if (_queue.TryDequeue(out envelope))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }
            return false;
        }
    }
}
