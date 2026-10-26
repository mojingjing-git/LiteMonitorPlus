# E01：真实采样快照、目录与需求闭环（P-E01）

- 日期：2026-10-26。
- 分支：`pkg/E01-snapshot`（独立工作树 `.worktrees/e01-snap`，主工作树零改动）。
- 起点：`cb24130`（开跑时的 master；契约 v2 基座 `8e2de2f` 已在 HEAD）。
  **收尾时已 rebase 到当前 master `bea6860`**（与并行的主题工作室分支零文件重叠，rebase 无冲突），
  rebase 后的提交 SHA 见下表。
- 依据：[完善计划 §2/§5](../../plans/2026-10-26-post-review-engineering-improvement-plan.md)、[派工单 P-E01](../../plans/dispatch/P-E01.md)。
- 独立性声明：本文所有数字来自本分支**实际构建与运行**的结果（TRX 口径）；未采信任何提交说明里的"已修复"表述。

## 1. 提交清单（按职责分阶段）

| 提交 | 主题 | 范围 |
|---|---|---|
| `329f5d7` | `feat(E01-A): 生产者发布——真实采样完成后原子发布不可变快照` | 新增 `SnapshotPublisher.cs` / `IHardwareCollector.cs`；改 `HardwareMonitor.cs`（发布路径）、`HardwareClientAdapter.cs`（快照面）、`SensorMap.cs`；新增 `E01SnapshotPublishingTests.cs` + `E01Fakes.cs` |
| `47d00f7` | `feat(E01-B): 目录版本化 + 需求租约（按消费者身份登记/释放）` | 新增 `CatalogPublisher.cs` / `DemandRegistry.cs`；改 `HardwareMonitor.cs`（目录发布面）、`HardwareClientAdapter.cs`（目录/需求面）、`HardwareInfoForm.cs`；新增 `E01CatalogDemandTests.cs` |
| `006b6d3` | `feat(E01-C): 适配器命令面——ApplyConfigAsync / RequestFullRescanAsync / ReleaseDemand` | 改 `HardwareClientAdapter.cs`（命令面）、`IHardwareCollector.cs`、`HardwareMonitor.cs`（重扫实现）；新增 `E01AdapterCommandSurfaceTests.cs` |

收尾报告提交：本分支最后一个提交（`docs(E01): 验收报告…`）。

改动统计（相对 `bea6860`）：8 个产品文件（4 改 / 4 新）+ 4 个测试文件（4 新），**零 Contracts 改动**、零 `src/System/Pipe/**`、零 `src/HardwareHost/**`、零 CI/门禁/基线、零 `MetricItem`/`MetricUtils`、零 SettingsForm/主题/lang。

## 2. 发布边界（E01-A）

### 2.1 发布点与原子性

- 新增 `src/System/HardwareServices/SnapshotPublisher.cs`：
  - **原子引用交换**发布（`Volatile.Write`）：读者要么看到旧批次、要么看到新批次，不会看到半截状态（值与目录不混代次）。
  - `BatchId` **只在发布时递增**（`Interlocked.Increment`）；`Current` 的读取**不推进**批次号、**不改变**时效字段。
  - 初始状态 = 明确的**空批次**（`BatchId=0`、样本为空），批次级缺失原因 `"initializing"`；不伪造成功批次。
- 发布点：`HardwareMonitor.OnCollectionCompleted(generation, success)`——即 `HardwareSamplingCoordinator` 既有的 `onCollectionCompleted` 回调（`src/System/HardwareMonitor.cs`）。真实采样完成后在此原子发布，**读取路径不再生成批次**。
- 样本组装分两段（关键：避免重复取值）：
  - **传感器样本**在采集锁内、同一采集代次、`hw.Update()` 之后读取（`BuildSensorSamples`）；
  - **指标样本**在"采集落定"之后组装（`BuildMetricSamples`）——取值层的代次缓存刚被清空，本轮**只计算一次**，随后 UI/任务栏/Web 命中同一份缓存，**性能计数器的 `NextValue()` 语义不变**（否则每轮会多取一次值，污染 `ValueComputations` 与计数器窗口口径）。

