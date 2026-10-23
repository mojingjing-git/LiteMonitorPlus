using System.Runtime.InteropServices;

namespace LiteMonitor.Rendering.Presentation;

/// <summary>
/// 【T02 合同 §10 GPU-003 / 裁决 D4】GPU 侧的窗口扩展样式读取。
/// <para><c>WS_EX_LAYERED</c> 与 <c>WDA_EXCLUDEFROMCAPTURE</c> 互斥：分层窗口上不得申请
/// WDA 排除。期望路由由 owner A 的 <c>RenderingRoutePolicy.Decide</c> 依据
/// <c>RenderingRouteRequest.WindowIsLayered</c> 决定（有效路由降为 <c>GpuFixedInput</c>，
/// 原因码 <c>wda_capture_policy_unsupported_on_layered_window</c>）；
/// 本类是宿主侧的**二次校验**，保证即使路由侧漏判也不会去申请一个必然失败的 WDA 排除。</para>
/// <para><b>不修改</b>任何 ex-style —— 既有透明度/穿透语义保持不变（合同 §1.3 明确否决
/// "在 <c>SetClickThrough(false)</c> 里一并清 <c>WS_EX_LAYERED</c>"）。</para>
/// </summary>
public static class WindowExtendedStyle
{
    /// <summary><c>GetWindowLong</c> 的 <c>GWL_EXSTYLE</c> 索引。</summary>
    public const int GwlExStyle = -20;

    /// <summary><c>WS_EX_LAYERED</c> 位。</summary>
    public const int WsExLayered = 0x00020000;

    /// <summary>读取窗口的扩展样式；句柄无效时返回 0。</summary>
    public static int GetExStyle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return 0;
        }

        return GetWindowLong(windowHandle, GwlExStyle);
    }

    /// <summary>窗口是否处于分层（<c>WS_EX_LAYERED</c>）状态。</summary>
    public static bool IsLayered(IntPtr windowHandle)
    {
        int exStyle = GetExStyle(windowHandle);
        return (exStyle & WsExLayered) != 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);
}
