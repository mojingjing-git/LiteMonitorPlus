using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// 外观探针（A04）：**不依赖硬件 / 不依赖驱动**的离屏渲染夹具。
///
/// 与既有性能探针（Program.Main 的默认路径）的区别：
///  - 不创建 <c>HardwareMonitor</c>，不 Open 任何硬件；
///  - 不做 PawnIO / 驱动就绪检查，不安装也不卸载任何驱动；
///  - 不启动 PresentMon / ETW，不下载任何外部程序；
///  - 不读也不写用户常用配置目录（自己 new Settings() 并注入 Settings._instance 单例）。
///
/// 它做的事：用**固定夹具**（固定主题 / 固定监控项集合 / 固定指标值 / 固定显卡简称）
/// 在离屏画布上重复绘制生产渲染器（UIRenderer / HorizontalRenderer），产出：
///  1) 每个夹具一张 PNG（逐像素可比的画面证据）；
///  2) 每个夹具的绘制成本（wall / cpu / allocated）与**原始像素哈希**（前后对比的锚点）；
///  3) 完整失效 / 局部失效场景的等价性证据（合成结果 vs 一次完整绘制）。
///
/// ⚠ 离屏渲染**不包含** DWM / 窗口呈现 / 桌面合成 / 透明度 / 任务栏。
///   这里的 DPI 是**离屏模拟**（见 <see cref="ApplySimulatedDpiFonts"/>），
///   不能替代 v4 §7.2 要求的真实高 DPI 窗口测试。
/// </summary>
internal static class AppearanceProbeRunner
{
    private const string ResultFileName = "appearance-results.json";
    private const string LogFileName = "appearance-run.txt";
    private const string PngFolderName = "png";

    private static readonly StringBuilder Log = new();

