using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P2 批次 B-2：插件沙箱的**调用方策略**与**主机允许集合**护栏。
    ///
    /// 盯住的缺陷：
    ///   LM-078 —— 在途合并键只有**请求摘要**，不含**允许目标 / 凭据策略**
    ///              ⇒ 凭据目标校验只对**创建租约**的调用方执行
    ///              （命中已有租约时自己的 valueFactory 永不被求值）
    ///              ⇒ **白名单更严**的第二个调用方直接复用别人的响应（fail-open）。
    ///              修法：把"按调用方策略"的校验**前移到调用侧**（每个等待者各自履责），
    ///              **不改在途键、不改摘要**（S1.0 契约 C1/C2/C4 + I7 锁死）。
    ///   LM-079 —— <c>StripPort</c> 对 IPv6 字面量按**最后一个冒号**截断
    ///              ⇒ <c>[fd00::1]</c> 与 <c>[fd00::2]</c> 都化成 <c>[fd00:</c>
    ///              ⇒ **地址碰撞**：声明了 <c>[fd00::1]</c> 却能把请求/凭据发往 <c>[fd00::2]</c>。
    ///              三处实现（PluginExecutor / PluginProcessor / CryptoNative）必须**同构**修复。
    ///
    /// 测试纪律：
    ///  - 一律只连 127.0.0.1 回环的本地服务器，**不触公网**；
    ///  - 服务器自带请求计数，"实际发出请求数"因此可以被真实断言；
    ///  - 所有断言都真正执行，不存在早退/跳过（0 发现必须非零退出）。
    /// </summary>
    public class BatchB2PluginHostPolicyTests
    {
        // =====================================================================
        // LM-078 —— 在途租约不得成为白名单旁路
        // =====================================================================

        /// <summary>
        /// LM-078 核心断言：**白名单更严**的第二个调用方**不得**复用别人已建立的租约拿到响应。
        /// <para>
        /// 场景：甲（白名单含目标主机）先发起请求并把响应挂住（租约保持在途）；
        /// 乙（**自己的**白名单不含目标主机）随后发起**完全相同**的请求
        /// （同 URL / 同正文 / 同 Header / 同代理 / 同编码 ⇒ 摘要相同 ⇒ 命中同一条在途租约）。
        /// </para>
        /// <para>
        /// 修复前：乙命中已有租约 ⇒ 自己的 valueFactory 永不被求值 ⇒ 乙的
        /// <c>EnsureCredentialTargetAllowed</c> **从未运行** ⇒ 乙拿到本应被拒绝的响应（fail-open）。
        /// 修复后：乙在**加入租约之前**用**自己的** declaredHosts 各校验一次 ⇒ 乙被拒，
        /// 且服务器自始至终只看到甲的那一条请求。
        /// </para>
        /// </summary>
        [Fact]
        public async Task LM078_白名单更严的第二个调用方不得复用别人已建立的租约()
        {
            using var gate = new ManualResetEventSlim(false);
            using var server = new RedirectSandboxServer(_ =>
            {
                // 把响应挂住：保证甲的租约在乙到达时**仍在途**
                gate.Wait(TimeSpan.FromSeconds(8));
                return RedirectSandboxResponse.Text("shared-payload");
            });
            using var exec = new PluginExecutor();

            var step = NewStep(server.Url + "/lm078", headers: H("Authorization", "Bearer lm078"));

            // 甲：白名单**含**目标主机 ⇒ 允许（请求真的发出，响应被挂住）
            using var scopeA = NewScope();
            var lenient = RunStepWithScopeAsync(exec, NewInstance("inst-lm078-lenient"), step, scopeA, Hosts("127.0.0.1"));

            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 1, TimeSpan.FromSeconds(15)),
                "甲的请求必须真的发出（回环服务器未收到请求）");

            // 乙：白名单**更严**（不含目标主机）⇒ 必须被**自己的**策略拒绝
            using var scopeB = NewScope();
            var ex = await Assert.ThrowsAsync<PluginSandboxException>(
                () => RunStepWithScopeAsync(exec, NewInstance("inst-lm078-strict"), step, scopeB, Hosts("strict.example")));

            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);

            // 乙**没有**拿到任何响应：服务器自始至终只看到甲的那一条请求
            Assert.Equal(1, server.RequestCount);

            gate.Set();
            string payload = await lenient;
            Assert.Equal("shared-payload", payload);
            Assert.Equal(1, server.RequestCount);
        }

        /// <summary>
        /// LM-078 的**反向护栏**：完全相同的身份 + 完全相同的策略 ⇒ 仍然**只发一条** HTTP。
        /// <para>
        /// 这条用例守住 S1.0 契约 C4（"在途键 = 摘要" ⇒ 同身份同响应，允许跨实例合并"）
        /// 与验收矩阵 I6 / R1 —— 修 LM-078 **不得**把共享请求变成两条。
        /// 同时钉住"**在途键逐字等于摘要**"（契约 C4 + <c>PluginRequestIdentityTests.I7</c>）：
        /// 本次修复只前移了校验，**没有**改动键与摘要。
        /// </para>
        /// </summary>
        [Fact]
        public async Task LM078_相同身份且相同策略仍然只发一条HTTP且在途键逐字等于摘要()
        {
            using var gate = new ManualResetEventSlim(false);
            using var server = new RedirectSandboxServer(_ =>
            {
                gate.Wait(TimeSpan.FromSeconds(8));
                return RedirectSandboxResponse.Text("shared-payload");
            });
            using var exec = new PluginExecutor();

            var step = NewStep(server.Url + "/lm078b", headers: H("Authorization", "Bearer lm078b"));

            using var scopeA = NewScope();
            using var scopeB = NewScope();

            // 两个调用方（不同实例 ⇒ 不同插件，但请求身份完全相同 ⇒ 摘要相同）
            // 注意：ExecuteStepWithScopeAsync 在首个 await 之前**同步**执行
            // ⇒ 调用返回时两次"加入租约"都已完成（与既有 PluginRequestIdentityTests 的用法一致）。
            var a = RunStepWithScopeAsync(exec, NewInstance("inst-lm078b-a"), step, scopeA, Hosts("127.0.0.1"));
            var b = RunStepWithScopeAsync(exec, NewInstance("inst-lm078b-b"), step, scopeB, Hosts("127.0.0.1"));

            // 在途字典里**只有一条**租约，且它的键**逐字等于摘要**
            var inflight = Inflight(exec);
            Assert.Single(inflight);
            string key = inflight.Keys.Single();
            Assert.Equal(IdentityDigest("GET", server.Url + "/lm078b", "", H("Authorization", "Bearer lm078b"), "", "utf-8"), key);

            Assert.True(await WaitUntilAsync(() => server.RequestCount >= 1, TimeSpan.FromSeconds(15)),
                "请求必须真的发出（回环服务器未收到请求）");

            gate.Set();
            var results = await Task.WhenAll(a, b);

            Assert.Equal("shared-payload", results[0]);
            Assert.Equal(results[0], results[1]);      // 两个调用方拿到**同一条**响应
            Assert.Equal(1, server.RequestCount);      // ★ 实际发出的 HTTP 请求数 == 1（守住 I6 / R1）
        }

        // =====================================================================
        // LM-079 —— StripPort 的 IPv6 地址碰撞
        // =====================================================================

        /// <summary>
        /// LM-079 核心断言：声明了 <c>[fd00::1]</c> 时，请求 <c>[fd00::2]</c> **必须被拒**。
        /// <para>
        /// 原实现按**最后一个冒号**截断 ⇒ <c>"[fd00::1]"</c> 与 <c>"[fd00::2]"</c>
        /// 都被化成 <c>"[fd00:"</c> ⇒ **地址碰撞**（fail-open）。
        /// </para>
        /// <para>三处口径各覆盖到：
        /// ① <c>PluginProcessor.HostMatches</c>（经 <c>PluginSandbox.EnsureCredentialTargetAllowed</c>，public）；
        /// ② <c>PluginExecutor.HostInAllowedSet</c>（重定向逐跳复核 / 原生目标校验）；
        /// ③ <c>CryptoNative.IsHostAllowed</c>（原生回退目标校验）。</para>
        /// </summary>
        [Fact]
        public void LM079_声明的IPv6主机不得匹配另一个IPv6地址()
        {
            var creds = H("Authorization", "Bearer lm079");

            // ① PluginProcessor 口径（public API）
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("http://[fd00::2]/x", creds, Hosts("[fd00::1]")));
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);

            // ② PluginExecutor 口径
            Assert.False(HostInAllowedSet("[fd00::2]", Hosts("[fd00::1]")));

            // ③ CryptoNative 口径
            Assert.False(IsHostAllowed("[fd00::2]", Hosts("[fd00::1]")));

            // 反向护栏：声明的主机**自己**必须仍然匹配（不得把 IPv6 一律拒掉）
            PluginSandbox.EnsureCredentialTargetAllowed("http://[fd00::1]/x", creds, Hosts("[fd00::1]"));
            Assert.True(HostInAllowedSet("[fd00::1]", Hosts("[fd00::1]")));
            Assert.True(IsHostAllowed("[fd00::1]", Hosts("[fd00::1]")));
        }

        /// <summary>
        /// LM-079 的**回归护栏**：IPv4 的 <c>host</c> 与 <c>host:port</c> 仍然等价（三处口径）。
        /// <para>剥端口是既有正确行为，本次修复只改 IPv6 分支，IPv4 必须**逐位**不变。</para>
        /// </summary>
        [Fact]
        public void LM079_IPv4的host与host_port仍然等价()
        {
            var creds = H("Authorization", "Bearer lm079");

            // host 与 host:port 等价
            PluginSandbox.EnsureCredentialTargetAllowed("http://api.example.com/x", creds, Hosts("api.example.com:8443"));
            PluginSandbox.EnsureCredentialTargetAllowed("http://api.example.com:8443/x", creds, Hosts("api.example.com"));
            Assert.True(HostInAllowedSet("api.example.com", Hosts("api.example.com:8443")));
            Assert.True(HostInAllowedSet("api.example.com:8443", Hosts("api.example.com")));
            Assert.True(IsHostAllowed("api.example.com", Hosts("api.example.com:8443")));
            Assert.True(IsHostAllowed("api.example.com:8443", Hosts("api.example.com")));

            // 不同主机仍然被拒
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("http://evil.example.com/x", creds, Hosts("api.example.com:8443")));
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            Assert.False(HostInAllowedSet("api.example.com", Hosts("evil.example.com:8443")));
            Assert.False(IsHostAllowed("api.example.com", Hosts("evil.example.com")));
        }

        /// <summary>
        /// LM-079 的**正向护栏**：IPv6 字面量**整体带端口**时，整体就是主机名
        /// （"<c>[fd00::1]:8080</c>" 的主机是 "<c>[fd00::1]</c>"），
        /// 且仍然不得匹配另一个 IPv6 地址。
        /// </summary>
        [Fact]
        public void LM079_IPv6字面量整体带端口时整体是主机名()
        {
            var creds = H("Authorization", "Bearer lm079");

            // 端口被剥掉，方括号内容整体保留
            PluginSandbox.EnsureCredentialTargetAllowed("http://[fd00::1]:8080/x", creds, Hosts("[fd00::1]"));
            Assert.True(HostInAllowedSet("[fd00::1]", Hosts("[fd00::1]:8080")));
            Assert.True(IsHostAllowed("[fd00::1]", Hosts("[fd00::1]:8080")));

            // 仍然不得匹配另一个 IPv6 地址
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("http://[fd00::1]:8080/x", creds, Hosts("[fd00::2]")));
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            Assert.False(HostInAllowedSet("[fd00::1]", Hosts("[fd00::2]:8080")));
            Assert.False(IsHostAllowed("[fd00::1]", Hosts("[fd00::2]:8080")));
        }

        /// <summary>
        /// LM-079 **端到端**：重定向到**另一个** IPv6 地址时，逐跳复核必须拒绝，
        /// 且**那一跳请求根本不发出**。
        /// <para>
        /// 场景：首跳在 127.0.0.1（回环，可达），跳向**未声明**的 <c>[fd00::2]</c>；
        /// 插件只声明了 <c>[fd00::1]</c>（IPv6 字面量整体是主机名）。
        /// 修复前：<c>[fd00::1]</c> 与 <c>[fd00::2]</c> 都被截成 <c>"[fd00:"</c>
        /// ⇒ 第二跳被**误判为已声明** ⇒ 请求被发往未声明的 IPv6 地址（fail-open）。
        /// </para>
        /// <para>两条路径分别覆盖两处口径：无凭据 ⇒ <c>PluginExecutor.HostInAllowedSet</c>；
        /// 有凭据 ⇒ <c>PluginProcessor.HostMatches</c>。</para>
        /// </summary>
        [Fact]
        public async Task LM079_端到端_重定向到另一个IPv6地址必须被逐跳复核拒绝()
        {
            using var server = new RedirectSandboxServer(_ => RedirectSandboxResponse.Redirect(302, "http://[fd00::2]/x"));
            using var exec = new PluginExecutor();

            // 无凭据 ⇒ 逐跳复核走 PluginExecutor.HostInAllowedSet 口径
            var stepNoCred = NewStep(server.Url + "/redirect");
            using (var scope = NewScope())
            {
                var ex = await Assert.ThrowsAsync<PluginSandboxException>(
                    () => RunStepWithScopeAsync(exec, NewInstance("inst-lm079-nocred"), stepNoCred, scope, Hosts("[fd00::1]")));
                Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            }

            // 有凭据 ⇒ 逐跳复核走 PluginProcessor.HostMatches 口径
            var stepCred = NewStep(server.Url + "/redirect", headers: H("Authorization", "Bearer lm079"));
            using (var scope = NewScope())
            {
                var ex = await Assert.ThrowsAsync<PluginSandboxException>(
                    () => RunStepWithScopeAsync(exec, NewInstance("inst-lm079-cred"), stepCred, scope, Hosts("127.0.0.1", "[fd00::1]")));
                Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
            }

            // 只有**首跳**真的发出（第二跳根本没发出）⇒ 两个场景各 1 条
            Assert.Equal(2, server.RequestCount);
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private static Dictionary<string, string> H(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        private static PluginInstanceConfig NewInstance(string id) => new PluginInstanceConfig { Id = id };

        private static HashSet<string> Hosts(params string[] hosts)
            => new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);

        private static PluginExecutionStep NewStep(string url, string method = "GET", string body = "",
            Dictionary<string, string> headers = null, string encoding = "utf-8", int cacheMinutes = 0)
        {
            return new PluginExecutionStep
            {
                Id = "s1",
                Url = url,
                Method = method,
                Body = body,
                Headers = headers,
                ResponseEncoding = encoding,
                ResponseFormat = "text",
                Extract = new Dictionary<string, string> { ["v"] = "$" },
                CacheMinutes = cacheMinutes
            };
        }

        /// <summary>用契约值创建沙箱作用域（与生产入口同一口径）。</summary>
        private static PluginSandboxScope NewScope() => new PluginSandboxScope(
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            PluginSandboxLimits.MaxStepsPerExecution,
            PluginSandboxLimits.MaxTargetsPerStep);

        /// <summary>
        /// 驱动产品的**带作用域**步骤执行路径（私有方法，签名与生产一致），
        /// 返回抽取出来的响应正文。
        /// </summary>
        private static async Task<string> RunStepWithScopeAsync(PluginExecutor exec, PluginInstanceConfig inst,
            PluginExecutionStep step, PluginSandboxScope scope, HashSet<string> declaredHosts,
            CancellationToken token = default)
        {
            var context = new Dictionary<string, string>();
            var task = (Task)Method("ExecuteStepWithScopeAsync").Invoke(exec,
                new object[] { inst, step, context, "", token, scope, declaredHosts });
            await task.ConfigureAwait(false);
            return context.TryGetValue("v", out var v) ? v : null;
        }

        /// <summary>走**完整身份构造路径**算摘要（方法规范化 + URL 规范化 + Header 排序 + 摘要）。</summary>
        private static string IdentityDigest(string method, string url, string body,
            Dictionary<string, string> headers, string proxy, string responseEncoding)
        {
            var type = typeof(PluginExecutor).GetNestedType("RequestIdentity", BindingFlags.NonPublic);
            Assert.NotNull(type);
            var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var identity = ctor.Invoke(new object[] { "inst", "", "s1", method, url, body, headers, proxy, responseEncoding });
            var digestProp = type.GetProperty("Digest");
            Assert.NotNull(digestProp);
            return (string)digestProp.GetValue(identity);
        }

        private static ConcurrentDictionary<string, Lazy<Task<string>>> Inflight(PluginExecutor exec)
            => (ConcurrentDictionary<string, Lazy<Task<string>>>)GetField(exec, "_inflightRequests");

        /// <summary>
        /// 产品的"主机是否在允许集合内"判定（PluginExecutor 侧；私有方法，签名与生产一致）。
        /// </summary>
        private static bool HostInAllowedSet(string host, HashSet<string> allowedHosts)
        {
            var m = typeof(PluginExecutor).GetMethod("HostInAllowedSet", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (bool)m.Invoke(null, new object[] { host, allowedHosts });
        }

        /// <summary>
        /// 产品的"主机是否在允许集合内"判定（CryptoNative 侧；私有方法，签名与生产一致）。
        /// </summary>
        private static bool IsHostAllowed(string host, HashSet<string> allowedHosts)
        {
            var m = typeof(CryptoNative).GetMethod("IsHostAllowed", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (bool)m.Invoke(null, new object[] { host, allowedHosts });
        }

        private static object GetField(object target, string name)
        {
            var f = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return f.GetValue(target);
        }

        private static MethodInfo Method(string name)
        {
            var m = typeof(PluginExecutor).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.NotNull(m);
            return m;
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (predicate()) return true;
                await Task.Delay(25);
            }
            return predicate();
        }
    }
}
