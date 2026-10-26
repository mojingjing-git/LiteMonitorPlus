using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>已加载的主题文档字节与身份。</summary>
    public sealed record ThemeDocument(string ThemeName, string SourcePath, byte[] SourceBytes, string Sha256);

    /// <summary>
    /// 恢复快照（重做计划 §5.1）：写入应用用户数据目录，与正式主题及市场安装记录隔离。
    /// 覆盖层以"路径 → 事务后值"记录（<see cref="ThemeOverlayChange.After"/>，null = 删除键）。
    /// </summary>
    public sealed class ThemeDraftSnapshot
    {
        public string ThemeName { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string SourceSha256 { get; set; } = string.Empty;
        public long Revision { get; set; }
        public long SavedRevision { get; set; }
        public bool IsRecoveredCopy { get; set; }
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
        public List<ThemeOverlayChange> Overlay { get; set; } = new();

        /// <summary>草稿配置快照（不含运行期字体；恢复后重新 BuildFonts）。</summary>
        public Theme DraftConfig { get; set; } = new();
    }

    /// <summary>
    /// 主题文档存储：封装 <see cref="ThemeFileService"/>，补摘要与恢复目录。
    /// 原子写入一律复用 ThemeFileService 的共用核心，不另写替换文件逻辑（重做计划 §5.2）。
    /// </summary>
    public static class ThemeDocumentStore
    {
        /// <summary>恢复草稿目录：应用用户数据目录下，最多 10 份、总量 8 MiB（TE01 冻结）。</summary>
        public const int MaxRecoveryDrafts = 10;
        public const long MaxRecoveryTotalBytes = 8 * 1024 * 1024;

        private static string? _recoveryDirOverride;
        private static string? _themeDirOverride;

        internal static string RecoveryDir
        {
            get
            {
                if (_recoveryDirOverride is not null) return _recoveryDirOverride;
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(baseDir, "LiteMonitor++", "theme-studio", "drafts");
            }
        }

        internal static void SetRecoveryDirForTests(string dir)
        {
            _recoveryDirOverride = dir;
        }

        internal static void ResetRecoveryDir()
        {
            _recoveryDirOverride = null;
        }

        /// <summary>主题文档目录。测试可重定向（生产恒为 ThemeManager.ThemeDir）。</summary>
        internal static string ThemeDir => _themeDirOverride ?? ThemeManager.ThemeDir;

        internal static void SetThemeDirForTests(string dir)
        {
            _themeDirOverride = dir;
        }

        // ------------------------------------------------------------
        // 主题文档
        // ------------------------------------------------------------

        public static string PathOf(string themeName) => Path.Combine(ThemeDir, themeName + ".json");

        public static bool Exists(string themeName) => File.Exists(PathOf(themeName));

        public static IReadOnlyList<string> ListThemes() => ThemeFileService.ListThemes(ThemeDir);

        /// <summary>读取主题字节与摘要；文件缺失或不可读时抛出（由 UI 层呈现原因）。</summary>
        public static ThemeDocument Read(string themeName)
        {
            string path = PathOf(themeName);
            byte[] bytes = File.ReadAllBytes(path);
            return new ThemeDocument(themeName, path, bytes, ThemeDocumentSession.Sha256Of(bytes));
        }

        /// <summary>
        /// 按载荷直写主题 JSON（补丁式保存入口）。
        /// 走 <see cref="ThemeFileService"/> 的临时文件 + 安全替换核心。
        /// </summary>
        public static void WriteThemeJson(string themeName, string json)
        {
            ThemeFileService.WriteThemeJson(ThemeDir, themeName, json);
        }

        // ------------------------------------------------------------
        // 恢复草稿
        // ------------------------------------------------------------

        private static string RecoveryPathOf(string themeName) =>
            Path.Combine(RecoveryDir, themeName + ".recovery.json");

        /// <summary>持久化恢复快照（原子写入；触限时淘汰最旧的其它草稿）。</summary>
        public static void PersistRecovery(ThemeDraftSnapshot snapshot)
        {
            Directory.CreateDirectory(RecoveryDir);
            EnforceRecoveryLimits(exclude: snapshot.ThemeName);

            string path = RecoveryPathOf(snapshot.ThemeName);
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(snapshot, ThemeManager.CreateSerializerOptions());
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>列出存在恢复草稿的主题名。</summary>
        public static IReadOnlyList<string> ListRecoveries()
        {
            if (!Directory.Exists(RecoveryDir)) return Array.Empty<string>();

            var names = new List<string>();
            foreach (string file in Directory.GetFiles(RecoveryDir, "*.recovery.json"))
            {
                names.Add(Path.GetFileName(file)[..^".recovery.json".Length]);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>读取恢复快照；损坏的恢复文件按不存在处理并删除。</summary>
        public static bool TryLoadRecovery(string themeName, out ThemeDraftSnapshot? snapshot)
        {
            snapshot = null;
            string path = RecoveryPathOf(themeName);
            if (!File.Exists(path)) return false;

            try
            {
                string json = File.ReadAllText(path);
                snapshot = JsonSerializer.Deserialize<ThemeDraftSnapshot>(json, ThemeManager.CreateSerializerOptions());
                return snapshot is not null;
            }
            catch
            {
                try { File.Delete(path); } catch { /* 删不掉不影响其余功能 */ }
                return false;
            }
        }

        /// <summary>删除某主题的恢复草稿（保存成功后调用）。</summary>
        public static void DeleteRecovery(string themeName)
        {
            string path = RecoveryPathOf(themeName);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删除失败不阻塞保存
            }
        }

        /// <summary>恢复目录超过数量／总量上限时按最旧先删（不含排除项）。</summary>
        private static void EnforceRecoveryLimits(string exclude)
        {
            try
            {
                var entries = new List<(string Path, DateTime Written, long Length)>();
                foreach (string file in Directory.GetFiles(RecoveryDir, "*.recovery.json"))
                {
                    string name = Path.GetFileName(file)[..^".recovery.json".Length];
                    if (string.Equals(name, exclude, StringComparison.Ordinal)) continue;
                    var info = new FileInfo(file);
                    entries.Add((file, info.LastWriteTimeUtc, info.Length));
                }

                long total = 0;
                foreach ((_, _, long length) in entries) total += length;

                entries.Sort((a, b) => a.Written.CompareTo(b.Written));
                int index = 0;
                while ((entries.Count - index > MaxRecoveryDrafts - 1 || total > MaxRecoveryTotalBytes) &&
                       index < entries.Count)
                {
                    try { File.Delete(entries[index].Path); } catch { }
                    total -= entries[index].Length;
                    index++;
                }
            }
            catch
            {
                // 限额兜底失败不影响主流程
            }
        }
    }
}
