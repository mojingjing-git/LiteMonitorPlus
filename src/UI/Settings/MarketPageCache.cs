using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.UI.Controls;

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

    /// <summary>
    /// 市场页**内容签名**（F14a）。
    ///
    /// <para>签名相同的两次进入 ⇒ 页面**不销毁、不创建**任何列表控件（缓存命中）。
    /// 任何一个维度变化 ⇒ 只更新受影响的那部分（行级差量），不做全量重建。</para>
    ///
    /// <para>八个维度（与母计划 F14-A 第 1 条一一对应）：</para>
    /// <list type="number">
    /// <item>资产类型（插件页签 / 主题页签）</item>
    /// <item>索引版本/摘要（当前页签那一段的内容摘要）</item>
    /// <item>安装状态（当前页签全部资产的安装状态摘要）</item>
    /// <item>选中项（当前选中的主题 id）</item>
    /// <item>语言（<see cref="LanguageManager.CurrentLang"/>）</item>
    /// <item>DPI（控件 DeviceDpi + 全局缩放因子）</item>
    /// <item>设置配色（<see cref="UIColors"/> 全部取值）</item>
    /// <item>请求代次（当前**已呈现内容**的代次；每应用一份新索引快照自增）</item>
    /// </list>
    /// </summary>
    internal readonly struct MarketContentSignature : IEquatable<MarketContentSignature>
    {
        public MarketContentSignature(
            MarketCategory category,
            string indexDigest,
            string installStateDigest,
            string selectedItemId,
            string languageTag,
            int dpiKey,
            string paletteDigest,
            int requestGeneration)
        {
            Category = category;
            IndexDigest = indexDigest ?? string.Empty;
            InstallStateDigest = installStateDigest ?? string.Empty;
            SelectedItemId = selectedItemId ?? string.Empty;
            LanguageTag = languageTag ?? string.Empty;
            DpiKey = dpiKey;
            PaletteDigest = paletteDigest ?? string.Empty;
            RequestGeneration = requestGeneration;
        }

        /// <summary>资产类型（插件 / 主题）。</summary>
        public MarketCategory Category { get; }

        /// <summary>索引版本/摘要。</summary>
        public string IndexDigest { get; }

        /// <summary>安装状态摘要。</summary>
        public string InstallStateDigest { get; }

        /// <summary>选中项（主题 id；插件页签为空）。</summary>
        public string SelectedItemId { get; }

        /// <summary>语言标识。</summary>
        public string LanguageTag { get; }

        /// <summary>DPI 键（DeviceDpi × 1000 + 缩放因子 × 100）。</summary>
        public int DpiKey { get; }

        /// <summary>设置配色摘要。</summary>
        public string PaletteDigest { get; }

        /// <summary>请求代次（已呈现内容的代次）。</summary>
        public int RequestGeneration { get; }

        public bool Equals(MarketContentSignature other)
        {
            return Category == other.Category
                   && string.Equals(IndexDigest, other.IndexDigest, StringComparison.Ordinal)
                   && string.Equals(InstallStateDigest, other.InstallStateDigest, StringComparison.Ordinal)
                   && string.Equals(SelectedItemId, other.SelectedItemId, StringComparison.Ordinal)
                   && string.Equals(LanguageTag, other.LanguageTag, StringComparison.Ordinal)
                   && DpiKey == other.DpiKey
                   && string.Equals(PaletteDigest, other.PaletteDigest, StringComparison.Ordinal)
                   && RequestGeneration == other.RequestGeneration;
        }

        public override bool Equals(object? obj) => obj is MarketContentSignature other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)Category;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(IndexDigest);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(InstallStateDigest);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(SelectedItemId);
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(LanguageTag);
                hash = hash * 31 + DpiKey;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(PaletteDigest);
                hash = hash * 31 + RequestGeneration;
                return hash;
            }
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "cat={0} idx={1} state={2} sel={3} lang={4} dpi={5} pal={6} gen={7}",
                Category, IndexDigest, InstallStateDigest, SelectedItemId, LanguageTag, DpiKey, PaletteDigest, RequestGeneration);
        }
    }

    /// <summary>
    /// 市场页签名/摘要计算工具（F14a）。
    /// <para>★ 只做**纯计算**：不碰控件、不碰文件、不碰网络 ⇒ 可单测、可复用。</para>
    /// </summary>
    internal static class MarketDigest
    {
        /// <summary>FNV-1a 64 位摘要（短、稳定、无外部依赖）。</summary>
        public static string Of(string? text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (char c in text)
                    {
                        hash ^= c;
                        hash *= 1099511628211UL;
                    }
                }
                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>把若干片段拼成一条稳定的摘要输入串。</summary>
        public static string Join(params string?[] parts)
        {
            var sb = new StringBuilder();
            foreach (string? part in parts)
            {
                sb.Append(part ?? string.Empty);
                sb.Append('\u001f');   // 单元分隔符：避免 "ab"+"c" 与 "a"+"bc" 撞车
            }
            return sb.ToString();
        }

        /// <summary>设置配色摘要：<see cref="UIColors"/> 全部取值的指纹。</summary>
        public static string Palette()
        {
            return Of(Join(
                UIColors.MainBg.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.SidebarBg.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.CardBg.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.Border.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.Primary.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.TextMain.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.TextSub.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.GroupHeader.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.NavSelected.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.NavHover.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.TextWarn.ToArgb().ToString(CultureInfo.InvariantCulture),
                UIColors.TextCrit.ToArgb().ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>DPI 键：控件实际 DPI + 全局缩放因子（任一变化 ⇒ 布局要重算）。</summary>
        public static int DpiKey(Control? control)
        {
            int deviceDpi = control?.DeviceDpi ?? 96;
            int scale = (int)Math.Round(UIUtils.ScaleFactor * 100.0);
            return deviceDpi * 1000 + scale;
        }

        /// <summary>插件段索引摘要（索引版本/摘要维度）。</summary>
        public static string Plugins(IEnumerable<MarketPluginEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (MarketPluginEntry entry in entries)
            {
                sb.Append(Join(
                    entry.Id, entry.Version, entry.Sha256, entry.Name, entry.NameEn,
                    entry.Description, entry.DescriptionEn, entry.Author,
                    entry.Status, entry.Verified ? "1" : "0",
                    entry.Renamed ? "1" : "0", entry.OriginalId, entry.Note,
                    entry.PageUrl, entry.Source, entry.File, entry.DownloadUrl,
                    string.Join(",", entry.Requires ?? new List<string>())));
            }
            return Of(sb.ToString());
        }

        /// <summary>主题段索引摘要（索引版本/摘要维度）。</summary>
        public static string Themes(IEnumerable<MarketThemeEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (MarketThemeEntry entry in entries)
            {
                sb.Append(Join(
                    entry.Id, entry.Version, entry.Sha256, entry.Name, entry.NameEn,
                    entry.Description, entry.DescriptionEn, entry.Author,
                    entry.Verified ? "1" : "0",
                    entry.Route, entry.Material, entry.Status,
                    entry.PageUrl, entry.File, entry.DownloadUrl));
            }
            return Of(sb.ToString());
        }
    }
}
