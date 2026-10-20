using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AppearanceBackdropProbe.Native;

namespace AppearanceBackdropProbe.Capture
{
    internal enum CaptureBackendKind
    {
        /// <summary>桌面 DC 的 BitBlt，不带 CAPTUREBLT。</summary>
        ScreenDcBitBlt = 0,
        /// <summary>桌面 DC 的 BitBlt，带 CAPTUREBLT（会抓取分层窗口）。</summary>
        ScreenDcBitBltCaptureBlt = 1,
        /// <summary>GDI+ Graphics.CopyFromScreen。</summary>
        CopyFromScreen = 2,
        /// <summary>PrintWindow（抓窗口自身内容，不是背后背景）。</summary>
        PrintWindow = 3,
    }

    internal sealed class CaptureResult
    {
        public string Backend { get; init; } = "";
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public Bitmap? Image { get; init; }
        public long WallMs { get; init; }
        public double CpuMs { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
    }

    /// <summary>
    /// 候选捕获后端。所有后端只针对"自建试验矩形"，不抓整块桌面。
    /// </summary>
    internal static class CaptureBackends
    {
        public static string NameOf(CaptureBackendKind kind) => kind switch
        {
            CaptureBackendKind.ScreenDcBitBlt => "ScreenDcBitBlt(SRCCOPY)",
            CaptureBackendKind.ScreenDcBitBltCaptureBlt => "ScreenDcBitBlt(SRCCOPY|CAPTUREBLT)",
            CaptureBackendKind.CopyFromScreen => "Graphics.CopyFromScreen",
            CaptureBackendKind.PrintWindow => "PrintWindow(PW_RENDERFULLCONTENT)",
            _ => kind.ToString(),
        };

        public static CaptureResult Capture(CaptureBackendKind kind, int x, int y, int w, int h, IntPtr targetWindow)
        {
            long t0 = Environment.TickCount64;
            var cpu0 = ProcessCpuMs();
            try
            {
                Bitmap? bmp = kind switch
                {
                    CaptureBackendKind.ScreenDcBitBlt => BitBltScreen(x, y, w, h, captureBlt: false, out var e1) ?? throw new InvalidOperationException(e1),
                    CaptureBackendKind.ScreenDcBitBltCaptureBlt => BitBltScreen(x, y, w, h, captureBlt: true, out var e2) ?? throw new InvalidOperationException(e2),
                    CaptureBackendKind.CopyFromScreen => CopyFromScreen(x, y, w, h),
                    CaptureBackendKind.PrintWindow => PrintWindowOf(targetWindow, w, h),
                    _ => throw new NotSupportedException(kind.ToString()),
                };

                return new CaptureResult
                {
                    Backend = NameOf(kind),
                    Ok = true,
                    Image = bmp,
                    WallMs = Environment.TickCount64 - t0,
                    CpuMs = ProcessCpuMs() - cpu0,
                    Width = bmp.Width,
                    Height = bmp.Height,
                };
            }
            catch (Exception ex)
            {
                return new CaptureResult
                {
                    Backend = NameOf(kind),
                    Ok = false,
                    Error = ex.Message,
                    WallMs = Environment.TickCount64 - t0,
                    CpuMs = ProcessCpuMs() - cpu0,
                    Width = w,
                    Height = h,
                };
            }
        }

        /// <summary>
        /// 进程 CPU 时间（毫秒）。注意：这不是墙钟。v4 §7.4 要求 CPUms/1000/逻辑核数×100%。
        /// </summary>
        public static double ProcessCpuMs()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                return p.TotalProcessorTime.TotalMilliseconds;
            }
            catch { return 0; }
        }

        private static Bitmap? BitBltScreen(int x, int y, int w, int h, bool captureBlt, out string error)
        {
            error = "";
            IntPtr srcDc = IntPtr.Zero, memDc = IntPtr.Zero, hbm = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                srcDc = Interop.GetDC(IntPtr.Zero);
                if (srcDc == IntPtr.Zero) { error = "GetDC(NULL) 失败"; return null; }

                memDc = Interop.CreateCompatibleDC(srcDc);
                if (memDc == IntPtr.Zero) { error = "CreateCompatibleDC 失败"; return null; }

                hbm = Interop.CreateCompatibleBitmap(srcDc, w, h);
                if (hbm == IntPtr.Zero) { error = "CreateCompatibleBitmap 失败"; return null; }

                old = Interop.SelectObject(memDc, hbm);

                uint rop = Interop.SRCCOPY | (captureBlt ? Interop.CAPTUREBLT : 0u);
                if (!Interop.BitBlt(memDc, 0, 0, w, h, srcDc, x, y, rop))
                {
                    error = $"BitBlt 失败 win32={Marshal.GetLastWin32Error()}";
                    return null;
                }

                using var img = Image.FromHbitmap(hbm);
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.DrawImage(img, new Rectangle(0, 0, w, h));
                }
                return bmp;
            }
            finally
            {
                if (memDc != IntPtr.Zero)
                {
                    if (old != IntPtr.Zero) Interop.SelectObject(memDc, old);
                    if (hbm != IntPtr.Zero) Interop.DeleteObject(hbm);
                    Interop.DeleteDC(memDc);
                }
                if (srcDc != IntPtr.Zero) Interop.ReleaseDC(IntPtr.Zero, srcDc);
            }
        }

        private static Bitmap CopyFromScreen(int x, int y, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        private static Bitmap PrintWindowOf(IntPtr hwnd, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Black);
            IntPtr hdc = g.GetHdc();
            try
            {
                bool ok = Interop.PrintWindow(hwnd, hdc, Interop.PW_RENDERFULLCONTENT);
                if (!ok) Interop.PrintWindow(hwnd, hdc, 0);
            }
            finally { g.ReleaseHdc(hdc); }
            return bmp;
        }
    }
}
