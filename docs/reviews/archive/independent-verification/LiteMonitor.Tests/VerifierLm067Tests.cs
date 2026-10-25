using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.WebServer;
using Xunit;
using BindMode = LiteMonitor.WebServerBindMode;

namespace LiteMonitor.Tests;

/// <summary>
/// ★ **独立复核 · LM-067 / R03 · LAN 模式运行中清除口令 ⇒ 无凭据监听**
/// <para>复核者自写用例。判据与实现者的 <c>WebPasswordPolicyTests</c> **不同**：
/// 关键差异是本文件用**自写 IL 读取器**独立证明"冷启动"与"运行中变更"调用的是
/// **同一个** <c>WebServerAccessPolicy.IsLanAccessForbidden</c> 方法（而不是两处各写一遍判据），
/// 并且用**反射**在 <c>WebSessionManager</c> 上直接验证会话 / 票据 / WS 计数确实被清零。</para>
/// </summary>
public sealed class VerifierLm067Tests
{
    private static bool CallsMethod(MethodBase method, MethodBase target) =>
        VerifierIl.Decode(method).Any(i =>
            (i.IsCallOrCallVirt || i.IsNewObj) &&
            i.Method != null &&
            i.Method.DeclaringType == target.DeclaringType &&
            i.Method.MetadataToken == target.MetadataToken);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool PortIsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, port);
            return task.Wait(TimeSpan.FromMilliseconds(800)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static Settings LanSettingsWithPassword(int port)
    {
        return new Settings
        {
            WebServerEnabled = true,
            WebServerPort = port,
            WebServerBindMode = BindMode.Lan,
            WebServerPassword = "verifier-secret",
        };
    }

    // ---------------------------------------------------------------- V1

    /// <summary>V1：「局域网暴露 + 无有效凭据」判定的真值表（冷启动与运行中变更共用）。</summary>
    [Theory]
    // LAN + 无凭据 ⇒ 禁止
    [InlineData(BindMode.Lan, false, false, true)]
    // LAN + 有凭据 ⇒ 允许
    [InlineData(BindMode.Lan, false, true, false)]
    // 仅本机 + 无凭据 + 未对局域网暴露 ⇒ 允许
    [InlineData(BindMode.Loopback, false, false, false)]
    // 仅本机 + 无凭据 + **运行时仍绑在非回环地址上** ⇒ 禁止
    [InlineData(BindMode.Loopback, true, false, true)]
    // 仅本机 + **有**凭据 + 运行时已对局域网暴露 ⇒ 允许
    [InlineData(BindMode.Loopback, true, true, false)]
    public void V1_局域网访问策略真值表(
        BindMode mode, bool runtimeLanExposed, bool hasPassword, bool expectedForbidden)
    {
        var cfg = new Settings { WebServerBindMode = mode };
        if (hasPassword)
        {
            cfg.WebServerPassword = "verifier-secret";
            cfg.WebServerPasswordSalt = "c2FsdHNhbHRzYWx0c2FsdA==";
        }

        bool actual = WebServerAccessPolicy.IsLanAccessForbidden(cfg, runtimeLanExposed);

        Assert.Equal(expectedForbidden, actual);
    }

    [Fact]
    public void V1b_没有配置对象时不得拒绝()
    {
        Assert.False(WebServerAccessPolicy.IsLanAccessForbidden(null, true));
        Assert.False(WebServerAccessPolicy.IsLanAccessForbidden(null, false));
    }

    // ---------------------------------------------------------------- V2

    /// <summary>
    /// V2（**载荷用例**）：冷启动（<c>LiteWebServer.Start</c>）与运行中变更
    /// （<c>AppActions.ApplyWebServerWithResult</c>）调用的是**同一个**判定方法。
    /// </summary>
    [Fact]
    public void V2_冷启动与运行中变更共用同一条判定()
    {
        MethodInfo policy = typeof(WebServerAccessPolicy).GetMethod(
            "IsLanAccessForbidden",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Settings), typeof(bool) }, null);

        Assert.True(policy != null, "WebServerAccessPolicy.IsLanAccessForbidden 不存在 —— 用例前提被破坏。");

        MethodInfo start = typeof(LiteWebServer).GetMethod(
            "Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(string).MakeByRefType() }, null);
        Assert.True(start != null, "LiteWebServer.Start 不存在 —— 用例前提被破坏。");

        MethodInfo apply = typeof(AppActions).GetMethod(
            "ApplyWebServerWithResult",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Settings) }, null);
        Assert.True(apply != null, "AppActions.ApplyWebServerWithResult 不存在 —— 用例前提被破坏。");

        Assert.True(
            CallsMethod(start!, policy!),
            "LiteWebServer.Start 必须调用 WebServerAccessPolicy.IsLanAccessForbidden（冷启动判定）。");
        Assert.True(
            CallsMethod(apply!, policy!),
            "AppActions.ApplyWebServerWithResult 必须调用 WebServerAccessPolicy.IsLanAccessForbidden" +
            "（运行中变更判定）⇒ 两条路径才是同一条约束。");
    }

    // ---------------------------------------------------------------- V3

    /// <summary>V3：有效凭据指纹 —— 无凭据时为空串；凭据变化时必须变。</summary>
    [Fact]
    public void V3_凭据指纹随凭据变化且无凭据时为空()
    {
        var empty = new Settings();
        Assert.Equal("", empty.GetWebPasswordFingerprint());
        Assert.False(empty.HasWebPasswordConfigured());

        var withPassword = new Settings { WebServerPassword = "verifier-secret" };
        string fp1 = withPassword.GetWebPasswordFingerprint();
        Assert.NotEqual("", fp1);
        Assert.True(withPassword.HasWebPasswordConfigured());

        // 只改盐 ⇒ 哈希变 ⇒ 指纹变
        var otherSalt = new Settings
        {
            WebServerPassword = "verifier-secret",
            WebServerPasswordSalt = "YW5vdGhlcnNhbHRzYWx0c2E=",
        };
        Assert.NotEqual(fp1, otherSalt.GetWebPasswordFingerprint());

        // 清掉凭据 ⇒ 指纹回到空串
        var cleared = new Settings
        {
            WebServerPassword = "",
            WebServerPasswordHash = "",
            WebServerPasswordSalt = "",
        };
        Assert.Equal("", cleared.GetWebPasswordFingerprint());
        Assert.False(cleared.HasWebPasswordConfigured());
    }

    // ---------------------------------------------------------------- V4

    /// <summary>
    /// V4（**载荷用例**）：冷启动 —— LAN 模式 + 无有效凭据 ⇒ **拒绝启动**。
    /// </summary>
    [Fact]
    public void V4_冷启动LAN无凭据必须拒绝监听()
    {
        var cfg = new Settings
        {
            WebServerEnabled = true,
            WebServerPort = FreePort(),
            WebServerBindMode = BindMode.Lan,
            WebServerPassword = "",
        };

        using var server = new LiteWebServer(cfg);
        bool started = server.Start(out string errorMsg);

        Assert.False(started, "LAN 模式 + 无有效凭据 ⇒ 必须拒绝启动。");
        Assert.False(server.IsRunning);
        Assert.True(errorMsg.Contains("口令", StringComparison.Ordinal), "拒绝原因必须点明缺少口令：" + errorMsg);
    }

    // ---------------------------------------------------------------- V5

    /// <summary>
    /// V5（**载荷用例**）：运行中清除口令 ⇒ 监听必须**停止**，并给出确定结果。
    /// </summary>
    [Fact]
    public void V5_运行中清除口令必须停止监听()
    {
        int port = FreePort();
        Settings cfg = LanSettingsWithPassword(port);

        LiteWebServer? server = null;
        try
        {
            server = new LiteWebServer(cfg) { ListenAddress = IPAddress.Any };
            Assert.True(server.Start(out string startErr), "前置条件：带口令的 LAN 服务应当能启动 —— " + startErr);
            Assert.True(server.IsRunning);
            Assert.True(server.IsLanExposed, "前置条件：此时应当确实对局域网暴露。");
            Assert.True(PortIsListening(port), "前置条件：端口上应当确实有人在监听。");

            // ★ 运行中把口令清掉（模拟"清除访问口令 + 应用"）
            cfg.WebServerPassword = "";
            cfg.WebServerPasswordHash = "";
            cfg.WebServerPasswordSalt = "";

            WebServerApplyResult result = AppActions.ApplyWebServerWithResult(cfg);

            Assert.True(
                result.RejectedLanWithoutPassword,
                "「局域网暴露 + 无有效凭据」必须被拒绝并给出确定结果。");
            Assert.False(result.ServiceRunning, "服务必须已经停止。");
            Assert.False(server.IsRunning);
            Assert.False(PortIsListening(port), "端口上不得还有人在监听（R03 的核心断言）。");
        }
        finally
        {
            server?.Dispose();
            ResetServerInstance();
        }
    }

    // ---------------------------------------------------------------- V6

    /// <summary>V6：凭据被**换掉** ⇒ 服务必须重启。</summary>
    [Fact]
    public void V6_凭据被换掉必须重启服务()
    {
        int port = FreePort();
        Settings cfg = LanSettingsWithPassword(port);

        LiteWebServer? server = null;
        try
        {
            server = new LiteWebServer(cfg) { ListenAddress = IPAddress.Any };
            Assert.True(server.Start(out string startErr), "前置条件 —— " + startErr);

            string fingerprintBefore = server.CurrentCredentialFingerprint;
            Assert.NotEqual("", fingerprintBefore);

            // 换口令（换盐 ⇒ 指纹变）
            cfg.WebServerPassword = "another-secret";
            cfg.WebServerPasswordSalt = "YW5vdGhlcnNhbHRzYWx0c2E=";

            WebServerApplyResult result = AppActions.ApplyWebServerWithResult(cfg);

            Assert.True(result.Restarted, "凭据被换掉 ⇒ 必须重启服务。");
            Assert.True(result.ServiceRunning);
            Assert.NotEqual(fingerprintBefore, server.CurrentCredentialFingerprint);
        }
        finally
        {
            server?.Dispose();
            ResetServerInstance();
        }
    }

    // ---------------------------------------------------------------- V7

    /// <summary>V7：凭据与端口都**没变** ⇒ 不得无故重启（否则每次"应用设置"都断连）。</summary>
    [Fact]
    public void V7_凭据与端口都没变不得无故重启()
    {
        int port = FreePort();
        Settings cfg = LanSettingsWithPassword(port);

        LiteWebServer? server = null;
        try
        {
            server = new LiteWebServer(cfg) { ListenAddress = IPAddress.Any };
            Assert.True(server.Start(out string startErr), "前置条件 —— " + startErr);

            string fingerprintBefore = server.CurrentCredentialFingerprint;

            WebServerApplyResult result = AppActions.ApplyWebServerWithResult(cfg);

            Assert.False(result.Restarted, "凭据与端口都没变 ⇒ 不得重启服务。");
            Assert.True(result.ServiceRunning);
            Assert.Equal(fingerprintBefore, server.CurrentCredentialFingerprint);
            Assert.True(PortIsListening(port), "服务应当仍在监听。");
        }
        finally
        {
            server?.Dispose();
            ResetServerInstance();
        }
    }

    // ---------------------------------------------------------------- V8

    /// <summary>
    /// V8（**载荷用例**）：停止 ⇒ 主会话、票据、**已建立的 WebSocket** 全部失效。
    /// </summary>
    [Fact]
    public void V8_停止服务后主会话票据与WS连接全部失效()
    {
        var demand = new WebDemandState();
        var manager = new WebSessionManager(writer => { }, demandState: demand);

        MethodInfo createSession = typeof(WebSessionManager).GetMethod(
            "CreateSession", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string) }, null)!;
        MethodInfo tryGetSession = typeof(WebSessionManager).GetMethod(
            "TryGetSession", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(string), typeof(WebSessionRecord).MakeByRefType() }, null)!;
        MethodInfo createTicket = typeof(WebSessionManager).GetMethod(
            "CreateTicket", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(string) }, null)!;
        MethodInfo tryConsumeTicket = typeof(WebSessionManager).GetMethod(
            "TryConsumeTicket", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(string) }, null)!;

        Assert.True(createSession != null && tryGetSession != null &&
                    createTicket != null && tryConsumeTicket != null,
            "WebSessionManager 的会话 / 票据内部方法改名了 —— 用例前提被破坏。");

        const string fingerprint = "verifier-fingerprint";

        object session = createSession.Invoke(manager, new object[] { fingerprint })!;
        string sessionId = (string)session.GetType().GetField("Id")!.GetValue(session)!;

        string ticketBefore = (string)createTicket.Invoke(manager, new object[] { sessionId, fingerprint })!;
        string ticketAfter = (string)createTicket.Invoke(manager, new object[] { sessionId, fingerprint })!;

        // 停止之前：会话与票据都有效
        object[] getArgs = { sessionId, fingerprint, null };
        Assert.True((bool)tryGetSession.Invoke(manager, getArgs)!, "前置条件：会话应当有效。");
        Assert.True((bool)tryConsumeTicket.Invoke(manager, new object[] { ticketBefore, fingerprint })!,
            "前置条件：票据应当有效。");

        // 已建立的 WebSocket 连接
        demand.AddAuthenticatedConnection();
        Assert.True(manager.AuthenticatedConnectionCount > 0, "前置条件：应当有已鉴权的 WS 连接。");

        manager.Stop();

        object[] getArgs2 = { sessionId, fingerprint, null };
        Assert.False((bool)tryGetSession.Invoke(manager, getArgs2)!, "停止后主会话必须失效。");
        Assert.False((bool)tryConsumeTicket.Invoke(manager, new object[] { ticketAfter, fingerprint })!,
            "停止后票据必须失效。");
        Assert.True(manager.AuthenticatedConnectionCount == 0, "停止后已建立的 WebSocket 连接必须全部失效。");
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>把静态单例复位，避免污染其它用例（LiteWebServer.Instance 是进程级状态）。</summary>
    private static void ResetServerInstance()
    {
        try
        {
            PropertyInfo instance = typeof(LiteWebServer).GetProperty(
                "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            instance.SetValue(null, null);
        }
        catch
        {
            // 复位失败不掩盖用例本身的结论
        }
    }
}