using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-073】WDA 取得的**统一闸门**（报告 R09）。
/// <para>缺陷：旧实时会话的 affinity 恢复失败并把清理责任移交 ledger 之后，
/// 同一 HWND 可以先启动 <b>fixed</b> 宿主（设计上允许），随后用户开启 live 时
/// <c>TryReconfigure</c> 会**直接**施加 <c>WDA_EXCLUDEFROMCAPTURE</c>（无闸门），
/// 新会话把遗留的 0x11 当成"外部原值"，旧 ledger 随后写回原策略 ⇒ 撤掉正在使用的排除 ⇒ 自捕获。</para>
/// <para>★ 契约口径（合同 §6.2 INV-6 / §4.5 :202）：**所有"要施加 WDA_EXCLUDEFROMCAPTURE"的入口**
/// 在施加前必须查**同一个**闸门；<b>释放</b> WDA 的分支不受闸门约束。</para>
/// </summary>
public sealed class CapturePolicyCleanupGateTests
{
    // ================================================================
    // 1. 启动入口（合同 §6.2 INV-6）
    // ================================================================

    [Fact]
    public void LM073_启动取得WDA所有权时同一代次仍有未完成清理必须被拒绝()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0);
                using Form form = new();
                _ = form.Handle;
                WindowHandleGeneration generations = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter,
                    handleGeneration: generations);

                // 同一代次仍有未完成的清理责任（ledger 侧 Pending）。
                CapturePolicyCleanupLedger ledger = CreateLedger(generations);
                Assert.True(ledger.Add(new WindowCapturePolicyCleanup
                {
                    Generation = generations.Current,
                    WindowHandle = new IntPtr(0x1234),
                    OriginalAffinity = 0,
                    AffinityChangedBySession = true,
                }));
                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;

                Assert.False(host.Start(
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out string? errorCode,
                    out _));
                Assert.Equal("gpu_capture_policy_cleanup_pending", errorCode);

                // 未取得所有权 ⇒ 不得对窗口施加任何 affinity。
                Assert.Empty(adapter.SetHistory);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ================================================================
    // 2. TryReconfigure 的 live 分支（报告 R09 的主战场）
    // ================================================================

    [Fact]
    public void LM073_TryReconfigure开启live时同一代次仍有未完成清理必须被拒绝()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0);
                using Form form = new();
                _ = form.Handle;
                WindowHandleGeneration generations = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter,
                    handleGeneration: generations);

                CapturePolicyCleanupLedger ledger = CreateLedger(generations);
                ledger.Add(new WindowCapturePolicyCleanup
                {
                    Generation = generations.Current,
                    WindowHandle = new IntPtr(0x1234),
                    OriginalAffinity = 0,
                    AffinityChangedBySession = true,
                });
                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;

                Assert.False(host.TryReconfigure(
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: true,
                    horizontalLayout: false,
                    renderingConfig: null,
                    userScale: 1f,
                    out string? errorCode,
                    out _));
                Assert.Equal("gpu_capture_policy_cleanup_pending", errorCode);

                // ★ 关键断言：0x11 **没有**被施加（否则新会话会把残留值当成外部原值）。
                Assert.DoesNotContain(adapter.SetHistory, entry => entry.Affinity == 0x11u);
                Assert.Empty(adapter.SetHistory);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void LM073_释放WDA的分支不受闸门约束()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0);
                using Form form = new();
                _ = form.Handle;
                WindowHandleGeneration generations = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter,
                    handleGeneration: generations);

                CapturePolicyCleanupLedger ledger = CreateLedger(generations);
                ledger.Add(new WindowCapturePolicyCleanup
                {
                    Generation = generations.Current,
                    WindowHandle = new IntPtr(0x1234),
                    OriginalAffinity = 0,
                    AffinityChangedBySession = true,
                });
                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;

                // liveBackgroundRequested = false ⇒ 这是**释放** WDA 的分支 ⇒ 不得被闸门拦下。
                host.TryReconfigure(
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    horizontalLayout: false,
                    renderingConfig: null,
                    userScale: 1f,
                    out string? errorCode,
                    out _);

                Assert.NotEqual("gpu_capture_policy_cleanup_pending", errorCode);
                Assert.NotEqual("gpu_capture_policy_cleanup_unresolved", errorCode);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ================================================================
    // 3. Resume 入口（重新施加 WDA 排除）
    // ================================================================

    [Fact]
    public void LM073_Resume重新施加WDA排除时同一代次仍有未完成清理必须被拒绝()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                RecordingWindowAdapter adapter = new(originalAffinity: 0);
                using Form form = new();
                _ = form.Handle;
                WindowHandleGeneration generations = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: true,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter,
                    handleGeneration: generations);

                CapturePolicyCleanupLedger ledger = CreateLedger(generations);
                ledger.Add(new WindowCapturePolicyCleanup
                {
                    Generation = generations.Current,
                    WindowHandle = new IntPtr(0x1234),
                    OriginalAffinity = 0,
                    AffinityChangedBySession = true,
                });
                host.CapturePolicyCleanupPendingProbe = ledger.HasPendingForGeneration;

                ForceSuspended(host);
                host.Resume();

                Assert.Equal("gpu_capture_policy_cleanup_pending", host.LastErrorCode);
                Assert.DoesNotContain(adapter.SetHistory, entry => entry.Affinity == 0x11u);
                Assert.Empty(adapter.SetHistory);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    // ================================================================
    // 4. NotifyOutputChanged 入口（显示器变化后重新施加 WDA 排除）
    // ================================================================

    [Fact]
    public void LM073_NotifyOutputChanged重新施加WDA排除时同一代次仍有未完成清理必须被拒绝()
    {
        // ★ 为什么用 IL 结构护栏而不是行为测试：生产的 NotifyOutputChanged 既有早退判断
        //   要求 `_renderer != null`（真实 GPU 渲染器，无头测试拿不到），
        //   确定性单测里无法驱动到闸门分支 ⇒ 改为钉住**结构性质**：
        //   "闸门检查位于任何 SetCapturePolicy 施加之前"。
        //   ★ 既有早退顺序（宿主未运行时静默返回、绝不抛异常）不在本护栏范围内，
        //   由生产代码的顺序保证。
        MethodInfo method = typeof(GpuPresentationHostController).GetMethod(
            "NotifyOutputChanged",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        IlInspector.Disassembly body = IlInspector.Disassemble(method!);

        int gateAt = CallOffsetOf(
            body,
            typeof(GpuPresentationHostController),
            "TryBlockCapturePolicyOwnership");
        int applyAt = CallOffsetOf(body, typeof(PresentationSession), "SetCapturePolicy");

        Assert.True(
            gateAt >= 0,
            "GpuPresentationHostController.NotifyOutputChanged 必须查 WDA 取得闸门 —— "
            + "显示器变化后重新施加 WDA 排除也是'取得所有权'，必须与 Start / TryReconfigure / "
            + "Resume 共用同一个闸门（LM-073）。");
        Assert.True(
            applyAt >= 0,
            "GpuPresentationHostController.NotifyOutputChanged 里找不到 SetCapturePolicy —— "
            + "护栏前提被破坏。");
        Assert.True(
            gateAt < applyAt,
            "闸门检查必须位于 SetCapturePolicy 施加**之前** —— "
            + "否则同一 HWND 上会叠加第二个 affinity 所有者（LM-073）。");
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static string TemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Tests",
            "lm073-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>在 STA 线程上执行（WinForms 控件需要 STA）。</summary>
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    /// <summary>
    /// 把宿主置于"已挂起"状态。
    /// <para>★ 生产的 <c>Suspend()</c> 要求 <c>IsActive</c>（需要真实 GPU 渲染器），
    /// 确定性单测里拿不到 ⇒ 直接置位内部标志，只为驱动 <c>Resume</c> 的闸门分支。</para>
    /// </summary>
    private static void ForceSuspended(GpuPresentationHostController host)
    {
        FieldInfo field = typeof(GpuPresentationHostController).GetField(
            "_suspended",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(host, true);
    }

    /// <summary>
    /// 找到"调用了 <paramref name="declaringType"/>.<paramref name="name"/>"的第一条指令偏移；
    /// 找不到返回 -1。
    /// </summary>
    private static int CallOffsetOf(
        IlInspector.Disassembly body,
        Type declaringType,
        string name)
    {
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

            if (called.DeclaringType == declaringType && called.Name == name)
            {
                return instruction.Offset;
            }
        }

        return -1;
    }

    private static CapturePolicyCleanupLedger CreateLedger(WindowHandleGeneration generations)
    {
        return new CapturePolicyCleanupLedger(
            () => generations.Current,
            _ => true,
            _ => new CapturePolicyRestoreResult(true, null, null));
    }

    private sealed class RecordingWindowAdapter : IPresentationWindowAdapter
    {
        private readonly uint _originalAffinity;

        public RecordingWindowAdapter(uint originalAffinity = 0)
        {
            _originalAffinity = originalAffinity;
            CurrentAffinity = originalAffinity;
        }

        public uint CurrentAffinity { get; set; }

        public List<(IntPtr Window, uint Affinity)> SetHistory { get; } = [];

        public bool TryGetDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            affinity = CurrentAffinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }

        public bool TrySetDisplayAffinity(
            IntPtr windowHandle,
            uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            SetHistory.Add((windowHandle, affinity));
            CurrentAffinity = affinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}
