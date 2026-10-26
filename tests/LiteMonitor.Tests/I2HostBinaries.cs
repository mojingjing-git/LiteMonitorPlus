using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace LiteMonitor.Tests;

/// <summary>
/// I2HostBinaries 的定位/校验结果：本轮 I2 测试实际使用的 Host 产物及其证据摘要。
/// 供验收报告与证据收集工具（scripts/collect-evidence.ps1 口径）直接取数，
/// 避免报告里的摘要与测试实际执行路径脱节（G5 的"记录摘要"要求）。
/// </summary>
internal sealed record I2HostArtifact(
    string ExePath,
    string ResolvedVia,
    string HostDirectory,
    string ManifestDirectory,
    IReadOnlyDictionary<string, string> Sha256ByFile);

/// <summary>
/// P-I2 §2.6 + E08 基础（完善计划 §12 前两条 / 审查报告 G5）：Host 产物定位器。
///
/// 解析顺序（G5 收口，不再在 Debug/Release/其他工作树之间静默挑产物）：
///   1. 环境变量 <see cref="HostExeEnvVar"/> 显式指定本轮 Host 产物（绝对路径；相对路径按仓库根解析）；
///   2. 否则使用默认构建目录 src/HardwareHost/bin/&lt;Configuration&gt;/net8.0-windows/
///      （CI 与本地经 LiteMonitor.sln / 主工程 ProjectReference 构建出该产物）。
///
/// 无论走哪条路径，都必须通过与"本轮构建清单"的逐文件 SHA-256 校验：
/// 清单锚点 = 主工程输出目录（LiteMonitor.csproj 的 CopyHardwareHostToOutput 目标把
/// src/HardwareHost/$(BaseOutputPath)$(Configuration) 下的 Host 产物复制到主输出，
/// 与 dotnet test 的构建同轮、且随 BaseOutputPath 流动）。目录缺失或摘要不符时
/// <b>直接失败</b>，不回退、不挑旧产物——宁可红，不可假绿。
/// </summary>
internal static class I2HostBinaries
{
    /// <summary>显式 Host 产物路径环境变量（E08a 引入）。</summary>
    public const string HostExeEnvVar = "LITEMONITOR_HOST_EXE";

    public const string HostExeFileName = "LiteMonitor.HardwareHost.exe";

    /// <summary>
    /// 必须与"本轮构建清单"逐字节一致的文件（相对 Host 目录）。
    /// 全部是 CopyHardwareHostToOutput 复制进主输出目录的产物 + 同轮构建的 Contracts。
    /// </summary>
    internal static readonly string[] ManifestFileNames =
    {
        "LiteMonitor.HardwareHost.dll",
        "LiteMonitor.HardwareHost.exe",
        "LiteMonitor.HardwareHost.deps.json",
        "LiteMonitor.HardwareHost.runtimeconfig.json",
        "LiteMonitor.Contracts.dll",
    };

    private static readonly Lazy<I2HostArtifact> LazyArtifact = new(LocateForTestRun);

    /// <summary>本轮实际使用的 Host 可执行文件路径（首次访问时定位并校验，失败即抛）。</summary>
    public static string ExePath => LazyArtifact.Value.ExePath;

    /// <summary>本轮定位/校验结果（路径、来源、各文件 SHA-256），供证据记录。</summary>
    public static I2HostArtifact Artifact => LazyArtifact.Value;

