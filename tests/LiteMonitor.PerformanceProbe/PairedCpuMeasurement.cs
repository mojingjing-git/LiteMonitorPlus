using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace LiteMonitor.PerformanceProbe;

/// <summary>
/// A14 配对测量的 **CPU 时间口径**部分（N1「口径混用」修正）。
///
/// ★ 为什么需要这一段：
///   既有的 <c>MeasurePaired</c> 用 <c>Stopwatch</c> 测差值，得到的是**经过时间**
///   （wall clock / 延迟），**不是 CPU 时间**（处理器时间）。
///   验收文档曾把它当成「额外 CPU 时间」，并据此推算「整机 CPU 百分比」—— 那是口径错误：
///     - CPU 预算（≤ 40 ms/秒）必须按 **CPU 时间** 判断；
///     - p95 处理延迟（≤ 30 ms）必须按 **经过时间** 判断。
///   本文件提供 CPU 时间口径的配对测量，让两种口径能被区分开、各自用对地方。
///
/// ★ Windows 上 <c>Process.TotalProcessorTime</c> 的量化步长约 15.6 ms，单帧绝对值不可用：
///     - 每批次绘制次数足够多（默认 2000 次），使 CPU 差值远超量化步长；
///     - **同进程同刻配对**：同一段内背靠背测旧入口与新入口，而不是跨批次比绝对值；
///     - **交替执行顺序**：段内交替 + 逐轮翻转起始顺序，抵消缓存/预热带来的系统性偏差；
///     - **至少 3 轮**，逐轮记录差值，并给出均值与轮间离散度（极差），
///       让「差异是否显著大于噪声」可被判断；
///     - 差值落在噪声量级以内时，如实报告「不可判定」，**不报告成 0**。
/// </summary>
internal static partial class AppearanceProbeRunner
{
    /// <summary>Windows 进程 CPU 时间的典型量化步长（毫秒）：内核按时钟粒度记账，约 15.625 ms。</summary>
    private const double CpuTimeQuantizationStepMs = 15.625;

    /// <summary>最小可判定差值下限（毫秒/次）：差值绝对值小于它一律按「在测量噪声内、不可判定」处理。</summary>
    private const double CpuDiffNoiseFloorMs = 0.1;

    /// <summary>整机占用推算用的刷新率（帧/秒）。</summary>
    private const double ProjectionFrameRateHz = 12.5;

    /// <summary>
    /// 本次运行里已经做过的 CPU 口径配对测量次数。
    /// 受 <c>--appearance-paired-cpu-max-measurements</c> 限制：矩阵流程里 CPU 测量很贵，
    /// 默认只做第一个夹具，其余夹具的 CPU 段如实标注「未测量」。
    /// </summary>
    private static int _pairedCpuMeasurementsDone;

    /// <summary>一次批次的测量结果（CPU 时间 + 经过时间）。</summary>
    private readonly struct BatchResult
    {
        public BatchResult(double cpuMs, double elapsedMs, int draws)
        {
            CpuMs = cpuMs;
            ElapsedMs = elapsedMs;
            Draws = draws;
        }

        /// <summary>批次的 CPU 时间（毫秒，处理器时间）。</summary>
        public double CpuMs { get; }
        /// <summary>批次的经过时间（毫秒，墙钟）。</summary>
        public double ElapsedMs { get; }
        /// <summary>批次的绘制次数。</summary>
        public int Draws { get; }
        /// <summary>每次绘制的 CPU 时间（毫秒）。</summary>
        public double CpuMsPerDraw => CpuMs / Math.Max(1, Draws);
        /// <summary>每次绘制的经过时间（毫秒）。</summary>
        public double ElapsedMsPerDraw => ElapsedMs / Math.Max(1, Draws);
    }

    // ==================================================================
    // CPU 时间口径的配对测量
    // ==================================================================

