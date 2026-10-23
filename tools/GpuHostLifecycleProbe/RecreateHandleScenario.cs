using System.Diagnostics;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Scene;

namespace GpuHostLifecycleProbe;

/// <summary>
/// Colour encoding of the dynamic GDI frame.
///
/// Every frame gets a colour that changes on every frame, so that a live GDI
/// frame can be told apart from a frozen GPU frame (GPU plan §7.2 forbids
/// mistaking the two).
/// </summary>
internal static class FramePalette
{
    public static Color ColorFor(int frameIndex)
    {
        int index = Math.Max(1, frameIndex);
        return Color.FromArgb(
            255,
            (byte)((index * 53) % 256),
            (byte)((index * 97) % 256),
            (byte)((index * 149) % 256));
    }
}

/// <summary>Paints one dynamic GDI frame.</summary>
internal static class FramePainter
{
    public static void Draw(Graphics graphics, Color color, int frameIndex)
    {
        using SolidBrush brush = new(color);
        graphics.FillRectangle(brush, -8, -8, 8192, 8192);

        using SolidBrush textBrush = new(Color.White);
        using SolidBrush shadowBrush = new(Color.Black);
        using Font font = new("Consolas", 14f, FontStyle.Bold);
        graphics.FillRectangle(shadowBrush, 4, 2, 140, 22);
        graphics.DrawString(
            $"GDI {frameIndex}",
            font,
            textBrush,
            6,
            3,
            StringFormat.GenericTypographic);
    }
}

/// <summary>Exposes the dynamic frame state to whatever paints the window.</summary>
internal interface IGdiFrameHost
{
    int PaintCount { get; }

    Color CurrentFrameColor { get; }
}

internal sealed record GdiFrameObservation(
    string Phase,
    int FrameIndex,
    Color Expected,
    Color Observed,
    int MatchingPixels,
    int TotalPixels,
    double MatchRatio,
    bool Matched,
    byte LayeredAlpha);

internal sealed record GdiPhaseResult(
    string Phase,
    IReadOnlyList<GdiFrameObservation> Frames,
    bool AllFramesMatched,
    bool AdjacentFramesDiffer,
    string ScreenshotPath);

internal sealed record HandleEventRecord(
    int Sequence,
    string Kind,
    long Generation,
    long Handle,
    bool Visible);

internal sealed record WindowStateRecord(
    string Label,
    bool Visible,
    bool TopMostStyle,
    int ExStyle,
    int Style,
    Rectangle WindowRect,
    Rectangle ClientRectOnScreen,
    int NativeRegionType,
    bool NativeRegionCoversOrigin,
    bool LayeredAttributesReadable,
    byte LayeredAlpha,
    uint LayeredColorKey,
    uint LayeredFlags,
    long ForegroundWindow,
    bool ForegroundIsThisWindow,
    double Opacity);

internal sealed record GpuPhaseRecord(
    string Phase,
    bool Started,
    string? ErrorCode,
    string? ErrorMessage,
    uint PresentCount,
    string? AdapterName,
    string? ScenePngPath,
    Color ObservedDesktopCenter,
    string? DiagnosticsJson);

internal sealed record RecreateAssertion(
    long GenerationBefore,
    long GenerationAfter,
    long HandleBefore,
    long HandleAfter,
    bool HandleDestroyedObserved,
    bool HandleCreatedObserved,
    bool DestroyedBeforeCreated,
    bool GenerationAdvancedByExactlyOne,
    bool Passed,
    string Note);

/// <summary>
/// Sends scene snapshots to the GPU host **on the UI thread** and records what
/// really happened.
///
/// ★ LM-086：原实现在 <c>Task.Run</c> 里送帧，而
/// <c>GpuPresentationHostController.UpdateScene</c> 强制要求**宿主拥有者线程**
/// （<c>EnsureOwnerThread</c> ⇒ 抛 <see cref="InvalidOperationException"/>）。
/// 那个任务又被 <c>_ = Task.Run(...)</c> **丢弃** ⇒ 异常从未被观察 ⇒
/// "持续送帧"一次都没成功，实验却照样退出 0（**假绿**）。
/// ⇒ 这里改用 WinForms 定时器（**必然**在拥有消息循环的 UI 线程上触发），
/// 并把成功 / 失败**如实计数**，让调用方能把"送帧失败"变成非零退出码。
/// </summary>
internal sealed class ScenePump : IDisposable
{
    private readonly Action _send;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _disposed;

