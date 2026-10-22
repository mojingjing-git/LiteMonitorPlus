using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace LiteMonitor.Updater
{
    internal enum UpdateApplyOutcome
    {
        Succeeded,
        FailedBeforeCommit,
        FailedRecovered,
        FailedRecoveryIncomplete
    }

    internal enum UpdateRecoveryState
    {
        NotCommitted,
        Restored,
        Removed,
        Failed
    }

    internal sealed class UpdateFileRecord
    {
        public UpdateFileRecord(
            string relativePath,
            string destinationPath,
            bool originallyExisted,
            string? backupPath)
        {
            RelativePath = relativePath;
            DestinationPath = destinationPath;
            OriginallyExisted = originallyExisted;
            BackupPath = backupPath;
        }

        public string RelativePath { get; }
        public string DestinationPath { get; }
        public bool OriginallyExisted { get; }
        public string? BackupPath { get; }
        public bool BackupCreated { get; internal set; }
        public bool CommitStarted { get; internal set; }
        public bool CommittedAndVerified { get; internal set; }
        public UpdateRecoveryState RecoveryState { get; internal set; }
        public string? RecoveryError { get; internal set; }
    }

    internal sealed class UpdateRollbackResult
    {
        public UpdateRollbackResult(IReadOnlyList<UpdateFileRecord> records)
        {
            Records = records;
            RestoredCount = records.Count(record => record.RecoveryState == UpdateRecoveryState.Restored);
            RemovedCount = records.Count(record => record.RecoveryState == UpdateRecoveryState.Removed);
            FailedCount = records.Count(record => record.RecoveryState == UpdateRecoveryState.Failed);
        }

        public IReadOnlyList<UpdateFileRecord> Records { get; }
        public int AttemptedCount => Records.Count(record => record.CommitStarted);
        public int RestoredCount { get; }
        public int RemovedCount { get; }
        public int FailedCount { get; }
        public bool IsComplete => FailedCount == 0;
    }

    internal sealed class UpdateApplyResult
    {
        public UpdateApplyResult(
            UpdateApplyOutcome outcome,
            string? failureReason,
            IReadOnlyList<UpdateFileRecord> records,
            UpdateRollbackResult? rollback,
            IReadOnlyList<string>? skippedPaths = null)
        {
            Outcome = outcome;
            FailureReason = failureReason;
            Records = records;
            Rollback = rollback;
            SkippedPaths = skippedPaths ?? Array.Empty<string>();
        }

        public UpdateApplyOutcome Outcome { get; }
        public string? FailureReason { get; }
        public IReadOnlyList<UpdateFileRecord> Records { get; }
        public UpdateRollbackResult? Rollback { get; }
        public bool Succeeded => Outcome == UpdateApplyOutcome.Succeeded;

        /// <summary>
        /// 被保护而没有覆盖的路径（用户数据 / 更新器自身）。
        /// </summary>
        /// <remarks>
        /// 这些路径**没有**被删除、也没有被覆盖：更新对它们完全不做任何写操作。
        /// </remarks>
        public IReadOnlyList<string> SkippedPaths { get; }
    }

    internal interface IUpdateFileOperations
    {
        bool FileExists(string path);
        void CreateDirectory(string path);
        void CopyFile(string source, string destination, bool overwrite);
        void MoveFile(string source, string destination, bool overwrite);
        void ReplaceFile(string source, string destination, string? backup);
        void DeleteFile(string path);
        long GetFileLength(string path);
        string ComputeSha256(string path);
    }

    internal sealed class SystemUpdateFileOperations : IUpdateFileOperations
    {
        public bool FileExists(string path) => File.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void CopyFile(string source, string destination, bool overwrite) =>
            File.Copy(source, destination, overwrite);
        public void MoveFile(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);
        public void ReplaceFile(string source, string destination, string? backup) =>
            File.Replace(source, destination, backup);
        public void DeleteFile(string path) => File.Delete(path);
        public long GetFileLength(string path) => new FileInfo(path).Length;

        public string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
    }

    internal sealed class UpdateFileTransaction
    {
        /// <summary>
        /// 用户数据文件（相对安装目录）。更新**不得**覆盖它们。
        /// </summary>
        /// <remarks>
        /// ★ 这些文件都住在安装目录里（便携版布局），不在 %APPDATA%：
        ///   · settings.json —— 用户配置（含 Web 口令哈希）与它的备份/临时文件
        ///   · HardwareHistory.json / TrafficHistory.json —— 历史数据与它们的备份/临时文件
        /// ★ 主题与插件单独按目录保护，见 <see cref="UserOwnedDirectory"/>。
        /// </remarks>
        private static readonly string[] UserOwnedFiles =
        {
            "settings.json",
            "settings.json.bak",
            "settings.json.tmp",
            "HardwareHistory.json",
            "HardwareHistory.json.bak",
            "HardwareHistory.json.tmp",
            "TrafficHistory.json",
            "TrafficHistory.json.bak",
            "TrafficHistory.json.tmp"
        };

        /// <summary>
        /// 用户数据目录（相对安装目录）。更新**不得**覆盖其下的任何文件。
        /// </summary>
        /// <remarks>
        /// ★ resources\themes —— 主题编辑器直接写回这个目录，用户改过的主题就在这里；
        ///   发布包本身带着同名内置主题，直接覆盖等于每次更新都把用户主题退回默认。
        /// ★ resources\plugins —— 插件市场把插件装到同一个目录，市场安装/更新过的插件
        ///   与内置插件同名，覆盖会退回内置版本并丢掉市场侧的版本标记。
        /// ★ 有意的取舍：这样"自动更新"不再刷新内置主题/内置插件资源；
        ///   需要新版内置资源时走全新安装。用户数据优先于内置资源的自动刷新。
        /// </remarks>
        private static readonly string[] UserOwnedDirectory =
        {
            "resources\\themes",
            "resources\\plugins"
        };

        /// <summary>
        /// 判断某个相对路径是不是"用户拥有的数据"，更新不得覆盖它。
        /// </summary>
        /// <param name="relativePath">相对安装目录的路径。</param>
        internal static bool IsUserOwnedPath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;

            string normalized = relativePath.Replace('/', '\\').TrimStart('\\');

            foreach (string owned in UserOwnedFiles)
            {
                if (string.Equals(normalized, owned, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (string ownedDirectory in UserOwnedDirectory)
            {
                if (normalized.StartsWith(ownedDirectory + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly string _destinationRoot;
        private readonly string _backupRoot;
        private readonly IUpdateFileOperations _operations;
        private readonly int _commitAttempts;
        private readonly TimeSpan _commitRetryDelay;
        private readonly List<UpdateFileRecord> _records = new();
        private readonly List<string> _skippedPaths = new();

        public UpdateFileTransaction(
            string destinationRoot,
            string backupRoot,
            IUpdateFileOperations? operations = null,
            int commitAttempts = 10,
            TimeSpan? commitRetryDelay = null)
        {
            _destinationRoot = Path.GetFullPath(destinationRoot);
            _backupRoot = Path.GetFullPath(backupRoot);
            _operations = operations ?? new SystemUpdateFileOperations();
            _commitAttempts = Math.Max(1, commitAttempts);
            _commitRetryDelay = commitRetryDelay ?? TimeSpan.FromMilliseconds(500);
        }

        public UpdateApplyResult ApplyDirectory(
            string sourceRoot,
            Func<string, bool> skip)
        {
            if (_records.Count != 0)
            {
                throw new InvalidOperationException("A file transaction can only be applied once.");
            }

            _operations.CreateDirectory(_backupRoot);

            try
            {
                foreach (string sourcePath in Directory
                    .GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                    if (skip(relativePath))
                    {
                        // ★ 被跳过的文件**完全不碰**：既不删除，也不覆盖。
                        //   用户数据（配置 / 主题 / 插件）就靠这一条保住。
                        _skippedPaths.Add(relativePath);
                        continue;
                    }

                    ApplyFile(sourcePath, relativePath);
                }

                return new UpdateApplyResult(
                    UpdateApplyOutcome.Succeeded,
                    failureReason: null,
                    _records.ToArray(),
                    rollback: null,
                    skippedPaths: _skippedPaths.ToArray());
            }
            catch (Exception ex)
            {
                return CreateFailureResult(ex.Message);
            }
        }

        public UpdateApplyResult RollbackAfterFailure(string failureReason) =>
            CreateFailureResult(failureReason);

        public UpdateRollbackResult Rollback()
        {
            foreach (UpdateFileRecord record in _records
                .Where(record => record.CommitStarted)
                .Reverse())
            {
                try
                {
                    if (record.OriginallyExisted)
                    {
                        RestoreOriginalFile(record);
                    }
                    else
                    {
                        RemoveAddedFile(record);
                    }
                }
                catch (Exception ex)
                {
                    record.RecoveryState = UpdateRecoveryState.Failed;
                    record.RecoveryError = ex.Message;
                }
            }

            return new UpdateRollbackResult(_records.ToArray());
        }

        private UpdateApplyResult CreateFailureResult(string failureReason)
        {
            UpdateRollbackResult rollback = Rollback();
            UpdateApplyOutcome outcome;

            if (rollback.AttemptedCount == 0)
            {
                outcome = UpdateApplyOutcome.FailedBeforeCommit;
            }
            else if (rollback.IsComplete)
            {
                outcome = UpdateApplyOutcome.FailedRecovered;
            }
            else
            {
                outcome = UpdateApplyOutcome.FailedRecoveryIncomplete;
            }

            return new UpdateApplyResult(
                outcome,
                failureReason,
                _records.ToArray(),
                rollback,
                skippedPaths: _skippedPaths.ToArray());
        }

        private void ApplyFile(string sourcePath, string relativePath)
        {
            string destinationPath = Path.Combine(_destinationRoot, relativePath);
            string destinationDirectory = Path.GetDirectoryName(destinationPath)
                ?? _destinationRoot;
            _operations.CreateDirectory(destinationDirectory);

            bool originallyExisted = _operations.FileExists(destinationPath);
            string? backupPath = originallyExisted
                ? Path.Combine(_backupRoot, relativePath)
                : null;

            // The recovery record exists before the first operation that can
            // change the destination. A process crash is still not a complete
            // multi-file transaction, but every in-process failure has enough
            // information to recover this file and every earlier file.
            var record = new UpdateFileRecord(
                relativePath,
                destinationPath,
                originallyExisted,
                backupPath);
            _records.Add(record);

            string? stagePath = null;
            try
            {
                if (originallyExisted)
                {
                    string backupDirectory = Path.GetDirectoryName(backupPath)
                        ?? _backupRoot;
                    _operations.CreateDirectory(backupDirectory);
                    _operations.CopyFile(destinationPath, backupPath!, overwrite: true);
                    record.BackupCreated = true;
                    VerifyMatches(
                        destinationPath,
                        backupPath!,
                        relativePath,
                        "备份");
                }

                stagePath = CreateSiblingPath(destinationDirectory, "stage");
                _operations.CopyFile(sourcePath, stagePath, overwrite: false);
                VerifyMatches(sourcePath, stagePath, relativePath, "暂存");

                record.CommitStarted = true;
                // source and destination live in the same directory, so existing
                // files use a same-volume replacement and new files use a move.
                CommitStagedFile(
                    stagePath,
                    destinationPath,
                    overwriteExisting: originallyExisted);

                stagePath = null;
                VerifyMatches(sourcePath, destinationPath, relativePath, "提交后");
                record.CommittedAndVerified = true;
            }
            finally
            {
                TryDelete(stagePath);
            }
        }

        private void RestoreOriginalFile(UpdateFileRecord record)
        {
            if (string.IsNullOrEmpty(record.BackupPath) ||
                !_operations.FileExists(record.BackupPath))
            {
                throw new FileNotFoundException(
                    "恢复备份不存在，无法还原原文件。",
                    record.BackupPath);
            }

            string destinationDirectory = Path.GetDirectoryName(record.DestinationPath)
                ?? _destinationRoot;
            _operations.CreateDirectory(destinationDirectory);
            string rollbackStage = CreateSiblingPath(destinationDirectory, "rollback");

            try
            {
                _operations.CopyFile(record.BackupPath, rollbackStage, overwrite: false);
                VerifyMatches(record.BackupPath, rollbackStage, record.RelativePath, "回滚暂存");

                CommitStagedFile(
                    rollbackStage,
                    record.DestinationPath,
                    overwriteExisting: _operations.FileExists(record.DestinationPath));

                VerifyMatches(
                    record.BackupPath,
                    record.DestinationPath,
                    record.RelativePath,
                    "回滚后");
                record.RecoveryState = UpdateRecoveryState.Restored;
                record.RecoveryError = null;
            }
            finally
            {
                TryDelete(rollbackStage);
            }
        }

        private void RemoveAddedFile(UpdateFileRecord record)
        {
            if (_operations.FileExists(record.DestinationPath))
            {
                _operations.DeleteFile(record.DestinationPath);
            }

            record.RecoveryState = UpdateRecoveryState.Removed;
            record.RecoveryError = null;
        }

        private void CommitStagedFile(
            string stagePath,
            string destinationPath,
            bool overwriteExisting)
        {
            for (int attempt = 1; attempt <= _commitAttempts; attempt++)
            {
                try
                {
                    if (overwriteExisting)
                    {
                        _operations.ReplaceFile(stagePath, destinationPath, backup: null);
                    }
                    else
                    {
                        _operations.MoveFile(stagePath, destinationPath, overwrite: false);
                    }

                    return;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == _commitAttempts)
                    {
                        throw;
                    }

                    if (_commitRetryDelay > TimeSpan.Zero)
                    {
                        Thread.Sleep(_commitRetryDelay);
                    }
                }
            }
        }

        private void VerifyMatches(
            string expectedPath,
            string actualPath,
            string relativePath,
            string phase)
        {
            long expectedLength = _operations.GetFileLength(expectedPath);
            long actualLength = _operations.GetFileLength(actualPath);
            if (expectedLength != actualLength)
            {
                throw new IOException(
                    $"{phase}长度不一致：{relativePath}（期望 {expectedLength} / 实际 {actualLength}）");
            }

            string expectedHash = _operations.ComputeSha256(expectedPath);
            string actualHash = _operations.ComputeSha256(actualPath);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"{phase}摘要不一致：{relativePath}");
            }
        }

        private static string CreateSiblingPath(string directory, string purpose) =>
            Path.Combine(directory, $".lmu-{purpose}-{Guid.NewGuid():N}.tmp");

        private void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path) || !_operations.FileExists(path))
            {
                return;
            }

            try
            {
                _operations.DeleteFile(path);
            }
            catch
            {
                // A stale staging file is diagnostic material, not a reason to
                // hide the original commit or recovery result.
            }
        }
    }
}
