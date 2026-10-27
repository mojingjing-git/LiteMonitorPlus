using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.src.Core.Trends
{
    /// <summary>T04：趋势窗口的分辨率/覆盖率只读计算，不依赖 WinForms。</summary>
    public sealed record TrendCoverage(
        TimeSpan Range,
        int BucketMinutes,
        int ExpectedBuckets,
        int ObservedBuckets,
        double Ratio);

    public static class TrendViewMetrics
    {
        /// <summary>
        /// 按当前范围与桶宽计算覆盖率。时间点先截断到桶边界，避免同一桶内多个样本重复计数。
        /// 空数据返回 0；覆盖率上限为 1，超出范围的旧点不计入。
        /// </summary>
        public static TrendCoverage Calculate(
            IEnumerable<DateTime> times,
            TimeSpan range,
            int bucketMinutes,
            DateTime? now = null)
        {
            ArgumentNullException.ThrowIfNull(times);
            if (bucketMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(bucketMinutes));

            int expected = Math.Max(1, (int)Math.Ceiling(range.TotalMinutes / bucketMinutes));
            DateTime end = now ?? DateTime.Now;
            DateTime start = end - range;

            var observed = new HashSet<DateTime>();
            foreach (DateTime time in times)
            {
                if (time < start || time > end) continue;
                DateTime bucket = new DateTime(
                    time.Year, time.Month, time.Day, time.Hour,
                    (time.Minute / bucketMinutes) * bucketMinutes, 0);
                observed.Add(bucket);
            }

            int count = Math.Min(expected, observed.Count);
            double ratio = expected == 0 ? 0 : count / (double)expected;
            return new TrendCoverage(range, bucketMinutes, expected, count, ratio);
        }
    }
}
