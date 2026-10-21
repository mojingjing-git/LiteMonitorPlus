using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LiteMonitor.src.SystemServices.NetworkTraffic;

/// <summary>传输层协议（首版只覆盖 TCP / UDP 的传输层负载）。</summary>
public enum NetworkTrafficProtocol
{
    Tcp = 1,
    Udp = 2,
}

/// <summary>方向。SEND 的远端 = 目的地址，RECV 的远端 = 源地址。</summary>
public enum NetworkTrafficDirection
{
    Send = 1,
    Receive = 2,
}

/// <summary>
/// 免分配的 IP 地址键。
/// <para>IPv4 存在 <see cref="V4"/>；IPv6 存在 <see cref="V6Hi"/>/<see cref="V6Lo"/>。</para>
/// <para>★ IPv4-mapped IPv6（::ffff:a.b.c.d）会被**归一化**成普通 IPv4，
/// 因此 "::ffff:192.168.1.5" 与 "192.168.1.5" 是同一个匹配目标。</para>
/// </summary>
public readonly struct IpKey : IEquatable<IpKey>
{
    public readonly bool IsV6;
    public readonly uint V4;
    public readonly ulong V6Hi;
    public readonly ulong V6Lo;

    private IpKey(bool isV6, uint v4, ulong hi, ulong lo)
    {
        IsV6 = isV6;
        V4 = v4;
        V6Hi = hi;
        V6Lo = lo;
    }

    public static IpKey FromV4(uint address) => new(false, address, 0, 0);

    public static IpKey FromV6(ulong hi, ulong lo) => new(true, 0, hi, lo);

    /// <summary>
    /// 从**网络字节序**的原始地址字节构造。IPv4 大端读取，IPv6 直接按两个 64 位字读取。
    /// </summary>
    public static IpKey FromBytes(bool isV6, ReadOnlySpan<byte> bytes)
    {
        if (isV6)
        {
            if (bytes.Length < 16) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
            ulong hi = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            ulong lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8));
            // IPv4-mapped（::ffff:a.b.c.d）归一化成普通 IPv4。
            // 字节布局：00×10 | FF FF | a b c d  ⇒ 前 8 字节为 0，lo 的高 32 位为 0xFFFF。
            if (hi == 0UL && (lo >> 32) == 0x0000FFFFUL)
            {
                return FromV4((uint)(lo & 0xFFFFFFFFUL));
            }
            return FromV6(hi, lo);
        }
        if (bytes.Length < 4) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
        // ETW 载荷里的 IPv4 是网络字节序（大端）。
        return FromV4(BinaryPrimitives.ReadUInt32BigEndian(bytes));
    }

    /// <summary>解析文本形式的 IPv4/IPv6（**不做名字解析**）。</summary>
    public static IpKey Parse(string text)
    {
        if (!IPAddress.TryParse(text, out IPAddress? parsed))
        {
            throw new FormatException("Not an IP address: " + text);
        }
        return FromIPAddress(parsed);
    }

    /// <summary>不抛异常的解析；失败返回 false。</summary>
    public static bool TryParse(string text, out IpKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) { return false; }
        if (!IPAddress.TryParse(text.Trim(), out IPAddress? parsed)) { return false; }
        key = FromIPAddress(parsed);
        return true;
    }

    public static IpKey FromIPAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            address.TryWriteBytes(b, out _);
            return FromV4(BinaryPrimitives.ReadUInt32BigEndian(b));
        }
        Span<byte> v6 = stackalloc byte[16];
        address.TryWriteBytes(v6, out _);
        return FromBytes(true, v6);
    }

    public IPAddress ToIPAddress()
    {
        if (!IsV6)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, V4);
            return new IPAddress(b);
        }
        Span<byte> b6 = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(b6, V6Hi);
        BinaryPrimitives.WriteUInt64BigEndian(b6.Slice(8), V6Lo);
        return new IPAddress(b6);
    }

    /// <summary>True 表示 IPv4 224/4 或 IPv6 ff00::/8。</summary>
    public bool IsMulticast
    {
        get
        {
            if (!IsV6) { return (V4 & 0xF0000000u) == 0xE0000000u; }
            return (V6Hi & 0xFF00000000000000UL) == 0xFF00000000000000UL;
        }
    }

    /// <summary>True 表示 127/8 与 ::1。</summary>
    public bool IsLoopback
    {
        get
        {
            if (!IsV6) { return (V4 & 0xFF000000u) == 0x7F000000u; }
            return V6Hi == 0 && V6Lo == 1;
        }
    }

    /// <summary>True 表示链路本地地址（IPv4 169.254/16 或 IPv6 fe80::/10）。</summary>
    public bool IsLinkLocal
    {
        get
        {
            if (!IsV6) { return (V4 & 0xFFFF0000u) == 0xA9FE0000u; }
            return (V6Hi & 0xFFC0000000000000UL) == 0xFE80000000000000UL;
        }
    }

    public bool Equals(IpKey other) =>
        IsV6 == other.IsV6 &&
        (IsV6 ? (V6Hi == other.V6Hi && V6Lo == other.V6Lo) : V4 == other.V4);

    public override bool Equals(object? obj) => obj is IpKey k && Equals(k);

    public override int GetHashCode() => IsV6 ? HashCode.Combine(true, V6Hi, V6Lo) : HashCode.Combine(false, V4);

    public override string ToString() => ToIPAddress().ToString();
}
