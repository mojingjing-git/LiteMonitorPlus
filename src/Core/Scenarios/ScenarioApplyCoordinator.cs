using System;
using System.Collections.Generic;
using System.Linq;
using LiteMonitor.Core.Actions.Services;

namespace LiteMonitor.src.Core.Scenarios
{
    public sealed record ScenarioApplyOutcome(
        ScenarioPreflightResult Preflight,
        ApplyOperationResult Operation,
        Settings? Candidate);

    /// <summary>
    /// S04 串行应用：固定修订读取 → 预检 → 只改允许字段的候选设置 → 持久化 →
    /// 按模块应用 → 由模块结果确认外层状态。不声称原子回滚；保存失败时 live 设置不变。
    /// 运行时应用由组合根注入，Core 不引用 UI/主题/任务栏类型。
    /// </summary>
    public sealed class ScenarioApplyCoordinator
    {
        private readonly Func<string, bool>? _themeExists;
        private readonly Func<Settings, bool> _save;
        private readonly Func<Settings, Settings, IReadOnlyList<ModuleApplyResult>> _applyModules;

        public ScenarioApplyCoordinator(
            Func<Settings, bool> save,
            Func<Settings, Settings, IReadOnlyList<ModuleApplyResult>> applyModules,
            Func<string, bool>? themeExists = null)
        {
            _save = save ?? throw new ArgumentNullException(nameof(save));
            _applyModules = applyModules ?? throw new ArgumentNullException(nameof(applyModules));
            _themeExists = themeExists;
        }

        public ScenarioApplyOutcome Apply(
            ScenarioProfile? profile,
            Settings current,
            long requestRevision,
            string? operationId = null)
        {
            ArgumentNullException.ThrowIfNull(current);
            var preflight = ScenarioPreflight.Inspect(profile, current, _themeExists);
            var id = string.IsNullOrWhiteSpace(operationId)
                ? "scenario-" + Guid.NewGuid().ToString("N")
                : operationId!;

            if (profile == null || !preflight.CanApply)
            {
                var failure = ModuleApplyResult.Fail(
                    "Preflight", "not-applied", "live-unchanged",
                    string.Join("; ", preflight.BlockingIssues.Select(issue => issue.Code)));
                var blocked = ApplyOperationResult.FromModules(
                    id, requestRevision, new[] { failure }, failureCode: "preflight-blocked");
                return new ScenarioApplyOutcome(preflight, blocked, null);
            }

            // 固定修订：调用方在进入本方法前已读取 profile；这里不再按名称重新查找。
            if (profile.DefinitionRevision != requestRevision)
            {
                var mismatch = ModuleApplyResult.Fail(
                    "Preflight", "not-applied", "live-unchanged",
                    $"stale-revision expected={requestRevision} actual={profile.DefinitionRevision}");
                var stale = ApplyOperationResult.FromModules(
                    id, requestRevision, new[] { mismatch }, failureCode: "stale-revision");
                return new ScenarioApplyOutcome(preflight, stale, null);
            }

            var changes = preflight.Changes;
            if (changes.Count == 0)
            {
                var noChange = ModuleApplyResult.Ok("Scenario", "current", "no-change");
                return new ScenarioApplyOutcome(
                    preflight,
                    ApplyOperationResult.FromModules(
                        id, requestRevision, new[] { noChange }, requestRevision),
                    null);
            }

            var candidate = current.DeepClone();
            ScenarioProjection.ApplyTo(candidate, profile.Settings);

            if (!_save(candidate))
            {
                var notSaved = ModuleApplyResult.Fail(
                    "Settings", "not-saved", "live-unchanged", "settings-write-failed");
                var failed = ApplyOperationResult.FromModules(
                    id, requestRevision, new[] { notSaved }, failureCode: "settings-write-failed");
                return new ScenarioApplyOutcome(preflight, failed, candidate);
            }

            IReadOnlyList<ModuleApplyResult> modules;
            try
            {
                modules = _applyModules(current, candidate)
                          ?? Array.Empty<ModuleApplyResult>();
            }
            catch (Exception ex)
            {
                modules = new[]
                {
                    ModuleApplyResult.Fail("Runtime", "saved", "live-partial", ex.Message, true)
                };
            }

            if (modules.Count == 0)
            {
                modules = new[]
                {
                    ModuleApplyResult.Ok("Scenario", "saved", "no-runtime-modules")
                };
            }

            var operation = ApplyOperationResult.FromModules(
                id, requestRevision, modules, effectiveRevision: requestRevision);
            return new ScenarioApplyOutcome(preflight, operation, candidate);
        }
    }
}
