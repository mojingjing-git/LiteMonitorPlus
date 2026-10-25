using System;
using System.Collections.Generic;
using System.Threading;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 网络样本的质量等级。
    /// <para>★ "缺失 / 失败 / 过期"各有状态：
    ///  - <see cref="Fresh"/>：本次读取**成功**拿到了新样本（0 也是有效样本，不是缺失）；
    ///  - <see cref="Stale"/>：本次读取**失败**，返回的是保留期内的旧样本；
    ///  - <see cref="Missing"/>：没有可用样本（从未成功采样，或旧值已超过保留期）。</para>
    /// </summary>
    public enum NetworkSampleQuality
    {
        /// <summary>本次读取成功拿到了新样本。</summary>
        Fresh = 0,

        /// <summary>本次读取失败，返回的是保留期内的旧样本。</summary>
        Stale = 1,

        /// <summary>没有可用样本（从未成功采样，或已超过保留期）。</summary>
        Missing = 2
    }

    /// <summary>
    /// 一个网络样本：**值 + 设备身份 + 设备代次 + 成功采样时间 + 质量**。
    /// </summary>
    public readonly struct NetworkSample
    {
        public NetworkSample(float value, string deviceIdentity, long deviceGeneration, long succeededTimestamp, NetworkSampleQuality quality)
        {
            Value = value;
            DeviceIdentity = deviceIdentity ?? "";
            DeviceGeneration = deviceGeneration;
            SucceededTimestamp = succeededTimestamp;
            Quality = quality;
        }

        /// <summary>样本值（单位与 LHM Throughput 传感器一致，bytes/sec）。</summary>
        public float Value { get; }

        /// <summary>设备身份（网卡名）。用于判断"旧值属于哪台设备"。</summary>
        public string DeviceIdentity { get; }

        /// <summary>设备代次：设备删除 / 首选变化 / 硬件重载时 +1。</summary>
        public long DeviceGeneration { get; }

        /// <summary>最近一次**成功采样**的单调时间戳。读缓存**不会**刷新它。</summary>
        public long SucceededTimestamp { get; }

        /// <summary>样本质量。</summary>
        public NetworkSampleQuality Quality { get; }

        /// <summary>是否携带可用数值。<see cref="NetworkSampleQuality.Missing"/> 时为 false（Fresh 的 0 是有效值）。</summary>
        public bool HasValue => Quality != NetworkSampleQuality.Missing;

        public static NetworkSample Fresh(float value, string deviceIdentity, long deviceGeneration, long succeededTimestamp)
            => new NetworkSample(value, deviceIdentity, deviceGeneration, succeededTimestamp, NetworkSampleQuality.Fresh);

        public static NetworkSample Stale(float value, string deviceIdentity, long deviceGeneration, long succeededTimestamp)
            => new NetworkSample(value, deviceIdentity, deviceGeneration, succeededTimestamp, NetworkSampleQuality.Stale);

        public static NetworkSample Missing => new NetworkSample(0f, "", 0, 0, NetworkSampleQuality.Missing);
    }

    /// <summary>
    /// 网络样本的时效策略：**阈值集中定义在这里**，时钟用 <see cref="TimeProvider"/>（可注入，测试不依赖真实等待）。
    /// <para>初始策略（计划 F03 第 3 步）：最多保留最后有效样本 <b>3 个预定采样周期</b>，上限 <b>15 秒</b>；
    /// 保留期内标 <see cref="NetworkSampleQuality.Stale"/>，到期后为 <see cref="NetworkSampleQuality.Missing"/>。</para>
    /// </summary>
    public sealed class NetworkSampleFreshnessPolicy
    {
        /// <summary>最多保留"最后有效样本"的采样周期数。</summary>
        public const int DefaultMaxSampleCycles = 3;

        /// <summary>保留期上限（秒）：采样周期再长也不会超过它。</summary>
        public const double DefaultMaxRetentionSeconds = 15.0;

        public NetworkSampleFreshnessPolicy(
            TimeProvider? timeProvider = null,
            int maxSampleCycles = DefaultMaxSampleCycles,
            double maxRetentionSeconds = DefaultMaxRetentionSeconds)
        {
            Time = timeProvider ?? TimeProvider.System;
            MaxSampleCycles = maxSampleCycles > 0 ? maxSampleCycles : DefaultMaxSampleCycles;
            MaxRetentionSeconds = maxRetentionSeconds > 0 ? maxRetentionSeconds : DefaultMaxRetentionSeconds;
            SampleCycleSeconds = 1.0;
        }

        /// <summary>单调时钟（可注入）。</summary>
        public TimeProvider Time { get; }

        /// <summary>保留的采样周期数。</summary>
        public int MaxSampleCycles { get; }

        /// <summary>保留期上限（秒）。</summary>
        public double MaxRetentionSeconds { get; }

        /// <summary>当前**预定采样周期**（秒）。默认 1 秒，由采集层按配置刷新周期推导。</summary>
        public double SampleCycleSeconds { get; set; }

        /// <summary>保留窗口（秒）= min(上限, 周期数 × 采样周期)。</summary>
        public double RetentionSeconds =>
            Math.Min(MaxRetentionSeconds, MaxSampleCycles * Math.Max(0.05, SampleCycleSeconds));

        /// <summary>保留窗口（单调时间戳刻度）。</summary>
        public long RetentionTicks => (long)(RetentionSeconds * Time.TimestampFrequency);

        /// <summary>
        /// 按配置刷新周期推导"预定采样周期"（秒）。
        /// <para>规则与采集层一致：普通硬件最快每秒一次（<see cref="HardwareSamplingCoordinator.MinimumPeriodMs"/>），
        /// 慢刷新用户保留自己的慢周期。</para>
        /// </summary>
        public static double ResolveSampleCycleSeconds(int refreshMs)
        {
            int period = refreshMs > 0 ? refreshMs : HardwareSamplingCoordinator.MinimumPeriodMs;
            if (period < HardwareSamplingCoordinator.MinimumPeriodMs) period = HardwareSamplingCoordinator.MinimumPeriodMs;
            return period / 1000.0;
        }
    }

    /// <summary>
    /// 网络样本登记处：记录每个 Key 最近一次**成功采样**的值、成功时间、设备身份与设备代次。
    ///
    /// <para>★ 核心约束（计划 F03 第 1、3 步）：
    ///  - <b>只有"成功采样"才刷新寿命</b>：UI 读、快照读、fallback 读都**不刷新**样本寿命；
    ///  - <b>样本轮次（epoch）</b>由采集层推进：网卡真的被 <c>hw.Update()</c> 过一轮，才算"新样本"，
    ///    否则反复读取同一个传感器值会把旧值一直当成新样本（这正是 #96 的冻结机制）；
    ///  - 设备删除 / 首选变化 / 硬件重载时立即作废（旧值不得再冒充新样本发布）；
    ///  - 超过保留期 -> <see cref="NetworkSampleQuality.Missing"/>。</para>
    /// </summary>
    public sealed class NetworkSampleRegistry
    {
        private sealed class Entry
        {
            public float Value;
            public long Timestamp;
            public string DeviceIdentity = "";
            public long DeviceGeneration;
            public long Epoch;
            /// <summary>由"从未登记过的旧值"一次性采纳而来（不是一次成功采样）。</summary>
            public bool Legacy;
            /// <summary>已作废（设备删除 / 首选变化 / 硬件重载）。</summary>
            public bool Invalidated;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly NetworkSampleFreshnessPolicy _policy;
        private long _epoch;

        public NetworkSampleRegistry(NetworkSampleFreshnessPolicy? policy = null)
        {
            _policy = policy ?? new NetworkSampleFreshnessPolicy();
        }

        public NetworkSampleFreshnessPolicy Policy => _policy;

        /// <summary>当前样本轮次（epoch）。只有采集层推进它。</summary>
        public long SampleEpoch => Interlocked.Read(ref _epoch);

        /// <summary>
        /// 推进样本轮次：网卡真的完成了一次 <c>hw.Update()</c>（即真的产生了一份新样本）时调用。
        /// </summary>
        public void AdvanceSampleEpoch() => Interlocked.Increment(ref _epoch);

        /// <summary>
        /// 登记一次**成功采样**。
        /// <para>★ 只有在"样本轮次变了"（真的采到了新数据）或"首次登记"时才刷新成功时间；
        /// 同一轮次内反复读取**不会**刷新寿命 —— 这是"读缓存不刷新数据寿命"的落点。</para>
        /// </summary>
        /// <returns>登记后的样本（<see cref="NetworkSampleQuality.Fresh"/>）。</returns>
        public NetworkSample MarkSampled(string key, float value, string deviceIdentity, long deviceGeneration)
        {
            if (string.IsNullOrEmpty(key)) return NetworkSample.Missing;

            long now = _policy.Time.GetTimestamp();
            long epoch = SampleEpoch;

            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out Entry? entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }
                else if (entry.Epoch == epoch && !entry.Invalidated)
                {
                    // 同一轮次内的重复读：只更新值，**不刷新寿命**。
                    entry.Value = value;
                    // ★ 采样轮次没推进 => 采集层已经很久没更新这块网卡了。
                    //   传感器里那个值不能再当成"新样本"发布，否则就是 #96 的冻结回退。
                    if (IsExpired(entry.Timestamp, now)) return NetworkSample.Missing;
                    return NetworkSample.Fresh(entry.Value, entry.DeviceIdentity, entry.DeviceGeneration, entry.Timestamp);
                }

                entry.Value = value;
                entry.Timestamp = now;
                entry.DeviceIdentity = deviceIdentity ?? "";
                entry.DeviceGeneration = deviceGeneration;
                entry.Epoch = epoch;
                entry.Legacy = false;
                entry.Invalidated = false;
                return NetworkSample.Fresh(entry.Value, entry.DeviceIdentity, entry.DeviceGeneration, entry.Timestamp);
            }
        }

        /// <summary>该样本是否已超过保留期。</summary>
        private bool IsExpired(long timestamp, long now)
        {
            long age = now - timestamp;
            if (age < 0) age = 0;
            return age >= _policy.RetentionTicks;
        }

        /// <summary>
        /// 一次性采纳"从未登记过"的旧值（例如进程内其它路径写下的最后有效值）。
        /// <para>★ 已经登记过的 Key（含已作废的）**不会**被再次采纳 —— 否则旧值会被无限期捡回来，
        /// 这正是 #96 的"冻结回退永不失效"。</para>
        /// </summary>
        /// <returns>是否真的采纳了。</returns>
        public bool AdoptLegacy(string key, float value, string? deviceIdentity, long deviceGeneration)
        {
            if (string.IsNullOrEmpty(key)) return false;

            lock (_gate)
            {
                if (_entries.ContainsKey(key)) return false;
                _entries[key] = new Entry
                {
                    Value = value,
                    Timestamp = _policy.Time.GetTimestamp(),
                    DeviceIdentity = deviceIdentity ?? "",
                    DeviceGeneration = deviceGeneration,
                    Epoch = SampleEpoch,
                    Legacy = true,
                    Invalidated = false
                };
                return true;
            }
        }

        /// <summary>该 Key 是否登记过样本（含已作废的：作废后不允许再被一次性采纳）。</summary>
        public bool IsRegistered(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_gate) { return _entries.ContainsKey(key); }
        }

        /// <summary>
        /// 取回样本：从未登记 / 已作废 / 超过保留期 -> <see cref="NetworkSampleQuality.Missing"/>；
        /// 仍在保留期内 -> <see cref="NetworkSampleQuality.Stale"/>（旧值，但还没过期）。
        /// </summary>
        public NetworkSample Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return NetworkSample.Missing;

            Entry? entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry)) return NetworkSample.Missing;
            }

            if (entry.Invalidated) return NetworkSample.Missing;

            if (IsExpired(entry.Timestamp, _policy.Time.GetTimestamp())) return NetworkSample.Missing;

            return NetworkSample.Stale(entry.Value, entry.DeviceIdentity, entry.DeviceGeneration, entry.Timestamp);
        }

        /// <summary>
        /// 取回可用样本：优先返回登记处仍在保留期内的样本；
        /// 若该 Key **从未登记过**且调用方手里有旧值，则一次性采纳它（寿命有界）。
        /// </summary>
        public NetworkSample GetOrAdopt(string key, float? legacyValue, string? deviceIdentity, long deviceGeneration)
        {
            NetworkSample sample = Get(key);
            if (sample.HasValue) return sample;

            if (legacyValue.HasValue && !float.IsNaN(legacyValue.Value))
            {
                AdoptLegacy(key, legacyValue.Value, deviceIdentity, deviceGeneration);
                sample = Get(key);
            }
            return sample;
        }

        /// <summary>作废某个设备登记的全部样本（设备删除 / 换卡）。</summary>
        public void InvalidateDevice(string deviceIdentity)
        {
            if (string.IsNullOrEmpty(deviceIdentity)) return;
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    if (string.Equals(entry.DeviceIdentity, deviceIdentity, StringComparison.Ordinal))
                    {
                        entry.Invalidated = true;
                    }
                }
            }
        }

        /// <summary>作废全部样本（硬件重载 / 首选设备变化 / 关闭）。</summary>
        public void InvalidateAll()
        {
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.Invalidated = true;
                }
            }
        }
    }
}
