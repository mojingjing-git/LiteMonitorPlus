using System;
using System.Collections.Generic;
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

        // ---- 复用的组块区域缓冲 ----
        // ★ 为什么需要它：窗口底纹要"排除组块区域"，而组块矩形会随布局变化。
        //   若每帧 new 一个 List，v4 §7.4「新效果关闭 → 热路径不增加托管分配」就会被打破
        //   （List 本身 + 它的内部数组 = 每帧 2 次托管分配）。
        //   这里复用一个 List：Clear() 不释放容量，布局稳定后每帧 0 次分配。
        private readonly List<DecorGroupRegion> _groupRegions = new();

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

            // ★ 背景层（噪点纹理）的失效合同（独立验收 R1）：
            //   BackdropRenderer.Invalidate() 之前在 src/** 里从未被调用 ——
            //   换肤 / 尺寸 / DPI 变化后背景层仍持有旧纹理。
            //   本方法正是这三个时机唯一的汇合点（主题引用、DPI、用户缩放、客户区任一变化），
            //   且**只在上下文真的变化时**才走到这里 ⇒ 稳态每帧 0 次重建。
            _backdrop.Invalidate();

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

            // ★ 背景层纹理同样要失效（窗口尺寸变了，旧纹理不再适用）。
            //   这里不走 Update —— 客户区矩形要等布局算完才知道，
            //   但背景层的纹理与尺寸无关，先失效掉总没有坏处。
            _backdrop.Invalidate();
        }

        /// <summary>
        /// 取当前分组布局对应的**组块区域列表**（供窗口底纹排除组块区域用）。
        /// <para>
        /// ★ 返回的是**内部复用缓冲**，调用方只能读，不能长期持有；
        ///   下一次调用会把内容覆盖。渲染层只在当帧内使用它，安全。
        /// </para>
        /// <para>
        /// ★ 这里刻意**不复用**结果缓存：组块矩形会随布局变化（换肤、横竖切换、缩放），
        ///   每次重建的开销远小于"缓存失效算错"的风险；而 List 的容量会被复用，
        ///   所以稳态下仍然是 0 次托管分配。
        /// </para>
        /// </summary>
        public IReadOnlyList<DecorGroupRegion> RentGroupRegions(List<GroupLayoutInfo> groups, Theme theme)
        {
            _groupRegions.Clear();
            if (groups == null || theme == null) return _groupRegions;

            int radiusPx = theme.Layout.GroupRadius;
            foreach (GroupLayoutInfo gr in groups)
            {
                if (gr.Bounds.Width <= 0 || gr.Bounds.Height <= 0) continue;
                _groupRegions.Add(new DecorGroupRegion(gr.Bounds, radiusPx));
            }
            return _groupRegions;
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
