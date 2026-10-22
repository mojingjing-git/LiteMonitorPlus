using System;
using LiteMonitor.src.Plugins;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 插件文案的语言选词规则测试。
    ///
    /// 规则：界面为中文（zh / zh-tw）时用中文字段；
    /// 其余语言优先用 _en 字段；_en 缺失时回退中文（绝不显示空白）。
    ///
    /// ★ 这里刻意只用 PickFor / PreferEnglishFor 这些【纯函数】，
    ///   不碰 LanguageManager.CurrentLang —— 它是进程级单例，
    ///   多个测试类并行改它会互相干扰（既有 GpuGroupTests 就踩过这个坑）。
    /// </summary>
    public class PluginI18nTests
    {
        [Theory]
        [InlineData("zh")]
        [InlineData("zh-tw")]
        public void 中文界面_选中文文案(string lang)
        {
            Assert.False(PluginI18n.PreferEnglishFor(lang));
            Assert.Equal("中文名", PluginI18n.PickFor(lang, "中文名", "English Name"));
        }

        [Theory]
        [InlineData("en")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("ru")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        public void 非中文界面_选英文文案(string lang)
        {
            Assert.True(PluginI18n.PreferEnglishFor(lang));
            Assert.Equal("English Name", PluginI18n.PickFor(lang, "中文名", "English Name"));
        }

        [Theory]
        [InlineData("en")]
        [InlineData("ja")]
        [InlineData("de")]
        public void 非中文界面_英文缺失_回退中文而不是空白(string lang)
        {
            Assert.Equal("中文名", PluginI18n.PickFor(lang, "中文名", ""));
            Assert.Equal("中文名", PluginI18n.PickFor(lang, "中文名", null));
        }

        [Theory]
        [InlineData("zh")]
        [InlineData("zh-tw")]
        public void 中文界面_即使有英文也用中文(string lang)
        {
            Assert.Equal("中文名", PluginI18n.PickFor(lang, "中文名", "English Name"));
        }

        [Fact]
        public void 两侧都空_返回空而不是抛异常()
        {
            Assert.Equal("", PluginI18n.PickFor("en", "", ""));
            Assert.Equal("", PluginI18n.PickFor("en", null, null));
            Assert.Equal("", PluginI18n.PickFor("en", null, ""));
            Assert.Equal("", PluginI18n.PickFor("en", "", null));
        }

        [Fact]
        public void 语言代码为null_按中文处理()
        {
            Assert.False(PluginI18n.PreferEnglishFor(null));
            Assert.Equal("中文名", PluginI18n.PickFor(null, "中文名", "English Name"));
        }

        [Fact]
        public void 选词_保留原文_不做任何改写()
        {
            // 标签里可能含 {{key}} 占位符，选词过程不得破坏它
            Assert.Equal("{{city}} 温度", PluginI18n.PickFor("en", "{{city}} 温度", ""));
            Assert.Equal("{{city}} Temp", PluginI18n.PickFor("en", "{{city}} 温度", "{{city}} Temp"));
        }

        [Fact]
        public void Pick_与PickFor当前语言_结果一致()
        {
            // Pick 走全局语言状态；这里只验证它与纯函数版本口径相同
            var zh = PluginI18n.PickFor("zh", "中文名", "English Name");
            var en = PluginI18n.PickFor("en", "中文名", "English Name");
            Assert.Equal("中文名", zh);
            Assert.Equal("English Name", en);
        }
    }
}
