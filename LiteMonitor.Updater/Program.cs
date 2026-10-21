using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LiteMonitor.Updater
{
    internal class Program
    {
        private const string ExeName = "LiteMonitor++.exe";
        private const string UpdaterVersion = "2.0";

        // 与主程序约定的状态文件名。改动时必须同步：
        //   src/System/UpdateChecker.cs
        //   src/UI/Helpers/MainFormBizHelper.cs
        private const string SuccessToken = "update_success";
        private const string InProgressToken = "update_in_progress";
        private const string ErrorLogFile = "update_error.log";

        /// <summary>
        /// 退出码约定，供主程序与本进程的自检/排障使用。
        /// 0 = 全部替换成功；非 0 = 未完成（主程序据此不提示"已成功更新"）。
        /// </summary>
        static int Main(string[] args)
        {
            // ★★★ [基础] 注册编码支持 (为智能识别做准备) ★★★
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (args.Length == 0)
            {
                Console.Error.WriteLine("用法: LiteMonitor++.Updater.exe <update.zip> | --selfcheck");
                return 2;
            }

            // ===========================================================
            // [0] 自检模式
            // ===========================================================
            // 主程序在覆盖旧更新器之后、启动它之前调用本模式。
            // 只要进程能起来且自身文件可读，就说明这份 exe 至少是可加载的：
            // 截断文件、架构不匹配、缺少运行时都会在启动阶段失败，从而被主程序拦下。
            // 注意：本项目是 WinExe，没有控制台；父进程只依赖退出码，控制台输出丢了不影响判定。
            if (string.Equals(args[0], "--selfcheck", StringComparison.OrdinalIgnoreCase))
                return RunSelfCheck();

            string zipFile = args[0];
            string resourcesDir = AppContext.BaseDirectory;

            // ===========================================================
            // 1. 智能定位主程序目录
            // ===========================================================
            string? baseDir = GetMainProgramDirectory(resourcesDir);

            if (baseDir == null)
            {
                // 连主程序目录都找不到，无法拉起，只能记日志
                LogError(resourcesDir, "[Fatal] 找不到 LiteMonitor++.exe，更新终止！");
                return 3;
            }

            // ===========================================================
            // 2. 等待主程序退出 (带缓冲)
            // ===========================================================
            string procName = Path.GetFileNameWithoutExtension(ExeName);
            WaitExit(procName);

            // 给系统 1秒 缓冲时间，确保文件句柄彻底释放
            Thread.Sleep(1000);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string tempDir = Path.Combine(baseDir, "_update_tmp_" + stamp);
            string backupDir = Path.Combine(baseDir, "_update_backup_" + stamp);
            string inProgressPath = Path.Combine(baseDir, InProgressToken);
            string successPath = Path.Combine(baseDir, SuccessToken);
            string errorLogPath = Path.Combine(baseDir, ErrorLogFile);

            // 已成功替换的文件清单，用于失败时逆序回滚
            var replaced = new List<(string Rel, string Dest, string? Backup)>();
            bool committed = false;
            string? failure = null;

            try
            {
                // =======================================================
                // 3. 先落地"进行中"标记
                // =======================================================
                // 必须在替换任何文件之前写。若本进程随后被强杀或崩溃，
                // 主程序下次启动会看到标记却没有任何结论，从而提示"上次更新未完成"。
                try
                {
                    File.WriteAllText(inProgressPath,
                        "version=" + UpdaterVersion + Environment.NewLine +
                        "backup=" + backupDir + Environment.NewLine +
                        "time=" + DateTime.Now.ToString("O") + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    throw new IOException("无法写入更新进行标记，已取消更新：" + ex.Message, ex);
                }

                // =======================================================
                // 4. 解压到带时间戳的临时目录
                // =======================================================
                // 不再复用固定 _update_tmp：固定名会与上一次失败的残留混在一起，
                // 导致"这份文件到底是新包的还是上次剩的"无法判断。
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                // ★★★ [核心] 智能识别编码解压 ★★★
                ExtractZipSmart(zipFile, tempDir);

                // =======================================================
                // 5. 处理 ZIP 的最外层目录
                // =======================================================
                string realFolder = ResolveZipRoot(tempDir);

                // =======================================================
                // 6. 备份 → 替换 → 校验（任一失败即抛，进入回滚）
                // =======================================================
                Directory.CreateDirectory(backupDir);

                foreach (string srcPath in Directory.GetFiles(realFolder, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(realFolder, srcPath);

                    // 更新器自身已由主程序（PreUpdateUpdater）在启动前更新，这里跳过，
                    // 否则会因"文件正在使用"而失败。
                    if (IsUpdaterBinary(rel)) continue;

                    string destPath = Path.Combine(baseDir, rel);
                    string? destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                    // 先备份原文件，再覆盖。备份是回滚的唯一依据，必须在覆盖之前完成。
                    string? backup = null;
                    if (File.Exists(destPath))
                    {
                        backup = Path.Combine(backupDir, rel);
                        string? backupParent = Path.GetDirectoryName(backup);
                        if (!string.IsNullOrEmpty(backupParent)) Directory.CreateDirectory(backupParent);
                        File.Copy(destPath, backup, true);
                    }

                    if (!TryCopyFile(srcPath, destPath))
                        throw new IOException("无法覆盖文件（被占用或权限不足）：" + rel);

                    // 校验：长度必须一致；二进制再比对摘要，防止只写了一半或写入损坏。
                    long srcLen = new FileInfo(srcPath).Length;
                    long dstLen = new FileInfo(destPath).Length;
                    if (srcLen != dstLen)
                        throw new IOException($"覆盖后长度不一致：{rel}（源 {srcLen} / 目标 {dstLen}）");

                    if (IsBinaryAsset(rel) &&
                        !string.Equals(Sha256Of(srcPath), Sha256Of(destPath), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("覆盖后摘要不一致：" + rel);
                    }

                    replaced.Add((rel, destPath, backup));
                }

                // =======================================================
                // 7. 目标可执行存在性校验
                // =======================================================
                string mainExe = Path.Combine(baseDir, ExeName);
                if (!File.Exists(mainExe))
                    throw new FileNotFoundException("替换后找不到主程序：" + mainExe);

                committed = true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                LogError(baseDir, "[Rollback] " + ex.Message);
                Rollback(replaced, baseDir);

                // 结论由 update_error.log 承载；这里清掉 in_progress，
                // 否则主程序每次启动都会报"上次更新未完成"。
                try { File.Delete(inProgressPath); } catch { }
            }
            finally
            {
                // 解压产物不是恢复材料，无论成败都清理
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }

            if (committed)
            {
                // 只有走到这里才代表"文件确实被正确替换"，此时才允许写成功标记。
                // 旧实现无条件写，导致"更新器跑到了最后一步"被当成"更新成功"。
                try { File.WriteAllText(successPath, DateTime.Now.ToString("O") + Environment.NewLine); } catch { }
                try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
                try { File.Delete(zipFile); } catch { }
                try { File.Delete(inProgressPath); } catch { }
                try { if (File.Exists(errorLogPath)) File.Delete(errorLogPath); } catch { }
            }
            else
            {
                // 失败：保留备份与 zip 作为恢复材料，绝不写成功标记
                try { if (File.Exists(successPath)) File.Delete(successPath); } catch { }
                LogError(baseDir,
                    $"[Failed] 更新未完成（{failure}）。已回滚 {replaced.Count} 个文件，备份保留在 {backupDir}");

                // 把失败摘要单独留一份，供主程序启动时读取并提示后删除
                LogError(baseDir, "[Summary] 上次更新失败，已回滚到原版本。原因：" + failure);
            }

            // ===========================================================
            // 8. 无论成败都拉起主程序
            // ===========================================================
            // 失败时不拉起会留下"程序消失、无人负责"的状态；此时已经回滚，
            // 拉起的是旧版本，是更小的伤害。失败事实由"不写 success + 写 error log"表达，
            // 而不是由"不重启"表达。
            RestartMain(baseDir);

            return committed ? 0 : 5;
        }

        // ======================================================
        // 自检
        // ======================================================
        private static int RunSelfCheck()
        {
            try
            {
                string? self = Environment.ProcessPath;
                if (string.IsNullOrEmpty(self) || !File.Exists(self)) return 2;

                var fi = new FileInfo(self);
                if (fi.Length <= 0) return 2;

                // 父进程只依赖退出码；这行输出在有重定向时才会被看到。
                Console.Out.WriteLine($"LiteMonitor.Updater selfcheck OK version={UpdaterVersion} size={fi.Length}");
                return 0;
            }
            catch
            {
                return 2;
            }
        }

        // ======================================================
        // 回滚
        // ======================================================
        private static void Rollback(List<(string Rel, string Dest, string? Backup)> replaced, string baseDir)
        {
            int restored = 0, removed = 0, failedCount = 0;

            // 逆序恢复：后替换的先还原，避免目录被先删后建
            for (int i = replaced.Count - 1; i >= 0; i--)
            {
                var (rel, dest, backup) = replaced[i];
                try
                {
                    if (backup != null && File.Exists(backup))
                    {
                        File.Copy(backup, dest, true);
                        restored++;
                    }
                    else
                    {
                        // 原本不存在的新增文件：删掉，回到"没有这个文件"的状态
                        if (File.Exists(dest)) { File.Delete(dest); removed++; }
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    LogError(baseDir, $"[Rollback] 恢复失败：{rel} -> {ex.Message}");
                }
            }

            LogError(baseDir,
                $"[Rollback] 已恢复 {restored} 个、删除 {removed} 个新增文件、失败 {failedCount} 个");
        }

        // ======================================================
        // ★★★ [核心方法] 智能解压 (自动兼容 UTF-8 和 GBK) ★★★
        // ======================================================
        private static void ExtractZipSmart(string zipPath, string extractTo)
        {
            // 默认假设是标准 UTF-8
            bool useGbk = false;

            try 
            {
                // 1. 试探性打开：.NET 默认使用 UTF-8 解析
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        // 检查文件名中是否有“未知字符”(Replacement Character)
                        // 如果有，说明 UTF-8 解析失败，这肯定是一个 GBK 编码的旧版压缩包
                        if (entry.FullName.Contains('\uFFFD'))
                        {
                            useGbk = true;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // 如果连头都读不出来，保险起见也尝试 GBK
                useGbk = true;
            }

            // 2. 执行真正的解压
            if (useGbk)
            {
                // 使用 GBK 解压 (解决旧版软件压缩包乱码)
                var gbk = Encoding.GetEncoding("GBK");
                ZipFile.ExtractToDirectory(zipPath, extractTo, gbk, true);
            }
            else
            {
                // 使用默认 UTF-8 解压 (解决 GitHub/新版压缩包乱码)
                ZipFile.ExtractToDirectory(zipPath, extractTo, true);
            }
        }

        // ------------------ 辅助方法 ------------------

        /// <summary>判断是否为更新器自身（由主程序负责更新，这里必须跳过）。</summary>
        private static bool IsUpdaterBinary(string rel)
        {
            return rel.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase) ||
                   rel.EndsWith("LiteMonitor++.Updater.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>需要做摘要校验的二进制资产。</summary>
        private static bool IsBinaryAsset(string rel)
        {
            string ext = Path.GetExtension(rel);
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private static string Sha256Of(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        private static bool TryCopyFile(string src, string dest)
        {
            // 最多重试 10 次，每次间隔 500ms (总共等待 5秒)
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    // 简单粗暴：直接覆盖
                    File.Copy(src, dest, true);
                    return true; 
                }
                catch (IOException) // 文件被占用
                {
                    if (i == 9) return false; 
                    Thread.Sleep(500); 
                }
                catch (UnauthorizedAccessException) // 权限不足
                {
                    if (i == 9) return false;
                    Thread.Sleep(500);
                }
            }
            return false;
        }

        private static bool ContainsMainExe(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                            .Any(f => Path.GetFileName(f)
                                .Equals(ExeName, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------ 自动检测主程序目录 ------------------
        private static string? GetMainProgramDirectory(string resourcesDir)
        {
            // 先检查 resourcesDir 的上级目录
            DirectoryInfo? current = new DirectoryInfo(resourcesDir).Parent;

            if (current != null && ContainsMainExe(current.FullName))
                return current.FullName;

            // 再检查当前目录（便携版）
            if (ContainsMainExe(resourcesDir))
                return resourcesDir;

            return null;
        }

        // ------------------ 处理 Zip 最外层目录 ------------------
        private static string ResolveZipRoot(string tempDir)
        {
            var entries = Directory.GetFileSystemEntries(tempDir);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
                return entries[0];
            return tempDir;
        }

        // ------------------ 重启主程序 ------------------
        /// <summary>
        /// 拉起主程序。成功标记不在这里写——那正是旧实现的缺陷来源。
        /// 启动失败必须留痕：旧实现的 catch { } 会让"程序消失"没有任何线索。
        /// </summary>
        private static void RestartMain(string baseDir)
        {
            string exePath = Path.Combine(baseDir, ExeName);

            if (!File.Exists(exePath))
            {
                LogError(baseDir, "[Fatal] 重启失败：找不到 " + exePath);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogError(baseDir, "[Fatal] 重启主程序失败：" + ex.Message);
            }
        }

        private static void WaitExit(string name)
        {
            // 阶段 1: 礼貌等待 (3秒)
            // 大多数情况下，主程序在启动 Updater 后会立即调用 Application.Exit()
            for (int i = 0; i < 15; i++)
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) return;
                Thread.Sleep(200);
            }

            // 阶段 2: 请求关闭 (尝试 CloseMainWindow)
            try 
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var p in processes)
                {
                    if (!p.HasExited) p.CloseMainWindow();
                }
            }
            catch { }

            // 阶段 3: 最终强制清理 (如果还不行，只能 Kill)
            // 等待最多 5 秒
            for (int i = 0; i < 25; i++)
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0) return;
                
                // 只有在超时严重时才尝试 Kill，且增加间隔
                if (i > 10) 
                {
                    foreach (var p in processes) 
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                    }
                }
                Thread.Sleep(200);
            }
        }

        private static void LogError(string dir, string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, ErrorLogFile),
                    DateTime.Now + " " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
