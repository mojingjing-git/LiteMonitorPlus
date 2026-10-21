namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>IP 排除功能的状态机。</summary>
public enum NetworkTrafficExclusionState
{
    /// <summary>功能关闭。</summary>
    Disabled = 0,

    /// <summary>已开启，但没有有效规则 / 暂时没有需求（不启动任何会话）。</summary>
    Idle = 1,

    /// <summary>准备中：会话正在启动，或还在预热 / 首窗口。</summary>
    Preparing = 2,

    /// <summary>运行中：排除已生效。</summary>
    Running = 3,

    /// <summary>降级：丢事件、归属不明、解码回退等，运行但数据不完整。</summary>
    Degraded = 4,

    /// <summary>失败：权限不足、后端启动失败、配置有误、网卡归属歧义。</summary>
    Failed = 5,
}

/// <summary>对外发布的状态快照。</summary>
public sealed record NetworkTrafficExclusionStatus(
    NetworkTrafficExclusionState State,
    bool Effective,
    string Reason,
    string? Detail,
    int RulesVersion,
    int RuleCount,
    int SessionGeneration,
    long EventsLost,
    long EventsUndecodable,
    long EventsApplied,
    long UnattributedEvents,
    IReadOnlyList<IpRuleError> RuleErrors);

/// <summary>
/// IP 流量排除的生命周期核心。
///
/// <para>职责：需求启停、单调时间退避、规则/适配器的原子热切换、窗口切分与快照发布、
/// 状态机（准备中/运行/降级/失败）、会话代次隔离。</para>
///
/// <para>★ 硬约束：</para>
/// <list type="bullet">
/// <item>关闭或没有有效规则时**不创建任何事件采集会话**；</item>
/// <item>准备中/错误/降级状态**不伪装成过滤后的 0**；</item>
/// <item>规则/适配器变化**原子切换**，下一窗口重新建立基线，旧回调写不进新窗口；</item>
/// <item>启动失败按**单调时间**退避；</item>
/// <item>只管理自己创建的会话，绝不碰 NT Kernel Logger 或 PresentMon 的会话。</item>
/// </list>
/// </summary>
public sealed class NetworkTrafficExclusionService : IDisposable
{
    /// <summary>测量窗口长度（秒）。</summary>
    public const double DefaultWindowSeconds = 1.0;

    /// <summary>超过这个长度的窗口按「长间隔」丢弃（睡眠/挂起），不补造尖峰。</summary>
    public const double MaxWindowSeconds = 30.0;

    /// <summary>会话启动后的预热保护期（秒）。N0 实测新会话开头 406~1422 ms 收不到事件。</summary>
    public const double WarmupGuardSeconds = 2.0;

    /// <summary>实时网速需求的存活时间（秒）：最后一次取 NET.Up/NET.Down 之后多久内仍然算有需求。</summary>
    public const double DemandTimeoutSeconds = 30.0;

    /// <summary>启动失败后的首次退避（秒）。</summary>
    public const double InitialBackoffSeconds = 5.0;

    /// <summary>退避上限（秒）。</summary>
    public const double MaxBackoffSeconds = 300.0;

    /// <summary>
    /// 「事件缺失」的判定时间（秒）：会话已在跑，但这么久都没收到任何事件 → 明确失败态。
    /// <para>★ TraceEvent 的 EnableProvider 返回值不可信（对本 provider 恒为 false），
    /// 真正可靠的判据是**有没有事件到达**。</para>
    /// </summary>
    public const double NoEventsTimeoutSeconds = 10.0;

    private readonly object _gate = new();
    private readonly INetworkTrafficEventSource _source;
    private readonly NetworkTrafficFilter _filter;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly double _windowSeconds;

    // ---- 配置 ----
    private bool _enabled;
    private string _configSignature = "\u0000none";
    private IpExclusionRules _rules = IpExclusionRules.Empty;
    private IReadOnlyList<IpRuleError> _ruleErrors = Array.Empty<IpRuleError>();
    private bool _rulesUsable;

    // ---- 目标网卡 ----
    private int? _targetIfIndex;
    private string? _adapterRejection;

    // ---- 会话生命周期 ----
    private bool _disposed;
    private bool _sourceRunning;

    /// <summary>当前会话代次。用无锁读写：事件回调线程绝不能去拿服务锁（会与控制路径锁反转）。</summary>
    private int _activeGeneration;

