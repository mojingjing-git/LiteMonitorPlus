using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor.src.Core; // Added for UIUtils
using LiteMonitor.src.SystemServices.InfoService;

namespace LiteMonitor.src.Plugins.Native
{
    public static class CityCodeResolver
    {
        // 优先使用随包分发的本地城市库，离线可用且无外部依赖。
        // 发布包内 resources/ 目录为平铺布局（与 driver.zip / FPS 组件同层），
        // 开发构建则位于 resources/assets/，两条路径都尝试。
        private static readonly string[] LocalJsonCandidates =
        {
            Path.Combine(AppContext.BaseDirectory, "resources", "CityCode.json"),
            Path.Combine(AppContext.BaseDirectory, "resources", "assets", "CityCode.json"),
        };
        // 远程回退：仅在本地文件缺失或读取失败时使用
        private const string JSON_DB_URL = "https://raw.githubusercontent.com/mojingjing-git/LiteMonitorPlus/master/resources/assets/CityCode.json";
        
        // Memory Cache
        private static Dictionary<string, List<List<string>>> _db = null;
        private static bool _isLoading = false;
        private static readonly object _lock = new object();

        public static async Task<string> ResolveAsync(string province, string city, string district)
        {
            return await ResolveAsync(province, city, district, CancellationToken.None, 0).ConfigureAwait(false);
        }

        /// <summary>
        /// [LM-039] 受沙箱约束的城市代码解析。
        /// <para>闸门（与 <c>PluginExecutor</c> 的 HTTP 步骤路径同一套）：
        /// 远程回退下载占用 <see cref="PluginSandbox"/> 的进程级并发槽位、
        /// 受响应体字节上限约束（≤ 2 MiB）、并绑定 <paramref name="token"/>
        /// ⇒ 单次执行总时长到期时下载可被中止。</para>
        /// </summary>
        /// <param name="token">作用域令牌（总时长到期即中止下载）。</param>
        /// <param name="maxResponseBytes">响应体字节上限；&lt;= 0 时回落到沙箱硬天花板。</param>
        public static async Task<string> ResolveAsync(string province, string city, string district,
            CancellationToken token, int maxResponseBytes)
        {
            await EnsureDbLoadedAsync(token, maxResponseBytes).ConfigureAwait(false);

            if (_db == null) throw new Exception("CityCode DB failed to load");

            // 1. 参数清洗
            province = (province ?? "").Replace("省", "").Replace("市", "").Replace("自治区", "").Replace("壮族", "").Replace("回族", "").Replace("维吾尔", "");
            city = (city ?? "").Trim();
            district = (district ?? "").Trim();

            if (string.IsNullOrEmpty(district) && string.IsNullOrEmpty(city))
            {
                // [Fix] Throw exception instead of returning error JSON, so that PluginExecutor can catch it and retry properly
                // Returning a valid JSON with "error" field might be parsed as success by generic JSON extractors if they don't check for "error"
                throw new ArgumentException("CityCodeResolver: Empty query parameters (province/city/district are all empty).");
            }

            // 2. 查找逻辑
            List<List<string>> candidates = null;
            string targetName = "";

            // 3.1 优先查 District
            if (!string.IsNullOrEmpty(district))
            {
                candidates = Lookup(district);
                targetName = district;
            }

            // 3.2 降级查 City
            if (candidates == null && !string.IsNullOrEmpty(city))
            {
                candidates = Lookup(city);
                targetName = city;
            }

            if (candidates == null)
            {
                // [Fix] Throw exception to trigger retry mechanism
                throw new KeyNotFoundException($"CityCodeResolver: No matching city found for query: {province} {city} {district}");
            }

            // 4. 评分排序
            List<string> bestMatch = candidates[0];
            int maxScore = -1000;

            if (candidates.Count > 1)
            {
                foreach (var item in candidates)
                {
                    // item: [code, name, province]
                    string code = item[0];
                    string name = item[1];
                    string prov = item.Count > 2 ? item[2] : "";

                    int score = 0;
                    if (!string.IsNullOrEmpty(province) && prov.Contains(province)) score += 100;
                    if (name == targetName) score += 50;
                    score -= name.Length;

                    if (score > maxScore)
                    {
                        maxScore = score;
                        bestMatch = item;
                    }
                }
            }

            return JsonSerializer.Serialize(new
            {
                code = bestMatch[0],
                name = bestMatch[1],
                province = bestMatch.Count > 2 ? bestMatch[2] : ""
            });
        }

