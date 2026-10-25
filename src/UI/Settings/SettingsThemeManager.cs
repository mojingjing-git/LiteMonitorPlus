using System;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 设置窗体明暗主题的运行时状态机（F14b / issue #437 暗色半项）。
    ///
    /// <para>★ 职责边界：<b>只管设置窗体自身的外观</b>，不碰悬浮窗皮肤
    ///   （<c>Settings.Skin</c> / <c>ThemeManager</c> / <c>resources/themes/*.json</c>）。
    ///   两套体系各自独立 —— 详见 <see cref="SettingThemeMode"/> 的注释。</para>
    ///
    /// <para>三个概念必须分清，混了就会出现"取消之后界面却已经变了色"这一类 bug：</para>
    /// <list type="number">
    /// <item><b>已提交模式</b>（<see cref="CommittedMode"/>）：来自 <c>Settings.SettingThemeMode</c>，
    ///   只有点了「确定 / 应用」并且 Draft-Commit 成功之后才会变。</item>
    /// <item><b>预览模式</b>（<see cref="PreviewMode"/>）：用户在下拉框里改了但还没保存的值。
    ///   <c>null</c> 表示没有待应用的改动；不为 null 时<b>界面立刻跟着它变</b>（即时预览），
    ///   但<b>配置对象一个字节都不动</b>。</item>
    /// <item><b>当前生效模式</b>（<see cref="EffectiveMode"/>）：预览有值用预览，否则用已提交值 ——
    ///   界面绘制只读这一个。</item>
    /// </list>
    /// <para>★ Draft-Commit 的"取消回退"因此非常简单：把预览置回 <c>null</c> 再重绘即可，
    ///   不需要保存/恢复任何颜色快照。</para>
    /// </summary>
    internal static class SettingsThemeManager
    {
        private static readonly object Gate = new object();

        /// <summary>系统"应用颜色"的最近一次已知值（<c>null</c> = 还没探测过）。</summary>
        private static bool? _systemUsesLightTheme;

        /// <summary>已提交的模式。</summary>
        private static SettingThemeMode _committedMode = SettingThemeMode.System;

        /// <summary>待应用的预览模式（<c>null</c> = 无待应用改动）。</summary>
        private static SettingThemeMode? _previewMode;

        /// <summary>已提交的模式 —— 只有 Draft-Commit 成功才变。</summary>
        internal static SettingThemeMode CommittedMode
        {
            get { lock (Gate) { return _committedMode; } }
        }

        /// <summary>待应用的预览模式（<c>null</c> = 无待应用改动）。</summary>
        internal static SettingThemeMode? PreviewMode
        {
            get { lock (Gate) { return _previewMode; } }
        }

        /// <summary>是否有尚未保存的预览改动。</summary>
        internal static bool HasPendingPreview
        {
            get { lock (Gate) { return _previewMode.HasValue; } }
        }

        /// <summary>
        /// 当前<b>应当呈现</b>的模式：预览优先，否则已提交值。
        /// <para>★ 界面绘制只读这个 —— 它保证"下拉框一改立刻可见"（即时预览），
        ///   同时保证"取消之后回到原来的样子"。</para>
        /// </summary>
        internal static SettingThemeMode EffectiveMode
        {
            get
            {
                lock (Gate)
                {
                    return _previewMode ?? _committedMode;
                }
            }
        }

        /// <summary>
        /// <see cref="EffectiveMode"/> 解析之后是不是深色（"跟随系统"在此收敛成二值）。
        /// <para>★ 界面绘制只看这个 bool —— 模式可以有三种，屏幕上的像素只有两套。</para>
        /// </summary>
        internal static bool IsDarkNow => IsModeDark(EffectiveMode);

        /// <summary>当前生效的那份配色。</summary>
        internal static SettingsThemePalette CurrentPalette => SettingsThemePalette.FromDarkFlag(IsDarkNow);

        /// <summary>模式变更（含预览切换与系统配色跟进）时触发，订阅者是设置窗体本体。</summary>
        internal static event EventHandler? ThemeChanged;

        /// <summary>用已提交的模式初始化（清空任何残留预览）。</summary>
        internal static void Initialize(SettingThemeMode committedMode)
        {
            lock (Gate)
            {
                _committedMode = committedMode;
                _previewMode = null;
            }
        }

        /// <summary>
        /// 把 <c>Settings</c> 里的已提交值同步进来（启动、外部改动、Commit 之后都可调）。
        /// <para>★ 不触发 <see cref="ThemeChanged"/> —— 需要重绘时由调用方显式刷一次，
        ///   避免"同步"这种纯数据操作顺带引发重绘风暴。</para>
        /// </summary>
        internal static void SyncFromSettings(LiteMonitor.Settings settings)
        {
            if (settings == null) return;
            lock (Gate)
            {
                _committedMode = settings.SettingThemeMode;
                _previewMode = null;
            }
        }

        /// <summary>
        /// 用户在下拉框里改了值：进入即时预览。
        /// <para>★ 只更新"预览"，<b>不动配置对象</b> —— 未保存不生效。</para>
        /// </summary>
        /// <returns>是否真的变了（变了才值得重绘）。</returns>
        internal static bool BeginPreview(SettingThemeMode mode)
        {
            bool changed;
            lock (Gate)
            {
                changed = _previewMode != mode;
                _previewMode = mode;
            }

            if (changed) NotifyThemeChanged();
            return changed;
        }

        /// <summary>
        /// 取消：丢弃预览，回到已提交的样子。
        /// <para>★ Draft-Commit 语义的另一半 —— 取消必须真的回到<b>视觉上</b>原来的状态，
        ///   不能只是把配置对象丢掉、而界面已经偷偷变了色。</para>
        /// </summary>
        internal static void CancelPreview()
        {
            bool hadPreview;
            lock (Gate)
            {
                hadPreview = _previewMode.HasValue;
                _previewMode = null;
            }

            if (hadPreview) NotifyThemeChanged();
        }

        /// <summary>
        /// 提交：预览落成为已提交值。
        /// <para>★ 只改<b>这个静态状态机</b>；配置对象 <c>Settings.SettingThemeMode</c>
        ///   由页面自己的回写（立即绑定）写入 —— 这里不越俎代庖，避免出现两个写入者。</para>
        /// </summary>
        internal static void CommitPreview()
        {
            bool changed;
            lock (Gate)
            {
                SettingThemeMode target = _previewMode ?? _committedMode;
                changed = target != _committedMode;
                _committedMode = target;
                _previewMode = null;
            }

            if (changed) NotifyThemeChanged();
        }

        /// <summary>通知订阅者：去看 <see cref="EffectiveMode"/> 重绘。</summary>
        internal static void NotifyThemeChanged()
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// 系统配色变了（用户在 Windows 设置里切了"应用颜色"）。
        /// <para>★ 只有处于"跟随系统"模式时才重绘 —— 显式选了浅色/深色的用户
        ///   不该被系统配色翻转打搅，这正是三态的意义。</para>
        /// </summary>
        /// <returns>界面是否需要重绘。</returns>
        internal static bool ApplySystemColorChange(bool systemUsesLightTheme)
        {
            bool changed;
            lock (Gate)
            {
                changed = _systemUsesLightTheme != systemUsesLightTheme;
                _systemUsesLightTheme = systemUsesLightTheme;
            }

            bool needsRedraw = changed && EffectiveMode == SettingThemeMode.System;
            if (needsRedraw) NotifyThemeChanged();
            return needsRedraw;
        }

        /// <summary>
        /// 探测 Windows 的"应用颜色"设置。
        /// <para>★ 为什么读注册表而不是 <c>ShouldAppsUseDarkMode</c>：后者是无文档的
        ///   undocumented API，签名与序号随时可能变；注册表键 <c>AppsUseLightTheme</c>
        ///   是自 Windows 10 1809 起稳定存在的偏好项（个人化 → 颜色 → 应用模式）。</para>
        /// <para>★ 读不到时返回 <c>null</c>（未知），由 <see cref="IsModeDark"/> 决定兜底策略 ——
        ///   保持修复前的行为（浅色），不因为一次注册表失败就把界面翻成黑的。</para>
        /// </summary>
        internal static bool? TryReadSystemUsesLightTheme()
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
                object? raw = key?.GetValue("AppsUseLightTheme");
                if (raw is int i) return i != 0;
                if (raw is string s && int.TryParse(s, out int parsed)) return parsed != 0;
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>某个模式在当下到底呈现为深色还是浅色。</summary>
        internal static bool IsModeDark(SettingThemeMode mode)
        {
            switch (mode)
            {
                case SettingThemeMode.Light: return false;
                case SettingThemeMode.Dark: return true;
                default:
                    // ★ "跟随系统"：**先读注入/缓存值，没有才去读注册表**。
                    //   测试用 OverrideSystemUsesLightThemeForTest 钉住系统配色，
                    //   而注册表路径会绕开那枚注入值 ⇒ 必须先查缓存。
                    return CachedSystemUsesLightTheme() == false;
            }
        }

        /// <summary>
        /// 系统"应用颜色"的当前已知值。
        /// <para>★ 优先级：<b>注入/缓存值</b> &gt; 注册表实时读取。
        ///   缓存存在的场景：① 测试注入；② 系统配色变更事件已投递过一次。
        ///   注册表只是"还没有任何缓存时"的兜底。</para>
        /// </summary>
        private static bool? CachedSystemUsesLightTheme()
        {
            lock (Gate)
            {
                if (_systemUsesLightTheme.HasValue) return _systemUsesLightTheme;
            }

            bool? probed = TryReadSystemUsesLightTheme();
            if (probed.HasValue)
            {
                lock (Gate)
                {
                    _systemUsesLightTheme = probed;
                }
            }
            return probed;
        }

        /// <summary>测试注入点：钉住系统配色，避免测试去改用户的 Windows 设置。</summary>
        internal static void OverrideSystemUsesLightThemeForTest(bool? usesLightTheme)
        {
            lock (Gate)
            {
                _systemUsesLightTheme = usesLightTheme;
            }
        }

        /// <summary>测试注入点：设置已知的起始状态（已提交 + 无预览）。</summary>
        internal static void SetCommittedForTest(SettingThemeMode mode)
        {
            lock (Gate)
            {
                _committedMode = mode;
                _previewMode = null;
            }
        }

        /// <summary>
        /// 把一份配色<b>推送</b>到 <see cref="UIColors"/> —— 供还没改造到位的既有控件继续取色。
        /// <para>★ 这是兼容层：新的 <see cref="SettingsThemePalette"/> 有 27 个语义槽位，
        ///   而 <see cref="UIColors"/> 原本只有 12 个静态字段。本包把缺失的那些
        ///   （输入框底色、菜单、禁用态、分隔线…）补进 <see cref="UIColors"/>，
        ///   历史控件在深色下至少不会退化成"白底黑字"。</para>
        /// </summary>
        internal static void PushToUIColors(SettingsThemePalette palette)
        {
            if (palette == null) return;

            UIColors.MainBg = palette.MainBg;
            UIColors.SidebarBg = palette.SidebarBg;
            UIColors.CardBg = palette.CardBg;
            UIColors.Border = palette.Border;
            UIColors.Primary = palette.Primary;
            UIColors.TextMain = palette.TextMain;
            UIColors.TextSub = palette.TextSub;
            UIColors.GroupHeader = palette.GroupHeader;
            UIColors.NavSelected = palette.NavSelected;
            UIColors.NavHover = palette.NavHover;
            UIColors.TextWarn = palette.TextWarn;
            UIColors.TextCrit = palette.TextCrit;

            // ★ 深色下必须是深/淡而不是默认的白/灰：这些正是"只改背景色"会漏掉的地方。
            UIColors.SurfaceBg = palette.SurfaceBg;
            UIColors.InputBg = palette.InputBg;
            UIColors.InputText = palette.InputText;
            UIColors.InputLine = palette.InputLine;
            UIColors.InputLineFocused = palette.InputLineFocused;
            UIColors.TextDisabled = palette.TextDisabled;
            UIColors.Separator = palette.Separator;
            UIColors.Hint = palette.Hint;
            UIColors.MenuBg = palette.MenuBg;
            UIColors.MenuText = palette.MenuText;
            UIColors.MenuSelectedBg = palette.MenuSelectedBg;
            UIColors.MenuSelectedText = palette.MenuSelectedText;
            UIColors.PreviewBg = palette.PreviewBg;
            UIColors.BannerBg = palette.BannerBg;
            UIColors.BannerText = palette.BannerText;
            UIColors.CurrentPalette = palette;
        }

        /// <summary>
        /// 按当前生效模式刷新全局取色表（并重画指定的根控件）。
        /// </summary>
        internal static void ApplyTo(Control root)
        {
            PushToUIColors(CurrentPalette);
            root?.Invalidate(true);
        }
    }
}
