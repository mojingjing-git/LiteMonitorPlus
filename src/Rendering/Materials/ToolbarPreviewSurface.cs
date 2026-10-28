using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using LiteMonitor.Rendering.Contracts;
namespace LiteMonitor.Rendering.Materials;

public sealed record ToolbarPreviewSurface(int Width, int Height, byte[] Pixels)
{
    public const int SurfaceWidth = 1280;
    public const int SurfaceHeight = 900;

    /// <summary>
    /// 尺寸档位：按**等比面积上限**取桶 ⇒ 命中缓存 ⇒ 再用双线性缩放到精确尺寸。
    /// <para>★ 为什么要档位：逐像素程序化生成每像素约 9 次指数衰减 + 若干三角函数；
    /// 在 200% 缩放、面板 800×3000 物理像素时是数百万次超越函数 ⇒
    /// **UI 线程阻塞数百毫秒**，而每次尺寸变化都会重算一遍。
    /// 后果：改缩放时窗口来不及改高度 ⇒ 面板只显示顶部一条（裁切），卡顿结束后才恢复。</para>
    /// <para>图案由相对坐标生成，等比缩放后肉眼无差；而成本从「每次按全尺寸重算」
    /// 降为「每桶算一次（面积受限）+ 每次纯内存缩放」。</para>
    /// </summary>
    /// <summary>档位面积上限（像素）：约 256×256，生成成本 ~25ms，肉眼无损（图案是柔和渐变）。</summary>
    private const int MaxBucketPixels = 256 * 256;

    /// <summary>档位缓存条目上限（每条 ≤ 2MB，合计有界）。</summary>
    private const int MaxBucketEntries = 3;

    /// <summary>exp 查表规模与自变量上限（z ≥ 上限时 exp(-z) 已可视为 0）。</summary>
    private const int ExpTableSize = 4096;
    private const float ExpTableMaxZ = 12f;

    private static readonly float[] ExpTable = BuildExpTable();

    private static float[] BuildExpTable()
    {
        float[] table = new float[ExpTableSize + 1];
        for (int i = 0; i <= ExpTableSize; i++)
        {
            table[i] = MathF.Exp(-i * (ExpTableMaxZ / ExpTableSize));
        }
        return table;
    }

    private static readonly Lazy<ToolbarPreviewSurface> Cached = new(() => Build(SurfaceWidth, SurfaceHeight));
    private static readonly Dictionary<(int Width, int Height), ToolbarPreviewSurface> Buckets = new();
    private static readonly LinkedList<(int Width, int Height)> BucketOrder = new();
    private static readonly object BucketsGate = new();

    public string Sha256 => Convert.ToHexString(SHA256.HashData(Pixels));

    public static ToolbarPreviewSurface Create() => Cached.Value;

    public static FixedBackgroundInput CreateInput(int width, int height)
    {
        if (width < 1 || height < 1 || width > FixedBackgroundInput.MaximumDimension || height > FixedBackgroundInput.MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        var surface = GetSurface(width, height);
        if (!FixedBackgroundInput.TryCreate(surface.Pixels, width, height, checked(width * 4), 96,
            new PixelRectI(0, 0, width, height), "toolbar-v4-synthetic", out var input, out var code, out var message))
            throw new InvalidOperationException(code + ": " + message);
        return input!;
    }

    /// <summary>
    /// 取目标尺寸的表面：命中档位缓存就缩放，未命中就生成一次并缓存。
    /// </summary>
    private static ToolbarPreviewSurface GetSurface(int width, int height)
    {
        if (width == SurfaceWidth && height == SurfaceHeight)
        {
            return Create();
        }

        (int bucketWidth, int bucketHeight) = BucketSize(width, height);
        ToolbarPreviewSurface bucket;
        lock (BucketsGate)
        {
            if (!Buckets.TryGetValue((bucketWidth, bucketHeight), out bucket!))
            {
                bucket = Build(bucketWidth, bucketHeight);
                Buckets[(bucketWidth, bucketHeight)] = bucket;
                BucketOrder.AddLast((bucketWidth, bucketHeight));

                // 档位缓存有条数上限（每条 ≤ 2MB）：超出就淘汰最旧的一条。
                while (BucketOrder.Count > MaxBucketEntries)
                {
                    (int Width, int Height) oldest = BucketOrder.First!.Value;
                    BucketOrder.RemoveFirst();
                    Buckets.Remove(oldest);
                }
            }
        }

        if (bucket.Width == width && bucket.Height == height)
        {
            return bucket;
        }

        return Scale(bucket, width, height);
    }

    /// <summary>
    /// 把请求尺寸映射到一个**等比**档位：保持长宽比，把面积压到
    /// <see cref="MaxBucketPixels"/> 以内。
    /// <para>等比 ⇒ 图案不被拉伸；面积受限 ⇒ 单次生成成本有界。
    /// 取整一律向下 ⇒ 面积不会因为取整而反超上限。</para>
    /// </summary>
    private static (int Width, int Height) BucketSize(int width, int height)
    {
        long pixels = (long)width * height;
        if (pixels <= MaxBucketPixels)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        double factor = Math.Sqrt((double)MaxBucketPixels / pixels);
        int bucketWidth = Math.Max(1, (int)Math.Floor(width * factor));
        int bucketHeight = Math.Max(1, (int)Math.Floor(height * factor));

        // 逐边向下取整可能让面积略超上限 ⇒ 再逐像素回退，直到达标。
        while ((long)bucketWidth * bucketHeight > MaxBucketPixels)
        {
            if (bucketWidth >= bucketHeight)
            {
                bucketWidth--;
            }
            else
            {
                bucketHeight--;
            }

            if (bucketWidth < 1 || bucketHeight < 1)
            {
                break;
            }
        }

        bucketWidth = Math.Clamp(bucketWidth, 1, FixedBackgroundInput.MaximumDimension);
        bucketHeight = Math.Clamp(bucketHeight, 1, FixedBackgroundInput.MaximumDimension);
        return (bucketWidth, bucketHeight);
    }

    /// <summary>
    /// 双线性缩放（走 GDI+ 原生缩放，不用手写逐像素插值）。
    /// <para>★ 为什么不用手写循环：目标面可达数百万像素，手写每像素每通道的插值在
    /// 托管层要跑几十毫秒到几百毫秒，等于把「生成卡顿」换成「缩放卡顿」。
    /// GDI+ 的 DrawImage 是原生实现，同样的放大只要几十毫秒。</para>
    /// </summary>
    private static ToolbarPreviewSurface Scale(ToolbarPreviewSurface source, int width, int height)
    {
        using var sourceBitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        WritePixels(sourceBitmap, source.Pixels, source.Width, source.Height);

        using var targetBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(targetBitmap))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.DrawImage(sourceBitmap, new Rectangle(0, 0, width, height));
        }

