using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.SystemServices;
using LiteMonitor.SystemServices.Pipe;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// P-E02（完善计划 §6）**客户端命令面语义一致性测试**。
/// <para>同一测试集对两种客户端（管道客户端 <see cref="PipeHardwareClient"/> / 进程内适配器
/// <see cref="HardwareClientAdapter"/>）跑同一组语义用例：管道侧断言全绿；进程内适配器的
/// 对照结果记入 <see cref="AdapterComparisonLog"/>，作为**移交清单**交由集成人转 P-E01
/// （适配器命令面实现归 E01，本包不越界修改）。</para>
///
/// <para>语义表（与计划 §6 表格逐行对应）：</para>
/// <list type="table">
/// <item><b>请求到期采样</b>：异步、共享唯一协调器；同一采样周期合并。</item>
/// <item><b>显式硬件重扫</b>：异步、有独立操作 ID 及状态；同代次重复请求加入在途操作。</item>
/// <item><b>应用配置</b>：异步、返回实际生效 revision；仅相同 revision/内容可合并。</item>
/// <item><b>提交需求</b>：按消费者更新需求 revision；同消费者保留最新值、不覆盖其他消费者。</item>
/// <item><b>超时</b>：只表示本调用方停止等待，不得报告底层已停止；非幂等操作保留有界记录。</item>
/// <item><b>取消</b>：一个加入者取消不取消共享操作。</item>
/// <item><b>重连</b>：重放最新期望配置与有效需求；旧会话应答不能完成新命令。</item>
/// </list>
/// </summary>
public sealed class E02ClientSemanticConsistencyTests
{
    // ── 进程内适配器对照日志（移交清单数据源）────────────────────────────────
    private static readonly ConcurrentQueue<string> ComparisonLog = new();

    /// <summary>进程内适配器的逐点对照结果（移交清单；本包不据此判失败）。</summary>
    public static IReadOnlyList<string> AdapterComparisonLog => ComparisonLog.ToArray();

    /// <summary>对照结果的落盘路径（验收报告引用；随测试输出目录走）。</summary>
    public static string ComparisonLogPath =>
        Path.Combine(AppContext.BaseDirectory, "e02-adapter-comparison.txt");

    // ── 测试装置 ─────────────────────────────────────────────────────────────

