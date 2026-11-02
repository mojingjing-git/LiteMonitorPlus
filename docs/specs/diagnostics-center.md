# 诊断中心规格

本文描述 LiteMonitor++ 中诊断中心的当前架构：诊断事件模型与有界事件存储、诊断事实快照与模块提供程序契约、应用级诊断出口、诊断包收集与本地导出、脱敏与隐私边界，以及诊断设置页的交互契约。

适用范围：

- `src/Diagnostics/`：`DiagnosticEvent`、`DiagnosticSeverity`、`DiagnosticEventStore`、`DiagnosticHub`、`DiagnosticSnapshot`、`DiagnosticModuleSnapshot`、`DiagnosticModuleState`、`IDiagnosticProvider`、`DiagnosticSnapshotBuilder`、`DelegateDiagnosticProvider`、`HardwareDiagnosticProvider`、`ApplyResultDiagnosticProvider`、`RenderingDiagnosticProvider`、`WebDiagnosticProvider`、`PluginDiagnosticProvider`、`IpcDiagnosticProvider`、`DiagnosticRedaction`、`DiagnosticPackageService`、`DiagnosticPackageInput`、`DiagnosticPackagePreview`、`DiagnosticPackageExportResult`、`DiagnosticRunMarker`
- `src/UI/Settings/DiagnosticPage.cs`：诊断设置页
- `src/UI/MainForm_Transparent.cs`：诊断接线（提供程序注册、启动事件、运行标记、退出标记）
- `src/UI/SettingsForm.cs`：诊断页入口
- `src/Core/Actions/AppActions.cs`：应用结果事件写入点
- `src/UI/UIController.cs`：报警状态迁移事件写入点

不在本文范围内：硬件采集策略（见 `docs/specs/hardware-sampling.md`）、报警规则语义与趋势缓冲（见 `docs/specs/trends-alerts.md`）、主题与渲染路线、插件市场。诊断数据不通过 Web 服务暴露。

---

## 1. 模块边界与依赖方向

`src/Diagnostics` 编译进主程序集 `LiteMonitor.csproj`，命名空间 `LiteMonitor.Diagnostics`。

| 关注点 | 归属类型 |
|---|---|
| 事件模型 | `DiagnosticEvent`、`DiagnosticSeverity` |
| 事件存储 | `DiagnosticEventStore` |
| 事件出口 | `DiagnosticHub` |
| 事实快照 | `DiagnosticSnapshot`、`DiagnosticModuleSnapshot`、`DiagnosticModuleState` |
| 模块提供程序 | `IDiagnosticProvider`、`DiagnosticSnapshotBuilder`、`DelegateDiagnosticProvider` 与各内置 Provider |
| 脱敏 | `DiagnosticRedaction` |
| 诊断包 | `DiagnosticPackageService`、`DiagnosticPackageInput`、`DiagnosticPackagePreview`、`DiagnosticPackageExportResult` |
| 运行标记 | `DiagnosticRunMarker` |

诊断层只负责采集、聚合与投影"可解释事实"，不拥有业务决策，也不拥有界面绘制。

依赖方向：

- 诊断层读取 `src/Contracts` 的采集契约（`HardwareHealth`、`HardwareSnapshot`、`SampleQuality`、`HardwareState`）与 `src/Core` 的应用结果模型（`ApplyOperationResult`）。
- 渲染诊断读取的 `RenderingRouteStatusSnapshot` 是 `src/UI/Settings/RenderingRouteStatusBuilder.cs` 中的纯数据记录，不引入 WinForms 依赖。
- UI 层依赖诊断层（`src/UI/Settings/DiagnosticPage.cs`、`src/UI/MainForm_Transparent.cs`）；诊断层不得反向引用窗体、控件或 `UIController`。
- 诊断层不采集硬件、不读设置、不访问网络；外部事实一律由调用方以委托或已发布快照的形式注入。

⇒ 打开、刷新或关闭诊断中心都不增加采样次数，也不加载第二个采集器。

---

## 2. 诊断事件模型

### 2.1 字段

`DiagnosticEvent` 为不可变记录：

