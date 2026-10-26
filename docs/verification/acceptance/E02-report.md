# P-E02 验收报告 —— 异步命令及超时/取消/合并语义

- 日期：2026-10-26。
- 工作包：P-E02（完善计划 §6「异步操作和命令语义」），分支 `pkg/E02-commands`，工作树 `.worktrees/e02-cmd`。
- 基线：`master` @ `cb24130`（含契约 v2 基座 `8e2de2f` 与集成人预落的 IPC 操作名 `cb24130`）。
- 契约边界：**未修改任何 `src/Contracts/` 文件**（集成人独占）。命令面语义以 v2 接口注释与
  `docs/plans/dispatch/P-E02.md` 为准；并行包 P-E01 领地（`src/System/HardwareMonitor.cs`、
  `src/System/HardwareServices/**`、`HardwareHistoryLogger`、`src/UI/HardwareInfoForm.cs`）**零改动**。
- 验证口径：全部用 TRX（`tests/*/TestResults/*.trx`），无裸退出码替代。

---

## 1. 交付范围与落点

| 落点 | 文件 | 说明 |
|---|---|---|
| 管道客户端命令面 | `src/System/Pipe/PipeHardwareClient.cs` | 命令应答/超时/取消、配置合并、显式重扫状态机、keyed 需求、重连重放、会话纪元 |
| 管道客户端协议件 | `src/System/Pipe/PipeCommandProtocol.cs`（新） | 新命令的线格式（camelCase 参数 / 大小写不敏感读取）与 DTO |
| 管道客户端操作记录 | `src/System/Pipe/PipeOperationRegistry.cs`（新） | 客户端侧有界操作记录（容量 + 截止期限） |
| 监督器 | `src/System/Pipe/HardwareHostSupervisor.cs` | 会话纪元递增并下发；会话令牌先于连接发布（响应校验前提） |
| Host 命令处理 | `src/HardwareHost/HostPipeSession.cs` | `sampling-due`/`rescan-full-v2`/`submit-demand-v2`/`release-demand`/`operation-status` |
| Host 协议件与操作表 | `src/HardwareHost/HostCommandProtocol.cs`（新） | 与客户端侧同构的线格式 + 有界操作登记表 |
| Host 采样器 | `src/HardwareHost/MinimalHardwareSampler.cs` | `Rescan()`（目录重建 + 代次递增）、快照带 `SessionEpoch` |
| Host 启动参数 | `src/HardwareHost/Program.cs` | `--session-epoch`、新增故障注入 `slow-rescan` |
| UI（本包唯一 UI 文件） | `src/UI/Settings/ThemePreviewService.cs` | §6.1 同步下载改真异步 |
| 测试 | `tests/LiteMonitor.Tests/E02ClientSemanticConsistencyTests.cs`（新，13 个命名语义点） | 管道侧全绿 + 进程内适配器对照（移交清单） |

**未触碰**：`src/Contracts/**`、`src/System/HardwareServices/**`、`src/System/HardwareMonitor.cs`、
`src/UI/HardwareInfoForm.cs`、`SettingsForm`/主题/lang 资源、`MetricItem`/`MetricUtils`、
CI/门禁/基线文件（`scripts/architecture-baseline.json` 未改）。

---

## 2. 语义表实现对照（完善计划 §6 表格逐行）

