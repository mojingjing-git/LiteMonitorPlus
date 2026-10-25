using System;
using System.Collections.Generic;
using System.Drawing;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 自动隐藏纯几何决策核心（<see cref="AutoHideLogic"/>）的行为回归。
    ///
    /// <para>★ 几何取自用户实机（2026-10-25 故障现场）：
    /// DISPLAY1 主屏 (0,0,2194,1234) + DISPLAY2 竖屏副屏 (-1152,0,1152,2048)，
    /// 面板 292×983 停靠在 DISPLAY2 左缘——泊出坐标 -1438 曾被写进 settings.json
    /// （Position.X=-1440，即故障现场），重启后 250ms 内再次泊出 ⇒
    /// "双屏皆无、进程正常"的完整复现路径。</para>
    ///
    /// <para>★ 期望值一律由公式推导（评审 R-2）：泊出 X = area.Left - (width - hideWidth)。</para>
    /// </summary>
    public class AutoHideLogicTests
    {
        private static readonly Rectangle Display1Area = new(0, 0, 2194, 1186);
        private static readonly Rectangle Display2Area = new(-1152, 0, 1152, 2048);
        private static readonly Rectangle[] UserScreens = { Display1Area, Display2Area };

        private const int PanelW = 292;
        private const int PanelH = 983;

        // 96 DPI（MetricsForDpi 下限生效：hideWidth 4→6）
        private static readonly (int HideWidth, int Threshold) Dpi96 = AutoHideLogic.MetricsForDpi(96);
        private const int Band = AutoHideLogic.HoverBandPx;

        // ---------------------------------------------------------------- metrics

        [Theory]
        [InlineData(96, 6, 10)]
        [InlineData(120, 6, 12)]
        [InlineData(144, 6, 15)]
        public void MetricsForDpi_ScalesAndFloorsHideWidth(int dpi, int expectedHideWidth, int expectedThreshold)
        {
            var (hideWidth, threshold) = AutoHideLogic.MetricsForDpi(dpi);
            Assert.Equal(expectedHideWidth, hideWidth);
            Assert.Equal(expectedThreshold, threshold);
            Assert.True(hideWidth >= 6, "hideWidth 必须 ≥6 物理像素（4px 条在副屏边框下肉眼不可见）");
        }

        // ---------------------------------------------------------------- 泊出

        [Fact]
        public void ParkLeft_UserGeometry_UsesFormula()
        {
            // 面板贴 DISPLAY2 左缘（Left == area.Left ⇒ nearLeft 恒真），鼠标在面板外。
            var form = new Rectangle(-1152, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(form, null, AutoHideLogic.DockEdge.None, Display2Area,
                new Point(-500, 300), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);

            Assert.Equal(AutoHideLogic.DecisionKind.Park, d.Kind);
            Assert.Equal(AutoHideLogic.DockEdge.Left, d.Edge);
            int expectedX = Display2Area.Left - (PanelW - Dpi96.HideWidth); // -1152-(292-6) = -1438
            Assert.Equal(expectedX, d.NewLocation.X);
            Assert.Equal(625, d.NewLocation.Y);
        }

        [Fact]
        public void ParkRight_Works()
        {
            var form = new Rectangle(2184, 625, PanelW, PanelH); // Right=2476，贴 DISPLAY1 右缘
            var d = AutoHideLogic.Decide(form, null, AutoHideLogic.DockEdge.None, Display1Area,
                new Point(1500, 300), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);

            Assert.Equal(AutoHideLogic.DecisionKind.Park, d.Kind);
            Assert.Equal(AutoHideLogic.DockEdge.Right, d.Edge);
            Assert.Equal(Display1Area.Right - Dpi96.HideWidth, d.NewLocation.X);
        }

        [Fact]
        public void ParkTop_Works()
        {
            var form = new Rectangle(100, 0, PanelW, PanelH); // 贴 DISPLAY1 顶缘
            var d = AutoHideLogic.Decide(form, null, AutoHideLogic.DockEdge.None, Display1Area,
                new Point(500, 400), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);

            Assert.Equal(AutoHideLogic.DecisionKind.Park, d.Kind);
            Assert.Equal(AutoHideLogic.DockEdge.Top, d.Edge);
            Assert.Equal(Display1Area.Top - (PanelH - Dpi96.HideWidth), d.NewLocation.Y);
        }

        [Fact]
        public void CenteredPanel_NeverParks()
        {
            var form = new Rectangle(800, 300, PanelW, PanelH);
            var d = AutoHideLogic.Decide(form, null, AutoHideLogic.DockEdge.None, Display1Area,
                new Point(946, 700), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.None, d.Kind);
        }

        [Fact]
        public void CursorInsideBounds_BlocksPark()
        {
            var form = new Rectangle(-1152, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(form, null, AutoHideLogic.DockEdge.None, Display2Area,
                new Point(-1100, 800), containsCursor: true, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.None, d.Kind);
        }

        [Fact]
        public void KeepVisible_BlocksEverything()
        {
            var form = new Rectangle(-1152, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(form, null, AutoHideLogic.DockEdge.None, Display2Area,
                new Point(-500, 300), containsCursor: false, inKeepVisible: true,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.None, d.Kind);
        }

        // ---------------------------------------------------------------- 弹出（R2：30px 主导）

        [Fact]
        public void Show_Left_CursorWithin30pxBand_PopsEvenBeyondHideWidth()
        {
            // 【故障复现】泊出条在 X=-1438，仅 DISPLAY2 左缘 6px 可见。
            // 旧算法弹出要求 cursor.X ≤ area.Left+hideWidth=-1146；
            // 新算法 30px 主导 ⇒ -1135 处即可弹出（这正是用户"找不回来"的修复点）。
            var parkedForm = new Rectangle(-1438, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(parkedForm, Display2Area, AutoHideLogic.DockEdge.Left,
                default, new Point(-1135, 800), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);

            Assert.Equal(AutoHideLogic.DecisionKind.Show, d.Kind);
            Assert.Equal(Display2Area.Left, d.NewLocation.X);
            Assert.Equal(625, d.NewLocation.Y);
        }

        [Fact]
        public void Show_Left_BeyondBand_DoesNotPop()
        {
            var parkedForm = new Rectangle(-1438, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(parkedForm, Display2Area, AutoHideLogic.DockEdge.Left,
                default, new Point(-1121, 800), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.None, d.Kind);
        }

        [Fact]
        public void Show_Left_YOutOfRange_DoesNotPop()
        {
            var parkedForm = new Rectangle(-1438, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(parkedForm, Display2Area, AutoHideLogic.DockEdge.Left,
                default, new Point(-1140, 100), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.None, d.Kind);
        }

        [Fact]
        public void Show_Left_WrongScreenCursor_DoesNotPop()
        {
            // 【错屏免疫】光标在 DISPLAY1 左缘（另一块屏）：核心只用 parkedArea 判定，
            // 无论"最近屏"如何翻转都不会在错误的屏上弹出。
            var parkedForm = new Rectangle(-1438, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(parkedForm, Display2Area, AutoHideLogic.DockEdge.Left,
                default, new Point(2, 800), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.None, d.Kind);
        }

        [Fact]
        public void Show_Right_PopsAtAreaRight()
        {
            var parkedForm = new Rectangle(2188, 625, PanelW, PanelH);
            var d = AutoHideLogic.Decide(parkedForm, Display1Area, AutoHideLogic.DockEdge.Right,
                default, new Point(2180, 800), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.Show, d.Kind);
            Assert.Equal(Display1Area.Right - PanelW, d.NewLocation.X);
        }

        [Fact]
        public void Show_Top_PopsAtAreaTop()
        {
            var parkedForm = new Rectangle(100, -977, PanelW, PanelH);
            var d = AutoHideLogic.Decide(parkedForm, Display1Area, AutoHideLogic.DockEdge.Top,
                default, new Point(200, -1000), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.Show, d.Kind);
            Assert.Equal(Display1Area.Top, d.NewLocation.Y);
        }

        [Fact]
        public void Show_Top_BeyondBand_DoesNotPop()
        {
            var parkedForm = new Rectangle(100, -977, PanelW, PanelH);
            var d = AutoHideLogic.Decide(parkedForm, Display1Area, AutoHideLogic.DockEdge.Top,
                default, new Point(200, 100), containsCursor: false, inKeepVisible: false,
                Dpi96.HideWidth, Dpi96.Threshold, Band);
            Assert.Equal(AutoHideLogic.DecisionKind.None, d.Kind);
        }

        // ---------------------------------------------------------------- 钳制（F 项自愈数学）

        [Fact]
        public void ClampToArea_PullsParkedPositionBackIntoScreen()
        {
            // 用户 settings.json 里的 -1440（泊出指纹）启动自愈路径。
            var clamped = AutoHideLogic.ClampToArea(new Point(-1440, 625), new Size(PanelW, PanelH), Display2Area);
            Assert.Equal(Display2Area.Left, clamped.X);
            Assert.Equal(625, clamped.Y);
        }

        [Fact]
        public void ClampToArea_InsideStaysUnchanged()
        {
            var clamped = AutoHideLogic.ClampToArea(new Point(-1000, 500), new Size(PanelW, PanelH), Display2Area);
            Assert.Equal(new Point(-1000, 500), clamped);
        }

        // ---------------------------------------------------------------- 看门狗判据

        [Fact]
        public void IntersectsAny_ParkedSliverStillIntersects_WatchdogMustNotFire()
        {
            // 泊出态（左缘 6px 条）与 DISPLAY2 必有交集 ⇒ 看门狗不得与自动隐藏打架。
            var parked = new Rectangle(Display2Area.Left - (PanelW - 6), 625, PanelW, PanelH);
            Assert.True(AutoHideLogic.IntersectsAny(parked, UserScreens));
        }

        [Fact]
        public void IntersectsAny_FullyOffScreen_ReturnsFalse()
        {
            var lost = new Rectangle(-3000, 625, PanelW, PanelH);
            Assert.False(AutoHideLogic.IntersectsAny(lost, UserScreens));
        }

        // ---------------------------------------------------------------- 落盘守卫

        [Fact]
        public void EffectiveSaveLocation_HiddenUsesPrePark()
        {
            var saved = AutoHideLogic.EffectiveSaveLocation(
                isHidden: true, preParkLocation: new Point(-1000, 625), currentLocation: new Point(-1438, 625));
            Assert.Equal(new Point(-1000, 625), saved);
        }

        [Fact]
        public void EffectiveSaveLocation_VisibleUsesCurrent()
        {
            var saved = AutoHideLogic.EffectiveSaveLocation(
                isHidden: false, preParkLocation: new Point(-1000, 625), currentLocation: new Point(-1100, 625));
            Assert.Equal(new Point(-1100, 625), saved);
        }
    }
}
