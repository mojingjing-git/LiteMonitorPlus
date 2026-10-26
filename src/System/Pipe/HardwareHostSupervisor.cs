using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;

namespace LiteMonitor.SystemServices.Pipe;

/// <summary>监督器候选参数（Plan A §6.1；I2 按实测固化前的默认值与派工单一致）。</summary>
public sealed record HardwareHostSupervisorOptions
{
    public int RefreshMs { get; init; } = 1000;
    public string? HostExePathOverride { get; init; }
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int HeartbeatMissLimit { get; init; } = 3;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SamplingProgressTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public IReadOnlyList<TimeSpan> BackoffSequence { get; init; } = new[]
        { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };
    public TimeSpan BackoffWindow { get; init; } = TimeSpan.FromMinutes(5);
    public int BackoffPauseThreshold { get; init; } = 3;
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>命令应答默认期限（生产默认 = IpcProtocol.DefaultCommandTimeout；测试可收紧）。</summary>
    public TimeSpan CommandTimeout { get; init; } = IpcProtocol.DefaultCommandTimeout;
    /// <summary>测试注入：附加到 Host 命令行（--test-pattern / --fault …）。</summary>
    public IReadOnlyList<string> HostExtraArguments { get; init; } = Array.Empty<string>();
    /// <summary>会话健康运行多久才算"成功"（用于连续失败清零；防止短命会话重置退避计数）。</summary>
    public TimeSpan HealthySessionResetInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>状态迁移记录（测试与诊断用；本地单调毫秒 + 观察到的原因）。</summary>
public sealed record SupervisorTransition(
    HardwareState From, HardwareState To, string? Reason, int? HostPid, long TimestampMonoMs);

/// <summary>
/// P-I2 / Plan A §6.1 最小监督器。状态机 Stopped→Starting→Ready→Unresponsive/Disconnected→
/// Backoff→Starting，另有 Degraded（含 crash-loop 暂停）与 Stopping。职责：
/// 启动（新会话新管道名）、握手期限、心跳失联（2s×3）、采样无进展（10s，心跳照发也判死）、
/// 初始化上限（60s）、退避 1/3/10s、5 分钟内连续 3 败暂停自动重启、正常关闭 3s 宽限；
/// Job Object（kill-on-close）绑定 Host 及其子进程，主程序异常退出不留孤儿。
/// 恢复循环由主程序拥有（本类）；旧进程确认退出后才启动新会话（§6.2）。
/// </summary>
public sealed class HardwareHostSupervisor : IDisposable
{
    private readonly HardwareHostSupervisorOptions _options;
    private readonly HostJobObject? _job;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<long> _failureTicks = new();

    private volatile bool _stopping;
    private volatile bool _paused;
    private volatile bool _isolationFailed;
    private Task? _loopTask;
    private CancellationTokenSource _loopCts = new();

    // 当前会话（仅监督循环线程写；其他线程只读快照字段）：
    private ConnectedHost? _connection;
    private Process? _hostProcess;
    private PipeServerEndpoint? _endpoint;
    private volatile string _sessionToken = "";
    private volatile HardwareState _state = HardwareState.Stopped;
    private string? _stateReason;
    private int _spawnCount;
    private int _sessionCount;      // 完成握手的会话数
    private int _restartCount;      // Ready 之后又失败的次数（成功会话之后再来一次）
    private int _consecutiveFailures;
    private int _sessionEpoch;      // 会话纪元（E02 §6.7：每次新建会话递增）

    /// <summary>消息到达（快照/心跳/响应原信封）；订阅方不得阻塞。</summary>
    public event Action<IpcEnvelope>? MessageReceived;

    /// <summary>会话建立（握手通过）。参数：连接、会话令牌、Host PID。</summary>
    public event Action<ConnectedHost, string, int>? SessionOpened;

    /// <summary>会话结束（对端退出/协议违规/监督裁决），带原因。
    /// <para>★ 原因为 null 表示"正常收尾，没有违规原因"，故载荷类型可空。</para></summary>
    public event Action<string?>? SessionClosed;

    /// <summary>状态迁移（含原因与 Host PID，测试观测用）。</summary>
    public event Action<SupervisorTransition>? Transitioned;

