using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.Contracts;
using LiteMonitor.Core;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Helpers; // TaskbarWinHelper(R6)
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.SystemServices;
using LiteMonitor.SystemServices; // CatalogSnapshotCache(R3)
using LiteMonitor.SystemServices.Pipe;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 2026-10-26 审查回归包（R1/R3/R4/R7/R8 + R5 护栏 + R6 Win32 场景）。
    /// 每项对应 docs/reviews/2026-10-26-post-plan-full-review.md 的探针，转为针对真实接线的自动回归。
    /// </summary>
    public class ReviewFix20261026Tests
    {
        // ───────────────────────── R1：控制器切换按目标语义 ─────────────────────────

        [Theory]
        [InlineData(ControllerProbeMode.Off, false, false)]
        [InlineData(ControllerProbeMode.Off, true, false)]
        [InlineData(ControllerProbeMode.On, false, true)]
        [InlineData(ControllerProbeMode.On, true, true)]
        [InlineData(ControllerProbeMode.Auto, false, false)]
        [InlineData(ControllerProbeMode.Auto, true, true)]
        public void R1_目标模式到启用状态_纯映射(ControllerProbeMode target, bool fanNeeded, bool expected)
        {
            // HardwareMonitor 内部静态映射（Off/On 显式，Auto 按监控项需求）
            Assert.Equal(expected, HardwareMonitor.ControllerTargetEnabled(target, fanNeeded));
        }

        [Fact]
        public void R1_ApplyMode回调收到目标模式而非旧值()
        {
            // 政策级钉子：apply/verify 收到的一定是 target——配合 HardwareMonitor 侧
            // "apply 按目标落状态 + verify 读真实 IsControllerEnabled"，修复"报成功实际仍开"。
            var policy = ControllerProbePolicy.FromSettings(null);
            ControllerProbeMode? applied = null, verified = null;
            bool ok = policy.ApplyMode(ControllerProbeMode.Off,
                apply: m => applied = m,
                verify: m => { verified = m; return true; });

            Assert.True(ok);
            Assert.Equal(ControllerProbeMode.Off, applied);
            Assert.Equal(ControllerProbeMode.Off, verified);
        }

        [Fact]
        public void R1_HotSwapLatch_显式Off放行关闭_源码护栏()
        {
            string code = File.ReadAllText(RepoFile("src", "System", "HardwareMonitor.cs"));
            Assert.Contains("_controllerProbePolicy.Mode != ControllerProbeMode.Off", code); // latch 例外
            Assert.Contains("ControllerTargetEnabled(target, FanMetricsNeeded())", code);     // apply 按目标
            Assert.Contains("ControllerProbeMode.Off => !_computer.IsControllerEnabled", code); // verify 读真实状态
        }

        // ───────────────────────── R3：目录查询不在调用线程等锁 ─────────────────────────

        private static HardwareCatalog EmptyCatalog() => new(1, Array.Empty<HardwareCatalogEntry>());

        [Fact]
        public async Task R3_构建慢时调用线程不被阻塞()
        {
            var cache = new CatalogSnapshotCache(() => { Thread.Sleep(400); return EmptyCatalog(); });

            var sw = Stopwatch.StartNew();
            Task<HardwareCatalog> task = cache.GetAsync(CancellationToken.None);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 150,
                $"GetAsync 在 {sw.ElapsedMilliseconds}ms 才返回——调用线程仍在等构建（R3 回归）");
            await task; // 构建最终完成
            Assert.Equal(1, cache.BuildCount);
        }

        [Fact]
        public async Task R3_在途合并_构建期间的新请求共享同一任务()
        {
            var gate = new ManualResetEventSlim(false);
            var cache = new CatalogSnapshotCache(() => { gate.Wait(2000); return EmptyCatalog(); });

            Task<HardwareCatalog> t1 = cache.GetAsync(CancellationToken.None);
            Task<HardwareCatalog> t2 = cache.GetAsync(CancellationToken.None);

            Assert.Same(t1, t2);      // 在途合并：不新增并发构建
            Assert.Equal(1, cache.BuildCount);
            gate.Set();
            await t1;
        }

        [Fact]
        public async Task R3_完成后下一次调用触发新构建()
        {
            var cache = new CatalogSnapshotCache(EmptyCatalog);
            _ = await cache.GetAsync(CancellationToken.None);
            _ = await cache.GetAsync(CancellationToken.None);
            Assert.Equal(2, cache.BuildCount); // 轮询方总能取到"最近完成"的目录
        }

        // ───────────────────────── R4：数值未变仍更新插件色（跨帧） ─────────────────────────

        private static MetricFormatResolvers StubResolvers() => new(
            GetValueStr: (k, v, c, f) => v?.ToString("0.0") ?? "--",
            GetUnitStr: (k, v, ctx) => "%",
            GetDisplayUnit: (k, u, f) => u,
            GetState: (k, v) => 0,
            GetProgressValue: (k, v) => v);

        [Fact]
        public void R4_同数值跨帧_插件色随覆盖更新与清除()
        {
            var f1 = MetricItemFormatter.Format("DASH.X", 42f, null, false, null, null,
                pluginColorOverride: 0, currentPower: (true, false), prev: new MetricFormatterState(), r: StubResolvers());
            Assert.Equal(0, f1.State.CachedColorState);

            // 数值完全不变（缓存命中），仅覆盖从 0 → 2：颜色必须更新（R4 探针 expected=2 actual=0）
            var f2 = MetricItemFormatter.Format("DASH.X", 42f, null, false, null, null,
                pluginColorOverride: 2, currentPower: (true, false), prev: f1.State, r: StubResolvers());
            Assert.Equal(2, f2.State.CachedColorState);
            Assert.Equal(f1.State.CachedNormalText, f2.State.CachedNormalText); // 文本缓存确实未刷新

            // 覆盖清除（0）→ 颜色回默认
            var f3 = MetricItemFormatter.Format("DASH.X", 42f, null, false, null, null,
                pluginColorOverride: 0, currentPower: (true, false), prev: f2.State, r: StubResolvers());
            Assert.Equal(0, f3.State.CachedColorState);
        }

        // ───────────────────────── R7：内存口令不产生幻影差异 ─────────────────────────

        [Fact]
        public void R7_含口令设置_克隆比对零差异()
        {
            var live = new Settings { WebServerPassword = "secret" };
            Settings snapshot = live.CloneForDiff(); // 持久化 JSON 往返：口令丢失

            var diffs = SettingsChanger.ComputeDiff(snapshot, live);
            Assert.DoesNotContain(diffs, d => d.Field == "WebServerPassword");
            Assert.Empty(diffs); // 未修改任何持久化字段 ⇒ 空保存零扰动
        }

        [Fact]
        public void R7_真实凭据变更_显式触发Web应用()
        {
            var empty = Array.Empty<LiteMonitor.Core.Actions.Services.SettingsFieldDiff>();
            var plan = AppActions.ResolveApplyPlan(empty, credentialChanged: true);
            Assert.True(plan.WebServer);
            Assert.False(plan.UiGroup); // 只动 Web，不重建界面

            var planNoCred = AppActions.ResolveApplyPlan(empty, credentialChanged: false);
            Assert.False(planNoCred.Any);
        }

        // ───────────────────────── R8：延迟窗口有界 ─────────────────────────

        [Fact]
        public void R8_环形窗口_超容量后计数有界且保留最新()
        {
            var w = new LatencyWindow(8);
            for (long i = 0; i < 100; i++) w.Add(i);

            Assert.Equal(8, w.Count);                       // 有界：100 次写入后仍只有 8 个样本
            var snap = w.SnapshotInOrder();
            Assert.Equal(new long[] { 92, 93, 94, 95, 96, 97, 98, 99 }, snap); // 按插入序保留最新
        }

        // ───────────────────────── R5：泊出态集中退出（源码护栏） ─────────────────────────
        // MainFormBizHelper 构造需要 UIController（含采集器），单测内无法实例化——
        // 与 WebSamplingDemandTests 同取源码级护栏；Helper 级状态迁移测试留待注入缝。

        [Fact]
        public void R5_RestorePos_走泊出态完整退出()
        {
            string code = File.ReadAllText(RepoFile("src", "UI", "Helpers", "MainFormBizHelper.cs"));
            int exitDef = code.IndexOf("private void ExitParkedState()", StringComparison.Ordinal);
            Assert.True(exitDef > 0, "缺少泊出态集中退出方法 ExitParkedState");
            string body = code.Substring(exitDef, Math.Min(400, code.Length - exitDef));
            Assert.Contains("_isHidden = false", body);
            Assert.Contains("_dock = AutoHideLogic.DockEdge.None", body);
            Assert.Contains("_parkedArea = null", body);
            Assert.Contains("_parkedScreenDevice = null", body);
            Assert.Contains("ExitParkedState();", code); // RestorePos 复用
        }

        // ───────────────────────── R6：同级 Z 序有界遍历 ─────────────────────────

        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;

        [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(int ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        private const uint GW_HWNDPREV = 3;

        [Fact]
        public void R6_隐藏窗口前的可见遮挡_仍判定覆盖()
        {
            RunSta(() =>
            {
            using var root = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-4000, -3000),
                Size = new System.Drawing.Size(600, 80),
            };
            root.Show();
            Application.DoEvents();
            IntPtr rootH = root.Handle;

            // Z 序（实测：子窗口 CreateWindowEx 落在同级底部，后建者在下）：
            // 先建 cover（可见遮挡）与 middle（隐藏占位），最后建 panel ⇒ panel 最底，
            // prev 链 = middle（隐藏）→ cover（可见）；再把 cover 显式抬顶消除创建序歧义。
            IntPtr cover = CreateWindowEx(0, "STATIC", "R6Cover", WS_CHILD | WS_VISIBLE, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            IntPtr middle = CreateWindowEx(0, "STATIC", "R6Hidden", WS_CHILD, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // 无 WS_VISIBLE
            IntPtr panel = CreateWindowEx(0, "STATIC", "R6Panel", WS_CHILD, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try
            {
                SetWindowPos(cover, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                Application.DoEvents();
                // 旧实现只看紧邻（middle，不可见）⇒ false；修复后跳过隐藏窗口命中 cover ⇒ true
                var chain = new System.Collections.Generic.List<string>();
                for (IntPtr h = GetWindow(panel, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
                    chain.Add($"{h}:visible={IsWindowVisible(h)}");
                Assert.True(TaskbarWinHelper.IsCoveredByVisibleSibling(panel),
                    "chain=[" + string.Join(", ", chain) + "]");

                DestroyWindow(cover);
                Application.DoEvents();
                // 唯一可见遮挡移除后：不再判定被覆盖
                Assert.False(TaskbarWinHelper.IsCoveredByVisibleSibling(panel));
            }
            finally
            {
                DestroyWindow(middle);
                DestroyWindow(panel);
            }
            });
        }

        /// <summary>STA 线程执行（Win32 窗口创建需要；同 TaskbarSiblingZOrderTests 手法）。</summary>
        private static void RunSta(Action action)
        {
            Exception? captured = null;
            var t = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (captured != null) throw captured;
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LiteMonitor.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }
    }
}
