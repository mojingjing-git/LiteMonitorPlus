using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 【P-F05】任务栏方向、坐标契约与占位幂等的行为测试。
    /// <para>★ 覆盖 #568（任务栏位置改变且重叠）、#539（切分辨率后位置/布局乱跳），
    /// 并为 #573 / #583 的"几何正确性"提供前置保证。</para>
    /// <para>★ 取证方式分两层：</para>
    /// <list type="bullet">
    /// <item><b>纯逻辑层</b>：<c>TaskbarDockResolver</c>（方向解析）与
    /// <c>TaskbarReservationModel</c>（基线 + 占位量）—— 直接调用**产品代码**，
    /// 不复制算法自证；用于覆盖四方向切换、DPI 矩阵、幂等与场景回归。</item>
    /// <item><b>真实 Win32 层</b>：复用 P-F04 的 <c>TaskbarWin32TestHost</c>
    /// 创建真实的"任务栏根 → ReBar → 任务列表"窗口层级，驱动
    /// <c>TaskbarStrategyWin10</c> / <c>TaskbarStrategyWin11</c>，
    /// 断言**真实窗口矩形**。测试宿主只创建自己的窗口，从不触碰真实 Shell_TrayWnd。</item>
    /// </list>
    /// <para>★ 全程不用 Thread.Sleep：不需要任何真实等待。</para>
    /// </summary>
    public class TaskbarGeometryTests
    {
        private const string PrimaryDevice = @"\\.\DISPLAY1";

        // =================================================================
        // 夹具
        // =================================================================

        /// <summary>可变的假显示器拓扑（生产实现来自 <see cref="Screen"/>）。</summary>
        private sealed class FakeScreenProvider : ITaskbarScreenProvider
        {
            private readonly List<TaskbarMonitorInfo> _monitors = new List<TaskbarMonitorInfo>();

            public FakeScreenProvider(params TaskbarMonitorInfo[] monitors)
            {
                _monitors.AddRange(monitors);
            }

            public void SetMonitors(params TaskbarMonitorInfo[] monitors)
            {
                _monitors.Clear();
                _monitors.AddRange(monitors);
            }

            public IReadOnlyList<TaskbarMonitorInfo> GetMonitors() => _monitors.ToList();

            public TaskbarMonitorInfo? GetMonitorForHandle(IntPtr hWnd)
            {
                if (hWnd == IntPtr.Zero) return null;
                Rectangle rect = TaskbarWin32TestHost.GetBounds(hWnd);
                if (rect.IsEmpty) return null;
                return GetMonitorForRectangle(rect);
            }

            public TaskbarMonitorInfo? GetMonitorForRectangle(Rectangle rect)
            {
                TaskbarMonitorInfo? best = null;
                long bestArea = 0;
                foreach (var monitor in _monitors)
                {
                    Rectangle overlap = Rectangle.Intersect(monitor.Bounds, rect);
                    long area = (long)overlap.Width * overlap.Height;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = monitor;
                    }
                }
                return best;
            }
        }

        /// <summary>
        /// 假的任务栏窗口查找来源：把"任务栏根 / ReBar / 任务列表"映射到测试宿主自己创建的窗口。
        /// <para>★ 绝不查找、也绝不触碰真实的 <c>Shell_TrayWnd</c>。</para>
        /// </summary>
        private sealed class FakeWindowSource : ITaskbarWindowSource
        {
            public IntPtr TaskbarRoot { get; set; }
            public IntPtr ReBar { get; set; }
            public IntPtr TaskList { get; set; }

            public IntPtr FindWindow(string className, string windowName)
            {
                if (className == "Shell_TrayWnd") return TaskbarRoot;
                return IntPtr.Zero;
            }

            public IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName)
            {
                if (className == "ReBarWindow32" && parent == TaskbarRoot) return ReBar;
                if (className == "WorkerW" && parent == TaskbarRoot) return IntPtr.Zero;
                if (className == "MSTaskSwWClass" && parent == ReBar) return TaskList;
                if (className == "MSTaskListWClass" && parent == ReBar) return IntPtr.Zero;
                return IntPtr.Zero;
            }
        }

        /// <summary>真实的面板窗体（需要真实 HWND 才能验证父/子关系与定位）。</summary>
        private sealed class PanelForm : Form
        {
            public PanelForm()
            {
                // ★ 无边框：否则 WinForms 会强制最小窗口高度（39px），影响坐标断言
                FormBorderStyle = FormBorderStyle.None;
            }
        }

        // =================================================================
        // 一、停靠方向解析（显式枚举，不用宽高比猜）
        // =================================================================

        [Theory]
        [InlineData(0, 1032, 1920, 48, TaskbarDockDirection.Bottom)]
        [InlineData(0, 0, 1920, 48, TaskbarDockDirection.Top)]
        [InlineData(0, 0, 48, 1080, TaskbarDockDirection.Left)]
        [InlineData(1872, 0, 48, 1080, TaskbarDockDirection.Right)]
        public void 停靠方向按任务栏相对显示器的物理矩形解析(int x, int y, int w, int h, TaskbarDockDirection expected)
        {
            Rectangle monitor = new Rectangle(0, 0, 1920, 1080);
            Rectangle work = new Rectangle(0, 0, 1920, 1032);
            Rectangle bar = new Rectangle(x, y, w, h);

            Assert.Equal(expected, TaskbarDockResolver.Resolve(bar, monitor, work));
        }

        [Theory]
        [InlineData(-1920, 0, TaskbarDockDirection.Bottom)]
        [InlineData(-1920, 0, TaskbarDockDirection.Top)]
        [InlineData(0, -1080, TaskbarDockDirection.Bottom)]
        [InlineData(0, -1080, TaskbarDockDirection.Left)]
        public void 负屏幕原点下停靠方向解析依然正确(int originX, int originY, TaskbarDockDirection expected)
        {
            Rectangle monitor = new Rectangle(originX, originY, 1920, 1080);

            // 任务栏条带按方向摆在对应边上（负原点 ⇒ 全部为负坐标）
            Rectangle bar = expected switch
            {
                TaskbarDockDirection.Bottom => new Rectangle(originX, originY + 1032, 1920, 48),
                TaskbarDockDirection.Top => new Rectangle(originX, originY, 1920, 48),
                TaskbarDockDirection.Left => new Rectangle(originX, originY, 48, 1080),
                _ => new Rectangle(originX + 1872, originY, 48, 1080),
            };

            Rectangle work = expected switch
            {
                TaskbarDockDirection.Bottom => new Rectangle(originX, originY, 1920, 1032),
                TaskbarDockDirection.Top => new Rectangle(originX, originY + 48, 1920, 1032),
                TaskbarDockDirection.Left => new Rectangle(originX + 48, originY, 1872, 1080),
                _ => new Rectangle(originX, originY, 1872, 1080),
            };

            Assert.Equal(expected, TaskbarDockResolver.Resolve(bar, monitor, work));
        }

        [Fact]
        public void 浮动任务栏且工作区无预留时方向为未知而不是靠宽高比猜()
        {
            // 一个 400×40 的浮动任务栏：既不贴边，工作区也没有预留条带
            Rectangle monitor = new Rectangle(0, 0, 1920, 1080);
            Rectangle work = new Rectangle(0, 0, 1920, 1080);
            Rectangle bar = new Rectangle(600, 500, 400, 40);

            Assert.Equal(TaskbarDockDirection.Unknown, TaskbarDockResolver.Resolve(bar, monitor, work));
        }

        [Fact]
        public void 任务栏未贴边时由工作区预留条带判定方向()
        {
            Rectangle monitor = new Rectangle(0, 0, 1920, 1080);
            Rectangle work = new Rectangle(0, 0, 1920, 1032);   // 底部预留 48px
            Rectangle bar = new Rectangle(100, 1000, 400, 40);   // 不贴边

            Assert.Equal(TaskbarDockDirection.Bottom, TaskbarDockResolver.Resolve(bar, monitor, work));
        }

        [Fact]
        public void 左侧停靠由工作区预留条带判定方向()
        {
            Rectangle monitor = new Rectangle(0, 0, 1920, 1080);
            Rectangle work = new Rectangle(60, 0, 1860, 1080);   // 左侧预留 60px
            Rectangle bar = new Rectangle(10, 300, 40, 300);     // 不贴边

            Assert.Equal(TaskbarDockDirection.Left, TaskbarDockResolver.Resolve(bar, monitor, work));
        }

        [Fact]
        public void 原生系统只提供底部停靠其余方向标注为第三方兼容性待验()
        {
            Assert.Equal(TaskbarCompatibility.Native, TaskbarDockResolver.ResolveCompatibility(TaskbarDockDirection.Bottom));
            Assert.Equal(TaskbarCompatibility.ThirdPartyPending, TaskbarDockResolver.ResolveCompatibility(TaskbarDockDirection.Top));
            Assert.Equal(TaskbarCompatibility.ThirdPartyPending, TaskbarDockResolver.ResolveCompatibility(TaskbarDockDirection.Left));
            Assert.Equal(TaskbarCompatibility.ThirdPartyPending, TaskbarDockResolver.ResolveCompatibility(TaskbarDockDirection.Right));
            Assert.Equal(TaskbarCompatibility.Unknown, TaskbarDockResolver.ResolveCompatibility(TaskbarDockDirection.Unknown));
        }

        // =================================================================
        // 二、几何缓存键（任一分量变化 ⇒ 整体失效）
        // =================================================================

        private static TaskbarGeometryKey SampleKey() => new TaskbarGeometryKey(
            rootHandle: new IntPtr(11),
            innerHandle: new IntPtr(22),
            deviceName: PrimaryDevice,
            screenBounds: new Rectangle(0, 0, 1920, 1080),
            workingArea: new Rectangle(0, 0, 1920, 1032),
            dpi: 96,
            direction: TaskbarDockDirection.Bottom,
            panelWidth: 300,
            panelHeight: 48,
            alignLeft: true,
            manualOffset: 0,
            centerAligned: true,
            widgetsWidth: 150);

        [Fact]
        public void 几何缓存键在任一分量变化时整体失效()
        {
            TaskbarGeometryKey baseline = SampleKey();
            Assert.Equal(baseline, SampleKey());

            var variants = new List<TaskbarGeometryKey>
            {
                new TaskbarGeometryKey(new IntPtr(99), baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, new IntPtr(99), baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, @"\\.\DISPLAY2", baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, new Rectangle(0, 0, 2560, 1440), baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, new Rectangle(0, 0, 1920, 1000), baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, 144, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, TaskbarDockDirection.Left, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, 301, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, 49, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, false, baseline.ManualOffset, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, 8, baseline.CenterAligned, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, false, baseline.WidgetsWidth),
                new TaskbarGeometryKey(baseline.RootHandle, baseline.InnerHandle, baseline.DeviceName, baseline.ScreenBounds, baseline.WorkingArea, baseline.Dpi, baseline.Direction, baseline.PanelWidth, baseline.PanelHeight, baseline.AlignLeft, baseline.ManualOffset, baseline.CenterAligned, 0),
            };

            foreach (var variant in variants)
            {
                Assert.NotEqual(baseline, variant);
            }
        }

        // =================================================================
        // 三、占位模型：幂等 / 场景回归 / 启停 / 四方向切换 / DPI 矩阵
        // =================================================================

        /// <summary>
        /// 复刻 TASK-4 报告探针 P2c 的输入序列：
        /// 任务列表自然宽 1200（relLeft 600）→ 面板 300px。
        /// </summary>
        private static Rectangle ProbeNaturalTaskList() => new Rectangle(600, 0, 1200, 48);

        [Fact]
        public void 任务列表1200变640且面板600变900的场景不再复现()
        {
            var model = new TaskbarReservationModel();
            Rectangle observed = ProbeNaturalTaskList();

            // tick 1：系统任务列表 1200（relLeft 600）
            Assert.True(model.TryCompute(observed, TaskbarShrinkAxis.Horizontal, 300, 0, 1920, TaskbarShrinkMode.FromStart, out var applied1, out var panelStart1));
            Assert.Equal(900, applied1.Left);        // 图标右移到面板右侧
            Assert.Equal(900, applied1.Width);       // 1200 - 300
            Assert.Equal(600, panelStart1);          // 面板仍在 600
            model.Commit(applied1, TaskbarShrinkAxis.Horizontal);

            // tick 2：稳定
            Assert.True(model.TryCompute(applied1, TaskbarShrinkAxis.Horizontal, 300, 0, 1920, TaskbarShrinkMode.FromStart, out var applied2, out var panelStart2));
            Assert.Equal(applied1, applied2);
            Assert.Equal(600, panelStart2);
            model.Commit(applied2, TaskbarShrinkAxis.Horizontal);

            // tick 3：系统把任务列表改成 940（用户开了一个任务栏应用）——旧实现在这里
            //         把 940 当成"原始宽度"再扣 300 ⇒ 640，并把图标推到 1200。
            Rectangle observed3 = new Rectangle(900, 0, 940, 48);
            Assert.True(model.TryCompute(observed3, TaskbarShrinkAxis.Horizontal, 300, 0, 1920, TaskbarShrinkMode.FromStart, out var applied3, out var panelStart3));

            Assert.NotEqual(1200, applied3.Left);     // 图标没有再右移 600px（600 → 1200）
            Assert.Equal(900, applied3.Left);
            Assert.NotEqual(640, applied3.Width);     // 任务列表没有被压到 640
            Assert.Equal(940, applied3.Width);        // 施加后的宽度 == 系统给的宽度
            Assert.Equal(600, panelStart3);           // 面板没有从 600 跳到 900
            model.Commit(applied3, TaskbarShrinkAxis.Horizontal);

            // tick 4：系统又改成 980 —— 依然不得二次扣减
            Rectangle observed4 = new Rectangle(900, 0, 980, 48);
            Assert.True(model.TryCompute(observed4, TaskbarShrinkAxis.Horizontal, 300, 0, 1920, TaskbarShrinkMode.FromStart, out var applied4, out var panelStart4));
            Assert.Equal(980, applied4.Width);
            Assert.Equal(900, applied4.Left);
            Assert.Equal(600, panelStart4);
        }

        [Fact]
        public void 重复Tick一百次不产生累计漂移()
        {
            var model = new TaskbarReservationModel();
            Rectangle observed = ProbeNaturalTaskList();

            Assert.True(model.TryCompute(observed, TaskbarShrinkAxis.Horizontal, 300, 0, 1920, TaskbarShrinkMode.FromStart, out var applied, out _));
            model.Commit(applied, TaskbarShrinkAxis.Horizontal);
            Rectangle first = applied;

            for (int i = 0; i < 100; i++)
            {
                Assert.True(model.TryCompute(applied, TaskbarShrinkAxis.Horizontal, 300, 0, 1920, TaskbarShrinkMode.FromStart, out applied, out int panelStart));
                model.Commit(applied, TaskbarShrinkAxis.Horizontal);
                Assert.Equal(first, applied);
                Assert.Equal(600, panelStart);
            }
        }

        [Fact]
        public void 重复启停一百次不缩窄任务列表()
        {
            var model = new TaskbarReservationModel();
            Rectangle natural = ProbeNaturalTaskList();
            Rectangle observed = natural;

            for (int i = 0; i < 100; i++)
            {
                // 启用：施加占位
                Assert.True(model.TryCompute(observed, TaskbarShrinkAxis.Horizontal, 300, 0, 1920, TaskbarShrinkMode.FromStart, out var applied, out _));
                model.Commit(applied, TaskbarShrinkAxis.Horizontal);
                Assert.True(applied.Width < natural.Width);

                // 停用：撤销占位（把基线原样还回去）
                Assert.True(model.TryTakeBaseline(out var baseline));
                Assert.Equal(natural, baseline);
                observed = baseline;
            }

            Assert.Equal(natural, observed);
        }

        [Fact]
        public void 四方向连续切换不产生累计漂移()
        {
            var model = new TaskbarReservationModel();

            var cases = new[]
            {
                (Axis: TaskbarShrinkAxis.Horizontal, Mode: TaskbarShrinkMode.FromStart, Natural: new Rectangle(600, 0, 1120, 48)),
                (Axis: TaskbarShrinkAxis.Vertical,   Mode: TaskbarShrinkMode.FromStart, Natural: new Rectangle(0, 40, 60, 940)),
                (Axis: TaskbarShrinkAxis.Vertical,   Mode: TaskbarShrinkMode.FromEnd,   Natural: new Rectangle(0, 40, 60, 940)),
                (Axis: TaskbarShrinkAxis.Horizontal, Mode: TaskbarShrinkMode.FromEnd,   Natural: new Rectangle(600, 0, 1120, 48)),
            };

            for (int round = 0; round < 25; round++)
            {
                foreach (var c in cases)
                {
                    int parentExtent = c.Axis == TaskbarShrinkAxis.Horizontal ? 1920 : 1080;
                    int panelExtent = c.Axis == TaskbarShrinkAxis.Horizontal ? 300 : 200;

                    // ★ 方向/轴变化 ⇒ 系统会按新朝向重新摆放任务列表
                    //   （真实场景里 Explorer 会重建任务列表窗口）。
                    Rectangle observed = c.Natural;

                    Assert.True(model.TryCompute(observed, c.Axis, panelExtent, 0, parentExtent, c.Mode, out var applied, out _));
                    model.Commit(applied, c.Axis);

                    // 系统没有改动 ⇒ 幂等
                    Assert.True(model.TryCompute(applied, c.Axis, panelExtent, 0, parentExtent, c.Mode, out var again, out _));
                    Assert.Equal(applied, again);
                    model.Commit(again, c.Axis);

                    // 方向/轴变化 ⇒ 撤销旧占位后重新取基线（基线必须回到自然矩形）
                    Assert.True(model.TryTakeBaseline(out var baseline));
                    Assert.Equal(c.Natural, baseline);
                }
            }
        }

        [Fact]
        public void 混合缩放与负屏幕原点与非默认工作区下无重叠且不越界()
        {
            int[] dpiScales = { 96, 144, 192 };
            Rectangle[] origins =
            {
                new Rectangle(0, 0, 1920, 1080),
                new Rectangle(-1920, 0, 1920, 1080),
                new Rectangle(0, -1080, 1920, 1080),
            };
            TaskbarDockDirection[] directions =
            {
                TaskbarDockDirection.Bottom,
                TaskbarDockDirection.Top,
                TaskbarDockDirection.Left,
                TaskbarDockDirection.Right,
            };

            foreach (int dpi in dpiScales)
            {
                foreach (Rectangle monitor in origins)
                {
                    foreach (TaskbarDockDirection direction in directions)
                    {
                        int thickness = (int)Math.Round(48.0 * dpi / 96.0);
                        int panelExtent = (int)Math.Round(300.0 * dpi / 96.0);

                        // ① 任务栏条带（物理像素）
                        Rectangle strip = direction switch
                        {
                            TaskbarDockDirection.Bottom => new Rectangle(monitor.Left, monitor.Bottom - thickness, monitor.Width, thickness),
                            TaskbarDockDirection.Top => new Rectangle(monitor.Left, monitor.Top, monitor.Width, thickness),
                            TaskbarDockDirection.Left => new Rectangle(monitor.Left, monitor.Top, thickness, monitor.Height),
                            _ => new Rectangle(monitor.Right - thickness, monitor.Top, thickness, monitor.Height),
                        };

                        // ② 非默认工作区：额外在对面留出 24px（模拟常驻小工具）
                        Rectangle work = direction switch
                        {
                            TaskbarDockDirection.Bottom => new Rectangle(monitor.Left, monitor.Top + 24, monitor.Width, monitor.Height - thickness - 24),
                            TaskbarDockDirection.Top => new Rectangle(monitor.Left, monitor.Top + thickness + 24, monitor.Width, monitor.Height - thickness - 24),
                            TaskbarDockDirection.Left => new Rectangle(monitor.Left + thickness + 24, monitor.Top, monitor.Width - thickness - 24, monitor.Height),
                            _ => new Rectangle(monitor.Left + 24, monitor.Top, monitor.Width - thickness - 24, monitor.Height),
                        };

                        // ③ 方向解析必须与摆放方向一致
                        Assert.Equal(direction, TaskbarDockResolver.Resolve(strip, monitor, work));

                        // ④ 父（ReBar）客户区 + 任务列表的自然矩形
                        bool vertical = direction == TaskbarDockDirection.Left || direction == TaskbarDockDirection.Right;
                        Rectangle parent = strip;
                        Rectangle natural = vertical
                            ? new Rectangle(0, 40, parent.Width, parent.Height - 140)
                            : new Rectangle(600, 0, parent.Width - 800, parent.Height);

                        var model = new TaskbarReservationModel();
                        var axis = vertical ? TaskbarShrinkAxis.Vertical : TaskbarShrinkAxis.Horizontal;
                        int parentExtent = vertical ? parent.Height : parent.Width;
                        int extent = vertical ? (int)Math.Round(200.0 * dpi / 96.0) : panelExtent;

                        Assert.True(model.TryCompute(natural, axis, extent, 0, parentExtent, TaskbarShrinkMode.FromStart, out var applied, out int panelStart));

                        Rectangle panel = vertical
                            ? new Rectangle((parent.Width - parent.Width) / 2, panelStart, parent.Width, extent)
                            : new Rectangle(panelStart, (parent.Height - parent.Height) / 2, extent, parent.Height);

                        // ⑤ 不重叠（注意：Rectangle.Intersect 对"刚好相接"返回宽度 0 的矩形，
                        //    因此这里判宽度/高度 <= 0，而不是 IsEmpty）
                        Rectangle overlap = Rectangle.Intersect(panel, applied);
                        Assert.True(
                            overlap.Width <= 0 || overlap.Height <= 0,
                            $"方向={direction} dpi={dpi} 原点={monitor.Location}：面板与任务列表重叠 {panel} ∩ {applied}");

                        // ⑥ 不越界（两者都必须落在父**客户区**内 ⇒ 用客户区尺寸比较）
                        Rectangle parentClient = new Rectangle(0, 0, parent.Width, parent.Height);
                        Assert.True(parentClient.Contains(panel), $"方向={direction} dpi={dpi}：面板越界 {panel}，父客户区={parentClient}");
                        Assert.True(parentClient.Contains(applied), $"方向={direction} dpi={dpi}：任务列表越界 {applied}，父客户区={parentClient}");

                        // ⑦ 施加后不宽于自然尺寸（占位只会变小），且尺寸仍为正
                        Assert.True(applied.Width > 0 && applied.Height > 0);
                        if (vertical)
                        {
                            Assert.True(applied.Height <= natural.Height);
                        }
                        else
                        {
                            Assert.True(applied.Width <= natural.Width);
                        }
                    }
                }
            }
        }

        // =================================================================
        // 四、真实 Win32 宿主：Win10 占位策略
        // =================================================================

        [Fact]
        public void 真实窗口上重复Tick不压缩任务列表且退出后恢复原始布局()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var windows = new FakeWindowSource();

                // ★ 放在屏幕外，避免干扰用户桌面
                Rectangle rootBounds = new Rectangle(-3000, -2000, 1920, 48);
                IntPtr root = host.CreateRootWindow(rootBounds, "LiteMonitorF05Root");
                IntPtr rebar = host.CreateSlotChild(root, 0, 0, 1920, 48, "LiteMonitorF05ReBar");
                IntPtr taskList = host.CreateSlotChild(rebar, 600, 0, 1200, 48, "LiteMonitorF05TaskList");

                windows.TaskbarRoot = root;
                windows.ReBar = rebar;
                windows.TaskList = taskList;

                using var panel = new PanelForm();
                var strategy = new TaskbarStrategyWin10(panel, windows);
                strategy.Attach(root);

                Rectangle originalList = TaskbarWin32TestHost.GetBounds(taskList);
                Assert.Equal(1200, originalList.Width);

                // ★ 连续 100 次 Tick（模拟 UI 定时器）
                Rectangle expected = Rectangle.Empty;
                for (int i = 0; i < 100; i++)
                {
                    strategy.SetPosition(root, 0, 0, 300, 48, 0, true, TaskbarDockDirection.Bottom);

                    Rectangle list = TaskbarWin32TestHost.GetBounds(taskList);
                    if (i == 0) expected = list;

                    Assert.Equal(expected, list);          // 幂等：窗口矩形不再变化
                    Assert.Equal(900, list.Width);          // 1200 - 300（没有被继续压缩）
                    Assert.Equal(rootBounds.X + 900, list.Left);
                }

                // 面板被定位在父客户区坐标 (600, 0) ⇒ 屏幕坐标 = 父原点 + 600
                Rectangle panelBounds = TaskbarWin32TestHost.GetBounds(panel.Handle);
                Assert.Equal(rootBounds.X + 600, panelBounds.Left);

                // ★ 退出：撤销占位，把 Explorer 的布局原样还回去
                strategy.Restore();
                Rectangle restored = TaskbarWin32TestHost.GetBounds(taskList);
                Assert.Equal(originalList, restored);
            });
        }

        [Fact]
        public void 真实窗口上方向切换会撤销旧占位并重新取基线()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var windows = new FakeWindowSource();

                Rectangle rootBounds = new Rectangle(-3000, -2000, 1920, 1080);
                IntPtr root = host.CreateRootWindow(rootBounds, "LiteMonitorF05Root2");
                IntPtr rebar = host.CreateSlotChild(root, 0, 0, 1920, 1080, "LiteMonitorF05ReBar2");
                IntPtr taskList = host.CreateSlotChild(rebar, 0, 0, 1200, 940, "LiteMonitorF05TaskList2");

                windows.TaskbarRoot = root;
                windows.ReBar = rebar;
                windows.TaskList = taskList;

                using var panel = new PanelForm();
                var strategy = new TaskbarStrategyWin10(panel, windows);
                strategy.Attach(root);

                Rectangle original = TaskbarWin32TestHost.GetBounds(taskList);

                // 底部停靠（水平收缩）
                strategy.SetPosition(root, 0, 0, 300, 48, 0, true, TaskbarDockDirection.Bottom);
                Rectangle squeezed = TaskbarWin32TestHost.GetBounds(taskList);
                Assert.Equal(original.Width - 300, squeezed.Width);   // 1200 → 900
                Assert.NotEqual(original, squeezed);

                // ★ 模拟 Explorer 在任务栏改向后对任务列表的重新摆放
                //   （真实场景里 Explorer 会重建 ReBar / MSTaskSwWClass，句柄随之变化）
                NativeMethods.MoveWindow(taskList, 0, 0, 1200, 940, true);

                // 切到左侧停靠（垂直收缩）⇒ 旧占位不得继续作用在宽度上
                strategy.SetPosition(root, 0, 0, 60, 200, 0, true, TaskbarDockDirection.Left);
                Rectangle verticalSqueezed = TaskbarWin32TestHost.GetBounds(taskList);

                Assert.Equal(original.Width, verticalSqueezed.Width);    // 宽度未被水平挤占
                Assert.True(verticalSqueezed.Height < original.Height); // 高度被垂直挤占
                Assert.Equal(original.Height - 200, verticalSqueezed.Height);
            });
        }

        [Fact]
        public void Win10策略在无法内部布局时把屏幕坐标显式转换到父客户区坐标()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var windows = new FakeWindowSource();

                // ★ 放在屏幕外，避免干扰用户桌面
                Rectangle rootBounds = new Rectangle(-3000, -2000, 1920, 48);
                IntPtr root = host.CreateRootWindow(rootBounds, "LiteMonitorF05Root3");
                IntPtr rebar = host.CreateSlotChild(root, 0, 0, 1920, 48, "LiteMonitorF05ReBar3");
                IntPtr taskList = host.CreateSlotChild(rebar, 600, 0, 1200, 48, "LiteMonitorF05TaskList3");

                windows.TaskbarRoot = root;
                windows.ReBar = rebar;
                windows.TaskList = taskList;

                using var panel = new PanelForm();
                var strategy = new TaskbarStrategyWin10(panel, windows);
                strategy.Attach(root);

                // ★ 方向未知 ⇒ 走"回退定位"分支：传入的是**屏幕坐标**，
                //   必须被显式转换到父客户区坐标（否则面板会被摆到屏幕原点 + 偏移处）。
                int screenX = rootBounds.X + 640;
                int screenY = rootBounds.Y + 6;
                strategy.SetPosition(root, screenX, screenY, 200, 30, 0, true, TaskbarDockDirection.Unknown);

                Rectangle panelBounds = TaskbarWin32TestHost.GetBounds(panel.Handle);
                Assert.Equal(screenX, panelBounds.Left);
                Assert.Equal(screenY, panelBounds.Top);
                Assert.Equal(200, panelBounds.Width);
                Assert.Equal(30, panelBounds.Height);
            });
        }

        // =================================================================
        // 五、真实 Win32 宿主：Win11 策略的坐标契约
        // =================================================================

        [Fact]
        public void Win11策略把屏幕坐标显式转换到父客户区坐标()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();

                Rectangle rootBounds = new Rectangle(-3000, -1500, 600, 48);
                IntPtr root = host.CreateRootWindow(rootBounds, "LiteMonitorF05Win11Root");

                using var panel = new PanelForm();
                var strategy = new TaskbarStrategyWin11(panel);
                strategy.Attach(root);

                // 屏幕坐标 (root.X + 120, root.Y + 8)
                strategy.SetPosition(root, rootBounds.X + 120, rootBounds.Y + 8, 200, 30, 0, true, TaskbarDockDirection.Bottom);

                Rectangle panelBounds = TaskbarWin32TestHost.GetBounds(panel.Handle);
                Assert.Equal(rootBounds.X + 120, panelBounds.Left);
                Assert.Equal(rootBounds.Y + 8, panelBounds.Top);
                Assert.Equal(200, panelBounds.Width);
                Assert.Equal(30, panelBounds.Height);

                // ★ 左/右/顶部方向属于第三方任务栏 ⇒ 兼容性待验
                strategy.SetPosition(root, rootBounds.X + 120, rootBounds.Y + 8, 200, 30, 0, true, TaskbarDockDirection.Left);
                Assert.True(strategy.IsThirdPartyDirection, "左停靠必然来自第三方任务栏，必须标注兼容性待验");
                Assert.Equal(TaskbarDockDirection.Left, strategy.LastDirection);

                strategy.SetPosition(root, rootBounds.X + 120, rootBounds.Y + 8, 200, 30, 0, true, TaskbarDockDirection.Bottom);
                Assert.False(strategy.IsThirdPartyDirection);
            });
        }

        // =================================================================
        // 六、几何缓存：面板尺寸 / 显示器变化 ⇒ 重新解析
        // =================================================================

        [Fact]
        public void 面板尺寸变化会让几何缓存整体失效()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var screens = new FakeScreenProvider(new TaskbarMonitorInfo(
                    PrimaryDevice,
                    new Rectangle(0, 0, 1920, 1080),
                    new Rectangle(0, 0, 1920, 1032),
                    true));

                IntPtr root = host.CreateRootWindow(new Rectangle(100, 1032, 400, 40), "primary");
                using var panel = new PanelForm();
                var win = new TaskbarWinHelper(panel, screens, new FakeWindowSource());

                var g1 = win.GetGeometry(root, PrimaryDevice, 300, 48, true, 0);
                var g2 = win.GetGeometry(root, PrimaryDevice, 301, 48, true, 0);

                Assert.Equal(TaskbarDockDirection.Bottom, g1.Direction);
                Assert.NotEqual(g1.PanelWidth, g2.PanelWidth);

                // 工作区变化 ⇒ 方向/矩形重新解析（#539）
                screens.SetMonitors(new TaskbarMonitorInfo(
                    PrimaryDevice,
                    new Rectangle(0, 0, 1920, 1080),
                    new Rectangle(0, 0, 1920, 984),
                    true));
                var g3 = win.GetGeometry(root, PrimaryDevice, 300, 48, true, 0);
                Assert.NotEqual(g1.WorkingArea, g3.WorkingArea);
            });
        }

        // =================================================================
        // 辅助
        // =================================================================

        private static void RunSta(Action action)
        {
            Exception captured = null;
            Thread thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (captured != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }
    }
}