    public HardwareState State => _state;
    public string? StateReason => _stateReason;
    public int SpawnCount => _spawnCount;
    public int SessionCount => _sessionCount;
    public int RestartCount => _restartCount;
    public bool IsolationFailed => _isolationFailed;
    public bool IsPaused => _paused;
    public int? CurrentHostPid => _hostProcess?.Id;

    /// <summary>
    /// 当前会话令牌（E02 §6.7：命令响应校验用）。**先于连接发布**——调用方看到
    /// "有活动连接"时必然能读到与之匹配的令牌（写序：令牌 → 连接）。
    /// </summary>
    public string CurrentSessionToken => _sessionToken;

    /// <summary>
    /// 当前会话纪元（E02 §6.7）：每次新建会话（Host 重启/重连）递增，并经命令行传给 Host
    /// 盖在快照上——旧会话快照数据不得混入新会话。
    /// </summary>
    public int SessionEpoch => Volatile.Read(ref _sessionEpoch);

    public HardwareHostSupervisor(HardwareHostSupervisorOptions? options = null)
    {
        _options = options ?? new HardwareHostSupervisorOptions();
        try
        {
            _job = HostJobObject.CreateKillOnClose();
        }
        catch (Exception)
        {
            // 创建失败不吞：无 Job 兜底时主程序崩溃会留孤儿 Host，原型期按致命处理（报告记录）。
            throw;
        }
    }

