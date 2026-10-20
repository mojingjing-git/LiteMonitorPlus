using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // ★★★ 新增：引用用于内存修剪的库
using System.Reflection; // ★★★ 新增：用于反射关闭历史记录
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using System.Linq;
using System.Threading;

namespace LiteMonitor.src.SystemServices
{
    public sealed class HardwareMonitor : IDisposable
    {
        /// <summary>界面刷新间隔的默认值（毫秒）。RefreshMs 非法时回退到它。</summary>
        public const int DefaultRefreshMs = 1000;

        #region Singleton & Events
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;

        /// <summary>
        /// 最近一次硬件层初始化/重载失败的原因（null 表示一切正常）。
        /// Release 构建里 Debug.WriteLine 会被整段编译掉，如果只靠它记录异常，
        /// 「所有 LHM 指标恒 0」这类故障对用户和排查者都是完全不可见的，
        /// 所以这里保留一个可查询的出口，UI 侧据此提示用户。
        /// </summary>
        public static string? LastHardwareError { get; private set; }
        #endregion

        #region Private Fields
        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly object _lock = new object();

        // 拆分出的子服务
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly FpsCounter _fpsCounter;
        private readonly DriverInstaller _driverInstaller;
        private readonly HardwareValueProvider _valueProvider;

        // 性能计数器管理器
        private readonly PerformanceCounterManager _perfCounterManager;

        private readonly Dictionary<string, float> _lastValidMap = new();
        
        // 状态标记 (防止并发重载)
        private volatile bool _isReloading = false;
        private volatile bool _isOpening = false;

        // ★★★ [限频] 采集就绪状态 ★★★
        // _isOpening == false 只说明"没有正在打开"，不代表初始化已经成功完成。
        // 构造后到异步初始化结束之间存在一个短窗口，这期间不允许发生真实采集。
        private volatile bool _isReady = false;
        private volatile bool _disposed = false;

        // 采样协调器 (唯一真实采集入口) + 低开销统计计数器
        private readonly HardwareSamplingStatistics _samplingStatistics;
        private readonly HardwareSamplingCoordinator _coordinator;

        // ★ 设备 Update 计数入口：网卡 / 磁盘由子管理器计数，采集层不重复记。
        private readonly HardwareDeviceUpdateCounter _deviceCounter;

        // ★ 单调截止时间调度 (慢扫描 / 磁盘后台扫描)。
        private readonly HardwareSamplingSchedule _schedule;

        // ★ 维护任务调度器：**本实例自己持有**（计划 §4.3「周期状态属于正确的服务实例」）。
        //   不用进程级静态实例，避免多实例互相污染。
        private readonly MaintenanceScheduler _maintenance;

        // Web 有效数据需求 (由 Web 层在存在合法需求时调用 SetWebDataDemand 写入)。
        private volatile bool _webDataDemand = false;

        // 计时器相关
        private long _tickCounter = 0;
        private double _secondAccumulator = 0;
        private long _secondsCounter = 0;

        // ★ 单调时钟：采集间隔不再用墙上时间（系统时间回拨不会产生负 delta）。
        private readonly TimeProvider _time = TimeProvider.System;
        private long _lastTrafficTimestamp;
        private bool _hasTrafficTimestamp;
        #endregion

        #region Public Properties
        // [新增] 允许 UI 层访问原始硬件树（用于硬件信息面板）
        public IComputer ComputerInstance => _computer;
        public object SyncLock => _lock;

        /// <summary>
        /// 采集统计快照入口（低开销计数器）。
        /// 探针 / 后续阶段用它统计真实采集轮数、每设备 Update 次数、慢扫描与维护触发次数。
        /// </summary>
        public HardwareSamplingStatistics SamplingStatistics => _samplingStatistics;

        /// <summary>当前采集代次编号（每完成一次真实采集 +1）。</summary>
        public int SamplingGeneration => _coordinator.Generation;

        /// <summary>硬件层是否已完成初始化（可以真实采集）。</summary>
        public bool IsReady => _isReady;

        /// <summary>
        /// 当前是否存在**可用的 FPS 数据**。
        ///
        /// 语义（供 Web 层区分"真 0 FPS"和"未启用 / 首帧未到"）：
        ///  - true  ：FPS 采集服务已在跑且有可上报的帧数据，<see cref="FpsCounter.GetFps"/> 的值可信；
        ///  - false ：没有 FPS 需求、服务没启动，或启动后首帧还没到 ——
        ///             此时网页/界面应输出 "--" 之类的**未启用占位**，不能把 0 当成真实的 0 FPS。
        ///
        /// 转发自 <see cref="FpsCounter.HasFpsData"/>。
        /// </summary>
        public bool HasFpsData => _fpsCounter?.HasFpsData ?? false;
        #endregion

        #region Constructor
        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            // 1. 初始化 Computer
            _computer = new Computer()
            {
                // ★★★ 修正：强制开启 CPU ★★★
                // 必须始终为 true。如果依赖 IsAnyEnabled("")，当用户未开启任何 CPU 监控项时，
                // LHM 将不会初始化 CPU 节点。后续即使通过热切换开启了风扇监控，
                // 由于 RefreshHardwareConfig 仅切换 IsControllerEnabled 而不更新 IsCpuEnabled，
                // 依然无法读取到依赖 CPU 节点的传感器数据。
                IsCpuEnabled = true, 
                
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                
                // ★★★ 优化 T0：动态开启控制器扫描 ★★★
                // 默认关闭以避免 USB 冲突，仅当需要风扇/水泵时开启
                IsControllerEnabled = ShouldEnableController(), 

                // 开启电池监控
                IsBatteryEnabled = true,

                // 顺便确保 PSU 也关闭（通常不需要监控电源模块，除非是高端 Corsair 电源）
                IsPsuEnabled = false
            };

            // ★★★ [新增] 1. 初始化计数器管理器 (必须在 ValueProvider 之前) ★★★
            _perfCounterManager = new PerformanceCounterManager();

            // 2. 初始化服务
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager(_perfCounterManager);
            _diskManager = new DiskManager();
            _driverInstaller = new DriverInstaller(cfg, ReloadComputerSafe, ReleaseComputerForDriverInstall);
            _fpsCounter = new FpsCounter(_driverInstaller); // <--- 新增

            // ★★★ [修改] 2. 采集统计计数器 + 调度 + 设备计数入口 ★★★
            _samplingStatistics = new HardwareSamplingStatistics();

            // 设备 Update 计数入口：网卡 / 磁盘的**精确**计数由子管理器负责
            // （NetworkManager / DiskManager 只在真的执行了 hw.Update() 时计数），
            // 采集层只负责 CPU / 显卡 / 内存 / 电池 / 主板，避免同一个 Key 被记两遍。
            _deviceCounter = new HardwareDeviceUpdateCounter(_samplingStatistics);

            // 慢扫描 / 磁盘后台扫描改用**单调截止时间**（同一到期时间只消费一次）。
            _schedule = new HardwareSamplingSchedule();

            // 维护任务调度器由**本实例**持有（流量保存 5s/60s、GC 30s/180s、工作集 45s/300s）。
            _maintenance = SystemOptimizer.CreateScheduler();

