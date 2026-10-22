using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
/// ① 失效 PID 残留：构造已退出的 PID 记录，观察实际统计方法是否清理它们、
///     每轮抛出多少次进程查询异常、单轮成本是多少。
/// ② Web 快照读取隐藏 FPS：面板/任务栏都隐藏 FPS 时，快照是否仍然读取 FPS。
///
/// 全程不启动 PresentMon/ETW，不下载任何外部程序。
///
/// ⚠ 健壮性：本探针通过反射读取生产代码的私有成员。
///   生产侧一旦重构（改名/换类型），反射就会失配。
///   因此这里：
///   - 查找一律「按名字优先、按成员形状兜底」，不依赖具体类型名；
///   - 任何一步失败都**降级为一条明确标注的记录**（skipped / 未验证），
///     绝不让整个测量跑因为一个字段改名而崩掉。
/// </summary>
internal static class FpsStateProbe
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>合成死 PID 的起始值：保证系统里不存在，且不会误伤真实进程。</summary>
    private const int DeadPidBase = 1_900_000_000;

    // ================================================================
    // 反射小工具：找不到就降级，绝不抛
    // ================================================================

    /// <summary>按名字找字段；找不到就按类型找第一个同类型字段；都没有就返回 false。</summary>
    private static bool SetFieldByNameOrType(object target, string name, Func<object?> valueFactory, Type fallbackType)
    {
        var byName = target.GetType().GetField(name, PrivateInstance);
        if (byName != null) { byName.SetValue(target, valueFactory()); return true; }
        foreach (var f in target.GetType().GetFields(PrivateInstance))
        {
            if (f.FieldType == fallbackType) { f.SetValue(target, valueFactory()); return true; }
        }
        return false;
    }

    /// <summary>写字段；字段不存在时按 required 决定是返回 false 还是抛。</summary>
    private static bool SetField(object target, string name, object? value, bool required = true)
    {
        var field = target.GetType().GetField(name, PrivateInstance);
        if (field == null)
        {
            if (required) throw new InvalidOperationException($"字段 {name} 不存在。");
            return false;
        }
        field.SetValue(target, value);
        return true;
    }

    /// <summary>
    /// 判断「FPS 数据是否被请求过」。
    /// 兼容两种实现：<c>_lastAccessTime</c>（DateTime）与 <c>_lastAccessTicks</c>（long 单调毫秒）。
    /// 两者都找不到时返回 null（= 未验证，不编造 false）。
    /// </summary>
    private static bool? WasFpsRequested(object fps)
    {
        var byName = fps.GetType().GetField("_lastAccessTime", PrivateInstance);
        if (byName != null && byName.FieldType == typeof(DateTime))
        {
            return (DateTime)byName.GetValue(fps)! != DateTime.MinValue;
        }
        var byTicks = fps.GetType().GetField("_lastAccessTicks", PrivateInstance);
        if (byTicks != null && byTicks.FieldType == typeof(long))
        {
            return (long)byTicks.GetValue(fps)! != 0L;
        }
        return null;
    }

    // ================================================================
    // 「每 PID 帧状态」的统一访问方式（屏蔽生产内部结构差异）
    // ================================================================

    /// <summary>生产侧「每 PID 帧状态」的操作封装。</summary>
    private sealed class PidStateAdapter
    {
        /// <summary>播种：为一个 PID 记一帧（模拟"这个进程刚产生过帧，随后退出"）。</summary>
        public required Action<int> RecordFrame { get; init; }

        /// <summary>推进一轮统计/清理（生产侧的真实周期动作；返回值 = 本轮清理掉的 PID 数）。</summary>
        public required Func<double, int> AdvanceCycle { get; init; }

        /// <summary>当前残留的 PID 状态条目数。</summary>
        public required Func<int> ProcessCount { get; init; }

        /// <summary>累计进程查询次数（生产侧若提供该计数则填上，否则为 null）。</summary>
        public Func<int>? QueryCount { get; set; }
    }

    /// <summary>解析器：<see cref="Create"/> 每次返回一份干净的状态实例。</summary>
    private sealed class PidStateResolver
    {
        public required Func<PidStateAdapter> Create { get; init; }

        /// <summary>命中的是哪一种结构（写进结果文件，便于交接时核对）。</summary>
        public required string Shape { get; init; }
    }

    /// <summary>
    /// 按**成员形状**（而不是类型名/字段名）定位生产的「每 PID 帧状态」结构。
    /// 找不到返回 null —— 调用方据此输出"未定位到结构、跳过本项"，而不是给出错误数字。
    /// </summary>
    private static PidStateResolver? TryResolvePidStateResolver()
    {
        foreach (var field in typeof(FpsCounter).GetFields(PrivateInstance))
        {
            var t = field.FieldType;

            // 形状：对象上同时具备 RecordFrame(int) / AdvanceCycle(double) / ProcessCount
            var record = t.GetMethod("RecordFrame", PublicInstance, null, new[] { typeof(int) }, null);
            var advance = t.GetMethod("AdvanceCycle", PublicInstance, null, new[] { typeof(double) }, null);
            var count = t.GetProperty("ProcessCount", PublicInstance)?.GetMethod;
            if (record == null || advance == null || count == null) continue;

            var queryType = FindQueryImplementationType(t);
            if (queryType == null) continue;   // 找不到生产默认的进程查询实现 → 试下一个候选字段

            var shape = $"状态存储对象（{t.Name}，经 FpsCounter 字段 {field.Name} 发现）";
            return new PidStateResolver
            {
                Shape = shape,
                Create = () =>
                {
                    var store = CreateStateStore(t, queryType);
                    var adapter = new PidStateAdapter
                    {
                        RecordFrame = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), store, record),
                        AdvanceCycle = (Func<double, int>)Delegate.CreateDelegate(typeof(Func<double, int>), store, advance),
                        ProcessCount = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), store, count),
                    };
                    var queryCount = t.GetProperty("QueryCount", PublicInstance)?.GetMethod;
                    if (queryCount != null)
                    {
                        adapter.QueryCount = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), store, queryCount);
                    }
                    return adapter;
                },
            };
        }
        return null;
    }

    /// <summary>找到状态存储构造函数所需的进程查询器接口，并选出可实例化的实现类型。</summary>
    private static Type? FindQueryImplementationType(Type storeType)
    {
        var ctor = storeType.GetConstructors(PublicInstance)
                             .OrderBy(c => c.GetParameters().Length)
                             .FirstOrDefault();
        if (ctor == null) return null;

        var iface = ctor.GetParameters()
                         .FirstOrDefault(p => p.ParameterType.IsInterface)
                         ?.ParameterType;
        if (iface == null) return null;

        // 优先使用生产默认实现（例如 FpsCounter 内部的进程查询器）
        var nested = typeof(FpsCounter).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(n => !n.IsAbstract && iface.IsAssignableFrom(n) && n.GetConstructor(Type.EmptyTypes) != null);
        if (nested != null) return nested;

        // 其次：同一程序集里的其它实现
        return iface.Assembly.GetTypes()
            .FirstOrDefault(n => !n.IsAbstract && iface.IsAssignableFrom(n) && n.GetConstructor(Type.EmptyTypes) != null);
    }

    /// <summary>用生产的默认进程查询器构造一份干净的状态存储实例。</summary>
    private static object CreateStateStore(Type storeType, Type queryType)
    {
        var ctor = storeType.GetConstructors(PublicInstance)
                             .OrderBy(c => c.GetParameters().Length)
                             .First();
        var ps = ctor.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType;
            args[i] = pt.IsInterface
                ? Activator.CreateInstance(queryType, nonPublic: true)
                : Activator.CreateInstance(pt);
        }
        return ctor.Invoke(args);
    }

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
        // 按名字优先、按类型兜底：生产改名时不至于直接崩掉
        SetFieldByNameOrType(fps, "_cycleTimer", () => Stopwatch.StartNew(), typeof(Stopwatch));
        SetFieldByNameOrType(fps, "_isRunning", () => true, typeof(bool));
        return fps;
    }

    // ================================================================
    // 证据 ①：失效 PID 残留
    // ================================================================

    /// <summary>失效 PID 残留证据。</summary>
    public static JsonArray RunDeadPidRetention(int[] seedCounts, int observedCycles, double costSeconds)
    {
        var results = new JsonArray();

        var resolver = TryResolvePidStateResolver();
        if (resolver == null)
        {
            results.Add(new JsonObject
            {
                ["kind"] = "fps_dead_pid_retention",
                ["skipped"] = true,
                ["reason"] = "未能在 FpsCounter 上定位「每 PID 帧状态」结构：既没有带 RecordFrame/AdvanceCycle/ProcessCount 的状态存储对象，也没有可用的旧式帧计数字段。生产结构可能再次重构。",
                ["presentMonStarted"] = false,
                ["note"] = "不编造数据：定位失败即跳过本项，不输出任何残留数或成本数字。",
            });
            return results;
        }

        foreach (int count in seedCounts)
        {
            try
            {
                results.Add(MeasureRetention(resolver, count, observedCycles, costSeconds));
            }
            catch (Exception ex)
            {
                results.Add(new JsonObject
                {
                    ["kind"] = "fps_dead_pid_retention",
                    ["seededDeadPids"] = count,
                    ["skipped"] = true,
                    ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                    ["stateShape"] = resolver.Shape,
                    ["presentMonStarted"] = false,
                    ["note"] = "本项测量失败，已降级为错误记录，不影响其它分组。",
                });
            }
        }
        return results;
    }

    /// <summary>测一组「播种 N 个死 PID」场景。</summary>
    private static JsonObject MeasureRetention(PidStateResolver resolver, int count, int observedCycles, double costSeconds)
    {
        var adapter = resolver.Create();

        // ---------- 观察阶段：死 PID 是否被清理、是否每轮重复查询 ----------
        int exceptions = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler =
            (_, e) => { if (e.Exception is ArgumentException) exceptions++; };
        AppDomain.CurrentDomain.FirstChanceException += handler;
        for (int i = 0; i < count; i++) adapter.RecordFrame(DeadPidBase + i);
        int queriesAfterSeed = adapter.QueryCount?.Invoke() ?? -1;
        for (int i = 0; i < observedCycles; i++) adapter.AdvanceCycle(0.5);
        AppDomain.CurrentDomain.FirstChanceException -= handler;

        int retained = adapter.ProcessCount();
        int queryCalls = adapter.QueryCount?.Invoke() ?? -1;
        int queriesDuringCycles = queryCalls >= 0 && queriesAfterSeed >= 0 ? queryCalls - queriesAfterSeed : -1;

        // ---------- 成本阶段：最坏情况下的单轮成本 ----------
        // 口径与实施前基线一致：**每一轮都要处理 count 个死 PID**。
        // 因此每轮前都重新播种一份干净状态（创建开销不计入单轮成本，单独扣除）。
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        double advanceMs = 0;
        int cycles = 0;
        while (clock.Elapsed.TotalSeconds < costSeconds)
        {
            var a = resolver.Create();
            for (int i = 0; i < count; i++) a.RecordFrame(DeadPidBase + i);
            long t0 = Stopwatch.GetTimestamp();
            a.AdvanceCycle(0.5);
            long t1 = Stopwatch.GetTimestamp();
            advanceMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            cycles++;
        }
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        process.Refresh();
        double cpuWithAdvance = process.TotalProcessorTime.TotalMilliseconds - cpuBefore;

        // 同形扣除：只准备（建状态 + 播种）不推进，跑同样的轮数
        process.Refresh();
        double cpuBeforePrepare = process.TotalProcessorTime.TotalMilliseconds;
        long allocBeforePrepare = GC.GetAllocatedBytesForCurrentThread();
        for (int c = 0; c < cycles; c++)
        {
            var a = resolver.Create();
            for (int i = 0; i < count; i++) a.RecordFrame(DeadPidBase + i);
        }
        long allocPrepare = GC.GetAllocatedBytesForCurrentThread() - allocBeforePrepare;
        process.Refresh();
        double cpuPrepare = process.TotalProcessorTime.TotalMilliseconds - cpuBeforePrepare;

        int divisor = Math.Max(1, cycles);
        double wallPerCycle = advanceMs / divisor;
        double cpuPerCycle = Math.Max(0, (cpuWithAdvance - cpuPrepare) / divisor);
        double allocPerCycle = Math.Max(0, (allocated - allocPrepare) / divisor);

        return new JsonObject
        {
            ["kind"] = "fps_dead_pid_retention",
            ["seededDeadPids"] = count,
            // 兼容旧基线字段名；语义 = 推进 observedCycles 轮后仍然残留的 PID 状态条目数
            ["retainedPidsInFrameCounts"] = retained,
            ["retainedPidStateEntries"] = retained,
            ["deadPidsCleanedUp"] = retained == 0,
            ["observedCycles"] = observedCycles,
            ["processQueryExceptions"] = exceptions,
            ["processQueryCalls"] = queryCalls,
            ["queryCallsDuringCycles"] = queriesDuringCycles,
            ["costCycles"] = cycles,
            ["wallMsPerCycle"] = Math.Round(wallPerCycle, 4),
            ["cpuMsPerCycle"] = Math.Round(cpuPerCycle, 4),
            ["allocatedBytesPerCycle"] = Math.Round(allocPerCycle, 1),
            ["projectedWholeMachineCpuAt2Hz"] = Math.Round(cpuPerCycle * 2 / 1000 / Environment.ProcessorCount * 100, 5),
            ["presentMonStarted"] = false,
            ["stateShape"] = resolver.Shape,
            ["costMeasurementMethod"] = "强类型委托直接调用生产侧的单轮推进（反射派发开销已排除）；墙钟只计单轮推进本身，CPU/分配已扣除同形的「建状态+播种」开销",
            ["note"] = "死 PID 由生产的进程查询判定为已退出后清理；processQueryExceptions 统计进程查询抛出的 ArgumentException 次数（首次机会异常）。",
        };
    }

    // ================================================================
    // 证据 ②：Web 快照读取隐藏 FPS
    // ================================================================

    /// <summary>
    /// Web 快照读取 FPS 的行为（计划 §4.4：按需求决定是否读取）。
    ///
    /// 两个场景：
    /// ① 无需求：FPS 在面板/任务栏都隐藏，且没有合法的 Web 数据需求 → 期望**不**读取 FPS。
    /// ② 有 Web 需求：FPS 本地隐藏，但存在合法的 Web 数据需求且开启了网页版帧率 → 期望**读取** FPS。
    ///
    /// 需求信号用生产自己的需求状态对象登记（不建立真实网络连接，不启动任何外部服务）。
    /// </summary>
    public static JsonObject RunWebHiddenFps(Settings cfg)
    {
        var oldItems = cfg.MonitorItems;
        var oldWebFpsEnabled = cfg.WebFpsEnabled;
        var result = new JsonObject
        {
            ["kind"] = "web_hidden_fps_read",
            ["visibleInPanel"] = false,
            ["visibleInTaskbar"] = false,
            ["presentMonStarted"] = false,
        };
        var warnings = new JsonArray();

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
            // 标记为「已最终释放」：GetFps() 会先记录访问时间再直接返回，
            // 既不会走需求判定，也不会触发外部服务启动/下载。
            SetFieldByNameOrType(fps, "_disposed", () => 1, typeof(int));

            var computer = new Computer();   // 不 Open：不初始化硬件树
            var net = new NetworkManager();
            perf = new LiteMonitor.src.SystemServices.PerformanceCounterManager();
            var provider = new HardwareValueProvider(computer, cfg, new SensorMap(), net, new DiskManager(), fps, perf, new object(), new Dictionary<string, float>());

            synthetic = (HardwareMonitor)RuntimeHelpers.GetUninitializedObject(typeof(HardwareMonitor));
            if (!SetField(synthetic, "_valueProvider", provider, required: false))
                warnings.Add("HardwareMonitor._valueProvider 字段不存在（生产重构），快照读不到 FPS。");
            if (!SetField(synthetic, "_networkManager", net, required: false))
                warnings.Add("HardwareMonitor._networkManager 字段不存在（生产重构）。");
            if (!SetField(synthetic, "_computer", computer, required: false))
                warnings.Add("HardwareMonitor._computer 字段不存在（生产重构）。");

            var instanceProp = typeof(HardwareMonitor).GetProperty("Instance");
            var instanceSetter = instanceProp?.GetSetMethod(true);
            if (instanceSetter == null)
            {
                warnings.Add("HardwareMonitor.Instance 没有可写 setter（生产重构），无法注入合成实例。");
            }
            else
            {
                instanceSetter.Invoke(null, new object[] { synthetic });
            }

            web = new LiteWebServer(cfg);

            // ---------- 场景 ①：无需求 ----------
            var (noDemand, noDemandRequested) = MeasureSnapshot(web, fps, "无需求：FPS 本地隐藏 + 无合法 Web 连接");
            noDemand["expectedFpsRead"] = false;
            noDemand["matchesExpectation"] = noDemandRequested.HasValue ? noDemandRequested.Value == false : (bool?)null;
            result["scenarioNoDemand"] = noDemand;

            // ---------- 场景 ②：有 Web 需求 ----------
            cfg.WebFpsEnabled = true;
            if (TryRegisterWebDemand(web))
            {
                var (withDemand, withDemandRequested) = MeasureSnapshot(web, fps, "有 Web 需求：FPS 本地隐藏 + 一个已鉴权 Web 连接 + 开启网页版帧率");
                withDemand["expectedFpsRead"] = true;
                withDemand["matchesExpectation"] = withDemandRequested.HasValue ? withDemandRequested.Value == true : (bool?)null;
                result["scenarioWithWebDemand"] = withDemand;
            }
            else
            {
                warnings.Add("无法在生产的需求状态对象上登记 Web 需求（有需求场景未验证）。");
            }

            result["warnings"] = warnings;
            result["note"] = "合成状态为「已释放、无结果」：既不启动外部服务，也不下载任何组件；只验证「网页快照是否真的去读 FPS」。";
        }
        catch (Exception ex)
        {
            result["error"] = $"{ex.GetType().Name}: {ex.Message}";
            result["warnings"] = warnings;
        }
        finally
        {
            cfg.MonitorItems = oldItems;
            cfg.WebFpsEnabled = oldWebFpsEnabled;
            try
            {
                var instanceProp = typeof(HardwareMonitor).GetProperty("Instance");
                instanceProp?.GetSetMethod(true)?.Invoke(null, new object[] { null! });
            }
            catch { }
            try { web?.Dispose(); } catch { }
            try { synthetic?.Dispose(); } catch { }
            try { perf?.Dispose(); } catch { }
        }
        return result;
    }

    /// <summary>按名字找快照写出方法；找不到就按参数形状（唯一参数是 Utf8JsonWriter）兜底。</summary>
    private static MethodInfo? FindSnapshotWriter()
    {
        var byName = typeof(LiteWebServer).GetMethod("WriteSnapshotJson", PrivateInstance);
        if (byName != null) return byName;
        return typeof(LiteWebServer)
            .GetMethods(PrivateInstance)
            .FirstOrDefault(m => m.Name.Contains("Snapshot", StringComparison.OrdinalIgnoreCase)
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType == typeof(Utf8JsonWriter));
    }

    /// <summary>写一次网页快照，并记录「FPS 是否真的被读取」。</summary>
    private static (JsonObject Json, bool? Requested) MeasureSnapshot(LiteWebServer web, object fps, string scenario)
    {
        var json = new JsonObject { ["scenario"] = scenario };
        var writeSnapshot = FindSnapshotWriter();
        if (writeSnapshot == null)
        {
            json["error"] = "找不到快照写出方法（WriteSnapshotJson）：生产方法名已变。";
            return (json, null);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writeSnapshot.Invoke(web, new object[] { writer });
            writer.Flush();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        bool containsFps = doc.RootElement.TryGetProperty("items", out var items) &&
                           items.EnumerateArray().Any(x => x.TryGetProperty("k", out var k) && k.GetString() == "FPS");

        bool? requested = WasFpsRequested(fps);
        json["outputContainsFps"] = containsFps;
        json["fpsReadRequested"] = requested.HasValue ? (JsonNode?)requested.Value : null;
        return (json, requested);
    }

    /// <summary>
    /// 在生产自己的需求状态对象上登记一个「已鉴权连接」，用来制造合法的 Web 需求。
    /// 不建立真实网络连接，只写生产读取的那份需求状态。
    /// </summary>
    private static bool TryRegisterWebDemand(LiteWebServer web)
    {
        try
        {
            var session = typeof(LiteWebServer).GetField("_sessionManager", PrivateInstance)?.GetValue(web);
            if (session == null) return false;
            var demand = session.GetType().GetField("_demand", PrivateInstance)?.GetValue(session);
            if (demand == null) return false;
            var add = demand.GetType().GetMethod("AddAuthenticatedConnection", PublicInstance);
            if (add == null) return false;
            add.Invoke(demand, null);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
