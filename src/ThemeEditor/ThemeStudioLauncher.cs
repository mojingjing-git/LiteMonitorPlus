using System;
using System.Linq;
using LiteMonitor.Core.Actions.Services;
using LiteMonitor.src.Core;
using LiteMonitor.src.Core.Actions;

namespace LiteMonitor.ThemeEditor;

/// <summary>
/// 主题工作室入口（重做计划 §8 接线）：
/// 从菜单注入打开与应用依赖。应用路径复用现有 <see cref="AppActions.ApplyThemeAndLayout"/>，
/// 不反射 MainForm 私有字段，不另建设置写入路径。
///
/// 过渡期（计划 §11）：Shift+点击菜单项仍打开旧编辑器，供内部对照与回滚。
/// </summary>
public static class ThemeStudioLauncher
{
    public static void Open(MainForm form, Settings cfg, UIController? ui)
    {
        var gateway = new AppActionsThemeApplyGateway(cfg, ui, form);
        var studio = new ThemeStudioForm(gateway, cfg);
        studio.Show(form);
    }

    /// <summary>主窗体应用网关：保存已由工作室完成，这里只负责让主界面生效（N06 三阶段细分）。</summary>
    internal sealed class AppActionsThemeApplyGateway : ThemeStudioForm.IThemeApplyGateway
    {
        private readonly Settings _cfg;
        private readonly UIController? _ui;
        private readonly MainForm _form;
        private readonly Func<
            Settings,
            UIController?,
            MainForm,
            long,
            ApplyOperationResult> _applyRuntime;
        private readonly Func<Settings, bool> _persistRollback;
        private readonly bool _hasInjectedRuntime;

        public AppActionsThemeApplyGateway(Settings cfg, UIController? ui, MainForm form)
            : this(
                cfg,
                ui,
                form,
                (settings, controller, mainForm, revision) =>
                    AppActions.ApplyThemeAndLayoutWithResult(
                        settings, controller, mainForm, revision: revision),
                hasInjectedRuntime: false,
                persistRollback: null)
        {
        }

        internal AppActionsThemeApplyGateway(
            Settings cfg,
            UIController? ui,
            MainForm form,
            Func<Settings, UIController?, MainForm, long, ApplyOperationResult> applyRuntime,
            Func<Settings, bool>? persistRollback = null)
            : this(
                cfg,
                ui,
                form,
                applyRuntime,
                hasInjectedRuntime: true,
                persistRollback: persistRollback)
        {
        }

        private AppActionsThemeApplyGateway(
            Settings cfg,
            UIController? ui,
            MainForm form,
            Func<Settings, UIController?, MainForm, long, ApplyOperationResult> applyRuntime,
            bool hasInjectedRuntime,
            Func<Settings, bool>? persistRollback)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _ui = ui;
            _form = form;
            _applyRuntime = applyRuntime ?? throw new ArgumentNullException(nameof(applyRuntime));
            _hasInjectedRuntime = hasInjectedRuntime;
            _persistRollback = persistRollback ?? (settings => settings.TrySave());
        }

