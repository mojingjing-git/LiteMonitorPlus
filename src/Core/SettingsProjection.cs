using System;
using System.Collections.Generic;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core; // Settings/SettingThemeMode（现状命名空间，B5 统一前的既有事实）

namespace LiteMonitor.Core
{
    /// <summary>
    /// 显示配置投影（Plan B §4.2）：语言/皮肤/主题/刷新率等界面相关字段的不可变快照。
    /// 派生为纯函数：无 IO、无单例读取、无副作用。
    /// </summary>
    public sealed record DisplayConfigProjection(
        string Language,
        string Skin,
        SettingThemeMode ThemeMode,
        int UiRefreshMs);

    /// <summary>
    /// Web 配置投影：只携带端口/开关/信任策略与"是否配置了口令"标志——
    /// 不传播口令明文/盐值（避免为满足接口把敏感材料带遍模块）。
    /// </summary>
    public sealed record WebConfigProjection(
        bool Enabled,
        int Port,
        bool TrustProxyHeaders,
        bool HasPassword);

    /// <summary>
    /// 插件配置投影（v1 携带规模信息；插件实例明细投影随 PluginRuntimeController 做实）。
    /// </summary>
    public sealed record PluginConfigProjection(
        int PluginInstanceCount,
        int InstalledMarketPluginCount);

    /// <summary>
    /// 设置投影入口（P-B3 阶段一）：从持久化 DTO <see cref="Settings"/> 派生各模块的不可变配置投影。
    /// 约束：纯函数；运行时计数/自动设备选择/错误缓存不得进入投影（§4.2）。
    /// </summary>
    public static class SettingsProjection
    {
        /// <summary>与 ControllerProbePolicy/DiskProbePolicy 的 UnconfiguredModeValue 同口径（-1 = 未配置）。</summary>
        public const int UnconfiguredProbeMode = -1;

        /// <summary>
        /// 采集配置投影：Revision 由调用方提供——设置文件本身无变更计数，
        /// B3 阶段三引入设置 revision 后由组合根传入；此前传 <see cref="UnconfiguredProbeMode"/> 占位。
        /// 探测策略只传原始整数值（Settings.DiskProbeMode/ControllerProbeMode 的 null → -1），
        /// 策略语义解释归采集层（ControllerProbePolicy.FromRawValue），投影不引用 HardwareServices 类型
        /// （避免 src/Core → SystemServices 的 using，architecture-assert A-2 硬拦）。
        /// 设备选择清单 v1 为空：F07 稳定身份绑定随阶段二 SystemHardwarPage 重构接入。
        /// </summary>
        public static SamplingConfig ToSamplingConfig(Settings settings, int revision = UnconfiguredProbeMode)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new SamplingConfig(
                Revision: revision,
                RefreshMs: settings.RefreshMs,
                ControllerProbe: new ControllerProbePolicyData(
                    ModeRaw: NormalizeProbeMode(settings.ControllerProbeMode),
                    ExcludedIds: Array.Empty<string>()),
                DiskProbe: new DiskProbePolicyData(NormalizeProbeMode(settings.DiskProbeMode)),
                Devices: Array.Empty<DeviceSelection>());
        }

        public static DisplayConfigProjection ToDisplayConfig(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new DisplayConfigProjection(
                Language: settings.Language,
                Skin: settings.Skin,
                ThemeMode: settings.SettingThemeMode,
                UiRefreshMs: settings.RefreshMs);
        }

        public static WebConfigProjection ToWebConfig(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new WebConfigProjection(
                Enabled: settings.WebServerEnabled,
                Port: settings.WebServerPort,
                TrustProxyHeaders: settings.WebServerTrustProxyHeaders,
                HasPassword: !string.IsNullOrEmpty(settings.WebServerPassword));
        }

        public static PluginConfigProjection ToPluginConfig(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            return new PluginConfigProjection(
                PluginInstanceCount: settings.PluginInstances?.Count ?? 0,
                InstalledMarketPluginCount: settings.MarketPlugins?.Count ?? 0);
        }

        private static int NormalizeProbeMode(int? raw)
            => raw.HasValue ? raw.Value : UnconfiguredProbeMode;
    }
}
