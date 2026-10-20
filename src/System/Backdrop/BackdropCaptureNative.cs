using System;
using System.Runtime.InteropServices;

namespace LiteMonitor.src.SystemServices.Backdrop
{
    // ================================================================
    // 屏幕捕获用到的最小 Win32 互操作层
    // ================================================================

    /// <summary>
    /// 实时背景捕获用到的最小 Win32 互操作。
    ///
    /// ★ 全部是文档化 API（v4 §2 明确排除未文档化调用）。
    /// ★ 只包含"抓屏 + 窗口显示亲和性 + 客户区几何 + DPI 感知"这条放行路线需要的东西；
    ///   DWM 系统材质（Mica/Acrylic）路线已在 A06 实测失败，**这里不再提供任何 DWM 入口**。
    /// </summary>
    internal static class BackdropCaptureNative
    {
        // ---------- 显示亲和性（SetWindowDisplayAffinity） ----------
        /// <summary>不排除（窗口正常出现在录屏/截图中）。</summary>
        internal const uint WDA_NONE = 0x0;

        /// <summary>
        /// 把窗口**整体**排除出捕获（A06 实测通过的唯一放行标志）。
        /// ★ 作用域是**整个窗口**，不是窗口内的某个矩形（合同 §5.3）。
        /// </summary>
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        // ---------- GetSystemMetrics ----------
        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

        // ---------- DPI 感知等级（DPI_AWARENESS，取值与 Windows SDK 的枚举一致） ----------
        /// <summary>无效（取不到，通常是老系统没有相关 API）。</summary>
        internal const int DPI_AWARENESS_INVALID = -1;

        /// <summary>不感知（坐标会被系统虚拟化，抓屏区域会错位）。</summary>
        internal const int DPI_AWARENESS_UNAWARE = 0;

        /// <summary>系统感知（坐标按系统 DPI 虚拟化，跨不同 DPI 的显示器仍会错位）。</summary>
        internal const int DPI_AWARENESS_SYSTEM = 1;

        /// <summary>每显示器感知（Per-Monitor）。</summary>
        internal const int DPI_AWARENESS_PER_MONITOR = 2;

        /// <summary>每显示器感知 V2（Per-Monitor-V2；Windows 11 推荐值）。</summary>
        internal const int DPI_AWARENESS_PER_MONITOR_V2 = 3;

        /// <summary>
        /// 坐标口径是否可信：只有**每显示器感知**的进程，
        /// <c>ClientToScreen</c> 返回的才是物理像素坐标。
        /// <para>★ 实测（本机 Win11 26300，.NET 8 WinForms）：
        /// 进程不感知 DPI 时，<c>ClientToScreen</c> 返回的是**虚拟化**坐标，
        /// 而 <c>Graphics.CopyFromScreen</c> 按**物理**坐标取屏幕内容 ⇒ 抓到的区域与窗口错位。</para>
        /// </summary>
        internal static bool HasPhysicalScreenCoordinates(int awareness)
        {
            return awareness == DPI_AWARENESS_PER_MONITOR || awareness == DPI_AWARENESS_PER_MONITOR_V2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        // ---------- user32 ----------
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

        /// <summary>
        /// 取客户区在**屏幕坐标系**里的矩形（物理像素，可为负坐标）。
        /// <para>★ 负坐标是真实场景：副屏在主屏左侧时客户区 X 就是负数
        /// （A06 实测通过，本机 DISPLAY2 位于 X = −1440）。</para>
        /// <para>★ 坐标必须是**物理像素**：进程需是 DPI 感知的，
        /// 否则系统会把坐标虚拟化，返回的矩形与实际不符。</para>
        /// </summary>
        /// <param name="hwnd">顶层窗口句柄。</param>
        /// <param name="bounds">成功时为客户区的屏幕矩形。</param>
        /// <returns>取到有效矩形时为 true。</returns>
        internal static bool TryGetClientScreenBounds(IntPtr hwnd, out System.Drawing.Rectangle bounds)
        {
            bounds = System.Drawing.Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetClientRect(hwnd, out RECT client)) return false;
            if (client.Width <= 0 || client.Height <= 0) return false;

            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) return false;

            bounds = new System.Drawing.Rectangle(origin.X, origin.Y, client.Width, client.Height);
            return true;
        }

        /// <summary>取虚拟桌面（所有显示器并集）在屏幕坐标系里的矩形。</summary>
        internal static System.Drawing.Rectangle GetVirtualScreenBounds()
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0) return System.Drawing.Rectangle.Empty;
            return new System.Drawing.Rectangle(x, y, w, h);
        }

        /// <summary>
        /// 取当前线程的 DPI 感知等级。
        /// <para>取不到（老系统没有这两个 API）时返回 <see cref="DPI_AWARENESS_INVALID"/>，
        /// 调用方据此"不阻塞"而不是"判定失败"。</para>
        /// </summary>
        internal static int GetDpiAwareness()
        {
            try
            {
                IntPtr context = GetThreadDpiAwarenessContext();
                if (context == IntPtr.Zero) return DPI_AWARENESS_INVALID;
                int awareness = GetAwarenessFromDpiAwarenessContext(context);
                return awareness <= DPI_AWARENESS_INVALID ? DPI_AWARENESS_INVALID : awareness;
            }
            catch (DllNotFoundException)
            {
                return DPI_AWARENESS_INVALID;
            }
            catch (EntryPointNotFoundException)
            {
                return DPI_AWARENESS_INVALID;
            }
        }

        /// <summary>取窗口所在显示器的实际 DPI（96 = 100%）。</summary>
        internal static uint GetWindowDpi(IntPtr hwnd)
        {
            try
            {
                return GetDpiForWindow(hwnd);
            }
            catch (DllNotFoundException)
            {
                return 96;
            }
            catch (EntryPointNotFoundException)
            {
                return 96;
            }
        }
    }
}
