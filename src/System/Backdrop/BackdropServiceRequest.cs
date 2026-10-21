using System;
using System.Drawing;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 不可变捕获请求（M2 合同 §1「捕获请求」/ §3「代次」）
    //
    // ★ 命名说明（本包统一）：服务侧类型一律用 "BackdropService*" 前缀。
    //   同命名空间下已有 ScreenCaptureFrameSource.cs 定义的
    //   BackdropCaptureRequest / BackdropCaptureState / BackdropCaptureMetrics，
    //   重名会直接编译失败，故本包整体改名避让（**不改动对方的文件**）。
    //   两套请求的字段对照见集成说明（物理矩形 / DPI / 模糊半径 / 三个代次 / 帧率）。
    // ================================================================

    /// <summary>
    /// 一次实时背景捕获的**不可变请求快照**。
    ///
    /// ★ 合同要点（M2 合同 §1 / §3 / §11）：
    /// 1. **不可变**：构造后只读（全部为 get-only 属性）。它是"某一刻的快照"，
    ///    不是任务队列、不是可变对象 —— 后台服务在某一刻读到它，就按这一刻的
    ///    几何与代次去捕获，捕获结果带着**同一份代次**回来。
    /// 2. **物理像素**：客户区矩形是**屏幕物理像素**（X/Y 允许为负 —— 多显示器
    ///    布局下客户区可能有一部分在主屏左侧/上方）；模糊半径直接取
    ///    <see cref="ThemeAppearanceOptions.BlurPx"/>（物理像素），
    ///    **不再乘一次缩放**（合同 §11：<c>BlurPx</c> 已是物理像素）。
    /// 3. **代次只增不减**：主题代次 / 窗口句柄代次 / 尺寸代次由**唯一 owner**
    ///    （主面板接线方）维护并随请求携带；后台与 UI 都用它做"结果归属校验"。
    /// 4. **帧率有上限**：默认 2 fps、硬上限 6 fps（合同 §6.3），构造时夹紧。
    /// </summary>
    public sealed class BackdropServiceRequest
    {
        /// <summary>默认目标帧率（2 fps，合同 §6.3）。</summary>
        public const int DefaultTargetFps = 2;

        /// <summary>目标帧率下限（1 fps）。</summary>
        public const int MinTargetFps = 1;

        /// <summary>目标帧率上限（6 fps，合同 §6.3）。</summary>
        public const int MaxTargetFps = 6;

        /// <summary>默认捕获降采样比例（A06 生产建议 0.25）。</summary>
        public const double DefaultCaptureScale = 0.25;

        /// <summary>
        /// 构造一份不可变捕获请求。
        /// </summary>
        /// <param name="windowHandle">目标窗口句柄（HWND）。捕获后端据此施加/恢复
        /// <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>（合同 §5.2.3）。</param>
        /// <param name="physicalClientBounds">客户区在**屏幕坐标系**中的物理像素矩形。
        /// X/Y 允许为负（多显示器）；Width/Height 是实际捕获尺寸。</param>
        /// <param name="dpiScale">当前窗口的实际 DPI 比例（DeviceDpi / 96）。</param>
        /// <param name="userScale">用户缩放倍率（主题设置里的倍率）。</param>
        /// <param name="themeGeneration">主题代次（换肤时自增）。</param>
        /// <param name="handleGeneration">窗口句柄代次（句柄被销毁/重建时自增）。</param>
        /// <param name="sizeGeneration">尺寸代次（客户区几何变化时自增）。</param>
        /// <param name="blurPx">模糊半径（**物理像素**，直接用 <c>options.BlurPx</c>，不再乘缩放）。</param>
        /// <param name="targetFps">目标帧率（默认 2，夹紧到 1–6）。</param>
        /// <param name="captureScale">捕获降采样比例（0 &lt; s ≤ 1，默认 0.25）。
        /// 模糊的 σ 换算到降采样空间时需要它，故随请求携带。</param>
        public BackdropServiceRequest(
            IntPtr windowHandle,
            Rectangle physicalClientBounds,
            float dpiScale,
            float userScale,
            int themeGeneration,
            long handleGeneration,
            long sizeGeneration,
            double blurPx,
            int targetFps = DefaultTargetFps,
            double captureScale = DefaultCaptureScale)
        {
            WindowHandle = windowHandle;
            PhysicalClientBounds = physicalClientBounds;
            DpiScale = SanitizeScale(dpiScale);
            UserScale = SanitizeScale(userScale);
            EffectiveScale = DpiScale * UserScale;
            DeviceDpi = (int)Math.Round(DpiScale * 96f, MidpointRounding.AwayFromZero);
            ThemeGeneration = themeGeneration;
            HandleGeneration = handleGeneration;
            SizeGeneration = sizeGeneration;
            BlurPx = double.IsNaN(blurPx) || blurPx < 0d ? 0d : blurPx;
            TargetFps = Math.Clamp(targetFps, MinTargetFps, MaxTargetFps);
            CaptureScale = double.IsNaN(captureScale) || captureScale <= 0d || captureScale > 1d
                ? DefaultCaptureScale
                : captureScale;
        }

        /// <summary>目标窗口句柄（HWND）。</summary>
        public IntPtr WindowHandle { get; }

        /// <summary>客户区的屏幕物理像素矩形（X/Y 可为负）。</summary>
        public Rectangle PhysicalClientBounds { get; }

        /// <summary>实际捕获宽度（物理像素）。</summary>
        public int CaptureWidth => Math.Max(0, PhysicalClientBounds.Width);

        /// <summary>实际捕获高度（物理像素）。</summary>
        public int CaptureHeight => Math.Max(0, PhysicalClientBounds.Height);

        /// <summary>客户区是否有有效宽高（false 时不应捕获）。</summary>
        public bool HasValidBounds => CaptureWidth > 0 && CaptureHeight > 0;

        /// <summary>当前窗口的实际 DPI 比例。</summary>
        public float DpiScale { get; }

        /// <summary>用户缩放倍率。</summary>
        public float UserScale { get; }

        /// <summary>合成比例 = DpiScale × UserScale。</summary>
        public float EffectiveScale { get; }

        /// <summary>当前窗口的实际 DPI（96 = 100%）。</summary>
        public int DeviceDpi { get; }

        /// <summary>主题代次（换肤时自增）。</summary>
        public int ThemeGeneration { get; }

        /// <summary>窗口句柄代次（句柄被销毁/重建时自增）。</summary>
        public long HandleGeneration { get; }

        /// <summary>尺寸代次（客户区几何变化时自增）。</summary>
        public long SizeGeneration { get; }

        /// <summary>模糊半径（物理像素，直接来自 <c>options.BlurPx</c>，不再乘缩放）。</summary>
        public double BlurPx { get; }

        /// <summary>目标帧率（1–6，默认 2）。</summary>
        public int TargetFps { get; }

        /// <summary>捕获降采样比例（0 &lt; s ≤ 1，默认 0.25）。</summary>
        public double CaptureScale { get; }

        /// <summary>两帧之间的最小间隔（毫秒）。</summary>
        public double FrameIntervalMs => 1000d / TargetFps;

        /// <summary>
        /// 代次是否与另一份请求一致（主题 / 句柄 / 尺寸三者全同才算一致）。
        /// </summary>
        public bool MatchesGeneration(BackdropServiceRequest other)
        {
            if (other is null) return false;
            return ThemeGeneration == other.ThemeGeneration
                && HandleGeneration == other.HandleGeneration
                && SizeGeneration == other.SizeGeneration
                && PhysicalClientBounds == other.PhysicalClientBounds;
        }

        /// <summary>
        /// 由外观快照构造一份捕获请求（接线方的常用入口）。
        /// <para>
        /// ★ 模糊半径直接取 <see cref="ThemeAppearanceOptions.BlurPx"/>（物理像素，**不再乘缩放**）。
        /// ★ 主题代次直接取 <see cref="ThemeAppearanceOptions.ThemeGeneration"/>；
        ///   句柄代次与尺寸代次由**唯一 owner**（主面板）自己维护并传入。
        /// </para>
        /// </summary>
        /// <param name="options">当前外观快照（只读）。</param>
        /// <param name="windowHandle">目标窗口句柄（HWND）。</param>
        /// <param name="physicalScreenClientBounds">客户区在屏幕坐标系中的物理像素矩形
        /// （一般由 <c>Control.PointToScreen</c> + 客户区尺寸换算而来，允许负坐标）。</param>
        /// <param name="handleGeneration">窗口句柄代次。</param>
        /// <param name="sizeGeneration">尺寸代次。</param>
        /// <param name="targetFps">目标帧率（默认 2）。</param>
        /// <param name="captureScale">捕获降采样比例（默认 0.25）。</param>
        public static BackdropServiceRequest FromOptions(
            ThemeAppearanceOptions options,
            IntPtr windowHandle,
            Rectangle physicalScreenClientBounds,
            long handleGeneration,
            long sizeGeneration,
            int targetFps = DefaultTargetFps,
            double captureScale = DefaultCaptureScale)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            return new BackdropServiceRequest(
                windowHandle,
                physicalScreenClientBounds,
                options.DpiScale,
                options.UserScale,
                options.ThemeGeneration,
                handleGeneration,
                sizeGeneration,
                options.BlurPx,
                targetFps,
                captureScale);
        }

        /// <summary>生成一行可写日志的诊断文本（会分配字符串，仅在显式调用时产生）。</summary>
        public string ToLogString()
        {
            return $"capture-request rect={PhysicalClientBounds.X},{PhysicalClientBounds.Y}"
                 + $" {CaptureWidth}x{CaptureHeight}px dpi={DeviceDpi} scale={EffectiveScale:0.##}"
                 + $" gen=(theme:{ThemeGeneration},handle:{HandleGeneration},size:{SizeGeneration})"
                 + $" blurPx={BlurPx:0.##} captureScale={CaptureScale:0.##} fps={TargetFps}";
        }

        private static float SanitizeScale(float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) return 1f;
            return scale;
        }
    }

    // ================================================================
    // 捕获后端自报的分段耗时
    // ================================================================

    /// <summary>
    /// 捕获后端自报的分段耗时（**经过时间**口径，单位毫秒）。
    ///
    /// ★ 口径说明（合同 §6.1）：这里的每个字段都是**墙钟时间**（<see cref="System.Diagnostics.Stopwatch"/>），
    ///   **不是**处理器时间。CPU 口径由服务用 <c>Process.TotalProcessorTime</c> 单独统计，
    ///   两者**不得混用**：CPU 预算只认 CPU 口径，延迟判据只认经过时间口径。
    /// </summary>
    public readonly struct BackdropServiceTiming
    {
        /// <summary>空值（后端未上报任何分段耗时）。</summary>
        public static readonly BackdropServiceTiming Empty = default;

        /// <summary>
        /// 构造一份分段耗时。
        /// </summary>
        /// <param name="excludeWindowMs">**瞬时排除窗口**的时长（排除属性生效期间无法捕获）。</param>
        /// <param name="backendCaptureMs">后端自报的纯捕获耗时（0 = 未测量）。</param>
        /// <param name="backendFilterMs">后端自报的纯滤波耗时（0 = 未测量）。</param>
        public BackdropServiceTiming(double excludeWindowMs = 0d, double backendCaptureMs = 0d, double backendFilterMs = 0d)
        {
            ExcludeWindowMs = excludeWindowMs < 0d ? 0d : excludeWindowMs;
            BackendCaptureMs = backendCaptureMs < 0d ? 0d : backendCaptureMs;
            BackendFilterMs = backendFilterMs < 0d ? 0d : backendFilterMs;
        }

        /// <summary>瞬时排除窗口的时长（毫秒，经过时间口径）。</summary>
        public double ExcludeWindowMs { get; }

        /// <summary>后端自报的纯捕获耗时（毫秒，0 = 未测量；仅供诊断）。</summary>
        public double BackendCaptureMs { get; }

        /// <summary>后端自报的纯滤波耗时（毫秒，0 = 未测量；仅供诊断）。</summary>
        public double BackendFilterMs { get; }
    }
}
