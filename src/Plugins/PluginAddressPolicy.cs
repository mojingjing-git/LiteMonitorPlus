using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// [S02a] 插件请求目标的地址类别。
    /// <para>分类是集中判定的唯一事实来源：调用方不得自行用字符串前缀或
    /// <c>IPAddress.IsLoopback</c> 之类零散判断替代。</para>
    /// </summary>
    internal enum PluginAddressClass
    {
        /// <summary>未指定地址（0.0.0.0、::）。永远禁止作为插件请求目标。</summary>
        Unspecified,

        /// <summary>本机回环（127/8、::1）。</summary>
        Loopback,

        /// <summary>链路本地（169.254/16 含云元数据、fe80::/10）。永远禁止。</summary>
        LinkLocal,

        /// <summary>云元数据端点（169.254.169.254、fd00:ec2::254）。永远禁止，"允许内网"也不放行。</summary>
        CloudMetadata,

        /// <summary>私网/保留（RFC1918、CGNAT、ULA、TEST-NET、组播、NAT64 内嵌私网等）。需用户单独确认本地网络权限。</summary>
        Private,

        /// <summary>公网单播。默认放行（仍受目标声明/授权约束）。</summary>
        Public
    }

    /// <summary>
    /// [S02a] 插件网络地址策略：集中地址分类 + 授权变更版本号。
    /// <para>
    /// 设计口径（docs/plans/2026-10-27 S02a §4.3）：
    /// ① IPv4/IPv6、本机、私网、链路本地、云元数据的分类集中在此，行为测试直接针对本类；
    /// ② 覆盖 v6 映射（::ffff:a.b.c.d）、NAT64（64:ff9b::/96）与 6to4（2002::/16）等
    ///    "v6 外壳包 v4"变体——解包后按内嵌 IPv4 分类，避免以 v6 形态绕过 v4 私网判定；
    /// ③ 分类结果只反映地址本身，"该地址类别是否可达"由预检与授权器判定。
    /// </para>
    /// </summary>
    internal static class PluginAddressPolicy
    {
        /// <summary>
        /// 解析单个 IP 的地址类别。任何合法 IPv4/IPv6 地址都能得到确定类别。
        /// </summary>
        public static PluginAddressClass Classify(IPAddress address)
        {
            // ::ffff:a.b.c.d、64:ff9b::a.b.c.d、2002:a.b.c.d::：v6 外壳内嵌 v4 ⇒ 按内嵌 v4 判。
            if (TryUnwrapEmbeddedIPv4(address, out IPAddress? embedded) && embedded != null)
                address = embedded;

            if (address.AddressFamily == AddressFamily.InterNetwork)
                return ClassifyIPv4(address.GetAddressBytes());
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return ClassifyIPv6(address.GetAddressBytes());

            // 未知地址族（理论上不会出现）：按最保守的私网处理。
            return PluginAddressClass.Private;
        }

        /// <summary>云元数据、链路本地、未指定地址：无论声明或授权如何一律拒绝。</summary>
        public static bool IsForbiddenAlways(PluginAddressClass cls)
            => cls is PluginAddressClass.Unspecified
                    or PluginAddressClass.LinkLocal
                    or PluginAddressClass.CloudMetadata;

        /// <summary>本机回环与私网：需要用户单独确认的"本地网络权限"。</summary>
        public static bool IsLocalScope(PluginAddressClass cls)
            => cls is PluginAddressClass.Loopback or PluginAddressClass.Private;

        private static PluginAddressClass ClassifyIPv4(byte[] b)
        {
            if (b[0] == 0) return PluginAddressClass.Unspecified;               // 0.0.0.0/8
            if (b[0] == 127) return PluginAddressClass.Loopback;                // 127/8
            if (b[0] == 169 && b[1] == 254)
            {
                return b[2] == 169 && b[3] == 254
                    ? PluginAddressClass.CloudMetadata                          // 169.254.169.254（AWS/GCP/Azure IMDS）
                    : PluginAddressClass.LinkLocal;                             // 169.254/16 其余
            }
            if (b[0] == 10) return PluginAddressClass.Private;                  // 10/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return PluginAddressClass.Private;   // 172.16/12
            if (b[0] == 192 && b[1] == 168) return PluginAddressClass.Private;  // 192.168/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return PluginAddressClass.Private;  // 100.64/10 CGNAT
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return PluginAddressClass.Private; // 198.18/15
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return PluginAddressClass.Private;      // 192.0.0.0/24
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return PluginAddressClass.Private;      // TEST-NET-1
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return PluginAddressClass.Private;  // TEST-NET-2
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return PluginAddressClass.Private;   // TEST-NET-3
            if (b[0] >= 224) return PluginAddressClass.Private;                 // 组播 224/4 + 保留 240/4 + 广播
            return PluginAddressClass.Public;
        }

        private static PluginAddressClass ClassifyIPv6(byte[] b)
        {
            bool allZero = true;
            for (int i = 0; i < b.Length; i++) { if (b[i] != 0) { allZero = false; break; } }
            if (allZero) return PluginAddressClass.Unspecified;                 // ::
            bool loopback = b[15] == 1;
            for (int i = 0; i < 15; i++) { if (b[i] != 0) { loopback = false; break; } }
            if (loopback) return PluginAddressClass.Loopback;                   // ::1
            // fd00:ec2::254（AWS IMDS v6）：hextet "ec2"=0x0ec2 → 字节 0e c2；"254"=0x0254 → 字节 02 54。
            if (b[0] == 0xfd && b[1] == 0x00 && b[2] == 0x0e && b[3] == 0xc2 &&
                b[14] == 0x02 && b[15] == 0x54 && b[4..14].All(x => x == 0))
                return PluginAddressClass.CloudMetadata;                        // fd00:ec2::254（AWS IMDS v6）
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return PluginAddressClass.LinkLocal;   // fe80::/10
            if (b[0] == 0xff) return PluginAddressClass.Private;                // ff00::/8 组播
            // fc00::/7 ULA + fec0::/10（已废弃站点本地）+ fe00::/16 保留：一律按私网保守处理。
            // 注意不能用 (b[0] & 0xf0) == 0xf0 表达组播：那会把 0xfe（fec0::/10 等）错吞进来。
            if (b[0] >= 0xf0) return PluginAddressClass.Private;
            return PluginAddressClass.Public;
        }

        private static bool TryUnwrapEmbeddedIPv4(IPAddress address, out IPAddress? embedded)
        {
            embedded = null;
            if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
            byte[] b = address.GetAddressBytes();

            // ::ffff:0:0/96（v4-mapped）
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 &&
                b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0xff && b[11] == 0xff)
            {
                embedded = new IPAddress(new ReadOnlySpan<byte>(b, 12, 4));
                return true;
            }

            // 64:ff9b::/96（NAT64）
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b &&
                b[4..12].All(x => x == 0))
            {
                embedded = new IPAddress(new ReadOnlySpan<byte>(b, 12, 4));
                return true;
            }

            // 2002::/16（6to4：内嵌 v4 在第 2-5 字节）
            if (b[0] == 0x20 && b[1] == 0x02)
            {
                embedded = new IPAddress(new ReadOnlySpan<byte>(b, 2, 4));
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// [S02a] 插件网络授权变更的进程内版本号。
    /// <para>任何授权写入（凭据目标/本地网络/代理确认）都必须调用
    /// <see cref="OnAuthorizationChanged"/>；执行器在取客户端时比对版本号，
    /// 落后即重建连接池并清空响应缓存，保证权限收紧不依赖 60 秒冷却窗口。</para>
    /// </summary>
    internal static class PluginNetworkPolicy
    {
        private static long _version;

        public static long Version => Interlocked.Read(ref _version);

        public static void OnAuthorizationChanged() => Interlocked.Increment(ref _version);
    }
}
