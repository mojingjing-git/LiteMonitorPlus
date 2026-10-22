using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [LM-045] 插件执行的**重定向沙箱**回归测试。
    ///
    /// ★ 缺陷：<c>PluginExecutor</c> 用的 <c>HttpClient</c> **默认跟随重定向**
    ///   （<c>AllowAutoRedirect</c> 默认 true）⇒ 模板声明主机 A → 302 到**未声明**主机 B 时，
    ///   B 确实收到了产品发出的请求（<c>X-Api-Key</c> / <c>Cookie</c> 原样带过去），
    ///   而且 B 的响应数据进入了插件流水线并被用于构造后续请求。定级 P1。
    ///
    /// ★ 修复：关闭自动重定向 + 手工逐跳跟随（镜像 <c>PluginMarket.DownloadBytesAsync</c> 的仓内先例），
    ///   每一跳都**重建请求**并**逐跳复核目标主机**：
    ///   · 带凭据 ⇒ 每一跳都必须在**声明主机集合**内（否则该跳请求根本不发出）；
    ///   · 不带凭据 ⇒ 每一跳都必须在（声明主机集合 ∪ {原始直连目标主机}）内。
    ///
    /// ★ 本文件**只使用本地回环服务器**（127.0.0.1 / 127.0.0.2），**绝不联网**。
    ///   服务器辅助类见同目录 <c>PluginRedirectSandboxServer.cs</c>
    ///   （不复用 <c>PluginExecutorSandboxTests.TinyHttpServer</c> 的原因见该文件头注释：
    ///   它是 private 嵌套类，且它的响应写不出 <c>Location</c> 头 ⇒ 无法表达重定向）。
    /// </summary>
    public sealed class PluginRedirectSandboxTests
    {
        /// <summary>测试用秘密值：出现在请求头里，被断言"绝不出现在未声明主机上"。</summary>
        private const string SecretApiKey = "SECRET-APIKEY";

        /// <summary>测试用秘密值（Cookie）。</summary>
        private const string SecretCookie = "SECRET-COOKIE";

        /// <summary>未声明主机响应正文里的唯一标记：被断言"绝不进入插件流水线"。</summary>
        private const string UndeclaredBodyMarker = "UNDECLARED-BODY-MARKER-9f3a7c";

        // ==========================================================
        // a. 声明主机 302 → 未声明主机：未声明主机收到 0 个请求
        // ==========================================================

        /// <summary>
        /// a：声明主机 302 → **未声明**主机 ⇒ 该跳请求**根本不发出**（未声明主机收到 0 个请求），
        /// 步骤失败且原因码可判定为 <c>LM009_REDIRECT_TARGET_NOT_ALLOWED</c>。
        /// </summary>
        [Fact]
        public async Task a_声明主机302到未声明主机_未声明主机收到0个请求且原因码可判定()
        {
            using var undeclared = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text(UndeclaredBodyMarker), "127.0.0.2");
            using var declared = new RedirectSandboxServer(req =>
                req.Path == "/start"
                    ? RedirectSandboxResponse.Redirect(302, undeclared.Url + "/target")
                    : RedirectSandboxResponse.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-a");
            var tmpl = ChainTemplate(Step("s1", declared.Url + "/start"));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, undeclared.RequestCount);
            // 声明主机那一跳确实发出去了（重定向确实被识别，而不是请求根本没发）
            Assert.Equal(1, declared.RequestCount);
        }

        // ==========================================================
        // b. 带凭据头 302 → 未声明主机：P1 的核心证据
        // ==========================================================

        /// <summary>
        /// b（P1 核心）：带凭据头（<c>X-Api-Key</c> / <c>Cookie</c>）的请求，声明主机 302 →
        /// **未声明**主机 ⇒ 未声明主机收到 **0** 个请求，且它所见到的内容里**绝不含秘密值**；
        /// 失败原因码为 <c>LM009_CREDENTIAL_TARGET_NOT_ALLOWED</c>。
        /// </summary>
        [Fact]
        public async Task b_带凭据头302到未声明主机_未声明主机收到0个请求且从未见到秘密()
        {
            using var undeclared = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text(UndeclaredBodyMarker), "127.0.0.2");
            using var declared = new RedirectSandboxServer(req =>
                req.Path == "/start"
                    ? RedirectSandboxResponse.Redirect(302, undeclared.Url + "/target")
                    : RedirectSandboxResponse.Text("DECLARED-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-b");
            var tmpl = ChainTemplate(Step("s1", declared.Url + "/start", headers: CredentialHeaders()));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            // 凭据层的失败原因码与普通重定向目标不同：凭据只能发往**声明**主机
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);

            // ★ P1 的核心证据：未声明主机一个请求都没收到
            Assert.Equal(0, undeclared.RequestCount);
            AssertNoSecretSeen(undeclared);

            // 凭据确实发给了**声明**主机（说明不是"请求根本没发"这种假阳性）
            Assert.Equal(1, declared.RequestCount);
            Assert.True(declared.Requests[0].HasHeader("X-Api-Key"), "声明主机应当收到凭据头");
        }

        // ==========================================================
        // c. 声明主机 302 → 另一个已声明主机：放行
        // ==========================================================

        /// <summary>
        /// c：模板声明**两个**主机时，声明主机 A 302 → 已声明主机 B **放行**且成功。
        /// </summary>
        [Fact]
        public async Task c_声明主机302到另一个已声明主机_放行且成功()
        {
            using var hostB = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text("DECLARED-B-OK"), "127.0.0.2");
            using var hostA = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Redirect(302, hostB.Url + "/target"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-c");
            // 两个步骤分别落在两个主机上 ⇒ 声明主机集合 = {127.0.0.1, 127.0.0.2}
            var tmpl = ChainTemplate(
                Step("s1", hostA.Url + "/start"),
                Step("s2", hostB.Url + "/second"));

            Assert.Equal(2, PluginProcessor.GetDeclaredHosts(tmpl).Count);

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            // 重定向那一跳确实打到了已声明的 B
            Assert.Contains(hostB.Requests, r => r.Path == "/target");
        }

        // ==========================================================
        // d. 声明主机 302 → 同主机不同路径：放行
        // ==========================================================

        /// <summary>
        /// d：声明主机 302 → **同主机**不同路径（绝对地址）⇒ 放行且成功。
        /// </summary>
        [Fact]
        public async Task d_声明主机302到同主机不同路径_放行且成功()
        {
            RedirectSandboxServer host = null;
            host = new RedirectSandboxServer(req =>
                req.Path == "/start"
                    ? RedirectSandboxResponse.Redirect(302, host.Url + "/other")
                    : RedirectSandboxResponse.Text("SAME-HOST-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-d");
            var tmpl = ChainTemplate(Step("s1", host.Url + "/start"));

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal(2, host.RequestCount);
            Assert.Contains(host.Requests, r => r.Path == "/other");
        }

        // ==========================================================
        // e. 模板变量主机（声明集为空）302 → 同主机：放行
        // ==========================================================

        /// <summary>
        /// e：插件 URL 来自模板变量（<c>{{target_url}}</c>）⇒ **声明主机集合为空**
        /// （与内置插件 <c>UniversalAPI</c> 同构）⇒ 302 到**同主机**仍放行
        /// （"原始直连目标主机"这一例外）。
        /// </summary>
        [Fact]
        public async Task e_模板变量主机302到同主机_放行()
        {
            RedirectSandboxServer host = null;
            host = new RedirectSandboxServer(req =>
                req.Path == "/start"
                    ? RedirectSandboxResponse.Redirect(302, host.Url + "/other")
                    : RedirectSandboxResponse.Text("TEMPLATE-HOST-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-e");
            inst.InputValues["target_url"] = host.Url + "/start";
            var tmpl = ChainTemplate(Step("s1", "{{target_url}}"));

            // 前提：主机来自模板变量 ⇒ 不构成显式声明
            Assert.Empty(PluginProcessor.GetDeclaredHosts(tmpl));

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal(2, host.RequestCount);
            Assert.Contains(host.Requests, r => r.Path == "/other");
        }

        // ==========================================================
        // f. 模板变量主机（声明集为空）302 → 不同主机：拒绝
        // ==========================================================

        /// <summary>
        /// f：声明主机集合为空时，302 到**不同主机**必须拒绝，且该主机收到 0 个请求。
        /// </summary>
        [Fact]
        public async Task f_模板变量主机302到不同主机_拒绝且未声明主机收到0个请求()
        {
            using var other = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text(UndeclaredBodyMarker), "127.0.0.2");
            using var host = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Redirect(302, other.Url + "/target"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-f");
            inst.InputValues["target_url"] = host.Url + "/start";
            var tmpl = ChainTemplate(Step("s1", "{{target_url}}"));

            Assert.Empty(PluginProcessor.GetDeclaredHosts(tmpl));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, other.RequestCount);
        }

        // ==========================================================
        // g. 重定向链超过 5 跳：失败，原因码为重定向上限码
        // ==========================================================

        /// <summary>
        /// g：重定向链超过 5 跳（上限与 <c>PluginMarket.MaxDownloadRedirects</c> 对齐）⇒ 失败，
        /// 原因码为 <c>LM009_REDIRECT_LIMIT</c>，且请求数被上限约束（不会无限跟随）。
        /// </summary>
        [Fact]
        public async Task g_重定向链超过5跳_失败且原因码为重定向上限码()
        {
            const int chainLength = 12;   // 远大于 5 跳
            using var host = new RedirectSandboxServer(req =>
            {
                int n = ParseHopNumber(req.Path);
                return n < chainLength
                    ? RedirectSandboxResponse.Redirect(302, "/hop" + (n + 1))
                    : RedirectSandboxResponse.Text("TOO-MANY-HOPS");
            }, "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-g");
            var tmpl = ChainTemplate(Step("s1", host.Url + "/hop0"));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectLimit, ex.Reason);
            // 上限 5 跳 ⇒ 最多发出 6 个请求（与 PluginMarket 的 MaxDownloadRedirects + 1 同口径）
            Assert.True(host.RequestCount <= 6,
                "重定向链的请求数必须被上限约束，实际 " + host.RequestCount + " 次");
        }

        // ==========================================================
        // h. https → http 降级：拒绝
        // ==========================================================

        /// <summary>
        /// h：**https → http 降级**必须拒绝（凭据 / 正文会从加密信道掉到明文信道）。
        /// <para>★ <c>NOT_VERIFIED（端到端）</c>：本机**没有**可用的本地 TLS 服务器 ——
        /// <c>HttpListener</c> 的 https 前缀需要 <c>netsh http add sslcert</c>（管理员），
        /// 而自签证书会被生产代码**刻意不设置**的证书校验回调拒掉
        /// （<c>PluginExecutor.InitializeDefaultClient</c> 的注释明确说明这一点）。
        /// 因此这里用**反射直接调用生产用的同一个守卫方法**
        /// <c>PluginExecutor.EnsureRedirectSchemeAllowed</c> 验证规则本身；
        /// 端到端变体需要具备 TLS 环境的机器才能验证。</para>
        /// </summary>
        [Fact]
        public void h_https降级到http_拒绝()
        {
            // https → http：拒绝
            var ex = InvokeSchemeGuard(new Uri("https://a.example/x"), new Uri("http://a.example/y"));
            Assert.NotNull(ex);
            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);

            // 非 http(s) 协议：拒绝
            var ex2 = InvokeSchemeGuard(new Uri("http://a.example/x"), new Uri("file:///c:/windows/win.ini"));
            Assert.NotNull(ex2);
            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex2.Reason);

            // 同协议、以及 http → https 升级：放行
            Assert.Null(InvokeSchemeGuard(new Uri("https://a.example/x"), new Uri("https://a.example/y")));
            Assert.Null(InvokeSchemeGuard(new Uri("http://a.example/x"), new Uri("http://a.example/y")));
            Assert.Null(InvokeSchemeGuard(new Uri("http://a.example/x"), new Uri("https://a.example/y")));
        }

        /// <summary>
        /// h2（端到端）：重定向到**非 http(s)** 的目标必须拒绝，且该跳请求不发出。
        /// </summary>
        [Fact]
        public async Task h2_重定向到非http协议_拒绝()
        {
            using var host = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Redirect(302, "file:///c:/windows/win.ini"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-h2");
            var tmpl = ChainTemplate(Step("s1", host.Url + "/start"));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(1, host.RequestCount);
        }

        // ==========================================================
        // i. 相对 Location → 按当前 URI 解析
        // ==========================================================

        /// <summary>
        /// i：相对 <c>Location</c>（<c>/other?x=1</c>）必须**以当前 URI 为基准**解析，
        /// 解析结果落在同主机 ⇒ 放行且成功。
        /// </summary>
        [Fact]
        public async Task i_相对Location_按当前URI解析为同主机并放行()
        {
            using var host = new RedirectSandboxServer(req =>
                req.Path == "/start"
                    ? RedirectSandboxResponse.Redirect(302, "/other?x=1")
                    : RedirectSandboxResponse.Text("RELATIVE-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-i");
            var tmpl = ChainTemplate(Step("s1", host.Url + "/start"));

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal(2, host.RequestCount);
            Assert.Contains(host.Requests, r => r.Path == "/other" && r.Target.Contains("x=1"));
        }

        // ==========================================================
        // j. 方法 / 正文语义
        // ==========================================================

        /// <summary>
        /// j1：302 / 303 ⇒ 方法变 **GET** 且**正文丢弃**（与 .NET 原生重定向一致）。
        /// </summary>
        [Theory]
        [InlineData(302)]
        [InlineData(303)]
        public async Task j1_302与303_方法变GET且正文丢弃(int status)
        {
            var second = await RunPostAndCaptureRedirect(status, "{\"a\":1}");

            Assert.Equal("GET", second.Method);
            Assert.Equal("", second.Body);
        }

        /// <summary>
        /// j2：307 / 308 ⇒ **保留方法与正文**（与 .NET 原生重定向一致）。
        /// </summary>
        [Theory]
        [InlineData(307)]
        [InlineData(308)]
        public async Task j2_307与308_保留方法与正文(int status)
        {
            var second = await RunPostAndCaptureRedirect(status, "{\"a\":1}");

            Assert.Equal("POST", second.Method);
            Assert.Equal("{\"a\":1}", second.Body);
        }

        // ==========================================================
        // k. 未声明主机的响应正文不得进入插件流水线
        // ==========================================================

        /// <summary>
        /// k：未声明主机的**响应正文不得进入插件流水线**（LM-045 的"响应数据进入流水线"那半边）。
        /// <para>用私有的 <c>ExecuteStepWithScopeAsync</c>（与生产同一条代码）驱动，
        /// 这样测试自己持有 context ⇒ 能直接断言"提取出的输入值"里没有未声明主机的正文。</para>
        /// </summary>
        [Fact]
        public async Task k_未声明主机的响应正文不得进入插件流水线()
        {
            using var undeclared = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text(UndeclaredBodyMarker), "127.0.0.2");
            using var declared = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Redirect(302, undeclared.Url + "/target"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-k");
            var tmpl = ChainTemplate(Step("s1", declared.Url + "/start"));
            var declaredHosts = PluginProcessor.GetDeclaredHosts(tmpl);

            var context = new Dictionary<string, string>();
            using var scope = new PluginSandboxScope();

            var m = typeof(PluginExecutor).GetMethod("ExecuteStepWithScopeAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);

            await Assert.ThrowsAnyAsync<PluginSandboxException>(async () =>
            {
                var task = (Task)m.Invoke(exec, new object[]
                {
                    inst, tmpl.Execution.Steps[0], context, "", CancellationToken.None, scope, declaredHosts
                });
                await task.ConfigureAwait(false);
            });

            // 未声明主机一个请求都没收到
            Assert.Equal(0, undeclared.RequestCount);

            // ★ 它的响应正文也没有进入流水线（提取出的输入值里没有标记）
            Assert.DoesNotContain(UndeclaredBodyMarker, string.Join("|", context.Values));
            Assert.False(context.ContainsKey("v"), "失败的重定向不得产出任何提取值");
        }

        // ==========================================================
        // l. 回归护栏：不带重定向的正常请求仍成功
        // ==========================================================

        /// <summary>
        /// l（回归护栏）：**不带重定向**的正常请求仍然成功，提取值不变 ——
        /// 关闭自动重定向没有破坏既有沙箱行为。
        /// </summary>
        [Fact]
        public async Task l_回归_不带重定向的正常请求仍成功()
        {
            using var host = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text("PLAIN-OK"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-l");
            var tmpl = ChainTemplate(Step("s1", host.Url + "/plain"));

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal(1, host.RequestCount);

            // 提取值仍是服务器返回的原文
            var context = await RunStepWithOwnContext(exec, inst, tmpl.Execution.Steps[0],
                PluginProcessor.GetDeclaredHosts(tmpl));
            Assert.Equal("PLAIN-OK", context["v"]);
        }

        // ==========================================================
        // 代理路径：同样关闭自动重定向、同样逐跳复核
        // ==========================================================

        /// <summary>
        /// 代理路径与直连路径**同一口径**：经代理的重定向也必须逐跳复核目标主机，
        /// 未声明主机的请求**不得经代理发出**。
        /// </summary>
        [Fact]
        public async Task 代理路径_重定向到未声明主机_被拒且代理未收到第二跳()
        {
            using var other = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text(UndeclaredBodyMarker), "127.0.0.2");
            using var proxy = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Redirect(302, other.Url + "/target"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-proxy");
            // 目标主机写成一个"已声明"的名字；实际请求由代理接管（代理不转发，只回 302）
            var tmpl = ChainTemplate(Step("s1", "http://declared.example.test/start", proxy: proxy.Url));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => exec.ExecuteInstanceAsync(inst, tmpl));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            // 代理只收到第一跳；未声明主机从未出现在任何一跳里
            Assert.Equal(1, proxy.RequestCount);
            Assert.DoesNotContain(proxy.Requests, r => r.TargetHost == "127.0.0.2");
            Assert.Equal(0, other.RequestCount);
        }

        // ==========================================================
        // 白盒护栏：两个 handler 都必须关闭自动重定向
        // ==========================================================

        /// <summary>
        /// 白盒护栏：默认客户端与**代理**客户端的 handler 都必须
        /// <c>AllowAutoRedirect == false</c>（自动重定向是 LM-045 的根因）。
        /// </summary>
        [Fact]
        public void 白盒护栏_默认与代理客户端都关闭自动重定向()
        {
            using var exec = new PluginExecutor();

            var httpField = typeof(PluginExecutor).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(httpField);
            var defaultClient = (HttpClient)httpField.GetValue(exec);
            Assert.False(AllowAutoRedirectOf(defaultClient));

            var getClient = typeof(PluginExecutor).GetMethod("GetClient", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(getClient);
            var proxyClient = (HttpClient)getClient.Invoke(exec, new object[] { "http://127.0.0.1:1" });
            Assert.False(AllowAutoRedirectOf(proxyClient));
        }

        // ==========================================================
        // native:// 路径的闭环（LM-045 的另一半）
        // ==========================================================

        /// <summary>
        /// native1：<c>native://crypto</c> 的 fallback（**声明主机**）302 → **未声明主机**
        /// ⇒ 未声明主机收到 **0** 个请求，且该步骤**失败**（不是静默返回未声明主机的数据）。
        /// <para>★ 官方源全部指向本地服务器并返回 500 ⇒ 流程必然走到 fallback 分支（不触公网）。</para>
        /// </summary>
        [Fact]
        public async Task native1_fallback重定向到未声明主机_未声明主机收到0个请求且步骤失败()
        {
            using var other = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text(UndeclaredBodyMarker), "127.0.0.2");
            using var fallbackServer = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Redirect(302, other.Url + "/target"), "127.0.0.1");
            using var official = new RedirectSandboxServer(
                _ => new RedirectSandboxResponse { Status = 500 }, "127.0.0.1");
            using var exec = new PluginExecutor();
            using var ov = new NativeSourceOverride(official.Url);

            var step = NativeStep("native://crypto?symbol=" + UniqueNativeSymbol("FB")
                + "&fallback=" + fallbackServer.Url + "/fb");

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => RunNativeStepAsync(exec, NewInstance("lm045-native1"), step));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            // ★ 未声明主机一个请求都没收到
            Assert.Equal(0, other.RequestCount);
            // fallback 那一跳确实发出去了（说明是"重定向被拦"，不是"请求根本没发"）
            Assert.Equal(1, fallbackServer.RequestCount);
        }

        /// <summary>
        /// native2：fallback **同主机**重定向 ⇒ 放行且成功（守住"回退目标主机"这一例外）。
        /// </summary>
        [Fact]
        public async Task native2_fallback重定向到同主机_放行且成功()
        {
            using var official = new RedirectSandboxServer(
                _ => new RedirectSandboxResponse { Status = 500 }, "127.0.0.1");
            using var fallbackServer = new RedirectSandboxServer(req =>
                req.Path == "/fb2"
                    ? RedirectSandboxResponse.Json("{\"ok\":true}")
                    : RedirectSandboxResponse.Redirect(302, "/fb2"), "127.0.0.1");
            using var exec = new PluginExecutor();
            using var ov = new NativeSourceOverride(official.Url);

            var step = NativeStep("native://crypto?symbol=" + UniqueNativeSymbol("FBSAME")
                + "&fallback=" + fallbackServer.Url + "/fb");

            await RunNativeStepAsync(exec, NewInstance("lm045-native2"), step);

            Assert.Equal(2, fallbackServer.RequestCount);
        }

        /// <summary>
        /// native3：**官方源**同主机重定向 ⇒ 放行且成功
        /// （官方源是代码内建目标，允许集合 = 内建官方源主机 ∪ {该次请求的初始主机}）。
        /// </summary>
        [Fact]
        public async Task native3_官方源重定向到同主机_放行且成功()
        {
            using var official = new RedirectSandboxServer(req =>
                req.Path == "/bybit2"
                    ? RedirectSandboxResponse.Text(BybitTickerJson)
                    : RedirectSandboxResponse.Redirect(302, "/bybit2"), "127.0.0.1");
            using var exec = new PluginExecutor();
            using var ov = new NativeSourceOverride(official.Url);

            var step = NativeStep("native://crypto?symbol=" + UniqueNativeSymbol("OFSAME"));

            await RunNativeStepAsync(exec, NewInstance("lm045-native3"), step);

            Assert.Equal(2, official.RequestCount);
        }

        /// <summary>
        /// native4：**官方源**重定向到**未声明主机** ⇒ 被拒，且未声明主机收到 0 个请求。
        /// </summary>
        [Fact]
        public async Task native4_官方源重定向到未声明主机_被拒且未声明主机收到0个请求()
        {
            using var other = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text(UndeclaredBodyMarker), "127.0.0.2");
            using var official = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Redirect(302, other.Url + "/target"), "127.0.0.1");
            using var exec = new PluginExecutor();
            using var ov = new NativeSourceOverride(official.Url);

            var step = NativeStep("native://crypto?symbol=" + UniqueNativeSymbol("OFUND"));

            var ex = await Assert.ThrowsAnyAsync<PluginSandboxException>(
                () => RunNativeStepAsync(exec, NewInstance("lm045-native4"), step));

            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, ex.Reason);
            Assert.Equal(0, other.RequestCount);
            // 只发出第一跳
            Assert.Equal(1, official.RequestCount);
        }

        // ==========================================================
        // 辅助
        // ==========================================================

        private static Dictionary<string, string> CredentialHeaders() => new Dictionary<string, string>
        {
            ["X-Api-Key"] = SecretApiKey,
            ["Cookie"] = "sid=" + SecretCookie
        };

        /// <summary>断言这台服务器收到的任何内容里都没有秘密值。</summary>
        private static void AssertNoSecretSeen(RedirectSandboxServer server)
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
        }

        /// <summary>反射调用生产用的同一个协议守卫，返回它抛出的异常（没抛则返回 null）。</summary>
        private static PluginSandboxException InvokeSchemeGuard(Uri current, Uri next)
        {
            var m = typeof(PluginExecutor).GetMethod("EnsureRedirectSchemeAllowed",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            try
            {
                m.Invoke(null, new object[] { current, next });
                return null;
            }
            catch (TargetInvocationException tie)
            {
                return Assert.IsType<PluginSandboxException>(tie.InnerException);
            }
        }

        /// <summary>读取 HttpClient 内部 handler 的 AllowAutoRedirect。</summary>
        private static bool AllowAutoRedirectOf(HttpClient client)
        {
            HttpMessageHandler handler = null;
            // ★ handler 字段声明在**基类** HttpMessageInvoker 上 ⇒ 必须沿继承链往上找
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

        /// <summary>POST 一条会收到指定状态码重定向的请求，返回**第二跳**收到的请求。</summary>
        private static async Task<RedirectSandboxRequest> RunPostAndCaptureRedirect(int redirectStatus, string body)
        {
            using var host = new RedirectSandboxServer(req =>
                req.Path == "/start"
                    ? RedirectSandboxResponse.Redirect(redirectStatus, "/target")
                    : RedirectSandboxResponse.Text("AFTER-REDIRECT"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm045-j-" + redirectStatus);
            var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: "POST", body: body));

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            var requests = host.Requests;
            Assert.Equal(2, requests.Count);
            return requests[1];
        }

        /// <summary>走私有步骤路径（与生产同一条代码），返回测试自己持有的 context。</summary>
        private static async Task<Dictionary<string, string>> RunStepWithOwnContext(
            PluginExecutor exec, PluginInstanceConfig inst, PluginExecutionStep step, HashSet<string> declaredHosts)
        {
            var context = new Dictionary<string, string>();
            using var scope = new PluginSandboxScope();

            var m = typeof(PluginExecutor).GetMethod("ExecuteStepWithScopeAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);

            var task = (Task)m.Invoke(exec, new object[]
            {
                inst, step, context, "", CancellationToken.None, scope, declaredHosts
            });
            await task.ConfigureAwait(false);
            return context;
        }

        /// <summary>从请求路径里取 "/hopN" 的 N。</summary>
        private static int ParseHopNumber(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("/hop", StringComparison.Ordinal)) return -1;
            return int.TryParse(path.Substring(4), out int n) ? n : -1;
        }

        // ---------------------------------------------------------------
        // native:// 路径专用辅助
        // ---------------------------------------------------------------

        /// <summary>Bybit 形状的行情 JSON（官方源第一个就是 Bybit，解析器按该形状解析）。</summary>
        private const string BybitTickerJson =
            "{\"retCode\":0,\"retMsg\":\"OK\",\"result\":{\"list\":[" +
            "{\"symbol\":\"BTCUSDT\",\"lastPrice\":\"95000\",\"price24hPcnt\":\"0.0123\"," +
            "\"highPrice24h\":\"96000\",\"lowPrice24h\":\"94000\",\"turnover24h\":\"123456\"}]}}";

        /// <summary>
        /// 测试接缝：把 <see cref="CryptoNative"/> 的官方数据源 URL 指向本地回环服务器
        /// （避免用例触公网）。**只换"请求去哪"**，闸门仍是生产同一条路径。
        /// </summary>
        private sealed class NativeSourceOverride : IDisposable
        {
            private readonly Dictionary<string, string> _previous;

            public NativeSourceOverride(string baseUrl)
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

            public void Dispose()
            {
                CryptoNative.SourceUrlOverrides = _previous;
            }
        }

        /// <summary>
        /// 每次用例都用**唯一**的交易对：<c>CryptoNative</c> 的行情缓存是进程级静态的，
        /// 复用同一个交易对会让第二次执行直接命中缓存（请求数变 0 ⇒ 用例失去判别力）。
        /// </summary>
        private static string UniqueNativeSymbol(string tag) =>
            "LM045" + tag + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

        /// <summary>构造一条 native:// 步骤。</summary>
        private static PluginExecutionStep NativeStep(string url)
        {
            return new PluginExecutionStep
            {
                Id = "s1",
                Url = url,
                Method = "GET",
                ResponseEncoding = "utf-8",
                ResponseFormat = "text",
                Extract = new Dictionary<string, string> { ["v"] = "$" },
                CacheMinutes = 0
            };
        }

        /// <summary>驱动产品的步骤执行路径（私有方法，签名与生产一致）。</summary>
        private static async Task RunNativeStepAsync(PluginExecutor exec, PluginInstanceConfig inst,
            PluginExecutionStep step)
        {
            var context = new Dictionary<string, string>();
            var m = typeof(PluginExecutor).GetMethod("ExecuteStepAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            var task = (Task)m.Invoke(exec, new object[] { inst, step, context, "", CancellationToken.None });
            await task.ConfigureAwait(false);
        }

        private static PluginInstanceConfig NewInstance(string id)
            => new PluginInstanceConfig { Id = id };

        private static PluginExecutionStep Step(string id, string url, string method = "GET", string body = "",
            Dictionary<string, string> headers = null, string proxy = "")
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
                Extract = new Dictionary<string, string> { ["v"] = "$" },
                CacheMinutes = 0
            };
        }

        private static PluginTemplate ChainTemplate(params PluginExecutionStep[] steps)
        {
            return new PluginTemplate
            {
                Id = "tmpl",
                Meta = new PluginMeta { Name = "T" },
                Execution = new PluginExecution { Type = "chain", Steps = steps.ToList() },
                Outputs = new List<PluginOutput>()
            };
        }
    }
}
