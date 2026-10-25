using System;
using System.IO;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// E05 接线护栏（源码级，同 WebSamplingDemandTests/R5 先例）：
    /// SettingsForm 构造需要 UIController（含采集器），单测不可实例化——
    /// 以源码断言钉住"引擎已接入用户路径"的每个必须动作，防接线被静默拆除。
    /// 状态机行为由 f14b 既有 52 条覆盖；真实界面验收单列（见 E05 报告）。
    /// </summary>
    public class E05ThemeWiringGuardTests
    {
        private static string FormCode() => File.ReadAllText(RepoFile("src", "UI", "SettingsForm.cs"));
        private static string PageCode() => File.ReadAllText(RepoFile("src", "UI", "Settings", "MainPanelPage.cs"));

        [Fact]
        public void 构建UI前_初始化主题状态机()
        {
            string code = FormCode();
            int init = code.IndexOf("SettingsThemeManager.Initialize(_cfg.SettingThemeMode);", StringComparison.Ordinal);
            int build = code.IndexOf("InitializeComponent();", StringComparison.Ordinal);
            Assert.True(init > 0 && build > init, "主题状态机必须在 InitializeComponent 之前初始化（避免先浅后深闪烁）");
        }

        [Fact]
        public void 主题变更_双层重绘_且跨线程调度()
        {
            string code = FormCode();
            Assert.Contains("SettingsThemeManager.ThemeChanged += OnSettingsThemeChanged", code);
            Assert.Contains("SettingsThemeManager.ApplyTo(this);", code);        // 全局取色表推送
            Assert.Contains("SettingsThemeApplier.Apply(this);", code);          // 槽位重映射兜底
            Assert.Contains("if (InvokeRequired) { BeginInvoke(OnSettingsThemeChanged); return; }", code);
        }

        [Fact]
        public void 保存提交_取消还原_退出注销()
        {
            string code = FormCode();
            Assert.Contains("SettingsThemeManager.SyncFromSettings(_cfg);", code); // 保存落定
            Assert.Contains("SettingsThemeManager.CancelPreview();", code);        // 取消/关闭还原预览
            Assert.Contains("SettingsThemeManager.ThemeChanged -= OnSettingsThemeChanged", code);
            Assert.Contains("Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged", code);
        }

        [Fact]
        public void 系统配色_只走General且带释放防护()
        {
            string code = FormCode();
            Assert.Contains("UserPreferenceCategory.General", code);
            Assert.Contains("if (IsDisposed || !IsHandleCreated) return;", code);  // 迟到消息不触已释放句柄
        }

        [Fact]
        public void 页面选择器_即写草稿并即时预览()
        {
            string code = PageCode();
            Assert.Contains("Menu.SettingsThemeMode", code);
            Assert.Contains("Config.SettingThemeMode = (SettingThemeMode)idx;", code); // 写草稿（立即绑定）
            Assert.Contains("SettingsThemeManager.BeginPreview((SettingThemeMode)idx)", code); // 即时预览
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }
    }
}