        private static List<List<string>> Lookup(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_db.TryGetValue(key, out var res)) return res;

            // 处理后缀
            string root = System.Text.RegularExpressions.Regex.Replace(key, "(自治州|自治县|地区|盟|市|区|县|旗)$", "");
            if (root.Length < 2 && key.Length > 1) root = key;

            if (_db.TryGetValue(root, out var resRoot)) return resRoot;

            return null;
        }

        public static void ResetClient()
        {
            lock (_lock)
            {
                _db = null; // Force reload on reset
            }
        }

        private static async Task EnsureDbLoadedAsync(CancellationToken token, int maxResponseBytes)
        {
            if (_db != null) return;
            if (_isLoading)
            {
                await Task.Delay(100, token);
                if (_db != null) return;
            }

            _isLoading = true;
            try
            {
                // 1) 优先读取随包分发的本地城市库（离线可用，不依赖任何外部服务）
                string json = null;
                try
                {
                    foreach (var candidate in LocalJsonCandidates)
                    {
                        if (!File.Exists(candidate)) continue;

                        // [LM-039] 本地库同样受字节上限约束（防止异常资产把内存打爆）。
                        int limit = maxResponseBytes > 0 ? maxResponseBytes : PluginSandboxLimits.MaxResponseBytes;
                        long length = new FileInfo(candidate).Length;
                        if (length > limit)
                        {
                            throw new PluginResponseTooLargeException(candidate, length, limit);
                        }

                        json = await File.ReadAllTextAsync(candidate, token).ConfigureAwait(false);
                        System.Diagnostics.Debug.WriteLine($"[CityCodeResolver] Loaded local CityCode.json: {candidate}");
                        break;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (PluginSandboxException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CityCodeResolver] Local CityCode.json load failed: {ex.Message}");
                }

                // 2) 本地缺失或读取失败时回退远程
                if (string.IsNullOrEmpty(json))
                {
                    // [Optimization] Use SocketsHttpHandler with Proxy support and Decompression
                    var handler = new SocketsHttpHandler
                    {
                        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                        UseProxy = true,
                        // Use system proxy
                        Proxy = System.Net.WebRequest.GetSystemWebProxy()
                        // 不设置自定义证书校验回调：TLS 校验按系统信任链进行。
                        // 曾为「解决用户证书报错」整体关闭校验，等于允许中间人伪造城市代码库；
                        // 企业 TLS 拦截环境应引导用户导入根证书，而不是关闭校验。
                    };

                    using (var client = new HttpClient(handler))
                    {
                        // 设置超时
                        client.Timeout = TimeSpan.FromSeconds(15);
                        client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor++/1.0");

                        // [LM-039] 远程回退同样受沙箱约束：
                        // 进程级并发槽位 + 作用域令牌（可中止）+ 响应体字节上限。
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        cts.CancelAfter(TimeSpan.FromSeconds(15));
                        using var slot = await PluginSandbox.AcquireRequestSlotAsync(cts.Token).ConfigureAwait(false);

                        using var response = await client
                            .GetAsync(JSON_DB_URL, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                            .ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();

                        using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                        byte[] bytes = await PluginSandbox
                            .ReadResponseBodyAsync(stream, maxResponseBytes, cts.Token)
                            .ConfigureAwait(false);
                        json = Encoding.UTF8.GetString(bytes);
                    }
                }

                var rawDb = JsonSerializer.Deserialize<Dictionary<string, List<List<string>>>>(json);

                // [Optimization] Intern strings in the DB to reduce memory usage
                // Many cities share the same province name, and many districts share the same city name
                _db = new Dictionary<string, List<List<string>>>(rawDb.Count);
                
                foreach (var kvp in rawDb)
                {
                    // Key (district name) can also be interned
                    string key = UIUtils.Intern(kvp.Key);
                    var lists = new List<List<string>>(kvp.Value.Count);

                    foreach (var item in kvp.Value)
                    {
                        // item: [code, name, province]
                        var newItem = new List<string>(item.Count);
                        foreach (var str in item)
                        {
                            newItem.Add(UIUtils.Intern(str));
                        }
                        lists.Add(newItem);
                    }
                    _db[key] = lists;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // [LM-039] 作用域取消（总时长到期）必须原样上抛：吞掉会变成"城市库加载失败"，
                // 上限就形同虚设。
                throw;
            }
            catch (PluginSandboxException)
            {
                // [LM-039] 沙箱上限（并发 / 响应字节）不得被降级成普通加载失败。
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CityCodeResolver] Load DB Failed: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }
    }
}
