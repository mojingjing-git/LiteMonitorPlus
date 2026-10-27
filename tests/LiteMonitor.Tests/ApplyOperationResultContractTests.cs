using LiteMonitor.Core.Actions.Services;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class ApplyOperationResultContractTests
{
    [Fact]
    public void C1_全部模块成功_外层为Applied并汇总修订()
    {
        var result = ApplyOperationResult.FromModules(
            "op-1",
            requestRevision: 7,
            new[]
            {
                ModuleApplyResult.Ok("Appearance", moduleRevision: 7),
                ModuleApplyResult.Ok("Taskbar", moduleRevision: 7),
            });

        Assert.Equal(ApplyOperationStatus.Applied, result.OverallStatus);
        Assert.Equal(7, result.EffectiveRevision);
        Assert.Equal(2, result.Modules.Count);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public void C1_部分模块成功_外层为Partial且保留失败细节()
    {
        var failed = ModuleApplyResult.Fail("Sampling", "saved", null, "sampling-rejected", retryable: true, moduleRevision: 0);
        var result = ApplyOperationResult.FromModules(
            "op-2",
            requestRevision: 8,
            new[] { ModuleApplyResult.Ok("Appearance", moduleRevision: 8), failed },
            failureCode: "partial-apply");

        Assert.Equal(ApplyOperationStatus.Partial, result.OverallStatus);
        Assert.Equal(8, result.EffectiveRevision);
        Assert.Equal("partial-apply", result.FailureCode);
        Assert.True(result.Modules[1].Retryable);
        Assert.Equal("sampling-rejected", result.Modules[1].FailureReason);
    }

    [Fact]
    public void C1_全部模块失败_外层为Failed()
    {
        var result = ApplyOperationResult.FromModules(
            "op-3",
            requestRevision: 9,
            new[] { ModuleApplyResult.Fail("Appearance", "saved", null, "theme-load-failed") });

        Assert.Equal(ApplyOperationStatus.Failed, result.OverallStatus);
        Assert.Null(result.EffectiveRevision);
    }

    [Fact]
    public void C1_空模块不伪造成功_外层为Unknown()
    {
        var result = ApplyOperationResult.FromModules("op-4", requestRevision: 10, Array.Empty<ModuleApplyResult>());

        Assert.Equal(ApplyOperationStatus.Unknown, result.OverallStatus);
        Assert.Empty(result.Modules);
    }

    [Fact]
    public void C1_OperationId不能为空()
    {
        Assert.Throws<ArgumentException>(() =>
            ApplyOperationResult.FromModules("", 1, Array.Empty<ModuleApplyResult>()));
    }
}
