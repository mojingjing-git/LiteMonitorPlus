using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;

namespace LiteMonitor.SystemServices.Pipe;

/// <summary>客户端侧指标（A/B 对照用：首样延迟 / 接收延迟 / 序号缺口 / 迟到响应）。</summary>
public sealed record PipeClientMetrics(
    long CreatedTickMs,
    long? FirstSnapshotDelayMs,
    long SnapshotCount,
    long SequenceGaps,
    long LateResponses,
    long CommandTimeouts,
    int SessionCount,
    int RestartCount,
    double? PublishToReceiveP50Ms,
    double? PublishToReceiveP95Ms,
    double? PublishToReceiveMaxMs,
    double? SampleToReceiveP95Ms,
    long SnapshotBatchGaps = 0,
    long StaleSessionResponses = 0);

/// <summary>
/// P-I2 / Plan A §4：IHardwareClient 的管道实现。主程序侧持有监督器（启动/监督/重启/回收
/// HardwareHost），经随机会话管道取完整快照：接收循环对快照缓存做**原子替换**（引用交换，
/// 读侧永不撕裂）；命令带请求 ID/超时/合并语义（同操作并发请求合并为同一在飞任务；客户端
/// 超时不等于底层操作已取消；迟到应答按未知请求 ID 丢弃并计数）；断线后拒绝新操作
/// （快照仍可读，过期标 Stale）。TTL 采用接收侧单调钟 + 传来的样本时间戳（TickCount64 为
/// 系统级单调钟，跨进程可比）；首次收到的已过期样本不得置 Fresh（§5.3）。
///
/// <para><b>E02（完善计划 §6）命令面</b>：<see cref="ApplyConfigAsync"/>（应答/超时/取消，
/// 相同 revision+内容可合并，不同版本排队或被明确取代）、<see cref="RequestSamplingAsync"/>
/// （=到期采样，同周期合并）、<see cref="RequestFullRescanAsync"/>（独立操作 ID + 有界 +
/// HardwareOperationResult 状态机）、<see cref="SubmitDemand(string, SamplingDemand)"/> /
/// <see cref="ReleaseDemand"/>（按消费者登记/释放）。<b>超时 ≠ 底层取消</b>：超时的非幂等
/// 操作保留有界操作记录，新请求取得明确 busy/operationId，不重复发起底层非幂等操作；
/// <b>一个加入者取消不取消共享操作</b>（取消只作用于各自等待）。重连后重放最新期望配置与
/// 有效需求；旧会话应答不能完成新命令（会话令牌校验）；Host 重启/重连时 SessionEpoch 递增，
/// 旧会话快照数据不混入新会话。</para>
/// </summary>
public sealed class PipeHardwareClient : IHardwareClient
{
    /// <summary>待应答表容量（超过即显式拒绝，不无限增长）。</summary>
    private const int MaxPendingCommands = 64;

    /// <summary>需求消费者数量上限（同消费者的最新值覆盖旧值，不同消费者互不影响）。</summary>
    private const int MaxDemandConsumers = 64;

    /// <summary>需求命令的独立期限（记录型命令，不拖住调用方）。</summary>
    private static readonly TimeSpan DemandTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 命令等待的兜底宽限：超时守护是传输期限的唯一裁决者，宽限只用于"守护未按时醒来"
    /// 时保证等待仍然**有界**（不得改为无限等待）。
    /// </summary>
    private static readonly TimeSpan CommandTimeoutGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>显式操作轮询间隔（超时调用方凭 OperationId 后询）。</summary>
    private static readonly TimeSpan OperationPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<string?>? _fallbackNetworkIp;
    private readonly int _refreshMs;
    private readonly TimeSpan _commandTimeout;
    private readonly long _createdTick = Environment.TickCount64;

    private readonly HardwareHostSupervisor _supervisor;
    private readonly object _pendingLock = new();
    private readonly Dictionary<long, PendingCommand> _pending = new();
    private readonly Dictionary<string, Task<IpcResponseMessage>> _inFlightByOp = new(StringComparer.Ordinal);
    private long _requestIds;

    // 快照缓存：不可变记录 + 引用原子替换；StaleCopy 缓存降级副本避免每次读重建。
    private volatile IpcSnapshotMessage? _latest;
    private sealed class StaleCacheEntry
    {
        public readonly long BatchId;
        public readonly HardwareSnapshot Downgraded;
        public StaleCacheEntry(long batchId, HardwareSnapshot downgraded) { BatchId = batchId; Downgraded = downgraded; }
    }
    private volatile StaleCacheEntry? _staleCopy;
    private long _firstValidTick;
    private long? _firstSnapshotDelayMs;
    private volatile bool _firstValidSeen;

    // 指标
    private long _snapshotCount;
    private long _sequenceGaps;
    private long _snapshotBatchGaps;
    private long _lateResponses;
    private long _commandTimeouts;
    private long _staleSessionResponses;
    private long _commandsSent;
    private long _lastSequence = -1;
    private long _lastBatchId = -1;
    private int _snapshotEpoch;
    // R8：延迟样本改固定环形窗口（旧 List 只增不淘汰，长跑无界增长且 ReadMetrics 全量复制排序）。
    private readonly LatencyWindow _publishToReceive = new(LatencyWindow.DefaultCapacity);
    private readonly LatencyWindow _sampleToReceive = new(LatencyWindow.DefaultCapacity);
    private readonly string? _metricsPath;
    private long _metricsWriteTick;

    private volatile bool _disposed;
    private volatile IpcHeartbeatMessage? _lastHeartbeat;

    // ── E02 命令面状态 ──
    private readonly object _stateLock = new();
    private SamplingConfig? _desiredConfig;                       // 最新期望配置（重连重放用）
    private int? _appliedRevision;                                // 已被 Host 确认生效的 revision
    private readonly Dictionary<string, SamplingDemand> _demands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _demandOutcomes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _demandCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SamplingDemand> _pendingDemandSends = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingDemandReleases = new(StringComparer.Ordinal);
    private SamplingDemand? _pendingLegacyDemand;
    private bool _demandPumpRunning;
    private readonly SemaphoreSlim _demandSignal = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private readonly object _applyLock = new();
    private ApplySlot? _applyInFlight;
    private ApplySlot? _applyPending;
    private bool _applyWorkerRunning;

