using System;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 背景层（A08 静态背景与降级）的状态模型
    // ================================================================

    /// <summary>
    /// 背景渲染模式（**解析后的有效模式**，不一定等于主题请求的模式）。
    /// </summary>
    public enum BackdropRenderMode
    {
        /// <summary>旧背景路径：本组件不绘制任何内容，沿用调用方原有的实心背景填充。</summary>
        Solid = 0,

        /// <summary>静态渐变。</summary>
        Gradient = 1,

        /// <summary>实时材质（玻璃）：底图来自已就绪的后端帧，或预览使用的固定源。</summary>
        Glass = 2
    }

    /// <summary>
    /// 降级原因码。**降级必须可诊断**：任何一次降级都要带上原因码与可读原因文本，
    /// 不允许静默回退。
    /// </summary>
    public enum BackdropDegradeReason
    {
        /// <summary>未发生降级。</summary>
        None = 0,

        /// <summary>主题请求了 glass，但没有注册任何实时背景后端。</summary>
        BackendMissing = 1,

        /// <summary>已注册后端，但后端报告自己未就绪。</summary>
        BackendNotReady = 2,

        /// <summary>后端声称就绪，但没有给出可用帧（空帧或取帧失败）。</summary>
        BackendFrameInvalid = 3,

        /// <summary>后端不可用，且渐变两端颜色均不可用（alpha 为 0），只能沿用旧背景路径。</summary>
        GradientUnavailable = 4,

        /// <summary>客户区没有有效宽高，已跳过全部绘制。</summary>
        InvalidBounds = 5
    }

    /// <summary>
    /// 底图来源（用于区分"实时玻璃"与"预览固定源"）。
    /// </summary>
    public enum BackdropFrameSource
    {
        /// <summary>没有底图。</summary>
        None = 0,

        /// <summary>沿用旧背景路径（未绘制底板）。</summary>
        Legacy = 1,

        /// <summary>自绘渐变。</summary>
        Gradient = 2,

        /// <summary>预览固定源（<c>ThemeAppearanceOptions.PreviewSource</c>），不是实时捕获结果。</summary>
        PreviewFrame = 3,

        /// <summary>实时后端帧（由 A15 的后端提供，本组件只借用不释放）。</summary>
        LiveFrame = 4
    }

    /// <summary>
    /// 本次实际绘制的层。用于自证"新能力关闭时零副作用"。
    /// </summary>
    [Flags]
    public enum BackdropLayer
    {
        /// <summary>没有绘制任何层。</summary>
        None = 0,

        /// <summary>不透明底板（保证重复绘制幂等、不累积变色）。</summary>
        Base = 1 << 0,

        /// <summary>背景染色层（tint，alpha = Tint 自身 alpha × Opacity）。</summary>
        Tint = 1 << 1,

        /// <summary>固定种子的噪点层。</summary>
        Noise = 1 << 2,

        /// <summary>边缘辉光层。</summary>
        EdgeGlow = 1 << 3
    }

    /// <summary>
    /// 背景层渲染状态（只读值类型）。
    ///
    /// ★ 合同要点：
    /// 1. 这是**值类型**，正常路径不产生任何堆分配（solid 模式可证零分配）。
    /// 2. <see cref="Reason"/> 在发生降级时**必定非空**，供 UI 与日志直接读取，
    ///    不允许"降级了但没人知道"。
    /// 3. <see cref="Source"/> 区分实时帧与预览固定源，避免把预览样例当成实时玻璃。
    /// </summary>
    public readonly struct BackdropStatus
    {
        /// <summary>构造一个状态。</summary>
        public BackdropStatus(
            string requestedMode,
            BackdropRenderMode effectiveMode,
            bool isDegraded,
            BackdropDegradeReason reasonCode,
            string? reason,
            BackdropFrameSource source,
            BackdropLayer layers,
            double requiredBlurPx)
        {
            RequestedMode = requestedMode ?? ThemeAppearanceModeNames.Solid;
            EffectiveMode = effectiveMode;
            IsDegraded = isDegraded;
            ReasonCode = reasonCode;
            Reason = reason;
            Source = source;
            Layers = layers;
            RequiredBlurPx = requiredBlurPx;
        }

        /// <summary>主题请求的模式（solid / gradient / glass）。</summary>
        public string RequestedMode { get; }

        /// <summary>实际生效的模式。</summary>
        public BackdropRenderMode EffectiveMode { get; }

        /// <summary>是否发生了降级。</summary>
        public bool IsDegraded { get; }

        /// <summary>降级原因码（未降级时为 <see cref="BackdropDegradeReason.None"/>）。</summary>
        public BackdropDegradeReason ReasonCode { get; }

        /// <summary>降级原因文本（人类可读）。未降级时为 null。</summary>
        public string? Reason { get; }

        /// <summary>底图来源。</summary>
        public BackdropFrameSource Source { get; }

        /// <summary>本次实际绘制的层。</summary>
        public BackdropLayer Layers { get; }

        /// <summary>本次要求的模糊半径（物理像素，来自 <c>options.BlurPx</c>，由捕获后端执行）。</summary>
        public double RequiredBlurPx { get; }

        /// <summary>有效模式的名称（solid / gradient / glass）。</summary>
        public string EffectiveModeName => EffectiveMode switch
        {
            BackdropRenderMode.Gradient => ThemeAppearanceModeNames.Gradient,
            BackdropRenderMode.Glass => ThemeAppearanceModeNames.Glass,
            _ => ThemeAppearanceModeNames.Solid
        };

        /// <summary>是否绘制了任何一层（false 表示本组件完全无副作用）。</summary>
        public bool PaintedAnything => Layers != BackdropLayer.None;

        /// <summary>生成一行可写日志的诊断文本（会分配字符串，仅在显式调用时产生）。</summary>
        public string ToLogString()
        {
            return $"backdrop requested={RequestedMode} effective={EffectiveModeName}"
                 + $" degraded={(IsDegraded ? "yes" : "no")} code={ReasonCode} source={Source}"
                 + $" layers={Layers} blurPx={RequiredBlurPx:0.##} reason={Reason ?? "-"}";
        }
    }

    /// <summary>背景模式名称常量（与 <c>ThemeAppearanceOptions</c> 中的常量同值，避免重复字面量）。</summary>
    internal static class ThemeAppearanceModeNames
    {
        internal const string Solid = "solid";
        internal const string Gradient = "gradient";
        internal const string Glass = "glass";
    }
}
