using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #610 N1：IP 排除规则的解析 / 规范化 / 去重 / 逐条拒绝。
    ///
    /// 纯输入测试：不启动 ETW、不碰网卡、不写任何文件。
    /// </summary>
    public class NetworkExclusionRulesTests
    {
        [Fact]
        public void 精确IP进入单播集合且多播单独成组()
        {
            var result = IpExclusionRules.CompileWithResult(new[] { "192.168.1.10", "239.255.42.99" });

            Assert.Empty(result.Errors);
            Assert.True(result.IsUsable);
            Assert.Equal(1, result.Rules.UnicastRuleCount);
            Assert.Equal(1, result.Rules.MulticastRuleCount);
            Assert.Equal(2, result.Rules.RuleCount);
        }

        [Fact]
        public void 多播组地址不与单播远端规则混淆()
        {
            var result = IpExclusionRules.CompileWithResult(new[] { "239.255.42.99" });
            IpKey group = IpKey.Parse("239.255.42.99");
            IpKey unicast = IpKey.Parse("10.0.0.1");

            Assert.True(result.Rules.IsExcludedRemote(group, out bool matchedGroup));
            Assert.True(matchedGroup);

            // 单播远端规则**不**会命中多播组
            var onlyUnicast = IpExclusionRules.CompileWithResult(new[] { "10.0.0.1" });
            Assert.False(onlyUnicast.Rules.IsExcludedRemote(group, out _));
            Assert.True(onlyUnicast.Rules.IsExcludedRemote(unicast, out _));
        }

        [Fact]
        public void 重复规则只保留一条()
        {
            var result = IpExclusionRules.CompileWithResult(new[] { "192.168.1.10", "192.168.1.10", " 192.168.1.10 " });

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.Rules.RuleCount);
        }

        [Fact]
        public void IPv6大小写与零压缩写法等价()
        {
            var a = IpExclusionRules.CompileWithResult(new[] { "2001:0DB8:0000:0000:0000:0000:0000:0001" });
            var b = IpExclusionRules.CompileWithResult(new[] { "2001:db8::1" });

            IpKey key = IpKey.Parse("2001:db8::1");
            Assert.True(a.Rules.IsExcludedRemote(key, out _));
            Assert.True(b.Rules.IsExcludedRemote(key, out _));
            Assert.Equal(a.Rules.UnicastRuleCount, b.Rules.UnicastRuleCount);
        }

        [Fact]
        public void IPv4映射的IPv6与等价IPv4统一匹配()
        {
            // "::ffff:192.168.1.5" 与 "192.168.1.5" 必须指向同一个匹配目标
            var mapped = IpExclusionRules.CompileWithResult(new[] { "::ffff:192.168.1.5" });
            var plain = IpExclusionRules.CompileWithResult(new[] { "192.168.1.5" });

            IpKey v4 = IpKey.Parse("192.168.1.5");
            Assert.True(mapped.Rules.IsExcludedRemote(v4, out _));
            Assert.True(plain.Rules.IsExcludedRemote(v4, out _));
            Assert.Equal(1, mapped.Rules.RuleCount);
            Assert.Equal(1, plain.Rules.RuleCount);

            // 反向：写 IPv4 规则也必须能命中 IPv4-mapped 的事件地址
            Assert.True(plain.Rules.IsExcludedRemote(IpKey.FromIPAddress(System.Net.IPAddress.Parse("::ffff:192.168.1.5")), out _));
        }

        [Theory]
        [InlineData("192.168.1.10:8080", "port")]
        [InlineData("example.com", "host name")]
        [InlineData("*.*.*.*", "Wildcard")]
        [InlineData("192.168.0.0/24", "CIDR")]
        [InlineData("not-an-ip", "valid IP")]
        [InlineData("999.1.1.1", "valid IP")]
        [InlineData("fe80::1%12", "scope id")]
        public void 不支持的写法被逐条拒绝并给出原因(string line, string reasonHint)
        {
            var result = IpExclusionRules.CompileWithResult(new[] { line });

            Assert.Single(result.Errors);
            Assert.False(string.IsNullOrWhiteSpace(result.Errors[0].Reason));
            Assert.Equal(0, result.Errors[0].LineNumber);
            Assert.Equal(line, result.Errors[0].Text);
            Assert.True(result.Errors[0].Reason.IndexOf(reasonHint, StringComparison.OrdinalIgnoreCase) >= 0,
                "原因里应能看出拒绝类型 (" + reasonHint + ")，实际: " + result.Errors[0].Reason);
            Assert.False(result.IsUsable);
        }

        [Fact]
        public void 错误行逐条标出行号且不静默忽略()
        {
            var lines = new[] { "192.168.1.10", "10.0.0.1:80", "example.com", "10.0.0.2" };
            var result = IpExclusionRules.CompileWithResult(lines);

            Assert.Equal(2, result.Errors.Count);
            Assert.Equal(1, result.Errors[0].LineNumber);
            Assert.Equal(2, result.Errors[1].LineNumber);
            // 有效的两行仍然进入集合（逐条拒绝，不是整批丢弃）
            Assert.Equal(2, result.Rules.RuleCount);
            // 但存在错误行 ⇒ 整体不可用（不得让用户以为"已经生效"）
            Assert.False(result.IsUsable);
        }

        [Fact]
        public void 规则数超过256被明确拒绝()
        {
            var lines = new List<string>();
            for (int i = 0; i < IpExclusionRules.MaxRuleCount + 5; i++)
            {
                lines.Add("10.1." + (i / 256) + "." + (i % 256));
            }

            var result = IpExclusionRules.CompileWithResult(lines);

            Assert.True(result.Rules.LimitExceeded);
            Assert.False(result.IsUsable);
            Assert.NotEmpty(result.Errors);
            Assert.True(result.Errors.Any(e => e.Reason.Contains("256")));
            Assert.Equal(IpExclusionRules.MaxRuleCount, result.Rules.RuleCount);
        }

        [Fact]
        public void 正好256条规则是可用的()
        {
            var lines = new List<string>();
            for (int i = 0; i < IpExclusionRules.MaxRuleCount; i++)
            {
                lines.Add("10.1." + (i / 256) + "." + (i % 256));
            }

            var result = IpExclusionRules.CompileWithResult(lines);

            Assert.Empty(result.Errors);
            Assert.False(result.Rules.LimitExceeded);
            Assert.True(result.IsUsable);
            Assert.Equal(256, result.Rules.RuleCount);
        }

        [Fact]
        public void 空规则集合不可用()
        {
            var result = IpExclusionRules.CompileWithResult(Array.Empty<string>());

            Assert.Empty(result.Errors);
            Assert.True(result.Rules.IsEmpty);
            Assert.False(result.IsUsable);
        }

        [Fact]
        public void 空白行被忽略且不算错误()
        {
            var result = IpExclusionRules.CompileWithResult(new[] { "  ", "", "192.168.1.10", "   " });

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.Rules.RuleCount);
        }

        [Fact]
        public void 规则版本在重新编译后变化()
        {
            var a = IpExclusionRules.CompileWithResult(new[] { "192.168.1.10" });
            var b = IpExclusionRules.CompileWithResult(new[] { "192.168.1.11" });

            Assert.NotEqual(a.Rules.Version, b.Rules.Version);
        }

        [Fact]
        public void 环回与链路本地地址可以被识别()
        {
            Assert.True(IpKey.Parse("127.0.0.1").IsLoopback);
            Assert.True(IpKey.Parse("::1").IsLoopback);
            Assert.True(IpKey.Parse("169.254.1.1").IsLinkLocal);
            Assert.True(IpKey.Parse("fe80::1").IsLinkLocal);
        }
    }
}
