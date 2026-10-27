using System;
using System.IO;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// Internal uninstall boundary. A plan is created only after every ownership
    /// check succeeds, so callers can safely use it before touching disk/state.
    /// </summary>
    internal static class PluginMarketUninstallCore
    {
        internal static bool Uninstall(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
            Action<string> removeRecord,
            out MarketErrorReason reason)
            => Uninstall(entry, pluginsDirectory, findRecord, removeRecord,
                PluginConstants.BuiltinPluginIds, PluginMarket.FileContentIdMatches, out reason);

        /// <summary>
        /// 卸载（M1 资产类型泛化）。<paramref name="builtinIds"/> 为 null 时跳过内置清单检查
        /// （主题没有内置常量表："内置 = 无安装记录"，NotManaged 已覆盖）；
        /// <paramref name="contentIdentityMatches"/> 决定"文件内容与记录身份一致"的判据
        /// （插件 = 内容 id 匹配；主题 = JSON 可解析，完整性由记录摘要比对兜底，F16）。
        /// <paramref name="faultInjector"/> 仅测试用：在事务边界间注入
        /// <see cref="MarketInstallCrashException"/> 模拟进程强杀（LM-008 安装侧同构）。
        /// </summary>
        internal static bool Uninstall(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
            Action<string> removeRecord,
            HashSet<string>? builtinIds,
            Func<string, string, bool>? contentIdentityMatches,
            out MarketErrorReason reason,
            Action<string>? faultInjector = null)
        {
            if (findRecord == null) throw new ArgumentNullException(nameof(findRecord));
            if (removeRecord == null) throw new ArgumentNullException(nameof(removeRecord));

            lock (PluginMarketInstallCore.TransactionGate)
                return UninstallLocked(entry, pluginsDirectory, findRecord, removeRecord,
                    builtinIds, contentIdentityMatches, faultInjector, out reason);
        }

        private static bool UninstallLocked(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
            Action<string> removeRecord,
            HashSet<string>? builtinIds,
            Func<string, string, bool>? contentIdentityMatches,
            Action<string>? faultInjector,
            out MarketErrorReason reason)
        {
            if (!TryCreatePlan(
                    entry,
                    pluginsDirectory,
                    findRecord,
                    builtinIds,
                    contentIdentityMatches,
                    out PluginUninstallPlan? createdPlan,
                    out reason))
                return false;

            PluginUninstallPlan plan = createdPlan!;
            // [S03] 卸载事务化：写前日志 → 备份 → 删载荷 → 删记录（提交点）→ 清理。
            // 进程在任一边界被强杀后，由 PluginMarketInstallCore 的启动恢复对账收尾。
            // 安装事务与卸载事务共用同一 pending 日志，双方都不得越过对方的在途事务。
            PluginMarketInstallCore.EnsureNoPendingInstall(pluginsDirectory, plan.RecordId);

            string targetName = Path.GetFileName(plan.TargetPath);
            string txId = Guid.NewGuid().ToString("N");
            // 备份名必须与安装事务同构（targetName + "." + txId + ".bak"）：
            // TryResolveTransactionPaths 的一致性校验按该形状重建路径（R04）。
            string backupName = targetName + "." + txId + ".bak";
            if (!PluginMarketInstallCore.TryResolveDirectChildPath(
                    pluginsDirectory, targetName, out _, out _) ||
                !PluginMarketInstallCore.TryResolveDirectChildPath(
                    pluginsDirectory, backupName, out string backupPath, out _))
            {
                reason = MarketErrorReason.RecordCorrupted;
                return false;
            }

            bool hadFile = File.Exists(plan.TargetPath);
            // TryCreatePlan 已确认记录存在；此处再取一次是为拿到旧记录摘要。
            MarketPluginRecord? recordForSha = findRecord(plan.RecordId);
            string previousRecordSha = recordForSha?.Sha256 ?? "";
            if (!PluginMarket.IsUsableSha256(previousRecordSha))
            {
                reason = MarketErrorReason.RecordCorrupted;
                return false;
            }

            MarketInstallTransaction tx = new()
            {
                TxId = txId,
                PluginId = plan.RecordId,
                Target = targetName,
                // Stage 文件在卸载事务中永不创建，但路径重建校验要求字段形状一致。
                Stage = targetName + "." + txId + ".stage",
                Backup = backupName,
                Sha256 = previousRecordSha,
                Operation = "uninstall",
                RecoveryVersion = 1,
                HadTarget = hadFile,
                PreviousFileSha256 = hadFile ? PluginMarketInstallCore.ComputeFileSha(plan.TargetPath) : "",
                PreviousRecordSha256 = previousRecordSha
            };

            // [S03] ① 意图写入：提交点之前唯一持久化事实。此后任一点强杀，
            // 启动恢复都能按 journal 把系统对账回一致状态。
            PluginMarketInstallCore.AppendPending(pluginsDirectory, tx);
            faultInjector?.Invoke("afterJournal");

            if (hadFile) File.Copy(plan.TargetPath, backupPath, overwrite: false);
            faultInjector?.Invoke("afterBackup");
            try
            {
                // ② 载荷删除 → ③ 记录移除（提交点：安装记录持久化移除）。
                if (hadFile) File.Delete(plan.TargetPath);
                faultInjector?.Invoke("afterPayloadDelete");
                removeRecord(plan.RecordId);
                faultInjector?.Invoke("afterRecord");

                // ④ 清理：提交点之后，残留备份与日志条目都只是垃圾；
                // 任一清理步骤失败都不回滚（启动恢复幂等收尾）。
                // MarketInstallCrashException（模拟强杀）不在此吞：它必须穿透，
                // 让调用方看到"进程死于该边界"，与真实强杀语义一致。
                try
                {
                    if (hadFile) File.Delete(backupPath);
                    faultInjector?.Invoke("afterBackupDelete");
                    PluginMarketInstallCore.RemovePending(pluginsDirectory, txId);
                }
                catch (Exception ex) when (ex is not MarketInstallCrashException)
                {
                    // Uninstall is durable; leftovers are recoverable garbage handled by startup recovery.
                }
                return true;
            }
            catch (MarketInstallCrashException)
            {
                // 模拟进程被杀：不做回滚，保留写前日志，交给启动恢复（与安装事务同一契约）。
                throw;
            }
            catch
            {
                // Strict record removal restores its own memory on save failure.
                // Restore the payload too, retaining the copy if restoration fails.
                if (hadFile && File.Exists(backupPath)) MarketFileIo.MoveAtomic(backupPath, plan.TargetPath);
                try { PluginMarketInstallCore.RemovePending(pluginsDirectory, txId); }
                catch
                {
                    // 回滚路径的 journal 残留交给启动恢复对账幂等处理（记录仍在 ⇒ 恢复载荷分支）。
                }
                throw;
            }
        }

        internal static bool TryCreatePlan(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
            out PluginUninstallPlan? plan,
            out MarketErrorReason reason)
            => TryCreatePlan(entry, pluginsDirectory, findRecord,
                PluginConstants.BuiltinPluginIds, PluginMarket.FileContentIdMatches,
                out plan, out reason);

        /// <summary>M1 资产类型泛化的卸载计划创建（参数语义见 <see cref="Uninstall"/>）。</summary>
        internal static bool TryCreatePlan(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
            HashSet<string>? builtinIds,
            Func<string, string, bool>? contentIdentityMatches,
            out PluginUninstallPlan? plan,
            out MarketErrorReason reason)
        {
            plan = null;
            reason = MarketErrorReason.Unknown;

            if (entry == null || !PluginMarket.TryValidatePluginId(entry.Id, out _))
            {
                reason = MarketErrorReason.InvalidId;
                return false;
            }

            if (builtinIds != null && builtinIds.Contains(entry.Id))
            {
                reason = MarketErrorReason.BuiltinConflict;
                return false;
            }

            MarketPluginRecord? record = findRecord(entry.Id);
            if (record == null)
            {
                reason = MarketErrorReason.NotManaged;
                return false;
            }

            if (!PluginMarket.TryValidatePluginId(record.Id, out _) ||
                !string.Equals(entry.Id, record.Id, StringComparison.OrdinalIgnoreCase))
            {
                reason = MarketErrorReason.RecordCorrupted;
                return false;
            }

            if (builtinIds != null && builtinIds.Contains(record.Id))
            {
                reason = MarketErrorReason.BuiltinConflict;
                return false;
            }

            string expectedFile = PluginMarket.GetInstallFileName(record.Id);
            if (!string.Equals(record.File, expectedFile, StringComparison.OrdinalIgnoreCase) ||
                !TryGetDirectChildFilePath(pluginsDirectory, expectedFile, out string targetPath))
            {
                reason = MarketErrorReason.RecordCorrupted;
                return false;
            }

            // A missing payload is an already-removed file. An existing payload
            // must still identify the same managed asset.
            if (File.Exists(targetPath) &&
                !(contentIdentityMatches ?? PluginMarket.FileContentIdMatches)(targetPath, record.Id))
            {
                reason = MarketErrorReason.RecordCorrupted;
                return false;
            }

            plan = new PluginUninstallPlan(record.Id, targetPath);
            reason = MarketErrorReason.Unknown;
            return true;
        }

        internal static bool TryGetDirectChildFilePath(
            string directory,
            string fileName,
            out string targetPath)
        {
            targetPath = "";
            if (string.IsNullOrWhiteSpace(directory) ||
                string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                fileName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                fileName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return false;
            }

            try
            {
                string normalizedDirectory = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(directory));
                string candidate = Path.GetFullPath(Path.Combine(normalizedDirectory, fileName));
                string? candidateDirectory = Path.GetDirectoryName(candidate);

                if (!string.Equals(
                        Path.TrimEndingDirectorySeparator(candidateDirectory ?? ""),
                        normalizedDirectory,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(candidate), fileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                targetPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal sealed class PluginUninstallPlan
    {
        internal PluginUninstallPlan(string recordId, string targetPath)
        {
            RecordId = recordId;
            TargetPath = targetPath;
        }

        internal string RecordId { get; }
        internal string TargetPath { get; }
    }
}
