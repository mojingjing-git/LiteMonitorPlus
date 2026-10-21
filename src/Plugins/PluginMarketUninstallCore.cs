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
        {
            if (findRecord == null) throw new ArgumentNullException(nameof(findRecord));
            if (removeRecord == null) throw new ArgumentNullException(nameof(removeRecord));

            if (!TryCreatePlan(
                    entry,
                    pluginsDirectory,
                    findRecord,
                    out PluginUninstallPlan? createdPlan,
                    out reason))
                return false;

            PluginUninstallPlan plan = createdPlan!;
            if (File.Exists(plan.TargetPath))
                File.Delete(plan.TargetPath);

            removeRecord(plan.RecordId);
            return true;
        }

        internal static bool TryCreatePlan(
            MarketPluginEntry? entry,
            string pluginsDirectory,
            Func<string, MarketPluginRecord?> findRecord,
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

            if (PluginConstants.BuiltinPluginIds.Contains(entry.Id))
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

            if (PluginConstants.BuiltinPluginIds.Contains(record.Id))
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
            // must still identify the same managed plugin.
            if (File.Exists(targetPath) &&
                !PluginMarket.FileContentIdMatches(targetPath, record.Id))
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
