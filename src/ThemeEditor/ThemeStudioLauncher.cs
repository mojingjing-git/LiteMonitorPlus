using System;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;

namespace LiteMonitor.ThemeEditor;

/// <summary>
/// 主题工作室入口（重做计划 §8 接线）：
/// 从菜单注入打开与应用依赖。应用路径复用现有 <see cref="AppActions.ApplyThemeAndLayout"/>，
/// 不反射 MainForm 私有字段，不另建设置写入路径。
///
/// 过渡期（计划 §11）：Shift+点击菜单项仍打开旧编辑器，供内部对照与回滚。
/// </summary>
public static class ThemeStudioLauncher
{
    public static void Open(MainForm form, Settings cfg, UIController? ui)
    {
        var gateway = new AppActionsThemeApplyGateway(cfg, ui, form);
        var studio = new ThemeStudioForm(gateway, cfg);
        studio.Show(form);
    }

    /// <summary>主窗体应用网关：保存已由工作室完成，这里只负责让主界面生效。</summary>
    private sealed class AppActionsThemeApplyGateway : ThemeStudioForm.IThemeApplyGateway
    {
        private readonly Settings _cfg;
        private readonly UIController? _ui;
        private readonly MainForm _form;

        public AppActionsThemeApplyGateway(Settings cfg, UIController? ui, MainForm form)
        {
            _cfg = cfg;
            _ui = ui;
            _form = form;
        }

        public bool ApplySavedTheme(string themeName)
        {
            _cfg.Skin = themeName;
            _cfg.Save();
            // 与菜单换肤、设置页应用共用同一条编排；GPU/legacy 过渡由协调器推进。
            AppActions.ApplyThemeAndLayout(_cfg, _ui, _form);
            return string.Equals(_cfg.Skin, themeName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
