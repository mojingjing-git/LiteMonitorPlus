using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using LiteMonitor.Contracts;

namespace LiteMonitor.SystemServices
{
    /// <summary>
    /// E01-B：设备/传感器目录的**发布点**（生产者侧）。
    ///
    /// ★ 版本语义（完善计划 §5 E01-B）：
    ///   - <see cref="HardwareCatalog.Version"/> 只在**结构变化**时递增
    ///     （设备/传感器的增删改、配置导致的探测策略变化）；
    ///   - 传感器**数值**的刷新**不**改变版本（值走快照，结构不变就不重建 LHM 遍历）；
    ///   - 旧版本不得覆盖新版本（发布带版本护栏）。
    ///
    /// ★ 值的来源分工：
    ///   - 本发布点负责**结构**（StableId / RuntimeId / ParentStableId / 设备类型 / 传感器类型 / 名称 / 峰值）；
    ///   - 传感器**当前值**由消费侧按 StableId 从**已发布快照**匹配刷新
    ///     （见 <c>HardwareClientAdapter</c> 的目录构建）——这样"值刷新"不需要再遍历 LHM 树。
    /// </summary>
    public sealed class CatalogPublisher
    {
        private readonly object _gate = new();
        private HardwareCatalog _current = new HardwareCatalog(0, Array.Empty<HardwareCatalogEntry>());
        private long _version;
        private string? _structureSignature;

        /// <summary>最近一次发布的目录（只读；整体替换，读者不会看到半截状态）。</summary>
        public HardwareCatalog Current => Volatile.Read(ref _current);

        /// <summary>当前目录版本（结构变化时递增）。</summary>
        public long Version { get { lock (_gate) { return _version; } } }

        /// <summary>
        /// 发布目录结构。**结构不变则版本不变**；结构变化才递增版本并整体替换。
        /// </summary>
        /// <param name="entries">新的目录条目（结构口径：不含当前值）。</param>
        /// <returns>true 表示结构确实变化、版本已递增。</returns>
        public bool PublishStructure(IReadOnlyList<HardwareCatalogEntry> entries)
        {
            string signature = ComputeSignature(entries);

            lock (_gate)
            {
                if (_structureSignature != null &&
                    string.Equals(_structureSignature, signature, StringComparison.Ordinal))
                {
                    // ★ 结构没变 ⇒ 版本不动（值刷新不重建目录代次）。
                    return false;
                }

                _version++;
                _structureSignature = signature;
                Volatile.Write(ref _current, new HardwareCatalog(_version, Copy(entries)));
                return true;
            }
        }

        /// <summary>
        /// 发布**同版本**的目录条目（结构不变，只更新峰值等随时间变化的字段）。
        /// <para>★ 旧版本不覆盖新版本：只有"版本号相同"或"当前尚未发布过"时才允许写入。</para>
        /// </summary>
        /// <param name="version">本次构建的目录版本。</param>
        /// <param name="entries">新的目录条目。</param>
        /// <returns>true 表示已写入。</returns>
        public bool PublishSameVersion(long version, IReadOnlyList<HardwareCatalogEntry> entries)
        {
            lock (_gate)
            {
                if (version < _version) return false;   // 旧版本不得覆盖新版本
                if (version == 0) return false;        // 尚未发布过结构 ⇒ 无意义
                Volatile.Write(ref _current, new HardwareCatalog(version, Copy(entries)));
                return true;
            }
        }

        /// <summary>
        /// 结构签名：只包含**结构**字段（不含值 / 峰值）。
        /// <para>★ 值变化不得让版本递增——否则每秒刷新都会把目录版本顶上去。</para>
        /// </summary>
        private static string ComputeSignature(IReadOnlyList<HardwareCatalogEntry> entries)
        {
            var sb = new StringBuilder(entries.Count * 48);
            for (int i = 0; i < entries.Count; i++)
            {
                HardwareCatalogEntry e = entries[i];
                sb.Append(e.StableId).Append('|')
                  .Append(e.RuntimeId).Append('|')
                  .Append(e.ParentStableId ?? string.Empty).Append('|')
                  .Append(e.DeviceType).Append('|')
                  .Append(e.SensorType ?? string.Empty).Append('|')
                  .Append(e.Available ? '1' : '0').Append('|')
                  .Append(e.Name).Append(';');
            }
            return sb.ToString();
        }

        private static IReadOnlyList<HardwareCatalogEntry> Copy(IReadOnlyList<HardwareCatalogEntry> entries)
        {
            var copy = new HardwareCatalogEntry[entries.Count];
            for (int i = 0; i < entries.Count; i++) copy[i] = entries[i];
            return copy;
        }
    }
}
