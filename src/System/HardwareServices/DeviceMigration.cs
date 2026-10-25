using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// [F07] 旧配置迁移的**结论**。
    /// </summary>
    public enum DeviceMigrationOutcome
    {
        /// <summary>没有旧值（自动模式），无需迁移。</summary>
        NotNeeded = 0,

        /// <summary>已经是新格式（取值已命中某个设备的稳定 ID）—— 幂等。</summary>
        AlreadyCurrent = 1,

        /// <summary>旧值**唯一**命中一个设备 → 已迁移。</summary>
        MigratedUnique = 2,

        /// <summary>旧值命中**多个**设备（同型号）→ 保留旧值 + 提示，等待用户明确选择。</summary>
        AmbiguousPreserved = 3,

        /// <summary>旧值**找不到**对应设备 → 保留旧值 + 提示。</summary>
        UnresolvedPreserved = 4,
    }

    /// <summary>
    /// [F07] 旧配置迁移结果 —— 迁移**夹具**的产物。
    /// </summary>
    public sealed class DeviceMigrationResult
    {
        /// <summary>迁移结论。</summary>
        public DeviceMigrationOutcome Outcome { get; init; }

        /// <summary>迁移前的旧值。</summary>
        public string PreviousValue { get; init; } = "";

        /// <summary>迁移后的新值（仅 <see cref="DeviceMigrationOutcome.MigratedUnique"/> 时有意义）。</summary>
        public string? MigratedValue { get; init; }

        /// <summary>是否需要用户明确选择（歧义 / 找不到）。</summary>
        public bool RequiresUserChoice =>
            Outcome is DeviceMigrationOutcome.AmbiguousPreserved or DeviceMigrationOutcome.UnresolvedPreserved;

        /// <summary>面向用户的中文提示（歧义 / 找不到时非空）。</summary>
        public string Prompt { get; init; } = "";

        /// <summary>候选设备（歧义时列出全部候选，供设置页展示）。</summary>
        public IReadOnlyList<DeviceOption> Candidates { get; init; } = Array.Empty<DeviceOption>();

        /// <summary>
        /// 应该被持久化的取值。
        /// <para>★ 歧义 / 找不到时**原样保留旧值**（绝不随机选第一块）。</para>
        /// </summary>
        public string ValueToPersist =>
            Outcome == DeviceMigrationOutcome.MigratedUnique && !string.IsNullOrEmpty(MigratedValue)
                ? MigratedValue!
                : PreviousValue;
    }

    /// <summary>
    /// [F07] 设备配置的**迁移夹具** —— 旧名称 / 旧 LHM 标识 → 新稳定 ID。
    ///
    /// <para>★ 规则（母计划 F07 第 5 条）：</para>
    /// <list type="bullet">
    ///   <item>只有**唯一匹配**才迁移；</item>
    ///   <item>同名歧义 → 保留旧值 + 提示，<b>不随机选第一块</b>；</item>
    ///   <item>找不到 → 保留旧值 + 提示；</item>
    ///   <item>幂等：已经是新格式时原样返回（重复加载不重复迁移）。</item>
    /// </list>
    /// </summary>
    public static class DeviceMigration
    {
        /// <summary>
        /// 把一个旧取值迁移到新的稳定 ID。
        /// </summary>
        /// <param name="currentValue">配置里当前保存的取值（可能是旧显示名、旧 LHM Identifier 或已经是新稳定 ID）。</param>
        /// <param name="options">当前枚举到的候选设备（Label + Value）。</param>
        /// <param name="kind">设备种类（决定提示文案）。</param>
        public static DeviceMigrationResult Migrate(
            string? currentValue,
            IReadOnlyList<DeviceOption> options,
            DeviceKind kind)
        {
            string value = currentValue ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                return new DeviceMigrationResult { Outcome = DeviceMigrationOutcome.NotNeeded };
            }

            options ??= Array.Empty<DeviceOption>();

            // 1) 已经是新格式：取值命中某个候选的 Value（稳定 ID）。
            var byValue = options.Where(o =>
                !string.IsNullOrEmpty(o.Value) &&
                string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byValue.Count >= 1)
            {
                return new DeviceMigrationResult
                {
                    Outcome = DeviceMigrationOutcome.AlreadyCurrent,
                    PreviousValue = value,
                    MigratedValue = byValue[0].Value,
                };
            }

            // 2) 旧 LHM 标识（如 "/gpu-nvidia/0"）：按运行时标识匹配。
            var byRuntimeId = options.Where(o =>
                !string.IsNullOrEmpty(o.RuntimeId) &&
                string.Equals(o.RuntimeId, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byRuntimeId.Count > 0)
            {
                return Finish(byRuntimeId, value, kind, "运行时标识");
            }

            // 3) 旧显示名：按名称匹配（同名设备会命中多个 ⇒ 歧义）。
            var byName = options.Where(o =>
                !string.IsNullOrEmpty(o.Name) &&
                string.Equals(o.Name, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0)
            {
                return Finish(byName, value, kind, "名称");
            }

            // 4) 找不到：保留旧值 + 提示。
            return new DeviceMigrationResult
            {
                Outcome = DeviceMigrationOutcome.UnresolvedPreserved,
                PreviousValue = value,
                Prompt = BuildPrompt(DeviceMigrationOutcome.UnresolvedPreserved, value, kind, Array.Empty<DeviceOption>()),
            };
        }

        private static DeviceMigrationResult Finish(
            List<DeviceOption> candidates,
            string value,
            DeviceKind kind,
            string matchedBy)
        {
            if (candidates.Count == 1)
            {
                return new DeviceMigrationResult
                {
                    Outcome = DeviceMigrationOutcome.MigratedUnique,
                    PreviousValue = value,
                    MigratedValue = candidates[0].Value,
                    Candidates = candidates,
                };
            }

            // ★ 歧义：保留旧值 + 提示，**绝不**随机选第一块。
            return new DeviceMigrationResult
            {
                Outcome = DeviceMigrationOutcome.AmbiguousPreserved,
                PreviousValue = value,
                Prompt = BuildPrompt(DeviceMigrationOutcome.AmbiguousPreserved, value, kind, candidates),
                Candidates = candidates,
            };
        }

        private static string BuildPrompt(
            DeviceMigrationOutcome outcome,
            string value,
            DeviceKind kind,
            IReadOnlyList<DeviceOption> candidates)
        {
            string kindName = kind switch
            {
                DeviceKind.Gpu => "显卡",
                DeviceKind.Storage => "磁盘",
                DeviceKind.Fan => "风扇",
                DeviceKind.Network => "网卡",
                _ => "设备",
            };

            if (outcome == DeviceMigrationOutcome.AmbiguousPreserved)
            {
                string labels = string.Join("、", candidates.Select(c => c.Label));
                return $"检测到多个同型号{kindName}（{labels}），无法确定旧设置「{value}」指的是哪一台；已保留原设置，请手动选择。";
            }

            return $"旧设置中的{kindName}「{value}」当前不存在；已保留原设置，设备恢复后仍可自动对应，也可在设置页重新选择。";
        }
    }
}
