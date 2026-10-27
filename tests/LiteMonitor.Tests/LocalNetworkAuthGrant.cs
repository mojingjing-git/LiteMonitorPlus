using System;
using LiteMonitor.src.Plugins;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [S02a] 测试作用域：在 Dispose 前表示"被测插件实例已获得用户本地网络授权"。
    /// <para>
    /// 既有 HTTP 沙箱/身份/重定向回归测试全部使用 127.0.0.1 回环测试服务器。
    /// S02a 地址预检对本地目标要求用户确认——这些回归测试的本意是验证
    /// 执行器/身份/重定向行为而非授权流，因此统一通过
    /// <see cref="PluginAddressPreflight.LocalAuthOverrideForTests"/> 授权放行；
    /// 预检的地址分类与整组判定逻辑仍完整执行。
    /// 地址预检自身的授权语义测试（PluginAddressPolicyTests）不使用本类，
    /// 走真实授权查询路径。
    /// </para>
    /// </summary>
    internal sealed class LocalNetworkAuthGrant : IDisposable
    {
        public LocalNetworkAuthGrant()
            => PluginAddressPreflight.LocalAuthOverrideForTests = (_, _) => true;

        public void Dispose()
            => PluginAddressPreflight.LocalAuthOverrideForTests = null;
    }

    /// <summary>
    /// [S02a] 测试作用域：Dispose 前插件代理授权检查全部放行。
    /// <para>
    /// 既有沙箱/身份回归通过插件实例发起请求，其中部分步骤模板指定了回环代理；
    /// S02a 要求插件代理经用户确认（实例+端点）。这些测试的本意不是代理授权流，
    /// 统一通过 <see cref="PluginProxyAuthorizer.AuthorizationOverrideForTests"/> 放行；
    /// 代理授权自身的语义测试走真实路径。
    /// </para>
    /// </summary>
    internal sealed class PluginProxyAuthGrant : IDisposable
    {
        public PluginProxyAuthGrant()
            => PluginProxyAuthorizer.AuthorizationOverrideForTests = (_, _) => true;

        public void Dispose()
            => PluginProxyAuthorizer.AuthorizationOverrideForTests = null;
    }

    /// <summary>
    /// [S02a] 测试作用域：Dispose 前表示"指定插件实例已由用户授权指定 origin"
    /// （DynamicHostAuthorizer 的持久化授权被本作用域替代）。
    /// <para>
    /// 语义测试（如"模板变量目标未授权时首跳拒绝"）需要真实授权路径且只授权
    /// 特定 origin，不能走程序集级全放行；本作用域按 (instanceId, origin) 精确注入。
    /// </para>
    /// </summary>
    internal sealed class AuthorizedOriginsGrant : IDisposable
    {
        private readonly Func<string, System.Collections.Generic.IReadOnlyList<string>>? _saved;

        public AuthorizedOriginsGrant(string instanceId, string origin)
        {
            _saved = DynamicHostAuthorizer.AuthorizedOriginsOverrideForTests;
            DynamicHostAuthorizer.AuthorizedOriginsOverrideForTests =
                id => string.Equals(id, instanceId, StringComparison.Ordinal)
                    ? new[] { origin }
                    : Array.Empty<string>();
        }

        public void Dispose()
            => DynamicHostAuthorizer.AuthorizedOriginsOverrideForTests = _saved;
    }
}
