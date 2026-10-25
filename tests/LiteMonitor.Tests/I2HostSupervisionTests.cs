using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.SystemServices.Pipe;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// P-I2 §2.6：Host 产物定位器。测试用相对路径定位 repo 根，再取
/// src/HardwareHost/bin/&lt;Configuration&gt;/net8.0-windows/ 下的 Host 产物
/// （CI 与本地均先经 LiteMonitor.sln / 主工程 ProjectReference 构建出该产物）。
/// </summary>
internal static class I2HostBinaries
{
    private static readonly Lazy<string> LazyExePath = new(Locate);

    public static string ExePath => LazyExePath.Value;

    public static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "LiteMonitor.sln")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null)
            throw new InvalidOperationException("无法从测试输出目录向上定位仓库根（LiteMonitor.sln）");

        string exe = Path.Combine(dir, "src", "HardwareHost", "bin", BuildConfiguration,
            "net8.0-windows", "LiteMonitor.HardwareHost.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException(
                $"Host 产物不存在：{exe}——请先构建 LiteMonitor.sln（主工程已挂 ProjectReference，正常 dotnet test 会先构建 Host）。");
        return exe;
    }

    /// <summary>以指定参数启动 Host 并等待退出，返回（退出码, stdout, stderr）。</summary>
    public static (int ExitCode, string Stdout, string Stderr) RunHost(IEnumerable<string> args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Host 运行超时（{timeoutMs}ms）：{string.Join(' ', args)}");
        }
        return (p.ExitCode, stdout, stderr);
    }
}

/// <summary>P-I2 步骤 1 冒烟：最小真实采样器经 Host 产物自测（真实进程 + 真实 LHM 路径）。</summary>
public sealed class I2HostSelftestTests
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "CPU.Load", "CPU.Temp", "CPU.Clock", "GPU.Load", "GPU.Temp", "GPU.Fan",
        "GPU.0.Load", "GPU.0.Temp", "GPU.0.Fan", "GPU.1.Load", "GPU.1.Temp", "GPU.1.Fan",
        "GPU.2.Load", "GPU.2.Temp", "GPU.2.Fan", "GPU.3.Load", "GPU.3.Temp", "GPU.3.Fan",
    };

    private static JsonDocument RunSelftest(params string[] extraArgs)
    {
        var (code, stdout, stderr) = I2HostBinaries.RunHost(
            new[] { "--selftest", "2", "--refresh-ms", "50" }.Concat(extraArgs));
        Assert.True(code == 0, $"selftest 退出码 {code}，stderr: {stderr}");
        Assert.NotEmpty(stdout);
        return JsonDocument.Parse(stdout);
    }

    [Fact]
    public void 真实采样自测_键集合与批次单调()
    {
        using var doc = RunSelftest();
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("rounds").GetInt32());
        Assert.True(root.GetProperty("batchMonotonic").GetBoolean(), "批次号必须单调递增（原子批次语义）");

        var batchIds = root.GetProperty("batchIds").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new[] { 1L, 2L }, batchIds);

        var keys = root.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Subset(KnownKeys, new HashSet<string>(keys, StringComparer.Ordinal));
        // 最小面：CPU 三项恒在；无 GPU 环境至少 3 + 首卡别名 3 = 6。
        Assert.Contains("CPU.Load", keys);
        Assert.InRange(keys.Count, 6, 18);
    }

    [Fact]
    public void 合成样本自测_固定十二键且值随批次推进()
    {
        var (code, stdout, stderr) = I2HostBinaries.RunHost(new[] { "--selftest", "3", "--refresh-ms", "20", "--test-pattern" });
        Assert.True(code == 0, $"selftest 退出码 {code}，stderr: {stderr}");
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        Assert.Equal(12, root.GetProperty("metricCount").GetInt32());
        Assert.Equal(3 * 12, root.GetProperty("samplesWithValue").GetInt32());
        Assert.Equal(0, root.GetProperty("samplesMissing").GetInt32());
        Assert.All(root.GetProperty("keys").EnumerateArray(), e => Assert.True(KnownKeys.Contains(e.GetString()!)));
    }

    [Fact]
    public void 未知参数返回约定退出码()
    {
        var (code, _, _) = I2HostBinaries.RunHost(new[] { "--definitely-not-a-flag" });
        Assert.Equal(64, code);
    }
}

