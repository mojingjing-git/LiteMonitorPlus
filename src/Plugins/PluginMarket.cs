using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
        BadContent,      // 下载内容不是合法 JSON
        MissingHash      // 索引条目没有提供可用的 SHA-256 摘要（LM-008：缺摘要 ≠ 不需要校验）
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

        /// <summary>
        /// 单次下载允许的最大字节数（8 MiB）。超过即中止下载并让安装失败，
        /// 避免被投毒的索引用一个超大响应把内存/磁盘打爆。
        /// </summary>
        public const long MaxDownloadBytes = 8L * 1024 * 1024;

        /// <summary>单次下载允许的最大跳转次数（每一跳都会重新校验地址）。</summary>
        public const int MaxDownloadRedirects = 5;

        /// <summary>下载地址的最大长度。超长一律拒绝，避免拿超长串做前缀匹配。</summary>
        public const int MaxDownloadUrlLength = 2048;

        /// <summary>
        /// 插件安装目录，与 PluginManager 的加载路径保持一致。
        /// </summary>
        public static string PluginsDir =>
            _pluginsDirOverride ?? Path.Combine(AppContext.BaseDirectory, "resources", "plugins");

        private static string? _pluginsDirOverride;

        /// <summary>
        /// 测试接缝：把插件安装目录重定向到临时目录。仅内部可见，生产路径不使用。
        /// </summary>
        internal static string? PluginsDirOverride
        {
            get => _pluginsDirOverride;
            set => _pluginsDirOverride = value;
        }

        /// <summary>
        /// 测试接缝：替换下载用的 HTTP 处理器（用于模拟跳转/超限响应）。仅内部可见。
        /// </summary>
        internal static HttpMessageHandler? HttpHandlerOverride { get; set; }

        /// <summary>
        /// 允许的下载源（LM-007）：**精确的 Host + 路径前缀**。
        ///
        /// 旧实现是 <c>url.StartsWith(prefix)</c> 的整串前缀比较，能被
        /// "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/plugins/../evil"
        /// 这类路径穿越、以及 "https://raw.githubusercontent.com/.../plugins.evil.tld/x" 这类
        /// "前缀后面接别的域名/路径" 的写法绕过。现在改成：
        /// 先用 <see cref="Uri"/> 解析，再逐项比对 Host 与路径前缀。
        /// </summary>
        private static readonly AllowedDownloadSource[] AllowedDownloadSources =
        {
            new AllowedDownloadSource(
                "raw.githubusercontent.com",
                "/mojingjing-git/awesome-litemonitor-plugins/main/plugins/")
        };

        /// <summary>索引拉取用（沿用原行为：允许自动跳转）。</summary>
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly HashSet<string> _recoveredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _recoveryLock = new object();

        static PluginMarket()
        {
            // ★ 启动恢复（LM-008）：进程上次被杀/崩溃时可能留下"写了一半"的安装事务。
            //   这里在类型首次使用时对账，保证不留半截插件文件、也不让记录与文件不一致。
            // ★★ LM-083：**这里只是兜底**，不能当成"恢复已经发生了"的依据 ——
            //   类型首次被碰到的那一刻可能**晚于**插件加载与调度（例：PluginSelfCheck.RunOnce
            //   才会第一次读 PluginsDir），那时磁盘虽然回滚了，内存里跑的却还是新版本。
            //   真正保证"先恢复、后加载"的是：
            //     ① PluginManager.LoadPlugins 开头的恢复（结构性兜底，任何调用方都绕不过）；
            //     ② MainForm 构造函数里显式的恢复调用（启动顺序一眼可见）。
            try { EnsureRecovered(PluginsDir); }
            catch { /* 恢复失败不阻断启动 */ }
        }

        /// <summary>
        /// 判断下载地址是否落在允许的插件源范围内。
        /// 索引被投毒时也不允许客户端去任意站点取内容。
        /// </summary>
        public static bool IsAllowedDownloadUrl(string url)
        {
            return TryResolveAllowedDownloadUri(url, out _, out _);
        }

        /// <summary>
        /// 解析并校验下载地址（LM-007）。通过时 <paramref name="allowed"/> 是可用于下载的绝对 URI。
        /// 校验项：绝对 URI、HTTPS、无 userinfo、默认端口、Host 精确命中白名单、
        /// 路径无编码分隔符/无 ".."/无空段、路径前缀命中白名单。
        /// </summary>
        internal static bool TryResolveAllowedDownloadUri(string? rawUrl, out Uri allowed, out string reason)
        {
            allowed = null!;
            reason = "";

            if (string.IsNullOrWhiteSpace(rawUrl)) { reason = "下载地址为空"; return false; }
            if (rawUrl.Length > MaxDownloadUrlLength) { reason = "下载地址过长"; return false; }
            // 反斜杠在浏览器/服务器的路径语义里常被当作分隔符，直接拒掉整串
            if (rawUrl.IndexOf('\\') >= 0) { reason = "下载地址含反斜杠"; return false; }

            string trimmed = rawUrl.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed) || parsed == null)
            {
                reason = "不是合法的绝对地址";
                return false;
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                reason = "只允许 HTTPS";
                return false;
            }

            // user:pass@host 形式：凭据不应出现在下载地址里
            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                reason = "地址不得包含 userinfo（user:password@）";
                return false;
            }

            if (parsed.Port != 443)
            {
                reason = "只允许默认 HTTPS 端口（443）";
                return false;
            }

            // ★ 用"原始写法"的路径做穿越/编码检查：
            //   Uri 解析本身会把 "/a/../b" 规范化成 "/b"，规范化后再检查就看不出穿越意图了。
            string rawPath = ExtractRawPath(trimmed, parsed);
            if (!TryValidateRawPath(rawPath, out reason)) return false;

            bool hostMatched = false;
            bool pathMatched = false;
            foreach (var source in AllowedDownloadSources)
            {
                if (!string.Equals(parsed.Host, source.Host, StringComparison.OrdinalIgnoreCase)) continue;
                hostMatched = true;
                if (rawPath.StartsWith(source.PathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    pathMatched = true;
                    break;
                }
            }

            if (!hostMatched)
            {
                reason = "主机 " + parsed.Host + " 不在允许的下载源白名单内";
                return false;
            }
            if (!pathMatched)
            {
                reason = "路径不在允许的下载源前缀内：" + rawPath;
                return false;
            }

            allowed = parsed;
            return true;
        }

        /// <summary>从原始地址串里取出"未经 Uri 规范化"的路径部分。</summary>
        private static string ExtractRawPath(string url, Uri parsed)
        {
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return parsed.AbsolutePath;

            int authorityStart = schemeEnd + 3;
            int pathStart = url.IndexOf('/', authorityStart);
            if (pathStart < 0) return "/";

            int end = url.IndexOfAny(new[] { '?', '#' }, pathStart);
            return end < 0 ? url.Substring(pathStart) : url.Substring(pathStart, end - pathStart);
        }

        /// <summary>
        /// 路径规范化检查：拒绝编码分隔符（%2f / %5c）、拒绝 ".." 穿越、拒绝空段、
        /// 拒绝解码后仍带分隔符或二次编码的片段。
        /// </summary>
        private static bool TryValidateRawPath(string rawPath, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(rawPath)) { reason = "路径为空"; return false; }
            if (!rawPath.StartsWith("/", StringComparison.Ordinal)) { reason = "路径必须以 / 开头"; return false; }

            // 编码分隔符：%2f（/）、%5c（\）
            if (ContainsToken(rawPath, "%2f") || ContainsToken(rawPath, "%5c"))
            {
                reason = "路径含编码分隔符（%2f / %5c）";
                return false;
            }

            string[] segments = rawPath.Split('/');
            for (int i = 1; i < segments.Length; i++)   // segments[0] 是首个 "/" 之前的空串
            {
                string segment = segments[i];
                if (segment.Length == 0) { reason = "路径含空段"; return false; }

                string decoded;
                try { decoded = Uri.UnescapeDataString(segment); }
                catch { reason = "路径含非法转义序列"; return false; }

                if (decoded.Length == 0) { reason = "路径含空段"; return false; }
                if (decoded == "." || decoded == "..") { reason = "路径含穿越段（" + decoded + "）"; return false; }
                if (decoded.IndexOf('/') >= 0 || decoded.IndexOf('\\') >= 0)
                {
                    reason = "路径段解码后含分隔符";
                    return false;
                }
                // 二次编码（%252f 之类）：解码一次后还带 %xx，说明还有一层编码
                if (HasPercentEscape(decoded)) { reason = "路径含二次编码"; return false; }
            }

            return true;
        }

        private static bool ContainsToken(string text, string token)
            => text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool HasPercentEscape(string text)
        {
            for (int i = 0; i + 2 < text.Length; i++)
            {
                if (text[i] == '%' && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2])) return true;
            }
            return false;
        }

        /// <summary>
        /// 校验下载内容的 SHA-256。expected 为空（老索引没有该字段）时不做校验，返回 true。
        /// 口径：必须对 HTTP 响应的【原始字节】计算，不能先取字符串再重新编码。
        ///
        /// ★ 注意（LM-008）：本方法只回答"给定的摘要是否匹配"。
        ///   "索引条目根本没有摘要"是另一件事，由安装路径强制拒绝（见 <see cref="IsUsableSha256"/>），
        ///   不能在这里被解释成"不需要校验"。
        /// </summary>
        public static bool VerifySha256(byte[] content, string? expected)
        {
            if (content == null) return false;
            if (string.IsNullOrEmpty(expected)) return true;

            string actual = ComputeSha256(content);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>对原始字节算 SHA-256（小写十六进制）。</summary>
        internal static string ComputeSha256(byte[] content)
            => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        /// <summary>
        /// 索引条目提供的摘要是否可用（LM-008）。
        /// 缺失或格式非法都算"不可用"：没有摘要就无法证明内容未被篡改，
        /// 安装必须被拒绝，而不是"没给摘要就不校验"。
        /// </summary>
        internal static bool IsUsableSha256(string? expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return false;

            string trimmed = expected.Trim();
            if (trimmed.Length != 64) return false;

            foreach (char c in trimmed)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
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
            => IsTargetAvailable(pluginId, PluginMarketState.Find, out reason);

        /// <summary>
        /// 目标文件是否允许被市场写入（LM-008 加固）。
        ///
        /// 只有"文件在 + 有市场安装记录 + 记录指向这个文件 + 文件内容 id 一致 + 文件内容仍与
        /// 安装时记录的摘要一致"时才允许覆盖（更新流程）。
        /// 摘要不一致说明用户装完之后自己改过这个文件 —— 市场不得静默覆盖用户的改动。
        /// 用户自己放的、没记录的文件（内置插件、手动安装、旧版残留）一律拒绝。
        /// </summary>
        internal static bool IsTargetAvailable(
            string pluginId,
            Func<string, MarketPluginRecord?> findRecord,
            out string reason)
        {
            string fileName = GetInstallFileName(pluginId);
            string path = Path.Combine(PluginsDir, fileName);
            if (!File.Exists(path)) { reason = ""; return true; }

            // 文件在，且确认是本市场装的这个 id → 允许（这是"更新"）
            var record = findRecord(pluginId);
            if (record != null && FileContentIdMatches(path, pluginId))
            {
                if (!string.Equals(record.File, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "安装记录与目标文件不匹配，拒绝覆盖";
                    return false;
                }

                // 老记录没有摘要时无法比对内容，退回原有的 id 判定
                if (IsUsableSha256(record.Sha256))
                {
                    string actual;
                    try { actual = ComputeSha256(File.ReadAllBytes(path)); }
                    catch { actual = ""; }

                    if (!string.Equals(actual, record.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "目标文件在安装后被修改过，拒绝覆盖（如需更新请先卸载该插件）";
                        return false;
                    }
                }

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

            // ★ 源白名单（LM-007）：先解析再比对 Host + 路径前缀，不是整串前缀比较
            if (!TryResolveAllowedDownloadUri(entry.DownloadUrl, out Uri downloadUri, out string urlReason))
            {
                throw new PluginMarketException(MarketErrorReason.BadSource,
                    "下载地址不在允许的插件源范围内：" + urlReason);
            }

            // id 合法性 + 不撞内置
            if (!CanInstall(entry.Id, out string reason))
            {
                var err = PluginConstants.BuiltinPluginIds.Contains(entry.Id)
                    ? MarketErrorReason.BuiltinConflict
                    : MarketErrorReason.InvalidId;
                throw new PluginMarketException(err, reason);
            }

            // 先把上次没干完的安装事务对账掉，再判断目标能不能写
            EnsureRecovered(PluginsDir);

            // L2：目标已存在且非本市场安装 → 拒绝（不覆盖内置插件 / 用户手放的文件 / 旧版残留）
            if (!IsTargetAvailable(entry.Id, PluginMarketState.Find, out string targetReason))
                throw new PluginMarketException(MarketErrorReason.TargetExists, targetReason);

            // ★ 摘要强制（LM-008）：索引条目没有可用摘要 ⇒ 安装被拒。
            //   "没给摘要"不等于"不需要校验"，更不等于"可以装"。
            if (!IsUsableSha256(entry.Sha256))
            {
                throw new PluginMarketException(MarketErrorReason.MissingHash,
                    "索引条目未提供可用的 SHA-256 摘要，无法校验内容完整性，拒绝安装");
            }

            // ★ 下载原始字节（不要用 GetStringAsync —— 字符串重新编码后的字节可能与原始字节不同，
            //   BOM、非 UTF-8 charset 都会导致误报"校验失败"）
            byte[] raw = await DownloadBytesAsync(downloadUri, ct).ConfigureAwait(false);

            // ★ 完整性校验：口径与仓库侧一致（对 plugin.json 的原始文件字节算 SHA-256）
            if (!VerifySha256(raw, entry.Sha256))
                throw new PluginMarketException(MarketErrorReason.HashMismatch, "下载内容校验失败（SHA-256 不匹配）");

            // 落地前先验一下：必须是合法 JSON，且带 id，否则装了也是白装
            string content;
            string id;
            try
            {
                content = Encoding.UTF8.GetString(raw);
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

            // ★ 事务落盘（LM-008）：同目录暂存 → 原子替换 → 落盘成功后才更新安装记录。
            //   失败时回滚：原文件原样保留、原记录原样保留、暂存文件清理。
            PluginMarketInstallCore.CommitInstall(
                PluginsDir,
                entry.Id,
                entry.Version,
                raw,
                entry.Sha256.Trim(),
                PluginMarketState.Find,
                PluginMarketState.Upsert,
                PluginMarketState.Remove);
        }

        /// <summary>
        /// 下载插件内容（LM-007）：自行逐跳处理重定向，**每一跳都重新校验地址**，
        /// 并且限制实际读取的字节数。
        /// </summary>
        internal static async Task<byte[]> DownloadBytesAsync(Uri initialUri, CancellationToken ct = default)
        {
            using HttpClient client = CreateDownloadClient();

            Uri current = initialUri;
            for (int hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (IsRedirectStatus(response.StatusCode))
                {
                    if (hop >= MaxDownloadRedirects)
                    {
                        throw new PluginMarketException(MarketErrorReason.BadSource,
                            $"下载重定向次数超过上限（{MaxDownloadRedirects} 次）");
                    }

                    Uri? location = response.Headers.Location;
                    if (location == null)
                    {
                        throw new PluginMarketException(MarketErrorReason.BadSource,
                            "重定向响应没有 Location 头");
                    }

                    Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!TryResolveAllowedDownloadUri(next.AbsoluteUri, out Uri allowedNext, out string reason))
                    {
                        throw new PluginMarketException(MarketErrorReason.BadSource,
                            "重定向目标不在允许的插件源范围内：" + reason);
                    }

                    current = allowedNext;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new PluginMarketException(MarketErrorReason.Unknown,
                        $"下载失败：HTTP {(int)response.StatusCode}");
                }

                return await ReadBodyWithLimitAsync(response, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 生产下载处理器：**禁止自动跳转**。
        ///
        /// 自动跳转会让 HttpClient 内部直接跟到下一跳，来不及对下一跳做白名单校验
        /// （跳到 evil 主机时请求已经发出去了），所以跳转必须由
        /// <see cref="DownloadBytesAsync"/> 自己逐跳处理、逐跳校验。
        /// </summary>
        internal static HttpMessageHandler CreateDownloadHandler()
            => new HttpClientHandler { AllowAutoRedirect = false };

        /// <summary>
        /// 下载用的客户端：<b>关闭自动跳转</b>，重定向必须由 <see cref="DownloadBytesAsync"/> 逐跳校验。
        /// </summary>
        private static HttpClient CreateDownloadClient()
        {
            HttpMessageHandler? handler = HttpHandlerOverride;
            if (handler != null)
            {
                // 测试替身：客户端释放时不连带释放处理器
                return new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
            }

            return new HttpClient(CreateDownloadHandler(), disposeHandler: true)
            { Timeout = TimeSpan.FromSeconds(30) };
        }

        private static bool IsRedirectStatus(HttpStatusCode status)
            => status == HttpStatusCode.MovedPermanently
               || status == HttpStatusCode.Found
               || status == HttpStatusCode.SeeOther
               || status == HttpStatusCode.TemporaryRedirect
               || status == HttpStatusCode.PermanentRedirect;

        /// <summary>
        /// 读取响应体，**实际读取的字节数**超过 <see cref="MaxDownloadBytes"/> 即中止并失败。
        /// </summary>
        private static async Task<byte[]> ReadBodyWithLimitAsync(HttpResponseMessage response, CancellationToken ct)
        {
            long? declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaxDownloadBytes)
            {
                throw new PluginMarketException(MarketErrorReason.Unknown,
                    $"下载内容 {declared.Value} 字节超过上限 {MaxDownloadBytes} 字节，已中止");
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];
            long total = 0;

            while (true)
            {
                int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;

                total += read;
                if (total > MaxDownloadBytes)
                {
                    throw new PluginMarketException(MarketErrorReason.Unknown,
                        $"下载内容超过上限 {MaxDownloadBytes} 字节，已中止");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// 卸载市场插件。只能卸载"由本市场安装并记录在案"的插件。
        /// </summary>
        public static bool Uninstall(MarketPluginEntry entry, out MarketErrorReason reason)
        {
            // 先把上次没干完的安装事务对账掉，避免卸载时踩到残留的暂存/备份文件
            EnsureRecovered(PluginsDir);

            return PluginMarketUninstallCore.Uninstall(
                entry,
                PluginsDir,
                PluginMarketState.Find,
                PluginMarketState.Remove,
                out reason);
        }

        /// <summary>
        /// 启动恢复（LM-008）：把上次没干完的安装事务对账掉 ——
        /// 要么回滚到旧内容，要么确认已提交并清理残留，
        /// 保证"不留半截插件文件、记录与文件不不一致"。
        /// </summary>
        public static void RecoverInterruptedInstalls()
        {
            EnsureRecovered(PluginsDir, force: true);
        }

        /// <summary>
        /// 启动恢复（LM-083）：对账**指定目录**里的未完成安装事务。
        /// <para>
        /// ★ 为什么需要"按目录"的入口：插件管理器加载的是**调用方传进来的目录**，
        ///   而 <see cref="RecoverInterruptedInstalls"/> 只认 <see cref="PluginsDir"/>。
        ///   恢复必须发生在**任何加载 / 调度之前**，所以加载路径上需要一个能对账
        ///   "即将被加载的那个目录"的入口。
        /// </para>
        /// <para>
        /// ★ 这里必须 <c>force: true</c>：恢复要**当场**发生，
        ///   不能因为"这个目录之前已经对账过"就跳过 —— 跳过就又回到"恢复晚于加载"的老问题。
        /// </para>
        /// </summary>
        internal static void RecoverInterruptedInstallsIn(string pluginsDir)
        {
            if (string.IsNullOrWhiteSpace(pluginsDir)) return;
            EnsureRecovered(pluginsDir, force: true);
        }

        /// <summary>确保指定目录里的未完成安装事务已经被对账过（每个目录只做一次）。</summary>
        private static void EnsureRecovered(string pluginsDir, bool force = false)
        {
            lock (_recoveryLock)
            {
                if (!force && _recoveredDirs.Contains(pluginsDir)) return;

                try
                {
                    PluginMarketInstallCore.RecoverInterruptedInstalls(pluginsDir, PluginMarketState.Find);
                    _recoveredDirs.Add(pluginsDir);
                }
                catch
                {
                    // 恢复失败不阻断后续操作；下次仍会重试
                }
            }
        }

        /// <summary>允许的下载源：Host + 路径前缀</summary>
        private sealed class AllowedDownloadSource
        {
            internal AllowedDownloadSource(string host, string pathPrefix)
            {
                Host = host;
                PathPrefix = pathPrefix;
            }

            internal string Host { get; }
            internal string PathPrefix { get; }
        }
    }

    /// <summary>
    /// 安装事务（LM-008）：文件与安装记录必须用同一个事务标识收敛。
    ///
    /// 写前日志（PluginsDir/.market-install-txn.log）里记录每一笔在途事务的
    /// 事务号、目标文件、暂存文件、备份文件与本次内容的 SHA-256。
    /// 进程被杀时日志会留下来，启动恢复据此判定该回滚还是该收尾。
    /// </summary>
    internal sealed class MarketInstallTransaction
    {
        public string TxId { get; set; } = "";
        public string PluginId { get; set; } = "";
        public string Target { get; set; } = "";
        public string Stage { get; set; } = "";
        public string Backup { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    /// <summary>写前日志文件的内容</summary>
    internal sealed class MarketInstallTxnLog
    {
        public int Version { get; set; } = 1;
        public List<MarketInstallTransaction> Pending { get; set; } = new List<MarketInstallTransaction>();
    }

    /// <summary>
    /// 模拟"进程被杀"的异常：抛出后安装流程**不做回滚**，
    /// 保留写前日志，等下次启动恢复处理。生产代码不会抛出它。
    /// </summary>
    internal sealed class MarketInstallCrashException : Exception
    {
        internal MarketInstallCrashException(string point) : base("模拟进程被杀：" + point)
        {
            Point = point;
        }

        internal string Point { get; }
    }

    /// <summary>
    /// 安装事务与启动恢复的核心（LM-008）。
    ///
    /// 落盘顺序：写前日志 → 同目录暂存（同卷）→ 备份旧内容 → 原子替换（File.Move overwrite）
    /// → 更新安装记录 → 清理备份与日志条目。
    /// 任一步抛异常都会回滚：旧文件原样恢复、旧记录原样恢复、暂存文件清理。
    /// </summary>
    internal static class PluginMarketInstallCore
    {
        /// <summary>写前日志文件名（放在 PluginsDir 下，不以 .json 结尾，不会被插件加载器扫到）</summary>
        internal const string TransactionLogFileName = ".market-install-txn.log";

        /// <summary>
        /// 坏日志隔离文件名（R04）。写前日志被篡改或损坏时，原始内容逐字节留存在这里供事后排查；
        /// 同样不以 .json 结尾，不会被插件加载器扫到。
        /// </summary>
        internal const string QuarantineLogFileName = ".market-install-txn.quarantine.log";

        /// <summary>
        /// 一笔事务在插件目录下的三个直接子文件（R04）。
        /// 路径**不是**从日志里读来的，而是按【插件 id + 事务 id】重建并做过目录边界校验的。
        /// </summary>
        internal readonly struct TransactionPaths
        {
            internal TransactionPaths(string targetPath, string stagePath, string backupPath)
            {
                TargetPath = targetPath;
                StagePath = stagePath;
                BackupPath = backupPath;
            }

            internal string TargetPath { get; }
            internal string StagePath { get; }
            internal string BackupPath { get; }
        }

        private static readonly object _commitLock = new object();

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 事务落盘。faultInjector 仅供测试模拟"进程被杀"（生产传 null）。
        /// </summary>
        internal static void CommitInstall(
            string pluginsDir,
            string pluginId,
            string version,
            byte[] raw,
            string sha256,
            Func<string, MarketPluginRecord?> findRecord,
            Action<MarketPluginRecord> upsertRecord,
            Action<string> removeRecord,
            Action<string>? faultInjector = null)
        {
            string targetName = PluginMarket.GetInstallFileName(pluginId);
            string targetPath = Path.Combine(pluginsDir, targetName);
            string txId = Guid.NewGuid().ToString("N");

            var tx = new MarketInstallTransaction
            {
                TxId = txId,
                PluginId = pluginId,
                Target = targetName,
                Stage = targetName + "." + txId + ".stage",
                Backup = targetName + "." + txId + ".bak",
                Sha256 = sha256
            };

            string stagePath = Path.Combine(pluginsDir, tx.Stage);
            string backupPath = Path.Combine(pluginsDir, tx.Backup);

            // ★ 并发安装同一插件时必须串行：暂存/替换/写记录必须是一个整体
            lock (_commitLock)
            {
                MarketPluginRecord? oldRecord = findRecord(pluginId);
                bool hadOldFile = File.Exists(targetPath);
                bool movedToTarget = false;
                bool recordTouched = false;

                Directory.CreateDirectory(pluginsDir);
                AppendPending(pluginsDir, tx);

                try
                {
                    // ① 同目录暂存（与目标同卷）→ 原子替换
                    File.WriteAllBytes(stagePath, raw);
                    if (hadOldFile) File.Copy(targetPath, backupPath, overwrite: true);

                    faultInjector?.Invoke("afterStage");

                    File.Move(stagePath, targetPath, overwrite: true);
                    movedToTarget = true;

                    faultInjector?.Invoke("afterMove");

                    // ② 落盘成功之后才更新安装记录
                    upsertRecord(new MarketPluginRecord
                    {
                        Id = pluginId,
                        File = targetName,
                        Version = version,
                        Sha256 = sha256,
                        InstalledAt = oldRecord?.InstalledAt ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    });
                    recordTouched = true;

                    faultInjector?.Invoke("afterRecord");

                    // ③ 提交：清理备份与日志条目
                    SafeDelete(backupPath);
                    RemovePending(pluginsDir, tx.TxId);
                }
                catch (MarketInstallCrashException)
                {
                    // 模拟进程被杀：不做回滚，保留写前日志，交给启动恢复
                    throw;
                }
                catch
                {
                    // ④ 回滚：原文件原样保留、原记录原样保留、暂存文件清理
                    Rollback(
                        pluginsDir, tx,
                        movedToTarget, recordTouched, oldRecord,
                        upsertRecord, removeRecord);
                    throw;
                }
            }
        }

        /// <summary>
        /// 启动恢复：对账写前日志里的每一笔在途事务。
        /// 返回被处理的条数。
        ///
        /// ★ R04（目录边界）：日志里的路径字段**一律不当路径用**。
        ///   每笔事务先按【插件 id + 事务 id】重建三个直接子文件名，再做边界校验；
        ///   校验不过（绝对路径 / 含目录分隔符 / 越界 / 重解析点 / 字段与重建值不一致）就整笔拒绝：
        ///   原文逐字节隔离留证，其中的路径一个都不执行，也不再重试。
        /// </summary>
        internal static int RecoverInterruptedInstalls(
            string pluginsDir,
            Func<string, MarketPluginRecord?> findRecord)
        {
            string logPath = LogPath(pluginsDir);
            if (!File.Exists(logPath)) return 0;

            string rawText;
            try
            {
                rawText = File.ReadAllText(logPath);
            }
            catch
            {
                // 读不到（被占用等）：按原行为当作没有在途事务
                SafeDelete(logPath);
                return 0;
            }

            if (!TryParseLogText(rawText, out MarketInstallTxnLog log))
            {
                // 坏日志：原文留证，一个字节都不执行
                QuarantineRejectedLog(pluginsDir, ReadLogBytes(logPath),
                    new[] { "写前日志无法解析成合法的日志结构" });
                SafeDelete(logPath);
                return 0;
            }

            if (log.Pending.Count == 0)
            {
                SafeDelete(logPath);
                return 0;
            }

            int handled = 0;
            var remaining = new List<MarketInstallTransaction>();
            var rejected = new List<string>();
            for (int i = 0; i < log.Pending.Count; i++)
            {
                MarketInstallTransaction tx = log.Pending[i];

                // ★ R04：先重建 + 先校验，不通过就整笔拒绝，绝不拿日志里的字符串去碰磁盘
                if (!TryResolveTransactionPaths(pluginsDir, tx, out TransactionPaths paths, out string reason))
                {
                    rejected.Add($"第 {i + 1} 条被拒绝：{reason}；原始条目 = {SerializeTransaction(tx)}");
                    continue;
                }

                try
                {
                    Reconcile(paths, tx, findRecord);
                    handled++;
                }
                catch
                {
                    // 单笔对账失败不影响其它笔，保留在日志里下次再试
                    remaining.Add(tx);
                }
            }

            if (rejected.Count > 0)
                QuarantineRejectedLog(pluginsDir, ReadLogBytes(logPath), rejected);

            if (remaining.Count == 0) SafeDelete(logPath);
            else WriteLog(pluginsDir, new MarketInstallTxnLog { Pending = remaining });

            return handled;
        }

        /// <summary>单笔事务对账。paths 已通过目录边界校验（R04），里面的路径都在插件目录之下。</summary>
        private static void Reconcile(
            TransactionPaths paths,
            MarketInstallTransaction tx,
            Func<string, MarketPluginRecord?> findRecord)
        {
            string targetPath = paths.TargetPath;
            string stagePath = paths.StagePath;
            string backupPath = paths.BackupPath;

            bool stageExists = File.Exists(stagePath);
            bool targetHasNewContent = File.Exists(targetPath)
                                       && ShaEquals(FileSha(targetPath), tx.Sha256);

            if (stageExists || !targetHasNewContent)
            {
                // 暂存文件还在 ⇒ 原子替换从未发生（File.Move 会消费掉暂存文件）；
                // 目标文件也不是新内容 ⇒ 同样是"没替换成功"。
                // 两种情况下目标文件都是旧内容（完好），只需清理暂存/备份。
                SafeDelete(stagePath);
                SafeDelete(backupPath);
                return;
            }

            // 目标已经是新内容：替换成功过。这时要看记录有没有跟上。
            MarketPluginRecord? record = findRecord(tx.PluginId);
            bool recordUpdated = record != null && ShaEquals(record.Sha256, tx.Sha256);

            if (recordUpdated)
            {
                // 记录也已更新 ⇒ 这次安装实际是成功的，只剩清理
                SafeDelete(backupPath);
                return;
            }

            // 记录没跟上 ⇒ 回滚：恢复旧内容；首次安装则删掉这次落下的文件
            if (File.Exists(backupPath))
            {
                File.Move(backupPath, targetPath, overwrite: true);
            }
            else
            {
                SafeDelete(targetPath);
            }
        }

        /// <summary>
        /// 回滚：恢复原文件与原记录，清理暂存文件。
        ///
        /// ★ R04：作用路径同样按【插件 id + 事务 id】重建并做目录边界校验，
        ///   校验不过就一个字节都不动。正常路径下 tx 由本类自己构造（插件 id 合法、事务 id 自产），
        ///   重建值与实际路径必然一致 ⇒ 回滚行为不变。
        /// </summary>
        private static void Rollback(
            string pluginsDir,
            MarketInstallTransaction tx,
            bool movedToTarget,
            bool recordTouched,
            MarketPluginRecord? oldRecord,
            Action<MarketPluginRecord> upsertRecord,
            Action<string> removeRecord)
        {
            try
            {
                if (!TryBuildTransactionPaths(pluginsDir, tx, out TransactionPaths paths, out _))
                    return;   // 路径不可信 ⇒ 不执行任何文件操作

                string targetPath = paths.TargetPath;
                string stagePath = paths.StagePath;
                string backupPath = paths.BackupPath;

                if (movedToTarget)
                {
                    if (File.Exists(backupPath))
                    {
                        // 原子恢复旧内容
                        File.Move(backupPath, targetPath, overwrite: true);
                    }
                    else
                    {
                        // 首次安装：把刚写进去的文件撤掉
                        SafeDelete(targetPath);
                    }
                }

                SafeDelete(stagePath);
                SafeDelete(backupPath);

                if (recordTouched)
                {
                    if (oldRecord != null) upsertRecord(oldRecord);
                    else removeRecord(tx.PluginId);
                }

                RemovePending(pluginsDir, tx.TxId);
            }
            catch
            {
                // 回滚本身失败不能掩盖原始异常；写前日志仍在，下次启动会再对账
            }
        }

        // ==========================================================
        // R04：目录边界 —— 事务日志里的路径字段一律不直接使用
        // ==========================================================

        /// <summary>
        /// 解析一笔事务，得到它在插件目录下的三个直接子文件路径（R04）。
        ///
        /// ★ 三层校验，任何一层不过都整笔拒绝：
        ///   ① 形状：日志字段不得含目录分隔符、不得是绝对路径、不得是 "." / ".."；
        ///   ② 一致性：Target / Stage / Backup 必须分别等于按【插件 id + 事务 id】重建出来的文件名；
        ///   ③ 边界：重建出来的名字必须落在插件目录之下，且目标不是重解析点。
        /// </summary>
        internal static bool TryResolveTransactionPaths(
            string pluginsDir,
            MarketInstallTransaction? tx,
            out TransactionPaths paths,
            out string reason)
        {
            paths = default;

            if (!TryBuildTransactionPaths(pluginsDir, tx, out paths, out reason)) return false;
            if (tx == null) return false;   // TryBuildTransactionPaths 已经拦住了空条目

            // ① 形状：日志里的"路径"字段只能是叶子文件名
            if (LooksLikePathEscape(tx.Target) || LooksLikePathEscape(tx.Stage) || LooksLikePathEscape(tx.Backup))
            {
                reason = "日志里的路径字段含目录分隔符 / 绝对路径 / 相对目录标记";
                return false;
            }

            // ② 一致性：字段必须与按 id 重建出来的文件名逐字节相同
            string targetName = PluginMarket.GetInstallFileName(tx.PluginId);
            if (!string.Equals(tx.Target, targetName, StringComparison.Ordinal) ||
                !string.Equals(tx.Stage, targetName + "." + tx.TxId + ".stage", StringComparison.Ordinal) ||
                !string.Equals(tx.Backup, targetName + "." + tx.TxId + ".bak", StringComparison.Ordinal))
            {
                reason = "日志里的路径字段与按插件 id / 事务 id 重建的文件名不一致";
                return false;
            }

            // ③ 摘要必须可用：摘要被改掉时，日志就没有可信的"新内容"判据
            if (!PluginMarket.IsUsableSha256(tx.Sha256))
            {
                reason = "事务摘要缺失或格式非法";
                return false;
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// 按【插件 id + 事务 id】重建三个直接子文件名，并解析成插件目录下的路径（R04）。
        /// 只做"路径安全"校验（重建 + 目录边界），不涉及日志字段的一致性判断。
        /// </summary>
        internal static bool TryBuildTransactionPaths(
            string pluginsDir,
            MarketInstallTransaction? tx,
            out TransactionPaths paths,
            out string reason)
        {
            paths = default;

            if (tx == null) { reason = "事务条目为空"; return false; }

            if (!PluginMarket.TryValidatePluginId(tx.PluginId, out string idReason))
            {
                reason = "插件 id 非法（" + idReason + "）";
                return false;
            }

            if (!IsValidTransactionId(tx.TxId))
            {
                reason = "事务 id 非法（必须是 32 位十六进制）";
                return false;
            }

            string targetName = PluginMarket.GetInstallFileName(tx.PluginId);
            if (!TryResolveDirectChildPath(pluginsDir, targetName, out string targetPath, out reason) ||
                !TryResolveDirectChildPath(pluginsDir, targetName + "." + tx.TxId + ".stage", out string stagePath, out reason) ||
                !TryResolveDirectChildPath(pluginsDir, targetName + "." + tx.TxId + ".bak", out string backupPath, out reason))
            {
                return false;
            }

            paths = new TransactionPaths(targetPath, stagePath, backupPath);
            reason = "";
            return true;
        }

        /// <summary>
        /// 把一个【叶子文件名】解析成 pluginsDir 下的直接子文件路径，并做目录边界校验（R04）。
        ///
        /// 拒绝：绝对路径、含目录分隔符的路径、"." / ".."、解析后越出 pluginsDir 的路径，
        ///       以及目标是重解析点（符号链接 / 联接）的情况。
        /// </summary>
        internal static bool TryResolveDirectChildPath(
            string pluginsDir,
            string? fileName,
            out string path,
            out string reason)
        {
            path = "";

            if (string.IsNullOrWhiteSpace(pluginsDir)) { reason = "插件目录为空"; return false; }
            if (string.IsNullOrEmpty(fileName)) { reason = "文件名为空"; return false; }

            // ★ 叶子名判定：只要带路径成分（分隔符 / 根 / 相对目录标记）就不是合法文件名
            if (fileName == "." || fileName == "..") { reason = "文件名是相对目录标记"; return false; }
            if (fileName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                fileName.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
                fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0 ||
                Path.IsPathRooted(fileName))
            {
                reason = "文件名含目录分隔符或绝对路径";
                return false;
            }

            try
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsDir));
                string candidate = Path.GetFullPath(Path.Combine(root, fileName));

                string? candidateDir = Path.GetDirectoryName(candidate);
                if (!string.Equals(
                        Path.TrimEndingDirectorySeparator(candidateDir ?? ""),
                        root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = "路径越出插件目录";
                    return false;
                }

                if (IsUntrustedReparsePoint(candidate))
                {
                    reason = "目标是重解析点（符号链接 / 联接），不可信";
                    return false;
                }

                path = candidate;
                reason = "";
                return true;
            }
            catch (Exception ex)
            {
                reason = "路径解析失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 目标是否是【不受信任的重解析点】（符号链接 / 联接等）。
        /// 读不到属性时按"不可信"处理（fail-closed）。
        /// </summary>
        private static bool IsUntrustedReparsePoint(string path)
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(path);
                return (attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch (FileNotFoundException) { return false; }   // 不存在 ⇒ 不是重解析点
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }                              // 读不到属性 ⇒ 不可信
        }

        /// <summary>事务 id 必须是 32 位十六进制（Guid.NewGuid().ToString("N") 的形状）</summary>
        private static bool IsValidTransactionId(string? txId)
        {
            if (string.IsNullOrEmpty(txId) || txId.Length != 32) return false;
            foreach (char c in txId)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>日志字段长得像"路径"（而不是叶子文件名）时返回 true。</summary>
        private static bool LooksLikePathEscape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            if (value == "." || value == "..") return true;
            if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0) return true;
            if (Path.IsPathRooted(value)) return true;
            return false;
        }

        /// <summary>解析写前日志原文。返回 false = 坏日志（无法解析或结构不对）。</summary>
        private static bool TryParseLogText(string? rawText, out MarketInstallTxnLog log)
        {
            log = new MarketInstallTxnLog();
            if (rawText == null) return false;
            if (string.IsNullOrWhiteSpace(rawText)) return true;   // 空日志 = 没有在途事务
            try
            {
                var parsed = JsonSerializer.Deserialize<MarketInstallTxnLog>(rawText, _jsonOpts);
                if (parsed?.Pending == null) return false;
                log = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读日志的原始字节（隔离留证时逐字节保留）。</summary>
        private static byte[]? ReadLogBytes(string logPath)
        {
            try { return File.ReadAllBytes(logPath); }
            catch { return null; }
        }

        /// <summary>把一条事务条目序列化回 JSON（隔离留证时用）。</summary>
        private static string SerializeTransaction(MarketInstallTransaction tx)
        {
            try { return JsonSerializer.Serialize(tx, _jsonOpts); }
            catch { return "(序列化失败)"; }
        }

        /// <summary>
        /// 坏日志隔离留证（R04）：原始日志内容【逐字节】追加到隔离文件，并记录拒绝原因。
        /// 隔离文件与写前日志同目录，不以 .json 结尾，不会被插件加载器扫到。
        /// 隔离失败不得阻断启动恢复。
        /// </summary>
        private static void QuarantineRejectedLog(
            string pluginsDir,
            byte[]? rawLogBytes,
            IReadOnlyList<string> reasons)
        {
            try
            {
                Directory.CreateDirectory(pluginsDir);
                string path = Path.Combine(pluginsDir, QuarantineLogFileName);

                var header = new StringBuilder();
                header.Append("===== LiteMonitor 插件市场事务日志隔离（R04 目录边界校验） =====\r\n");
                header.Append("隔离时间(UTC): ")
                      .Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                      .Append("\r\n");
                header.Append("拒绝原因:\r\n");
                foreach (string reason in reasons) header.Append("  - ").Append(reason).Append("\r\n");
                header.Append("----- 原始日志全文（逐字节保留）开始 -----\r\n");

                var encoding = new UTF8Encoding(false);
                byte[] headerBytes = encoding.GetBytes(header.ToString());
                byte[] footerBytes = encoding.GetBytes("\r\n----- 原始日志全文 结束 -----\r\n");

                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                fs.Write(headerBytes, 0, headerBytes.Length);
                if (rawLogBytes != null && rawLogBytes.Length > 0)
                    fs.Write(rawLogBytes, 0, rawLogBytes.Length);
                fs.Write(footerBytes, 0, footerBytes.Length);
            }
            catch
            {
                // 隔离失败也不能阻断启动恢复
            }
        }

        private static string LogPath(string pluginsDir)
            => Path.Combine(pluginsDir, TransactionLogFileName);

        private static MarketInstallTxnLog ReadLog(string pluginsDir)
        {
            string path = LogPath(pluginsDir);
            if (!File.Exists(path)) return new MarketInstallTxnLog();

            try
            {
                return TryParseLogText(File.ReadAllText(path), out MarketInstallTxnLog log)
                    ? log
                    : new MarketInstallTxnLog();
            }
            catch
            {
                // 日志损坏：当作没有在途事务，但保留文件让人工排查
                return new MarketInstallTxnLog();
            }
        }

        private static void WriteLog(string pluginsDir, MarketInstallTxnLog log)
        {
            Directory.CreateDirectory(pluginsDir);
            string path = LogPath(pluginsDir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(log, _jsonOpts), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }

        private static void AppendPending(string pluginsDir, MarketInstallTransaction tx)
        {
            MarketInstallTxnLog log = ReadLog(pluginsDir);
            log.Pending.RemoveAll(t => string.Equals(t.TxId, tx.TxId, StringComparison.OrdinalIgnoreCase));
            log.Pending.Add(tx);
            WriteLog(pluginsDir, log);
        }

        private static void RemovePending(string pluginsDir, string txId)
        {
            if (!File.Exists(LogPath(pluginsDir))) return;

            MarketInstallTxnLog log = ReadLog(pluginsDir);
            log.Pending.RemoveAll(t => string.Equals(t.TxId, txId, StringComparison.OrdinalIgnoreCase));

            // 没有在途事务了 ⇒ 日志文件直接删掉，不留空壳
            if (log.Pending.Count == 0) { SafeDelete(LogPath(pluginsDir)); return; }

            WriteLog(pluginsDir, log);
        }

        private static string FileSha(string path)
        {
            try { return ComputeFileSha(path); }
            catch { return ""; }
        }

        private static string ComputeFileSha(string path)
            => PluginMarket.ComputeSha256(File.ReadAllBytes(path));

        private static bool ShaEquals(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
