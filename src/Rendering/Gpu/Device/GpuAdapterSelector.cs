using SharpGen.Runtime;
using Vortice.DXGI;

namespace LiteMonitor.Rendering.Gpu.Device;

/// <summary>
/// 按**窗口所在显示器**选择渲染适配器。
/// <para>★ 为什么必须这样做：D3D11CreateDevice 传 null 适配器时用的是 DXGI 默认适配器
/// （混合显卡机器上通常是独显）。若窗口所在显示器并不接在该适配器上：
/// <list type="bullet">
///   <item>CreateSwapChainForHwnd 无法呈现到该屏 ⇒ 画面根本出不来（观感像"没反应"）；</item>
///   <item>Desktop Duplication 也抓不到该屏 ⇒ 实时桌面背景拿不到画面。</item>
/// </list>
/// 用户侧观感：切液态玻璃没反应，**把窗口拖到另一块显示器**才正常。
/// 因此渲染适配器必须跟随窗口所在屏选择，而不是盲用默认适配器。</para>
/// </summary>
public static class GpuAdapterSelector
{
    /// <summary>
    /// 选出覆盖指定显示器的硬件适配器；DXGI 枚举顺序即性能序（独显优先），取第一个覆盖者。
    /// <para>返回 null 表示没有任何适配器驱动该显示器（此时调用方应回退默认适配器并给出原因码）。</para>
    /// </summary>
    public static IDXGIAdapter? SelectAdapterForMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; ; index++)
        {
            Result enumerated = factory.EnumAdapters1(index, out IDXGIAdapter1? adapter);
            if (enumerated.Failure || adapter is null)
            {
                break;
            }

            bool dispose = true;
            try
            {
                AdapterDescription1 description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) != 0)
                {
                    continue;
                }

                if (AdapterCoversMonitor(adapter, monitor))
                {
                    // 所有权移交调用方 ⇒ 不在此释放。
                    dispose = false;
                    return adapter;
                }
            }
            finally
            {
                if (dispose)
                {
                    adapter.Dispose();
                }
            }
        }

        return null;
    }

    /// <summary>该适配器是否有输出挂在指定显示器上。</summary>
    public static bool AdapterCoversMonitor(IDXGIAdapter adapter, IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        for (uint index = 0; ; index++)
        {
            Result enumerated = adapter.EnumOutputs(index, out IDXGIOutput? output);
            if (enumerated.Failure || output is null)
            {
                break;
            }

            using (output)
            {
                if (output.Description.Monitor == monitor)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
