using System;
using System.Collections.Generic;
using System.Drawing;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// 自动隐藏的**纯几何决策核心**（无 WinForms 依赖，LiteMonitor.Tests 可直接单测）。
    ///
    /// <para>【R2 / 2026-10-25】从 <see cref="MainFormBizHelper.CheckAutoHide"/> 抽出。
    /// 抽出的动机：泊出后窗口中心落在所有屏幕之外，宿主每周期用
    /// <c>Screen.FromPoint(center)</c> 取屏可能取到邻居屏 ⇒ 弹出带坐标错位、面板
    /// 永远弹不出（用户实测：面板泊出后双屏皆"消失"，仅存 4px 边条）。</para>
    ///
    /// <para>★ 修复三件套（与本核心配套）：</para>
    /// <list type="number">
    /// <item><b>泊出屏记忆</b>：宿主在泊出时记录 <c>parkedArea</c>，弹出判定**只**用它；
    ///       本核心因此不再需要"取屏"这个不可靠步骤。</item>
    /// <item><b>弹出带 30px 主导</b>：旧实现的弹出要求光标同时落在 hideWidth(4px) 带
    ///       与 hoverBand(30px) 带内，有效带宽 = min = 4px，肉眼不可命中；
    ///       本核心弹出判据以 hoverBand 为主（保留纵向/横向范围约束防误弹）。</item>
    /// <item><b>DPI 现算</b>：hideWidth/threshold 由 <see cref="MetricsForDpi"/> 按窗体
    ///       当前 DeviceDpi 计算，hideWidth 有 6 物理像素下限（旧值 4px 在高 DPI 下不可见）。</item>
    /// </list>
    /// </summary>
    internal static class AutoHideLogic
    {
        internal enum DockEdge { None, Left, Right, Top }

        internal enum DecisionKind { None, Park, Show }

        internal readonly record struct Decision(DecisionKind Kind, DockEdge Edge, Point NewLocation);

        /// <summary>弹出判定带宽（物理像素）：泊出屏边缘此带宽内即弹出。</summary>
        internal const int HoverBandPx = 30;

        /// <summary>
        /// 按窗体当前 DeviceDpi 现算泊出宽度与贴边阈值。
        /// <para>hideWidth 下限 6 物理像素：旧值 4px 在副屏边框/过扫描下肉眼不可见，
        /// 是"窗口消失"观感的直接来源之一；threshold 维持 10 逻辑像素语义。</para>
        /// </summary>
        internal static (int HideWidthPx, int ThresholdPx) MetricsForDpi(int deviceDpi)
        {
            int dpi = Math.Max(96, deviceDpi);
            int hideWidth = Math.Max(4 * dpi / 96, 6);
            int threshold = Math.Max(10 * dpi / 96, 10);
            return (hideWidth, threshold);
        }

        /// <summary>
        /// 自动隐藏每 tick 的判定。
        /// <para>★ <paramref name="parkedArea"/> 非 null ⇒ 已泊出，做**弹出**判定
        /// （只用该屏，见类型注释）；null ⇒ 未泊出，做**泊出**判定
        /// （<paramref name="screenArea"/> 必须是泊出前窗口中心所在屏的 WorkingArea——
        /// 此刻中心在屏内，宿主用 <c>Screen.FromPoint</c> 取值可靠）。</para>
        /// <para>语义迁移自旧 <c>CheckAutoHide</c>：贴边阈值、
        /// <c>Bounds.Contains(cursor)</c> 门、泊出坐标公式逐字保持；
        /// 仅弹出带与 hideWidth 来源按上注变更。</para>
        /// </summary>
        internal static Decision Decide(
            Rectangle formBounds,
            Rectangle? parkedArea,
            DockEdge dock,
            Rectangle screenArea,
            Point cursor,
            bool containsCursor,
            bool inKeepVisible,
            int hideWidthPx,
            int thresholdPx,
            int hoverBandPx)
        {
            if (inKeepVisible) return new(DecisionKind.None, DockEdge.None, default);

            if (!parkedArea.HasValue)
            {
                // ---- 泊出判定（旧语义逐字迁移）----
                bool nearLeft = formBounds.Left <= screenArea.Left + thresholdPx;
                bool nearRight = screenArea.Right - formBounds.Right <= thresholdPx;
                bool nearTop = formBounds.Top <= screenArea.Top + thresholdPx;
                bool shouldHide = nearLeft || nearRight || nearTop;
                if (!shouldHide || containsCursor) return new(DecisionKind.None, DockEdge.None, default);

                // 优先级与旧实现一致：Right > Left > Top（Bottom 从未实现）。
                if (nearRight)
                    return new(DecisionKind.Park, DockEdge.Right,
                        new Point(screenArea.Right - hideWidthPx, formBounds.Y));
                if (nearLeft)
                    return new(DecisionKind.Park, DockEdge.Left,
                        new Point(screenArea.Left - (formBounds.Width - hideWidthPx), formBounds.Y));
                return new(DecisionKind.Park, DockEdge.Top,
                    new Point(formBounds.X, screenArea.Top - (formBounds.Height - hideWidthPx)));
            }

            // ---- 弹出判定（R2：hoverBand 主导，范围约束防误弹）----
            var area = parkedArea.Value;
            bool inSpanY = cursor.Y >= formBounds.Y && cursor.Y <= formBounds.Y + formBounds.Height;
            bool inSpanX = cursor.X >= formBounds.X && cursor.X <= formBounds.X + formBounds.Width;
            switch (dock)
            {
                case DockEdge.Left:
                    return cursor.X <= area.Left + hoverBandPx && inSpanY
                        ? new(DecisionKind.Show, DockEdge.None, new Point(area.Left, formBounds.Y))
                        : new(DecisionKind.None, DockEdge.None, default);
                case DockEdge.Right:
                    return cursor.X >= area.Right - hoverBandPx && inSpanY
                        ? new(DecisionKind.Show, DockEdge.None, new Point(area.Right - formBounds.Width, formBounds.Y))
                        : new(DecisionKind.None, DockEdge.None, default);
                case DockEdge.Top:
                    return cursor.Y <= area.Top + hoverBandPx && inSpanX
                        ? new(DecisionKind.Show, DockEdge.None, new Point(formBounds.X, area.Top))
                        : new(DecisionKind.None, DockEdge.None, default);
                default:
                    return new(DecisionKind.None, DockEdge.None, default);
            }
        }

        /// <summary>
        /// 屏内钳制（原 <c>MainFormBizHelper.SetSafeLocation</c> 数学逐字迁移）：
        /// 完全在屏内的位置原样返回；越界部分拉回 <paramref name="area"/> 工作区内。
        /// </summary>
        internal static Point ClampToArea(Point location, Size size, Rectangle area)
        {
            int x = location.X;
            int y = location.Y;
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + size.Width > area.Right) x = area.Right - size.Width;
            if (y + size.Height > area.Bottom) y = area.Bottom - size.Height;
            return new Point(x, y);
        }

        /// <summary>
        /// 窗口 Bounds 是否与**任意**一块屏幕工作区相交。
        /// 看门狗判据：泊出态窗口恒有 hideWidth(≥6px) 交集 ⇒ 不触发；
        /// 完全失去屏幕定位（拖出虚拟桌面/拓扑残影）⇒ 触发自愈。
        /// </summary>
        internal static bool IntersectsAny(Rectangle bounds, IReadOnlyList<Rectangle> areas)
        {
            foreach (Rectangle area in areas)
            {
                if (bounds.IntersectsWith(area)) return true;
            }
            return false;
        }

        /// <summary>落盘位置守卫：泊出态**绝不**把泊出坐标写进 settings.json（R2 评审 P2）。</summary>
        internal static Point EffectiveSaveLocation(bool isHidden, Point preParkLocation, Point currentLocation)
            => isHidden ? preParkLocation : currentLocation;
    }
}
