using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// E08a：I2HostBinaries 定位器行为测试（完善计划 §12 第一条 / 审查报告 G5 收口）。
/// 全部经由注入式核心 <see cref="I2HostBinaries.Locate"/> 驱动：以临时目录构造伪
/// "仓库根 / 本轮构建清单锚点 / 测试输出目录 / Host 目录"，不触碰进程环境变量、
/// 不触发真实构建，可确定性复现以下场景：
///
///   · 自定义输出目录（-p:BaseOutputPath=bin/review-xxx/）+ 环境变量显式指定 → 采用并记录摘要；
///   · 默认目录为旧产物（G5 本体：清单 fresh、默认目录 stale）→ 直接失败，不静默挑旧；
///   · 显式路径缺失/指错/摘要不符 → 直接失败，不回退默认目录；
///   · 默认目录与清单一致 → 通过（G5 复核口径：SHA-256 一致才算同一产物）。
/// </summary>
public sealed class I2HostBinariesLocatorTests : IDisposable
{
    private const string Config = "Debug";

    /// <summary>伪仓库根（临时目录）。</summary>
    private readonly string _root;

    /// <summary>伪测试输出目录：&lt;root&gt;\tests\LiteMonitor.Tests\bin\review-e08a\Debug\net8.0-windows。</summary>
    private readonly string _testOutputDir;

    /// <summary>伪"本轮构建清单"锚点（主工程输出目录）：&lt;root&gt;\bin\review-e08a\Debug\net8.0-windows。</summary>
    private readonly string _manifestDir;

    /// <summary>默认构建目录：&lt;root&gt;\src\HardwareHost\bin\Debug\net8.0-windows。</summary>
    private readonly string _defaultHostDir;

    /// <summary>自定义输出目录下的 Host 目录：&lt;root&gt;\src\HardwareHost\bin\review-e08a\Debug\net8.0-windows。</summary>
    private readonly string _customHostDir;

