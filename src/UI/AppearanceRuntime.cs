using System;
using System.Drawing;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices.Backdrop;
using LiteMonitor.src.UI.Decor;

namespace LiteMonitor
{
    /// <summary>
    /// 外观运行时（A13 接线引入）：把"当前这一帧的外观上下文"与各个外观渲染器
    /// （背景层 / 装饰层 / 进度条）绑在一起，作为**唯一**的接线宿主。
    ///
    /// ★ 合同要点：
    /// 1. <b>每个挂载点只有一处</b>：UIRenderer / HorizontalRenderer 只从这里取渲染器，
    ///    不各自 new 一份，也不在两处重复建同一层。
    /// 2. <b>共享一个 <see cref="DecorResourceHost"/></b>：装饰层与进度条层共用同一份
    ///    纹理/渐变缓存（条目上限 128、位图上限 16 MiB 是**共享**预算，不是各算一份）。
    /// 3. <b>options 只在必要时重建</b>：主题 / DPI / 用户缩放 / 客户区尺寸都没变时
    ///    复用上一次的快照 → 热路径不产生额外托管分配。
    /// 4. <b>归一化只作用于运行时副本</b>：本类不修改传入的 <see cref="Theme"/>，也绝不回写用户 JSON。
    /// 5. <b>实际 DPI 由调用方传入</b>（当前窗口的 DeviceDpi / 96），
    ///    禁止用全局 <c>UIUtils.ScaleFactor</c> 代替。
    /// </summary>
    public sealed class AppearanceRuntime : IDisposable
    {
        // ---- 资源与渲染器（生命周期与本对象一致）----
        private readonly DecorResourceHost _host;
        private readonly DecorRenderer _decor;
        private readonly BarRenderer _bars;
        private readonly BackdropRenderer _backdrop;

        // ---- 当前上下文 ----
        private ThemeAppearanceOptions? _options;
        private Theme? _lastTheme;
        private float _lastDpiScale = -1f;
        private float _lastUserScale = -1f;
        private Rectangle _lastBounds;
        private int _themeGeneration;
        private bool _disposed;

        /// <summary>
        /// 创建外观运行时。
        /// </summary>
        /// <param name="frameSource">
        /// 实时背景帧来源（可空）。为 null 时，主题若请求 glass 会按 A08 的降级链
        /// 明确回退到 gradient → solid，并给出可诊断原因。**A15 接入真实后端后在此传入。**
        /// </param>
        public AppearanceRuntime(IBackdropFrameSource? frameSource = null)
        {
            _host = new DecorResourceHost();
            // 两个渲染器共用同一个宿主 → 缓存与配额是共享的
            _decor = new DecorRenderer(_host);
            _bars = new BarRenderer(_host);
            _backdrop = new BackdropRenderer(frameSource);
        }

        /// <summary>当前外观快照（未初始化时为 null）。</summary>
        public ThemeAppearanceOptions? Options => _options;

        /// <summary>装饰层渲染器（扫描线 / 网格 / 组框 / 窗口边框与辉光）。</summary>
        public DecorRenderer Decor => _decor;

        /// <summary>进度条渲染器（旧条 / 渐变条 / 纹理条）。</summary>
        public BarRenderer Bars => _bars;

        /// <summary>背景层渲染器（solid / gradient / glass 降级）。</summary>
        public BackdropRenderer Backdrop => _backdrop;

        /// <summary>主题代次：换肤时自增，供各层缓存失效判断。</summary>
        public int ThemeGeneration => _themeGeneration;

        /// <summary>是否已经初始化过上下文。</summary>
        public bool HasContext => _options != null;

        /// <summary>
        /// 更新当前帧的外观上下文。
        /// <para>
        /// 主题 / DPI / 用户缩放 / 客户区尺寸<b>全都未变</b>时直接复用上一次的快照
        /// （不产生新分配）；任一项变了才重建。
        /// </para>
        /// </summary>
        /// <param name="theme">当前主题（只读，不会被修改）。</param>
        /// <param name="dpiScale">当前窗口的实际 DPI 比例（DeviceDpi / 96）。<b>不要传全局 UIUtils.ScaleFactor。</b></param>
        /// <param name="userScale">用户缩放倍率。</param>
        /// <param name="clientBounds">完整客户区矩形（布局完成后的 ClientRectangle）。</param>
        /// <returns>当前的外观快照（不可变）。</returns>
        public ThemeAppearanceOptions Update(
            Theme theme,
            float dpiScale,
            float userScale,
            Rectangle clientBounds)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (theme == null) throw new ArgumentNullException(nameof(theme));

            bool unchanged =
                _options != null
                && ReferenceEquals(_lastTheme, theme)
                && _lastDpiScale.Equals(dpiScale)
                && _lastUserScale.Equals(userScale)
                && _lastBounds.Equals(clientBounds);

            if (unchanged) return _options!;

            _options = ThemeAppearanceOptions.Create(theme, dpiScale, userScale, clientBounds, _themeGeneration);
            _lastTheme = theme;
            _lastDpiScale = dpiScale;
            _lastUserScale = userScale;
            _lastBounds = clientBounds;

            // 上下文变了 → 让缓存按"代次 / DPI / 用户缩放"自行失效（槽位尺寸由缓存键区分）
            _host.UpdateContext(_options);

            return _options;
        }

        /// <summary>
        /// 换肤：主题代次自增并让各层缓存失效，然后重建上下文。
        /// </summary>
        public ThemeAppearanceOptions OnThemeChanged(
            Theme theme,
            float dpiScale,
            float userScale,
            Rectangle clientBounds)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _themeGeneration++;
            _host.OnThemeChanged(_themeGeneration);
            _options = null;              // 强制重建
            _lastTheme = null;

            return Update(theme, dpiScale, userScale, clientBounds);
        }

        /// <summary>
        /// 窗口尺寸变化：槽位尺寸不再复用，全量失效。
        /// </summary>
        public void OnSizeChanged()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _host.OnSizeChanged();
        }

        /// <summary>
        /// 释放本对象持有的资源（纹理缓存、画刷、噪点位图）。
        /// 背景帧与预览源是**借用**引用，本类不释放它们。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _decor.Dispose();
            _bars.Dispose();
            _host.Dispose();
            _backdrop.Dispose();
        }
    }
}
