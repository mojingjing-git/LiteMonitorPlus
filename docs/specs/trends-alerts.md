# 趋势统计与规则化报警规格

本文描述 LiteMonitor++ 中趋势统计（Trend）与规则化报警（Alert）的当前规格：数据来源、存储分层、快照消费契约、视图指标、规则模型、迁移行为与求值节拍。

适用范围：

- `src/Core/Trends/`：`ShortTermTrendBuffer`、`TrendHistoryV2Store`、`TrendViewMetrics`
- `src/Core/Alerts/`：`AlertRule`、`AlertEvaluator`、`AlertRuntime`、`AlertRuleMigration`
- `src/Core/HardwareHistoryLogger.cs`：长期历史的采样、聚合、写入与读取
- `src/UI/HardwareTrendForm.cs`：趋势窗口（分类、时间窗、覆盖率、表格、CSV 导出）
- `src/UI/UIController.cs`：短时缓冲摄入与告警求值接线
- `src/Core/Settings.cs`：告警相关配置键

不在本文范围内：采样器本身的采集策略（见 `docs/specs/hardware-sampling.md`）、主题与渲染路线、插件市场。

---

## 1. 总体数据流

系统内存在两条相互独立的数据通路，共同服务"趋势"与"报警"两类功能。

| 通路 | 数据来源 | 存储 | 消费方 |
| --- | --- | --- | --- |
| 短时趋势缓冲 | 已发布快照 `HardwareSnapshot` | 内存环形缓冲（每序列定长队列） | 暂无界面消费方，仅由 `UIController` 持续写入 |
| 长期趋势历史 | 界面指标缓存（`HardwareMonitor.Get`） | 按天分段 JSONL + manifest | 趋势窗口、覆盖率统计、CSV 导出 |
| 规则化报警 | 已发布快照 `HardwareSnapshot` | 仅状态机（内存），事件写诊断中心 | 托盘气泡通知、诊断中心 |

两条通路的差异必须保持明确：

- 短时缓冲与报警**只消费采集层发布的快照**，不得读取界面缓存 ⇒ 同一份快照在多处被读到时不会产生重复采样。
- 长期历史**只消费界面指标缓存**，其采样节流与界面刷新节拍一致，指标集合取决于用户在面板/任务栏中开启的监控项。

---

## 2. 短期趋势缓冲（内存环形缓冲）

### 2.1 类型与常量

`src/Core/Trends/ShortTermTrendBuffer.cs`

- `ShortTermTrendBuffer`：线程安全的定长序列集合，构造参数 `capacityPerSeries`（默认 `DefaultCapacityPerSeries = 3600`）与 `maxSeries`（默认 `DefaultMaxSeries = 32`）。任一参数 `<= 0` 必须抛出 `ArgumentOutOfRangeException`。
- `TrendBucket`：单点记录，字段为 `TimeUtc`（观测时刻）、`Value`（可空）、`Quality`（`SampleQuality`）、`SampleAgeMs`、`StableDeviceId`、`Max`、`Min`、`Count`。
- `TrendSeriesKey`：序列身份 = `MetricKey` + `StableDeviceId`；设备身份缺失时 `StableDeviceId` 为 `null`。
- `TrendBufferStats`：计数快照，字段为 `SeriesCount`、`PointCount`、`MaxSeries`、`CapacityPerSeries`、`AcceptedSnapshots`、`DuplicateBatches`、`OutOfOrderBatches`、`RejectedSamples`、`SessionResets`、`DroppedPoints`。
- `IngestResult`：单次摄入结果，`Accepted`、`AcceptedSamples`、`RejectedSamples`、`SessionReset`。

每个样本产生**一个**桶，桶内 `Max`/`Min` 为 `null`、`Count` 为 `1` ⇒ 短时缓冲不提供同秒聚合，秒桶承载的是单次读数与其质量信息。

### 2.2 摄入规则

`ShortTermTrendBuffer.Ingest(HardwareSnapshot snapshot, DateTimeOffset observedAtUtc)` 必须按以下顺序处理：

