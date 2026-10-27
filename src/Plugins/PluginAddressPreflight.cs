using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// [S02a] 连接前地址预检：在 HTTP 请求实际发出前解析目标主机并按
    /// <see cref="PluginAddressPolicy"/> 判定整组候选地址。
    /// <para>
    /// 口径（docs/plans/2026-10-27 S02a §4.3）：
    /// ① 主机名解析出的**整组**候选地址一起判定——只要任一候选落入"永远禁止"
    ///    类别（云元数据/链路本地/未指定），整组拒绝；任一候选落在本机/私网且无
    ///    本地网络授权，整组拒绝。不允许"公网候选存在就放行"的部分判定；
    /// ② IP 字面量目标直接分类，即使请求走代理也执行（代理请求仍做 IP 字面量检查）；
    /// ③ 走代理的主机名目标不做本地 DNS 解析——解析发生在代理侧，本地结果既不代表
    ///    终点也可能误伤企业代理场景；本地能解析成功时结果与直连一致（终点 IP 是否
    ///    与预检一致属于 S02b 的连接绑定范畴，本类不承诺）；
    /// ④ 解析失败按"目标不可解析"拒绝，不静默放行；
    /// ⑤ 本预检与实际连接之间存在时间差（DNS 记录可能变化），不构成完整的
    ///    DNS 重绑定防护——发布表述不得超出"连接前缓解"。
    /// </para>
    /// </summary>
    internal static class PluginAddressPreflight
    {
        /// <summary>测试接缝：非空时替代真实 DNS 解析（返回整组候选地址）。</summary>
        internal static Func<string, CancellationToken, Task<IPAddress[]>>? ResolverForTests;

        /// <summary>测试接缝：非空时替代 <see cref="PluginLocalNetworkAuthorizer.Default"/> 查询本地网络授权。</summary>
        internal static PluginLocalNetworkAuthorizer? LocalAuthorizerForTests;

        /// <summary>
        /// 测试接缝：非空时以该谓词替代整个本地网络授权查询
        /// （参数 instanceId、目标 origin）。供既有的 HTTP 沙箱/身份/重定向
        /// 回归测试表示"实例已被用户确认本地网络权限"，预检的地址分类与
        /// 整组判定逻辑仍完整执行。
        /// </summary>
        internal static Func<string?, Uri, bool>? LocalAuthOverrideForTests;

        /// <summary>测试观察：解析调用次数（含 IP 字面量路径为 0 次的断言）。</summary>
        internal static long ResolveCallCount;

        /// <summary>
        /// 判定一个跳（首跳或重定向跳）的目标地址策略。
        /// 未通过时抛出 <see cref="PluginSandboxException"/>，请求在发送前中止。
        /// </summary>
        public static async Task EnsureAddressPolicyAsync(
            Uri target, string? proxy, string? instanceId, CancellationToken token)
        {
            string host = target.IdnHost;

            // IP 字面量：直接分类，不经过 DNS（代理与否都判——IP 字面量不依赖解析）。
            if (IPAddress.TryParse(host, out IPAddress? literal))
            {
                Judge(new[] { literal }, host, instanceId, target);
                return;
            }

            // 走代理的主机名：解析在代理侧，本地解析失败不能断网（§4.3.7）。
            if (!string.IsNullOrEmpty(proxy))
                return;

            IPAddress[] addresses = await ResolveAsync(host, token).ConfigureAwait(false);
            Judge(addresses, host, instanceId, target);
        }

        private static void Judge(IPAddress[] addresses, string host, string? instanceId, Uri target)
        {
            if (addresses is null || addresses.Length == 0)
                throw new PluginSandboxException(
                    PluginSandboxReasons.TargetUnresolvable,
                    $"目标主机 {host} 未解析到任何地址，请求未发出。");

            PluginAddressClass[] classes = addresses
                .Select(PluginAddressPolicy.Classify)
                .Distinct()
                .ToArray();

            if (classes.Any(PluginAddressPolicy.IsForbiddenAlways))
                throw new PluginSandboxException(
                    PluginSandboxReasons.AddressPolicyDenied,
                    $"目标 {host} 解析到禁止访问的地址类别（云元数据/链路本地/未指定地址），请求未发出。");

            if (classes.Any(PluginAddressPolicy.IsLocalScope))
            {
                bool authorized;
                if (LocalAuthOverrideForTests != null)
                {
                    authorized = LocalAuthOverrideForTests(instanceId, target);
                }
                else
                {
                    PluginLocalNetworkAuthorizer localAuthorizer =
                        LocalAuthorizerForTests ?? PluginLocalNetworkAuthorizer.Default;
                    authorized = !string.IsNullOrWhiteSpace(instanceId) &&
                                 localAuthorizer.IsLocalAuthorized(instanceId, target);
                }
                if (!authorized)
                {
                    throw new PluginSandboxException(
                        PluginSandboxReasons.LocalNetworkNotAllowed,
                        $"目标 {host} 解析到本机或私网地址，该插件实例未经用户确认本地网络权限，请求未发出。");
                }
            }

            // 整组均为公网 → 通过（仍受目标声明/授权与重定向策略约束）。
        }

        private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
        {
            Interlocked.Increment(ref ResolveCallCount);
            try
            {
                if (ResolverForTests != null)
                    return await ResolverForTests(host, token).ConfigureAwait(false);

                return await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消是正常流程，原样上抛，不伪装成"不可解析"。
            }
            catch (Exception ex) when (ex is SocketException)
            {
                // DNS 解析失败（域不存在/临时故障）。其他异常类型不在此翻译，
                // 避免 NRE 之类的程序缺陷被伪装成"目标不可解析"。
                throw new PluginSandboxException(
                    PluginSandboxReasons.TargetUnresolvable,
                    $"目标主机 {host} 解析失败（{ex.Message}），请求未发出。");
            }
        }
    }
}
