using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 主题文件保存可靠性测试（外观改造计划 A02 / v4 §2 P1、§7.1）。
    ///
    /// ★ 写入位置：全部用例只在**系统临时目录**下的独立子目录里读写。
    ///   既不碰仓库里的 resources/themes 内置主题，也不写用户当前配置目录。
    ///   另有一个用例直接对比默认主题目录（ThemeManager.ThemeDir）的前后快照，
    ///   证明测试自身不会改动内置主题。
    ///
    /// ★ 故障注入方式：替换 ThemeFileService 暴露的测试钩子
    ///   （TempFileWriter / TargetReplacer），在**真实的失败点**上抛异常，
    ///   而不是把整个保存流程 mock 掉。
    ///   - TempFileWriter 抛异常 = 临时文件写入失败；
    ///   - TargetReplacer 抛异常 = 替换/移动失败（此时临时文件已经写好，
    ///     正是旧实现"先删目标再移动"会丢文件的危险场景）。
    ///
    /// ★ 失败语义：SaveTheme 保持"成功即无异常、失败即抛异常"的既有约定，
    ///   调用方（ThemeEditorForm.SaveCurrentTheme）本来就是 try/catch 包着的，
    ///   因此"抛异常"就是与现有调用方兼容的"明确失败"。
    /// </summary>
    public sealed class ThemeSaveTests : IDisposable
    {
        /// <summary>每个用例一个独立临时目录，用例结束即删除。</summary>
        private readonly string _dir;

        public ThemeSaveTests()
        {
            _dir = Path.Combine(
                Path.GetTempPath(), "LiteMonitor.Tests", "ThemeSaveTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            // 还原测试钩子，避免污染其它用例
            ThemeFileService.ResetTestHooks();

            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论
            }
        }

        // ============================================================
        // 正常路径
        // ============================================================

        [Fact]
        public void SaveTheme_正常保存_写入新内容且不残留临时文件()
        {
            ThemeFileService.SaveTheme(_dir, "Fresh", NewTheme("Fresh"));

            string path = Path.Combine(_dir, "Fresh.json");
            Assert.True(File.Exists(path));

            var loaded = ThemeFileService.LoadTheme(_dir, "Fresh");
            Assert.Equal("Fresh", loaded.Name);

            // 成功后目录里只应有主题文件本身，不能留下临时文件
            Assert.Empty(TempFiles());
            Assert.Equal(new[] { "Fresh.json" }, FileNames());
        }

        [Fact]
        public void SaveTheme_覆盖已有主题_内容被替换且不残留临时文件()
        {
            string path = Path.Combine(_dir, "Old.json");
            File.WriteAllText(path, "{\"Name\":\"Old\"}", new UTF8Encoding(false));

            ThemeFileService.SaveTheme(_dir, "Old", NewTheme("New"));

            Assert.Equal("New", ThemeFileService.LoadTheme(_dir, "Old").Name);
            Assert.Empty(TempFiles());
            Assert.Equal(new[] { "Old.json" }, FileNames());
        }

        [Fact]
        public void SaveTheme_保存后原样加载_往返内容一致()
        {
            var theme = NewTheme("RoundTrip");
            theme.Version = 4;

            ThemeFileService.SaveTheme(_dir, "RoundTrip", theme);
            var loaded = ThemeFileService.LoadTheme(_dir, "RoundTrip");

            Assert.Equal("RoundTrip", loaded.Name);
            Assert.Equal(4, loaded.Version);
            Assert.Empty(TempFiles());
        }

        // ============================================================
        // 故障注入：临时文件写入失败
        // ============================================================

        [Fact]
        public void SaveTheme_临时文件写入失败_抛异常且原文件逐字节不变()
        {
            string path = Path.Combine(_dir, "Old.json");
            File.WriteAllText(path, "{\"Name\":\"Old\"}", new UTF8Encoding(false));
            string hashBefore = Sha256OfFile(path);

            // 注入"临时文件写入失败"
            ThemeFileService.TempFileWriter = (tmp, content) =>
                throw new IOException("注入：临时文件写入失败");

            var ex = Assert.Throws<IOException>(
                () => ThemeFileService.SaveTheme(_dir, "Old", NewTheme("New")));
            Assert.Contains("注入", ex.Message);

            // ★ 原文件必须逐字节不变
            Assert.Equal(hashBefore, Sha256OfFile(path));
            Assert.Equal("{\"Name\":\"Old\"}", File.ReadAllText(path));
        }

        [Fact]
        public void SaveTheme_临时文件写入失败_不产生任何残留文件()
        {
            // 目标本来就不存在：失败后也不该留下半个文件或临时文件
            ThemeFileService.TempFileWriter = (tmp, content) =>
                throw new IOException("注入：临时文件写入失败");

            Assert.Throws<IOException>(
                () => ThemeFileService.SaveTheme(_dir, "Ghost", NewTheme("Ghost")));

            Assert.Empty(TempFiles());
            Assert.Empty(FileNames());
        }

        // ============================================================
        // 故障注入：替换 / 移动失败（最危险的场景）
        // ============================================================

        [Fact]
        public void SaveTheme_替换失败_抛异常且原文件逐字节不变()
        {
            string path = Path.Combine(_dir, "Old.json");
            File.WriteAllText(path, "{\"Name\":\"Old\"}", new UTF8Encoding(false));
            string hashBefore = Sha256OfFile(path);

            // 注入"替换失败"：此时临时文件已经写好，正是旧实现会丢原文件的场景
            string observedTmp = null;
            bool tmpExistedWhenFailed = false;
            ThemeFileService.TargetReplacer = (tmp, target) =>
            {
                observedTmp = tmp;
                tmpExistedWhenFailed = File.Exists(tmp);
                throw new IOException("注入：替换失败");
            };

            var ex = Assert.Throws<IOException>(
                () => ThemeFileService.SaveTheme(_dir, "Old", NewTheme("New")));
            Assert.Contains("注入", ex.Message);

            // ★ 原文件必须逐字节不变
            Assert.Equal(hashBefore, Sha256OfFile(path));
            Assert.Equal("{\"Name\":\"Old\"}", File.ReadAllText(path));

            // 失败点确实落在"替换"这一步：临时文件当时已经写好了
            Assert.True(tmpExistedWhenFailed, "替换失败前临时文件应已写入，说明失败点正确");
        }

        [Fact]
        public void SaveTheme_替换失败_临时文件与目标同目录且可辨识()
        {
            string observedTmp = null;
            ThemeFileService.TargetReplacer = (tmp, target) =>
            {
                observedTmp = tmp;
                throw new IOException("注入：替换失败");
            };

            Assert.Throws<IOException>(
                () => ThemeFileService.SaveTheme(_dir, "Old", NewTheme("New")));

            Assert.NotNull(observedTmp);

            // ★ 临时文件必须与目标同目录，否则最后一步会变成跨卷移动而失败
            Assert.Equal(
                Path.GetFullPath(_dir),
                Path.GetFullPath(Path.GetDirectoryName(observedTmp)));

            // ★ 命名可辨识：带专属后缀，且不会被当成 .json 主题
            Assert.EndsWith(ThemeFileService.TempFileSuffix, observedTmp);
            Assert.NotEqual(".json", Path.GetExtension(observedTmp));
        }

        [Fact]
        public void SaveTheme_替换失败_清理临时文件不残留()
        {
            string path = Path.Combine(_dir, "Old.json");
            File.WriteAllText(path, "{\"Name\":\"Old\"}", new UTF8Encoding(false));

            ThemeFileService.TargetReplacer = (tmp, target) =>
                throw new IOException("注入：替换失败");

            Assert.Throws<IOException>(
                () => ThemeFileService.SaveTheme(_dir, "Old", NewTheme("New")));

            Assert.Empty(TempFiles());
            Assert.Equal(new[] { "Old.json" }, FileNames());
        }

        [Fact]
        public void SaveTheme_替换失败后_原主题仍可正常加载()
        {
            string path = Path.Combine(_dir, "Old.json");
            File.WriteAllText(path, "{\"Name\":\"Old\",\"Version\":3}", new UTF8Encoding(false));

            ThemeFileService.TargetReplacer = (tmp, target) =>
                throw new IOException("注入：替换失败");

            Assert.Throws<IOException>(
                () => ThemeFileService.SaveTheme(_dir, "Old", NewTheme("New")));

            // 失败不能把主题变成读不出来的坏文件
            var loaded = ThemeFileService.LoadTheme(_dir, "Old");
            Assert.Equal("Old", loaded.Name);
        }

        // ============================================================
        // 隔离性：测试不碰内置主题目录
        // ============================================================

        [Fact]
        public void 保存失败_默认主题目录零变化()
        {
            // 默认主题目录 = 运行时 resources/themes（内置主题所在处）
            var before = SnapshotDir(ThemeManager.ThemeDir);

            ThemeFileService.TempFileWriter = (tmp, content) =>
                throw new IOException("注入：临时文件写入失败");
            Assert.Throws<IOException>(
                () => ThemeFileService.SaveTheme(_dir, "X", NewTheme("X")));

            var after = SnapshotDir(ThemeManager.ThemeDir);
            Assert.Equal(before, after);
        }

        [Fact]
        public void 保存成功_默认主题目录零变化()
        {
            var before = SnapshotDir(ThemeManager.ThemeDir);

            ThemeFileService.SaveTheme(_dir, "X", NewTheme("X"));

            var after = SnapshotDir(ThemeManager.ThemeDir);
            Assert.Equal(before, after);
        }

        // ============================================================
        // 临时文件残留清理
        // ============================================================

        [Fact]
        public void ListThemes_残留临时文件不会被当成主题()
        {
            ThemeFileService.SaveTheme(_dir, "Real", NewTheme("Real"));

            // 模拟异常中断留下的临时文件
            File.WriteAllText(
                Path.Combine(_dir, "Real.json" + ThemeFileService.TempFileSuffix),
                "{}", new UTF8Encoding(false));

            Assert.Equal(new[] { "Real" }, ThemeFileService.ListThemes(_dir));
        }

        [Fact]
        public void CleanupTempFiles_只删除过期临时文件()
        {
            ThemeFileService.SaveTheme(_dir, "Real", NewTheme("Real"));

            string stale = Path.Combine(_dir, "Real.json" + ThemeFileService.TempFileSuffix);
            string fresh = Path.Combine(_dir, "Fresh.json" + ThemeFileService.TempFileSuffix);
            File.WriteAllText(stale, "{}", new UTF8Encoding(false));
            File.WriteAllText(fresh, "{}", new UTF8Encoding(false));

            // 把 stale 的最后写入时间推到 1 小时前，模拟上次中断留下的垃圾
            File.SetLastWriteTime(stale, DateTime.Now.AddHours(-1));

            int removed = ThemeFileService.CleanupTempFiles(_dir);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh), "未过期的临时文件不应被清理");
            Assert.True(File.Exists(Path.Combine(_dir, "Real.json")), "正常主题文件不应被清理");
        }

        // ============================================================
        // 回归：新建 / 复制走安全写入后字节仍完全一致
        // ============================================================

        [Fact]
        public void CreateTheme_复制模板_字节完全一致()
        {
            string template = Path.Combine(_dir, "DarkFlat_Classic.json");
            byte[] templateBytes = Encoding.UTF8.GetBytes("{\"Name\":\"DarkFlat\",\"Version\":3}");
            File.WriteAllBytes(template, templateBytes);

            ThemeFileService.CreateTheme(_dir, "BrandNew");

            string created = Path.Combine(_dir, "BrandNew.json");
            Assert.True(File.Exists(created));
            Assert.Equal(templateBytes, File.ReadAllBytes(created));
            Assert.Empty(TempFiles());
        }

        [Fact]
        public void DuplicateTheme_复制主题_字节完全一致()
        {
            string src = Path.Combine(_dir, "Src.json");
            byte[] srcBytes = Encoding.UTF8.GetBytes("{\"Name\":\"Src\",\"Version\":3}");
            File.WriteAllBytes(src, srcBytes);

            ThemeFileService.DuplicateTheme(_dir, "Src", "Dst");

            string dst = Path.Combine(_dir, "Dst.json");
            Assert.True(File.Exists(dst));
            Assert.Equal(srcBytes, File.ReadAllBytes(dst));
            Assert.Empty(TempFiles());
        }

        // ============================================================
        // 辅助方法
        // ============================================================

        private static Theme NewTheme(string name)
        {
            return new Theme { Name = name, Version = 4 };
        }

        /// <summary>当前目录下的临时文件列表。</summary>
        private string[] TempFiles()
        {
            return Directory.GetFiles(_dir)
                .Where(f => f.EndsWith(ThemeFileService.TempFileSuffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        /// <summary>当前目录下的全部文件名（排序）。</summary>
        private string[] FileNames()
        {
            return Directory.GetFiles(_dir)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>文件的 SHA256（十六进制），用于逐字节比对。</summary>
        private static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return Convert.ToHexString(sha.ComputeHash(fs));
        }

        /// <summary>目录快照："文件名|SHA256" 列表，用于比对目录是否被改动。</summary>
        private static List<string> SnapshotDir(string dir)
        {
            var list = new List<string>();
            if (!Directory.Exists(dir))
                return list;

            foreach (string f in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
                list.Add(Path.GetFileName(f) + "|" + Sha256OfFile(f));

            return list;
        }
    }
}
