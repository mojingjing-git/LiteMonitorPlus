using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteMonitor.src.Core;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 系统优化器：负责内存清理、性能修剪及后台维护任务
    /// </summary>
    public static class SystemOptimizer
    {
        #region Native Methods
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);
        #endregion

        /// <summary>
        /// 执行深度内存清理 (针对所有进程)
        /// </summary>
        public static void CleanMemory(Action<int>? onProgress = null)
        {
            // 1. 自身 GC (0-5%)
            onProgress?.Invoke(0);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            onProgress?.Invoke(5);

            // 2. 全局清理 (5-100%)
            try
            {
                var procs = Process.GetProcesses();
                int total = procs.Length;
                int current = 0;

                if (total == 0) { onProgress?.Invoke(100); return; }

                foreach (var proc in procs)
                {
                    try
                    {
                        using (proc)
                        {
                            if (!proc.HasExited) EmptyWorkingSet(proc.Handle);
                        }
                    }
                    catch 
                    {
                        // 忽略无权限访问的系统进程
                    }
                    finally
                    {
                        current++;
                        int p = 5 + (int)((double)current / total * 95);
                        onProgress?.Invoke(Math.Min(p, 100));
                    }
                }
            }
            catch { }
            onProgress?.Invoke(100);
        }

        /// <summary>
        /// 修剪当前进程的工作集内存
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                EmptyWorkingSet(proc.Handle);
            }
            catch { }
        }

        /// <summary>
        /// 维护任务调度器（单调截止时间驱动）。
        ///
        /// 状态保存在**实例**内部（见 <see cref="MaintenanceScheduler"/>），
        /// 而不是散落的全局静态时间戳；这里只是因为旧入口是静态方法，
        /// 才保留一个进程级默认实例。测试可以整体替换它。
        ///
        /// ★ 推荐用法：调用方持有**自己的**实例（见 <see cref="CreateScheduler"/>），
        ///   以满足"周期状态属于正确的服务实例"；
        ///   这里的默认实例只服务旧静态入口 <see cref="RunMaintenanceTasks(long)"/>。
        /// </summary>
        private static MaintenanceScheduler _maintenanceScheduler = CreateScheduler();

        /// <summary>
        /// 维护任务调度器实例。
        /// 需要注入假时钟 / 自定义动作的测试可以整体替换它。
        /// </summary>
        public static MaintenanceScheduler MaintenanceScheduler
        {
            get => _maintenanceScheduler;
            set => _maintenanceScheduler = value ?? CreateScheduler();
        }

        /// <summary>
        /// 创建一个**由调用方持有**的维护任务调度器实例（推荐路径）。
        ///
        /// 调用方（典型是 <c>HardwareMonitor</c>）持有自己的实例并调用
        /// <see cref="MaintenanceScheduler.RunDueTasks"/>，
        /// 即可获得"周期状态属于正确的服务实例"的行为：
        ///
        /// <code>
        /// private readonly MaintenanceScheduler _maintenance = SystemOptimizer.CreateScheduler();
        /// ...
        /// var result = _maintenance.RunDueTasks();
        /// if (result.TrafficSave) _samplingStatistics.RecordTrafficSaveTrigger();
        /// if (result.GcRequest) _samplingStatistics.RecordGcRequestTrigger();
        /// if (result.WorkingSetTrim) _samplingStatistics.RecordWorkingSetTrimTrigger();
        /// </code>
        /// </summary>
        /// <param name="timeProvider">可注入的单调时钟，默认 <see cref="TimeProvider.System"/>。</param>
        public static MaintenanceScheduler CreateScheduler(TimeProvider? timeProvider = null)
        {
            return new MaintenanceScheduler(
                onTrafficSave: TrafficLogger.Save,
                onGcRequest: RequestGarbageCollection,
                onWorkingSetTrim: TrimWorkingSetIfNeeded,
                timeProvider: timeProvider);
        }

        /// <summary>
        /// 距离下一次流量保存到期还有多久（诊断用；已经到期时为 <see cref="TimeSpan.Zero"/>）。
        /// </summary>
        public static TimeSpan NextTrafficSaveDelay => _maintenanceScheduler.NextTrafficSaveDelay;

        /// <summary>
        /// 执行定时维护任务 (流量保存、GC 优化、内存修剪)
        ///
        /// ★ 调度语义已改为**单调截止时间**（计划 §4.3）：
        ///   - 流量保存：首次约 5 秒，此后每 60 秒；
        ///   - 自动 GC：首次约 30 秒，此后每 180 秒；
        ///   - 自动工作集检查：首次约 45 秒，此后每 300 秒。
        ///
        /// 旧实现用「整数秒计数器取模」判断到期，同一个到期秒内被调用十次
        /// 就会真正执行十次（实测：十次文件重写）。现在同一到期时间最多消费一次，
        /// 跳过若干周期也只执行一次，不补跑一串。
        ///
        /// <paramref name="secondsCounter"/> 仅为兼容既有调用点保留，**不再参与调度判断**。
        /// </summary>
        /// <param name="secondsCounter">系统运行秒数计数器（已不再使用，保留以兼容旧调用点）</param>
        public static void RunMaintenanceTasks(long secondsCounter)
        {
            _ = secondsCounter;   // 调度改由单调截止时间驱动
            _maintenanceScheduler.RunDueTasks();
        }

        /// <summary>
        /// 执行定时维护任务，并返回**本次真正到期并被消费**的任务。
        ///
        /// 调用方（例如 HardwareMonitor）可以用它把统计计数器记在真实执行上，
        /// 而不是记在「计数器取模刚好命中」这种可能与实际执行脱节的地方。
        /// </summary>
        public static MaintenanceRunResult RunMaintenanceTasksAndReport()
        {
            return _maintenanceScheduler.RunDueTasks();
        }

        /// <summary>自动 GC 请求（保留旧行为：Optimized 模式，不阻塞式完整回收）。</summary>
        private static void RequestGarbageCollection()
        {
            GC.Collect(2, GCCollectionMode.Optimized);
        }

        /// <summary>
        /// 自动工作集检查（保留旧行为：工作集超过 30MB 才裁剪，
        /// 避免以反复清空工作集制造「低内存」假象）。
        /// </summary>
        private static void TrimWorkingSetIfNeeded()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                // 阈值 30MB，确保极致轻量
                if (proc.WorkingSet64 > 30 * 1024 * 1024)
                {
                    EmptyWorkingSet(proc.Handle);
                }
            }
            catch { }
        }
    }
}
