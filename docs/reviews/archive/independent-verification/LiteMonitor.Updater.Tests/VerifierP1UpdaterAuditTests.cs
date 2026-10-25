using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteMonitor.Updater;
using Xunit;

namespace LiteMonitor.Updater.Tests;

/// <summary>
/// ★ **独立复核 · LM-069 / R05 · 旧客户端 → 新更新器协议桥接（更新器侧）**
/// <para>复核者自写用例。核心是"**完整性校验不得被削弱**"这条硬红线：
/// 单参数调用仍然被拒、退出码仍然是 6；装配了"可信摘要来源"之后，
/// 摘要不匹配 / 来源不可用 / 验签失败 **一律仍然拒绝**。</para>
/// </summary>
public sealed class VerifierP1UpdaterAuditTests
{
    private const string MarkerFileName = "update_legacy_bridge.json";

    private sealed class TempInstall : IDisposable
    {
        public string Dir { get; }
        public string ZipPath { get; }

        public TempInstall()
        {
            Dir = Path.Combine(Path.GetTempPath(), "verifier-upd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);

            // 让 GetMainProgramDirectory 找得到"主程序目录"
            File.WriteAllBytes(Path.Combine(Dir, "LiteMonitor++.exe"), new byte[] { 0x4D, 0x5A });

            ZipPath = Path.Combine(Dir, "update.zip");
            using (var fs = new FileStream(ZipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("payload.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("hello");
            }
        }

        public string Sha256OfZip() =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ZipPath)));

        public string ErrorLogPath => Path.Combine(Dir, "update_error.log");

        public string ErrorLogText =>
            File.Exists(ErrorLogPath) ? File.ReadAllText(ErrorLogPath) : "";

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------- V1

    /// <summary>
    /// V1（**载荷用例**）：单参数（旧客户端）调用**仍然被拒**，退出码**仍然是 6**。
    /// </summary>
    [Fact]
    public void V1_单参数调用仍然被拒且退出码仍为6()
    {
        using var temp = new TempInstall();

        int exit = Program.RunFromArgs(new[] { temp.ZipPath }, temp.Dir);

        Assert.Equal(6, exit);
        Assert.Equal(Program.ExitIntegrityRejected, exit);
    }

    // ---------------------------------------------------------------- V2

