using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AppearanceBackdropProbe.Native;

namespace AppearanceBackdropProbe.Evidence
{
    /// <summary>环境记录：每次试验都必须带上（v4 §4 / §7.4）。</summary>
    internal sealed class EnvRecord
    {
        public string TimestampUtc { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public int OsBuild { get; set; }
        public string OsDisplayVersion { get; set; } = "";
        public string OsBuildUbr { get; set; } = "";
        public bool IsWindows11 { get; set; }
        public bool SupportsSystemBackdropApiBuild { get; set; }
        public string CpuName { get; set; } = "";
        public int LogicalProcessors { get; set; }
        public string DotnetVersion { get; set; } = "";
        public string UserName { get; set; } = "";
        public bool IsElevated { get; set; }
        public string IntegrityLevel { get; set; } = "";
        public string RequestedExecutionLevel { get; set; } = "";
        public bool IsRemoteSession { get; set; }
        public bool CompositionEnabled { get; set; }
        public List<string> DisplayAdapters { get; set; } = new();
        public List<ScreenRecord> Screens { get; set; } = new();
        public VirtualScreenRecord VirtualScreen { get; set; } = new();
        public List<AdvancedColorRecord> AdvancedColor { get; set; } = new();
        public string AdvancedColorNote { get; set; } = "";
    }

    internal sealed class ScreenRecord
    {
        public string DeviceName { get; set; } = "";
        public bool IsPrimary { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int DpiX { get; set; }
        public int DpiY { get; set; }
        public double ScaleX { get; set; }
        public bool HasNegativeOrigin { get; set; }
    }

    internal sealed class VirtualScreenRecord
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class AdvancedColorRecord
    {
        public int AdapterLuidLow { get; set; }
        public int AdapterLuidHigh { get; set; }
        public uint TargetId { get; set; }
        public int DeviceInfoType { get; set; }
        public int HResult { get; set; }
        public uint ColorEncoding { get; set; }
        public uint BitsPerColorChannel { get; set; }
        public bool AdvancedColorSupported { get; set; }
        public bool AdvancedColorEnabled { get; set; }
        public bool WideColorEnforced { get; set; }
    }

    internal static class EnvProbe
    {
        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Interop.POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(IntPtr requestBuffer);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, out uint numPathElements, IntPtr pathInfoArray,
                                                     out uint numModeElements, IntPtr modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathElements, out uint numModeElements);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSidSubAuthority(IntPtr sid, uint subAuthority, out uint subAuthorityValue);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint TOKEN_QUERY = 0x0008;
        // SDK winnt.h: TokenIntegrityLevel = 25 (TOKEN_INFORMATION_CLASS)
        private const int TokenIntegrityLevel = 25;

        public static EnvRecord Capture(string requestedExecutionLevel)
        {
            var rec = new EnvRecord
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                RequestedExecutionLevel = requestedExecutionLevel,
            };

            var ver = Environment.OSVersion.Version;
            rec.OsVersion = ver.ToString();
            rec.OsBuild = ver.Build;
            rec.IsWindows11 = ver.Major == 10 && ver.Build >= 22000;
            rec.SupportsSystemBackdropApiBuild = ver.Major == 10 && ver.Build >= 22621;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                rec.OsDisplayVersion = key?.GetValue("DisplayVersion")?.ToString() ?? "";
                rec.OsBuildUbr = key?.GetValue("UBR")?.ToString() ?? "";
            }
            catch { }

            try
            {
                rec.CpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
                rec.LogicalProcessors = Environment.ProcessorCount;
            }
            catch { }

            rec.DotnetVersion = Environment.Version.ToString();
            rec.UserName = Environment.UserName;
            rec.IsElevated = IsElevated();
            rec.IntegrityLevel = GetIntegrityLevel();
            rec.IsRemoteSession = Interop.GetSystemMetrics(Interop.SM_REMOTESESSION) != 0;

            try
            {
                bool enabled = false;
                int hr = Interop.DwmIsCompositionEnabled(out enabled);
                rec.CompositionEnabled = enabled && hr == 0;
            }
            catch { }

