using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiteMonitor.Rendering.Contracts;

public static class RenderingContractVersion
{
    public const int Core = 1;
    public const int Capture = 1;
    public const string SchemaId = "litemonitor.gpu-render/1";
}

public enum RenderBackendKind
{
    LegacyGdiPlus = 0,
    Gpu = 1
}

public enum MaterialKind
{
    Legacy = 0,
    LiquidGlass = 1,
    CyberPunk = 2
}

public enum RenderLifecycleState
{
    Disabled,
    Probing,
    Initializing,
    Running,
    Suspended,
    Recovering,
    Fallback,
    Stopping
}

public enum CaptureLifecycleState
{
    Disabled,
    Initializing,
    Running,
    AccessLost,
    Stopping,
    Failed
}

public enum MetricStatus
{
    Normal,
    Warning,
    Danger,
    Missing
}

public enum WindowCapturePolicy
{
    None = 0,
    WdaExcludeFromCapture = 1
}

/// <summary>
/// Identifies the immutable source consumed by the material pass. Values are
/// encoded in MaterialFrameConstants.State.X; do not reorder.
/// </summary>
public enum SceneBackgroundMode
{
    Procedural = 0,
    FixedTexture = 1,
    LiveCapture = 2
}

/// <summary>
/// Material geometry programs supported by the frozen 2026-10-22 contract.
/// </summary>
public enum GlassSurfaceStyle
{
    Standard = 0,
    FloatingToolbar = 1
}

public enum MetricRowPresentationStyle
{
    StandardBar,
    TwoColumn,
    TextOnly
}

public readonly record struct RenderGenerations(
    ulong Theme,
    ulong Window,
    ulong Size,
    ulong Topology,
    ulong Device)
{
    public bool CanAccept(RenderGenerations other) =>
        Theme == other.Theme &&
        Window == other.Window &&
        Size == other.Size &&
        Topology == other.Topology &&
        Device == other.Device;
}

public readonly record struct LogicalRect(double X, double Y, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width >= 0 &&
        Height >= 0;
}

public readonly record struct PixelRectI(int X, int Y, int Width, int Height)
{
    public bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        X >= 0 &&
        Y >= 0 &&
        (long)X + Width <= int.MaxValue &&
        (long)Y + Height <= int.MaxValue;
}

/// <summary>
/// Non-premultiplied sRGB color used by the foreground contract. The Direct2D
/// painter converts this to premultiplied BGRA only at the drawing boundary.
/// </summary>
public readonly record struct RgbaColor(float R, float G, float B, float A)
{
    public bool IsValid =>
        float.IsFinite(R) &&
        float.IsFinite(G) &&
        float.IsFinite(B) &&
        float.IsFinite(A) &&
        R is >= 0 and <= 1 &&
        G is >= 0 and <= 1 &&
        B is >= 0 and <= 1 &&
        A is >= 0 and <= 1;
}

/// <summary>
/// Immutable BGRA8 SDR fixed input shared by production fixed replay and the
/// editor. The input owns a private copy; callers cannot mutate accepted pixels.
/// </summary>
public sealed class FixedBackgroundInput
{
    public const string PixelFormat = "B8G8R8A8_UNorm";
    public const string ColorSpace = "SDR-sRGB";
    public const int MaximumDimension = 16384;

    private readonly byte[] _pixels;

    private FixedBackgroundInput(
        byte[] pixels,
        int width,
        int height,
        int rowPitch,
        int dpi,
        PixelRectI opaquePixelBounds,
        string contentVersion)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        RowPitch = rowPitch;
        Dpi = dpi;
        OpaquePixelBounds = opaquePixelBounds;
        ContentVersion = contentVersion;
        PixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
    }

    public ReadOnlyMemory<byte> Bgra8Pixels => _pixels;

    public int Width { get; }

    public int Height { get; }

    public int RowPitch { get; }

    public int Dpi { get; }

    /// <summary>Physical pixel offset of the image origin relative to the client origin.</summary>
    public Point Origin { get; init; }

    public PixelRectI OpaquePixelBounds { get; }

    public string ContentVersion { get; }

    public string PixelSha256 { get; }

    public string PixelFormatName => PixelFormat;

    public string ColorSpaceName => ColorSpace;

    public static bool TryCreate(
        ReadOnlyMemory<byte> bgra8Pixels,
        int width,
        int height,
        int rowPitch,
        int dpi,
        PixelRectI opaquePixelBounds,
        string? contentVersion,
        out FixedBackgroundInput? input,
        out string? errorCode,
        out string? errorMessage)
    {
        input = null;
        if (width <= 0 || width > MaximumDimension ||
            height <= 0 || height > MaximumDimension)
        {
            errorCode = "fixed_background_invalid_dimensions";
            errorMessage =
                $"Fixed background dimensions must be in [1, {MaximumDimension}].";
            return false;
        }

        long minimumRowPitch = (long)width * 4;
        long requiredBytes = (long)rowPitch * height;
        if (rowPitch < minimumRowPitch ||
            requiredBytes > int.MaxValue ||
            bgra8Pixels.Length < requiredBytes)
        {
            errorCode = "fixed_background_invalid_stride";
            errorMessage =
                "Fixed background requires a BGRA8 row pitch of at least width*4 and a complete pixel buffer.";
            return false;
        }

        if (dpi <= 0)
        {
            errorCode = "fixed_background_invalid_dpi";
            errorMessage = "Fixed background DPI must be positive.";
            return false;
        }

        if (!opaquePixelBounds.IsValid ||
            (long)opaquePixelBounds.X + opaquePixelBounds.Width > width ||
            (long)opaquePixelBounds.Y + opaquePixelBounds.Height > height)
        {
            errorCode = "fixed_background_invalid_bounds";
            errorMessage =
                "Opaque pixel bounds must be non-empty and contained by the fixed background.";
            return false;
        }

        string normalizedVersion = contentVersion?.Trim() ?? string.Empty;
        if (normalizedVersion.Length == 0)
        {
            errorCode = "fixed_background_missing_version";
            errorMessage = "Fixed background contentVersion is required.";
            return false;
        }

        byte[] copied = bgra8Pixels.Span[..(int)requiredBytes].ToArray();
        input = new FixedBackgroundInput(
            copied,
            width,
            height,
            rowPitch,
            dpi,
            opaquePixelBounds,
            normalizedVersion);
        errorCode = null;
        errorMessage = null;
        return true;
    }
}

