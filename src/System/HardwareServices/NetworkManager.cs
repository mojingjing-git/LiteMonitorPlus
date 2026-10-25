using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    public class NetworkManager
    {
        // ★★★ [修复] 状态隔离：每个硬件拥有独立的网络状态，不再全局共享 ★★★
        private class NetworkState
        {
            public NetworkState(TimeProvider time)
            {
                // ★★★ [限频] 流量累计状态：真实采样间隔 + 原生字节计数基线 ★★★
                Accumulator = new TrafficAccumulator(time);
            }

            public INetworkAdapterSnapshot? NativeAdapter;
            /// <summary>上次尝试匹配原生网卡的单调时间戳（0 = 从未尝试）。</summary>
            public long LastMatchAttempt;
            public bool HasMatchAttempt;
            /// <summary>
            /// ★ [F03b] 有失效信号（网卡消失 / 地址变化 / 计数失配）：
            /// 允许**立即**重新匹配，不等重匹配节流。
            /// </summary>
            public bool ForceRematch;
            /// <summary>连续匹配失败次数（重匹配退避阶梯用）。</summary>
            public int ConsecutiveMatchFailures;
            /// <summary>本状态已处理到的"网络变更代次"（用于判定是否需要作废已锁定的网卡）。</summary>
            public long AppliedInvalidationGeneration;

            // 缓存 LHM 传感器
            public ISensor? CachedUpSensor;
            public ISensor? CachedDownSensor;

            /// <summary>流量累计状态（真实采样间隔 + 原生字节计数基线）。</summary>
            public readonly TrafficAccumulator Accumulator;
        }
        private readonly Dictionary<IHardware, NetworkState> _netStates = new();
        
        // 网络智能缓存
        private IHardware? _cachedNetHw;
        // 上次全盘扫描的时刻（单调时间戳；墙上时间回拨不会让缓存窗口失效）
        private long _lastNetScan;
        private bool _hasNetScan;
        // 启动时刻（单调时间戳）
        private readonly long _startTimestamp;
        // ★ [F03b] IP 地址缓存：**值 + 网络环境代次**（进程级共享，切换主题/重启服务不丢失）。
        //   网络变更事件到达时代次推进 → 旧值立即作废 → 下一次读取立刻重新取值。
        private readonly NetworkAddressCache _addressCache;
        // ★ [F03b] IP 地址刷新闸门：事件驱动优先 + 时间窗兜底 + 有界。
        private readonly NetworkRefreshGate _refreshGate;
        // ★ [F03b] 网卡枚举源（生产 = 真实网卡；测试 = 假网卡）。
        private readonly INetworkAdapterSource _adapters;
        // ★ [F03b] 网络变更事件源（生产 = NetworkChange；测试 = 假事件源）。
        private readonly INetworkChangeSource _changeSource;
        // ★ [F03b] 网络变更代次（单调）：事件到达时 +1，用来作废已锁定的原生网卡实例。
        private long _adapterInvalidationGeneration;

        // ★ [#96 / F03] 网络样本登记处：值 + 成功采样时间 + 设备身份 + 设备代次。
        //   旧实现把"最后有效值"放在永不失效的 lastValidMap 里，传感器停更时旧值会被
        //   无限期重发布为"新样本"（面板上的数字卡住不动）。这里用带时效的登记处替代它。
        private readonly NetworkSampleRegistry _samples;

        // ★ [F03] 设备代次：设备删除 / 首选变化 / 硬件重载时 +1（用于识别"旧值属于另一台设备"）。
        private long _deviceGeneration;

        // ★ [F03] 当前选中的网卡（手动选卡或自动选优的结果），用于样本的设备身份。
        private string _selectedDeviceName = "";

        // ★★★ 依赖注入：性能计数器 (用于获取 SMB 流量) ★★★
        private readonly PerformanceCounterManager _perfManager;

        // ★ [#610] IP 流量排除后端（可选注入）。由 HardwareMonitor 创建并接线。
        private NetworkTrafficExclusionService? _exclusion;

        // ★ [#610] 本地地址 → 网卡映射（ETW 载荷里没有网卡 ID，只能靠本地地址识别）。
        private NetworkAdapterResolver? _adapterResolver;
        private long _lastResolverRebuild;
        private bool _hasResolverRebuild;

        /// <summary>归属映射的最短重建间隔（秒）：地址热变更的刷新代价与及时性的折中。</summary>
        private const double ResolverRebuildIntervalSeconds = 30.0;

        /// <summary>
        /// [F03] 重选窗口（秒）：读取失败后最多每 3 秒触发一次全盘重选（去抖 + 退避）。
        /// </summary>
        private const double ReselectIntervalSeconds = 3.0;

        /// <summary>
        /// [F03] 自动选优评分里的"采样成功"权重：它必须大于任何可能的流量值，
        /// 这样"采样成功的空闲网卡"永远优于"采样失败的网卡"（空闲 ≠ 坏网卡）。
        /// </summary>
        private const double SampleSuccessBonus = 1e12;

        /// <summary>[F03] 虚拟网卡（VPN / 虚拟交换机等）的评分惩罚，保持既有"优先物理网卡"的语义。</summary>
        private const double VirtualAdapterPenalty = -1e9;

        // ★★★ [限频] 单调时钟：流量间隔必须来自两次真实采集之间的经过时间 ★★★
        private readonly TimeProvider _time;

        /// <summary>
        /// 采集统计计数器。
        /// 由拥有 <see cref="HardwareSamplingStatistics"/> 的服务（HardwareMonitor）在构造后接线，
        /// 接线后这里记录的是**真实执行了 Update 的设备次数**，而不是「进入更新路径的次数」。
        /// </summary>
        public HardwareSamplingStatistics? SamplingStatistics { get; set; }

        /// <summary>
        /// [F03b] 重匹配的最短重试间隔（秒）：匹配失败后的**快速重试**间隔。
        /// <para>这是退避阶梯的第一级 —— 单次失效信号触发的重试是"有界且快速"的，
        /// 不需要等满 10 秒才有机会恢复（网卡可能几秒后才被系统重新枚举到）。</para>
        /// </summary>
        private const double RematchRetryIntervalSeconds = 1.0;

        /// <summary>
        /// [F03b] 重匹配的退避阶梯上限（次）：连续失败超过该次数后，
        /// 退回到长节流（见 <see cref="RematchThrottleSeconds"/>），避免无失效信号时反复空转。
        /// </summary>
        private const int MaxImmediateRematchAttempts = 3;

        /// <summary>
        /// [F03b] 重匹配的长节流（秒）：**没有**失效信号时的重试间隔。
        /// <para>★ 节流只约束"无失效信号时不要反复空转"；一旦出现失效信号
        /// （网卡消失 / 地址变化 / 计数失配），重匹配会**立即**放行。</para>
        /// </summary>
        private const double RematchThrottleSeconds = 10.0;

        public NetworkManager(
            PerformanceCounterManager perfManager = null,
            TimeProvider? timeProvider = null,
            NetworkSampleFreshnessPolicy? freshnessPolicy = null,
            INetworkAdapterSource? adapterSource = null,
            INetworkChangeSource? changeSource = null,
            NetworkAddressCache? addressCache = null)
        {
            // 允许为空 (为了兼容性)，如果为空则内部功能自动禁用
            _perfManager = perfManager ?? new PerformanceCounterManager();
            _time = timeProvider ?? TimeProvider.System;
            _startTimestamp = _time.GetTimestamp();   // 启动时间（单调时钟）

            // ★ [#96 / F03] 样本时效策略与登记处：时钟必须与本类一致，测试可注入假时钟。
            _samples = new NetworkSampleRegistry(freshnessPolicy ?? new NetworkSampleFreshnessPolicy(_time));

            // ★ [F03b] 地址缓存与刷新闸门：事件驱动优先 + 时间窗兜底 + 有界。
            _addressCache = addressCache ?? NetworkAddressCache.Shared;
            _refreshGate = new NetworkRefreshGate(_time);

            // ★ [F03b] 网卡枚举源 / 网络变更事件源（测试可注入假实现）。
            _adapters = adapterSource ?? SystemNetworkAdapterSource.Instance;
            _changeSource = changeSource ?? SystemNetworkChangeSource.Instance;

            // [Fix #287] 监听网络变更事件，强制刷新 IP 和 网卡缓存
            // 当用户切换 WIFI 或插拔网线时，IP地址和网卡实例都会失效，必须重置
            // ★ [F03b] 现在同时监听**可用性**变更（Wi-Fi ↔ 有线切换时地址未必变化），
            //   并且是"立即失效"而不是"等 30 秒缓存窗过期"。
            _changeSource.Changed += OnNetworkChanged;
        }

        /// <summary>
        /// 网络变更事件（地址变更 / 可用性变更）到达。
        /// <para>★ [F03b] **事件驱动**：立刻作废 IP 缓存（推进共享代次），
        /// 下一次读取就会重新取值，不再受 30 秒缓存窗拖累；
        /// 同时作废已锁定的原生网卡实例，让重匹配**立即**放行。</para>
        /// </summary>
        private void OnNetworkChanged(object? sender, NetworkChangeKind kind)
        {
            // 旧 IP 立即作废（值本身保留在缓存里，但不再"有效"）。
            _addressCache.AdvanceGeneration();

            // 已锁定的原生网卡实例可能已失效 → 作废并允许立即重新匹配。
            Interlocked.Increment(ref _adapterInvalidationGeneration);
        }

        public void ClearCache()
        {
            _netStates.Clear();
            _cachedNetHw = null;
            // ★ [F03] 硬件重载：旧样本与速率差分基线一并作废，
            //   新设备不得继承上一台设备的累计量。
            _samples.InvalidateAll();
            _selectedDeviceName = "";
            Interlocked.Increment(ref _deviceGeneration);
        }

        // ===========================================================
        // ★ [F03] 网络样本：时效、失效与设备代次
        // ===========================================================

        /// <summary>网络样本登记处（值、成功采样时间、设备身份、设备代次）。</summary>
        public NetworkSampleRegistry Samples => _samples;

        /// <summary>设备代次：设备删除 / 首选变化 / 硬件重载时 +1。</summary>
        public long DeviceGeneration => Interlocked.Read(ref _deviceGeneration);

        /// <summary>当前选中的网卡名（空 = 尚未选出）。</summary>
        public string SelectedDeviceName => _selectedDeviceName;

        /// <summary>
        /// 作废网络样本缓存与速率差分基线。
        /// <para>调用时机：设备删除、首选网卡变化、硬件重载。</para>
        /// <para>★ 同时推进设备代次：旧样本带着旧代次，不会被当成新设备的读数。</para>
        /// </summary>
        public void InvalidateNetworkSamples()
        {
            _samples.InvalidateAll();
            // 差分基线一并作废：新设备不继承上一台设备的累计量（也不产生流量尖峰）。
            _netStates.Clear();
            _cachedNetHw = null;
            _selectedDeviceName = "";
            Interlocked.Increment(ref _deviceGeneration);
        }

        /// <summary>按配置刷新周期刷新"预定采样周期"（保留窗口 = 周期数 × 周期，上限 15 秒）。</summary>
        private void EnsureSampleCycle(Settings cfg)
        {
            double cycle = NetworkSampleFreshnessPolicy.ResolveSampleCycleSeconds(cfg.RefreshMs);
            if (Math.Abs(_samples.Policy.SampleCycleSeconds - cycle) > 1e-9)
            {
                _samples.Policy.SampleCycleSeconds = cycle;
            }
        }

        /// <summary>选中一台网卡：切换设备时立即作废旧设备的样本（不允许旧值冒充新设备）。</summary>
        private void SelectDevice(IHardware hw)
        {
            if (hw == null) return;
            if (string.Equals(_selectedDeviceName, hw.Name, StringComparison.Ordinal)) return;

            if (!string.IsNullOrEmpty(_selectedDeviceName))
            {
                _samples.InvalidateDevice(_selectedDeviceName);
                if (_cachedNetHw != null) _netStates.Remove(_cachedNetHw);
            }
            _selectedDeviceName = hw.Name;
            _cachedNetHw = hw;
            Interlocked.Increment(ref _deviceGeneration);
        }

        /// <summary>设备消失：立即作废该设备的样本与差分基线。</summary>
        private void OnDeviceLost(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return;

            // 旧值不得继续冒充当前读数：即使这台设备已经不是当前选择，也要作废它的样本。
            _samples.InvalidateDevice(deviceName);

            bool changed = false;
            if (_cachedNetHw != null && string.Equals(_cachedNetHw.Name, deviceName, StringComparison.Ordinal))
            {
                _netStates.Remove(_cachedNetHw);
                _cachedNetHw = null;
                changed = true;
            }
            if (string.Equals(_selectedDeviceName, deviceName, StringComparison.Ordinal))
            {
                _selectedDeviceName = "";
                changed = true;
            }
            // 设备代次只在**真的发生变化**时推进（避免每次读取都 +1）
            if (changed) Interlocked.Increment(ref _deviceGeneration);
        }

        // ===========================================================
        // ★ [#610] IP 流量排除后端接线
        // ===========================================================

        /// <summary>接入 IP 流量排除后端（由 HardwareMonitor 在构造后调用）。</summary>
        public void AttachTrafficExclusion(NetworkTrafficExclusionService service)
        {
            _exclusion = service;
        }

        /// <summary>
        /// 把「当前选中的网卡」告诉排除后端。
        /// <para>★ C1：ETW 载荷里没有网卡 ID，只能靠本地地址映射；目标网卡上的本地地址如果
        /// 同时出现在别的网卡上（归属歧义），必须**拒绝**，绝不能猜。</para>
        /// </summary>
        private void UpdateExclusionTarget(INetworkAdapterSnapshot nic)
        {
            var service = _exclusion;
            if (service == null) { return; }

            int? ifIndex = ResolveIfIndex(nic);
            if (ifIndex == null)
            {
                service.SetTargetAdapter(null, null);
                return;
            }

            NetworkAdapterResolver resolver = EnsureResolver();
            string? rejection = null;
            var ambiguous = resolver.AmbiguousAddressesOf(ifIndex.Value);
            if (ambiguous.Count > 0)
            {
                rejection = string.Join(", ", ambiguous.Select(a => a.ToString()));
            }
            else if (resolver.TryGetAdapter(ifIndex.Value, out var info) && info != null &&
                     info.Type == NetworkInterfaceType.Tunnel)
            {
                // ★ C1：VPN / 隧道网卡的归属未经验证 → 明确拒绝，不猜。
                rejection = "VPN/tunnel adapters are not supported for IP exclusion.";
            }
            service.SetTargetAdapter(ifIndex, rejection);
        }

        /// <summary>取网卡的接口索引；IPv6-only 网卡没有 IPv4 属性，退回按描述名匹配。</summary>
        private int? ResolveIfIndex(INetworkAdapterSnapshot nic)
        {
            int? index = nic.IfIndex;
            if (index.HasValue && index.Value > 0) { return index; }

            NetworkAdapterResolver? resolver = _adapterResolver;
            if (resolver == null) { return null; }
            foreach (NetworkAdapterInfo info in resolver.Adapters)
            {
                if (string.Equals(info.Description, nic.Description, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.FriendlyName, nic.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return info.IfIndex;
                }
            }
            return null;
        }

        /// <summary>按需重建归属映射（最多每 30 秒一次）。</summary>
        private NetworkAdapterResolver EnsureResolver()
        {
            long now = _time.GetTimestamp();
            if (_adapterResolver != null && _hasResolverRebuild &&
                _time.GetElapsedTime(_lastResolverRebuild, now).TotalSeconds < ResolverRebuildIntervalSeconds)
            {
                return _adapterResolver;
            }

            var rebuilt = NetworkAdapterResolver.Build();
            _adapterResolver = rebuilt;
            _lastResolverRebuild = now;
            _hasResolverRebuild = true;
            _exclusion?.SetAttributor(rebuilt);
            return rebuilt;
        }

        // ===========================================================
        // 更新逻辑 (原 UpdateAll 中的部分)
        // ===========================================================
        public void ProcessUpdate(IHardware hw, Settings cfg, double timeDelta, bool isSlowScanTick)
        {
            bool isTarget = (_cachedNetHw != null && hw == _cachedNetHw) ||
                            (hw.Name == cfg.LastAutoNetwork) ||
                            (hw.Name == cfg.PreferredNetwork);

            bool isStartupPhase = _time.GetElapsedTime(_startTimestamp, _time.GetTimestamp()).TotalSeconds < 3;

            if (isTarget)
            {
                hw.Update();
                // ★★★ [真实计数] 目标网卡真的执行了 Update 才计数 ★★★
                RecordDeviceUpdate(hw, "Network");
                AccumulateTraffic(hw, cfg, timeDelta);
                // ★ [F03] 目标网卡真的被更新过了 -> 新的网络样本轮次。
                //   样本寿命**只**在这里刷新：UI 读 / 快照读 / fallback 读都不刷新，
                //   否则停更的网卡会把同一个旧值反复当成"新样本"发布（#96 的冻结机制）。
                _samples.AdvanceSampleEpoch();
            }
            else if (isStartupPhase || IsVirtualNetwork(hw.Name))
            {
                return;
            }
            else if (isSlowScanTick)
            {
                hw.Update();
                // ★★★ [真实计数] 非目标网卡只在慢扫描轮真的执行了 Update 才计数 ★★★
                RecordDeviceUpdate(hw, "Network");
            }
        }

        /// <summary>
        /// 记录一次**真实发生**的设备更新。
        /// Key 格式与 HardwareMonitor.BuildDeviceKey 保持一致（"类型:名称"），
        /// 这样 A1 的统计计数器里 CPU/GPU/磁盘/网卡可以用同一套 Key 对比。
        /// </summary>
        private void RecordDeviceUpdate(IHardware hw, string category)
        {
            var statistics = SamplingStatistics;
            if (statistics == null) return;
            statistics.RecordDeviceUpdate(hw.HardwareType + ":" + hw.Name, category);
        }

        /// <summary>记录一次网络全盘扫描时刻（单调时钟）。</summary>
        private void MarkNetScanned()
        {
            _lastNetScan = _time.GetTimestamp();
            _hasNetScan = true;
        }

        /// <summary>距上次网络全盘扫描是否还在重选窗口内（单调时钟判断）。</summary>
        private bool IsNetScanFresh()
        {
            if (!_hasNetScan) return false;
            return _time.GetElapsedTime(_lastNetScan, _time.GetTimestamp()).TotalSeconds < ReselectIntervalSeconds;
        }

        /// <summary>
        /// 是否到了允许"有限重选"的时间点（单调时钟）。
        /// <para>★ [F03] 读取失败后的重选是**有界**的：最快每 <see cref="ReselectIntervalSeconds"/> 秒一次，
        /// 不会因为传感器持续失败而形成忙循环。</para>
        /// </summary>
        private bool IsReselectDue() => !IsNetScanFresh();

        /// <summary>
        /// ★ [F03b] 网络变更后作废已锁定的原生网卡实例。
        /// <para>网卡消失 / 地址变化 / 设备代次变化时，已缓存的 <see cref="NetworkInterface"/>
        /// 实例可能已经失效，必须作废并**允许立即重新匹配**（不等 10 秒节流）。</para>
        /// </summary>
        private void ApplyAdapterInvalidation(NetworkState state)
        {
            long generation = Interlocked.Read(ref _adapterInvalidationGeneration);
            if (state.AppliedInvalidationGeneration == generation) return;

            state.AppliedInvalidationGeneration = generation;
            state.NativeAdapter = null;
            state.ForceRematch = true;              // 立即允许重新匹配
            state.ConsecutiveMatchFailures = 0;
        }

        /// <summary>
        /// 获取当前 IP（**事件驱动优先 + 时间窗兜底**）。
        /// <para>★ [F03b] 语义：
        ///  - 网络变更事件到达 → 缓存值立即作废 → 下一次调用**立刻**重新取值（不等 30 秒）；
        ///  - 事件丢失 → 距上次成功取值超过 30 秒 → 时间窗兜底重新取值；
        ///  - 两者都**有界**：单轮最多 3 次真正取值，事件风暴不会拉爆网卡枚举频率。</para>
        /// </summary>
        public string GetCurrentIP()
        {
            NetworkAddressResult result = GetCurrentIPResult();
            // ★ 兼容既有契约：没有地址时返回 null（上层统一用 IsNullOrEmpty 判空）。
            //   绝**不**返回 "0.0.0.0" 之类的占位地址 —— 那是"冒充有效数据"。
            return result.HasAddress ? result.Address : null!;
        }

        /// <summary>
        /// 获取当前 IP 的**完整结果**（值 + 状态）。
        /// <para>★ "缺失"（网卡在但没有 IPv4）与"读取失败"（枚举出错）是**不同状态**，
        /// 绝不用 "0.0.0.0" 之类的占位地址冒充。</para>
        /// </summary>
        public NetworkAddressResult GetCurrentIPResult()
        {
            // 1. 缓存命中？—— 值未被网络变更作废，且时间窗未到期
            string cached = _addressCache.GetValidValue();
            bool cacheValid = cached.Length > 0;
            if (cacheValid && !_refreshGate.IsWindowExpired)
            {
                return NetworkAddressResult.Ok(cached);
            }

            // 2. 该不该真的去读一次网卡？—— 事件驱动优先 / 时间窗兜底 / 有界
            if (!_refreshGate.ShouldRefresh(cacheValid, _addressCache.Generation))
            {
                // 预算用尽：缓存还有效时保留"上次已知值"（过渡展示）；
                // 缓存已被事件作废时**不返回旧网卡的 IP**。
                return cacheValid
                    ? new NetworkAddressResult(cached, NetworkAddressStatus.Ok, isRetained: true)
                    : NetworkAddressResult.Missing;
            }

            // 3. 真的去读一次网卡
            NetworkAddressResult result = ResolveCurrentIP();
            if (result.Status == NetworkAddressStatus.Ok)
            {
                _addressCache.Store(result.Address);
                _refreshGate.MarkRefreshSucceeded();
                return result;
            }

            // 4. 没读到：兜底路径保留"上次已知值"；事件路径不返回旧网卡的 IP
            if (cacheValid)
            {
                return new NetworkAddressResult(cached, result.Status, isRetained: true);
            }
            return result;
        }

        /// <summary>
        /// 真正去网卡上读一次 IP。
        /// <para>★ "缺失"与"失败"分开：网卡在但没有 IPv4 = <see cref="NetworkAddressStatus.Missing"/>；
        /// 枚举网卡抛异常 = <see cref="NetworkAddressStatus.Failed"/>。</para>
        /// </summary>
        private NetworkAddressResult ResolveCurrentIP()
        {
            string? foundIP = null;
            bool enumerationFailed = false;

            // 策略A：直接从当前锁定的硬件中获取 (这是最直接的方式)
            try
            {
                // ★★★ 核心修复：即使 _cachedNetHw 为空（还没锁定网卡），也要尝试从 _netStates 中寻找已匹配 NativeAdapter 的网卡 ★★★
                // 之前的 bug 是：网速跳动说明 MatchNativeNetworkAdapter 成功了，_netStates 里有 NativeAdapter，
                // 但 _cachedNetHw 还没来得及更新，导致这里直接跳过。

                // 如果已锁定，优先用锁定的
                if (_cachedNetHw != null && _netStates.TryGetValue(_cachedNetHw, out var state))
                {
                    // ★ [F03b] 网络变更后已锁定的网卡实例已作废（避免读到旧网卡的 IP）。
                    ApplyAdapterInvalidation(state);
                    if (state.NativeAdapter != null)
                    {
                        foundIP = GetIPv4FromAdapter(state.NativeAdapter);
                    }
                }

                // 如果没锁定（或锁定的没取到），遍历所有已知状态的网卡 (这些网卡都是有流量活动的)
                if (string.IsNullOrEmpty(foundIP))
                {
                    foreach (var kv in _netStates)
                    {
                        // ★ [F03b] 网络变更后已锁定的网卡实例已作废（避免读到旧网卡的 IP）。
                        ApplyAdapterInvalidation(kv.Value);
                        if (kv.Value.NativeAdapter != null)
                        {
                            string ip = GetIPv4FromAdapter(kv.Value.NativeAdapter);
                            if (!string.IsNullOrEmpty(ip))
                            {
                                foundIP = ip;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. 策略B (兜底)：如果上面没拿到，才主动遍历系统网卡 (只跑一次)
            if (string.IsNullOrEmpty(foundIP))
            {
                try
                {
                    IReadOnlyList<INetworkAdapterSnapshot> nics = _adapters.GetAdapters();
                    foreach (var nic in nics)
                    {
                        if (!nic.IsOperationalUp) continue;
                        if (nic.IsLoopback) continue;
                        if (IsVirtualNetwork(nic.Description) || IsVirtualNetwork(nic.Name)) continue; // 仅兜底物理网卡

                        foundIP = GetIPv4FromAdapter(nic);
                        if (!string.IsNullOrEmpty(foundIP)) break;
                    }
                }
                catch
                {
                    // ★ 枚举网卡本身失败 = **读取失败**（与"没有 IPv4 地址"不同）
                    enumerationFailed = true;
                }
            }

            if (!string.IsNullOrEmpty(foundIP))
            {
                return NetworkAddressResult.Ok(foundIP);
            }

            // ★ 网卡在但没有 IPv4 地址 = **缺失**（有效事实）；枚举失败 = **故障**。
            return enumerationFailed ? NetworkAddressResult.Failed : NetworkAddressResult.Missing;
        }

        /// <summary>
        /// 取网卡的"最佳"IPv4 地址（没有则 null）。
        /// <para>保留既有语义：[Fix #287] 忽略 APIPA（169.254.x.x）；
        /// [Fix #206] 优先 192.168.x.x 内网地址。</para>
        /// </summary>
        private string GetIPv4FromAdapter(INetworkAdapterSnapshot nic)
        {
            try
            {
                string? bestIP = null;

                foreach (string ipStr in nic.GetIPv4Addresses())
                {
                    // [Fix #287] 忽略 APIPA (169.254.x.x) 地址
                    // 这些地址通常是 DHCP 失败时的临时地址，会导致网页版服务绑定错误
                    if (ipStr.StartsWith("169.254.")) continue;

                    // [Fix #206] 优先返回 192.168.x.x 类型的内网 IP
                    if (ipStr.StartsWith("192.168."))
                    {
                        return ipStr;
                    }

                    // 如果还没找到最佳 IP，暂时存第一个找到的 IPv4 作为兜底
                    if (bestIP == null)
                    {
                        bestIP = ipStr;
                    }
                }
                return bestIP;
            }
            catch { }
            return null;
        }

        // ===========================================================
        // 获取最佳网络数值 (已简化：Preferred 逻辑已移至 ValueProvider 静态缓存)
        // ===========================================================

        /// <summary>
        /// 取当前最佳网络样本（值 + 质量）。
        /// <para>★ [F03] 与旧实现的差别：
        ///  - 旧实现回退到**永不失效**的 lastValidMap，传感器停更时旧值被无限期重发布（#96）；
        ///  - 现在回退只允许使用"保留期内"的样本（Stale），到期后一律 Missing（显示缺失）；
        ///  - 自动模式按"存活 + 采样成功 + 有效流量"重新评分，**读取失败**才触发有限重选；
        ///    空闲（0 流量）不算坏网卡，不会因此反复换卡；
        ///  - 手动模式设备消失时**保留选择**并显示不可用，不静默改成自动选卡。</para>
        /// </summary>
        public NetworkSample GetBestSample(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            if (computer == null) return NetworkSample.Missing;
            EnsureSampleCycle(cfg);

            bool manual = !string.IsNullOrEmpty(cfg.PreferredNetwork);
            string preferredName = cfg.PreferredNetwork;

            // ============ 手动模式：设备消失时保留选择并显示不可用 ============
            if (manual)
            {
                var preferred = computer.Hardware.FirstOrDefault(h =>
                    h.HardwareType == HardwareType.Network &&
                    string.Equals(h.Name, preferredName, StringComparison.OrdinalIgnoreCase));

                if (preferred == null)
                {
                    // ★ 设备已消失：立即作废它的样本（不允许旧值继续冒充当前读数）
                    OnDeviceLost(preferredName);
                    return GetRetainedSample(key, lastValidMap, syncLock);
                }

                SelectDevice(preferred);
                return ReadNetworkSensor(preferred, key, lastValidMap, syncLock);
            }

            // ============ 自动模式 ============
            // A. 运行时缓存的网卡
            if (_cachedNetHw != null)
            {
                // ★★★ 【修复 1】存活检查：如果缓存的硬件对象已经不在当前的硬件列表中（已失效），强制丢弃 ★★★
                if (!computer.Hardware.Contains(_cachedNetHw))
                {
                    // ★ 设备删除：立即失效该设备的样本缓存与速率差分基线
                    OnDeviceLost(_cachedNetHw.Name);
                }
                else
                {
                    NetworkSample sample = ReadNetworkSensor(_cachedNetHw, key, lastValidMap, syncLock);
                    if (sample.Quality == NetworkSampleQuality.Fresh)
                    {
                        // ★ 采样成功（0 也是有效值）：直接用，不触发重选。
                        //   只有"读取失败"才需要重选 —— 空闲不等于坏网卡。
                        //   （保留旧逻辑的节流形状：有流量直接用；空闲时允许重新评分，
                        //     但分数相同会保持当前选择，不会来回换卡。）
                        if (sample.Value > 0.1f || !IsReselectDue()) return sample;
                    }
                    else
                    {
                        // 读取失败：有限重选（受重选窗口约束）；窗口内不重选，直接给保留期内的旧样本
                        if (!IsReselectDue()) return sample;
                    }
                }
            }

            // ★★★ [漏掉的部分] B. 尝试启动时缓存 (Settings 中的记录) ★★★
            if (_cachedNetHw == null && !string.IsNullOrEmpty(cfg.LastAutoNetwork))
            {
                // 尝试直接找上次记住的网卡
                var savedHw = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Network && h.Name == cfg.LastAutoNetwork);
                if (savedHw != null)
                {
                    // 找到了！直接设为缓存，跳过全盘扫描
                    SelectDevice(savedHw);
                    MarkNetScanned();
                    NetworkSample sample = ReadNetworkSensor(savedHw, key, lastValidMap, syncLock);
                    if (sample.Quality == NetworkSampleQuality.Fresh) return sample;
                }
            }

            // C. 全盘扫描（自动选优）
            IHardware? bestHw = ScanBestAdapter(computer, key, out ISensor? bestTarget);

            // D. 更新缓存
            if (bestHw != null)
            {
                SelectDevice(bestHw);
                MarkNetScanned();

                // ★★★ [漏掉的部分] 记住这次的选择 ★★★
                if (cfg.LastAutoNetwork != bestHw.Name)
                {
                    cfg.LastAutoNetwork = bestHw.Name;
                }
            }

            // E. 返回
            if (bestHw != null && bestTarget?.Value is float v && !float.IsNaN(v))
            {
                // ★ 成功采样：登记值与成功时间（读缓存不会刷新寿命）
                return _samples.MarkSampled(key, v, bestHw.Name, DeviceGeneration);
            }

            // 扫描也没拿到有效样本：只允许回退到保留期内的旧样本
            return GetRetainedSample(key, lastValidMap, syncLock);
        }

        /// <summary>兼容入口：只要数值（不含质量）。</summary>
        public float? GetBestValue(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            NetworkSample sample = GetBestSample(key, computer, cfg, lastValidMap, syncLock);
            return sample.HasValue ? sample.Value : (float?)null;
        }

        /// <summary>
        /// 全盘扫描并选出最佳网卡。
        /// <para>★ [F03] 评分顺序：**先"采样成功"，再"有效流量"**。
        /// 空闲（0 流量）但采样成功的网卡，永远优于采样失败的网卡；
        /// 分数相同则保持当前选择，避免在两块都空闲的网卡之间来回跳。</para>
        /// </summary>
        private IHardware? ScanBestAdapter(Computer computer, string key, out ISensor? bestTarget)
        {
            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            double currentScore = double.MinValue;
            bestTarget = null;

            foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
            {
                ISensor? up = null, down = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => SensorMap.Has(s.Name, k))) up ??= s;
                    if (_downKW.Any(k => SensorMap.Has(s.Name, k))) down ??= s;
                }
                if (up == null && down == null) continue;

                ISensor? target = key == "NET.Up" ? up : down;
                // ★ 没有对应方向的传感器 -> 不参与本次选优（否则选中的网卡根本给不出这个值）
                if (target == null) continue;

                float? upValue = up?.Value;
                float? downValue = down?.Value;
                bool upOk = upValue.HasValue && !float.IsNaN(upValue.Value);
                bool downOk = downValue.HasValue && !float.IsNaN(downValue.Value);
                float? targetValue = target?.Value;
                bool keyOk = targetValue.HasValue && !float.IsNaN(targetValue.Value);

                double traffic = (upOk ? upValue!.Value : 0) + (downOk ? downValue!.Value : 0);
                double score = (keyOk ? SampleSuccessBonus : 0)
                             + traffic
                             + (IsVirtualNetwork(hw.Name) ? VirtualAdapterPenalty : 0);

                if (hw == _cachedNetHw) currentScore = score;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = target;
                }
            }

            // 分数相同（或当前网卡更好）时保持当前选择：不要因为"另一张卡刚好更忙"来回换卡。
            if (_cachedNetHw != null && bestHw != null && bestScore <= currentScore)
            {
                bestHw = _cachedNetHw;
                bestTarget = FindThroughputSensor(_cachedNetHw, key);
            }

            return bestHw;
        }

        /// <summary>按 Key 找到对应的 Throughput 传感器。</summary>
        private ISensor? FindThroughputSensor(IHardware hw, string key)
        {
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "NET.Up" && _upKW.Any(k => SensorMap.Has(s.Name, k))) return s;
                if (key == "NET.Down" && _downKW.Any(k => SensorMap.Has(s.Name, k))) return s;
            }
            return null;
        }

        /// <summary>
        /// 取保留期内的旧样本。
        /// <para>★ [F03] 旧实现这里是"永不失效"的 lastValidMap 冻结回退（#96 的根因）；
        /// 现在只允许使用仍在保留期内的样本；从未登记过的旧值只会被**一次性采纳**（寿命有界）。</para>
        /// </summary>
        private NetworkSample GetRetainedSample(string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            float? legacy = null;
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out float lv)) legacy = lv; }
            return _samples.GetOrAdopt(key, legacy, _selectedDeviceName, DeviceGeneration);
        }

        private NetworkSample ReadNetworkSensor(IHardware hw, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            ISensor? target = FindThroughputSensor(hw, key);

            if (target?.Value is float v && !float.IsNaN(v))
            {
                // ★ 成功采样：登记值与成功时间。
                //   读缓存**不会**刷新寿命：登记处的"样本轮次"没变时只更新值。
                return _samples.MarkSampled(key, v, hw.Name, DeviceGeneration);
            }

            // ★ 读取失败：不再回退到"永不失效"的旧值，只回退保留期内的样本。
            return GetRetainedSample(key, lastValidMap, syncLock);
        }

        // ===========================================================
        // 流量累积与匹配 (原 HardwareMonitor.cs 核心逻辑)
        // ===========================================================
        private void AccumulateTraffic(IHardware hw, Settings cfg, double seconds)
        {
            // 1. 获取或创建当前硬件的独立状态
            if (!_netStates.TryGetValue(hw, out var state))
            {
                state = new NetworkState(_time);
                _netStates[hw] = state;
            }

            // A. LHM 估算速率 (bytes/sec)
            if (state.CachedUpSensor == null || state.CachedDownSensor == null)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => SensorMap.Has(s.Name, k))) state.CachedUpSensor ??= s;
                    if (_downKW.Any(k => SensorMap.Has(s.Name, k))) state.CachedDownSensor ??= s;
                }
            }
            double lhmUpRate = state.CachedUpSensor?.Value ?? 0;
            double lhmDownRate = state.CachedDownSensor?.Value ?? 0;

            // B. 原生精准值
            MatchNativeNetworkAdapter(hw.Name, state);

            long? nativeUp = null;
            long? nativeDown = null;
            if (state.NativeAdapter != null)
            {
                if (state.NativeAdapter.TryGetTrafficBytes(out long sent, out long received))
                {
                    nativeUp = sent;
                    nativeDown = received;
                }
                else
                {
                    // ★ [F03b] 读计数失败 = 网卡实例已失效 → 立即允许重新匹配（不等 10 秒节流）。
                    state.NativeAdapter = null;
                    state.ForceRematch = true;
                    state.ConsecutiveMatchFailures = 0;
                }

                // ★ [#610] 把选中的网卡告诉排除后端（归属靠本地地址映射）。
                //   注意：这**不改写**累计口径，累计仍然走原有的原生字节 + SMB 估算路径。
                if (state.NativeAdapter != null)
                {
                    UpdateExclusionTarget(state.NativeAdapter);
                }
            }

            // C. 累计：真实采样间隔 + 原生字节计数优先 + SMB 扣除 + 安全阀
            var sample = new TrafficSample
            {
                LhmUpRate = lhmUpRate,
                LhmDownRate = lhmDownRate,
                NativeUpBytes = nativeUp,
                NativeDownBytes = nativeDown
            };

            // SMB 扣除（沿用旧语义：仅在开启开关且计数器就绪时生效）
            Func<double, (long Up, long Down)>? smbEstimator = null;
            if (cfg.IgnoreSmbTraffic && _perfManager.IsInitialized)
            {
                smbEstimator = s => _perfManager.GetEstimatedSmbBytes(s);
            }

            var result = state.Accumulator.Accumulate(sample, seconds, smbEstimator);

            if (result.NativeMismatch)
            {
                // 匹配错误：丢弃当前适配器，下次**立即**重新匹配（不等 10 秒节流）
                state.NativeAdapter = null;
                state.ForceRematch = true;
                state.ConsecutiveMatchFailures = 0;
            }

            // D. 存入数据
            if (result.UpBytes > 0 || result.DownBytes > 0)
            {
                cfg.SessionUploadBytes += result.UpBytes;
                cfg.SessionDownloadBytes += result.DownBytes;
                TrafficLogger.AddTraffic(result.UpBytes, result.DownBytes);
            }
        }

        /// <summary>
        /// 匹配 LHM 网卡与原生 <see cref="System.Net.NetworkInformation.NetworkInterface"/>。
        /// <para>★ [F03b] 节流语义从"固定 10 秒"改为
        /// <b>事件驱动优先 + 时间窗兜底</b>：
        ///  - 有失效信号（网络变更事件 / 计数读取失败 / 匹配失配）→ <b>立即</b>允许重匹配；
        ///  - 连续失败但仍在快速重试预算内 → 每 <see cref="RematchRetryIntervalSeconds"/> 秒重试一次；
        ///  - 预算用尽且没有新失效信号 → 退避到 <see cref="RematchThrottleSeconds"/> 秒，
        ///    避免"无失效信号时反复空转"。</para>
        /// </summary>
        private void MatchNativeNetworkAdapter(string lhmName, NetworkState state)
        {
            // ★ [F03b] 网络变更事件 → 立即作废已锁定的原生网卡实例（并允许立即重匹配）。
            ApplyAdapterInvalidation(state);

            if (state.NativeAdapter != null) return;

            long now = _time.GetTimestamp();
            bool hasAttempt = state.HasMatchAttempt;
            double elapsed = hasAttempt
                ? _time.GetElapsedTime(state.LastMatchAttempt, now).TotalSeconds
                : double.MaxValue;

            // 退避阶梯：有失效信号 → 立即；快速重试预算内 → 1 秒；预算用尽 → 10 秒。
            double required = state.ForceRematch ? 0.0
                           : (state.ConsecutiveMatchFailures < MaxImmediateRematchAttempts
                               ? RematchRetryIntervalSeconds
                               : RematchThrottleSeconds);

            if (hasAttempt && elapsed < required) return;

            state.LastMatchAttempt = now;
            state.HasMatchAttempt = true;
            state.ForceRematch = false;   // 本次尝试已消耗"立即重试"资格

            bool matched = false;
            try
            {
                IReadOnlyList<INetworkAdapterSnapshot> nics = _adapters.GetAdapters();

                // 预先分配令牌列表容量
                var lhmTokens = new List<string>(capacity: 10);
                SplitTokens(lhmName, lhmTokens);

                foreach (var nic in nics)
                {
                    // 1. 匹配连接名称
                    if (nic.Name.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); matched = state.NativeAdapter != null; break; }
                    // 2. 匹配硬件描述
                    if (nic.Description.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); matched = state.NativeAdapter != null; break; }
                    // 3. 模糊匹配
                    if (lhmTokens.Count > 0 && lhmName.Length > 5)
                    {
                        var nicTokens = new List<string>(capacity: 10);
                        SplitTokens(nic.Description, nicTokens);

                        // 优化匹配算法，减少内存分配
                        int matchCount = 0;
                        foreach (var token in lhmTokens)
                        {
                            if (nicTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                            {
                                matchCount++;
                                if (matchCount > 2) break; // 提前退出，满足条件即可
                            }
                        }

                        if (matchCount > 2 && (double)matchCount / lhmTokens.Count > 0.6)
                        {
                            SetNativeAdapter(nic, state);
                            matched = state.NativeAdapter != null;
                            break;
                        }
                    }
                }
            }
            catch
            {
                state.NativeAdapter = null;
                matched = false;
            }

            state.ConsecutiveMatchFailures = matched ? 0 : state.ConsecutiveMatchFailures + 1;
        }
        private static readonly char[] _tokenSeparators = { ' ', '(', ')', '[', ']', '-', '_', '#' };
        // 优化的SplitTokens方法，使用预分配的列表减少内存分配
        private void SplitTokens(string input, List<string> result)
        {
            result.Clear();
            int startIndex = 0;
            int length = input.Length;
            //char[] _tokenSeparators = { ' ', '(', ')', '[', ']', '-', '_', '#' };
            
            for (int i = 0; i < length; i++)
            {
                if (Array.IndexOf(_tokenSeparators, input[i]) >= 0)
                {
                    if (i > startIndex)
                    {
                        result.Add(UIUtils.Intern(input.Substring(startIndex, i - startIndex)));
                    }
                    startIndex = i + 1;
                }
            }
            
            if (startIndex < length)
            {
                result.Add(UIUtils.Intern(input.Substring(startIndex)));
            }
        }
        
        // 保持向后兼容性
        private List<string> SplitTokens(string input)
        {
            var result = new List<string>(capacity: 10);
            SplitTokens(input, result);
            return result;
        }

        private void SetNativeAdapter(INetworkAdapterSnapshot nic, NetworkState state)
        {
            state.NativeAdapter = nic;
            if (!nic.TryGetTrafficBytes(out long sent, out long received))
            {
                state.NativeAdapter = null;
                return;
            }
            // ★★★ [网卡切换] 重新建立原生字节计数基线：切换后的第一次采样不产生增量 ★★★
            state.Accumulator.ResetBaseline(sent, received);
        }

        private bool IsVirtualNetwork(string name)
        {
            foreach (var k in _virtualNicKW)
            {
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static readonly string[] _upKW = { "upload", "up", "sent", "send", "tx", "transmit" };
        private static readonly string[] _downKW = { "download", "down", "received", "receive", "rx" };
        private static readonly string[] _virtualNicKW = { "virtual", "vmware", "hyper-v", "hyper v", "vbox", "loopback", "tunnel", "tap", "tun", "bluetooth", "zerotier", "tailscale", "wan miniport" };
    }

    /// <summary>
    /// 一次流量采样的输入。
    ///
    /// - LHM 速率：LibreHardwareMonitor 提供的 Throughput 传感器值（单位 bytes/sec）；
    /// - 原生字节计数：网卡的累计收发字节数（null 表示原生计数不可用，例如还没匹配到网卡）。
    /// </summary>
    public readonly struct TrafficSample
    {
        /// <summary>上传速率（bytes/sec）。</summary>
        public double LhmUpRate { get; init; }
        /// <summary>下载速率（bytes/sec）。</summary>
        public double LhmDownRate { get; init; }
        /// <summary>网卡累计发送字节（null = 不可用）。</summary>
        public long? NativeUpBytes { get; init; }
        /// <summary>网卡累计接收字节（null = 不可用）。</summary>
        public long? NativeDownBytes { get; init; }
    }

    /// <summary>一次流量累计的结果。</summary>
    public readonly struct TrafficAccumulationResult
    {
        public TrafficAccumulationResult(long upBytes, long downBytes, double intervalSeconds,
            bool nativeAvailable, bool nativeMismatch, bool resetByLongGap)
            : this(upBytes, downBytes, intervalSeconds, intervalSeconds, nativeAvailable, nativeMismatch, resetByLongGap)
        {
        }

        public TrafficAccumulationResult(long upBytes, long downBytes, double intervalSeconds, double realIntervalSeconds,
            bool nativeAvailable, bool nativeMismatch, bool resetByLongGap)
        {
            UpBytes = upBytes;
            DownBytes = downBytes;
            IntervalSeconds = intervalSeconds;
            RealIntervalSeconds = realIntervalSeconds;
            NativeAvailable = nativeAvailable;
            NativeMismatch = nativeMismatch;
            ResetByLongGap = resetByLongGap;
        }

        /// <summary>本次计入的上传字节。</summary>
        public long UpBytes { get; }
        /// <summary>本次计入的下载字节。</summary>
        public long DownBytes { get; }
        /// <summary>
        /// 本次**换算实际使用**的时间间隔（秒）。
        /// 超长间隔（重置）分支固定为 0（该轮不计入任何流量）。
        /// 测得的真实经过时间见 <see cref="RealIntervalSeconds"/>。
        /// </summary>
        public double IntervalSeconds { get; }
        /// <summary>
        /// 本次采样测得的**真实经过时间**（秒，单调时钟）。
        /// 与 <see cref="IntervalSeconds"/> 的区别：超长间隔时这里是真实值，
        /// 而换算用的 <see cref="IntervalSeconds"/> 为 0。
        /// </summary>
        public double RealIntervalSeconds { get; }
        /// <summary>本次是否拿到了原生字节计数。</summary>
        public bool NativeAvailable { get; }
        /// <summary>原生计数与 LHM 估算明显不符（调用方应丢弃当前网卡适配器）。</summary>
        public bool NativeMismatch { get; }
        /// <summary>本次因超长间隔触发了重置策略（不计入任何流量）。</summary>
        public bool ResetByLongGap { get; }
    }

    /// <summary>
    /// 流量累计器：把「两次真实采集之间的经过时间」与字节计数换算成本次增量。
    ///
    /// 关键语义（计划 §4.3 / §6.1）：
    ///  - 时间间隔来自**单调时钟**上两次真实采样的经过时间，不是 UI Tick 数乘刷新间隔；
    ///  - **原生字节计数优先**；原生计数与 LHM 估算明显不符时判定为匹配错误并回退到 LHM；
    ///  - 保留**首次计数初始化**（第一次只有基线，增量为 0）与**计数重置**（不产生负增量）；
    ///  - **休眠 / 超长间隔**按重置策略处理：重建基线、本轮不计入流量，不补造流量尖峰；
    ///  - **墙上时间回拨**不会得到负 delta（间隔被夹到 0）。
    ///
    /// 本类不依赖任何真实网卡对象，单元测试可以用假时钟 + 构造的字节数直接驱动。
    /// </summary>
    public sealed class TrafficAccumulator
    {
        /// <summary>默认的最大可信采样间隔（秒）。超过则按「超长间隔」重置处理。</summary>
        public const double DefaultMaxSampleIntervalSeconds = 30.0;

        /// <summary>单次增量安全阀：超过 10GB 视为异常丢弃。</summary>
        public const long MaxSingleDeltaBytes = 10737418240L;

        /// <summary>原生计数与 LHM 估算的失配阈值（50KB）。</summary>
        private const long MismatchThresholdBytes = 51200;

        private readonly TimeProvider _time;
        private readonly double _maxSampleIntervalSeconds;

        private long _lastSampleTimestamp;   // 上一次真实采样的单调时间戳
        private bool _hasLastSample;
        private long _baselineUp;            // 原生字节计数基线
        private long _baselineDown;
        private bool _hasBaseline;

        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        /// <param name="maxSampleIntervalSeconds">最大可信采样间隔（秒），超过按超长间隔重置。</param>
        public TrafficAccumulator(TimeProvider? timeProvider = null, double maxSampleIntervalSeconds = DefaultMaxSampleIntervalSeconds)
        {
            _time = timeProvider ?? TimeProvider.System;
            _maxSampleIntervalSeconds = maxSampleIntervalSeconds > 0 ? maxSampleIntervalSeconds : DefaultMaxSampleIntervalSeconds;
        }

        /// <summary>上一次真实采样使用的间隔（秒）。</summary>
        public double LastIntervalSeconds { get; private set; }

        /// <summary>是否已经建立过原生字节计数基线。</summary>
        public bool HasBaseline => _hasBaseline;

        /// <summary>
        /// 重新建立原生字节计数基线（网卡切换 / 首次匹配到网卡时调用）。
        /// 之后的第一次采样增量为 0。
        /// </summary>
        public void ResetBaseline(long upBytes, long downBytes)
        {
            _baselineUp = upBytes;
            _baselineDown = downBytes;
            _hasBaseline = true;
        }

        /// <summary>
        /// 累计一次采样。
        /// </summary>
        /// <param name="sample">本次采样数据。</param>
        /// <param name="fallbackIntervalSeconds">
        /// 调用方传入的采样间隔，仅在**第一次**采样（还没有上一次真实采样时间）时使用；
        /// 之后一律使用单调时钟测得的真实经过时间。
        /// </param>
        /// <param name="smbEstimator">可选的 SMB 流量估算器（入参是本次间隔）。</param>
        public TrafficAccumulationResult Accumulate(
            TrafficSample sample,
            double fallbackIntervalSeconds,
            Func<double, (long Up, long Down)>? smbEstimator = null)
        {
            double realInterval = ResolveInterval(fallbackIntervalSeconds);
            LastIntervalSeconds = realInterval;

            bool longGap = realInterval > _maxSampleIntervalSeconds;
            if (longGap)
            {
                // 超长间隔（休眠 / 长时间暂停）：按重置策略处理，不补造流量尖峰
                // 注意：换算用的间隔是 0（该轮不计入任何流量），真实间隔记录在 RealIntervalSeconds。
                bool hadNative = sample.NativeUpBytes.HasValue && sample.NativeDownBytes.HasValue;
                if (hadNative)
                {
                    _baselineUp = sample.NativeUpBytes!.Value;
                    _baselineDown = sample.NativeDownBytes!.Value;
                    _hasBaseline = true;
                }
                return new TrafficAccumulationResult(0, 0, 0, realInterval, hadNative, false, true);
            }

            double interval = realInterval;

            // ---- 原生字节计数（优先） ----
            bool nativeAvailable = sample.NativeUpBytes.HasValue && sample.NativeDownBytes.HasValue;
            long nativeUp = 0;
            long nativeDown = 0;

            if (nativeAvailable)
            {
                long currentUp = sample.NativeUpBytes!.Value;
                long currentDown = sample.NativeDownBytes!.Value;

                if (!_hasBaseline)
                {
                    // 首次计数初始化：只建立基线，本轮增量为 0
                    _baselineUp = currentUp;
                    _baselineDown = currentDown;
                    _hasBaseline = true;
                }
                else
                {
                    // 计数重置（换网卡 / 计数器回绕）不会产生负增量
                    nativeUp = Math.Max(0, currentUp - _baselineUp);
                    nativeDown = Math.Max(0, currentDown - _baselineDown);
                    _baselineUp = currentUp;
                    _baselineDown = currentDown;
                }
            }

            // ---- LHM 速率估算（原生不可用或失配时使用） ----
            long lhmUp = RateToBytes(sample.LhmUpRate, interval);
            long lhmDown = RateToBytes(sample.LhmDownRate, interval);

            long finalUp;
            long finalDown;
            bool nativeMismatch = false;

            if (nativeAvailable)
            {
                if ((nativeUp + nativeDown == 0) && (lhmUp + lhmDown > MismatchThresholdBytes))
                {
                    // 原生计数为 0 但 LHM 说有流量 -> 认为匹配错了网卡，回退到 LHM 估算
                    nativeMismatch = true;
                    finalUp = lhmUp;
                    finalDown = lhmDown;
                }
                else
                {
                    finalUp = nativeUp;
                    finalDown = nativeDown;
                }
            }
            else
            {
                finalUp = lhmUp;
                finalDown = lhmDown;
            }

            // ---- SMB 扣除 ----
            if (smbEstimator != null)
            {
                try
                {
                    var smb = smbEstimator(interval);
                    if (smb.Up > 0) finalUp = Math.Max(0, finalUp - smb.Up);
                    if (smb.Down > 0) finalDown = Math.Max(0, finalDown - smb.Down);
                }
                catch { }
            }

            // ---- 安全阀：单次增量超过 10GB 视为异常丢弃 ----
            if (finalUp > MaxSingleDeltaBytes || finalDown > MaxSingleDeltaBytes)
            {
                finalUp = 0;
                finalDown = 0;
            }

            return new TrafficAccumulationResult(finalUp, finalDown, interval, realInterval, nativeAvailable, nativeMismatch, false);
        }

        /// <summary>
        /// 解析本次采样间隔：
        ///  - 已有上一次真实采样：用**单调时钟**测得的经过时间（墙上时间回拨不影响）；
        ///  - 第一次采样：退回到调用方传入的间隔（负数/异常值按 0 处理）。
        /// </summary>
        private double ResolveInterval(double fallbackIntervalSeconds)
        {
            long now = _time.GetTimestamp();
            double interval;

            if (_hasLastSample)
            {
                interval = _time.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds;
            }
            else
            {
                interval = fallbackIntervalSeconds;
            }

            _lastSampleTimestamp = now;
            _hasLastSample = true;

            if (double.IsNaN(interval) || double.IsInfinity(interval) || interval < 0)
            {
                // 墙上时间回拨 / 异常值：不允许出现负间隔
                interval = 0;
            }

            return interval;
        }

        private static long RateToBytes(double ratePerSecond, double seconds)
        {
            if (ratePerSecond <= 0 || seconds <= 0) return 0;
            double bytes = ratePerSecond * seconds;
            if (bytes >= long.MaxValue) return long.MaxValue;
            return (long)bytes;
        }
    }
}