    private int _generation;
    private long _nextStartAttemptTicks;
    private double _backoffSeconds = InitialBackoffSeconds;
    private long _sessionStartTicks;

    // ---- 需求与窗口 ----
    private long _demandUntilTicks;
    private bool _windowOpen;
    private long _windowStartTicks;
    private bool _discardNextWindow;
    private FilteredNetworkSnapshot? _published;
    private bool _havePublished;

    public NetworkTrafficExclusionService(
        INetworkTrafficEventSource source,
        INetworkAdapterAttributor attributor,
        TimeProvider? timeProvider = null,
        double windowSeconds = DefaultWindowSeconds,
        Action<string>? log = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _filter = new NetworkTrafficFilter(attributor);
        _time = timeProvider ?? TimeProvider.System;
        _windowSeconds = windowSeconds > 0 ? windowSeconds : DefaultWindowSeconds;
        _log = log;
        _source.EventReceived += OnSourceEvent;
    }

    /// <summary>当前是否已经发布过一个有效快照。</summary>
    public bool HasPublishedSnapshot => _published != null;

    /// <summary>功能开关是否打开（不代表排除真的生效）。</summary>
    public bool IsEnabled { get { lock (_gate) { return _enabled; } } }

    /// <summary>IP 排除是否真的生效（有有效快照）。</summary>
    public bool IsEffective
    {
        get
        {
            FilteredNetworkSnapshot? snap = _published;
            return snap != null && snap.IsValid;
        }
    }

    /// <summary>规则是否可用（没有错误行、至少一条规则、未超上限）。</summary>
    public bool HasUsableRules { get { lock (_gate) { return _rulesUsable; } } }

    /// <summary>目标网卡索引（-1 = 未指定）。</summary>
    public int TargetIfIndex { get { lock (_gate) { return _targetIfIndex ?? -1; } } }

    /// <summary>当前发布的快照（没有则 null）。</summary>
    public FilteredNetworkSnapshot? GetSnapshot() => _published;

    /// <summary>
    /// 取过滤后的实时速率（bytes/sec）。
    /// <para>★ 返回 false 表示「没有可用数据」——调用方必须显示**缺失**，绝不能当成 0。</para>
    /// </summary>
    public bool TryGetFilteredRate(string key, out double bytesPerSecond)
    {
        bytesPerSecond = 0;
        FilteredNetworkSnapshot? snap = _published;
        if (snap == null || !snap.IsValid) { return false; }
        if (key == "NET.Up") { bytesPerSecond = snap.FilteredSendBytesPerSecond; return true; }
        if (key == "NET.Down") { bytesPerSecond = snap.FilteredReceiveBytesPerSecond; return true; }
        return false;
    }

    /// <summary>
    /// 应用配置。规则文本变化时**原子切换**并让下一个窗口重新建立基线。
    /// <para>本方法绝不触发硬件采集、绝不重开硬件树。</para>
    /// </summary>
    public void ApplyConfiguration(bool enabled, IReadOnlyList<string> ipLines)
    {
        string signature = (enabled ? "1" : "0") + "|" + string.Join("\n", ipLines ?? Array.Empty<string>());
        lock (_gate)
        {
            if (signature == _configSignature) { return; }
            _configSignature = signature;
            _enabled = enabled;

            var result = IpExclusionRules.CompileWithResult(ipLines ?? Array.Empty<string>());
            _rules = result.Rules;
            _ruleErrors = result.Errors;
            _rulesUsable = result.IsUsable;

            // 规则换代：旧窗口的字节属于旧规则，必须作废。
            _discardNextWindow = true;
            _havePublished = false;
            _published = null;
        }

        // 规则不再可用 → 立刻停掉会话（关闭/无有效规则时不保留任何额外采集）。
        if (!HasUsableRules)
        {
            StopSource();
        }
        else
        {
            _filter.SetRules(_rules, TargetIfIndex, RetransmitPolicy.ExcludeRetransmitFromTotal);
        }
    }

    /// <summary>
    /// 设置目标网卡。网卡切换 / 地址热变更时调用。
    /// <para>★ C1：目标网卡上的本地地址如果同时出现在别的网卡上（归属歧义），**拒绝**该配置。</para>
    /// </summary>
    public void SetTargetAdapter(int? ifIndex, string? ambiguityReason = null)
    {
        bool changed;
        lock (_gate)
        {
            changed = _targetIfIndex != ifIndex || _adapterRejection != ambiguityReason;
            _targetIfIndex = ifIndex;
            _adapterRejection = ambiguityReason;
            if (changed)
            {
                // 网卡切换 → 旧窗口作废，重新建立基线（不出现尖峰或负值）。
                _discardNextWindow = true;
                _havePublished = false;
                _published = null;
            }
        }
        if (!changed) { return; }

        _filter.SetRules(_rules, TargetIfIndex, RetransmitPolicy.ExcludeRetransmitFromTotal);
    }