| 操作 | 计划约定 | 本包实现（管道侧） | 语义测试点 |
|---|---|---|---|
| 读取最新快照 | 同步、纯本地、不触发采样 | `GetLatestSnapshot()` 不变；**新增**：会话纪元守卫 + 会话关闭即丢弃旧会话快照 | 语义点 8/9 |
| 请求到期采样 | 异步、共享唯一协调器；同一采样周期合并 | `RequestSamplingAsync` → `sampling-due`（客户端按操作名合并为一条命令；Host 侧用采样票号合并为一轮采样） | 语义点 2 |
| 显式硬件重扫 | 异步、有独立操作 ID 及状态；同代次重复请求加入在途操作 | `RequestFullRescanAsync` → `rescan-full-v2` + `operation-status` 轮询；Host 侧 `Queued→Running→Completed/Failed` 状态机；在途操作被重复请求**加入**（同一 OperationId），不重复重扫 | 语义点 3/4/12 |
| 获取目录 | 异步读取已发布目录/分页 | `GetCatalogAsync` 不变（旧消息 `get-catalog` 含义未改） | I2 既有用例 |
| 应用配置 | 异步，返回实际生效 revision；仅相同 revision/内容可合并；不同版本排队或明确被新版本取代 | `ApplyConfigAsync` → `apply-config`；相同 revision+内容**合并**为一次底层应用；不同版本**排队**执行，未开始的旧版本被**明确取代**（被取代方收到 `superseded-by-revision:N`，不静默丢配置）；只有 Host 应答确认后才更新 `AppliedConfigRevision` 与快照 `ConfigRevision` | 语义点 1 |
| 提交需求 | 更新某消费者的需求 revision；同消费者可保留最新值，不覆盖其他消费者 | `SubmitDemand(consumerId, demand)` → `submit-demand-v2`；`ReleaseDemand(consumerId)` → `release-demand`；同消费者最新值覆盖、不同消费者互不影响 | 语义点 10 |
| 超时 | 只表示本调用方停止等待，不能报告底层已停止 | 超时返回/抛出 `TimeoutException` 或带 `timeout:` 原因的结果；**不**取消底层；非幂等操作保留有界记录，新请求取得同一 `OperationId`（busy） | 语义点 4 |
| 取消 | 一个加入者取消不取消其他加入者共享的操作 | 取消只作用于各自等待（`WaitAsync(..., ct)`）；共享操作继续，受理与轮询用 `CancellationToken.None` | 语义点 3 |
| 容量与截止期限 | 请求队列/待应答表/执行记录都有界；超限返回可解释结果 | 待应答表上限 64（`command-capacity-exceeded`）；操作记录上限 32 + 2 分钟截止期限；Host 侧操作表上限 32 + 2 分钟；需求消费者上限 64 | 语义点 11 |
| 重连重放 | Host 重连后重放最新期望配置和有效需求；旧会话应答不能完成新命令 | 新会话建立即重放 `_desiredConfig` 与逐消费者需求；响应按**会话令牌**校验，旧会话应答丢弃并计数（`StaleSessionResponses`） | 语义点 6/7 |
| SessionEpoch | Host 重启/重连时快照纪元递增，旧会话快照数据不混入新会话 | 监督器每次新建会话纪元 +1 并经 `--session-epoch` 下发；Host 盖在快照上；客户端按纪元丢弃旧纪元迟到快照，会话关闭即清空缓存快照 | 语义点 8/9 |
| E00-F2 | 会话切换重置批次基准 | `OnSessionClosed` 同时重置 `_lastSequence` 与 `_lastBatchId` | 语义点 9 |

---

## 3. 协议（次版本演进，旧消息含义零改动）

`IpcProtocol.MinorVersion = 1`（集成人预落）。本包只使用**已预落**的操作名，未新增消息类型、
未改旧操作含义：

| 操作名 | 参数 JSON | 结果 JSON | 说明 |
|---|---|---|---|
| `sampling-due` | 无 | `{"triggered": bool}` | 到期采样（同周期合并） |
| `rescan-full-v2` | 无 | `HardwareOperationResult`（`Accepted`/`OperationId`/`State`/`FailureReason`） | 显式重扫受理 |
| `submit-demand-v2` | `{"consumerId": string, "demand": SamplingDemand}` | `{"registered": bool}` | 按消费者登记需求 |
| `release-demand` | `{"consumerId": string}` | `{"released": bool}` | 释放需求 |
| `operation-status` | `{"operationId": string}` | `HardwareOperationResult`；未知 ID ⇒ `Ok=true` + `State=Rejected("unknown-operation")` | 超时调用方后询真实结局 |

线格式约定：参数 JSON 用 camelCase（对齐 `IpcMessages` 注释），结果 DTO 的 `HardwareOperationResult`
用契约记录本身的字段名（PascalCase）；**两侧读取一律大小写不敏感**，因此两种写法都能解析。
客户端与 Host 各自持有一份同构选项（`PipeCommandJson.Options` / `HostCommandJson.Options`），
以在不触碰 Contracts 的前提下演进。旧消息 `apply-config`/`submit-demand`/`rescan`/`get-catalog`/`shutdown`
的参数与结果形状**未变**。

---

## 4. 并发 / 取消 / 超时用例（管道侧断言全绿）

