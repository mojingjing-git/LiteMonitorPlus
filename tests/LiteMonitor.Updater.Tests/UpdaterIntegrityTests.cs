using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LiteMonitor.Updater;
using Xunit;

namespace LiteMonitor.Updater.Tests;

/// <summary>
/// LM-016：主程序校验完安装包摘要后，只把**路径**交给以高权限运行的更新器，
/// 更新器再按路径把同一个文件重读一遍 —— 两者之间没有"字节必须保持不变"的约束。
/// </summary>
/// <remarks>
/// 这里守的不变式是：
/// <b>高权限更新器所使用的每一个字节，都必须是主程序已经验证过的那批字节。</b>
/// 防线落在高权限侧（更新器自己开流、自己算摘要、自己从这个流解压），
/// 因此不依赖主程序侧的时序。
/// </remarks>
public sealed class UpdaterIntegrityTests : IDisposable
{
    private const string MainExeName = "LiteMonitor++.exe";

    /// <summary>
    /// 测试用的运行参数：不重启主程序、不等待主程序退出。
    /// </summary>
    /// <remarks>
    /// ★ 绝不能让测试去等/去杀用户的 LiteMonitor++ 进程（更新器的正常流程会
    ///   在等待超时后强制结束主程序）。测试里这两步一律关掉。
    /// </remarks>
    private static readonly UpdaterRunOptions TestOptions = new()
    {
        RestartMain = false,
        WaitForMainExit = false
    };

    private readonly string _root;
    private readonly string _installDir;
    private readonly string _resourcesDir;
    private readonly string _zipPath;

