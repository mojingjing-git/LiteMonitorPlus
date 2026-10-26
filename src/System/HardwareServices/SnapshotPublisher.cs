using System;
using System.Collections.Generic;
using System.Threading;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core; // MetricUtils（单位口径的单一来源；只读调用）

namespace LiteMonitor.SystemServices
{
    /// <summary>
    /// E01-A：采样快照的**唯一发布点**（生产者侧）。
    ///
    /// ★ 语义边界（完善计划 §5 E01-A）：
    ///   1) 只有采集层会**写**这里（真实采样完成后一次）；
    ///   2) 消费方（面板 / 任务栏 / Web / 历史 / 传感器窗口）**只读**已发布批次；
    ///   3) <see cref="Current"/> 的读取**不推进** BatchId、**不改变**时效字段 ——
    ///      同一批数据读多少次都返回同一份不可变内容；
    ///   4) 发布用**原子引用交换**（Volatile.Write）：读者要么看到旧批次、要么看到新批次，
    ///      永远不会看到"半截"状态（值与目录不混代次）。
    ///
    /// ★ 代次语义（本包范围）：
    ///   - <see cref="BatchId"/>：只在**发布**时递增（读取不推进）；
    ///   - <see cref="SessionEpoch"/>：只在**采集器重建（进程内）**时递增
    ///     （Host 会话切换的纪元递增属管道路径，归 P-E02）；
    ///   - <see cref="ConfigRevision"/>：由适配器 ApplyConfig **成功后**回写。
    ///
    /// ★ 初始状态：尚未完成第一次真实采集时，返回**空批次**（不伪造成功批次），
    ///   缺失原因见 <see cref="CurrentMissingReason"/>（"initializing"）。
    /// </summary>
    public sealed class SnapshotPublisher
    {
        /// <summary>初始化未完成时的缺失原因。</summary>
        public const string InitializingReason = "initializing";

        /// <summary>该指标本轮没有读到值时的缺失原因。</summary>
        public const string NoValueReason = "no-value";

        /// <summary>本轮真实采集失败时的缺失原因。</summary>
        public const string CollectionFailedReason = "collection-failed";

        private readonly TimeProvider _time;
        private readonly object _gate = new();

        private HardwareSnapshot? _current;             // 已发布批次（原子交换点）
        private HardwareSnapshot? _initializingSnapshot; // 初始空批次（只创建一次）
        private long _batchId;
        private int _sessionEpoch;
        private int _configRevision = -1;
        private string? _missingReason = InitializingReason;

        /// <param name="timeProvider">可注入的单调时钟（测试用假时钟）；默认 <see cref="TimeProvider.System"/>。</param>
        public SnapshotPublisher(TimeProvider? timeProvider = null)
        {
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>最近一次**已发布**的批次；从未发布过时返回初始空批次（BatchId=0）。</summary>
        public HardwareSnapshot Current
        {
            get
            {
                HardwareSnapshot? snapshot = Volatile.Read(ref _current);
                return snapshot ?? InitializingSnapshot();
            }
        }

        /// <summary>是否已经发布过至少一个真实批次。</summary>
        public bool HasPublished => Volatile.Read(ref _current) != null;

        /// <summary>当前批次号（只在发布时递增；读取不推进）。</summary>
        public long BatchId => Interlocked.Read(ref _batchId);

        /// <summary>会话纪元（采集器重建时 +1）。</summary>
        public int SessionEpoch => Volatile.Read(ref _sessionEpoch);

        /// <summary>最近一次**实际生效**的配置 revision（未应用过任何配置时为 -1）。</summary>
        public int ConfigRevision => Volatile.Read(ref _configRevision);

        /// <summary>
        /// 当前批次的**批次级**缺失原因：
        /// "initializing"（尚未发布）/ "collection-failed"（本轮采集失败）/ null（正常）。
        /// <para>★ 契约的 <see cref="HardwareSnapshot"/> 没有批次级质量字段，
        /// 空批次无法用样本承载原因，因此原因在这里单独暴露。</para>
        /// </summary>
        public string? CurrentMissingReason => Volatile.Read(ref _missingReason);

        /// <summary>当前单调时刻（毫秒）。与 <see cref="HardwareSnapshot.TimestampMonoMs"/> 同口径。</summary>
        public double NowMonoMs
        {
            get
            {
                long frequency = _time.TimestampFrequency;
                if (frequency <= 0) frequency = TimeSpan.TicksPerSecond;
                return _time.GetTimestamp() * 1000.0 / frequency;
            }
        }

        /// <summary>
        /// 采集器重建（进程内）：会话纪元 +1。
        /// <para>★ 只负责"采集器重建"（硬件树整体替换）；Host 会话切换的纪元递增属管道路径（P-E02）。</para>
        /// </summary>
        public int BeginSession() => Interlocked.Increment(ref _sessionEpoch);

        /// <summary>配置**实际生效**后回写 revision（由适配器 ApplyConfig 成功后调用）。</summary>
        public void RecordConfigRevision(int revision) => Volatile.Write(ref _configRevision, revision);

        /// <summary>
        /// 发布一个完整批次（不可变）。BatchId 只在这里递增。
        /// </summary>
        /// <param name="samples">本批次的全部样本（值与质量同批发布）。</param>
        /// <param name="catalogVersion">本批次对应的目录版本。</param>
        /// <param name="duration">本轮真实采集耗时。</param>
        public HardwareSnapshot Publish(IReadOnlyList<MetricSample> samples, long catalogVersion, TimeSpan duration)
        {
            long batchId = Interlocked.Increment(ref _batchId);
            var snapshot = new HardwareSnapshot(
                BatchId: batchId,
                CatalogVersion: catalogVersion,
                ConfigRevision: Volatile.Read(ref _configRevision),
                Samples: samples,
                SamplingDuration: duration,
                TimestampMonoMs: NowMonoMs,
                SessionEpoch: Volatile.Read(ref _sessionEpoch));

            Volatile.Write(ref _missingReason, null);
            // ★ 原子发布：整体替换，读者不会看到半截状态。
            Volatile.Write(ref _current, snapshot);
            return snapshot;
        }

        /// <summary>
        /// 发布"本轮采集失败"的批次：已知 Key 全部标记为 Missing + <see cref="CollectionFailedReason"/>。
        /// <para>★ 失败**不保活**上一批的有效值：通用回退层不得把陈旧读数无限期地当成有效数据。</para>
        /// </summary>
        public HardwareSnapshot PublishFailure(IReadOnlyList<string> keys, long catalogVersion, TimeSpan duration)
        {
            var samples = new List<MetricSample>(keys.Count);
            foreach (string key in keys)
            {
                samples.Add(new MetricSample(
                    Key: key,
                    StableDeviceId: null,
                    Value: null,
                    Unit: null,
                    Quality: SampleQuality.Missing,
                    MissingReason: CollectionFailedReason,
                    SampleAgeMs: 0));
            }

            long batchId = Interlocked.Increment(ref _batchId);
            var snapshot = new HardwareSnapshot(
                BatchId: batchId,
                CatalogVersion: catalogVersion,
                ConfigRevision: Volatile.Read(ref _configRevision),
                Samples: samples,
                SamplingDuration: duration,
                TimestampMonoMs: NowMonoMs,
                SessionEpoch: Volatile.Read(ref _sessionEpoch));

            Volatile.Write(ref _missingReason, CollectionFailedReason);
            Volatile.Write(ref _current, snapshot);
            return snapshot;
        }

        /// <summary>
        /// 初始空批次：只在第一次读取时创建一次并缓存
        /// （重复读取不得改变 BatchId / TimestampMonoMs ——"读取不推进时效字段"）。
        /// </summary>
        private HardwareSnapshot InitializingSnapshot()
        {
            HardwareSnapshot? snapshot = Volatile.Read(ref _initializingSnapshot);
            if (snapshot != null) return snapshot;

            lock (_gate)
            {
                if (_initializingSnapshot == null)
                {
                    _initializingSnapshot = new HardwareSnapshot(
                        BatchId: 0,
                        CatalogVersion: 0,
                        ConfigRevision: Volatile.Read(ref _configRevision),
                        Samples: Array.Empty<MetricSample>(),
                        SamplingDuration: TimeSpan.Zero,
                        TimestampMonoMs: NowMonoMs,
                        SessionEpoch: Volatile.Read(ref _sessionEpoch));
                }
                return _initializingSnapshot;
            }
        }
    }

