using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [F07] 设备身份目录的契约测试。
    ///
    /// <para><b>要守住的</b>：</para>
    /// <list type="bullet">
    ///   <item>设备身份**不按显示名去重** —— 同型号两块盘必须是两个独立设备（#538 根因）；</item>
    ///   <item>稳定身份优先用 PnP 实例路径 / 序列号 / 总线位置；
    ///         只有枚举序号或显示名时**显式标记为不稳定**（不猜绑定）；</item>
    ///   <item>枚举身份**不得**顺带做 SMART 探测（不调用 Update）。</item>
    /// </list>
    /// </summary>
    public class DeviceIdentityTests
    {
        /// <summary>可注入的探针工厂：让测试能模拟"驱动更新导致标识变化"等形态。</summary>
        private sealed class StubProbeFactory : IDeviceProbeFactory
        {
            private readonly Dictionary<IHardware, DeviceProbe> _probes = new();

            public StubProbeFactory Set(IHardware hardware, DeviceProbe probe)
            {
                _probes[hardware] = probe;
                return this;
            }

            public DeviceProbe Create(IHardware hardware, DeviceKind kind)
            {
                if (_probes.TryGetValue(hardware, out DeviceProbe? probe)) return probe;
                return new DeviceProbe
                {
                    Kind = kind,
                    DisplayName = hardware.Name ?? "",
                    RuntimeId = hardware.Identifier?.ToString() ?? "",
                };
            }
        }

        [Fact]
        public void 显卡身份_用PnP实例路径当稳定ID()
        {
            // LHM 0.9.6 实测：显卡的 DeviceId 就是 PnP 实例路径。
            var gpu = new F07Fake.FakeGpu(
                "NVIDIA GeForce RTX 3080",
                "gpu-nvidia/0",
                @"PCI\VEN_10DE&DEV_2206&SUBSYS_146D10DE&REV_A1\4&19aa7bb9&0&0019");

            DeviceIdentity identity = new DeviceIdentityResolver().Resolve(gpu, DeviceKind.Gpu);

            Assert.Equal(IdentityStability.Stable, identity.Stability);
            Assert.StartsWith("pnp:", identity.StableId, StringComparison.Ordinal);
            Assert.Contains("VEN_10DE", identity.StableId, StringComparison.Ordinal);
            Assert.Equal(IdentityStability.Stable, identity.Stability);
            Assert.False(string.IsNullOrWhiteSpace(identity.StabilityReason));
        }

        [Fact]
        public void 显卡身份_没有实例ID时_显式标记为不稳定()
        {
            var gpu = new F07Fake.FakeGpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0");

            DeviceIdentity identity = new DeviceIdentityResolver().Resolve(gpu, DeviceKind.Gpu);

            // ★ 枚举序号 / 显示名不可单独作跨重启身份 ⇒ 必须显式标记为不稳定。
            Assert.Equal(IdentityStability.Unstable, identity.Stability);
            Assert.Equal("", identity.StableId);
            Assert.Contains("枚举序号", identity.StabilityReason, StringComparison.Ordinal);
            // 运行时标识仍然可用（本次会话内区分设备）。
            Assert.False(string.IsNullOrWhiteSpace(identity.RuntimeId));
        }

        [Fact]
        public void 同型号两块盘_目录里是两个独立设备()
        {
            var disk1 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");
            var disk2 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");

            var factory = new StubProbeFactory()
                .Set(disk1, new DeviceProbe
                {
                    Kind = DeviceKind.Storage,
                    DisplayName = "Samsung SSD 980 PRO 1TB",
                    RuntimeId = "/nvme|\\\\.\\PhysicalDrive0",
                    InstanceId = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\4&1a2b3c&0&000000",
                    SerialNumber = "S5Y1NJ0T111111",
                })
                .Set(disk2, new DeviceProbe
                {
                    Kind = DeviceKind.Storage,
                    DisplayName = "Samsung SSD 980 PRO 1TB",
                    RuntimeId = "/nvme|\\\\.\\PhysicalDrive1",
                    InstanceId = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\4&4d5e6f&0&000000",
                    SerialNumber = "S5Y1NJ0T222222",
                });

            var resolver = new DeviceIdentityResolver(factory);
            DeviceCatalog catalog = DeviceCatalog.Build(new IHardware[] { disk1, disk2 }, DeviceKind.Storage, resolver);

            // ★ 不再按显示名去重：两块同型号盘都在目录里。
            Assert.Equal(2, catalog.OfKind(DeviceKind.Storage).Count);
            Assert.Equal(2, catalog.Devices.Select(d => d.StableId).Distinct().Count());
        }

        [Fact]
        public void 同型号两块盘_选择项值不同且标签可区分()
        {
            var disk1 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");
            var disk2 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");

            var factory = new StubProbeFactory()
                .Set(disk1, new DeviceProbe
                {
                    Kind = DeviceKind.Storage,
                    DisplayName = "Samsung SSD 980 PRO 1TB",
                    RuntimeId = "/nvme|\\\\.\\PhysicalDrive0",
                    InstanceId = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\4&1a2b3c&0&000000",
                })
                .Set(disk2, new DeviceProbe
                {
                    Kind = DeviceKind.Storage,
                    DisplayName = "Samsung SSD 980 PRO 1TB",
                    RuntimeId = "/nvme|\\\\.\\PhysicalDrive1",
                    InstanceId = @"SCSI\DISK&VEN_NVME&PROD_SAMSUNG\4&4d5e6f&0&000000",
                });

            var resolver = new DeviceIdentityResolver(factory);
            DeviceCatalog catalog = DeviceCatalog.Build(new IHardware[] { disk1, disk2 }, DeviceKind.Storage, resolver);
            IReadOnlyList<DeviceOption> options = catalog.ToOptions(DeviceKind.Storage);

            Assert.Equal(2, options.Count);
            // 同名 ⇒ 加 "#序号" 后缀（与仓库既有 GPU 范式一致）。
            Assert.Equal("Samsung SSD 980 PRO 1TB #1", options[0].Label);
            Assert.Equal("Samsung SSD 980 PRO 1TB #2", options[1].Label);
            // 值必须是身份（不是显示名）⇒ 两块盘可以分别选中。
            Assert.NotEqual(options[0].Value, options[1].Value);
            Assert.DoesNotContain("Samsung", options[0].Value, StringComparison.Ordinal);
        }

        [Fact]
        public void 枚举磁盘身份_不触发任何设备Update()
        {
            // ★ #466/#601 止血口径：为了拿身份而枚举，**不得**顺带做 SMART 探测。
            var disk1 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");
            var disk2 = new F07Fake.FakeStorage("Samsung SSD 980 PRO 1TB", "nvme");
            IEnumerable<IHardware> hardware = new IHardware[] { disk1, disk2 };

            HardwareScanner.ClearCache();
            List<DeviceOption> options = HardwareScanner.ListAllDiskOptions(hardware);

            Assert.Equal(2, options.Count);
            Assert.Equal(0, disk1.UpdateCount);
            Assert.Equal(0, disk2.UpdateCount);
        }

        [Fact]
        public void 双同型号显卡_枚举顺序互换_身份不变()
        {
            // 同一块物理卡在两次枚举里的 LHM 序号不同（重启后 NVML 顺序可能变）。
            var gpuA = new F07Fake.FakeGpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0",
                @"PCI\VEN_10DE&DEV_2206&SUBSYS_146D10DE\4&19aa7bb9&0&0019");
            var gpuB = new F07Fake.FakeGpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1",
                @"PCI\VEN_10DE&DEV_2206&SUBSYS_146D10DE\4&4d5e6f&0&001A");

            var resolver = new DeviceIdentityResolver();
            DeviceCatalog first = DeviceCatalog.Build(new IHardware[] { gpuA, gpuB }, DeviceKind.Gpu, resolver);
            DeviceCatalog swapped = DeviceCatalog.Build(new IHardware[] { gpuB, gpuA }, DeviceKind.Gpu, resolver);

            string idA = first.OfKind(DeviceKind.Gpu)[0].StableId;
            string idASwapped = swapped.OfKind(DeviceKind.Gpu)[1].StableId;

            // ★ 顺序互换后，同一块物理卡的稳定身份**不变**（这正是 #290 的关键）。
            Assert.Equal(idA, idASwapped);
            Assert.Equal(2, swapped.OfKind(DeviceKind.Gpu).Count);
        }

        [Fact]
        public void 显卡选项_值优先是稳定身份而不是LHM序号()
        {
            var gpu = new F07Fake.FakeGpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0",
                @"PCI\VEN_10DE&DEV_2206\4&19aa7bb9&0&0019");
            IEnumerable<IHardware> hardware = new IHardware[] { gpu };

            HardwareScanner.ClearCache();
            var options = HardwareScanner.ListAllGpuOptions(hardware);

            Assert.Single(options);
            Assert.StartsWith("pnp:", options[0].Value, StringComparison.Ordinal);
            Assert.Equal(IdentityStability.Stable, options[0].Stability);
        }

        [Fact]
        public void 风扇身份_按父设备区分_同名传感器不互相覆盖()
        {
            var sensor1 = new F07Fake.FakeFanSensor("Fan1");
            var sensor2 = new F07Fake.FakeFanSensor("Fan1");
            var mainboard = new F07Fake.FakeMotherboard("Mainboard", "mainboard", sensor1);
            var superio = new F07Fake.FakeMotherboard("SuperIO", "superio", sensor2);
            IEnumerable<IHardware> hardware = new IHardware[] { mainboard, superio };

            HardwareScanner.ClearCache();
            List<DeviceOption> options = HardwareScanner.ListAllFanOptions(hardware, new object());

            Assert.Equal(2, options.Count);
            // 两个父设备上的同名传感器，身份必须不同（#577 的基础）。
            Assert.NotEqual(options[0].Value, options[1].Value);
            Assert.Contains("mainboard", options[0].Value, StringComparison.Ordinal);
            Assert.Contains("superio", options[1].Value, StringComparison.Ordinal);
        }

        [Fact]
        public void 身份不可证明时_持久化取值退回运行时标识()
        {
            var gpu = new F07Fake.FakeGpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0");

            DeviceIdentity identity = new DeviceIdentityResolver().Resolve(gpu, DeviceKind.Gpu);

            Assert.False(identity.HasStableId);
            Assert.Equal(identity.RuntimeId, identity.PersistedId);
            Assert.Equal(IdentityStability.Unstable, identity.Stability);
        }
    }
}
