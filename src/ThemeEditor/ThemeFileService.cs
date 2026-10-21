using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 管理主题文件：加载、保存、新建、复制、重命名、删除
    ///
    /// ★ 保存可靠性（外观改造计划 A02）：
    ///   写主题文件一律走"同目录临时文件 + 安全替换"，
    ///   任何一步失败都**保留原文件内容不变**，并且向上抛出异常，绝不返回"保存成功"。
    /// </summary>
    public static class ThemeFileService
    {
        /// <summary>
        /// 临时文件后缀。
        /// 放在扩展名**之后**（如 `Foo.json.litemonitor-tmp`），有两个好处：
        ///   1) 一眼能认出是临时文件，不会和用户的 .json 主题混淆；
        ///   2) 不会被 `Directory.GetFiles(dir, "*.json")` 匹配成主题，不会出现在主题列表里。
        /// </summary>
        public const string TempFileSuffix = ".litemonitor-tmp";

        /// <summary>
        /// 残留临时文件的清理阈值：最后写入时间超过该时长的临时文件，
        /// 视为上次异常中断（崩溃 / 断电 / 杀软锁文件）留下的垃圾。
        /// </summary>
        private static readonly TimeSpan TempFileMaxAge = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 无 BOM 的 UTF-8：与原 <see cref="File.WriteAllText(string, string)"/> 的默认编码一致，
        /// 保证改造前后保存出的主题文件字节完全相同。
        /// </summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 主题目录
        /// </summary>
        private static string Dir => ThemeManager.ThemeDir;

        // ============================================================
        // 测试钩子：仅供单元测试注入"写入失败 / 替换失败"，生产代码不要赋值
        // ============================================================

        /// <summary>
        /// 写临时文件的操作。默认实现见 <see cref="WriteTempFileCore"/>。
        /// 单元测试可替换为抛异常的实现，用来注入"临时文件写入失败"。
        /// </summary>
        internal static Action<string, byte[]> TempFileWriter { get; set; } = WriteTempFileCore;

        /// <summary>
        /// 用临时文件替换目标文件的操作。默认实现见 <see cref="ReplaceTargetCore"/>。
        /// 单元测试可替换为抛异常的实现，用来注入"替换失败"。
        /// </summary>
        internal static Action<string, string> TargetReplacer { get; set; } = ReplaceTargetCore;

        /// <summary>
        /// 把测试钩子恢复成默认实现（单元测试收尾时调用，避免污染其它用例）。
        /// </summary>
        internal static void ResetTestHooks()
        {
            TempFileWriter = WriteTempFileCore;
            TargetReplacer = ReplaceTargetCore;
        }

        // ============================================================
        // 列表 / 加载
        // ============================================================

        /// <summary>
        /// 列出所有主题文件
        /// </summary>
        public static List<string> ListThemes()
        {
            return ListThemes(Dir);
        }

        /// <summary>
        /// 列出指定目录下所有主题文件
        /// </summary>
        public static List<string> ListThemes(string dir)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 顺手清理上次异常中断留下的临时文件（只删够旧的，不动正在写的那一个）
            CleanupTempFiles(dir);

            var list = new List<string>();

            foreach (var f in Directory.GetFiles(dir, "*.json"))
                list.Add(Path.GetFileNameWithoutExtension(f));

            list.Sort();
            return list;
        }

        /// <summary>
        /// 加载主题 JSON → Theme 对象
        /// </summary>
        public static Theme LoadTheme(string name)
        {
            return LoadTheme(Dir, name);
        }

        /// <summary>
        /// 从指定目录加载主题 JSON → Theme 对象
        /// </summary>
        public static Theme LoadTheme(string dir, string name)
        {
            string path = Path.Combine(dir, name + ".json");

            if (!File.Exists(path))
                throw new FileNotFoundException($"Theme not found: {path}");

            string json = File.ReadAllText(path);

            // 读写统一用 ThemeManager 的序列化选项：与 ThemeManager.Load 保持同一套
            // "已知键/扩展键"匹配规则（大小写不敏感），避免编辑器保存后出现
            // 同名已知键与扩展键重复写出（v4 §3.1）。
            var theme = JsonSerializer.Deserialize<Theme>(json, ThemeManager.CreateSerializerOptions());

            if (theme == null)
                throw new Exception($"Failed to parse theme JSON: {name}");

            // 兜住"用非大小写不敏感选项加载"的场景：丢弃与已知属性同名的扩展键。
            ThemeManager.PruneExtensionDataConflicts(theme);

            theme.BuildFonts();

            return theme;
        }

        // ============================================================
        // 保存 / 新建 / 复制 / 重命名 / 删除
        // ============================================================

        /// <summary>
        /// 保存 Theme 对象 → JSON 文件
        /// </summary>
        public static void SaveTheme(string name, Theme theme)
        {
            SaveTheme(Dir, name, theme);
        }

        /// <summary>
        /// 把 Theme 对象保存到指定目录下的 JSON 文件
        /// </summary>
        public static void SaveTheme(string dir, string name, Theme theme)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, name + ".json");
            // 与加载路径共用同一套序列化选项（WriteIndented 默认仍为 true，输出字节不变），
            // 保证"已知键"与"扩展键"的匹配规则一致。
            string json = JsonSerializer.Serialize(theme, ThemeManager.CreateSerializerOptions());

            SafeWrite(path, Utf8NoBom.GetBytes(json));
        }

        /// <summary>
        /// 新建主题（复制模板）
        /// </summary>
        public static void CreateTheme(string name)
        {
            CreateTheme(Dir, name);
        }

        /// <summary>
        /// 在指定目录下新建主题（复制模板）
        /// </summary>
        public static void CreateTheme(string dir, string name)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, name + ".json");

            if (File.Exists(path))
                throw new Exception("Theme already exists: " + name);

            // 默认复制当前主题模板
            string template = Path.Combine(dir, "DarkFlat_Classic.json");

            if (File.Exists(template))
            {
                // 走安全写入：复制中途失败不会留下半个主题文件
                SafeWrite(path, File.ReadAllBytes(template));
            }
            else
            {
                SafeWrite(path, Utf8NoBom.GetBytes("{}"));
            }
        }

        /// <summary>
        /// 复制主题
        /// </summary>
        public static void DuplicateTheme(string src, string dst)
        {
            DuplicateTheme(Dir, src, dst);
        }

        /// <summary>
        /// 在指定目录下复制主题
        /// </summary>
        public static void DuplicateTheme(string dir, string src, string dst)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string srcPath = Path.Combine(dir, src + ".json");
            string dstPath = Path.Combine(dir, dst + ".json");

            if (!File.Exists(srcPath))
                throw new FileNotFoundException("Source theme missing: " + src);

            if (File.Exists(dstPath))
                throw new Exception("Target theme already exists: " + dst);

            // 走安全写入：字节完全一致，中途失败不会留下半个主题文件
            SafeWrite(dstPath, File.ReadAllBytes(srcPath));
        }

        /// <summary>
        /// 重命名主题
        /// </summary>
        public static void RenameTheme(string src, string dst)
        {
            RenameTheme(Dir, src, dst);
        }

        /// <summary>
        /// 在指定目录下重命名主题
        /// </summary>
        public static void RenameTheme(string dir, string src, string dst)
        {
            string srcPath = Path.Combine(dir, src + ".json");
            string dstPath = Path.Combine(dir, dst + ".json");

            if (!File.Exists(srcPath))
                throw new FileNotFoundException("Source theme missing: " + src);

            if (File.Exists(dstPath))
                throw new Exception("Target theme already exists: " + dst);

            // 同目录改名是原子操作，失败时源文件仍在，不会丢主题
            File.Move(srcPath, dstPath);
        }

        /// <summary>
        /// 删除主题
        /// </summary>
        public static void DeleteTheme(string name)
        {
            DeleteTheme(Dir, name);
        }

        /// <summary>
        /// 从指定目录删除主题
        /// </summary>
        public static void DeleteTheme(string dir, string name)
        {
            string path = Path.Combine(dir, name + ".json");
            if (File.Exists(path))
                File.Delete(path);
        }

        // ============================================================
        // 安全写入
        // ============================================================

        /// <summary>
        /// 清理残留的临时文件。
        /// 只删"文件名以 <see cref="TempFileSuffix"/> 结尾"且"最后写入时间超过阈值"的文件，
        /// 避免误删正在写入中的临时文件，也绝不会碰用户的主题文件。
        /// </summary>
        /// <returns>实际删除的临时文件数量</returns>
        public static int CleanupTempFiles(string dir, TimeSpan? maxAge = null)
        {
            if (!Directory.Exists(dir))
                return 0;

            TimeSpan age = maxAge ?? TempFileMaxAge;
            DateTime cutoff = DateTime.Now - age;
            int removed = 0;

            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (!f.EndsWith(TempFileSuffix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        // 正在写入中的临时文件还"很新"，不删
                        if (File.GetLastWriteTime(f) > cutoff)
                            continue;

                        File.Delete(f);
                        removed++;
                    }
                    catch
                    {
                        // 单个临时文件删不掉（被占用）不影响其它文件
                    }
                }
            }
            catch
            {
                // 目录不可读时静默跳过，清理永远不能影响正常功能
            }

            return removed;
        }

        /// <summary>
        /// 安全写入：先写同目录临时文件并确保落盘，再用它替换目标文件。
        ///
        /// ★ 与旧实现的区别（旧实现是"写临时文件 → 删目标 → 移动临时文件"）：
        ///   旧实现最后一步一旦失败（跨卷、权限不足、目标被占用、杀软介入），
        ///   原主题文件已经被删掉了，主题就此"消失"；
        ///   新实现**从不先删除目标文件**，任何一步失败都保留原文件，
        ///   并把异常继续向上抛，调用方绝不会收到"保存成功"。
        /// </summary>
        private static void SafeWrite(string path, byte[] content)
        {
            // ★ 临时文件必须与目标同目录，否则最后一步会变成跨卷移动而失败
            string tmp = path + TempFileSuffix;

            try
            {
                // ① 写同目录临时文件并确保落盘
                TempFileWriter(tmp, content);

                // ② 用临时文件替换目标（不删除目标，也没有"删了再移"的退路）
                TargetReplacer(tmp, path);
            }
            catch
            {
                // 失败：清掉临时文件，原文件保持原样，异常继续向上抛
                TryDeleteFile(tmp);
                throw;
            }

            // 成功：临时文件理论上已经被替换掉，这里兜底清理残留
            TryDeleteFile(tmp);
        }

        /// <summary>
        /// 写临时文件：WriteThrough + Flush(true)，确保内容真正落盘后才去替换目标。
        /// </summary>
        private static void WriteTempFileCore(string tmpPath, byte[] content)
        {
            using (var fs = new FileStream(
                tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(true);
            }
        }

        /// <summary>
        /// 用同目录的临时文件替换目标文件。
        ///
        /// ★ 关键点：这里**从不先删除目标文件**，所以替换失败时目标文件原样还在。
        ///   - 目标已存在：File.Replace 在同卷内原子替换（保留目标文件的 ACL 等元数据）；
        ///   - 目标不存在：直接移动；
        ///   - 兜底路径仍然是"移动并覆盖"（MoveFileEx + REPLACE_EXISTING），同样不会先删目标。
        /// </summary>
        private static void ReplaceTargetCore(string tmpPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tmpPath, targetPath);
                return;
            }

            try
            {
                File.Replace(tmpPath, targetPath, null);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // 平台不支持 Replace，退到移动覆盖
            }
            catch (NotSupportedException)
            {
                // 文件系统不支持 Replace，退到移动覆盖
            }
            catch (IOException replaceEx)
            {
                // 目标被占用 / 杀软介入 / 文件系统不支持替换时 Replace 会失败。
                // 退到"移动并覆盖"：依然是同卷原子替换，覆盖不到时异常继续向上抛。
                try
                {
                    File.Move(tmpPath, targetPath, overwrite: true);
                    return;
                }
                catch (Exception moveEx)
                {
                    throw new IOException(
                        "替换主题文件失败，原文件保持不变：" + targetPath,
                        new AggregateException(replaceEx, moveEx));
                }
            }

            File.Move(tmpPath, targetPath, overwrite: true);
        }

        /// <summary>
        /// 尽力删除文件；删除失败不掩盖原始异常。
        /// </summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 清理失败不应覆盖真正的失败原因
            }
        }
        /// <summary>Save the current in-memory configuration under a new ID without overwriting an existing theme.</summary>
        public static void SaveThemeAs(string name, Theme theme) => SaveThemeAs(Dir, name, theme);

        public static void SaveThemeAs(string dir, string name, Theme theme)
        {
            ValidateThemeName(name);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, name + ".json");
            string staging = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + TempFileSuffix);
            try
            {
                SafeWrite(staging, Utf8NoBom.GetBytes(JsonSerializer.Serialize(theme, ThemeManager.CreateSerializerOptions())));
                File.Move(staging, target, overwrite: false);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        public static void ValidateThemeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
                name is "." or "..")
                throw new ArgumentException("Invalid theme name.", nameof(name));
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new ArgumentException("Reserved Windows file name.", nameof(name));
        }

    }
}