    public ScenePump(Action send, int intervalMs = 120)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _timer = new System.Windows.Forms.Timer { Interval = intervalMs };
        _timer.Tick += OnTick;
    }

    /// <summary>成功送帧的次数。</summary>
    public int SuccessCount { get; private set; }

    /// <summary>送帧失败的次数。</summary>
    public int FailureCount { get; private set; }

    /// <summary>最近一次送帧失败的全部细节（异常 + 堆栈）。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>定时器当前是否在跑。</summary>
    public bool Running => _timer.Enabled;

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ScenePump));
        }

        _timer.Start();
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            _send();
            SuccessCount++;
        }
        catch (Exception exception)
        {
            // ★ 失败必须被**记录**：把异常吞掉再继续跑 = 假绿。
            FailureCount++;
            LastFailure = exception.ToString();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer.Dispose();
    }
}

/// <summary>
/// The T01 A/B experiment: GPU frames, then "stop the GPU but keep the HWND"
/// (control arm A) versus "stop the GPU and recreate the HWND" (experiment arm
/// B). Both arms run in the same process, on the same window, with continuous
/// input, and the verdict is built on desktop pixel read-back only.
/// </summary>
internal sealed class GdiRecoveryExperiment : IGdiFrameHost
{
    private const int SamplePadding = 16;

    /// <summary>
    /// 进程退出码：**持续送帧失败**（LM-086）。
    /// <para>0 = 通过；1 = 运行异常；2 = 源码漂移；3 = 超时；4 = 隔离门失败；
    /// 5 = 持续送帧失败；6 = 源码漂移比对**未做**（NOT_VERIFIED）；64 = 参数错误；70 = 未处理异常。</para>
    /// </summary>
    internal const int ScenePumpFailureExitCode = 5;

    /// <summary>
    /// 进程退出码：**源码漂移比对未做**（LM-088，NOT_VERIFIED）。
    /// <para>找不到源码 ⇒ 根本没有做比对 ⇒ 这一项**不能**当成"通过"（假绿）。</para>
    /// </summary>
    internal const int SourceHashNotVerifiedExitCode = 6;

    private readonly Form _form;
    private readonly Action _recreateHandle;
    private readonly string _output;
    private readonly string _scenarioId;
    private readonly CandidateHashGuard? _guard;
    private readonly ScenePump _scenePump;
    private readonly List<object> _steps = new();
    private readonly List<HandleEventRecord> _handleEvents = new();
    private readonly List<GpuPhaseRecord> _gpuPhases = new();

    private CancellationTokenRegistration _scenePumpRegistration;
    private GpuPresentationHostController? _host;
    private int _frameIndex;
    private Color? _forcedColor;
    private int _eventSequence;
    private long _handleGeneration;
    private long _lastHandleValue;
    private int _sceneCounter;
    private int _paintCount;

    public GdiRecoveryExperiment(
        Form form,
        Action recreateHandle,
        string output,
        string scenarioId,
        CandidateHashGuard? guard)
    {
        _form = form;
        _recreateHandle = recreateHandle;
        _output = output;
        _scenarioId = scenarioId;
        _guard = guard;

        // ★ LM-086：送帧通道被单独抽出来（UI 线程定时器），失败可以被计数。
        _scenePump = new ScenePump(SendScene);
        SendSceneToHost = host =>
        {
            if (host is null)
            {
                return;
            }

            host.UpdateScene(BuildScene(host.Generations));
        };

        _form.HandleCreated += OnHandleCreated;
        _form.HandleDestroyed += OnHandleDestroyed;
    }

    /// <summary>
    /// 送一帧场景到 GPU 宿主。默认实现走生产路径（<c>UpdateScene</c>）。
    /// <para>★ 它**只能**在宿主拥有者线程（UI 线程）上调用 —— 测试可以替换成
    /// 会抛异常的版本，用来验证"送帧失败必须被如实报告、不得退出 0"。
    /// <paramref name="host"/> 为 <c>null</c> 表示"此刻没有 GPU 宿主"
    /// （默认实现此时什么都不做；注入的测试版本可以把"没有宿主"也当成失败）。</para>
    /// </summary>
    internal Action<GpuPresentationHostController?> SendSceneToHost { get; set; }

    /// <summary>持续送帧通道（LM-086）。</summary>
    internal ScenePump ScenePump => _scenePump;

    public string OutputDirectory => _output;

    public int PaintCount => _paintCount;

    public int CurrentFrameIndex => _frameIndex;

    public Color CurrentFrameColor => _forcedColor ?? FramePalette.ColorFor(_frameIndex);

    public List<object> Steps => _steps;

    public List<HandleEventRecord> HandleEvents => _handleEvents;

    public List<GpuPhaseRecord> GpuPhases => _gpuPhases;

    public GdiPhaseResult? Baseline { get; private set; }

    public GdiPhaseResult? RoundA { get; private set; }

    public GdiPhaseResult? RoundB { get; private set; }

    public RecreateAssertion? Recreate { get; private set; }