| 语义点 | 用例 | 关键断言 |
|---|---|---|
| 语义点 1 | 相同 revision+内容并发应用 / 不同版本并发应用 | 相同内容 ⇒ `CommandsSent` 增量 **1**（合并）；不同版本 ⇒ 最终 `AppliedConfigRevision=2` 且快照 `ConfigRevision=2`；未成功者必须有可解释 `FailureReason` |
| 语义点 2 | 4 路并发到期采样 | 全部返回 true；`CommandsSent` 增量 **1**（同周期合并） |
| 语义点 3 | 两调用者共享一次显式重扫，其一取消 | 取消方抛 `OperationCanceledException`；另一方得到 `State=Completed`；目录代次只 **+1**（底层只重扫一次） |
| 语义点 4 | 重扫超时后再次请求 | 首次超时返回 `Accepted=true` + `OperationId` + `State=Running` + `timeout:` 原因；再次请求取得**同一** `OperationId`；目录代次只 **+1**（不重复重扫） |
| 语义点 5 | 杀掉 Host（会话断开） | 在飞的重扫与配置应用**都在 30s 期限内结束**（无悬空等待）；重扫不得报告 `Completed`；待应答表清空为 0 |
| 语义点 6 | 注入旧会话应答 | `StaleSessionResponses` +1；在飞命令**未被完成**（请求 ID 仍在配对表） |
| 语义点 7 | 杀掉 Host 后重连 | 新会话快照 `ConfigRevision=7`（期望配置被重放）；需求登记次数 **+1**（有效需求被重放） |
| 语义点 8 | 杀掉 Host 后重连 | 快照 `SessionEpoch` 递增；客户端当前快照纪元 == 当前纪元（旧会话快照不混入） |
| 语义点 9 | 合成信封直灌接收入口 | 会话关闭 ⇒ 缓存快照清空（样本数 0）；新会话批次号回退到 1 ⇒ `SnapshotBatchGaps=0`（E00-F2） |
| 语义点 10 | 两个消费者登记/释放 | 两者均 `registered`；释放其一不影响另一；重复释放返回 false |
| 语义点 11 | 待应答表占满 / 操作记录超限 | 表满 ⇒ `FailureReason` 含 `capacity`（可解释拒绝）；操作表容量 2 时第 3 条创建失败，过期剪除后可再新建 |
| 语义点 12 | 操作状态后询 | 未知 ID ⇒ `State=Rejected("unknown-operation")`；已完成操作可查得 `Completed` 与同一 `OperationId` |
| 语义点 13 | 主题预览同步等待修复 | `ThemePreviewService` 代码中无 `GetAwaiter().GetResult()`/`.Result`；存在 `RenderSnapshotAsync`（返回 `Task<string?>`）；旧同步入口已移除 |

---

## 5. 重连重放与"旧会话应答不能完成新命令"

- **重放触发点**：监督器 `SessionOpened` ⇒ 客户端 `ScheduleReplay()`（单线程、有界、自标志防重入）：
  重放 `_desiredConfig`（最新期望配置）与逐消费者 `_demands`（有效需求）。重放后的 Host 应答
  仍以 `AppliedConfigRevision` 为准（**只有实际应用版本确认后**才把该版本视为当前生效）。
- **旧会话应答拦截**：每条待应答命令记录**发送时所属会话令牌**；应答到达时令牌不匹配即判定为
  旧会话应答 ⇒ 丢弃并计入 `StaleSessionResponses`，**不完成**该命令（语义点 6）。
  令牌以监督器为权威（`HardwareHostSupervisor.CurrentSessionToken`），且监督器按
  "**令牌先于连接**发布"（写序：`_sessionToken` → `_connection`），杜绝"有连接但令牌未更新"的窗口。
- **SessionEpoch**：监督器每次新建会话纪元 +1 并经 `--session-epoch` 传给 Host，Host 盖在快照上；
  客户端收到纪元更小的快照即丢弃（不混入新会话）；会话关闭即清空缓存快照与纪元基准。

---

## 6. 适配器对照移交清单（进程内客户端 ⇒ P-E01）

对照方式：同一测试集经 `IHardwareClient` 接口对两种客户端跑同一组语义用例；管道侧断言必须全绿，
进程内适配器（`HardwareClientAdapter`）的对照结果落盘为移交清单
（`tests/LiteMonitor.Tests/bin/Debug/net8.0-windows/e02-adapter-comparison.txt`，
亦可经 `E02ClientSemanticConsistencyTests.AdapterComparisonLog` 读取）。

**本轮对照输出（逐点）**：

