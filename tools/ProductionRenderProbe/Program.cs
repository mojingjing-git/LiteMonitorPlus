using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Scene;

namespace ProductionRenderProbe;

internal enum ProbeMode
{
    Glass,
    Cyber,
    LiveGlass,
    LegacyGlass
}

internal sealed record ProbeOptions(
    ProbeMode Mode,
    string OutputDirectory,
    bool ExcludeFromCapture,
    int ExitAfterMilliseconds,
    int EvidenceAtMilliseconds,
    bool ResizeSequence,
    int? PhysicalWidth,
    int? PhysicalHeight,
    int PerformanceStartMilliseconds,
    bool TopMost,
    bool ForegroundActivation,
    int ScreenIndex);

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ProbeOptions options = Parse(args);
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using ProbeForm form = new(options);
            Application.Run(form);
            return form.ExitCode;
        }
        catch (Exception exception)
        {
            string? evidenceOutput = args.Length > 1
                ? Path.GetFullPath(args[1])
                : null;
            if (!string.IsNullOrWhiteSpace(evidenceOutput))
            {
                Directory.CreateDirectory(evidenceOutput);
                File.WriteAllText(
                    Path.Combine(evidenceOutput, "failure.log"),
                    exception.ToString());
            }
            string path = Path.Combine(
                Path.GetTempPath(),
                $"LiteMonitor-production-render-probe-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, exception.ToString());
            if (!args.Any(argument =>
                string.Equals(
                    argument,
                    "--no-error-dialog",
                    StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    exception.ToString(),
                    "Production render probe failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static ProbeOptions Parse(string[] args)
    {
        string modeText = args.Length > 0 ? args[0] : "glass";
        ProbeMode mode = modeText.ToLowerInvariant() switch
        {
            "glass" => ProbeMode.Glass,
            "cyber" => ProbeMode.Cyber,
            "live-glass" => ProbeMode.LiveGlass,
            "legacy-glass" => ProbeMode.LegacyGlass,
            _ => throw new ArgumentException(
                "Mode must be glass, cyber, live-glass or legacy-glass.")
        };
        string output = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(AppContext.BaseDirectory, "evidence", modeText);
        bool exclude = mode == ProbeMode.LiveGlass;
        int exitAfter = 3500;
        int evidenceAt = 1600;
        bool resizeSequence = false;
        int? physicalWidth = null;
        int? physicalHeight = null;
        int performanceStart = 0;
        bool topMost = false;
        bool foregroundActivation = false;
        int screenIndex = 0;
        foreach (string argument in args.Skip(2))
        {
            if (argument.StartsWith("--exclude=", StringComparison.OrdinalIgnoreCase))
            {
                exclude = bool.Parse(argument["--exclude=".Length..]);
            }
            else if (argument.StartsWith("--exit-after=", StringComparison.OrdinalIgnoreCase))
            {
                exitAfter = int.Parse(argument["--exit-after=".Length..]);
            }
            else if (argument.StartsWith("--evidence-at=", StringComparison.OrdinalIgnoreCase))
            {
                evidenceAt = int.Parse(argument["--evidence-at=".Length..]);
            }
            else if (argument.StartsWith("--resize-sequence=", StringComparison.OrdinalIgnoreCase))
            {
                resizeSequence = bool.Parse(
                    argument["--resize-sequence=".Length..]);
            }
            else if (argument.StartsWith("--width=", StringComparison.OrdinalIgnoreCase))
            {
                physicalWidth = int.Parse(argument["--width=".Length..]);
            }
            else if (argument.StartsWith("--height=", StringComparison.OrdinalIgnoreCase))
            {
                physicalHeight = int.Parse(argument["--height=".Length..]);
            }
            else if (argument.StartsWith("--performance-start=", StringComparison.OrdinalIgnoreCase))
            {
                performanceStart = int.Parse(
                    argument["--performance-start=".Length..]);
            }
            else if (string.Equals(
                argument,
                "--topmost",
                StringComparison.OrdinalIgnoreCase))
            {
                topMost = true;
            }
            else if (string.Equals(
                argument,
                "--foreground",
                StringComparison.OrdinalIgnoreCase))
            {
                foregroundActivation = true;
            }
            else if (argument.StartsWith(
                "--screen-index=",
                StringComparison.OrdinalIgnoreCase))
            {
                screenIndex = int.Parse(
                    argument["--screen-index=".Length..]);
            }
        }

        if (exitAfter < 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exitAfter),
                "--exit-after must be at least 500 ms.");
        }

        if (physicalWidth is <= 0 || physicalHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalWidth),
                "--width and --height must be positive when supplied.");
        }

        if (performanceStart < 0 || performanceStart >= exitAfter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(performanceStart),
                "--performance-start must be non-negative and earlier than exit.");
        }

        bool longRun =
            performanceStart > 0 ||
            exitAfter > 15000;
        if (longRun && (topMost || foregroundActivation))
        {
            throw new ArgumentException(
                "Long or performance runs cannot use --topmost or --foreground. " +
                "Those flags are reserved for an explicitly requested short screenshot.");
        }
        if (topMost != foregroundActivation)
        {
            throw new ArgumentException(
                "--topmost and --foreground must be supplied together.");
        }
        if (screenIndex < 0 || screenIndex >= Screen.AllScreens.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(screenIndex),
                "--screen-index must identify an available monitor.");
        }

        evidenceAt = Math.Clamp(evidenceAt, 250, exitAfter - 100);
        return new ProbeOptions(
            mode,
            output,
            exclude,
            exitAfter,
            evidenceAt,
            resizeSequence,
            physicalWidth,
            physicalHeight,
            performanceStart,
            topMost,
            foregroundActivation,
            screenIndex);
    }
}

