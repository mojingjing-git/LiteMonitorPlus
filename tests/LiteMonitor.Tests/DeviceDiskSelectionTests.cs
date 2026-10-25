using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [F07] 磁盘选择的**行为**测试（#538 的落点）。
    ///
    /// <para><b>要守住的</b>：同型号的两块盘可以**分别**选中 ——
    /// 选中第一块时，第二块**不会**被一起更新（旧实现按显示名匹配，两块同型号盘会同时命中）。</para>
    /// </summary>
    public class DeviceDiskSelectionTests
    {
        private const string Disk1Id = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\4&1a2b3c&0&000000";
        private const string Disk2Id = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\4&4d5e6f&0&000000";

        /// <summary>可注入的探针工厂：给两块同型号盘不同的实例 ID。</summary>
        private sealed class DiskProbeFactory : IDeviceProbeFactory
        {
            private readonly Dictionary<IHardware, DeviceProbe> _probes = new();

            public void Set(IHardware hardware, string instanceId, string physicalPath, string serial)
            {
                _probes[hardware] = new DeviceProbe
                {
                    Kind = DeviceKind.Storage,
                    DisplayName = hardware.Name ?? "",
                    RuntimeId = "/nvme|" + physicalPath,
                    InstanceId = instanceId,
                    SerialNumber = serial,
                };
            }

            public DeviceProbe Create(IHardware hardware, DeviceKind kind)
            {
                return _probes.TryGetValue(hardware, out DeviceProbe? probe)
                    ? probe
                    : new DeviceProbe
                    {
                        Kind = kind,
                        DisplayName = hardware.Name ?? "",
                        RuntimeId = hardware.Identifier?.ToString() ?? "",
                    };
            }
        }

        private static (F07Fake.FakeStorage disk1, F07Fake.FakeStorage disk2, DiskProbeFactory factory) NewDisks()
        {
            var disk1 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");
            var disk2 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");
            var factory = new DiskProbeFactory();
            factory.Set(disk1, Disk1Id, @"\\.\PhysicalDrive0", "SN-1111");
            factory.Set(disk2, Disk2Id, @"\\.\PhysicalDrive1", "SN-2222");
            return (disk1, disk2, factory);
        }

        [Fact]
        public void 同型号双盘_选中第一块时第二块不被更新()
        {
            var (disk1, disk2, factory) = NewDisks();
            var manager = new DiskManager(identityResolver: new DeviceIdentityResolver(factory));
            var cfg = new Settings();

            // 用户按新的稳定身份选中第一块。
            cfg.PreferredDisk = "pnp:" + Disk1Id;

            manager.ProcessUpdate(disk1, cfg, false, false);
            manager.ProcessUpdate(disk2, cfg, false, false);

            // ★ 只有被选中的那块被更新（同型号的另一块不受影响）。
            Assert.Equal(1, disk1.UpdateCount);
            Assert.Equal(0, disk2.UpdateCount);
        }

        [Fact]
        public void 同型号双盘_选中第二块时第一块不被更新()
        {
            var (disk1, disk2, factory) = NewDisks();
            var manager = new DiskManager(identityResolver: new DeviceIdentityResolver(factory));
            var cfg = new Settings();

            cfg.PreferredDisk = "pnp:" + Disk2Id;

            manager.ProcessUpdate(disk1, cfg, false, false);
            manager.ProcessUpdate(disk2, cfg, false, false);

            Assert.Equal(0, disk1.UpdateCount);
            Assert.Equal(1, disk2.UpdateCount);
        }

        [Fact]
        public void 未指定磁盘时_两块盘都按自动策略更新()
        {
            var (disk1, disk2, factory) = NewDisks();
            var manager = new DiskManager(identityResolver: new DeviceIdentityResolver(factory));
            var cfg = new Settings();

            // 自动模式：没有首选 ⇒ 不锁定任何一块（既有语义不变）。
            manager.ProcessUpdate(disk1, cfg, false, false);
            manager.ProcessUpdate(disk2, cfg, false, false);

            Assert.Equal(1, disk1.UpdateCount);
            Assert.Equal(1, disk2.UpdateCount);
        }

        [Fact]
        public void 旧配置按名称指定_两块同型号盘都会命中_保持兼容不报错()
        {
            var (disk1, disk2, factory) = NewDisks();
            var manager = new DiskManager(identityResolver: new DeviceIdentityResolver(factory));
            var cfg = new Settings();

            // 旧配置里存的是显示名（同型号 ⇒ 名字相同）⇒ 名称匹配，两块都命中。
            // ★ 这里断言的是"不崩溃 + 旧语义不被破坏"，不是"随机选第一块"。
            cfg.PreferredDisk = "Samsung SSD 980 PRO 1TB";

            manager.ProcessUpdate(disk1, cfg, false, false);
            manager.ProcessUpdate(disk2, cfg, false, false);

            Assert.Equal(1, disk1.UpdateCount);
            Assert.Equal(1, disk2.UpdateCount);
        }

        [Fact]
        public void 设备消失_首选身份不匹配任何盘时不更新任何盘()
        {
            var (disk1, disk2, factory) = NewDisks();
            var manager = new DiskManager(identityResolver: new DeviceIdentityResolver(factory));
            var cfg = new Settings();

            // 首选的是一块**已经拔掉**的盘（第三块）。
            cfg.PreferredDisk = "pnp:SCSI\\DISK&VEN_NVME&PROD_SAMSUNG\\4&dead&0&000000";

            manager.ProcessUpdate(disk1, cfg, false, false);
            manager.ProcessUpdate(disk2, cfg, false, false);

            // ★ 首选盘不在场 ⇒ 不把别的盘当成它（不会静默改绑）。
            Assert.Equal(0, disk1.UpdateCount);
            Assert.Equal(0, disk2.UpdateCount);
        }
    }
}
