using System;
using System.Collections.Generic;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P2 批次 C · <b>LM-080</b>：<c>PluginProcessor.AddDeclaredHost</c> 取 authority 时
    /// **只按 '/' 截断**，不处理 <c>'?'</c> / <c>'#'</c>。
    ///
    /// ★ 缺陷事实（源码见 <c>src/Plugins/PluginProcessor.cs</c> 的 <c>AddDeclaredHost</c>）：
    ///   无路径 URL（"https://host?x=1" / "https://host#frag"）的**查询串 / 片段**
    ///   被当成了主机名的一部分 ⇒ 声明集合里出现一枚**垃圾串**。
    ///
    /// ★ 危害方向：**偏严 / fail-closed**（不会误放行）：
    ///   主机名不可能包含 '?' 或 '#' ⇒ 被污染出来的声明项匹配不到任何真实主机
    ///   ⇒ 该主机的**带凭据请求被一律拒绝**（插件功能直接不可用）。
    ///
    /// 测试纪律：
    ///  · 全部是**纯函数**断言（不触网、不依赖服务器）；
    ///  · 每条用例都断言"垃圾串**不得**出现"，而不只是断言"正确主机在里面"
    ///    （否则"多出一个垃圾串"这种半修复也会变绿）；
    ///  · 全部断言都真正执行，不存在早退 / 跳过。
    /// </summary>
    public class BatchCLM080DeclaredHostQueryTests
    {
        /// <summary>
        /// LM-080 核心断言：无路径 URL 的**查询串**不得被当成主机名。
        /// <para>修复前：声明集合 = {"api.example.com?symbol=BTC&x=1"}（唯一的项就是垃圾串）。</para>
        /// </summary>
        [Fact]
        public void LM080_无路径URL的查询串不得被当成主机名()
        {
            var hosts = PluginProcessor.GetDeclaredHosts(Template("https://api.example.com?symbol=BTC&x=1"));

            Assert.Contains("api.example.com", hosts);

            // ★ 垃圾串不得出现（主机名不可能含 '?' / '#'）
            Assert.DoesNotContain(hosts, h => h.Contains("?") || h.Contains("#"));
            Assert.DoesNotContain("api.example.com?symbol=BTC&x=1", hosts);
            Assert.Single(hosts);
        }

        /// <summary>
        /// LM-080 核心断言：无路径 URL 的**片段**（fragment）同样不得被当成主机名。
        /// </summary>
        [Fact]
        public void LM080_无路径URL的片段不得被当成主机名()
        {
            var hosts = PluginProcessor.GetDeclaredHosts(Template("https://api.example.com#top"));

            Assert.Contains("api.example.com", hosts);
            Assert.DoesNotContain(hosts, h => h.Contains("?") || h.Contains("#"));
            Assert.DoesNotContain("api.example.com#top", hosts);
            Assert.Single(hosts);
        }

        /// <summary>
        /// LM-080 核心断言：查询串里**含协议头与斜杠**时（"…?redirect=https://evil.example.net/x"），
        /// 声明的主机必须仍然是**声明方自己的主机**，不得混入查询串里的任何内容。
        /// <para>修复前：authority 被截成 "api.example.com?redirect=https:" ⇒ 再按 ':' 截端口
        /// ⇒ 声明 "api.example.com?redirect=https"（一枚与真实主机毫无关系的垃圾串）。</para>
        /// </summary>
        [Fact]
        public void LM080_查询串里含协议头与斜杠时主机名仍然正确()
        {
            var hosts = PluginProcessor.GetDeclaredHosts(
                Template("https://api.example.com?redirect=https://evil.example.net/x"));

            Assert.Contains("api.example.com", hosts);
            Assert.DoesNotContain(hosts, h => h.Contains("evil"));
            Assert.DoesNotContain(hosts, h => h.Contains("?") || h.Contains("#"));
            Assert.Single(hosts);
        }

        /// <summary>
        /// LM-080 的**危害面**断言：模板声明的是"带查询串的无路径 URL"时，
        /// 该主机的**带凭据请求必须被放行**（修复前被一律拒绝）。
        /// <para>反向护栏：未声明的主机仍然必须被拒（修污染 ≠ 放宽白名单）。</para>
        /// </summary>
        [Fact]
        public void LM080_声明主机带查询串时该主机的凭据请求必须被放行()
        {
            var tmpl = Template("https://api.example.com?symbol=BTC");
            var declared = PluginProcessor.GetDeclaredHosts(tmpl);
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer lm080" };

            // 修复前：声明集合被污染 ⇒ 该主机的带凭据请求被拒（插件功能不可用）
            PluginSandbox.EnsureCredentialTargetAllowed("https://api.example.com/v1/quote", headers, declared);

            // 未声明的主机仍然被拒
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("https://evil.example.net/steal", headers, declared));
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
        }

        /// <summary>
        /// 回归护栏：**带路径 / 带端口**的 URL 的声明主机**逐字不变**（本次修复只补 '?' / '#'）。
        /// </summary>
        [Fact]
        public void LM080_带路径与端口的URL声明主机不变()
        {
            var hosts = PluginProcessor.GetDeclaredHosts(
                Template("https://api.example.com:8443/v1/quote?a=1#f"));

            Assert.Contains("api.example.com", hosts);
            Assert.DoesNotContain(hosts, h => h.Contains("?") || h.Contains("#"));
            Assert.Single(hosts);
        }

        /// <summary>
        /// 回归护栏：**模板变量**主机仍然**不构成显式声明**（本次修复不得放宽这条）。
        /// </summary>
        [Fact]
        public void LM080_模板变量主机仍然不构成显式声明()
        {
            Assert.Empty(PluginProcessor.GetDeclaredHosts(Template("{{target_url}}")));
            Assert.Empty(PluginProcessor.GetDeclaredHosts(Template("https://{{host}}/v1")));
        }

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------

        /// <summary>构造一条链式模板（单步，URL 即被测输入）。</summary>
        private static PluginTemplate Template(string stepUrl)
        {
            return new PluginTemplate
            {
                Id = "lm080.demo",
                Execution = new PluginExecution
                {
                    Type = "chain",
                    Url = "",
                    Steps = new List<PluginExecutionStep>
                    {
                        new PluginExecutionStep
                        {
                            Id = "s1",
                            Url = stepUrl,
                            Method = "GET"
                        }
                    }
                }
            };
        }
    }
}
