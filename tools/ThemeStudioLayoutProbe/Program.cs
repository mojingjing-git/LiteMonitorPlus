using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ThemeStudioLayoutProbe;

/// <summary>
/// TE00 交付物 2：原生标题栏 + 三栏最小布局原型。
///
/// 只验证窗口与布局事实，不接入主题编辑逻辑：
/// - 标准 WinForms Form，保留系统非客户区（标题栏、最小化/最大化/关闭、可调整边框）；
/// - 资料库（左，220 DIP）、画布（中，填充）、属性检查器（右，300 DIP）三栏；
/// - 文档工具栏与画布工具栏、底部状态区；
/// - 尺寸契约（计划 §7 第 1 条）：AutoScaleMode.None，全部几何以 96 DIP 设计，
///   由唯一入口 <see cref="Dip"/> 按 DeviceDpi 一次性换算——不依赖 AutoScale 魔法，
///   也不用 CreateGraphics 手动测 DPI（旧编辑器的两条缺陷路径）。
/// - --compact 以 720×480 DIP 紧凑工作区启动，并折叠资料库、收窄检查器。
///
/// 运行后自动截取窗口自身（DrawToBitmap，不做屏幕拷贝）到 --out 路径并退出。
/// </summary>
internal static class Program
{
    private const int DefaultWidthDip = 1120;
    private const int DefaultHeightDip = 760;
    private const int CompactWidthDip = 720;
    private const int CompactHeightDip = 480;

    // 工作区语义色（计划 §3.2：暖灰资料库、克制的蓝色强调；仅原型验证用）
    private static readonly Color LibraryBack = Color.FromArgb(0xF3, 0xF1, 0xEE);
    private static readonly Color LibraryItemSelected = Color.FromArgb(0xDC, 0xE7, 0xF8);
    private static readonly Color AccentBlue = Color.FromArgb(0x2F, 0x6F, 0xED);
    private static readonly Color CanvasBack = Color.FromArgb(0xFA, 0xFA, 0xF8);
    private static readonly Color PreviewBack = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Hairline = Color.FromArgb(0xE5, 0xE3, 0xDF);
    private static readonly Color TextMuted = Color.FromArgb(0x6B, 0x72, 0x80);
    private static readonly Color TextStrong = Color.FromArgb(0x2A, 0x2C, 0x30);

    /// <summary>DPI 换算唯一入口：先建句柄取 DeviceDpi，再创建全部几何。</summary>
    private static Func<int, int> _dip = dip => dip;

    private static int Dip(int value) => _dip(value);

    [STAThread]
    private static int Main(string[] args)
    {
        string outPath = Path.Combine(AppContext.BaseDirectory, "probe-screenshot.png");
        bool compact = Array.IndexOf(args, "--compact") >= 0;
        int outIndex = Array.IndexOf(args, "--out");
        if (outIndex >= 0 && outIndex + 1 < args.Length)
        {
            outPath = args[outIndex + 1];
        }

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Form form = BuildForm(compact);
        string? failure = null;

        form.Shown += (_, _) =>
        {
            // 等一轮布局与绘制完成再截图。
            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                try
                {
                    CaptureWindow(form, outPath);
                }
                catch (Exception ex)
                {
                    failure = ex.ToString();
                }

                form.Close();
            };
            timer.Start();
        };

        form.StartPosition = FormStartPosition.Manual;
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        form.Location = new Point(
            Math.Max(work.Left, work.Left + (work.Width - form.Width) / 2),
            Math.Max(work.Top, work.Top + (work.Height - form.Height) / 2));
        form.TopMost = true;

