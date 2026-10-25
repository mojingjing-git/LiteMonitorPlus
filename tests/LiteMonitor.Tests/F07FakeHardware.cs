#nullable enable annotations

using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// [F07] 测试替身：假硬件。
    ///
    /// <para>★ 全部**不接触驱动**：只继承 LHM 公开的 Hardware 基类，记录被调用的次数，
    /// 便于断言"枚举身份时没有顺带做 SMART 探测"（#466/#601 止血口径）。</para>
    /// </summary>
    internal static class F07Fake
    {
        /// <summary>空的 ISettings 实现（LHM 的 Hardware 基类构造函数需要它）。</summary>
        internal sealed class FakeSettings : ISettings
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

            public bool Contains(string name) => _values.ContainsKey(name);

            public void SetValue(string name, string value) => _values[name] = value;

            public string GetValue(string name, string value) => _values.TryGetValue(name, out var v) ? v : value;

            public void Remove(string name) => _values.Remove(name);
        }

        /// <summary>
        /// 假显卡。
        /// <para>★ <c>DeviceId</c> 模拟 LHM 的 PnP 实例路径（LHM 0.9.6 实测：显卡的 DeviceId 就是 PnP 路径）。</para>
        /// </summary>
        internal sealed class FakeGpu : Hardware
        {
            internal FakeGpu(string name, string identifier, string? deviceId = null)
                : base(name, new Identifier(new[] { identifier }), new FakeSettings())
            {
                DeviceId = deviceId;
            }

            public override HardwareType HardwareType => HardwareType.GpuNvidia;

            public override ISensor[] Sensors => Array.Empty<ISensor>();

            /// <summary>真实执行过的 Update 次数（枚举身份时必须是 0）。</summary>
            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;

            /// <summary>PnP 实例路径（可为空 ⇒ 身份不可证明）。</summary>
            public string? DeviceId { get; set; }

            /// <summary>AMD 风格的 PCI 总线号（可选佐证）。</summary>
            public int? BusNumber { get; set; }

            /// <summary>AMD 风格的 PCI 设备号（可选佐证）。</summary>
            public int? DeviceNumber { get; set; }
        }

        /// <summary>假存储设备。</summary>
        internal sealed class FakeStorage : Hardware
        {
            internal FakeStorage(string name, string identifier)
                : base(name, new Identifier(new[] { identifier }), new FakeSettings())
            {
            }

            public override HardwareType HardwareType => HardwareType.Storage;

            public override ISensor[] Sensors => Array.Empty<ISensor>();

            /// <summary>真实执行过的 Update 次数（枚举身份时必须是 0）。</summary>
            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;
        }

        /// <summary>假主板（作为风扇的父设备）。</summary>
        internal sealed class FakeMotherboard : Hardware
        {
            private readonly ISensor[] _sensors;

            internal FakeMotherboard(string name, string identifier, params ISensor[] sensors)
                : base(name, new Identifier(new[] { identifier }), new FakeSettings())
            {
                _sensors = sensors;
            }

            public override HardwareType HardwareType => HardwareType.Motherboard;

            public override ISensor[] Sensors => _sensors;

            public int UpdateCount { get; private set; }

            public override void Update() => UpdateCount++;
        }

        /// <summary>假风扇传感器。</summary>
        internal sealed class FakeFanSensor : ISensor
        {
            public FakeFanSensor(string name)
            {
                Name = name;
            }

            public string Name { get; set; }

            public float? Value { get; set; }

            public SensorType SensorType => SensorType.Fan;

            public IControl Control => null!;

            public IHardware Hardware => null!;

            public Identifier Identifier => default;

            public int Index => 0;

            public bool IsDefaultHidden => false;

            public float? Max => null;

            public float? Min => null;

            public IReadOnlyList<IParameter> Parameters => Array.Empty<IParameter>();

            public IEnumerable<SensorValue> Values => Array.Empty<SensorValue>();

            public TimeSpan ValuesTimeWindow { get; set; }

            public void ResetMin() { }

            public void ResetMax() { }

            public void ClearValues() { }

            public void Accept(IVisitor visitor) { }

            public void Traverse(IVisitor visitor) { }
        }
    }
}