    public WindowStateRecord? StateBeforeRecreate { get; private set; }

    public WindowStateRecord? StateAfterRecreate { get; private set; }

    public string? PositionProbeResult { get; private set; }

    public string? WindowAdapter { get; private set; }

    /// <summary>
    /// Optional hook that repaints the dynamic frame straight onto the window
    /// device context after the host has finished its own painting. The real
    /// MainForm renders its legacy UI from MainForm.OnPaint -> _ui.Render,
    /// which runs *after* base.OnPaint and therefore covers anything the Paint
    /// event drew. The probe forms have no such follower and leave this null.
    /// </summary>
    public Action? AfterPaintOverlay { get; set; }

    /// <summary>Scenario specific observations merged into the evidence file.</summary>
    public List<object> ExtraObservations { get; } = [];

    public void NotePaint() => _paintCount++;

    private void OnHandleCreated(object? sender, EventArgs e)
    {
        _handleGeneration++;
        _lastHandleValue = _form.IsHandleCreated ? _form.Handle.ToInt64() : 0L;
        _handleEvents.Add(new HandleEventRecord(++_eventSequence, "HandleCreated", _handleGeneration, _lastHandleValue, _form.Visible));
    }

    private void OnHandleDestroyed(object? sender, EventArgs e)
    {
        _handleEvents.Add(new HandleEventRecord(++_eventSequence, "HandleDestroyed", _handleGeneration, _lastHandleValue, _form.Visible));
    }

    /// <summary>Runs the whole A/B sequence.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        RecordEnvironment();

        // ---- Phase 0: put the window where the desktop read-back can see it ----
        PositionProbeResult = await PositionForObservationAsync(cancellationToken);
        if (!PositionProbeResult.StartsWith("ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "No unobstructed window position was found, so the desktop read-back cannot be trusted: " +
                PositionProbeResult);
        }

        // ---- Phase 1: validate the measurement pipeline before the GPU runs ----
        Baseline = await CaptureGdiPhaseAsync("baseline-pre-gpu", 3, cancellationToken);
        if (!Baseline.AllFramesMatched || !Baseline.AdjacentFramesDiffer)
        {
            throw new InvalidOperationException(
                "The desktop read-back pipeline did not validate before the GPU phase " +
                $"(allMatched={Baseline.AllFramesMatched}, dynamic={Baseline.AdjacentFramesDiffer}). " +
                "Any GDI verdict measured afterwards would be unreliable.");
        }

        // ---- Phase 2: GPU up, wait for a real Present ----
        await StartGpuAsync("gpu-phase-1", cancellationToken);
        await WaitForFirstPresentAsync("gpu-phase-1", cancellationToken);
        await Task.Delay(300, cancellationToken);
        _gpuPhases.Add(await CaptureGpuDesktopAsync("gpu-phase-1", cancellationToken));

        // ---- Phase 3: arm A - stop the GPU, keep the HWND ----
        StopGpu();
        await Task.Delay(500, cancellationToken);
        RoundA = await CaptureGdiPhaseAsync("roundA-stop-gpu-keep-hwnd", 3, cancellationToken);

        // ---- Phase 4: GPU up again on the same HWND ----
        await StartGpuAsync("gpu-phase-2", cancellationToken);
        await WaitForFirstPresentAsync("gpu-phase-2", cancellationToken);
        await Task.Delay(300, cancellationToken);
        _gpuPhases.Add(await CaptureGpuDesktopAsync("gpu-phase-2", cancellationToken));

