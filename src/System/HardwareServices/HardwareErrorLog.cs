using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 硬件层错误日志。
    ///
    /// 背景：HardwareMonitor 原先用 System.Diagnostics.Debug.WriteLine 记录异常，
    /// 而 Debug.WriteLine 带 [Conditional("DEBUG")]，在 Release 构建里整段会被编译掉。
    /// 结果是「LHM 打开硬件树失败 → 所有 LHM 指标恒 0」这类故障对用户完全不可见：
    /// 程序照常启动，CPU/内存等走性能计数器的指标正常，只有 LHM 系指标全是 0，
    /// 用户既看不到任何提示，也拿不到任何日志。
    ///
    /// 这里改为写入运行目录下的持久化日志文件，Release 构建同样生效。
    /// </summary>
    internal static class HardwareErrorLog
    {
        /// <summary>单个日志文件上限，超过后滚动为 .1 归档，避免无限膨胀。</summary>
        private const long MaxLogBytes = 512 * 1024;

        private static readonly object _lock = new object();

        /// <summary>日志统一用 UTF-8 无 BOM；提成单例，避免每条日志都新建一个编码器。</summary>
        private static readonly UTF8Encoding _utf8NoBom = new UTF8Encoding(false);

        private static string LogPath =>
            Path.Combine(AppContext.BaseDirectory, "LiteMonitor++_HardwareError.log");

        /// <summary>记录一个异常（含完整堆栈）。</summary>
        public static void Write(string source, Exception? ex)
        {
            if (ex == null) return;
            Write(source, ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        /// <summary>记录一条文本消息。</summary>
        public static void Write(string source, string message)
        {
            try
            {
                lock (_lock)
                {
                    string path = LogPath;
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    // 超过上限时先滚动归档，再重新写入
                    try
                    {
                        if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                        {
                            string archived = path + ".1";
                            if (File.Exists(archived)) File.Delete(archived);
                            File.Move(path, archived);
                        }
                    }
                    catch { /* 滚动失败不影响主流程 */ }

                    string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}{Environment.NewLine}";
                    File.AppendAllText(path, entry, _utf8NoBom);
                }
            }
            catch
            {
                // 日志都写不进去（磁盘满 / 权限受限）时只能放弃，不能因此影响主流程
            }
        }

        /// <summary>
        /// 自检发布目录是否被污染。
        ///
        /// 背景：把新版本 publish 到一个**旧的**输出目录（不先清空）时，目录里会留下
        /// 上一个版本的依赖程序集。本机实测踩到的坑是：根目录的
        /// System.Threading.AccessControl.dll 是 33072 字节的**旧版本**，而当前
        /// RID-specific publish 期望的是 runtimes/win/lib/net8.0/ 下那份 41224 字节。
        /// .NET host 的 app-local 优先规则会加载根目录那份旧版本，于是 LHM 打开硬件树时
        /// 在 Mutexes.Open() 里抛 ArgumentNullException(Parameter 'identity')，
        /// 硬件树整个为空，结果是所有 LHM 系指标恒 0，而程序本身看起来一切正常。
        ///
        /// 这里只写日志、不打扰用户；但排查时一眼就能看出「是发布目录被污染了，
        /// 而且是哪个文件」。
        /// </summary>
        public static void CheckPublishDirectory()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                var problems = new System.Collections.Generic.List<string>();

                // ① 旧程序集名(AssemblyName = LiteMonitor)留下的文件
                foreach (string name in new[] { "LiteMonitor.dll", "LiteMonitor.deps.json", "LiteMonitor.runtimeconfig.json" })
                {
                    string p = Path.Combine(baseDir, name);
                    if (File.Exists(p)) problems.Add(name + " (旧程序集名残留)");
                }

                // ② 根目录(app-local)的依赖程序集与 deps.json 期望的版本不一致
                //    .NET host 在 RID-specific 发布下会优先加载根目录那份同名程序集,
                //    所以根目录留着上一版的文件 = 实际加载了错版本。
                problems.AddRange(FindAppLocalMismatches(baseDir));

                if (problems.Count > 0)
                {
                    Write("PublishDirectory",
                        "检测到发布目录被污染，可能导致 LHM 打不开硬件树、硬件指标全部恒 0：" +
                        string.Join("; ", problems) +
                        "。当前程序集名是 LiteMonitor++；建议先清空发布目录再重新 publish" +
                        "(scripts/pack-release.ps1 会自动清空暂存目录)。");
                }
            }
            catch { }
        }

        /// <summary>
        /// 以 deps.json 为准，找出「根目录 app-local 程序集」与「deps.json 期望的运行时资产」
        /// 不一致的项。
        ///
        /// 判据：RID-specific 发布（.deps.json 里 runtime 指向 runtimes/win/lib/net8.0/...）时，
        /// 根目录那份同名 dll 会被 host 优先加载；若它是上一次发布（例如 portable 发布）
        /// 留下的旧版本，就会真的加载到错版本 —— 这正是本机踩到的坑。
        /// portable 发布（runtime 指向 lib/net8.0/...）时根目录那份就是权威版本，不检查。
        /// </summary>
        private static System.Collections.Generic.List<string> FindAppLocalMismatches(string baseDir)
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                string depsPath = Path.Combine(baseDir, "LiteMonitor++.deps.json");
                if (!File.Exists(depsPath)) return result;

                using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
                if (!doc.RootElement.TryGetProperty("targets", out var targets)) return result;

                foreach (var target in targets.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var pkg in target.Value.EnumerateObject())
                    {
                        if (pkg.Value.ValueKind != JsonValueKind.Object) continue;
                        if (!pkg.Value.TryGetProperty("runtime", out var runtime)) continue;
                        if (runtime.ValueKind != JsonValueKind.Object) continue;

                        foreach (var asset in runtime.EnumerateObject())
                        {
                            string rel = asset.Name.Replace('\\', '/');
                            // 只关心 RID 特定资产 (runtimes/...);
                            // portable 布局的 lib/... 指向的就是根目录那份, 无需检查
                            if (!rel.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)) continue;

                            string expected = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(expected)) continue;

                            string local = Path.Combine(baseDir, Path.GetFileName(rel));
                            if (!File.Exists(local)) continue;

                            long localSize = new FileInfo(local).Length;
                            long expectedSize = new FileInfo(expected).Length;
                            if (localSize != expectedSize)
                            {
                                result.Add(string.Format("{0} (根目录 {1} 字节 / 期望 {2} 字节)",
                                    Path.GetFileName(rel), localSize, expectedSize));
                            }
                        }
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
