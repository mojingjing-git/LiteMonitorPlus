using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.src.WebServer;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 调度计时器抽象（内部接缝，LM-001）。
    /// 生产实现是 <see cref="System.Timers.Timer"/> 的 Stop-Wait（AutoReset=false）包装：
    /// 触发一次之后必须由调度逻辑显式"重新武装 (re-arm)"才会再次触发。
    /// 测试可注入手动驱动实现，使"重新武装"这一关键行为能够确定性验证，无需真实等待。
    /// <para>
    /// ★ 事实（.NET 8 源码 + 实测）：<see cref="System.Timers.Timer"/> 在 <c>Elapsed</c>
    /// 处理器**内部**被赋值 <c>Interval</c> 时会**顺带重新武装**
    /// （<c>Interval</c> setter → <c>UpdateTimer()</c> → 内部 <c>_timer.Change(i, Infinite)</c>）。
    /// 该行为**未被 .NET 文档明文保证**，因此调度**不得**依赖它：
    /// 重武装必须由本调度逻辑显式调用 <see cref="IPluginTimer.Start"/> 完成。
    /// </para>
    /// </summary>
    internal interface IPluginTimer : IDisposable
    {
        /// <summary>距离下一次触发的时间（毫秒）。</summary>
        double Interval { get; set; }

        /// <summary>武装计时器（触发一次）。</summary>
        void Start();

        /// <summary>停止计时器（之后仍可再次武装）。</summary>
        void Stop();
    }

    /// <summary>
    /// 生产计时器：包装 <see cref="System.Timers.Timer"/>（AutoReset=false 的 Stop-Wait 模式）。
    /// <para>
    /// ★ 注意：给 <see cref="System.Timers.Timer"/> 的 <c>Interval</c> 赋值会顺带重新武装它
    /// （未文档化副作用）。本包装**不依赖**该副作用 —— 重新武装一律由调用方显式
    /// 调用 <see cref="Start"/> 完成（见 <c>RearmSchedule</c>）。
    /// </para>
    /// </summary>
    internal sealed class SystemPluginTimer : IPluginTimer
    {
        internal System.Timers.Timer Inner { get; }

        public SystemPluginTimer(double intervalMs)
        {
            Inner = new System.Timers.Timer(intervalMs) { AutoReset = false };
        }

        public double Interval { get => Inner.Interval; set => Inner.Interval = value; }

        public void Start() => Inner.Start();

        public void Stop() => Inner.Stop();

        public void Dispose() => Inner.Dispose();
    }

    /// <summary>
    /// 插件管理器 (Refactored)
    /// 负责插件的加载、生命周期管理以及调度执行
    /// </summary>
    public class PluginManager
    {
        private static PluginManager _instance;
        public static PluginManager Instance => _instance ??= new PluginManager();

        private readonly List<PluginTemplate> _templates = new();
        private readonly Dictionary<string, IPluginTimer> _timers = new();
        private readonly Dictionary<string, System.Threading.CancellationTokenSource> _cts = new();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
        private readonly Dictionary<string, string> _configSnapshots = new();
        private readonly PluginExecutor _executor;

        /// <summary>
        /// 调度代次（LM-001）。每个实例一个单调递增的代号：
        /// <c>StopInstance()</c> / <c>Stop()</c> / 重新 <c>StartInstance()</c> 时递增。
        /// <para>
        /// 异步回调在继续之前必须核对"自己仍属于当前代次"，否则：
        /// 不得重新启动计时器、不得写回执行结果、不得访问已释放的计时器或 CTS。
        /// </para>
        /// <para>
        /// ★ 代次只用于"结果写回裁决"，<b>不</b>参与在途请求键的构成（契约 C6）。
        /// </para>
        /// </summary>
        private readonly ConcurrentDictionary<string, long> _generations = new();

        /// <summary>
        /// 保护 <see cref="_timers"/> / <see cref="_cts"/> 的锁。
        /// 计时器回调运行在线程池线程上，与 UI 线程的启停会并发访问这两个字典。
        /// </summary>
        private readonly object _scheduleLock = new();

        public event Action OnPluginSchemaChanged;

        /// <summary>
        /// 首次心跳延迟（毫秒）：调度启动后尽快触发一次，
        /// 这样第一次执行的结果也能被捕获，从而触发失败重试逻辑。
        /// </summary>
        internal const int InitialTickDelayMs = 50;

        /// <summary>
        /// 插件不可见时的复查间隔（毫秒）：可见性是"按需刷新"的开关，
        /// 不可见时仍必须按此间隔**显式**重新武装计时器（LM-001）。
        /// <para>
        /// ★ 根因更正（2026-10-22 复核）：基线的不可见分支在 <c>try</c> 之前 <c>return</c>，
        /// 从未显式重新武装，它靠的是 <see cref="System.Timers.Timer"/> "在处理器内赋值
        /// <c>Interval</c> 即重新武装"这一**未文档化副作用**才得以继续轮询 ——
        /// 因此基线**并非永久停摆**。修复的定位是"消除对该副作用的依赖"，不是"恢复停摆"。
        /// </para>
        /// <para>internal 可变：测试可压到毫秒级以在生产计时器上验证"仍在轮询"，不改变生产取值。</para>
        /// </summary>
        internal int InvisibleRetryIntervalMs { get; set; } = 2000;

        /// <summary>
        /// 构造插件管理器。
        /// <para>
        /// internal 而非 private：测试需要构造与单例隔离的实例，
        /// 以便注入可见性 / 执行 / 计时器接缝而不触碰真实设置与真实网络。
        /// </para>
        /// </summary>
        internal PluginManager(PluginExecutor executor = null)
        {
            _executor = executor ?? new PluginExecutor();
            _executor.OnSchemaChanged += () => OnPluginSchemaChanged?.Invoke();
        }

        // ==========================================================
        // 测试接缝（internal；生产路径保持 null，走默认实现）
        // ==========================================================

        /// <summary>可见性探针：null 时使用 <see cref="IsPluginVisible"/>。</summary>
        internal Func<string, bool> VisibilityProbe { get; set; }

        /// <summary>执行探针：null 时使用 <see cref="PluginExecutor.ExecuteInstanceAsync"/>。</summary>
        internal Func<PluginInstanceConfig, PluginTemplate, System.Threading.CancellationToken, Task<bool>> ExecutionProbe { get; set; }

        /// <summary>计时器工厂：null 时使用生产计时器（System.Timers.Timer）。</summary>
        internal Func<double, Func<IPluginTimer, Task>, IPluginTimer> TimerFactory { get; set; }

        /// <summary>连续失败触发"重置网络客户端 + 清缓存"时的观察点（仅供测试断言）。</summary>
        internal Action<string> NetworkResetObserver { get; set; }

        /// <summary>测试/诊断用：读取某实例当前的连续失败计数。</summary>
        internal int GetConsecutiveFailures(string instanceId)
            => _consecutiveFailures.TryGetValue(instanceId, out var v) ? v : 0;

        /// <summary>测试/诊断用：内部执行器实例（便于测试释放其持有的 HttpClient）。</summary>
        internal PluginExecutor ExecutorInternal => _executor;

        /// <summary>测试/诊断用：当前登记在册的调度循环总数。</summary>
        internal int RegisteredScheduleCount
        {
            get { lock (_scheduleLock) { return _timers.Count; } }
        }

        /// <summary>测试/诊断用：某实例当前登记在册的调度循环数量（正常恒为 0 或 1）。</summary>
        internal int GetRegisteredScheduleCount(string instanceId)
        {
            lock (_scheduleLock) { return _timers.ContainsKey(instanceId) ? 1 : 0; }
        }

        public void LoadPlugins(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                try { Directory.CreateDirectory(directoryPath); } catch { }
                return;
            }

            // 1. 加载模版
            _templates.Clear();

            // ★ 排序后再遍历，保证去重的 tie-break 是确定性的
            //   （否则留下的那个取决于 Directory.GetFiles 的枚举顺序 —— 正是我们要消除的不确定性）
            var files = Directory.GetFiles(directoryPath, PluginConstants.CONFIG_EXT)
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                .ToList();

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var tmpl = JsonSerializer.Deserialize<PluginTemplate>(json);
                    if (tmpl != null && !string.IsNullOrEmpty(tmpl.Id))
                    {
                        // ★ 同 id 的模板只留第一个：
                        //   内置插件改 id 时旧文件不会被 CopyToOutputDirectory 删除，
                        //   新旧两个模板并存会让 FirstOrDefault 取到哪个不确定。
                        if (_templates.Any(t => t.Id == tmpl.Id))
                        {
                            System.Diagnostics.Debug.WriteLine($"[PluginManager] 跳过重复 id 的插件: {tmpl.Id} ({file})");
                            continue;
                        }
                        _templates.Add(tmpl);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load plugin {file}: {ex.Message}");
                }
            }

            // 2. 自动同步逻辑 (确保新插件出现在配置中，且已删除的插件被清理)
            var settings = Settings.Load();
            bool changed = false;

            // [新增] 清理失效的插件实例 (Template 不存在的)
            var instancesToRemove = new List<PluginInstanceConfig>();
            foreach (var inst in settings.PluginInstances)
            {
                if (!_templates.Any(t => t.Id == inst.TemplateId))
                {
                    instancesToRemove.Add(inst);
                }
            }

            foreach (var inst in instancesToRemove)
            {
                settings.PluginInstances.Remove(inst);
                // 同时清理关联的 MonitorItems
                PluginMonitorSyncService.Instance.RemoveMonitorItems(inst.Id, saveIfChanged: false);
                changed = true;
            }

            // [原有] 添加新插件实例
            foreach (var tmpl in _templates)
            {
                if (!settings.PluginInstances.Any(x => x.TemplateId == tmpl.Id))
                {
                    string newId = tmpl.Id;
                    if (settings.PluginInstances.Any(x => x.Id == newId))
                    {
                         newId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    }

                    var newInst = new PluginInstanceConfig
                    {
                        Id = newId,
                        TemplateId = tmpl.Id,
                        Enabled = false
                    };
                    
                    if (tmpl.Inputs != null)
                    {
                        foreach(var input in tmpl.Inputs)
                        {
                            newInst.InputValues[input.Key] = input.DefaultValue;
                        }
                    }
                    
                    settings.PluginInstances.Add(newInst);
                    changed = true;
                }
            }
            if (changed) settings.Save();
        }

        public List<PluginTemplate> GetAllTemplates()
        {
            return _templates;
        }

        private void CleanupPluginData(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            
            // 1. Remove raw values injected by plugin (Key starts with instanceId)
            InfoService.Instance.RemoveDataByPrefix(instanceId);
            
            // 2. Remove dynamic properties (Label, ShortLabel, etc.)
            // Keys are like PROP.Label.DASH.{instanceId}.{OutputKey}
            InfoService.Instance.RemoveDataByPrefix("PROP.Label.DASH." + instanceId);
            InfoService.Instance.RemoveDataByPrefix("PROP.ShortLabel.DASH." + instanceId);
            InfoService.Instance.RemoveDataByPrefix("PROP.Unit.DASH." + instanceId); // If we add Unit support later
        }

        public void Reload(Settings cfg)
        {
            // 1. Identify active instances
            List<string> currentIds;
            lock (_scheduleLock) { currentIds = _timers.Keys.ToList(); }
            var newInstances = cfg.PluginInstances.Where(x => x.Enabled).ToDictionary(x => x.Id);

            // 2. Process Removals
            foreach (var id in currentIds)
            {
                if (!newInstances.ContainsKey(id))
                {
                    StopInstance(id);
                    _configSnapshots.Remove(id);
                    CleanupPluginData(id);
                }
            }

            // 3. Process Additions & Updates
            foreach (var kv in newInstances)
            {
                var inst = kv.Value;
                string newHash = GetConfigHash(inst);
                bool needsRestart = false;

                // ★ 读 _timers 必须在 _scheduleLock 内：计时器回调在线程池读写同一个字典，
                //   锁外 ContainsKey 与锁内 Remove 交错会让 Dictionary 的内部状态撕裂。
                bool exists;
                lock (_scheduleLock)
                {
                    exists = _timers.ContainsKey(inst.Id);
                }

                if (exists)
                {
                    // Exists. Check if changed.
                    if (_configSnapshots.TryGetValue(inst.Id, out var oldHash) && oldHash == newHash)
                    {
                        continue; // Stable, skip
                    }
                    needsRestart = true;
                    StopInstance(inst.Id);
                }
                else
                {
                    needsRestart = true;
                }

                if (needsRestart)
                {
                    var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                    if (tmpl != null)
                    {
                        
                        PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl, saveIfChanged: false);
                        StartInstance(inst, tmpl);
                        _configSnapshots[inst.Id] = newHash;
                    }
                }
            }
            
            // Force save once if needed, or rely on caller? 
            // Caller (SettingsForm) already saves. But SyncMonitorItem might have added new items to Settings.
            // Let's save just in case structure changed.
            Settings.Load().Save();
        }
        
        private string GetConfigHash(PluginInstanceConfig inst)
        {
            try { return JsonSerializer.Serialize(inst); } catch { return ""; }
        }

        /// <summary>
        /// 停止某个插件实例的调度循环。
        /// 返回后，该实例的旧计时器不得再自行启动（LM-001）。
        /// </summary>
        internal void StopInstance(string instanceId)
        {
            StopSchedule(instanceId);

            // Reset failure count
            _consecutiveFailures.TryRemove(instanceId, out _);
        }

        /// <summary>
        /// 停止某个实例的调度循环：停表 + 释放计时器 + 取消 CTS + <b>递增代次</b>。
        /// <para>
        /// ★ 代次递增是"停止后不复活"的裁决依据：所有仍在运行的旧回调在下一次同步点
        /// 都会发现代次不匹配，从而放弃重新调度、放弃写回结果。
        /// </para>
        /// <para>
        /// ★ <c>Stop()</c> + <c>Dispose()</c> 会让 <see cref="System.Timers.Timer"/>
        /// 内部计时器被释放，此后即便旧回调再给 <c>Interval</c> 赋值也不会重新武装
        /// （赋值副作用依赖内部计时器仍然存在）—— 所以停止路径不会留下"僵尸计时器"。
        /// </para>
        /// <para>
        /// ★ 取消/释放在锁外执行：<c>CancellationTokenSource.Cancel()</c> 会同步触发
        /// 已注册的回调，回调不应该被同一把锁挡住。
        /// </para>
        /// </summary>
        private void StopSchedule(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;

            IPluginTimer timer = null;
            System.Threading.CancellationTokenSource cts = null;

            lock (_scheduleLock)
            {
                // ★ 代次递增：旧回调在继续之前都会核对代次
                _generations.AddOrUpdate(instanceId, 1, (k, v) => v + 1);

                if (_timers.TryGetValue(instanceId, out timer)) _timers.Remove(instanceId);
                if (_cts.TryGetValue(instanceId, out cts)) _cts.Remove(instanceId);
            }

            try { timer?.Stop(); } catch { }
            try { timer?.Dispose(); } catch { }
            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }
        }

        public void Start()
        {
            Stop(); 
            _configSnapshots.Clear();

            var settings = Settings.Load();
            bool anyChange = false;
            
            foreach (var inst in settings.PluginInstances)
            {
                if (!inst.Enabled) continue;

                var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
                if (tmpl == null) continue;

                bool changed = PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl, saveIfChanged: false);
                if (changed) anyChange = true;
                
                StartInstance(inst, tmpl);
                _configSnapshots[inst.Id] = GetConfigHash(inst);
            }
            
            if (anyChange) settings.Save();
        }
        
        public void RestartInstance(string instanceId, PluginInstanceConfig configOverride = null)
        {
            StopInstance(instanceId);
            
            PluginInstanceConfig inst;
            if (configOverride != null)
            {
                inst = configOverride;
            }
            else
            {
                var settings = Settings.Load();
                inst = settings.PluginInstances.FirstOrDefault(x => x.Id == instanceId);
            }
            
            if (inst == null || !inst.Enabled)
            {
                // Clean up items if disabled
                PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId, saveIfChanged: true);
                return;
            }
            
            var tmpl = _templates.FirstOrDefault(x => x.Id == inst.TemplateId);
            if (tmpl == null) return;
            
            PluginMonitorSyncService.Instance.SyncMonitorItem(inst, tmpl, saveIfChanged: true);
            StartInstance(inst, tmpl);
            _configSnapshots[inst.Id] = GetConfigHash(inst);
        }

        public void RemoveInstance(string instanceId)
        {
            StopInstance(instanceId);
            _configSnapshots.Remove(instanceId);
            CleanupPluginData(instanceId);

            PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId, saveIfChanged: true);
        }

        public void Stop()
        {
            List<string> ids;
            lock (_scheduleLock)
            {
                ids = _timers.Keys.Concat(_cts.Keys).Distinct().ToList();
            }

            // ★ 逐实例走 StopSchedule：每个实例的代次都会递增，
            //   返回后旧计时器不得再自行启动（LM-001）。
            foreach (var id in ids) StopSchedule(id);
        }

        private bool IsPluginVisible(string instanceId)
        {
            var settings = Settings.Load();
            if (settings?.MonitorItems == null) return false;

            // 1. Web Server Check
            if (LiteWebServer.Instance != null && LiteWebServer.Instance.IsRunning) return true;

            // 2. Monitor Items Visibility Check
            string prefix = PluginConstants.DASH_PREFIX + instanceId + ".";
            lock (settings.MonitorItems)
            {
                foreach (var item in settings.MonitorItems)
                {
                    if (item.Key.StartsWith(prefix))
                    {
                        if (item.VisibleInPanel || item.VisibleInTaskbar) return true;
                    }
                }
            }
            return false;
        }

        public static int GetMinimumInterval(PluginTemplate tmpl)
        {
            return Math.Max(PluginConstants.DEFAULT_INTERVAL, tmpl?.Execution?.MinInterval ?? 0);
        }

        public static int GetEffectiveInterval(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            int interval = inst.CustomInterval > 0 ? inst.CustomInterval : tmpl.Execution.Interval;
            return Math.Max(interval, GetMinimumInterval(tmpl));
        }

        /// <summary>
        /// 启动某个插件实例的调度循环。
        /// <para>
        /// ★ 先无条件停掉同一实例可能残留的旧循环，保证"同一实例最多一个有效调度循环"（LM-001）。
        /// </para>
        /// </summary>
        internal void StartInstance(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            if (inst == null || tmpl == null) return;

            // ★ 同实例重复启动（快速 stop/start/restart）时，先彻底停掉旧循环：
            //   旧计时器被释放、旧代次作废，剩下唯一一条新循环。
            StopSchedule(inst.Id);

            var cts = new System.Threading.CancellationTokenSource();
            // ★ 提前取出 token：CTS 被释放后再访问 .Token 会抛 ObjectDisposedException
            var token = cts.Token;

            // ★ 代次：本次启动的代号。Stop/StopInstance/再次 StartInstance 都会让它递增。
            long generation = _generations.AddOrUpdate(inst.Id, 1, (k, v) => v + 1);

            lock (_scheduleLock)
            {
                _cts[inst.Id] = cts;
            }

            // 设定间隔 (单位：秒)
            int interval = GetEffectiveInterval(inst, tmpl);

            // 使用 Timer 统一调度：初始间隔设为极短(例如 50ms)以触发立即执行
            // 这样第一次执行的结果也能被捕获，从而触发失败重试逻辑
            IPluginTimer newTimer = CreateTimer(InitialTickDelayMs,
                timer => RunScheduleTickAsync(inst, tmpl, generation, interval, timer, token));

            lock (_scheduleLock)
            {
                _timers[inst.Id] = newTimer;
            }

            newTimer.Start();

            // ★★★ Inject Initial Loading State ★★★
            InitDefaultValues(inst, tmpl);
        }

        /// <summary>
        /// 调度心跳：执行插件，并根据结果调整下一次心跳的间隔。
        /// <para>
        /// ★ 不可见分支必须留在 <c>try</c> 内部 —— 这样它才会经过 <c>finally</c> 的统一收尾，
        /// 由 <c>RearmSchedule</c> **显式**重新武装计时器（LM-001 修复要求 1）。
        /// <para>
        /// ★ 根因更正（2026-10-22 复核）：基线的不可见分支在 <c>try</c> 之前直接 <c>return</c>，
        /// 从未显式重新武装；基线之所以还能继续轮询，靠的是
        /// <see cref="System.Timers.Timer"/> "在处理器内赋值 <c>Interval</c> 即重新武装"这一
        /// **未文档化副作用**（赋值发生在提前 <c>return</c> 之前）——
        /// <b>并非"调度永久停摆"</b>。修复的定位因此是
        /// "消除对未文档化副作用的依赖"，而非"恢复停摆的轮询"。
        /// </para>
        /// <para>
        /// ★ 所有退出路径（可见 / 不可见 / 失败 / 取消 / 停止）统一由 <c>finally</c> 收尾。
        /// </para>
        /// </summary>
        private async Task RunScheduleTickAsync(PluginInstanceConfig inst, PluginTemplate tmpl, long generation, int intervalSeconds, IPluginTimer timer, System.Threading.CancellationToken token)
        {
            try
            {
                // ★ 代次校验：Stop/Restart 之后，旧回调必须自我否决
                if (!IsCurrentGeneration(inst.Id, generation)) return;
                if (token.IsCancellationRequested) return;

                // [Optimization] On-Demand Update: Skip if not visible
                if (!ProbeVisible(inst.Id))
                {
                    timer.Interval = InvisibleRetryIntervalMs; // Check again in 2s
                    return; // ★ 走 finally → 统一收尾 → **显式**重新武装（不依赖赋值 Interval 的副作用）
                }

                // Execute and get success status
                bool success = await ProbeExecute(inst, tmpl, token);

                // ★ 再次校验代次：旧代次的执行结果不得写回（契约 C6）
                if (!IsCurrentGeneration(inst.Id, generation)) return;

                // 动态调整间隔：如果失败，使用快速重试间隔 (5s)；如果成功，恢复正常间隔
                if (!success)
                {
                    // 失败逻辑：线性退避 (5s, 10s, 15s... max 60s)
                    int fails = _consecutiveFailures.AddOrUpdate(inst.Id, 1, (k, v) => v + 1);
                    int backoff = Math.Min(fails * PluginConstants.RETRY_INTERVAL_MS, 60000);

                    // [Fix] 连续失败达到一定次数时，尝试重置网络客户端，以应对网络环境切换(如VPN/代理)导致的连接僵死
                    // [Optimization] Reduced threshold from 5 to 3 to recover faster from network changes
                    if (fails >= 3 && fails % 3 == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plugin {inst.Id} failed {fails} times. Resetting network clients and clearing cache.");
                        _executor.ResetNetworkClients();
                        _executor.ClearCache(inst.Id);
                        NetworkResetObserver?.Invoke(inst.Id);
                    }

                    timer.Interval = backoff;
                    System.Diagnostics.Debug.WriteLine($"Plugin {inst.Id} failed ({fails} times). Retry in {backoff}ms.");
                }
                else
                {
                    // 成功：重置失败计数
                    if (_consecutiveFailures.ContainsKey(inst.Id)) _consecutiveFailures.TryRemove(inst.Id, out _);
                    timer.Interval = intervalSeconds * 1000;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Timer execution failed: {ex.Message}");

                // ★ 代次校验：旧代次的异常不得写回失败计数（契约 C6）
                if (!IsCurrentGeneration(inst.Id, generation)) return;
                // ★ 取消不是失败：不应计入连续失败
                if (token.IsCancellationRequested) return;

                // 异常逻辑：同样应用退避
                int fails = _consecutiveFailures.AddOrUpdate(inst.Id, 1, (k, v) => v + 1);
                int backoff = Math.Min(fails * PluginConstants.RETRY_INTERVAL_MS, 60000);// 最大 60s
                timer.Interval = backoff;
            }
            finally
            {
                // ★★ 统一收尾（LM-001 修复要求 1）：所有退出路径都经过这里，
                //    由这里统一裁决是否重新武装计时器。
                RearmSchedule(inst, timer, generation, token);
            }
        }

        /// <summary>
        /// 统一收尾：决定是否重新武装计时器。
        /// <para>
        /// ★ 以下任一条件成立时都<b>不得</b>重新武装：
        /// 代次已变（实例被停止/重启）、令牌已取消（实例被停止）、
        /// 实例已被禁用、计时器已不是该实例当前的计时器（被更替）。
        /// </para>
        /// <para>
        /// ★ 这里的 <c>Start()</c> 是<b>显式</b>重新武装：调度存活**不依赖</b>
        /// <see cref="System.Timers.Timer"/> "在处理器内赋值 <c>Interval</c> 即重新武装"
        /// 这一未文档化副作用（LM-001 修复要求 1）。
        /// </para>
        /// </summary>
        private void RearmSchedule(PluginInstanceConfig inst, IPluginTimer timer, long generation, System.Threading.CancellationToken token)
        {
            try
            {
                // ★ 代次不匹配 → 旧调度不得复活
                if (!IsCurrentGeneration(inst.Id, generation)) return;
                if (token.IsCancellationRequested) return;
                if (!inst.Enabled) return;

                // ★ 计时器必须仍是该实例当前的计时器
                lock (_scheduleLock)
                {
                    if (!_timers.TryGetValue(inst.Id, out var current) || !ReferenceEquals(current, timer)) return;
                }

                timer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PluginManager] Failed to re-arm timer for {inst?.Id}: {ex.Message}");
            }
        }

        /// <summary>某实例的当前代次是否仍等于回调自称的代次。</summary>
        private bool IsCurrentGeneration(string instanceId, long generation)
        {
            return _generations.TryGetValue(instanceId, out var current) && current == generation;
        }

        /// <summary>可见性探针：注入时走注入实现，否则走真实实现。</summary>
        private bool ProbeVisible(string instanceId)
        {
            var probe = VisibilityProbe;
            return probe != null ? probe(instanceId) : IsPluginVisible(instanceId);
        }

        /// <summary>执行探针：注入时走注入实现，否则走真实执行器。</summary>
        private Task<bool> ProbeExecute(PluginInstanceConfig inst, PluginTemplate tmpl, System.Threading.CancellationToken token)
        {
            var probe = ExecutionProbe;
            return probe != null ? probe(inst, tmpl, token) : _executor.ExecuteInstanceAsync(inst, tmpl, token);
        }

        /// <summary>创建调度计时器：注入工厂时走注入实现，否则走生产计时器。</summary>
        private IPluginTimer CreateTimer(double intervalMs, Func<IPluginTimer, Task> onTick)
        {
            var factory = TimerFactory;
            return factory != null ? factory(intervalMs, onTick) : CreateProductionTimer(intervalMs, onTick);
        }

        /// <summary>
        /// 生产计时器：<see cref="System.Timers.Timer"/> 的 AutoReset=false（Stop-Wait）包装。
        /// <para>
        /// ★ <c>Elapsed</c> 是 <c>async void</c> 事件处理器：其中抛出的异常会逃逸到线程池，
        /// 在 .NET Core 上会直接终止进程。这里显式吞掉异常，保证调度异常不会变成未观察异常（T7）。
        /// </para>
        /// </summary>
        internal static IPluginTimer CreateProductionTimer(double intervalMs, Func<IPluginTimer, Task> onTick)
        {
            var wrapper = new SystemPluginTimer(intervalMs);
            wrapper.Inner.Elapsed += async (s, e) =>
            {
                try
                {
                    await onTick(wrapper).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PluginManager] Timer tick failed: {ex.Message}");
                }
            };
            return wrapper;
        }

        private void InitDefaultValues(PluginInstanceConfig inst, PluginTemplate tmpl)
        {
            if (tmpl.Outputs == null) return;
            
            // Logic must match PluginExecutor to generate correct keys
            int targetCount = (inst.Targets != null && inst.Targets.Count > 0) ? inst.Targets.Count : 1;
            bool hasTargets = (inst.Targets != null && inst.Targets.Count > 0);

            for (int i = 0; i < targetCount; i++)
            {
                string keySuffix = hasTargets ? $".{i}" : "";
                
                foreach (var output in tmpl.Outputs)
                {
                    string injectKey = inst.Id + keySuffix + "." + output.Key;
                    
                    // Only inject if currently empty to avoid flashing "..." on reload if data exists
                    if (string.IsNullOrEmpty(InfoService.Instance.GetValue(injectKey)))
                    {
                        InfoService.Instance.InjectValue(injectKey, PluginConstants.STATUS_LOADING);
                    }
                }
            }
        }

        // 委托给 Service 的 UI 辅助方法
        public string TryGetSmartLabel(string itemKey, string targetField = "label")
        {
            return PluginMonitorSyncService.Instance.TryGetSmartLabel(itemKey, _templates, targetField);
        }
    }
}
