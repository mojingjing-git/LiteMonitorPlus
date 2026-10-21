using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// UI Builder Extension Methods
    /// Provides a fluent API for building settings UI
    /// </summary>
    public static class SettingsUIBuilder
    {
        // =============================================================
        //  Basic Inputs
        // =============================================================

        /// <summary>
        /// Adds a toggle switch (LiteCheck)
        /// </summary>
        public static LiteCheck AddToggle(this LiteSettingsGroup group, SettingsPageBase page, string titleKey, Func<bool> get, Action<bool> set)
        {
            var chk = new LiteCheck(get(), LanguageManager.T("Menu.Enable"));
            
            // Immediate binding: Update Draft on Change
            // ★ W3：抑制作用域内（程序化改控件）不回写 —— 否则"界面自己动了一下"
            //   就把用户的配置改了。
            chk.CheckedChanged += (s, e) => { if (!page.IsWriteBackSuppressed) set(chk.Checked); };
            page.RegisterRefresh(() => chk.Checked = get());
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), chk));
            return chk;
        }

        /// <summary>
        /// Adds a string input (LiteUnderlineInput)
        /// </summary>
        public static LiteUnderlineInput AddInput(this LiteSettingsGroup group, SettingsPageBase page, string titleKey, Func<string> get, Action<string> set, string placeholder = "", int width = 100, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var input = new LiteUnderlineInput(get(), "", "", width, null, align);
            if (!string.IsNullOrEmpty(placeholder)) input.Placeholder = placeholder;

            // Immediate binding
            input.Inner.TextChanged += (s, e) => { if (!page.IsWriteBackSuppressed) set(input.Inner.Text); };
            page.RegisterRefresh(() => input.Inner.Text = get());
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
            return input;
        }

        /// <summary>
        /// Adds an integer input
        /// </summary>
        public static LiteNumberInput AddInt(this LiteSettingsGroup group, SettingsPageBase page, string titleKey, string unit, Func<int> get, Action<int> set, int width = 60, Color? color = null)
        {
            var input = new LiteNumberInput(get().ToString(), unit, "", width, color);
            input.Padding = UIUtils.S(new Padding(0, 5, 0, 1)); // Fix vertical alignment

            // Immediate Binding: Update Model on TextChange
            input.Inner.TextChanged += (s, e) => {
                if (page.IsWriteBackSuppressed) return;
                if (int.TryParse(input.Inner.Text, out int val))
                    set(val);
            };

            page.RegisterRefresh(() => input.Inner.Text = get().ToString());
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
            return input;
        }

        /// <summary>
        /// Adds a double/float input
        /// </summary>
        public static LiteNumberInput AddDouble(this LiteSettingsGroup group, SettingsPageBase page, string titleKey, string unit, Func<double> get, Action<double> set, int width = 70)
        {
            var input = new LiteNumberInput(get().ToString(), unit, "", width);
            input.Padding = UIUtils.S(new Padding(0, 5, 0, 1));

            // Immediate binding
            input.Inner.TextChanged += (s, e) => {
                if (page.IsWriteBackSuppressed) return;
                if (double.TryParse(input.Inner.Text, out double val))
                    set(val);
            };
            page.RegisterRefresh(() => input.Inner.Text = get().ToString());
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
            return input;
        }

        /// <summary>
        /// Adds a color picker
        /// </summary>
        public static LiteColorInput AddColor(this LiteSettingsGroup group, SettingsPageBase page, string titleKey, Func<string> get, Action<string> set)
        {
            var input = new LiteColorInput(get());
            input.Input.Padding = UIUtils.S(new Padding(0, 5, 0, 1));

            // Immediate binding
            input.Input.Inner.TextChanged += (s, e) => { if (!page.IsWriteBackSuppressed) set(input.HexValue); };
            page.RegisterRefresh(() => input.HexValue = get());
            
            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), input));
            return input;
        }

        // =============================================================
        //  ComboBoxes
        // =============================================================

        /// <summary>
        /// Adds a ComboBox for a list of strings
        /// </summary>
        public static LiteComboBox AddCombo(this LiteSettingsGroup group, SettingsPageBase page, string titleKey, IEnumerable<string> items, Func<string> get, Action<string> set)
        {
            var cmb = new LiteComboBox();
            foreach (var i in items) cmb.Items.Add(i);
            
            // Initial selection
            string current = get();
            if (cmb.Items.Contains(current)) cmb.SelectedItem = current;
            else if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;

            // Immediate binding
            // ★ W3：① 抑制作用域内不回写；② 选中项为空（-1）时**不写** ——
            //   否则清空/重填选项时会把空串写进配置，用户的原值就此丢失。
            cmb.Inner.SelectedIndexChanged += (s, e) =>
            {
                if (page.IsWriteBackSuppressed) return;
                if (cmb.SelectedIndex < 0) return;
                set(cmb.Text);
            };
            page.RegisterRefresh(() => 
            {
                // ★ 刷新时选中项可能被迫落到第一项（"自动"）—— 那是**程序化**改动，
                //   绝不能当成用户的选择写回配置。
                using (page.BeginSuppressWriteBack())
                {
                    string current = get();
                    if (cmb.Items.Contains(current)) cmb.SelectedItem = current;
                    else if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
                }
            });
            
            // Auto-width logic
            AttachAutoWidth(cmb);

            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), cmb));
            return cmb;
        }

        /// <summary>
        /// Adds a ComboBox for an index-based selection
        /// </summary>
        public static LiteComboBox AddComboIndex(this LiteSettingsGroup group, SettingsPageBase page, string titleKey, IEnumerable<string> items, Func<int> get, Action<int> set)
        {
            var cmb = new LiteComboBox();
            foreach (var i in items) cmb.Items.Add(i);

            int idx = get();
            if (idx >= 0 && idx < cmb.Items.Count) cmb.SelectedIndex = idx;

            // 只保存有效选项，避免 ComboBox 初始化/刷新时的 -1 临时状态写坏配置
            cmb.Inner.SelectedIndexChanged += (s, e) => 
            {
                if (page.IsWriteBackSuppressed) return;
                int selectedIndex = cmb.SelectedIndex;
                if (selectedIndex >= 0 && selectedIndex < cmb.Items.Count)
                    set(selectedIndex);
            };
            page.RegisterRefresh(() => 
            {
                using (page.BeginSuppressWriteBack())
                {
                    int idx = get();
                    if (idx >= 0 && idx < cmb.Items.Count) cmb.SelectedIndex = idx;
                }
            });
            AttachAutoWidth(cmb);

            group.AddItem(new LiteSettingsItem(LanguageManager.T(titleKey), cmb));
            return cmb;
        }

        /// <summary>
        /// Adds a ComboBox for dynamic key-value pairs
        /// </summary>
        public static LiteComboBox AddComboPair(this LiteSettingsGroup group, SettingsPageBase page, string title, IEnumerable<dynamic> options, Func<string> get, Action<string> set)
        {
            var cmb = new LiteComboBox();
            foreach (var opt in options)
            {
                string label = "";
                string val = "";
                
                // Reflection to get Label/Value
                Type t = opt.GetType();
                var pLabel = t.GetProperty("Label");
                var pValue = t.GetProperty("Value");
                
                if (pLabel != null) label = pLabel.GetValue(opt)?.ToString();
                if (pValue != null) val = pValue.GetValue(opt)?.ToString();
                
                cmb.AddItem(label, val);
            }

            cmb.SelectValue(get());
            // Immediate binding
            cmb.Inner.SelectedIndexChanged += (s, e) => { if (!page.IsWriteBackSuppressed) set(cmb.SelectedValue); };
            page.RegisterRefresh(() =>
            {
                // ★ 刷新时 SelectValue 找不到原值会落到第一项（"自动"）——
                //   那是程序化改动，不能写回配置把原值冲掉。
                using (page.BeginSuppressWriteBack())
                {
                    cmb.SelectValue(get());
                }
            });
            AttachAutoWidth(cmb);

            group.AddItem(new LiteSettingsItem(title, cmb));
            return cmb;
        }

        private static void AttachAutoWidth(LiteComboBox cmb)
        {
            cmb.Inner.DropDown += (s, e) =>
            {
                var box = (ComboBox)s;
                int maxWidth = box.Width;
                int scrollBarWidth = SystemInformation.VerticalScrollBarWidth;

                foreach (var item in box.Items)
                {
                    if (item == null) continue;
                    int w = TextRenderer.MeasureText(item.ToString(), box.Font).Width + scrollBarWidth + 10;
                    if (w > maxWidth) maxWidth = w;
                }
                box.DropDownWidth = maxWidth;
            };
        }

        // =============================================================
        //  Advanced / Composite Rows
        // =============================================================

        /// <summary>
        /// Adds a specialized Action Row (Label + Control on Right)
        /// </summary>
        public static LiteActionRow AddAction(this LiteSettingsGroup group, string title, Control rightControl)
        {
            var row = new LiteActionRow(title, rightControl);
            group.AddFullItem(row);
            return row;
        }

        /// <summary>
        /// Shortcut for Action Row with a Button
        /// </summary>
        public static LiteActionRow AddButton(this LiteSettingsGroup group, string title, string btnText, Action onClick, bool isPrimary = false)
        {
            var btn = new LiteButton(btnText, isPrimary);
            btn.Click += (s, e) => onClick?.Invoke();
            return group.AddAction(title, btn);
        }

        /// <summary>
        /// Shortcut for Action Row with a Link
        /// </summary>
        public static LiteActionRow AddLink(this LiteSettingsGroup group, string title, string linkText, Action onClick)
        {
            var link = new LiteLink(linkText, onClick);
            return group.AddAction(title, link);
        }

        /// <summary>
        /// Adds a Threshold Row (Warn -> Crit)
        /// <para>★ <paramref name="clamp"/>：该指标类型的取值范围钳制（可为 null）。
        ///   通用层**不猜**各类型的上下限 —— 由页面（ThresholdPage）按指标类型提供。
        ///   钳制结果会带**行内提示**，不会静默改值。</para>
        /// </summary>
        public static LiteThresholdRow AddThreshold(this LiteSettingsGroup group, SettingsPageBase page, string title, string unit, ValueRange range, LiteThresholdRow.ThresholdClamp? clamp = null)
        {
            var row = new LiteThresholdRow(page, title, unit, range, clamp);
            group.AddFullItem(row);
            return row;
        }

        /// <summary>
        /// Adds a Hint/Note Row
        /// </summary>
        public static LiteHintRow AddHint(this LiteSettingsGroup group, string text, int indent = 0)
        {
            var row = new LiteHintRow(text, indent);
            group.AddFullItem(row);
            return row;
        }
    }
}
