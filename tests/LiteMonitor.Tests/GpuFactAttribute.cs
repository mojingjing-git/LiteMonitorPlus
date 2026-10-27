using System;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// R10：真实 GPU 出帧门禁。默认跳过，只有显式设置
    /// <c>LITEMONITOR_GPU_TESTS=1</c> 且机器具备可用图形设备时才运行。
    /// 未运行时 xUnit 记为 Skipped，不能把“没跑”写成 Passed。
    /// </summary>
    public sealed class GpuFactAttribute : FactAttribute
    {
        public const string GateVariable = "LITEMONITOR_GPU_TESTS";

        public GpuFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(GateVariable) != "1")
            {
                Skip = $"需要 {GateVariable}=1 才运行（真实 GPU 出帧门禁）";
            }
        }
    }
}
