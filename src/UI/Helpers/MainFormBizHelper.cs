using LiteMonitor.SystemServices; // B3:应用层硬件网关
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.SystemServices;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 【P-F06 / #612】置顶重插的<b>抑制</b>判定来源。
    /// <para>★ 为什么要有这层接缝：菜单打开、自动隐藏收起、前景全屏这些"让路"语义，
    /// 在自动化测试里<b>无法</b>靠真实前台窗口稳定复现（测试机随时可能有别的窗口）。
    /// 把它做成可注入接口 ⇒ 断言写的是<b>产品代码的分派逻辑</b>，而不是测试机的偶然状态。</para>
    /// </summary>
    public interface ITopMostSuppressionProvider
    {
        /// <summary>此刻是否应当抑制"重插 Z 序"（让路给菜单 / 自动隐藏 / 全屏 / 安全桌面）。</summary>
        bool ShouldSuppressZOrderReinsert(Form form);
    }

    /// <summary>
    /// 生产实现的可替换基类：把"前景是否全屏"这一支做成可替换的接缝。
    /// <para>★ 为什么要分层：测试进程里 <c>Form.Activate()</c> 不会真的改变系统前台窗口
    /// （无真实输入焦点），"前景全屏"这一支必须由测试替身驱动。
    /// <see cref="DefaultTopMostSuppressionProvider"/> 仍是生产使用的实现。</para>
    /// </summary>
    public abstract class TopMostSuppressionProviderBase : ITopMostSuppressionProvider
    {
        public abstract bool ShouldSuppressZOrderReinsert(Form form);

        /// <summary>
        /// 前景窗口是否整屏覆盖本窗所在显示器（全屏 / 锁屏 / 安全桌面）。
        /// </summary>
        protected virtual bool IsForegroundFullscreen(Form form)
        {
            return MainFormWinHelper.IsForegroundCoveringMonitor(form.Handle);
        }
    }

    /// <summary>
    /// 生产实现：菜单打开 / 自动隐藏已收起 / 前景窗口整屏覆盖本窗所在显示器 ⇒ 让路。
    /// </summary>
    public sealed class DefaultTopMostSuppressionProvider : TopMostSuppressionProviderBase
    {
        /// <summary>进程内共享实例。</summary>
        public static readonly DefaultTopMostSuppressionProvider Instance = new DefaultTopMostSuppressionProvider();

        public override bool ShouldSuppressZOrderReinsert(Form form)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated) return true;

            // ① 右键菜单打开中：不要抢它的 Z 序
            if (form.ContextMenuStrip != null && form.ContextMenuStrip.Visible) return true;

            // ② 自动隐藏已把面板收起：收起状态下重插没有意义，还可能露出边缘
            if (IsAutoHidden(form)) return true;

            // ③ 前景全屏（全屏游戏 / 视频 / 锁屏 / 安全桌面）：不压上去
            return IsForegroundFullscreen(form);
        }

        /// <summary>
        /// 该窗体当前是否处于"自动隐藏已收起"状态。
        /// <para>★ 只有宿主是 <see cref="MainForm"/> 时才读它的状态；其它窗体（如任务栏面板）
        /// 没有自动隐藏语义 ⇒ 一律按"未收起"处理。</para>
        /// </summary>
        private static bool IsAutoHidden(Form form)
        {
            return form is MainForm main && main.IsAutoHidden;
        }
    }

    /// <summary>
    /// 主窗口业务助手 (Business Helper)
    /// 职责：自动隐藏、托盘交互、快捷动作、启动流程、布局切换
    /// </summary>
    public class MainFormBizHelper : IDisposable
    {
        private readonly Form _form;
        private readonly Settings _cfg;
        private readonly UIController _ui;
        private readonly MainFormWinHelper _winHelper;
        private readonly NotifyIcon _tray;

        // 自动隐藏相关
        private System.Windows.Forms.Timer? _autoHideTimer;
        private System.Windows.Forms.Timer? _topMostTimer;
        private bool _isHidden = false;
        // 【R2】泊出屏记忆与治理（评审 P1/P2）：
        //   泊出后窗口中心落在所有屏之外 ⇒ 旧实现每周期 Screen.FromPoint(center) 取屏
        //   可能取错，弹出带坐标错位 ⇒ 面板永久只剩 4px 条（"双屏皆无、进程正常"故障）。
        private Rectangle? _parkedArea;          // 泊出所在屏的 WorkingArea（弹出判定唯一依据）
        private string? _parkedScreenDevice;     // 泊出屏设备名（SavePos 泊出态落盘用）
        private Point _preParkLocation;          // 泊出前位置（SavePos/ForceShow/恢复用）
        private int _zeroIntersectTicks;         // 看门狗：连续零交集 tick 计数
        private bool _watchdogNotified;          // 看门狗气泡只弹一次
        // 【R2】DockEdge 统一使用 AutoHideLogic.DockEdge（原私有枚举删除——Bottom 从未实现）。
        private AutoHideLogic.DockEdge _dock = AutoHideLogic.DockEdge.None;
        private DateTime _keepVisibleUntil = DateTime.MinValue;

        public bool IsHidden => _isHidden;
        public bool IsDragging { get; set; } = false;

        public void ReconcileDisplayChange()
        {
            // A display notification must not replay an old saved location
            // over a drag that has moved the panel to another monitor.
            if (IsDragging) return;
            if (Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(_form.Bounds)))
                ClampToScreen();
            else
                RestorePos();
        }

        public MainFormBizHelper(Form form, Settings cfg, UIController ui, MainFormWinHelper winHelper)
        {
            _form = form;
            _cfg = cfg;
            _ui = ui;
            _winHelper = winHelper;
            _tray = new NotifyIcon();
        }

        public void Initialize()
        {
            InitTray();
            // 【R2 / 评审 P1】timer **无条件**启动：AutoHide 关闭时泊出恢复与看门狗仍须运行。
            //   旧实现 `if (_cfg.AutoHide) StartTimer()` ⇒ 关闭链路 StopTimer 后恢复逻辑成死代码。
            StartTimer();
            StartTopMostTimer();
        }

        // =================================================================
        // 自动隐藏逻辑
        // =================================================================
        public void StartTimer()
        {
            _autoHideTimer ??= new System.Windows.Forms.Timer { Interval = 250 };
            _autoHideTimer.Tick -= AutoHideTick;
            _autoHideTimer.Tick += AutoHideTick;
            _autoHideTimer.Start();
        }

        // 【R2】StopTimer 已移除：250ms tick 常驻（看门狗/泊出恢复常活），关闭走 DisableAutoHide。

        public void KeepVisible(double seconds) => _keepVisibleUntil = DateTime.Now.AddSeconds(seconds);

        public void ForceShow()
        {
            if (_isHidden)
            {
                _isHidden = false;
                _dock = AutoHideLogic.DockEdge.None;
                // 【R2 / 评审 P2】优先回泊出前位置：多屏对角布局下"最近屏"≠泊出屏，
                // 用 ClampToScreen(force) 会弹到错误的屏。
                if (_parkedArea.HasValue)
                {
                    _form.Location = AutoHideLogic.ClampToArea(_preParkLocation, _form.Size, _parkedArea.Value);
                    _parkedArea = null;
                    _parkedScreenDevice = null;
                }
                else
                {
                    ClampToScreen(force: true); // 强制拉回
                }
            }
            KeepVisible(3.0);
        }

        private void AutoHideTick(object? sender, EventArgs e)
        {
            // 【R2 / E】看门狗先于自动隐藏 gate 运行：AutoHide 关闭时它仍须值守。
            RunWatchdogTick();
            CheckAutoHide();
        }

        /// <summary>
        /// 【R2 / 评审 P1】关闭自动隐藏：恢复泊出态；250ms tick **保持运行**
        /// （看门狗与"重新开启"路径都依赖它）。由 <c>AppActions.ApplyWindowAttributes</c>
        /// 的关闭分支调用（替代旧的 <c>StopAutoHideTimer</c>——那会让恢复逻辑成死代码）。
        /// </summary>
        public void DisableAutoHide() => RestoreFromParkIfParked();

        /// <summary>【R2 / 评审 P1】若处于泊出态则立即恢复到泊出前位置。</summary>
        public void RestoreFromParkIfParked()
        {
            if (!_isHidden) return;
            if (_parkedArea.HasValue)
            {
                _form.Location = AutoHideLogic.ClampToArea(_preParkLocation, _form.Size, _parkedArea.Value);
            }
            _isHidden = false;
            _dock = AutoHideLogic.DockEdge.None;
            _parkedArea = null;
            _parkedScreenDevice = null;
            KeepVisible(1.0);
        }

        // =================================================================
        // 置顶看门狗（P-F06 / #612）
        // =================================================================

        /// <summary>
        /// 置顶维护是否应当<b>暂时让路</b>（菜单打开 / 自动隐藏已收起 / 前景全屏）。
        /// <para>★ 规格要求"尊重用户主动关闭置顶、自动隐藏、菜单与全屏行为，不压安全桌面"。
        /// 这些情形下重插 Z 序会把面板顶到用户正在操作的内容上面，属于回归。</para>
        /// <para>★ 可注入（<see cref="SetTopMostSuppressionProvider"/>）⇒ 这些语义能被
        /// <b>真实调用</b>验证，而不必依赖测试机的真实前台窗口。</para>
        /// </summary>
        private ITopMostSuppressionProvider _suppression = DefaultTopMostSuppressionProvider.Instance;

        /// <summary>替换置顶抑制判定来源（测试用；生产用默认实现）。</summary>
        public void SetTopMostSuppressionProvider(ITopMostSuppressionProvider provider)
        {
            _suppression = provider ?? DefaultTopMostSuppressionProvider.Instance;
        }

        /// <summary>
        /// 置顶看门狗的一次维护动作。
        /// <para>★ <b>#612 的核心修复</b>：旧实现第一行是 <c>if (!_form.Visible) return;</c>
        /// ⇒ 纯任务栏模式（用户把主面板藏起来，只留任务栏）下置顶维护<b>完全停摆</b>，
        /// 等用户重新显示主窗时置顶状态已经错了。置顶是<b>窗口属性</b>，不是"可见性属性"
        /// ⇒ 维护必须独立于 <c>Visible</c>。</para>
        /// <para>★ 双判（PR #373 意图）：既看样式位 <see cref="MainFormWinHelper.IsTopMostStyleApplied"/>,
        /// 也看<b>实际可见层级</b> <see cref="MainFormWinHelper.IsTopMostLayeringEffective"/>。
        /// 只看样式位会漏掉"样式位在、但层级已经掉下去"的情况。</para>
        /// <para>★ 单向：用户主动关闭置顶后只做"摘掉残留样式位"，<b>绝不</b>反向把置顶打开 ——
        /// 恢复的是"用户要的置顶"，不是"看门狗想要的置顶"。</para>
        /// </summary>
        public void MaintainTopMost()
        {
            if (_form.IsDisposed || !_form.IsHandleCreated) return;

            if (_cfg.TopMost)
            {
                // ★ 尊重用户/系统当下的行为：菜单打开、自动隐藏已收起、前景全屏 ⇒ 让路
                // （"不压安全桌面"是同一条规则：安全桌面就是全屏前景的一种）。
                if (_suppression.ShouldSuppressZOrderReinsert(_form)) return;

                bool styleApplied = _winHelper.IsTopMostStyleApplied();

                // ★ 双判：样式位缺失 ⇒ 补；样式位在但**实际层级**已掉 ⇒ 也要补。
                if (!styleApplied || !_winHelper.IsTopMostLayeringEffective())
                {
                    _winHelper.RefreshTopMost(true, forceReinsert: true);
                }

                return;
            }

            // 用户主动关闭了置顶 ⇒ 只摘残留样式位，绝不重新打开。
            if (_winHelper.IsTopMostStyleApplied())
            {
                _winHelper.RefreshTopMost(false);
            }
        }

        private void StartTopMostTimer()
        {
            _topMostTimer ??= new System.Windows.Forms.Timer { Interval = 10000 };
            _topMostTimer.Tick -= TopMostTick;
            _topMostTimer.Tick += TopMostTick;
            _topMostTimer.Start();
        }

        private void TopMostTick(object? sender, EventArgs e)
        {
            // ★ P-F06：不再因 !_form.Visible 早退 —— 见 MaintainTopMost 的注释。
            MaintainTopMost();
        }

        private void CheckAutoHide()
        {
            // 【R2 / 评审 P1】AutoHide 关闭 ⇒ 恢复泊出态后返回；看门狗已在 tick 里独立运行。
            if (!_cfg.AutoHide)
            {
                RestoreFromParkIfParked();
                return;
            }
            if (!_form.Visible) return;
            if (IsDragging || _form.ContextMenuStrip?.Visible == true) return;
            if (DateTime.Now < _keepVisibleUntil) return;

            var (hideWidthPx, thresholdPx) = AutoHideLogic.MetricsForDpi(_form.DeviceDpi);
            var cursor = Cursor.Position;

            if (_parkedArea.HasValue)
            {
                // ---- 泊出态：弹出判定**只**用泊出屏（评审 B）。
                // 旧实现用 Screen.FromPoint(center)，而泊出后中心在所有屏之外，
                // 取屏结果随布局翻转 ⇒ 弹出带坐标错位。
                var decision = AutoHideLogic.Decide(
                    _form.Bounds, _parkedArea, _dock, default, cursor,
                    containsCursor: _form.Bounds.Contains(cursor),
                    inKeepVisible: false,
                    hideWidthPx, thresholdPx, AutoHideLogic.HoverBandPx);
                ApplyDecision(decision, screenArea: null, screenDevice: null);
                return;
            }

            // ---- 泊出判定：中心此刻必在屏内 ⇒ FromPoint 取屏可靠（逐字保持旧口径）。
            var center = new Point(_form.Left + _form.Width / 2, _form.Top + _form.Height / 2);
            var screen = Screen.FromPoint(center);
            var parkDecision = AutoHideLogic.Decide(
                _form.Bounds, null, AutoHideLogic.DockEdge.None, screen.WorkingArea, cursor,
                containsCursor: _form.Bounds.Contains(cursor),
                inKeepVisible: false,
                hideWidthPx, thresholdPx, AutoHideLogic.HoverBandPx);
            ApplyDecision(parkDecision, screen.WorkingArea, screen.DeviceName);
        }

        /// <summary>应用核心决策：泊出（先记账再挪窗）/ 弹出（清泊出态 + 防抖保护）。</summary>
        private void ApplyDecision(AutoHideLogic.Decision d, Rectangle? screenArea, string? screenDevice)
        {
            switch (d.Kind)
            {
                case AutoHideLogic.DecisionKind.Park:
                    // ★ 先记泊出前位置/所在屏，再挪窗——泊出坐标永不覆盖原始位置。
                    _preParkLocation = _form.Location;
                    _parkedArea = screenArea;
                    _parkedScreenDevice = screenDevice;
                    _dock = d.Edge;
                    _isHidden = true;
                    _form.Location = d.NewLocation;
                    break;

                case AutoHideLogic.DecisionKind.Show:
                    _form.Location = d.NewLocation;
                    _isHidden = false;
                    _dock = AutoHideLogic.DockEdge.None;
                    _parkedArea = null;
                    _parkedScreenDevice = null;
                    KeepVisible(1.0); // 【R2/D】防 250ms 内回泊抖动
                    break;
            }
        }

        // =================================================================
        // 【R2 / E】窗口零交集看门狗：双屏皆无但进程正常的最终兜底。
        //   泊出态恒有 hideWidth(≥6px) 交集 ⇒ 不触发；完全失去定位持续 ≈5s ⇒ 拉回。
        // ================================================================

        private void RunWatchdogTick()
        {
            if (!_form.Visible || IsDragging)
            {
                _zeroIntersectTicks = 0;
                return;
            }

            var bounds = _form.Bounds;
            bool intersectsAny = false;
            foreach (var s in Screen.AllScreens)
            {
                if (s.WorkingArea.IntersectsWith(bounds))
                {
                    intersectsAny = true;
                    break;
                }
            }

            if (intersectsAny)
            {
                _zeroIntersectTicks = 0;
                return;
            }

            _zeroIntersectTicks++;
            if (_zeroIntersectTicks < 20) return; // ≈5s（防抖：拓扑变化由 ReconcileDisplayChange 先行自愈）
            _zeroIntersectTicks = 0;

            try
            {
                ClampToScreen(force: true);
                KeepVisible(3.0);
                try
                {
                    File.AppendAllText(
                        Path.Combine(AppContext.BaseDirectory, "window-watchdog.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] bounds={bounds} screens={Screen.AllScreens.Length} → 拉回 {Screen.FromControl(_form).DeviceName}\n");
                }
                catch { // Program Files 可能无写权限：自愈照常，日志缺席
                }

                if (!_watchdogNotified)
                {
                    _watchdogNotified = true;
                    bool zh = (_cfg.Language ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                    ShowNotification("LiteMonitor++",
                        zh
                            ? "检测到主面板失去所有屏幕定位，已自动恢复显示。"
                            : "The panel lost its position on all screens and was restored automatically.",
                        ToolTipIcon.Warning);
                }
            }
            catch
            {
                // 自愈失败不致命：下一个周期重试。
            }
        }

        // =================================================================
        // 托盘管理
        // =================================================================
        private void InitTray()
        {
            try { _tray.Icon = Properties.Resources.AppIcon ?? _form.Icon; } catch { _tray.Icon = _form.Icon; }
            _tray.Text = "LiteMonitor++";
            _tray.Visible = !_cfg.HideTrayIcon;

            RebuildMenus();

            _tray.MouseUp += (_, e) => 
            {
                if (e.Button == MouseButtons.Right)
                {
                    MainFormWinHelper.ActivateWindow(_form.Handle);
                    _form.ContextMenuStrip?.Show(Cursor.Position);
                }
            };
            
            _tray.MouseDoubleClick += (_, e) => 
            {
                if (e.Button == MouseButtons.Left) 
                {
                    if (_form.Visible) ((MainForm)_form).HideMainWindow();
                    else ((MainForm)_form).ShowMainWindow();
                }
            };
        }

        public void RebuildMenus()
        {
            if (_form.ContextMenuStrip != null)
            {
                _form.ContextMenuStrip.Dispose();
                _form.ContextMenuStrip = null;
            }
            _form.ContextMenuStrip = MenuManager.Build((MainForm)_form, _cfg, _ui);
            UIUtils.ClearBrushCache();
        }

        public void ShowNotification(string title, string text, ToolTipIcon icon)
        {
            if (_tray.Visible) _tray.ShowBalloonTip(5000, title, text, icon);
        }

        public void SetTrayVisible(bool visible) => _tray.Visible = visible;

        // =================================================================
        // 布局切换与位置管理
        // =================================================================
        public void ToggleLayoutMode()
        {
            _form.SuspendLayout();
            try
            {
                // 记录旧模式
                bool oldMode = _cfg.HorizontalMode;
                
                _cfg.HorizontalMode = !oldMode;
                _cfg.Save();
                
                // ★ 统一使用 AppActions，传入旧模式以触发自动居中
                Core.Actions.AppActions.ApplyThemeAndLayout(_cfg, _ui, (MainForm)_form, oldMode);
            }
            finally
            {
                _form.ResumeLayout(true);
            }
        }

        public void SavePos()
        {
            ClampToScreen(force: false);
            var center = new Point(_form.Left + _form.Width / 2, _form.Top + _form.Height / 2);
            var scr = Screen.FromPoint(center);

            _cfg.ScreenDevice = scr.DeviceName;
            _cfg.Position = new Point(_form.Left, _form.Top);
            _cfg.Save();
        }

        public void RestorePos()
        {
            Screen? savedScreen = null;
            if (!string.IsNullOrEmpty(_cfg.ScreenDevice))
            {
                savedScreen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _cfg.ScreenDevice);
            }

            if (savedScreen != null)
            {
                var area = savedScreen.WorkingArea;
                int x = _cfg.Position.X;
                int y = _cfg.Position.Y;
                SetSafeLocation(area, x, y);
                
                // [Fix #288] 恢复位置后，重置隐藏状态，并让 Timer 重新接管
                // 防止之前是 Hidden 状态，恢复后 Location 变了但 _isHidden 仍为 true，导致自动隐藏逻辑失效
                _isHidden = false;
                _dock = AutoHideLogic.DockEdge.None;
                KeepVisible(0.5); // 短暂保持可见，让用户确认位置，随后由 Timer 自动隐藏
            }
            else
            {
                var screen = Screen.FromControl(_form);
                var area = screen.WorkingArea;
                if (Screen.AllScreens.Any(s => s.Bounds.Contains(_cfg.Position)))
                    _form.Location = _cfg.Position;
                else
                {
                    int x = area.Right - _form.Width - 50; 
                    int y = area.Top + (area.Height - _form.Height) / 2;
                    _form.Location = new Point(x, y);
                }
            }
        }

        public void ClampToScreen(bool force = false)
        {
            if (!_cfg.ClampToScreen && !force) return;

            var area = Screen.FromControl(_form).WorkingArea;
            int x = _form.Left;
            int y = _form.Top;

            // 修复逻辑：
            // 如果启用了自动隐藏，允许窗口稍微贴边，而不是强制弹开
            // 只有当窗口完全跑出屏幕外时，才强制拉回
            // 之前的 margin = _hideThreshold + 1 逻辑会导致：用户刚拖到边缘想隐藏，就被弹回来了

            if (x < area.Left) x = area.Left;
            if (x + _form.Width > area.Right) x = area.Right - _form.Width;
            if (y < area.Top) y = area.Top;
            if (y + _form.Height > area.Bottom) y = area.Bottom - _form.Height;

            _form.Location = new Point(x, y);
        }

        private void SetSafeLocation(Rectangle area, int x, int y)
        {
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + _form.Width > area.Right) x = area.Right - _form.Width;
            if (y + _form.Height > area.Bottom) y = area.Bottom - _form.Height;
            _form.Location = new Point(x, y);
        }

        // =================================================================
        // 快捷动作
        // =================================================================
        public void HandleDoubleClick()
        {
            switch (_cfg.MainFormDoubleClickAction)
            {
                case 1: OpenTaskManager(); break;
                case 2: OpenSettings(); break;
                case 3: OpenTrafficHistory(); break;
                case 4: CleanMemory(); break;
                case 5: WebActions.OpenWebMonitor(_cfg); break;
                case 0: default: ToggleLayoutMode(); break;
            }
        }

        public void OpenTaskManager()
        {
            try { Process.Start(new ProcessStartInfo("taskmgr") { UseShellExecute = true }); } catch { }
        }

        public void OpenSettings()
        {
            foreach (Form f in Application.OpenForms) { if (f is SettingsForm) { f.Activate(); return; } }
            new SettingsForm(_cfg, _ui, (MainForm)_form).Show();
        }

        public void OpenTrafficHistory()
        {
            foreach (Form f in Application.OpenForms) { if (f is TrafficHistoryForm) { f.Activate(); return; } }
            new TrafficHistoryForm(_cfg).Show();
        }

        public async void CleanMemory()
        {
            try { using (var form = new CleanMemoryForm()) await form.StartCleaningAsync(); } catch { }
        }

        // =================================================================
        // 启动流程
        // =================================================================
        public async Task RunStartupChecksAsync()
        {
            try
            {
                await HardwareGateway.SmartCheckDriverAsync();
                await UpdateChecker.CheckAsync();
                CheckUpdateSuccess();

                // 如果发现新版本，重新构建菜单以显示“发现新版本”按钮
                if (UpdateChecker.IsUpdateFound)
                {
                    _form.BeginInvoke(new Action(() => RebuildMenus()));
                }

                // [Fix] 再次确认窗口属性（置顶、穿透），作为启动后的二次校验，确保功能与 UI 勾选一致
                _form.BeginInvoke(new Action(() => {
                    Core.Actions.AppActions.ApplyWindowAttributes(_cfg, (MainForm)_form);
                }));
            }
            catch { }
        }

        /// <summary>
        /// 读取更新器留下的三种结论并提示，然后清理标记。
        ///
        /// 三种状态必须分开，否则「更新器跑到了最后一步」会被误报成「已成功更新」：
        ///   update_success     → 全部文件替换并校验成功
        ///   update_error.log   → 按 [Result] 恢复结果区分提交前失败、完整恢复或恢复不完整
        ///   update_in_progress → 更新器被强杀，更新和恢复状态均未确认
        /// </summary>
        private void CheckUpdateSuccess()
        {
            string baseDir = AppContext.BaseDirectory;
            string successPath = Path.Combine(baseDir, "update_success");
            string errorPath = Path.Combine(baseDir, "update_error.log");
            string inProgressPath = Path.Combine(baseDir, "update_in_progress");

            bool zh = _cfg.Language == "zh";
            string title = "LiteMonitor++_v" + UpdateChecker.GetCurrentVersion();

            if (File.Exists(successPath))
            {
                try { File.Delete(successPath); } catch { }
                // 成功时顺手清掉历史错误日志，避免下次启动误报失败
                try { if (File.Exists(errorPath)) File.Delete(errorPath); } catch { }
                try { if (File.Exists(inProgressPath)) File.Delete(inProgressPath); } catch { }

                string content = zh ? "软件已成功更新到最新版本！" : "Software updated to latest version!";
                ShowNotification(title, content, ToolTipIcon.Info);
                return;
            }

            if (File.Exists(errorPath))
            {
                // 更新器把恢复结果写在这里；读取后提示一次，避免每次启动都弹。
                string[] lines = Array.Empty<string>();
                try
                {
                    lines = File.ReadAllLines(errorPath);
                }
                catch { }

                string status = FindTaggedValue(lines, "[Result]", "status") ?? "unknown";
                string? backup = FindTaggedValue(lines, "[Result]", "backup");
                string? reason = FindTaggedLine(lines, "[Failure]")
                    ?? FindTaggedLine(lines, "[Summary]");

                try { File.Delete(errorPath); } catch { }
                try { File.Delete(inProgressPath); } catch { }

                string content;
                ToolTipIcon icon = ToolTipIcon.Warning;
                if (string.Equals(status, "recovered", StringComparison.OrdinalIgnoreCase))
                {
                    content = zh
                        ? AppendReason("上次更新失败，受影响文件已恢复。", reason)
                        : AppendReason("The last update failed, and all affected files were restored.", reason);
                }
                else if (string.Equals(status, "failed_before_commit", StringComparison.OrdinalIgnoreCase))
                {
                    content = zh
                        ? AppendReason("上次更新在提交前失败，目标文件未被修改。", reason)
                        : AppendReason("The last update failed before commit; target files were not changed.", reason);
                }
                else if (string.Equals(status, "recovery_incomplete", StringComparison.OrdinalIgnoreCase))
                {
                    icon = ToolTipIcon.Error;
                    content = zh
                        ? $"上次更新失败，恢复不完整。请使用保留的恢复材料人工检查。{BackupLocation(zh, backup)}"
                        : $"The last update failed and recovery is incomplete. Inspect the retained recovery materials.{BackupLocation(zh, backup)}";
                    content = AppendReason(content, reason);
                }
                else
                {
                    icon = ToolTipIcon.Error;
                    content = zh
                        ? $"上次更新失败，恢复状态未确认。请检查保留的更新材料。{BackupLocation(zh, backup)}"
                        : $"The last update failed and its recovery state is unconfirmed. Check the retained update materials.{BackupLocation(zh, backup)}";
                    content = AppendReason(content, reason);
                }

                ShowNotification(title, content, icon);
                return;
            }

            if (File.Exists(inProgressPath))
            {
                // 既无成功也无失败日志 = 更新器被强制结束（断电、任务管理器、安全软件拦截）。
                // 提示一次后删除标记；备份目录 _update_backup_* 本身保留，供人工恢复。
                string[] markerLines = Array.Empty<string>();
                try { markerLines = File.ReadAllLines(inProgressPath); } catch { }
                string? backup = FindTaggedValue(markerLines, null, "backup");
                try { File.Delete(inProgressPath); } catch { }

                string content = zh
                    ? $"上次更新被中断，当前安装状态待确认，部分文件可能已更新。请检查保留的恢复材料。{BackupLocation(zh, backup)}"
                    : $"The last update was interrupted. The current installation state is unconfirmed, and some files may already be updated. Check the retained recovery materials.{BackupLocation(zh, backup)}";
                ShowNotification(title, content, ToolTipIcon.Error);
            }
        }

        private static string? FindTaggedLine(string[] lines, string tag)
        {
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                int tagIndex = line.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (tagIndex >= 0)
                {
                    string value = line.Substring(tagIndex + tag.Length).Trim();
                    return value.Length == 0 ? null : value;
                }
            }

            return null;
        }

        private static string? FindTaggedValue(
            string[] lines,
            string? tag,
            string key)
        {
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                int tagIndex = 0;
                if (tag != null)
                {
                    tagIndex = line.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                    if (tagIndex < 0)
                    {
                        continue;
                    }
                }

                int keyIndex = line.IndexOf(
                    key + "=",
                    tagIndex + (tag?.Length ?? 0),
                    StringComparison.OrdinalIgnoreCase);
                if (keyIndex < 0) continue;

                int start = keyIndex + key.Length + 1;
                if (start >= line.Length) return null;

                if (line[start] == '"')
                {
                    int endQuote = line.IndexOf('"', start + 1);
                    if (endQuote < 0) return null;
                    return line.Substring(start + 1, endQuote - start - 1);
                }

                int end = start;
                while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
                return line.Substring(start, end - start);
            }

            return null;
        }

        private static string AppendReason(string message, string? reason) =>
            string.IsNullOrWhiteSpace(reason)
                ? message
                : message + Environment.NewLine + reason;

        private static string BackupLocation(bool zh, string? backup)
        {
            if (string.IsNullOrWhiteSpace(backup))
            {
                return zh
                    ? "\n恢复标记未提供备份位置，请检查更新目录。"
                    : "\nThe recovery marker did not provide a backup location; inspect the update directory.";
            }

            return zh
                ? "\n恢复材料：" + backup
                : "\nRecovery materials: " + backup;
        }

        public void Dispose()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _autoHideTimer?.Stop();
            _autoHideTimer?.Dispose();
            _topMostTimer?.Stop();
            _topMostTimer?.Dispose();
        }
    }
}
