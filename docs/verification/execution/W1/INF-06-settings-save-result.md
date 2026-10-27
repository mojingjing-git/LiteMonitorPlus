# INF-06 保存事务与模块结果接线

- **日期**：2026-10-27（Asia/Shanghai）
- **状态**：完成（保存失败路径 + 模块结果汇总）；真实 UI 冒烟验收待 G1 前执行
- **对应缺口**：INF-05 矩阵 E03-1（保存失败仍合并 Live）、E03-2（模块结果未进生产路径）

## 1. 变更

| 文件 | 变更 |
|---|---|
| `src/UI/SettingsForm.cs` | `ApplySettings()` 改为 `bool`；先 `candidate.TrySave()`，失败返回 `false`，不合并 Live；`btnOk` 仅在成功时关闭；`btnApply` 保持只应用 |
| `src/Core/Actions/AppActions.cs` | 新增 `ApplySettingsSelectivelyWithResults`；旧 `ApplySettingsSelectively` 委托新入口，保持调用方兼容；UI 组、Web、插件分别返回 `ModuleApplyResult` |
| `resources/lang/*.json` | 新增 `SettingsApplyPartial`，9 种语言同步 |
| `tests/LiteMonitor.Tests/SettingsApplyResultGuardTests.cs` | 源码级护栏：保存先于合并、失败返回、结果入口、九语言 key |

## 2. 行为边界

- 保存失败：磁盘、Live 配置、界面均保持原状；对话框不关闭；不触发任何应用副作用。
- 保存成功但应用失败：磁盘已是新配置，`ModuleApplyResult` 明确 `Applied=false`，用户看到 `SettingsApplyPartial` 与失败模块；不自动回滚已成功模块。
- UI 重建组按组报告：组内任一步异常时，该组涉及的模块统一记失败；这是首版粒度，单子步骤级归因留给后续诊断事件。Web/插件独立报告。
- 模块结果当前不落盘；由 UI 即时提示，后续诊断中心 D02 接入事件出口。

## 3. 证据

| 文件 | 结果 |
|---|---|
| `docs/verification/execution/G0/inf06-save-failure-build.log` | Debug 构建 exit 0 |
| `docs/verification/execution/G0/inf06-module-results-build.log` | Debug 构建 exit 0；44 warnings / 0 errors |
| `docs/verification/execution/G0/inf06-settings-tests.log` | E00/E05 回归 27/27 通过 |
| `docs/verification/execution/G0/inf06-module-results-tests.log` | INF-06 护栏 3/3 通过 |
| `docs/verification/execution/G0/inf06-contract-tests.log` | 合同 + 采样 + 护栏 53/53 通过 |

## 4. 剩余限制

- 尚未做真实 SettingsForm 手工冒烟（保存失败注入、模块应用异常注入）；G1 前必须在物理会话补做。
- UI 组内子模块失败目前是组级归因，不能精确到 Appearance/Taskbar/Sampling 的单步异常；不阻塞保存事务，但影响诊断精度。
- `SettingsApplyDispatcher` 仍未接入生产路径；当前由 `AppActions` 直接返回结果。E03 的完整 dispatcher 统一留给 INF-06b/INF-07，避免在保存事务里同时改两套编排。
