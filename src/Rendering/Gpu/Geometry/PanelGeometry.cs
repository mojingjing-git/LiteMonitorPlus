using System.Numerics;
using LiteMonitor.Rendering.Contracts;

namespace LiteMonitor.Rendering.Gpu.Geometry;

public enum PanelShapeKind
{
    Rectangle,
    RoundedRectangle,
    ChamferedRectangle
}

public readonly record struct SamplingBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

public readonly record struct GeometryShaderConstants(
    Vector4 Bounds,
    Vector4 Shape,
    Vector4 Edge,
    Vector4 Sampling);

public sealed record PanelGeometry
{
    public PanelGeometry(
        PanelShapeKind shape,
        LogicalRect bounds,
        double cornerRadius,
        double cutLength,
        double outlineWidth,
        double refractionRadius,
        double blurRadius,
        double shadowRadius,
        double glowRadius)
    {
        if (!bounds.IsValid || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Panel bounds must be finite and non-empty.");
        }

        ValidateNonNegative(cornerRadius, nameof(cornerRadius));
        ValidateNonNegative(cutLength, nameof(cutLength));
        ValidateNonNegative(outlineWidth, nameof(outlineWidth));
        ValidateNonNegative(refractionRadius, nameof(refractionRadius));
        ValidateNonNegative(blurRadius, nameof(blurRadius));
        ValidateNonNegative(shadowRadius, nameof(shadowRadius));
        ValidateNonNegative(glowRadius, nameof(glowRadius));

        double maxCorner = Math.Min(bounds.Width, bounds.Height) * 0.5;
        if (cornerRadius > maxCorner)
        {
            throw new ArgumentOutOfRangeException(nameof(cornerRadius), "Corner radius exceeds half of the shortest side.");
        }

        double maxCut = Math.Min(bounds.Width, bounds.Height) * 0.5;
        if (cutLength > maxCut)
        {
            throw new ArgumentOutOfRangeException(nameof(cutLength), "Cut length exceeds half of the shortest side.");
        }

        Shape = shape;
        Bounds = bounds;
        CornerRadius = cornerRadius;
        CutLength = cutLength;
        OutlineWidth = outlineWidth;
        RefractionRadius = refractionRadius;
        BlurRadius = blurRadius;
        ShadowRadius = shadowRadius;
        GlowRadius = glowRadius;
    }

    public PanelShapeKind Shape { get; }

    public LogicalRect Bounds { get; }

    public double CornerRadius { get; }

    public double CutLength { get; }

    public double OutlineWidth { get; }

    public double RefractionRadius { get; }

    public double BlurRadius { get; }

    public double ShadowRadius { get; }

    public double GlowRadius { get; }

    public double SamplingMargin =>
        Math.Ceiling(Math.Max(BlurRadius, RefractionRadius) + OutlineWidth + 2.0);

    public LogicalRect SamplingRectangle => new(
        Bounds.X - SamplingMargin,
        Bounds.Y - SamplingMargin,
        Bounds.Width + SamplingMargin * 2.0,
        Bounds.Height + SamplingMargin * 2.0);

    public LogicalRect VisualRectangle
    {
        get
        {
            double margin = Math.Max(GlowRadius, ShadowRadius);
            return new LogicalRect(
                Bounds.X - margin,
                Bounds.Y - margin,
                Bounds.Width + margin * 2.0,
                Bounds.Height + margin * 2.0);
        }
    }

    public double SignedDistance(double x, double y) =>
        Shape switch
        {
            PanelShapeKind.Rectangle => RectangleDistance(x, y),
            PanelShapeKind.RoundedRectangle => RoundedRectangleDistance(x, y),
            PanelShapeKind.ChamferedRectangle => ChamferedRectangleDistance(x, y),
            _ => throw new ArgumentOutOfRangeException(nameof(Shape))
        };

    public double MaskCoverage(double x, double y, double antialiasWidth = 1.0)
    {
        ValidateNonNegative(antialiasWidth, nameof(antialiasWidth));
        if (antialiasWidth == 0)
        {
            return SignedDistance(x, y) <= 0 ? 1.0 : 0.0;
        }

        return Math.Clamp(0.5 - SignedDistance(x, y) / antialiasWidth, 0.0, 1.0);
    }

    public bool ContainsHit(double x, double y) => SignedDistance(x, y) <= 0.0;

