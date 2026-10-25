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
        /// <para>★ 坐标契约（P-F05）：<paramref name="left"/> / <paramref name="top"/> 一律为
        /// <b>物理屏幕像素</b>；策略内部负责把它<b>显式转换</b>到父窗口客户区坐标。
        /// 策略自带内部布局（Win10 挤占）时，传入的坐标会被忽略，由策略自行计算。</para>
        /// </summary>
        /// <param name="direction">停靠方向（显式枚举，替代宽高比猜测）。</param>
        void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft, TaskbarDockDirection direction);

        /// <summary>
        /// 恢复任务栏原始布局（如果做过修改）
        /// </summary>
        void Restore();

        /// <summary>
        /// 获取期望的父窗口句柄（用于检测是否脱离）
        /// </summary>
        IntPtr GetExpectedParent(IntPtr taskbarHandle);

        /// <summary>
        /// 策略内部的"子宿主"句柄（Win10 = ReBarWindow32；Win11 = 任务栏根窗口本身）。
        /// <para>★ 该句柄参与<b>几何缓存键</b>：Explorer 重建子层时句柄必变 ⇒ 缓存整体失效。</para>
        /// </summary>
        IntPtr GetInnerHostHandle();

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

        /// <summary>GetWindow 的关系命令：取 Z 序上"前一个"（更靠前）的同级窗口。</summary>
        public const uint GW_HWNDPREV = 3;

        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

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
        // ★ P-F05：缓存单位从"一个矩形"升级为"一份几何快照"，
        //   缓存键覆盖：根/子 HWND 代次、目标显示器、屏幕/工作区矩形、DPI、方向、
        //   面板尺寸、相关任务栏设置 —— 任一变化**整体失效**（#539）。
        private TaskbarGeometryKey _lastGeometryKey;
        private TaskbarGeometry _cachedGeometry;
        private bool _isCacheValid = false;

        // ★ 定位上下文缓存键里的"面板尺寸 / 任务栏设置"分量。
        //   每次 SetPosition 更新；GetTaskbarRect 作为兼容入口复用同一份上下文，
        //   避免两个入口互相把对方的缓存条目顶掉。
        private int _ctxPanelWidth;
        private int _ctxPanelHeight;
        private int _ctxManualOffset;
        private bool _ctxAlignLeft = true;

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
                // ★ 策略需要按类名查找内部子窗口（ReBarWindow32 / MSTaskSwWClass），
                //   走可注入来源 ⇒ 测试可在**真实测试 HWND** 上驱动它，
                //   且绝不触碰真实的 Shell_TrayWnd。
                _strategy = new TaskbarStrategyWin10(form, _windows);
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
        /// 【P-F06 / #599】显示变化之后<b>立即</b>检查一次同级 Z 序。
        /// <para>★ 合成层在显示变化那一刻往往已被重建 ⇒ 不能只等下一次定位周期。</para>
        /// </summary>
        public void RequestDisplayChangeCheck()
        {
            if (_strategy is TaskbarStrategyWin11 win11)
            {
                win11.RequestDisplayChangeCheck();
            }
        }

        /// <summary>
        /// 【P-F06 / #599】维护周期内的一次低成本同级 Z 序检查（由 <c>TaskbarForm.Tick</c> 调用）。
        /// <para>★ 只在"还有后续检查配额"时才真的看一眼，配额由定位 / 显示变化排定，
        /// 用完即止 ⇒ 不会被放大成每帧无条件重排。</para>
        /// </summary>
        public void PumpZOrderMaintenance()
        {
            if (_strategy is TaskbarStrategyWin11 win11)
            {
                win11.PumpZOrderMaintenance();
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

        /// <summary>
        /// 设置"定位上下文"（面板尺寸 / 有效对齐 / 手动偏移）—— 参与几何缓存键。
        /// <para>★ 由 <see cref="TaskbarBizHelper"/> 统一维护：几何解析与最终定位
        /// 必须使用**同一份**上下文，否则两个入口会互相把对方的缓存条目顶掉。</para>
        /// </summary>
        public void SetPlacementContext(int panelWidth, int panelHeight, bool alignLeft, int manualOffset)
        {
            _ctxPanelWidth = panelWidth;
            _ctxPanelHeight = panelHeight;
            _ctxAlignLeft = alignLeft;
            _ctxManualOffset = manualOffset;
        }

        /// <summary>
        /// 定位面板。
        /// <para>★ 坐标契约（P-F05）：<paramref name="left"/> / <paramref name="top"/> 为
        /// <b>物理屏幕像素</b>；策略负责显式转换成父窗口客户区坐标。</para>
        /// </summary>
        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset = 0, bool alignLeft = true, TaskbarDockDirection direction = TaskbarDockDirection.Unknown)
        {
            // 检查父窗口是否正确，防止脱离
            IntPtr currentParent = GetParent(_form.Handle);
            IntPtr expectedParent = _strategy.GetExpectedParent(taskbarHandle);

            if (currentParent != expectedParent)
            {
                AttachToTaskbar(taskbarHandle);
            }

            _strategy.SetPosition(taskbarHandle, left, top, w, h, manualOffset, alignLeft, direction);
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

        /// <summary>
        /// 任务栏矩形（<b>物理屏幕像素</b>）—— 兼容入口。
        /// <para>★ 复用"最近一次的定位上下文"参与缓存键，因此与
        /// <see cref="GetGeometry"/> 共享同一份缓存，不会互相顶掉。</para>
        /// </summary>
        public Rectangle GetTaskbarRect(IntPtr hTaskbar, string? targetDevice)
        {
            return GetGeometry(
                hTaskbar,
                targetDevice,
                _ctxPanelWidth,
                _ctxPanelHeight,
                _ctxAlignLeft,
                _ctxManualOffset).TaskbarRect;
        }

        /// <summary>
        /// 解析任务栏几何快照（★ P-F05：方向 + 矩形 + 完整缓存键）。
        /// <para>★ 坐标空间：本方法内所有矩形均为<b>物理屏幕像素</b>。</para>
        /// </summary>
        /// <param name="hTaskbar">任务栏根窗口句柄。</param>
        /// <param name="targetDevice">目标显示器设备名（空 = 自动 / 主屏）。</param>
        /// <param name="panelWidth">面板宽度（物理像素）—— 参与缓存键。</param>
        /// <param name="panelHeight">面板高度（物理像素）—— 参与缓存键。</param>
        /// <param name="alignLeft">有效对齐（靠左）—— 参与缓存键。</param>
        /// <param name="manualOffset">手动偏移（物理像素）—— 参与缓存键。</param>
        public TaskbarGeometry GetGeometry(IntPtr hTaskbar, string? targetDevice, int panelWidth, int panelHeight, bool alignLeft, int manualOffset)
        {
            if (hTaskbar == IntPtr.Zero) return TaskbarGeometry.Empty;

            // 1. 获取物理矩形
            if (!GetWindowRect(hTaskbar, out RECT r)) return TaskbarGeometry.Empty;
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

            // 3. ★ 停靠方向：用任务栏**相对所属显示器**的物理矩形解析（不用宽高比猜）。
            //    所属显示器 = 真正"拥有"这块任务栏的那块屏（而不是用户指定的那块）。
            TaskbarMonitorInfo? owner = null;
            try { owner = _screens.GetMonitorForHandle(hTaskbar); }
            catch { }
            Rectangle ownerBounds = owner?.Bounds ?? screenBounds;
            Rectangle ownerWork = owner?.WorkingArea ?? workingArea;
            TaskbarDockDirection direction = TaskbarDockResolver.Resolve(rectPhys, ownerBounds, ownerWork);

            // 4. 任务栏设置（参与缓存键）
            bool centerAligned = IsCenterAligned();
            int widgetsWidth = GetWidgetsWidth();

            var key = new TaskbarGeometryKey(
                hTaskbar,
                _strategy.GetInnerHostHandle(),
                targetDevice ?? string.Empty,
                screenBounds,
                workingArea,
                taskbarDpi,
                direction,
                panelWidth,
                panelHeight,
                alignLeft,
                manualOffset,
                centerAligned,
                widgetsWidth);

            if (_isCacheValid && key == _lastGeometryKey) return _cachedGeometry;

            Rectangle finalRect = ComputeTaskbarRect(rectPhys, screenBounds, workingArea, direction);

            // =========================================================================
            // 生效的任务栏矩形（锚定方向 ⇒ 用工作区预留的条带；否则用物理矩形）
            // =========================================================================
            var geometry = new TaskbarGeometry(
                finalRect,
                direction,
                screenBounds,
                workingArea,
                taskbarDpi,
                owner?.DeviceName ?? targetDevice ?? string.Empty,
                panelWidth,
                panelHeight,
                alignLeft,
                manualOffset,
                centerAligned,
                widgetsWidth,
                TaskbarDockResolver.ResolveCompatibility(direction));

            _lastGeometryKey = key;
            _cachedGeometry = geometry;
            _isCacheValid = true;

            return geometry;
        }

        /// <summary>
        /// 由任务栏的物理矩形 + 显示器信息算出"生效的任务栏矩形"。
        /// <para>★ 不使用任何"高度启发式"（母计划 F05 第 2 步：未经本仓验证的
        /// 20～100px 阈值不得直接采用）⇒ 多行任务栏 / 高 DPI 不会被误裁。</para>
        /// </summary>
        private static Rectangle ComputeTaskbarRect(Rectangle phys, Rectangle screenBounds, Rectangle workingArea, TaskbarDockDirection direction)
        {
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return phys;

            int reservedTop = workingArea.Top - screenBounds.Top;
            int reservedBottom = screenBounds.Bottom - workingArea.Bottom;
            int reservedLeft = workingArea.Left - screenBounds.Left;
            int reservedRight = screenBounds.Right - workingArea.Right;

            switch (direction)
            {
                case TaskbarDockDirection.Bottom:
                    if (reservedBottom > 2) return new Rectangle(phys.Left, workingArea.Bottom, phys.Width, reservedBottom);
                    break;
                case TaskbarDockDirection.Top:
                    if (reservedTop > 2) return new Rectangle(phys.Left, screenBounds.Top, phys.Width, reservedTop);
                    break;
                case TaskbarDockDirection.Left:
                    if (reservedLeft > 2) return new Rectangle(screenBounds.Left, phys.Top, reservedLeft, phys.Height);
                    break;
                case TaskbarDockDirection.Right:
                    if (reservedRight > 2) return new Rectangle(workingArea.Right, phys.Top, reservedRight, phys.Height);
                    break;
            }

            // 未锚定（自动隐藏 / 悬浮 / 方向未知）⇒ 直接采用物理矩形。
            return phys;
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

        /// <summary>
        /// 把一个**子窗口**提升到<b>同级最前</b>（<c>HWND_TOP</c>）—— 不移动、不改大小、不激活。
        /// <para>★ 这是"同级 Z 序维护"的<b>唯一</b>写法：面板是任务栏的<b>子窗口</b>，
        /// 正确语义只到 <c>HWND_TOP</c>（同级最前），<b>绝不</b>是顶级窗口的
        /// <c>HWND_TOPMOST</c>（那会把面板从任务栏层级里拔出来，压住整个桌面）。</para>
        /// </summary>
        public static bool RaiseSiblingToTop(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            return SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>
        /// 该窗口的<b>同级 Z 序</b>是否已经被可见窗口挡住（前方存在可见的同级窗口）。
        /// <para>★ 判定只沿 <c>GW_HWNDPREV</c> 链走 —— 这是"同级"Z 序的<b>真实</b>定义，
        /// 不需要遍历系统窗口（母计划 F06 第 2 步明确禁止遍历 / 每帧无条件置顶）。</para>
        /// <para>★ 链上可能有不可见的占位窗口（隐藏层、零面积层），它们<b>不会</b>遮挡内容
        /// ⇒ 只看可见窗口，避免"明明没被挡却反复重排"的抖动。</para>
        /// </summary>
        public static bool IsCoveredByVisibleSibling(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;

            IntPtr previous = GetWindow(hWnd, GW_HWNDPREV);
            return previous != IntPtr.Zero && IsWindowVisible(previous);
        }

        /// <summary>
        /// 按需恢复同级 Z 序：<b>只有确实被可见同级窗口挡住时</b>才重排。
        /// <para>★ 为什么必须"按需"：无条件每帧 <c>HWND_TOP</c> 会与任务栏自身的合成层
        /// （以及第三方任务栏）互相抢占，表现为闪烁。这里先判再动，
        /// 且重排只带 NOMOVE / NOSIZE / NOACTIVATE —— 矩形与焦点都不受影响。</para>
        /// </summary>
        /// <returns>是否真的执行过一次重排。</returns>
        public static bool RaiseSiblingToTopIfCovered(IntPtr hWnd)
        {
            if (!IsCoveredByVisibleSibling(hWnd)) return false;
            return RaiseSiblingToTop(hWnd);
        }

        public static void ApplyChildWindowStyle(IntPtr hWnd)
        {
            int style = GetWindowLong(hWnd, GWL_STYLE);
            style &= (int)~0x80000000; 
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            SetWindowLong(hWnd, GWL_STYLE, style);
        }

        // =================================================================
        // 坐标空间转换（P-F05：屏幕物理像素 ⇄ 父窗口客户区坐标）
        // =================================================================

        /// <summary>
        /// 把一个<b>物理屏幕坐标</b>显式转换成父窗口的<b>客户区坐标</b>。
        /// <para>★ 这是"坐标契约"的唯一入口：所有最终定位都必须经过它，
        /// 不得再出现"拿屏幕坐标当客户区坐标用"的混算。</para>
        /// </summary>
        /// <returns>false 表示转换失败（此时输出退化为输入值）。</returns>
        public static bool TryScreenToClient(IntPtr parent, int screenX, int screenY, out int clientX, out int clientY)
        {
            clientX = screenX;
            clientY = screenY;
            if (parent == IntPtr.Zero) return false;

            POINT pt = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(parent, ref pt)) return false;

            clientX = pt.X;
            clientY = pt.Y;
            return true;
        }

        /// <summary>
        /// 把一个<b>物理屏幕矩形</b>显式转换成父窗口的<b>客户区矩形</b>
        /// （尺寸不变，只平移原点）。
        /// </summary>
        public static bool TryScreenRectToClient(IntPtr parent, Rectangle screenRect, out Rectangle clientRect)
        {
            if (!TryScreenToClient(parent, screenRect.Left, screenRect.Top, out int x, out int y))
            {
                clientRect = Rectangle.Empty;
                return false;
            }
            clientRect = new Rectangle(x, y, screenRect.Width, screenRect.Height);
            return true;
        }
    }
}
