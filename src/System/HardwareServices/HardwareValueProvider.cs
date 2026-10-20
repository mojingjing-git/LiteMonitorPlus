using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 采集代次值缓存。
    ///
    /// 限频后，取值缓存的生命周期跟着**真实采集代次**走，而不是界面 Tick：
    ///  - 同一代次内，同一个 Key 只计算一次（UI / Web / 任务栏可以共享同一轮采样）；
    ///  - 合法的 0 会被缓存（0 是有效值，不能每次重算）；
    ///  - 本代次"不可用"的结果同样会被缓存，避免反复走昂贵的失败回退；
    ///  - 进入新代次时整体作废，下一次读取会重新计算。
    /// </summary>
    public sealed class GenerationValueCache
    {
        private readonly Dictionary<string, float?> _values = new Dictionary<string, float?>(StringComparer.Ordinal);
        private int _generation;

        /// <summary>当前采集代次。</summary>
        public int Generation => _generation;

        /// <summary>本代次已缓存的 Key 数量。</summary>
        public int Count => _values.Count;

        /// <summary>推进到新的采集代次（旧代次的缓存整体作废）。</summary>
        public void BeginGeneration(int generation)
        {
            _generation = generation;
            _values.Clear();
        }

        /// <summary>清空缓存（映射重建 / 重载等失效路径）。</summary>
        public void Clear() => _values.Clear();

        /// <summary>
        /// 读取本代次的缓存值。
        /// 返回 true 表示本代次已经算过这个 Key（value 为 null 表示"本代次不可用"）。
        /// </summary>
        public bool TryGetValue(string key, out float? value) => _values.TryGetValue(key, out value);

        /// <summary>写入本代次的值（value 为 null 表示"本代次不可用"）。</summary>
        public void SetValue(string key, float? value) => _values[key] = value;
    }

    public class HardwareValueProvider : IDisposable
    {
        private readonly Computer _computer;
        private readonly Settings _cfg;
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly FpsCounter? _fpsCounter;
        private readonly object _lock;
        private readonly Dictionary<string, float> _lastValidMap; 
        
        // 子服务与处理器
        private readonly PerformanceCounterManager _perfManager;
        private readonly ComponentProcessor _componentProcessor;

        // ★ 采集代次缓存：同一代次内同一个 Key 只计算一次
        private readonly GenerationValueCache _cache = new GenerationValueCache();

        // ★ 启动期缓存：硬件尚未就绪时的性能计数器兜底。
        //   UI 在初始化期间会高频读取，这里用 TTL 限流（默认 1 秒），
        //   避免绕过采集门限反复调用 PerformanceCounter.NextValue()。
        private struct StartupEntry
        {
            public float? Value;
            public long Timestamp;
        }
        private readonly Dictionary<string, StartupEntry> _startupCache = new Dictionary<string, StartupEntry>(StringComparer.Ordinal);
        private readonly object _startupLock = new object();
        private readonly TimeProvider _time;
        private readonly HardwareSamplingStatistics? _statistics;

        // ★ 设备切换后作废的"最后有效值"。
        //   只在这里做标记与查询，**不**直接改共享的 _lastValidMap（非线程安全）。
        //   _staleValuePrefixes：作废的 Key 前缀（换卡后是 "GPU"）。
        //   _refreshedValueKeys：切换之后已经重新算过有效值的 Key（这些 Key 的兜底恢复可用）。
        private readonly HashSet<string> _staleValuePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _refreshedValueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _staleLock = new object();

        // ★ 设备选择变更通知（接线见 A2b：协调器 Invalidate()）
        private readonly Action? _onDeviceSelectionChanged;

        // 对象级缓存：(Sensor对象)
        private Dictionary<string, ISensor> _manualSensorCache = new();

        private const float AutoMoboTempHardMax = 95f;
        private const float ManualMoboTempHardMax = 125f;

        /// <summary>启动期缓存的存活时间（毫秒）。与普通硬件的最小采集周期一致。</summary>
        private const int StartupCacheTtlMs = 1000;

        // 配置版本追踪，用于自动触发预热
        private string _lastPrefCpuFan = "";
        private string _lastPrefCpuPump = "";
        private string _lastPrefCaseFan = "";
        private string _lastPrefMoboTemp = "";
        private string _lastPrefDisk = "";
        private string _lastPrefNet = "";
        private string _lastPrefGpu = "";
        private int _lastGpuCount = 1;
        
        public HardwareValueProvider(Computer c, Settings s, SensorMap map, NetworkManager net, DiskManager disk, FpsCounter? fpsCounter, PerformanceCounterManager perfManager, object syncLock, Dictionary<string, float> lastValid, HardwareSamplingStatistics? statistics = null, TimeProvider? timeProvider = null, Action? onDeviceSelectionChanged = null)
        {
            _computer = c;
            _cfg = s;
            _sensorMap = map;
            _networkManager = net;
            _diskManager = disk;
            _fpsCounter = fpsCounter;
            _perfManager = perfManager;
            _lock = syncLock;
            _lastValidMap = lastValid;
            _statistics = statistics;
            _time = timeProvider ?? TimeProvider.System;
            _onDeviceSelectionChanged = onDeviceSelectionChanged;

            // 初始化子服务
            _componentProcessor = new ComponentProcessor(c, s, map);
        }

        // ★★★ [新增] 清空缓存并重新预热（当硬件重载或配置变更时调用） ★★★
        public void PreCacheAllSensors(SensorMap map)
        {
            var newCache = map.GetInternalMap();

            // 记录当前预热的配置版本
            _lastPrefCpuFan = _cfg.PreferredCpuFan;
            _lastPrefCpuPump = _cfg.PreferredCpuPump;
            _lastPrefCaseFan = _cfg.PreferredCaseFan;
            _lastPrefMoboTemp = _cfg.PreferredMoboTemp;
            _lastPrefDisk = _cfg.PreferredDisk;
            _lastPrefNet = _cfg.PreferredNetwork;
            _lastPrefGpu = _cfg.PreferredGpu ?? "";
            _lastGpuCount = _cfg.MonitoredGpuCount;

            // 1. 预查找用户指定的首选传感器 (风扇、水泵、主板温度)
            string[] preferredKeys = { "CPU.Fan", "CPU.Pump", "CASE.Fan", "MOBO.Temp" };
            foreach (var key in preferredKeys)
            {
                string pref = (key == "CPU.Fan") ? _lastPrefCpuFan :
                             (key == "CPU.Pump") ? _lastPrefCpuPump :
                             (key == "CASE.Fan") ? _lastPrefCaseFan : _lastPrefMoboTemp;

                SensorType type = (key == "MOBO.Temp") ? SensorType.Temperature : SensorType.Fan;

                if (!string.IsNullOrEmpty(pref) && !pref.Contains("自动") && !pref.Contains("Auto"))
                {
                    var s = FindSensorReverse(pref, type);
                    if (s != null) newCache[key] = s;
                }
            }

            // 2. 预查找首选磁盘 (DISK.*)
            if (!string.IsNullOrEmpty(_lastPrefDisk))
            {
                var disk = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Storage && h.Name.Equals(_lastPrefDisk, StringComparison.OrdinalIgnoreCase));
                if (disk != null)
                {
                    foreach (var s in disk.Sensors)
                    {
                        if (s.SensorType == SensorType.Throughput)
                        {
                            if (SensorMap.Has(s.Name, "read")) newCache["DISK.Read"] = s;
                            else if (SensorMap.Has(s.Name, "write")) newCache["DISK.Write"] = s;
                        }
                    }
                    
                    // 独立处理温度筛选逻辑 (复用 DiskManager 算法)
                    var bestTemp = DiskManager.FindBestTempSensor(disk);
                    if (bestTemp != null) newCache["DISK.Temp"] = bestTemp;
                }
            }

            // 3. 预查找首选网络 (NET.*)
            if (!string.IsNullOrEmpty(_lastPrefNet))
            {
                var net = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Network && h.Name.Equals(_lastPrefNet, StringComparison.OrdinalIgnoreCase));
                if (net != null)
                {
                    foreach (var s in net.Sensors)
                    {
                        if (s.SensorType == SensorType.Throughput)
                        {
                            if (SensorMap.Has(s.Name, "upload") || SensorMap.Has(s.Name, "sent") || SensorMap.Has(s.Name, "up")) newCache["NET.Up"] = s;
                            else if (SensorMap.Has(s.Name, "download") || SensorMap.Has(s.Name, "received") || SensorMap.Has(s.Name, "down")) newCache["NET.Down"] = s;
                        }
                    }
                }
            }

            // 4. 原子更新缓存
            lock (_lock)
            {
                _manualSensorCache = newCache;
                _cache.Clear();
                _componentProcessor.ClearCache();
            }
            ClearStartupCache();
        }

        /// <summary>
        /// 极速反向查找：解析保存的字符串（格式：SensorName [HardwareName]）并定位到 ISensor 对象
        /// </summary>
        private ISensor? FindSensorReverse(string savedString, SensorType type)
        {
            if (string.IsNullOrEmpty(savedString) || savedString.Contains("Auto") || savedString.Contains("自动")) 
                return null;

            int idx = savedString.LastIndexOf('[');
            if (idx < 0) return null; 

            string targetSensorName = savedString.Substring(0, idx).Trim();
            string targetHardwareName = savedString.Substring(idx + 1).TrimEnd(']');

            ISensor? SearchBranch(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType == type && s.Name == targetSensorName)
                        return s; 
                }
                foreach (var sub in h.SubHardware)
                {
                    var s = SearchBranch(sub);
                    if (s != null) return s;
                }
                return null;
            }

            foreach (var hw in _computer.Hardware)
            {
                if (hw.Name == targetHardwareName)
                {
                    return SearchBranch(hw);
                }
            }
            return null;
        }

        public void ClearCache()
        {
            lock (_lock)
            {
                _manualSensorCache.Clear();
                _cache.Clear();
                _componentProcessor.ClearCache();
            }
            ClearStartupCache();
        }

        /// <summary>
        /// 一轮**真实采集**开始时的准备工作。
        ///
        /// ★ 触发时机从"每个界面 Tick"改成"每个采集代次"：
        ///   限频后如果还按界面 Tick 清缓存，UI/Web 的高频读取仍会反复执行
        ///   PerformanceCounter.NextValue()，限频就白做了。
        ///
        /// 这里仍然负责"设备偏好变更 -> 重建映射 + 预热"的失效逻辑，
        /// 只是它现在每个采集代次才检查一次（用户修改会在下一次允许采样内生效）。
        /// </summary>
        /// <param name="generation">新的采集代次编号。</param>
        public void OnCollectionStarted(int generation)
        {
            lock (_lock)
            {
                // 旧代次的取值全部作废（含"不可用"的记录），新一代次重新计算
                _cache.BeginGeneration(generation);

                bool gpuChanged = _lastPrefGpu != (_cfg.PreferredGpu ?? "");
                bool gpuCountChanged = _lastGpuCount != _cfg.MonitoredGpuCount;
                if (gpuChanged || gpuCountChanged)
                {
                    // ★★★ [多显卡] 显卡数量变了, 重建映射 ★★★
                    _sensorMap.Rebuild(_computer, _cfg);

                    // ★★★ [显存修复] 被监控卡集合变了, 之前记的显存容量作废 ★★★
                    // 旧格式 Key "GPU" 指向的卡可能已经换人 (首选显卡改了 / 数量改了),
                    // 留着旧容量会让面板出现"百分比按 20GB 算、容量却显示 12GB"的自相矛盾。
                    Settings.DetectedGpuVramTotalGB = 0;
                    Settings.DetectedGpuVramTotalGBByKey.Clear();

                    // ★★★ [多显卡] 换卡后不能串用上一张卡的最后有效值 ★★★
                    // "GPU.0.Temp" 之类的 Key 在换卡后指向的是另一块卡，
                    // 留着旧值会让界面显示另一张卡的温度。
                    // 这里只"标记"这些 Key 作废，不去动共享的 _lastValidMap：
                    // 那个字典是非线程安全的，而 GetValue 抢锁失败时会无锁读它，
                    // 采集线程直接改它会引入未定义行为。
                    // 真正的删除推迟到"有人真的要读这个 Key"的时候，在持锁状态下做。
                    MarkStaleGpuValues();

                    // 设备选择变更 -> 允许立即重新采一次（接线见 A2b：协调器 Invalidate()）
                    try { _onDeviceSelectionChanged?.Invoke(); }
                    catch { /* 失效通知失败不能影响采集 */ }
                }

                // 自动检测配置变更：如果用户更改了首选风扇/磁盘/显卡，立即自动预热
                if (gpuChanged || gpuCountChanged ||
                    _lastPrefCpuFan != _cfg.PreferredCpuFan ||
                    _lastPrefCpuPump != _cfg.PreferredCpuPump ||
                    _lastPrefCaseFan != _cfg.PreferredCaseFan ||
                    _lastPrefMoboTemp != _cfg.PreferredMoboTemp ||
                    _lastPrefDisk != _cfg.PreferredDisk ||
                    _lastPrefNet != _cfg.PreferredNetwork)
                {
                    PreCacheAllSensors(_sensorMap);
                }
            }
        }

        /// <summary>
        /// 一轮**真实采集**落定后的收尾。
        ///
        /// 采集期间（hw.Update() 还没做完）如果有别的线程读了值，
        /// 那些"更新前"的旧值会被写进本代次缓存并钉住一整个周期。
        /// 这里在采集结束后把缓存再作废一次，让下一次读取用更新后的传感器重新算。
        ///
        /// ★ 接线说明：协调器需要在采样动作**之后**调用它（onCollectionSettled 回调）。
        ///   当前主工程的接线由 A2b 补上（见报告的"A2b 接线需求清单"）。
        /// </summary>
        /// <param name="generation">当前采集代次编号。</param>
        public void OnCollectionSettled(int generation)
        {
            lock (_lock)
            {
                _cache.BeginGeneration(generation);
            }
        }

        /// <summary>
        /// 标记显卡相关的"最后有效值"作废（换卡后不能串用上一张卡的数据）。
        ///
        /// 只标记，不改共享字典：_lastValidMap 是非线程安全的共享 Dictionary，
        /// 而 GetValue 抢锁失败时会无锁读它；采集线程直接改它属于未定义行为。
        /// 被标记的 Key 在真正被读取时（持锁）才从字典里删掉。
        /// </summary>
        private void MarkStaleGpuValues()
        {
            lock (_staleLock)
            {
                _staleValuePrefixes.Add("GPU");
                // 换卡后，之前"已经算过"的那些 Key 不再算作已刷新
                _refreshedValueKeys.Clear();
            }
        }

        /// <summary>该 Key 的"最后有效值"是否已作废（设备切换后）。</summary>
        private bool IsStaleValue(string key)
        {
            lock (_staleLock)
            {
                if (_staleValuePrefixes.Count == 0) return false;
                if (_refreshedValueKeys.Contains(key)) return false;
                foreach (string prefix in _staleValuePrefixes)
                {
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
        }

        /// <summary>该 Key 重新算出了有效值 -> 不再是陈旧值。</summary>
        private void ClearStaleValue(string key)
        {
            lock (_staleLock)
            {
                if (_staleValuePrefixes.Count > 0) _refreshedValueKeys.Add(key);
            }
        }

        /// <summary>
        /// 诊断：某个 Key 的"最后有效值"当前是否处于作废状态
        /// （设备切换后，旧设备的数值不允许再拿来兜底）。
        /// </summary>
        public bool IsLastValidValueStale(string key) => IsStaleValue(key);

        /// <summary>清空启动期缓存。</summary>
        private void ClearStartupCache()
        {
            lock (_startupLock)
            {
                _startupCache.Clear();
            }
        }


        // ===========================================================
        // ===================== 公共取值入口 =========================
        // ===========================================================
        public float? GetStartupValue(string key)
        {
            // ★ 启动期也要限流：硬件还没就绪时 UI 会以界面频率反复读取，
            //   这里按 TTL 缓存，避免绕过采集门限反复调用 PerformanceCounter.NextValue()。
            long now = _time.GetTimestamp();
            long ttlTicks = _time.TimestampFrequency * StartupCacheTtlMs / 1000;
            lock (_startupLock)
            {
                if (_startupCache.TryGetValue(key, out StartupEntry cached) && (now - cached.Timestamp) < ttlTicks)
                {
                    return cached.Value;
                }
            }

            float? result = ComputeStartupValue(key);

            lock (_startupLock)
            {
                _startupCache[key] = new StartupEntry { Value = result, Timestamp = now };
            }
            return result;
        }

        private float? ComputeStartupValue(string key)
        {
            // 启动期的取值也要计数：用来证明"早期取值没有绕过采集门限"
            _statistics?.RecordValueComputation();

            if (_lastValidMap.TryGetValue(key, out float lastVal)) return lastVal;

            bool useCounter = _cfg.UseWinPerCounters && _perfManager.IsInitialized;
            if (!useCounter) return null;

            if (key == "CPU.Load")
            {
                var cpuLoad = _perfManager.GetCpuLoad();
                return cpuLoad.HasValue ? Math.Clamp(cpuLoad.Value, 0f, 100f) : null;
            }

            if (key == "CPU.Clock")
            {
                return _perfManager.GetCpuFreq();
            }

            if (key == "MEM.Load")
            {
                var memData = _perfManager.GetMemoryData();
                if (memData.Load.HasValue && Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                {
                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                }
                return memData.Load;
            }

            if (key == "DISK.Read") return _perfManager.GetDiskRead();
            if (key == "DISK.Write") return _perfManager.GetDiskWrite();
            if (key == "DISK.Activity")
            {
                var diskActive = _perfManager.GetDiskActive();
                return diskActive.HasValue ? Math.Clamp(diskActive.Value, 0f, 100f) : null;
            }

            return null;
        }

        public float? GetValue(string key)
        {
            // ★★★ [优化] 使用 TryEnter 避免 UI 线程因后台重载而卡死 ★★★
            // 如果 10ms 内拿不到锁（说明正在重载硬件），直接返回 null
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_lock, 10, ref lockTaken);
                if (!lockTaken) 
                {
                    // ★★★ [Fix] 如果无法获取锁（硬件正在重载），优先返回上一帧的有效值，防止 UI 闪烁 ★★★
                    // 因为 _lastValidMap 只在 GetValue 内部写入（且都在锁内），
                    // 而后台线程（采集/重载）不再直接改它，
                    // 所以此时读取是相对安全的（虽然没有锁）。
                    // ★ 设备切换后被标记作废的 Key 不能在这里返回旧卡的值。
                    if (IsStaleValue(key)) return null;
                    if (_lastValidMap.TryGetValue(key, out float lastVal)) return lastVal;
                    return null;
                }

                // ★★★ [新增 3] 优先查缓存，如果本代次算过，直接返回 ★★★
                //   缓存里可能是 null：表示"本代次这个 Key 不可用"，
                //   直接返回可以避免同一代次反复走昂贵的失败回退路径。
                if (_cache.TryGetValue(key, out float? cachedVal)) return cachedVal;

                // 本代次第一次算这个 Key
                _statistics?.RecordValueComputation();

                // 定义临时结果变量
                float? result = null;
                bool skipGenericFallback = false;
                // FPS 有自己的更新机制（外部服务 + 自己的平滑/回收逻辑），
                // 不进普通硬件的采集代次缓存：否则会被冻结接近一个采集周期，
                // 也会让 FPS 的"空闲回收"判断失真。读取它也不会清空普通硬件缓存。
                bool skipCache = false;
                
                // ★★★ [核心逻辑] 全局开关判断：只有当开关开启，且管理器已初始化成功时，才尝试走计数器 ★★★
                // 这里的 UseWindowsPerformanceCounters 对应 Step 1 中 Settings 新增的属性
                bool useCounter = _cfg.UseWinPerCounters && _perfManager.IsInitialized;

                // ★★★ [终极优化] 使用 switch 替代 if-else 链，实现 O(1) 哈希跳转 ★★★
                switch (key)
                {
                    // 1. CPU.Load
                    case "CPU.Load":
                        if (useCounter)
                        {
                            result = _perfManager.GetCpuLoad();
                            if (result.HasValue && result.Value > 100f) result = 100f;
                        }

                        if (result == null)
                        {
                            // [Fix] 优先使用 LHM 的 "Total" 聚合传感器（与任务管理器一致）
                            // 然后才回退到各核心平均值计算
                            if (_manualSensorCache.TryGetValue("CPU.Load", out var totalSensor) && totalSensor.Value.HasValue)
                            {
                                result = totalSensor.Value.Value;
                            }

                            // 如果 Total 传感器不可用，才回退到核心平均值
                            if (result == null)
                            {
                                result = _componentProcessor.GetCpuLoad();
                            }

                            if (result == null) result = 0f;
                        }
                        break;

                    // 2. CPU.Temp
                    case "CPU.Temp":
                        result = _componentProcessor.GetCpuTemp();
                        if (result == null && _manualSensorCache.TryGetValue("CPU.Temp", out var fallbackT))
                        {
                            result = fallbackT.Value;
                        }
                        if (result == null) result = 0f;
                        break;

                    // 4. 每日流量
                    case "DATA.DayUp":
                        result = TrafficLogger.GetTodayStats().up;
                        break;
                    case "DATA.DayDown":
                        result = TrafficLogger.GetTodayStats().down;
                        break;

                    // 6. 内存
                    case "MEM.Load":
                        if (useCounter)
                        {
                            var memData = _perfManager.GetMemoryData();
                            if (memData.Load.HasValue)
                            {
                                result = memData.Load.Value;
                                if (Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                                {
                                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                                }
                            }
                        }

                        if (result == null)
                        {
                            // 1. 尝试获取基础数据 (Used, Available)
                            // 这是最可靠的数据源，既能算出负载，也能初始化总容量
                            float? usedVal = null;
                            float? availVal = null;
                            if (_manualSensorCache.TryGetValue("MEM.Used", out var u) && u.Value.HasValue) usedVal = u.Value.Value;
                            if (_manualSensorCache.TryGetValue("MEM.Available", out var a) && a.Value.HasValue) availVal = a.Value.Value;

                            // 2. 优先通过计算获取结果 (Used / (Used + Available))
                            // 这种方式能同时确保 Load 百分比和 Total 容量都被正确处理，避免"有负载没容量"的问题
                            if (usedVal.HasValue && availVal.HasValue)
                            {
                                float memTotal = usedVal.Value + availVal.Value;
                                if (memTotal > 0)
                                {
                                    // 初始化总容量 (供容量显示模式使用)
                                    if (Settings.DetectedRamTotalGB <= 0)
                                    {
                                        Settings.DetectedRamTotalGB = memTotal > 512.0f ? memTotal / 1024.0f : memTotal;
                                    }

                                    // 计算负载百分比
                                    result = (usedVal.Value / memTotal) * 100.0f;
                                }
                            }

                            // 3. 兜底：如果无法计算 (缺 Used/Available)，才尝试读取原生 Load 传感器
                            if (result == null && _manualSensorCache.TryGetValue("MEM.Load", out var sLoad) && sLoad.Value.HasValue)
                            {
                                result = sLoad.Value.Value;
                            }
                        }
                        else
                        {
                            break; 
                        }
                        break;

                    // 7. 显存
                    case "GPU.VRAM":
                        result = GetGpuVramPercent(key);
                        break;

                    // 8. 风扇/泵 (带 Max 记录)
                    case "CPU.Fan":
                    case "CPU.Pump":
                    case "CASE.Fan":
                    case "GPU.Fan":
                        result = ReadFanValue(key);
                        break;

                    // 主板温度
                    case "MOBO.Temp":
                        skipGenericFallback = true;
                        if (_manualSensorCache.TryGetValue(key, out var sMobo))
                        {
                            result = ReadMoboTemperature(sMobo);
                        }
                        break;

                    case "FPS":
                        result = _fpsCounter?.GetFps();
                        skipCache = true;
                        break;

                    // 电池
                    case "BAT.Percent":
                    case "BAT.Power":
                    case "BAT.Voltage":
                    case "BAT.Current":
                        result = BatteryService.GetBatteryValue(key, _manualSensorCache);
                        break;

                    // 默认分支：处理模糊匹配 (StartsWith/Contains)
                    default:
                        if (key.StartsWith("NET"))
                        {
                            if (_manualSensorCache.TryGetValue(key, out var sNet))
                            {
                                result = sNet.Value;
                            }
                            
                            if (result == null)
                            {
                                result = _networkManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                            }
                        }
                        else if (key.StartsWith("DISK"))
                        {
                            bool isSpecificDisk = !string.IsNullOrEmpty(_cfg.PreferredDisk);
                            if (useCounter && !isSpecificDisk)
                            {
                                if (key == "DISK.Read") result = _perfManager.GetDiskRead();
                                else if (key == "DISK.Write") result = _perfManager.GetDiskWrite();
                                else if (key == "DISK.Activity") 
                                {
                                    result = _perfManager.GetDiskActive();
                                    if (result.HasValue) result = Math.Clamp(result.Value, 0f, 100f);
                                }
                            }

                            if (result == null && _manualSensorCache.TryGetValue(key, out var sDisk))
                            {
                                result = sDisk.Value;
                            }

                            if (result == null)
                            {
                                result = _diskManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                            }
                        }
                        else if (key.EndsWith(".Fan", StringComparison.OrdinalIgnoreCase) ||
                                 key.EndsWith(".Pump", StringComparison.OrdinalIgnoreCase))
                        {
                            // ★★★ [多显卡] 带序号的 "GPU.0.Fan" 也要记录每卡峰值 ★★★
                            // 精确 case "GPU.Fan" 接不住它, 落到这里统一处理。
                            result = ReadFanValue(key);
                        }
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                            {
                                result = _perfManager.GetCpuFreq();
                            }
                            
                            if (result == null)
                            {
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                            }
                        }
                        else if (GpuKey.TryParse(key, out _, out string gpuMetric)
                                 && gpuMetric.Equals("VRAM", StringComparison.OrdinalIgnoreCase))
                        {
                            // ★★★ [多显卡] "GPU.1.VRAM" 等带序号的显存项 ★★★
                            result = GetGpuVramPercent(key);
                        }
                        break;
                }

                // 10. 通用传感器查找 (兜底)
                // ★★★ [优化] 移除锁和 _sensorMap 查找，直接查静态缓存 ★★★
                if (!skipGenericFallback && result == null && _manualSensorCache.TryGetValue(key, out var sGen))
                {
                    var val = sGen.Value;
                    if (val.HasValue && !float.IsNaN(val.Value)) 
                    { 
                        _lastValidMap[key] = val.Value; 
                        // 重新算出了有效值 -> 不再是设备切换前的陈旧值
                        ClearStaleValue(key);
                        result = val.Value; 
                    }
                    else if (IsStaleValue(key))
                    {
                        // 设备切换后：这个 Key 的旧值属于上一张卡，不能拿来兜底。
                        // 顺手把它从共享字典里删掉（此时持有 _lock，与其它写入同一把锁）。
                        _lastValidMap.Remove(key);
                    }
                    else if (_lastValidMap.TryGetValue(key, out var last))
                    {
                        result = last;
                    }
                }

                // ★★★ [新增 4] 写入缓存并返回 ★★★
                //   合法的 0 必须被缓存；null（不可用）同样缓存，
                //   这样同一代次里多个消费者不会反复触发昂贵的失败回退。
                if (!skipCache)
                {
                    _cache.SetValue(key, result);
                }

                return result;
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_lock);
            }
        }

        /// <summary>
        /// 计算显存占用百分比。
        /// key 形如 "GPU.VRAM" (旧) 或 "GPU.1.VRAM" (多卡), 由此派生出 Used / Total 子 Key。
        ///
        /// ★★★ [显存修复] 双通道择源 ★★★
        /// 厂商通道 (NVAPI/ADL) 在 LHM 0.9.6 的 NVIDIA 实现上会被"全局化", 多卡时多张卡拿到同一份数据;
        /// D3D 通道按适配器统计, 每卡独立。
        /// 判定交给 GpuVramSource: 厂商通道报的容量命中了"另一块适配器"的容量 ⇒ 它在报告别的卡
        /// ⇒ 换 D3D 通道。没有证据 ⇒ 维持厂商通道 (单卡 / AMD / Intel 场景行为完全不变)。
        /// </summary>
        private float? GetGpuVramPercent(string key)
        {
            // "GPU.VRAM" -> "GPU." ; "GPU.1.VRAM" -> "GPU.1."
            string prefix = key.Substring(0, key.Length - "VRAM".Length);
            string cardKey = prefix.TrimEnd('.');        // "GPU" / "GPU.1"

            // --- 1. 厂商通道 (NVAPI / ADL) ---
            float? usedVendor  = ToMB(GetValue(prefix + "VRAM.Used"));
            float? totalVendor = ToMB(GetValue(prefix + "VRAM.Total"));

            // --- 2. D3D 通道 (按适配器统计, 每卡独立) ---
            float? usedD3D  = ToMB(GetValue(prefix + "VRAM.D3D.Used"));
            float? totalD3D = ToMB(GetValue(prefix + "VRAM.D3D.Total"));

            // --- 3. 该卡真实容量 (注册表) + 全部适配器容量 (串卡交叉证据) ---
            string fullName = GetGpuFullName(cardKey);
            float? totalReal = GpuVramCapacity.GetTotalMB(fullName);
            IReadOnlyList<float> allTotals = GpuVramCapacity.GetAllTotalsMB();

            // --- 4. 守卫: 这张卡有没有资格参与"串卡"判定 ---
            // 核显一律不参与: 注册表里核显记的是 UMA 窗口大小, 与它实际用的共享显存上限
            // 本来就不该相等, 拿它当基准会把核显误判成串卡, 再切到几乎没有的 D3D 专用显存口径,
            // 结果核显显存恒显 0% —— 比原 bug 更糟。
            bool eligible = HardwareRules.UsesDedicatedVramName(fullName)
                            && !IsSharedMemoryCard(prefix);

            // --- 5. 择源 ---
            bool useD3D = eligible && GpuVramSource.IsVendorChannelForeign(totalVendor, totalReal, allTotals);
            var (used, total) = GpuVramSource.Select(
                usedVendor, totalVendor, usedD3D, totalD3D, totalReal, useD3D);

            if (used.HasValue && total.HasValue && total > 0)
            {
                // total 的单位是 MB
                float gb = total.Value / 1024f;

                // 旧单卡缓存 (保留原有语义)
                if (Settings.DetectedGpuVramTotalGB <= 0 && gb > 0 && gb < 1024)
                {
                    Settings.DetectedGpuVramTotalGB = gb;
                }

                // ★★★ [多显卡] 每张卡单独记录自己的显存总容量 ★★★
                // 写的是"校正后"的容量 (串卡时用注册表真值), 容量模式显示才会对。
                // 不能先到先得: 择源结果会随"被监控卡集合"变化, 值变了必须覆盖。
                if (gb > 0 && gb < 1024)
                {
                    if (!Settings.DetectedGpuVramTotalGBByKey.TryGetValue(cardKey, out float oldGb)
                        || Math.Abs(oldGb - gb) > 0.01f)
                    {
                        Settings.DetectedGpuVramTotalGBByKey[cardKey] = gb;
                    }
                }

                return used / total * 100f;
            }

            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Load", out var s) && s.Value.HasValue) return s.Value;
            return null;
        }

        /// <summary>
        /// 统一把传感器的显存数值换算成 MB。
        /// LHM 0.9.x 报 MB; 换新版 LHM 报字节。10485760 MB (= 10 TB) 实际不可能出现,
        /// 命中即代表单位是字节。
        /// </summary>
        private static float? ToMB(float? value)
        {
            if (!value.HasValue) return null;
            return value.Value > 10485760f ? value.Value / 1048576f : value.Value;
        }

        /// <summary>
        /// 该卡的显存是否走"共享显存"口径 (核显)。
        /// 判据: 它映射到的 LHM 传感器名里带 "Shared" (核显是 "D3D Shared Memory *")。
        /// </summary>
        private bool IsSharedMemoryCard(string prefix)
        {
            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Used", out var usedSensor)
                && usedSensor?.Name != null
                && usedSensor.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_manualSensorCache.TryGetValue(prefix + "VRAM.Total", out var totalSensor)
                && totalSensor?.Name != null
                && totalSensor.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 取 "GPU" / "GPU.1" 对应显卡的全名, 供注册表按名字匹配真实显存容量。
        /// </summary>
        private static string GetGpuFullName(string cardKey)
        {
            int index = -1;
            if (cardKey == GpuKey.Prefix.TrimEnd('.')) index = 0;      // "GPU" -> 0 号卡
            else if (cardKey.StartsWith(GpuKey.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = cardKey.Substring(GpuKey.Prefix.Length);
                if (int.TryParse(rest, out int i) && i >= 0) index = i;
            }
            return index >= 0 ? GpuRegistry.GetFullName(index) : "";
        }

        /// <summary>
        /// 读取风扇/泵读数并记录峰值。
        /// 同时支持旧格式 ("CPU.Fan" / "GPU.Fan") 与多卡格式 ("GPU.0.Fan") ——
        /// 带序号的 Key 必须走这里, 否则不会触发 UpdateMaxRecord, 每卡峰值永远记不上。
        /// </summary>
        private float? ReadFanValue(string key)
        {
            float? result = null;
            if (_manualSensorCache.TryGetValue(key, out var sFan))
            {
                result = sFan.Value;
            }
            if (result.HasValue && result.Value < 10000f)
                _cfg.UpdateMaxRecord(key, result.Value);
            return result;
        }
        private float? ReadMoboTemperature(ISensor sensor)
        {
            bool manualSensor = IsManualMoboTemperatureSelected();
            var raw = sensor.Value;
            if (!raw.HasValue || float.IsNaN(raw.Value) || float.IsInfinity(raw.Value) || raw.Value <= 0f)
            {
                return GetLastValidMoboTemperature(manualSensor);
            }

            float value = raw.Value;
            bool overHardMax = manualSensor ? value > ManualMoboTempHardMax : value >= AutoMoboTempHardMax;
            if (overHardMax)
            {
                return GetLastValidMoboTemperature(manualSensor);
            }

            _lastValidMap["MOBO.Temp"] = value;
            return value;
        }

        private bool IsManualMoboTemperatureSelected()
        {
            string pref = _cfg.PreferredMoboTemp ?? "";
            return !string.IsNullOrWhiteSpace(pref) &&
                   !pref.Contains("自动", StringComparison.OrdinalIgnoreCase) &&
                   !pref.Contains("Auto", StringComparison.OrdinalIgnoreCase);
        }

        private float? GetLastValidMoboTemperature(bool manualSensor)
        {
            float max = manualSensor ? ManualMoboTempHardMax : AutoMoboTempHardMax;
            return _lastValidMap.TryGetValue("MOBO.Temp", out float last) &&
                   last > 0f &&
                   last <= max
                ? last
                : null;
        }

        public void Dispose()
        {
        }
    }
}
