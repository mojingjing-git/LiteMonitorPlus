using System;
using System.Drawing;
using System.Drawing.Imaging;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 背景层**底图来源诊断**测试（A14 独立复核 N2）。
    ///
    /// ★ N2 的问题：编辑器预览用的是**固定样例图**（绝不是实时桌面捕获），
    ///   但背景层诊断把来源报成了 <c>LiveFrame</c> ——
    ///   这会让"实时玻璃真的在工作"这个验收结论失真（固定样本被计入实时帧）。
    ///
    /// ★ 两层保险（本次修复）：
    /// 1. <b>预览固定源走 <c>options.PreviewSource</c> 路径</b>：
    ///    编辑器**根本不注册帧后端**，因此"后端调用次数 = 0"是**结构性保证**
    ///    （而不是靠约定），且来源诊断是 <c>PreviewFrame</c>；
    /// 2. <b>帧提供者自报来源</b>：<see cref="IBackdropFrameSource.IsPreview"/>
    ///    由提供者自己声明 —— 真实捕获后端报 false（⇒ <c>LiveFrame</c>），
    ///    固定图 / 预览适配器报 true（⇒ <c>PreviewFrame</c>），
    ///    "把固定图当实时帧上报"的误报从根上不可能发生。
    /// </summary>
    public sealed class AppearanceBackdropSourceTests
    {
        // ================================================================
        // ① 预览固定源：来源必须是 PreviewFrame
        // ================================================================

        [Fact]
        public void 预览固定源走PreviewSource路径_底图来源报PreviewFrame()
        {
            Theme theme = NewGlassTheme();
            using Bitmap sample = NewSampleBitmap();

            using var runtime = new AppearanceRuntime(previewSource: sample);
            ThemeAppearanceOptions options = runtime.Update(theme, 1.0f, 1.0f, new Rectangle(0, 0, 64, 48));

            // ★ 固定样例图确实进入了快照（不是走后端取来的）
            Assert.NotNull(options.PreviewSource);

            using Bitmap canvas = new Bitmap(64, 48, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                runtime.Backdrop.Render(g, options);
            }

            BackdropStatus status = runtime.Backdrop.LastStatus;
            Assert.Equal(BackdropRenderMode.Glass, status.EffectiveMode);
            Assert.Equal(BackdropFrameSource.PreviewFrame, status.Source);
            Assert.NotEqual(BackdropFrameSource.LiveFrame, status.Source);
            Assert.False(status.IsDegraded);

            // 画面上确实画的是那张固定样例图
            Assert.Equal(Color.FromArgb(0x22, 0xCC, 0x88), canvas.GetPixel(32, 24));
        }

        // ================================================================
        // ② 预览固定源优先于已注册后端：后端一次都不被访问
        // ================================================================

        [Fact]
        public void 预览固定源优先于已注册后端_后端一次都不被访问()
        {
            // ★ 这是"结构性保证"的直接证据：即使同时注册了一个**就绪的**实时后端，
            //   预览路径也**不会碰它**（取帧次数 = 0），
            //   而且来源仍然报 PreviewFrame（不会被后端顶成 LiveFrame）。
            Theme theme = NewGlassTheme();
            using Bitmap sample = NewSampleBitmap();
            using Bitmap liveFrame = NewLiveFrameBitmap();

            var backend = new CountingFrameSource { IsReady = true, IsPreview = false, Frame = liveFrame };
            using var runtime = new AppearanceRuntime(backend, sample);
            ThemeAppearanceOptions options = runtime.Update(theme, 1.0f, 1.0f, new Rectangle(0, 0, 64, 48));

            using Bitmap canvas = new Bitmap(64, 48, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                runtime.Backdrop.Render(g, options);
            }

            Assert.Equal(0, backend.TryGetFrameCalls);
            Assert.Equal(BackdropFrameSource.PreviewFrame, runtime.Backdrop.LastStatus.Source);
        }

        // ================================================================
        // ③ 帧提供者自报来源：真实后端 ⇒ LiveFrame
        // ================================================================

        [Fact]
        public void 帧提供者自报是实时_底图来源报LiveFrame()
        {
            Theme theme = NewGlassTheme();
            using Bitmap liveFrame = NewLiveFrameBitmap();

            var backend = new CountingFrameSource { IsReady = true, IsPreview = false, Frame = liveFrame };
            using var runtime = new AppearanceRuntime(backend);
            ThemeAppearanceOptions options = runtime.Update(theme, 1.0f, 1.0f, new Rectangle(0, 0, 64, 48));

            using Bitmap canvas = new Bitmap(64, 48, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                runtime.Backdrop.Render(g, options);
            }

            BackdropStatus status = runtime.Backdrop.LastStatus;
            Assert.Equal(BackdropRenderMode.Glass, status.EffectiveMode);
            Assert.Equal(BackdropFrameSource.LiveFrame, status.Source);
            Assert.False(status.IsDegraded);
            Assert.Equal(1, backend.TryGetFrameCalls);
        }

        // ================================================================
        // ④ 帧提供者自报来源：固定图提供者 ⇒ PreviewFrame
        // ================================================================

        [Fact]
        public void 帧提供者自报是预览_底图来源报PreviewFrame()
        {
            // ★ 独立复核 N2 的根因防线：即便有人把固定图包成"帧来源"注入运行时，
            //   只要它如实自报 IsPreview = true，诊断就不会把它当成实时帧。
            Theme theme = NewGlassTheme();
            using Bitmap fixedFrame = NewSampleBitmap();

            var backend = new CountingFrameSource { IsReady = true, IsPreview = true, Frame = fixedFrame };
            using var runtime = new AppearanceRuntime(backend);
            ThemeAppearanceOptions options = runtime.Update(theme, 1.0f, 1.0f, new Rectangle(0, 0, 64, 48));

            using Bitmap canvas = new Bitmap(64, 48, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                runtime.Backdrop.Render(g, options);
            }

            BackdropStatus status = runtime.Backdrop.LastStatus;
            Assert.Equal(BackdropRenderMode.Glass, status.EffectiveMode);
            Assert.Equal(BackdropFrameSource.PreviewFrame, status.Source);
            Assert.NotEqual(BackdropFrameSource.LiveFrame, status.Source);
            Assert.False(status.IsDegraded);
        }

        // ================================================================
        // ⑤ 预览帧来源适配器（固定样例图包成帧来源）
        // ================================================================

        [Fact]
        public void 预览帧来源适配器_固定样例图报PreviewFrame而不是LiveFrame()
        {
            // ★ 这就是独立复核 N2 的**原始复现路径**：
            //   把固定 Bitmap 包成 PreviewFrameSource 注入运行时。
            //   修复前 runtime.Backdrop.LastStatus.Source == LiveFrame（误报）。
            Theme theme = NewGlassTheme();
            using Bitmap sample = NewSampleBitmap();

            using var runtime = new AppearanceRuntime(new PreviewFrameSource(sample));
            ThemeAppearanceOptions options = runtime.Update(theme, 1.0f, 1.0f, new Rectangle(0, 0, 64, 48));

            using Bitmap canvas = new Bitmap(64, 48, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                runtime.Backdrop.Render(g, options);
            }

            BackdropStatus status = runtime.Backdrop.LastStatus;
            Assert.Equal(BackdropFrameSource.PreviewFrame, status.Source);
            Assert.NotEqual(BackdropFrameSource.LiveFrame, status.Source);
        }

        // ================================================================
        // ⑥ 编辑器预览兼容重载：来源诊断也是 PreviewFrame
        // ================================================================

        [Fact]
        public void 编辑器预览兼容重载_底图来源报PreviewFrame()
        {
            // ★ 编辑器真正使用的那条路径（正式渲染器 + 固定样例图）
            //   必须把来源报成 PreviewFrame。
            Theme theme = NewGlassTheme();
            using Bitmap sample = NewSampleBitmap();

            Theme preview = ThemePreviewControl.CreatePreviewTheme(theme, 1.0f);
            var groups = ThemePreviewControl.CreateMockGroups();
            int height = new UILayout(preview).Build(groups);
            var bounds = new Rectangle(0, 0, preview.Layout.Width, Math.Max(1, height));

            using var backdrop = new BackdropRenderer();
            using Bitmap canvas = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                ThemePreviewControl.RenderPreview(g, groups, preview, bounds, backdrop, sample, 1.0f);
            }

            Assert.Equal(BackdropFrameSource.PreviewFrame, backdrop.LastStatus.Source);
            Assert.NotEqual(BackdropFrameSource.LiveFrame, backdrop.LastStatus.Source);
            Assert.Equal(BackdropRenderMode.Glass, backdrop.LastStatus.EffectiveMode);
        }

        // ================================================================
        // 辅助
        // ================================================================

        /// <summary>构造一个请求 glass 的主题（背景层会真的走玻璃路径）。</summary>
        private static Theme NewGlassTheme()
        {
            var theme = new Theme { Name = "GlassSource", Version = 4 };

            theme.Layout.FrameStyle = 1;
            theme.Layout.FrameWidth = 1;
            theme.Layout.FrameLength = 12;

            theme.Color.Border = "#00FFFF";
            theme.Color.Shadow = "#00FFFF";

            theme.Backdrop = new BackdropConfig
            {
                Mode = "glass",
                Opacity = 0,          // 不产生染色层，画面只反映底图本身
                Blur = 8,
                Tint = "#203040",
                GradientStart = "#101010",
                GradientEnd = "#303030",
                GradientAngle = 45
            };

            return theme;
        }

        /// <summary>固定样例图（预览用）：整幅 0x22CC88。</summary>
        private static Bitmap NewSampleBitmap()
        {
            var bmp = new Bitmap(64, 48, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                using var brush = new SolidBrush(Color.FromArgb(0x22, 0xCC, 0x88));
                g.FillRectangle(brush, new Rectangle(0, 0, 64, 48));
            }
            return bmp;
        }

        /// <summary>"实时帧"（对照组）：整幅 0x3366FF。</summary>
        private static Bitmap NewLiveFrameBitmap()
        {
            var bmp = new Bitmap(64, 48, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                using var brush = new SolidBrush(Color.FromArgb(0x33, 0x66, 0xFF));
                g.FillRectangle(brush, new Rectangle(0, 0, 64, 48));
            }
            return bmp;
        }

        /// <summary>
        /// 会计数的假帧来源（不含任何抓屏调用，只是记账）。
        /// </summary>
        private sealed class CountingFrameSource : IBackdropFrameSource
        {
            public bool IsReady { get; set; } = true;
            public string UnavailableReason => "假帧来源不可用";
            public bool IsPreview { get; set; }
            public Image Frame { get; set; }
            public int TryGetFrameCalls { get; private set; }

            public bool TryGetFrame(double blurPx, out Image frame)
            {
                TryGetFrameCalls++;
                frame = Frame;
                return Frame != null;
            }
        }
    }
}
