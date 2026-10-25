using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 网卡快照：<see cref="NetworkManager"/> 需要的**最小**信息集。
    /// <para>★ 抽象出来是为了让"热插拔 / Wi-Fi↔有线切换 / 双网卡并存"这些场景
    /// 可以在单元测试里被真实驱动（假网卡），而不是只能靠真实硬件碰运气。</para>
    /// </summary>
    public interface INetworkAdapterSnapshot
    {
        /// <summary>连接名（<c>NetworkInterface.Name</c>）。</summary>
        string Name { get; }

        /// <summary>硬件描述（<c>NetworkInterface.Description</c>）。</summary>
        string Description { get; }

        /// <summary>链路是否为 Up。</summary>
        bool IsOperationalUp { get; }

        /// <summary>是否为回环网卡。</summary>
        bool IsLoopback { get; }

        /// <summary>IPv4 接口索引（没有则 null；IPv6-only 网卡取不到）。</summary>
        int? IfIndex { get; }

        /// <summary>该网卡上的 IPv4 地址列表（没有则空列表）。</summary>
        IReadOnlyList<string> GetIPv4Addresses();

        /// <summary>取累计收发字节数（不可用时返回 false）。</summary>
        bool TryGetTrafficBytes(out long bytesSent, out long bytesReceived);
    }

    /// <summary>
    /// 网卡枚举源：生产实现走 <see cref="NetworkInterface"/>，测试注入假实现。
    /// </summary>
    public interface INetworkAdapterSource
    {
        /// <summary>枚举当前所有网卡。</summary>
        IReadOnlyList<INetworkAdapterSnapshot> GetAdapters();
    }

    /// <summary>生产实现：真实枚举系统网卡。</summary>
    public sealed class SystemNetworkAdapterSource : INetworkAdapterSource
    {
        /// <summary>进程级共享实例。</summary>
        public static SystemNetworkAdapterSource Instance { get; } = new SystemNetworkAdapterSource();

        /// <inheritdoc />
        public IReadOnlyList<INetworkAdapterSnapshot> GetAdapters()
        {
            var result = new List<INetworkAdapterSnapshot>();
            NetworkInterface[] nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return result;   // 枚举失败：当作"没有网卡"，由调用方区分失败状态
            }

            foreach (NetworkInterface nic in nics)
            {
                result.Add(new SystemNetworkAdapterSnapshot(nic));
            }
            return result;
        }
    }

    /// <summary>真实网卡的一次快照（包装 <see cref="NetworkInterface"/>）。</summary>
    internal sealed class SystemNetworkAdapterSnapshot : INetworkAdapterSnapshot
    {
        private readonly NetworkInterface _nic;

        public SystemNetworkAdapterSnapshot(NetworkInterface nic)
        {
            _nic = nic;
        }

        public string Name => _nic.Name;

        public string Description => _nic.Description;

        public bool IsOperationalUp => _nic.OperationalStatus == OperationalStatus.Up;

        public bool IsLoopback => _nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;

        public int? IfIndex
        {
            get
            {
                try
                {
                    int index = _nic.GetIPProperties().GetIPv4Properties().Index;
                    return index > 0 ? index : (int?)null;
                }
                catch
                {
                    return null;   // IPv6-only 网卡
                }
            }
        }

        public IReadOnlyList<string> GetIPv4Addresses()
        {
            var result = new List<string>();
            try
            {
                foreach (UnicastIPAddressInformation ip in _nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    result.Add(ip.Address.ToString());
                }
            }
            catch
            {
                // 取不到就当作没有地址（调用方会标成 Missing，而不是 Failed）。
            }
            return result;
        }

        public bool TryGetTrafficBytes(out long bytesSent, out long bytesReceived)
        {
            bytesSent = 0;
            bytesReceived = 0;
            try
            {
                IPInterfaceStatistics stats = _nic.GetIPStatistics();
                bytesSent = stats.BytesSent;
                bytesReceived = stats.BytesReceived;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
