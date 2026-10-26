using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.Rendering.Contracts;
using LiteMonitor.Rendering.Gpu.Device;
using LiteMonitor.Rendering.Gpu.Effects;
using LiteMonitor.Rendering.Materials;
using LiteMonitor.Rendering.Presentation;
using LiteMonitor.Rendering.Scene;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor;

/// <summary>预览形态。</summary>
public enum PreviewShape
{
    Vertical = 0,
    Horizontal = 1,
    Taskbar = 2
}

/// <summary>横向预览数据模拟工厂。</summary>
public static class HorizontalPreviewMock
{
    public static List<MetricItem> BuildMockMetrics() => ThemePreviewScene.CreateMetrics();

    public static List<Column> BuildColumns(
        IReadOnlyList<MetricItem>? metrics = null,
        bool singleLine = false,
        Theme? theme = null,
        int width = 320)
    {
        var cols = ThemePreviewScene.BuildColumns(metrics ?? BuildMockMetrics(), singleLine);
        if (theme != null)
        {
            var settings = new Settings { HorizontalSingleLine = singleLine };
            var layout = new HorizontalLayout(theme, Math.Max(1, width), LayoutMode.Horizontal, settings, null);
            layout.Build(cols);
        }
        return cols;
    }

    public static List<Column> BuildColumns(Theme theme, bool singleLine = false, int width = 320) =>
        BuildColumns(BuildMockMetrics(), singleLine, theme, width);
}

