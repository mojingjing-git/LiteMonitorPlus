using System;
using System.Reflection;
using System.Reflection.Emit;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 实时桌面捕获的**重试抑制**护栏。
    /// <para>背景：渲染循环里 <c>_captureSource == null</c> 时会每帧重试启动实时捕获
    /// （每 8ms 一次）。当窗口所在显示器不在渲染适配器的输出列表里时（双屏分属不同 GPU），
    /// 这个重试会永远失败：既拖垮 CPU，反复重建捕获对象还可能把 GPU 拖进
    /// DEVICE_REMOVED ⇒ 整场降级到 legacy —— 用户看到"切液态玻璃就回退、换屏才正常"。</para>
    /// </summary>
    public sealed class LiveCaptureRetrySuppressionTests
    {
        private static MethodInfo TryStartLiveCaptureMethod =>
            typeof(GpuSceneRenderer).GetMethod(
                "TryStartLiveCapture",
                BindingFlags.Public | BindingFlags.Instance)!;

        [Fact]
        public void 护栏_捕获启动必须记住失败显示器以抑制同屏重试()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(TryStartLiveCaptureMethod);

            bool readsFailedMonitor = false;
            bool recordsFailure = false;
            foreach (IlInspector.IlInstruction instruction in body.Instructions)
            {
                if (instruction.OpCode == OpCodes.Ldfld &&
                    instruction.Field?.Name == "_liveCaptureFailedMonitor")
                {
                    readsFailedMonitor = true;
                }

                if (instruction.OpCode == OpCodes.Call &&
                    instruction.Target is MethodInfo called &&
                    called.Name == "RecordLiveCaptureFailure")
                {
                    recordsFailure = true;
                }
            }

            Assert.True(readsFailedMonitor, "捕获启动必须读取「失败显示器」记录，用于抑制同屏重复重试。");
            Assert.True(recordsFailure, "捕获失败必须被记录，否则渲染循环会每帧重复尝试。");
        }

        [Fact]
        public void 护栏_用户主动操作必须允许重新尝试捕获()
        {
            // 切主题 / 重新配置 / 显式恢复属于"用户主动操作"，
            // 这时应该再试一次（环境可能已变化，例如换了输出接线）。
            MethodInfo reset = typeof(GpuSceneRenderer).GetMethod(
                "ResetLiveCaptureFailure",
                BindingFlags.Public | BindingFlags.Instance)!;
            Assert.NotNull(reset);

            foreach (string methodName in new[] { "Start", "TryReconfigure" })
            {
                MethodInfo? method = typeof(GpuPresentationHostController).GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance);
                Assert.NotNull(method);

                IlInspector.Disassembly body = IlInspector.Disassemble(method!);
                bool callsReset = false;
                foreach (IlInspector.IlInstruction instruction in body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                    {
                        continue;
                    }

                    if (instruction.Target is MethodInfo called &&
                        called.Name == "ResetLiveCaptureFailure")
                    {
                        callsReset = true;
                    }
                }

                Assert.True(
                    callsReset,
                    $"{methodName} 必须先解除捕获失败抑制，再尝试实时捕获。");
            }
        }
    }
}