    /// <summary>启动监督循环（非阻塞；内部自循环直到 StopAsync/Dispose）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_job is null || _loopCts.IsCancellationRequested, this);
        if (_loopTask != null) return;
        lock (this)
        {
            if (_loopTask != null) return;
            _loopTask = Task.Run(() => RunAsync(_loopCts.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_stopping)
        {
            if (_paused)
            {
                SetState(HardwareState.Degraded, "crash-loop-paused");
                return; // 暂停自动重启；显式重试/新会话由上层决定（原型期=重启应用）
            }

            SessionOutcome outcome;
            await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                outcome = await RunSingleSessionAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _lifecycleLock.Release();
            }
            if (_stopping || ct.IsCancellationRequested) break;

            if (outcome.HealthyLongEnough)
            {
                _consecutiveFailures = 0; // 健康运行足够久的会话才清零"连续失败"（短命会话不清零，防退避被重置）
                _restartCount++;
            }
            _consecutiveFailures++;
            _failureTicks.Add(Environment.TickCount64);
            PruneFailures();
            SetState(HardwareState.Unresponsive, outcome.Reason);

            if (_consecutiveFailures >= _options.BackoffPauseThreshold)
            {
                _paused = true; // 5 分钟窗口内连续 N 败 → 暂停自动重启（下一轮循环置 Degraded 返回）
                continue;
            }

            TimeSpan delay = _options.BackoffSequence[
                Math.Min(_consecutiveFailures - 1, _options.BackoffSequence.Count - 1)];
            SetState(HardwareState.Degraded, $"backoff-{(int)delay.TotalSeconds}s:{outcome.Reason}");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        if (_stopping || ct.IsCancellationRequested) SetState(HardwareState.Stopped, "stopped");
    }

    private sealed class SessionOutcome
    {
        public required string? Reason { get; init; }
        public bool HealthyLongEnough { get; init; }
    }

    private async Task<SessionOutcome> RunSingleSessionAsync(CancellationToken ct)
    {
        SetState(HardwareState.Starting, "spawn");
        // R2：endpoint 创建/SpawnHost/会话全程都在收口范围内——启动失败（缺文件/权限）与
        // 合法握手后的协议异常同样转成失败会话，监督循环不退出。
        PipeServerEndpoint? endpoint = null;
        Process? process = null;
        try
        {
            endpoint = PipeServerEndpoint.Create();
            _endpoint = endpoint;

            // E02 §6.7：会话纪元在每次新建会话时递增，并传给 Host 盖在快照上。
            int sessionEpoch = Interlocked.Increment(ref _sessionEpoch);
            process = SpawnHost(endpoint, sessionEpoch);
            _hostProcess = process;
            _spawnCount++;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SessionOutcome { Reason = "cancelled", HealthyLongEnough = false };
        }
        catch (Exception ex)
        {
            return new SessionOutcome { Reason = $"spawn-error:{ex.GetType().Name}", HealthyLongEnough = false };
        }
        try
        {
            _job!.TryAssign(process);

            ConnectedHost? connected = await endpoint.WaitForHostAsync(process.Id, _options.HandshakeTimeout, ct)
                .ConfigureAwait(false);
            if (connected is null)
            {
                TerminateHost(process, "handshake-timeout");
                return new SessionOutcome { Reason = "handshake-timeout", HealthyLongEnough = false };
            }

            // 写序（E02 §6.7）：**令牌先于连接**发布——调用方看到"有活动连接"时
            // 必然能读到与之匹配的会话令牌（响应校验依赖这一点）。
            _sessionToken = endpoint.SessionToken;
            _connection = connected;
            _sessionCount++;
            SetState(HardwareState.Ready, null, process.Id);
            SessionOpened?.Invoke(connected, endpoint.SessionToken, process.Id);

            var vitals = new Vitals();
            var readCts = new CancellationTokenSource();
            Task readTask = Task.Run(() => ReadLoopAsync(connected, vitals, readCts.Token), CancellationToken.None);
            string? violation = null;

            try
            {
                long heartbeatMissMs = (long)(_options.HeartbeatInterval.TotalMilliseconds * _options.HeartbeatMissLimit);
                long samplingMs = (long)_options.SamplingProgressTimeout.TotalMilliseconds;
                long initMs = (long)(_options.InitializationTimeout.TotalMilliseconds);
                while (violation is null && !_stopping && !ct.IsCancellationRequested && !readTask.IsCompleted)
                {
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                    long now = Environment.TickCount64;
                    // 心跳失联只看心跳时戳（快照照发也不能证明控制通道活着，§6.1）。
                    if (now - vitals.LastHeartbeatTick > heartbeatMissMs)
                        violation = $"heartbeat-missed-{_options.HeartbeatMissLimit}x{(long)_options.HeartbeatInterval.TotalMilliseconds}ms";                    else if (now - vitals.LastProgressTick > samplingMs)
                        violation = "sampling-stalled";
                    else if (!vitals.FirstSnapshotSeen && now - vitals.SessionStartTick > initMs)
                        violation = "init-timeout";
                }
            }
            finally
            {
                readCts.Cancel();
            }

            if (violation is null && !_stopping)
            {
                // 读循环先结束：对端关闭（退出/崩溃）或协议违规。
                await readTask.ConfigureAwait(false);
                violation = readTask.IsFaulted ? "host-disconnected:protocol-violation" : "host-disconnected:eof";
            }
            readCts.Cancel();
            connected.Dispose();

            bool healthyLongEnough = vitals.FirstSnapshotSeen
                && Environment.TickCount64 - vitals.SessionStartTick >= (long)_options.HealthySessionResetInterval.TotalMilliseconds;
            bool hostExited = process.HasExited;
            if (!hostExited)
                TerminateHost(process, violation);
            else
                process.WaitForExit(2000);

            SessionClosed?.Invoke(violation);
            return new SessionOutcome { Reason = violation, HealthyLongEnough = healthyLongEnough };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SessionOutcome { Reason = "cancelled", HealthyLongEnough = false };
        }
        catch (Exception ex)
        {
            // R2：合法握手后的协议异常（ReadLoop 重抛）等一切未收口异常都转成失败会话——
            // 不让监督循环退出、状态不误报 Ready。
            string reason = $"session-error:{ex.GetType().Name}";
            SetState(HardwareState.Unresponsive, reason);
            SessionClosed?.Invoke(reason);
            return new SessionOutcome { Reason = reason, HealthyLongEnough = false };
        }
        finally
        {
            _connection = null;
            // R2：无论路径如何，已启动的子进程必须被终止并确认退出，才允许进入下一会话。
            if (process != null)
            {
                try
                {
                    if (!process.HasExited) TerminateHost(process, "session-cleanup");
                }
                catch { /* 回收尽力而为；Job Object 兜底 */ }
            }
            if (process != null && endpoint != null) CleanupSession(process, endpoint);
            else endpoint?.Dispose();
        }
    }

    private async Task ReadLoopAsync(ConnectedHost connected, Vitals vitals, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IpcEnvelope? envelope;
            try
            {
                envelope = await connected.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IpcProtocolException)
            {
                throw; // 协议违规：断连（调用方按违规记录）
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                return; // 对端关闭/连接被监督器断开
            }
            if (envelope is null) return; // EOF

            vitals.OnMessage(envelope);
            try { MessageReceived?.Invoke(envelope); } catch { /* 订阅方异常不断连 */ }
        }
    }

