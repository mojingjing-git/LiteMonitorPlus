using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using AppearanceBackdropProbe.Analysis;
using AppearanceBackdropProbe.Capture;
using AppearanceBackdropProbe.Evidence;
using AppearanceBackdropProbe.Native;
using AppearanceBackdropProbe.Probe;

namespace AppearanceBackdropProbe.Scenarios
{
    internal sealed class BackendProbeResult
    {
        public string Backend { get; set; } = "";
        public string AffinityMode { get; set; } = "";
        public uint AffinityValue { get; set; }
        public bool SetDisplayAffinityOk { get; set; }
        public uint AffinityReadBack { get; set; }
        public bool CaptureOk { get; set; }
        public string CaptureError { get; set; } = "";
        public string CapturePath { get; set; } = "";
        public long WallMs { get; set; }
        public double CpuMs { get; set; }
        public FrameStats Stats { get; set; } = new();
        /// <summary>捕获结果里没有本窗口自己的像素（无自反馈）。</summary>
        public bool ExcludedSelf { get; set; }
        /// <summary>捕获结果里拿到了场景的真实背景色块。</summary>
        public bool GotTrueBackground { get; set; }
    }

    internal sealed class RecorderImpactResult
    {
        public string AffinityMode { get; set; } = "";
        public uint AffinityValue { get; set; }
        public bool ExternalCaptureOk { get; set; }
        public string ExternalCapturePath { get; set; } = "";
        /// <summary>外部观察者（≈用户录屏软件）是否还能看到本窗口。</summary>
        public bool WindowVisibleToExternalObserver { get; set; }
        public double SelfMarkerFraction { get; set; }
        public int MarkerPixelCount { get; set; } = -1;
        public double StaticPatchFraction { get; set; }
        public string Note { get; set; } = "";
    }

    internal sealed class ObservationSummary
    {
        public string Backend { get; set; } = "";
        public int Frames { get; set; }
        public double DurationSec { get; set; }
        public double TargetFps { get; set; }
        public double ActualFps { get; set; }
        public bool AnySelfContamination { get; set; }
        public double MaxSelfMarkerFraction { get; set; }
        public double MinStaticPatchFraction { get; set; }
        public double MeanStaticPatchFraction { get; set; }
        public double LuminanceSlopePerFrame { get; set; }
        public bool RecursiveDarkeningSuspected { get; set; }
        public bool WindowJitterDetected { get; set; }
        public int MaxWindowRectDelta { get; set; }
        public double MeanTemporalDiff { get; set; }
        public double MeanHueDistanceToExpected { get; set; }
        /// <summary>观察期间背景场景的重绘次数 —— 为 0 表示背景没动，"背景正确"只是静态巧合。</summary>
        public int SceneRepaintsDuringObservation { get; set; }
        public List<string> SavedFrames { get; set; } = new();
        public string ContactSheetPath { get; set; } = "";
        public string Verdict { get; set; } = "";
    }

