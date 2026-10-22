using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LiteMonitor;
using Xunit;

namespace LiteMonitor.Updater.Tests;

/// <summary>
/// 主程序侧的字节绑定（LM-016）。
/// </summary>
/// <remarks>
/// 交接链：已验签的 version.json 给出期望摘要 → 主程序算出实际摘要并比对 →
/// 主程序把**期望摘要**交给更新器 → 更新器用**自己打开的**文件流重算摘要并比对。
/// 这一组用例守的是链条的前两环：主程序算摘要时用的字节，必须和它交出去的文件
/// 是同一批字节（句柄钉住），而且"没摘要"永远不会被解释成"不需要校验"。
/// </remarks>
public sealed class UpdatePackageBinderTests : IDisposable
{
    private readonly string _root;
    private readonly string _packagePath;

    public UpdatePackageBinderTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Updater.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _packagePath = Path.Combine(_root, "update.zip");
        File.WriteAllBytes(_packagePath, Encoding.UTF8.GetBytes("package bytes"));
    }

    [Fact]
    public void PinnedPackageCannotBeReplacedOrDeleted()
    {
        byte[] before = File.ReadAllBytes(_packagePath);

        using FileStream pinned = UpdatePackageBinder.OpenPinned(_packagePath);

        // 钉住之后：别的进程仍可读，但不能写入 / 替换 / 删除。
        Assert.Throws<IOException>(() => File.WriteAllText(_packagePath, "replaced"));
        Assert.Throws<IOException>(() => File.Delete(_packagePath));

        Assert.Equal(before, File.ReadAllBytes(_packagePath));
    }

    [Fact]
    public void DigestIsComputedOverThePinnedHandleAndMatchesTheFile()
    {
        using FileStream pinned = UpdatePackageBinder.OpenPinned(_packagePath);

        string digest = UpdatePackageBinder.ComputeSha256Hex(pinned);

        Assert.Equal(64, digest.Length);
        Assert.Equal(Sha256OfFile(_packagePath), digest, ignoreCase: true);
        Assert.Equal(digest, digest.ToUpperInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-digest")]
    [InlineData("0123456789abcdef")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void MalformedExpectedDigestIsNeverTreatedAsValid(string? value)
    {
        Assert.False(UpdatePackageBinder.IsWellFormedDigest(value));
    }

    [Fact]
    public void WellFormedExpectedDigestIsAcceptedInEitherCase()
    {
        string digest = Sha256OfFile(_packagePath);

        Assert.True(UpdatePackageBinder.IsWellFormedDigest(digest));
        Assert.True(UpdatePackageBinder.IsWellFormedDigest(digest.ToLowerInvariant()));
        Assert.True(UpdatePackageBinder.IsWellFormedDigest("  " + digest + "  "));
    }

    [Fact]
    public void DigestComparisonIsCaseInsensitive()
    {
        string digest = Sha256OfFile(_packagePath);

        Assert.True(UpdatePackageBinder.Matches(digest, digest.ToLowerInvariant()));
        Assert.True(UpdatePackageBinder.Matches(digest.ToLowerInvariant(), digest));
        Assert.False(UpdatePackageBinder.Matches(digest, new string('0', 64)));
    }

    [Fact]
    public void UpdaterArgumentsCarryBothThePackagePathAndTheVerifiedDigest()
    {
        string digest = Sha256OfFile(_packagePath);

        string arguments = UpdatePackageBinder.BuildUpdaterArguments(_packagePath, digest);

        // 两个参数：ZIP 路径 + 期望摘要。摘要必须在里面 ——
        // 只传路径的旧交接方式正是 LM-016 要消除的缺口。
        Assert.Equal("\"" + _packagePath + "\" \"" + digest.ToLowerInvariant() + "\"", arguments);
        Assert.Contains(digest.ToLowerInvariant(), arguments);
        Assert.Equal(2, arguments.Split(' ').Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-digest")]
    [InlineData("0123456789abcdef")]
    public void UpdaterArgumentsRefuseToBeBuiltWithoutAUsableDigest(string digest)
    {
        Assert.Throws<ArgumentException>(
            () => UpdatePackageBinder.BuildUpdaterArguments(_packagePath, digest));
    }

    [Fact]
    public void UpdateDialogStartsTheUpdaterThroughTheDigestAwareArgumentBuilder()
    {
        // 接线守卫：StartUpdater 必须走 BuildUpdaterArguments，
        // 一旦有人把交接改回"只传路径"，这里就会失败。
        string source = ReadRepoFile(Path.Combine("src", "UI", "UpdateDialog.cs"));

        Assert.Contains("UpdatePackageBinder.BuildUpdaterArguments(", source);
        Assert.Contains("UpdatePackageBinder.OpenPinned(", source);
        Assert.DoesNotContain("Arguments = $\"\\\"{_context.SavePath}\\\"\"", source);
    }

    private static string ReadRepoFile(string relativePath)
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LiteMonitor.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        string path = Path.Combine(directory!.FullName, relativePath);
        Assert.True(File.Exists(path), "expected repo file " + path);
        return File.ReadAllText(path);
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

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