1. **会话纪元变化**：当已摄入过快照且 `snapshot.SessionEpoch` 与上次不同 ⇒ 清空全部序列（会话纪元 = 采集端重启/重建的标识），`SessionResets` 计数加一，并把批号（`BatchId`）水位重置为"未建立"状态。新会话的首个快照即使 `BatchId` 小于上一会话的值也必须被接受。
2. **重复检测**：`BatchId` 等于上一次摄入的 `BatchId` ⇒ 计入 `DuplicateBatches`，整批丢弃，不写入任何点。
3. **倒序检测**：`BatchId` 小于上一次摄入的 `BatchId` ⇒ 计入 `OutOfOrderBatches`，整批丢弃。
4. **样本级校验**：`MetricSample.Key` 为空白或 `Value` 为 `null` 的样本被拒，计入 `RejectedSamples`。
5. **序列数上限**：出现新序列且当前序列数已达 `maxSeries` ⇒ 该样本被拒（计入 `RejectedSamples`），已有序列不受影响。
6. **入队**：桶的 `TimeUtc` 取调用方传入的 `observedAtUtc`，`Quality`、`SampleAgeMs`、`StableDeviceId` 原样保留 ⇒ 质量与样本年龄在短时层可见。
7. **容量淘汰**：序列内点数超过 `capacityPerSeries` ⇒ 出队最旧的一个点，`DroppedPoints` 加一。

`GetSeries`、`GetStats`、`Clear` 必须在锁内完成，保证并发读到的集合一致。

### 2.3 容量语义

默认配置下缓冲最多容纳 `32` 条序列 × `3600` 点 = `115200` 点。按每秒一个采样估算，单条序列覆盖约 1 小时窗口；超出容量的最旧点被静默丢弃。

---

## 3. 长期趋势历史（按天分段 JSONL）

### 3.1 存储布局

`src/Core/Trends/TrendHistoryV2Store.cs`

- 默认目录：`%LOCALAPPDATA%\LiteMonitor++\trends-v2`（构造参数可覆盖目录、字节上限、保留天数与时钟）。
- 日分段文件：`trend-<yyyy-MM-dd>.jsonl`，文件名中的日期为**本地日**。每行一条 JSON 记录，追加写入。
- manifest：`manifest.json`，同目录。

JSON 采用 camelCase 命名。

**日分段行格式**（`TrendHistoryPointRecord`）：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `timeUtc` | ISO 8601 时间戳 | 采样时刻 |
| `metricKey` | string | 指标键，如 `CPU.Temp`、`GPU.0.Temp` |
| `stableDeviceId` | string 或 null | 设备稳定身份；分钟聚合写入路径固定为 `null` |
| `value` | number | 该时间窗内的平均值 |
| `quality` | int | `SampleQuality` 枚举值；分钟聚合写入路径固定为 `Good`(0) |
| `sampleAgeMs` | number | 样本年龄；分钟聚合写入路径固定为 `0` |
| `max` | number 或 null | 窗内峰值 |
| `min` | number 或 null | 窗内谷值 |
| `count` | int | 窗内样本数 |

**manifest 格式**（`TrendHistoryManifest`）：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `version` | int | 格式版本，当前为 `2`（`TrendHistoryV2Store.CurrentVersion`） |
| `lastImportedTimestampUtc` | 时间戳或 null | 导入水位：已导入/已写入的最大时刻；同时作为 v1 增量导入的截止点 |
| `totalPoints` | long | 累计写入点数 |
| `updatedAtUtc` | 时间戳 | 最近一次写入时间 |

### 3.2 写入路径的唯一性

- 长期历史的**唯一写入点**是 `TrendHistoryV2Store.Append`。生产调用方只有 `HardwareHistoryLogger.AppendPointToV2`。
- `TrendHistoryV2Store.AppendBuckets` 提供从 `TrendBucket` 转换写入的入口，生产路径不使用它。
- v1 格式文件 `HardwareHistory.json`（位于程序目录，含 `.bak`/`.tmp` 伴随文件）**只作为导入源**，任何运行路径都不得写入该文件 ⇒ v2 JSONL 与 v1 JSON 不得出现双写分歧。
- 趋势窗口、覆盖率统计、CSV 导出全部从同一份 v2 数据派生 ⇒ 各视图之间不得出现口径差异。

`Append` 的行为必须满足：

1. 丢弃 `MetricKey` 为空白的点。
2. 按本地日分组，每组写入对应 `trend-<yyyy-MM-dd>.jsonl`，组内按 `TimeUtc` 升序追加。
3. 更新 manifest：`totalPoints` 累加；`lastImportedTimestampUtc` 取写入点的最大时刻（已有水位更大时保持不变）。
4. 以"先写 `.tmp` 再覆盖移动"的方式原子写 manifest ⇒ manifest 不得出现半写状态。
5. 执行保留期与字节上限淘汰（见 3.5）。

### 3.3 采样来源、节流与值域

`HardwareHistoryLogger.RecordSnapshot(Settings cfg, Func<string, float?> readValue)`：