| 语义点 | 适配器对照结果 | 移交给 E01 的语义要求 |
|---|---|---|
| 语义点 1 应用配置 | `不支持:NotSupportedException:IHardwareClient v2：由具体客户端实现（P-E02）` | 适配器需实现 `ApplyConfigAsync`：仅相同 revision+内容可合并；不同版本排队或明确被取代；返回**实际生效** revision；超时/取消不得谎报底层已停止 |
| 语义点 2 到期采样 | `不支持:ArgumentNullException:Value cannot be null.` | 接口默认实现转发到 `RequestRescanAsync`；适配器需把"到期采样"与"显式重扫"分家：`RequestSamplingAsync` 走唯一协调器的到期判定，`RequestFullRescanAsync` 走独立重扫路径（**不得**把 UI Tick 当重扫）。*（本次 ArgumentNullException 来自无硬件壳，非语义差异）* |
| 语义点 3 显式重扫 | `不支持:NotSupportedException:…（P-E02）` | 适配器需实现 `RequestFullRescanAsync`：独立操作 ID、有界记录、`HardwareOperationResult` 状态机；同代次重复请求加入在途操作；超时后新请求取得同一 operationId |
| 语义点 6 旧会话应答 | `不支持:ArgumentNullException:Value cannot be null.` | 进程内无会话概念，语义等价项为"旧操作结果不得完成新命令"；适配器需保证同一操作 ID 的后询能取到真实结局 |
| 语义点 7 重连重放 | `不支持:NotSupportedException:IHardwareClient v2：由具体客户端实现（P-E01 需求租约）` | 适配器需实现 `ReleaseDemand(string)`（需求租约）；进程内"重连"对应采集器重建，重建后须重放需求并递增进程内纪元（**进程内纪元归 E01**） |
| 语义点 10 keyed 需求 | `不支持:NotSupportedException:…（P-E01 需求租约）` | 适配器当前 `SubmitDemand(consumerId, demand)` 走接口默认桥接到旧单桶（**不按消费者隔离**）；需实现 keyed 需求：同消费者保留最新值、关闭一个消费者不清掉其他消费者需求 |

**结论**：进程内适配器的**命令面在本轮基线尚未实现**（`IHardwareClient` v2 的默认实现抛
`NotSupportedException`），因此适配器侧不具备与管道侧一致的语义。本包**不越界修改**
`src/System/HardwareServices/**`；上述 6 项移交清单由集成人转 P-E01，E01 落地后用**同一测试文件**
（`E02ClientSemanticConsistencyTests.cs`）复验收口。

---

## 7. UI 同步等待修复（§6.1）

- 位置：`src/UI/Settings/ThemePreviewService.cs`（本包唯一 UI 文件）。
- 修复前：`RenderSnapshotBlocking` 用 `ThemeMarket.DownloadThemePayloadAsync(entry).GetAwaiter().GetResult()`
  在 UI 线程同步下载（实际阻塞消息循环）。
- 修复后：改为 `RenderSnapshotAsync(MarketThemeEntry, CancellationToken)` —— 下载走 `await` 真异步路径；
  渲染（GDI/GPU 离屏窗体）按调用方的 `SynchronizationContext` marshal 回 UI 线程执行（无 UI 上下文时就地渲染）；
  失败返回 null。旧同步入口移除（仓内无其他调用方）。
- 护栏：语义点 13（源码扫描仅作**附加**护栏；真实路径为 `RequestSnapshot`/`PumpAllAsync` 既有异步泵，未改）。

---

## 8. 验收数字（TRX 口径）

命令：`dotnet test <project> -c Debug --logger trx;LogFileName=<name>.trx`（串行执行；
环境变量 `NUGET_PACKAGES=G:\TOOL\LiteMonitor-fork\.nuget-packages`、
`DOTNET_CLI_HOME=G:\TOOL\LiteMonitor-fork\.dotnet-cli-home`）。

<!--FINAL-NUMBERS-->

| 测试工程 | 发现(total) | 执行(executed) | 通过 | 失败 | 跳过 | TRX |
|---|---|---|---|---|---|---|
| LiteMonitor.Tests | 2492 | 2485 | 2485 | **0** | 7 | `tests/LiteMonitor.Tests/TestResults/e02-full-main-final.trx` |
| LiteMonitor.GpuNonperformance.Tests | 19 | 19 | 19 | **0** | 0 | `tests/LiteMonitor.GpuNonperformance.Tests/TestResults/e02-full-gpu.trx` |
| LiteMonitor.Updater.Tests | 73 | 73 | 73 | **0** | 0 | `tests/LiteMonitor.Updater.Tests/TestResults/e02-full-updater.trx` |
| **合计** | **2584** | **2577** | **2577** | **0** | **7** | |

