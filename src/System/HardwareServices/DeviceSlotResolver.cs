using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// [F07] 槽位解析状态 —— 公开给 UI / 下游包（F10 / F11）的**设备选择结论**。
    /// </summary>
    public enum DeviceSlotState
    {
        /// <summary>首次分配（此前没有绑定）。</summary>
        NewSlot = 0,

        /// <summary>已按稳定 ID 绑定。</summary>
        Bound = 1,

        /// <summary>稳定 ID 变了，但佐证属性**唯一**命中 ⇒ 判定为同一台设备。</summary>
        ResolvedByCorroboration = 2,

        /// <summary>绑定的设备当前不存在（离线）—— **保持绑定，不改绑**。</summary>
        Offline = 3,

        /// <summary>无法证明是同一台设备 —— **保持绑定，不复用历史绑定**。</summary>
        Unresolved = 4,

        /// <summary>
        /// 自动模式的**临时回退**：原绑定设备当前不存在 / 无法证明，
        /// 已临时改用当前可用设备（<b>原绑定保持不变</b>，设备回来后自动回到原槽位）。
        /// </summary>
        AutoFallback = 5,
    }

    /// <summary>
    /// [F07] 一个槽位（或"用户首选"）的解析结果。
    /// </summary>
    public sealed class DeviceSlotResolution
    {
        /// <summary>槽位序号（"用户首选"为 -1）。</summary>
        public int Slot { get; init; } = -1;

        /// <summary>槽位键（"preferred" / "0" / "1" ...）。</summary>
        public string SlotKey { get; init; } = DeviceBindingStore.PreferredSlot;

        /// <summary>解析到的硬件对象（离线 / 无法解析时为 null）。</summary>
        public IHardware? Hardware { get; init; }

        /// <summary>解析到的设备身份（离线 / 无法解析时为 null）。</summary>
        public DeviceIdentity? Identity { get; init; }

        /// <summary>解析状态。</summary>
        public DeviceSlotState State { get; init; } = DeviceSlotState.NewSlot;

        /// <summary>结论说明（中文，可直接进日志 / 提示）。</summary>
        public string Reason { get; init; } = "";

        /// <summary>是否是用户手动指定。</summary>
        public bool IsManual { get; init; }

        /// <summary>是否拿到了设备（离线 / 无法解析时为 false）。</summary>
        public bool HasDevice => Hardware != null;
    }

    /// <summary>
    /// [F07] 槽位解析器 —— **展示槽位与设备身份分离**的核心。
    ///
    /// <para>★ 规则：</para>
    /// <list type="bullet">
    ///   <item>槽位 → 设备靠**持久化绑定**（稳定 ID），公开 Key 仍是 "GPU.{index}.*"（不变）；</item>
    ///   <item>手动首选的设备消失 → <b>保持绑定</b>、报告离线，<b>不改绑</b>到别的设备；</item>
    ///   <item>稳定 ID 变了（驱动更新等）→ 只有佐证属性（序列号 / 总线）<b>唯一</b>命中才判定为同一台，
    ///         否则 <b>不复用</b>历史绑定（宁可离线，也不猜）。</item>
    /// </list>
    /// </summary>
    public sealed class DeviceSlotResolver
    {
        private readonly DeviceBindingStore _store;
        private readonly DeviceIdentityResolver _resolver;

        /// <param name="store">绑定仓库（null = 产品默认实例）。</param>
        /// <param name="resolver">身份解析器（null = 默认）。</param>
        public DeviceSlotResolver(DeviceBindingStore? store = null, DeviceIdentityResolver? resolver = null)
        {
            _store = store ?? DeviceBindingStore.Shared;
            _resolver = resolver ?? new DeviceIdentityResolver();
        }

        /// <summary>绑定仓库（供上层读写 / 迁移）。</summary>
        public DeviceBindingStore Store => _store;

        /// <summary>身份解析器（供上层解析"当前实际选中的设备"）。</summary>
        public DeviceIdentityResolver IdentityResolver => _resolver;

        /// <summary>
        /// 解析"用户手动首选"的设备。
        /// <para>★ 返回 <see cref="DeviceSlotState.Offline"/> 时，调用方必须**保持原绑定**，
        /// 不得改绑到其它设备（母计划 F07 第 6 条）。</para>
        /// </summary>
        public DeviceSlotResolution ResolvePreferred(
            DeviceKind kind,
            string? persistedValue,
            IReadOnlyList<IHardware> candidates)
        {
            var pairs = Pair(candidates, kind);
            string value = persistedValue ?? "";

            if (string.IsNullOrWhiteSpace(value))
            {
                return new DeviceSlotResolution
                {
                    Slot = -1,
                    SlotKey = DeviceBindingStore.PreferredSlot,
                    State = DeviceSlotState.NewSlot,
                    Reason = "未指定设备（自动模式）",
                };
            }

            // 1) 稳定 ID 命中
            var byStable = pairs.Where(p =>
                !string.IsNullOrEmpty(p.Identity.StableId) &&
                string.Equals(p.Identity.StableId, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byStable.Count == 1)
            {
                return Build(-1, DeviceBindingStore.PreferredSlot, byStable[0], DeviceSlotState.Bound,
                    "按稳定设备 ID 命中", true);
            }

            // 2) 运行时标识命中（旧配置：LHM Identifier）
            var byRuntime = pairs.Where(p =>
                string.Equals(p.Identity.RuntimeId, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byRuntime.Count == 1)
            {
                return Build(-1, DeviceBindingStore.PreferredSlot, byRuntime[0], DeviceSlotState.Bound,
                    "按运行时标识命中（旧配置，建议迁移为稳定 ID）", true);
            }
            if (byRuntime.Count > 1)
            {
                return new DeviceSlotResolution
                {
                    Slot = -1,
                    SlotKey = DeviceBindingStore.PreferredSlot,
                    State = DeviceSlotState.Unresolved,
                    Reason = "旧取值命中多个设备（同名），无法确定是哪一台",
                    IsManual = true,
                };
            }

            // 3) 显示名命中（旧配置：设备显示名）—— 同名 ⇒ 歧义，绝不随机选第一块
            var byName = pairs.Where(p =>
                string.Equals(p.Identity.DisplayName, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1)
            {
                return Build(-1, DeviceBindingStore.PreferredSlot, byName[0], DeviceSlotState.Bound,
                    "按显示名唯一命中（旧配置）", true);
            }
            if (byName.Count > 1)
            {
                return new DeviceSlotResolution
                {
                    Slot = -1,
                    SlotKey = DeviceBindingStore.PreferredSlot,
                    State = DeviceSlotState.Unresolved,
                    Reason = "旧取值命中多个同型号设备，无法确定是哪一台（保留原设置）",
                    IsManual = true,
                };
            }

            // 4) 找不到 ⇒ 设备离线（保持绑定）
            var binding = _store.Find(kind, DeviceBindingStore.PreferredSlot);
            return new DeviceSlotResolution
            {
                Slot = -1,
                SlotKey = DeviceBindingStore.PreferredSlot,
                State = DeviceSlotState.Offline,
                Reason = binding == null
                    ? "指定的设备当前不存在"
                    : $"指定的设备当前不存在（仍绑定「{binding.DisplayName}」）",
                IsManual = true,
            };
        }

        /// <summary>
        /// 解析某个编号槽位（多显卡用）。
        /// </summary>
        public DeviceSlotResolution ResolveSlot(DeviceKind kind, int slot, IReadOnlyList<IHardware> candidates)
        {
            string slotKey = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var binding = _store.Find(kind, slotKey);
            if (binding == null)
            {
                return new DeviceSlotResolution
                {
                    Slot = slot,
                    SlotKey = slotKey,
                    State = DeviceSlotState.NewSlot,
                    Reason = "该槽位尚未绑定设备",
                };
            }

            var pairs = Pair(candidates, kind);

            // 1) 稳定 ID 命中
            var byStable = pairs.Where(p =>
                !string.IsNullOrEmpty(p.Identity.StableId) &&
                string.Equals(p.Identity.StableId, binding.StableId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byStable.Count == 1)
            {
                return Build(slot, slotKey, byStable[0], DeviceSlotState.Bound, "按稳定设备 ID 命中", binding.Manual);
            }

            // 2) 稳定 ID 没命中（设备换了标识 / 驱动更新）⇒ 佐证属性唯一命中才认同一台
            var corroborated = Corroborate(binding, pairs);
            if (corroborated.Count == 1)
            {
                return Build(slot, slotKey, corroborated[0], DeviceSlotState.ResolvedByCorroboration,
                    "稳定 ID 已变化，但序列号 / 总线信息唯一命中，判定为同一台设备", binding.Manual);
            }
            if (corroborated.Count > 1)
            {
                return new DeviceSlotResolution
                {
                    Slot = slot,
                    SlotKey = slotKey,
                    State = DeviceSlotState.Unresolved,
                    Reason = "稳定 ID 已变化，且佐证属性命中多个设备，无法证明是哪一台",
                    IsManual = binding.Manual,
                };
            }

            // 3) 设备不在场 vs 无法证明
            bool present = pairs.Any(p =>
                string.Equals(p.Identity.RuntimeId, binding.RuntimeId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(binding.StableId) &&
                 string.Equals(p.Identity.StableId, binding.StableId, StringComparison.OrdinalIgnoreCase)));

            return new DeviceSlotResolution
            {
                Slot = slot,
                SlotKey = slotKey,
                State = present ? DeviceSlotState.Unresolved : DeviceSlotState.Offline,
                Reason = present
                    ? "无法证明与原绑定是同一台设备（不复用历史绑定）"
                    : $"绑定的设备当前不存在（仍绑定「{binding.DisplayName}」）",
                IsManual = binding.Manual,
            };
        }

        /// <summary>
        /// 佐证匹配：序列号或总线位置**唯一**命中，且显示名一致，才判定为同一台设备。
        /// </summary>
        private static List<(IHardware Hardware, DeviceIdentity Identity)> Corroborate(
            DeviceBindingStore.DeviceBinding binding,
            List<(IHardware Hardware, DeviceIdentity Identity)> pairs)
        {
            string? serial = binding.Attributes.TryGetValue(DeviceIdentityResolver.AttributeSerial, out string? s)
                ? s : null;
            string? bus = binding.Attributes.TryGetValue(DeviceIdentityResolver.AttributeBus, out string? b) ? b : null;

            if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(bus))
            {
                // ★ 没有强佐证（序列号 / 总线）⇒ 不猜。
                return new List<(IHardware, DeviceIdentity)>();
            }

            return pairs.Where(p =>
            {
                if (!string.Equals(p.Identity.DisplayName, binding.DisplayName, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.IsNullOrWhiteSpace(serial) &&
                    string.Equals(p.Identity.GetAttribute(DeviceIdentityResolver.AttributeSerial), serial, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(bus) &&
                    string.Equals(p.Identity.GetAttribute(DeviceIdentityResolver.AttributeBus), bus, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }).ToList();
        }

        /// <summary>写入（或更新）一条槽位绑定，返回 true 表示内容发生了变化。</summary>
        public bool PersistBinding(DeviceKind kind, string slotKey, DeviceIdentity identity, bool manual)
        {
            var binding = new DeviceBindingStore.DeviceBinding
            {
                Kind = kind.ToString(),
                Slot = slotKey,
                StableId = identity.StableId,
                RuntimeId = identity.RuntimeId,
                DisplayName = identity.DisplayName,
                Manual = manual,
                UpdatedTimestamp = _store.NowTimestamp,
                Attributes = identity.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            };
            return _store.Upsert(binding);
        }

        /// <summary>删除一条绑定，返回 true 表示确实删掉了。</summary>
        public bool ClearBinding(DeviceKind kind, string slotKey) => _store.Remove(kind, slotKey);

        private List<(IHardware Hardware, DeviceIdentity Identity)> Pair(IReadOnlyList<IHardware> candidates, DeviceKind kind)
        {
            var result = new List<(IHardware, DeviceIdentity)>();
            foreach (IHardware hw in candidates ?? Array.Empty<IHardware>())
            {
                result.Add((hw, _resolver.Resolve(hw, kind)));
            }
            return result;
        }

        private static DeviceSlotResolution Build(
            int slot,
            string slotKey,
            (IHardware Hardware, DeviceIdentity Identity) pair,
            DeviceSlotState state,
            string reason,
            bool manual)
        {
            return new DeviceSlotResolution
            {
                Slot = slot,
                SlotKey = slotKey,
                Hardware = pair.Hardware,
                Identity = pair.Identity,
                State = state,
                Reason = reason,
                IsManual = manual,
            };
        }
    }
}
