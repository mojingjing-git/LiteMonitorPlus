using System;
using System.Reflection;
using System.Reflection.Emit;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// GPU 尺寸同步的**健壮性护栏**。
    /// <para>背景：旧实现在 Resize 里**先释放尺寸相关资源、再改交换链尺寸**，
    /// 一旦 ResizeBuffers 失败，渲染器就停在半残状态（下一帧必抛 "not ready to draw"）
    /// ⇒ 一次可恢复的尺寸抖动被升级成整场降级（用户看到"液态玻璃突然失效"）。
    /// 宿主侧还把尺寸代次提前自增，失败后宿主与渲染器代次**永久错位**，
    /// 场景更新被静默丢弃 ⇒ 面板看起来"空掉"。</para>
    /// </summary>
    public sealed class GpuResizeResilienceGateTests
    {
        private static MethodInfo ResizeMethod =>
            typeof(GpuSceneRenderer).GetMethod(
                "Resize",
                BindingFlags.Public | BindingFlags.Instance)!;

        private static MethodInfo ReleaseMethod =>
            typeof(GpuSceneRenderer).GetMethod(
                "ReleaseSizeDependentResources",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static MethodInfo NotifySizeChangedMethod =>
            typeof(GpuPresentationHostController).GetMethod(
                "NotifySizeChanged",
                BindingFlags.Public | BindingFlags.Instance)!;

        [Fact]
        public void 护栏_改尺寸必须先改交换链再释放尺寸资源()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(ResizeMethod);

            int resizeBuffersOffset = -1;
            int releaseOffset = -1;
            foreach (IlInspector.IlInstruction instruction in body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                {
                    continue;
                }

                if (instruction.Target is not MethodInfo called)
                {
                    continue;
                }

                if (called.Name.Contains("ResizeBuffers", StringComparison.Ordinal))
                {
                    resizeBuffersOffset = resizeBuffersOffset < 0
                        ? instruction.Offset
                        : Math.Min(resizeBuffersOffset, instruction.Offset);
                }
                else if (called.Name == ReleaseMethod.Name)
                {
                    releaseOffset = releaseOffset < 0
                        ? instruction.Offset
                        : Math.Min(releaseOffset, instruction.Offset);
                }
            }

            Assert.True(resizeBuffersOffset >= 0, "Resize 里必须调用交换链改尺寸（ResizeBuffers）。");
            Assert.True(releaseOffset >= 0, "Resize 里必须释放尺寸相关资源。");
            Assert.True(
                resizeBuffersOffset < releaseOffset,
                "必须**先**改交换链尺寸、**后**释放尺寸相关资源：" +
                "否则 ResizeBuffers 一失败，渲染器就停在半残状态，下一帧必抛，" +
                "一次尺寸抖动会被升级成整场降级。");
        }

        [Fact]
        public void 护栏_宿主尺寸同步必须捕获异常且不得无条件拆会话()
        {
            MethodBody? methodBody = NotifySizeChangedMethod.GetMethodBody();
            bool hasExceptionHandler =
                methodBody is not null && methodBody.ExceptionHandlingClauses.Count > 0;
            IlInspector.Disassembly body = IlInspector.Disassemble(NotifySizeChangedMethod);
            bool callsStopCore = false;
            bool callsReset = false;

            foreach (IlInspector.IlInstruction instruction in body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                {
                    continue;
                }

                if (instruction.Target is not MethodInfo called)
                {
                    continue;
                }

                if (called.Name == "StopCore")
                {
                    callsStopCore = true;
                }
                else if (called.Name == "ResetResizeFailureCount")
                {
                    callsReset = true;
                }
            }

            Assert.True(hasExceptionHandler, "NotifySizeChanged 必须捕获异常（异常逃进 WinForms 事件会被当成未处理异常）。");
            Assert.True(callsStopCore, "连续失败仍要能降级会话（保留兜底路径）。");
            Assert.True(callsReset, "尺寸同步成功后必须清零失败计数。");
        }

        [Fact]
        public void 护栏_失败计数阈值必须存在且可读()        {
            FieldInfo? field = typeof(GpuPresentationHostController).GetField(
                "ResizeFailureThreshold",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(field);
            int threshold = (int)field!.GetValue(null)!;
            Assert.True(threshold >= 2, "阈值至少为 2：一次抖动不应立刻降级。");
        }
    }
}
