using System.Drawing;
using LiteMonitor.src.SystemServices.Backdrop;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 预览用背景帧来源：只提供**固定样例图**，绝不访问真实桌面。
    ///
    /// ★ 合同要点：
    /// 1. 本类**不含任何抓屏调用**（位块传输 / 窗口打印 / 屏幕拷贝一类一律没有），
    ///    也不创建计时器或后台任务；样例图由
    ///    <see cref="ThemePreviewControl.CreatePreviewSample"/> 生成的静态图案提供。
    /// 2. 帧是**借用**引用：本类既不释放样例图，也不修改它
    ///    （样例图由 <see cref="ThemePreviewControl"/> 自己创建、自己释放）。
    /// 3. <see cref="IsPreview"/> 返回 true —— 通过本类提供的帧一律报
    ///    <c>PreviewFrame</c>，**不会**被误记成"实时玻璃真的在工作"（独立复核 N2）。
    /// <para>
    /// ★ <b>编辑器的预览路径已经不用本类了</b>：编辑器把固定样例图通过
    ///   <c>AppearanceRuntime</c> 的 <c>previewSource</c> 参数交给
    ///   <c>options.PreviewSource</c>，那条路径**根本不注册帧后端**（后端调用次数 = 0），
    ///   是不依赖约定的结构性保证。本类保留给需要"以帧来源身份"注入固定图的场景
    ///   （测试、复现程序、未来的预览适配器）。
    /// </para>
    /// </summary>
    public sealed class PreviewFrameSource : IBackdropFrameSource
    {
        private Image? _sample;

        /// <summary>固定样例图（可空）。为空表示"没有样例图"，玻璃背景会按降级链回退。</summary>
        public Image? Sample
        {
            get => _sample;
            set => _sample = value;
        }

        /// <summary>构造（还没有样例图）。</summary>
        public PreviewFrameSource()
        {
        }

        /// <summary>构造（直接给一张固定样例图）。</summary>
        /// <param name="sample">固定样例图（借用，本类不释放）。</param>
        public PreviewFrameSource(Image? sample)
        {
            _sample = sample;
        }

        /// <summary>后端是否就绪：只有拿到固定样例图才算就绪。</summary>
        public bool IsReady => _sample != null;

        /// <summary>
        /// 本来源提供的帧是**预览用**的 ⇒ 诊断里报 <c>PreviewFrame</c>。
        /// </summary>
        public bool IsPreview => true;

        /// <summary>未就绪原因（人类可读），会进入背景层的降级诊断文本。</summary>
        public string UnavailableReason => "预览没有固定样例图，玻璃背景已按降级链回退。";

        /// <summary>
        /// 取当前帧：返回固定样例图。
        /// </summary>
        /// <param name="blurPx">要求的模糊半径（物理像素）。预览固定样例图不模糊，直接忽略。</param>
        /// <param name="frame">取到的帧（借用，调用方不得释放）。</param>
        /// <returns>拿到固定样例图时为 true。</returns>
        public bool TryGetFrame(double blurPx, out Image? frame)
        {
            frame = _sample;
            return frame != null;
        }
    }
}
