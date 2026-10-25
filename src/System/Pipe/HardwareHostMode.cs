using System;
using System.Linq;

namespace LiteMonitor.SystemServices.Pipe;

/// <summary>
/// P-I2 §2.4 模式开关：仅命令行 <c>--hardware-host</c> 或环境变量
/// <c>LITEMONITOR_HARDWARE_HOST=1</c> 两种入口（settings.json 零变化、不新增字段；
/// 设置项开关属 I5）。首次读取后固定（命令行/环境变量在进程生命周期内不变）。
/// </summary>
public static class HardwareHostMode
{
    public const string CommandLineFlag = "--hardware-host";
    public const string EnvironmentVariable = "LITEMONITOR_HARDWARE_HOST";

    private static readonly Lazy<bool> RequestedLazy = new(Compute);

    public static bool IsRequested() => RequestedLazy.Value;

    private static bool Compute()
    {
        if (Environment.GetCommandLineArgs().Any(a =>
                string.Equals(a, CommandLineFlag, StringComparison.OrdinalIgnoreCase)))
            return true;

        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
