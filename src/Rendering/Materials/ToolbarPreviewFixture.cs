using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using LiteMonitor.Rendering.Contracts;
namespace LiteMonitor.Rendering.Materials;

public sealed record ToolbarPreviewFixture(int Width, int Height, byte[] Pixels)
{
    public const int FixtureWidth = 1280;
    public const int FixtureHeight = 900;

    private static readonly Lazy<ToolbarPreviewFixture> Cached = new(() => Build(FixtureWidth, FixtureHeight));

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Pixels));

    public static ToolbarPreviewFixture Create() => Cached.Value;

    public static FixedBackgroundInput CreateInput(int width, int height)
    {
        if (width < 1 || height < 1 || width > FixedBackgroundInput.MaximumDimension || height > FixedBackgroundInput.MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        var fixture = width == FixtureWidth && height == FixtureHeight ? Create() : Build(width, height);
        if (!FixedBackgroundInput.TryCreate(fixture.Pixels, width, height, checked(width * 4), 96,
            new PixelRectI(0, 0, width, height), "toolbar-v4-synthetic", out var input, out var code, out var message))
            throw new InvalidOperationException(code + ": " + message);
        return input!;
    }
    public void SavePng(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using Bitmap bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, Width, Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(
                    Pixels,
                    y * Width * 4,
                    data.Scan0 + y * data.Stride,
                    Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static ToolbarPreviewFixture Build(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            float ny = y / (float)Math.Max(1, height - 1);
            for (int x = 0; x < width; x++)
            {
                float nx = x / (float)Math.Max(1, width - 1);
                float r = Lerp(0.925f, 0.885f, ny) + 0.010f * nx;
                float g = Lerp(0.925f, 0.900f, ny) + 0.006f * nx;
                float b = Lerp(0.945f, 0.925f, ny) - 0.004f * nx;

                BlendField(ref r, ref g, ref b, nx, ny, 0.10f, 0.24f, 0.43f, 0.48f,
                    0.79f, 0.85f, 0.94f, 0.72f);
                BlendField(ref r, ref g, ref b, nx, ny, 0.23f, 0.92f, 0.55f, 0.32f,
                    0.85f, 0.80f, 0.93f, 0.58f);
                BlendField(ref r, ref g, ref b, nx, ny, 0.88f, 0.25f, 0.46f, 0.43f,
                    0.97f, 0.87f, 0.80f, 0.57f);
                BlendField(ref r, ref g, ref b, nx, ny, 0.83f, 0.84f, 0.43f, 0.36f,
                    0.79f, 0.90f, 0.88f, 0.46f);

                // A broad blue-teal curve crosses the middle of the glass and
                // makes the edge resampling visible without becoming a test swatch.
                float blueCurve = 0.43f + 0.14f * MathF.Sin(nx * 5.8f);
                float blueDistance = MathF.Abs(ny - blueCurve);
                float blueSurface = 1.0f - SmoothStep(0.060f, 0.115f, blueDistance);
                r = Lerp(r, 0.30f, blueSurface * 0.50f);
                g = Lerp(g, 0.55f, blueSurface * 0.50f);
                b = Lerp(b, 0.67f, blueSurface * 0.50f);

                // A soft apricot surface occupies the upper-right wallpaper area.
                float warmCurve = 0.19f + 0.055f * MathF.Sin(nx * 3.7f + 0.35f);
                float warmSurface = SmoothStep(0.52f, 0.66f, nx) *
                    (1.0f - SmoothStep(warmCurve - 0.035f, warmCurve + 0.12f, ny));
                r = Lerp(r, 0.96f, warmSurface * 0.42f);
                g = Lerp(g, 0.83f, warmSurface * 0.42f);
                b = Lerp(b, 0.73f, warmSurface * 0.42f);

                // Broad, smooth color boundaries keep the synthetic wallpaper quiet.
                float curveA = 0.69f + 0.115f * MathF.Sin((nx + 0.06f) * 5.1f) +
                    0.030f * MathF.Sin((ny - nx) * 4.0f);
                float curveBand = MathF.Exp(-MathF.Pow((ny - curveA) / 0.055f, 2.0f));
                r = Lerp(r, 0.84f, curveBand * 0.20f);
                g = Lerp(g, 0.88f, curveBand * 0.20f);
                b = Lerp(b, 0.93f, curveBand * 0.20f);

                float curveB = 0.20f + 0.035f * MathF.Sin(nx * 8.4f + 0.8f);
                float coolBand = MathF.Exp(-MathF.Pow((ny - curveB) / 0.026f, 2.0f));
                r = Lerp(r, 0.87f, coolBand * 0.17f);
                g = Lerp(g, 0.91f, coolBand * 0.17f);
                b = Lerp(b, 0.96f, coolBand * 0.17f);

                float lineA = MathF.Abs(ny - (0.77f + 0.085f * MathF.Sin(nx * 5.8f + 0.35f)));
                float lineB = MathF.Abs(ny - (0.52f + 0.065f * MathF.Sin(nx * 7.2f - 1.1f)));
                float circle = MathF.Abs(
                    MathF.Sqrt(MathF.Pow((nx - 0.50f) * 1.32f, 2.0f) + MathF.Pow(ny - 0.44f, 2.0f)) -
                    0.355f);

                float lineWeight = MathF.Max(
                    MathF.Exp(-MathF.Pow(lineA / 0.0012f, 2.0f)),
                    MathF.Max(
                        MathF.Exp(-MathF.Pow(lineB / 0.0009f, 2.0f)) * 0.68f,
                        MathF.Exp(-MathF.Pow(circle / 0.0010f, 2.0f)) * 0.60f));
                r = Lerp(r, 0.55f, lineWeight * 0.20f);
                g = Lerp(g, 0.59f, lineWeight * 0.20f);
                b = Lerp(b, 0.64f, lineWeight * 0.20f);

                float vignette = 1.0f - 0.035f * MathF.Pow(
                    MathF.Min(1.0f, MathF.Sqrt(
                        MathF.Pow((nx - 0.5f) * 1.35f, 2.0f) +
                        MathF.Pow((ny - 0.5f) * 1.08f, 2.0f))),
                    2.0f);
                int offset = (y * width + x) * 4;
                WritePixel(pixels, offset, r * vignette, g * vignette, b * vignette);
            }
        }

        return new ToolbarPreviewFixture(width, height, pixels);
    }

    private static void BlendField(
        ref float r,
        ref float g,
        ref float b,
        float x,
        float y,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float fieldR,
        float fieldG,
        float fieldB,
        float strength)
    {
        float distance = MathF.Pow((x - centerX) / radiusX, 2.0f) +
            MathF.Pow((y - centerY) / radiusY, 2.0f);
        float weight = MathF.Exp(-distance * 1.35f) * strength;
        r = Lerp(r, fieldR, weight);
        g = Lerp(g, fieldG, weight);
        b = Lerp(b, fieldB, weight);
    }

    private static float Lerp(float from, float to, float amount) =>
        from + (to - from) * Math.Clamp(amount, 0.0f, 1.0f);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float amount = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return amount * amount * (3.0f - 2.0f * amount);
    }

    private static void WritePixel(byte[] pixels, int offset, float r, float g, float b)
    {
        pixels[offset + 0] = ToByte(b);
        pixels[offset + 1] = ToByte(g);
        pixels[offset + 2] = ToByte(r);
        pixels[offset + 3] = 255;
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
}
