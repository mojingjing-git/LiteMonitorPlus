using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.InfoService;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.Helpers;
using LiteMonitor.src.WebServer;
using LiteMonitor.Rendering;
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

        // ================================================================
        // 【GPU T03 / T02 合同】切换协调器 + 清理责任 ledger
        // ================================================================
        // ★ 协调器 = "状态与判据"的唯一落点（期望/有效路由、阶段、身份、呈现历史、
        //   清理状态、诊断字段、HandleCreated 判别位、三重丢弃判据）。
        //   它**不持有**句柄代次实例，只只读消费注入进来的唯一真相源。
        private readonly RenderingTransitionCoordinator _transition;

        // ★ ledger = **未完成的 affinity 清理责任的独立持有者**（合同 §6.2 / INV-1~INV-6）。
        //   清理责任**永不**通过 `_gpuPresentation` 表达（那正是 GPU-001 的根因）。
        private readonly CapturePolicyCleanupLedger _captureCleanup;

        // ★ 重入保护：合同 §4 步 1 / 步 6 的禁止动作 —— "不得递归 ApplyTheme"。
        //   过渡过程中（步 6 需要重放 ApplyTheme）用它切断递归。
        private bool _inGpuTransition;

        // ★★【R02 / 合同 §4.4 / U11】GPU 故障 → legacy 转换的**统一调度器**。
        //   三条故障路径（启动失败 / 首帧失败 / 运行故障恢复耗尽）全部经由它入队，
        //   再由 UI 安全点（UI Tick）统一执行 —— 与"用户手动关闭 GPU"路径共用
        //   同一个 legacy 转换入口（RunGpuToLegacyTransition）。
        private readonly GpuFailureFallbackScheduler _gpuFailureFallback = new();

        // ================================================================
        // 【GPU T03 / 合同 §4.6 / GPU T05】legacy 回退样式的**派生**覆盖值
        // ================================================================
        // ★ GPU→legacy 过渡时，用 resolver 的输出派生一份 legacy 外观样式，
        //   驱动 legacy 渲染。
        // ★ **绝不**修改 theme.Color、**绝不**修改 ThemeManager.Current ——
        //   派生值是一份**独立副本**（见 BuildLegacyFallbackTheme）。
        // ★ **绝不**新增任何写盘路径（不改原主题 JSON、不保存/覆盖原文件）。
        private Theme? _legacyFallbackTheme;

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

            // ★【GPU T03 / 合同 §7.2 / 主审裁决 D-G】协调器**注入**句柄代次的唯一真相源
            //   （进程内唯一实例，由类型自身持有）—— owner B **不得**另行 new。
            _transition = new RenderingTransitionCoordinator(WindowHandleGeneration.Instance);

            // ★【合同 §6.2】ledger 由 **UI 生命周期所有者**（本控制器）持有：
            //   独立持有全部未完成的 affinity 清理责任，并按合同 §6.4 的退避表重试。
            //   恢复动作把**原始** affinity 精确写回旧 HWND（合同 §4.5 :198）。
            _captureCleanup = new CapturePolicyCleanupLedger(
                () => WindowHandleGeneration.Instance.Current,
                MainFormWinHelper.IsWindowAlive,
                RestoreCapturePolicyCore);

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

            // ★【合同 §4.6 / GPU T05】主题被重新加载 ⇒ 派生回退样式必须跟着重算
            //   （否则派生副本会持有旧主题的 Layout / Rendering）。
            RefreshLegacyFallbackTheme(t);

            // 4. 安全释放旧主题的字体
            //   ★【LM-071 / R07】必须**晚于** RefreshLegacyFallbackTheme(t)：
            //     派生主题与源主题**共享同一批 Font 实例**（见 LegacyFallbackStyleBridge.BuildDerivedTheme），
            //     先释放旧主题的字体 ⇒ 派生主题仍引用它们 ⇒ 悬垂引用。
            //   此时 Current 已经是新主题、派生覆盖也已重算或清空 ⇒ 释放旧的是安全的。
            if (oldTheme != null && oldTheme != t)
            {
                oldTheme.DisposeFonts();
            }

            _form.BackColor = ThemeManager.ParseColor(CurrentRenderTheme.Color.Background);

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

        /// <summary>
        /// 停止实时玻璃（**整停**：服务 + 配置态开关）—— 窗口**真退出**时调用。
        /// <para>★【LM-072 / R08】句柄销毁**不走**这里（那是临时停机，
        ///   见 <see cref="StopLiveGlassForHandleRecreate"/>）——
        ///   只有真关闭才允许把配置态开关清成 false。</para>
        /// </summary>
        public void StopLiveGlass() => _liveGlass?.SetEnabled(false);

        /// <summary>
        /// 【LM-072 / R08】句柄重建 ⇒ 实时玻璃**临时停机**。
        /// <para>★ 临时停机 ≠ 用户关闭：只停捕获服务，**保留**配置态的开关
        ///   （配置仍为"开"，重建完成后按配置恢复，见 <see cref="LiveGlassController.RestoreIfEnabled"/>）。</para>
        /// </summary>
        public void StopLiveGlassForHandleRecreate() => _liveGlass?.StopForHandleRecreate();

        /// <summary>
        /// 【GPU T03 / 合同 §8.2 / §4 步 5】窗口句柄创建事件。
        /// <para>★ 判别位（合同 §8.1）：<paramref name="controlled"/> 为 <c>true</c> ⇒ 本次
        /// <c>HandleCreated</c> 来自**受控重建**（可走 GPU 重建路径）；
        /// 为 <c>false</c> ⇒ 来自**构造期首次创建**或**非受控创建**
        /// ⇒ <b>禁止</b>拉起 GPU（计划 §4.2 :147，既有设计必须保持）。</para>
        /// <para>★ 过渡期 GPU 启动次数必须为零（计划 §4.3 :172）：协调器的阶段守卫
        /// （合同 §8.4）保证重建过程中不会自动拉起 GPU。</para>
        /// </summary>
        public void NotifyGpuHandleChanged(bool controlled, long handleGeneration)
        {
            // 步 5：原生生命周期 + 代次推进（状态侧）。
            _transition.OnHandleCreated(controlled, handleGeneration);

            if (!controlled)
            {
                // 构造期首次创建 / 非受控创建 ⇒ **禁止**拉起 GPU（计划 §4.2 :147）。
                return;
            }

            if (!_transition.CanStartGpuPresentation)
            {
                // 合同 §8.4：只有 Phase == Stable 且 ExpectedRoute.IsGpu 才允许拉起 GPU。
                return;
            }

            if (_gpuRoute?.IsGpu != true)
            {
                return;
            }

            StopGpuPresentation();
            if (_gpuPresentation is not null) return;
            if (_form.IsHandleCreated && _gpuRoute?.IsGpu == true)
            {
                // ★ 宿主会话代次 +1（合同 §2 维度 5 的 HostGeneration）。
                _transition.NextHostGeneration();
                _gpuPresentation = CreateGpuPresentationHost(
                    _gpuRoute.EffectiveMaterial,
                    _gpuRoute.CaptureRequested,
                    ThemeManager.Current.Rendering,
                    ThemeManager.Current);
                StartGpuPresentation();
            }
        }

        /// <summary>
        /// 【GPU T03 / 合同 §8.1 消费者】尝试消费一次受控重建判别位。
        /// <para>由 <c>MainForm</c> 的 <c>HandleCreated</c> 处理器调用。</para>
        /// </summary>
        internal bool TryConsumeControlledRecreate(out long requestId) =>
            _transition.TryConsumeControlledRecreate(out requestId);

        /// <summary>
        /// 【GPU T03 / 合同 §8.1 置位者】请求一次受控 HWND 重建。
        /// <para>必须由 <c>MainForm</c> 的内部受控重建薄入口在调用 <c>RecreateHandle()</c>
        /// **之前**调用。</para>
        /// </summary>
        internal long RequestControlledRecreate(string reason) =>
            _transition.RequestControlledRecreate(reason);

        /// <summary>【GPU T03 / 合同 §8.1 规则 ②】取消一次受控重建请求并归零。</summary>
        internal bool CancelControlledRecreate(long requestId) =>
            _transition.CancelControlledRecreate(requestId);

        /// <summary>
        /// 【GPU T03 / 合同 §4 全局约束 / U10】窗体进入关闭过程 ⇒ 关闭拥有最高优先级。
        /// </summary>
        internal void NotifyFormClosing() => _transition.NotifyFormClosing();

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
            // ★ 宿主会话代次 +1（合同 §2 维度 5 的 HostGeneration）。
            _transition.NextHostGeneration();
            _gpuPresentation = CreateGpuPresentationHost(
                _gpuRoute.EffectiveMaterial,
                _gpuRoute.CaptureRequested,
                ThemeManager.Current.Rendering,
                ThemeManager.Current);
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

        /// <summary>
        /// 【GPU T03 / 合同 §6.2 / INV-1、INV-6】创建 GPU 呈现宿主并**立刻**接上清理责任移交出口。
        /// <para>★ 订阅必须**早于**任何 <c>Dispose()</c>（<see cref="StopGpuPresentation"/> 会 Dispose 宿主）：
        /// 宿主在停止时若 affinity 恢复失败，会把一条 <see cref="WindowCapturePolicyCleanup"/>
        /// **移交**给本控制器 ⇒ 由 ledger 独立持有（合同 §6.2 的所有权形状）。</para>
        /// <para>★ INV-6：新代次取得 WDA 所有权前，宿主会回调
        /// <see cref="CapturePolicyCleanupLedger.HasPendingForGeneration"/> 查询"同一代次是否仍有未完成清理"
        /// ⇒ 有 ⇒ 拒绝叠加新的 affinity 所有者。</para>
        /// <para>★ 三个创建点（<c>NotifyGpuHandleChanged</c> / <c>EnsureGpuPresentationStarted</c> /
        /// <c>ApplyGpuRenderingRoute</c>）统一走本工厂 ⇒ 订阅**不可能**被漏掉。</para>
        /// </summary>
        private GpuPresentationHostController CreateGpuPresentationHost(
            MaterialKind material,
            bool captureRequested,
            ThemeRenderingConfig rendering,
            Theme theme)
        {
            GpuPresentationHostController host = new(
                _form,
                material,
                captureRequested,
                _cfg.HorizontalMode,
                rendering,
                (float)_cfg.UIScale, theme: theme);

            // ★ 清理责任移交出口（合同 §6.2）—— 订阅早于任何 Dispose。
            host.CapturePolicyCleanupRequired += OnGpuCapturePolicyCleanupRequired;
            // ★★【R02】故障通知出口 —— 订阅早于任何 Dispose。
            //   ★ 回调**只允许**记录 / 入队：它可能在 GPU 帧绘制栈内触发。
            host.PresentationFailed += OnGpuPresentationFailed;
            // ★ INV-6 探针：同一代次仍有未完成清理 ⇒ 拒绝叠加新的 affinity 所有者。
            host.CapturePolicyCleanupPendingProbe = _captureCleanup.HasPendingForGeneration;
            // ★【LM-074】闸门**原因**探针：可区分"还在按合同 §6.4 重试"
            //   与"已经耗尽但**从未恢复**"（后者会让 HWND 残留 WDA 排除）。
            host.CapturePolicyCleanupBlockingProbe = _captureCleanup.GetBlockingReasonForGeneration;
            return host;
        }

        /// <summary>
        /// 【GPU T03 / 合同 §6.2】宿主的清理责任移交回调：把记录收进 **ledger**
        /// （未完成 affinity 清理责任的独立持有者）。
        /// <para>★ 清理责任**永不**通过宿主引用表达（合同 §6.2 INV-1）。</para>
        /// </summary>
        private void OnGpuCapturePolicyCleanupRequired(WindowCapturePolicyCleanup cleanup)
        {
            bool registered = _captureCleanup.Add(cleanup);
            _transition.RecordCleanupState(
                _captureCleanup.State,
                _captureCleanup.CleanupErrorCode);

            // ★【LM-074】移交被拒（INV-4：同代次已有非终态责任）⇒ **不得静默丢弃**。
            //   被拒的那条往往正是"误采残留 0x11 为原值"的记录 ⇒ 必须留下可观测诊断。
            if (!registered)
            {
                _transition.RecordCleanupWarning(_captureCleanup.RegistrationRejectedCode);
            }
        }

        // ================================================================
        // 【R02 / 合同 §4.4 / U11】GPU 故障 → legacy 转换的统一调度
        // ================================================================

        /// <summary>
        /// 【R02】宿主的故障通知回调。
        /// <para>★ <b>本方法不做任何窗口操作</b> —— 它可能在 <b>GPU 帧绘制栈内</b>被调用
        /// （计划 §4.3 :169 禁止在那里递归执行 legacy 转换）
        /// ⇒ 这里只把故障交给统一调度入口入队，真正的转换由 UI 安全点执行。</para>
        /// </summary>
        private void OnGpuPresentationFailed(GpuFailureReport report)
        {
            ScheduleGpuFailureFallback(report);
        }

        /// <summary>
        /// 【R02 / 合同 §4.4 / U11】GPU 故障 → legacy 转换的<b>统一调度入口</b>。
        /// <para>★ "启动失败""首帧失败""运行故障恢复耗尽"三条路径全部经由本方法入队，
        /// 再由 <see cref="PumpGpuFailureFallback"/>（UI 安全点）执行 ——
        /// 与"用户手动关闭 GPU"路径<b>共用</b>同一个 legacy 转换入口
        /// （<see cref="RunGpuToLegacyTransition"/>）。</para>
        /// <para>★ <b>保留失败原因</b>：呈现根因写入协调器的 <c>FailureCode</c>，
        /// 不得退化成"普通 fallback code"（合同 §5.3 规则 2 / U11）。</para>
        /// <para>★ <b>本方法不做任何窗口操作</b>（只记录 + 入队）。</para>
        /// </summary>
        internal void ScheduleGpuFailureFallback(GpuFailureReport report)
        {
            if (report is null) return;

            // 关闭拥有最高优先级（合同 §4 全局约束 / U10）：关闭过程中不得再启动一次切换。
            if (_disposed || _form.IsDisposed) return;
            if (_transition.IsClosing) return;

            // ★ 保留失败原因（先到先得 ⇒ 最早的根因不被后来的诊断顶替）。
            _transition.RecordPresentationRootCause(report.FailureCode);
            if (!string.IsNullOrEmpty(report.FailureCode))
            {
                _gpuFallbackCode = report.FailureCode;
            }

            // ★【合同 §4 步 4 / 计划 §4.1 :123-126】把**本代次**的 HWND 呈现历史写进协调器 ——
            //   "是否需要受控重建 HWND"的唯一依据（与手动关闭路径共用同一判据）。
            _transition.RecordBindingState(report.BindingState);

            // ★ 运行故障**不**立刻回退：宿主仍保留既有的一次自动恢复
            //   （计划 §4.4 :186 "最终失败才进入完整 legacy 切换"）。
            //   只有"恢复也失败"（恢复预算耗尽）才落到 legacy 转换。
            if (report.Stage == GpuFailureStage.Runtime) return;

            _gpuFailureFallback.Request(report);
        }

        /// <summary>
        /// 【R02 / 合同 §4 全局约束】UI 安全点：执行排队的 legacy 转换。
        /// <para>★ <b>只有 UI Tick 会调用本方法</b> ⇒ 转换一定发生在
        /// <c>OnPaint</c> / <c>WndProc</c> 销毁回调 / GPU 帧绘制栈<b>之外</b>
        /// （计划 §4.3 :169）。</para>
        /// <para>★ 返回 <c>true</c> = 本次执行了一次转换。</para>
        /// </summary>
        private bool PumpGpuFailureFallback()
        {
            if (!_gpuFailureFallback.HasPending) return false;

            // 关闭优先：关闭过程中不得重建 HWND、不得恢复 affinity（合同 §4 全局约束 / U10）。
            if (_disposed || _form.IsDisposed || _transition.IsClosing)
            {
                _gpuFailureFallback.Clear();
                return false;
            }

            // 已有一次转换在跑 ⇒ 留给下一次 Tick（重入保护）。
            if (_inGpuTransition) return false;

            // 禁止上下文（OnPaint / 销毁回调 / GPU 帧栈）与重入保护。
            if (!_gpuFailureFallback.IsExecutionAllowed) return false;

            if (!_gpuFailureFallback.TryDequeue(out GpuFailureReport report)) return false;

            _gpuFailureFallback.BeginExecution();
            try
            {
                RunGpuFailureFallbackTransition(report);
            }
            finally
            {
                _gpuFailureFallback.EndExecution();
            }

            return true;
        }

        /// <summary>
        /// 【R02】执行一次"GPU 故障 → legacy"转换。
        /// <para>★ 与"用户手动关闭 GPU"路径<b>共用</b> <see cref="RunGpuToLegacyTransition"/>
        /// （合同 §4 步 2–9：受控 HWND 重建 + 布局/DPI 恢复 + 窗口状态恢复 + 一次有效重绘 + 发布稳定）。</para>
        /// <para>★ 是否需要受控重建 HWND，由**本代次的呈现历史**决定
        /// （计划 §4.1 :123-126，见 <see cref="RequiresControlledRecreate"/>）。</para>
        /// </summary>
        private void RunGpuFailureFallbackTransition(GpuFailureReport report)
        {
            // ★ 合同 §4 步 1（故障版本）：进入保护 + 推进切换号 + **保留失败原因**。
            if (!_transition.TryBeginFailureFallback(
                    report.FailureCode,
                    RenderingTransitionTrigger.RuntimeFailure))
            {
                return;
            }

            // ★ 故障回退的路由判定：**期望路由不变**（用户的 GPU 选择不被悄悄改写，合同 U11），
            //   有效路由在步 9 落到 legacy；FallbackCode 承载**失败原因**本身。
            RenderingRouteDecision decision = new(
                RenderingRoute.Legacy,
                RenderBackendKind.LegacyGdiPlus,
                MaterialKind.Legacy,
                CaptureRequested: false,
                FallbackCode: report.FailureCode,
                Errors: []);

            RunGpuToLegacyTransition(decision, ThemeManager.Current);
        }

        /// <summary>
        /// 停止 GPU 呈现宿主。
        /// <para>★【合同 §6.2 / INV-1、INV-2】清理责任**不再**通过宿主引用表达 ——
        /// 停止之后 <c>_gpuPresentation</c> **无条件**是 <c>null</c>；未完成的 affinity 恢复责任
        /// 由 ledger 独立持有（合同 §6.2 的所有权形状）。</para>
        /// <para>★ 移交出口已接入：宿主释放会话时会把未完成的清理责任移交给 ledger
        /// （<c>CapturePolicyCleanupRequired</c> 事件 + <c>LastCapturePolicyCleanup</c> 兜底）。
        /// ★【LM-093】双路并存**安全**：两条路径登记的是<b>同一实例</b>
        /// ⇒ <see cref="CapturePolicyCleanupLedger.Add"/> 对<b>同一实例</b>幂等
        /// （返回"已在册"、不新增条目、<b>不</b>产生"登记被拒"诊断）；
        /// 只有<b>不同</b>条目撞同代次才被 INV-4 拒绝并留痕。</para>
        /// </summary>
        public void StopGpuPresentation()
        {
            GpuPresentationHostController? host = _gpuPresentation;
            if (host is null) return;

            try
            {
                host.Dispose();
            }
            finally
            {
                try
                {
                    // ★ 兜底：事件可能因订阅者异常而未送达 ⇒ 这里再取一次宿主记录的最后一次移交。
                    WindowCapturePolicyCleanup? handedOff = host.LastCapturePolicyCleanup;
                    if (handedOff is not null)
                    {
                        // ★【LM-074】移交被拒（INV-4）⇒ 留痕，不得静默丢弃。
                        bool registered = _captureCleanup.Add(handedOff);
                        if (!registered)
                        {
                            _transition.RecordCleanupWarning(
                                _captureCleanup.RegistrationRejectedCode);
                        }
                    }
                }
                finally
                {
                    // ★ 合同 §6.2 INV-2：无条件置 null —— 清理责任在 ledger，不在宿主引用上。
                    //   ★【LM-063 · 主审补修】放在**最内层 finally** ⇒ 即便上面的登记抛异常，
                    //   宿主引用也**一定**被释放（"释放引用"是 INV-2 的核心保证，不允许被旁路）。
                    _gpuPresentation = null;
                }
            }

            _transition.RecordCleanupState(
                _captureCleanup.State,
                _captureCleanup.CleanupErrorCode);
        }

        /// <summary>
        /// 【合同 §6.4 / §4.5 :198】把**原始** affinity 精确写回旧 HWND。
        /// <para>★ 恢复的是原始策略，**不能固定写成 <c>WDA_NONE</c></c>。</para>
        /// <para>★ 调用前提（合同 §7.4）：该 HWND 的代次必须**等于**当前代次 ——
        /// 由 ledger 在判定之后才调用本方法。</para>
        /// </summary>
        private CapturePolicyRestoreResult RestoreCapturePolicyCore(WindowCapturePolicyCleanup cleanup)
        {
            if (!cleanup.AffinityChangedBySession)
            {
                // 本程序没有改过 affinity ⇒ 无需恢复。
                return new CapturePolicyRestoreResult(true, null, null);
            }

            bool ok = MainFormWinHelper.RestoreDisplayAffinity(
                cleanup.WindowHandle,
                cleanup.OriginalAffinity);
            if (ok)
            {
                return new CapturePolicyRestoreResult(true, null, null);
            }

            return new CapturePolicyRestoreResult(
                false,
                CapturePolicyCleanupLedger.RestoreFailedErrorCode,
                "SetWindowDisplayAffinity failed for the superseded handle.");
        }

        /// <summary>
        /// Allows one new native GPU attempt only after the user explicitly
        /// applies settings. An unclean previous process otherwise remains in
        /// the conservative GDI+ route.
        /// </summary>
        public bool AllowGpuStartupRetryAfterExplicitSettingsApply()
        {
            // ★【合同 §6.4 / §8.4 条件 3 / U11】用户**显式**重试 ⇒ 清除"恢复预算已耗尽"标记，
            //   此后普通 Tick 才允许再次尝试启动 GPU。
            _transition.ClearGpuRecoveryExhausted();

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

            // ★【合同 §4.6】GPU→legacy 过渡期间用**派生**主题（回退样式已代入）；
            //   其余时间是 ThemeManager.Current（原对象，不被修改）。
            var t = CurrentRenderTheme;
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
            UpdateAppearanceContext(CurrentRenderTheme);
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
                // ★【GPU T03 / 合同 §6.4 / §8.1 规则 ④】判别位超时兜底：
                //   请求后 5 秒内未被消费 ⇒ 归零并记 gpu_presentation_handle_recreate_not_observed。
                _transition.EnforceControlledRecreateTimeout();

                // ★【GPU T03 / 合同 §6.4】ledger 的退避重试驱动源 = 既有 UI Tick，
                //   且**仅当** Now >= NextAttemptAt 才尝试 ⇒ 天然满足"禁止每帧重试"。
                _captureCleanup.Pump(DateTimeOffset.UtcNow);
                if (_captureCleanup.State != CapturePolicyCleanupState.None)
                {
                    _transition.RecordCleanupState(
                        _captureCleanup.State,
                        _captureCleanup.CleanupErrorCode);
                }

                // ★★【R02 / 合同 §4.4 / U11】UI 安全点：执行排队的"GPU 故障 → legacy"转换。
                //   ★ 必须排在下面的 GPU 重启兜底**之前** —— 否则一次启动失败刚被排进队列，
                //   同一个 Tick 里的重启兜底就会先把 GPU 再拉起来，回退永远轮不到。
                //   ★ 本处是 UI 消息循环（Timer 回调）⇒ 不在 OnPaint / WndProc 销毁回调 /
                //     GPU 帧绘制栈内（计划 §4.3 :169）。
                PumpGpuFailureFallback();

                // ★★【GPU T03 / 合同 §8.4 / §4.4 / U11】Tick 兜底的**启动守卫** ★★
                //   既有口径是 `_gpuRoute?.IsGpu == true`（只看期望路由）⇒ 普通 Tick 会把 GPU
                //   重新拉起来。合同要求收紧为：Phase == Stable 且 ExpectedRoute.IsGpu，
                //   且没有未终结的 gpu_presentation_recovery_exhausted 记录。
                //   ★ 这**不改变** src/UI/UIController.cs:951 的期望路由口径（合同 D8 / §13 自检），
                //   只收紧**启动**条件。
                if (_transition.CanStartGpuPresentation &&
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
            // ★ 重入保护（合同 §4 步 1 / 步 6 的禁止动作"不得递归 ApplyTheme"）：
            //   过渡流程进行中时不再启动一次新的路由切换。
            if (_inGpuTransition) return;

            // ★★【GPU T03 / 合同 §4 步 1】计算新请求 + 进入保护 ★★
            //   判别位（构造期首次创建 vs 受控重建）、幂等短路（U04）、Decide 失败处理
            //   全部由协调器判定（合同 §4 步 1：Decide 抛异常 ⇒ gpu_transition_decide_failed，
            //   **保持原状态**，不进入 Stopping）。
            //
            // ★【合同 §10 GPU-003 / 裁决 D4】WindowIsLayered 传入**真实读数**：
            //   分层窗口上不得申请 WDA 排除 ⇒ 有效路由降级为 GpuFixedInput。
            if (!_transition.TryBeginTransition(
                    new(
                        theme.Rendering,
                        GpuEffectsEnabled: _cfg.GpuThemeEffectsEnabled,
                        LiveBackgroundEnabled: _cfg.GpuLiveBackgroundEnabled,
                        WdaCapturePolicyAccepted: _cfg.GpuWdaCapturePolicyAccepted,
                        SafeMode: Program.SafeMode,
                        WindowIsLayered: MainForm.IsWindowLayered()),
                    RenderingTransitionTrigger.UserThemeChange,
                    out RenderingRouteDecision? decided) ||
                decided is null)
            {
                // 幂等短路（U04：同一请求重复到达 ⇒ 不再次重建，返回相同稳定状态）
                // 或 Decide 失败（合同 §4 步 1）⇒ 保持原状态，不进入 Stopping。
                return;
            }

            RenderingRouteDecision decision = decided;

            // ApplyTheme may synchronously paint the old GDI route before
            // reaching this decision. Do not let that desktop-DC layout leak
            // into the GPU route (or retain its window-DC layout on fallback).
            if (decision.IsGpu || _gpuRoute?.IsGpu == true)
                _hxLayout = null;

            if (!decision.IsGpu)
            {
                // ★【合同 §4 步 2–9】GPU → legacy 的固定顺序（只在**确实从 GPU 退出**时走）。
                bool wasGpu = _gpuRoute?.IsGpu == true || _gpuPresentation is not null;
                if (wasGpu)
                {
                    RunGpuToLegacyTransition(decision, theme);
                    return;
                }

                StopGpuPresentation();
                _gpuRoute = decision;
                if (_gpuPresentation is null) _gpuFallbackCode = decision.FallbackCode;
                _transition.PublishLegacyFallback();
                return;
            }

            // ★【合同 §4.6】回到 GPU 路线 ⇒ 派生回退样式**不再**参与渲染（legacy 外观恢复原样）。
            _legacyFallbackTheme = null;

            bool canReconfigure =
                _gpuPresentation is not null &&
                (_gpuPresentation.IsActive || _gpuPresentation.IsSuspended);
            bool reconfigured = false;
            string? reconfigureErrorCode = null;
            if (canReconfigure)
            {
                reconfigured = _gpuPresentation!.TryReconfigure(
                    decision.EffectiveMaterial,
                    decision.CaptureRequested,
                    _cfg.HorizontalMode,
                    theme.Rendering,
                    (float)_cfg.UIScale,
                    out reconfigureErrorCode,
                    out _, theme);
            }

            if (reconfigured)
            {
                _gpuRoute = decision;
                _gpuFallbackCode =
                    decision.CaptureRequested &&
                    !_gpuPresentation.IsLiveCaptureStarted
                        ? _gpuPresentation.LastErrorCode
                        : decision.FallbackCode;
                _gpuPresentation.ResetPerformanceWindows();
                UpdateGpuScene();
                // ★ GPU 路线就绪 ⇒ 发布稳定状态（否则协调器会一直停在 Stopping，
                //   §8.4 的启动守卫会永远为假 ⇒ Tick 兜底失效）。
                _transition.PublishStable(decision.Route);
                return;
            }

            // ★【LM-074】用户的 live / 主题切换被"**未解决**的清理责任"拒绝
            //   ⇒ 安排**一次**明确恢复（合同 §6.4 的"用户显式重试"）。
            //   ★ 主审裁决 ④的约束：只有**真正发起过 TryReconfigure**
            //     （canReconfigure == true）且拿到 gpu_capture_policy_cleanup_unresolved
            //     码时才安排恢复；canReconfigure == false 时 reconfigureErrorCode 恒为 null
            //     ⇒ 本调用**不触发**任何恢复（该场景由下面的 Start 失败分支负责）。
            //   ★★★ 顺序至关重要（必须排在 StopGpuPresentation() **之前**）：
            //     先安排明确恢复 ⇒ ledger 里先有一条携带**精确原值**的 Pending 责任；
            //     随后宿主停止时的移交若因 INV-4（同代次已有非终态责任）被拒，
            //     被拒的那条往往是"误采残留 0x11 为原值"的记录，
            //     而**先建**的重建责任携带的是精确原值 ⇒ 最终写回的是正确值。
            //     若顺序反过来（先停后恢复）⇒ 移交先占坑、恢复被拒 ⇒ 写回误采值
            //     ⇒ 报告 R10 的危害原样复活。
            ScheduleExplicitCapturePolicyRecovery(reconfigureErrorCode);

            // A suspended host can be reconfigured without replacing its device
            // or swap chain. Any other state, including a failed in-place
            // update, is rebuilt through the normal guarded startup path.
            StopGpuPresentation();
            if (_gpuPresentation is not null) return;
            _gpuRoute = decision;
            _gpuFallbackCode = decision.FallbackCode;
            // ★ 宿主会话代次 +1（合同 §2 维度 5 的 HostGeneration）。
            _transition.NextHostGeneration();
            _gpuPresentation = CreateGpuPresentationHost(
                decision.EffectiveMaterial,
                decision.CaptureRequested,
                theme.Rendering,
                ThemeManager.Current);
            if (_form.IsHandleCreated)
            {
                if (StartGpuPresentation())
                {
                    // ★ GPU 路线就绪 ⇒ 发布稳定状态（否则协调器停在 Stopping，
                    //   §8.4 的启动守卫恒为假 ⇒ Tick 兜底失效）。
                    _transition.PublishStable(decision.Route);
                }
                else
                {
                    // ★★★【LM-074】这里**必须**安排明确恢复（主审裁决 · 补漏）：
                    //   这是用户真正会撞上的那个点 ——
                    //   "GPU live 运行时恢复失败耗尽 ⇒ 用户切到 legacy 主题（宿主释放）
                    //     ⇒ 用户切回 GPU 主题 ⇒ canReconfigure == false（宿主是 null）
                    //     ⇒ 走不到 TryReconfigure ⇒ 第一个触发点不触发
                    //     ⇒ 新建宿主后 Start 被闸门拒绝（unresolved）"。
                    //   若这里不安排恢复，残留的 0x11 会一直留着
                    //   ⇒ "明确恢复"这条解除路径在**最常见的用户流程**里几乎不可达。
                    //
                    //   ★ 三条"不误触发"的保证（缺一不可）：
                    //   ① ScheduleExplicitCapturePolicyRecovery 内部**逐字比较**拒绝码，
                    //      只有 gpu_capture_policy_cleanup_unresolved 才动作；
                    //      gpu_capture_policy_cleanup_pending（还在正常重试）、
                    //      gpu_presentation_not_reconfigurable 及其它任何失败码**一律不触发**。
                    //   ② ledger 的重建入口幂等且拒绝叠加（同代次已有非终态责任 ⇒ 拒绝；
                    //      无可重建条目 ⇒ 拒绝）⇒ 反复触发不会堆积条目。
                    //   ③ 恢复本身只做**记账**（新建一条带精确原值的责任），**不触碰 HWND**；
                    //      真正的写回由 ledger 的退避重试完成（合同 §6.4）。
                    //
                    //   ★ 与前一个触发点的关系：上面的 TryReconfigure 分支里，
                    //   "安排恢复"必须排在 StopGpuPresentation() **之前**
                    //   （先建的那条责任携带**精确原值**）；
                    //   本触发点位于宿主停止**之后**，属另一条路径，两者不冲突 ——
                    //   但若两条路径都命中，ledger 的 INV-4 只会保留**先建**的那条
                    //   （即携带精确原值的那条）。
                    ScheduleExplicitCapturePolicyRecovery(_gpuFallbackCode);
                }
                // ★★【R02 / 合同 §4.4 / U11】启动失败 ⇒ **不再**在这里"只标记 Stable"：
                //   失败已经由统一调度入口排进队列，由 UI 安全点执行**完整**的 legacy 转换
                //   （合同 §4 步 2–9：受控 HWND 重建 + 布局/DPI 恢复 + 窗口状态恢复 + 一次有效重绘）。
                //   ★ 在那之前阶段停在 Stopping ⇒ §8.4 的启动守卫恒为假
                //   ⇒ 普通 Tick 不会把 GPU 再拉起来（合同 §4.4 :188）。
            }
            else
            {
                // 句柄未就绪 ⇒ GPU 尚未运行，等 OnShown / HandleCreated 再拉起；
                //   但期望路由已经是 GPU ⇒ 阶段必须回到 Stable，否则守卫恒为假。
                _transition.PublishStable(decision.Route);
            }
        }

        /// <summary>
        /// 【LM-074】WDA 取得被"**未解决**的清理责任"拒绝 ⇒ 安排**一次**明确恢复
        /// （合同 §6.4 的"用户显式重试"）。
        /// <para>★ 触发路径是**事件驱动**的 WDA 取得尝试（用户切换主题 / 开关 live /
        /// DPI 变化等 ⇒ <see cref="ApplyGpuRenderingRoute"/>）收到拒绝码
        /// <c>gpu_capture_policy_cleanup_unresolved</c>；
        /// <b>严禁</b>在 <c>Tick</c> / ledger 的 <c>Pump</c> 里自动触发
        /// （合同 §6.4"禁止每帧重试"）。</para>
        /// <para>★ 只有 <c>gpu_capture_policy_cleanup_unresolved</c> 会动作
        /// （判据在 <see cref="CapturePolicyCleanupLedger.TryScheduleExplicitRecoveryForRejectedCode"/>）：
        /// <c>gpu_capture_policy_cleanup_pending</c>（还在正常重试）、
        /// <c>gpu_presentation_not_reconfigurable</c> 及其它任何失败码**一律不触发**。</para>
        /// <para>★ 防重复触发：ledger 的重建入口对"同代次已有非终态责任"直接拒绝（INV-4）
        /// ⇒ 用户反复点击不会无限堆积条目。</para>
        /// <para>★ 恢复动作本身只做 ledger 记账（不触碰 HWND）⇒ 真正的写回由
        /// ledger 的退避重试（UI Tick 驱动）完成。</para>
        /// </summary>
        private void ScheduleExplicitCapturePolicyRecovery(string? errorCode)
        {
            _captureCleanup.TryScheduleExplicitRecoveryForRejectedCode(
                errorCode,
                WindowHandleGeneration.Instance.Current,
                DateTimeOffset.UtcNow,
                out _);
        }

        /// <summary>
        /// 【GPU T03 / 合同 §4.6 / GPU T05】legacy 渲染当前应使用的**主题快照**。
        /// <para>★ GPU→legacy 过渡期间是**派生**副本（回退样式已代入）；
        /// 其余时间是 <see cref="ThemeManager.Current"/>（原对象，不做任何修改）。</para>
        /// </summary>
        private Theme CurrentRenderTheme => _legacyFallbackTheme ?? ThemeManager.Current;

        /// <summary>
        /// 【GPU T03 / 合同 §4.6 / GPU T05】主题重新加载后重算**派生**回退样式。
        /// <para>★ 未处于 legacy 回退态（<c>_legacyFallbackTheme == null</c>）⇒ **不**引入派生样式，
        /// 保持"只有从 GPU 退出到 legacy 时才用回退样式"的产品语义。</para>
        /// <para>★【LM-070 / R06】重载进来的主题**本身**不是 GPU 主题（原生 legacy 主题）
        /// ⇒ **不得**套用任何回退配色 ⇒ **清空**派生覆盖并返回。
        /// R06 说的"覆盖一直挂到重新进入 GPU 或重启"就修在这里：原实现只要
        /// <c>_legacyFallbackTheme != null</c> 就会拿新主题重算覆盖，而原生旧主题的
        /// <c>fallbackStyle</c> 解析结果是**中性默认值** ⇒ 旧主题自身配色被整组盖掉。</para>
        /// </summary>
        private void RefreshLegacyFallbackTheme(Theme theme)
        {
            // ★【LM-070 / R06】目标主题**本身**不是 GPU 主题 ⇒ 不套回退配色。
            if (!LegacyFallbackStyleBridge.IsGpuTheme(theme))
            {
                _legacyFallbackTheme = null;
                return;
            }

            if (_legacyFallbackTheme is null) return;

            LegacyFallbackStyle style = LegacyFallbackStyleBridge.Resolve(
                theme.Rendering, out _);
            _legacyFallbackTheme = LegacyFallbackStyleBridge.BuildDerivedTheme(theme, style);
        }

        // ================================================================
        // 【GPU T03 / 合同 §4】GPU → legacy 的固定顺序（步 2 – 9）
        // ================================================================

        /// <summary>
        /// 【合同 §4 步 2 – 9】GPU → legacy 的固定顺序。
        /// <para><b>全局约束</b>（合同 §4）：所有窗口操作在 UI 线程执行；
        /// 禁止在 <c>OnPaint</c> / <c>WndProc</c> 的销毁回调 / GPU 帧绘制栈内递归执行本流程；
        /// 关闭窗体拥有最高优先级（关闭过程中不得重建 HWND、不得恢复 affinity、不得重新创建宿主）。</para>
        /// </summary>
        private void RunGpuToLegacyTransition(RenderingRouteDecision decision, Theme theme)
        {
            MainForm mainForm = MainForm;

            // ★ 重入保护：步 6 需要重放主题/布局，ApplyTheme 会再次调用 ApplyGpuRenderingRoute
            //   ⇒ 合同 §4 步 1 / 步 6 的禁止动作"不得递归 ApplyTheme"。
            _inGpuTransition = true;
            try
            {
                // ───────── 步 2：记录窗口状态 + 停呈现调度 ─────────
                // ★ 禁止动作（计划 §4.3 :167）：不得触碰 HWND ex-style；
                //   不得调用 SetWindowDisplayAffinity；不得销毁业务对象。
                RenderingWindowSnapshot? snapshot = CaptureWindowState(mainForm);
                if (!_transition.TryCaptureWindowState(snapshot))
                {
                    // 合同 §4 步 2 失败语义：Phase=Failed，**不重建**。
                    return;
                }

                // 停呈现调度（GPU 帧绘制栈内不得递归执行后续步骤 ⇒ 先停调度）。
                _gpuPresentation?.Suspend();

                // ───────── 步 3：停止捕获 + 释放资源 + 转交清理 ─────────
                // ★ 禁止动作（计划 §4.3 :168）：不得以"宿主引用非空"代理"清理责任仍在"
                //   —— 清理责任由 ledger 独立持有（合同 §6.2）。
                StopGpuPresentation();
                _transition.RecordCleanupState(
                    _captureCleanup.State,
                    _captureCleanup.CleanupErrorCode);

                // ───────── 步 4：受控 HWND 重建 ─────────
                if (!RequiresControlledRecreate())
                {
                    // 重建触发依据不成立（计划 §4.1 :125-126）⇒ 无需重建，直接进入步 6。
                    CompleteLegacyTransition(decision, theme, mainForm, snapshot);
                    return;
                }

                _transition.EnterRecreatingHandle();
                if (!mainForm.TryRecreateHandleForTransition("gpu_to_legacy"))
                {
                    // 合同 §4 步 4 失败语义：Phase=Failed + 保留可控重试入口；
                    // **禁止**显示"已成功恢复"（计划 §4.3 :179）。
                    _transition.FailHandleRecreate();
                    return;
                }

                // ───────── 步 5：原生生命周期 + 代次推进 ─────────
                //   已在 MainForm 的 HandleCreated 处理器内完成：
                //   代次推进（Advance）+ 判别位消费（TryConsumeControlledRecreate）。

                // ───────── 步 6 – 9 ─────────
                CompleteLegacyTransition(decision, theme, mainForm, snapshot);
            }
            finally
            {
                _inGpuTransition = false;
            }
        }

        /// <summary>
        /// 【合同 §4 步 6 – 9】恢复布局 → 恢复窗口状态 → 一次有效重绘 → 发布完成状态。
        /// </summary>
        private void CompleteLegacyTransition(
            RenderingRouteDecision decision,
            Theme theme,
            MainForm mainForm,
            RenderingWindowSnapshot snapshot)
        {
            // ───────── 步 6：恢复大小 / DPI + 重算布局 ─────────
            // ★ 禁止动作（计划 §4.3 :174）：不得把旧显示器的已缩放像素布局直接当新布局使用
            //   ⇒ 位置按**当前**屏幕的工作区重新夹紧。
            bool layoutOk = RestoreGeometryAfterRecreate(mainForm, snapshot);
            if (layoutOk)
            {
                // 重建画笔缓存（画刷绑定在已销毁的 DC 上，必须丢弃）并标脏布局。
                UIRenderer.ClearCache();
                UIUtils.ClearBrushCache();
                _layoutDirty = true;
                _hxLayout = null;

                // ★【LM-070 / R06】目标主题**本身**不是 GPU 主题（原生 legacy 主题）
                //   ⇒ **不得**套用任何回退配色 ⇒ 清空派生覆盖，直接用主题自身。
                if (!LegacyFallbackStyleBridge.IsGpuTheme(theme))
                {
                    _legacyFallbackTheme = null;
                    _form.BackColor = ThemeManager.ParseColor(theme.Color.Background);
                }
                else
                {
                    // ★【合同 §4.6 / GPU T05】legacy 回退外观：解析 resolver 的输出，生成**派生**样式副本，
                    //   驱动 legacy 渲染。★ 绝不改 theme.Color / ThemeManager.Current / 任何写盘路径。
                    LegacyFallbackStyle fallbackStyle = LegacyFallbackStyleBridge.Resolve(
                        theme.Rendering, out IReadOnlyList<string> fallbackDiagnostics);
                    foreach (string diagnostic in fallbackDiagnostics)
                    {
                        // 非致命诊断（合同 §5.3 规则 1：不得进 Validate() 错误列表；
                        // §5.3 规则 2：不得覆盖既有故障根因）。
                        _transition.RecordNonFatalDiagnostic(diagnostic);
                    }

                    _legacyFallbackTheme = LegacyFallbackStyleBridge.BuildDerivedTheme(
                        theme, fallbackStyle);
                    _form.BackColor = ThemeManager.ParseColor(_legacyFallbackTheme.Color.Background);
                }
            }
            _transition.RecordLayoutRestore(layoutOk);
            if (!layoutOk)
            {
                // 合同 §4 步 6 失败语义：Phase=Failed，保留快照供重试。
                return;
            }

            // ───────── 步 7：恢复透明度 / 穿透 / 置顶 / 可见性 ─────────
            // ★ 禁止动作（计划 §4.3 :175）：不得 Show() 隐藏窗口；不得抢焦点；不得重复启动淡入。
            //   期望状态 = Opacity&lt;1 || ClickThrough（合同 §4 步 7 / GPU-003）。
            mainForm.RefreshTopMost(forceReinsert: true);
            if (snapshot.Opacity < 1.0 && Math.Abs(mainForm.Opacity - snapshot.Opacity) > 0.001)
            {
                // 分层 alpha 恢复（仅在**确实分层**时写；写 1.0 会清掉 WS_EX_LAYERED，
                // 那会破坏"开启穿透 ⇒ 分层"的既有语义，如同 §1.3 明确否决的行为）。
                mainForm.Opacity = snapshot.Opacity;
            }
            mainForm.SetClickThrough(_cfg.ClickThrough);
            if (!snapshot.Visible && mainForm.Visible)
            {
                // 隐藏窗口保持隐藏（不得 Show、不得激活）。
                mainForm.Hide();
            }
            _transition.RecordWindowStateRestore(true);

            // ───────── 步 8：一次有效重绘 + 轮廓 ─────────
            // ★ 禁止动作（计划 §4.3 :176）：不得依赖普通采样刷新周期
            //   ⇒ 显式 Invalidate + Update，在同一次 UI 处理流程内完成 legacy 首帧。
            mainForm.ApplyWindowAppearance();
            mainForm.Invalidate();
            mainForm.Update();

            bool windowVisible =
                mainForm.Visible && mainForm.WindowState != FormWindowState.Minimized;
            _transition.RecordFirstFrame(windowVisible, windowVisible);

            // ───────── 步 9：发布完成状态 ─────────
            // ★ 禁止在未拿到 legacy 首帧证据时显示"已成功恢复"（计划 §4.3 :177-179）：
            //   隐藏窗口 ⇒ PendingFirstFrame = true（协调器记录"已切换、等待显示"）。
            _gpuRoute = decision;
            if (_gpuPresentation is null) _gpuFallbackCode = decision.FallbackCode;
            _transition.PublishStable(RenderingRoute.Legacy);

            // ───────── 步 6 – 9 收尾：按配置恢复实时玻璃（LM-072 / R08）─────────
            // ★ 受控重建期间句柄没了 ⇒ 捕获服务被**临时停机**（配置态的开关仍为开）。
            //   目标 legacy 已就绪 ⇒ 此处按配置把服务拉回来；
            //   **不得**在句柄重建过程中恢复（那时句柄尚未就绪，捕获请求是不完整的）。
            // ★ GPU 宿主已在步 3 停止 ⇒ 此刻只有实时玻璃一个捕获所有者（合同 §13.2）。
            if (_cfg.LiveGlassEnabled)
            {
                _liveGlass?.RestoreIfEnabled();
            }
        }

        /// <summary>
        /// 【合同 §4 步 4 / 计划 §4.1 :123-126】受控 HWND 重建的触发依据。
        /// <para>★ **不得**用 <c>newHwnd != oldHwnd</c> 作为唯一重建断言（计划 §4.1 :129）——
        /// 这里依据的是**呈现历史**（单调推进的绑定状态）。</para>
        /// <para>★【R02】判据本身放在协调器里 ⇒ "用户手动关闭 GPU"与"GPU 故障回退"
        /// 两条路径<b>共用同一个</b>判据 ⇒ 两条路径的重建决策天然对称。</para>
        /// </summary>
        private bool RequiresControlledRecreate() =>
            _transition.RequiresControlledRecreate();

        /// <summary>【合同 §4 步 2】快照当前窗口状态。</summary>
        private RenderingWindowSnapshot? CaptureWindowState(MainForm form)
        {
            try
            {
                if (form.IsDisposed || !form.IsHandleCreated) return null;

                return new RenderingWindowSnapshot(
                    form.Bounds,
                    form.ClientSize,
                    form.DeviceDpi,
                    form.Visible,
                    form.WindowState,
                    form.TopMost,
                    _cfg.ClickThrough,
                    form.Opacity);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 【合同 §4 步 6】按**当前**屏幕重新求几何并写回窗口位置/尺寸。
        /// <para>★ 禁止动作（计划 §4.3 :174）：不得把旧显示器的已缩放像素布局直接当新布局使用
        /// ⇒ 位置按当前 <see cref="Screen.WorkingArea"/> 重新夹紧。</para>
        /// </summary>
        private static bool RestoreGeometryAfterRecreate(MainForm form, RenderingWindowSnapshot snapshot)
        {
            try
            {
                Screen screen = Screen.FromRectangle(snapshot.Bounds);
                Rectangle work = screen.WorkingArea;
                int width = snapshot.Bounds.Width > 0 ? snapshot.Bounds.Width : snapshot.ClientSize.Width;
                int height = snapshot.Bounds.Height > 0 ? snapshot.Bounds.Height : snapshot.ClientSize.Height;
                if (width <= 0 || height <= 0) return false;

                int x = Math.Clamp(
                    snapshot.Bounds.Left,
                    work.Left,
                    Math.Max(work.Left, work.Right - width));
                int y = Math.Clamp(
                    snapshot.Bounds.Top,
                    work.Top,
                    Math.Max(work.Top, work.Bottom - height));

                form.Bounds = new Rectangle(x, y, width, height);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 启动 GPU 呈现宿主。
        /// <para>★ 合同 §4.4 / §8.4 / U11：启动失败后**不得**让普通 Tick 无条件把 GPU 再拉起来 ——
        /// 恢复预算耗尽（<c>gpu_presentation_recovery_exhausted</c>）时记在案，
        /// 只有用户显式重试才清除。</para>
        /// </summary>
        /// <returns><c>true</c> = 宿主已成功启动。</returns>
        private bool StartGpuPresentation()
        {
            if (_gpuPresentation is null || _gpuRoute?.IsGpu != true)
            {
                return false;
            }

            WindowCapturePolicy policy = _gpuRoute.CaptureRequested
                ? WindowCapturePolicy.WdaExcludeFromCapture
                : WindowCapturePolicy.None;
            if (_gpuPresentation.Start(
                    policy,
                    out string? errorCode,
                    out string? errorMessage))
            {
                // ★【合同 §2 维度 6 / §4 步 4】HWND 呈现历史：直接取宿主的精确值
                //   （Presented / PresentAttempted / Bound / Unbound）——
                //   重建触发依据（计划 §4.1 :123-126）据此决定是否必须受控重建 HWND。
                _transition.RecordBindingState(_gpuPresentation.BindingState);
                _gpuFallbackCode = _gpuPresentation.RequestsLiveBackground &&
                    !_gpuPresentation.IsLiveCaptureStarted
                    ? _gpuPresentation.LastErrorCode
                    : _gpuRoute.FallbackCode;
                UpdateGpuScene();
                if (!_form.Visible || _form.WindowState == FormWindowState.Minimized)
                    _gpuPresentation.Suspend();
                return true;
            }

            // ══════════════【R02 / 合同 §4.4 / U11】启动失败 → 统一调度到 legacy 转换 ══════════════
            // ★ 旧实现在这里只做 `StopGpuPresentation(); _form.Invalidate();` ——
            //   那**不**走完整的 legacy 转换流程，也不按呈现历史决定是否受控重建 HWND
            //   ⇒ 与"用户手动关闭 GPU"路径不对称（GPU 已经呈现过 flip model 时画面会冻结）。
            // ★ 现在：记录失败原因 + 本代次呈现历史 → 交给统一调度入口入队，
            //   由 UI 安全点执行与手动关闭路径**同一个** legacy 转换入口。

            GpuPresentationHostController host = _gpuPresentation;
            string? failureReason = host.FailureReasonCode ?? errorCode;
            string? failureMessage = host.FailureReasonMessage ?? errorMessage;
            PresentationBindingState bindingState = host.BindingState;
            FirstFrameOutcome firstFrameOutcome = host.FirstFrameOutcome;

            // ★【合同 §4 步 4 / 计划 §4.1 :123-126】本代次的 HWND 呈现历史 ——
            //   "是否需要受控重建 HWND"的**唯一**依据。
            _transition.RecordBindingState(bindingState);

            bool recoveryExhausted = string.Equals(
                errorCode,
                RenderingTransitionCoordinator.RecoveryExhaustedCode,
                StringComparison.Ordinal);

            // ★ 阶段判定：宿主已经消耗过那一次自动恢复（<c>RecoveryAttempted</c>）
            //   ⇒ 这次启动失败就是"运行故障恢复耗尽"（计划 §4.4 :186）；
            //   否则是"启动失败"。两条都会走同一条 legacy 转换入口。
            GpuFailureStage stage =
                recoveryExhausted || host.RecoveryAttempted
                    ? GpuFailureStage.RecoveryExhausted
                    : GpuFailureStage.Startup;

            // ★ 统一调度入口（只记录 + 入队；真正的转换在 UI 安全点执行）。
            ScheduleGpuFailureFallback(new GpuFailureReport(
                stage,
                failureReason ?? errorCode ?? "gpu_presentation_start_failed",
                failureMessage,
                bindingState,
                firstFrameOutcome));

            // ★ 保留失败原因：不得退化成"普通 fallback code"（合同 §5.3 规则 2 / U11）。
            _gpuFallbackCode = failureReason ?? errorCode;

            // ★【合同 §4.4 / §8.4 条件 3 / U11】恢复预算耗尽 ⇒ 普通 Tick 不得再自动拉起 GPU。
            if (recoveryExhausted)
            {
                _transition.MarkGpuRecoveryExhausted();
            }

            StopGpuPresentation();
            return false;
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
                        CurrentRenderTheme,
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
                        height = Math.Max(height, MinEmptyPanelHeight(CurrentRenderTheme));
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

            // ★★【R02】丢弃排队的故障回退请求 ——
            //   关闭拥有最高优先级（合同 §4 全局约束 / U10）：关闭过程中不得重建 HWND、
            //   不得恢复 affinity、不得重新创建宿主。
            _gpuFailureFallback.Clear();

            // ★【GPU T03 / 合同 §6.4 / INV-5】清理责任在窗体关闭时**终结**（幂等）。
            //   ★ 关闭拥有最高优先级（合同 §4 全局约束）：关闭过程中不得恢复 affinity
            //     ⇒ 此处**不发起**新的恢复尝试，只把未终结的清理责任终结为 form_closed。
            //     （合同 §6.4 的"关闭时最多再尝试 1 次"是**上限**，0 次同样合规。）
            _captureCleanup.FinalizeOnFormClosed(DateTimeOffset.UtcNow, allowFinalAttempt: false);

            // 降级提示的画刷
            _noticeBrush?.Dispose();
            _noticeBrush = null;
        }
    }
}
