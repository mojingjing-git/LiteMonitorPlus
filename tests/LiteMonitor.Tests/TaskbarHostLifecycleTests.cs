using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 【P-F04】任务栏宿主生命周期与有界恢复的行为测试。
    /// <para>★ 覆盖 #553（父销毁后面板丢失）、#573（开机后位置不固定）、#583（RDP 后副屏错位）、
    /// #539（切分辨率后位置/布局跳动）。</para>
    /// <para>★ 取证方式：**真实 Win32 窗口**。测试宿主创建真实的父窗口（模拟任务栏根）与
    /// 真实的子窗口（模拟内层），面板是真实的 WinForms 窗体 ⇒ "父窗口销毁 ⇒ 子窗口一并销毁"
    /// 是 Windows 的**真实**语义，而不是模拟调用。</para>
    /// <para>★ 被测产品代码：<c>TaskbarRecoveryCoordinator</c>（恢复状态机与有界调度）、
    /// <c>TaskbarBizHelper</c>（解析 / 校验 / 挂载）、<c>TaskbarWinHelper</c>（句柄解析与矩形缓存）。
    /// 测试只替换"桌面事实"（显示器拓扑、任务栏窗口所在），**不复制**产品算法。</para>
    /// <para>★ 全程不用 Thread.Sleep：时间一律由可注入的假时钟推进。</para>
    /// </summary>
    public class TaskbarHostLifecycleTests
    {
        private const string PrimaryDevice = @"\\.\DISPLAY1";
        private const string SecondaryDevice = @"\\.\DISPLAY2";
        private const string MissingDevice = @"\\.\DISPLAY99";

        private static readonly Rectangle PrimaryBounds = new Rectangle(0, 0, 1920, 1080);
        private static readonly Rectangle SecondaryBounds = new Rectangle(1920, 0, 1920, 1080);

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
        /// 假的任务栏窗口查找来源：把"任务栏根 / 内层"映射到测试宿主自己创建的窗口。
        /// <para>★ 绝不查找、也绝不触碰真实的 <c>Shell_TrayWnd</c>。</para>
        /// </summary>
        private sealed class FakeWindowSource : ITaskbarWindowSource
        {
            public IntPtr PrimaryTaskbar { get; set; }
            public IntPtr PrimaryTray { get; set; }
            public IntPtr SecondaryTaskbar { get; set; }

            public IntPtr FindWindow(string className, string? windowName)
            {
                if (className == "Shell_TrayWnd") return PrimaryTaskbar;
                if (className == "Shell_SecondaryTrayWnd") return SecondaryTaskbar;
                return IntPtr.Zero;
            }

            public IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName)
            {
                // "Shell_SecondaryTrayWnd" 的枚举是"逐个往后找"：只提供一个时，
                // after 非零就必须返回 Zero，否则产品的枚举循环永远不结束。
                if (className == "Shell_SecondaryTrayWnd")
                {
                    return after == IntPtr.Zero ? SecondaryTaskbar : IntPtr.Zero;
                }
                if (className == "TrayNotifyWnd" && parent == PrimaryTaskbar) return PrimaryTray;
                return IntPtr.Zero;
            }
        }

        /// <summary>真实的面板窗体（可观察句柄生命周期）。</summary>
        private sealed class PanelForm : Form
        {
            public int HandleCreatedCount;
            public int HandleDestroyedCount;

            protected override void OnHandleCreated(EventArgs e)
            {
                HandleCreatedCount++;
                base.OnHandleCreated(e);
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                HandleDestroyedCount++;
                base.OnHandleDestroyed(e);
            }

            /// <summary>受控地丢弃一个"还在但已经失效"的句柄（<c>Control.DestroyHandle</c> 是 protected）。</summary>
            public void DestroyStaleHandle() => DestroyHandle();
        }

        /// <summary>
        /// 测试宿主接缝：**产品**的 <c>TaskbarBizHelper</c> + <c>TaskbarPanelHandleKeeper</c>，
        /// 只把"桌面事实"换成本次测试的窗口。
        /// </summary>
        private sealed class PanelHostBridge : ITaskbarHostBridge
        {
            private readonly TaskbarBizHelper _biz;
            private readonly TaskbarPanelHandleKeeper _keeper;

            public PanelHostBridge(PanelForm panel, Settings cfg, ITaskbarScreenProvider screens, ITaskbarWindowSource windows)
            {
                var win = new TaskbarWinHelper(panel, screens, windows);
                _biz = new TaskbarBizHelper(panel, cfg, win);
                _keeper = new TaskbarPanelHandleKeeper(panel, panel.DestroyStaleHandle);
            }

            public TaskbarBizHelper Biz => _biz;

            public TaskbarHostResolution ResolveTarget() => _biz.ResolveTarget();

            public bool IsPanelAttached(TaskbarHostResolution resolution) => _biz.IsPanelAttached(resolution);

            public void AttachToTarget(TaskbarHostResolution resolution)
            {
                _keeper.EnsureHandle();
                _biz.AttachToResolved(resolution);
            }
        }

        /// <summary>假时钟（单调时间戳与墙上时间独立）。</summary>
        private sealed class FakeTimeProvider : TimeProvider
        {
            private long _ticks;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => Interlocked.Read(ref _ticks);

            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

            public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
        }

        private static Settings MakeSettings(string device)
        {
            return new Settings
            {
                TaskbarMonitorDevice = device,
                ShowTaskbar = true,
            };
        }

        private static TaskbarMonitorInfo Monitor(string device, Rectangle bounds, bool primary)
        {
            // 工作区 = 屏幕底部留出 48px 的"任务栏区"，用于验证矩形缓存键包含工作区。
            Rectangle working = new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height - 48);
            return new TaskbarMonitorInfo(device, bounds, working, primary);
        }

        // =================================================================
        // 场景一：父窗口被销毁 ⇒ 面板随之销毁 ⇒ 恢复后重新挂载（#553）
        // =================================================================

        [Fact]
        public void 父任务栏窗口被销毁后面板被连带销毁且可恢复到新根窗口()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var screens = new FakeScreenProvider(Monitor(PrimaryDevice, PrimaryBounds, true),
                                                    Monitor(SecondaryDevice, SecondaryBounds, false));
                var windows = new FakeWindowSource();

                // 主屏任务栏 + 副屏任务栏（都在屏幕外，不干扰用户桌面）
                windows.PrimaryTaskbar = host.CreateRootWindow(new Rectangle(PrimaryBounds.X, PrimaryBounds.Y, 400, 40), "primary");
                windows.PrimaryTray = host.CreateInnerChild(windows.PrimaryTaskbar, "primaryTray");
                IntPtr root = host.CreateRootWindow(new Rectangle(SecondaryBounds.X + 10, SecondaryBounds.Y + 10, 400, 40), "secondary");
                host.CreateInnerChild(root, "secondaryInner");
                windows.SecondaryTaskbar = root;

                using var panel = new PanelForm();
                panel.Show();

                var bridge = new PanelHostBridge(panel, MakeSettings(SecondaryDevice), screens, windows);
                var clock = new FakeTimeProvider();
                var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

                // 首次挂载
                coordinator.RequestRecovery(TaskbarRecoveryReason.Startup, immediate: true);
                Assert.True(coordinator.Pump());
                Assert.Equal(TaskbarHostState.Attached, coordinator.State);

                IntPtr panelBefore = panel.Handle;
                Assert.Equal(root, TaskbarWin32TestHost.ParentOf(panelBefore));

                // 父窗口销毁 ⇒ 子窗口（面板）**一并销毁**（真实 Win32 语义）
                host.DestroyRoot(root);
                Assert.False(TaskbarWin32TestHost.IsAlive(panelBefore));
                Assert.Equal(1, panel.HandleDestroyedCount);

                // Explorer 重建了副屏任务栏根窗口
                IntPtr root2 = host.CreateRootWindow(new Rectangle(SecondaryBounds.X + 10, SecondaryBounds.Y + 10, 400, 40), "secondary2");
                host.CreateInnerChild(root2, "secondaryInner2");
                windows.SecondaryTaskbar = root2;

                // 恢复请求走统一去抖：未到时间不执行
                coordinator.RequestRecovery(TaskbarRecoveryReason.HandleDestroyed);
                Assert.False(coordinator.Pump());

                clock.Advance(TimeSpan.FromMilliseconds(400));
                Assert.True(coordinator.Pump());
                Assert.Equal(TaskbarHostState.Attached, coordinator.State);

                IntPtr panelAfter = panel.Handle;
                Assert.True(TaskbarWin32TestHost.IsAlive(panelAfter));
                Assert.Equal(root2, TaskbarWin32TestHost.ParentOf(panelAfter));
                Assert.True(panelAfter != panelBefore);
            });
        }

        // =================================================================
        // 场景二：保留根窗口、重建子层 ⇒ 检测到"挂载已过期"并重挂（#553 / #599 路径）
        // =================================================================

        [Fact]
        public void 保留根窗口但重建子层时挂载被判定为过期并重新挂载()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var screens = new FakeScreenProvider(Monitor(PrimaryDevice, PrimaryBounds, true));
                var windows = new FakeWindowSource();

                IntPtr root = host.CreateRootWindow(new Rectangle(PrimaryBounds.X, PrimaryBounds.Y, 400, 40), "primary");
                IntPtr tray = host.CreateInnerChild(root, "tray");
                windows.PrimaryTaskbar = root;
                windows.PrimaryTray = tray;

                using var panel = new PanelForm();
                panel.Show();

                var bridge = new PanelHostBridge(panel, MakeSettings(PrimaryDevice), screens, windows);
                var clock = new FakeTimeProvider();
                var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

                coordinator.RequestRecovery(TaskbarRecoveryReason.Startup, immediate: true);
                Assert.True(coordinator.Pump());
                Assert.Equal(TaskbarHostState.Attached, coordinator.State);
                Assert.Equal(tray, bridge.Biz.AttachedInnerHandle);

                // Explorer 重建了子层（根 HWND 保留）
                IntPtr tray2 = host.RebuildInnerLayer(root, tray, "tray2");
                windows.PrimaryTray = tray2;

                // 根窗口仍然有效 ⇒ 旧的"只看根 HWND"判定会说"一切正常"。
                // 新判定必须识别出"内层子句柄已变 ⇒ 挂载过期"。
                Assert.False(bridge.IsPanelAttached(bridge.ResolveTarget()));

                coordinator.RequestRecovery(TaskbarRecoveryReason.HostInvalid);
                clock.Advance(TimeSpan.FromMilliseconds(400));
                Assert.True(coordinator.Pump());

                Assert.Equal(TaskbarHostState.Attached, coordinator.State);
                Assert.Equal(tray2, bridge.Biz.AttachedInnerHandle);
            });
        }

        // =================================================================
        // 场景三：目标屏宿主延迟出现 ⇒ 保持等待、绝不静默绑定主屏（#573 / #583）
        // =================================================================

        [Fact]
        public void 目标屏未就绪时保持等待且不绑定主屏目标出现后自动挂载()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                // ★ 开机竞态：拓扑里**只有主屏**，用户却指定了副屏
                var screens = new FakeScreenProvider(Monitor(PrimaryDevice, PrimaryBounds, true));
                var windows = new FakeWindowSource();

                IntPtr primaryRoot = host.CreateRootWindow(new Rectangle(PrimaryBounds.X, PrimaryBounds.Y, 400, 40), "primary");
                host.CreateInnerChild(primaryRoot, "primaryTray");
                windows.PrimaryTaskbar = primaryRoot;
                windows.PrimaryTray = host.CreateInnerChild(primaryRoot, "primaryTray2");

                using var panel = new PanelForm();
                panel.Show();

                var bridge = new PanelHostBridge(panel, MakeSettings(SecondaryDevice), screens, windows);
                var clock = new FakeTimeProvider();
                var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

                coordinator.RequestRecovery(TaskbarRecoveryReason.Startup, immediate: true);
                Assert.True(coordinator.Pump());

                // ★ 关键断言：目标屏没就绪 ⇒ 进入"等待"，**没有**被挂到主屏上。
                Assert.Equal(TaskbarHostState.WaitingForTaskbar, coordinator.State);
                Assert.False(bridge.IsPanelAttached(bridge.ResolveTarget()));
                Assert.NotEqual(primaryRoot, TaskbarWin32TestHost.ParentOf(panel.Handle));

                // 副屏任务栏延迟就绪
                screens.SetMonitors(Monitor(PrimaryDevice, PrimaryBounds, true),
                                     Monitor(SecondaryDevice, SecondaryBounds, false));
                IntPtr secondaryRoot = host.CreateRootWindow(new Rectangle(SecondaryBounds.X + 10, SecondaryBounds.Y + 10, 400, 40), "secondary");
                host.CreateInnerChild(secondaryRoot, "secondaryInner");
                windows.SecondaryTaskbar = secondaryRoot;

                // 等待期内的下一次重试（恢复期：每 2 秒一次）
                clock.Advance(TimeSpan.FromSeconds(3));
                Assert.True(coordinator.Pump());
                Assert.Equal(TaskbarHostState.Attached, coordinator.State);
                Assert.Equal(secondaryRoot, TaskbarWin32TestHost.ParentOf(panel.Handle));
            });
        }

        // =================================================================
        // 场景四：恢复期间"点退出"⇒ 不再重建、不复活（关闭优先）
        // =================================================================

        [Fact]
        public void 恢复期间关闭则不再重建面板()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var screens = new FakeScreenProvider(Monitor(PrimaryDevice, PrimaryBounds, true));
                var windows = new FakeWindowSource();

                IntPtr root = host.CreateRootWindow(new Rectangle(PrimaryBounds.X, PrimaryBounds.Y, 400, 40), "primary");
                windows.PrimaryTaskbar = root;
                windows.PrimaryTray = host.CreateInnerChild(root, "tray");

                using var panel = new PanelForm();
                panel.Show();

                var bridge = new PanelHostBridge(panel, MakeSettings(PrimaryDevice), screens, windows);
                var clock = new FakeTimeProvider();
                var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

                coordinator.RequestRecovery(TaskbarRecoveryReason.Startup, immediate: true);
                Assert.True(coordinator.Pump());

                int createdBefore = panel.HandleCreatedCount;
                int destroyedBefore = panel.HandleDestroyedCount;

                // 关闭：取消排队、禁止复活
                coordinator.BeginClosing();
                Assert.Equal(TaskbarHostState.Closing, coordinator.State);

                // 关闭之后再来的恢复请求必须被丢弃
                coordinator.RequestRecovery(TaskbarRecoveryReason.HandleDestroyed);
                clock.Advance(TimeSpan.FromSeconds(5));
                Assert.False(coordinator.Pump());

                Assert.Equal(createdBefore, panel.HandleCreatedCount);
                Assert.Equal(destroyedBefore, panel.HandleDestroyedCount);
            });
        }

        // =================================================================
        // 场景五：重复 100 次宿主重建 ⇒ 面板单份、句柄不增长
        // =================================================================

        [Fact]
        public void 重复一百次宿主重建后面板单份且窗口句柄不增长()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var screens = new FakeScreenProvider(Monitor(PrimaryDevice, PrimaryBounds, true));
                var windows = new FakeWindowSource();

                IntPtr root = host.CreateRootWindow(new Rectangle(PrimaryBounds.X, PrimaryBounds.Y, 400, 40), "primary");
                windows.PrimaryTaskbar = root;
                windows.PrimaryTray = host.CreateInnerChild(root, "tray");

                using var panel = new PanelForm();
                panel.Show();

                var bridge = new PanelHostBridge(panel, MakeSettings(PrimaryDevice), screens, windows);
                var clock = new FakeTimeProvider();
                var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

                coordinator.RequestRecovery(TaskbarRecoveryReason.Startup, immediate: true);
                Assert.True(coordinator.Pump());

                int windowsBefore = CountProcessWindows();

                for (int i = 0; i < 100; i++)
                {
                    host.DestroyRoot(root);
                    IntPtr next = host.CreateRootWindow(new Rectangle(PrimaryBounds.X, PrimaryBounds.Y, 400, 40), "primary" + i);
                    windows.PrimaryTray = host.CreateInnerChild(next, "tray" + i);
                    windows.PrimaryTaskbar = next;
                    root = next;

                    coordinator.RequestRecovery(TaskbarRecoveryReason.HandleDestroyed);
                    clock.Advance(TimeSpan.FromMilliseconds(400));
                    Assert.True(coordinator.Pump());
                    Assert.Equal(TaskbarHostState.Attached, coordinator.State);
                }

                // 面板始终是**同一个**窗体实例（不新建面板、不新建定时器）
                Assert.Equal(root, TaskbarWin32TestHost.ParentOf(panel.Handle));
                Assert.True(TaskbarWin32TestHost.IsAlive(panel.Handle));

                // 本进程的窗口数量不持续增长（旧根窗口都被真正销毁了）
                int windowsAfter = CountProcessWindows();
                Assert.True(
                    windowsAfter <= windowsBefore + 2,
                    $"100 次宿主重建后窗口数从 {windowsBefore} 涨到 {windowsAfter} —— 存在句柄泄漏。");
            });
        }

        // =================================================================
        // 场景六：恢复状态机与有界退避（假时钟，不用 Sleep）
        // =================================================================

        [Fact]
        public void 恢复期两秒一次持续十秒之后按低频退避等待且不永久停止()
        {
            var clock = new FakeTimeProvider();
            var bridge = new CountingBridge();
            var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

            coordinator.RequestRecovery(TaskbarRecoveryReason.Startup);
            Assert.Equal(TaskbarHostState.Recovering, coordinator.State);

            // 去抖窗口内不执行
            Assert.False(coordinator.Pump());
            clock.Advance(TimeSpan.FromMilliseconds(400));
            Assert.True(coordinator.Pump());
            Assert.Equal(TaskbarHostState.WaitingForTaskbar, coordinator.State);
            Assert.Equal(1, bridge.Attempts);

            // 恢复期：每 2 秒一次，持续 10 秒
            for (int i = 0; i < 4; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.True(coordinator.Pump());
            }
            Assert.Equal(5, bridge.Attempts);

            // 恢复期结束 ⇒ 低频退避（5s → 10s → 20s → 40s → 60s 封顶），且**永不停止**
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(coordinator.Pump());   // 第 6 次（仍在 10 秒窗口内）
            Assert.Equal(6, bridge.Attempts);

            var gaps = new List<long>();
            long previous = coordinator.AttemptCount;
            for (int i = 0; i < 12; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(60));
                Assert.True(coordinator.Pump());
                gaps.Add(coordinator.AttemptCount - previous);
                previous = coordinator.AttemptCount;
            }

            // 退避不会无限拉长（封顶 60 秒），也不会停止（每次 Pump 都真的执行了一次尝试）
            Assert.All(gaps, g => Assert.Equal(1, g));
            Assert.Equal(18, coordinator.AttemptCount);
        }

        // =================================================================
        // 场景七：多个来源的事件合并去抖 ⇒ 只触发一次恢复
        // =================================================================

        [Fact]
        public void 多个来源的恢复事件被合并成一次恢复()
        {
            var clock = new FakeTimeProvider();
            var bridge = new CountingBridge();
            var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

            // 显示变化 + 设置变化 + 电源恢复 + 锁屏解锁 —— 同一时间窗内涌入
            coordinator.RequestRecovery(TaskbarRecoveryReason.DisplayChange);
            coordinator.RequestRecovery(TaskbarRecoveryReason.SettingChange);
            coordinator.RequestRecovery(TaskbarRecoveryReason.PowerResume);
            coordinator.RequestRecovery(TaskbarRecoveryReason.SessionChange);

            clock.Advance(TimeSpan.FromMilliseconds(400));
            Assert.True(coordinator.Pump());
            Assert.Equal(1, bridge.Attempts);

            // 之后没有新事件 ⇒ 恢复按**低频退避**继续（不新增高频轮询，也不永久停止）
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.True(coordinator.Pump());
            Assert.True(
                bridge.Attempts >= 2 && bridge.Attempts <= 12,
                $"5 分钟内的恢复重试次数必须有界（退避），不能变成高频轮询；实际 {bridge.Attempts} 次。");
        }

        // =================================================================
        // 场景八：巡检不得抢占已排定的退避节奏（恢复必须有界）
        // =================================================================

        [Fact]
        public void 定时巡检不得把退避节奏拉回高频()
        {
            var clock = new FakeTimeProvider();
            var bridge = new CountingBridge();
            var coordinator = new TaskbarRecoveryCoordinator(bridge, clock);

            coordinator.RequestRecovery(TaskbarRecoveryReason.Startup);
            clock.Advance(TimeSpan.FromMilliseconds(400));
            Assert.True(coordinator.Pump());
            Assert.Equal(1, bridge.Attempts);

            // 走过恢复期，进入低频退避
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.True(coordinator.Pump());
            int attemptsAtBackoff = bridge.Attempts;

            // ★ UI 定时 Tick 的廉价存活检查（巡检请求）持续到来：
            //   它**不得**抢占已排定的退避，否则"恢复有界"退化成高频轮询。
            for (int i = 0; i < 50; i++)
            {
                coordinator.EnsureRecoveryScheduled(TaskbarRecoveryReason.HostInvalid);
                coordinator.Pump();
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            Assert.True(
                bridge.Attempts - attemptsAtBackoff <= 1,
                $"5 秒内因巡检多出了 {bridge.Attempts - attemptsAtBackoff} 次恢复尝试 —— 退避节奏被巡检破坏了。");
        }

        // =================================================================
        // 场景九：目标屏未就绪时面板不得以顶层窗口形态浮在桌面上
        // =================================================================

        [Fact]
        public void 目标屏未就绪时面板的显示请求被推迟到挂载成功之后()
        {
            var gate = new TaskbarPanelVisibilityGate();

            // 尚未挂载 ⇒ 抑制显示（否则面板会以顶层窗口形态浮在桌面上）
            Assert.True(gate.ShouldSuppressShow);
            gate.RequestShow();
            Assert.False(gate.TryConsumeShowRequest());

            // 挂载成功 ⇒ 解除抑制，并把之前被压住的显示请求补上**一次**
            gate.ConfirmAttached();
            Assert.False(gate.ShouldSuppressShow);
            Assert.True(gate.TryConsumeShowRequest());
            Assert.False(gate.TryConsumeShowRequest());
        }

        // =================================================================
        // 场景十：目标设备缺失时**不得**回退主屏（#573 / #583 根因）
        // =================================================================

        [Fact]
        public void 目标设备不存在时解析结果为空而不是回退到主屏()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var screens = new FakeScreenProvider(Monitor(PrimaryDevice, PrimaryBounds, true));
                var windows = new FakeWindowSource();

                windows.PrimaryTaskbar = host.CreateRootWindow(new Rectangle(PrimaryBounds.X, PrimaryBounds.Y, 400, 40), "primary");
                windows.PrimaryTray = host.CreateInnerChild(windows.PrimaryTaskbar, "tray");

                using var panel = new PanelForm();
                var win = new TaskbarWinHelper(panel, screens, windows);

                // ★ 旧实现在这里回退主屏 ⇒ 句柄有效 ⇒ 永不重解析（#573 / #583 的同根因）。
                var handles = win.FindHandles(MissingDevice);
                Assert.Equal(IntPtr.Zero, handles.hTaskbar);
                Assert.Equal(IntPtr.Zero, handles.hTray);

                // 副屏存在但没有独立的副屏任务栏窗口时，同样不得回退主屏
                var secondaryHandles = win.FindHandles(SecondaryDevice);
                Assert.Equal(IntPtr.Zero, secondaryHandles.hTaskbar);
            });
        }

        // =================================================================
        // 场景十一：任务栏矩形缓存键必须包含 HWND / 设备 / 工作区（#539）
        // =================================================================

        [Fact]
        public void 任务栏矩形缓存在显示器与工作区变化后不得复用旧坐标()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                var screens = new FakeScreenProvider(Monitor(PrimaryDevice, PrimaryBounds, true));
                var windows = new FakeWindowSource();

                IntPtr root = host.CreateRootWindow(new Rectangle(PrimaryBounds.X + 100, PrimaryBounds.Y + 1000, 400, 40), "primary");
                windows.PrimaryTaskbar = root;

                using var panel = new PanelForm();
                var win = new TaskbarWinHelper(panel, screens, windows);

                Rectangle first = win.GetTaskbarRect(root, PrimaryDevice);
                Assert.False(first.IsEmpty);

                // 同一个物理矩形，但**工作区变了**（切分辨率 / 任务栏大小变化）
                screens.SetMonitors(new TaskbarMonitorInfo(
                    PrimaryDevice, PrimaryBounds,
                    new Rectangle(PrimaryBounds.Left, PrimaryBounds.Top, PrimaryBounds.Width, PrimaryBounds.Height - 96),
                    true));
                Rectangle second = win.GetTaskbarRect(root, PrimaryDevice);
                Assert.False(second.IsEmpty);
                Assert.True(
                    second != first,
                    "工作区变了却复用了旧的任务栏矩形缓存 —— 这正是 #539「切分辨率后位置乱跳」的缓存根因。");
            });
        }

        // =================================================================
        // 场景十二：面板 / 定时器单份（结构性护栏）
        // =================================================================

        [Fact]
        public void 任务栏窗体只持有一个定时器字段()
        {
            int timerFields = typeof(TaskbarForm)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Count(f => f.FieldType == typeof(System.Windows.Forms.Timer));

            Assert.Equal(1, timerFields);
        }

        // =================================================================
        // 辅助
        // =================================================================

        /// <summary>只统计"永远不可用"的桥（用于纯调度语义测试）。</summary>
        private sealed class CountingBridge : ITaskbarHostBridge
        {
            public int Attempts;

            public TaskbarHostResolution ResolveTarget()
            {
                Attempts++;
                return new TaskbarHostResolution(IntPtr.Zero, IntPtr.Zero, true, false, false, false);
            }

            public bool IsPanelAttached(TaskbarHostResolution resolution) => false;

            public void AttachToTarget(TaskbarHostResolution resolution) { }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static int CountProcessWindows()
        {
            int count = 0;
            uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            EnumWindowsProc proc = (hWnd, lParam) =>
            {
                uint owner;
                GetWindowThreadProcessId(hWnd, out owner);
                if (owner == pid) count++;
                return true;
            };

            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            return count;
        }

        private static void RunSta(Action action)
        {
            Exception? captured = null;
            Thread thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (captured is not null)
            {
                ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }
    }
}