    /// <summary>
    /// CPU 口径配对测量：同一夹具、同一帧数据，旧入口 vs 新入口的 **处理器时间** 差值。
    /// <para>
    /// ★ 测量方法（针对 Windows 约 15.6 ms 的 CPU 时间量化步长）：
    ///   1) 每批 <c>--appearance-paired-cpu-draws</c> 次绘制，批次前后取
    ///      <c>Process.TotalProcessorTime</c> 的差值 → 除以绘制次数 → 每帧 CPU 毫秒；
    ///   2) 同一段内**背靠背**测旧入口与新入口（同进程同刻配对），抵消机器负载漂移；
    ///   3) 段内**交替**先后顺序，且**逐轮翻转**起始顺序，抵消「谁先画谁受益于缓存」的偏差；
    ///   4) 逐轮记录 old/new 的 CPU 差值与经过时间差值，并给出均值与轮间离散度；
    ///   5) 差值落在噪声量级以内 → 如实报告「不可判定」（字段为 null，不是 0）。
    /// </para>
    /// </summary>
    private static JsonObject MeasurePairedCpu(
        AppearanceContext ctx,
        AppearanceFixtureSpec spec,
        Graphics g,
        AppearanceRuntime runtime,
        ProbeOptions options)
    {
        int draws = Math.Max(1, options.AppearancePairedCpuDraws);
        int segments = Math.Max(1, options.AppearancePairedCpuSegments);
        // ★ 至少 3 轮：轮间离散度（极差）需要至少 3 个样本才有意义。
        int rounds = Math.Max(3, options.AppearancePairedRounds);

        using var process = Process.GetCurrentProcess();

        var perRound = new JsonArray();
        var cpuDiffs = new List<double>();        // 每轮「新 - 旧」的 CPU 时间差（毫秒/次）
        var elapsedDiffs = new List<double>();    // 每轮「新 - 旧」的经过时间差（毫秒/次）
        var legacyCpu = new List<double>();
        var wiredCpu = new List<double>();
        var legacyElapsed = new List<double>();
        var wiredElapsed = new List<double>();

        for (int r = 0; r < rounds; r++)
        {
            // 预热（不计入）：两条路径各自的字体 / 画刷 / 纹理缓存都进入稳态
            for (int i = 0; i < 3; i++) { Draw(g, ctx, null); Draw(g, ctx, runtime); }

            // ★ 逐轮翻转起始顺序：奇数轮 旧→新 起始，偶数轮 新→旧 起始
            int phase = r % 2;
            double sumLegacyCpu = 0, sumWiredCpu = 0, sumLegacyElapsed = 0, sumWiredElapsed = 0;
            int totalDraws = 0;
            var segmentArray = new JsonArray();

            for (int s = 0; s < segments; s++)
            {
                // ★ 段内交替：相邻两段的先后顺序相反，一轮内部就把顺序偏差抵消掉
                bool legacyFirst = ((s + phase) % 2 == 0);

                BatchResult legacy, wired;
                if (legacyFirst)
                {
                    legacy = MeasureBatch(g, ctx, null, draws, process);
                    wired = MeasureBatch(g, ctx, runtime, draws, process);
                }
                else
                {
                    wired = MeasureBatch(g, ctx, runtime, draws, process);
                    legacy = MeasureBatch(g, ctx, null, draws, process);
                }

                sumLegacyCpu += legacy.CpuMs;
                sumWiredCpu += wired.CpuMs;
                sumLegacyElapsed += legacy.ElapsedMs;
                sumWiredElapsed += wired.ElapsedMs;
                totalDraws += draws;

                segmentArray.Add(new JsonObject
                {
                    ["segment"] = s + 1,
                    ["order"] = legacyFirst ? "legacy→wired" : "wired→legacy",
                    ["legacyCpuMsPerDraw"] = Round(legacy.CpuMsPerDraw, 4),
                    ["wiredCpuMsPerDraw"] = Round(wired.CpuMsPerDraw, 4),
                    ["cpuDiffMsPerDraw"] = Round(wired.CpuMsPerDraw - legacy.CpuMsPerDraw, 4),
                    ["legacyBatchElapsedMsPerDraw"] = Round(legacy.ElapsedMsPerDraw, 4),
                    ["wiredBatchElapsedMsPerDraw"] = Round(wired.ElapsedMsPerDraw, 4),
                    ["elapsedDiffMsPerDraw"] = Round(wired.ElapsedMsPerDraw - legacy.ElapsedMsPerDraw, 4),
                    ["legacyBatchCpuMs"] = Round(legacy.CpuMs, 2),
                    ["wiredBatchCpuMs"] = Round(wired.CpuMs, 2),
                });
            }

            double roundLegacyCpu = sumLegacyCpu / Math.Max(1, totalDraws);
            double roundWiredCpu = sumWiredCpu / Math.Max(1, totalDraws);
            double roundLegacyElapsed = sumLegacyElapsed / Math.Max(1, totalDraws);
            double roundWiredElapsed = sumWiredElapsed / Math.Max(1, totalDraws);
            double roundCpuDiff = roundWiredCpu - roundLegacyCpu;
            double roundElapsedDiff = roundWiredElapsed - roundLegacyElapsed;

            legacyCpu.Add(roundLegacyCpu);
            wiredCpu.Add(roundWiredCpu);
            legacyElapsed.Add(roundLegacyElapsed);
            wiredElapsed.Add(roundWiredElapsed);
            cpuDiffs.Add(roundCpuDiff);
            elapsedDiffs.Add(roundElapsedDiff);

            perRound.Add(new JsonObject
            {
                ["round"] = r + 1,
                ["order"] = phase == 0 ? "legacy→wired 起始" : "wired→legacy 起始",
                ["segments"] = segments,
                ["drawsPerSegmentPerPath"] = draws,
                ["drawsPerRoundPerPath"] = totalDraws,
                ["legacyCpuMsPerDraw"] = Round(roundLegacyCpu, 4),
                ["wiredCpuMsPerDraw"] = Round(roundWiredCpu, 4),
                ["cpuDiffMsPerDraw"] = Round(roundCpuDiff, 4),
                ["legacyBatchElapsedMsPerDraw"] = Round(roundLegacyElapsed, 4),
                ["wiredBatchElapsedMsPerDraw"] = Round(roundWiredElapsed, 4),
                ["elapsedDiffMsPerDraw"] = Round(roundElapsedDiff, 4),
                ["segmentsDetail"] = segmentArray,
            });
        }

        // ---- 轮间离散度与噪声判定 ----
        double cpuMean = cpuDiffs.Average();
        double cpuMin = cpuDiffs.Min();
        double cpuMax = cpuDiffs.Max();
        double cpuRange = cpuMax - cpuMin;
        double elapsedMean = elapsedDiffs.Average();
        double elapsedMin = elapsedDiffs.Min();
        double elapsedMax = elapsedDiffs.Max();
        double elapsedRange = elapsedMax - elapsedMin;

        // 单批次 CPU 差值的量化误差上限约 15.625 ms，折算到「每次绘制」。
        double quantizationPerDraw = CpuTimeQuantizationStepMs / draws;
        // 噪声门槛：轮间极差、量化步长、0.1 ms 下限三者取大。
        double noiseFloor = Math.Max(Math.Max(cpuRange, quantizationPerDraw), CpuDiffNoiseFloorMs);
        bool withinNoise = Math.Abs(cpuMean) < noiseFloor;

        // 逐轮标注：哪些轮次的差值落在噪声门槛以内
        for (int i = 0; i < perRound.Count; i++)
            if (perRound[i] is JsonObject o)
                o["cpuDiffWithinNoise"] = Math.Abs(cpuDiffs[i]) < noiseFloor;

        double legacyCpuAvg = legacyCpu.Average();
        double wiredCpuAvg = wiredCpu.Average();
        int cores = Math.Max(1, Environment.ProcessorCount);

        // 整机占用推算：只基于 CPU 时间口径。
        double legacyProjection = ProjectWholeMachineCpuPercent(legacyCpuAvg, cores);
        double wiredProjection = ProjectWholeMachineCpuPercent(wiredCpuAvg, cores);
        double extraProjection = ProjectWholeMachineCpuPercent(cpuMean, cores);

        string verdict = withinNoise
            ? $"该差异落在测量噪声内（|实测均值| = {Math.Abs(cpuMean):F4} ms/次 < 噪声门槛 {noiseFloor:F4} ms/次，" +
              $"轮间极差 {cpuRange:F4} ms/次），**不可判定**：不能据此断言新入口的 CPU 成本更高或更低。"
            : $"该差异显著大于测量噪声（实测均值 {cpuMean:F4} ms/次，噪声门槛 {noiseFloor:F4} ms/次，" +
              $"轮间极差 {cpuRange:F4} ms/次），方向：{(cpuMean > 0 ? "新入口更贵" : "新入口更省")}。";

        return new JsonObject
        {
            ["kind"] = "appearance_paired_cpu",
            ["caliber"] = "CPU 时间（处理器时间）",
            ["fixtureId"] = spec.Id,
            ["theme"] = spec.Theme,
            ["mode"] = spec.Mode,
            ["gpuCount"] = spec.GpuCount,
            ["dpi"] = spec.Dpi,
            ["userScale"] = spec.UserScale,
            ["rounds"] = rounds,
            ["segmentsPerRound"] = segments,
            ["drawsPerSegmentPerPath"] = draws,
            ["drawsPerRoundPerPath"] = draws * segments,
            ["totalDrawsPerPath"] = draws * segments * rounds,

            // ---- CPU 时间口径 ----
            ["legacyCpuMsPerDraw"] = Round(legacyCpuAvg, 4),
            ["wiredCpuMsPerDraw"] = Round(wiredCpuAvg, 4),
            // ★ 判定值：落在噪声门槛以内时为 null（**不可判定**），不是 0
            ["extraCpuMsPerDraw"] = withinNoise ? null : Round(cpuMean, 4),
            ["extraCpuMsPerDrawMeasured"] = Round(cpuMean, 4),
            ["extraCpuMsPerDrawMin"] = Round(cpuMin, 4),
            ["extraCpuMsPerDrawMax"] = Round(cpuMax, 4),
            ["extraCpuMsPerDrawRange"] = Round(cpuRange, 4),
            ["extraCpuMsPerDrawStdDev"] = Round(StdDev(cpuDiffs), 4),
            ["noiseFloorMsPerDraw"] = Round(noiseFloor, 4),
            ["withinMeasurementNoise"] = withinNoise,
            ["verdict"] = verdict,

            // ---- 同一批次的经过时间（墙钟）：并列给出，便于对照 ----
            ["legacyBatchElapsedMsPerDraw"] = Round(legacyElapsed.Average(), 4),
            ["wiredBatchElapsedMsPerDraw"] = Round(wiredElapsed.Average(), 4),
            ["extraBatchElapsedMsPerDraw"] = Round(elapsedMean, 4),
            ["extraBatchElapsedMsPerDrawMin"] = Round(elapsedMin, 4),
            ["extraBatchElapsedMsPerDrawMax"] = Round(elapsedMax, 4),
            ["extraBatchElapsedMsPerDrawRange"] = Round(elapsedRange, 4),

            // ---- 整机占用推算（只基于 CPU 时间口径）----
            ["legacyProjectedWholeMachineCpuAt12_5Hz"] = Round(legacyProjection, 4),
            ["wiredProjectedWholeMachineCpuAt12_5Hz"] = Round(wiredProjection, 4),
            ["projectedWholeMachineCpuAt12_5Hz"] = withinNoise ? null : Round(extraProjection, 4),
            ["projectedWholeMachineCpuAt12_5HzMeasured"] = Round(extraProjection, 4),
            ["projectedWholeMachineCpuFormula"] =
                "整机占用(%) = 12.5 帧/秒 × 每帧 CPU 毫秒 ÷ 1000 ÷ CPU 核心数 × 100；" +
                "**只能用 CPU 时间口径**，经过时间不能用于整机 CPU 占用推算。",
            ["processorCount"] = cores,

            ["perRound"] = perRound,
            ["cpuTimeSource"] = "Process.TotalProcessorTime（用户态 + 内核态处理器时间），批次前后取差值再除以绘制次数",
            ["elapsedTimeSource"] = "Stopwatch.GetTimestamp（经过时间 / 墙钟），同一批次前后取差值",
            ["caliberNote"] =
                "本段是 **CPU 时间** 口径（可用于 CPU 预算与整机占用推算）；" +
                "同批次的**经过时间**另列在 *BatchElapsed* 字段（可用于延迟判据）。两者不可混用。",
            ["note"] =
                "Windows 进程 CPU 时间的量化步长约 15.6 ms，因此：每批次绘制 " + draws + " 次（远超量化步长）、" +
                "同进程同刻背靠背配对、段内交替 + 逐轮翻转起始顺序、至少 " + rounds + " 轮并给出轮间极差。" +
                "差值绝对值小于噪声门槛（轮间极差 / 量化步长 / 0.1 ms 三者取大）时，extraCpuMsPerDraw 为 null，" +
                "表示「该差异在测量噪声内、不可判定」，**不是 0**。",
        };
    }

