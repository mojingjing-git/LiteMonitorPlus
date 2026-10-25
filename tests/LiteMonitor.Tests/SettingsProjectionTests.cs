using System.Collections.Generic;
using LiteMonitor.Contracts;
using LiteMonitor.Core;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins; // MarketPluginRecord(插件投影用例)
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P-B3 阶段一：设置投影派生规则（纯函数、无单例、无 IO）。
    /// 期望值按 settings.json 字段语义固定，不随实现细节漂移。
    /// </summary>
    public class SettingsProjectionTests
    {
        [Fact]
        public void 采集投影_默认设置_刷新率与探测原始值()
        {
            var cfg = new Settings();

            var sampling = SettingsProjection.ToSamplingConfig(cfg, revision: 7);

            Assert.Equal(7, sampling.Revision);
            Assert.Equal(1000, sampling.RefreshMs);
            Assert.Equal(-1, sampling.ControllerProbe.ModeRaw);   // null ⇒ 未配置
            Assert.Equal(-1, sampling.DiskProbe.ModeRaw);
            Assert.Empty(sampling.Devices);                        // v1：F07 绑定随阶段二接入
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(0, 0)]
        public void 采集投影_F08b探测策略原始值透传(int? controllerRaw, int expectedController)
        {
            var cfg = new Settings { ControllerProbeMode = controllerRaw, DiskProbeMode = 3 };

            var sampling = SettingsProjection.ToSamplingConfig(cfg);

            Assert.Equal(expectedController, sampling.ControllerProbe.ModeRaw);
            Assert.Equal(3, sampling.DiskProbe.ModeRaw);
        }

        [Fact]
        public void 显示投影_默认与自定义()
        {
            var defaults = SettingsProjection.ToDisplayConfig(new Settings());
            Assert.Equal("", defaults.Language);
            Assert.Equal("DarkFlat_Classic", defaults.Skin);
            Assert.Equal(SettingThemeMode.System, defaults.ThemeMode);  // f14b 字段透传（默认 System）
            Assert.Equal(1000, defaults.UiRefreshMs);

            var customized = SettingsProjection.ToDisplayConfig(new Settings
            {
                Language = "zh-CN",
                Skin = "Custom",
                SettingThemeMode = SettingThemeMode.Dark,               // f14b 字段透传（Dark）
                RefreshMs = 500,
            });
            Assert.Equal("zh-CN", customized.Language);
            Assert.Equal("Dark", customized.ThemeMode.ToString());
            Assert.Equal(500, customized.UiRefreshMs);
        }

        [Fact]
        public void Web投影_默认关闭_不传播口令明文()
        {
            var defaults = SettingsProjection.ToWebConfig(new Settings());
            Assert.False(defaults.Enabled);
            Assert.Equal(5000, defaults.Port);
            Assert.False(defaults.TrustProxyHeaders);
            Assert.False(defaults.HasPassword);

            var enabled = SettingsProjection.ToWebConfig(new Settings
            {
                WebServerEnabled = true,
                WebServerPort = 8080,
                WebServerTrustProxyHeaders = true,
                WebServerPassword = "secret",
            });
            Assert.True(enabled.Enabled);
            Assert.Equal(8080, enabled.Port);
            Assert.True(enabled.HasPassword);
        }

        [Fact]
        public void 插件投影_规模计数()
        {
            var cfg = new Settings();
            cfg.PluginInstances.Add(new PluginInstanceConfig());
            cfg.PluginInstances.Add(new PluginInstanceConfig());
            cfg.MarketPlugins.Add(new MarketPluginRecord());

            var plugin = SettingsProjection.ToPluginConfig(cfg);

            Assert.Equal(2, plugin.PluginInstanceCount);
            Assert.Equal(1, plugin.InstalledMarketPluginCount);
        }

        [Fact]
        public void 空引用防护()
        {
            Assert.Throws<System.ArgumentNullException>(() => SettingsProjection.ToSamplingConfig(null!));
            Assert.Throws<System.ArgumentNullException>(() => SettingsProjection.ToDisplayConfig(null!));
            Assert.Throws<System.ArgumentNullException>(() => SettingsProjection.ToWebConfig(null!));
            Assert.Throws<System.ArgumentNullException>(() => SettingsProjection.ToPluginConfig(null!));
        }
    }
}
