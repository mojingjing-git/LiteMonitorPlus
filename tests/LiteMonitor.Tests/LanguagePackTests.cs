using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 语言包一致性测试。
    ///
    /// 背景：插件市场页面的 38 个 PluginMarket* 文案必须 9 个语言包全都有 ——
    /// 缺一个就会在界面上回退成 key 名（LanguageManager.GetOriginal 取第一个 "." 之后的后缀），
    /// 等于没国际化。这条测试把"漏加语言"变成编译期就能发现的失败。
    ///
    /// 全部是纯文件读取，不触碰任何全局状态。
    /// </summary>
    public class LanguagePackTests
    {
        private static readonly string[] AllLangs = { "zh", "zh-tw", "en", "ja", "ko", "ru", "de", "es", "fr" };

        private static string LangPath(string lang)
            => Path.Combine(AppContext.BaseDirectory, "resources", "lang", lang + ".json");

        private static Dictionary<string, string> LoadMenu(string lang)
        {
            string path = LangPath(lang);
            Assert.True(File.Exists(path), "语言包缺失: " + path);

            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("Menu", out var menu), lang + " 缺少 Menu 节点");

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in menu.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString() ?? "";
            }
            return map;
        }

        [Theory]
        [InlineData("zh")]
        [InlineData("zh-tw")]
        [InlineData("en")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("ru")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        public void 语言包存在且可解析(string lang)
        {
            var menu = LoadMenu(lang);
            Assert.True(menu.Count > 0, lang + " 的 Menu 节点为空");
        }

        [Fact]
        public void 九个语言包的MenuKey集合完全一致()
        {
            var reference = LoadMenu("zh").Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

            foreach (var lang in AllLangs)
            {
                var keys = LoadMenu(lang).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                var missing = reference.Except(keys, StringComparer.OrdinalIgnoreCase).ToList();
                var extra = keys.Except(reference, StringComparer.OrdinalIgnoreCase).ToList();

                Assert.True(missing.Count == 0,
                    lang + " 缺少 key: " + string.Join(", ", missing.Take(10)));
                Assert.True(extra.Count == 0,
                    lang + " 多出 key: " + string.Join(", ", extra.Take(10)));
            }
        }

        [Fact]
        public void 九个语言包都有全部插件市场文案()
        {
            string[] required = {
                "PluginMarket", "PluginMarketSource", "PluginMarketRefresh", "PluginMarketLoading",
                "PluginMarketLoaded", "PluginMarketEmpty", "PluginMarketFailed", "PluginMarketInstall",
                "PluginMarketUninstall", "PluginMarketUpdate", "PluginMarketInstalling", "PluginMarketInstallOk",
                "PluginMarketUninstallOk", "PluginMarketUpdateOk", "PluginMarketGoEnable", "PluginMarketRequires",
                "PluginMarketRenamed", "PluginMarketSourceLink", "PluginMarketInstalled", "PluginMarketUpdatable",
                "PluginMarketHint", "PluginMarketUnverified", "PluginMarketUnverifiedHint",
                "PluginMarketBuiltinProtected", "PluginMarketHashMismatch", "PluginMarketIdMismatch",
                "PluginMarketNotManaged", "PluginMarketTargetExists", "PluginMarketBadSource",
                "PluginMarketSchemaMismatch", "PluginMarketMissingHash", "PluginMarketNeedAdmin",
                "PluginMarketStatusOk",
                "PluginMarketStatusService", "PluginMarketStatusCode", "PluginMarketReinstall",
                "PluginMarketReinstallAction", "PluginMarketPageLink", "PluginMarketSelfCheck"
            };

            foreach (var lang in AllLangs)
            {
                var menu = LoadMenu(lang);
                foreach (var key in required)
                {
                    Assert.True(menu.ContainsKey(key), lang + " 缺少 " + key);
                    Assert.False(string.IsNullOrEmpty(menu[key]), lang + " 的 " + key + " 是空字符串");
                }
            }
        }

        [Fact]
        public void 插件市场文案_英文包不残留中文()
        {
            var en = LoadMenu("en");
            foreach (var kv in en)
            {
                if (!kv.Key.StartsWith("PluginMarket", StringComparison.OrdinalIgnoreCase)) continue;

                bool hasCjk = kv.Value.Any(c => c >= '\u4e00' && c <= '\u9fff');
                Assert.False(hasCjk, "英文包的 " + kv.Key + " 残留中文: " + kv.Value);
            }
        }

        [Fact]
        public void 插件市场文案_简体中文包不残留英文单词()
        {
            // 中文包允许保留 API Key / JSON / Plugin Market 这类专有名词，
            // 这里只做粗筛：整条文案全是 ASCII 字母时才算漏翻译
            var zh = LoadMenu("zh");
            foreach (var kv in zh)
            {
                if (!kv.Key.StartsWith("PluginMarket", StringComparison.OrdinalIgnoreCase)) continue;

                bool allAsciiLetters = kv.Value.Length > 0 && kv.Value.All(c => char.IsLetter(c) && c < 128);
                Assert.False(allAsciiLetters, "中文包的 " + kv.Key + " 疑似漏翻译: " + kv.Value);
            }
        }

        /// <summary>
        /// ★ 回归守卫：失败原因 → 专属文案的映射。
        ///
        /// 背景：<c>PluginMarketHashMismatch</c> / <c>PluginMarketIdMismatch</c> /
        /// <c>PluginMarketBadSource</c> / <c>PluginMarketSchemaMismatch</c> 四个 key
        /// 在 9 个语言包里**一直存在**，但界面侧的 <c>DescribeReason</c> switch
        /// **缺少对应分支**，于是这些失败原因全部落回通用的"插件市场加载失败"——
        /// 用户看到的提示与真实原因不符。LM-008 引入 <c>MissingHash</c> 后同样如此。
        ///
        /// 本用例把"switch 少一个分支"变成可发现的失败：删掉任意一个 case，
        /// 对应断言立即失败（落回通用文案 ⇒ 与期望文案不等）。
        /// </summary>
        [Fact]
        public void 插件市场失败原因都有专属文案_不落回通用失败文案()
        {
            // ★ 显式加载语言包：断言不能依赖"别的测试刚好加载过"
            //   （与 CpuVoltagePresentationTests / GpuGroupTests 同一约定；
            //     本测试工程已关闭并行，全局语言态不会被并发改写）
            LanguageManager.Load("zh");

            var method = typeof(LiteMonitor.src.UI.SettingsPage.PluginMarketPage)
                .GetMethod("DescribeReason", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var expectations = new Dictionary<MarketErrorReason, string>
            {
                [MarketErrorReason.HashMismatch] = "Menu.PluginMarketHashMismatch",
                [MarketErrorReason.IdMismatch] = "Menu.PluginMarketIdMismatch",
                [MarketErrorReason.BadSource] = "Menu.PluginMarketBadSource",
                [MarketErrorReason.SchemaMismatch] = "Menu.PluginMarketSchemaMismatch",
                [MarketErrorReason.MissingHash] = "Menu.PluginMarketMissingHash",
            };

            string generic = LanguageManager.T("Menu.PluginMarketFailed");

            foreach (var pair in expectations)
            {
                string actual = (string)method.Invoke(null, new object[] { pair.Key });
                string expected = LanguageManager.T(pair.Value);

                Assert.False(string.IsNullOrEmpty(expected), pair.Value + " 没有对应文案");
                Assert.Equal(expected, actual);
                Assert.NotEqual(generic, actual);
            }
        }
    }
}
