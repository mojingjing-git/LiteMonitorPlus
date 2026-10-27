using System.Net;
using System.Runtime.CompilerServices;
using LiteMonitor.src.Plugins;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [S02a 集成] 测试程序集统一放行「本地网络授权」与「插件代理授权」两个
    /// 用户确认闸门：沙箱/市场/主题回归普遍使用 127.0.0.1 回环测试服务器，
    /// 逐类加授权作用域不可维护（全量回归里失败数随执行顺序波动）。
    ///
    /// 同时固定预检解析结果：沙箱网络环境的 DNS 会把任意域名拦到链路本地地址
    /// （fe80::1），真实解析在传输层 mock 下既无意义也不稳定；地址分类、整组判定、
    /// 云元数据/链路本地拒绝在 <see cref="PluginAddressPreflight"/> 里仍完整执行。
    ///
    /// 需要真实授权路径或真实解析语义的测试（PluginAddressPolicyTests）显式自护——
    /// 保存 / 置空 / finally 恢复这些接缝。
    /// </summary>
    internal static class TestModuleInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            PluginAddressPreflight.LocalAuthOverrideForTests = (_, _) => true;
            PluginProxyAuthorizer.AuthorizationOverrideForTests = (_, _) => true;
            PluginAddressPreflight.ResolverForTests = (_, _) =>
                Task.FromResult(new IPAddress[] { IPAddress.Parse("1.1.1.1") });
        }
    }
}