            // ★ 网卡 / 磁盘的真实 Update 次数接线（A2a 提供属性，这里只接上统计实例）
            _networkManager.SamplingStatistics = _samplingStatistics;
            _diskManager.SamplingStatistics = _samplingStatistics;

            // 3. 采集协调器: 唯一真实采集入口 (单调时钟门限 + 并发合并 + 代次)
            //    ★ 必须先于 ValueProvider 构造：Provider 的"设备选择变更"回调要调用
            //      协调器的 Invalidate()（让该轮被记为**例外采集**）。
            _coordinator = new HardwareSamplingCoordinator(
                periodProvider: ComputeEffectivePeriodMs,
                sampler: CollectAsync,
                onCollectionStarted: OnCollectionStarted,
                onCollectionCompleted: OnCollectionCompleted,
                statistics: _samplingStatistics,
                // ★ 采集落定回调：作废"采集中途被读到并写进缓存的旧值"（闭合钉住旧值的窗口）
                onCollectionSettled: OnCollectionSettled);

            // 4. 将 Manager 注入给 ValueProvider
            _valueProvider = new HardwareValueProvider(
                _computer, 
                cfg, 
                _sensorMap, 
                _networkManager, 
                _diskManager, 
                _fpsCounter, 
                _perfCounterManager, 
                _lock, 
                _lastValidMap,
                _samplingStatistics,
                onDeviceSelectionChanged: () => _coordinator.Invalidate()
            );

            // 5. 异步启动 (唯一优化：不卡UI)
            InitializeAsync();
        }
        #endregion

        #region Public Methods

        public float? Get(string key)
        {
            return _isOpening ? _valueProvider.GetStartupValue(key) : _valueProvider.GetValue(key);
        }

        public string GetNetworkIP() => _networkManager.GetCurrentIP();

        // ★★★ 新增：允许主程序手动触发驱动检查 (用于解决启动弹窗冲突) ★★★
        public Task SmartCheckDriver() => _driverInstaller.SmartCheckDriver();

        // ★★★ [新增] 动态刷新硬件配置 (热切换) ★★★
        public void RefreshHardwareConfig()
        {
            // 1. 计算期望状态
            bool targetState = ShouldEnableController();

            // 2. 检查当前状态 (无需加锁，bool读写原子且 IsControllerEnabled 只是个属性)
            bool currentState = _computer.IsControllerEnabled;

            // 3. 如果状态一致，无需操作
            if (targetState == currentState) return;

            // ★★★ [需求变更] 关闭风扇/水泵不需要重载，仅首次开启时重载 ★★★
            // 避免关闭监控项时触发重载导致软件短暂卡顿或设备重连
            // 如果当前已开启 (true) 且目标是关闭 (false)，则保持开启状态，不执行重载
            if (currentState && !targetState)
            {
                System.Diagnostics.Debug.WriteLine($"[HotSwap] Controller disable requested but ignored to avoid reload (Latch Mode).");
                return;
            }

            // 4. 状态变更 (False -> True)，触发异步重载
            System.Diagnostics.Debug.WriteLine($"[HotSwap] Controller State Change: {currentState} -> {targetState}");

            // Fire-and-forget 异步任务，避免阻塞 UI
            Task.Run(() =>
            {
                // 更新配置属性
                _computer.IsControllerEnabled = targetState;

                // 触发安全重载
                // 由于 ReloadComputerSafe 全程持有 _lock，
                // 而 UpdateAll 和 GetValue 都改用了 TryEnter，
                // 所以这里会独占 _lock 几秒钟，期间 UI 不会卡死（只会显示空数据）。
                ReloadComputerSafe();
            });
        }

        /// <summary>
        /// 采集入口（兼容外观）：与 <see cref="UpdateIfDueAsync"/> 共用同一套门限与并发保护。
        ///
        /// ★ 真实硬件采集现在由 <see cref="HardwareSamplingCoordinator"/> 限频：
        /// 未到期时这里不会调用 hw.Update()、不会清值缓存、不会推进采集时间。
        /// </summary>
        public void UpdateAll()
        {
            _coordinator.RequestCollection();
        }

        /// <summary>
        /// 采集入口（异步）：UI / Web 都通过它请求采样。
        /// 返回 true 表示本次请求触发（或共享到）了一次真实采集。
        /// </summary>
        public Task<bool> UpdateIfDueAsync(CancellationToken cancellationToken = default)
        {
            return _coordinator.RequestCollectionAsync(cancellationToken);
        }

        /// <summary>
        /// 通知采集层"当前有合法的 Web 数据需求"。
        ///
        /// ★ 契约（Web 层 / B2 负责调用）：
        ///  - <paramref name="hasDemand"/> = **已鉴权且尚未关闭的 WebSocket 连接数 &gt; 0**，
        ///    或**已鉴权的 /api/snapshot 请求仍在租期内**（建议 2 秒租期）；
        ///  - 未鉴权请求、只取 HTML/favicon、页面还没连上 → 必须传 false；
        ///  - 最后一个连接关闭且租期过期 → 传 false（本地需求不受影响，会自动恢复）；
        ///  - Web 服务 Stop / 重启 → 先传 false 清理旧需求。
        ///
        /// ★ 语义：它决定"是否按**全量普通指标**需求更新"，**不再**由 WebServerEnabled 开关决定
        ///   （旧行为是"Web 开关一开就全量采集"，无人访问也会白采）。
        ///   本地需求（面板/任务栏启用的指标）与它**完全无关**，断开网页不会清空本地需求。
        /// </summary>
        public void SetWebDataDemand(bool hasDemand)
        {
            _webDataDemand = hasDemand;
        }

        /// <summary>
        /// 计算当前有效的采集周期（毫秒）。
        /// 规则（计划 §4.1）：普通硬件最高每秒一次；慢刷新用户保留自己的慢周期。
        /// </summary>
        private int ComputeEffectivePeriodMs()
        {
            int refresh = _cfg.RefreshMs;
            if (refresh <= 0) refresh = DefaultRefreshMs;

            int localPeriod = Math.Max(HardwareSamplingCoordinator.MinimumPeriodMs, refresh);

            // 有已鉴权的 Web 数据需求时按 1 秒采集（当前阶段 Web 需求恒为 false）
            if (_webDataDemand) return HardwareSamplingCoordinator.MinimumPeriodMs;

            return localPeriod;
        }

        /// <summary>采集动作的异步包装（真实工作是同步的）。</summary>
        private Task CollectAsync(CancellationToken cancellationToken)
        {
            CollectOnce();
            return Task.CompletedTask;
        }

        /// <summary>采集开始：推进值缓存的采集代次。</summary>
        private void OnCollectionStarted(int generation)
        {
            _valueProvider.OnCollectionStarted(generation);
        }

        /// <summary>采集结束（目前仅用于诊断）。</summary>
        private void OnCollectionCompleted(int generation, bool success)
        {
            System.Diagnostics.Debug.WriteLine($"[HardwareMonitor] 采集代次 {generation} 结束, 成功={success}");
        }