    private Process SpawnHost(PipeServerEndpoint endpoint, int sessionEpoch)
    {
        string exe = ResolveHostExePath(_options.HostExePathOverride);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(endpoint.PipeName);
        psi.ArgumentList.Add("--session"); psi.ArgumentList.Add(endpoint.SessionToken);
        psi.ArgumentList.Add("--parent-pid"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--refresh-ms"); psi.ArgumentList.Add(Math.Max(50, _options.RefreshMs).ToString());
        psi.ArgumentList.Add("--heartbeat-ms"); psi.ArgumentList.Add(Math.Max(100, (int)_options.HeartbeatInterval.TotalMilliseconds).ToString());
        psi.ArgumentList.Add("--session-epoch"); psi.ArgumentList.Add(sessionEpoch.ToString());
        foreach (string extra in _options.HostExtraArguments) psi.ArgumentList.Add(extra);

        Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 HardwareHost：{exe}");
        // 重定向输出落临时文件（可诊断性；不阻塞：丢弃消费避免写满缓冲）。
        string logPath = Path.Combine(Path.GetTempPath(), $"LiteMonitor.HardwareHost.{endpoint.SessionToken[..8]}.log");
        _ = Task.Run(async () =>
        {
            var sb = new StringBuilder();
            await Task.WhenAll(
                ConsumeAsync(process.StandardOutput, sb),
                ConsumeAsync(process.StandardError, sb));
            try { await File.WriteAllTextAsync(logPath, sb.ToString()); } catch { }
        });
        return process;
    }

    private static async Task ConsumeAsync(StreamReader reader, StringBuilder sb)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (sb.Length < 64_000) sb.AppendLine(line);
            }
        }
        catch { }
    }

    internal static string ResolveHostExePath(string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            if (!File.Exists(overridePath)) throw new FileNotFoundException("Host 产物不存在（显式指定路径）", overridePath);
            return overridePath!;
        }

        // 1) 与主程序同目录（安装形态 / 主输出目录复制项）。
        string beside = Path.Combine(AppContext.BaseDirectory, "LiteMonitor.HardwareHost.exe");
        if (File.Exists(beside)) return beside;

        // 2) 仓库开发布局：向上找 LiteMonitor.sln 再取 src/HardwareHost/bin。
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "LiteMonitor.sln")))
            dir = Path.GetDirectoryName(dir);
        if (dir != null)
        {
            foreach (string configuration in new[] { BuildConfiguration(), "Debug", "Release" })
            {
                string dev = Path.Combine(dir, "src", "HardwareHost", "bin", configuration,
                    "net8.0-windows", "LiteMonitor.HardwareHost.exe");
                if (File.Exists(dev)) return dev;
            }
        }
        throw new FileNotFoundException(
            "找不到 LiteMonitor.HardwareHost.exe（同目录与仓库开发布局均未命中）", beside);
    }

    private static string BuildConfiguration() =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>向当前会话发送原始信封（串行化；信封头会话令牌自动盖上当前会话）。
    /// 无活动会话时抛 InvalidOperationException（调用方按断线处理）。</summary>
    public Task SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_stopping || _connection is null)
            throw new InvalidOperationException("no-active-host-session");
        return SendCoreAsync(envelope, cancellationToken);
    }

    private async Task SendCoreAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        ConnectedHost? connection = _connection;
        string token = _sessionToken;
        if (connection is null)
            throw new InvalidOperationException("no-active-host-session");
        if (envelope.Header.SessionId != token)
            envelope = envelope with { Header = envelope.Header with { SessionId = token } };
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>受控停止：shutdown 命令 → 退出宽限 → 终止 → 校验退出；无法终止则标记隔离失败。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopping) return;
        _stopping = true;
        SetState(HardwareState.Stopping, "stop-requested");

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConnectedHost? connection = _connection;
            Process? process = _hostProcess;
            if (connection != null && process != null && !process.HasExited)
            {
                try
                {
                    await SendCoreAsync(new IpcEnvelope(
                        new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion,
                            IpcProtocol.TypeCommand, "stop", 0, 1),
                        new IpcCommandMessage(IpcProtocol.OpShutdown, null)), cancellationToken).ConfigureAwait(false);
                }
                catch { /* 发送失败走强杀 */ }
            }

            if (process != null && !process.HasExited)
            {
                if (!process.WaitForExit((int)_options.ShutdownTimeout.TotalMilliseconds))
                {
                    TerminateHost(process, "shutdown-timeout");
                    if (!process.WaitForExit(3000))
                    {
                        _isolationFailed = true; // §6.1：无法退出 → 标记隔离失败，禁止启动重复采集器
                        SetState(HardwareState.Degraded, "isolation-failed:cannot-terminate");
                    }
                }
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        try { if (_loopTask != null) await _loopTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        SetState(_isolationFailed ? HardwareState.Degraded : HardwareState.Stopped, "stopped");
    }

    /// <param name="reason">终止原因；为 null 表示"没有特定违规原因"（正常收尾）。</param>
    private void TerminateHost(Process process, string? reason)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception)
        {
            // Kill 失败交由上层标记（StopAsync 内检查）；会话路径失败则进入下一轮退避。
        }
        _stateReason = reason;
    }

    private void CleanupSession(Process process, PipeServerEndpoint endpoint)
    {
        try { _connection?.Dispose(); } catch { }
        try { endpoint.Dispose(); } catch { }
        try { process.Dispose(); } catch { }
        _connection = null;
        _endpoint = null;
        _hostProcess = null;
    }

    private void PruneFailures()
    {
        long windowStart = Environment.TickCount64 - (long)_options.BackoffWindow.TotalMilliseconds;
        _failureTicks.RemoveAll(t => t < windowStart);
    }

    private void SetState(HardwareState to, string? reason, int? pid = null)
    {
        HardwareState from = _state;
        if (from == to && reason == _stateReason) return;
        _state = to;
        _stateReason = reason;
        Transitioned?.Invoke(new SupervisorTransition(from, to, reason, pid ?? CurrentHostPid, Environment.TickCount64));
    }

    public void Dispose()
    {
        if (!_stopping)
        {
            try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        }
        try { _loopCts.Cancel(); } catch { }
        try { _loopCts.Dispose(); } catch { }
        _lifecycleLock.Dispose();
        _sendLock.Dispose();
        _job?.Dispose(); // 关闭作业句柄：若 Host 仍在（异常路径），kill-on-close 兜底回收
    }

    /// <summary>会话活性指标（读取循环单线程更新）。</summary>
    private sealed class Vitals
    {
        public long SessionStartTick = Environment.TickCount64;
        public long LastReceiveTick = Environment.TickCount64;
        public long LastHeartbeatTick = Environment.TickCount64;
        public long LastProgressTick = Environment.TickCount64;
        public long ProgressSeq = -1;
        public volatile bool FirstSnapshotSeen;

        public void OnMessage(IpcEnvelope envelope)
        {
            long now = Environment.TickCount64;
            LastReceiveTick = now;
            switch (envelope.Header.Type)
            {
                case IpcProtocol.TypeSnapshot:
                    FirstSnapshotSeen = true;
                    ProgressSeq = envelope.PayloadAs<IpcSnapshotMessage>().Snapshot.BatchId;
                    LastProgressTick = now;
                    break;
                case IpcProtocol.TypeHeartbeat:
                    LastHeartbeatTick = now;
                    long seq = envelope.PayloadAs<IpcHeartbeatMessage>().ProgressSeq;
                    if (seq != ProgressSeq)
                    {
                        ProgressSeq = seq;
                        LastProgressTick = now;
                    }
                    break;
            }
        }
    }
}
