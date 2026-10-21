using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Drawing.Text;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// LiteMonitor++ 主题编辑器主窗口
    /// 左侧：主题管理
    /// 中间：TabControl（Layout / Font / Colors / Threshold）
    /// 右侧：ThemePreviewControl（实时预览）
    /// </summary>
    public class ThemeEditorForm : Form
    {
        // 左栏：主题管理
        private ListBox? lstThemes;
        private Button? btnNew;
        private Button? btnRename;
        private Button? btnCopy;
        private Button? btnDelete;
        private Button? btnCancel;

        // 中间：TabControl + 编辑页
        private TabControl? tab;
        private Panel? pageLayout;
        private Panel? pageFont;
        private Panel? pageColors;

        // 外观字段页（机能风 / 液态玻璃的新字段，外观改造计划 A10）
        private AppearanceFields? appearanceFields;

        // 右栏：预览
        private ThemePreviewControl? preview;

        private Theme? _theme;

        /// <summary>
        /// 进入编辑器时的原始快照：用于"取消"时把内存中的主题恢复原状。
        /// ★ 只存在于内存，<b>从不落盘</b>。
        /// </summary>
        private Theme? _pristineTheme;

        private string _currentThemeName = "";
        private readonly ThemeEditHistory _history = new();
        private bool _bindingTheme;
        private bool _restoringSelection;
        private bool IsGpuTheme => _theme?.Rendering?.IsGpuRequested == true;
        private bool IsToolbarTheme => _theme?.Rendering?.Parameters?.TryGetValue("glassStyle", out var value) == true &&
            value.ValueKind == System.Text.Json.JsonValueKind.String &&
            string.Equals(value.GetString(), "floatingToolbar", StringComparison.OrdinalIgnoreCase);

        private int AddGpuMappingNotice(Panel page, int y, float dpi)
        {
            if (!IsGpuTheme) return y;
            string text = IsToolbarTheme
                ? "浮动轻雾：组标题、项目/数值字体，主次文字、告警与进度条色即时预览。\n独立窗口标题及传统底纹字段仅用于普通回退。"
                : "当前 GPU 样式使用已确认的固定前景。字体/颜色页的字段仅影响普通回退；GPU 材质参数请在外观页调整。";
            page.Controls.Add(new Label { Text = text, Left = (int)(10 * dpi), Top = y,
                Width = (int)(510 * dpi), Height = (int)(62 * dpi), ForeColor = Color.DimGray });
            return y + (int)(68 * dpi);
        }

        // 系统字体缓存
        private InstalledFontCollection _fontCollection;

        public ThemeEditorForm()
        {
            Text = "LiteMonitor++ Theme Editor";
            StartPosition = FormStartPosition.CenterScreen;
            
            // DPI 缩放支持
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            
            // 基础尺寸（96 DPI 下的尺寸）
            int baseWidth = 1050;
            int baseHeight = 720;
            
            // 计算 DPI 缩放后的尺寸
            using (Graphics g = CreateGraphics())
            {
                float dpiScaleX = g.DpiX / 96f;
                float dpiScaleY = g.DpiY / 96f;
                
                Width = (int)(baseWidth * dpiScaleX);
                Height = (int)(baseHeight * dpiScaleY);
                MinimumSize = new Size((int)(baseWidth * dpiScaleX), (int)(baseHeight * dpiScaleY));
            }

            DoubleBuffered = true;

            _fontCollection = new InstalledFontCollection();

            BuildUI();
            LoadThemeList();
            KeyPreview = true;
            KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Z) { UndoEdit(); e.Handled = true; }
            };
        }

        // ============================================================
        // 初始化 UI 布局
        // ============================================================
        private void BuildUI()
        {
            // 获取 DPI 缩放比例
            float dpiScale = GetDpiScale();
            
            // ------------- 中间：TabControl -------------
            tab = new TabControl
            {
                Dock = DockStyle.Fill,
                Alignment = TabAlignment.Top,
                Font = new Font("Microsoft YaHei UI", 10) // 字体自动 DPI 缩放
            };
            
            pageLayout = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            pageFont = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            pageColors = new Panel { AutoScroll = true, Dock = DockStyle.Fill };

            // 外观字段页（新外观开关 / 背景 / alpha 编辑）
            appearanceFields = new AppearanceFields(dpiScale);

            
            var tpColors = new TabPage(" 颜色 / Colors ");
            tpColors.Controls.Add(pageColors);
            tab.TabPages.Add(tpColors);

            var tpFont = new TabPage(" 字体 / Font ");
            tpFont.Controls.Add(pageFont);
            tab.TabPages.Add(tpFont);

            var tpLayout = new TabPage(" 界面布局 / Layout ");
            tpLayout.Controls.Add(pageLayout);
            tab.TabPages.Add(tpLayout);

            var tpAppearance = new TabPage(" 外观 / Appearance ");
            tpAppearance.Controls.Add(appearanceFields);
            tab.TabPages.Add(tpAppearance);
            
            // ------------- 右侧：预览 -------------
            preview = new ThemePreviewControl
            {
                Dock = DockStyle.Right,
                Width = (int)((this.Width / 3.0) * dpiScale),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            
            // ------------- 左侧：主题管理 -------------
            var leftPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = (int)(220 * dpiScale),
                Padding = new Padding((int)(10 * dpiScale)),
                BackColor = Color.WhiteSmoke
            };
            
            lstThemes = new ListBox
            {
                Dock = DockStyle.Top,
                Height = (int)(500 * dpiScale),
                Font = new Font("Microsoft YaHei UI", 10), // 字体自动 DPI 缩放
            };
            lstThemes.SelectedIndexChanged += (_, __) => LoadSelectedTheme();
            leftPanel.Controls.Add(lstThemes);

            
            btnDelete = new Button { Text = "删除主题 / Delete Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnDelete.Click += (_, __) => CmdDeleteTheme();
            leftPanel.Controls.Add(btnDelete);

            btnCopy = new Button { Text = "复制主题 / Copy Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnCopy.Click += (_, __) => CmdCopyTheme();
            leftPanel.Controls.Add(btnCopy);

            btnRename = new Button { Text = "重命名主题 / Rename Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnRename.Click += (_, __) => CmdRenameTheme();
            leftPanel.Controls.Add(btnRename);

             
            btnNew = new Button { Text = "新建主题 / New Theme", Dock = DockStyle.Top, Height = (int)(40 * dpiScale) };
            btnNew.Click += (_, __) => CmdNewTheme();
            leftPanel.Controls.Add(btnNew);

            var saveAs = new Button { Text = "另存为 / Save As", Dock = DockStyle.Bottom, Height = (int)(36 * dpiScale) };
            saveAs.Click += (_, _) => SaveCurrentThemeAs();
            leftPanel.Controls.Add(saveAs);
            var undo = new Button { Text = "撤销 / Undo (Ctrl+Z)", Dock = DockStyle.Bottom, Height = (int)(36 * dpiScale) };
            undo.Click += (_, _) => UndoEdit();
            leftPanel.Controls.Add(undo);

            // 取消：不保存任何东西，内存中的主题恢复原状，磁盘文件一个字节都不动
            btnCancel = new Button { Text = "取消 / Cancel", Dock = DockStyle.Bottom, Height = (int)(40 * dpiScale) };
            btnCancel.Click += (_, __) => CancelEditing();
            leftPanel.Controls.Add(btnCancel);

            // 外观字段变化 → 刷新预览（只改内存，不写盘）
            if (appearanceFields != null)
            {
                appearanceFields.ValueChanged += (_, __) =>
                {
                    if (_theme != null) RefreshEditedPreview();
                };
            }

            // 按照正确的顺序添加控件到主窗体
            Controls.Add(tab);      // 先添加填充中间的控件
            Controls.Add(preview);  // 再添加右侧控件
            Controls.Add(leftPanel); // 最后添加左侧控件

            // 确保预览区域自适应宽度
            void UpdatePreviewWidth()
            {
                // 计算预览区域宽度，留出左侧面板和中间选项卡的空间
                int remainingWidth = this.Width - leftPanel.Width - (int)(20 * dpiScale); // 减去左侧面板宽度和一些边距
                preview.Width = Math.Max((int)(240 * dpiScale), remainingWidth / 3); // 确保最小宽度为240
            }
            
            // 初始化时设置宽度
            UpdatePreviewWidth();
            
            // 窗口大小改变时更新宽度
            this.Resize += (_, __) =>
            {
                UpdatePreviewWidth();
            };
        }

        // ============================================================
        // 加载主题列表
        // ============================================================
        private void LoadThemeList()
        {
            lstThemes.Items.Clear();
            foreach (var t in ThemeFileService.ListThemes())
                lstThemes.Items.Add(t);

            if (lstThemes.Items.Count > 0)
                lstThemes.SelectedIndex = 0;
        }

        // ============================================================
        // 获取 DPI 缩放比例
        // ============================================================
        private float GetDpiScale()
        {
            using (Graphics g = CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        // ============================================================
        // 加载选中的主题
        // ============================================================
        private void LoadSelectedTheme()
        {
            if (_restoringSelection || lstThemes.SelectedItem == null) return;
            string requested = lstThemes.SelectedItem.ToString()!;
            if (_theme is not null && _pristineTheme is not null && requested != _currentThemeName &&
                ThemeEditHistory.Signature(_theme) != ThemeEditHistory.Signature(_pristineTheme))
            {
                var choice = MessageBox.Show(this, "当前主题有未保存修改。保存后切换？\nSave changes before switching?",
                    "未保存的修改 / Unsaved changes", MessageBoxButtons.YesNoCancel);
                try
                {
                    if (choice == DialogResult.Yes) ThemeFileService.SaveTheme(_currentThemeName, _theme);
                    else if (choice == DialogResult.Cancel) { RestoreSelection(); return; }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失败 / Save failed"); RestoreSelection(); return; }
            }

            _currentThemeName = requested;
            _bindingTheme = true;
            try
            {
                _theme = ThemeFileService.LoadTheme(_currentThemeName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法加载主题：\n" + ex.Message);
                _bindingTheme = false;
                return;
            }

            // ★ 记录进入编辑器时的快照（只在内存里）：取消时用它恢复原状
            _pristineTheme = AppearanceThemeConfig.CloneConfig(_theme);

            BuildLayoutPage();
            BuildFontPage();
            BuildColorsPage();
            appearanceFields?.Bind(_theme);
            _history.Reset(_theme);
            _bindingTheme = false;
            RefreshEditedPreview();
        }

        private void RestoreSelection()
        {
            _restoringSelection = true;
            try { lstThemes!.SelectedItem = _currentThemeName; }
            finally { _restoringSelection = false; }
        }

        private void RefreshEditedPreview()
        {
            if (_theme is null || _bindingTheme) return;
            _history.Observe(_theme);
            preview?.SetTheme(_theme);
        }

        private void UndoEdit()
        {
            if (_theme is null) return;
            var previous = _history.Undo();
            if (previous is null) return;
            _bindingTheme = true;
            try
            {
                AppearanceThemeConfig.RestoreConfig(_theme, previous);
                BuildLayoutPage(); BuildFontPage(); BuildColorsPage();
                appearanceFields?.Bind(_theme);
            }
            finally { _bindingTheme = false; }
            preview?.SetTheme(_theme);
        }

        private void SaveCurrentThemeAs()
        {
            if (_theme is null) return;
            string name = InputBox("另存为（不含扩展名） / Save As", _currentThemeName + "_copy");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                ThemeFileService.SaveThemeAs(name, _theme);
                _pristineTheme = AppearanceThemeConfig.CloneConfig(_theme);
                _currentThemeName = name;
                _history.Reset(_theme);
                _restoringSelection = true;
                try { LoadThemeList(); lstThemes!.SelectedItem = name; }
                finally { _restoringSelection = false; }
                RefreshMainFormThemeMenu();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "另存为失败 / Save As failed"); }
        }

        /// <summary>
        /// 取消编辑：把内存中的主题恢复成进入编辑器前的状态，然后关闭编辑器。
        /// ★ 磁盘文件<b>不写</b>：只有"保存"按钮才会落盘。
        /// </summary>
        private void CancelEditing()
        {
            RestorePristineTheme();
            Close();
        }

        /// <summary>
        /// 就地把内存中的主题恢复成快照状态（对象引用不变）。
        /// </summary>
        private void RestorePristineTheme()
        {
            if (_theme == null || _pristineTheme == null) return;

            AppearanceThemeConfig.RestoreConfig(_theme, _pristineTheme);

            appearanceFields?.Bind(_theme);
            RefreshEditedPreview();
        }

        /// <summary>
        /// 关闭编辑器（不保存）= 取消：内存中的主题回到进入编辑器前的状态，
        /// 磁盘上的主题文件一个字节都不改。
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            RestorePristineTheme();
            base.OnFormClosing(e);
        }


        // ============================================================
        // 页面构建：Layout
        // ============================================================
        private void BuildLayoutPage()
        {
            pageLayout.Controls.Clear();
            if (_theme == null) return;

            float dpiScale = GetDpiScale();
            int y = (int)(10 * dpiScale);

            NumericUpDown AddNum(string label, Func<int> get, Action<int> set)
            {
                pageLayout.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(4 * dpiScale)),
                    Width = (int)(240 * dpiScale),
                    Height = (int)(25 * dpiScale) // Fix DPI clipping
                });

                var n = new NumericUpDown
                {
                    Location = new Point((int)(260 * dpiScale), y),
                    Width = (int)(100 * dpiScale),
                    Minimum = 0,
                    Maximum = 2000,
                    Value = get()
                };
                n.ValueChanged += (_, __) => { set((int)n.Value); RefreshEditedPreview(); };
                pageLayout.Controls.Add(n);

                y += (int)(36 * dpiScale);
                return n;
            }

            var L = _theme.Layout;

            //AddNum("界面宽度 / Width (px)", () => L.Width, v => L.Width = v);
            pageLayout.Controls.Add(new Label
            {
                Text = "说明：窗体圆角设置暂不支持预览 且仅win10下修改圆角有效",
                Location = new Point((int)(10 * dpiScale), y),
                Width = (int)(350 * dpiScale),
                Height = (int)(20 * dpiScale), // Fix DPI clipping
                ForeColor = Color.Gray,
                Font = new Font("Microsoft YaHei UI", 8f)
            });
            y += (int)(25 * dpiScale);
            AddNum("窗体圆角 / CornerRadius (px)", () => L.CornerRadius, v => L.CornerRadius = v);
            AddNum("行高 / RowHeight (px)", () => L.RowHeight, v => L.RowHeight = v);
            AddNum("内边距 / Padding (px)", () => L.Padding, v => L.Padding = v);
            AddNum("组块圆角 / GroupRadius (px)", () => L.GroupRadius, v => L.GroupRadius = v);
            AddNum("组块内边距 / GroupPadding (px)", () => L.GroupPadding, v => L.GroupPadding = v);
            AddNum("组块间距 / GroupSpacing (px)", () => L.GroupSpacing, v => L.GroupSpacing = v);
            AddNum("组块底部留白 / GroupBottom (px)", () => L.GroupBottom, v => L.GroupBottom = v);
            AddNum("监控项间距 / ItemGap (px)", () => L.ItemGap, v => L.ItemGap = v);
            AddNum("组标题偏移 / GroupTitleOffset (px)", () => L.GroupTitleOffset, v => L.GroupTitleOffset = v);

            AddSaveButton(pageLayout);
        }


        // ============================================================
        // 页面构建：Font
        // ============================================================
        private void BuildFontPage()
        {
            pageFont.Controls.Clear();
            if (_theme == null) return;

            float dpiScale = GetDpiScale();
            int y = (int)(10 * dpiScale);
            y = AddGpuMappingNotice(pageFont, y, dpiScale);

            // 系统字体
            var fonts = _fontCollection.Families.Select(f => f.Name).OrderBy(n => n).ToList();

            ComboBox AddFontCombo(string label, Func<string> get, Action<string> set)
            {
                pageFont.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(4 * dpiScale)),
                    Width = (int)(180 * dpiScale),
                    Height = (int)(25 * dpiScale) // Fix DPI clipping
                });

                var combo = new ComboBox
                {
                    Location = new Point((int)(200 * dpiScale), y),
                    Width = (int)(180 * dpiScale),
                    DropDownStyle = ComboBoxStyle.DropDownList
                };

                combo.Items.AddRange(fonts.ToArray());
                combo.SelectedItem = get();
                combo.SelectedIndexChanged += (_, __) =>
                {
                    set(combo.SelectedItem.ToString()!);
                    _theme!.BuildFonts(); // 添加这一行来构建新的字体
                    RefreshEditedPreview();
                };

                pageFont.Controls.Add(combo);
                y += (int)(36 * dpiScale);

                return combo;
            }

            NumericUpDown AddNum(string label, Func<double> get, Action<int> set)
            {
                pageFont.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(4 * dpiScale)),
                    Width = (int)(180 * dpiScale),
                    Height = (int)(25 * dpiScale) // Fix DPI clipping
                });

                var n = new NumericUpDown
                {
                    Location = new Point((int)(200 * dpiScale), y),
                    Width = (int)(150 * dpiScale),
                    Minimum = 8,
                    Maximum = 72,
                    Value = (decimal)get()
                };
                n.ValueChanged += (_, __) =>
                {
                    set((int)n.Value);
                    _theme!.BuildFonts();
                    RefreshEditedPreview();
                };
                pageFont.Controls.Add(n);

                y += (int)(36 * dpiScale);
                return n;
            }

            var F = _theme.Font;

            AddFontCombo("主字体 / Font Family", () => F.Family, v => F.Family = v);
            AddFontCombo("数值字体 / Value Font", () => F.ValueFamily, v => F.ValueFamily = v);

            AddNum("标题字号 / Title Size (pt)", () => F.Title, v => F.Title = v);
            AddNum("组标题字号 / GroupTitle Size (pt)", () => F.Group, v => F.Group = v);
            AddNum("项目字号 / Item Size (pt)", () => F.Item, v => F.Item = v);
            AddNum("数值字号 / Value Size (pt)", () => F.Value, v => F.Value = v);

            var chkBold = new CheckBox
            {
                Text = "加粗 / Bold",   
                Location = new Point((int)(10 * dpiScale), y),
                Checked = F.Bold
            };
            chkBold.CheckedChanged += (_, __) =>
            {
                F.Bold = chkBold.Checked;
                _theme.BuildFonts();
                RefreshEditedPreview();
            };
            pageFont.Controls.Add(chkBold);

            y += (int)(40 * dpiScale);

            AddSaveButton(pageFont);
        }


        // ============================================================
        // 页面构建：Colors
        // ============================================================
        private void BuildColorsPage()
        {
            pageColors.Controls.Clear();
            if (_theme == null) return;

            float dpiScale = GetDpiScale();
            int y = (int)(10 * dpiScale);
            y = AddGpuMappingNotice(pageColors, y, dpiScale);

            // 颜色缺省值（与 ColorConfig 的属性默认值一致）
            var defaults = new ColorConfig();

            Button AddColor(string label, Func<string> get, Action<string> set, string? defaultText = null)
            {
                pageColors.Controls.Add(new Label
                {
                    Text = label,
                    Location = new Point((int)(10 * dpiScale), y + (int)(6 * dpiScale)),
                    Width = (int)(210 * dpiScale),
                    Height = (int)(25 * dpiScale) // Fix DPI clipping
                });

                // 获取当前颜色
                Color currentColor = ThemeManager.ParseColor(get());
                
                // 简单的对比度计算：根据颜色亮度决定文字颜色
                var brightness = (currentColor.R * 0.299 + currentColor.G * 0.587 + currentColor.B * 0.114);
                var textColor = brightness > 127 ? Color.Black : Color.White;
                
                var btn = new Button
                {
                    Location = new Point((int)(220 * dpiScale), y),
                    Width = (int)(120 * dpiScale),
                    Height = (int)(30 * dpiScale), // 稍微增加高度
                    Text = get(),
                    BackColor = currentColor, // 设置按钮背景色为当前颜色
                    ForeColor = textColor // 根据背景亮度设置文字颜色
                };

                // alpha（透明度）编辑：0–1，写回格式 rgba(r,g,b,a)
                var alphaEditor = new AppearanceAlphaEditor
                {
                    Location = new Point((int)(348 * dpiScale), y + (int)(2 * dpiScale)),
                    Width = (int)(110 * dpiScale)
                };
                alphaEditor.SetColorText(get());

                // 统一的颜色写回（按钮外观 + alpha 显示一起刷新）
                void ApplyColorText(string text)
                {
                    btn.Text = text;
                    Color c = ThemeManager.ParseColor(text);
                    btn.BackColor = c;
                    var b = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
                    btn.ForeColor = b > 127 ? Color.Black : Color.White;
                    alphaEditor.SetColorText(text);
                }

                btn.Click += (_, __) =>
                {
                    using var cd = new ColorDialog();
                    cd.Color = ThemeManager.ParseColor(get());
                    // 配置新版颜色选择器选项
                    cd.AllowFullOpen = true; // 允许用户自定义颜色
                    cd.FullOpen = true; // 默认展开自定义颜色面板
                    cd.AnyColor = true; // 允许选择任何颜色
                    cd.SolidColorOnly = false; // 允许选择非纯色
                    cd.ShowHelp = false; // 不显示帮助按钮
                    
                    if (cd.ShowDialog() == DialogResult.OK)
                    {
                        // 保留当前 alpha，只换 RGB（alpha 由右侧输入框单独编辑）
                        double alpha = AppearanceColorText.TryGetAlpha(get(), out double a) ? a : 1.0;
                        string hex = AppearanceColorText.Format(cd.Color.R, cd.Color.G, cd.Color.B, alpha);
                        set(hex);
                        ApplyColorText(hex);
                        RefreshEditedPreview();
                    }
                };

                // alpha 改动：合法才写回；非法（越界 / NaN / Infinity / 颜色写坏了）一律拒绝并回退
                alphaEditor.AlphaChanged += (_, __) =>
                {
                    string current = get();
                    if (!AppearanceColorText.TrySetAlpha(current, alphaEditor.Alpha, out string newText))
                    {
                        alphaEditor.SetColorText(current);
                        return;
                    }

                    set(newText);
                    ApplyColorText(newText);
                    RefreshEditedPreview();
                };

                // 恢复缺省
                var btnDefault = new Button
                {
                    Text = "缺省",
                    Location = new Point((int)(464 * dpiScale), y),
                    Width = (int)(60 * dpiScale),
                    Height = (int)(30 * dpiScale)
                };
                btnDefault.Click += (_, __) =>
                {
                    string text = defaultText ?? string.Empty;
                    set(text);
                    ApplyColorText(text);
                    RefreshEditedPreview();
                };

                pageColors.Controls.Add(btn);
                pageColors.Controls.Add(alphaEditor);
                pageColors.Controls.Add(btnDefault);

                y += (int)(45 * dpiScale); // 增加垂直间距以适应更高的按钮
                return btn;
            }

            var C = _theme.Color;

            AddColor("背景色 / Background", () => C.Background, v => C.Background = v, defaults.Background);
            AddColor("组背景 / GroupBackground", () => C.GroupBackground, v => C.GroupBackground = v, defaults.GroupBackground);

            AddColor("标题字体 / TextTitle", () => C.TextTitle, v => C.TextTitle = v, defaults.TextTitle);
            AddColor("组标题字体 / TextGroup", () => C.TextGroup, v => C.TextGroup = v, defaults.TextGroup);
            AddColor("普通字体 / TextPrimary", () => C.TextPrimary, v => C.TextPrimary = v, defaults.TextPrimary);

            AddColor("安全值颜色 / ValueSafe", () => C.ValueSafe, v => C.ValueSafe = v, defaults.ValueSafe);    
            AddColor("警告值颜色 / ValueWarn", () => C.ValueWarn, v => C.ValueWarn = v, defaults.ValueWarn);
            AddColor("严重值颜色 / ValueCrit", () => C.ValueCrit, v => C.ValueCrit = v, defaults.ValueCrit);

            AddColor("进度条背景 / BarBackground", () => C.BarBackground, v => C.BarBackground = v, defaults.BarBackground);
            AddColor("进度条低位 / BarLow", () => C.BarLow, v => C.BarLow = v, defaults.BarLow);
            AddColor("进度条中位 / BarMid", () => C.BarMid, v => C.BarMid = v, defaults.BarMid);
            AddColor("进度条高位 / BarHigh", () => C.BarHigh, v => C.BarHigh = v, defaults.BarHigh);  

            // 新用途色（可空；缺省不写出）：描边 / 辉光 / 次级文字
            AddColor("描边色 / Border", () => C.Border ?? string.Empty, v => C.Border = string.IsNullOrEmpty(v) ? null : v);
            AddColor("辉光色 / Shadow", () => C.Shadow ?? string.Empty, v => C.Shadow = string.IsNullOrEmpty(v) ? null : v);
            AddColor("次级文字色 / TextSecondary", () => C.TextSecondary ?? string.Empty, v => C.TextSecondary = string.IsNullOrEmpty(v) ? null : v);

            AddSaveButton(pageColors);
        }

        // ============================================================
        // 通用保存按钮
        // ============================================================
        private void AddSaveButton(Panel page)
        {
            float dpiScale = GetDpiScale();
            
            var btn = new Button
            {
                Text = "保存主题 / Save Theme",
                Width = (int)(200 * dpiScale),
                Height = (int)(40 * dpiScale),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point((int)(10 * dpiScale), 0) // 初始位置设为顶部，让Anchor属性自动处理到底部的定位
            };

            // 为了确保按钮在面板加载完成后正确定位到底部，添加Layout事件处理
            page.Layout += (sender, e) => {
                if (page.Controls.Contains(btn))
                {
                    btn.Top = page.ClientSize.Height - (int)(80 * dpiScale); // 增加底部间距，应用DPI缩放
                }
            };

            btn.Click += (_, __) => SaveCurrentTheme();
            page.Controls.Add(btn);
        }

        private void SaveCurrentTheme()
        {
            if (_theme == null) return;

            try
            {
                ThemeFileService.SaveTheme(_currentThemeName, _theme);

                // 保存成功：快照更新为已保存状态（之后再"取消"就恢复到这份内容）
                _pristineTheme = AppearanceThemeConfig.CloneConfig(_theme);
                _history.Reset(_theme);

                // 刷新主界面菜单的主题列表
                RefreshMainFormThemeMenu();

                // 合并保存成功和是否应用主题的弹窗
                var result = MessageBox.Show("主题已保存 / Saved Theme: " + _currentThemeName +
                    "\n\n是否立即应用此主题？\nApply this theme now?",
                    "保存成功 / Save Success",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    ApplyCurrentThemeToMainForm();
                    RefreshMainFormThemeMenu();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败 / Save Failed:\n" + ex.Message,
                    "错误 / Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 刷新主界面的主题菜单列表
        /// </summary>
        private void RefreshMainFormThemeMenu()
        {
            // 查找主窗体实例
            var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            if (mainForm != null)
            {
                // 调用主窗体的菜单重建方法
                mainForm.RebuildMenus();
            }
        }

        /// <summary>
        /// 应用当前主题到主窗体
        /// </summary>
        private void ApplyCurrentThemeToMainForm()
        {
            // 查找主窗体实例
            var mainForm = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
            if (mainForm != null)
            {
                // 获取主窗体的UIController
                var uiControllerField = typeof(MainForm).GetField("_ui",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var settingsField = typeof(MainForm).GetField("_cfg",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (uiControllerField != null && settingsField != null)
                {
                    var uiController = uiControllerField.GetValue(mainForm) as UIController;
                    var settings = settingsField.GetValue(mainForm) as Settings;

                    if (uiController != null && settings != null)
                    {
                        // 更新设置中的主题名称
                        settings.Skin = _currentThemeName;
                        settings.Save();

                        // 应用新主题
                        uiController.ApplyTheme(_currentThemeName);

                    }
                }
            }
        }


        // ============================================================
        // 左侧菜单功能：新建、复制、重命名、删除
        // ============================================================
        private void CmdNewTheme()
        {
            string name = InputBox("输入新主题名称 / New Theme Name:");
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                ThemeFileService.CreateTheme(name);
                LoadThemeList();
                lstThemes.SelectedItem = name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void CmdRenameTheme()
        {
            if (_currentThemeName == "") return;

            string name = InputBox("输入新的主题名称 / Rename Theme Name:", _currentThemeName);
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                ThemeFileService.RenameTheme(_currentThemeName, name);
                LoadThemeList();
                lstThemes.SelectedItem = name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void CmdCopyTheme()
        {
            if (_currentThemeName == "") return;

            string name = InputBox("复制的主题名称 / Copy Theme Name:", _currentThemeName + "_copy");
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                ThemeFileService.DuplicateTheme(_currentThemeName, name);
                LoadThemeList();
                lstThemes.SelectedItem = name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void CmdDeleteTheme()
        {
            if (_currentThemeName == "") return;

            if (MessageBox.Show($"确定删除主题 / Delete Theme: {_currentThemeName}？", "提示 / Confirm:",
                MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;

            try
            {
                ThemeFileService.DeleteTheme(_currentThemeName);
                LoadThemeList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        // ============================================================
        // 简易输入框
        // ============================================================
        private string InputBox(string title, string defaultText = "")
        {
            float dpiScale = GetDpiScale();
            
            Form f = new Form
            {
                Text = title,
                Width = (int)(400 * dpiScale),
                Height = (int)(150 * dpiScale),
                StartPosition = FormStartPosition.CenterParent
            };

            TextBox tb = new TextBox
            {
                Left = (int)(20 * dpiScale),
                Top = (int)(20 * dpiScale),
                Width = (int)(340 * dpiScale),
                Text = defaultText
            };

            Button ok = new Button
            {
                Text = "确定 / OK",
                Left = (int)(80 * dpiScale),
                Top = (int)(60 * dpiScale),
                Width = (int)(100 * dpiScale),
                Height = (int)(30 * dpiScale) // 添加DPI缩放的高度
            };
            ok.Click += (_, __) => { f.DialogResult = DialogResult.OK; f.Close(); };

            Button cancel = new Button
            {
                Text = "取消 / Cancel",
                Left = (int)(180 * dpiScale),
                Top = (int)(60 * dpiScale),
                Width = (int)(100 * dpiScale),
                Height = (int)(30 * dpiScale) // 添加DPI缩放的高度
            };
            cancel.Click += (_, __) => { f.DialogResult = DialogResult.Cancel; f.Close(); };

            f.Controls.Add(tb);
            f.Controls.Add(ok);
            f.Controls.Add(cancel);

            return f.ShowDialog() == DialogResult.OK ? tb.Text : "";
        }
    }
}
