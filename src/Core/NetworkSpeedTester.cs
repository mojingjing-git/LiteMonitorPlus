using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Security; // ★★★ 新增：解决 SslClientAuthenticationOptions 引用 ★★★

namespace LiteMonitor
{
    public static class NetworkSpeedTester
    {
        // 增加 HttpClient 实例的连接池，避免频繁创建连接
        private static readonly HttpClient http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), // 连接重用
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 32 // 增加到服务器的最大连接数
            // 不设置自定义证书校验回调：TLS 校验按系统信任链进行。
            // 曾为「解决用户证书报错」整体关闭校验，等于允许中间人伪造测速响应；
            // 企业 TLS 拦截环境应引导用户导入根证书，而不是关闭校验。
        })
        {
            Timeout = TimeSpan.FromSeconds(25) // 增加全局超时时间，为下载预留更多时间
        };

        private static readonly Random _rng = new Random();
        private static string Rand() { lock (_rng) return _rng.NextDouble().ToString("0.0000000000"); }

        // ======================================================
        // 0. HTTP 期限口径（LM-003c / 契约 H5）
        // ======================================================
        //
        // ★ HttpClient.Timeout 在 ResponseHeadersRead 模式下**只覆盖到响应头**：
        //   响应头一到，HttpClient 内部的超时 CTS 就被释放，之后的正文读取不再受它约束。
        //   所以正文阶段必须自建期限，否则"只发响应头、正文永远不来"的镜像
        //   能把测速线程一直挂住（客户端级 25s 超时在这里帮不上忙）。
        //
        // ★ 三段期限分别是：
        //   ① 连接与响应头期限：竞速阶段 = 竞速窗口 + 2s（保持既有预算）；
        //      工作线程 = 20s（保持既有"单个请求 20 秒"预算）。
        //   ② 正文读取期限：**无进展期限** —— 连续多久一个字节都没读到就判定镜像已死。
        //      这里刻意不用"正文总期限"：测速正文是持续流，总期限会把正常的长测速一起砍掉；
        //      真要防的是"响应头来了、正文不来"这种挂死，所以盯的是**进展**而不是时长。
        //   ③ 总期限：竞速阶段 = 竞速窗口 + 2s（一次尝试的整段预算）；
        //      工作线程 = 整次测速的结束期限（既有 cts）。
        internal static readonly TimeSpan RaceBodyStallTimeout = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan DownloadHeadersTimeout = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan DownloadBodyStallTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 在"无进展期限"下读取正文。
        /// </summary>
        /// <param name="stream">正文流。</param>
        /// <param name="buffer">读缓冲。</param>
        /// <param name="stallTimeout">无进展期限：连续这么久没读到任何字节就中断本次读取。</param>
        /// <param name="shouldContinue">是否继续读（竞速窗口 / 测速时长）。</param>
        /// <param name="outerToken">外层取消信号（整次尝试 / 整次测速的总期限）。</param>
        /// <param name="onChunk">每读到一块数据的回调（用于累计字节与上报进度）。</param>
        /// <returns>本次读到的字节数。</returns>
        /// <remarks>
        /// ★ 这个方法是"正文读取有独立取消期限"的落点：期限从**正文阶段开始**起算，
        ///   与"连接与响应头期限"彼此独立 —— 响应头慢不会把正文的预算吃掉。
        /// ★ 每次读到数据都会重置无进展计时，因此正常的持续流不会被误杀。
        /// </remarks>
        internal static async Task<long> ReadBodyWithStallDeadlineAsync(
            Stream stream,
            byte[] buffer,
            TimeSpan stallTimeout,
            Func<bool> shouldContinue,
            CancellationToken outerToken,
            Action<int>? onChunk = null)
        {
            long total = 0;

            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            stallCts.CancelAfter(stallTimeout);

            while (shouldContinue())
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length, stallCts.Token);
                if (n <= 0) break;                    // 正文结束（EOF）

                total += n;
                onChunk?.Invoke(n);

                // 有进展 → 重置无进展期限
                stallCts.CancelAfter(stallTimeout);
            }

            return total;
        }

        // 优化的下载源列表 - 按地区分组 (已包含所有优质节点)
        private static readonly string[] DownloadSources =
        {
            // --- 第一梯队：中国大陆国内镜像 (物理距离近，带宽易跑满) ---
            "https://mirrors.huaweicloud.com/repository/ubuntu/pool/main/l/linux/linux-source-5.4.0_5.4.0-89.100_all.deb?r=", // 华为云 CDN (国内极速)
            "https://mirrors.aliyun.com/debian/pool/main/g/gcc-10/gcc-10.2.1-6_amd64.deb?r=", // 阿里云 CDN (国内极速)
            "https://mirrors.tuna.tsinghua.edu.cn/ubuntu/pool/main/l/linux/linux-source-5.4.0_5.4.0-89.100_all.deb?r=",   // 清华大学 TUNA (教育网/三大运营商优化)

            // --- 第二梯队：全球CDN / 优质直连线路 (CN2 GIA/低延迟) ---
            "https://speed.cloudflare.com/__down?during=download&bytes=50000000&r=", // CloudFlare (全球CDN，虽有波动但覆盖最广)
            "https://lg.bandwagonhost.com/100MB.test?r=",       // 搬瓦工 洛杉矶 (CN2 GIA 线路，晚高峰不掉速)
            "http://la-cn2-gia.lg.dmit.io/100MB.test?r=",        // DMIT 洛杉矶 (CN2 GIA 高端线路)
            "http://hk-main.lg.misaka.io/100MB.test?r=",         // Misaka 香港 (大陆直连优化)

            // --- 第三梯队：亚洲地区常规节点 (原有列表 + 补充) ---
            "https://speedtest.hgc.jp/downloading?n=",                                                    // 日本 HGC (响应快)
            "https://seednet-ty1.seed.net.tw.prod.hosts.ooklaserver.net:8080/download?size=50000000&r=", // 台湾 Seednet (亚洲大带宽)
            "https://hnd-jp-ping.vultr.com/vultr.com.100MB.bin?r=",                                      // 日本 Vultr
            "https://sgp-ping.vultr.com/vultr.com.100MB.bin?r=",                                         // 新加坡 Vultr

            // --- 第四梯队：欧美备用节点 (延迟较高) ---
            "https://la.speedtest.clouvider.net/backend/garbage.php?cors=true&size=25000000&r=",  // 洛杉矶 Clouvider
            "https://ams.speedtest.clouvider.net/backend/garbage.php?cors=true&size=25000000&r=", // 阿姆斯特丹 Clouvider
            "https://atl.speedtest.clouvider.net/backend/garbage.php?cors=true&size=25000000&r=", // 亚特兰大 Clouvider
        };

        // 优化的上传源列表
        private static readonly string[] UploadSources =
        {
            // --- 第一梯队：全球 Anycast (智能路由，自动选最近) ---
            "https://speed.cloudflare.com/__up?measId=",   // CloudFlare (全球CDN上传，最稳健)

            // --- 第二梯队：亚洲优质节点 (物理距离近，上传损耗小) ---
            "https://tyo.speedtest.clouvider.net/backend/empty.php?cors=true&r=",        // 日本东京 Clouvider (对华路由优化，推荐)
            "https://speedtest.hgc.jp/upload?n=",                                        // 日本 HGC (原有，上传专用)
            "http://seednet-ty1.seed.net.tw.prod.hosts.ooklaserver.net:8080/upload?r=",  // 台湾 Seednet (大带宽节点)

            // --- 第三梯队：北美 (测试跨境带宽) ---
            "https://la.speedtest.clouvider.net/backend/empty.php?cors=true&r=",         // 洛杉矶 Clouvider (电信/联通友好)
            "http://los-angeles-ca-speedtest.reliablesite.net/empty.php?r=",             // 洛杉矶 ReliableSite (备用)

            // --- 第四梯队：欧洲 (原有备选) ---
            "https://ams.speedtest.clouvider.net/backend/empty.php?cors=true&r=",        // 阿姆斯特丹 Clouvider
            "https://atl.speedtest.clouvider.net/backend/empty.php?cors=true&r=",        // 亚特兰大 Clouvider
        };

        // 共享变量，用于存储动态的最佳 URL
        private static string currentBestDownloadUrl = DownloadSources[0];
        private static string currentBestUploadUrl = UploadSources[0];

        // 日志记录
        private static void Log(string message)
        {
            Debug.WriteLine($"[NetworkSpeedTester] {DateTime.Now:HH:mm:ss.fff} - {message}");
        }

        // ======================================================
        // 1. 优化的竞速逻辑
        // ======================================================
        internal static async Task<string> PickFastestAsync(
            string[] urls,
            int raceTimeMs,
            bool isDownload = true,
            TimeSpan? headersTimeout = null,
            TimeSpan? bodyStallTimeout = null,
            TimeSpan? totalTimeout = null)
        {
            // ① 连接与响应头期限：默认保持既有预算（竞速窗口 + 2s 容错）
            TimeSpan headerBudget = headersTimeout ?? TimeSpan.FromMilliseconds(raceTimeMs + 2000);
            // ② 正文读取期限：无进展期限
            TimeSpan stallBudget = bodyStallTimeout ?? RaceBodyStallTimeout;
            // ③ 总期限：一次尝试的整段预算
            TimeSpan totalBudget = totalTimeout ?? TimeSpan.FromMilliseconds(raceTimeMs + 2000);

            var tasks = new List<Task<(string url, long bytes, bool success)>>();

            foreach (var url in urls)
            {
                // 仅对前七个（国内和优质CN2）进行更快的竞速，其余作为备选 (这里只跳过奇数索引，减少并发压力)
                if (Array.IndexOf(urls, url) > 6 && Array.IndexOf(urls, url) % 2 == 0) continue; 
                
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[] buf = new byte[256 * 1024]; // 256KB 缓冲区
                        long bytes = 0;
                        int attempts = 0;
                        const int maxAttempts = 2; // 减少尝试次数

                        while (attempts < maxAttempts)
                        {
                            try
                            {
                                // ③ 总期限：覆盖"连接+响应头+正文"整段
                                using var attemptCts = new CancellationTokenSource(totalBudget);

                                // ① 连接与响应头期限：与正文期限彼此独立
                                using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token);
                                headerCts.CancelAfter(headerBudget);

                                // 请求消息与响应消息都要释放：ResponseHeadersRead 之后
                                // 响应一直攥着底层连接与响应流
                                using var req = new HttpRequestMessage(HttpMethod.Get, url + Rand());
                                
                                // 设置请求头，模仿浏览器行为，可能提高国内节点成功率
                                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

                                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
                                
                                if (!resp.IsSuccessStatusCode)
                                {
                                    Log($"{url} - HTTP {resp.StatusCode}");
                                    attempts++;
                                    continue;
                                }

                                using var stream = await resp.Content.ReadAsStreamAsync();
                                var sw = Stopwatch.StartNew();
                                
                                // ② 正文读取：从响应头到达那一刻起算的独立无进展期限
                                await ReadBodyWithStallDeadlineAsync(
                                    stream,
                                    buf,
                                    stallBudget,
                                    () => sw.ElapsedMilliseconds < raceTimeMs,
                                    attemptCts.Token,
                                    n => bytes += n);

                                Log($"{url} - Success: {bytes / 1024.0 / 1024.0:F2} MB in {sw.ElapsedMilliseconds}ms");
                                return (url, bytes, true);
                            }
                            catch (Exception ex)
                            {
                                Log($"{url} - Attempt {attempts + 1} failed: {ex.Message}");
                                attempts++;
                                if (attempts >= maxAttempts) break;
                                
                                // 修复 CS0103 错误：cts 在 catch 块中超出了作用域
                                await Task.Delay(50); // 只进行 50ms 延迟，不使用 cts.Token
                            }
                        }

                        return (url, 0, false);
                    }
                    catch (Exception ex)
                    {
                        Log($"{url} - Critical error: {ex.Message}");
                        return (url, 0, false);
                    }
                }));
            }

            var results = await Task.WhenAll(tasks);
            var successfulResults = results.Where(r => r.success).ToArray();

            if (successfulResults.Length == 0)
            {
                Log("All download sources failed, using fallback");
                return urls[0]; // 返回第一个作为备选
            }

            Array.Sort(successfulResults, (a, b) => b.bytes.CompareTo(a.bytes));
            var best = successfulResults[0];
            Log($"Best { (isDownload ? "download" : "upload") } source: {best.url} - {best.bytes / 1024.0 / 1024.0:F2} MB");
            
            return best.url;
        }

        // ======================================================
        // 2. 优化的下载测速 (提高并发，增加竞速频率)
        // ======================================================
        public static Task<double> TestDownloadAsync(
            int durationSec = 15,
            int threads = 16, // 优化点1：增加线程数以跑满带宽
            Action<double>? progress = null)
            => TestDownloadAsyncCore(
                DownloadSources, durationSec, threads, progress,
                headersTimeout: null, bodyStallTimeout: null, totalTimeout: null);

        /// <summary>
        /// 下载测速的实现（源列表与各段期限可注入，生产路径走默认值）。
        /// </summary>
        internal static async Task<double> TestDownloadAsyncCore(
            string[] sources,
            int durationSec,
            int threads,
            Action<double>? progress,
            TimeSpan? headersTimeout,
            TimeSpan? bodyStallTimeout,
            TimeSpan? totalTimeout)
        {
            Log("Starting download test...");
            Interlocked.Exchange(ref currentBestDownloadUrl, sources[0]);

            // ① 连接与响应头期限：保持既有"单个请求 20 秒"预算
            TimeSpan headerBudget = headersTimeout ?? DownloadHeadersTimeout;
            // ② 正文读取期限：无进展期限
            TimeSpan stallBudget = bodyStallTimeout ?? DownloadBodyStallTimeout;

            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            List<Task> worker = new();
            var cts = new CancellationTokenSource();

            // 优化的管理线程 - 提高竞速频率
            var managerTask = Task.Run(async () =>
            {
                while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // 优化点2：缩短竞速评估间隔，更快切换到最佳节点
                        string best = await PickFastestAsync(
                            sources, raceTimeMs: 2000, isDownload: true,
                            headersTimeout: headersTimeout,
                            bodyStallTimeout: bodyStallTimeout,
                            totalTimeout: totalTimeout);
                        Interlocked.Exchange(ref currentBestDownloadUrl, best);
                        
                        // 每 1.5 秒重新评估一次
                        await Task.Delay(1500, cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Manager task error: {ex.Message}");
                    }
                }
            });

            // 工作线程
            for (int i = 0; i < threads; i++)
            {
                worker.Add(Task.Run(async () =>
                {
                    // ★★★ 优化：512KB缓冲区（平衡性能与内存）★★★
                    byte[] buf = new byte[512 * 1024]; 

                    while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            string dynamicUrl = currentBestDownloadUrl;
                            // Log($"Worker using: {dynamicUrl}"); // 避免日志过多

                            using var req = new HttpRequestMessage(HttpMethod.Get, dynamicUrl + Rand());
                            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

                            // ① 连接与响应头期限（保持既有 20 秒单请求预算）
                            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                            headerCts.CancelAfter(headerBudget);
                            
                            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
                            resp.EnsureSuccessStatusCode();

                            using var stream = await resp.Content.ReadAsStreamAsync();

                            // ② 正文读取期限：独立的无进展期限
                            //    ③ 总期限：整次测速的结束期限（既有 cts）
                            await ReadBodyWithStallDeadlineAsync(
                                stream,
                                buf,
                                stallBudget,
                                () => sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested,
                                cts.Token,
                                n =>
                                {
                                    long now = Interlocked.Add(ref totalBytes, n);
                                    double speedMbps = (now * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                                    progress?.Invoke(speedMbps);
                                });
                        }
                        catch (OperationCanceledException) when (cts.IsCancellationRequested)
                        {
                            // ③ 总期限到了 → 结束这个工作线程
                            break;
                        }
                        catch (Exception ex)
                        {
                            // 正文无进展期限到了也走这里：镜像已死，换一个再试，而不是把线程挂到测速结束
                            Log($"Worker error: {ex.Message}");
                            try { await Task.Delay(500, cts.Token); } // 错误后短暂延迟
                            catch (OperationCanceledException) { break; }
                        }
                    }
                }));
            }

            try
            {
                // 等待测试时间结束
                await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token);
            }
            catch (TaskCanceledException) { }

            // 取消所有任务
            cts.Cancel();
            
            // 优化点3：增加 Task.WhenAll 的等待时限，避免卡死在收尾阶段
            try
            {
                // WaitAsync 需要 .NET 6 或更高版本，如果版本低于此，请替换为 Task.WhenAll + CancellationTokenSource
                await Task.WhenAll(worker.Concat(new[] { managerTask }))
                          .WaitAsync(TimeSpan.FromSeconds(5)); // 最多等待 5 秒清理
            }
            catch (TimeoutException)
            {
                 Log("Download cleanup timed out, exiting gracefully.");
            }
            catch (Exception ex)
            {
                Log($"Final download await error: {ex.Message}");
            }

            double finalSpeed = Math.Round((totalBytes * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1), 1);
            Log($"Download test completed: {finalSpeed} Mbps, Total: {totalBytes / 1024.0 / 1024.0:F2} MB");

            // ★★★ 优化：强制清理内存 ★★★
            worker.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            return finalSpeed;
        }

        // ======================================================
        // 3. 优化的上传测速 (增加请求超时，解决卡顿)
        // ======================================================
        public static async Task<double> TestUploadAsync(
            int durationSec = 5,
            int threads = 8, // 优化点1：增加上传线程数
            Action<double>? progress = null)
        {
            Log("Starting upload test...");
            Interlocked.Exchange(ref currentBestUploadUrl, UploadSources[0]);

            // ★★★ 优化：上传负载 512KB ★★★
            byte[] payload = new byte[512 * 1024]; 
            _rng.NextBytes(payload);

            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            List<Task> worker = new();
            var cts = new CancellationTokenSource();

            // 管理线程 (保持竞速评估频率优化)
            var managerTask = Task.Run(async () =>
            {
                while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        string best = await PickFastestAsync(UploadSources, raceTimeMs: 2000, isDownload: false);
                        Interlocked.Exchange(ref currentBestUploadUrl, best);
                        // 每 1.5 秒重新评估一次
                        await Task.Delay(1500, cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Upload manager error: {ex.Message}");
                    }
                }
            });

            for (int i = 0; i < threads; i++)
            {
                worker.Add(Task.Run(async () =>
                {
                    var contentType = new MediaTypeHeaderValue("application/octet-stream");

                    while (sw.Elapsed.TotalSeconds < durationSec && !cts.Token.IsCancellationRequested)
                    {
                        // 优化点2：给每个 POST 请求一个严格的超时 (5秒)，防止卡顿
                        using var uploadTimeoutCts = new CancellationTokenSource(5000); 
                        // 联合 Token：外部取消信号 或 内部超时信号 都会触发
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, uploadTimeoutCts.Token);
                        string url = currentBestUploadUrl + Rand();
                        try
                        {
                            // ★ 请求体也必须释放：ByteArrayContent 持有底层缓冲与流
                            using var content = new ByteArrayContent(payload);
                            content.Headers.ContentType = contentType;
                            
                            // 使用联合 Token
                            using var resp = await http.PostAsync(url, content, linkedCts.Token);
                            resp.EnsureSuccessStatusCode();

                            long now = Interlocked.Add(ref totalBytes, payload.Length);
                            double speedMbps = (now * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                            progress?.Invoke(speedMbps);
                        }
                        catch (TaskCanceledException ex) when (uploadTimeoutCts.IsCancellationRequested)
                        {
                            // 如果是内部超时导致的取消，记录并继续
                            Log($"Upload request timed out for worker: {url}");
                            await Task.Delay(500, cts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            break; // 外部取消信号，退出循环
                        }
                        catch (Exception ex)
                        {
                            Log($"Upload worker error: {ex.Message}");
                            await Task.Delay(500, cts.Token);
                        }
                    }
                }));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(durationSec), cts.Token);
            }
            catch (TaskCanceledException) { }

            cts.Cancel();
            
            // 优化点3：增加 Task.WhenAll 的等待时限
            try
            {
                await Task.WhenAll(worker.Concat(new[] { managerTask }))
                          .WaitAsync(TimeSpan.FromSeconds(5)); // 最多等待 5 秒清理
            }
            catch (TimeoutException)
            {
                Log("Upload cleanup timed out, exiting gracefully.");
            }
            catch (Exception ex)
            {
                Log($"Upload final await error: {ex.Message}");
            }

            double finalSpeed = Math.Round((totalBytes * 8.0 / 1_000_000) / Math.Max(sw.Elapsed.TotalSeconds, 0.1), 1);
            Log($"Upload test completed: {finalSpeed} Mbps, Total: {totalBytes / 1024.0 / 1024.0:F2} MB");

            // ★★★ 优化：强制清理内存 ★★★
            worker.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            return finalSpeed;
        }
    }
}