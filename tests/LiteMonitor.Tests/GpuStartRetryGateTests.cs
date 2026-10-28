using System;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// GPU 启动的「延迟一次重试」护栏。
    /// <para>背景：切到 GPU 主题时，旧宿主在同一个 tick 内被 Dispose、新宿主立刻创建并启动，
    /// 而 DXGI 的设备释放是异步的 ⇒ 新建交换链/设备可能撞上瞬态错误
    /// （DEVICE_REMOVED / STILL_HAVE_PENDING_OPERATIONS）⇒ 启动失败 ⇒ 立刻降级到 legacy。
    /// 用户侧的观感是"切液态玻璃就回退"，必须手动把窗口挪到另一块屏才恢复。</para>
    /// </summary>
    public sealed class GpuStartRetryGateTests
    {
        private static MethodInfo ScheduleMethod =>
            typeof(UIController).GetMethod(
                "ScheduleGpuFailureFallback",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

        [Fact]
        public void 护栏_启动失败必须先尝试安排延迟重试再考虑降级()
        {
            IlInspector.Disassembly body = IlInspector.Disassemble(ScheduleMethod);

            int retryCallOffset = -1;
            int enqueueOffset = -1;
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

                if (called.Name == "TryScheduleGpuStartRetry")
                {
                    retryCallOffset = retryCallOffset < 0
                        ? instruction.Offset
                        : Math.Min(retryCallOffset, instruction.Offset);
                }
                else if (called.Name == "Request" &&
                         called.DeclaringType?.Name.Contains("GpuFailureFallback") == true)
                {
                    enqueueOffset = enqueueOffset < 0
                        ? instruction.Offset
                        : Math.Min(enqueueOffset, instruction.Offset);
                }
            }

            Assert.True(retryCallOffset >= 0, "故障调度必须先尝试安排延迟重试。");
            Assert.True(enqueueOffset >= 0, "延迟重试用掉后仍要能排进降级队列。");
            Assert.True(
                retryCallOffset < enqueueOffset,
                "延迟重试的判断必须**早于**入队降级，否则重试永远等不到执行时机。");
        }

        [Fact]
        public void 护栏_延迟重试定时器与预算必须存在()
        {
            FieldInfo? timer = typeof(UIController).GetField(
                "_gpuStartRetryTimer",
                BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo? budget = typeof(UIController).GetField(
                "_gpuStartRetryBudget",
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(timer);
            Assert.NotNull(budget);
        }

        [Fact]
        public void 护栏_延迟重试到期必须重建宿主而不是复用失败的宿主()
        {
            MethodInfo tick = typeof(UIController).GetMethod(
                "OnGpuStartRetryTick",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(tick);

            IlInspector.Disassembly body = IlInspector.Disassemble(tick);

            bool stops = false;
            bool creates = false;
            bool starts = false;
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

                if (called.Name == "StopGpuPresentation") stops = true;
                else if (called.Name == "CreateGpuPresentationHost") creates = true;
                else if (called.Name == "StartGpuPresentation") starts = true;
            }

            Assert.True(stops, "重试前必须停掉失败的旧宿主。");
            Assert.True(creates, "重试必须新建宿主（失败的宿主不可复用）。");
            Assert.True(starts, "重试必须真正启动一次。");
        }
    }
}
