#nullable enable annotations

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 【P-F06】任务栏面板的**同级 Z 序**维护（#599）。
    /// <para>★ 取证方式：**真实 Win32 窗口**。根窗口由测试宿主创建（模拟任务栏），
    /// 面板是真实的 WinForms 窗体，合成兄弟窗口是真实的 <c>WS_CHILD</c> HWND；
    /// Z 序的真值一律用 <c>GetWindow(GW_HWNDPREV)</c> 读取，不复制产品算法自证。</para>
    /// <para>★ 被测产品代码：<c>TaskbarStrategyWin11</c>（<c>TaskbarWin32TestHost</c> 挂载 +
    /// 定位 + Z 序维护）。测试只提供"桌面事实"，不替换被测逻辑。</para>
    /// </summary>
    public class TaskbarSiblingZOrderTests
    {
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint GW_HWNDPREV = 3;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hWnd);

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;

        /// <summary>真实的面板窗体（需要真实 HWND 才能有父子关系与同级 Z 序）。</summary>
        private sealed class PanelForm : Form
        {
            public PanelForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
            }
        }

        // =================================================================
        // #599：迟到的可见合成兄弟窗口覆盖面板后，下一次维护必须恢复
        // =================================================================

        /// <summary>
        /// 面板已挂载并定位之后，新建一个**可见**的合成兄弟窗口并把它抬到同级最前；
        /// 下一个维护周期（<c>SetPosition</c>）必须把面板恢复回同级最前，
        /// 且**不移动、不改大小、不激活**（矩形与前台窗口都不变）。
        /// <para>★ 旧实现 <c>TaskbarStrategyWin11.cs:78/82</c> 的定位恒带 <c>SWP_NOZORDER</c>
        /// ⇒ 无论跑多少个维护周期都不会恢复（探针实测 10 次定位不恢复）。</para>
        /// </summary>
        [Fact]
        public void Maintenance_RestoresZOrder_AfterVisibleSiblingCoversPanel()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                IntPtr root = host.CreateRootWindow(new Rectangle(-3000, -2000, 900, 60), "F06ZRoot");

                using var panel = new PanelForm { Location = new Point(-3000, -2000), Size = new Size(200, 40) };
                panel.Show();
                Application.DoEvents();

                var strategy = new TaskbarStrategyWin11(panel);
                strategy.Attach(root);
                Application.DoEvents();

                IntPtr hp = panel.Handle;
                Assert.Equal(root, TaskbarWin32TestHost.ParentOf(hp));

                // ① 挂载后的第一个维护周期：定位
                Rectangle want = new Rectangle(-3000, -2000, 200, 40);
                strategy.SetPosition(root, want.Left, want.Top, want.Width, want.Height, 0, true, TaskbarDockDirection.Bottom);
                Application.DoEvents();

                Rectangle before = TaskbarWin32TestHost.GetBounds(hp);
                IntPtr foregroundBefore = GetForegroundWindow();

                // ② 迟到的可见合成层：新建兄弟窗口并抬到同级最前
                IntPtr sibling = CreateWindowEx(
                    0, "STATIC", "F06Composition",
                    WS_CHILD | WS_VISIBLE,
                    0, 0, 120, 40,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, sibling);
                applicationDoEvents();

                SetWindowPos(sibling, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                applicationDoEvents();

                Assert.True(
                    IsWindowVisible(sibling),
                    "前置条件：合成兄弟窗口必须可见，否则本用例无法构成 #599 场景。");
                Assert.True(
                    GetWindow(hp, GW_HWNDPREV) == sibling,
                    "前置条件：合成兄弟窗口必须真的压在面板前面，否则本用例无法证明恢复能力。");

                // ③ 下一个维护周期：必须恢复同级 Z 序
                strategy.SetPosition(root, want.Left, want.Top, want.Width, want.Height, 0, true, TaskbarDockDirection.Bottom);
                applicationDoEvents();

                Assert.True(
                    GetWindow(hp, GW_HWNDPREV) == IntPtr.Zero,
                    "迟到的可见合成层覆盖面板后，下一次维护必须把面板恢复到同级最前（#599）。");

                // ④ 矩形与焦点都不许变（不移动 / 不改大小 / 不激活）
                Rectangle after = TaskbarWin32TestHost.GetBounds(hp);
                Assert.Equal(before, after);
                Assert.Equal(foregroundBefore, GetForegroundWindow());

                DestroyWindow(sibling);
            });
        }

        /// <summary>
        /// 同级 Z 序维护**不得**把面板写成顶级窗口的 TOPMOST。
        /// <para>★ 规格明确："不能把子窗口提升逻辑写成顶级窗口 TOPMOST"。
        /// 面板是任务栏的子窗口，正确做法只到 <c>HWND_TOP</c>（同级最前）。</para>
        /// </summary>
        [Fact]
        public void Maintenance_NeverPromotesPanelToTopMost()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                IntPtr root = host.CreateRootWindow(new Rectangle(-3000, -2000, 900, 60), "F06ZRoot2");

                using var panel = new PanelForm { Location = new Point(-3000, -2000), Size = new Size(200, 40) };
                panel.Show();
                Application.DoEvents();

                var strategy = new TaskbarStrategyWin11(panel);
                strategy.Attach(root);
                Application.DoEvents();

                IntPtr hp = panel.Handle;

                IntPtr sibling = CreateWindowEx(
                    0, "STATIC", "F06Composition2",
                    WS_CHILD | WS_VISIBLE,
                    0, 0, 120, 40,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, sibling);
                applicationDoEvents();
                SetWindowPos(sibling, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                applicationDoEvents();

                strategy.SetPosition(root, -3000, -2000, 200, 40, 0, true, TaskbarDockDirection.Bottom);
                applicationDoEvents();

                // ★ 面板必须仍然是任务栏的**子窗口**，且没有被套上 WS_EX_TOPMOST
                Assert.Equal(root, TaskbarWin32TestHost.ParentOf(hp));
                Assert.False(
                    IsTopMost(hp),
                    "同级 Z 序维护只能用 HWND_TOP（同级最前），不得把子窗口提升成顶级窗口 TOPMOST。");

                DestroyWindow(sibling);
            });
        }

        /// <summary>
        /// "迟到合成层"的后续检查必须是<b>有界</b>的：排定次数用完之后，
        /// 维护周期不再做任何 Z 序动作（不每帧无条件置顶、不无限重试）。
        /// <para>★ 对应拆解计划 §1.2 第 4 条（恢复必须有界）与母计划 F06 第 2 步
        /// （避免每帧无条件置顶、遍历系统窗口、反复重挂钩子）。</para>
        /// </summary>
        [Fact]
        public void Maintenance_FollowUpChecks_AreBounded()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                IntPtr root = host.CreateRootWindow(new Rectangle(-3000, -2000, 900, 60), "F06ZRoot3");

                using var panel = new PanelForm { Location = new Point(-3000, -2000), Size = new Size(200, 40) };
                panel.Show();
                Application.DoEvents();

                var strategy = new TaskbarStrategyWin11(panel);
                strategy.Attach(root);
                Application.DoEvents();

                IntPtr hp = panel.Handle;

                // 排定一次显示变化检查（会立即看一眼 + 排定有限次后续检查）
                strategy.RequestDisplayChangeCheck();

                // ★ 配额必须有限（不得是"无限重试"）
                int limit = TaskbarStrategyWin11.MaintenanceFollowUps;
                Assert.True(limit > 0, "后续检查次数必须是正数，否则覆盖不到迟到的合成层。");

                // 消费掉全部配额（每次都保持"未被遮挡"⇒ 不应产生任何 Z 序动作）
                for (int i = 0; i < limit; i++)
                {
                    strategy.PumpZOrderMaintenance();
                }

                // ★ 配额耗尽后再跑大量维护周期：不得再有任何动作
                //   （用"被遮挡也不恢复"来证明它确实停手了 —— 这正是"有界"的可观测含义）
                IntPtr lateSibling = CreateWindowEx(
                    0, "STATIC", "F06Late",
                    WS_CHILD | WS_VISIBLE,
                    0, 0, 120, 40,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, lateSibling);
                applicationDoEvents();
                SetWindowPos(lateSibling, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                applicationDoEvents();
                Assert.True(
                    GetWindow(hp, GW_HWNDPREV) == lateSibling,
                    "前置条件：迟到的可见合成层必须真的压在面板前面。");

                for (int i = 0; i < 50; i++) strategy.PumpZOrderMaintenance();

                Assert.True(
                    GetWindow(hp, GW_HWNDPREV) == lateSibling,
                    "后续检查配额耗尽后不得再有任何 Z 序动作（有界性：不无限重试、不每帧无条件置顶）。");

                DestroyWindow(lateSibling);
            });
        }

        /// <summary>
        /// 后续检查覆盖<b>迟到</b>的合成层：合成层在定位之后才出现时，
        /// 配额之内的下一次维护必须把它重新压回去。
        /// </summary>
        [Fact]
        public void Maintenance_FollowUpChecks_CoverLateCompositionLayer()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                IntPtr root = host.CreateRootWindow(new Rectangle(-3000, -2000, 900, 60), "F06ZRoot4");

                using var panel = new PanelForm { Location = new Point(-3000, -2000), Size = new Size(200, 40) };
                panel.Show();
                Application.DoEvents();

                var strategy = new TaskbarStrategyWin11(panel);
                strategy.Attach(root);
                Application.DoEvents();
                IntPtr hp = panel.Handle;

                // 定位（排定后续检查）
                strategy.SetPosition(root, -3000, -2000, 200, 40, 0, true, TaskbarDockDirection.Bottom);
                applicationDoEvents();

                // ★ 合成层迟到出现
                IntPtr late = CreateWindowEx(
                    0, "STATIC", "F06Late2",
                    WS_CHILD | WS_VISIBLE,
                    0, 0, 120, 40,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, late);
                applicationDoEvents();
                SetWindowPos(late, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                applicationDoEvents();
                Assert.True(GetWindow(hp, GW_HWNDPREV) == late, "前置条件：迟到合成层必须压在面板前面。");

                // 配额内的下一次维护 ⇒ 恢复
                strategy.PumpZOrderMaintenance();
                applicationDoEvents();

                Assert.True(
                    GetWindow(hp, GW_HWNDPREV) == IntPtr.Zero,
                    "迟到的合成层必须被后续检查覆盖到（母计划 F06 第 2 步）。");

                DestroyWindow(late);
            });
        }

        /// <summary>
        /// 显示变化后<b>立即</b>检查：不必等下一个定位周期。
        /// </summary>
        [Fact]
        public void Maintenance_DisplayChangeCheck_IsImmediate()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                IntPtr root = host.CreateRootWindow(new Rectangle(-3000, -2000, 900, 60), "F06ZRoot5");

                using var panel = new PanelForm { Location = new Point(-3000, -2000), Size = new Size(200, 40) };
                panel.Show();
                Application.DoEvents();

                var strategy = new TaskbarStrategyWin11(panel);
                strategy.Attach(root);
                Application.DoEvents();
                IntPtr hp = panel.Handle;

                IntPtr sibling = CreateWindowEx(
                    0, "STATIC", "F06Sib5",
                    WS_CHILD | WS_VISIBLE,
                    0, 0, 120, 40,
                    root, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, sibling);
                applicationDoEvents();
                SetWindowPos(sibling, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                applicationDoEvents();
                Assert.True(GetWindow(hp, GW_HWNDPREV) == sibling, "前置条件：合成层必须压在面板前面。");

                // ★ 显示变化 ⇒ 立即检查（不等定位周期）
                strategy.RequestDisplayChangeCheck();
                applicationDoEvents();

                Assert.True(
                    GetWindow(hp, GW_HWNDPREV) == IntPtr.Zero,
                    "显示变化必须立即检查一次同级 Z 序（母计划 F06 第 2 步）。");

                DestroyWindow(sibling);
            });
        }

        /// <summary>
        /// 面板<b>脱离</b>任务栏（父窗口不是任务栏）时，Z 序维护必须停手 ——
        /// 此时它是顶层窗口，"同级"已经不是同一个层级，重排会误伤别的窗口。
        /// </summary>
        [Fact]
        public void Maintenance_DoesNothing_WhenPanelDetached()
        {
            RunSta(() =>
            {
                using var host = new TaskbarWin32TestHost();
                IntPtr root = host.CreateRootWindow(new Rectangle(-3000, -2000, 900, 60), "F06ZRoot6");

                using var panel = new PanelForm { Location = new Point(-3000, -2000), Size = new Size(200, 40) };
                panel.Show();
                Application.DoEvents();

                var strategy = new TaskbarStrategyWin11(panel);
                strategy.Attach(root);
                Application.DoEvents();
                IntPtr hp = panel.Handle;

                // ★ 脱离：把父窗口改回桌面
                SetParent(hp, IntPtr.Zero);
                applicationDoEvents();
                Assert.NotEqual(root, TaskbarWin32TestHost.ParentOf(hp));

                bool before = IsTopMost(hp);

                strategy.RequestDisplayChangeCheck();
                strategy.PumpZOrderMaintenance();
                applicationDoEvents();

                Assert.Equal(before, IsTopMost(hp));
            });
        }

        // =================================================================
        // 辅助
        // =================================================================

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        private static bool IsTopMost(IntPtr h) =>
            h != IntPtr.Zero && (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

        private static void applicationDoEvents() => Application.DoEvents();

        private static void RunSta(Action action)
        {
            Exception? captured = null;
            var thread = new Thread(() =>
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
