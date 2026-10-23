using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using GpuHostLifecycleProbe;
using Xunit;

namespace LiteMonitor.Tests;

/// <summary>
/// 【LM-086 · 探针持续送帧护栏】
/// <para><b>缺陷：</b><c>tools/GpuHostLifecycleProbe/RecreateHandleScenario.cs</c> 的
/// <c>PumpScene</c> 把"持续送帧"放在 <c>Task.Run</c> 里，而
/// <c>GpuPresentationHostController.UpdateScene</c> 强制要求**宿主拥有者线程**
/// （<c>EnsureOwnerThread</c> ⇒ 抛 <see cref="InvalidOperationException"/>）。
/// 那个任务又被 <c>_ = Task.Run(...)</c> **丢弃** ⇒ 异常从未被观察 ⇒
/// "持续送帧"**一次都没成功**，实验却照样**退出 0**（假绿）。</para>
/// <para><b>本护栏守两件事：</b></para>
/// <list type="number">
/// <item>送帧必须发生在**拥有者线程**（UI 线程）上 —— 不得退化成后台线程；</item>
/// <item>送帧失败必须被**记录**，并让探针以**非零退出码**收尾。</item>
/// </list>
/// </summary>
public sealed class BatchEProbeScenePumpTests
{
    // ---------------------------------------------------------------- 线程

    [Fact]
    public void 护栏_持续送帧必须发生在宿主拥有者线程()
    {
        int ownerThread = Environment.CurrentManagedThreadId;
        int observedCalls = 0;
        int? observedThread = null;

        using Form form = new Form();
        GdiRecoveryExperiment experiment = new(form, () => { }, CreateOutputDirectory("scene-pump-thread"), "unit-scene-pump", null);
        experiment.SendSceneToHost = _ =>
        {
            Interlocked.Increment(ref observedCalls);
            observedThread = Environment.CurrentManagedThreadId;
        };

        using CancellationTokenSource cancellation = new();
        experiment.PumpScene(cancellation.Token);
        PumpMessagesUntil(() => Volatile.Read(ref observedCalls) > 0, TimeSpan.FromSeconds(10));

        Assert.True(
            observedCalls > 0,
            "持续送帧一次都没有发生 —— 探针的'持续场景输入'场景等于没跑（LM-086 假绿）。");
        Assert.True(
            observedThread.HasValue,
            "送帧回调没有被调用。");
        Assert.Equal(
            ownerThread,
            observedThread.Value);
    }

    // ---------------------------------------------------------------- 失败

    [Fact]
    public void 护栏_送帧失败必须被记录()
    {
        using Form form = new Form();
        string output = CreateOutputDirectory("scene-pump-failure");
        GdiRecoveryExperiment experiment = new(form, () => { }, output, "unit-scene-pump-failure", null);
        experiment.SendSceneToHost = _ => throw new InvalidOperationException(
            "GPU presentation must be controlled on the UI thread that created it.");

        using CancellationTokenSource cancellation = new();
        experiment.PumpScene(cancellation.Token);
        PumpMessagesUntil(() => experiment.ScenePump.FailureCount > 0, TimeSpan.FromSeconds(10));

        Assert.True(
            experiment.ScenePump.FailureCount > 0,
            "送帧抛了异常却没有被计数 —— 这正是 LM-086 的假绿：异常被吞掉，实验照样退出 0。");
        Assert.True(
            experiment.ScenePump.LastFailure is not null &&
            experiment.ScenePump.LastFailure.Contains("UI thread", StringComparison.Ordinal),
            "送帧失败必须留下可读的失败详情（异常 + 堆栈），实际：" + experiment.ScenePump.LastFailure);
    }

    [Fact]
    public void 护栏_送帧失败必须让探针以非零退出码收尾()
    {
        using Form form = new Form();
        string output = CreateOutputDirectory("scene-pump-exit-code");
        GdiRecoveryExperiment experiment = new(form, () => { }, output, "unit-scene-pump-exit", null);
        experiment.SendSceneToHost = _ => throw new InvalidOperationException(
            "GPU presentation must be controlled on the UI thread that created it.");

        using CancellationTokenSource cancellation = new();
        experiment.PumpScene(cancellation.Token);
        PumpMessagesUntil(() => experiment.ScenePump.FailureCount > 0, TimeSpan.FromSeconds(10));

        int exitCode = experiment.WriteEvidence("unit-scene-pump-exit", 0, null);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(GdiRecoveryExperiment.ScenePumpFailureExitCode, exitCode);

        string evidencePath = Path.Combine(output, "t01-unit-scene-pump-exit.json");
        Assert.True(File.Exists(evidencePath), "证据文件缺失：" + evidencePath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(evidencePath));
        JsonElement scenePump = document.RootElement.GetProperty("scenePump");
        Assert.False(
            scenePump.GetProperty("ok").GetBoolean(),
            "证据文件里的 scenePump.ok 必须为 false —— 送帧失败却报成功就是假绿。");
        Assert.True(
            scenePump.GetProperty("failed").GetInt32() > 0,
            "证据文件里的 scenePump.failed 必须大于 0。");
        Assert.Equal(
            GdiRecoveryExperiment.ScenePumpFailureExitCode,
            document.RootElement.GetProperty("exitCode").GetInt32());
    }

    // ---------------------------------------------------------------- 源码

    [Fact]
    public void 护栏_探针不得在后台线程送帧()
    {
        string source = File.ReadAllText(ProbeSourcePath("RecreateHandleScenario.cs"));

        int start = source.IndexOf("internal void PumpScene", StringComparison.Ordinal);
        Assert.True(start >= 0, "找不到 PumpScene —— 护栏前提被破坏，请同步更新本用例。");
        int end = source.IndexOf("private void StopScenePump", StringComparison.Ordinal);
        Assert.True(end > start, "找不到 StopScenePump —— 护栏前提被破坏，请同步更新本用例。");

        string body = source[start..end];
        Assert.DoesNotContain(
            "Task.Run",
            body,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>
    /// 抽干消息队列，直到条件成立或超时。
    /// <para>WinForms 定时器靠 <c>WM_TIMER</c> 消息触发 ⇒ 必须有人抽消息。</para>
    /// </summary>
    private static void PumpMessagesUntil(Func<bool> condition, TimeSpan limit)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            Application.DoEvents();
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }
    }

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

    private static string ProbeSourcePath(string fileName) =>
        Path.Combine(RepositoryRoot(), "tools", "GpuHostLifecycleProbe", fileName);

    /// <summary>仓库根目录（含 LiteMonitor.sln 的那一层）。</summary>
    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiteMonitor.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（从 " + AppContext.BaseDirectory + " 向上找不到 LiteMonitor.sln）。");
    }
}
