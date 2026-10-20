using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;

namespace LiteMonitor.PerformanceProbe;

/// <summary>§6.2 实机性能组定义。</summary>
internal sealed record GroupSpec(
    string Id,
    string Purpose,
    int RefreshMs,
    bool WebEnabled,
    bool GpuItemsEnabled,
    int MonitoredGpuCount,
    int WebSocketClients,
    double HttpPollPerSecond,
    string Note = "");

/// <summary>
/// 探针复算"到期轮数"时使用的**当前生产实现的取模语义**。
///
/// ⚠ 时效性警告：这些常量复制自生产代码
///   - HardwareMonitor.UpdateAll()：`isSlowScanTick = _secondsCounter % 3 == 0`、
///     `needDiskBgScan = _secondsCounter % 10 == 0`
///   - SystemOptimizer.RunMaintenanceTasks()：`% 60 == 5`（流量保存）、
///     `% 180 == 30`（GC 请求）、`% 300 == 45`（工作集裁剪）
/// 计划 §4.3 要把它们改成**单调截止时间**。一旦生产改完，这里的判定就与生产脱节，
/// 探针仍会按旧语义报出一个"形状几乎一样"的假指标，而且**不会报错**。
///
/// 缓解措施：探针把「按取模算出的到期轮数」与「实际观测到的文件重写次数」交叉验证，
/// 两者明显不一致时把 `trafficRewriteAgreesWithDueRounds` 置为 false。
/// Task 2/Task 3 落地后，必须把到期轮数改成**直接观测实际调用**，不再复算。
/// </summary>
internal static class MaintenanceDueSemantics
{
    public const int SlowScanModulo = 3;
    public const int DiskBackgroundScanModulo = 10;
    public const int TrafficSaveModulo = 60;
    public const int TrafficSaveOffset = 5;
    public const int GcRequestModulo = 180;
    public const int GcRequestOffset = 30;
    public const int WorkingSetModulo = 300;
    public const int WorkingSetOffset = 45;

    public const string Warning =
        "到期轮数按【当前生产实现的整数秒取模语义】复算（%3/%10/%60==5/%180==30/%300==45）。" +
        "计划 §4.3 把生产改成单调截止时间之后，本列会与生产脱节而不会报错，" +
        "Task 2/3 必须改为直接观测实际调用。";

    public static string Describe => $"slow%{SlowScanModulo},diskBg%{DiskBackgroundScanModulo}," +
        $"traffic%{TrafficSaveModulo}=={TrafficSaveOffset},gc%{GcRequestModulo}=={GcRequestOffset}," +
        $"workingSet%{WorkingSetModulo}=={WorkingSetOffset}";
}

/// <summary>
/// 采集循环测量器。
///
/// 采集循环刻意模拟 UIController.Tick() 的真实节奏：
///   ① HardwareMonitor.UpdateAll()   （= 一次真实硬件采集轮）
///   ② 依次读取当前启用的监控项      （= 界面/Web 的取值）
/// 然后按 UI 周期等待下一轮。等待使用「周期网格上的下一个刻度」，
/// 与 WinForms 定时器在忙碌时丢帧的行为一致（不会补跑错过的轮次）。
///
/// 计数全部是低开销的内存计数：默认不逐帧写日志。
/// </summary>
internal sealed class SamplingRunner
{
    private readonly ProbeEnvironment _env;
    private readonly FieldInfo _secondsCounterField;
    private readonly string _trafficFile;