    /// <summary>
    /// V2（**载荷用例**）：拒绝时**写出了机器可读标记**，且内容含原因与手动升级指引。
    /// </summary>
    [Fact]
    public void V2_拒绝时写出含原因与手动升级指引的机器可读标记()
    {
        using var temp = new TempInstall();

        Program.RunFromArgs(new[] { temp.ZipPath }, temp.Dir);

        string marker = Path.Combine(temp.Dir, MarkerFileName);
        Assert.True(File.Exists(marker), "拒绝时必须写出机器可读标记。");

        using var doc = JsonDocument.Parse(File.ReadAllText(marker));
        var root = doc.RootElement;

        Assert.Equal("liteMonitor.legacyBridge/v1", root.GetProperty("schema").GetString());
        Assert.Equal("missing_digest_rejected", root.GetProperty("code").GetString());

        string reason = root.GetProperty("reason").GetString() ?? "";
        Assert.True(reason.Length > 0, "标记必须含原因。");
        Assert.True(reason.Contains("摘要", StringComparison.Ordinal), "原因必须点明缺的是摘要：" + reason);

        string guidance = root.GetProperty("guidance").GetString() ?? "";
        Assert.True(guidance.Contains("手动下载", StringComparison.Ordinal), "指引必须让用户手动下载。");

        string url = root.GetProperty("manualDownloadUrl").GetString() ?? "";
        Assert.True(url.StartsWith("https://", StringComparison.Ordinal), "发布页地址必须是 https：" + url);

        // 不得含任何私钥材料
        string raw = File.ReadAllText(marker);
        Assert.False(raw.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase));
        Assert.False(raw.Contains("privateKey", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- V3

    /// <summary>
    /// V3（**载荷用例**）：被拒时**没有解压任何文件**（"验不过也放行"的红线）。
    /// </summary>
    [Fact]
    public void V3_被拒时没有解压任何文件()
    {
        using var temp = new TempInstall();

        int exit = Program.RunFromArgs(new[] { temp.ZipPath }, temp.Dir);
        Assert.Equal(6, exit);

        Assert.False(
            File.Exists(Path.Combine(temp.Dir, "payload.txt")),
            "被拒时不得把更新包内容解压到目标目录。");
        Assert.False(
            File.Exists(Path.Combine(temp.Dir, "update_in_progress")),
            "被拒时不得留下 in_progress 标记。");
        Assert.False(
            Directory.Exists(Path.Combine(temp.Dir, "_update_tmp_20260101_000000")),
            "被拒时不得留下临时目录。");

        // 确实走进了"缺少期望摘要"的拒绝分支（不是无关失败）
        Assert.True(temp.ErrorLogText.Contains("没有提供期望 SHA-256 摘要", StringComparison.Ordinal),
            "必须走进「缺少期望摘要」的拒绝分支：" + temp.ErrorLogText);
    }

    // ---------------------------------------------------------------- V4

    /// <summary>
    /// V4（**载荷用例**）：装配了"可信摘要来源"但**摘要不匹配** ⇒ 仍然拒绝。
    /// </summary>
    [Fact]
    public void V4_可信摘要来源给出不匹配的摘要仍然拒绝()
    {
        using var temp = new TempInstall();

        var source = new FixedDigestSource(new string('b', 64));
        var options = new UpdaterRunOptions { TrustedDigestSource = source, RestartMain = false, WaitForMainExit = false };

        int exit = Program.RunFromArgs(new[] { temp.ZipPath }, temp.Dir, options);

        Assert.Equal(6, exit);
        Assert.True(temp.ErrorLogText.Contains("摘要与期望值不一致", StringComparison.Ordinal),
            "必须走进「摘要不匹配」的拒绝分支：" + temp.ErrorLogText);
        Assert.False(File.Exists(Path.Combine(temp.Dir, "payload.txt")));
    }

    // ---------------------------------------------------------------- V5

    /// <summary>
    /// V5（**载荷用例**）：来源**不可用**（返回 null）⇒ 仍然拒绝。
    /// </summary>
    [Fact]
    public void V5_可信摘要来源不可用仍然拒绝()
    {
        using var temp = new TempInstall();

        var options = new UpdaterRunOptions
        {
            TrustedDigestSource = new FixedDigestSource(null),
            RestartMain = false,
            WaitForMainExit = false,
        };

        int exit = Program.RunFromArgs(new[] { temp.ZipPath }, temp.Dir, options);

        Assert.Equal(6, exit);
        Assert.True(temp.ErrorLogText.Contains("没有提供期望 SHA-256 摘要", StringComparison.Ordinal),
            "必须走进「缺少期望摘要」的拒绝分支：" + temp.ErrorLogText);
    }

    // ---------------------------------------------------------------- V6

    /// <summary>
    /// V6（**载荷用例**）：来源**自己抛异常** ⇒ 仍然拒绝（fail-closed）。
    /// </summary>
    [Fact]
    public void V6_可信摘要来源抛异常仍然拒绝()
    {
        using var temp = new TempInstall();

        var options = new UpdaterRunOptions
        {
            TrustedDigestSource = new ThrowingDigestSource(),
            RestartMain = false,
            WaitForMainExit = false,
        };

        int exit = Program.RunFromArgs(new[] { temp.ZipPath }, temp.Dir, options);

        Assert.Equal(6, exit);
        Assert.False(File.Exists(Path.Combine(temp.Dir, "payload.txt")));
    }

    // ---------------------------------------------------------------- V7

    /// <summary>
    /// V7（**载荷用例**）：签名元数据的**验签失败** ⇒ 来源返回 null ⇒ 调用方拒绝。
    /// </summary>
    [Fact]
    public void V7_验签失败时来源必须返回null()
    {
        var source = new SignedVersionMetadataDigestSource(
            () => "{\"version\":\"1.0.0\",\"sha256\":\"" + new string('a', 64) +
                  "\",\"releaseDate\":\"2026-01-01\",\"signature\":\"AAAA\"}");

        Assert.True(source.TryGetExpectedDigest("x.zip") == null,
            "验签失败必须返回 null（fail-closed），绝不能返回一个占位摘要。");
    }

    // ---------------------------------------------------------------- V8

    /// <summary>V8：元数据**取不到**（null / 空 / 抛异常）⇒ 来源返回 null。</summary>
    [Fact]
    public void V8_元数据取不到时来源必须返回null()
    {
        Assert.True(new SignedVersionMetadataDigestSource(null).TryGetExpectedDigest("x.zip") == null);
        Assert.True(new SignedVersionMetadataDigestSource(() => null).TryGetExpectedDigest("x.zip") == null);
        Assert.True(new SignedVersionMetadataDigestSource(() => "").TryGetExpectedDigest("x.zip") == null);
        Assert.True(new SignedVersionMetadataDigestSource(() => throw new InvalidOperationException("boom"))
            .TryGetExpectedDigest("x.zip") == null);
    }

    // ---------------------------------------------------------------- V9

    /// <summary>
    /// V9（**载荷用例**）：默认（生产）**不联网** —— 更新器工程里根本不存在任何网络 API。
    /// </summary>
    [Fact]
    public void V9_更新器工程不含任何网络API_所以默认不可能联网()
    {
        var offenders = new System.Collections.Generic.List<string>();

        foreach (Type type in typeof(Program).Assembly.GetTypes())
        {
            foreach (var member in type.GetMembers(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Instance |
                         System.Reflection.BindingFlags.Static |
                         System.Reflection.BindingFlags.DeclaredOnly))
            {
                Type? memberType = member switch
                {
                    System.Reflection.MethodInfo m => m.ReturnType,
                    System.Reflection.FieldInfo f => f.FieldType,
                    System.Reflection.PropertyInfo p => p.PropertyType,
                    _ => null,
                };

                if (memberType == null) continue;

                string ns = memberType.Namespace ?? "";
                if (ns == "System.Net" || ns.StartsWith("System.Net.", StringComparison.Ordinal) ||
                    ns.StartsWith("System.Net.Sockets", StringComparison.Ordinal))
                {
                    offenders.Add(type.FullName + "." + member.Name + " : " + memberType.FullName);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "更新器工程里出现了网络 API —— 默认（生产）路径就不可能「不联网」：" +
            string.Join("; ", offenders));
    }

    // ---------------------------------------------------------------- V10

    /// <summary>V10：摘要形态自检 —— 非法摘要一律不被当成"合法摘要"。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("ZZZZ456789012345678901234567890123456789012345678901234567890123")]
    public void V10_非法摘要形态一律不被接受(string? value)
    {
        Assert.False(Program.IsWellFormedDigest(value));
    }

    [Fact]
    public void V10b_合法摘要形态被接受()
    {
        Assert.True(Program.IsWellFormedDigest(new string('a', 64)));
        Assert.True(Program.IsWellFormedDigest(new string('A', 64)));
    }

    // ---------------------------------------------------------------- 辅助

    private sealed class FixedDigestSource : ITrustedDigestSource
    {
        private readonly string? _digest;
        internal FixedDigestSource(string? digest) { _digest = digest; }
        public string? TryGetExpectedDigest(string zipFile) => _digest;
    }

    private sealed class ThrowingDigestSource : ITrustedDigestSource
    {
        public string? TryGetExpectedDigest(string zipFile)
            => throw new InvalidOperationException("source unavailable");
    }
}