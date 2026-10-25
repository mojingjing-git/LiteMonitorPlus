using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.Rendering.Configuration;
using LiteMonitor.ThemeEditor;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;

namespace LiteMonitor.src.UI.SettingsPage
{
    /// <summary>
    /// 主题市场预览服务（M4/D5，两层现场渲染的卡片层）：
    ///
    /// - 卡片快照：PNG 磁盘缓存（key = themeId + sha256，版本变更自动失效）。
    ///   GPU 主题走 <see cref="GpuThemePreviewControl.TryCaptureOnce"/>（隐藏宿主、串行渲染，
    ///   同一时刻全市场只有一个 GPU 呈现表面）；GPU 不可用（老显卡/远程会话/启动守卫限流）
    ///   → legacy 示意帧回退 + "需要 DirectX 11" 标注；legacy 主题走
    ///   <see cref="ThemePreviewControl.RenderPreview"/>（固定样例图，绝不抓真实桌面）。
    /// - 渲染统一在 UI 线程上串行执行（下载在后台线程），避免滚动卡顿。
    /// - 诚实性（D5）：一律固定程序化背景 + <see cref="ThemePreviewControl.CreateMockGroups"/>
    ///   演示数据，GPU 快照的场景通知与编辑器一致（FIXED INPUT / CAPTURE 0）。
    /// </summary>
    public static class ThemePreviewService
    {
        /// <summary>快照缓存上限（D5/§9.2：~50MB LRU）。</summary>
        private const long MaxCacheBytes = 50L * 1024 * 1024;

        private const int SnapshotWidth = 360;
        private const int SnapshotHeight = 200;

        private static readonly object _gate = new object();
        private static readonly Queue<SnapshotJob> _queue = new Queue<SnapshotJob>();
        private static readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _pumping;

        internal static string? CacheDirOverride;

        /// <summary>快照磁盘缓存目录（%LocalAppData%\LiteMonitor++\theme-previews）。</summary>
        public static string CacheDir =>
            CacheDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiteMonitor++",
                "theme-previews");

        /// <summary>快照缓存文件路径（key = themeId + sha256 前 12 位，版本变更自动失效）。</summary>
        public static string CachePath(string themeId, string sha256)
        {
            string key = (sha256 ?? "").Trim().ToLowerInvariant();
            if (key.Length > 12) key = key[..12];
            return Path.Combine(CacheDir, $"{themeId}-{key}.png");
        }

        /// <summary>
        /// 请求卡片快照：命中缓存立即回调（同步）；否则入队，下载 + 渲染完成后在 UI 线程回调。
        /// <paramref name="onReady"/> 收到 null 表示快照不可用（调用方显示占位文案）。
        /// </summary>
        public static void RequestSnapshot(MarketThemeEntry entry, Action<string?> onReady)
        {
            if (entry == null || onReady == null) return;

            string path = CachePath(entry.Id, entry.Sha256);
            if (File.Exists(path))
            {
                onReady(path);
                return;
            }

            lock (_gate)
            {
                if (_inFlight.Contains(entry.Id)) return;   // 已排队，完成后由当次回调
                _inFlight.Add(entry.Id);
                _queue.Enqueue(new SnapshotJob(entry, path, onReady));
            }
            PumpQueue();
        }

        private static void PumpQueue()
        {
            lock (_gate)
            {
                if (_pumping) return;
                _pumping = true;
            }

            // 串行泵：下载在后台线程，渲染在 UI 线程（ConfigureAwait(true) 回到 UI 上下文）
            _ = PumpAllAsync();
        }

        private static async Task PumpAllAsync()
        {
            try
            {
                while (true)
                {
                    SnapshotJob job;
                    lock (_gate)
                    {
                        if (_queue.Count == 0)
                        {
                            _pumping = false;
                            return;
                        }
                        job = _queue.Dequeue();
                    }

                    string? result = null;
                    try
                    {
                        byte[] payload = await ThemeMarket.DownloadThemePayloadAsync(job.Entry).ConfigureAwait(true);
                        result = RenderFromPayload(job.Entry, payload, job.PngPath);
                    }
                    catch
                    {
                        result = null;
                    }
                    finally
                    {
                        lock (_gate) { _inFlight.Remove(job.Entry.Id); }
                    }

                    job.OnReady(result);
                }
            }
            catch
            {
                lock (_gate) { _pumping = false; }
            }
        }

