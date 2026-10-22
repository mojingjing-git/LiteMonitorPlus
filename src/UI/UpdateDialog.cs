using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers; // 引入头文件处理
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security.Principal;
using LiteMonitor.src.Core; // 修复: 引用 Settings 类

namespace LiteMonitor
{
    /// <summary>
    /// 更新包与已验签的摘要不一致（或根本没有摘要）。
    /// </summary>
    /// <remarks>
    /// 单独一个类型是为了让调用方能把"校验失败"与"网络失败"分开提示 ——
    /// 这两种失败对用户意味着完全不同的下一步动作。
    /// </remarks>
    public sealed class UpdateIntegrityException : Exception
    {
        public UpdateIntegrityException(string message) : base(message) { }
    }

    /// <summary>
    /// 更新包字节绑定（LM-016）。
    /// </summary>
    /// <remarks>
    /// ★ 要解决的问题：主程序校验完安装包摘要后，只把**路径**交给以高权限运行的更新器，
    ///   更新器再按路径把文件读一遍 —— 两者之间没有"字节必须保持不变"的约束。
    /// ★ 这里的做法：摘要从**钉住的句柄**上算，且这个句柄一直保持到更新器进程启动。
    ///   <see cref="FileShare.Read"/> 的含义是：别的进程仍然可以**读**这个文件，
    ///   但不能**写入、替换或删除**它（这些操作需要写/删除访问权）。
    ///   于是"算摘要的字节"、"预解压新版更新器读的字节"、"交给更新器的字节"
    ///   必然是同一批字节。
    /// ★ 摘要口径与 <c>UpdateChecker.ComputeFileSha256</c> 一致：SHA-256 + 十六进制。
    ///   比对大小写不敏感；对外输出（交给更新器的参数、日志）统一用小写。
    /// </remarks>
    public static class UpdatePackageBinder
    {
        /// <summary>
        /// 打开并"钉住"更新包：别的进程仍可读，但不能写入、替换或删除它。
        /// </summary>
        public static FileStream OpenPinned(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        /// <summary>
        /// 从流上计算 SHA-256（大写十六进制，与 UpdateChecker.ComputeFileSha256 同口径）。
        /// </summary>
        public static string ComputeSha256Hex(Stream stream)
        {
            stream.Position = 0;
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            stream.Position = 0;
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// 期望摘要形态是否合法（64 位十六进制）。空值/畸形值一律不合法。
        /// </summary>
        public static bool IsWellFormedDigest(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim();
            if (trimmed.Length != 64) return false;

            foreach (char c in trimmed)
            {
                if (!Uri.IsHexDigit(c)) return false;
            }

            return true;
        }

        /// <summary>
        /// 摘要是否一致（大小写不敏感）。
        /// </summary>
        public static bool Matches(string actual, string expected) =>
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 构造交给更新器的命令行参数：更新包路径 + 期望摘要。
        /// </summary>
        /// <remarks>
        /// ★ 只传路径的旧交接方式已经被移除 —— 那正是"校验与使用之间没有字节约束"的缺口。
        ///   更新器拿到摘要后会用**它自己打开的**文件流重算并比对；不匹配就拒绝解压。
        /// ★ 摘要形态不合法时直接抛错（失败即不启动），不允许"没有摘要也照样传路径"。
        /// </remarks>
        public static string BuildUpdaterArguments(string packagePath, string verifiedSha256)
        {
            if (!IsWellFormedDigest(verifiedSha256))
            {
                throw new ArgumentException(
                    "交给更新器的期望摘要必须是非空的 64 位十六进制。",
                    nameof(verifiedSha256));
            }

            return "\"" + packagePath + "\" \"" + verifiedSha256.Trim().ToLowerInvariant() + "\"";
        }
    }

    public class DownloadContext
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] Urls { get; set; } = Array.Empty<string>();
        public string SavePath { get; set; } = "";

