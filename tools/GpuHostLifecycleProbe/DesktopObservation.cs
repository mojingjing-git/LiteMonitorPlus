using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GpuHostLifecycleProbe;

/// <summary>
/// Desktop read-back helpers for the T01 "did GDI recover" criterion.
///
/// The criterion is deliberately based on the pixels the desktop compositor
/// actually shows (Graphics.CopyFromScreen over the window's screen region),
/// never on DrawToBitmap / PrintWindow / "the GDI call was made"
/// (GPU plan §7.3 forbids those as sole evidence).
/// </summary>
internal static class DesktopObservation
{
    /// <summary>Per-channel tolerance between the expected GDI colour and the observed desktop pixel.</summary>
    public const int ChannelTolerance = 12;

    /// <summary>Edge length of the sampled square, in desktop pixels.</summary>
    public const int BlockSize = 32;

    /// <summary>Minimum share of the sampled block that must match the expected colour.</summary>
    public const double RequiredMatchRatio = 0.9;

    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;

    public const int WsExTopmost = 0x00000008;
    public const int WsExLayered = 0x00080000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetClientRect")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "ClientToScreen")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowRgn")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", EntryPoint = "PtInRegion")]
    private static extern bool PtInRegion(IntPtr hRgn, int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetLayeredWindowAttributes")]
    private static extern bool GetLayeredWindowAttributes(
        IntPtr hWnd,
        out uint pcrKey,
        out byte pbAlpha,
        out uint pdwFlags);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Reads the physical-pixel client rectangle of a window, in desktop coordinates.</summary>
    public static (int X, int Y, int Width, int Height) ClientRectOnScreen(IntPtr hWnd)
    {
        GetClientRect(hWnd, out NativeRect client);
        NativePoint origin = new() { X = 0, Y = 0 };
        ClientToScreen(hWnd, ref origin);
        return (origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    /// <summary>Reads the physical-pixel window rectangle of a window, in desktop coordinates.</summary>
    public static Rectangle WindowRectOnScreen(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out NativeRect rect))
        {
            return Rectangle.Empty;
        }

        return new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static int ExStyle(IntPtr hWnd) => GetWindowLong(hWnd, GwlExStyle);

    public static int Style(IntPtr hWnd) => GetWindowLong(hWnd, GwlStyle);

    /// <summary>
    /// Reports whether the native window currently carries a region that covers the
    /// client origin. GetWindowRgn returns NULLREGION (0) for "no region", but
    /// SIMPLEREGION is also 0, so the region is probed with PtInRegion instead.
    /// </summary>
    public static (int RegionType, bool CoversOrigin) NativeRegion(IntPtr hWnd)
    {
        IntPtr region = CreateRectRgn(4096, 4096, 4097, 4097);
        if (region == IntPtr.Zero)
        {
            return (-1, false);
        }

        try
        {
            int type = GetWindowRgn(hWnd, region);
            bool covers = PtInRegion(region, 0, 0);
            return (type, covers);
        }
        finally
        {
            DeleteObject(region);
        }
    }

    /// <summary>Reads the layered-window attributes; false when the window is not layered.</summary>
    public static (bool Readable, byte Alpha, uint ColorKey, uint Flags) LayeredAttributes(IntPtr hWnd)
    {
        bool readable = GetLayeredWindowAttributes(hWnd, out uint colorKey, out byte alpha, out uint flags);
        return (readable, alpha, colorKey, flags);
    }

    public static bool IsVisible(IntPtr hWnd) => IsWindowVisible(hWnd);

    /// <summary>Captures a physical desktop region. Negative coordinates are supported (left-hand displays).</summary>
    public static Bitmap CaptureRegion(int x, int y, int width, int height)
    {
        Bitmap bitmap = new(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// Compares a desktop block against the expected GDI colour and reports how much
    /// of the block matches. A live GDI frame matches almost completely; a frozen
    /// GPU frame or an obstructed window does not.
    /// </summary>
    public static BlockMatch MatchBlock(Bitmap region, int blockX, int blockY, int blockSize, Color expected)
    {
        int total = 0;
        int matching = 0;
        Color center = Color.Black;
        bool hasCenter = false;

        for (int dy = 0; dy < blockSize; dy++)
        {
            for (int dx = 0; dx < blockSize; dx++)
            {
                int px = blockX + dx;
                int py = blockY + dy;
                if (px < 0 || py < 0 || px >= region.Width || py >= region.Height)
                {
                    continue;
                }

                Color pixel = region.GetPixel(px, py);
                total++;
                if (ChannelsMatch(pixel, expected))
                {
                    matching++;
                }

                if (!hasCenter && dx == blockSize / 2 && dy == blockSize / 2)
                {
                    center = pixel;
                    hasCenter = true;
                }
            }
        }

        double ratio = total == 0 ? 0d : (double)matching / total;
        return new BlockMatch(center, matching, total, ratio, ratio >= RequiredMatchRatio);
    }

    public static bool ChannelsMatch(Color left, Color right) =>
        Math.Abs(left.R - right.R) <= ChannelTolerance &&
        Math.Abs(left.G - right.G) <= ChannelTolerance &&
        Math.Abs(left.B - right.B) <= ChannelTolerance;

    /// <summary>True when two observed colours are far enough apart to prove the frame changed.</summary>
    public static bool ColorsDiffer(Color left, Color right) => !ChannelsMatch(left, right);
}

/// <summary>Result of comparing one sampled desktop block against the expected GDI colour.</summary>
internal sealed record BlockMatch(
    Color ObservedCenter,
    int MatchingPixels,
    int TotalPixels,
    double MatchRatio,
    bool Matched);
