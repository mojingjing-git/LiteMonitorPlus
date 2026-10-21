using System;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// 仅在设置 <c>LITEMONITOR_E2E=1</c> 时才运行的 <see cref="FactAttribute"/>。
    ///
    /// 未启用时由 xunit 记为 <c>Skipped</c>，而不是让用例体 <c>return</c> 之后仍显示 Passed。
    /// 「没跑却报通过」会让 CI 与本地都误以为真实链路已经验证过，这比明确跳过更有害。
    ///
    /// 实现依据：xunit 2.9.2 的 <c>FactAttribute.Skip</c> 是
    /// <c>public virtual string? { get; set; }</c>，在特性构造时赋值即可按环境动态跳过。
    /// xunit v3 的 <c>Assert.Skip</c> 在 2.x 中不存在，不能直接使用。
    /// </summary>
    public sealed class E2EFactAttribute : FactAttribute
    {
        /// <summary>开关环境变量名。</summary>
        public const string GateVariable = "LITEMONITOR_E2E";

        public E2EFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(GateVariable) != "1")
            {
                Skip = $"需要 {GateVariable}=1 才运行（联网 E2E，会真实访问 GitHub）";
            }
        }
    }
}
