using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using AppearanceBackdropProbe.Capture;
using AppearanceBackdropProbe.Evidence;
using AppearanceBackdropProbe.Probe;

namespace AppearanceBackdropProbe.Scenarios
{
    internal sealed class PerfCase
    {
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public long PhysicalPixels { get; set; }
        public bool FitsSingleScreen { get; set; }
        public bool CaptureMeasured { get; set; }
        public string CaptureNote { get; set; } = "";
        /// <summary>单次捕获的 CPU 时间（批量测量后取均值；Windows 进程 CPU 时钟粒度 15.625ms，单次不可测）。</summary>
        public double CaptureCpuMsPerOp { get; set; } = -1;
        public double CaptureWallMsMean { get; set; } = -1;
        public double CaptureWallMsP95 { get; set; } = -1;
        public int CaptureBatchSize { get; set; }
        public List<ProcessScaleRow> Processing { get; set; } = new();
        public double FrameBufferBytes { get; set; }
        public bool WithinCaptureAreaBudget { get; set; }
        public bool WithinFrameBufferBudget { get; set; }
        public string BestConfig { get; set; } = "";
        public double BestCombinedCpuMs { get; set; } = -1;
        public bool BestWithinSingleProcessP95Budget { get; set; }
        public bool BestWithinBackgroundCpuBudget { get; set; }
        public double BestCpuPercentAt2Fps { get; set; } = -1;
        public double BestCpuPercentAt6Fps { get; set; } = -1;
    }

    internal sealed class ProcessScaleRow
    {
        public double CaptureScale { get; set; }
        public int BoxRadius { get; set; }
        public double CpuMsPerOp { get; set; } = -1;
        public double WallMsMean { get; set; } = -1;
        public double WallMsP95 { get; set; } = -1;
        public int BatchSize { get; set; }
        public double CombinedCpuMsP95 { get; set; } = -1;
        public bool WithinSingleProcessP95Budget { get; set; }
        public bool WithinBackgroundCpuBudget { get; set; }
        public double CpuPercentAt2Fps { get; set; } = -1;
        public double CpuPercentAt6Fps { get; set; } = -1;
        public double FrameBufferBytesTotal { get; set; }
    }

    internal static class PerfScenario
    {
        private const int CaptureBatch = 30;
        private const int ProcessBatch = 10;
        private const double SingleProcessBudgetMs = 30.0;
        private const double BackgroundCpuBudgetMsPerSec = 40.0;
        private const long CaptureAreaBudgetPixels = 2_000_000;
        private const long FrameBufferBudgetBytes = 32L * 1024 * 1024;

        public static void Run(Options opts, string outDir, LogWriter log)
        {
            var cases = new List<PerfCase>
            {
                new() { Name = "生产竖屏 240x788 (scale1)", Width = 240, Height = 788 },
                new() { Name = "生产竖屏 480x1576 (scale2)", Width = 480, Height = 1576 },
                new() { Name = "动态高面板 240x1576 (2倍高)", Width = 240, Height = 1576 },
                new() { Name = "动态高面板 480x3152 (scale2 2倍高)", Width = 480, Height = 3152 },
            };

            using var rig = new Rig(opts, layered: true, paintContent: true, clickThrough: true, opacity: 0.85, animateScene: false);
            var screen = rig.Screen;
            int sceneX = rig.ProbeScreenRect.X;
            int sceneY = rig.ProbeScreenRect.Y;

            log.Info($"性能场景: 屏幕={screen.DeviceName} {screen.Bounds} 逻辑核={Environment.ProcessorCount} Blur={opts.Blur}");

            foreach (var c in cases)
            {
                c.PhysicalPixels = (long)c.Width * c.Height;
                c.FrameBufferBytes = c.PhysicalPixels * 4;
                c.WithinCaptureAreaBudget = c.PhysicalPixels <= CaptureAreaBudgetPixels;
                c.WithinFrameBufferBudget = c.FrameBufferBytes * 3 <= FrameBufferBudgetBytes;

                bool fits = sceneX + c.Width <= screen.Bounds.Right && sceneY + c.Height <= screen.Bounds.Bottom;
                c.FitsSingleScreen = fits;

                // ---- 捕获成本：批量测量（进程 CPU 时钟粒度 15.625ms，单次捕获不可测）----
                if (fits)
                {
                    var wall = new List<double>();
                    double cpu0 = CaptureBackends.ProcessCpuMs();
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < CaptureBatch; i++)
                    {
                        var one = Stopwatch.StartNew();
                        var cap = CaptureBackends.Capture(CaptureBackendKind.ScreenDcBitBlt, sceneX, sceneY, c.Width, c.Height, rig.Probe.Handle);
                        one.Stop();
                        wall.Add(one.Elapsed.TotalMilliseconds);
                        cap.Image?.Dispose();
                    }
                    sw.Stop();
                    double cpuTotal = CaptureBackends.ProcessCpuMs() - cpu0;
                    c.CaptureCpuMsPerOp = cpuTotal / CaptureBatch;
                    c.CaptureWallMsMean = wall.Count > 0 ? wall.Average() : -1;
                    c.CaptureWallMsP95 = Percentile(wall, 0.95);
                    c.CaptureBatchSize = CaptureBatch;
                    c.CaptureMeasured = true;
                }
                else
                {
                    c.CaptureNote = $"该尺寸超出单屏物理范围({screen.Bounds})，未做真实捕获测量；仅测滤波/呈现成本";
                }

                // ---- 处理成本（降采样→模糊→升采样）：对多个 captureScale 分别测量 ----
                using (var synth = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(synth))
                    {
                        g.Clear(Color.FromArgb(30, 60, 90));
                        for (int y = 0; y < synth.Height; y += 8)
                            using (var br = new SolidBrush(((y / 8) % 2 == 0) ? Color.FromArgb(120, 30, 90) : Color.FromArgb(30, 120, 90)))
                                g.FillRectangle(br, 0, y, synth.Width, 4);
                    }

                    foreach (double scale in new[] { 1.0, 0.5, 0.25 })
                    {
                        var contract = new BlurContract { BlurLogical = opts.Blur, DpiScale = 1.0, CaptureScale = scale };
                        var wall = new List<double>();
                        double p0 = CaptureBackends.ProcessCpuMs();
                        for (int i = 0; i < ProcessBatch; i++)
                        {
                            var one = Stopwatch.StartNew();
                            using var processed = BlurPipeline.Process(synth, contract, out _);
                            one.Stop();
                            wall.Add(one.Elapsed.TotalMilliseconds);
                        }
                        double cpuPerOp = (CaptureBackends.ProcessCpuMs() - p0) / ProcessBatch;

                        var row = new ProcessScaleRow
                        {
                            CaptureScale = scale,
                            BoxRadius = contract.BoxRadius,
                            CpuMsPerOp = cpuPerOp,
                            WallMsMean = wall.Count > 0 ? wall.Average() : -1,
                            WallMsP95 = Percentile(wall, 0.95),
                            BatchSize = ProcessBatch,
                            CombinedCpuMsP95 = c.CaptureMeasured ? c.CaptureCpuMsPerOp + cpuPerOp : cpuPerOp,
                        };
                        int cores = Math.Max(1, Environment.ProcessorCount);
                        row.WithinSingleProcessP95Budget = row.WallMsP95 <= SingleProcessBudgetMs;
                        row.WithinBackgroundCpuBudget = row.CombinedCpuMsP95 * 2.0 <= BackgroundCpuBudgetMsPerSec;
                        row.CpuPercentAt2Fps = row.CombinedCpuMsP95 * 2.0 / 1000.0 / cores * 100.0;
                        row.CpuPercentAt6Fps = row.CombinedCpuMsP95 * 6.0 / 1000.0 / cores * 100.0;
                        // 帧缓冲：active + pending + 降采样中间缓冲
                        row.FrameBufferBytesTotal = (long)(c.PhysicalPixels * 4) + (long)(c.PhysicalPixels * scale * scale * 4) * 2;
                        c.Processing.Add(row);
                    }
                }

