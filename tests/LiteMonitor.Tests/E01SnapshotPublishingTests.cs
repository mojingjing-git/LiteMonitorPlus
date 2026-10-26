using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LiteMonitor.Contracts;
using LiteMonitor.src.Core;
using LiteMonitor.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// E01-A（生产者发布）：快照的**发布边界**断言。
    /// <para>覆盖：批次号只在发布时递增（读取不推进）、初始空批次不伪造成功、
    /// 真实 0 = Good + 0、无值 = Missing + "no-value"、采集失败不保活上一批、
    /// 会话纪元只在采集器重建时递增、配置 revision 生效后回写、并行读取不混代次。</para>
    /// </summary>
    public class E01SnapshotPublishingTests
    {
        private static readonly MetricSample[] NoSamples = Array.Empty<MetricSample>();

        [Fact]
        public void A1_未发布前_返回空批次且原因initializing_不伪造成功批次()
        {
            var publisher = new SnapshotPublisher();

            Assert.False(publisher.HasPublished);
            Assert.Equal("initializing", publisher.CurrentMissingReason);

            HardwareSnapshot snapshot = publisher.Current;
            Assert.Empty(snapshot.Samples);          // ★ 空批次
            Assert.Equal(0, snapshot.BatchId);        // ★ 没有批次号
            Assert.Equal(SampleQuality.Missing, SampleQuality.Missing); // 缺失语义由原因承载
            Assert.Equal("initializing", publisher.CurrentMissingReason);
        }

        [Fact]
        public void A2_重复读取同一批次_批次号与时间戳与内容都不变()
        {
            var publisher = new SnapshotPublisher();
            var samples = new[]
            {
                SnapshotSampleFactory.Create("CPU.Temp", 55f, "/amdcpu/0", 0),
            };

            HardwareSnapshot first = publisher.Publish(samples, catalogVersion: 3, TimeSpan.FromMilliseconds(12));
            HardwareSnapshot a = publisher.Current;
            HardwareSnapshot b = publisher.Current;
            HardwareSnapshot c = publisher.Current;

            Assert.Same(first, a);
            Assert.Same(a, b);
            Assert.Same(b, c);
            Assert.Equal(1, publisher.BatchId);
            Assert.Equal(first.TimestampMonoMs, c.TimestampMonoMs);
            Assert.Equal(first.SamplingDuration, c.SamplingDuration);
            Assert.Equal(3, c.CatalogVersion);
        }

        [Fact]
        public void A3_再次发布_批次号递增且旧批次内容不被改写()
        {
            var publisher = new SnapshotPublisher();
            HardwareSnapshot first = publisher.Publish(NoSamples, 1, TimeSpan.Zero);
            HardwareSnapshot second = publisher.Publish(NoSamples, 1, TimeSpan.Zero);

            Assert.Equal(1, first.BatchId);
            Assert.Equal(2, second.BatchId);
            Assert.Equal(2, publisher.BatchId);
            Assert.NotEqual(first.TimestampMonoMs, 0);
        }

        [Fact]
        public void A4_真实0_是Good加0_不是缺失()
        {
            MetricSample sample = SnapshotSampleFactory.Create("CPU.Load", 0f, "/amdcpu/0", 0);

            Assert.Equal(SampleQuality.Good, sample.Quality);
            Assert.Equal(0f, sample.Value);
            Assert.Null(sample.MissingReason);
        }

        [Fact]
        public void A5_无值_是Missing加原因no_value()
        {
            MetricSample sample = SnapshotSampleFactory.Create("CPU.Temp", null, "/amdcpu/0", 0);

            Assert.Equal(SampleQuality.Missing, sample.Quality);
            Assert.Null(sample.Value);
            Assert.Equal("no-value", sample.MissingReason);
            Assert.Null(sample.Unit);   // 缺失不带单位
        }

        [Fact]
        public void A6_单位走面板口径_与MetricUtils一致()
        {
            MetricSample sample = SnapshotSampleFactory.Create("CPU.Temp", 55f, "/amdcpu/0", 0);

            Assert.Equal(
                MetricUtils.GetUnitStr("CPU.Temp", 55f, MetricUtils.UnitContext.Panel),
                sample.Unit);
        }

        [Fact]
        public void A7_采集失败_发布缺失批次_不保活上一批的有效值()
        {
            var publisher = new SnapshotPublisher();
            publisher.Publish(new[]
            {
                SnapshotSampleFactory.Create("CPU.Temp", 55f, "/amdcpu/0", 0),
            }, 1, TimeSpan.Zero);

            HardwareSnapshot failed = publisher.PublishFailure(new[] { "CPU.Temp" }, 1, TimeSpan.Zero);

            Assert.Equal("collection-failed", publisher.CurrentMissingReason);
            Assert.Single(failed.Samples);
            Assert.Equal(SampleQuality.Missing, failed.Samples[0].Quality);
            Assert.Null(failed.Samples[0].Value);
            Assert.Equal("collection-failed", failed.Samples[0].MissingReason);
        }

        [Fact]
        public void A8_会话纪元_只在BeginSession时递增_读取不递增()
        {
            var publisher = new SnapshotPublisher();
            Assert.Equal(0, publisher.SessionEpoch);

            publisher.Publish(NoSamples, 1, TimeSpan.Zero);
            _ = publisher.Current;
            _ = publisher.Current;
            Assert.Equal(0, publisher.SessionEpoch);   // 普通发布 / 读取不递增

            publisher.BeginSession();
            Assert.Equal(1, publisher.SessionEpoch);

            HardwareSnapshot snapshot = publisher.Publish(NoSamples, 1, TimeSpan.Zero);
            Assert.Equal(1, snapshot.SessionEpoch);    // 新批次带上当前纪元
        }

        [Fact]
        public void A9_配置revision_回写后出现在下一批快照里()
        {
            var publisher = new SnapshotPublisher();
            Assert.Equal(-1, publisher.ConfigRevision);

            publisher.RecordConfigRevision(7);
            HardwareSnapshot snapshot = publisher.Publish(NoSamples, 1, TimeSpan.Zero);

            Assert.Equal(7, snapshot.ConfigRevision);
        }

        [Fact]
        public void A10_并行读取同一批次_永远看到同一份内容不混代次()
        {
            var publisher = new SnapshotPublisher();
            publisher.Publish(new[]
            {
                SnapshotSampleFactory.Create("CPU.Temp", 10f, "/amdcpu/0", 0),
            }, 1, TimeSpan.Zero);

            var seen = new System.Collections.Concurrent.ConcurrentBag<HardwareSnapshot>();
            Parallel.For(0, 64, _ =>
            {
                for (int i = 0; i < 200; i++) seen.Add(publisher.Current);
            });

            Assert.All(seen, s =>
            {
                Assert.Equal(1, s.BatchId);
                Assert.Single(s.Samples);
                Assert.Equal(10f, s.Samples[0].Value);
            });
        }

        [Fact]
        public void A11_适配器GetLatestSnapshot_只读已发布批次_重复读内容不变()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            // 尚未发布：适配器返回明确的空批次（不伪造成功）。
            HardwareSnapshot before = adapter.GetLatestSnapshot();
            Assert.Empty(before.Samples);
            Assert.Equal(0, before.BatchId);

            collector.Publisher.Publish(new[]
            {
                SnapshotSampleFactory.Create("CPU.Temp", 42f, "/amdcpu/0", 0),
            }, catalogVersion: 5, TimeSpan.FromMilliseconds(7));

            HardwareSnapshot a = adapter.GetLatestSnapshot();
            HardwareSnapshot b = adapter.GetLatestSnapshot();

            Assert.Equal(1, a.BatchId);
            Assert.Equal(5, a.CatalogVersion);
            Assert.Equal(42f, Assert.Single(a.Samples).Value);
            Assert.Same(a, b);          // ★ 读取不推进批次号 / 不改时效
            Assert.Equal(a.TimestampMonoMs, b.TimestampMonoMs);
        }

        [Fact]
        public void A12_适配器GetLatestSnapshot_读取不改变批次号()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            for (int i = 0; i < 5; i++) collector.Publisher.Publish(NoSamples, 1, TimeSpan.Zero);

            long batchBefore = collector.Publisher.BatchId;
            for (int i = 0; i < 50; i++) _ = adapter.GetLatestSnapshot();

            Assert.Equal(batchBefore, collector.Publisher.BatchId);
            Assert.Equal(5, adapter.GetLatestSnapshot().BatchId);
        }
    }
}