    public static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    /// <summary>生产入口：接线真实环境（环境变量、测试输出目录、构建配置）后走可注入核心。</summary>
    private static I2HostArtifact LocateForTestRun()
    {
        I2HostArtifact artifact = Locate(
            Environment.GetEnvironmentVariable(HostExeEnvVar),
            FindRepoRoot(AppContext.BaseDirectory),
            AppContext.BaseDirectory,
            BuildConfiguration);
        // 证据行（G5"记录摘要"）：随 xunit 输出进 TRX StdOut，报告与实际执行路径同源。
        Console.WriteLine(
            $"[I2HostBinaries] via={artifact.ResolvedVia} config={BuildConfiguration} exe={artifact.ExePath} " +
            string.Join(" ", artifact.Sha256ByFile.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"SHA256({kv.Key})={kv.Value}")) +
            $" manifest={artifact.ManifestDirectory}");
        return artifact;
    }

    /// <summary>
    /// 可注入核心：定位并校验 Host 产物。单元测试（I2HostBinariesLocatorTests）以伪产物目录驱动，
    /// 不触碰进程环境变量、不触发真实构建。
    /// </summary>
    /// <param name="explicitHostExe">环境变量原始值（null/空白 = 未设置）。</param>
    /// <param name="repoRoot">仓库根（含 LiteMonitor.sln）。</param>
    /// <param name="testOutputDirectory">测试程序集输出目录（AppContext.BaseDirectory）。</param>
    /// <param name="buildConfiguration">构建配置（Debug/Release）。</param>
    internal static I2HostArtifact Locate(
        string? explicitHostExe, string repoRoot, string testOutputDirectory, string buildConfiguration)
    {
        string manifestDir = DeriveMainOutputDirectory(repoRoot, testOutputDirectory)
            ?? throw new InvalidOperationException(
                $"无法从测试输出目录 {testOutputDirectory} 推导主工程输出目录（清单锚点）。" +
                $"期望布局：…\\tests\\LiteMonitor.Tests\\bin\\[自定义]\\{buildConfiguration}\\net8.0-windows\\。" +
                "请改用常规 dotnet test / dotnet build 流程（或 -p:BaseOutputPath=…），不接受绕过构建清单的产物。");

        string hostDir;
        string exePath;
        string via;
        if (!string.IsNullOrWhiteSpace(explicitHostExe))
        {
            via = HostExeEnvVar;
            string raw = explicitHostExe.Trim();
            exePath = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(repoRoot, raw));
            if (!File.Exists(exePath))
                throw new InvalidOperationException(
                    $"环境变量 {HostExeEnvVar} 指向的 Host 产物不存在：{exePath}。" +
                    "不回退默认目录——请修正该变量为**本轮构建**的 LiteMonitor.HardwareHost.exe，或删除变量后重建默认目录。");
            if (!string.Equals(Path.GetFileName(exePath), HostExeFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"环境变量 {HostExeEnvVar} 必须指向 {HostExeFileName}（实际：{exePath}）。");
            hostDir = Path.GetDirectoryName(exePath)!;
        }
        else
        {
            via = "DefaultBuildOutput";
            hostDir = Path.Combine(repoRoot, "src", "HardwareHost", "bin", buildConfiguration, "net8.0-windows");
            exePath = Path.Combine(hostDir, HostExeFileName);
            if (!File.Exists(exePath))
                throw new InvalidOperationException(
                    $"Host 产物不存在：{exePath}——请先构建 LiteMonitor.sln（主工程已挂 ProjectReference，" +
                    $"正常 dotnet test 会先构建 Host）。若本轮构建使用了自定义输出目录" +
                    $"（-p:BaseOutputPath=bin/xxx/），请设置环境变量 {HostExeEnvVar}=" +
                    $"…\\src\\HardwareHost\\bin\\xxx\\{buildConfiguration}\\net8.0-windows\\{HostExeFileName}。" +
                    "不在 Debug/Release/其他工作树之间静默挑旧产物（G5）。");
        }

        IReadOnlyDictionary<string, string> hashes =
            VerifyAgainstBuildManifest(hostDir, manifestDir, testOutputDirectory, buildConfiguration);
        return new I2HostArtifact(exePath, via, hostDir, manifestDir, hashes);
    }

    /// <summary>
    /// 由测试输出目录推导主工程输出目录（"本轮构建清单"锚点）。
    /// …\tests\LiteMonitor.Tests\bin\&lt;profile&gt;\&lt;Config&gt;\net8.0-windows
    ///   ⇒ &lt;repoRoot&gt;\bin\&lt;profile&gt;\&lt;Config&gt;\net8.0-windows
    /// （CopyHardwareHostToOutput 的复制目标；&lt;profile&gt; 为空时即默认 bin\&lt;Config&gt;\…）
    /// </summary>
    internal static string? DeriveMainOutputDirectory(string repoRoot, string testOutputDirectory)
    {
        string full = Path.GetFullPath(testOutputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string marker = $"{Path.DirectorySeparatorChar}LiteMonitor.Tests{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        int idx = full.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string suffix = full[(idx + marker.Length)..]; // "<profile>\<Config>\net8.0-windows" 或 "<Config>\net8.0-windows"
        return Path.GetFullPath(Path.Combine(repoRoot, "bin", suffix));
    }

    /// <summary>从任意目录向上定位仓库根（含 LiteMonitor.sln 的最近祖先）。</summary>
    internal static string FindRepoRoot(string startDirectory)
    {
        string? dir = Path.GetFullPath(startDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir, "LiteMonitor.sln")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null)
            throw new InvalidOperationException("无法从测试输出目录向上定位仓库根（LiteMonitor.sln）");
        return dir;
    }

    /// <summary>
    /// 逐文件校验 Host 目录与"本轮构建清单"（主工程输出目录锚点 + 测试输出目录内的 Contracts 引用副本）。
    /// 任何缺失/摘要不符都直接失败，异常文本同时给出两侧 SHA-256 与修复指引。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> VerifyAgainstBuildManifest(
        string hostDir, string manifestDir, string testOutputDirectory, string buildConfiguration)
    {
        var problems = new List<string>();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in ManifestFileNames)
        {
            string manifestFile = Path.Combine(manifestDir, name);
            if (!File.Exists(manifestFile)) continue; // 清单中不存在的文件无从比对（如未复制项）
            string hostFile = Path.Combine(hostDir, name);
            if (!File.Exists(hostFile))
            {
                problems.Add($"  - {name}: Host 目录缺失，本轮构建清单中存在");
                continue;
            }
            string hostHash = ComputeSha256(hostFile);
            string manifestHash = ComputeSha256(manifestFile);
            hashes[name] = hostHash;
            if (!string.Equals(hostHash, manifestHash, StringComparison.OrdinalIgnoreCase))
                problems.Add($"  - {name}: Host 目录 {hostHash} ≠ 本轮构建清单 {manifestHash}");
        }

        // 第二锚点：测试输出目录里的 Contracts 引用副本（本轮构建随引用复制，独立于复制目标）。
        string contractsInTestOutput = Path.Combine(testOutputDirectory, "LiteMonitor.Contracts.dll");
        string contractsInHostDir = Path.Combine(hostDir, "LiteMonitor.Contracts.dll");
        if (File.Exists(contractsInTestOutput) && File.Exists(contractsInHostDir))
        {
            string hostHash = ComputeSha256(contractsInHostDir);
            string testOutHash = ComputeSha256(contractsInTestOutput);
            hashes["LiteMonitor.Contracts.dll"] = hostHash;
            if (!string.Equals(hostHash, testOutHash, StringComparison.OrdinalIgnoreCase))
                problems.Add($"  - LiteMonitor.Contracts.dll: Host 目录 {hostHash} ≠ 测试输出目录引用副本 {testOutHash}");
        }

        if (hashes.Count == 0)
            problems.Add($"  - 构建清单锚点目录为空或不存在：{manifestDir}（主工程未构建？）");

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Host 产物与本轮构建清单不符，拒绝执行 I2 测试（G5 收口：不在 Debug/Release/其他工作树之间" +
                "静默挑旧产物）。\n" +
                $"  Host 目录:      {hostDir}\n" +
                $"  构建清单锚点:   {manifestDir}（主工程 CopyHardwareHostToOutput 本轮复制目标）\n" +
                $"  配置:           {buildConfiguration}\n" +
                string.Join("\n", problems) + "\n" +
                "处理方式（任选其一）：\n" +
                $"  1) dotnet build LiteMonitor.sln -c {buildConfiguration}（重建默认目录并刷新清单）；\n" +
                "  2) 若本轮使用了 -p:BaseOutputPath=bin/xxx/，设置环境变量 " + HostExeEnvVar + "=" +
                "…\\src\\HardwareHost\\bin\\xxx\\<配置>\\net8.0-windows\\" + HostExeFileName + "；\n" +
                "  3) --no-build 复跑前，确认清单与 Host 目录来自同一轮构建（比对 SHA-256）。");
        }
        return hashes;
    }

    /// <summary>文件的 SHA-256（大写十六进制）。证据记录与测试共用同一口径。</summary>
    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
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