- 采样节流：两次采样间隔不得小于 **10 秒**；不足时直接返回。
- 聚合粒度：按**分钟**聚合。跨入新的分钟时，把上一分钟的聚合结果落盘并开启新的一分钟。
- 指标集合：只包含在面板或任务栏中可见（`VisibleInPanel` 或 `VisibleInTaskbar`）的受支持指标。分组常量：

  | 分组 | 键 |
  | --- | --- |
  | `TemperatureKeys` | `CPU.Temp`、`GPU.Temp`、`DISK.Temp`、`MOBO.Temp` |
  | `LoadKeys` | `CPU.Load`、`GPU.Load`、`MEM.Load` |
  | `FrequencyKeys` | `CPU.Clock`、`GPU.Clock` |
  | `PowerKeys` | `CPU.Power`、`GPU.Power`、`BAT.Power` |
  | `FpsKeys` | `FPS` |

  带序号的显卡键（如 `GPU.0.Temp`、`GPU.1.Temp`，由 `GpuKey.Build` 生成）只要对应监控项可见也必须被记录。
- 值域校验（`IsValidValue`）：`NaN`/`Infinity` 一律拒绝；温度 `> 0 且 <= 125`；百分比与内存 `0 ~ 100`；频率 `> 0 且 <= 10000`；功耗 `> 0 且 <= 1000`；帧率 `0 ~ 1000`；其他类型拒绝。不合格的值不进入聚合。
- 单个键读取失败只跳过该键，不得中断其余键的采样。

### 3.4 读取与视图数据形状

- `TrendHistoryV2Store.ReadSince(DateTimeOffset sinceUtc)`：按文件名顺序遍历 `trend-*.jsonl`，文件名日期早于起始本地日的文件整文件跳过；文件内 `timeUtc` 早于 `sinceUtc` 的点跳过；无法解析的行计入 `CorruptLines` 且不中断读取；返回的点按 `timeUtc` 升序。
- `HardwareHistoryLogger` 在启动时读取近 **7 天**的 v2 数据，按分钟分组还原成 `HardwareHistoryPoint`（每点含各键的 `Avg`/`Max`/`Min`/`Count`）；v2 记录缺少 `max`/`min` 时退回单值语义，`count <= 0` 时按 1 计。
- `HardwareHistoryLogger.GetPoints(DateTime from)`：返回 `from` 之后的点，并附加当前尚未结束的分钟聚合点 ⇒ 趋势窗口看到的最后一点是当前分钟的实时值。

### 3.5 淘汰

- **保留期**：`DefaultRetentionDays = 7`。删除本地日期早于「今天 − (保留天数 − 1)」的日分段文件 ⇒ 始终保留最近 7 个本地日（含今天）。
- **字节上限**：`DefaultMaxBytes = 64 MiB`。当日分段总字节数超过上限时，按文件名（等价于日期）顺序删除最早的分段，直到回到上限以内。
- 淘汰在每次 `Append` 之后执行，并在保存路径上由 `TrimToNow` 再执行一次 ⇒ 磁盘占用有硬上限。
- 被淘汰的日分段不可恢复。

### 3.6 v1 格式的增量导入

`TrendHistoryV2Store.ImportV1(string legacyJsonPath)`：

- 读取 v1 格式的 `HardwareHistory.json`（`HardwareHistoryData` / `HardwareHistoryPoint` / `HardwareHistoryValue`），把每个分钟点的每个键转换成一条 v2 记录。
- 以 manifest 的 `lastImportedTimestampUtc` 作为**导入水位**：时间戳小于或等于水位的点被跳过 ⇒ 重复调用不会重复追加，导入是幂等的。
- 导入水位为空时导入全部可见点。
- 导入在 `HardwareHistoryLogger.Load()` 中执行，条件是 manifest 尚未记录水位（即 `lastImportedTimestampUtc` 为 `null`）⇒ 每份 v1 数据只会被导入一次。
- manifest 缺失、版本不匹配或内容损坏时，`TrendHistoryV2Store` 回落到新建 manifest（`version = 2`、水位为空、点数为 0）⇒ 损坏的 manifest 不得阻断启动。

---

## 4. 快照消费契约

### 4.1 快照语义

`src/Contracts/SnapshotModels.cs`

- `HardwareSnapshot` 是**不可变**的采集快照，字段为 `BatchId`、`CatalogVersion`、`ConfigRevision`、`Samples`、`SamplingDuration`、`TimestampMonoMs`、`SessionEpoch`。
- `MetricSample` 不可变，字段为 `Key`、`StableDeviceId`、`Value`、`Unit`、`Quality`、`MissingReason`、`SampleAgeMs`。
- `SampleQuality` 取值为 `Good`、`Stale`、`Missing`、`Error`。
- `BatchId` 在采集端单调递增；`SessionEpoch` 在采集端重启/重建时变化。
- `TimestampMonoMs` 是采集端的单调时钟毫秒数，用于把"采样发生的时刻"与"界面读到它的时刻"分开。