    public SamplingRunner(ProbeEnvironment env)
    {
        _env = env;
        _secondsCounterField = typeof(LiteMonitor.src.SystemServices.HardwareMonitor)
            .GetField("_secondsCounter", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("无法读取 HardwareMonitor._secondsCounter，采集轮次分类证据不可用。");
        _trafficFile = Path.Combine(AppContext.BaseDirectory, "TrafficHistory.json");
    }

    /// <summary>本轮实际读取的监控项 Key（界面可见项的并集，跳过 DASH.*）。</summary>
    private List<string> VisibleKeys()
    {
        var keys = new List<string>();
        foreach (var item in _env.Cfg.MonitorItems)
        {
            if (!item.VisibleInPanel && !item.VisibleInTaskbar) continue;
            if (item.Key.StartsWith("DASH.", StringComparison.Ordinal)) continue;
            keys.Add(item.Key);
        }
        return keys;
    }

    public JsonObject RunGroup(GroupSpec spec, int warmupSeconds, int steadySeconds)
    {
        var record = new JsonObject
        {
            ["kind"] = "sampling_group",
            ["group"] = spec.Id,
            ["purpose"] = spec.Purpose,
            ["uiPeriodMs"] = spec.RefreshMs,
            ["webEnabled"] = spec.WebEnabled,
            ["gpuItemsEnabled"] = spec.GpuItemsEnabled,
            ["monitoredGpuCount"] = spec.MonitoredGpuCount,
            ["webSocketClients"] = spec.WebSocketClients,
            ["httpPollPerSecond"] = spec.HttpPollPerSecond,
            ["warmupSeconds"] = warmupSeconds,
            ["steadySeconds"] = steadySeconds,
        };
        if (spec.Note.Length > 0) record["note"] = spec.Note;

        var gpuVisibilityBackup = ApplyGroupConfig(spec);

        WebActivity? web = null;
        try
        {
            if (spec.WebEnabled)
            {
                var handle = _env.StartWeb();
                if (handle == null)
                {
                    record["webStatus"] = "Web 服务启动失败，本组未采集（见 metadata.readyNote）。";
                    return record;
                }
                web = new WebActivity(handle.Port, _env.Cfg.WebServerPassword);
                int opened = web.OpenWebSockets(spec.WebSocketClients);
                record["webSocketOpened"] = opened;
                if (spec.HttpPollPerSecond > 0) web.StartHttpPolling(spec.HttpPollPerSecond);
            }

            var keys = VisibleKeys();
            record["readKeyCount"] = keys.Count;

            // 预热：让缓存、驱动、频率都进入稳态；预热期间的计数全部丢弃。
            RunLoop(keys, spec.RefreshMs, warmupSeconds, collect: false);

            _env.ResetCounters();
            var steady = RunLoop(keys, spec.RefreshMs, steadySeconds, collect: true);

            // ★ 计数有效性守卫：硬件重载（HardwareMonitor.ReloadComputerSafe）会重建分组集合，
            //   把注入的计数代理丢掉。之后 deviceUpdateCounters 会变成 0 或残缺，
            //   而 JSON 看起来仍是一份正常基线 —— 所以这里必须复检并把结果写进记录。
            bool countersValid = _env.CountersStillInstalled();
            steady["countersStillInstalled"] = countersValid;
            steady["deviceUpdateCountersTrusted"] = countersValid;
            steady["deviceUpdateCounters"] = _env.SnapshotDeviceCounters((int)(steady["collectionRounds"]?.GetValue<long>() ?? 0));
            steady["deviceUpdateCountersMeaning"] =
                "LHM 注入代理看到的每设备 Update 次数（只覆盖可注入分组：显卡/内存）；" +
                "权威的每设备次数是 productionDeviceUpdates（生产侧计数，含 CPU/网卡/磁盘/主板）。";
            if (!countersValid)
            {
                steady["deviceUpdateCountersWarning"] =
                    "本组结束时计数代理已不在硬件分组里（很可能期间发生过硬件重载/热插拔），" +
                    "deviceUpdateCounters 不可信，不能当作每卡 Update 次数的证据。";
            }
            if (web != null) steady["webActivity"] = web.ToJson();

            // 取值链路自检：确认关键指标真的读到了数（不是恒 0 / 不是 null）
            var sample = new System.Text.Json.Nodes.JsonObject();
            foreach (var key in new[] { "CPU.Load", "CPU.Temp", "MEM.Load", "NET.Up", "NET.Down", "GPU.0.Temp", "GPU.0.Load" })
            {
                if (!keys.Contains(key)) continue;
                float? v = _env.Monitor.Get(key);
                sample[key] = v.HasValue ? Math.Round(v.Value, 2) : "null";
            }
            steady["sampleMetricValues"] = sample;
            steady["note"] = "valueReads 只统计探针自身的读取；Web 快照与广播的读取发生在应用进程内，未计入。"
                            + " 不包含界面绘制、InfoService/Dashboard、历史记录落盘。";

            foreach (var kv in steady) record[kv.Key] = kv.Value?.DeepClone();
        }
        finally
        {
            web?.Dispose();
            RestoreGpuVisibility(gpuVisibilityBackup);
            _env.StopWeb();
        }

        return record;
    }

    private sealed class GpuVisibilityBackup
    {
        public Dictionary<string, (bool Panel, bool Taskbar)> Items { get; } = new();
    }

    private GpuVisibilityBackup ApplyGroupConfig(GroupSpec spec)
    {
        _env.Cfg.RefreshMs = spec.RefreshMs;
        _env.Cfg.WebServerEnabled = spec.WebEnabled;
        _env.Cfg.MonitoredGpuCount = spec.MonitoredGpuCount;
        // 让监控项与被监控卡数保持一致（主程序 UIController.Tick 做的同一件事）
        LiteMonitor.src.Core.GpuItemSync.Ensure(_env.Cfg, spec.MonitoredGpuCount);

        // 备份并应用 GPU 项的显隐
        var backup = new GpuVisibilityBackup();
        foreach (var item in _env.Cfg.MonitorItems)
        {
            if (!item.Key.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)) continue;
            backup.Items[item.Key] = (item.VisibleInPanel, item.VisibleInTaskbar);
            bool enable = spec.GpuItemsEnabled;
            item.VisibleInPanel = enable && backup.Items[item.Key].Panel;
            item.VisibleInTaskbar = enable && backup.Items[item.Key].Taskbar;
        }
        return backup;
    }

