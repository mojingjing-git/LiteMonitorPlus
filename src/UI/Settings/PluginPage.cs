using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor;
using System.Diagnostics;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.UI.Controls;

namespace LiteMonitor.src.UI.SettingsPage
{
    public class PluginPage : SettingsPageBase
    {
        private Panel _container;
        private Dictionary<string, LiteCheck> _toggles = new Dictionary<string, LiteCheck>();
        // Track modified instances for batch restart on Save
        private HashSet<string> _modifiedInstanceIds = new HashSet<string>();

        // [Fix] Custom Panel without WS_EX_COMPOSITED to prevent "Unable to set Win32 parent" crash
        // when dynamically toggling visibility of deeply nested controls.
        private class SafeBufferedPanel : Panel
        {
            public SafeBufferedPanel()
            {
                this.DoubleBuffered = true;
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                this.UpdateStyles();
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0014) // WM_ERASEBKGND
                {
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        public PluginPage()
        {
            this.BackColor = UIColors.MainBg;
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(0);

            _container = new SafeBufferedPanel 
            { 
                Dock = DockStyle.Fill, 
                AutoScroll = true, 
                Padding = new Padding(20, 5, 20, 20) // 减少顶部内间距
            };
            this.Controls.Add(_container);
        }

        public override void Save()
        {
            base.Save();
            
            // Restart only modified plugins
            if (_modifiedInstanceIds.Count > 0)
            {
                // Use Config property to access the latest in-memory state
                var instances = Config?.PluginInstances;
                if (instances == null) return;
                var liveSettings = Settings.Load();

                foreach (var id in _modifiedInstanceIds)
                {
                    var inst = instances.FirstOrDefault(x => x.Id == id);
                    // Pass the in-memory instance to avoid reading stale config from disk
                    // ★ inst 可能为 null（该实例已经被删掉了）。RestartInstance 的第二个形参
                    //   虽然声明为非空，但它的默认值就是 null，而且插件管理器内部对 null 的处理是
                    //   「实例不存在 ⇒ 清理对应的监控项」（见 src/Plugins/PluginManager.cs 的
                    //   RestartInstance，以及本文件 Save() 末尾「cleanup」那条注释）。
                    //   所以这里按「找得到 / 找不到」分两次调用：重载的默认实参同样是 null，
                    //   运行期行为与原来完全一致，也不需要用 `!` 去压制编译器。
                    if (inst == null)
                        PluginManager.Instance.RestartInstance(id);
                    else
                        PluginManager.Instance.RestartInstance(id, inst);
                }

                _modifiedInstanceIds.Clear();
            }
        }

        public override void OnShow()
        {
            base.OnShow();
            // Always rebuild UI on show to prevent Handle creation issues with deeply nested controls
            // when switching back to this page.
            RebuildUI();
        }

        private void RebuildUI()
        {
            // 1. Save State
            int savedScroll = 0;
            // ★ _container 在构造函数里就赋了值、之后从未被置空，运行时必定非 null。
            //   原先这里写的是 `_container != null && !_container.IsDisposed`，
            //   那个 null 判定反而把编译器的字段空值状态降级成「可能为 null」，
            //   于是下面 110 行 `_container.SuspendLayout()` 报 CS8602
            //   （空值检查自己制造出了空值警告）。
            //   改成先取到局部变量、只判真正要防的 IsDisposed：判定内容等价，
            //   运行期行为不变；并且本方法后续一律使用这个局部变量，
            //   局部变量不会污染字段的空值状态。
            var container = _container;
            if (!container.IsDisposed)
            {
                savedScroll = container.VerticalScroll.Value;
            }

            // Save Checkbox States
            var savedStates = new Dictionary<string, bool>();
            foreach (var kvp in _toggles)
            {
                if (kvp.Value != null && !kvp.Value.IsDisposed)
                {
                    savedStates[kvp.Key] = kvp.Value.Checked;
                }
            }
            _toggles.Clear();

            // 2. Suspend Layout & Clear Controls (Reuse container)
            container.SuspendLayout();
            
            // Clear pending actions
            _refreshActions.Clear();

            // Dispose old controls
            while (container.Controls.Count > 0)
            {
                var ctrl = container.Controls[0];
                container.Controls.RemoveAt(0);
                ctrl.Dispose();
            }
            
            // Fix: Reset AutoScrollMinSize to prevent ghost whitespace at bottom
            container.AutoScrollMinSize = new Size(0, 0);
            
            var templates = PluginManager.Instance.GetAllTemplates();
            // Use Config instead of Settings.Load() to ensure consistency with SettingsForm context
            // [Fix] Prevent singleton pollution by avoiding Settings.Load() fallback
            var instances = Config?.PluginInstances;
            
            // 1. Hint Note with Link
            var linkDoc = new LiteLink(LanguageManager.T("Menu.PluginDevGuide"), () => {
                try { 
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/mojingjing-git/LiteMonitorPlus/blob/master/resources/plugins/PLUGIN_DEV_GUIDE.md") { UseShellExecute = true }); 
                } catch { }
            });
            var hintRow = new LiteActionRow(LanguageManager.T("Menu.PluginHint"), linkDoc);

            hintRow.Dock = DockStyle.Top;
            container.Controls.Add(hintRow);
            
            var hintSpacer = new Panel { Dock = DockStyle.Top, Height = 5, BackColor = Color.Transparent };
            container.Controls.Add(hintSpacer);
            
            if (instances == null || instances.Count == 0)
            {
                var lbl = new Label { 
                    Text = LanguageManager.T("Menu.PluginNoInstances"), 
                    AutoSize = true, 
                    ForeColor = UIColors.TextSub, 
                    Location = new Point(UIUtils.S(20), UIUtils.S(60)) 
                };
                container.Controls.Add(lbl);
            }
            else
            {
                var grouped = instances.GroupBy(i => i.TemplateId);

                foreach (var grp in grouped)
                {
                    var tmpl = templates.FirstOrDefault(t => t.Id == grp.Key);
                    if (tmpl == null) continue;

                    var list = grp.ToList();
                    for (int i = 0; i < list.Count; i++)
                    {
                        var inst = list[i];
                        bool isDefault = (i == 0); 
                        bool? savedState = savedStates.ContainsKey(inst.Id) ? savedStates[inst.Id] : (bool?)null;
                        CreatePluginGroup(inst, tmpl, isDefault, savedState);
                    }
                }
            }
            
            // Fix: Add a transparent spacer to force bottom padding in AutoScroll
            var spacer = new Panel 
            { 
                Dock = DockStyle.Top, 
                Height = 40, 
                BackColor = Color.Transparent 
            };
            container.Controls.Add(spacer);
            container.Controls.SetChildIndex(spacer, 0); // Force to be the last docked item (Bottom)

            // 3. Resume Layout & Restore Scroll
            container.ResumeLayout(true);

            // [Fix] Reset AutoScroll to recalculate scrollable area and remove ghost whitespace
            container.AutoScroll = false;
            container.PerformLayout();
            container.AutoScroll = true;

            if (savedScroll > 0)
            {
                container.AutoScrollPosition = new Point(0, savedScroll);
            }
        }

        private void CreatePluginGroup(PluginInstanceConfig inst, PluginTemplate tmpl, bool isDefault, bool? savedState = null)
        {
            string title = $"{PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn)} v{tmpl.Meta.Version} (ID: {inst.Id}) by: {tmpl.Meta.Author}";
            var group = new LiteSettingsGroup(title);

            // 1. Header Actions
            if (isDefault)
            {
                var btnCopy = new LiteHeaderBtn(LanguageManager.T("Menu.PluginCreateCopy"));
                btnCopy.SetColor(UIColors.Primary);
                btnCopy.Click += (s, e) => CopyInstance(inst);
                group.AddHeaderAction(btnCopy);
            }
            else
            {
                var btnDel = new LiteHeaderBtn(LanguageManager.T("Menu.PluginDeleteCopy"));
                btnDel.SetColor(Color.IndianRed);
                btnDel.Click += (s, e) => DeleteInstance(inst);
                group.AddHeaderAction(btnDel);
            }

            string metaDescription = PluginI18n.Pick(tmpl.Meta.Description, tmpl.Meta.DescriptionEn);
            if (!string.IsNullOrEmpty(metaDescription))
            {
                 group.AddHint(metaDescription);
            }

            // 2. Enable Switch
            // Defined here to be used in toggle logic
            var targetVisibles = new List<Control>();

            // Handle state memory: Use savedState for first render, but inst.Enabled for subsequent refreshes
            bool usedCache = false;
            Func<bool> getVal = () => 
            {
                if (!usedCache && savedState.HasValue) 
                {
                    usedCache = true;
                    return savedState.Value;
                }
                return inst.Enabled;
            };

            var chk = group.AddToggle(this, PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn),
                getVal, 
                v => {
                    if (inst.Enabled != v) {
                        inst.Enabled = v;
                        _modifiedInstanceIds.Add(inst.Id);
                    }
                }
            );
            _toggles[inst.Id] = chk;

            // Real-time visibility toggle
            chk.CheckedChanged += (s, e) => {
                if (group.IsDisposed) return;
                foreach (var c in targetVisibles) 
                {
                    if (c != null && !c.IsDisposed)
                        c.Visible = chk.Checked;
                }
            };

            // 3. Refresh Rate
            int minInterval = PluginManager.GetMinimumInterval(tmpl);
            group.AddInt(this, "Menu.Refresh", "s", 
                () => PluginManager.GetEffectiveInterval(inst, tmpl),
                v => {
                    int safeValue = Math.Max(v, minInterval);
                    if (inst.CustomInterval != safeValue) {
                        inst.CustomInterval = safeValue;
                        _modifiedInstanceIds.Add(inst.Id);
                    }
                },
                60, null
            );

            // Split Inputs
            var globalInputs = tmpl.Inputs.Where(x => x.Scope != "target").ToList();
            var targetInputs = tmpl.Inputs.Where(x => x.Scope == "target").ToList();

            // 4. Global Inputs
            foreach (var input in globalInputs)
            {
                var inputCtrl = new LiteUnderlineInput(
                    inst.InputValues.ContainsKey(input.Key) ? inst.InputValues[input.Key] : input.DefaultValue, 
                    "", "", 100, null, HorizontalAlignment.Left
                );
                if (!string.IsNullOrEmpty(input.Placeholder)) inputCtrl.Placeholder = input.Placeholder;

                // Direct Binding: Update Model Immediately on Change (Memory Only)
                inputCtrl.Inner.TextChanged += (s, e) => {
                    inst.InputValues[input.Key] = inputCtrl.Inner.Text;
                    _modifiedInstanceIds.Add(inst.Id);
                };
                // Initial refresh for display (if needed, but constructor sets initial value)
                // this.RegisterRefresh(() => inputCtrl.Inner.Text = inst.InputValues.ContainsKey(input.Key) ? inst.InputValues[input.Key] : input.DefaultValue);

                var item = new LiteSettingsItem(input.Label, inputCtrl);
                group.AddItem(item);
                targetVisibles.Add(item);
            }

            // 5. Targets Section
            if (targetInputs.Count > 0)
            {
                if (inst.Targets == null) inst.Targets = new List<Dictionary<string, string>>();
                
                // Ensure at least one target exists for UI display
                if (inst.Targets.Count == 0)
                {
                    var defaultTarget = new Dictionary<string, string>();
                    foreach(var input in targetInputs)
                    {
                        defaultTarget[input.Key] = input.DefaultValue;
                    }
                    inst.Targets.Add(defaultTarget);
                }
                
                for (int i = 0; i < inst.Targets.Count; i++)
                {
                    int index = i; 
                    var targetVals = inst.Targets[i];
                    
                    // Remove Action
                    var linkRem = new LiteLink(LanguageManager.T("Menu.PluginRemoveTarget"), () => {
                        inst.Targets.RemoveAt(index);
                        // SaveAndRestart(inst); // <-- Removed auto-save on delete
                        _modifiedInstanceIds.Add(inst.Id);
                        RebuildUI();
                    });
                    linkRem.SetColor(Color.IndianRed, Color.Red);

                    if (inst.Targets.Count <= 1)
                    {
                        linkRem.Enabled = false;
                    }

                    var headerItem = new LiteSettingsItem(LanguageManager.T("Menu.PluginTargetTitle") + " " + (index + 1), linkRem);
                    headerItem.Label.Font = UIFonts.Bold(9F);
                    headerItem.Label.ForeColor = UIColors.Primary;
                    
                    group.AddFullItem(headerItem);
                    targetVisibles.Add(headerItem);

                    foreach (var input in targetInputs)
                    {
                        var val = targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue;
                        
                        if (input.Type == "select" && input.Options != null)
                        {
                            // Manual AddComboPair to capture item
                            var cmb = new LiteComboBox();
                            foreach (var opt in input.Options)
                            {
                                string label = "";
                                string vOpt = "";
                                
                                // Reflection to get Label/Value (assuming dynamic/object)
                                Type t = opt.GetType();
                                var pLabel = t.GetProperty("Label");
                                var pValue = t.GetProperty("Value");
                                
                                // ★ 反射取 Label/Value：这两个属性在 PluginInputOption 里都是
                                //   非空 string（`public string Label { get; set; } = "";`），
                                //   正常情况下 ToString() 不会返回 null。
                                //   ★ 但 ComboBox.AddItem 的两个形参都要求非空 string，
                                //   这里用 `?? ""` 兜住「反射拿到 null」这一理论情况：
                                //   原代码把 null 传进去，下拉项显示成空白、选中值也是 null；
                                //   兜成空串后显示效果完全一致（还是空标签），只是不再把 null 带进配置。
                                if (pLabel != null) label = pLabel.GetValue(opt)?.ToString() ?? "";
                                if (pValue != null) vOpt = pValue.GetValue(opt)?.ToString() ?? "";
                                
                                cmb.AddItem(label, vOpt);
                            }

                            cmb.SelectValue(targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue);
                            
                            // Direct Binding: Update Model Immediately on Change (Memory Only)
                            cmb.Inner.SelectedIndexChanged += (s, e) => {
                                targetVals[input.Key] = cmb.SelectedValue;
                                _modifiedInstanceIds.Add(inst.Id);
                            };
                            
                            // AttachAutoWidth logic inline
                            cmb.Inner.DropDown += (s, e) => {
                                // ★ DropDown 事件的 sender 按 WinForms 约定声明为 object?，
                                //   这里用模式匹配收窄成 ComboBox（而不是强制转换 + 直接解引用），
                                //   这样 sender 为 null 时不会抛 NullReferenceException。
                                //   ★ 实际运行时 sender 一定是内嵌的 ComboBox，行为不变。
                                if (s is ComboBox box)
                                {
                                    int maxWidth = box.Width;
                                    foreach (var item in box.Items) {
                                        if (item == null) continue;
                                        int w = TextRenderer.MeasureText(item.ToString(), box.Font).Width + SystemInformation.VerticalScrollBarWidth + 10;
                                        if (w > maxWidth) maxWidth = w;
                                    }
                                    box.DropDownWidth = maxWidth;
                                }
                            };

                            var item = new LiteSettingsItem("  " + input.Label, cmb);
                            group.AddItem(item);
                            targetVisibles.Add(item);
                        }
                        else
                        {
                            // Manual AddInput to capture item
                            var inputCtrl = new LiteUnderlineInput(
                                targetVals.ContainsKey(input.Key) ? targetVals[input.Key] : input.DefaultValue, 
                                "", "", 120, null, HorizontalAlignment.Center
                            );
                            if (!string.IsNullOrEmpty(input.Placeholder)) inputCtrl.Placeholder = input.Placeholder;

                            // Direct Binding: Update Model Immediately on Change (Memory Only)
                            inputCtrl.Inner.TextChanged += (s, e) => {
                                targetVals[input.Key] = inputCtrl.Inner.Text;
                                _modifiedInstanceIds.Add(inst.Id);
                            };

                            var item = new LiteSettingsItem("  " + input.Label, inputCtrl);
                            group.AddItem(item);
                            targetVisibles.Add(item);
                        }
                    }
                }

                // Add Target Button
                var btnAdd = new LiteButton(LanguageManager.T("Menu.PluginAddTarget"), false, true); 
                btnAdd.Click += (s, e) => {
                    var newTarget = new Dictionary<string, string>();
                    // [New Feature] Populate default values from template definition
                    foreach(var input in targetInputs)
                    {
                        newTarget[input.Key] = input.DefaultValue;
                    }
                    inst.Targets.Add(newTarget);
                    _modifiedInstanceIds.Add(inst.Id);
                    RebuildUI();
                };
                
                group.AddFullItem(btnAdd);
                btnAdd.Margin = UIUtils.S(new Padding(0, 15, 0, 0));
                targetVisibles.Add(btnAdd);
            }

            // [Fix] 先将组添加到页面，确保其父窗口链完整，防止 "未能设置控件的 Win32 父窗口" 崩溃
            AddGroupToPage(group);
            
            // [Fix] 强制创建句柄，确保子控件在设置可见性时能找到有效的父句柄
            if (!group.IsHandleCreated) { var _ = group.Handle; }

            // Initial visibility state
            foreach (var c in targetVisibles) c.Visible = chk.Checked;
        }

