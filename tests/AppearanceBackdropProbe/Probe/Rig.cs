using System;
using System.Drawing;
using System.Windows.Forms;
using AppearanceBackdropProbe.Native;

namespace AppearanceBackdropProbe.Probe
{
    /// <summary>
    /// 试验台：合成背景窗口 + 探针窗口（复刻生产 MainForm 条件）。
    /// 只覆盖自建矩形，不触碰用户真实窗口与系统设置。
    /// </summary>
    internal sealed class Rig : IDisposable
    {
        public Screen Screen { get; }
        public SceneLayout Layout { get; }
        public SceneForm Scene { get; }
        public ProbeForm Probe { get; }
        public Rectangle ProbeScreenRect { get; private set; }

        public Rig(Options opts, bool layered, bool paintContent, bool clickThrough, double opacity, bool animateScene = true)
        {
            Screen = SelectScreen(opts.DisplayIndex);

            // 探针尺寸按"逻辑像素"给出，按目标显示器的 DPI 换算成物理像素（与生产窗口的 DPI 缩放一致）
            double scale = GetScreenScale(Screen);
            int margin = Math.Max(0, (int)Math.Round(opts.Margin * scale));
            int probeW = (int)Math.Round(opts.Width * scale);
            int probeH = (int)Math.Round(opts.Height * scale);

            Layout = new SceneLayout
            {
                SceneWidth = probeW + margin * 2,
                SceneHeight = probeH + margin * 2,
                Margin = margin,
                ProbeWidth = probeW,
                ProbeHeight = probeH,
            };

            int sceneX = Screen.Bounds.X + 60;
            int sceneY = Screen.Bounds.Y + 60;

            Scene = new SceneForm(Layout, animate: animateScene) { Location = new Point(sceneX, sceneY) };
            Scene.Show();
            Interop.BringToTopMost(Scene.Handle);
            Pump(300);

            Probe = new ProbeForm
            {
                UseLayeredStyle = layered,
                PaintContent = paintContent,
                ClickThrough = clickThrough,
                WindowOpacity = opacity,
                Location = new Point(sceneX + margin, sceneY + margin),
                Size = new Size(probeW, probeH),
            };
            Probe.ApplyVariant();
            Probe.Show();
            Interop.BringToTopMost(Probe.Handle);
            Pump(900);

            UpdateProbeRect();
        }

        public void UpdateProbeRect()
        {
            if (Interop.GetWindowRect(Probe.Handle, out var r))
                ProbeScreenRect = new Rectangle(r.Left, r.Top, r.Width, r.Height);
        }

        /// <summary>场景参考图（与屏幕同源绘制），已裁剪到探针窗口区域。</summary>
        public Bitmap ReferenceCrop()
        {
            using var full = ScenePainter.RenderReference(Layout, Scene.TimeSeconds);
            var rect = Layout.ProbeRect;
            rect = new Rectangle(Math.Max(0, rect.X), Math.Max(0, rect.Y),
                                 Math.Min(rect.Width, full.Width - rect.X),
                                 Math.Min(rect.Height, full.Height - rect.Y));
            return full.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }

        public static void Pump(int ms)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(8);
            }
        }

        private static Screen SelectScreen(int index)
        {
            var screens = Screen.AllScreens;
            if (screens.Length == 0) throw new InvalidOperationException("没有可用显示器");
            if (index >= 0 && index < screens.Length) return screens[index];
            foreach (var s in screens)
                if (s.Bounds.X < 0 || s.Bounds.Y < 0) return s;   // 优先负坐标显示器（覆盖支持矩阵的一格）
            return Screen.PrimaryScreen ?? screens[0];
        }

        /// <summary>目标显示器的有效 DPI 缩放（Shcore GetDpiForMonitor，MDT_EFFECTIVE_DPI）。</summary>
        private static double GetScreenScale(Screen s)
        {
            try
            {
                var pt = new Interop.POINT { X = s.Bounds.X + s.Bounds.Width / 2, Y = s.Bounds.Y + s.Bounds.Height / 2 };
                IntPtr hm = MonitorFromPoint(pt, 2);
                uint dx = 96, dy = 96;
                if (GetDpiForMonitor(hm, 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
            }
            catch { }
            return 1.0;
        }

        [System.Runtime.InteropServices.DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Interop.POINT pt, uint dwFlags);

        public void Dispose()
        {
            try { Probe?.Close(); Probe?.Dispose(); } catch { }
            try { Scene?.Close(); Scene?.Dispose(); } catch { }
        }
    }
}
