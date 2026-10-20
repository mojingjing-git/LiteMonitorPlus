using System.Drawing;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 实时背景帧来源（由 A15 实现；A08 只定义接口与降级决策）
    // ================================================================

    /// <summary>
    /// 实时背景帧来源。
    ///
    /// ★ 所有权合同：
    /// 1. <see cref="TryGetFrame"/> 返回的帧是**借用**引用：调用方与背景渲染器
    ///    都**不得释放**它，释放责任始终在生产者（后台捕获服务）一侧。
    /// 2. 本接口**不启动**任何抓屏/计时器/后台任务；是否以及何时抓屏由实现方
    ///    按 A06 的结论与 v4 §3.4 的帧所有权合同自行决定。
    /// 3. 实现方必须能报告"为什么不可用"，否则降级无法诊断。
    /// 4. 实现方必须**如实声明 <see cref="IsPreview"/>** —— 它决定诊断里的底图来源
    ///    是 <c>LiveFrame</c> 还是 <c>PreviewFrame</c>。把固定样例图当实时帧上报，
    ///    会让"实时玻璃真的在工作"这个验收结论失真（独立复核 N2）。
    /// </summary>
    public interface IBackdropFrameSource
    {
        /// <summary>后端是否已就绪、可以提供有效帧。</summary>
        bool IsReady { get; }

        /// <summary>
        /// 未就绪原因（人类可读）。<see cref="IsReady"/> 为 false 时必须给出可读原因，
        /// 该文本会进入降级诊断信息。
        /// </summary>
        string UnavailableReason { get; }

        /// <summary>
        /// 本提供者提供的帧是不是**预览用**的。
        /// <para>
        /// ★ true  = 固定样例图 / 测试帧 —— 诊断里报 <c>PreviewFrame</c>；
        /// ★ false = 真实捕获帧 —— 诊断里报 <c>LiveFrame</c>。
        /// </para>
        /// <para>
        /// ★ **默认约定**：真实捕获后端必须返回 false。
        ///   固定图提供者（编辑器的预览样例、探针的假后端）必须返回 true。
        /// </para>
        /// </summary>
        bool IsPreview { get; }

        /// <summary>
        /// 尝试取当前帧。
        /// </summary>
        /// <param name="blurPx">要求的模糊半径（物理像素，来自 <c>options.BlurPx</c>）。</param>
        /// <param name="frame">取到的帧（借用，调用方不得释放）。取不到时为 null。</param>
        /// <returns>取到有效帧时为 true。</returns>
        bool TryGetFrame(double blurPx, out Image? frame);
    }
}
