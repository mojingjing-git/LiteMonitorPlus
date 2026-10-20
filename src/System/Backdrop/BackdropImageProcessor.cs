using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 背景帧图像处理（降采样 + 物理模糊半径滤波）
    // ================================================================

    /// <summary>
    /// 模糊半径合同（A06 §5.1；v4 §4 第 4 条）。
    ///
    /// ★ 合同定义（逐条对应 A06 §5.1）：
    /// 1. <c>Blur</c> 的单位是 **96 DPI 逻辑像素**，表示"最终显示尺寸下"的等效高斯标准差 σ。
    /// 2. 物理标准差 σ_physical = 逻辑值 × (窗口 DPI/96) × 用户缩放。
    ///    —— 生产链路上这个值就是 <c>ThemeAppearanceOptions.BlurPx</c>（已缩放一次），本结构不再乘缩放。
    /// 3. 捕获可以按 captureScale 降采样；降采样后滤波在降采样缓冲上进行，
    ///    其标准差 σ_down = σ_physical × captureScale。
    /// 4. 实际滤波 = **3 次可分离 box blur**（半径 r 的 box 方差为 r(r+1)/3，3 次卷积后 σ² = r(r+1)），
    ///    因此 r = round((√(1+4σ_down²) − 1) / 2)，实际等效 σ_down_eff = √(r(r+1))。
    /// 5. <c>Blur = 0</c> → σ = 0 → **不做滤波**（不是"半径 0 的 box"）。
    ///
    /// ★ 明确排除：单纯缩小再放大只是**缩放近似**，不算模糊
    ///   （A06 §5.2 对照组：标称 Blur=12 与 24 的上升沿恒为 4 px，对 Blur 参数完全不响应）。
    ///   因此本结构只有在真正施加了 box blur（r ≥ 1）时才做降采样/升采样；
    ///   r = 0 时默认**既不滤波也不重采样**，保证"Blur = 0 ⇒ 画面零软化"。
    ///   需要复刻 A06 探针"总是重采样"的行为时，把 <see cref="ResampleWithoutBlur"/> 置 true。
    /// </summary>
    public readonly struct BackdropBlurContract
    {
        private BackdropBlurContract(double sigmaPhysicalPx, double captureScale, bool resampleWithoutBlur)
        {
            SigmaPhysicalPx = sigmaPhysicalPx > 0 && !double.IsNaN(sigmaPhysicalPx) ? sigmaPhysicalPx : 0.0;
            CaptureScale = captureScale > 0 && captureScale <= 1 && !double.IsNaN(captureScale) ? captureScale : 1.0;
            SigmaDownsampledPx = SigmaPhysicalPx * CaptureScale;
            BoxRadius = ComputeBoxRadius(SigmaDownsampledPx);
            SigmaEffectiveDownsampledPx = BoxRadius > 0 ? Math.Sqrt((double)BoxRadius * (BoxRadius + 1)) : 0.0;
            SigmaEffectiveDisplayPx = CaptureScale > 0 ? SigmaEffectiveDownsampledPx / CaptureScale : 0.0;
            ResampleWithoutBlur = resampleWithoutBlur;
        }

        /// <summary>物理标准差 σ_physical（物理像素；= 96 DPI 逻辑值 × DPI 比例 × 用户缩放）。</summary>
        public double SigmaPhysicalPx { get; }

        /// <summary>捕获降采样比例（0 &lt; s ≤ 1；A06 生产建议 0.25）。</summary>
        public double CaptureScale { get; }

        /// <summary>降采样缓冲上的目标标准差 σ_down = σ_physical × captureScale（降采样缓冲像素）。</summary>
        public double SigmaDownsampledPx { get; }

        /// <summary>3×box blur 的半径 r（0 表示不施加任何模糊）。</summary>
        public int BoxRadius { get; }

        /// <summary>实际等效标准差（降采样缓冲像素）= √(r(r+1))。</summary>
        public double SigmaEffectiveDownsampledPx { get; }

        /// <summary>折算回显示尺寸的等效标准差（物理像素）= σ_eff_down / captureScale。</summary>
        public double SigmaEffectiveDisplayPx { get; }

        /// <summary>
        /// r = 0（σ = 0）时是否仍然做降采样/升采样。
        /// <para>默认 false：σ = 0 ⇒ 画面零软化（严格满足"Blur = 0 不做滤波"）。</para>
        /// <para>true 时行为与 A06 探针一致（总是重采样），会留下约 4 px 的插值残留边。</para>
        /// </summary>
        public bool ResampleWithoutBlur { get; }

        /// <summary>是否需要真正施加模糊（r ≥ 1）。</summary>
        public bool RequiresBlur => BoxRadius > 0;

        /// <summary>是否需要降采样/升采样。</summary>
        public bool RequiresResample => BoxRadius > 0 || ResampleWithoutBlur;

        /// <summary>
        /// 用**物理标准差**与降采样比例构造。
        /// </summary>
        /// <param name="sigmaPhysicalPx">σ_physical（物理像素）。生产链路直接传 <c>options.BlurPx</c>。</param>
        /// <param name="captureScale">捕获降采样比例（0 &lt; s ≤ 1）。</param>
        /// <param name="resampleWithoutBlur">σ = 0 时是否仍然重采样（默认 false）。</param>
        public static BackdropBlurContract Create(double sigmaPhysicalPx, double captureScale, bool resampleWithoutBlur = false)
        {
            return new BackdropBlurContract(sigmaPhysicalPx, captureScale, resampleWithoutBlur);
        }

        /// <summary>
        /// 用 96 DPI 逻辑模糊值 + DPI 比例 + 用户缩放构造。
        /// 换算与 <c>ThemeAppearanceOptions.BlurPx</c> 完全一致（逻辑值 × DPI 比例 × 用户缩放）。
        /// </summary>
        /// <param name="blurLogicalPx">96 DPI 逻辑模糊值（0–32）。</param>
        /// <param name="dpiScale">DPI 比例（DeviceDpi / 96）。</param>
        /// <param name="userScale">用户缩放倍率。</param>
        /// <param name="captureScale">捕获降采样比例。</param>
        /// <param name="resampleWithoutBlur">σ = 0 时是否仍然重采样（默认 false）。</param>
        public static BackdropBlurContract FromLogicalBlur(
            double blurLogicalPx,
            double dpiScale,
            double userScale,
            double captureScale,
            bool resampleWithoutBlur = false)
        {
            double effective = (dpiScale > 0 ? dpiScale : 1.0) * (userScale > 0 ? userScale : 1.0);
            return new BackdropBlurContract(blurLogicalPx * effective, captureScale, resampleWithoutBlur);
        }

        /// <summary>
        /// 3×box blur 的等效半径 r = round((√(1+4σ²) − 1) / 2)。
        /// <para>推导：半径 r 的 box 方差为 r(r+1)/3，3 次卷积后 σ² = 3 · r(r+1)/3 = r(r+1)，
        /// 反解 r 即得上式；取最近的整数 r 即得"等效 σ 最接近目标 σ"的半径。</para>
        /// <para>★ 与 A06 探针的唯一差别：这里用 <see cref="MidpointRounding.AwayFromZero"/>（.5 远离零），
        /// A06 用的是默认的"就近取偶"。在合同表的取值（σ = 3 / 6 / 12 / 24）上两者结果完全相同
        /// （r = 3 / 6 / 12 / 24），远离零只是避免 .5 处的取偶歧义。</para>
        /// </summary>
        private static int ComputeBoxRadius(double sigmaDown)
        {
            if (sigmaDown <= 0.0001) return 0;
            double r = (Math.Sqrt(1.0 + 4.0 * sigmaDown * sigmaDown) - 1.0) / 2.0;
            int ri = (int)Math.Round(r, MidpointRounding.AwayFromZero);
            return ri < 0 ? 0 : ri;
        }

        /// <summary>生成一行可写日志的合同描述（会分配字符串，仅在显式调用时产生）。</summary>
        public string Describe()
        {
            return $"σ_physical={SigmaPhysicalPx:0.##}px → σ_down={SigmaDownsampledPx:0.##}px"
                 + $"(captureScale={CaptureScale:0.##}) → 3×box r={BoxRadius}"
                 + $" → σ_eff_down={SigmaEffectiveDownsampledPx:0.##}px"
                 + $" → σ_eff_display={SigmaEffectiveDisplayPx:0.##}px";
        }
    }

    /// <summary>
    /// 一次图像处理的分段计量（合同 §6）。
    ///
    /// ★ 两种口径严格分开，绝不混用（合同 §6.1 / §6.4）：
    /// 1. <see cref="FilterElapsedMs"/> = **经过时间**（墙钟，含等待与抢占）→ 用于延迟判据。
    /// 2. <see cref="FilterCpuMs"/> = **CPU 时间**（处理器时间）→ 用于 CPU 预算。
    /// <para>★ 注意：Windows 进程 CPU 时钟粒度约 15.6 ms，单次操作无法直接测。
    /// 单帧的 <see cref="FilterCpuMs"/> 只能当**指示性**数值看；
    /// 要判定 CPU 预算必须**批量执行**（≥2000 次/批）后取每操作均值（合同 §6.1）。</para>
    /// </summary>
    public readonly struct BackdropProcessMetrics
    {
        /// <summary>构造一次处理计量。</summary>
        public BackdropProcessMetrics(
            bool success,
            string? failureReason,
            double filterElapsedMs,
            double filterCpuMs,
            int sourceWidth,
            int sourceHeight,
            int outputWidth,
            int outputHeight,
            int boxRadius,
            double sigmaEffectiveDisplayPx,
            bool appliedBlur,
            bool appliedResample,
            long peakTempBytes,
            int peakTempPixels)
        {
            Success = success;
            FailureReason = failureReason;
            FilterElapsedMs = filterElapsedMs;
            FilterCpuMs = filterCpuMs;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;
            BoxRadius = boxRadius;
            SigmaEffectiveDisplayPx = sigmaEffectiveDisplayPx;
            AppliedBlur = appliedBlur;
            AppliedResample = appliedResample;
            PeakTempBytes = peakTempBytes;
            PeakTempPixels = peakTempPixels;
        }

        /// <summary>处理是否成功。</summary>
        public bool Success { get; }

        /// <summary>失败原因（人类可读）；成功时为 null。</summary>
        public string? FailureReason { get; }

        /// <summary>滤波（降采样 + box blur）的**经过时间**（毫秒）。</summary>
        public double FilterElapsedMs { get; }

        /// <summary>滤波（降采样 + box blur）的**CPU 时间**（毫秒，进程处理器时间差值）。</summary>
        public double FilterCpuMs { get; }

        /// <summary>源图宽度（像素）。</summary>
        public int SourceWidth { get; }

        /// <summary>源图高度（像素）。</summary>
        public int SourceHeight { get; }

        /// <summary>输出图宽度（像素）。</summary>
        public int OutputWidth { get; }

        /// <summary>输出图高度（像素）。</summary>
        public int OutputHeight { get; }

        /// <summary>实际使用的 box 半径 r。</summary>
        public int BoxRadius { get; }

        /// <summary>折算回显示尺寸的等效标准差（物理像素）。</summary>
        public double SigmaEffectiveDisplayPx { get; }

        /// <summary>是否真的施加了 box 模糊。</summary>
        public bool AppliedBlur { get; }

        /// <summary>是否做了降采样/升采样。</summary>
        public bool AppliedResample { get; }

        /// <summary>本次处理的临时位图峰值字节数（不含源图与输出图）。</summary>
        public long PeakTempBytes { get; }

        /// <summary>本次处理的临时位图像素峰值。</summary>
        public int PeakTempPixels { get; }
    }

    /// <summary>
    /// 受预算约束的背景帧图像处理（降采样 + 3× 可分离 box blur）。
    ///
    /// ★ 合同要点：
    /// 1. **所有权**：本类**不拥有**传入的源图，也**绝不**释放它；
    ///    返回值是一张**新位图**，所有权归调用方（生产者侧），由调用方负责释放。
    /// 2. **半径合同**：按 <see cref="BackdropBlurContract"/> 执行（A06 §5.1），
    ///    不是"缩小再放大"的缩放近似。
    /// 3. **有界**：临时位图（降采样副本 + 模糊结果 + 行前缀和缓冲）只在本次调用内存在，
    ///    调用结束**必然释放**；峰值字节数由 <see cref="BackdropProcessMetrics"/> 记录，
    ///    当前在用字节数由 <see cref="CurrentTempBytes"/> 暴露。
    ///    ⇒ 任何时刻的临时位图总量有界，**不随帧率线性增长**。
    /// 4. **预算前置**：源图像素数超过 <see cref="MaxBufferPixels"/> 时**直接失败**（返回 null），
    ///    不做"先分配再判断"。
    /// </summary>
    public static class BackdropImageProcessor
    {
        /// <summary>默认捕获降采样比例（A06 §8.2 生产建议 0.25）。</summary>
        public const double DefaultCaptureScale = 0.25;

        /// <summary>
        /// 采集/处理缓冲的像素上限（A06 §7：最大档位 480×3152 = 1,512,960 px &lt; 2,000,000 px）。
        /// </summary>
        public const int MaxBufferPixels = 2_000_000;

        /// <summary>缓冲字节上限（32 MiB，A06 §7）。</summary>
        public const long MaxBufferBytes = 32L * 1024 * 1024;

        // 当前在用的临时位图字节数（诊断用；Interlocked 保证线程安全）
        private static long _currentTempBytes;
        private static long _peakTempBytes;

        /// <summary>当前在用的临时位图字节数（跨线程累加，诊断用）。</summary>
        public static long CurrentTempBytes => Interlocked.Read(ref _currentTempBytes);

        /// <summary>历史峰值临时位图字节数（诊断用）。</summary>
        public static long PeakTempBytes => Interlocked.Read(ref _peakTempBytes);

        /// <summary>
        /// 处理一帧：降采样（按 captureScale）→ 3× 可分离 box blur → 升采样回源尺寸。
        /// </summary>
        /// <param name="source">源图（**借用**：本方法不释放它）。</param>
        /// <param name="contract">模糊半径合同。</param>
        /// <param name="metrics">分段计量（经过时间 / CPU 时间分列）。</param>
        /// <returns>
        /// 处理后的新位图（**调用方拥有**，必须释放）；失败时返回 null，原因见 <paramref name="metrics"/>。
        /// </returns>
        public static Bitmap? Process(Image source, in BackdropBlurContract contract, out BackdropProcessMetrics metrics)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            int sw = source.Width;
            int sh = source.Height;
            long srcPixels = (long)sw * sh;

            if (sw <= 0 || sh <= 0)
            {
                metrics = new BackdropProcessMetrics(
                    false, $"源图尺寸无效（{sw}×{sh}）。",
                    0, 0, sw, sh, 0, 0, contract.BoxRadius, contract.SigmaEffectiveDisplayPx,
                    false, false, 0, 0);
                return null;
            }

            // ★ 预算前置检查：超预算直接失败，绝不先分配再判断
            if (srcPixels > MaxBufferPixels || srcPixels * 4 > MaxBufferBytes)
            {
                metrics = new BackdropProcessMetrics(
                    false, $"捕获尺寸 {sw}×{sh}（{srcPixels} 像素 / {srcPixels * 4} 字节）超出缓冲预算" +
                           $"（{MaxBufferPixels} 像素 / {MaxBufferBytes} 字节）。",
                    0, 0, sw, sh, 0, 0, contract.BoxRadius, contract.SigmaEffectiveDisplayPx,
                    false, false, 0, 0);
                return null;
            }

            double cpu0 = CurrentCpuMs();
            long t0 = Stopwatch.GetTimestamp();

            Bitmap? small = null;      // 降采样缓冲（32bpp）
            Bitmap? blurred = null;     // 模糊结果（32bpp）
            long tempBytes = 0;
            long peakTemp = 0;

            try
            {
                Bitmap result;

                if (!contract.RequiresResample)
                {
                    // ★ σ = 0：既不滤波也不重采样，只做一次等尺寸格式转换
                    //   （输出统一为 32bpp，且所有权明确交给调用方）。
                    result = CreateFlatCopy(source);
                    peakTemp = 0;
                }
                else
                {
                    int dw = Math.Max(1, (int)Math.Round(sw * contract.CaptureScale));
                    int dh = Math.Max(1, (int)Math.Round(sh * contract.CaptureScale));

                    small = Downscale(source, dw, dh);
                    tempBytes += (long)dw * dh * 4;
                    peakTemp = Math.Max(peakTemp, tempBytes);
                    AddTempBytes((long)dw * dh * 4);

                    if (contract.RequiresBlur)
                    {
                        blurred = ApplyBoxBlur3x(small, contract.BoxRadius);
                        tempBytes += (long)dw * dh * 4;
                        peakTemp = Math.Max(peakTemp, tempBytes);
                        AddTempBytes((long)dw * dh * 4);
                    }
                    else
                    {
                        // σ = 0 但要求重采样（复刻 A06 探针行为）：缩放近似**不算模糊**
                        blurred = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(blurred))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.DrawImage(small, new Rectangle(0, 0, dw, dh));
                        }
                        tempBytes += (long)dw * dh * 4;
                        peakTemp = Math.Max(peakTemp, tempBytes);
                        AddTempBytes((long)dw * dh * 4);
                    }

                    // 升采样回源尺寸：模糊半径是"最终显示尺寸"下的口径，
                    // 因此成品必须回到显示尺寸，否则等效 σ 会缩小 captureScale 倍。
                    result = Upscale(blurred, sw, sh);
                }

                double elapsed = ElapsedMsSince(t0);
                double cpu = CurrentCpuMs() - cpu0;

                metrics = new BackdropProcessMetrics(
                    true, null,
                    elapsed, cpu,
                    sw, sh, result.Width, result.Height,
                    contract.BoxRadius, contract.SigmaEffectiveDisplayPx,
                    contract.RequiresBlur, contract.RequiresResample,
                    peakTemp, PeakTempPixels(sw, sh, contract.CaptureScale));

                return result;
            }
            catch (Exception ex)
            {
                double elapsed = ElapsedMsSince(t0);
                double cpu = CurrentCpuMs() - cpu0;
                metrics = new BackdropProcessMetrics(
                    false, "图像处理失败：" + ex.Message,
                    elapsed, cpu, sw, sh, 0, 0,
                    contract.BoxRadius, contract.SigmaEffectiveDisplayPx,
                    false, false, peakTemp, 0);
                return null;
            }
            finally
            {
                // ★ 临时位图确定释放：无论成功、失败还是异常，都不残留
                small?.Dispose();
                blurred?.Dispose();
                AddTempBytes(-tempBytes);
            }
        }

        /// <summary>
        /// 3 次可分离 box blur（半径 r），预乘 alpha 安全。
        /// <para>★ 算法与 A06 探针 <c>BlurPipeline.ApplyBoxBlur3x</c> 一致：
        /// 前缀和（O(1)/像素）做水平与垂直滑动窗口，逐像素取窗口平均。</para>
        /// </summary>
        /// <param name="source">源图（**借用**：本方法不释放它）。</param>
        /// <param name="radius">box 半径 r（≤0 表示不施加模糊）。</param>
        /// <returns>新位图（**调用方拥有**，必须释放）。</returns>
        public static Bitmap ApplyBoxBlur3x(Bitmap source, int radius)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            int w = source.Width;
            int h = source.Height;
            if (w <= 0 || h <= 0) throw new ArgumentException("源图尺寸无效。", nameof(source));

            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                return BlurCore(source, result, radius);
            }
            catch
            {
                // 失败路径必须释放已创建但未交付的位图（合同 §4.2 第 6 条）
                result.Dispose();
                throw;
            }
        }

        // 模糊主体（目标位图由调用方创建并负责释放）
        private static Bitmap BlurCore(Bitmap source, Bitmap result, int radius)
        {
            int w = source.Width;
            int h = source.Height;
            var rect = new Rectangle(0, 0, w, h);

            if (radius <= 0)
            {
                // 半径 0 = 不施加任何模糊（合同：Blur = 0 ⇒ 不做滤波）
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(source, new Rectangle(0, 0, w, h));
                }
                return result;
            }

            var data = new int[w * h];
            BitmapData bd = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, data, 0, data.Length);
            }
            finally { source.UnlockBits(bd); }

            // 预乘 alpha（半透明像素的 box 平均必须在预乘空间里做，否则边缘会发黑）
            for (int i = 0; i < data.Length; i++)
            {
                int c = data[i];
                int a = (c >> 24) & 0xFF;
                if (a == 255) continue;
                int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                r = (r * a + 127) / 255;
                g = (g * a + 127) / 255;
                b = (b * a + 127) / 255;
                data[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }

            var tmp = new int[data.Length];
            var outBuf = new int[data.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlurH(data, tmp, w, h, radius);
                BoxBlurV(tmp, outBuf, w, h, radius);
                Array.Copy(outBuf, data, data.Length);
            }

            // 反预乘并写回
            BitmapData rb = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int i = 0; i < data.Length; i++)
                {
                    int c = data[i];
                    int a = (c >> 24) & 0xFF;
                    int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                    if (a != 255 && a != 0)
                    {
                        r = Math.Min(255, r * 255 / a);
                        g = Math.Min(255, g * 255 / a);
                        b = Math.Min(255, b * 255 / a);
                    }
                    data[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }
                System.Runtime.InteropServices.Marshal.Copy(data, 0, rb.Scan0, data.Length);
            }
            finally { result.UnlockBits(rb); }

            return result;
        }

        /// <summary>
        /// 降采样（面积平均优先：GDI+ 的双三次在大幅缩小时仍会漏掉高频信息，
        /// 这里沿用 A06 探针的 HighQualityBicubic 以保持与实测数据同源）。
        /// </summary>
        /// <param name="source">源图（**借用**：本方法不释放它）。</param>
        /// <param name="width">目标宽（像素，至少 1）。</param>
        /// <param name="height">目标高（像素，至少 1）。</param>
        /// <returns>新位图（**调用方拥有**，必须释放）。</returns>
        public static Bitmap Downscale(Image source, int width, int height)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    // ★ SourceCopy：直接把源的 alpha 一并写入，避免与透明底色混合
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle(0, 0, w, h));
                }
            }
            catch
            {
                dst.Dispose();
                throw;
            }
            return dst;
        }

        /// <summary>
        /// 升采样回最终显示尺寸。
        /// </summary>
        /// <param name="source">源图（**借用**：本方法不释放它）。</param>
        /// <param name="width">目标宽（像素，至少 1）。</param>
        /// <param name="height">目标高（像素，至少 1）。</param>
        /// <returns>新位图（**调用方拥有**，必须释放）。</returns>
        public static Bitmap Upscale(Image source, int width, int height)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle(0, 0, w, h));
                }
            }
            catch
            {
                dst.Dispose();
                throw;
            }
            return dst;
        }

        // 等尺寸副本（σ = 0 路径）：统一输出格式为 32bpp，并明确所有权归属
        private static Bitmap CreateFlatCopy(Image source)
        {
            int w = source.Width;
            int h = source.Height;
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(source, new Rectangle(0, 0, w, h));
                }
            }
            catch
            {
                dst.Dispose();
                throw;
            }
            return dst;
        }

        // 水平滑动窗口（前缀和，O(1)/像素）
        private static void BoxBlurH(int[] src, int[] dst, int w, int h, int r)
        {
            var prefix = new long[(w + 1) * 4];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                long a = 0, rr = 0, g = 0, b = 0;
                prefix[0] = 0; prefix[1] = 0; prefix[2] = 0; prefix[3] = 0;
                for (int x = 0; x < w; x++)
                {
                    int c = src[row + x];
                    a += (c >> 24) & 0xFF;
                    rr += (c >> 16) & 0xFF;
                    g += (c >> 8) & 0xFF;
                    b += c & 0xFF;
                    int o = (x + 1) * 4;
                    prefix[o] = a; prefix[o + 1] = rr; prefix[o + 2] = g; prefix[o + 3] = b;
                }
                for (int x = 0; x < w; x++)
                {
                    int left = Math.Max(0, x - r);
                    int right = Math.Min(w - 1, x + r);
                    int o0 = left * 4, o1 = (right + 1) * 4;
                    int cnt = right - left + 1;
                    int oa = (int)((prefix[o1 + 0] - prefix[o0 + 0]) / cnt);
                    int orr = (int)((prefix[o1 + 1] - prefix[o0 + 1]) / cnt);
                    int og = (int)((prefix[o1 + 2] - prefix[o0 + 2]) / cnt);
                    int ob = (int)((prefix[o1 + 3] - prefix[o0 + 3]) / cnt);
                    dst[row + x] = (oa << 24) | (orr << 16) | (og << 8) | ob;
                }
            }
        }

        // 垂直滑动窗口（前缀和，O(1)/像素）
        private static void BoxBlurV(int[] src, int[] dst, int w, int h, int r)
        {
            var prefix = new long[(h + 1) * 4];
            for (int x = 0; x < w; x++)
            {
                long a = 0, rr = 0, g = 0, b = 0;
                prefix[0] = 0; prefix[1] = 0; prefix[2] = 0; prefix[3] = 0;
                for (int y = 0; y < h; y++)
                {
                    int c = src[y * w + x];
                    a += (c >> 24) & 0xFF;
                    rr += (c >> 16) & 0xFF;
                    g += (c >> 8) & 0xFF;
                    b += c & 0xFF;
                    int o = (y + 1) * 4;
                    prefix[o] = a; prefix[o + 1] = rr; prefix[o + 2] = g; prefix[o + 3] = b;
                }
                for (int y = 0; y < h; y++)
                {
                    int top = Math.Max(0, y - r);
                    int bottom = Math.Min(h - 1, y + r);
                    int o0 = top * 4, o1 = (bottom + 1) * 4;
                    int cnt = bottom - top + 1;
                    int oa = (int)((prefix[o1 + 0] - prefix[o0 + 0]) / cnt);
                    int orr = (int)((prefix[o1 + 1] - prefix[o0 + 1]) / cnt);
                    int og = (int)((prefix[o1 + 2] - prefix[o0 + 2]) / cnt);
                    int ob = (int)((prefix[o1 + 3] - prefix[o0 + 3]) / cnt);
                    dst[y * w + x] = (oa << 24) | (orr << 16) | (og << 8) | ob;
                }
            }
        }

        // 临时字节记账（诊断用；正数=占用，负数=释放）
        private static void AddTempBytes(long delta)
        {
            long now = Interlocked.Add(ref _currentTempBytes, delta);
            if (now < 0) Interlocked.Exchange(ref _currentTempBytes, 0);

            long peak = Interlocked.Read(ref _peakTempBytes);
            while (now > peak)
            {
                long prev = Interlocked.CompareExchange(ref _peakTempBytes, now, peak);
                if (prev == peak) break;
                peak = prev;
            }
        }

        // 峰值临时像素估算（降采样缓冲的像素数）
        private static int PeakTempPixels(int sw, int sh, double captureScale)
        {
            int dw = Math.Max(1, (int)Math.Round(sw * captureScale));
            int dh = Math.Max(1, (int)Math.Round(sh * captureScale));
            return dw * dh;
        }

        // 进程 CPU 时间（毫秒）。★ 这是 CPU 口径，不是经过时间（合同 §6.1）。
        private static double CurrentCpuMs()
        {
            try
            {
                return s_cpuProcess.TotalProcessorTime.TotalMilliseconds;
            }
            catch
            {
                return 0;
            }
        }

        // 经过时间（毫秒）。★ 这是墙钟口径，不是 CPU 时间（合同 §6.1）。
        private static double ElapsedMsSince(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        // 当前进程（缓存实例；TotalProcessorTime 每次读取都是实时值）
        private static readonly System.Diagnostics.Process s_cpuProcess = System.Diagnostics.Process.GetCurrentProcess();
    }
}
