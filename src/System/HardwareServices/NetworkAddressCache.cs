using System;
using System.Threading;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// IP 地址读取结果的状态。
    /// <para>★ "缺失"与"读取失败"必须能区分（0 是有效数据原则的地址版）：
    ///  - <see cref="Ok"/>：读到了一个真实地址；
    ///  - <see cref="Missing"/>：网卡在，但**没有 IPv4 地址**（未连接 / 仅 IPv6）—— 这是**有效事实**；
    ///  - <see cref="Failed"/>：读取过程本身失败（枚举网卡出错等）—— 这是**故障**。
    /// 两者都**不允许**用 "0.0.0.0" 之类的占位地址冒充。</para>
    /// </summary>
    public enum NetworkAddressStatus
    {
        /// <summary>读到了真实地址。</summary>
        Ok = 0,

        /// <summary>网卡在，但没有 IPv4 地址（未连接 / 仅 IPv6）。有效事实，不是故障。</summary>
        Missing = 1,

        /// <summary>读取过程失败（枚举网卡抛异常等）。是故障，不是"没有地址"。</summary>
        Failed = 2
    }

    /// <summary>
    /// IP 地址读取结果（值 + 状态）。
    /// <para>★ 状态与值必须分开看：<see cref="Address"/> 为空串表示"没有地址"，
    /// 但"没有地址"（未连接）和"读取失败"（枚举出错）是**不同的状态**。</para>
    /// </summary>
    public readonly struct NetworkAddressResult
    {
        public NetworkAddressResult(string address, NetworkAddressStatus status, bool isRetained = false)
        {
            Address = address ?? "";
            Status = status;
            IsRetained = isRetained;
        }

        /// <summary>地址文本（没有则空串；**绝不**是 "0.0.0.0" 这种占位）。</summary>
        public string Address { get; }

        /// <summary>本次读取的状态。</summary>
        public NetworkAddressStatus Status { get; }

        /// <summary>本次没读到，<see cref="Address"/> 是"上次已知值"（仅作过渡展示）。</summary>
        public bool IsRetained { get; }

        /// <summary>是否带有一个可用于展示的地址文本。</summary>
        public bool HasAddress => Address.Length > 0;

        public static NetworkAddressResult Ok(string address) => new NetworkAddressResult(address, NetworkAddressStatus.Ok);

        public static NetworkAddressResult Missing => new NetworkAddressResult("", NetworkAddressStatus.Missing);

        public static NetworkAddressResult Failed => new NetworkAddressResult("", NetworkAddressStatus.Failed);
    }

    /// <summary>
    /// IP 地址缓存：**值 + 网络环境代次**。
    /// <para>★ 值是**进程级共享**的（切换主题 / 重启服务时不丢失，避免闪烁），
    /// 因此它的失效代次也必须同样共享 —— 否则新建的 <see cref="NetworkManager"/>
    /// 会把"已被网络变更事件作废的旧 IP"当成有效值继续显示（这正是 #96 关闭条件里
    /// "缓存过期与实际网络切换/恢复通过"要堵的洞）。</para>
    ///
    /// <para>★ 失效是**事件驱动**的：网络变更事件到达 → 推进代次 → 旧值立即作废。
    /// 时间窗只是"事件丢失"时的兜底（见 <see cref="NetworkRefreshGate"/>）。</para>
    /// </summary>
    public sealed class NetworkAddressCache
    {
        /// <summary>默认的代次推进去抖间隔（秒）：一次物理网络变更往往连发十几个事件。</summary>
        public const double DefaultMinAdvanceIntervalSeconds = 1.0;

        private readonly object _gate = new object();
        private readonly TimeProvider _time;
        private readonly double _minAdvanceIntervalSeconds;

        private string _value = "";
        private long _valueGeneration = -1;
        private long _generation;
        private long _lastAdvanceTimestamp;
        private bool _hasAdvance;

        public NetworkAddressCache(TimeProvider? timeProvider = null, double minAdvanceIntervalSeconds = DefaultMinAdvanceIntervalSeconds)
        {
            _time = timeProvider ?? TimeProvider.System;
            _minAdvanceIntervalSeconds = minAdvanceIntervalSeconds >= 0
                ? minAdvanceIntervalSeconds
                : DefaultMinAdvanceIntervalSeconds;
        }

        /// <summary>
        /// 进程级共享实例（生产环境用）。
        /// <para>测试请注入**独立实例**，避免用例之间通过共享缓存互相污染。</para>
        /// </summary>
        public static NetworkAddressCache Shared { get; } = new NetworkAddressCache();

        /// <summary>当前网络环境代次（网络变更事件推进）。</summary>
        public long Generation
        {
            get { lock (_gate) { return _generation; } }
        }

        /// <summary>
        /// 网络变更事件到达：推进代次，旧值**立即作废**。
        /// <para>★ **去抖**：最短间隔内的重复事件只算一次 —— 一次物理网络变更
        /// （插拔 / 切换）往往连发十几个事件，不去抖就会变成"重取风暴"。</para>
        /// </summary>
        /// <returns>推进后的代次（被去抖时返回当前代次）。</returns>
        public long AdvanceGeneration()
        {
            long now = _time.GetTimestamp();
            lock (_gate)
            {
                if (_hasAdvance &&
                    _time.GetElapsedTime(_lastAdvanceTimestamp, now).TotalSeconds < _minAdvanceIntervalSeconds)
                {
                    return _generation;   // 去抖：同一波事件只推进一次
                }

                _lastAdvanceTimestamp = now;
                _hasAdvance = true;
                _generation++;
                return _generation;
            }
        }

        /// <summary>缓存值是否仍然有效（未被网络变更作废）。</summary>
        public bool HasValidValue
        {
            get { lock (_gate) { return _value.Length > 0 && _valueGeneration == _generation; } }
        }

        /// <summary>取回仍然有效的缓存值（无效返回空串）。</summary>
        public string GetValidValue()
        {
            lock (_gate) { return _valueGeneration == _generation ? _value : ""; }
        }

        /// <summary>取回缓存值（**不**校验代次；用于"上次已知值"的过渡展示）。</summary>
        public string PeekValue()
        {
            lock (_gate) { return _value; }
        }

        /// <summary>写入缓存值（记录当前代次）。空串不会被写入。</summary>
        public void Store(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_gate)
            {
                _value = value;
                _valueGeneration = _generation;
            }
        }

        /// <summary>清空缓存。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _value = "";
                _valueGeneration = -1;
            }
        }
    }

    /// <summary>
    /// IP 地址刷新闸门：**事件驱动优先 + 时间窗兜底**，且**有界**。
    ///
    /// <para>语义：
    ///  - <b>事件驱动</b>：网络变更事件把缓存值作废后，下一次读取**立刻**允许重新取值
    ///    （不再等满 30 秒 —— 这是 #96 关闭条件"实际网络切换/恢复通过"的核心）；
    ///  - <b>时间窗兜底</b>：事件丢失时，距上次成功取值超过窗口（默认 30 秒）同样允许重新取值；
    ///  - <b>有界</b>：每一轮最多 <see cref="DefaultMaxAttemptsPerRound"/> 次真正取值，
    ///    两次尝试之间至少间隔 <see cref="DefaultRetryIntervalSeconds"/> 秒 ——
    ///    事件风暴不会把网卡枚举频率拉爆。</para>
    ///
    /// <para>★ 时钟一律走 <see cref="TimeProvider"/>（可注入），测试里禁止真实等待。</para>
    /// </summary>
    public sealed class NetworkRefreshGate
    {
        /// <summary>时间窗（秒）：事件丢失时的兜底重取间隔。</summary>
        public const double DefaultWindowSeconds = 30.0;

        /// <summary>单轮最多的真正取值次数（单次事件触发的重试上限）。</summary>
        public const int DefaultMaxAttemptsPerRound = 3;

        /// <summary>两次真正取值之间的最短间隔（秒）：事件风暴去抖。</summary>
        public const double DefaultRetryIntervalSeconds = 1.0;

        private readonly TimeProvider _time;
        private readonly double _windowSeconds;
        private readonly int _maxAttemptsPerRound;
        private readonly double _retryIntervalSeconds;

        private long _lastSuccessTimestamp;
        private bool _hasSuccess;
        private long _lastAttemptTimestamp;
        private bool _hasAttempt;
        private long _roundStamp;
        private bool _hasRound;
        private int _attemptsInRound;
        private long _observedGeneration;
        private bool _hasObservedGeneration;

        public NetworkRefreshGate(
            TimeProvider? timeProvider = null,
            double windowSeconds = DefaultWindowSeconds,
            int maxAttemptsPerRound = DefaultMaxAttemptsPerRound,
            double retryIntervalSeconds = DefaultRetryIntervalSeconds)
        {
            _time = timeProvider ?? TimeProvider.System;
            _windowSeconds = windowSeconds > 0 ? windowSeconds : DefaultWindowSeconds;
            _maxAttemptsPerRound = maxAttemptsPerRound > 0 ? maxAttemptsPerRound : DefaultMaxAttemptsPerRound;
            _retryIntervalSeconds = retryIntervalSeconds >= 0 ? retryIntervalSeconds : DefaultRetryIntervalSeconds;
        }

        /// <summary>时间窗是否已到期（兜底路径的判据）。</summary>
        public bool IsWindowExpired
        {
            get
            {
                if (!_hasSuccess) return true;
                return _time.GetElapsedTime(_lastSuccessTimestamp, _time.GetTimestamp()).TotalSeconds >= _windowSeconds;
            }
        }

        /// <summary>当前这一轮已经用掉的取值次数。</summary>
        public int AttemptsInRound => _attemptsInRound;

        /// <summary>
        /// 是否应当现在真的去读一次网卡。
        /// </summary>
        /// <param name="cacheValid">
        /// 缓存值是否仍然有效（没有被网络变更事件作废）。
        /// <c>false</c> = <b>事件驱动</b>路径（立即重取，不等时间窗）；
        /// <c>true</c> = 只能走 <b>时间窗兜底</b>路径。
        /// </param>
        /// <param name="generation">当前网络环境代次（事件推进；用于识别"新的失效信号"）。</param>
        public bool ShouldRefresh(bool cacheValid, long generation)
        {
            long now = _time.GetTimestamp();

            bool newSignal = !_hasObservedGeneration || generation != _observedGeneration;
            if (newSignal)
            {
                _observedGeneration = generation;
                _hasObservedGeneration = true;
                // 新的失效信号 → 开启新一轮（预算重置），否则旧预算会把新事件"卡死"。
                OpenRound();
            }

            if (!cacheValid)
            {
                // ★ 事件驱动：缓存已被网络变更作废 → 立即重取。
                if (newSignal) return Consume(now);
                // 同一信号的后续读取：受最短重试间隔约束（事件风暴去抖）。
                if (_hasAttempt &&
                    _time.GetElapsedTime(_lastAttemptTimestamp, now).TotalSeconds < _retryIntervalSeconds)
                {
                    return false;
                }
                return Consume(now);
            }

            // ★ 时间窗兜底：事件丢失时，这是唯一的出路。
            if (!IsWindowExpired) return false;
            TryOpenWindowRound();
            return Consume(now);
        }

        /// <summary>取值成功后调用：刷新窗口起点，本轮结束（预算归零）。</summary>
        public void MarkRefreshSucceeded()
        {
            _lastSuccessTimestamp = _time.GetTimestamp();
            _hasSuccess = true;
            _attemptsInRound = 0;
        }

        private void OpenRound()
        {
            _attemptsInRound = 0;
            _roundStamp = _lastSuccessTimestamp;
            _hasRound = true;
        }

        /// <summary>时间窗到期时开启新一轮（同一个窗口只开一次，避免"每读一次就重置预算"）。</summary>
        private void TryOpenWindowRound()
        {
            if (!IsWindowExpired) return;
            if (_hasRound && _roundStamp == _lastSuccessTimestamp) return;
            OpenRound();
        }

        private bool Consume(long now)
        {
            if (_attemptsInRound >= _maxAttemptsPerRound) return false;
            _attemptsInRound++;
            _lastAttemptTimestamp = now;
            _hasAttempt = true;
            return true;
        }
    }
}
