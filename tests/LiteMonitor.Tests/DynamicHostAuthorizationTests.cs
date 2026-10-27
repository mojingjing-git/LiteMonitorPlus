using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class DynamicHostAuthorizationTests
{
    [Fact]
    public void F10_XApiToken按真实凭据头分类()
    {
        Assert.True(PluginSandbox.HasCredentialHeader(new[]
        {
            new KeyValuePair<string, string>("X-API-Token", "secret")
        }));
        Assert.True(PluginSandbox.HasCredentialHeader(new[]
        {
            new KeyValuePair<string, string>("x-api-token", "secret")
        }));
        Assert.False(PluginSandbox.HasCredentialHeader(new[]
        {
            new KeyValuePair<string, string>("X-Trace-Id", "trace")
        }));
    }

    [Fact]
    public void F10_用户授权精确匹配origin且端口不可复用()
    {
        string path = TempStore();
        try
        {
            var authorizer = new DynamicHostAuthorizer(path);
            var endpoint = new Uri("https://example.com:8443/api/v1");

            Assert.False(authorizer.IsAuthorized("inst-1", endpoint));
            var decision = authorizer.AuthorizeForUser("inst-1", endpoint);
            Assert.True(decision.Allow);
            Assert.True(decision.Persisted);
            Assert.True(authorizer.IsAuthorized("inst-1", new Uri("https://example.com:8443/other")));
            Assert.False(authorizer.IsAuthorized("inst-1", new Uri("https://example.com:9443/api")));
            Assert.False(authorizer.IsAuthorized("inst-2", endpoint));
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void F10_非HTTPS非回环目标不能授权()
    {
        string path = TempStore();
        try
        {
            var authorizer = new DynamicHostAuthorizer(path);
            var decision = authorizer.AuthorizeForUser("inst-1", new Uri("http://example.com/api"));
            Assert.False(decision.Allow);
            Assert.Equal("OriginInvalid", decision.ReasonCode);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void F10_授权写入后可跨实例读取且重复授权不重复写入()
    {
        string path = TempStore();
        try
        {
            var first = new DynamicHostAuthorizer(path);
            Assert.True(first.AuthorizeForUser("inst-1", new Uri("https://example.com/api")).Allow);
            Assert.True(first.AuthorizeForUser("inst-1", new Uri("https://example.com/other")).Allow);

            var second = new DynamicHostAuthorizer(path);
            string[] origins = second.GetAuthorizedOrigins("inst-1").ToArray();
            Assert.Single(origins);
            Assert.Equal("https://example.com", origins[0]);

            var repeat = second.AuthorizeForUser("inst-1", new Uri("https://example.com/api"));
            Assert.True(repeat.Allow);
            Assert.Equal("AlreadyAuthorized", repeat.ReasonCode);
        }
        finally { DeleteStore(path); }
    }

    [Fact]
    public void F10_授权origin使带凭据请求通过且端口不符仍拒绝()
    {
        var headers = new[]
        {
            new KeyValuePair<string, string>("X-API-Token", "secret")
        };

        PluginSandbox.EnsureCredentialTargetAllowed(
            "https://example.com:8443/api",
            headers,
            new[] { "https://example.com:8443" });

        Assert.Throws<PluginSandboxException>(() =>
            PluginSandbox.EnsureCredentialTargetAllowed(
                "https://example.com:9443/api",
                headers,
                new[] { "https://example.com:8443" }));
    }

    [Fact]
    public async Task F10_重定向到未授权origin时不发送凭据且已授权时逐跳发送()
    {
        using var target = new RedirectSandboxServer(_ => RedirectSandboxResponse.Text("ok"));
        using var first = new RedirectSandboxServer(_ =>
            RedirectSandboxResponse.Redirect(302, target.Url + "/next"));
        using var exec = new PluginExecutor();

        string firstOrigin = "http://127.0.0.1:" + first.Port;
        string targetOrigin = "http://127.0.0.1:" + target.Port;
        var step = NewStep(first.Url + "/start",
            headers: new Dictionary<string, string> { ["X-API-Token"] = "secret" });

        // 只授权第一跳：第二跳未授权，必须在发出请求前拒绝。
        var denied = await Assert.ThrowsAsync<PluginSandboxException>(
            () => RunStepAsync(exec, step, new[] { firstOrigin }));
        Assert.Equal(PluginSandboxReasons.CredentialTargetNotAllowed, denied.Reason);
        Assert.Equal(1, first.RequestCount);
        Assert.Equal(0, target.RequestCount);

        // 两跳都授权：第二跳必须收到同一凭据头。
        using var exec2 = new PluginExecutor();
        string payload = await RunStepAsync(exec2, step, new[] { firstOrigin, targetOrigin });
        Assert.Equal("ok", payload);
        Assert.Equal(2, first.RequestCount);
        Assert.Equal(1, target.RequestCount);
        Assert.True(target.Requests[0].Headers.TryGetValue("X-API-Token", out string? token));
        Assert.Equal("secret", token);
    }

    private static PluginExecutionStep NewStep(string url, Dictionary<string, string>? headers = null)
        => new PluginExecutionStep
        {
            Id = "s1",
            Url = url,
            Method = "GET",
            Headers = headers,
            ResponseFormat = "text",
            ResponseEncoding = "utf-8",
            Extract = new Dictionary<string, string> { ["v"] = "$" }
        };

    private static async Task<string> RunStepAsync(
        PluginExecutor exec, PluginExecutionStep step, IReadOnlyCollection<string> allowedOrigins)
    {
        var context = new Dictionary<string, string>();
        using var scope = new PluginSandboxScope(
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            PluginSandboxLimits.MaxStepsPerExecution,
            PluginSandboxLimits.MaxTargetsPerStep);
        var method = typeof(PluginExecutor).GetMethod(
            "ExecuteStepWithScopeAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(exec, new object[]
        {
            new PluginInstanceConfig { Id = "inst-redirect" },
            step,
            context,
            "",
            CancellationToken.None,
            scope,
            new HashSet<string>(allowedOrigins, StringComparer.OrdinalIgnoreCase)
        });
        await task.ConfigureAwait(false);
        return context.TryGetValue("v", out string? value) ? value : "";
    }

    [Fact]
    public void F10_授权origin按IDN规范化并区分端口()
    {
        string store = TempStore();
        try
        {
            var authorizer = new DynamicHostAuthorizer(store);
            var decision = authorizer.AuthorizeForUser("inst-idn", new Uri("https://bücher.example/api"));
            Assert.True(decision.Allow);
            string origin = Assert.Single(authorizer.GetAuthorizedOrigins("inst-idn"));
            Assert.StartsWith("https://xn--bcher-kva.example", origin, StringComparison.OrdinalIgnoreCase);
            Assert.True(authorizer.IsAuthorized("inst-idn", new Uri("https://xn--bcher-kva.example/other")));
            Assert.False(authorizer.IsAuthorized("inst-idn", new Uri("https://xn--bcher-kva.example:8443/other")));
        }
        finally { DeleteStore(store); }
    }

    [Fact]
    public void F10_删除实例时清理全部授权且幂等()
    {
        string store = TempStore();
        try
        {
            var authorizer = new DynamicHostAuthorizer(store);
            Assert.True(authorizer.AuthorizeForUser("inst-del", new Uri("https://a.example/api")).Allow);
            Assert.True(authorizer.AuthorizeForUser("inst-del", new Uri("https://b.example/api")).Allow);
            Assert.Equal(2, authorizer.GetAuthorizedOrigins("inst-del").Count);
            Assert.True(authorizer.RevokeAll("inst-del"));
            Assert.Empty(authorizer.GetAuthorizedOrigins("inst-del"));
            Assert.False(authorizer.RevokeAll("inst-del"));
        }
        finally { DeleteStore(store); }
    }

    private static string TempStore()
        => Path.Combine(Path.GetTempPath(), "lm-host-auth-" + Guid.NewGuid().ToString("N") + ".json");

    private static void DeleteStore(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
