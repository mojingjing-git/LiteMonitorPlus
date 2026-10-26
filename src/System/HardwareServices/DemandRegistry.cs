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
    ///   - 聚合需求 = 各消费者需求的**按位或**（任一消费者要 ⇒ 采集层就要）；
    ///   - 释放一个消费者**不影响**其他消费者（"关闭一个窗口不会清掉其他消费者需求"）；
    ///   - 未登记过的消费者释放 ⇒ 返回 false（不存在可释放的租约）。
    ///
    /// ★ 本类是**纯数据**的（不碰硬件、不起线程），因此可以在没有硬件的环境里被完整断言。
    /// </summary>
    public sealed class DemandRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SamplingDemand> _demands = new Dictionary<string, SamplingDemand>(StringComparer.Ordinal);

        /// <summary>已登记的消费者数量。</summary>
        public int ConsumerCount { get { lock (_gate) { return _demands.Count; } } }

        /// <summary>某消费者是否已登记。</summary>
        public bool Contains(string consumerId)
        {
            if (string.IsNullOrWhiteSpace(consumerId)) return false;
            lock (_gate) { return _demands.ContainsKey(consumerId); }
        }

        /// <summary>
        /// 登记 / 更新某个消费者的需求。
        /// </summary>
        /// <param name="consumerId">消费者身份（空 ⇒ 拒绝）。</param>
        /// <param name="demand">该消费者的需求。</param>
        /// <returns>true 表示登记成功。</returns>
        public bool Submit(string consumerId, SamplingDemand demand)
        {
            if (string.IsNullOrWhiteSpace(consumerId)) return false;
            lock (_gate)
            {
                _demands[consumerId] = demand;
                return true;
            }
        }

        /// <summary>
        /// 释放某个消费者的需求（关闭窗口时调用）。
        /// </summary>
        /// <returns>true 表示确实释放过一份已登记的需求。</returns>
        public bool Release(string consumerId)
        {
            if (string.IsNullOrWhiteSpace(consumerId)) return false;
            lock (_gate) { return _demands.Remove(consumerId); }
        }

        /// <summary>取某个消费者当前登记的需求（未登记 ⇒ null）。</summary>
        public SamplingDemand? Get(string consumerId)
        {
            if (string.IsNullOrWhiteSpace(consumerId)) return null;
            lock (_gate)
            {
                if (_demands.TryGetValue(consumerId, out SamplingDemand? demand)) return demand;
                return null;
            }
        }

        /// <summary>
        /// 聚合需求：所有已登记消费者需求的按位或。
        /// <para>★ 未登记任何消费者 ⇒ 全部为 false（"没有需求就不要采样"）。</para>
        /// </summary>
        public SamplingDemand Aggregate
        {
            get
            {
                lock (_gate)
                {
                    bool ui = false, taskbar = false, web = false, history = false, sensor = false;
                    foreach (SamplingDemand d in _demands.Values)
                    {
                        ui |= d.Ui;
                        taskbar |= d.Taskbar;
                        web |= d.Web;
                        history |= d.History;
                        sensor |= d.SensorWindow;
                    }
                    return new SamplingDemand(ui, taskbar, web, history, sensor);
                }
            }
        }

        /// <summary>当前登记的消费者 → 需求（副本）。</summary>
        public IReadOnlyDictionary<string, SamplingDemand> Snapshot()
        {
            lock (_gate)
            {
                return new Dictionary<string, SamplingDemand>(_demands, StringComparer.Ordinal);
            }
        }
    }
}
