using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// FPS 相关的状态证据（不启动任何外部服务）。
///
/// 两条证据：
///  ① 失效 PID 残留：构造已退出的 PID 记录，观察实际统计方法是否清理它们、
///     每轮抛出多少次进程查询异常、单轮成本是多少。
///  ② Web 快照读取隐藏 FPS：面板/任务栏都隐藏 FPS 时，快照是否仍然读取 FPS。
///
/// 全程不启动 PresentMon/ETW，不下载任何外部程序。
/// </summary>
internal static class FpsStateProbe
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// 造一个「已运行、无外部服务」的 FpsCounter 合成对象。
    /// 用 RuntimeHelpers.GetUninitializedObject 跳过构造函数，
    /// 从而不会启动后台线程，也不会触发外部服务下载/启动。
    /// </summary>
    private static FpsCounter MakeSyntheticFpsCounter()
    {
        var fps = (FpsCounter)RuntimeHelpers.GetUninitializedObject(typeof(FpsCounter));
        foreach (var field in typeof(FpsCounter).GetFields(PrivateInstance))
        {
            if (field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>))
            {
                field.SetValue(fps, Activator.CreateInstance(field.FieldType));
            }
        }
        SetField(fps, "_cycleTimer", Stopwatch.StartNew());
        SetField(fps, "_isRunning", true);
        return fps;
    }

    private static void SetField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, PrivateInstance)
                    ?? throw new InvalidOperationException($"字段 {name} 不存在。");
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, PrivateInstance)
                    ?? throw new InvalidOperationException($"字段 {name} 不存在。");
        return (T)field.GetValue(target)!;
    }

    /// <summary>失效 PID 残留证据。</summary>
    public static JsonArray RunDeadPidRetention(int[] seedCounts, int observedCycles, double costSeconds)
    {
        var results = new JsonArray();
        var calculate = typeof(FpsCounter).GetMethod("CalculateFps", PrivateInstance)
                        ?? throw new InvalidOperationException("找不到 FpsCounter.CalculateFps。");

        foreach (int count in seedCounts)
        {
            var fps = MakeSyntheticFpsCounter();
            var frames = GetField<ConcurrentDictionary<int, int>>(fps, "_processFrameCounts");
            for (int i = 0; i < count; i++) frames[1_900_000_000 + i] = 0;

            int exceptions = 0;
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> handler =
                (_, e) => { if (e.Exception is ArgumentException) exceptions++; };
            AppDomain.CurrentDomain.FirstChanceException += handler;
            for (int i = 0; i < observedCycles; i++) calculate.Invoke(fps, null);
            AppDomain.CurrentDomain.FirstChanceException -= handler;

            using var process = Process.GetCurrentProcess();
            double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            int cycles = 0;
            while (clock.Elapsed.TotalSeconds < costSeconds) { calculate.Invoke(fps, null); cycles++; }
            clock.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            process.Refresh();
            double cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpuBefore;

            results.Add(new JsonObject
            {
                ["kind"] = "fps_dead_pid_retention",
                ["seededDeadPids"] = count,
                ["retainedPidsInFrameCounts"] = frames.Count,
                ["observedCycles"] = observedCycles,
                ["processQueryExceptions"] = exceptions,
                ["costCycles"] = cycles,
                ["wallMsPerCycle"] = Math.Round(clock.Elapsed.TotalMilliseconds / Math.Max(1, cycles), 4),
                ["cpuMsPerCycle"] = Math.Round(cpuMs / Math.Max(1, cycles), 4),
                ["allocatedBytesPerCycle"] = Math.Round((double)allocated / Math.Max(1, cycles), 1),
                ["projectedWholeMachineCpuAt2Hz"] = Math.Round(cpuMs / Math.Max(1, cycles) * 2 / 1000 / Environment.ProcessorCount * 100, 5),
                ["presentMonStarted"] = false,
                ["note"] = "只调用实际统计方法，未启动外部服务；残留记录数是清理缺陷的直接证据。",
            });
        }
        return results;
    }

    /// <summary>Web 快照是否读取隐藏的 FPS 项。</summary>
    public static JsonObject RunWebHiddenFps(Settings cfg)
    {
        var oldItems = cfg.MonitorItems;
        var result = new JsonObject
        {
            ["kind"] = "web_hidden_fps_read",
            ["visibleInPanel"] = false,
            ["visibleInTaskbar"] = false,
            ["presentMonStarted"] = false,
        };

        HardwareMonitor? synthetic = null;
        LiteWebServer? web = null;
        LiteMonitor.src.SystemServices.PerformanceCounterManager? perf = null;
        try
        {
            cfg.MonitorItems = new List<MonitorItemConfig>
            {
                new() { Key = "FPS", VisibleInPanel = false, VisibleInTaskbar = false }
            };

            var fps = MakeSyntheticFpsCounter();
            var computer = new Computer();   // 不 Open：不初始化硬件树
            var net = new NetworkManager();
            perf = new LiteMonitor.src.SystemServices.PerformanceCounterManager();
            var provider = new HardwareValueProvider(computer, cfg, new SensorMap(), net, new DiskManager(), fps, perf, new object(), new Dictionary<string, float>());

            synthetic = (HardwareMonitor)RuntimeHelpers.GetUninitializedObject(typeof(HardwareMonitor));
            SetField(synthetic, "_valueProvider", provider);
            SetField(synthetic, "_networkManager", net);
            SetField(synthetic, "_computer", computer);
            var instanceProp = typeof(HardwareMonitor).GetProperty("Instance")!;
            instanceProp.GetSetMethod(true)!.Invoke(null, new object[] { synthetic });

            web = new LiteWebServer(cfg);
            var writeSnapshot = typeof(LiteWebServer).GetMethod("WriteSnapshotJson", PrivateInstance)!;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writeSnapshot.Invoke(web, new object[] { writer });
                writer.Flush();
            }

            using var doc = JsonDocument.Parse(stream.ToArray());
            bool containsFps = doc.RootElement.TryGetProperty("items", out var items) &&
                               items.EnumerateArray().Any(x => x.TryGetProperty("k", out var k) && k.GetString() == "FPS");
            var lastAccess = GetField<DateTime>(fps, "_lastAccessTime");

            result["outputContainsFps"] = containsFps;
            result["fpsReadRequested"] = lastAccess != DateTime.MinValue;
            result["note"] = "合成状态为「已运行、无结果」，避免启动外部服务；证明的是隐藏 FPS 被读取。";
        }
        catch (Exception ex)
        {
            result["error"] = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            cfg.MonitorItems = oldItems;
            var instanceProp = typeof(HardwareMonitor).GetProperty("Instance")!;
            instanceProp.GetSetMethod(true)!.Invoke(null, new object[] { null! });
            try { web?.Dispose(); } catch { }
            try { synthetic?.Dispose(); } catch { }
            try { perf?.Dispose(); } catch { }
        }
        return result;
    }
}