        public ThemeStudioForm.ThemeApplyResult ApplySavedTheme(string themeName, long revision)
        {
            // 阶段 0：文件预检（R3/F02）。坏 JSON / 缺失文件不能先持久化再静默套缺省主题。
            string themePath = System.IO.Path.Combine(ThemeDocumentStore.ThemeDir, themeName + ".json");
            ThemeManager.ThemeLoadResult inspected = ThemeManager.Inspect(themePath, themeName);
            if (!inspected.Success)
            {
                return new ThemeStudioForm.ThemeApplyResult(
                    Success: false,
                    Stage: ThemeStudioForm.ThemeApplyStage.ThemeSaved,
                    Error: "主题文件无效，未写入配置：" + (inspected.Error ?? "未知错误"),
                    EffectiveTheme: ThemeManager.Current.Name,
                    Revision: revision
                );
            }

            // 阶段 1：Settings 持久化
            // ★ R2 整改（计划 §5.1 A-1）：Save() 故意静默（丢弃 TrySave 的 bool），
            //   失败既不抛也不返回 ⇒ 这里必须直接消费 TrySave 的结果，
            //   否则「保存失败」会被谎报成「已保存并应用」。
            string previousSkin = _cfg.Skin;
            bool persisted;
            try
            {
                _cfg.Skin = themeName;
                persisted = _cfg.TrySave();
            }
            catch (Exception ex)
            {
                _cfg.Skin = previousSkin;
                return new ThemeStudioForm.ThemeApplyResult(
                    Success: false,
                    Stage: ThemeStudioForm.ThemeApplyStage.SettingsPersisted,
                    Error: "配置持久化失败：" + ex.Message,
                    Revision: revision
                );
            }

            if (!persisted)
            {
                // 持久化未落盘：回滚内存值，绝不进入 ThemeApplied 阶段。
                _cfg.Skin = previousSkin;
                return new ThemeStudioForm.ThemeApplyResult(
                    Success: false,
                    Stage: ThemeStudioForm.ThemeApplyStage.SettingsPersisted,
                    Error: "配置持久化失败（可能被安全策略或磁盘写入阻止）。",
                    Revision: revision
                );
            }

            // 阶段 2：实际生效（同名文件变更强制重载，无按 Skin 跳过）
            try
            {
                ApplyOperationResult apply = _applyRuntime(_cfg, _ui, _form, revision);

                if (_ui is null && !_hasInjectedRuntime)
                {
                    // 没有运行时宿主 ⇒ 主题没有被真正应用，不能报「已生效」。
                    return new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        Error: "未应用（无运行时宿主）：主题已保存，但没有可渲染的运行时宿主。",
                        EffectiveTheme: _cfg.Skin,
                        EffectiveRoute: "未应用（无运行时宿主）",
                        Revision: revision,
                        Modules: apply.Modules
                    );
                }

                if (apply.OverallStatus == ApplyOperationStatus.Failed)
                {
                    ThemeStudioForm.SettingsRollbackState rollback = RollbackSkin(previousSkin);
                    return new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        Error: "主题运行时提交失败：" + string.Join("；", apply.Modules
                            .Where(m => !m.Applied)
                            .Select(m => m.Module + "：" + (m.FailureReason ?? "失败"))),
                        EffectiveTheme: previousSkin,
                        Revision: revision,
                        Modules: apply.Modules,
                        SettingsRollback: rollback
                    );
                }