门禁对照：主工程 **2492 ≥ 2479** ✓；三工程合计 **2584 ≥ 2571** ✓；失败 **0** ✓。
7 项跳过的原因与既有基线一致（`MarketIndexContractTests` 6 例 + `MarketE2ETests` 1 例：
需要真实插件仓库环境变量 `LITEMONITOR_PLUGIN_REPO`，本机不具备 ⇒ 合法跳过，**不得**据此宣称完整发布门禁通过）。

**专项复跑（独立 TRX）**

| 专项 | 结果 | TRX |
|---|---|---|
| E02 语义一致性测试（13 个命名语义点） | 13/13 通过 | `e02-new.trx` |
| Host 专项（`I2*` 全类：监督 12 + 定位器 12 + 协议 12 + 原子性 5 + 端点等） | **47/47 通过**（不回退） | `e02-i2-final.trx` |
| 架构棘轮断言 `scripts/architecture-assert.ps1` | **12/12 通过**（C 组 4/4、A 组 8/8；基线未改） | 控制台输出 |
| 构建警告基线 | 289（与基线**一致**，本包新增/改动文件**零新增警告**） | `dotnet build -t:Rebuild` 输出 |

**并行会话干扰记录（须知）**：`I2HostSupervisionTests` 中两处断言统计的是**整机**同名
`LiteMonitor.HardwareHost` 进程数（`Assert.Equal(1, HostProcessCount())` /
`Assert.Equal(0, HostProcessCount())`）。本轮验证期间有其他并行工作树
（`.worktrees/e01-snap`、`.worktrees/lane-b`）同时在跑各自的 `dotnet test`，其 Host 进程会被计入，
导致该两处断言偶发失败。取证：进程采样日志显示同期存在
`…\.worktrees\e01-snap\src\HardwareHost\bin\Debug\…\LiteMonitor.HardwareHost.exe --pipe … --fault stop-heartbeat`
等**其他工作树**的 Host 进程。单独复跑 `I2*` 专项（47/47）与机器空闲时的全量复跑（2492/0 失败）
均通过 ⇒ 属**环境干扰**，非本包回归。

---

## 9. 遗留与已知边界

1. **适配器命令面未实现**：见 §6 移交清单（6 项），归 P-E01；本包不修。
2. **最小 Host 的"显式全量重扫"= 目录重建 + 代次递增**，不是真实设备重开
   （`Computer.Close/Open`）：真实采集器迁移属 I3/E09 范围。目录代次变化可被快照 `CatalogVersion` 观测，
   语义点 3/4 据此断言"底层只重扫一次"。
3. **`ReleaseDemand` 的同步形状**：`IHardwareClient.ReleaseDemand` 是同步返回 `bool` 的契约形状，
   管道实现**本地注销并立即返回**（此前是否登记由本地记录判定），释放命令由有界需求泵异步送达，
   Host 侧受理结果见 `GetLastDemandOutcome`。理由：§2.4 明确 UI 不得同步等待 IPC；契约的同步签名无法
   同时满足两者。**接口注释目标态与本实现的差异**记录在此，迁移归 E03/E04。
4. **`SubmitDemand` 为 void**：无法回传受理结果；提供可等待的 `SubmitDemandAsync` 供调用方/测试使用，
   受理结果亦可经 `GetLastDemandOutcome(consumerId)` 观测。
5. **同步兼容桥保持有界**：`ApplyConfig`（同步）仍走有界命令期限（未改为无限等待）；现存 UI 线程调用链
   （SettingsForm 保存 → SamplingConfigApplier → ApplyConfig）的迁移归 E03。
6. **进程内纪元**：`SessionEpoch` 的**进程内重建**语义归 E01（复审必改 4 的归属拆分）；本包只做
   Host 会话侧（纪元递增、快照盖章、旧会话快照隔离）。
7. **命令超时计数口径**：超时守护是传输期限的唯一裁决者（到期即计数并结算完成源）；调用方等待另有
   250ms 兜底宽限，仅用于"守护未按时醒来"时保持等待**有界**（不得改为无限等待）。
8. **Release 配置未在本轮执行**：沿用既有基线口径（Debug 全量 + Release 待 CI）。

---

## 10. 结论

- 计划 §6 的 9 条语义约定在**管道侧**逐条落地并有命名用例钉住（13 个语义点，管道侧断言全绿）。
- 超时 ≠ 底层取消、取消不取消共享操作、容量与截止期限有界、重连重放、旧会话应答拦截、
  SessionEpoch 递增与旧会话快照隔离、E00-F2 批次基准重置、UI 同步下载改异步——均有独立可复跑证据。
- 进程内适配器的命令面差异**只记不改**，已形成 6 项可执行的移交清单交 P-E01。