### 4.2 趋势侧的摄入契约

`UIController` 在每次界面刷新中调用 `ShortTermTrendBuffer.Ingest(_hwClient.GetLatestSnapshot(), DateTimeOffset.UtcNow)`：

- 趋势摄入是**旁路**：异常必须被吞掉，不得影响指标刷新与采样。
- 重复刷新读到同一份快照时，由 `BatchId` 去重保证不产生重复点。

### 4.3 报警侧的快照契约

`AlertRuntime.Evaluate(IReadOnlyList<AlertRule> rules, HardwareSnapshot snapshot, DateTimeOffset now)` 是生产的求值入口：

- 必须消费采集层发布的快照，**不得**用界面缓存读数伪造批号与采样时刻。
- 同一 `snapshot.BatchId` 的重复求值由评估器按 `BatchId` 丢弃 ⇒ 重复刷新不会产生重复事件。
- 会话标识为 `"snapshot:" + snapshot.SessionEpoch`；`SessionEpoch` 变化时重置源时间原点。
- 同一 `MetricKey` 出现多个样本时只保留第一条（该键首次出现的样本）。
- **样本有效性**：样本存在、`Value` 有值、`Quality == Good`、且 `SampleAgeMs <= rule.MaxSampleInterval`（毫秒）时才算有效输入。
- **设备身份缺失**（`StableDeviceId` 为空白）时，该指标**已知的所有设备**都被判定为输入不可用，不得新建 `"default"` 身份为原设备保活 ⇒ 采集失败区间不会被计入越限持续时间。
- **规则集合变化**：规则的签名（`MetricKey`、方向、触发/恢复阈值、触发/恢复持续时间、采样窗口、冷却、提醒间隔、`Enabled`、`Muted`）变化时，只重置该规则的状态；被移除的规则同样被清理；等价规则集合保持原有计时。

### 4.4 源时间映射

采样时刻由快照的源时间推导：`sourceMonoMs = snapshot.TimestampMonoMs − max(0, sample.SampleAgeMs)`，再按源时间原点映射为墙钟时间。

- 首个被求值的快照建立原点（原点墙钟 = 调用方传入的 `now`）。
- 之后只按源时间的增量推进 ⇒ 界面排队、重绘、刷新节拍带来的延迟**不计入**越限持续时间。
- 源时间倒退时按源时间原样映射，评估器按倒序样本丢弃，不会倒扣持续时间。

---

## 5. 趋势视图指标

`src/UI/HardwareTrendForm.cs`

### 5.1 分类与指标集合

窗口提供 5 个分类（`TrendCategory`），每个分类对应一个指标键集合：

| 分类 | 标题 | 指标来源 |
| --- | --- | --- |
| `Temperature` | 温度趋势 | `HardwareHistoryLogger.TemperatureKeys` + 已开启的带序号显卡温度键 |
| `Load` | 负载趋势 | `HardwareHistoryLogger.LoadKeys` + 已开启的带序号显卡负载键 |
| `Power` | 功耗趋势 | `HardwareHistoryLogger.PowerKeys` + 已开启的带序号显卡功耗键 |
| `Frequency` | 频率趋势 | `HardwareHistoryLogger.FrequencyKeys` + 已开启的带序号显卡频率键 |
| `Fps` | 帧率趋势 | `HardwareHistoryLogger.FpsKeys` |

规则：

- 带序号的显卡键（`GPU.<N>.<Metric>`）只有在对应监控项被用户开启（面板或任务栏可见）时才进入指标集合。
- 不带序号的显卡键（如 `GPU.Temp`）在存在对应带序号键时必须被剔除，避免出现冻结在最后一个历史点的曲线。
- 指标显示名优先取监控项自定义标签，其次取语言项 `Items.<基础键>`，最后退回键本身。

### 5.2 时间窗与桶宽

| 时间窗 | 跨度 | 桶宽 |
| --- | --- | --- |
| `Hour1` | 1 小时 | 1 分钟 |
| `Day1` | 24 小时 | 5 分钟 |
| `Day7` | 7 天 | 60 分钟 |

窗口每 15 秒刷新一次数据。

### 5.3 聚合口径

历史数据在分钟粒度上已聚合一次，视图按当前桶宽做二次聚合：

