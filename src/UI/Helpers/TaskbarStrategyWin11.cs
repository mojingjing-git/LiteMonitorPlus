using System;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// Win11 任务栏策略
    /// 机制：使用 SetParent 将窗口挂载到 Shell_TrayWnd 或 Shell_SecondaryTrayWnd
    /// 特点：依赖系统自身的布局，不需要手动挤占空间
    ///
    /// ★ P-F05 变更要点：
    /// <list type="number">
    /// <item><b>显式方向</b>：停靠方向来自 <see cref="TaskbarDockDirection"/>。
    /// 原生 Windows 11 只提供"底部"停靠；左 / 右 / 顶部方向必然来自第三方任务栏
    /// ⇒ 显式标注为<b>兼容性待验</b>，不声称原生支持（母计划 F05 第 5 步）。</item>
    /// <item><b>坐标契约</b>：传入的坐标是<b>物理屏幕像素</b>；这里用
    /// <c>ScreenToClient</c> <b>显式转换</b>到父客户区坐标，不再用
    /// "屏幕坐标 - 父窗口矩形原点"的近似换算（父窗口有边框时两者并不相等）。</item>
    /// </list>
    /// </summary>
    public class TaskbarStrategyWin11 : ITaskbarStrategy
    {
        private readonly Form _form;

        /// <summary>最近一次定位使用的停靠方向（诊断 / 测试用）。</summary>
        public TaskbarDockDirection LastDirection { get; private set; } = TaskbarDockDirection.Unknown;

        /// <summary>
        /// 最近一次定位的方向是否属于"系统本身不提供"的第三方方向
        /// （左 / 右 / 顶部）⇒ <b>兼容性待验</b>。
        /// </summary>
        public bool IsThirdPartyDirection { get; private set; }

        public bool IsReady => true; // Win11 模式不需要特殊的预热

        public bool HasInternalLayout => false;

        /// <summary>策略内部的子宿主句柄（Win11 = 任务栏根窗口本身）—— 参与几何缓存键。</summary>
        public IntPtr GetInnerHostHandle() => _taskbarHandle;

        /// <summary>当前任务栏根窗口句柄。</summary>
        private IntPtr _taskbarHandle = IntPtr.Zero;

        public TaskbarStrategyWin11(Form form)
        {
            _form = form;
        }

        public void Attach(IntPtr taskbarHandle)
        {
            _taskbarHandle = taskbarHandle;
            // Win11 直接挂载到任务栏句柄
            SetParent(_form.Handle, taskbarHandle);
            TaskbarWinHelper.ApplyChildWindowStyle(_form.Handle);
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft, TaskbarDockDirection direction)
        {
            // Win11 模式下，位置由外部业务逻辑计算 (TaskbarBizHelper) 且已包含 manualOffset，
            // 这里只负责转换坐标系，忽略 manualOffset 参数以避免重复偏移。

            LastDirection = direction;
            _taskbarHandle = taskbarHandle;
            IsThirdPartyDirection = direction == TaskbarDockDirection.Top
                                 || direction == TaskbarDockDirection.Left
                                 || direction == TaskbarDockDirection.Right;

            IntPtr currentParent = GetParent(_form.Handle);
            bool isAttached = (currentParent == taskbarHandle);

            if (isAttached)
            {
                // ★ 坐标契约：屏幕物理像素 → 父（任务栏）客户区坐标（显式转换）
                if (TaskbarWinHelper.TryScreenToClient(taskbarHandle, left, top, out int clientX, out int clientY))
                {
                    SetWindowPos(_form.Handle, IntPtr.Zero, clientX, clientY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
                }
                else
                {
                    SetWindowPos(_form.Handle, IntPtr.Zero, left, top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
            else
            {
                // 如果意外脱离，尝试使用 HWND_TOPMOST 保持可见
                IntPtr HWND_TOPMOST = (IntPtr)(-1);
                SetWindowPos(_form.Handle, HWND_TOPMOST, left, top, w, h, SWP_NOACTIVATE);
            }
        }

        public void Restore()
        {
            // Win11 模式没有修改系统窗口，不需要恢复
        }

        public IntPtr GetExpectedParent(IntPtr taskbarHandle)
        {
            return taskbarHandle;
        }
    }
}
