# INF-05：E03/E04 模块应用与生命周期缺口矩阵

- **日期**：2026-10-27
- **状态**：审计完成，待 G0 会签
- **范围**：设置保存/模块应用（E03）与应用运行时/生命周期（E04）
- **方法**：静态阅读生产代码 + 现有测试；本审计不修改代码，不把“接口存在”当作“已接线”
- **结论**：E03 的**保存失败反馈**与**模块级结果**尚未进入生产路径；E04 仍由 `MainForm` 兼任组合根，Web 服务器单例的停止/释放边界不完整。两项都必须作为 INF-06/INF-07 的独立工作包，不能由 R3/S04 顺带完成。

## 1. 现状路径

### 1.1 设置保存与应用

| 步骤 | 当前实现 | 证据 |
|---|---|---|
| 用户点“确定/应用” | `SettingsForm.ApplySettings()` 为 `void`；确定按钮直接 `DialogResult.OK; Close()` | `src/UI/SettingsForm.cs:173-175`、`:284` |
| 合并差异 | `SettingsChanger.Merge(_cfg, _draftCfg)`；保存前克隆 `before` | `src/UI/SettingsForm.cs:316-317` |
| 持久化 | `_cfg.Save()`；该扩展方法**静默**调用 `TrySave`，失败不返回给 UI | `src/Core/SettingsHelper.cs:469-472`、`:248-255` |
| 应用副作用 | `AppActions.ApplySettingsSelectively(...)` 返回摘要 `string`，调用方忽略返回值 | `src/UI/SettingsForm.cs:341`、`src/Core/Actions/AppActions.cs:73-93` |
| 模块分派器 | `SettingsApplyDispatcher` 已存在，但生产代码中没有调用点；仅定义/测试引用 | `src/Core/Actions/Services/ModuleServices.cs:126-174` |
| 未接线模块 | `NotWired` 返回 `Applied=true`、`EffectiveState="not-wired(阶段三接线)"` | `src/Core/Actions/Services/ModuleServices.cs:172-173` |
| 主题保存 | `AppActionsThemeApplyGateway.ApplySavedTheme` 已分 `SettingsPersisted/ThemeApplied` 阶段，但只覆盖主题路径 | `src/ThemeEditor/ThemeStudioLauncher.cs:48-105` |

### 1.2 应用组合与关闭

| 对象 | 创建/拥有 | 关闭/释放 | 证据 |
|---|---|---|---|
| `MainForm` | `Program.Main` 直接 `new MainForm()` | `Application.Run` 退出 | `src/System/Program.cs:100-110` |
| `UIController` | `MainForm` 构造函数 `new UIController(_cfg, this)` | `OnFormClosed` 调 `_ui.Dispose()` | `src/UI/MainForm_Transparent.cs:306`、`:752` |
| `LiteWebServer` | `MainForm` 构造函数 `new LiteWebServer(_cfg)`；类内 `Instance` 单例 | `OnFormClosed` 只 `Instance?.Stop()`，未见 `Dispose()` | `src/UI/MainForm_Transparent.cs:307`、`:746`；`src/System/WebServer/LiteWebServer.cs:62,397-400` |
| `MainFormBizHelper` | `MainForm` 构造函数创建并 `Initialize()` | `OnFormClosed` `Dispose()` | `src/UI/MainForm_Transparent.cs:338-339`、`:753` |
| `MainFormWinHelper` / `_windowAppearance` | `MainForm` 创建 | `_windowAppearance?.Dispose()`；WinHelper 未见显式释放 | `src/UI/MainForm_Transparent.cs:304`、`:756` |
| 硬件客户端 / GPU 呈现宿主 | 由 `UIController` 内部创建/持有，MainForm 不直接持有 | 依赖 `UIController.Dispose()` 的二级释放 | `src/UI/UIController.cs:20,2211` |

## 2. 缺口矩阵

