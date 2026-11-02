using System.Collections.Generic;
using System.Text.Json;
using LiteMonitor.Rendering.Configuration;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 渲染路线**全部原因码**的防漂移回归（R3 §4.5-5.3）。
    /// <para>★ 每个 <c>FallbackCode</c> 都必须有一条断言——原因码被改掉/删掉时，这里先红。
    /// 2026-10-24 步 0（方案 A）后，<c>wda_capture_policy_unsupported_on_layered_window</c>
    /// **不再产出**（分层降为诊断条目），本类同时守住"不得复活"。</para>
    /// </summary>
    public class RenderingRouteReasonCodeTests
    {
        private static ThemeRenderingConfig GpuConfig(
            string material = ThemeRenderingConfig.LiquidGlassMaterial,
            bool liveBackground = true) => new()
        {
            Backend = ThemeRenderingConfig.GpuBackend,
            Material = material,
            Parameters = new Dictionary<string, JsonElement>
            {
                ["liveBackground"] = JsonSerializer.SerializeToElement(liveBackground),
                ["cornerRadius"] = JsonSerializer.SerializeToElement(24),
                ["primaryCut"] = JsonSerializer.SerializeToElement(14)
            }
        };

        [Fact]
        public void AbsentConfigurationFallsBackToLegacyWithCode()
        {
            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: null,
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false));

            Assert.Equal(RenderingRoute.Legacy, decision.Route);
            Assert.Equal("rendering_configuration_absent", decision.FallbackCode);
        }

        [Fact]
        public void InvalidConfigurationFallsBackToLegacyWithCode()
        {
            // material 不在支持清单 ⇒ Validate 至少产出一条错误 ⇒ 整份主题不可用。
            var invalid = GpuConfig(material: "definitely_not_a_material");

            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: invalid,
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false));

            Assert.Equal(RenderingRoute.Legacy, decision.Route);
            Assert.Equal("rendering_configuration_invalid", decision.FallbackCode);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SafeModeBeatsEverythingAndReportsCode(bool wdaAccepted)
        {
            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: GpuConfig(),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: wdaAccepted,
                SafeMode: true));

            Assert.Equal(RenderingRoute.Legacy, decision.Route);
            Assert.Equal("safe_mode", decision.FallbackCode);
        }

        [Fact]
        public void GpuEffectsNotOptedInReportsCode()
        {
            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: GpuConfig(),
                GpuEffectsEnabled: false,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false));

            Assert.Equal(RenderingRoute.Legacy, decision.Route);
            Assert.Equal("gpu_effects_opt_in_required", decision.FallbackCode);
        }

        [Fact]
        public void WdaPolicyNotAcceptedFallsToFixedInputWithCode()
        {
            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: GpuConfig(),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: false,
                SafeMode: false));

            Assert.Equal(RenderingRoute.GpuFixedInput, decision.Route);
            Assert.False(decision.CaptureRequested);
            Assert.Equal("wda_capture_policy_not_accepted", decision.FallbackCode);
        }

        [Fact]
        public void LayeredWindowCodeIsRetiredAndNeverProduced()
        {
            // 方案 A（2026-10-24 步 0）：分层窗口**不得**再产生该原因码；
            // 分层事实走 Errors 的 diagnostic_window_is_layered（见 RenderingTransitionStateTests）。
            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: GpuConfig(),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false,
                WindowIsLayered: true));

            Assert.NotEqual("wda_capture_policy_unsupported_on_layered_window", decision.FallbackCode);
        }

        [Theory]
        [InlineData(
            ThemeRenderingConfig.LiquidGlassMaterial,
            ThemeRenderingConfig.NebulaAuroraVisualPreset)]
        [InlineData(
            ThemeRenderingConfig.CyberPunkMaterial,
            ThemeRenderingConfig.NixieBrassVisualPreset)]
        [InlineData(
            ThemeRenderingConfig.CyberPunkMaterial,
            ThemeRenderingConfig.CrtPhosphorVisualPreset)]
        public void NamedVisualPresetsNeverRequestLiveCapture(
            string material,
            string preset)
        {
            ThemeRenderingConfig configuration = GpuConfig(
                material,
                liveBackground: false);
            configuration.Parameters["visualPreset"] =
                JsonSerializer.SerializeToElement(preset);

            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: configuration,
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false));

            Assert.Equal(RenderingRoute.GpuFixedInput, decision.Route);
            Assert.False(decision.CaptureRequested);
            Assert.Null(decision.FallbackCode);
        }

        [Fact]
        public void FullyQualifiedRequestRoutesToGpuLiveBackgroundWithoutCode()
        {
            var decision = RenderingRoutePolicy.Decide(new(
                Configuration: GpuConfig(),
                GpuEffectsEnabled: true,
                LiveBackgroundEnabled: true,
                WdaCapturePolicyAccepted: true,
                SafeMode: false));

            Assert.Equal(RenderingRoute.GpuLiveBackground, decision.Route);
            Assert.True(decision.CaptureRequested);
            Assert.Null(decision.FallbackCode);
            Assert.Empty(decision.Errors);
        }
    }
}
