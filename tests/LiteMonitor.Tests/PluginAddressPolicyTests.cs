using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [S02a] 地址分类与连接前预检的行为测试。
    /// 覆盖：IPv4/IPv6 全类别、v6 内嵌 v4 变体（v4-mapped/NAT64/6to4）、
    /// 整组判定（混合候选集）、解析失败拒绝、代理路径不本地解析、
    /// "允许发请求"与凭据检查分离、本地网络/代理授权器往返。
    /// </summary>
    [Collection("cfgfile")]
    public class PluginAddressPolicyTests : IDisposable
    {
        private static IPAddress IP(string s) => IPAddress.Parse(s);

        private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _savedResolver;

        public PluginAddressPolicyTests()
        {
            // 每个用例都从"无测试解析器"的干净状态开始，由各 Fact 自行设置。
            // 保存原值并在 Dispose 恢复：xunit 每个测试都新建实例，若只置空不恢复，
            // 会把"真实 DNS"泄漏给后续类（组合回归里 Reload 类因此挂死）。
            _savedResolver = PluginAddressPreflight.ResolverForTests;
            PluginAddressPreflight.ResolverForTests = null;
        }

        public void Dispose() => PluginAddressPreflight.ResolverForTests = _savedResolver;

        // ---------- 分类：IPv4 ----------

        // PluginAddressClass 是 internal：Theory 参数用枚举名字符串，体内 Parse。
        [Theory]
        [InlineData("8.8.8.8", "Public")]
        [InlineData("1.1.1.1", "Public")]
        [InlineData("172.32.0.1", "Public")]      // 172.16/12 之外
        [InlineData("100.128.0.1", "Public")]     // 100.64/10 之外
        [InlineData("198.20.0.1", "Public")]      // 198.18/15 之外
        [InlineData("127.0.0.1", "Loopback")]
        [InlineData("127.8.8.8", "Loopback")]
        [InlineData("10.1.2.3", "Private")]
        [InlineData("172.16.0.1", "Private")]
        [InlineData("172.31.255.255", "Private")]
        [InlineData("192.168.1.1", "Private")]
        [InlineData("100.64.0.1", "Private")]     // CGNAT
        [InlineData("100.127.255.255", "Private")]
        [InlineData("198.18.0.1", "Private")]     // benchmark
        [InlineData("198.19.255.255", "Private")]
        [InlineData("192.0.0.1", "Private")]      // 192.0.0.0/24
        [InlineData("192.0.2.1", "Private")]      // TEST-NET-1
        [InlineData("198.51.100.9", "Private")]   // TEST-NET-2
        [InlineData("203.0.113.9", "Private")]    // TEST-NET-3
        [InlineData("224.0.0.1", "Private")]      // 组播
        [InlineData("239.255.255.250", "Private")] // mDNS 组播
        [InlineData("240.0.0.1", "Private")]      // 保留
        [InlineData("255.255.255.255", "Private")] // 广播
        [InlineData("0.0.0.0", "Unspecified")]
        [InlineData("169.254.10.1", "LinkLocal")]
        [InlineData("169.254.169.254", "CloudMetadata")]
        public void Classify_IPv4(string text, string expected)
        {
            Assert.Equal(Enum.Parse<PluginAddressClass>(expected), PluginAddressPolicy.Classify(IP(text)));
        }

        // ---------- 分类：IPv6 ----------

        [Theory]
        [InlineData("2600::1", "Public")]
        [InlineData("2001:4860:4860::8888", "Public")]
        [InlineData("::1", "Loopback")]
        [InlineData("::", "Unspecified")]
        [InlineData("fe80::1", "LinkLocal")]
        [InlineData("febf::1", "LinkLocal")]      // fe80::/10 上界
        [InlineData("fec0::1", "Private")]        // fec0::/10 已废弃站点本地，保守按私网拒绝
        [InlineData("fd00::1", "Private")]        // ULA
        [InlineData("fc00::1", "Private")]
        [InlineData("ff02::1", "Private")]        // 组播
        [InlineData("fd00:ec2::254", "CloudMetadata")] // AWS IMDS v6
        public void Classify_IPv6(string text, string expected)
        {
            Assert.Equal(Enum.Parse<PluginAddressClass>(expected), PluginAddressPolicy.Classify(IP(text)));
        }

        // ---------- 分类：v6 内嵌 v4 变体 ----------

        [Theory]
        [InlineData("::ffff:8.8.8.8", "Public")]
        [InlineData("::ffff:127.0.0.1", "Loopback")]
        [InlineData("::ffff:192.168.1.1", "Private")]
        [InlineData("::ffff:169.254.169.254", "CloudMetadata")]
        [InlineData("::ffff:169.254.10.1", "LinkLocal")]
        [InlineData("64:ff9b::808:808", "Public")]         // NAT64 → 8.8.8.8
        [InlineData("64:ff9b::7f00:1", "Loopback")]        // NAT64 → 127.0.0.1
        [InlineData("64:ff9b::a9fe:a9fe", "CloudMetadata")] // NAT64 → 169.254.169.254
        [InlineData("2002:c0a8:101::", "Private")]         // 6to4 → 192.168.1.1
        [InlineData("2002:808:808::", "Public")]           // 6to4 → 8.8.8.8
        public void Classify_EmbeddedIPv4Variants(string text, string expected)
        {
            Assert.Equal(Enum.Parse<PluginAddressClass>(expected), PluginAddressPolicy.Classify(IP(text)));
        }

        [Fact]
        public void ForbiddenAlways_ExcludesMetadataLinkLocalUnspecified_AllowsPublicAndLocal()
        {
            Assert.True(PluginAddressPolicy.IsForbiddenAlways(PluginAddressClass.CloudMetadata));
            Assert.True(PluginAddressPolicy.IsForbiddenAlways(PluginAddressClass.LinkLocal));
            Assert.True(PluginAddressPolicy.IsForbiddenAlways(PluginAddressClass.Unspecified));
            Assert.False(PluginAddressPolicy.IsForbiddenAlways(PluginAddressClass.Loopback));
            Assert.False(PluginAddressPolicy.IsForbiddenAlways(PluginAddressClass.Private));
            Assert.False(PluginAddressPolicy.IsForbiddenAlways(PluginAddressClass.Public));

            Assert.True(PluginAddressPolicy.IsLocalScope(PluginAddressClass.Loopback));
            Assert.True(PluginAddressPolicy.IsLocalScope(PluginAddressClass.Private));
            Assert.False(PluginAddressPolicy.IsLocalScope(PluginAddressClass.Public));
        }

        // ---------- 连接前预检 ----------

        [Fact]
        public async Task Preflight_IpLiteralMetadata_DeniedWithoutDnsCall()
        {
            long before = Interlocked.Read(ref PluginAddressPreflight.ResolveCallCount);
            var ex = await Assert.ThrowsAsync<PluginSandboxException>(() =>
                PluginAddressPreflight.EnsureAddressPolicyAsync(
                    new Uri("http://169.254.169.254/latest"), null, "inst-1", CancellationToken.None));
            Assert.Equal(PluginSandboxReasons.AddressPolicyDenied, ex.Reason);
            Assert.Equal(before, Interlocked.Read(ref PluginAddressPreflight.ResolveCallCount));
        }

        [Fact]
        public async Task Preflight_IpLiteralLocal_WithoutAuthorization_Denied()
        {
            // 组合跑时其他回归类可能持有 LocalAuthOverrideForTests 作用域；
            // 本测试验证"未授权⇒拒绝"语义，运行期间显式清空 override。
            Func<string?, Uri, bool>? saved = PluginAddressPreflight.LocalAuthOverrideForTests;
            PluginAddressPreflight.LocalAuthOverrideForTests = null;
            try
            {
                var ex = await Assert.ThrowsAsync<PluginSandboxException>(() =>
                    PluginAddressPreflight.EnsureAddressPolicyAsync(
                        new Uri("http://192.168.1.1:8080/api"), null, "inst-unauthorized", CancellationToken.None));
                Assert.Equal(PluginSandboxReasons.LocalNetworkNotAllowed, ex.Reason);
            }
            finally
            {
                PluginAddressPreflight.LocalAuthOverrideForTests = saved;
            }
        }

        [Fact]
        public async Task Preflight_IpLiteralPublic_Passes()
        {
            await PluginAddressPreflight.EnsureAddressPolicyAsync(
                new Uri("https://9.9.9.9/x"), null, "inst-1", CancellationToken.None);
        }

        [Fact]
        public async Task Preflight_MixedCandidateSet_AnyForbiddenDeniesWholeGroup()
        {
            // 整组判定：公网候选存在也不能放行（DNS rebinding 常见混合组）。
            PluginAddressPreflight.ResolverForTests = (_, _) =>
                Task.FromResult(new[] { IP("8.8.8.8"), IP("169.254.169.254") });

            var ex = await Assert.ThrowsAsync<PluginSandboxException>(() =>
                PluginAddressPreflight.EnsureAddressPolicyAsync(
                    new Uri("http://rebind.example.com/x"), null, "inst-1", CancellationToken.None));
            Assert.Equal(PluginSandboxReasons.AddressPolicyDenied, ex.Reason);
        }

        [Fact]
        public async Task Preflight_MixedCandidateSet_LocalWithoutAuthorization_Denies()
        {
            PluginAddressPreflight.ResolverForTests = (_, _) =>
                Task.FromResult(new[] { IP("8.8.8.8"), IP("10.0.0.5") });

            // 同上：拒绝语义测试显式清空组合跑中可能存在的 override 作用域。
            Func<string?, Uri, bool>? saved = PluginAddressPreflight.LocalAuthOverrideForTests;
            PluginAddressPreflight.LocalAuthOverrideForTests = null;
            try
            {
                var ex = await Assert.ThrowsAsync<PluginSandboxException>(() =>
                    PluginAddressPreflight.EnsureAddressPolicyAsync(
                        new Uri("http://mixed.example.com/x"), null, "inst-1", CancellationToken.None));
                Assert.Equal(PluginSandboxReasons.LocalNetworkNotAllowed, ex.Reason);
            }
            finally
            {
                PluginAddressPreflight.LocalAuthOverrideForTests = saved;
            }
        }

        [Fact]
        public async Task Preflight_LocalCandidate_WithAuthorization_Passes()
        {
            PluginAddressPreflight.ResolverForTests = (_, _) =>
                Task.FromResult(new[] { IP("192.168.1.1") });
            string instanceId = "inst-local-" + Guid.NewGuid().ToString("N");
            var store = Path.Combine(AppContext.BaseDirectory, "s02a-local-" + Guid.NewGuid().ToString("N"), "auth.json");
            try
            {
                var authorizer = new PluginLocalNetworkAuthorizer(store);
                Assert.True(authorizer.AuthorizeForUser(instanceId, new Uri("http://192.168.1.1:8080")));
                PluginAddressPreflight.LocalAuthorizerForTests = authorizer;
                try
                {
                    await PluginAddressPreflight.EnsureAddressPolicyAsync(
                        new Uri("http://192.168.1.1:8080/api"), null, instanceId, CancellationToken.None);
                }
                finally
                {
                    PluginAddressPreflight.LocalAuthorizerForTests = null;
                }
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(store)!, true); } catch { }
            }
        }

        [Fact]
        public async Task Preflight_UnresolvableHost_Denied()
        {
            PluginAddressPreflight.ResolverForTests = (_, _) =>
                Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException());

            var ex = await Assert.ThrowsAsync<PluginSandboxException>(() =>
                PluginAddressPreflight.EnsureAddressPolicyAsync(
                    new Uri("http://nope.example.com/x"), null, "inst-1", CancellationToken.None));
            Assert.Equal(PluginSandboxReasons.TargetUnresolvable, ex.Reason);
        }

        [Fact]
        public async Task Preflight_EmptyCandidateSet_Denied()
        {
            PluginAddressPreflight.ResolverForTests = (_, _) => Task.FromResult(Array.Empty<IPAddress>());

            var ex = await Assert.ThrowsAsync<PluginSandboxException>(() =>
                PluginAddressPreflight.EnsureAddressPolicyAsync(
                    new Uri("http://empty.example.com/x"), null, "inst-1", CancellationToken.None));
            Assert.Equal(PluginSandboxReasons.TargetUnresolvable, ex.Reason);
        }

        [Fact]
        public async Task Preflight_ProxyPath_SkipsLocalResolution()
        {
            long before = Interlocked.Read(ref PluginAddressPreflight.ResolveCallCount);
            // 代理路径：主机名不做本地解析（解析发生在代理侧，企业代理本地解析失败不断网）。
            await PluginAddressPreflight.EnsureAddressPolicyAsync(
                new Uri("http://behind-proxy.example.com/x"), "http://127.0.0.1:8888", "inst-1", CancellationToken.None);
            Assert.Equal(before, Interlocked.Read(ref PluginAddressPreflight.ResolveCallCount));
        }

        [Fact]
        public async Task Preflight_ProxyPath_IpLiteralStillJudged()
        {
            // 代理路径的 IP 字面量仍分类（不依赖解析）。
            var ex = await Assert.ThrowsAsync<PluginSandboxException>(() =>
                PluginAddressPreflight.EnsureAddressPolicyAsync(
                    new Uri("http://169.254.169.254/x"), "http://127.0.0.1:8888", "inst-1", CancellationToken.None));
            Assert.Equal(PluginSandboxReasons.AddressPolicyDenied, ex.Reason);
        }

        // ---------- "允许发请求"检查（与凭据检查分离） ----------

        [Fact]
        public void RequestTarget_UnauthorizedHost_RejectedEvenWithoutCredentials()
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "api.example.com" };
            var ex = Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureRequestTargetAllowed("http://evil.example.com/x", allowed));
            Assert.Equal(PluginSandboxReasons.RequestTargetNotAllowed, ex.Reason);
        }

        [Fact]
        public void RequestTarget_DeclaredHost_Passes()
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "api.example.com", "https://mirror.example.org:8443" };
            PluginSandbox.EnsureRequestTargetAllowed("https://api.example.com/v1", allowed);
            PluginSandbox.EnsureRequestTargetAllowed("https://mirror.example.org:8443/v1", allowed);
        }

        [Fact]
        public void RequestTarget_AuthorizedOrigin_Passes()
        {
            // 用户授权记录以完整 origin 保存；MatchesAuthorizedOrigin 精确比较。
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "https://dyn.example.com:8443" };
            PluginSandbox.EnsureRequestTargetAllowed("https://dyn.example.com:8443/path", allowed);
            // 端口不同的同主机 origin 仍被拒绝（origin 级精确匹配）。
            Assert.Throws<PluginSandboxException>(() =>
                PluginSandbox.EnsureRequestTargetAllowed("https://dyn.example.com/path", allowed));
        }

        [Fact]
        public void RequestTarget_NullAllowedHosts_NoCheck()
        {
            // 无声明上下文（兼容旧测试/直连兜底）：不做集合复核。
            PluginSandbox.EnsureRequestTargetAllowed("https://anything.example.com/x", null);
        }

        // ---------- 本地网络授权器 ----------

        [Fact]
        public void LocalNetworkAuthorizer_HttpPrivateOrigin_RoundTrip()
        {
            string store = Path.Combine(AppContext.BaseDirectory, "s02a-local-" + Guid.NewGuid().ToString("N"), "auth.json");
            try
            {
                var authorizer = new PluginLocalNetworkAuthorizer(store);
                string instanceId = "inst-" + Guid.NewGuid().ToString("N");

                Assert.False(authorizer.IsLocalAuthorized(instanceId, new Uri("http://192.168.1.1:8080")));
                Assert.True(authorizer.AuthorizeForUser(instanceId, new Uri("http://192.168.1.1:8080")));
                Assert.True(authorizer.IsLocalAuthorized(instanceId, new Uri("http://192.168.1.1:8080")));
                // 不同端口 = 不同 origin。
                Assert.False(authorizer.IsLocalAuthorized(instanceId, new Uri("http://192.168.1.1:9090")));
                // 持久化：新实例读取同一存储。
                Assert.True(new PluginLocalNetworkAuthorizer(store).IsLocalAuthorized(instanceId, new Uri("http://192.168.1.1:8080")));
                // 实例删除清理。
                Assert.True(authorizer.RevokeAll(instanceId));
                Assert.False(authorizer.IsLocalAuthorized(instanceId, new Uri("http://192.168.1.1:8080")));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(store)!, true); } catch { }
            }
        }

        // ---------- 代理确认授权器 ----------

        [Fact]
        public void ProxyAuthorizer_NormalizeAndRoundTrip()
        {
            Assert.True(PluginProxyAuthorizer.TryNormalizeProxyEndpoint("1.2.3.4:8080", out string bare));
            Assert.Equal("http://1.2.3.4:8080", bare);
            Assert.True(PluginProxyAuthorizer.TryNormalizeProxyEndpoint("HTTP://Proxy.Example.COM", out string defaultPort));
            Assert.Equal("http://proxy.example.com", defaultPort);
            Assert.True(PluginProxyAuthorizer.TryNormalizeProxyEndpoint("http://[::1]:8080", out string v6));
            Assert.Equal("http://[::1]:8080", v6);
            Assert.False(PluginProxyAuthorizer.TryNormalizeProxyEndpoint("", out _));
            Assert.False(PluginProxyAuthorizer.TryNormalizeProxyEndpoint("ftp://x", out _));

            string store = Path.Combine(AppContext.BaseDirectory, "s02a-proxy-" + Guid.NewGuid().ToString("N"), "auth.json");
            try
            {
                // 语义测试要求真实存储查询：显式清空组合跑中可能存在的 override 作用域。
                Func<string, string, bool>? saved = PluginProxyAuthorizer.AuthorizationOverrideForTests;
                PluginProxyAuthorizer.AuthorizationOverrideForTests = null;
                try
                {
                    var authorizer = new PluginProxyAuthorizer(store);
                    string instanceId = "inst-" + Guid.NewGuid().ToString("N");

                    Assert.False(authorizer.IsAuthorized(instanceId, "1.2.3.4:8080"));
                    Assert.True(authorizer.AuthorizeForUser(instanceId, "1.2.3.4:8080"));
                    Assert.True(authorizer.IsAuthorized(instanceId, "http://1.2.3.4:8080")); // 规范化后同一端点
                    Assert.False(authorizer.IsAuthorized(instanceId, "5.6.7.8:9090"));      // 端点变化 = 新端点
                    Assert.True(authorizer.RevokeAll(instanceId));
                    Assert.False(authorizer.IsAuthorized(instanceId, "1.2.3.4:8080"));
                }
                finally
                {
                    PluginProxyAuthorizer.AuthorizationOverrideForTests = saved;
                }
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(store)!, true); } catch { }
            }
        }

        // ---------- 授权变更 → 版本号推进（连接池失效依据） ----------

        [Fact]
        public void NetworkPolicy_VersionAdvancesOnAuthorizationChange()
        {
            string store = Path.Combine(AppContext.BaseDirectory, "s02a-policy-" + Guid.NewGuid().ToString("N"), "auth.json");
            try
            {
                long before = PluginNetworkPolicy.Version;
                var authorizer = new PluginProxyAuthorizer(store);
                authorizer.AuthorizeForUser("inst-policy", "1.2.3.4:8080");
                Assert.True(PluginNetworkPolicy.Version > before);
                long afterAuthorize = PluginNetworkPolicy.Version;
                authorizer.RevokeAll("inst-policy");
                Assert.True(PluginNetworkPolicy.Version > afterAuthorize);
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(store)!, true); } catch { }
            }
        }
    }
}
