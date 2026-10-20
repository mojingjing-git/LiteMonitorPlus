using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;

namespace LiteMonitor.AppearanceStaticBackdropProbe
{
    // ================================================================
    // A08 静态背景与降级：自证程序
    //
    // 覆盖用例：
    //  1. solid 模式与旧路径像素一致（新能力关闭时零副作用、零分配）
    //  2. gradient 缺省两端色 + 缺省角度 90°，以及显式两端色与 0/90/180/270/45 角度
    //  3. glass 后端不可用 → 明确降级 gradient → solid，并给出可读原因
    //  4. tint 存在/缺省 × Opacity 0/0.5/1，Opacity 只作用于背景层
    //  5. noise=0 零开销；noise>0 有界且可复现
    //  6. edge glow=0 零开销
    //  7. 连续 100 次绘制像素完全一致（幂等）
    //  8. 1×1 / 宽高为 0 → 跳过绘制不抛异常
    //  9. 预览使用固定源，且不启动捕获
    // 10. 不创建后台线程/计时器
    // ================================================================

    internal static class Program
    {
        private static readonly List<CaseResult> Cases = new();
        private static int _threadsBefore;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 控制台不支持时忽略 */ }

            // 预热后再记线程数，避免把运行时自身的惰性线程算进来
            Warmup();
            _threadsBefore = CurrentThreadCount();

            RunCase("C1-solid-legacy-parity", "solid 模式：与旧路径像素一致、零副作用、零分配", CaseSolidMatchesLegacy);
            RunCase("C1b-backdrop-missing-noop", "整个 Backdrop 缺失：零副作用、零分配", CaseBackdropMissing);
            RunCase("C2a-gradient-default", "gradient 缺省两端色 + 缺省角度 90°", CaseGradientDefaults);
            RunCase("C2b-gradient-angles", "gradient 显式两端色与 0/90/180/270/45 角度方向", CaseGradientAngles);
            RunCase("C3a-glass-degrade-no-backend", "glass 无后端 → 明确降级到 gradient", CaseGlassDegradeNoBackend);
            RunCase("C3b-glass-degrade-not-ready", "glass 后端未就绪 → 降级并带上后端原因", CaseGlassDegradeNotReady);
            RunCase("C3c-glass-degrade-invalid-frame", "glass 后端未给出有效帧 → 降级", CaseGlassDegradeInvalidFrame);
            RunCase("C3d-glass-degrade-to-solid", "glass 且渐变不可用 → 降级到 solid", CaseGlassDegradeToSolid);
            RunCase("C3e-glass-live-frame", "glass 后端就绪 → 生效且不降级", CaseGlassLiveFrame);
            RunCase("C4-tint-opacity", "tint 存在/缺省 × Opacity 0/0.5/1，只作用于背景层", CaseTintOpacity);
            RunCase("C5a-noise-zero", "noise=0：不分配位图", CaseNoiseZero);
            RunCase("C5b-noise-deterministic", "noise>0：有界且固定种子可复现", CaseNoiseDeterministic);
            RunCase("C6-edgeglow-zero", "edge glow=0：零开销", CaseEdgeGlowZero);
            RunCase("C7-idempotent-100", "连续 100 次绘制像素完全一致", CaseIdempotent100);
            RunCase("C8a-bounds-1x1", "1×1 客户区：不抛异常", CaseBounds1x1);
            RunCase("C8b-bounds-zero", "宽高为 0：跳过绘制", CaseBoundsZero);
            RunCase("C9-preview-fixed-source", "预览使用固定源且不启动捕获", CasePreviewFixedSource);
            RunCase("C10-no-background-thread", "不创建后台线程/计时器", CaseNoBackgroundThread);

            int threadsAfter = CurrentThreadCount();

            var evidence = new
            {
                probe = "AppearanceStaticBackdropProbe",
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                mainAssemblySha256 = MainAssemblyHash(),
                threadCountBefore = _threadsBefore,
                threadCountAfter = threadsAfter,
                caseCount = Cases.Count,
                passedCount = Cases.Count(c => c.Passed),
                failedCount = Cases.Count(c => !c.Passed),
                allPassed = Cases.All(c => c.Passed),
                cases = Cases
            };

