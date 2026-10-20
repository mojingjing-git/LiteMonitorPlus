using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI;
using LiteMonitor.src.UI.Helpers;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private readonly int _wmTaskbarCreated;

        // ★ A13：窗口轮廓的唯一写入者（A09 的 WindowAppearanceController）。
        //   它负责 DWM 角偏好与 Region；本类只负责在正确的时机调用它。
        private readonly WindowAppearanceController _windowAppearance;

        private const int WM_DISPLAYCHANGE = 0x007E;
        private CancellationTokenSource _displayChangeCts;

        private Point _dragOffset;
        private bool _uiDragging = false;

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
        public void StopAutoHideTimer() => _bizHelper.StopTimer();
        public void HideTrayIcon() => _bizHelper.SetTrayVisible(false);
        public void ShowTrayIcon() => _bizHelper.SetTrayVisible(true);
        public void RebuildMenus() => _bizHelper.RebuildMenus();
        public void ShowNotification(string title, string text, ToolTipIcon icon) => _bizHelper.ShowNotification(title, text, icon);
        
        // 供 Helper 调用
        public void ToggleLayoutMode() => _bizHelper.ToggleLayoutMode();
        public void EnsureVisibleAndSavePos() => _bizHelper.SavePos();
        public void ApplyRoundedCorners() => _winHelper.ApplyRoundedCorners();
        public void RefreshTopMost(bool forceReinsert = false) => _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert);
        
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
            src.Plugins.PluginManager.Instance.LoadPlugins(Path.Combine(AppContext.BaseDirectory, "resources", "plugins"));
            src.Plugins.PluginManager.Instance.Start();

            // 启动自检：找出疑似覆盖内置插件的残留文件（如旧版市场落盘的 PublicIP.json），
            // 结果由插件市场页展示，提示用户删除
            src.Plugins.PluginSelfCheck.RunOnce();

            _ui = new UIController(_cfg, this);
            new src.WebServer.LiteWebServer(_cfg);

            // 5. 设置背景色 (这是关键！解耦时漏掉了这行，导致背景是系统默认色而非透明或皮肤色)
            BackColor = ThemeManager.ParseColor(ThemeManager.Current.Color.Background);

            // 2. 初始化双助手
            _winHelper = new MainFormWinHelper(this);

            // ★ A13：窗口轮廓交给 A09 的控制器统一负责（DWM 角偏好 + Region）。
            //   必须在 UI 线程构造（控制器记录构造线程 ID，并拒绝跨线程调用）。
            _windowAppearance = new WindowAppearanceController(this);
            _winHelper.AttachWindowAppearance(_windowAppearance);
            
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
                    _uiDragging = true;
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
                    _uiDragging = false;
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
            this.Resize += (_, __) => _winHelper.ApplyRoundedCorners();
            this.VisibleChanged += (_, __) => { if (Visible) _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true); };
            this.HandleCreated += (_, __) =>
            {
                _winHelper.RefreshTopMost(_cfg.TopMost, forceReinsert: true);
                // 句柄重建后旧窗口的 Region 已随旧窗口销毁，必须重新应用一次轮廓
                _windowAppearance?.OnHandleCreated();
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
        /// </summary>
        private void ApplyWindowAppearance()
        {
            if (_ui == null) return;

            ThemeAppearanceOptions? options = _ui.SyncAppearanceContext();
            if (options == null) return;

            // 半径取当前主题的 layout.cornerRadius —— 与 UIRenderer.RenderWindowFrame 传入的同一个值
            int cornerRadiusPx = ThemeManager.Current.Layout.CornerRadius;
            _windowAppearance?.Apply(options, cornerRadiusPx);
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
                // 必须由主窗口感知并重建 TaskbarForm，才能恢复显示。
                if (_cfg.ShowTaskbar)
                {
                    // 延迟执行，确保 Explorer 初始化基本完成，同时避免阻塞消息泵
                    this.BeginInvoke(new Action(async () =>
                    {
                        try 
                        {
                            await Task.Delay(3000); // 等待3秒让Explorer缓口气
                            ToggleTaskbar(false);   // 彻底销毁旧实例
                            ToggleTaskbar(true);    // 创建新实例(新实例自带重试机制)
                        }
                        catch { }
                    }));
                }
            }

            if (m.Msg == WM_DISPLAYCHANGE)
            {
                // [Fix #288] 分辨率改变后，延迟执行位置恢复，确保 Screen.AllScreens 已完全更新
                // 增加防抖机制，避免短时间内多次触发
                _displayChangeCts?.Cancel();
                _displayChangeCts = new System.Threading.CancellationTokenSource();
                var token = _displayChangeCts.Token;

                Task.Delay(500, token).ContinueWith(t => 
                {
                    if (!t.IsCanceled)
                    {
                        this.BeginInvoke(new Action(() => _bizHelper?.RestorePos()));
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
            base.OnHandleDestroyed(e);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // ApplyTheme 内部会重建外观快照并触发 AppearanceChanged → 轮廓随之更新
            _ui?.ApplyTheme(_cfg.Skin);
            ApplyWindowAppearance();
            this.Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            
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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cfg.Save(); 
            TrafficLogger.Save(); 
            HardwareHistoryLogger.Save();
            src.WebServer.LiteWebServer.Instance?.Stop();
            
            base.OnFormClosed(e);
            
            _ui?.Dispose();
            _bizHelper.Dispose();

            // ★ A13：释放窗口外观控制器（它负责把窗体上的 Region 交回 WinForms 释放）
            _windowAppearance?.Dispose();
        }
    }
}
