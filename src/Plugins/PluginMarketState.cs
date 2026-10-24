using System;
using System.IO;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 市场安装记录：记录"哪些插件是通过插件市场装的、装的是哪一版"。
    /// 这是插件卸载的所有权判定与更新检测的唯一依据。
    /// </summary>
    public class MarketPluginRecord
    {
        /// <summary>插件 id（落盘文件名的唯一来源）</summary>
        public string Id { get; set; } = "";

        /// <summary>落盘文件名（= id + ".json"），仅用于校验记录是否被篡改</summary>
        public string File { get; set; } = "";

        /// <summary>安装时的版本号</summary>
        public string Version { get; set; } = "";

        /// <summary>安装时内容的 SHA-256（小写十六进制），用于更新检测</summary>
        public string Sha256 { get; set; } = "";

        /// <summary>安装时间（ISO8601）</summary>
        public string InstalledAt { get; set; } = "";
    }

    /// <summary>
    /// 插件市场安装记录的读写。记录挂在 Settings.MarketPlugins 上，随 settings.json 持久化。
    /// </summary>
    public static class PluginMarketState
    {
        /// <summary>查找某 id 的安装记录；未通过市场安装过则返回 null</summary>
        public static MarketPluginRecord? Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return SettingsHelper.FindMarketRecord(id);
        }

        internal static MarketPluginRecord? FindPersisted(string id)
        {
            if (!SettingsHelper.TryReadPersistedMarketRecord(id, out var record))
                throw new IOException("The persisted market record is unavailable; recovery remains pending.");
            return record;
        }

        internal static MarketPluginRecord? FindPersistedForRecovery(string id)
        {
            if (!SettingsHelper.TryReadPersistedMarketRecord(id, out var record, synchronizeMemory: true))
                throw new IOException("The persisted market record is unavailable; recovery remains pending.");
            return record;
        }

        /// <summary>写入或更新一条安装记录</summary>
        public static void Upsert(MarketPluginRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Id)) return;

            if (!SettingsHelper.TryChangeMarketRecord(record.Id, record))
                throw new MarketRecordPersistenceException();
        }

        /// <summary>移除一条安装记录</summary>
        public static void Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!SettingsHelper.TryChangeMarketRecord(id, null))
                throw new MarketRecordPersistenceException();
        }
    }

    // Only the strict record writer creates this failure, after restoring memory
    // inside the shared save gate. The installer need not attempt another save.
    internal sealed class MarketRecordPersistenceException : IOException
    {
        internal MarketRecordPersistenceException()
            : base("The market record was not saved; its in-memory change was restored.") { }
    }
}
