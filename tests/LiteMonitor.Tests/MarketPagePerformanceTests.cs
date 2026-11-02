using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.UI.SettingsPage;
using Xunit;
using Xunit.Abstractions;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// F14a 性能留证：固定 100 项夹具，记录**首次**与**重复**打开耗时。
    ///
    /// <para>目标：重复打开的 UI 阻塞 p95 ≤ 100ms。修复前（每次进入页面全量重建 UI）
    /// 这一项必然超标；修复后（内容签名命中 ⇒ 控件零创建零销毁）应当显著下降。
    /// 测试直接使用事件与耗时观测验证缓存命中后的重建成本。</para>
    ///
    /// <para>★ 用 stopwatch 取时序（不是固定 sleep 冒充等待）：用例里没有任何
    /// <c>Thread.Sleep</c>，索引来源是立即完成的 Task ⇒ 全部同步、无真实等待。</para>
    /// </summary>
    public class MarketPagePerformanceTests
    {
        private readonly ITestOutputHelper _output;

        public MarketPagePerformanceTests(ITestOutputHelper output) => _output = output;

        private const int FixtureSize = 100;
        private const int RepeatSamples = 40;
        private const double TargetP95Ms = 100.0;

        [Fact]
        public void 固定100项_重复打开_p95不超过100ms()
        {
            MarketPageCacheTests.RunSta(() =>
            {
                var index = BuildFixture(FixtureSize);
                var page = new PluginMarketPage(new MarketPageCacheTests.FixedIndexProvider(index));
                page.InstallStateResolverOverride = (id, isTheme) => MarketInstallState.NotInstalled;

                var host = new Panel();
                host.Controls.Add(page);

                // ---------- 首次打开（含索引加载 + 全部行首次构建）----------
                var firstWatch = Stopwatch.StartNew();
                page.OnShow();
                firstWatch.Stop();
                double firstMs = firstWatch.Elapsed.TotalMilliseconds;

                int controlsAfterFirst = MarketPageCacheTests.SnapshotTree(page).Count;

                // ---------- 重复打开（切页往返）----------
                var samples = new List<double>(RepeatSamples);
                for (int i = 0; i < RepeatSamples; i++)
                {
                    host.Controls.Remove(page);
                    host.Controls.Add(page);

                    var watch = Stopwatch.StartNew();
                    page.OnShow();
                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }

                double p95 = Percentile(samples, 0.95);
                double min = samples.Min();
                double max = samples.Max();
                double median = Percentile(samples, 0.50);

                _output.WriteLine($"[F14a perf] fixture={FixtureSize} 首次打开={firstMs:F2}ms");
                _output.WriteLine($"[F14a perf] 重复打开 min={min:F2}ms median={median:F2}ms p95={p95:F2}ms max={max:F2}ms (n={samples.Count})");
                _output.WriteLine($"[F14a perf] 首次打开后控件树控件数={controlsAfterFirst}");

                Assert.True(p95 <= TargetP95Ms,
                    $"重复打开 p95={p95:F2}ms 超过目标 {TargetP95Ms:F0}ms（首次 {firstMs:F2}ms，max {max:F2}ms）");
            });
        }

        /// <summary>最近秩百分位（nearest-rank）。</summary>
        private static double Percentile(List<double> samples, double percentile)
        {
            List<double> sorted = samples.OrderBy(x => x).ToList();
            if (sorted.Count == 0) return 0;
            int rank = (int)Math.Ceiling(percentile * sorted.Count);
            if (rank < 1) rank = 1;
            if (rank > sorted.Count) rank = sorted.Count;
            return sorted[rank - 1];
        }

        /// <summary>固定 100 项夹具（插件 100 个）。</summary>
        private static MarketIndex BuildFixture(int count)
        {
            var index = new MarketIndex { Schema = 1, Repo = "https://example.invalid/repo" };
            for (int i = 0; i < count; i++)
            {
                index.Plugins.Add(new MarketPluginEntry
                {
                    Id = $"perf.plugin.{i:D3}",
                    Name = $"性能夹具插件 {i}",
                    NameEn = $"Perf Fixture Plugin {i}",
                    Description = $"第 {i} 个性能夹具插件，用于市场页切页性能留证。",
                    DescriptionEn = $"Perf fixture plugin number {i}, used for market page switch performance evidence.",
                    Author = "f14a",
                    Version = "1.0.0",
                    Sha256 = new string('a', 64),
                    Status = "ok",
                    Verified = i % 2 == 0,
                    PageUrl = $"https://example.invalid/plugin/{i}",
                    Source = $"https://example.invalid/plugin/{i}/src"
                });
            }
            return index;
        }
    }
}
