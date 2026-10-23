using LiteMonitor.Rendering.Configuration;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【合同 §10 GPU-005 / 裁决 D6 · owner A 物化补全】
/// 覆盖 <see cref="IGpuStartupAttemptAbandon"/>：首帧失败且停止流程**不干净**时，
/// pending 标记必须**保留** ⇒ 下次进程启动进入 conservative、等待用户显式重试。
/// <para>本文件同时补上 GPU T04 交付报告里标为 <c>NOT_VERIFIED</c> 的
/// "停止不干净 ⇒ pending 保留"在 <b>guard 侧</b>的钉住（宿主侧仍需真实 GPU，属 T07/T09）。</para>
/// </summary>
public sealed class GpuStartupGuardAbandonTests
{
    [Fact]
    public void 放弃启动尝试_该尝试实现了放弃接口()
    {
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard guard = new(directory, @"C:\apps\A");
            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));

            Assert.IsAssignableFrom<IGpuStartupAttemptAbandon>(attempt!);

            attempt!.AbandonPreservingPending();
            attempt.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 放弃启动尝试_pending标记保留且下次进程进入保守()
    {
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard first = new(directory, @"C:\apps\A");
            Assert.True(first.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));
            attempt!.AbandonPreservingPending();

            // 下次进程启动必须读到 pending 标记 ⇒ conservative ⇒ 禁止自动 GPU 启动。
            GpuStartupGuard nextProcess = new(directory, @"C:\apps\A");
            Assert.True(nextProcess.ConservativeForThisProcess);
            Assert.False(nextProcess.TryBeginAttempt(
                out _,
                out string? code,
                out _));
            Assert.Equal("gpu_startup_guard_previous_attempt_unclean", code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 放弃启动尝试_尝试仍被注销_后续成功的首帧可以清掉pending标记()
    {
        // ★ 这条是"把该尝试从集合里注销"的关键收益：
        //   若放弃后该尝试仍占着集合，后续任何一次成功首帧都会因"集合非空"提前返回
        //   ⇒ pending 标记永远清不掉 ⇒ 即便用户后来成功过，下个进程仍被误判为保守。
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard guard = new(directory, @"C:\apps\A");
            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? abandoned, out _, out _));
            abandoned!.AbandonPreservingPending();

            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? later, out _, out _));
            later!.MarkFirstSuccessfulFrame();
            later.Dispose();

            GpuStartupGuard nextProcess = new(directory, @"C:\apps\A");
            Assert.False(nextProcess.ConservativeForThisProcess);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 放弃启动尝试_是幂等的_且终结后不能再标记成功()
    {
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard guard = new(directory, @"C:\apps\A");
            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));

            attempt!.AbandonPreservingPending();

            // ★ 判别点必须在 Dispose **之前**：Dispose 也会把 _disposed 置 1，
            //   若先 Dispose 再断言，"终结后不可再标记成功"就恒真 ⇒ 变体不被判别。
            Assert.Throws<ObjectDisposedException>(() => attempt.MarkFirstSuccessfulFrame());

            // 重复放弃：不得抛异常，也不得改变任何状态。
            attempt.AbandonPreservingPending();
            attempt.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 正常Dispose仍会清掉pending标记_与放弃路径行为不同()
    {
        // 对照组：正常 Dispose（= 首帧成功或受控放弃且停止流程干净）**必须**清掉标记。
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard guard = new(directory, @"C:\apps\A");
            Assert.True(guard.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));
            attempt!.MarkFirstSuccessfulFrame();
            attempt.Dispose();

            GpuStartupGuard nextProcess = new(directory, @"C:\apps\A");
            Assert.False(nextProcess.ConservativeForThisProcess);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "litemonitor-gpu-guard-abandon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
