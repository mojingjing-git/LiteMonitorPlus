namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>
/// 一个测量窗口的不可变视图。**绝不**是半填状态：窗口不完整时标记为无效，而不是报 0 流量。
/// </summary>
public sealed record FilteredNetworkSnapshot(
    bool IsValid,
    string? InvalidReason,
    long WindowStartTicks,
    long WindowEndTicks,
    double RealIntervalSeconds,
    long SourceSendBytes,
    long SourceReceiveBytes,
    long ExcludedSendBytes,
    long ExcludedReceiveBytes,
    long FilteredSendBytes,
    long FilteredReceiveBytes,
    double FilteredSendBytesPerSecond,
    double FilteredReceiveBytesPerSecond,
    int RulesVersion,
    int SessionGeneration,
    int? AdapterIfIndex,
    long EventsApplied,
    long DuplicateEventsSuppressed,
    long RetransmitBytesObserved,
    long CopyEventsObserved,
    bool NegativeClamped,
    long UnattributedEvents,
    long OtherAdapterEvents,
    bool AdapterAmbiguous);

/// <summary>
/// 在**同一个事件源 / 同一张网卡 / 同一个时间窗**内累计总量与排除量，
/// 保证两个数字口径一致、可以安全相减。
/// </summary>
public sealed class NetworkTrafficFilter
{
    private const int MaxDedupEntries = 50_000;

    private readonly object _gate = new();
    private readonly HashSet<EventIdentity> _seen = new();
    private INetworkAdapterAttributor _attributor;

    private IpExclusionRules _rules = IpExclusionRules.Empty;
    private int _rulesVersion;
    private int _sessionGeneration = -1;
    private int _adapterIfIndex = -1;
    private RetransmitPolicy _retransmitPolicy = RetransmitPolicy.ExcludeRetransmitFromTotal;

    private long _windowStartTicks;
    private long _windowEndTicks;
    private double _realSeconds;
    private bool _windowOpen;

    private long _sourceSendBytes;
    private long _sourceReceiveBytes;
    private long _excludedSendBytes;
    private long _excludedReceiveBytes;
    private long _eventsApplied;
    private long _duplicateEventsSuppressed;
    private long _retransmitBytesObserved;
    private long _copyEventsObserved;
    private long _unattributedEvents;
    private long _otherAdapterEvents;
    private bool _adapterAmbiguous;

    public NetworkTrafficFilter(INetworkAdapterAttributor attributor)
    {
        _attributor = attributor ?? throw new ArgumentNullException(nameof(attributor));
    }

    /// <summary>替换归属映射（地址热变更后刷新）。旧映射在替换瞬间不再被使用。</summary>
    public void SetAttributor(INetworkAdapterAttributor attributor)
    {
        lock (_gate)
        {
            _attributor = attributor ?? throw new ArgumentNullException(nameof(attributor));
        }
    }

    public int RulesVersion { get { lock (_gate) { return _rulesVersion; } } }

    /// <summary>当前绑定的网卡索引（-1 = 未指定）。</summary>
    public int AdapterIfIndex { get { lock (_gate) { return _adapterIfIndex; } } }

    /// <summary>原子切换规则集；下一个窗口重新建立基线。</summary>
    public void SetRules(IpExclusionRules rules, int adapterIfIndex, RetransmitPolicy policy)
    {
        lock (_gate)
        {
            _rules = rules;
            _rulesVersion = rules.Version;
            _adapterIfIndex = adapterIfIndex;
            _retransmitPolicy = policy;
            _seen.Clear();
        }
    }

    /// <summary>打开一个新的测量窗口。旧代次的事件会被丢弃。</summary>
    public void BeginWindow(long startTicks, long endTicks, double realSeconds, int sessionGeneration)
    {
        lock (_gate)
        {
            _windowStartTicks = startTicks;
            _windowEndTicks = endTicks;
            _realSeconds = realSeconds;
            _sessionGeneration = sessionGeneration;
            _windowOpen = true;
            _seen.Clear();
            ResetCounters();
        }
    }

    /// <summary>只更新窗口时长，不清空已经累计的计数。</summary>
    public void UpdateWindowEnd(long endTicks, double realSeconds)
    {
        lock (_gate)
        {
            _windowEndTicks = endTicks;
            _realSeconds = realSeconds;
        }
    }

