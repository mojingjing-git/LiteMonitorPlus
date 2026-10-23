using System;
using System.Collections.Generic;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P2 批次 C · <b>LM-094</b>：<c>PluginProcessor.AddDeclaredHost</c> 截端口时
    /// 按**第一个冒号**截断 ⇒ **IPv6 字面量**被截成畸形串。
    ///
    /// ★ 缺陷事实（修复前的源码）：
    ///   <c>int colon = authority.IndexOf(':'); if (colon >= 0) authority = authority.Substring(0, colon);</c>
    ///   ⇒ 形如 <c>[fd00::1]</c> 的 IPv6 字面量被截成 <c>"[fd00"</c>
    ///   ⇒ 声明主机集合里出现一枚**畸形串** ⇒ 该 IPv6 主机永远无法被正确声明
    ///   （带凭据请求被一律拒绝）。
    ///
    /// ★ 危害方向：**偏严 / fail-closed**（不会误放行）。
    ///
    /// ★ 与 LM-079 的关系：LM-079 已修好 <c>PluginSandbox.StripPort</c>（按方括号判定 IPv6），
    ///   但**故意没碰这里**（避免跨批次冲突）—— 本批次补齐。本修复的 IPv6 语义必须与
    ///   <c>StripPort</c> **一致**：方括号内的冒号不算端口分隔符。
    ///
    /// 测试纪律：全部是纯函数断言（不触网）；每条用例都断言"畸形串**不得**出现"；
    /// 全部断言都真正执行，不存在早退 / 跳过。
    /// </summary>
    public class BatchCLM094DeclaredHostIPv6Tests
    {
        /// <summary>
        /// LM-094 核心断言：IPv6 字面量声明必须被**正确接受**。
        /// <para>修复前：声明集合 = {"[fd00"}（唯一的项就是畸形串）。</para>
        /// </summary>
        [Fact]
        public void LM094_IPv6字面量声明必须被正确接受()
        {
            var hosts = PluginProcessor.GetDeclaredHosts(Template("https://[fd00::1]/v1/quote"));

            Assert.Contains("[fd00::1]", hosts);

            // ★ 畸形串不得出现（残缺的 IPv6 字面量：'[' 没有配对的 ']'）
            AssertNoMalformedHost(hosts);
            Assert.DoesNotContain("[fd00", hosts);
            Assert.Single(hosts);
        }

        /// <summary>
        /// LM-094 核心断言：IPv6 字面量**整体带端口**时，整体就是主机名
        /// （"[fd00::1]:8443" 的主机是 "[fd00::1]"）。
        /// </summary>
        [Fact]
        public void LM094_IPv6字面量整体带端口时整体是主机名()
        {
            var hosts = PluginProcessor.GetDeclaredHosts(Template("https://[fd00::1]:8443/v1/quote"));

            Assert.Contains("[fd00::1]", hosts);
            AssertNoMalformedHost(hosts);
            Assert.Single(hosts);
        }

        /// <summary>
        /// LM-094 的**危害面**断言：声明了 <c>[fd00::1]</c> 时，发往该 IPv6 主机的
        /// **带凭据请求必须被放行**（修复前因声明是畸形串 "[fd00" 而被一律拒绝）。
        /// <para>反向护栏：另一个 IPv6 地址仍然必须被拒（修 IPv6 ≠ 放宽白名单，
        /// 更不得引入 LM-079 修掉的"地址碰撞"）。</para>
        /// </summary>
        [Fact]
        public void LM094_声明的IPv6主机的凭据请求必须被放行且不得匹配另一个IPv6地址()
        {
            var tmpl = Template("https://[fd00::1]/v1/quote");
            var declared = PluginProcessor.GetDeclaredHosts(tmpl);
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer lm094" };

            // 修复前：声明集合里是 "[fd00" ⇒ 该 IPv6 主机的带凭据请求被拒（插件功能不可用）
            PluginSandbox.EnsureCredentialTargetAllowed("http://[fd00::1]/v1/quote", headers, declared);

            // 另一个 IPv6 地址不得被匹配（LM-079 的地址碰撞护栏在此继续生效）
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("http://[fd00::2]/v1/quote", headers, declared));
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
        }

        /// <summary>
        /// LM-094 核心断言：两个不同的 IPv6 地址声明出来**不得互相碰撞**
        /// （修复前两者都会被截成 "[fd00"，声明集合完全无法区分）。
        /// </summary>
        [Fact]
        public void LM094_两个不同的IPv6声明不得互相碰撞()
        {
            var a = PluginProcessor.GetDeclaredHosts(Template("https://[fd00::1]/x"));
            var b = PluginProcessor.GetDeclaredHosts(Template("https://[fd00::2]/x"));

            Assert.Contains("[fd00::1]", a);
            Assert.Contains("[fd00::2]", b);
            Assert.NotEqual(a, b);

            var headers = new Dictionary<string, string> { ["X-Api-Key"] = "Bearer lm094" };
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureCredentialTargetAllowed("http://[fd00::2]/x", headers, a));
            Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, ex.Reason);
        }

        /// <summary>
        /// 回归护栏：**IPv4** 的 host 与 host:port 仍然等价（本次修复只改 IPv6 分支，
        /// IPv4 必须逐字不变）。
        /// </summary>
        [Fact]
        public void LM094_IPv4的host与host_port仍然等价()
        {
            var withPort = PluginProcessor.GetDeclaredHosts(Template("https://api.example.com:8443/v1"));
            var withoutPort = PluginProcessor.GetDeclaredHosts(Template("https://api.example.com/v1"));

            Assert.Contains("api.example.com", withPort);
            Assert.Contains("api.example.com", withoutPort);
            Assert.Equal(withoutPort, withPort);
            Assert.Single(withPort);
        }

        /// <summary>
        /// 回归护栏：**IPv4 的主机名不得被截出垃圾串**（"https://api.example.com:8443/…" 的
        /// 声明主机仍然是 "api.example.com"，不含端口、也不含任何残片）。
        /// </summary>
        [Fact]
        public void LM094_IPv4声明主机不得含端口或残片()
        {
            var hosts = PluginProcessor.GetDeclaredHosts(Template("https://api.example.com:8443/v1?q=1"));

            Assert.Contains("api.example.com", hosts);
            Assert.DoesNotContain(hosts, h => h.Contains(":"));
            Assert.Single(hosts);
        }

        // ---------------------------------------------------------------
        // 辅助
        // ---------------------------------------------------------------

        /// <summary>
        /// 断言声明主机集合里**没有畸形主机**：
        /// 以 '[' 开头（IPv6 字面量）就必须以 ']' 收尾；不是 IPv6 字面量的主机就**不得**含冒号
        /// （否则说明端口没被正确剥掉，或 IPv6 被截成了残片）。
        /// </summary>
        private static void AssertNoMalformedHost(HashSet<string> hosts)
        {
            foreach (var h in hosts)
            {
                bool bracketed = h.StartsWith("[", StringComparison.Ordinal);
                Assert.True(bracketed ? h.EndsWith("]", StringComparison.Ordinal) : !h.Contains(":"),
                    $"声明主机 '{h}' 是畸形串（IPv6 字面量必须成对带方括号；普通主机不得含冒号）");
            }
        }

        /// <summary>构造一条链式模板（单步，URL 即被测输入）。</summary>
        private static PluginTemplate Template(string stepUrl)
        {
            return new PluginTemplate
            {
                Id = "lm094.demo",
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
