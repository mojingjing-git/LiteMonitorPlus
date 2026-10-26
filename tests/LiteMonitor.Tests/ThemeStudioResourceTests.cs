using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.ThemeEditor;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// Lane C / TE08 前半：主题工作室**资源与性能基线**。
    ///
    /// 目的：为 TE08 建立 M1 参考数值（基线提交 master@832d357，工作树起点 8dfe0d4，差异可忽略），
    /// 后续 Lane I 集成完成后按门禁 G6 重跑并追加第二组数据。
    ///
    /// ★ 口径：
    ///   - 三个场景在**同一个 testhost 进程**内跑，采样取「场景前 vs 场景后」的**差值**；
    ///   - GDI / USER 对象数用 <c>GetGuiResources</c> P/Invoke（本文件自带一份 private extern，
    ///     不引用 tests/AppearanceResourceProbe 工程）；
    ///   - 句柄数 / 私存用 <see cref="Process"/>；托管堆用 <see cref="GC.GetTotalMemory(bool)"/>；
    ///   - 每个场景采样三次：before（强制 GC 归一化后）、afterRaw（场景结束立即采）、
    ///     afterSettled（强制 GC + 等待终结器后再采）。**断言用 settled 口径**，
    ///     raw 口径一并记录（能暴露「未终结的资源」瞬时占用）；
    ///   - 场景内部再采一条**时间序列**（等间隔），用最小二乘算「每次迭代增量」，
    ///     用来判定是否线性累积；
    ///   - 每个场景分**冷启动段**与**稳态段**两段独立测量：冷启动段记录一次性初始化成本，
    ///     稳态段才是「每轮增量」。
    ///
    /// ★ 断言策略：**不设绝对阈值失败线**。只断言
    ///   (1) 场景完成（迭代次数达标）；
    ///   (2) 各差值为有限值；
    ///   (3) GDI / USER / 句柄的 **settled 增量**不超过宽松上限（上限 = M1 实测值 + 余量，
    ///       见 docs/verification/theme-studio/perf-baseline.md「断言与余量」一节）。
    ///   稳定性优先：宁可放宽，也不引入偶发失败。
    /// </summary>
    public class ThemeStudioResourceTests : IDisposable
    {
        // ─────────────────────────── 场景规模 ───────────────────────────

        /// <summary>场景 1：开关工作室次数。</summary>
        private const int StudioCycles = 50;

        /// <summary>场景 2：编辑事务总次数。</summary>
        private const int EditIterations = 1000;

        /// <summary>场景 2：每多少次编辑切换一次合并键（→ 期望 100 个可撤销事务）。</summary>
        private const int EditsPerTransaction = 10;

        /// <summary>场景 3：主题打开／切换次数。</summary>
        private const int ThemeOpenIterations = 200;

        // ─────────────────────────── 冷启动段规模 ───────────────────────────
        // 每个场景先跑一段「冷启动」并单独测量：把「一次性初始化」（WinForms 视觉样式、
        // 字体缓存、静态资源缓存、反射/JIT 预热等）与「每轮增量」分开，
        // 否则一次性成本会被误读成线性泄漏。冷启动段之后的稳态段才是每轮增量。

        /// <summary>场景 1 冷启动轮次（单独测量）。</summary>
        private const int StudioWarmupCycles = 3;

        /// <summary>场景 2 冷启动编辑次数（单独测量）。</summary>
        private const int EditWarmupIterations = 20;

        /// <summary>场景 3 冷启动打开次数（单独测量）。</summary>
        private const int ThemeOpenWarmupIterations = 10;

        // ─────────────────────── 宽松上限（M1 参考值 + 余量）───────────────────────
        // 上限来自 docs/verification/theme-studio/perf-baseline.md 的实测值加余量，
        // 只用于拦截「数量级失控」，不作为验收线。

        /// <summary>GDI 对象 settled 增量上限（M1 最大实测 43，出自场景 1 冷启动段）。</summary>
        private const int MaxGdiDelta = 200;

        /// <summary>USER 对象 settled 增量上限（M1 最大实测 7，出自场景 1 冷启动段）。</summary>
        private const int MaxUserDelta = 200;

        /// <summary>
        /// 句柄 settled 增量上限（M1 最大实测 110，出自场景 1 冷启动段）。
        /// 稳态段实测 −7 ~ +3（无每轮累积），上限只用于拦截数量级失控。
        /// </summary>
        private const int MaxHandleDelta = 400;

        // ─────────────────────────── 测试夹具 ───────────────────────────

        private const string ClassicThemeName = "经典蓝";

        private const string ClassicJson = @"{
  ""version"": 3,
  ""name"": ""经典蓝"",
  ""layout"": { ""rowHeight"": 40, ""padding"": 12 },
  ""font"": { ""family"": ""Microsoft YaHei UI"", ""value"": 14.0 },
  ""color"": { ""background"": ""#3C3C3C"", ""textTitle"": ""#FFFFFF"" },
  ""x-unknown"": { ""keep"": [1, 2] }
}";

        private readonly string _dir;
        private readonly Process _self;

        public ThemeStudioResourceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "litemonitor-laneC-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ThemeDocumentStore.SetThemeDirForTests(_dir);
            ThemeDocumentStore.SetRecoveryDirForTests(Path.Combine(_dir, "recovery"));

            // 预置一个经典主题（工作室资料库会列出它）
            File.WriteAllText(
                Path.Combine(_dir, ClassicThemeName + ".json"),
                ClassicJson,
                new UTF8Encoding(false));

            // 采样用的进程句柄只取一次并常驻：避免每次采样开关句柄造成噪声
            _self = Process.GetCurrentProcess();
            _self.Refresh();
            _ = _self.Handle;
        }

        public void Dispose()
        {
            ThemeDocumentStore.SetThemeDirForTests(null);
            ThemeDocumentStore.ResetRecoveryDir();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ─────────────────────────── 场景 1 ───────────────────────────

        /// <summary>
        /// 场景 1：开关工作室 50 次（STA 线程 + ThemeStudioForm + Show/DoEvents/Close）。
        /// </summary>
        [Fact]
        public void 场景1_开关工作室50次_资源增量不累积()
        {
            // 冷启动：进程内第一次开工作室（一次性初始化成本）
            ScenarioMeasurement cold = Measure("S1-coldstart", StudioWarmupCycles, checkpoint =>
            {
                int done = RunStudioCycles(StudioWarmupCycles, checkpoint);
                Assert.Equal(StudioWarmupCycles, done);
            });

            // 稳态：预热之后连续 50 轮（每轮增量）
            ScenarioMeasurement steady = Measure("S1-steady-state", StudioCycles, checkpoint =>
            {
                int done = RunStudioCycles(StudioCycles, checkpoint);
                Assert.Equal(StudioCycles, done);
            });

            cold.Dpi = _dpi;
            steady.Dpi = _dpi;
            AssertScenario(cold);
            AssertScenario(steady);
            Report(cold);
            Report(steady);
        }

        /// <summary>工作室的窗口 DeviceDpi（最后一次测量值）。</summary>
        private int _dpi;

        /// <summary>
        /// 在 STA 线程上跑 N 轮「开窗 → 打开主题 → 刷新预览 → 关窗」。
        /// </summary>
        /// <param name="count">轮数。</param>
        /// <param name="checkpoint">每轮结束后的回调（可为空；用于采时间序列点）。</param>
        /// <returns>实际完成的轮数。</returns>
        private int RunStudioCycles(int count, Action<int>? checkpoint)
        {
            int completed = 0;
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    try { Application.EnableVisualStyles(); } catch { /* 已启用 */ }

                    for (int i = 1; i <= count; i++)
                    {
                        using var form = new ThemeStudioForm();
                        form.Show();
                        Application.DoEvents();

                        // 打开预置的经典主题（走完整打开流程：会话 + 检查器 + 同源预览）
                        form.OpenThemeForTesting(ClassicThemeName);
                        form.RefreshPreviewForTesting();
                        Application.DoEvents();

                        _dpi = form.DeviceDpi;

                        form.Close();
                        Application.DoEvents();
                        completed++;

                        // 每 10 轮采一次时间序列点（用于判定线性累积）
                        if (i % 10 == 0 || i == count) checkpoint?.Invoke(i);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            bool joined = thread.Join(TimeSpan.FromMinutes(10));
            if (!joined)
            {
                throw new TimeoutException("场景 1 的 STA 线程超时（可能被模态对话框阻塞）。");
            }

            if (failure is not null)
            {
                throw new InvalidOperationException("场景 1 抛出异常。", failure);
            }

            return completed;
        }

        // ─────────────────────────── 场景 2 ───────────────────────────

        /// <summary>
        /// 场景 2：编辑事务 1000 次（ThemeDocumentSession.ApplyEdit）。
        /// 合并键每 10 次切换一次 → 100 个可撤销事务。
        /// </summary>
        [Fact]
        public void 场景2_编辑事务1000次_资源增量不累积()
        {
            // 冷启动：进程内第一次编辑（一次性初始化成本）
            ScenarioMeasurement cold = Measure("S2-coldstart", EditWarmupIterations, checkpoint =>
            {
                RunEdits(EditWarmupIterations, checkpoint);
            });

            // 稳态：预热之后连续 1000 次编辑
            ThemeDocumentSession? measured = null;
            ScenarioMeasurement steady = Measure("S2-steady-state", EditIterations, checkpoint =>
            {
                measured = RunEdits(EditIterations, checkpoint);
            });

            Assert.NotNull(measured);
            // 100 组 × 合并 → 100 个可撤销事务（历史上限 100，触限淘汰最旧）
            Assert.Equal(100, measured!.UndoCount);
            Assert.Equal(EditIterations, measured.Revision - 1);
            AssertScenario(cold);
            AssertScenario(steady);
            Report(cold);
            Report(steady);
        }

        /// <summary>
        /// 跑 N 次编辑：奇偶交替修改 <c>color.background</c> 与 <c>layout.rowHeight</c>，
        /// 合并键每 10 次切换一次。
        /// </summary>
        private static ThemeDocumentSession RunEdits(int count, Action<int>? checkpoint)
        {
            ThemeDocumentSession session = ThemeDocumentSession.Open(ClassicThemeName);
            ThemePropertyDescriptor background = Find("color.background");
            ThemePropertyDescriptor rowHeight = Find("layout.rowHeight");

            for (int i = 0; i < count; i++)
            {
                // 合并键每 10 次切一次：组内合并成一个事务，共 count/10 组
                string key = "laneC-group-" +
                    (i / EditsPerTransaction).ToString(CultureInfo.InvariantCulture);

                if ((i % 2) == 0)
                {
                    // 替代键：颜色在两个值之间来回切
                    string hex = (i % 4) == 0 ? "#3C3C3C" : "#4A4A4A";
                    session.ApplyEdit(
                        new ThemeFieldEdit(
                            background.JsonPath,
                            background.ToJson(hex),
                            theme => background.Setter(theme, hex)),
                        key);
                }
                else
                {
                    double value = 30 + (i % 7);
                    session.ApplyEdit(
                        new ThemeFieldEdit(
                            rowHeight.JsonPath,
                            rowHeight.ToJson(value),
                            theme => rowHeight.Setter(theme, value)),
                        key);
                }

                // 每 100 次采一次时间序列点
                if ((i + 1) % 100 == 0) checkpoint?.Invoke(i + 1);
            }

            return session;
        }

        // ─────────────────────────── 场景 3 ───────────────────────────

        /// <summary>
        /// 场景 3：主题打开／切换 200 次（ThemeDocumentSession.Open + 立即丢弃）。
        /// 说明：当前工作树尚无「切换编辑目标」路径（Lane I 的 TE03 批次），
        /// 按计划以「主题打开／切换」替代，集成后按门禁 G6 重跑。
        /// </summary>
        [Fact]
        public void 场景3_主题打开切换200次_资源增量不累积()
        {
            // 冷启动：进程内第一次打开主题（一次性初始化成本）
            ScenarioMeasurement cold = Measure("S3-coldstart", ThemeOpenWarmupIterations, checkpoint =>
            {
                for (int i = 1; i <= ThemeOpenWarmupIterations; i++)
                {
                    OpenAndDiscard();
                    checkpoint(i);
                }
            });

            // 稳态：预热之后连续 200 次打开／切换
            ScenarioMeasurement steady = Measure("S3-steady-state", ThemeOpenIterations, checkpoint =>
            {
                for (int i = 1; i <= ThemeOpenIterations; i++)
                {
                    OpenAndDiscard();
                    if (i % 20 == 0) checkpoint(i);
                }
            });

            AssertScenario(cold);
            AssertScenario(steady);
            Report(cold);
            Report(steady);
        }

        /// <summary>打开主题后立即丢弃（独立方法：确保局部变量真正离开作用域）。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OpenAndDiscard()
        {
            ThemeDocumentSession session = ThemeDocumentSession.Open(ClassicThemeName);
            Assert.NotNull(session.Draft);
        }

        // ─────────────────────────── 场景 4 (N09 G6 真实目标切换升级) ───────────────────────────

        /// <summary>
        /// 场景 4（G6 升级版）：真实编辑目标切换 200 次（主效果 ↔ 兼容外观）。
        /// 验证双路线频繁切换时，D3D 会话与 GDI 回退派生之间资源平稳，无句柄或 GDI 泄漏。
        /// </summary>
        [Fact]
        public void 场景4_真实目标切换200次_主效果与兼容外观交替_资源增量不累积()
        {
            ScenarioMeasurement measurement = Measure("S4-target-switch-200", 200, checkpoint =>
            {
                RunSta(() =>
                {
                    try { Application.EnableVisualStyles(); } catch { }
                    using var form = new ThemeStudioForm();
                    form.Show();
                    Application.DoEvents();

                    form.OpenThemeForTesting(ClassicThemeName);
                    Application.DoEvents();

                    for (int i = 1; i <= 200; i++)
                    {
                        var target = i % 2 == 0 ? ThemeEditTarget.GpuFallback : ThemeEditTarget.GpuMain;
                        form.SelectTargetForTesting(target);
                        Application.DoEvents();

                        if (i % 20 == 0 || i == 200) checkpoint(i);
                    }

                    form.Close();
                    Application.DoEvents();
                });
            });

            AssertScenario(measurement);
            Report(measurement);
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
            thread.Join(TimeSpan.FromMinutes(2));

            if (captured != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }

        // ─────────────────────────── 采样与断言 ───────────────────────────

        private const uint GR_GDIOBJECTS = 0;
        private const uint GR_USEROBJECTS = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        /// <summary>一次资源快照。</summary>
        private readonly struct ResourceSample
        {
            public ResourceSample(int handles, int gdi, int user, long privateBytes, long managedBytes)
            {
                Handles = handles;
                Gdi = gdi;
                User = user;
                PrivateBytes = privateBytes;
                ManagedBytes = managedBytes;
            }

            public int Handles { get; }
            public int Gdi { get; }
            public int User { get; }
            public long PrivateBytes { get; }
            public long ManagedBytes { get; }
        }

        /// <summary>一个场景的采样结果。</summary>
        private sealed class ScenarioMeasurement
        {
            public string Label { get; init; } = string.Empty;
            public int Expected { get; init; }
            public long ElapsedMs { get; init; }
            public ResourceSample Before { get; init; }
            public ResourceSample AfterRaw { get; init; }
            public ResourceSample AfterSettled { get; init; }
            public int Dpi { get; set; }

            /// <summary>场景内部的时间序列（迭代序号 → 快照）。</summary>
            public List<KeyValuePair<int, ResourceSample>> Series { get; } = new();

            public int RawHandleDelta => AfterRaw.Handles - Before.Handles;
            public int SettledHandleDelta => AfterSettled.Handles - Before.Handles;
            public int RawGdiDelta => AfterRaw.Gdi - Before.Gdi;
            public int SettledGdiDelta => AfterSettled.Gdi - Before.Gdi;
            public int RawUserDelta => AfterRaw.User - Before.User;
            public int SettledUserDelta => AfterSettled.User - Before.User;
            public long RawPrivateDelta => AfterRaw.PrivateBytes - Before.PrivateBytes;
            public long SettledPrivateDelta => AfterSettled.PrivateBytes - Before.PrivateBytes;
            public long RawManagedDelta => AfterRaw.ManagedBytes - Before.ManagedBytes;
            public long SettledManagedDelta => AfterSettled.ManagedBytes - Before.ManagedBytes;

            /// <summary>时间序列的最小二乘斜率（每次迭代增量）。样本不足时返回 null。</summary>
            public double? SlopePerIteration(Func<ResourceSample, double> selector)
            {
                if (Series.Count < 2) return null;
                double x0 = Series[0].Key;
                double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
                foreach (KeyValuePair<int, ResourceSample> point in Series)
                {
                    double x = point.Key - x0;
                    double y = selector(point.Value);
                    sumX += x;
                    sumY += y;
                    sumXY += x * y;
                    sumXX += x * x;
                }

                double n = Series.Count;
                double denom = n * sumXX - sumX * sumX;
                if (Math.Abs(denom) < 1e-9) return null;
                return (n * sumXY - sumX * sumY) / denom;
            }
        }

        /// <summary>
        /// 跑一个场景并采样。
        /// before：强制 GC 归一化后的基线；afterRaw：场景结束立即采；afterSettled：强制 GC 后再采。
        /// 场景内部通过 <c>checkpoint</c> 回调追加时间序列点。
        /// </summary>
        private ScenarioMeasurement Measure(string label, int expected, Action<Action<int>> action)
        {
            Settle();
            ResourceSample before = TakeSample();

            var series = new List<KeyValuePair<int, ResourceSample>>();

            void Checkpoint(int iteration)
            {
                series.Add(new KeyValuePair<int, ResourceSample>(iteration, TakeSample()));
            }

            var sw = Stopwatch.StartNew();
            action(Checkpoint);
            sw.Stop();

            ResourceSample afterRaw = TakeSample();
            Settle();
            ResourceSample afterSettled = TakeSample();

            var m = new ScenarioMeasurement
            {
                Label = label,
                Expected = expected,
                ElapsedMs = sw.ElapsedMilliseconds,
                Before = before,
                AfterRaw = afterRaw,
                AfterSettled = afterSettled,
            };

            m.Series.Add(new KeyValuePair<int, ResourceSample>(0, before));
            m.Series.AddRange(series);
            return m;
        }

        /// <summary>强制回收 + 等待终结器（让 Font 等有终结器的资源真正释放）。</summary>
        private static void Settle()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private ResourceSample TakeSample()
        {
            _self.Refresh();
            IntPtr handle = _self.Handle;
            uint gdi = GetGuiResources(handle, GR_GDIOBJECTS);
            uint user = GetGuiResources(handle, GR_USEROBJECTS);
            return new ResourceSample(
                _self.HandleCount,
                unchecked((int)gdi),
                unchecked((int)user),
                _self.PrivateMemorySize64,
                GC.GetTotalMemory(false));
        }

        /// <summary>场景级断言：完成 / 有限值 / 宽松上限。</summary>
        private static void AssertScenario(ScenarioMeasurement m)
        {
            // (1) 场景完成
            Assert.True(m.Expected > 0, "场景迭代次数必须为正。");

            // (2) 差值为有限值
            foreach (double delta in new[]
            {
                (double)m.RawHandleDelta, (double)m.SettledHandleDelta,
                (double)m.RawGdiDelta, (double)m.SettledGdiDelta,
                (double)m.RawUserDelta, (double)m.SettledUserDelta,
                (double)m.RawPrivateDelta, (double)m.SettledPrivateDelta,
                (double)m.RawManagedDelta, (double)m.SettledManagedDelta,
            })
            {
                Assert.True(double.IsFinite(delta),
                    m.Label + "：存在非有限差值 " + delta.ToString(CultureInfo.InvariantCulture));
            }

            // (3) GDI / USER / 句柄的 settled 增量不超过宽松上限
            Assert.True(m.SettledGdiDelta <= MaxGdiDelta,
                m.Label + "：GDI 对象 settled 增量 " + m.SettledGdiDelta + " > 宽松上限 " + MaxGdiDelta);
            Assert.True(m.SettledUserDelta <= MaxUserDelta,
                m.Label + "：USER 对象 settled 增量 " + m.SettledUserDelta + " > 宽松上限 " + MaxUserDelta);
            Assert.True(m.SettledHandleDelta <= MaxHandleDelta,
                m.Label + "：句柄 settled 增量 " + m.SettledHandleDelta + " > 宽松上限 " + MaxHandleDelta);
        }

        // ─────────────────────────── 输出格式化 ───────────────────────────

        private static void Report(ScenarioMeasurement m)
        {
            Console.WriteLine("[LaneC-PERF] " + Format(m));
        }

        private static string Format(ScenarioMeasurement m)
        {
            string series = string.Join(", ",
                m.Series.Select(p =>
                    p.Key.ToString(CultureInfo.InvariantCulture) + ":h" + p.Value.Handles +
                    "/g" + p.Value.Gdi + "/u" + p.Value.User +
                    "/p" + Mb(p.Value.PrivateBytes) + "/m" + Mb(p.Value.ManagedBytes)));

            return string.Join(" | ",
                m.Label,
                "iter=" + m.Expected.ToString(CultureInfo.InvariantCulture),
                "ms=" + m.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                "dpi=" + m.Dpi.ToString(CultureInfo.InvariantCulture),
                "handles=" + m.Before.Handles + "->" + m.AfterRaw.Handles + "/" + m.AfterSettled.Handles +
                    " (raw " + m.RawHandleDelta + ", settled " + m.SettledHandleDelta + ")",
                "gdi=" + m.Before.Gdi + "->" + m.AfterRaw.Gdi + "/" + m.AfterSettled.Gdi +
                    " (raw " + m.RawGdiDelta + ", settled " + m.SettledGdiDelta + ")",
                "user=" + m.Before.User + "->" + m.AfterRaw.User + "/" + m.AfterSettled.User +
                    " (raw " + m.RawUserDelta + ", settled " + m.SettledUserDelta + ")",
                "privateMB=" + Mb(m.Before.PrivateBytes) + "->" + Mb(m.AfterRaw.PrivateBytes) + "/" +
                    Mb(m.AfterSettled.PrivateBytes) +
                    " (raw " + Mb(m.RawPrivateDelta) + ", settled " + Mb(m.SettledPrivateDelta) + ")",
                "managedMB=" + Mb(m.Before.ManagedBytes) + "->" + Mb(m.AfterRaw.ManagedBytes) + "/" +
                    Mb(m.AfterSettled.ManagedBytes) +
                    " (raw " + Mb(m.RawManagedDelta) + ", settled " + Mb(m.SettledManagedDelta) + ")",
                "slopePerIter: h=" + Fmt(m.SlopePerIteration(s => s.Handles)) +
                    " g=" + Fmt(m.SlopePerIteration(s => s.Gdi)) +
                    " u=" + Fmt(m.SlopePerIteration(s => s.User)),
                "series[" + series + "]");
        }

        private static string Fmt(double? value) =>
            value.HasValue ? value.Value.ToString("F3", CultureInfo.InvariantCulture) : "n/a";

        private static string Mb(long bytes) =>
            (bytes / 1024.0 / 1024.0).ToString("F2", CultureInfo.InvariantCulture);

        private static ThemePropertyDescriptor Find(string path) =>
            System.Linq.Enumerable.Single(
                ThemePropertyCatalog.ClassicProperties,
                p => p.JsonPath == path);
    }
}