        /// <summary>
        /// 采集落定（在采样动作**之后**调用）。
        ///
        /// ★ 作用：采集期间（hw.Update() 还没做完）如果有别的线程读了值，
        ///   那些"更新前"的旧值会被写进本代次缓存并钉住一整个周期。
        ///   这里在采集结束后把缓存再作废一次，让下一次读取用更新后的传感器重新算。
        /// </summary>
        private void OnCollectionSettled(int generation)
        {
            _valueProvider.OnCollectionSettled(generation);
        }

        /// <summary>
        /// 一次真实采集（原 UpdateAll 的全部工作）。
        /// 只有通过协调器门限后才会被调用。
        /// </summary>
        private void CollectOnce()
        {
            if (_disposed) return;
            if (!_isReady) return;   // 尚未就绪：不发生真实采集

            // [Fix #290] 标记是否需要触发重载（因硬件变更或故障）
            bool needsReload = false;

            try
            {
                // 1. 更新计时器
                double timeDelta = UpdateTiming();

                // 2. 计算更新需求
                var requirements = CheckUpdateRequirements();

                lock (_lock)
                {
                    // [Optimization] 极简维护
                    // 仅每10分钟做一次兜底检查 (内部自动 Rebuild)。
                    // ★ 重建后必须刷新 ValueProvider 的静态缓存: GetValue 的通用兜底只查
                    //   _manualSensorCache (已移除对 _sensorMap 的直接查找), 不刷新则
                    //   "映射表里补上了键" 与 "取值层能读到该键" 是两回事。
                    if (_sensorMap.EnsureFresh(_computer, _cfg))
                    {
                        _valueProvider.PreCacheAllSensors(_sensorMap);
                    }

                    // 主板/SuperIO、非目标网卡：不快于每 3 秒一次
                    // ★ 改用**单调截止时间**（同一到期时间只消费一次；跳过若干周期后最多执行一次）。
                    //   旧实现是 `_secondsCounter % 3 == 0`：同一个到期秒里被调用 N 次就会消费 N 次，
                    //   而且依赖墙上时间（系统时间回拨会让到期判断失真）。
                    bool isSlowScanTick = _schedule.TryConsumeSlowScan();
                    // 磁盘后台扫描：不快于每 10 秒一次（同样改用单调截止时间）
                    bool needDiskBgScan = _schedule.TryConsumeDiskBackgroundScan();

                    if (isSlowScanTick) _samplingStatistics.RecordSlowScanTrigger();
                    if (needDiskBgScan) _samplingStatistics.RecordDiskBackgroundScanTrigger();

                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu && requirements.NeedCpu) { UpdateDevice(hw, "Cpu"); continue; }
                        
                        // [Fix #290] 显卡防闪退保护：双显卡切换时 Update 可能抛出异常
                        if (IsGpu(hw) && requirements.NeedGpu) 
                        { 
                            // ★ 全量普通指标需求（Web）**不得**绕过显卡选择：
                            //   只更新"当前可输出的 GPU key"对应的卡（监控卡集合 + 首选卡）。
                            //   MonitoredGpuCount=1 的 Web 不会隐式更新两张物理卡；
                            //   配置两卡时两卡都在监控集合里，都会更新。编号与首选卡规则保持不变。
                            if (!ShouldUpdateGpuHardware(hw)) continue;

                            try { hw.Update(); RecordDeviceUpdate(hw, "Gpu"); }
                            catch (Exception ex)
                            {
                                HardwareErrorLog.Write("UpdateAll.GPU", ex);
                                // ★ 显卡更新失败必须可观测：否则 FailedCollections 是恒零的死指标。
                                _samplingStatistics.RecordFailedCollection();
                                System.Diagnostics.Debug.WriteLine($"[GPU Error] Update failed: {ex.Message}");
                                needsReload = true;
                            }
                            continue; 
                        }

                        if (hw.HardwareType == HardwareType.Memory && requirements.NeedMem) { UpdateDevice(hw, "Memory"); continue; }
                        if (hw.HardwareType == HardwareType.Battery && requirements.NeedBat) { UpdateDevice(hw, "Battery"); continue; }

                        if (hw.HardwareType == HardwareType.Network && requirements.NeedNet)
                        {
                            // ★★★ 修复：使用正确的 timeDelta，而不是 requirements.TimeDelta (它是0) ★★★
                            _networkManager.ProcessUpdate(hw, _cfg, timeDelta, isSlowScanTick);
                            // ★ 网卡的 Update 计数由 NetworkManager 负责
                            //   （它只在**真的执行了 hw.Update()** 时计数，Key 格式 "类型:名称"）。
                            //   采集层**不再**重复计数，否则同一个 Key 会被记两遍。
                            continue;
                        }
                        if (hw.HardwareType == HardwareType.Storage && requirements.NeedDisk)
                        {
                            // ★ 必须为每个 Storage 设备调用 ProcessUpdate，否则后台磁盘永远不会被更新。
                            //   磁盘的 Update 计数同样由 DiskManager 负责（只在真的 Update 时计数）。
                            _diskManager.ProcessUpdate(hw, _cfg, isSlowScanTick, needDiskBgScan);
                            continue;
                        }
                        
                        // 递归更新主板 (Motherboard / SuperIO)
                        if (IsMoboOrCooler(hw) && requirements.NeedMobo)
                        {
                             // ★★★ [优化] 降低更新频率：主板传感器每 3 秒更新一次，减少 I/O 阻塞 ★★★
                             // [Fix] 无论是否 ForceAll (WebServer)，只要是 SuperIO 这种慢速设备，
                             // 都必须强制跟随慢速扫描周期 (isSlowScanTick)，禁止高频更新。
                             if (isSlowScanTick)
                             {
                                 UpdateWithSubHardware(hw);
                                 RecordDeviceUpdate(hw, "Motherboard");
                             }
                             continue;
                        }
                    }
                }

                // 任务错峰执行 (维护任务)
                // ★ 维护调度由**本实例持有的** MaintenanceScheduler 驱动（单调截止时间：
                //   流量保存 5s/60s、GC 30s/180s、工作集 45s/300s，同一到期时间只消费一次）。
                //   计数记在 RunDueTasks() 返回的"本次**真正到期并被消费**"的任务上，
                //   而不是按旧的整数秒取模语义推算
                //   （旧口径会出现"同一到期秒被消费十次"的记账版本缺陷）。
                MaintenanceRunResult maintenance = _maintenance.RunDueTasks();
                RecordMaintenanceTriggers(_samplingStatistics, maintenance);
                
                OnValuesUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                // ★ 异常必须可观测：写错误日志 + 记一次失败采集。
                //   旧实现是 `catch { }`：全部异常被吞掉 → 协调器收到的 sampler 永远不抛 →
                //   协调器的 catch 与 success=false 分支在生产永远不发生 →
                //   交付出去的 FailedCollections 是**恒零的死指标**。
                HardwareErrorLog.Write("CollectOnce", ex);
                _samplingStatistics.RecordFailedCollection();
            }

            // [Fix #290] 硬件故障自动恢复
            // 如果检测到 GPU 丢失或驱动崩溃，触发安全重载以刷新硬件列表
            if (needsReload && !_isReloading)
            {
                _isReloading = true;
                System.Diagnostics.Debug.WriteLine("[HardwareMonitor] Hardware change detected, triggering reload...");
                
                // 异步延迟重载，给硬件切换留出缓冲时间
                Task.Run(async () => 
                {
                    try
                    {
                        await Task.Delay(2000); // 等待 2秒让系统硬件列表稳定
                        ReloadComputerSafe();
                    }
                    finally
                    {
                        _isReloading = false;
                    }
                });
            }
        }

        /// <summary>更新一个设备并记录计数。</summary>
        private void UpdateDevice(IHardware hw, string category)
        {
            hw.Update();
            RecordDeviceUpdate(hw, category);
        }

        /// <summary>
        /// 记录一次设备更新次数（Key 形如 "GPU.0" / "Cpu:xxx"）。
        ///
        /// ★ 网卡（Network）与磁盘（Storage）**不在这里计数**：
        ///   它们的精确 Update 次数由 NetworkManager / DiskManager 记录
        ///   （只在真的执行了 hw.Update() 时计数），采集层再记一次会让同一个 Key 被记两遍。
        /// </summary>
        private void RecordDeviceUpdate(IHardware hw, string category)
        {
            _deviceCounter.Record(BuildDeviceKey(hw), category);
        }

        /// <summary>
        /// 构造设备计数 Key。
        /// 显卡用应用里的编号 ("GPU.0" / "GPU.1")，与界面上的 Key 一一对应；
        /// 其它设备用 "类型:名称"。
        /// </summary>
        private string BuildDeviceKey(IHardware hw)
        {
            if (IsGpu(hw))
            {
                var gpus = _sensorMap.CachedGpus;
                for (int i = 0; i < gpus.Count; i++)
                {
                    if (ReferenceEquals(gpus[i], hw)) return "GPU." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                return "GPU:" + hw.Name;
            }
            return hw.HardwareType + ":" + hw.Name;
        }

        public void CleanMemory(Action<int>? onProgress = null) => SystemOptimizer.CleanMemory(onProgress);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ★★★ 关闭顺序：先停止接受新的采集请求，再等在途采集结束，最后关闭硬件 ★★★
            //   否则可能在对已关闭的 Computer 上执行 hw.Update()。
            _coordinator.Dispose();
            _coordinator.WaitForIdle(TimeSpan.FromSeconds(2));

            // ★★★ 核心修复：加锁！防止与正在运行的采集任务冲突 ★★★
            lock (_lock)
            {
                try 
                {
                    // 将 Close 操作放入锁内，确保此时采集任务不在运行
                    _computer.Close(); 
                }
                catch (Exception ex)
                {
                    // 吞掉关闭时的异常，防止弹窗报错困扰用户
                    System.Diagnostics.Debug.WriteLine($"Dispose Error: {ex.Message}");
                }
            }

            _valueProvider.Dispose();
            _perfCounterManager.Dispose(); // ★★★ [新增] 释放计数器资源 ★★★
            _fpsCounter.Dispose(); // <--- 新增
            _networkManager.ClearCache();
            _diskManager.ClearCache(); // 漏掉的，补上
        }
        #endregion

        #region Private Helpers (Logic)

        private void InitializeAsync()
        {
            Task.Run(async () =>
            {
                _isOpening = true;
                try
                {
                    // ★★★ [自检] 发布目录里是否残留旧版本文件 ★★★
                    //   把新版本 publish 到旧输出目录(不先清空)时，目录里会同时留着
                    //   旧程序集名(AssemblyName=LiteMonitor)的那一套文件，可能与当前 deps.json 冲突，
                    //   导致 LHM 打不开硬件树、硬件指标全部恒 0。这里只写日志，不打扰用户。
                    HardwareErrorLog.CheckPublishDirectory();

                    // ★★★ [新增] 启动计数器预热 (不阻塞主 UI) ★★★
                    _perfCounterManager.InitializeAsync();

                    lock (_lock)
                    {
                        // LHM 打开双 Nvidia 显卡时可能很慢。启动阶段 Get() 已改走性能计数器兜底，
                        // 这里继续加锁只会让设置页等硬件树稳定，不会拖住 CPU/MEM/DISK 首屏数据。
                        _computer.Open();
                        WarmUpMotherboardSensors();
                        WarmUpBatterySensors();
                        WarmUpGpuSensors();          // ★★★ [新增] Intel 核显负载传感器懒激活预热

                        // 先建立映射，再由正常刷新循环更新数值，避免启动时预热所有 GPU 拖慢 CPU/MEM 展示。
                        _sensorMap.Rebuild(_computer, _cfg);
                        
                        // ★★★ [新增] 静态化预热：将所有传感器对象存入 Provider 缓存 ★★★
                        _valueProvider.PreCacheAllSensors(_sensorMap);
                    }

                    _isOpening = false;

                    // 首轮缓存就绪后再做历史记录禁用和内存整理，避免延后第一屏数据。
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);

                        lock (_lock)
                        {
                            DisableSensorHistory();
                        }

                        // 优化 T1：启动后大扫除
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        SystemOptimizer.TrimWorkingSet();
                    });
                }
                catch (Exception ex)
                {
                    // [Fix] Release 构建里 Debug.WriteLine 会被整段编译掉。
                    // 之前 LHM 打开失败会被完全静默：程序照常启动，CPU/内存等走性能计数器的指标正常，
                    // 只有 LHM 系指标全是 0，用户既看不到提示，也拿不到任何日志。
                    LastHardwareError = ex.ToString();
                    HardwareErrorLog.Write("InitializeAsync", ex);
                    System.Diagnostics.Debug.WriteLine($"Init Error: {ex.Message}");
                }
                finally
                {
                    _isOpening = false;

                    // 初始化尝试结束（成功或失败都算结束）：此后允许采集。
                    // 失败时硬件树是空的，采集会自然空转，不影响性能计数器兜底取值。
                    if (!_disposed)
                    {
                        _isReady = true;
                        _coordinator.SetReady(true);
                    }
                }
            });
        }

        // ★★★ [新增] 动态判断是否需要开启控制器 ★★★
        private bool ShouldEnableController()
        {
            // 检查是否开启了任何需要读取外部控制器的监控项。
            // 主板温度走 Motherboard/SuperIO 硬件树，不应该因此打开 Controller 扫描。
            if (_cfg.IsAnyEnabled("CPU.Fan")) return true;
            if (_cfg.IsAnyEnabled("CPU.Pump")) return true;
            if (_cfg.IsAnyEnabled("CASE.Fan")) return true;
            return false;
        }

        private void WarmUpMotherboardSensors()
        {
            // 主板硬件树本来就是常开。启动时预热一次，保证主板温度即使暂未显示，
            // 也能在 SensorMap.Rebuild 阶段完成有效映射，后续打开显示项可直接读缓存。
            foreach (var hw in _computer.Hardware)
            {
                if (IsMotherboardSensorHardware(hw))
                {
                    try { UpdateWithSubHardware(hw); }
                    catch { }
                }
            }
        }

        private void WarmUpBatterySensors()
        {
            // 电池传感器在新版硬件库中可能需要先 Update 才会稳定暴露数值。
            // 只预热 Battery，避免恢复启动时全硬件 Update 带来的多显卡/磁盘卡顿。
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Battery)
                {
                    try { hw.Update(); }
                    catch { }
                }
            }
        }

        private void WarmUpGpuSensors()
        {
            // Intel 核显的负载/显存传感器是懒激活的: 硬件对象构造时只创建传感器实例,
            // 只在 Update() 里才调用 ActivateSensor 注册进 Sensors 集合; 而 NVIDIA / AMD /
            // Intel 独显 (IntelDiscreteGpu) 的构造函数末尾都自调 Update(), 传感器开箱即就绪。
            // 若不补这一次 Update, SensorMap.Rebuild 建表时拿不到核显的负载传感器,
            // "GPU.Load" / "GPU.0.Load" 键直接缺失 -> 占用率恒为 0 且无法自愈。
            //
            // 只对"没有 Load 传感器"的显卡预热 (GpuWarmup.NeedsWarmUp 判定),
            // 独显机器零额外开销, 不会拖慢启动。
            //
            // 本方法必须在 HardwareMonitor._lock 内调用: SensorMap.Rebuild 在
            // ReloadComputerSafe 里是锁外执行的, 若把预热塞进 Rebuild 就会在锁外
            // 触发 hw.Update(), 与 UpdateAll 持锁做的 hw.Update() 竞争同一硬件对象。
            GpuWarmup.WarmUpIfNeeded(_computer, UpdateWithSubHardware);
        }

        private double UpdateTiming()
        {
            // 1. 统一心跳计数 (假设 UpdateAll 约 1秒调用一次)
            _tickCounter++;
            
            // 2. 计算精确时间差 (仅用于网速计算)
            //    ★ 改用**单调时钟**：墙上时间回拨（用户改系统时间 / NTP 校时）不会产生负 delta。
            //      这里量的是"两次真实采集之间的经过时间"，与 UI Tick 次数无关。
            long now = _time.GetTimestamp();
            double timeDelta = 0;
            if (_hasTrafficTimestamp)
            {
                timeDelta = _time.GetElapsedTime(_lastTrafficTimestamp, now).TotalSeconds;
                if (timeDelta < 0) timeDelta = 0;
            }
            _lastTrafficTimestamp = now;
            _hasTrafficTimestamp = true;
            if (timeDelta > 5.0) timeDelta = 0; // 防止休眠唤醒后的数据突刺

            // ★ 刷新率自适应逻辑：把经过时间累加，凑满 1 秒才增加 _secondsCounter。
            //   （_secondsCounter 现在只作为**诊断计数**保留，不再参与任何调度判断；
            //     慢扫描 / 磁盘后台扫描 / 维护任务都已经改用单调截止时间。）
            _secondAccumulator += timeDelta;
            while (_secondAccumulator >= 1.0)
            {
                _secondAccumulator -= 1.0;
                _secondsCounter++;
            }

            return timeDelta;
        }

        /// <summary>
        /// 计算这一轮采集的更新需求。
        ///
        /// ★ 全量判断（ForceAll）现在反映**有效的 Web 数据需求**（<see cref="SetWebDataDemand"/>），
        ///   而不是"Web 服务开关"：
        ///   - Web 开关开着但**没有合法需求**（没人连、没有已鉴权快照请求）→ **不扩大**采集范围；
        ///   - 有合法需求 → 按全量普通指标需求更新；
        ///   - 本地需求（面板/任务栏启用的指标）与 Web 无关，断开网页不会被清空。
        /// </summary>
        private HardwareUpdateRequirements CheckUpdateRequirements()
        {
            // 1. 获取计数器状态
            bool useCounter = _cfg.UseWinPerCounters && _perfCounterManager.IsInitialized;

            return HardwareUpdateRequirements.Compute(_cfg, _webDataDemand, useCounter);
        }

        /// <summary>
        /// 把"本次真正到期并被消费"的维护任务记进统计计数器。
        ///
        /// ★ 计数必须记在**真实触发**上，而不是按整数秒取模推算：
        ///   旧口径（<c>_secondsCounter % 60 == 5</c>）在同一个到期秒里被调用十次就会记十次，
        ///   正是"同一到期秒被消费十次"缺陷的**记账版本**。
        ///
        /// 抽成静态方法是为了能在不构造 <see cref="HardwareMonitor"/>（它会打开真实硬件树）
        /// 的前提下做单元测试。
        /// </summary>
        public static void RecordMaintenanceTriggers(HardwareSamplingStatistics statistics, MaintenanceRunResult result)
        {
            if (statistics == null) return;
            if (result.TrafficSave) statistics.RecordTrafficSaveTrigger();
            if (result.GcRequest) statistics.RecordGcRequestTrigger();
            if (result.WorkingSetTrim) statistics.RecordWorkingSetTrimTrigger();
        }

        private void ReloadComputerSafe()
        {
            bool opened = false;

            // ★ 关闭/重开硬件期间不允许真实采集（硬件树正在被整体替换） ★
            _isReady = false;
            _coordinator.SetReady(false);

            try
            {
                lock (_lock)
                {
                    // 1. 清理业务缓存
                    //    [Fix] 这里原本还有一句 _sensorMap.Clear()，必须去掉。
                    //    旧实现是「先清空映射表 -> 再重开硬件 -> 最后 Rebuild」，
                    //    而这三步全在同一个 try 里：中途任何一步抛异常都会跳到 catch，
                    //    Rebuild 永远不执行，映射表就永久停在空状态，
                    //    结果是所有 LHM 指标恒 0，而且异常被 catch { } 吞掉、毫无痕迹。
                    //    SensorMap.Rebuild 内部本来就是在锁内原子交换 _map，所以这里无需提前清空。
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _valueProvider.ClearCache();

                    // 清理扫描器缓存
                    HardwareScanner.ClearCache();

                    // 2. 清理字符串池
                    UIUtils.ClearStringPool();

                    // 3. 关闭旧硬件服务
                    try
                    {
                        if (_computer != null)
                        {
                            _computer.Accept(new HardwareVisitor(h => { }));
                            _computer.Close();
                            // ★★★ 核心修复：手动清空硬件列表 ★★★
                            // LHM 的 Close() 不会清空列表，必须手动 Clear，否则再次 Open 会追加重复硬件
                            _computer.Hardware.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        HardwareErrorLog.Write("ReloadComputerSafe.Close", ex);
                    }

                    // 4. 重新打开硬件（失败不再静默，而是记录原因并保留旧映射表）
                    try
                    {
                        _computer.Open();
                        opened = true;
                    }
                    catch (Exception ex)
                    {
                        LastHardwareError = ex.ToString();
                        HardwareErrorLog.Write("ReloadComputerSafe.Open", ex);
                    }

                    if (opened)
                    {
                        try { WarmUpMotherboardSensors(); }
                        catch (Exception ex) { HardwareErrorLog.Write("ReloadComputerSafe.WarmUpMotherboard", ex); }

                        try { WarmUpBatterySensors(); }
                        catch (Exception ex) { HardwareErrorLog.Write("ReloadComputerSafe.WarmUpBattery", ex); }

                        // 热插拔 / 重开硬件后, 显卡对象被重建, 传感器回到"未上线"状态, 需重新预热
                        try { WarmUpGpuSensors(); }
                        catch (Exception ex) { HardwareErrorLog.Write("ReloadComputerSafe.WarmUpGpu", ex); }

                        try { DisableSensorHistory(); }
                        catch (Exception ex) { HardwareErrorLog.Write("ReloadComputerSafe.DisableSensorHistory", ex); }
                    }
                }
            }
            catch (Exception ex)
            {
                HardwareErrorLog.Write("ReloadComputerSafe", ex);
            }

            // 5. 重建映射表
            //    [Fix] 这一段独立于上面的风险步骤：无论前面成功与否都会尝试执行。
            //    只有 Open 成功才重建，避免拿空硬件树把还能用的旧映射冲掉。
            try
            {
                if (opened && _computer != null)
                {
                    _sensorMap.Rebuild(_computer, _cfg);
                    _valueProvider.PreCacheAllSensors(_sensorMap);

                    // [Fix] 硬件层已经恢复正常，清掉上一次失败的残留错误。
                    // 否则 UI 侧会拿到一条「过期」的失败原因并弹出误导性的错误气泡。
                    LastHardwareError = null;
                }
                else
                {
                    // 打开失败：保留原有映射表，宁可读到陈旧数据，也不要把所有指标退化成 0
                    HardwareErrorLog.Write("ReloadComputerSafe", "Computer.Open() 失败，已保留原有映射表以避免所有指标退化为 0。");
                }
            }
            catch (Exception ex)
            {
                HardwareErrorLog.Write("ReloadComputerSafe.Rebuild", ex);
            }

            // 6. 优化 T1：重置后再次修剪内存
            try
            {
                GC.Collect();
                SystemOptimizer.TrimWorkingSet();
            }
            catch (Exception ex)
            {
                HardwareErrorLog.Write("ReloadComputerSafe.Trim", ex);
            }
            finally
            {
                // ★ 重载结束：重新允许采集，并允许立刻采一次新样本 ★
                //   打开失败时硬件列表是空的，采集会自然空转，旧的映射表与旧的有效值仍然保留。
                if (!_disposed)
                {
                    _isReady = true;
                    _coordinator.SetReady(true);
                }
            }
        }

        private void ReleaseComputerForDriverInstall()
        {
            try
            {
                lock (_lock)
                {
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    _valueProvider.ClearCache();
                    HardwareScanner.ClearCache();

                    try
                    {
                        _computer.Accept(new HardwareVisitor(h => { }));
                        _computer.Close();
                        _computer.Hardware.Clear();
                    }
                    catch (Exception ex)
                    {
                        HardwareErrorLog.Write("ReleaseComputerForDriverInstall", ex);
                        System.Diagnostics.Debug.WriteLine($"[DriverInstaller] 释放硬件监控失败: {ex.Message}");
                    }
                }
            }
            catch { }
        }

        // =========================================================
        // ★★★ 核心修复：禁用所有传感器的历史记录 ★★★
        // 这将阻止 LibreHardwareMonitor 在内存中保留 24 小时的数据缓存
        // =========================================================
        private void DisableSensorHistory()
        {
            try { _computer.Accept(new DisableHistoryVisitor()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemoryFix] Failed: {ex.Message}"); }
        }

        // 递归更新子硬件，确保 SuperIO 刷新
        private void UpdateWithSubHardware(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) UpdateWithSubHardware(sub);
        }

        private static bool IsGpu(IHardware hw)
        {
            return hw.HardwareType == HardwareType.GpuNvidia || 
                   hw.HardwareType == HardwareType.GpuAmd || 
                   hw.HardwareType == HardwareType.GpuIntel;
        }

        private bool ShouldUpdateGpuHardware(IHardware hw)
        {
            return GpuUpdateSelection.ShouldUpdate(hw, _sensorMap.CachedGpu, _sensorMap.CachedGpus);
        }

        /// <summary>
        /// 当前实际被监控的显卡数量 (以传感器映射为准)。
        /// 用于把配置里的"监控显卡数量"夹到真实卡数, 避免生成空白监控项。
        /// </summary>
        public int GetMonitoredGpuCount()
        {
            // ★ 无锁读 ★
            // CachedGpus 只会被整体替换 (Rebuild 的原子交换块 / Clear), 不会被就地修改,
            // 所以读 .Count 是安全的。
            // 刻意不用 lock(_lock): 这个方法会被 UI 线程 (UIController.Tick) 调用,
            // 而 ReloadComputerSafe 持锁可达数秒, 会把界面冻住。
            return _sensorMap.CachedGpus.Count;
        }

        private static bool IsMoboOrCooler(IHardware hw)
        {
            return hw.HardwareType == HardwareType.Motherboard || 
                   hw.HardwareType == HardwareType.SuperIO || 
                   hw.HardwareType == HardwareType.Cooler;
        }

        private static bool IsMotherboardSensorHardware(IHardware hw)
        {
            return hw.HardwareType == HardwareType.Motherboard ||
                   hw.HardwareType == HardwareType.SuperIO;
        }
        #endregion

        #region Static UI Helpers (Delegated to HardwareScanner)
        public static string GenerateSmartName(ISensor sensor, IHardware hardware) => 
            HardwareScanner.GenerateSmartName(sensor, hardware, Instance!._computer);

        public static List<string> ListAllNetworks() => HardwareScanner.ListAllNetworks(Instance!._computer);

        public static List<string> ListAllDisks() => HardwareScanner.ListAllDisks(Instance!._computer);

        public static List<string> ListAllGpus()
        {
            lock (Instance!._lock)
                return HardwareScanner.ListAllGpus(Instance!._computer);
        }

        public static List<HardwareScanner.GpuOption> ListAllGpuOptions()
        {
            lock (Instance!._lock)
                return HardwareScanner.ListAllGpuOptions(Instance!._computer);
        }

        /// <summary>
        /// 取得"已检测到的显卡数量", **不阻塞 UI 线程**。
        ///
        /// 快路径: 最多等锁 10ms, 拿到就精确取一次。
        /// 慢路径: 硬件正在刷新 (ReloadComputerSafe 持锁数秒) 时, 直接读扫描器缓存,
        ///         绝不阻塞 —— 缓存里的卡数最多是"上一次"的, 但足够用来夹紧。
        /// 都拿不到时返回 0, 由调用方决定"不夹紧"。
        /// </summary>
        public static int TryGetDetectedGpuCount()
        {
            var inst = Instance;
            if (inst == null) return 0;

            if (Monitor.TryEnter(inst._lock, 10))
            {
                try
                {
                    return HardwareScanner.ListAllGpuOptions(inst._computer)?.Count ?? 0;
                }
                catch
                {
                    return 0;
                }
                finally
                {
                    Monitor.Exit(inst._lock);
                }
            }

            // 拿不到锁: 退回到扫描器缓存 (不碰硬件, 不阻塞)
            return HardwareScanner.CachedGpuCount;
        }

        public static List<string> ListAllFans() => HardwareScanner.ListAllFans(Instance!._computer, Instance!._lock);

        public static List<string> ListAllMoboTemps() => HardwareScanner.ListAllMoboTemps(Instance!._computer, Instance!._lock);
        #endregion

        #region Inner Visitors
        // ★★★ 核心修复：禁用所有传感器的历史记录 ★★★
        /// <summary>
        /// 专用访问器：将所有 Sensor 的 ValuesTimeWindow 设为 0
        /// </summary>
        private class DisableHistoryVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
                foreach (var sensor in hardware.Sensors) VisitSensor(sensor);
            }
            public void VisitSensor(ISensor sensor)
            {
                // ★ 关键：通过反射设置 ValuesTimeWindow = TimeSpan.Zero
                // 因为 ISensor 接口通常不暴露这个属性，它属于具体的 Sensor 类
                try
                {
                    var prop = sensor.GetType().GetProperty("ValuesTimeWindow");
                    if (prop != null && prop.CanWrite) prop.SetValue(sensor, TimeSpan.Zero);
                }
                catch { }
            }
            public void VisitParameter(IParameter parameter) { }
        }

        // 内部 Visitor 类，用于触发 LHM 清理逻辑
        private class HardwareVisitor : IVisitor
        {
            private readonly Action<IHardware> _action;
            public HardwareVisitor(Action<IHardware> action) { _action = action; }
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                _action(hardware);
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
        #endregion
    }

    // =====================================================================
    // 采集层的可测试策略类型
    //
    // 之所以把它们从 HardwareMonitor 里抽出来（而不是留在私有方法里）：
    //   HardwareMonitor 的构造函数会启动异步初始化并打开 LHM 硬件树，
    //   **无法在普通单元测试里构造**。把这些纯策略抽成独立类型后，
    //   就能用假时钟 + 假硬件在**不启动 LHM / 驱动 / 真实 PresentMon / 外网**的前提下
    //   锁定计划 §4.3 / §4.4 / §6.1 的硬性不变量。
    // =====================================================================

    /// <summary>
    /// 采集层的**单调截止时间**调度（计划 §4.3）。
    ///
    /// 解决的问题：旧实现用「整数秒计数器取模」判断到期
    /// （<c>_secondsCounter % 3 == 0</c> / <c>% 10 == 0</c>），
    /// 同一个到期秒里被调用 N 次就会消费 N 次，而且依赖墙上时间。
    ///
    /// 语义：
    ///  - 同一到期时间**最多消费一次**；
    ///  - 跳过若干周期后**最多执行一次**，然后安排下一次（不补跑一串）；
    ///  - 两次执行之间**至少**间隔各自的周期（见下方"为什么不用固定网格"）；
    ///  - 状态由**实例**持有，墙上时间回拨不影响判断；
    ///  - 时钟可注入 → 单元测试用假时钟，不做真 Sleep。
    /// </summary>
    public sealed class HardwareSamplingSchedule
    {
        /// <summary>主板 / SuperIO / 非目标网卡的最快更新间隔：不快于每 3 秒一次。</summary>
        public static readonly TimeSpan SlowScanInterval = TimeSpan.FromSeconds(3);

        /// <summary>磁盘后台扫描的最快间隔：不快于每 10 秒一次。</summary>
        public static readonly TimeSpan DiskBackgroundScanInterval = TimeSpan.FromSeconds(10);

        private readonly MinGapTrigger _slowScan;
        private readonly MinGapTrigger _diskBackgroundScan;

        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        public HardwareSamplingSchedule(TimeProvider? timeProvider = null)
        {
            var time = timeProvider ?? TimeProvider.System;

            // 首次延迟 = 0：第一次采集就是一次慢扫描 / 后台扫描
            //（与旧实现 "_secondsCounter == 0 时命中" 的行为一致）；之后按各自周期推进。
            _slowScan = new MinGapTrigger(time, SlowScanInterval);
            _diskBackgroundScan = new MinGapTrigger(time, DiskBackgroundScanInterval);
        }

        /// <summary>
        /// 本轮是否应执行慢扫描（主板 / SuperIO / 非目标网卡）。
        /// 同一到期时间只会被消费一次。
        /// </summary>
        public bool TryConsumeSlowScan() => _slowScan.TryConsume();

        /// <summary>
        /// 本轮是否应执行磁盘后台扫描。同一到期时间只会被消费一次。
        /// </summary>
        public bool TryConsumeDiskBackgroundScan() => _diskBackgroundScan.TryConsume();

        /// <summary>
        /// 最小间隔触发器（单调时钟）。
        ///
        /// ★ 为什么不用 <see cref="MonotonicPeriodicTrigger"/> 的固定网格推进：
        ///   固定网格把下一次到期点锚在"原截止时间 + k*周期"上，
        ///   当一次消费**迟到**时（例如采集周期 1 秒、慢扫描周期 3 秒，两条网格相位不同），
        ///   两次实际执行之间的间隔最短只有 <c>周期 - 采集周期</c>（3 - 1 = 2 秒），
        ///   违反计划 §4.3 表格里"主板/SuperIO 不快于每 3 秒一次"的硬性约定。
        ///   本类改用"now + 周期"锚定，保证两次执行之间**至少**间隔一个周期。
        ///
        /// 其余语义与 <see cref="MonotonicPeriodicTrigger"/> 一致：
        /// 同一到期时间只消费一次、跳过若干周期后最多执行一次、墙上时间回拨不影响判断。
        /// </summary>
        private sealed class MinGapTrigger
        {
            private readonly TimeProvider _time;
            private readonly long _periodTicks;
            private long _nextDue;
            private bool _armed;

            public MinGapTrigger(TimeProvider time, TimeSpan period)
            {
                _time = time;
                long frequency = time.TimestampFrequency > 0 ? time.TimestampFrequency : TimeSpan.TicksPerSecond;
                _periodTicks = (long)Math.Round(frequency * period.TotalSeconds);
                if (_periodTicks <= 0) _periodTicks = 1;
            }

            public bool TryConsume()
            {
                long now = _time.GetTimestamp();

                if (_armed && now < _nextDue) return false;

                // 下一次到期 = 现在 + 周期（保证两次执行之间至少间隔一个周期）
                _nextDue = now > long.MaxValue - _periodTicks ? long.MaxValue : now + _periodTicks;
                _armed = true;
                return true;
            }
        }
    }

    /// <summary>
    /// 一轮采集的更新需求（计划 §4.1 / §4.4）。
    ///
    /// ★ 关键语义：<see cref="ForceAll"/> 反映的是**有效的 Web 数据需求**，
    ///   而不是"Web 服务开关"。旧行为是「WebServerEnabled 一开就全量采集」，
    ///   无人访问网页时也在白采 —— 这正是本轮要修的根因。
    /// </summary>
    public readonly struct HardwareUpdateRequirements
    {
        public HardwareUpdateRequirements(
            bool forceAll,
            bool needCpu,
            bool needGpu,
            bool needMem,
            bool needNet,
            bool needDisk,
            bool needBat,
            bool needMobo)
        {
            ForceAll = forceAll;
            NeedCpu = needCpu;
            NeedGpu = needGpu;
            NeedMem = needMem;
            NeedNet = needNet;
            NeedDisk = needDisk;
            NeedBat = needBat;
            NeedMobo = needMobo;
        }

        /// <summary>是否有合法的 Web 数据需求（此时按全量普通指标需求更新）。</summary>
        public bool ForceAll { get; }
        /// <summary>是否需要更新 CPU。</summary>
        public bool NeedCpu { get; }
        /// <summary>是否需要更新显卡（**具体哪几张卡仍由显卡选择规则决定**）。</summary>
        public bool NeedGpu { get; }
        /// <summary>是否需要更新内存。</summary>
        public bool NeedMem { get; }
        /// <summary>是否需要更新网卡。</summary>
        public bool NeedNet { get; }
        /// <summary>是否需要更新磁盘。</summary>
        public bool NeedDisk { get; }
        /// <summary>是否需要更新电池。</summary>
        public bool NeedBat { get; }
        /// <summary>是否需要更新主板 / SuperIO（仍强制跟随慢扫描周期）。</summary>
        public bool NeedMobo { get; }

        /// <summary>
        /// 计算一轮采集的更新需求。
        /// </summary>
        /// <param name="cfg">当前设置（本地需求来源：面板/任务栏启用的监控项）。</param>
        /// <param name="hasWebDataDemand">
        /// 是否有**合法**的 Web 数据需求（已鉴权连接 或 未过期的已鉴权快照租期）。
        /// 传 false 时**不会**扩大采集范围，但**也不会**清空本地需求。
        /// </param>
        /// <param name="usePerformanceCounters">
        /// 是否正在使用 Windows 性能计数器（内存/磁盘速度可以不走 LHM 轮询）。
        /// </param>
        public static HardwareUpdateRequirements Compute(
            Settings cfg,
            bool hasWebDataDemand,
            bool usePerformanceCounters)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            // ★ 全量判断 = 有效的 Web 数据需求（不是 WebServerEnabled 开关）
            bool forceAll = hasWebDataDemand;

            // CPU: 总是需要 (因为 LHM 要读温度)
            bool needCpu = forceAll || cfg.IsAnyEnabled("CPU");

            // 显卡: 总是需要（**具体哪几张卡**由 ShouldUpdateGpuHardware 决定，
            //        全量需求不允许绕过显卡选择）
            bool needGpu = forceAll || cfg.IsAnyEnabled("GPU");

            // 内存: 如果走了计数器，就不需要 LHM 轮询了
            bool needMem = forceAll || (cfg.IsAnyEnabled("MEM") && !usePerformanceCounters);

            // 网络
            bool needNet = forceAll || cfg.IsAnyEnabled("NET") || cfg.IsAnyEnabled("DATA");

            // 电池: 只有在开启时才更新
            bool needBat = forceAll || cfg.IsAnyEnabled("BAT");

            // 磁盘: 智能判断
            // 只有当 (没开计数器 OR 指定了特定盘 OR 需要看温度) 时，才需要 LHM 介入
            bool needDiskTemp = cfg.IsAnyEnabled("DISK.Temp");
            bool hasSpecificDisk = !string.IsNullOrEmpty(cfg.PreferredDisk);
            bool needDiskSpeed = cfg.IsAnyEnabled("DISK") && (!usePerformanceCounters || hasSpecificDisk);
            bool needDisk = forceAll || needDiskSpeed || needDiskTemp;

            // 主板
            bool needMobo = forceAll ||
                cfg.IsAnyEnabled("MOBO") ||
                cfg.IsAnyEnabled("CPU.Fan") ||
                cfg.IsAnyEnabled("CPU.Pump") ||
                cfg.IsAnyEnabled("CASE.Fan");

            return new HardwareUpdateRequirements(
                forceAll, needCpu, needGpu, needMem, needNet, needDisk, needBat, needMobo);
        }
    }

    /// <summary>
    /// 显卡更新选择规则（计划 §4.4）。
    ///
    /// ★ 全量普通指标需求（Web）**不得**绕过这个规则：
    ///   更新"未纳入监控集合、也不在网页快照里"的物理卡没有收益。
    ///   <c>MonitoredGpuCount=1</c> 的 Web 不应隐式更新两张物理卡；配置两卡时两卡都必须更新。
    ///   首选卡与编号规则保持不变。
    /// </summary>
    public static class GpuUpdateSelection
    {
        /// <summary>
        /// 判断某个显卡这一轮是否需要更新。
        /// </summary>
        /// <param name="hw">待判断的显卡。</param>
        /// <param name="preferredGpu">当前首选卡（可为空）。</param>
        /// <param name="monitoredGpus">当前被监控的显卡集合（对应 MonitoredGpuCount）。</param>
        public static bool ShouldUpdate(IHardware hw, IHardware? preferredGpu, IReadOnlyList<IHardware>? monitoredGpus)
        {
            if (hw == null) return false;

            // ★ 映射还没建好时（首选卡与监控集合都为空）: 保持旧行为, 更新所有显卡。
            //   否则热插拔触发 SensorMap.Clear() 之后到下一次 Rebuild 之间, 显卡会完全不刷新。
            bool noMapping = preferredGpu == null && (monitoredGpus == null || monitoredGpus.Count == 0);
            if (noMapping) return true;

            if (preferredGpu != null && ReferenceEquals(hw, preferredGpu)) return true;

            if (monitoredGpus != null)
            {
                foreach (var g in monitoredGpus)
                {
                    if (ReferenceEquals(g, hw)) return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 采集层的设备 Update 计数入口。
    ///
    /// ★ 计数**归属**（防止同一个 Key 被记两遍）：
    ///   - CPU / 显卡 / 内存 / 电池 / 主板 → 由采集层（本类）计数；
    ///   - 网卡（Network）/ 磁盘（Storage） → 由 NetworkManager / DiskManager 计数
    ///     （它们只在**真的执行了 hw.Update()** 时计数，Key 格式 "类型:名称"）。
    ///
    /// 本类会**直接忽略** Network / Storage 两类，防止重复计数。
    /// </summary>
    public sealed class HardwareDeviceUpdateCounter
    {
        /// <summary>网卡分类名（由 NetworkManager 计数）。</summary>
        public const string NetworkCategory = "Network";
        /// <summary>磁盘分类名（由 DiskManager 计数）。</summary>
        public const string StorageCategory = "Storage";

        private readonly HardwareSamplingStatistics? _statistics;

        /// <param name="statistics">共享的统计计数器实例（null 时不计数）。</param>
        public HardwareDeviceUpdateCounter(HardwareSamplingStatistics? statistics)
        {
            _statistics = statistics;
        }

        /// <summary>
        /// 该分类是否由**采集层**自己计数。
        /// 网卡与磁盘返回 false（它们由子管理器精确计数）。
        /// </summary>
        public static bool IsCollectLayerOwned(string category)
        {
            if (string.IsNullOrEmpty(category)) return false;
            return !string.Equals(category, NetworkCategory, StringComparison.Ordinal)
                && !string.Equals(category, StorageCategory, StringComparison.Ordinal);
        }

        /// <summary>
        /// 记录一次设备更新。网卡 / 磁盘会被忽略（避免与子管理器重复计数）。
        /// </summary>
        public void Record(string deviceKey, string category)
        {
            if (_statistics == null) return;
            if (!IsCollectLayerOwned(category)) return;
            _statistics.RecordDeviceUpdate(deviceKey, category);
        }
    }
}
