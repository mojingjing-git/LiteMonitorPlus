using System.Threading.Tasks;
using LiteMonitor.src.Plugins;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 市场页的资产类别（M4：同一份索引的插件段 / themes 附加段）。
    /// <para>★ F14a：类别是内容签名的第一维度 —— 插件页签与主题页签**分别**缓存。</para>
    /// </summary>
    internal enum MarketCategory { Plugins, Themes }

    /// <summary>
    /// 市场资产在市场页上的安装状态。
    /// <para>★ F14a：0（未安装）是**有效数据**，与"缺失/失败/过期"是三种不同的状态，
    /// 不能互相冒充。</para>
    /// </summary>
    internal enum MarketInstallState
    {
        NotInstalled,  // 未安装
        NeedReinstall, // 记录还在但文件没了（用户手动删了）→ 需重装
        Updatable,     // 已安装且索引里的 sha256 变了 → 可更新
        Installed      // 已安装且是最新版
    }

    /// <summary>
    /// 市场索引来源。
    /// <para>默认实现走插件源网络地址；测试可注入固定索引，使验收用例不依赖网络。</para>
    /// </summary>
    internal interface IMarketIndexProvider
    {
        Task<MarketIndex?> FetchAsync();
    }

    /// <summary>默认索引来源：走 <see cref="PluginMarket.DefaultIndexUrl"/>。</summary>
    internal sealed class NetworkMarketIndexProvider : IMarketIndexProvider
    {
        public async Task<MarketIndex?> FetchAsync()
        {
            return await PluginMarket.FetchIndexAsync(PluginMarket.DefaultIndexUrl).ConfigureAwait(false);
        }
    }
}
