using System;
using System.Collections.Generic;
using LiteMonitor.src.Core.Actions;
using LiteMonitor.Core.Actions.Services;

namespace LiteMonitor.src.Core.Scenarios
{
    /// <summary>
    /// S04 生产侧门面：固定库路径、从当前设置创建、重命名/删除、按修订应用。
    /// 运行时模块应用由组合根注入；默认仅持久化并把候选值合并到 live。
    /// </summary>
    public sealed class ScenarioService
    {
        private readonly ScenarioProfileStore _store;
        private readonly ScenarioApplyCoordinator _coordinator;

        public ScenarioService(
            ScenarioProfileStore? store = null,
            Func<Settings, bool>? save = null,
            Func<Settings, Settings, IReadOnlyList<ModuleApplyResult>>? applyModules = null,
            Func<string, bool>? themeExists = null)
        {
            _store = store ?? new ScenarioProfileStore();
            _coordinator = new ScenarioApplyCoordinator(
                save ?? (candidate => candidate.TrySave()),
                applyModules ?? DefaultApply,
                themeExists);
        }

        public ScenarioProfileStore Store => _store;

        public IReadOnlyList<ScenarioProfile> List() => _store.LoadAll();

        public ScenarioStoreResult CreateFromCurrent(string name, Settings current)
        {
            if (current == null) return ScenarioStoreResult.Fail("invalid-settings", "当前设置不可用。");
            var profile = ScenarioProfile.Create(name, current);
            return _store.Save(profile, 0);
        }

        public ScenarioStoreResult Rename(ScenarioProfile profile, string newName)
        {
            if (profile == null) return ScenarioStoreResult.Fail("invalid-profile", "场景不能为空。");
            var copy = profile.Clone();
            copy.Name = newName ?? "";
            copy.UpdatedAtUtc = DateTimeOffset.UtcNow;
            return _store.Save(copy, profile.DefinitionRevision);
        }

        public ScenarioStoreResult Delete(ScenarioProfile profile)
            => profile == null
                ? ScenarioStoreResult.Fail("invalid-profile", "场景不能为空。")
                : _store.Delete(profile.Id, profile.DefinitionRevision);

        public ScenarioApplyOutcome Apply(ScenarioProfile profile, Settings current)
            => _coordinator.Apply(profile, current, profile?.DefinitionRevision ?? 0);

        private static IReadOnlyList<ModuleApplyResult> DefaultApply(Settings live, Settings candidate)
        {
            if (live == null || candidate == null)
                return new[] { ModuleApplyResult.Fail("Settings", "not-applied", "live-unchanged", "missing-settings") };

            // 保存已经成功；这里只把允许字段的候选值合并到 live，供无法注入 UI 的宿主使用。
            SettingsChanger.Merge(live, candidate);
            return new[] { ModuleApplyResult.Ok("Settings", "saved", "merged") };
        }
    }
}