            rec.VirtualScreen = new VirtualScreenRecord
            {
                X = Interop.GetSystemMetrics(Interop.SM_XVIRTUALSCREEN),
                Y = Interop.GetSystemMetrics(Interop.SM_YVIRTUALSCREEN),
                Width = Interop.GetSystemMetrics(Interop.SM_CXVIRTUALSCREEN),
                Height = Interop.GetSystemMetrics(Interop.SM_CYVIRTUALSCREEN),
            };

            foreach (var s in Screen.AllScreens)
            {
                var sr = new ScreenRecord
                {
                    DeviceName = s.DeviceName,
                    IsPrimary = s.Primary,
                    X = s.Bounds.X,
                    Y = s.Bounds.Y,
                    Width = s.Bounds.Width,
                    Height = s.Bounds.Height,
                    HasNegativeOrigin = s.Bounds.X < 0 || s.Bounds.Y < 0,
                };
                try
                {
                    var pt = new Interop.POINT
                    {
                        X = s.Bounds.X + s.Bounds.Width / 2,
                        Y = s.Bounds.Y + s.Bounds.Height / 2,
                    };
                    IntPtr hm = MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
                    uint dx = 96, dy = 96;
                    int hr = GetDpiForMonitor(hm, 0 /*MDT_EFFECTIVE_DPI*/, out dx, out dy);
                    if (hr == 0) { sr.DpiX = (int)dx; sr.DpiY = (int)dy; }
                }
                catch { }
                sr.ScaleX = sr.DpiX / 96.0;
                rec.Screens.Add(sr);
            }

            try
            {
                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                    rec.DisplayAdapters.Add($"{dd.DeviceName} | {dd.DeviceString} | {dd.DeviceID}");
                    if (i > 64) break;
                }
            }
            catch { }

