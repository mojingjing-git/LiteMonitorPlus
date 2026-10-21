using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>一张网卡的快照信息。</summary>
public sealed record NetworkAdapterInfo(
    int IfIndex,
    string FriendlyName,
    string Description,
    bool IsUp,
    NetworkInterfaceType Type,
    IReadOnlyList<IpKey> Addresses);

/// <summary>
/// 把「事件的本地地址」映射到「网卡」。
/// <para>★ C1：ETW 载荷里**没有网卡 ID**，只能靠本地地址映射。归属歧义必须被识别出来，
/// 交给上层**拒绝**，绝不能猜。</para>
/// </summary>
public interface INetworkAdapterAttributor
{
    /// <summary>
    /// 解析本地地址所属的网卡。无法唯一确定（未知地址 / 同一地址出现在多张网卡）时返回 false。
    /// </summary>
    bool TryResolveIfIndex(IpKey localAddress, out int ifIndex);

    /// <summary>该本地地址是否出现在**多张**网卡上（归属歧义）。</summary>
    bool IsAmbiguous(IpKey localAddress);

    /// <summary>该本地地址出现过的全部网卡索引（空 = 未知地址）。</summary>
    IReadOnlyList<int> Candidates(IpKey localAddress);
}

/// <summary>
/// 基于 <c>GetAdaptersAddresses</c> 的本地地址 → 网卡索引映射。
/// <para>失败时退回 <see cref="NetworkInterface"/>（托管 API），功能降级但不会崩。</para>
/// </summary>
public sealed class NetworkAdapterResolver : INetworkAdapterAttributor
{
    private readonly List<NetworkAdapterInfo> _adapters = new();
    private readonly Dictionary<IpKey, List<int>> _byAddress = new();

    private NetworkAdapterResolver() { }

    public IReadOnlyList<NetworkAdapterInfo> Adapters => _adapters;

    public static NetworkAdapterResolver Build()
    {
        var map = new NetworkAdapterResolver();
        try
        {
            map.LoadNative();
        }
        catch
        {
            map._adapters.Clear();
            map._byAddress.Clear();
            map.LoadManaged();
        }
        return map;
    }

    public bool TryResolveIfIndex(IpKey localAddress, out int ifIndex)
    {
        if (_byAddress.TryGetValue(localAddress, out List<int>? hits) && hits.Count == 1)
        {
            ifIndex = hits[0];
            return true;
        }
        ifIndex = -1;
        return false;
    }

    public bool IsAmbiguous(IpKey localAddress)
    {
        return _byAddress.TryGetValue(localAddress, out List<int>? hits) && hits.Count > 1;
    }

    public IReadOnlyList<int> Candidates(IpKey localAddress)
    {
        return _byAddress.TryGetValue(localAddress, out List<int>? hits)
            ? hits
            : Array.Empty<int>();
    }

    /// <summary>取某张网卡上**归属歧义**的本地地址（同一地址还在别的网卡上）。</summary>
    public IReadOnlyList<IpKey> AmbiguousAddressesOf(int ifIndex)
    {
        var result = new List<IpKey>();
        foreach (NetworkAdapterInfo adapter in _adapters)
        {
            if (adapter.IfIndex != ifIndex) { continue; }
            foreach (IpKey a in adapter.Addresses)
            {
                if (IsAmbiguous(a)) { result.Add(a); }
            }
        }
        return result;
    }

    public bool TryGetAdapter(int ifIndex, out NetworkAdapterInfo? info)
    {
        foreach (NetworkAdapterInfo a in _adapters)
        {
            if (a.IfIndex == ifIndex) { info = a; return true; }
        }
        info = null;
        return false;
    }

    // ---------------------------------------------------------------
    // 采集
    // ---------------------------------------------------------------

