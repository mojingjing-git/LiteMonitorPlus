namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 设置窗体的明暗模式（F14b / issue #437 的「暗色」半项）。
    ///
    /// <para>★ 与<b>悬浮窗皮肤体系</b>（<c>Settings.Skin</c> + <c>ThemeManager</c> +
    ///   <c>resources/themes/*.json</c>）是<b>两套互不相干的东西</b>，本包刻意不混为一谈：</para>
    /// <list type="bullet">
    /// <item><b>皮肤（Skin）</b>管的是桌面上那个悬浮面板长什么样：主题 JSON、GPU 材质/渐变、
    ///       字号配色、渲染路线 —— 取值是磁盘上的主题文件，由 <c>ThemeManager</c> 加载，
    ///       <b>与 Windows 明暗无关</b>（暗色皮肤在系统浅色模式下也是暗的）。</item>
    /// <item><b>明暗模式（本枚举）</b>管的是设置窗体自身的界面底色：背景/文字/边框/输入框/菜单/导航。
    ///       取值只有三个，<b>不读也不写任何主题文件</b>，不参与 GPU 渲染路线，不影响悬浮窗外观。</item>
    /// </list>
    /// <para>★ 为什么要分家：合成一个"主题"概念时，"选了暗色皮肤"会被误当成"界面也该变暗"，
    ///   反之亦然 —— 用户在市场页装了个亮色皮肤、设置窗体跟着变亮，这就是串味。</para>
    ///
    /// <para>★ 放在 <c>src/Core/</c> 而不是 UI 目录：<c>Settings</c> 自身要引用它，
    ///   若定义在 UI 命名空间下就会形成 Core→UI 的反向依赖（仓库现有代码里没有这种方向）。</para>
    ///
    /// <para>★ 迁移：枚举的 <c>0</c> 是 <see cref="System"/> ——
    ///   旧版 settings.json 没有这个字段时反序列化即得到 0 ⇒ 跟随系统，
    ///   界面观感与修复前逐字一致（修复前就是一套固定的浅色）。</para>
    /// </summary>
    public enum SettingThemeMode
    {
        /// <summary>跟随 Windows 的"应用颜色"设置（缺失/未知时的默认值，也是迁移后的既有配置值）。</summary>
        System = 0,

        /// <summary>始终浅色。</summary>
        Light = 1,

        /// <summary>始终深色。</summary>
        Dark = 2,
    }

    /// <summary>
    /// <see cref="SettingThemeMode"/> 的解析/校验工具。
    /// <para>★ 存在的理由：<c>settings.json</c> 是<b>用户可编辑</b>的文件。
    ///   一个手写的 <c>7</c> 或负数值不该让整个配置加载失败（那会把用户所有其它设置一起丢掉），
    ///   也不该被静默当成某个合法模式（那会让用户不知道自己配的东西没生效）。
    ///   统一收敛：越界值落到 <see cref="SettingThemeMode.System"/>。</para>
    /// </summary>
    public static class SettingThemeModes
    {
        /// <summary>是否为三个已定义的值之一。</summary>
        public static bool IsDefined(SettingThemeMode mode)
            => mode == SettingThemeMode.System
            || mode == SettingThemeMode.Light
            || mode == SettingThemeMode.Dark;

        /// <summary>
        /// 把任意整数夹到合法枚举上（越界 ⇒ <see cref="SettingThemeMode.System"/>）。
        /// <para>★ 用于反序列化哨兵与自定义 JSON 回退路径。</para>
        /// </summary>
        public static SettingThemeMode Coerce(int raw)
        {
            SettingThemeMode candidate = (SettingThemeMode)raw;
            return IsDefined(candidate) ? candidate : SettingThemeMode.System;
        }

        /// <summary>把可能非法的枚举值夹回合法值。</summary>
        public static SettingThemeMode Coerce(SettingThemeMode mode)
            => IsDefined(mode) ? mode : SettingThemeMode.System;
    }
}
