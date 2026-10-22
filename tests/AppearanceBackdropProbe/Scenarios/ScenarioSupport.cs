using System;
using System.Drawing;
using System.IO;
using AppearanceBackdropProbe.Native;
using AppearanceBackdropProbe.Probe;

namespace AppearanceBackdropProbe.Scenarios
{
    internal static class ScenarioSupport
    {
        public static Rectangle StaticRegion(Rig rig) =>
            new(0, 0, rig.Layout.ProbeWidth, (int)(rig.Layout.ProbeHeight * 0.45));

        public static Rectangle HueRegion(Rig rig) =>
            new(0, (int)(rig.Layout.ProbeHeight * 0.45), rig.Layout.ProbeWidth, (int)(rig.Layout.ProbeHeight * 0.17));

        public static Rectangle BarRegion(Rig rig) =>
            new(0, (int)(rig.Layout.ProbeHeight * 0.62), rig.Layout.ProbeWidth,
                rig.Layout.ProbeHeight - (int)(rig.Layout.ProbeHeight * 0.62));

        public static string ExStyleText(IntPtr hwnd)
        {
            int ex = Interop.GetWindowLong(hwnd, Interop.GWL_EXSTYLE);
            return $"0x{ex:X8}(LAYERED={(ex & Interop.WS_EX_LAYERED) != 0},TRANSPARENT={(ex & Interop.WS_EX_TRANSPARENT) != 0},TOOLWINDOW={(ex & Interop.WS_EX_TOOLWINDOW) != 0},TOPMOST={(ex & Interop.WS_EX_TOPMOST) != 0})";
        }

        public static bool HasLayered(IntPtr hwnd) => (Interop.GetWindowLong(hwnd, Interop.GWL_EXSTYLE) & Interop.WS_EX_LAYERED) != 0;

        public static bool HasTransparent(IntPtr hwnd) => (Interop.GetWindowLong(hwnd, Interop.GWL_EXSTYLE) & Interop.WS_EX_TRANSPARENT) != 0;

        public static double HueDistance(double a, double b)
        {
            if (a < 0 || b < 0) return -1;
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        /// <summary>场景色相循环区的理论色相（120°/秒）。</summary>
        public static double ExpectedHue(double sceneSeconds) => (sceneSeconds * 120.0) % 360.0;

        public static string ResolveScriptDir(Options opts)
        {
            if (!string.IsNullOrWhiteSpace(opts.ScriptDir) && Directory.Exists(opts.ScriptDir))
                return Path.GetFullPath(opts.ScriptDir);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir.FullName, "scripts");
                if (File.Exists(Path.Combine(candidate, "external-capture.ps1"))) return candidate;
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "scripts");
        }

        public static string BackdropTypeName(int type) => type switch
        {
            -1 => "未设置",
            Interop.DWMSBT_AUTO => "DWMSBT_AUTO(0)",
            Interop.DWMSBT_NONE => "DWMSBT_NONE(1)",
            Interop.DWMSBT_MAINWINDOW => "DWMSBT_MAINWINDOW(2)/Mica",
            Interop.DWMSBT_TRANSIENTWINDOW => "DWMSBT_TRANSIENTWINDOW(3)/Acrylic",
            Interop.DWMSBT_TABBEDWINDOW => "DWMSBT_TABBEDWINDOW(4)",
            _ => $"未知({type})",
        };
    }
}