    public SamplingBounds ClampSamplingBounds(int textureWidth, int textureHeight)
    {
        if (textureWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textureWidth));
        }

        if (textureHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textureHeight));
        }

        LogicalRect sample = SamplingRectangle;
        int left = Math.Clamp((int)Math.Floor(sample.X), 0, textureWidth);
        int top = Math.Clamp((int)Math.Floor(sample.Y), 0, textureHeight);
        int right = Math.Clamp((int)Math.Ceiling(sample.X + sample.Width), left, textureWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(sample.Y + sample.Height), top, textureHeight);
        return new SamplingBounds(left, top, right, bottom);
    }

    public GeometryShaderConstants ToShaderConstants() =>
        new(
            new Vector4(
                (float)Bounds.X,
                (float)Bounds.Y,
                (float)Bounds.X + (float)Bounds.Width,
                (float)Bounds.Y + (float)Bounds.Height),
            new Vector4(
                (float)((int)Shape),
                (float)CornerRadius,
                (float)CutLength,
                (float)OutlineWidth),
            new Vector4(
                (float)RefractionRadius,
                (float)BlurRadius,
                (float)ShadowRadius,
                (float)GlowRadius),
            new Vector4(
                (float)SamplingRectangle.X,
                (float)SamplingRectangle.Y,
                (float)SamplingMargin,
                0));

    public static PanelGeometry LiquidGlass(LogicalRect bounds, double cornerRadius = 24) =>
        new(
            PanelShapeKind.RoundedRectangle,
            bounds,
            cornerRadius,
            cutLength: 0,
            outlineWidth: 1.25,
            refractionRadius: 1.6,
            blurRadius: 9,
            shadowRadius: 18,
            glowRadius: 0);

    public static PanelGeometry CyberPunk(LogicalRect bounds, double cutLength = 14) =>
        new(
            PanelShapeKind.ChamferedRectangle,
            bounds,
            cornerRadius: 0,
            cutLength,
            outlineWidth: 1,
            refractionRadius: 0,
            blurRadius: 0,
            shadowRadius: 0,
            glowRadius: 6);

    private double RectangleDistance(double x, double y)
    {
        double dx = Math.Abs(x - (Bounds.X + Bounds.Width * 0.5)) - Bounds.Width * 0.5;
        double dy = Math.Abs(y - (Bounds.Y + Bounds.Height * 0.5)) - Bounds.Height * 0.5;
        double outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0));
        return outside + Math.Min(Math.Max(dx, dy), 0);
    }

    private double RoundedRectangleDistance(double x, double y)
    {
        if (CornerRadius == 0)
        {
            return RectangleDistance(x, y);
        }

        double halfWidth = Bounds.Width * 0.5;
        double halfHeight = Bounds.Height * 0.5;
        double qx = Math.Abs(x - (Bounds.X + halfWidth)) - (halfWidth - CornerRadius);
        double qy = Math.Abs(y - (Bounds.Y + halfHeight)) - (halfHeight - CornerRadius);
        double outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        return outside + Math.Min(Math.Max(qx, qy), 0) - CornerRadius;
    }

    private double ChamferedRectangleDistance(double x, double y)
    {
        if (CutLength == 0)
        {
            return RectangleDistance(x, y);
        }

        double left = Bounds.X;
        double top = Bounds.Y;
        double right = Bounds.X + Bounds.Width;
        double bottom = Bounds.Y + Bounds.Height;
        ReadOnlySpan<Vector2> vertices =
        [
            new((float)(left + CutLength), (float)top),
            new((float)(right - CutLength), (float)top),
            new((float)right, (float)(top + CutLength)),
            new((float)right, (float)(bottom - CutLength)),
            new((float)(right - CutLength), (float)bottom),
            new((float)(left + CutLength), (float)bottom),
            new((float)left, (float)(bottom - CutLength)),
            new((float)left, (float)(top + CutLength))
        ];

        double distanceToEdge = double.PositiveInfinity;
        bool inside = true;
        Vector2 point = new((float)x, (float)y);
        for (int index = 0; index < vertices.Length; index++)
        {
            Vector2 start = vertices[index];
            Vector2 end = vertices[(index + 1) % vertices.Length];
            Vector2 edge = end - start;
            Vector2 toPoint = point - start;
            double cross = edge.X * (double)toPoint.Y - edge.Y * (double)toPoint.X;
            if (cross < 0)
            {
                inside = false;
            }

            double length = edge.Length();
            if (length > 0)
            {
                double t = Math.Clamp(Vector2.Dot(toPoint, edge) / (length * length), 0, 1);
                Vector2 projection = start + edge * (float)t;
                distanceToEdge = Math.Min(distanceToEdge, Vector2.Distance(point, projection));
            }
        }

        return inside ? -distanceToEdge : distanceToEdge;
    }

    private static void ValidateNonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be finite and non-negative.");
        }
    }
}

public readonly record struct PremultipliedArgb(byte Alpha, byte Red, byte Green, byte Blue)
{
    public static PremultipliedArgb FromStraight(byte alpha, byte red, byte green, byte blue)
    {
        static byte Premultiply(byte component, byte a) =>
            (byte)((component * a + 127) / 255);

        return new PremultipliedArgb(
            alpha,
            Premultiply(red, alpha),
            Premultiply(green, alpha),
            Premultiply(blue, alpha));
    }

    public static PremultipliedArgb FromStraight(float alpha, float red, float green, float blue)
    {
        ValidateUnit(alpha, nameof(alpha));
        ValidateUnit(red, nameof(red));
        ValidateUnit(green, nameof(green));
        ValidateUnit(blue, nameof(blue));

        return new PremultipliedArgb(
            ToByte(alpha),
            ToByte(red * alpha),
            ToByte(green * alpha),
            ToByte(blue * alpha));
    }

    public bool IsValid => Red <= Alpha && Green <= Alpha && Blue <= Alpha;

    private static void ValidateUnit(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Color component must be in [0, 1].");
        }
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
}