    private readonly object _operationLock = new();
    private readonly PipeOperationRegistry _operations = new();
    private string? _activeRescanLocalId;

    private readonly object _replayLock = new();
    private bool _replayRunning;
    private bool _replayPending;

    /// <param name="refreshMs">采样周期（随会话传给 Host；TTL 计算基准）。</param>
    /// <param name="fallbackNetworkIp">网络 IP 回退（最小采样器无网络面，进程内现状维持）。</param>
    /// <param name="supervisorOptions">监督参数/测试注入（Host 路径、故障参数等）。</param>
    /// <param name="metricsPath">指标落盘路径（默认读环境变量 LITEMONITOR_I2_METRICS；A/B 采集用）。</param>
    public PipeHardwareClient(int refreshMs,
        Func<string?>? fallbackNetworkIp = null,
        HardwareHostSupervisorOptions? supervisorOptions = null,
        string? metricsPath = null)
    {
        _refreshMs = Math.Max(50, refreshMs);
        _fallbackNetworkIp = fallbackNetworkIp;
        _metricsPath = metricsPath
            ?? (Environment.GetEnvironmentVariable("LITEMONITOR_I2_METRICS") is { Length: > 0 } p ? p : null);
        var options = (supervisorOptions ?? new HardwareHostSupervisorOptions()) with { RefreshMs = _refreshMs };
        _commandTimeout = options.CommandTimeout;
        _supervisor = new HardwareHostSupervisor(options);
        _supervisor.MessageReceived += OnMessage;
        _supervisor.SessionClosed += OnSessionClosed;
        _supervisor.SessionOpened += OnSessionOpened;
        _supervisor.Start();
    }

    // ── 接收循环回调（监督器读线程）──────────────────────────────────────────

    private void OnMessage(IpcEnvelope envelope)
    {
        // R9：帧序号在统一接收入口跟踪（心跳/应答/快照共用一个方向序号——
        // 只在快照里跟踪会把正常心跳误计为缺口）。
        long seq = envelope.Header.Sequence;
        long last = Interlocked.Read(ref _lastSequence);
        if (last >= 0 && seq > last + 1)
            Interlocked.Add(ref _sequenceGaps, seq - last - 1);
        Interlocked.Exchange(ref _lastSequence, seq);

        switch (envelope.Header.Type)
        {
            case IpcProtocol.TypeSnapshot:
                HandleSnapshot(envelope);
                break;
            case IpcProtocol.TypeHeartbeat:
                _lastHeartbeat = envelope.PayloadAs<IpcHeartbeatMessage>();
                break;
            case IpcProtocol.TypeResponse:
                HandleResponse(envelope);
                break;
        }
    }

    private void HandleSnapshot(IpcEnvelope envelope)
    {
        IpcSnapshotMessage message = envelope.PayloadAs<IpcSnapshotMessage>();
        long now = Environment.TickCount64;

        // E02 §6.7：会话纪元。Host 重启/重连时纪元递增——旧会话快照数据不得混入新会话。
        int epoch = message.Snapshot.SessionEpoch;
        int current = Volatile.Read(ref _snapshotEpoch);
        if (epoch > 0)
        {
            if (epoch < current)
            {
                // 旧纪元的迟到快照：丢弃，不混入当前会话。
                Interlocked.Increment(ref _staleSessionResponses);
                return;
            }
            if (epoch > current)
            {
                Interlocked.Exchange(ref _snapshotEpoch, epoch);
                _staleCopy = null; // 代次切换：旧降级副本失效
            }
        }

        // R9：采样批次缺口按 BatchId 另行统计（快照槽位合并/丢批 ≠ 帧序号缺口）。
        long batchId = message.Snapshot.BatchId;
        long lastBatch = Interlocked.Read(ref _lastBatchId);
        if (lastBatch >= 0 && batchId > lastBatch + 1)
            Interlocked.Add(ref _snapshotBatchGaps, batchId - lastBatch - 1);
        Interlocked.Exchange(ref _lastBatchId, batchId);

        // 首个有效样本：已过期则不置 Fresh、不作为首样（继续等新批次，§5.3）。
        long age = (long)(now - message.Snapshot.TimestampMonoMs);
        bool fresh = age <= StaleThresholdMs && now - message.PublishTickMs <= StaleThresholdMs;
        if (!_firstValidSeen)
        {
            if (!fresh) return;
            _firstValidTick = now;
            _firstSnapshotDelayMs = now - _createdTick;
            _firstValidSeen = true;
        }

        _latest = message; // 原子替换（引用写）
        _staleCopy = null; // 新批次使旧降级副本失效
        Interlocked.Increment(ref _snapshotCount);

        _publishToReceive.Add(now - message.PublishTickMs);
        _sampleToReceive.Add(age);
        MaybeWriteMetrics(now);
    }

    private void HandleResponse(IpcEnvelope envelope)
    {
        IpcResponseMessage response = envelope.PayloadAs<IpcResponseMessage>();
        PendingCommand? pending;
        lock (_pendingLock)
        {
            if (!_pending.TryGetValue(envelope.Header.RequestId, out pending))
            {
                _lateResponses++; // 迟到响应（调用方已超时/会话已换）：按未知请求丢弃且不破坏后续配对
                return;
            }
            // 会话校验（E02 §6.7）：**旧会话的应答不得完成新命令**。
            if (!string.Equals(pending.SessionToken, envelope.Header.SessionId, StringComparison.Ordinal))
            {
                string currentToken = _supervisor.CurrentSessionToken;
                if (!string.Equals(currentToken, envelope.Header.SessionId, StringComparison.Ordinal))
                {
                    _staleSessionResponses++; // 旧会话应答：丢弃（不完成 pending）
                    return;
                }
                // 发送期间会话刚好切换：命令实际落在当前会话上——对齐后接受。
                pending.SessionToken = currentToken;
            }
            _pending.Remove(envelope.Header.RequestId);
        }
        pending.Completion.TrySetResult(response);
    }

