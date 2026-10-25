#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.UI.Controls;
using LiteMonitor.src.UI.SettingsPage;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// F14b（#437 暗色半项）：设置页明暗主题。
    /// </summary>
    public class SettingsThemeTests
    {
        /// <summary>在 STA 线程上执行（WinForms 控件需要 STA）。</summary>
        private static void RunSta(Action action)
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

        // ================================================================
        // ① 调色板本体：深色必须真的在深的一侧，且所有槽位都齐
        // ================================================================

        [Fact]
        public void 深色配色的底色必须明显比浅色暗()
        {
            double lightLum = SettingsThemePalette.Luminance(SettingsThemePalette.Light.MainBg);
            double darkLum = SettingsThemePalette.Luminance(SettingsThemePalette.Dark.MainBg);

            Assert.True(darkLum < lightLum,
                string.Format("深色主底色的亮度({0:F4})必须低于浅色({1:F4})", darkLum, lightLum));
            Assert.True(darkLum <= 0.10,
                string.Format("深色主底色应当足够暗，实测亮度 {0:F4}", darkLum));
            Assert.True(lightLum >= 0.60,
                string.Format("浅色主底色应当足够亮，实测亮度 {0:F4}", lightLum));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void 深色下每个语义槽位都与浅色不同(bool dark)
        {
            var palette = SettingsThemePalette.FromDarkFlag(dark);
            var other = SettingsThemePalette.FromDarkFlag(!dark);

            foreach (var slot in SettingsThemePalette.AllSlots(palette))
            {
                if (slot.Role == nameof(SettingsThemePalette.MenuSelectedText)) continue;

                Assert.NotEqual(other.Get(slot.Role).ToArgb(), slot.Color.ToArgb());
            }
        }

        [Fact]
        public void 浅色与深色的槽位清单完全一致()
        {
            var lightRoles = SettingsThemePalette.AllSlots(SettingsThemePalette.Light)
                .Select(s => s.Role).OrderBy(r => r, StringComparer.Ordinal).ToList();
            var darkRoles = SettingsThemePalette.AllSlots(SettingsThemePalette.Dark)
                .Select(s => s.Role).OrderBy(r => r, StringComparer.Ordinal).ToList();

            Assert.Equal(lightRoles, darkRoles);
        }

        // ================================================================
        // ② 覆盖度：不只是改背景色 —— 文字/输入框/菜单/禁用/选中/预览都要落在两面 side
        // ================================================================

        [Theory]
        [InlineData("MainBg")]
        [InlineData("SurfaceBg")]
        [InlineData("InputBg")]
        [InlineData("MenuBg")]
        [InlineData("PreviewBg")]
        [InlineData("GroupHeader")]
        [InlineData("NavSelected")]
        [InlineData("BannerBg")]
        public void 深色下的容器类底色全部落在深的一侧(string role)
        {
            foreach (bool dark in new[] { false, true })
            {
                var palette = SettingsThemePalette.FromDarkFlag(dark);
                double lum = SettingsThemePalette.Luminance(palette.Get(role));

                if (dark)
                {
                    Assert.True(lum <= 0.25, role + " 在深色下仍然偏亮: " + lum.ToString("F4"));
                }
                else
                {
                    Assert.True(lum >= 0.55, role + " 在浅色下偏暗: " + lum.ToString("F4"));
                }
            }
        }

        [Theory]
        [InlineData("TextMain")]
        [InlineData("TextSub")]
        [InlineData("InputText")]
        [InlineData("MenuText")]
        [InlineData("Hint")]
        [InlineData("TextDisabled")]
        public void 深色下的文字色全部落在浅的一侧(string role)
        {
            foreach (bool dark in new[] { false, true })
            {
                var palette = SettingsThemePalette.FromDarkFlag(dark);
                double lum = SettingsThemePalette.Luminance(palette.Get(role));

                if (dark)
                {
                    Assert.True(lum >= 0.35, role + " 在深色下太暗，读不清: " + lum.ToString("F4"));
                }
                else
                {
                    Assert.True(lum <= 0.65, role + " 在浅色下太亮: " + lum.ToString("F4"));
                }
            }
        }

        // ================================================================
        // ③ 可读性（正文在各自底色上的对比度下限）
        // ================================================================

        [Theory]
        [InlineData("TextMain", "MainBg", 4.5)]
        [InlineData("TextSub", "MainBg", 3.0)]
        [InlineData("TextMain", "CardBg", 4.5)]
        [InlineData("InputText", "InputBg", 4.5)]
        [InlineData("MenuText", "MenuBg", 4.5)]
        [InlineData("MenuSelectedText", "MenuSelectedBg", 4.5)]
        [InlineData("BannerText", "BannerBg", 4.5)]
        [InlineData("TextWarn", "CardBg", 3.0)]
        [InlineData("TextCrit", "CardBg", 3.0)]
        public void 文字在各自底上的对比度达标(string fgRole, string bgRole, double minContrast)
        {
            foreach (bool dark in new[] { false, true })
            {
                var palette = SettingsThemePalette.FromDarkFlag(dark);
                double contrast = SettingsThemePalette.Contrast(palette.Get(fgRole), palette.Get(bgRole));

                Assert.True(contrast >= minContrast,
                    string.Format("{0}/{1} 在 {2} 下对比度 {3:F2} < {4}",
                        fgRole, bgRole, palette.Name, contrast, minContrast));
            }
        }

        // ================================================================
        // ④ 边界状态：禁用要看得见、但要与可执行区分开
        // ================================================================

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void 禁用文字与正文不同色_但不至于读不清(bool dark)
        {
            var palette = SettingsThemePalette.FromDarkFlag(dark);

            Assert.NotEqual(palette.TextMain.ToArgb(), palette.TextDisabled.ToArgb());

            double contrast = SettingsThemePalette.Contrast(palette.TextDisabled, palette.MainBg);
            Assert.True(contrast >= 1.8,
                string.Format("禁用文字在 {0} 下对比度 {1:F2} 过低, 等于看不见", palette.Name, contrast));

            double enabledContrast = SettingsThemePalette.Contrast(palette.TextMain, palette.MainBg);
            Assert.True(contrast < enabledContrast,
                "禁用文字的对比度不该高于正文, 否则看着像可执行");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void 选中态与悬停态各自区分(bool dark)
        {
            var palette = SettingsThemePalette.FromDarkFlag(dark);

            Assert.NotEqual(palette.NavSelected.ToArgb(), palette.NavHover.ToArgb());
            Assert.NotEqual(palette.NavSelected.ToArgb(), palette.SidebarBg.ToArgb());
            Assert.NotEqual(palette.NavHover.ToArgb(), palette.SidebarBg.ToArgb());
        }

        // ================================================================
        // ⑤ 三态模式：跟随系统 / 浅色 / 深色
        // ================================================================

        [Fact]
        public void 三个模式的方向性_显式模式不受系统配色影响()
        {
            SettingsThemeManager.OverrideSystemUsesLightThemeForTest(false); // 系统深色
            Assert.True(SettingsThemeManager.IsModeDark(SettingThemeMode.System));
            Assert.True(SettingsThemeManager.IsModeDark(SettingThemeMode.Dark));
            Assert.False(SettingsThemeManager.IsModeDark(SettingThemeMode.Light));

            SettingsThemeManager.OverrideSystemUsesLightThemeForTest(true);  // 系统浅色
            Assert.False(SettingsThemeManager.IsModeDark(SettingThemeMode.System));
            Assert.True(SettingsThemeManager.IsModeDark(SettingThemeMode.Dark));
            Assert.False(SettingsThemeManager.IsModeDark(SettingThemeMode.Light));
        }

        [Fact]
        public void 系统配色读取失败时按浅色处理_不把界面翻黑()
        {
            SettingsThemeManager.OverrideSystemUsesLightThemeForTest(null);
            Assert.False(SettingsThemeManager.IsModeDark(SettingThemeMode.System));
        }

        [Fact]
        public void 枚举零值是跟随系统_保证旧配置迁移后观感不变()
        {
            Assert.Equal(0, (int)SettingThemeMode.System);

            var fresh = new LiteMonitor.Settings();
            Assert.Equal(SettingThemeMode.System, fresh.SettingThemeMode);
        }

        [Fact]
        public void 配置里的越界值被夹回跟随系统_而不是让整份配置加载失败()
        {
            var settings = new LiteMonitor.Settings();

            settings.SettingThemeMode = (SettingThemeMode)7;
            Assert.Equal(SettingThemeMode.System, settings.SettingThemeMode);

            settings.SettingThemeMode = (SettingThemeMode)(-3);
            Assert.Equal(SettingThemeMode.System, settings.SettingThemeMode);

            settings.SettingThemeMode = SettingThemeMode.Dark;
            Assert.Equal(SettingThemeMode.Dark, settings.SettingThemeMode);
        }

        // ================================================================
        // ⑥ 立即预览 + Draft-Commit 语义
        // ================================================================

        [Fact]
        public void 预览不写配置_取消后回到已提交状态()
        {
            SettingsThemeManager.OverrideSystemUsesLightThemeForTest(true);

            var settings = new LiteMonitor.Settings();
            settings.SettingThemeMode = SettingThemeMode.System;
            SettingsThemeManager.SetCommittedForTest(SettingThemeMode.System);

            Assert.False(SettingsThemeManager.HasPendingPreview);
            Assert.False(SettingsThemeManager.IsDarkNow);

            Assert.True(SettingsThemeManager.BeginPreview(SettingThemeMode.Dark));
            Assert.True(SettingsThemeManager.HasPendingPreview);
            Assert.True(SettingsThemeManager.IsDarkNow);

            // ★ 未保存不生效：配置对象一个字节都不动
            Assert.Equal(SettingThemeMode.System, settings.SettingThemeMode);

            SettingsThemeManager.CancelPreview();
            Assert.False(SettingsThemeManager.HasPendingPreview);
            Assert.False(SettingsThemeManager.IsDarkNow);
            Assert.Equal(SettingThemeMode.System, SettingsThemeManager.CommittedMode);
        }

        [Fact]
        public void 预览只有走过一次Commit才改变已提交模式()
        {
            SettingsThemeManager.SetCommittedForTest(SettingThemeMode.System);
            SettingsThemeManager.BeginPreview(SettingThemeMode.Dark);
            Assert.Equal(SettingThemeMode.System, SettingsThemeManager.CommittedMode);

            SettingsThemeManager.CommitPreview();
            Assert.Equal(SettingThemeMode.Dark, SettingsThemeManager.CommittedMode);
            Assert.False(SettingsThemeManager.HasPendingPreview);
        }

        [Fact]
        public void 重复预览到同一个值不触发多余的重绘通知()
        {
            SettingsThemeManager.SetCommittedForTest(SettingThemeMode.System);

            int notifications = 0;
            EventHandler handler = (s, e) => notifications++;
            SettingsThemeManager.ThemeChanged += handler;
            try
            {
                Assert.True(SettingsThemeManager.BeginPreview(SettingThemeMode.Dark));
                int afterFirst = notifications;

                Assert.False(SettingsThemeManager.BeginPreview(SettingThemeMode.Dark));
                Assert.Equal(afterFirst, notifications);
            }
            finally
            {
                SettingsThemeManager.ThemeChanged -= handler;
            }
        }

        [Fact]
        public void 系统配色翻转只影响跟随系统模式_不打扰显式选择()
        {
            SettingsThemeManager.OverrideSystemUsesLightThemeForTest(true);

            SettingsThemeManager.SetCommittedForTest(SettingThemeMode.Light);
            Assert.False(SettingsThemeManager.ApplySystemColorChange(false));
            Assert.False(SettingsThemeManager.IsDarkNow);

            SettingsThemeManager.SetCommittedForTest(SettingThemeMode.Dark);
            Assert.False(SettingsThemeManager.ApplySystemColorChange(true));
            Assert.True(SettingsThemeManager.IsDarkNow);

            SettingsThemeManager.SetCommittedForTest(SettingThemeMode.System);
            Assert.False(SettingsThemeManager.IsDarkNow);
            Assert.True(SettingsThemeManager.ApplySystemColorChange(false));
            Assert.True(SettingsThemeManager.IsDarkNow);
        }

        // ================================================================
        // ⑦ 与悬浮窗皮肤体系互不串味
        // ================================================================

        [Fact]
        public void 明暗模式不改动悬浮窗皮肤字段()
        {
            var settings = new LiteMonitor.Settings();
            settings.Skin = "DarkFlat_Classic";

            settings.SettingThemeMode = SettingThemeMode.Dark;

            Assert.Equal("DarkFlat_Classic", settings.Skin);
        }

        [Fact]
        public void 皮肤字段翻值不影响明暗模式()
        {
            var settings = new LiteMonitor.Settings();
            settings.SettingThemeMode = SettingThemeMode.Light;

            settings.Skin = "SomeVeryDarkTheme";

            Assert.Equal(SettingThemeMode.Light, settings.SettingThemeMode);
        }

        // ================================================================
        // ⑧ 推送到 UIColors：两套配色的槽位都要真的换过去
        // ================================================================

        [Fact]
        public void 推送深色后UIColors的12个原槽位全部改变()
        {
            SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);
            Color[] light =
            {
                UIColors.MainBg, UIColors.SidebarBg, UIColors.CardBg, UIColors.Border,
                UIColors.Primary, UIColors.TextMain, UIColors.TextSub, UIColors.GroupHeader,
                UIColors.NavSelected, UIColors.NavHover, UIColors.TextWarn, UIColors.TextCrit,
            };

            SettingsThemeManager.PushToUIColors(SettingsThemePalette.Dark);
            Color[] dark =
            {
                UIColors.MainBg, UIColors.SidebarBg, UIColors.CardBg, UIColors.Border,
                UIColors.Primary, UIColors.TextMain, UIColors.TextSub, UIColors.GroupHeader,
                UIColors.NavSelected, UIColors.NavHover, UIColors.TextWarn, UIColors.TextCrit,
            };

            for (int i = 0; i < light.Length; i++)
            {
                Assert.NotEqual(light[i].ToArgb(), dark[i].ToArgb());
            }
        }

        // ================================================================
        // ⑧-bis 槽位两两不同（重映射歧义的根因护栏）
        //   ★ 为什么必须钉住：Remap 是"按色值反查语义槽位"。只要同一套配色里有
        //     两个槽位取了相同 ARGB，白/黑底就会一律命中数组里靠前的那一个，
        //     输入框被刷成卡片色、卡片被刷成输入框色 —— 且只在切换方向相反时才暴露，
        //     极难在手工观感检查中发现。
        //     （本条为根因护栏：修复前 CardBg/SurfaceBg/InputBg/MenuBg 在浅色下全是 Color.White。）
        // ================================================================

        [Fact]
        public void 同一套配色内没有两个槽位取相同色值_否则重映射必然串味()
        {
            // ★ 只校验"重映射会真的串味"的两类：容器底色 与 正文字色。
            //   其余同值是**有意的同义复用**，强行区分反而破坏观感一致性：
            //     · Primary == InputLineFocused（聚焦描边就该用主色）
            //     · CardBg == MenuSelectedText（一底一字，都是白，谈不上串）
            //     · SidebarBg == Separator（同为浅灰/深灰分隔色）
            //   它们即便被 Remap 命中，语义上也落在同一处，不会互相污染。
            string[] containerRoles = { "MainBg", "SidebarBg", "CardBg", "SurfaceBg", "GroupHeader", "InputBg", "MenuBg", "PreviewBg", "BannerBg" };
            string[] textRoles = { "TextMain", "TextSub", "TextDisabled", "InputText", "MenuText", "Hint", "BannerText" };

            foreach (bool isDark in new[] { false, true })
            {
                var palette = SettingsThemePalette.FromDarkFlag(isDark);
                var slots = SettingsThemePalette.AllSlots(palette);

                foreach (var group in new[] { containerRoles, textRoles })
                {
                    var byArgb = new Dictionary<int, List<string>>();
                    foreach (var slot in slots.Where(s => group.Contains(s.Role)))
                    {
                        if (!byArgb.TryGetValue(slot.Color.ToArgb(), out var names))
                        {
                            names = new List<string>();
                            byArgb[slot.Color.ToArgb()] = names;
                        }
                        names.Add(slot.Role);
                    }

                    var collisions = byArgb.Where(kv => kv.Value.Count > 1)
                                           .Select(kv => string.Join("=", kv.Value))
                                           .ToList();
                    Assert.True(collisions.Count == 0,
                        (isDark ? "深色" : "浅色") + "配色内存在同值槽位（重映射会串味）: "
                        + string.Join(" | ", collisions));
                }
            }
        }

        [Fact]
        public void 推送后UIColors与来源调色板逐槽位一致()
        {
            foreach (bool isDark in new[] { false, true })
            {
                var palette = SettingsThemePalette.FromDarkFlag(isDark);
                SettingsThemeManager.PushToUIColors(palette);

                Assert.Equal(palette.MainBg.ToArgb(), UIColors.MainBg.ToArgb());
                Assert.Equal(palette.InputBg.ToArgb(), UIColors.InputBg.ToArgb());
                Assert.Equal(palette.InputText.ToArgb(), UIColors.InputText.ToArgb());
                Assert.Equal(palette.TextDisabled.ToArgb(), UIColors.TextDisabled.ToArgb());
                Assert.Equal(palette.Separator.ToArgb(), UIColors.Separator.ToArgb());
                Assert.Equal(palette.Hint.ToArgb(), UIColors.Hint.ToArgb());
                Assert.Equal(palette.MenuBg.ToArgb(), UIColors.MenuBg.ToArgb());
                Assert.Equal(palette.MenuText.ToArgb(), UIColors.MenuText.ToArgb());
                Assert.Equal(palette.MenuSelectedBg.ToArgb(), UIColors.MenuSelectedBg.ToArgb());
                Assert.Equal(palette.PreviewBg.ToArgb(), UIColors.PreviewBg.ToArgb());
                Assert.Equal(palette.BannerBg.ToArgb(), UIColors.BannerBg.ToArgb());
                Assert.Equal(palette.BannerText.ToArgb(), UIColors.BannerText.ToArgb());
            }
        }

        // ================================================================
        // ⑨ 重映射：能把旧配色的槽位带到新配色的同义槽位，且不碰用户的自定义色
        // ================================================================

        [Fact]
        public void 重映射把旧配色的槽位带到新配色的同义槽位()
        {
            SettingsThemeApplier.Remap(SettingsThemePalette.Light.MainBg, dark: true)
                .ToArgb().Equals(SettingsThemePalette.Dark.MainBg.ToArgb());

            Assert.Equal(
                SettingsThemePalette.Dark.InputBg.ToArgb(),
                SettingsThemeApplier.Remap(SettingsThemePalette.Light.InputBg, dark: true).ToArgb());

            Assert.Equal(
                SettingsThemePalette.Light.TextMain.ToArgb(),
                SettingsThemeApplier.Remap(SettingsThemePalette.Dark.TextMain, dark: false).ToArgb());
        }

        [Fact]
        public void 已经在目标配色里的色保持不动_幂等()
        {
            foreach (bool dark in new[] { false, true })
            {
                var palette = SettingsThemePalette.FromDarkFlag(dark);
                foreach (var slot in SettingsThemePalette.AllSlots(palette))
                {
                    Color once = SettingsThemeApplier.Remap(slot.Color, dark);
                    Color twice = SettingsThemeApplier.Remap(once, dark);
                    Assert.Equal(once.ToArgb(), twice.ToArgb());
                }
            }
        }

        [Fact]
        public void 用户自定义颜色不被主题改写()
        {
            Color custom = Color.FromArgb(255, 123, 45);
            Assert.Equal(custom.ToArgb(), SettingsThemeApplier.Remap(custom, dark: true).ToArgb());
            Assert.Equal(custom.ToArgb(), SettingsThemeApplier.Remap(custom, dark: false).ToArgb());
            Assert.False(SettingsThemeApplier.IsThemeColor(custom));
        }

        [Fact]
        public void 透明色不参与主题重映射()
        {
            Assert.Equal(Color.Transparent.ToArgb(),
                SettingsThemeApplier.Remap(Color.Transparent, dark: true).ToArgb());
        }

        // ================================================================
        // ⑩ 端到端：真实控件树在两种模式下的渲染一致性
        // ================================================================

        [Fact]
        public void 控件树切到深色后不再残留浅色的白底黑字()
        {
            RunSta(() =>
            {
                SettingsThemeManager.OverrideSystemUsesLightThemeForTest(true);
                SettingsThemeManager.SetCommittedForTest(SettingThemeMode.System);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);

                using var group = new LiteSettingsGroup("测试分组");
                using var input = new LiteUnderlineInput("abc", "GB", "", 120);
                using var combo = new LiteComboBox();
                using var nav = new LiteNavBtn("导航");
                using var host = new Panel();
                host.Controls.Add(group);
                host.Controls.Add(input);
                host.Controls.Add(combo);
                host.Controls.Add(nav);

                // ★ 先让控件树按当前（浅色）配色取一次色，再断言"此时没有深色残留"——
                //   这才是本步的真正前提：控件刚构造时的硬编码色必须已被浅色配色接管。
                //   （原先写成 darkExpected: true 是前提写反了：此时刚 Push 的是 Light，
                //     判"应为深色"必然残留，与实现无关。）
                SettingsThemeApplier.Apply(host);
                Assert.False(HasLightArtifact(host, darkExpected: false));

                // 切到深色
                SettingsThemeManager.SetCommittedForTest(SettingThemeMode.Dark);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Dark);
                SettingsThemeApplier.Apply(host);

                var offenders = ListLightArtifacts(host, darkExpected: true);
                Assert.True(offenders.Count == 0,
                    "深色下仍残留浅底色控件: " + string.Join(" | ", offenders.Take(8)));
            });
        }

        [Fact]
        public void 深色再切回浅色能完整还原_不残留深色残留()
        {
            RunSta(() =>
            {
                SettingsThemeManager.OverrideSystemUsesLightThemeForTest(true);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);

                using var group = new LiteSettingsGroup("测试分组");
                using var input = new LiteUnderlineInput("abc", "GB", "", 120);
                using var host = new Panel();
                host.Controls.Add(group);
                host.Controls.Add(input);

                SettingsThemeManager.SetCommittedForTest(SettingThemeMode.Dark);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Dark);
                SettingsThemeApplier.Apply(host);

                SettingsThemeManager.SetCommittedForTest(SettingThemeMode.Light);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);
                SettingsThemeApplier.Apply(host);

                var offenders = ListLightArtifacts(host, darkExpected: false);
                Assert.True(offenders.Count == 0,
                    "切回浅色后仍残留深底色控件: " + string.Join(" | ", offenders.Take(8)));
            });
        }

        [Fact]
        public void 反复切换主题是可重入的_结果与一次切换一致()
        {
            RunSta(() =>
            {
                SettingsThemeManager.OverrideSystemUsesLightThemeForTest(true);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);

                using var group = new LiteSettingsGroup("测试分组");
                using var input = new LiteUnderlineInput("abc", "", "", 120);
                using var combo = new LiteComboBox();
                using var host = new Panel();
                host.Controls.Add(group);
                host.Controls.Add(input);
                host.Controls.Add(combo);

                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Dark);
                SettingsThemeApplier.Apply(host);
                List<string> once = new List<string>();
                Walk(host, c => once.Add(c.GetType().Name + ":" + c.BackColor.ToArgb() + "/" + c.ForeColor.ToArgb()));

                for (int i = 0; i < 5; i++)
                {
                    SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);
                    SettingsThemeApplier.Apply(host);
                    SettingsThemeManager.PushToUIColors(SettingsThemePalette.Dark);
                    SettingsThemeApplier.Apply(host);
                }

                List<string> many = new List<string>();
                Walk(host, c => many.Add(c.GetType().Name + ":" + c.BackColor.ToArgb() + "/" + c.ForeColor.ToArgb()));

                Assert.Equal(once, many);
            });
        }

        [Fact]
        public void 浅色与深色下控件树的着色结果不同()
        {
            RunSta(() =>
            {
                SettingsThemeManager.OverrideSystemUsesLightThemeForTest(true);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);

                using var input = new LiteUnderlineInput("abc", "GB", "", 120);
                using var combo = new LiteComboBox();
                using var host = new Panel();
                host.Controls.Add(input);
                host.Controls.Add(combo);

                SettingsThemeManager.SetCommittedForTest(SettingThemeMode.Light);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Light);
                SettingsThemeApplier.Apply(host);
                var light = SnapshotColors(host);

                SettingsThemeManager.SetCommittedForTest(SettingThemeMode.Dark);
                SettingsThemeManager.PushToUIColors(SettingsThemePalette.Dark);
                SettingsThemeApplier.Apply(host);
                var dark = SnapshotColors(host);

                Assert.NotEqual(light.Signature, dark.Signature);
                Assert.True(light.LuminanceSum > dark.LuminanceSum);
            });
        }

        // ================================================================
        // 夹具
        // ================================================================

        /// <summary>控件是否有"浅底"残留（深色模式下）或"深底"残留（浅色模式下）。</summary>
        private static bool HasLightArtifact(Control root, bool darkExpected)
            => ListLightArtifacts(root, darkExpected).Count > 0;

        private static List<string> ListLightArtifacts(Control root, bool darkExpected)
        {
            var offenders = new List<string>();

            Walk(root, c =>
            {
                double lum = SettingsThemePalette.Luminance(c.BackColor);
                // 透明容器透出父背景，不算它自己的问题
                if (c.BackColor.A == 0) return;

                if (darkExpected && lum >= 0.55) offenders.Add(c.GetType().Name + " BackColor 太亮 " + lum.ToString("F3"));
                if (!darkExpected && lum <= 0.12) offenders.Add(c.GetType().Name + " BackColor 太暗 " + lum.ToString("F3"));
            });

            return offenders;
        }

        private static void Walk(Control root, Action<Control> visit)
        {
            visit(root);
            foreach (Control child in root.Controls)
            {
                Walk(child, visit);
            }
        }

        private static (string Signature, double LuminanceSum) SnapshotColors(Control root)
        {
            var parts = new List<string>();
            double sum = 0;

            Walk(root, c =>
            {
                parts.Add(c.GetType().Name + ":" + c.BackColor.ToArgb() + "/" + c.ForeColor.ToArgb());
                sum += SettingsThemePalette.Luminance(c.BackColor) + SettingsThemePalette.Luminance(c.ForeColor);
            });

            return (string.Join("|", parts), sum);
        }
    }
}