        /// <summary>
        /// 本地优先文件路径（可空）。非空且文件存在时，下载对话框会直接把该文件
        /// 复制到 SavePath 而不走网络，用于发布包自带驱动包等场景的离线安装。
        /// 复制后仍由调用方执行 SHA256 校验，信任边界不变。
        /// </summary>
        public string? LocalPath { get; set; }

        /// <summary>
        /// 期望的产物 SHA-256（大写十六进制，来自已验签的 version.json）。
        /// </summary>
        /// <remarks>
        /// ★ 非空时，下载（或本地复制）完成后**必须**比对通过才允许交给更新器；
        ///   不匹配则删除产物并中止，保留现有版本。
        /// ★ 空表示"调用方没有提供摘要"。更新流程（AutoExitOnSuccess = true）里
        ///   这等同于拒绝——见 UpdateDialog 中的校验分支，不存在"没摘要就放行"的路径。
        /// </remarks>
        public string? ExpectedSha256 { get; set; }
        
        // UI 配置
        public string VersionLabel { get; set; } = "";
        public string ActionButtonText { get; set; } = "Install";
        public bool AutoExitOnSuccess { get; set; } = false; // 更新模式下为 true
    }

    public partial class UpdateDialog : Form
    {
        private readonly List<string> _sortedUrls;
        private readonly DownloadContext _context;
        private readonly Settings _settings;

        // ★ LM-016：钉住更新包的文件句柄。
        //   FileShare.Read ⇒ 别的进程仍可读，但**不能**写入/替换/删除这个文件。
        //   从摘要校验一直保持到更新器进程启动，保证"校验过的字节"就是"交出去的字节"。
        private FileStream? _pinnedPackage;

        // ★ LM-016：已验证的摘要（小写十六进制），随更新器参数一起交接。
        private string _verifiedSha256 = "";

        private CancellationTokenSource? _cts;
        private Stopwatch _speedWatch;
        private bool IsChinese => _settings?.Language?.ToLower() == "zh";

        public UpdateDialog(DownloadContext context, Settings settings)
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = true;
            
            _context = context;
            _settings = settings;
            _sortedUrls = new List<string>(_context.Urls);

            // 初始化 UI
            this.Text = IsChinese ? $"LiteMonitor++ - {_context.Title}" : $"LiteMonitor++ - {_context.Title}";
            lblVersion.Text = _context.VersionLabel;
            
            // 富文本设置 (必须在设置文本之前/之间正确处理)
            rtbChangelog.Text = _context.Description;
            rtbChangelog.SelectAll();
            rtbChangelog.SelectionIndent = 6; // 减小左缩进 (原为10)
            rtbChangelog.SelectionRightIndent = 5; // 减小右缩进 (原为10)
            rtbChangelog.DeselectAll();
            rtbChangelog.SelectionStart = 0;
            
            rtbChangelog.DetectUrls = true;
            rtbChangelog.LinkClicked += RtbChangelog_LinkClicked;
            // 强制显示垂直滚动条
            rtbChangelog.ScrollBars = RichTextBoxScrollBars.Vertical; 
            
            // 移除动态居中逻辑，恢复 Designer 中预设的"靠右"布局
            // rtbChangelog.Width = ... 
            // rtbChangelog.Left = ...
            
            // 设置按钮文本
            btnUpdate.Text = IsChinese ? 
                (_context.ActionButtonText == "Update" ? "立即更新" : "立即安装") : 
                _context.ActionButtonText;
                
            label1.Text = _context.Title; // 修复: 直接使用 Context 中的标题，避免英文环境下被错误覆盖
            btnCancel.Text = IsChinese ? "取消" : "Cancel";
            lblStatus.Text = IsChinese ? "准备就绪" : "Ready";

            // 防止自动聚焦文本框 (将焦点设为按钮)
            this.ActiveControl = btnUpdate;

            // 启动测速
            if (_context.Urls.Length > 1) Task.Run(TestMirrors);
        }

