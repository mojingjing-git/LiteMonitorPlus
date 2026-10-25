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
        /// </summary>
        internal static bool Uninstall(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
            Action<string> removeRecord,
            HashSet<string>? builtinIds,
            Func<string, string, bool>? contentIdentityMatches,
            out MarketErrorReason reason)
        {
            if (findRecord == null) throw new ArgumentNullException(nameof(findRecord));
            if (removeRecord == null) throw new ArgumentNullException(nameof(removeRecord));

            lock (PluginMarketInstallCore.TransactionGate)
                return UninstallLocked(entry, pluginsDirectory, findRecord, removeRecord,
                    builtinIds, contentIdentityMatches, out reason);
        }

        private static bool UninstallLocked(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
            Action<string> removeRecord,
            HashSet<string>? builtinIds,
            Func<string, string, bool>? contentIdentityMatches,
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
            PluginMarketInstallCore.EnsureNoPendingInstall(pluginsDirectory, plan.RecordId);
            string backupName = Path.GetFileName(plan.TargetPath) + "." + Guid.NewGuid().ToString("N") + ".uninstall";
            if (!PluginMarketInstallCore.TryResolveDirectChildPath(
                    pluginsDirectory, Path.GetFileName(plan.TargetPath), out _, out _) ||
                !PluginMarketInstallCore.TryResolveDirectChildPath(
                    pluginsDirectory, backupName, out string backupPath, out _))
            {
                reason = MarketErrorReason.RecordCorrupted;
                return false;
            }

            bool hadFile = File.Exists(plan.TargetPath);
            if (hadFile) File.Copy(plan.TargetPath, backupPath, overwrite: false);
            try
            {
                if (hadFile) File.Delete(plan.TargetPath);
                removeRecord(plan.RecordId);
            }
            catch
            {
                // Strict record removal restores its own memory on save failure.
                // Restore the payload too, retaining the copy if restoration fails.
                if (hadFile) MarketFileIo.MoveAtomic(backupPath, plan.TargetPath);
                throw;
            }

            // The uninstall is durable; a leftover .uninstall file is only garbage.
            try { if (hadFile) File.Delete(backupPath); } catch { }
            return true;
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
