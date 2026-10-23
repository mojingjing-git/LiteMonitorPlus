using System;
using System.Reflection;
using System.Reflection.Emit;
using GpuHostLifecycleProbe;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-087 · 真实 MainForm 隔离门护栏】
/// <para><b>缺陷：</b><c>tools/GpuHostLifecycleProbe/RealMainFormScenario.cs</c> 的隔离门
/// 只检查"此刻有没有网页服务在监听"，而生产 <c>MainForm.OnShown</c>
/// （<c>src/UI/MainForm_Transparent.cs</c>：<c>if (_cfg.WebServerEnabled)</c>）
/// 会在窗口**首次显示**时启动网页服务 ⇒ 门通过之后服务照样开起来 ⇒
/// "隔离"承诺**没有被约束**（可能真的开局域网监听）。</para>
/// <para><b>本护栏守三件事：</b></para>
/// <list type="number">
/// <item>配置里开着网页服务 ⇒ 隔离门**必须拒绝**（不能只看"此刻有没有在监听"）；</item>
/// <item>隔离门**必须在消息循环（Application.Run）之前**求值 —— 否则拒绝得太晚；</item>
/// <item>运行结束后**必须复查**网页服务确实没有开起来。</item>
/// </list>
/// </summary>
public sealed class BatchEProbeIsolationGateTests
{
    // ---------------------------------------------------------------- 语义

    [Fact]
    public void 护栏_配置开启网页服务时隔离门必须拒绝()
    {
        IsolationGateResult result = IsolationGate.EvaluateBeforeRun(
            trayIconVisible: false,
            webServerRunning: false,
            webServerEnabledInConfig: true);

        Assert.False(
            result.Passed,
            "配置里开着网页服务 ⇒ 生产 OnShown 一定会开监听 ⇒ 隔离门必须拒绝。" +
            "只检查'此刻有没有在监听'就是 LM-087 的假绿：门通过，服务照开。");
        Assert.False(result.WebServerDisabledInConfig);
        Assert.Contains(
            "web server",
            result.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 护栏_隔离条件全部满足时隔离门必须放行()
    {
        IsolationGateResult result = IsolationGate.EvaluateBeforeRun(
            trayIconVisible: false,
            webServerRunning: false,
            webServerEnabledInConfig: false);

        Assert.True(
            result.Passed,
            "托盘隐藏 + 没有在监听 + 配置关闭网页服务 ⇒ 隔离门必须放行，否则这条 arm 永远跑不成。" +
            "实际原因：" + result.Reason);
        Assert.True(result.WebServerDisabledInConfig);
        Assert.True(result.WebServerNotListeningBefore);
        Assert.True(result.TrayIconHidden);
    }

    [Fact]
    public void 护栏_读不到配置时必须按会启动处理()
    {
        Assert.Null(RealMainFormScenario.ReadWebServerEnabledFrom(null));
        Assert.True(
            RealMainFormScenario.ResolveWebServerEnabled(null),
            "读不到配置时必须按'会启动网页服务'处理（保守）：宁可中止这条 arm，" +
            "也不许真的开一个局域网监听（LM-087）。");
        Assert.False(RealMainFormScenario.ResolveWebServerEnabled(false));
        Assert.True(RealMainFormScenario.ResolveWebServerEnabled(true));
    }

    // ---------------------------------------------------------------- 顺序

    [Fact]
    public void 护栏_隔离门必须在消息循环之前求值()
    {
        IlInspector.Disassembly body = IlInspector.Disassemble(RunMethod);

        int gateOffset = -1;
        int applicationRunOffset = -1;
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

            if (called.DeclaringType == typeof(IsolationGate) && called.Name == nameof(IsolationGate.EvaluateBeforeRun))
            {
                gateOffset = gateOffset < 0 ? instruction.Offset : Math.Min(gateOffset, instruction.Offset);
            }
            else if (called.DeclaringType == typeof(Application) && called.Name == "Run")
            {
                applicationRunOffset = applicationRunOffset < 0 ? instruction.Offset : Math.Min(applicationRunOffset, instruction.Offset);
            }
        }

        Assert.True(
            gateOffset >= 0,
            "RealMainFormScenario.Run 里找不到 IsolationGate.EvaluateBeforeRun 的调用 —— 护栏前提被破坏。");
        Assert.True(
            applicationRunOffset >= 0,
            "RealMainFormScenario.Run 里找不到 Application.Run 的调用 —— 护栏前提被破坏。");
        Assert.True(
            gateOffset < applicationRunOffset,
            "隔离门必须在 Application.Run（消息循环）**之前**求值：IL 偏移 " + gateOffset +
            " 的隔离门判定晚于偏移 " + applicationRunOffset + " 的 Application.Run —— " +
            "生产 OnShown 已经跑完、网页服务已经开起来了，拒绝就太晚了（LM-087）。");
    }

    // ---------------------------------------------------------------- 复查

    [Fact]
    public void 护栏_运行结束后必须复查网页服务确实没有开起来()
    {
        IsolationGateResult before = IsolationGate.EvaluateBeforeRun(
            trayIconVisible: false,
            webServerRunning: false,
            webServerEnabledInConfig: false);
        Assert.True(before.Passed);

        IsolationGateResult leaked = IsolationGate.EvaluateAfterRun(before, webServerRunningAfter: true);
        Assert.False(
            leaked.Passed,
            "运行结束时网页服务仍在监听 ⇒ 隔离承诺被打破，必须报出来。");
        Assert.False(leaked.WebServerNotListeningAfter);

        IsolationGateResult held = IsolationGate.EvaluateAfterRun(before, webServerRunningAfter: false);
        Assert.True(
            held.Passed,
            "网页服务全程没有开起来 ⇒ 隔离承诺成立。实际原因：" + held.Reason);
        Assert.True(held.WebServerNotListeningAfter);
    }

    // ---------------------------------------------------------------- 辅助

    private static MethodInfo RunMethod
    {
        get
        {
            MethodInfo? method = typeof(RealMainFormScenario).GetMethod(
                "Run",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(int), typeof(CandidateHashGuard) },
                null);
            return method ?? throw new InvalidOperationException(
                "RealMainFormScenario.Run(string, int, CandidateHashGuard) 不存在 —— 护栏前提被破坏。");
        }
    }
}
