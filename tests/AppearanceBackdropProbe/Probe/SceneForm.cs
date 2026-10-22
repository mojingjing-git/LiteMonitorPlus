using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AppearanceBackdropProbe.Probe
{
    /// <summary>试验场景的几何布局（场景窗口内坐标）。</summary>
    internal sealed class SceneLayout
    {
        public int SceneWidth { get; init; }
        public int SceneHeight { get; init; }
        /// <summary>探针窗口相对场景窗口的左上角偏移。</summary>
        public int Margin { get; init; }
        public int ProbeWidth { get; init; }
        public int ProbeHeight { get; init; }

        /// <summary>探针窗口在场景内的矩形。</summary>
        public Rectangle ProbeRect => new(Margin, Margin, ProbeWidth, ProbeHeight);

        /// <summary>探针窗口内"静态纯色块"区域（时间不变，可用于精确比对背景正确性）。</summary>
        public Rectangle StaticPatchRect =>
            new(Margin, Margin, ProbeWidth, (int)(ProbeHeight * 0.45));

        /// <summary>探针窗口内"色相循环"区域（用于判定背后内容是否实时响应）。</summary>
        public Rectangle HueRegionRect =>
            new(Margin, Margin + (int)(ProbeHeight * 0.45), ProbeWidth, (int)(ProbeHeight * 0.17));

        /// <summary>探针窗口内"移动扫描条"区域（用于判定运动与拖影）。</summary>
        public Rectangle BarRegionRect =>
            new(Margin, Margin + (int)(ProbeHeight * 0.62), ProbeWidth, ProbeHeight - (int)(ProbeHeight * 0.62));
    }

    /// <summary>
    /// 合成背景场景的唯一绘制实现：屏幕上的场景窗口与离屏"参考图"共用同一段代码，
    /// 保证参考图与屏幕内容严格对应（否则"抓到的是不是真实背景"无法判定）。
    /// </summary>
    internal static class ScenePainter
    {
        /// <summary>静态纯色块颜色：RGB(24,92,168)。</summary>
        public static readonly Color StaticPatch = Color.FromArgb(255, 24, 92, 168);

        private static readonly Color CheckerA = Color.FromArgb(255, 40, 44, 52);
        private static readonly Color CheckerB = Color.FromArgb(255, 58, 63, 74);
        private static readonly Color BarBase = Color.FromArgb(255, 72, 72, 78);
        private static readonly Color BarBody = Color.FromArgb(255, 248, 248, 250);

        public static void Paint(Graphics g, Rectangle area, SceneLayout layout, double t)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 底板
            using (var b = new SolidBrush(Color.FromArgb(255, 24, 26, 32)))
                g.FillRectangle(b, area);

            // 棋盘（背景高频细节，用于观察模糊）
            const int cell = 16;
            for (int y = 0; y < area.Height; y += cell)
            {
                for (int x = 0; x < area.Width; x += cell)
                {
                    bool odd = ((x / cell) + (y / cell)) % 2 == 1;
                    using var b = new SolidBrush(odd ? CheckerB : CheckerA);
                    g.FillRectangle(b, x, y, cell, cell);
                }
            }

            // 探针窗口覆盖区域的场景内容（探针窗口本身位于该矩形之上）
            var pr = layout.ProbeRect;

            // 1) 静态纯色块：用于"抓到的确实是场景而不是自身"的精确比对
            using (var b = new SolidBrush(StaticPatch))
                g.FillRectangle(b, pr.X, pr.Y, pr.Width, (int)(pr.Height * 0.45));

            // 2) 色相循环区：颜色随时间连续变化，用于判定"背后内容是否实时响应"
            var hueRect = layout.HueRegionRect;
            using (var b = new SolidBrush(HsvToColor((float)((t * 120.0) % 360.0), 0.85f, 1.0f)))
                g.FillRectangle(b, hueRect.X, hueRect.Y, hueRect.Width, hueRect.Height);

            // 3) 移动扫描条区：白色竖条周期横扫，用于判定运动、拖影与递归变暗
            var barRect = layout.BarRegionRect;
            using (var b = new SolidBrush(BarBase))
                g.FillRectangle(b, barRect.X, barRect.Y, barRect.Width, barRect.Height);

            const int barWidth = 100;
            double cycle = 2.0;                       // 一个来回 2 秒
            double phase = (t % cycle) / cycle;        // 0..1
            double travel = pr.Width + barWidth;
            int barX = pr.X + (int)Math.Round((phase < 0.5 ? phase * 2.0 : (1.0 - phase) * 2.0) * travel) - barWidth;
            using (var b = new SolidBrush(BarBody))
                g.FillRectangle(b, barX, barRect.Y, barWidth, barRect.Height);

            // 场景外围文字（证明背景在动，且不落在探针窗口区域内）
            using (var f = new Font("Consolas", 20f, FontStyle.Bold))
            using (var b = new SolidBrush(Color.FromArgb(255, 210, 220, 235)))
            {
                g.DrawString($"A06 SCENE  t={t:0.00}s", f, b, 12, area.Height - 40);
            }
            using (var f = new Font("Consolas", 14f))
            using (var b = new SolidBrush(Color.FromArgb(255, 170, 180, 195)))
            {
                g.DrawString($"STATIC=RGB(24,92,168)  PROBE={layout.ProbeWidth}x{layout.ProbeHeight}", f, b, 12, area.Height - 62);
            }
        }

        /// <summary>离屏渲染参考图（与屏幕内容同源）。</summary>
        public static Bitmap RenderReference(SceneLayout layout, double t)
        {
            var bmp = new Bitmap(layout.SceneWidth, layout.SceneHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            Paint(g, new Rectangle(0, 0, layout.SceneWidth, layout.SceneHeight), layout, t);
            return bmp;
        }

        /// <summary>HSV → RGB（h: 0..360, s/v: 0..1）。</summary>
        public static Color HsvToColor(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
            float m = v - c;
            float r = 0f, g = 0f, b = 0f;
            if (h < 60f) { r = c; g = x; }
            else if (h < 120f) { r = x; g = c; }
            else if (h < 180f) { g = c; b = x; }
            else if (h < 240f) { g = x; b = c; }
            else if (h < 300f) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(255,
                (int)Math.Round((r + m) * 255f),
                (int)Math.Round((g + m) * 255f),
                (int)Math.Round((b + m) * 255f));
        }
    }

    /// <summary>
    /// 试验用合成背景窗口：棋盘 + 静态纯色块 + 色相循环区 + 移动扫描条。
    /// 只覆盖试验窗口所在矩形，不捕获整块桌面。
    /// </summary>
    internal sealed class SceneForm : Form
    {
        private readonly SceneLayout _layout;
        private readonly DateTime _start = DateTime.Now;
        private readonly System.Windows.Forms.Timer? _timer;

        public SceneForm(SceneLayout layout, bool animate)
        {
            _layout = layout;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(layout.SceneWidth, layout.SceneHeight);
            BackColor = Color.FromArgb(255, 24, 26, 32);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            if (animate)
            {
                _timer = new System.Windows.Forms.Timer { Interval = 33 };
                _timer.Tick += (_, __) => { TickCount++; Invalidate(); };
                _timer.Start();   // WinForms Timer 的 Enabled 默认 false，必须显式启动
            }
        }

        /// <summary>是否置顶。默认 true（历史行为）。</summary>
        public bool UseTopMost { get; set; } = true;

        /// <summary>是否允许激活窗口。默认 true（历史行为）。</summary>
        public bool AllowActivation { get; set; } = true;

        /// <summary>显示之前调用：把置顶策略落到窗口属性上。</summary>
        public void ApplyActivationPolicy()
        {
            TopMost = UseTopMost;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (!AllowActivation)
                {
                    cp.ExStyle |= AppearanceBackdropProbe.Native.Interop.WS_EX_NOACTIVATE;
                }
                return cp;
            }
        }

        public double TimeSeconds => (DateTime.Now - _start).TotalSeconds;

        /// <summary>累计重绘次数 —— 用于诊断"场景到底有没有在动"。</summary>
        public int RepaintCount { get; private set; }

        /// <summary>累计动画定时器触发次数。</summary>
        public int TickCount { get; private set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var b = new SolidBrush(Color.FromArgb(255, 24, 26, 32));
            e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RepaintCount++;
            ScenePainter.Paint(e.Graphics, ClientRectangle, _layout, TimeSeconds);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }
    }
}