| 字段 | 类型 | 说明 |
|---|---|---|
| `UtcTime` | `DateTimeOffset` | 事件时刻。调用方显式给出时采用调用方时间（如报警状态迁移的观测时刻），否则取记录时刻 |
| `Module` | `string` | 来源模块标识，非空 |
| `Severity` | `DiagnosticSeverity` | 严重程度 |
| `ReasonCode` | `string` | 稳定原因码，非空 |
| `OperationId` | `string?` | 关联一次操作（应用操作、硬件重扫等）的标识 |
| `SessionId` | `string` | 记录该事件的进程会话标识，由诊断出口分配 |
| `Context` | `IReadOnlyDictionary<string, string>?` | 可公开的结构化上下文，入库前必须过允许列表 |
| `EventId` | `long` | 由 `DiagnosticEventStore` 单调分配，调用方传入 `0` 即可 |
| `RepeatCount` | `int` | 合并计数，默认 `1` |

### 2.2 严重程度

`DiagnosticSeverity` 取值 `Info`、`Warning`、`Error`、`Critical`。数值只用于界面排序，不构成稳定协议；跨版本比较必须比较枚举名。

### 2.3 来源分类

事件与事实共用同一个 `Module` 标识空间：

| 标识 | 类别 | 含义 |
|---|---|---|
| `app` | 事件来源 | 进程启动与上次运行结果 |
| `sampling` | 事件来源与事实模块 | 采样健康、样本质量、手动重扫结果 |
| `apply` | 事件来源与事实模块 | 设置 / 主题 / 场景的应用结果 |
| `alerts` | 事件来源 | 规则状态迁移 |
| `rendering` | 事实模块 | 实际渲染路线与回退原因码 |
| `web` | 事实模块 | 内置 Web 服务状态 |
| `plugins` | 事实模块 | 插件实例计数 |
| `ipc` | 事实模块 | 与硬件采样子进程的传输健康 |

`Module` 是自由字符串：写入方给出的标识即使不在上表中也可以入库，界面与诊断包按原始字符串展示。

### 2.4 写入时机

| 写入点 | 模块 | 严重程度 | 原因码 | 上下文键 | 时刻来源 |
|---|---|---|---|---|---|
| `MainForm_Transparent` 构造函数 | `app` | `Info` | `started` | 无 | 记录时刻 |
| `MainForm_Transparent` 构造函数（存在运行标记时） | `app` | `Info` / `Warning` | `previous-run-clean` / `previous-run-unclean` | `startedAtUtc`、`endedAtUtc` | 记录时刻 |
| `AppActions.RecordApplyDiagnostic` | `apply` | 由 `ApplyOperationStatus` 映射 | `apply-applied` / `apply-not-applied` | `status`、`revision`、`count`、`error_code` | 记录时刻 |
| `UIController` 报警状态迁移 | `alerts` | 由 `AlertEventKind` 映射 | `alert_triggered` / `alert_recovered` / `alert_inputunavailable` / `alert_inputrestored` / `alert_reminder` | `ruleId`、`deviceId`、`state`、`notification`、`reason`、`incidentId` | `AlertTransition.ObservedAt` |
| `DiagnosticPage` 手动重扫 | `sampling` | `Info` / `Warning` / `Error` | `manual-rescan` / `manual-rescan-rejected` / `manual-rescan-failed` | `operationId`、`state`、`failureReason`、`error` | 记录时刻 |

严重程度映射：

- 应用结果：`Applied` → `Info`；`Partial`、`Pending`、`Cancelled` → `Warning`；`Failed` → `Error`。
- 报警迁移：`Triggered`、`InputUnavailable`、`Reminder` → `Warning`；`Recovered`、`InputRestored` → `Info`。

写入时机的不变量：

- 事件只承载状态迁移与必要故障摘要。不得记录逐帧数据、单个传感器读数与正常轮询。
- 事件写入不得阻断业务路径：写入点必须自行捕获诊断异常并吞掉（`AppActions.RecordApplyDiagnostic` 与 `MainForm_Transparent` 的接线块均如此）。
- 上下文键必须来自允许列表；不在列表中的键在入库前被整键丢弃（见第 7 节）。⇒ `app` 的 `startedAtUtc` / `endedAtUtc` 与 `sampling` 的 `failureReason` / `error` 不会出现在事件、界面或诊断包中。

