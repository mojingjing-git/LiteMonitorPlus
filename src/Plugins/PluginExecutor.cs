using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteMonitor;
using LiteMonitor.src.SystemServices.InfoService;
using LiteMonitor.src.Plugins.Native;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// 插件执行引擎 (Refactored)
    /// 负责执行 API 请求、链式步骤、数据处理和结果注入
    /// </summary>
    public class PluginExecutor : IDisposable
    {
        private HttpClient _http;

        /// <summary>
        /// [C3] 在途租约表。
        /// <para>key = 请求身份摘要（见 <see cref="RequestIdentity"/>）；value = <c>Lazy&lt;Task&lt;string&gt;&gt;</c>，
        /// 以 <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> 保证**同一身份下任意时刻只有一条
        /// HTTP 请求在途**，且 <c>GetOrAdd</c> 的 valueFactory 不会直接启动请求。</para>
        /// <para>条目**不绑定**任何 <see cref="HttpClient"/> 实例：客户端被替换时本字典不受影响，
        /// 在途请求也不会被主动取消。</para>
        /// </summary>
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inflightRequests = new();
        private readonly object _httpLock = new object();

        // Key = 实例ID | 目标索引 | 步骤ID | 请求身份摘要
        private class CacheItem
        {
            public string RawResponse { get; set; } 
            public DateTime Timestamp { get; set; }
        }

        private readonly ConcurrentDictionary<string, CacheItem> _stepCache = new();

        public event Action OnSchemaChanged;

        public PluginExecutor()
        {
            InitializeDefaultClient();
            
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        private void InitializeDefaultClient()
        {
            lock (_httpLock)
            {
                _http = new HttpClient(new SocketsHttpHandler
                {
                    // 关键修复：允许 HttpClient 自动识别并解压 GZip 或 Deflate 数据
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                    // 【安全】不设置自定义证书校验回调：TLS 校验按系统信任链进行。
                    // 插件内容随取随执行，关闭校验等于允许中间人下发任意插件脚本。
                    // 企业 TLS 拦截/自签代理环境应引导用户导入根证书，而不是关闭校验。
                    // [Fix] Reduce pooled connection lifetime to ensure timely DNS updates and socket release
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    // [Fix] Limit connections per server to prevent ephemeral port exhaustion
                    MaxConnectionsPerServer = 20
                });
                _http.Timeout = TimeSpan.FromSeconds(10); 
                _http.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor++/1.0");
            }
        }

        /// <summary>[C5 #4] 全局重置冷却窗口：同一窗口内至多执行一次**真正**的客户端重置。</summary>
        private static readonly TimeSpan NetworkResetCooldown = TimeSpan.FromSeconds(60);

        private DateTime _lastNetworkReset = DateTime.MinValue;

        /// <summary>
        /// [C5] 客户端代次：每次真正重置 +1。
        /// 用于把"客户端被替换/释放导致的在途中止"识别成**普通异常**并给出可诊断分类，
        /// 而不是触发递归重置或放大成重置风暴。
        /// </summary>
        private int _clientGeneration;

        /// <summary>读取当前客户端代次（线程安全）。</summary>
        private int GetClientGeneration()
        {
            lock (_httpLock) return _clientGeneration;
        }

        /// <summary>
        /// [C5] 重置全局网络客户端（替换并释放 <see cref="HttpClient"/>）。
        /// <para><b>公开签名不可变</b>：调用点在 <c>PluginManager.cs</c>（另一个步骤的 ownership），
        /// 节流逻辑必须实现在本方法内部。</para>
        /// <para><b>上限规则（C5 #4）</b>：重置是**全局**动作而失败计数是**按实例**的，
        /// 因此用**全局冷却窗口**限流 —— 60 秒窗口内至多真正重置一次，
        /// 窗口内的重复调用是幂等空操作（不替换客户端、不释放资源、不递归重置）。
        /// 这样"单个实例连续失败"最多在窗口内引发一次全局重置，不会形成重置风暴。</para>
        /// <para><b>与在途请求的关系（C5 #1/#2）</b>：本方法**不清空**在途字典、
        /// **不主动取消**在途请求；在途条目不绑定 <see cref="HttpClient"/> 实例。
        /// 被释放客户端上的在途请求允许自然失败，失败走普通异常路径返回给等待者。</para>
        /// </summary>
        public void ResetNetworkClients()
        {
            lock (_httpLock)
            {
                // [Fix] Throttle resets to prevent resource exhaustion (e.g. during system sleep/wake cycle)
                // [C5 #4] 上限规则：同一时间窗内至多一次真正重置；窗口内重复调用是幂等空操作。
                if (_lastNetworkReset != DateTime.MinValue && (DateTime.Now - _lastNetworkReset) < NetworkResetCooldown) return;
                _lastNetworkReset = DateTime.Now;

                // [Fix] Properly dispose old clients to release sockets
                var oldHttp = _http;
                var oldProxies = new List<HttpClient>(_proxyClients.Values);

                // [C5] 代次推进：正在途的请求据此把中止归类为"客户端被替换"，走普通异常路径。
                _clientGeneration++;

                InitializeDefaultClient();
                
                // [Optimized] Clear proxy clients dictionary but do NOT dispose them.
                // Other threads might be using them. Removing them from the dictionary ensures
                // new requests get fresh clients, while old requests can finish (or fail safely).
                _proxyClients.Clear();
                
                // [Fix] Also reset native resolvers that might hold network state or cache
                CityCodeResolver.ResetClient();
                CryptoNative.ResetClient();

                // [Fix] Delay dispose old clients to allow inflight requests to complete (or timeout)
                // This prevents ObjectDisposedException while ensuring Sockets are eventually released
                Task.Run(async () => 
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    try 
                    {
                        oldHttp?.Dispose();
                        foreach(var c in oldProxies) c.Dispose();
                    }
                    catch {}
                });
            }
            
        }

        public void Dispose()
        {
            _http?.Dispose();
            foreach (var client in _proxyClients.Values)
            {
                client.Dispose();
            }
            _proxyClients.Clear();
            _stepCache.Clear();
        }

        public void ClearCache(string instanceId = null)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                _stepCache.Clear();
            }
            else
            {
                // [C4] 缓存键格式 = 实例ID | 目标索引 | 步骤ID | 摘要。
                // 用"实例ID + 分隔符"做前缀匹配：既能按实例清理，又不会把
                // 实例 "AB" 的条目误当成实例 "A" 的条目一起清掉。
                string prefix = instanceId + "|";
                var keysToRemove = _stepCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                foreach (var k in keysToRemove) _stepCache.TryRemove(k, out _);
            }
        }

        public async Task<bool> ExecuteInstanceAsync(PluginInstanceConfig inst, PluginTemplate tmpl, System.Threading.CancellationToken token = default)
        {
            if (inst == null || tmpl == null) return false;

            var targets = inst.Targets != null && inst.Targets.Count > 0 ? inst.Targets : new List<Dictionary<string, string>> { new Dictionary<string, string>() };

            var tasks = new List<Task<bool>>();
            for (int i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                var idx = i; 
                // Explicitly specify Task<bool> to avoid ambiguity
                tasks.Add(Task.Run<bool>(async () => 
                {
                    if (token.IsCancellationRequested) return false;

                    if (idx > 0) 
                    {
                        try { await Task.Delay(idx * 50, token); } catch (OperationCanceledException) { return false; }
                    }

                    var mergedInputs = new Dictionary<string, string>(inst.InputValues);
                    foreach (var kv in targets[idx]) mergedInputs[kv.Key] = kv.Value;
                    
                    if (tmpl.Inputs != null)
                    {
                        foreach (var input in tmpl.Inputs)
                        {
                            if (!mergedInputs.ContainsKey(input.Key)) mergedInputs[input.Key] = input.DefaultValue;
                        }
                    }

                    string keySuffix = (inst.Targets != null && inst.Targets.Count > 0) ? $".{idx}" : "";
                    
                    return await ExecuteSingleTargetAsync(inst, tmpl, mergedInputs, keySuffix, token);
                }, token));
            }
            try
            {
                var results = await Task.WhenAll(tasks);
                // If any target succeeded, we consider it a partial success (or maybe we require all? let's be lenient for retry logic)
                // If ALL failed, return false to trigger fast retry.
                return results.Any(x => x);
            }
            catch (OperationCanceledException) { return false; }
        }

        private async Task<bool> ExecuteSingleTargetAsync(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix, System.Threading.CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested) return false;

                string url = PluginProcessor.ResolveTemplate(tmpl.Execution.Url, inputs);
                string body = PluginProcessor.ResolveTemplate(tmpl.Execution.Body ?? "", inputs);

                string resultRaw = "";
                // Handle legacy execution types by mapping them to steps internally or executing directly
                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "api_text")
                {
                    // Convert legacy single-request to a "step" concept for consistent execution
                    var step = new PluginExecutionStep
                    {
                        Url = url,
                        Body = body,
                        Method = tmpl.Execution.Method,
                        Headers = tmpl.Execution.Headers,
                        ResponseEncoding = null 
                    };

                    // Direct fetch (no caching logic for legacy root level yet, or use step logic?)
                    // For simplicity and backward compatibility, we execute directly here but reuse Fetch helper
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var resolvedHeaders = ResolveHeaders(step.Headers, inputs);
                    resultRaw = await FetchRawAsync(step.Method, url, body, resolvedHeaders, step.ResponseEncoding, token);
                    sw.Stop();
                    inputs["__latency__"] = sw.ElapsedMilliseconds.ToString();
                }

                if (tmpl.Execution.Type == "api_json" || tmpl.Execution.Type == "chain")
                {
                    if (tmpl.Execution.Type == "chain")
                    {
                        if (tmpl.Execution.Steps != null)
                        {
                            foreach (var step in tmpl.Execution.Steps)
                            {
                                if (token.IsCancellationRequested) return false;
                                await ExecuteStepAsync(inst, step, inputs, keySuffix, token);
                            }
                        }
                    }
                    else
                    {
                        // Legacy api_json processing
                        ParseAndExtract(resultRaw, tmpl.Execution.Extract, inputs, "json");
                    }

                    PluginProcessor.ApplyTransforms(tmpl.Execution.Process, inputs);
                    
                    if (tmpl.Outputs != null)
                    {
                        ProcessOutputs(inst, tmpl, inputs, keySuffix);
                    }
                }
                else
                {
                     // api_text
                     string injectKey = inst.Id + keySuffix;
                     InfoService.Instance.InjectValue(injectKey, resultRaw);
                }
                return true;
            }
            catch (OperationCanceledException opEx) 
            {
                if (token.IsCancellationRequested) return false;
                
                // If token is NOT cancelled, it's a timeout. Treat as error to trigger reset logic.
                HandleExecutionError(inst, tmpl, inputs, keySuffix, opEx);
                return false;
            }
            catch (Exception ex)
            {
                 HandleExecutionError(inst, tmpl, inputs, keySuffix, ex);
                 return false;
            }
        }

        private async Task ExecuteStepAsync(PluginInstanceConfig inst, PluginExecutionStep step, Dictionary<string, string> context, string keySuffix, System.Threading.CancellationToken token)
        {
            try {
                // 0. Check Skip Condition
                if (!string.IsNullOrEmpty(step.SkipIfSet))
                {
                    if (context.TryGetValue(step.SkipIfSet, out var val) && !string.IsNullOrEmpty(val))
                    {
                        return; // Skip this step
                    }
                }

                string url = PluginProcessor.ResolveTemplate(step.Url, context);

                // [Fix] Check for invalid parameters in URL (e.g. "?") to prevent downstream errors
                if (url.Contains(PluginConstants.STATUS_UNKNOWN) || url.Contains(PluginConstants.STATUS_ERROR))
                {
                     throw new Exception($"Step {step.Id} blocked: URL contains invalid parameter. URL: {url}");
                }

                string body = PluginProcessor.ResolveTemplate(step.Body ?? "", context);

                // [C1] 代理与请求头必须**先于**缓存/在途判定解析出来：
                // 它们是请求身份的一部分（凭据/代理串用正是从这里来的）。
                string proxy = step.Proxy ?? "";
                if (proxy.Contains("{{"))
                {
                    proxy = PluginProcessor.ResolveTemplate(proxy, context);
                }
                // 1. 先解析 Header 中的占位符 (例如 Authorization: Bearer {{token}})
                var resolvedHeaders = ResolveHeaders(step.Headers, context);

                // [C1] 请求身份：实例ID / 目标索引 / 步骤ID 沿用现行键里的三个维度，
                // 摘要则覆盖**全部影响响应**的字段（方法 / 规范化URL / 请求体 /
                // 全部步骤级Header / 代理 / 响应编码）。
                var identity = new RequestIdentity(inst.Id, keySuffix, step.Id, step.Method, url, body,
                                                  resolvedHeaders, proxy, step.ResponseEncoding);

                // [C4] 缓存键保留实例前缀（ClearCache(inst.Id) 依旧按实例清理）；
                //       在途键只用摘要 —— 摘要相同即"同请求同响应"，允许跨实例合并。
                string cacheKey = $"{identity.InstanceId}|{identity.TargetIndex}|{identity.StepId}|{identity.Digest}";
                string inflightKey = identity.Digest;

                bool hit = false;
                string resultRaw = "";

                if (step.CacheMinutes > 0)
                {
                    if (_stepCache.TryGetValue(cacheKey, out var cached))
                    {
                        if (DateTime.Now - cached.Timestamp < TimeSpan.FromMinutes(step.CacheMinutes))
                        {
                            resultRaw = cached.RawResponse;
                            hit = true;
                        }
                        else
                        {
                            _stepCache.TryRemove(cacheKey, out _); 
                        }
                    }
                }

                if (!hit)
                {
                    // [Feature] Support Native Native Handlers (Weather, Crypto)
                    if (url.StartsWith("native://"))
                    {
                        resultRaw = await ExecuteNativeAsync(url, context);
                    }
                    else
                    {
                        // [C3] 在途租约：同一身份共享同一条请求。
                        // valueFactory 只创建未求值的 Lazy，不在这里启动 HTTP 请求；
                        // 共享请求以 CancellationToken.None 启动 —— 取消单个等待者
                        // 不会取消共享任务，也不影响其他等待者。
                        var task = GetOrStartInflight(inflightKey, () =>
                            FetchRawAsync(step.Method, url, body, resolvedHeaders, step.ResponseEncoding, System.Threading.CancellationToken.None, proxy));

                        // 等待者取消只释放自己：不触碰在途条目、不取消共享任务。
                        // 条目的移除由共享任务终结时的延续驱动（见 ReleaseInflight），
                        // 等待者的 finally 一律不参与移除。
                        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (token.Register(() => tcs.TrySetCanceled()))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var finishedTask = await Task.WhenAny(task, tcs.Task);
                            if (finishedTask == tcs.Task) throw new OperationCanceledException(token);
                            resultRaw = await task;
                            sw.Stop();
                            context["__latency__"] = sw.ElapsedMilliseconds.ToString();
                        }
                    }
                }

                // Parse
                ParseAndExtract(resultRaw, step.Extract, context, step.ResponseFormat);
                
                // Process
                PluginProcessor.ApplyTransforms(step.Process, context);

                // [Fix] Validate extracted data before caching
                // If critical data is missing (Unknown/Error), THROW exception to prevent downstream steps from executing with bad data
                if (step.Extract != null)
                {
                    foreach (var kv in step.Extract)
                    {
                        if (context.TryGetValue(kv.Key, out string val))
                        {
                            if (val == PluginConstants.STATUS_UNKNOWN || val == PluginConstants.STATUS_ERROR)
                            {
                                // [Fix] Fail fast if critical extraction failed
                                throw new Exception($"Step {step.Id} failed: Extracted invalid value for '{kv.Key}'. Value: '{val}'");
                            }
                        }
                    }
                }

                // [Fix] Update Cache AFTER successful parsing and processing
                // This ensures we don't cache invalid data (e.g. HTML error pages that passed HTTP check but failed JSON parse)
                if (!hit && step.CacheMinutes != 0)
                {
                    // [Optimization] Prevent large blobs from polluting cache (limit 500KB)
                    if (resultRaw != null && resultRaw.Length > 500 * 1024)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plugin response too large to cache ({resultRaw.Length} bytes). Key: {cacheKey}");
                    }
                    else
                    {
                        // [Optimization] Prevent unbounded growth with timestamp-based eviction
                        if (_stepCache.Count > 100)
                        {
                            // Evict oldest items first
                            var keysToRemove = _stepCache
                                .OrderBy(kv => kv.Value.Timestamp)
                                .Take(20)
                                .Select(kv => kv.Key)
                                .ToList();
                                
                            foreach(var k in keysToRemove) _stepCache.TryRemove(k, out _);
                        }

                        _stepCache[cacheKey] = new CacheItem
                        {
                            RawResponse = resultRaw,
                            Timestamp = DateTime.Now
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Step {step.Id} Error: {ex.Message}");
                throw; 
            }
        }

        // Proxy Client Cache: Key = "host:port" or "http://host:port"
        private readonly ConcurrentDictionary<string, HttpClient> _proxyClients = new();

        private HttpClient GetClient(string proxy)
        {
            if (string.IsNullOrEmpty(proxy))
            {
                lock (_httpLock) return _http;
            }

            return _proxyClients.GetOrAdd(proxy.Trim(), p => 
            {
                var handler = new SocketsHttpHandler();
                handler.Proxy = new System.Net.WebProxy(p);
                handler.UseProxy = true;
                // 补上这一行，确保代理请求也能解压缩
                handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
                // 【安全】不设置自定义证书校验回调：TLS 校验按系统信任链进行。
                // [Fix] Limit pooled connections for proxies too
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);
                handler.MaxConnectionsPerServer = 20;
                
                var client = new HttpClient(handler);
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor++/1.0");
                return client;
            });
        }

        private async Task<string> ExecuteNativeAsync(string url, Dictionary<string, string> context)
        {
            // url: native://citycode?province=...
            var uri = new Uri(url);
            
            // Simple Query Parse
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(uri.Query))
            {
                var q = uri.Query.TrimStart('?');
                foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length == 2) 
                    {
                        args[kv[0]] = System.Net.WebUtility.UrlDecode(kv[1]);
                    }
                }
            }

            if (uri.Host.Equals("citycode", StringComparison.OrdinalIgnoreCase))
            {
                string p = args.ContainsKey("province") ? args["province"] : "";
                string c = args.ContainsKey("city") ? args["city"] : "";
                string d = args.ContainsKey("district") ? args["district"] : "";
                return await CityCodeResolver.ResolveAsync(p, c, d);
            }
            else if (uri.Host.Equals("crypto", StringComparison.OrdinalIgnoreCase))
            {
                string s = args.ContainsKey("symbol") ? args["symbol"] : "BTC";
                string fb = args.ContainsKey("fallback") ? args["fallback"] : "";
                return await CryptoNative.FetchAsync(s, fb);
            }
            
            throw new Exception($"Unknown native host: {uri.Host}");
        }

        /// <summary>响应体读取上限（字节）默认值。超出即明确失败，防止异常插件把内存打爆。</summary>
        private const int DefaultMaxResponseBytes = 8 * 1024 * 1024;

        /// <summary>
        /// [LM-003a] 响应体字节上限（实例级）。设为 &lt;= 0 时回落到默认值。
        /// </summary>
        private int _maxResponseBytes = DefaultMaxResponseBytes;

        private async Task<string> FetchRawAsync(string methodStr, string url, string body, Dictionary<string, string> headers, string encoding, System.Threading.CancellationToken token, string proxy = null)
        {
            HttpMethod method = HttpMethod.Get;
            if (methodStr?.ToUpper() == "POST") method = HttpMethod.Post;
            else if (methodStr?.ToUpper() == "HEAD") method = HttpMethod.Head;
            
            // Log($"[Start] {method} {url} Proxy:{proxy ?? "None"}");

            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post && !string.IsNullOrEmpty(body))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            if (headers != null)
            {
                foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            var client = GetClient(proxy);
            int generation = GetClientGeneration();
            try
            {
                // [LM-003a] 响应必须被释放；正文读取有字节上限。
                // 用 ResponseHeadersRead + 自己的期限，使"正文读取"仍在客户端超时预算内
                // （HttpClient.Timeout 只覆盖到响应头，因此这里用同长的链接令牌补齐总期限，
                //  用户可见的超时语义不变）。
                using var deadline = new CancellationTokenSource(client.Timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                // Log($"[End] {url} Status:{response.StatusCode}");

                // [Fix] Enforce success status code to prevent caching error pages (e.g. 404/500 HTML)
                response.EnsureSuccessStatusCode();
                
                // [Optimization] HEAD request optimization
                if (method == HttpMethod.Head) return "";

                byte[] bytes = await ReadBodyWithLimitAsync(response, linked.Token);
                
                if (encoding?.ToLower() == "gbk")
                {
                    return Encoding.GetEncoding("GBK").GetString(bytes);
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 调用方主动取消：保持取消语义原样上抛（不归类为客户端被替换）。
                Debug.WriteLine($"[Cancel] {url}");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Error] {url} {ex.Message}");
                // Log($"[Error] {url} {ex.Message}");

                // [C5 #3] 客户端被重置/释放导致的中止：**走普通异常路径**返回给等待者，
                // 只补一个可诊断分类，绝不触发递归重置、绝不放大成重置风暴。
                if (GetClientGeneration() != generation)
                {
                    throw new PluginClientDisposedException(url, ex);
                }
                throw;
            }
        }

        /// <summary>
        /// [LM-003a] 带**字节上限**的正文读取。
        /// <list type="bullet">
        /// <item>声明长度已知且超限 → 立即明确失败（不截断、不静默丢数据）。</item>
        /// <item>边读边计数，实际字节数超限 → 同样明确失败。</item>
        /// <item>超限时 <see cref="HttpResponseMessage"/> 仍由 using 释放 → 连接被归还，不会泄漏。</item>
        /// </list>
        /// </summary>
        private async Task<byte[]> ReadBodyWithLimitAsync(HttpResponseMessage response, CancellationToken token)
        {
            int limit = _maxResponseBytes > 0 ? _maxResponseBytes : DefaultMaxResponseBytes;
            string url = response?.RequestMessage?.RequestUri?.ToString();

            long? declared = response?.Content?.Headers?.ContentLength;
            if (declared.HasValue && declared.Value > limit)
            {
                throw new PluginResponseTooLargeException(url, declared.Value, limit);
            }

            using var stream = await response.Content.ReadAsStreamAsync(token);
            byte[] buffer = new byte[8192];
            using var ms = new MemoryStream();
            long total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read <= 0) break;
                total += read;
                if (total > limit)
                {
                    throw new PluginResponseTooLargeException(url, total, limit);
                }
                ms.Write(buffer, 0, read);
            }
            return ms.ToArray();
        }

        private void ParseAndExtract(string resultRaw, Dictionary<string, string> extractRules, Dictionary<string, string> context, string format = "json")
        {
            if (extractRules == null || extractRules.Count == 0) return;

            string json = resultRaw.Trim();
            string fmt = format?.ToLower() ?? "json";
            
            if (fmt == "jsonp")
            {
                if (json.StartsWith("(") && json.EndsWith(")"))
                {
                    json = json.Substring(1, json.Length - 2);
                }
            }
            
            if (fmt == "json" || fmt == "jsonp")
            {
                if (json.StartsWith("{") || json.StartsWith("["))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    foreach (var kv in extractRules)
                    {
                        // Support dynamic paths like "rates.{{to}}"
                        string resolvedPath = PluginProcessor.ResolveTemplate(kv.Value, context);
                        context[kv.Key] = PluginProcessor.ExtractJsonValue(root, resolvedPath);
                    }
                }
            }
            else if (fmt == "text")
            {
                foreach (var kv in extractRules)
                {
                    if (kv.Value == "$")
                    {
                        context[kv.Key] = resultRaw;
                    }
                }
            }
        }

        private void ProcessOutputs(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix)
        {
            // [Optimization] Use Unified KeyCache from PluginInstanceConfig
            // Cache Key format: "{keySuffix}.{outputKey}" (e.g. ".0.main" or ".main")
            
            foreach (var output in tmpl.Outputs)
            {
                string cacheKey = keySuffix + "." + output.Key;
                
                var keys = inst.KeyCache.GetOrAdd(cacheKey, _ => 
                {
                    var k = new PluginOutputKeys();
                    
                    // 1. InjectKey: "Id.0.Key"
                    string rawInjectKey = inst.Id + keySuffix + "." + output.Key;
                    k.InjectKey = UIUtils.Intern(rawInjectKey);

                    // 2. Attribute Keys
                    k.InjectColorKey = UIUtils.Intern(rawInjectKey + ".Color");
                    k.InjectUnitKey = UIUtils.Intern(rawInjectKey + ".Unit");

                    // 3. Prop Keys (DASH prefix)
                    string itemKey = PluginConstants.DASH_PREFIX + rawInjectKey;
                    k.PropLabelKey = UIUtils.Intern("PROP.Label." + itemKey);
                    k.PropShortKey = UIUtils.Intern("PROP.ShortLabel." + itemKey);
                    
                    return k;
                });

                // 1. Value Injection
                string val = PluginProcessor.ResolveTemplate(output.Format, inputs);
                if (string.IsNullOrEmpty(val)) val = PluginConstants.STATUS_EMPTY;
                
                // [Optimization] Check existing value to avoid string churn
                string currentVal = InfoService.Instance.GetValue(keys.InjectKey);
                if (val != currentVal)
                {
                    InfoService.Instance.InjectValue(keys.InjectKey, val);
                }

                // 2. Color Injection
                if (!string.IsNullOrEmpty(output.Color))
                {
                    string colorState = PluginProcessor.ResolveTemplate(output.Color, inputs);
                    string currentColor = InfoService.Instance.GetValue(keys.InjectColorKey);
                    
                    if (colorState != currentColor)
                    {
                        InfoService.Instance.InjectValue(keys.InjectColorKey, colorState);
                    }
                }

                // 3. Unit Injection
                if (!string.IsNullOrEmpty(output.Unit))
                {
                    string resolvedUnit = PluginProcessor.ResolveTemplate(output.Unit, inputs);
                    string currentUnit = InfoService.Instance.GetValue(keys.InjectUnitKey);

                    if (resolvedUnit != currentUnit)
                    {
                        InfoService.Instance.InjectValue(keys.InjectUnitKey, resolvedUnit);
                    }
                }

                // 4. Dynamic Label Logic
                string labelPattern = !string.IsNullOrEmpty(output.Label) ? PluginI18n.Pick(output.Label, output.LabelEn) : (PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn) + " " + output.Key);
                
                string newName = PluginProcessor.ResolveTemplate(labelPattern, inputs);
                string newShort = PluginProcessor.ResolveTemplate(PluginI18n.Pick(output.ShortLabel ?? "", output.ShortLabelEn ?? ""), inputs);
                
                // Apply default values for missing inputs in labels
                if (tmpl.Inputs != null)
                {
                    foreach (var input in tmpl.Inputs)
                    {
                        if (!inputs.ContainsKey(input.Key))
                        {
                            newName = newName.Replace("{{" + input.Key + "}}", input.DefaultValue);
                            newShort = newShort.Replace("{{" + input.Key + "}}", input.DefaultValue);
                        }
                    }
                }

                // [Optimization] Use cached Prop keys
                if (InfoService.Instance.GetValue(keys.PropLabelKey) != newName)
                {
                    InfoService.Instance.InjectValue(keys.PropLabelKey, newName);
                }

                if (InfoService.Instance.GetValue(keys.PropShortKey) != newShort)
                {
                    InfoService.Instance.InjectValue(keys.PropShortKey, newShort);
                }
            }
        }

        private void HandleExecutionError(PluginInstanceConfig inst, PluginTemplate tmpl, Dictionary<string, string> inputs, string keySuffix, Exception ex)
        {
            // [Fix] Removed aggressive ResetNetworkClients() on single error.
            // Let PluginManager handle network resets based on consecutive failures to avoid client storm.

            // [Improvement] Even on error, try to resolve labels if possible (so user knows which item failed)
            if (tmpl.Outputs != null)
            {
                foreach(var output in tmpl.Outputs) 
                {
                    string injectKey = inst.Id + keySuffix + "." + output.Key;
                    
                    // Inject Error Status
                    InfoService.Instance.InjectValue(injectKey, PluginConstants.STATUS_ERROR);

                    // Try resolve label (ignore errors in template resolution)
                    try 
                    {
                        string itemKey = PluginConstants.DASH_PREFIX + injectKey;
                        string labelPattern = !string.IsNullOrEmpty(output.Label) ? PluginI18n.Pick(output.Label, output.LabelEn) : (PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn) + " " + output.Key);
                        
                        string newName = PluginProcessor.ResolveTemplate(labelPattern, inputs);
                        string newShort = PluginProcessor.ResolveTemplate(PluginI18n.Pick(output.ShortLabel ?? "", output.ShortLabelEn ?? ""), inputs);

                        // Apply default values for missing inputs in labels
                        if (tmpl.Inputs != null)
                        {
                            foreach (var input in tmpl.Inputs)
                            {
                                if (!inputs.ContainsKey(input.Key))
                                {
                                    newName = newName.Replace("{{" + input.Key + "}}", input.DefaultValue);
                                    newShort = newShort.Replace("{{" + input.Key + "}}", input.DefaultValue);
                                }
                            }
                        }

                        string propLabelKey = "PROP.Label." + itemKey;
                        string propShortKey = "PROP.ShortLabel." + itemKey;

                        if (InfoService.Instance.GetValue(propLabelKey) != newName)
                            InfoService.Instance.InjectValue(propLabelKey, newName);

                        if (InfoService.Instance.GetValue(propShortKey) != newShort)
                            InfoService.Instance.InjectValue(propShortKey, newShort);
                    }
                    catch (Exception lblEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Label resolution failed during error handling: {lblEx.Message}");
                        // [Fix] Fallback to raw pattern if template resolution fails, to avoid showing raw keys like DASH.GitHub.0.stats
                        try 
                        {
                            string itemKey = PluginConstants.DASH_PREFIX + injectKey;
                            string fallback = !string.IsNullOrEmpty(output.Label) ? PluginI18n.Pick(output.Label, output.LabelEn) : (PluginI18n.Pick(tmpl.Meta.Name, tmpl.Meta.NameEn) + " " + output.Key);
                            
                            // Remove template markers to make it look cleaner if possible
                            if (fallback.Contains("{{")) 
                            {
                                // Simple cleanup: remove {{...}} parts or keep them, keeping them is better than nothing
                            }

                            InfoService.Instance.InjectValue(UIUtils.Intern("PROP.Label." + itemKey), fallback);
                        }
                        catch {}
                    }
                }
            }
            System.Diagnostics.Debug.WriteLine($"Plugin exec error ({inst.Id}): {ex.Message}");
        }

        private Dictionary<string, string> ResolveHeaders(Dictionary<string, string> headers, Dictionary<string, string> context)
        {
            if (headers == null || headers.Count == 0) return null;
            var resolved = new Dictionary<string, string>();
            foreach (var kv in headers)
            {
                resolved[kv.Key] = PluginProcessor.ResolveTemplate(kv.Value, context);
            }
            return resolved;
        }

        // =====================================================================
        // [C1] 请求身份
        // =====================================================================

        /// <summary>
        /// 请求身份（C1）。
        /// <para>维度分两类：</para>
        /// <list type="bullet">
        /// <item><b>路由维度</b>：实例ID / 目标索引 / 步骤ID —— 沿用现行键里已有的三个维度，
        /// **不**参与摘要计算（否则会与缓存键里的实例前缀重复编码）。</item>
        /// <item><b>响应维度</b>：方法 / 规范化URL / 请求体 / 全部步骤级Header / 代理 / 响应编码 ——
        /// 这些决定服务端返回什么，全部进入摘要。</item>
        /// </list>
        /// <para>摘要（<see cref="Digest"/>）是**纯函数**：同一组输入在任何进程、任何次运行
        /// 都得到同一个值（不依赖 <c>GetHashCode()</c> 的进程内加盐）。</para>
        /// </summary>
        private sealed class RequestIdentity
        {
            public string InstanceId { get; }
            public string TargetIndex { get; }
            public string StepId { get; }
            public string Method { get; }
            public string CanonicalUrl { get; }
            public string Body { get; }
            public string Proxy { get; }
            public string ResponseEncoding { get; }
            public string Digest { get; }

            public RequestIdentity(string instanceId, string targetIndex, string stepId, string method,
                                   string url, string body, Dictionary<string, string> headers,
                                   string proxy, string responseEncoding)
            {
                InstanceId = instanceId ?? "";
                TargetIndex = targetIndex ?? "";
                StepId = stepId ?? "";
                Method = NormalizeMethod(method);
                CanonicalUrl = CanonicalizeUrl(url);
                Body = body ?? "";
                Proxy = proxy ?? "";
                ResponseEncoding = responseEncoding ?? "";
                Digest = ComputeRequestDigest(Method, CanonicalUrl, Body, headers, Proxy, ResponseEncoding);
            }
        }

        /// <summary>HTTP 方法大写规范化；未指定按 GET。</summary>
        private static string NormalizeMethod(string method)
        {
            if (string.IsNullOrWhiteSpace(method)) return "GET";
            return method.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// URL 规范化：scheme / host 转小写；去掉 fragment；path 与 query 原样保留
        /// （不重排 query —— 顺序可能影响语义）；非默认端口保留；
        /// userinfo（可能携带 Basic 凭据）保留，不能丢。
        /// </summary>
        private static string CanonicalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

            var sb = new StringBuilder(url.Length + 8);
            sb.Append(uri.Scheme.ToLowerInvariant()).Append("://");
            if (!string.IsNullOrEmpty(uri.UserInfo)) sb.Append(uri.UserInfo).Append('@');
            sb.Append(uri.Host.ToLowerInvariant());
            if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);
            sb.Append(uri.AbsolutePath);
            sb.Append(uri.Query);
            return sb.ToString();
        }

        /// <summary>
        /// Header 全部参与身份（**不做白名单**：漏掉一个携带凭据的 header 造成的串用，
        /// 比合并机会减少严重得多）。名统一小写（HTTP 头名大小写无关），
        /// 按 (名, 值) 排序，保证拼接顺序确定。
        /// </summary>
        private static List<KeyValuePair<string, string>> SortHeaders(Dictionary<string, string> headers)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (headers == null) return list;
            foreach (var kv in headers)
            {
                list.Add(new KeyValuePair<string, string>(kv.Key?.ToLowerInvariant() ?? "", kv.Value ?? ""));
            }
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
            });
            return list;
        }

        /// <summary>
        /// [C2] 请求身份摘要：SHA-256，输出十六进制后取**前 32 个字符**（128 位）。
        /// <para>拼接采用"长度前缀 + 冒号"的无歧义编码，字段顺序固定：</para>
        /// <code>版本 | 方法 | 规范化URL | 请求体 | 响应编码 | 代理 | Header数量 | (Header名, Header值)*</code>
        /// <para>**禁止**使用 <c>GetHashCode()</c>：它是进程内加盐的 32 位弱摘要，
        /// 不同进程/不同次运行结果不同，且碰撞概率不可忽略。</para>
        /// </summary>
        private static string ComputeRequestDigest(string method, string canonicalUrl, string body,
            Dictionary<string, string> headers, string proxy, string responseEncoding)
        {
            var sorted = SortHeaders(headers);
            var sb = new StringBuilder(256);
            sb.Append("lm-plugin-req-v1");
            AppendDigestField(sb, method);
            AppendDigestField(sb, canonicalUrl);
            AppendDigestField(sb, body);
            AppendDigestField(sb, responseEncoding);
            AppendDigestField(sb, proxy);
            AppendDigestField(sb, sorted.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var h in sorted)
            {
                AppendDigestField(sb, h.Key);
                AppendDigestField(sb, h.Value);
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash).Substring(0, 32);
        }

        private static void AppendDigestField(StringBuilder sb, string value)
        {
            string v = value ?? "";
            sb.Append(v.Length).Append(':').Append(v);
        }

        // =====================================================================
        // [C3] 在途租约模型
        // =====================================================================

        /// <summary>
        /// [C3] 获取（必要时启动）在途请求。
        /// <para><b>原子获取</b>：<c>GetOrAdd</c> 的 valueFactory 只负责**创建**一个未求值的
        /// <see cref="Lazy{T}"/>，**不**启动 HTTP 请求；<c>Lazy</c> 使用
        /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>，保证同一把 Lazy 只被求值一次。
        /// <c>GetOrAdd</c> 返回的是字典里"实际存着的"那把 —— 落败的 factory 产物永不被求值，
        /// 因此同一身份下不会发出第二条 HTTP 请求。</para>
        /// <para><b>移除时机</b>：条目只在共享任务**终结**时由任务延续驱动移除一次。</para>
        /// </summary>
        private Task<string> GetOrStartInflight(string key, Func<Task<string>> factory)
        {
            Lazy<Task<string>> mine = null;
            mine = new Lazy<Task<string>>(() =>
            {
                Task<string> task;
                try { task = factory(); }
                catch (Exception ex) { task = Task.FromException<string>(ex); }

                // [C3 #3] 共享任务终结时移除条目（只此一次），由任务自身的延续驱动。
                // 同时观察异常：即使所有等待者都取消了，失败也不会变成"未观察异常"。
                _ = task.ContinueWith(t =>
                {
                    _ = t.Exception;
                    ReleaseInflight(key, mine);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            var winner = _inflightRequests.GetOrAdd(key, _ => mine);
            return winner.Value;
        }

        /// <summary>
        /// [C3 #2] **比较后**原子移除：只有字典中该键当前映射的值**仍是本条目**时才删除。
        /// 这样旧任务的收尾不会误删同键的新条目。禁止无条件 <c>TryRemove(key, out _)</c>。
        /// </summary>
        private void ReleaseInflight(string key, Lazy<Task<string>> entry)
        {
            if (entry == null) return;
            _inflightRequests.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(key, entry));
        }
    }

    /// <summary>
    /// [LM-003a] 插件响应体超过字节上限时抛出：**明确失败**，不截断、不静默丢数据。
    /// 抛出时响应仍会被释放，连接随之归还。
    /// </summary>
    public sealed class PluginResponseTooLargeException : Exception
    {
        /// <summary>服务端声明的响应体长度（字节）；未知时为 null。</summary>
        public long? DeclaredLength { get; }
        /// <summary>本次生效的上限（字节）。</summary>
        public int Limit { get; }

        public PluginResponseTooLargeException(string url, long? declaredLength, int limit)
            : base($"Plugin response exceeded the {limit} byte limit (declared={declaredLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, url={url})")
        {
            DeclaredLength = declaredLength;
            Limit = limit;
        }
    }

    /// <summary>
    /// [C5 #3] 客户端被替换/释放导致在途请求中止时的**可诊断**异常。
    /// 它走**普通异常路径**返回给等待者：不会触发递归重置，也不会放大成重置风暴。
    /// </summary>
    public sealed class PluginClientDisposedException : Exception
    {
        public PluginClientDisposedException(string url, Exception inner)
            : base($"Network client was replaced while the request was in flight (url={url}): {inner?.Message}", inner)
        {
        }
    }
}