                ThemeManager.ThemeLoadResult? actual = ThemeManager.LastLoad;
                if (actual is null || !actual.Success ||
                    !string.Equals(actual.RequestedName, themeName, StringComparison.Ordinal))
                {
                    ThemeStudioForm.SettingsRollbackState rollback = RollbackSkin(previousSkin);
                    return new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        Error: "运行时没有加载请求主题，当前实际主题：" + ThemeManager.Current.Name,
                        EffectiveTheme: ThemeManager.Current.Name,
                        EffectiveRoute: "未应用（请求主题未加载）",
                        Revision: revision,
                        Modules: apply.Modules,
                        SettingsRollback: rollback
                    );
                }

                if (apply.OverallStatus == ApplyOperationStatus.Partial)
                {
                    string failed = string.Join("；", apply.Modules
                        .Where(m => !m.Applied)
                        .Select(m => m.Module + "：" + (m.FailureReason ?? "失败")));
                    return new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Partial: true,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        Error: failed,
                        EffectiveTheme: ThemeManager.Current.Name,
                        EffectiveRoute: ResolveEffectiveRoute(themeName),
                        Revision: revision,
                        Modules: apply.Modules
                    );
                }

                return new ThemeStudioForm.ThemeApplyResult(
                    Success: true,
                    Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                    EffectiveTheme: _cfg.Skin,
                    EffectiveRoute: ResolveEffectiveRoute(themeName),
                    Revision: revision,
                    Modules: apply.Modules
                );
            }
            catch (Exception ex)
            {
                // R3/F02：主题可能已加载、后续菜单/任务栏阶段才失败；此时必须如实报 Partial，
                // 不能回滚 Skin 后声称旧主题仍在生效。
                ThemeManager.ThemeLoadResult? actual = ThemeManager.LastLoad;
                bool themeApplied = actual is { Success: true } &&
                    string.Equals(actual.RequestedName, themeName, StringComparison.Ordinal);
                if (themeApplied)
                {
                    return new ThemeStudioForm.ThemeApplyResult(
                        Success: false,
                        Partial: true,
                        Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                        Error: "主题已生效，但后续模块失败：" + ex.Message,
                        EffectiveTheme: ThemeManager.Current.Name,
                        EffectiveRoute: ResolveEffectiveRoute(themeName),
                        Revision: revision
                    );
                }

                // 新主题并未真正加载 ⇒ 仍然生效的是旧主题；回滚内存值并尽力把磁盘拉回一致。
                ThemeStudioForm.SettingsRollbackState rollback = RollbackSkin(previousSkin);
                return new ThemeStudioForm.ThemeApplyResult(
                    Success: false,
                    Stage: ThemeStudioForm.ThemeApplyStage.ThemeApplied,
                    Error: "运行时应用失败：" + ex.Message,
                    EffectiveTheme: previousSkin,
                    Revision: revision,
                    SettingsRollback: rollback
                );
            }
        }

        /// <summary>回滚到应用前皮肤；持久化失败或异常时如实上报未知/失败状态。</summary>
        private ThemeStudioForm.SettingsRollbackState RollbackSkin(string previousSkin)
        {
            _cfg.Skin = previousSkin;
            try
            {
                return _persistRollback(_cfg)
                    ? ThemeStudioForm.SettingsRollbackState.Restored
                    : ThemeStudioForm.SettingsRollbackState.Failed;
            }
            catch (Exception ex)
            {
                // 回滚状态"未知"本身就是需要诊断的场景，不能静默吞掉。
                try
                {
                    Diagnostics.DiagnosticHub.Current.Record(
                        "theme-studio",
                        Diagnostics.DiagnosticSeverity.Error,
                        "settings-rollback-failed: " + ex.Message);
                }
                catch
                {
                    // 诊断失败不得影响回滚路径。
                }

                return ThemeStudioForm.SettingsRollbackState.Unknown;
            }
        }

        /// <summary>
        /// 解析「实际生效路线」。
        /// <para>判定顺序：</para>
        /// <list type="number">
        ///   <item>主题本身是否请求 GPU —— 不请求则一律经典渲染，<b>与全局开关无关</b>
        ///     （旧规则只看开关，经典主题 + GPU 开关开启时会谎报「GPU 场景生效」）。</item>
        ///   <item>运行时是否真的在跑 GPU —— <c>IsGpuPresentationActive</c> 为真才是主效果路线。</item>
        ///   <item>降级标注 —— <c>GpuFallbackCode</c> / <c>GpuRuntimeFailure</c> 非空时在文案里带上码；
        ///     降级码只用于<b>标注</b>，不得用来反推路线。</item>
        /// </list>
        /// <para>
        /// 关于单一事实源：协调器 <c>RenderingTransitionCoordinator.ExpectedRoute/EffectiveRoute</c>
        /// 确实更权威，但它的 <c>PublishStable/PublishLegacyFallback</c> 是由 GPU 宿主在渲染线程
        /// 异步发布的（见 <c>UIController</c> 内的 Publish 调用点），本方法紧随
        /// <c>ApplyThemeAndLayout</c> 同步返回，此刻读取存在竞态 ⇒ 本批不采用。
        /// </para>
        /// </summary>
        private string ResolveEffectiveRoute(string themeName)
        {
            if (_ui is null)
            {
                return "未应用（无运行时宿主）";
            }

            // ① 主题是否请求 GPU：读「运行时实际应用的那个主题对象」。
            //    主题文件位于工作室的主题目录（生产恒等于 ThemeManager.ThemeDir）。
            bool themeRequestsGpu = false;
            try
            {
                Theme theme = ThemeFileService.LoadTheme(ThemeDocumentStore.ThemeDir, themeName);
                themeRequestsGpu = theme.Rendering?.IsGpuRequested == true;
            }
            catch (Exception)
            {
                // 主题文件缺失或解析失败：按「不请求 GPU」处理 —— 路线不得声称 GPU 生效。
                themeRequestsGpu = false;
            }

            string route;
            if (!themeRequestsGpu)
            {
                route = "经典渲染生效";
            }
            else if (_ui.IsGpuPresentationActive)
            {
                route = "GPU 场景生效";
            }
            else
            {
                route = "GPU 未生效（当前为经典渲染）";
            }

            // ③ 降级标注：只标注，不参与路线判定。
            string? fallbackCode = _ui.GpuFallbackCode;
            string? runtimeFailure = _ui.GpuRuntimeFailure;
            if (!string.IsNullOrEmpty(fallbackCode))
            {
                route += "（回退绘制：" + fallbackCode + "）";
            }
            else if (!string.IsNullOrEmpty(runtimeFailure))
            {
                route += "（" + runtimeFailure + "）";
            }

            return route;
        }
    }
}