- `Avg`：按样本数加权平均（权重为该分钟的 `Count`）。
- `Max` / `Min`：取桶内极值。
- `Count`：桶内样本数累加（每分钟至少计 1）。
- 频率类指标在视图中按 GHz 显示（原始值 ÷ 1000）。
- 单位：温度 `°C`、百分比与内存 `%`、频率 `GHz`、功耗 `W`、帧率 `FPS`。

### 5.4 覆盖率

`TrendViewMetrics.Calculate(times, range, bucketMinutes, now)` 返回 `TrendCoverage`（`Range`、`BucketMinutes`、`ExpectedBuckets`、`ObservedBuckets`、`Ratio`）：

- `bucketMinutes <= 0` 必须抛出 `ArgumentOutOfRangeException`。
- 期望桶数 `expected = max(1, ceil(range 分钟 / bucketMinutes))`。
- 时间点先截断到桶边界再计数 ⇒ 同一桶内的多个样本只计一次。
- 落在范围外（早于起点或晚于终点）的时间点被忽略。
- 观测桶数取 `min(expected, 实际桶数)`，`Ratio = observed / expected`；无数据时 `Ratio = 0`。

窗口标题显示「分辨率 N min · 覆盖 Observed/Expected」，让用户直接看到当前分辨率下的数据完整度。

### 5.5 阈值线、表格与导出

- 温度分类绘制阈值线，取值为配置项 `AlertTempThreshold`；其它分类不绘制阈值线。阈值超出当前纵轴范围时不绘制。
- 表格列为 `Name`、`Current`、`Avg`、`Max`、`Min`、`Count`；`Avg` 为窗口内按样本数加权的平均值，`Count` 为窗口内样本总数。
- 摘要行对每条曲线显示「最新 / 峰值」。
- CSV 导出：表头固定为 `Time,Key,Name,Average,Max,Min,Unit,Samples`，默认文件名 `HardwareTrend_yyyyMMdd_HHmm.csv`，以 UTF-8（带 BOM）写出，便于 Excel 直接打开。

---

## 6. 规则化报警模型

### 6.1 规则的构成

`src/Core/Alerts/AlertModels.cs` 中 `AlertRule` 是可持久化的规则定义，不持有窗体、采集器或通知 API。

| 字段 | 语义 |
| --- | --- |
| `RuleId` | 规则唯一标识，同时是状态与诊断的关联键 |
| `Name` | 面向用户的显示名（通知与诊断中显示） |
| `MetricKey` | 监控的指标键，如 `CPU.Temp`、`GPU.0.Temp` |
| `Direction` | 比较方向：`Above`（高于阈值触发）或 `Below`（低于阈值触发） |
| `TriggerThreshold` | 触发阈值 |
| `RecoveryThreshold` | 恢复阈值（`Above` 方向为回差下限，`Below` 方向为回差上限） |
| `TriggerDuration` | 越限需持续的时间，`<= 0` 表示立即触发 |
| `RecoveryDuration` | 回到恢复区间需持续的时间 |
| `MaxSampleInterval` | 相邻样本的最大允许间隔，超过即视为采样缺口 |
| `NotificationCooldown` | 两次通知之间的最小间隔 |
| `ReminderInterval` | 可选；活动状态下重复提醒的间隔 |
| `Enabled` | 规则是否参与评估 |
| `Muted` | 规则级静音：仍然评估并产生事件，但不通知 |

条件的构成 = 指标（`MetricKey`）+ 比较符（`Direction` + `>=`/`<=` 触发比较与 `<=`/`>=` 恢复比较）+ 阈值（触发阈值与恢复阈值）+ 持续时间（触发/恢复持续时间）+ 采样新鲜度（`MaxSampleInterval`）。

### 6.2 规则校验与错误码

`AlertRule.IsValid(out string? error)` 返回布尔值并给出错误码字符串。错误码是接口约定，取值如下：

| 错误码 | 触发条件 |
| --- | --- |
| `rule_id_empty` | `RuleId` 为空白 |
| `metric_key_empty` | `MetricKey` 为空白 |
| `threshold_not_finite` | 触发阈值或恢复阈值非有限值 |
| `recovery_must_be_below_trigger` | `Direction = Above` 且恢复阈值 `>=` 触发阈值 |
| `recovery_must_be_above_trigger` | `Direction = Below` 且恢复阈值 `<=` 触发阈值 |
| `duration_negative` | 触发持续时间或恢复持续时间为负 |
| `max_sample_interval_must_be_positive` | 采样窗口 `<= 0` |
| `cooldown_negative` | 冷却时间为负 |
| `reminder_interval_must_be_positive` | 提醒间隔存在且 `<= 0` |