### 2.2 快照字段做实

| 字段 | 实现 | 证据 |
|---|---|---|
| `Key` | 外部指标 Key（`SensorMap` 的 Key ∪ 用户配置的监控项 Key）；另有"传感器样本"，其 Key = 该传感器在目录中的 `StableId` | A6 / B5 |
| `StableDeviceId` | 指标样本 = 背靠传感器的 `ISensor.Hardware.Identifier.ToString()`（**与目录里设备条目的 StableId 同口径**）；传感器样本 = 传感器自身的 `StableId` | §7 口径说明 |
| `Unit` | `MetricUtils.GetUnitStr(key, value, MetricUtils.UnitContext.Panel)`（**面板口径**，缺失不带单位） | A6 |
| `Value` | 指标样本取自取值层（与面板显示同口径）；传感器样本取自 LHM 传感器原值 | A4 / B5 |
| `Quality` / `MissingReason` | 真实 0 = `Good`+0；无值 = `Missing`+`"no-value"`；本轮采集失败 = `Missing`+`"collection-failed"` | A4 / A5 / A7 |
| `SampleAgeMs` | 进程内发布：样本在采集锁内、发布前一刻生成，发布时刻−样本时刻在毫秒级，按 **0** 记（契约允许进程内为 0，已在代码注释注明） | 代码注释 |
| `ConfigRevision` | 适配器 `ApplyConfig` **成功后**回写 `SnapshotPublisher.RecordConfigRevision`，进入下一批快照 | A9 / C1 |
| `SessionEpoch` | **只在"采集器重建（进程内）"时 +1**：`ReloadComputerSafe`（硬件树整体替换）、`ReleaseComputerForDriverInstall`（驱动安装前释放）。**管道路径（Host 会话切换）的纪元递增归 P-E02，本包零改动** | A8 |
| `TimestampMonoMs` | 发布时刻（单调钟，`TimeProvider`） | A2 |
| `CatalogVersion` | 已发布目录版本 | B1 / B5 |
| `BatchId` | 仅发布时递增 | A2 / A3 |

### 2.3 失败/过期语义

- 采集失败 ⇒ 发布"缺失批次"：已知 Key 全部 `Missing` + `"collection-failed"`，**不保活**上一批的有效值（通用回退层不得把陈旧读数无限期当有效数据）。
- 目录侧同步：采集失败时目录里传感器的当前值也置空（缺失必须显式可见）。
- 真实 0 的判定口径：取值层对部分 Key（如 `CPU.Temp`）在无读数时会**用 0 兜底**。本包据"背靠传感器是否有真实读数"判定——有真实读数的 0 = `Good`+0；没有真实读数的 0 = `Missing`+`"no-value"`（**不冒充有效值**）。

## 3. 目录版本（E01-B）

- 新增 `src/System/HardwareServices/CatalogPublisher.cs`：
  - **结构变化才递增版本**：结构签名只含 `StableId/RuntimeId/ParentStableId/DeviceType/SensorType/Available/Name`；值/峰值变化**不**顶版本号（B1 / B3）。
  - 值刷新走 `PublishSameVersion`（同版本）；**旧版本不覆盖新版本**（B4）。
- 生产者发布时机：初始化完成、硬件重载（`ReloadComputerSafe`）、映射表重建（`EnsureFresh` 命中 10 分钟兜底重建）。
- **值刷新不重建 LHM 遍历**：生产者维护传感器登记表（`StableId → ISensor`），值刷新只读缓存的 `ISensor` 引用。
- 目录的**当前值**由消费侧按 `StableId` 从**已发布快照**匹配刷新（`HardwareClientAdapter.BuildCatalog`）——结构不变时不需要再遍历硬件树（B5）。
- R3 单飞保留：`CatalogSnapshotCache` 仍在（`GetCatalogAsync` 走同一套在途合并），作为过渡；目录的**版本**改由生产者的 `CatalogPublisher` 维护。
- 目录**条目口径**与生产者一致：设备条目 `StableId = hw.Identifier`，传感器条目 `StableId = sensor.Identifier`。