    private static HardwareHostSupervisorOptions FastOptions(string[] hostArgs,
        TimeSpan? commandTimeout = null, TimeSpan? heartbeat = null)
    {
        return new HardwareHostSupervisorOptions
        {
            HostExePathOverride = I2HostBinaries.ExePath,
            HostExtraArguments = hostArgs,
            HandshakeTimeout = TimeSpan.FromSeconds(5),
            // 重扫故障注入会占用采样闸门 3s：无进展期限须大于它，否则监督器误判宿主死亡。
            SamplingProgressTimeout = TimeSpan.FromSeconds(30),
            HeartbeatInterval = heartbeat ?? TimeSpan.FromSeconds(2),
            InitializationTimeout = TimeSpan.FromSeconds(60),
            CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5),
            BackoffSequence = new[]
            {
                TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300),
            },
            BackoffPauseThreshold = 3,
        };
    }

    private static PipeHardwareClient CreateClient(HardwareHostSupervisorOptions options, int refreshMs = 100) =>
        new(refreshMs, supervisorOptions: options);

    private static async Task<PipeHardwareClient> WaitReadyClientAsync(
        HardwareHostSupervisorOptions options, int refreshMs = 100)
    {
        PipeHardwareClient client = CreateClient(options, refreshMs);
        bool ready = await WaitUntilAsync(() => client.GetLatestSnapshot().Samples.Count > 0, TimeSpan.FromSeconds(25));
        if (!ready)
        {
            client.Dispose();
            throw new TimeoutException("Host 未在期限内产出首批数据");
        }
        return client;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private static SamplingConfig Config(int revision, int refreshMs = 200) => new SamplingConfig(
        Revision: revision,
        RefreshMs: refreshMs,
        ControllerProbe: new ControllerProbePolicyData(-1, Array.Empty<string>()),
        DiskProbe: new DiskProbePolicyData(-1),
        Devices: Array.Empty<DeviceSelection>());

    private static readonly SamplingDemand DemandA = new(Ui: true, Taskbar: false, Web: false, History: false, SensorWindow: false);
    private static readonly SamplingDemand DemandB = new(Ui: false, Taskbar: true, Web: false, History: false, SensorWindow: false);

    // ── 白盒接缝（与 E00 回归同口径：合成信封直灌接收入口）───────────────────

    private static PipeHardwareClient CreateQuietClient()
    {
        var options = new HardwareHostSupervisorOptions
        {
            HostExePathOverride = Path.Combine(Path.GetTempPath(), "e02-no-such-host.exe"),
            HandshakeTimeout = TimeSpan.FromSeconds(1),
            HeartbeatInterval = TimeSpan.FromSeconds(2),
            BackoffSequence = new[]
            {
                TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20),
            },
            BackoffPauseThreshold = 3,
        };
        return new PipeHardwareClient(100, supervisorOptions: options,
            metricsPath: Path.Combine(Path.GetTempPath(), $"e02-metrics-{Guid.NewGuid():N}.json"));
    }

    private static void Feed(PipeHardwareClient client, IpcEnvelope envelope) =>
        typeof(PipeHardwareClient).GetMethod("OnMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(client, new[] { (object)envelope });

    private static void CloseSession(PipeHardwareClient client) =>
        typeof(PipeHardwareClient).GetMethod("OnSessionClosed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(client, new[] { (object)"e02-test-session-end" });

    private static IpcEnvelope SnapshotEnvelope(long seq, long batchId, int epoch, string session = "e02")
    {
        var snapshot = new HardwareSnapshot(
            BatchId: batchId, CatalogVersion: 1, ConfigRevision: -1,
            Samples: new[]
            {
                new MetricSample("CPU.Load", null, 42.5f, null, SampleQuality.Good, null, 0),
            },
            SamplingDuration: TimeSpan.FromMilliseconds(1), TimestampMonoMs: Environment.TickCount64,
            SessionEpoch: epoch);
        return new IpcEnvelope(
            new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeSnapshot, session, 0, seq),
            JsonSerializer.SerializeToElement(new IpcSnapshotMessage(snapshot, Environment.TickCount64)));
    }

    private static IpcEnvelope ResponseEnvelope(long seq, long requestId, string sessionToken) => new(
        new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeResponse, sessionToken, requestId, seq),
        JsonSerializer.SerializeToElement(new IpcResponseMessage(true, null, "true")));

    /// <summary>在飞命令的请求 ID 集合（白盒：PendingCommand 为私有嵌套类型，只取键）。</summary>
    private static List<long> PendingRequestIds(PipeHardwareClient client)
    {
        var field = typeof(PipeHardwareClient).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (IDictionary)field.GetValue(client)!;
        var ids = new List<long>();
        foreach (object? key in dict.Keys) ids.Add(Convert.ToInt64(key));
        return ids;
    }

    /// <summary>向待应答表注入占位条目（验证容量上限的显式拒绝）。</summary>
    private static void FillPendingTable(PipeHardwareClient client, int count)
    {
        var field = typeof(PipeHardwareClient).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (IDictionary)field.GetValue(client)!;
        Type pendingType = typeof(PipeHardwareClient).GetNestedType("PendingCommand", BindingFlags.NonPublic)!;
        for (long i = 1; i <= count; i++)
        {
            object instance = Activator.CreateInstance(pendingType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { "e02-filler", "" }, null)!;
            dict[i] = instance;
        }
    }

    // ── 进程内适配器对照（移交清单；不作为本包失败判据）──────────────────────

    /// <summary>
    /// 构造进程内适配器（对照用）。
    /// <para>用 E01 提供的可编程采集替身（<see cref="E01FakeCollector"/>）而非真实硬件：
    /// <c>HardwareMonitor</c> 构造即 Open LHM 硬件树，单测不可实例化；替身让命令面/需求面
    /// 在不打开硬件的环境里被真实驱动（E01 的命令面断言同此思路）。</para>
    /// </summary>
    private static HardwareClientAdapter CreateAdapterForCompare(out E01FakeCollector collector)
    {
        collector = new E01FakeCollector();
        return new HardwareClientAdapter(collector);
    }

    private static async Task<string> CompareAdapterAsync(string point, Func<IHardwareClient, Task<string>> scenario)
    {
        string outcome;
        try
        {
            using HardwareClientAdapter adapter = CreateAdapterForCompare(out _);
            outcome = await scenario(adapter);
        }
        catch (Exception ex)
        {
            string message = (ex.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 160) message = message[..160];
            outcome = $"不支持:{ex.GetType().Name}:{message}";
        }
        ComparisonLog.Enqueue($"{point} => {outcome}");
        // 证据行：同时写文件（验收报告直接引用，不凭记忆转述）。
        Console.WriteLine($"[E02对照] {point} => {outcome}");
        try
        {
            File.AppendAllText(ComparisonLogPath, $"{point} => {outcome}{Environment.NewLine}");
        }
        catch { /* 证据落盘失败不影响测试结论 */ }
        return outcome;
    }

    // ── 语义点 1：应用配置（合并 / 取代 / 实际生效版本）───────────────────────

    [Fact]
    public async Task 语义点1_应用配置_相同内容可合并且不同版本最终以最新版本生效()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));

        // (a) 相同 revision + 相同内容 ⇒ 合并为一次底层应用（只下发一条命令）。
        long before = client.CommandsSent;
        Task<SamplingApplyResult> first = client.ApplyConfigAsync(Config(1));
        Task<SamplingApplyResult> second = client.ApplyConfigAsync(Config(1));
        SamplingApplyResult[] merged = await Task.WhenAll(first, second);
        Assert.Equal(1, client.CommandsSent - before);
        Assert.All(merged, r => Assert.True(r.Applied));
        Assert.All(merged, r => Assert.Equal(1, r.Revision));

        // (b) 不同版本并发 ⇒ 最终明确为 v2（被取代者拿到可解释原因，不静默丢弃）。
        before = client.CommandsSent;
        Task<SamplingApplyResult> v1 = client.ApplyConfigAsync(Config(1));
        Task<SamplingApplyResult> v2 = client.ApplyConfigAsync(Config(2));
        SamplingApplyResult[] raced = await Task.WhenAll(v1, v2);
        Assert.True(client.CommandsSent - before >= 1);
        Assert.Contains(raced, r => r.Applied && r.Revision == 2);
        foreach (SamplingApplyResult r in raced)
        {
            if (!r.Applied)
                Assert.False(string.IsNullOrWhiteSpace(r.FailureReason)); // 失败必须可解释
        }

        // (c) 实际生效版本：只有 Host 确认后，快照上的 ConfigRevision 才是 2。
        Assert.Equal(2, client.AppliedConfigRevision);
        bool effective = await WaitUntilAsync(
            () => client.GetLatestSnapshot().ConfigRevision == 2, TimeSpan.FromSeconds(15));
        Assert.True(effective, $"Host 未以 revision=2 发布快照（当前 {client.GetLatestSnapshot().ConfigRevision}）");

        await CompareAdapterAsync("语义点1_应用配置", async adapter =>
        {
            SamplingApplyResult r = await adapter.ApplyConfigAsync(Config(1));
            return $"applied={r.Applied},revision={r.Revision},reason={r.FailureReason}";
        });
        await CompareAdapterAsync("语义点1_应用配置_同内容重复提交", async adapter =>
        {
            SamplingApplyResult r1 = await adapter.ApplyConfigAsync(Config(1));
            SamplingApplyResult r2 = await adapter.ApplyConfigAsync(Config(1));
            return $"first={r1.Applied}/{r1.Revision},second={r2.Applied}/{r2.Revision}";
        });
    }

    // ── 语义点 2：到期采样（共享协调器 / 同周期合并）──────────────────────────

    [Fact]
    public async Task 语义点2_到期采样_并发请求合并为一次采样()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));

        long before = client.CommandsSent;
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.RequestSamplingAsync()));
        Assert.All(results, r => Assert.True(r));
        Assert.Equal(1, client.CommandsSent - before); // 同一采样周期合并为一条命令

        await CompareAdapterAsync("语义点2_到期采样", async adapter =>
        {
            bool r = await adapter.RequestSamplingAsync();
            return $"triggered={r}";
        });
    }

    // ── 语义点 3：显式重扫（共享操作 / 一个加入者取消 / 另一个完成）────────────

    [Fact]
    public async Task 语义点3_显式重扫_两调用者共享同一操作且其一取消另一完成()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(
            FastOptions(new[] { "--test-pattern", "--fault", "slow-rescan" }));
        long catalogBefore = client.GetLatestSnapshot().CatalogVersion;

        using var cts = new CancellationTokenSource();
        Task<HardwareOperationResult> cancelled = client.RequestFullRescanAsync(cts.Token);
        Task<HardwareOperationResult> survivor = client.RequestFullRescanAsync();
        await Task.Delay(200);
        cts.Cancel();

        // 取消只作用于本调用方的等待：共享操作不被取消。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        HardwareOperationResult result = await survivor.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Accepted);
        Assert.Equal(HardwareOperationState.Completed, result.State);
        Assert.False(string.IsNullOrEmpty(result.OperationId));

        // 底层只发生一次重扫（目录代次只 +1）。
        bool bumped = await WaitUntilAsync(
            () => client.GetLatestSnapshot().CatalogVersion > catalogBefore, TimeSpan.FromSeconds(20));
        Assert.True(bumped, "重扫未反映到目录代次");
        Assert.Equal(catalogBefore + 1, client.GetLatestSnapshot().CatalogVersion);

        await CompareAdapterAsync("语义点3_显式重扫", async adapter =>
        {
            HardwareOperationResult r = await adapter.RequestFullRescanAsync();
            return $"accepted={r.Accepted},state={r.State},operationId={r.OperationId},reason={r.FailureReason}";
        });
    }

    // ── 语义点 4：超时语义（超时≠底层取消；新请求取得 busy/operationId）────────

    [Fact]
    public async Task 语义点4_重扫超时后再次请求_取得同一操作ID且不重复底层重扫()
    {
        // 命令期限收紧到 1s：调用方先超时，底层重扫仍在执行（slow-rescan 3s）。
        using PipeHardwareClient client = await WaitReadyClientAsync(
            FastOptions(new[] { "--test-pattern", "--fault", "slow-rescan" },
                commandTimeout: TimeSpan.FromSeconds(1)));
        long catalogBefore = client.GetLatestSnapshot().CatalogVersion;

        HardwareOperationResult first = await client.RequestFullRescanAsync();
        Assert.True(first.Accepted);
        Assert.False(string.IsNullOrEmpty(first.OperationId));
        Assert.Equal(HardwareOperationState.Running, first.State);      // 底层仍在执行
        Assert.Contains("timeout", first.FailureReason ?? string.Empty);

        // 超时后再请求：取得同一 operationId（busy），**不重复发起底层重扫**。
        HardwareOperationResult second = await client.RequestFullRescanAsync();
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.True(second.Accepted);

        // 底层只发生一次重扫。
        bool bumped = await WaitUntilAsync(
            () => client.GetLatestSnapshot().CatalogVersion > catalogBefore, TimeSpan.FromSeconds(25));
        Assert.True(bumped, "重扫未反映到目录代次");
        Assert.Equal(catalogBefore + 1, client.GetLatestSnapshot().CatalogVersion);
    }

    // ── 语义点 5：断连/关闭时全部等待者有界结束（无悬空等待）─────────────────

    [Fact]
    public async Task 语义点5_会话断开_全部等待者有界结束且无悬空等待()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(
            FastOptions(new[] { "--test-pattern", "--fault", "slow-rescan" }));

        Task<HardwareOperationResult> rescan = client.RequestFullRescanAsync();
        Task<SamplingApplyResult> apply = client.ApplyConfigAsync(Config(9));

        int pid = client.CurrentHostPid!.Value;
        Process.GetProcessById(pid).Kill(entireProcessTree: true);

        Task all = Task.WhenAll(rescan, apply);
        Task finished = await Task.WhenAny(all, Task.Delay(30000));
        Assert.Same(all, finished); // 两个等待者都在期限内结束（不悬空）

        // 结束方式：异常或明确的失败结果（都不得报告成功）。
        foreach (Task task in new Task[] { rescan, apply })
        {
            if (task.IsFaulted)
            {
                Assert.True(task.Exception!.InnerException is not null);
                continue;
            }
            object? value = task.GetType().GetProperty("Result")!.GetValue(task);
            if (value is HardwareOperationResult op)
            {
                // 重扫在会话死亡时仍在执行：不得报告"已完成"（会话结束 ⇒ 底层执行已终止）。
                Assert.NotEqual(HardwareOperationState.Completed, op.State);
            }
        }

        // 待应答表被清空（会话结束时全部等待者一次性收口）。
        Assert.Equal(0, client.PendingCommandCount);
    }

    // ── 语义点 6：旧会话应答不得完成新命令 ───────────────────────────────────

    [Fact]
    public async Task 语义点6_旧会话应答不完成新命令且被计为陈旧应答()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(
            FastOptions(new[] { "--test-pattern", "--fault", "late-response" }));

        // late-response 故障：rescan 应答推迟 10s ⇒ 命令在飞（可注入同请求 ID 的陈旧应答）。
        Task<bool> inFlight = client.RequestRescanAsync();
        List<long> pending = PendingRequestIds(client);
        Assert.NotEmpty(pending);
        long requestId = pending[0];

        long staleBefore = client.StaleSessionResponses;
        Feed(client, ResponseEnvelope(seq: 90001, requestId: requestId, sessionToken: "stale-session-token"));

        Assert.Equal(staleBefore + 1, client.StaleSessionResponses);       // 陈旧应答被丢弃并计数
        Assert.Contains(requestId, PendingRequestIds(client));             // 在飞命令未被完成

        await CompareAdapterAsync("语义点6_旧会话应答", async adapter =>
        {
            bool r = await adapter.RequestRescanAsync();
            return $"ok={r}";
        });
    }

    // ── 语义点 7：重连重放（最新期望配置 + 有效需求）─────────────────────────

    [Fact]
    public async Task 语义点7_重连后重放最新期望配置与有效需求()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));

        SamplingApplyResult applied = await client.ApplyConfigAsync(Config(7));
        Assert.True(applied.Applied);
        Assert.True(await client.SubmitDemandAsync("e02-consumer", DemandA));
        int registrationsBefore = client.GetDemandRegistrationCount("e02-consumer");
        Assert.Equal(1, registrationsBefore);

        // 杀掉 Host ⇒ 监督器重启出新会话 ⇒ 客户端重放最新期望配置与有效需求。
        Process.GetProcessById(client.CurrentHostPid!.Value).Kill(entireProcessTree: true);

        bool replayed = await WaitUntilAsync(() =>
            client.GetDemandRegistrationCount("e02-consumer") > registrationsBefore
            && client.GetLatestSnapshot().ConfigRevision == 7,
            TimeSpan.FromSeconds(40));
        Assert.True(replayed,
            $"重放未完成：登记次数 {client.GetDemandRegistrationCount("e02-consumer")}，" +
            $"快照 revision {client.GetLatestSnapshot().ConfigRevision}");
        Assert.Equal(7, client.AppliedConfigRevision);

        await CompareAdapterAsync("语义点7_重连重放", adapter =>
        {
            adapter.SubmitDemand("e02-consumer", DemandA);
            bool released = adapter.ReleaseDemand("e02-consumer");
            return Task.FromResult($"released={released}");
        });
    }

    // ── 语义点 8：会话纪元（递增 + 旧会话快照不混入新会话）────────────────────

    [Fact]
    public async Task 语义点8_会话纪元_宿主重启后递增且旧会话快照不混入新会话()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));

        bool gotEpoch = await WaitUntilAsync(() => client.CurrentSessionEpoch > 0, TimeSpan.FromSeconds(15));
        Assert.True(gotEpoch, "快照未携带会话纪元");
        int epochBefore = client.CurrentSessionEpoch;

        Process.GetProcessById(client.CurrentHostPid!.Value).Kill(entireProcessTree: true);

        bool bumped = await WaitUntilAsync(() =>
            client.CurrentSessionEpoch > epochBefore && client.GetLatestSnapshot().Samples.Count > 0,
            TimeSpan.FromSeconds(40));
        Assert.True(bumped, $"会话纪元未递增（{epochBefore} → {client.CurrentSessionEpoch}）");
        Assert.Equal(client.CurrentSessionEpoch, client.GetLatestSnapshot().SessionEpoch);
    }

    // ── 语义点 9：E00-F2（会话关闭重置批次基准 + 旧会话快照丢弃）──────────────

    [Fact]
    public void 语义点9_会话关闭_重置批次基准且旧会话快照不混入新会话()
    {
        using PipeHardwareClient client = CreateQuietClient();

        Feed(client, SnapshotEnvelope(seq: 1, batchId: 1, epoch: 1));
        Assert.Equal(1, client.GetLatestSnapshot().Samples.Count);

        CloseSession(client);

        // 旧会话快照数据不混入新会话：会话关闭即丢弃缓存快照。
        Assert.Equal(0, client.GetLatestSnapshot().Samples.Count);
        Assert.Equal(0, client.CurrentSessionEpoch);

        // E00-F2：批次基准随会话重置 ⇒ 新会话批次号回退不产生幻影缺口。
        Feed(client, SnapshotEnvelope(seq: 2, batchId: 1, epoch: 1));
        Assert.Equal(0, client.ReadMetrics().SnapshotBatchGaps);
        Assert.Equal(1, client.GetLatestSnapshot().Samples.Count);
    }

    // ── 语义点 10：需求登记/释放（按消费者隔离）───────────────────────────────

    [Fact]
    public async Task 语义点10_需求登记与释放_按消费者隔离且互不影响()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));

        Assert.True(await client.SubmitDemandAsync("consumer-a", DemandA));
        Assert.True(await client.SubmitDemandAsync("consumer-b", DemandB));
        Assert.Equal("registered", client.GetLastDemandOutcome("consumer-a"));
        Assert.Equal("registered", client.GetLastDemandOutcome("consumer-b"));

        // 释放一个消费者：另一个的需求不受影响。
        Assert.True(client.ReleaseDemand("consumer-a"));
        bool released = await WaitUntilAsync(
            () => client.GetLastDemandOutcome("consumer-a")?.StartsWith("released") == true,
            TimeSpan.FromSeconds(15));
        Assert.True(released, $"释放命令未落地：{client.GetLastDemandOutcome("consumer-a")}");
        Assert.Equal("registered", client.GetLastDemandOutcome("consumer-b"));

        // 再次释放：此前无登记 ⇒ 返回 false。
        Assert.False(client.ReleaseDemand("consumer-a"));

        await CompareAdapterAsync("语义点10_需求登记与释放", adapter =>
        {
            adapter.SubmitDemand("consumer-a", DemandA);
            adapter.SubmitDemand("consumer-b", DemandB);
            bool released = adapter.ReleaseDemand("consumer-a");
            return Task.FromResult($"released={released}");
        });
    }

    // ── 语义点 12：操作状态可查（超时调用方凭 OperationId 后询）────────────────

    [Fact]
    public async Task 语义点12_操作状态可查_未知操作ID给出可解释拒绝()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));

        HardwareOperationResult unknown = await client.QueryOperationAsync("rescan-does-not-exist");
        Assert.Equal(HardwareOperationState.Rejected, unknown.State);
        Assert.Equal("unknown-operation", unknown.FailureReason);

        // 已完成的操作同样可查（状态机终态可被复核）。
        HardwareOperationResult done = await client.RequestFullRescanAsync();
        Assert.Equal(HardwareOperationState.Completed, done.State);
        HardwareOperationResult queried = await client.QueryOperationAsync(done.OperationId);
        Assert.Equal(HardwareOperationState.Completed, queried.State);
        Assert.Equal(done.OperationId, queried.OperationId);
    }

    // ── 语义点 13：UI 同步等待修复（§6.1：ThemePreviewService 下载改真异步）──

    [Fact]
    public void 语义点13_UI同步等待修复_主题预览不再同步阻塞下载()
    {
        string repoRoot = I2HostBinaries.FindRepoRoot(AppContext.BaseDirectory);
        string source = Path.Combine(repoRoot, "src", "UI", "Settings", "ThemePreviewService.cs");
        Assert.True(File.Exists(source), $"未找到 {source}");

        string text = File.ReadAllText(source);
        // 只看代码行（注释里可以说明历史实现）。
        string code = string.Join("\n", text.Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain("GetAwaiter().GetResult()", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", code, StringComparison.Ordinal);
        Assert.Contains("RenderSnapshotAsync", text, StringComparison.Ordinal);

        // 真异步入口存在且可等待（旧同步入口已移除）。
        MethodInfo? asyncEntry = typeof(ThemePreviewService).GetMethod("RenderSnapshotAsync",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(asyncEntry);
        Assert.Equal(typeof(Task<string>), asyncEntry!.ReturnType);
        Assert.Null(typeof(ThemePreviewService).GetMethod("RenderSnapshotBlocking",
            BindingFlags.Public | BindingFlags.Static));
    }

    // ── 语义点 11：有界（待应答表 / 操作记录容量与截止期限）──────────────────

    [Fact]
    public async Task 语义点11_有界_待应答表与操作记录超限给出可解释拒绝()
    {
        // (a) 待应答表：容量用尽 ⇒ 显式拒绝（可解释），不无限增长、不静默丢配置。
        using (PipeHardwareClient client = CreateQuietClient())
        {
            FillPendingTable(client, 64);
            SamplingApplyResult result = await client.ApplyConfigAsync(Config(3));
            Assert.False(result.Applied);
            Assert.Contains("capacity", result.FailureReason ?? string.Empty);
        }

        // (b) 操作记录：容量 + 截止期限有界（过期剪除后可再新建）。
        var registry = new PipeOperationRegistry(capacity: 2, ttl: TimeSpan.FromMilliseconds(60));
        Assert.NotNull(registry.TryCreate("op-1"));
        Assert.NotNull(registry.TryCreate("op-2"));
        Assert.Null(registry.TryCreate("op-3")); // 容量超限：可解释拒绝
        Thread.Sleep(120);
        registry.Prune();
        Assert.Equal(0, registry.Count);
        Assert.NotNull(registry.TryCreate("op-4"));
    }
}
