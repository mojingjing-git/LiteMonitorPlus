using System;
using System.Collections.Generic;
using System.Text.Json;
using LiteMonitor.Contracts;

namespace LiteMonitor.HardwareHost;

/// <summary>Host 命令行选项。管道模式参数由主程序监督器生成；故障注入与合成样本
/// 仅为测试参数（P-I2 §2.6：以本工程产物 + 注入故障参数运行故障矩阵）。</summary>
internal sealed record HostOptions
{
    public string? PipeName { get; init; }
    public string? SessionToken { get; init; }
    public int ParentPid { get; init; } = -1;
    public int RefreshMs { get; init; } = 1000;
    public int HeartbeatMs { get; init; } = 2000;
    public bool TestPattern { get; init; }
    public bool StaleFirst { get; init; }
    public string? Fault { get; init; }
    public int? SelftestRounds { get; init; }

    public static HostOptions? Parse(string[] args)
    {
        var opt = new HostOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--pipe": opt = opt with { PipeName = Next() }; break;
                case "--session": opt = opt with { SessionToken = Next() }; break;
                case "--parent-pid":
                    if (int.TryParse(Next(), out int pid)) opt = opt with { ParentPid = pid };
                    break;
                case "--refresh-ms":
                    if (int.TryParse(Next(), out int ms)) opt = opt with { RefreshMs = ms };
                    break;
                case "--heartbeat-ms":
                    if (int.TryParse(Next(), out int hb)) opt = opt with { HeartbeatMs = Math.Max(100, hb) };
                    break;
                case "--test-pattern": opt = opt with { TestPattern = true }; break;
                case "--stale-first": opt = opt with { StaleFirst = true }; break;
                case "--fault": opt = opt with { Fault = Next() }; break;
                case "--selftest":
                    string? rounds = Next();
                    if (int.TryParse(rounds, out int n) && n > 0) opt = opt with { SelftestRounds = n };
                    break;
                default: return null; // 未知参数 → 64（与探针退出码契约一致）
            }
        }
        if (opt.SelftestRounds is null && (opt.PipeName is null || opt.SessionToken is null))
            return null;
        return opt;
    }
}

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitArgError = 64;

    internal static int Main(string[] args)
    {
        HostOptions? options = HostOptions.Parse(args);
        if (options == null)
        {
            Console.Error.WriteLine(
                "usage: LiteMonitor.HardwareHost --pipe <name> --session <token> [--parent-pid <pid>] [--refresh-ms <n>]" +
                " [--test-pattern] [--stale-first] [--fault <fail-fast|crash-after-handshake|hang-sampling|stop-heartbeat|delay-handshake|bad-version|bad-session|late-response>]" +
                " | --selftest <rounds>");
            return ExitArgError;
        }

        if (options.SelftestRounds is int rounds)
            return RunSelftest(options, rounds);

        return HostPipeSession.RunAsync(options).GetAwaiter().GetResult();
    }

    /// <summary>自测模式：跑 N 轮真实采样，stdout 输出 JSON 摘要后退出（供测试与实机核查）。</summary>
    private static int RunSelftest(HostOptions options, int rounds)
    {
        var summary = new Dictionary<string, object?>
        {
            ["testPattern"] = options.TestPattern,
            ["refreshMs"] = options.RefreshMs,
        };
        var batchIds = new List<long>();
        var durationsMs = new List<double>();
        var keys = new List<string>();
        int missing = 0, withValue = 0;
        long lastBatch = -1;
        bool monotonic = true;

        using (var sampler = new MinimalHardwareSampler(options.TestPattern))
        {
            summary["generation"] = sampler.Generation;
            for (int r = 0; r < rounds; r++)
            {
                HardwareSnapshot snap = sampler.SampleOnce(configRevision: -1);
                if (snap.BatchId <= lastBatch) monotonic = false;
                lastBatch = snap.BatchId;
                batchIds.Add(snap.BatchId);
                durationsMs.Add(Math.Round(snap.SamplingDuration.TotalMilliseconds, 2));
                foreach (var s in snap.Samples)
                {
                    if (r == 0) keys.Add(s.Key);
                    if (s.Value.HasValue) withValue++; else missing++;
                }
                if (r + 1 < rounds) System.Threading.Thread.Sleep(Math.Max(0, options.RefreshMs));
            }
        }

        summary["rounds"] = rounds;
        summary["batchIds"] = batchIds;
        summary["batchMonotonic"] = monotonic;
        summary["samplingDurationMs"] = durationsMs;
        summary["keys"] = keys;
        summary["metricCount"] = keys.Count;
        summary["samplesWithValue"] = withValue;
        summary["samplesMissing"] = missing;
        Console.Out.Write(JsonSerializer.Serialize(summary));
        return ExitOk;
    }
}
