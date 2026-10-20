using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
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

            public NetworkInterface? NativeAdapter;
            /// <summary>上次尝试匹配原生网卡的单调时间戳（0 = 从未尝试）。</summary>
            public long LastMatchAttempt;
            public bool HasMatchAttempt;
            
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
        // [修改] IP 值静态缓存：切换主题/重启服务时不丢失，解决闪烁问题
        //      （只缓存"值"；"多久之内可以复用"的时间窗是**实例级**的，避免全局静态时间戳互相污染）
        private static string _staticIPCache = "";
        // 上次检查IP的时间（单调时间戳，实例级）
        private long _lastIPCheckTimestamp;
        private bool _hasIPCheck;
        private volatile bool _shouldResetAdapters = false; // [Fix #287] 网络变更标记

        // ★★★ 依赖注入：性能计数器 (用于获取 SMB 流量) ★★★
        private readonly PerformanceCounterManager _perfManager;

        // ★★★ [限频] 单调时钟：流量间隔必须来自两次真实采集之间的经过时间 ★★★
        private readonly TimeProvider _time;

        /// <summary>
        /// 采集统计计数器。
        /// 由拥有 <see cref="HardwareSamplingStatistics"/> 的服务（HardwareMonitor）在构造后接线，
        /// 接线后这里记录的是**真实执行了 Update 的设备次数**，而不是「进入更新路径的次数」。
        /// </summary>
        public HardwareSamplingStatistics? SamplingStatistics { get; set; }

        public NetworkManager(PerformanceCounterManager perfManager = null, TimeProvider? timeProvider = null)
        {
            // 允许为空 (为了兼容性)，如果为空则内部功能自动禁用
            _perfManager = perfManager ?? new PerformanceCounterManager(); 
            _time = timeProvider ?? TimeProvider.System;
            _startTimestamp = _time.GetTimestamp();   // 启动时间（单调时钟）
            
            // [Fix #287] 监听网络地址变更事件，强制刷新 IP 和 网卡缓存
            // 当用户切换 WIFI 或插拔网线时，IP地址和网卡实例都会失效，必须重置
            NetworkChange.NetworkAddressChanged += (s, e) => {
                _staticIPCache = null; 
                _shouldResetAdapters = true;
            };
        }

        public void ClearCache()
        {
            _netStates.Clear();
            _cachedNetHw = null;
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

        /// <summary>距上次网络全盘扫描是否还在 3 秒缓存期内（单调时钟判断）。</summary>
        private bool IsNetScanFresh()
        {
            if (!_hasNetScan) return false;
            return _time.GetElapsedTime(_lastNetScan, _time.GetTimestamp()).TotalSeconds < 3;
        }

        /// <summary>记录一次 IP 检查时刻（单调时钟，实例级）。</summary>
        private void MarkIPChecked()
        {
            _lastIPCheckTimestamp = _time.GetTimestamp();
            _hasIPCheck = true;
        }

        /// <summary>距上次 IP 检查是否还在 30 秒缓存期内（单调时钟判断）。</summary>
        private bool IsIPCheckFresh()
        {
            if (!_hasIPCheck) return false;
            return _time.GetElapsedTime(_lastIPCheckTimestamp, _time.GetTimestamp()).TotalSeconds < 30;
        }

        // [新增] 获取当前 IP (带 10秒 缓存)
        public string GetCurrentIP()
        {
            // [Fix #287] 如果网络发生了变更，强制重置所有网卡状态
            // 这会迫使 MatchNativeNetworkAdapter 重新寻找最新的 NetworkInterface 实例 (获取正确的新IP)
            if (_shouldResetAdapters)
            {
                try
                {
                    foreach (var state in _netStates.Values)
                    {
                        state.NativeAdapter = null;
                        state.HasMatchAttempt = false; // 允许立即重新匹配
                    }
                    _shouldResetAdapters = false;
                }
                catch 
                { 
                    // 忽略并发修改异常，保留标志位下次重试
                }
            }

            // 1. 缓存保护：只有当缓存了【有效IP】且距离上次检查不到 30 秒时，才直接返回
            // 关键修改：如果 _staticIPCache 是空的，忽略时间限制，强制重试
            // 时间窗用单调时钟判断（墙上时间回拨不会让缓存窗口永远不过期）
            if (!string.IsNullOrEmpty(_staticIPCache) && IsIPCheckFresh())
            {
                return _staticIPCache;
            }

            string foundIP = null;

            // 2. 策略A：直接从当前锁定的硬件中获取 (这是最直接的方式)
            try 
            {
                // ★★★ 核心修复：即使 _cachedNetHw 为空（还没锁定网卡），也要尝试从 _netStates 中寻找已匹配 NativeAdapter 的网卡 ★★★
                // 之前的 bug 是：网速跳动说明 MatchNativeNetworkAdapter 成功了，_netStates 里有 NativeAdapter，
                // 但 _cachedNetHw 还没来得及更新，导致这里直接跳过。
                
                // 如果已锁定，优先用锁定的
                if (_cachedNetHw != null && _netStates.TryGetValue(_cachedNetHw, out var state) && state.NativeAdapter != null)
                {
                     foundIP = GetIPv4FromAdapter(state.NativeAdapter);
                }
                
                // 如果没锁定（或锁定的没取到），遍历所有已知状态的网卡 (这些网卡都是有流量活动的)
                if (string.IsNullOrEmpty(foundIP))
                {
                    foreach (var kv in _netStates)
                    {
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
                    var nics = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var nic in nics)
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (IsVirtualNetwork(nic.Description) || IsVirtualNetwork(nic.Name)) continue; // 仅兜底物理网卡

                        foundIP = GetIPv4FromAdapter(nic);
                        if (!string.IsNullOrEmpty(foundIP)) break;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(foundIP))
            {
                _staticIPCache = foundIP;
                MarkIPChecked();
                return foundIP;
            }
            
            return _staticIPCache; 
        }

        private string GetIPv4FromAdapter(NetworkInterface nic)
        {
            try
            {
                var props = nic.GetIPProperties();
                string bestIP = null;

                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ipStr = ip.Address.ToString();
                        
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
                }
                return bestIP;
            }
            catch { }
            return null;
        }

        // ===========================================================
        // 获取最佳网络数值 (已简化：Preferred 逻辑已移至 ValueProvider 静态缓存)
        // ===========================================================
        public float? GetBestValue(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            // 2. 自动选优 (带缓存) - 原 GetBestNetworkValue
            // A. 尝试运行时缓存
            if (_cachedNetHw != null)
            {
                // ★★★ 【修复 1】存活检查：如果缓存的硬件对象已经不在当前的硬件列表中（已失效），强制丢弃 ★★★
                if (!computer.Hardware.Contains(_cachedNetHw))
                {
                    _cachedNetHw = null;
                }
                else
                {
                    float? cachedVal = ReadNetworkSensor(_cachedNetHw, key, lastValidMap, syncLock);
                    // 逻辑优化：如果有流量，直接用；如果没流量但距离上次全盘扫描 < 3秒，也直接用。
                    // （3 秒窗口用单调时钟判断：墙上时间回拨不会让窗口永远不过期）
                    if ((cachedVal.HasValue && cachedVal.Value > 0.1f) || IsNetScanFresh())
                    {
                        return cachedVal;
                    }
                }
            }

            // ★★★ [漏掉的部分] B. 尝试启动时缓存 (Settings 中的记录) ★★★
            if (_cachedNetHw == null && !string.IsNullOrEmpty(cfg.LastAutoNetwork))
            {
                // 尝试直接找上次记住的网卡
                var savedHw = computer.Hardware.FirstOrDefault(h => h.Name == cfg.LastAutoNetwork);
                if (savedHw != null)
                {
                    // 找到了！直接设为缓存，跳过全盘扫描
                    _cachedNetHw = savedHw;
                    MarkNetScanned();
                    return ReadNetworkSensor(savedHw, key, lastValidMap, syncLock);
                }
            }

            // C. 全盘扫描
            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
            {
                double penalty = IsVirtualNetwork(hw.Name) ? -1e9 : 0;
                ISensor? up = null, down = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => SensorMap.Has(s.Name, k))) up ??= s;
                    if (_downKW.Any(k => SensorMap.Has(s.Name, k))) down ??= s;
                }
                if (up == null && down == null) continue;
                double score = (up?.Value ?? 0) + (down?.Value ?? 0) + penalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = (key == "NET.Up") ? up : down;
                }
            }

            // D. 更新缓存
            if (bestHw != null)
            {
                _cachedNetHw = bestHw;
                MarkNetScanned();
                
                // ★★★ [漏掉的部分] 记住这次的选择 ★★★
                if (cfg.LastAutoNetwork != bestHw.Name)
                {
                    cfg.LastAutoNetwork = bestHw.Name;
                }
            }

            // E. 返回
            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        private float? ReadNetworkSensor(IHardware hw, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            ISensor? target = null;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "NET.Up" && _upKW.Any(k => SensorMap.Has(s.Name, k))) { target = s; break; } 
                if (key == "NET.Down" && _downKW.Any(k => SensorMap.Has(s.Name, k))) { target = s; break; }
            }

            if (target?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
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
                try
                {
                    var stats = state.NativeAdapter.GetIPStatistics();
                    nativeUp = stats.BytesSent;
                    nativeDown = stats.BytesReceived;
                }
                catch { state.NativeAdapter = null; }
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
                // 匹配错误：丢弃当前适配器，下次重新匹配
                state.NativeAdapter = null;
            }

            // D. 存入数据
            if (result.UpBytes > 0 || result.DownBytes > 0)
            {
                cfg.SessionUploadBytes += result.UpBytes;
                cfg.SessionDownloadBytes += result.DownBytes;
                TrafficLogger.AddTraffic(result.UpBytes, result.DownBytes);
            }
        }

        private void MatchNativeNetworkAdapter(string lhmName, NetworkState state)
        {
            if (state.NativeAdapter != null) return;

            // 重新匹配的最短间隔（10 秒），用单调时钟判断
            long now = _time.GetTimestamp();
            if (state.HasMatchAttempt &&
                _time.GetElapsedTime(state.LastMatchAttempt, now).TotalSeconds < 10) return;
            state.LastMatchAttempt = now;
            state.HasMatchAttempt = true;

            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                
                // 预先分配令牌列表容量
                var lhmTokens = new List<string>(capacity: 10);
                SplitTokens(lhmName, lhmTokens);

                foreach (var nic in nics)
                {
                    // 1. 匹配连接名称
                    if (nic.Name.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); return; }
                    // 2. 匹配硬件描述
                    if (nic.Description.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); return; }
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
                            return;
                        }
                    }
                }
            }
            catch { state.NativeAdapter = null; }
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

        private void SetNativeAdapter(NetworkInterface nic, NetworkState state)
        {
            state.NativeAdapter = nic;
            try
            {
                var stats = nic.GetIPStatistics();
                // ★★★ [网卡切换] 重新建立原生字节计数基线：切换后的第一次采样不产生增量 ★★★
                state.Accumulator.ResetBaseline(stats.BytesSent, stats.BytesReceived);
            }
            catch { state.NativeAdapter = null; }
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