            string outPath = Path.Combine(AppContext.BaseDirectory, "a08-static-backdrop-evidence.json");
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            File.WriteAllText(outPath, JsonSerializer.Serialize(evidence, jsonOptions), new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine("==== A08 静态背景与降级：自证结果 ====");
            foreach (CaseResult c in Cases)
            {
                Console.WriteLine($"[{(c.Passed ? "PASS" : "FAIL")}] {c.Name} — {c.Title}");
                foreach (string f in c.Failures) Console.WriteLine("       ! " + f);
            }
            Console.WriteLine();
            Console.WriteLine($"用例 {evidence.caseCount} 个，通过 {evidence.passedCount} 个，失败 {evidence.failedCount} 个。");
            Console.WriteLine("证据文件：" + outPath);
            return evidence.allPassed ? 0 : 1;
        }

        // ================================================================
        // 用例实现
        // ================================================================

        // C1：solid 模式与旧路径像素一致
        private static void CaseSolidMatchesLegacy(CaseResult c)
        {
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "solid" });
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            ProbeSupport.FillLegacyBackground(surface, options.BackgroundColor);
            string legacyHash = ProbeSupport.Hash(surface);

            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }
            string afterHash = ProbeSupport.Hash(surface);

            c.Details["legacyHash"] = legacyHash;
            c.Details["afterRenderHash"] = afterHash;
            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["layers"] = status.Layers.ToString();
            c.Details["degraded"] = status.IsDegraded;

            Require(c, legacyHash == afterHash, "solid 模式改变了像素（与旧路径不一致）");
            Require(c, status.EffectiveMode == BackdropRenderMode.Solid, "solid 模式的有效模式不是 solid");
            Require(c, status.Layers == BackdropLayer.None, "solid 模式绘制了图层");
            Require(c, !status.IsDegraded, "solid 模式不应被标记为降级");

            long alloc = MeasureSolidAlloc(renderer, options, surface);
            c.Details["allocBytesPerRender"] = alloc;
            Require(c, alloc == 0, $"solid 模式每次渲染分配了 {alloc} 字节（应为 0）");
        }

        // C1b：整个 Backdrop 缺失
        private static void CaseBackdropMissing(CaseResult c)
        {
            var options = ProbeSupport.MakeOptions(null);
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            ProbeSupport.FillLegacyBackground(surface, options.BackgroundColor);
            string legacyHash = ProbeSupport.Hash(surface);

            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }
            c.Details["requestedMode"] = status.RequestedMode;
            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["legacyHash"] = legacyHash;
            c.Details["afterRenderHash"] = ProbeSupport.Hash(surface);

            Require(c, status.RequestedMode == "solid", "Backdrop 缺失时请求模式不是 solid");
            Require(c, status.Layers == BackdropLayer.None, "Backdrop 缺失时绘制了图层");
            Require(c, legacyHash == ProbeSupport.Hash(surface), "Backdrop 缺失时改变了像素");

            long alloc = MeasureSolidAlloc(renderer, options, surface);
            c.Details["allocBytesPerRender"] = alloc;
            Require(c, alloc == 0, $"Backdrop 缺失时每次渲染分配了 {alloc} 字节（应为 0）");
        }

        // C2a：gradient 缺省两端色 + 缺省角度 90°
        private static void CaseGradientDefaults(CaseResult c)
        {
            // Opacity=0 是为了单独观察渐变本身（tint 单独在 C4 验证）
            var backdrop = new BackdropConfig { Mode = "gradient", Opacity = 0 };
            var options = ProbeSupport.MakeOptions(backdrop);
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(surface))
            {
                renderer.Render(g, options);
            }

            Color start = options.GradientStartColor;
            Color end = options.GradientEndColor;
            Color top = surface.GetPixel(32, 0);
            Color bottom = surface.GetPixel(32, 47);

            c.Details["gradientStartArgb"] = start.ToArgb().ToString("X8");
            c.Details["gradientEndArgb"] = end.ToArgb().ToString("X8");
            c.Details["backgroundArgb"] = options.BackgroundColor.ToArgb().ToString("X8");
            c.Details["groupBackgroundArgb"] = options.GroupBackgroundColor.ToArgb().ToString("X8");
            c.Details["angle"] = options.GradientAngle;
            c.Details["topPixel"] = top.ToArgb().ToString("X8");
            c.Details["bottomPixel"] = bottom.ToArgb().ToString("X8");
            c.Details["hash"] = ProbeSupport.Hash(surface);

            Require(c, start == options.BackgroundColor, "渐变起始色缺省值不是 Background");
            Require(c, end == options.GroupBackgroundColor, "渐变结束色缺省值不是 GroupBackground");
            Require(c, Math.Abs(options.GradientAngle - 90.0) < 0.001, "渐变角度缺省值不是 90°");
            Require(c, ProbeSupport.ChannelDistance(top, start) <= 2, $"渐变顶端像素 {top} 与起始色 {start} 偏差过大");
            Require(c, ProbeSupport.ChannelDistance(bottom, end) <= 6, $"渐变底端像素 {bottom} 与结束色 {end} 偏差过大");

            // 沿中轴单调变化（允许 1 的抖动）
            bool monotonic = true;
            int previous = surface.GetPixel(32, 0).R;
            for (int y = 1; y < surface.Height; y++)
            {
                int current = surface.GetPixel(32, y).R;
                if (current < previous - 1) { monotonic = false; break; }
                previous = current;
            }
            c.Details["monotonicTopToBottom"] = monotonic;
            Require(c, monotonic, "渐变沿 90° 方向不是单调变化");
        }

        // C2b：显式两端色与各种角度的方向
        private static void CaseGradientAngles(CaseResult c)
        {
            var results = new List<string>();
            foreach (double angle in new double[] { 0, 45, 90, 180, 270 })
            {
                var backdrop = new BackdropConfig
                {
                    Mode = "gradient",
                    Opacity = 0,
                    GradientStart = "#000000",
                    GradientEnd = "#FFFFFF",
                    GradientAngle = angle
                };
                var options = ProbeSupport.MakeOptions(backdrop);
                using var renderer = new BackdropRenderer();
                using Bitmap surface = ProbeSupport.NewSurface(64, 48);
                using (Graphics g = Graphics.FromImage(surface))
                {
                    renderer.Render(g, options);
                }

                Color left = ProbeSupport.AverageColor(surface, new Rectangle(0, 16, 12, 16));
                Color right = ProbeSupport.AverageColor(surface, new Rectangle(52, 16, 12, 16));
                Color top = ProbeSupport.AverageColor(surface, new Rectangle(16, 0, 32, 8));
                Color bottom = ProbeSupport.AverageColor(surface, new Rectangle(16, 40, 32, 8));
                Color topLeft = ProbeSupport.AverageColor(surface, new Rectangle(0, 0, 16, 12));
                Color bottomRight = ProbeSupport.AverageColor(surface, new Rectangle(48, 36, 16, 12));

                bool ok = angle switch
                {
                    0 => left.R < right.R - 30,
                    90 => top.R < bottom.R - 30,
                    180 => right.R < left.R - 30,
                    270 => bottom.R < top.R - 30,
                    45 => topLeft.R < bottomRight.R - 30,
                    _ => false
                };

                results.Add($"{angle:0}°:{(ok ? "ok" : "bad")}");
                c.Details[$"angle{angle:0}"] = new
                {
                    left = left.ToArgb().ToString("X8"),
                    right = right.ToArgb().ToString("X8"),
                    top = top.ToArgb().ToString("X8"),
                    bottom = bottom.ToArgb().ToString("X8"),
                    topLeft = topLeft.ToArgb().ToString("X8"),
                    bottomRight = bottomRight.ToArgb().ToString("X8"),
                    directionOk = ok
                };
                Require(c, ok, $"{angle}° 渐变方向不正确（起点/终点颠倒或退化）");
            }
            c.Details["summary"] = string.Join(", ", results);
        }

        // C3a：glass 且没有注册后端
        private static void CaseGlassDegradeNoBackend(CaseResult c)
        {
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "glass" });
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }

            c.Details["requestedMode"] = status.RequestedMode;
            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["degraded"] = status.IsDegraded;
            c.Details["reasonCode"] = status.ReasonCode.ToString();
            c.Details["reason"] = status.Reason;
            c.Details["hash"] = ProbeSupport.Hash(surface);

            Require(c, status.RequestedMode == "glass", "请求模式不是 glass");
            Require(c, status.EffectiveMode == BackdropRenderMode.Gradient, "没有后端时没有降级到 gradient");
            Require(c, status.IsDegraded, "没有标记为降级");
            Require(c, status.ReasonCode == BackdropDegradeReason.BackendMissing, "降级原因码不是 BackendMissing");
            Require(c, !string.IsNullOrWhiteSpace(status.Reason), "降级没有可读原因");
            Require(c, status.Source == BackdropFrameSource.Gradient, "降级后的底图来源不是 gradient");

            // 降级后的画面应与直接用 gradient 渲染一致
            using var plain = new BackdropRenderer();
            using Bitmap plainSurface = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(plainSurface))
            {
                plain.Render(g, ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient" }));
            }
            c.Details["plainGradientHash"] = ProbeSupport.Hash(plainSurface);
            Require(c, ProbeSupport.Hash(plainSurface) == ProbeSupport.Hash(surface), "降级到 gradient 后的画面与直接 gradient 不一致");
        }

        // C3b：后端未就绪
        private static void CaseGlassDegradeNotReady(CaseResult c)
        {
            var source = new FakeFrameSource { IsReady = false, UnavailableReason = "DWM 系统材质在当前系统不可用" };
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "glass" });
            using var renderer = new BackdropRenderer(source);
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }

            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["degraded"] = status.IsDegraded;
            c.Details["reasonCode"] = status.ReasonCode.ToString();
            c.Details["reason"] = status.Reason;
            c.Details["backendTryGetFrameCalls"] = source.TryGetFrameCalls;

            Require(c, status.EffectiveMode == BackdropRenderMode.Gradient, "后端未就绪时没有降级到 gradient");
            Require(c, status.IsDegraded, "后端未就绪时没有标记降级");
            Require(c, status.ReasonCode == BackdropDegradeReason.BackendNotReady, "降级原因码不是 BackendNotReady");
            Require(c, status.Reason != null && status.Reason.Contains("DWM 系统材质在当前系统不可用"), "降级原因没有带上后端的可读原因");
            Require(c, source.TryGetFrameCalls == 0, "后端未就绪时不应尝试取帧");
        }

        // C3c：后端声称就绪但没给出有效帧
        private static void CaseGlassDegradeInvalidFrame(CaseResult c)
        {
            var source = new FakeFrameSource { IsReady = true, Frame = null, UnavailableReason = "帧句柄失效" };
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "glass" });
            using var renderer = new BackdropRenderer(source);
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }

            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["degraded"] = status.IsDegraded;
            c.Details["reasonCode"] = status.ReasonCode.ToString();
            c.Details["reason"] = status.Reason;
            c.Details["backendTryGetFrameCalls"] = source.TryGetFrameCalls;

            Require(c, status.IsDegraded, "无效帧没有标记降级");
            Require(c, status.ReasonCode == BackdropDegradeReason.BackendFrameInvalid, "降级原因码不是 BackendFrameInvalid");
            Require(c, source.TryGetFrameCalls == 1, "后端就绪时应当取过一次帧");
        }

        // C3d：渐变也不可用 → 降级到 solid
        private static void CaseGlassDegradeToSolid(CaseResult c)
        {
            var backdrop = new BackdropConfig
            {
                Mode = "glass",
                GradientStart = "#00000000",
                GradientEnd = "#00000000"
            };
            var options = ProbeSupport.MakeOptions(backdrop);
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            ProbeSupport.FillLegacyBackground(surface, options.BackgroundColor);
            string before = ProbeSupport.Hash(surface);

            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }
            string after = ProbeSupport.Hash(surface);

            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["degraded"] = status.IsDegraded;
            c.Details["reasonCode"] = status.ReasonCode.ToString();
            c.Details["reason"] = status.Reason;
            c.Details["layers"] = status.Layers.ToString();
            c.Details["legacyHash"] = before;
            c.Details["afterRenderHash"] = after;

            Require(c, status.EffectiveMode == BackdropRenderMode.Solid, "渐变不可用时没有降级到 solid");
            Require(c, status.IsDegraded, "降级到 solid 没有标记降级");
            Require(c, status.ReasonCode == BackdropDegradeReason.GradientUnavailable, "降级原因码不是 GradientUnavailable");
            Require(c, status.Layers == BackdropLayer.None, "降级到 solid 后仍绘制了图层");
            Require(c, before == after, "降级到 solid 后仍然改动了像素");
        }

        // C3e：后端就绪 → glass 生效
        private static void CaseGlassLiveFrame(CaseResult c)
        {
            using Bitmap frame = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(frame))
            {
                using var brush = new SolidBrush(Color.FromArgb(0x33, 0x66, 0xFF));
                g.FillRectangle(brush, new Rectangle(0, 0, 64, 48));
            }

            var source = new FakeFrameSource { IsReady = true, Frame = frame };
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "glass", Opacity = 0 });
            using var renderer = new BackdropRenderer(source);
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }

            Color pixel = surface.GetPixel(32, 24);
            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["degraded"] = status.IsDegraded;
            c.Details["source"] = status.Source.ToString();
            c.Details["centerPixel"] = pixel.ToArgb().ToString("X8");
            c.Details["backendBlurPx"] = source.LastBlurPx;
            c.Details["optionsBlurPx"] = options.BlurPx;

            Require(c, status.EffectiveMode == BackdropRenderMode.Glass, "后端就绪时没有走 glass");
            Require(c, !status.IsDegraded, "后端就绪时不应标记降级");
            Require(c, status.Source == BackdropFrameSource.LiveFrame, "底图来源不是 LiveFrame");
            Require(c, pixel == Color.FromArgb(0x33, 0x66, 0xFF), $"实时帧没有绘制到画面上（中心像素 {pixel}）");
            Require(c, Math.Abs(source.LastBlurPx - options.BlurPx) < 0.001, "没有把 options.BlurPx 传给后端");

            // 借用帧不得被释放
            Require(c, frame.Width == 64, "渲染后借用的帧被释放了");
        }

        // C4：tint 与 Opacity
        private static void CaseTintOpacity(CaseResult c)
        {
            // 底板固定为不透明黑（渐变两端都是纯黑），tint 为不透明红
            var red = new ColorConfig { Background = "#000000", GroupBackground = "#000000" };

            // Opacity = 0 → 不产生染色层
            var opt0 = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, Tint = "#FF0000" }, red);
            Color p0 = RenderSample(opt0, out BackdropStatus s0);
            c.Details["opacity0Pixel"] = p0.ToArgb().ToString("X8");
            c.Details["opacity0Layers"] = s0.Layers.ToString();
            Require(c, p0 == Color.FromArgb(0, 0, 0), $"Opacity=0 时出现了染色（像素 {p0}）");
            Require(c, (s0.Layers & BackdropLayer.Tint) == 0, "Opacity=0 时仍然报告了染色层");

            // Opacity = 0.5 → alpha = 255 × 0.5 = 128
            var optHalf = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0.5, Tint = "#FF0000" }, red);
            Color pHalf = RenderSample(optHalf, out BackdropStatus sHalf);
            c.Details["opacity0_5Pixel"] = pHalf.ToArgb().ToString("X8");
            c.Details["opacity0_5Layers"] = sHalf.Layers.ToString();
            Require(c, ProbeSupport.ChannelDistance(pHalf, Color.FromArgb(128, 0, 0)) <= 2, $"Opacity=0.5 的叠加结果不对（像素 {pHalf}）");
            Require(c, (sHalf.Layers & BackdropLayer.Tint) != 0, "Opacity=0.5 时没有报告染色层");

            // Opacity = 1 → 完全被染色覆盖
            var optFull = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 1, Tint = "#FF0000" }, red);
            Color pFull = RenderSample(optFull, out _);
            c.Details["opacity1Pixel"] = pFull.ToArgb().ToString("X8");
            Require(c, ProbeSupport.ChannelDistance(pFull, Color.FromArgb(255, 0, 0)) <= 2, $"Opacity=1 的叠加结果不对（像素 {pFull}）");

            // Tint 缺省 → 取 Background（这里让 Background 明显区别于渐变，才能观察到染色）
            var gray = new ColorConfig { Background = "#404040", GroupBackground = "#404040" };
            var optDefaultTint = ProbeSupport.MakeOptions(
                new BackdropConfig { Mode = "gradient", Opacity = 0.5, GradientStart = "#000000", GradientEnd = "#000000" },
                gray);
            Color pDefault = RenderSample(optDefaultTint, out _);
            c.Details["defaultTintArgb"] = optDefaultTint.TintColor.ToArgb().ToString("X8");
            c.Details["defaultTintPixel"] = pDefault.ToArgb().ToString("X8");
            Require(c, optDefaultTint.TintColor.ToArgb() == optDefaultTint.BackgroundColor.ToArgb(), "Tint 缺省时没有取 Background");
            Require(c, ProbeSupport.ChannelDistance(pDefault, Color.FromArgb(32, 32, 32)) <= 2, $"Tint 缺省时的叠加结果不对（像素 {pDefault}）");

            // Tint 自身 alpha 与 Opacity 相乘
            var optHalfAlpha = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0.5, Tint = "rgba(255,0,0,0.5)" }, red);
            Color pHalfAlpha = RenderSample(optHalfAlpha, out _);
            c.Details["tintHalfAlphaPixel"] = pHalfAlpha.ToArgb().ToString("X8");
            Require(c, ProbeSupport.ChannelDistance(pHalfAlpha, Color.FromArgb(64, 0, 0)) <= 2, $"Tint 自身 alpha 没有与 Opacity 相乘（像素 {pHalfAlpha}）");

            // Opacity 只作用于背景层：客户区之外的像素不受影响
            // 注意：.NET 的 Color.op_Equality 对"命名颜色 vs 构造出的同色"会返回 false，
            // 因此这里一律比较 ARGB 整数，不直接用 Color ==。
            Color magenta = Color.FromArgb(255, 255, 0, 255);
            using var bounded = ProbeSupport.NewSurface(80, 60);
            using (Graphics g = Graphics.FromImage(bounded))
            {
                using var brush = new SolidBrush(magenta);
                g.FillRectangle(brush, new Rectangle(0, 0, 80, 60));
            }
            var boundedOptions = ProbeSupport.MakeOptions(
                new BackdropConfig { Mode = "gradient", Opacity = 1, Tint = "#FF0000" },
                red,
                new Rectangle(10, 5, 64, 48));
            using (Graphics g = Graphics.FromImage(bounded))
            {
                new BackdropRenderer().Render(g, boundedOptions);
            }
            Color outside = bounded.GetPixel(2, 2);
            Color inside = bounded.GetPixel(32, 24);
            c.Details["outsidePixel"] = outside.ToArgb().ToString("X8");
            c.Details["insidePixel"] = inside.ToArgb().ToString("X8");
            Require(c, outside.ToArgb() == magenta.ToArgb(), "染色越出了客户区（影响了背景层之外的像素）");
            Require(c, inside.ToArgb() == Color.FromArgb(255, 0, 0).ToArgb(), "客户区内没有正确染色");
        }

        // C5a：noise = 0 零开销
        private static void CaseNoiseZero(CaseResult c)
        {
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, Noise = 0 });
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(surface))
            {
                renderer.Render(g, options);
            }

            c.Details["noiseCacheEntryCount"] = renderer.NoiseCacheEntryCount;
            c.Details["noiseCacheBytes"] = renderer.NoiseCacheBytes;
            c.Details["layers"] = renderer.LastStatus.Layers.ToString();

            Require(c, renderer.NoiseCacheEntryCount == 0, "noise=0 却创建了噪点纹理");
            Require(c, renderer.NoiseCacheBytes == 0, "noise=0 却占用了位图预算");
            Require(c, (renderer.LastStatus.Layers & BackdropLayer.Noise) == 0, "noise=0 却报告了噪点层");
        }

        // C5b：noise > 0 有界且可复现
        private static void CaseNoiseDeterministic(CaseResult c)
        {
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, Noise = 40 });
            using var renderer = new BackdropRenderer();

            using Bitmap a = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(a)) renderer.Render(g, options);
            using Bitmap b = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(b)) renderer.Render(g, options);

            string hashA = ProbeSupport.Hash(a);
            string hashB = ProbeSupport.Hash(b);
            c.Details["hashA"] = hashA;
            c.Details["hashB"] = hashB;
            c.Details["noiseCacheEntryCount"] = renderer.NoiseCacheEntryCount;
            c.Details["noiseCacheBytes"] = renderer.NoiseCacheBytes;

            Require(c, hashA == hashB, "固定种子下两次渲染的噪点不一致");
            Require(c, renderer.NoiseCacheEntryCount == 1, "噪点纹理块数量不为 1");
            Require(c, renderer.NoiseCacheBytes == 128L * 128L * 4L, $"噪点位图占用 {renderer.NoiseCacheBytes} 字节，与核算不符");

            // 与 noise=0 的画面必须不同（噪点确实生效）
            var quiet = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, Noise = 0 });
            using Bitmap noNoise = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(noNoise))
            {
                new BackdropRenderer().Render(g, quiet);
            }
            Require(c, ProbeSupport.Hash(noNoise) != hashA, "噪点没有改变画面");

            // 稳态分配：纹理块缓存后不再分配位图
            long allocSteady = ProbeSupport.MeasureAlloc(() =>
            {
                using Bitmap tmp = ProbeSupport.NewSurface(64, 48);
                using Graphics g = Graphics.FromImage(tmp);
                renderer.Render(g, options);
            });
            c.Details["steadyAllocBytesWithSurface"] = allocSteady;
            c.Details["note"] = "稳态分配里含画布与 Graphics 本身，只用于观察不再出现位图分配";
        }

        // C6：edge glow = 0 零开销
        private static void CaseEdgeGlowZero(CaseResult c)
        {
            var plain = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, EdgeGlow = 0 });
            var glow = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, EdgeGlow = 60 });

            long allocPlain = MeasureGradientAlloc(plain);
            long allocZero = MeasureGradientAlloc(ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, EdgeGlow = 0 }));
            long allocGlow = MeasureGradientAlloc(glow);

            using Bitmap surfacePlain = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(surfacePlain)) new BackdropRenderer().Render(g, plain);
            using Bitmap surfaceGlow = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(surfaceGlow)) new BackdropRenderer().Render(g, glow);

            c.Details["allocGradientOnly"] = allocPlain;
            c.Details["allocEdgeGlowZero"] = allocZero;
            c.Details["allocEdgeGlow60"] = allocGlow;
            c.Details["hashEdgeGlowZero"] = ProbeSupport.Hash(surfacePlain);
            c.Details["hashEdgeGlow60"] = ProbeSupport.Hash(surfaceGlow);

            Require(c, allocZero == allocPlain, "EdgeGlow=0 与不启用边缘辉光的分配不一致");
            Require(c, allocGlow > allocPlain, "EdgeGlow>0 没有产生任何额外开销，辉光可能没生效");
            Require(c, ProbeSupport.Hash(surfacePlain) != ProbeSupport.Hash(surfaceGlow), "边缘辉光没有改变画面");
        }

        // C7：连续 100 次绘制像素完全一致
        private static void CaseIdempotent100(CaseResult c)
        {
            var full = new BackdropConfig { Mode = "gradient", Opacity = 0.5, Tint = "#204080", Noise = 35, EdgeGlow = 45 };
            var options = ProbeSupport.MakeOptions(full);

            using var renderer = new BackdropRenderer();
            using Bitmap once = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(once)) renderer.Render(g, options);
            string hashOnce = ProbeSupport.Hash(once);

            using Bitmap repeated = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(repeated))
            {
                for (int i = 0; i < 100; i++) renderer.Render(g, options);
            }
            string hashRepeated = ProbeSupport.Hash(repeated);

            c.Details["hashOnce"] = hashOnce;
            c.Details["hashAfter100"] = hashRepeated;
            c.Details["noiseCacheEntryCount"] = renderer.NoiseCacheEntryCount;
            Require(c, hashOnce == hashRepeated, "连续 100 次绘制后像素与单次绘制不一致（存在累积变色）");

            // 降级路径同样要幂等
            var glassOptions = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "glass", Opacity = 0.5, Noise = 20, EdgeGlow = 30 });
            using var glassRenderer = new BackdropRenderer();
            using Bitmap glassOnce = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(glassOnce)) glassRenderer.Render(g, glassOptions);
            using Bitmap glassRepeated = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(glassRepeated))
            {
                for (int i = 0; i < 100; i++) glassRenderer.Render(g, glassOptions);
            }
            c.Details["glassHashOnce"] = ProbeSupport.Hash(glassOnce);
            c.Details["glassHashAfter100"] = ProbeSupport.Hash(glassRepeated);
            Require(c, ProbeSupport.Hash(glassOnce) == ProbeSupport.Hash(glassRepeated), "降级路径连续 100 次绘制后像素不一致");
        }

        // C8a：1×1
        private static void CaseBounds1x1(CaseResult c)
        {
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0, Noise = 20 }, bounds: new Rectangle(0, 0, 1, 1));
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(1, 1);
            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }
            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["layers"] = status.Layers.ToString();
            c.Details["hash"] = ProbeSupport.Hash(surface);
            Require(c, status.EffectiveMode == BackdropRenderMode.Gradient, "1×1 时没有正常渲染");
        }

        // C8b：宽高为 0
        private static void CaseBoundsZero(CaseResult c)
        {
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient" }, bounds: new Rectangle(0, 0, 0, 0));
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(8, 8);
            ProbeSupport.FillLegacyBackground(surface, options.BackgroundColor);
            string before = ProbeSupport.Hash(surface);

            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }
            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["reasonCode"] = status.ReasonCode.ToString();
            c.Details["layers"] = status.Layers.ToString();
            c.Details["hashBefore"] = before;
            c.Details["hashAfter"] = ProbeSupport.Hash(surface);

            Require(c, status.ReasonCode == BackdropDegradeReason.InvalidBounds, "宽高为 0 时没有报告 InvalidBounds");
            Require(c, status.Layers == BackdropLayer.None, "宽高为 0 时仍然绘制了图层");
            Require(c, before == ProbeSupport.Hash(surface), "宽高为 0 时仍然改动了像素");
        }

        // C9：预览使用固定源，不启动捕获
        private static void CasePreviewFixedSource(CaseResult c)
        {
            using Bitmap preview = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(preview))
            {
                using var brush = new SolidBrush(Color.FromArgb(0x22, 0xCC, 0x88));
                g.FillRectangle(brush, new Rectangle(0, 0, 64, 48));
            }

            var source = new FakeFrameSource { IsReady = true, Frame = preview };
            var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "glass", Opacity = 0 }, previewSource: preview);
            using var renderer = new BackdropRenderer(source);
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            BackdropStatus status;
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }

            Color pixel = surface.GetPixel(32, 24);
            c.Details["effectiveMode"] = status.EffectiveModeName;
            c.Details["source"] = status.Source.ToString();
            c.Details["degraded"] = status.IsDegraded;
            c.Details["backendTryGetFrameCalls"] = source.TryGetFrameCalls;
            c.Details["centerPixel"] = pixel.ToArgb().ToString("X8");

            Require(c, status.EffectiveMode == BackdropRenderMode.Glass, "预览没有走 glass 路径");
            Require(c, status.Source == BackdropFrameSource.PreviewFrame, "预览底图来源不是 PreviewFrame");
            Require(c, !status.IsDegraded, "预览固定源不应被标记为降级");
            Require(c, source.TryGetFrameCalls == 0, "预览路径仍然访问了实时后端（启动了捕获链路）");
            Require(c, pixel == Color.FromArgb(0x22, 0xCC, 0x88), $"预览固定源没有绘制到画面（像素 {pixel}）");
            Require(c, preview.Width == 64, "预览固定源被释放了");
        }

        // C10：不创建后台线程
        private static void CaseNoBackgroundThread(CaseResult c)
        {
            int before = _threadsBefore;
            int after = CurrentThreadCount();
            c.Details["threadsBefore"] = before;
            c.Details["threadsAfter"] = after;
            c.Details["delta"] = after - before;
            Require(c, after == before, $"渲染期间线程数从 {before} 变成 {after}（出现了后台线程/计时器）");
        }

        // ================================================================
        // 辅助
        // ================================================================

        private static void RunCase(string name, string title, Action<CaseResult> body)
        {
            var result = new CaseResult { Name = name, Title = title };
            try
            {
                body(result);
            }
            catch (Exception ex)
            {
                result.Failures.Add("用例抛出异常：" + ex.GetType().Name + " — " + ex.Message);
            }
            result.Passed = result.Failures.Count == 0;
            Cases.Add(result);
        }

        private static void Require(CaseResult c, bool condition, string message)
        {
            if (!condition) c.Failures.Add(message);
        }

        private static Color RenderSample(ThemeAppearanceOptions options, out BackdropStatus status)
        {
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            using (Graphics g = Graphics.FromImage(surface))
            {
                status = renderer.Render(g, options);
            }
            return surface.GetPixel(32, 24);
        }

        private static long MeasureSolidAlloc(BackdropRenderer renderer, ThemeAppearanceOptions options, Bitmap surface)
        {
            using Graphics g = Graphics.FromImage(surface);
            return ProbeSupport.MeasureAlloc(() => renderer.Render(g, options));
        }

        private static long MeasureGradientAlloc(ThemeAppearanceOptions options)
        {
            using var renderer = new BackdropRenderer();
            using Bitmap surface = ProbeSupport.NewSurface(64, 48);
            using Graphics g = Graphics.FromImage(surface);
            return ProbeSupport.MeasureAlloc(() => renderer.Render(g, options));
        }

        private static int CurrentThreadCount()
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                return process.Threads.Count;
            }
            catch
            {
                return -1;
            }
        }

        private static void Warmup()
        {
            try
            {
                var options = ProbeSupport.MakeOptions(new BackdropConfig { Mode = "gradient", Opacity = 0.5, Noise = 10, EdgeGlow = 10 });
                using var renderer = new BackdropRenderer();
                using Bitmap surface = ProbeSupport.NewSurface(32, 32);
                using Graphics g = Graphics.FromImage(surface);
                for (int i = 0; i < 3; i++) renderer.Render(g, options);
            }
            catch
            {
                // 预热失败不致命，用例里会给出真实异常
            }
        }

        private static string MainAssemblyHash()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "LiteMonitor++.dll");
                if (!File.Exists(path)) return "(未找到主程序集)";
                using var sha = System.Security.Cryptography.SHA256.Create();
                using FileStream stream = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(stream));
            }
            catch (Exception ex)
            {
                return "(计算失败：" + ex.Message + ")";
            }
        }
    }
}
