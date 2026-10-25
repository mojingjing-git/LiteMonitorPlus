using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 市场主题索引条目：index.json 顶层 `themes` 附加段里的一项（D3：schema 保持 1 的加法扩展）。
    /// </summary>
    public class MarketThemeEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("name_en")] public string NameEn { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("description_en")] public string DescriptionEn { get; set; } = "";
        [JsonPropertyName("author")] public string Author { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("page_url")] public string PageUrl { get; set; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("size")] public int Size { get; set; }

        /// <summary>渲染路线声明：gpu | legacy（与主题 JSON rendering.backend 互验，F16）</summary>
        [JsonPropertyName("route")] public string Route { get; set; } = "legacy";

        /// <summary>材质声明：liquid_glass | cyber_punk | legacy（与 rendering.material 互验，F16）</summary>
        [JsonPropertyName("material")] public string Material { get; set; } = "legacy";

        [JsonPropertyName("status")] public string Status { get; set; } = "ok";

        /// <summary>是否经审核 Agent 实机验证（仓库侧打 verified 标记）</summary>
        [JsonPropertyName("verified")] public bool Verified { get; set; }
    }

    /// <summary>
    /// 主题市场服务：拉索引（D1=A：与插件市场同一仓库、同一 URL）、解析 themes 附加段、
    /// 白名单（themes/ 前缀变体，M1 泛化口径）、内容互验（对齐 ThemeRenderingConfig.Validate
    /// 全规则集，F16/REV1-P2-4）、沙箱安装到 resources/themes/（复用插件安装事务核心 F6）、
    /// 卸载（D7 守卫）。
    /// </summary>
    public static class ThemeMarket
    {
        private static string? _themesDirOverride;

        /// <summary>主题安装目录：与 ThemeManager.ThemeDir 同一目录（F12，平铺，零迁移）。</summary>
        public static string ThemesDir =>
            _themesDirOverride ?? Path.Combine(AppContext.BaseDirectory, "resources", "themes");

        /// <summary>测试接缝：把主题安装目录重定向到临时目录。仅内部可见，生产路径不使用。</summary>
        internal static string? ThemesDirOverride
        {
            get => _themesDirOverride;
            set => _themesDirOverride = value;
        }

        /// <summary>主题 JSON 反序列化口径：与 ThemeManager.Load 完全一致。</summary>
        internal static readonly JsonSerializerOptions ThemeJsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            IgnoreReadOnlyProperties = true,
            AllowTrailingCommas = true
        };

        static ThemeMarket()
        {
            // ★ 兜底恢复：主题目录里的未完成安装事务（与 PluginMarket 静态构造同模式）。
            //   主题加载发生在启动应用主题 / 设置页枚举时，类型首次使用即触发对账；
            //   与插件一致，这里只是兜底，真正的顺序保证由"先恢复、后加载"的调用方路径承担。
            try { PluginMarket.RecoverInterruptedInstallsIn(ThemesDir, ThemeMarketState.FindPersistedForRecovery); }
            catch { /* 恢复失败不阻断启动 */ }
        }

        /// <summary>拉取市场索引（与插件市场同一 URL；主题消费返回值的 Themes 附加段）。</summary>
        public static Task<MarketIndex> FetchIndexAsync(CancellationToken ct = default)
            => PluginMarket.FetchIndexAsync(PluginMarket.DefaultIndexUrl, ct);

        /// <summary>
        /// 主题内容身份判别（F16）：主题 JSON 顶层没有 id 字段，"内容与记录身份一致"
        /// 退化为"文件是可解析的 JSON 对象"；内容完整性由安装记录的 SHA-256 比对兜底
        /// （IsTargetAvailable / 卸载计划都会比对记录摘要）。
        /// </summary>
        internal static bool ThemeContentIdentityMatches(string path, string expectedId)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch { return false; }
        }

        /// <summary>
        /// 主题包安全契约（F7/REV1-P3-6）：主题 JSON 全文不得含远程资源引用。
        /// </summary>
        internal static bool ContainsRemoteReference(string text)
            => text.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0
               || text.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// 下载并校验主题载荷（卡片快照 / 实时预览 / 安装共用同一通道）：
        /// 白名单（themes/ 变体）→ 摘要强制 → 下载 → SHA-256 → 主题内容校验。
        /// </summary>
        public static async Task<byte[]> DownloadThemePayloadAsync(MarketThemeEntry entry, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.DownloadUrl))
                throw new PluginMarketException(MarketErrorReason.NoDownloadUrl, "该条目没有下载地址");

            // ★ 白名单：themes/ 前缀变体（同仓库同域名；URI 解析/穿越/编码检查与插件完全同口径）
            if (!PluginMarket.TryResolveAllowedDownloadUri(
                    entry.DownloadUrl, PluginMarket.ThemesAssetPrefix, out Uri downloadUri, out string urlReason))
            {
                throw new PluginMarketException(MarketErrorReason.BadSource,
                    "下载地址不在允许的主题源范围内：" + urlReason);
            }

            // ★ 摘要强制（LM-008 口径）：没有可用摘要就无法证明内容未被篡改，拒绝安装
            if (!PluginMarket.IsUsableSha256(entry.Sha256))
            {
                throw new PluginMarketException(MarketErrorReason.MissingHash,
                    "索引条目未提供可用的 SHA-256 摘要，无法校验内容完整性，拒绝安装");
            }

            // ★ 下载原始字节并校验完整性（口径与插件一致：对响应原始字节算 SHA-256）
            byte[] raw = await PluginMarket.DownloadBytesAsync(downloadUri, ct).ConfigureAwait(false);
            if (!PluginMarket.VerifySha256(raw, entry.Sha256))
                throw new PluginMarketException(MarketErrorReason.HashMismatch, "下载内容校验失败（SHA-256 不匹配）");

            // ★ 主题特有内容校验：合法 JSON / 无远程引用 / route⇔backend、material⇔material 互验 /
            //   ThemeRenderingConfig.Validate() 全规则集（F16/REV1-P2-4）
            ValidateThemeContent(raw, entry);
            return raw;
        }

        /// <summary>
        /// 下载并安装市场主题。事务语义与插件安装完全一致（stage → journal → commit → 记录）。
        /// </summary>
        public static async Task InstallAsync(MarketThemeEntry entry, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            // id 合法性（与插件同一字符集）。"与内置主题重名"没有常量表（D6）：
            // 内置 = ThemeDir 既有文件，由下面的目标可用性检查覆盖 —— 有文件且无本市场记录即拒绝。
            if (!PluginMarket.TryValidatePluginId(entry.Id, out string idReason))
                throw new PluginMarketException(MarketErrorReason.InvalidId, idReason);

            // 先把上次没干完的安装事务对账掉，再判断目标能不能写
            PluginMarket.RecoverInterruptedInstallsIn(ThemesDir, ThemeMarketState.FindPersistedForRecovery);

            if (!PluginMarket.IsTargetAvailable(entry.Id, ThemesDir, ThemeMarketState.Find, ThemeContentIdentityMatches, out string targetReason))
                throw new PluginMarketException(MarketErrorReason.TargetExists, targetReason);

            // ★ 下载 + 白名单 + 摘要 + 内容校验（与预览/快照共用同一载荷通道）
            byte[] raw = await DownloadThemePayloadAsync(entry, ct).ConfigureAwait(false);

            // ★ 事务落盘（复用插件安装事务核心，F6）：stage → 原子替换 → 记录 → 清理
            PluginMarketInstallCore.CommitInstall(
                ThemesDir,
                entry.Id,
                entry.Version,
                raw,
                entry.Sha256.Trim(),
                ThemeMarketState.Find,
                ThemeMarketState.Upsert,
                ThemeMarketState.Remove);
        }

        /// <summary>
        /// D7 卸载守卫：目标主题是当前 Skin 时，先经 <paramref name="switchToDefaultTheme"/> 切走
        /// （由 UI 层注入 —— AppActions.ApplyThemeAndLayout 需要 MainForm 上下文），切换成功才执行卸载事务；
        /// 切换或卸载失败 ⇒ 记录保持、如实报错，不得出现"列表已删文件还在"或反之。
        /// </summary>
        public static bool TryUninstall(MarketThemeEntry entry, Action? switchToDefaultTheme, out MarketErrorReason reason)
        {
            reason = MarketErrorReason.Unknown;
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id))
            {
                reason = MarketErrorReason.InvalidId;
                return false;
            }

            try
            {
                string currentSkin = Settings.Load().Skin ?? "";
                if (string.Equals(currentSkin, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (switchToDefaultTheme == null)
                    {
                        // 当前主题必须先切走才能卸载；调用方没提供切换手段 ⇒ 如实失败
                        reason = MarketErrorReason.Unknown;
                        return false;
                    }
                    switchToDefaultTheme();   // 切换抛异常 ⇒ 不删文件
                }
            }
            catch
            {
                reason = MarketErrorReason.Unknown;
                return false;
            }

            // 卸载核心复用插件口径（M1 泛化）：主题没有内置常量表（builtinIds: null，
            // "内置 = 无安装记录"由 NotManaged 覆盖）；内容身份 = JSON 可解析（F16）。
            // UninstallCore 只消费 entry.Id，用 id 等价的插件条目壳复用同一实现。
            var entryShim = new MarketPluginEntry { Id = entry.Id };
            return PluginMarketUninstallCore.Uninstall(
                entryShim,
                ThemesDir,
                ThemeMarketState.Find,
                ThemeMarketState.Remove,
                builtinIds: null,
                contentIdentityMatches: ThemeContentIdentityMatches,
                out reason);
        }

        /// <summary>
        /// 主题特有内容校验（安装前）。任一不过 ⇒ ThemeContentRejected / BadContent，如实给因。
        /// 判据全部对齐既有代码：ThemeRenderingConfig.Validate() 的规则集是唯一权威
        /// （REV1-P2-4），这里不另写弱口径。
        /// </summary>
        private static void ValidateThemeContent(byte[] raw, MarketThemeEntry entry)
        {
            string content;
            try { content = Encoding.UTF8.GetString(raw); }
            catch (DecoderFallbackException ex)
            {
                throw new PluginMarketException(MarketErrorReason.BadContent, "下载到的内容不是合法 UTF-8 文本：" + ex.Message);
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(content); }
            catch (JsonException ex)
            {
                throw new PluginMarketException(MarketErrorReason.BadContent, "下载到的不是合法 JSON：" + ex.Message);
            }

            // ② route 声明必须合法（gpu | legacy）
            bool routeIsGpu = string.Equals(entry.Route, ThemeRenderingConfig.GpuBackend, StringComparison.OrdinalIgnoreCase);
            bool routeIsLegacy = string.Equals(entry.Route, ThemeRenderingConfig.LegacyBackend, StringComparison.OrdinalIgnoreCase);

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new PluginMarketException(MarketErrorReason.BadContent, "主题文件顶层必须是 JSON 对象");

                // ① 远程引用契约（F7/REV1-P3-6）：主题 JSON 全文不得含 http(s)://
                if (ContainsRemoteReference(content))
                {
                    throw new PluginMarketException(MarketErrorReason.ThemeContentRejected,
                        "主题文件包含远程资源引用（http/https），违反主题包安全契约");
                }

                if (!routeIsGpu && !routeIsLegacy)
                {
                    throw new PluginMarketException(MarketErrorReason.ThemeContentRejected,
                        "索引 route 非法（必须是 gpu 或 legacy）：" + entry.Route);
                }

                // ③ rendering 块存在时，backend 必须存在且与索引 route 互验；
                //    rendering 块缺席 ⇒ 内容按 legacy 解释（与 ThemeManager.IsGpuTheme 的
                //    容错口径一致：缺 rendering/坏 JSON 一律归类 legacy，F16），
                //    此时索引必须声明 legacy 路线，声明 gpu 仍然拒收（下方分支处理）。
                if (doc.RootElement.TryGetProperty("rendering", out JsonElement rendering) &&
                    rendering.ValueKind == JsonValueKind.Object)
                {
                    if (!rendering.TryGetProperty("backend", out JsonElement backendEl) ||
                        backendEl.ValueKind != JsonValueKind.String)
                    {
                        throw new PluginMarketException(MarketErrorReason.ThemeContentRejected,
                            "rendering.backend 声明缺失或非法，无法与索引 route 互验");
                    }

                    string backend = backendEl.GetString() ?? "";
                    if (!string.Equals(backend, entry.Route, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PluginMarketException(MarketErrorReason.ThemeContentRejected,
                            $"rendering.backend（{backend}）与索引 route（{entry.Route}）不一致");
                    }
                }
            }

            // ④ 完整反序列化（口径与 ThemeManager.Load 一致），material 互验 + Validate() 全规则集
            Theme? theme;
            try { theme = JsonSerializer.Deserialize<Theme>(content, ThemeJsonOpts); }
            catch (JsonException ex)
            {
                throw new PluginMarketException(MarketErrorReason.BadContent, "主题反序列化失败：" + ex.Message);
            }

            if (theme == null)
                throw new PluginMarketException(MarketErrorReason.BadContent, "主题解析失败");

            if (theme.Rendering == null)
            {
                // 缺 rendering = 纯 legacy 主题（12 个内置 legacy 主题均无此块，F16）。
                // 索引必须声明 legacy 路线 + legacy 材质；声明 gpu/material 非法即拒收。
                if (!routeIsLegacy || !string.Equals(entry.Material, ThemeRenderingConfig.LegacyMaterial, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginMarketException(MarketErrorReason.ThemeContentRejected,
                        "主题缺少 rendering 配置（按 legacy 解释），与索引 route/material 声明不一致");
                }
                return;
            }

            if (!string.Equals(theme.Rendering.Material, entry.Material, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginMarketException(MarketErrorReason.ThemeContentRejected,
                    $"rendering.material（{theme.Rendering.Material}）与索引 material（{entry.Material}）不一致");
            }

            var errors = theme.Rendering.Validate();
            if (errors.Count > 0)
            {
                throw new PluginMarketException(MarketErrorReason.ThemeContentRejected,
                    "主题渲染参数未通过校验：" + string.Join("; ", errors));
            }
        }
    }
}