    private void LoadNative()
    {
        uint size = 16384;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal((int)size);
                uint ret = GetAdaptersAddresses(AF_UNSPEC, 0, IntPtr.Zero, buffer, out uint outSize);
                if (ret == ERROR_BUFFER_OVERFLOW)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    size = outSize + 4096;
                    continue;
                }
                if (ret != 0) { throw new InvalidOperationException("GetAdaptersAddresses failed: " + ret); }
                break;
            }

            IntPtr current = buffer;
            while (current != IntPtr.Zero)
            {
                int ifIndex = Marshal.ReadInt32(current, 4);
                IntPtr next = Marshal.ReadIntPtr(current, 8);
                IntPtr friendlyPtr = Marshal.ReadIntPtr(current, 72);
                IntPtr descPtr = Marshal.ReadIntPtr(current, 64);
                int operStatus = Marshal.ReadInt32(current, 104);
                int ifType = Marshal.ReadInt32(current, 100);

                var addresses = new List<IpKey>();

                IntPtr uni = Marshal.ReadIntPtr(current, 24);
                while (uni != IntPtr.Zero)
                {
                    IntPtr sockaddrPtr = Marshal.ReadIntPtr(uni, 16);
                    int sockaddrLen = Marshal.ReadInt32(uni, 24);
                    if (sockaddrPtr != IntPtr.Zero && sockaddrLen >= 8)
                    {
                        int family = Marshal.ReadInt16(sockaddrPtr, 0);
                        if (family == AF_INET)
                        {
                            addresses.Add(IpKey.FromBytes(false, ReadBytes(sockaddrPtr, 4, 4)));
                        }
                        else if (family == AF_INET6)
                        {
                            addresses.Add(IpKey.FromBytes(true, ReadBytes(sockaddrPtr, 8, 16)));
                        }
                    }
                    uni = Marshal.ReadIntPtr(uni, 8);
                }

                AddAdapter(ifIndex, ReadUnicode(friendlyPtr), ReadUnicode(descPtr), operStatus == 1, MapInterfaceType(ifType), addresses);
                current = next;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
        }
    }

    private void LoadManaged()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var addresses = new List<IpKey>();
            int ifIndex = 0;
            try
            {
                IPInterfaceProperties props = nic.GetIPProperties();
                try { ifIndex = props.GetIPv4Properties().Index; } catch { /* IPv6-only 网卡没有 IPv4 属性 */ }
                foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                {
                    addresses.Add(IpKey.FromIPAddress(u.Address));
                }
            }
            catch { /* 忽略单张网卡的读取失败 */ }

            AddAdapter(ifIndex, nic.Name, nic.Description, nic.OperationalStatus == OperationalStatus.Up,
                nic.NetworkInterfaceType, addresses);
        }
    }

    private void AddAdapter(int ifIndex, string friendly, string description, bool isUp,
        NetworkInterfaceType type, List<IpKey> addresses)
    {
        _adapters.Add(new NetworkAdapterInfo(ifIndex, friendly, description, isUp, type, addresses));
        foreach (IpKey a in addresses)
        {
            if (!_byAddress.TryGetValue(a, out List<int>? list))
            {
                list = new List<int>(1);
                _byAddress[a] = list;
            }
            if (!list.Contains(ifIndex)) { list.Add(ifIndex); }
        }
    }

    private static byte[] ReadBytes(IntPtr p, int offset, int count)
    {
        var b = new byte[count];
        Marshal.Copy(p + offset, b, 0, count);
        return b;
    }

    private static string ReadUnicode(IntPtr p)
    {
        if (p == IntPtr.Zero) { return string.Empty; }
        int len = 0;
        while (Marshal.ReadInt16(p, len * 2) != 0) { len++; }
        if (len == 0) { return string.Empty; }
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) { sb.Append((char)Marshal.ReadInt16(p, i * 2)); }
        return sb.ToString();
    }

    private const uint ERROR_BUFFER_OVERFLOW = 111;
    private const int AF_UNSPEC = 0;
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    /// <summary>把 IFTYPE 映射成托管枚举（取值来自 iphlpapi 的 IF_TYPE_*）。</summary>
    private static NetworkInterfaceType MapInterfaceType(int ifType) => ifType switch
    {
        6 => NetworkInterfaceType.Ethernet,
        71 => NetworkInterfaceType.Wireless80211,
        24 => NetworkInterfaceType.Loopback,
        131 => NetworkInterfaceType.Tunnel,
        23 => NetworkInterfaceType.Ppp,
        _ => NetworkInterfaceType.Unknown,
    };

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetAdaptersAddresses(
        uint family, uint flags, IntPtr reserved, IntPtr adapterAddresses, out uint sizePointer);
}