    internal static class CaptureScenario
    {
        public static void Run(Options opts, string outDir, LogWriter log)
        {
            var scriptDir = ScenarioSupport.ResolveScriptDir(opts);
            var external = new ExternalCapture(opts.PwshPath, Path.Combine(scriptDir, "external-capture.ps1"));
            log.Info(external.Info);

            using var rig = new Rig(opts, layered: true, paintContent: true, clickThrough: true, opacity: 0.85,
                topMost: opts.UsesTopMost, allowActivation: opts.AllowsActivation);
            log.Info($"试验台就绪: 屏幕={rig.Screen.DeviceName} bounds={rig.Screen.Bounds} 探针矩形={rig.ProbeScreenRect}");

            var backends = new[]
            {
                CaptureBackendKind.ScreenDcBitBlt,
                CaptureBackendKind.ScreenDcBitBltCaptureBlt,
                CaptureBackendKind.CopyFromScreen,
                CaptureBackendKind.PrintWindow,
            };

            var affinities = new (uint Value, string Name)[]
            {
                (Interop.WDA_NONE, "WDA_NONE(0)"),
                (Interop.WDA_EXCLUDEFROMCAPTURE, "WDA_EXCLUDEFROMCAPTURE(0x11)"),
                (Interop.WDA_MONITOR, "WDA_MONITOR(0x01)"),
            };

            var backendResults = new List<BackendProbeResult>();
            var recorderImpact = new List<RecorderImpactResult>();

            using var refCrop = rig.ReferenceCrop();
            int[] refPixels = PixelAnalyzer.GetPixels(refCrop);

            foreach (var (affinity, affName) in affinities)
            {
                bool setOk = Interop.SetWindowDisplayAffinity(rig.Probe.Handle, affinity);
                uint readBack = 0;
                Interop.GetWindowDisplayAffinity(rig.Probe.Handle, out readBack);
                Rig.Pump(350);

                foreach (var backend in backends)
                {
                    rig.UpdateProbeRect();
                    var rect = rig.ProbeScreenRect;
                    var cap = CaptureBackends.Capture(backend, rect.X, rect.Y, rect.Width, rect.Height, rig.Probe.Handle);

                    var br = new BackendProbeResult
                    {
                        Backend = CaptureBackends.NameOf(backend),
                        AffinityMode = affName,
                        AffinityValue = affinity,
                        SetDisplayAffinityOk = setOk,
                        AffinityReadBack = readBack,
                        CaptureOk = cap.Ok,
                        CaptureError = cap.Error,
                        WallMs = cap.WallMs,
                        CpuMs = cap.CpuMs,
                    };

                    if (cap.Ok && cap.Image != null)
                    {
                        using var img = cap.Image;
                        br.Stats = PixelAnalyzer.Analyze(img, ScenarioSupport.StaticRegion(rig), ScenarioSupport.HueRegion(rig),
                            ScenarioSupport.BarRegion(rig), ProbeForm.SelfMarker, refPixels, ScenarioSupport.StaticRegion(rig));
                        br.ExcludedSelf = br.Stats.SelfMarkerFraction < 0.005;
                        br.GotTrueBackground = br.Stats.StaticPatchFraction > 0.5;

                        string path = Path.Combine(outDir, "capture", $"{affName}_{CaptureBackends.NameOf(backend)}.png".Replace("(", "_").Replace(")", "_").Replace(",", "_").Replace("|", "_"));
                        EvidenceWriter.SavePng(img, path);
                        br.CapturePath = Path.GetRelativePath(outDir, path);
                    }

                    backendResults.Add(br);
                    log.Info($"  [{affName}] {br.Backend}: ok={br.CaptureOk} self={br.Stats.SelfMarkerFraction:F4} bg={br.Stats.StaticPatchFraction:F4} diffRef={br.Stats.MeanAbsDiffVsReference:F2}");
                }

                // 外部观察者（≈用户录屏软件视角）对 EXCLUDEFROMCAPTURE 的影响
                rig.UpdateProbeRect();
                var r2 = rig.ProbeScreenRect;
                string extPath = Path.Combine(outDir, "capture", $"recorder-impact_{affName}.png".Replace("(", "_").Replace(")", "_").Replace(",", "_"));
                var oc = external.Capture(r2.X, r2.Y, r2.Width, r2.Height, extPath);
                var ri = new RecorderImpactResult
                {
                    AffinityMode = affName,
                    AffinityValue = affinity,
                    ExternalCaptureOk = oc.Ok,
                    ExternalCapturePath = oc.Ok ? Path.GetRelativePath(outDir, extPath) : "",
                };
                if (oc.Ok)
                {
                    using var img = new Bitmap(extPath);
                    var st = PixelAnalyzer.Analyze(img, ScenarioSupport.StaticRegion(rig), ScenarioSupport.HueRegion(rig),
                        ScenarioSupport.BarRegion(rig), ProbeForm.SelfMarker, refPixels, ScenarioSupport.StaticRegion(rig));
                    ri.SelfMarkerFraction = st.SelfMarkerFraction;
                    ri.StaticPatchFraction = st.StaticPatchFraction;
                    // 绝对像素数判定（标记方块 48×48=2304px）：占比会随窗口尺寸变化，用占比会在大窗口上假阴性
                    ri.MarkerPixelCount = PixelAnalyzer.CountNear(PixelAnalyzer.GetPixels(img), img.Width,
                        new Rectangle(0, 0, img.Width, img.Height), ProbeForm.SelfMarker, 60);
                    ri.WindowVisibleToExternalObserver = ri.MarkerPixelCount >= 500;
                    ri.Note = ri.WindowVisibleToExternalObserver
                        ? $"外部观察者仍能看到本窗口（标记像素 {ri.MarkerPixelCount}），未对录屏屏蔽"
                        : $"外部观察者看不到本窗口（标记像素 {ri.MarkerPixelCount}），窗口已从屏幕捕获中排除";
                }
                recorderImpact.Add(ri);
                log.Info($"  [录屏影响] {affName}: 外部可见={ri.WindowVisibleToExternalObserver} selfMarker={ri.SelfMarkerFraction:F4} bg={ri.StaticPatchFraction:F4}");

                // 复位
                Interop.SetWindowDisplayAffinity(rig.Probe.Handle, Interop.WDA_NONE);
                Rig.Pump(200);
            }

            // 选择"可放行的后端"：无自反馈 且 拿到真实背景
            var pass = backendResults.Where(b => b.CaptureOk && b.ExcludedSelf && b.GotTrueBackground)
                                     .OrderBy(b => b.CpuMs).ToList();
            var chosen = pass.FirstOrDefault();
            string chosenName = chosen?.Backend ?? "(无可放行后端)";

            var observation = chosen != null
                ? RunObservation(opts, outDir, log, rig, chosen, refPixels)
                : new ObservationSummary { Verdict = "没有任何后端同时满足'排除自身'与'拿到真实背景'，未进行 30 秒连续观察" };
            // 自反馈演示：把捕获结果画回窗口 → 递归变暗/拖影
            var feedback = RunSelfFeedbackDemo(opts, outDir, log, rig);

            var summary = new
            {
                Scenario = "capture",
                Items = "v4 §4 第 2 项（排除自身且背景正确）+ 第 3 项（EXCLUDEFROMCAPTURE 与录屏影响）",
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                Screen = rig.Screen.DeviceName,
                ScreenBounds = rig.Screen.Bounds.ToString(),
                ProbeScreenRect = rig.ProbeScreenRect.ToString(),
                ProbePhysicalSize = $"{rig.ProbeScreenRect.Width}x{rig.ProbeScreenRect.Height}",
                ExternalObserver = external.Info,
                BackendResults = backendResults,
                RecorderImpact = recorderImpact,
                ChosenBackend = chosenName,
                Observation = observation,
                SelfFeedbackDemo = feedback,
            };
            EvidenceWriter.WriteJson(Path.Combine(outDir, "capture-backends.json"), summary);
            log.Info("捕获后端场景完成");
        }

