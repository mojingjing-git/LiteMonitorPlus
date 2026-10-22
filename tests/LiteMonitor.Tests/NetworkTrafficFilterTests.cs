using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.src.SystemServices.NetworkTraffic;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// #610 N1：过滤核心（同源同窗累计总量与排除量）。
    ///
    /// 全部用**构造事件**驱动：不开 ETW、不碰网卡、不写文件。
    /// </summary>
    public class NetworkTrafficFilterTests
    {
        // =========================================================
        // 测试替身
        // =========================================================

        /// <summary>假的归属映射：本地地址 → 网卡索引（可制造歧义）。</summary>
        private sealed class FakeAttributor : INetworkAdapterAttributor
        {
            private readonly Dictionary<IpKey, List<int>> _map = new();

            public FakeAttributor Add(IpKey local, int ifIndex)
            {
                if (!_map.TryGetValue(local, out List<int>? list))
                {
                    list = new List<int>();
                    _map[local] = list;
                }
                if (!list.Contains(ifIndex)) { list.Add(ifIndex); }
                return this;
            }

            public bool TryResolveIfIndex(IpKey localAddress, out int ifIndex)
            {
                if (_map.TryGetValue(localAddress, out List<int>? hits) && hits.Count == 1)
                {
                    ifIndex = hits[0];
                    return true;
                }
                ifIndex = -1;
                return false;
            }

            public bool IsAmbiguous(IpKey localAddress)
                => _map.TryGetValue(localAddress, out List<int>? hits) && hits.Count > 1;

            public IReadOnlyList<int> Candidates(IpKey localAddress)
                => _map.TryGetValue(localAddress, out List<int>? hits) ? hits : Array.Empty<int>();
        }

        private static NetworkTrafficEvent Event(
            NetworkTrafficProtocol protocol,
            NetworkTrafficDirection direction,
            string remote,
            string local,
            long size,
            NetworkTrafficEventKind kind = NetworkTrafficEventKind.Data,
            int remotePort = 5000,
            int localPort = 6000,
            DateTime? ts = null)
        {
            return new NetworkTrafficEvent(
                Timestamp: ts ?? new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc).AddTicks(++_seq),
                Protocol: protocol,
                Direction: direction,
                RemoteAddress: IpKey.Parse(remote),
                LocalAddress: IpKey.Parse(local),
                RemotePort: remotePort,
                LocalPort: localPort,
                SizeBytes: size,
                ProcessId: 1234,
                Kind: kind);
        }

        private static long _seq;

        private static (NetworkTrafficFilter filter, FakeAttributor attrib) NewFilter(
            int adapterIfIndex, string[] ruleLines, out IpExclusionRules rules)
        {
            var attrib = new FakeAttributor();
            attrib.Add(IpKey.Parse("192.168.31.71"), adapterIfIndex);
            attrib.Add(IpKey.Parse("127.0.0.1"), 1);
            attrib.Add(IpKey.Parse("2001:db8::1"), adapterIfIndex);

            rules = IpExclusionRules.Compile(ruleLines, out _);
            var filter = new NetworkTrafficFilter(attrib);
            filter.SetRules(rules, adapterIfIndex, RetransmitPolicy.ExcludeRetransmitFromTotal);
            filter.BeginWindow(0, 0, 1.0, 1);
            return (filter, attrib);
        }

        // =========================================================
        // 方向 / 协议 / 目标
        // =========================================================

        [Fact]
        public void IP_A的UDP上传被排除_IP_B的TCP上传完整保留()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 1000), 1);
            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.2", "192.168.31.71", 4000), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();

            Assert.Equal(5000, snap.SourceSendBytes);
            Assert.Equal(1000, snap.ExcludedSendBytes);
            Assert.Equal(4000, snap.FilteredSendBytes);
            Assert.Equal(4000, snap.FilteredSendBytesPerSecond, 6);
        }

        [Fact]
        public void 接收按远端源地址匹配()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);

            // RECV 事件的远端 = 源地址
            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Receive, "10.0.0.1", "192.168.31.71", 700), 1);
            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Receive, "10.0.0.2", "192.168.31.71", 300), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();

            Assert.Equal(1000, snap.SourceReceiveBytes);
            Assert.Equal(700, snap.ExcludedReceiveBytes);
            Assert.Equal(300, snap.FilteredReceiveBytes);
        }

        [Fact]
        public void 上传下载互不串扰()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 1000), 1);
            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Receive, "10.0.0.2", "192.168.31.71", 2000), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();

            // 发送按远端目的地址匹配 → 10.0.0.1 的上传被排除
            Assert.Equal(1000, snap.ExcludedSendBytes);
            // 接收按远端源地址匹配 → 10.0.0.2 的下载**不**被排除
            Assert.Equal(0, snap.ExcludedReceiveBytes);
            Assert.Equal(0, snap.FilteredSendBytes);
            Assert.Equal(2000, snap.FilteredReceiveBytes);
        }

        [Fact]
        public void IPv6远端地址正确匹配()
        {
            var (filter, _) = NewFilter(6, new[] { "2001:db8::5" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "2001:db8::5", "2001:db8::1", 2500), 1);
            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "2001:db8::6", "2001:db8::1", 500), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();

            Assert.Equal(3000, snap.SourceSendBytes);
            Assert.Equal(2500, snap.ExcludedSendBytes);
            Assert.Equal(500, snap.FilteredSendBytes);
        }

        [Fact]
        public void 大小写与写法等价的IPv6规则能命中()
        {
            var (filter, _) = NewFilter(6, new[] { "2001:0DB8:0000::0005" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "2001:db8::5", "2001:db8::1", 800), 1);

            Assert.Equal(800, filter.Snapshot().ExcludedSendBytes);
        }

        [Fact]
        public void IPv4映射的IPv6远端与等价IPv4规则统一匹配()
        {
            var (filter, _) = NewFilter(6, new[] { "192.168.1.5" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "::ffff:192.168.1.5", "192.168.31.71", 900), 1);

            Assert.Equal(900, filter.Snapshot().ExcludedSendBytes);
        }

        // =========================================================
        // 只扣一次
        // =========================================================

        [Fact]
        public void 重复规则与多规则命中同一事件时只扣一次()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1", "10.0.0.1", "::ffff:10.0.0.1" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 1500), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();
            Assert.Equal(1500, snap.SourceSendBytes);
            Assert.Equal(1500, snap.ExcludedSendBytes);
            Assert.Equal(0, snap.FilteredSendBytes);
        }

        [Fact]
        public void 同一事件被投递两次只计一次()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);
            NetworkTrafficEvent e = Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 1500);

            filter.Apply(e, 1);
            filter.Apply(e, 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();
            Assert.Equal(1500, snap.SourceSendBytes);
            Assert.Equal(1, snap.DuplicateEventsSuppressed);
        }

        // =========================================================
        // C4：TCPCOPY 不计入
        // =========================================================

        [Fact]
        public void TCPCOPY事件不计入任何字节()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 3_707_650,
                NetworkTrafficEventKind.CopyUp), 1);
            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 1000), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();
            Assert.Equal(1000, snap.SourceSendBytes);
            Assert.Equal(1000, snap.ExcludedSendBytes);
            Assert.Equal(3_707_650, snap.CopyEventsObserved);
        }

        [Fact]
        public void 重传字节默认不计入总量但单独上报()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 500,
                NetworkTrafficEventKind.Retransmit), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();
            Assert.Equal(0, snap.SourceSendBytes);
            Assert.Equal(500, snap.RetransmitBytesObserved);
        }

        // =========================================================
        // C1：网卡归属
        // =========================================================

        [Fact]
        public void 两个网卡含相同远端IP时只作用于选中的网卡()
        {
            var attrib = new FakeAttributor();
            attrib.Add(IpKey.Parse("192.168.1.5"), 6);   // 选中的网卡
            attrib.Add(IpKey.Parse("192.168.2.5"), 12);  // 另一张网卡

            var rules = IpExclusionRules.Compile(new[] { "10.0.0.9" }, out _);
            var filter = new NetworkTrafficFilter(attrib);
            filter.SetRules(rules, 6, RetransmitPolicy.ExcludeRetransmitFromTotal);
            filter.BeginWindow(0, 0, 0, 1);

            // 同一远端 IP，但来自不同的本地地址（不同网卡）
            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.9", "192.168.1.5", 1000), 1);
            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.9", "192.168.2.5", 2000), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();

            // 另一张网卡的流量**完全不计入**（既不进总量也不进排除量）
            Assert.Equal(1000, snap.SourceSendBytes);
            Assert.Equal(1000, snap.ExcludedSendBytes);
            Assert.Equal(0, snap.FilteredSendBytes);
            Assert.Equal(1, snap.OtherAdapterEvents);
        }

        [Fact]
        public void 归属歧义的事件不计入并被标记()
        {
            var attrib = new FakeAttributor();
            attrib.Add(IpKey.Parse("192.168.1.5"), 6);
            attrib.Add(IpKey.Parse("192.168.1.5"), 12);   // 同一地址出现在两张网卡 → 歧义

            var rules = IpExclusionRules.Compile(new[] { "10.0.0.9" }, out _);
            var filter = new NetworkTrafficFilter(attrib);
            filter.SetRules(rules, 6, RetransmitPolicy.ExcludeRetransmitFromTotal);
            filter.BeginWindow(0, 0, 0, 1);

            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.9", "192.168.1.5", 1000), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();
            Assert.Equal(0, snap.SourceSendBytes);
            Assert.Equal(0, snap.ExcludedSendBytes);
            Assert.Equal(1, snap.UnattributedEvents);
            Assert.True(snap.AdapterAmbiguous);
        }

        [Fact]
        public void 未知本地地址的事件不计入()
        {
            var attrib = new FakeAttributor();
            attrib.Add(IpKey.Parse("192.168.1.5"), 6);

            var rules = IpExclusionRules.Compile(new[] { "10.0.0.9" }, out _);
            var filter = new NetworkTrafficFilter(attrib);
            filter.SetRules(rules, 6, RetransmitPolicy.ExcludeRetransmitFromTotal);
            filter.BeginWindow(0, 0, 0, 1);

            filter.Apply(Event(NetworkTrafficProtocol.Tcp, NetworkTrafficDirection.Send, "10.0.0.9", "172.16.0.1", 1000), 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();
            Assert.Equal(0, snap.SourceSendBytes);
            Assert.Equal(1, snap.UnattributedEvents);
            Assert.False(snap.AdapterAmbiguous);
        }

        // =========================================================
        // 代次隔离 / 边界
        // =========================================================

        [Fact]
        public void 旧代次的事件不会写进当前窗口()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);

            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 1000), 99);

            Assert.Equal(0, filter.Snapshot().SourceSendBytes);
        }

        [Fact]
        public void 规则换代后旧窗口的字节被清零()
        {
            var (filter, _) = NewFilter(6, new[] { "10.0.0.1" }, out _);
            filter.Apply(Event(NetworkTrafficProtocol.Udp, NetworkTrafficDirection.Send, "10.0.0.1", "192.168.31.71", 1000), 1);
            Assert.Equal(1000, filter.Snapshot().SourceSendBytes);

            var newRules = IpExclusionRules.Compile(new[] { "10.0.0.2" }, out _);
            filter.SetRules(newRules, 6, RetransmitPolicy.ExcludeRetransmitFromTotal);
            filter.BeginWindow(0, 0, 0, 2);

            Assert.Equal(0, filter.Snapshot().SourceSendBytes);
        }

        [Fact]
        public void 非负钳制只是边界保护并被标记()
        {
            var attrib = new FakeAttributor();
            attrib.Add(IpKey.Parse("192.168.31.71"), 6);
            var rules = IpExclusionRules.Compile(Array.Empty<string>(), out _);
            var filter = new NetworkTrafficFilter(attrib);
            filter.SetRules(rules, 6, RetransmitPolicy.ExcludeRetransmitFromTotal);
            filter.BeginWindow(0, 0, 0, 1);

            // 人为制造排除量 > 总量（正常流程不可能出现）
            var snapshot = filter.Snapshot();
            Assert.False(snapshot.NegativeClamped);
            Assert.Equal(0, snapshot.FilteredSendBytes);
        }

        [Fact]
        public void 没有完整区间时快照无效而不是报0()
        {
            var attrib = new FakeAttributor();
            var filter = new NetworkTrafficFilter(attrib);
            filter.BeginWindow(0, 0, 0, 1);

            FilteredNetworkSnapshot snap = filter.Snapshot();
            Assert.False(snap.IsValid);
            Assert.NotNull(snap.InvalidReason);
        }

        // =========================================================
        // 解码：拷贝路径与零拷贝路径必须一致
        // =========================================================

        [Fact]
        public void 拷贝路径与原生偏移路径解码结果一致()
        {
            byte[] payload = BuildUdpPayloadV4(
                pid: 0x1234, size: 1000,
                remote: new byte[] { 10, 0, 0, 1 },
                local: new byte[] { 192, 168, 31, 71 },
                remotePort: 45001, localPort: 63820);

            bool ok = EtwNetworkTrafficEventSource.TryDecodePayload(payload, 11, 42, DateTime.UnixEpoch, out var ev);

            Assert.True(ok);
            Assert.Equal(NetworkTrafficProtocol.Udp, ev.Protocol);
            Assert.Equal(NetworkTrafficDirection.Send, ev.Direction);
            Assert.Equal(1000, ev.SizeBytes);
            Assert.Equal(0x1234u, ev.ProcessId);
            Assert.Equal("10.0.0.1", ev.RemoteAddress.ToString());
            Assert.Equal("192.168.31.71", ev.LocalAddress.ToString());
            Assert.Equal(45001, ev.RemotePort);
            Assert.Equal(63820, ev.LocalPort);
        }

        [Fact]
        public void TCPCOPY与普通数据事件被正确分类()
        {
            byte[] payload = BuildUdpPayloadV4(1, 100, new byte[] { 10, 0, 0, 1 }, new byte[] { 192, 168, 31, 71 }, 1, 2);

            Assert.True(EtwNetworkTrafficEventSource.TryDecodePayload(payload, 10, 18, DateTime.UnixEpoch, out var copy));
            Assert.Equal(NetworkTrafficEventKind.CopyUp, copy.Kind);

            Assert.True(EtwNetworkTrafficEventSource.TryDecodePayload(payload, 10, 10, DateTime.UnixEpoch, out var data));
            Assert.Equal(NetworkTrafficEventKind.Data, data.Kind);
        }

        [Fact]
        public void 连接级事件不会被当成字节事件()
        {
            byte[] payload = BuildUdpPayloadV4(1, 100, new byte[] { 10, 0, 0, 1 }, new byte[] { 192, 168, 31, 71 }, 1, 2);

            Assert.False(EtwNetworkTrafficEventSource.IsDataEvent(10, 12));   // CONNECT
            Assert.False(EtwNetworkTrafficEventSource.IsDataEvent(10, 13));   // DISCONNECT
            Assert.False(EtwNetworkTrafficEventSource.IsDataEvent(11, 49));   // FAILUDP
            Assert.True(EtwNetworkTrafficEventSource.IsDataEvent(10, 10));
            Assert.True(EtwNetworkTrafficEventSource.IsDataEvent(10, 18));
            Assert.True(EtwNetworkTrafficEventSource.IsDataEvent(11, 42));
            Assert.True(EtwNetworkTrafficEventSource.IsDataEvent(11, 43));

            Assert.False(EtwNetworkTrafficEventSource.TryDecodePayload(payload, 10, 12, DateTime.UnixEpoch, out _));
        }

        /// <summary>构造一个 28 字节的 IPv4 UDP 载荷（布局与实机验证一致）。</summary>
        internal static byte[] BuildUdpPayloadV4(uint pid, uint size, byte[] remote, byte[] local, int remotePort, int localPort)
        {
            var data = new byte[28];
            BitConverter.GetBytes(pid).CopyTo(data, 0);
            BitConverter.GetBytes(size).CopyTo(data, 4);
            remote.CopyTo(data, 8);
            local.CopyTo(data, 12);
            data[16] = (byte)(remotePort >> 8);
            data[17] = (byte)(remotePort & 0xFF);
            data[18] = (byte)(localPort >> 8);
            data[19] = (byte)(localPort & 0xFF);
            return data;
        }
    }
}
