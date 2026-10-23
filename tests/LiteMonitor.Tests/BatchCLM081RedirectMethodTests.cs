using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P2 批次 C · <b>LM-081</b>：<c>PluginExecutor</c> 的**手动重定向**按状态码
    /// **无条件**把方法设为 GET ⇒ <b>HEAD 语义丢失</b>。
    ///
    /// ★ 缺陷事实（修复前的源码，<c>src/Plugins/PluginExecutor.cs</c>）：
    ///   <c>RewritesMethodOnRedirect(status)</c> 的判据**只看状态码**（301/302/303），
    ///   完全不看当前方法 ⇒ HEAD 也被改成 GET。
    ///
    /// ★ 口径依据（.NET 原生重定向的**本机实测**，.NET SDK 8.0.426，裸 TcpListener 自写
    ///   HTTP/1.1 响应，只监听 127.0.0.1 回环）：
    ///   原始探针 <c>.artifacts/lm081-probe-evidence.txt</c>（HEAD / GET / POST × 301/302/303）：
    ///     301 HEAD -&gt; observed=[HEAD /301 | HEAD /final]   finalMethod=HEAD
    ///     301 GET  -&gt; observed=[GET  /301 | GET  /final]   finalMethod=GET
    ///     301 POST -&gt; observed=[POST /301 | GET  /final]   finalMethod=GET
    ///     302 HEAD -&gt; observed=[HEAD /302 | HEAD /final]   finalMethod=HEAD
    ///     303 HEAD -&gt; observed=[HEAD /303 | HEAD /final]   finalMethod=HEAD
    ///   扩展探针 <c>.artifacts/lm081-probe-ext-evidence.txt</c>（主审补测：PUT / DELETE × 301/302/303/307/308）：
    ///     301 PUT    -&gt; observed=[PUT /301 | PUT /final]        finalMethod=PUT
    ///     301 DELETE -&gt; observed=[DELETE /301 | DELETE /final] finalMethod=DELETE
    ///     303 PUT    -&gt; observed=[PUT /303 | GET /final]        finalMethod=GET
    ///     303 DELETE -&gt; observed=[DELETE /303 | GET /final]     finalMethod=GET
    ///     307 / 308  =&gt; 全部方法保持原方法与正文
    ///   ⇒ .NET 原生**逐状态码**的真实口径：
    ///     · <b>301 / 302</b>：<b>只有 POST</b> 被降级为 GET（并丢弃正文）；
    ///       <b>PUT / DELETE / HEAD 等其他方法一律保持原方法</b>（正文也保留并重发）；
    ///     · <b>303</b>：<b>非 GET / HEAD</b> 的方法一律降级为 GET（并丢弃正文）；
    ///     · <b>307 / 308</b>：全部保持原方法与正文。
    ///
    /// ★ 缺陷事实（修复前的源码，<c>src/Plugins/PluginExecutor.cs</c>）：
    ///   <c>RewritesMethodOnRedirect(status)</c> 的判据**只看状态码**（301/302/303），
    ///   完全不看当前方法 ⇒ HEAD 也被改成 GET。
    ///
    /// ★ 危害：
    ///   ① 注释与实现不符（注释是本仓现有的唯一口径依据）；
    ///   ② HEAD 被降级成 GET ⇒ "只要响应头"的请求会把正文整个下载回来
    ///      （白耗 2 MiB 响应体上限与并发槽位）。
    ///
    /// ★ 返工记录（R4，主审补测后）：第一次修复把判据放宽成"非 GET/HEAD 即降级"，
    ///   仍把 301/302 上的 PUT / DELETE 改写掉 ⇒ 与 .NET 原生**仍然不一致**（同一类残留）。
    ///   本文件现已按**逐状态码**实测口径重做，并补齐 PUT / DELETE 的端到端用例。
    ///
    /// 测试纪律：只连 127.0.0.1 回环服务器（不触公网）；断言"服务器**实际收到**的方法"，
    /// 而不是断言内部状态；全部断言都真正执行，不存在早退 / 跳过。
    /// </summary>
    public class BatchCLM081RedirectMethodTests
    {
        /// <summary>
        /// LM-081 核心断言：**HEAD 在 301/302/303 上必须保持 HEAD**（不得被改成 GET）。
        /// <para>修复前：第二跳的方法是 "GET"。</para>
        /// </summary>
        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(303)]
        public async Task LM081_HEAD在301_302_303上必须保持HEAD(int status)
        {
            var (host, exec) = NewHostAndExec(status);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-head-" + status);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: "HEAD"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok, "HEAD 请求的重定向链应当正常完成");
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal("HEAD", requests[0].Method);
                // ★ 第二跳必须仍然是 HEAD
                Assert.Equal("HEAD", requests[1].Method);
            }
        }

        /// <summary>
        /// LM-081 的**正向护栏**：POST 在 301/302/303 上**必须降级为 GET** 且丢弃正文
        /// （.NET 原生行为；修复不得把这一半改坏）。
        /// </summary>
        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(303)]
        public async Task LM081_POST在301_302_303上必须降级为GET且丢弃正文(int status)
        {
            var (host, exec) = NewHostAndExec(status);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-post-" + status);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: "POST", body: "{\"a\":1}"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok);
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal("POST", requests[0].Method);
                // ★ 第二跳是 GET 且正文被丢弃
                Assert.Equal("GET", requests[1].Method);
                Assert.Equal("", requests[1].Body);
            }
        }

        /// <summary>
        /// LM-081 的**正向护栏**：GET 在 301/302/303 上保持 GET。
        /// </summary>
        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(303)]
        public async Task LM081_GET在301_302_303上必须保持GET(int status)
        {
            var (host, exec) = NewHostAndExec(status);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-get-" + status);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: "GET"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok);
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal("GET", requests[0].Method);
                Assert.Equal("GET", requests[1].Method);
            }
        }

        /// <summary>
        /// LM-081 的**回归护栏**：307 / 308 一律保留方法与正文
        /// （HEAD 也必须保持 HEAD —— 307/308 本来就不该改写方法）。
        /// </summary>
        [Theory]
        [InlineData(307)]
        [InlineData(308)]
        public async Task LM081_307与308一律保留方法(int status)
        {
            var (host, exec) = NewHostAndExec(status);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-307-" + status);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: "HEAD"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok);
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal("HEAD", requests[0].Method);
                Assert.Equal("HEAD", requests[1].Method);
            }
        }

        /// <summary>
        /// LM-081 返工（R4）核心断言：**PUT 在 301/302 上必须保持 PUT**。
        /// <para>★ .NET 原生实测（主审补测 <c>.artifacts/lm081-probe-ext-evidence.txt</c>）：
        /// <c>301 PUT -&gt; observed=[PUT /301 | PUT /final] finalMethod=PUT</c>、
        /// <c>302 PUT -&gt; finalMethod=PUT</c> ⇒ **301/302 只降级 POST**，PUT 保持原方法。</para>
        /// <para>修复前的判据（非 GET/HEAD 即降级）会把第二跳改成 GET ⇒ 服务端实际收到 GET。</para>
        /// </summary>
        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        public async Task LM081_PUT在301_302上必须保持PUT(int status)
        {
            var (host, exec) = NewHostAndExec(status);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-put-" + status);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: "PUT", body: "{\"a\":1}"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok, "PUT 请求的重定向链应当正常完成");
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal("PUT", requests[0].Method);
                // ★ 第二跳必须仍然是 PUT（.NET 原生：301/302 只降级 POST）
                Assert.Equal("PUT", requests[1].Method);
            }
        }

        /// <summary>
        /// LM-081 返工（R4）核心断言：**DELETE 在 301/302 上必须保持 DELETE**。
        /// <para>★ .NET 原生实测：<c>301 DELETE -&gt; finalMethod=DELETE</c>、<c>302 DELETE -&gt; finalMethod=DELETE</c>。</para>
        /// </summary>
        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        public async Task LM081_DELETE在301_302上必须保持DELETE(int status)
        {
            var (host, exec) = NewHostAndExec(status);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-delete-" + status);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: "DELETE"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok, "DELETE 请求的重定向链应当正常完成");
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal("DELETE", requests[0].Method);
                // ★ 第二跳必须仍然是 DELETE
                Assert.Equal("DELETE", requests[1].Method);
            }
        }

        /// <summary>
        /// LM-081 返工（R4）核心断言：**303 上的 PUT / DELETE 必须降级为 GET**。
        /// <para>★ .NET 原生实测：<c>303 PUT -&gt; observed=[PUT /303 | GET /final] finalMethod=GET</c>、
        /// <c>303 DELETE -&gt; observed=[DELETE /303 | GET /final] finalMethod=GET</c>
        /// ⇒ 303 降级**一切非 GET/HEAD** 的方法（与 301/302 只降级 POST 不同）。</para>
        /// </summary>
        [Theory]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task LM081_303上的PUT与DELETE必须降级为GET(string method)
        {
            var (host, exec) = NewHostAndExec(303);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-303-" + method);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: method, body: "{\"a\":1}"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok);
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal(method, requests[0].Method);
                // ★ 303 ⇒ 降级为 GET 且丢弃正文
                Assert.Equal("GET", requests[1].Method);
                Assert.Equal("", requests[1].Body);
            }
        }

        /// <summary>
        /// LM-081 返工（R4）的**回归护栏**：307 / 308 上 PUT / DELETE 保持原方法
        /// （★ .NET 原生实测：<c>307 PUT -&gt; finalMethod=PUT</c>、<c>308 DELETE -&gt; finalMethod=DELETE</c>）。
        /// </summary>
        [Theory]
        [InlineData(307, "PUT")]
        [InlineData(308, "DELETE")]
        public async Task LM081_307与308上的PUT与DELETE保持原方法(int status, string method)
        {
            var (host, exec) = NewHostAndExec(status);
            using (host)
            using (exec)
            {
                var inst = NewInstance("lm081-307-" + status + "-" + method);
                var tmpl = ChainTemplate(Step("s1", host.Url + "/start", method: method, body: "{\"a\":1}"));

                bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

                Assert.True(ok);
                var requests = host.Requests;
                Assert.Equal(2, requests.Count);
                Assert.Equal(method, requests[0].Method);
                Assert.Equal(method, requests[1].Method);
            }
        }

        /// <summary>
        /// LM-081 返工（R4）的**方法映射护栏**：步骤声明的方法必须**真的发出去**
        /// （修复前一切非 POST/HEAD 的方法都被发成 GET ⇒ PUT / DELETE 根本无从观测）。
        /// </summary>
        [Theory]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        [InlineData("PATCH")]
        [InlineData("OPTIONS")]
        public async Task LM081_步骤声明的方法必须真的发出去(string method)
        {
            using var host = new RedirectSandboxServer(
                _ => RedirectSandboxResponse.Text("NO-REDIRECT"), "127.0.0.1");
            using var exec = new PluginExecutor();

            var inst = NewInstance("lm081-method-" + method);
            var tmpl = ChainTemplate(Step("s1", host.Url + "/plain", method: method));

            bool ok = await exec.ExecuteInstanceAsync(inst, tmpl);

            Assert.True(ok);
            Assert.Equal(1, host.RequestCount);
            Assert.Equal(method, host.Requests[0].Method);
        }

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------

        /// <summary>
        /// 起一台回环服务器："/start" 返回指定状态码的重定向，"/target" 返回正文。
        /// </summary>
        private static (RedirectSandboxServer host, PluginExecutor exec) NewHostAndExec(int redirectStatus)
        {
            var host = new RedirectSandboxServer(req =>
                req.Path == "/start"
                    ? RedirectSandboxResponse.Redirect(redirectStatus, "/target")
                    : RedirectSandboxResponse.Text("AFTER-REDIRECT"), "127.0.0.1");
            return (host, new PluginExecutor());
        }

        private static PluginInstanceConfig NewInstance(string id) => new PluginInstanceConfig { Id = id };

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