### 2.5 合并与计数

相同故障在合并窗口内合并为一条记录，`RepeatCount` 累加；界面与诊断包显示计数，不重复列举同一故障。

---

## 3. 事件存储

### 3.1 容量与边界

`DiagnosticEventStore` 全部驻留进程内存，读写在同一把锁内完成。

| 约束 | 默认值 | 越界行为 |
|---|---|---|
| 事件条数上限 | `capacity` = 1024 | 淘汰最旧一条，`DroppedCount` 加一 |
| 上下文总量上限 | `maxTotalContextBytes` = 2 MiB | 先淘汰最旧直到可容纳；仍放不下则拒绝，`RejectedCount` 加一 |
| 单条上下文上限 | `maxSingleContextBytes` = 2 KiB | 直接拒绝，`RejectedCount` 加一 |
| 合并窗口 | `mergeWindow` = 5 s | 超出窗口不合并 |

构造函数参数校验：`capacity` 与 `maxTotalContextBytes` 必须为正；`maxSingleContextBytes` 必须为正且不大于 `maxTotalContextBytes`；否则抛出 `ArgumentOutOfRangeException`。

`Add` 不抛出业务异常：超限只返回 `false` 并计数。

### 3.2 滚动与合并规则

- 事件按写入顺序追加，最新在尾部。达到条数或字节上限时从头部淘汰最旧的一条并累加 `DroppedCount`。
- 合并判据（全部满足才合并）：`Module`、`Severity`、`ReasonCode`、`OperationId`、`SessionId` 相同；身份键 `ruleId` / `rule_id`、`deviceId` / `device_id`、`incidentId` / `incident_id`、`eventKind` / `event_kind`、`batchId` / `batch_id` 取值相同；新事件时刻不早于已存事件，且时间差不超过合并窗口。
- 合并后保留首个事件的 `EventId`，`RepeatCount` 加一，上下文替换为最新一次的值。
- `EventId` 由存储单调分配（从 1 起递增），淘汰后不复用 ⇒ 序号可用于同进程内的稳定排序。
- 入库前上下文必须经过 `DiagnosticRedaction.Filter`，存储内不保留允许列表之外的键。

### 3.3 落盘与保留

| 产物 | 位置 | 保留策略 | 大小上限 |
|---|---|---|---|
| 诊断事件 | 仅进程内存 | 随进程退出消失，不落盘 | 1024 条 / 上下文合计 2 MiB |
| 上次运行标记（`DiagnosticRunMarker`） | `%LOCALAPPDATA%\LiteMonitor++\diagnostics\last-run.json` | 单文件覆盖写入，只保存最近一次运行 | 固定小文件（单个 JSON 对象） |
| 诊断包 | 导出对话框中用户选择的路径 | 由用户管理 | JSON 载荷 10 MiB |

运行标记语义：启动时 `DiagnosticRunMarker.ReadAndBegin` 读出上一次运行结果，并写入新的"未干净结束"标记；退出时 `DiagnosticRunMarker.MarkCleanShutdown` 把标记改写为"干净结束"。标记内容为 `CleanShutdown`、`StartedAtUtc`、`EndedAtUtc`。写入采用临时文件加覆盖移动，读写失败一律静默 ⇒ 标记故障既不阻断启动也不阻断退出。

`%LOCALAPPDATA%\LiteMonitor++\gpu-diagnostics.log` 由 `UIController.RecordGpuDiagnosticsIfChanged` 在渲染路线状态变化时追加，超过 512 KB 时清空重写。它属于渲染排障日志，不属于诊断中心的事件流，也不进入诊断包。

---

## 4. 诊断事实快照与模块提供程序

### 4.1 快照模型

`DiagnosticModuleState` 取值 `Ok`、`Warning`、`Error`、`Unknown`、`NotSupported`。

`DiagnosticModuleSnapshot` 字段：`Module`、`State`、`ReasonCode`、`Summary`、`ObservedAtUtc`、`Facts`。
`DiagnosticSnapshot` 字段：`CapturedAtUtc`、`Modules`，并提供 `Find(module)` 按模块名查找。

不变量：