## 4. 需求租约（E01-B）

- 新增 `src/System/HardwareServices/DemandRegistry.cs`：按消费者身份的租约表（纯数据，可独立断言）。
- 适配器命令面：
  - `SubmitDemand(string consumerId, SamplingDemand)`：按消费者登记/更新（同消费者只更新自己那一份，**不覆盖**其他消费者）。
  - `ReleaseDemand(string consumerId)`：字典移除；返回是否存在过登记（未登记 ⇒ `false`）。
  - **"legacy" 桶**：兼容旧的单桶 `SubmitDemand(demand)` 调用。
  - **"web" 桶**：`SetWebDataDemand` 语义**只**经该桶映射——其它消费者（面板/任务栏/传感器窗口/legacy）登记或释放时**不碰** Web 需求通道（否则传感器窗口登记需求会把 Web 层自己的合法需求误关掉，这是 B2 注释里记录过的坑）。
  - 聚合需求 = 各消费者需求的按位或；释放一个消费者**不影响**其他消费者。
- `HardwareInfoForm`（本包唯一 UI 文件）：按消费者身份 `"sensor-window"` 登记，关闭窗口时 `ReleaseDemand("sensor-window")`——**只释放自己那一份**。

## 5. 适配器命令面（E01-C / 复审必改 1）

| 成员 | 实现 | 语义要点 |
|---|---|---|
| `ApplyConfigAsync` | 委托既有 `ApplyConfig` 逻辑的 Task 包装（放线程池，真正的硬件重配可能持锁数秒 ⇒ 不阻塞调用线程） | 成功 ⇒ 回写生效 revision；失败 ⇒ 返回可解释原因、**不伪报成功**、不写 revision |
| `RequestFullRescanAsync` | `HardwareMonitor`：`Invalidate()`（绕过周期门限，记为例外采集）+ 协调器采集；适配器：**有界**（默认 5s）、独立操作 ID、返回 `HardwareOperationResult` | 超时 ⇒ `Running` + "本调用方停止等待（底层可能仍在执行）"；调用方取消 ⇒ `Cancelled`；底层执行**不**绑定调用方取消令牌（"取消底层执行必须由操作所有者明确发起"） |
| `ReleaseDemand` | 字典移除 | 返回是否存在过登记 |

至此契约 v2 的成员在进程内实现上**全部落地**（`GetLatestSnapshot` / `GetCatalogAsync` / `ApplyConfigAsync` / `RequestFullRescanAsync` / `SubmitDemand(consumerId)` / `ReleaseDemand`），不再有"抛 `NotSupportedException` 的孤儿成员"（C7 钉住）。

## 6. 传感器窗口消费改造（E01-B）

`src/UI/HardwareInfoForm.cs`：

- 目录**版本变化才重建树**；普通数值更新只刷新可见节点（沿用既有 `UpdateVisibleNodesSmart`），**不重建整棵树**。
- 重建时按 **StableId** 记下并恢复展开状态（设备条目用 `StableId`；传感器类型分组用 `父StableId|类型`）。
- 需求按消费者身份登记/释放（见 §4）。

## 7. 需要复审确认的口径说明（两处与派工单字面略有出入，均已在代码注释注明）

1. **`MetricSample.StableDeviceId` 的两个取值口径**
   派工单写的是"`ISensor→Hardware.Identifier.ToString()`"。本包实现为：
   - **指标样本**（Key = 外部指标 Key）：`StableDeviceId` = 背靠传感器的 `ISensor.Hardware.Identifier.ToString()`——**与派工单字面一致**，且与目录里**设备**条目的 StableId 同口径（两者都是 `hw.Identifier`）。
   - **传感器样本**（Key = 传感器自身的 `StableId`）：`StableDeviceId` = 传感器自身的 `StableId`。
   原因：E01-B 要求"目录的 `HardwareCatalogEntry.Value` **由最新快照按 StableId 匹配刷新**"，而目录里**传感器**条目的 StableId 是 `sensor.Identifier`。若指标样本一律用设备 id，目录的传感器条目就**匹配不到**任何样本、值刷不出来。故快照额外携带一组"传感器样本"，其 StableId 与目录传感器条目**逐字相等**。设备归属仍由目录条目的 `ParentStableId` 表达。
