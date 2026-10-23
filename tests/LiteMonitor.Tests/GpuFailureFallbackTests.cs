using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.src.Core;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【R02 · GPU 故障回退对称性护栏】
/// <para><b>缺陷（R02，P1）：</b>GPU 已经成功呈现（flip model）之后发生运行故障时，
/// 回退路径**不**走完整的 legacy 转换流程，只做 <c>StopGpuPresentation(); _form.Invalidate();</c>
/// ⇒ 可能让画面冻结；而"用户手动关闭 GPU"路径**会**走受控 HWND 重建 ⇒ 两条路径不对称。</para>
/// <para><b>本文件守五件事：</b></para>
/// <list type="number">
/// <item>「启动失败」「首帧失败」「运行故障恢复耗尽」三条路径<b>统一调度</b>到 legacy 转换入口，
///       与手动关闭路径<b>共用同一个</b>转换入口（对称性）；</item>
/// <item>转换只在 <b>UI 安全点</b>执行（不得在 OnPaint / WndProc 销毁回调 / GPU 帧绘制栈内递归执行）；</item>
/// <item><b>保留失败原因</b>（不得把失败退化成普通 fallback code）；</item>
/// <item>是否受控重建 HWND 按<b>本代次的呈现历史</b>决定（两条路径共用同一判据）；</item>
/// <item>恢复预算耗尽后<b>不再</b>被普通 Tick 拉起（<c>gpu_presentation_recovery_exhausted</c> 只由用户显式重试清除）。</item>
/// </list>
/// <para>纯逻辑部分不依赖真实桌面 / GPU / DXGI；结构性部分用 IL 扫描生产代码。</para>
/// </summary>
public sealed class GpuFailureFallbackTests
{
    private static readonly RenderGenerations Generations = new(1, 1, 1, 1, 1);

    // ================================================================
    // 1. 统一调度器（纯 CLR）
    // ================================================================

    [Fact]
    public void 调度器_先到先得_后来的请求不顶替最早的失败原因()
    {
        GpuFailureFallbackScheduler scheduler = new();

        Assert.True(scheduler.Request(Report(GpuFailureStage.Startup, "gpu_presentation_start_failed")));
        Assert.False(scheduler.Request(Report(GpuFailureStage.RecoveryExhausted, "gpu_presentation_recovery_exhausted")));

        Assert.Equal(1, scheduler.PendingCount);
        Assert.True(scheduler.HasPending);
        Assert.Equal("gpu_presentation_start_failed", scheduler.Pending!.FailureCode);
        Assert.Equal(1, scheduler.CoalescedCount);
    }

    [Fact]
    public void 调度器_取出一条之后队列为空()
    {
        GpuFailureFallbackScheduler scheduler = new();
        scheduler.Request(Report(GpuFailureStage.FirstFrame, "gpu_presentation_render_failed"));

        Assert.True(scheduler.TryDequeue(out GpuFailureReport report));
        Assert.Equal("gpu_presentation_render_failed", report.FailureCode);
        Assert.False(scheduler.HasPending);
        Assert.Equal(0, scheduler.PendingCount);
        Assert.False(scheduler.TryDequeue(out _));
    }

    [Theory]
    [InlineData(GpuFailureUnsafeContext.Paint)]
    [InlineData(GpuFailureUnsafeContext.HandleDestroy)]
    [InlineData(GpuFailureUnsafeContext.GpuFrameStack)]
    public void 调度器_禁止上下文内不允许执行转换(GpuFailureUnsafeContext context)
    {
        GpuFailureFallbackScheduler scheduler = new();
        Assert.True(scheduler.IsExecutionAllowed);

        using (scheduler.EnterUnsafeContext(context))
        {
            Assert.False(scheduler.IsExecutionAllowed);
            Assert.Equal(context, scheduler.UnsafeContext);
        }

        // 退出禁止上下文后恢复可执行。
        Assert.True(scheduler.IsExecutionAllowed);
        Assert.Equal(GpuFailureUnsafeContext.None, scheduler.UnsafeContext);
    }