非法规则必须被静默跳过：不产生任何事件，不影响其它规则。

### 6.3 状态机

`AlertEvaluator` 是纯规则引擎，状态键为 `RuleId` 与 `DeviceId` 的组合 ⇒ 多台设备上的同一条规则彼此独立评估、互不影响。

状态取值（`AlertState`）：`Normal`、`Pending`、`Active`、`Recovering`、`InputUnavailable`。

| 当前状态 | 输入 | 迁移 |
| --- | --- | --- |
| `Normal` | 越过触发阈值 | `TriggerDuration <= 0` ⇒ 直接进入 `Active` 并产生 `Triggered`；否则进入 `Pending` |
| `Pending` | 仍在越限区间且已满足触发持续时间 | 进入 `Active`，产生 `Triggered` |
| `Pending` | 回到触发阈值以内 | 回到 `Normal`，清空计时 |
| `Active` | 达到恢复阈值且已满足恢复持续时间 | 回到 `Normal`，产生 `Recovered`，事件关闭 |
| `Active` | 达到恢复阈值但恢复持续时间未满 | 进入 `Recovering` |
| `Active` | 仍在触发/恢复阈值之间 | 保持 `Active`（回差区间不确认恢复） |
| `Active` | 再次越过触发阈值 | 保持 `Active`，不重复触发 |
| `Recovering` | 再次越过触发阈值 | 回到 `Active`，不重复触发 |
| `Recovering` | 仍在恢复区间且恢复持续时间已满 | 回到 `Normal`，产生 `Recovered` |
| `Recovering` | 落入触发/恢复阈值之间 | 回到 `Active`，不确认恢复 |
| 任意（`Active`/`Recovering` 且事件未关闭） | 采样缺口 | 转入 `InputUnavailable`，产生 `InputUnavailable` 事件 |
| 任意（`Pending`/`Recovering`） | 采样缺口 | 计时清空，回到 `Normal` |
| 任意 | 样本无效 | 转入 `InputUnavailable`，产生 `InputUnavailable` 事件（已处于该状态则不重复产生事件） |
| `InputUnavailable` | 恢复有效输入 | 事件未关闭 ⇒ 回到 `Active` 并产生 `InputRestored`；否则回到 `Normal` 并产生 `InputRestored` |

补充约束：

- 越限持续时间必须由**有效样本**累计；重复批号、倒序样本、采样缺口一律不得补算持续时间。
- 同一会话内 `BatchId` 不大于上次值、或观测时刻早于上次样本的输入被丢弃。
- 切换会话（`SessionId` 变化）时，原状态为 `Active` 且事件未关闭 ⇒ 产生 `InputUnavailable`（原因码 `session_changed`）并重置计时；跨会话不得确认前一次事件的恢复。
- 触发时生成事件标识 `IncidentId`，格式为 `{RuleId}:{DeviceId}:{ObservedAt:O}`，在事件关闭前保持不变。

### 6.4 事件、原因码与通知决策

事件种类（`AlertEventKind`）：`Triggered`、`Recovered`、`InputUnavailable`、`InputRestored`、`Reminder`。

原因码（`AlertTransition.ReasonCode`）：

| 原因码 | 含义 |
| --- | --- |
| `trigger_duration_met` | 越限持续时间满足，事件触发 |
| `recovery_duration_met` | 恢复持续时间满足，事件关闭 |
| `reminder_interval_elapsed` | 活动状态下到达提醒间隔 |
| `session_changed` | 采集会话切换导致输入中断 |
| `sample_gap` | 相邻样本间隔超过 `MaxSampleInterval` |
| `input_unavailable` | 样本无效（缺值、质量非 `Good`、样本年龄超限或身份缺失） |
| `input_restored` | 输入恢复 |

通知决策（`AlertNotificationDecision`）：

| 取值 | 含义 |
| --- | --- |
| `Notify` | 允许通知 |
| `SuppressedByMute` | 规则级静音（`AlertRule.Muted = true`） |
| `SuppressedByCooldown` | 距上一次通知时间小于 `NotificationCooldown` |
| `NotApplicable` | 该事件不涉及通知（输入中断/恢复类事件） |

冷却只抑制通知，**不抑制事件与状态推进** ⇒ 冷却期内发生的触发仍然产生事件、仍然写诊断事件。冷却计时以"实际发出通知的时刻"为准；被静音或被冷却抑制时不更新该时刻。提醒事件只在规则已发出过通知（`ReminderInterval` 的计时以最近一次通知为起点）之后才可能产生。

### 6.5 动作集合

每个事件在界面接线（`UIController.CheckRuleAlerts`）中触发以下动作：

