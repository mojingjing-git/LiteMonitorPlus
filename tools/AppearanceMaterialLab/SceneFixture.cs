using System.Security.Cryptography;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AppearanceMaterialLab;

internal sealed record FixedBackground(int Width, int Height, byte[] Pixels)
{
    public const int FixtureWidth = 1600;
    public const int FixtureHeight = 1000;

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Pixels));

    public void SavePng(string path)
    {
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

    public static FixedBackground Create()
    {
        byte[] pixels = new byte[FixtureWidth * FixtureHeight * 4];

        for (int y = 0; y < FixtureHeight; y++)
        {
            for (int x = 0; x < FixtureWidth; x++)
            {
                int offset = (y * FixtureWidth + x) * 4;
                float nx = x / (float)(FixtureWidth - 1);
                float ny = y / (float)(FixtureHeight - 1);

                float vignette = 1.0f - 0.2f * MathF.Pow(
                    MathF.Max(MathF.Abs(nx - 0.5f), MathF.Abs(ny - 0.5f)) * 2.0f,
                    2.0f);
                float wave = MathF.Sin(nx * 12.0f + ny * 4.0f) * 0.018f;
                float b = (0.20f + 0.18f * ny + 0.08f * nx + wave) * vignette;
                float g = (0.12f + 0.23f * nx + 0.05f * ny - wave) * vignette;
                float r = (0.07f + 0.08f * ny + 0.04f * MathF.Sin(ny * 9.0f)) * vignette;

                if (y < 330)
                {
                    float light = (1.0f - y / 330.0f) * 0.24f;
                    b += light * 0.95f;
                    g += light * 0.72f;
                    r += light * 0.42f;
                }

                WritePixel(pixels, offset, r, g, b);
            }
        }

        int checkerSize = 38;
        for (int y = 60; y < 260; y++)
        {
            for (int x = 80; x < 460; x++)
            {
                int offset = (y * FixtureWidth + x) * 4;
                bool light = ((x / checkerSize) + (y / checkerSize)) % 2 == 0;
                float value = light ? 0.82f : 0.10f;
                WritePixel(pixels, offset, value * 0.94f, value * 0.98f, value);
            }
        }

        FillRect(pixels, 520, 90, 370, 190, 0.93f, 0.24f, 0.22f);
        FillRect(pixels, 900, 120, 420, 160, 0.16f, 0.65f, 0.87f);
        FillRect(pixels, 110, 610, 610, 230, 0.92f, 0.82f, 0.55f);
        FillRect(pixels, 990, 590, 430, 240, 0.34f, 0.82f, 0.48f);

        for (int y = 660; y < 820; y++)
        {
            for (int x = 136; x < 680; x++)
            {
                if ((y - 660) % 12 >= 7)
                {
                    continue;
                }

                int offset = (y * FixtureWidth + x) * 4;
                float stripe = (x / 6) % 2 == 0 ? 0.07f : 0.19f;
                WritePixel(pixels, offset, stripe * 0.8f, stripe * 0.72f, stripe * 0.55f);
            }
        }

        for (int y = 610; y < 870; y++)
        {
            for (int x = 990; x < 1420; x++)
            {
                if ((x + y) % 23 == 0 || (x - y + 2000) % 29 == 0)
                {
                    int offset = (y * FixtureWidth + x) * 4;
                    WritePixel(pixels, offset, 0.78f, 0.94f, 0.80f);
                }
            }
        }

        return new FixedBackground(FixtureWidth, FixtureHeight, pixels);
    }

    private static void FillRect(
        byte[] pixels,
        int left,
        int top,
        int width,
        int height,
        float r,
        float g,
        float b)
    {
        for (int y = top; y < top + height; y++)
        {
            for (int x = left; x < left + width; x++)
            {
                int offset = (y * FixtureWidth + x) * 4;
                WritePixel(pixels, offset, r, g, b);
            }
        }
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
