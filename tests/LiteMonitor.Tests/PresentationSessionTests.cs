using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using Xunit;

namespace LiteMonitor.Tests;

public sealed class PresentationSessionTests
{
    private static readonly RenderGenerations Generations = new(1, 1, 1, 1, 1);

    [Fact]
    public void FixedModeDoesNotReadOrChangeWindowAffinity()
    {
        RecordingAdapter adapter = new(originalAffinity: 0);
        PresentationSession session = new(adapter);
        IntPtr handle = new(42);

        Assert.True(session.Start(
            handle,
            Generations,
            WindowCapturePolicy.None,
            out _,
            out _));

        Assert.Equal(0, adapter.ReadCount);
        Assert.Empty(adapter.SetHistory);
        Assert.True(session.BeginStop(out _));
        Assert.True(session.CompleteStop(out _, out _));
        Assert.Empty(adapter.SetHistory);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x11u)]
    public void StopRestoresTheOriginalAffinity(uint originalAffinity)
    {
        RecordingAdapter adapter = new(originalAffinity);
        PresentationSession session = new(adapter);
        IntPtr handle = new(42);

        Assert.True(session.Start(
            handle,
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        Assert.Equal(0x11u, adapter.CurrentAffinity);
        Assert.True(session.BeginStop(out _));
        Assert.True(session.CompleteStop(out _, out _));

        Assert.Equal(originalAffinity, adapter.CurrentAffinity);
        Assert.Equal(0, session.WindowHandle.ToInt64());
        Assert.Equal(RenderLifecycleState.Disabled, session.State);
    }

    [Fact]
    public void RepeatedExcludePolicyKeepsSessionOwnershipUntilStop()
    {
        RecordingAdapter adapter = new(originalAffinity: 0);
        PresentationSession session = new(adapter);
        IntPtr handle = new(42);

        Assert.True(session.Start(
            handle,
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        Assert.True(session.SetCapturePolicy(
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        Assert.Equal(0x11u, adapter.CurrentAffinity);

        Assert.True(session.BeginStop(out _));
        Assert.True(session.CompleteStop(out _, out _));
        Assert.Equal(0u, adapter.CurrentAffinity);
        Assert.Equal(
            [
                (handle, 0x11u),
                (handle, 0u)
            ],
            adapter.SetHistory);
    }

    [Fact]
    public void AlreadyExcludedWindowIsNotReclassifiedAsSessionOwned()
    {
        RecordingAdapter adapter = new(originalAffinity: 0x11);
        PresentationSession session = new(adapter);
        IntPtr handle = new(42);

        Assert.True(session.Start(
            handle,
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        Assert.True(session.SetCapturePolicy(
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));
        Assert.True(session.BeginStop(out _));
        Assert.True(session.CompleteStop(out _, out _));

        Assert.Equal(0x11u, adapter.CurrentAffinity);
        Assert.Empty(adapter.SetHistory);
    }

    [Fact]
    public void FailedAffinityWriteDoesNotClaimRunning()
    {
        RecordingAdapter adapter = new(
            originalAffinity: 0,
            failSet: true);
        PresentationSession session = new(adapter);

        Assert.False(session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out string? code,
            out _));
        Assert.Equal("capture_policy_rejected", code);
        Assert.Equal(RenderLifecycleState.Fallback, session.State);
        Assert.Equal(0u, adapter.CurrentAffinity);
    }

    [Fact]
    public void SecondSessionCannotStartBeforeFirstStops()
    {
        PresentationSession session = new(new RecordingAdapter());
        Assert.True(session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.None,
            out _,
            out _));

        Assert.False(session.Start(
            new IntPtr(43),
            Generations,
            WindowCapturePolicy.None,
            out string? code,
            out _));
        Assert.Equal("presentation_session_already_active", code);
    }

    [Fact]
    public void HandleRecreationRestoresOldAffinityBeforeApplyingNewWindow()
    {
        RecordingAdapter adapter = new(originalAffinity: 0);
        PresentationSession session = new(adapter);
        session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _);
        RenderGenerations recreated = Generations with { Window = 2, Size = 2 };
        Assert.True(session.RecreateWindow(
            new IntPtr(43),
            recreated,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _));

        Assert.Equal(new IntPtr(43), session.WindowHandle);
        Assert.Equal(RenderLifecycleState.Running, session.State);
        Assert.Equal(
            [
                (new IntPtr(42), 0x11u),
                (new IntPtr(42), 0u),
                (new IntPtr(43), 0x11u)
            ],
            adapter.SetHistory);
        Assert.True(
            session.Diagnostics(WindowCapturePolicy.WdaExcludeFromCapture)
                .EntireTopLevelWindowExcluded);
    }

    [Fact]
    public void FailedLiveCaptureCanRestoreAffinityWithoutStoppingPresentation()
    {
        RecordingAdapter adapter = new(originalAffinity: 0x11);
        PresentationSession session = new(adapter);
        session.Start(
            new IntPtr(42),
            Generations,
            WindowCapturePolicy.WdaExcludeFromCapture,
            out _,
            out _);

        Assert.True(session.SetCapturePolicy(
            WindowCapturePolicy.None,
            out _,
            out _));
        Assert.Equal(0x11u, adapter.CurrentAffinity);
        Assert.Equal(RenderLifecycleState.Running, session.State);
        Assert.False(
            session.Diagnostics(WindowCapturePolicy.None)
                .EntireTopLevelWindowExcluded);
    }

    private sealed class RecordingAdapter : IPresentationWindowAdapter
    {
        private readonly bool _failSet;

        public RecordingAdapter(
            uint originalAffinity = 0,
            bool failSet = false)
        {
            CurrentAffinity = originalAffinity;
            _failSet = failSet;
        }

        public uint CurrentAffinity { get; set; }

        public int ReadCount { get; private set; }

        public List<(IntPtr Window, uint Affinity)> SetHistory { get; } = [];

        public bool TryGetDisplayAffinity(
            IntPtr windowHandle,
            out uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            ReadCount++;
            affinity = CurrentAffinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }

        public bool TrySetDisplayAffinity(
            IntPtr windowHandle,
            uint affinity,
            out string? errorCode,
            out string? errorMessage)
        {
            if (_failSet)
            {
                errorCode = "capture_policy_rejected";
                errorMessage = "Rejected by test adapter.";
                return false;
            }

            SetHistory.Add((windowHandle, affinity));
            CurrentAffinity = affinity;
            errorCode = null;
            errorMessage = null;
            return true;
        }
    }
}
