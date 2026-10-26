using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor
{
    public class TaskbarForm : Form, ITaskbarHostBridge
    {
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly MainForm _mainForm;
        private readonly System.Windows.Forms.Timer _timer = new();

        // ★★★ 双助手架构 ★★★
        private readonly TaskbarWinHelper _winHelper;
        private readonly TaskbarBizHelper _bizHelper;

        // ★★★ P-F04：任务栏宿主生命周期与有界恢复 ★★★
        // 恢复调度由本窗体持有 —— 它在**面板 HWND 被外部销毁之后依然存活**，
        //   因此恢复不会随子窗口一起消失（母计划 F04 第 1 步）。
        private readonly TaskbarRecoveryCoordinator _recovery;

        // ★ 本窗句柄代次：句柄每次重建都推进一代，
        //   "挂载时记录的代次"与"当前代次"不一致 ⇒ 旧句柄上的判断结果一律作废。
        private long _handleGeneration;
        private long _attachedGeneration = -1;

        // ★ 关闭优先：关闭过程中不得新建/重建 HWND。
        private bool _isClosing;

        // ★ 受控重建标记：区分"我们自己触发的句柄重建"与"外部把句柄销毁了"。
        private bool _controlledHandleRebuild;

        // ★ 面板 HWND 的受控重建器（重建逻辑与判定集中在产品类里，便于真实调用验证）。
        private readonly TaskbarPanelHandleKeeper _panelHandleKeeper;

        // ★ 可见性闸门：只有**确实挂在目标任务栏上**的面板才允许显示。
        //   目标屏未就绪时若把面板显示出来，它会以顶层窗口形态浮在桌面上。
        private readonly TaskbarPanelVisibilityGate _visibility = new TaskbarPanelVisibilityGate();

        private HorizontalLayout _layout;
        private List<Column>? _cols;
        private ContextMenuStrip? _currentMenu;
        private string _lastLayoutSignature = "";
        private readonly TaskbarTooltipHelper _tooltipHelper;

        /// <summary>
        /// 任务栏主题检查的周期（计划 §4.3：约每 5 秒一次）。
        /// ★ 唯一来源：生产代码与单元测试共用这个常量，
        ///   避免"测试复制一份配置、两边悄悄跑偏"的情况。
        /// </summary>
        public static readonly TimeSpan ThemeCheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 主题检查的**单调截止时间**触发器。
        /// 旧实现用 `Math.Abs(Environment.TickCount) % 5000 &lt; RefreshMs` 判断，
        /// 在高刷新率下会反复读取系统主题、甚至反复应用分层样式。
        /// </summary>
        private readonly MonotonicPeriodicTrigger _themeCheckTrigger;

        /// <summary>
        /// 创建任务栏主题检查的触发器（生产代码与单元测试共用）。
        /// </summary>
        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        public static MonotonicPeriodicTrigger CreateThemeCheckTrigger(TimeProvider? timeProvider = null)
        {
            return new MonotonicPeriodicTrigger(ThemeCheckInterval, ThemeCheckInterval, timeProvider);
        }
        
        // 公开属性
        public string TargetDevice { get; private set; } = "";

        // 判断菜单是否打开
        public bool IsMenuOpen => _currentMenu != null && !_currentMenu.IsDisposed && _currentMenu.Visible;
        
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_LBUTTONDBLCLK = 0x0203;

        /// <param name="timeProvider">
        /// 可注入的单调时钟（默认 <see cref="TimeProvider.System"/>）。
        /// 仅用于测试驱动主题检查的触发器；生产代码不需要传。
        /// </param>
        public TaskbarForm(Settings cfg, UIController ui, MainForm mainForm, TimeProvider? timeProvider = null)
        {
            _cfg = cfg;
            _ui = ui;
            _mainForm = mainForm;
            TargetDevice = _cfg.TaskbarMonitorDevice;

            _themeCheckTrigger = CreateThemeCheckTrigger(timeProvider);

            // 初始化组件
            _winHelper = new TaskbarWinHelper(this);
            _bizHelper = new TaskbarBizHelper(this, _cfg, _winHelper);

            // ★ 恢复协调器（持有恢复调度；独立于面板 HWND 的存活）
            _recovery = new TaskbarRecoveryCoordinator(this, timeProvider);

            // ★ 面板句柄重建器：Dispose 失效句柄时带上"受控重建"标记，
            //   以免被 OnHandleDestroyed 误判成"外部失效"而重复投递恢复请求。
            _panelHandleKeeper = new TaskbarPanelHandleKeeper(this, () =>
            {
                _controlledHandleRebuild = true;
                try { DestroyHandle(); }
                finally { _controlledHandleRebuild = false; }
            });

            // 窗体属性
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = false;
            DoubleBuffered = true;

            // ★ 运行期事实：_layout 唯一的赋值点在 ReloadLayoutCore 里（每次都新建一个 HorizontalLayout），
            //   而构造函数紧接着就会调用它 ⇒ 构造完成时 _layout 一定已赋值。
            //   这里显式建好实例再交给 ReloadLayoutCore：实例数量、创建顺序、后续逻辑与原来完全一致，
            //   只是让编译器看见"构造函数里 _layout 被赋值过"（消 CS8618），不用 null! 兜底。
            _layout = new HorizontalLayout(ThemeManager.Current, 300, LayoutMode.Taskbar, _cfg);
            ReloadLayoutCore(_layout);

            _bizHelper.CheckTheme(true);

            // ★ 首次挂载：立即尝试一次。
            //   目标设备（副屏任务栏）还没就绪时 ⇒ 进入"等待目标任务栏"，
            //   由协调器按 2 秒×10 秒 → 低频退避持续等待，**不静默绑定主屏**（#573 / #583）。
            _recovery.RequestRecovery(TaskbarRecoveryReason.Startup, immediate: true);
            _recovery.Pump();

            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);

            _timer.Interval = Math.Max(_cfg.RefreshMs, 60);
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            // 鼠标悬浮提示初始化
            _tooltipHelper = new TaskbarTooltipHelper(this, _cfg, _ui);

            Tick();
        }

        // =================================================================
        // P-F04：任务栏宿主接缝（ITaskbarHostBridge）
        // =================================================================

        /// <summary>当前宿主生命周期状态（诊断 / 测试用）。</summary>
        public TaskbarHostState HostState => _recovery.State;

        /// <summary>已执行的恢复尝试次数（诊断 / 测试用）。</summary>
        public int RecoveryAttemptCount => _recovery.AttemptCount;

        /// <summary>
        /// 投递一次任务栏恢复请求（**只投递**，不执行窗口动作）。
        /// <para>★ 由独立于面板 HWND 的 UI 宿主（主窗）在收到
        /// Explorer 重启 / 电源 / 锁屏 / 显示拓扑 / DPI / 任务栏设置变化时调用。</para>
        /// </summary>
        public void RequestTaskbarRecovery(TaskbarRecoveryReason reason = TaskbarRecoveryReason.HostInvalid)
        {
            if (_isClosing) return;
            _recovery.RequestRecovery(reason);
        }

        /// <summary>
        /// 【P-F06 / #599】显示变化（分辨率 / 拓扑 / DPI / 任务栏设置）之后立即检查一次
        /// 同级 Z 序，并排定有限次后续检查以覆盖<b>迟到</b>的合成层。
        /// <para>★ 由主窗在收到 WM_DISPLAYCHANGE / WM_SETTINGCHANGE / WM_DPICHANGED 时调用
        /// （与已投递的恢复请求并列 —— 两者互不替代）。</para>
        /// </summary>
        public void NotifyDisplayChanged()
        {
            _winHelper.RequestDisplayChangeCheck();
        }

        /// <summary>解析目标任务栏宿主（目标缺失时**不回退主屏**）。</summary>
        public TaskbarHostResolution ResolveTarget() => _bizHelper.ResolveTarget();

        /// <summary>
        /// 面板当前是否确实挂在目标任务栏上。
        /// <para>★ 额外校验**本窗句柄代次**：句柄重建后旧代次的判断结果一律作废。</para>
        /// </summary>
        public bool IsPanelAttached(TaskbarHostResolution resolution)
        {
            if (_attachedGeneration != _handleGeneration) return false;
            return _bizHelper.IsPanelAttached(resolution);
        }

        /// <summary>
        /// ★ 可见性闸门：面板**只有挂在目标任务栏上**才允许显示。
        /// <para>目标屏（副屏任务栏）未就绪时，面板尚未 <c>SetParent</c> 到任何任务栏 ——
        /// 此时显示它会让它以**顶层窗口**形态浮在桌面上。正确语义是"暂不可用" ⇒ 不显示，
        /// 等挂载成功后再补一次显示。</para>
        /// </summary>
        protected override void SetVisibleCore(bool value)
        {
            if (value && _visibility.ShouldSuppressShow)
            {
                _visibility.RequestShow();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        /// <summary>执行一次挂载（必要时先重建面板自身的 HWND）。</summary>
        public void AttachToTarget(TaskbarHostResolution resolution)
        {
            EnsurePanelHandle();

            // ★ 句柄被重建后，旧句柄上的分层样式（颜色键）与扩展样式**随句柄一起消失** ⇒ 必须重放一次。
            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);

            _bizHelper.AttachToResolved(resolution);

            // 记录"挂载时的句柄代次"：之后句柄一旦重建，这里就对不上 ⇒ 判定为未挂好。
            _attachedGeneration = _handleGeneration;

            // ★ 挂上去了 ⇒ 面板可以显示了（兑现之前被闸门压住的显示请求）。
            _visibility.ConfirmAttached();
            if (_visibility.TryConsumeShowRequest() && !Visible)
            {
                Show();
            }
        }

        /// <summary>
        /// 确保面板自身的 HWND 存在。
        /// <para>★ 场景：任务栏根窗口被 Explorer 销毁 ⇒ 作为其子窗口的面板**一并被销毁**
        /// （Windows 语义，已实测）。此时 WinForms 的句柄已失效 ⇒ 这里重建一个，
        /// 随后由 <see cref="AttachToTarget"/> 重新挂到（新的）任务栏上。</para>
        /// <para>★ 关闭过程中**不得**重建 HWND。</para>
        /// </summary>
        private void EnsurePanelHandle()
        {
            if (_isClosing) return;
            _panelHandleKeeper.EnsureHandle();
        }

        public void ReloadLayout()
        {
            ReloadLayoutCore(new HorizontalLayout(ThemeManager.Current, 300, LayoutMode.Taskbar, _cfg));
        }

        /// <summary>
        /// 重建任务栏布局。<paramref name="layout"/> 是本次使用的新布局实例。
        /// </summary>
        /// <remarks>
        /// ★ 单独拆出来是为了让构造函数能"先建实例、再走同一套逻辑"，
        ///   这样 _layout 在构造函数结束前一定已被赋值（消除 CS8618），
        ///   同时每次 ReloadLayout() 依旧会新建一个 HorizontalLayout（行为不变）。
        /// </remarks>
        private void ReloadLayoutCore(HorizontalLayout layout)
        {
            _layout = layout;
            _lastLayoutSignature = ""; // 重置签名，强制重算
            _winHelper.ApplyLayeredStyle(_bizHelper.TransparentKey, _cfg.TaskbarClickThrough);
            _bizHelper.CheckTheme(true);

            // 更新悬浮窗模式 (支持热切换)
            _tooltipHelper?.ReloadMode();

            // 注意：这里仍然可能因为 _cols 为空而暂时不 Build，
            // 但随后的 Tick 会在获取到新数据后自动 Build
            if (_cols != null && _cols.Count > 0)
            {
                _layout.Build(_cols, _bizHelper.Height);
                Width = _layout.PanelWidth;
                _bizHelper.UpdatePlacement(Width);
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // ★ 关闭优先：取消排队中的恢复、注销后续动作、禁止复活窗口。
                _isClosing = true;
                _recovery.BeginClosing();

                _winHelper?.RestoreTaskbar();
                _timer.Stop();
                _timer.Dispose();
                _currentMenu?.Dispose();
                _tooltipHelper?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // ★ 关闭拥有最高优先级：关闭过程中不得新建 HWND、不得复活面板。
            _isClosing = true;
            _recovery.BeginClosing();
            base.OnFormClosing(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // ★ 句柄代次推进：新句柄 ⇒ 新代次（旧代次上的判断结果一律作废）。
            Interlocked.Increment(ref _handleGeneration);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            base.OnHandleDestroyed(e);

            // ① 应用关闭 ⇒ 不恢复。
            if (_isClosing) return;

            // ② 受控重建（我们自己触发的）⇒ 由发起方负责后续挂载，不重复投递。
            if (_controlledHandleRebuild) return;

            // ③ 外部失效（父任务栏窗口被销毁 ⇒ 子窗口一并销毁）⇒ 投递恢复请求。
            //    ★ 只投递，不在销毁回调里递归重建（母计划 F04 第 3 步）。
            _recovery.RequestRecovery(TaskbarRecoveryReason.HandleDestroyed);
        }

        protected override void WndProc(ref Message m)
        {
            // [Fix] 兼容性修复：在 Win11 25H2 + StartAllBack 环境下，
            // 右键事件会穿透到原生任务栏。
            // 因此不再区分系统版本，统一拦截右键按下和抬起消息。
            if (m.Msg == WM_RBUTTONDOWN)
            {
                return; // 吞掉按下事件，防止穿透
            }

            if (m.Msg == WM_RBUTTONUP)
            {
                this.BeginInvoke(new Action(ShowContextMenu));
                return; 
            }

            // [Fix] 强制拦截双击事件
            // 当悬浮窗(Tooltip)显示时，WinForms 的标准双击事件可能因为焦点/激活状态的微妙变化而失效。
            // 这里直接在消息层处理 WM_LBUTTONDBLCLK，确保双击动作始终能被触发。
            if (m.Msg == WM_LBUTTONDBLCLK)
            {
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
                return;
            }

            base.WndProc(ref m);
        }

        private void ShowContextMenu()
        {
            if (_currentMenu != null)
            {
                _currentMenu.Dispose();
                _currentMenu = null;
            }

            _currentMenu = MenuManager.Build(_mainForm, _cfg, _ui, "Taskbar");
            
            TaskbarWinHelper.ActivateWindow(this.Handle);
            _currentMenu.Show(Cursor.Position);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                _bizHelper.HandleDoubleClick(_mainForm, _ui);
            }
        }

        private void Tick()
        {
            // [Fix] 周期性检查句柄，防止 Explorer 重启后句柄失效。
            // ★ P-F04：不再由这里自己按 2 秒轮询重查，而是**投递恢复请求**，
            //   由恢复协调器统一去抖 + 有界退避（恢复期 2 秒×10 秒，期后低频等待，不永久停止）。
            //   两项检查都是廉价的 IsWindow 调用，不新增高频轮询线程。
            //   ★ 用"巡检请求"而不是普通恢复请求：巡检**不得**抢占已排定的退避节奏。
            if (!_bizHelper.IsTaskbarValid() || !_bizHelper.IsPanelHandleAlive())
            {
                _recovery.EnsureRecoveryScheduled(TaskbarRecoveryReason.HostInvalid);
            }

            // ★ 在 UI 消息循环里执行（到期的）恢复动作：单次在途、绝不递归重建。
            _recovery.Pump();

            // ★ P-F06 / #599：同级 Z 序的低成本后续检查（覆盖迟到的合成层）。
            //   只有"定位/显示变化排定的配额"还没用完时才看一眼，用完即止 ——
            //   不每帧无条件置顶、不遍历系统窗口、不反复重挂钩子。
            _winHelper.PumpZOrderMaintenance();

            // [限频] 主题检查改为单调截止时间驱动（约每 5 秒一次）：
            // 正常重复 Tick 不再反复读取系统主题 / 反复应用分层样式。
            if (_themeCheckTrigger.TryConsume()) _bizHelper.CheckTheme();

            // [Fix Part 1] 防空数据保护
            // 使用临时变量接收，先判断数据有效性，再赋值给成员变量 _cols
            // 防止在 UI 重建期间(RebuildLayout)获取到空列表导致任务栏闪烁或清空
            var nextCols = _ui.GetTaskbarColumns();
            if (nextCols == null || nextCols.Count == 0) return;
            
            _cols = nextCols; // 确认有效后再更新引用

            _bizHelper.UpdateTaskbarRect(); 
            
            // [Fix Part 2] 布局变更检测
            if (_bizHelper.IsVertical())
            {
                // 垂直模式逻辑简单且无测量开销，直接重算即可
                _bizHelper.BuildVerticalLayout(_cols);
                _lastLayoutSignature = "vertical";
            }
            else
            {
                // [优化] 智能判断更新条件
                // 1. 必须检查：如果列表是新生成的（坐标还没算过，Bounds为空），必须重算！
                // 这解决了“主界面显隐导致任务栏消失”的问题
                bool isUninitialized = (_cols.Count > 0 && _cols[0].Bounds.IsEmpty);

                // 2. 常规检查：如果内容长度/结构变了（签名变了），也要重算
                string currentSig = _layout.GetLayoutSignature(_cols) + "_" + _bizHelper.Height;
                bool isContentChanged = (currentSig != _lastLayoutSignature);

                if (isUninitialized || isContentChanged)
                {
                    _layout.Build(_cols, _bizHelper.Height);
                    Width = _layout.PanelWidth;
                    Height = _bizHelper.Height;
                    
                    _lastLayoutSignature = currentSig;
                }
            }
            
            _bizHelper.UpdatePlacement(Width);
            
            _tooltipHelper.UpdateContent();

            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_bizHelper.TransparentKey);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // ★ 调试验证用：如果消失时出现了一个红块，说明 OnPaint 被调用但 _cols 为空
            // e.Graphics.FillRectangle(Brushes.Red, 0, 0, 20, 20); 

            if (_cols == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TaskbarRenderer.Render(g, _cols, _bizHelper.LastIsLightTheme);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (防止点击激活窗口，避免抢占焦点)
                if (_cfg != null && _cfg.TaskbarClickThrough)
                {
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                }
                return cp;
            }
        }
    }
}