    public UpdaterIntegrityTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Updater.Tests",
            Guid.NewGuid().ToString("N"));
        _installDir = Path.Combine(_root, "install");
        _resourcesDir = Path.Combine(_installDir, "resources");
        _zipPath = Path.Combine(_root, "update.zip");

        Directory.CreateDirectory(_resourcesDir);

        // 更新器靠这个文件定位安装目录（GetMainProgramDirectory）。
        File.WriteAllText(Path.Combine(_installDir, MainExeName), "not a real executable");
    }

    // ================================================================
    // 1. 核心 TOCTOU 用例：校验通过之后，同一路径上的文件被换掉
    // ================================================================

    [Fact]
    public void PackageReplacedAfterVerificationIsRejectedAndNothingIsExtracted()
    {
        CreateZip(("app.txt", "verified bytes"));
        string expectedDigest = Sha256OfFile(_zipPath);

        // 主程序校验通过之后，同一路径上的文件被换成内容不同的**另一份合法更新包**。
        // 如果更新器不自己校验，它会照常把这份内容装进安装目录。
        string attackerZip = Path.Combine(_root, "attacker.zip");
        CreateZipAt(attackerZip, ("app.txt", "attacker bytes"));
        File.Copy(attackerZip, _zipPath, overwrite: true);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);

        // 一个文件都没有被替换
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        Assert.Empty(Directory.GetFiles(_installDir, "*.txt", SearchOption.AllDirectories));

        // 没有临时目录残留、没有"进行中"标记
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.False(File.Exists(Path.Combine(_installDir, "update_success")));

        // 错误日志明确：期望值、实际值
        string log = ReadErrorLog();
        Assert.Contains("摘要与期望值不一致", log);
        Assert.Contains(expectedDigest.ToLowerInvariant(), log);
        Assert.Contains(Sha256OfFile(_zipPath).ToLowerInvariant(), log);
        Assert.DoesNotContain("password", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", log, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // 2. 期望摘要为空 / 缺失 / 格式非法 ⇒ 拒绝（不得当成"不校验"）
    // ================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-digest")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeZ")]
    public void MissingOrMalformedExpectedDigestIsRejected(string? expectedDigest)
    {
        CreateZip(("app.txt", "bytes"));

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();

        // 拒绝发生在**读取更新包之前**：契约本身坏了就没必要再去碰那个文件。
        string log = ReadErrorLog();
        Assert.Contains("摘要缺失或格式非法", log);
        Assert.Contains("未解压任何文件", log);
    }

    // ================================================================
    // 3. 期望摘要与实际不符 ⇒ 拒绝，且拒绝发生在解压之前
    // ================================================================

    [Fact]
    public void DigestMismatchIsRejectedBeforeAnyExtractionHappens()
    {
        CreateZip(("app.txt", "bytes"));
        string unrelatedDigest = Sha256OfBytes(Encoding.UTF8.GetBytes("a different package"));

        int exitCode = Program.RunUpdate(_zipPath, unrelatedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);

        // 拒绝发生在解压之前：既没有落地文件，也没有临时目录
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.False(File.Exists(Path.Combine(_installDir, "update_success")));

        string log = ReadErrorLog();
        Assert.Contains("摘要与期望值不一致", log);
        Assert.Contains("expected=" + unrelatedDigest.ToLowerInvariant(), log);
        Assert.Contains("actual=" + Sha256OfFile(_zipPath).ToLowerInvariant(), log);
    }

    // ================================================================
    // 4. 摘要匹配 ⇒ 正常更新成功（正面回归）
    // ================================================================

    [Fact]
    public void MatchingDigestCompletesTheUpdate()
    {
        CreateZip(("app.txt", "new app"), ("nested/data.txt", "new data"));
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_installDir, "app.txt")));
        Assert.Equal(
            "new data",
            File.ReadAllText(Path.Combine(_installDir, "nested", "data.txt")));

        // 成功标记写了、备份目录清理了、更新包删了
        Assert.True(File.Exists(Path.Combine(_installDir, "update_success")));
        Assert.Empty(Directory.GetDirectories(_installDir, "_update_backup_*"));
        AssertNoTemporaryDirectory();
        Assert.False(File.Exists(_zipPath));
    }

    [Fact]
    public void MatchingDigestIsAcceptedRegardlessOfHexCase()
    {
        CreateZip(("app.txt", "new app"));
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(
            _zipPath, expectedDigest.ToLowerInvariant(), _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_installDir, "app.txt")));
    }

    // ================================================================
    // 5. 旧式单参数调用 ⇒ 明确拒绝（有损兼容是刻意的）
    // ================================================================

    [Fact]
    public void LegacySingleArgumentInvocationIsRejectedWithDiagnostic()
    {
        CreateZip(("app.txt", "bytes"));

        int exitCode = Program.RunFromArgs(new[] { _zipPath }, _resourcesDir);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();

        string log = ReadErrorLog();
        Assert.Contains("没有提供期望 SHA-256", log);
        Assert.Contains("未解压任何文件", log);
    }

    [Fact]
    public void LegacySingleArgumentInvocationDoesNotDependOnThePackageBeingValid()
    {
        // 即使给的是一个根本不是 ZIP 的路径，也必须先因为"没有摘要"被拒，
        // 而不是先尝试解压。
        string notAZip = Path.Combine(_root, "not-a-zip.bin");
        File.WriteAllText(notAZip, "definitely not a zip");

        int exitCode = Program.RunFromArgs(new[] { notAZip }, _resourcesDir);

        Assert.Equal(Program.ExitIntegrityRejected, exitCode);
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
    }

    // ================================================================
    // 6. 异常 ZIP 展开量（zip bomb）
    // ================================================================

    [Fact]
    public void TooManyEntriesIsRejectedAndTemporaryDirectoryIsCleaned()
    {
        int entryCount = ZipExpansionLimits.MaxEntryCount + 1;
        CreateZipWithManyEntries(entryCount);
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitUpdateFailed, exitCode);
        Assert.Empty(Directory.GetFiles(_installDir, "file*.txt", SearchOption.AllDirectories));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.False(File.Exists(Path.Combine(_installDir, "update_success")));
        Assert.Contains("条目数超限", ReadErrorLog());
    }

    [Fact]
    public void OversizedExpansionIsRejectedAndTemporaryDirectoryIsCleaned()
    {
        // 生产上限：单条目 256 MiB / 整包 512 MiB。
        // 这里用"声明展开量超过整包上限"来触发，并额外用注入的小上限覆盖同一分支。
        CreateZipWithZeros("bomb.bin", 300L * 1024 * 1024);
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitUpdateFailed, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "bomb.bin")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.Contains("超过上限", ReadErrorLog());
    }

    [Fact]
    public void OversizedTotalExpansionIsRejected()
    {
        // 用注入的小上限走"整包展开总量"这条分支（行为与生产上限同一条代码路径）。
        CreateZip(("a.bin", new string('a', 4096)), ("b.bin", new string('b', 4096)));
        string expectedDigest = Sha256OfFile(_zipPath);

        var options = new UpdaterRunOptions
        {
            RestartMain = false,
            WaitForMainExit = false,
            MaxExpandedBytes = 1024
        };

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, options);

        Assert.Equal(Program.ExitUpdateFailed, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "a.bin")));
        Assert.False(File.Exists(Path.Combine(_installDir, "b.bin")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.Contains("总字节超过上限", ReadErrorLog());
    }

    [Fact]
    public void ProductionExpansionLimitsArePinned()
    {
        Assert.Equal(4096, ZipExpansionLimits.MaxEntryCount);
        Assert.Equal(256L * 1024 * 1024, ZipExpansionLimits.MaxSingleEntryBytes);
        Assert.Equal(512L * 1024 * 1024, ZipExpansionLimits.MaxExpandedBytes);
    }

    // ================================================================
    // 7. 目标目录越界
    // ================================================================

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    public void EntriesEscapingTheTargetDirectoryAreRejected(string entryName)
    {
        CreateZip((entryName, "escaped"));
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitUpdateFailed, exitCode);

        // 越界目标一个都不该被创建
        Assert.False(File.Exists(Path.Combine(_installDir, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.Contains("越界", ReadErrorLog());
    }

    [Fact]
    public void AbsolutePathEntriesAreRejected()
    {
        // 用临时目录里的绝对路径，避免万一防线失效时写到系统目录。
        string absoluteTarget = Path.Combine(_root, "absolute-escaped.txt");
        CreateZip((absoluteTarget, "escaped"));
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitUpdateFailed, exitCode);
        Assert.False(File.Exists(absoluteTarget));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.Contains("越界", ReadErrorLog());
    }

    // ================================================================
    // 8. 并发更新
    // ================================================================

    [Fact]
    public void ConcurrentUpdateIsRejectedWhileAnotherUpdateHoldsTheLock()
    {
        CreateZip(("app.txt", "new app"));
        string expectedDigest = Sha256OfFile(_zipPath);

        // 模拟"另一个更新进程"：它持有安装目录里的互斥锁。
        using var otherUpdater = new FileStream(
            Path.Combine(_installDir, Program.UpdateLockFile),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitConcurrentUpdate, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "app.txt")));
        AssertNoTemporaryDirectory();
        AssertNoInProgressMarker();
        Assert.Contains("另一个更新进程", ReadErrorLog());
    }

    [Fact]
    public void TwoConcurrentUpdatesNeverProduceMixedContent()
    {
        string zipA = Path.Combine(_root, "a.zip");
        string zipB = Path.Combine(_root, "b.zip");
        CreateZipAt(zipA, ("app.txt", "version A"));
        CreateZipAt(zipB, ("app.txt", "version B"));
        string digestA = Sha256OfFile(zipA);
        string digestB = Sha256OfFile(zipB);

        int[] results = new int[2];
        var first = new Thread(() => results[0] = Program.RunUpdate(zipA, digestA, _resourcesDir, TestOptions));
        var second = new Thread(() => results[1] = Program.RunUpdate(zipB, digestB, _resourcesDir, TestOptions));
        first.Start();
        second.Start();
        Assert.True(first.Join(TimeSpan.FromMinutes(2)));
        Assert.True(second.Join(TimeSpan.FromMinutes(2)));

        // 一个成功，另一个要么被并发保护拒绝，要么排在后面也成功
        Assert.All(results, code => Assert.True(
            code == Program.ExitSuccess || code == Program.ExitConcurrentUpdate,
            "unexpected exit code " + code));
        Assert.Contains(Program.ExitSuccess, results);

        // 目标文件必须是**某一个**完整版本，绝不能是两者的混合
        string content = File.ReadAllText(Path.Combine(_installDir, "app.txt"));
        Assert.True(
            content == "version A" || content == "version B",
            "mixed content: " + content);
        AssertNoTemporaryDirectory();
    }

    // ================================================================
    // 9. 失败可恢复 ⇒ 恢复原版本；恢复材料不被误删；不误删用户数据
    // ================================================================

    [Fact]
    public void FailedUpdateRestoresPreviousVersionAndKeepsRecoveryMaterial()
    {
        File.WriteAllText(Path.Combine(_installDir, "app.txt"), "old app");
        File.WriteAllText(Path.Combine(_installDir, "settings.json"), "user settings");
        Directory.CreateDirectory(Path.Combine(_installDir, "resources", "themes"));
        File.WriteAllText(
            Path.Combine(_installDir, "resources", "themes", "mine.json"), "user theme");

        // app.txt 会被成功替换；zzz.txt 的目标是一个目录 ⇒ 提交失败 ⇒ 触发回滚。
        // 用户数据同时出现在包里（模拟发布包带了同名文件），验证它们不会被覆盖。
        CreateZip(
            ("app.txt", "new app"),
            ("zzz.txt", "new zzz"),
            ("settings.json", "packaged settings"),
            ("resources/themes/mine.json", "packaged theme"));
        Directory.CreateDirectory(Path.Combine(_installDir, "zzz.txt"));
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitUpdateFailed, exitCode);

        // 原版本被恢复
        Assert.Equal("old app", File.ReadAllText(Path.Combine(_installDir, "app.txt")));

        // 用户数据既没被删，也没被覆盖
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(_installDir, "settings.json")));
        Assert.Equal(
            "user theme",
            File.ReadAllText(Path.Combine(_installDir, "resources", "themes", "mine.json")));

        // 不得把部分更新标记为成功
        Assert.False(File.Exists(Path.Combine(_installDir, "update_success")));

        // 恢复材料保留：备份目录 + 完整错误日志
        string[] backups = Directory.GetDirectories(_installDir, "_update_backup_*");
        Assert.Single(backups);
        Assert.True(File.Exists(Path.Combine(backups[0], "app.txt")));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(backups[0], "app.txt")));
        Assert.True(File.Exists(Path.Combine(_installDir, "update_error.log")));
        Assert.True(File.Exists(Path.Combine(backups[0], "update_error.log")));

        // 解压产物不是恢复材料，已清理
        AssertNoTemporaryDirectory();

        // 被保护而没有覆盖的路径有留痕
        Assert.Contains("[Preserve]", ReadErrorLog());
    }

    [Fact]
    public void UserDataIsNeverOverwrittenByASuccessfulUpdate()
    {
        File.WriteAllText(Path.Combine(_installDir, "settings.json"), "user settings");
        Directory.CreateDirectory(Path.Combine(_installDir, "resources", "themes"));
        File.WriteAllText(
            Path.Combine(_installDir, "resources", "themes", "mine.json"), "user theme");
        Directory.CreateDirectory(Path.Combine(_installDir, "resources", "plugins"));
        File.WriteAllText(
            Path.Combine(_installDir, "resources", "plugins", "mine.json"), "user plugin");

        CreateZip(
            ("app.txt", "new app"),
            ("settings.json", "packaged settings"),
            ("resources/themes/mine.json", "packaged theme"),
            ("resources/plugins/mine.json", "packaged plugin"));
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_installDir, "app.txt")));
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(_installDir, "settings.json")));
        Assert.Equal(
            "user theme",
            File.ReadAllText(Path.Combine(_installDir, "resources", "themes", "mine.json")));
        Assert.Equal(
            "user plugin",
            File.ReadAllText(Path.Combine(_installDir, "resources", "plugins", "mine.json")));
    }

    // ================================================================
    // 10. --selfcheck 路径行为不变
    // ================================================================

    [Fact]
    public void SelfCheckPathIsUnchanged()
    {
        int exitCode = Program.RunFromArgs(new[] { "--selfcheck" }, _resourcesDir);

        Assert.Equal(0, exitCode);

        // 自检不写任何状态文件，也不建任何目录
        Assert.False(File.Exists(Path.Combine(_installDir, "update_error.log")));
        Assert.False(File.Exists(Path.Combine(_installDir, "update_in_progress")));
        Assert.False(File.Exists(Path.Combine(_installDir, "update_success")));
        Assert.False(File.Exists(Path.Combine(_installDir, "update.lock")));
        AssertNoTemporaryDirectory();
    }

    [Fact]
    public void SelfCheckStillTakesPrecedenceOverTheDigestArgument()
    {
        int exitCode = Program.RunFromArgs(
            new[] { "--selfcheck", new string('a', 64) }, _resourcesDir);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(_installDir, "update_error.log")));
    }

    [Fact]
    public void NoArgumentsKeepsTheUsageExitCode()
    {
        Assert.Equal(Program.ExitUsage, Program.RunFromArgs(Array.Empty<string>(), _resourcesDir));
    }

    // ================================================================
    // 字节绑定的机制本身
    // ================================================================

    [Fact]
    public void PinnedPackageCannotBeReplacedOrDeletedWhileTheUpdaterHoldsIt()
    {
        CreateZip(("app.txt", "bytes"));
        byte[] before = File.ReadAllBytes(_zipPath);

        using FileStream pinned = Program.OpenPinnedPackage(_zipPath);

        // 钉住的句柄以 FileShare.Read 打开：别的进程仍可读，但不能写入/替换/删除。
        Assert.Throws<IOException>(() => File.WriteAllText(_zipPath, "replaced"));
        Assert.Throws<IOException>(() => File.Delete(_zipPath));

        Assert.Equal(before, File.ReadAllBytes(_zipPath));
    }

    [Fact]
    public void DigestIsComputedOverTheSameStreamThatIsExtracted()
    {
        // 摘要与解压用的是同一个句柄：更新器读到的字节就是它校验过的字节。
        CreateZip(("app.txt", "bytes"));
        string expectedDigest = Sha256OfFile(_zipPath);

        int exitCode = Program.RunUpdate(_zipPath, expectedDigest, _resourcesDir, TestOptions);

        Assert.Equal(Program.ExitSuccess, exitCode);
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(_installDir, "app.txt")));
    }

    // ================================================================
    // 辅助
    // ================================================================

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

    private string CreateZip(params (string Name, string Content)[] entries) =>
        CreateZipAt(_zipPath, entries);

    private static string CreateZipAt(
        string path,
        params (string Name, string Content)[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using Stream target = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            target.Write(bytes, 0, bytes.Length);
        }

        return path;
    }

    private string CreateZipWithManyEntries(int count)
    {
        using var stream = new FileStream(_zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        for (int i = 0; i < count; i++)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                $"file{i:D6}.txt", CompressionLevel.Fastest);
            using Stream target = entry.Open();
            target.WriteByte((byte)('a' + (i % 26)));
        }

        return _zipPath;
    }

    private string CreateZipWithZeros(string entryName, long byteCount)
    {
        using var stream = new FileStream(_zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using (Stream target = entry.Open())
        {
            byte[] chunk = new byte[1024 * 1024];
            long remaining = byteCount;
            while (remaining > 0)
            {
                int write = (int)Math.Min(chunk.Length, remaining);
                target.Write(chunk, 0, write);
                remaining -= write;
            }
        }

        return _zipPath;
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sha256OfBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

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
