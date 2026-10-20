using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Decor;
using LiteMonitor.src.UI.Helpers;

namespace LiteMonitor.AppearanceWindowShapeProbe
{
    /// <summary>
    /// A09 窗口轮廓策略自证跑批。
    ///
    /// 全部场景都在**不显示**的离屏窗体上完成（只创建句柄）：
    /// 不创建 HardwareMonitor、不 Open 硬件、不做屏幕捕获、不读写用户配置目录。
    /// </summary>
    internal sealed class WindowShapeProbeRunner
    {
        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;

        private readonly List<ProbeCase> _cases = new();
        private readonly List<(int attribute, int value)> _dwmCalls = new();
        private Bitmap? _probeBitmap;
        private Graphics? _probeGraphics;

        public List<ProbeCase> Cases => _cases;

        public void RunAll()
        {
            RunOnStaThread(() =>
            {
                using Form form = CreateProbeForm();
                using WindowAppearanceController controller = new WindowAppearanceController(form);

                // A13 接线示范：句柄事件 → 控制器（组件本身不绑定任何事件）
                form.HandleCreated += (_, __) => controller.OnHandleCreated();
                form.HandleDestroyed += (_, __) => controller.OnHandleDestroyed();

                controller.DwmSetWindowAttributeInterceptor = CaptureDwmCall;

                RunStyleMapping(form, controller);
                RunChamferUsesDoNotRound(form, controller);
                RunReversibleSwitch(form, controller);
                RunRegionOwnershipAndLeak(form, controller);
                RunBoundarySizes(form, controller);
                RunPhysicalPixelsUsedOnce(form, controller);
                RunGeometryDiagnostic();
                RunUiThreadOnly(controller);
                RunPathCopiedIntoRegion();
                RunDwmReadback(form, controller);
                RunHandleDestroyed();
                RunResizeReclampsCornerLength();
                RunThemeSwitchUpdatesCornerLength();
                RunLayoutSwitchUpdatesCornerLength();
                RunResizeBindingUnique();

                _probeGraphics?.Dispose();
                _probeBitmap?.Dispose();
                _probeGraphics = null;
                _probeBitmap = null;
            });
        }

        // ============================================================
        // 用例 1：样式 → 轮廓策略 / DWM 属性值 映射
        // ============================================================

        private void RunStyleMapping(Form form, WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("style_mapping");
            JsonArray arr = new();

            (int style, string label)[] styles =
            {
                (0, "none"),
                (1, "chamfer"),
                (2, "rounded_stroke"),
                (3, "holographic"),
                (99, "unknown")
            };

            foreach ((int style, string label) in styles)
            {
                _dwmCalls.Clear();
                ThemeAppearanceOptions options = BuildOptions(style, 16, 2, new Rectangle(0, 0, 240, 160));
                WindowShapeResult r = controller.Apply(options);

                int cornerValue = LastCornerPreferenceValue();
                bool hasRegion = form.Region != null;

                arr.Add(new JsonObject
                {
                    ["frameStyle"] = style,
                    ["label"] = label,
                    ["strategy"] = r.Strategy.ToString(),
                    ["dwmCornerPreference"] = cornerValue,
                    ["regionApplied"] = hasRegion,
                    ["clientBounds"] = $"{r.ClientBounds.Width}x{r.ClientBounds.Height}",
                    ["cornerLengthPx"] = r.CornerLengthPx,
                    ["radiusPx"] = r.RadiusPx,
                    ["note"] = r.Note
                });

                switch (style)
                {
                    case 0:
                        c.Require(r.Strategy == WindowShapeStrategy.SystemRounded,
                            $"FrameStyle=0 应映射 SystemRounded，实际 {r.Strategy}");
                        c.Require(cornerValue == 2, $"FrameStyle=0 的 DWM 角偏好应为 2(ROUND)，实际 {cornerValue}");
                        c.Require(!hasRegion, "FrameStyle=0 不应设置 Region（交回系统）");
                        break;

                    case 1:
                        c.Require(r.Strategy == WindowShapeStrategy.Chamfered,
                            $"FrameStyle=1 应映射 Chamfered，实际 {r.Strategy}");
                        c.Require(cornerValue == 1, $"FrameStyle=1 的 DWM 角偏好应为 1(DONOTROUND)，实际 {cornerValue}");
                        c.Require(hasRegion, "FrameStyle=1 应设置切角 Region");
                        break;

                    case 2:
                        c.Require(r.Strategy == WindowShapeStrategy.Rounded,
                            $"FrameStyle=2 应映射 Rounded，实际 {r.Strategy}");
                        c.Require(cornerValue == 2, $"FrameStyle=2 的 DWM 角偏好应为 2(ROUND)，实际 {cornerValue}");
                        c.Require(hasRegion, "FrameStyle=2 应设置圆角 Region");
                        break;

                    case 3:
                        c.Require(r.Strategy == WindowShapeStrategy.Chamfered,
                            $"FrameStyle=3 应映射 Chamfered（外层切角），实际 {r.Strategy}");
                        c.Require(cornerValue == 1, $"FrameStyle=3 的 DWM 角偏好应为 1(DONOTROUND)，实际 {cornerValue}");
                        c.Require(hasRegion, "FrameStyle=3 应设置切角 Region");
                        break;

                    default:
                        c.Require(r.Strategy == WindowShapeStrategy.SystemRounded,
                            $"未知样式 {style} 应按无框处理，实际 {r.Strategy}");
                        c.Require(cornerValue == 2, $"未知样式的 DWM 角偏好应为 2，实际 {cornerValue}");
                        c.Require(!hasRegion, "未知样式不应设置 Region");
                        break;
                }
            }

            c.Detail("mapping", arr);
        }

        // ============================================================
        // 用例 2：切角必须用 DONOTROUND = 1（不是 0）
        // ============================================================

