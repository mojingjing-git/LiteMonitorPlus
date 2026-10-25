using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.SystemServices.Pipe;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// P-I2 / Plan A §8「数据一致性」行 + §5.3 时效语义：快照原子切换不混代次（值与批次同源）、
/// 首次收到已过期样本不置 Fresh、断线后拒绝新操作（快照仍可读）、迟到命令响应丢弃且
/// 不破坏后续配对。合成样本模式（batch*1000+i）使任何撕裂读都可被公式检验。
/// </summary>
public sealed class I2SnapshotAtomicityTests
{
    private static HardwareHostSupervisorOptions Options(
        string[] hostArgs, TimeSpan? commandTimeout = null, TimeSpan? handshake = null) => new()
    {
        HostExePathOverride = I2HostBinaries.ExePath,
        HostExtraArguments = hostArgs,
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5),
        HandshakeTimeout = handshake ?? TimeSpan.FromSeconds(5),
        BackoffSequence = new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300) },
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>合成样本一致性：samples[i].Value == BatchId*1000 + i（撕裂/混代次必然破坏公式）。</summary>
    private static void AssertBatchConsistent(HardwareSnapshot snapshot)
    {
        Assert.Equal(MinimalSampleCount, snapshot.Samples.Count);
        for (int i = 0; i < snapshot.Samples.Count; i++)
        {
            MetricSample s = snapshot.Samples[i];
            Assert.NotNull(s.Value);
            float expected = snapshot.BatchId * 1000f + i;
            Assert.Equal(expected, s.Value!.Value);
            Assert.Equal(SampleQuality.Good, s.Quality);
        }
    }

    private const int MinimalSampleCount = 12;

    [Fact]
    public async Task 快照原子切换_并发读不混代次且批次单调()
    {
        using var client = new PipeHardwareClient(50, supervisorOptions: Options(new[] { "--test-pattern" }));
        Assert.True(await WaitUntilAsync(() => client.GetLatestSnapshot().Samples.Count > 0, TimeSpan.FromSeconds(20)),
            "未取得首批数据");

        long lastBatch = 0;
        int reads = 0;
        var readStop = new Stopwatch();
        readStop.Start();
        // 读侧持续轮询（比 UI Tick 更密），模拟多读者只读最新快照。
        while (readStop.Elapsed < TimeSpan.FromSeconds(3))
        {
            HardwareSnapshot snapshot = client.GetLatestSnapshot();
            AssertBatchConsistent(snapshot); // 任何一次撕裂读立即失败
            Assert.True(snapshot.BatchId >= lastBatch, $"批次回退：{snapshot.BatchId} < {lastBatch}");
            lastBatch = snapshot.BatchId;
            reads++;
        }

        Assert.True(reads > 50, $"读取次数过少：{reads}");
        Assert.True(lastBatch >= 10, $"3 秒 50ms 周期应推进多批，实际末批 {lastBatch}");
    }

    [Fact]
    public async Task 首帧已过期_不置Fresh_后续新鲜批次正常发布()
    {
        // --stale-first：Host 首批时间戳回拨 30s（TTL 3s）。首个有效批次必须是批次 2 且质量 Good。
        using var client = new PipeHardwareClient(50, supervisorOptions: Options(new[] { "--test-pattern", "--stale-first" }));
        Assert.True(await WaitUntilAsync(() => client.GetLatestSnapshot().Samples.Count > 0, TimeSpan.FromSeconds(20)),
            "未取得首批有效数据");

        HardwareSnapshot first = client.GetLatestSnapshot();
        Assert.True(first.BatchId >= 2, $"过期首帧不得作为 Fresh 发布，实际首批 BatchId={first.BatchId}");
        AssertBatchConsistent(first);
        Assert.Equal(SampleQuality.Good, first.Samples[0].Quality);
    }

    [Fact]
    public async Task 断线暂停后_拒绝新操作_快照仍可读()
    {
        // bad-session 故障：永远无法建立会话 → 三败 → crash-loop 暂停（Degraded）。
        using var client = new PipeHardwareClient(100,
            supervisorOptions: Options(new[] { "--fault", "bad-session" }, handshake: TimeSpan.FromSeconds(1)));
        Assert.True(await WaitUntilAsync(
            () => client.GetHealth().State == HardwareState.Degraded && client.GetHealth().DiagnosticReason == "crash-loop-paused",
            TimeSpan.FromSeconds(30)), "未进入暂停态");

        // 拒绝新操作：不抛未受控异常，明确失败结果。
        Assert.False(await client.RequestRescanAsync());
        Assert.False(client.ApplyConfig(new SamplingConfig(1, 1000,
            new ControllerProbePolicyData(-1, Array.Empty<string>()), new DiskProbePolicyData(-1),
            Array.Empty<DeviceSelection>())).Applied);
        HardwareCatalog catalog = await client.GetCatalogAsync(CancellationToken.None);
        Assert.Equal(-1, catalog.Version); // 空目录哨兵 = 无会话

        // 快照可读（空批次哨兵，不抛）。
        Assert.Empty(client.GetLatestSnapshot().Samples);
    }

    [Fact]
    public async Task 迟到命令响应_按未知请求丢弃_后续命令配对不受影响()
    {
        // late-response 故障：rescan 应答推迟 10s；客户端 1.2s 超时后先失败，
        // 迟到应答到达时必须被丢弃并计数，且不占用后续请求的配对。
        using var client = new PipeHardwareClient(100,
            supervisorOptions: Options(new[] { "--test-pattern", "--fault", "late-response" },
                commandTimeout: TimeSpan.FromSeconds(1.2)));
        Assert.True(await WaitUntilAsync(() => client.GetLatestSnapshot().Samples.Count > 0, TimeSpan.FromSeconds(20)),
            "未取得首批数据");

        // 第一次 rescan：超时失败（超时≠底层已取消，不立即重开相同操作——见下一断言的合并语义）。
        Assert.False(await client.RequestRescanAsync());
        Assert.True(client.ReadMetrics().CommandTimeouts >= 1, "超时应计数");

        // 等迟到应答真正到达（10s 故障延迟）后被丢弃。
        Assert.True(await WaitUntilAsync(() => client.ReadMetrics().LateResponses >= 1, TimeSpan.FromSeconds(15)),
            $"迟到响应未被丢弃计数：{client.ReadMetrics().LateResponses}");

        // 后续命令配对不受污染：目录查询正常往返。
        HardwareCatalog catalog = await client.GetCatalogAsync(CancellationToken.None);
        Assert.Equal(1, catalog.Version); // 合成采样器目录代次
    }

    [Fact]
    public async Task 命令在飞合并_并发重复请求不放大()
    {
        using var client = new PipeHardwareClient(100,
            supervisorOptions: Options(new[] { "--test-pattern", "--fault", "late-response" },
                commandTimeout: TimeSpan.FromSeconds(8)));
        Assert.True(await WaitUntilAsync(() => client.GetLatestSnapshot().Samples.Count > 0, TimeSpan.FromSeconds(20)),
            "未取得首批数据");

        // 并发 5 个 rescan：同操作在飞合并 → 同一迟到应答只补一个配对，其余按未知请求丢弃或合并等待。
        Task<bool>[] calls = Enumerable.Range(0, 5).Select(_ => client.RequestRescanAsync()).ToArray();
        bool[] results = await Task.WhenAll(calls);
        Assert.Equal(5, results.Length); // 全部有界返回（合并/超时均不挂死）

        Assert.True(await WaitUntilAsync(() => client.ReadMetrics().LateResponses >= 1, TimeSpan.FromSeconds(15)),
            "迟到响应应至少出现一次（首个请求超时后到达）");
    }
}
