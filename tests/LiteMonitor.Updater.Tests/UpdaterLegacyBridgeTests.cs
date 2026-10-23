using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteMonitor.Updater;
using Xunit;

namespace LiteMonitor.Updater.Tests;

/// <summary>
/// R05：旧客户端 → 新更新器的升级通道桥接。
/// </summary>
/// <remarks>
/// 背景：旧版主程序用**单参数**调用更新器（只给 ZIP 路径），新版更新器按 LM-016 契约
/// 要求必须带上期望 SHA-256，否则拒绝执行。旧客户端随即 <c>Application.Exit()</c>，
/// 于是用户看到的是"应用自己关掉了，什么提示都没有"，下次再试还是同样失败。
///
/// 这里盯住三件不能被"桥接"弄坏的事：
///   1. **单参数调用仍然被拒绝**，退出码与现状完全一致（6 / ExitIntegrityRejected）；
///   2. 拒绝时**必须留下机器可读的标记**，且标记里写明原因与"请手动下载新版本"；
///   3. 装配"可信摘要来源"**不等于放宽校验**：来源给出的摘要必须照样过比对，
///      比对不过 / 来源不可用 / 来源炸了 ⇒ 一律仍然拒绝（fail-closed）。
///
/// 测试纪律：自造密钥对、自签自验，不依赖仓库里的真实私钥，也不触网。
/// </remarks>
public sealed class UpdaterLegacyBridgeTests : IDisposable
{
    private const string MainExeName = "LiteMonitor++.exe";
    private const string MarkerFileName = "update_legacy_bridge.json";

    /// <summary>
    /// 内置签名公钥的指纹（base64 文本的 SHA-256，大写十六进制）。
    /// </summary>
    /// <remarks>
    /// ★ 与 <c>src/System/UpdateChecker.cs</c> 里主程序那份拷贝用**同一个**期望值，
    ///   两边各自断言一次 ⇒ 两份拷贝一旦漂移，至少有一边会红。
    /// </remarks>
    private const string ExpectedPublicKeyFingerprint =
        "25E6659D954A92A9FCCF1E2622F30F68A7FA9C84390B419B33F3EB0F11F97572";

    private static readonly UpdaterRunOptions TestOptions = new()
    {
        RestartMain = false,
        WaitForMainExit = false
    };

    private readonly string _root;
    private readonly string _installDir;
    private readonly string _resourcesDir;
    private readonly string _zipPath;

    public UpdaterLegacyBridgeTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Updater.Tests.LegacyBridge",
            Guid.NewGuid().ToString("N"));
        _installDir = Path.Combine(_root, "install");
        _resourcesDir = Path.Combine(_installDir, "resources");
        _zipPath = Path.Combine(_root, "update.zip");

        Directory.CreateDirectory(_resourcesDir);

        // 更新器靠这个文件定位安装目录（GetMainProgramDirectory）。
        File.WriteAllText(Path.Combine(_installDir, MainExeName), "not a real executable");
    }

    // ================================================================
    // 1. 桥接不得放宽：单参数调用仍然被拒绝，退出码与现状一致
    // ================================================================

    [Fact]
    public void 单参数调用仍被拒绝且退出码与现状一致()
    {
        CreateZip(("app.txt", "bytes"));

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.Equal(6, Program.ExitIntegrityRejected);

        // 一个文件都没有被替换
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.False(File.Exists(Path.Combine(_installDir, "update_success")));
    }

    [Fact]
    public void 默认没有装配摘要来源时行为与加桥接之前完全一致()
    {
        CreateZip(("app.txt", "bytes"));

        // 显式传一份"没有来源"的选项：来源为 null ⇒ 仍然拒绝
        int exitCode = Program.RunFromArgs(
            new[] { _zipPath }, _resourcesDir,
            new UpdaterRunOptions { RestartMain = false, WaitForMainExit = false });

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
    }

    // ================================================================
    // 2. 失败对用户可见：拒绝时写出机器可读标记
    // ================================================================

    [Fact]
    public void 拒绝时写出机器可读标记且含原因与手动升级指引()
    {
        CreateZip(("app.txt", "bytes"));

        Program.RunFromArgs(new[] { _zipPath }, _resourcesDir);

        string markerPath = Path.Combine(_installDir, MarkerFileName);
        Assert.True(File.Exists(markerPath), "拒绝时必须在主程序目录旁写出标记");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(markerPath));
        JsonElement root = doc.RootElement;

        Assert.Equal("liteMonitor.legacyBridge/v1", root.GetProperty("schema").GetString());
        Assert.Equal("missing_digest_rejected", root.GetProperty("code").GetString());

        string reason = root.GetProperty("reason").GetString()!;
        string guidance = root.GetProperty("guidance").GetString()!;
        Assert.Contains("摘要", reason);
        Assert.Contains("手动下载新版本", guidance);
        Assert.Contains(
            "https://github.com/mojingjing-git/LiteMonitorPlus/releases",
            root.GetProperty("manualDownloadUrl").GetString());
    }

    [Fact]
    public void 标记里不写入任何私钥材料()
    {
        CreateZip(("app.txt", "bytes"));

        Program.RunFromArgs(new[] { _zipPath }, _resourcesDir);

        string raw = File.ReadAllText(Path.Combine(_installDir, MarkerFileName));
        Assert.DoesNotContain("PRIVATE KEY", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 标记同时被写进日志便于排障()
    {
        CreateZip(("app.txt", "bytes"));

        Program.RunFromArgs(new[] { _zipPath }, _resourcesDir);

        string log = ReadErrorLog();
        Assert.Contains("没有提供期望 SHA-256", log);
        Assert.Contains("未解压任何文件", log);
        Assert.Contains("[Bridge]", log);
    }

    // ================================================================
    // 3. 装配可信摘要来源后：单参数路径可以走通（自造密钥对、自签自验）
    // ================================================================

    [Fact]
    public void 注入可信摘要来源后单参数路径按该来源的摘要完成更新()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateZip(("app.txt", "new app"));

        // 发布端语义：version.json 里声明的是**发布包的真实摘要**（大写十六进制）
        string realDigest = Sha256OfFile(_zipPath);
        string metadata = BuildSignedVersionJson(key, "9.9.9", realDigest, "2026-10-23");

        var options = new UpdaterRunOptions
        {
            RestartMain = false,
            WaitForMainExit = false,
            TrustedDigestSource = new SignedVersionMetadataDigestSource(
                () => metadata, ExportPublicKey(key))
        };

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir, options);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_installDir, "app.txt")));

        // 通道既然通了，就不该留下"升级通道不兼容"的标记
        Assert.False(File.Exists(Path.Combine(_installDir, MarkerFileName)));
    }

    // ================================================================
    // 4. 装配来源不等于放宽校验
    // ================================================================

    [Fact]
    public void 来源给出的摘要与实际不符时仍然拒绝()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateZip(("app.txt", "new app"));

        // 签名是真的，但声明的摘要不是这个包的摘要
        string wrongDigest = Sha256OfBytes(Encoding.UTF8.GetBytes("a different package"));
        string metadata = BuildSignedVersionJson(key, "9.9.9", wrongDigest, "2026-10-23");

        var options = new UpdaterRunOptions
        {
            RestartMain = false,
            WaitForMainExit = false,
            TrustedDigestSource = new SignedVersionMetadataDigestSource(
                () => metadata, ExportPublicKey(key))
        };

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir, options);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();

        // ★ 这里失败的是"包没通过校验"，不是"通道不兼容"：
        //   摘要来源本身工作正常（它给出了摘要），所以不该写"升级通道不兼容"标记 ——
        //   那会把"包坏了"误导成"通道坏了"。这种失败由 update_error.log 承载。
        Assert.False(File.Exists(Path.Combine(_installDir, MarkerFileName)));

        string log = ReadErrorLog();
        Assert.Contains("摘要与期望值不一致", log);
        Assert.Contains(wrongDigest.ToLowerInvariant(), log);
    }

    [Fact]
    public void 来源用非受信私钥签名时仍然拒绝()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        CreateZip(("app.txt", "new app"));
        string realDigest = Sha256OfFile(_zipPath);

        // 攻击者用自己的私钥签了一份"看起来没问题"的元数据
        string metadata = BuildSignedVersionJson(attacker, "9.9.9", realDigest, "2026-10-23");

        var options = new UpdaterRunOptions
        {
            RestartMain = false,
            WaitForMainExit = false,
            TrustedDigestSource = new SignedVersionMetadataDigestSource(
                () => metadata, ExportPublicKey(trusted))
        };

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir, options);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        Assert.True(File.Exists(Path.Combine(_installDir, MarkerFileName)));
    }

    [Fact]
    public void 来源取不到元数据时仍然拒绝()
    {
        CreateZip(("app.txt", "bytes"));

        var options = new UpdaterRunOptions
        {
            RestartMain = false,
            WaitForMainExit = false,
            TrustedDigestSource = new SignedVersionMetadataDigestSource(() => null)
        };

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir, options);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
    }

    [Fact]
    public void 来源自己抛异常时仍然拒绝()
    {
        CreateZip(("app.txt", "bytes"));

        var options = new UpdaterRunOptions
        {
            RestartMain = false,
            WaitForMainExit = false,
            TrustedDigestSource = new ThrowingDigestSource()
        };

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir, options);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
    }

    [Fact]
    public void 来源给出畸形摘要时仍然拒绝()
    {
        CreateZip(("app.txt", "bytes"));

        var options = new UpdaterRunOptions
        {
            RestartMain = false,
            WaitForMainExit = false,
            TrustedDigestSource = new FixedDigestSource("not-a-digest")
        };

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir, options);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
    }

    // ================================================================
    // 5. 跨进程契约：更新器写的标记，主程序读得出来
    // ================================================================

    [Fact]
    public void 更新器写出的标记能被主程序读出来并给出提示()
    {
        CreateZip(("app.txt", "bytes"));

        Program.RunFromArgs(new[] { _zipPath }, _resourcesDir);

        LegacyBridgeNotice? notice = UpdateChecker.TryConsumeLegacyBridgeNotice(_installDir);
        Assert.NotNull(notice);
        Assert.Contains("摘要", notice!.Reason);

        string message = LegacyBridgeNotice.BuildMessage(true, notice);
        Assert.Contains("自动升级通道与当前版本不兼容", message);
        Assert.Contains("手动下载新版本", message);
        Assert.Contains("https://github.com/mojingjing-git/LiteMonitorPlus/releases", message);

        // 消费后不再重复提示
        Assert.Null(UpdateChecker.TryConsumeLegacyBridgeNotice(_installDir));
    }

    // ================================================================
    // 6. 信任锚不漂移
    // ================================================================

    [Fact]
    public void 签名载荷格式与主程序一致()
    {
        Assert.Equal(
            UpdateChecker.BuildUpdateSignaturePayload("1.0.1", "AB", "2026-10-23"),
            SignedVersionMetadataDigestSource.BuildSignaturePayload("1.0.1", "AB", "2026-10-23"));
    }

    [Fact]
    public void 更新器内置签名公钥指纹与文档一致()
    {
        Assert.Equal(
            ExpectedPublicKeyFingerprint,
            FingerprintOfBase64Text(SignedVersionMetadataDigestSource.UpdateSigningPublicKeySpkiBase64));
    }

    [Fact]
    public void 更新器内置签名公钥必须是可用的ECDSA主程序公钥()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(
            Convert.FromBase64String(
                SignedVersionMetadataDigestSource.UpdateSigningPublicKeySpkiBase64),
            out int bytesRead);

        Assert.True(bytesRead > 0);
        Assert.Equal(32, ecdsa.ExportParameters(false).Q.X.Length);
        Assert.Equal(32, ecdsa.ExportParameters(false).Q.Y.Length);
    }

    // ================================================================
    // 辅助
    // ================================================================

    private sealed class ThrowingDigestSource : ITrustedDigestSource
    {
        public string? TryGetExpectedDigest(string zipFile)
            => throw new InvalidOperationException("来源炸了");
    }

    private sealed class FixedDigestSource : ITrustedDigestSource
    {
        private readonly string _digest;

        public FixedDigestSource(string digest) => _digest = digest;

        public string? TryGetExpectedDigest(string zipFile) => _digest;
    }

    private void AssertNoTemporaryDirectory()
    {
        Assert.Empty(Directory.GetDirectories(_installDir, "_update_tmp_*"));
    }

    private void AssertNoInProgressMarker()
    {
        Assert.False(File.Exists(Path.Combine(_installDir, "update_in_progress")));
    }

    private string ReadErrorLog()
    {
        string path = Path.Combine(_installDir, "update_error.log");
        Assert.True(File.Exists(path), "expected an error log at " + path);
        return File.ReadAllText(path);
    }

    private void CreateZip(params (string Name, string Content)[] entries)
    {
        using var stream = new FileStream(_zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using Stream target = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            target.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// 造一份"已签名"的 version.json（与发布脚本 sign-release.ps1 的字段与格式一致）。
    /// </summary>
    private static string BuildSignedVersionJson(
        ECDsa key, string version, string sha256, string releaseDate)
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            SignedVersionMetadataDigestSource.BuildSignaturePayload(version, sha256, releaseDate));

        byte[] signature = key.SignData(
            payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return "{" +
            "\"version\": \"" + version + "\", " +
            "\"releaseDate\": \"" + releaseDate + "\", " +
            "\"sha256\": \"" + sha256 + "\", " +
            "\"signature\": \"" + Convert.ToBase64String(signature) + "\"" +
            "}";
    }

    private static string ExportPublicKey(ECDsa key)
        => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static string FingerprintOfBase64Text(string base64Text)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(base64Text)));

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sha256OfBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
