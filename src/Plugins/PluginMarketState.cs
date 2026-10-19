using System;
using System.Collections.Generic;

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

            var settings = Settings.Load();
            var list = settings.MarketPlugins;
            if (list == null) return null;

            foreach (var r in list)
            {
                if (r != null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
            return null;
        }

        /// <summary>写入或更新一条安装记录</summary>
        public static void Upsert(MarketPluginRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Id)) return;

            var settings = Settings.Load();
            if (settings.MarketPlugins == null)
                settings.MarketPlugins = new List<MarketPluginRecord>();

            int idx = settings.MarketPlugins.FindIndex(
                r => r != null && string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0) settings.MarketPlugins[idx] = record;
            else settings.MarketPlugins.Add(record);

            settings.Save();
        }

        /// <summary>移除一条安装记录</summary>
        public static void Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            var settings = Settings.Load();
            if (settings.MarketPlugins == null) return;

            int removed = settings.MarketPlugins.RemoveAll(
                r => r != null && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

            if (removed > 0) settings.Save();
        }
    }
}
