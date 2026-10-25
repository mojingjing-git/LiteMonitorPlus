using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// ★ **独立复核 · LM-066 / R02 · GPU 故障回退与手动关闭路径不对称**
/// <para>复核者自写用例。判据与实现者的 <c>GpuFailureFallbackTests</c> **不同**：
/// 实现者主要测调度器与协调器的纯函数行为；本文件额外用**自写 IL 读取器**独立判定
/// "三条故障路径与手动关闭路径是否真的汇进同一个 legacy 转换入口"、
/// "调度入口本身是否做了窗口操作"、"重建判据是否真的只有一份"。</para>
/// </summary>
public sealed class VerifierLm066Tests
{
    private static RenderingTransitionCoordinator NewCoordinator() =>
        new(WindowHandleGeneration.Instance);

    private static bool IsSame(MethodBase? a, MethodBase? b) =>
        a != null && b != null &&
        a.DeclaringType == b.DeclaringType &&
        a.MetadataToken == b.MetadataToken;

    private static IEnumerable<MethodInfo> UiMethods() =>
        typeof(UIController).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    private static MethodInfo UiMethod(string name) =>
        UiMethods().FirstOrDefault(m => m.Name == name)
        ?? throw new InvalidOperationException("UIController." + name + " 不存在 —— 用例前提被破坏。");

    private static bool CallsMethod(MethodBase method, MethodBase target) =>
        VerifierIl.Decode(VerifierIl.EffectiveBody(method)).Any(i => i.IsCallOrCallVirt && IsSame(i.Method, target));

    // ---------------------------------------------------------------- V1

    /// <summary>V1：调度器**先到先得** —— 后到的故障报告不得顶替先到的根因。</summary>
    [Fact]
    public void V1_调度器先到先得_后来的根因不得顶替先到的()
    {
        var scheduler = new GpuFailureFallbackScheduler();

        var first = new GpuFailureReport(
            GpuFailureStage.Startup, "gpu_first_root_cause", "first",
            PresentationBindingState.Bound, FirstFrameOutcome.NotAttempted);
        var second = new GpuFailureReport(
            GpuFailureStage.RecoveryExhausted, "gpu_second_root_cause", "second",
            PresentationBindingState.Presented, FirstFrameOutcome.Succeeded);

        Assert.True(scheduler.Request(first), "第一条请求必须被接受。");
        Assert.False(scheduler.Request(second), "已有待执行请求时，后到的请求必须被合并（返回 false）。");
        Assert.Equal(1, scheduler.CoalescedCount);
        Assert.True(scheduler.HasPending);
        Assert.NotNull(scheduler.Pending);
        Assert.Equal("gpu_first_root_cause", scheduler.Pending!.FailureCode);
        Assert.Equal(1, scheduler.PendingCount);
    }

    // ---------------------------------------------------------------- V2

    /// <summary>V2：禁止上下文（OnPaint / 销毁回调 / GPU 帧栈）内**不得**执行转换。</summary>
    [Theory]
    [InlineData(GpuFailureUnsafeContext.Paint)]
    [InlineData(GpuFailureUnsafeContext.HandleDestroy)]
    [InlineData(GpuFailureUnsafeContext.GpuFrameStack)]
    public void V2_禁止上下文内不得执行转换(GpuFailureUnsafeContext context)
    {
        var scheduler = new GpuFailureFallbackScheduler();
        Assert.True(scheduler.IsExecutionAllowed, "默认（UI 安全点）必须允许执行。");

        using (scheduler.EnterUnsafeContext(context))
        {
            Assert.False(scheduler.IsExecutionAllowed, context + " 上下文内不得执行 legacy 转换。");
            Assert.Equal(context, scheduler.UnsafeContext);
        }

        Assert.True(scheduler.IsExecutionAllowed, "退出禁止上下文后必须恢复可执行。");
    }

    // ---------------------------------------------------------------- V3

    /// <summary>V3：重入保护 —— 转换执行期间不得再执行一次。</summary>
    [Fact]
    public void V3_转换执行期间有重入保护()
    {
        var scheduler = new GpuFailureFallbackScheduler();
        Assert.True(scheduler.IsExecutionAllowed);
        scheduler.BeginExecution();
        Assert.False(scheduler.IsExecutionAllowed, "转换执行期间不得再执行一次。");
        scheduler.EndExecution();
        Assert.True(scheduler.IsExecutionAllowed);
    }

    // ---------------------------------------------------------------- V4

