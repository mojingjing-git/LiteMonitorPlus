using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件市场失败原因。界面据此挑选对应的本地化文案。
    /// </summary>
    public enum MarketErrorReason
    {
        Unknown = 0,
        NoDownloadUrl,   // 条目没有下载地址
        BadSource,       // 下载地址不在允许的插件源范围内
        InvalidId,       // 插件 id 含非法字符
        BuiltinConflict, // 与内置插件 id 冲突
        TargetExists,    // 目标文件已存在且非本市场安装
        HashMismatch,    // 下载内容 SHA-256 校验失败
        IdMismatch,      // 下载内容与索引声明的 id 不一致
        NotManaged,      // 不是通过本市场安装的，无法卸载
        RecordCorrupted, // 安装记录异常（文件名与 id 不匹配）
        SchemaMismatch,  // 索引格式不兼容
        BadContent       // 下载内容不是合法 JSON
    }

    /// <summary>
    /// 插件市场操作失败。Reason 供界面做本地化，Message 为技术细节。
    /// </summary>
    public class PluginMarketException : Exception
    {
        public MarketErrorReason Reason { get; }

        public PluginMarketException(MarketErrorReason reason, string message) : base(message)
        {
            Reason = reason;
        }
    }

    /// <summary>
    /// 市场里的单个插件条目，对应 awesome-litemonitor-plugins 仓库 index.json 里的一项
    /// </summary>
    public class MarketPluginEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("original_id")] public string OriginalId { get; set; } = "";
        [JsonPropertyName("renamed")] public bool Renamed { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("name_en")] public string NameEn { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("description_en")] public string DescriptionEn { get; set; } = "";
        [JsonPropertyName("author")] public string Author { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("readme_url")] public string ReadmeUrl { get; set; } = "";
        [JsonPropertyName("page_url")] public string PageUrl { get; set; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "ok";
        [JsonPropertyName("requires")] public List<string> Requires { get; set; } = new List<string>();
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";

        /// <summary>是否经审核 Agent 实机验证（仓库侧打 verified 标记）</summary>
        [JsonPropertyName("verified")] public bool Verified { get; set; }

        /// <summary>状态标记：ok = 纯 JSON 直接能用，needs-service = 还要跑配套服务，needs-code = 要改代码重编译</summary>
        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case "needs-service": return LanguageManager.T("Menu.PluginMarketStatusService");
                    case "needs-code": return LanguageManager.T("Menu.PluginMarketStatusCode");
                    default: return LanguageManager.T("Menu.PluginMarketStatusOk");
                }
            }
        }
    }

    /// <summary>
    /// 插件市场索引，对应 index.json
    /// </summary>
    public class MarketIndex
    {
        [JsonPropertyName("schema")] public int Schema { get; set; }
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
        [JsonPropertyName("repo")] public string Repo { get; set; } = "";
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("plugins")] public List<MarketPluginEntry> Plugins { get; set; } = new List<MarketPluginEntry>();
    }

    /// <summary>
    /// 插件市场服务：拉索引、下载、安装、卸载
    /// </summary>
    public static class PluginMarket
    {
        /// <summary>本程序支持的索引格式版本</summary>
        public const int SupportedIndexSchema = 1;

        /// <summary>默认插件源：社区插件索引仓库</summary>
        public const string DefaultIndexUrl =
            "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json";

        /// <summary>插件安装目录，与 PluginManager 的加载路径保持一致</summary>
        public static string PluginsDir => Path.Combine(AppContext.BaseDirectory, "resources", "plugins");

        /// <summary>
        /// 允许的下载源前缀 —— 索引被投毒时不得让客户端去任意站点取内容。
        /// sha256 与 download_url 都在同一份索引里，索引被改时二者可以一起被改；
        /// 前缀白名单把"能取内容的地方"限制在自家仓库。
        /// </summary>
        private static readonly string[] AllowedDownloadPrefixes =
        {
            "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/"
        };

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 判断下载地址是否落在允许的插件源范围内。
        /// 索引被投毒时也不允许客户端去任意站点取内容。
        /// </summary>
        public static bool IsAllowedDownloadUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return AllowedDownloadPrefixes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 校验下载内容的 SHA-256。expected 为空（老索引没有该字段）时不做校验，返回 true。
        /// 口径：必须对 HTTP 响应的【原始字节】计算，不能先取字符串再重新编码。
        /// </summary>
        public static bool VerifySha256(byte[] content, string? expected)
        {
            if (content == null) return false;
            if (string.IsNullOrEmpty(expected)) return true;

            string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>拉取市场索引</summary>
        public static async Task<MarketIndex> FetchIndexAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("插件源地址不能为空");

            string json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var index = JsonSerializer.Deserialize<MarketIndex>(json, _jsonOpts);
            if (index == null)
                throw new InvalidDataException("索引文件解析失败");
            if (index.Plugins == null)
                index.Plugins = new List<MarketPluginEntry>();

            if (index.Schema != SupportedIndexSchema)
            {
                throw new PluginMarketException(MarketErrorReason.SchemaMismatch,
                    $"插件索引格式不兼容（schema={index.Schema}，本程序支持 {SupportedIndexSchema}）");
            }

            return index;
        }

        /// <summary>
        /// 落盘文件名：只由插件 id 决定，不信任索引给出的 file 字段。
        /// id 的字符集不含路径分隔符与点号，因此路径穿越在结构上不可能发生。
        /// </summary>
        public static string GetInstallFileName(string pluginId)
            => pluginId + ".json";

        /// <summary>目标路径是否安全（id 合法 + 不撞内置）</summary>
        internal static bool TryValidatePluginId(string pluginId, out string reason)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                reason = "id 为空";
                return false;
            }

            // 使用 \A..\z 而非 ^..$：.NET 的 $ 会匹配末尾换行符之前的位置，
            // 使 "abc\n" 也可能通过校验；路径处理又会拒绝其中的 CR/LF。
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    pluginId, @"\A[A-Za-z0-9_-]{1,64}\z"))
            {
                reason = "id 含非法字符";
                return false;
            }

            reason = "";
            return true;
        }

        public static bool CanInstall(string pluginId, out string reason)
        {
            if (!TryValidatePluginId(pluginId, out reason)) return false;
            if (PluginConstants.BuiltinPluginIds.Contains(pluginId))
            {
                reason = "插件 id 与内置插件冲突";
                return false;
            }
            return true;
        }

        public static bool IsTargetAvailable(string pluginId, out string reason)
        {
            string path = Path.Combine(PluginsDir, GetInstallFileName(pluginId));
            if (!File.Exists(path)) { reason = ""; return true; }

            // 文件在，且确认是本市场装的这个 id → 允许（这是"更新"）
            var record = PluginMarketState.Find(pluginId);
            if (record != null && FileContentIdMatches(path, pluginId))
            {
                reason = "";
                return true;
            }

            reason = "目标文件已存在且非本市场安装（可能是内置插件或旧版残留），请先移除该文件";
            return false;
        }

        /// <summary>读取插件文件的 id 字段；读不出来返回 false</summary>
        internal static bool FileContentIdMatches(string path, string expectedId)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    string id = idEl.GetString() ?? "";
                    return string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 下载并安装插件。写的是无 BOM 的 UTF-8，跟内置插件保持一致。
        /// </summary>
        public static async Task InstallAsync(MarketPluginEntry entry, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.DownloadUrl))
                throw new PluginMarketException(MarketErrorReason.NoDownloadUrl, "该条目没有下载地址");

            // ★ 源前缀白名单：索引被投毒时也不允许去别处取内容
            if (!IsAllowedDownloadUrl(entry.DownloadUrl))
            {
                throw new PluginMarketException(MarketErrorReason.BadSource, "下载地址不在允许的插件源范围内");
            }

            // id 合法性 + 不撞内置
            if (!CanInstall(entry.Id, out string reason))
            {
                var err = PluginConstants.BuiltinPluginIds.Contains(entry.Id)
                    ? MarketErrorReason.BuiltinConflict
                    : MarketErrorReason.InvalidId;
                throw new PluginMarketException(err, reason);
            }

            // L2：目标已存在且非本市场安装 → 拒绝（不覆盖内置插件 / 用户手放的文件 / 旧版残留）
            if (!IsTargetAvailable(entry.Id, out string targetReason))
                throw new PluginMarketException(MarketErrorReason.TargetExists, targetReason);

            // ★ 下载原始字节（不要用 GetStringAsync —— 字符串重新编码后的字节可能与原始字节不同，
            //   BOM、非 UTF-8 charset 都会导致误报"校验失败"）
            byte[] raw = await _http.GetByteArrayAsync(entry.DownloadUrl, ct).ConfigureAwait(false);
            string content = Encoding.UTF8.GetString(raw);

            // ★ 完整性校验：口径与仓库侧一致（对 plugin.json 的原始文件字节算 SHA-256）
            if (!string.IsNullOrEmpty(entry.Sha256))
            {
                if (!VerifySha256(raw, entry.Sha256))
                    throw new PluginMarketException(MarketErrorReason.HashMismatch, "下载内容校验失败（SHA-256 不匹配）");
            }

            // 落地前先验一下：必须是合法 JSON，且带 id，否则装了也是白装
            string id;
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("id", out var idEl))
                    throw new PluginMarketException(MarketErrorReason.BadContent, "下载到的内容没有 id 字段");
                id = idEl.GetString() ?? "";
            }
            catch (JsonException ex)
            {
                throw new PluginMarketException(MarketErrorReason.BadContent, "下载到的不是合法 JSON：" + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(id))
                throw new PluginMarketException(MarketErrorReason.BadContent, "下载到的插件 id 为空");

            // ★ 下载内容的 id 必须与索引声明的一致
            if (!string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase))
                throw new PluginMarketException(MarketErrorReason.IdMismatch, "下载内容与索引声明的 id 不一致");

            Directory.CreateDirectory(PluginsDir);

            // ★ 落盘路径只由 id 决定，不使用索引里的 file 字段
            string path = Path.Combine(PluginsDir, GetInstallFileName(entry.Id));
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);

            // 写安装记录：所有权判定与更新检测的唯一依据
            var existing = PluginMarketState.Find(entry.Id);
            PluginMarketState.Upsert(new MarketPluginRecord
            {
                Id = entry.Id,
                File = GetInstallFileName(entry.Id),
                Version = entry.Version,
                Sha256 = string.IsNullOrEmpty(entry.Sha256) ? (existing?.Sha256 ?? "") : entry.Sha256,
                InstalledAt = existing?.InstalledAt ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });
        }

        /// <summary>
        /// 卸载市场插件。只能卸载"由本市场安装并记录在案"的插件。
        /// </summary>
        public static bool Uninstall(MarketPluginEntry entry, out MarketErrorReason reason)
        {
            return PluginMarketUninstallCore.Uninstall(
                entry,
                PluginsDir,
                PluginMarketState.Find,
                PluginMarketState.Remove,
                out reason);
        }
    }
}