    private void RestoreGpuVisibility(GpuVisibilityBackup backup)
    {
        foreach (var item in _env.Cfg.MonitorItems)
        {
            if (!item.Key.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)) continue;
            if (backup.Items.TryGetValue(item.Key, out var v))
            {
                item.VisibleInPanel = v.Panel;
                item.VisibleInTaskbar = v.Taskbar;
            }
            else
            {
                // 分组期间新增的显卡项（例如多卡组的第二张卡）恢复为隐藏
                item.VisibleInPanel = false;
                item.VisibleInTaskbar = false;
            }
        }
    }

    /// <summary>
    /// 跑一轮采集循环。collect=false 时只跑不记（预热）。
    /// </summary>
    private JsonObject RunLoop(List<string> keys, int periodMs, int seconds, bool collect)
    {
        var sw = Stopwatch.StartNew();
        long durationMs = (long)seconds * 1000;

        // 生产侧统计（公开 API）：采集轮数、每设备 Update 次数、维护触发次数都直接读生产计数，
        // 不再由探针按取模语义复算 —— 复算值只留作交叉验证。
        var stats = _env.Monitor.SamplingStatistics;
        long prodRoundsBefore = stats.CollectionRounds;
        long prodGenBefore = stats.Generation;
        long prodSkippedBefore = stats.SkippedRequests;
        long prodSharedBefore = stats.SharedRequests;
        long prodRejectedBefore = stats.RejectedRequests;
        long prodForcedBefore = stats.ForcedCollections;
        long prodFailedBefore = stats.FailedCollections;
        long prodValueCalcBefore = stats.ValueComputations;
        long prodSlowScanBefore = stats.SlowScanTriggers;
        long prodDiskBgBefore = stats.DiskBackgroundScanTriggers;
        long prodTrafficBefore = stats.TrafficSaveTriggers;
        long prodGcBefore = stats.GcRequestTriggers;
        long prodWorkingSetBefore = stats.WorkingSetTrimTriggers;
        double prodTotalDurationBefore = stats.TotalCollectionDurationMs;
        var prodDevicesBefore = stats.SnapshotDeviceUpdates();
        var prodCategoriesBefore = stats.SnapshotCategoryTotals();

        long rounds = 0, reads = 0;
        long lastObservedRounds = stats.CollectionRounds;
        long slowScanRounds = 0, diskBgScanRounds = 0, trafficSaveDueRounds = 0, gcRequestDueRounds = 0, workingSetDueRounds = 0;
        long trafficFileRewrites = 0;
        var updateDurations = new List<double>(capacity: 4096);
        var readDurations = new List<double>(capacity: 4096);
        var perSecond = new JsonArray();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long lastSampleMs = 0;
        double cpuStartMs = process.TotalProcessorTime.TotalMilliseconds;
        double lastSampleCpuMs = cpuStartMs;
        long allocStart = GC.GetTotalAllocatedBytes(false);
        long lastSampleAllocated = allocStart;
        DateTime lastTrafficWrite = SafeLastWriteUtc(_trafficFile);

        long nextDueMs = 0;
        while (sw.ElapsedMilliseconds < durationMs)
        {
            // 等待到周期网格上的下一个刻度（WinForms 定时器忙碌时丢帧，不补跑）
            long now = sw.ElapsedMilliseconds;
            if (nextDueMs <= now) nextDueMs = ((now / periodMs) + 1) * periodMs;
            long wait = nextDueMs - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);

            long t0 = sw.ElapsedMilliseconds;
            _env.Monitor.UpdateAll();
            long t1 = sw.ElapsedMilliseconds;
            foreach (var key in keys) _env.Monitor.Get(key);
            long t2 = sw.ElapsedMilliseconds;

            rounds++;
            reads += keys.Count;
            updateDurations.Add(t1 - t0);
            readDurations.Add(t2 - t1);

            // 采集代次/慢扫描/维护到期分类：读取应用自己的秒计数器。
            // ⚠ 判定语义见 MaintenanceDueSemantics：这是按**当前实现的取模语义**复算的，
            //   计划 §4.3 改成单调截止时间后必须换成直接观测。
            // ★ 只在**本次请求真的产生了一次采集**时复算：生产侧现在有采集门限，
            //   未到期的请求根本不会采集，也就不会推进秒计数器/触发维护。
            //   若按请求复算，会比生产计数多出"被跳过的请求"那一部分，无法交叉验证。
            long roundsNow = stats.CollectionRounds;
            if (roundsNow != lastObservedRounds)
            {
                lastObservedRounds = roundsNow;
                long secondsCounter = (long)_secondsCounterField.GetValue(_env.Monitor)!;
                if (secondsCounter % MaintenanceDueSemantics.SlowScanModulo == 0) slowScanRounds++;
                if (secondsCounter % MaintenanceDueSemantics.DiskBackgroundScanModulo == 0) diskBgScanRounds++;
                if (secondsCounter % MaintenanceDueSemantics.TrafficSaveModulo == MaintenanceDueSemantics.TrafficSaveOffset) trafficSaveDueRounds++;
                if (secondsCounter % MaintenanceDueSemantics.GcRequestModulo == MaintenanceDueSemantics.GcRequestOffset) gcRequestDueRounds++;
                if (secondsCounter % MaintenanceDueSemantics.WorkingSetModulo == MaintenanceDueSemantics.WorkingSetOffset) workingSetDueRounds++;
            }

            // 流量文件真实重写次数
            DateTime trafficWrite = SafeLastWriteUtc(_trafficFile);
            if (trafficWrite != lastTrafficWrite) { trafficFileRewrites++; lastTrafficWrite = trafficWrite; }

            // 每秒采样一次进程 CPU 时间与分配量
            long elapsed = sw.ElapsedMilliseconds;
            if (elapsed - lastSampleMs >= 1000)
            {
                process.Refresh();
                double cpuMs = process.TotalProcessorTime.TotalMilliseconds;
                long allocated = GC.GetTotalAllocatedBytes(false);
                perSecond.Add(new JsonObject
                {
                    ["tMs"] = elapsed,
                    ["cpuMs"] = Math.Round(cpuMs - lastSampleCpuMs, 3),
                    ["allocatedBytes"] = allocated - lastSampleAllocated,
                });
                lastSampleMs = elapsed;
                lastSampleCpuMs = cpuMs;
                lastSampleAllocated = allocated;
            }
        }

        long elapsedTotal = sw.ElapsedMilliseconds;
        process.Refresh();
        double cpuTotalMs = process.TotalProcessorTime.TotalMilliseconds - cpuStartMs;
        long allocatedTotal = GC.GetTotalAllocatedBytes(false) - allocStart;

        // 生产侧实际发生的采集轮数（请求次数 ≠ 采集轮数：未到期会被门限跳过）
        long actualRounds = stats.CollectionRounds - prodRoundsBefore;

        var result = new JsonObject
        {
            ["elapsedMs"] = elapsedTotal,
            ["collectionRounds"] = actualRounds,
            ["collectionRoundsMeaning"] = "生产侧实际发生的真实采集轮数（HardwareSamplingStatistics.CollectionRounds 增量）",
            ["collectionRequests"] = rounds,
            ["collectionRequestsMeaning"] = "探针向应用发起的采集请求次数；未到期的请求会被门限跳过，不产生采集",
            ["collectionRoundsPerSecond"] = elapsedTotal > 0 ? Math.Round(actualRounds * 1000.0 / elapsedTotal, 3) : 0,
            ["collectionRequestsPerSecond"] = elapsedTotal > 0 ? Math.Round(rounds * 1000.0 / elapsedTotal, 3) : 0,
            ["valueReads"] = reads,
            ["valueReadsPerRound"] = rounds > 0 ? Math.Round((double)reads / rounds, 3) : 0,
            ["collectionGenerations"] = stats.Generation - prodGenBefore,
            ["skippedRequests"] = stats.SkippedRequests - prodSkippedBefore,
            ["sharedRequests"] = stats.SharedRequests - prodSharedBefore,
            ["rejectedRequests"] = stats.RejectedRequests - prodRejectedBefore,
            ["forcedCollections"] = stats.ForcedCollections - prodForcedBefore,
            ["failedCollections"] = stats.FailedCollections - prodFailedBefore,
            ["valueComputations"] = stats.ValueComputations - prodValueCalcBefore,
            ["productionCollectionDurationMs"] = Math.Round(stats.TotalCollectionDurationMs - prodTotalDurationBefore, 3),
            ["slowScanTriggerRounds"] = stats.SlowScanTriggers - prodSlowScanBefore,
            ["diskBackgroundScanTriggerRounds"] = stats.DiskBackgroundScanTriggers - prodDiskBgBefore,
            ["trafficSaveDueRounds"] = stats.TrafficSaveTriggers - prodTrafficBefore,
            ["gcRequestDueRounds"] = stats.GcRequestTriggers - prodGcBefore,
            ["workingSetTrimDueRounds"] = stats.WorkingSetTrimTriggers - prodWorkingSetBefore,
            ["productionDeviceUpdates"] = DeltaCounters(prodDevicesBefore, stats.SnapshotDeviceUpdates()),
            ["productionCategoryUpdates"] = DeltaCounters(prodCategoriesBefore, stats.SnapshotCategoryTotals()),
            ["trafficFileObservedRewrites"] = trafficFileRewrites,
            ["trafficRewriteNote"] = "这是**轮询文件 LastWriteTime 变化**得到的下界：" +
                "两次轮询之间的多次写入会被合并成一次，所以它不是写入次数本身。" +
                "把它和 trafficSaveDueRounds（生产侧实际触发的次数）放在一起看：两者一致才说明" +
                "\"到期一次就真的写一次\"。",
            ["dueSemantics"] = MaintenanceDueSemantics.Describe,
            ["dueSemanticsWarning"] = MaintenanceDueSemantics.Warning,
            // 取模复算值只作交叉验证：权威值是上面直接读生产计数得到的那些字段。
            ["slowScanTriggerRoundsRecomputed"] = slowScanRounds,
            ["diskBackgroundScanTriggerRoundsRecomputed"] = diskBgScanRounds,
            ["trafficSaveDueRoundsRecomputed"] = trafficSaveDueRounds,
            ["gcRequestDueRoundsRecomputed"] = gcRequestDueRounds,
            ["workingSetTrimDueRoundsRecomputed"] = workingSetDueRounds,
            ["dueSemanticsRecomputationAgrees"] =
                Agrees(slowScanRounds, stats.SlowScanTriggers - prodSlowScanBefore) &&
                Agrees(diskBgScanRounds, stats.DiskBackgroundScanTriggers - prodDiskBgBefore) &&
                Agrees(trafficSaveDueRounds, stats.TrafficSaveTriggers - prodTrafficBefore) &&
                Agrees(gcRequestDueRounds, stats.GcRequestTriggers - prodGcBefore) &&
                Agrees(workingSetDueRounds, stats.WorkingSetTrimTriggers - prodWorkingSetBefore),
            ["processCpuMs"] = Math.Round(cpuTotalMs, 3),
            ["wholeMachineCpuPercent"] = elapsedTotal > 0
                ? Math.Round(cpuTotalMs / elapsedTotal / Environment.ProcessorCount * 100.0, 4)
                : 0,
            ["allocatedBytes"] = allocatedTotal,
            ["perSecondSamples"] = perSecond,
        };

        // 交叉验证：生产侧实际触发的"流量保存次数"与观测到的文件重写次数是否一致。
        // 用生产计数（而不是探针复算值）做基准；生产改成单调截止时间后，
        // 两者会明显对不上 —— 这就是探针的报警点。
        long trafficSaveObserved = stats.TrafficSaveTriggers - prodTrafficBefore;
        result["trafficRewriteAgreesWithDueRounds"] =
            Math.Abs(trafficFileRewrites - trafficSaveObserved) <= 1;
        result["trafficRewriteDelta"] = trafficFileRewrites - trafficSaveObserved;

        if (collect)
        {
            result["updateDurationMs"] = Stats(updateDurations);
            result["readDurationMs"] = Stats(readDurations);
        }
        return result;
    }

    /// <summary>
    /// 复算值与生产计数是否吻合（允许 ±1：探针读到的秒计数器可能比采集内部用的那个晚一拍）。
    /// </summary>
    private static bool Agrees(long recomputed, long observed) => Math.Abs(recomputed - observed) <= 1;

    /// <summary>两次快照之间的计数增量（按 Key 逐项相减）。</summary>
    private static JsonObject DeltaCounters(IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after)
    {
        var obj = new JsonObject();
        foreach (var kv in after)
        {
            before.TryGetValue(kv.Key, out long b);
            obj[kv.Key] = kv.Value - b;
        }
        return obj;
    }

    private static JsonObject Stats(List<double> values)    {
        if (values.Count == 0) return new JsonObject { ["count"] = 0 };
        var sorted = new List<double>(values);
        sorted.Sort();
        return new JsonObject
        {
            ["count"] = sorted.Count,
            ["avg"] = Math.Round(sorted.Average(), 3),
            ["p50"] = Math.Round(sorted[sorted.Count / 2], 3),
            ["p95"] = Math.Round(sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))], 3),
            ["max"] = Math.Round(sorted[^1], 3),
        };
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
