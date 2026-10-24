using System.Collections;
using System.Net;
using System.Net.Http;
using System.Reflection;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using LiteMonitor.src.SystemServices.InfoService;
using Xunit;

namespace LiteMonitor.Tests;

[Collection("cfgfile")]
public class ReviewPluginHostNormalizationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly Dictionary<string, string> Credentials = new() { ["Authorization"] = "Bearer review" };

    [Theory]
    [InlineData("https://[2001:0DB8:0000:0000:0000:0000:0000:0001]/x", "[2001:db8::1]", "[2001:db8::2]")]
    [InlineData("https://user:pass@[2001:0DB8:0:0:0:0:0:0001]:8443/x", "[2001:db8::1]", "[2001:db8::2]")]
    [InlineData("https://user:p%40ss@API.EXAMPLE.TEST:8443/x", "api.example.test", "api.example.test.evil")]
    [InlineData("https://user:pass@127.0.0.1:8443/x", "127.0.0.1", "127.0.0.2")]
    public void EquivalentHostsUseTheSameCanonicalPolicyInCredentialsHttpAndNative(string declaration, string host, string other)
    {
        var allowed = PluginProcessor.GetDeclaredHosts(Template(declaration));
        Assert.Equal(host, Assert.Single(allowed));
        PluginSandbox.EnsureCredentialTargetAllowed("http://" + host + ":8080/data", Credentials, allowed);
        Assert.True(HostMatch(typeof(PluginExecutor), "HostInAllowedSet", host, allowed));
        Assert.True(HostMatch(typeof(CryptoNative), "IsHostAllowed", host, allowed));
        Assert.False(HostMatch(typeof(PluginExecutor), "HostInAllowedSet", other, allowed));
        Assert.False(HostMatch(typeof(CryptoNative), "IsHostAllowed", other, allowed));
        Assert.Throws<PluginSandboxException>(() => PluginSandbox.EnsureCredentialTargetAllowed(
            "http://" + other + "/data", Credentials, allowed));
    }

    [Theory]
    [InlineData("https://user@@api.example.test/data")]
    [InlineData("https://api.example.test:notaport/data")]
    [InlineData("https://api.example.test:65536/data")]
    [InlineData("https://api.example.test:/data")]
    [InlineData("https://api.example.test:-1/data")]
    [InlineData("https://api.example.test:+80/data")]
    [InlineData("https://[2001:db8::1/data")]
    [InlineData("https://[2001:db8::1]junk/data")]
    [InlineData("https://2001:db8::1/data")]
    [InlineData("https://[api.example.test]/data")]
    public void InvalidFullAuthorityCannotGrantCredentialsToAnOtherwiseValidDynamicTarget(string declaration)
    {
        var allowed = PluginProcessor.GetDeclaredHosts(Template(declaration));
        Assert.Empty(allowed);
        Assert.Throws<PluginSandboxException>(() => PluginSandbox.EnsureCredentialTargetAllowed(
            "https://api.example.test/data", Credentials, allowed));
        Assert.Throws<PluginSandboxException>(() => PluginSandbox.EnsureCredentialTargetAllowed(
            "https://[2001:db8::1]/data", Credentials, allowed));
        var native = PluginExecutor.GetNativeAllowedHosts("native://crypto?fallback=" + declaration + "&symbol={{symbol}}", allowed);
        Assert.Empty(native);
    }

    [Theory]
    [InlineData("https://api.example.test/{{path}}?q={{value}}#{{fragment}}")]
    [InlineData("https://api.example.test?q={{value}}&other=https://evil.test/path")]
    [InlineData("https://api.example.test#{{fragment}}")]
    [InlineData("https://user:pass@api.example.test:8443?value={{value}}")]
    public void StaticAuthorityCanDeclareDynamicPathsQueriesAndFragments(string declaration)
    {
        var allowed = PluginProcessor.GetDeclaredHosts(Template(declaration));
        Assert.Equal("api.example.test", Assert.Single(allowed));
        PluginSandbox.EnsureCredentialTargetAllowed("https://api.example.test/resolved?q=ok", Credentials, allowed);
    }

    [Theory]
    [InlineData("{{target}}")]
    [InlineData("https://{{host}}/path")]
    [InlineData("https://user:{{secret}}@api.example.test/path")]
    [InlineData("https://api.example.test:{{port}}/path")]
    public void DynamicAuthorityCannotContributeAStaticDeclaration(string declaration)
        => Assert.Empty(PluginProcessor.GetDeclaredHosts(Template(declaration)));

    [Fact]
    public void NativeEmbeddedUrlHasAnOuterAmpersandBoundaryAndCanonicalIpv6Authority()
    {
        var allowed = PluginExecutor.GetNativeAllowedHosts(
            "native://crypto?fallback=https://user:pass@[2001:0DB8:0:0:0:0:0:0001]:8443&symbol={{symbol}}&unrelated=evil.test", new());
        Assert.Equal("[2001:db8::1]", Assert.Single(allowed));
        Assert.True(HostMatch(typeof(CryptoNative), "IsHostAllowed", "[2001:db8::1]:443", allowed));
        var encoded = PluginExecutor.GetNativeAllowedHosts(
            "native://crypto?fallback=" + WebUtility.UrlEncode("https://api.example.test/{{path}}?q={{q}}") + "&symbol={{symbol}}", new());
        Assert.Equal("api.example.test", Assert.Single(encoded));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("13")]
    [InlineData("2512")]
    [InlineData("2513")]
    public async Task ScopedIpv6IsRejectedBeforeCacheIdentityInflightReuseAndDirectHttp(string scopeId)
    {
        string plain = "http://[fe80::1]/data";
        string scoped = "http://[fe80::1%" + scopeId + "]/data";
        Assert.Empty(PluginProcessor.GetDeclaredHosts(Template(scoped)));
        Assert.Throws<PluginSandboxException>(() => PluginSandbox.EnsureCredentialTargetAllowed(scoped, Credentials, new[] { "[fe80::1]" }));
        Assert.False(HostMatch(typeof(PluginExecutor), "HostInAllowedSet", "[fe80::1%" + scopeId + "]", new() { "[fe80::1]" }));
        Assert.False(HostMatch(typeof(CryptoNative), "IsHostAllowed", "[fe80::1%" + scopeId + "]", new() { "[fe80::1]" }));
        var canonical = typeof(PluginExecutor).GetMethod("CanonicalizeUrl", BindingFlags.NonPublic | BindingFlags.Static);
        var failure = Assert.Throws<TargetInvocationException>(() => canonical.Invoke(null, new object[] { scoped }));
        Assert.IsType<PluginSandboxException>(failure.InnerException);
        var identity = typeof(PluginExecutor).GetNestedType("RequestIdentity", BindingFlags.NonPublic).GetConstructors().Single();
        failure = Assert.Throws<TargetInvocationException>(() => identity.Invoke(new object[]
            { "review-scope", "", "fetch", "GET", scoped, "", null, "", "utf-8" }));
        Assert.IsType<PluginSandboxException>(failure.InnerException);

        using var executor = new PluginExecutor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ReviewPluginTransport { Respond = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Limit);
            return Json();
        } };
        ReviewPluginTransport.Install(executor, transport);
        var instance = new PluginInstanceConfig { Id = "host-" + Guid.NewGuid().ToString("N"), Enabled = true };
        var template = Template(plain);
        template.Execution.Steps[0].CacheMinutes = 1;
        Task<bool> unscoped = executor.ExecuteInstanceAsync(instance, template);
        try
        {
            await entered.Task.WaitAsync(Limit);
            var rejected = Template(scoped);
            rejected.Execution.Steps[0].CacheMinutes = 1;
            await Assert.ThrowsAsync<PluginSandboxException>(() => executor.ExecuteInstanceAsync(instance, rejected));
            Assert.Equal(1, transport.Count);
            Assert.Equal(1, ((IDictionary)ReviewPluginReloadRegressionTests.Field(executor, "_inflightRequests")).Count);
            release.TrySetResult();
            Assert.True(await unscoped.WaitAsync(Limit));
            Assert.Equal(1, ((IDictionary)ReviewPluginReloadRegressionTests.Field(executor, "_stepCache")).Count);
            await Assert.ThrowsAsync<PluginSandboxException>(() => executor.ExecuteInstanceAsync(instance, rejected));
            Assert.Equal(1, transport.Count); // Neither a cached response nor a shared response bypasses rejection.
            rejected.Execution.Type = "api_json";
            rejected.Execution.Url = scoped;
            await Assert.ThrowsAsync<PluginSandboxException>(() => executor.ExecuteInstanceAsync(instance, rejected));
            Assert.Equal(1, transport.Count);
        }
        finally
        {
            release.TrySetResult();
            await unscoped.WaitAsync(Limit);
            executor.BeforePublicationAsync = null;
            Cleanup(instance.Id);
        }
    }

    [Theory]
    [InlineData("http://[fe80::1%12]/next")]
    [InlineData("http://[fe80::1%13]/next")]
    [InlineData("http://[fe80::1%2512]/next")]
    [InlineData("//[fe80::1%2513]/next")]
    public async Task HttpAndNativeRedirectRejectRawScopedAuthorityBeforeTheNextHop(string location)
    {
        using var executor = new PluginExecutor();
        var transport = new ReviewPluginTransport { Respond = (_, _) => Task.FromResult(Redirect(location)) };
        ReviewPluginTransport.Install(executor, transport);
        var instance = new PluginInstanceConfig { Id = "redirect-" + Guid.NewGuid().ToString("N"), Enabled = true };
        try
        {
            var error = await Assert.ThrowsAsync<PluginSandboxException>(() => executor.ExecuteInstanceAsync(instance, Template("http://[fe80::1]/data")));
            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, error.Reason);
            Assert.Equal(1, transport.Count);

            using var native = new NativeScope((_, _) => Task.FromResult(Redirect(location)));
            error = await Assert.ThrowsAsync<PluginSandboxException>(() => CryptoNative.FetchAsync(native.Symbol, "", CancellationToken.None, 0, null, "review-redirect"));
            Assert.Equal(PluginSandboxReasons.RedirectTargetNotAllowed, error.Reason);
            Assert.Equal(1, native.Transport.Count);
        }
        finally { Cleanup(instance.Id); }
    }

    [Theory]
    [InlineData("12")]
    [InlineData("13")]
    [InlineData("2512")]
    [InlineData("2513")]
    public async Task NativeFallbackRejectsScopedTargetBeforeItsWarmGlobalCacheOrInflightDictionary(string scopeId)
    {
        using var native = new NativeScope((_, _) => Task.FromResult(Bybit()));
        await CryptoNative.FetchAsync(native.Symbol, "", CancellationToken.None, 0, null, "warm-review");
        Assert.Equal(1, native.Transport.Count);
        string target = "http://[fe80::1%" + scopeId + "]/fallback";
        await Assert.ThrowsAsync<PluginSandboxException>(() => CryptoNative.FetchAsync(native.Symbol, target, CancellationToken.None, 0, null, "warm-review"));
        await Assert.ThrowsAsync<PluginSandboxException>(() => CryptoNative.FetchAsync(native.Symbol, target, CancellationToken.None, 0,
            new[] { "[fe80::1]" }, "warm-review"));
        Assert.Equal(1, native.Transport.Count);

        using var executor = new PluginExecutor();
        var transport = new ReviewPluginTransport { Respond = (_, _) => Task.FromResult(Json()) };
        ReviewPluginTransport.Install(executor, transport);
        var instance = new PluginInstanceConfig { Id = "fallback-" + Guid.NewGuid().ToString("N"), Enabled = true };
        try
        {
            var template = Template("native://crypto?symbol=" + native.Symbol + "&fallback=" + WebUtility.UrlEncode(target));
            template.Execution.Steps[0].CacheMinutes = -1;
            await Assert.ThrowsAsync<PluginSandboxException>(() => executor.ExecuteInstanceAsync(instance, template));
            Assert.Equal(0, transport.Count);
            Assert.Empty((IDictionary)ReviewPluginReloadRegressionTests.Field(executor, "_stepCache"));
            Assert.Empty((IDictionary)ReviewPluginReloadRegressionTests.Field(executor, "_inflightRequests"));
            Assert.Equal(1, native.Transport.Count);
        }
        finally { Cleanup(instance.Id); }
    }

    [Fact]
    public async Task OrdinaryIpv6EquivalentRedirectAndPercentEncodedPathQueryRemainAllowed()
    {
        using var executor = new PluginExecutor();
        var transport = new ReviewPluginTransport();
        transport.Respond = (_, _) => Task.FromResult(transport.Count == 1
            ? Redirect("http://[2001:0DB8:0:0:0:0:0:0001]/a%20b?q=%25") : Json());
        ReviewPluginTransport.Install(executor, transport);
        var template = Template("http://[2001:db8::1]/data");
        template.Execution.Steps[0].Headers = Credentials;
        var instance = new PluginInstanceConfig { Id = "ordinary-" + Guid.NewGuid().ToString("N"), Enabled = true };
        try
        {
            Assert.True(await executor.ExecuteInstanceAsync(instance, template).WaitAsync(Limit));
            Assert.Equal(2, transport.Count);
            PluginSandbox.EnsureCredentialTargetAllowed("http://[2001:db8::1]/x%25?q=%2512", Credentials, new[] { "[2001:db8::1]" });
            var canonical = typeof(PluginExecutor).GetMethod("CanonicalizeUrl", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.Contains("/a%20b?q=%25", (string)canonical.Invoke(null, new object[] { "http://[2001:db8::1]/a%20b?q=%25" }));
            using var native = new NativeScope((_, _) => Task.FromResult(Bybit()));
            Assert.NotEmpty(await CryptoNative.FetchAsync(native.Symbol, "http://[fe80::1]/x%25?q=%2512", CancellationToken.None, 0,
                new[] { "[fe80::1]" }, "ordinary-review"));
            Assert.Equal(1, native.Transport.Count);
        }
        finally { Cleanup(instance.Id); }
    }

    private static bool HostMatch(Type type, string method, string host, HashSet<string> allowed)
        => (bool)type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { host, allowed });

    private static PluginTemplate Template(string url) => new()
    {
        Id = "review.host",
        Execution = new() { Type = "chain", Steps = new() { new PluginExecutionStep
            { Id = "fetch", Url = url, Extract = new() { ["v"] = "v" } } } },
        Outputs = new() { new PluginOutput { Key = "value", Format = "{{v}}" } }
    };

    private static HttpResponseMessage Json() => new(HttpStatusCode.OK) { Content = new StringContent("{\"v\":\"ok\"}") };
    private static HttpResponseMessage Bybit() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"retCode\":0,\"result\":{\"list\":[{\"symbol\":\"REVIEWUSDT\",\"lastPrice\":\"1\",\"price24hPcnt\":\"0\",\"highPrice24h\":\"1\",\"lowPrice24h\":\"1\",\"turnover24h\":\"1\"}]}}")
    };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static void Cleanup(string id)
    {
        InfoService.Instance.RemoveDataByPrefix(id + ".");
        InfoService.Instance.RemoveDataByPrefix("PROP.Label.DASH." + id + ".");
        InfoService.Instance.RemoveDataByPrefix("PROP.ShortLabel.DASH." + id + ".");
    }

    private sealed class NativeScope : IDisposable
    {
        private readonly FieldInfo _client = typeof(CryptoNative).GetField("_client", BindingFlags.NonPublic | BindingFlags.Static);
        private readonly HttpClient _savedClient;
        private readonly Dictionary<string, string> _savedOverrides;
        internal string Symbol { get; } = "REVIEW" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        internal ReviewPluginTransport Transport { get; } = new();
        internal NativeScope(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _savedClient = (HttpClient)_client.GetValue(null);
            _savedOverrides = CryptoNative.SourceUrlOverrides;
            Transport.Respond = respond;
            try
            {
                _client.SetValue(null, new HttpClient(Transport) { Timeout = TimeSpan.FromSeconds(8) });
                CryptoNative.SourceUrlOverrides = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Bybit"] = "http://[fe80::1]/data", ["GateIO"] = "http://[fe80::1]/data",
                    ["Binance"] = "http://[fe80::1]/data", ["OKX"] = "http://[fe80::1]/data"
                };
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            try { ((HttpClient)_client.GetValue(null))?.Dispose(); }
            finally
            {
                _client.SetValue(null, _savedClient);
                CryptoNative.SourceUrlOverrides = _savedOverrides;
                var cache = (IDictionary)typeof(CryptoNative).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                cache.Remove(Symbol + "USDT");
            }
        }
    }
}
