using LiteMonitor.Rendering.Configuration;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class GpuStartupGuardTests
{
    [Fact]
    public void SuccessfulFirstFrameClearsPersistentMarker()
    {
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard first = new(directory, @"C:\apps\A");
            Assert.True(first.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));
            attempt!.MarkFirstSuccessfulFrame();
            attempt.Dispose();

            GpuStartupGuard next = new(directory, @"C:\apps\A");
            Assert.False(next.ConservativeForThisProcess);
            Assert.True(next.TryBeginAttempt(out GpuStartupAttempt? nextAttempt, out _, out _));
            nextAttempt!.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnclosedPreviousAttemptForcesConservativeStartup()
    {
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard crashed = new(directory, @"C:\apps\A");
            Assert.True(crashed.TryBeginAttempt(out GpuStartupAttempt? abandoned, out _, out _));

            GpuStartupGuard nextProcess = new(directory, @"C:\apps\A");
            Assert.True(nextProcess.ConservativeForThisProcess);
            Assert.False(nextProcess.TryBeginAttempt(
                out GpuStartupAttempt? blocked,
                out string? code,
                out _));
            Assert.Null(blocked);
            Assert.Equal(
                "gpu_startup_guard_previous_attempt_unclean",
                code);

            Assert.True(nextProcess.AllowExplicitManualRetry(out _, out _));
            Assert.True(nextProcess.TryBeginAttempt(
                out GpuStartupAttempt? retried,
                out _,
                out _));
            retried!.MarkFirstSuccessfulFrame();
            retried.Dispose();
            abandoned!.Dispose();

            GpuStartupGuard afterRetry = new(directory, @"C:\apps\A");
            Assert.False(afterRetry.ConservativeForThisProcess);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GracefulFailedInitializationDoesNotPoisonNextProcess()
    {
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard first = new(directory, @"C:\apps\A");
            Assert.True(first.TryBeginAttempt(out GpuStartupAttempt? attempt, out _, out _));
            attempt!.Dispose();

            GpuStartupGuard next = new(directory, @"C:\apps\A");
            Assert.False(next.ConservativeForThisProcess);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InstallationsAreIsolatedByStableIdentity()
    {
        string directory = TemporaryDirectory();
        try
        {
            GpuStartupGuard firstInstall = new(directory, @"C:\apps\A");
            Assert.True(firstInstall.TryBeginAttempt(out GpuStartupAttempt? abandoned, out _, out _));

            GpuStartupGuard secondInstall = new(directory, @"C:\apps\B");
            Assert.False(secondInstall.ConservativeForThisProcess);
            abandoned!.Dispose();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnwritableStateFailsClosedWithoutStartingGpu()
    {
        string parent = TemporaryDirectory();
        string stateFile = Path.Combine(parent, "not-a-directory");
        File.WriteAllText(stateFile, "occupied");
        try
        {
            GpuStartupGuard guard = new(stateFile, @"C:\apps\A");
            Assert.True(guard.ConservativeForThisProcess);
            Assert.False(guard.TryBeginAttempt(
                out GpuStartupAttempt? attempt,
                out string? code,
                out _));
            Assert.Null(attempt);
            Assert.Equal("gpu_startup_guard_storage_unavailable", code);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "LiteMonitor-startup-guard-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