        /// <summary>同步渲染一张快照（UI 线程调用；测试可用）。失败返回 null。</summary>
        public static string? RenderSnapshotBlocking(MarketThemeEntry entry)
        {
            if (entry == null) return null;
            string path = CachePath(entry.Id, entry.Sha256);
            if (File.Exists(path)) return path;

            try
            {
                byte[] payload = ThemeMarket.DownloadThemePayloadAsync(entry).GetAwaiter().GetResult();
                return RenderFromPayload(entry, payload, path);
            }
            catch
            {
                return null;
            }
        }

        private static string? RenderFromPayload(MarketThemeEntry entry, byte[] payload, string pngPath)
        {
            Theme? theme;
            try
            {
                theme = JsonSerializer.Deserialize<Theme>(Encoding.UTF8.GetString(payload), ThemeMarket.ThemeJsonOpts);
            }
            catch
            {
                return null;
            }
            if (theme == null) return null;

            // GPU 判据与渲染器同源（F16）：rendering.backend == gpu 且 Validate 全绿才走 GPU 快照；
            // 缺 rendering 块 = 纯 legacy 主题（12 个内置 legacy 主题均如此），直接走 legacy 快照
            var errors = theme.Rendering?.Validate() ?? (IReadOnlyList<string>)Array.Empty<string>();
            bool isGpu = theme.Rendering != null
                         && string.Equals(theme.Rendering.Backend, ThemeRenderingConfig.GpuBackend, StringComparison.OrdinalIgnoreCase)
                         && errors.Count == 0;

            if (isGpu && !Program.SafeMode && TryRenderGpuSnapshot(theme, pngPath))
            {
                TrimCache();
                return pngPath;
            }

            // 回退（D5）：legacy 示意帧；GPU 主题补"需要 DirectX 11"标注
            if (TryRenderLegacySnapshot(theme, pngPath, needDxNotice: isGpu))
            {
                TrimCache();
                return pngPath;
            }
            return null;
        }

        /// <summary>
        /// GPU 快照（D5 spike 落点）：不可见离屏窗体 + <see cref="GpuThemePreviewControl"/>，
        /// HandleCreated 后同步渲一帧并从 GPU 帧缓冲回读 PNG；用完即释放。
        /// </summary>
        private static bool TryRenderGpuSnapshot(Theme theme, string pngPath)
        {
            try
            {
                using var owner = new Form
                {
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-30000, -30000),
                    Size = new Size(SnapshotWidth + 40, SnapshotHeight + 60),
                    FormBorderStyle = FormBorderStyle.None,
                    Opacity = 0d
                };
                var host = new GpuThemePreviewControl(theme, ThemePreviewControl.CreateMockGroups())
                {
                    Bounds = new Rectangle(10, 10, SnapshotWidth, SnapshotHeight)
                };
                owner.Controls.Add(host);

                owner.CreateControl();
                if (!host.IsHandleCreated) host.CreateControl();
                if (!host.IsHandleCreated) return false;

                bool ok = host.TryCaptureOnce(pngPath);
                return ok;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>legacy 示意帧：正式 UIRenderer 管线画到 bitmap（固定样例图，不抓桌面）。</summary>
        private static bool TryRenderLegacySnapshot(Theme theme, string pngPath, bool needDxNotice)
        {
            try
            {
                int w = SnapshotWidth, h = SnapshotHeight;
                using var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(16, 20, 24));
                    var groups = ThemePreviewControl.CreateMockGroups();
                    ThemePreviewControl.RenderPreview(g, groups, theme, new Rectangle(0, 0, w, h));

                    if (needDxNotice)
                    {
                        using var font = new Font("Segoe UI", 9f);
                        using var brush = new SolidBrush(Color.FromArgb(255, 122, 92));
                        string notice = LanguageManager.T("Menu.ThemeMarketNeedDx");
                        g.DrawString(notice, font, brush, new PointF(8, h - 26));
                    }
                }
                bmp.Save(pngPath, ImageFormat.Png);
                return File.Exists(pngPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>缓存 LRU 裁剪：超上限时按最后写入时间从旧到新删除。</summary>
        private static void TrimCache()
        {
            try
            {
                string dir = CacheDir;
                if (!Directory.Exists(dir)) return;

                var files = new DirectoryInfo(dir)
                    .EnumerateFiles("*.png")
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= MaxCacheBytes) break;
                    long size = f.Length;
                    try { f.Delete(); total -= size; } catch { }
                }
            }
            catch { }
        }

        private sealed record SnapshotJob(MarketThemeEntry Entry, string PngPath, Action<string?> OnReady);
    }
}