internal sealed class ProbeForm : Form
{
    private readonly ProbeOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RollingTimingWindow _legacyPresentIntervals = new(2048);
    private readonly RollingTimingWindow _legacyRenderDurations = new(2048);
    private readonly System.Windows.Forms.Timer _timer;
    private GpuPresentationHostController? _host;
    private DemoBackdropForm? _backdrop;
    private Theme? _legacyTheme;
    private List<GroupLayoutInfo>? _legacyGroups;
    private AppearanceRuntime? _appearance;
    private SceneSnapshot? _scene;
    private bool _captured;
    private bool _resizeApplied;
    private bool _performanceReset;
    private Exception? _fatalError;
    private long _legacyLastPaintTimestamp;
    private long _lastVisibilityAuditTimestamp;
    private int _visibilityAuditCount;
    private int _visibleUnoccludedAuditCount;
    private int _occludedAuditCount;
    private int _notVisibleAuditCount;
    private string[] _lastCoveringWindows = [];

    public ProbeForm(ProbeOptions options)
    {
        _options = options;
        Text = $"LiteMonitor Production Probe - {options.Mode}";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Size defaultSize = options.Mode == ProbeMode.Cyber
            ? new Size(760, 760)
            : new Size(520, 720);
        ClientSize = new Size(
            options.PhysicalWidth ?? defaultSize.Width,
            options.PhysicalHeight ?? defaultSize.Height);
        Rectangle screenBounds =
            Screen.AllScreens[options.ScreenIndex].WorkingArea;
        Location = new Point(
            screenBounds.Left + Math.Min(260, Math.Max(0, screenBounds.Width / 8)),
            screenBounds.Top + Math.Min(120, Math.Max(0, screenBounds.Height / 10)));
        BackColor = Color.Black;
        DoubleBuffered = true;
        TopMost = options.TopMost;
        ShowInTaskbar = false;
        Opacity = 0.96;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);

