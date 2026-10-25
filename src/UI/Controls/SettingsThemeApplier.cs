using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.SettingsPage;

namespace LiteMonitor.src.UI.Controls
{
    /// <summary>
    /// 需要在深浅切换时重新取色的控件。
    ///
    /// <para>★ 为什么需要它：大部分控件的色存在 <c>BackColor</c>/<c>ForeColor</c> 字段里，
    ///   可以被 <see cref="SettingsThemeApplier"/> 的"色值重映射"顺手刷新；
    ///   但有一类控件的颜色是<b>算出来的</b>（组合自多个槽位、或在子控件上、或在 paint 里按状态取），
    ///   静态字段里没有那个值可以映射 —— 这类必须自己申明"我会重新取色"。</para>
    /// </summary>
    public interface IThemeRefreshable
    {
        /// <summary>按当前 <see cref="UIColors"/> 重新落到自身（含子控件）。</summary>
        void ApplyTheme();
    }

    /// <summary>
    /// 把主题配色<b>刷进既有控件树</b>（F14b 第 2 步）。
    ///
    /// <para>★ 存在的理由：修复前的 <see cref="UIColors"/> 只有一份写死的浅色，
    ///   于是「把页面背景改成深色」只能停在**半成品** —— 输入框、下拉框、提示行、导航按钮
    ///   在构造时就各自 <c>BackColor = Color.White</c> 之类的硬编码抓了一手，
    ///   页面上留下成片的白底黑字。这就是派发单里点名"不以只改页面背景色认定完成"的那个坑。</para>
    ///
    /// <para>★ 做法分两层：</para>
    /// <list type="number">
    /// <item>控件<b>自己会重新取色</b>的（实现 <see cref="IThemeRefreshable"/>）⇒ 调它的 <c>ApplyTheme</c>。</item>
    /// <item>其余控件 ⇒ 按"语义槽位配对"重映射 <c>BackColor</c> / <c>ForeColor</c>：
    ///   若当前色等于<b>另一套配色</b>里的某个槽位，就换成<b>当前配色</b>的同名槽位。
    ///   只看当前值属于哪个语义槽位，**不用**记录控件是在哪套配色下构造的 ——
    ///   因此可重复调用、幂等，且对完全没改造过的历史控件同样有效。</item>
    /// </list>
    /// </summary>
    public static class SettingsThemeApplier
    {
        /// <summary>
        /// 槽位配对表：浅色值 ⇄ 深色值，逐<b>语义槽位</b>一一对应。
        /// <para>★ 顺序有意义：出现歧义（两套配色里某个色值恰好相同）时取靠前的一对，
        ///   因此把语义最强的（主/次文字、卡片/页面底色）排在前面。</para>
        /// </summary>
        private static readonly (string Role, int LightArgb, int DarkArgb)[] SlotPairs = BuildPairs();

        private static (string, int, int)[] BuildPairs()
        {
            var light = SettingsThemePalette.Light;
            var dark = SettingsThemePalette.Dark;

            return new (string, int, int)[]
            {
                ("MainBg", light.MainBg.ToArgb(), dark.MainBg.ToArgb()),
                ("SidebarBg", light.SidebarBg.ToArgb(), dark.SidebarBg.ToArgb()),
                ("CardBg", light.CardBg.ToArgb(), dark.CardBg.ToArgb()),
                ("SurfaceBg", light.SurfaceBg.ToArgb(), dark.SurfaceBg.ToArgb()),
                ("Border", light.Border.ToArgb(), dark.Border.ToArgb()),
                ("Primary", light.Primary.ToArgb(), dark.Primary.ToArgb()),
                ("PrimaryHover", light.PrimaryHover.ToArgb(), dark.PrimaryHover.ToArgb()),
                ("TextMain", light.TextMain.ToArgb(), dark.TextMain.ToArgb()),
                ("TextSub", light.TextSub.ToArgb(), dark.TextSub.ToArgb()),
                ("TextDisabled", light.TextDisabled.ToArgb(), dark.TextDisabled.ToArgb()),
                ("GroupHeader", light.GroupHeader.ToArgb(), dark.GroupHeader.ToArgb()),
                ("NavSelected", light.NavSelected.ToArgb(), dark.NavSelected.ToArgb()),
                ("NavHover", light.NavHover.ToArgb(), dark.NavHover.ToArgb()),
                ("Separator", light.Separator.ToArgb(), dark.Separator.ToArgb()),
                ("TextWarn", light.TextWarn.ToArgb(), dark.TextWarn.ToArgb()),
                ("TextCrit", light.TextCrit.ToArgb(), dark.TextCrit.ToArgb()),
                ("Hint", light.Hint.ToArgb(), dark.Hint.ToArgb()),
                ("InputBg", light.InputBg.ToArgb(), dark.InputBg.ToArgb()),
                ("InputText", light.InputText.ToArgb(), dark.InputText.ToArgb()),
                ("InputLine", light.InputLine.ToArgb(), dark.InputLine.ToArgb()),
                ("InputLineFocused", light.InputLineFocused.ToArgb(), dark.InputLineFocused.ToArgb()),
                ("MenuBg", light.MenuBg.ToArgb(), dark.MenuBg.ToArgb()),
                ("MenuText", light.MenuText.ToArgb(), dark.MenuText.ToArgb()),
                ("MenuSelectedBg", light.MenuSelectedBg.ToArgb(), dark.MenuSelectedBg.ToArgb()),
                ("MenuSelectedText", light.MenuSelectedText.ToArgb(), dark.MenuSelectedText.ToArgb()),
                ("PreviewBg", light.PreviewBg.ToArgb(), dark.PreviewBg.ToArgb()),
                ("BannerBg", light.BannerBg.ToArgb(), dark.BannerBg.ToArgb()),
                ("BannerText", light.BannerText.ToArgb(), dark.BannerText.ToArgb()),
            };
        }

