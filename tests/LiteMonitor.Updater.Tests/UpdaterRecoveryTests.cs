using System.Reflection;
using System.Security.Cryptography;
using LiteMonitor.Updater;
using Xunit;

namespace LiteMonitor.Updater.Tests;

public sealed class UpdaterRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourceDir;
    private readonly string _destinationDir;
    private readonly string _backupDir;

    public UpdaterRecoveryTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor.Updater.Tests",
            Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_root, "source");
        _destinationDir = Path.Combine(_root, "destination");
        _backupDir = Path.Combine(_root, "backup");

        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destinationDir);
        Directory.CreateDirectory(_backupDir);
    }

    [Fact]
    public void StageVerificationFailureLeavesExistingTargetUntouched()
    {
        WriteSource("app.dll", "new bytes");
        WriteDestination("app.dll", "old bytes");

        var operations = new FaultInjectingFileOperations
        {
            ComputeSha256 = path =>
                path.Contains(".lmu-stage-", StringComparison.Ordinal)
                    ? throw new IOException("injected staging verification failure")
                    : Sha256Of(path)
        };

        var result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.FailedBeforeCommit, result.Outcome);
        Assert.Equal("old bytes", File.ReadAllText(Path.Combine(_destinationDir, "app.dll")));
        Assert.False(Assert.Single(result.Records).CommitStarted);
    }

    [Fact]
    public void StageWriteFailureLeavesExistingTargetUntouched()
    {
        WriteSource("app.dll", "new bytes");
        WriteDestination("app.dll", "old bytes");

        var operations = new FaultInjectingFileOperations
        {
            CopyFile = (source, destination, overwrite) =>
            {
                if (destination.Contains(".lmu-stage-", StringComparison.Ordinal))
                {
                    throw new IOException("injected staging write failure");
                }

                File.Copy(source, destination, overwrite);
            }
        };

        var result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.FailedBeforeCommit, result.Outcome);
        Assert.Equal("old bytes", File.ReadAllText(Path.Combine(_destinationDir, "app.dll")));
        Assert.False(Assert.Single(result.Records).CommitStarted);
    }

    [Fact]
    public void CurrentFileCommitFailureRestoresCurrentAndPreviouslyCommittedFiles()
    {
        WriteSource("a-existing.txt", "new a");
        WriteSource("b-current.txt", "new b");
        WriteDestination("a-existing.txt", "old a");
        WriteDestination("b-current.txt", "old b");

        var operations = new FaultInjectingFileOperations
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (source.Contains(".lmu-stage-", StringComparison.Ordinal) &&
                    Path.GetFileName(destination).Equals("b-current.txt", StringComparison.Ordinal))
                {
                    throw new IOException("injected current-file commit failure");
                }

                File.Replace(source, destination, backup);
            }
        };

        var result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.FailedRecovered, result.Outcome);
        Assert.Equal("old a", File.ReadAllText(Path.Combine(_destinationDir, "a-existing.txt")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(_destinationDir, "b-current.txt")));
        Assert.All(result.Records, record => Assert.Equal(UpdateRecoveryState.Restored, record.RecoveryState));
    }

    [Fact]
    public void TransientCommitFailureIsRetriedBeforeSucceeding()
    {
        WriteSource("existing.txt", "new existing");
        WriteDestination("existing.txt", "old existing");

        int attempts = 0;
        var operations = new FaultInjectingFileOperations
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (source.Contains(".lmu-stage-", StringComparison.Ordinal) &&
                    ++attempts < 3)
                {
                    throw new IOException("injected transient lock");
                }

                File.Replace(source, destination, backup);
            }
        };

        var result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, attempts);
        Assert.Equal("new existing", File.ReadAllText(Path.Combine(_destinationDir, "existing.txt")));
    }

    [Fact]
    public void PostCommitVerificationFailureRestoresCurrentAndPreviouslyCommittedFiles()
    {
        WriteSource("a-existing.txt", "new a");
        WriteSource("b-current.txt", "new b");
        WriteDestination("a-existing.txt", "old a");
        WriteDestination("b-current.txt", "old b");

        bool verificationCorrupted = false;
        var operations = new FaultInjectingFileOperations
        {
            AfterReplace = destination =>
            {
                if (!verificationCorrupted &&
                    Path.GetFileName(destination).Equals("b-current.txt", StringComparison.Ordinal))
                {
                    verificationCorrupted = true;
                    File.WriteAllText(destination, "bad");
                }
            }
        };

        var result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.FailedRecovered, result.Outcome);
        Assert.Equal("old a", File.ReadAllText(Path.Combine(_destinationDir, "a-existing.txt")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(_destinationDir, "b-current.txt")));
    }

    [Fact]
    public void FailureAfterAddingNewFileRemovesThatFile()
    {
        WriteSource("a-new.txt", "new file");
        WriteSource("z-current.txt", "new current");
        WriteDestination("z-current.txt", "old current");

        var operations = new FaultInjectingFileOperations
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (source.Contains(".lmu-stage-", StringComparison.Ordinal) &&
                    Path.GetFileName(destination).Equals("z-current.txt", StringComparison.Ordinal))
                {
                    throw new IOException("injected failure after adding new file");
                }

                File.Replace(source, destination, backup);
            }
        };

        var result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.FailedRecovered, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_destinationDir, "a-new.txt")));
        Assert.Equal("old current", File.ReadAllText(Path.Combine(_destinationDir, "z-current.txt")));
    }

    [Fact]
    public void RollbackFailureIsReportedAndBackupIsRetained()
    {
        WriteSource("a-new.txt", "new file");
        WriteSource("z-current.txt", "new current");
        WriteDestination("z-current.txt", "old current");

        var operations = new FaultInjectingFileOperations
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (source.Contains(".lmu-stage-", StringComparison.Ordinal) &&
                    Path.GetFileName(destination).Equals("z-current.txt", StringComparison.Ordinal))
                {
                    throw new IOException("injected failure after adding new file");
                }

                File.Replace(source, destination, backup);
            },
            DeleteFile = path =>
            {
                if (Path.GetFileName(path).Equals("a-new.txt", StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("injected rollback delete failure");
                }

                File.Delete(path);
            }
        };

        var result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.FailedRecoveryIncomplete, result.Outcome);
        Assert.NotNull(result.Rollback);
        Assert.Equal(1, result.Rollback!.FailedCount);
        Assert.True(File.Exists(Path.Combine(_destinationDir, "a-new.txt")));
        Assert.True(File.Exists(Path.Combine(_backupDir, "z-current.txt")));
    }

    [Fact]
    public void SuccessfulApplyCommitsExistingAndNewFiles()
    {
        WriteSource("existing.txt", "new existing");
        WriteSource("nested\\new.txt", "new file");
        WriteDestination("existing.txt", "old existing");

        var result = CreateTransaction().ApplyDirectory(_sourceDir, _ => false);

        Assert.Equal(UpdateApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("new existing", File.ReadAllText(Path.Combine(_destinationDir, "existing.txt")));
        Assert.Equal(
            "new file",
            File.ReadAllText(Path.Combine(_destinationDir, "nested", "new.txt")));
    }

    [Fact]
    public void ProductionLoggerTimestampsEveryResultLine()
    {
        MethodInfo logError = typeof(Program).GetMethod(
            "LogError",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(Program).FullName, "LogError");

        logError.Invoke(
            null,
            new object[]
            {
                _root,
                "[Failure] injected failure\r\n[Result] status=recovered backup=\"C:\\tmp\""
            });

        string[] lines = File.ReadAllLines(Path.Combine(_root, "update_error.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains(" [Failure] injected failure", lines[0]);
        Assert.Contains(" [Result] status=recovered", lines[1]);
        Assert.True(lines.All(line => line.IndexOf(' ') > 0));
    }

    [Fact]
    public void IncompleteRecoveryDiagnosticsRemainAfterNotificationLogIsDeleted()
    {
        UpdateApplyResult result = ApplyWithIncompleteRecovery();
        WriteProductionLog("[Verify] earlier diagnostic\r\n[Verify] another diagnostic");

        Program.WriteFailureDiagnostics(
            _destinationDir, _backupDir, result.FailureReason, result);

        string notificationPath = Path.Combine(_destinationDir, "update_error.log");
        byte[] completeLog = File.ReadAllBytes(notificationPath);
        string diagnosticText = File.ReadAllText(notificationPath);
        Assert.Contains("[Verify] earlier diagnostic", diagnosticText);
        Assert.Contains("[Verify] another diagnostic", diagnosticText);
        Assert.Contains("[Failure] " + result.FailureReason, diagnosticText);
        Assert.Contains("[Rollback] 恢复失败：app.dll -> injected restore failure", diagnosticText);
        Assert.Contains("[Result] status=recovery_incomplete", diagnosticText);
        Assert.Contains("failed=1", diagnosticText);
        Assert.Contains("[Summary]", diagnosticText);

        File.Delete(notificationPath);

        Assert.False(File.Exists(notificationPath));
        Assert.Equal(completeLog, File.ReadAllBytes(Path.Combine(_backupDir, "update_error.log")));
        Assert.Equal("old bytes", File.ReadAllText(Path.Combine(_backupDir, "app.dll")));
        Assert.Equal(UpdateApplyOutcome.FailedRecoveryIncomplete, result.Outcome);
    }

    [Fact]
    public void EarlyFailureCreatesRecoveryDirectoryWithoutUsingPathsFromLog()
    {
        string generatedBackupDir = Path.Combine(_root, "generated-backup");
        string untrustedBackupDir = Path.Combine(_root, "untrusted-backup");
        WriteProductionLog($"[Result] status=recovered backup=\"{untrustedBackupDir}\"");

        Program.WriteFailureDiagnostics(
            _destinationDir, generatedBackupDir, "injected extraction failure", null);

        Assert.False(Directory.Exists(untrustedBackupDir));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(_destinationDir, "update_error.log")),
            File.ReadAllBytes(Path.Combine(generatedBackupDir, "update_error.log")));
        Assert.Contains(
            "[Result] status=unknown",
            File.ReadAllText(Path.Combine(generatedBackupDir, "update_error.log")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DiagnosticPreservationFailureIsLoggedWithoutClaimingRecoverySucceeded(
        bool blockDirectoryCreation)
    {
        UpdateApplyResult result = ApplyWithIncompleteRecovery();
        string generatedBackupDir = _backupDir;
        if (blockDirectoryCreation)
        {
            generatedBackupDir = Path.Combine(_root, "blocked-backup");
            File.WriteAllText(generatedBackupDir, "blocks backup directory creation");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(generatedBackupDir, "update_error.log"));
        }

        Program.WriteFailureDiagnostics(
            _destinationDir, generatedBackupDir, result.FailureReason, result);

        string notificationLog = File.ReadAllText(Path.Combine(_destinationDir, "update_error.log"));
        Assert.Contains("[Diagnostics] 完整更新日志保留失败", notificationLog);
        Assert.Contains(generatedBackupDir, notificationLog);
        Assert.Contains("[Rollback] 恢复失败：app.dll -> injected restore failure", notificationLog);
        Assert.Contains("[Result] status=recovery_incomplete", notificationLog);
        Assert.DoesNotContain("status=recovered", notificationLog);
        Assert.Equal(UpdateApplyOutcome.FailedRecoveryIncomplete, result.Outcome);
        Assert.False(File.Exists(Path.Combine(generatedBackupDir, "update_error.log")));
        Assert.False(File.Exists(Path.Combine(_destinationDir, "update_success")));
        Assert.Equal("old bytes", File.ReadAllText(Path.Combine(_backupDir, "app.dll")));
    }

    private UpdateApplyResult ApplyWithIncompleteRecovery()
    {
        WriteSource("app.dll", "new bytes");
        WriteDestination("app.dll", "old bytes");

        var operations = new FaultInjectingFileOperations
        {
            ReplaceFile = (source, destination, backup) =>
            {
                if (source.Contains(".lmu-rollback-", StringComparison.Ordinal))
                {
                    throw new IOException("injected restore failure");
                }

                File.Replace(source, destination, backup);
            },
            AfterReplace = destination => File.WriteAllText(destination, "bad")
        };

        UpdateApplyResult result = CreateTransaction(operations).ApplyDirectory(_sourceDir, _ => false);
        Assert.Equal(UpdateApplyOutcome.FailedRecoveryIncomplete, result.Outcome);
        return result;
    }

    private void WriteProductionLog(string message)
    {
        MethodInfo logError = typeof(Program).GetMethod(
            "LogError", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(Program).FullName, "LogError");
        logError.Invoke(null, new object[] { _destinationDir, message });
    }

    private UpdateFileTransaction CreateTransaction(
        IUpdateFileOperations? operations = null) =>
        new(
            _destinationDir,
            _backupDir,
            operations ?? new SystemUpdateFileOperations(),
            commitRetryDelay: TimeSpan.Zero);

    private void WriteSource(string relativePath, string contents)
    {
        string path = Path.Combine(_sourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private void WriteDestination(string relativePath, string contents)
    {
        string path = Path.Combine(_destinationDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string Sha256Of(string path)
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

    private sealed class FaultInjectingFileOperations : IUpdateFileOperations
    {
        public Action<string>? AfterReplace { get; init; }
        public Action<string, string, bool>? CopyFile { get; init; }
        public Action<string>? DeleteFile { get; init; }
        public Action<string, string, string?>? ReplaceFile { get; init; }
        public Func<string, string>? ComputeSha256 { get; init; }

        void IUpdateFileOperations.CopyFile(string source, string destination, bool overwrite)
        {
            if (CopyFile is not null)
            {
                CopyFile(source, destination, overwrite);
            }
            else
            {
                File.Copy(source, destination, overwrite);
            }
        }

        void IUpdateFileOperations.CreateDirectory(string path) =>
            Directory.CreateDirectory(path);

        void IUpdateFileOperations.DeleteFile(string path) =>
            (DeleteFile ?? File.Delete)(path);

        bool IUpdateFileOperations.FileExists(string path) =>
            File.Exists(path);

        long IUpdateFileOperations.GetFileLength(string path) =>
            new FileInfo(path).Length;

        void IUpdateFileOperations.MoveFile(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);

        void IUpdateFileOperations.ReplaceFile(
            string source,
            string destination,
            string? backup)
        {
            if (ReplaceFile is not null)
            {
                ReplaceFile(source, destination, backup);
            }
            else
            {
                File.Replace(source, destination, backup);
            }

            AfterReplace?.Invoke(destination);
        }

        string IUpdateFileOperations.ComputeSha256(string path) =>
            (ComputeSha256 ?? Sha256Of)(path);
    }
}
