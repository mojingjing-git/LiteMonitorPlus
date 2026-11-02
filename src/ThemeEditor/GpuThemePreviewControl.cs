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
    private bool _wasRenderEligible;
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

    /// <summary>
    /// 已呈现帧数（★ D-1 可观测量，计划 §5.4 D-1）。
    /// <para>
    /// 旧实现没有任何对外可读的"实际出帧"计数 —— 验收报告只能靠反射读
    /// <c>GpuSceneRenderer._presentedFrames</c> 私有字段才能证明"隐藏后仍在出帧"。
    /// 这里把生产渲染器已有的 <c>PresentedFrameCount</c> 透出，让隐藏停帧可被断言。
    /// </para>
    /// </summary>
    public int PresentedFrameCount => _renderer?.PresentedFrameCount ?? 0;

    /// <summary>
    /// 当前调度器目标帧率（★ D-2 可观测量，计划 §5.4 D-2）。
    /// <para>
    /// 与 <see cref="UpdateTheme"/> 传入的主题 <c>rendering.motion.targetFps</c> 同步；
    /// 旧实现只在构造函数里定死，主题改帧率后调度器仍是旧值（实测 requested=2 / actual=30）。
    /// </para>
    /// </summary>
    public int TargetFramesPerSecond => _scheduler.TargetFramesPerSecond;

    /// <summary>仅供测试区分 GPU renderer 最后成功保留的主题与父控件 GDI 回退主题。</summary>
    internal Theme CommittedThemeForTesting => _theme;

    /// <summary>当前 renderer 实际分配的场景表面尺寸；未激活时为 null。</summary>
    internal Size? RenderSurfaceSize =>
        _renderer is null ? null : new Size(_renderer.Width, _renderer.Height);

    public bool UpdateTheme(
        Theme theme,
        List<GroupLayoutInfo>? groups = null,
        PreviewShape shape = PreviewShape.Vertical,
        IReadOnlyList<Column>? horizontalCols = null)
    {
        if (_disposed || !IsHandleCreated || Width <= 0 || Height <= 0)
        {
            // ★ R6 整改（计划 §5.1 A-3）：失败必须留下原因 ——
            //   旧代码直接 return false 且不写 _error，调用方拿到的只有「false」，
            //   既无法提示用户，也无法区分「控件没就绪」与「主题非法」。
            _error = "preview_not_ready";
            return false;
        }

        if (theme?.Rendering == null || !theme.Rendering.IsGpuRequested)
        {
            _error = "theme_rendering_not_gpu";
            return false;
        }

        MaterialKind newMaterial = ResolveMaterial(theme);
        IReadOnlyList<string> validationErrors = theme.Rendering.Validate();
        if (newMaterial == MaterialKind.Legacy || validationErrors.Count > 0)
        {
            // ★ R6 整改：把校验明细写进 _error，UI 与测试都能看到「为什么没生效」。
            _error = validationErrors.Count > 0
                ? "theme_rendering_invalid: " + string.Join("; ", validationErrors)
                : "theme_material_legacy";
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

        // ★ D-2 整改（计划 §5.4 D-2）：目标帧率必须同步到调度器 ——
        //   旧实现只在构造函数里把 FPS 定死，UpdateTheme 重建 _runtimeSettings 之后
        //   调度器仍按旧值出帧（验收实测 requested=2 / actual=30）。
        //   与生产链（GpuPresentationHostController.UpdateConfiguration）同一写法。
        _scheduler.UpdateTargetFramesPerSecond(_runtimeSettings.TargetFramesPerSecond);

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

            RenderGenerations nextGenerations =
                _generations with { Theme = _generations.Theme + 1 };
            _renderer.UpdateConfiguration(
                newMaterial,
                _runtimeSettings,
                DeviceDpi,
                userScale,
                nextGenerations);
            _generations = nextGenerations;
            SetPreviewBackground();
            UpdateScene(shape, horizontalCols);
            SessionUpdateCount++;

            // ★ R6 整改配套：本次更新成功 ⇒ 清除上一轮遗留的失败原因。
            //   A-3 让失败路径写入 _error 之后，若成功路径不清除，
            //   一次「非法主题」失败就会让预览永久停留在错误态（后续合法主题也不再生效）。
            _error = null;
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
            // 【S06】守卫类失败补充作用域说明：预览守卫独立于主面板守卫，
            // 重试主面板不能修复预览；预览自身的恢复路径是重启工作室进程。
            string note = _error.Contains("gpu_startup_guard", StringComparison.Ordinal)
                ? "\n（预览守卫独立于主面板；重启主题工作室可重置预览守卫。）"
                : string.Empty;
            e.Graphics.DrawString(
                "GPU preview unavailable: " + _error + note,
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

        // R01：同尺寸布局事件（最小化恢复常见）不能让 Size 代次单边递增。
        // GpuSceneRenderer.Resize 对同尺寸直接 return，不同步代次；若这里先递增，
        // 下一次 Render 会因 CanAccept 不等而拒帧，表现为“恢复后黑屏”。
        if (_renderer.Width == Width && _renderer.Height == Height)
        {
            SetPreviewBackground();
            UpdateScene(CurrentShape, _horizontalCols);
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
        // ★ D-1 整改（计划 §5.4 D-1）：隐藏时不再出帧 ——
        //   切到经典外观/兼容外观时 ThemePreviewControl 会把 _gpuPreview.Visible 置 false，
        //   但定时器仍在跑、GPU 仍在出帧（验收实测隐藏期间 framesWhileHidden=6）。
        //   这里按"自身与整条祖先链都可见"判定可见性，隐藏即跳过本次帧。
        if (!IsEffectivelyVisible())
        {
            _wasRenderEligible = false;
            return;
        }

        if (!_wasRenderEligible)
        {
            // 恢复时重新建立帧基准，避免把不可见期间的墙钟时间当成掉帧/追帧。
            _scheduler.Reset();
            _wasRenderEligible = true;
        }

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

    /// <summary>控件自身与整条祖先链都可见才算可见（隐藏的预览不该继续烧 GPU）。</summary>
    private bool IsEffectivelyVisible()
    {
        if (TopLevelControl is Form form && form.WindowState == FormWindowState.Minimized)
        {
            return false;
        }

        for (Control? current = this; current is not null; current = current.Parent)
        {
            if (!current.Visible) return false;
        }

        return true;
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

        if (!EnsureRendererSurfaceMatchesControl())
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
        catch (Exception ex)
        {
            _error = "gpu_capture_failed: " + ex.Message;
            return false;
        }
    }

    private bool EnsureRendererSurfaceMatchesControl()
    {
        if (_renderer is null)
        {
            _error = "gpu_surface_unavailable";
            return false;
        }

        int expectedWidth = Math.Max(1, ClientSize.Width);
        int expectedHeight = Math.Max(1, ClientSize.Height);
        if (_renderer.Width != expectedWidth || _renderer.Height != expectedHeight)
        {
            ResizeRenderer();
        }

        if (_renderer is not null &&
            _renderer.Width == expectedWidth &&
            _renderer.Height == expectedHeight)
        {
            return true;
        }

        int actualWidth = _renderer?.Width ?? 0;
        int actualHeight = _renderer?.Height ?? 0;
        _error = $"gpu_surface_size_mismatch: expected {expectedWidth}x{expectedHeight}, " +
            $"actual {actualWidth}x{actualHeight}.";
        return false;
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