    /// <summary>
    /// E01-A：快照样本的构造口径（单位 / 质量 / 缺失原因）**单一来源**。
    /// <para>★ 真实 0 是有效值（Good + 0）；没有读到值一律 Missing + 原因，
    /// 绝不用 0 冒充"没读到"。</para>
    /// </summary>
    public static class SnapshotSampleFactory
    {
        /// <summary>
        /// 构造一个样本。
        /// </summary>
        /// <param name="key">外部指标 Key（如 "CPU.Temp" / "GPU.0.Load"）。</param>
        /// <param name="value">读数；null / NaN / Infinity 视为"没有值"。</param>
        /// <param name="stableDeviceId">稳定设备身份（与目录 StableId 同口径）；可为 null。</param>
        /// <param name="sampleAgeMs">发布时刻 - 样本时刻（毫秒）。</param>
        /// <param name="missingReason">缺失原因；缺省用 <see cref="SnapshotPublisher.NoValueReason"/>。</param>
        public static MetricSample Create(
            string key,
            float? value,
            string? stableDeviceId,
            double sampleAgeMs,
            string? missingReason = null)
        {
            bool hasValue = value.HasValue
                            && !float.IsNaN(value.Value)
                            && !float.IsInfinity(value.Value);

            if (!hasValue)
            {
                return new MetricSample(
                    Key: key,
                    StableDeviceId: stableDeviceId,
                    Value: null,
                    Unit: null,                                  // 缺失不带单位（与显示端口径一致）
                    Quality: SampleQuality.Missing,
                    MissingReason: missingReason ?? SnapshotPublisher.NoValueReason,
                    SampleAgeMs: sampleAgeMs);
            }

            return new MetricSample(
                Key: key,
                StableDeviceId: stableDeviceId,
                Value: value,
                Unit: ResolveUnit(key, value!.Value),
                Quality: SampleQuality.Good,
                MissingReason: null,
                SampleAgeMs: sampleAgeMs);
        }

        /// <summary>
        /// 单位口径：与面板完全一致（<see cref="MetricUtils.UnitContext.Panel"/>）。
        /// <para>★ 单位计算绝不能让发布动作失败：异常时退化为"无单位"，样本本身仍然发布。</para>
        /// </summary>
        private static string? ResolveUnit(string key, float value)
        {
            try
            {
                return MetricUtils.GetUnitStr(key, value, MetricUtils.UnitContext.Panel);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SnapshotPublisher] 单位解析失败 {key}: {ex.Message}");
                return null;
            }
        }
    }
}