- 各模块保留自己的 `ObservedAtUtc`，快照不声称跨模块原子 ⇒ 界面不得把多个模块的读数表述为同一瞬间的一致状态。
- 取不到事实时状态必须为 `Unknown`（`DiagnosticModuleSnapshot.Unknown`），不得用正常状态掩盖缺失。
- `Facts` 只放可公开的结构化字段，不得放入路径、令牌、原始请求 / 响应与设备序列号。

### 4.2 提供程序契约

```csharp
public interface IDiagnosticProvider
{
    string Module { get; }
    DiagnosticModuleSnapshot Read();
}
```

实现约束：

- `Read` 必须只读且无副作用：不得获取 LHM 对象锁、不得等待硬件命令、不得扫描磁盘或网络、不得触发采样。
- `Read` 只读取已发布的状态（健康快照、最新快照、统计计数、服务状态）。
- `Read` 不得返回 `null`；返回 `null` 时聚合器按未知处理。

`DelegateDiagnosticProvider` 把只读委托包装成提供程序，适用于没有专用窄接口的模块。

`DiagnosticSnapshotBuilder` 负责聚合：

- 单个提供程序抛异常时，该模块记为 `Unknown`、原因码 `provider-failed`，`Summary` 只包含异常类型名；其余模块不受影响。
- 提供程序返回 `null` 时，该模块记为 `Unknown`、原因码 `provider-null`。

### 4.3 内置提供程序

| 类型 | `Module` | 事实键 |
|---|---|---|
| `HardwareDiagnosticProvider` | `sampling` | `state`、`progressSeq`、`batchId`、`catalogVersion`、`configRevision`、`sampleCount`、`samplingDurationMs`、`operation`、`diagnosticReason`、`good`、`stale`、`missing`、`error`、`maxSampleAgeMs`；有采样统计时追加 `collectionRounds`、`failedCollections`、`skippedRequests`、`sharedRequests` |
| `ApplyResultDiagnosticProvider` | `apply` | `operationId`、`overallStatus`、`requestRevision`、`moduleCount`、`effectiveRevision`、`failureCode`、`module.<模块名>` |
| `RenderingDiagnosticProvider` | `rendering` | `themeUsesGpuMaterial`、`gpuEffectsEnabled`、`presentationActive`、`liveBackgroundActive`、`liveBackgroundRequested`、`windowIsLayered`、`routeLines`、`failureOrFallbackCode` |
| `WebDiagnosticProvider` | `web` | `enabled`、`running`、`port`、`authenticatedConnections`、`hasWebDataDemand` |
| `PluginDiagnosticProvider` | `plugins` | `configuredInstances`、`enabledInstances` |
| `IpcDiagnosticProvider` | `ipc` | `healthInstrumented` |

采集模块的状态映射：

| 采集状态 | 诊断状态 | 原因码 |
|---|---|---|
| `Ready` 且有样本且无错误样本 | `Ok` | 无 |
| `Ready` 的其余情况 | `Warning` | `snapshot-empty` 或 `sample-errors` |
| `Degraded` | `Warning` | `hardware-degraded` |
| `Unresponsive` | `Error` | `hardware-unresponsive` |
| `Stopped` / `Stopping` | `Error` | `hardware-stopped` |
| 其它 | `Warning` | `hardware-starting` |

配置应用模块的状态映射：`Applied` → `Ok`；`Partial`、`Pending`、`Cancelled` → `Warning`；`Failed` → `Error`；没有最近一次应用记录时为 `Unknown`、原因码 `no-apply-operation`。

### 4.4 注册方式

提供程序集合在组合根装配：`MainForm_Transparent` 构造函数把 `DiagnosticHub.Current.SnapshotProvider` 赋为一个 `DiagnosticSnapshotBuilder` 的调用，该 Builder 的提供程序数组即为当前注册的模块集合（`sampling`、`rendering`、`web`、`plugins`、`ipc`）。

新增模块提供程序需要满足：实现 `IDiagnosticProvider`（或用 `DelegateDiagnosticProvider` 包装只读委托）；实例加入组合根的提供程序数组；该模块的事实键取得诊断包允许列表中的位置（允许列表内的固定键，或 `module.` 前缀键）。