                var best = c.Processing.OrderBy(r => r.CombinedCpuMsP95).FirstOrDefault();
                if (best != null)
                {
                    c.BestConfig = $"captureScale={best.CaptureScale} (r={best.BoxRadius})";
                    c.BestCombinedCpuMs = best.CombinedCpuMsP95;
                    c.BestWithinSingleProcessP95Budget = best.WithinSingleProcessP95Budget;
                    c.BestWithinBackgroundCpuBudget = best.WithinBackgroundCpuBudget;
                    c.BestCpuPercentAt2Fps = best.CpuPercentAt2Fps;
                    c.BestCpuPercentAt6Fps = best.CpuPercentAt6Fps;
                }

                log.Info($"  {c.Name}: 捕获CPU={c.CaptureCpuMsPerOp:F3}ms/次 捕获墙钟p95={c.CaptureWallMsP95:F2}ms | " +
                         string.Join(" | ", c.Processing.Select(r => $"s{r.CaptureScale}: CPU {r.CpuMsPerOp:F2}ms 墙钟p95 {r.WallMsP95:F1}ms p95预算{(r.WithinSingleProcessP95Budget ? "OK" : "超")}")) +
                         $" | 最佳={c.BestConfig} 合计CPU={c.BestCombinedCpuMs:F2}ms 2fps={c.BestCpuPercentAt2Fps:F3}%");
            }

            var summary = new
            {
                Scenario = "perf",
                Item = "v4 §7.4 实时背景性能与资源预算",
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                LogicalProcessors = Environment.ProcessorCount,
                Screen = screen.DeviceName,
                ScreenBounds = screen.Bounds.ToString(),
                BlurLogical = opts.Blur,
                MeasurementMethod = "CPU：进程 CPU 时间，批量执行后取每操作均值（Windows 进程 CPU 时钟粒度 15.625ms，单次操作不可直接测量）；" +
                                    "墙钟：Stopwatch(高分辨率) 单操作测量并取 p95。",
                ConcurrentLoadNote = "测量前后已确认无其它 agent 的构建/测量进程；机器上有常驻应用占用约 18–20% CPU（见 environment/负载快照）。",
                Budgets = new
                {
                    SingleProcessP95Ms = SingleProcessBudgetMs,
                    BackgroundCpuMsPerSecond = BackgroundCpuBudgetMsPerSec,
                    CaptureAreaPhysicalPixels = CaptureAreaBudgetPixels,
                    FrameBufferBytes = FrameBufferBudgetBytes,
                },
                MetricNote = "占用百分比 = CPUms/1000/逻辑核数×100%（不是墙钟）",
                Cases = cases,
            };
            EvidenceWriter.WriteJson(Path.Combine(outDir, "performance.json"), summary);
            log.Info("性能场景完成");
        }

        private static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0) return -1;
            var sorted = new List<double>(values);
            sorted.Sort();
            int idx = (int)Math.Round((sorted.Count - 1) * p);
            return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
        }
    }
}
