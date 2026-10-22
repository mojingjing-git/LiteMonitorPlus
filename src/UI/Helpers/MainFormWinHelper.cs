using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 主窗口底层助手 (Windows Helper)
    /// 职责：封装 Win32 API、窗口样式、圆角、穿透、透明度等底层操作
    /// </summary>
    public class MainFormWinHelper
    {
        private readonly Form _form;

        // ★ A13：窗口轮廓（DWM 角偏好 + Region）的**唯一写入者**是 A09 的
        //   WindowAppearanceController。本类只做转发，避免两个写入者互相打架。
        private WindowAppearanceController? _windowAppearance;

        public MainFormWinHelper(Form form)
        {
            _form = form;
        }

        /// <summary>
        /// 挂上窗口外观控制器（A09）。
        /// <para>
        /// 挂上之后，<see cref="ApplyRoundedCorners"/> 只做转发；
        /// 未挂上时走旧路径的兼容回退，保证本类被独立使用时行为不退化。
        /// </para>
        /// </summary>
        public void AttachWindowAppearance(WindowAppearanceController controller)
        {
            _windowAppearance = controller;
        }

        public void InitializeStyle(bool topMost, bool clickThrough)
        {
            _form.FormBorderStyle = FormBorderStyle.None;
            _form.ShowInTaskbar = false;
            _form.TopMost = topMost;


            
            // 解决 DoubleBuffered 访问权限问题
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(_form, true, null);

            // 原始逻辑还原：
            // 在原始代码中，Opacity 是在构造函数末尾启动异步任务来渐变的
            // 而不是在 InitializeStyle 这种早期阶段直接设为 0
            // 这里的 Opacity = 0 会导致窗体在 OnShown 之前就是透明的，
            // 但如果 SetStyle(ControlStyles.SupportsTransparentBackColor, true) 还没生效或冲突
            // 可能会导致这一瞬间的绘制异常（如黑色闪烁）
            // 
            // 修正：删除这里的 _form.Opacity = 0; 
            // 改回完全依赖 StartFadeIn 方法来控制，并且那个方法是在 OnShown 之后调用的
            
            ApplyRoundedCorners();
            if (clickThrough) SetClickThrough(true);

            // ★ A13：Resize 绑定**只有一处**（在 MainForm_Transparent 里）。
            //   这里不再重复绑定 —— 两处绑定会让窗口轮廓被无谓地重建两遍。
        }

        public bool IsTopMostStyleApplied()
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return false;

                int ex = GetWindowLong(_form.Handle, GWL_EXSTYLE);
                return (ex & WS_EX_TOPMOST) != 0;
            }
            catch
            {
                return false;
            }
        }

        public void RefreshTopMost(bool enabled, bool forceReinsert = false)
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return;

                _form.TopMost = enabled;
                uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

                if (enabled)
                {
                    // 仅在明确恢复/用户操作时重插 Z 序，避免定时器频繁抢其它置顶窗口。
                    if (forceReinsert)
                    {
                        SetWindowPos(_form.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                    }
                    SetWindowPos(_form.Handle, HWND_TOPMOST, 0, 0, 0, 0, flags);
                }
                else
                {
                    SetWindowPos(_form.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
                }
            }
            catch { }
        }

        // =================================================================
        // 透明度渐变
        // =================================================================
        public void StartFadeIn(double targetOpacity)
        {
            targetOpacity = Math.Clamp(targetOpacity, 0.1, 1.0);
            _ = Task.Run(async () =>
            {
                try
                {
                    double current = 0;
                    while (current < targetOpacity)
                    {
                        await Task.Delay(16).ConfigureAwait(false);
                        _form.BeginInvoke(new Action(() => 
                        {
                            current += 0.05;
                            if (current > targetOpacity) current = targetOpacity;
                            _form.Opacity = current;
                        }));
                        if (current >= targetOpacity) break;
                    }
                }
                catch { }
            });
        }

        // =================================================================
        // 鼠标穿透
        // =================================================================
        public void SetClickThrough(bool enable)
        {
            try
            {
                int ex = GetWindowLong(_form.Handle, GWL_EXSTYLE);
                if (enable)
                    SetWindowLong(_form.Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                else
                    SetWindowLong(_form.Handle, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
            }
            catch { }
        }

        // =================================================================
        // 【GPU T03 / 合同 §10 GPU-003 / 裁决 D4】分层窗口（WS_EX_LAYERED）读数
        // =================================================================

        /// <summary>
        /// 当前窗口是否处于**分层窗口**状态（<c>WS_EX_LAYERED</c>）。
        /// <para>★ <c>WS_EX_LAYERED</c> 与 <c>WDA_EXCLUDEFROMCAPTURE</c> **互斥**（合同 §10 GPU-003）：
        /// 分层窗口上不得申请 WDA 排除 ⇒ 期望路由由
        /// <c>RenderingRouteRequest.WindowIsLayered</c> 交给
        /// <c>RenderingRoutePolicy.Decide</c> 判定（有效路由降级为 <c>GpuFixedInput</c>，
        /// 原因码 <c>wda_capture_policy_unsupported_on_layered_window</c>）。</para>
        /// <para>★ 本方法**只读**，绝不修改 ex-style（合同 §1.3 明确否决"顺手清 WS_EX_LAYERED"）。</para>
        /// <para>★ 期望状态 = <c>Opacity &lt; 1 || ClickThrough</c>（合同 §4 步 7 / GPU-003）：
        /// 重建后新 HWND 的 <c>WS_EX_LAYERED</c> 由 <c>CreateParams</c> 按配置决定 ⇒ 旧残留**不继承**。</para>
        /// </summary>
        public bool IsWindowLayered()
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return false;
                int ex = GetWindowLong(_form.Handle, GWL_EXSTYLE);
                return (ex & WS_EX_LAYERED) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 窗口存活探针（<c>IsWindow</c>）。
        /// <para>★ 合同 §7.4：只有**同代次**才允许调用它 —— 旧代次的 HWND 数值可能被复用，
        /// <c>IsWindow</c> 无法区分"旧 HWND 已销毁"与"句柄数值被复用"。</para>
        /// </summary>
        public static bool IsWindowAlive(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            try
            {
                return IsWindow(handle);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把**原始** affinity 精确写回指定 HWND（合同 §4.5 :198）。
        /// <para>★ 恢复的是原始策略，**不能固定写成 <c>WDA_NONE</c></para> —— 由调用方传入
        /// <see cref="WindowCapturePolicyCleanup.OriginalAffinity"/> 的精确值。</para>
        /// <para>★ 调用前提：合同 §7.4 —— 该 HWND 的代次必须**等于**当前代次。</para>
        /// </summary>
        public static bool RestoreDisplayAffinity(IntPtr handle, uint originalAffinity)
        {
            if (handle == IntPtr.Zero) return false;
            try
            {
                return SetWindowDisplayAffinity(handle, originalAffinity);
            }
            catch
            {
                return false;
            }
        }

        // =================================================================
        // 圆角处理 (Hybrid: Win11 DWM / Win10 Region)
        // =================================================================
        /// <summary>
        /// 按**当前尺寸**重新应用窗口轮廓。
        /// <para>
        /// ★ A13：本方法现在是**转发**给 <see cref="WindowAppearanceController"/>（A09）——
        ///   窗口轮廓的唯一写入者是那个控制器（统一几何 + 统一的 Region 所有权），
        ///   本类不再自己写 DWM / Region，避免两套实现互相打架。
        ///   控制器尚未挂上时（旧路径）才走兼容回退 <see cref="ApplyLegacyRoundedCorners"/>。
        /// </para>
        /// </summary>
        public void ApplyRoundedCorners()
        {
            if (_windowAppearance != null)
            {
                try { _windowAppearance.Reapply(); }
                catch { }
                return;
            }

            ApplyLegacyRoundedCorners();
        }

        /// <summary>
        /// 旧路径的圆角实现（窗口外观控制器未挂上时的兼容回退）。
        /// <para>
        /// ★ 与 A09 路径的差别只有一处，但那一处很关键：
        ///   旧代码是 <c>_form.Region?.Dispose(); _form.Region = new Region(gp);</c> ——
        ///   先释放再赋值，而赋给 <c>Control.Region</c> 的实例是由 **WinForms 持有**的，
        ///   提前释放会造成悬空引用（WinForms 复制旧 Region 时源已失效）。
        ///   这里改成：先构造新 Region 并赋值，旧实例交给 WinForms 在赋值时释放。
        /// </para>
        /// </summary>
        private void ApplyLegacyRoundedCorners()
        {
            try
            {
                bool isWin11 = Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

                if (isWin11)
                {
                    _form.Region = null;
                    int preference = DWMWCP_ROUND;
                    DwmSetWindowAttribute(_form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                    int borderColor = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(_form.Handle, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                }
                else
                {
                    var t = ThemeManager.Current;
                    int r = Math.Max(0, t.Layout.CornerRadius);

                    if (r == 0)
                    {
                        _form.Region = null;
                        return;
                    }

                    using var gp = new GraphicsPath();
                    int d = r * 2;
                    gp.AddArc(0, 0, d, d, 180, 90);
                    gp.AddArc(_form.Width - d, 0, d, d, 270, 90);
                    gp.AddArc(_form.Width - d, _form.Height - d, d, d, 0, 90);
                    gp.AddArc(0, _form.Height - d, d, d, 90, 90);
                    gp.CloseFigure();

                    // ★ 不再手动释放赋给 Control.Region 的实例（那属于 WinForms）
                    _form.Region = new Region(gp);
                }
            }
            catch { }
        }

        // =================================================================
        // Win32 API
        // =================================================================
        public static int RegisterTaskbarCreatedMessage()
        {
            int msgId = RegisterWindowMessage("TaskbarCreated");
            if (msgId != 0)
            {
                ChangeWindowMessageFilter((uint)msgId, MSGFLT_ADD);
            }
            return msgId;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern int RegisterWindowMessage(string lpString);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        public const uint MSGFLT_ADD = 1;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        public static void ActivateWindow(IntPtr handle) => SetForegroundWindow(handle);
    }
}