        private void RunChamferUsesDoNotRound(Form form, WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("chamfer_uses_do_not_round_one");

            c.Require(WindowAppearanceController.DwmwaWindowCornerPreference == 33,
                "DWMWA_WINDOW_CORNER_PREFERENCE 属性号必须是 33");
            c.Require(WindowAppearanceController.DwmcpDefault == 0, "DWMWCP_DEFAULT 必须是 0");
            c.Require(WindowAppearanceController.DwmcpDoNotRound == 1, "DWMWCP_DONOTROUND 必须是 1");
            c.Require(WindowAppearanceController.DwmcpRound == 2, "DWMWCP_ROUND 必须是 2");
            c.Require(WindowAppearanceController.DwmcpRoundSmall == 3, "DWMWCP_ROUNDSMALL 必须是 3");

            _dwmCalls.Clear();
            controller.Apply(BuildOptions(1, 16, 2, new Rectangle(0, 0, 240, 160)));

            List<int> values = CornerPreferenceValues();
            c.Require(values.Count > 0, "未捕获到任何 DWM 角偏好写入");
            foreach (int v in values)
            {
                c.Require(v == 1, $"切角时传给 DwmSetWindowAttribute 的角偏好必须是 1(DONOTROUND)，实际 {v}");
                c.Require(v != 0, "切角时不得使用 0(DWMWCP_DEFAULT)：DEFAULT 不是禁用圆角");
            }

            c.Detail("cornerPreferenceValues",
                new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
            c.Detail("dwmAttribute", WindowAppearanceController.DwmwaWindowCornerPreference);
        }

        // ============================================================
        // 用例 3：可逆性（切角 → 圆角 → 无框）
        // ============================================================

        private void RunReversibleSwitch(Form form, WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("reversible_style_switch");
            Rectangle bounds = new Rectangle(0, 0, 240, 160);

            // 初始：旧路径（无框）
            WindowShapeResult initial = controller.Apply(BuildOptions(0, 16, 2, bounds));
            string initialState = DescribeState(form, controller);

            // 切角
            WindowShapeResult chamfer = controller.Apply(BuildOptions(1, 16, 2, bounds));
            c.Require(chamfer.Strategy == WindowShapeStrategy.Chamfered, "切角策略不对");
            c.Require(chamfer.CornerPreference == 1, "切角的 DWM 值应为 1");
            c.Require(form.Region != null, "切角后应有 Region");
            c.Require(LastCornerPreferenceValue() == 1, "切角后实际写入的 DWM 值应为 1");

            // 圆角
            WindowShapeResult rounded = controller.Apply(BuildOptions(2, 16, 2, bounds));
            c.Require(rounded.Strategy == WindowShapeStrategy.Rounded, "圆角策略不对");
            c.Require(rounded.CornerPreference == 2, "回到圆角必须恢复 ROUND=2");
            c.Require(form.Region != null, "圆角应有圆角 Region");
            c.Require(LastCornerPreferenceValue() == 2, "回到圆角后实际写入的 DWM 值应为 2");

            // 无框
            WindowShapeResult none = controller.Apply(BuildOptions(0, 16, 2, bounds));
            c.Require(none.Strategy == WindowShapeStrategy.SystemRounded, "无框策略不对");
            c.Require(none.CornerPreference == 2, "无框的 DWM 值应为 2");
            c.Require(form.Region == null, "无框必须把 Region 交回系统（不能残留上一次的 Region）");
            c.Require(LastCornerPreferenceValue() == 2, "无框必须把 DWM 角偏好恢复为 2（不能残留 DONOTROUND）");

            string finalState = DescribeState(form, controller);
            c.Require(finalState == initialState,
                $"最终状态与初始状态不一致：初始={initialState}；最终={finalState}");

            c.Detail("initialState", initialState);
            c.Detail("finalState", finalState);
            c.Detail("initialCornerPreference", initial.CornerPreference);
        }

        // ============================================================
        // 用例 4：Region 所有权 + 反复切换不泄漏
        // ============================================================

        private void RunRegionOwnershipAndLeak(Form form, WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("region_ownership_no_leak");
            Rectangle bounds = new Rectangle(0, 0, 240, 160);

            // ① 所有权证据：赋给 Control.Region 的实例由 WinForms（Control）释放
            controller.Apply(BuildOptions(1, 16, 2, bounds));
            Region? owned = form.Region;
            c.Require(owned != null, "切角后应有 Region");

            bool releasedByWinForms = false;
            string releaseDetail = "";
            if (owned != null)
            {
                form.Region = null;      // 置空 → WinForms 释放旧 Region 并清掉窗口 HRGN
                try
                {
                    GetRegionBounds(owned);
                    releaseDetail = "置空后仍可访问（未被释放）";
                }
                catch (Exception ex)
                {
                    releasedByWinForms = true;
                    releaseDetail = ex.GetType().Name;
                }
            }
            c.Require(releasedByWinForms,
                "Control.Region 置空后旧 Region 应已被 WinForms 释放（所有权属于 Control）: " + releaseDetail);
            c.Require(controller.Diagnostics.RegionsDisposedByController == 0,
                "组件不应主动 Dispose 赋给 Control.Region 的 Region（会变成悬空引用）");

            // ② 反复切换：GDI / USER 对象数与句柄数不得持续增长
            uint gdiBefore = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
            uint userBefore = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
            int handleBefore = Process.GetCurrentProcess().HandleCount;

            const int cycles = 100;
            for (int i = 0; i < cycles; i++)
            {
                controller.Apply(BuildOptions(1, 16, 2, bounds));
                controller.Apply(BuildOptions(2, 16, 2, bounds));
                controller.Apply(BuildOptions(0, 16, 2, bounds));
            }

            uint gdiRound1 = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
            uint userRound1 = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
            int handleRound1 = Process.GetCurrentProcess().HandleCount;

            for (int i = 0; i < cycles; i++)
            {
                controller.Apply(BuildOptions(1, 16, 2, bounds));
                controller.Apply(BuildOptions(2, 16, 2, bounds));
                controller.Apply(BuildOptions(0, 16, 2, bounds));
            }

            uint gdiAfter = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
            uint userAfter = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
            int handleAfter = Process.GetCurrentProcess().HandleCount;

            c.Require(gdiAfter <= gdiBefore + 2,
                $"GDI 对象数持续增长：{gdiBefore} → {gdiAfter}（{cycles * 2} 轮切换后）");
            c.Require(userAfter <= userBefore + 2,
                $"USER 对象数持续增长：{userBefore} → {userAfter}");
            c.Require(handleAfter <= handleBefore + 2,
                $"内核句柄数持续增长：{handleBefore} → {handleAfter}");
            // ★ 第二轮相对第一轮不得再增长（一次性分配不算泄漏，每轮都涨才是泄漏）
            c.Require(gdiAfter <= gdiRound1 + 1,
                $"GDI 对象数在第二轮继续增长：{gdiRound1} → {gdiAfter}");
            c.Require(userAfter <= userRound1 + 1,
                $"USER 对象数在第二轮继续增长：{userRound1} → {userAfter}");
            c.Require(handleAfter <= handleRound1 + 1,
                $"内核句柄数在第二轮继续增长：{handleRound1} → {handleAfter}");
            c.Require(controller.Diagnostics.PathsCreated == controller.Diagnostics.PathsDisposed,
                $"GraphicsPath 未全部释放：创建 {controller.Diagnostics.PathsCreated} / 释放 {controller.Diagnostics.PathsDisposed}");
            c.Require(controller.Diagnostics.RegionsCreated > 0, "应至少创建过 Region");
            c.Require(form.Region == null, "最后一轮是“无框”，窗体上不应残留 Region");

            c.Detail("cycles", cycles * 2);
            c.Detail("gdiBefore", (int)gdiBefore);
            c.Detail("gdiRound1", (int)gdiRound1);
            c.Detail("gdiAfter", (int)gdiAfter);
            c.Detail("userBefore", (int)userBefore);
            c.Detail("userRound1", (int)userRound1);
            c.Detail("userAfter", (int)userAfter);
            c.Detail("handleBefore", handleBefore);
            c.Detail("handleRound1", handleRound1);
            c.Detail("handleAfter", handleAfter);
            c.Detail("pathsCreated", controller.Diagnostics.PathsCreated);
            c.Detail("pathsDisposed", controller.Diagnostics.PathsDisposed);
            c.Detail("regionsCreated", controller.Diagnostics.RegionsCreated);
            c.Detail("regionsDisposedByController", controller.Diagnostics.RegionsDisposedByController);
            c.Detail("releaseDetail", releaseDetail);
        }

        // ============================================================
        // 用例 5：边界尺寸（1×1 / 零宽高 / 极小窗 / 切角超半宽）
        // ============================================================

        private void RunBoundarySizes(Form form, WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("boundary_sizes");
            JsonArray arr = new();

            (string label, int w, int h)[] sizes =
            {
                ("1x1", 1, 1),
                ("0x0", 0, 0),
                ("0x120", 0, 120),
                ("240x0", 240, 0),
                ("2x2", 2, 2),
                ("60x40", 60, 40),
                ("240x160", 240, 160)
            };

            foreach ((string label, int w, int h) in sizes)
            {
                foreach (int style in new[] { 0, 1, 2, 3 })
                {
                    try
                    {
                        ThemeAppearanceOptions options = BuildOptions(style, 64, 8, new Rectangle(0, 0, w, h));
                        WindowShapeResult r = controller.Apply(options);

                        Region? reg = form.Region;
                        bool regionValid = true;
                        string regionInfo = "none";
                        if (reg != null)
                        {
                            RectangleF rb = GetRegionBounds(reg);
                            regionValid = rb.Width > 0 && rb.Height > 0
                                          && rb.Width <= Math.Max(w, 1) + 0.5f && rb.Height <= Math.Max(h, 1) + 0.5f;
                            regionInfo = $"{rb.Width}x{rb.Height}@({rb.X},{rb.Y})";
                        }

                        c.Require(regionValid, $"{label} style={style}: 产生了非法 Region（{regionInfo}）");
                        arr.Add(new JsonObject
                        {
                            ["size"] = label,
                            ["style"] = style,
                            ["strategy"] = r.Strategy.ToString(),
                            ["regionApplied"] = r.RegionApplied,
                            ["regionBounds"] = regionInfo
                        });
                    }
                    catch (Exception ex)
                    {
                        c.Fail($"{label} style={style} 抛出异常：{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // 极小窗口 + Resize 后重算
            try
            {
                form.Size = new Size(1, 1);
                controller.Reapply();
                form.Size = new Size(240, 160);
                controller.Reapply();
            }
            catch (Exception ex)
            {
                c.Fail("极小尺寸 Reapply 抛出异常：" + ex.Message);
            }

            c.Detail("sizes", arr);
        }

        // ============================================================
        // 用例 6：物理像素只用一次（不二次缩放）
        // ============================================================

        private void RunPhysicalPixelsUsedOnce(Form form, WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("physical_pixels_used_once");
            Rectangle bounds = new Rectangle(0, 0, 240, 160);

            // 192 DPI：逻辑 8 → 物理 16
            ThemeAppearanceOptions hi = BuildOptions(1, 8, 2, bounds, dpiScale: 2.0f);
            c.Require(hi.FrameLengthPx == 16, $"192 DPI 下 FrameLengthPx 应为 16，实际 {hi.FrameLengthPx}");

            controller.Apply(hi);
            c.Require(controller.LastCornerLengthPx == 16,
                $"传给窗口轮廓的切角边长应为物理像素 16，实际 {controller.LastCornerLengthPx}");
            c.Require(controller.LastCornerLengthPx != 32, "切角边长不得二次缩放（不能是 32）");

            Region? reg = form.Region;
            c.Require(reg != null, "切角后应有 Region");
            if (reg != null)
            {
                // 用扫描线精确定位切角：第 0 条扫描线（最上一行）的左边界 = 切角边长。
                // ★ 不能用 Region.IsVisible 判断"在不在切角内"：GDI+ 的命中测试对边界有
                //   约 1~2 像素的容差（实测 x=14.5 已被判为可见，而实际切角线在 x=15.5）。
                float scanX = FirstScanX(reg);
                c.Require(Math.Abs(scanX - 16f) < 0.5f,
                    $"192 DPI 下顶部切角位置应为 16 物理像素，实际 {scanX}（不得二次缩放成 32）");
                c.Detail("dpi192TopScanX", scanX);
            }

            // 96 DPI：逻辑 8 → 物理 8
            ThemeAppearanceOptions lo = BuildOptions(1, 8, 2, bounds, dpiScale: 1.0f);
            c.Require(lo.FrameLengthPx == 8, $"96 DPI 下 FrameLengthPx 应为 8，实际 {lo.FrameLengthPx}");

            controller.Apply(lo);
            c.Require(controller.LastCornerLengthPx == 8,
                $"96 DPI 下传给窗口轮廓的切角边长应为 8，实际 {controller.LastCornerLengthPx}");

            reg = form.Region;
            if (reg != null)
            {
                float scanX = FirstScanX(reg);
                c.Require(Math.Abs(scanX - 8f) < 0.5f,
                    $"96 DPI 下顶部切角位置应为 8 物理像素，实际 {scanX}");
                c.Detail("dpi96TopScanX", scanX);
            }

            c.Detail("dpi192FrameLengthPx", hi.FrameLengthPx);
            c.Detail("dpi96FrameLengthPx", lo.FrameLengthPx);
            c.Detail("appliedCornerLengthPx", controller.LastCornerLengthPx);
        }

        // ============================================================
        // 诊断：Region 的几何语义（切角位置 / IsVisible 行为 / Equals 行为）
        // ============================================================

        private void RunGeometryDiagnostic()
        {
            ProbeCase c = NewCase("geometry_diagnostic");
            Rectangle bounds = new Rectangle(0, 0, 240, 160);

            using GraphicsPath path = DecorGeometry.BuildFramePath(bounds, 1, 16, 16, 0);
            JsonArray pts = new();
            foreach (PointF p in path.PathPoints) pts.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
            c.Detail("pathPoints", pts);
            c.Detail("pointCount", path.PointCount);
            c.Detail("fillMode", path.FillMode.ToString());

            using Region region = new Region(path);
            JsonArray scan = new();
            foreach (float x in new float[] { 0.5f, 4.5f, 8.5f, 12.5f, 14.5f, 15.5f, 16.5f, 20.5f, 100.5f })
            {
                scan.Add(new JsonObject { ["x"] = x, ["y"] = 0.5f, ["visible"] = region.IsVisible(x, 0.5f) });
            }
            c.Detail("isVisibleScanTopEdge", scan);

            using Region ref16 = BuildReferenceChamfer(bounds, 16);
            using Region ref8 = BuildReferenceChamfer(bounds, 8);
            using Region ref32 = BuildReferenceChamfer(bounds, 32);
            c.Detail("equalsRef16", region.Equals(ref16));
            c.Detail("equalsRef8", region.Equals(ref8));
            c.Detail("equalsRef32", region.Equals(ref32));

            // 扫描线：第 0 条扫描线的左边界 = 顶部切角的实际位置
            JsonArray scans = new();
            RectangleF[] scanRects = region.GetRegionScans(new Matrix());
            for (int i = 0; i < Math.Min(3, scanRects.Length); i++)
            {
                scans.Add(new JsonObject
                {
                    ["index"] = i,
                    ["x"] = scanRects[i].X,
                    ["y"] = scanRects[i].Y,
                    ["w"] = scanRects[i].Width,
                    ["h"] = scanRects[i].Height
                });
            }
            c.Detail("scanCount", scanRects.Length);
            c.Detail("firstScans", scans);
            c.Detail("firstScanX", scanRects.Length > 0 ? scanRects[0].X : -1);

            // 参考扫描线（不同切角边长）
            foreach ((int corner, string label) in new[] { (8, "c8"), (16, "c16"), (32, "c32") })
            {
                using Region r = BuildReferenceChamfer(bounds, corner);
                RectangleF[] rs = r.GetRegionScans(new Matrix());
                c.Detail("firstScanX_" + label, rs.Length > 0 ? rs[0].X : -1);
            }

            // 用 IsVisible(x, y, Graphics) 变体再测一次
            if (_probeGraphics != null)
            {
                JsonArray scan2 = new();
                foreach (float x in new float[] { 0.5f, 8.5f, 14.5f, 15.5f, 16.5f, 20.5f })
                {
                    scan2.Add(new JsonObject { ["x"] = x, ["y"] = 0.5f, ["visible"] = region.IsVisible(x, 0.5f, _probeGraphics) });
                }
                c.Detail("isVisibleScanWithGraphics", scan2);
            }
        }

        /// <summary>独立构造一个切角参考区域（不依赖 A05 的几何代码）。</summary>
        private static Region BuildReferenceChamfer(Rectangle rect, int corner)
        {
            float f = corner;
            GraphicsPath p = new GraphicsPath();
            p.AddPolygon(new[]
            {
                new PointF(rect.X + f, rect.Y),
                new PointF(rect.X + rect.Width - f, rect.Y),
                new PointF(rect.X + rect.Width, rect.Y + f),
                new PointF(rect.X + rect.Width, rect.Y + rect.Height - f),
                new PointF(rect.X + rect.Width - f, rect.Y + rect.Height),
                new PointF(rect.X + f, rect.Y + rect.Height),
                new PointF(rect.X, rect.Y + rect.Height - f),
                new PointF(rect.X, rect.Y + f)
            });
            p.CloseFigure();
            Region region = new Region(p);
            p.Dispose();
            return region;
        }

        // ============================================================
        // 用例 7：不得在非 UI 线程调用
        // ============================================================

        private void RunUiThreadOnly(WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("ui_thread_only");
            ThemeAppearanceOptions options = BuildOptions(1, 16, 2, new Rectangle(0, 0, 240, 160));

            Exception? caught = null;
            Thread worker = new Thread(() =>
            {
                try { controller.Apply(options); }
                catch (Exception ex) { caught = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            worker.Join();

            c.Require(caught is InvalidOperationException,
                $"非 UI 线程调用应抛 InvalidOperationException，实际 {(caught == null ? "无异常" : caught.GetType().Name)}");
            c.Require(controller.Diagnostics.OffThreadRejections >= 1, "应记录非 UI 线程拒绝次数");
            c.Detail("caught", caught?.GetType().Name ?? "none");
        }

        // ============================================================
        // 用例 8：Region 会复制路径内容（路径可立即释放）
        // ============================================================

        private void RunPathCopiedIntoRegion()
        {
            ProbeCase c = NewCase("path_copied_into_region");

            GraphicsPath path = new GraphicsPath();
            path.AddRectangle(new Rectangle(0, 0, 100, 50));
            using Region region = new Region(path);
            path.Dispose();   // 路径立即释放

            bool ok;
            string detail;
            try
            {
                RectangleF b = GetRegionBounds(region);
                ok = Math.Abs(b.Width - 100) < 0.5f && Math.Abs(b.Height - 50) < 0.5f;
                detail = $"{b.Width}x{b.Height}";
            }
            catch (Exception ex)
            {
                ok = false;
                detail = ex.GetType().Name + ": " + ex.Message;
            }

            c.Require(ok, "Region 应复制路径内容（路径释放后 Region 仍可用）：" + detail);
            c.Detail("boundsAfterPathDisposed", detail);
        }

        // ============================================================
        // 用例 9：系统读回实际的角偏好（真实 DWM 调用，不走钩子）
        // ============================================================

        private void RunDwmReadback(Form form, WindowAppearanceController controller)
        {
            ProbeCase c = NewCase("dwm_corner_preference_readback");
            Rectangle bounds = new Rectangle(0, 0, 240, 160);

            controller.DwmSetWindowAttributeInterceptor = null;   // 走真实 DWM 调用

            controller.Apply(BuildOptions(1, 16, 2, bounds));
            bool chamferSupported = controller.TryGetCurrentCornerPreference(out int chamferValue);
            c.Detail("chamferReadbackSupported", chamferSupported);
            c.Detail("chamferReadback", chamferValue);
            if (chamferSupported)
            {
                c.Require(chamferValue == 1, $"切角时系统读回的角偏好应为 1，实际 {chamferValue}");
            }

            controller.Apply(BuildOptions(2, 16, 2, bounds));
            bool roundedSupported = controller.TryGetCurrentCornerPreference(out int roundedValue);
            c.Detail("roundedReadbackSupported", roundedSupported);
            c.Detail("roundedReadback", roundedValue);
            if (roundedSupported)
            {
                c.Require(roundedValue == 2, $"圆角时系统读回的角偏好应为 2，实际 {roundedValue}");
            }

            controller.Apply(BuildOptions(0, 16, 2, bounds));
            bool noneSupported = controller.TryGetCurrentCornerPreference(out int noneValue);
            c.Detail("noneReadbackSupported", noneSupported);
            c.Detail("noneReadback", noneValue);
            if (noneSupported)
            {
                c.Require(noneValue == 2, $"无框时系统读回的角偏好应为 2，实际 {noneValue}");
            }

            controller.DwmSetWindowAttributeInterceptor = CaptureDwmCall;
        }

        // ============================================================
        // 用例 10：句柄销毁后不得再调用 Win32
        // ============================================================

        private void RunHandleDestroyed()
        {
            ProbeCase c = NewCase("handle_destroyed_no_win32");

            Form form = CreateProbeForm();
            using WindowAppearanceController controller = new WindowAppearanceController(form);
            form.HandleCreated += (_, __) => controller.OnHandleCreated();
            form.HandleDestroyed += (_, __) => controller.OnHandleDestroyed();

            controller.Apply(BuildOptions(1, 16, 2, new Rectangle(0, 0, 240, 160)));
            c.Require(controller.HasAppliedRegion, "初始应用后应有 Region");

            // 句柄重建
            bool recreated = TryRecreateHandle(form);
            c.Detail("recreateHandleInvoked", recreated);
            if (recreated)
            {
                c.Require(controller.HasAppliedRegion, "句柄重建后应重新应用 Region");
                c.Require(controller.Diagnostics.DwmCornerCalls >= 2, "句柄重建后应再次写 DWM 角偏好");
            }

            // 真正销毁
            int dwmBefore = controller.Diagnostics.DwmCornerCalls;
            form.Dispose();
            c.Require(!form.IsHandleCreated, "窗口销毁后不应再有句柄");

            Exception? err = null;
            try
            {
                controller.Apply(BuildOptions(1, 16, 2, new Rectangle(0, 0, 240, 160)));
            }
            catch (Exception ex)
            {
                err = ex;
            }

            c.Require(err == null, $"句柄销毁后调用 Apply 不应抛异常：{err?.GetType().Name} {err?.Message}");
            c.Require(controller.Diagnostics.DwmCornerCalls == dwmBefore, "句柄销毁后不得再调用 DWM");
            c.Require(controller.Diagnostics.SkippedNoHandle >= 1, "应记录一次“无句柄跳过”");

            c.Detail("dwmCallsBeforeDispose", dwmBefore);
            c.Detail("dwmCallsAfterDispose", controller.Diagnostics.DwmCornerCalls);
            c.Detail("skippedNoHandle", controller.Diagnostics.SkippedNoHandle);
        }

        // ============================================================
        // 用例 11：尺寸变化后切角长度必须按新高度重新夹紧（R4 反例）
        // ============================================================

        private void RunResizeReclampsCornerLength()
        {
            ProbeCase c = NewCase("resize_reclamps_corner_length");

            using Form form = CreateProbeForm(240, 80);
            using WindowAppearanceController controller = new WindowAppearanceController(form);
            MainFormWinHelper helper = new MainFormWinHelper(form);
            helper.AttachWindowAppearance(controller);

            // 模拟生产接线：快照提供者（与 MainForm 的 SyncAppearanceContext 同源）
            ProbeSnapshotSource source = new ProbeSnapshotSource(BuildChamferTheme(64));
            controller.SnapshotProvider = () => source.SnapshotFor(form);

            // ★ 唯一的 Resize 订阅（与 MainForm_Transparent.BindEvents 一致）
            int resizeCount = 0;
            form.Resize += (_, __) => { resizeCount++; helper.ApplyRoundedCorners(); };

            // ① 初始：小高度 → 切角被夹到 80 / 2 = 40
            ThemeAppearanceOptions small = source.Sync(form);
            c.Require(small.FrameLengthPx == 40,
                $"240×80 的切角长度应被夹到 40，实际 {small.FrameLengthPx}");
            controller.Apply(small, source.CornerRadiusPx);
            c.Require(controller.LastCornerLengthPx == 40,
                $"初始切角长度应为 40，实际 {controller.LastCornerLengthPx}");
            int smallCorner = RequireOutlineMatchesSnapshot(c, form, controller, small, "初始 240×80");

            Region? smallRegion = form.Region;
            float smallLeft = smallRegion != null ? ScanLeftAt(smallRegion, 5f) : -1f;
            c.Require(Math.Abs(smallLeft - (smallCorner - 5f)) <= 1f,
                $"240×80 下 y=5 处的左边界应为 {smallCorner - 5}，实际 {smallLeft}");
            c.Require(smallLeft < 50f,
                "240×80 时点 (50,5) 应落在旧 Region 内（复现验收方的测量前提）");

            // ② 变高：240×200 → 切角长度必须按新高度重算（64）
            form.Size = new Size(240, 200);
            c.Require(resizeCount >= 1,
                "尺寸变化后 Resize 事件没有触发（窗口轮廓不会重算）");

            ThemeAppearanceOptions tall = source.Sync(form);
            c.Require(tall.FrameLengthPx == 64,
                $"240×200 的切角长度应为 64，实际 {tall.FrameLengthPx}");
            c.Require(controller.LastCornerLengthPx == 64,
                $"窗口变高后切角长度必须按新高度重算为 64，实际 {controller.LastCornerLengthPx}" +
                "（沿用了旧高度下被夹紧的 40）");
            int tallCorner = RequireOutlineMatchesSnapshot(c, form, controller, tall, "变高后 240×200");
            c.Require(tallCorner == 64,
                $"240×200 下实际生效的切角边长应为 64，实际 {tallCorner}");

            Region? tallRegion = form.Region;
            float tallLeft = tallRegion != null ? ScanLeftAt(tallRegion, 5f) : -1f;
            c.Require(Math.Abs(tallLeft - (tallCorner - 5f)) <= 1f,
                $"240×200 下 y=5 处的左边界应为 {tallCorner - 5}，实际 {tallLeft}");
            c.Require(tallLeft > 50f,
                "窗口变高后点 (50,5) 必须落在新 Region 之外（它仍在旧 Region 内）");
            int grownCornerLength = controller.LastCornerLengthPx;

            // ③ 再缩小：切角长度必须重新被夹紧
            form.Size = new Size(240, 80);
            ThemeAppearanceOptions smallAgain = source.Sync(form);
            c.Require(smallAgain.FrameLengthPx == 40,
                $"回到 240×80 的切角长度应为 40，实际 {smallAgain.FrameLengthPx}");
            c.Require(controller.LastCornerLengthPx == 40,
                $"缩小后切角长度应重新被夹到 40，实际 {controller.LastCornerLengthPx}");
            RequireOutlineMatchesSnapshot(c, form, controller, smallAgain, "再缩小 240×80");

            c.Detail("initialCornerLengthPx", 40);
            c.Detail("grownCornerLengthPx", grownCornerLength);
            c.Detail("shrunkCornerLengthPx", controller.LastCornerLengthPx);
            c.Detail("resizeEvents", resizeCount);
        }

        // ============================================================
        // 用例 12：同尺寸换肤 → 切角长度随新主题更新（R4 反例）
        // ============================================================

        private void RunThemeSwitchUpdatesCornerLength()
        {
            ProbeCase c = NewCase("theme_switch_updates_corner_length");

            using Form form = CreateProbeForm(240, 200);
            using WindowAppearanceController controller = new WindowAppearanceController(form);
            MainFormWinHelper helper = new MainFormWinHelper(form);
            helper.AttachWindowAppearance(controller);

            ProbeSnapshotSource source = new ProbeSnapshotSource(BuildChamferTheme(16));
            controller.SnapshotProvider = () => source.SnapshotFor(form);

            int resizeCount = 0;
            form.Resize += (_, __) => { resizeCount++; helper.ApplyRoundedCorners(); };

            // ① 主题 A：切角 16
            ThemeAppearanceOptions themeA = source.Sync(form);
            c.Require(themeA.FrameLengthPx == 16,
                $"主题 A（frameLength=16）的切角长度应为 16，实际 {themeA.FrameLengthPx}");
            controller.Apply(themeA, source.CornerRadiusPx);
            RequireOutlineMatchesSnapshot(c, form, controller, themeA, "主题 A");

            // ② 同尺寸换肤 → 主题 B（切角 48）：尺寸没变，只有主题变了
            source.CurrentTheme = BuildChamferTheme(48);
            ThemeAppearanceOptions themeB = source.Sync(form);
            c.Require(themeB.FrameLengthPx == 48,
                $"主题 B（frameLength=48）的切角长度应为 48，实际 {themeB.FrameLengthPx}");
            c.Require(themeB.ClientBounds == themeA.ClientBounds,
                "换肤前后客户区尺寸应当相同（同尺寸换肤）");

            controller.Reapply();   // 生产上：换肤 → 轮廓重算走的就是这条路径
            c.Require(controller.LastCornerLengthPx == 48,
                $"同尺寸换肤后切角长度必须随新主题更新为 48，实际 {controller.LastCornerLengthPx}");
            RequireOutlineMatchesSnapshot(c, form, controller, themeB, "主题 B");

            // ③ 换肤后再变尺寸：事件路径也必须跟着新主题走
            form.Size = new Size(240, 201);
            c.Require(resizeCount >= 1, "尺寸变化后 Resize 事件没有触发");
            ThemeAppearanceOptions themeBResized = source.Sync(form);
            c.Require(controller.LastCornerLengthPx == themeBResized.FrameLengthPx,
                $"换肤后变尺寸的切角长度应为 {themeBResized.FrameLengthPx}，实际 {controller.LastCornerLengthPx}");
            RequireOutlineMatchesSnapshot(c, form, controller, themeBResized, "主题 B 变尺寸");

            // ④ 换回主题 A：切角长度必须回落
            source.CurrentTheme = BuildChamferTheme(16);
            controller.Reapply();
            ThemeAppearanceOptions themeAAgain = source.Sync(form);
            c.Require(controller.LastCornerLengthPx == 16,
                $"换回主题 A 后切角长度应为 16，实际 {controller.LastCornerLengthPx}");
            RequireOutlineMatchesSnapshot(c, form, controller, themeAAgain, "主题 A 回来");

            c.Detail("themeACornerLengthPx", 16);
            c.Detail("themeBCornerLengthPx", 48);
        }

        // ============================================================
        // 用例 13：布局切换（竖屏 → 横屏）→ 客户区高度变了，切角长度重算（R4 反例）
        // ============================================================

        private void RunLayoutSwitchUpdatesCornerLength()
        {
            ProbeCase c = NewCase("layout_switch_updates_corner_length");

            using Form form = CreateProbeForm(240, 100);   // 竖屏：高度小 → 切角被夹到 50
            using WindowAppearanceController controller = new WindowAppearanceController(form);
            MainFormWinHelper helper = new MainFormWinHelper(form);
            helper.AttachWindowAppearance(controller);

            ProbeSnapshotSource source = new ProbeSnapshotSource(BuildChamferTheme(64));
            controller.SnapshotProvider = () => source.SnapshotFor(form);

            int resizeCount = 0;
            form.Resize += (_, __) => { resizeCount++; helper.ApplyRoundedCorners(); };

            // ① 竖屏 240×100 → 切角被夹到 50
            ThemeAppearanceOptions portrait = source.Sync(form);
            c.Require(portrait.FrameLengthPx == 50,
                $"竖屏 240×100 的切角长度应被夹到 50，实际 {portrait.FrameLengthPx}");
            controller.Apply(portrait, source.CornerRadiusPx);
            RequireOutlineMatchesSnapshot(c, form, controller, portrait, "竖屏 240×100");

            // ② 切到横屏 900×240 → 高度变大，切角不再被夹紧（64）
            form.Size = new Size(900, 240);
            c.Require(resizeCount >= 1, "布局切换后 Resize 事件没有触发");
            ThemeAppearanceOptions landscape = source.Sync(form);
            c.Require(landscape.FrameLengthPx == 64,
                $"横屏 900×240 的切角长度应为 64，实际 {landscape.FrameLengthPx}");
            c.Require(controller.LastCornerLengthPx == 64,
                $"布局切换后切角长度必须按新高度重算为 64，实际 {controller.LastCornerLengthPx}");
            RequireOutlineMatchesSnapshot(c, form, controller, landscape, "横屏 900×240");

            // ③ 再切回竖屏 → 切角重新被夹紧（50）
            form.Size = new Size(240, 100);
            ThemeAppearanceOptions portraitAgain = source.Sync(form);
            c.Require(portraitAgain.FrameLengthPx == 50,
                $"切回竖屏 240×100 的切角长度应为 50，实际 {portraitAgain.FrameLengthPx}");
            c.Require(controller.LastCornerLengthPx == 50,
                $"切回竖屏后切角长度应重新被夹到 50，实际 {controller.LastCornerLengthPx}");
            RequireOutlineMatchesSnapshot(c, form, controller, portraitAgain, "切回竖屏 240×100");

            c.Detail("portraitCornerLengthPx", 50);
            c.Detail("landscapeCornerLengthPx", 64);
            c.Detail("resizeEvents", resizeCount);
        }

        // ============================================================
        // 用例 14：Resize 订阅仍然只有一处（A13 的硬约束）
        // ============================================================

        private void RunResizeBindingUnique()
        {
            ProbeCase c = NewCase("resize_binding_unique");

            string? root = FindRepoRoot();
            if (root == null)
            {
                c.Detail("skipped", "未找到仓库根目录（LiteMonitor.csproj），跳过源码扫描");
                return;
            }

            // 只扫窗口轮廓接线涉及的这三个文件：
            //   Resize 绑定必须只有一处（MainForm_Transparent），控制器与 helper 都不得再绑。
            string[] files =
            {
                Path.Combine(root, "src", "UI", "MainForm_Transparent.cs"),
                Path.Combine(root, "src", "UI", "Helpers", "MainFormWinHelper.cs"),
                Path.Combine(root, "src", "UI", "Helpers", "WindowAppearanceController.cs")
            };

            JsonArray arr = new();
            int total = 0;
            foreach (string file in files)
            {
                if (!File.Exists(file))
                {
                    c.Fail("找不到源文件：" + file);
                    continue;
                }

                string text = File.ReadAllText(file, Encoding.UTF8);
                int count = CountOccurrences(text, "Resize +=");
                total += count;
                arr.Add(new JsonObject
                {
                    ["file"] = Path.GetFileName(file),
                    ["resizeSubscriptions"] = count
                });
            }

            c.Require(total == 1, $"窗口轮廓的 Resize 订阅必须只有一处，实际 {total} 处");
            c.Detail("files", arr);
            c.Detail("total", total);
        }

        // ============================================================
        // 辅助
        // ============================================================

        /// <summary>
        /// 模拟生产接线里的"当前外观快照"来源。
        /// <para>
        /// 与 MainForm 的 <c>SyncAppearanceContext()</c> 同构：用**当前**客户区尺寸重建快照，
        /// 因此快照里的 <c>FrameLengthPx</c> 一定是按当前高度夹紧过的值。
        /// </para>
        /// </summary>
        private sealed class ProbeSnapshotSource
        {
            private Theme _theme;

            public ProbeSnapshotSource(Theme theme, int cornerRadiusPx = 8)
            {
                _theme = theme;
                CornerRadiusPx = cornerRadiusPx;
            }

            /// <summary>圆角半径（物理像素，与绘制层外框用同一个值）。</summary>
            public int CornerRadiusPx { get; }

            /// <summary>当前主题（换肤时替换）。</summary>
            public Theme CurrentTheme
            {
                get => _theme;
                set => _theme = value;
            }

            /// <summary>用当前客户区尺寸重建快照（同 AppearanceRuntime.Update 的语义）。</summary>
            public ThemeAppearanceOptions Sync(Form form)
            {
                return ThemeAppearanceOptions.Create(_theme, 1f, 1f, form.ClientRectangle, 1);
            }

            /// <summary>控制器用的快照提供者委托（与 MainForm 的挂载方式一致）。</summary>
            public WindowAppearanceSnapshot? SnapshotFor(Form form)
            {
                return new WindowAppearanceSnapshot(Sync(form), CornerRadiusPx);
            }
        }

        /// <summary>构造一个切角主题（frameLength 为 96 DPI 逻辑值）。</summary>
        private static Theme BuildChamferTheme(int frameLength)
        {
            return new Theme
            {
                Name = "probe-chamfer",
                Layout = new LayoutConfig
                {
                    FrameStyle = ThemeAppearanceOptions.FrameStyleChamfer,
                    FrameLength = frameLength,
                    FrameWidth = 8,
                    CornerRadius = 8
                }
            };
        }

        /// <summary>
        /// 断言"窗口轮廓几何 == 当前快照推导出的几何"。
        /// <para>
        /// 期望几何由**当前快照**独立推导：切角边长 = <c>options.FrameLengthPx</c>，
        /// 客户区 = <c>options.ClientBounds</c>；再按几何层的夹紧规则（避免重合顶点）
        /// 算出实际生效的切角边长 —— 与绘制层用的是同一份快照与同一组参数。
        /// </para>
        /// </summary>
        /// <returns>实际生效的切角边长（物理像素）。</returns>
        private int RequireOutlineMatchesSnapshot(
            ProbeCase c,
            Form form,
            WindowAppearanceController controller,
            ThemeAppearanceOptions snapshot,
            string label)
        {
            // ① 轮廓用的切角边长必须就是当前快照里的值（不能是旧尺寸下被夹紧的值）
            c.Require(controller.LastCornerLengthPx == snapshot.FrameLengthPx,
                $"{label}: 轮廓用的切角边长应为当前快照的 {snapshot.FrameLengthPx}，实际 {controller.LastCornerLengthPx}");

            Region? actual = form.Region;
            c.Require(actual != null, $"{label}: 应有 Region");
            if (actual == null) return snapshot.FrameLengthPx;

            Rectangle expect = snapshot.ClientBounds;

            // ② Region 实际边界 == 快照里的客户区
            RectangleF bounds = GetRegionBounds(actual);
            c.Require(
                Math.Abs(bounds.X - expect.X) < 0.5f && Math.Abs(bounds.Y - expect.Y) < 0.5f
                && Math.Abs(bounds.Width - expect.Width) < 0.5f && Math.Abs(bounds.Height - expect.Height) < 0.5f,
                $"{label}: Region 实际边界 {bounds.Width}x{bounds.Height}@({bounds.X},{bounds.Y})" +
                $" 与快照客户区 {expect.Width}x{expect.Height}@({expect.X},{expect.Y}) 不一致");

            // ③ Region 实际轮廓 == 由当前快照独立推导出来的切角几何
            int corner = DecorGeometry.ClampCornerLengthPx(snapshot.FrameLengthPx, expect);
            using Region reference = BuildReferenceChamfer(expect, corner);
            string diff = CompareScans(actual, reference);
            c.Require(diff.Length == 0, $"{label}: Region 实际轮廓与当前快照推导的轮廓不一致（{diff}）");

            // ④ 顶部切角位置 == 当前快照推导出的切角边长
            float scanX = FirstScanX(actual);
            c.Require(Math.Abs(scanX - corner) < 0.5f,
                $"{label}: 顶部切角位置应为 {corner} 物理像素，实际 {scanX}");

            return corner;
        }

        /// <summary>比较两个 Region 的扫描线几何（逐条对比 X / Y / 宽 / 高）。</summary>
        private static string CompareScans(Region actual, Region expected)
        {
            RectangleF[] a = actual.GetRegionScans(new Matrix());
            RectangleF[] e = expected.GetRegionScans(new Matrix());
            if (a.Length != e.Length) return $"扫描线条数不同（实际 {a.Length} / 期望 {e.Length}）";

            for (int i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i].X - e[i].X) > 0.5f || Math.Abs(a[i].Y - e[i].Y) > 0.5f
                    || Math.Abs(a[i].Width - e[i].Width) > 0.5f || Math.Abs(a[i].Height - e[i].Height) > 0.5f)
                {
                    return $"第 {i} 条扫描线不同" +
                           $"（实际 {a[i].X},{a[i].Y},{a[i].Width},{a[i].Height}" +
                           $" / 期望 {e[i].X},{e[i].Y},{e[i].Width},{e[i].Height}）";
                }
            }

            return "";
        }

        /// <summary>取指定 y 所在扫描线的左边界（物理像素）；找不到返回 -1。</summary>
        private static float ScanLeftAt(Region region, float y)
        {
            foreach (RectangleF r in region.GetRegionScans(new Matrix()))
            {
                if (y >= r.Y && y < r.Y + r.Height) return r.X;
            }

            return -1f;
        }

        /// <summary>向上找仓库根目录（以 LiteMonitor.csproj 为准）。</summary>
        private static string? FindRepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }

            return null;
        }

        /// <summary>统计子串出现次数。</summary>
        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private ProbeCase NewCase(string name)
        {
            ProbeCase c = new ProbeCase { Name = name };
            _cases.Add(c);
            return c;
        }

        /// <summary>DWM 写入捕获（局部函数：lambda 不允许 ref 参数）。</summary>
        private int CaptureDwmCall(IntPtr hwnd, int attribute, ref int value, int size)
        {
            _dwmCalls.Add((attribute, value));
            return 0;
        }

        private int LastCornerPreferenceValue()
        {
            for (int i = _dwmCalls.Count - 1; i >= 0; i--)
            {
                if (_dwmCalls[i].attribute == WindowAppearanceController.DwmwaWindowCornerPreference)
                {
                    return _dwmCalls[i].value;
                }
            }
            return -1;
        }

        private List<int> CornerPreferenceValues()
        {
            List<int> list = new();
            foreach ((int attribute, int value) in _dwmCalls)
            {
                if (attribute == WindowAppearanceController.DwmwaWindowCornerPreference) list.Add(value);
            }
            return list;
        }

        private static string DescribeState(Form form, WindowAppearanceController controller)
        {
            return $"strategy={controller.LastStrategy},corner={controller.LastCornerPreference},hasRegion={(form.Region != null)}";
        }

        /// <summary>
        /// 取 Region 最上一行扫描线的左边界 = 顶部切角的实际位置（物理像素）。
        /// </summary>
        private static float FirstScanX(Region region)
        {
            RectangleF[] scans = region.GetRegionScans(new Matrix());
            return scans.Length > 0 ? scans[0].X : -1f;
        }

        /// <summary>
        /// 取 Region 的边界（.NET 8 的 Region.GetBounds 需要一个 Graphics；
        /// 这里用 1×1 位图的 Graphics，世界坐标与像素 1:1）。
        /// </summary>
        private RectangleF GetRegionBounds(Region region)
        {
            if (_probeGraphics == null)
            {
                _probeBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
                _probeGraphics = Graphics.FromImage(_probeBitmap);
            }
            return region.GetBounds(_probeGraphics);
        }

        private static Form CreateProbeForm(int w = 240, int h = 160)
        {
            Form form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                Size = new Size(w, h)
            };
            _ = form.Handle;   // 只创建句柄，不显示（不打扰桌面、不做屏幕捕获）
            return form;
        }

        private static ThemeAppearanceOptions BuildOptions(
            int frameStyle, int frameLength, int frameWidth, Rectangle bounds, float dpiScale = 1.0f)
        {
            Theme theme = new Theme
            {
                Name = "probe",
                Layout = new LayoutConfig
                {
                    FrameStyle = frameStyle,
                    FrameLength = frameLength,
                    FrameWidth = frameWidth
                }
            };
            return ThemeAppearanceOptions.Create(theme, dpiScale, 1.0f, bounds, 1);
        }

        private static bool TryRecreateHandle(Form form)
        {
            try
            {
                MethodInfo? mi = typeof(Form).GetMethod("RecreateHandle",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (mi == null) return false;
                mi.Invoke(form, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RunOnStaThread(Action action)
        {
            Exception? error = null;
            Thread thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (error != null) throw error;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    }
}
