using System;
using System.IO;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 主题市场安装记录的读写（镜像 PluginMarketState/F5）。
    /// 记录挂在 Settings.Themes 上，随 settings.json 持久化。
    /// 主题身份 = 文件名 + sha256（F16），记录形状与插件一致（复用 MarketPluginRecord）。
    /// </summary>
    public static class ThemeMarketState
    {
        /// <summary>查找某 id 的安装记录；未通过市场安装过则返回 null</summary>
        public static MarketPluginRecord? Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return SettingsHelper.FindThemeRecord(id);
        }

        internal static MarketPluginRecord? FindPersisted(string id)
        {
            if (!SettingsHelper.TryReadPersistedThemeRecord(id, out var record))
                throw new IOException("The persisted theme market record is unavailable; recovery remains pending.");
            return record;
        }

        internal static MarketPluginRecord? FindPersistedForRecovery(string id)
        {
            if (!SettingsHelper.TryReadPersistedThemeRecord(id, out var record, synchronizeMemory: true))
                throw new IOException("The persisted theme market record is unavailable; recovery remains pending.");
            return record;
        }

        /// <summary>写入或更新一条安装记录</summary>
        public static void Upsert(MarketPluginRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Id)) return;

            if (!SettingsHelper.TryChangeThemeRecord(record.Id, record))
                throw new MarketRecordPersistenceException();
        }

        /// <summary>移除一条安装记录</summary>
        public static void Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!SettingsHelper.TryChangeThemeRecord(id, null))
                throw new MarketRecordPersistenceException();
        }
    }
}