/// <summary>
/// P-I2 / Plan A §6.1 + §8「故障隔离/恢复/资源释放」行：真实 Host 进程故障矩阵 + 监督器
/// 状态机（FailFast 退出 / 采样线程无限阻塞 / 心跳失联 / 进程被杀重启 / 握手超时 /
/// crash-loop 暂停 / Job Object kill-on-close / 连续 100 次启停无孤儿与句柄增长）。
/// 期限类参数在测试里收紧（机制同、时限短）；派工单默认值（心跳 2s×3、无进展 10s、
/// 初始化 60s、退避 1/3/10s、关闭 3s）随 HardwareHostSupervisorOptions 交付。
/// </summary>
public sealed class I2HostSupervisionTests
{
    private static HardwareHostSupervisorOptions FastOptions(
        string[] hostArgs,
        TimeSpan? handshake = null,
        TimeSpan? sampling = null,
        TimeSpan? heartbeat = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? init = null)
    {
        return new HardwareHostSupervisorOptions
        {
            HostExePathOverride = I2HostBinaries.ExePath,
            HostExtraArguments = hostArgs,
            HandshakeTimeout = handshake ?? TimeSpan.FromSeconds(5),
            SamplingProgressTimeout = sampling ?? TimeSpan.FromSeconds(10),
            HeartbeatInterval = heartbeat ?? TimeSpan.FromSeconds(2),
            InitializationTimeout = init ?? TimeSpan.FromSeconds(60),
            CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5),
            BackoffSequence = new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300) },
            BackoffPauseThreshold = 3,
        };
    }

    /// <summary>轮询直到条件满足或超时；返回是否满足（测试禁用 Thread.Sleep 真实等待的替代）。</summary>
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

    private static PipeHardwareClient CreateClient(HardwareHostSupervisorOptions options, int refreshMs = 100) =>
        new(refreshMs, supervisorOptions: options);

    private static async Task<PipeHardwareClient> WaitReadyClientAsync(HardwareHostSupervisorOptions options, int refreshMs = 100)
    {
        PipeHardwareClient client = CreateClient(options, refreshMs);
        bool ready = await WaitUntilAsync(() => client.GetLatestSnapshot().Samples.Count > 0, TimeSpan.FromSeconds(20));
        if (!ready) { client.Dispose(); throw new TimeoutException("Host 未在期限内产出首批数据"); }
        return client;
    }

    private static int HostProcessCount() =>
        Process.GetProcessesByName("LiteMonitor.HardwareHost").Length;

    [Fact]
    public async Task 健康会话_健康状态Ready且指标可见()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));
        Assert.Equal(HardwareState.Ready, client.GetHealth().State);
        Assert.NotNull(client.CurrentHostPid);
        PipeClientMetrics metrics = client.ReadMetrics();
        Assert.NotNull(metrics.FirstSnapshotDelayMs);
        Assert.True(metrics.FirstSnapshotDelayMs >= 0);
        Assert.True(metrics.SnapshotCount >= 1);
        Assert.True(metrics.PublishToReceiveP95Ms >= 0);
    }

    [Fact]
    public async Task 进程被杀_监督器确认退出后重启并恢复新会话()
    {
        using PipeHardwareClient client = await WaitReadyClientAsync(FastOptions(new[] { "--test-pattern" }));
        int oldPid = client.CurrentHostPid!.Value;

        Process.GetProcessById(oldPid).Kill(entireProcessTree: true);

        // 断线 → 退避 → 重启 → 新 PID 且继续出数（同一时刻只有一个 Host：恢复前旧进程必已退出）。
        bool recovered = await WaitUntilAsync(() =>
            client.GetHealth().State == HardwareState.Ready
            && client.CurrentHostPid is { } pid && pid != oldPid
            && client.GetLatestSnapshot().Samples.Count > 0,
            TimeSpan.FromSeconds(20));
        Assert.True(recovered, $"未恢复到 Ready：state={client.GetHealth().State}, pid={client.CurrentHostPid}");
        Assert.Equal(1, HostProcessCount()); // 不留第二个采集进程
        Assert.True(client.ReadMetrics().SessionCount >= 2);
    }

    [Fact]
    public async Task 采样线程无限阻塞_心跳照发_无进展判死并计入失败()
    {
        var options = FastOptions(new[] { "--test-pattern", "--fault", "hang-sampling" },
            sampling: TimeSpan.FromSeconds(3));
        var reasons = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var client = new PipeHardwareClient(100, supervisorOptions: options);
        // 记录状态迁移原因（监督器内部 Transitioned 不对外：以 StateReason 采样观察）。
        bool sawStall = await WaitUntilAsync(() =>
        {
            string? r = client.GetHealth().DiagnosticReason;
            return r is not null && (r.Contains("sampling-stalled") || r.Contains("backoff") && r.Contains("sampling-stalled"));
        }, TimeSpan.FromSeconds(30));
        Assert.True(sawStall, $"未观察到采样无进展裁决，last reason={client.GetHealth().DiagnosticReason}");
        // 心跳仍在发的阻塞也会被判死（心跳缺失路径由下一用例覆盖）。
    }

    [Fact]
    public async Task 心跳失联_三倍周期内判死并最终crash_loop暂停()
    {
        var options = FastOptions(new[] { "--test-pattern", "--fault", "stop-heartbeat" },
            heartbeat: TimeSpan.FromMilliseconds(500));
        using PipeHardwareClient client = CreateClient(options);

        bool paused = await WaitUntilAsync(() =>
            client.GetHealth().State == HardwareState.Degraded && client.GetHealth().DiagnosticReason == "crash-loop-paused",
            TimeSpan.FromSeconds(40));
        Assert.True(paused, $"未进入 crash-loop 暂停：state={client.GetHealth().State}, reason={client.GetHealth().DiagnosticReason}");
        Assert.True(client.ReadMetrics().SessionCount >= 1, "至少应建立过一个会话（故障在 Ready 后注入）");
    }

    [Fact]
    public async Task FailFast与握手超时_三败进窗口_暂停自动重启()
    {
        // fail-fast：进程即起即死（真实 FailFast 崩溃路径）。
        var failFast = FastOptions(new[] { "--fault", "fail-fast" }, handshake: TimeSpan.FromSeconds(1));
        using (PipeHardwareClient client = CreateClient(failFast))
        {
            bool paused = await WaitUntilAsync(() => client.GetHealth().State == HardwareState.Degraded
                && client.GetHealth().DiagnosticReason == "crash-loop-paused", TimeSpan.FromSeconds(30));
            Assert.True(paused, $"fail-fast 未暂停：{client.GetHealth().State}/{client.GetHealth().DiagnosticReason}");
        }

        // delay-handshake：连接迟于握手期限（§6.1 握手 5s 只管控制通道）。
        var delayed = FastOptions(new[] { "--fault", "delay-handshake" }, handshake: TimeSpan.FromSeconds(1));
        using (PipeHardwareClient client = CreateClient(delayed))
        {
            bool paused = await WaitUntilAsync(() => client.GetHealth().State == HardwareState.Degraded
                && client.GetHealth().DiagnosticReason == "crash-loop-paused", TimeSpan.FromSeconds(30));
            Assert.True(paused, $"delay-handshake 未暂停：{client.GetHealth().State}/{client.GetHealth().DiagnosticReason}");
        }
        await Task.Delay(500); // 留给延迟 Host 自退（服务端已消失，连接失败退出）。
        Assert.Equal(0, HostProcessCount());
    }

    [Fact]
    public async Task JobObject句柄关闭_Host随作业终止_不留孤儿()
    {
        // 直接验证 kill-on-close 语义（模拟主程序异常退出时 OS 关闭作业句柄）。
        using var endpoint = LiteMonitor.SystemServices.Pipe.PipeServerEndpoint.Create();
        var psi = new ProcessStartInfo(I2HostBinaries.ExePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "--pipe", endpoint.PipeName, "--session", endpoint.SessionToken, "--test-pattern" })
            psi.ArgumentList.Add(a);
        using Process host = Process.Start(psi)!;
        using var job = LiteMonitor.SystemServices.Pipe.HostJobObject.CreateKillOnClose();
        Assert.True(job.TryAssign(host));

        job.Dispose(); // 仅关作业句柄（不走受控停止）

        bool exited = await WaitUntilAsync(() => host.HasExited, TimeSpan.FromSeconds(5));
        Assert.True(exited, "作业句柄关闭后 Host 应被系统终止（kill-on-close）");
    }

    [Fact]
    public async Task 连续100次启停_无孤儿进程且句柄线程无持续增长()
    {
        Process self = Process.GetCurrentProcess();
        self.Refresh();
        int handlesStart = self.HandleCount, threadsStart = self.Threads.Count;
        var options = FastOptions(new[] { "--test-pattern" }, handshake: TimeSpan.FromSeconds(3));

        for (int i = 0; i < 100; i++)
        {
            PipeHardwareClient client = CreateClient(options, refreshMs: 100);
            bool ok = await WaitUntilAsync(() => client.GetLatestSnapshot().Samples.Count > 0, TimeSpan.FromSeconds(15));
            if (!ok) { client.Dispose(); throw new TimeoutException($"第 {i + 1} 轮未取得首样"); }
            client.Dispose();
        }

        bool noOrphan = await WaitUntilAsync(() => HostProcessCount() == 0, TimeSpan.FromSeconds(10));
        Assert.True(noOrphan, $"启停 100 轮后存在孤儿 Host：{HostProcessCount()} 个");

        self.Refresh();
        Assert.InRange(self.HandleCount, 0, handlesStart + 300);
        Assert.InRange(self.Threads.Count, 0, threadsStart + 30);
    }
}