        private void AddGroupToPage(LiteSettingsGroup group)
        {
            // Use a transparent spacer instead of wrapper to reduce nesting depth
            // This helps prevent "Unable to set Win32 parent" errors caused by deep nesting
            var spacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            
            _container.Controls.Add(spacer);
            _container.Controls.SetChildIndex(spacer, 0);

            _container.Controls.Add(group);
            _container.Controls.SetChildIndex(group, 0);
        }

        private void CopyInstance(PluginInstanceConfig source)
        {
            var newInst = new PluginInstanceConfig
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                TemplateId = source.TemplateId,
                Enabled = source.Enabled,
                InputValues = new Dictionary<string, string>(source.InputValues),
                CustomInterval = source.CustomInterval
            };
            
            if (source.Targets != null)
            {
                 foreach(var t in source.Targets)
                 {
                     newInst.Targets.Add(new Dictionary<string, string>(t));
                 }
            }

            if (Config == null) return;
            SettingsChanger.AddPlugin(Config, newInst);
            
            // Do NOT start instance immediately for Draft config.
            // It will be started when user clicks "Apply/Save" via _modifiedInstanceIds logic.
            _modifiedInstanceIds.Add(newInst.Id);
            
            RebuildUI();
        }

        private void DeleteInstance(PluginInstanceConfig inst)
        {
            if (MessageBox.Show(LanguageManager.T("Menu.PluginDeleteConfirm"), LanguageManager.T("Menu.OK"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                if (Config == null) return;
                SettingsChanger.RemovePlugin(Config, inst);
                
                // Do NOT call PluginManager.RemoveInstance directly for Draft.
                // Just mark it as modified so Save() can handle cleanup (RestartInstance -> null/disabled logic).
                _modifiedInstanceIds.Add(inst.Id); 
                
                RebuildUI();
            }
        }
    }
}