    /// <summary>喂入一个事件。运行在事件源线程上。</summary>
    public void Apply(in NetworkTrafficEvent e, int sessionGeneration)
    {
        lock (_gate)
        {
            // 旧代次的回调绝不能写进新窗口。
            if (!_windowOpen || sessionGeneration != _sessionGeneration) { return; }

            if (_seen.Count < MaxDedupEntries)
            {
                var id = new EventIdentity(e);
                if (!_seen.Add(id))
                {
                    _duplicateEventsSuppressed++;
                    return;
                }
            }

            _eventsApplied++;

            // ★ C4：TCPCOPY 永远不是新数据 —— 计入即重复计数（N0 实测多算 54%）。
            if (e.Kind == NetworkTrafficEventKind.CopyUp)
            {
                _copyEventsObserved += e.SizeBytes;
                return;
            }

            if (e.Kind == NetworkTrafficEventKind.Retransmit)
            {
                _retransmitBytesObserved += e.SizeBytes;
                if (_retransmitPolicy != RetransmitPolicy.IncludeRetransmitInTotal) { return; }
            }

            // ★ C1：归属只能靠本地地址映射；无法唯一归属的事件不参与统计。
            if (!_attributor.TryResolveIfIndex(e.LocalAddress, out int ifIndex))
            {
                _unattributedEvents++;
                if (_attributor.IsAmbiguous(e.LocalAddress)) { _adapterAmbiguous = true; }
                return;
            }

            // 只统计**选中网卡**的流量：其它网卡的同远端 IP 流量不属于这张卡。
            if (_adapterIfIndex >= 0 && ifIndex != _adapterIfIndex)
            {
                _otherAdapterEvents++;
                return;
            }

            bool isSend = e.Direction == NetworkTrafficDirection.Send;

            if (isSend) { _sourceSendBytes += e.SizeBytes; }
            else { _sourceReceiveBytes += e.SizeBytes; }

            // 一个事件最多被排除一次（无论命中几条规则）。
            if (_rules.IsExcludedRemote(e.RemoteAddress, out _))
            {
                if (isSend) { _excludedSendBytes += e.SizeBytes; }
                else { _excludedReceiveBytes += e.SizeBytes; }
            }
        }
    }

    public FilteredNetworkSnapshot Snapshot()
    {
        lock (_gate)
        {
            long filteredSend = _sourceSendBytes - _excludedSendBytes;
            long filteredRecv = _sourceReceiveBytes - _excludedReceiveBytes;
            bool clamped = false;
            // 非负钳制**只是边界保护**：它不掩盖窗口/适配器不匹配（那由 IsValid / UnattributedEvents 表达）。
            if (filteredSend < 0) { filteredSend = 0; clamped = true; }
            if (filteredRecv < 0) { filteredRecv = 0; clamped = true; }

            double seconds = _realSeconds > 0 ? _realSeconds : 0;
            double sendRate = seconds > 0 ? filteredSend / seconds : 0;
            double recvRate = seconds > 0 ? filteredRecv / seconds : 0;

            bool valid = _windowOpen && seconds > 0;
            string? reason = valid ? null : "Window has no complete real-time interval yet.";

            return new FilteredNetworkSnapshot(
                IsValid: valid,
                InvalidReason: reason,
                WindowStartTicks: _windowStartTicks,
                WindowEndTicks: _windowEndTicks,
                RealIntervalSeconds: seconds,
                SourceSendBytes: _sourceSendBytes,
                SourceReceiveBytes: _sourceReceiveBytes,
                ExcludedSendBytes: _excludedSendBytes,
                ExcludedReceiveBytes: _excludedReceiveBytes,
                FilteredSendBytes: filteredSend,
                FilteredReceiveBytes: filteredRecv,
                FilteredSendBytesPerSecond: sendRate,
                FilteredReceiveBytesPerSecond: recvRate,
                RulesVersion: _rulesVersion,
                SessionGeneration: _sessionGeneration,
                AdapterIfIndex: _adapterIfIndex >= 0 ? _adapterIfIndex : null,
                EventsApplied: _eventsApplied,
                DuplicateEventsSuppressed: _duplicateEventsSuppressed,
                RetransmitBytesObserved: _retransmitBytesObserved,
                CopyEventsObserved: _copyEventsObserved,
                NegativeClamped: clamped,
                UnattributedEvents: _unattributedEvents,
                OtherAdapterEvents: _otherAdapterEvents,
                AdapterAmbiguous: _adapterAmbiguous);
        }
    }

    private void ResetCounters()
    {
        _sourceSendBytes = 0;
        _sourceReceiveBytes = 0;
        _excludedSendBytes = 0;
        _excludedReceiveBytes = 0;
        _eventsApplied = 0;
        _duplicateEventsSuppressed = 0;
        _retransmitBytesObserved = 0;
        _copyEventsObserved = 0;
        _unattributedEvents = 0;
        _otherAdapterEvents = 0;
        _adapterAmbiguous = false;
    }

    private readonly struct EventIdentity : IEquatable<EventIdentity>
    {
        private readonly long _ticks;
        private readonly byte _proto;
        private readonly byte _dir;
        private readonly IpKey _remote;
        private readonly IpKey _local;
        private readonly int _rport;
        private readonly int _lport;
        private readonly long _size;

        public EventIdentity(in NetworkTrafficEvent e)
        {
            _ticks = e.Timestamp.Ticks;
            _proto = (byte)e.Protocol;
            _dir = (byte)e.Direction;
            _remote = e.RemoteAddress;
            _local = e.LocalAddress;
            _rport = e.RemotePort;
            _lport = e.LocalPort;
            _size = e.SizeBytes;
        }

        public bool Equals(EventIdentity other) =>
            _ticks == other._ticks && _proto == other._proto && _dir == other._dir &&
            _rport == other._rport && _lport == other._lport && _size == other._size &&
            _remote.Equals(other._remote) && _local.Equals(other._local);

        public override bool Equals(object? obj) => obj is EventIdentity o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(_ticks, _proto, _dir, _rport, _lport, _size, _remote, _local);
    }
}
