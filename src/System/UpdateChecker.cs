using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;
using LiteMonitor.src.Core;

using System.IO;
using System.IO.Compression;

namespace LiteMonitor
{
    /// <summary>
    /// LiteMonitor++ 自动更新模块
    /// - version.json 支持国内 / GitHub 两源自动 fallback
    /// - ZIP 下载支持两源测速自动选择最快
    /// - ZIP 下载完成后，主程序抢先更新 Updater.exe，防止自更新死锁
    /// - CheckAsync() 可被右键菜单直接调用
    /// </summary>
    public static class UpdateChecker
    {
        // 全局 HttpClient（降低系统资源消耗）
        private static readonly HttpClient http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2) // 保持连接复用
            // 【安全】不设置自定义证书校验回调：TLS 校验按系统信任链进行。
            // 曾为「解决用户证书报错」整体关闭校验，这等于让任何中间人都能替换 version.json，
            // 进而把任意 update.zip 推给一个持有管理员令牌的进程（app.manifest 为
            // requireAdministrator）。证书失败必须可见并中止更新，不得静默降级，也不得提供跳过开关。
            // 企业 TLS 拦截/自签代理环境的正确做法是引导用户导入根证书，而不是关闭校验。
        })
        {
            Timeout = TimeSpan.FromSeconds(6) // 适当放宽超时时间，避免网络波动导致失败
        };

        // ========================================================
        // 【1】两个 version.json 源（自动 fallback）
        // ========================================================
        private static readonly string[] VersionJsonUrls =
        {
             // 自有源（主）
            "https://raw.githubusercontent.com/mojingjing-git/LiteMonitorPlus/master/resources/version.json",

            // Gitee 镜像（自动 fallback 使用）
            "https://gitee.com/mojingjing-git/LiteMonitorPlus/raw/master/resources/version.json",
            
        };

        // ========================================================
        // 【2】两个 ZIP 下载镜像（测速自动选择最快）
        // ========================================================
        private static readonly string[] Mirrors =
        {
            
            // GitHub Releases
            "https://github.com/mojingjing-git/LiteMonitorPlus/releases/download/v{0}/LiteMonitorPlus_v{0}-win-x64.zip",
            // Gitee 镜像
            "https://gitee.com/mojingjing-git/LiteMonitorPlus/releases/download/v{0}/LiteMonitorPlus_v{0}-win-x64.zip",

            
        };

        /// <summary>
        /// 更新源不可信或证书校验失败时的手工下载入口。
        ///
        /// 这不是绕过认证：证书由浏览器自行校验，且下载到的包需要用户手工解压安装，
        /// 不再由本进程以管理员令牌静默执行。代价是该包不经过签名核验（见 W1.2），
        /// 所以只在自动更新已经失败后、由用户显式选择时才打开。
        /// </summary>
        public const string ReleasePageUrl = "https://github.com/mojingjing-git/LiteMonitorPlus/releases";

        // ========================================================
        // 【2.5】更新元数据签名（W1.2）
        // ========================================================

        /// <summary>
        /// 更新元数据的签名公钥（ECDSA P-256，SubjectPublicKeyInfo / DER，单行 base64）。
        /// </summary>
        /// <remarks>
        /// ★ 这里只有**公钥**：公开它不构成任何额外能力，等于告诉别人"如何验证我们"。
        ///   私钥只在发布流程里出现（<c>scripts/sign-release.ps1 -KeyPath ...</c>），绝不进版本库
        ///   —— <c>.gitignore</c> 已排除 <c>secrets/</c>、<c>*.pkcs8</c>、<c>*.pfx</c>。
        /// ★ 换密钥会让所有用旧密钥签过的 <c>version.json</c> 立刻失效，
        ///   所以客户端与发布必须**同一次**更新，不能分开发布。
        /// ★ 公钥指纹（SPKI 的 SHA-256）：
        ///   <c>25E6659D954A92A9FCCF1E2622F30F68A7FA9C84390B419B33F3EB0F11F97572</c>
        /// </remarks>
        private const string UpdateSigningPublicKeySpkiBase64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAElF/yF1mc8EdJOeUhr/u5vxFJQXVm7oI1cEDuugjRmj5vWM9b14bZ5os1OZD9pDm0Gvj8GXevjHnl10G7kMeOuw==";

        /// <summary>
        /// 构造被签名的规范化载荷：<c>version\nsha256\nreleaseDate</c>（UTF-8，单个 <c>\n</c> 分隔）。
        /// </summary>
        /// <remarks>
        /// ★ 刻意**不签** <c>changelog</c>：它含中文与换行，规范化规则极易在发布脚本与客户端之间漂移；
        ///   漂移的后果是"签名永远验不过"，而不是"更安全"。
        /// ★ 也刻意**不签**整段 JSON：序列化顺序、缩进、行尾任何一处变化都会让签名失效。
        /// ★ 字段顺序就是协议。改动它必须与 <c>scripts/sign-release.ps1</c> 同步改。
        /// </remarks>
        public static string BuildUpdateSignaturePayload(string version, string sha256, string releaseDate)
            => version + "\n" + sha256 + "\n" + releaseDate;

        /// <summary>
        /// 校验 <c>version.json</c> 的签名。
        /// </summary>
        /// <returns>
        /// true = 签名有效。任何缺字段、格式错误、异常一律返回 false ——
        /// **失败即不信任**，不存在"验不了就放行"的分支。
        /// </returns>
        public static bool TryVerifyUpdateSignature(
            string version, string sha256, string releaseDate, string? signatureBase64)
            => VerifyUpdateSignatureWithKey(
                version, sha256, releaseDate, signatureBase64, UpdateSigningPublicKeySpkiBase64);

        /// <summary>
        /// 用指定公钥校验签名。
        /// </summary>
        /// <remarks>
        /// ★ <c>internal</c>：生产路径只有上面那个用内置公钥的入口。
        ///   这里允许注入公钥，是为了让测试能自造密钥对、自签自验，
        ///   而不是把一份"预先算好的签名样本"焊进测试（换密钥就会静默失效）。
        /// </remarks>
        internal static bool VerifyUpdateSignatureWithKey(
            string version, string sha256, string releaseDate, string? signatureBase64,
            string publicKeySpkiBase64)
        {
            if (string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(sha256) ||
                string.IsNullOrWhiteSpace(releaseDate) ||
                string.IsNullOrWhiteSpace(signatureBase64) ||
                string.IsNullOrWhiteSpace(publicKeySpkiBase64))
            {
                return false;
            }

            // 摘要形态先自检：不是 64 位十六进制就不必再费一次公钥验签
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return false;

            try
            {
                byte[] signature = Convert.FromBase64String(signatureBase64!);
                byte[] payload = Encoding.UTF8.GetBytes(
                    BuildUpdateSignaturePayload(version, sha256, releaseDate));

                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(
                    Convert.FromBase64String(publicKeySpkiBase64), out _);

                return ecdsa.VerifyData(payload, signature,
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 计算文件的 SHA-256，返回大写十六进制（与 <c>scripts/sign-release.ps1</c> 的输出格式一致）。
        /// </summary>
        public static string ComputeFileSha256(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        /// <summary>
        /// 判断异常链中是否存在 TLS 证书校验失败。
        ///
        /// 恢复证书校验后，企业 TLS 拦截、自签代理、证书过期与系统时间错误都会落到这里，
        /// 需要与普通的网络中断区分开：前者要引导用户走手工下载，后者提示重试即可。
        /// </summary>
        public static bool IsCertificateFailure(Exception? ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Security.Authentication.AuthenticationException) return true;
            }
            return false;
        }

        /// <summary>
        /// 缓存最新版本信息，供菜单等处使用
        /// </summary>
        public static (string latest, string changelog, string releaseDate)? LatestVersionInfo { get; private set; }

        /// <summary>
        /// 是否发现了新版本
        /// </summary>
        public static bool IsUpdateFound => LatestVersionInfo != null;

        /// <summary>
        /// 最近一次签名校验通过的 release ZIP 摘要（大写十六进制）。
        /// </summary>
        /// <remarks>
        /// 只有验签通过才会被赋值；下载完成后必须拿它比对实际文件，不一致就丢弃。
        /// </remarks>
        public static string? LatestZipSha256 { get; private set; }

        /// <summary>
        /// 最近一次检查更新的失败类型。
        /// </summary>
        /// <remarks>
        /// 用来把"网络不通，稍后重试"与"拿到了数据但来源不可信"分开提示 ——
        /// 对后者说"请稍后重试"是在误导用户。
        /// </remarks>
        public static UpdateCheckFailure LastFailure { get; private set; } = UpdateCheckFailure.None;

        /// <summary>检查更新的失败类型。</summary>
        public enum UpdateCheckFailure
        {
            /// <summary>没有失败（或还没检查过）。</summary>
            None,
            /// <summary>网络、服务器状态或 JSON 解析错误。</summary>
            Network,
            /// <summary>拿到了 version.json，但签名 / 摘要不合法 —— 来源不可信。</summary>
            Signature,
        }


        // ========================================================
        // 【3】主入口：检查更新
        // ========================================================
        /// <summary>
        /// 检查更新主入口。
        /// showMessage = true 时，在无更新或失败时提示用户。
        /// </summary>
        public static async Task CheckAsync(bool showMessage = false)
        {
            try
            {
                // ---- 获取版本信息（自动 fallback + 验签）----
                var info = await GetVersionInfo();
                if (info == null)
                {
                    // ★ 清掉上一轮的结果：否则菜单里的"有新版本"会指着一条已经不成立的记录
                    LatestVersionInfo = null;
                    LatestZipSha256 = null;

                    if (showMessage)
                    {
                        bool isZh = LanguageManager.CurrentLang == "zh";

                        // ★ 签名不通过与网络不通必须分开说：
                        //   对"来源不可信"回一句"请稍后重试"是在误导用户重复一次注定失败的尝试。
                        if (LastFailure == UpdateCheckFailure.Signature)
                        {
                            MessageBox.Show(
                                isZh
                                    ? "更新信息未通过签名校验，已忽略。\n这通常意味着下载源被篡改，或发布方忘记签名。\n请稍后重试；若持续出现，请从发布页手工下载。"
                                    : "Update metadata failed signature verification and was ignored.\nThis usually means the source was tampered with, or the release was not signed.\nPlease retry later; if this persists, download manually from the release page.",
                                isZh ? "更新源不可信" : "Untrusted Update Source",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else if (isZh)
                        {
                            MessageBox.Show("无法连接到更新服务器，请稍后重试。",
                                "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            MessageBox.Show("Unable to connect to update server, please try again later.",
                                "Update Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    return;
                }

                string latest = info.Value.latest;
                string changelog = info.Value.changelog;
                string releaseDate = info.Value.releaseDate;
                string current = GetCurrentVersion();

                if (new Version(latest) > new Version(current))
                {
                    // 记录发现新版本（LatestVersionInfo 的公开签名保持三元组不变）
                    LatestVersionInfo = (info.Value.latest, info.Value.changelog, info.Value.releaseDate);
                    // ★ 期望摘要来自已验签的元数据：下载完成后必须拿它比对实际文件
                    LatestZipSha256 = info.Value.sha256;

                    // ★★★ [新增逻辑] 预检查设置：是否开启自动更新 ★★★
                    // 如果不是手动触发 (showMessage=false) 且未开启自动检查，则只记录不弹窗
                    var settings = Settings.Load();
                    if (!showMessage && !settings.AutoCheckUpdate) return;

                    // ---- 获取排序后的下载源 (最快优先) ----
                    var sortedUrls = await GetSortedZipUrls(latest);

                    // ---- 加载设置并弹出更新窗口 ----
                    bool isZh = settings?.Language?.ToLower() == "zh";

                    var context = new DownloadContext
                    {
                        Title = isZh ? "发现新版本！" : "New Version!",
                        VersionLabel = $"LiteMonitor++_v{latest}",
                        Description = $"更新日志：\n{changelog} \n更新日期：\n{releaseDate}\n\nGitHub：https://github.com/mojingjing-git/LiteMonitorPlus",
                        Urls = sortedUrls.ToArray(),
                        SavePath = Path.Combine(AppContext.BaseDirectory, "resources", "update.zip"),
                        ActionButtonText = "Update",
                        AutoExitOnSuccess = true,
                        ExpectedSha256 = info.Value.sha256
                    };

                    new UpdateDialog(context, settings).ShowDialog();
                }
                else
                {
                    // 已是最新版本，清除缓存
                    LatestVersionInfo = null;
                    LatestZipSha256 = null;

                    if (showMessage)
                    {
                        // 根据当前语言设置显示中文或英文版本
                        if (LanguageManager.CurrentLang == "zh")
                        {
                            MessageBox.Show($"当前已是最新版本 ：v{current}\n发布日期：{releaseDate}", "检查更新", 
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show($"Already the latest version: v{current}\nRelease date: {releaseDate}", "Update Check", 
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UpdateChecker] Error: " + ex.Message);
                // 异常路径同样要清掉摘要：不能让上一次验签通过的摘要在本轮失败后仍然生效
                LatestVersionInfo = null;
                LatestZipSha256 = null;
                if (showMessage)
                {
                    // 根据当前语言设置显示中文或英文版本
                    if (LanguageManager.CurrentLang == "zh")
                    {
                        MessageBox.Show("检查更新失败，可能是网络问题。", 
                            "检查更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    else
                    {
                        MessageBox.Show("Update check failed, possibly due to network issues.", 
                            "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }


        // ========================================================
        // 【4】version.json 自动 fallback
        // ========================================================
       private static async Task<(string latest, string changelog, string releaseDate, string sha256)?> GetVersionInfo()
        {
            foreach (var url in VersionJsonUrls)
            {
                try
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(3000); // 最大等待 3 秒（连接+读取全部）

                    // 构造真正带连接超时的 HttpRequest
                    var request = new HttpRequestMessage(HttpMethod.Get, url);

                    var task = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    // 这里我们用 WhenAny 避免 HttpClient 自身的内部卡顿
                    var finished = await Task.WhenAny(task, Task.Delay(3000, cts.Token));

                    if (finished != task)
                        throw new TimeoutException("Connection timeout");

                    var resp = await task;

                    if (!resp.IsSuccessStatusCode)
                        throw new Exception("Bad status");

                    string json = await resp.Content.ReadAsStringAsync(cts.Token);

                    var doc = JsonDocument.Parse(json);

                    string latest = doc.RootElement.GetProperty("version").GetString()!;
                    string log = doc.RootElement.GetProperty("changelog").GetString()!;
                    string releaseDate = doc.RootElement.GetProperty("releaseDate").GetString()!;
                    //string downloadUrl = doc.RootElement.GetProperty("downloadUrl").GetString()!;

                    // ---- 来源认证：验签通过才承认这份元数据 ----
                    // ★ 缺字段按空串处理，交给 TryVerifyUpdateSignature 判否；
                    //   这样"旧客户端 + 新 version.json"和"新客户端 + 未签名 version.json"
                    //   都走同一条拒绝路径，不会出现"读不到就当作没校验"的漏洞。
                    string sha256 = doc.RootElement.TryGetProperty("sha256", out var shaEl)
                        ? (shaEl.GetString() ?? string.Empty) : string.Empty;
                    string signature = doc.RootElement.TryGetProperty("signature", out var sigEl)
                        ? (sigEl.GetString() ?? string.Empty) : string.Empty;

                    if (!TryVerifyUpdateSignature(latest, sha256, releaseDate, signature))
                    {
                        // ★ 不降级、不"验不过也先装上"：验不过就等于这个源不可信，换下一个源。
                        //   全部源都验不过时 GetVersionInfo 返回 null，主流程因此不会提示任何更新。
                        Debug.WriteLine($"[Update] 签名校验失败，拒绝该源：{url}");
                        LastFailure = UpdateCheckFailure.Signature;
                        continue;
                    }

                    // ---- 成功，立即返回 ----
                    LastFailure = UpdateCheckFailure.None;
                    return (latest, log, releaseDate, sha256);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Update] 源失败：{url} -> {ex.Message}");
                    LastFailure = UpdateCheckFailure.Network;
                    continue; // 换下一个源
                }
            }

            return null; // 两个源都失败
        }



        // ========================================================
        // 【5】测速获取排序后的 ZIP 下载源
        // ========================================================
        private static async Task<List<string>> GetSortedZipUrls(string version)
        {
            var tests = new Task<(string url, long speed)>[Mirrors.Length];

            for (int i = 0; i < Mirrors.Length; i++)
            {
                string url = string.Format(Mirrors[i], version);
                tests[i] = TestMirrorSpeed(url);
            }

            var results = await Task.WhenAll(tests);

            // 速度降序排序 (Speed > 0 的优先)
            var sorted = results
                .OrderByDescending(r => r.speed)
                .Select(r => r.url)
                .ToList();

            // 如果所有源都失败(speed=0)，至少保留默认顺序的 URL 以供重试
            if (sorted.Count == 0 || results.All(r => r.speed == 0))
            {
                 return Mirrors.Select(m => string.Format(m, version)).ToList();
            }

            return sorted;
        }


        // ========================================================
        // 【6】轻量测速（读取 32KB 换算下载速度）
        // ========================================================
        private static async Task<(string url, long speed)> TestMirrorSpeed(string url)
        {
            try
            {
                var sw = Stopwatch.StartNew();

                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                    return (url, 0);

                using var stream = await resp.Content.ReadAsStreamAsync();

                byte[] testBuf = new byte[32 * 1024];
                int read = await stream.ReadAsync(testBuf, 0, testBuf.Length);

                sw.Stop();

                if (read <= 0)
                    return (url, 0);

                // Bytes per second
                long speed = (long)(read * 1000.0 / Math.Max(sw.ElapsedMilliseconds, 1));

                return (url, speed);
            }
            catch
            {
                return (url, 0);
            }
        }


        // ========================================================
        // 【7】获取当前版本号
        // ========================================================
                // ========================================================
        // 【7】获取当前版本号 (修复版)
        // ========================================================
        public static string GetCurrentVersion()
        {
            // 优先读取 AssemblyInformationalVersion (对应 csproj 中的 <Version>)
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            // 如果读取失败，回退到 ProductVersion
            if (string.IsNullOrWhiteSpace(version))
                version = Application.ProductVersion;

            // 这里的 version 可能会包含后缀 (如 1.0.7+abcdef)，需要截断
            int plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
                version = version.Substring(0, plusIndex);

            return version;
        }


        // ========================================================
        // 【8】主程序抢先更新 Updater.exe (解决自更新死锁)
        // ========================================================
        /// <summary>
        /// 在启动 Updater 之前，强制从 ZIP 中解压出最新的 Updater.exe 覆盖旧版。
        /// 这样 Updater 运行时就是最新的，无需再尝试更新自己。
        /// </summary>
        /// <param name="zipPath">下载好的 update.zip 路径</param>
        /// <returns>返回成功解压的 Updater 路径，失败则返回 null</returns>
        public static string? PreUpdateUpdater(string zipPath)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string resourcesDir = Path.Combine(baseDir, "resources");

                // 1. 确保 resources 目录存在
                if (!Directory.Exists(resourcesDir)) Directory.CreateDirectory(resourcesDir);

                // 2. 杀掉所有残留的 Updater 进程 (防止占用)
                // 涵盖新旧两个名字
                string[] updaterNames = { "Updater", "LiteMonitor++.Updater" };
                foreach (var name in updaterNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try 
                        {
                            // 优化：检查进程路径是否匹配
                             if (p.MainModule != null && 
                                 p.MainModule.FileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Kill(); 
                            }
                        } 
                        catch { }
                    }
                }
                
                // 给一点时间释放句柄
                System.Threading.Thread.Sleep(200);

                // 3. 打开 ZIP 查找 Updater
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    // 优先找新版
                    var entry = archive.Entries.FirstOrDefault(e => 
                        e.FullName.EndsWith("LiteMonitor++.Updater.exe", StringComparison.OrdinalIgnoreCase));
                    
                    // 没找到则找旧版
                    if (entry == null)
                    {
                        entry = archive.Entries.FirstOrDefault(e => 
                            e.FullName.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase));
                    }

                    if (entry != null)
                    {
                        // 确定解压目标路径 (保持文件名一致)
                        string fileName = Path.GetFileName(entry.FullName);
                        string targetPath = Path.Combine(resourcesDir, fileName);

                        // 【自举保护】先解到临时文件 → 校验长度 → 自检可启动 → 备份旧文件 → 才覆盖。
                        //
                        // 旧实现直接 ExtractToFile(targetPath, true)，没有任何备份和自检。
                        // 若 ZIP 里的新更新器本身损坏（截断 / 架构不符 / 缺依赖），主程序会把坏的 exe
                        // 写到磁盘并随即启动它；而负责回滚的代码正好在这个坏的 exe 里，
                        // 等于「更新器自身损坏」这一失败模式完全没有恢复路径。
                        // 把「更新器可用」变成更新前的前置断言，是这里唯一能做的收口。
                        string stagingPath = targetPath + ".new";
                        string backupPath = targetPath + ".bak";

                        try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }

                        entry.ExtractToFile(stagingPath, true);

                        // 长度必须与 ZIP 条目一致，用于捕获截断
                        long expectedLen = entry.Length;
                        long actualLen = new FileInfo(stagingPath).Length;
                        if (expectedLen != actualLen)
                            throw new IOException($"更新器大小不一致：期望 {expectedLen} / 实际 {actualLen}");

                        if (!SelfCheckUpdaterBinary(stagingPath))
                            throw new IOException("更新器自检未通过（无法启动或退出码非 0）");

                        // 自检通过才动既有文件：先留 .bak，再覆盖
                        try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                        if (File.Exists(targetPath)) File.Copy(targetPath, backupPath, true);

                        File.Copy(stagingPath, targetPath, true);
                        try { File.Delete(stagingPath); } catch { }

                        Debug.WriteLine($"[UpdateChecker] Updater 预更新成功: {targetPath}");
                        
                        return targetPath;
                    }
                }
            }
            catch (Exception ex)
            {
                // 预更新失败不阻断流程：调用方 StartUpdater 会回退到现有更新器
                Debug.WriteLine($"[UpdateChecker] Updater 预更新失败: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// 启动候选更新器并执行 --selfcheck，确认它在本机可加载、可运行。
        ///
        /// 更新器是 WinExe（OutputType=WinExe，没有控制台），所以判定只看退出码；
        /// 有重定向时顺便收集输出用于排障，输出丢失不影响结论。
        /// </summary>
        private static bool SelfCheckUpdaterBinary(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--selfcheck",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
                };

                using var p = Process.Start(psi);
                if (p == null) return false;

                if (!p.WaitForExit(3000))
                {
                    // 起不来或卡住都视为不可用，不要留下僵尸进程
                    try { p.Kill(true); } catch { }
                    Debug.WriteLine("[UpdateChecker] 更新器自检超时");
                    return false;
                }

                string diag = "";
                try { diag = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim(); } catch { }

                Debug.WriteLine($"[UpdateChecker] 更新器自检退出码={p.ExitCode} 输出={diag}");
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateChecker] 更新器自检异常: {ex.Message}");
                return false;
            }
        }
    }
}
