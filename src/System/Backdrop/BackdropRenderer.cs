using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 背景层渲染器（静态背景 + 降级决策）
    // ================================================================

    /// <summary>
    /// 背景层渲染器：按 v4 §3.1 / §3.3 绘制背景最底层。
    ///
    /// ★ 合同要点（接线方务必遵守）：
    /// 1. **层次**：本组件只画背景层（底板 → 渐变/帧 → tint → 噪点 → 边缘辉光），
    ///    必须在标题/指标/组块之前调用；本组件不画任何文字。
    /// 2. **坐标**：一律使用 <c>options.ClientBounds</c>（完整客户区），
    ///    不使用 <c>Graphics.VisibleClipBounds</c>，也不使用旧的 (-5,-5) 背景填充矩形定位。
    /// 3. **幂等**：每次绘制都从一张**不透明底板**开始，
    ///    因此同一 options 连续绘制 N 次的像素结果完全一致（不会累积变色）。
    /// 4. **solid 是零副作用路径**：模式为 solid、模式未知、或整个 Backdrop 缺失时，
    ///    本组件**不绘制、不分配位图、不创建画刷**，旧背景路径原样保留。
    /// 5. **降级不静默**：glass 拿不到底图时降级到 gradient（再退 solid），
    ///    并通过 <see cref="LastStatus"/> / 返回值给出原因码与可读原因。
    /// 6. **不释放借用对象**：<c>options.BackgroundFrame</c> / <c>options.PreviewSource</c>
    ///    只是借用引用，本组件既不释放也不修改。
    /// 7. **不含任何屏幕捕获**：本文件不出现 BitBlt/PrintWindow/DXGI 等抓屏 API，
    ///    也不创建计时器或后台任务；预览路径使用固定源，绝不启动捕获。
    /// </summary>
    public sealed class BackdropRenderer : IDisposable
    {
        private readonly IBackdropFrameSource? _frameSource;
        private readonly BackdropModeResolver _resolver = new();
        private readonly BackdropNoiseCache _noise = new();
        private bool _disposed;

        /// <summary>
        /// 构造背景渲染器。
        /// </summary>
        /// <param name="frameSource">
        /// 实时背景帧来源（可空）。为 null 时，glass 模式一律降级并给出
        /// "未注册实时背景后端"的原因。预览场景可以不传。
        /// </param>
        public BackdropRenderer(IBackdropFrameSource? frameSource = null)
        {
            _frameSource = frameSource;
        }

        /// <summary>最近一次渲染的状态（UI 与日志可直接读取）。</summary>
        public BackdropStatus LastStatus { get; private set; }

        /// <summary>当前保留的噪点纹理块数量（诊断用）。</summary>
        public int NoiseCacheEntryCount => _noise.EntryCount;

        /// <summary>当前噪点纹理占用的位图字节数（诊断用）。</summary>
        public long NoiseCacheBytes => _noise.TotalBytes;

        /// <summary>
        /// 绘制背景层。
        /// </summary>
        /// <param name="g">目标画布（通常是窗体客户区的 Graphics）。</param>
        /// <param name="options">外观快照（只读）。</param>
        /// <returns>本次渲染的状态（含降级原因）。</returns>
        public BackdropStatus Render(Graphics g, ThemeAppearanceOptions options)
        {
            if (g is null) throw new ArgumentNullException(nameof(g));
            if (options is null) throw new ArgumentNullException(nameof(options));
            if (_disposed) throw new ObjectDisposedException(nameof(BackdropRenderer));

            // 1) 客户区没有有效宽高 → 跳过全部绘制（不抛异常）
            if (!options.HasValidBounds)
            {
                BackdropStatus skipped = new BackdropStatus(
                    options.BackdropMode ?? ThemeAppearanceModeNames.Solid,
                    BackdropRenderMode.Solid,
                    false,
                    BackdropDegradeReason.InvalidBounds,
                    "客户区没有有效宽高，已跳过背景层绘制。",
                    BackdropFrameSource.None,
                    BackdropLayer.None,
                    options.BlurPx);
                LastStatus = skipped;
                return skipped;
            }

            // 2) 采集底图：预览固定源优先（预览不启动捕获），其次才是实时后端
            Image? frame = null;
            BackdropFrameOffer offer = BackdropFrameOffer.Available(isPreview: false);

            if (options.PreviewSource != null)
            {
                // 预览路径：固定样例图直接作为底图，绝不触碰实时后端
                frame = options.PreviewSource;
                offer = BackdropFrameOffer.Available(isPreview: true);
            }
            else if (options.GlassRequested)
            {
                if (_frameSource is null)
                {
                    offer = BackdropFrameOffer.Missing(BackdropDegradeReason.BackendMissing, "未注册实时背景后端");
                }
                else if (!_frameSource.IsReady)
                {
                    offer = BackdropFrameOffer.Missing(BackdropDegradeReason.BackendNotReady, _frameSource.UnavailableReason);
                }
                else if (!_frameSource.TryGetFrame(options.BlurPx, out frame) || frame is null || frame.Width <= 0 || frame.Height <= 0)
                {
                    offer = BackdropFrameOffer.Missing(BackdropDegradeReason.BackendFrameInvalid, _frameSource.UnavailableReason);
                    frame = null;
                }
                else
                {
                    // ★ 来源由**帧提供者自己声明**（IBackdropFrameSource.IsPreview）：
                    //   真实捕获后端报 LiveFrame，固定样例图 / 假后端报 PreviewFrame。
                    //   这样"把固定图当实时帧上报"的误报从根上不可能发生（独立复核 N2）。
                    offer = BackdropFrameOffer.Available(isPreview: _frameSource.IsPreview);
                }
            }

            // 3) 解析有效模式（可能降级）
            BackdropStatus status = _resolver.Resolve(options, offer);

            // 4) 绘制（solid 路径完全跳过：本组件零副作用、零分配）
            if (status.EffectiveMode != BackdropRenderMode.Solid)
            {
                GraphicsState state = g.Save();
                try
                {
                    if (NeedsBasePlate(status.EffectiveMode, options)) DrawBasePlate(g, options);

                    if (status.EffectiveMode == BackdropRenderMode.Gradient)
                    {
                        DrawGradient(g, options);
                    }
                    else if (frame != null)
                    {
                        DrawFrame(g, options, frame);
                    }

                    if ((status.Layers & BackdropLayer.Tint) != 0) DrawTint(g, options);
                    if ((status.Layers & BackdropLayer.Noise) != 0) DrawNoise(g, options);
                    if ((status.Layers & BackdropLayer.EdgeGlow) != 0) DrawEdgeGlow(g, options);
                }
                finally
                {
                    g.Restore(state);
                }
            }

            LastStatus = status;
            return status;
        }

        /// <summary>
        /// 失效入口：释放噪点纹理与画刷。
        /// 主题代次、尺寸、DPI 变化或窗口退出时调用；只能在两次绘制之间调用。
        /// </summary>
        public void Invalidate()
        {
            _noise.Invalidate();
        }

        /// <summary>释放本组件持有的资源（不碰借用帧）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _noise.Dispose();
            _disposed = true;
        }

        // ------------------------------------------------------------
        // 各层绘制
        // ------------------------------------------------------------

        // 不透明底板：保证每次绘制都从确定底板开始，重复绘制不会累积变色
        // ★ 用"外扩矩形"填充：抗锯齿下严格按客户区填充会让最外一圈只被覆盖一半（漏色）。
        private static void DrawBasePlate(Graphics g, ThemeAppearanceOptions options)
        {
            Color bg = options.BackgroundColor;
            Color plate = Color.FromArgb(255, bg.R, bg.G, bg.B);
            using var brush = new SolidBrush(plate);
            g.FillRectangle(brush, ThemeAppearanceOptions.GetEdgeCoveredBounds(options.ClientBounds));
        }

        /// <summary>
        /// 是否需要铺那张**不透明底板**。
        /// <para>
        /// ★ 渐变模式：若渐变两端色都**完全不透明**，<see cref="DrawGradient"/> 会把
        ///   整块客户区盖满 ⇒ 底板是纯浪费的一次全屏不透明填充
        ///   （A14 实测：4 卡两倍档约 2.2 ms/帧）。
        ///   此时跳过底板，**画面像素完全不变**（底板本来就被完全覆盖）。
        /// <para>
        /// ★ 玻璃 / 其它模式：帧是半透明的，必须有底板来兜住 ⇒ 一律铺。
        /// <para>
        /// ★ 幂等性仍然成立：不透明渐变每次都会盖满整块客户区，
        ///   连续绘制 N 次的结果与第 1 次完全一致。
        /// </para>
        /// </summary>
        private static bool NeedsBasePlate(BackdropRenderMode mode, ThemeAppearanceOptions options)
        {
            if (mode != BackdropRenderMode.Gradient) return true;
            return options.GradientStartColor.A < 255 || options.GradientEndColor.A < 255;
        }

        // 静态渐变：按 GradientAngle 的语义自行求轴，支持任意角度
        private static void DrawGradient(Graphics g, ThemeAppearanceOptions options)
        {
            // ★ 渐变轴仍按**精确客户区**计算 —— 渐变的颜色分布（两端色落在哪里）
            //   不能因为抗锯齿补偿而改变，否则画面观感会漂。
            using LinearGradientBrush? brush = BackdropGradient.CreateBrush(
                options.ClientBounds,
                options.GradientAngle,
                options.GradientStartColor,
                options.GradientEndColor);
            if (brush is null) return;

            // ★ 填充范围外扩：抗锯齿下严格按客户区填充，客户区最外一圈只被覆盖一半（漏色）。
            //   溢出部分会被 Graphics 裁剪丢弃；超出渐变轴的部分由 GDI+ 自然钳到端点色。
            g.FillRectangle(brush, ThemeAppearanceOptions.GetEdgeCoveredBounds(options.ClientBounds));
        }

        // 实时帧 / 预览固定源：铺满完整客户区（帧是借用的，不释放）
        private static void DrawFrame(Graphics g, ThemeAppearanceOptions options, Image frame)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(frame, options.ClientBounds);
        }

        // 背景染色层：alpha = Tint 自身 alpha × Opacity，只作用于背景层
        private static void DrawTint(Graphics g, ThemeAppearanceOptions options)
        {
            int alpha = BackdropModeResolver.ComputeTintAlpha(options);
            if (alpha <= 0) return;
            Color tint = options.TintColor;
            using var brush = new SolidBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
            // ★ 同样外扩：染色层必须覆盖整块客户区，否则边缘会留一圈没上色的带。
            g.FillRectangle(brush, ThemeAppearanceOptions.GetEdgeCoveredBounds(options.ClientBounds));
        }

        // 固定种子的噪点（强度 0 时这里根本不会被调用）
        private void DrawNoise(Graphics g, ThemeAppearanceOptions options)
        {
            TextureBrush? brush = _noise.GetBrush(options.BackdropNoise);
            if (brush is null) return;
            g.FillRectangle(brush, ThemeAppearanceOptions.GetEdgeCoveredBounds(options.ClientBounds));
        }

        // 边缘辉光（颜色取背景染色色）
        private void DrawEdgeGlow(Graphics g, ThemeAppearanceOptions options)
        {
            BackdropEdgeGlow.Draw(g, options.ClientBounds, options.TintColor, options.BackdropEdgeGlow, options.EffectiveScale);
        }
    }
}
