using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using AppearanceBackdropProbe.Native;

namespace AppearanceBackdropProbe.Probe
{
    /// <summary>
    /// 复刻生产 MainForm 窗口条件的试验窗口。
    ///
    /// 复刻项（对照 src/UI/MainForm_Transparent.cs + src/UI/Helpers/MainFormWinHelper.cs）：
    ///  - FormBorderStyle.None / ShowInTaskbar=false / TopMost
    ///  - WS_EX_TOOLWINDOW 且清除 WS_EX_APPWINDOW
    ///  - SetStyle(SupportsTransparentBackColor) + AllPaintingInWmPaint|OptimizedDoubleBuffer|ResizeRedraw
    ///  - OnPaintBackground 空实现（不画系统背景）
    ///  - GDI+ 文本绘制（DrawString）
    ///  - Form.Opacity（0.1–1.0，生产淡入终态）
    ///  - WS_EX_TRANSPARENT 鼠标穿透 + WS_EX_LAYERED
    ///  - Win11 走 DWM 系统圆角，Win10 走 Form.Region
    ///
    /// 额外：画一个纯品红"自身标记"方块，用于在捕获结果里判定"是否把自身也抓进来了"。
    /// </summary>
    internal sealed class ProbeForm : Form
    {
        /// <summary>自身标记色（品红）。生产面板不使用该色，命中即代表画面里有本窗口自己的像素。</summary>
        public static readonly Color SelfMarker = Color.FromArgb(255, 0, 255);

        /// <summary>是否为分层窗口（生产：Form.Opacity 必然产生 WS_EX_LAYERED）。</summary>
        public bool UseLayeredStyle { get; set; } = true;

        /// <summary>是否启用鼠标穿透（WS_EX_TRANSPARENT）。</summary>
        public bool ClickThrough { get; set; } = true;

        /// <summary>窗口整体透明度（生产淡入终态）。</summary>
        public double WindowOpacity { get; set; } = 0.85;

        /// <summary>是否绘制自绘内容。false = 只画边框与自身标记（用于"让出客户区给 DWM 材质"的对照变体）。</summary>
        public bool PaintContent { get; set; } = true;

        /// <summary>是否绘制 GDI+ 文本。对照变体可关闭，避免遮挡 DWM 材质。</summary>
        public bool PaintText { get; set; } = true;

        /// <summary>圆角半径（Win10 Region 路径）。</summary>
        public int CornerRadius { get; set; } = 8;

        /// <summary>自反馈演示用：把捕获到的画面画回窗口（模拟"没排除自身"的错误实现）。</summary>
        public Bitmap? FeedbackImage { get; set; }

        public Point FeedbackOffset { get; set; }

        public int FeedbackAlpha { get; set; } = 200;

        /// <summary>创建后是否立即应用圆角/穿透样式。</summary>
        public bool ApplyWindowStyles { get; set; } = true;

        /// <summary>
        /// 是否置顶。默认 true（历史行为）。设为 false 时不置顶、不抢前台，
        /// 用于不得干扰用户桌面的非置顶验收模式。
        /// </summary>
        public bool UseTopMost { get; set; } = true;

        /// <summary>
        /// 是否允许激活窗口。默认 true（历史行为）。设为 false 时加 WS_EX_NOACTIVATE，
        /// 显示与点击都不会把前台焦点抢走。
        /// </summary>
        public bool AllowActivation { get; set; } = true;

        public ProbeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            // 尺寸一律按"物理像素"给出：试验需要场景与探针严格对齐，不能让 WinForms 自行缩放
            AutoScaleMode = AutoScaleMode.None;

            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>
        /// 显示之前调用：把置顶策略落到窗口属性上（必须在句柄创建前设置）。
        /// </summary>
        public void ApplyActivationPolicy()
        {
            TopMost = UseTopMost;
        }

        /// <summary>
        /// 在创建句柄之前设置"是否分层"。WinForms 依据 BackColor 的 alpha 或 Opacity 自动决定 WS_EX_LAYERED，
        /// 因此这里必须显式区分：
        ///  - 分层（生产复刻）：BackColor 全透明 → WinForms 产生 WS_EX_LAYERED；
        ///  - 非分层（对照组）：BackColor 不透明 + Opacity=1 → 无 WS_EX_LAYERED。
        /// </summary>
        public void ApplyVariant()
        {
            BackColor = UseLayeredStyle ? Color.FromArgb(0, 0, 0) : SystemColors.Control;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Interop.WS_EX_TOOLWINDOW;
                cp.ExStyle &= ~Interop.WS_EX_APPWINDOW;
                if (UseTopMost)
                {
                    cp.ExStyle |= Interop.WS_EX_TOPMOST;
                }
                else
                {
                    cp.ExStyle &= ~Interop.WS_EX_TOPMOST;
                }
                if (!AllowActivation)
                {
                    cp.ExStyle |= Interop.WS_EX_NOACTIVATE;
                }
                if (ClickThrough && UseLayeredStyle)
                {
                    cp.ExStyle |= Interop.WS_EX_TRANSPARENT;
                    cp.ExStyle |= Interop.WS_EX_LAYERED;
                }
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (UseLayeredStyle)
            {
                // 生产：淡入结束后的稳定态
                Opacity = WindowOpacity;
            }

            if (ApplyWindowStyles)
            {
                ApplyRoundedCornersLikeProduction();
                if (ClickThrough && UseLayeredStyle) SetClickThroughLikeProduction(true);
            }
        }

