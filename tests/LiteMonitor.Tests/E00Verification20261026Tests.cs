using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.Contracts;
using LiteMonitor.Core; // MetricItemFormatter/MetricFormatResolvers(R4)
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.UI.Helpers; // MainFormBizHelper(R5)/MainFormWinHelper/TaskbarWinHelper(R6)
using LiteMonitor.SystemServices;
using LiteMonitor.SystemServices.Pipe;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// E00（P-E00 / 2026-10-26）：对修复包 <c>5ef523b</c>（R1～R9）的<b>独立复验新增角度</b>。
    /// <para>与 <see cref="ReviewFix20261026Tests"/> 的关系：那 19 例是修复包自带的回归；
    /// 本文件只补派工单要求的<b>新角度</b>——R1 真实接线序列与未 Open Computer 探针、
    /// R2 SpawnHost 缺文件收口、R3 适配器关闭边界、R4 横向帧/刷新帧、
    /// R5 真实 Helper 状态迁移（经 internal 构造接缝）、R6 多层隐藏/有界遍历、
    /// R7 完整保存编排四态（经 ResolveSaveApplyPlan 接缝）、R8 容量钳制边界、
    /// R9 两类缺口分别计数与会话切换重基准化。</para>
    /// <para>接缝边界：产品改动仅两处——MainFormBizHelper 免 UIController 的 internal 构造、
    /// AppActions.ResolveSaveApplyPlan（ApplySettingsSelectively 前缀原样抽出）。均零行为变化。</para>
    /// </summary>
    public class E00Verification20261026Tests
    {
        // ───────────────────────── R1：真实切换序列 + 未 Open 的 Computer 探针 ─────────────────────────

        [Fact]
        public void R1_策略序列OnOffOn_回调收到目标模式且回退有界()
        {
            var policy = ControllerProbePolicy.FromSettings(new Settings { ControllerProbeMode = (int)ControllerProbeMode.On });
            Assert.Equal(ControllerProbeMode.On, policy.Mode);

            var applied = new List<ControllerProbeMode>();
            var verified = new List<ControllerProbeMode>();

            // On → Off（显式关闭必须真的落下去——R1 的原始缺陷：apply 收到旧值导致"报成功实际仍开"）
            Assert.True(policy.ApplyMode(ControllerProbeMode.Off,
                apply: m => applied.Add(m),
                verify: m => { verified.Add(m); return true; }));
            policy.MarkOpened(false); // 产品语义：RefreshHardwareConfig→ShouldEnableController→MarkOpened 落稳定态

            // Off → Off 重入：稳定态幂等，不再触发 apply
            Assert.True(policy.ApplyMode(ControllerProbeMode.Off,
                apply: m => applied.Add(m),
                verify: m => { verified.Add(m); return true; }));

            // Off → On
            Assert.True(policy.ApplyMode(ControllerProbeMode.On,
                apply: m => applied.Add(m),
                verify: m => { verified.Add(m); return true; }));
            policy.MarkOpened(true);

            Assert.Equal(
                new[] { ControllerProbeMode.Off, ControllerProbeMode.On },
                applied);  // 重入的第二次 Off 未触发 apply
            Assert.Equal(applied, verified); // verify 与 apply 收到同一目标序列（政策级钉子）
            Assert.Equal(ControllerProbeMode.On, policy.Mode);
            Assert.Equal(ControllerProbeState.Enabled, policy.State);
        }

        [Fact]
        public void R1_校验拒绝_回滚到原模式且状态降级()
        {
            var policy = ControllerProbePolicy.FromSettings(new Settings { ControllerProbeMode = (int)ControllerProbeMode.On });
            var applied = new List<ControllerProbeMode>();

            bool ok = policy.ApplyMode(ControllerProbeMode.Off,
                apply: m => applied.Add(m),
                verify: _ => false); // 设备侧校验不过（如 Off 后控制器仍在硬件树里）

            Assert.False(ok);
            Assert.Equal(ControllerProbeMode.On, policy.Mode);          // 模式回滚到原值
            Assert.Equal(ControllerProbeState.Degraded, policy.State);  // 失败不伪装成功
            Assert.Equal(
                new[] { ControllerProbeMode.Off, ControllerProbeMode.On },
                applied); // TryRevert 恰好再落地一次（回退有界）
            Assert.NotNull(policy.LastError);
        }

        /// <summary>
        /// R1 探针复现（无设备环境分界）：构造<b>未 Open</b> 的真实 LHM Computer（不触碰任何硬件），
        /// 按 HardwareMonitor.ApplyControllerProbeMode 的固定接线（apply=按目标映射落状态、
        /// verify=读真实 IsControllerEnabled）走 On→Off→On。原探针的
        /// 「controller-off: applied=True mode=Off enabled=True」坏状态必须不可再现；
        /// 真机开关与设备释放仍列入待实机清单。
        /// </summary>
        [Fact]
        public void R1_未Open的Computer_按修复接线_Off确实落到IsControllerEnabled()
        {
            // 未 Open：IsControllerEnabled 只是开关位，Open 前不构造任何控制器组、不碰 HID
            var computer = new Computer { IsControllerEnabled = true };
            var policy = ControllerProbePolicy.FromSettings(new Settings { ControllerProbeMode = (int)ControllerProbeMode.On });
            bool fanNeeded = true; // Auto/On 语境下有风扇监控项需求（与 FanMetricsNeeded 同义）

            // On（初始已开）→ Off
            Assert.True(policy.ApplyMode(ControllerProbeMode.Off,
                apply: target => computer.IsControllerEnabled =
                    HardwareMonitor.ControllerTargetEnabled(target, fanNeeded),
                verify: target => target switch
                {
                    ControllerProbeMode.Off => !computer.IsControllerEnabled,   // 与 HardwareMonitor 相同的 verify 接线
                    ControllerProbeMode.On => computer.IsControllerEnabled,
                    _ => true,
                }));
            Assert.Equal(ControllerProbeMode.Off, policy.Mode);
            Assert.False(computer.IsControllerEnabled, "R1 原探针坏状态再现：Off 报成功但设备仍开");
            // 运行态核对（RefreshHardwareConfig 的 MarkOpened 语义）
            policy.MarkOpened(computer.IsControllerEnabled);
            Assert.Equal(ControllerProbeState.Disabled, policy.State);

            // Off → On
            Assert.True(policy.ApplyMode(ControllerProbeMode.On,
                apply: target => computer.IsControllerEnabled =
                    HardwareMonitor.ControllerTargetEnabled(target, fanNeeded),
                verify: target => target switch
                {
                    ControllerProbeMode.Off => !computer.IsControllerEnabled,
                    ControllerProbeMode.On => computer.IsControllerEnabled,
                    _ => true,
                }));
            Assert.True(computer.IsControllerEnabled);
            policy.MarkOpened(computer.IsControllerEnabled);
            Assert.Equal(ControllerProbeState.Enabled, policy.State);
        }

        // ───────────────────────── R2：SpawnHost 缺文件的会话收口 ─────────────────────────

        [Fact]
        public async Task R2_SpawnHost缺文件_失败会话收口_退避后三败暂停且无子进程()
        {
            var options = new HardwareHostSupervisorOptions
            {
                HostExePathOverride = Path.Combine(Path.GetTempPath(), "e00-no-such-host.exe"), // 不存在 ⇒ SpawnHost 抛 FileNotFoundException
                HandshakeTimeout = TimeSpan.FromSeconds(2),
                HeartbeatInterval = TimeSpan.FromSeconds(2),
                BackoffSequence = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50) },
                BackoffPauseThreshold = 3,
            };

            var transitions = new System.Collections.Concurrent.ConcurrentQueue<SupervisorTransition>();
            using var supervisor = new HardwareHostSupervisor(options);
            supervisor.Transitioned += t => transitions.Enqueue(t);
            supervisor.Start();

            // 3 次失败会话（每次都被 R2 收口）→ 5 分钟窗口内连续 3 败 → crash-loop 暂停
            bool paused = await WaitUntilAsync(() =>
                supervisor.IsPaused
                && supervisor.State == HardwareState.Degraded
                && supervisor.StateReason == "crash-loop-paused",
                TimeSpan.FromSeconds(20));
            Assert.True(paused, $"state={supervisor.State} reason={supervisor.StateReason}");

            // 启动失败被转成失败会话，监督循环不退出、状态不误报 Ready
            Assert.DoesNotContain(transitions, t => t.To == HardwareState.Ready);
            Assert.Contains(transitions, t => t.Reason != null && t.Reason.StartsWith("spawn-error:"));
            Assert.Contains(transitions, t => t.Reason != null && t.Reason.Contains("backoff-")); // 退避确实发生

            // spawn 失败 ⇒ 没有任何子进程被创建，也没有"成功握手"的会话
            Assert.Equal(0, supervisor.SpawnCount);
            Assert.Equal(0, supervisor.SessionCount);
            Assert.Null(supervisor.CurrentHostPid);
            Assert.True(HostProcessCount() <= 1, "遗留了意外的 HardwareHost 子进程");
        }

        // ───────────────────────── R3：适配器关闭边界（在飞构建自然完成、新调用被拒） ─────────────────────────

        [Fact]
        public async Task R3_适配器关闭_在飞目录构建自然完成_新调用被拒()
        {
            // HardwareMonitor 构造即 Open 硬件，单测不可实例化——目录关闭边界只读三个
            // 空值安全的成员（ComputerInstance/IsReady/SamplingGeneration），用未初始化壳即可，
            // 不触碰任何硬件路径。SamplingGeneration 读 _coordinator.Generation ⇒ 注入空壳协调器
            //（其 _generation 字段默认 0，读路径无副作用）。
            var monitorShell = (HardwareMonitor)RuntimeHelpers.GetUninitializedObject(typeof(HardwareMonitor));
            typeof(HardwareMonitor)
                .GetField("_coordinator", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(monitorShell, RuntimeHelpers.GetUninitializedObject(typeof(HardwareSamplingCoordinator)));
            var adapter = new HardwareClientAdapter(monitorShell);

            Task<HardwareCatalog> inFlight = adapter.GetCatalogAsync(CancellationToken.None);
            adapter.Dispose(); // 关闭：在飞构建自然完成，此后新调用被拒

            HardwareCatalog catalog = await inFlight;
            Assert.Empty(catalog.Entries); // 空壳 monitor：无设备 ⇒ 空目录，任务未失败

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => adapter.GetCatalogAsync(CancellationToken.None));
        }

        // ───────────────────────── R4：横向缓存帧与刷新帧的颜色口径 ─────────────────────────

        private static MetricFormatResolvers StubResolvers() => new(
            GetValueStr: (k, v, c, f) => v?.ToString("0.0") ?? "--",
            GetUnitStr: (k, v, ctx) => "%",
            GetDisplayUnit: (k, u, f) => u,
            GetState: (k, v) => 0,
            GetProgressValue: (k, v) => v);

        [Fact]
        public void R4_横向缓存帧_插件色仍随覆盖更新()
        {
            var f1 = MetricItemFormatter.Format("DASH.X", 42f, null, isHorizontal: true, null, null,
                pluginColorOverride: 0, currentPower: (true, false), prev: new MetricFormatterState(), r: StubResolvers());
            Assert.Equal(0, f1.State.CachedColorState);

            // 横向布局同样走文本缓存分支：数值不变、只改覆盖 ⇒ 颜色必须更新（R4 在横向路径的镜像）
            var f2 = MetricItemFormatter.Format("DASH.X", 42f, null, isHorizontal: true, null, null,
                pluginColorOverride: 3, currentPower: (true, false), prev: f1.State, r: StubResolvers());
            Assert.Equal(3, f2.State.CachedColorState);
        }

        [Fact]
        public void R4_数值变化帧_新颜色同步应用()
        {
            var f1 = MetricItemFormatter.Format("DASH.X", 42f, null, false, null, null,
                pluginColorOverride: 0, currentPower: (true, false), prev: new MetricFormatterState(), r: StubResolvers());

            // 数值变化（refresh 路径）+ 覆盖同时变化 ⇒ 颜色与文本都更新，且状态写入新覆盖
            var f2 = MetricItemFormatter.Format("DASH.X", 43f, null, false, null, null,
                pluginColorOverride: 2, currentPower: (true, false), prev: f1.State, r: StubResolvers());
            Assert.Equal(2, f2.State.CachedColorState);
            Assert.NotEqual(f1.State.CachedNormalText, f2.State.CachedNormalText); // 文本确实重算
        }

        // ───────────────────────── R6：多层隐藏窗口与有界遍历 ─────────────────────────

        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;

        [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(int ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

        [Fact]
        public void R6_多层隐藏窗口后的可见遮挡_仍判定覆盖()
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

                // Z 序（顶→底）：cover（可见）→ hidden2（隐藏）→ hidden1（隐藏）→ panel
                IntPtr cover = CreateWindowEx(0, "STATIC", "R6Cover", WS_CHILD | WS_VISIBLE, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                IntPtr hidden2 = CreateWindowEx(0, "STATIC", "R6H2", WS_CHILD, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                IntPtr hidden1 = CreateWindowEx(0, "STATIC", "R6H1", WS_CHILD, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                IntPtr panel = CreateWindowEx(0, "STATIC", "R6Panel", WS_CHILD, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                try
                {
                    SetWindowPos(cover, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    Application.DoEvents();

                    // ≥2 层隐藏窗口隔在中间：旧实现只看紧邻（hidden1，不可见）⇒ false；
                    // 修复后有界遍历跳过全部隐藏层命中 cover ⇒ true
                    Assert.True(TaskbarWinHelper.IsCoveredByVisibleSibling(panel));
                }
                finally
                {
                    DestroyWindow(cover);
                    DestroyWindow(hidden2);
                    DestroyWindow(hidden1);
                    DestroyWindow(panel);
                }
            });
        }

        [Fact]
        public void R6_超界隐藏窗口链_有界返回不拖死调用方()
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

                // 70 层隐藏前驱（> MaxSiblingHops=64）+ 底部 panel：无可见遮挡 ⇒ false，
                // 且遍历有界（异常长链不得拖死每秒轮询的调用方）
                var created = new List<IntPtr>(71);
                try
                {
                    for (int i = 0; i < 70; i++)
                        created.Add(CreateWindowEx(0, "STATIC", "R6H" + i, WS_CHILD, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
                    IntPtr panel = CreateWindowEx(0, "STATIC", "R6Panel", WS_CHILD, 0, 0, 200, 40, rootH, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    created.Add(panel);
                    Application.DoEvents();

                    var sw = Stopwatch.StartNew();
                    bool covered = TaskbarWinHelper.IsCoveredByVisibleSibling(panel);
                    sw.Stop();
                    Assert.False(covered);
                    Assert.True(sw.ElapsedMilliseconds < 1000, $"有界遍历超时：{sw.ElapsedMilliseconds}ms");
                }
                finally
                {
                    foreach (IntPtr h in created) DestroyWindow(h);
                }
            });
        }

        // ───────────────────────── R7：完整保存编排（CloneForDiff → Merge → 计划分派） ─────────────────────────

        /// <summary>
        /// 复刻 SettingsForm.ApplySettings 的保存编排前缀（SettingsForm.cs:311-336）：
        /// before=CloneForDiff(live) → Merge(live, draft) → ApplySettingsSelectively 的决策前缀
        /// （经 ResolveSaveApplyPlan 接缝=真实产品代码，避免测试复制品漂移）。
        /// ApplySettingsSelectively 的后缀（MainForm/UIController 副作用）不可构造，边界记录在报告。
        /// </summary>
        private static (AppActions.ApplyPlan Plan, List<LiteMonitor.Core.Actions.Services.SettingsFieldDiff> Diffs) RunSaveOrchestration(
            Settings live, Action<Settings>? editDraft = null)
        {
            Settings draft = live.DeepClone();              // SettingsForm.ctor: _draftCfg = _cfg.DeepClone()
            editDraft?.Invoke(draft);                       // 用户在草稿上的编辑（页 Save() 写草稿）
            Settings before = live.CloneForDiff();          // SettingsForm.ApplySettings: 合并前快照
            SettingsChanger.Merge(live, draft);             // 草稿合并进 live
            return AppActions.ResolveSaveApplyPlan(before, live);
        }

        [Fact]
        public void R7_编排_有内存口令_无改动保存_零差异零计划()
        {
            // 有内存口令（本会话刚设过、未重启）+ 用户什么都没改就点保存
            var live = new Settings { WebServerPassword = "secret", WebServerPasswordSalt = "e00-salt" };
            string fingerprintBefore = live.GetWebPasswordFingerprint();

            var (plan, diffs) = RunSaveOrchestration(live);

            Assert.Empty(diffs);                    // 幻影差异（原探针 count=1 fields=WebServerPassword）不得再现
            Assert.False(plan.Any);                 // 空保存零扰动：不触发 Web 应用、不重建界面
            Assert.False(plan.UiGroup);
            Assert.False(plan.WebServer);
            // 口令仍可校验：克隆丢明文，但有效哈希经 Merge 回填，凭据状态无损
            Assert.True(live.HasWebPasswordConfigured());
            Assert.Equal(fingerprintBefore, live.GetWebPasswordFingerprint());
        }

        [Fact]
        public void R7_编排_仅持久化哈希_无改动保存_零差异零计划()
        {
            // 稳态：明文已被迁移剥离，仅哈希 + 盐落盘
            var live = new Settings();
            live.WebServerPasswordHash = "persisted-hash-x";
            live.WebServerPasswordSalt = "persisted-salt-x";

            Settings beforeClone = live.CloneForDiff(); // 克隆必须保留有效哈希（否则凭据无从比对）
            Assert.Equal("persisted-hash-x", beforeClone.WebServerPasswordHash);

            var (plan, diffs) = RunSaveOrchestration(live);

            Assert.Empty(diffs);
            Assert.False(plan.Any);
            Assert.True(live.HasWebPasswordConfigured());
        }

        [Fact]
        public void R7_编排_真实改口令_触发Web应用_不重建界面()
        {
            var live = new Settings { WebServerPassword = "old-pass", WebServerPasswordSalt = "e00-salt" };

            var (plan, diffs) = RunSaveOrchestration(live, draft => draft.WebServerPassword = "new-pass");

            // 幻影差异被黑名单挡住，但真实凭据变更必须经由显式比对触发 Web 应用（R7 双向闭合）
            Assert.DoesNotContain(diffs, d => d.Field == "WebServerPassword");
            Assert.Contains(diffs, d => d.Field == "WebServerPasswordHash"); // 哈希随明文变化（独立证据）
            Assert.True(plan.WebServer);
            Assert.True(live.HasWebPasswordConfigured());
            Assert.True(live.VerifyWebPassword("new-pass"));
            Assert.False(live.VerifyWebPassword("old-pass"));

            // ── E00 发现 F1（钉住现状，不改产品）：WebServerPasswordHash 不在 SettingsChanger.WebFields
            // 归类表里 ⇒ 被 Classify 落入 Other ⇒ ResolveApplyPlan 的 default 分支置 UiGroup=true。
            // 即"只改口令"当前会触发整组界面重建（Web 也触发）——行为正确但影响面过大。
            // 属 E03"字段归属表"的既有缺口（非 5ef523b 引入的回归）；E03 修正归属后本断言应翻转为 False。
            Assert.Contains(diffs, d => d.Field == "WebServerPasswordHash"
                && d.Module == LiteMonitor.Core.Actions.Services.SettingsModule.Other);
            Assert.True(plan.UiGroup, "现状：哈希差异归类 Other ⇒ 整组重建（发现 F1，见上注）");
        }

        [Fact]
        public void R7_编排_无改动保存_零差异零计划()
        {
            var live = new Settings { Language = "zh", RefreshMs = 2000, WebServerPort = 9988 };
            var (plan, diffs) = RunSaveOrchestration(live);

            Assert.Empty(diffs);
            Assert.False(plan.Any);
        }

        [Fact]
        public void R7_编排_清除口令_哈希与盐差异触发Web应用()
        {
            // 仅持久化哈希态 + 用户点"清除访问口令"（MainPanelPage：明文/哈希/盐一起清）
            var live = new Settings();
            live.WebServerPasswordHash = "persisted-hash-x";
            live.WebServerPasswordSalt = "persisted-salt-x";

            var (plan, diffs) = RunSaveOrchestration(live, draft =>
            {
                draft.WebServerPassword = "";
                draft.WebServerPasswordHash = "";
                draft.WebServerPasswordSalt = "";
            });

            // 拿明文比较永远发现不了"哈希被清掉"（R03）——哈希/盐差异路径必须接住
            Assert.Contains(diffs, d => d.Field == "WebServerPasswordHash");
            Assert.Contains(diffs, d => d.Field == "WebServerPasswordSalt");
            Assert.True(plan.WebServer);
            Assert.False(live.HasWebPasswordConfigured());
            // 发现 F1 同款：哈希归类 Other ⇒ 现状同时置 UiGroup=true（E03 修正后翻转）
            Assert.True(plan.UiGroup, "现状：哈希差异归类 Other ⇒ 整组重建（发现 F1）");
        }

        // ───────────────────────── R8：环形窗口容量边界 ─────────────────────────

        [Fact]
        public void R8_容量钳制_最小为1且保留最新()
        {
            var w = new LatencyWindow(0); // 非法容量钳到 1
            Assert.Equal(1, w.Capacity);
            for (long i = 0; i < 5; i++) w.Add(i);
            Assert.Equal(1, w.Count);
            Assert.Equal(new long[] { 4 }, w.SnapshotInOrder());
        }

        [Fact]
        public void R8_未满窗口_按插入序返回部分样本()
        {
            var w = new LatencyWindow(8);
            w.Add(10); w.Add(20); w.Add(30);
            Assert.Equal(3, w.Count);
            Assert.Equal(new long[] { 10, 20, 30 }, w.SnapshotInOrder());

            w.Add(40); // 恰好到容量边界前继续追加
            Assert.Equal(new long[] { 10, 20, 30, 40 }, w.SnapshotInOrder());
        }

        [Fact]
        public void R8_默认容量512_常量钉住()
        {
            // 修复包口径：环形窗口默认 512（防长跑无界）；误改会改变 A/B 指标口径
            Assert.Equal(512, LatencyWindow.DefaultCapacity);
            var w = new LatencyWindow();
            Assert.Equal(512, w.Capacity);
        }

        // ───────────────────────── R9：两类缺口分别计数 + 会话切换重基准化 ─────────────────────────
        // PipeHardwareClient 构造即 Start 监督循环——用不存在的 Host 路径让它 3 败后进入暂停
        //（≈几十毫秒、零子进程），随后经反射直灌合成信封，验证接收入口的序号/批次口径。

        private static PipeHardwareClient CreateQuietClient()
        {
            var options = new HardwareHostSupervisorOptions
            {
                HostExePathOverride = Path.Combine(Path.GetTempPath(), "e00-no-such-host.exe"),
                HandshakeTimeout = TimeSpan.FromSeconds(1),
                HeartbeatInterval = TimeSpan.FromSeconds(2),
                BackoffSequence = new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20) },
                BackoffPauseThreshold = 3,
            };
            return new PipeHardwareClient(100, supervisorOptions: options,
                metricsPath: Path.Combine(Path.GetTempPath(), $"e00-metrics-{Guid.NewGuid():N}.json"));
        }

        private static void Feed(PipeHardwareClient client, IpcEnvelope envelope) =>
            typeof(PipeHardwareClient).GetMethod("OnMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(client, new[] { (object)envelope });

        private static void CloseSession(PipeHardwareClient client) =>
            typeof(PipeHardwareClient).GetMethod("OnSessionClosed", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(client, new[] { (object)"e00-test-session-end" });

        private static IpcEnvelope HeartbeatEnvelope(long seq) => new(
            new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeHeartbeat, "e00", 0, seq),
            System.Text.Json.JsonSerializer.SerializeToElement(
                new IpcHeartbeatMessage(HardwareState.Ready, null, 0, null, Environment.TickCount64)));

        private static IpcEnvelope SnapshotEnvelope(long seq, long batchId) => new(
            new IpcHeader(IpcProtocol.MajorVersion, IpcProtocol.MinorVersion, IpcProtocol.TypeSnapshot, "e00", 0, seq),
            System.Text.Json.JsonSerializer.SerializeToElement(
                new IpcSnapshotMessage(
                    new HardwareSnapshot(batchId, 1, -1, Array.Empty<MetricSample>(), TimeSpan.Zero, Environment.TickCount64),
                    Environment.TickCount64)));

        [Fact]
        public void R9_帧序号缺口_单独计数_批次缺口为零()
        {
            using var client = CreateQuietClient();
            Feed(client, HeartbeatEnvelope(1));
            Feed(client, HeartbeatEnvelope(5)); // 中间丢 2/3/4 三帧

            PipeClientMetrics metrics = client.ReadMetrics();
            Assert.Equal(3, metrics.SequenceGaps);
            Assert.Equal(0, metrics.SnapshotBatchGaps); // 无快照 ⇒ 批次口径不动
            Assert.Equal(0, metrics.SnapshotCount);
        }

        [Fact]
        public void R9_批次缺口_单独计数_帧缺口为零()
        {
            using var client = CreateQuietClient();
            Feed(client, SnapshotEnvelope(10, 1));
            Feed(client, SnapshotEnvelope(11, 4)); // 帧连续、批次跳 2/3（快照槽位合并形态）

            PipeClientMetrics metrics = client.ReadMetrics();
            Assert.Equal(0, metrics.SequenceGaps);      // 帧序号连续：不误计
            Assert.Equal(2, metrics.SnapshotBatchGaps); // 批次缺口另行统计
            Assert.Equal(2, metrics.SnapshotCount);
        }

        [Fact]
        public void R9_会话切换_帧序号重新基准化_无幻影缺口()
        {
            using var client = CreateQuietClient();
            Feed(client, HeartbeatEnvelope(1));
            Feed(client, HeartbeatEnvelope(2));
            CloseSession(client);      // 会话关闭：序号基准重置（OnSessionClosed）
            Feed(client, HeartbeatEnvelope(5)); // 新会话序号重新从小值起算

            Assert.Equal(0, client.ReadMetrics().SequenceGaps); // 若未重置，2→5 会误计 2 个缺口
        }

        [Fact]
        public void R9_会话切换_批次重新起算_无幻影批次缺口()
        {
            using var client = CreateQuietClient();
            Feed(client, SnapshotEnvelope(1, 1));
            CloseSession(client);
            Feed(client, SnapshotEnvelope(2, 1)); // 新会话 Host 从 batchId=1 重新起算（重启语义）

            PipeClientMetrics metrics = client.ReadMetrics();
            Assert.Equal(0, metrics.SnapshotBatchGaps); // 批次号回退不得计成缺口
            Assert.Equal(2, metrics.SnapshotCount);
            // 口径备注（记录于 E00 报告）：OnSessionClosed 显式重置帧序号基准，
            // 批次基准未显式重置——重启语义下批次号回退同样安全（本例钉住），
            // 仅在"跨会话延续批次号"的假想实现下少计缺口，当前两端实现不存在该形态。
        }

        // ───────────────────────── R5：真实 Helper 泊出态状态迁移（internal 构造接缝） ─────────────────────────

        [Fact]
        public void R5_真实Helper_泊出_RestorePos_重新泊出_关闭自动隐藏()
        {
            RunSta(() =>
            {
                var area = Screen.PrimaryScreen!.WorkingArea;
                Point originalCursor = Cursor.Position;
                try
                {
                    int w = 220, h = 480;
                    var edgePos = new Point(area.Right - w, area.Top + 60); // 贴右缘（≤threshold ⇒ 可泊出）

                    using var form = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Location = edgePos,
                        Size = new System.Drawing.Size(w, h),
                    };
                    var cfg = new Settings { AutoHide = true, ClampToScreen = false, HideTrayIcon = true };
                    cfg.ScreenDevice = Screen.PrimaryScreen.DeviceName;
                    cfg.Position = edgePos;

                    // internal 接缝：免 UIController。状态迁移路径不触碰 _ui。
                    var helper = new MainFormBizHelper(form, cfg, new MainFormWinHelper(form));
                    form.Show();
                    Application.DoEvents();

                    // 1) 泊出：光标远离 + 贴右缘 ⇒ Timer tick 后 Park
                    Cursor.Position = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
                    helper.StartTimer();
                    Assert.True(PumpUntil(() => helper.IsHidden, TimeSpan.FromSeconds(6)), "未在期限内泊出");
                    Assert.NotEqual(edgePos, form.Location); // 窗体已被挪到泊出坐标（只剩边条）

                    // 2) RestorePos：完整退出泊出态（R5：旧实现残留 _parkedArea）
                    helper.RestorePos();
                    Assert.False(helper.IsHidden);
                    Assert.Equal(edgePos, form.Location); // 回到保存位置

                    // 3) 重新泊出（R5 回归核心）：旧缺陷下 _parkedArea 残留 + dock=None
                    //    ⇒ 纯决策恒 None，自动隐藏永久卡死；修复后必须能重新泊出
                    Assert.True(PumpUntil(() => helper.IsHidden, TimeSpan.FromSeconds(6)),
                        "RestorePos 后自动隐藏未能重新泊出——泊出态退出不完整（R5 回归）");

                    // 4) 关闭自动隐藏：恢复泊出前位置并保持可见
                    Point prePark = edgePos; // 第二次泊出前的位置（RestorePos 刚放回的位置）
                    cfg.AutoHide = false;
                    helper.DisableAutoHide();
                    Assert.False(helper.IsHidden);
                    Assert.Equal(prePark, form.Location);
                    Assert.True(PumpUntilStaysVisible(helper, form, prePark, TimeSpan.FromMilliseconds(900)),
                        "关闭自动隐藏后又消失或被挪走");
                }
                finally
                {
                    Cursor.Position = originalCursor; // 归还真实光标
                }
            });
        }

        /// <summary>STA 线程执行（WinForms Timer/窗口需要）；同 ReviewFix20261026Tests 手法。</summary>
        private static void RunSta(Action action)
        {
            Exception? captured = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromMinutes(2));
            if (!t.IsAlive && captured != null) throw captured;
            if (t.IsAlive) throw new TimeoutException("STA 测试线程 2 分钟未结束");
        }

        /// <summary>泵消息直到条件满足（250ms Timer 的 tick 依赖消息循环）。</summary>
        private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                if (condition()) return true;
                Thread.Sleep(25);
            }
            Application.DoEvents();
            return condition();
        }

        /// <summary>关闭自动隐藏后继续泵若干 tick：窗体保持可见、位置稳定。</summary>
        private static bool PumpUntilStaysVisible(MainFormBizHelper helper, Form form, Point expected, TimeSpan duration)
        {
            long deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                if (helper.IsHidden) return false;
                if (form.Location != expected) return false;
                Thread.Sleep(50);
            }
            return !helper.IsHidden && form.Location == expected;
        }

        private static int HostProcessCount() =>
            Process.GetProcessesByName("LiteMonitor.HardwareHost").Length;

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }
            return condition();
        }
    }
}