    public I2HostBinariesLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "i2hostbinaries-" + Guid.NewGuid().ToString("N"));
        _testOutputDir = Path.Combine(_root, "tests", "LiteMonitor.Tests", "bin", "review-e08a", Config, "net8.0-windows");
        _manifestDir = Path.Combine(_root, "bin", "review-e08a", Config, "net8.0-windows");
        _defaultHostDir = Path.Combine(_root, "src", "HardwareHost", "bin", Config, "net8.0-windows");
        _customHostDir = Path.Combine(_root, "src", "HardwareHost", "bin", "review-e08a", Config, "net8.0-windows");
        Directory.CreateDirectory(_testOutputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>写入一个伪产物文件（内容按 seed 变化，保证不同 seed 摘要必不同）。</summary>
    private static string WriteArtifact(string dir, string fileName, int seed)
    {
        Directory.CreateDirectory(dir);
        byte[] bytes = Encoding.UTF8.GetBytes(new string((char)('A' + seed % 26), 64) + seed.ToString("X8"));
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>在指定 Host 目录布置一套 seed=0 的完整伪产物。</summary>
    private static void SeedHostArtifacts(string dir, int seed = 0)
    {
        int i = 0;
        foreach (string name in I2HostBinaries.ManifestFileNames) WriteArtifact(dir, name, seed + i++);
    }

    /// <summary>把源目录的全部伪产物按字节复制到目标目录（同一轮构建的两个目录应当一致）。</summary>
    private static void CopyArtifacts(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string name in I2HostBinaries.ManifestFileNames)
            File.Copy(Path.Combine(sourceDir, name), Path.Combine(targetDir, name));
    }

    // ── G5 修复路径：环境变量显式指定本轮自定义输出目录产物 ──

    [Fact]
    public void 环境变量显式路径_与清单一致_采用显式路径并记录摘要()
    {
        SeedHostArtifacts(_customHostDir);
        CopyArtifacts(_customHostDir, _manifestDir); // 主工程输出目录 = 本轮复制目标
        // 测试输出目录的 Contracts 引用副本与自定义目录一致（同一轮构建）。
        File.Copy(Path.Combine(_customHostDir, "LiteMonitor.Contracts.dll"),
            Path.Combine(_testOutputDir, "LiteMonitor.Contracts.dll"));

        string exe = Path.Combine(_customHostDir, I2HostBinaries.HostExeFileName);
        I2HostArtifact artifact = I2HostBinaries.Locate(exe, _root, _testOutputDir, Config);

        Assert.Equal("LITEMONITOR_HOST_EXE", artifact.ResolvedVia);
        Assert.Equal(exe, artifact.ExePath);
        Assert.Equal(_manifestDir, artifact.ManifestDirectory);
        Assert.Equal(I2HostBinaries.ManifestFileNames.Length, artifact.Sha256ByFile.Count);
        Assert.All(artifact.Sha256ByFile.Values, h => Assert.Equal(64, h.Length));
        Assert.Equal(
            I2HostBinaries.ComputeSha256(exe.Replace(I2HostBinaries.HostExeFileName, "LiteMonitor.HardwareHost.dll")),
            artifact.Sha256ByFile["LiteMonitor.HardwareHost.dll"]);
    }

    [Fact]
    public void 环境变量相对路径_按仓库根解析()
    {
        SeedHostArtifacts(_customHostDir);
        CopyArtifacts(_customHostDir, _manifestDir);
        File.Copy(Path.Combine(_customHostDir, "LiteMonitor.Contracts.dll"),
            Path.Combine(_testOutputDir, "LiteMonitor.Contracts.dll"));

        string relative = Path.Combine("src", "HardwareHost", "bin", "review-e08a", Config, "net8.0-windows",
            I2HostBinaries.HostExeFileName);
        I2HostArtifact artifact = I2HostBinaries.Locate(relative, _root, _testOutputDir, Config);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, relative)), artifact.ExePath);
    }

    [Fact]
    public void 环境变量路径缺失_直接失败_不回退默认目录()
    {
        // 默认目录完整且与清单一致：即便如此，显式路径缺失也必须失败（不允许静默回退挑目录）。
        SeedHostArtifacts(_defaultHostDir);
        CopyArtifacts(_defaultHostDir, _manifestDir);

        string missing = Path.Combine(_customHostDir, I2HostBinaries.HostExeFileName);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            I2HostBinaries.Locate(missing, _root, _testOutputDir, Config));
        Assert.Contains("LITEMONITOR_HOST_EXE", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void 环境变量路径文件名不对_直接失败()
    {
        SeedHostArtifacts(_customHostDir);
        CopyArtifacts(_customHostDir, _manifestDir);
        string wrong = WriteArtifact(_customHostDir, "LiteMonitor++.exe", 9);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            I2HostBinaries.Locate(wrong, _root, _testOutputDir, Config));
        Assert.Contains(I2HostBinaries.HostExeFileName, ex.Message);
    }

    // ── G5 本体复现：默认目录是旧产物时必须失败 ──

    [Fact]
    public void G5复现_默认目录旧产物与清单不符_直接失败不静默挑旧()
    {
        // G5 场景：本轮构建落在自定义输出目录（清单锚点 = 主工程输出目录，内容 fresh），
        // 默认目录里躺着上一轮的旧 Host。定位器不得静默挑旧产物。
        SeedHostArtifacts(_manifestDir, seed: 100);        // 本轮清单（fresh）
        SeedHostArtifacts(_defaultHostDir, seed: 0);       // 旧产物（stale，字节不同）
        File.Copy(Path.Combine(_manifestDir, "LiteMonitor.Contracts.dll"),
            Path.Combine(_testOutputDir, "LiteMonitor.Contracts.dll"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            I2HostBinaries.Locate(null, _root, _testOutputDir, Config));
        Assert.Contains("LiteMonitor.HardwareHost.dll", ex.Message);
        Assert.Contains("≠", ex.Message);
        // 异常必须同时给出两侧摘要与修复指引（可复核、可行动）。
        Assert.Contains("SHA", ex.Message);
        Assert.Contains("LITEMONITOR_HOST_EXE", ex.Message);
        Assert.Contains("dotnet build", ex.Message);
    }

    [Fact]
    public void G5复现_默认目录Contracts陈旧_直接失败()
    {
        // 变体：Host 程序集一致但 Contracts 旧（依赖层漂移同样不允许）。
        SeedHostArtifacts(_manifestDir, seed: 100);
        SeedHostArtifacts(_defaultHostDir, seed: 0);
        // 让默认目录的 Host 程序集与清单一致，仅保留 Contracts 陈旧：
        File.Copy(Path.Combine(_manifestDir, "LiteMonitor.HardwareHost.dll"),
            Path.Combine(_defaultHostDir, "LiteMonitor.HardwareHost.dll"), overwrite: true);
        File.Copy(Path.Combine(_manifestDir, "LiteMonitor.HardwareHost.exe"),
            Path.Combine(_defaultHostDir, "LiteMonitor.HardwareHost.exe"), overwrite: true);
        File.Copy(Path.Combine(_manifestDir, "LiteMonitor.HardwareHost.deps.json"),
            Path.Combine(_defaultHostDir, "LiteMonitor.HardwareHost.deps.json"), overwrite: true);
        File.Copy(Path.Combine(_manifestDir, "LiteMonitor.HardwareHost.runtimeconfig.json"),
            Path.Combine(_defaultHostDir, "LiteMonitor.HardwareHost.runtimeconfig.json"), overwrite: true);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            I2HostBinaries.Locate(null, _root, _testOutputDir, Config));
        Assert.Contains("LiteMonitor.Contracts.dll", ex.Message);
    }

    // ── 缺失即失败 ──

    [Fact]
    public void 默认目录缺失_直接失败并给出构建指引()
    {
        SeedHostArtifacts(_manifestDir, seed: 100);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            I2HostBinaries.Locate(null, _root, _testOutputDir, Config));
        Assert.Contains("Host 产物不存在", ex.Message);
        Assert.Contains("dotnet test", ex.Message);
        Assert.Contains("LITEMONITOR_HOST_EXE", ex.Message);
    }

    [Fact]
    public void 清单锚点缺失_直接失败_不接受不可验证的产物()
    {
        SeedHostArtifacts(_defaultHostDir);
        // 主工程输出目录不存在（主工程未参与本轮构建）：无法证明 Host 是本轮产物 → 失败。
        var ex = Assert.Throws<InvalidOperationException>(() =>
            I2HostBinaries.Locate(null, _root, _testOutputDir, Config));
        Assert.Contains("构建清单", ex.Message);
    }

    // ── 正常路径 ──

    [Fact]
    public void 默认目录与清单一致_通过并记录摘要()
    {
        SeedHostArtifacts(_defaultHostDir);
        CopyArtifacts(_defaultHostDir, _manifestDir);
        File.Copy(Path.Combine(_defaultHostDir, "LiteMonitor.Contracts.dll"),
            Path.Combine(_testOutputDir, "LiteMonitor.Contracts.dll"));

        I2HostArtifact artifact = I2HostBinaries.Locate(null, _root, _testOutputDir, Config);
        Assert.Equal("DefaultBuildOutput", artifact.ResolvedVia);
        Assert.Equal(Path.Combine(_defaultHostDir, I2HostBinaries.HostExeFileName), artifact.ExePath);
        Assert.Equal(I2HostBinaries.ManifestFileNames.Length, artifact.Sha256ByFile.Count);
    }

    [Fact]
    public void 环境变量空白值_视为未设置()
    {
        SeedHostArtifacts(_defaultHostDir);
        CopyArtifacts(_defaultHostDir, _manifestDir);
        File.Copy(Path.Combine(_defaultHostDir, "LiteMonitor.Contracts.dll"),
            Path.Combine(_testOutputDir, "LiteMonitor.Contracts.dll"));

        I2HostArtifact artifact = I2HostBinaries.Locate("   ", _root, _testOutputDir, Config);
        Assert.Equal("DefaultBuildOutput", artifact.ResolvedVia);
    }

    // ── 清单锚点推导 ──

    [Fact]
    public void 清单锚点推导_自定义输出目录随测试输出布局流动()
    {
        string derived = I2HostBinaries.DeriveMainOutputDirectory(_root, _testOutputDir);
        Assert.Equal(Path.GetFullPath(_manifestDir), Path.GetFullPath(derived!));

        // 默认布局：tests\LiteMonitor.Tests\bin\Debug\net8.0-windows ⇒ <root>\bin\Debug\net8.0-windows。
        string defaultTestOut = Path.Combine(_root, "tests", "LiteMonitor.Tests", "bin", "Debug", "net8.0-windows");
        string derivedDefault = I2HostBinaries.DeriveMainOutputDirectory(_root, defaultTestOut);
        Assert.Equal(Path.Combine(_root, "bin", "Debug", "net8.0-windows"), derivedDefault);
    }

    [Fact]
    public void 清单锚点推导_非标准布局返回null()
    {
        string odd = Path.Combine(_root, "some-publish-dir");
        Assert.Null(I2HostBinaries.DeriveMainOutputDirectory(_root, odd));
    }
}