    /// <summary>替换归属映射（地址热变更后刷新）。</summary>
    public void SetAttributor(INetworkAdapterAttributor attributor)
    {
        _filter.SetAttributor(attributor);
    }

    /// <summary>
    /// 登记「有人正在看实时网速」的需求。
    /// <para>只有存在需求时才允许启动事件采集会话。</para>
    /// </summary>
    public void NotifyRealtimeDemand()
    {
        long now = _time.GetTimestamp();
        lock (_gate)
        {
            _demandUntilTicks = now + (long)(DemandTimeoutSeconds * _time.TimestampFrequency);
        }
    }

    /// <summary>
    /// 采集节拍（由共享采集协调器调用，≤ 1 次/秒）。
    /// <para>负责：会话准入/退避、窗口切分、快照发布。</para>
    /// </summary>
    public void Tick()
    {
        if (_disposed) { return; }

        EnsureSession();
        UpdateWindow();
    }

    /// <summary>主动停止事件采集（可恢复）。</summary>
    public void Stop()
    {
        StopSource();
        lock (_gate)
        {
            _windowOpen = false;
            _havePublished = false;
            _published = null;
            _discardNextWindow = true;
            _demandUntilTicks = 0;
        }
    }

    public void Dispose()
    {
        bool already;
        lock (_gate)
        {
            already = _disposed;
            _disposed = true;
        }
        if (already) { return; }

        _source.EventReceived -= OnSourceEvent;
        StopSource();
        // ★ 最终关闭：把事件源（及其 ETW 会话）一并释放。
        try { _source.Dispose(); }
        catch (Exception ex) { _log?.Invoke("IP exclusion backend dispose failed: " + ex.Message); }
    }

    /// <summary>当前状态（供 UI 显示）。</summary>
    public NetworkTrafficExclusionStatus GetStatus()
    {
        bool enabled;
        bool usable;
        IReadOnlyList<IpRuleError> errors;
        int rulesVersion;
        int ruleCount;
        string? rejection;
        bool demandLive;
        int generation;
        long sessionStartTicks;
        bool targetKnown;
        bool sourceRunning;

        lock (_gate)
        {
            enabled = _enabled;
            usable = _rulesUsable;
            errors = _ruleErrors;
            rulesVersion = _rules.Version;
            ruleCount = _rules.RuleCount;
            rejection = _adapterRejection;
            demandLive = _demandUntilTicks > _time.GetTimestamp();
            generation = _generation;
            sessionStartTicks = _sessionStartTicks;
            targetKnown = _targetIfIndex.HasValue;
            sourceRunning = _sourceRunning;
        }

        if (!enabled)
        {
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Disabled, false,
                "Menu.IpExclusionStateDisabled", null, rulesVersion, ruleCount, generation,
                0, 0, 0, 0, errors);
        }