    private void OnSessionOpened(ConnectedHost host, string sessionToken, int hostPid)
    {
        // 重连重放（E02 §6.7）：新会话建立后重放最新期望配置与有效需求。
        ScheduleReplay();
    }

    private void OnSessionClosed(string reason)
    {
        lock (_pendingLock)
        {
            foreach (KeyValuePair<long, PendingCommand> kvp in _pending)
                kvp.Value.Completion.TrySetException(new InvalidOperationException($"host-session-ended:{reason}"));
            _pending.Clear();
            _inFlightByOp.Clear();
            Interlocked.Exchange(ref _lastSequence, -1); // 新会话序号重新基准化
            // E00-F2：会话切换同样重置**批次基准**——否则两代会话的批次轴混在一起算缺口
            //（新会话批次号低于上一会话高水位期间真实丢批少计）。
            Interlocked.Exchange(ref _lastBatchId, -1);
        }
        // 旧会话快照数据不得混入新会话：清空缓存（读取返回明确的空快照，直到新会话首批数据）。
        _latest = null;
        _staleCopy = null;
        Interlocked.Exchange(ref _snapshotEpoch, 0);
        // 会话结束 ⇒ Host 进程退出 ⇒ 底层显式执行已终止：在途操作以失败收口（不悬空）。
        FailActiveOperations($"host-session-ended:{reason}");
    }

    // ── IHardwareClient（快照面）──────────────────────────────────────────────

    public HardwareSnapshot GetLatestSnapshot()
    {
        ThrowIfDisposed();
        IpcSnapshotMessage? latest = _latest;
        if (latest is null)
            return new HardwareSnapshot(0, 0, -1, Array.Empty<MetricSample>(), TimeSpan.Zero, 0);

        long age = (long)(Environment.TickCount64 - latest.Snapshot.TimestampMonoMs);
        if (age <= StaleThresholdMs)
            return latest.Snapshot;

        // 过期：质量降级为 Stale（值保留，TTL 不重置）；按批次缓存降级副本。
        if (_staleCopy is { } cached && cached.BatchId == latest.Snapshot.BatchId)
            return cached.Downgraded;
        HardwareSnapshot downgraded = latest.Snapshot with
        {
            Samples = latest.Snapshot.Samples
                .Select(s => s.Quality == SampleQuality.Good ? s with { Quality = SampleQuality.Stale } : s)
                .ToArray(),
        };
        _staleCopy = new StaleCacheEntry(latest.Snapshot.BatchId, downgraded);
        return downgraded;
    }