注册约束：提供程序不得持有窗体或控件引用；`Read` 必须在 UI 线程上快速返回；提供程序不得修改被观测系统的状态。

`DiagnosticHub.Current.SnapshotProvider` 的默认值返回空模块集合 ⇒ 未接线时诊断页显示空列表而不抛异常。

---

## 5. 诊断出口与消费模型

### 5.1 出口

`DiagnosticHub.Current` 是诊断模块对外的唯一出口（`Lazy` 单例，线程安全）。它持有三样东西：

- `Store`：`DiagnosticEventStore`，事件存储。
- `SessionId`：进程会话标识，默认 `Guid.NewGuid().ToString("N")`，随事件一同记录 ⇒ 同一进程内的事件可归属到同一次会话。
- `SnapshotProvider`：`Func<DiagnosticSnapshot>`，事实快照来源，可由组合根替换。

写入接口：

```csharp
bool Record(string module,
            DiagnosticSeverity severity,
            string reasonCode,
            string? operationId = null,
            IReadOnlyDictionary<string, string>? context = null,
            DateTimeOffset? utcTime = null);
```

- `module` 或 `reasonCode` 为空白时抛出 `ArgumentException`。
- 返回值即 `DiagnosticEventStore.Add` 的结果：`true` 表示事件已入库或已合并进既有事件，`false` 表示因超限被拒绝。
- 不传 `utcTime` 时取记录时刻。

### 5.2 分发与解耦

诊断中心采用拉取式分发：诊断模块不提供回调注册接口，消费者不得挂在事件写入路径上。

| 消费者 | 读取方式 | 节奏 |
|---|---|---|
| 诊断设置页 | `DiagnosticHub.Current.SnapshotProvider()` 取事实，`DiagnosticHub.Current.Store.Snapshot()` 取事件 | 页面可见时 1 Hz |
| 诊断包导出 | 同一对入口，事件与快照取自同一次刷新 | 用户触发 |
| 其它业务模块 | `DiagnosticHub.Current.Record(...)` 写入 | 写入方自选 |

由此得到的不变量：

- 消费者之间零耦合：任一消费者的异常不会传播到事件写入方，写入方也不感知消费者是否存在。
- 关闭诊断页只释放页面自身资源（定时器），不释放 `DiagnosticHub`、不清空事件、不影响其它消费者。
- 事件写入路径不执行 UI、磁盘或网络操作 ⇒ 记录一条事件的成本是常数级内存操作。

### 5.3 其它模块的写入义务

诊断事件是应用级留痕出口之一（与硬件错误日志、未处理异常日志并列，见 `docs/architecture/engineering-rules.md` 的异常留痕出口表）。凡是发生状态迁移或可预期失败并需要事后追溯的模块都应写入事件，并遵守第 2 节的写入约束与第 7 节的脱敏约束。

---

## 6. 诊断包收集与导出

### 6.1 输入与职责

`DiagnosticPackageService` 只做投影与打包：不采集硬件、不读设置、不访问网络。快照与事件由调用方在同一次刷新中取得后传入。

`DiagnosticPackageInput` 字段：`Snapshot`、`Events`、`AppVersion`、`DroppedCount`、`RejectedCount`、`GeneratedAtUtc`。
`DiagnosticPackagePreview` 字段：`FileName`、`EstimatedBytes`、`EventCount`、`ModuleCount`、`Warnings`。
`DiagnosticPackageExportResult` 字段：`Success`、`FilePath`、`Bytes`、`ErrorCode`。

### 6.2 导出格式

导出产物是 ZIP，包含两个条目：

- `diagnostic.json`：JSON 载荷（camelCase 命名，属性不缩进）。顶层字段 `schema`（固定值 `litemonitor.diagnostics/1`）、`appVersion`、`generatedAtUtc`、`summary`、`snapshot`、`events`；`summary` 含 `eventCount`、`moduleCount`、`droppedCount`、`rejectedCount`；`snapshot` 含 `capturedAtUtc` 与 `modules`；`events` 为事件数组。枚举（`DiagnosticSeverity`、`DiagnosticModuleState`）以数值写出。
- `summary.txt`：人读摘要，逐行给出 `schema`、`appVersion`、`generatedAtUtc`、`modules`、`events`、`dropped`、`rejected`、`jsonBytes`，末行固定为 `privacy=allowlist-projected; no credentials, paths or raw URLs`。

