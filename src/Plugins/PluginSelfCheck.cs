using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 启动自检：扫描插件目录，找出"文件名与内容 id 不一致"的疑似残留文件。
    ///
    /// 典型场景 —— 旧版市场用索引里的 file 字段落盘，仓库里的 plugins/PublicIP.json
    /// （内容 id = public_ip_info）会落成 PublicIP.json，与内置插件同名，
    /// 等于直接覆盖了内置插件。这类文件"文件名 stem ≠ 内容 id"，且不在市场安装记录里，
    /// 正是本自检要找出来并提示用户删除的对象。
    /// </summary>
    public static class PluginSelfCheck
    {
        /// <summary>自检发现的疑似残留文件（文件名 stem ≠ 内容 id，且不在市场记录内）</summary>
        public static List<string> SuspiciousFiles { get; } = new List<string>();

        /// <summary>执行一次自检。结果供插件市场页展示。</summary>
        public static void RunOnce()
        {
            SuspiciousFiles.Clear();

            string dir = PluginMarket.PluginsDir;
            if (!Directory.Exists(dir)) return;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch { return; }

            foreach (var f in files)
            {
                string stem = Path.GetFileNameWithoutExtension(f);

                // 市场装的，文件名由 id 推导，天然自洽
                if (PluginMarketState.Find(stem) != null) continue;

                string? fileId = TryReadPluginId(f);
                if (fileId == null) continue;   // 读不出 id，不是插件文件，跳过

                // ★ 判定：文件名 stem 与内容 id 不一致。
                //   所有"撞内置插件"的情形都必然满足这一条：
                //     · 旧版残留：stem=PublicIP（内置 id），内容 id=public_ip_info
                //     · 内容是内置插件但改了名：stem=my_plugin，内容 id=PublicIP
                //   内容自洽的文件（stem == 内容 id）不算可疑 —— 那是用户自己放的文件。
                if (!string.Equals(fileId, stem, StringComparison.OrdinalIgnoreCase))
                {
                    SuspiciousFiles.Add(f);
                }
            }
        }

        /// <summary>读取插件文件的 id 字段；读不出来返回 null</summary>
        private static string? TryReadPluginId(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    string id = idEl.GetString() ?? "";
                    return string.IsNullOrEmpty(id) ? null : id;
                }
            }
            catch { }
            return null;
        }
    }
}
