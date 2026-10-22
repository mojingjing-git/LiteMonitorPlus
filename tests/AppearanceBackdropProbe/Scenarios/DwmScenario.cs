using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using AppearanceBackdropProbe.Analysis;
using AppearanceBackdropProbe.Evidence;
using AppearanceBackdropProbe.Native;
using AppearanceBackdropProbe.Probe;

namespace AppearanceBackdropProbe.Scenarios
{
    internal sealed class DwmVariantResult
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Layered { get; set; }
        public bool PaintsContent { get; set; }
        public int BackdropType { get; set; }
        public string BackdropTypeName { get; set; } = "";
        public bool ExtendFrameIntoClientArea { get; set; }
        public int DwmSetWindowAttributeHResult { get; set; }
        public int DwmGetWindowAttributeHResult { get; set; }
        public int BackdropTypeReadBack { get; set; }
        public int DwmExtendFrameIntoClientAreaHResult { get; set; }
        public string ExStyleBefore { get; set; } = "";
        public string ExStyleAfter { get; set; } = "";
        public bool ExStyleHasLayered { get; set; }
        public bool ExStyleHasTransparent { get; set; }
        public string Capture1Path { get; set; } = "";
        public string Capture2Path { get; set; } = "";
        public bool ExternalCaptureOk { get; set; }
        public string ExternalCaptureMessage { get; set; } = "";
        public FrameStats Capture1 { get; set; } = new();
        public FrameStats Capture2 { get; set; } = new();
        public double TemporalDiff { get; set; } = -1;
        public double ExpectedHue1 { get; set; } = -1;
        public double ExpectedHue2 { get; set; } = -1;
        public double HueDistanceToExpected1 { get; set; } = -1;
        public double HueDistanceToExpected2 { get; set; } = -1;
        public double MeanAbsDiffVsBaseline { get; set; } = -1;
        public bool VisuallyChangedVsBaseline { get; set; }
        public bool LiveBackdrop { get; set; }
        /// <summary>测量期间背景场景的重绘次数 —— 为 0 表示背景没动，结论不成立。</summary>
        public int SceneRepaintsDuringMeasurement { get; set; }
        public bool MeasurementValid { get; set; }
        public string Note { get; set; } = "";
    }

    internal static class DwmScenario
    {
        private sealed class Variant
        {
            public string Name = "";
            public string Description = "";
            public bool Layered = true;
            public bool PaintContent = true;
            public bool PaintText = true;
            public bool ClickThrough = true;
            public int BackdropType = -1;
            public bool ExtendFrame = false;
        }

        public static void Run(Options opts, string outDir, LogWriter log)
        {
            var scriptDir = ScenarioSupport.ResolveScriptDir(opts);
            var external = new ExternalCapture(opts.PwshPath, Path.Combine(scriptDir, "external-capture.ps1"));
            log.Info(external.Info);

            var variants = new List<Variant>
            {
                new() { Name = "baseline-layered-prod", Description = "生产复刻基线：分层+Opacity+自绘内容，不设置任何材质属性", Layered = true, PaintContent = true, PaintText = true, BackdropType = -1 },
                new() { Name = "layered-mica", Description = "生产复刻 + DWMSBT_MAINWINDOW(Mica)", Layered = true, PaintContent = true, PaintText = true, BackdropType = Interop.DWMSBT_MAINWINDOW },
                new() { Name = "layered-acrylic", Description = "生产复刻 + DWMSBT_TRANSIENTWINDOW(Acrylic)", Layered = true, PaintContent = true, PaintText = true, BackdropType = Interop.DWMSBT_TRANSIENTWINDOW },
                new() { Name = "layered-acrylic-extend", Description = "生产复刻 + Acrylic + DwmExtendFrameIntoClientArea(-1)", Layered = true, PaintContent = true, PaintText = true, BackdropType = Interop.DWMSBT_TRANSIENTWINDOW, ExtendFrame = true },
                new() { Name = "baseline-unlayered", Description = "非分层对照基线：Opacity=1、无WS_EX_LAYERED、不设置材质属性", Layered = false, PaintContent = false, PaintText = false, ClickThrough = false, BackdropType = -1 },
                new() { Name = "unlayered-mica-extend", Description = "非分层对照(Opacity=1,无WS_EX_LAYERED) + Mica + 扩展边框", Layered = false, PaintContent = false, PaintText = false, ClickThrough = false, BackdropType = Interop.DWMSBT_MAINWINDOW, ExtendFrame = true },
                new() { Name = "unlayered-acrylic-extend", Description = "非分层对照 + Acrylic + 扩展边框", Layered = false, PaintContent = false, PaintText = false, ClickThrough = false, BackdropType = Interop.DWMSBT_TRANSIENTWINDOW, ExtendFrame = true },
                new() { Name = "unlayered-acrylic-noextend", Description = "非分层对照 + Acrylic，不扩展边框", Layered = false, PaintContent = false, PaintText = false, ClickThrough = false, BackdropType = Interop.DWMSBT_TRANSIENTWINDOW, ExtendFrame = false },
            };

            var results = new List<DwmVariantResult>();
            var baselines = new Dictionary<string, Bitmap>();

            foreach (var v in variants)
            {
                log.Info($"DWM 变体开始: {v.Name}");
                string family = v.Layered ? "layered" : "unlayered";
                baselines.TryGetValue(family, out var baseline);
                DwmVariantResult res = RunVariant(opts, outDir, log, external, v, baseline);
                results.Add(res);

                if (v.BackdropType < 0)
                {
                    string p = Path.Combine(outDir, res.Capture1Path);
                    if (File.Exists(p)) baselines[family] = new Bitmap(p);
                }
            }

            var summary = new
            {
                Scenario = "dwm",
                Item = "v4 §4 第 1 项：Win11 22621+ 文档化 DWM 系统材质",
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                OsBuild = Environment.OSVersion.Version.Build,
                ExternalObserver = external.Info,
                Variants = results,
                Conclusion = "见 results 文件正文",
            };
            EvidenceWriter.WriteJson(Path.Combine(outDir, "dwm-backdrop.json"), summary);
            log.Info("DWM 场景完成");
        }

        private static DwmVariantResult RunVariant(
            Options opts, string outDir, LogWriter log, ExternalCapture external, Variant v, Bitmap? baseline)
        {
            var res = new DwmVariantResult
            {
                Name = v.Name,
                Description = v.Description,
                Layered = v.Layered,
                PaintsContent = v.PaintContent,
                BackdropType = v.BackdropType,
                BackdropTypeName = ScenarioSupport.BackdropTypeName(v.BackdropType),
                ExtendFrameIntoClientArea = v.ExtendFrame,
            };

            using var rig = new Rig(opts, v.Layered, v.PaintContent, v.ClickThrough, 0.85,
                topMost: opts.UsesTopMost, allowActivation: opts.AllowsActivation);
            rig.Probe.PaintText = v.PaintText;
            rig.Probe.PaintContent = v.PaintContent;
            rig.Probe.Invalidate();
            Rig.Pump(500);

            res.ExStyleBefore = ScenarioSupport.ExStyleText(rig.Probe.Handle);
            res.ExStyleHasLayered = ScenarioSupport.HasLayered(rig.Probe.Handle);
            res.ExStyleHasTransparent = ScenarioSupport.HasTransparent(rig.Probe.Handle);

            if (v.BackdropType >= 0)
            {
                int type = v.BackdropType;
                res.DwmSetWindowAttributeHResult = Interop.DwmSetWindowAttribute(rig.Probe.Handle, Interop.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
                int readBack = 0;
                res.DwmGetWindowAttributeHResult = Interop.DwmGetWindowAttribute(rig.Probe.Handle, Interop.DWMWA_SYSTEMBACKDROP_TYPE, out readBack, sizeof(int));
                res.BackdropTypeReadBack = readBack;
            }
            else
            {
                res.DwmSetWindowAttributeHResult = int.MinValue; // 表示未调用
            }

            if (v.ExtendFrame)
            {
                var margins = new Interop.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                res.DwmExtendFrameIntoClientAreaHResult = Interop.DwmExtendFrameIntoClientArea(rig.Probe.Handle, ref margins);
            }
            else
            {
                res.DwmExtendFrameIntoClientAreaHResult = int.MinValue; // 未调用
            }

            rig.Probe.Invalidate();
            rig.Probe.Update();
            Rig.Pump(700);

            res.ExStyleAfter = ScenarioSupport.ExStyleText(rig.Probe.Handle);

            // 外部观察者抓两张（间隔约 1.2s），用于判定"背后内容是否随时间响应"
            rig.UpdateProbeRect();
            var rect = rig.ProbeScreenRect;
            string dir = Path.Combine(outDir, "dwm", v.Name);
            string p1 = Path.Combine(dir, "external-1.png");
            string p2 = Path.Combine(dir, "external-2.png");

            var o1 = external.Capture(rect.X, rect.Y, rect.Width, rect.Height, p1);
            double sceneT1 = rig.Scene.TimeSeconds;
            int repaints1 = rig.Scene.RepaintCount;
            Rig.Pump(1200);
            var o2 = external.Capture(rect.X, rect.Y, rect.Width, rect.Height, p2);
            double sceneT2 = rig.Scene.TimeSeconds;
            res.SceneRepaintsDuringMeasurement = rig.Scene.RepaintCount - repaints1;

            res.ExternalCaptureOk = o1.Ok && o2.Ok;
            res.ExternalCaptureMessage = $"{o1.Message} | {o2.Message}";
            res.Capture1Path = Path.Combine("dwm", v.Name, "external-1.png");
            res.Capture2Path = Path.Combine("dwm", v.Name, "external-2.png");
            res.ExpectedHue1 = ScenarioSupport.ExpectedHue(sceneT1);
            res.ExpectedHue2 = ScenarioSupport.ExpectedHue(sceneT2);

            using var refCrop = rig.ReferenceCrop();
            int[] refPixels = PixelAnalyzer.GetPixels(refCrop);

            if (o1.Ok)
            {
                using var bmp = new Bitmap(p1);
                res.Capture1 = PixelAnalyzer.Analyze(bmp, ScenarioSupport.StaticRegion(rig), ScenarioSupport.HueRegion(rig),
                    ScenarioSupport.BarRegion(rig), ProbeForm.SelfMarker, refPixels, ScenarioSupport.StaticRegion(rig));
                res.HueDistanceToExpected1 = ScenarioSupport.HueDistance(res.Capture1.HueDegrees, res.ExpectedHue1);
            }
            if (o2.Ok)
            {
                using var bmp = new Bitmap(p2);
                res.Capture2 = PixelAnalyzer.Analyze(bmp, ScenarioSupport.StaticRegion(rig), ScenarioSupport.HueRegion(rig),
                    ScenarioSupport.BarRegion(rig), ProbeForm.SelfMarker, refPixels, ScenarioSupport.StaticRegion(rig));
                res.HueDistanceToExpected2 = ScenarioSupport.HueDistance(res.Capture2.HueDegrees, res.ExpectedHue2);
            }

            if (o1.Ok && o2.Ok)
            {
                using var b1 = new Bitmap(p1);
                using var b2 = new Bitmap(p2);
                res.TemporalDiff = PixelAnalyzer.MeanAbsDiff(PixelAnalyzer.GetPixels(b1), PixelAnalyzer.GetPixels(b2));
            }

            if (baseline != null && o1.Ok)
            {
                using var b1 = new Bitmap(p1);
                res.MeanAbsDiffVsBaseline = PixelAnalyzer.MeanAbsDiff(PixelAnalyzer.GetPixels(baseline), PixelAnalyzer.GetPixels(b1));
                res.VisuallyChangedVsBaseline = res.MeanAbsDiffVsBaseline > 3.0;
            }

            // 自动判定：背后内容是否"实时响应"
            // 前提守卫：测量期间背景必须真的在动，否则"没变化"只说明装置失灵，不能算"没有实时响应"
            res.LiveBackdrop = res.SceneRepaintsDuringMeasurement > 0
                               && res.HueDistanceToExpected1 >= 0 && res.HueDistanceToExpected1 < 45
                               && res.TemporalDiff > 1.5;
            res.MeasurementValid = res.SceneRepaintsDuringMeasurement > 0;

            log.Info($"  {v.Name}: setHR=0x{res.DwmSetWindowAttributeHResult:X8} extendHR=0x{res.DwmExtendFrameIntoClientAreaHResult:X8} " +
                     $"场景重绘={res.SceneRepaintsDuringMeasurement} selfMarker={res.Capture1.SelfMarkerFraction:F4} staticPatch={res.Capture1.StaticPatchFraction:F4} " +
                     $"hueMeasured={res.Capture1.HueDegrees:F1} hueExpected={res.ExpectedHue1:F1} hueDist={res.HueDistanceToExpected1:F1} " +
                     $"temporalDiff={res.TemporalDiff:F2} diffVsBaseline={res.MeanAbsDiffVsBaseline:F2} live={res.LiveBackdrop} valid={res.MeasurementValid}");

            return res;
        }
    }
}