    public async Task<HardwareCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpGetCatalog, null,
                null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (!response.Ok || response.ResultJson is null)
                return new HardwareCatalog(-1, Array.Empty<HardwareCatalogEntry>());
            return JsonSerializer.Deserialize<HardwareCatalog>(response.ResultJson)
                ?? new HardwareCatalog(-1, Array.Empty<HardwareCatalogEntry>());
        }
        catch (InvalidOperationException)
        {
            return new HardwareCatalog(-1, Array.Empty<HardwareCatalogEntry>()); // 断线拒绝新操作
        }
    }

    public HardwareHealth GetHealth()
    {
        ThrowIfDisposed();
        IpcHeartbeatMessage? heartbeat = _lastHeartbeat;
        HardwareState state = _supervisor.State == HardwareState.Ready && _latest is null
            ? HardwareState.Starting
            : _supervisor.State;
        string? reason = _supervisor.StateReason ?? heartbeat?.DiagnosticReason;
        if (state == HardwareState.Ready && _latest is IpcSnapshotMessage latest &&
            Environment.TickCount64 - latest.Snapshot.TimestampMonoMs > StaleThresholdMs)
            reason = $"snapshot-stale:{Environment.TickCount64 - latest.Snapshot.TimestampMonoMs}ms";
        return new HardwareHealth(
            State: state,
            CurrentOperation: heartbeat?.CurrentOperation,
            ProgressSeq: Math.Max(_supervisor.SessionCount, heartbeat?.ProgressSeq ?? 0),
            DiagnosticReason: reason);
    }

    public string? GetNetworkIP()
    {
        ThrowIfDisposed();
        // 最小采样器无网络采集面：回退进程内实现（I2 已知边界，报告记录）。
        return _fallbackNetworkIp?.Invoke();
    }

    // ── IHardwareClient（命令面：E02）─────────────────────────────────────────

    /// <summary>
    /// 应用采集配置（异步）：返回实际生效 revision 或明确失败。
    /// <para>合并语义：**仅相同 revision + 相同内容**可合并（同内容并发调用共享一次底层应用）；
    /// 不同版本排队执行，或在尚未开始时被新版本**明确取代**（被取代方收到可解释的
    /// "superseded-by-revision:N"，不静默丢配置）。</para>
    /// <para>超时语义：调用方超时**不等于**底层应用被取消（底层继续执行，Host 侧生效版本
    /// 以应答确认者为准）；取消同理只作用于本调用方的等待。</para>
    /// </summary>
    public async Task<SamplingApplyResult> ApplyConfigAsync(SamplingConfig config, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (config is null) throw new ArgumentNullException(nameof(config));

        string content = JsonSerializer.Serialize(config);
        string contentKey = config.Revision.ToString(CultureInfo.InvariantCulture) + ":" + content;

        ApplySlot slot;
        lock (_applyLock)
        {
            // 记录最新期望配置（会话重连后据此重放）。
            lock (_stateLock) { _desiredConfig = config; }

            if (_applyInFlight is { } inflight && !inflight.Completion.Task.IsCompleted && inflight.ContentKey == contentKey)
            {
                slot = inflight; // 相同 revision/内容 ⇒ 合并到在途应用
            }
            else if (_applyPending is { } pending && !pending.Completion.Task.IsCompleted && pending.ContentKey == contentKey)
            {
                slot = pending; // 与排队中的同内容 ⇒ 合并
            }
            else
            {
                if (_applyPending is { } superseded && !superseded.Completion.Task.IsCompleted)
                {
                    // 明确取代（不静默丢弃）：被取代方拿到可解释原因。
                    superseded.Completion.TrySetResult(new SamplingApplyResult(
                        Applied: false, Revision: null,
                        FailureReason: $"superseded-by-revision:{config.Revision}"));
                }
                slot = new ApplySlot { Config = config, ContentKey = contentKey };
                _applyPending = slot;
                EnsureApplyWorkerRunning();
            }
        }

        try
        {
            return await slot.Completion.Task.WaitAsync(_commandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 超时 ≠ 底层取消：应用可能仍在 Host 侧执行（记录保留）。
            return new SamplingApplyResult(Applied: false, Revision: null,
                FailureReason: $"timeout:{_commandTimeout.TotalMilliseconds:0}ms");
        }
    }

    /// <summary>
    /// 请求"到期才采"的一次采样（共享唯一协调器，同一采样周期合并）。
    /// 返回值 = 是否确实触发了（含合并到的）一轮采样。
    /// </summary>
    public async Task<bool> RequestSamplingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpSamplingDue, null,
                "sampling-due", _commandTimeout, cancellationToken).ConfigureAwait(false);
            if (!response.Ok || response.ResultJson is null) return false;
            SamplingDueResult? result =
                JsonSerializer.Deserialize<SamplingDueResult>(response.ResultJson, PipeCommandJson.Options);
            return result?.Triggered ?? false;
        }
        catch (InvalidOperationException)
        {
            return false; // 断线拒绝新操作
        }
        catch (TimeoutException)
        {
            return false; // 超时≠底层已取消
        }
    }

    /// <summary>
    /// 显式全量重扫（独立操作 ID、有界、状态可查）。
    /// <para>并发调用**共享同一操作**（加入在途操作）；一个加入者取消不取消共享操作；
    /// 调用方超时**不等于**底层停止——返回明确的结果（含 OperationId 与"仍在执行"状态），
    /// 后续新请求据此取得同一 operationId（busy），**不重复发起底层重扫**。</para>
    /// </summary>
    public async Task<HardwareOperationResult> RequestFullRescanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        PipeOperationRegistry.Record record;
        bool created;
        lock (_operationLock)
        {
            _operations.Prune();
            if (_activeRescanLocalId is { } activeId
                && _operations.Find(activeId) is { } active && !active.IsTerminal)
            {
                record = active;   // 加入在途操作（不重复发起）
                created = false;
            }
            else
            {
                string localId = "rescan-" + Guid.NewGuid().ToString("N");
                PipeOperationRegistry.Record? created0 = _operations.TryCreate(localId);
                if (created0 is null)
                {
                    // 容量超限：可解释拒绝（不静默丢弃）。
                    return HardwareOperationResult.Reject("operation-capacity-exceeded");
                }
                record = created0;
                _activeRescanLocalId = localId;
                created = true;
            }
        }

        if (created)
        {
            try
            {
                // 受理请求属于**共享操作**的建立过程：不受单个调用方取消影响
                // （取消只作用于各自的等待阶段）。
                IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpRescanFull, null,
                    null, _commandTimeout, CancellationToken.None).ConfigureAwait(false);
                if (!response.Ok || response.ResultJson is null)
                {
                    var rejected = HardwareOperationResult.Reject(response.Error ?? "host-rejected");
                    CompleteOperation(record, rejected);
                    return rejected;
                }
                HardwareOperationResult? accepted =
                    JsonSerializer.Deserialize<HardwareOperationResult>(response.ResultJson, PipeCommandJson.Options);
                if (accepted is null || !accepted.Accepted || string.IsNullOrEmpty(accepted.OperationId))
                {
                    var rejected = accepted ?? HardwareOperationResult.Reject("empty-result");
                    CompleteOperation(record, rejected);
                    return rejected;
                }
                record.OperationId = accepted.OperationId;
                record.State = accepted.State;
                _ = Task.Run(() => PollOperationAsync(record));
            }
            catch (Exception ex)
            {
                var failed = new HardwareOperationResult(false, record.OperationId,
                    HardwareOperationState.Failed, $"not-connected:{ex.GetType().Name}");
                CompleteOperation(record, failed);
                return failed;
            }
        }

        try
        {
            return await record.Completion.Task.WaitAsync(_commandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 超时 ≠ 底层取消：操作仍在 Host 侧执行（记录保留，有界且可查）。
            if (string.IsNullOrEmpty(record.OperationId))
                return HardwareOperationResult.Reject("timeout:await-acceptance");
            return new HardwareOperationResult(true, record.OperationId,
                record.IsTerminal ? record.State : HardwareOperationState.Running,
                $"timeout:{_commandTimeout.TotalMilliseconds:0}ms");
        }
    }

    /// <summary>
    /// 查询显式操作的当前状态（IpcMessages: operation-status）。
    /// <para>超时调用方凭 <see cref="HardwareOperationResult.OperationId"/> 后询真实结局，
    /// **不重发非幂等操作**；未知/已过期的操作 ID 返回 State=Rejected("unknown-operation")。</para>
    /// </summary>
    public async Task<HardwareOperationResult> QueryOperationAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(operationId)) throw new ArgumentException("操作 ID 不能为空", nameof(operationId));
        IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpOperationStatus,
            JsonSerializer.Serialize(new OperationStatusArgs(operationId), PipeCommandJson.Options),
            null, _commandTimeout, cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.ResultJson is null)
            return HardwareOperationResult.Reject(response.Error ?? "host-rejected");
        return JsonSerializer.Deserialize<HardwareOperationResult>(response.ResultJson, PipeCommandJson.Options)
            ?? HardwareOperationResult.Reject("empty-result");
    }

    /// <summary>
    /// 按消费者身份登记/更新需求（同消费者保留最新值，不覆盖其他消费者）。
    /// 记录型命令：不拖住调用方（发送由有界的需求泵完成，最新值覆盖旧值）。
    /// </summary>
    public void SubmitDemand(string consumerId, SamplingDemand demand)
    {
        ThrowIfDisposed();
        if (consumerId is null) throw new ArgumentNullException(nameof(consumerId));
        if (demand is null) throw new ArgumentNullException(nameof(demand));
        lock (_stateLock)
        {
            if (!_demands.ContainsKey(consumerId) && _demands.Count >= MaxDemandConsumers)
                return; // 有界：消费者数量超限，不再接收新消费者（诊断见 GetLastDemandOutcome）
            _demands[consumerId] = demand;
            _pendingDemandSends[consumerId] = demand; // 同消费者只保留最新值（合并）
        }
        StartDemandPumpIfNeeded();
        SignalDemandPump();
    }

    /// <summary>
    /// 按消费者身份登记/更新需求（可等待版）：返回 Host 是否受理登记。
    /// 与 <see cref="SubmitDemand(string, SamplingDemand)"/> 的区别仅在"是否等待应答"。
    /// </summary>
    public async Task<bool> SubmitDemandAsync(string consumerId, SamplingDemand demand,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (consumerId is null) throw new ArgumentNullException(nameof(consumerId));
        if (demand is null) throw new ArgumentNullException(nameof(demand));
        lock (_stateLock)
        {
            if (!_demands.ContainsKey(consumerId) && _demands.Count >= MaxDemandConsumers)
            {
                _demandOutcomes[consumerId] = "rejected:demand-capacity-exceeded";
                return false;
            }
            _demands[consumerId] = demand;
        }
        try
        {
            IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpSubmitDemandV2,
                JsonSerializer.Serialize(new SubmitDemandArgs(consumerId, demand), PipeCommandJson.Options),
                null, DemandTimeout, cancellationToken).ConfigureAwait(false);
            bool registered = response.Ok
                && (JsonSerializer.Deserialize<DemandRegisterResult>(response.ResultJson ?? "",
                    PipeCommandJson.Options)?.Registered ?? false);
            RecordDemandOutcome(consumerId, registered ? "registered" : "rejected");
            return registered;
        }
        catch (Exception ex)
        {
            RecordDemandOutcome(consumerId, $"failed:{ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// 释放消费者需求；返回此前是否存在登记。
    /// <para>契约的同步形状不允许"等待 Host 应答"而不阻塞调用方（§2.4：UI 不得同步等待
    /// IPC），因此本实现**本地注销并立即返回**，释放命令由有界的需求泵异步送达；
    /// Host 侧的受理结果见 <see cref="GetLastDemandOutcome"/>。</para>
    /// </summary>
    public bool ReleaseDemand(string consumerId)
    {
        ThrowIfDisposed();
        if (consumerId is null) throw new ArgumentNullException(nameof(consumerId));
        bool existed;
        lock (_stateLock)
        {
            existed = _demands.Remove(consumerId);
            _pendingDemandSends.Remove(consumerId);
            if (existed) _pendingDemandReleases.Add(consumerId);
        }
        if (existed)
        {
            StartDemandPumpIfNeeded();
            SignalDemandPump();
        }
        return existed;
    }

    /// <summary>[兼容桥] 同步应用配置——不得在 UI 线程调用（既有调用方随 E03 迁移到 ApplyConfigAsync）。</summary>
    public SamplingApplyResult ApplyConfig(SamplingConfig config)
    {
        ThrowIfDisposed();
        if (config is null) throw new ArgumentNullException(nameof(config));
        try
        {
            IpcResponseMessage response = SendCommandAsync(IpcProtocol.OpApplyConfig,
                JsonSerializer.Serialize(config), null, _commandTimeout, CancellationToken.None)
                .GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<SamplingApplyResult>(response.ResultJson ?? "null")
                ?? new SamplingApplyResult(false, null, "empty-result");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
        {
            return new SamplingApplyResult(false, null, $"not-connected:{ex.GetType().Name}");
        }
    }

    /// <summary>[兼容桥] 单桶需求（无消费者身份）；走有界需求泵，不阻塞调用方。</summary>
    public void SubmitDemand(SamplingDemand demand)
    {
        ThrowIfDisposed();
        if (demand is null) throw new ArgumentNullException(nameof(demand));
        lock (_stateLock)
        {
            _pendingLegacyDemand = demand; // 旧消息语义不变（submit-demand），只保留最新值
        }
        StartDemandPumpIfNeeded();
        SignalDemandPump();
    }

    /// <summary>[兼容桥] 语义已收窄为"到期采样"；显式重扫请用 RequestFullRescanAsync。</summary>
    public async Task<bool> RequestRescanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpRescan, null,
                "rescan", _commandTimeout, cancellationToken).ConfigureAwait(false);
            return response.Ok;
        }
        catch (InvalidOperationException)
        {
            return false; // 断线拒绝新操作
        }
        catch (TimeoutException)
        {
            return false; // 超时≠底层已取消：调用方不得立即重开相同操作（并发调用已合并）
        }
    }

    // ── 诊断与观测（测试/报告取数）───────────────────────────────────────────

    /// <summary>当前快照会话纪元（Host 重启/重连时递增；0 = 尚无纪元信息）。</summary>
    public int CurrentSessionEpoch => Volatile.Read(ref _snapshotEpoch);

    /// <summary>已被 Host 应答确认生效的配置 revision（未确认则为 null）。</summary>
    public int? AppliedConfigRevision { get { lock (_stateLock) { return _appliedRevision; } } }

    /// <summary>被丢弃的旧会话应答数（旧会话应答不得完成新命令）。</summary>
    public long StaleSessionResponses => Interlocked.Read(ref _staleSessionResponses);

    /// <summary>当前在途显式操作的 Host 侧 OperationId（无则 null）。</summary>
    public string? ActiveRescanOperationId
    {
        get
        {
            lock (_operationLock)
            {
                return _activeRescanLocalId is { } id ? _operations.Find(id)?.OperationId : null;
            }
        }
    }

    /// <summary>待应答表条目数（有界诊断）。</summary>
    public int PendingCommandCount { get { lock (_pendingLock) { return _pending.Count; } } }

    /// <summary>
    /// 已发出的命令条数（合并语义的可观测面）：相同内容合并 ⇒ 条数不增；
    /// 不同版本各自下发 ⇒ 条数增加。
    /// </summary>
    public long CommandsSent => Interlocked.Read(ref _commandsSent);

    /// <summary>显式操作记录条数（有界诊断）。</summary>
    public int OperationRecordCount => _operations.Count;

    /// <summary>某消费者最近一次需求命令的结果（registered/released/rejected/failed:…）。</summary>
    public string? GetLastDemandOutcome(string consumerId)
    {
        lock (_stateLock) { return _demandOutcomes.TryGetValue(consumerId, out string? v) ? v : null; }
    }

    /// <summary>某消费者累计收到的登记次数（重连重放后计数增加 ⇒ 可观测重放确实发生）。</summary>
    public int GetDemandRegistrationCount(string consumerId)
    {
        lock (_stateLock) { return _demandCounts.TryGetValue(consumerId, out int v) ? v : 0; }
    }

    // ── 配置应用（串行 + 合并/取代）──────────────────────────────────────────

    private sealed class ApplySlot
    {
        public SamplingConfig Config = null!;
        public string ContentKey = "";
        public TaskCompletionSource<SamplingApplyResult> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EnsureApplyWorkerRunning()
    {
        lock (_applyLock)
        {
            if (_applyWorkerRunning) return;
            _applyWorkerRunning = true;
        }
        _ = Task.Run(ApplyWorkerAsync);
    }

    private async Task ApplyWorkerAsync()
    {
        try
        {
            while (true)
            {
                ApplySlot? slot;
                lock (_applyLock)
                {
                    slot = _applyPending;
                    if (slot is null)
                    {
                        _applyWorkerRunning = false;
                        return;
                    }
                    _applyPending = null;
                    _applyInFlight = slot;
                }

                SamplingApplyResult result;
                try
                {
                    IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpApplyConfig,
                        JsonSerializer.Serialize(slot.Config), null, _commandTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (response.Ok && response.ResultJson is not null)
                    {
                        result = JsonSerializer.Deserialize<SamplingApplyResult>(response.ResultJson,
                            PipeCommandJson.Options) ?? new SamplingApplyResult(false, null, "empty-result");
                    }
                    else
                    {
                        result = new SamplingApplyResult(false, null, response.Error ?? "host-rejected");
                    }
                    // 只有实际应用版本被确认后，才把该版本视为当前生效 revision。
                    if (result.Applied && result.Revision is { } revision)
                    {
                        lock (_stateLock) { _appliedRevision = revision; }
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("capacity", StringComparison.Ordinal))
                {
                    // 有界拒绝：可解释原因直接透出（不静默丢配置）。
                    result = new SamplingApplyResult(false, null, ex.Message);
                }
                catch (Exception ex)
                {
                    result = new SamplingApplyResult(false, null, $"not-connected:{ex.GetType().Name}");
                }

                lock (_applyLock) { _applyInFlight = null; }
                slot.Completion.TrySetResult(result);
            }
        }
        catch
        {
            lock (_applyLock) { _applyWorkerRunning = false; }
        }
    }

    // ── 显式操作（轮询 + 有界记录）───────────────────────────────────────────

    private async Task PollOperationAsync(PipeOperationRegistry.Record record)
    {
        try
        {
            while (Environment.TickCount64 < record.DeadlineTick)
            {
                await Task.Delay(OperationPollInterval, _disposeCts.Token).ConfigureAwait(false);
                IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpOperationStatus,
                    JsonSerializer.Serialize(new OperationStatusArgs(record.OperationId), PipeCommandJson.Options),
                    null, _commandTimeout, _disposeCts.Token).ConfigureAwait(false);
                if (!response.Ok || response.ResultJson is null) continue;
                HardwareOperationResult? status =
                    JsonSerializer.Deserialize<HardwareOperationResult>(response.ResultJson, PipeCommandJson.Options);
                if (status is null) continue;
                if (status.State is HardwareOperationState.Completed or HardwareOperationState.Failed
                    or HardwareOperationState.Cancelled or HardwareOperationState.Rejected)
                {
                    CompleteOperation(record, status);
                    return;
                }
                record.State = status.State;
            }
            CompleteOperation(record, new HardwareOperationResult(true, record.OperationId,
                HardwareOperationState.Failed, "operation-deadline-exceeded"));
        }
        catch (OperationCanceledException)
        {
            CompleteOperation(record, new HardwareOperationResult(false, record.OperationId,
                HardwareOperationState.Cancelled, "client-disposed"));
        }
        catch (Exception ex)
        {
            CompleteOperation(record, new HardwareOperationResult(false, record.OperationId,
                HardwareOperationState.Failed, $"status-unavailable:{ex.GetType().Name}"));
        }
    }

    private void CompleteOperation(PipeOperationRegistry.Record record, HardwareOperationResult result)
    {
        _operations.Complete(record, result);
        lock (_operationLock)
        {
            if (_activeRescanLocalId == record.LocalId && record.IsTerminal)
                _activeRescanLocalId = null;
        }
    }

    private void FailActiveOperations(string reason)
    {
        lock (_operationLock)
        {
            if (_activeRescanLocalId is null) return;
            PipeOperationRegistry.Record? record = _operations.Find(_activeRescanLocalId);
            _activeRescanLocalId = null;
            if (record is null || record.IsTerminal) return;
            _operations.Complete(record, new HardwareOperationResult(false, record.OperationId,
                HardwareOperationState.Failed, reason));
        }
    }

    // ── 需求泵（有界、合并、不阻塞调用方）───────────────────────────────────

    private void StartDemandPumpIfNeeded()
    {
        lock (_stateLock)
        {
            if (_demandPumpRunning) return;
            _demandPumpRunning = true;
        }
        _ = Task.Run(DemandPumpAsync);
    }

    private void SignalDemandPump()
    {
        try { _demandSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task DemandPumpAsync()
    {
        try
        {
            while (true)
            {
                await _demandSignal.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                while (true)
                {
                    KeyValuePair<string, SamplingDemand>[] submits;
                    string[] releases;
                    SamplingDemand? legacy;
                    lock (_stateLock)
                    {
                        if (_pendingDemandSends.Count == 0 && _pendingDemandReleases.Count == 0
                            && _pendingLegacyDemand is null)
                        {
                            break;
                        }
                        submits = _pendingDemandSends.ToArray();
                        _pendingDemandSends.Clear();
                        releases = _pendingDemandReleases.ToArray();
                        _pendingDemandReleases.Clear();
                        legacy = _pendingLegacyDemand;
                        _pendingLegacyDemand = null;
                    }

                    if (legacy is not null)
                    {
                        try
                        {
                            await SendCommandAsync(IpcProtocol.OpSubmitDemand,
                                JsonSerializer.Serialize(legacy), null, DemandTimeout, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { /* 记录失败不影响主流程 */ }
                    }

                    foreach (KeyValuePair<string, SamplingDemand> kv in submits)
                    {
                        try
                        {
                            IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpSubmitDemandV2,
                                JsonSerializer.Serialize(new SubmitDemandArgs(kv.Key, kv.Value), PipeCommandJson.Options),
                                null, DemandTimeout, CancellationToken.None).ConfigureAwait(false);
                            bool registered = response.Ok
                                && (JsonSerializer.Deserialize<DemandRegisterResult>(response.ResultJson ?? "",
                                    PipeCommandJson.Options)?.Registered ?? false);
                            RecordDemandOutcome(kv.Key, registered ? "registered" : "rejected");
                        }
                        catch (Exception ex)
                        {
                            RecordDemandOutcome(kv.Key, $"failed:{ex.GetType().Name}");
                        }
                    }

                    foreach (string consumerId in releases)
                    {
                        try
                        {
                            IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpReleaseDemand,
                                JsonSerializer.Serialize(new ReleaseDemandArgs(consumerId), PipeCommandJson.Options),
                                null, DemandTimeout, CancellationToken.None).ConfigureAwait(false);
                            bool released = response.Ok
                                && (JsonSerializer.Deserialize<DemandReleaseResult>(response.ResultJson ?? "",
                                    PipeCommandJson.Options)?.Released ?? false);
                            RecordDemandOutcome(consumerId, released ? "released" : "released:not-registered");
                        }
                        catch (Exception ex)
                        {
                            RecordDemandOutcome(consumerId, $"failed:{ex.GetType().Name}");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_stateLock) { _demandPumpRunning = false; }
        }
    }

    private void RecordDemandOutcome(string consumerId, string outcome)
    {
        lock (_stateLock)
        {
            _demandOutcomes[consumerId] = outcome;
            if (outcome.StartsWith("registered", StringComparison.Ordinal))
                _demandCounts[consumerId] = _demandCounts.TryGetValue(consumerId, out int n) ? n + 1 : 1;
        }
    }

    // ── 重连重放（最新期望配置 + 有效需求）───────────────────────────────────

    private void ScheduleReplay()
    {
        lock (_replayLock)
        {
            if (_replayRunning)
            {
                _replayPending = true;
                return;
            }
            _replayRunning = true;
        }
        _ = Task.Run(ReplayLoopAsync);
    }

    private async Task ReplayLoopAsync()
    {
        try
        {
            while (true)
            {
                SamplingConfig? config;
                KeyValuePair<string, SamplingDemand>[] demands;
                lock (_stateLock)
                {
                    config = _desiredConfig;
                    demands = _demands.ToArray();
                }

                if (config is not null)
                {
                    try
                    {
                        IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpApplyConfig,
                            JsonSerializer.Serialize(config), null, _commandTimeout, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (response.Ok && response.ResultJson is not null)
                        {
                            SamplingApplyResult? applied = JsonSerializer.Deserialize<SamplingApplyResult>(
                                response.ResultJson, PipeCommandJson.Options);
                            if (applied is { Applied: true, Revision: { } revision })
                            {
                                lock (_stateLock) { _appliedRevision = revision; }
                            }
                        }
                    }
                    catch { /* 会话可能已断：下一轮重放再试 */ }
                }

                foreach (KeyValuePair<string, SamplingDemand> kv in demands)
                {
                    try
                    {
                        IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpSubmitDemandV2,
                            JsonSerializer.Serialize(new SubmitDemandArgs(kv.Key, kv.Value), PipeCommandJson.Options),
                            null, DemandTimeout, CancellationToken.None).ConfigureAwait(false);
                        bool registered = response.Ok
                            && (JsonSerializer.Deserialize<DemandRegisterResult>(response.ResultJson ?? "",
                                PipeCommandJson.Options)?.Registered ?? false);
                        if (registered) RecordDemandOutcome(kv.Key, "registered");
                    }
                    catch { /* 同上 */ }
                }

                lock (_replayLock)
                {
                    if (!_replayPending)
                    {
                        _replayRunning = false;
                        return;
                    }
                    _replayPending = false;
                }
            }
        }
        catch
        {
            lock (_replayLock) { _replayRunning = false; }
        }
    }

    // ── 命令通道：请求 ID 配对 + 同操作在飞合并 + 超时守护 ────────────────────

    /// <summary>待应答条目：完成源 + 发送时所属会话令牌（响应校验：旧会话应答不得完成新命令）。</summary>
    private sealed class PendingCommand
    {
        public PendingCommand(string operation, string sessionToken)
        {
            Operation = operation;
            SessionToken = sessionToken;
        }

        public string Operation { get; }
        public string SessionToken { get; set; }

        /// <summary>超时已计数标记（保证同一条命令只计一次，计数先于调用方返回）。</summary>
        public int TimeoutCounted;

        public TaskCompletionSource<IpcResponseMessage> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<IpcResponseMessage> SendCommandAsync(string operation, string? argumentJson,
        string? mergeKey, TimeSpan timeout, CancellationToken cancellationToken)
    {
        PendingCommand? pending = null;
        Task<IpcResponseMessage> shared;
        bool isNew;
        long requestId = 0;

        lock (_pendingLock)
        {
            if (mergeKey is not null
                && _inFlightByOp.TryGetValue(mergeKey, out Task<IpcResponseMessage>? existing)
                && !existing.IsCompleted)
            {
                shared = existing;   // 重复请求合并：等待同一在飞任务
                isNew = false;
            }
            else
            {
                if (_pending.Count >= MaxPendingCommands)
                    throw new InvalidOperationException("command-capacity-exceeded"); // 有界：显式拒绝
                requestId = ++_requestIds;
                Interlocked.Increment(ref _commandsSent);
                pending = new PendingCommand(operation, _supervisor.CurrentSessionToken);
                _pending[requestId] = pending;
                shared = pending.Completion.Task;
                if (mergeKey is not null) _inFlightByOp[mergeKey] = shared;
                isNew = true;
            }
        }

        if (isNew)
        {
            try
            {
                await _supervisor.SendAsync(new IpcEnvelope(
                    new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeCommand,
                        pending!.SessionToken, requestId, 0),
                    new IpcCommandMessage(operation, argumentJson)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RemovePending(requestId, mergeKey, shared);
                pending!.Completion.TrySetException(ex);
            }
            // 超时守护：到达期限仍未应答 ⇒ 移出配对表（随后到达的应答按迟到丢弃计数）。
            _ = WatchCommandDeadlineAsync(requestId, pending!, mergeKey, shared, timeout);
        }

        try
        {
            // 传输期限由超时守护统一裁决（守护到期即结算完成源并计数）；
            // 此处的额外宽限只是"守护未按时醒来"时的兜底，保证等待**有界**。
            return await shared.WaitAsync(timeout + CommandTimeoutGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (pending is not null) CountCommandTimeout(pending); // 兜底计数（守护已计则不重复）
            throw;
        }
        finally
        {
            if (isNew) RemovePending(requestId, mergeKey, shared);
        }
    }

    /// <summary>命令超时计数（同一条命令只计一次；先于调用方返回可见）。</summary>
    private void CountCommandTimeout(PendingCommand pending)
    {
        if (Interlocked.Exchange(ref pending.TimeoutCounted, 1) == 0)
            Interlocked.Increment(ref _commandTimeouts);
    }

    private void RemovePending(long requestId, string? mergeKey, Task<IpcResponseMessage> shared)
    {
        lock (_pendingLock)
        {
            _pending.Remove(requestId);
            if (mergeKey is not null
                && _inFlightByOp.TryGetValue(mergeKey, out Task<IpcResponseMessage>? t) && t == shared)
            {
                _inFlightByOp.Remove(mergeKey);
            }
        }
    }

    private async Task WatchCommandDeadlineAsync(long requestId, PendingCommand pending, string? mergeKey,
        Task<IpcResponseMessage> shared, TimeSpan timeout)
    {
        try
        {
            await shared.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 超时守护是传输期限的唯一裁决者：先计数（调用方返回时即可见），再移出配对表，
            // 最后以 TimeoutException 结算完成源（所有加入者同时收到"超时≠底层已取消"）。
            CountCommandTimeout(pending);
            RemovePending(requestId, mergeKey, shared);
            pending.Completion.TrySetException(new TimeoutException($"command-timeout:{pending.Operation}"));
        }
        catch { /* 已完成或已失败：无需处理 */ }
    }

    private long StaleThresholdMs => Math.Max(3_000, _refreshMs * 3);

    public PipeClientMetrics ReadMetrics()
    {
        double? p50 = null, p95 = null, max = null, s95 = null;
        long[] pub = _publishToReceive.SnapshotInOrder();
        long[] samp = _sampleToReceive.SnapshotInOrder();
        if (pub.Length > 0)
        {
            long[] sorted = pub.OrderBy(v => v).ToArray();
            p50 = Percentile(sorted, 0.50);
            p95 = Percentile(sorted, 0.95);
            max = sorted[^1];
        }
        if (samp.Length > 0)
            s95 = Percentile(samp.OrderBy(v => v).ToArray(), 0.95);
        return new PipeClientMetrics(
            _createdTick, _firstSnapshotDelayMs, Interlocked.Read(ref _snapshotCount),
            Interlocked.Read(ref _sequenceGaps), Interlocked.Read(ref _lateResponses), Interlocked.Read(ref _commandTimeouts),
            _supervisor.SessionCount, _supervisor.RestartCount, p50, p95, max, s95,
            Interlocked.Read(ref _snapshotBatchGaps),
            Interlocked.Read(ref _staleSessionResponses));
    }

    private static double Percentile(long[] sorted, double q)
    {
        int index = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private void MaybeWriteMetrics(long now)
    {
        if (_metricsPath is null || now - Interlocked.Read(ref _metricsWriteTick) < 5_000) return;
        Interlocked.Exchange(ref _metricsWriteTick, now);
        try
        {
            File.WriteAllText(_metricsPath, JsonSerializer.Serialize(ReadMetrics(), new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch { /* 指标落盘失败不影响采集 */ }
    }

    /// <summary>当前 Host 进程 PID（诊断/测试；无会话为 null）。</summary>
    public int? CurrentHostPid => _supervisor.CurrentHostPid;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _disposeCts.Cancel(); } catch { }
        try { _supervisor.Dispose(); } catch { }
        if (_metricsPath is not null)
        {
            try
            {
                File.WriteAllText(_metricsPath, JsonSerializer.Serialize(ReadMetrics(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// R8：固定容量环形延迟窗口。Add 只覆盖最旧样本（内存有界），SnapshotInOrder 返回
/// 插入序副本；总样本数由调用方独立计数（SnapshotCount），窗口只服务分位数。
/// </summary>
public sealed class LatencyWindow
{
    public const int DefaultCapacity = 512;

    private readonly long[] _buffer;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public LatencyWindow(int capacity = DefaultCapacity) => _buffer = new long[Math.Max(1, capacity)];

    public int Count { get { lock (_gate) return _count; } }
    public int Capacity => _buffer.Length;

    public void Add(long value)
    {
        lock (_gate)
        {
            _buffer[_next] = value;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    /// <summary>按插入序返回窗口内样本副本（调用方可自由排序/聚合）。</summary>
    public long[] SnapshotInOrder()
    {
        lock (_gate)
        {
            var result = new long[_count];
            int start = _count < _buffer.Length ? 0 : _next;
            for (int i = 0; i < _count; i++) result[i] = _buffer[(start + i) % _buffer.Length];
            return result;
        }
    }
}
