using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 任务栏集成策略接口
    /// 定义了不同系统版本下任务栏挂载和布局的统一行为
    /// </summary>
    public interface ITaskbarStrategy
    {
        /// <summary>
        /// 是否准备就绪
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// 挂载到任务栏
        /// </summary>
        void Attach(IntPtr taskbarHandle);

        /// <summary>
        /// 设置位置和大小
        /// </summary>
        void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft);

        /// <summary>
        /// 恢复任务栏原始布局（如果做过修改）
        /// </summary>
        void Restore();

        /// <summary>
        /// 获取期望的父窗口句柄（用于检测是否脱离）
        /// </summary>
        IntPtr GetExpectedParent(IntPtr taskbarHandle);

        /// <summary>
        /// 是否拥有内部布局逻辑（如 Win10 挤占模式）
        /// </summary>
        bool HasInternalLayout { get; }
    }

    internal static class NativeMethods
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_EX_LAYERED = 0x80000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const uint LWA_COLORKEY = 0x00000001;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        /// <summary>SetWindowPos 的 hWndInsertAfter：置顶到同级最前（不激活）。</summary>
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int idx, int value);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Auto)] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hWnd);
    }

    /// <summary>
    /// 任务栏窗口底层助手 (Windows Helper) - Facade
    /// 职责：作为统一入口，根据系统版本委托给具体的策略 (Strategy) 处理挂载和布局
    /// </summary>
    public class TaskbarWinHelper
    {
        private readonly Form _form;
        private readonly ITaskbarStrategy _strategy;

        // ★★★ 性能优化缓存 ★★★
        private Rectangle _lastWindowRect = Rectangle.Empty;
        private Rectangle _cachedResult = Rectangle.Empty;
        private bool _isCacheValid = false;

        // ★ 缓存键的其余分量（P-F04 / #539）：任务栏 HWND 可能在显示器、工作区、DPI 变化后
        //   **继续有效**，旧缓存只比较物理矩形 ⇒ 会一直复用过期坐标直到整个程序重启。
        private IntPtr _lastTaskbarHandle = IntPtr.Zero;
        private string _lastTargetDevice = string.Empty;
        private Rectangle _lastScreenBounds = Rectangle.Empty;
        private Rectangle _lastWorkingArea = Rectangle.Empty;
        private uint _lastTaskbarDpi = 96;

        // ★ 显示器拓扑来源（可注入 ⇒ 多屏/副屏缺失的判定可被真实调用验证）。
        private readonly ITaskbarScreenProvider _screens;

        // ★ 任务栏窗口查找来源（可注入 ⇒ 解析逻辑可在**真实测试 HWND** 上验证，
        //   且测试过程绝不触碰真实的 Shell_TrayWnd）。
        private readonly ITaskbarWindowSource _windows;

        // [Optimization] 静态缓存系统版本检测结果
        private static readonly bool _isWin11 = Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build >= 22000;

        public bool UsesInternalLayout => _strategy.HasInternalLayout;

        /// <summary>当前使用的显示器拓扑来源。</summary>
        public ITaskbarScreenProvider Screens => _screens;

        public TaskbarWinHelper(Form form, ITaskbarScreenProvider? screens = null, ITaskbarWindowSource? windows = null)
        {
            _form = form;
            _screens = screens ?? WinFormsTaskbarScreenProvider.Instance;
            _windows = windows ?? Win32TaskbarWindowSource.Instance;
            // 策略工厂模式：根据系统版本选择合适的集成策略
            if (_isWin11)
            {
                _strategy = new TaskbarStrategyWin11(form);
            }
            else
            {
                _strategy = new TaskbarStrategyWin10(form);
            }
        }

        // =================================================================
        // 样式与图层
        // =================================================================
        public void ApplyLayeredStyle(Color transparentKey, bool clickThrough)
        {
            _form.BackColor = transparentKey;
            
            if (_form.IsHandleCreated)
            {
                uint colorKey = (uint)(transparentKey.R | (transparentKey.G << 8) | (transparentKey.B << 16));
                SetLayeredWindowAttributes(_form.Handle, colorKey, 0, LWA_COLORKEY);
            }

            int exStyle = GetWindowLong(_form.Handle, GWL_EXSTYLE);
            if (clickThrough) exStyle |= WS_EX_TRANSPARENT; 
            else exStyle &= ~WS_EX_TRANSPARENT; 
            SetWindowLong(_form.Handle, GWL_EXSTYLE, exStyle);
            
            _form.Invalidate();
        }

        public bool IsSystemLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    object? val = key.GetValue("SystemUsesLightTheme");
                    if (val is int i) return i == 1;
                }
            }
            catch { }
            return false;
        }

        // =================================================================
        // 挂载逻辑 (委托给策略)
        // =================================================================
        public void AttachToTaskbar(IntPtr taskbarHandle)
        {
            _strategy.Attach(taskbarHandle);

            // Explorer 在启动/唤醒时可能保留父子关系，却把窗口排到新的任务栏层后面，
            // 或者在句柄重建后把新窗口留在不可见状态。
            // 每次恢复集成时重新确认可见性与同级位置（不激活、不移动、不改大小）。
            if (_form.IsHandleCreated && IsWindow(_form.Handle))
            {
                SetWindowPos(
                    _form.Handle,
                    HWND_TOP,
                    0,
                    0,
                    0,
                    0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }

        /// <summary>
        /// 期望的父窗口句柄（策略相关：Win11 = 任务栏根，Win10 = 任务栏根）。
        /// </summary>
        public IntPtr GetExpectedParent(IntPtr taskbarHandle)
        {
            return _strategy.GetExpectedParent(taskbarHandle);
        }

        /// <summary>
        /// 面板当前是否真的挂在给定任务栏下（**实际父子关系**，不只是"根 HWND 非零"）。
        /// </summary>
        public bool IsAttachedToTaskbar(IntPtr taskbarHandle)
        {
            if (taskbarHandle == IntPtr.Zero) return false;
            if (!_form.IsHandleCreated || !IsWindow(_form.Handle)) return false;

            IntPtr expectedParent = _strategy.GetExpectedParent(taskbarHandle);
            if (expectedParent == IntPtr.Zero || !IsWindow(expectedParent)) return false;

            return GetParent(_form.Handle) == expectedParent;
        }

        /// <summary>任务栏矩形缓存失效（恢复 / 显示变化后必须调用）。</summary>
        public void InvalidateTaskbarRectCache()
        {
            _isCacheValid = false;
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset = 0, bool alignLeft = true)
        {
            // 检查父窗口是否正确，防止脱离
            IntPtr currentParent = GetParent(_form.Handle);
            IntPtr expectedParent = _strategy.GetExpectedParent(taskbarHandle);

            if (currentParent != expectedParent)
            {
                AttachToTaskbar(taskbarHandle);
            }

            _strategy.SetPosition(taskbarHandle, left, top, w, h, manualOffset, alignLeft);
        }

        public void RestoreTaskbar()
        {
            _strategy.Restore();
        }

        // =================================================================
        // 句柄与信息获取 (通用逻辑)
        // =================================================================

        /// <summary>
        /// 查找目标显示器。找不到返回 null（**不回退主屏**）。
        /// </summary>
        public TaskbarMonitorInfo? FindMonitor(string? targetDevice)
        {
            if (string.IsNullOrEmpty(targetDevice)) return null;
            foreach (var monitor in _screens.GetMonitors())
            {
                if (monitor.DeviceName == targetDevice) return monitor;
            }
            return null;
        }

        /// <summary>主屏显示器信息（自动模式使用）。</summary>
        public TaskbarMonitorInfo? FindPrimaryMonitor()
        {
            var monitors = _screens.GetMonitors();
            foreach (var monitor in monitors)
            {
                if (monitor.IsPrimary) return monitor;
            }
            return monitors.Count > 0 ? monitors[0] : null;
        }

        /// <summary>
        /// 解析目标任务栏句柄。
        /// <para>★ [Fix #573 / #583] 指定了目标设备但**目标设备不存在**时返回
        /// <see cref="IntPtr.Zero"/>，**不再回退到主屏**。
        /// 旧实现回退主屏 ⇒ 拿到的句柄有效 ⇒ <c>IsTaskbarValid()</c> 恒真 ⇒
        /// 永不重解析 ⇒ 开机竞态与 RDP 断开后永久锁死在错误屏幕上。</para>
        /// </summary>
        public (IntPtr hTaskbar, IntPtr hTray) FindHandles(string? targetDevice)
        {
            if (!string.IsNullOrEmpty(targetDevice))
            {
                var target = FindMonitor(targetDevice);
                // ★ 目标设备缺失 ⇒ 交回"等待"状态，而不是偷偷绑主屏。
                if (target == null) return (IntPtr.Zero, IntPtr.Zero);

                if (target.Value.IsPrimary)
                {
                    IntPtr hTaskbar = _windows.FindWindow("Shell_TrayWnd", null);
                    IntPtr hTray = _windows.FindWindowEx(hTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                    return (hTaskbar, hTray);
                }
                else
                {
                    IntPtr hTaskbar = FindSecondaryTaskbar(target.Value);
                    return (hTaskbar, IntPtr.Zero);
                }
            }

            // 自动模式（未指定目标设备）：使用主屏
            {
                IntPtr hTaskbar = _windows.FindWindow("Shell_TrayWnd", null);
                IntPtr hTray = _windows.FindWindowEx(hTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                return (hTaskbar, hTray);
            }
        }

        private IntPtr FindSecondaryTaskbar(TaskbarMonitorInfo screen)
        {
            IntPtr hWnd = IntPtr.Zero;
            while ((hWnd = _windows.FindWindowEx(IntPtr.Zero, hWnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                GetWindowRect(hWnd, out RECT rect);
                Rectangle r = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
                if (screen.Bounds.Contains(r.Location) || screen.Bounds.IntersectsWith(r))
                    return hWnd;
            }
            // ★ [Fix #573 / #583] 副屏任务栏还没就绪 ⇒ 返回 Zero，
            //   让上层进入"等待目标任务栏"状态并持续重试，**不回退主屏**。
            return IntPtr.Zero;
        }

        public Rectangle GetTaskbarRect(IntPtr hTaskbar, string? targetDevice)
        {
            if (hTaskbar == IntPtr.Zero) return Rectangle.Empty;

            // 1. 获取物理矩形
            if (!GetWindowRect(hTaskbar, out RECT r)) return Rectangle.Empty;
            var rectPhys = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);

            // 2. 缓存键的其余分量（#539）：任务栏 HWND 可能在显示器、工作区、DPI 变化后继续有效，
            //    旧缓存只比较物理矩形 ⇒ 切分辨率后一直复用过期坐标直到进程重启。
            TaskbarMonitorInfo? screen = null;
            Rectangle screenBounds = Rectangle.Empty;
            Rectangle workingArea = Rectangle.Empty;
            uint taskbarDpi = 96;

            try
            {
                screen = FindMonitor(targetDevice);
                screen ??= _screens.GetMonitorForHandle(hTaskbar);

                if (screen.HasValue)
                {
                    screenBounds = screen.Value.Bounds;
                    workingArea = screen.Value.WorkingArea;
                }

                taskbarDpi = GetDpiForWindow(hTaskbar);
                if (taskbarDpi == 0) taskbarDpi = 96;
            }
            catch { }

            if (_isCacheValid &&
                hTaskbar == _lastTaskbarHandle &&
                string.Equals(targetDevice ?? string.Empty, _lastTargetDevice, StringComparison.Ordinal) &&
                rectPhys == _lastWindowRect &&
                screenBounds == _lastScreenBounds &&
                workingArea == _lastWorkingArea &&
                taskbarDpi == _lastTaskbarDpi)
            {
                return _cachedResult;
            }

            Rectangle finalRect = rectPhys;

            // =========================================================================
            // [SIMPLIFIED FIX] 极简稳定方案：基于 WorkingArea 和 强制高度修正 (DPI适配版)
            // =========================================================================
            try
            {
                if (screen.HasValue)
                {
                    int reservedBottom = screenBounds.Bottom - workingArea.Bottom;

                    // 场景 A: 锚定模式
                    if (reservedBottom > 2)
                    {
                        finalRect = new Rectangle(rectPhys.Left, workingArea.Bottom, rectPhys.Width, reservedBottom);
                    }
                    // 场景 B: 悬浮模式
                    else
                    {
                        if (_isWin11)
                        {
                            // [Fix] 增加对垂直任务栏的判断，防止误判
                            // StartAllBack 等软件可能启用垂直任务栏，此时不应应用底部水平任务栏的高度修正
                            bool isVertical = rectPhys.Height > rectPhys.Width;

                            if (!isVertical)
                            {
                                int standardHeight = (int)Math.Round(48.0 * taskbarDpi / 96.0);

                                if (rectPhys.Height > (standardHeight * 0.8))
                                {
                                    finalRect = new Rectangle(
                                        rectPhys.Left,
                                        rectPhys.Bottom - standardHeight,
                                        rectPhys.Width,
                                        standardHeight);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            _lastWindowRect = rectPhys;
            _cachedResult = finalRect;
            _lastTaskbarHandle = hTaskbar;
            _lastTargetDevice = targetDevice ?? string.Empty;
            _lastScreenBounds = screenBounds;
            _lastWorkingArea = workingArea;
            _lastTaskbarDpi = taskbarDpi;
            _isCacheValid = true;

            return _cachedResult;
        }

        public bool GetWindowRectWrapper(IntPtr hWnd, out Rectangle rect)
        {
            if (GetWindowRect(hWnd, out RECT r))
            {
                rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
                return true;
            }
            rect = Rectangle.Empty;
            return false;
        }

        public static bool IsCenterAligned()
        {
            if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 22000) 
                return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                return ((int)(key?.GetValue("TaskbarAl", 1) ?? 1)) == 1;
            }
            catch { return false; }
        }

        public static int GetTaskbarDpi()
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                try { return (int)GetDpiForWindow(taskbar); } catch { }
            }
            return 96;
        }

        public static int GetWidgetsWidth()
        {
            int dpi = GetTaskbarDpi();
            if (_isWin11)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pkg = Path.Combine(local, "Packages");
                bool hasWidgetPkg = false;
                try { hasWidgetPkg = Directory.GetDirectories(pkg, "MicrosoftWindows.Client.WebExperience*").Any(); } catch {}
                
                if (!hasWidgetPkg) return 0;

                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key == null) return 0;

                object? val = key.GetValue("TaskbarDa");
                if (val is int i && i != 0) return 150 * dpi / 96;
            }
            return 0;
        }

        public static void ActivateWindow(IntPtr handle) => SetForegroundWindow(handle);

        public static bool IsWindow(IntPtr hWnd) => NativeMethods.IsWindow(hWnd);

        public static void ApplyChildWindowStyle(IntPtr hWnd)
        {
            int style = GetWindowLong(hWnd, GWL_STYLE);
            style &= (int)~0x80000000; 
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            SetWindowLong(hWnd, GWL_STYLE, style);
        }
    }
}