/// <summary>
/// Embedded fixed-input GPU preview. It uses the production material renderer
/// and scene adapter, and intentionally never constructs a capture source.
/// </summary>
public sealed class GpuThemePreviewControl : Control
{
    /// <summary>
    /// 预览专用的 GPU 启动保护（重做计划 §6.3 / TE00 第 5 节结论）：
    /// 使用独立状态目录，**不读写生产崩溃标记**、不消耗生产保守状态。
    /// 预览异常退出只会让预览自身进入保守模式，不会把主窗体下次启动打入 GDI。
    /// </summary>
    private static readonly Lazy<GpuStartupGuard> PreviewGuardFactory = new(() =>
        new GpuStartupGuard(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiteMonitor++",
                "gpu-startup-preview"),
            AppContext.BaseDirectory));

    public static GpuStartupGuard PreviewGuard => PreviewGuardFactory.Value;

    private readonly GpuStartupGuard _startupGuard;
    private Theme _theme;
    private List<GroupLayoutInfo> _groups;
    private IReadOnlyList<Column>? _horizontalCols;
    private MaterialRuntimeSettings _runtimeSettings;
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
    private bool _hasStartedOnce;
    private string? _error;

    public int DeviceCreationCount { get; private set; } = 1;
    public int SessionUpdateCount { get; private set; } = 0;
    public long CurrentGeneration => (long)_generations.Theme;
    public PreviewShape CurrentShape { get; private set; } = PreviewShape.Vertical;

    public GpuThemePreviewControl(
        Theme theme,
        List<GroupLayoutInfo> groups,
        GpuStartupGuard? startupGuard = null,
        PreviewShape shape = PreviewShape.Vertical,
        IReadOnlyList<Column>? horizontalCols = null)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _startupGuard = startupGuard ?? PreviewGuard;
        CurrentShape = shape;
        _horizontalCols = horizontalCols;

        MaterialKind material = ResolveMaterial(theme);
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
        Resize += (_, _) => ResizeRenderer();
    }

    public bool IsActive => _renderer is not null && _error is null;

    public string? Error => _error;

    public int CaptureSessionsCreated =>
        _renderer?.CaptureSessionsCreated ?? 0;

    public bool UpdateTheme(
        Theme theme,
        List<GroupLayoutInfo>? groups = null,
        PreviewShape shape = PreviewShape.Vertical,
        IReadOnlyList<Column>? horizontalCols = null)
    {
        if (_disposed || !IsHandleCreated || Width <= 0 || Height <= 0)
        {
            return false;
        }

        if (theme?.Rendering == null || !theme.Rendering.IsGpuRequested)
        {
            return false;
        }

        MaterialKind newMaterial = ResolveMaterial(theme);
        if (newMaterial == MaterialKind.Legacy || theme.Rendering.Validate().Count > 0)
        {
            return false;
        }

        MaterialKind oldMaterial = ResolveMaterial(_theme);
        bool materialChanged = newMaterial != oldMaterial;

        _theme = theme;
        if (groups != null)
        {
            _groups = groups;
        }
        CurrentShape = shape;
        if (horizontalCols != null)
        {
            _horizontalCols = horizontalCols;
        }

        _runtimeSettings = MaterialRuntimeSettings.Create(theme.Rendering, newMaterial, theme);

        if (materialChanged || _renderer is null)
        {
            if (_hasStartedOnce)
            {
                DeviceCreationCount++;
            }
            _hasStartedOnce = true;
            _renderer?.Dispose();
            _renderer = null;
            _startupAttempt?.Dispose();
            _startupAttempt = null;
            _error = null;
            StartRenderer();
            return _renderer != null && _error == null;
        }

        // 坚决复用既有 D3D 设备与 SwapChain 会话
        try
        {
            float dpiScale = Math.Max(1, DeviceDpi) / 96f;
            float layoutScale = Math.Max(0.1f, _theme.Layout.LayoutScale);
            float userScale = Math.Max(0.1f, layoutScale / dpiScale);

            _renderer.UpdateConfiguration(newMaterial, _runtimeSettings, DeviceDpi, userScale);
            _generations = _generations with { Theme = _generations.Theme + 1 };
            SetPreviewBackground();
            UpdateScene(shape, horizontalCols);
            SessionUpdateCount++;
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return false;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_disposed && Width > 0 && Height > 0)
        {
            if (_hasStartedOnce)
            {
                DeviceCreationCount++;
            }
            _hasStartedOnce = true;
            StartRenderer();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _timer.Stop();
        _renderer?.Dispose();
        _renderer = null;
        base.OnHandleDestroyed(e);
    }

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
            Width <= 0 || Height <= 0 || !IsHandleCreated)
        {
            return;
        }

        MaterialKind material = ResolveMaterial(_theme);
        if (!_theme.Rendering!.IsGpuRequested ||
            _theme.Rendering.Validate().Count > 0 ||
            material == MaterialKind.Legacy)
        {
            _error = "theme_rendering_not_gpu";
            return;
        }

        if (_generations.Theme == 0)
        {
            _generations = new RenderGenerations(1, 1, 1, 1, 1);
        }
        else
        {
            _generations = _generations with { Device = _generations.Device + 1 };
        }

        if (!_startupGuard.TryBeginAttempt(
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
        _error = null;
        try
        {
            SetPreviewBackground();
            UpdateScene(CurrentShape, _horizontalCols);
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
            UpdateScene(CurrentShape, _horizontalCols);
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    private void UpdateScene(PreviewShape shape = PreviewShape.Vertical, IReadOnlyList<Column>? horizontalCols = null)
    {
        if (_renderer is null)
        {
            return;
        }

        CurrentShape = shape;
        if (horizontalCols != null)
        {
            _horizontalCols = horizontalCols;
        }

        if (shape == PreviewShape.Horizontal)
        {
            IReadOnlyList<Column> cols = _horizontalCols ?? horizontalCols ?? HorizontalPreviewMock.BuildColumns(
                metrics: HorizontalPreviewMock.BuildMockMetrics(),
                singleLine: false,
                theme: _theme,
                width: Width > 0 ? Width : 320);

            if (cols.Count > 0 && cols[0].Bounds.Width <= 0)
            {
                var settings = new Settings { HorizontalSingleLine = false };
                var layout = new HorizontalLayout(_theme, Math.Max(1, Width > 0 ? Width : 320), LayoutMode.Horizontal, settings, null);
                layout.Build(cols as List<Column> ?? cols.ToList());
            }

            _scene = UiSceneSnapshotAdapter.BuildHorizontal(
                cols,
                _generations,
                ClientRectangle,
                DeviceDpi,
                [new SceneNotice("gpu-preview", "GPU PREVIEW / HORIZONTAL", MetricStatus.Normal)]);
        }
        else
        {
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
    }

    private void SetPreviewBackground()
    {
        if (_renderer is null || _runtimeSettings.GlassStyle != GlassSurfaceStyle.FloatingToolbar) return;
        if (!_renderer.TrySetFixedBackground(ToolbarPreviewSurface.CreateInput(Width, Height),
                out string? code, out string? message))
            throw new InvalidOperationException(code + ": " + message);
    }

    private static MaterialKind ResolveMaterial(Theme? theme) =>
        theme?.Rendering?.Material switch
        {
            ThemeRenderingConfig.LiquidGlassMaterial => MaterialKind.LiquidGlass,
            ThemeRenderingConfig.CyberPunkMaterial => MaterialKind.CyberPunk,
            _ => MaterialKind.Legacy
        };

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

    /// <summary>
    /// 快照通道（主题市场卡片快照，M4/D5）：同步渲染一帧并从 GPU 帧缓冲回读 PNG。
    /// 与实时预览共用同一渲染器与固定输入（仍不构造抓屏源），运动时间取零 ⇒ 静态帧。
    /// 失败返回 false（调用方走 legacy 示意帧回退）。
    /// </summary>
    public bool TryCaptureOnce(string pngPath)
    {
        if (_disposed || _renderer is null || _error is not null || _scene is null)
        {
            return false;
        }
        try
        {
            _renderer.Render(
                _scene,
                _generations,
                RectangleToScreen(ClientRectangle),
                TimeSpan.Zero,
                captureRequested: false);
            _renderer.CaptureScenePng(pngPath);
            return File.Exists(pngPath);
        }
        catch
        {
            return false;
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
