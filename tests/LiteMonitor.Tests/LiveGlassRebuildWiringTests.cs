using System;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 【LM-072 / R08】受控 HWND 重建与实时玻璃（M2）恢复的**接线**护栏。
    /// <para>
    /// ★ 为什么用 IL 护栏而不是行为测试：受控重建需要**真实**的 HWND 重建 +
    ///   真实的 GPU→legacy 路由切换，这在无头测试里无法复现；
    ///   而"重建完成后到底有没有调用恢复入口"是**结构性质**，
    ///   用 IL 顺序断言钉住比行为断言更稳定、也更贴近缺陷本身。
    /// </para>
    /// <para>★ 护栏钉住的四条主审裁决：
    /// <list type="number">
    /// <item>句柄销毁 ≠ 真关闭：<c>OnHandleDestroyed</c> 只走**临时停机**，
    ///   <c>OnFormClosed</c> 才允许整停（含把配置态开关置 false）。</item>
    /// <item>恢复必须发生在目标 legacy **就绪之后**（步 6–9 收尾处），
    ///   且必须按 <c>_cfg.LiveGlassEnabled</c> 判定。</item>
    /// <item>单一捕获所有者：GPU 宿主必须在恢复实时玻璃**之前**停止。</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class LiveGlassRebuildWiringTests
    {
        /// <summary>
        /// 裁决 ①：句柄销毁只停服务、**不得**清掉配置态的开关。
        /// <para>★ <c>OnHandleDestroyed</c> 里调用整停（<c>StopLiveGlass</c>）
        /// ⇒ 配置态开关被清成 false ⇒ 重建完成后没有任何东西会把它拉回来
        /// ⇒ "设置页仍显示开启，但捕获只能手动重开"。</para>
        /// </summary>
        [Fact]
        public void 句柄销毁必须走临时停机而不是整停()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(
                RequireMethod(typeof(MainForm), "OnHandleDestroyed"));

            Assert.True(
                CallOffsetOf(body, typeof(UIController), "StopLiveGlassForHandleRecreate") >= 0,
                "MainForm.OnHandleDestroyed 必须调用 UIController.StopLiveGlassForHandleRecreate（临时停机）—— "
                + "句柄销毁只停服务、保留配置态开关，重建完成后才按配置恢复（LM-072）。");

            Assert.True(
                CallOffsetOf(body, typeof(UIController), "StopLiveGlass") < 0,
                "MainForm.OnHandleDestroyed 不得调用 UIController.StopLiveGlass（整停）—— "
                + "整停会把配置态的实时玻璃开关清成 false，重建后永远恢复不了（LM-072）。");
        }

        /// <summary>
        /// 裁决 ①（另一半）：<b>真关闭</b>必须整停 —— 退出时要把配置态开关一起清掉。
        /// </summary>
        [Fact]
        public void 真关闭必须整停实时玻璃()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(
                RequireMethod(typeof(MainForm), "OnFormClosed"));

            Assert.True(
                CallOffsetOf(body, typeof(UIController), "StopLiveGlass") >= 0,
                "MainForm.OnFormClosed 是真关闭，必须调用 UIController.StopLiveGlass（整停）—— "
                + "退出时既停捕获、也把配置态开关清掉（LM-072）。");
        }

        /// <summary>
        /// 裁决 ② + ③：受控重建完成后必须**按配置**恢复实时玻璃，
        /// 且恢复点位于步 6–9 的收尾处（目标 legacy 就绪之后）。
        /// </summary>
        [Fact]
        public void 受控重建完成后必须按配置恢复实时玻璃()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(
                RequireMethod(typeof(UIController), "CompleteLegacyTransition"));

            int restoreAt = CallOffsetOf(body, typeof(LiveGlassController), "RestoreIfEnabled");
            Assert.True(
                restoreAt >= 0,
                "UIController.CompleteLegacyTransition 必须调用 LiveGlassController.RestoreIfEnabled —— "
                + "受控重建期间捕获服务被临时停机，重建完成（步 6–9）后必须按配置拉回来（LM-072）。");

            int publishAt = CallOffsetOf(body, typeof(RenderingTransitionCoordinator), "PublishStable");
            Assert.True(
                publishAt >= 0,
                "UIController.CompleteLegacyTransition 里找不到步 9 的 PublishStable —— 护栏前提被破坏。");

            Assert.True(
                restoreAt > publishAt,
                "恢复调用必须位于步 6–9 的**收尾处**（步 9 发布稳定状态之后）—— "
                + "句柄尚未就绪时恢复只会拿到不完整的捕获请求（LM-072）。");

            int configAt = CallOffsetOf(body, typeof(Settings), "get_LiveGlassEnabled");
            Assert.True(
                configAt >= 0 && configAt < restoreAt,
                "恢复必须按 _cfg.LiveGlassEnabled 判定（LM-072）—— "
                + "找不到 Settings.LiveGlassEnabled 的读取点，或它出现在恢复调用之后。");
        }

        /// <summary>
        /// 裁决 ④：单一捕获所有者 —— GPU 宿主必须在实时玻璃恢复**之前**停止。
        /// </summary>
        [Fact]
        public void GPU宿主必须在实时玻璃恢复之前停止()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(
                RequireMethod(typeof(UIController), "RunGpuToLegacyTransition"));

            int stopGpuAt = CallOffsetOf(body, typeof(UIController), "StopGpuPresentation");
            int completeAt = CallOffsetOf(body, typeof(UIController), "CompleteLegacyTransition");

            Assert.True(
                stopGpuAt >= 0 && completeAt >= 0,
                "UIController.RunGpuToLegacyTransition 里找不到 StopGpuPresentation / CompleteLegacyTransition —— "
                + "护栏前提被破坏。");

            Assert.True(
                stopGpuAt < completeAt,
                "步 3 必须先停 GPU 宿主，步 6–9 才恢复实时玻璃 —— "
                + "两个捕获所有者并存会重复抓屏、击穿 CPU 预算（合同 §13.2，LM-072）。");
        }

        // ---------------------------------------------------------------- 辅助

        private static MethodInfo RequireMethod(Type type, string name)
        {
            MethodInfo[] candidates = Array.FindAll(
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                method => method.Name == name);

            Assert.True(
                candidates.Length == 1,
                $"{type.Name}.{name} 应当恰好有一个实例方法（实际 {candidates.Length} 个）—— 护栏前提被破坏。");
            return candidates[0];
        }

        /// <summary>
        /// 找到"调用了 <paramref name="declaringType"/>.<paramref name="name"/>"的第一条指令偏移；
        /// 找不到返回 -1。
        /// </summary>
        private static int CallOffsetOf(IlInspector.Disassembly body, Type declaringType, string name)
        {
            foreach (IlInspector.IlInstruction instruction in body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) continue;
                if (instruction.Target is not MethodInfo called) continue;
                if (called.DeclaringType == declaringType && called.Name == name)
                {
                    return instruction.Offset;
                }
            }

            return -1;
        }
    }
}
