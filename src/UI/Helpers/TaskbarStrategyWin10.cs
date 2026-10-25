using System;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// Win10 任务栏策略
    /// 机制：通过调整 MSTaskSwWClass (任务列表) 的位置来腾出空间 (挤占式)
    /// 特点：挂载到 ReBarWindow32，需要精确控制兄弟窗口位置
    ///
    /// ★ P-F05 变更要点（母计划 F05 五步）：
    /// <list type="number">
    /// <item><b>显式方向</b>：停靠方向来自 <see cref="TaskbarDockDirection"/>（由上层按
    /// "任务栏相对所属显示器的物理矩形"解析），本类**不再用宽高比猜方向**。</item>
    /// <item><b>坐标契约</b>：几何查询一律取<b>物理屏幕像素</b>；布局中间值一律在
    /// <b>父（ReBar）客户区坐标</b>；最终定位经 <c>ScreenToClient</c> 显式转换，
    /// 不再混用屏幕坐标与客户区坐标。</item>
    /// <item><b>占位幂等</b>：占位由 <see cref="TaskbarReservationModel"/> 维护
    /// 「基线 + 本次占位量」；重复 Tick 幂等；句柄 / 方向 / 分辨率变化重取基线；
    /// 禁用 / 退出 / 重挂撤销旧占位；<b>绝不在已缩短的任务列表上继续扣减</b>。</item>
    /// </list>
    /// </summary>
    public class TaskbarStrategyWin10 : ITaskbarStrategy
    {
        private readonly Form _form;

        // ★ 任务栏窗口查找来源（可注入 ⇒ 测试可在真实 HWND 上驱动本策略，
        //   且绝不查找、也绝不触碰真实的 Shell_TrayWnd）。
        private readonly ITaskbarWindowSource _windows;

        // 关键句柄
        private IntPtr _hReBar = IntPtr.Zero;
        private IntPtr _hMin = IntPtr.Zero; // MSTaskSwWClass

        // ★ 占位模型：基线（未施加本面板影响的任务列表矩形）+ 本次占位量
        private readonly TaskbarReservationModel _reservation = new TaskbarReservationModel();

        // ★ 基线身份：句柄 / 方向 / 父客户区尺寸（分辨率）/ 占位模式
        private TaskbarReservationToken _token;
        private bool _tokenValid;

        public bool IsReady => _hReBar != IntPtr.Zero && _hMin != IntPtr.Zero;

        public bool HasInternalLayout => true;

        /// <summary>策略内部的子宿主句柄（Win10 = ReBarWindow32）—— 参与几何缓存键。</summary>
        public IntPtr GetInnerHostHandle() => _hReBar;

        /// <summary>当前占位基线（父客户区坐标；诊断 / 测试用）。</summary>
        public Rectangle ReservationBaseline => _reservation.HasBaseline ? _reservation.Baseline : Rectangle.Empty;

        /// <summary>当前占位量（诊断 / 测试用）。</summary>
        public int ReservationAmount => _reservation.Reservation;

        public TaskbarStrategyWin10(Form form, ITaskbarWindowSource? windows = null)
        {
            _form = form;
            _windows = windows ?? Win32TaskbarWindowSource.Instance;
        }

        public void Attach(IntPtr taskbarHandle)
        {
            InitializeHandles(taskbarHandle);

            IntPtr targetParent = taskbarHandle;
            // Win10下，为了与系统图标平级且正确遮挡，我们将父窗口设为 ReBarWindow32
            if (IsReady)
            {
                targetParent = _hReBar;
            }

            SetParent(_form.Handle, targetParent);
            TaskbarWinHelper.ApplyChildWindowStyle(_form.Handle);
        }

        public IntPtr GetExpectedParent(IntPtr taskbarHandle)
        {
            if (IsReady) return _hReBar;
            return taskbarHandle; // 如果初始化失败，回退到 Taskbar
        }

        public void SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft, TaskbarDockDirection direction)
        {
            // 确保句柄已初始化
            if (!IsReady) InitializeHandles(taskbarHandle);

            if (TryAdjustLayout(w, h, manualOffset, alignLeft, direction, out int win10X, out int win10Y))
            {
                // ★ x / y 已经是**父（ReBar）客户区坐标**
                SetWindowPos(_form.Handle, IntPtr.Zero, win10X, win10Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            else
            {
                // 回退：方向未知 / 句柄缺失 ⇒ 用上层算好的屏幕坐标，
                // 但必须**显式转换**到父客户区坐标（不能直接拿屏幕坐标当客户区坐标用）。
                IntPtr parent = GetParent(_form.Handle);
                if (!TaskbarWinHelper.TryScreenToClient(parent, left, top, out int clientX, out int clientY))
                {
                    clientX = left;
                    clientY = top;
                }
                SetWindowPos(_form.Handle, IntPtr.Zero, clientX, clientY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        public void Restore()
        {
            // ★ 撤销占位：把任务列表恢复到基线（"未施加本面板影响"的原始布局）。
            UndoReservation();
        }

        private void InitializeHandles(IntPtr hTaskbar)
        {
            if (hTaskbar == IntPtr.Zero) return;

            // Win10 结构：Shell_TrayWnd -> ReBarWindow32 -> MSTaskSwWClass
            _hReBar = _windows.FindWindowEx(hTaskbar, IntPtr.Zero, "ReBarWindow32", null);
            if (_hReBar == IntPtr.Zero)
                _hReBar = _windows.FindWindowEx(hTaskbar, IntPtr.Zero, "WorkerW", null);

            if (_hReBar != IntPtr.Zero)
            {
                _hMin = _windows.FindWindowEx(_hReBar, IntPtr.Zero, "MSTaskSwWClass", null);
                if (_hMin == IntPtr.Zero)
                    _hMin = _windows.FindWindowEx(_hReBar, IntPtr.Zero, "MSTaskListWClass", null);
            }
        }

        // =================================================================
        // 核心布局逻辑
        // =================================================================

        /// <summary>
        /// 计算并施加占位（挤占）布局。
        /// </summary>
        /// <param name="w">面板宽度（物理像素）。</param>
        /// <param name="h">面板高度（物理像素）。</param>
        /// <param name="manualOffset">手动偏移（物理像素）。</param>
        /// <param name="alignLeft">靠左（水平）/ 靠上（垂直）。</param>
        /// <param name="direction">显式停靠方向。</param>
        /// <param name="x">输出：面板在父客户区坐标系的 X。</param>
        /// <param name="y">输出：面板在父客户区坐标系的 Y。</param>
        /// <returns>false 表示无法处理（方向未知 / 句柄缺失 / 空间不足）。</returns>
        private bool TryAdjustLayout(int w, int h, int manualOffset, bool alignLeft, TaskbarDockDirection direction, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (!IsReady) return false;

            // ★ 方向必须是显式枚举；未知方向下**不做任何猜测**（0 是有效数据）。
            bool vertical = direction == TaskbarDockDirection.Left || direction == TaskbarDockDirection.Right;
            bool horizontal = direction == TaskbarDockDirection.Top || direction == TaskbarDockDirection.Bottom;
            if (!vertical && !horizontal) return false;

            // ① 几何查询：物理屏幕像素。
            //    ★ 先读**父窗口**（ReBar）—— 它不受任务列表占位影响，
            //    因此可以在撤销占位之前安全读取。
            if (!GetWindowRect(_hReBar, out RECT rBar)) return false;
            Rectangle barScreen = Rectangle.FromLTRB(rBar.left, rBar.top, rBar.right, rBar.bottom);

            // ② ★ 坐标契约：屏幕物理像素 → 父（ReBar）客户区坐标
            if (!TaskbarWinHelper.TryScreenRectToClient(_hReBar, barScreen, out Rectangle barClient)) return false;

            var axis = vertical ? TaskbarShrinkAxis.Vertical : TaskbarShrinkAxis.Horizontal;
            var mode = alignLeft ? TaskbarShrinkMode.FromStart : TaskbarShrinkMode.FromEnd;

            // ③ 基线身份：句柄 / 方向 / 父客户区尺寸（分辨率）/ 占位模式
            int parentExtent = vertical ? barClient.Height : barClient.Width;
            var token = new TaskbarReservationToken(_hReBar, _hMin, direction, parentExtent, mode);
            if (!_tokenValid || _token != token)
            {
                if (_tokenValid && _token.IsSameLayout(token))
                {
                    // ★ 只是对齐方式变了 ⇒ 先撤销旧占位（还原到基线），再重新施加
                    UndoReservation();
                }
                else
                {
                    // ★ 句柄 / 方向 / 分辨率变化 ⇒ 系统的**新布局**已经取代了旧占位：
                    //   只丢状态，**不把旧基线写回新窗口**（否则会破坏新布局）。
                    _reservation.Reset();
                }
                _token = token;
                _tokenValid = true;
            }

            // ④ 现在才读任务列表的几何（★ 必须在撤销占位**之后**读，否则拿到的是旧值）
            if (!GetWindowRect(_hMin, out RECT rMin)) return false;
            Rectangle minScreen = Rectangle.FromLTRB(rMin.left, rMin.top, rMin.right, rMin.bottom);
            if (!TaskbarWinHelper.TryScreenRectToClient(_hReBar, minScreen, out Rectangle minClient)) return false;

            // ⑤ 由「基线 + 本次占位量」推导应施加的矩形
            int panelExtent = vertical ? h : w;
            if (!_reservation.TryCompute(minClient, axis, panelExtent, manualOffset, parentExtent, mode, out Rectangle applied, out int panelStart))
            {
                return false;
            }

            // ⑥ 施加（幂等：已经一致就不动窗口，避免反复重绘）
            if (!SameRect(applied, minClient))
            {
                MoveWindow(_hMin, applied.Left, applied.Top, applied.Width, applied.Height, true);
            }
            _reservation.Commit(applied, axis);

            // ⑦ 面板位置（父客户区坐标）
            if (vertical)
            {
                x = (barClient.Width - w) / 2;
                y = panelStart;
            }
            else
            {
                x = panelStart;
                y = (barClient.Height - h) / 2;
            }
            return true;
        }

        /// <summary>
        /// 撤销占位：把任务列表恢复到基线，并清空占位状态。
        /// <para>★ 用于禁用 / 退出 / 重挂 / 方向或分辨率变化。</para>
        /// </summary>
        private void UndoReservation()
        {
            if (_reservation.TryTakeBaseline(out Rectangle baseline))
            {
                if (_hMin != IntPtr.Zero && IsWindow(_hMin))
                {
                    MoveWindow(_hMin, baseline.Left, baseline.Top, baseline.Width, baseline.Height, true);
                }
            }
            _tokenValid = false;
        }

        private static bool SameRect(Rectangle a, Rectangle b)
        {
            return Math.Abs(a.Left - b.Left) <= TaskbarReservationModel.Tolerance
                && Math.Abs(a.Top - b.Top) <= TaskbarReservationModel.Tolerance
                && Math.Abs(a.Width - b.Width) <= TaskbarReservationModel.Tolerance
                && Math.Abs(a.Height - b.Height) <= TaskbarReservationModel.Tolerance;
        }
    }
}
