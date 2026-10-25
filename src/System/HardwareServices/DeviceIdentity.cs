using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// [F07] 设备种类。
    /// <para>GPU / 磁盘 / 风扇**共用同一套身份原则**（StableId / RuntimeId / DisplayName / ParentId / Kind / IsPresent），
    /// 但**不强行共用一套硬件 API**：各自的探针工厂不同（见 <see cref="IDeviceProbeFactory"/>）。</para>
    /// </summary>
    public enum DeviceKind
    {
        Unknown = 0,
        Gpu = 1,
        Storage = 2,
        Fan = 3,
        Network = 4,
        Motherboard = 5,
    }

    /// <summary>
    /// [F07] 身份可靠性等级。
    /// </summary>
    public enum IdentityStability
    {
        /// <summary>未判定（还没有任何身份来源可用）。</summary>
        Unknown = 0,

        /// <summary>
        /// 明确**不可**作为跨重启身份：枚举序号、LHM 媒体类型标识（"/nvme"）、显示名、LUID 等。
        /// <para>★ 处于该等级时**不得**把历史绑定复用到当前设备（母计划 F07 第 3/5 条）。</para>
        /// </summary>
        Unstable = 1,

        /// <summary>有可证明的跨重启唯一标识（PnP 实例路径 / 序列号 / PCI 总线位置）。</summary>
        Stable = 2,
    }

    /// <summary>
    /// [F07] 设备身份 —— **展示槽位与设备身份的分离载体**。
    /// <para>公开指标 Key 仍然是 "GPU.{index}.*"，<see cref="StableId"/> 只用来把"槽位"绑到"物理设备"，
    /// 裸 PCI 路径**不会**被拼进公开 Key（母计划 F07 第 2 条）。</para>
    /// </summary>
    public sealed class DeviceIdentity
    {
        /// <summary>跨重启稳定标识；空串 = **无法证明**（此时 <see cref="Stability"/> 不会是 Stable）。</summary>
        public string StableId { get; init; } = "";

        /// <summary>本次枚举的实例标识（永远非空；只在本次会话内有意义）。</summary>
        public string RuntimeId { get; init; } = "";

        /// <summary>显示名（**不可**单独作为身份）。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>父设备身份（风扇 → 主板；GPU / 磁盘为空）。</summary>
        public string ParentId { get; init; } = "";

        /// <summary>设备种类。</summary>
        public DeviceKind Kind { get; init; } = DeviceKind.Unknown;

        /// <summary>本次枚举中是否存在（消失的设备 IsPresent = false）。</summary>
        public bool IsPresent { get; init; } = true;

        /// <summary>身份可靠性等级。</summary>
        public IdentityStability Stability { get; init; } = IdentityStability.Unknown;

        /// <summary>可靠性判定依据（中文，可直接进日志 / 验收报告）。</summary>
        public string StabilityReason { get; init; } = "";

        /// <summary>
        /// 佐证属性（"serial" / "bus" / "size" / "model" / "name" / "lhmIdentifier" ...）。
        /// <para>★ 用途：当稳定 ID 变了（例如驱动更新后 PnP 实例路径变化）时，
        /// 只有**唯一命中**佐证属性才允许判定"仍是同一台设备"，否则一律不复用历史绑定。</para>
        /// </summary>
        public IReadOnlyDictionary<string, string> Attributes { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否拿到了可证明的跨重启标识。</summary>
        public bool HasStableId => !string.IsNullOrEmpty(StableId);

        /// <summary>
        /// 持久化取值：有稳定 ID 用稳定 ID，否则退回运行时 ID。
        /// <para>★ 调用方必须自己看 <see cref="Stability"/>：退回运行时 ID 意味着"只能本次会话内有效"。</para>
        /// </summary>
        public string PersistedId => HasStableId ? StableId : RuntimeId;

        /// <summary>取一个佐证属性（不存在返回 null）。</summary>
        public string? GetAttribute(string key)
            => Attributes.TryGetValue(key, out string? v) ? v : null;
    }

    /// <summary>
    /// [F07] 设备选择项 —— **Label + Value** 成对（与同仓既有 <c>ListAllGpuOptions</c> 范式一致）。
    /// <para>Label 是给人看的（同名同型号会加 "#序号" 后缀），Value 是给机器看的（稳定 ID）。</para>
    /// </summary>
    public sealed class DeviceOption
    {
        /// <summary>显示标签（同名设备会带 "#序号" 后缀）。</summary>
        public string Label { get; set; } = "";

        /// <summary>持久化取值：稳定 ID（无法证明时退回运行时 ID）。</summary>
        public string Value { get; set; } = "";

        /// <summary>原始显示名（不含后缀）。</summary>
        public string Name { get; set; } = "";

        /// <summary>设备种类。</summary>
        public DeviceKind Kind { get; set; } = DeviceKind.Unknown;

        /// <summary>本次枚举中是否存在。</summary>
        public bool IsPresent { get; set; } = true;

        /// <summary>身份可靠性等级。</summary>
        public IdentityStability Stability { get; set; } = IdentityStability.Unknown;

        /// <summary>运行时标识（迁移旧配置时用来匹配旧的 LHM Identifier）。</summary>
        public string RuntimeId { get; set; } = "";

        /// <summary>稳定标识（无法证明时为空串）。</summary>
        public string StableId { get; set; } = "";
    }

    /// <summary>
    /// [F07] 设备探针 —— 把一个硬件对象抽象成"可用于判定身份"的原始字段。
    /// <para>★ 抽象出来是为了可注入：测试要能模拟"驱动更新导致标识变化""同名双盘""枚举顺序互换"。</para>
    /// </summary>
    public sealed class DeviceProbe
    {
        /// <summary>设备种类。</summary>
        public DeviceKind Kind { get; init; } = DeviceKind.Unknown;

        /// <summary>显示名。</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>运行时标识（本次枚举的实例标识）。</summary>
        public string RuntimeId { get; init; } = "";

        /// <summary>设备实例 ID（PnP 实例路径或等价物）；空 = 没有。</summary>
        public string? InstanceId { get; init; }

        /// <summary>设备序列号；空 = 没有。</summary>
        public string? SerialNumber { get; init; }

        /// <summary>总线位置（如 PCI "0:2d.0"）；空 = 没有。</summary>
        public string? BusInfo { get; init; }

        /// <summary>型号。</summary>
        public string? Model { get; init; }

        /// <summary>容量（字节）。</summary>
        public long? SizeBytes { get; init; }

        /// <summary>父设备的运行时标识（风扇 → 主板）。</summary>
        public string? ParentRuntimeId { get; init; }
    }

    /// <summary>
    /// [F07] 设备探针工厂 —— **可注入**的硬件形态适配层。
    /// </summary>
    public interface IDeviceProbeFactory
    {
        /// <summary>把一个 LHM 硬件对象转成 <see cref="DeviceProbe"/>（**不得**触发设备探测 / SMART 读取）。</summary>
        DeviceProbe Create(IHardware hardware, DeviceKind kind);
    }

    /// <summary>
    /// [F07] 默认探针工厂 —— 从 LHM 0.9.6 公开面上能拿到的字段里取身份线索。
    /// <para>★ 实测事实（本机 LHM 0.9.6，见验收报告 §2）：
    ///   · <c>IHardware.Identifier</c> 对显卡是 "/gpu-nvidia/{枚举序号}"、对存储是 "/nvme"（媒体类型）—— **都不是身份**；
    ///   · 显卡的 <c>DeviceId</c> 是 PnP 实例路径（"PCI\VEN_10DE&DEV_2206&...\4&19aa7bb9&0&0019"）—— **可作身份**；
    ///   · 存储设备可经 <c>StorageDevice.Storage</c>（public）拿到 DiskInfoToolkit 的 DeviceID / SerialNumber / PhysicalPath。</para>
    /// <para>★ 本类**只读属性**，不调用 <c>Update()</c>：枚举身份**不得**顺带做 SMART 探测（#466/#601 止血口径）。</para>
    /// </summary>
    public sealed class DefaultDeviceProbeFactory : IDeviceProbeFactory
    {
        /// <summary>共享实例（无状态）。</summary>
        public static readonly DefaultDeviceProbeFactory Instance = new DefaultDeviceProbeFactory();

        /// <inheritdoc />
        public DeviceProbe Create(IHardware hardware, DeviceKind kind)
        {
            ArgumentNullException.ThrowIfNull(hardware);

            string displayName = hardware.Name ?? "";
            string lhmIdentifier = hardware.Identifier?.ToString() ?? "";
            string? instanceId = null;
            string? serial = null;
            string? bus = null;
            string? model = null;
            long? size = null;
            string runtimeId = lhmIdentifier;

            if (kind == DeviceKind.Gpu)
            {
                // ★ 显卡：DeviceId 是 PnP 实例路径（LHM GenericGpu 上的 public 属性）。
                instanceId = ReadString(hardware, "DeviceId");

                // AMD：BusNumber + DeviceNumber 组成 PCI 位置（public 属性）。
                int? busNumber = ReadInt(hardware, "BusNumber");
                int? deviceNumber = ReadInt(hardware, "DeviceNumber");
                if (busNumber.HasValue && deviceNumber.HasValue)
                {
                    bus = $"{busNumber.Value:x}:{deviceNumber.Value:x}";
                }
            }
            else if (kind == DeviceKind.Storage)
            {
                // ★ 存储：LHM 0.9.6 的 StorageDevice 包装了 DiskInfoToolkit.Storage（public get_Storage）。
                //   这里走反射读取，避免在编译期绑定 DiskInfoToolkit（它只是 LHM 的传递依赖，F09b 可能换版本）。
                object? storage = TryGetStorageDeviceStorage(hardware);
                if (storage != null)
                {
                    instanceId = ReadString(storage, "DeviceID");
                    serial = ReadString(storage, "SerialNumber");
                    model = ReadString(storage, "Model");
                    size = ReadInt(storage, "TotalSize");

                    // LHM 0.9.6 的存储 Identifier 只是媒体类型（"/nvme"），必须在运行时标识里补上设备路径，
                    // 否则**所有** NVMe 盘共用同一个运行时标识，迁移时无法区分。
                    string? physicalPath = ReadString(storage, "PhysicalPath");
                    if (!string.IsNullOrWhiteSpace(physicalPath))
                    {
                        runtimeId = string.IsNullOrEmpty(lhmIdentifier)
                            ? physicalPath
                            : lhmIdentifier + "|" + physicalPath;
                    }
                }
            }

            return new DeviceProbe
            {
                Kind = kind,
                DisplayName = displayName,
                RuntimeId = runtimeId,
                InstanceId = string.IsNullOrWhiteSpace(instanceId) ? null : instanceId,
                SerialNumber = string.IsNullOrWhiteSpace(serial) ? null : serial,
                BusInfo = string.IsNullOrWhiteSpace(bus) ? null : bus,
                Model = string.IsNullOrWhiteSpace(model) ? null : model,
                SizeBytes = size,
            };
        }

        /// <summary>取 LHM <c>StorageDevice.Storage</c>（public get_Storage），取不到返回 null。</summary>
        private static object? TryGetStorageDeviceStorage(IHardware hardware)
        {
            try
            {
                PropertyInfo? prop = hardware.GetType().GetProperty("Storage", BindingFlags.Public | BindingFlags.Instance);
                return prop?.GetValue(hardware);
            }
            catch
            {
                // 取不到身份线索不是致命错误：降级成"只有枚举序号/显示名"，由调用方标记为不稳定。
                return null;
            }
        }

        private static string? ReadString(object? target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo? prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object? value = prop?.GetValue(target);
                string? text = value as string ?? value?.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static int? ReadInt(object? target, string propertyName)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo? prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (prop?.GetValue(target) is int i) return i;
                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// [F07] 身份解析器 —— **身份原则的单一落点**（GPU / 磁盘 / 风扇共用）。
    /// <para>取值优先级（母计划 F07 第 3 条：优先 PnP 实例 / 设备唯一标识 / 总线信息）：</para>
    /// <list type="number">
    ///   <item>设备实例 ID（PnP 实例路径）→ 稳定；</item>
    ///   <item>设备序列号 → 稳定；</item>
    ///   <item>总线位置（PCI bus:device）→ 稳定；</item>
    ///   <item>都没有 → **明确标记为不稳定**，<b>不猜绑定</b>。</item>
    /// </list>
    /// </summary>
    public sealed class DeviceIdentityResolver
    {
        /// <summary>佐证属性名：序列号。</summary>
        public const string AttributeSerial = "serial";

        /// <summary>佐证属性名：总线位置。</summary>
        public const string AttributeBus = "bus";

        /// <summary>佐证属性名：容量。</summary>
        public const string AttributeSize = "size";

        /// <summary>佐证属性名：型号。</summary>
        public const string AttributeModel = "model";

        /// <summary>佐证属性名：显示名。</summary>
        public const string AttributeName = "name";

        /// <summary>佐证属性名：LHM 原始 Identifier。</summary>
        public const string AttributeLhmIdentifier = "lhmIdentifier";

        private readonly IDeviceProbeFactory _factory;
        private readonly ConcurrentDictionary<IHardware, DeviceIdentity> _cache =
            new(ReferenceEqualityComparer.Instance);

        /// <param name="factory">探针工厂（默认 <see cref="DefaultDeviceProbeFactory"/>）。</param>
        public DeviceIdentityResolver(IDeviceProbeFactory? factory = null)
        {
            _factory = factory ?? DefaultDeviceProbeFactory.Instance;
        }

        /// <summary>解析一个硬件对象的身份（带缓存，避免每次采样都反射一遍）。</summary>
        public DeviceIdentity Resolve(IHardware hardware, DeviceKind kind)
        {
            ArgumentNullException.ThrowIfNull(hardware);
            return _cache.GetOrAdd(hardware, hw => Resolve(_factory.Create(hw, kind)));
        }

        /// <summary>按探针解析身份（纯函数，便于测试直接喂探针）。</summary>
        public DeviceIdentity Resolve(DeviceProbe probe)
        {
            ArgumentNullException.ThrowIfNull(probe);

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AttributeName] = probe.DisplayName ?? "",
            };
            if (!string.IsNullOrWhiteSpace(probe.Model))
                attributes[AttributeModel] = probe.Model!;
            if (!string.IsNullOrWhiteSpace(probe.SerialNumber))
                attributes[AttributeSerial] = probe.SerialNumber!;
            if (!string.IsNullOrWhiteSpace(probe.BusInfo))
                attributes[AttributeBus] = probe.BusInfo!;
            if (probe.SizeBytes.HasValue)
                attributes[AttributeSize] = probe.SizeBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string runtimeId = string.IsNullOrWhiteSpace(probe.RuntimeId)
                ? (probe.DisplayName ?? "")
                : probe.RuntimeId;

            // 1) 设备实例 ID（PnP 实例路径）
            if (!string.IsNullOrWhiteSpace(probe.InstanceId))
            {
                return Build(probe, attributes, runtimeId, "pnp:" + probe.InstanceId!.Trim(),
                    IdentityStability.Stable, "设备实例 ID（PnP 实例路径）");
            }

            // 2) 序列号
            if (!string.IsNullOrWhiteSpace(probe.SerialNumber))
            {
                return Build(probe, attributes, runtimeId, "serial:" + probe.SerialNumber!.Trim(),
                    IdentityStability.Stable, "设备序列号");
            }

            // 3) 总线位置
            if (!string.IsNullOrWhiteSpace(probe.BusInfo))
            {
                return Build(probe, attributes, runtimeId, "bus:" + probe.BusInfo!.Trim(),
                    IdentityStability.Stable, "总线位置（PCI bus:device）");
            }

            // 4) 兜底：只有枚举序号 / 显示名 ⇒ 明确标记不稳定，**不猜绑定**
            return Build(probe, attributes, runtimeId, "",
                IdentityStability.Unstable, "只有枚举序号 / 显示名，不能作为跨重启身份");
        }

        private static DeviceIdentity Build(
            DeviceProbe probe,
            Dictionary<string, string> attributes,
            string runtimeId,
            string stableId,
            IdentityStability stability,
            string reason)
        {
            attributes[AttributeLhmIdentifier] = probe.RuntimeId ?? "";
            return new DeviceIdentity
            {
                StableId = stableId,
                RuntimeId = runtimeId,
                DisplayName = probe.DisplayName ?? "",
                ParentId = probe.ParentRuntimeId ?? "",
                Kind = probe.Kind,
                IsPresent = true,
                Stability = stability,
                StabilityReason = reason,
                Attributes = attributes,
            };
        }
    }

    /// <summary>
    /// [F07] 设备目录 —— 一次枚举的**不可变快照**。
    /// <para>★ 目录里的设备按"身份"索引，不再按显示名去重（#538 根因）。</para>
    /// </summary>
    public sealed class DeviceCatalog
    {
        private readonly List<DeviceIdentity> _devices;

        /// <summary>目录里的全部设备。</summary>
        public IReadOnlyList<DeviceIdentity> Devices => _devices;

        /// <param name="devices">设备集合。</param>
        public DeviceCatalog(IEnumerable<DeviceIdentity> devices)
        {
            _devices = (devices ?? Enumerable.Empty<DeviceIdentity>()).ToList();
        }

        /// <summary>按种类过滤。</summary>
        public IReadOnlyList<DeviceIdentity> OfKind(DeviceKind kind)
            => _devices.Where(d => d.Kind == kind).ToList();

        /// <summary>按稳定 ID 查找（找不到返回 null）。</summary>
        public DeviceIdentity? FindByStableId(string? stableId)
        {
            if (string.IsNullOrWhiteSpace(stableId)) return null;
            return _devices.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.StableId) &&
                string.Equals(d.StableId, stableId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按运行时标识查找（找不到返回 null）。</summary>
        public DeviceIdentity? FindByRuntimeId(string? runtimeId)
        {
            if (string.IsNullOrWhiteSpace(runtimeId)) return null;
            return _devices.FirstOrDefault(d =>
                string.Equals(d.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按显示名查找（**可能**返回多个 —— 同名设备不能按名字唯一定位）。</summary>
        public IReadOnlyList<DeviceIdentity> FindByDisplayName(string? displayName, DeviceKind kind)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return Array.Empty<DeviceIdentity>();
            return _devices.Where(d =>
                d.Kind == kind &&
                string.Equals(d.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// 把设备集合转成 **Label + Value** 选择项。
        /// <para>★ 同名设备按既有范式加 "#序号" 后缀（与 <c>ListAllGpuOptions</c> 一致）。</para>
        /// </summary>
        public IReadOnlyList<DeviceOption> ToOptions(DeviceKind kind)
        {
            var sameKind = OfKind(kind);
            var nameCounts = sameKind
                .GroupBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var nameIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var options = new List<DeviceOption>();

            foreach (DeviceIdentity device in sameKind)
            {
                string name = device.DisplayName ?? "";
                nameIndexes.TryGetValue(name, out int currentIndex);
                currentIndex++;
                nameIndexes[name] = currentIndex;

                bool duplicated = nameCounts.TryGetValue(name, out int count) && count > 1;
                options.Add(new DeviceOption
                {
                    Label = duplicated ? $"{name} #{currentIndex}" : name,
                    Value = device.PersistedId,
                    Name = name,
                    Kind = device.Kind,
                    IsPresent = device.IsPresent,
                    Stability = device.Stability,
                    RuntimeId = device.RuntimeId,
                    StableId = device.StableId,
                });
            }

            return options;
        }

        /// <summary>按硬件集合建目录。</summary>
        public static DeviceCatalog Build(
            IEnumerable<IHardware> hardware,
            DeviceKind kind,
            DeviceIdentityResolver? resolver = null)
        {
            ArgumentNullException.ThrowIfNull(hardware);
            resolver ??= new DeviceIdentityResolver();
            var identities = hardware.Select(hw => resolver.Resolve(hw, kind));
            return new DeviceCatalog(identities);
        }
    }

    /// <summary>
    /// [F07] 设备身份匹配器 —— 把"配置里保存的取值"和"当前枚举到的设备"对上。
    /// </summary>
    public static class DeviceIdentityMatcher
    {
        /// <summary>
        /// 判断某个硬件对象是否命中配置里保存的设备取值。
        /// <para>★ 命中口径：稳定 ID → 运行时标识 → 显示名（旧配置遗留）。</para>
        /// </summary>
        public static bool Matches(
            IHardware hardware,
            string? persistedValue,
            DeviceKind kind,
            DeviceIdentityResolver? resolver = null)
        {
            if (string.IsNullOrWhiteSpace(persistedValue)) return true;
            DeviceIdentity identity = (resolver ?? new DeviceIdentityResolver()).Resolve(hardware, kind);
            return Matches(identity, persistedValue);
        }

        /// <summary>判断某个身份是否命中配置里保存的设备取值。</summary>
        public static bool Matches(DeviceIdentity identity, string? persistedValue)
        {
            if (string.IsNullOrWhiteSpace(persistedValue)) return true;
            if (string.Equals(identity.StableId, persistedValue, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(identity.RuntimeId, persistedValue, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(identity.DisplayName, persistedValue, StringComparison.OrdinalIgnoreCase);
        }
    }
}
