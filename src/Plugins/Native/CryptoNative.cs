using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteMonitor.src.Plugins.Native
{
    public static class CryptoNative
    {
        private sealed class SymbolInfo
        {
            public string Base { get; init; } = "BTC";
            public string Quote { get; init; } = "USDT";
            public string ApiSymbol => Base + Quote;
            public string OkxSymbol => Base + "-" + Quote;
            public string GateSymbol => Base + "_" + Quote;
        }

        private sealed class QuoteCache
        {
            public string Json { get; init; } = "";
            public DateTime Time { get; init; }
            public bool IsFallback { get; init; }
        }

        private static readonly object _clientLock = new object();
        private static readonly ConcurrentDictionary<string, QuoteCache> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflightRequests = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan _directCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan _fallbackCacheTtl = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan _staleCacheTtl = TimeSpan.FromMinutes(30);
        private static DateTime _fallbackBlockedUntil = DateTime.MinValue;
        private static HttpClient _client = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 8,
                UseProxy = true,
                Proxy = System.Net.WebRequest.GetSystemWebProxy()
                // 不设置自定义证书校验回调：TLS 校验按系统信任链进行。
                // 曾为「解决用户证书报错」整体关闭校验，等于允许中间人替换汇率数据；
                // 企业 TLS 拦截环境应引导用户导入根证书，而不是关闭校验。
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor++/1.0");
            return client;
        }

        public static void ResetClient()
        {
            HttpClient old;
            lock (_clientLock)
            {
                old = _client;
                _client = CreateClient();
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                try { old.Dispose(); } catch { }
            });
        }

        private static HttpClient GetClient()
        {
            lock (_clientLock)
            {
                return _client;
            }
        }

        public static async Task<string> FetchAsync(string symbol, string fallbackUrl)
        {
            return await FetchAsync(symbol, fallbackUrl, CancellationToken.None, 0, null, "");
        }

        /// <summary>
        /// [LM-039] 受沙箱约束的取数入口。
        /// <para>闸门（与 <c>PluginExecutor</c> 的 HTTP 步骤路径同一套）：</para>
        /// <list type="bullet">
        /// <item><b>允许目标</b>：<paramref name="fallbackUrl"/> 的主机必须落在
        /// <paramref name="allowedHosts"/>（插件<b>显式声明</b>的主机集合）内，
        /// 否则直接拒绝（原因码 <see cref="PluginSandboxReasons.CredentialTargetNotAllowed"/>）——
        /// 校验发生在<b>任何请求发出之前</b>，用户填的任意 URL 不会被取回。</item>
        /// <item><b>并发槽位</b>：每个网络请求都占用 <see cref="PluginSandbox"/> 的进程级槽位。</item>
        /// <item><b>响应体字节上限</b>：读取受 2 MiB 上限约束（同一原语、同一原因码）。</item>
        /// <item><b>总时长</b>：请求绑定 <paramref name="token"/> ⇒ 截止时刻可中止。</item>
        /// </list>
        /// </summary>
        /// <param name="token">作用域令牌（总时长到期即中止请求）。</param>
        /// <param name="maxResponseBytes">响应体字节上限；&lt;= 0 时回落到沙箱硬天花板。</param>
        /// <param name="allowedHosts">允许的目标主机集合；为 null 表示无声明上下文（不做目标校验）。</param>
        /// <param name="inflightScopeKey">在途去重的作用域键：<b>不同作用域不共享</b>在途请求，
        /// 避免"一个作用域到期"连带打断另一个作用域正在复用的同一请求。</param>
        public static async Task<string> FetchAsync(string symbol, string fallbackUrl, CancellationToken token,
            int maxResponseBytes, IReadOnlyCollection<string> allowedHosts, string inflightScopeKey)
        {
            // [LM-039] 回退目标校验：必须在任何请求发出之前（否则用户填的任意 URL 会被取回）。
            EnsureFallbackTargetAllowed(fallbackUrl, allowedHosts);

            var info = NormalizeSymbol(symbol);
            string cacheKey = info.ApiSymbol;

            if (TryGetFreshCache(cacheKey, out var cached))
            {
                return cached;
            }

            string inflightKey = string.IsNullOrEmpty(inflightScopeKey)
                ? cacheKey
                : cacheKey + "|" + inflightScopeKey;

            var lazyTask = _inflightRequests.GetOrAdd(
                inflightKey,
                _ => new Lazy<Task<string>>(
                    () => FetchFreshAsync(info, fallbackUrl, cacheKey, token, maxResponseBytes),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await lazyTask.Value;
            }
            finally
            {
                // 只移除当前 Lazy，避免误删同 key 下刚创建的新请求。
                _inflightRequests.TryRemove(
                    new KeyValuePair<string, Lazy<Task<string>>>(inflightKey, lazyTask));
            }
        }

        /// <summary>
        /// [LM-039] 回退（fallback）目标校验：主机必须落在插件**显式声明**的允许集合内。
        /// </summary>
        private static void EnsureFallbackTargetAllowed(string fallbackUrl, IReadOnlyCollection<string> allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(fallbackUrl)) return;
            if (allowedHosts == null) return;   // 无声明上下文 ⇒ 不做目标校验（与 HTTP 路径同一口径）

            if (!Uri.TryCreate(fallbackUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.CredentialTargetNotAllowed,
                    "原生步骤的回退地址非法，请求未发出");
            }

            string host = uri.Host;
            if (string.IsNullOrEmpty(host) || !IsHostAllowed(host, allowedHosts))
            {
                throw new PluginSandboxException(
                    PluginSandboxReasons.CredentialTargetNotAllowed,
                    $"原生步骤的回退目标主机 '{host}' 不在插件显式声明的允许目标集合内，请求未发出");
            }
        }

        /// <summary>主机是否在允许集合内（忽略端口、大小写不敏感）。</summary>
        private static bool IsHostAllowed(string host, IEnumerable<string> allowedHosts)
        {
            if (allowedHosts == null) return false;
            string h = StripPort(host);
            foreach (var allowed in allowedHosts)
            {
                if (string.IsNullOrWhiteSpace(allowed)) continue;
                if (string.Equals(StripPort(allowed.Trim()), h, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string StripPort(string value)
        {
            int colon = value.LastIndexOf(':');
            if (colon > 0 && colon < value.Length - 1) return value.Substring(0, colon);
            return value;
        }

        private static async Task<string> FetchFreshAsync(SymbolInfo info, string fallbackUrl, string cacheKey,
            CancellationToken token, int maxResponseBytes)
        {
            foreach (var source in GetOfficialSources(info))
            {
                try
                {
                    string json = await GetStringAsync(source.url, TimeSpan.FromSeconds(3), token, maxResponseBytes);
                    string normalized = source.parser(json, info);
                    SaveCache(cacheKey, normalized, false);
                    return normalized;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // [LM-039] 作用域取消（总时长到期）⇒ 整个原生步骤必须中止，
                    // 不能"换下一个源"继续跑。
                    throw;
                }
                catch (PluginSandboxException)
                {
                    // [LM-039] 沙箱上限（并发 / 响应字节）必须**原样上抛**：
                    // 吞掉后会被降级成"这个源不可用"，上限就形同虚设。
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CryptoNative] 官方源 {source.name} 失败: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(fallbackUrl))
            {
                if (DateTime.Now < _fallbackBlockedUntil)
                {
                    if (TryGetStaleCache(cacheKey, out var stale))
                        return stale;

                    throw new Exception("Crypto Native Failed: fallback is cooling down.");
                }

                try
                {
                    string targetUrl = BuildFallbackUrl(fallbackUrl, info.ApiSymbol);
                    string fallbackJson = await GetStringAsync(targetUrl, TimeSpan.FromSeconds(8), token, maxResponseBytes);
                    SaveCache(cacheKey, fallbackJson, true);
                    return fallbackJson;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (PluginSandboxException)
                {
                    // [LM-039] 沙箱上限不得被降级成"回退源冷却"。
                    throw;
                }
                catch (Exception ex)
                {
                    BlockFallback(ex);
                    if (TryGetStaleCache(cacheKey, out var stale))
                        return stale;

                    throw new Exception($"Crypto Native Failed (Official & Fallback): {ex.Message}");
                }
            }

            if (TryGetStaleCache(cacheKey, out var last))
                return last;

            throw new Exception("Crypto Native Failed: official APIs failed and no fallback provided.");
        }

        private static SymbolInfo NormalizeSymbol(string symbol)
        {
            string raw = (symbol ?? "BTC").Trim().ToUpperInvariant().Replace("/", "-").Replace("_", "-");
            string[] parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return new SymbolInfo { Base = parts[0], Quote = NormalizeQuote(parts[1]) };
            }

            string compact = raw.Replace("-", "");
            if (compact.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
            {
                return new SymbolInfo { Base = compact[..^4], Quote = "USDT" };
            }
            if (compact.EndsWith("USD", StringComparison.OrdinalIgnoreCase))
            {
                return new SymbolInfo { Base = compact[..^3], Quote = "USDT" };
            }

            return new SymbolInfo { Base = compact.Length == 0 ? "BTC" : compact, Quote = "USDT" };
        }

        private static string NormalizeQuote(string quote)
        {
            return quote.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "USDT" : quote.ToUpperInvariant();
        }

        private static IEnumerable<(string name, string url, Func<string, SymbolInfo, string> parser)> GetOfficialSources(SymbolInfo info)
        {
            // 优先保持原有 Bybit 数据源；GateIO 作为国内直连兜底，减少中转消耗。
            // [LM-039] 官方源是**代码内建**的目标（插件/用户改不动），因此不受"允许目标集合"约束；
            // 但仍受并发闸门 / 响应字节上限 / 总时长中止约束（见 GetStringAsync）。
            yield return ("Bybit", ResolveSourceUrl("Bybit", $"https://api.bybit.com/v5/market/tickers?category=spot&symbol={info.ApiSymbol}", info.ApiSymbol), ParseBybitResponse);
            yield return ("GateIO", ResolveSourceUrl("GateIO", $"https://api.gateio.ws/api/v4/spot/tickers?currency_pair={info.GateSymbol}", info.GateSymbol), ParseGateResponse);
            yield return ("Binance", ResolveSourceUrl("Binance", $"https://api.binance.com/api/v3/ticker/24hr?symbol={info.ApiSymbol}", info.ApiSymbol), ParseBinanceResponse);
            yield return ("OKX", ResolveSourceUrl("OKX", $"https://www.okx.com/api/v5/market/ticker?instId={info.OkxSymbol}", info.OkxSymbol), ParseOkxResponse);
        }

        /// <summary>
        /// [LM-039][测试接缝] 官方数据源 URL 覆盖表：键 = 源名称（Bybit/GateIO/Binance/OKX），
        /// 值 = 替代 URL 模板（<c>{symbol}</c> 会被替换成该源对应的交易对写法）。
        /// <para>默认 null ⇒ 使用内置 URL。<b>仅供回归用例把公网源替换为本地回环服务器</b>，
        /// 生产路径永不设置（设置后仍是同一套闸门：并发槽位 / 字节上限 / 令牌中止）。</para>
        /// </summary>
        internal static Dictionary<string, string> SourceUrlOverrides;

        private static string ResolveSourceUrl(string name, string builtinUrl, string symbol)
        {
            var overrides = SourceUrlOverrides;
            if (overrides == null) return builtinUrl;
            if (!overrides.TryGetValue(name, out var template) || string.IsNullOrWhiteSpace(template)) return builtinUrl;
            return template.Replace("{symbol}", Uri.EscapeDataString(symbol));
        }

        /// <summary>
        /// [LM-039] 受沙箱约束的取数：进程级并发槽位 + 作用域令牌（可中止）+ 响应体字节上限。
        /// <para>与 <c>PluginExecutor.FetchRawAsync</c>（HTTP 步骤路径）**同一套闸门**：
        /// 槽位来自 <see cref="PluginSandbox.AcquireRequestSlotAsync"/>，
        /// 正文读取来自 <see cref="PluginSandbox.ReadResponseBodyAsync"/>（同一原因码）。</para>
        /// </summary>
        private static async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken token, int maxResponseBytes)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);

            // [LM-039] 进程级并发闸门：原生步骤的网络请求与 HTTP 步骤共用同一把闸门（上限 8）。
            using var slot = await PluginSandbox.AcquireRequestSlotAsync(cts.Token).ConfigureAwait(false);

            // 用 ResponseHeadersRead：正文在读取时按字节上限计数（整体缓冲会先吃满内存）。
            using var response = await GetClient()
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
            }

            // [LM-039] 响应体字节上限（≤ 2 MiB）：超出即中止读取并明确失败（不截断）。
            using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            byte[] bytes = await PluginSandbox.ReadResponseBodyAsync(stream, maxResponseBytes, cts.Token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }

        private static string BuildFallbackUrl(string fallbackUrl, string symbol)
        {
            string targetUrl = fallbackUrl.Replace("{{symbol}}", Uri.EscapeDataString(symbol));
            if (!targetUrl.Contains("symbol=", StringComparison.OrdinalIgnoreCase) &&
                !targetUrl.Contains(symbol, StringComparison.OrdinalIgnoreCase))
            {
                targetUrl += (targetUrl.Contains("?") ? "&" : "?") + $"symbol={Uri.EscapeDataString(symbol)}";
            }
            return targetUrl;
        }

        private static bool TryGetFreshCache(string key, out string json)
        {
            json = "";
            if (!_cache.TryGetValue(key, out var entry)) return false;

            var ttl = entry.IsFallback ? _fallbackCacheTtl : _directCacheTtl;
            if (DateTime.Now - entry.Time > ttl) return false;

            json = entry.Json;
            return true;
        }

        private static bool TryGetStaleCache(string key, out string json)
        {
            json = "";
            if (!_cache.TryGetValue(key, out var entry)) return false;
            if (DateTime.Now - entry.Time > _staleCacheTtl) return false;

            json = entry.Json;
            return true;
        }

        private static void SaveCache(string key, string json, bool isFallback)
        {
            _cache[key] = new QuoteCache { Json = json, Time = DateTime.Now, IsFallback = isFallback };
        }

        private static void BlockFallback(Exception ex)
        {
            TimeSpan cooldown = IsQuotaError(ex) ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);
            _fallbackBlockedUntil = DateTime.Now.Add(cooldown);
            System.Diagnostics.Debug.WriteLine($"[CryptoNative] 中转源冷却 {cooldown.TotalMinutes:0} 分钟: {ex.Message}");
        }

        private static bool IsQuotaError(Exception ex)
        {
            if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
            {
                return httpEx.StatusCode.Value == HttpStatusCode.TooManyRequests ||
                       httpEx.StatusCode.Value == HttpStatusCode.Forbidden ||
                       httpEx.StatusCode.Value == HttpStatusCode.ServiceUnavailable;
            }
            return false;
        }

        private static string ParseBybitResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            
            if (root.GetProperty("retCode").GetInt32() != 0)
            {
                throw new Exception($"Bybit Error: {root.GetProperty("retMsg").GetString()}");
            }

            var list = root.GetProperty("result").GetProperty("list");
            if (list.GetArrayLength() == 0)
            {
                throw new Exception("Symbol Not Found");
            }

            var data = list[0];

            double pcnt = 0;
            if (double.TryParse(data.GetProperty("price24hPcnt").GetString(), out double p))
            {
                pcnt = Math.Round(p * 100, 2);
            }

            var responseData = new
            {
                name = data.GetProperty("symbol").GetString(),
                price = data.GetProperty("lastPrice").GetString(),
                change_percent = pcnt,
                high = data.GetProperty("highPrice24h").GetString(),
                low = data.GetProperty("lowPrice24h").GetString(),
                vol = data.GetProperty("turnover24h").GetString(),
                source = "Bybit"
            };

            return JsonSerializer.Serialize(responseData);
        }

        private static string ParseBinanceResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var data = doc.RootElement;

            double pcnt = 0;
            double.TryParse(data.GetProperty("priceChangePercent").GetString(), out pcnt);

            var responseData = new
            {
                name = data.GetProperty("symbol").GetString(),
                price = data.GetProperty("lastPrice").GetString(),
                change_percent = Math.Round(pcnt, 2),
                high = data.GetProperty("highPrice").GetString(),
                low = data.GetProperty("lowPrice").GetString(),
                vol = data.GetProperty("quoteVolume").GetString(),
                source = "Binance"
            };

            return JsonSerializer.Serialize(responseData);
        }

        private static string ParseGateResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var list = doc.RootElement;
            if (list.GetArrayLength() == 0)
            {
                throw new Exception("Symbol Not Found");
            }

            var data = list[0];
            double pcnt = 0;
            double.TryParse(data.GetProperty("change_percentage").GetString(), out pcnt);

            var responseData = new
            {
                name = info.ApiSymbol,
                price = data.GetProperty("last").GetString(),
                change_percent = Math.Round(pcnt, 2),
                high = data.GetProperty("high_24h").GetString(),
                low = data.GetProperty("low_24h").GetString(),
                vol = data.GetProperty("quote_volume").GetString(),
                source = "GateIO"
            };

            return JsonSerializer.Serialize(responseData);
        }

        private static string ParseOkxResponse(string jsonStr, SymbolInfo info)
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.GetProperty("code").GetString() != "0")
            {
                throw new Exception($"OKX Error: {root.GetProperty("msg").GetString()}");
            }

            var list = root.GetProperty("data");
            if (list.GetArrayLength() == 0)
            {
                throw new Exception("Symbol Not Found");
            }

            var data = list[0];
            double open = 0;
            double last = 0;
            double.TryParse(data.GetProperty("open24h").GetString(), out open);
            double.TryParse(data.GetProperty("last").GetString(), out last);
            double pcnt = open > 0 ? Math.Round((last - open) / open * 100, 2) : 0;

            var responseData = new
            {
                name = info.ApiSymbol,
                price = data.GetProperty("last").GetString(),
                change_percent = pcnt,
                high = data.GetProperty("high24h").GetString(),
                low = data.GetProperty("low24h").GetString(),
                vol = data.GetProperty("volCcy24h").GetString(),
                source = "OKX"
            };

            return JsonSerializer.Serialize(responseData);
        }
    }
}