            QueryAdvancedColor(rec);
            return rec;
        }

        private static bool IsElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static string GetIntegrityLevel()
        {
            string why = "";
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token))
                    return $"未知(OpenProcessToken 失败 win32={Marshal.GetLastWin32Error()})";
                try
                {
                    // 首次调用用于查询所需缓冲区大小；失败(122=INSUFFICIENT_BUFFER)是预期结果
                    GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
                    if (len <= 0) return $"未知(尺寸查询失败 len={len} win32={Marshal.GetLastWin32Error()})";
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _))
                            return $"未知(GetTokenInformation 失败 win32={Marshal.GetLastWin32Error()})";
                        IntPtr sid = Marshal.ReadIntPtr(buf);
                        if (!ConvertSidToStringSid(sid, out var sidStr)) return "未知(ConvertSidToStringSid 失败)";
                        try
                        {
                            string? s = Marshal.PtrToStringUni(sidStr);
                            if (string.IsNullOrEmpty(s)) return "未知(SID 字符串为空)";
                            int lastDash = s.LastIndexOf('-');
                            // SID 字符串里的子权限是十进制表示（S-1-16-8192 即 0x2000）
                            if (!uint.TryParse(s.Substring(lastDash + 1), System.Globalization.NumberStyles.Integer, null, out uint rid))
                                return $"未知(SID 解析失败 {s})";
                            string name = rid switch
                            {
                                0x0000 => "Untrusted",
                                0x1000 => "Low",
                                0x2000 => "Medium",
                                0x3000 => "High",
                                0x4000 => "System",
                                _ => "未知",
                            };
                            return $"{name}(0x{rid:X4}) SID={s}";
                        }
                        finally { LocalFree(sidStr); }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(token); }
            }
            catch (Exception ex) { return $"未知({ex.GetType().Name}: {ex.Message})"; }
        }

        /// <summary>
        /// 尝试用文档化的 DisplayConfigGetDeviceInfo 读取高级颜色(HDR)状态。
        /// 取不到就如实记为"未取得"，不做任何推测。
        /// </summary>
        private static void QueryAdvancedColor(EnvRecord rec)
        {
            try
            {
                uint pathCount = 0, modeCount = 0;
                int rc = GetDisplayConfigBufferSizes(0x00000002 /*QDC_ONLY_ACTIVE_PATHS*/, out pathCount, out modeCount);
                if (rc != 0) { rec.AdvancedColorNote = $"GetDisplayConfigBufferSizes 失败 rc={rc}"; return; }
                if (pathCount == 0) { rec.AdvancedColorNote = "DisplayConfig 未返回任何显示路径"; return; }

                int pathSize = 72;
                IntPtr buf = Marshal.AllocHGlobal(pathSize * (int)pathCount);
                IntPtr modeBuf = Marshal.AllocHGlobal((int)Math.Max(1, modeCount) * 64);
                try
                {
                    uint numPaths = pathCount, numModes = modeCount;
                    rc = QueryDisplayConfig(0x00000002 /*QDC_ONLY_ACTIVE_PATHS*/, out numPaths, buf, out numModes, modeBuf, IntPtr.Zero);
                    if (rc != 0) { rec.AdvancedColorNote = $"QueryDisplayConfig 失败 rc={rc}"; return; }
                    pathCount = numPaths;

                    for (int i = 0; i < pathCount; i++)
                    {
                        // DISPLAYCONFIG_PATH_INFO: sourceInfo(20) + targetInfo(48) + flags(4)
                        int targetOffset = 20;
                        uint adapterLow = (uint)Marshal.ReadInt32(buf, i * pathSize + targetOffset);
                        int adapterHigh = Marshal.ReadInt32(buf, i * pathSize + targetOffset + 4);
                        uint targetId = (uint)Marshal.ReadInt32(buf, i * pathSize + targetOffset + 8);

                        foreach (int type in new[] { 9 /*GET_ADVANCED_COLOR_INFO*/, 11 /*GET_SDR_WHITE_LEVEL*/ })
                        {
                            var r = QueryColorInfo(adapterLow, adapterHigh, targetId, type);
                            if (r != null) rec.AdvancedColor.Add(r);
                        }
                    }

                    rec.AdvancedColorNote = rec.AdvancedColor.Count == 0
                        ? "DisplayConfigGetDeviceInfo 未返回高级颜色信息（可能为 SDR 或 API 不支持）"
                        : "已读取";
                }
                finally { Marshal.FreeHGlobal(modeBuf); Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex)
            {
                rec.AdvancedColorNote = $"高级颜色查询异常: {ex.Message}";
            }
        }

        private static AdvancedColorRecord? QueryColorInfo(uint adapterLow, int adapterHigh, uint targetId, int type)
        {
            const int totalSize = 32;
            IntPtr mem = Marshal.AllocHGlobal(totalSize + 16);
            try
            {
                for (int i = 0; i < totalSize + 16; i++) Marshal.WriteByte(mem, i, 0);
                Marshal.WriteInt32(mem, 0, type);            // header.type
                Marshal.WriteInt32(mem, 4, totalSize);       // header.size
                Marshal.WriteInt32(mem, 8, unchecked((int)adapterLow)); // header.adapterId.LowPart
                Marshal.WriteInt32(mem, 12, adapterHigh);    // header.adapterId.HighPart
                Marshal.WriteInt32(mem, 16, unchecked((int)targetId));  // header.id

                int rc = DisplayConfigGetDeviceInfo(mem);
                var rec = new AdvancedColorRecord
                {
                    AdapterLuidLow = unchecked((int)adapterLow),
                    AdapterLuidHigh = adapterHigh,
                    TargetId = targetId,
                    DeviceInfoType = type,
                    HResult = rc,
                };
                if (rc == 0)
                {
                    // SDK 布局 (wingdi.h):
                    //   header(20) | union flags(4) | colorEncoding(4) | bitsPerColorChannel(4)  => 32 字节
                    uint flags = (uint)Marshal.ReadInt32(mem, 20);
                    rec.ColorEncoding = (uint)Marshal.ReadInt32(mem, 24);
                    rec.BitsPerColorChannel = (uint)Marshal.ReadInt32(mem, 28);
                    rec.AdvancedColorSupported = (flags & 1) != 0;
                    rec.AdvancedColorEnabled = (flags & 2) != 0;
                    rec.WideColorEnforced = (flags & 4) != 0;
                    return rec;
                }
                return rec; // 记录失败码
            }
            finally { Marshal.FreeHGlobal(mem); }
        }
    }
}
