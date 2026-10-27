using System;
using System.Collections.Generic;
using LiteMonitor.Diagnostics;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class DiagnosticEventStoreContractTests
{
    private static DiagnosticEvent NewEvent(string reason = "render.fallback", DateTimeOffset? time = null)
        => new(
            time ?? DateTimeOffset.UtcNow,
            "render",
            DiagnosticSeverity.Warning,
            reason,
            null,
            "session-1",
            new Dictionary<string, string> { ["route"] = "gpu", ["stage"] = "first-frame" });

    [Fact]
    public void C2_事件编号单调_超过容量淘汰最旧并计数()
    {
        var store = new DiagnosticEventStore(capacity: 3, mergeWindow: TimeSpan.Zero);

        for (int i = 0; i < 5; i++)
            Assert.True(store.Add(NewEvent($"reason-{i}")));

        Assert.Equal(3, store.Count);
        Assert.Equal(2, store.DroppedCount);
        var snapshot = store.Snapshot();
        Assert.Equal(3, snapshot[0].EventId);
        Assert.Equal(5, snapshot[2].EventId);
    }

    [Fact]
    public void C2_单条上下文超过2KiB_拒绝并计数()
    {
        var store = new DiagnosticEventStore();
        var evt = NewEvent() with
        {
            Context = new Dictionary<string, string> { ["route"] = new string('x', 3000) },
        };

        Assert.False(store.Add(evt));
        Assert.Equal(0, store.Count);
        Assert.Equal(1, store.RejectedCount);
    }

    [Fact]
    public void C2_允许列表外的键与敏感值不进入事件()
    {
        var store = new DiagnosticEventStore();
        var evt = NewEvent() with
        {
            Context = new Dictionary<string, string>
            {
                ["route"] = "gpu",
                ["authorization"] = "Bearer secret",
                ["path"] = @"C:\Users\me\secret.txt",
            },
        };

        Assert.True(store.Add(evt));
        var context = store.Snapshot()[0].Context;
        Assert.NotNull(context);
        Assert.True(context.ContainsKey("route"));
        Assert.False(context.ContainsKey("authorization"));
        Assert.False(context.ContainsKey("path"));
    }

    [Fact]
    public void R17_不同设备或会话的告警不得合并()
    {
        var store = new DiagnosticEventStore(mergeWindow: TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;

        Assert.True(store.Add(NewEvent(time: start) with
        {
            Context = new Dictionary<string, string> { ["ruleId"] = "cpu.temp", ["deviceId"] = "cpu-1", ["incidentId"] = "i-1" },
        }));
        Assert.True(store.Add(NewEvent(time: start.AddSeconds(1)) with
        {
            Context = new Dictionary<string, string> { ["ruleId"] = "gpu.temp", ["deviceId"] = "gpu-0", ["incidentId"] = "i-2" },
        }));

        Assert.Equal(2, store.Count);
        Assert.All(store.Snapshot(), evt =>
        {
            Assert.True(evt.Context!.ContainsKey("deviceId"));
            Assert.Equal(1, evt.RepeatCount);
        });
    }

    [Fact]
    public void R18_任意盘符路径与IP不进入上下文()
    {
        var store = new DiagnosticEventStore();
        var evt = NewEvent() with
        {
            Context = new Dictionary<string, string>
            {
                ["route"] = "gpu",
                ["reason"] = @"failed: G:\Users\Alice\private-theme.json (host 192.0.2.1)",
                ["status"] = "https://example.com/path",
            },
        };

        Assert.True(store.Add(evt));
        var context = store.Snapshot()[0].Context!;
        Assert.True(context.ContainsKey("route"));
        Assert.False(context.ContainsKey("reason"));
        Assert.False(context.ContainsKey("status"));
    }

    [Fact]
    public void C05_IPv6与任意绝对路径不进入上下文()
    {
        Assert.False(DiagnosticRedaction.IsSafeValue("2001:db8::1234"));
        Assert.False(DiagnosticRedaction.IsSafeValue(@"failed:G:\Users\Alice\private.json"));
        Assert.False(DiagnosticRedaction.IsSafeValue("/srv/private/alice/data.json"));
        Assert.True(DiagnosticRedaction.IsSafeValue("gpu/route"));
        Assert.True(DiagnosticRedaction.IsSafeValue("512"));
        Assert.True(DiagnosticRedaction.IsSafeValue("42.0"));
        Assert.True(DiagnosticRedaction.IsSafeValue("1200"));
    }

    [Fact]
    public void C2_同模块同原因在窗口内合并并累计重复次数()
    {
        var store = new DiagnosticEventStore(mergeWindow: TimeSpan.FromSeconds(5));
        var start = DateTimeOffset.UtcNow;

        Assert.True(store.Add(NewEvent(time: start)));
        Assert.True(store.Add(NewEvent(time: start.AddSeconds(1))));

        Assert.Equal(1, store.Count);
        Assert.Equal(2, store.Snapshot()[0].RepeatCount);
        Assert.Equal(1, store.Snapshot()[0].EventId);
    }
}