        _timer = new System.Windows.Forms.Timer
        {
            Interval = options.Mode == ProbeMode.LegacyGlass ? 33 : 8
        };
        _timer.Tick += OnTick;
        Shown += OnShown;
        FormClosed += OnFormClosed;
    }

    protected override bool ShowWithoutActivation =>
        !_options.ForegroundActivation;

    public int ExitCode =>
        _fatalError is null && _captured ? 0 : 1;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_options.Mode == ProbeMode.LegacyGlass &&
            _legacyTheme is not null &&
            _legacyGroups is not null &&
            _appearance is not null)
        {
            long paintStarted = Stopwatch.GetTimestamp();
            try
            {
                UIRenderer.Render(
                    e.Graphics,
                    _legacyGroups,
                    _legacyTheme,
                    _appearance);
            }
            finally
            {
                long painted = Stopwatch.GetTimestamp();
                if (_legacyLastPaintTimestamp != 0)
                {
                    _legacyPresentIntervals.Add(
                        (painted - _legacyLastPaintTimestamp) * 1000.0 /
                        Stopwatch.Frequency);
                }

                _legacyRenderDurations.Add(
                    (painted - paintStarted) * 1000.0 /
                    Stopwatch.Frequency);
                _legacyLastPaintTimestamp = painted;
            }

            return;
        }

        // The top-level HWND swap chain owns the client surface in GPU mode.
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    private void OnShown(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_options.OutputDirectory);
            if (_options.Mode == ProbeMode.LegacyGlass)
            {
                BuildLegacyInput();
                _timer.Start();
                Invalidate();
                return;
            }

            MaterialKind material = _options.Mode == ProbeMode.Cyber
                ? MaterialKind.CyberPunk
                : MaterialKind.LiquidGlass;
            _host = new GpuPresentationHostController(
                this,
                material,
                liveBackgroundRequested: _options.Mode == ProbeMode.LiveGlass,
                horizontalLayout: false);
            WindowCapturePolicy policy = _options.ExcludeFromCapture
                ? WindowCapturePolicy.WdaExcludeFromCapture
                : WindowCapturePolicy.None;
            if (!_host.Start(
                    policy,
                    out string? errorCode,
                    out string? errorMessage))
            {
                throw new InvalidOperationException($"{errorCode}: {errorMessage}");
            }

            if (_options.Mode == ProbeMode.LiveGlass)
            {
                _backdrop = new DemoBackdropForm(
                    Location,
                    ClientSize,
                    topMost: _options.TopMost);
                _backdrop.Show();
                if (_options.TopMost)
                {
                    BringToFront();
                }
                else
                {
                    _backdrop.SendToBackWithoutActivation();
                }
            }

            if (_options.ForegroundActivation)
            {
                Activate();
                BringToFront();
            }

            _scene = BuildScene();
            _host.UpdateScene(_scene);
            _timer.Start();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_fatalError is not null)
        {
            return;
        }

        try
        {
            if (!_performanceReset &&
                _clock.ElapsedMilliseconds >=
                    _options.PerformanceStartMilliseconds)
            {
                _performanceReset = true;
                if (_options.Mode == ProbeMode.LegacyGlass)
                {
                    _legacyPresentIntervals.Reset();
                    _legacyRenderDurations.Reset();
                    _legacyLastPaintTimestamp = 0;
                }
                else
                {
                    _host?.ResetPerformanceWindows();
                }
                ResetVisibilityAudit();
            }

            if (_performanceReset &&
                _options.PerformanceStartMilliseconds > 0)
            {
                SamplePerformanceVisibility();
            }

            if (_options.Mode == ProbeMode.LegacyGlass)
            {
                Invalidate();
                Update();
            }
            else
            {
                if (_host is null)
                {
                    return;
                }

                if (_options.ResizeSequence &&
                    !_resizeApplied &&
                    _clock.ElapsedMilliseconds >= 650)
                {
                    _resizeApplied = true;
                    ClientSize = new Size(
                        Math.Max(320, ClientSize.Width + 96),
                        Math.Max(260, ClientSize.Height + 64));
                    _host.NotifySizeChanged();
                    _scene = BuildScene();
                    _host.UpdateScene(_scene);
                    _host.RenderNow();
                }
            }

            if (!_captured &&
                _clock.ElapsedMilliseconds >= _options.EvidenceAtMilliseconds)
            {
                WriteEvidence();
                _captured = true;
            }

            if (_captured && _clock.ElapsedMilliseconds >= _options.ExitAfterMilliseconds)
            {
                Close();
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private SceneSnapshot BuildScene()
    {
        RenderGenerations generations = _host!.Generations;
        SceneSourceMetric Cpu(string id, string label, string value, string unit, double progress) =>
            new(
                id,
                label,
                value,
                unit,
                progress,
                progress >= 0.85
                    ? MetricStatus.Danger
                    : progress >= 0.65
                        ? MetricStatus.Warning
                        : MetricStatus.Normal,
                false,
                default);

        SceneSourceMetric[] cpu =
        [
            Cpu("cpu.load", "CPU LOAD", "62", "%", 0.62),
            Cpu("cpu.temp", "PACKAGE", "71", "C", 0.71)
        ];
        SceneSourceMetric[] gpu =
        [
            Cpu("gpu.load", "GPU LOAD", "78", "%", 0.78),
            Cpu("gpu.temp", "GPU TEMP", "43", "C", 0.43),
            Cpu("gpu.vram", "VRAM", "6.2", "GB", 0.39)
        ];
        SceneSourceMetric[] network =
        [
            Cpu("net.down", "DOWNLOAD", "24.8", "MB/s", 0.31),
            Cpu("net.up", "UPLOAD", "3.4", "MB/s", 0.17)
        ];
        SceneSourceMetric[] disk =
        [
            Cpu("disk.read", "READ", "128", "MB/s", 0.48),
            Cpu("disk.write", "WRITE", "42", "MB/s", 0.22)
        ];
        SceneSourceMetric[] fans =
        [
            Cpu("fan.cpu", "CPU FAN", "1260", "RPM", 0.41),
            Cpu("fan.case", "CASE FAN", "890", "RPM", 0.28),
            new(
                "sensor.missing",
                "AUX SENSOR",
                "--",
                string.Empty,
                null,
                MetricStatus.Missing,
                true,
                default)
        ];

        float width = ClientSize.Width;
        float height = ClientSize.Height;
        bool cyber = _options.Mode == ProbeMode.Cyber;
        SceneSourceGroup[] groups = cyber
            ?
            [
                new("CPU", "PROCESSOR ARRAY // CH-01", new LogicalRect(18, 18, width - 36, height * 0.42f - 26), cpu.Concat(gpu).ToArray()),
                new("IO", "NETWORK / STORAGE BUS", new LogicalRect(18, height * 0.42f + 10, width - 36, height * 0.28f), network.Concat(disk).ToArray()),
                new("THERMAL", "THERMAL CONTROL / 4-GPU BUS", new LogicalRect(18, height * 0.70f + 12, width - 36, height * 0.30f - 30), fans)
            ]
            :
            [
                new("CPU", "SYSTEM PULSE", new LogicalRect(16, 18, width - 32, height * 0.30f), cpu),
                new("GPU", "GPU PACKAGE", new LogicalRect(16, height * 0.30f + 12, width - 32, height * 0.27f), gpu),
                new("NETWORK", "SENSOR FABRIC", new LogicalRect(16, height * 0.57f + 24, width - 32, height * 0.23f), network),
                new("SYSTEM", "SYSTEM NOMINAL", new LogicalRect(16, height * 0.80f + 36, width - 32, height * 0.20f - 54), fans)
            ];

        return SceneSnapshotBuilder.Build(
            generations,
            ClientSize.Width,
            ClientSize.Height,
            DeviceDpi,
            groups,
            Array.Empty<SceneNotice>());
    }

    private void BuildLegacyInput()
    {
        _legacyTheme = ThemeManager.Load("LiquidGlass");
        float dpiScale = DeviceDpi / 96f;
        _legacyTheme.Scale(dpiScale, 1f);
        _legacyTheme.Layout.Width = ClientSize.Width;
        _appearance = new AppearanceRuntime();
        _appearance.OnThemeChanged(
            _legacyTheme,
            dpiScale,
            1f,
            ClientRectangle);
        _legacyGroups = BuildLegacyGroups();
        int height = new UILayout(_legacyTheme).Build(_legacyGroups);
        if (!_options.PhysicalHeight.HasValue &&
            ClientSize.Height != height)
        {
            ClientSize = new Size(ClientSize.Width, height);
            _appearance.OnSizeChanged();
            _appearance.OnThemeChanged(
                _legacyTheme,
                dpiScale,
                1f,
                ClientRectangle);
            height = new UILayout(_legacyTheme).Build(_legacyGroups);
        }

        _legacySceneHeight = height;
    }

    private int _legacySceneHeight;

    private List<GroupLayoutInfo> BuildLegacyGroups()
    {
        MetricItem Metric(
            string key,
            string label,
            string value,
            float numericValue,
            MetricRenderStyle style = MetricRenderStyle.StandardBar)
        {
            MetricItem item = new()
            {
                Key = key,
                Label = label,
                Value = numericValue,
                DisplayValue = numericValue,
                Style = style
            };
            _ = item.GetFormattedText(false);
            return item;
        }

        return
        [
            new("CPU", [
                Metric("CPU.Load", "CPU LOAD", "62%", 62),
                Metric("CPU.Temp", "PACKAGE", "71C", 71)
            ])
            {
                Label = "SYSTEM PULSE"
            },
            new("GPU", [
                Metric("GPU.Load", "GPU LOAD", "78%", 78),
                Metric("GPU.Temp", "GPU TEMP", "43C", 43),
                Metric("GPU.Vram", "VRAM", "6.2GB", 6200)
            ])
            {
                Label = "GPU PACKAGE"
            },
            new("NET", [
                Metric("NET.Down", "DOWNLOAD", "24.8MB/s", 24.8f,
                    MetricRenderStyle.TwoColumn),
                Metric("NET.Up", "UPLOAD", "3.4MB/s", 3.4f,
                    MetricRenderStyle.TwoColumn)
            ])
            {
                Label = "SENSOR FABRIC"
            },
            new("SYS", [
                Metric("FAN.Cpu", "CPU FAN", "1260RPM", 1260),
                Metric("FAN.Case", "CASE FAN", "890RPM", 890),
                Metric("DASH.Sensor", "AUX SENSOR", "--", 0)
            ])
            {
                Label = "SYSTEM NOMINAL"
            }
        ];
    }

    private void WriteEvidence()
    {
        string stem = _options.Mode.ToString().ToLowerInvariant();
        string scenePath = Path.Combine(_options.OutputDirectory, $"{stem}-scene.png");
        if (_options.Mode == ProbeMode.LegacyGlass)
        {
            using Bitmap bitmap = new(ClientSize.Width, ClientSize.Height);
            using Graphics graphics = Graphics.FromImage(bitmap);
            if (_legacyTheme is not null &&
                _legacyGroups is not null &&
                _appearance is not null)
            {
                UIRenderer.Render(
                    graphics,
                    _legacyGroups,
                    _legacyTheme,
                    _appearance);
            }

            bitmap.Save(scenePath, ImageFormat.Png);
            File.WriteAllText(
                Path.Combine(
                    _options.OutputDirectory,
                    $"{stem}.diagnostics.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        mode = _options.Mode.ToString(),
                        clientSize = new
                        {
                            ClientSize.Width,
                            ClientSize.Height,
                            DeviceDpi
                        },
                        screen = new
                        {
                            index = _options.ScreenIndex,
                            deviceName = Screen.AllScreens[_options.ScreenIndex].DeviceName,
                            bounds = Screen.AllScreens[_options.ScreenIndex].Bounds,
                            workingArea = Screen.AllScreens[_options.ScreenIndex].WorkingArea
                        },
                        legacyLayoutHeight = _legacySceneHeight,
                        metricCount = _legacyGroups?.Sum(
                            group => group.Items.Count) ?? 0,
                        presentIntervals =
                            _legacyPresentIntervals.Snapshot(),
                        renderDurations =
                            _legacyRenderDurations.Snapshot(),
                        windowPolicy = new
                        {
                            topMost = _options.TopMost,
                            foregroundActivation =
                                _options.ForegroundActivation
                        },
                        visibilityAudit = BuildVisibilityAudit(),
                        elapsedMilliseconds = _clock.ElapsedMilliseconds,
                        evidenceBounds = "fixed procedural input",
                        capturedAt = DateTimeOffset.Now
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        if (_host is null)
        {
            return;
        }

        bool visibilityCurrentlyValid =
            WindowOcclusionProbe.Sample(Handle) is
            { Visible: true, Occluded: false };
        bool liveCaptureEvidenceAllowed =
            _options.PerformanceStartMilliseconds > 0
                ? _visibilityAuditCount > 0 &&
                    _visibleUnoccludedAuditCount == _visibilityAuditCount &&
                    visibilityCurrentlyValid
                : _options.TopMost &&
                    _options.ForegroundActivation;
        bool sceneCaptureWritten =
            _options.Mode != ProbeMode.LiveGlass ||
            liveCaptureEvidenceAllowed;
        if (sceneCaptureWritten)
        {
            _host.CaptureScenePng(scenePath);
        }
        bool externalCaptureWritten = false;
        if (_options.Mode == ProbeMode.LiveGlass && _options.ExcludeFromCapture)
        {
            if (liveCaptureEvidenceAllowed)
            {
                CaptureExternal(
                    Path.Combine(
                        _options.OutputDirectory,
                        $"{stem}-external.png"));
                externalCaptureWritten = true;
            }
        }

        GpuSceneRendererDiagnostics? diagnostics = _host.Diagnostics();
        PresentationDiagnostics? presentation =
            _host.PresentationDiagnostics();
        File.WriteAllText(
            Path.Combine(_options.OutputDirectory, $"{stem}.diagnostics.json"),
            JsonSerializer.Serialize(
                new
                {
                    mode = _options.Mode.ToString(),
                    liveCaptureRequested = _options.Mode == ProbeMode.LiveGlass,
                    captureSessionsCreated = _host.CaptureSessionsCreated,
                    renderer = diagnostics,
                    presentation,
                    clientSize = new { ClientSize.Width, ClientSize.Height, DeviceDpi },
                    screen = new
                    {
                        index = _options.ScreenIndex,
                        deviceName = Screen.AllScreens[_options.ScreenIndex].DeviceName,
                        bounds = Screen.AllScreens[_options.ScreenIndex].Bounds,
                        workingArea = Screen.AllScreens[_options.ScreenIndex].WorkingArea
                    },
                    resizeRequested = _options.ResizeSequence,
                    resizeApplied = _resizeApplied,
                    generations = _host.Generations,
                    windowPolicy = new
                    {
                        topMost = _options.TopMost,
                        foregroundActivation = _options.ForegroundActivation
                    },
                    visibilityAudit = BuildVisibilityAudit(),
                    externalCapture = new
                    {
                        sceneWritten = sceneCaptureWritten,
                        written = externalCaptureWritten,
                        skippedForPrivacyOrInvalidVisibility =
                            _options.Mode == ProbeMode.LiveGlass &&
                            (!sceneCaptureWritten || !externalCaptureWritten)
                    },
                    elapsedMilliseconds = _clock.ElapsedMilliseconds,
                    shaderSha256 = new
                    {
                        vertex = Sha256(Path.Combine(
                            AppContext.BaseDirectory,
                            "resources",
                            "shaders",
                            "SceneMaterialVS.cso")),
                        pixel = Sha256(Path.Combine(
                            AppContext.BaseDirectory,
                            "resources",
                            "shaders",
                            "SceneMaterialPS.cso"))
                    },
                    capturedAt = DateTimeOffset.Now,
                    evidenceBounds = _options.Mode == ProbeMode.LiveGlass
                        ? "dedicated synthetic backdrop only"
                        : "fixed procedural input"
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ResetVisibilityAudit()
    {
        _lastVisibilityAuditTimestamp = 0;
        _visibilityAuditCount = 0;
        _visibleUnoccludedAuditCount = 0;
        _occludedAuditCount = 0;
        _notVisibleAuditCount = 0;
        _lastCoveringWindows = [];
    }

    private void SamplePerformanceVisibility()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastVisibilityAuditTimestamp != 0 &&
            now - _lastVisibilityAuditTimestamp <
                Stopwatch.Frequency / 4)
        {
            return;
        }

        _lastVisibilityAuditTimestamp = now;
        WindowOcclusionResult result =
            WindowOcclusionProbe.Sample(Handle);
        _visibilityAuditCount++;
        if (!result.Visible)
        {
            _notVisibleAuditCount++;
        }
        else if (result.Occluded)
        {
            _occludedAuditCount++;
        }
        else
        {
            _visibleUnoccludedAuditCount++;
        }

        _lastCoveringWindows = result.CoveringWindows;
    }

    private object BuildVisibilityAudit() => new
    {
        performanceMode =
            _options.PerformanceStartMilliseconds > 0,
        sampleCount = _visibilityAuditCount,
        visibleUnoccludedSampleCount =
            _visibleUnoccludedAuditCount,
        occludedSampleCount = _occludedAuditCount,
        notVisibleSampleCount = _notVisibleAuditCount,
        visibleAndUnoccludedThroughout =
            _visibilityAuditCount > 0 &&
            _visibleUnoccludedAuditCount == _visibilityAuditCount,
        lastCoveringWindows = _lastCoveringWindows
    };

    private void CaptureExternal(string path)
    {
        using Bitmap bitmap = new(ClientSize.Width, ClientSize.Height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(PointToScreen(Point.Empty), Point.Empty, ClientSize);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(stream));
    }

    private void Fail(Exception exception)
    {
        if (_fatalError is not null)
        {
            return;
        }

        _fatalError = exception;
        _timer.Stop();
        Directory.CreateDirectory(_options.OutputDirectory);
        File.WriteAllText(
            Path.Combine(_options.OutputDirectory, "failure.log"),
            exception.ToString());
        Close();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _timer.Stop();
        _host?.Dispose();
        _host = null;
        _backdrop?.Close();
        _backdrop = null;
        _appearance?.Dispose();
        _appearance = null;
        _legacyTheme?.DisposeFonts();
        _legacyTheme = null;
    }
}

internal sealed record WindowOcclusionResult(
    bool Visible,
    bool Occluded,
    string[] CoveringWindows);

internal static class WindowOcclusionProbe
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const int DwmwaCloaked = 14;

    public static WindowOcclusionResult Sample(IntPtr target)
    {
        if (target == IntPtr.Zero ||
            !IsWindowVisible(target) ||
            IsIconic(target) ||
            !GetWindowRect(target, out NativeRect targetBounds))
        {
            return new WindowOcclusionResult(
                Visible: false,
                Occluded: false,
                CoveringWindows: []);
        }

        GetWindowThreadProcessId(target, out uint targetProcessId);
        List<string> coveringWindows = [];
        bool targetReached = false;
        EnumWindows(
            (candidate, _) =>
            {
                if (candidate == target)
                {
                    targetReached = true;
                    return false;
                }

                if (!IsWindowVisible(candidate) ||
                    IsIconic(candidate) ||
                    IsCloaked(candidate) ||
                    GetWindowThreadProcessId(
                        candidate,
                        out uint candidateProcessId) == 0 ||
                    candidateProcessId == targetProcessId ||
                    (GetWindowLongPtr(candidate, GwlExStyle).ToInt64() &
                        WsExToolWindow) != 0)
                {
                    return true;
                }

                string className = GetClassName(candidate);
                if (className is "Progman" or "WorkerW" or
                    "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                {
                    return true;
                }

                if (!GetWindowRect(candidate, out NativeRect bounds) ||
                    !bounds.Intersects(targetBounds))
                {
                    return true;
                }

                coveringWindows.Add(
                    $"0x{candidate.ToInt64():X} {className}".TrimEnd());
                return true;
            },
            IntPtr.Zero);

        return new WindowOcclusionResult(
            Visible: true,
            Occluded: !targetReached || coveringWindows.Count > 0,
            CoveringWindows: coveringWindows.Take(8).ToArray());
    }

    private static bool IsCloaked(IntPtr window)
    {
        int cloaked = 0;
        return DwmGetWindowAttribute(
                window,
                DwmwaCloaked,
                out cloaked,
                sizeof(int)) == 0 &&
            cloaked != 0;
    }

    private static string GetClassName(IntPtr window)
    {
        char[] buffer = new char[256];
        int copied = GetClassName(window, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));

    private delegate bool EnumWindowsCallback(
        IntPtr window,
        IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public bool Intersects(NativeRect other) =>
            Left < other.Right &&
            Right > other.Left &&
            Top < other.Bottom &&
            Bottom > other.Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out NativeRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(
        IntPtr window,
        int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(
        IntPtr window,
        int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        char[] className,
        int maximumCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out int value,
        int valueSize);
}

internal sealed class DemoBackdropForm : Form
{
    private readonly Rectangle _targetBounds;
    private readonly Rectangle _probeBounds;
    private readonly System.Windows.Forms.Timer _timer;

    public DemoBackdropForm(
        Point targetLocation,
        Size targetSize,
        bool topMost)
    {
        Rectangle screenBounds =
            Screen.FromPoint(targetLocation).Bounds;
        _targetBounds = screenBounds;
        _probeBounds = new Rectangle(
            targetLocation.X - screenBounds.X,
            targetLocation.Y - screenBounds.Y,
            targetSize.Width,
            targetSize.Height);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _targetBounds;
        BackColor = Color.FromArgb(8, 28, 34);
        DoubleBuffered = true;
        TopMost = topMost;
        ShowInTaskbar = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => Invalidate();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    public void SendToBackWithoutActivation()
    {
        const uint noSize = 0x0001;
        const uint noMove = 0x0002;
        const uint noActivate = 0x0010;
        SetWindowPos(
            Handle,
            new IntPtr(1),
            0,
            0,
            0,
            0,
            noSize | noMove | noActivate);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using System.Drawing.Drawing2D.LinearGradientBrush background = new(
            ClientRectangle,
            Color.FromArgb(8, 76, 88),
            Color.FromArgb(5, 30, 34),
            30);
        graphics.FillRectangle(background, ClientRectangle);

        float phase = (float)(DateTime.Now.TimeOfDay.TotalSeconds % 8) / 8;
        int movingX = _probeBounds.Left +
            (int)(_probeBounds.Width * (-0.02 + phase * 0.56));
        using SolidBrush cyan = new(Color.FromArgb(78, 30, 196, 212));
        using SolidBrush amber = new(Color.FromArgb(54, 230, 142, 48));
        graphics.FillRectangle(
            cyan,
            movingX,
            _probeBounds.Top + 60,
            Math.Max(160, _probeBounds.Width / 2),
            Math.Max(140, _probeBounds.Height / 4));
        graphics.FillRectangle(
            amber,
            _probeBounds.Left + _probeBounds.Width / 3,
            _probeBounds.Top + _probeBounds.Height / 2,
            _probeBounds.Width / 2,
            _probeBounds.Height / 3);
        using Pen grid = new(Color.FromArgb(34, 168, 220, 226), 1);
        for (int x = -ClientSize.Height; x < ClientSize.Width; x += 54)
        {
            graphics.DrawLine(grid, x, 0, x + ClientSize.Height, ClientSize.Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
