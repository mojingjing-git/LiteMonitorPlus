using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// LiteMonitor++ 性能探针入口。
///
/// 用法示例（PowerShell 7）：
///   dotnet run --project tests/LiteMonitor.PerformanceProbe -c Release -- --out baseline --groups A,B
///
/// 详见 tests/LiteMonitor.PerformanceProbe/README.md。
/// </summary>
internal static class Program
{
    private static readonly StringBuilder Log = new();

    [STAThread]
    private static int Main(string[] args)
    {
        var options = ProbeOptions.Parse(args);

        // ★ A04 外观探针入口（独立入口，不改变既有采集语义）。
        //   --appearance-only 走完全不同的路径：不创建 HardwareMonitor、不 Open 硬件、
        //   不做驱动就绪检查、不安装/卸载驱动、不启动 PresentMon/ETW。
        //   既有采集路径（默认分支）完全不进入这里，行为保持不变。
        if (options.AppearanceOnly)
            return AppearanceProbeRunner.Run(options);

        Write($"LiteMonitor++ 性能探针 启动于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write($"参数: {string.Join(' ', args)}");
        Write($"隔离输出目录 (AppContext.BaseDirectory): {AppContext.BaseDirectory}");
        Write($"结果目录: {options.OutputDir}");

        var results = new JsonObject
        {
            ["kind"] = "performance_probe_result",
            ["startedAt"] = DateTime.Now.ToString("o"),
            ["options"] = options.ToJson(),
            // ★ 批次级口径声明：任何跨批次比较都必须先读它（见 README §6）。
            //   它说明本批次各计数字段的来源，避免"同名字段不同含义"被直接相减。
            ["counterSemantics"] = ProbeCounterSemantics.Build(),
        };

        ProbeEnvironment? env = null;
        bool skipped = false;
        try
        {
            env = new ProbeEnvironment(options.OutputDir);

            // 驱动就绪检查：只读检查，绝不安装/卸载驱动。
            var driverCheck = CheckDriverReadiness(env.Cfg);
            results["driverReadiness"] = driverCheck;
            Write($"驱动就绪检查: {driverCheck.ToJsonString()}");

            bool hardwareUsable = env.WaitForHardwareReady(TimeSpan.FromSeconds(45));
            results["hardwareReady"] = hardwareReadyJson(env, hardwareUsable, driverCheck);
            Write($"硬件就绪: {results["hardwareReady"]?.ToJsonString()}");

            if (hardwareUsable && (driverCheck["needsInstall"]?.GetValue<bool>() ?? false) == false)
            {
                bool counters = env.InstallUpdateCounters();
                results["updateCountersInstalled"] = counters;
                results["updateCounterDiagnostics"] = env.Diagnostics;
                Write($"LHM Update 计数代理安装: {counters} 诊断: {env.Diagnostics.ToJsonString()}");

                var runner = new SamplingRunner(env);
                // 旧取模复算是否可用：不可用时只影响 *Recomputed 系列（输出 null），
                // 主字段（生产侧真实计数）照常产出 —— 探针不会因此起不来。
                results["secondsCounterAvailability"] = runner.SecondsCounterAvailable
                    ? "available"
                    : runner.SecondsCounterNote;
                var groups = BuildGroups(options);
                var groupArray = new JsonArray();
                foreach (var spec in groups)
                {
                    Write($"--- 开始分组 {spec.Id}: {spec.Purpose} (预热 {options.WarmupSeconds}s / 稳态 {options.SteadySeconds}s)");
                    var record = runner.RunGroup(spec, options.WarmupSeconds, options.SteadySeconds);
                    groupArray.Add(record);
                    Write($"--- 分组 {spec.Id} 完成: {record["collectionRounds"]?.ToJsonString()} 轮, " +
                          $"整机 CPU {record["wholeMachineCpuPercent"]?.ToJsonString()}%");
                    // 组间留出恢复时间，避免上一组的余热影响下一组
                    Thread.Sleep(options.SettleSeconds * 1000);
                }
                results["groups"] = groupArray;

                Write("--- 单设备 Update 成本测量");
                results["deviceUpdateCosts"] = MicroBenchmarks.RunDeviceUpdateBench(env, options.DeviceCalls);

                Write("--- 兼容缓冲绘制微基准");
                results["renderBench"] = MicroBenchmarks.RunRenderBench(env, new[] { 1.0, 2.0 }, options.RenderSeconds);
            }
            else
            {
                results["skippedReason"] = "硬件层不可用或驱动就绪检查未通过，未执行需要真实硬件的测量（不编造数据）。";
                Write("跳过需要真实硬件的测量。");
                // 退出码非 0：一份"没有硬件数据"的结果文件不能被当成成功的基线。
                skipped = true;
            }

            Write("--- FPS 状态证据（不启动外部服务）");
            // FPS 证据段单独兜底：生产侧重构导致探针失配时，只降级这一段，
            // 不应该让整轮测量（含硬件分组）作废。
            var fpsState = new JsonObject();
            try
            {
                fpsState["deadPidRetention"] = FpsStateProbe.RunDeadPidRetention(new[] { 0, 1, 10, 100 }, options.FpsObservedCycles, options.FpsCostSeconds);
            }
            catch (Exception ex)
            {
                fpsState["deadPidRetentionError"] = $"{ex.GetType().Name}: {ex.Message}";
                Write($"!! FPS 死 PID 证据失败（已降级，不影响其它测量）: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                fpsState["webHiddenFps"] = FpsStateProbe.RunWebHiddenFps(env.Cfg);
            }
            catch (Exception ex)
            {
                fpsState["webHiddenFpsError"] = $"{ex.GetType().Name}: {ex.Message}";
                Write($"!! Web 隐藏 FPS 证据失败（已降级，不影响其它测量）: {ex.GetType().Name}: {ex.Message}");
            }
            results["fpsState"] = fpsState;

            results["metadata"] = env.BuildMetadata();
        }
        catch (Exception ex)
        {
            Write($"!! 探针异常: {ex}");
            results["fatalError"] = $"{ex.GetType().Name}: {ex.Message}";
            results["fatalStack"] = ex.StackTrace;
        }
        finally
        {
            try { env?.Dispose(); } catch { }
        }

        results["finishedAt"] = DateTime.Now.ToString("o");
        results["log"] = Log.ToString();
        results["exitCode"] = results["fatalError"] != null ? 1 : (skipped ? 2 : 0);
        results["exitCodeMeaning"] = "0=正常；1=探针异常；2=硬件层不可用/驱动未就绪，未产出硬件测量数据";

        string resultPath = Path.Combine(options.OutputDir, "probe-results.json");
        string logPath = Path.Combine(options.OutputDir, "probe-run.txt");
        Directory.CreateDirectory(options.OutputDir);
        File.WriteAllText(resultPath, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(logPath, Log.ToString(), new UTF8Encoding(false));
        Write($"结果已写入: {resultPath}");
        Write($"日志已写入: {logPath}");

        int exitCode = (int)results["exitCode"]!.GetValue<int>();
        if (exitCode != 0)
            Write($"!! 退出码 {exitCode}：{(exitCode == 2 ? "没有产出硬件测量数据，这份结果不能当基线用" : "探针异常")}。");
        return exitCode;
    }

    private static JsonObject hardwareReadyJson(ProbeEnvironment env, bool ready, JsonObject driverCheck)
    {
        return new JsonObject
        {
            ["ready"] = ready,
            ["note"] = env.ReadyNote,
            ["driverNeedsInstall"] = driverCheck["needsInstall"]?.GetValue<bool>() ?? false,
            ["driverStatus"] = driverCheck["status"]?.ToJsonString() ?? "",
        };
    }

    /// <summary>只读检查驱动就绪状态；绝不触发安装。</summary>
    private static JsonObject CheckDriverReadiness(Settings cfg)
    {
        var result = new JsonObject();
        try
        {
            var installer = new DriverInstaller(cfg, null, null);
            var method = typeof(DriverInstaller).GetMethod("CheckPawnIORequirement", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var check = method.Invoke(installer, null)!;
            bool needsInstall = (bool)check.GetType().GetProperty("NeedsInstall")!.GetValue(check)!;
            string status = check.GetType().GetProperty("Status")?.GetValue(check)?.ToString() ?? "";
            result["needsInstall"] = needsInstall;
            result["status"] = status;
            result["note"] = "只读检查，未安装/卸载任何驱动。";
        }
        catch (Exception ex)
        {
            result["error"] = $"{ex.GetType().Name}: {ex.Message}";
        }
        return result;
    }

    private static List<GroupSpec> BuildGroups(ProbeOptions options)
    {
        var all = new List<GroupSpec>
        {
            new("A", "1000 ms、默认单卡、Web 关：保护默认模式成本", 1000, false, true, 1, 0, 0),
            new("B", "100 ms、默认单卡、Web 关：验证高刷新放大硬件采集", 100, false, true, 1, 0, 0),
            new("C", "100 ms、GPU 项全关、Web 关：对照剩余基础成本", 100, false, false, 1, 0, 0),
            new("D", "100 ms、Web 开且无人访问：验证空闲服务不扩大范围", 100, true, true, 1, 0, 0),
            new("E", "100 ms、单个已鉴权网页：连接活动时数据正确且限频", 100, true, true, 1, 1, 0),
            new("F", "100 ms、多网页 + HTTP 轮询：验证多消费者共用采样", 100, true, true, 1, 3, 1),
            new("G", "100 ms、多卡本地监控：每张启用卡均正确", 100, false, true, 2, 0, 0),
            new("H1", "3000 ms、Web 关：慢刷新省电", 3000, false, true, 1, 0, 0),
            new("H2", "3000 ms、Web 开且有合法网页：独立 Web 需求", 3000, true, true, 1, 1, 0),
        };

        if (options.Groups.Count == 0) return all;
        return all.Where(g => options.Groups.Contains(g.Id)).ToList();
    }

    private static void Write(string message)
    {
        Console.WriteLine(message);
        Log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}

/// <summary>命令行参数。</summary>
internal sealed class ProbeOptions
{
    public string OutputDir { get; private set; } = Path.Combine(AppContext.BaseDirectory, "results");
    public List<string> Groups { get; private set; } = new();
    public int WarmupSeconds { get; private set; } = 15;
    public int SteadySeconds { get; private set; } = 60;
    public int SettleSeconds { get; private set; } = 3;
    public int DeviceCalls { get; private set; } = 20;
    public double RenderSeconds { get; private set; } = 2.0;
    public int FpsObservedCycles { get; private set; } = 12;
    public double FpsCostSeconds { get; private set; } = 1.5;

    // ===== A04 外观探针专用参数（不影响既有采集路径） =====
    /// <summary>是否走"只做外观"的独立入口（不创建 HardwareMonitor）。</summary>
    public bool AppearanceOnly { get; private set; }
    /// <summary>夹具集合：full | core | edge | quick。</summary>
    public string AppearanceFixtureSet { get; private set; } = "full";
    /// <summary>每个夹具的测量绘制次数。</summary>
    public int AppearanceDraws { get; private set; } = 20;
    /// <summary>局部失效场景：all | sample | none。</summary>
    public string AppearanceInvalidation { get; private set; } = "all";
    /// <summary>与哪一份基线做逐像素对比（结果目录）。</summary>
    public string CompareDir { get; private set; } = "";
    /// <summary>
    /// 是否做"旧入口 vs 新入口"配对测量（A14 / v4 §7.4 的装饰层额外 CPU 口径）。
    /// <para>不传时行为与既有基线**完全一致**（纯加法，不影响既有可比性）。</para>
    /// </summary>
    public bool AppearancePaired { get; private set; }
    /// <summary>配对测量轮数（v4 §7.4 要求同机至少三轮配对）。</summary>
    public int AppearancePairedRounds { get; private set; } = 3;
    /// <summary>夹具过滤（按 fixtureId 子串，逗号分隔；留空 = 不过滤）。</summary>
    public string AppearanceFilter { get; private set; } = "";
    /// <summary>装饰分解测量的主题名（非空 = 只跑分解模式，不跑常规夹具矩阵）。</summary>
    public string AppearanceDecompose { get; private set; } = "";
    /// <summary>
    /// 是否做 **CPU 时间口径** 的配对测量（N1「口径混用」修正）。
    /// <para>
    /// 与 <see cref="AppearancePaired"/> 搭配：在既有配对流程里一并产出 CPU 口径字段；
    /// 单独使用：只跑 CPU 口径配对测量（跳过常规夹具矩阵与经过时间配对测量）。
    /// </para>
    /// </summary>
    public bool AppearancePairedCpu { get; private set; }
    /// <summary>
    /// CPU 口径每个批次的绘制次数（默认 2000）。
    /// <para>Windows 进程 CPU 时间的量化步长约 15.6 ms，批次太短则 CPU 差值不可用。</para>
    /// </summary>
    public int AppearancePairedCpuDraws { get; private set; } = 2000;
    /// <summary>
    /// CPU 口径每轮的配对段数（默认 2）。
    /// <para>
    /// 段数取偶数时，一轮之内旧/新各当一次"先测的"，先后顺序偏差在**轮内**就被抵消；
    /// 再叠加逐轮翻转起始顺序（奇数轮旧→新、偶数轮新→旧），形成双重抵消。
    /// </para>
    /// </summary>
    public int AppearancePairedCpuSegments { get; private set; } = 2;
    /// <summary>单次运行里最多做多少次 CPU 口径配对测量（默认 1；矩阵流程里 = 前 N 个夹具）。</summary>
    public int AppearancePairedCpuMaxMeasurements { get; private set; } = 1;
    /// <summary>是否"只跑"CPU 口径配对测量（--appearance-paired-cpu 单独使用、未搭配 --appearance-paired）。</summary>
    public bool AppearancePairedCpuOnly => AppearancePairedCpu && !AppearancePaired;
    /// <summary>
    /// CPU 口径**只跑**模式下要测的主题名（默认空 = 取主题清单里的第一个）。
    /// <para>
    /// ★ 为什么需要它：<c>--appearance-paired-cpu</c> 单独使用时会走"CPU 口径只跑"分支，
    /// 该分支**绕过 <c>--appearance-filter</c>（过滤逻辑在它之后才执行），
    /// 主题只能取 <c>themes[0]</c> —— 而主题清单是**字母序动态发现**的，
    /// 于是"想测 CyberPunk_Mech"却测成了 AuroraDream_Purple，与 §6.4 的数字对不上。
    /// 本参数让 CPU 口径只跑模式可以显式指定主题。
    /// </para>
    /// </summary>
    public string AppearancePairedCpuTheme { get; private set; } = "";
    /// <summary>原始命令行（写进结果 JSON 备查）。</summary>
    public string[] RawArgs { get; private set; } = Array.Empty<string>();

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["outputDir"] = OutputDir,
            ["groups"] = new JsonArray(Groups.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
            ["warmupSeconds"] = WarmupSeconds,
            ["steadySeconds"] = SteadySeconds,
            ["settleSeconds"] = SettleSeconds,
            ["deviceCalls"] = DeviceCalls,
            ["renderSeconds"] = RenderSeconds,
            ["fpsObservedCycles"] = FpsObservedCycles,
            ["fpsCostSeconds"] = FpsCostSeconds,
            // ===== A14 CPU 口径配对测量（默认关闭；开启会显著拉长探针运行时间）=====
            ["appearancePairedCpu"] = AppearancePairedCpu,
            ["appearancePairedCpuOnly"] = AppearancePairedCpuOnly,
            ["appearancePairedCpuDraws"] = AppearancePairedCpuDraws,
            ["appearancePairedCpuSegments"] = AppearancePairedCpuSegments,
            ["appearancePairedCpuMaxMeasurements"] = AppearancePairedCpuMaxMeasurements,
            ["appearancePairedCpuTheme"] = AppearancePairedCpuTheme,
        };
    }

    public static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions { RawArgs = args };
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (arg)
            {
                case "--out": options.OutputDir = Path.GetFullPath(Next()); break;
                case "--groups":
                    options.Groups = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                case "--warmup": options.WarmupSeconds = int.Parse(Next()); break;
                case "--steady": options.SteadySeconds = int.Parse(Next()); break;
                case "--settle": options.SettleSeconds = int.Parse(Next()); break;
                case "--device-calls": options.DeviceCalls = int.Parse(Next()); break;
                case "--render-seconds": options.RenderSeconds = double.Parse(Next()); break;
                case "--fps-cycles": options.FpsObservedCycles = int.Parse(Next()); break;
                case "--fps-cost-seconds": options.FpsCostSeconds = double.Parse(Next()); break;
                // ===== A04 外观探针 =====
                case "--appearance-only": options.AppearanceOnly = true; break;
                case "--appearance-set": options.AppearanceFixtureSet = Next(); break;
                case "--appearance-draws": options.AppearanceDraws = int.Parse(Next()); break;
                case "--appearance-invalidation": options.AppearanceInvalidation = Next(); break;
                case "--compare": options.CompareDir = Path.GetFullPath(Next()); break;
                // ===== A14 配对测量（旧入口 vs 新入口）=====
                case "--appearance-paired": options.AppearancePaired = true; break;
                case "--appearance-paired-rounds": options.AppearancePairedRounds = int.Parse(Next()); break;
                case "--appearance-filter": options.AppearanceFilter = Next(); break;
                case "--appearance-decompose": options.AppearanceDecompose = Next(); break;
                // ===== A14 CPU 口径配对测量（N1 口径混用修正）=====
                case "--appearance-paired-cpu": options.AppearancePairedCpu = true; break;
                case "--appearance-paired-cpu-draws": options.AppearancePairedCpuDraws = int.Parse(Next()); break;
                case "--appearance-paired-cpu-segments": options.AppearancePairedCpuSegments = int.Parse(Next()); break;
                case "--appearance-paired-cpu-max-measurements": options.AppearancePairedCpuMaxMeasurements = int.Parse(Next()); break;
                case "--appearance-paired-cpu-theme": options.AppearancePairedCpuTheme = Next(); break;
                case "--quick":
                    options.WarmupSeconds = 3;
                    options.SteadySeconds = 10;
                    options.SettleSeconds = 1;
                    options.RenderSeconds = 1.0;
                    options.FpsCostSeconds = 0.5;
                    break;
                default:
                    Console.WriteLine($"未知参数: {arg}");
                    break;
            }
        }
        return options;
    }
}