1. **写诊断事件（恒执行）**：调用 `DiagnosticHub.Current.Record("alerts", severity, reasonCode, context, utcTime)`。
   - `reasonCode` 形如 `alert_triggered`、`alert_recovered`、`alert_inputunavailable`、`alert_inputrestored`、`alert_reminder`（由事件种类小写化得到）。
   - `severity`：`Triggered`、`InputUnavailable`、`Reminder` 为 `Warning`；`Recovered` 与其它种类为 `Info`。
   - `context` 字段：`ruleId`、`deviceId`、`state`、`notification`、`reason`、`incidentId`。
   - `utcTime` 取事件的观测时刻。
   - 诊断出口失败不得阻塞告警流程。
2. **托盘气泡通知（受控）**：仅当事件的 `Notification` 为 `Notify`、且全局静音未开启时触发。通知标题取语言项 `Menu.AlertTemp`，内容为多行文本：
   - 触发：`{规则名}: {读数}（阈值 {阈值}）`
   - 恢复：`{规则名}: 已恢复`
   - 其它事件种类不产生气泡文本。
   - 气泡由 `MainForm.ShowNotification` → `MainFormBizHelper.ShowNotification` 发出（`NotifyIcon.ShowBalloonTip`，显示时长 5000 毫秒）；托盘图标不可见时不显示气泡。
   - 通知发出后更新配置的 `LastAlertTime`。
3. **声音：当前动作集合中不存在该动作**——事件不产生任何声音输出。

### 6.6 全局静音

配置项 `AlertMuteAll`（默认 `false`）是全局静音开关，设置页提供「全局静音」开关。

静音时的行为必须满足：

- 规则**仍然被评估**：状态机照常推进，事件照常产生。
- 诊断事件**仍然被写入**：诊断写入先于静音判定发生 ⇒ 静音不损失可观测性。
- 只抑制通知：托盘气泡不出现。
- 静音判定同时覆盖规则级静音与全局静音两种来源，二者都不会阻止状态推进。

---

## 7. 旧温度阈值向规则的迁移

`src/Core/Alerts/AlertRuleMigration.cs`

### 7.1 版本与幂等

- 迁移版本常量 `AlertRuleMigration.CurrentVersion = 1`，对应配置项 `AlertRulesVersion`（默认 `0`）。
- `Apply(settings, monitoredGpuCount)`：`AlertRulesVersion >= 1` 时直接返回 `false`，不改动规则集合 ⇒ 迁移是幂等的，多次启动不会重复生成规则。
- 版本低于当前值时，用内置规则集合覆盖 `settings.AlertRules`，并把 `AlertRulesVersion` 置为 `1`，返回 `true`。
- 温度告警总开关处于关闭状态时，生成的规则同样是禁用的（`Enabled = false`），规则存在但永不触发。

### 7.2 生成的规则

| `RuleId` | 名称 | `MetricKey` | 触发阈值 |
| --- | --- | --- | --- |
| `temp.cpu` | CPU 温度 | `CPU.Temp` | `AlertTempThreshold` |
| `temp.mobo` | 主板温度 | `MOBO.Temp` | `AlertTempThreshold` |
| `temp.disk` | 磁盘温度 | `DISK.Temp` | `min(AlertTempThreshold − 20, 60)` |
| `temp.gpu.{N}` | GPU{N} 温度 | `GpuKey.Build(N, "Temp")` | `AlertTempThreshold` |

每条温度规则的固定参数：

| 参数 | 值 |
| --- | --- |
| `Direction` | `Above` |
| `RecoveryThreshold` | `max(0, 触发阈值 − 5)` |
| `TriggerDuration` | 5 秒 |
| `RecoveryDuration` | 10 秒 |
| `MaxSampleInterval` | 30 秒 |
| `NotificationCooldown` | 3 分钟 |
| `ReminderInterval` | 无 |
| `Enabled` | 跟随 `AlertTempEnabled` |

### 7.3 显卡规则的增量补齐

`SyncGpuRules(settings, monitoredGpuCount)` 在监控显卡数量变化时补齐缺失的 `temp.gpu.N`：

- 只添加缺失的 `RuleId`；已存在的规则（哪怕是用户改过阈值）必须原样保留。
- 不重建规则集合、不覆盖已有规则、不影响事件状态。
- 有变化时返回 `true`。

`Settings.Load` 依次执行 `AlertRuleMigration.Apply` 与 `AlertRuleMigration.SyncGpuRules`。

### 7.4 规则在 settings.json 中的形态