| ID | 区域 | 当前行为 | 缺口/风险 | 目标工作包 | Owner | 验收 |
|---|---|---|---|---|---|---|
| E03-1 | 保存失败反馈 | `Settings.Save()` 静默失败；`ApplySettings()` 无返回值；确定按钮仍关闭 | 用户会认为已保存；重启后配置回退，且无错误码 | INF-06 + INF-08 | A | 注入磁盘失败：对话框不关闭/明确失败；`TrySave=false` 可断言 |
| E03-2 | 模块分派 | `SettingsApplyDispatcher` 无生产调用；`ApplySettingsSelectively` 直接按 `ApplyPlan` 调 UI/Web/Plugin | 无法得到每模块 `SavedState/EffectiveState/FailureReason`；无法部分成功 | INF-06 | A | Appearance/Taskbar/Sampling/Visibility 各返回 `ModuleApplyResult` |
| E03-3 | 未接线模块 | `NotWired` 返回 `Applied=true` | 假成功；诊断/场景会误判“已生效” | INF-06 | A | 未接线必须 `Applied=false`、`Retryable=true`、原因可读 |
| E03-4 | 主题应用 | 已有 `ThemeApplyResult` 三阶段；但 `SettingsForm` 不走该结果 | 主题保存成功/应用失败仍显示成功 | R3 + INF-06 | B/Core | 主题失败返回 `Partial/Failed`；C1 外层汇总 |
| E03-5 | Web/插件应用 | `ApplySettingsSelectively` 直接调用 `ApplyWebServer`/`PluginManager.Reload`；无结果 | Web 启动失败/插件重载失败不可见；无重试 | INF-06 | A | 每模块结果 + 可重试标记；失败不影响已成功模块 |
| E03-6 | 操作身份 | `ApplySettingsSelectively` 无 `OperationId`/`RequestRevision` | D01/S04 无法关联“这次保存”的事件与最终状态 | C1 + INF-06 | A | 一次保存一个 `OperationId`；结果带 revision |
| E03-7 | 场景/诊断消费 | D01 需要“配置应用结果” Provider；S04 需要模块结果 | 目前只能读 UI 摘要字符串或猜状态 | INF-06 → D01/S04 | A/B | Provider 只读 `ApplyOperationResult`；无结果时明确 Unknown |
| E04-1 | 组合根 | `MainForm` 构造函数创建 UI/Web/Helper；`Program` 只创建 MainForm | 无法在测试/无 UI 环境组装；所有权分散 | INF-07 | B | 单一 `AppRuntime`/组合根；MainForm 只做视图 |
| E04-2 | Web 服务器生命周期 | `new LiteWebServer(_cfg)` 设置单例；关闭只 `Stop()` | 未 `Dispose()`；单例跨关闭残留；测试可能串扰 | INF-07 | B | 启动/停止/释放成对；50 次开关无实例/句柄增长 |
| E04-3 | 关闭顺序 | `OnFormClosed` 先 `_cfg.Save()`，再 `StopLiveGlass`、`_ui.Dispose()`、`_bizHelper.Dispose()`、`_windowAppearance.Dispose()` | 保存失败被忽略；Web 停止在 UI 释放后；顺序未合同化 | INF-07 + INF-06 | B | 关闭顺序文档化并测试：停止入口→保存→释放订阅→释放 UI→释放服务器 |
| E04-4 | 订阅/事件 | `MainFormBizHelper.Initialize()`、`UIController` 内部订阅；静态 `AppActions` | 订阅归属不清；重复开关可能叠加 | INF-07 | B | 每个订阅有 owner/Dispose；50 次开关事件计数不增长 |
| E04-5 | GPU/硬件宿主 | `GpuPresentationHostController`、`HardwareClientAdapter` 由 UI 层二级持有 | 关闭时是否停宿主/杀进程未单点保证 | INF-07 | B | 退出后无子进程/无 GPU 会话残留；日志可核对 |
| E04-6 | 设置降级 | `Settings` 尚无 `[JsonExtensionData]`（C5 待落） | 旧版保存会删新字段；降级承诺不成立 | INF-08 | A | 旧版读写新字段后保存，未知字段仍在 |

## 3. 模块接线现状

| 模块 | 当前实现 | 生产接线 | 目标 |
|---|---|---|---|
| Sampling | `SamplingConfigApplier` 调 `IHardwareClient.ApplyConfig`，返回 `SamplingApplyResult` | 已有真实实现，但 `SettingsApplyDispatcher` 未调用 | INF-06 接入统一结果 |
| Appearance | `WindowAppearanceApplier` 委托壳 | 组合根未注入真实 UI 动作 | INF-06 注入并返回结果 |
| Taskbar | `TaskbarController` 委托壳 | 组合根未注入 | INF-06 |
| WebServer | `WebServerController` 委托壳 | 组合根未注入；实际 `AppActions.ApplyWebServer` 直调 | INF-06 |
| Plugins | `PluginRuntimeController` 委托壳 | 组合根未注入；实际 `PluginManager.Reload` 直调 | INF-06 |
| Theme | `ThemeStudioLauncher` 真实分阶段 | 仅主题路径；未汇总到 C1 | R3 |

## 4. 结论与顺序

1. **先做 INF-06**：把 `SettingsApplyDispatcher` 接入 `SettingsForm.ApplySettings`，让每个模块返回真实 `ModuleApplyResult`，保存失败不关闭对话框；这是 D01/S04 的共同前置。
2. **并行做 INF-07**：抽应用级 owner，先把 Web 服务器与 UI 订阅的创建/停止/释放成对，再收口 GPU/硬件宿主。
3. **R3 只做主题编排**：复用 C1 外层结果，不重新定义模块结果；S04 只消费 INF-06 的结果，不自己探测 UI。
4. 本审计不改变原计划范围；E03/E04 仍按执行计划 §7.1 的 INF-06/INF-07 估算（3–4 + 1.5–2.5 人日）。
