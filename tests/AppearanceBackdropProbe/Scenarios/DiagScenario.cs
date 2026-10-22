using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using AppearanceBackdropProbe.Analysis;
using AppearanceBackdropProbe.Capture;
using AppearanceBackdropProbe.Evidence;
using AppearanceBackdropProbe.Native;
using AppearanceBackdropProbe.Probe;

namespace AppearanceBackdropProbe.Scenarios
{
    internal sealed class LivenessRow
    {
        public string AffinityMode { get; set; } = "";
        public uint AffinityValue { get; set; }
        /// <summary>探针窗口矩形（被排除的区域）在两帧之间的差异。</summary>
        public double ProbeRectTemporalDiff { get; set; } = -1;
        /// <summary>同一时刻"探针之外"的场景区域差异 —— 用来证明场景本身在动、且捕获是实时的。</summary>
        public double OutsideSceneTemporalDiff { get; set; } = -1;
        /// <summary>探针矩形内的色相与场景理论色相的距离。</summary>
        public double HueDistanceToExpected { get; set; } = -1;
        public double SelfMarkerFraction { get; set; }
        public double StaticPatchFraction { get; set; }
        public bool HoleIsLive { get; set; }
        public int SceneRepaintsDuringWait { get; set; }
        public int SceneTicksDuringWait { get; set; }
        public double SceneSecondsBetweenCaptures { get; set; }
        public string Note { get; set; } = "";
        public string EvidencePath { get; set; } = "";
    }