    [Fact]
    public void 调度器_执行中不允许重入()
    {
        GpuFailureFallbackScheduler scheduler = new();
        scheduler.BeginExecution();
        try
        {
            Assert.False(scheduler.IsExecutionAllowed);
        }
        finally
        {
            scheduler.EndExecution();
        }

        Assert.True(scheduler.IsExecutionAllowed);
    }

    // ================================================================
    // 2. 协调器（纯 CLR）：失败原因保留 + 期望路由不被改写 + 恢复预算
    // ================================================================

    [Fact]
    public void 故障回退进入停止阶段并保留失败原因()
    {
        RenderingTransitionCoordinator coordinator = CreateCoordinator();

        Assert.True(coordinator.TryBeginFailureFallback(
            "gpu_presentation_render_failed",
            RenderingTransitionTrigger.RuntimeFailure));

        Assert.Equal(RenderingTransitionPhase.Stopping, coordinator.Phase);
        Assert.Equal("gpu_presentation_render_failed", coordinator.State.FailureCode);
        Assert.Equal(RenderingTransitionTrigger.RuntimeFailure, coordinator.State.Trigger);
        Assert.Equal(1, coordinator.State.TransitionId);
        Assert.Equal(1, coordinator.State.LastAppliedRequestVersion);
    }

    [Fact]
    public void 故障回退不得把用户的GPU选择改写为legacy()
    {
        RenderingTransitionCoordinator coordinator = CreateCoordinator();
        coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);
        coordinator.PublishStable(RenderingRoute.GpuLiveBackground);

        coordinator.TryBeginFailureFallback(
            "gpu_presentation_render_failed",
            RenderingTransitionTrigger.RuntimeFailure);