`settings.json` 以 `WriteIndented` 序列化，属性名保持 PascalCase。`AlertRules` 的每个元素序列化结果如下（枚举以整数存储，时长以 `"hh:mm:ss"` 字符串存储）：

```json
{
  "RuleId": "temp.cpu",
  "Name": "CPU 温度",
  "MetricKey": "CPU.Temp",
  "Direction": 0,
  "TriggerThreshold": 80,
  "RecoveryThreshold": 75,
  "TriggerDuration": "00:00:05",
  "RecoveryDuration": "00:00:10",
  "MaxSampleInterval": "00:00:30",
  "NotificationCooldown": "00:03:00",
  "ReminderInterval": null,
  "Enabled": true,
  "Muted": false
}
```

`Direction` 的整数值：`0 = Above`，`1 = Below`。`ReminderInterval` 为 `null` 表示不提醒。

告警相关的配置键：

| 键 | 类型 | 默认值 | 含义 |
| --- | --- | --- | --- |
| `AlertTempEnabled` | bool | `true` | 温度告警总开关；迁移生成规则的 `Enabled` 取值来源 |
| `AlertTempThreshold` | int（°C） | `80` | 全局温度阈值；也是趋势窗口温度分类的阈值线取值 |
| `AlertMuteAll` | bool | `false` | 全局静音 |
| `AlertRulesVersion` | int | `0` | 规则迁移版本水位 |
| `AlertRules` | 数组 | 空 | 规则集合 |
| `LastAlertTime` | 时间 | — | 最近一次发出通知的时刻 |

---

## 8. 求值节拍与执行位置

- **节拍**：规则求值随界面刷新节拍执行。刷新间隔为 `max(80, Settings.RefreshMs)` 毫秒，由 `UIController` 的定时器驱动；每个节拍中依次完成短时缓冲摄入、指标刷新、历史记录写入，随后调用 `CheckRuleAlerts`。
- **位置**：当前接线在界面线程内执行求值（`UIController.Tick` → `CheckRuleAlerts`）。`AlertEvaluator` 与 `AlertRuntime` 不引用任何 WinForms 类型、不读取界面缓存、不调用通知 API ⇒ 引擎本身不依赖界面线程，可整体托管到后台线程；把执行位置改到后台线程时必须保持两条约束：
  1. 同一份快照只求值一次（由 `BatchId` 去重保证）；
  2. 通知与界面状态更新在界面线程触发。
- **输入**：`CheckRuleAlerts` 只消费 `_hwClient.GetLatestSnapshot()` 返回的已发布快照；重复节拍读到同一快照时被评估器丢弃，不会产生重复事件。
- **故障隔离**：求值过程中的异常必须被吞掉，不得影响界面刷新与采样；诊断写入失败同样不得阻塞通知流程。
- **规则集合变更**：设置中规则定义发生变化时，`AlertRuntime` 按规则签名检测并只重置受影响规则的状态。

---

## 9. 已知限制

- 短时秒桶缓冲（`ShortTermTrendBuffer`）目前只有写入方，没有任何界面消费其序列；趋势窗口的数据完全来自长期历史的分钟聚合 ⇒ 秒级明细不可见。
- 规则求值当前在界面线程随刷新节拍执行；引擎虽无界面依赖，但接线尚未移出界面线程 ⇒ 规则数量增长时求值耗时会占用刷新时间。
- 规则集合没有逐条编辑界面。设置页只提供「温度告警开关」「温度阈值」「全局静音」三项；调整开关或阈值会以迁移参数重建全部内置规则（`Settings.RebuildMigratedAlertRules`），会覆盖 `settings.json` 中对内置规则的阈值改动。
- 通知动作只有托盘气泡一种；提醒类事件（`Reminder`）不产生气泡文本，仅写入诊断事件。音频提示未纳入当前实现。
- 托盘图标不可见时气泡不显示 ⇒ 通知在托盘隐藏场景下只体现在诊断事件中。
- 长期历史只记录面板/任务栏可见且通过值域校验的指标；不可见的指标不会被采集，也无法事后补齐。
- 长期历史按本地日分段，保留最近 7 个本地日且总量不超过 64 MiB；超出上限的最早日分段被删除且不可恢复。
- v1 格式文件 `HardwareHistory.json` 仅在导入水位为空时导入一次；导入完成后该文件的内容保持导入时刻的状态。
- 规则以单个 `MetricKey` 定义，多设备场景下同一规则对每个 `StableDeviceId` 独立评估，规则本身不含设备选择条件。
- 温度阈值线取自全局 `AlertTempThreshold`，温度分类下各曲线的阈值线一致，不随规则逐条区分。