    /// <summary>
    /// V4（**载荷用例**）：呈现根因**不得**被"恢复预算耗尽"码顶替；
    /// 同时耗尽码必须**另有留痕**（诊断通道），两者同时可见。
    /// </summary>
    [Fact]
    public void V4_恢复耗尽码不得顶替已记录的呈现根因()
    {
        RenderingTransitionCoordinator coordinator = NewCoordinator();

        coordinator.RecordPresentationRootCause("gpu_presentation_render_failed");
        coordinator.MarkGpuRecoveryExhausted();

        Assert.Equal(
            "gpu_presentation_render_failed",
            coordinator.State.FailureCode);

        Assert.True(
            coordinator.NonFatalDiagnostics.Contains(RenderingTransitionCoordinator.RecoveryExhaustedCode),
            "恢复预算耗尽必须另有留痕，否则用户只看到根因、看不到「为什么不再自动重试」。");
    }

    // ---------------------------------------------------------------- V5

    /// <summary>
    /// V5（**载荷用例**）：进入故障回退时，失败原因必须被**带进** legacy 决策，
    /// 且不得被"普通 fallback code"顶替。
    /// </summary>
    [Fact]
    public void V5_故障回退必须携带失败原因且不被普通码顶替()
    {
        RenderingTransitionCoordinator coordinator = NewCoordinator();

        bool begun = coordinator.TryBeginFailureFallback(
            "gpu_presentation_render_failed", RenderingTransitionTrigger.RuntimeFailure);

        Assert.True(begun, "故障回退必须能进入一次新的切换。");
        Assert.Equal("gpu_presentation_render_failed", coordinator.State.FailureCode);
        Assert.Equal(RenderingTransitionPhase.Stopping, coordinator.State.Phase);

        // 再进一次、这次不给原因 ⇒ 原有的根因必须保留
        coordinator.TryBeginFailureFallback(null, RenderingTransitionTrigger.RuntimeFailure);
        Assert.Equal(
            "gpu_presentation_render_failed",
            coordinator.State.FailureCode);
    }

    // ---------------------------------------------------------------- V6

    /// <summary>V6：受控重建的判据只有**一份**（在协调器里），两条路径共用。</summary>
    [Fact]
    public void V6_受控重建判据由协调器统一提供()
    {
        RenderingTransitionCoordinator coordinator = NewCoordinator();
        Assert.False(coordinator.RequiresControlledRecreate(), "未绑定交换链时不需要受控重建。");

        coordinator.RecordBindingState(PresentationBindingState.Bound);
        Assert.True(coordinator.RequiresControlledRecreate(), "已绑定交换链 ⇒ 必须受控重建。");

        RenderingTransitionCoordinator coordinator2 = NewCoordinator();
        coordinator2.RecordBindingState(PresentationBindingState.PresentAttempted);
        Assert.True(coordinator2.RequiresControlledRecreate(), "呈现尝试中失败 ⇒ 保守重建。");

        RenderingTransitionCoordinator coordinator3 = NewCoordinator();
        coordinator3.RecordBindingState(PresentationBindingState.Presented);
        Assert.True(coordinator3.RequiresControlledRecreate(), "成功呈现过 ⇒ 必须受控重建。");
    }

    // ---------------------------------------------------------------- V7

    /// <summary>
    /// V7（**载荷用例**）：调度入口**本身不做窗口操作** ——
    /// 它可能在 GPU 帧绘制栈内被调用（宿主 <c>PresentationFailed</c> 事件）。
    /// </summary>
    [Fact]
    public void V7_调度入口不做任何窗口操作()
    {
        foreach (string name in new[] { "OnGpuPresentationFailed", "ScheduleGpuFailureFallback" })
        {
            MethodInfo method = UiMethod(name);
            List<VerifierIlInstruction> il = VerifierIl.Decode(method);

            var offenders = il.Where(i =>
                i.IsCallOrCallVirt &&
                i.Method?.DeclaringType != null &&
                typeof(System.Windows.Forms.Control).IsAssignableFrom(i.Method.DeclaringType) &&
                !i.Method.Name.StartsWith("get_", StringComparison.Ordinal))
                .Select(i => i.Method!.DeclaringType!.Name + "." + i.Method.Name)
                .Distinct()
                .ToArray();

            Assert.True(
                offenders.Length == 0,
                "UIController." + name + "() 触碰了 WinForms 控件成员：" +
                string.Join(", ", offenders) +
                " ⇒ 调度入口做了窗口操作，可能在 GPU 帧绘制栈 / OnPaint / 销毁回调内执行（R02 合同 §4）。");
        }
    }

    // ---------------------------------------------------------------- V8

