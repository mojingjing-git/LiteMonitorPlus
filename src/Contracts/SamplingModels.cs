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

/// <summary>配置应用结果：生效 revision 或可解释的失败原因；无隐式部分成功。</summary>
public sealed record SamplingApplyResult(bool Applied, int? Revision, string? FailureReason);
