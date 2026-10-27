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

        /// <summary>
        /// 置顶样式位（<c>WS_EX_TOPMOST</c>）是否已应用。
        /// <para>★ virtual 的原因：与 <see cref="RefreshTopMost"/> 同理 —— 置顶看门狗的
        /// 双判逻辑需要在测试里被真实调用（见 <c>MainFormBizHelper.MaintainTopMost</c>）。</para>
        /// </summary>
        public virtual bool IsTopMostStyleApplied()
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

        /// <summary>
        /// 【P-F06 / #612】置顶的<b>实际可见层级</b>是否仍然有效。
        /// <para>★ 为什么需要"双判"：<see cref="IsTopMostStyleApplied"/> 只读
        /// <c>WS_EX_TOPMOST</c> <b>样式位</b>。样式位只能说明"窗口被标记成置顶"，
        /// 不能证明它现在<b>真的</b>待在置顶层 —— 第三方窗口、驱动/合成层变化、
        /// 句柄重建都可能让一个"带着置顶样式位"的窗口实际掉到普通层里。
        /// 旧看门狗只看样式位 ⇒ 那种情况下它认为"一切正常"，什么都不做。</para>
        /// <para>★ 判定方法：沿 <c>GW_HWNDPREV</c> 链向前看，只要前方有一个<b>可见</b>且
        /// <b>非置顶</b>的窗口，就说明我们<b>已经不在置顶层</b>。这是"同级/分层"的真实定义，
        /// 不遍历系统窗口（母计划 F06 第 2 步明确禁止）。链上的<b>置顶</b>窗口不算问题 ——
        /// 强行把它们压下去就是"抢其它置顶程序的 Z 序"，规格明确不允许。</para>
        /// </summary>
        public virtual bool IsTopMostLayeringEffective()
        {
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return false;

                // 样式位都不在 ⇒ 根本谈不上"置顶生效"。
                if (!IsTopMostStyleApplied()) return false;

                IntPtr self = _form.Handle;
                if (self == IntPtr.Zero) return false;

                IntPtr previous = GetWindow(self, GW_HWNDPREV);
                for (int i = 0; previous != IntPtr.Zero && i < LayeringScanLimit; i++)
                {
                    if (IsWindowVisible(previous) &&
                        (GetWindowLong(previous, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
                    {
                        return false;   // 一个普通窗口压在"置顶窗口"上面 ⇒ 置顶已失效
                    }

                    previous = GetWindow(previous, GW_HWNDPREV);
                }

                return true;
            }
            catch
            {
                // 读取 Z 序失败 ⇒ 无法证明异常，按"有效"处理，避免误判导致反复重插。
                return true;
            }
        }

        /// <summary>
        /// 【P-F06 / #612】前景窗口是否<b>整屏覆盖</b>本窗口所在显示器
        /// （全屏游戏 / 全屏视频 / 锁屏与安全桌面都属于这一类）。
        /// <para>★ 用途：置顶恢复的<b>抑制</b>条件 —— 用户正在看全屏内容时，
        /// 看门狗不得把悬浮窗插到全屏窗口上面（规格：尊重全屏行为、不压安全桌面）。</para>
        /// </summary>
        public static bool IsForegroundCoveringMonitor(IntPtr ourHandle)
        {
            if (ourHandle == IntPtr.Zero) return false;

            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero || foreground == ourHandle) return false;

                if (!GetWindowRect(foreground, out RECT fg)) return false;

                Screen? screen = Screen.FromHandle(ourHandle);
                if (screen == null) return false;

                Rectangle bounds = screen.Bounds;
                return fg.left <= bounds.Left &&
                       fg.top <= bounds.Top &&
                       fg.right >= bounds.Right &&
                       fg.bottom >= bounds.Bottom;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 应用 / 摘除置顶。
        /// <para>★ virtual 的原因：置顶看门狗（<c>MainFormBizHelper.MaintainTopMost</c>）的
        /// 决策逻辑需要在测试里被<b>真实调用</b>并观察它"到底请求了什么"，
        /// 而真实 Win32 上无法稳定复现"样式位在、层级已掉"的状态（见 F06 验收报告）。</para>
        /// </summary>
        public virtual void RefreshTopMost(bool enabled, bool forceReinsert = false)
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
        /// <para>★ <b>不要再按"分层 ⇒ 降级为 <c>GpuFixedInput</c>"来改这里。</b>
        /// 那条硬闸来自 T02 合同 §10 GPU-003，其前提"<c>WS_EX_LAYERED</c> 与
        /// <c>WDA_EXCLUDEFROMCAPTURE</c> 互斥"已被 2026-10-24 步 0 实机实验<b>证伪</b>：
        /// legacy 路线在同一块分层窗口（<c>Opacity=0.85</c>）上成功抓到实时背景，
        /// 亲和性全程保持 <c>0x11</c>。按<b>方案 A</b>，分层判定已被降级为
        /// <b>纯诊断</b>——<c>RenderingRoutePolicy.Decide</c> 不再因分层改变路由，
        /// 只追加诊断条目 <c>diagnostic_window_is_layered</c>
        /// （见 <c>src/Rendering/Configuration/RenderingRoutePolicy.cs:122-131</c>）。
        /// 原因码 <c>wda_capture_policy_unsupported_on_layered_window</c> 已随之作废，
        /// 全仓仅剩 <c>docs/</c> 的历史记录，生产代码不再产生该码。</para>
        /// <para>★ 本方法**只读**，绝不修改 ex-style（合同 §1.3 明确否决"顺手清 WS_EX_LAYERED"）。</para>
        /// <para>★ 期望状态 = <c>Opacity &lt; 1 || ClickThrough</c>（合同 §4 步 7 / GPU-003）：
        /// 重建后新 HWND 的 <c>WS_EX_LAYERED</c> 由 <c>CreateParams</c> 按配置决定 ⇒ 旧残留**不继承**。</para>
        /// <para>★ 消费方：<c>RenderingRouteRequest.WindowIsLayered</c>（路由诊断）与
        /// <c>src/UI/Settings/RenderingRouteStatusView.cs</c>（设置页状态行，经
        /// MainPanelPage 摘要行 / SystemHardwarPage 诊断卡消费）。
        /// 注意：<c>RenderingRouteDecision.Errors</c> 目前<b>没有生产消费者</b>，
        /// 状态行走的是本方法这条独立路径。</para>
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
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }
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

        /// <summary>
        /// 【P-F06 / #612】沿 Z 序链向前扫描的上限（有界，绝不无限遍历系统窗口）。
        /// </summary>
        private const int LayeringScanLimit = 64;

        /// <summary>GetWindow 的关系命令：取 Z 序上"前一个"（更靠前）的窗口。</summary>
        private const uint GW_HWNDPREV = 3;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;

        public static void ActivateWindow(IntPtr handle) => SetForegroundWindow(handle);
    }
}
