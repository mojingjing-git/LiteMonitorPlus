#nullable enable annotations

using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 【P-F06】主窗置顶看门狗的行为测试（#612 的一半：置顶恢复）。
    /// <para>★ 取证方式：**真实 Win32 窗口** + **真实产品代码**（<c>MainFormBizHelper</c> /
    /// <c>MainFormWinHelper</c>），只替换"是否允许重排 Z 序"的闸门
    /// （全屏 / 安全桌面 / 菜单 / 自动隐藏 ⇒ 可注入，避免依赖测试机的真实前台窗口）。</para>
    /// <para>★ 覆盖点：</para>
    /// <list type="bullet">
    /// <item><b>#612 核心</b>：旧实现的 <c>TopMostTick</c> 第一行就是 <c>if (!_form.Visible) return;</c>
    /// ⇒ 纯任务栏模式（用户隐藏主面板）下置顶维护**完全停摆**。</item>
    /// <item><b>用户主动关闭置顶后不得被重开</b>（PR #373 意图的反面：只做恢复，不抢用户决定）。</item>
    /// </list>
    /// <para>★ 全程不用 Thread.Sleep：维护动作由测试直接驱动，不依赖 WinForms 定时器。</para>
    /// </summary>
    public class TaskbarTopMostWatchdogTests
    {
        // =================================================================
        // 一、#612：主窗隐藏（纯任务栏模式）时置顶维护不得停摆
        // =================================================================

        /// <summary>
        /// 纯任务栏模式下（主窗 <c>Visible == false</c>），置顶看门狗**仍然**必须把丢失的
        /// 置顶样式位补回来。
        /// <para>★ 旧实现 <c>MainFormBizHelper.cs:104</c> 的 <c>if (!_form.Visible) return;</c>
        /// 会让这里什么都不做 ⇒ 用户隐藏主面板期间置顶状态永久停在"错的那一侧"，
        /// 等他重新显示主窗时窗口已经不置顶了。</para>
        /// <para>★ 本用例用反射驱动**现有的**私有定时器回调 ⇒ 它能在<b>改动之前</b>编译并运行，
        /// 从而如实证明旧实现必红。</para>
        /// </summary>
        [Fact]
        public void Watchdog_RestoresTopMost_WhenMainFormHidden()
        {
            RunSta(() =>
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -2000),
                    Size = new Size(240, 60),
                };

                var cfg = new Settings { TopMost = true };
                var win = new MainFormWinHelper(form);

                using var biz = new MainFormBizHelper(form, cfg, null!, win);

                form.Show();
                Application.DoEvents();

                // 制造"用户要置顶、但样式位不在"的状态（句柄重建 / 第三方抢 Z 序后就是这个样子）
                win.RefreshTopMost(false);
                Assert.False(win.IsTopMostStyleApplied(), "前置条件：置顶样式位应当处于缺失状态。");

                // ★ 纯任务栏模式：用户把主面板藏起来了
                form.Hide();
                Application.DoEvents();
                Assert.False(form.Visible, "前置条件：主窗必须处于隐藏（纯任务栏）状态。");

                // 驱动**现有**的私有定时器回调
                InvokeTopMostTick(biz);

                Assert.True(
                    win.IsTopMostStyleApplied(),
                    "纯任务栏模式（主窗隐藏）下，置顶看门狗必须照常把样式位补回来 —— " +
                    "旧实现在 TopMostTick 开头就因 !_form.Visible 早退了。");

                form.Dispose();
            });
        }

        // =================================================================
        // 二、用户主动关闭置顶 ⇒ 绝不被看门狗重新打开
        // =================================================================

        /// <summary>
        /// 用户把置顶关掉（<c>cfg.TopMost == false</c>）之后，看门狗**只**负责摘掉残留样式位，
        /// 无论跑多少个维护周期、无论上方压着什么窗口，都不得把置顶重新打开。
        /// <para>★ PR #373 的"恢复意图"最容易写成"发现不在顶层就重新置顶" ——
        /// 那会把用户主动关闭的置顶又打开。本用例锁死这条边界。</para>
        /// </summary>
        [Fact]
        public void Watchdog_NeverReenablesTopMost_AfterUserTurnedItOff()
        {
            RunSta(() =>
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -2000),
                    Size = new Size(240, 60),
                };

                // ★ 用户主动关闭了置顶
                var cfg = new Settings { TopMost = false };
                var win = new MainFormWinHelper(form);
                using var biz = new MainFormBizHelper(form, cfg, null!, win);

                form.Show();
                Application.DoEvents();

                // 外部把样式位打开了（例如上一次还开着、或第三方置顶过）
                win.RefreshTopMost(true, forceReinsert: true);
                Assert.True(win.IsTopMostStyleApplied(), "前置条件：应当先有一个残留的置顶样式位。");

                // 跑一批维护周期
                for (int i = 0; i < 5; i++) InvokeTopMostTick(biz);

                Assert.False(
                    win.IsTopMostStyleApplied(),
                    "用户关闭置顶后，看门狗必须把残留样式位摘掉，且不得在任何后续周期里重新打开。");

                // 再跑一批：确认不会反弹
                for (int i = 0; i < 5; i++) InvokeTopMostTick(biz);
                Assert.False(win.IsTopMostStyleApplied(), "置顶被关闭后不得在看门狗里反弹回来。");

                form.Dispose();
            });
        }

        // =================================================================
        // 三、#612 的另一半：样式位在、但实际层级已经掉下去
        // =================================================================

        /// <summary>
        /// <c>WS_EX_TOPMOST</c> 样式位<b>还在</b>、但窗口实际已经掉到普通层时，
        /// 看门狗必须把它重新插回置顶层。
        /// <para>★ 这正是旧看门狗漏掉的那半：它只读样式位，样式位在就认为"一切正常"。
        /// 修复后为"样式位 + 实际可见层级"<b>双判</b>。</para>
        /// <para>★ <b>为什么用替身而不是造真窗口</b>（诚实说明）：本包做了实机探针，
        /// 在真实 Win32 上<b>无法</b>用"普通窗口抬到前面"制造"置顶样式位仍在、层级却掉下去"
        /// —— Windows 会同步维护二者：<c>SetWindowPos(A, hNormal)</c> 会顺带把样式位清掉
        /// （实测 S2：A.bit 由 True 变 False），<c>SetWindowLong</c> 又无法写回该位。
        /// 因此这一支用<b>可替换的层级判定接缝</b>驱动（<c>IsTopMostLayeringEffective</c> 是
        /// virtual 且看门狗<b>真实调用它</b>），断言的是"看门狗会不会因为层级异常而重插"，
        /// 而不是复制算法自证。<b>真实驱动场景下降级为待实机</b>（见 F06 验收报告）。</para>
        /// </summary>
        [Fact]
        public void Watchdog_RestoresLayering_WhenStyleSetButRealOrderLost()
        {
            RunSta(() =>
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -2000),
                    Size = new Size(240, 60),
                };

                var cfg = new Settings { TopMost = true };
                var win = new MainFormWinHelper(form);

                // ★ 替身：样式位"在"，但真实层级"已失效"
                var brokenLayering = new StubWinHelper(form) { StyleApplied = true, LayeringEffective = false };
                using var biz = new MainFormBizHelper(form, cfg, null!, brokenLayering);

                form.Show();
                Application.DoEvents();

                bool reinserted = false;
                brokenLayering.OnRefresh = (enabled, force) => { if (enabled && force) reinserted = true; };

                biz.MaintainTopMost();

                Assert.True(
                    reinserted,
                    "样式位在但真实层级已掉时，看门狗必须重新插入置顶层（#612 双判）；" +
                    "旧实现只看样式位，这种情况会误判为正常。");
            });
        }

        // =================================================================
        // 四、让路规则：自动隐藏 / 菜单 / 全屏（含安全桌面）
        // =================================================================

        /// <summary>
        /// 自动隐藏已把面板收起时，看门狗<b>不得</b>重插 Z 序（尊重自动隐藏行为）。
        /// <para>★ 收起状态下重插没有意义，还可能把本该藏起来的边缘露出来。</para>
        /// </summary>
        [Fact]
        public void Watchdog_SkipsReinsert_WhenAutoHidden()
        {
            RunSta(() =>
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -2000),
                    Size = new Size(240, 60),
                };

                var cfg = new Settings { TopMost = true };
                var win = new MainFormWinHelper(form);
                using var biz = new MainFormBizHelper(form, cfg, null!, win);

                form.Show();
                Application.DoEvents();
                win.RefreshTopMost(false);
                Assert.False(win.IsTopMostStyleApplied(), "前置条件：样式位应当处于缺失状态。");

                // ★ 自动隐藏已收起
                biz.SetTopMostSuppressionProvider(new FixedSuppressionProvider(suppress: true));

                biz.MaintainTopMost();
                Assert.False(
                    win.IsTopMostStyleApplied(),
                    "自动隐藏收起期间必须让路：不得重插 Z 序（规格：尊重自动隐藏行为）。");

                // 让路解除后照常恢复
                biz.SetTopMostSuppressionProvider(new FixedSuppressionProvider(suppress: false));
                biz.MaintainTopMost();
                Assert.True(
                    win.IsTopMostStyleApplied(),
                    "让路解除后必须照常恢复置顶 —— 让路是暂时的，不是永久停摆。");
            });
        }

        /// <summary>
        /// 前景窗口整屏覆盖本窗所在显示器时（全屏游戏 / 视频 / 锁屏 / 安全桌面），
        /// 看门狗<b>不得</b>把悬浮窗插到它上面。
        /// <para>★ "不压安全桌面"是这条规则的一个实例：安全桌面就是一个整屏前景窗口。</para>
        /// <para>★ <b>为什么用替身</b>（诚实说明）：测试进程里 <c>Form.Activate()</c> 不会真的
        /// 改变系统前台窗口（无真实输入焦点，实测 <c>GetForegroundWindow()</c> 不指向被激活窗体），
        /// 所以"前景全屏"这一支由可替换的 <c>IsForegroundFullscreen</c> 接缝驱动。
        /// 生产实现仍真实调用 <see cref="MainFormWinHelper.IsForegroundCoveringMonitor"/>；
        /// 真实全屏 / 安全桌面下的行为列为<b>待实机</b>（见 F06 验收报告）。</para>
        /// </summary>
        [Fact]
        public void Watchdog_SkipsReinsert_WhenForegroundFullscreen()
        {
            RunSta(() =>
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -2000),
                    Size = new Size(240, 60),
                };

                var cfg = new Settings { TopMost = true };
                var win = new MainFormWinHelper(form);
                using var biz = new MainFormBizHelper(form, cfg, null!, win);

                form.Show();
                Application.DoEvents();
                win.RefreshTopMost(false);
                Assert.False(win.IsTopMostStyleApplied(), "前置条件：样式位应当处于缺失状态。");

                // ★ 前景全屏（真实全屏窗口 / 锁屏 / 安全桌面都归这一支）
                biz.SetTopMostSuppressionProvider(new FullscreenSuppressionProvider(fullscreen: true));

                biz.MaintainTopMost();
                Assert.False(
                    win.IsTopMostStyleApplied(),
                    "前景全屏（含安全桌面）时不得重插 Z 序 —— 规格：尊重全屏行为、不压安全桌面。");

                // 退出全屏后照常恢复
                biz.SetTopMostSuppressionProvider(new FullscreenSuppressionProvider(fullscreen: false));
                biz.MaintainTopMost();
                Assert.True(
                    win.IsTopMostStyleApplied(),
                    "退出全屏后必须照常恢复置顶 —— 让路是暂时的，不是永久停摆。");
            });
        }

        /// <summary>
        /// 菜单打开时不得重插 Z 序（不要把面板顶到菜单上面）。
        /// </summary>
        [Fact]
        public void Watchdog_SkipsReinsert_WhenMenuOpen()
        {
            RunSta(() =>
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -2000),
                    Size = new Size(240, 60),
                };

                var cfg = new Settings { TopMost = true };
                var win = new MainFormWinHelper(form);
                using var biz = new MainFormBizHelper(form, cfg, null!, win);

                form.Show();
                Application.DoEvents();
                win.RefreshTopMost(false);

                using var menu = new ContextMenuStrip();
                menu.Items.Add("item");
                form.ContextMenuStrip = menu;
                menu.Show(new Point(-2900, -1900));
                Application.DoEvents();

                Assert.True(menu.Visible, "前置条件：菜单必须处于打开状态。");

                biz.MaintainTopMost();
                Assert.False(
                    win.IsTopMostStyleApplied(),
                    "菜单打开时不得重插 Z 序（规格：尊重菜单行为）。");

                menu.Close();
                form.ContextMenuStrip = null;
            });
        }

        // =================================================================
        // 五、有界性：恢复不得变成无限重试
        // =================================================================

        /// <summary>
        /// 维护动作本身必须是<b>幂等且无副作用</b>的：状态已经正确时，反复调用
        /// 既不报错、也不改变样式位，更不会触发 Z 序抖动。
        /// </summary>
        [Fact]
        public void Watchdog_IsIdempotent_WhenStateAlreadyCorrect()
        {
            RunSta(() =>
            {
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-3000, -2000),
                    Size = new Size(240, 60),
                };

                var cfg = new Settings { TopMost = true };
                var win = new MainFormWinHelper(form);
                using var biz = new MainFormBizHelper(form, cfg, null!, win);

                form.Show();
                Application.DoEvents();
                win.RefreshTopMost(true, forceReinsert: true);

                biz.MaintainTopMost();
                Assert.True(win.IsTopMostStyleApplied());

                // 再跑一批维护周期：状态保持不变（不抖动、不翻转）
                for (int i = 0; i < 20; i++)
                {
                    biz.MaintainTopMost();
                    Assert.True(win.IsTopMostStyleApplied(), "状态已正确时反复维护不得把置顶弄丢。");
                }
            });
        }

        // =================================================================
        // 夹具
        // =================================================================

        /// <summary>固定的抑制判定（用来隔离"让路"这一个变量）。</summary>
        private sealed class FixedSuppressionProvider : ITopMostSuppressionProvider
        {
            private readonly bool _suppress;
            public FixedSuppressionProvider(bool suppress) => _suppress = suppress;
            public bool ShouldSuppressZOrderReinsert(Form form) => _suppress;
        }

        /// <summary>
        /// 前景全屏判定的替身（真实全屏窗口在测试进程里无法成为系统前台窗口）。
        /// <para>★ 它<b>真实调用</b>产品基类里被替换的那个虚方法语义，用来驱动
        /// "让路 ⇒ 不重插"这条分支。</para>
        /// </summary>
        private sealed class FullscreenSuppressionProvider : TopMostSuppressionProviderBase
        {
            private readonly bool _fullscreen;
            public FullscreenSuppressionProvider(bool fullscreen) => _fullscreen = fullscreen;

            public override bool ShouldSuppressZOrderReinsert(Form form) => IsForegroundFullscreen(form);

            protected override bool IsForegroundFullscreen(Form form) => _fullscreen;
        }

        /// <summary>
        /// 置顶助手的替身：可独立设定"样式位"与"真实层级"两个判定结果。
        /// <para>★ 之所以需要它：真实 Win32 上无法制造"样式位在、层级却掉下去"
        /// （见对应用例的说明），但看门狗<b>确实</b>会调用这两个方法 ⇒
        /// 用它驱动可以如实断言"看门狗的双判逻辑"。</para>
        /// </summary>
        private sealed class StubWinHelper : MainFormWinHelper
        {
            public StubWinHelper(Form form) : base(form) { }

            public bool StyleApplied { get; set; }
            public bool LayeringEffective { get; set; }
            public Action<bool, bool>? OnRefresh { get; set; }

            public override bool IsTopMostStyleApplied() => StyleApplied;

            public override bool IsTopMostLayeringEffective() => LayeringEffective;

            public override void RefreshTopMost(bool enabled, bool forceReinsert = false)
            {
                OnRefresh?.Invoke(enabled, forceReinsert);
            }
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);

        private static void RaiseToTop(IntPtr hWnd) =>
            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // =================================================================
        // 辅助
        // =================================================================

        /// <summary>
        /// 驱动 <c>MainFormBizHelper</c> 的私有置顶定时器回调。
        /// <para>★ 之所以用反射：修复之前<b>没有</b>公开的维护入口，
        /// 而"先写会失败的用例"要求用例能在旧实现上跑起来并如实变红。</para>
        /// </summary>
        private static void InvokeTopMostTick(MainFormBizHelper helper)
        {
            MethodInfo? method = typeof(MainFormBizHelper).GetMethod(
                "TopMostTick",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.True(method != null, "MainFormBizHelper.TopMostTick 不存在 —— 护栏前提被破坏。");

            try
            {
                method!.Invoke(helper, new object?[] { null, EventArgs.Empty });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

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
