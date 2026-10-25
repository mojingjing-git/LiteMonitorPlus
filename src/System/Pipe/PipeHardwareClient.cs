using System;
using System.Collections.Generic;
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
    long SnapshotBatchGaps = 0);

/// <summary>
/// P-I2 / Plan A §4：IHardwareClient 的管道实现。主程序侧持有监督器（启动/监督/重启/回收
/// HardwareHost），经随机会话管道取完整快照：接收循环对快照缓存做**原子替换**（引用交换，
/// 读侧永不撕裂）；命令带请求 ID/超时/合并语义（同操作并发请求合并为同一在飞任务；客户端
/// 超时不等于底层操作已取消；迟到应答按未知请求 ID 丢弃并计数）；断线后拒绝新操作
/// （快照仍可读，过期标 Stale）。TTL 采用接收侧单调钟 + 传来的样本时间戳（TickCount64 为
/// 系统级单调钟，跨进程可比）；首次收到的已过期样本不得置 Fresh（§5.3）。
/// </summary>
public sealed class PipeHardwareClient : IHardwareClient
{
    private readonly Func<string?>? _fallbackNetworkIp;
    private readonly int _refreshMs;
    private readonly TimeSpan _commandTimeout;
    private readonly long _createdTick = Environment.TickCount64;

    private readonly HardwareHostSupervisor _supervisor;
    private readonly object _pendingLock = new();
    private readonly Dictionary<long, TaskCompletionSource<IpcResponseMessage>> _pending = new();
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
    private long _lastSequence = -1;
    private long _lastBatchId = -1;
    // R8：延迟样本改固定环形窗口（旧 List 只增不淘汰，长跑无界增长且 ReadMetrics 全量复制排序）。
    private readonly LatencyWindow _publishToReceive = new(LatencyWindow.DefaultCapacity);
    private readonly LatencyWindow _sampleToReceive = new(LatencyWindow.DefaultCapacity);
    private readonly string? _metricsPath;
    private long _metricsWriteTick;

    private volatile bool _disposed;
    private volatile IpcHeartbeatMessage? _lastHeartbeat;

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
        lock (_pendingLock)
        {
            if (_pending.Remove(envelope.Header.RequestId, out TaskCompletionSource<IpcResponseMessage>? tcs))
                tcs.TrySetResult(response);
            else
                _lateResponses++; // 迟到响应（调用方已超时/会话已换）：按未知请求丢弃且不破坏后续配对
        }
    }

    private void OnSessionClosed(string reason)
    {
        lock (_pendingLock)
        {
            foreach (KeyValuePair<long, TaskCompletionSource<IpcResponseMessage>> kvp in _pending)
                kvp.Value.TrySetException(new InvalidOperationException($"host-session-ended:{reason}"));
            _pending.Clear();
            _inFlightByOp.Clear();
            _lastSequence = -1; // 新会话序号重新基准化
        }
    }

    // ── IHardwareClient ──────────────────────────────────────────────────────

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
                TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
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

    public SamplingApplyResult ApplyConfig(SamplingConfig config)
    {
        ThrowIfDisposed();
        if (config is null) throw new ArgumentNullException(nameof(config));
        try
        {
            IpcResponseMessage response = SendCommandAsync(IpcProtocol.OpApplyConfig,
                JsonSerializer.Serialize(config), _commandTimeout, CancellationToken.None)
                .GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<SamplingApplyResult>(response.ResultJson ?? "null")
                ?? new SamplingApplyResult(false, null, "empty-result");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
        {
            return new SamplingApplyResult(false, null, $"not-connected:{ex.GetType().Name}");
        }
    }

    public void SubmitDemand(SamplingDemand demand)
    {
        ThrowIfDisposed();
        if (demand is null) throw new ArgumentNullException(nameof(demand));
        // 记录型操作：尽力发送，超时/断线不阻塞调用方（与进程内适配器 V1 记录语义一致）。
        _ = Task.Run(async () =>
        {
            try
            {
                await SendCommandAsync(IpcProtocol.OpSubmitDemand, JsonSerializer.Serialize(demand),
                    TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* 记录失败不影响主流程 */ }
        });
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

    public async Task<bool> RequestRescanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            IpcResponseMessage response = await SendCommandAsync(IpcProtocol.OpRescan, null,
                _commandTimeout, cancellationToken).ConfigureAwait(false);
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

    // ── 命令通道：请求 ID 配对 + 同操作在飞合并 + 超时守护 ────────────────────

    private async Task<IpcResponseMessage> SendCommandAsync(string operation, string? argumentJson,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IpcResponseMessage>? tcs = null;
        Task<IpcResponseMessage> task;
        long requestId = 0;
        bool isNew = false;

        lock (_pendingLock)
        {
            if (_inFlightByOp.TryGetValue(operation, out Task<IpcResponseMessage>? existing)
                && !existing.IsCompleted)
            {
                task = existing; // 重复请求合并：等待同一在飞任务
            }
            else
            {
                requestId = ++_requestIds;
                tcs = new TaskCompletionSource<IpcResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[requestId] = tcs;
                task = tcs.Task;
                _inFlightByOp[operation] = task;
                isNew = true;
            }
        }

        if (isNew)
        {
            try
            {
                await _supervisor.SendAsync(new IpcEnvelope(
                    new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeCommand,
                        "main", requestId, 0),
                    new IpcCommandMessage(operation, argumentJson)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_pendingLock)
                {
                    _pending.Remove(requestId);
                    if (_inFlightByOp.TryGetValue(operation, out Task<IpcResponseMessage>? t) && t == task)
                        _inFlightByOp.Remove(operation);
                }
                tcs!.TrySetException(ex);
                return await task.ConfigureAwait(false);
            }

            // 超时守护：超时即从配对表移除（随后到达的应答按迟到丢弃计数）。
            _ = Task.Run(async () =>
            {
                try { await task.WaitAsync(timeout).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref _commandTimeouts);
                    lock (_pendingLock) { _pending.Remove(requestId); }
                }
                finally
                {
                    lock (_pendingLock)
                    {
                        if (_inFlightByOp.TryGetValue(operation, out Task<IpcResponseMessage>? t) && t == task)
                            _inFlightByOp.Remove(operation);
                    }
                }
            });
        }

        return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
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
            Interlocked.Read(ref _snapshotBatchGaps));
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
