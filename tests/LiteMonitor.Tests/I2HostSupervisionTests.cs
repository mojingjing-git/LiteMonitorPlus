using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.Tests;
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