        /// <summary>与生产 MainFormWinHelper.ApplyRoundedCorners 完全同构。</summary>
        public void ApplyRoundedCornersLikeProduction()
        {
            try
            {
                bool isWin11 = Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;
                if (isWin11)
                {
                    Region = null;
                    int preference = Interop.DWMWCP_ROUND;
                    Interop.DwmSetWindowAttribute(Handle, Interop.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                    int borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                    Interop.DwmSetWindowAttribute(Handle, Interop.DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                }
                else
                {
                    if (CornerRadius <= 0) { Region = null; return; }
                    using var gp = new GraphicsPath();
                    int d = CornerRadius * 2;
                    gp.AddArc(0, 0, d, d, 180, 90);
                    gp.AddArc(Width - d, 0, d, d, 270, 90);
                    gp.AddArc(Width - d, Height - d, d, d, 0, 90);
                    gp.AddArc(0, Height - d, d, d, 90, 90);
                    gp.CloseFigure();
                    Region?.Dispose();
                    Region = new Region(gp);
                }
            }
            catch { /* 试验窗口：失败不影响其它测量 */ }
        }

        /// <summary>与生产 MainFormWinHelper.SetClickThrough 完全同构。</summary>
        public void SetClickThroughLikeProduction(bool enable)
        {
            try
            {
                int ex = Interop.GetWindowLong(Handle, Interop.GWL_EXSTYLE);
                if (enable)
                    Interop.SetWindowLong(Handle, Interop.GWL_EXSTYLE, ex | Interop.WS_EX_TRANSPARENT | Interop.WS_EX_LAYERED);
                else
                    Interop.SetWindowLong(Handle, Interop.GWL_EXSTYLE, ex & ~Interop.WS_EX_TRANSPARENT);
            }
            catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 生产 MainForm.OnPaintBackground 是空实现（不画系统背景）
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var client = ClientRectangle;

            if (PaintContent)
            {
                // 生产面板语义：半透明深色底板 + 分组 + GDI+ 文本
                // 底色取中性灰，避免给背后内容引入色相偏移（色相是"实时响应"的判定量）
                using (var back = new SolidBrush(Color.FromArgb(150, 16, 16, 16)))
                    g.FillRectangle(back, client);
            }

            // 自身标记：纯品红方块。命中即代表"捕获结果里包含了本窗口自己的像素"。
            using (var marker = new SolidBrush(SelfMarker))
                g.FillRectangle(marker, 8, 8, 48, 48);

            // GDI+ 文本：标题 + 数字。用于判定"自己的标题/数字轮廓"是否出现在背景里。
            if (PaintText)
            {
                using (var title = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (var body = new Font("Consolas", 16f, FontStyle.Regular))
                using (var white = new SolidBrush(Color.FromArgb(235, 235, 240, 250)))
                using (var cyan = new SolidBrush(Color.FromArgb(235, 120, 230, 255)))
                {
                    g.DrawString("A06 PROBE SELF", title, white, 8, 66);
                    g.DrawString("00000", body, cyan, 8, 92);
                    g.DrawString("1234567", body, white, 8, 120);
                    g.DrawString("PROBE", title, white, 8, 150);
                }
            }

            // 外框
            using (var pen = new Pen(Color.FromArgb(200, 120, 230, 255), 1f))
                g.DrawRectangle(pen, 0, 0, client.Width - 1, client.Height - 1);

            // 自反馈演示层：把捕获结果叠回窗口
            if (FeedbackImage != null)
            {
                using var attr = new ImageAttributes();
                var m = new ColorMatrix { Matrix33 = FeedbackAlpha / 255f };
                attr.SetColorMatrix(m);
                var dest = new Rectangle(FeedbackOffset.X, FeedbackOffset.Y, client.Width, client.Height);
                g.DrawImage(FeedbackImage, dest, 0, 0, FeedbackImage.Width, FeedbackImage.Height, GraphicsUnit.Pixel, attr);
            }
        }
    }
}
