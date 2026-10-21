using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

            UpdateFileTransaction? fileTransaction = null;
            UpdateApplyResult? applicationResult = null;
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
                    string safeBackup = backupDir.Replace("\"", "'");
                    File.WriteAllText(inProgressPath,
                        "version=" + UpdaterVersion + Environment.NewLine +
                        "backup=\"" + safeBackup + "\"" + Environment.NewLine +
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
                // 6. 逐文件暂存 → 校验 → 提交 → 再校验
                // =======================================================
                fileTransaction = new UpdateFileTransaction(baseDir, backupDir);
                applicationResult = fileTransaction.ApplyDirectory(
                    realFolder,
                    IsUpdaterBinary);

                if (!applicationResult.Succeeded)
                {
                    failure = applicationResult.FailureReason ?? "文件应用失败。";
                }
                else
                {
                    // ===================================================
                    // 7. 目标可执行存在性校验
                    // ===================================================
                    string mainExe = Path.Combine(baseDir, ExeName);
                    if (!File.Exists(mainExe))
                        throw new FileNotFoundException("替换后找不到主程序：" + mainExe);

                    committed = true;
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                if (fileTransaction != null)
                {
                    applicationResult = fileTransaction.RollbackAfterFailure(failure);
                }

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
                WriteFailureDiagnostics(baseDir, backupDir, failure, applicationResult);
            }

            // ===========================================================
            // 8. 无论成败都拉起主程序
            // ===========================================================
            // 失败时不拉起会留下"程序消失、无人负责"的状态。完整恢复时拉起当前
            // 旧版本；恢复不完整时安装状态未确认。两种情况都保留失败/恢复日志，
            // 让主程序启动后提示用户检查恢复材料。
            RestartMain(baseDir);

            return committed ? 0 : 5;
        }

        internal static void WriteFailureDiagnostics(
            string baseDir,
            string backupDir,
            string? failure,
            UpdateApplyResult? applicationResult)
        {
            UpdateRollbackResult? rollback = applicationResult?.Rollback;
            string safeFailure = (failure ?? "未知错误")
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            string safeBackup = backupDir.Replace("\"", "'");

            LogError(baseDir, "[Failure] " + safeFailure);
            if (rollback != null)
            {
                foreach (UpdateFileRecord record in rollback.Records
                    .Where(record => record.RecoveryState == UpdateRecoveryState.Failed))
                {
                    LogError(
                        baseDir,
                        $"[Rollback] 恢复失败：{record.RelativePath} -> {record.RecoveryError}");
                }
            }

            string status;
            string summary;
            if (applicationResult?.Outcome == UpdateApplyOutcome.FailedBeforeCommit)
            {
                status = "failed_before_commit";
                summary = "上次更新在提交前失败，目标文件未被修改。";
            }
            else if (applicationResult?.Outcome == UpdateApplyOutcome.FailedRecovered)
            {
                status = "recovered";
                summary = "上次更新失败，当前文件和此前文件均已恢复。";
            }
            else if (applicationResult?.Outcome == UpdateApplyOutcome.FailedRecoveryIncomplete)
            {
                status = "recovery_incomplete";
                summary = "上次更新失败，恢复不完整，请使用保留的备份人工恢复。";
            }
            else
            {
                status = "unknown";
                summary = "上次更新失败，恢复状态未确认，请检查保留的更新材料。";
            }

            LogError(
                baseDir,
                $"[Result] status={status} backup=\"{safeBackup}\" " +
                $"restored={rollback?.RestoredCount ?? 0} " +
                $"removed={rollback?.RemovedCount ?? 0} " +
                $"failed={rollback?.FailedCount ?? 0}");
            LogError(baseDir, "[Summary] " + summary + " 原因：" + safeFailure);

            // 主程序会在显示一次性通知后删除日志，必须在重启它之前保留完整副本。
            // backupDir 由更新器为本次更新生成，绝不从日志内容读取写入路径。
            try
            {
                Directory.CreateDirectory(backupDir);
                File.Copy(
                    Path.Combine(baseDir, ErrorLogFile),
                    Path.Combine(backupDir, ErrorLogFile),
                    overwrite: false);
            }
            catch (Exception ex)
            {
                // 保留诊断失败不改变文件恢复结论，也不能破坏已经存在的备份。
                LogError(
                    baseDir,
                    $"[Diagnostics] 完整更新日志保留失败，恢复材料目录：{backupDir}。原因：{ex.Message}");
            }
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
                string path = Path.Combine(dir, ErrorLogFile);
                string normalized = (msg ?? string.Empty)
                    .Replace("\r\n", "\n")
                    .Replace('\r', '\n');
                DateTime timestamp = DateTime.Now;
                using var writer = new StreamWriter(path, append: true);
                foreach (string line in normalized.Split('\n'))
                {
                    writer.WriteLine(timestamp + " " + line);
                }
            }
            catch { }
        }
    }
}