/// <summary>
/// Theme-owned foreground typography and tone contract. Sizes are DIPs and are
/// scaled exactly once by DeviceDpi/96 * userScale in the Direct2D painter.
/// </summary>
public sealed record ForegroundThemeStyle(
    string UiFontFamily,
    string ValueFontFamily,
    float DisplaySizeDip,
    float TitleSizeDip,
    float BodySizeDip,
    float CaptionSizeDip,
    bool BoldText,
    RgbaColor Primary,
    RgbaColor Muted,
    RgbaColor Success,
    RgbaColor Warning,
    RgbaColor Danger,
    RgbaColor Accent,
    RgbaColor Rule,
    RgbaColor Shadow)
{
    public string? DisplayFontFamily { get; init; } = "Segoe UI Variable";
    public string? CaptionFontFamily { get; init; } = "Cascadia Mono";
    public bool UsesThemeTypography { get; init; }
    public RgbaColor? TrackColor { get; init; }
    public RgbaColor? GroupTitleColor { get; init; }
    public RgbaColor? BarSafe { get; init; }
    public RgbaColor? BarWarn { get; init; }
    public RgbaColor? BarCrit { get; init; }
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(UiFontFamily) &&
        !string.IsNullOrWhiteSpace(ValueFontFamily) &&
        float.IsFinite(DisplaySizeDip) &&
        float.IsFinite(TitleSizeDip) &&
        float.IsFinite(BodySizeDip) &&
        float.IsFinite(CaptionSizeDip) &&
        DisplaySizeDip is >= 1 and <= 256 &&
        TitleSizeDip is >= 1 and <= 256 &&
        BodySizeDip is >= 1 and <= 256 &&
        CaptionSizeDip is >= 1 and <= 256 &&
        Primary.IsValid &&
        Muted.IsValid &&
        Success.IsValid &&
        Warning.IsValid &&
        Danger.IsValid &&
        Accent.IsValid &&
        Rule.IsValid &&
        Shadow.IsValid;

    public static ForegroundThemeStyle Default(MaterialKind material) =>
        material == MaterialKind.CyberPunk
            ? new ForegroundThemeStyle(
                "Segoe UI Variable",
                "Cascadia Mono",
                25,
                13.5f,
                10.5f,
                8.5f,
                BoldText: true,
                Primary: new RgbaColor(0.92f, 0.98f, 1.0f, 1.0f),
                Muted: new RgbaColor(0.52f, 0.66f, 0.72f, 0.92f),
                Success: new RgbaColor(0.32f, 0.94f, 0.78f, 1.0f),
                Warning: new RgbaColor(1.0f, 0.66f, 0.22f, 1.0f),
                Danger: new RgbaColor(1.0f, 0.27f, 0.34f, 1.0f),
                Accent: new RgbaColor(0.26f, 0.90f, 0.98f, 1.0f),
                Rule: new RgbaColor(0.31f, 0.58f, 0.66f, 0.50f),
                Shadow: new RgbaColor(0.005f, 0.012f, 0.018f, 0.78f))
            : new ForegroundThemeStyle(
                "Segoe UI Variable",
                "Cascadia Mono",
                25,
                13.5f,
                10.5f,
                8.5f,
                BoldText: true,
                Primary: new RgbaColor(0.96f, 0.985f, 1.0f, 1.0f),
                Muted: new RgbaColor(0.70f, 0.82f, 0.88f, 0.90f),
                Success: new RgbaColor(0.38f, 0.95f, 0.82f, 1.0f),
                Warning: new RgbaColor(1.0f, 0.76f, 0.34f, 1.0f),
                Danger: new RgbaColor(1.0f, 0.38f, 0.43f, 1.0f),
                Accent: new RgbaColor(0.60f, 0.93f, 1.0f, 1.0f),
                Rule: new RgbaColor(0.76f, 0.91f, 0.98f, 0.36f),
                Shadow: new RgbaColor(0.005f, 0.012f, 0.018f, 0.78f));
}

