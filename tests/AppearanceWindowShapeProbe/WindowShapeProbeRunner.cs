using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        // 辅助
        // ============================================================

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
