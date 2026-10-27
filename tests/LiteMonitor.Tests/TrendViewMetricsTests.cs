using System;
using LiteMonitor.src.Core.Trends;
using Xunit;

namespace LiteMonitor.Tests
{
    public sealed class TrendViewMetricsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 27, 12, 0, 0);

        [Fact]
        public void Empty_IsZeroCoverage()
        {
            TrendCoverage coverage = TrendViewMetrics.Calculate(
                Array.Empty<DateTime>(), TimeSpan.FromHours(1), 1, Now);

            Assert.Equal(60, coverage.ExpectedBuckets);
            Assert.Equal(0, coverage.ObservedBuckets);
            Assert.Equal(0, coverage.Ratio);
        }

        [Fact]
        public void SameBucket_CountsOnce()
        {
            TrendCoverage coverage = TrendViewMetrics.Calculate(
                new[]
                {
                    Now.AddMinutes(-30),
                    Now.AddMinutes(-30).AddSeconds(10),
                    Now.AddMinutes(-30).AddSeconds(50)
                },
                TimeSpan.FromHours(1), 1, Now);

            Assert.Equal(1, coverage.ObservedBuckets);
            Assert.Equal(1d / 60d, coverage.Ratio, 6);
        }

        [Fact]
        public void FullCoverage_IsOne()
        {
            var times = new DateTime[60];
            for (int i = 0; i < times.Length; i++) times[i] = Now.AddMinutes(-i);

            TrendCoverage coverage = TrendViewMetrics.Calculate(
                times, TimeSpan.FromHours(1), 1, Now);

            Assert.Equal(60, coverage.ObservedBuckets);
            Assert.Equal(1d, coverage.Ratio);
        }

        [Fact]
        public void OutOfRange_IsIgnored()
        {
            TrendCoverage coverage = TrendViewMetrics.Calculate(
                new[] { Now.AddHours(-2), Now.AddMinutes(-1) },
                TimeSpan.FromHours(1), 5, Now);

            Assert.Equal(12, coverage.ExpectedBuckets);
            Assert.Equal(1, coverage.ObservedBuckets);
        }

        [Fact]
        public void InvalidBucket_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TrendViewMetrics.Calculate(Array.Empty<DateTime>(), TimeSpan.FromHours(1), 0, Now));
        }
    }
}
