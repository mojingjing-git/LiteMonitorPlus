using System;
using System.IO;
using System.Text;
using System.Text.Json;
using LiteMonitor.src.Core;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>R3/F02：主题预检必须如实报告缺失/坏 JSON/有效文件；Inspect 不得修改运行时 Current。</summary>
    public class R3ThemeLoadContractTests : IDisposable
    {
        private readonly string _dir;

        public R3ThemeLoadContractTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-r3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void F02_缺失文件_预检失败且不修改Current()
        {
            Theme before = ThemeManager.Current;
            ThemeManager.ThemeLoadResult result = ThemeManager.Inspect(
                Path.Combine(_dir, "missing.json"), "missing");

            Assert.False(result.Success);
            Assert.True(result.UsedDefault);
            Assert.Null(result.Theme);
            Assert.Same(before, ThemeManager.Current);
        }

        [Fact]
        public void F02_坏JSON_预检失败且不修改Current()
        {
            string path = Path.Combine(_dir, "bad.json");
            File.WriteAllText(path, "{ not valid json", new UTF8Encoding(false));
            Theme before = ThemeManager.Current;

            ThemeManager.ThemeLoadResult result = ThemeManager.Inspect(path, "bad");

            Assert.False(result.Success);
            Assert.True(result.UsedDefault);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Same(before, ThemeManager.Current);
        }

        [Fact]
        public void F02_同名主题修改后摘要变化且不修改Current()
        {
            string path = Path.Combine(_dir, "same-name.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Theme { Name = "Same", Version = 1 }), new UTF8Encoding(false));
            Theme before = ThemeManager.Current;

            ThemeManager.ThemeLoadResult first = ThemeManager.Inspect(path, "same-name");
            File.WriteAllText(path, JsonSerializer.Serialize(new Theme { Name = "Same", Version = 2 }), new UTF8Encoding(false));
            ThemeManager.ThemeLoadResult second = ThemeManager.Inspect(path, "same-name");

            Assert.True(first.Success, first.Error);
            Assert.True(second.Success, second.Error);
            Assert.NotEqual(first.SourceHash, second.SourceHash);
            Assert.Same(before, ThemeManager.Current);
        }

        [Fact]
        public void F02_有效文件_返回源摘要与主题对象()
        {
            string path = Path.Combine(_dir, "good.json");
            var theme = new Theme { Name = "R3-Good", Version = 1 };
            File.WriteAllText(path, JsonSerializer.Serialize(theme), new UTF8Encoding(false));

            ThemeManager.ThemeLoadResult result = ThemeManager.Inspect(path, "good");

            Assert.True(result.Success, result.Error);
            Assert.False(result.UsedDefault);
            Assert.NotNull(result.Theme);
            Assert.Equal("R3-Good", result.Theme!.Name);
            Assert.False(string.IsNullOrWhiteSpace(result.SourceHash));
            Assert.Equal(64, result.SourceHash!.Length);
        }

        [Fact]
        public void C02_同名不同摘要视为已提交_相同摘要不视为提交()
        {
            var oldTheme = new Theme { Name = "same-name" };
            var newTheme = new Theme { Name = "same-name" };
            var before = new ThemeManager.ThemeLoadResult(
                "same-name", @"C:\themes\same-name.json", "AAAA", oldTheme, true, false, null);
            var afterDifferent = before with { SourceHash = "BBBB", Theme = newTheme };
            var afterSame = before with { Theme = oldTheme };

            Assert.True(LiteMonitor.src.Core.Actions.AppActions.HasThemeCommitted(
                before, afterDifferent, oldTheme, newTheme));
            Assert.False(LiteMonitor.src.Core.Actions.AppActions.HasThemeCommitted(
                before, afterSame, oldTheme, oldTheme));
        }

        [Fact]
        public void F02_Settings写失败_不进入运行时且回滚Skin()
        {
            string themeName = "r3-save-blocked-" + Guid.NewGuid().ToString("N");
            string path = Path.Combine(ThemeManager.ThemeDir, themeName + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new Theme { Name = themeName, Version = 1 }), new UTF8Encoding(false));

            var cfg = new Settings { Skin = "original-theme" };
            bool priorBlock = SettingsHelper.GlobalBlockSave;
            try
            {
                SettingsHelper.GlobalBlockSave = true;
                var gateway = new ThemeStudioLauncher.AppActionsThemeApplyGateway(cfg, null!, null!);
                ThemeStudioForm.ThemeApplyResult result = gateway.ApplySavedTheme(themeName, 7);

                Assert.False(result.Success);
                Assert.Equal(ThemeStudioForm.ThemeApplyStage.SettingsPersisted, result.Stage);
                Assert.Equal("original-theme", cfg.Skin);
                Assert.Contains("配置持久化失败", result.Error);
            }
            finally
            {
                SettingsHelper.GlobalBlockSave = priorBlock;
                try { File.Delete(path); } catch { }
            }
        }
    }
}