    /// <summary>
    /// 测一个批次（<paramref name="draws"/> 次绘制）的 CPU 时间与经过时间。
    /// <para>CPU 时间取自 <c>Process.TotalProcessorTime</c>（需先 <c>Refresh()</c> 才会取到新值）。</para>
    /// </summary>
    private static BatchResult MeasureBatch(
        Graphics g,
        AppearanceContext ctx,
        AppearanceRuntime? appearance,
        int draws,
        Process process)
    {
        process.Refresh();
        double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;

        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < draws; i++)
            Draw(g, ctx, appearance);
        long t1 = Stopwatch.GetTimestamp();

        process.Refresh();
        double cpuAfter = process.TotalProcessorTime.TotalMilliseconds;

        double elapsedMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        // 计数器偶尔会因为时钟回读/采样抖动出现极小的负值，夹到 0。
        double cpuMs = Math.Max(0, cpuAfter - cpuBefore);
        return new BatchResult(cpuMs, elapsedMs, draws);
    }

    /// <summary>整机 CPU 占用推算（%）：12.5 帧/秒 × 每帧 CPU 毫秒 ÷ 1000 ÷ CPU 核心数 × 100。</summary>
    private static double ProjectWholeMachineCpuPercent(double cpuMsPerDraw, int processorCount)
        => cpuMsPerDraw * ProjectionFrameRateHz / 1000 / Math.Max(1, processorCount) * 100;

    /// <summary>样本标准差（样本数 &lt; 2 时返回 0）。</summary>
    private static double StdDev(List<double> samples)
    {
        if (samples.Count < 2) return 0;
        double mean = samples.Average();
        double sum = 0;
        foreach (double s in samples)
            sum += (s - mean) * (s - mean);
        return Math.Sqrt(sum / (samples.Count - 1));
    }

    /// <summary>
    /// 安全读取一个可能缺失 / 为 null 的数值字段。
    /// <para>JSON 里的 <c>null</c> 反序列化成 <c>JsonNull</c>（不是 null 引用），
    /// 直接 <c>GetValue&lt;double&gt;()</c> 会抛异常，所以这里统一兜住。</para>
    /// </summary>
    private static double? ReadDouble(JsonObject? obj, string key)
    {
        if (obj == null) return null;
        JsonNode? node = obj[key];
        if (node == null) return null;
        try { return node.GetValue<double>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>安全读取一个可能缺失 / 为 null 的布尔字段。</summary>
    private static bool? ReadBool(JsonObject? obj, string key)
    {
        if (obj == null) return null;
        JsonNode? node = obj[key];
        if (node == null) return null;
        try { return node.GetValue<bool>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>安全读取一个可能缺失 / 为 null 的字符串字段。</summary>
    private static string? ReadString(JsonObject? obj, string key)
    {
        if (obj == null) return null;
        JsonNode? node = obj[key];
        if (node == null) return null;
        try { return node.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }

    // ==================================================================
    // 只跑 CPU 口径配对测量的独立入口（--appearance-paired-cpu 单独使用）
    // ==================================================================

    /// <summary>
    /// 只跑 CPU 口径配对测量：跳过常规夹具矩阵与经过时间配对测量，
    /// 只产出 CPU 时间口径的 old/new 差值与整机占用推算。
    /// <para>档位与装饰分解模式保持一致（普通档 / 4 卡两倍档），便于横向对照。</para>
    /// </summary>
    private static JsonObject RunPairedCpuOnly(
        AppearanceFixtureData data,
        Settings cfg,
        ProbeOptions options,
        IReadOnlyList<string> themes)
    {
        string themeName = !string.IsNullOrWhiteSpace(options.AppearanceDecompose)
                           && themes.Contains(options.AppearanceDecompose, StringComparer.OrdinalIgnoreCase)
            ? themes.First(t => string.Equals(t, options.AppearanceDecompose, StringComparison.OrdinalIgnoreCase))
            : themes[0];

        var levels = new (string Name, int Gpu, int Dpi, double Scale)[]
        {
            ("普通档(1卡@96/1.0)", 1, 96, 1.0),
            ("4卡两倍档(4卡@96/2.0)", 4, 96, 2.0),
        };

        var levelArray = new JsonArray();
        foreach (var level in levels)
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
            using var bitmap = new Bitmap(Math.Max(1, ctx.Width), Math.Max(1, ctx.Height), PixelFormat.Format32bppArgb);
            bitmap.SetResolution(spec.Dpi, spec.Dpi);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);

            JsonObject measurement = MeasureCpuForFixture(ctx, data, spec, g, options);
            ctx.Theme.DisposeFonts();

            levelArray.Add(new JsonObject
            {
                ["level"] = level.Name,
                ["measurement"] = measurement,
            });
        }

        return new JsonObject
        {
            ["kind"] = "appearance_paired_cpu_only",
            ["mode"] = "cpu-only",
            ["theme"] = themeName,
            ["levels"] = levelArray,
            ["note"] =
                "本结果**只有 CPU 时间口径**（处理器时间）。" +
                "经过时间口径的配对结果在 --appearance-paired 流程里（paired 段），两者不可混用。" +
                "整机占用推算公式：12.5 帧/秒 × 每帧 CPU 毫秒 ÷ 1000 ÷ CPU 核心数 × 100。",
        };
    }

    /// <summary>
    /// CPU 口径配对测量的独立包装：建立新入口运行时 + 固定帧号，然后测量。
    /// （与 <see cref="MeasurePaired"/> 完全同一条渲染路径、同一张图。）
    /// </summary>
    private static JsonObject MeasureCpuForFixture(
        AppearanceContext ctx,
        AppearanceFixtureData data,
        AppearanceFixtureSpec spec,
        Graphics g,
        ProbeOptions options)
    {
        using var runtime = CreatePairedRuntime(ctx, spec);

        // 固定帧号 → 两条路径画的是同一张图
        const int fixedFrame = 1;
        ApplyFrame(ctx, data, fixedFrame);

        return MeasurePairedCpu(ctx, spec, g, runtime, options);
    }

    /// <summary>建立配对测量用的新入口运行时（与 MeasurePaired 同一条路径）。</summary>
    private static AppearanceRuntime CreatePairedRuntime(AppearanceContext ctx, AppearanceFixtureSpec spec)
    {
        var runtime = new AppearanceRuntime();
        float dpiScale = spec.Dpi / 96f;
        float userScale = (float)spec.UserScale;
        runtime.Update(ctx.Theme, dpiScale, userScale, new Rectangle(0, 0, ctx.Width, ctx.Height));
        return runtime;
    }
}