    public static int Run(ProbeOptions options)
    {
        Log.Clear();
        Write("LiteMonitor++ 外观探针 (--appearance-only) 启动于 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Write("参数: " + string.Join(' ', options.RawArgs));
        Write("结果目录: " + options.OutputDir);
        Write("隔离输出目录 (AppContext.BaseDirectory): " + AppContext.BaseDirectory);

        var results = new JsonObject
        {
            ["kind"] = "appearance_probe_result",
            ["probeVersion"] = 1,
            ["startedAt"] = DateTime.Now.ToString("o"),
            ["options"] = BuildOptionsJson(options),
            // ★ 隔离声明：这份结果是在"完全绕开硬件路径"的前提下产生的。
            ["isolation"] = new JsonObject
            {
                ["hardwareMonitorCreated"] = false,
                ["hardwareOpened"] = false,
                ["driverChecked"] = false,
                ["driverInstalledOrRemoved"] = false,
                ["externalToolStarted"] = false,
                ["userSettingsRead"] = false,
                ["userSettingsWritten"] = false,
                ["note"] = "本入口不创建 HardwareMonitor、不 Open 硬件、不做驱动就绪检查、不安装/卸载驱动、不启动 PresentMon/ETW、不下载任何外部程序。" +
                          "Settings 由探针自己 new 出来并注入 Settings._instance 单例，因此不读也不写用户常用配置目录。",
            },
            // ★ 画面证据的边界声明：避免把离屏图当成真实窗口证据。
            ["surfaceDeclaration"] = new JsonObject
            {
                ["kind"] = "offscreen-bitmap",
                ["includesDwmOrWindowPresentation"] = false,
                ["includesDesktopComposition"] = false,
                ["includesWindowOpacity"] = false,
                ["includesTaskbar"] = false,
                ["dpiSource"] = "offscreen-simulated",
                ["userScaleSource"] = "offscreen-simulated",
                ["realWindowTestRequired"] = true,
                ["note"] = "离屏渲染不包含 DWM / 窗口呈现 / 桌面合成。DPI 与用户缩放均为**离屏模拟**：布局尺寸走生产同一条路径 " +
                          "Theme.Scale(dpiScale, userScale)，字形点值再乘 dpiScale 以逼近真实高 DPI 窗口的 GDI 光栅化；" +
                          "这不等价于 v4 §7.2 要求的真实 96/120/144/192 DPI 窗口测试。",
            },
        };

        int exitCode = 0;
        try
        {
            var data = AppearanceFixtureData.Load();
            Write($"夹具数据: {data.SourcePath} (sha256 {data.Sha256})");
            results["fixtureData"] = new JsonObject
            {
                ["path"] = data.SourcePath,
                ["sha256"] = data.Sha256,
                ["version"] = data.Version,
            };

            var cfg = BuildIsolatedSettings(data);
            ProbeEnvironment.InstallSettingsSingleton(cfg);
            LanguageManager.Load("en");
            SettleInfoService(data);
            Write($"隔离配置已就绪: PanelWidth={cfg.PanelWidth} MemoryDisplayMode={cfg.MemoryDisplayMode} Skin={cfg.Skin}");

            // 主题清单：动态发现 —— 模型线交付新主题后重建探针即可自动纳入。
            var themes = ThemeManager.GetAvailableThemes().ToList();
            var missingLegacy = data.RequiredLegacyThemes.Where(t => !themes.Contains(t)).ToList();
            results["themes"] = new JsonObject
            {
                ["discovered"] = new JsonArray(themes.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["discoveredCount"] = themes.Count,
                ["requiredLegacy"] = new JsonArray(data.RequiredLegacyThemes.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["missingLegacyThemes"] = new JsonArray(missingLegacy.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["note"] = "主题清单来自探针输出目录 resources/themes/*.json；模型线新增新主题后重新 build 探针即可自动纳入，无需改代码。",
            };
            Write($"发现主题 {themes.Count} 个: {string.Join(", ", themes)}");
            if (missingLegacy.Count > 0)
                Write($"!! 缺少 A00 冻结的旧主题: {string.Join(", ", missingLegacy)}");

            if (themes.Count == 0)
                throw new InvalidOperationException("没有可用主题，无法建立外观基线。");

            var specs = AppearanceFixtureMatrix.Build(data, options.AppearanceFixtureSet, themes);
            if (!string.IsNullOrWhiteSpace(options.AppearanceDecompose))
            {
                // ★ 装饰分解模式：只回答"额外 CPU 花在哪一层"，不跑常规夹具矩阵。
                Write($"模式: 装饰分解测量（主题 {options.AppearanceDecompose}）");
                results["decomposition"] = RunDecompose(data, cfg, options);
                results["fixtureSummary"] = new JsonObject
                {
                    ["mode"] = "decompose-only",
                    ["note"] = "分解模式只跑变体测量，不跑常规夹具矩阵。",
                };
                results["metadata"] = BuildMetadata();
                results["finishedAt"] = DateTime.Now.ToString("o");
                results["log"] = Log.ToString();
                results["exitCode"] = 0;

                Directory.CreateDirectory(options.OutputDir);
                string decomposePath = Path.Combine(options.OutputDir, ResultFileName);
                string decomposeLog = Path.Combine(options.OutputDir, LogFileName);
                File.WriteAllText(decomposePath, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.WriteAllText(decomposeLog, Log.ToString(), new UTF8Encoding(false));
                Write($"结果已写入: {decomposePath}");
                return 0;
            }
            if (!string.IsNullOrWhiteSpace(options.AppearanceFilter))
            {
                var filters = options.AppearanceFilter.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                specs = specs
                    .Where(s => filters.Any(f => s.Id.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                Write($"夹具过滤: {filters.Length} 个关键词 → 命中 {specs.Count} 个夹具");
            }
            Write($"夹具数量: {specs.Count} (set={options.AppearanceFixtureSet})");

            var fixtureArray = new JsonArray();
            int ok = 0, failed = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                try
                {
                    fixtureArray.Add(RenderFixture(spec, data, cfg, options));
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    fixtureArray.Add(new JsonObject
                    {
                        ["kind"] = "appearance_fixture",
                        ["fixtureId"] = spec.Id,
                        ["theme"] = spec.Theme,
                        ["mode"] = spec.Mode,
                        ["dpi"] = spec.Dpi,
                        ["userScale"] = spec.UserScale,
                        ["gpuCount"] = spec.GpuCount,
                        ["panelWidth"] = spec.PanelWidth,
                        ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                    });
                    Write($"!! 夹具失败 {spec.Id}: {ex.GetType().Name}: {ex.Message}");
                    Write($"   堆栈: {ex.StackTrace}");
                }
                if ((i + 1) % 25 == 0 || i + 1 == specs.Count)
                    Write($"  进度 {i + 1}/{specs.Count} (用时 {sw.Elapsed.TotalSeconds:F1}s)");
            }
            results["fixtures"] = fixtureArray;
            results["fixtureSummary"] = new JsonObject
            {
                ["total"] = specs.Count,
                ["succeeded"] = ok,
                ["failed"] = failed,
                ["elapsedSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2),
            };
            results["matrixCoverage"] = BuildMatrixCoverage(specs, data, themes);

            // 完整失效 / 局部失效场景（v4 §7.2）
            if (options.AppearanceInvalidation != "none")
            {
                results["invalidation"] = RunInvalidationScenarios(data, cfg, options, themes);
            }
            else
            {
                results["invalidation"] = new JsonObject
                {
                    ["skipped"] = true,
                    ["note"] = "本次以 --appearance-invalidation none 跳过局部失效场景。",
                };
            }

            // 前后对比（可选）
            if (!string.IsNullOrWhiteSpace(options.CompareDir))
                results["comparison"] = CompareWithBaseline(options.CompareDir, fixtureArray);

            results["metadata"] = BuildMetadata();
        }
        catch (Exception ex)
        {
            Write($"!! 外观探针异常: {ex}");
            results["fatalError"] = $"{ex.GetType().Name}: {ex.Message}";
            results["fatalStack"] = ex.StackTrace;
            exitCode = 1;
        }

        results["finishedAt"] = DateTime.Now.ToString("o");
        results["log"] = Log.ToString();
        results["exitCode"] = exitCode;
        results["exitCodeMeaning"] = "0=正常；1=外观探针异常";

        Directory.CreateDirectory(options.OutputDir);
        string resultPath = Path.Combine(options.OutputDir, ResultFileName);
        string logPath = Path.Combine(options.OutputDir, LogFileName);
        File.WriteAllText(resultPath, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(logPath, Log.ToString(), new UTF8Encoding(false));
        Write($"结果已写入: {resultPath}");
        Write($"日志已写入: {logPath}");

        if (exitCode != 0) Write($"!! 退出码 {exitCode}");
        return exitCode;
    }

    // ==================================================================
    // 隔离配置
    // ==================================================================

    /// <summary>
    /// 探针自己的配置：不读也不写用户常用 settings.json。
    /// MemoryDisplayMode 固定为 0（百分比），避免依赖"探测到的总内存/显存容量"（那是硬件路径）。
    /// </summary>
    private static Settings BuildIsolatedSettings(AppearanceFixtureData data)
    {
        return new Settings
        {
            Language = "en",
            Skin = "DarkFlat_Classic",
            PanelWidth = data.PanelWidth,
            MemoryDisplayMode = 0,     // 百分比模式：不依赖探测到的总容量
            MonitoredGpuCount = 1,
            AlertTempEnabled = false,
            ShowTaskbar = false,
            AutoCheckUpdate = false,
            RefreshMs = 1000,
            WebServerEnabled = false,
            UseWinPerCounters = true,
            GroupAliases = new Dictionary<string, string>(),
            MonitorItems = new List<MonitorItemConfig>(),
        };
    }

    /// <summary>注入固定的 Dashboard 文本，避免画面依赖机器名 / 当前时间 / 开机时长。</summary>
    private static void InjectFixedDashboardValues(AppearanceFixtureData data)
    {
        var svc = InfoService.Instance;
        foreach (var kv in data.DashValues)
            svc.InjectValue(kv.Key, kv.Value);
    }

    /// <summary>
    /// InfoService 的构造函数会异步跑一次 <c>UpdateTimeInfo()</c>，把 "Time" / "Uptime" 写成**真实**值。
    /// 那会破坏夹具的可复现性，所以这里先让它跑完，再把固定值重新注入。
    /// 之后每个夹具在抓取 TextValue 之前还会再注入一次（见 BuildContext），确保不会被异步任务事后覆盖。
    /// </summary>
    private static void SettleInfoService(AppearanceFixtureData data)
    {
        var svc = InfoService.Instance;                     // 触发构造（含一次性的异步初始化）
        Thread.Sleep(400);                                   // 让那次异步初始化跑完
        foreach (var kv in data.DashValues)
            svc.InjectValue(kv.Key, kv.Value);               // 重新注入固定值
    }

    // ==================================================================
    // 单个夹具
    // ==================================================================

    private static JsonObject RenderFixture(AppearanceFixtureSpec spec, AppearanceFixtureData data, Settings cfg, ProbeOptions options)
    {
        var ctx = BuildContext(spec, data, cfg);

        string pngDir = Path.Combine(options.OutputDir, PngFolderName);
        Directory.CreateDirectory(pngDir);
        string pngPath = Path.Combine(pngDir, spec.Id + ".png");

        using var bitmap = new Bitmap(Math.Max(1, ctx.Width), Math.Max(1, ctx.Height), PixelFormat.Format32bppArgb);
        bitmap.SetResolution(spec.Dpi, spec.Dpi);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);

        // 预热：让字体 / 画刷 / 文本测量缓存进入稳态，避免把首帧成本算进测量。
        for (int i = 0; i < 3; i++)
        {
            ApplyFrame(ctx, data, i + 1);
            Draw(g, ctx);
        }

        // 测量：指标值持续变化，保证缓存失效 / 重绘路径被真实走到。
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        int draws = 0;
        for (int i = 0; i < options.AppearanceDraws; i++)
        {
            ApplyFrame(ctx, data, i + 1);
            Draw(g, ctx);
            draws++;
        }
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        process.Refresh();
        double cpuMsPerDraw = (process.TotalProcessorTime.TotalMilliseconds - cpuBefore) / Math.Max(1, draws);

        // A14 配对测量：同一夹具、同一帧数据，旧入口 vs 新入口（v4 §7.4 的装饰层额外 CPU 口径）。
        // ★ 必须放在 ctx.Theme.DisposeFonts() **之前** —— 主题字体一旦被释放，
        //   后续绘制访问 Font.Height 会抛 ArgumentException（绘制层在标题处读字体高度）。
        JsonObject? paired = options.AppearancePaired
            ? MeasurePaired(ctx, data, spec, g, options)
            : null;

        // 最终画面：回到规范状态（第 0 帧），保证 PNG 与像素哈希可复现。
        ApplyFrame(ctx, data, 0);
        Draw(g, ctx);

        bitmap.Save(pngPath, ImageFormat.Png);
        string pixelHash = ComputePixelHash(bitmap);
        string pngHash = ComputeFileSha256(pngPath);

        ctx.Theme.DisposeFonts();

        double wallMsPerDraw = clock.Elapsed.TotalMilliseconds / Math.Max(1, draws);

        return new JsonObject
        {
            ["kind"] = "appearance_fixture",
            ["fixtureId"] = spec.Id,
            ["theme"] = spec.Theme,
            ["themeFileSha256"] = ctx.ThemeFileSha256,
            ["mode"] = spec.Mode,
            ["gpuCount"] = spec.GpuCount,
            ["groupCount"] = ctx.Groups.Count,
            ["itemCount"] = ctx.Items.Count,
            ["width"] = ctx.Width,
            ["height"] = ctx.Height,
            ["dpi"] = spec.Dpi,
            ["userScale"] = spec.UserScale,
            ["panelWidthLogical"] = spec.PanelWidth,
            ["dpiSource"] = "offscreen-simulated",
            ["userScaleSource"] = "offscreen-simulated",
            ["layoutScale"] = Math.Round(ctx.LayoutScale, 4),
            ["draws"] = draws,
            ["wallMsPerDraw"] = Math.Round(wallMsPerDraw, 4),
            ["cpuMsPerDraw"] = Math.Round(cpuMsPerDraw, 4),
            ["allocatedBytesPerDraw"] = Math.Round((double)allocated / Math.Max(1, draws), 1),
            ["paired"] = paired,
            ["projectedWholeMachineCpuAt12_5Hz"] = Math.Round(cpuMsPerDraw * 12.5 / 1000 / Environment.ProcessorCount * 100, 4),
            ["pngPath"] = RelativeToOutput(pngPath, options.OutputDir),
            ["pngSha256"] = pngHash,
            ["pixelSha256"] = pixelHash,
            ["edgeScenario"] = spec.EdgeId == "" ? null : spec.EdgeId,
            ["edgeKind"] = spec.EdgeKind == "" ? null : spec.EdgeKind,
            ["notes"] = new JsonArray(ctx.Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["excludes"] = "DWM、窗口呈现、桌面合成、窗口透明度、任务栏、硬件采集、驱动、插件",
        };
    }

    // ==================================================================
    // A14 配对测量：旧入口 vs 新入口
    // ==================================================================

    /// <summary>
    /// 配对测量（A14 / v4 §7.4）：**同一夹具、同一帧数据**，分别用
    /// 旧入口（不含新外观）与新入口（A13 接线后的生产路径）渲染，
    /// 报告"新入口相对旧入口的额外 CPU 与额外托管分配"。
    /// <para>
    /// ★ 两条路径画的是**同一张图**（固定帧号）→ 差值只反映渲染路径本身的成本。
    /// ★ **帧级交替**：每一帧都先测一条路径、紧接着测另一条，
    ///   这样系统负载 / CPU 频率漂移在**每一对**里都被抵消，而不是只靠轮次平均。
    /// ★ **按轮翻转先后顺序**（第 1 轮 old→new，第 2 轮 new→old），
    ///   消除"谁先画谁受益于缓存"的系统性偏差。
    /// ★ 主判据用**逐帧配对差值的中位数**（抗单帧抖动）；
    ///   Windows 进程 CPU 时间粒度约 15.6ms，不适合判 0.1ms 级差异，故不作主判据。
    /// ★ 托管分配是**硬判据**：v4 §7.4「新效果关闭 → 热路径不增加托管分配」。
    /// </para>
    /// </summary>
    private static JsonObject? MeasurePaired(
        AppearanceContext ctx,
        AppearanceFixtureData data,
        AppearanceFixtureSpec spec,
        Graphics g,
        ProbeOptions options)
    {
        int rounds = Math.Max(1, options.AppearancePairedRounds);
        int draws = Math.Max(1, options.AppearanceDraws);

        using var runtime = new AppearanceRuntime();
        float dpiScale = spec.Dpi / 96f;
        float userScale = (float)spec.UserScale;
        runtime.Update(ctx.Theme, dpiScale, userScale, new Rectangle(0, 0, ctx.Width, ctx.Height));

        // 固定帧号 → 两条路径画的是同一张图
        const int fixedFrame = 1;
        ApplyFrame(ctx, data, fixedFrame);

        var perRound = new JsonArray();
        var allDiffs = new List<double>();
        var oldMedians = new List<double>();
        var newMedians = new List<double>();

        for (int r = 0; r < rounds; r++)
        {
            // 预热（不计入）：两条路径各自的字体 / 画刷 / 纹理缓存都进入稳态
            for (int i = 0; i < 3; i++) { Draw(g, ctx, null); Draw(g, ctx, runtime); }

            var oldSamples = new double[draws];
            var newSamples = new double[draws];
            var diffs = new double[draws];
            bool oldFirst = (r % 2 == 0);

            for (int i = 0; i < draws; i++)
            {
                double oldMs, newMs;
                if (oldFirst)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    Draw(g, ctx, null);
                    long t1 = Stopwatch.GetTimestamp();
                    Draw(g, ctx, runtime);
                    long t2 = Stopwatch.GetTimestamp();
                    oldMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    newMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
                }
                else
                {
                    long t0 = Stopwatch.GetTimestamp();
                    Draw(g, ctx, runtime);
                    long t1 = Stopwatch.GetTimestamp();
                    Draw(g, ctx, null);
                    long t2 = Stopwatch.GetTimestamp();
                    newMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                    oldMs = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
                }
                oldSamples[i] = oldMs;
                newSamples[i] = newMs;
                diffs[i] = newMs - oldMs;
            }

            double oldMedian = Median(oldSamples);
            double newMedian = Median(newSamples);
            double diffMedian = Median(diffs);
            oldMedians.Add(oldMedian);
            newMedians.Add(newMedian);
            allDiffs.AddRange(diffs);

            perRound.Add(new JsonObject
            {
                ["round"] = r + 1,
                ["order"] = oldFirst ? "legacy→wired" : "wired→legacy",
                ["legacyMedianMsPerDraw"] = Round(oldMedian, 4),
                ["wiredMedianMsPerDraw"] = Round(newMedian, 4),
                ["extraMsPerDraw"] = Round(diffMedian, 4),
                ["extraMsPerDrawMean"] = Round(diffs.Average(), 4),
            });
        }

        // 托管分配：单独测（不夹在计时里），两条路径各测一轮
        long legacyAllocPerDraw = MeasureAllocPerDraw(g, ctx, null, draws);
        long wiredAllocPerDraw = MeasureAllocPerDraw(g, ctx, runtime, draws);

        double legacyMedian = oldMedians.Average();
        double wiredMedian = newMedians.Average();
        double extra = Median(allDiffs.ToArray());
        double extraMean = allDiffs.Average();

        return new JsonObject
        {
            ["rounds"] = rounds,
            ["drawsPerRound"] = draws,
            ["legacyMedianMsPerDraw"] = Round(legacyMedian, 4),
            ["wiredMedianMsPerDraw"] = Round(wiredMedian, 4),
            ["extraMsPerDraw"] = Round(extra, 4),
            ["extraMsPerDrawMean"] = Round(extraMean, 4),
            ["extraMsPerDrawP10"] = Round(Percentile(allDiffs, 0.10), 4),
            ["extraMsPerDrawP90"] = Round(Percentile(allDiffs, 0.90), 4),
            ["legacyAllocBytesPerDraw"] = legacyAllocPerDraw,
            ["wiredAllocBytesPerDraw"] = wiredAllocPerDraw,
            ["extraAllocBytesPerDraw"] = wiredAllocPerDraw - legacyAllocPerDraw,
            ["perRound"] = perRound,
            ["frameMode"] = "fixed-frame + 帧级交替（两条路径画同一张图，逐帧配对取差值）",
            ["note"] = "主判据为逐帧配对差值的中位数（离屏渲染为 CPU-bound）；" +
                      "Windows 进程 CPU 时间粒度约 15.6ms，不适合判 0.1ms 级差异，故不作为主判据。" +
                      "托管分配为硬判据：v4 7.4 要求「新效果关闭 → 热路径不增加托管分配」。" +
                      "遗留偏差：先画的那条路径略占便宜，已按轮翻转顺序抵消。",
        };
    }

    /// <summary>只测每帧托管分配（不计时）。</summary>
    private static long MeasureAllocPerDraw(Graphics g, AppearanceContext ctx, AppearanceRuntime? appearance, int draws)
    {
        for (int i = 0; i < 3; i++) Draw(g, ctx, appearance);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < draws; i++) Draw(g, ctx, appearance);
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        return alloc / Math.Max(1, draws);
    }

    /// <summary>中位数（内部会对副本排序，不改调用方数组）。</summary>
    private static double Median(double[] samples)
    {
        if (samples.Length == 0) return 0;
        double[] copy = (double[])samples.Clone();
        Array.Sort(copy);
        int mid = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2.0;
    }

    /// <summary>百分位（线性插值）。</summary>
    private static double Percentile(List<double> samples, double p)
    {
        if (samples.Count == 0) return 0;
        double[] copy = samples.ToArray();
        Array.Sort(copy);
        double pos = p * (copy.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return copy[lo];
        return copy[lo] + (copy[hi] - copy[lo]) * (pos - lo);
    }

    private static double Round(double value, int digits) => Math.Round(value, digits);

    // ==================================================================
    // A14 装饰分解测量：额外 CPU 花在哪一层
    // ==================================================================

    /// <summary>
    /// 装饰分解测量（A14）：把"新入口相对旧入口的额外 CPU"**按层拆开**，
    /// 回答"钱花在哪一层"。
    /// <para>
    /// ★ 变体 00（所有新效果关闭）的额外 CPU 应当 ≈ 0 ——
    ///   它同时是**测量可信度自检**：如果 00 明显不为 0，说明配对测量本身不可信。
    /// </para>
    /// <para>
    /// ★ 变体只改**内存里**的主题对象（不碰用户的主题 JSON 文件）。
    ///   改动只涉及新外观字段（frameStyle / frameWidth / frameLength / scanline /
    ///   glow / grid / barStyle）以及 backdrop.mode —— 前者不参与布局计算，
    ///   所以各变体的**布局与画面结构完全一致**，差值可直接相减。
    /// </para>
    /// </summary>
    private static JsonObject RunDecompose(AppearanceFixtureData data, Settings cfg, ProbeOptions options)
    {
        string themeName = options.AppearanceDecompose;
        var variants = BuildDecomposeVariants();

        // v4 §7.4 的两个判定档位
        var levels = new (string Name, int Gpu, int Dpi, double Scale)[]
        {
            ("普通档(1卡@96/1.0)", 1, 96, 1.0),
            ("4卡两倍档(4卡@96/2.0)", 4, 96, 2.0),
        };

        var levelArray = new JsonArray();
        foreach (var level in levels)
        {
            var variantArray = new JsonArray();
            double previousExtra = 0;

            foreach (var (id, desc, apply) in variants)
            {
                var spec = new AppearanceFixtureSpec
                {
                    Theme = themeName,
                    Mode = "vertical",
                    GpuCount = level.Gpu,
                    Dpi = level.Dpi,
                    UserScale = level.Scale,
                    PanelWidth = data.PanelWidth,
                };

                var ctx = BuildContext(spec, data, cfg);
                apply(ctx.Theme);

                using var bitmap = new Bitmap(Math.Max(1, ctx.Width), Math.Max(1, ctx.Height), PixelFormat.Format32bppArgb);
                bitmap.SetResolution(spec.Dpi, spec.Dpi);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(Color.Transparent);

                JsonObject paired = MeasurePaired(ctx, data, spec, g, options)!;
                ctx.Theme.DisposeFonts();

                double extra = paired["extraMsPerDraw"]?.GetValue<double>() ?? 0;
                // ★ JsonNode 不能被挂到第二个父节点上，必须取出原始值再放。
                variantArray.Add(new JsonObject
                {
                    ["variant"] = id,
                    ["description"] = desc,
                    ["width"] = ctx.Width,
                    ["height"] = ctx.Height,
                    ["legacyMedianMsPerDraw"] = paired["legacyMedianMsPerDraw"]?.GetValue<double>(),
                    ["wiredMedianMsPerDraw"] = paired["wiredMedianMsPerDraw"]?.GetValue<double>(),
                    ["extraMsPerDraw"] = extra,
                    ["deltaFromPreviousMsPerDraw"] = Math.Round(extra - previousExtra, 4),
                    ["extraAllocBytesPerDraw"] = paired["extraAllocBytesPerDraw"]?.GetValue<long>(),
                });
                previousExtra = extra;

                Write($"  {level.Name} / {id}: 额外 {extra:F3} ms/帧, 分配 +{paired["extraAllocBytesPerDraw"]} 字节/帧");
            }

            levelArray.Add(new JsonObject
            {
                ["level"] = level.Name,
                ["variants"] = variantArray,
            });
        }

        return new JsonObject
        {
            ["theme"] = themeName,
            ["levels"] = levelArray,
            ["note"] = "deltaFromPreviousMsPerDraw = 相对上一个变体的增量，即「该层自己的成本」。" +
                       "变体 00 的额外 CPU 应当 ≈ 0（测量可信度自检）。",
        };
    }

    /// <summary>装饰分解的变体定义（按"逐层打开"的顺序）。</summary>
    private static List<(string Id, string Desc, Action<Theme> Apply)> BuildDecomposeVariants()
    {
        return new List<(string, string, Action<Theme>)>
        {
            ("00-全部关闭", "所有新效果关闭（额外 CPU 应当 ≈ 0，兼作测量可信度自检）", t =>
            {
                t.Layout.FrameStyle = 0; t.Layout.FrameWidth = 0; t.Layout.FrameLength = 0;
                t.Layout.Scanline = 0; t.Layout.Glow = 0; t.Layout.Grid = 0; t.Layout.BarStyle = 0;
                if (t.Backdrop != null) t.Backdrop.Mode = "solid";
            }),
            ("01-仅渐变背景", "只开背景渐变（tint 的 opacity=0 → tint 层关闭）", t =>
            {
                t.Layout.FrameStyle = 0; t.Layout.FrameWidth = 0; t.Layout.FrameLength = 0;
                t.Layout.Scanline = 0; t.Layout.Glow = 0; t.Layout.Grid = 0; t.Layout.BarStyle = 0;
            }),
            ("02-加扫描线", "渐变 + 扫描线(30)", t =>
            {
                t.Layout.FrameStyle = 0; t.Layout.FrameWidth = 0; t.Layout.FrameLength = 0;
                t.Layout.Scanline = 30; t.Layout.Glow = 0; t.Layout.Grid = 0; t.Layout.BarStyle = 0;
            }),
            ("03-加网格", "渐变 + 扫描线 + 网格(20)", t =>
            {
                t.Layout.FrameStyle = 0; t.Layout.FrameWidth = 0; t.Layout.FrameLength = 0;
                t.Layout.Scanline = 30; t.Layout.Glow = 0; t.Layout.Grid = 20; t.Layout.BarStyle = 0;
            }),
            ("04-加外框+辉光", "渐变 + 扫描线 + 网格 + 切角外框(3px) + 辉光(55)", t =>
            {
                t.Layout.FrameStyle = 1; t.Layout.FrameWidth = 3; t.Layout.FrameLength = 14;
                t.Layout.Scanline = 30; t.Layout.Glow = 55; t.Layout.Grid = 20; t.Layout.BarStyle = 0;
            }),
            ("05-加渐变进度条", "CyberPunk_Mech 原样（全开）", t => { }),
        };
    }

    // ==================================================================
    // 夹具上下文（主题 / 监控项 / 分组 / 布局）
    // ==================================================================

    private sealed class AppearanceContext
    {
        public Theme Theme = null!;
        public string ThemeFileSha256 = "";
        public bool IsHorizontal;
        public List<GroupLayoutInfo> Groups = new();
        public List<Column> Columns = new();
        public List<MetricItem> Items = new();
        /// <summary>每个指标在画面上的实际绘制矩形（竖屏=item.Bounds；横屏=列的行矩形）。</summary>
        public List<(MetricItem Item, Rectangle Rect)> ItemRects = new();
        public int Width;
        public int Height;
        public float LayoutScale = 1f;
        public List<string> Notes = new();
    }

    private static AppearanceContext BuildContext(AppearanceFixtureSpec spec, AppearanceFixtureData data, Settings cfg)
    {
        var ctx = new AppearanceContext { IsHorizontal = spec.Horizontal };

        // ---- 1) 主题 ----
        var theme = ThemeManager.Load(spec.Theme);
        ctx.Theme = theme;
        ctx.ThemeFileSha256 = ComputeFileSha256(Path.Combine(ThemeManager.ThemeDir, spec.Theme + ".json"));

        // ---- 2) 缩放：与 UIController.ApplyTheme 完全同一条路径 ----
        float dpiScale = spec.Dpi / 96f;
        float userScale = (float)spec.UserScale;
        theme.Scale(dpiScale, userScale);
        ApplySimulatedDpiFonts(theme, userScale, dpiScale);
        float finalScale = dpiScale * userScale;
        ctx.LayoutScale = finalScale;

        if (!spec.Horizontal)
            theme.Layout.Width = (int)(spec.PanelWidth * finalScale);

        // ---- 3) 夹具专用设置（组别名 / 显卡简称 / 监控项） ----
        ApplyEdgeScenario(spec, data, cfg);
        RegisterGpuNames(spec, data);

        var itemConfigs = BuildItemConfigs(spec, data);
        cfg.MonitorItems = itemConfigs;
        cfg.GroupAliases = cfg.GroupAliases ?? new Dictionary<string, string>();

        // ---- 4) 监控项 + 分组（复刻 UIController.BuildMetrics 的分组/排序规则） ----
        var active = itemConfigs
            .Where(x => x.VisibleInPanel)
            .GroupBy(x => x.UIGroup)
            .OrderBy(g => g.Min(x => x.SortIndex))
            .SelectMany(g => g.OrderBy(x => x.SortIndex))
            .ToList();

        var items = new List<MetricItem>();
        // 抓取 Dashboard 文本前再注入一次固定值，杜绝 InfoService 的异步初始化事后覆盖。
        InjectFixedDashboardValues(data);
        foreach (var ic in active)
        {
            var item = new MetricItem { Key = ic.Key, BoundConfig = ic };
            item.Label = LanguageManager.T(UIUtils.Intern("Items." + GpuKey.ToBaseKey(ic.Key)));
            item.ShortLabel = LanguageManager.T(UIUtils.Intern("Short." + GpuKey.ToBaseKey(ic.Key)));

            if (ic.Key.StartsWith("DASH.", StringComparison.Ordinal))
            {
                item.TextValue = InfoService.Instance.GetValue(ic.Key.Substring(5));
                item.Value = null;
            }
            else
            {
                item.Value = 0f;
                item.DisplayValue = 0f;
            }
            items.Add(item);
        }
        ctx.Items = items;

        // 分组：按 UIGroup 连续切段（与生产一致）
        string currentGroup = "";
        List<MetricItem> currentList = new();
        foreach (var item in items)
        {
            string groupKey = item.BoundConfig!.UIGroup;
            if (groupKey != currentGroup && currentList.Count > 0)
            {
                ctx.Groups.Add(new GroupLayoutInfo(currentGroup, currentList) { Label = GroupTitleResolver.Resolve(cfg, currentGroup) });
                currentList = new List<MetricItem>();
            }
            currentGroup = groupKey;
            currentList.Add(item);
        }
        if (currentList.Count > 0)
            ctx.Groups.Add(new GroupLayoutInfo(currentGroup, currentList) { Label = GroupTitleResolver.Resolve(cfg, currentGroup) });

        // ---- 5) 布局 ----
        if (spec.Horizontal)
        {
            // 横屏：复刻 UIController.BuildColumnsCore（双行）
            var cols = new List<Column>();
            for (int i = 0; i < items.Count; i += 2)
                cols.Add(new Column { Top = items[i], Bottom = i + 1 < items.Count ? items[i + 1] : null });

            var hLayout = new HorizontalLayout(theme, theme.Layout.Width, LayoutMode.Horizontal, cfg);
            int h = hLayout.Build(cols);
            ctx.Columns = cols;
            ctx.Width = hLayout.PanelWidth;
            ctx.Height = h;
            if (h <= 0)
            {
                ctx.Notes.Add("横屏零列：HorizontalLayout.Build 返回 0，高度被夹到 1 像素以保证可产出 PNG。");
                ctx.Height = 1;
            }
            ctx.Notes.Add("横屏列间距由 HorizontalLayout 内部按进程**真实系统 DPI** 缩放（Graphics.FromHwnd(IntPtr.Zero)），离屏模拟无法改写；" +
                          "模拟 DPI 与真实系统 DPI 不同时，横屏列间距与真实高 DPI 窗口不一致。");

            // 横屏的指标绘制矩形是列的行矩形（HorizontalLayout 不写 MetricItem.Bounds）
            foreach (var col in cols)
            {
                if (col.Top != null && col.BoundsTop != Rectangle.Empty) ctx.ItemRects.Add((col.Top, col.BoundsTop));
                if (col.Bottom != null && col.BoundsBottom != Rectangle.Empty) ctx.ItemRects.Add((col.Bottom, col.BoundsBottom));
            }
        }
        else
        {
            int h = new UILayout(theme).Build(ctx.Groups);
            ctx.Width = theme.Layout.Width;
            ctx.Height = h;
            if (h <= 0)
            {
                ctx.Notes.Add("竖屏零项：UILayout.Build 返回 0，高度被夹到 1 像素以保证可产出 PNG。");
                ctx.Height = 1;
            }
            foreach (var item in items)
                ctx.ItemRects.Add((item, item.Bounds));
        }

        return ctx;
    }

    /// <summary>
    /// DPI 模拟的字体处理。
    ///
    /// 生产路径 <c>Theme.Scale(dpiScale, userScale)</c> 只把**布局尺寸**乘以 dpiScale*userScale，
    /// 字体点值只乘 userScale —— 真实高 DPI 窗口里，Windows 会用 DC 的 DPI 把点值换算成物理像素。
    /// 离屏画布没有那个换算（GDI 的 LOGFONT 高度在字体创建时就按系统 DPI 定死），
    /// 所以这里显式把字体点值再乘一次 dpiScale，让字形像素尺寸逼近真实高 DPI 窗口。
    ///
    /// ⚠ 这仍然是**模拟**，不是真实高 DPI 窗口测试（v4 §7.2 的真实 DPI 项仍需实窗验证）。
    /// </summary>
    private static void ApplySimulatedDpiFonts(Theme theme, float userScale, float dpiScale)
    {
        if (Math.Abs(dpiScale - 1f) < 0.001f) return;   // 96 DPI：完全走生产原路径

        var style = theme.Font.Bold ? FontStyle.Bold : FontStyle.Regular;
        string valueFamily = string.IsNullOrWhiteSpace(theme.Font.ValueFamily) ? theme.Font.Family : theme.Font.ValueFamily;
        float f = (userScale <= 0 ? 1f : userScale) * dpiScale;

        theme.DisposeFonts();
        theme.FontTitle = new Font(theme.Font.Family, (float)theme.Font.Title * f, style);
        theme.FontGroup = new Font(theme.Font.Family, (float)theme.Font.Group * f, style);
        theme.FontItem = new Font(theme.Font.Family, (float)theme.Font.Item * f, style);
        theme.FontValue = new Font(valueFamily, (float)theme.Font.Value * f, style);
    }

    private static void ApplyEdgeScenario(AppearanceFixtureSpec spec, AppearanceFixtureData data, Settings cfg)
    {
        cfg.GroupAliases = new Dictionary<string, string>();
        if (spec.EdgeId == "") return;

        var edge = data.EdgeScenarios.FirstOrDefault(e => e.Id == spec.EdgeId);
        if (edge == null) return;

        if (edge.GroupAliases != null)
            foreach (var kv in edge.GroupAliases) cfg.GroupAliases[kv.Key] = kv.Value;
    }

    private static void RegisterGpuNames(AppearanceFixtureSpec spec, AppearanceFixtureData data)
    {
        var entries = new GpuInfo[GpuKey.MaxGpuCount];
        var edge = spec.EdgeId == "" ? null : data.EdgeScenarios.FirstOrDefault(e => e.Id == spec.EdgeId);

        for (int i = 0; i < entries.Length; i++)
        {
            var gpu = data.FindGpu(i);
            string shortName = gpu?.ShortName ?? "";
            string fullName = gpu?.FullName ?? "";
            if (edge?.GpuShortNames != null && edge.GpuShortNames.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out string overridden))
                shortName = overridden;
            entries[i] = new GpuInfo { ShortName = shortName, FullName = fullName };
        }
        GpuRegistry.ReplaceAll(entries);
    }

    private static List<MonitorItemConfig> BuildItemConfigs(AppearanceFixtureSpec spec, AppearanceFixtureData data)
    {
        var list = new List<MonitorItemConfig>();
        var edge = spec.EdgeId == "" ? null : data.EdgeScenarios.FirstOrDefault(e => e.Id == spec.EdgeId);

        if (edge?.ClearAllItems == true)
        {
            // 零项场景：保留一个不可见的占位项，保证 MonitorItems 非空但没有任何可见项。
            list.Add(new MonitorItemConfig { Key = "CPU.Load", SortIndex = 201, VisibleInPanel = false, VisibleInTaskbar = false });
            return list;
        }

        foreach (var itemSpec in data.Items)
            list.Add(new MonitorItemConfig
            {
                Key = itemSpec.Key,
                SortIndex = itemSpec.SortIndex,
                VisibleInPanel = itemSpec.VisibleInPanel,
                VisibleInTaskbar = itemSpec.VisibleInTaskbar,
            });

        for (int card = 0; card < spec.GpuCount; card++)
        {
            foreach (var itemSpec in data.GpuItems)
            {
                list.Add(new MonitorItemConfig
                {
                    Key = itemSpec.Key.Replace("{card}", card.ToString(CultureInfo.InvariantCulture)),
                    SortIndex = itemSpec.SortIndex + card * 10,
                    VisibleInPanel = itemSpec.VisibleInPanel,
                    VisibleInTaskbar = itemSpec.VisibleInTaskbar,
                });
            }
        }

        if (edge?.UserLabels != null)
            foreach (var kv in edge.UserLabels)
            {
                var target = list.FirstOrDefault(x => x.Key == kv.Key);
                if (target != null) target.UserLabel = kv.Value;
            }
        if (edge?.TaskbarLabels != null)
            foreach (var kv in edge.TaskbarLabels)
            {
                var target = list.FirstOrDefault(x => x.Key == kv.Key);
                if (target != null) target.TaskbarLabel = kv.Value;
            }

        return list;
    }

    // ==================================================================
    // 绘制
    // ==================================================================

    private static void ApplyFrame(AppearanceContext ctx, AppearanceFixtureData data, int frame)
    {
        for (int i = 0; i < ctx.Items.Count; i++)
        {
            var item = ctx.Items[i];
            if (item.TextValue != null) continue;    // Dashboard 文本项固定
            float v = (float)AnimatedValue(data, i, frame);
            item.Value = v;
            item.DisplayValue = v;
        }
    }

    private static double AnimatedValue(AppearanceFixtureData data, int itemIndex, int frame)
    {
        double span = data.ValueMax - data.ValueBase;
        if (span <= 0) return data.ValueBase;
        double t = (itemIndex * data.ItemOffset + frame * data.ValueStep) % span;
        if (t < 0) t += span;
        return data.ValueBase + t;
    }

    private static void Draw(Graphics g, AppearanceContext ctx, AppearanceRuntime? appearance = null)
    {
        if (ctx.IsHorizontal)
            HorizontalRenderer.Render(g, ctx.Theme, ctx.Columns, ctx.Width, appearance);
        else
            UIRenderer.Render(g, ctx.Groups, ctx.Theme, appearance);
    }

    // ==================================================================
    // 完整 / 局部失效场景
    // ==================================================================

    private static JsonObject RunInvalidationScenarios(AppearanceFixtureData data, Settings cfg, ProbeOptions options, IReadOnlyList<string> themes)
    {
        var scenarios = new JsonArray();
        int frames = data.InvalidationFrames;

        // 每个主题 × 两种模式 × 1/4 卡，在参考 DPI/缩放下各跑一轮。
        var specs = new List<AppearanceFixtureSpec>();
        foreach (string theme in themes)
        {
            if (options.AppearanceInvalidation == "sample" && specs.Count > 0) break;
            foreach (string mode in data.Modes)
            foreach (int gpu in data.GpuCounts)
                specs.Add(new AppearanceFixtureSpec
                {
                    Theme = theme,
                    Mode = mode,
                    GpuCount = gpu,
                    Dpi = data.ReferenceDpi,
                    UserScale = data.ReferenceUserScale,
                    PanelWidth = data.PanelWidth,
                });
        }

        int matchCount = 0, mismatchCount = 0;
        var perVariant = new Dictionary<string, (int Match, int Mismatch)>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            try
            {
                var rec = RunInvalidationScenario(spec, data, cfg, frames);
                scenarios.Add(rec);
                if (rec["allVariantsMatch"]?.GetValue<bool>() ?? false) matchCount++; else mismatchCount++;

                if (rec["variants"] is JsonArray vars)
                    foreach (var v in vars)
                    {
                        if (v is not JsonObject vo) continue;
                        string name = vo["variant"]?.GetValue<string>() ?? "?";
                        bool ok = vo["match"]?.GetValue<bool>() ?? false;
                        perVariant.TryGetValue(name, out var agg);
                        perVariant[name] = ok ? (agg.Match + 1, agg.Mismatch) : (agg.Match, agg.Mismatch + 1);
                    }
            }
            catch (Exception ex)
            {
                scenarios.Add(new JsonObject
                {
                    ["fixtureId"] = spec.Id,
                    ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                });
                mismatchCount++;
            }
        }

        var variantSummary = new JsonObject();
        foreach (var kv in perVariant.OrderBy(k => k.Key, StringComparer.Ordinal))
            variantSummary[kv.Key] = new JsonObject { ["match"] = kv.Value.Match, ["mismatch"] = kv.Value.Mismatch };

        return new JsonObject
        {
            ["kind"] = "appearance_invalidation",
            ["frames"] = frames,
            ["fixedRect"] = $"{data.InvalidationFixedRect}",
            ["clipInflation"] = data.ClipInflation,
            ["scenarioCount"] = specs.Count,
            ["allMatched"] = matchCount,
            ["someMismatch"] = mismatchCount,
            ["variantSummary"] = variantSummary,
            ["variants"] = scenarios,
            ["diagnosis"] = BuildInvalidationDiagnosis(variantSummary),
            ["note"] = "itemDriven：每帧只改一个指标的值，失效区 = 该指标绘制矩形（外扩 clipInflation 像素以覆盖字形抗锯齿），" +
                      "连续 frames 次局部重绘后，合成结果必须与「一次完整绘制」逐像素一致。" +
                      "fixedRect：v4 §7.2 点名的固定失效矩形，连续局部重绘后的合成结果必须与「一次完整绘制」逐像素一致。" +
                      "两个变体各带一个 *WithBackgroundPrefill 版本：先在失效区补一层背景底色再局部重绘，用来区分" +
                      "「渲染器本身不重绘失效区底色」与「装饰层自身画错」两类原因。",
        };
    }

    /// <summary>根据各变体的通过情况给出结论（只陈述实测到的现象，不臆断修复方案）。</summary>
    private static string BuildInvalidationDiagnosis(JsonObject variantSummary)
    {
        bool plainMismatch = GetCount(variantSummary, "itemDriven", "mismatch") > 0;
        bool prefillOk = GetCount(variantSummary, "itemDrivenWithBackgroundPrefill", "mismatch") == 0;
        if (!plainMismatch) return "所有局部失效场景在不改生产代码的情况下即已逐像素一致。";
        if (prefillOk)
            return "已定位根因：**渲染器在局部失效时没有重画失效区的底色**。" +
                   "UIRenderer 的背景填充是 Rectangle(-5,-5, VisibleClipBounds.Width+10, VisibleClipBounds.Height+10)，" +
                   "即「以窗口原点为锚点、按失效区尺寸」——失效区不在原点时，底色根本不会被重画，" +
                   "组块圆角的 GDI+ 抗锯齿边与旧像素二次叠加（竖屏），旧文本残留在新文本变短时露出（横屏）。" +
                   "证据：先在失效区补一层背景底色再局部重绘（*WithBackgroundPrefill 变体），全部场景与「一次完整绘制」逐像素一致。" +
                   "这是**改造前就存在**的渲染器行为（HEAD 97f793d 的 UIRenderer / HorizontalRenderer 代码未变），" +
                   "但新增的半透明装饰 / 玻璃背景会把它放大，因此 A07/A08/A13 必须保证失效区底色被重画。";
        return "局部失效场景存在差异，且补背景底色后仍有差异 —— 需要进一步定位（不是单纯的底色未重画问题）。";
    }

    private static int GetCount(JsonObject summary, string variant, string field)
    {
        if (summary[variant] is not JsonObject o) return 0;
        return o[field]?.GetValue<int>() ?? 0;
    }

    private static JsonObject RunInvalidationScenario(AppearanceFixtureSpec spec, AppearanceFixtureData data, Settings cfg, int frames)
    {
        var ctx = BuildContext(spec, data, cfg);
        int w = Math.Max(1, ctx.Width), h = Math.Max(1, ctx.Height);

        var variants = new JsonArray
        {
            RunItemDrivenInvalidation(ctx, data, w, h, frames, prefillBackground: false),
            RunItemDrivenInvalidation(ctx, data, w, h, frames, prefillBackground: true),
            RunFixedRectInvalidation(ctx, data, w, h, frames, prefillBackground: false),
            RunFixedRectInvalidation(ctx, data, w, h, frames, prefillBackground: true),
        };

        bool all = variants.All(v => v?["match"]?.GetValue<bool>() ?? false);
        ctx.Theme.DisposeFonts();

        return new JsonObject
        {
            ["fixtureId"] = spec.Id,
            ["theme"] = spec.Theme,
            ["mode"] = spec.Mode,
            ["gpuCount"] = spec.GpuCount,
            ["width"] = w,
            ["height"] = h,
            ["allVariantsMatch"] = all,
            ["variants"] = variants,
        };
    }

    /// <summary>按指标驱动的局部失效：每帧只改一个指标，失效区 = 该指标绘制矩形。</summary>
    private static JsonObject RunItemDrivenInvalidation(AppearanceContext ctx, AppearanceFixtureData data, int w, int h, int frames, bool prefillBackground)
    {
        // 可动画的指标（Dashboard 文本项固定，不参与）
        var animated = ctx.Items.Where(i => i.TextValue == null).ToList();
        int n = animated.Count;

        if (n == 0)
        {
            return new JsonObject
            {
                ["variant"] = prefillBackground ? "itemDrivenWithBackgroundPrefill" : "itemDriven",
                ["match"] = true,
                ["diffPixelCount"] = 0,
                ["skipped"] = true,
                ["note"] = "没有可动画的指标，局部失效场景不适用。",
            };
        }

        var rectOf = ctx.ItemRects.ToDictionary(r => r.Item, r => r.Rect);

        using var composite = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var reference = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        // 初始状态：第 0 帧
        ApplyFrame(ctx, data, 0);
        using (var g = Graphics.FromImage(composite)) Draw(g, ctx);

        var lastFrame = new int[n];
        for (int k = 1; k <= frames; k++)
        {
            int idx = (k - 1) % n;
            lastFrame[idx] = k;
            var item = animated[idx];
            float v = (float)AnimatedValue(data, ctx.Items.IndexOf(item), k);
            item.Value = v;
            item.DisplayValue = v;

            Rectangle clip = rectOf.TryGetValue(item, out var r) ? Inflate(r, data.ClipInflation, w, h) : Rectangle.Empty;
            if (clip.Width <= 0 || clip.Height <= 0) continue;

            using var g = Graphics.FromImage(composite);
            g.SetClip(clip);
            if (prefillBackground)
                using (var bg = new SolidBrush(ThemeManager.ParseColor(ctx.Theme.Color.Background)))
                    g.FillRectangle(bg, clip);
            Draw(g, ctx);
        }

        // 参考：把每个指标置为它的最终值，做一次完整绘制
        for (int i = 0; i < n; i++)
        {
            var item = animated[i];
            int k = lastFrame[i];
            float v = (float)AnimatedValue(data, ctx.Items.IndexOf(item), k);
            item.Value = v;
            item.DisplayValue = v;
        }
        using (var g = Graphics.FromImage(reference)) Draw(g, ctx);

        var diff = CompareBitmaps(composite, reference);
        return new JsonObject
        {
            ["variant"] = prefillBackground ? "itemDrivenWithBackgroundPrefill" : "itemDriven",
            ["match"] = diff.Count == 0,
            ["diffPixelCount"] = diff.Count,
            ["diffBounds"] = diff.Count == 0 ? null : diff.Bounds.ToString(),
            ["frames"] = frames,
            ["animatedItemCount"] = n,
            ["note"] = diff.Count == 0
                ? "合成结果与「一次完整绘制」逐像素一致。"
                : "合成结果与「一次完整绘制」**不一致** —— 局部失效后画面有残留差异（局部失效区没有把底色重画一遍，叠在旧像素上）。",
        };
    }

    /// <summary>固定失效矩形：v4 §7.2 点名的 Rectangle(37,43,20,20)。</summary>
    private static JsonObject RunFixedRectInvalidation(AppearanceContext ctx, AppearanceFixtureData data, int w, int h, int frames, bool prefillBackground)
    {
        using var composite = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var reference = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var rect = data.InvalidationFixedRect;
        var clip = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        bool clipInside = clip.Left >= 0 && clip.Top >= 0 && clip.Right <= w && clip.Bottom <= h;

        // 该矩形覆盖到哪些指标（仅作说明；固定矩形场景不改指标值，保证"合成 vs 一次完整绘制"可比）
        var covered = ctx.ItemRects
            .Where(r => r.Rect.IntersectsWith(clip))
            .Select(r => r.Item.Key)
            .ToList();

        ApplyFrame(ctx, data, 0);
        using (var g = Graphics.FromImage(composite)) Draw(g, ctx);

        for (int k = 1; k <= frames; k++)
        {
            using var g = Graphics.FromImage(composite);
            if (clipInside)
            {
                g.SetClip(clip);
                if (prefillBackground)
                    using (var bg = new SolidBrush(ThemeManager.ParseColor(ctx.Theme.Color.Background)))
                        g.FillRectangle(bg, clip);
                Draw(g, ctx);
            }
        }

        using (var g = Graphics.FromImage(reference)) Draw(g, ctx);

        var diff = CompareBitmaps(composite, reference);
        return new JsonObject
        {
            ["variant"] = prefillBackground ? "fixedRectWithBackgroundPrefill" : "fixedRect",
            ["match"] = diff.Count == 0,
            ["diffPixelCount"] = diff.Count,
            ["diffBounds"] = diff.Count == 0 ? null : diff.Bounds.ToString(),
            ["frames"] = frames,
            ["fixedRect"] = $"{rect}",
            ["rectInsideCanvas"] = clipInside,
            ["overlappingItemKeys"] = new JsonArray(covered.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
            ["note"] = !clipInside
                ? "固定失效矩形超出画布，本次只做完整重绘对比（无实际局部失效）。"
                : (diff.Count == 0
                    ? "固定失效矩形连续重绘后的合成结果与「一次完整绘制」逐像素一致。"
                    : "固定失效矩形连续重绘后的合成结果与「一次完整绘制」**不一致** —— 失效区底色没有被重画，文字抗锯齿在旧像素上二次叠加。"),
        };
    }

    private static Rectangle Inflate(Rectangle r, int inflate, int w, int h)
    {
        if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
        var outR = Rectangle.Inflate(r, inflate, inflate);
        outR.X = Math.Max(0, outR.X);
        outR.Y = Math.Max(0, outR.Y);
        outR.Width = Math.Min(outR.Width, w - outR.X);
        outR.Height = Math.Min(outR.Height, h - outR.Y);
        if (outR.Width < 0) outR.Width = 0;
        if (outR.Height < 0) outR.Height = 0;
        return outR;
    }

    // ==================================================================
    // 像素工具
    // ==================================================================

    private readonly struct BitmapDiff
    {
        public BitmapDiff(int count, Rectangle bounds) { Count = count; Bounds = bounds; }
        public int Count { get; }
        public Rectangle Bounds { get; }
    }

    private static BitmapDiff CompareBitmaps(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return new BitmapDiff(-1, Rectangle.Empty);

        var rect = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = da.Stride;
            int bytes = stride * a.Height;
            var ba = new byte[bytes];
            var bb = new byte[bytes];
            Marshal.Copy(da.Scan0, ba, 0, bytes);
            Marshal.Copy(db.Scan0, bb, 0, bytes);

            int count = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < a.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < a.Width; x++)
                {
                    int i = row + x * 4;
                    if (ba[i] == bb[i] && ba[i + 1] == bb[i + 1] && ba[i + 2] == bb[i + 2] && ba[i + 3] == bb[i + 3]) continue;
                    count++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
            if (count == 0) return new BitmapDiff(0, Rectangle.Empty);
            return new BitmapDiff(count, new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1));
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }

    /// <summary>原始像素（未编码）哈希 —— 前后逐像素对比的锚点。</summary>
    private static string ComputePixelHash(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = data.Stride * bmp.Height;
            var buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(buf));
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static string ComputeFileSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception ex)
        {
            return $"unavailable: {ex.GetType().Name}";
        }
    }

    // ==================================================================
    // 前后对比
    // ==================================================================

    private static JsonObject CompareWithBaseline(string baselineDir, JsonArray currentFixtures)
    {
        string baselinePath = Path.Combine(baselineDir, ResultFileName);
        if (!File.Exists(baselinePath))
            return new JsonObject
            {
                ["error"] = $"基线结果不存在: {baselinePath}",
                ["baselineDir"] = baselineDir,
            };

        var baseline = JsonNode.Parse(File.ReadAllText(baselinePath)) as JsonObject;
        var baselineFixtures = baseline?["fixtures"] as JsonArray;
        if (baselineFixtures == null)
            return new JsonObject { ["error"] = "基线结果里没有 fixtures 数组。", ["baselinePath"] = baselinePath };

        var baselineMap = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in baselineFixtures)
            if (node is JsonObject o && o["fixtureId"]?.GetValue<string>() is { } id)
                baselineMap[id] = o;

        var currentMap = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in currentFixtures)
            if (node is JsonObject o && o["fixtureId"]?.GetValue<string>() is { } id)
                currentMap[id] = o;

        int identical = 0, changed = 0, missing = 0, added = 0;
        var details = new JsonArray();

        foreach (var kv in currentMap)
        {
            if (!baselineMap.TryGetValue(kv.Key, out var b))
            {
                added++;
                details.Add(new JsonObject { ["fixtureId"] = kv.Key, ["status"] = "newInCurrent" });
                continue;
            }
            string? curHash = kv.Value["pixelSha256"]?.GetValue<string>();
            string? baseHash = b["pixelSha256"]?.GetValue<string>();
            bool same = string.Equals(curHash, baseHash, StringComparison.Ordinal);
            if (same) identical++; else changed++;
            details.Add(new JsonObject
            {
                ["fixtureId"] = kv.Key,
                ["status"] = same ? "identical" : "changed",
                ["baselinePixelSha256"] = baseHash,
                ["currentPixelSha256"] = curHash,
                ["baselineSize"] = $"{b["width"]?.GetValue<int>()}x{b["height"]?.GetValue<int>()}",
                ["currentSize"] = $"{kv.Value["width"]?.GetValue<int>()}x{kv.Value["height"]?.GetValue<int>()}",
            });
        }

        foreach (var kv in baselineMap)
        {
            if (currentMap.ContainsKey(kv.Key)) continue;
            missing++;
            details.Add(new JsonObject { ["fixtureId"] = kv.Key, ["status"] = "missingInCurrent" });
        }

        return new JsonObject
        {
            ["baselinePath"] = baselinePath,
            ["baselineDir"] = baselineDir,
            ["identical"] = identical,
            ["changed"] = changed,
            ["newInCurrent"] = added,
            ["missingInCurrent"] = missing,
            ["allIdentical"] = changed == 0 && added == 0 && missing == 0,
            ["details"] = details,
            ["note"] = "逐像素对比以 pixelSha256（原始 ARGB 像素哈希）为准；PNG 文件哈希只作为附加证据。",
        };
    }

    // ==================================================================
    // 元数据 / 杂项
    // ==================================================================

    /// <summary>夹具矩阵覆盖情况：让复核者一眼看清每个维度覆盖到了哪些取值。</summary>
    private static JsonObject BuildMatrixCoverage(List<AppearanceFixtureSpec> specs, AppearanceFixtureData data, IReadOnlyList<string> themes)
    {
        var core = specs.Where(s => s.EdgeId == "").ToList();
        var edge = specs.Where(s => s.EdgeId != "").ToList();

        return new JsonObject
        {
            ["themeCount"] = themes.Count,
            ["themes"] = new JsonArray(themes.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["modes"] = new JsonArray(data.Modes.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["gpuCounts"] = new JsonArray(data.GpuCounts.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
            ["dpis"] = new JsonArray(data.Dpis.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["userScales"] = new JsonArray(data.UserScales.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()),
            ["coreFixtureCount"] = core.Count,
            ["edgeFixtureCount"] = edge.Count,
            ["edgeScenarios"] = new JsonArray(data.EdgeScenarios.Select(e => (JsonNode)JsonValue.Create(e.Id + " (" + e.Kind + ")")!).ToArray()),
            ["distinctDpiValues"] = new JsonArray(core.Select(s => s.Dpi).Distinct().OrderBy(x => x).Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["distinctUserScaleValues"] = new JsonArray(core.Select(s => s.UserScale).Distinct().OrderBy(x => x).Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()),
            ["distinctPanelWidths"] = new JsonArray(specs.Select(s => s.PanelWidth).Distinct().OrderBy(x => x).Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
        };
    }

    private static JsonObject BuildOptionsJson(ProbeOptions options)    {
        return new JsonObject
        {
            ["outputDir"] = options.OutputDir,
            ["appearanceFixtureSet"] = options.AppearanceFixtureSet,
            ["appearanceDraws"] = options.AppearanceDraws,
            ["appearanceInvalidation"] = options.AppearanceInvalidation,
            ["compareDir"] = options.CompareDir == "" ? null : options.CompareDir,
        };
    }

    private static JsonObject BuildMetadata()
    {
        return new JsonObject
        {
            ["probeUtc"] = DateTime.UtcNow.ToString("o"),
            ["machineName"] = Environment.MachineName,
            ["os"] = Environment.OSVersion.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["runtimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["baseDirectory"] = AppContext.BaseDirectory,
            ["gitHead"] = GitHeadResolver.Resolve(),
            ["mainAssemblyPath"] = typeof(Settings).Assembly.Location,
            ["mainAssemblySha256"] = ComputeFileSha256(typeof(Settings).Assembly.Location),
            ["probeAssemblyPath"] = typeof(AppearanceProbeRunner).Assembly.Location,
            ["probeAssemblySha256"] = ComputeFileSha256(typeof(AppearanceProbeRunner).Assembly.Location),
            ["systemDpiX"] = ReadSystemDpiX(),
            ["systemDpiNote"] = "进程真实系统 DPI（Graphics.FromHwnd(IntPtr.Zero)）。离屏模拟的 DPI 与它不同，因此字形光栅化只是近似。",
        };
    }

    private static double ReadSystemDpiX()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX;
        }
        catch { return 0; }
    }

    private static string RelativeToOutput(string path, string outputDir)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(outputDir);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, '/');
        }
        catch { }
        return path;
    }

    private static void Write(string message)
    {
        Console.WriteLine(message);
        Log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
