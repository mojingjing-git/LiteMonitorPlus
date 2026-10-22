using System.Reflection;
using LiteMonitor.src.UI.Helpers;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class UpdaterRecoveryTests
{
    [Fact]
    public void ParsesRecoveryResultAfterProductionLogTimestamp()
    {
        DateTime timestamp = new(2026, 10, 21, 12, 34, 56);
        string[] lines =
        {
            timestamp + " [Failure] injected commit failure",
            timestamp + " [Result] status=recovery_incomplete " +
                "backup=\"C:\\Program Files\\LiteMonitor\\_update_backup_20261021_123456\" " +
                "restored=1 removed=0 failed=1",
            timestamp + " [Summary] recovery incomplete"
        };

        Assert.Equal(
            "recovery_incomplete",
            InvokeStringParser("FindTaggedValue", lines, "[Result]", "status"));
        Assert.Equal(
            "C:\\Program Files\\LiteMonitor\\_update_backup_20261021_123456",
            InvokeStringParser("FindTaggedValue", lines, "[Result]", "backup"));
        Assert.Equal(
            "injected commit failure",
            InvokeStringParser("FindTaggedLine", lines, "[Failure]"));
    }

    private static string? InvokeStringParser(
        string methodName,
        params object[] arguments)
    {
        MethodInfo method = typeof(MainFormBizHelper).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(MainFormBizHelper).FullName, methodName);

        return (string?)method.Invoke(null, arguments);
    }
}