    /// <summary>
    /// 诊断：EXCLUDEFROMCAPTURE 排除出来的"洞"是**实时**的还是**冻结快照**？
    /// 做法：同一时刻抓两处 —— 探针窗口矩形（排除区）与探针之外的场景矩形（未被排除）。
    /// 若"探针之外"在变而"探针之内"不变，说明排除区是冻结的，不能用于实时玻璃。
    /// </summary>
    internal static class DiagScenario
    {
        public static void Run(Options opts, string outDir, LogWriter log)
        {
            using var rig = new Rig(opts, layered: true, paintContent: true, clickThrough: true, opacity: 0.85, animateScene: true,
                topMost: opts.UsesTopMost, allowActivation: opts.AllowsActivation);

            // 探针之外的场景区域：场景窗口左下角的时间戳文字（每帧都变，且不在探针矩形内）
            int sceneH = rig.Layout.SceneHeight;
            var outsideRect = new Rectangle(
                rig.ProbeScreenRect.X - rig.Layout.Margin + 5,
                rig.ProbeScreenRect.Y + rig.Layout.ProbeHeight + rig.Layout.Margin - 70,
                420, 55);

            var rows = new List<LivenessRow>();
            var affinities = new (uint, string)[]
            {
                (Interop.WDA_NONE, "WDA_NONE(0)"),
                (Interop.WDA_EXCLUDEFROMCAPTURE, "WDA_EXCLUDEFROMCAPTURE(0x11)"),
                (Interop.WDA_MONITOR, "WDA_MONITOR(0x01)"),
            };

            foreach (var (aff, name) in affinities)
            {
                Interop.SetWindowDisplayAffinity(rig.Probe.Handle, aff);
                Rig.Pump(400);

                rig.UpdateProbeRect();
                var pr = rig.ProbeScreenRect;

                var row = new LivenessRow { AffinityMode = name, AffinityValue = aff };

                var a = CaptureBackends.Capture(CaptureBackendKind.CopyFromScreen, pr.X, pr.Y, pr.Width, pr.Height, rig.Probe.Handle);
                var o1 = CaptureBackends.Capture(CaptureBackendKind.CopyFromScreen, outsideRect.X, outsideRect.Y, outsideRect.Width, outsideRect.Height, rig.Probe.Handle);
                double t1 = rig.Scene.TimeSeconds;
                int repaints1 = rig.Scene.RepaintCount;
                int ticks1 = rig.Scene.TickCount;

                Rig.Pump(1500);

                var b = CaptureBackends.Capture(CaptureBackendKind.CopyFromScreen, pr.X, pr.Y, pr.Width, pr.Height, rig.Probe.Handle);
                var o2 = CaptureBackends.Capture(CaptureBackendKind.CopyFromScreen, outsideRect.X, outsideRect.Y, outsideRect.Width, outsideRect.Height, rig.Probe.Handle);
                double t2 = rig.Scene.TimeSeconds;

                row.SceneRepaintsDuringWait = rig.Scene.RepaintCount - repaints1;
                row.SceneTicksDuringWait = rig.Scene.TickCount - ticks1;
                row.SceneSecondsBetweenCaptures = t2 - t1;

                if (a.Ok && b.Ok && a.Image != null && b.Image != null)
                {
                    row.ProbeRectTemporalDiff = PixelAnalyzer.MeanAbsDiff(PixelAnalyzer.GetPixels(a.Image), PixelAnalyzer.GetPixels(b.Image));
                    var st = PixelAnalyzer.Analyze(a.Image, ScenarioSupport.StaticRegion(rig), ScenarioSupport.HueRegion(rig),
                        ScenarioSupport.BarRegion(rig), ProbeForm.SelfMarker, null, new Rectangle(0, 0, 0, 0));
                    row.HueDistanceToExpected = ScenarioSupport.HueDistance(st.HueDegrees, ScenarioSupport.ExpectedHue(t1));
                    row.SelfMarkerFraction = st.SelfMarkerFraction;
                    row.StaticPatchFraction = st.StaticPatchFraction;

                    string p = Path.Combine(outDir, "diag", $"probe-area_{name}.png".Replace("(", "_").Replace(")", "_").Replace(",", "_"));
                    EvidenceWriter.SavePng(a.Image, p);
                    row.EvidencePath = Path.GetRelativePath(outDir, p);
                    a.Image.Dispose(); b.Image.Dispose();
                }

                if (o1.Ok && o2.Ok && o1.Image != null && o2.Image != null)
                {
                    row.OutsideSceneTemporalDiff = PixelAnalyzer.MeanAbsDiff(PixelAnalyzer.GetPixels(o1.Image), PixelAnalyzer.GetPixels(o2.Image));
                    string p = Path.Combine(outDir, "diag", $"outside-scene_{name}.png".Replace("(", "_").Replace(")", "_").Replace(",", "_"));
                    EvidenceWriter.SavePng(o1.Image, p);
                    o1.Image.Dispose(); o2.Image.Dispose();
                }

                row.HoleIsLive = row.ProbeRectTemporalDiff > 0.5 && row.OutsideSceneTemporalDiff > 0.5;
                row.Note = row.SceneRepaintsDuringWait == 0
                    ? $"场景在等待期间没有重绘（tick={row.SceneTicksDuringWait}）—— 背景根本没动，不能据此判断排除区是否实时"
                    : (row.OutsideSceneTemporalDiff > 0.5
                        ? (row.ProbeRectTemporalDiff > 0.5
                            ? "场景在动且排除区也在动 → 排除区是实时的"
                            : "场景在动但排除区不变 → 排除区是冻结快照，不能用于实时玻璃")
                        : "场景外部区域也没变（场景已重绘但捕获未刷新）");

                rows.Add(row);
                log.Info($"  {name}: 探针区变化={row.ProbeRectTemporalDiff:F3} 探针外场景变化={row.OutsideSceneTemporalDiff:F3} " +
                         $"场景重绘={row.SceneRepaintsDuringWait} tick={row.SceneTicksDuringWait} hueDist={row.HueDistanceToExpected:F1} 实时={row.HoleIsLive} — {row.Note}");

                Interop.SetWindowDisplayAffinity(rig.Probe.Handle, Interop.WDA_NONE);
                Rig.Pump(200);
            }

            EvidenceWriter.WriteJson(Path.Combine(outDir, "liveness-diagnostic.json"), new
            {
                Scenario = "diag",
                Item = "v4 §4 第 2/3 项补充诊断：EXCLUDEFROMCAPTURE 排除区是否实时",
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                Screen = rig.Screen.DeviceName,
                ProbeRect = rig.ProbeScreenRect.ToString(),
                OutsideSceneRect = outsideRect.ToString(),
                Note = "探针之外的场景区域含每帧变化的时间戳文字；探针矩形是被排除的区域。两处同时抓取即可判定排除区是否冻结。",
                Rows = rows,
                TransientExclusion = RunTransientExclusionTest(opts, outDir, log, rig),
            });

            log.Info("诊断场景完成");
        }

        internal sealed class TransientExclusionResult
        {
            public bool Ok { get; set; }
            public double ExclusionWindowMs { get; set; } = -1;
            public double CaptureCpuMs { get; set; } = -1;
            public bool WindowVisibleAgainToExternalObserver { get; set; }
            public bool VisibleImmediatelyAfterClear { get; set; }
            public bool VisibleAfter400ms { get; set; }
            public int MarkerPixelsImmediatelyAfterClear { get; set; } = -1;
            public int MarkerPixelsAfter400ms { get; set; } = -1;
            public double ExclusionDutyCycleAt2Fps { get; set; } = -1;
            public string Note { get; set; } = "";
        }

