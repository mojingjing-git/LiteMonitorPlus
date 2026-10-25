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
    /// ★ P-F06 变更要点（同级 Z 序维护 —— #599）：
    /// <list type="number">
    /// <item><b>按需 HWND_TOP</b>：定位之后，只有<b>确实被可见同级窗口挡住</b>时才把面板
    /// 提到同级最前，且只带 NOMOVE / NOSIZE / NOACTIVATE ⇒ 矩形与焦点都不变。
    /// 旧实现定位恒带 <c>SWP_NOZORDER</c>（本文件原 :78 / :82）⇒ 迟到的合成层
    /// （显卡驱动热更新 / Explorer 重建子层）把面板盖住后，跑再多维护周期也不会恢复。</item>
    /// <item><b>绝不写成 TOPMOST</b>：面板是任务栏的<b>子窗口</b>，正确语义只到
    /// <c>HWND_TOP</c>（同级最前）。<c>HWND_TOPMOST</c> 会把面板从任务栏层级里拔出来，
    /// 变成压住整个桌面的顶级置顶窗口。</item>
    /// <item><b>低成本后续检查</b>：合成层可能<b>迟到</b>（定位之后才出现），因此每次定位
    /// 都会排定一次<b>有限次</b>的后续检查（<c>MaintenanceFollowUps</c> 次，见
    /// <see cref="RequestDisplayChangeCheck"/>），用完即止 —— 不每帧无条件置顶、
    /// 不遍历系统窗口、不反复重挂钩子。</item>
    /// </list>
    /// </summary>
    public class TaskbarStrategyWin11 : ITaskbarStrategy
    {
        /// <summary>
        /// 一次定位之后允许的"迟到合成层"后续检查次数。
        /// <para>★ 有界：这是"低成本后续检查"的上限，用完即止，
        /// 绝不退化成每帧无条件重排（母计划 F06 第 2 步）。</para>
        /// </summary>
        public const int MaintenanceFollowUps = 3;

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

        /// <summary>
        /// 剩余的"迟到合成层"后续检查次数（有界，见 <see cref="MaintenanceFollowUps"/>）。
        /// <para>★ 由定位与显示变化排定，<see cref="PumpZOrderMaintenance"/> 消费，用完即止。</para>
        /// </summary>
        private int _followUpChecks;

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

        /// <summary>
        /// 显示变化 / 重挂之后<b>立即</b>检查一次同级 Z 序（母计划 F06 第 2 步）。
        /// <para>★ 合成层往往在显示变化那一刻就被重建，所以这里必须<b>马上</b>看一眼，
        /// 不能只等下一次定位周期。</para>
        /// <para>★ 同时排定 <see cref="MaintenanceFollowUps"/> 次低成本后续检查，
        /// 用来覆盖<b>迟到</b>的合成层（驱动热更新、Explorer 异步重建子层）。</para>
        /// </summary>
        public void RequestDisplayChangeCheck()
        {
            _followUpChecks = MaintenanceFollowUps;
            EnsureSiblingZOrder();
        }

        /// <summary>
        /// 维护周期内的一次低成本同级 Z 序检查。
        /// <para>★ 只有"还有后续检查配额"时才看（配额由定位 / 显示变化排定），
        /// 用完即止 ⇒ 不会被放大成每帧无条件置顶。</para>
        /// </summary>
        public void PumpZOrderMaintenance()
        {
            if (_followUpChecks <= 0) return;
            _followUpChecks--;
            EnsureSiblingZOrder();
        }

        /// <summary>
        /// 按需把面板恢复到同级最前：只有确实被可见同级窗口挡住时才重排。
        /// <para>★ 面板必须<b>确实挂着</b>（父窗口 == 任务栏）才允许重排 ——
        /// 脱离时它是顶层窗口，"同级"已经不是同一个层级，重排会误伤。</para>
        /// </summary>
        private void EnsureSiblingZOrder()
        {
            if (_taskbarHandle == IntPtr.Zero) return;
            if (!_form.IsHandleCreated) return;

            IntPtr hp = _form.Handle;
            if (hp == IntPtr.Zero || !IsWindow(hp)) return;
            if (GetParent(hp) != _taskbarHandle) return;

            TaskbarWinHelper.RaiseSiblingToTopIfCovered(hp);
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

            // ★ P-F06（#599）：定位之后检查一次同级 Z 序。
            //   定位本身恒带 SWP_NOZORDER（不能因为移动就把面板乱插），
            //   所以 Z 序单独在这里按需维护：只有被可见同级窗口挡住才提到 HWND_TOP。
            //   ★ 同时排定有限次后续检查，覆盖迟到的合成层（不每帧无条件置顶）。
            _followUpChecks = MaintenanceFollowUps;
            EnsureSiblingZOrder();
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
