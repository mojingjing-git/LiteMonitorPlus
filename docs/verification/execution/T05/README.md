# T05 告警引擎与迁移 — 证据

- 日期：2026-10-27。
- 状态：T05 核心完成；T06 通知/设置/事件接线与 T07 长时验收未开始，G3 未满足。
- 构建：`t05-build.log`，`LiteMonitor.csproj` Release，0 warnings / 0 errors。
- 测试：`t05-tests.log`，`FullyQualifiedName~AlertEvaluatorTests`，10/10 通过。

## 已落地

- `src/Core/Alerts/AlertModels.cs`
  - `AlertDirection`、`AlertState`、`AlertEventKind`、`AlertNotificationDecision`。
  - `AlertRule`：规则 ID、指标键、方向、触发/恢复阈值、持续时间、最大采样间隔、冷却、静音、提醒间隔。
  - `AlertSample`、`AlertEvent`、`ActiveAlertState`。
- `src/Core/Alerts/AlertEvaluator.cs`
  - 输入为采集批次 + 观测时刻；同一批次重复、乱序、会话变化均不推进状态。
  - 触发需连续满足 `TriggerDuration`；采样间隔超过 `MaxSampleInterval` 视为不连续，重新累计。
  - 恢复使用 `RecoveryThreshold` + `RecoveryDuration` 回差；触发与恢复之间不抖动。
  - `IncidentId` 在一次 Active 生命周期内稳定；恢复后清空。
  - 多设备按 `MetricKey + DeviceId` 独立状态；冷却按设备独立。
  - 输入不可用保留原 Incident，恢复后发 `InputRestored`，不重复发 `Triggered`。
  - 冷却只抑制通知决策（`SuppressedByCooldown`），事件仍写入；静音同理（`SuppressedByMute`）。
- `src/Core/Alerts/AlertRuleMigration.cs`
  - 旧 `AlertTempEnabled` / `AlertTempThreshold` / 磁盘公式迁移为 CPU/主板/磁盘/每 GPU 规则。
  - 幂等（`AlertRulesVersion`）；旧告警关闭时生成禁用规则，不擅自启用。
- `src/Core/Settings.cs`
  - `Load()` 末尾调用迁移，使用 `MonitoredGpuCount` 生成 GPU 规则。

## 尚未完成（G3 前置）

- T06：真实通知出口、静音/设置页、诊断事件接线、旧告警路径撤除；新旧执行器不得同时通知。
- T07：24h 等效长跑、实机资源/多消费者验收、全量回归。
- 旧 `UIController.CheckTemperatureAlert` 仍保留（计划允许到 T06 验收后撤除）；当前规则引擎尚未接生产通知路径。

## 验收边界

- 本轮只证明状态机/迁移合同与定向测试，不证明真实通知、UI 或长时稳定性。
- 迁移不会自动写盘；用户下一次保存设置后持久化 `AlertRules`，未保存时下次启动会再次幂等迁移。