        return new ToolbarPreviewSurface(width, height, ReadPixels(targetBitmap, width, height));
    }

    private static void WritePixels(Bitmap bitmap, byte[] pixels, int width, int height)
    {
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte[] ReadPixels(Bitmap bitmap, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return pixels;
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

    private static ToolbarPreviewSurface Build(int width, int height)
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
                float curveBand = ExpNeg(Square((ny - curveA) / 0.055f));
                r = Lerp(r, 0.84f, curveBand * 0.20f);
                g = Lerp(g, 0.88f, curveBand * 0.20f);
                b = Lerp(b, 0.93f, curveBand * 0.20f);

                float curveB = 0.20f + 0.035f * MathF.Sin(nx * 8.4f + 0.8f);
                float coolBand = ExpNeg(Square((ny - curveB) / 0.026f));
                r = Lerp(r, 0.87f, coolBand * 0.17f);
                g = Lerp(g, 0.91f, coolBand * 0.17f);
                b = Lerp(b, 0.96f, coolBand * 0.17f);

                float lineA = MathF.Abs(ny - (0.77f + 0.085f * MathF.Sin(nx * 5.8f + 0.35f)));
                float lineB = MathF.Abs(ny - (0.52f + 0.065f * MathF.Sin(nx * 7.2f - 1.1f)));
                float circle = MathF.Abs(
                    MathF.Sqrt(Square((nx - 0.50f) * 1.32f) + Square(ny - 0.44f)) -
                    0.355f);

                float lineWeight = MathF.Max(
                    ExpNeg(Square(lineA / 0.0012f)),
                    MathF.Max(
                        ExpNeg(Square(lineB / 0.0009f)) * 0.68f,
                        ExpNeg(Square(circle / 0.0010f)) * 0.60f));
                r = Lerp(r, 0.55f, lineWeight * 0.20f);
                g = Lerp(g, 0.59f, lineWeight * 0.20f);
                b = Lerp(b, 0.64f, lineWeight * 0.20f);

                float vignette = 1.0f - 0.035f * Square(
                    MathF.Min(1.0f, MathF.Sqrt(
                        Square((nx - 0.5f) * 1.35f) +
                        Square((ny - 0.5f) * 1.08f))));
                int offset = (y * width + x) * 4;
                WritePixel(pixels, offset, r * vignette, g * vignette, b * vignette);
            }
        }

        return new ToolbarPreviewSurface(width, height, pixels);
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
        float distance = Square((x - centerX) / radiusX) +
            Square((y - centerY) / radiusY);
        float weight = ExpNeg(distance * 1.35f) * strength;
        r = Lerp(r, fieldR, weight);
        g = Lerp(g, fieldG, weight);
        b = Lerp(b, fieldB, weight);
    }

    /// <summary>平方（等价 <c>MathF.Pow(v, 2)</c>，但快一个数量级）。</summary>
    private static float Square(float value) => value * value;

    /// <summary>
    /// <c>exp(-z)</c> 的查表实现（线性插值）。
    /// <para>★ 为什么不用 <c>MathF.Exp</c>：逐像素生成里它出现约 9 次/像素，
    /// 在数十万像素的表面上就是几百万次超越函数 ⇒ UI 线程阻塞上百毫秒。
    /// 查表步长 12/4096 ≈ 0.003 ⇒ 相对误差 &lt; 0.2%，肉眼无差。</para>
    /// </summary>
    private static float ExpNeg(float z)
    {
        if (z <= 0f) return 1f;
        if (z >= ExpTableMaxZ) return 0f;
        float scaled = z * (ExpTableSize / ExpTableMaxZ);
        int index = (int)scaled;
        if (index >= ExpTableSize) return 0f;
        float fraction = scaled - index;
        return ExpTable[index] + (ExpTable[index + 1] - ExpTable[index]) * fraction;
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