        /// <summary>
        /// 把一个色值映射到目标配色的同义槽位。
        /// <para>★ 已经在目标配色里、或不属于任何槽位（用户自定义色，如 <see cref="LiteColorPicker"/>）
        ///   时返回原值 —— 不擅自改动来路不明的颜色。</para>
        /// </summary>
        public static Color Remap(Color color, bool dark)
        {
            int argb = color.ToArgb();
            if (color.A == 0) return color;                      // 透明：不参与主题

            foreach (var slot in SlotPairs)
            {
                int source = dark ? slot.LightArgb : slot.DarkArgb;
                int target = dark ? slot.DarkArgb : slot.LightArgb;
                if (argb == source) return Color.FromArgb(target);
            }

            return color;
        }

        /// <summary>该色是否属于某个主题槽位（测试/诊断用）。</summary>
        public static bool IsThemeColor(Color color)
        {
            int argb = color.ToArgb();
            foreach (var slot in SlotPairs)
            {
                if (argb == slot.LightArgb || argb == slot.DarkArgb) return true;
            }
            return false;
        }

        /// <summary>
        /// 把当前生效的配色刷进整棵控件树（幂等，可重复调用）。
        /// </summary>
        public static void Apply(Control root)
        {
            if (root == null) return;

            bool dark = UIColors.CurrentPalette?.IsDark ?? false;
            ApplyRecursive(root, dark, depth: 0);
        }

        private static void ApplyRecursive(Control control, bool dark, int depth)
        {
            // ★ 有界：控件树可能很深（设置页 + 嵌套容器），但也**不可能是无限的**。
            //   这里是防御性的上限 —— 万一将来出现循环父子关系，不能把 UI 线程卡死在递归里。
            const int MaxDepth = 64;
            if (control == null || control.IsDisposed || depth > MaxDepth) return;

            if (control is IThemeRefreshable refreshable)
            {
                refreshable.ApplyTheme();
            }
            else
            {
                ApplyColorPair(control, dark);
            }

            foreach (Control child in control.Controls)
            {
                ApplyRecursive(child, dark, depth + 1);
            }
        }

        private static void ApplyColorPair(Control control, bool dark)
        {
            SetBackColor(control, dark);
            SetForeColor(control, dark);

            // ★ ComboBox 的下拉列表底色/文字不属于 Control.BackColor 的常规路径：
            //   DropDownStyle=DropDownList 时列表部分由系统绘制，
            //   但本文所述的所有输入框都是 ToolStrip 无关的原生 ComboBox
            //   —— 其 BackColor 生效范围是编辑框 + 列表项，覆盖不到"选中项高亮"。
            //   选中项高亮在 Windows 上跟随系统高亮色，**不走** BackColor，
            //   因此这里不假装能改它（留待后续若引入自绘下拉列表再处理，见验收记录"未覆盖"）。
        }

        private static void SetBackColor(Control control, bool dark)
        {
            Color current = control.BackColor;
            if (current.A == 0) return;                          // Transparent 不参与

            Color mapped = Remap(current, dark);
            if (mapped.ToArgb() != current.ToArgb())
            {
                control.BackColor = mapped;
            }
        }

        private static void SetForeColor(Control control, bool dark)
        {
            Color mapped = Remap(control.ForeColor, dark);
            if (mapped.ToArgb() != control.ForeColor.ToArgb())
            {
                control.ForeColor = mapped;
            }
        }
    }
}
