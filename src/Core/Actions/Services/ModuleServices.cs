using System;
using System.Collections.Generic;
using LiteMonitor.Contracts;
using LiteMonitor.Core; // SettingsProjection（同目录树目标命名空间）

namespace LiteMonitor.Core.Actions.Services
{
    /// <summary>设置变更归属的模块（差异清单按此分派）。</summary>
    public enum SettingsModule
    {
        Sampling,
        WebServer,
        Appearance,
        Taskbar,
        Plugins,
        Other,
    }

    /// <summary>单个字段的差异：字段名 + 归属模块。</summary>
    public sealed record SettingsFieldDiff(string Field, SettingsModule Module);

    /// <summary>
    /// 模块应用结果三元组（Plan B §4.2：失败时保留"已保存配置 / 实际生效状态 / 失败原因"，
    /// 可解释、不声称原子回滚）。
    /// </summary>
    public sealed record ModuleApplyResult(
        string Module,
        bool Applied,
        string? SavedState,
        string? EffectiveState,
        string? FailureReason)
    {
        public static ModuleApplyResult Ok(string module, string? saved = null, string? effective = null)
            => new(module, true, saved, effective, null);

        public static ModuleApplyResult Fail(string module, string saved, string? effective, string reason)
            => new(module, false, saved, effective, reason);
    }

    /// <summary>
    /// 采集配置应用器（P-B3 阶段一）：投影 → <see cref="IHardwareClient.ApplyConfig"/>。
    /// 唯一做实的模块服务；其余四个为委托壳（Core 不得引用 UI/WebServer/Plugins 类型，
    /// architecture-assert A-1～A-4 硬拦），委托在组合根接线（阶段三）。
    /// </summary>
    public sealed class SamplingConfigApplier
    {
        private readonly IHardwareClient _client;

        public SamplingConfigApplier(IHardwareClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public ModuleApplyResult Apply(Settings settings, int revision)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            string saved = $"refresh={settings.RefreshMs}ms controller={settings.ControllerProbeMode?.ToString() ?? "-1"} disk={settings.DiskProbeMode?.ToString() ?? "-1"}";
            try
            {
                var result = _client.ApplyConfig(SettingsProjection.ToSamplingConfig(settings, revision));
                return result.Applied
                    ? ModuleApplyResult.Ok(nameof(SettingsModule.Sampling), saved, $"revision={result.Revision}")
                    : ModuleApplyResult.Fail(nameof(SettingsModule.Sampling), saved, null, result.FailureReason ?? "apply-config rejected");
            }
            catch (Exception ex)
            {
                return ModuleApplyResult.Fail(nameof(SettingsModule.Sampling), saved, null, ex.Message);
            }
        }
    }

    /// <summary>外观应用器（委托壳）：主题/语言/布局等既有 Apply 路径由组合根注入。</summary>
    public sealed class WindowAppearanceApplier
    {
        private readonly Action _apply;
        public WindowAppearanceApplier(Action apply) => _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        public ModuleApplyResult Apply()
        {
            try { _apply(); return ModuleApplyResult.Ok(nameof(SettingsModule.Appearance)); }
            catch (Exception ex) { return ModuleApplyResult.Fail(nameof(SettingsModule.Appearance), "draft", null, ex.Message); }
        }
    }

    /// <summary>任务栏控制器（委托壳）。</summary>
    public sealed class TaskbarController
    {
        private readonly Action _apply;
        public TaskbarController(Action apply) => _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        public ModuleApplyResult Apply()
        {
            try { _apply(); return ModuleApplyResult.Ok(nameof(SettingsModule.Taskbar)); }
            catch (Exception ex) { return ModuleApplyResult.Fail(nameof(SettingsModule.Taskbar), "draft", null, ex.Message); }
        }
    }

    /// <summary>Web 服务器控制器（委托壳）：端口/开关变化的单独重启监听由既有路径承担。</summary>
    public sealed class WebServerController
    {
        private readonly Action _apply;
        public WebServerController(Action apply) => _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        public ModuleApplyResult Apply()
        {
            try { _apply(); return ModuleApplyResult.Ok(nameof(SettingsModule.WebServer)); }
            catch (Exception ex) { return ModuleApplyResult.Fail(nameof(SettingsModule.WebServer), "draft", null, ex.Message); }
        }
    }

    /// <summary>插件运行时控制器（委托壳）。</summary>
    public sealed class PluginRuntimeController
    {
        private readonly Action _apply;
        public PluginRuntimeController(Action apply) => _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        public ModuleApplyResult Apply()
        {
            try { _apply(); return ModuleApplyResult.Ok(nameof(SettingsModule.Plugins)); }
            catch (Exception ex) { return ModuleApplyResult.Fail(nameof(SettingsModule.Plugins), "draft", null, ex.Message); }
        }
    }

    /// <summary>
    /// 差异分派器：按 <see cref="SettingsFieldDiff"/> 的模块归属调用对应服务，每模块至多一次。
    /// 未接线的模块（委托壳未提供）记为 Ok(saved) 并注明"未接线"，不阻塞其他模块。
    /// </summary>
    public sealed class SettingsApplyDispatcher
    {
        private readonly SamplingConfigApplier? _sampling;
        private readonly WindowAppearanceApplier? _appearance;
        private readonly TaskbarController? _taskbar;
        private readonly WebServerController? _web;
        private readonly PluginRuntimeController? _plugins;

        public SettingsApplyDispatcher(
            SamplingConfigApplier? sampling = null,
            WindowAppearanceApplier? appearance = null,
            TaskbarController? taskbar = null,
            WebServerController? web = null,
            PluginRuntimeController? plugins = null)
        {
            _sampling = sampling; _appearance = appearance; _taskbar = taskbar; _web = web; _plugins = plugins;
        }

        public IReadOnlyList<ModuleApplyResult> Dispatch(
            IEnumerable<SettingsFieldDiff> diffs,
            Settings? settings = null,
            int revision = SettingsProjection.UnconfiguredProbeMode)
        {
            var results = new List<ModuleApplyResult>();
            var modules = new HashSet<SettingsModule>();
            foreach (var d in diffs) modules.Add(d.Module);

            if (modules.Contains(SettingsModule.Sampling))
            {
                results.Add(_sampling is null
                    ? NotWired(nameof(SettingsModule.Sampling))
                    : (settings is null
                        ? ModuleApplyResult.Fail(nameof(SettingsModule.Sampling), "null", null, "sampling diff but no settings provided")
                        : _sampling.Apply(settings, revision)));
            }
            if (modules.Contains(SettingsModule.Appearance))
                results.Add(_appearance is null ? NotWired(nameof(SettingsModule.Appearance)) : _appearance.Apply());
            if (modules.Contains(SettingsModule.Taskbar))
                results.Add(_taskbar is null ? NotWired(nameof(SettingsModule.Taskbar)) : _taskbar.Apply());
            if (modules.Contains(SettingsModule.WebServer))
                results.Add(_web is null ? NotWired(nameof(SettingsModule.WebServer)) : _web.Apply());
            if (modules.Contains(SettingsModule.Plugins))
                results.Add(_plugins is null ? NotWired(nameof(SettingsModule.Plugins)) : _plugins.Apply());
            return results;
        }

        private static ModuleApplyResult NotWired(string module)
            => new(module, true, "draft", "not-wired(阶段三接线)", null);
    }
}
