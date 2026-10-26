using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiteMonitor.Contracts;
using LiteMonitor.src.SystemServices;
using LiteMonitor.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// E01-B（目录与需求）：目录**版本化** + 需求**租约**的断言。
    /// <para>覆盖：结构变化才递增版本、值刷新不重建（版本不动）、旧版本不覆盖新版本、
    /// 目录值按 StableId 从已发布快照刷新、采集失败不保活旧值、
    /// 多消费者需求互不覆盖、释放一个不影响其他、web 桶保持 SetWebDataDemand 语义。</para>
    /// </summary>
    public class E01CatalogDemandTests
    {
        private static HardwareCatalogEntry Device(string stableId, string name = "CPU") =>
            new HardwareCatalogEntry(
                StableId: stableId,
                RuntimeId: stableId,
                ParentStableId: null,
                DeviceType: "Cpu",
                SensorType: null,
                Available: true,
                Name: name);

        private static HardwareCatalogEntry Sensor(string stableId, string parentId, string name = "Tctl") =>
            new HardwareCatalogEntry(
                StableId: stableId,
                RuntimeId: stableId,
                ParentStableId: parentId,
                DeviceType: "Cpu",
                SensorType: "Temperature",
                Available: true,
                Name: name,
                Value: null,
                Max: 90f);

        [Fact]
        public void B1_结构不变_版本不变_值刷新不重建()
        {
            var publisher = new CatalogPublisher();
            var structure = new[] { Device("/amdcpu/0"), Sensor("/amdcpu/0/temperature/0", "/amdcpu/0") };

            Assert.True(publisher.PublishStructure(structure));
            long version = publisher.Version;
            Assert.Equal(1, version);

            // 值变了（同一结构）⇒ 版本不动。
            var refreshed = new[]
            {
                Device("/amdcpu/0"),
                Sensor("/amdcpu/0/temperature/0", "/amdcpu/0") with { Value = 55f },
            };
            Assert.True(publisher.PublishSameVersion(version, refreshed));

            Assert.Equal(version, publisher.Version);
            Assert.Equal(55f, publisher.Current.Entries[1].Value);
        }

        [Fact]
        public void B2_结构变化_版本递增()
        {
            var publisher = new CatalogPublisher();
            publisher.PublishStructure(new[] { Device("/amdcpu/0") });
            long first = publisher.Version;

            publisher.PublishStructure(new[] { Device("/amdcpu/0"), Device("/amdcpu/1", "GPU") });

            Assert.Equal(first + 1, publisher.Version);
        }

        [Fact]
        public void B3_同结构重复发布_版本不变()
        {
            var publisher = new CatalogPublisher();
            var structure = new[] { Device("/amdcpu/0") };

            Assert.True(publisher.PublishStructure(structure));
            long version = publisher.Version;
            Assert.False(publisher.PublishStructure(structure));   // 结构没变
            Assert.Equal(version, publisher.Version);
        }

        [Fact]
        public void B4_旧版本不覆盖新版本()
        {
            var publisher = new CatalogPublisher();
            publisher.PublishStructure(new[] { Device("/amdcpu/0") });
            long current = publisher.Version;

            Assert.False(publisher.PublishSameVersion(current - 1, new[] { Device("/other") }));
            Assert.Equal(current, publisher.Version);
            Assert.Equal("/amdcpu/0", publisher.Current.Entries[0].StableId);
        }

        [Fact]
        public async Task B5_目录值按StableId从已发布快照刷新()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            collector.Catalog.PublishStructure(new[]
            {
                Device("/amdcpu/0"),
                Sensor("/amdcpu/0/temperature/0", "/amdcpu/0"),
            });

            // 快照里的传感器样本按 StableDeviceId 与目录条目对应。
            collector.Publisher.Publish(new[]
            {
                SnapshotSampleFactory.Create("/amdcpu/0/temperature/0", 61.5f, "/amdcpu/0/temperature/0", 0),
            }, catalogVersion: collector.Catalog.Version, TimeSpan.Zero);

            HardwareCatalog catalog = await adapter.GetCatalogAsync(CancellationToken.None);

            Assert.Equal(collector.Catalog.Version, catalog.Version);
            HardwareCatalogEntry sensorEntry = catalog.Entries.Single(e => e.SensorType != null);
            Assert.Equal(61.5f, sensorEntry.Value);
            // 设备条目没有"当前值"。
            Assert.Null(catalog.Entries.Single(e => e.SensorType == null).Value);
        }

        [Fact]
        public async Task B6_采集失败_目录里的传感器值不保活旧值()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            collector.Catalog.PublishStructure(new[]
            {
                Device("/amdcpu/0"),
                Sensor("/amdcpu/0/temperature/0", "/amdcpu/0"),
            });
            collector.Publisher.Publish(new[]
            {
                SnapshotSampleFactory.Create("/amdcpu/0/temperature/0", 61.5f, "/amdcpu/0/temperature/0", 0),
            }, catalogVersion: collector.Catalog.Version, TimeSpan.Zero);

            collector.Publisher.PublishFailure(new[] { "CPU.Temp" }, collector.Catalog.Version, TimeSpan.Zero);

            HardwareCatalog catalog = await adapter.GetCatalogAsync(CancellationToken.None);
            Assert.Null(catalog.Entries.Single(e => e.SensorType != null).Value);
        }

        [Fact]
        public void B7_需求按消费者登记_释放一个不影响其他()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            adapter.SubmitDemand("panel", new SamplingDemand(Ui: true, Taskbar: false, Web: false, History: false, SensorWindow: false));
            adapter.SubmitDemand("sensor-window", new SamplingDemand(Ui: false, Taskbar: false, Web: false, History: false, SensorWindow: true));

            Assert.Equal(2, adapter.DemandConsumerCount);
            Assert.True(adapter.AggregateDemand.Ui);
            Assert.True(adapter.AggregateDemand.SensorWindow);

            Assert.True(adapter.ReleaseDemand("sensor-window"));

            Assert.Equal(1, adapter.DemandConsumerCount);
            Assert.True(adapter.AggregateDemand.Ui);          // ★ 面板需求不受影响
            Assert.False(adapter.AggregateDemand.SensorWindow);
        }

        [Fact]
        public void B8_未登记的消费者释放_返回false()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            Assert.False(adapter.ReleaseDemand("never-registered"));
        }

        [Fact]
        public void B9_同消费者重复登记_只更新自己那一份()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            adapter.SubmitDemand("panel", new SamplingDemand(Ui: true, Taskbar: false, Web: false, History: false, SensorWindow: false));
            adapter.SubmitDemand("taskbar", new SamplingDemand(Ui: false, Taskbar: true, Web: false, History: false, SensorWindow: false));
            adapter.SubmitDemand("panel", new SamplingDemand(Ui: false, Taskbar: false, Web: false, History: false, SensorWindow: false));

            Assert.Equal(2, adapter.DemandConsumerCount);
            Assert.False(adapter.AggregateDemand.Ui);          // 面板改成不要了
            Assert.True(adapter.AggregateDemand.Taskbar);      // 任务栏的还在
        }

        [Fact]
        public void B10_web桶_保持SetWebDataDemand语义()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            adapter.SubmitDemand(HardwareClientAdapter.WebConsumerId,
                new SamplingDemand(Ui: false, Taskbar: false, Web: true, History: false, SensorWindow: false));
            Assert.True(collector.WebDemand);

            Assert.True(adapter.ReleaseDemand(HardwareClientAdapter.WebConsumerId));
            Assert.False(collector.WebDemand);
        }

        [Fact]
        public void B11_legacy桶_不驱动Web需求通道()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            // 兼容桥：旧单桶调用落到 "legacy" 桶。
            adapter.SubmitDemand(new SamplingDemand(Ui: false, Taskbar: false, Web: false, History: false, SensorWindow: true));

            Assert.Equal(1, adapter.DemandConsumerCount);
            Assert.True(adapter.AggregateDemand.SensorWindow);
            Assert.False(collector.WebDemand);   // ★ 不得把 Web 需求误关掉
        }

        [Fact]
        public void B12_需求登记表_纯数据语义可独立断言()
        {
            var registry = new DemandRegistry();

            Assert.True(registry.Submit("a", new SamplingDemand(Ui: true, Taskbar: false, Web: false, History: false, SensorWindow: false)));
            Assert.True(registry.Submit("b", new SamplingDemand(Ui: false, Taskbar: true, Web: false, History: false, SensorWindow: false)));

            SamplingDemand aggregate = registry.Aggregate;
            Assert.True(aggregate.Ui);
            Assert.True(aggregate.Taskbar);
            Assert.False(aggregate.Web);

            Assert.True(registry.Release("a"));
            Assert.False(registry.Release("a"));   // 第二次释放：没有可释放的租约
            Assert.True(registry.Aggregate.Taskbar);
            Assert.Equal(1, registry.ConsumerCount);
        }

        [Fact]
        public async Task B13_多消费者登记需求_并发采样请求只触发一轮真实采集()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);

            adapter.SubmitDemand("panel", new SamplingDemand(Ui: true, Taskbar: false, Web: false, History: false, SensorWindow: false));
            adapter.SubmitDemand("taskbar", new SamplingDemand(Ui: false, Taskbar: true, Web: false, History: false, SensorWindow: false));
            adapter.SubmitDemand("sensor-window", new SamplingDemand(Ui: false, Taskbar: false, Web: false, History: false, SensorWindow: true));

            int rounds = 0;
            using var coordinator = new HardwareSamplingCoordinator(
                periodProvider: () => 1000,
                sampler: _ => { Interlocked.Increment(ref rounds); return Task.CompletedTask; });
            coordinator.SetReady(true);

            await Task.WhenAll(
                coordinator.RequestCollectionAsync(),
                coordinator.RequestCollectionAsync(),
                coordinator.RequestCollectionAsync());

            Assert.Equal(1, rounds);   // ★ 多个消费者的并发请求共享同一轮真实采集
            Assert.Equal(3, adapter.DemandConsumerCount);
        }

        [Fact]
        public void B14_关闭传感器窗口_释放它自己的需求_不影响其他消费者()
        {
            var collector = new E01FakeCollector();
            using var adapter = new HardwareClientAdapter(collector);
            adapter.SubmitDemand("panel", new SamplingDemand(Ui: true, Taskbar: false, Web: false, History: false, SensorWindow: false));

            RunSta(() =>
            {
                using var form = new LiteMonitor.src.UI.HardwareInfoForm(adapter);
                form.Show();
                Application.DoEvents();

                // 打开传感器窗口 ⇒ 它登记了自己的需求租约。
                Assert.Equal(2, adapter.DemandConsumerCount);
                Assert.True(adapter.AggregateDemand.SensorWindow);

                form.Close();
                Application.DoEvents();
            });

            // ★ 关闭传感器窗口只释放它自己那一份；面板的需求不受影响。
            Assert.Equal(1, adapter.DemandConsumerCount);
            Assert.True(adapter.AggregateDemand.Ui);
            Assert.False(adapter.AggregateDemand.SensorWindow);
        }

        /// <summary>STA 线程执行（WinForms 窗口需要）。</summary>
        private static void RunSta(Action action)
        {
            Exception? captured = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromMinutes(2));
            if (!t.IsAlive && captured != null) throw captured;
            if (t.IsAlive) throw new TimeoutException("STA 测试线程 2 分钟未结束");
        }
    }
}
