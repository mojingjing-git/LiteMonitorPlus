#nullable enable annotations

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using LiteMonitor.src.Core;
using LiteMonitor.src.SystemServices;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [F07] **槽位 ↔ 设备身份**绑定的测试（供 F08b / F10 / F11 复用的迁移夹具）。
    ///
    /// <para><b>要守住的</b>：</para>
    /// <list type="bullet">
    ///   <item>公开指标 Key 仍是 "GPU.{index}.*"（**格式不变**），槽位靠**持久化绑定**指向物理设备；</item>
    ///   <item>双同型号显卡**枚举顺序互换**后，槽位仍指向同一块物理卡（#290 的核心）；</item>
    ///   <item>手动首选卡消失 ⇒ <b>保持绑定、标记离线</b>，不改绑到别的卡；设备回来 ⇒ 恢复原身份；</item>
    ///   <item>驱动更新导致标识变化 ⇒ <b>无法证明就不复用</b>历史绑定；有强佐证才判定为同一台。</item>
    /// </list>
    /// </summary>
    public class DeviceSlotBindingTests : IDisposable
    {
        private const string GpuA = @"PCI\VEN_10DE&DEV_2206&SUBSYS_146D10DE\4&19aa7bb9&0&0019";
        private const string GpuB = @"PCI\VEN_10DE&DEV_2206&SUBSYS_146D10DE\4&4d5e6f&0&001A";

        private readonly string _sandbox;
        private readonly DeviceBindingStore _store;

        public DeviceSlotBindingTests()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), "lm-f07-bind-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sandbox);
            _store = new DeviceBindingStore(Path.Combine(_sandbox, "device-bindings.json"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_sandbox, true); } catch { /* 清理失败不影响断言 */ }
        }

        private static F07Fake.FakeGpu Gpu(string name, string identifier, string? deviceId)
            => new F07Fake.FakeGpu(name, identifier, deviceId);

        private static Settings NewSettings(int count, string? preferredGpu = null)
        {
            var s = new Settings { MonitoredGpuCount = count };
            s.PreferredGpu = preferredGpu ?? "";
            s.MonitorItems = new List<MonitorItemConfig>();
            return s;
        }

        [Fact]
        public void 双同型号显卡_枚举顺序互换_槽位仍指向同一块物理卡()
        {
            var gpuA = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuA);
            var gpuB = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuB);

            var map = new SensorMap(new DeviceSlotResolver(_store));
            Settings cfg = NewSettings(2);

            // 第一次枚举：槽位 0 → A，槽位 1 → B（并持久化绑定）。
            map.Rebuild(new IHardware[] { gpuA, gpuB }, cfg);
            Assert.Same(gpuA, map.GetGpuByIndex(0));
            Assert.Same(gpuB, map.GetGpuByIndex(1));

            // 模拟重启：同样的两块卡，但 LHM 枚举顺序反了、序号也变了。
            var gpuA2 = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuA);
            var gpuB2 = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuB);

            map.Rebuild(new IHardware[] { gpuB2, gpuA2 }, cfg);

            // ★ 槽位 0 仍然是 A（同一块物理卡），不是"枚举里的第一块"。
            Assert.Same(gpuA2, map.GetGpuByIndex(0));
            Assert.Same(gpuB2, map.GetGpuByIndex(1));
        }

        [Fact]
        public void 公开指标Key格式不变_仍是GPU序号形式()
        {
            var gpuA = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuA);
            var gpuB = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuB);

            var map = new SensorMap(new DeviceSlotResolver(_store));
            Settings cfg = NewSettings(2);
            map.Rebuild(new IHardware[] { gpuA, gpuB }, cfg);

            GpuItemSync.Ensure(cfg, availableGpuCount: 2);
            List<string> keys = cfg.MonitorItems.Select(x => x.Key).ToList();

            // 监控项 Key 仍然是 "GPU.{index}.{指标}"。
            Assert.Contains("GPU.0.Load", keys);
            Assert.Contains("GPU.1.Load", keys);
            // 裸 PCI 路径**不会**被拼进公开 Key（主题 / 插件 / Web 约定不受影响）。
            Assert.DoesNotContain(keys, k => k.Contains("VEN_10DE", StringComparison.Ordinal));
            Assert.DoesNotContain(keys, k => k.Contains("PCI", StringComparison.Ordinal));
            Assert.DoesNotContain(keys, k => k.Contains("pnp:", StringComparison.Ordinal));
        }

        [Fact]
        public void 手动首选卡消失_保持绑定并标记离线_不改绑()
        {
            var gpuA = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuA);
            var gpuB = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuB);

            var map = new SensorMap(new DeviceSlotResolver(_store));
            Settings cfg = NewSettings(1);

            // 用户手动选了 A。
            string stableA = new DeviceIdentityResolver().Resolve(gpuA, DeviceKind.Gpu).StableId;
            cfg.PreferredGpu = stableA;
            map.Rebuild(new IHardware[] { gpuA, gpuB }, cfg);
            Assert.Same(gpuA, map.GetGpuByIndex(0));

            // A 消失（驱动重置 / 设备拔出）。
            map.Rebuild(new IHardware[] { gpuB }, cfg);

            // ★ 保持绑定、显示离线；**不**改绑到 B。
            Assert.Null(map.GetGpuByIndex(0));
            Assert.True(map.IsPreferredGpuOffline);
            Assert.Equal(stableA, cfg.PreferredGpu);

            var binding = _store.Find(DeviceKind.Gpu, DeviceBindingStore.PreferredSlot);
            Assert.NotNull(binding);
            Assert.Equal(stableA, binding!.StableId);
            Assert.Equal("NVIDIA GeForce RTX 3080", binding.DisplayName);
        }

        [Fact]
        public void 首选卡回来_恢复原身份()
        {
            var gpuA = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuA);
            var gpuB = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuB);

            var map = new SensorMap(new DeviceSlotResolver(_store));
            Settings cfg = NewSettings(1);
            string stableA = new DeviceIdentityResolver().Resolve(gpuA, DeviceKind.Gpu).StableId;
            cfg.PreferredGpu = stableA;

            map.Rebuild(new IHardware[] { gpuA, gpuB }, cfg);
            map.Rebuild(new IHardware[] { gpuB }, cfg);              // A 消失
            var gpuA2 = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuA);
            map.Rebuild(new IHardware[] { gpuB, gpuA2 }, cfg);       // A 回来（序号还变了）

            Assert.Same(gpuA2, map.GetGpuByIndex(0));
            Assert.False(map.IsPreferredGpuOffline);
        }

        [Fact]
        public void 驱动更新导致标识变化_无法证明时不复用历史绑定()
        {
            var gpuA = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuA);
            var gpuB = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuB);

            var resolver = new DeviceSlotResolver(_store);
            Settings cfg = NewSettings(2);

            // 第一次：槽位 0 绑定 A。
            var map = new SensorMap(resolver);
            map.Rebuild(new IHardware[] { gpuA, gpuB }, cfg);
            Assert.Same(gpuA, map.GetGpuByIndex(0));

            // 驱动更新 ⇒ A 的 PnP 实例路径变了，而且**没有**序列号 / 总线佐证。
            var gpuA2 = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuA + "-NEW");
            var gpuB2 = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuB);

            var map2 = new SensorMap(new DeviceSlotResolver(_store));
            map2.Rebuild(new IHardware[] { gpuB2, gpuA2 }, cfg);

            // ★ 无法证明 ⇒ 不复用历史绑定（状态必须是 Unresolved / AutoFallback，而不是 Bound）。
            var resolution = map2.GpuSlotResolutions.First(r => r.Slot == 0);
            Assert.NotEqual(DeviceSlotState.Bound, resolution.State);
            Assert.NotEqual(DeviceSlotState.ResolvedByCorroboration, resolution.State);

            // 原绑定**保持不变**（没有被悄悄改绑到别的卡）。
            var binding = _store.Find(DeviceKind.Gpu, "0");
            Assert.NotNull(binding);
            Assert.Equal("pnp:" + GpuA, binding!.StableId);
        }

        [Fact]
        public void 驱动更新导致标识变化_有序列号佐证_判定为同一台设备()
        {
            // 可注入的探针工厂：给 A 一个序列号（强佐证）。
            var factory = new SerialProbeFactory();
            var resolver = new DeviceSlotResolver(_store, new DeviceIdentityResolver(factory));
            Settings cfg = NewSettings(1);

            var gpuA = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuA);
            factory.Set(gpuA, DeviceIdentityResolver.AttributeSerial, "SN-12345");

            var map = new SensorMap(resolver);
            map.Rebuild(new IHardware[] { gpuA }, cfg);
            Assert.Same(gpuA, map.GetGpuByIndex(0));

            // 驱动更新：PnP 路径变了，但序列号没变 ⇒ 仍能证明是同一台。
            var gpuA2 = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuA + "-NEW");
            factory.Set(gpuA2, DeviceIdentityResolver.AttributeSerial, "SN-12345");

            var map2 = new SensorMap(new DeviceSlotResolver(_store, new DeviceIdentityResolver(factory)));
            map2.Rebuild(new IHardware[] { gpuA2 }, cfg);

            var resolution = map2.GpuSlotResolutions.First(r => r.Slot == 0);
            Assert.Equal(DeviceSlotState.ResolvedByCorroboration, resolution.State);
            Assert.Same(gpuA2, map2.GetGpuByIndex(0));
        }

        [Fact]
        public void 绑定仓库_写入幂等_覆盖时保留备份()
        {
            var binding = new DeviceBindingStore.DeviceBinding
            {
                Kind = DeviceKind.Gpu.ToString(),
                Slot = "0",
                StableId = "pnp:" + GpuA,
                DisplayName = "NVIDIA GeForce RTX 3080",
                UpdatedTimestamp = 1,
            };

            Assert.True(_store.Upsert(binding));
            Assert.True(_store.Save());
            string first = File.ReadAllText(_store.FilePath);

            // 幂等：内容没变 ⇒ 不重写（文件内容逐字相同）。
            Assert.False(_store.Upsert(binding));
            Assert.True(_store.Save());
            Assert.Equal(first, File.ReadAllText(_store.FilePath));

            // 内容变了 ⇒ 覆盖，并把旧版留成 .bak。
            binding.StableId = "pnp:" + GpuB;
            Assert.True(_store.Upsert(binding));
            Assert.True(_store.Save());
            Assert.True(File.Exists(_store.BackupPath));
            Assert.Equal(first, File.ReadAllText(_store.BackupPath));
        }

        [Fact]
        public void 绑定仓库_主文件损坏_回落到备份()
        {
            var binding = new DeviceBindingStore.DeviceBinding
            {
                Kind = DeviceKind.Gpu.ToString(),
                Slot = "0",
                StableId = "pnp:" + GpuA,
                DisplayName = "NVIDIA GeForce RTX 3080",
            };
            _store.Upsert(binding);
            _store.Save();

            string good = File.ReadAllText(_store.FilePath);
            File.WriteAllText(_store.FilePath, "{ this is not json");
            File.WriteAllText(_store.BackupPath, good);

            var reloaded = new DeviceBindingStore(_store.FilePath);
            Assert.True(reloaded.Load());
            Assert.Single(reloaded.Bindings);
            Assert.Equal("pnp:" + GpuA, reloaded.Bindings[0].StableId);
        }

        [Fact]
        public void 重复加载不重复新增指标_排序阈值不漂移()
        {
            var gpuA = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/0", GpuA);
            var gpuB = Gpu("NVIDIA GeForce RTX 3080", "gpu-nvidia/1", GpuB);

            var map = new SensorMap(new DeviceSlotResolver(_store));
            Settings cfg = NewSettings(2);
            map.Rebuild(new IHardware[] { gpuA, gpuB }, cfg);

            GpuItemSync.Ensure(cfg, availableGpuCount: 2);
            int firstCount = cfg.MonitorItems.Count;
            var firstSorts = cfg.MonitorItems.Select(x => (x.Key, x.SortIndex)).ToList();

            // 重复重建 + 重复 Ensure：指标数量与排序**不漂移**。
            map.Rebuild(new IHardware[] { gpuB, gpuA }, cfg);
            GpuItemSync.Ensure(cfg, availableGpuCount: 2);

            Assert.Equal(firstCount, cfg.MonitorItems.Count);
            var secondSorts = cfg.MonitorItems.Select(x => (x.Key, x.SortIndex)).ToList();
            Assert.Equal(firstSorts, secondSorts);
            Assert.Equal(firstCount, cfg.MonitorItems.Select(x => x.Key).Distinct().Count());
        }

        /// <summary>可注入的探针工厂：给指定硬件追加一个佐证属性（序列号）。</summary>
        private sealed class SerialProbeFactory : IDeviceProbeFactory
        {
            private readonly Dictionary<IHardware, string> _serials = new();

            public void Set(IHardware hardware, string attribute, string value)
            {
                _serials[hardware] = value;
            }

            public DeviceProbe Create(IHardware hardware, DeviceKind kind)
            {
                return new DeviceProbe
                {
                    Kind = kind,
                    DisplayName = hardware.Name ?? "",
                    RuntimeId = hardware.Identifier?.ToString() ?? "",
                    InstanceId = (hardware as F07Fake.FakeGpu)?.DeviceId,
                    SerialNumber = _serials.TryGetValue(hardware, out string? serial) ? serial : null,
                };
            }
        }
    }
}