        // ---- Phase 5: arm B - stop the GPU and recreate the HWND ----
        StopGpu();
        await Task.Delay(300, cancellationToken);
        StateBeforeRecreate = SnapshotWindowState("before-recreate");
        Recreate = PerformRecreate();
        await Task.Delay(500, cancellationToken);
        StateAfterRecreate = SnapshotWindowState("after-recreate");
        RoundB = await CaptureGdiPhaseAsync("roundB-stop-gpu-recreate-hwnd", 3, cancellationToken);
    }

    private void RecordEnvironment()
    {
        IntPtr foregroundBefore = DesktopObservation.GetForegroundWindow();
        (int clientX, int clientY, int clientWidth, int clientHeight) =
            _form.IsHandleCreated
                ? DesktopObservation.ClientRectOnScreen(_form.Handle)
                : (0, 0, 0, 0);
        WindowAdapter = DisplayAdapters.ResolveAdapterForPoint(
            clientX + (clientWidth / 2),
            clientY + (clientHeight / 2));

        _steps.Add(new
        {
            step = "environment",
            scenario = _scenarioId,
            startedAt = DateTimeOffset.Now,
            processId = Environment.ProcessId,
            foregroundWindowBefore = foregroundBefore.ToInt64(),
            windowAdapter = WindowAdapter,
            screens = Screen.AllScreens.Select(screen => new
            {
                screen.DeviceName,
                screen.Primary,
                screen.Bounds,
                screen.WorkingArea
            }).ToList(),
            displayAdapters = DisplayAdapters.Enumerate(),
            repoRoot = _guard?.RepositoryRoot,
            candidateHashesAtStart = _guard?.CaptureStart()
        });
    }

    /// <summary>
    /// Moves the window to a place where the desktop read-back can actually see
    /// it. Without this step an overlapping window would silently produce a
    /// false negative.
    /// </summary>
    private async Task<string> PositionForObservationAsync(CancellationToken cancellationToken)
    {
        List<Point> candidates = [];
        foreach (Screen screen in Screen.AllScreens)
        {
            candidates.Add(new Point(screen.Bounds.Left + 70, screen.Bounds.Top + 70));
            candidates.Add(new Point(screen.Bounds.Left + 140, screen.Bounds.Top + 260));
            candidates.Add(new Point(screen.Bounds.Right - 520, screen.Bounds.Top + 70));
        }

        foreach (Point candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _form.Location = candidate;
            await Task.Delay(260, cancellationToken);

            _forcedColor = Color.Magenta;
            _form.Invalidate();
            _form.Update();
            AfterPaintOverlay?.Invoke();
            await Task.Delay(220, cancellationToken);
            AfterPaintOverlay?.Invoke();

            (int x, int y, int width, int height) = DesktopObservation.ClientRectOnScreen(_form.Handle);
            using Bitmap region = DesktopObservation.CaptureRegion(
                x - SamplePadding,
                y - SamplePadding,
                width + (2 * SamplePadding),
                height + (2 * SamplePadding));

            int blockX = SamplePadding + (width / 2) - (DesktopObservation.BlockSize / 2);
            int blockY = SamplePadding + (height / 2) - (DesktopObservation.BlockSize / 2);
            BlockMatch match = DesktopObservation.MatchBlock(
                region,
                blockX,
                blockY,
                DesktopObservation.BlockSize,
                Color.Magenta);
            _forcedColor = null;

            if (match.Matched)
            {
                string result = $"ok at {candidate.X},{candidate.Y} (display {_form.Bounds})";
                _steps.Add(new { step = "position-probe", result, candidate });
                return result;
            }

            _steps.Add(new
            {
                step = "position-probe",
                result = "obstructed",
                candidate,
                observed = match.ObservedCenter.ToArgb().ToString("X8"),
                matchRatio = match.MatchRatio
            });
        }

        return "no unobstructed candidate position found";
    }

    private async Task StartGpuAsync(string phase, CancellationToken cancellationToken)
    {
        _host = new GpuPresentationHostController(
            _form,
            MaterialKind.LiquidGlass,
            liveBackgroundRequested: false);

        if (!_host.Start(WindowCapturePolicy.None, out string? errorCode, out string? errorMessage))
        {
            _steps.Add(new { step = "gpu-start", phase, accepted = false, errorCode, errorMessage });
            throw new InvalidOperationException($"GPU host start failed in {phase}: {errorCode} {errorMessage}");
        }

        PumpScene(cancellationToken);
        _host.UpdateScene(BuildScene(_host.Generations));
        _host.RenderNow();
        _steps.Add(new { step = "gpu-start", phase, accepted = true, errorCode, errorMessage });
    }

    /// <summary>
    /// Starts the continuous scene pump.
    ///
    /// ★ LM-086：这**必须**跑在 UI 线程（GPU 宿主的拥有者线程）上。
    ///   原实现用 <c>Task.Run</c> 在后台线程调 <c>UpdateScene</c> ⇒
    ///   <c>EnsureOwnerThread</c> 抛 <see cref="InvalidOperationException"/> ⇒
    ///   任务被丢弃 ⇒ 异常从未被观察 ⇒ "持续送帧"一次都没成功却退出 0（假绿）。
    ///   现在改用 WinForms 定时器：它**必然**在拥有消息循环的 UI 线程上触发，
    ///   失败会被 <see cref="ScenePump"/> 计数并最终变成非零退出码。
    /// </summary>
    internal void PumpScene(CancellationToken cancellationToken)
    {
        StopScenePump();
        _scenePump.Start();
        if (cancellationToken.CanBeCanceled)
        {
            _scenePumpRegistration = cancellationToken.Register(
                static state =>
                {
                    try
                    {
                        ((ScenePump)state!).Stop();
                    }
                    catch (Exception)
                    {
                        // 停止失败不影响主流程：主流程自己也会 StopScenePump。
                    }
                },
                _scenePump);
        }
    }

    private void StopScenePump()
    {
        _scenePumpRegistration.Dispose();
        _scenePumpRegistration = default;
        _scenePump.Stop();
    }

    private void SendScene()
    {
        SendSceneToHost(_host);
    }

    private async Task WaitForFirstPresentAsync(string phase, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 20000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuSceneRendererDiagnostics? diagnostics = _host?.Diagnostics();
            if (diagnostics is not null && diagnostics.LastPresentCount > 0)
            {
                _steps.Add(new
                {
                    step = "first-present",
                    phase,
                    presentCount = diagnostics.LastPresentCount,
                    adapter = diagnostics.AdapterName,
                    syncInterval = diagnostics.PresentSyncInterval,
                    elapsedMs = clock.ElapsedMilliseconds
                });
                return;
            }

            await Task.Delay(40, cancellationToken);
        }

        throw new InvalidOperationException($"No successful Present was observed in {phase} within 20000 ms.");
    }

    private async Task<GpuPhaseRecord> CaptureGpuDesktopAsync(string phase, CancellationToken cancellationToken)
    {
        string scenePath = Path.Combine(_output, $"{phase}-scene.png");
        try
        {
            _host?.CaptureScenePng(scenePath);
        }
        catch (Exception exception)
        {
            _steps.Add(new { step = "scene-capture", phase, error = exception.Message });
        }

        (int x, int y, int width, int height) = DesktopObservation.ClientRectOnScreen(_form.Handle);
        using Bitmap region = DesktopObservation.CaptureRegion(
            x - SamplePadding,
            y - SamplePadding,
            width + (2 * SamplePadding),
            height + (2 * SamplePadding));
        string screenshot = Path.Combine(_output, $"{phase}-desktop.png");
        region.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);

        int blockX = SamplePadding + (width / 2) - (DesktopObservation.BlockSize / 2);
        int blockY = SamplePadding + (height / 2) - (DesktopObservation.BlockSize / 2);
        BlockMatch match = DesktopObservation.MatchBlock(region, blockX, blockY, DesktopObservation.BlockSize, Color.Black);

        GpuSceneRendererDiagnostics? diagnostics = _host?.Diagnostics();
        _steps.Add(new
        {
            step = "gpu-desktop",
            phase,
            observedCenter = match.ObservedCenter.ToArgb().ToString("X8"),
            presentCount = diagnostics?.LastPresentCount ?? 0,
            adapter = diagnostics?.AdapterName,
            screenshot
        });

        return new GpuPhaseRecord(
            phase,
            true,
            null,
            null,
            diagnostics?.LastPresentCount ?? 0,
            diagnostics?.AdapterName,
            File.Exists(scenePath) ? scenePath : null,
            match.ObservedCenter,
            diagnostics is null ? null : System.Text.Json.JsonSerializer.Serialize(diagnostics));
    }

    private void StopGpu()
    {
        // ★ LM-086：先停送帧，再拆宿主 —— 否则定时器可能在宿主被 Dispose
        //   之后才触发，把一次正常的收尾记成"送帧失败"。
        StopScenePump();

        if (_host is null)
        {
            return;
        }

        GpuPresentationHostController host = _host;
        _host = null;
        host.Dispose();
        _steps.Add(new
        {
            step = "gpu-stopped",
            hostDisposed = true,
            windowCapturePolicyRestored = host.WindowCapturePolicyRestored,
            resources = host.ResourceSnapshot
        });
    }

    private RecreateAssertion PerformRecreate()
    {
        long generationBefore = _handleGeneration;
        long handleBefore = _lastHandleValue;
        int eventCountBefore = _handleEvents.Count;

        _recreateHandle();

        List<HandleEventRecord> newEvents = _handleEvents.Skip(eventCountBefore).ToList();
        int destroyedIndex = newEvents.FindIndex(record => record.Kind == "HandleDestroyed");
        int createdIndex = newEvents.FindIndex(record => record.Kind == "HandleCreated");
        bool destroyed = destroyedIndex >= 0;
        bool created = createdIndex >= 0;
        bool ordered = destroyed && created && destroyedIndex < createdIndex;
        bool monotonic = _handleGeneration == generationBefore + 1;

        RecreateAssertion assertion = new(
            generationBefore,
            _handleGeneration,
            handleBefore,
            _lastHandleValue,
            destroyed,
            created,
            ordered,
            monotonic,
            destroyed && created && ordered && monotonic,
            "The rebuild is proven by the HandleDestroyed/HandleCreated event order and the " +
            "monotonic handle generation. The native handle value is recorded for information " +
            "only and is never the assertion (GPU plan §4.1).");

        _steps.Add(new { step = "recreate", assertion, newEvents });
        return assertion;
    }

    private async Task<GdiPhaseResult> CaptureGdiPhaseAsync(string phase, int frameCount, CancellationToken cancellationToken)
    {
        List<GdiFrameObservation> frames = [];
        string screenshot = string.Empty;

        for (int index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int frame = ++_frameIndex;
            _form.Invalidate();
            _form.Update();
            // Draw the dynamic frame after the host's own painting so that the
            // production legacy render cannot cover it.
            AfterPaintOverlay?.Invoke();
            await Task.Delay(200, cancellationToken);
            AfterPaintOverlay?.Invoke();
            await Task.Delay(120, cancellationToken);

            (int x, int y, int width, int height) = DesktopObservation.ClientRectOnScreen(_form.Handle);
            using Bitmap region = DesktopObservation.CaptureRegion(
                x - SamplePadding,
                y - SamplePadding,
                width + (2 * SamplePadding),
                height + (2 * SamplePadding));

            Color background = region.GetPixel(2, SamplePadding + (height / 2));
            (bool layeredReadable, byte alpha, _, _) = DesktopObservation.LayeredAttributes(_form.Handle);
            Color expected = Compose(FramePalette.ColorFor(frame), background, layeredReadable ? alpha : (byte)255);

            int blockX = SamplePadding + (width / 2) - (DesktopObservation.BlockSize / 2);
            int blockY = SamplePadding + (height / 2) - (DesktopObservation.BlockSize / 2);
            BlockMatch match = DesktopObservation.MatchBlock(region, blockX, blockY, DesktopObservation.BlockSize, expected);

            frames.Add(new GdiFrameObservation(
                phase,
                frame,
                expected,
                match.ObservedCenter,
                match.MatchingPixels,
                match.TotalPixels,
                match.MatchRatio,
                match.Matched,
                layeredReadable ? alpha : (byte)0));

            screenshot = Path.Combine(_output, $"{phase}-frame{frame}.png");
            region.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
        }

        bool allMatched = frames.All(frame => frame.Matched);
        bool dynamic = true;
        for (int index = 1; index < frames.Count; index++)
        {
            if (!DesktopObservation.ColorsDiffer(frames[index - 1].Observed, frames[index].Observed))
            {
                dynamic = false;
            }
        }

        _steps.Add(new
        {
            step = "gdi-phase",
            phase,
            frames = frames.Select(frame => new
            {
                frame.FrameIndex,
                expected = frame.Expected.ToArgb().ToString("X8"),
                observed = frame.Observed.ToArgb().ToString("X8"),
                frame.MatchingPixels,
                frame.TotalPixels,
                frame.MatchRatio,
                frame.Matched,
                frame.LayeredAlpha
            }).ToList(),
            allFramesMatched = allMatched,
            adjacentFramesDiffer = dynamic,
            screenshot
        });

        return new GdiPhaseResult(phase, frames, allMatched, dynamic, screenshot);
    }

    private static Color Compose(Color frame, Color background, byte alpha)
    {
        if (alpha >= 255)
        {
            return frame;
        }

        double a = alpha / 255d;
        return Color.FromArgb(
            (int)Math.Round((frame.R * a) + (background.R * (1d - a))),
            (int)Math.Round((frame.G * a) + (background.G * (1d - a))),
            (int)Math.Round((frame.B * a) + (background.B * (1d - a))));
    }

    public WindowStateRecord SnapshotWindowState(string label)
    {
        IntPtr handle = _form.IsHandleCreated ? _form.Handle : IntPtr.Zero;
        Rectangle windowRect = handle == IntPtr.Zero ? Rectangle.Empty : DesktopObservation.WindowRectOnScreen(handle);
        (int x, int y, int width, int height) = handle == IntPtr.Zero
            ? (0, 0, 0, 0)
            : DesktopObservation.ClientRectOnScreen(handle);
        (int regionType, bool coversOrigin) = handle == IntPtr.Zero ? (-1, false) : DesktopObservation.NativeRegion(handle);
        (bool layeredReadable, byte alpha, uint colorKey, uint flags) = handle == IntPtr.Zero
            ? (false, 0, 0, 0)
            : DesktopObservation.LayeredAttributes(handle);
        IntPtr foreground = DesktopObservation.GetForegroundWindow();

        return new WindowStateRecord(
            label,
            _form.Visible,
            handle != IntPtr.Zero && (DesktopObservation.ExStyle(handle) & DesktopObservation.WsExTopmost) != 0,
            handle == IntPtr.Zero ? 0 : DesktopObservation.ExStyle(handle),
            handle == IntPtr.Zero ? 0 : DesktopObservation.Style(handle),
            windowRect,
            new Rectangle(x, y, width, height),
            regionType,
            coversOrigin,
            layeredReadable,
            alpha,
            colorKey,
            flags,
            foreground.ToInt64(),
            foreground != IntPtr.Zero && foreground == handle,
            _form.Opacity);
    }

    private SceneSnapshot BuildScene(RenderGenerations generations)
    {
        _sceneCounter++;
        float width = _form.ClientSize.Width;
        float height = _form.ClientSize.Height;
        int tick = _sceneCounter;
        return SceneSnapshotBuilder.Build(
            generations,
            _form.ClientSize.Width,
            _form.ClientSize.Height,
            _form.DeviceDpi,
            [
                new SceneSourceGroup(
                    "CPU",
                    "SYSTEM PULSE",
                    new LogicalRect(16, 18, width - 32, height * 0.46f),
                    [
                        Metric("cpu.load", "CPU LOAD", (40 + (tick % 50)).ToString(), "%", (40 + (tick % 50)) / 100d, MetricStatus.Normal),
                        Metric("cpu.temp", "PACKAGE", (50 + (tick % 30)).ToString(), "C", (50 + (tick % 30)) / 100d, MetricStatus.Warning)
                    ]),
                new SceneSourceGroup(
                    "GPU",
                    "GPU PACKAGE",
                    new LogicalRect(16, height * 0.50f, width - 32, height * 0.42f),
                    [
                        Metric("gpu.load", "GPU LOAD", (30 + (tick % 60)).ToString(), "%", (30 + (tick % 60)) / 100d, MetricStatus.Normal),
                        Metric("gpu.temp", "GPU TEMP", (40 + (tick % 20)).ToString(), "C", (40 + (tick % 20)) / 100d, MetricStatus.Normal),
                        Metric("gpu.vram", "VRAM", (5 + (tick % 4)).ToString(), "GB", 0.39, MetricStatus.Normal)
                    ])
            ]);
    }

    private static SceneSourceMetric Metric(
        string id,
        string label,
        string value,
        string unit,
        double progress,
        MetricStatus status) =>
        new(id, label, value, unit, progress, status, false, default);

    /// <summary>Writes the evidence file and returns the process exit code.</summary>
    public int WriteEvidence(string scenarioId, int exitCode, string? failure)
    {
        // ★ LM-086：收尾时确保送帧通道已经停下（失败计数才是最终值）。
        StopScenePump();

        // ★ LM-088：比对**没做**时必须**如实说没做**。
        //   原实现让 `unchanged` 保持初值 true ⇒ 输出 `sourceHashChanged = false`
        //   （读起来就是"源码未漂移"），而实际上**根本没有做比对** ——
        //   「候选二进制是否与当前源码一致」这一项在没有源码的机器上会**恒绿**。
        bool sourceHashVerified = _guard is not null;
        bool unchanged = false;
        IReadOnlyDictionary<string, string>? endHashes = null;
        if (_guard is not null)
        {
            unchanged = _guard.VerifyUnchanged(out endHashes);
        }

        // ★ 没做比对 ⇒ `sourceHashChanged` 必须是 **null**（"不知道"），
        //   绝不能是 false（"没漂移"）。
        bool? sourceHashChanged = sourceHashVerified ? !unchanged : null;
        string sourceHashStatus = sourceHashVerified
            ? (unchanged ? "VERIFIED_UNCHANGED" : "VERIFIED_CHANGED")
            : "NOT_VERIFIED_NO_SOURCE";

        // ★ LM-086：持续送帧的**真实**结果。
        //   `required` = 这次运行真的进过 GPU 阶段（否则"持续送帧"不是本实验的前提）。
        int scenePumpSent = _scenePump.SuccessCount;
        int scenePumpFailed = _scenePump.FailureCount;
        bool scenePumpRequired = _gpuPhases.Count > 0;
        bool scenePumpOk = scenePumpFailed == 0 && (!scenePumpRequired || scenePumpSent > 0);
        string? scenePumpProblem = scenePumpFailed > 0
            ? "The continuous scene pump failed " + scenePumpFailed + " time(s): " + _scenePump.LastFailure
            : scenePumpRequired && scenePumpSent == 0
                ? "The continuous scene pump never delivered a single frame."
                : null;

        bool roundARecovered = RoundA is not null && RoundA.AllFramesMatched && RoundA.AdjacentFramesDiffer;
        bool roundBRecovered = RoundB is not null && RoundB.AllFramesMatched && RoundB.AdjacentFramesDiffer;
        string verdict = roundBRecovered && !roundARecovered
            ? "RECREATE_HANDLE_SAFE"
            : roundBRecovered
                ? "INCONCLUSIVE_BOTH_ARMS_RECOVERED"
                : "RECREATE_HANDLE_UNSAFE";

        Color? gpuColor = _gpuPhases.Count > 0 ? _gpuPhases[^1].ObservedDesktopCenter : null;
        bool roundAFrozenOnGpuFrame = false;
        if (gpuColor is not null && RoundA is not null && RoundA.Frames.Count > 0)
        {
            roundAFrozenOnGpuFrame = DesktopObservation.ChannelsMatch(RoundA.Frames[0].Observed, gpuColor.Value);
        }

        string path = Path.Combine(_output, $"t01-{scenarioId}.json");

        // ★ 证据文件里的 exitCode 必须与**进程真实退出码**一致 ——
        //   否则"文件说成功、进程说失败"本身就是一种假绿。
        int finalExitCode = exitCode;
        if (sourceHashVerified && !unchanged && finalExitCode == 0)
        {
            finalExitCode = 2;
        }

        if (!scenePumpOk && finalExitCode == 0)
        {
            finalExitCode = ScenePumpFailureExitCode;
        }

        // ★ LM-088：比对没做 ⇒ 不能算"通过"（NOT_VERIFIED 必须有非零退出码）。
        if (!sourceHashVerified && finalExitCode == 0)
        {
            finalExitCode = SourceHashNotVerifiedExitCode;
        }

        try
        {
            File.WriteAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        task = "GPU-T01-recreate-handle",
                        scenario = scenarioId,
                        exitCode = finalExitCode,
                        failure,
                        finishedAt = DateTimeOffset.Now,
                        sourceHashChanged = sourceHashChanged,
                        sourceHashVerified = sourceHashVerified,
                        sourceHashStatus = sourceHashStatus,
                        candidateHashesAtEnd = endHashes,
                        scenePump = new
                        {
                            sent = scenePumpSent,
                            failed = scenePumpFailed,
                            required = scenePumpRequired,
                            ok = scenePumpOk,
                            problem = scenePumpProblem,
                            lastError = _scenePump.LastFailure
                        },
                        verdict = new
                        {
                            result = verdict,
                            roundA_gdiRecovered = roundARecovered,
                            roundB_gdiRecovered = roundBRecovered,
                            roundA_allFramesMatched = RoundA?.AllFramesMatched,
                            roundA_adjacentFramesDiffer = RoundA?.AdjacentFramesDiffer,
                            roundB_allFramesMatched = RoundB?.AllFramesMatched,
                            roundB_adjacentFramesDiffer = RoundB?.AdjacentFramesDiffer,
                            roundA_frozenOnGpuFrame = roundAFrozenOnGpuFrame,
                            gpuDesktopCenterColor = gpuColor?.ToArgb().ToString("X8"),
                            roundA_firstObserved = RoundA is null || RoundA.Frames.Count == 0
                                ? null
                                : RoundA.Frames[0].Observed.ToArgb().ToString("X8"),
                            recreatePassed = Recreate?.Passed,
                            scenePumpOk = scenePumpOk,
                            sourceHashVerified = sourceHashVerified,
                            sourceHashStatus = sourceHashStatus,
                            pixelCriterion = new
                            {
                                tolerancePerChannel = DesktopObservation.ChannelTolerance,
                                blockSize = DesktopObservation.BlockSize,
                                requiredMatchRatio = DesktopObservation.RequiredMatchRatio,
                                samplePoint = "centre of the client area, 32x32 desktop pixels"
                            }
                        },
                        windowAdapter = WindowAdapter,
                        positionProbe = PositionProbeResult,
                        handleEvents = _handleEvents,
                        recreate = Recreate,
                        windowStateBeforeRecreate = StateBeforeRecreate,
                        windowStateAfterRecreate = StateAfterRecreate,
                        baseline = Baseline,
                        roundA = RoundA,
                        roundB = RoundB,
                        gpuPhases = _gpuPhases,
                        extraObservations = ExtraObservations,
                        steps = _steps
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            // The evidence must survive a serialization problem: fall back to a
            // minimal record instead of losing the whole run.
            File.WriteAllText(
                path + ".fallback.json",
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        task = "GPU-T01-recreate-handle",
                        scenario = scenarioId,
                        exitCode = finalExitCode,
                        failure,
                        serializationError = exception.Message,
                        verdict = verdict,
                        roundA_gdiRecovered = roundARecovered,
                        roundB_gdiRecovered = roundBRecovered,
                        recreatePassed = Recreate?.Passed,
                        sourceHashChanged = sourceHashChanged,
                        sourceHashVerified = sourceHashVerified,
                        sourceHashStatus = sourceHashStatus,
                        scenePumpOk = scenePumpOk,
                        scenePumpSent,
                        scenePumpFailed
                    },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return finalExitCode == 0 ? 1 : finalExitCode;
        }

        return finalExitCode;
    }
}
