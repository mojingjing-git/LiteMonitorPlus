using System.Text.Json;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Scene;

namespace GpuHostLifecycleProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(AppContext.BaseDirectory, "evidence");
        Directory.CreateDirectory(output);
        ApplicationConfiguration.Initialize();
        using ProbeForm form = new(output);
        Application.Run(form);
        return form.ExitCode;
    }
}

internal sealed class ProbeForm : Form
{
    private readonly string _output;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<object> _steps = new();
    private GpuPresentationHostController? _host;
    private int _phase;
    private int _phaseElapsed;
    private bool _initialActive;
    private bool _recoveryAttempted;
    private bool _secondRecoveryRejected;
    private bool _finalResourcesZero;

    public int ExitCode { get; private set; }

    public ProbeForm(string output)
    {
        _output = output;
        Text = "LiteMonitor GPU host lifecycle probe";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 720);
        Location = new Point(260, 120);
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);
        _timer = new System.Windows.Forms.Timer { Interval = 100 };
        _timer.Tick += OnTick;
        Shown += OnShown;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExNoActivate = 0x08000000;
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The top-level swap chain owns this client surface.
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    private void OnShown(object? sender, EventArgs e)
    {
        try
        {
            _host = new GpuPresentationHostController(
                this,
                MaterialKind.LiquidGlass,
                liveBackgroundRequested: false);
            if (!_host.Start(
                    WindowCapturePolicy.None,
                    out string? errorCode,
                    out string? errorMessage))
            {
                throw new InvalidOperationException(
                    $"{errorCode}: {errorMessage}");
            }

            _host.UpdateScene(BuildScene(_host.Generations));
            _host.RenderNow();
            _steps.Add(ReadState("initial", _host));
            _initialActive = _host.IsActive;
            _host.CaptureScenePng(
                Path.Combine(_output, "lifecycle-initial.png"));
            _timer.Start();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _phaseElapsed += _timer.Interval;
        try
        {
            switch (_phase)
            {
                case 0 when _phaseElapsed >= 500:
                    _host!.Suspend();
                    _steps.Add(ReadState("suspended", _host));
                    _host.Resume();
                    _host.UpdateScene(BuildScene(_host.Generations));
                    _host.RenderNow();
                    MoveToNextPhase();
                    break;

                case 1 when _phaseElapsed >= 500:
                    _steps.Add(ReadState("resumed", _host!));
                    _host!.OnRenderFailure(
                        new InvalidOperationException(
                            "Injected first render failure."));
                    _steps.Add(ReadState("first-failure", _host));
                    bool recovered = _host.Start(
                        WindowCapturePolicy.None,
                        out string? recoveryCode,
                        out string? recoveryMessage);
                    _steps.Add(new
                    {
                        step = "first-recovery-start",
                        accepted = recovered,
                        errorCode = recoveryCode,
                        errorMessage = recoveryMessage,
                        state = ReadState("first-recovery-started", _host)
                    });
                    if (!recovered)
                    {
                        throw new InvalidOperationException(
                            "The first automatic GPU recovery failed.");
                    }

                    _host.UpdateScene(BuildScene(_host.Generations));
                    _host.RenderNow();
                    MoveToNextPhase();
                    break;

                case 2 when _phaseElapsed >= 500:
                    _steps.Add(ReadState("first-recovery-running", _host!));
                    _recoveryAttempted = _host!.RecoveryAttempted;
                    if (!_host!.IsActive || !_host.RecoveryAttempted)
                    {
                        throw new InvalidOperationException(
                            "The first GPU recovery did not reach Running.");
                    }

                    _host.CaptureScenePng(
                        Path.Combine(_output, "lifecycle-recovered.png"));
                    _host.OnRenderFailure(
                        new InvalidOperationException(
                            "Injected second render failure."));
                    _steps.Add(ReadState("second-failure", _host));
                    bool secondRecovery = _host.Start(
                        WindowCapturePolicy.None,
                        out string? secondCode,
                        out string? secondMessage);
                    _steps.Add(new
                    {
                        step = "second-recovery-start",
                        accepted = secondRecovery,
                        errorCode = secondCode,
                        errorMessage = secondMessage,
                        state = ReadState("second-recovery-rejected", _host)
                    });
                    if (secondRecovery ||
                        secondCode != "gpu_presentation_recovery_exhausted")
                    {
                        throw new InvalidOperationException(
                            "The second automatic GPU recovery was not rejected.");
                    }

                    _secondRecoveryRejected = true;
                    _host.Dispose();
                    _steps.Add(ReadState("disposed", _host));
                    _finalResourcesZero =
                        _host.ResourceSnapshot.ActiveResourceCount == 0 &&
                        _host.ResourceSnapshot.ActiveBytes == 0;
                    ExitCode = 0;
                    WriteEvidence();
                    _timer.Stop();
                    Close();
                    break;
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void MoveToNextPhase()
    {
        _phase++;
        _phaseElapsed = 0;
    }

    private SceneSnapshot BuildScene(RenderGenerations generations)
    {
        float width = ClientSize.Width;
        float height = ClientSize.Height;
        return SceneSnapshotBuilder.Build(
            generations,
            ClientSize.Width,
            ClientSize.Height,
            DeviceDpi,
            [
                new SceneSourceGroup(
                    "CPU",
                    "SYSTEM PULSE",
                    new LogicalRect(16, 18, width - 32, height * 0.46f),
                    [
                        Metric(
                            "cpu.load",
                            "CPU LOAD",
                            "62",
                            "%",
                            0.62,
                            MetricStatus.Normal),
                        Metric(
                            "cpu.temp",
                            "PACKAGE",
                            "71",
                            "C",
                            0.71,
                            MetricStatus.Warning)
                    ]),
                new SceneSourceGroup(
                    "GPU",
                    "GPU PACKAGE",
                    new LogicalRect(16, height * 0.50f, width - 32, height * 0.42f),
                    [
                        Metric(
                            "gpu.load",
                            "GPU LOAD",
                            "78",
                            "%",
                            0.78,
                            MetricStatus.Warning),
                        Metric(
                            "gpu.temp",
                            "GPU TEMP",
                            "43",
                            "C",
                            0.43,
                            MetricStatus.Normal),
                        Metric(
                            "gpu.vram",
                            "VRAM",
                            "6.2",
                            "GB",
                            0.39,
                            MetricStatus.Normal)
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
        new(
            id,
            label,
            value,
            unit,
            progress,
            status,
            false,
            default);

    private static object ReadState(
        string step,
        GpuPresentationHostController host) =>
        new
        {
            step,
            host.IsActive,
            host.NeedsRestart,
            host.RecoveryAttempted,
            host.IsLiveCaptureStarted,
            host.CaptureSessionsCreated,
            host.LastErrorCode,
            host.LastErrorMessage,
            renderer = host.Diagnostics(),
            resources = host.ResourceSnapshot
        };

    private void WriteEvidence()
    {
        File.WriteAllText(
            Path.Combine(_output, "lifecycle.diagnostics.json"),
            JsonSerializer.Serialize(
                new
                {
                    task = "R21-gpu-host-lifecycle",
                    exitCode = ExitCode,
                    stepCount = _steps.Count,
                    initialActive = _initialActive,
                    recoveryAttempted = _recoveryAttempted,
                    secondRecoveryRejected = _secondRecoveryRejected,
                    finalResourcesZero = _finalResourcesZero,
                    evidenceBounds = "fixed procedural input only",
                    steps = _steps,
                    capturedAt = DateTimeOffset.Now
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Fail(Exception exception)
    {
        _timer.Stop();
        File.WriteAllText(
            Path.Combine(_output, "failure.log"),
            exception + "\nSteps:\n" +
            JsonSerializer.Serialize(
                _steps,
                new JsonSerializerOptions { WriteIndented = true }));
        _host?.Dispose();
        _host = null;
        ExitCode = 1;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
            _host?.Dispose();
            _host = null;
        }

        base.Dispose(disposing);
    }
}