public sealed record BackgroundInputDiagnostics(
    SceneBackgroundMode Mode,
    string SourceId,
    int Width,
    int Height,
    string? ContentVersion,
    string? PixelSha256,
    PixelRectI? OpaquePixelBounds)
{
    public Point PhysicalOrigin { get; init; }
}

/// <summary>
/// Renderer boundary for deterministic fixed replay and editor preview. The
/// implementation must upload only on this explicit call and release its GPU
/// resources on replacement, ClearFixedBackground and Dispose.
/// </summary>
public interface IFixedBackgroundTarget
{
    BackgroundInputDiagnostics CurrentBackgroundInput { get; }

    bool TrySetFixedBackground(
        FixedBackgroundInput input,
        out string? errorCode,
        out string? errorMessage);

    void ClearFixedBackground();
}

public sealed record SceneSnapshot(
    int ContractVersion,
    RenderGenerations Generations,
    double LogicalWidth,
    double LogicalHeight,
    int Dpi,
    ImmutableArray<SceneGroup> Groups,
    ImmutableArray<SceneNotice> Notices)
{
    public bool IsValid =>
        ContractVersion == RenderingContractVersion.Core &&
        double.IsFinite(LogicalWidth) &&
        LogicalWidth >= 0 &&
        double.IsFinite(LogicalHeight) &&
        LogicalHeight >= 0 &&
        Dpi > 0;
}

public sealed record SceneGroup(
    string StableId,
    string Title,
    ImmutableArray<SceneMetric> Metrics,
    LogicalRect Bounds,
    int Order);

public sealed record SceneMetric(
    string StableId,
    string Label,
    string DisplayValue,
    string Unit,
    double? NormalizedValue,
    MetricStatus Status,
    bool IsMissing,
    ImmutableArray<double> Trend)
{
    public MetricRowPresentationStyle PresentationStyle { get; init; } =
        MetricRowPresentationStyle.StandardBar;
}

public sealed record SceneNotice(
    string StableId,
    string Text,
    MetricStatus Severity);

public sealed record GpuOutputDescriptor(
    long AdapterLuid,
    uint Width,
    uint Height,
    string PixelFormat,
    string ColorSpace,
    long PresentationTimestampMicroseconds,
    LogicalRect ContentRectangle);

public interface IBackgroundFrameLease : IDisposable
{
    long LeaseId { get; }

    bool IsPreview { get; }

    RenderGenerations Generations { get; }

    GpuOutputDescriptor Descriptor { get; }
}

public readonly record struct CaptureCapabilities(
    bool Available,
    bool SupportsWindowExclusion,
    bool SupportsTimestamp,
    bool SupportsHdr,
    WindowCapturePolicy ExclusionPolicy,
    string BackendId,
    string? Limitation);

public interface IBackgroundFrameSource : IDisposable
{
    CaptureCapabilities Capabilities { get; }

    CaptureLifecycleState State { get; }

    bool TryAcquireLatest(
        RenderGenerations requestedGenerations,
        int timeoutMilliseconds,
        out IBackgroundFrameLease? lease,
        out CaptureDiagnostic diagnostic);
}

public sealed record CaptureDiagnostic(
    CaptureLifecycleState State,
    string Code,
    string Message,
    double? LastFrameAgeMilliseconds,
    int RecoveryAttempt);

public sealed record RenderDiagnostics(
    RenderBackendKind RequestedBackend,
    RenderBackendKind ActualBackend,
    MaterialKind RequestedMaterial,
    MaterialKind ActualMaterial,
    RenderLifecycleState State,
    CaptureLifecycleState CaptureState,
    long AdapterLuid,
    string AdapterName,
    double? LastGpuPassMilliseconds,
    double? LastPresentIntervalMilliseconds,
    int RecoveryAttempt,
    string? FailureCode);

public sealed record RenderingConfig(
    int SchemaVersion,
    string Backend,
    string Material,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    IReadOnlyDictionary<string, JsonElement> Motion,
    IReadOnlyDictionary<string, JsonElement> Fallback)
{
    public const int CurrentSchemaVersion = 1;

    public static RenderingConfig Legacy { get; } = new(
        CurrentSchemaVersion,
        "legacy",
        "legacy",
        new Dictionary<string, JsonElement>(),
        new Dictionary<string, JsonElement>(),
        new Dictionary<string, JsonElement>());
}