默认文件名 `LiteMonitor-diagnostics-<yyyyMMdd-HHmmss>.zip`，时间戳取 `GeneratedAtUtc`（缺省为 UTC 当前时刻）。

### 6.3 同源预览

`CreatePreview` 与 `Export` 共用同一个 JSON 构建入口 ⇒ 预览展示的内容与最终写入文件的内容来自同一模型与同一套过滤器，不存在"预览安全而文件多出内容"的情况。

预览 `Warnings` 取值：

| 警告串 | 含义 |
|---|---|
| `events-dropped:<n>` | 事件存储因达到上限淘汰了 n 条 |
| `events-rejected:<n>` | 事件存储因超限拒绝了 n 条 |
| `package-too-large` | JSON 载荷超过 10 MiB 上限 |

`EstimatedBytes` 是 JSON 载荷长度，不是压缩后的 ZIP 长度。

### 6.4 写入与失败语义

- 导出先取预览；载荷超过 `MaxPackageBytes`（10 MiB）时直接返回失败，不写文件。
- 写入路径：先在目标目录创建临时文件 `<目标路径>.tmp-<随机串>`，写入 ZIP 后用 `File.Move(overwrite: true)` 覆盖目标 ⇒ 目标路径要么是完整包，要么保持原状，不会出现半成品。
- 失败时删除导出过程产生的临时文件。

稳定错误码：

| 错误码 | 触发条件 |
|---|---|
| `package-too-large` | JSON 载荷超过 10 MiB |
| `invalid-path` | 目标路径没有可用的父目录 |
| `write-failed` | 写入过程抛出 `IOException`、`UnauthorizedAccessException` 或 `NotSupportedException` |

失败结果只返回错误码，不把异常原文或路径带进界面。取消（`OperationCanceledException`）会删除临时文件并向上抛出。

### 6.5 内容投影

诊断包对事实与事件执行两层投影：

- 事实：键必须在允许列表内（`state`、`batchId`、`catalogVersion`、`configRevision`、`moduleCount`、`operationId`、`overallStatus`、`progressSeq`、`requestRevision`、`sampleCount`、`samplingDurationMs`、`effectiveRevision`、`failureCode`、`good`、`stale`、`missing`、`error`、`maxSampleAgeMs`、`sharedRequests`、`skippedRequests`、`collectionRounds`、`failedCollections`、`operation`、`diagnosticReason`）或以 `module.` 开头；值还要通过安全值检查。
- 事件：`Module`、`ReasonCode`、`OperationId` 作为文本投影，命中敏感特征时整体替换为 `<redacted>`；`Context` 再次经过允许列表过滤。

诊断包不上传，也不提供网络发送入口。

---

## 7. 隐私边界

### 7.1 不得进入诊断的数据

下列数据不得出现在事件上下文、模块事实、诊断包或任何导出物中：

- 口令、令牌、密钥、Cookie、授权头等凭据材料。
- 硬件序列号、BIOS 与设备唯一标识一类的设备身份原值。
- 磁盘路径与含用户名的路径片段、UNC 路径。
- 内外网 IP 地址。
- 带查询参数的 URL 与任何请求 / 响应原文。
- 完整设置文件、插件输入值、Web 会话内容、桌面截图、系统级日志原文。

### 7.2 上下文允许列表

事件上下文按键允许列表过滤，不在列表中的键整条丢弃（含其值）。允许的键：

`stage`、`status`、`reason`、`route`、`device_kind`、`revision`、`count`、`duration_ms`、`source`、`error_code`、`window_state`、`gpu_state`、`web_state`、`profile_id`、`operation_id`、`ruleId` / `rule_id`、`deviceId` / `device_id`、`incidentId` / `incident_id`、`eventKind` / `event_kind`、`batchId` / `batch_id`、`state`、`notification`。

键名大小写不敏感。

### 7.3 值级脱敏

允许列表内的键，其值仍要通过敏感特征判定；命中任一特征时该键值对被整条丢弃：

