using System.Collections.Generic;
using System.Linq;
using LiteMonitor.Contracts;
using LiteMonitor.Core;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// P-B3 阶段一：模块服务与差异分派。核心验收断言：
    /// 颜色/语言（外观类）变更不触发硬件重配——ApplyConfig 不被调用（Plan B §6.1"设置"行）。
    /// </summary>
    public class SettingsModuleServicesTests
    {
        private sealed class RecordingHardwareClient : IHardwareClient
        {
            public List<SamplingConfig> AppliedConfigs { get; } = new();
            public HardwareSnapshot GetLatestSnapshot() => throw new System.NotImplementedException();
            public System.Threading.Tasks.Task<HardwareCatalog> GetCatalogAsync(System.Threading.CancellationToken ct) => throw new System.NotImplementedException();
            public SamplingApplyResult ApplyConfig(SamplingConfig config)
            {
                AppliedConfigs.Add(config);
                return new SamplingApplyResult(true, config.Revision, null);
            }
            public void SubmitDemand(SamplingDemand demand) { }
            public HardwareHealth GetHealth() => new(HardwareState.Ready, null, 0, null);
            public string? GetNetworkIP() => null;
            public System.Threading.Tasks.Task<bool> RequestRescanAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.FromResult(true);
            public void Dispose() { }
        }

        [Fact]
        public void 外观变更_不触发硬件重配()
        {
            var client = new RecordingHardwareClient();
            var dispatcher = new SettingsApplyDispatcher(sampling: new SamplingConfigApplier(client));
            var live = new Settings();
            var draft = new Settings { Skin = "NewSkin", Language = "en", SettingThemeMode = SettingThemeMode.Dark };

            var diffs = SettingsChanger.ComputeDiff(live, draft);
            var results = dispatcher.Dispatch(diffs, draft);

            Assert.Empty(client.AppliedConfigs);                                   // ★ ApplyConfig 未被调用
            Assert.Contains(diffs, d => d.Module == SettingsModule.Appearance);
            Assert.DoesNotContain(diffs, d => d.Module == SettingsModule.Sampling);
            Assert.Contains(results, r => r.Module == nameof(SettingsModule.Appearance)); // 未接线壳记 not-wired，不阻塞
        }

        [Fact]
        public void 采样字段变更_投影应用一次()
        {
            var client = new RecordingHardwareClient();
            var dispatcher = new SettingsApplyDispatcher(sampling: new SamplingConfigApplier(client));
            var live = new Settings();
            var draft = new Settings { RefreshMs = 500, ControllerProbeMode = 1 };

            var diffs = SettingsChanger.ComputeDiff(live, draft);
            var results = dispatcher.Dispatch(diffs, draft, revision: 42);

            var applied = Assert.Single(client.AppliedConfigs);
            Assert.Equal(42, applied.Revision);
            Assert.Equal(500, applied.RefreshMs);
            Assert.Equal(1, applied.ControllerProbe.ModeRaw);
            Assert.Contains(results, r => r.Module == nameof(SettingsModule.Sampling) && r.Applied && r.EffectiveState == "revision=42");
        }

        [Theory]
        [InlineData("Skin", SettingsModule.Appearance)]
        [InlineData("Language", SettingsModule.Appearance)]
        [InlineData("SettingThemeMode", SettingsModule.Appearance)]
        [InlineData("RefreshMs", SettingsModule.Sampling)]
        [InlineData("ControllerProbeMode", SettingsModule.Sampling)]
        [InlineData("DiskProbeMode", SettingsModule.Sampling)]
        [InlineData("MonitorItems", SettingsModule.Sampling)]
        [InlineData("WebServerPort", SettingsModule.WebServer)]
        [InlineData("WebServerEnabled", SettingsModule.WebServer)]
        [InlineData("PluginInstances", SettingsModule.Plugins)]
        [InlineData("TaskbarWidth", SettingsModule.Taskbar)]   // 前缀归类
        [InlineData("SomethingElse", SettingsModule.Other)]
        public void 字段归类(string field, SettingsModule expected)
        {
            Assert.Equal(expected, SettingsChanger.Classify(field));
        }

        [Fact]
        public void 差异计算_运行时黑名单与等值字段不产生差异()
        {
            var live = new Settings();
            var draft = new Settings();

            // 等值：无差异
            Assert.Empty(SettingsChanger.ComputeDiff(live, draft));

            // 黑名单字段（运行时数据）改动不算差异
            draft.SessionUploadBytes = 999999;
            Assert.Empty(SettingsChanger.ComputeDiff(live, draft));

            // 复杂类型按序列化比较：同内容列表不算差异
            var a = new Settings(); var b = new Settings();
            b.MonitorItems.Add(new MonitorItemConfig());
            a.MonitorItems.Add(new MonitorItemConfig());
            Assert.DoesNotContain(SettingsChanger.ComputeDiff(a, b), d => d.Field == "MonitorItems");
        }

        [Fact]
        public void 委托壳异常_返回可解释失败_不抛出()
        {
            var appearance = new WindowAppearanceApplier(() => throw new System.InvalidOperationException("theme boom"));
            var web = new WebServerController(() => { });

            var r1 = appearance.Apply();
            Assert.False(r1.Applied);
            Assert.Equal("theme boom", r1.FailureReason);

            Assert.True(web.Apply().Applied);
        }

        [Fact]
        public void 未接线模块_记录not_wired不阻塞其他模块()
        {
            var client = new RecordingHardwareClient();
            var dispatcher = new SettingsApplyDispatcher(sampling: new SamplingConfigApplier(client));
            var draft = new Settings { Skin = "X", RefreshMs = 250 };

            var results = dispatcher.Dispatch(SettingsChanger.ComputeDiff(new Settings(), draft), draft);

            Assert.Single(client.AppliedConfigs);                                        // 采样照常应用
            var notWired = Assert.Single(results, r => r.Module == nameof(SettingsModule.Appearance));
            Assert.Equal("not-wired(阶段三接线)", notWired.EffectiveState);
        }
    }
}
