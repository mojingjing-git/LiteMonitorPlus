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

        private List<PluginTemplate> _templates = new();
        private readonly Dictionary<string, IPluginTimer> _timers = new();
        private readonly Dictionary<string, Runtime> _runtimes = new();
        private readonly Dictionary<string, string> _configSnapshots = new();
        private readonly Dictionary<string, HashSet<string>> _ownedDataKeys = new();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();
        private readonly PluginExecutor _executor;

        // Lock order: lifecycle -> publication (_scheduleLock) -> executor HTTP/cache,
        // InfoService and MonitorItems. Ticks take only the publication gate. No path
        // takes lifecycle from a tick/commit. Cancel, Dispose, timer factories, settings
        // saves and external observers run after BOTH manager locks have been released.
        private readonly object _lifecycleLock = new();
        private readonly object _scheduleLock = new();
        private bool _running;

        private sealed class Runtime
        {
            internal PluginInstanceConfig Source;
            internal PluginInstanceConfig Instance;
            internal PluginTemplate Template;
            internal PluginPublicationLease Lease;
            internal System.Threading.CancellationTokenSource Cancellation;
            internal System.Threading.CancellationToken Token;
            internal IPluginTimer Timer;
            internal int IntervalSeconds;
            internal HashSet<string> DataKeys;
        }

        internal const int InitialTickDelayMs = 50;
        internal int InvisibleRetryIntervalMs { get; set; } = 2000;
        internal Func<string, bool> VisibilityProbe { get; set; }
        internal Func<PluginInstanceConfig, PluginTemplate, System.Threading.CancellationToken, Task<bool>> ExecutionProbe { get; set; }
        internal Func<double, Func<IPluginTimer, Task>, IPluginTimer> TimerFactory { get; set; }
        internal Action<string> NetworkResetObserver { get; set; }
        internal Func<string, Task> BeforeScheduleCommitAsync { get; set; }

        internal PluginManager(PluginExecutor executor = null)
        {
            _executor = executor ?? new PluginExecutor();
        }

        internal int GetConsecutiveFailures(string instanceId)
            => _consecutiveFailures.TryGetValue(instanceId, out var count) ? count : 0;
        internal PluginExecutor ExecutorInternal => _executor;
        internal int RegisteredScheduleCount { get { lock (_scheduleLock) return _timers.Count; } }
        internal int GetRegisteredScheduleCount(string instanceId)
        {
            lock (_scheduleLock) return _timers.ContainsKey(instanceId) ? 1 : 0;
        }

        public void RecoverMarketTransactions(string pluginsDir)
        {
            try { PluginMarket.RecoverInterruptedInstallsIn(pluginsDir); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PluginManager] Market recovery failed: " + ex.Message);
            }
        }

        public void LoadPlugins(string directoryPath)
        {
            RecoverMarketTransactions(directoryPath);
            var loaded = new List<PluginTemplate>();
            if (!Directory.Exists(directoryPath))
            {
                try { Directory.CreateDirectory(directoryPath); } catch { }
            }
            if (Directory.Exists(directoryPath))
            {
                foreach (string file in Directory.GetFiles(directoryPath, PluginConstants.CONFIG_EXT)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var template = JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(file));
                        if (template == null || string.IsNullOrEmpty(template.Id) ||
                            loaded.Any(t => t.Id == template.Id)) continue;
                        loaded.Add(template);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load plugin {file}: {ex.Message}");
                    }
                }
            }

            var retired = new List<Runtime>();
            var added = new List<Runtime>();
            var settings = Settings.Load();
            bool changed = false;
            lock (_lifecycleLock)
            {
                // Publish the complete template set, never a partially loaded collection.
                lock (_scheduleLock) _templates = loaded;
                foreach (var instance in settings.PluginInstances.ToList())
                {
                    if (loaded.Any(t => t.Id == instance.TemplateId)) continue;
                    lock (_scheduleLock)
                    {
                        RetireLocked(instance.Id, retired, cleanup: true);
                        instance.KeyCache.Clear();
                        PluginMonitorSyncService.Instance.RemoveMonitorItems(instance.Id, saveIfChanged: false);
                    }
                    settings.PluginInstances.Remove(instance);
                    changed = true;
                }
                foreach (var template in loaded)
                {
                    if (settings.PluginInstances.Any(i => i.TemplateId == template.Id)) continue;
                    string id = template.Id;
                    if (settings.PluginInstances.Any(i => i.Id == id)) id = Guid.NewGuid().ToString("N").Substring(0, 8);
                    var instance = new PluginInstanceConfig { Id = id, TemplateId = template.Id, Enabled = false };
                    foreach (var input in template.Inputs ?? new List<PluginInput>())
                        instance.InputValues[input.Key] = input.DefaultValue;
                    settings.PluginInstances.Add(instance);
                    changed = true;
                }
                changed |= ReconcileLocked(settings, retired, added);
            }
            FinishChanges(retired, added);
            if (changed) settings.Save();
        }

        public List<PluginTemplate> GetAllTemplates()
        {
            lock (_scheduleLock) return _templates.ToList();
        }

        // Call only with the publication gate held. Dotted instance ids cannot be
        // separated by a prefix; remove only the complete keys this instance owned.
        private void CleanupPluginData(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            if (_ownedDataKeys.Remove(instanceId, out var keys)) InfoService.Instance.RemoveDataByKeys(keys);
        }

        public void Reload(Settings cfg)
        {
            var retired = new List<Runtime>();
            var added = new List<Runtime>();
            lock (_lifecycleLock) ReconcileLocked(cfg, retired, added);
            FinishChanges(retired, added);
            Settings.Load().Save();
        }

        // Lifecycle lock held, payload snapshots/fingerprints prepared outside the
        // publication gate. New runtimes are reserved before timer creation begins.
        private bool ReconcileLocked(Settings cfg, List<Runtime> retired, List<Runtime> added)
        {
            bool changed = false;
            var enabled = cfg.PluginInstances.Where(i => i.Enabled)
                .Where(i => _templates.Any(t => t.Id == i.TemplateId)).ToDictionary(i => i.Id);
            lock (_scheduleLock)
            {
                foreach (var id in _runtimes.Keys.ToList())
                    if (!_running || !enabled.ContainsKey(id)) RetireLocked(id, retired, cleanup: true);
            }
            if (!_running) return changed;

            foreach (var source in enabled.Values)
            {
                var template = _templates.First(t => t.Id == source.TemplateId);
                string fingerprint = PluginRuntimeSnapshot.Fingerprint(source, template);
                lock (_scheduleLock)
                {
                    if (_runtimes.ContainsKey(source.Id) &&
                        _configSnapshots.TryGetValue(source.Id, out var previous) && previous == fingerprint)
                        continue;
                }
                var runtime = PrepareRuntime(source, template);
                lock (_scheduleLock)
                {
                    RetireLocked(source.Id, retired, cleanup: true);
                    changed |= PluginMonitorSyncService.Instance.SyncMonitorItem(source, template, saveIfChanged: false);
                    ReserveLocked(runtime, fingerprint);
                }
                added.Add(runtime);
            }
            return changed;
        }

        private Runtime PrepareRuntime(PluginInstanceConfig source, PluginTemplate template)
        {
            var instanceSnapshot = PluginRuntimeSnapshot.Copy(source);
            var templateSnapshot = PluginRuntimeSnapshot.Copy(template);
            var cancellation = new System.Threading.CancellationTokenSource();
            return new Runtime
            {
                Source = source,
                Instance = instanceSnapshot,
                Template = templateSnapshot,
                Cancellation = cancellation,
                Token = cancellation.Token,
                Lease = new PluginPublicationLease(_scheduleLock, cancellation.Token),
                IntervalSeconds = GetEffectiveInterval(instanceSnapshot, templateSnapshot),
                DataKeys = PluginKeyOwnership.PublicDataKeys(instanceSnapshot, templateSnapshot)
            };
        }

        private void ReserveLocked(Runtime runtime, string fingerprint)
        {
            _runtimes[runtime.Instance.Id] = runtime;
            _configSnapshots[runtime.Instance.Id] = fingerprint;
            if (!_ownedDataKeys.TryGetValue(runtime.Instance.Id, out var keys))
                _ownedDataKeys[runtime.Instance.Id] = keys = new HashSet<string>(StringComparer.Ordinal);
            keys.UnionWith(runtime.DataKeys);
        }

        private void RetireLocked(string instanceId, List<Runtime> retired, bool cleanup)
        {
            if (_runtimes.Remove(instanceId, out var runtime))
            {
                runtime.Lease.Retire();
                runtime.Instance.KeyCache.Clear();
                runtime.Source.KeyCache.Clear();
                retired.Add(runtime);
            }
            _timers.Remove(instanceId);
            _configSnapshots.Remove(instanceId);
            _consecutiveFailures.TryRemove(instanceId, out _);
            _executor.ClearCache(instanceId);
            if (cleanup) CleanupPluginData(instanceId);
        }

        private void FinishChanges(List<Runtime> retired, List<Runtime> added)
        {
            // Cancellation can synchronously call arbitrary user code. It is never
            // under lifecycle/publication locks and only touches detached resources.
            foreach (var runtime in retired) ReleaseRuntime(runtime);
            foreach (var runtime in added) ArmRuntime(runtime);
        }

        private static void ReleaseRuntime(Runtime runtime)
        {
            try { runtime.Timer?.Stop(); } catch { }
            try { runtime.Timer?.Dispose(); } catch { }
            try { runtime.Cancellation.Cancel(); } catch { }
            try { runtime.Cancellation.Dispose(); } catch { }
        }

        private void ArmRuntime(Runtime runtime)
        {
            IPluginTimer timer = null;
            bool installed = false;
            try
            {
                timer = CreateTimer(InitialTickDelayMs, t => RunScheduleTickAsync(runtime, t));
                installed = runtime.Lease.TryCommit(() =>
                {
                    runtime.Timer = timer;
                    _timers[runtime.Instance.Id] = timer;
                    InitDefaultValues(runtime.Instance, runtime.Template);
                    timer.Start();
                });
            }
            catch
            {
                var retired = new List<Runtime>();
                lock (_scheduleLock)
                {
                    if (_runtimes.TryGetValue(runtime.Instance.Id, out var current) && ReferenceEquals(current, runtime))
                        RetireLocked(runtime.Instance.Id, retired, cleanup: false);
                }
                FinishChanges(retired, new List<Runtime>());
                throw;
            }
            finally
            {
                if (!installed)
                {
                    try { timer?.Stop(); } catch { }
                    try { timer?.Dispose(); } catch { }
                }
            }
        }

        internal void StopInstance(string instanceId)
        {
            var retired = new List<Runtime>();
            lock (_lifecycleLock)
            {
                lock (_scheduleLock) RetireLocked(instanceId, retired, cleanup: false);
            }
            FinishChanges(retired, new List<Runtime>());
        }

        public void Start()
        {
            var retired = new List<Runtime>();
            var added = new List<Runtime>();
            var settings = Settings.Load();
            bool changed;
            lock (_lifecycleLock)
            {
                lock (_scheduleLock)
                {
                    foreach (var id in _runtimes.Keys.ToList()) RetireLocked(id, retired, cleanup: false);
                    _running = true;
                }
                changed = ReconcileLocked(settings, retired, added);
            }
            FinishChanges(retired, added);
            if (changed) settings.Save();
        }

        public void RestartInstance(string instanceId, PluginInstanceConfig configOverride = null)
        {
            var retired = new List<Runtime>();
            var added = new List<Runtime>();
            lock (_lifecycleLock)
            {
                lock (_scheduleLock) RetireLocked(instanceId, retired, cleanup: true);
                var instance = configOverride ?? Settings.Load().PluginInstances.FirstOrDefault(i => i.Id == instanceId);
                var template = _templates.FirstOrDefault(t => t.Id == instance?.TemplateId);
                if (instance == null || !instance.Enabled || template == null)
                {
                    lock (_scheduleLock)
                        PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId, saveIfChanged: false);
                }
                else if (_running)
                {
                    var runtime = PrepareRuntime(instance, template);
                    string fingerprint = PluginRuntimeSnapshot.Fingerprint(instance, template);
                    lock (_scheduleLock)
                    {
                        PluginMonitorSyncService.Instance.SyncMonitorItem(instance, template, saveIfChanged: false);
                        ReserveLocked(runtime, fingerprint);
                    }
                    added.Add(runtime);
                }
            }
            FinishChanges(retired, added);
            Settings.Load().Save();
        }

        public void RemoveInstance(string instanceId)
        {
            var retired = new List<Runtime>();
            lock (_lifecycleLock)
            {
                lock (_scheduleLock)
                {
                    RetireLocked(instanceId, retired, cleanup: true);
                    PluginMonitorSyncService.Instance.RemoveMonitorItems(instanceId, saveIfChanged: false);
                }
            }
            FinishChanges(retired, new List<Runtime>());
            Settings.Load().Save();
        }

        public void Stop()
        {
            var retired = new List<Runtime>();
            lock (_lifecycleLock)
            {
                lock (_scheduleLock)
                {
                    _running = false;
                    foreach (var id in _runtimes.Keys.ToList()) RetireLocked(id, retired, cleanup: false);
                }
            }
            FinishChanges(retired, new List<Runtime>());
        }

        internal void StartInstance(PluginInstanceConfig instance, PluginTemplate template)
        {
            if (instance == null || template == null) return;
            var retired = new List<Runtime>();
            Runtime runtime;
            lock (_lifecycleLock)
            {
                runtime = PrepareRuntime(instance, template);
                string fingerprint = PluginRuntimeSnapshot.Fingerprint(instance, template);
                lock (_scheduleLock)
                {
                    RetireLocked(instance.Id, retired, cleanup: false);
                    ReserveLocked(runtime, fingerprint);
                }
            }
            FinishChanges(retired, new List<Runtime> { runtime });
        }

        private bool IsPluginVisible(string instanceId)
        {
            var settings = Settings.Load();
            if (settings?.MonitorItems == null) return false;
            if (LiteWebServer.Instance != null && LiteWebServer.Instance.IsRunning) return true;
            lock (settings.MonitorItems)
            {
                var keys = PluginKeyOwnership.MonitorKeys(settings, instanceId);
                return settings.MonitorItems.Any(item => keys.Contains(item.Key) &&
                    (item.VisibleInPanel || item.VisibleInTaskbar));
            }
        }

        public static int GetMinimumInterval(PluginTemplate template)
            => Math.Max(PluginConstants.DEFAULT_INTERVAL, template?.Execution?.MinInterval ?? 0);

        public static int GetEffectiveInterval(PluginInstanceConfig instance, PluginTemplate template)
            => Math.Max(instance.CustomInterval > 0 ? instance.CustomInterval : template.Execution.Interval,
                GetMinimumInterval(template));

        private async Task RunScheduleTickAsync(Runtime runtime, IPluginTimer timer)
        {
            bool resetCommitted = false;
            try
            {
                if (!runtime.Lease.IsCurrent || !runtime.Source.Enabled) return;
                if (!(VisibilityProbe?.Invoke(runtime.Instance.Id) ?? IsPluginVisible(runtime.Instance.Id)))
                {
                    runtime.Lease.TryCommit(() => timer.Interval = InvisibleRetryIntervalMs);
                    return;
                }
                if (!runtime.Lease.IsCurrent) return;
                var probe = ExecutionProbe;
                bool success = probe != null
                    ? await probe(runtime.Instance, runtime.Template, runtime.Token).ConfigureAwait(false)
                    : await _executor.ExecuteLeasedAsync(runtime.Instance, runtime.Template, runtime.Token, runtime.Lease).ConfigureAwait(false);
                var barrier = BeforeScheduleCommitAsync;
                if (barrier != null) await barrier(runtime.Instance.Id).ConfigureAwait(false);
                runtime.Lease.TryCommit(() =>
                {
                    if (success)
                    {
                        _consecutiveFailures.TryRemove(runtime.Instance.Id, out _);
                        timer.Interval = runtime.IntervalSeconds * 1000;
                    }
                    else
                    {
                        int failures = _consecutiveFailures.AddOrUpdate(runtime.Instance.Id, 1, (_, n) => n + 1);
                        timer.Interval = Math.Min(failures * PluginConstants.RETRY_INTERVAL_MS, 60000);
                        if (failures >= 3 && failures % 3 == 0)
                        {
                            // Controlled client reference swaps, not network I/O. Delayed
                            // disposal owns only the detached old clients. No observer here.
                            _executor.ResetNetworkClients();
                            _executor.ClearCache(runtime.Instance.Id);
                            resetCommitted = true;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Plugin timer execution failed: " + ex.Message);
                runtime.Lease.TryCommit(() =>
                {
                    int failures = _consecutiveFailures.AddOrUpdate(runtime.Instance.Id, 1, (_, n) => n + 1);
                    timer.Interval = Math.Min(failures * PluginConstants.RETRY_INTERVAL_MS, 60000);
                });
            }
            finally
            {
                runtime.Lease.TryCommit(() =>
                {
                    if (runtime.Source.Enabled && _timers.TryGetValue(runtime.Instance.Id, out var current) &&
                        ReferenceEquals(current, timer)) timer.Start();
                });
            }
            // Audit of a reset already committed before any subsequent retirement.
            if (resetCommitted) NetworkResetObserver?.Invoke(runtime.Instance.Id);
        }

        private IPluginTimer CreateTimer(double intervalMs, Func<IPluginTimer, Task> onTick)
            => TimerFactory?.Invoke(intervalMs, onTick) ?? CreateProductionTimer(intervalMs, onTick);

        internal static IPluginTimer CreateProductionTimer(double intervalMs, Func<IPluginTimer, Task> onTick)
        {
            var wrapper = new SystemPluginTimer(intervalMs);
            wrapper.Inner.Elapsed += async (_, _) =>
            {
                try { await onTick(wrapper).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[PluginManager] Timer tick failed: " + ex.Message);
                }
            };
            return wrapper;
        }

        private void InitDefaultValues(PluginInstanceConfig instance, PluginTemplate template)
        {
            foreach (var binding in PluginKeyOwnership.Outputs(instance, template))
            {
                string key = binding.RawKey;
                if (string.IsNullOrEmpty(InfoService.Instance.GetValue(key)))
                    InfoService.Instance.InjectValue(key, PluginConstants.STATUS_LOADING);
            }
        }

        public string TryGetSmartLabel(string itemKey, string targetField = "label")
            => PluginMonitorSyncService.Instance.TryGetSmartLabel(itemKey, GetAllTemplates(), targetField);
    }
}
