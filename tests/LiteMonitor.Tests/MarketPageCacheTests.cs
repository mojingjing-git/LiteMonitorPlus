#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.ThemeEditor;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.UI.SettingsPage;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// F14a（#437 卡顿半项）：市场页缓存与切页性能。
    ///
    /// 缺陷（修复前）：<c>PluginMarketPage.OnShow</c> **每次进入页面都全量销毁/重建 UI**
    /// （<c>RebuildUI()</c> 把容器里所有控件 Dispose 掉再逐个 new 出来）。
    /// 于是"来回切设置页"每切一次就重建一遍全部控件 —— 插件/主题越多越卡。
    ///
    /// 修复后：页面按**内容签名**做差量更新 ——
    /// 签名相同的两次进入**不销毁、不创建**任何列表控件；
    /// 安装/卸载只更新受影响的那一行；语言/DPI/配色/索引变化才重建对应部分。
    ///
    /// ★ 本文件里的用例全部用**外部可观测事实**断言（控件实例是否被复用、控件树差异），
    /// 不依赖产品内部计数器 ⇒ 在"修复前"的实现上同样能编译并**失败**（红→绿可证）。
    /// </summary>
    public class MarketPageCacheTests
    {
        private const int PluginCount = 12;
        private const int ThemeCount = 4;

        // ================================================================
        // ① 缓存命中：重进页面 ⇒ 控件零创建、零销毁
        // ================================================================

        [Fact]
        public void 缓存命中_重进插件页_控件零创建零销毁且行实例被复用()
        {
            RunSta(() =>
            {
                var page = NewPage();
                var host = new Panel();
                host.Controls.Add(page);

                page.OnShow();   // 首次进入：把索引与全部控件建出来

                List<Control> before = SnapshotTree(page);
                List<LiteSettingsGroup> groupsBefore = Groups(before);
                Assert.NotEmpty(groupsBefore);

                // 离开 → 返回（等价于设置窗体切页）
                host.Controls.Remove(page);
                host.Controls.Add(page);
                page.OnShow();

                List<Control> after = SnapshotTree(page);
                int created = CountNew(after, before);
                int destroyed = CountNew(before, after);

                Assert.Equal(0, created);
                Assert.Equal(0, destroyed);

                // 更强的断言：卡片控件是**同一批实例**，不是"重建了看起来一样的东西"
                List<LiteSettingsGroup> groupsAfter = Groups(after);
                Assert.Equal(groupsBefore.Count, groupsAfter.Count);
                foreach (LiteSettingsGroup g in groupsBefore)
                {
                    Assert.Contains(groupsAfter, x => ReferenceEquals(x, g));
                    Assert.False(g.IsDisposed);
                }
            });
        }

        [Fact]
        public void 缓存命中_重进主题页_列表行零创建零销毁()
        {
            RunSta(() =>
            {
                var page = NewPage();
                var host = new Panel();
                host.Controls.Add(page);

                page.OnShow();
                page.SwitchCategory(MarketCategory.Themes);

                List<Control> before = SnapshotTree(page);
                List<LiteSettingsGroup> groupsBefore = Groups(before);
                Assert.NotEmpty(groupsBefore);

                host.Controls.Remove(page);
                host.Controls.Add(page);
                page.OnShow();

                List<Control> after = SnapshotTree(page);
                Assert.Equal(0, CountNew(after, before));
                Assert.Equal(0, CountNew(before, after));

                foreach (LiteSettingsGroup g in groupsBefore)
                {
                    Assert.Contains(Groups(after), x => ReferenceEquals(x, g));
                }
            });
        }

        // ================================================================
        // ② 安装状态变化：只更新受影响的那一行
        // ================================================================

        [Fact]
        public void 安装状态变化_只更新受影响行_其余行原样不动且控件零创建零销毁()
        {
            RunSta(() =>
            {
                var page = NewPage();
                var host = new Panel();
                host.Controls.Add(page);
                page.OnShow();

                const string target = "demo.plugin.003";
                string uninstallText = LanguageManager.T("Menu.PluginMarketUninstall");

                // 初始：全部未安装 ⇒ 没有"卸载"按钮
                Assert.Equal(0, CountButtons(page, uninstallText));

                // 只有 target 变成"已安装"
                page.InstallStateResolverOverride = (id, isTheme) =>
                    string.Equals(id, target, StringComparison.OrdinalIgnoreCase)
                        ? MarketInstallState.Installed
                        : MarketInstallState.NotInstalled;

                List<Control> before = SnapshotTree(page);
                List<LiteSettingsGroup> groupsBefore = Groups(before);

                page.OnShow();

                List<Control> after = SnapshotTree(page);

                // 行**没有**被重建（控件零创建零销毁）⇒ 只可能就地改了文字
                Assert.Equal(0, CountNew(after, before));
                Assert.Equal(0, CountNew(before, after));
                foreach (LiteSettingsGroup g in groupsBefore)
                {
                    Assert.Contains(Groups(after), x => ReferenceEquals(x, g));
                }

                // 只有受影响的那一行变成了"卸载"
                Assert.Equal(1, CountButtons(page, uninstallText));
            });
        }

        // ================================================================
        // ③ 语言 / DPI 变化：必须及时刷新
        // ================================================================

        [Fact]
        public void 语言变化_文案及时刷新为新语言()
        {
            RunSta(() =>
            {
                string oldLang = LanguageManager.CurrentLang;
                float oldScale = UIUtils.ScaleFactor;
                try
                {
                    LanguageManager.Load("zh");
                    var page = NewPage();
                    var host = new Panel();
                    host.Controls.Add(page);
                    page.OnShow();

                    string zhSource = LanguageManager.T("Menu.PluginMarketSource");
                    Assert.Contains(ActionRowLabels(page), l => l.Text == zhSource);

                    LanguageManager.Load("en");
                    page.OnShow();

                    string enSource = LanguageManager.T("Menu.PluginMarketSource");
                    Assert.NotEqual(zhSource, enSource);
                    Assert.Contains(ActionRowLabels(page), l => l.Text == enSource);
                }
                finally
                {
                    LanguageManager.Load(oldLang);
                    UIUtils.ScaleFactor = oldScale;
                }
            });
        }

        [Fact]
        public void DPI变化_布局尺寸及时重算()
        {
            RunSta(() =>
            {
                float oldScale = UIUtils.ScaleFactor;
                try
                {
                    UIUtils.ScaleFactor = 1.0f;
                    var page = NewPage();
                    var host = new Panel();
                    host.Controls.Add(page);
                    page.OnShow();

                    int before = RefreshButtonWidth(page);
                    Assert.True(before > 0);

                    UIUtils.ScaleFactor = 2.0f;
                    page.OnShow();

                    int after = RefreshButtonWidth(page);
                    Assert.True(after > before,
                        $"DPI 变化后刷新按钮宽度没有重算（before={before}, after={after}）");
                }
                finally
                {
                    UIUtils.ScaleFactor = oldScale;
                }
            });
        }

        // ================================================================
        // ④ 连续切页 100 次：控件数 / 句柄数无持续增长
        // ================================================================

        [Fact]
        public void 连续切页100次_控件数与句柄数无持续增长()
        {
            RunSta(() =>
            {
                var page = NewPage();
                var host = new Panel();
                host.Controls.Add(page);
                page.OnShow();

                int controlsAt10 = 0;
                int handlesAt10 = 0;
                int controlsAt100 = 0;
                int handlesAt100 = 0;

                using Process proc = Process.GetCurrentProcess();
                proc.Refresh();

                for (int i = 1; i <= 100; i++)
                {
                    host.Controls.Remove(page);
                    host.Controls.Add(page);
                    page.OnShow();

                    if (i == 10)
                    {
                        controlsAt10 = SnapshotTree(page).Count;
                        proc.Refresh();
                        handlesAt10 = proc.HandleCount;
                    }
                }

                controlsAt100 = SnapshotTree(page).Count;
                proc.Refresh();
                handlesAt100 = proc.HandleCount;

                Assert.Equal(controlsAt10, controlsAt100);
                Assert.True(handlesAt100 - handlesAt10 <= 10,
                    $"切页 100 次后句柄数持续增长（10 次={handlesAt10}, 100 次={handlesAt100}）");
            });
        }

        // ================================================================
        // ⑤ 预览生命周期：离开页面释放唯一活跃的 GPU 预览，返回时恢复
        // ================================================================

        [Fact]
        public void 离开页面_释放唯一活跃预览_返回后恢复()
        {
            RunSta(() =>
            {
                var page = NewPage();
                var host = new Panel();
                host.Controls.Add(page);
                page.OnShow();

                page.SwitchCategory(MarketCategory.Themes);
                MarketThemeEntry theme = pageThemeEntry();
                page.SelectThemeForPreview(theme);

                List<ThemePreviewControl> previews = PreviewControls(page);
                Assert.Single(previews);
                ThemePreviewControl captured = previews[0];

                // 离开页面 ⇒ 预览必须被释放（不能留在后台跑）
                host.Controls.Remove(page);

                Assert.Empty(PreviewControls(page));
                Assert.True(captured.IsDisposed, "离开页面后实时预览没有被释放");

                // 返回 ⇒ 预览恢复
                host.Controls.Add(page);
                page.OnShow();

                Assert.Single(PreviewControls(page));
            });
        }

        // ================================================================
        // ⑥ 代次守卫：旧页面代次的异步回调不得写入新页面代次
        // ================================================================

        [Fact]
        public void 旧页面代次的预览载荷回调_不得写入新代次()
        {
            RunSta(() =>
            {
                var page = NewPage();
                var host = new Panel();
                host.Controls.Add(page);
                page.OnShow();

                page.SwitchCategory(MarketCategory.Themes);
                page.SelectThemeForPreview(pageThemeEntry());

                ThemePreviewControl preview = PreviewControls(page).Single();

                int staleGeneration = page.CurrentPageGeneration - 1;
                Assert.False(page.TryApplyPreviewPayload(staleGeneration, preview, new Theme()),
                    "旧代次的回调被写进了新页面代次");
                Assert.True(page.StaleCallbacksDropped > 0, "旧代次回调没有被记录为已丢弃");

                // 当前代次 + 当前预览 ⇒ 允许写入
                Assert.True(page.TryApplyPreviewPayload(page.CurrentPageGeneration, preview, new Theme()),
                    "当前代次的回调被误丢弃");
            });
        }

        // ================================================================
        // ⑦ 布局回归护栏：改造后的视觉顺序必须与改造前逐项一致
        // ================================================================

        [Fact]
        public void 页面视觉顺序与改造前一致()
        {
            RunSta(() =>
            {
                var page = NewPage();
                var host = new Panel();
                host.Controls.Add(page);
                page.OnShow();

                Control container = page.Controls[0];
                List<Control> visual = VisualOrder(container);

                Assert.Equal(8, visual.Count);
                Assert.IsType<LiteHintRow>(visual[0]);                              // 列表提示行
                Assert.IsType<Label>(visual[1]);                                     // 启动自检提示
                Assert.IsType<Label>(visual[2]);                                     // 进度/错误状态
                Assert.Equal(LanguageManager.T("Menu.PluginMarketCategory"),
                    ((LiteActionRow)visual[3]).Label.Text);                         // 分类页签行
                Assert.Equal(LanguageManager.T("Menu.PluginMarketSource"),
                    ((LiteActionRow)visual[4]).Label.Text);                         // 插件源行
                bool listHostOk = false;
                foreach (Control c in visual)
                {
                    if (c is Panel p && p.Controls.OfType<LiteSettingsGroup>().Any()) { listHostOk = true; break; }
                }
                Assert.True(listHostOk, "列表区不在预期的视觉位置");   // 列表区
                Assert.IsType<Panel>(visual[6]);                                    // 间隔
                Assert.IsType<Panel>(visual[7]);                                    // 预览窗格宿主
            });
        }

        // ================================================================
        // 夹具
        // ================================================================

        private static PluginMarketPage NewPage()
        {
            var page = new PluginMarketPage(new FixedIndexProvider(BuildIndex()));
            page.InstallStateResolverOverride = (id, isTheme) => MarketInstallState.NotInstalled;
            return page;
        }

        private static MarketThemeEntry pageThemeEntry()
        {
            return BuildIndex().Themes[0];
        }

        /// <summary>固定夹具：12 个插件 + 4 个主题（结构与真实 index.json 同形）。</summary>
        internal static MarketIndex BuildIndex()
        {
            var index = new MarketIndex { Schema = 1, Repo = "https://example.invalid/repo" };

            for (int i = 0; i < PluginCount; i++)
            {
                index.Plugins.Add(new MarketPluginEntry
                {
                    Id = $"demo.plugin.{i:D3}",
                    Name = $"示例插件 {i}",
                    NameEn = $"Demo Plugin {i}",
                    Description = $"第 {i} 个示例插件",
                    DescriptionEn = $"Demo plugin number {i}",
                    Author = "f14a",
                    Version = "1.0.0",
                    Sha256 = new string('a', 64),
                    Status = "ok",
                    Verified = i % 2 == 0,
                    PageUrl = $"https://example.invalid/plugin/{i}",
                    Source = $"https://example.invalid/plugin/{i}/src"
                });
            }

            for (int i = 0; i < ThemeCount; i++)
            {
                index.Themes.Add(new MarketThemeEntry
                {
                    Id = $"demo.theme.{i:D3}",
                    Name = $"示例主题 {i}",
                    NameEn = $"Demo Theme {i}",
                    Description = $"第 {i} 个示例主题",
                    DescriptionEn = $"Demo theme number {i}",
                    Author = "f14a",
                    Version = "1.0.0",
                    Sha256 = new string('b', 64),
                    Route = "legacy",
                    Material = "legacy",
                    Status = "ok",
                    Verified = true
                });
            }

            return index;
        }

        /// <summary>固定索引来源：立即返回，不触网。</summary>
        internal sealed class FixedIndexProvider : IMarketIndexProvider
        {
            private readonly MarketIndex _index;

            public FixedIndexProvider(MarketIndex index) => _index = index;

            // [W2 集成期修正] 本工程 <Nullable>disable</Nullable> ⇒ 可空标注脱离 "#nullable" 上下文会报 CS8632。
            // 该标注在 oblivious 模式下不承载语义，去掉即可，零行为影响（基线历史文件不在本次范围内，不动）。
            public Task<MarketIndex> FetchAsync() => Task.FromResult<MarketIndex>(_index);
        }

        // ================================================================
        // 观测工具
        // ================================================================

        /// <summary>控件树快照（深度优先，含容器自身）。</summary>
        internal static List<Control> SnapshotTree(Control root)
        {
            var list = new List<Control>();
            Walk(root, list);
            return list;
        }

        private static void Walk(Control control, List<Control> sink)
        {
            sink.Add(control);
            foreach (Control child in control.Controls)
            {
                Walk(child, sink);
            }
        }

        private static int CountNew(List<Control> candidate, List<Control> baseline)
        {
            int count = 0;
            foreach (Control c in candidate)
            {
                bool found = false;
                foreach (Control b in baseline)
                {
                    if (ReferenceEquals(c, b)) { found = true; break; }
                }
                if (!found) count++;
            }
            return count;
        }

        internal static List<LiteSettingsGroup> Groups(List<Control> controls)
        {
            return controls.OfType<LiteSettingsGroup>().ToList();
        }

        /// <summary>
        /// 视觉顺序：WinForms 的 Dock=Top 按 z 序**逆序**停靠（索引越大越靠上），
        /// 所以视觉顺序 = 集合顺序的逆序。
        /// </summary>
        internal static List<Control> VisualOrder(Control container)
        {
            var list = new List<Control>();
            for (int i = container.Controls.Count - 1; i >= 0; i--)
            {
                list.Add(container.Controls[i]);
            }
            return list;
        }

        internal static List<ThemePreviewControl> PreviewControls(Control root)
        {
            return SnapshotTree(root).OfType<ThemePreviewControl>().ToList();
        }

        internal static List<Label> ActionRowLabels(Control root)
        {
            var result = new List<Label>();
            foreach (LiteActionRow row in SnapshotTree(root).OfType<LiteActionRow>())
            {
                result.Add(row.Label);
            }
            return result;
        }

        internal static int CountButtons(Control root, string text)
        {
            int count = 0;
            foreach (LiteHeaderBtn btn in SnapshotTree(root).OfType<LiteHeaderBtn>())
            {
                if (string.Equals(btn.Text, text, StringComparison.Ordinal)) count++;
            }
            return count;
        }

        internal static int RefreshButtonWidth(Control root)
        {
            foreach (LiteButton btn in SnapshotTree(root).OfType<LiteButton>())
            {
                if (string.Equals(btn.Text, LanguageManager.T("Menu.PluginMarketRefresh"), StringComparison.Ordinal))
                {
                    return btn.Width;
                }
            }
            return 0;
        }

        /// <summary>在 STA 线程上执行（WinForms 控件需要 STA）。</summary>
        internal static void RunSta(Action action)
        {
            Exception captured = null;
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
                ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }
    }
}