        private static ObservationSummary RunObservation(Options opts, string outDir, LogWriter log, Rig rig,
            BackendProbeResult chosen, int[] refPixels)
        {
            var summary = new ObservationSummary
            {
                Backend = chosen.Backend,
                TargetFps = opts.Fps,
            };

            var kind = Enum.GetValues<CaptureBackendKind>()
                .First(k => CaptureBackends.NameOf(k) == chosen.Backend);

            // 观察期必须复现"放行组合"：同一后端 + 同一显示亲和性
            Interop.SetWindowDisplayAffinity(rig.Probe.Handle, chosen.AffinityValue);
            Rig.Pump(300);

            int frames = Math.Max(1, (int)Math.Round(opts.DurationSec * opts.Fps));
            double interval = 1000.0 / Math.Max(0.1, opts.Fps);
            var saved = new List<Bitmap>();
            var luminances = new List<double>();
            var selfFractions = new List<double>();
            var staticFractions = new List<double>();
            var hueDistances = new List<double>();
            var temporalDiffs = new List<double>();
            int[]? prevPixels = null;
            Rectangle firstRect = Rectangle.Empty;

            long t0 = Environment.TickCount64;
            int repaints0 = rig.Scene.RepaintCount;
            for (int i = 0; i < frames; i++)
            {
                rig.UpdateProbeRect();
                var rect = rig.ProbeScreenRect;
                if (i == 0) firstRect = rect;

                var cap = CaptureBackends.Capture(kind, rect.X, rect.Y, rect.Width, rect.Height, rig.Probe.Handle);
                if (!cap.Ok || cap.Image == null) { Rig.Pump((int)interval); continue; }

                using var img = cap.Image;
                var st = PixelAnalyzer.Analyze(img, ScenarioSupport.StaticRegion(rig), ScenarioSupport.HueRegion(rig),
                    ScenarioSupport.BarRegion(rig), ProbeForm.SelfMarker, refPixels, ScenarioSupport.StaticRegion(rig));

                luminances.Add(st.Luminance);
                selfFractions.Add(st.SelfMarkerFraction);
                staticFractions.Add(st.StaticPatchFraction);
                double expected = ScenarioSupport.ExpectedHue(rig.Scene.TimeSeconds);
                double hd = ScenarioSupport.HueDistance(st.HueDegrees, expected);
                if (hd >= 0) hueDistances.Add(hd);

                int[] px = PixelAnalyzer.GetPixels(img);
                if (prevPixels != null) temporalDiffs.Add(PixelAnalyzer.MeanAbsDiff(prevPixels, px));
                prevPixels = px;

                if (i % 10 == 0)
                {
                    var copy = new Bitmap(img);
                    saved.Add(copy);
                    string p = Path.Combine(outDir, "capture", "observation", $"frame-{i:0000}.png");
                    EvidenceWriter.SavePng(copy, p);
                    summary.SavedFrames.Add(Path.GetRelativePath(outDir, p));
                }

                int dx = Math.Abs(rect.X - firstRect.X) + Math.Abs(rect.Y - firstRect.Y)
                       + Math.Abs(rect.Width - firstRect.Width) + Math.Abs(rect.Height - firstRect.Height);
                summary.MaxWindowRectDelta = Math.Max(summary.MaxWindowRectDelta, dx);

                Rig.Pump((int)Math.Max(0, interval - 40));
            }
            summary.DurationSec = (Environment.TickCount64 - t0) / 1000.0;
            summary.Frames = luminances.Count;
            summary.SceneRepaintsDuringObservation = rig.Scene.RepaintCount - repaints0;
            summary.ActualFps = summary.DurationSec > 0 ? luminances.Count / summary.DurationSec : 0;
            summary.AnySelfContamination = selfFractions.Count > 0 && selfFractions.Max() >= 0.005;
            summary.MaxSelfMarkerFraction = selfFractions.Count > 0 ? selfFractions.Max() : -1;
            summary.MinStaticPatchFraction = staticFractions.Count > 0 ? staticFractions.Min() : -1;
            summary.MeanStaticPatchFraction = staticFractions.Count > 0 ? staticFractions.Average() : -1;
            summary.LuminanceSlopePerFrame = PixelAnalyzer.TrendSlope(luminances);
            summary.RecursiveDarkeningSuspected = summary.LuminanceSlopePerFrame < -0.25;
            summary.WindowJitterDetected = summary.MaxWindowRectDelta > 0;
            summary.MeanTemporalDiff = temporalDiffs.Count > 0 ? temporalDiffs.Average() : -1;
            summary.MeanHueDistanceToExpected = hueDistances.Count > 0 ? hueDistances.Average() : -1;

            if (saved.Count > 0)
            {
                var sheet = EvidenceWriter.ContactSheet(saved, 6, 150, $"{chosen.Backend} — {summary.Frames} 帧 / {summary.DurationSec:0.0}s");
                string sp = Path.Combine(outDir, "capture", "observation", "contact-sheet.png");
                EvidenceWriter.SavePng(sheet, sp);
                summary.ContactSheetPath = Path.GetRelativePath(outDir, sp);
            }

            summary.Verdict =
                summary.SceneRepaintsDuringObservation <= 0
                ? "无效：观察期间背景场景没有重绘，'背景正确'只是静态巧合，结论不成立"
                : ((!summary.AnySelfContamination && summary.MinStaticPatchFraction > 0.5 && !summary.WindowJitterDetected && !summary.RecursiveDarkeningSuspected && summary.MeanTemporalDiff > 0.5)
                    ? "通过：连续观察期间无自身像素、背景持续正确且随背景实时变化、窗口无抖动、无递归变暗"
                    : "失败：见各项指标");

            Interop.SetWindowDisplayAffinity(rig.Probe.Handle, Interop.WDA_NONE);
            Rig.Pump(200);

            log.Info($"  连续观察: {summary.Frames}帧/{summary.DurationSec:0.0}s 场景重绘={summary.SceneRepaintsDuringObservation} " +
                     $"selfMax={summary.MaxSelfMarkerFraction:F4} bgMin={summary.MinStaticPatchFraction:F4} " +
                     $"lumaSlope={summary.LuminanceSlopePerFrame:F4} jitter={summary.MaxWindowRectDelta} " +
                     $"meanTemporalDiff={summary.MeanTemporalDiff:F3} hueDist={summary.MeanHueDistanceToExpected:F1} verdict={summary.Verdict}");

            return summary;
        }

