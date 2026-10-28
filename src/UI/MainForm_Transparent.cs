using LiteMonitor.Diagnostics;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI;
using LiteMonitor.src.UI.SettingsPage;
using LiteMonitor.src.WebServer;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class MainForm : Form
    {
        private readonly Settings _cfg = Settings.Load();
        private UIController? _ui;
        
        // ★★★ 双助手架构 ★★★
        private readonly MainFormWinHelper _winHelper;
        private readonly MainFormBizHelper _bizHelper;
        private readonly src.WebServer.LiteWebServer? _webServer;
        private readonly int _wmTaskbarCreated;

        // ★ A13：窗口轮廓的唯一写入者（A09 的 WindowAppearanceController）。
        //   它负责 DWM 角偏好与 Region；本类只负责在正确的时机调用它。
        private readonly WindowAppearanceController _windowAppearance;

        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int WM_WTSSESSION_CHANGE = 0x02B1;

        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        private const int WTS_SESSION_LOGON = 0x5;
        private const int WTS_SESSION_UNLOCK = 0x8;

        private const int NOTIFY_FOR_THIS_SESSION = 0;

        // ★ 可空：这个 CTS 只在收到 WM_DISPLAYCHANGE 时才创建（构造函数里并没有），
        //   原来的用法也是 "_displayChangeCts?.Cancel()" ⇒ 如实声明为可空。
        private CancellationTokenSource? _displayChangeCts;

        private Point _dragOffset;

        /// <summary>
        /// 【GPU T03 / 合同 §4 全局约束 / U10】窗体是否已进入关闭过程。
        /// <para>关闭拥有**最高优先级**：关闭过程中不得重建 HWND、不得恢复 affinity、
        /// 不得重新创建宿主。</para>
        /// </summary>
        private bool _isClosing;

        /// <summary>
        /// 【LM-095】关闭流程是否已经开始（<see cref="OnFormClosing"/> 之后为 true）。
        /// <para>★ 关闭拥有**最高优先级**：关闭过程中不得重建 HWND、也不得把捕获服务拉回来
        /// （合同 §4 全局约束 / U10）。</para>
        /// </summary>
        internal bool IsClosing => _isClosing;

        // 防止 Win11 自动隐藏无边框 + 无任务栏窗口
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
                cp.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
                if (_cfg != null && _cfg.TopMost)
                {
                    cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST，防止句柄重建后丢失置顶样式
                }
                
                // [Fix] 启动时应用鼠标穿透配置，防止因句柄重建导致样式丢失
                if (_cfg != null && _cfg.ClickThrough)
                {
                    cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                    cp.ExStyle |= 0x80000; // WS_EX_LAYERED (必须配合才能实现完整穿透)
                }

                return cp;
            }
        }

        // ========== 代理方法 (保持兼容性) ==========
        public void SetClickThrough(bool enable) => _winHelper.SetClickThrough(enable);
        public void InitAutoHideTimer() => _bizHelper.StartTimer();
        /// <summary>【R2】关闭自动隐藏：恢复泊出态；tick 常驻（看门狗继续值守）。</summary>
        public void DisableAutoHide() => _bizHelper.DisableAutoHide();
        public void HideTrayIcon() => _bizHelper.SetTrayVisible(false);
        public void ShowTrayIcon() => _bizHelper.SetTrayVisible(true);
        public void RebuildMenus() => _bizHelper.RebuildMenus();
        public void ShowNotification(string title, string text, ToolTipIcon icon) => _bizHelper.ShowNotification(title, text, icon);
        
        /// <summary>
        /// 【P-F06 / #612】主面板当前是否处于"自动隐藏已收起"状态（转发给业务助手）。
        /// <para>★ 用途：置顶看门狗在面板收起时<b>不得</b>重插 Z 序 —— 收起状态下重插没有意义，
        /// 还可能把本该藏起来的边缘露出来。规格要求"尊重自动隐藏行为"。</para>
        /// </summary>
        public bool IsAutoHidden => _bizHelper?.IsHidden ?? false;

        // 供 Helper 调用
        public void ToggleLayoutMode() => _bizHelper.ToggleLayoutMode();
        public void EnsureVisibleAndSavePos() => _bizHelper.SavePos();
        public void ApplyRoundedCorners() => _winHelper.ApplyRoundedCorners();
        public void RefreshTopMost(bool forceReinsert = false) => _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert);

        // =================================================================
        // 【P-F04】会话变化通知（锁屏解锁 / 登录 ⇒ 任务栏整层重建）
        // =================================================================

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        /// <summary>已注册会话变化通知的句柄（句柄重建后需要重新注册）。</summary>
        private IntPtr _sessionNotifiedHandle = IntPtr.Zero;

        /// <summary>
        /// 注册会话变化通知（<c>WM_WTSSESSION_CHANGE</c>）。
        /// <para>★ 锁屏/解锁会让 Explorer 重建整个任务栏层 —— 这是 #553 的关键触发源之一。
        /// 窗口销毁时系统会自动注销，这里只负责句柄重建后的重新注册。</para>
        /// </summary>
        private void RegisterSessionNotification()
        {
            try
            {
                if (!IsHandleCreated) return;
                if (_sessionNotifiedHandle == Handle) return;

                if (WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION))
                {
                    _sessionNotifiedHandle = Handle;
                }
            }
            catch
            {
                // 注册失败（极老系统 / 权限）⇒ 不影响其它恢复路径。
            }
        }

        // ================================================================
        // 【GPU T03 / 合同 §4 步 7、§8、§10 GPU-003】窗口切换相关入口
        // ================================================================

        /// <summary>
        /// 【GPU T03 / 合同 §10 GPU-003 / 裁决 D4】当前窗口是否处于分层窗口（<c>WS_EX_LAYERED</c>）。
        /// <para>只读；用于把真实值喂给 <c>RenderingRouteRequest.WindowIsLayered</c>。</para>
        /// </summary>
        public bool IsWindowLayered() => _winHelper.IsWindowLayered();

        /// <summary>
        /// 【GPU T03 / 合同 §4 步 4 / §8】MainForm 内部的**受控 HWND 重建薄入口**。
        /// <para>★ <c>MainForm</c> 是**非 partial** 的单文件类（全仓唯一声明在
        /// <c>src/UI/MainForm_Transparent.cs:14</c>）⇒ 本入口只能加在本文件内，
        /// 切换协调器另立独立类（计划 §4.3 :170-171）。</para>
        /// <para>★ <c>RecreateHandle()</c> 是 <see cref="Control.RecreateHandle"/>（<c>protected</c>）：
        /// 这里**直接调用**（本类成员），**绝不**用反射（合同 §4 步 4 的禁止动作 / T01 §7 约束 2）。</para>
        /// <para>★ 判别位：调用 <c>RecreateHandle()</c> **之前**先
        /// <see cref="UIController.RequestControlledRecreate"/> 置位；
        /// 抛异常 ⇒ 立即 <see cref="UIController.CancelControlledRecreate"/> 归零（合同 §8.1 规则 ②）。</para>
        /// <para>★ 关闭优先（合同 §4 全局约束）：关闭过程中**不得**重建 HWND。</para>
        /// </summary>
        /// <param name="reason">重建原因（诊断用）。</param>
        /// <returns><c>true</c> = 重建已发起。</returns>
        internal bool TryRecreateHandleForTransition(string reason)
        {
            if (_isClosing)
            {
                // 关闭拥有最高优先级：关闭过程中不得重建 HWND（合同 §4 全局约束 / U10）。
                return false;
            }

            long requestId = _ui?.RequestControlledRecreate(reason) ?? 0;
            if (requestId == 0)
            {
                return false;
            }

            try
            {
                // ★ 直接调用 protected 成员 —— 不用反射（合同 §4 步 4）。
                RecreateHandle();
                return true;
            }
            catch
            {
                // 规则 ②：RecreateHandle() 抛异常 ⇒ 立即取消判别位并归零。
                _ui?.CancelControlledRecreate(requestId);
                return false;
            }
        }
        
        // 供外部调用
        public void OpenTaskManager() => _bizHelper.OpenTaskManager();
        public void OpenSettings() => _bizHelper.OpenSettings();
        public void OpenTrafficHistory() => _bizHelper.OpenTrafficHistory();
        public void CleanMemory() => _bizHelper.CleanMemory();

        // ==== 任务栏显示 ====
        private TaskbarForm? _taskbar;

        public void ToggleTaskbar(bool show)
        {
            if (show)
            {
                if (_taskbar != null && !_taskbar.IsDisposed)
                {
                    if (_taskbar.TargetDevice != _cfg.TaskbarMonitorDevice)
                    {
                        _taskbar.Close();
                        _taskbar.Dispose();
                        _taskbar = null;
                    }
                }

                if (_taskbar == null || _taskbar.IsDisposed)
                {
                    if (_ui != null)
                    {
                        _taskbar = new TaskbarForm(_cfg, _ui, this);
                        _taskbar.Show();
                    }
                }
                else
                {
                    if (!_taskbar.Visible)
                    {
                        _taskbar.Show();
                        _taskbar.ReloadLayout();
                    }
                }
            }
            else
            {
                if (_taskbar != null)
                {
                    _taskbar.Close();
                    _taskbar.Dispose();
                    _taskbar = null;
                }
            }
        }

        // ========== 构造函数 ==========
        public MainForm()
        {
            // ★ 主窗体的身份标识：把窗口标题固定成**精确的** "LiteMonitor++"。
            //   用途：外部实机验收脚本（scripts/run-livecheck.ps1）靠"标题精确等于 LiteMonitor++"
            //   来唯一识别主窗体 —— 启动阶段的驱动安装对话框（UpdateDialog）标题形如
            //   "LiteMonitor++ - {说明}"，与主窗体同属一个进程，用精确相等就能把它排除掉。
            //   为什么设标题不会有任何可见外观变化：本窗体是 FormBorderStyle.None 的无边框透明窗口，
            //   且 ShowInTaskbar=false（见 MainFormWinHelper.InitializeStyle），本来就不画标题栏。
            //   ★ 标题必须**精确等于** "LiteMonitor++"，不要加" - 主界面"之类后缀，
            //     否则会被 UpdateDialog 的标题前缀规则误伤。
            Text = "LiteMonitor++";

            // 语言加载
            if (string.IsNullOrEmpty(_cfg.Language))
            {
                string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLower();
                string langPath = Path.Combine(AppContext.BaseDirectory, "resources/lang", $"{sysLang}.json");
                _cfg.Language = File.Exists(langPath) ? sysLang : "en";
            }
            LanguageManager.Load(_cfg.Language);
            _cfg.SyncToLanguage();

            // 1. 初始化业务
            // ★★★ Fix: 初始化全局 DPI 缩放系数，防止未打开设置面板时弹窗排版异常 ★★★
            UIUtils.ScaleFactor = this.DeviceDpi / 96f;

            TrafficLogger.Load();
            HardwareHistoryLogger.Load();

            // ★★ LM-083：安装事务恢复必须排在**任何加载 / 调度之前** ——
            //   上次进程被杀/崩溃可能留下"写了一半"的安装事务；恢复会把磁盘回滚到旧版本。
            //   若先加载再恢复，磁盘虽然回滚了，内存里跑的却仍是新版本 ⇒ 回滚承诺在进程内不成立。
            //   （插件管理器内部还有一道结构性兜底，见 PluginManager.LoadPlugins。）
            src.Plugins.PluginMarket.RecoverInterruptedInstalls();
            src.Plugins.PluginManager.Instance.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "resources", "plugins"));
            src.Plugins.PluginManager.Instance.Start();

            // 启动自检：找出疑似覆盖内置插件的残留文件（如旧版市场落盘的 PublicIP.json），
            // 结果由插件市场页展示，提示用户删除
            src.Plugins.PluginSelfCheck.RunOnce();

            // ★★★ 初始化顺序约束（R01，启动阻断）★★★
            //   `_winHelper` 必须在 `new UIController(...)` **之前**赋值。
            //   UIController 构造函数会走 ApplyTheme → ApplyGpuRenderingRoute →
            //   MainForm.IsWindowLayered()，而 IsWindowLayered() 直接解引用 `_winHelper`
            //   （`MainForm_Transparent.cs` 的转发方法）⇒ 晚一步赋值就是**启动即崩**的空引用，
            //   且与主题、GPU 开关、安全模式**全部无关**（该调用无条件求值）。
            //   实测复现：`.artifacts/r01-repro/mainform-construction-error.log`
            //   （NullReferenceException @ MainForm.IsWindowLayered）。
            //   安全性：MainFormWinHelper 的构造函数只保存窗体引用（不触碰 HWND、不依赖 _ui），
            //   提前赋值无任何副作用；`IsWindowLayered()` 本身在句柄未创建时安全返回 false。
            _winHelper = new MainFormWinHelper(this);

            _ui = new UIController(_cfg, this);
            _webServer = new src.WebServer.LiteWebServer(_cfg);

            try
            {
                DiagnosticHub.Current.SnapshotProvider = () => new DiagnosticSnapshotBuilder(new IDiagnosticProvider[]
                {
                    new HardwareDiagnosticProvider(
                        () => _ui.HardwareClient.GetHealth(),
                        () => _ui.HardwareClient.GetLatestSnapshot(),
                        _ui.HardwareSamplingStatistics),
                    new RenderingDiagnosticProvider(
                        () => new RenderingRouteStatusSnapshot(
                            ThemeUsesGpuMaterial: ThemeManager.Current.Rendering is { } rendering &&
                                string.Equals(rendering.Backend?.Trim(), ThemeRenderingConfig.GpuBackend,
                                    StringComparison.OrdinalIgnoreCase),
                            GpuEffectsEnabled: _cfg.GpuThemeEffectsEnabled,
                            PresentationActive: _ui.IsGpuPresentationActive,
                            LiveBackgroundActive: _ui.IsGpuLiveBackgroundActive,
                            LiveBackgroundRequested: _ui.IsGpuLiveBackgroundRequested,
                            RuntimeFailure: _ui.GpuRuntimeFailure,
                            FallbackCode: _ui.GpuFallbackCode,
                            WindowIsLayered: _winHelper.IsWindowLayered())),
                    new WebDiagnosticProvider(
                        () => _cfg.WebServerEnabled,
                        () => LiteWebServer.Instance),
                    new PluginDiagnosticProvider(
                        () => _cfg.PluginInstances.Count,
                        () => _cfg.PluginInstances.Count(i => i.Enabled)),
                    new IpcDiagnosticProvider(),
                }).Build();
                DiagnosticHub.Current.Record("app", DiagnosticSeverity.Info, "started");
                DiagnosticRunMarker.PreviousRun previous = DiagnosticRunMarker.ReadAndBegin();
                if (previous.HadMarker)
                {
                    DiagnosticHub.Current.Record(
                        "app",
                        previous.CleanShutdown ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
                        previous.CleanShutdown ? "previous-run-clean" : "previous-run-unclean",
                        context: new Dictionary<string, string>
                        {
                            ["startedAtUtc"] = previous.StartedAtUtc?.ToString("O") ?? string.Empty,
                            ["endedAtUtc"] = previous.EndedAtUtc?.ToString("O") ?? string.Empty,
                        });
                }
            }
            catch
            {
                // 诊断不得阻断启动。
            }

            // 5. 设置背景色 (这是关键！解耦时漏掉了这行，导致背景是系统默认色而非透明或皮肤色)
            BackColor = ThemeManager.ParseColor(ThemeManager.Current.Color.Background);

            // ★ A13：窗口轮廓交给 A09 的控制器统一负责（DWM 角偏好 + Region）。
            //   必须在 UI 线程构造（控制器记录构造线程 ID，并拒绝跨线程调用）。
            _windowAppearance = new WindowAppearanceController(this);
            _winHelper.AttachWindowAppearance(_windowAppearance);

            // ★ R4：把"当前外观快照"的取法交给控制器 ——
            //   Resize 时窗口轮廓必须按**新客户区尺寸**重新取一次快照，
            //   否则会沿用旧快照里已被旧高度夹紧的切角长度（轮廓几何与绘制层几何分叉）。
            //   取法与绘制层完全一致：同一个 SyncAppearanceContext + 同一个 layout.cornerRadius。
            _windowAppearance.SnapshotProvider = CurrentAppearanceSnapshot;
            
            //资源管理器重启监听
            _wmTaskbarCreated = MainFormWinHelper.RegisterTaskbarCreatedMessage();

            // ★★★ 关键修复：补全 SetStyle 调用，开启透明支持 ★★★
            // 原始代码中这里调用了 SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            // 解耦时漏掉了这一行，导致背景无法透明，显示为黑色或系统默认色
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _winHelper.InitializeStyle(_cfg.TopMost, _cfg.ClickThrough);

            // 原始代码还原：这里需要手动设置 Opacity = 0，
            // 但是要在构造函数里设置，和原始代码保持一致的位置
            this.Opacity = 0; 

            _bizHelper = new MainFormBizHelper(this, _cfg, _ui, _winHelper);
            _bizHelper.Initialize();

            // === 渐入透明度 (还原原始代码逻辑) ===
            // 原始代码是在构造函数末尾启动 Task
            // 之前解耦时移到了 OnShown 里，这可能导致时序差异（OnShown 之前会有一瞬间的默认绘制）
            _winHelper.StartFadeIn(_cfg.Opacity);

            // 3. 事件绑定
            BindEvents();
        }

        private void BindEvents()
        {
            // 拖拽
            MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(true);
                    _bizHelper.IsDragging = true;
                    _dragOffset = e.Location;
                }
            };
            MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.X - _dragOffset.X) + Math.Abs(e.Y - _dragOffset.Y) < 1) return;
                    Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);
                }
            };
            MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _ui?.SetDragging(false);
                    _bizHelper.IsDragging = false;
                    _bizHelper.ClampToScreen(); 
                    _bizHelper.SavePos();
                }
            };

            // 双击
            this.DoubleClick += (_, __) => _bizHelper.HandleDoubleClick();
            
            // DPI / Resize
            // ★ A13：窗口轮廓的 Resize 绑定**只有这一处**。
            //   MainFormWinHelper 里原先那份已删除 —— 两处绑定会把轮廓重建两遍。
            //   ★ 尺寸变化时轮廓会按**当前**快照重算（切角边长按新高度重新夹紧），
            //   见 WindowAppearanceController.SnapshotProvider。
            this.Resize += (_, __) =>
            {
                _winHelper.ApplyRoundedCorners();
                // ★ A16：尺寸代次 +1 并更新捕获请求
                //   （窗口大小变了，客户区的**物理**矩形也跟着变，坐标不变就会抓错区域）。
                _ui?.NotifyLiveGlassSizeChanged();
                _ui?.NotifyGpuSizeChanged();
            };
            this.LocationChanged += (_, __) => _ui?.NotifyLiveGlassLocationChanged();
            this.VisibleChanged += (_, __) =>
            {
                if (Visible) _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
                // ★ A16：隐藏 / 最小化 ⇒ 暂停捕获；重新显示 ⇒ 继续（合同 §5.1）。
                _ui?.NotifyLiveGlassVisibility(Visible);
                _ui?.NotifyGpuVisibility(Visible);
                if (Visible && _ui?.IsGpuPresentationActive == true)
                    ApplyWindowAppearance();
            };
            this.HandleCreated += (_, __) =>
            {
                // ★★【GPU T03 / 合同 §7.2 / 裁决 D1】句柄代次推进 = **第一行** ★★
                //   唯一递增点 = WindowHandleGeneration.Advance()；唯一驱动者 = 协调器（owner B）。
                //   必须**先于** NotifyLiveGlassHandleChanged()（下方）与 NotifyGpuHandleChanged()
                //   ⇒ live 捕获链与 GPU 清理链读到**同一个**新代次（合同 §7.2"递增顺序"）。
                long handleGeneration = WindowHandleGeneration.Instance.Advance();

                // ★【GPU T03 / 合同 §8.2】判别位：区分"受控重建后创建"与"构造期首次创建"。
                //   controlled == false ⇒ 构造期首次创建 / 非受控创建
                //   ⇒ **禁止**拉起 GPU（计划 §4.2 :147，既有设计必须保持）；
                //   但 RefreshTopMost / OnHandleCreated / NotifyLiveGlassHandleChanged
                //   仍必须**照旧**执行（合同 §8.2 规则 3）。
                bool controlled = _ui?.TryConsumeControlledRecreate(out long recreateRequestId) == true;

                _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
                // 句柄重建后旧窗口的 Region 已随旧窗口销毁，必须重新应用一次轮廓
                _windowAppearance?.OnHandleCreated();
                // ★ A16：句柄代次 +1（句柄变了，旧请求里的句柄已失效，必须换新的）。
                //   代次已在上方推进 ⇒ 这里消费的是**新**代次。
                _ui?.NotifyLiveGlassHandleChanged();
                _ui?.NotifyGpuHandleChanged(controlled, handleGeneration);

                // ★ P-F04：注册会话变化通知（锁屏解锁 / 登录）⇒ 任务栏整层重建后可自愈。
                RegisterSessionNotification();
            };

            // 换肤 / DPI 变化 → 重新应用窗口轮廓（外框样式可能在切角与圆角之间切换）
            if (_ui != null) _ui.AppearanceChanged += ApplyWindowAppearance;
        }

        /// <summary>
        /// 用当前外观快照重新应用窗口轮廓（DWM 角偏好 + Region）。
        /// <para>
        /// ★ 窗口轮廓的**唯一写入者**是 <see cref="WindowAppearanceController"/>（A09）；
        ///   本类只负责在正确的时机调用它：换肤、DPI 变化、尺寸变化、句柄重建、首次显示。
        /// </para>
        /// <para>
        /// 圆角半径必须与绘制层的外框用**同一个值**（主题的 <c>layout.cornerRadius</c>，
        /// 已按布局缩放换算成物理像素），否则窗口轮廓与画出来的边框会错位
        /// （v4 §3.4「切角使用与画框相同几何」）。
        /// </para>
        /// <para>
        /// ★【GPU T03 / 合同 §4 步 8】过渡完成后需要**显式**重放一次窗口轮廓
        ///   （"所有状态稳定后发出一次有效重绘并应用窗口轮廓"，计划 §4.3 :176）
        ///   ⇒ 可见性由 private 放宽为 internal。
        /// </para>
        /// </summary>
        internal void ApplyWindowAppearance()
        {
            WindowAppearanceSnapshot? snapshot = CurrentAppearanceSnapshot();
            if (!snapshot.HasValue) return;

            ThemeAppearanceOptions? options = snapshot.Value.Options;
            if (options == null) return;

            _windowAppearance?.Apply(options, snapshot.Value.CornerRadiusPx, useMaterialOutline: snapshot.Value.UseMaterialOutline);
        }

        /// <summary>
        /// 取**当前**的外观快照与圆角半径 —— 窗口轮廓与绘制层的唯一同源来源。
        /// <para>
        /// ★ 快照来自 <c>SyncAppearanceContext()</c>（绘制层每帧用的就是它，
        ///   里面的 <c>FrameLengthPx</c> 已按**当前**客户区高度夹紧）；
        ///   半径取主题的 <c>layout.cornerRadius</c>（绘制层外框用的也是它）。
        /// </para>
        /// </summary>
        private WindowAppearanceSnapshot? CurrentAppearanceSnapshot()
        {
            ThemeAppearanceOptions? options = _ui?.SyncAppearanceContext();
            if (options == null) return null;
            var rendering = ThemeManager.Current.Rendering;
            if (_ui?.IsGpuPresentationActive == true &&
                rendering?.Material == LiteMonitor.Rendering.Configuration.ThemeRenderingConfig.LiquidGlassMaterial)
            {
                var settings = LiteMonitor.Rendering.Configuration.MaterialRuntimeSettings.Create(
                    rendering, LiteMonitor.Rendering.Contracts.MaterialKind.LiquidGlass);
                if (settings.GlassStyle == LiteMonitor.Rendering.Contracts.GlassSurfaceStyle.FloatingToolbar)
                {
                    int radius = LiteMonitor.Rendering.Materials.SceneMaterialLayoutBuilder.ToolbarCornerRadius(
                        settings.CornerRadius, DeviceDpi, (float)_cfg.UIScale, ClientSize.Width, ClientSize.Height);
                    return new WindowAppearanceSnapshot(options, radius, useMaterialOutline: true);
                }
                int standardRadius = (int)Math.Min(settings.CornerRadius, Math.Min(ClientSize.Width, ClientSize.Height) / 2f);
                return new WindowAppearanceSnapshot(options, standardRadius, useMaterialOutline: true);
            }
            return new WindowAppearanceSnapshot(options, ThemeManager.Current.Layout.CornerRadius);
        }

        public void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            _cfg.HideMainForm = false;
            _cfg.Save();

            _bizHelper.ForceShow();
            _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
            _bizHelper.RebuildMenus();
        }

        public void HideMainWindow()
        {
            this.Hide();
            _cfg.HideMainForm = true;
            _cfg.Save();
            _bizHelper.RebuildMenus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
            {
                // [Fix] Explorer 重启后，子窗口 TaskbarForm 会被销毁或失效。
                // ★ P-F04：不再"销毁 + 重建整个 TaskbarForm"，而是**投递一次恢复请求** ——
                //   Explorer 重启后根 HWND 可能换了、子层可能重建，但面板本身（及其定时器）应当保持**单份**。
                //   恢复由任务栏恢复协调器统一去抖 + 有界退避完成（#553 / #573 / #583）。
                if (_cfg.ShowTaskbar)
                {
                    if (_taskbar != null && !_taskbar.IsDisposed)
                    {
                        _taskbar.RequestTaskbarRecovery(TaskbarRecoveryReason.ExplorerRestarted);
                    }
                    else
                    {
                        // 没有任务栏窗体可恢复 ⇒ 延迟新建一个（等 Explorer 缓口气）。
                        // 恢复失败必须留痕并再试一次：Explorer 重启后的面板重建是核心 UX，
                        // 静默失败会让任务栏区域永久消失且没有任何诊断线索。
                        this.BeginInvoke(new Action(async () =>
                        {
                            try
                            {
                                await Task.Delay(3000);
                                ToggleTaskbar(true);
                            }
                            catch (Exception ex)
                            {
                                HardwareErrorLog.Write("TaskbarRecovery.DelayedCreate", ex);
                                try
                                {
                                    await Task.Delay(1000);
                                    ToggleTaskbar(true);
                                }
                                catch (Exception retryEx)
                                {
                                    HardwareErrorLog.Write("TaskbarRecovery.DelayedCreate.Retry", retryEx);
                                }
                            }
                        }));
                    }
                }
            }

            if (m.Msg == WM_POWERBROADCAST)
            {
                int powerEvent = m.WParam.ToInt32();
                if (powerEvent == PBT_APMRESUMESUSPEND ||
                    powerEvent == PBT_APMRESUMEAUTOMATIC ||
                    powerEvent == PBT_APMSUSPEND)
                {
                    // ★ 睡眠 / 唤醒 / Modern Standby：任务栏根 HWND 往往仍有效，
                    //   但其子窗口、Z 序和显示器坐标已经被重建 ⇒ 投递恢复请求（#553）。
                    _taskbar?.RequestTaskbarRecovery(TaskbarRecoveryReason.PowerResume);
                }
            }

            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int sessionEvent = m.WParam.ToInt32();
                if (sessionEvent == WTS_SESSION_UNLOCK || sessionEvent == WTS_SESSION_LOGON)
                {
                    // ★ 锁屏解锁 / 会话切换：任务栏整层会被重建 ⇒ 投递恢复请求（#553）。
                    _taskbar?.RequestTaskbarRecovery(TaskbarRecoveryReason.SessionChange);
                }
            }

            if (m.Msg == WM_SETTINGCHANGE)
            {
                // ★ 显示设置变化（任务栏大小 / 位置 / 自动隐藏）⇒ 任务栏矩形缓存必须失效并重新定位（#539）。
                _taskbar?.RequestTaskbarRecovery(TaskbarRecoveryReason.SettingChange);

                // ★ P-F06 / #599：任务栏设置变化同样会重建合成层 ⇒ 立即检查一次同级 Z 序。
                _taskbar?.NotifyDisplayChanged();
            }

            if (m.Msg == WM_DISPLAYCHANGE)
            {
                // [Fix #288] 分辨率改变后，延迟执行位置恢复，确保 Screen.AllScreens 已完全更新
                // 增加防抖机制，避免短时间内多次触发
                _displayChangeCts?.Cancel();
                // ★ 字段声明为可空，这里用局部量承接新实例：取 Token 时保证一定非空
                //   （不要直接写 _displayChangeCts.Token：字段的 null 状态在方法调用后会退回"可能为空"）。
                var displayChangeCts = new System.Threading.CancellationTokenSource();
                _displayChangeCts = displayChangeCts;
                var token = displayChangeCts.Token;

                // ★ 任务栏侧：拓扑/分辨率变化后必须重新解析目标设备并重挂（#539 / #573 / #583）。
                _taskbar?.RequestTaskbarRecovery(TaskbarRecoveryReason.DisplayChange);

                // ★ P-F06 / #599：显示变化会重建任务栏的合成层 ⇒ 立即检查一次同级 Z 序，
                //   并排定有限次后续检查覆盖迟到的合成层（与上面的恢复请求互不替代）。
                _taskbar?.NotifyDisplayChanged();

                Task.Delay(500, token).ContinueWith(t => 
                {
                    if (!t.IsCanceled)
                    {
                        if (IsDisposed || Disposing || !IsHandleCreated) return;
                        this.BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed || Disposing) return;
                            _bizHelper?.ReconcileDisplayChange();
                            _ui?.NotifyGpuDisplayChanged();
                        }));
                    }
                });
            }

            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _ui?.Render(e.Graphics);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            // ★ A13：句柄销毁后清掉窗口级状态（Region 交回 WinForms 释放），
            //   避免句柄重建时把"上一次尺寸的旧 Region"再套到新窗口上。
            _windowAppearance?.OnHandleDestroyed();
            // ★ A16：句柄没了 ⇒ 捕获必须停（否则会对着已失效的句柄抓屏）。
            //   句柄重建后由 HandleCreated 事件重新接上。
            // ★【LM-072 / R08】句柄销毁是**临时停机**，不是用户关闭：
            //   只停服务、**保留**配置态的开关；重建完成后由 UIController 按配置恢复。
            //   （真关闭在 OnFormClosed 里整停 —— 那时才允许把开关置 false。）
            _ui?.StopLiveGlassForHandleRecreate();
            _ui?.StopGpuPresentation();
            // ★ P-F04：会话变化通知随句柄一起失效（系统会在窗口销毁时自动注销，这里只清标记）。
            _sessionNotifiedHandle = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // ApplyTheme 内部会重建外观快照并触发 AppearanceChanged → 轮廓随之更新
            _ui?.ApplyTheme(_cfg.Skin);
            // ★ A16：DPI 变了 ⇒ 客户区的物理尺寸与坐标都变了 ⇒ 尺寸代次 +1。
            _ui?.NotifyLiveGlassSizeChanged();
            _ui?.NotifyGpuSizeChanged();
            ApplyWindowAppearance();
            // ★ P-F04：任务栏侧的 DPI 变化同样要重解析目标设备、重算任务栏矩形并重挂（#539）。
            _taskbar?.RequestTaskbarRecovery(TaskbarRecoveryReason.DpiChanged);

            // ★ P-F06 / #599：DPI 变化会重建任务栏合成层 ⇒ 立即检查一次同级 Z 序。
            _taskbar?.NotifyDisplayChanged();
            this.Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // HandleCreated owns GPU-host creation. Rebuilding the host here
            // would replace the session that VisibleChanged just resumed and
            // turn every show into an unnecessary device/capture teardown.
            _ui?.NotifyGpuSizeChanged();

            // ★ 句柄可能早于 BindEvents() 就已存在（主题应用里的 CreateGraphics()
            //   会创建句柄），HandleCreated 不再触发，GPU 宿主就永远不会被启动。
            //   这里只在宿主缺席时补建，不触碰已存在的会话。
            _ui?.EnsureGpuPresentationStarted();

            // 恢复可见性
            if (_cfg.HideMainForm) this.Hide();
            
            this.Update();
            
            // 恢复位置
            _bizHelper.RestorePos();

            // 确保渲染尺寸正确 (横屏模式)
            if (_cfg.HorizontalMode && _ui != null)
            {
                this.Size = new Size(this.Width, this.Height);
            }
            
            // 移除了 StartFadeIn 调用，因为它已经还原回构造函数了
            // ★ A13：首次显示时用当前主题的外观参数正式应用窗口轮廓。
            //   此时客户区尺寸已最终确定（Reapply 此前还没有任何样式参数可用）。
            ApplyWindowAppearance();
            _bizHelper.KeepVisible(3.0); // 启动保护期

            if (_cfg.ShowTaskbar) ToggleTaskbar(true);

            // 启动 WebServer
            if (_cfg.WebServerEnabled)
            {
                if (src.WebServer.LiteWebServer.Instance?.Start(out string err) == false)
                {
                     ShowNotification("WebServer Error", 
                         (_cfg.Language == "zh" ? "Web服务启动失败: " : "Web Server Failed: ") + err, 
                         ToolTipIcon.Error);
                }
            }
            // 这样既检查了驱动，也检查了更新，以及置顶 透明度 穿透 等，而且时机完美（窗口显示后）
            if (_bizHelper != null)
            {
                 _ = _bizHelper.RunStartupChecksAsync();
            }
            // [Fix] 强制置顶刷新，增加重试机制确保在某些系统环境下依然生效
            if (_cfg.TopMost)
            {
                this.BeginInvoke(new Action(async () =>
                {
                    await Task.Delay(3000);
                    _winHelper.RefreshTopMost(true, forceReinsert: true);
                }));
            }
        }

        /// <summary>
        /// 【GPU T03 / 合同 §4 全局约束 / U10】关闭**最高优先级**的入口。
        /// <para>关闭过程中：不得重建 HWND、不得恢复 affinity、不得重新创建宿主
        /// （计划 §4.2 :162 / §4.3 :179"若当前窗体已进入关闭过程，直接执行清理而不恢复"）。</para>
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _isClosing = true;
            _ui?.NotifyFormClosing();

            // ★ P-F04：关闭拥有最高优先级 —— 先把任务栏面板收掉：
            //   取消排队的恢复请求、注销后续动作、**禁止在关闭过程中复活窗口**
            //   （母计划 F04 第 6 步：关闭过程不新建 HWND）。
            if (_taskbar != null && !_taskbar.IsDisposed)
            {
                _taskbar.Close();
                _taskbar.Dispose();
                _taskbar = null;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // ★ INF-07：退出路径逐项隔离。任一资源释放失败，不得阻断后续清理。
            //   先落盘，再停服务；日志/设置保存失败也要继续关闭。
            SafeShutdown("Settings.Save", () => _cfg.Save());
            SafeShutdown("TrafficLogger.Save", TrafficLogger.Save);
            SafeShutdown("HardwareHistoryLogger.Save", HardwareHistoryLogger.Save);
            SafeShutdown("PluginManager.Stop", () => src.Plugins.PluginManager.Instance.Stop());
            SafeShutdown("LiteWebServer.Dispose", () => _webServer?.Dispose());

            base.OnFormClosed(e);

            // ★ A16：退出前停掉后台捕获服务（恢复显示亲和性，避免窗口在用户录屏里永久消失）。
            //   INF-07：直接调用并就地隔离异常；不能包进 lambda，否则 IL 护栏看不到整停调用。
            try
            {
                _ui?.StopLiveGlass();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[INF-07] shutdown step failed: UIController.StopLiveGlass: {ex}");
            }
            SafeShutdown("UIController.Dispose", () => _ui?.Dispose());
            SafeShutdown("MainFormBizHelper.Dispose", () => _bizHelper.Dispose());

            // ★ A13：释放窗口外观控制器（它负责把窗体上的 Region 交回 WinForms 释放）
            SafeShutdown("WindowAppearance.Dispose", () => _windowAppearance?.Dispose());
            SafeShutdown("DiagnosticRunMarker.MarkClean", () => DiagnosticRunMarker.MarkCleanShutdown());
        }

        private static void SafeShutdown(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 退出阶段不得再弹窗或抛异常；留下可诊断痕迹即可。
                System.Diagnostics.Debug.WriteLine($"[INF-07] shutdown step failed: {name}: {ex}");
            }
        }
    }
}