        private void RtbChangelog_LinkClicked(object? sender, LinkClickedEventArgs e)
        {
            try
            {
                // 使用默认浏览器打开链接
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.LinkText,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                string title = IsChinese ? "错误" : "Error";
                string msg = IsChinese ? $"无法打开链接：{e.LinkText}\n错误：{ex.Message}" : $"Cannot open link: {e.LinkText}\nError: {ex.Message}";
                
                MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            // 如果正在下载，则触发取消
            if (_cts != null)
            {
                _cts.Cancel();
            }
            else
            {
                this.Close();
            }
        }

        //防止窗口在下载时被强制关闭
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null && e.CloseReason == CloseReason.UserClosing)
            {
                string msg = IsChinese ? "正在下载更新，确定要取消吗？" : "Download in progress. Are you sure you want to cancel?";
                string title = IsChinese ? "提示" : "Confirmation";
                
                if (MessageBox.Show(msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                _cts.Cancel();
            }
            base.OnFormClosing(e);
        }

        private async Task TestMirrors()
        {
            if (_context.Urls.Length <= 1) return;

            try
            {
                var tasks = _context.Urls.Select(async url =>
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                        var sw = Stopwatch.StartNew();
                        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        sw.Stop();
                        return response.IsSuccessStatusCode ? (url, sw.ElapsedMilliseconds) : (url, long.MaxValue);
                    }
                    catch
                    {
                        return (url, long.MaxValue);
                    }
                });

                var results = await Task.WhenAll(tasks);
                
                var sorted = results
                    .Where(x => x.Item2 != long.MaxValue)
                    .OrderBy(x => x.Item2)
                    .Select(x => x.url)
                    .ToList();

                if (sorted.Count > 0)
                {
                    lock (_sortedUrls)
                    {
                        _sortedUrls.Clear();
                        _sortedUrls.AddRange(sorted);
                        foreach (var url in _context.Urls)
                        {
                            if (!_sortedUrls.Contains(url)) _sortedUrls.Add(url);
                        }
                    }
                    Debug.WriteLine($"[更新弹窗] 镜像源排序完成: {string.Join(", ", _sortedUrls)}");
                }
            }
            catch { }
        }

        private async void btnUpdate_Click(object sender, EventArgs e)
        {
            btnUpdate.Enabled = false;
            btnCancel.Enabled = true; // 允许取消
            
            _cts = new CancellationTokenSource();
            _speedWatch = Stopwatch.StartNew();
            
            // 进度条模式切换
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = 0;

            bool downloadSuccess = false;
            Exception? lastError = null;

            // 获取当前的 URL 列表
            List<string> downloadList;
            lock (_sortedUrls)
            {
                downloadList = new List<string>(_sortedUrls);
            }

            try
            {
                // 0. 本地优先：发布包自带驱动包时直接使用本地文件（离线可用）。
                //    复制完成后仍会走与网络下载完全相同的 SHA256 校验，信任边界不变。
                if (!string.IsNullOrEmpty(_context.LocalPath) && File.Exists(_context.LocalPath))
                {
                    lblStatus.Text = _settings?.Language?.ToLower() == "zh"
                        ? "正在使用本地安装包..."
                        : "Using local package...";
                    File.Copy(_context.LocalPath, _context.SavePath, true);
                    downloadSuccess = true;
                }

                // 1. 尝试所有镜像源
                for (int mirrorIndex = 0; mirrorIndex < downloadList.Count && !downloadSuccess; mirrorIndex++)
                {
                    var url = downloadList[mirrorIndex];
                    if (_cts.IsCancellationRequested) break;

                    try
                    {
                        lblStatus.Text = _settings?.Language?.ToLower() == "zh" 
                            ? $"正在连接下载服务器..." 
                            : $"Connecting to server...";

                        // 4. 配置 HttpClient
                        // 【安全】不设置自定义证书校验回调：TLS 校验按系统信任链进行。
                        // 这里下载的是随后会被执行的 update.zip，关闭校验等于允许中间人替换更新包；
                        // 证书失败必须可见并中止更新，不得静默降级，也不得提供跳过开关。
                        using var http = new HttpClient()
                        { 
                            Timeout = TimeSpan.FromMinutes(10) 
                        };
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteMonitor++-Updater/1.0");

                        // 5. 发起请求
                        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                        
                        if (!resp.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"HTTP Error: {resp.StatusCode}");
                        }

                        long totalBytes = resp.Content.Headers.ContentLength ?? -1;
                        
                        // 6. 流式下载
                        using (var fs = new FileStream(_context.SavePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        using (var stream = await resp.Content.ReadAsStreamAsync(_cts.Token))
                        {
                            byte[] buffer = new byte[81920]; 
                            long totalRead = 0;
                            int bytesRead;
                            
                            long lastSpeedCheckBytes = 0;
                            long lastSpeedCheckTime = 0;

                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                                totalRead += bytesRead;

                                // 更新进度条
                                if (totalBytes > 0)
                                {
                                    int progressPct = (int)((double)totalRead / totalBytes * 100);
                                    if (progress.Value != progressPct) progress.Value = progressPct;
                                }
                                else
                                {
                                    if (progress.Style != ProgressBarStyle.Marquee) 
                                        progress.Style = ProgressBarStyle.Marquee;
                                }

                                // 更新状态文本
                                long now = _speedWatch.ElapsedMilliseconds;
                                if (now - lastSpeedCheckTime > 200)
                                {
                                    double speed = (totalRead - lastSpeedCheckBytes) / 1024.0 / 1024.0 / ((now - lastSpeedCheckTime) / 1000.0);
                                    string speedStr = speed.ToString("F1");
                                    string downloadedStr = (totalRead / 1024.0 / 1024.0).ToString("F1");
                                    string totalStr = totalBytes > 0 ? (totalBytes / 1024.0 / 1024.0).ToString("F1") : "??";

                                    lblStatus.Text = $"{downloadedStr}MB / {totalStr}MB  -  {speedStr} MB/s";
                                    lastSpeedCheckBytes = totalRead;
                                    lastSpeedCheckTime = now;
                                }
                            }
                        }
                        
                        // 下载成功
                        downloadSuccess = true;
                        break; 
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (ex is OperationCanceledException) throw;
                        lblStatus.Text = _settings?.Language?.ToLower() == "zh" ? "切换下载源..." : "Switching mirror...";
                        continue; 
                    }
                }

                if (!downloadSuccess)
                {
                     throw lastError ?? new Exception("Download failed from all mirrors.");
                }

                // 7. 下载完成
                // ★★★ Fix: Explicitly clear CTS so OnFormClosing doesn't prompt user ★★★
                var ctsToDispose = _cts;
                _cts = null; 
                ctsToDispose?.Dispose();

                // ---- 7.5 产物摘要校验 + 字节绑定（W1.2 / LM-016）----
                // ★ 为什么必须在这里、在 StartUpdater 之前：
                //   更新器会以管理员令牌替换程序文件，等它跑起来再发现内容不对就已经太晚
                //   （旧文件可能已被覆盖）。这里是唯一还能"什么都不做"的时间点。
                // ★ 更新模式下摘要为空 = 直接失败。"没提供摘要"不能被解释成"不需要校验"，
                //   否则只需把 version.json 的 sha256 删掉就能绕过整条认证链路。
                // ★ LM-016：摘要必须从**钉住的句柄**上算，且句柄一直保持到更新器启动。
                //   以前这里只算一次摘要就把**路径**交出去，更新器再按路径自己读一遍 ——
                //   校验与使用之间没有任何"字节必须保持不变"的约束。
                if (_context.AutoExitOnSuccess)
                {
                    string? expected = _context.ExpectedSha256;
                    if (!UpdatePackageBinder.IsWellFormedDigest(expected))
                    {
                        throw new UpdateIntegrityException(
                            IsChinese
                                ? "更新元数据里没有可用的校验摘要，无法确认这个安装包的来源。"
                                : "The update metadata carries no usable checksum, so this package's origin cannot be confirmed.");
                    }

                    FileStream pinned;
                    try
                    {
                        pinned = UpdatePackageBinder.OpenPinned(_context.SavePath);
                    }
                    catch (Exception ex)
                    {
                        throw new UpdateIntegrityException(
                            IsChinese
                                ? $"无法读取已下载的安装包：{ex.Message}"
                                : $"Could not read the downloaded package: {ex.Message}");
                    }

                    string actual;
                    try
                    {
                        actual = UpdatePackageBinder.ComputeSha256Hex(pinned);
                    }
                    catch (Exception ex)
                    {
                        pinned.Dispose();
                        throw new UpdateIntegrityException(
                            IsChinese
                                ? $"无法读取已下载的安装包：{ex.Message}"
                                : $"Could not read the downloaded package: {ex.Message}");
                    }

                    if (!UpdatePackageBinder.Matches(actual, expected!))
                    {
                        pinned.Dispose();
                        throw new UpdateIntegrityException(
                            IsChinese
                                ? $"期望 SHA-256：{expected.Trim().ToLowerInvariant()}\n实际 SHA-256：{actual.ToLowerInvariant()}"
                                : $"Expected SHA-256: {expected.Trim().ToLowerInvariant()}\nActual SHA-256: {actual.ToLowerInvariant()}");
                    }

                    // ★ 句柄保持打开：直到更新器进程启动，这个文件的内容不可被替换。
                    _pinnedPackage = pinned;
                    _verifiedSha256 = actual.ToLowerInvariant();

                    Debug.WriteLine($"[Update] 安装包摘要校验通过：{_verifiedSha256}");
                }

                if (_context.AutoExitOnSuccess)
                {
                    StartUpdater();
                }
                else
                {
                    // 组件下载模式：直接返回成功，由调用方负责后续安装逻辑
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消，清理残留文件
                ReleasePinnedPackage();
                CleanupPartialFile();
                bool isZh = _settings?.Language?.ToLower() == "zh";
                lblStatus.Text = isZh ? "已取消下载" : "Download Canceled";
                btnUpdate.Enabled = true;
                btnUpdate.Text = IsChinese ? 
                    (_context.ActionButtonText == "Update" ? "立即更新" : "立即安装") : 
                    _context.ActionButtonText;
                btnCancel.Enabled = true; // 确保取消按钮可用
                btnCancel.Text = isZh ? "关闭" : "Close";
                progress.Value = 0;
                progress.Style = ProgressBarStyle.Blocks;
            }
            catch (Exception ex)
            {
                // ★ LM-016：先释放钉住句柄，否则下面删不掉这个坏包
                //   （钉住的句柄对其他进程 denies 删除访问）。
                ReleasePinnedPackage();
                CleanupPartialFile();
                bool isZh = _settings?.Language?.ToLower() == "zh";

                if (UpdateChecker.IsCertificateFailure(ex))
                {
                    // 证书校验失败：明确告知已中止，并给出浏览器下载入口（对应 P-1 决策）。
                    // 不提供「跳过校验继续」之类的选项——那会把本次修复直接作废。
                    lblStatus.Text = isZh ? "无法验证更新源，已停止" : "Update source not verified, stopped";

                    string m = isZh
                        ? "无法验证更新源的身份，已停止自动更新。\n\n" +
                          "常见原因：公司网络做了流量审计、使用了自签代理、服务器证书过期，或系统时间不正确。\n\n" +
                          "可以在浏览器中手动下载安装包继续。注意：手动下载的包不经过本程序的签名核验。\n\n" +
                          "是否现在打开下载页面？"
                        : "Could not verify the update source, so the automatic update was stopped.\n\n" +
                          "Usual causes: corporate TLS inspection, a self-signed proxy, an expired server certificate, or a wrong system clock.\n\n" +
                          "You can download the package manually in a browser. Note: a manually downloaded package is not verified by this program's signature check.\n\n" +
                          "Open the download page now?";

                    var choice = MessageBox.Show(m,
                        isZh ? "更新源不可信" : "Update source not trusted",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);

                    if (choice == DialogResult.Yes) OpenReleasePage();
                }
                else if (ex is UpdateIntegrityException)
                {
                    // 摘要不符或缺失：这是"来源不可信"，不是"网络不好"。
                    // 坏包已被 CleanupPartialFile 删除，现有版本原封不动。
                    lblStatus.Text = isZh ? "安装包校验失败，已中止" : "Package verification failed, stopped";

                    string errTitle = isZh ? "安装包校验失败" : "Package Verification Failed";
                    string errMsg = isZh
                        ? $"下载到的安装包与发布方签名声明的内容不一致，已删除并中止更新。\n\n{ex.Message}\n\n" +
                          "现有版本未被改动。若反复出现，请从发布页手工下载。"
                        : $"The downloaded package does not match the checksum declared in the signed metadata. It was deleted and the update was canceled.\n\n{ex.Message}\n\n" +
                          "Your current version is unchanged. If this keeps happening, download manually from the release page.";
                    MessageBox.Show(errMsg, errTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    string errTitle = isZh ? "错误" : "Error";
                    string errMsg = isZh ? $"下载失败：\n{ex.Message}" : $"Download failed:\n{ex.Message}";
                    MessageBox.Show(errMsg, errTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                // 重置 UI
                btnUpdate.Enabled = true;
                btnUpdate.Text = isZh ? "重试" : "Retry";
                btnCancel.Enabled = true;
                btnCancel.Text = isZh ? "关闭" : "Close"; // 失败时也应显示"关闭"
                progress.Value = 0;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>
        /// 在默认浏览器中打开发布页。
        /// 仅用于证书校验失败后的手工下载路径，见 UpdateChecker.ReleasePageUrl。
        /// </summary>
        private void OpenReleasePage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = UpdateChecker.ReleasePageUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    IsChinese ? $"无法打开下载页面：{ex.Message}" : $"Cannot open the download page: {ex.Message}",
                    IsChinese ? "错误" : "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartUpdater()
        {
            string resourcesDir = Path.Combine(AppContext.BaseDirectory, "resources");
            string updater = "";

            // ★★ LM-016：失败即不启动 ★★
            // 没有钉住的句柄 / 没有已验证的摘要 ⇒ 没有"已验证的字节"，
            // 这种情况下绝不能让更新器按路径自己去读那个文件（那正是要消除的缺口）。
            if (_pinnedPackage == null || string.IsNullOrEmpty(_verifiedSha256))
            {
                bool isZhGuard = _settings?.Language?.ToLower() == "zh";
                MessageBox.Show(
                    isZhGuard
                        ? "安装包尚未通过完整性校验，已中止更新。"
                        : "The package did not pass integrity verification, so the update was canceled.",
                    isZhGuard ? "安装包校验失败" : "Package Verification Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            // ★★★ 1. 主程序抢先更新 Updater.exe ★★★
            // 防止 Updater 运行时无法自我覆盖
            // 返回解压后的 Updater 路径 (优先取 ZIP 里的新版)
            // ★ LM-016：这个读取发生在摘要校验**之后**，而且 _pinnedPackage 还钉着这个文件
            //   （别的进程此刻无法替换它），所以解出来的更新器一定来自"被校验过的那批字节"。
            string? preparedPath = UpdateChecker.PreUpdateUpdater(_context.SavePath);

            // ★★★ 2. 决策使用哪个 Updater ★★★
            if (!string.IsNullOrEmpty(preparedPath) && File.Exists(preparedPath))
            {
                // A. 预更新成功，直接使用刚解压出来的最新版
                updater = preparedPath;
            }
            else
            {
                // B. 预更新失败 (ZIP异常?)，尝试回退到现有文件
                string newUpdater = Path.Combine(resourcesDir, "LiteMonitor++.Updater.exe");
                string oldUpdater = Path.Combine(resourcesDir, "Updater.exe");

                if (File.Exists(newUpdater)) updater = newUpdater;
                else if (File.Exists(oldUpdater)) updater = oldUpdater;
            }

            // ★★★ 3. 最终检查 ★★★
            if (string.IsNullOrEmpty(updater) || !File.Exists(updater))
            {
                bool isZh = _settings?.Language?.ToLower() == "zh";
                string msg = isZh ? $"找不到更新程序，请尝试重新安装。\nSearch Path: {resourcesDir}" : $"Updater not found, please reinstall.\nSearch Path: {resourcesDir}";
                string title = isZh ? "文件丢失" : "File Missing";
                
                MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = updater,
                // ★ LM-016：把**期望摘要**一并交给更新器。
                //   更新器会用**它自己打开的**文件流重新计算 SHA-256 并比对：
                //   不匹配（或者根本没给摘要）就拒绝解压 —— 不写进行中标记、不建临时目录、
                //   不替换任何文件、退出码非 0，并写明确诊断日志。
                //   以前这里只传路径，更新器按路径自己重读，校验与使用之间没有约束。
                Arguments = UpdatePackageBinder.BuildUpdaterArguments(
                    _context.SavePath,
                    _verifiedSha256),
                WorkingDirectory = AppContext.BaseDirectory // 显式指定工作目录
            };

            // ★★★ 智能提权逻辑 ★★★
            if (IsRunningAsAdmin())
            {
                // 已经是管理员，直接继承权限，无需再弹窗
                psi.UseShellExecute = false; 
            }
            else
            {
                // 普通用户，申请提权
                psi.UseShellExecute = true;
                psi.Verb = "runas"; 
            }

            try
            {
                Process.Start(psi);
                Application.Exit(); // 启动成功，退出主程序
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ★★★ 捕获 Error 1223: 操作已被用户取消 ★★★
                // 用户在 UAC 弹窗点了“否”，我们要静默处理，不要弹错误窗
                // 恢复界面状态，允许用户再次点击
                bool isZh = _settings?.Language?.ToLower() == "zh";
                btnUpdate.Enabled = true;
                btnUpdate.Text = isZh ? "立即更新" : "Update";
            }
            catch (Exception ex)
            {
                bool isZh = _settings?.Language?.ToLower() == "zh";
                string msg = isZh ? $"启动更新程序失败：\n{ex.Message}" : $"Failed to start updater:\n{ex.Message}";
                string title = isZh ? "错误" : "Error";
                
                MessageBox.Show(msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                // 恢复按钮状态
                btnUpdate.Enabled = true;
                btnUpdate.Text = isZh ? "重试" : "Retry";
            }
            finally
            {
                // ★ LM-016：更新器已经启动（或启动失败），钉住句柄到此可以释放。
                //   释放之后仍有一小段"主程序尚未退出 / 更新器尚未打开文件"的窗口 ——
                //   那段窗口由**更新器自己的摘要校验**兜住：它只使用自己校验过的字节。
                ReleasePinnedPackage();
            }
        }

        /// <summary>
        /// 释放钉住更新包的句柄。
        /// </summary>
        private void ReleasePinnedPackage()
        {
            try
            {
                _pinnedPackage?.Dispose();
            }
            catch
            {
                // 释放失败不影响更新结论：更新器还有自己的一次独立校验。
            }
            finally
            {
                _pinnedPackage = null;
            }
        }

        // 辅助方法：检查当前是否为管理员
        private bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private void CleanupPartialFile()
        {
            try
            {
                if (File.Exists(_context.SavePath))
                    File.Delete(_context.SavePath);
            }
            catch { /* 忽略清理错误 */ }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            CenterTitles();
        }

        private void CenterTitles()
        {
            // 确保控件已创建
            if (label1 != null)
            {
                // 计算水平居中位置: (窗口宽度 - 控件宽度) / 2
                label1.Left = (this.ClientSize.Width - label1.Width) / 2;
            }

            if (lblVersion != null)
            {
                lblVersion.Left = (this.ClientSize.Width - lblVersion.Width) / 2;
            }
        }
    }
}