        Application.Run(form);
        return failure is null ? 0 : 2;
    }

    private static Form BuildForm(bool compact)
    {
        var form = new Form
        {
            Text = "LiteMonitor 主题工作室 — 布局原型（TE00）",
            AutoScaleMode = AutoScaleMode.None,
            Font = new Font("Segoe UI", 9F),
            BackColor = CanvasBack,
        };

        // 先建句柄取得真实 DeviceDpi，再以单一入口换算全部几何。
        _ = form.Handle;
        _dip = value => (int)Math.Round(value * form.DeviceDpi / 96.0);
        form.ClientSize = new Size(
            Dip(compact ? CompactWidthDip : DefaultWidthDip),
            Dip(compact ? CompactHeightDip : DefaultHeightDip));

        // === 状态区（底部）===
        var status = new StatusStrip { SizingGrip = false, BackColor = LibraryBack };
        var statusSaved = new ToolStripStatusLabel("未保存的更改");
        var statusRoute = new ToolStripStatusLabel("预览路线：GPU 场景（固定输入）");
        var statusDpi = new ToolStripStatusLabel("DPI：-");
        status.Items.AddRange(
            [statusSaved, new ToolStripStatusLabel("    "), statusRoute, new ToolStripStatusLabel("    "), statusDpi]);
        form.Controls.Add(status);

        // === 资料库（左栏）===
        var library = new Panel
        {
            Dock = DockStyle.Left,
            Width = Dip(compact ? 180 : 220),
            BackColor = LibraryBack,
            Padding = new Padding(Dip(8), Dip(8), Dip(4), Dip(4)),
        };
        var libraryTitle = new Label
        {
            Text = "资料库",
            Dock = DockStyle.Top,
            Height = Dip(28),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        };
        var libraryList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Dip(40),
            BackColor = LibraryBack,
            IntegralHeight = false,
        };
        libraryList.Items.AddRange([
            new LibraryItem("液态玻璃 · 浮动工具栏", "GPU · liquid_glass"),
            new LibraryItem("赛博曙光", "GPU · cyber_punk"),
            new LibraryItem("晨雾", "经典 · 玻璃"),
            new LibraryItem("墨玉", "经典 · 不透明"),
            new LibraryItem("经典蓝", "经典 · 不透明"),
        ]);
        libraryList.SelectedIndex = 0;
        libraryList.DrawItem += DrawLibraryItem;
        library.Controls.Add(libraryList);
        library.Controls.Add(libraryTitle);

        // === 属性检查器（右栏）===
        var inspector = new Panel
        {
            Dock = DockStyle.Right,
            Width = Dip(compact ? 240 : 300),
            BackColor = PreviewBack,
            Padding = new Padding(Dip(12), Dip(8), Dip(12), Dip(8)),
            AutoScroll = true,
        };
        BuildInspector(inspector, compact);

        // === 画布（中栏，三栏组合后填充）===
        var canvas = new Panel { Dock = DockStyle.Fill, BackColor = CanvasBack, Padding = new Padding(Dip(16)) };
        var canvasToolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = CanvasBack,
            RenderMode = ToolStripRenderMode.System,
        };
        var targetLabel = new ToolStripLabel("编辑目标：") { ForeColor = TextMuted };
        var targetCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dip(160) };
        targetCombo.Items.AddRange(["主效果（GPU）", "兼容外观（回退）"]);
        targetCombo.SelectedIndex = 0;
        var viewLabel = new ToolStripLabel("预览形态：") { ForeColor = TextMuted };
        var viewCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dip(170) };
        viewCombo.Items.AddRange(["竖向 · GPU 场景", "横向 · GPU 场景", "任务栏 · 常规绘制"]);
        viewCombo.SelectedIndex = 0;
        canvasToolbar.Items.AddRange([targetLabel, targetCombo, new ToolStripSeparator(), viewLabel, viewCombo]);

        var previewHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PreviewBack,
            Padding = new Padding(1),
            Margin = Padding.Empty,
        };
        previewHost.Paint += (_, e) =>
        {
            using var pen = new Pen(Hairline);
            e.Graphics.DrawRectangle(pen, 0, 0, previewHost.Width - 1, previewHost.Height - 1);
        };
        var previewTag = new Label
        {
            Text = "预览占位 — 主效果 · GPU 场景 · 固定输入\n（TE04 接入真实同源渲染）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = TextMuted,
            BackColor = PreviewBack,
        };
        previewHost.Controls.Add(previewTag);

        canvas.Controls.Add(previewHost);
        canvas.Controls.Add(canvasToolbar);

        // === 文档工具栏（顶部）===
        var docToolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = PreviewBack,
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(Dip(6), Dip(3), Dip(6), Dip(3)),
        };
        var docName = new ToolStripLabel("液态玻璃 · 浮动工具栏") { Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var dirtyDot = new ToolStripLabel("●") { ForeColor = AccentBlue, ToolTipText = "未保存的更改" };
        var undoButton = new ToolStripButton("撤销");
        var redoButton = new ToolStripButton("重做") { Enabled = false };
        var saveButton = new ToolStripButton("保存");
        var applyButton = new ToolStripButton("应用");
        var exportMenu = new ToolStripDropDownButton("导出");
        exportMenu.DropDownItems.Add("主题 JSON…");
        exportMenu.DropDownItems.Add("效果图 PNG…");
        exportMenu.DropDownItems.Add("准备投稿资料…");
        var collapseButton = new ToolStripButton("资料库");
        collapseButton.Click += (_, _) => library.Visible = !library.Visible;
        docToolbar.Items.AddRange(
            [docName, dirtyDot, new ToolStripSeparator(), undoButton, redoButton, new ToolStripSeparator(), saveButton, applyButton, exportMenu, new ToolStripSeparator(), collapseButton]);

        // Dock 优先级：后加入者优先（docToolbar Top > status Bottom > library Left >
        // inspector Right > canvas Fill），与三栏 + 双工具栏的成品组合一致。
        form.Controls.Add(canvas);
        form.Controls.Add(inspector);
        form.Controls.Add(library);
        form.Controls.Add(status);
        form.Controls.Add(docToolbar);

        form.Shown += (_, _) =>
        {
            statusDpi.Text = $"DPI：{form.DeviceDpi}（{form.DeviceDpi * 100 / 96}%）";
            if (compact)
            {
                library.Visible = false;
            }
        };

        return form;
    }

    private static void BuildInspector(Panel inspector, bool compact)
    {
        int y = 4;
        void AddGroupTitle(string text)
        {
            var label = new Label
            {
                Text = text,
                Location = new Point(Dip(12), Dip(y)),
                Size = new Size(inspector.Width - Dip(24), Dip(24)),
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            };
            inspector.Controls.Add(label);
            y += 28;
        }

        void AddRow(string name, string value, bool shared = false)
        {
            var nameLabel = new Label
            {
                Text = name,
                Location = new Point(Dip(12), Dip(y)),
                Size = new Size(Dip(96), Dip(24)),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var valueBox = new TextBox
            {
                Text = value,
                Location = new Point(Dip(112), Dip(y) - 1),
                Size = new Size(inspector.Width - Dip(128), Dip(24)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            inspector.Controls.Add(nameLabel);
            inspector.Controls.Add(valueBox);
            y += 28;
            if (shared)
            {
                var scope = new Label
                {
                    Text = "共享设置 — 同时影响主效果与兼容外观",
                    Location = new Point(Dip(112), Dip(y)),
                    Size = new Size(inspector.Width - Dip(128), Dip(16)),
                    ForeColor = AccentBlue,
                    Font = new Font("Segoe UI", 7.5F),
                };
                inspector.Controls.Add(scope);
                y += 18;
            }
        }

        AddGroupTitle("外观（主效果）");
        AddRow("cornerRadius", "30");
        AddRow("thickness", "8");
        if (!compact)
        {
            AddRow("refraction", "8");
            AddRow("blurRadius", "8");
        }

        AddRow("glassStyle", "floatingToolbar");

        AddGroupTitle("文字");
        AddRow("Font.Family", "微软雅黑", shared: true);
        AddRow("Font.Value", "12", shared: true);

        AddGroupTitle("兼容外观配色");
        AddRow("background", "#1E2228");
        if (!compact)
        {
            AddRow("text", "#F2F4F7");
        }

        AddRow("accent", "#8FB4D9");

        if (!compact)
        {
            AddGroupTitle("阈值");
            AddRow("CPU.Warn", "80");
            AddRow("CPU.Crit", "95");
        }
    }

    private static void DrawLibraryItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || list.Items[e.Index] is not LibraryItem item)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var backBrush = new SolidBrush(selected ? LibraryItemSelected : list.BackColor);
        e.Graphics.FillRectangle(backBrush, e.Bounds);
        if (selected)
        {
            using var accentBrush = new SolidBrush(AccentBlue);
            e.Graphics.FillRectangle(accentBrush, e.Bounds.Left, e.Bounds.Top, Dip(3), e.Bounds.Height);
        }

        var nameRect = new Rectangle(e.Bounds.Left + Dip(12), e.Bounds.Top + Dip(5), e.Bounds.Width - Dip(16), Dip(16));
        var subRect = new Rectangle(e.Bounds.Left + Dip(12), e.Bounds.Top + Dip(22), e.Bounds.Width - Dip(16), Dip(14));
        TextRenderer.DrawText(e.Graphics, item.Name, new Font("Segoe UI", 9F), nameRect, TextStrong);
        TextRenderer.DrawText(e.Graphics, item.Subtitle, new Font("Segoe UI", 7.5F), subRect, TextMuted);
        e.DrawFocusRectangle();
    }

    private static void CaptureWindow(Form form, string outPath)
    {
        // 只截取窗口自身（含非客户区），不做屏幕拷贝：
        // 多显示器混合 DPI 下 CopyFromScreen 坐标可能错位，且会抓到窗口外的桌面内容。
        int width = form.Width;
        int height = form.Height;
        using var bitmap = new Bitmap(width, height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        bitmap.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine(
            $"captured: {outPath} ({width}x{height}px, DeviceDpi={form.DeviceDpi}, " +
            $"ClientSize={form.ClientSize.Width}x{form.ClientSize.Height}, " +
            $"Screen={Screen.FromControl(form).Bounds}, Scale={form.DeviceDpi * 100 / 96}%)");
    }

    private sealed record LibraryItem(string Name, string Subtitle)
    {
        public override string ToString() => Name;
    }
}
