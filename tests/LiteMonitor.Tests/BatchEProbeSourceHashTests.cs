using System;
using System.IO;
using System.Text.Json;
using GpuHostLifecycleProbe;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-088 · 探针源码漂移比对护栏】
/// <para><b>缺陷：</b><c>tools/GpuHostLifecycleProbe/RecreateHandleScenario.cs</c> 的
/// <c>WriteEvidence</c> 在找不到源码（<c>guard == null</c>）时，让 <c>unchanged</c>
/// 保持**初值 <c>true</c></c> ⇒ 输出 <c>sourceHashChanged = false</c>，
/// 读起来就是"**源码未漂移**"，而实际上**根本没有做比对** ——
/// 「候选二进制是否与当前源码一致」这一项在没有源码的机器上会**恒绿**。</para>
/// <para><b>本护栏守两件事：</b></para>
/// <list type="number">
/// <item>比对**没做**时，证据里绝不能出现"未漂移"（必须是"未验证"）；</item>
/// <item>比对**做了**且确实没漂移时，必须如实报"未漂移"（不能一律报未验证）。</item>
/// </list>
/// </summary>
public sealed class BatchEProbeSourceHashTests
{
    [Fact]
    public void 护栏_找不到源码时不得输出源码未漂移()
    {
        string output = CreateOutputDirectory("source-hash-unverified");
        using Form form = new Form();
        GdiRecoveryExperiment experiment = new(form, () => { }, output, "unit-source-hash", guard: null);

        int exitCode = experiment.WriteEvidence("unit-source-hash", 0, null);

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(output, "t01-unit-source-hash.json")));
        JsonElement root = document.RootElement;

        Assert.False(
            root.GetProperty("sourceHashVerified").GetBoolean(),
            "根本没有源码可比对（guard == null）⇒ sourceHashVerified 必须是 false。");
        Assert.Equal(
            JsonValueKind.Null,
            root.GetProperty("sourceHashChanged").ValueKind);
        Assert.Equal(
            "NOT_VERIFIED_NO_SOURCE",
            root.GetProperty("sourceHashStatus").GetString());
        Assert.NotEqual(0, exitCode);
        Assert.Equal(GdiRecoveryExperiment.SourceHashNotVerifiedExitCode, exitCode);
    }

    [Fact]
    public void 护栏_找不到源码时证据里的退出码必须与进程退出码一致()
    {
        string output = CreateOutputDirectory("source-hash-unverified-exitcode");
        using Form form = new Form();
        GdiRecoveryExperiment experiment = new(form, () => { }, output, "unit-source-hash-exit", guard: null);

        int exitCode = experiment.WriteEvidence("unit-source-hash-exit", 0, null);

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(output, "t01-unit-source-hash-exit.json")));

        Assert.Equal(
            exitCode,
            document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public void 护栏_源码可比对且未漂移时必须如实报未漂移()
    {
        CandidateHashGuard? guard = CandidateHashGuard.TryCreate(AppContext.BaseDirectory);
        Assert.True(
            guard is not null,
            "在仓库里跑测试时必须能找到源码（guard == null）—— 护栏前提被破坏。");
        guard!.CaptureStart();

        string output = CreateOutputDirectory("source-hash-verified");
        using Form form = new Form();
        GdiRecoveryExperiment experiment = new(form, () => { }, output, "unit-source-hash-verified", guard);

        int exitCode = experiment.WriteEvidence("unit-source-hash-verified", 0, null);

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(output, "t01-unit-source-hash-verified.json")));
        JsonElement root = document.RootElement;

        Assert.True(
            root.GetProperty("sourceHashVerified").GetBoolean(),
            "找到了源码、也做了比对 ⇒ sourceHashVerified 必须是 true。");
        Assert.Equal(
            JsonValueKind.False,
            root.GetProperty("sourceHashChanged").ValueKind);
        Assert.Equal(
            "VERIFIED_UNCHANGED",
            root.GetProperty("sourceHashStatus").GetString());
        Assert.Equal(0, exitCode);
    }

    // ---------------------------------------------------------------- 辅助

    private static string CreateOutputDirectory(string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "batch-e-evidence", name);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }
}