2. **`HardwareSnapshot` 没有批次级质量字段**
   契约的 `HardwareSnapshot` 只有 `Samples`，空批次**无法**用样本承载"缺失原因"。因此"初始化未完成"以**空批次 + 发布器上的 `CurrentMissingReason = "initializing"`** 表达（适配器同样暴露 `CurrentMissingReason`）；`"collection-failed"` 则同时体现在批次级原因与逐样本的 `MissingReason` 上。这是契约现有字段能做到的最大表达力，**未修改任何 Contracts 文件**。

## 8. 实际门禁口径（发现/执行/通过/失败/跳过）

命令：`dotnet test <project> -c Debug --no-build --logger trx`（串行执行；环境变量 `NUGET_PACKAGES=G:\TOOL\LiteMonitor-fork\.nuget-packages`、`DOTNET_CLI_HOME=G:\TOOL\LiteMonitor-fork\.dotnet-cli-home`）。

| 测试工程 | 发现 | 执行 | 通过 | 失败 | 跳过 | 备注 |
|---|---|---|---|---|---|---|
| LiteMonitor.Tests | **2550** | 2543 | 2539 | **4** | 7 | 4 例失败全部落在 `I2HostSupervisionTests`（见 §8.1，**并发会话互相干扰**，非本包引入） |
| LiteMonitor.Tests（排除 `I2HostSupervisionTests`） | 2541 | 2534 | **2534** | **0** | 7 | 本包相关用例与其余全部用例 0 失败 |
| `I2HostSupervisionTests`（**隔离**复跑） | 9 | 9 | **9** | 0 | 0 | 同一份代码隔离跑全绿 |
| LiteMonitor.GpuNonperformance.Tests | 19 | 19 | 19 | 0 | 0 | |
| LiteMonitor.Updater.Tests | 73 | 73 | 73 | 0 | 0 | |
| **合计** | **2642** | 2635 | 2634 | **4** | 7 | 门禁阈值 2571（主工程 2479）已越过 |

新增用例 34 例（`E01SnapshotPublishingTests` 12 + `E01CatalogDemandTests` 14 + `E01AdapterCommandSurfaceTests` 8），全部通过。**门禁阈值未在本包上调**（CI/门禁属禁改区），实际发现数 2550（主工程，含并行分支带进来的主题工作室用例）由集成人按实处理。

> 注：主工程发现数 2550 里含并行会话（主题工作室）合入 master 的新增用例；**本包自身净增 34 例**（基线 2479 → 本包 2513 → rebase 后 2550）。

### 8.1 唯一失败项：并发测试会话互相干扰导致的既有 I2 监督器断言抖动（只记录，不修）

- 失败用例全部落在 `LiteMonitor.Tests.I2HostSupervisionTests`（4 例，机器负载高时更多）：
  `FailFast与握手超时_三败进窗口_暂停自动重启`（断言 `HostProcessCount()==0` 实测 1）、
  `进程被杀_监督器确认退出后重启并恢复新会话`（断言 `==1` 实测 3）、
  `R2_合法握手后协议异常_监督器回收子进程并恢复新会话`（"遗留了故障会话的活子进程"）、
  `连续100次启停_无孤儿进程且句柄线程无持续增长`（"启停 100 轮后存在孤儿 Host：2 个"）。
- **根因（本次实测定位，非推断）**：这些断言用的是**机器全局**的进程计数
  `GetProcessesByName("LiteMonitor.HardwareHost").Count`——它数的是**整台机器**上的 Host 进程，
  不区分是哪个测试会话拉起的。收尾期间**并行会话（主题工作室）正在同机跑测试**，
  其 `I2HostSupervisionTests` 同样在拉起 Host 进程 ⇒ 两侧互相"看见"对方的进程 ⇒ 计数断言不符。
  证据：采样时 `LiteMonitor.HardwareHost` 的 PID 在两次查询之间发生变化（60148 → 62348），
  说明当时确有**另一个会话正在实时拉起/回收** Host 进程。
