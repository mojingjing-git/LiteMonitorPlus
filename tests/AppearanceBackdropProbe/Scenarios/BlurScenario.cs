using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using AppearanceBackdropProbe.Analysis;
using AppearanceBackdropProbe.Capture;
using AppearanceBackdropProbe.Evidence;

namespace AppearanceBackdropProbe.Scenarios
{
    internal sealed class BlurCaseResult
    {
        public string Name { get; set; } = "";
        public int BlurLogical { get; set; }
        public double DpiScale { get; set; }
        public double CaptureScale { get; set; }
        public int BoxRadius { get; set; }
        public double SigmaContractDisplay { get; set; }
        public double MeasuredEdgeRisePx { get; set; }
        public double MeasuredSigmaFromRise { get; set; }
        public string ContractDescription { get; set; } = "";
        public string ImagePath { get; set; } = "";
    }

    internal static class BlurScenario
    {
        private const int EdgeWidth = 512;
        private const int EdgeHeight = 64;
        private const int EdgeX = 256;

        public static void Run(Options opts, string outDir, LogWriter log)
        {
            var results = new List<BlurCaseResult>();
            var scaleOnly = new List<BlurCaseResult>();

            // 阶跃边缘：左黑右白
            using var edge = CreateStepEdge(EdgeWidth, EdgeHeight);
            using var checker = CreateChecker(256, 256, 16);

            foreach (int blur in new[] { 0, 12, 24 })
            {
                // (a) 全尺寸滤波（captureScale = 1）
                var c1 = new BlurContract { BlurLogical = blur, DpiScale = 1.0, CaptureScale = 1.0 };
                using (var blurred = BlurPipeline.ApplyBoxBlur3x(edge, c1.BoxRadius))
                {
                    double rise = MeasureRise(blurred);
                    string p = Path.Combine(outDir, "blur", $"edge_blur{blur}_scale1.png");
                    EvidenceWriter.SavePng(blurred, p);
                    results.Add(new BlurCaseResult
                    {
                        Name = $"阶跃边缘 Blur={blur} @captureScale=1",
                        BlurLogical = blur,
                        DpiScale = 1.0,
                        CaptureScale = 1.0,
                        BoxRadius = c1.BoxRadius,
                        SigmaContractDisplay = c1.SigmaEffectiveDisplay,
                        MeasuredEdgeRisePx = rise,
                        MeasuredSigmaFromRise = rise > 0 ? rise / 2.563 : -1,
                        ContractDescription = c1.Describe(),
                        ImagePath = Path.GetRelativePath(outDir, p),
                    });
                }

                // (b) 先降采样再滤波再升采样（生产常见路径）
                var c2 = new BlurContract { BlurLogical = blur, DpiScale = 1.0, CaptureScale = 0.25 };
                using (var processed = BlurPipeline.Process(edge, c2, out _))
                {
                    double rise = MeasureRise(processed);
                    string p = Path.Combine(outDir, "blur", $"edge_blur{blur}_scale0.25.png");
                    EvidenceWriter.SavePng(processed, p);
                    results.Add(new BlurCaseResult
                    {
                        Name = $"阶跃边缘 Blur={blur} @captureScale=0.25",
                        BlurLogical = blur,
                        DpiScale = 1.0,
                        CaptureScale = 0.25,
                        BoxRadius = c2.BoxRadius,
                        SigmaContractDisplay = c2.SigmaEffectiveDisplay,
                        MeasuredEdgeRisePx = rise,
                        MeasuredSigmaFromRise = rise > 0 ? rise / 2.563 : -1,
                        ContractDescription = c2.Describe(),
                        ImagePath = Path.GetRelativePath(outDir, p),
                    });
                }
            }

            // 对照：只缩小再放大（不含滤波）——证明"缩放近似"不是带半径合同的模糊
            foreach (int blur in new[] { 12, 24 })
            {
                var c = new BlurContract { BlurLogical = blur, DpiScale = 1.0, CaptureScale = 0.25 };
                using var small = BlurPipeline.Downscale(edge, 0.25f);
                using var up = BlurPipeline.Upscale(small, EdgeWidth, EdgeHeight);
                double rise = MeasureRise(up);
                string p = Path.Combine(outDir, "blur", $"edge_scaleonly_noBlur_{blur}.png");
                EvidenceWriter.SavePng(up, p);
                scaleOnly.Add(new BlurCaseResult
                {
                    Name = $"仅缩放(无滤波) captureScale=0.25，标称 Blur={blur}",
                    BlurLogical = blur,
                    DpiScale = 1.0,
                    CaptureScale = 0.25,
                    BoxRadius = 0,
                    SigmaContractDisplay = 0,
                    MeasuredEdgeRisePx = rise,
                    MeasuredSigmaFromRise = rise > 0 ? rise / 2.563 : -1,
                    ContractDescription = "无滤波：仅缩小再放大（缩放近似，不是模糊）",
                    ImagePath = Path.GetRelativePath(outDir, p),
                });
            }

            // 棋盘图：目视模糊程度
            using (var checkerBlurred = BlurPipeline.ApplyBoxBlur3x(checker, new BlurContract { BlurLogical = 12, CaptureScale = 1.0 }.BoxRadius))
            {
                EvidenceWriter.SavePng(checkerBlurred, Path.Combine(outDir, "blur", "checker_blur12_scale1.png"));
            }

            var byScale1 = results.Where(r => r.CaptureScale == 1.0).OrderBy(r => r.BlurLogical).ToList();
            double rise0 = byScale1.FirstOrDefault(r => r.BlurLogical == 0)?.MeasuredEdgeRisePx ?? -1;
            double rise12 = byScale1.FirstOrDefault(r => r.BlurLogical == 12)?.MeasuredEdgeRisePx ?? -1;
            double rise24 = byScale1.FirstOrDefault(r => r.BlurLogical == 24)?.MeasuredEdgeRisePx ?? -1;

            bool monotonic = rise0 >= 0 && rise12 > rise0 + 3 && rise24 > rise12 + 5;
            double ratio = rise12 > 0 ? rise24 / rise12 : -1;
            bool ratioOk = ratio >= 1.6 && ratio <= 2.4;

            var summary = new
            {
                Scenario = "blur",
                Item = "v4 §4 第 4 项：模糊半径合同",
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                Contract = "Blur 单位 = 96DPI 逻辑像素，表示最终显示尺寸下的等效高斯 σ；物理σ = 逻辑值×(DPI/96)；" +
                           "降采样后 σ_down = σ_physical×captureScale；实际滤波 = 3 次可分离 box blur（σ_eff=√(r(r+1))），O(1)/像素。",
                ExplicitlyNotBlur = "单纯缩小再放大只是缩放近似；SmoothingMode 不是模糊算法。",
                EdgeRiseModel = "高斯 10%–90% 上升沿宽度 ≈ 2.563σ（用于把半径合同变成可自动断言的数值）",
                Cases = results,
                ScaleOnlyControlCases = scaleOnly,
                Assertions = new
                {
                    Rise_Blur0 = rise0,
                    Rise_Blur12 = rise12,
                    Rise_Blur24 = rise24,
                    MonotonicWithRadius = monotonic,
                    Ratio24over12 = ratio,
                    RatioWithin1_6to2_4 = ratioOk,
                    Passed = monotonic && ratioOk,
                },
            };
            EvidenceWriter.WriteJson(Path.Combine(outDir, "blur-radius.json"), summary);

            log.Info($"  模糊半径: rise(0)={rise0:F2} rise(12)={rise12:F2} rise(24)={rise24:F2} 单调={monotonic} 比值={ratio:F2} 通过={monotonic && ratioOk}");
            foreach (var r in results)
                log.Info($"    {r.Name}: r={r.BoxRadius} σ合同={r.SigmaContractDisplay:F2} 实测上升沿={r.MeasuredEdgeRisePx:F2}px → σ实测={r.MeasuredSigmaFromRise:F2}");
        }

        private static double MeasureRise(Bitmap bmp)
        {
            int[] px = PixelAnalyzer.GetPixels(bmp);
            return PixelAnalyzer.EdgeRiseWidth(px, bmp.Width, bmp.Height, EdgeHeight / 2, 40, EdgeWidth - 40);
        }

        private static Bitmap CreateStepEdge(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            using var b = new SolidBrush(Color.White);
            g.FillRectangle(b, EdgeX, 0, w - EdgeX, h);
            return bmp;
        }

        private static Bitmap CreateChecker(int w, int h, int cell)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            using var b = new SolidBrush(Color.White);
            for (int y = 0; y < h; y += cell)
                for (int x = 0; x < w; x += cell)
                    if (((x / cell) + (y / cell)) % 2 == 1) g.FillRectangle(b, x, y, cell, cell);
            return bmp;
        }
    }
}
