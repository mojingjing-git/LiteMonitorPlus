using System;
using System.IO;
using System.Text;
using System.Threading;

namespace LiteMonitor.src.Plugins
{
    /// <summary>
    /// F17：目标文件被占用、无法原子替换（可识别异常）。
    ///
    /// <para>
    /// Windows 上 <c>MoveFileEx(..., MOVEFILE_REPLACE_EXISTING)</c> 要求**目标文件当前没有任何打开的句柄**
    /// （哪怕对方以 <c>FILE_SHARE_DELETE</c> 共享，替换依然返回 Win32 <c>ERROR_ACCESS_DENIED</c>）。
    /// 只要有一个读者正拿着目标文件（插件加载器、恢复对账、可用性校验……），
    /// 原子替换就会当场失败。F17 因此把"替换"收敛到一处：共享读 + 有界重试。
    /// </para>
    ///
    /// <para>
    ///
    /// <para>
    /// ★ 两类 Windows 语义必须分清（实测结论，勿混淆）：
    /// <list type="bullet">
    /// <item><b>replace-existing</b>（目标已存在）：要求目标<b>无任何打开的句柄</b> ⇒
    /// 读侧共享 <c>FileShare.Delete</c> 救不了它，只有「等占用解除后重试」能救。</item>
    /// <item><b>rename-away</b>（改名挪走，目标不存在）：只要求源允许 delete 共享 ⇒
    /// 读侧共享 <c>FileShare.Delete</c> 确实能解锁。</item>
    /// </list>
    /// 所以两个机制的分工是：共享读负责「读者不挡写侧的改名/删除」，
    /// 有界重试负责「replace 被占用时的最终出路」。
    /// </para>
    ///
    /// ★ 与 <see cref="IOException"/> 的区别：本异常**只**表示"重试预算耗尽、目标仍被占用"，
    /// 调用方可以据此判定"目标文件被占用，无法原子替换"，而不是把它当成普通 IO 故障。
    /// </para>
    /// </summary>
    internal sealed class MarketFileReplaceBlockedException : IOException
    {
        internal MarketFileReplaceBlockedException(string path, int attempts, Exception inner)
            : base("目标文件被占用，无法原子替换：" + path + "（已尝试 " + attempts + " 次）", inner)
        {
            DestinationPath = path;
            Attempts = attempts;
        }

        /// <summary>被占用的目标文件路径。</summary>
        internal string DestinationPath { get; }

        /// <summary>实际尝试次数（首次 + 重试）。</summary>
        internal int Attempts { get; }
    }

    /// <summary>
    /// F17：插件/主题市场落盘的文件 IO 边界。
    ///
    /// <para>
    /// ★ 共享读：所有"读目标文件"的入口都改成
    /// <c>FileShare.ReadWrite | FileShare.Delete</c>。
    /// 读侧不再对写侧持有排他权 —— 读者不会挡住别人把文件改名挪走（rename-away）。
    /// ★ 但这**不能**让 replace-existing 成功 —— Windows 的 replace 要求目标无任何打开的句柄。
    /// </para>
    ///
    /// <para>
    /// ★ 有界重试：原子替换在 <see cref="UnauthorizedAccessException"/> / <see cref="IOException"/>
    /// 上做**有界**重试（次数上限 + 单调时钟预算上限），绝不无限循环；
    /// 预算耗尽仍失败 ⇒ 抛 <see cref="MarketFileReplaceBlockedException"/>，由既有回滚路径清理，
    /// **不把"重试耗尽"吞掉伪装成成功**。
    /// </para>
    /// </summary>
    internal static class MarketFileIo
    {
        /// <summary>单次原子替换的最大尝试次数（首次 + 3 次重试）。</summary>
        internal const int MaxAttempts = 4;

        /// <summary>单次原子替换的总预算（单调时钟口径，硬上限）。</summary>
        internal static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(300);

        /// <summary>退避基数：第 k 次重试前等待 min(基数×2^(k-1), 剩余预算)。</summary>
        internal static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(25);