- 佐证（本次实测）：
  1. 本分支**隔离**复跑 `I2HostSupervisionTests`：**9/9 通过**（`e01-final-i2b.trx`）；
  2. 在**当前 master 基线 `bea6860`（不含本包任何改动）**上跑同一类：**9 例里失败 2 例**
     （比本包还多）⇒ 与本包改动无关；
  3. 本分支排除该类后的全量：**2541 发现 / 2534 通过 / 7 跳过 / 0 失败**。
- 处置：`tests/LiteMonitor.Tests/I2Host*` 属 **P-E02 领地**（零改动边界）⇒ **只记录不修**。
  建议 P-E02 把这类断言从"机器全局进程计数"改为"按本次会话的 PID 集合核对"
  （或"有界等待 + 允许短暂残留"），否则同机并行跑测试时必然互相干扰。

### 8.2 其它门禁项

| 项 | 结果 |
|---|---|
| fixture 181 + 22 | `MetricFormatBehaviorFixtureTests` **181/181 通过**、`E00Verification20261026Tests` **22/22 通过** |
| 架构断言 | **12/12 PASS**（A 组 8/8 + C 组 4/4）；且**改动前后计数完全一致**（C-1 146 / C-2 27 / C-3 173 / C-4 351 / A 组不变）⇒ 棘轮零移动；rebase 到 `bea6860` 后复跑仍 12/12 |
| MUST 执行清单（CI 门禁类） | `MarketIndexContractTests` 6、`ReviewMarketPersistenceRegressionTests` 33、`ReviewMarketInstallRaceRegressionTests` 6、`ThemeMarketContractTests` 8、`ThemeMarketPolicyTests` 6 —— 带 `LITEMONITOR_PLUGIN_REPO` 指向 `tests/fixtures/plugin-repo` 全部**真执行且 0 跳过**（262 例全绿） |
| 7 项跳过 | `MarketIndexContractTests` 6 + `MarketE2ETests` 1：本地未设 `LITEMONITOR_PLUGIN_REPO` / `LITEMONITOR_E2E` 的合法门控跳过（与既有基线同） |
| 构建 | `dotnet build LiteMonitor.sln -c Debug` **0 错误**；本包新增/修改文件**零新增 warning** |
| Release 配置 | 本次仅 Debug（与既有基线口径一致） |

## 9. 移交清单

| 任务 | 源/产物 SHA | 已执行用例 | 失败/待验 | 用户可见变化 | 兼容影响 | 回滚提交 |
|---|---|---|---|---|---|---|
| E01-A 生产者发布 | `329f5d7` | A1～A12（12 例） | 无 | **无**（发布面尚未被面板/任务栏/Web 消费） | 适配器 `GetLatestSnapshot` 语义由"读取时生成批次"改为"读已发布批次"；`snapshotKeys` 构造参数不再使用（签名保留） | `git revert 329f5d7` |
| E01-B 目录与需求 | `47d00f7` | B1～B14（14 例） | 无 | 传感器窗口：结构变化才重建树、按 StableId 保持展开状态；关闭窗口释放自己的需求 | 目录 `Version` 由生产者维护；`HardwareInfoForm` 改用带消费者身份的需求登记 | `git revert 47d00f7` |
| E01-C 适配器命令面 | `006b6d3` | C1～C7（8 例） | 无 | **无** | `IHardwareClient` 的 v2 成员在进程内实现上不再抛 `NotSupportedException` | `git revert 006b6d3` |

## 10. 遗留与移交下一棒

