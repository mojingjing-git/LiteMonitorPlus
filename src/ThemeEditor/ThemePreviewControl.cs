using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LiteMonitor.Rendering;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 实时预览控件（独立于 LiteMonitor++ 主程序）
    /// - 使用 Mock 数据
    /// - 使用 UILayout + UIRenderer 渲染
    /// - 背景 + 边框 + DPI 处理
    ///
    /// ★ 预览副本合同（外观改造计划 v4 §3.2 / A10）：
    /// 1. 布局使用 <b>独立副本</b>：<c>original.Layout.CreatePreviewCopy(dpiScale)</c>，
    ///    复制全部配置字段与扩展数据，并且<b>只缩放一次</b>
    ///    （源已缩放过时不再重复缩放，LayoutScale 语义保持正确）。
    ///    <b>禁止</b>手写 <c>new LayoutConfig { ... }</c> 逐字段复制。
    /// 2. 颜色 / 阈值 / 背景配置<b>只读借用</b>，预览不会修改它们。
    /// 3. 字体<b>只借用，不释放</b>原主题字体（预览主题绝不调用 DisposeFonts）。
    /// 4. 预览<b>不启动桌面捕获</b>：玻璃使用固定样例图，并在画面上注明"预览"。
    /// 5. 实际 DPI 取当前控件的 <see cref="Control.DeviceDpi"/>，不用全局 UIUtils.ScaleFactor。
    /// 6. 归一化只发生在运行时副本（ThemeAppearanceOptions），绝不回写用户 JSON。
    ///
    /// ★ 正式渲染器合同（A13 收口 / 独立验收 R2）：
    /// 1. 预览与主窗口<b>共用同一套渲染器</b>：本控件持有 <see cref="AppearanceRuntime"/>，
    ///    绘制时走 <c>UIRenderer.Render(g, groups, theme, appearance)</c> 四参重载 ——
    ///    外框、辉光、窗口底纹（扫描线 / 网格）与新进度条样式因此与主窗口<b>完全一致</b>。
    /// 2. 预览<b>不再自绘</b>假扫描线 / 假网格，也不再使用"透明背景"变通：
    ///    背景层、底纹、外框、辉光、进度条一律由正式渲染层绘制。
    /// 3. 生命周期：主题切换（主题代次自增）、尺寸变化、DPI 变化都会让运行时快照失效重建；
    ///    <see cref="AppearanceRuntime"/> 在 <see cref="Dispose(bool)"/> 里释放，绝不泄漏。
    /// 4. 玻璃底图仍然只来自<b>固定样例图</b>，预览路径<b>绝不</b>访问真实桌面：
    ///    样例图通过 <c>AppearanceRuntime</c> 的 <c>previewSource</c> 参数进入
    ///    <c>options.PreviewSource</c>，背景层走 A08 已验证的"预览固定源优先"分支 ——
    ///    <b>完全不访问帧后端</b>（后端调用次数 = 0），且来源诊断是
    ///    <c>PreviewFrame</c> 而<b>不是</b> <c>LiveFrame</c>（独立复核 N2）。
    /// </summary>
    public class ThemePreviewControl : Panel
    {
        private Theme? _theme;

        /// <summary>预览副本（主题 / DPI 变化时重建；其余情况复用，保证运行时快照稳定）。</summary>
        private Theme? _previewTheme;

        /// <summary>预览副本的来源主题（用来判断副本是否需要重建）。</summary>
        private Theme? _previewThemeSource;

        /// <summary>预览副本使用的 DPI 比例（与 <see cref="_previewTheme"/> 配套）。</summary>
        private float _previewDpiScale = -1f;

        private UILayout? _layout;
        private readonly List<GroupLayoutInfo> _groups = new();
        private float _dpiScale = 1.0f;

        // ★★★ 新增：复用 Bitmap，避免每次 OnPaint 都创建 ★★★
        private Bitmap? _cachedBitmap;
        private Size _lastSize;

        /// <summary>正式外观运行时：预览与主窗口共用同一套渲染器与资源生命周期。</summary>
        private readonly AppearanceRuntime _appearance;

        /// <summary>预览专用的固定样例图（静态图案，绝不来自桌面捕获）。</summary>
        private Bitmap? _previewSample;

        /// <summary>主题是否已换代（换代时让运行时主题代次自增、各层缓存失效）。</summary>
        private bool _themeChanged = true;

        /// <summary>GDI 布局的构建条件跟踪（主题换代 / DPI / 宽度变化才重建）。</summary>
        private float _layoutDpiScale;
        private int _lastLayoutWidth = -1;
        private int _lastLayoutHeight;

        /// <summary>控件是否已释放（释放后不再触碰运行时）。</summary>
        private bool _disposed;
        private GpuThemePreviewControl? _gpuPreview;

        private ThemeEditTarget _target = ThemeEditTarget.Classic;
        private PreviewShape _shape = PreviewShape.Vertical;
        private Theme? _rawTheme;

        public ThemePreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;

            // 正式外观运行时：背景层 / 装饰层 / 进度条与主窗口完全同源。
            // ★ 预览**不注册任何实时帧后端** —— 固定样例图通过 previewSource 走
            //   options.PreviewSource 路径（A08 已验证：完全不访问后端，后端调用次数 = 0），
            //   并且来源诊断是 PreviewFrame 而不是 LiveFrame（独立复核 N2）。
            _appearance = new AppearanceRuntime(previewSource: GetPreviewSample());

            // DPI 缩放支持 - 手动计算内边距
            _dpiScale = GetDpiScale();
            Padding = new Padding((int)(5 * _dpiScale));
        }

        /// <summary>
        /// 当前控件的实际 DPI 比例。
        /// 取当前窗口的 <see cref="Control.DeviceDpi"/>（所在显示器的真实 DPI），
        /// <b>不</b>使用全局 <c>UIUtils.ScaleFactor</c>（进程级值，跨屏不可靠）。
        /// </summary>
        public float DpiScale => GetDpiScale();

        /// <summary>当前会话设备创建/重建次数（穿透诊断）。</summary>
        public int DeviceCreationCount => _gpuPreview?.DeviceCreationCount ?? 0;

        /// <summary>当前会话参数热更新次数（穿透诊断）。</summary>
        public int SessionUpdateCount => _gpuPreview?.SessionUpdateCount ?? 0;

        /// <summary>当前 GPU 会话代次（穿透诊断）。</summary>
        public long CurrentGeneration => _gpuPreview?.CurrentGeneration ?? 0;

        /// <summary>GPU 预览实际呈现帧数（R10：门禁必须验证真实出帧，而不是只看资源数量）。</summary>
        public int PresentedFrameCount => _gpuPreview?.PresentedFrameCount ?? 0;

        /// <summary>当前 GPU 预览调度器目标帧率；无 GPU 会话时为 0。</summary>
        public int TargetFramesPerSecond => _gpuPreview?.TargetFramesPerSecond ?? 0;

        /// <summary>GPU 子控件最后成功保留的主题；仅供测试与 GDI 回退状态独立比较。</summary>
        internal Theme? CommittedGpuThemeForTesting => _gpuPreview?.CommittedThemeForTesting;
        internal bool TryCaptureOnceForTesting(string pngPath) => _gpuPreview?.TryCaptureOnce(pngPath) == true;

        internal string PreviewDiagnosticsForTesting =>
            $"target={_target} gpuReq={_rawTheme?.Rendering?.IsGpuRequested} " +
            $"safeMode={Program.SafeMode} handle={IsHandleCreated} size={Width}x{Height} " +
            $"gpuCreated={_gpuPreview != null} gpuVisible={_gpuPreview?.Visible} " +
            $"gpuActive={_gpuPreview?.IsActive} gpuErr={_gpuPreview?.Error ?? "<null>"} " +
            $"gpuDevCreations={DeviceCreationCount} sessionUpdates={SessionUpdateCount} " +
            $"scene=[{_gpuPreview?.SceneDiagnosticsForTesting ?? "<none>"}]";

        /// <summary>父控件交给 GDI 路线的实际主题；仅供测试验证失败后的完整回退派生。</summary>
        internal Theme? GdiRenderedThemeForTesting => _theme;

        /// <summary>
        /// 当前预览是否走 GPU 真实渲染（供状态区显示"实际路线"而非声明路线）。
        /// GPU 不可用 / 关闭特效 / 安全模式时恒为 false。
        /// </summary>
        public bool IsGpuPreviewActive => _gpuPreview?.IsActive == true && _gpuPreview.Visible;

        /// <summary>GPU 预览不可用原因（未尝试或可用时为 null）。</summary>
        public string? GpuPreviewError => _gpuPreview?.Error;

        /// <summary>GPU 预览运行期不可用时触发，宿主应切换到同形态的 GDI 回退。</summary>
        public event EventHandler? GpuPreviewUnavailable;

        /// <summary>
        /// 当前画布**实际渲染**的主题副本（R1 整改新增可观测量）。
        /// 回退目标下是 <c>LegacyFallbackStyleResolver.BuildDerivedTheme</c> 的派生主题，
        /// 与 <c>_rawTheme</c> 不同 —— 这正是「目标切换后画布没跟着变」的可观测证据。
        /// </summary>
        internal Theme? RenderedTheme => _theme;

        /// <summary>当前画布实际渲染的编辑目标（R1 整改新增可观测量）。</summary>
        internal ThemeEditTarget RenderedTarget => _target;

        /// <summary>当前画布形态（R2 F05 回归可观测量）。</summary>
        internal PreviewShape CurrentShape => _shape;

        /// <summary>
        /// 编辑器传入主题
        /// </summary>
        public void SetTheme(
            Theme theme,
            ThemeEditTarget target = ThemeEditTarget.Classic,
            PreviewShape shape = PreviewShape.Vertical)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));

            ThemeEditTarget effectiveTarget = target;
            if (effectiveTarget == ThemeEditTarget.Classic && theme.Rendering?.IsGpuRequested == true)
            {
                effectiveTarget = ThemeEditTarget.GpuMain;
            }

            _rawTheme = theme;
            _target = effectiveTarget;
            _shape = shape;
            _dpiScale = GetDpiScale();

            // mock 场景输入是 GPU 与 GDI 两条预览路径共用的（轻量对象构建 + 一次布局），
            // 必须在 GPU 分支之前完成——否则 GPU 场景拿到空组，预览只剩材质层。
            BuildMockData();

            // 编辑目标是“用户想编辑哪条路线”，实际绘制主题还要看 GPU 是否真的可用。
            // F04：主效果目标在 GPU 关闭/安全模式/初始化失败时也必须走同一份
            // legacy fallback 派生主题，不能继续用主效果原始 Color 画 GDI 兜底。
            UpdateGpuPreview();

            if (_gpuPreview is { IsActive: true, Visible: true })
            {
                // GPU 预览已接管画布：GDI 兜底层保持轻量——不做主题深拷贝、布局与
                // mock 构建，也不触发重绘（重绘结果完全被 GPU 表面盖住，纯浪费）。
                // GPU 失败回退时 UpdateGpuPreview 会立即走 BuildGdiPreview。
                return;
            }

            BuildGdiPreview();
        }

        /// <summary>
        /// 构建 GDI 绘制路径的全部状态（主题渲染副本、预览深拷贝、布局、mock 数据）。
        /// 只在 GDI 真正要画的时候调用——GPU 预览活跃时这些工作全部被表面遮住。
        /// </summary>
        private void BuildGdiPreview()
        {
            if (_rawTheme is null)
            {
                return;
            }

            Theme themeToRender = ResolveGdiTheme(_target);
            _theme = themeToRender;

            // ★ 主题可能是**就地修改**的（编辑器改一个字段就会调一次 SetTheme），
            //   所以每次都强制重建预览副本 —— 不能按引用复用，否则改动的字段不会进预览。
            _previewTheme = null;
            _previewThemeSource = null;

            // 创建 DPI 缩放后的主题副本用于预览（独立布局副本，见类注释合同）
            _layout = new UILayout(CreatePreviewTheme(themeToRender, _dpiScale));

            BuildMockData();

            // 换肤：主题代次自增，让各层缓存（纹理 / 渐变槽 / 噪点）失效
            _themeChanged = true;

            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateGpuPreview();
        }

        /// <summary>
        /// 解析 GDI 实际应绘制的主题。编辑目标与运行路线分开：
        /// 主效果目标只有在 GPU 真正可用时才用原主题，否则用兼容派生主题。
        /// </summary>
        private Theme ResolveGdiTheme(ThemeEditTarget target)
        {
            if (_rawTheme is null) throw new InvalidOperationException("Theme is not set.");

            if (target == ThemeEditTarget.GpuFallback)
            {
                return LegacyFallbackStyleResolver.BuildDerivedTheme(_rawTheme);
            }

            if (target == ThemeEditTarget.GpuMain && !IsGpuRouteUsable())
            {
                return LegacyFallbackStyleResolver.BuildDerivedTheme(_rawTheme);
            }

            return _rawTheme;
        }

        private bool IsGpuRouteUsable()
        {
            // 工作室预览不走生产 opt-in：预览控件有独立启动守卫（不读写生产崩溃标记）、
            // 固定输入（不抓屏）、不消耗生产保守状态——编辑 GPU 主题时必须能看到
            // GPU 效果，否则预览显示的是兼容派生外观，与主题真实效果完全不同。
            // 生产主面板的 opt-in 语义在 GpuPresentationHostController 侧保持不变。
            return _rawTheme?.Rendering?.IsGpuRequested == true && !Program.SafeMode;
        }

        private void UseGdiFallbackTheme()
        {
            if (_rawTheme is null || ReferenceEquals(_theme, _rawTheme) == false)
            {
                return;
            }

            Theme fallback = LegacyFallbackStyleResolver.BuildDerivedTheme(_rawTheme);
            _theme = fallback;
            _previewTheme = null;
            _previewThemeSource = null;
            _layout = new UILayout(CreatePreviewTheme(fallback, _dpiScale));
            Invalidate();
            NotifyGpuUnavailable();
        }

        private void NotifyGpuUnavailable()
        {
            if (!IsHandleCreated || _disposed)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() => GpuPreviewUnavailable?.Invoke(this, EventArgs.Empty)));
            }
            catch
            {
                // 关闭/句柄销毁阶段不再抛给宿主。
            }
        }

        private void UpdateGpuPreview()
        {
            if (_disposed || _rawTheme == null)
            {
                if (_gpuPreview != null) _gpuPreview.Visible = false;
                return;
            }

            if (_target == ThemeEditTarget.GpuFallback)
            {
                if (_gpuPreview != null)
                {
                    _gpuPreview.Visible = false;
                }
                return;
            }

            if (_target == ThemeEditTarget.GpuMain && _rawTheme.Rendering?.IsGpuRequested == true)
            {
                if (Program.SafeMode)
                {
                    BuildGdiPreview();
                    if (_gpuPreview != null)
                    {
                        _gpuPreview.Visible = false;
                    }
                    return;
                }

                if (!IsHandleCreated)
                {
                    return;
                }

                Rectangle targetBounds = new(
                    Padding.Left,
                    Padding.Top,
                    Math.Max(1, Width - Padding.Horizontal),
                    Math.Max(1, Height - Padding.Vertical));

                if (_gpuPreview == null)
                {
                    // GPU 预览消费原始主题的 DPI 副本——不经过 _theme（那是 GDI
                    // 绘制路径的状态，GPU 激活时 BuildGdiPreview 尚未执行）。
                    _gpuPreview = new GpuThemePreviewControl(
                        CreatePreviewTheme(_rawTheme, GetDpiScale()),
                        _groups,
                        shape: _shape)
                    {
                        Bounds = targetBounds
                    };
                    Controls.Add(_gpuPreview);
                }
                else
                {
                    _gpuPreview.Bounds = targetBounds;

                    // ★ R6 整改（计划 §5.1 A-3）：必须消费 UpdateTheme 的返回值 ——
                    //   旧代码丢弃 bool 并继续把预览设为可见 ⇒ 主题非法时画布仍显示
                    //   上一个主题（用户看到「改了没反应」），且错误被彻底吞掉。
                    //   失败时隐藏 GPU 预览并保留 GpuThemePreviewControl.Error 以便 UI 提示。
                    //   传给 UpdateTheme 的同样基于 _rawTheme（GPU 分支不依赖 _theme）。
                    if (!_gpuPreview.UpdateTheme(
                            CreatePreviewTheme(_rawTheme, GetDpiScale()),
                            _groups,
                            _shape))
                    {
                        BuildGdiPreview();
                        _gpuPreview.Visible = false;
                        return;
                    }
                }

                _gpuPreview.Visible = true;
                _gpuPreview.BringToFront();
            }
            else
            {
                if (_gpuPreview != null)
                {
                    _gpuPreview.Visible = false;
                }
            }
        }

        // ================================================================
        // 预览副本（静态、可测试）
        // ================================================================

        /// <summary>
        /// 创建预览主题：
        /// - Layout：<b>独立副本</b>（<c>CreatePreviewCopy</c>，含全部字段与扩展数据，只缩放一次）；
        /// - Color / Thresholds / Backdrop：只读借用；
        /// - 字体：只借用（原主题负责释放，预览绝不释放）。
        /// </summary>
        /// <param name="original">原主题（不会被修改）。</param>
        /// <param name="dpiScale">预览使用的 DPI 比例。</param>
        public static Theme CreatePreviewTheme(Theme original, float dpiScale)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));

            float scale = dpiScale <= 0f ? 1.0f : dpiScale;

            var preview = new Theme
            {
                Name = original.Name,
                Version = original.Version,

                // ★ 独立布局副本：复制所有配置字段与扩展数据；已缩放的源不会被二次缩放
                Layout = original.Layout != null
                    ? original.Layout.CreatePreviewCopy(scale)
                    : new LayoutConfig(),

                // 只读借用（预览不会修改原主题）
                Font = original.Font,
                Thresholds = original.Thresholds,
                Color = original.Color,
                Backdrop = original.Backdrop,
                Rendering = original.Rendering != null
                    ? AppearanceThemeConfig.CloneRendering(original.Rendering)
                    : null,

                // 字体：只借用，不释放
                FontTitle = original.FontTitle,
                FontGroup = original.FontGroup,
                FontItem = original.FontItem,
                FontValue = original.FontValue,
                FontTaskbar = original.FontTaskbar
            };

            // 主题级扩展数据一并复制（未知键不丢）
            if (original.ExtensionData != null && original.ExtensionData.Count > 0)
            {
                foreach (KeyValuePair<string, System.Text.Json.JsonElement> kv in original.ExtensionData)
                    preview.ExtensionData[kv.Key] = kv.Value;
            }

            return preview;
        }

        /// <summary>
        /// 构造 Mock 数据用于预览（静态版本，供测试直接使用）。
        /// </summary>
        public static List<GroupLayoutInfo> CreateMockGroups()
        {
            var groups = new List<GroupLayoutInfo>();

            groups.Add(new GroupLayoutInfo("CPU", new()
            {
                new MetricItem { Key = "CPU.Load", Value = 23, DisplayValue = 23, Label = "CPU 使用率" },
                new MetricItem { Key = "CPU.Temp", Value = 65, DisplayValue = 65, Label = "CPU 温度" }
            }));

            groups.Add(new GroupLayoutInfo("GPU", new()
            {
                new MetricItem { Key = "GPU.Load", Value = 90, DisplayValue = 90, Label = "GPU 使用率" },
                new MetricItem { Key = "GPU.Temp", Value = 65, DisplayValue = 65, Label = "GPU 温度" },
                new MetricItem { Key = "GPU.VRAM", Value = 41, DisplayValue = 41, Label = "VRAM 占用" }
            }));

            groups.Add(new GroupLayoutInfo("MEM", new()
            {
                new MetricItem { Key = "MEM.Load", Value = 68, DisplayValue = 68, Label = "内存占用" }
            }));

            groups.Add(new GroupLayoutInfo("DISK", new()
            {
                new MetricItem { Key = "DISK.Read", Value = 1024 * 500, Label = "磁盘读取" },
                new MetricItem { Key = "DISK.Write", Value = 1024 * 3320, Label = "磁盘写入" }
            }));

            groups.Add(new GroupLayoutInfo("NET", new()
            {
                new MetricItem { Key = "NET.Up", Value = 1024 * 80, Label = "上传速度" },
                new MetricItem { Key = "NET.Down", Value = 1024 * 38000, Label = "下载速度" }
            }));

            return groups;
        }

        // ================================================================
        // 预览渲染（走正式渲染器）
        // ================================================================

        /// <summary>
        /// 把预览主题渲染到指定画布（静态、可测试）。
        /// <para>
        /// ★ 层次（v4 §3.3）：背景层 → 底纹（扫描线/网格）→ 内容 → 向内的窗口外框 + 辉光。
        /// </para>
        /// <para>
        /// ★ 全部由<b>正式渲染器</b>绘制：<see cref="AppearanceRuntime"/> 提供外观上下文，
        /// <c>UIRenderer</c> 的四参重载负责背景层、窗口底纹、组块装饰、进度条与外框 / 辉光。
        /// 预览**不再**自绘假扫描线 / 假网格，也**不再**使用"透明背景"变通。
        /// </para>
        /// </summary>
        /// <param name="g">目标画布。</param>
        /// <param name="groups">要渲染的分组（Mock 数据）。</param>
        /// <param name="previewTheme">预览主题（由 <see cref="CreatePreviewTheme"/> 生成）。</param>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="appearance">正式外观运行时（调用方持有并负责释放）。</param>
        /// <param name="dpiScale">实际 DPI 比例（仅在运行时还没有上下文时用于补一次快照）。</param>
        public static void RenderPreview(
            Graphics g,
            List<GroupLayoutInfo> groups,
            Theme previewTheme,
            Rectangle bounds,
            AppearanceRuntime appearance,
            float dpiScale = 1.0f)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (previewTheme == null) throw new ArgumentNullException(nameof(previewTheme));
            if (groups == null) throw new ArgumentNullException(nameof(groups));
            if (appearance == null) throw new ArgumentNullException(nameof(appearance));

            // 调用方还没建立上下文时补一次，保证一定走正式渲染层（而不是旧的三参入口）
            if (appearance.Options == null)
                appearance.Update(previewTheme, dpiScale <= 0f ? 1.0f : dpiScale, 1.0f, bounds);

            // 正式渲染入口：背景层 → 窗口底纹 → 组块 → 内容 → 向内的窗口外框 + 辉光
            UIRenderer.Render(g, groups, previewTheme, appearance);

            // 预览提示（玻璃用的是固定样例图，不是实时桌面）
            ThemeAppearanceOptions? options = appearance.Options;
            if (options != null && options.GlassRequested)
            {
                DrawPreviewNotice(g, bounds);
            }
        }

        /// <summary>
        /// 兼容入口（不传外观运行时）：自建运行时，用完即释放。
        /// <para>
        /// ★ 保留它是为了让**既有调用方与独立复现程序**（它们拿不到运行时实例）继续编译。
        ///   背景层只画**一次**（由正式渲染器画），因此画面与主窗口逐像素一致。
        /// </para>
        /// </summary>
        /// <param name="g">目标画布。</param>
        /// <param name="groups">要渲染的分组（Mock 数据）。</param>
        /// <param name="previewTheme">预览主题。</param>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        public static void RenderPreview(
            Graphics g,
            List<GroupLayoutInfo> groups,
            Theme previewTheme,
            Rectangle bounds)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (previewTheme == null) throw new ArgumentNullException(nameof(previewTheme));
            if (groups == null) throw new ArgumentNullException(nameof(groups));

            // 预览专用运行时：**只**注册固定样例图来源（PreviewFrameSource），
            // 它绝不访问真实桌面（IsReady 只看有没有样例图）。
            // ★ 保留它而不是"什么都不注册"：没有样例图时降级原因仍是
            //   "预览没有固定样例图…"（预览语境），而不是"未注册实时背景后端"
            //   （那是主窗口的语境，放在预览里会误导）。
            using var appearance = new AppearanceRuntime(new PreviewFrameSource(null));
            appearance.Update(previewTheme, 1.0f, 1.0f, bounds);

            RenderPreview(g, groups, previewTheme, bounds, appearance, 1.0f);
        }

        /// <summary>
        /// 兼容入口：自建外观运行时（用完即释放），并把背景层的诊断状态留在调用方给的渲染器上。
        /// <para>
        /// ★ 保留这个重载是为了兼容既有调用方（它自建运行时，调用方拿不到运行时实例）。
        /// 新代码请直接用带 <see cref="AppearanceRuntime"/> 的重载：运行时由调用方长期持有，
        /// 纹理 / 渐变槽缓存才能跨帧复用。
        /// </para>
        /// <para>
        /// ★ 背景层**只画一次**：正式渲染器（走固定样例图的帧来源）负责真正落到画布上。
        ///   调用方给的 <paramref name="backdrop"/> 只用来记录 <c>LastStatus</c> 诊断，
        ///   它被渲染到一张**离屏画布**上 —— 若直接落到目标画布，背景就会被画两遍，
        ///   而染色 / 噪点这类**半透明层叠加后会变深**，画面就不再与主窗口一致。
        /// </para>
        /// </summary>
        /// <param name="g">目标画布。</param>
        /// <param name="groups">要渲染的分组（Mock 数据）。</param>
        /// <param name="previewTheme">预览主题（由 <see cref="CreatePreviewTheme"/> 生成）。</param>
        /// <param name="bounds">完整客户区矩形（物理像素）。</param>
        /// <param name="backdrop">背景层渲染器（可空 = 不记录诊断）。</param>
        /// <param name="previewSource">预览固定样例图（玻璃用；可空）。</param>
        /// <param name="dpiScale">实际 DPI 比例（用于新尺寸的唯一一次换算）。</param>
        public static void RenderPreview(
            Graphics g,
            List<GroupLayoutInfo> groups,
            Theme previewTheme,
            Rectangle bounds,
            BackdropRenderer? backdrop = null,
            Image? previewSource = null,
            float dpiScale = 1.0f)
        {
            if (g == null) throw new ArgumentNullException(nameof(g));
            if (previewTheme == null) throw new ArgumentNullException(nameof(previewTheme));
            if (groups == null) throw new ArgumentNullException(nameof(groups));

            // 预览专用运行时：**只**注册固定样例图来源（PreviewFrameSource），
            // 它绝不访问真实桌面（IsReady 只看有没有样例图）。
            // ★ 之所以不改成 options.PreviewSource 路径：这个兼容重载允许
            //   调用方传 null 样例图，此时历史降级文案是"预览没有固定样例图…"；
            //   走 PreviewSource 路径会变成"未注册实时背景后端"（主窗口语境，预览里会误导）。
            // ★ 而 N2 的来源误报由 PreviewFrameSource.IsPreview = true 根治：
            //   来源报 PreviewFrame 而不是 LiveFrame。
            using var appearance = new AppearanceRuntime(new PreviewFrameSource(previewSource));
            appearance.Update(previewTheme, dpiScale <= 0f ? 1.0f : dpiScale, 1.0f, bounds);

            // 背景层诊断：渲染到**离屏画布**，只为拿到 LastStatus，不碰目标画布
            if (backdrop != null)
            {
                ThemeAppearanceOptions options = ThemeAppearanceOptions.Create(
                    previewTheme,
                    dpiScale <= 0f ? 1.0f : dpiScale,
                    1.0f,
                    bounds,
                    0,
                    null,
                    previewSource);

                using var offscreen = new Bitmap(
                    Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppPArgb);
                using (Graphics gOff = Graphics.FromImage(offscreen))
                {
                    backdrop.Render(gOff, options);
                }
            }

            RenderPreview(g, groups, previewTheme, bounds, appearance, dpiScale);
        }

        /// <summary>预览提示文字：玻璃用的是固定样例图。</summary>
        private static void DrawPreviewNotice(Graphics g, Rectangle bounds)
        {
            const string notice = "预览样例图 / Preview sample（非实时桌面捕获）";
            using var brush = new SolidBrush(Color.FromArgb(220, 255, 214, 0));
            using var font = new Font("Microsoft YaHei UI", 8f);
            g.DrawString(notice, font, brush, new PointF(bounds.Left + 4f, bounds.Top + 2f));
        }

        /// <summary>
        /// 构造 Mock 数据用于预览
        /// </summary>
        private void BuildMockData()
        {
            _groups.Clear();
            _groups.AddRange(CreateMockGroups());

            if (_layout != null)
                _layout.Build(_groups);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_gpuPreview?.Error is not null)
            {
                bool wasVisible = _gpuPreview.Visible;
                // 首次构造失败时 SetTheme 仍持有 raw 主效果主题；这里必须在
                // 同一修订上切到 fallback 派生配色，不能只隐藏 GPU 子控件。
                UseGdiFallbackTheme();
                _gpuPreview.Visible = false;
                AccessibleDescription = "GPU preview unavailable; showing GDI+ fallback preview.";
                if (wasVisible)
                {
                    NotifyGpuUnavailable();
                }
            }

            if (_theme == null || _layout == null || _disposed)
                return;

            if (_gpuPreview?.IsActive == true && _gpuPreview.Visible)
            {
                return;
            }

            e.Graphics.Clear(Color.FromArgb(245, 245, 245));

            Rectangle content = new Rectangle(
                Padding.Left,
                Padding.Top,
                Width - Padding.Left - Padding.Right,
                Height - Padding.Top - Padding.Bottom
            );

            if (content.Width <= 0 || content.Height <= 0) return;

            try
            {
                // 实际 DPI（窗口可能跨屏移动，每次绘制都重新取）
                float dpiScale = GetDpiScale();
                _dpiScale = dpiScale;

                // 布局只在主题换代 / DPI / 宽度变化时重建：BuildGdiPreview（或 BuildMockData）
                // 已经对同一主题构建过布局并写入 _groups，这里重复 Build 是每帧全量字体测量。
                bool layoutDirty = _themeChanged ||
                    Math.Abs(_layoutDpiScale - dpiScale) > 0.01f ||
                    _lastLayoutWidth != content.Width;
                if (layoutDirty && _theme is not null)
                {
                    _layoutDpiScale = dpiScale;
                    _lastLayoutWidth = content.Width;

                    // 生产同源：布局宽度 = 画布客户区像素宽（面板宽度随窗口变化）。
                    // UILayout 构建于自己的主题副本，必须带着新宽度重建，改 _theme 无效。
                    Theme layoutTheme = CreatePreviewTheme(_theme, dpiScale);
                    if (layoutTheme.Layout is not null)
                    {
                        layoutTheme.Layout.Width = Math.Max(1, content.Width);
                    }
                    _layout = new UILayout(layoutTheme);
                    int builtHeight = _layout.Build(_groups);

                    // 顶部保护：首组面板从布局 Y=Padding 起，面板描边/圆角向上溢出的
                    // 1-2px 会被位图上缘裁掉（"上半部分吞一点"）。所有组整体下移
                    // 2×DPI，位图与场景同高——GPU/GDI 共用同一份 groups，两路一致。
                    int topPad = (int)Math.Ceiling(2 * dpiScale);
                    if (topPad > 0)
                    {
                        foreach (GroupLayoutInfo group in _groups)
                        {
                            Rectangle b = group.Bounds;
                            group.Bounds = new Rectangle(b.X, b.Y + topPad, b.Width, b.Height);
                        }
                        builtHeight += topPad;
                    }

                    _lastLayoutHeight = Math.Max(1, builtHeight);
                }

                // 预览副本：主题或 DPI 变化时才重建（复用时运行时快照才能保持稳定）
                Theme previewTheme = GetPreviewTheme(dpiScale);

                int bmpH = Math.Max(1, _lastLayoutHeight);
                var requiredSize = new Size(Math.Max(1, content.Width), bmpH);

                // ★★★ 核心优化：复用 Bitmap 而非每次新建 ★★★
                if (_cachedBitmap == null || _lastSize != requiredSize)
                {
                    _cachedBitmap?.Dispose();
                    _cachedBitmap = new Bitmap(requiredSize.Width, requiredSize.Height);
                    _lastSize = requiredSize;
                }

                // ★ 固定样例图在构造运行时时就已交给它（options.PreviewSource 路径），
                //   这里不需要再做任何注入。

                var bounds = new Rectangle(0, 0, requiredSize.Width, requiredSize.Height);

                // 正式外观上下文：换代时主题代次自增，其余情况复用上一次快照
                if (_themeChanged)
                {
                    _appearance.OnThemeChanged(previewTheme, dpiScale, 1.0f, bounds);
                    _themeChanged = false;
                }
                else
                {
                    _appearance.Update(previewTheme, dpiScale, 1.0f, bounds);
                }

                using (Graphics gBmp = Graphics.FromImage(_cachedBitmap))
                {
                    gBmp.Clear(Color.Transparent); // 清空上次内容
                    gBmp.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    gBmp.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    gBmp.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                    // 正式渲染器：背景层 / 底纹 / 外框 / 辉光 / 进度条与主窗口同源
                    RenderPreview(
                        gBmp,
                        _groups,
                        previewTheme,
                        bounds,
                        _appearance,
                        dpiScale);
                }

                e.Graphics.DrawImageUnscaled(_cachedBitmap, content.X, content.Y);
            }
            catch (Exception ex)
            {
                e.Graphics.DrawString(
                    $"Render Error:\n{ex.Message}",
                    this.Font,
                    Brushes.Red,
                    new PointF(5, 5)
                );
            }
        }

        /// <summary>
        /// 取当前预览副本：主题或 DPI 变化时重建，其余情况复用
        /// （复用才能让运行时的外观快照保持稳定，不逐帧重建）。
        /// <para>
        /// ★ <see cref="SetTheme(Theme, ThemeEditTarget, PreviewShape)"/> 会把这里的缓存作废：编辑器是**就地改主题**的，
        ///   同一个主题对象的内容随时可能变，不能按引用复用旧副本。
        /// </para>
        /// </summary>
        private Theme GetPreviewTheme(float dpiScale)
        {
            if (_previewTheme != null
                && ReferenceEquals(_previewThemeSource, _theme)
                && _previewDpiScale.Equals(dpiScale))
            {
                return _previewTheme;
            }

            _previewThemeSource = _theme;
            _previewDpiScale = dpiScale;
            _previewTheme = CreatePreviewTheme(_theme!, dpiScale);

            return _previewTheme;
        }

        /// <summary>
        /// 预览固定样例图（静态图案）。
        /// ★ 预览路径<b>绝不</b>抓取桌面：这里只画一张固定图案。
        /// </summary>
        private Image? GetPreviewSample()
        {
            if (_previewSample == null)
                _previewSample = CreatePreviewSample();

            return _previewSample;
        }

        /// <summary>生成预览固定样例图（96×96 的静态图案）。</summary>
        public static Bitmap CreatePreviewSample()
        {
            var bmp = new Bitmap(96, 96);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(38, 42, 52));
                using var brush1 = new SolidBrush(Color.FromArgb(150, 90, 200, 255));
                using var brush2 = new SolidBrush(Color.FromArgb(150, 255, 120, 200));
                using var brush3 = new SolidBrush(Color.FromArgb(120, 120, 255, 160));
                g.FillEllipse(brush1, 6, 6, 44, 44);
                g.FillRectangle(brush2, 46, 40, 44, 44);
                g.FillEllipse(brush3, 30, 30, 40, 40);
            }
            return bmp;
        }

        /// <summary>
        /// 添加释放方法
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;

                _cachedBitmap?.Dispose();
                _cachedBitmap = null;

                // 只释放预览自己创建的位图；原主题的字体是借用的，绝不释放
                _previewSample?.Dispose();
                _previewSample = null;

                // 正式外观运行时：纹理缓存、画刷、噪点位图都在这里释放
                _appearance.Dispose();
                _gpuPreview?.Dispose();
                _gpuPreview = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (_layout != null)
            {
                _layout.Build(_groups);

                // 尺寸变化：槽位尺寸不再复用，让各层缓存全量失效
                if (!_disposed)
                    _appearance.OnSizeChanged();

                if (_gpuPreview is not null)
                {
                    _gpuPreview.Bounds = new Rectangle(
                        Padding.Left,
                        Padding.Top,
                        Math.Max(1, Width - Padding.Horizontal),
                        Math.Max(1, Height - Padding.Vertical));
                }

                Invalidate();
            }
        }

        /// <summary>
        /// 取当前控件的真实 DPI 比例。
        /// </summary>
        private float GetDpiScale()
        {
            try
            {
                int dpi = DeviceDpi;
                if (dpi > 0) return dpi / 96f;
            }
            catch
            {
                // 句柄尚未创建时 DeviceDpi 可能不可用，退回 Graphics 查询
            }

            using (Graphics g = CreateGraphics())
            {
                if (g != null) return g.DpiX / 96f;
            }

            return 1.0f;
        }
    }
}
