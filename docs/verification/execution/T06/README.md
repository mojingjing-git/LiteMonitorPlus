# T06 告警通知/设置/事件接线 — 证据

- 日期：2026-10-27。
- 状态：T06 核心完成；T07 长时/资源验收未开始，G3 未满足。
- 构建：`t06-build.log`，`LiteMonitor.csproj` Release，0 warnings / 0 errors。
- 测试：`t06-tests.log`，`AlertEvaluatorTests` + `AlertRuntimeTests` + `LanguagePackTests`，29/29 通过。
- 全量回归：本次启动后 testhost 长时间无进度，已取消；`t06-full-tests.log` 只有启动头。不能据此宣称全量通过，留 T07/全量门禁重跑。

## 已落地

- `src/Core/Alerts/AlertRuntime.cs`
  - 一次采集批次统一递增 `BatchId`，避免多规则各自计时。
  - `AlertRules` 列表引用替换时重置状态机，防止旧阈值/旧事件残留。
- `src/UI/UIController.cs`
  - `CheckRuleAlerts()` 取代旧 `CheckTemperatureAlert()`；旧执行器已移除，不存在双通知路径。
  - 规则事件写 `DiagnosticHub`（module=`alerts`，含 ruleId/deviceId/state/notification/reason/incidentId）。
  - 仅 `Notification == Notify` 且未全局静音时调用托盘通知；通知失败不影响状态机。
- `src/Core/Settings.cs`
  - 新增 `AlertMuteAll`（全局静音，不停止评估/诊断）。
  - `RebuildMigratedAlertRules()`：旧温度开关/阈值改变时重建迁移规则。
- `src/UI/Settings/ThresholdPage.cs`
  - 温度开关/阈值变更后同步规则；新增“静音告警”开关。
- `resources/lang/*.json`：9 语言补 `Menu.AlertMute`。

## 验收边界

- 真实托盘通知、系统通知中心行为、长时间多规则并发仍需 T07 实机验收。
- 逐规则编辑（单条静音/阈值）尚未提供 UI；当前通过旧温度设置重建迁移规则，全局静音覆盖“所有通知”。
- 未证明真实硬件下 24h 稳定性；G3 仍需 T07。