    /// <summary>
    /// V8（**载荷用例**）：故障回退**不得**在宿主回调 / 调度入口里就地执行 ——
    /// 真正的转换必须由 UI 安全点（Tick）执行。
    /// </summary>
    [Fact]
    public void V8_调度入口不就地执行转换_转换由UI安全点执行()
    {
        MethodInfo legacyEntry = UiMethod("RunGpuToLegacyTransition");
        MethodInfo failureEntry = UiMethod("RunGpuFailureFallbackTransition");
        MethodInfo pump = UiMethod("PumpGpuFailureFallback");

        Assert.False(
            CallsMethod(UiMethod("OnGpuPresentationFailed"), legacyEntry),
            "宿主故障回调里不得直接执行 legacy 转换（它可能在 GPU 帧绘制栈内被调用）。");
        Assert.False(
            CallsMethod(UiMethod("ScheduleGpuFailureFallback"), legacyEntry),
            "调度入口里不得就地执行 legacy 转换。");

        Assert.True(
            CallsMethod(failureEntry, legacyEntry),
            "故障转换必须调用与手动关闭路径**同一个** legacy 转换入口。");

        MethodInfo tick = UiMethod("Tick");
        Assert.True(CallsMethod(tick, pump), "UI Tick 必须驱动故障回退泵。");
        Assert.True(CallsMethod(pump, failureEntry), "泵必须执行故障转换。");

        HashSet<MethodBase> reachable = VerifierIl.Reachable(pump, 8);
        Assert.True(
            reachable.Any(m => IsSame(m, legacyEntry)),
            "从 UI 安全点出发必须能到达 legacy 转换入口。");
    }

    // ---------------------------------------------------------------- V9

    /// <summary>
    /// V9（**载荷用例**）：**手动关闭**路径与**故障回退**路径调用的是**同一个**
    /// legacy 转换入口方法（不是两个各自实现的）。
    /// </summary>
    [Fact]
    public void V9_手动关闭路径与故障路径共用同一个legacy转换入口()
    {
        MethodInfo legacyEntry = UiMethod("RunGpuToLegacyTransition");

        var callers = UiMethods()
            .Where(m => CallsMethod(m, legacyEntry))
            .Select(m => m.Name)
            .Distinct()
            .ToArray();

        Assert.True(
            callers.Contains("RunGpuFailureFallbackTransition"),
            "故障路径必须调用 legacy 转换入口；实际调用方 = " + string.Join(", ", callers));

        Assert.True(
            callers.Length >= 2,
            "只找到一条调用 legacy 转换入口的路径（" + string.Join(", ", callers) +
            "）⇒ 手动关闭路径与故障路径**没有**共用同一个入口（R02 的核心断言）。");
    }

    // ---------------------------------------------------------------- V10

    /// <summary>
    /// V10：启动失败路径**必须**把故障交给统一调度入口（而不是只做
    /// <c>StopGpuPresentation()</c> + <c>Invalidate()</c>）。
    /// </summary>
    [Fact]
    public void V10_启动失败路径把故障交给统一调度入口()
    {
        MethodInfo schedulerEntry = UiMethod("ScheduleGpuFailureFallback");

        var startupPaths = UiMethods()
            .Where(m => VerifierIl.Decode(VerifierIl.EffectiveBody(m)).Any(i =>
                i.IsNewObj && i.Constructor?.DeclaringType == typeof(GpuFailureReport)) &&
                CallsMethod(m, schedulerEntry))
            .Select(m => m.Name)
            .ToArray();

        Assert.True(
            startupPaths.Length > 0,
            "没有任何方法构造 GpuFailureReport 后交给统一调度入口 ⇒ 启动失败路径没有接入（R02）。");

        foreach (string name in startupPaths)
        {
            MethodInfo m = UiMethod(name);
            Assert.False(
                CallsMethod(m, UiMethod("RunGpuToLegacyTransition")),
                "启动失败路径 " + name + " 自己调用了 legacy 转换入口 ⇒ 转换不在 UI 安全点执行。");
        }
    }

    // ---------------------------------------------------------------- V11

    /// <summary>
    /// V11：重建判据在 UIController 里**只转发**给协调器 ⇒ 两条路径真的共用一份判据。
    /// </summary>
    [Fact]
    public void V11_UIController的重建判据只转发给协调器()
    {
        MethodInfo uiCriterion = UiMethod("RequiresControlledRecreate");
        MethodInfo coordinatorCriterion = typeof(RenderingTransitionCoordinator).GetMethod(
            "RequiresControlledRecreate",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);

        Assert.True(coordinatorCriterion != null, "协调器上必须有唯一的重建判据 —— 用例前提被破坏。");
        Assert.True(
            CallsMethod(uiCriterion, coordinatorCriterion!),
            "UIController.RequiresControlledRecreate() 必须转发给协调器，" +
            "否则两条路径可能各自持有一份判据（R02 的「共用判据」断言不成立）。");

        MethodInfo legacyEntry = UiMethod("RunGpuToLegacyTransition");
        Assert.True(
            CallsMethod(legacyEntry, uiCriterion) || CallsMethod(legacyEntry, coordinatorCriterion!),
            "legacy 转换入口必须按重建判据决定是否受控重建 HWND。");
    }
}