using System.Runtime.InteropServices;

namespace AppearanceMaterialLab;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const long WsExLayered = 0x00080000L;
    internal const long WsExTransparent = 0x00000020L;
    internal const uint WdaNone = 0x00000000;
    internal const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    internal static long GetExtendedStyle(IntPtr hWnd) =>
        GetWindowLongPtr(hWnd, GwlExStyle).ToInt64();

    internal static void SetExtendedStyleBits(IntPtr hWnd, long enabled, long disabled)
    {
        long style = GetExtendedStyle(hWnd);
        SetWindowLongPtr(hWnd, GwlExStyle, new IntPtr((style | enabled) & ~disabled));
    }
}
