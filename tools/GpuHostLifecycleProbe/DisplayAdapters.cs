using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace GpuHostLifecycleProbe;

/// <summary>
/// Records which display adapter the probe window actually sits on.
///
/// The workstation has two NVIDIA boards plus four virtual display adapters,
/// so "an NVIDIA card" is not an acceptable answer. The window is mapped to
/// the DXGI output whose desktop rectangle contains the window centre
/// (DXGI_OUTPUT_DESC.DesktopCoordinates), and the owning adapter is resolved
/// through EnumDisplayDevices.
/// </summary>
internal static class DisplayAdapters
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? deviceName,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);

    /// <summary>Enumerates every DXGI output with its desktop rectangle and owning adapter.</summary>
    public static object[] Enumerate()
    {
        List<object> results = [];

        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1 adapter;
                Result enumerated = factory.EnumAdapters1(index, out adapter);
                if (enumerated.Failure || adapter is null)
                {
                    break;
                }

                using (adapter)
                {
                    string adapterName = adapter.Description.Description;
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        IDXGIOutput output;
                        Result outputResult = adapter.EnumOutputs(outputIndex, out output);
                        if (outputResult.Failure || output is null)
                        {
                            break;
                        }

                        using (output)
                        {
                            OutputDescription description = output.Description;
                            results.Add(new
                            {
                                adapter = adapterName,
                                deviceName = description.DeviceName,
                                desktopLeft = description.DesktopCoordinates.Left,
                                desktopTop = description.DesktopCoordinates.Top,
                                desktopRight = description.DesktopCoordinates.Right,
                                desktopBottom = description.DesktopCoordinates.Bottom,
                                attachedToDesktop = description.AttachedToDesktop
                            });
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            results.Add(new { error = $"DXGI enumeration failed: {exception.Message}" });
        }

        return results.ToArray();
    }

    /// <summary>
    /// Resolves the adapter that owns the display device the window sits on
    /// (for example "\\.\DISPLAY1" -> "NVIDIA GeForce RTX 3080").
    /// </summary>
    public static string? ResolveAdapterForDisplayDevice(string displayDeviceName)
    {
        for (uint index = 0; ; index++)
        {
            DisplayDevice device = new() { Cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            if (!string.Equals(device.DeviceName, displayDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The display adapter entry carries the adapter name; walk its
            // monitors to confirm the display device really is attached.
            for (uint monitorIndex = 0; ; monitorIndex++)
            {
                DisplayDevice monitor = new() { Cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(device.DeviceName, monitorIndex, ref monitor, 0))
                {
                    break;
                }

                if (string.Equals(monitor.DeviceID, displayDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return device.DeviceString;
                }
            }

            return device.DeviceString;
        }

        return null;
    }

    /// <summary>Maps a point in desktop coordinates to the adapter that owns it.</summary>
    public static string? ResolveAdapterForPoint(int x, int y)
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            if (!screen.Bounds.Contains(x, y))
            {
                continue;
            }

            string? adapter = ResolveAdapterForDisplayDevice(screen.DeviceName);
            if (!string.IsNullOrWhiteSpace(adapter))
            {
                return adapter;
            }

            return $"{screen.DeviceName} (adapter name unresolved)";
        }

        return null;
    }
}