        /// <summary>
        /// 测试接缝：整组重试策略（时钟 / 等待 / 观测回调）。
        /// 用 <see cref="AsyncLocal{T}"/> 隔离 ⇒ 并行用例之间不会互相污染。
        /// </summary>
        internal static AsyncLocal<ReplacePolicy?> Policy { get; } = new AsyncLocal<ReplacePolicy?>();

        /// <summary>F17：可注入的重试策略（生产用默认值；测试用它把"等待"换成确定性闸门）。</summary>
        internal sealed class ReplacePolicy
        {
            /// <summary>单调时钟（预算判定不许依赖墙钟）。</summary>
            internal TimeProvider Clock { get; init; } = TimeProvider.System;

            /// <summary>等待实现：参数是本次要等的时长，返回 false 表示"不再继续等"。</summary>
            internal Func<TimeSpan, bool> Wait { get; init; } = MarketFileIo.DefaultPolicyWait;

            /// <summary>观测回调（每次重试前触发一次）。</summary>
            internal Action<int, Exception>? OnRetry { get; init; }
        }

        /// <summary>
        /// 读一个文件的全部字节（共享读：不阻塞写侧改名/替换）。
        /// 失败时照旧抛异常，由调用方按既有语义处理（读不到就当不匹配 / 返回 null）。
        /// </summary>
        internal static byte[] ReadAllBytesShared(string path)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        /// <summary>
        /// 读一个文件的全部文本（共享读；语义与 <see cref="File.ReadAllText(string)"/> 一致：UTF-8 + BOM 识别）。
        /// </summary>
        internal static string ReadAllTextShared(string path)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// 原子替换（<c>File.Move(overwrite: true)</c>）+ **有界**重试。
        ///
        /// <para>
        /// 失败语义：重试次数耗尽或单调时钟预算耗尽 ⇒ 抛
        /// <see cref="MarketFileReplaceBlockedException"/>（可识别：目标文件被占用）。
        /// 调用方（安装事务 / 回滚 / 对账 / 卸载回滚）据此走既有回滚路径，不留半成品。
        /// </para>
        /// </summary>
        internal static void MoveAtomic(string sourcePath, string destinationPath)
        {
            ReplacePolicy? policy = Policy.Value;
            TimeProvider clock = policy?.Clock ?? TimeProvider.System;
            Func<TimeSpan, bool> wait = policy?.Wait ?? DefaultPolicyWait;
            Action<int, Exception>? onRetry = policy?.OnRetry;

            long startedAt = clock.GetTimestamp();
            Exception? last = null;
            int attempt = 0;

            for (; ; attempt++)
            {
                try
                {
                    File.Move(sourcePath, destinationPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    last = ex;
                }

                if (attempt + 1 >= MaxAttempts) break;

                TimeSpan remaining = DefaultBudget - clock.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero) break;

                onRetry?.Invoke(attempt + 1, last!);

                TimeSpan delay = Backoff(attempt + 1);
                if (delay > remaining) delay = remaining;
                if (!wait(delay)) break;
            }

            throw new MarketFileReplaceBlockedException(destinationPath, attempt + 1, last!);
        }

        /// <summary>退避表：25ms → 50ms → 100ms …（上限由剩余预算兜底）。</summary>
        private static TimeSpan Backoff(int retryIndex)
        {
            int shift = Math.Min(retryIndex - 1, 16);
            long ticks = BaseDelay.Ticks * (1L << shift);
            return TimeSpan.FromTicks(ticks);
        }

        /// <summary>
        /// 只有"可能被占用/瞬时锁"造成的失败才值得重试；
        /// 源文件/目录不存在不是瞬时故障，重试没有意义。
        /// </summary>
        private static bool IsTransient(Exception ex)
            => ex is UnauthorizedAccessException
               || (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException);

        private static bool DefaultPolicyWait(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero) return true;
            // ★ 时长由"退避表 + 剩余预算"算出来，不是写死的常数；
            //   上限由 DefaultBudget 兜底 ⇒ 任何情况下都不会无限等。
            Thread.Sleep(delay);
            return true;
        }
    }
}
