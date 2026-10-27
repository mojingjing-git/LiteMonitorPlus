using System;
using System.Collections.Generic;
using LiteMonitor.Contracts;

namespace LiteMonitor.SystemServices
{
    /// <summary>
    /// E01-B：按**消费者身份**登记的采样需求租约表。
    ///
    /// ★ 语义（完善计划 §5 E01-B"需求按消费者身份登记并返回可释放的订阅/租约"）：
    ///   - 每个消费者一个槽位；同消费者重复登记 = 更新自己的需求（**不覆盖**其他消费者）；
    ///   - 聚合需求 = 各活跃消费者需求的**按位或**（任一消费者要 ⇒ 采集层就要）；
    ///   - 释放一个消费者**不影响**其他消费者（"关闭一个窗口不会清掉其他消费者需求"）；
    ///   - 未登记过的消费者释放 ⇒ 返回 false（不存在可释放的租约）。
    ///
    /// ★ C3：内部存 <see cref="ConsumerSamplingDemand"/>，显式保留每消费者的
    ///   <c>MaxIntervalMs</c>/<c>Degradable</c>；旧 <see cref="SamplingDemand"/> 只作为兼容入口。
    /// ★ 本类是**纯数据**的（不碰硬件、不起线程），因此可以在没有硬件的环境里被完整断言。
    /// </summary>
    public sealed class DemandRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ConsumerSamplingDemand> _demands =
            new Dictionary<string, ConsumerSamplingDemand>(StringComparer.Ordinal);

        /// <summary>已登记的消费者数量。</summary>
        public int ConsumerCount { get { lock (_gate) { return _demands.Count; } } }

        /// <summary>某消费者是否已登记。</summary>
        public bool Contains(string consumerId)
        {
            if (string.IsNullOrWhiteSpace(consumerId)) return false;
            lock (_gate) { return _demands.ContainsKey(consumerId); }
        }

        /// <summary>[兼容入口] 用旧五布尔需求登记；等价于 Active=true、MaxIntervalMs=0、Degradable=false。</summary>
        public bool Submit(string consumerId, SamplingDemand demand)
        {
            if (string.IsNullOrWhiteSpace(consumerId) || demand is null) return false;
            return Submit(new ConsumerSamplingDemand(
                consumerId,
                true,
                0,
                false,
                "legacy",
                ConsumerSamplingDemand.FromLegacy(demand)));
        }

        /// <summary>C3 主入口：登记 / 更新某个消费者的显式需求；空 consumerId 或负间隔拒绝。</summary>
        public bool Submit(ConsumerSamplingDemand demand)
        {
            if (demand is null) return false;
            if (string.IsNullOrWhiteSpace(demand.ConsumerId)) return false;
            if (demand.MaxIntervalMs < 0) return false;

            lock (_gate)
            {
                _demands[demand.ConsumerId] = demand with { Reason = demand.Reason ?? string.Empty };
                return true;
            }
        }

        /// <summary>释放某个消费者的需求（关闭窗口时调用）。</summary>
        public bool Release(string consumerId)
        {
            if (string.IsNullOrWhiteSpace(consumerId)) return false;
            lock (_gate) { return _demands.Remove(consumerId); }
        }

        /// <summary>取某个消费者当前的旧式需求（未登记 ⇒ null）。</summary>
        public SamplingDemand? Get(string consumerId)
            => GetConsumer(consumerId)?.ToLegacyDemand();

        /// <summary>取某个消费者当前的显式需求（未登记 ⇒ null）。</summary>
        public ConsumerSamplingDemand? GetConsumer(string consumerId)
        {
            if (string.IsNullOrWhiteSpace(consumerId)) return null;
            lock (_gate)
            {
                if (_demands.TryGetValue(consumerId, out ConsumerSamplingDemand? demand)) return demand;
                return null;
            }
        }

        /// <summary>
        /// 聚合通道需求：所有 **Active** 消费者通道的按位或。
        /// <para>★ 未登记任何活跃消费者 ⇒ 全部为 false（"没有需求就不要采样"）。</para>
        /// </summary>
        public SamplingDemand Aggregate
        {
            get
            {
                lock (_gate)
                {
                    bool ui = false, taskbar = false, web = false, history = false, sensor = false;
                    foreach (ConsumerSamplingDemand d in _demands.Values)
                    {
                        if (!d.Active) continue;
                        SamplingDemand legacy = d.ToLegacyDemand();
                        ui |= legacy.Ui;
                        taskbar |= legacy.Taskbar;
                        web |= legacy.Web;
                        history |= legacy.History;
                        sensor |= legacy.SensorWindow;
                    }
                    return new SamplingDemand(ui, taskbar, web, history, sensor);
                }
            }
        }

        /// <summary>
        /// 聚合最大允许间隔：所有 Active 消费者中最小正值；0=未声明被忽略，正值下限 1000ms。
        /// 无声明 ⇒ 0（沿用当前周期）。不可降级消费者通过 <see cref="HasNonDegradable"/> 暴露。
        /// </summary>
        public int AggregateMaxIntervalMs
        {
            get
            {
                lock (_gate)
                {
                    int min = 0;
                    foreach (ConsumerSamplingDemand d in _demands.Values)
                    {
                        if (!d.Active || d.MaxIntervalMs <= 0) continue;
                        int interval = Math.Max(1000, d.MaxIntervalMs);
                        if (min == 0 || interval < min) min = interval;
                    }
                    return min;
                }
            }
        }

        /// <summary>是否存在 Active 且不可降级的消费者；P04 用它拒绝把采样降到其请求以下。</summary>
        public bool HasNonDegradable
        {
            get
            {
                lock (_gate)
                {
                    foreach (ConsumerSamplingDemand d in _demands.Values)
                        if (d.Active && !d.Degradable) return true;
                    return false;
                }
            }
        }

        /// <summary>当前登记的消费者 → 旧式需求（副本，兼容旧诊断/测试）。</summary>
        public IReadOnlyDictionary<string, SamplingDemand> Snapshot()
        {
            lock (_gate)
            {
                var result = new Dictionary<string, SamplingDemand>(StringComparer.Ordinal);
                foreach (var pair in _demands)
                    result[pair.Key] = pair.Value.ToLegacyDemand();
                return result;
            }
        }

        /// <summary>当前登记的消费者 → 显式需求（副本，C3/P04 使用）。</summary>
        public IReadOnlyDictionary<string, ConsumerSamplingDemand> SnapshotConsumers()
        {
            lock (_gate)
            {
                return new Dictionary<string, ConsumerSamplingDemand>(_demands, StringComparer.Ordinal);
            }
        }
    }
}
