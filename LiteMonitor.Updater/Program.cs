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
    /// <summary>
    /// ZIP 展开边界（zip bomb 防线）。
    /// </summary>
    /// <remarks>
    /// ★ ZIP 头里的展开尺寸可以撒谎（声明 1 字节、实际解压出 1 GB），
    ///   所以这里给三层上限：条目数、单条目展开字节、整包展开字节。
    ///   声明值先查一遍，实际写出的字节再查一遍，任一越界都中止并清理临时目录。
    /// </remarks>
    internal static class ZipExpansionLimits
    {
        /// <summary>单个更新包允许的条目数上限。</summary>
        internal const int MaxEntryCount = 4096;

        /// <summary>单个条目展开后的字节上限（256 MiB）。</summary>
        internal const long MaxSingleEntryBytes = 256L * 1024 * 1024;

        /// <summary>整个更新包展开后的总字节上限（512 MiB）。</summary>
        internal const long MaxExpandedBytes = 512L * 1024 * 1024;
    }

    /// <summary>
    /// 更新过程的可调项。生产路径一律使用默认值。
    /// </summary>
    /// <remarks>
    /// 这里只保留三类接缝：
    ///   · <see cref="RestartMain"/> / <see cref="WaitForMainExit"/>：测试环境里不能去
    ///     拉起或等待用户的 LiteMonitor++ 进程（更不能去终止它）。
    ///   · 展开上限：让"越界即拒绝"这条路径可以用很小的构造样本被确定性地测到。
    ///   · <see cref="TrustedDigestSource"/>：旧客户端兼容用的"可信摘要来源"。
    /// 展开上限的**生产默认值**另有独立用例直接断言常量本身。
    /// </remarks>
    internal sealed class UpdaterRunOptions
    {
        public bool RestartMain { get; init; } = true;
        public bool WaitForMainExit { get; init; } = true;
        public int MaxZipEntryCount { get; init; } = ZipExpansionLimits.MaxEntryCount;
        public long MaxExpandedBytes { get; init; } = ZipExpansionLimits.MaxExpandedBytes;
        public long MaxSingleEntryBytes { get; init; } = ZipExpansionLimits.MaxSingleEntryBytes;

        /// <summary>
        /// "可信摘要来源"：调用方没给期望摘要时，更新器去哪里取那份摘要。
        /// </summary>
        /// <remarks>
        /// ★ 默认 null ⇒ 单参数（旧客户端）调用**仍然被拒绝**，现状行为不变。
        /// ★ 装配它不等于"放宽校验"：来源给出的摘要仍然要通过
        ///   <see cref="RunUpdate"/> 里的摘要比对，比对不过照样拒绝。
        /// </remarks>
        public ITrustedDigestSource? TrustedDigestSource { get; init; }
    }

    internal class Program
    {
        private const string ExeName = "LiteMonitor++.exe";
        private const string UpdaterVersion = "2.1";

        // 与主程序约定的状态文件名。改动时必须同步：
        //   src/System/UpdateChecker.cs
        //   src/UI/Helpers/MainFormBizHelper.cs
        private const string SuccessToken = "update_success";
        private const string InProgressToken = "update_in_progress";
        private const string ErrorLogFile = "update_error.log";

        // 同一安装目录的互斥锁文件。两个更新器同时改同一批文件必然互相破坏。
        internal const string UpdateLockFile = "update.lock";

        /// <summary>退出码约定，供主程序与本进程的自检/排障使用。</summary>
        internal const int ExitSuccess = 0;
        internal const int ExitUsage = 2;
        internal const int ExitNoMainProgram = 3;
        internal const int ExitUpdateFailed = 5;

        /// <summary>摘要契约被拒（摘要缺失/非法/不匹配，或旧式单参数调用）。</summary>
        internal const int ExitIntegrityRejected = 6;

        /// <summary>同一安装目录已有另一个更新进程在跑。</summary>
        internal const int ExitConcurrentUpdate = 7;

        /// <summary>
        /// 注册编码支持 (为智能识别做准备)。
        /// </summary>
        /// <remarks>
        /// 放在静态构造里而不是只在 <see cref="Main"/> 里：测试直接调用内部入口时
        /// 也必须能拿到 GBK 编码，否则"旧版压缩包乱码"那条路径在测试里会变成异常。
        /// </remarks>
        static Program()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// 退出码约定，供主程序与本进程的自检/排障使用。
        /// 0 = 全部替换成功；非 0 = 未完成（主程序据此不提示"已成功更新"）。
        /// </summary>
        static int Main(string[] args) => RunFromArgs(args, AppContext.BaseDirectory);

        /// <summary>
        /// 命令行入口（拆成可测形式，行为与 <see cref="Main"/> 完全一致）。
        /// </summary>
        /// <param name="args">命令行参数。</param>
        /// <param name="resourcesDir">更新器所在目录（生产上是 AppContext.BaseDirectory）。</param>
        /// <param name="options">
        /// 可调项；生产路径传 null 走默认值（**没有可信摘要来源** ⇒ 单参数调用被拒绝）。
        /// </param>
        internal static int RunFromArgs(string[] args, string resourcesDir, UpdaterRunOptions? options = null)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("用法: LiteMonitor++.Updater.exe <update.zip> <sha256> | --selfcheck");
                return ExitUsage;
            }

            // ===========================================================
            // [0] 自检模式
            // ===========================================================
            // 主程序在覆盖旧更新器之后、启动它之前调用本模式。
            // 只要进程能起来且自身文件可读，就说明这份 exe 至少是可加载的：
            // 截断文件、架构不匹配、缺少运行时都会在启动阶段失败，从而被主程序拦下。
            // 注意：本项目是 WinExe，没有控制台；父进程只依赖退出码，控制台输出丢了不影响判定。
            // ★ LM-016：这条路径**完全不变**，不引入摘要参数。
            if (string.Equals(args[0], "--selfcheck", StringComparison.OrdinalIgnoreCase))
                return RunSelfCheck();

            // ===========================================================
            // [0.5] 交接契约（LM-016）
            // ===========================================================
            // ★ 更新器现在必须拿到"期望摘要"才能工作。
            //   旧版主程序用单参数方式调用（只有 ZIP 路径）—— 这是**有意的不兼容变更**：
            //   那种交接方式没有任何"字节必须保持不变"的约束，退化成"不校验就继续解压"
            //   正是本次要消除的缺口，所以这里明确拒绝并给出可诊断原因，绝不静默放行。
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                // ★ 旧客户端兼容接缝：先问"可信摘要来源"要期望摘要。
                //   默认没有装配来源（options 为 null 或 TrustedDigestSource 为 null），
                //   于是这里仍然落到拒绝分支 —— 现状行为不变。
                //   装配了来源也不等于放宽：拿到的摘要照样要过 RunUpdate 里的比对。
                string? sourcedDigest = TryGetSourcedDigest(options, args[0]);
                if (IsWellFormedDigest(sourcedDigest))
                    return RunUpdate(args[0], sourcedDigest!, resourcesDir, options);

                return RejectMissingDigest(resourcesDir, args[0]);
            }

            return RunUpdate(args[0], args[1], resourcesDir, options);
        }

        /// <summary>
        /// 向"可信摘要来源"要期望摘要。拿不到 / 来源抛异常 ⇒ null（fail-closed）。
        /// </summary>
        private static string? TryGetSourcedDigest(UpdaterRunOptions? options, string zipFile)
        {
            ITrustedDigestSource? source = options?.TrustedDigestSource;
            if (source == null) return null;

            try
            {
                return source.TryGetExpectedDigest(zipFile);
            }
            catch
            {
                // 来源自己炸了 ⇒ 等同于"没有可信摘要"，不能因此放行
                return null;
            }
        }

        /// <summary>
        /// 拒绝"没有期望摘要"的调用。
        /// </summary>
        private static int RejectMissingDigest(string resourcesDir, string zipFile)
        {
            string? baseDir = GetMainProgramDirectory(resourcesDir);
            string targetDir = baseDir ?? resourcesDir;

            LogError(
                targetDir,
                "[Reject] 拒绝执行：调用方没有提供期望 SHA-256 摘要（LM-016）。" +
                "本更新器只接受 '<update.zip> <sha256>' 形式；" +
                "单参数调用（旧版主程序）属于有意的不兼容变更，" +
                "不会退化成'不校验就继续解压'。未解压任何文件，现有版本未被改动。" +
                " zip=" + zipFile);

            // ★ 机器可读标记：让**能读它的新客户端**在启动时把这件事明确告诉用户。
            //   旧客户端自己读不了这个标记（它早于本机制），但它的失败会留在磁盘上，
            //   等用户装上带本机制的新版本时就能看到"请手动下载新版本"。
            //   写标记失败不改变拒绝结论，只留日志。
            const string reason =
                "调用方没有提供期望 SHA-256 摘要（LM-016）：本更新器只接受 '<update.zip> <sha256>' 形式；" +
                "旧版主程序的单参数调用属于有意的不兼容变更，不会退化成'不校验就继续解压'。";

            if (LegacyBridgeMarker.TryWrite(
                    targetDir,
                    LegacyBridgeMarker.CodeMissingDigest,
                    reason,
                    UpdaterVersion,
                    out string markerPath))
            {
                LogError(targetDir, "[Bridge] 已写出升级通道不兼容标记：" + markerPath);
            }
            else
            {
                LogError(targetDir, "[Bridge] 写出升级通道不兼容标记失败，原因仍见上方日志。");
            }

            return ExitIntegrityRejected;
        }

        /// <summary>
        /// 执行一次更新。
        /// </summary>
        /// <param name="zipFile">更新包路径。</param>
        /// <param name="expectedSha256">期望的 SHA-256（大写或小写十六进制均可）。</param>
        /// <param name="resourcesDir">更新器所在目录。</param>
        /// <param name="options">可调项；生产路径传 null 走默认值。</param>
        internal static int RunUpdate(
            string zipFile,
            string? expectedSha256,
            string resourcesDir,
            UpdaterRunOptions? options = null)
        {
            options ??= new UpdaterRunOptions();

            // ===========================================================
            // 1. 智能定位主程序目录
            // ===========================================================
            string? baseDir = GetMainProgramDirectory(resourcesDir);

            if (baseDir == null)
            {
                // 连主程序目录都找不到，无法拉起，只能记日志
                LogError(resourcesDir, "[Fatal] 找不到 LiteMonitor++.exe，更新终止！");
                return ExitNoMainProgram;
            }

            // ===========================================================
            // 2. ★★ 摘要校验（LM-016 主防线）★★
            // ===========================================================
            // ★ 必须在**任何解压动作、任何标记写入、任何临时目录创建之前**完成：
            //   校验不过 ⇒ 不写 in_progress、不建临时目录、不解压任何文件、不改任何文件。
            // ★ 期望摘要缺失/非法 ⇒ 同样拒绝。"没提供摘要"不能被解释成"不需要校验"。
            if (!IsWellFormedDigest(expectedSha256))
            {
                LogError(
                    baseDir,
                    "[Reject] 拒绝执行：期望 SHA-256 摘要缺失或格式非法（LM-016）。" +
                    "「没有摘要」不被解释为「不需要校验」。" +
                    "未解压任何文件，现有版本未被改动。");
                return ExitIntegrityRejected;
            }

            string expected = expectedSha256!.Trim();

            FileStream package;
            string actualDigest;
            try
            {
                // ★ 打开一次并**钉住**这个文件：FileShare.Read 表示别的进程仍可读，
                //   但不能写入、替换或删除它。于是"算摘要用的字节"与"随后解压用的字节"
                //   必然是同一批字节 —— 高权限侧不依赖主程序侧的时序。
                package = OpenPinnedPackage(zipFile);
                actualDigest = ComputeSha256Hex(package);
            }
            catch (Exception ex)
            {
                LogError(
                    baseDir,
                    "[Reject] 无法读取更新包，拒绝执行：" + ex.Message +
                    " 未解压任何文件，现有版本未被改动。");
                return ExitIntegrityRejected;
            }

            if (!string.Equals(actualDigest, expected, StringComparison.OrdinalIgnoreCase))
            {
                package.Dispose();
                LogError(
                    baseDir,
                    "[Reject] 拒绝执行：更新包摘要与期望值不一致（LM-016）。" +
                    " expected=" + expected.ToLowerInvariant() +
                    " actual=" + actualDigest.ToLowerInvariant() +
                    " 未解压任何文件，现有版本未被改动。");
                return ExitIntegrityRejected;
            }

            // ===========================================================
            // 2.5 同一安装目录的互斥
            // ===========================================================
            // ★ 两个更新器同时替换同一批文件时，各自的"暂存→备份→提交"会互相踩：
            //   一方的回滚会把另一方刚提交的内容改回去。这里用文件锁把并发更新变成
            //   "第二个进程明确拒绝"，而不是"两个进程互相破坏"。
            // ★ 放在摘要校验之后：被摘要拒绝的更新**不留任何痕迹**（连锁文件都不建）。
            using FileStream? updateLock = TryAcquireUpdateLock(baseDir);
            if (updateLock == null)
            {
                package.Dispose();
                LogError(
                    baseDir,
                    "[Reject] 另一个更新进程正在运行，本次更新已拒绝（并发保护）。" +
                    "未解压任何文件，现有版本未被改动。");
                return ExitConcurrentUpdate;
            }

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

            // ★ package 在整个"校验 → 解压"区间保持打开：这段时间里文件内容不可被替换。
            using (package)
            {
                try
                {
                    // =======================================================
                    // 3. 等待主程序退出 (带缓冲)
                    // =======================================================
                    if (options.WaitForMainExit)
                    {
                        string procName = Path.GetFileNameWithoutExtension(ExeName);
                        WaitExit(procName);

                        // 给系统 1秒 缓冲时间，确保文件句柄彻底释放
                        Thread.Sleep(1000);
                    }

                    // =======================================================
                    // 4. 先落地"进行中"标记
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
                    // 5. 解压到带时间戳的临时目录
                    // =======================================================
                    // 不再复用固定 _update_tmp：固定名会与上一次失败的残留混在一起，
                    // 导致"这份文件到底是新包的还是上次剩的"无法判断。
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                    Directory.CreateDirectory(tempDir);

                    // ★★★ [核心] 智能识别编码解压 ★★★
                    // ★ 从**刚刚校验过摘要的那同一个句柄**解压：展开出来的每一个字节
                    //   都来自被校验过的那批字节。
                    ExtractZipSmart(package, tempDir, options);

                    // =======================================================
                    // 6. 处理 ZIP 的最外层目录
                    // =======================================================
                    string realFolder = ResolveZipRoot(tempDir);

                    // =======================================================
                    // 7. 逐文件暂存 → 校验 → 提交 → 再校验
                    // =======================================================
                    fileTransaction = new UpdateFileTransaction(baseDir, backupDir);
                    applicationResult = fileTransaction.ApplyDirectory(
                        realFolder,
                        relativePath => IsUpdaterBinary(relativePath) ||
                                        UpdateFileTransaction.IsUserOwnedPath(relativePath));

                    foreach (string skipped in applicationResult.SkippedPaths)
                    {
                        LogError(baseDir, "[Preserve] 用户数据未被更新覆盖：" + skipped);
                    }

                    if (!applicationResult.Succeeded)
                    {
                        failure = applicationResult.FailureReason ?? "文件应用失败。";
                    }
                    else
                    {
                        // ===================================================
                        // 8. 目标可执行存在性校验
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
                // ★ 通道既然通了，之前留下的"升级通道不兼容"标记就是过期信息：
                //   不清掉的话，新客户端启动时会对一个已经能正常升级的用户弹"请手动下载"。
                try { File.Delete(Path.Combine(baseDir, LegacyBridgeMarker.MarkerFileName)); } catch { }
            }
            else
            {
                // 失败：保留备份与 zip 作为恢复材料，绝不写成功标记
                try { if (File.Exists(successPath)) File.Delete(successPath); } catch { }
                WriteFailureDiagnostics(baseDir, backupDir, failure, applicationResult);
            }

            // ===========================================================
            // 9. 无论成败都拉起主程序
            // ===========================================================
            // 失败时不拉起会留下"程序消失、无人负责"的状态。完整恢复时拉起当前
            // 旧版本；恢复不完整时安装状态未确认。两种情况都保留失败/恢复日志，
            // 让主程序启动后提示用户检查恢复材料。
            if (options.RestartMain)
            {
                RestartMain(baseDir);
            }

            return committed ? ExitSuccess : ExitUpdateFailed;
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
        /// <summary>
        /// 从**已经校验过摘要的同一个句柄**解压，并守住展开边界。
        /// </summary>
        /// <remarks>
        /// ★ 不用 <c>ZipFile.ExtractToDirectory</c>：它没有条目数/展开字节上限，
        ///   也没有目标目录越界检查，一个精心构造的包就能把磁盘写满或写到目录外。
        /// ★ 同一个句柄 = "校验过的字节"就是"解压出来的字节"，中间没有二次打开的窗口。
        /// </remarks>
        private static void ExtractZipSmart(Stream package, string extractTo, UpdaterRunOptions options)
        {
            // 1. 试探性打开：.NET 默认使用 UTF-8 解析
            //    如果出现"未知字符"(Replacement Character)，说明是 GBK 编码的旧版压缩包
            bool useGbk = RequiresGbkEntryNames(package);

            // 2. 执行真正的解压
            Encoding? entryNameEncoding = useGbk ? Encoding.GetEncoding("GBK") : null;

            package.Position = 0;
            using var archive = entryNameEncoding == null
                ? new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true)
                : new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding);

            int entryCount = 0;
            long declaredTotal = 0;
            long expandedTotal = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (++entryCount > options.MaxZipEntryCount)
                {
                    throw new InvalidDataException(
                        $"更新包条目数超限（上限 {options.MaxZipEntryCount} 个），已中止更新。");
                }

                bool isDirectoryEntry =
                    entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal);

                // ★ 越界条目（.. 穿越 / 绝对路径 / 盘符 / ADS）一律拒绝。
                string relativePath = NormalizeEntryRelativePath(entry.FullName);

                if (isDirectoryEntry)
                {
                    if (relativePath.Length > 0)
                    {
                        Directory.CreateDirectory(
                            ResolveEntryDestination(extractTo, relativePath));
                    }

                    continue;
                }

                if (relativePath.Length == 0) continue;

                // 声明值先查一遍（ZIP 头可以撒谎，但先按它兜底）
                if (entry.Length > options.MaxSingleEntryBytes)
                {
                    throw new InvalidDataException(
                        $"更新包单条目展开后超过上限（{options.MaxSingleEntryBytes} 字节）：{relativePath}");
                }

                declaredTotal += entry.Length;
                if (declaredTotal > options.MaxExpandedBytes)
                {
                    throw new InvalidDataException(
                        $"更新包展开后总字节超过上限（{options.MaxExpandedBytes} 字节），已中止更新。");
                }

                string destination = ResolveEntryDestination(extractTo, relativePath);
                string? destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                // 实际写出的字节再查一遍（防止头里撒谎）
                long written = 0;
                using (Stream source = entry.Open())
                using (var target = new FileStream(
                    destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        expandedTotal += read;

                        if (written > options.MaxSingleEntryBytes)
                        {
                            throw new InvalidDataException(
                                $"更新包单条目实际展开超过上限（{options.MaxSingleEntryBytes} 字节）：{relativePath}");
                        }

                        if (expandedTotal > options.MaxExpandedBytes)
                        {
                            throw new InvalidDataException(
                                $"更新包实际展开总字节超过上限（{options.MaxExpandedBytes} 字节），已中止更新。");
                        }

                        target.Write(buffer, 0, read);
                    }
                }

                if (written != entry.Length)
                {
                    throw new InvalidDataException(
                        $"更新包条目长度与声明不符：{relativePath}（声明 {entry.Length} / 实际 {written}）");
                }
            }
        }

        /// <summary>
        /// 判断条目名是否需要按 GBK 解码。
        /// </summary>
        private static bool RequiresGbkEntryNames(Stream package)
        {
            try
            {
                package.Position = 0;
                using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    // 检查文件名中是否有"未知字符"(Replacement Character)
                    // 如果有，说明 UTF-8 解析失败，这肯定是一个 GBK 编码的旧版压缩包
                    if (entry.FullName.Contains('\uFFFD'))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 如果连头都读不出来，保险起见也尝试 GBK
                return true;
            }

            return false;
        }

        /// <summary>
        /// 把 ZIP 条目名规整成安全的相对路径。
        /// </summary>
        /// <remarks>
        /// 拒绝：绝对路径、盘符/UNC、<c>..</c> 穿越、ADS（<c>file:stream</c>）、
        /// 以及 Windows 会静默裁掉结尾空格/点的名字（会造成同名别名覆盖）。
        /// </remarks>
        private static string NormalizeEntryRelativePath(string entryName)
        {
            if (string.IsNullOrEmpty(entryName)) return "";

            string name = entryName.Replace('/', '\\');

            if (Path.IsPathRooted(name))
            {
                throw new InvalidDataException(
                    $"更新包含越界条目（绝对路径），已中止更新：{entryName}");
            }

            if (name.Contains(':'))
            {
                throw new InvalidDataException(
                    $"更新包含非法条目名（盘符或数据流），已中止更新：{entryName}");
            }

            string[] segments = name.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var safe = new List<string>();
            foreach (string segment in segments)
            {
                if (segment == ".") continue;

                if (segment == "..")
                {
                    throw new InvalidDataException(
                        $"更新包含越界条目（路径穿越），已中止更新：{entryName}");
                }

                if (segment.EndsWith(" ", StringComparison.Ordinal) ||
                    segment.EndsWith(".", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"更新包含非法条目名（结尾空格或点），已中止更新：{entryName}");
                }

                safe.Add(segment);
            }

            return string.Join("\\", safe);
        }

        /// <summary>
        /// 解析条目的落地路径，并确认它没有写出目标目录。
        /// </summary>
        private static string ResolveEntryDestination(string extractTo, string relativePath)
        {
            string root = Path.GetFullPath(extractTo);
            string destination;
            try
            {
                destination = Path.GetFullPath(Path.Combine(root, relativePath));
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"更新包含非法条目路径，已中止更新：{relativePath}（{ex.Message}）", ex);
            }

            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"更新包含越界条目（写出目标目录），已中止更新：{relativePath}");
            }

            return destination;
        }

        // ------------------ 摘要契约 ------------------

        /// <summary>
        /// 打开更新包并"钉住"它。
        /// </summary>
        /// <remarks>
        /// ★ <see cref="FileShare.Read"/> 的含义：别的进程仍然可以**读**这个文件，
        ///   但不能**写入、替换或删除**它（这些操作需要写/删除访问权）。
        ///   因此"算摘要用的字节"与"随后被解压、被安装的字节"必然是同一批字节。
        /// </remarks>
        internal static FileStream OpenPinnedPackage(string zipFile) =>
            new FileStream(zipFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        /// <summary>
        /// 计算摘要（大写十六进制，口径与主程序 <c>UpdateChecker.ComputeFileSha256</c> 一致）。
        /// </summary>
        private static string ComputeSha256Hex(Stream stream)
        {
            stream.Position = 0;
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            stream.Position = 0;
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// 期望摘要是否形态合法（64 位十六进制）。
        /// </summary>
        internal static bool IsWellFormedDigest(string? value)
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
        /// 取得同一安装目录的互斥锁；已被别的更新进程持有时返回 null。
        /// </summary>
        /// <remarks>
        /// 锁基于**文件句柄**而不是文件是否存在：进程崩溃后句柄由系统释放，
        /// 残留的锁文件不会变成"永远锁死"。
        /// </remarks>
        private static FileStream? TryAcquireUpdateLock(string baseDir)
        {
            string lockPath = Path.Combine(baseDir, UpdateLockFile);
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
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