        // ★ 合同 U11："不把用户选择悄悄写回为关闭" ⇒ 期望路由不变。
        Assert.Equal(RenderingRoute.GpuLiveBackground, coordinator.ExpectedRoute);
        // 有效路由在步 9 才落到 legacy。
        Assert.NotEqual(RenderingRoute.Legacy, coordinator.EffectiveRoute);
    }

    [Fact]
    public void 呈现根因先到先得不被后来的诊断顶替()
    {
        RenderingTransitionCoordinator coordinator = CreateCoordinator();

        coordinator.RecordPresentationRootCause("gpu_presentation_render_failed");
        coordinator.RecordPresentationRootCause("gpu_capture_policy_restore_failed");

        Assert.Equal("gpu_presentation_render_failed", coordinator.State.FailureCode);
        // 记录根因**不改变**切换阶段（故障上报不是一次切换失败）。
        Assert.Equal(RenderingTransitionPhase.Stable, coordinator.Phase);
    }

    [Fact]
    public void 恢复预算耗尽不顶替已记录的根因但仍关闭自动重启()
    {
        RenderingTransitionCoordinator coordinator = CreateCoordinator();
        coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);
        coordinator.PublishStable(RenderingRoute.GpuLiveBackground);
        Assert.True(coordinator.CanStartGpuPresentation);

        coordinator.RecordPresentationRootCause("gpu_presentation_render_failed");
        coordinator.MarkGpuRecoveryExhausted();

        // ★ 根因（为什么失败）与预算状态（为什么不再自动重试）是两件事，必须同时可见。
        Assert.Equal("gpu_presentation_render_failed", coordinator.State.FailureCode);
        Assert.Contains(
            "gpu_presentation_recovery_exhausted",
            coordinator.NonFatalDiagnostics);
        Assert.True(coordinator.GpuRecoveryExhausted);
        Assert.False(coordinator.CanStartGpuPresentation);
    }

    [Fact]
    public void 恢复预算耗尽后普通Tick不得再自动拉起GPU()
    {
        RenderingTransitionCoordinator coordinator = CreateCoordinator();
        coordinator.TryBeginTransition(
            CreateGpuRouteRequest(),
            RenderingTransitionTrigger.UserThemeChange,
            out _);
        coordinator.PublishStable(RenderingRoute.GpuLiveBackground);

        coordinator.MarkGpuRecoveryExhausted();

        // ★ 合同 §8.4 条件 3 / U11：普通 Tick 不得再自动拉起 GPU。
        Assert.False(coordinator.CanStartGpuPresentation);

        // 只有用户显式重试才清除。
        coordinator.ClearGpuRecoveryExhausted();
        Assert.True(coordinator.CanStartGpuPresentation);
    }

    // ================================================================
    // 3. 重建判据：按本代次的呈现历史决定（两条路径共用）
    // ================================================================

    [Theory]
    [InlineData(PresentationBindingState.Unbound, false)]
    [InlineData(PresentationBindingState.Bound, true)]
    [InlineData(PresentationBindingState.PresentAttempted, true)]
    [InlineData(PresentationBindingState.Presented, true)]
    public void 重建判据按本代次呈现历史决定(
        PresentationBindingState bindingState,
        bool expected)
    {
        RenderingTransitionCoordinator coordinator = CreateCoordinator();
        coordinator.RecordBindingState(bindingState);

        Assert.Equal(expected, coordinator.RequiresControlledRecreate());
    }

    [Fact]
    public void 对称性_两条路径对同一份呈现历史给出相同的重建决策()
    {
        foreach (PresentationBindingState state in Enum.GetValues<PresentationBindingState>())
        {
            RenderingTransitionCoordinator manual = CreateCoordinator();
            RenderingTransitionCoordinator failure = CreateCoordinator();

            manual.RecordBindingState(state);
            failure.RecordBindingState(state);

            // 手动关闭 GPU（合同 §4 步 1：Decide）与故障回退（合同 §4.4）两条入口。
            manual.TryBeginTransition(
                CreateGpuRouteRequest(),
                RenderingTransitionTrigger.UserThemeChange,
                out _);
            failure.TryBeginFailureFallback(
                "gpu_presentation_render_failed",
                RenderingTransitionTrigger.RuntimeFailure);

            Assert.Equal(
                manual.RequiresControlledRecreate(),
                failure.RequiresControlledRecreate());
        }
    }

    // ================================================================
    // 4. 宿主：失败原因保留 + 故障上报
    // ================================================================

    [Fact]
    public void 宿主首次失败原因不被随后的清理诊断顶替()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                FailingRestoreAdapter adapter = new();
                using Form form = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: adapter);

                PresentationSession session = new(adapter);
                Assert.True(session.Start(
                    new IntPtr(42),
                    Generations,
                    WindowCapturePolicy.WdaExcludeFromCapture,
                    out _,
                    out _));
                host.SessionOwner.Bind(session);

                host.OnRenderFailure(new InvalidOperationException("device removed"));

                // 根因 = 呈现失败。
                Assert.Equal("gpu_presentation_render_failed", host.FailureReasonCode);

                host.Dispose();

                // ★ 停止流程里的 affinity 恢复失败会改写 LastErrorCode，
                //   但**不得**顶替呈现根因（合同 §5.3 规则 2 / U11）。
                Assert.Equal("gpu_presentation_render_failed", host.FailureReasonCode);
                Assert.NotEqual(
                    host.FailureReasonCode,
                    host.LastErrorCode);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void 宿主运行故障上报阶段与本代次呈现历史()
    {
        RunSta(() =>
        {
            string directory = TemporaryDirectory();
            try
            {
                using Form form = new();
                using GpuPresentationHostController host = new(
                    form,
                    MaterialKind.LiquidGlass,
                    liveBackgroundRequested: false,
                    startupGuard: new GpuStartupGuard(directory, @"C:\apps\A"),
                    windowAdapter: new FailingRestoreAdapter());

                List<GpuFailureReport> reports = [];
                host.PresentationFailed += report => reports.Add(report);

                host.OnRenderFailure(new InvalidOperationException("device removed"));

                Assert.Single(reports);
                Assert.Equal(GpuFailureStage.FirstFrame, reports[0].Stage);
                Assert.Equal("gpu_presentation_render_failed", reports[0].FailureCode);
                Assert.Equal(PresentationBindingState.Unbound, reports[0].BindingState);
                Assert.Equal(FirstFrameOutcome.NotAttempted, reports[0].FirstFrameOutcome);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void 宿主不得依赖UI控制器_故障只能通过通知出口上报()
    {
        // ★ 结构性护栏：宿主**不认识** UIController ⇒ 它不可能在 GPU 帧绘制栈内
        //   直接启动 legacy 转换（计划 §4.3 :169）。
        foreach (MethodInfo method in AllMethods(typeof(GpuPresentationHostController)))
        {
            IlInspector.ScanResult scan = IlInspector.Scan(method);
            foreach (MethodBase call in scan.Calls)
            {
                Assert.NotEqual(
                    typeof(UIController),
                    call.DeclaringType);
            }
        }
    }

    // ================================================================
    // 5. IL 结构护栏：对称性 / UI 安全点 / 失败原因保留
    // ================================================================

    [Fact]
    public void 护栏_Tick的GPU重启兜底必须走协调器的启动守卫()
    {
        MethodInfo body = ResolveAsyncBody(typeof(UIController), "Tick");
        Assert.NotNull(body);

        Assert.True(
            CallsOn(body, "get_CanStartGpuPresentation", typeof(RenderingTransitionCoordinator)),
            "Tick 的 GPU 重启兜底必须走协调器的 CanStartGpuPresentation 守卫 —— " +
            "恢复预算耗尽（gpu_presentation_recovery_exhausted）之后，" +
            "普通 Tick 不得再把 GPU 拉起来（合同 §8.4 条件 3 / U11）。");
    }

    [Fact]
    public void 护栏_故障回退必须把失败原因带进legacy决策()
    {
        MethodInfo method = RequireMethod(
            typeof(UIController),
            "RunGpuFailureFallbackTransition",
            typeof(GpuFailureReport));

        Assert.True(
            CallsOn(method, "get_FailureCode", typeof(GpuFailureReport)),
            "故障回退必须把失败原因带进 legacy 决策（FallbackCode）—— " +
            "把它换成普通 fallback code 就是 R02 缺陷的退化形态（合同 §5.3 规则 2 / U11）。");
    }

    [Fact]
    public void 护栏_故障调度入口必须记录本代次的呈现历史()
    {
        MethodInfo method = RequireMethod(
            typeof(UIController),
            "ScheduleGpuFailureFallback",
            typeof(GpuFailureReport));

        Assert.True(
            CallsOn(method, "RecordBindingState", typeof(RenderingTransitionCoordinator)),
            "故障调度入口必须把**本代次**的 HWND 呈现历史写进协调器 —— " +
            "是否受控重建 HWND 的判据只读这一份历史（计划 §4.1 :123-126）。");
    }

    [Fact]
    public void 护栏_故障调度入口不得做任何窗口操作()
    {
        MethodInfo method = RequireMethod(
            typeof(UIController),
            "ScheduleGpuFailureFallback",
            typeof(GpuFailureReport));

        string[] forbidden =
        [
            "RunGpuToLegacyTransition",
            "RunGpuFailureFallbackTransition",
            "PumpGpuFailureFallback",
            "StopGpuPresentation",
            "StartGpuPresentation",
            "EnsureGpuPresentationStarted",
            "ApplyGpuRenderingRoute",
            "NotifyGpuHandleChanged",
            "TryRecreateHandleForTransition",
            "ApplyWindowAppearance",
            "RefreshTopMost",
            "SetClickThrough",
            "Invalidate",
            "Update",
            "RecreateHandle",
            "Refresh",
            "CreateGraphics",
            "Show",
            "Hide",
        ];

        foreach (MethodBase call in IlInspector.Scan(method).Calls)
        {
            Assert.DoesNotContain(
                call.Name,
                forbidden);
        }
    }

    [Fact]
    public void 护栏_故障调度入口必须保留失败原因()
    {
        MethodInfo method = RequireMethod(
            typeof(UIController),
            "ScheduleGpuFailureFallback",
            typeof(GpuFailureReport));

        Assert.True(
            Calls(method, "RecordPresentationRootCause"),
            "故障调度入口必须把失败原因交给协调器（合同 §5.3 规则 2 / U11）—— " +
            "只记录到 fallback code 里会在后续诊断中被顶替。");
    }

    [Fact]
    public void 护栏_故障转换只在UI安全点执行_泵只被Tick调用()
    {
        IReadOnlyList<string> callers = CallersOf(typeof(UIController), "PumpGpuFailureFallback");

        Assert.True(
            callers.Count > 0,
            "找不到任何 PumpGpuFailureFallback 的调用点 —— 护栏前提被破坏。");
        Assert.All(
            callers,
            caller => Assert.Equal("Tick", caller));
    }

    [Fact]
    public void 护栏_Tick必须先泵回退再重启GPU()
    {
        MethodInfo body = ResolveAsyncBody(typeof(UIController), "Tick");
        Assert.NotNull(body);

        int pumpOffset = FindCallOffset(body, "PumpGpuFailureFallback");
        int restartOffset = FindCallOffset(body, "StartGpuPresentation");

        Assert.True(pumpOffset >= 0, "Tick 里找不到 PumpGpuFailureFallback 的调用。");
        Assert.True(restartOffset >= 0, "Tick 里找不到 StartGpuPresentation 的调用。");
        Assert.True(
            pumpOffset < restartOffset,
            "Tick 必须**先**执行排队的故障回退，再考虑 GPU 重启兜底 —— " +
            "否则一次启动失败刚被排进队列，同一个 Tick 里的重启兜底就会先把 GPU 再拉起来，" +
            "回退永远轮不到。");
    }

    [Fact]
    public void 护栏_故障回退与手动关闭共用同一个legacy转换入口()
    {
        // 手动关闭 GPU 的路径。
        MethodInfo manual = RequireMethod(
            typeof(UIController),
            "ApplyGpuRenderingRoute",
            typeof(Theme));
        Assert.True(
            Calls(manual, "RunGpuToLegacyTransition"),
            "手动关闭 GPU 的路径必须走 RunGpuToLegacyTransition。");

        // 故障回退的路径。
        MethodInfo failure = RequireMethod(
            typeof(UIController),
            "RunGpuFailureFallbackTransition",
            typeof(GpuFailureReport));
        Assert.True(
            Calls(failure, "RunGpuToLegacyTransition"),
            "故障回退的路径必须走**同一个** RunGpuToLegacyTransition —— " +
            "两条路径不对称正是 R02 缺陷本身。");
    }

    [Fact]
    public void 护栏_启动失败不得只做Invalidate()
    {
        MethodInfo method = RequireMethod(typeof(UIController), "StartGpuPresentation");

        Assert.True(
            Calls(method, "ScheduleGpuFailureFallback"),
            "启动失败必须进入统一调度入口（ScheduleGpuFailureFallback）—— " +
            "只做 Invalidate 不会走完整的 legacy 转换流程，也不按呈现历史决定是否重建 HWND。");
        Assert.False(
            Calls(method, "Invalidate"),
            "启动失败**不得**再只补一次 Invalidate（R02 缺陷的原始写法）。");
    }

    [Fact]
    public void 护栏_重建判据两条路径共用协调器()
    {
        MethodInfo method = RequireMethod(typeof(UIController), "RequiresControlledRecreate");

        Assert.True(
            Calls(method, "RequiresControlledRecreate"),
            "UIController 的重建判据必须转发给协调器 —— " +
            "两条路径各写一份判据必然漂移（R02 的对称性要求）。");
    }

    [Fact]
    public void 护栏_宿主故障通知不得直接触发转换()
    {
        MethodInfo method = RequireMethod(
            typeof(GpuPresentationHostController),
            "OnRenderFailure",
            typeof(Exception));

        Assert.True(
            Calls(method, "RaisePresentationFailed"),
            "宿主的故障路径必须经由通知出口上报。");
        foreach (string forbidden in new[] { "Invalidate", "RecreateHandle", "Update" })
        {
            Assert.False(
                Calls(method, forbidden),
                "宿主的故障路径**不得**自己做窗口操作 —— " +
                "它可能在 GPU 帧绘制栈内被调用（计划 §4.3 :169）。");
        }
    }

    // ================================================================
    // 辅助
    // ================================================================

    private static GpuFailureReport Report(GpuFailureStage stage, string code) =>
        new(stage, code, code + "_message", PresentationBindingState.Unbound, FirstFrameOutcome.NotAttempted);

    private static RenderingTransitionCoordinator CreateCoordinator() =>
        new(new WindowHandleGeneration(), TimeProvider.System);

    private static RenderingRouteRequest CreateGpuRouteRequest() =>
        new(
            new ThemeRenderingConfig
            {
                Backend = ThemeRenderingConfig.GpuBackend,
                Material = ThemeRenderingConfig.LiquidGlassMaterial,
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["liveBackground"] = JsonSerializer.SerializeToElement(true),
                    ["cornerRadius"] = JsonSerializer.SerializeToElement(24),
                    ["primaryCut"] = JsonSerializer.SerializeToElement(14)
                }
            },
            GpuEffectsEnabled: true,
            LiveBackgroundEnabled: true,
            WdaCapturePolicyAccepted: true,
            SafeMode: false);

    private static MethodInfo RequireMethod(Type owner, string name, params Type[] parameterTypes)
    {
        MethodInfo method = owner.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            parameterTypes,
            null);
        Assert.True(method != null, $"{owner.Name}.{name}({string.Join(", ", parameterTypes.Select(t => t.Name))}) 不存在 —— 护栏前提被破坏。");
        return method!;
    }

    private static bool Calls(MethodInfo method, string name) =>
        IlInspector.Scan(method).Calls.Any(call => call.Name == name);

    private static bool CallsOn(MethodInfo method, string name, Type declaringType) =>
        IlInspector.Scan(method).Calls.Any(call =>
            call.Name == name && call.DeclaringType == declaringType);

    private static int FindCallOffset(MethodInfo method, string name)
    {
        foreach (IlInspector.IlInstruction instruction in IlInspector.Disassemble(method).Instructions)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
            {
                continue;
            }

            if (instruction.Target?.Name == name)
            {
                return instruction.Offset;
            }
        }

        return -1;
    }

    /// <summary>
    /// 找出调用了指定方法的**所有**位置（async 方法的真实体在编译器生成的嵌套状态机里，
    /// 这里把嵌套类型名还原成原始方法名）。
    /// </summary>
    private static IReadOnlyList<string> CallersOf(Type owner, string targetName)
    {
        List<string> callers = [];
        foreach ((string label, MethodInfo method) in AllLabelledMethods(owner))
        {
            if (Calls(method, targetName))
            {
                callers.Add(label);
            }
        }

        return callers;
    }

    private static IEnumerable<(string Label, MethodInfo Method)> AllLabelledMethods(Type owner)
    {
        foreach (MethodInfo method in AllMethods(owner))
        {
            yield return (method.Name, method);
        }

        foreach (Type nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            string label = nested.Name;
            int start = label.IndexOf('<');
            int end = label.IndexOf(">d__", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                label = label.Substring(start + 1, end - start - 1);
            }

            foreach (MethodInfo method in nested.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
            {
                yield return (label, method);
            }
        }
    }

    private static IEnumerable<MethodInfo> AllMethods(Type owner) =>
        owner.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly);

    /// <summary>取 async 方法的真实方法体（编译器生成的嵌套状态机的 MoveNext）。</summary>
    private static MethodInfo ResolveAsyncBody(Type owner, string methodName)
    {
        foreach (Type nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!nested.Name.StartsWith("<" + methodName + ">d__", StringComparison.Ordinal))
            {
                continue;
            }

            MethodInfo moveNext = nested.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext != null)
            {
                return moveNext;
            }
        }

        return null;
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitorGpuFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>在 STA 线程上执行（WinForms 控件需要 STA）。</summary>
    private static void RunSta(Action action)
    {
        Exception captured = null;
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

    /// <summary>affinity **恢复**写入必定失败的适配器（制造清理诊断）。</summary>
    private sealed class FailingRestoreAdapter : IPresentationWindowAdapter
    {
        /// <summary>原始 affinity（恢复动作写回的就是它）。</summary>
        private const uint OriginalAffinity = 0;

        public bool TryGetDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity,
            out string errorCode,
            out string errorMessage)
        {
            affinity = OriginalAffinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }

        public bool TrySetDisplayAffinity(
            IntPtr windowHandle,
            uint affinity,
            out string errorCode,
            out string errorMessage)
        {
            // 只让**恢复**写入失败（恢复写回的是原始 affinity），
            // 初始的 WDA 排除写入必须成功，否则会话根本起不来。
            if (affinity == OriginalAffinity)
            {
                errorCode = "presentation_capture_policy_failed";
                errorMessage = "injected affinity restore failure";
                return false;
            }

            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}
