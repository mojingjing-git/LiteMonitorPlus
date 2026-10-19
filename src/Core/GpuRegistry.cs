using System;

namespace LiteMonitor.src.Core
{
    /// <summary>
    /// 运行时显卡信息注册表: 把 "GPU.{序号}" 对应到显卡的简称 / 全名, 供 UI 打标签用。
    /// 由 SensorMap 在建立映射时写入。
    /// </summary>
    public static class GpuRegistry
    {
        // ★★★ 用 volatile 引用持有不可变快照 ★★★
        // SensorMap.Rebuild 是在锁外边填边改的, 读侧 (UI 线程) 可能看到
        // "第 0 张已更新、第 1 张还没更新"的半成品注册表。
        // 整体替换一个数组引用后, 读侧要么看到旧的完整快照, 要么看到新的完整快照。
        private static volatile GpuInfo[] _entries = new GpuInfo[GpuKey.MaxGpuCount];

        /// <summary>整体替换注册表 (原子操作, 读侧不会看到半更新状态)</summary>
        public static void ReplaceAll(GpuInfo[] entries)
        {
            var copy = new GpuInfo[GpuKey.MaxGpuCount];
            if (entries != null)
            {
                int n = Math.Min(entries.Length, copy.Length);
                for (int i = 0; i < n; i++) copy[i] = entries[i] ?? new GpuInfo();
            }
            _entries = copy;
        }

        /// <summary>登记某序号对应的显卡 (单项写入, 仅供调试/单点更新)</summary>
        public static void Set(int index, string shortName, string fullName)
        {
            if (index < 0 || index >= GpuKey.MaxGpuCount) return;
            var copy = (GpuInfo[])_entries.Clone();
            copy[index] = new GpuInfo { ShortName = shortName ?? "", FullName = fullName ?? "" };
            _entries = copy;
        }

        /// <summary>取显卡简称 (如 "3080 Ti")，越界返回空串</summary>
        public static string GetShortName(int index)
        {
            var e = _entries;
            return (index >= 0 && index < e.Length && e[index] != null) ? e[index].ShortName : "";
        }

        /// <summary>取显卡全名 (如 "NVIDIA GeForce RTX 3080 Ti")，越界返回空串</summary>
        public static string GetFullName(int index)
        {
            var e = _entries;
            return (index >= 0 && index < e.Length && e[index] != null) ? e[index].FullName : "";
        }

        /// <summary>清空注册表 (硬件重载时调用)</summary>
        public static void Clear() => ReplaceAll(null);
    }

    /// <summary>一张显卡的简称 / 全名。</summary>
    public sealed class GpuInfo
    {
        public string ShortName { get; set; } = "";
        public string FullName { get; set; } = "";
    }

}