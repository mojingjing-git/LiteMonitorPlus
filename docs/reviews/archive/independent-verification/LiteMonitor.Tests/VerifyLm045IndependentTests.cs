using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// ★ 独立复核（verifier）自写的 LM-045 复核用例。
    /// <para>本文件**不复用**实现者提交的 <c>PluginRedirectSandboxServer</c> / <c>PluginRedirectSandboxTests</c>
    /// 的任何辅助类或用例 —— 回环服务器在 <see cref="Verify2LoopbackServer"/> 里**独立重写**了一遍，
    /// 目的是避免"拿被审对象的自证材料验证被审对象"。</para>
    /// <para>全部用例只使用本地回环地址（127.0.0.1 / 127.0.0.2 / 127.0.0.3），**不联网**。</para>
    /// </summary>
    public sealed class VerifyLm045IndependentTests
    {
        private const string SecretApiKey = "VERIFY2-SECRET-APIKEY";
        private const string SecretCookie = "VERIFY2-SECRET-COOKIE";
        private const string UndeclaredMarker = "VERIFY2-UNDECLARED-BODY-MARKER";

        // =====================================================================
        // A. LM-045 闭环（最重要）
        // =====================================================================

        [Fact]
        public async Task A1_带凭据的请求302到未声明主机_未声明主机零请求且从未见到秘密()
        {
            using var undeclared = new Verify2LoopbackServer(
                _ => Verify2Response.Text(UndeclaredMarker), "127.0.0.2");
            using var declared = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, undeclared.Url + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(
                Step("s1", declared.Url + "/start", headers: CredentialHeaders()));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-a1"), tmpl));

            // 凭据只能发往"声明主机集合" ⇒ 原因码是凭据类
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);

            // ★ 核心：未声明主机一个请求都没收到
            Assert.Equal(0, undeclared.RequestCount);
            AssertNoSecret(undeclared);

            // 不是假阳性：第一跳确实发出去了，且凭据确实到了声明主机
            Assert.Equal(1, declared.RequestCount);
            Assert.True(declared.Requests[0].HasHeader("X-Api-Key"));
            Assert.Equal(SecretApiKey, declared.Requests[0].HeaderValue("X-Api-Key"));
        }

        [Fact]
        public async Task A2_请求302到未声明主机_未声明主机零请求()
        {
            using var undeclared = new Verify2LoopbackServer(
                _ => Verify2Response.Text(UndeclaredMarker), "127.0.0.2");
            using var declared = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, undeclared.Url + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", declared.Url + "/start"));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-a2"), tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, undeclared.RequestCount);
            Assert.Equal(1, declared.RequestCount);
        }

        [Fact]
        public async Task A3_未声明主机的响应正文不得进入插件流水线()
        {
            using var undeclared = new Verify2LoopbackServer(
                _ => Verify2Response.Text(UndeclaredMarker), "127.0.0.2");
            using var declared = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, undeclared.Url + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            // 第二步的 URL 里带上第一步提取出的值 {{v}}：
            // 若未声明主机的正文泄漏进流水线，v 会带着标记出现在第二步的请求目标里。
            var tmpl = Chain(
                Step("s1", declared.Url + "/start", extract: new Dictionary<string, string> { ["v"] = "$" }),
                Step("s2", declared.Url + "/second?v={{v}}", extract: new Dictionary<string, string> { ["w"] = "$" }));

            await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-a3"), tmpl));

            // 未声明主机从未被触达
            Assert.Equal(0, undeclared.RequestCount);

            // 声明主机只收到第一跳（第二步根本没跑）
            Assert.Equal(1, declared.RequestCount);

            // 任何被记录下来的请求里都不得出现未声明主机的正文标记
            foreach (var r in declared.Requests)
            {
                Assert.DoesNotContain(UndeclaredMarker, r.Target ?? "");
                Assert.DoesNotContain(UndeclaredMarker, r.Body ?? "");
                foreach (var kv in r.Headers)
                {
                    Assert.DoesNotContain(UndeclaredMarker, kv.Value ?? "");
                }
            }
        }

        [Fact]
        public async Task A4_native_fallback302到未声明主机_未声明主机零请求且步骤失败()
        {
            using var undeclared = new Verify2LoopbackServer(
                _ => Verify2Response.Text(UndeclaredMarker), "127.0.0.2");
            using var fallback = new Verify2LoopbackServer(req =>
                req.Path == "/fb"
                    ? Verify2Response.Redirect(302, undeclared.Url + "/target")
                    : Verify2Response.Text("FALLBACK-OK"), "127.0.0.1");
            // 官方源全部指向一个"必然失败"的本地端点 ⇒ 流程必然落到 fallback
            using var official = new Verify2LoopbackServer(
                _ => Verify2Response.Text("OFFICIAL-FAIL"), "127.0.0.1");
            using var overrideScope = new Verify2SourceOverride(official.Url);
            using var exec = new PluginExecutor();

            string symbol = UniqueSymbol("A4");
            string nativeUrl = "native://crypto?symbol=" + symbol + "&fallback=" + fallback.Url + "/fb";

            var tmpl = Chain(NativeStep("s1", nativeUrl));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-a4"), tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, undeclared.RequestCount);
            Assert.DoesNotContain(UndeclaredMarker, fallback.RawTranscript);
        }

        [Fact]
        public async Task A5_对照_同主机换路径重定向_必须仍然成功()
        {
            using var host = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, "/target?x=1")
                    : Verify2Response.Text("AFTER-REDIRECT"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", host.Url + "/start"));
            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-a5"), tmpl);

            Assert.True(ok);
            Assert.Equal(2, host.RequestCount);
            Assert.Equal("/target", host.Requests[1].Path);
        }

        [Fact]
        public async Task A6_对照_带凭据的请求302到另一个已声明主机_必须放行()
        {
            using var hostB = new Verify2LoopbackServer(
                _ => Verify2Response.Text("DECLARED-B-OK"), "127.0.0.2");
            using var hostA = new Verify2LoopbackServer(
                _ => Verify2Response.Redirect(302, hostB.Url + "/target"), "127.0.0.1");
            using var exec = new PluginExecutor();

            // 两个步骤分别落在两个主机 ⇒ 声明集合 = {127.0.0.1, 127.0.0.2}
            var tmpl = Chain(
                Step("s1", hostA.Url + "/start", headers: CredentialHeaders()),
                Step("s2", hostB.Url + "/second"));

            Assert.Equal(2, PluginProcessor.GetDeclaredHosts(tmpl).Count);

            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-a6"), tmpl);

            Assert.True(ok);
            Assert.Contains(hostB.Requests, r => r.Path == "/target");
        }

        [Fact]
        public async Task A7_对照_native_fallback同主机重定向_必须仍然成功()
        {
            using var fallback = new Verify2LoopbackServer(req =>
                req.Path == "/fb"
                    ? Verify2Response.Redirect(302, "/fb2")
                    : Verify2Response.Text("FALLBACK-OK"), "127.0.0.1");
            using var official = new Verify2LoopbackServer(
                _ => Verify2Response.Text("OFFICIAL-FAIL"), "127.0.0.1");
            using var overrideScope = new Verify2SourceOverride(official.Url);
            using var exec = new PluginExecutor();

            string symbol = UniqueSymbol("A7");
            var tmpl = Chain(NativeStep("s1", "native://crypto?symbol=" + symbol + "&fallback=" + fallback.Url + "/fb"));

            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-a7"), tmpl);

            Assert.True(ok);
            Assert.Contains(fallback.Requests, r => r.Path == "/fb2");
        }
        [Fact]
        public async Task A8_native_fallback307同主机重定向_必须被跟随并成功()
        {
            using var fallback = new Verify2LoopbackServer(req =>
                req.Path == "/fb"
                    ? Verify2Response.Redirect(307, "/fb2")
                    : Verify2Response.Text("FALLBACK-OK"), "127.0.0.1");
            using var official = new Verify2LoopbackServer(
                _ => Verify2Response.Text("OFFICIAL-FAIL"), "127.0.0.1");
            using var overrideScope = new Verify2SourceOverride(official.Url);
            using var exec = new PluginExecutor();

            string symbol = UniqueSymbol("A8");
            var tmpl = Chain(NativeStep("s1", "native://crypto?symbol=" + symbol + "&fallback=" + fallback.Url + "/fb"));

            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-a8"), tmpl);

            Assert.True(ok);
            Assert.Contains(fallback.Requests, r => r.Path == "/fb2");
        }
        // =====================================================================
        // B. 绕过面（自己找的，不只是实现者想到的）
        // =====================================================================

        [Fact]
        public async Task B1_相对Location指向同主机_放行()
        {
            using var host = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, "/other?x=1")
                    : Verify2Response.Text("REL-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-b1"), Chain(Step("s1", host.Url + "/start")));

            Assert.True(ok);
            Assert.Equal("/other", host.Requests[1].Path);
        }

        [Fact]
        public async Task B2_协议相对Location指向未声明主机_必须被拒()
        {
            using var undeclared = new Verify2LoopbackServer(
                _ => Verify2Response.Text(UndeclaredMarker), "127.0.0.2");
            using var declared = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    // 协议相对形式：没有 scheme，继承当前协议但换主机
                    ? Verify2Response.Redirect(302, "//" + undeclared.Host + ":" + undeclared.Port + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-b2"), Chain(Step("s1", declared.Url + "/start"))));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, undeclared.RequestCount);
        }

        [Fact]
        public async Task B3_重定向响应缺Location头_必须失败且原因码可判定()
        {
            using var host = new Verify2LoopbackServer(
                _ => new Verify2Response { Status = 302, Body = Encoding.UTF8.GetBytes("NO-LOCATION") }, "127.0.0.1");
            using var exec = new PluginExecutor();

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-b3"), Chain(Step("s1", host.Url + "/start"))));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(1, host.RequestCount);
        }

        [Fact]
        public async Task B4_重定向跳数超限_必须失败且原因码为跳数上限()
        {
            using var host = new Verify2LoopbackServer(req =>
                Verify2Response.Redirect(302, "/hop" + (int.Parse(req.Path.Substring(4)) + 1)), "127.0.0.1");
            using var exec = new PluginExecutor();

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-b4"), Chain(Step("s1", host.Url + "/hop1"))));

            Assert.Equal(PluginSandboxReasons.RedirectLimit, ex.Reason);
            // 首跳 + 5 次重定向 = 6 个请求（第 6 个响应才触发跳数上限）
            Assert.Equal(6, host.RequestCount);
        }

        [Fact]
        public async Task B5_重定向目标协议非http_s_必须被拒()
        {
            using var host = new Verify2LoopbackServer(
                _ => Verify2Response.Redirect(302, "file:///C:/Windows/win.ini"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-b5"), Chain(Step("s1", host.Url + "/start"))));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
        }

        [Fact]
        public async Task B6_经代理的路径_302到未声明主机_未声明主机零请求()
        {
            using var undeclared = new Verify2LoopbackServer(
                _ => Verify2Response.Text(UndeclaredMarker), "127.0.0.2");
            // 这台服务器扮演"代理"：它收到的是绝对 URL 形式的请求目标
            using var proxy = new Verify2LoopbackServer(req =>
                Verify2Response.Redirect(302, undeclared.Url + "/target"), "127.0.0.3");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", "http://127.0.0.1:1/start", proxy: proxy.Url));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-b6"), tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, undeclared.RequestCount);
            // 代理只收到第一跳（第二跳根本没发出去）
            Assert.Equal(1, proxy.RequestCount);
        }

        [Fact]
        public async Task B7_声明主机集合为空_模板变量主机__带凭据请求必须被拒()
        {
            using var host = new Verify2LoopbackServer(
                _ => Verify2Response.Text("OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            // 主机来自模板变量 ⇒ 不构成"显式声明" ⇒ 声明集合为空
            var tmpl = Chain(Step("s1", "http://{{target_host}}/x", headers: CredentialHeaders()));
            var inst = Instance("v2-b7");
            inst.InputValues["target_host"] = "127.0.0.1:" + host.Port;

            Assert.Empty(PluginProcessor.GetDeclaredHosts(tmpl));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            Assert.Equal(0, host.RequestCount);
        }

        [Fact]
        public async Task B8_声明主机集合为空_模板变量主机__不带凭据时只能重定向到直连主机()
        {
            using var undeclared = new Verify2LoopbackServer(
                _ => Verify2Response.Text(UndeclaredMarker), "127.0.0.2");
            using var declared = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, undeclared.Url + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", "http://{{target_host}}/start"));
            var inst = Instance("v2-b8");
            inst.InputValues["target_host"] = "127.0.0.1:" + declared.Port;

            Assert.Empty(PluginProcessor.GetDeclaredHosts(tmpl));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, undeclared.RequestCount);
        }

        [Fact]
        public async Task B14_不带凭据时_重定向到另一个已声明主机_必须放行()
        {
            using var hostB = new Verify2LoopbackServer(
                _ => Verify2Response.Text("DECLARED-B-OK"), "127.0.0.2");
            using var hostA = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, hostB.Url + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            // 两个步骤分别落在两个主机 ⇒ 声明集合 = {127.0.0.1, 127.0.0.2}
            var tmpl = Chain(
                Step("s1", hostA.Url + "/start"),
                Step("s2", hostB.Url + "/second"));

            Assert.Equal(2, PluginProcessor.GetDeclaredHosts(tmpl).Count);

            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-b14"), tmpl);

            Assert.True(ok);
            Assert.Contains(hostB.Requests, r => r.Path == "/target");
        }
        [Fact]
        public async Task B9_端口差异_同主机不同端口_事实记录()
        {
            using var other = new Verify2LoopbackServer(
                _ => Verify2Response.Text("OTHER-PORT-OK"), "127.0.0.1");
            using var declared = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, "http://127.0.0.1:" + other.Port + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", declared.Url + "/start"));

            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-b9"), tmpl);

            // 事实记录：主机比对**忽略端口** ⇒ 同主机不同端口的重定向被放行。
            Assert.True(ok);
            Assert.Equal(1, other.RequestCount);
        }

        [Fact]
        public async Task B10_尾随点主机写法_事实记录()
        {
            int port = 0;
            using var host = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(302, "http://127.0.0.1.:" + port + "/target")
                    : Verify2Response.Text("DECLARED-OK"), "127.0.0.1");
            port = host.Port;
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", host.Url + "/start"));

            // 事实记录：尾随点写法与声明主机**不匹配** ⇒ 被拒（偏严方向）
            await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-b10"), tmpl));

            Assert.Equal(1, host.RequestCount);
        }

        [Fact]
        public async Task B11_native官方源接缝指向本地回环_必须仍然可用()
        {
            using var official = new Verify2LoopbackServer(req =>
                req.Path.StartsWith("/bybit")
                    ? Verify2Response.Json(BybitShape)
                    : Verify2Response.Text("OFFICIAL-FAIL"), "127.0.0.1");
            using var overrideScope = new Verify2SourceOverride(official.Url);
            using var exec = new PluginExecutor();

            string symbol = UniqueSymbol("B11");
            var tmpl = Chain(NativeStep("s1", "native://crypto?symbol=" + symbol));

            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-b11"), tmpl);

            Assert.True(ok);
            Assert.Contains(official.Requests, r => r.Path.StartsWith("/bybit"));
        }

        [Fact]
        public void B12_三个HttpClient的自动重定向都必须关闭()
        {
            // 默认客户端
            using var exec = new PluginExecutor();
            Assert.False(AllowAutoRedirectOf(PrivateField(exec, "_http")));

            // 代理客户端（走一次 GetClient 才会创建）
            var getClient = typeof(PluginExecutor).GetMethod("GetClient", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(getClient);
            using var proxyClient = (HttpClient)getClient.Invoke(exec, new object[] { "http://127.0.0.1:9" });
            Assert.False(AllowAutoRedirectOf(proxyClient));

            // CryptoNative 的客户端
            Assert.False(AllowAutoRedirectOf(CryptoNativeClient()));
        }

        [Fact]
        public void B13_主机比对口径白盒核对()
        {
            // 白盒核对生产用的主机比对原语（独立于实现者的测试，自己重新推导口径）：
            var m = typeof(PluginExecutor).GetMethod("HostInAllowedSet",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };

            // 端口被忽略
            Assert.True((bool)m.Invoke(null, new object[] { "127.0.0.1:8080", allowed }));
            // 大小写不敏感（仅对有大小写的名字有意义）
            Assert.True((bool)m.Invoke(null, new object[] { "LOCALHOST", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" } }));
            // 尾随点不被归一化 ⇒ 不匹配
            Assert.False((bool)m.Invoke(null, new object[] { "127.0.0.1.", allowed }));
            // 不同主机不匹配
            Assert.False((bool)m.Invoke(null, new object[] { "127.0.0.2", allowed }));
        }
        [Fact]
        public void B15_IPv6字面量主机的声明与比对口径_事实记录()
        {
            // 事实记录：IPv6 字面量主机在"声明主机提取"与"主机比对"两处被**不同地**截断
            //   ⇒ IPv6 字面量目标在本策略下永远匹配不上（偏严方向，不会漏放行）。
            var tmpl = Chain(Step("s1", "http://[::1]:8080/x"));
            var declared = PluginProcessor.GetDeclaredHosts(tmpl);
            Console.WriteLine("DECLARED_FOR_IPV6=" + string.Join(",", declared));

            var m = typeof(PluginExecutor).GetMethod("HostInAllowedSet",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);

            // Uri.Host 对 IPv6 字面量返回带方括号的形式
            Assert.Equal("[::1]", new Uri("http://[::1]:8080/x").Host);

            // 用真实声明集合去比对 → 不匹配（IPv6 目标在本策略下不可用）
            Assert.False((bool)m.Invoke(null, new object[] { "[::1]", declared }));
        }

        [Fact]
        public void B16_等价IP写法不会绕过主机比对_事实记录()
        {
            // 事实记录：.NET 的 Uri 解析会把十进制/八进制/十六进制/短式 IP 全部归一化成
            // 规范点分十进制 ⇒ 这些写法**无法**用来绕过主机比对（它们就是同一个主机）。
            var m = typeof(PluginExecutor).GetMethod("HostInAllowedSet",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };

            foreach (string variant in new[]
                     {
                         "http://2130706433/", "http://0177.0.0.1/", "http://0x7f.0.0.1/", "http://127.1/"
                     })
            {
                string host = new Uri(variant).Host;
                Assert.Equal("127.0.0.1", host);
                Assert.True((bool)m.Invoke(null, new object[] { host, allowed }));
            }

            // 尾随点不会被归一化 ⇒ 与声明主机不匹配（偏严方向）
            Assert.Equal("127.0.0.1.", new Uri("http://127.0.0.1./").Host);
            Assert.False((bool)m.Invoke(null, new object[] { "127.0.0.1.", allowed }));
        }
        [Fact]
        public void B17_https降级到http_白盒核对_必须被拒()
        {
            // ★ 端到端不可测（生产客户端刻意不装证书校验回调 ⇒ 自签 TLS 服务器会被拒），
            //   因此对**协议复核原语**做白盒核对：https → http 降级必须抛 RedirectTargetNotAllowed。
            var m = typeof(PluginExecutor).GetMethod("EnsureRedirectSchemeAllowed",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);

            var https = new Uri("https://api.example.com/x");
            var http = new Uri("http://api.example.com/x");
            var https2 = new Uri("https://api.example.com/y");

            // 降级 ⇒ 抛
            var ex = Assert.Throws<PluginSandboxException>(() =>
            {
                try { m.Invoke(null, new object[] { https, http }); }
                catch (TargetInvocationException tie) { throw Assert.IsType<PluginSandboxException>(tie.InnerException); }
            });
            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);

            // 升级 http → https ⇒ 放行（不抛）
            m.Invoke(null, new object[] { http, https2 });

            // 同协议 ⇒ 放行
            m.Invoke(null, new object[] { https, new Uri("https://api.example.com/z") });

            // 非 http(s) ⇒ 抛
            var ex2 = Assert.Throws<PluginSandboxException>(() =>
            {
                try { m.Invoke(null, new object[] { https, new Uri("file:///C:/Windows/win.ini") }); }
                catch (TargetInvocationException tie) { throw Assert.IsType<PluginSandboxException>(tie.InnerException); }
            });
            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex2.Reason);
        }
        // =====================================================================
        // C. 既有行为不得被破坏
        // =====================================================================

        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(303)]
        public async Task C1_30x_方法变GET且正文丢弃(int status)
        {
            using var host = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(status, "/target")
                    : Verify2Response.Text("AFTER"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", host.Url + "/start", method: "POST", body: "{\"k\":1}"));
            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-c1-" + status), tmpl);

            Assert.True(ok);
            Assert.Equal(2, host.RequestCount);
            Assert.Equal("GET", host.Requests[1].Method);
            Assert.Equal("", host.Requests[1].Body);
        }

        [Theory]
        [InlineData(307)]
        [InlineData(308)]
        public async Task C2_307_308_保留方法与正文(int status)
        {
            using var host = new Verify2LoopbackServer(req =>
                req.Path == "/start"
                    ? Verify2Response.Redirect(status, "/target")
                    : Verify2Response.Text("AFTER"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", host.Url + "/start", method: "POST", body: "{\"k\":1}"));
            bool ok = await exec.ExecuteInstanceAsync(Instance("v2-c2-" + status), tmpl);

            Assert.True(ok);
            Assert.Equal(2, host.RequestCount);
            Assert.Equal("POST", host.Requests[1].Method);
            Assert.Contains("\"k\":1", host.Requests[1].Body);
        }

        [Fact]
        public async Task C3_响应体2MiB上限仍生效()
        {
            byte[] big = new byte[2 * 1024 * 1024 + 1];
            using var host = new Verify2LoopbackServer(
                _ => new Verify2Response { Body = big }, "127.0.0.1");
            using var exec = new PluginExecutor();

            var tmpl = Chain(Step("s1", host.Url + "/big"));
            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-c3"), tmpl));

            Assert.Equal(PluginSandboxReasons.ResponseTooLarge, ex.Reason);
        }

        [Fact]
        public async Task C4_并发槽位不超过8()
        {
            using var host = new Verify2LoopbackServer(
                async req => { await Task.Delay(400); return Verify2Response.Text("SLOW-OK"); },
                "127.0.0.1");

            var executors = new List<PluginExecutor>();
            try
            {
                var tasks = new List<Task<bool>>();
                for (int i = 0; i < 12; i++)
                {
                    var exec = new PluginExecutor();
                    executors.Add(exec);
                    var tmpl = Chain(Step("s1", host.Url + "/slow"));
                    tasks.Add(exec.ExecuteInstanceAsync(Instance("v2-c4-" + i), tmpl));
                }

                bool[] results = await Task.WhenAll(tasks);

                Assert.True(results.All(x => x), "所有请求都应当成功");
                Assert.True(host.MaxConcurrent <= 8,
                    "并发在途请求数必须 <= 8，实测 " + host.MaxConcurrent);
                Assert.True(host.MaxConcurrent >= 2,
                    "本用例必须真的产生并发，实测峰值 " + host.MaxConcurrent);
            }
            finally
            {
                foreach (var e in executors) e.Dispose();
            }
        }

        [Fact]
        public void C5_契约常量6个数值逐条核对()
        {
            Assert.Equal(2 * 1024 * 1024, PluginSandboxLimits.MaxResponseBytes);
            Assert.Equal(500, PluginSandboxLimits.MaxRegexMatchTimeoutMs);
            Assert.Equal(16, PluginSandboxLimits.MaxStepsPerExecution);
            Assert.Equal(64, PluginSandboxLimits.MaxTargetsPerStep);
            Assert.Equal(30_000, PluginSandboxLimits.MaxExecutionDurationMs);
            Assert.Equal(8, PluginSandboxLimits.MaxConcurrentRequests);

            Assert.Equal(TimeSpan.FromSeconds(30), PluginSandboxScope.DefaultExecutionDuration);
        }

        [Fact]
        public async Task C6_总时长上限覆盖整条重定向链()
        {
            // 用一个 1.5 秒上限的作用域（覆写 CreateScope）⇒ 证明截止时刻约束**整条链**，
            // 而不是每跳各算一次：第一跳花掉 1.2 s 后只剩 0.3 s，第二跳必然超时。
            using var host = new Verify2LoopbackServer(async req =>
            {
                await Task.Delay(1200);
                return req.Path == "/start"
                    ? Verify2Response.Redirect(302, "/next")
                    : Verify2Response.Text("OK");
            }, "127.0.0.1");
            using var exec = new Verify2ShortScopeExecutor(TimeSpan.FromMilliseconds(1500));

            var tmpl = Chain(Step("s1", host.Url + "/start"));
            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(Instance("v2-c6"), tmpl));

            Assert.Equal(PluginSandboxReasons.DeadlineExceeded, ex.Reason);

            // 让后台共享请求自然收尾，避免占用并发槽位影响别的用例
            await Task.Delay(1500);
        }

        // =====================================================================
        // 辅助
        // =====================================================================

        private static Dictionary<string, string> CredentialHeaders() => new Dictionary<string, string>
        {
            ["X-Api-Key"] = SecretApiKey,
            ["Cookie"] = "sid=" + SecretCookie
        };

        private static void AssertNoSecret(Verify2LoopbackServer server)
        {
            foreach (var r in server.Requests)
            {
                foreach (var kv in r.Headers)
                {
                    Assert.DoesNotContain(SecretApiKey, kv.Value ?? "");
                    Assert.DoesNotContain(SecretCookie, kv.Value ?? "");
                }
                Assert.DoesNotContain(SecretApiKey, r.Body ?? "");
                Assert.DoesNotContain(SecretCookie, r.Body ?? "");
            }
            Assert.DoesNotContain(SecretApiKey, server.RawTranscript);
            Assert.DoesNotContain(SecretCookie, server.RawTranscript);
        }

        private static HttpClient PrivateField(object instance, string name)
        {
            var f = typeof(PluginExecutor).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return (HttpClient)f.GetValue(instance);
        }

        private static HttpClient CryptoNativeClient()
        {
            var f = typeof(CryptoNative).GetField("_client", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f);
            return (HttpClient)f.GetValue(null);
        }

        private static bool AllowAutoRedirectOf(HttpClient client)
        {
            HttpMessageHandler handler = null;
            for (var t = typeof(HttpClient); t != null && handler == null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.GetValue(client) is HttpMessageHandler h) { handler = h; break; }
                }
            }
            Assert.NotNull(handler);
            var prop = handler.GetType().GetProperty("AllowAutoRedirect");
            Assert.NotNull(prop);
            return (bool)prop.GetValue(handler);
        }

        private static string UniqueSymbol(string tag) =>
            "V2" + tag + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

        private static PluginInstanceConfig Instance(string id) => new PluginInstanceConfig { Id = id };

        private static PluginExecutionStep Step(string id, string url, string method = "GET", string body = "",
            Dictionary<string, string> headers = null, string proxy = "",
            Dictionary<string, string> extract = null)
        {
            return new PluginExecutionStep
            {
                Id = id,
                Url = url,
                Method = method,
                Body = body,
                Headers = headers,
                Proxy = proxy,
                ResponseEncoding = "utf-8",
                ResponseFormat = "text",
                Extract = extract ?? new Dictionary<string, string> { ["v"] = "$" },
                CacheMinutes = 0
            };
        }

        private static PluginExecutionStep NativeStep(string id, string url)
        {
            return new PluginExecutionStep
            {
                Id = id,
                Url = url,
                Method = "GET",
                ResponseEncoding = "utf-8",
                ResponseFormat = "text",
                Extract = new Dictionary<string, string> { ["v"] = "$" },
                CacheMinutes = 0
            };
        }

        private static PluginTemplate Chain(params PluginExecutionStep[] steps)
        {
            return new PluginTemplate
            {
                Id = "tmpl-v2",
                Meta = new PluginMeta { Name = "V2" },
                Execution = new PluginExecution { Type = "chain", Steps = steps.ToList() },
                Outputs = new List<PluginOutput>()
            };
        }

        private const string BybitShape =
            "{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[" +
            "{\"symbol\":\"BTCUSDT\",\"lastPrice\":\"95000\",\"price24hPcnt\":\"0.0123\"," +
            "\"highPrice24h\":\"96000\",\"lowPrice24h\":\"94000\",\"turnover24h\":\"123456\"}]}}";

        /// <summary>把 CryptoNative 的官方源指向本地回环（测试接缝），退出时还原。</summary>
        private sealed class Verify2SourceOverride : IDisposable
        {
            private readonly Dictionary<string, string> _previous;

            public Verify2SourceOverride(string baseUrl)
            {
                _previous = CryptoNative.SourceUrlOverrides;
                CryptoNative.SourceUrlOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bybit"] = baseUrl + "/bybit?symbol={symbol}",
                    ["GateIO"] = baseUrl + "/gate?symbol={symbol}",
                    ["Binance"] = baseUrl + "/binance?symbol={symbol}",
                    ["OKX"] = baseUrl + "/okx?symbol={symbol}",
                };
            }

            public void Dispose() => CryptoNative.SourceUrlOverrides = _previous;
        }

        /// <summary>把沙箱作用域的总时长上限改成可配置的短时长（用于验证"截止时刻覆盖整条链"）。</summary>
        private sealed class Verify2ShortScopeExecutor : PluginExecutor
        {
            private readonly TimeSpan _duration;

            public Verify2ShortScopeExecutor(TimeSpan duration) => _duration = duration;

            protected override PluginSandboxScope CreateScope(CancellationToken token) =>
                new PluginSandboxScope(token, _duration,
                    PluginSandboxLimits.MaxStepsPerExecution, PluginSandboxLimits.MaxTargetsPerStep);
        }
    }
    // =====================================================================
    // 独立重写的回环服务器（不复用实现者的 PluginRedirectSandboxServer）
    // =====================================================================

    internal sealed class Verify2LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Verify2Request> _requests = new();
        private readonly object _sync = new();
        private readonly StringBuilder _transcript = new();
        private int _inFlight;
        private int _maxConcurrent;

        public Verify2LoopbackServer(Func<Verify2Request, Verify2Response> handler, string host = "127.0.0.1")
            : this(req => Task.FromResult(handler(req)), host)
        {
        }

        public Verify2LoopbackServer(Func<Verify2Request, Task<Verify2Response>> handler, string host = "127.0.0.1")
        {
            Handler2 = handler ?? throw new ArgumentNullException(nameof(handler));
            Host = host;
            _listener = new TcpListener(IPAddress.Parse(host), 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private Func<Verify2Request, Task<Verify2Response>> Handler2 { get; }

        /// <summary>同步处理器（可随时替换，用于需要"先建服务器再决定跳转目标"的用例）。</summary>
        public Func<Verify2Request, Verify2Response> Handler { get; set; }

        public string Host { get; }

        public int Port { get; }

        public string Url => "http://" + Host + ":" + Port;

        public int RequestCount { get { lock (_sync) return _requests.Count; } }

        public List<Verify2Request> Requests { get { lock (_sync) return new List<Verify2Request>(_requests); } }

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public string RawTranscript { get { lock (_sync) return _transcript.ToString(); } }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            int current = Interlocked.Increment(ref _inFlight);
            int observed;
            while (current > (observed = Volatile.Read(ref _maxConcurrent)))
            {
                if (Interlocked.CompareExchange(ref _maxConcurrent, current, observed) == observed) break;
            }
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var req = await Verify2Http.ReadRequestAsync(stream);
                    if (req == null) return;

                    lock (_sync)
                    {
                        _requests.Add(req);
                        _transcript.Append(req.Method).Append(' ').Append(req.Target).Append('\n');
                        foreach (var kv in req.Headers) _transcript.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
                        _transcript.Append(req.Body).Append('\n').Append("----\n");
                    }

                    var resp = Handler != null
                        ? Handler(req)
                        : await Handler2(req).ConfigureAwait(false);

                    await Verify2Http.WriteResponseAsync(stream, req, resp);
                }
            }
            catch { /* 客户端提前断开是预期内的 */ }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }

    internal sealed class Verify2Request
    {
        public string Method = "";
        public string Target = "";
        public string Body = "";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

        public string Path
        {
            get
            {
                string t = Target ?? "";
                int scheme = t.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0)
                {
                    int slash = t.IndexOf('/', scheme + 3);
                    t = slash >= 0 ? t.Substring(slash) : "/";
                }
                int q = t.IndexOf('?');
                if (q >= 0) t = t.Substring(0, q);
                return t;
            }
        }

        public bool HasHeader(string name) => Headers.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v);

        public string HeaderValue(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    internal sealed class Verify2Response
    {
        public int Status = 200;
        public byte[] Body = Array.Empty<byte>();
        public string ContentType = "text/plain; charset=utf-8";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

        public static Verify2Response Text(string s) =>
            new Verify2Response { Body = Encoding.UTF8.GetBytes(s ?? "") };

        public static Verify2Response Json(string s) =>
            new Verify2Response
            {
                ContentType = "application/json; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(s ?? "")
            };

        public static Verify2Response Redirect(int status, string location)
        {
            var r = new Verify2Response { Status = status };
            r.Headers["Location"] = location;
            return r;
        }
    }

    internal static class Verify2Http
    {
        public static async Task<Verify2Request> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[16384];
            var head = new List<byte>();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0) return null;
                for (int i = 0; i < read; i++) head.Add(buffer[i]);
                headerEnd = IndexOfCrLfCrLf(head);
                if (head.Count > 256 * 1024) return null;
            }

            string text = Encoding.ASCII.GetString(head.ToArray(), 0, headerEnd);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            var req = new Verify2Request
            {
                Method = parts.Length > 0 ? parts[0] : "",
                Target = parts.Length > 1 ? parts[1] : ""
            };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            if (req.Headers.TryGetValue("Content-Length", out var lenText)
                && int.TryParse(lenText, out int len) && len > 0)
            {
                var body = new byte[len];
                int bodyOffset = headerEnd + 4;
                int copied = Math.Min(len, head.Count - bodyOffset);
                for (int i = 0; i < copied; i++) body[i] = head[bodyOffset + i];
                int offset = copied;
                while (offset < len)
                {
                    int read = await stream.ReadAsync(body, offset, len - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                req.Body = Encoding.UTF8.GetString(body, 0, offset);
            }
            return req;
        }

        public static async Task WriteResponseAsync(NetworkStream stream, Verify2Request req, Verify2Response resp)
        {
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {resp.Status} {Reason(resp.Status)}\r\n");
            head.Append($"Content-Type: {resp.ContentType}\r\n");
            foreach (var kv in resp.Headers) head.Append($"{kv.Key}: {kv.Value}\r\n");
            head.Append($"Content-Length: {resp.Body.Length}\r\n");
            head.Append("Connection: close\r\n\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, 0, headBytes.Length);
            if (req.Method != "HEAD" && resp.Body.Length > 0)
            {
                await stream.WriteAsync(resp.Body, 0, resp.Body.Length);
            }
            await stream.FlushAsync();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 303: return "See Other";
                case 307: return "Temporary Redirect";
                case 308: return "Permanent Redirect";
                default: return "Status";
            }
        }

        private static int IndexOfCrLfCrLf(List<byte> bytes)
        {
            for (int i = 0; i + 3 < bytes.Count; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n') return i;
            }
            return -1;
        }
    }
}