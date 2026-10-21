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
            UpdateRollbackResult? rollback)
        {
            Outcome = outcome;
            FailureReason = failureReason;
            Records = records;
            Rollback = rollback;
        }

        public UpdateApplyOutcome Outcome { get; }
        public string? FailureReason { get; }
        public IReadOnlyList<UpdateFileRecord> Records { get; }
        public UpdateRollbackResult? Rollback { get; }
        public bool Succeeded => Outcome == UpdateApplyOutcome.Succeeded;
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
        private readonly string _destinationRoot;
        private readonly string _backupRoot;
        private readonly IUpdateFileOperations _operations;
        private readonly int _commitAttempts;
        private readonly TimeSpan _commitRetryDelay;
        private readonly List<UpdateFileRecord> _records = new();

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
                        continue;
                    }

                    ApplyFile(sourcePath, relativePath);
                }

                return new UpdateApplyResult(
                    UpdateApplyOutcome.Succeeded,
                    failureReason: null,
                    _records.ToArray(),
                    rollback: null);
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
                rollback);
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