1. **E01-C（消费方迁移）尚未开始**：面板 / 任务栏 / Web / 历史 / 告警仍走 `HardwareGateway.GetSampleValue`（即时读），**尚未**改读已发布快照。本包交付的是发布边界（前置）。
2. **管道路径（Host 会话）**：`SessionEpoch` 在管道侧的纪元递增、`PipeHardwareClient` 的命令面语义 —— 归 **P-E02**。本包在进程内实现上给出了对照语义（`ApplyConfigAsync` / `RequestFullRescanAsync` / `ReleaseDemand`），供其做一致性测试。
3. **`I2HostSupervisionTests` 的子进程计数抖动**（§8.1）—— 根因是断言用了**机器全局**进程计数，
   同机并行跑测试时必然互相干扰；归 **P-E02**（禁改边界内只记录）。
4. **快照的"过期"判定尚未接线**：契约的 `SampleQuality.Stale` 目前未由生产者产出——进程内发布每轮都会刷新批次，过期只在"采集层停摆"时出现；建议 E01-C 消费侧按 `TimestampMonoMs` 自行判过期并把 `Stale` 语义用起来。
5. **Web 需求通道的合流**：本包只保证 `"web"` 桶 ⇒ `SetWebDataDemand` 的映射正确；Web 层目前仍经 `HardwareGateway.TrySetWebDataDemand` **直接**写需求。若 Web 层日后改用 `"web"` 桶，两条路径会重复写同一状态——迁移时必须二选一（建议届时移除直写路径）。
6. **快照体积**：每批次携带"指标样本 + 传感器样本"（典型机器 ~150～400 条），约 10～30 KB/批、每秒一批。属 E06 性能预算的观测项，本包未做分配优化。
7. **取值口径的已知折中**：指标样本的"缺失"判定依赖"背靠传感器是否有真实读数"，对**聚合类**指标（如由多个核心传感器平均得到的 `CPU.Temp`）在个别机器上可能出现"面板有数、快照报缺失"。E01-C 迁移消费方前建议先复核这条口径。
8. **未做实机验证**：传感器窗口的展开状态保持、需求释放、目录刷新节奏依赖真实硬件树与真实 UI 交互，属 §11 的 GUI 清单。

## 11. 待实机清单（本机环境分界，如实记录）

| # | 场景 | 期望 | 关联 |
|---|---|---|---|
| G1 | 打开传感器窗口 → 展开若干设备 → 等待数轮 → 目录结构变化（如插拔设备） | 展开状态按 StableId 保持；结构变化时重建且恢复展开 | §6 |
| G2 | 关闭传感器窗口 | 该窗口的需求被释放；面板/任务栏/Web 的需求不受影响 | §4 |
| G3 | 面板/任务栏显示 0 的指标 | 快照里真实 0 = `Good`+0；无读数 = `Missing`（不显示 0） | §2.3 |
| G4 | 拔网线 / 设备故障导致采集失败 | 快照发布"缺失批次"，界面不回退成陈旧读数 | §2.3 |
| G5 | 触发硬件重载（改风扇监控项开关 / 磁盘探测模式） | `SessionEpoch` +1；新批次不与旧批次混代次 | §2.2 |
| G6 | 设置里改采样相关配置并保存 | `ApplyConfig` 成功后 `ConfigRevision` 进入后续快照 | §2.2 |

## 12. 结论

- E01-A/B 的**发布边界**已建立并被自动化钉住：批次只在真实采样完成后发布、读取不推进代次、目录结构变化才递增版本、需求按消费者身份登记与释放、命令面成员在进程内实现上全部落地。
- 本包**未**触碰任何 Contracts 文件、管道路径、Host、CI/门禁/基线、主题与语言资源。
- 实际门禁：主工程 **2550 发现 / 2539 通过 / 7 跳过 / 4 失败**（4 例全部是并发测试会话互相干扰导致的既有 I2 断言抖动：master 基线同跑失败 2 例，本分支隔离跑 9/9 通过；排除该类后 2541 发现 / 2534 通过 / **0 失败**）；三工程合计 **2642 发现**（阈值 2571）；fixture 181+22 全绿；架构断言 12/12 且棘轮零移动。
- 完整发布门禁（Release 配置 + 完整 CI 插件仓库条件 + 真实驱动/目标设备）**未**在本机闭合，如实留待。
