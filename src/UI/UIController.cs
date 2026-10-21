using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Scene;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LiteMonitor
{
    public class UIController : IDisposable
    {
        private readonly Settings _cfg;
        private readonly Form _form;
        private readonly HardwareMonitor _mon;
        private readonly System.Windows.Forms.Timer _timer;

        private UILayout _layout;
        private bool _layoutDirty = true;
        private bool _dragging = false;
        private string _lastLayoutSignature = "";

        private List<GroupLayoutInfo> _groups = new();
        private bool _gpuLegacyLabelMigrated = false;
        private bool _hardwareErrorNotified = false;
        private List<Column> _hxColsHorizontal = new();
        private List<Column> _hxColsTaskbar = new();
        private HorizontalLayout? _hxLayout;
        private Dictionary<string, DateTime> _overheatStartTimes = new Dictionary<string, DateTime>();

        // ===== 外观运行时（A13 接线）=====
        // 唯一的外观宿主：背景层 / 装饰层 / 进度条层都从这里取，
        // 避免出现第二处挂载点（v4 §3.3「每项入口唯一」）。
        private AppearanceRuntime? _appearance;
        // 与 t.Scale(dpiScale, userScale) 使用的**同一对**缩放系数。
        // DPI 变化会经 OnDpiChanged → ApplyTheme 重新计算，这里跟着更新。
        private float _appearanceDpiScale = 1f;
        private float _appearanceUserScale = 1f;

        // ===== 实时玻璃（M2 / A16）=====
        // ★ 唯一的接线宿主：负责后台捕获服务的生命周期 + 每帧租约交接。
        //   它**不是**第二个捕获循环 —— 真正的循环只有服务内部那一个（合同 §13.2 / §13.5）。
        private LiveGlassController? _liveGlass;

        // ===== GPU 材质路由（R19）=====
        // 只有主题显式声明 rendering.backend=gpu 且用户独立开启后才创建。
        // 旧主题与旧 GDI+ 路径在此字段为 null 时保持原有行为。
        private GpuPresentationHostController? _gpuPresentation;
        private RenderingRouteDecision? _gpuRoute;
        private string? _gpuFallbackCode;
        private bool _updatingGpuScene;

        /// <summary>
        /// 外观上下文变化事件（换肤 / DPI 变化 / 布局重建后触发）。
        /// <para>
        /// A13 用它让**窗口轮廓**跟着重新应用：换肤会改变外框样式
        /// （切角 ⇄ 圆角），必须重新写一次 DWM 角偏好与 Region。
        /// </para>
        /// </summary>
        public event Action? AppearanceChanged;

        /// <summary>外观运行时（A13 接线用；窗口轮廓控制器从这里取快照）。</summary>
        public AppearanceRuntime? Appearance => _appearance;

        /// <summary>当前外观快照（尚未初始化时为 null）。</summary>
        public ThemeAppearanceOptions? AppearanceOptions => _appearance?.Options;

        public MainForm MainForm => (MainForm)_form;
        
        // 辅助判断当前渲染状态是否为横屏 (用于 AppActions 判断是否切换了模式)
        // 只有在 Render 并在横屏分支运行后 _hxLayout 才不为 null
        // 竖屏模式下 ApplyTheme 会将其重置为 null
        public bool IsLayoutHorizontal => _hxLayout != null;

        public List<Column> GetTaskbarColumns() => _hxColsTaskbar;
        public List<GroupLayoutInfo> GetMainGroups() => _groups;

        public bool IsGpuPresentationActive =>
            _gpuPresentation?.IsActive == true;

        public string? GpuFallbackCode => _gpuFallbackCode;
        public bool IsGpuLiveBackgroundActive => _gpuPresentation?.IsLiveCaptureStarted == true;
        public bool IsGpuLiveBackgroundRequested => _gpuRoute?.CaptureRequested == true;
        public string? GpuRuntimeFailure => _gpuPresentation?.LastErrorCode ?? _gpuFallbackCode;

        public UIController(Settings cfg, Form form)
        {
            _cfg = cfg;
            _form = form;
            _mon = new HardwareMonitor(cfg);

            _layout = new UILayout(ThemeManager.Current);

            _timer = new System.Windows.Forms.Timer { Interval = ComputeUiRefreshIntervalMs(_cfg.RefreshMs) };
            _timer.Tick += (_, __) => Tick();
            _timer.Start();

            // ★ A16：实时玻璃接线控制器。
            //   必须在 ApplyTheme **之前**建好 —— ApplyTheme 会创建外观运行时，
            //   而外观运行时需要拿到实时玻璃的帧来源代理。
            //   代理用的是闭包而不是直接引用本对象：构造期间 _appearance 还不存在。
            _liveGlass = new LiveGlassController(form, () => _appearance?.Options);

            ApplyTheme(_cfg.Skin);

            // ★ 配置里的开关在启动时就生效（用户上次是开着的 ⇒ 这次直接开）。
            //   默认值是 false，所以绝大多数用户不会走到这里。
            if (_cfg.LiveGlassEnabled)
            {
                _liveGlass.SetEnabled(true);
            }
        }

        public float GetCurrentDpiScale()
        {
            using (Graphics g = _form.CreateGraphics())
            {
                return g.DpiX / 96f;
            }
        }

        public void ApplyTheme(string name, bool retainData = false)
        {
            // 1. 先保留旧主题的引用 (为了稍后释放)
            var oldTheme = ThemeManager.Current;

            // 2. 清理全局画刷缓存 (这不会影响 ThemeManager 的字体了，因为解耦了)
            UIRenderer.ClearCache();
            UIUtils.ClearBrushCache();

            // 3. 加载新主题 (Current 指向新对象，包含全新的字体)
            // 如果主题名相同且要求保留数据，可以考虑跳过 Load，但为了应用 Scale 还是重新 Load 比较稳妥
            // 或者优化：ThemeManager.Load 内部判断是否已加载
            ThemeManager.Load(name);
            var t = ThemeManager.Current;

            // 4. 安全释放旧主题的字体
            // 此时 Current 已经是新主题了，Paint 事件只会用新字体，所以释放旧的是安全的
            if (oldTheme != null && oldTheme != t)
            {
                oldTheme.DisposeFonts();
            }

            // ... 后续缩放逻辑保持不变 ...
            float dpiScale = GetCurrentDpiScale();   
            float userScale = (float)_cfg.UIScale;    
            float finalScale = dpiScale * userScale;

            t.Scale(dpiScale, userScale); // Scale 内部现在会自动清理旧缩放字体

            // ... 边距修复逻辑 ...
            if (!_cfg.HorizontalMode)
            {
                t.Layout.Width = (int)(_cfg.PanelWidth * finalScale);
                _form.ClientSize = new Size(t.Layout.Width, _form.ClientSize.Height);
            }

            // ---- 外观运行时（A13）----
            // ★ DPI 与用户缩放必须与上面 t.Scale(dpiScale, userScale) 用的**同一对值**：
            //   主题里的旧字段已经被 LayoutScale(=dpi*user) 缩放过一次，
            //   而新的外观字段故意没被缩放，要由 options 用同一个系数换算成物理像素。
            //   传错就会出现"装饰尺寸与布局尺寸比例不一致"。
            _appearanceDpiScale = dpiScale;
            _appearanceUserScale = userScale;

            // ★ A16：帧来源用**实时玻璃代理**。代理是终身唯一的中转对象，
            //   开关只切换它背后的服务 —— 外观运行时不必重建，装饰/纹理缓存得以保留。
            _appearance ??= new AppearanceRuntime(frameSource: _liveGlass?.FrameSource);
            // 换肤 → 主题代次自增，各层缓存按代次失效
            _appearance.OnThemeChanged(t, dpiScale, userScale, _form.ClientRectangle);

            // ★ A16：主题代次变了 ⇒ 捕获请求里的主题代次要跟着更新，
            //   否则后台会一直用旧代次的请求抓屏（帧会被当作代次不匹配丢弃）。
            _liveGlass?.OnAppearanceChanged();

            // Web 端颜色快照：启动时与每次换肤各发布一次
            // （网页端刻意不推送、不轮询 —— 浏览器刷新后取到新配色）
            WebThemeColorPublisher.PublishFromTheme(t);

            TaskbarRenderer.ReloadStyle(_cfg);

            _layout = new UILayout(t);
            _hxLayout = null;

            if (!retainData)
            {
                BuildMetrics();
                BuildHorizontalColumns();
            }
            else
            {
                // [Safety Check] Even if retaining data, ensure we have content.
                // This handles cases where we switch modes but data wasn't built for that mode yet.
                if (_groups.Count == 0) BuildMetrics();
                if (_hxColsHorizontal.Count == 0) BuildHorizontalColumns();
            }
            
            _layoutDirty = true;

            _form.BackColor = ThemeManager.ParseColor(t.Color.Background);

            _timer.Interval = ComputeUiRefreshIntervalMs(_cfg.RefreshMs);
            _form.Invalidate();
            _form.Update();

            // 换肤后客户区尺寸可能已变（竖屏会改 ClientSize）→ 同步一次外观上下文，
            // 保证订阅方拿到的快照里 ClientBounds 是最新的（装饰层与窗口轮廓都按它定位）。
            SyncAppearanceContext();
            ApplyGpuRenderingRoute(t);
            AppearanceChanged?.Invoke();
        }

        public void RebuildLayout()
        {
            BuildMetrics();
            BuildHorizontalColumns(); 
            _layoutDirty = true;
            UpdateGpuScene();
            _form.Invalidate();
            _form.Update();
        }

        public void SetDragging(bool dragging) => _dragging = dragging;

        // ================================================================
        // 实时玻璃（M2 / A16）—— 供主窗体事件与设置页调用
        // ================================================================

        /// <summary>实时玻璃是否已开启（配置值）。</summary>
        public bool LiveGlassEnabled => _cfg.LiveGlassEnabled;

        /// <summary>实时玻璃的**实际**状态说明（人类可读）—— 设置页展示用。</summary>
        public string LiveGlassStatusText =>
            _liveGlass?.StatusText ?? "实时玻璃未启用。";

        /// <summary>
        /// 打开 / 关闭实时玻璃。
        /// <para>★ 配置与服务状态**必须同步**：配置说"开"而服务没起来时，
        ///   状态说明会显示真实原因（合同 §7「降级不静默」、A16「配置和实际降级状态一致」）。</para>
        /// </summary>
        public void SetLiveGlassEnabled(bool enabled)
        {
            _cfg.LiveGlassEnabled = enabled;
            _liveGlass?.SetEnabled(enabled);
        }

        /// <summary>窗口可见性变化（隐藏 / 最小化 ⇒ 暂停捕获）。</summary>
        public void NotifyLiveGlassVisibility(bool visible) =>
            _liveGlass?.OnVisibilityChanged(visible);

        /// <summary>窗口句柄重建（句柄代次 +1）。</summary>
        public void NotifyLiveGlassHandleChanged() => _liveGlass?.OnHandleChanged();

        /// <summary>窗口尺寸变化（尺寸代次 +1）。</summary>
        public void NotifyLiveGlassSizeChanged() => _liveGlass?.OnSizeChanged();

        /// <summary>窗口位置变化（客户区几何代次 +1）。</summary>
        public void NotifyLiveGlassLocationChanged() => _liveGlass?.OnLocationChanged();

        /// <summary>主题 / DPI 变化（主题代次随外观运行时自增）。</summary>
        public void NotifyLiveGlassAppearanceChanged() => _liveGlass?.OnAppearanceChanged();

        /// <summary>停止实时玻璃（窗口退出 / 句柄销毁时调用）。</summary>
        public void StopLiveGlass() => _liveGlass?.SetEnabled(false);

        public void NotifyGpuHandleChanged()
        {
            if (_gpuRoute?.IsGpu != true)
            {
                return;
            }

            StopGpuPresentation();
            if (_gpuPresentation is not null) return;
            if (_form.IsHandleCreated && _gpuRoute?.IsGpu == true)
            {
                _gpuPresentation = new GpuPresentationHostController(
                    _form,
                    _gpuRoute.EffectiveMaterial,
                    _gpuRoute.CaptureRequested,
                    _cfg.HorizontalMode,
                    ThemeManager.Current.Rendering,
                    (float)_cfg.UIScale, theme: ThemeManager.Current);
                StartGpuPresentation();
            }
        }

        /// <summary>
        /// 确保 GPU 宿主存在并已启动（窗口可见后补建）。
        /// <para>
        /// 窗口句柄可能在 <c>MainForm.BindEvents()</c> 注册 <c>HandleCreated</c>
        /// 处理器**之前**就已经创建：主题应用里的 <c>GetCurrentDpiScale()</c> 会用
        /// <c>Control.CreateGraphics()</c> 强制创建句柄，而这个过程发生在
        /// <c>MainForm</c> 构造体内。等到真正显示窗口时句柄早已存在，
        /// <c>HandleCreated</c> 不再触发，<see cref="NotifyGpuHandleChanged"/>
        /// 于是永远不会被调用，GPU 宿主也就永远不会启动。
        /// </para>
        /// <para>
        /// 这里只在宿主**缺席**时创建，不触碰已经存在的会话，
        /// 因此不会产生多余的设备 / 捕获拆除重建。
        /// </para>
        /// </summary>
        public void EnsureGpuPresentationStarted()
        {
            if (_gpuRoute?.IsGpu != true) return;
            if (_gpuPresentation is not null) return;
            if (!_form.IsHandleCreated) return;
            _gpuPresentation = new GpuPresentationHostController(
                _form,
                _gpuRoute.EffectiveMaterial,
                _gpuRoute.CaptureRequested,
                _cfg.HorizontalMode,
                ThemeManager.Current.Rendering,
                (float)_cfg.UIScale, theme: ThemeManager.Current);
            StartGpuPresentation();
        }

        public void NotifyGpuVisibility(bool visible)
        {
            if (_gpuPresentation is null)
            {
                return;
            }

            if (visible)
            {
                _gpuPresentation.Resume();
                _gpuPresentation.NotifySizeChanged();
                UpdateGpuScene();
            }
            else
            {
                _gpuPresentation.Suspend();
            }
        }

        public void NotifyGpuSizeChanged()
        {
            if (_updatingGpuScene)
            {
                return;
            }

            _gpuPresentation?.NotifySizeChanged();
            UpdateGpuScene();
        }

        public void NotifyGpuDisplayChanged()
        {
            _gpuPresentation?.NotifyOutputChanged(force: true);
            NotifyGpuSizeChanged();
        }

        public void StopGpuPresentation()
        {
            _gpuPresentation?.Dispose();
            if (_gpuPresentation?.WindowCapturePolicyRestored == false)
            {
                _gpuFallbackCode = _gpuPresentation.LastErrorCode;
                return;
            }
            _gpuPresentation = null;
        }

        /// <summary>
        /// Allows one new native GPU attempt only after the user explicitly
        /// applies settings. An unclean previous process otherwise remains in
        /// the conservative GDI+ route.
        /// </summary>
        public bool AllowGpuStartupRetryAfterExplicitSettingsApply()
        {
            return GpuStartupGuard.Shared.AllowExplicitManualRetry(
                out _,
                out _);
        }

        public void Render(Graphics g)
        {
            if (IsGpuPresentationActive)
            {
                return;
            }

            var t = ThemeManager.Current;
            _layout ??= new UILayout(t);

            // ★ A16：绘制**开始前**完成帧的租约交接 ——
            //   优先接管新帧，成功后才释放被替换的旧租约；没有新帧时保留当前有效租约。
            //   因此同一绘制周期内的重复调用不会释放正在使用的帧。
            _liveGlass?.BeginPaint();

            // === 横屏模式 ===
            if (_cfg.HorizontalMode)
            {
                _hxLayout ??= new HorizontalLayout(t, _form.Width, LayoutMode.Horizontal);

                // [通用方案] 动态检测是否需要重新计算布局
                // 移至 Tick() 中低频检查，避免 Render 高频调用
                if (_layoutDirty)
                {
                    int h = _hxLayout.Build(_hxColsHorizontal);
                    // ★ D10：列集合为空（全部可见项关闭）时，Build 过去返回 0，
                    //   客户区高度被写成 0 ⇒ 面板整个从屏幕上消失。
                    //   这里再兜一层最小高度：面板退化成空面板，由空态提示说明原因。
                    h = Math.Max(h, _hxLayout.MinPanelHeight);
                    // 同样建议横屏模式也使用 ClientSize
                    // _form.Width = ...
                    // _form.Height = h;
                    _form.ClientSize = new Size(_hxLayout.PanelWidth, h);
                    _layoutDirty = false;
                }
                // 外观上下文：客户区尺寸会变（横屏会改 ClientSize），每帧同步一次
                UpdateAppearanceContext(t);
                HorizontalRenderer.Render(g, t, _hxColsHorizontal, _hxLayout.PanelWidth, _appearance);
                // ★ 降级提示在横屏下同样要出现（否则横屏用户看不到玻璃没生效）。
                DrawLiveGlassNotice(g, t);
                // ★ 配置未保存提示在横屏下同样要出现。
                DrawConfigWriteBlockedNotice(g, t);
                // ★ D10：没有可见项时说明原因（否则用户只看到一块空板）。
                DrawEmptyStateNotice(g, t, _hxColsHorizontal.Count == 0);
                return;
            }

            // === 竖屏模式 ===
            if (_layoutDirty)
            {
                int h = _layout.Build(_groups);
                // ★ D10 同理：竖屏在没有任何可见分组时只返回 Padding（≈10px），
                //   面板会退化成一条几像素的细缝。同样兜一个最小高度。
                h = Math.Max(h, MinEmptyPanelHeight(t));
                // [修复2补充] 设置高度时也使用 ClientSize，确保高度精准
                _form.ClientSize = new Size(_form.ClientSize.Width, h);
                _layoutDirty = false;
            }

            // === [关键修改] 获取布局中的看板信息 ===
            // var dashboard = _layout.Dashboard; // 已移除
            // ==========================================

            UpdateAppearanceContext(t);
            UIRenderer.Render(g, _groups, t, _appearance);

            // ★ A16 复核修正：降级必须**看得见**（合同 §7「降级不静默」）。
            //   之前玻璃没生效时，背景层静默降级成一块浅色板 —— 屏幕上与正常状态
            //   毫无区别，用户既不知道坏了，也无从判断该怎么处理。
            DrawLiveGlassNotice(g, t);

            // ★ D10：没有可见分组时说明原因（否则用户只看到一块空板）。
            DrawEmptyStateNotice(g, t, _groups.Count == 0);

            // ★ 配置未保存提示（托盘菜单里的改动在配置读不出来时也存不下去）。
            DrawConfigWriteBlockedNotice(g, t);
        }

        /// <summary>
        /// 在面板**左下角**画一行「配置未保存」提示。
        /// <para>★ 条件：<c>SettingsHelper.GlobalBlockSave</c> 为真 —— 配置读不出来时，
        ///   本次会话的任何自动落盘都被阻止（托盘菜单里的改动也存不下去）。
        ///   不告诉用户的话，他改了半天退出后全没了。</para>
        /// <para>★ 放在左下角，与右下角的「实时玻璃未生效」提示互不重叠。</para>
        /// </summary>
        private void DrawConfigWriteBlockedNotice(Graphics g, Theme t)
        {
            if (!SettingsHelper.GlobalBlockSave) return;

            int w = _form.ClientRectangle.Width;
            int h = _form.ClientRectangle.Height;
            if (w <= 0 || h <= 18) return;

            string text = LanguageManager.T("Menu.ConfigWriteBlockedShort");
            if (string.IsNullOrEmpty(text)) return;

            SolidBrush brush = EnsureNoticeBrush(t);
            using var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

            g.DrawString(
                text,
                LiveGlassNoticeFont,
                brush,
                new RectangleF(4, h - 16, w - 8, 14),
                fmt);
        }

        /// <summary>
        /// 在面板底部画一行"实时玻璃未生效"的原因说明。
        /// <para>★ 只在三个条件同时成立时出现：开关已打开、当前主题请求 glass、服务确实在降级。
        ///   其余情况一律不画 ⇒ 旧主题与正常状态的画面**零变化**。</para>
        /// </summary>
        private void DrawLiveGlassNotice(Graphics g, Theme t)
        {
            if (!_cfg.LiveGlassEnabled) return;

            ThemeAppearanceOptions? options = _appearance?.Options;
            if (options is null || !options.GlassRequested) return;

            string reason = _liveGlass?.LastDegradeReason ?? string.Empty;
            if (string.IsNullOrEmpty(reason)) return;

            string text = "实时玻璃未生效：" + reason;
            const int maxChars = 46;
            if (text.Length > maxChars) text = text.Substring(0, maxChars) + "…";

            int w = _form.ClientRectangle.Width;
            int h = _form.ClientRectangle.Height;
            if (w <= 0 || h <= 18) return;

            // ★ 画刷按颜色缓存：换主题后颜色会变，必须重建，否则提示文字停留在旧主题的颜色上。
            //   次级文字色可为空（新旧主题都可能没有），此时退回主文字色 —— 它一定有值。
            SolidBrush brush = EnsureNoticeBrush(t);

            using var fmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            g.DrawString(
                text,
                LiveGlassNoticeFont,
                brush,
                new RectangleF(0, h - 16, w - 4, 14),
                fmt);
        }

        /// <summary>
        /// 空态提示：面板里一个可见项都没有时，居中画一行说明。
        /// <para>★ 只在**确实没有可见项/分组**时出现，正常状态画面零变化。</para>
        /// <para>★ 不画的话用户只看到一块空板，无法区分"没勾选任何指标"与"程序坏了"。
        ///   D10 的另一半修复：尺寸写入点只保证面板不消失，这一行负责说明原因。</para>
        /// </summary>
        private void DrawEmptyStateNotice(Graphics g, Theme t, bool isEmpty)
        {
            if (!isEmpty) return;

            int w = _form.ClientRectangle.Width;
            int h = _form.ClientRectangle.Height;
            if (w <= 0 || h <= 12) return;

            string text = LanguageManager.T("Menu.NoVisibleItems");
            if (string.IsNullOrEmpty(text)) return;

            SolidBrush brush = EnsureNoticeBrush(t);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            g.DrawString(text, t.FontItem, brush, new RectangleF(0, 0, w, h), fmt);
        }

        /// <summary>
        /// 竖屏空态下的最小客户区高度（物理像素）。
        /// <para>★ 竖屏布局在没有任何可见分组时只返回 Padding（≈10px），
        ///   面板会退化成一条几像素的细缝；这里给一个"一行指标 + 上下留白"的最小高度。</para>
        /// </summary>
        private static int MinEmptyPanelHeight(Theme t) =>
            Math.Max(t.Layout.RowHeight + t.Layout.Padding * 2, 48);

        /// <summary>
        /// 取（并按需重建）提示文字用的画刷。
        /// <para>★ 换主题后颜色会变，必须重建，否则提示文字停留在旧主题的颜色上。
        ///   次级文字色可为空（新旧主题都可能没有），此时退回主文字色 —— 它一定有值。</para>
        /// </summary>
        private SolidBrush EnsureNoticeBrush(Theme t)
        {
            Color noticeColor = ThemeManager.ParseColor(t.Color.TextSecondary ?? t.Color.TextPrimary);
            if (_noticeBrush == null || _noticeBrushColor != noticeColor)
            {
                _noticeBrush?.Dispose();
                _noticeBrush = new SolidBrush(noticeColor);
                _noticeBrushColor = noticeColor;
            }
            return _noticeBrush;
        }

        /// <summary>降级提示用的字体（静态缓存：绝不能每帧 new Font —— 会漏 GDI 句柄）。</summary>
        private static readonly Font LiveGlassNoticeFont = new Font("Microsoft YaHei UI", 7.5f);

        /// <summary>降级提示用的画刷（缓存，避免每帧分配）。</summary>
        private SolidBrush? _noticeBrush;

        /// <summary>画刷当前对应的颜色（换主题时用来判断要不要重建画刷）。</summary>
        private Color _noticeBrushColor;

        /// <summary>
        /// 同步外观运行时上下文。
        /// <para>
        /// 客户区尺寸可能每帧变化（横竖屏切换、面板宽度变化），因此每帧同步一次；
        /// 但 DPI 与用户缩放沿用 <see cref="ApplyTheme"/> 里记录的**同一对值** ——
        /// 必须与 <c>t.Scale(dpiScale, userScale)</c> 一致，否则装饰层的物理尺寸
        /// 与布局尺寸比例会错。
        /// </para>
        /// </summary>
        private void UpdateAppearanceContext(Theme t)
        {
            if (_appearance == null) return;
            _appearance.Update(t, _appearanceDpiScale, _appearanceUserScale, _form.ClientRectangle);
        }

        /// <summary>
        /// 用**当前客户区尺寸**同步一次外观上下文。
        /// <para>
        /// 客户区尺寸会在换肤（竖屏改 ClientSize）、横竖切换、窗口缩放时变化，
        /// 而外观快照里带着 ClientBounds（装饰层与窗口轮廓都按它定位）。
        /// 尺寸没变时复用上一次的快照，不产生新分配。
        /// </para>
        /// </summary>
        /// <returns>当前外观快照；外观运行时尚未创建时返回 null。</returns>
        public ThemeAppearanceOptions? SyncAppearanceContext()
        {
            if (_appearance == null) return null;
            UpdateAppearanceContext(ThemeManager.Current);
            return _appearance.Options;
        }

        private bool _busy = false;
        private bool _disposed = false;

        /// <summary>
        /// 界面刷新间隔（毫秒）。
        ///
        /// ★ 这个值只决定"界面刷新多快"，与"硬件采集多快"是两件事：
        ///   硬件采集由 <see cref="HardwareMonitor"/> 的采样协调器限频（最高 1 次/秒）。
        ///   界面的取值、平滑、绘制仍然按这个频率跑，不能被改成每秒一次。
        /// </summary>
        public static int ComputeUiRefreshIntervalMs(int refreshMs) => Math.Max(80, refreshMs);

        private async void Tick()
        {
            if (_dragging || _busy) return;
            _busy = true;

            try
            {
                if (_gpuRoute?.IsGpu == true &&
                    _gpuPresentation?.NeedsRestart == true &&
                    _form.IsHandleCreated)
                {
                    StartGpuPresentation();
                }

                // ★ 只请求"到期才采"的一次硬件采集：
                //   未到期时这里立即返回，不会创建任何硬件任务，
                //   但下面的取值 / 平滑 / 绘制仍然按界面频率执行。
                await _mon.UpdateIfDueAsync();

                // ★ 关闭竞态：异步回调回来时窗体/控制器可能已经释放，
                //   此时不能再触碰界面对象，也不能触发重建。
                if (_disposed || _form.IsDisposed) return;

                // ★★★ [多显卡] 实际显卡数少于配置数时 (例如拔掉了第二张卡),
                // 夹紧配置并重建监控项, 否则会留下一组永远没有数据的空白指标。 ★★★
                int actualGpuCount = _mon.GetMonitoredGpuCount();
                if (actualGpuCount > 0 && _cfg.MonitoredGpuCount > actualGpuCount)
                {
                    GpuItemSync.Ensure(_cfg, availableGpuCount: actualGpuCount);
                    RebuildLayout();
                }

                // ★★★ [按卡拆组] 就地刷新分组标题 ★★★
                //   面板的分组标题在 BuildMetrics() 里算好并缓存进 GroupLayoutInfo.Label,
                //   而 BuildMetrics() 在 UIController 构造时就跑完了 —— 那时硬件还没初始化完,
                //   GpuRegistry 里拿不到显卡简称, 标题只能是 "GPU.0" / "GPU.1"。
                //   硬件是异步初始化的, 如果不在这里刷新, 冷启动就永远看不到 "3080 Ti" 这种标题。
                //   标题只参与绘制 (画在 gr.Bounds 之上), 不参与布局计算 -> 只改 Label 即可。
                RefreshPerCardGroupTitles();

                // ★★★ [按卡拆组] 一次性清掉显卡项上被固化的旧式自动名 ★★★
                //   旧行为下自动标签是 "3080 Ti 温度", 会被旧版设置页固化成 UserLabel,
                //   而 UserLabel 优先级最高 -> 会把组标题的显卡型号重新顶回行标签上。
                MigrateLegacyGpuItemLabels();

                // ★★★ [硬件自检] 硬件层失败时提示一次用户 ★★★
                //   之前 LHM 打不开硬件树会被 catch 静默吞掉，用户只看到「指标全是 0」，
                //   既没有提示也没有日志。现在由 HardwareMonitor 暴露 LastHardwareError，
                //   这里统一转成托盘气泡提示（详情已写进 LiteMonitor++_HardwareError.log）。
                NotifyHardwareErrorOnce();

                // ① 更新竖屏 items
                foreach (var g in _groups)
                    foreach (var it in g.Items)
                    {
                        // [新增] Dashboard 实时更新
                        if (it.Key.StartsWith("DASH."))
                        {
                             string dashKey = it.Key.Substring(5);
                             string val = InfoService.Instance.GetValue(dashKey);
                             it.TextValue = val;
                        }
                        else
                        {
                            it.Value = _mon.Get(it.Key);
                            it.TickSmooth(_cfg.AnimationSpeed);
                        }
                    }

                // ② 更新横版 / 任务栏 (清理了冗余代码)
                void UpdateCol(Column col)
                {
                    void UpdateItem(MetricItem it) 
                    {
                        if (it == null) return;
                        
                        // [新增] Dashboard 实时更新 (横版/任务栏)
                        if (it.Key.StartsWith("DASH."))
                        {
                             string dashKey = it.Key.Substring(5);
                             string val = InfoService.Instance.GetValue(dashKey);
                             it.TextValue = val;
                        }
                        else 
                        {
                            it.Value = _mon.Get(it.Key);
                            it.TickSmooth(_cfg.AnimationSpeed);
                        }
                    }
                    UpdateItem(col.Top);
                    UpdateItem(col.Bottom);
                }
                
                foreach (var col in _hxColsHorizontal) UpdateCol(col);
                foreach (var col in _hxColsTaskbar) UpdateCol(col);

                HardwareHistoryLogger.RecordSnapshot(_cfg, key => _mon.Get(key));
 
                CheckTemperatureAlert();

                // 驱动 DashboardService 更新
                InfoService.Instance.Update();

                // [优化] 在数据更新后检查布局签名 (每秒一次)
                // 只有当充电状态等导致样本变化时，才标记 Dirty
                if (_cfg.HorizontalMode && _hxLayout != null)
                {
                    string currentLayoutSig = _hxLayout.GetLayoutSignature(_hxColsHorizontal);
                    if (currentLayoutSig != _lastLayoutSignature)
                    {
                        _lastLayoutSignature = currentLayoutSig;
                        _layoutDirty = true;
                    }
                }

                UpdateGpuScene();
                _form.Invalidate();   
            }
            finally
            {
                _busy = false;
            }
        }

        private void BuildMetrics()
        {
            _groups = new List<GroupLayoutInfo>();

            var activeItems = _cfg.MonitorItems
                .Where(x => x.VisibleInPanel)
                // ★★★ 终极排序逻辑 ★★★
                // 1. 先按 Group 分组，解决 "分裂问题" (物理聚类)
                .GroupBy(x => x.UIGroup)
                // 2. 组间排序：使用组内最小的 SortIndex 决定组的位置 (保留用户的整体排序意图)
                .OrderBy(g => g.Min(x => x.SortIndex))
                // 3. 组内排序并展平：组内按 SortIndex 排列
                .SelectMany(g => g.OrderBy(x => x.SortIndex))
                .ToList();

            if (activeItems.Count == 0) return;

            string currentGroupKey = "";
            List<MetricItem> currentGroupList = new List<MetricItem>();

            foreach (var cfgItem in activeItems)
            {
                string groupKey = cfgItem.UIGroup;

                if (groupKey != currentGroupKey && currentGroupList.Count > 0)
                {
                    var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                    string gName = GroupTitleResolver.Resolve(_cfg, currentGroupKey);
                    gr.Label = gName;
                    _groups.Add(gr);
                    currentGroupList = new List<MetricItem>();
                }

                currentGroupKey = groupKey;

                // ★★★ [多显卡] 带序号的 Key ("GPU.0.Load") 要回退到基础 Key ("GPU.Load") ★★★
                // 否则语言包查不到时 LanguageManager 会回退成字面量 "GPU.0.Load"。
                string label = LanguageManager.T(UIUtils.Intern("Items." + GpuKey.ToBaseKey(cfgItem.Key)));
                var item = new MetricItem 
                { 
                    Key = cfgItem.Key, 
                    BoundConfig = cfgItem 
                };
                
                // [修复] 初始化默认 Label (作为 Fallback)
                // 如果 BoundConfig.UserLabel 有值，Getter 会优先返回它
                item.Label = label;

                string defShort = LanguageManager.T(UIUtils.Intern("Short." + GpuKey.ToBaseKey(cfgItem.Key)));
                item.ShortLabel = defShort;

                
                // ★★★ [新增] Dashboard 数据源绑定 ★★★
                if (cfgItem.Key.StartsWith("DASH."))
                {
                     string dashKey = cfgItem.Key.Substring(5); // DASH.HOST -> HOST
                     
                     // 直接从服务获取值，不再依赖 WidgetItem 对象
                     string val = InfoService.Instance.GetValue(dashKey);
                     item.TextValue = val;
                     item.Value = null; // Dashboard 项没有数值，只有文本
                }
                else
                {
                    float? val = _mon.Get(item.Key);
                    item.Value = val;
                    if (val.HasValue) item.DisplayValue = val.Value;
                }

                currentGroupList.Add(item);
            }

            if (currentGroupList.Count > 0)
            {
                var gr = new GroupLayoutInfo(currentGroupKey, currentGroupList);
                string gName = GroupTitleResolver.Resolve(_cfg, currentGroupKey);
                gr.Label = gName;
                _groups.Add(gr);
            }
        }

        /// <summary>
        /// 就地刷新「按卡分组」的标题。
        ///
        /// 背景: 面板的分组标题在 BuildMetrics() 里算好并缓存到 GroupLayoutInfo.Label,
        ///   而 BuildMetrics() 在 UIController 构造时就跑完了 —— 那时硬件还没初始化完,
        ///   GpuRegistry 里拿不到显卡简称, 标题只能是 "GPU.0" / "GPU.1"。
        ///   硬件是异步初始化的, 如果不刷新, 冷启动就永远看不到 "3080 Ti" 这种标题。
        ///
        /// 只处理按卡分组: 其余分组的标题是静态的 (语言包 / 用户别名), 不会随时间变化。
        /// </summary>
        private void RefreshPerCardGroupTitles()
        {
            bool changed = false;
            foreach (var gr in _groups)
            {
                if (!GpuGroupPolicy.TryGetCardIndex(gr.GroupName, out _)) continue;
                string title = GroupTitleResolver.Resolve(_cfg, gr.GroupName);
                if (!string.Equals(gr.Label, title, StringComparison.Ordinal))
                {
                    gr.Label = title;
                    changed = true;
                }
            }
            if (changed) _form.Invalidate();
        }

        /// <summary>
        /// 硬件层初始化/重载失败时，用托盘气泡提示用户一次。
        /// [Fix] 之前这类故障被 catch 吞掉，Release 构建下连日志都没有，
        /// 用户只能看到「所有硬件指标都是 0」而完全不知道原因。
        /// 失败详情已经写进运行目录下的 LiteMonitor++_HardwareError.log。
        /// </summary>
        private void NotifyHardwareErrorOnce()
        {
            string? err = HardwareMonitor.LastHardwareError;

            // 硬件层已恢复正常（HardwareMonitor 在重建映射表成功后会清空 LastHardwareError）
            // -> 复位通知标记, 以便之后再次失败时能重新提示一次
            if (string.IsNullOrEmpty(err))
            {
                _hardwareErrorNotified = false;
                return;
            }

            if (_hardwareErrorNotified) return;

            _hardwareErrorNotified = true;
            try
            {
                string firstLine = err.Split('\n')[0].Trim();
                MainForm.ShowNotification(
                    "LiteMonitor++ 硬件监控初始化失败",
                    "所有硬件传感器指标将显示为 0。\r\n\r\n原因: " + firstLine +
                    "\r\n\r\n完整信息见运行目录下的 LiteMonitor++_HardwareError.log",
                    ToolTipIcon.Error);
            }
            catch { }
        }

        /// <summary>
        /// 一次性迁移: 清掉显卡项上「被固化的旧式自动名」。
        ///
        /// 背景: 旧行为下显卡项的自动标签是 "&lt;显卡简称&gt; &lt;指标名&gt;" (如 "3080 Ti 温度"),
        ///   而旧版设置页在保存时把这个自动标签固化进了 UserLabel。
        ///   UserLabel 的优先级高于一切, 所以按卡拆组之后, 组标题虽然已经是 "3080 Ti",
        ///   行标签却还是 "3080 Ti 温度" —— 正是本功能要消灭的重复。
        ///
        /// 只清掉**与旧式自动名完全一致**的 UserLabel, 用户自己起的名字一律保留。
        /// 需要等 GpuRegistry 就绪 (拿到显卡简称) 才能构造旧式自动名, 所以放在这里做。
        /// </summary>
        private void MigrateLegacyGpuItemLabels()
        {
            if (_gpuLegacyLabelMigrated) return;

            // 硬件还没初始化完 -> 拿不到显卡简称, 下个 Tick 再试
            bool anyShortName = false;
            for (int i = 0; i < GpuKey.MaxGpuCount; i++)
            {
                if (!string.IsNullOrEmpty(GpuRegistry.GetShortName(i))) { anyShortName = true; break; }
            }
            if (!anyShortName) return;

            _gpuLegacyLabelMigrated = true;

            bool changed = false;
            foreach (var item in _cfg.MonitorItems)
            {
                if (item == null) continue;
                if (string.IsNullOrEmpty(item.UserLabel)) continue;

                string legacyAuto = MetricLabelResolver.BuildLegacyAutoLabel(item);
                if (string.IsNullOrEmpty(legacyAuto)) continue;

                if (string.Equals(item.UserLabel, legacyAuto, StringComparison.Ordinal))
                {
                    item.UserLabel = "";
                    changed = true;
                }
            }

            if (changed)
            {
                // 落盘: 让迁移结果持久化, 下次启动不用再迁
                _cfg.Save();
            }
        }

        private void ApplyGpuRenderingRoute(Theme theme)
        {
            RenderingRouteDecision decision = RenderingRoutePolicy.Decide(new(
                theme.Rendering,
                GpuEffectsEnabled: _cfg.GpuThemeEffectsEnabled,
                LiveBackgroundEnabled: _cfg.GpuLiveBackgroundEnabled,
                WdaCapturePolicyAccepted: _cfg.GpuWdaCapturePolicyAccepted,
                SafeMode: Program.SafeMode));

            // ApplyTheme may synchronously paint the old GDI route before
            // reaching this decision. Do not let that desktop-DC layout leak
            // into the GPU route (or retain its window-DC layout on fallback).
            if (decision.IsGpu || _gpuRoute?.IsGpu == true)
                _hxLayout = null;

            if (!decision.IsGpu)
            {
                StopGpuPresentation();
                _gpuRoute = decision;
                if (_gpuPresentation is null) _gpuFallbackCode = decision.FallbackCode;
                return;
            }

            bool canReconfigure =
                _gpuPresentation is not null &&
                (_gpuPresentation.IsActive || _gpuPresentation.IsSuspended);
            if (canReconfigure &&
                _gpuPresentation!.TryReconfigure(
                    decision.EffectiveMaterial,
                    decision.CaptureRequested,
                    _cfg.HorizontalMode,
                    theme.Rendering,
                    (float)_cfg.UIScale,
                    out _,
                    out _, theme))
            {
                _gpuRoute = decision;
                _gpuFallbackCode =
                    decision.CaptureRequested &&
                    !_gpuPresentation.IsLiveCaptureStarted
                        ? _gpuPresentation.LastErrorCode
                        : decision.FallbackCode;
                _gpuPresentation.ResetPerformanceWindows();
                UpdateGpuScene();
                return;
            }

            // A suspended host can be reconfigured without replacing its device
            // or swap chain. Any other state, including a failed in-place
            // update, is rebuilt through the normal guarded startup path.
            StopGpuPresentation();
            if (_gpuPresentation is not null) return;
            _gpuRoute = decision;
            _gpuFallbackCode = decision.FallbackCode;
            _gpuPresentation = new GpuPresentationHostController(
                _form,
                decision.EffectiveMaterial,
                decision.CaptureRequested,
                _cfg.HorizontalMode,
                theme.Rendering,
                (float)_cfg.UIScale, theme: ThemeManager.Current);
            if (_form.IsHandleCreated)
            {
                StartGpuPresentation();
            }
        }

        private void StartGpuPresentation()
        {
            if (_gpuPresentation is null || _gpuRoute?.IsGpu != true)
            {
                return;
            }

            WindowCapturePolicy policy = _gpuRoute.CaptureRequested
                ? WindowCapturePolicy.WdaExcludeFromCapture
                : WindowCapturePolicy.None;
            if (_gpuPresentation.Start(
                    policy,
                    out string? errorCode,
                    out string? errorMessage))
            {
                _gpuFallbackCode = _gpuPresentation.RequestsLiveBackground &&
                    !_gpuPresentation.IsLiveCaptureStarted
                    ? _gpuPresentation.LastErrorCode
                    : _gpuRoute.FallbackCode;
                UpdateGpuScene();
                if (!_form.Visible || _form.WindowState == FormWindowState.Minimized)
                    _gpuPresentation.Suspend();
                return;
            }

            _gpuFallbackCode = errorCode;
            StopGpuPresentation();
            _form.Invalidate();
        }

        private void UpdateGpuScene()
        {
            if (_gpuPresentation?.IsActive != true)
            {
                return;
            }

            _updatingGpuScene = true;
            try
            {
                var notices = new List<SceneNotice>(2);
                if (SettingsHelper.GlobalBlockSave)
                {
                    notices.Add(new SceneNotice(
                        "config-write-blocked",
                        LanguageManager.T("Menu.ConfigWriteBlockedShort"),
                        MetricStatus.Warning));
                }

                if (_groups.Count == 0 && !_cfg.HorizontalMode)
                {
                    notices.Add(new SceneNotice(
                        "empty-state",
                        LanguageManager.T("Menu.NoVisibleItems"),
                        MetricStatus.Missing));
                }

                SceneSnapshot scene;
                if (_cfg.HorizontalMode)
                {
                    _hxLayout ??= new HorizontalLayout(
                        ThemeManager.Current,
                        _form.Width,
                        LayoutMode.Horizontal, _cfg, measurementControl: _form);
                    int height = _hxLayout.Build(_hxColsHorizontal);
                    Size target = new(
                        _hxLayout.PanelWidth,
                        Math.Max(height, _hxLayout.MinPanelHeight));
                    if (_form.ClientSize != target)
                    {
                        _form.ClientSize = target;
                    }

                    scene = UiSceneSnapshotAdapter.BuildHorizontal(
                        _hxColsHorizontal,
                        _gpuPresentation.Generations,
                        _form.ClientRectangle,
                        _form.DeviceDpi,
                        notices);
                }
                else
                {
                    if (_layoutDirty)
                    {
                        int height = _layout.Build(_groups);
                        height = Math.Max(height, MinEmptyPanelHeight(ThemeManager.Current));
                        Size target = new(_form.ClientSize.Width, height);
                        if (_form.ClientSize != target)
                        {
                            _form.ClientSize = target;
                        }
                        _layoutDirty = false;
                    }

                    scene = UiSceneSnapshotAdapter.Build(
                        _groups,
                        _gpuPresentation.Generations,
                        _form.ClientRectangle,
                        _form.DeviceDpi,
                        isHorizontal: false,
                        notices);
                }

                // ClientSize changes above synchronously raise Resize. Its
                // reentrant notification is suppressed, so synchronize the
                // final size here before publishing the scene generation.
                _gpuPresentation.NotifySizeChanged();
                if (!_gpuPresentation.IsActive) return;
                scene = scene with { Generations = _gpuPresentation.Generations };
                _gpuPresentation.UpdateScene(scene);
            }
            finally
            {
                _updatingGpuScene = false;
            }
        }

        private void BuildHorizontalColumns()
        {
            _hxColsHorizontal = BuildColumnsCore(forTaskbar: false);
            _hxColsTaskbar = BuildColumnsCore(forTaskbar: true);
        }

        private List<Column> BuildColumnsCore(bool forTaskbar)
        {
            var cols = new List<Column>();

            // 1. 筛选
            var query = _cfg.MonitorItems
                .Where(x => forTaskbar ? x.VisibleInTaskbar : x.VisibleInPanel);

            // 2. 排序 (优化：先按组聚类，防止新插件跑到末尾)
            bool useTaskbarSort = forTaskbar || _cfg.HorizontalFollowsTaskbar;
            List<MonitorItemConfig> items;

            if (useTaskbarSort)
            {
                // [Taskbar Mode] Allow cross-group sorting (Flat Sort)
                // 用户要求：任务栏排序不受分组约束，可以跨组混排
                items = query
                    .OrderBy(item => item.TaskbarSortIndex)
                    .ToList();
            }
            else
            {
                // [Panel Mode] Enforce grouping (Grouped Sort)
                // 面板模式默认保持分组聚合的视觉习惯
                items = query
                    .GroupBy(x => x.UIGroup)
                    .OrderBy(g => g.Min(item => item.SortIndex))
                    .SelectMany(g => g.OrderBy(item => item.SortIndex))
                    .ToList();
            }

            bool singleLine = (forTaskbar && _cfg.TaskbarSingleLine) || 
                              (!forTaskbar && _cfg.HorizontalMode && _cfg.HorizontalSingleLine);
            int step = singleLine ? 1 : 2;

            for (int i = 0; i < items.Count; i += step)
            {
                var col = new Column();
                col.Top = CreateMetric(items[i]);

                if (!singleLine && i + 1 < items.Count)
                {
                    col.Bottom = CreateMetric(items[i + 1]);
                }
                cols.Add(col);
            }

            return cols;
        }

        private MetricItem CreateMetric(MonitorItemConfig cfg)
        {
            var item = new MetricItem 
            { 
                Key = cfg.Key,
                BoundConfig = cfg // [核心修复] 绑定 Config
            };
            
            // Standard initialization
            // 优先使用用户自定义的 Label/ShortLabel
            // 如果用户未定义 (null/empty)，则使用语言包中的默认值
            // 如果用户定义了 " " (空格)，则保留空格 (意味着隐藏标签)
            
            string defLabel = LanguageManager.T(UIUtils.Intern("Items." + GpuKey.ToBaseKey(cfg.Key)));
            item.Label = defLabel; // 设为默认值，BoundConfig 会负责处理覆盖

            string defShort = LanguageManager.T(UIUtils.Intern("Short." + GpuKey.ToBaseKey(cfg.Key)));
            item.ShortLabel = defShort;


            // [新增] Dashboard 数据源绑定
            if (cfg.Key.StartsWith("DASH."))
            {
                 string dashKey = cfg.Key.Substring(5);
                 // 立即尝试获取一次，如果是 Loading 且 NetworkManager 有缓存，则直接更新
                 // 避免异步等待导致的 "?"
                 if (dashKey == "IP")
                 {
                      string cachedIP = HardwareMonitor.Instance?.GetNetworkIP();
                      if (!string.IsNullOrEmpty(cachedIP) && cachedIP != "?")
                      {
                           InfoService.Instance.InjectIP(cachedIP);
                      }
                 }
                 
                 string val = InfoService.Instance.GetValue(dashKey);   
                 item.TextValue = val;
                 item.Value = null;
            }
            else
            {
                InitMetricValue(item);
            }
            
            return item;
        }

        private void InitMetricValue(MetricItem? item)
        {
            if (item == null) return;
            float? val = _mon.Get(item.Key);
            item.Value = val;
            if (val.HasValue) item.DisplayValue = val.Value;
        }
        
        private void CheckTemperatureAlert()
        {
            if (!_cfg.AlertTempEnabled) 
            {
                _overheatStartTimes.Clear();
                return;
            }

            if ((DateTime.Now - _cfg.LastAlertTime).TotalMinutes < 3) return;

            int globalThreshold = _cfg.AlertTempThreshold; 
            int diskThreshold = Math.Min(globalThreshold - 20, 60); 

            List<string> alertLines = new List<string>();
            string alertTitle = LanguageManager.T("Menu.AlertTemp"); 

            void Check(string key, float? val, int threshold, string label, string msgSuffix = "")
            {
                if (val.HasValue && val.Value >= threshold)
                {
                    if (!_overheatStartTimes.ContainsKey(key))
                        _overheatStartTimes[key] = DateTime.Now;
                    
                    if ((DateTime.Now - _overheatStartTimes[key]).TotalSeconds >= 5)
                    {
                        alertLines.Add($"{label} {alertTitle}: {val:F0}°C{msgSuffix}");
                    }
                }
                else
                {
                    if (_overheatStartTimes.ContainsKey(key))
                        _overheatStartTimes.Remove(key);
                }
            }

            Check("CPU.Temp", _mon.Get("CPU.Temp"), globalThreshold, LanguageManager.T("Short.CPU.Temp"));

            // ★★★ [多显卡] 逐张显卡检查超温 ★★★
            //   ⚠️ 不能只查 "GPU.Temp": 迁移后 MonitorItems 里不再有它,
            //   而 SensorMap 只为 0 号卡写了这个旧格式 Key —— 那样等于**只告警第 0 张卡**,
            //   多卡用户最关心的"哪张卡过热"会完全失效。
            for (int gpuIdx = 0; gpuIdx < _cfg.MonitoredGpuCount; gpuIdx++)
            {
                string gpuTempKey = GpuKey.Build(gpuIdx, "Temp");
                string gpuShort = GpuRegistry.GetShortName(gpuIdx);
                string gpuAlertLabel = string.IsNullOrEmpty(gpuShort)
                    ? LanguageManager.T("Short.GPU.Temp")
                    : $"{gpuShort} {LanguageManager.T("Short.GPU.Temp")}";
                Check(gpuTempKey, _mon.Get(gpuTempKey), globalThreshold, gpuAlertLabel);
            }

            Check("MOBO.Temp", _mon.Get("MOBO.Temp"), globalThreshold, LanguageManager.T("Short.MOBO.Temp"));
            Check("DISK.Temp", _mon.Get("DISK.Temp"), diskThreshold, LanguageManager.T("Short.DISK.Temp"), $" (>{diskThreshold}°C)");

            if (alertLines.Count > 0)
            {
                string thresholdText = (alertLines.Count == 1 && alertLines[0].StartsWith("DISK")) 
                    ? $"(>{diskThreshold}°C)" 
                    : $"(>{globalThreshold}°C)";

                alertTitle += $" {thresholdText}";
                string bodyText = string.Join("\n", alertLines);
                
                ((MainForm)_form).ShowNotification(alertTitle, bodyText, ToolTipIcon.Warning);
                _cfg.LastAlertTime = DateTime.Now;
            }
        }
        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _mon.Dispose();

            // 释放外观运行时持有的纹理/画刷/噪点位图。
            // 背景帧与预览源是借用引用，AppearanceRuntime 不会释放它们。
            _appearance?.Dispose();
            _appearance = null;

            // ★ A16：停掉后台捕获服务并释放租约（必须在外观运行时之后 ——
            //   代理还指向服务，顺序反了会漏掉最后一次亲和性恢复）。
            _liveGlass?.Dispose();
            _liveGlass = null;

            _gpuPresentation?.Dispose();
            _gpuPresentation = null;

            // 降级提示的画刷
            _noticeBrush?.Dispose();
            _noticeBrush = null;
        }
    }
}
