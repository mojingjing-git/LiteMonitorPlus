using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 【P-F04】真实 Win32 测试宿主。
    /// <para>★ 为什么必须自建：本仓库的 <c>.tmp/verification/</c> 里**没有**任何 Win32 探针
    /// （只有 webprobe），而 P-F04 的验收要求"真实父/子窗口销毁、重建"，不是模拟调用。</para>
    /// <para>这里用系统预置窗口类 <c>STATIC</c> 创建**真实**的 HWND：</para>
    /// <list type="bullet">
    /// <item>根窗口 = 顶层 popup，模拟 <c>Shell_TrayWnd</c> / <c>Shell_SecondaryTrayWnd</c>；</item>
    /// <item>内层子窗口 = <c>WS_CHILD</c>，模拟托盘 / ReBar 层；</item>
    /// <item>面板 = 真实的 WinForms <c>Form</c>（真实 HWND），被 <c>SetParent</c> 挂到根窗口下。</item>
    /// </list>
    /// <para>★ 纪律：宿主**只创建并销毁自己的窗口**，从不查找、也从不触碰真实的
    /// <c>Shell_TrayWnd</c>；窗口放在屏幕外，避免干扰用户桌面。</para>
    /// </summary>
    internal sealed class TaskbarWin32TestHost : IDisposable
    {
        private const string HostWindowClass = "STATIC";   // 系统预置类，无需注册 WndProc

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CHILD = 0x40000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private readonly List<IntPtr> _windows = new List<IntPtr>();
        private bool _disposed;

        /// <summary>创建根窗口（顶层 popup）。</summary>
        public IntPtr CreateRootWindow(Rectangle bounds, string name = "LiteMonitorTaskbarHost")
        {
            IntPtr hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW, HostWindowClass, name,
                WS_POPUP | WS_VISIBLE,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx(根窗口) 失败，Win32 错误码 = {Marshal.GetLastWin32Error()}");
            }

            _windows.Add(hwnd);
            return hwnd;
        }

        /// <summary>在内层创建子窗口（WS_CHILD）。</summary>
        public IntPtr CreateInnerChild(IntPtr root, string name = "LiteMonitorTaskbarInner")
        {
            IntPtr hwnd = CreateWindowEx(
                0, HostWindowClass, name,
                WS_CHILD | WS_VISIBLE,
                0, 0, 120, 24,
                root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx(内层子窗口) 失败，Win32 错误码 = {Marshal.GetLastWin32Error()}");
            }

            _windows.Add(hwnd);
            return hwnd;
        }

        /// <summary>
        /// 销毁根窗口 —— 真实的 <c>DestroyWindow</c>。
        /// <para>★ Windows 语义：父窗口销毁 ⇒ **子窗口一并销毁**（P5 探针已实测的机制基石）。</para>
        /// </summary>
        public void DestroyRoot(IntPtr root)
        {
            if (root == IntPtr.Zero) return;
            DestroyWindow(root);
        }

        /// <summary>
        /// 保留根窗口、重建内层子窗口（模拟 Explorer 重建子层但保留根 HWND）。
        /// </summary>
        public IntPtr RebuildInnerLayer(IntPtr root, IntPtr oldInner, string name = "LiteMonitorTaskbarInner")
        {
            if (oldInner != IntPtr.Zero && IsWindow(oldInner))
            {
                DestroyWindow(oldInner);
            }
            return CreateInnerChild(root, name);
        }

        /// <summary>把面板挂到指定根窗口下（与产品路径一致的挂载语义）。</summary>
        public static void AttachPanel(IntPtr panel, IntPtr root)
        {
            SetParent(panel, root);
        }

        public static bool IsAlive(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindow(hWnd);

        public static IntPtr ParentOf(IntPtr hWnd) => GetParent(hWnd);

        public static bool IsVisible(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindowVisible(hWnd);

        public static Rectangle GetBounds(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out RECT rect)) return Rectangle.Empty;
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 先销毁根窗口（子窗口随之销毁），再兜底清理残留。
            for (int i = _windows.Count - 1; i >= 0; i--)
            {
                IntPtr hwnd = _windows[i];
                if (hwnd != IntPtr.Zero && IsWindow(hwnd))
                {
                    DestroyWindow(hwnd);
                }
            }
            _windows.Clear();
        }
    }
}