        /// <summary>
        /// 缓解方案验证：把"永久排除"改成"瞬时排除"（设置 → 抓一帧 → 立即清除），
        /// 看用户录屏是否仍能拍到窗口，以及排除占空比有多高。
        /// </summary>
        private static TransientExclusionResult RunTransientExclusionTest(Options opts, string outDir, LogWriter log, Rig rig)
        {
            var res = new TransientExclusionResult();
            rig.UpdateProbeRect();
            var pr = rig.ProbeScreenRect;

            Interop.SetWindowDisplayAffinity(rig.Probe.Handle, Interop.WDA_NONE);
            Rig.Pump(300);

            double cpu0 = CaptureBackends.ProcessCpuMs();
            var sw = Stopwatch.StartNew();
            Interop.SetWindowDisplayAffinity(rig.Probe.Handle, Interop.WDA_EXCLUDEFROMCAPTURE);
            var cap = CaptureBackends.Capture(CaptureBackendKind.CopyFromScreen, pr.X, pr.Y, pr.Width, pr.Height, rig.Probe.Handle);
            Interop.SetWindowDisplayAffinity(rig.Probe.Handle, Interop.WDA_NONE);
            sw.Stop();
            double cpu = CaptureBackends.ProcessCpuMs() - cpu0;

            // 清除排除后强制重绘：检验"合成器缓存了排除态"这一可能
            rig.Probe.Invalidate();
            rig.Probe.Update();
            Rig.Pump(120);

            res.Ok = cap.Ok;
            res.ExclusionWindowMs = sw.Elapsed.TotalMilliseconds;
            res.CaptureCpuMs = cpu;
            cap.Image?.Dispose();

            // 外部观察者再拍一次：窗口应当重新可见。
            // 分两次拍：立刻拍（t+0ms）与等 400ms 后拍（t+400ms），
            // 用来区分"清除未生效"与"清除生效但 DWM 需要重新合成"。
            var scriptDir = ScenarioSupport.ResolveScriptDir(opts);
            var external = new ExternalCapture(opts.PwshPath, Path.Combine(scriptDir, "external-capture.ps1"));

            string path0 = Path.Combine(outDir, "diag", "transient-exclusion-restored-t0.png");
            var oc0 = external.Capture(pr.X, pr.Y, pr.Width, pr.Height, path0);
            int countT0 = -1;
            if (oc0.Ok)
            {
                using var img = new Bitmap(path0);
                countT0 = PixelAnalyzer.CountNear(PixelAnalyzer.GetPixels(img), img.Width,
                    new Rectangle(0, 0, img.Width, img.Height), ProbeForm.SelfMarker, 60);
            }

            Rig.Pump(400);

            string path = Path.Combine(outDir, "diag", "transient-exclusion-restored.png");
            var oc = external.Capture(pr.X, pr.Y, pr.Width, pr.Height, path);
            int countLater = -1;
            if (oc.Ok)
            {
                using var img = new Bitmap(path);
                countLater = PixelAnalyzer.CountNear(PixelAnalyzer.GetPixels(img), img.Width,
                    new Rectangle(0, 0, img.Width, img.Height), ProbeForm.SelfMarker, 60);
            }

            // 绝对像素数阈值：标记方块 48×48=2304px，>500px 即判定"仍能看到窗口"
            res.MarkerPixelsImmediatelyAfterClear = countT0;
            res.MarkerPixelsAfter400ms = countLater;
            res.VisibleImmediatelyAfterClear = countT0 >= 500;
            res.VisibleAfter400ms = countLater >= 500;
            res.WindowVisibleAgainToExternalObserver = res.VisibleAfter400ms;
            res.ExclusionDutyCycleAt2Fps = res.ExclusionWindowMs / 1000.0 * 2.0 * 100.0;
            res.Note = (res.VisibleImmediatelyAfterClear ? "清除后立即可见" : "清除后立即仍不可见")
                       + "；" + (res.VisibleAfter400ms ? "400ms 后可见" : "400ms 后仍不可见")
                       + $"；每帧排除 {res.ExclusionWindowMs:F1}ms，2fps 下占空比约 {res.ExclusionDutyCycleAt2Fps:F2}%";

            log.Info($"  瞬时排除: 排除窗口={res.ExclusionWindowMs:F1}ms 捕获CPU={res.CaptureCpuMs:F2}ms " +
                     $"立即可见={res.VisibleImmediatelyAfterClear} 400ms后可见={res.VisibleAfter400ms} 占空比={res.ExclusionDutyCycleAt2Fps:F2}%");
            return res;
        }
    }
}
