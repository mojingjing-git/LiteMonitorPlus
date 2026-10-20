using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Decor;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 窗口轮廓策略：把"主题的外框样式"翻译成**窗口级形状**。
    ///
    /// ★ 与绘制层的关系：A07 负责把外框**画**在客户区里（内侧描边带），
    ///   本枚举负责把同一个几何**变成窗口本身的形状**（Region + DWM 角偏好）。
    ///   两者必须使用同一套几何（<see cref="DecorGeometry"/>）与同一组物理像素参数，
    ///   否则窗口轮廓与画出来的边框对不上。
    /// </summary>
    public enum WindowShapeStrategy
    {
        /// <summary>
        /// 无自定义形状：Region 交回系统（null），DWM 角偏好 = ROUND(2)。
        /// 这是**旧路径**的基线状态，与既有 <c>MainFormWinHelper.ApplyRoundedCorners</c> 在
        /// Win11 上的行为完全一致（Region=null + DWMWCP_ROUND），保证旧主题画面不变。
        /// </summary>
        SystemRounded = 0,

        /// <summary>
        /// 切角：Region = 切角路径，DWM 角偏好 = DONOTROUND(1)。
        /// ★ DONOTROUND(1) 才是"禁用圆角"；0 是 DWMWCP_DEFAULT（系统默认，不是禁用）。
        /// </summary>
        Chamfered = 1,

        /// <summary>
        /// 圆角：Region = 圆角路径（与画框半径一致），DWM 角偏好 = ROUND(2)。
        /// 半径为 0 时不设 Region，交给系统圆角。
        /// </summary>
        Rounded = 2
    }

    /// <summary>
    /// 一次窗口轮廓应用的结果（供 A13 接线自检与自证探针断言）。
    /// </summary>
    public readonly struct WindowShapeResult
    {
        public WindowShapeResult(
            bool applied,
            bool skipped,
            WindowShapeStrategy strategy,
            int cornerPreference,
            bool regionApplied,
            Rectangle clientBounds,
            int cornerLengthPx,
            int radiusPx,
            int insetPx,
            string note)
        {
            Applied = applied;
            Skipped = skipped;
            Strategy = strategy;
            CornerPreference = cornerPreference;
            RegionApplied = regionApplied;
            ClientBounds = clientBounds;
            CornerLengthPx = cornerLengthPx;
            RadiusPx = radiusPx;
            InsetPx = insetPx;
            Note = note ?? string.Empty;
        }

        /// <summary>是否真的应用了窗口级属性（DWM / Region）。</summary>
        public bool Applied { get; }

        /// <summary>是否因为句柄不可用等原因被跳过（跳过不是失败）。</summary>
        public bool Skipped { get; }

        /// <summary>本次采用的轮廓策略。</summary>
        public WindowShapeStrategy Strategy { get; }

        /// <summary>实际传给 DWM 的角偏好值（未调用时为 -1）。</summary>
        public int CornerPreference { get; }

        /// <summary>本次是否给窗体设置了 Region。</summary>
        public bool RegionApplied { get; }

        /// <summary>本次使用的完整客户区矩形（物理像素）。</summary>
        public Rectangle ClientBounds { get; }

        /// <summary>本次使用的切角边长（物理像素，未二次缩放）。</summary>
        public int CornerLengthPx { get; }

        /// <summary>本次使用的圆角半径（物理像素，未二次缩放）。</summary>
        public int RadiusPx { get; }

        /// <summary>本次使用的向内缩进（物理像素）。</summary>
        public int InsetPx { get; }

        /// <summary>诊断说明。</summary>
        public string Note { get; }
    }

    /// <summary>
    /// 窗口轮廓控制器（A09）的运行时计数（只读，供自证探针断言"没有泄漏"）。
    /// </summary>
    public sealed class WindowAppearanceDiagnostics
    {
        /// <summary>Apply / Reapply 被调用的总次数。</summary>
        public int ApplyCount { get; internal set; }

        /// <summary>DWM 角偏好写入次数。</summary>
        public int DwmCornerCalls { get; internal set; }

        /// <summary>DWM 角偏好写入失败（HRESULT != 0）次数。</summary>
        public int DwmCornerFailures { get; internal set; }

        /// <summary>最后一次 DWM 调用的 HRESULT（0 = 成功）。</summary>
        public int LastDwmHr { get; internal set; }

        /// <summary>DWM 边框色写入次数（与既有 helper 行为保持一致）。</summary>
        public int DwmBorderCalls { get; internal set; }

        /// <summary>创建的 GraphicsPath 数量（每个都在本次调用内释放）。</summary>
        public int PathsCreated { get; internal set; }

        /// <summary>已释放的 GraphicsPath 数量（应与 <see cref="PathsCreated"/> 相等）。</summary>
        public int PathsDisposed { get; internal set; }

        /// <summary>创建的 Region 数量（全部移交给 WinForms 的 Control 持有）。</summary>
        public int RegionsCreated { get; internal set; }

        /// <summary>
        /// 本组件主动释放的 Region 数量。
        /// ★ 恒为 0：赋给 <c>Control.Region</c> 的实例由 WinForms 持有并释放，
        ///   本组件不重复释放（重复释放会造成"赋值的对象被提前释放"的悬空引用）。
        /// </summary>
        public int RegionsDisposedByController { get; internal set; }

        /// <summary>因窗口句柄不可用而跳过的次数。</summary>
        public int SkippedNoHandle { get; internal set; }

        /// <summary>因非 UI 线程调用而被拒绝的次数。</summary>
        public int OffThreadRejections { get; internal set; }

        /// <summary>最后一次读取到的窗口实际 DPI（用于核对 options 里的 DPI 是否过期）。</summary>
        public int LastWindowDeviceDpi { get; internal set; }
    }

    /// <summary>
    /// 窗口外观控制器（A09）：把主题的外框样式翻译成**窗口级形状**。
    ///
    /// ★ 职责边界（v4 §3.4）：
    /// 1. 只管"形状"：DWM 角偏好 + <see cref="Control.Region"/>。
    ///    <b>不碰</b>置顶（TopMost）、鼠标穿透（ClickThrough）、淡入（Opacity），
    ///    也不绑定任何事件（Resize / HandleCreated 等的接线由 A13 负责，且只保留一处 Resize 绑定）。
    /// 2. 消费统一几何：路径一律由 <see cref="DecorGeometry"/> 构造（不另造一套路径算法），
    ///    参数一律取 <see cref="ThemeAppearanceOptions"/> 的**物理像素**（FrameLengthPx），
    ///    <b>绝不二次缩放</b>。
    /// 3. 完整客户区 bounds 一律取 <c>options.ClientBounds</c>（不用 VisibleClipBounds、不用 (-5,-5) 填充矩形）。
    /// 4. 不消费 <c>BorderColor</c>（那是绘制层的事；消费颜色前必须先判 FrameEnabled）。
    ///
    /// ★ Region / 路径所有权（关键）：
    /// | 对象 | 创建者 | 释放者 | 时机 |
    /// |---|---|---|---|
    /// | GraphicsPath | 本组件（<see cref="DecorGeometry.BuildFramePath"/>） | **本组件** | 构造出 Region 后立即释放（Region 会复制路径内容） |
    /// | Region | 本组件 | **WinForms（Control）** | 赋给 <c>Control.Region</c> 后由 Control 持有；替换 / 置空 / 控件销毁时由 Control 释放 |
    /// | 窗口 HRGN | WinForms（SetWindowRgn） | 系统 / 窗口 | 窗口销毁或换 Region 时由系统释放 |
    ///
    /// ⇒ 本组件**绝不** Dispose 赋给 <c>Control.Region</c> 的实例。
    ///   （既有 <c>MainFormWinHelper.ApplyRoundedCorners</c> 里 <c>_form.Region?.Dispose()</c>
    ///   之后再赋新值的写法，会在窗体仍引用它时把它释放掉，属于悬空引用隐患。）
    ///
    /// ★ 线程：所有 Win32 / Region 操作必须在**构造本组件的那个线程**（UI 线程）上执行，
    ///   否则抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public sealed class WindowAppearanceController : IDisposable
    {
        // ============================================================
        // DWM 常量（公开，便于 A13 / 自证探针核对取值）
        // ============================================================

        /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE（DwmSetWindowAttribute 的属性号）。</summary>
        public const int DwmwaWindowCornerPreference = 33;

        /// <summary>DWMWA_BORDER_COLOR（DwmSetWindowAttribute 的属性号）。</summary>
        public const int DwmwaBorderColor = 34;

        /// <summary>DWMWCP_DEFAULT（0）：系统默认角，<b>不是</b>"禁用圆角"。</summary>
        public const int DwmcpDefault = 0;

        /// <summary>DWMWCP_DONOTROUND（1）：<b>禁用</b>系统圆角——切角必须用这个值。</summary>
        public const int DwmcpDoNotRound = 1;

        /// <summary>DWMWCP_ROUND（2）：系统圆角——回到圆角时必须恢复这个值。</summary>
        public const int DwmcpRound = 2;

        /// <summary>DWMWCP_ROUNDSMALL（3）：小圆角（当前未使用）。</summary>
        public const int DwmcpRoundSmall = 3;

        /// <summary>DWMWA_COLOR_NONE：去掉 DWM 边框色（与既有 helper 行为一致）。</summary>
        private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

        // ============================================================
        // 字段
        // ============================================================

        private readonly Form _form;
        private readonly int _uiThreadId;
        private bool _disposed;

        private ThemeAppearanceOptions? _lastOptions;
        private int _lastFrameStyle = ThemeAppearanceOptions.FrameStyleNone;
        private int _lastCornerLengthPx;
        private int _lastRadiusPx;
        private int _lastInsetPx;
        private bool _regionAssigned;

        // ============================================================
        // 公开状态
        // ============================================================

        /// <summary>运行时计数（只读）。</summary>
        public WindowAppearanceDiagnostics Diagnostics { get; } = new WindowAppearanceDiagnostics();

        /// <summary>目标窗体。</summary>
        public Form Form => _form;

        /// <summary>构造线程的托管线程 ID（即 UI 线程）。</summary>
        public int UiThreadId => _uiThreadId;

        /// <summary>最近一次采用的轮廓策略。</summary>
        public WindowShapeStrategy LastStrategy { get; private set; } = WindowShapeStrategy.SystemRounded;

        /// <summary>最近一次实际传给 DWM 的角偏好值（未调用时为 -1）。</summary>
        public int LastCornerPreference { get; private set; } = -1;

        /// <summary>最近一次使用的完整客户区矩形（物理像素）。</summary>
        public Rectangle LastClientBounds { get; private set; } = Rectangle.Empty;

        /// <summary>最近一次使用的切角边长（物理像素，未二次缩放）。</summary>
        public int LastCornerLengthPx { get; private set; }

        /// <summary>最近一次使用的圆角半径（物理像素，未二次缩放）。</summary>
        public int LastRadiusPx { get; private set; }

        /// <summary>最近一次使用的向内缩进（物理像素）。</summary>
        public int LastInsetPx { get; private set; }

        /// <summary>当前窗体上是否挂着本组件设置的 Region。</summary>
        public bool HasAppliedRegion => _regionAssigned;

        /// <summary>
        /// 旧路径（FrameStyle=0）在**无 DWM 圆角能力**的系统（Win10）上使用的圆角半径（像素）。
        /// <para>
        /// -1（默认）= 自动读取 <c>ThemeManager.Current.Layout.CornerRadius</c>，与既有
        /// <c>MainFormWinHelper.ApplyRoundedCorners</c> 的取值完全一致（保证旧主题观感不变）。
        /// </para>
        /// <para>
        /// ≥0 = 显式指定（自证探针用它避免读取用户配置目录）。
        /// </para>
        /// </summary>
        public int LegacyCornerRadiusPx { get; set; } = -1;

        /// <summary>
        /// DWM 角偏好能力覆盖（仅供自证探针模拟旧系统）。
        /// <para>null（默认）= 按系统版本自动判断（Windows 11 22H2 / build 22000 起支持）。</para>
        /// </summary>
        public bool? DwmCornerSupportOverride { get; internal set; }

        /// <summary>
        /// DWM 属性写入钩子（仅供自证探针注入断言用）。
        /// <para>
        /// 注入后<b>所有</b> DWM 属性写入都走钩子（不再调用原生实现），
        /// 因此探针可以断言"实际传给 DwmSetWindowAttribute 的值"。
        /// 生产环境保持 null 即可。
        /// </para>
        /// </summary>
        public delegate int DwmSetWindowAttributeHook(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>DWM 属性写入钩子（null = 走原生实现）。</summary>
        internal DwmSetWindowAttributeHook? DwmSetWindowAttributeInterceptor { get; set; }

        // ============================================================
        // 构造
        // ============================================================

        /// <summary>
        /// 构造窗口外观控制器。
        /// </summary>
        /// <param name="form">目标窗体（必须在 UI 线程上构造本组件）。</param>
        public WindowAppearanceController(Form form)
        {
            if (form == null) throw new ArgumentNullException(nameof(form));
            _form = form;
            _uiThreadId = Environment.CurrentManagedThreadId;
        }

        // ============================================================
        // 统一入口
        // ============================================================

        /// <summary>
        /// 按主题外观应用窗口轮廓（**统一入口**，A13 在初始化 / 换肤 / DPI 变化时调用）。
        /// </summary>
        /// <param name="options">外观快照（物理像素已由 options 换算好，本方法不再缩放）。</param>
        /// <param name="cornerRadiusPx">
        /// 圆角半径（物理像素）。&lt;0 表示用 <c>options.FrameLengthPx</c>。
        /// ★ 必须与 A07 绘制外框时使用的半径一致，否则窗口轮廓与画出来的边框对不上。
        /// </param>
        /// <param name="frameOuterInsetPx">
        /// 外轮廓相对客户区向内缩进（物理像素，默认 0）。
        /// ★ 建议保持 0：Region 会**裁掉**客户区，超出部分的内容会被切掉。
        /// </param>
        public WindowShapeResult Apply(ThemeAppearanceOptions options, int cornerRadiusPx = -1, int frameOuterInsetPx = 0)
        {
            EnsureUiThread(nameof(Apply));
            ThrowIfDisposed();
            if (options == null) throw new ArgumentNullException(nameof(options));

            _lastOptions = options;
            _lastFrameStyle = options.FrameStyle;

            // ★ 物理像素只用一次：options.FrameLengthPx 已经是物理像素，这里直接用，绝不乘缩放。
            _lastCornerLengthPx = Math.Max(0, options.FrameLengthPx);
            _lastRadiusPx = Math.Max(0, cornerRadiusPx < 0 ? options.FrameLengthPx : cornerRadiusPx);
            _lastInsetPx = Math.Max(0, frameOuterInsetPx);

            return ApplyShape(
                _lastFrameStyle,
                NormalizeToClientOrigin(options.ClientBounds),
                _lastCornerLengthPx,
                _lastRadiusPx,
                _lastInsetPx,
                options.DeviceDpi);
        }

        /// <summary>
        /// 用上次的主题外观**重新应用**窗口轮廓（尺寸 / DPI / 横竖变化后由 A13 调用）。
        /// <para>
        /// ★ 本方法<b>不绑定</b>任何事件：A13 只保留**一处** Resize 绑定并在其中调用它。
        /// </para>
        /// <para>
        /// 尺寸变了 → 轮廓必须跟着变，因此这里用窗体<b>当前</b>客户区重新计算 bounds，
        /// 而样式参数（切角边长 / 半径 / 缩进）沿用上次 Apply 的值。
        /// </para>
        /// </summary>
        public WindowShapeResult Reapply()
        {
            EnsureUiThread(nameof(Reapply));
            ThrowIfDisposed();

            if (_lastOptions == null)
            {
                return new WindowShapeResult(false, true, WindowShapeStrategy.SystemRounded, -1, false,
                    Rectangle.Empty, 0, 0, 0, "尚未应用过主题轮廓，无需重算");
            }

            Rectangle bounds = NormalizeToClientOrigin(_form.ClientRectangle);
            return ApplyShape(_lastFrameStyle, bounds, _lastCornerLengthPx, _lastRadiusPx, _lastInsetPx, _lastOptions.DeviceDpi);
        }

        /// <summary>
        /// 句柄重建后调用：旧的窗口 Region 已随旧窗口被系统销毁，必须重新应用一次。
        /// </summary>
        public void OnHandleCreated()
        {
            EnsureUiThread(nameof(OnHandleCreated));
            if (_disposed) return;
            Reapply();
        }

        /// <summary>
        /// 句柄销毁后调用：清掉窗口级状态，之后<b>不再</b>调用任何 Win32。
        /// <para>
        /// ★ 这里把 <c>Control.Region</c> 置空：置空由 WinForms 释放 Region 实例，
        ///   并且句柄重建后不会把"上一次尺寸的旧 Region"再套到新窗口上。
        /// </para>
        /// </summary>
        public void OnHandleDestroyed()
        {
            EnsureUiThread(nameof(OnHandleDestroyed));
            if (_disposed) return;

            _regionAssigned = false;
            try
            {
                if (!_form.IsDisposed) _form.Region = null;   // 句柄已销毁：不会触发 Win32 调用
            }
            catch
            {
                // 句柄销毁过程中不允许抛异常
            }
        }

        /// <summary>
        /// 交回系统（无自定义形状）：Region 置空 + DWM 角偏好恢复 ROUND(2)。
        /// </summary>
        public WindowShapeResult Reset()
        {
            EnsureUiThread(nameof(Reset));
            ThrowIfDisposed();

            _lastOptions = null;
            _lastFrameStyle = ThemeAppearanceOptions.FrameStyleNone;
            _lastCornerLengthPx = 0;
            _lastRadiusPx = 0;
            _lastInsetPx = 0;

            return ApplyShape(ThemeAppearanceOptions.FrameStyleNone, NormalizeToClientOrigin(_form.ClientRectangle), 0, 0, 0, 0);
        }

        /// <summary>
        /// 读取窗口当前的 DWM 角偏好（诊断用；旧系统不支持时返回 false）。
        /// </summary>
        public bool TryGetCurrentCornerPreference(out int value)
        {
            value = -1;
            try
            {
                if (_form.IsDisposed || !_form.IsHandleCreated) return false;
                int raw = 0;
                int hr = DwmGetWindowAttribute(_form.Handle, DwmwaWindowCornerPreference, out raw, sizeof(int));
                if (hr != 0) return false;
                value = raw;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 释放本组件持有的状态（Region 的所有权在 WinForms，这里通过置空让 WinForms 释放它）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (!_form.IsDisposed) _form.Region = null;
            }
            catch
            {
                // 退出阶段不允许抛异常
            }

            _regionAssigned = false;
            _lastOptions = null;
        }

        // ============================================================
        // 核心：把样式翻译成窗口形状
        // ============================================================

        private WindowShapeResult ApplyShape(int frameStyle, Rectangle bounds, int cornerLengthPx, int radiusPx, int insetPx, int deviceDpi)
        {
            Diagnostics.ApplyCount++;

            bool dwmSupported = DwmCornerSupport;
            WindowShapeStrategy strategy = ResolveStrategy(frameStyle, dwmSupported);
            int preference = ResolveCornerPreference(strategy);

            LastStrategy = strategy;
            LastCornerPreference = -1;
            LastClientBounds = bounds;
            LastCornerLengthPx = cornerLengthPx;
            LastRadiusPx = radiusPx;
            LastInsetPx = insetPx;
            if (_form.IsHandleCreated) Diagnostics.LastWindowDeviceDpi = _form.DeviceDpi;

            // 句柄不可用 → 禁止任何 Win32 调用（也不产生非法 Region）
            if (_form.IsDisposed || !_form.IsHandleCreated)
            {
                Diagnostics.SkippedNoHandle++;
                ReleaseRegion();
                return new WindowShapeResult(false, true, strategy, -1, false, bounds,
                    cornerLengthPx, radiusPx, insetPx, "窗口句柄不可用，已跳过全部 Win32 调用");
            }

            // ① 先写 DWM 角偏好：
            //    切角必须先 DONOTROUND(1)，否则系统圆角会盖在切角上；
            //    回到圆角时这里写回 ROUND(2)。
            int hr = SetCornerPreference(preference);
            LastCornerPreference = preference;

            // ② 再处理 Region（形状）
            bool regionApplied = false;
            string note;
            switch (strategy)
            {
                case WindowShapeStrategy.Chamfered:
                    regionApplied = ApplyRegionPath(bounds, frameStyle, cornerLengthPx, radiusPx, insetPx);
                    note = "切角：Region=切角路径，DWM=DONOTROUND(1)";
                    break;

                case WindowShapeStrategy.Rounded:
                    if (frameStyle == ThemeAppearanceOptions.FrameStyleNone)
                    {
                        // 旧路径（无 DWM 圆角能力的系统）：沿用既有 helper 的圆角区域
                        regionApplied = ApplyLegacyRoundedRegion(bounds);
                        note = "旧路径圆角：Region=圆角路径（沿用既有 CornerRadius）";
                    }
                    else
                    {
                        regionApplied = ApplyRegionPath(bounds, frameStyle, cornerLengthPx, radiusPx, insetPx);
                        note = radiusPx > 0
                            ? "圆角：Region=圆角路径，DWM=ROUND(2)"
                            : "圆角半径为 0：不设 Region，交给系统圆角，DWM=ROUND(2)";
                    }
                    break;

                default:
                    // 无框 / 旧路径基线：Region 交回系统
                    ReleaseRegion();
                    note = "无自定义形状：Region 交回系统，DWM=ROUND(2)";
                    break;
            }

            if (hr != 0) note += $"（DWM 角偏好写入失败 HRESULT=0x{hr:X8}）";

            return new WindowShapeResult(true, false, strategy, preference, regionApplied, bounds,
                cornerLengthPx, radiusPx, insetPx, note);
        }

        /// <summary>
        /// 样式 → 轮廓策略。
        /// <list type="bullet">
        /// <item>1=切角 / 3=全息：窗口轮廓 = 切角（外层是切角，与画框一致）。</item>
        /// <item>2=圆角描边：有 DWM 圆角能力时用系统圆角 + 同半径 Region；否则用圆角 Region。</item>
        /// <item>0=无 / 旧路径：系统基线（Region 交回系统 + ROUND(2)），保证旧主题观感不变。</item>
        /// </list>
        /// </summary>
        private WindowShapeStrategy ResolveStrategy(int frameStyle, bool dwmSupported)
        {
            switch (frameStyle)
            {
                case ThemeAppearanceOptions.FrameStyleChamfer:
                case ThemeAppearanceOptions.FrameStyleHolographic:
                    return WindowShapeStrategy.Chamfered;

                case ThemeAppearanceOptions.FrameStyleRoundedStroke:
                    // 有 DWM 圆角能力：系统圆角 + 同半径 Region；没有：只能靠圆角 Region
                    return WindowShapeStrategy.Rounded;

                case ThemeAppearanceOptions.FrameStyleNone:
                default:
                    if (dwmSupported) return WindowShapeStrategy.SystemRounded;
                    return ResolveLegacyRadiusPx() > 0 ? WindowShapeStrategy.Rounded : WindowShapeStrategy.SystemRounded;
            }
        }

        /// <summary>策略 → DWM 角偏好值（★ 切角必须是 DONOTROUND=1，不能是 DEFAULT=0）。</summary>
        private static int ResolveCornerPreference(WindowShapeStrategy strategy)
        {
            switch (strategy)
            {
                case WindowShapeStrategy.Chamfered:
                    return DwmcpDoNotRound;    // 1
                case WindowShapeStrategy.Rounded:
                case WindowShapeStrategy.SystemRounded:
                default:
                    return DwmcpRound;         // 2
            }
        }

        // ============================================================
        // Region / 路径
        // ============================================================

        /// <summary>
        /// 用统一几何构造轮廓路径并挂到窗体上。
        /// <para>
        /// ★ 路径与 Region 的所有权：
        /// 路径由本组件创建并在构造出 Region 后**立即释放**（Region 会复制路径内容）；
        /// Region 移交给 WinForms（<c>Control.Region</c>）持有，<b>本组件不释放它</b>。
        /// </para>
        /// </summary>
        private bool ApplyRegionPath(Rectangle bounds, int frameStyle, int cornerLengthPx, int radiusPx, int insetPx)
        {
            // 无效尺寸 / 无效半径 → 不产生非法 Region
            if (bounds.Width <= 0 || bounds.Height <= 0) { ReleaseRegion(); return false; }
            if (frameStyle == ThemeAppearanceOptions.FrameStyleRoundedStroke && radiusPx <= 0) { ReleaseRegion(); return false; }

            GraphicsPath path = DecorGeometry.BuildFramePath(bounds, frameStyle, cornerLengthPx, radiusPx, insetPx);
            Diagnostics.PathsCreated++;
            try
            {
                // 空路径绝不能变成 Region：空 Region 会让整个窗口不可见
                if (path.PointCount == 0) { ReleaseRegion(); return false; }

                Region region = new Region(path);   // ★ Region 复制路径内容
                Diagnostics.RegionsCreated++;

                // ★ 赋给 Control.Region 之后，所有权归 WinForms：
                //   旧 Region 由 WinForms 在这里 Dispose，本组件不碰它。
                _form.Region = region;
                _regionAssigned = true;
                return true;
            }
            finally
            {
                // ★ 路径归本组件：Region 已复制内容，可以立即释放
                path.Dispose();
                Diagnostics.PathsDisposed++;
            }
        }

        /// <summary>旧路径（无 DWM 圆角能力时）的圆角区域：沿用既有 helper 的 CornerRadius 取值。</summary>
        private bool ApplyLegacyRoundedRegion(Rectangle bounds)
        {
            int radius = ResolveLegacyRadiusPx();
            if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0) { ReleaseRegion(); return false; }

            GraphicsPath path = DecorGeometry.BuildRoundedPath(bounds, radius);
            Diagnostics.PathsCreated++;
            try
            {
                if (path.PointCount == 0) { ReleaseRegion(); return false; }

                Region region = new Region(path);
                Diagnostics.RegionsCreated++;
                _form.Region = region;
                _regionAssigned = true;
                return true;
            }
            finally
            {
                path.Dispose();
                Diagnostics.PathsDisposed++;
            }
        }

        /// <summary>
        /// 摘掉窗体上的 Region。
        /// <para>
        /// ★ 置空后由 WinForms 释放 Region 实例并清掉窗口 HRGN；
        ///   本组件不 Dispose 它（重复释放会造成悬空引用）。
        /// </para>
        /// </summary>
        private void ReleaseRegion()
        {
            if (!_regionAssigned) return;
            _regionAssigned = false;
            if (_form.IsDisposed) return;
            _form.Region = null;
        }

        // ============================================================
        // DWM
        // ============================================================

        private int SetCornerPreference(int preference)
        {
            // 旧系统没有 DWMWA_WINDOW_CORNER_PREFERENCE：不调用（避免无意义的失败 HRESULT）
            if (!DwmCornerSupport) return 0;

            int value = preference;
            int hr = DwmSetWindowAttributeInterceptor != null
                ? DwmSetWindowAttributeInterceptor(_form.Handle, DwmwaWindowCornerPreference, ref value, sizeof(int))
                : DwmSetWindowAttribute(_form.Handle, DwmwaWindowCornerPreference, ref value, sizeof(int));

            Diagnostics.DwmCornerCalls++;
            Diagnostics.LastDwmHr = hr;
            if (hr != 0) Diagnostics.DwmCornerFailures++;

            // 与既有 helper 一致：去掉 DWM 边框色（旧主题观感不变）
            int borderColor = DwmwaColorNone;
            if (DwmSetWindowAttributeInterceptor != null)
            {
                DwmSetWindowAttributeInterceptor(_form.Handle, DwmwaBorderColor, ref borderColor, sizeof(int));
            }
            else
            {
                DwmSetWindowAttribute(_form.Handle, DwmwaBorderColor, ref borderColor, sizeof(int));
            }
            Diagnostics.DwmBorderCalls++;

            return hr;
        }

        /// <summary>当前系统是否支持 DWM 角偏好（Windows 11 22H2 / build 22000 起）。</summary>
        public bool DwmCornerSupport => DwmCornerSupportOverride ?? DetectDwmCornerSupport();

        private static bool DetectDwmCornerSupport()
        {
            Version v = Environment.OSVersion.Version;
            return v.Major > 10 || (v.Major == 10 && v.Build >= 22000);
        }

        // ============================================================
        // 辅助
        // ============================================================

        /// <summary>
        /// 把 bounds 归一到客户区原点：<c>Control.Region</c> 用的是客户区坐标，
        /// 而 options.ClientBounds 的原点对 Form 恒为 (0,0)；这里显式归一，
        /// 避免将来 bounds 带偏移时把整个窗口形状平移。
        /// </summary>
        private static Rectangle NormalizeToClientOrigin(Rectangle bounds)
        {
            return new Rectangle(0, 0, bounds.Width, bounds.Height);
        }

        private int ResolveLegacyRadiusPx()
        {
            if (LegacyCornerRadiusPx >= 0) return LegacyCornerRadiusPx;
            try
            {
                Theme t = ThemeManager.Current;
                return t?.Layout?.CornerRadius ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private void EnsureUiThread(string operation)
        {
            int current = Environment.CurrentManagedThreadId;
            if (current == _uiThreadId) return;

            Diagnostics.OffThreadRejections++;
            throw new InvalidOperationException(
                $"WindowAppearanceController.{operation} 必须在 UI 线程（构造线程 {_uiThreadId}）调用，当前线程 {current}。" +
                " 窗口 Region / DWM 属性只能在 UI 线程操作。");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowAppearanceController));
        }

        // ============================================================
        // Win32
        // ============================================================

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);
    }
}
