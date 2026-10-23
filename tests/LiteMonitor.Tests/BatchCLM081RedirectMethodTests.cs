using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
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
    /// ★ 返工记录：
    ///   · R4（主审补测后）：第一次修复把判据放宽成"非 GET/HEAD 即降级"，仍把 301/302 上的
    ///     PUT / DELETE 改写掉 ⇒ 与 .NET 原生**仍然不一致**（同一类残留）⇒ 判据已按
    ///     **逐状态码**实测口径重做。
    ///   · R5（主审驳回"配套前置修正"后）：上一版为了让 PUT / DELETE 可被端到端观测，
    ///     扩大了 <c>FetchRawAsync</c> 的方法白名单（PUT / DELETE / PATCH / OPTIONS）⇒
    ///     那是**安全面扩大**（第三方插件代码可用的方法面），与 LM-081 无关 ⇒ **已回退**，
    ///     方法解析**恢复原样**（只发 GET / POST / HEAD，其余回落 GET）。
    ///     PUT / DELETE 的断言因此改为**谓词级**（反射调用生产用的同一个私有判据）。
    ///
    /// ★ <b>生产不可达（谓词级用例的前提，须如实标注）</b>：
    ///   修复前的 <c>FetchRawAsync</c> 只把 **GET / POST / HEAD** 发出去，
    ///   <b>PUT / DELETE / PATCH / … 一律被发成 GET</b> ⇒ 这些方法在**生产路径上不存在** ⇒
    ///   "301/302 上 PUT 保持原方法"这条规则在生产中**没有实际效果**；
    ///   对它们的断言是为**语义对齐 .NET** 而钉住的**防御性**判据。
    ///   生产可达的 **HEAD / GET / POST** 三条另有**端到端**断言（服务端实际收到的方法）。
    ///
    /// 测试纪律：只连 127.0.0.1 回环服务器（不触公网）；端到端用例断言"服务器**实际收到**的方法"，
    /// 谓词级用例直接断言生产用的同一个判据；全部断言都真正执行，不存在早退 / 跳过。
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
        /// LM-081 返工（R5）核心断言（**谓词级**）：**PUT / DELETE 在 301/302 上不降级**。
        /// <para>★ .NET 原生实测（主审补测 <c>.artifacts/lm081-probe-ext-evidence.txt</c>）：
        /// <c>301 PUT -&gt; observed=[PUT /301 | PUT /final] finalMethod=PUT</c>、
        /// <c>302 PUT -&gt; finalMethod=PUT</c>、<c>301/302 DELETE -&gt; finalMethod=DELETE</c>
        /// ⇒ **301/302 只降级 POST**，PUT / DELETE 保持原方法。</para>
        /// <para>★ <b>生产不可达</b>：修复前的 <c>FetchRawAsync</c> 只把 **GET / POST / HEAD** 发出去
        /// （PUT / DELETE / PATCH / … 一律被发成 GET），故这些方法在**生产路径上不存在**；
        /// 本条是为**语义对齐 .NET** 而钉住的**防御性**判据 —— 用反射直接断言生产用的同一个
        /// 私有判据 <c>RewritesMethodOnRedirect</c>，不经由"能不能发出该方法"这一步。</para>
        /// </summary>
        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        public void LM081_谓词_301与302上PUT与DELETE不降级(int status)
        {
            Assert.False(RewritesMethodOnRedirect(status, HttpMethod.Put),
                $"301/302 不得把 PUT 降级为 GET（.NET 原生：PUT 保持原方法）");
            Assert.False(RewritesMethodOnRedirect(status, HttpMethod.Delete),
                $"301/302 不得把 DELETE 降级为 GET（.NET 原生：DELETE 保持原方法）");
        }

        /// <summary>
        /// LM-081 返工（R5）核心断言（**谓词级**）：**303 上的 PUT / DELETE 必须降级为 GET**。
        /// <para>★ .NET 原生实测：<c>303 PUT -&gt; observed=[PUT /303 | GET /final] finalMethod=GET</c>、
        /// <c>303 DELETE -&gt; observed=[DELETE /303 | GET /final] finalMethod=GET</c>
        /// ⇒ 303 降级**一切非 GET/HEAD** 的方法（与 301/302 只降级 POST 不同）。</para>
        /// <para>★ <b>生产不可达</b>（同上）：PUT / DELETE 在生产路径上不会被发出；
        /// 本条是**防御性**的语义对齐判据。</para>
        /// </summary>
        [Theory]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public void LM081_谓词_303上PUT与DELETE必须降级为GET(string method)
        {
            Assert.True(RewritesMethodOnRedirect(303, MethodOf(method)),
                $"303 应把 {method} 降级为 GET（.NET 原生：303 降级一切非 GET/HEAD 的方法）");
        }

        /// <summary>
        /// LM-081 返工（R5）的**回归护栏**（谓词级）：**307 / 308 上一律不降级**。
        /// <para>★ .NET 原生实测：<c>307 PUT -&gt; finalMethod=PUT</c>、<c>308 DELETE -&gt; finalMethod=DELETE</c>、
        /// <c>307 POST -&gt; finalMethod=POST</c> ⇒ 307/308 全部方法保持原方法与正文。</para>
        /// </summary>
        [Theory]
        [InlineData(307)]
        [InlineData(308)]
        public void LM081_谓词_307与308上一切方法都不降级(int status)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Head,
                                           HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            {
                Assert.False(RewritesMethodOnRedirect(status, method),
                    $"{status} 不得降级 {method}（.NET 原生：307/308 全部保持原方法与正文）");
            }
        }

        /// <summary>
        /// LM-081 返工（R5）的**全矩阵**断言（谓词级）：逐条对应
        /// <c>.artifacts/lm081-probe-ext-evidence.txt</c> 的实测矩阵
        /// （5 个状态码 × 5 个方法 = 25 条），"是否降级为 GET"必须与 .NET 原生**逐格一致**。
        /// <para>★ <b>生产不可达</b>：GET / POST / HEAD 三行在生产路径上可达（且另有端到端断言）；
        /// PUT / DELETE 两行在生产路径上不可达（<c>FetchRawAsync</c> 只发 GET/POST/HEAD），
        /// 属**防御性**的语义对齐判据。</para>
        /// </summary>
        [Theory]
        [InlineData(301, "HEAD", false)]
        [InlineData(301, "GET", false)]
        [InlineData(301, "POST", true)]
        [InlineData(301, "PUT", false)]
        [InlineData(301, "DELETE", false)]
        [InlineData(302, "HEAD", false)]
        [InlineData(302, "GET", false)]
        [InlineData(302, "POST", true)]
        [InlineData(302, "PUT", false)]
        [InlineData(302, "DELETE", false)]
        [InlineData(303, "HEAD", false)]
        [InlineData(303, "GET", false)]
        [InlineData(303, "POST", true)]
        [InlineData(303, "PUT", true)]
        [InlineData(303, "DELETE", true)]
        [InlineData(307, "HEAD", false)]
        [InlineData(307, "GET", false)]
        [InlineData(307, "POST", false)]
        [InlineData(307, "PUT", false)]
        [InlineData(307, "DELETE", false)]
        [InlineData(308, "HEAD", false)]
        [InlineData(308, "GET", false)]
        [InlineData(308, "POST", false)]
        [InlineData(308, "PUT", false)]
        [InlineData(308, "DELETE", false)]
        public void LM081_谓词_与dotnet实测矩阵逐条一致(int status, string method, bool expectRewrite)
        {
            Assert.Equal(expectRewrite, RewritesMethodOnRedirect(status, MethodOf(method)));
        }

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------

        /// <summary>
        /// 直接调用生产用的**同一个**私有判据 <c>PluginExecutor.RewritesMethodOnRedirect</c>
        /// （private static；反射取用，签名与生产一致），返回"该重定向是否改写方法"。
        /// <para>★ 用于**生产不可达**的方法（PUT / DELETE / PATCH）：这些方法在生产路径上
        /// 不会被发出（<c>FetchRawAsync</c> 只发 GET/POST/HEAD），但判据仍必须与 .NET 原生对齐。</para>
        /// </summary>
        private static bool RewritesMethodOnRedirect(int status, HttpMethod method)
        {
            var m = typeof(PluginExecutor).GetMethod("RewritesMethodOnRedirect",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (bool)m.Invoke(null, new object[] { (HttpStatusCode)status, method });
        }

        /// <summary>方法名 → <see cref="HttpMethod"/>（仅覆盖实测矩阵里出现的方法）。</summary>
        private static HttpMethod MethodOf(string name)
        {
            switch (name)
            {
                case "GET": return HttpMethod.Get;
                case "POST": return HttpMethod.Post;
                case "HEAD": return HttpMethod.Head;
                case "PUT": return HttpMethod.Put;
                case "DELETE": return HttpMethod.Delete;
                case "PATCH": return HttpMethod.Patch;
                default: throw new ArgumentOutOfRangeException(nameof(name), name, "未覆盖的方法");
            }
        }

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
