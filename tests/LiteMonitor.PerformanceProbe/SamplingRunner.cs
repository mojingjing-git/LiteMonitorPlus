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
            steady["deviceUpdateCounters"] = _env.SnapshotDeviceCounters((int)(steady["collectionRounds"]?.GetValue<long>() ?? 0));
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

        long rounds = 0, reads = 0;
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

            // 采集代次/慢扫描/维护到期分类：读取应用自己的秒计数器
            long secondsCounter = (long)_secondsCounterField.GetValue(_env.Monitor)!;
            if (secondsCounter % 3 == 0) slowScanRounds++;
            if (secondsCounter % 10 == 0) diskBgScanRounds++;
            if (secondsCounter % 60 == 5) trafficSaveDueRounds++;
            if (secondsCounter % 180 == 30) gcRequestDueRounds++;
            if (secondsCounter % 300 == 45) workingSetDueRounds++;

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

        var result = new JsonObject
        {
            ["elapsedMs"] = elapsedTotal,
            ["collectionRounds"] = rounds,
            ["collectionRoundsPerSecond"] = elapsedTotal > 0 ? Math.Round(rounds * 1000.0 / elapsedTotal, 3) : 0,
            ["valueReads"] = reads,
            ["valueReadsPerRound"] = rounds > 0 ? Math.Round((double)reads / rounds, 3) : 0,
            ["collectionGenerations"] = rounds,
            ["slowScanTriggerRounds"] = slowScanRounds,
            ["diskBackgroundScanTriggerRounds"] = diskBgScanRounds,
            ["trafficSaveDueRounds"] = trafficSaveDueRounds,
            ["gcRequestDueRounds"] = gcRequestDueRounds,
            ["workingSetTrimDueRounds"] = workingSetDueRounds,
            ["trafficFileObservedRewrites"] = trafficFileRewrites,
            ["processCpuMs"] = Math.Round(cpuTotalMs, 3),
            ["wholeMachineCpuPercent"] = elapsedTotal > 0
                ? Math.Round(cpuTotalMs / elapsedTotal / Environment.ProcessorCount * 100.0, 4)
                : 0,
            ["allocatedBytes"] = allocatedTotal,
            ["perSecondSamples"] = perSecond,
        };

        if (collect)
        {
            result["updateDurationMs"] = Stats(updateDurations);
            result["readDurationMs"] = Stats(readDurations);
        }
        return result;
    }

    private static JsonObject Stats(List<double> values)
    {
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