        private sealed class FeedbackResult
        {
            public int Iterations { get; set; }
            public bool Ok { get; set; }
            public string EvidencePath { get; set; } = "";
            public double LuminanceFirst { get; set; }
            public double LuminanceLast { get; set; }
            public double LuminanceSlopePerIteration { get; set; }
            public bool ProgressiveDarkening { get; set; }
            public int BrightPixelCountFirst { get; set; }
            public int BrightPixelCountLast { get; set; }
            public int BrightPixelCountGrowth { get; set; }
            public string Note { get; set; } = "";
        }

        private static FeedbackResult RunSelfFeedbackDemo(Options opts, string outDir, LogWriter log, Rig rig)
        {
            var res = new FeedbackResult();
            Interop.SetWindowDisplayAffinity(rig.Probe.Handle, Interop.WDA_NONE);
            Rig.Pump(200);

            var lum = new List<double>();
            var brightCounts = new List<int>();
            var frames = new List<Bitmap>();

            for (int i = 0; i < 12; i++)
            {
                rig.UpdateProbeRect();
                var rect = rig.ProbeScreenRect;
                // 用 WDA_NONE 下的 CopyFromScreen：该后端实测会把分层窗口一起抓进来（自反馈的前提）
                var cap = CaptureBackends.Capture(CaptureBackendKind.CopyFromScreen, rect.X, rect.Y, rect.Width, rect.Height, rig.Probe.Handle);
                if (!cap.Ok || cap.Image == null) break;

                using var img = cap.Image;
                var st = PixelAnalyzer.Analyze(img, ScenarioSupport.StaticRegion(rig), ScenarioSupport.HueRegion(rig),
                    ScenarioSupport.BarRegion(rig), ProbeForm.SelfMarker, null, new Rectangle(0, 0, 0, 0));
                lum.Add(st.Luminance);
                brightCounts.Add(CountBrightPixels(img, ScenarioSupport.BarRegion(rig)));

                rig.Probe.FeedbackImage = new Bitmap(img);
                rig.Probe.FeedbackOffset = new Point(i % 2 == 0 ? 2 : -2, i % 3 == 0 ? 2 : 0);
                rig.Probe.FeedbackAlpha = 210;
                rig.Probe.Invalidate();
                rig.Probe.Update();
                Rig.Pump(120);
                if (i % 4 == 0) frames.Add(new Bitmap(img));
            }

            // 注意：必须在"反馈仍然叠加"的状态下拍外部照，否则拍到的是清理后的干净画面
            rig.UpdateProbeRect();
            var r0 = rig.ProbeScreenRect;
            var scriptDir = ScenarioSupport.ResolveScriptDir(opts);
            var external = new ExternalCapture(opts.PwshPath, Path.Combine(scriptDir, "external-capture.ps1"));
            string path = Path.Combine(outDir, "capture", "self-feedback.png");
            var oc = external.Capture(r0.X, r0.Y, r0.Width, r0.Height, path);
            res.EvidencePath = oc.Ok ? Path.GetRelativePath(outDir, path) : "";
            res.Note = oc.Ok
                ? "自反馈演示：把屏幕捕获结果按 2px 偏移叠回窗口 → 出现自身文字/数字轮廓与多重拖影"
                : $"外部观察者失败: {oc.Message}";

            rig.Probe.FeedbackImage?.Dispose();
            rig.Probe.FeedbackImage = null;
            rig.Probe.Invalidate();
            rig.Probe.Update();
            Rig.Pump(200);

            res.Iterations = lum.Count;
            res.Ok = lum.Count > 0;
            if (lum.Count > 0)
            {
                res.LuminanceFirst = lum[0];
                res.LuminanceLast = lum[^1];
                res.LuminanceSlopePerIteration = PixelAnalyzer.TrendSlope(lum);
                res.ProgressiveDarkening = res.LuminanceSlopePerIteration < -0.2;
            }
            res.BrightPixelCountFirst = brightCounts.Count > 0 ? brightCounts[0] : -1;
            res.BrightPixelCountLast = brightCounts.Count > 0 ? brightCounts[^1] : -1;
            res.BrightPixelCountGrowth = brightCounts.Count > 1 ? (brightCounts[^1] - brightCounts[0]) : 0;

            if (frames.Count > 0)
            {
                var sheet = EvidenceWriter.ContactSheet(frames, 3, 200, "自反馈演示（捕获结果被叠回窗口）");
                EvidenceWriter.SavePng(sheet, Path.Combine(outDir, "capture", "self-feedback-contact-sheet.png"));
            }

            log.Info($"  自反馈演示: 迭代={res.Iterations} 亮度首={res.LuminanceFirst:F2} 末={res.LuminanceLast:F2} 斜率={res.LuminanceSlopePerIteration:F4} " +
                     $"亮像素首={res.BrightPixelCountFirst} 末={res.BrightPixelCountLast} 递归变暗={res.ProgressiveDarkening}");
            return res;
        }

        /// <summary>区域内高亮（接近白色）像素数——自反馈叠加会让文字轮廓翻倍。</summary>
        private static int CountBrightPixels(Bitmap bmp, Rectangle region)
        {
            int[] px = PixelAnalyzer.GetPixels(bmp);
            int count = 0;
            for (int y = region.Top; y < region.Bottom; y++)
            {
                for (int x = region.Left; x < region.Right; x++)
                {
                    int c = px[y * bmp.Width + x];
                    int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                    if (r > 200 && g > 200 && b > 200) count++;
                }
            }
            return count;
        }
    }
}
