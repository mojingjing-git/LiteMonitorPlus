using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Device;
using LiteMonitor.Rendering.Gpu.Effects;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Materials;
using LiteMonitor.Rendering.Scene;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor;

/// <summary>
/// Embedded fixed-input GPU preview. It uses the production material renderer
/// and scene adapter, and intentionally never constructs a capture source.
/// </summary>
public sealed class GpuThemePreviewControl : Control
{
    private readonly Theme _theme;
    private readonly List<GroupLayoutInfo> _groups;
    private readonly MaterialRuntimeSettings _runtimeSettings;
    private readonly GpuResourceLedger _ledger = new(
        byteBudget: 512L * 1024 * 1024,
        resourceBudget: 128);
    private readonly FrameScheduler _scheduler;
    private readonly System.Windows.Forms.Timer _timer;
    private GpuSceneRenderer? _renderer;
    private GpuStartupAttempt? _startupAttempt;
    private SceneSnapshot? _scene;
    private RenderGenerations _generations;
    private bool _disposed;
    private string? _error;

    public GpuThemePreviewControl(
        Theme theme,
        List<GroupLayoutInfo> groups)
    {
        _theme = theme;
        _groups = groups;
        MaterialKind material = theme.Rendering?.Material switch
        {
            ThemeRenderingConfig.LiquidGlassMaterial => MaterialKind.LiquidGlass,
            ThemeRenderingConfig.CyberPunkMaterial => MaterialKind.CyberPunk,
            _ => MaterialKind.Legacy
        };
        _runtimeSettings = MaterialRuntimeSettings.Create(theme.Rendering, material, theme);
        _scheduler = new FrameScheduler(_runtimeSettings.TargetFramesPerSecond);
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);
        _timer = new System.Windows.Forms.Timer { Interval = 8 };
        _timer.Tick += OnTick;
        HandleCreated += (_, _) => StartRenderer();
        Resize += (_, _) => ResizeRenderer();
    }

    public bool IsActive => _renderer is not null && _error is null;

    public string? Error => _error;

    public int CaptureSessionsCreated =>
        _renderer?.CaptureSessionsCreated ?? 0;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_error is not null)
        {
            Visible = false;
            Parent?.Invalidate();
            e.Graphics.Clear(Color.FromArgb(16, 20, 24));
            using SolidBrush brush = new(Color.FromArgb(255, 122, 92));
            e.Graphics.DrawString(
                "GPU preview unavailable: " + _error,
                Font,
                brush,
                new PointF(8, 8));
        }
    }

    private void StartRenderer()
    {
        if (_disposed || _renderer is not null ||
            Width <= 0 || Height <= 0)
        {
            return;
        }

        MaterialKind material = _theme.Rendering?.Material switch
        {
            ThemeRenderingConfig.LiquidGlassMaterial => MaterialKind.LiquidGlass,
            ThemeRenderingConfig.CyberPunkMaterial => MaterialKind.CyberPunk,
            _ => MaterialKind.Legacy
        };
        if (!_theme.Rendering!.IsGpuRequested ||
            _theme.Rendering.Validate().Count > 0 ||
            material == MaterialKind.Legacy)
        {
            _error = "theme_rendering_not_gpu";
            return;
        }

        _generations = new RenderGenerations(1, 1, 1, 1, 1);
        if (!GpuStartupGuard.Shared.TryBeginAttempt(
                out GpuStartupAttempt? startupAttempt,
                out string? guardCode,
                out string? guardMessage))
        {
            _error = $"{guardCode}: {guardMessage}";
            return;
        }

        _startupAttempt = startupAttempt;
        float dpiScale = Math.Max(1, DeviceDpi) / 96f;
        float layoutScale = Math.Max(0.1f, _theme.Layout.LayoutScale);
        if (!GpuSceneRenderer.TryCreate(
                Handle,
                _generations,
                Width,
                Height,
                material,
                _ledger,
                _runtimeSettings,
                DeviceDpi,
                Math.Max(0.1f, layoutScale / dpiScale),
                initializationHook: null,
                out GpuSceneRenderer? renderer,
                out string? errorCode,
                out string? errorMessage))
        {
            _startupAttempt?.Dispose();
            _startupAttempt = null;
            _error = $"{errorCode}: {errorMessage}";
            return;
        }

        _renderer = renderer;
        try
        {
            SetPreviewBackground();
            UpdateScene();
            _timer.Start();
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void ResizeRenderer()
    {
        if (_renderer is null || Width <= 0 || Height <= 0)
        {
            return;
        }

        _generations = _generations with { Size = _generations.Size + 1 };
        try
        {
            _renderer.Resize(_generations, Width, Height);
            SetPreviewBackground();
            UpdateScene();
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    private void UpdateScene()
    {
        if (_renderer is null)
        {
            return;
        }

        UILayout layout = new(_theme);
        layout.Build(_groups);
        _scene = UiSceneSnapshotAdapter.Build(
            _groups,
            _generations,
            ClientRectangle,
            DeviceDpi,
            notices:
            [
                new SceneNotice(
                    "gpu-preview",
                    "GPU PREVIEW / FIXED INPUT / CAPTURE 0",
                    MetricStatus.Normal)
            ]);
    }

    private void SetPreviewBackground()
    {
        if (_renderer is null || _runtimeSettings.GlassStyle != GlassSurfaceStyle.FloatingToolbar) return;
        if (!_renderer.TrySetFixedBackground(ToolbarPreviewFixture.CreateInput(Width, Height),
                out string? code, out string? message))
            throw new InvalidOperationException(code + ": " + message);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_renderer is null || _scene is null || !_scheduler.TryTakeFrame(out FrameTick tick))
        {
            return;
        }

        try
        {
            _renderer.Render(
                _scene,
                _generations,
                RectangleToScreen(ClientRectangle),
                _runtimeSettings.MotionEnabled ? tick.AnimationTime : TimeSpan.Zero,
                captureRequested: false);
            _startupAttempt?.MarkFirstSuccessfulFrame();
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    private void Fail(string message)
    {
        _error = message;
        _timer.Stop();
        _renderer?.Dispose();
        _renderer = null;
        _startupAttempt?.Dispose();
        _startupAttempt = null;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
            _renderer?.Dispose();
            _renderer = null;
            _startupAttempt?.Dispose();
            _startupAttempt = null;
        }

        base.Dispose(disposing);
    }
}
