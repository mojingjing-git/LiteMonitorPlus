using System;
using System.Collections.Generic;

namespace LiteMonitor.Contracts;

/// <summary>控制器探测策略的纯数据投影（F08b 语义）。ModeRaw 采用 ControllerProbePolicy 的原始整数值，-1 = 未配置。</summary>
public sealed record ControllerProbePolicyData(int ModeRaw, IReadOnlyList<string> ExcludedIds);

/// <summary>磁盘探测策略的纯数据投影（F08a 语义）。ModeRaw 采用 DiskProbePolicy 的原始整数值，-1 = 未配置。</summary>
public sealed record DiskProbePolicyData(int ModeRaw);

/// <summary>设备选择项。Kind 为设备类别标识；StableId 承载 F07 稳定设备身份。</summary>
public sealed record DeviceSelection(string Kind, string? StableId, bool Enabled);

/// <summary>采集配置投影：只含采样相关字段，不含皮肤、窗体、菜单、API Key 或 Web 口令（Plan A §4）。</summary>
public sealed record SamplingConfig(
    int Revision,
    int RefreshMs,
    ControllerProbePolicyData ControllerProbe,
    DiskProbePolicyData DiskProbe,
    IReadOnlyList<DeviceSelection> Devices);

/// <summary>消费方需求。传感器窗口打开期间显式登记，关闭时释放；不得用界面遍历触发库更新。</summary>
public sealed record SamplingDemand(bool Ui, bool Taskbar, bool Web, bool History, bool SensorWindow);

/// <summary>采样通道位。与 <see cref="SamplingDemand"/> 的五个布尔字段一一对应，供 C3 消费者合同保留通道语义。</summary>
[Flags]
public enum SamplingChannels
{
    None = 0,
    Ui = 1,
    Taskbar = 2,
    Web = 4,
    History = 8,
    SensorWindow = 16,
}

/// <summary>
/// C3 消费者采样需求：以 consumerId 为主键，显式携带最大允许间隔与可降级性。
/// <para><c>MaxIntervalMs = 0</c> 表示沿用当前周期（未声明）；聚合时 0 被忽略，正值最小 1000ms。</para>
/// <para><see cref="Channels"/> 保留旧 <see cref="SamplingDemand"/> 的通道位，兼容层不再用五个布尔字段承载频率合同。</para>
/// </summary>
public sealed record ConsumerSamplingDemand(
    string ConsumerId,
    bool Active,
    int MaxIntervalMs,
    bool Degradable,
    string Reason,
    SamplingChannels Channels = SamplingChannels.None)
{
    public SamplingDemand ToLegacyDemand()
        => new(
            (Channels & SamplingChannels.Ui) != 0,
            (Channels & SamplingChannels.Taskbar) != 0,
            (Channels & SamplingChannels.Web) != 0,
            (Channels & SamplingChannels.History) != 0,
            (Channels & SamplingChannels.SensorWindow) != 0);

    public static SamplingChannels FromLegacy(SamplingDemand demand)
    {
        if (demand is null) return SamplingChannels.None;
        SamplingChannels channels = SamplingChannels.None;
        if (demand.Ui) channels |= SamplingChannels.Ui;
        if (demand.Taskbar) channels |= SamplingChannels.Taskbar;
        if (demand.Web) channels |= SamplingChannels.Web;
        if (demand.History) channels |= SamplingChannels.History;
        if (demand.SensorWindow) channels |= SamplingChannels.SensorWindow;
        return channels;
    }
}

/// <summary>配置应用结果：生效 revision 或可解释的失败原因；无隐式部分成功。</summary>
public sealed record SamplingApplyResult(bool Applied, int? Revision, string? FailureReason);