        if (errors.Count > 0)
        {
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Failed, false,
                "Menu.IpExclusionStateBadRules", string.Join("; ", errors.Select(e => "#" + (e.LineNumber + 1) + " " + e.Reason)),
                rulesVersion, ruleCount, generation, 0, 0, 0, 0, errors);
        }

        if (!usable)
        {
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Idle, false,
                "Menu.IpExclusionStateNoRules", null, rulesVersion, ruleCount, generation,
                0, 0, 0, 0, errors);
        }

        if (rejection != null)
        {
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Failed, false,
                "Menu.IpExclusionStateAmbiguousAdapter", rejection, rulesVersion, ruleCount, generation,
                0, 0, 0, 0, errors);
        }

        if (!demandLive)
        {
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Idle, false,
                "Menu.IpExclusionStateIdle", null, rulesVersion, ruleCount, generation,
                0, 0, 0, 0, errors);
        }

        if (!targetKnown)
        {
            // 目标网卡还没识别出来（自动选网卡尚未完成 / 网卡刚切换）→ 待命，不启动会话。
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Idle, false,
                "Menu.IpExclusionStateIdle", "target adapter is not identified yet", rulesVersion, ruleCount, generation,
                0, 0, 0, 0, errors);
        }

        TrafficSourceStatus sourceStatus = _source.Status;
        switch (sourceStatus)
        {
            case TrafficSourceStatus.Failed:
                return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Failed, false,
                    "Menu.IpExclusionStateBackendFailed", _source.StatusDetail, rulesVersion, ruleCount, generation,
                    _source.EventsLost, _source.EventsUndecodable, 0, 0, errors);

            case TrafficSourceStatus.Starting:
                return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Preparing, false,
                    "Menu.IpExclusionStatePreparing", null, rulesVersion, ruleCount, generation,
                    _source.EventsLost, _source.EventsUndecodable, 0, 0, errors);

            case TrafficSourceStatus.Stopped:
                return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Preparing, false,
                    "Menu.IpExclusionStatePreparing", null, rulesVersion, ruleCount, generation,
                    _source.EventsLost, _source.EventsUndecodable, 0, 0, errors);
        }

        // ★ 事件缺失：会话在跑但一直收不到事件 → 明确失败态（绝不显示成"过滤后为 0"）。
        //   注意：TraceEvent 的 EnableProvider 返回值不可信，真正的判据是**有没有事件**。
        if (sourceRunning && _source.EventsProcessed == 0 &&
            _time.GetElapsedTime(sessionStartTicks, _time.GetTimestamp()).TotalSeconds >= NoEventsTimeoutSeconds)
        {
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Failed, false,
                "Menu.IpExclusionStateNoEvents", null, rulesVersion, ruleCount, generation,
                _source.EventsLost, _source.EventsUndecodable, 0, 0, errors);
        }

        FilteredNetworkSnapshot? snap = _published;
        bool effective = snap != null && snap.IsValid;
        long unattributed = snap?.UnattributedEvents ?? 0;

        if (!effective)
        {
            return new NetworkTrafficExclusionStatus(NetworkTrafficExclusionState.Preparing, false,
                snap?.InvalidReason != null && snap.InvalidReason.Contains("warm", StringComparison.OrdinalIgnoreCase)
                    ? "Menu.IpExclusionStateWarmingUp"
                    : "Menu.IpExclusionStatePreparing",
                snap?.InvalidReason, rulesVersion, ruleCount, generation,
                _source.EventsLost, _source.EventsUndecodable, snap?.EventsApplied ?? 0, unattributed, errors);
        }

        bool degraded = _source.EventsLost > 0 || unattributed > 0 || snap!.AdapterAmbiguous;
        return new NetworkTrafficExclusionStatus(
            degraded ? NetworkTrafficExclusionState.Degraded : NetworkTrafficExclusionState.Running,
            true,
            degraded ? "Menu.IpExclusionStateDegraded" : "Menu.IpExclusionStateRunning",
            degraded ? "lost=" + _source.EventsLost + " unattributed=" + unattributed : null,
            rulesVersion, ruleCount, generation,
            _source.EventsLost, _source.EventsUndecodable, snap.EventsApplied, unattributed, errors);
    }

    // -----------------------------------------------------------------
    // 会话生命周期
    // -----------------------------------------------------------------

    private void EnsureSession()
    {
        bool shouldRun;
        bool consumerDied = false;
        long now = _time.GetTimestamp();

        lock (_gate)
        {
            if (_disposed) { return; }

            // 消费线程挂掉（source 自己变成 Failed）→ 按退避重试，不能永远停在半死状态。
            if (_sourceRunning && _source.Status == TrafficSourceStatus.Failed)
            {
                _sourceRunning = false;
                consumerDied = true;
                _nextStartAttemptTicks = now + (long)(_backoffSeconds * _time.TimestampFrequency);
                _backoffSeconds = Math.Min(_backoffSeconds * 2, MaxBackoffSeconds);
            }

            shouldRun = _enabled && _rulesUsable && _adapterRejection == null &&
                        _targetIfIndex.HasValue &&
                        _demandUntilTicks > now && _nextStartAttemptTicks <= now;
        }

        if (consumerDied)
        {
            StopSource();
            return;
        }

        if (!shouldRun)
        {
            // 条件不满足 → 停掉（关闭/无规则/无需求/退避未到期）。
            bool running;
            lock (_gate) { running = _sourceRunning; }
            if (running)
            {
                StopSource();
            }
            return;
        }

        // 已经在跑就不要重复 Start —— 重复启动会重置预热计时，让首个窗口永远被丢弃。
        bool alreadyRunning;
        lock (_gate) { alreadyRunning = _sourceRunning; }
        if (alreadyRunning) { return; }

        try
        {
            _source.Start();
            int gen = _source.Generation;
            Volatile.Write(ref _activeGeneration, gen);
            lock (_gate)
            {
                _sourceRunning = true;
                _generation = gen;
                _sessionStartTicks = _time.GetTimestamp();
                _backoffSeconds = InitialBackoffSeconds;
                _nextStartAttemptTicks = 0;
                _discardNextWindow = true;
                _windowOpen = false;
                _havePublished = false;
                _published = null;
            }
            _filter.SetRules(_rules, TargetIfIndex, RetransmitPolicy.ExcludeRetransmitFromTotal);
            _log?.Invoke("IP exclusion backend started, generation " + gen);
        }
        catch (Exception ex)
        {
            double backoff;
            lock (_gate)
            {
                _sourceRunning = false;
                backoff = _backoffSeconds;
                _backoffSeconds = Math.Min(_backoffSeconds * 2, MaxBackoffSeconds);
                _nextStartAttemptTicks = _time.GetTimestamp() + (long)(backoff * _time.TimestampFrequency);
            }
            _log?.Invoke("IP exclusion backend failed to start: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void StopSource()
    {
        bool running;
        lock (_gate) { running = _sourceRunning; }
        if (!running) { return; }
        try
        {
            _source.Stop();
        }
        catch (Exception ex)
        {
            _log?.Invoke("IP exclusion backend stop failed: " + ex.Message);
        }
        lock (_gate)
        {
            _sourceRunning = false;
            _windowOpen = false;
            _havePublished = false;
            _published = null;
            _discardNextWindow = true;
        }
    }

    // -----------------------------------------------------------------
    // 窗口
    // -----------------------------------------------------------------

    private void UpdateWindow()
    {
        long now = _time.GetTimestamp();
        double elapsed;
        bool open;
        long startTicks;

        lock (_gate)
        {
            if (_disposed) { return; }
            open = _windowOpen;
            startTicks = _windowStartTicks;
        }

        if (!open)
        {
            // 开启一个新窗口（基线从现在开始）。
            _filter.BeginWindow(now, now, 0, CurrentGeneration());
            lock (_gate)
            {
                _windowOpen = true;
                _windowStartTicks = now;
            }
            return;
        }

        elapsed = _time.GetElapsedTime(startTicks, now).TotalSeconds;
        if (elapsed < _windowSeconds) { return; }

        if (elapsed > MaxWindowSeconds)
        {
            // 长间隔（睡眠/挂起）→ 丢弃这个窗口，重新建立基线，不补造尖峰。
            _filter.BeginWindow(now, now, 0, CurrentGeneration());
            lock (_gate)
            {
                _windowStartTicks = now;
                _discardNextWindow = true;
                _havePublished = false;
                _published = null;
            }
            return;
        }

        // 关闭当前窗口 → 生成快照。
        _filter.UpdateWindowEnd(now, elapsed);
        FilteredNetworkSnapshot snap = _filter.Snapshot();

        bool discard;
        bool warm;
        lock (_gate)
        {
            discard = _discardNextWindow;
            _discardNextWindow = false;
            warm = _sessionStartTicks > 0 &&
                   _time.GetElapsedTime(_sessionStartTicks, now).TotalSeconds < WarmupGuardSeconds;
            _windowStartTicks = now;
            _windowOpen = true;
        }

        if (discard || warm)
        {
            // ★ 预热期 / 首窗口：丢弃，不产出速率（避免假的 0 速率）。
            _filter.BeginWindow(now, now, 0, CurrentGeneration());
            lock (_gate)
            {
                _windowStartTicks = now;
                _havePublished = false;
                _published = null;
            }
            return;
        }

        _published = snap;
        lock (_gate) { _havePublished = true; }
    }

    private int CurrentGeneration() => Volatile.Read(ref _activeGeneration);

    // -----------------------------------------------------------------
    // 事件回调（运行在 ETW 消费线程上）
    // -----------------------------------------------------------------

    private void OnSourceEvent(NetworkTrafficEvent ev)
    {
        // ★ 这里**绝不能**再取服务锁（会话控制路径是 service → source 的顺序），
        //   否则会与「持服务锁去 Start/Stop」形成锁反转。过滤器自己会拒绝旧代次的事件。
        _filter.Apply(ev, CurrentGeneration());
    }
}
