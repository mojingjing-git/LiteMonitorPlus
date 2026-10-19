using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 市场里的单个插件条目，对应 awesome-litemonitor-plugins 仓库 index.json 里的一项
    /// </summary>
    public class MarketPluginEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("original_id")] public string OriginalId { get; set; } = "";
        [JsonPropertyName("renamed")] public bool Renamed { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("author")] public string Author { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "ok";
        [JsonPropertyName("requires")] public List<string> Requires { get; set; } = new List<string>();
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";

        /// <summary>状态标记：ok = 纯 JSON 直接能用，needs-service = 还要跑配套服务，needs-code = 要改代码重编译</summary>
        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case "needs-service": return "需配套服务";
                    case "needs-code": return "需改代码";
                    default: return "开箱即用";
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
        /// <summary>默认插件源：社区插件索引仓库</summary>
        public const string DefaultIndexUrl =
            "https://raw.githubusercontent.com/mojingjing-git/awesome-litemonitor-plugins/main/index.json";

        /// <summary>插件安装目录，与 PluginManager 的加载路径保持一致</summary>
        public static string PluginsDir => Path.Combine(AppContext.BaseDirectory, "resources", "plugins");

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

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
            return index;
        }

        /// <summary>该插件是否已经装在本地</summary>
        public static bool IsInstalled(MarketPluginEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.File)) return false;
            return File.Exists(Path.Combine(PluginsDir, entry.File));
        }

        /// <summary>
        /// 下载并安装插件。写的是无 BOM 的 UTF-8，跟内置插件保持一致。
        /// </summary>
        public static async Task InstallAsync(MarketPluginEntry entry, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.DownloadUrl))
                throw new InvalidDataException("该条目没有下载地址");
            if (string.IsNullOrEmpty(entry.File))
                throw new InvalidDataException("该条目缺少文件名");

            string content = await _http.GetStringAsync(entry.DownloadUrl, ct).ConfigureAwait(false);

            // 落地前先验一下：必须是合法 JSON，且带 id，否则装了也是白装
            string id;
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("id", out var idEl))
                    throw new InvalidDataException("下载到的内容没有 id 字段");
                id = idEl.GetString() ?? "";
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("下载到的不是合法 JSON：" + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException("下载到的插件 id 为空");

            Directory.CreateDirectory(PluginsDir);
            string path = Path.Combine(PluginsDir, entry.File);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
        }

        /// <summary>卸载插件（删文件）</summary>
        public static bool Uninstall(MarketPluginEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.File)) return false;
            string path = Path.Combine(PluginsDir, entry.File);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        /// <summary>
        /// 检查文件名字是否安全，避免索引被投毒后写到目录外面去
        /// </summary>
        public static bool IsSafeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\')) return false;
            return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }
    }
}