- 出现 `bearer `、`authorization`、`cookie`、`password`、`secret`、`token`、`api_key`、`api-key`（大小写不敏感）。
- 出现 `http://` 或 `https://`。
- 出现 `c:\` 或 `\\`（UNC）。
- 匹配 Windows 绝对路径或 Unix 绝对路径。
- 匹配 IPv4 地址；匹配 IPv6（完整或压缩写法）。
- 可被 `IPAddress.TryParse` 解析出的 IP 字面量。

误判防护：只有同时含 `.` 或 `:` 且形如四段点分（`a.b.c.d`）的候选才走 IP 解析 ⇒ `revision`、`count`、`duration_ms` 这类数值不会被当成地址丢弃。

`DiagnosticRedaction.IsSafeValue` 是同一判定的对外入口，供诊断包的事实投影复用。

### 7.4 效果

- 脱敏在事件入库前执行（`DiagnosticEventStore.Add`）⇒ 存储、界面与诊断包看到的都是同一份已脱敏内容。
- 被丢弃的是整个键值对，而不是替换成代号 ⇒ 诊断包内不存在可还原的映射表。
- 设备身份在诊断事实中只以非敏感计数或原因码形式出现；报警事件中的 `deviceId` 是规则命中的标识值，同样必须是不含路径与地址形态的值。

### 7.5 脱敏的边界

脱敏是键与值的形态判定，不是语义判定：写入方不得把敏感内容塞进允许列表的键（例如把口令写进 `reason`）。⇒ 写入方有义务只把可公开的结构化事实放进上下文。

---

## 8. 诊断设置页契约

- 入口：设置窗口导航项 `Diagnostics`（`src/UI/SettingsForm.cs`），与其它设置页同级。
- 布局：模块列表与模块详情（事实键值）、事件时间线（时刻、严重程度、模块、原因码、计数）、过滤框、命令按钮 `Refresh`、`Rescan`、`Export…`。
- 过滤：模块侧按模块名、状态、原因码、摘要匹配；事件侧按模块、严重程度、原因码与上下文键匹配；一律不区分大小写的子串匹配。过滤只影响展示，不影响存储与导出。
- 刷新：`System.Windows.Forms.Timer`，间隔 1 s。页面可见时运行；隐藏、被摘出（`Parent` 变为 `null`）或释放时停止。刷新只读已发布快照 ⇒ 打开、刷新、关闭诊断页都不改变采样次数。
- `Rescan`：调用 `IHardwareClient.RequestFullRescanAsync` 发起一次受控的硬件全量重扫，结果按 `manual-rescan` / `manual-rescan-rejected` / `manual-rescan-failed` 记入事件。诊断页不提供其它恢复动作。
- `Export…`：取当前事实快照与事件快照（在同一次刷新中取得）构造 `DiagnosticPackageInput`，生成预览后经保存对话框选路径导出；`AppVersion` 取 `Application.ProductVersion`。
- 页面只读：除显式的 `Rescan` 与 `Export` 外，不提供任何改变系统状态的命令。

---

## 9. 已知限制

- 诊断事件只驻留内存，不落盘 ⇒ 进程退出后事件不可追溯；唯一跨进程的诊断痕迹是上次运行标记。
- 诊断模块没有推送式订阅接口；消费者必须轮询，快照新鲜度受各自刷新节奏限制。
- `ApplyResultDiagnosticProvider` 未在组合根注册 ⇒ 诊断页的模块列表不含 `apply` 模块；`apply` 相关事实仍以事件形式出现在事件流与诊断包中。
- `IpcDiagnosticProvider` 恒为 `Unknown`（`health_not_instrumented`）⇒ IPC 传输健康在诊断中心不可观测。
- `PluginDiagnosticProvider` 只给出实例计数；存在启用实例时状态为 `Warning`、原因码 `health_not_instrumented` ⇒ 单实例健康未纳入诊断。
- 诊断包大小上限是硬拒绝：载荷超过 10 MiB 时导出失败，而不是截断内容。
- 诊断数据不通过 Web 服务暴露，也无法从外部拉取。
- 脱敏是形态判定，允许列表内的键仍可能被写入方填入敏感内容（见第 7.5 节）。
