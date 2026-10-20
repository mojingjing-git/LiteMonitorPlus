# LiteMonitor++ 性能探针

本目录是 LiteMonitor++ 性能改造计划（`docs/superpowers/plans/2026-10-20-performance-sampling-hardening.md`）
的**可复跑测量工具**。Task 0 用它建立"实施前基线"，Task 2/6 用同一个探针复测"实施后"结果。

探针**不修改任何生产代码**，只引用主项目与已有的 `LibreHardwareMonitorLib`，不新增任何 NuGet 包、
测试框架或运行时依赖。

---

## 1. 它测什么

| 能力 | 说明 | 对应计划章节 |
|---|---|---|
| 采集组 A–H | 按 §6.2 的实机性能组跑"采集循环"，预热 ≥15 s、稳态 ≥60 s | §6.2 / §6.1 |
| 每设备 Update 次数 | **直接读生产侧计数** `HardwareMonitor.SamplingStatistics`（含 CPU/显卡/内存/网卡/磁盘/主板）；另用 LHM 硬件对象代理独立复核显卡与内存 | §4.1 / §6.1 |
| 采集轮数 vs 请求数 | 采集轮数 = 生产侧实际发生的采集（`CollectionRounds` 增量）；请求数 = 探针发起的采集请求（未到期会被门限跳过） | §4.1 / §4.2 |
| 慢扫描 / 保存次数 | **直接读生产侧触发计数**（慢扫描/后台磁盘扫描/流量保存/GC 请求/工作集裁剪）；另保留探针按**旧取模语义**复算的推算值作参考 | §4.3 |
| 单设备 Update 成本 | 对每个真实硬件对象直接调用 `Update()`，测墙钟与进程 CPU 时间 | §2 |
| 兼容缓冲绘制 | 用真实 `UILayout`/`UIRenderer`/`HorizontalRenderer` 在 WinForms 兼容缓冲画布上测绘制成本 | §2 / §6.2 |
| FPS 状态 | 失效 PID 残留（是否被清理、是否每轮重复查询）、Web 快照在**有/无需求**下是否读取 FPS（**不启动任何外部服务**） | §4.4 / §4.5 |

---

## 2. 目录与隔离

```
tests/LiteMonitor.PerformanceProbe/
├── LiteMonitor.PerformanceProbe.csproj
├── Program.cs            命令行入口与编排
├── ProbeEnvironment.cs   隔离配置、应用生命周期、环境元数据、LHM 计数代理注入
├── CountingHardware.cs   LHM Hardware 基类代理（统计 Update 次数）+ 空 ISettings
├── SamplingRunner.cs     采集循环测量器（§6.2 的 A–H 组）
├── CounterSemantics.cs   批次级计数口径声明（counterSemantics）
├── WebActivity.cs        最小 WebSocket / HTTP 客户端（只用 BCL）
├── MicroBenchmarks.cs    单设备 Update 成本 + 兼容缓冲绘制
├── FpsStateProbe.cs      失效 PID 残留 + Web 隐藏 FPS
├── output\               ★ 隔离运行期输出目录（已 git 忽略）
├── baseline\             ★ 已提交的基线原始数据
│   ├── probe-results.json   第 1 轮完整基线
│   ├── probe-run.txt        第 1 轮运行日志
│   └── repeat\              第 2 轮独立重复（用于量化单轮波动）
├── rerun3\               ★ 实施后第一次重跑（D/E/F/H2 四组，45 s 稳态）
└── run-final-r1|r2|r3\   ★ Task 6 最终复测（三轮 × 9 组，预热 15 s / 稳态 60 s）
    ├── probe-results.json   该轮全部原始数据
    └── probe-run.txt        该轮运行日志
```

**隔离保证**

- 探针的 `AppContext.BaseDirectory` = `output\`。`TrafficHistory.json`、
  `HardwareHistory.json`、`LiteMonitor++_HardwareError.log` 等运行期文件都写在这里，
  不会落到用户常用实例目录。
- 探针自己 `new Settings()` 并注入 `Settings._instance` 单例，
  因此**不读也不写**用户常用的 `settings.json`；`Settings.Load()` 命中单例后直接返回内存对象。
- 不安装/卸载任何驱动：启动前只做一次**只读**的 PawnIO 就绪检查，检查不通过就不测硬件项。
- 不启动 PresentMon/ETW，不下载任何外部程序。

---

## 3. 怎么跑

```powershell
# 构建（探针输出目录固定为 output\）
dotnet build tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj -c Release

# 完整基线（预热 15s / 稳态 60s，约 13 分钟）
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --out tests/LiteMonitor.PerformanceProbe/baseline

# 再跑一次独立重复轮（量化单轮波动）
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --out tests/LiteMonitor.PerformanceProbe/baseline/repeat

# 只跑某几个组
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --out <dir> --groups B,C

# 冒烟（缩短时长，只能用于验证流程，不能当基线）
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --out <dir> --quick
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `--out <dir>` | `output\results` | 结果 JSON 与运行日志的输出目录 |
| `--groups <A,B,...>` | 全部 | 要跑的组 |
| `--warmup <秒>` | 15 | 每组预热时长 |
| `--steady <秒>` | 60 | 每组稳态时长 |
| `--settle <秒>` | 3 | 组间恢复时间 |
| `--device-calls <次>` | 20 | 单设备 Update 测量次数 |
| `--render-seconds <秒>` | 2 | 每个渲染用例的测量时长 |
| `--quick` | — | 冒烟用：预热 3s / 稳态 10s，**不可作为基线** |

输出文件：`<out>\probe-results.json`（全部原始数据）、`<out>\probe-run.txt`（运行日志）。

---

## 4. 采集循环是怎么模拟的

探针按 `UIController.Tick()` 的真实节奏跑：

1. `HardwareMonitor.UpdateAll()` —— **请求**一次采集（是否真的采集由生产的门限决定）；
2. 依次读取当前启用的监控项 —— 界面/Web 的取值；
3. 等待到**周期网格上的下一个刻度**再开始下一轮。

第 3 点与 WinForms 定时器一致：忙碌时丢帧、不会补跑错过的轮次。
所以"100 ms 刷新"在这里就是"每 100 ms 尝试采集一轮"。

⚠ **请求次数 ≠ 采集轮数**：生产侧现在有采集门限（`HardwareSamplingCoordinator`，
普通硬件最小周期 1000 ms），未到期的请求会被直接跳过，不产生采集。
因此结果里有两个不同的数：

| 字段 | 含义 |
|---|---|
| `collectionRequests` | 探针发起的采集请求次数（= 循环轮数） |
| `collectionRounds` | 生产侧**实际发生**的采集轮数（`SamplingStatistics.CollectionRounds` 增量） |
| `skippedRequests` / `sharedRequests` / `rejectedRequests` / `forcedCollections` / `failedCollections` | 门限跳过 / 并发共享 / 未就绪拒绝 / 例外采集 / 采集异常 |

> 与已提交基线的口径差异：基线（实施前）没有门限，`collectionRounds` = 请求次数 = 实际采集次数；
> 现在 `collectionRounds` 是**实际采集轮数**，`collectionRequests` 才是请求次数。比较时必须对号入座。

**探针不做的事**：界面绘制、`InfoService`/Dashboard 取值、`HardwareHistoryLogger` 落盘、
温度告警。这些都在进程 CPU 里被排除或单独测量，报告里逐条标注。

---

## 5. 每卡 Update 次数是怎么数出来的

`LibreHardwareMonitor.Hardware.Computer.Hardware` 是**按分组现算出来的副本**，
硬件对象实际存放在各分组的强类型集合里：

| 分组 | 集合类型 | 能否注入代理 |
|---|---|---|
| `NvidiaGroup` / `IntelGpuGroup` / `MemoryGroup` | `List<Hardware>` | ✅ 可以（元素类型是基类 `Hardware`） |
| `CpuGroup` | `List<GenericCpu>` | ❌ |
| `NetworkGroup` | `List<Network>` | ❌ |
| `StorageGroup` | `List<StorageDevice>` | ❌ |
| `MotherboardGroup` | `Motherboard[]` | ❌ |

探针把 `CountingHardware`（继承 LHM 公开的 `Hardware` 基类）注入到可注入的分组里，
并强制 `SensorMap` 重建一次，让 `CachedGpu`/`CachedGpus` 也指向代理对象
（否则 `HardwareMonitor.ShouldUpdateGpuHardware()` 的 `ReferenceEquals` 判断会把显卡整个跳过）。

> 安装后的验证必须**等一次真实采集**发生：生产侧有采集门限，
> 未到期时 `UpdateAll()` 会被跳过，只看一次调用会把"被门限跳过"误判成"代理失效"。
> 探针因此轮询 `SamplingStatistics.CollectionRounds` 是否增长，再看代理计数。

**现在有了两个来源，探针都写进结果**：

| 字段 | 来源 | 覆盖范围 | 地位 |
|---|---|---|---|
| `productionDeviceUpdates` / `productionCategoryUpdates` | 生产侧 `HardwareMonitor.SamplingStatistics` | **全部设备**（CPU/显卡/内存/网卡/磁盘/主板/电池） | **权威** |
| `deviceUpdateCounters` | LHM 注入代理直接数的 `IHardware.Update()` | 仅显卡与内存（可注入分组） | 独立复核 |

**运行时有效性守卫**：`HardwareMonitor.ReloadComputerSafe()`（硬件变化/热插拔时触发，
延迟 2 秒后重建分组集合）会把已注入的代理丢掉，之后 `deviceUpdateCounters` 会变成 0 或残缺，
而 JSON 看起来仍是一份正常基线。因此**每组稳态结束后**探针都会复检一次，
结果写在该组记录的 `countersStillInstalled`（true/false）与 `deviceUpdateCountersTrusted`。
为 false 时该组的代理计数**不可信**（但 `productionDeviceUpdates` 仍然可用）。

---

## 6. 计数口径（跨批次比较前**必读**）

### 6.1 每个批次都带 `counterSemantics` 顶层声明

`probe-results.json` 的**顶层**与**每个分组记录**里都有 `counterSemantics`，
它逐字段写明本批次各计数**从哪来**（`counterSemantics.semanticsVersion` = `2026-10-20-d-prep`）：

| 字段 | 本批次口径（来源类别） |
|---|---|
| `collectionRounds` / `collectionGenerations` / `skippedRequests` / `sharedRequests` / `rejectedRequests` / `forcedCollections` / `failedCollections` / `valueComputations` / `productionCollectionDurationMs` | **生产侧真实计数**（`HardwareMonitor.SamplingStatistics` 增量） |
| `slowScanTriggerRounds` / `diskBackgroundScanTriggerRounds` / `trafficSaveDueRounds` / `gcRequestDueRounds` / `workingSetTrimDueRounds` | **生产侧真实计数**（生产侧**真的**触发了多少次） |
| `productionDeviceUpdates` / `productionCategoryUpdates` | **生产侧真实计数**（每设备 / 每分类 Update 次数增量） |
| `collectionRequests` / `valueReads` | 探针自身计数（发起了多少次请求 / 取了多少次值） |
| `trafficFileObservedRewrites` | 探针观测值（轮询文件 `LastWriteTime` 变化得到的**下界**） |
| `deviceUpdateCounters` | LHM 注入代理独立复核（只覆盖显卡/内存） |
| `slowScanTriggerRoundsRecomputed` / `diskBackgroundScanTriggerRoundsRecomputed` / `trafficSaveDueRoundsRecomputed` / `gcRequestDueRoundsRecomputed` / `workingSetTrimDueRoundsRecomputed` | ⚠ **旧取模语义推算值**（`%3` / `%10` / `%60==5` / `%180==30` / `%300==45`）——**不可与生产计数相减** |
| `dueSemanticsRecomputationAgrees` / `dueSemanticsRecomputationDelta` | 两套口径的对照结果，见 §6.4 |

**任何跨批次比较都必须先读 `counterSemantics`**，确认两边口径一致再比。

### 6.2 ⚠ 跨批次"同名不同义"（最容易踩的一条）

同一个字段名，在**不同批次**里含义可能不同 —— **直接相减会得出与事实相反的结论**：

| 批次 | "到期轮数"类字段的来源 |
|---|---|
| `baseline/`（Task 0 建立的实施前基线） | **旧探针按整数秒取模复算的值** |
| 本批次及之后的复跑 | **生产侧真实触发计数** |

实测证据（可自行核对）：

- `baseline/probe-results.json` A 组：`collectionRounds=60`、`slowScanTriggerRounds=20`、
  `diskBackgroundScanTriggerRounds=6` —— 20 = 60/3、6 = 60/10，是**取模复算值**；
  那时还没有采集门限，`collectionRounds` = 请求次数 = 实际采集次数。
- `rerun3/probe-results.json` H2 组：`collectionRounds=12`（真实采集轮数）、
  `collectionRequests=15`（请求次数）—— 已经是**生产侧计数口径**。

两者**形状很像、含义不同**。所以：**先看 `counterSemantics` → 再看字段名 → 最后才比较**。
另外 `collectionRounds` 在基线里等于请求次数、在现在等于真实采集轮数（见 §4 的提示）。

### 6.3 ⚠ "生产计数"也不总是权威：Task 3 接线之前它只是取模镜像

⚠ **在生产侧接线（Task 3）之前**，生产侧那些"触发计数"本身**只是取模规则的镜像**
（当时只做计数、不改调度语义）—— 也就是说，**"生产计数"并不总是权威，不能一概而论**。

- 基线批次：取模规则在**探针侧**复算；
- Task 3 接线之后：生产侧改成单调截止时间（`HardwareSamplingSchedule` 的 `now + 周期` 锚定、
  `MaintenanceScheduler` 的**真实到期**），计数才变成**真实触发次数**。

判断某个批次的计数可不可信，要看它**有没有 `counterSemantics` 声明**：
没有声明的旧产物（`baseline/`、`rerun3/`）一律按"旧取模语义"对待。

### 6.4 `dueSemanticsRecomputationAgrees` 现在比较的是什么

- **比较对象**：「探针按旧取模语义复算的推算值」 vs 「生产侧真实触发计数」，逐项看 |差| ≤ 1。
- **为什么不再报警**：生产侧已经改成单调截止时间，两套语义**本来就不同**，
  所以该字段为 `false` 属于**正常状态**，**不是**"生产侧出 bug"的信号。
  旧实现拿"同源的取模副本"互比，自身漂移才会变 false，形同虚设。
- **什么情况下才值得看**：它变成 `true` 时 —— 说明某个触发器的节奏**恰好**与旧取模规则重合，
  需要人工确认该计数究竟来自单调截止时间，还是生产侧回退到了取模语义。
- **真正的行为级报警**是 `trafficRewriteAgreesWithDueRounds`
  （生产侧**实际触发**的流量保存次数 vs 轮询文件 `LastWriteTime` 变化得到的下界），
  以及它的差值 `trafficRewriteDelta`。
- `dueSemanticsRecomputationDelta` 是**逐项差**（复算值 − 生产计数），
  它衡量的是**两套口径的差**，**不是**"生产侧多触发/少触发"的差，不要据此下结论。

### 6.5 优雅降级：`_secondsCounter` 读不到时

复算 `*Recomputed` 系列需要反射读 `HardwareMonitor._secondsCounter`。
该字段在生产侧已被降级为"只作诊断计数"的私有字段，**随时可能被改名或删除**。

探针对此**不再抛异常**（旧实现会在构造期直接抛，探针**根本起不来**，所有测量归零）：

| 位置 | 读不到时的表现 |
|---|---|
| 顶层 `secondsCounterAvailability` | `unavailable: <原因>` |
| 分组 `recomputedCountersStatus` | `unavailable` |
| 分组 `recomputedCountersNote` | 具体原因（字段不存在 / 反射异常） |
| 5 个 `*Recomputed` 字段 | `null` —— **不是 0**（0 会被误读成"复算得 0 次"） |
| `dueSemanticsRecomputationAgrees` / `dueSemanticsRecomputationDelta` | `null`（没测到，不是不一致） |
| **主字段**（生产侧真实计数） | **照常产出**（`slowScanTriggerRounds` 等仍然有值） |

**降级不会让整个测量失败** —— 其它证据（生产侧计数）仍然有效。

### 6.6 字段口径表（改调度之后会失效的列）

| 字段 | 口径 | 什么时候失效 |
|---|---|---|
| `collectionRounds` | 生产侧**实际采集**轮数（`CollectionRounds` 增量） | 不失效 |
| `collectionRequests` | 探针发起的采集请求次数 | 不失效 |
| `productionDeviceUpdates` | 生产侧每设备 Update 次数 | 生产改设备 Key 规则时 |
| `deviceUpdateCounters` | LHM 代理直接数的 `IHardware.Update()` 调用 | 改硬件重载/代理注入路径时 |
| `valueReads` | 探针自己的 `Get()` 调用 | 不失效 |
| `slowScanTriggerRounds` / `diskBackgroundScanTriggerRounds` / `trafficSaveDueRounds` / `gcRequestDueRounds` / `workingSetTrimDueRounds` | **直接读生产侧触发计数**（`SamplingStatistics`） | 不失效（生产改调度语义时数值自动跟着变） |
| `*Recomputed` 系列 | ⚠ **旧取模语义推算值**（`%3` / `%10` / `%60==5` / `%180==30` / `%300==45`），**不可与生产计数相减** | 只能当参考；`_secondsCounter` 消失时为 `null` |
| `dueSemanticsRecomputationAgrees` | 复算推算值与生产计数是否逐项吻合（±1） | **不是**报警项，见 §6.4 |
| `trafficFileObservedRewrites` | 轮询文件 `LastWriteTime` 变化得到的**下界** | 不失效，但永远 ≤ 真实写入次数 |
| `trafficRewriteAgreesWithDueRounds` / `trafficRewriteDelta` | 生产侧真实流量保存次数 vs 文件重写观测 | 不失效（**行为级报警点**） |

取模判定集中放在 `SamplingRunner.MaintenanceDueSemantics`，常量来源在文件头的注释里。
**注意**：这些常量是**改造前**的旧规则，生产侧已改用单调截止时间 —— 它们不再代表生产调度语义。

### 6.7 ⚠ `webSocketFramesReceived` 在基线批次里是死计数器值

基线批次（`baseline/`）里 `webSocketFramesReceived` **恒为 0**（D/E/F/H2 四组实测都是 0）：
那是**根本没计到帧**的占位值，**不携带任何信息**。

**不可**把它与复跑批次的 53 / 159 / 55（`rerun3/` 的 E/F/H2 组实测）相比 ——
那是**换了计法**（Web 侧改成真实计帧），**不是**性能变化。
`counterSemantics.knownDeadCounters` 里也写了这一条。

### 6.8 ⚠ `webHiddenFps` 的"有需求"场景期望值已过期（**不是产品缺陷**）

`fpsState.webHiddenFps.scenarioWithWebDemand` 这一格的 `expectedFpsRead` / `matchesExpectation`
是在"生产只要有需求就会读 FPS"的规则下写的。**提交 `197601c`（B2）之后，生产快照取值路径
加上了可信度守卫**（`LiteWebServer.ResolveValue`）：

```text
先判 fpsDemand  →  再判 hasFpsData  →  最后才 readValue
```

于是"有需求但**尚无可信 FPS 数据**"时**同样不读**、仍输出 `--`。
探针的合成 FPS 状态是「已释放、无结果」→ `HasFpsData = false` →
生产**正确地**不读 → 探针这一格显示 `fpsReadRequested=false` / `matchesExpectation=false`。

| 批次 | 场景②结果 | 说明 |
|---|---|---|
| `rerun3/`（`efeeb69`，B2 之前） | `fpsReadRequested=true` ✅ | 当时生产还没有 `hasFpsData` 守卫 |
| `run-final-r1\|r2\|r3/`（`b777ec0`） | `fpsReadRequested=false` ❌ | 生产规则变了，**探针期望没跟着变** |

**读这一格时请注意**：它**不能**用来判断"生产在有需求时是否去读 FPS"——
"有需求 + 有可信数据 → 读"这条路径需要真实 PresentMon 提供可信数据，
本轮测不了（见 §7）。生产侧该路径由单测覆盖
（`WebDemandTests.FPS启用时读取真实数值而不是占位`、
`WebSamplingDemandTests.有可信FPS数据时输出真实数值`）。
**场景①（无需求 → 不读）依然有效**，且是本轮唯一可用的行为级证据。

### 6.9 ⚠ 外观探针：经过时间 vs CPU 时间（两种口径，**不可混用**）

外观探针（`--appearance-only`）的配对测量里同时存在**两种口径**，字段名必须看得出区别：

| 口径 | 含义 | 来源 | 用在哪 |
|---|---|---|---|
| **经过时间**（elapsed / wall clock） | 渲染**延迟** | `Stopwatch.GetTimestamp()` | p95 处理延迟判据（≤ 30 ms） |
| **CPU 时间**（处理器时间） | 真正占用的**处理器**时间 | `Process.TotalProcessorTime` | CPU 预算（≤ 40 ms/秒）、整机 CPU 占用推算 |

⚠ 旧字段 `extraMsPerDraw` / `legacyMedianMsPerDraw` / `wiredMedianMsPerDraw` 是**经过时间**口径
（旧键名保留、值不变）。**不能**把它们当成 CPU 时间去推算整机 CPU 百分比。
新增的 `legacyElapsedMsPerDraw` / `wiredElapsedMsPerDraw` / `extraElapsedMsPerDraw`
是同值的**显式命名**，与旧键并存，便于新旧结果对照。

CPU 时间口径由 `--appearance-paired-cpu` 单独产出，放在配对结果的 `pairedCpu` 段：

| 字段 | 口径 | 说明 |
|---|---|---|
| `pairedCpu.legacyCpuMsPerDraw` / `pairedCpu.wiredCpuMsPerDraw` | CPU 时间 | 旧/新入口每帧的处理器时间 |
| `pairedCpu.extraCpuMsPerDraw` | CPU 时间 | 新 − 旧差值；**落在噪声内时为 `null`（不可判定），不是 0** |
| `pairedCpu.extraCpuMsPerDrawMeasured` | CPU 时间 | 实测均值（永远有值），供噪声判定 |
| `pairedCpu.extraCpuMsPerDrawMin/Max/Range/StdDev` | CPU 时间 | 轮间离散度（极差 = 噪声量级的主要来源） |
| `pairedCpu.noiseFloorMsPerDraw` / `pairedCpu.withinMeasurementNoise` / `pairedCpu.verdict` | — | 噪声门槛与判定结论 |
| `pairedCpu.legacyProjectedWholeMachineCpuAt12_5Hz` / `wiredProjectedWholeMachineCpuAt12_5Hz` / `projectedWholeMachineCpuAt12_5Hz` | CPU 时间 | 整机占用推算（%）；差值落在噪声内时为 `null` |
| `pairedCpu.*BatchElapsedMsPerDraw` | 经过时间 | 同一批次的墙钟值，与 CPU 值并列对照 |

整机占用推算公式：**整机占用(%) = 12.5 帧/秒 × 每帧 CPU 毫秒 ÷ 1000 ÷ CPU 核心数 × 100**
（**只能用 CPU 时间口径**；经过时间不能用于整机 CPU 占用推算。）

#### CPU 口径测量的方法

Windows 上 `Process.TotalProcessorTime` 的量化步长约 **15.6 ms**，单帧绝对值不可用，所以：

1. 每批次绘制 **≥ 2000 次**（`--appearance-paired-cpu-draws`，默认 2000），让 CPU 差值远超量化步长；
2. **同进程同刻配对**：同一段内背靠背测旧入口与新入口，而不是跨批次比绝对值；
3. **交替执行顺序**：段内交替 + 逐轮翻转起始顺序（奇数轮旧→新、偶数轮新→旧）；
4. **至少 3 轮**（复用 `--appearance-paired-rounds`，CPU 口径强制下限 3 轮），
   逐轮记录 old/new 的 CPU 差值与经过时间差值，并给出均值与轮间离散度；
5. 差值绝对值落在噪声门槛（轮间极差 / 量化步长 / 0.1 ms 三者取大）以内 →
   **如实报告「不可判定」**：`extraCpuMsPerDraw` 为 `null`，**不报告成 0**。

| 开关 | 默认 | 说明 |
|---|---|---|
| `--appearance-paired-cpu` | 关 | 开启 CPU 口径配对测量。**单独使用** = 只跑 CPU 口径测量（跳过常规夹具矩阵与经过时间配对测量）；与 `--appearance-paired` 同用 = 在既有配对流程里一并产出 CPU 口径字段 |
| `--appearance-paired-cpu-draws <n>` | 2000 | 每个批次的绘制次数（需远超 15.6 ms 的量化步长） |
| `--appearance-paired-cpu-segments <n>` | 2 | 每轮的配对段数（取偶数时，一轮之内旧/新各当一次先测的） |
| `--appearance-paired-cpu-max-measurements <n>` | 1 | 单次运行最多做多少次 CPU 口径测量（矩阵流程里 = 前 N 个夹具；其余夹具的 `pairedCpu` 为 `null` 并附 `pairedCpuNote`） |
| `--appearance-paired-cpu-theme <主题名>` | 空（取主题清单里的第一个） | **CPU 口径「只跑」模式**要测的主题。⚠ 该模式会**绕过 `--appearance-filter`**（过滤逻辑在它之后才执行），主题只能取 `themes[0]` —— 而主题清单是**字母序动态发现**的，不指定就会测成 `AuroraDream_Purple`。**指定的主题不存在时直接报错**，绝不静默回退 |

⚠ **未知参数一律 fail fast**（拼错参数名会直接抛 `ArgumentException` 并以非零码退出，不会被静默忽略）。
2026-10-21 实测踩过：把轮数参数拼成 `--appearance-paired-cpu-rounds`（正确名是 `--appearance-paired-rounds`），
探针只打了个警告就按默认 3 轮跑完，结果被当成 5 轮解读。用 `--help` 看简表。

⚠ **CPU 口径测量很贵**（每帧离屏渲染本身就要数毫秒到数十毫秒，
默认参数下一次完整 CPU 口径运行是**分钟级**），因此**默认关闭**、由开关显式开启。

```powershell
# 只跑 CPU 口径配对测量（跳过常规夹具矩阵与经过时间配对测量）
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --appearance-only --appearance-paired-cpu `
    --out tests/LiteMonitor.PerformanceProbe/output/paired-cpu

# 在既有配对流程里一并产出 CPU 口径字段（矩阵里默认只测第一个夹具）
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --appearance-only --appearance-paired --appearance-paired-cpu `
    --appearance-set quick --appearance-filter "DarkFlat_Classic__vertical__gpu1__dpi96" `
    --out tests/LiteMonitor.PerformanceProbe/output/paired-cpu-in-matrix
```

---

## 7. 已知限制

- 探针驱动的是 `UpdateAll()`，**不是**完整的 WinForms 窗口：不含窗口呈现、桌面合成、
  透明效果、任务栏嵌入。完整窗口测量属于 Task 6（已在 verification 文档 §9.7 用**完整程序**
  单独做了两次运行，但只做到**单次 40 s 窗口观察**，不足以做可下结论的三轮对比）。
- Web 快照/广播的取值发生在应用进程内，**不计入** `valueReads`；只有进程 CPU 总量包含它们。
- `webSocketFramesReceived` 数的是**收到的 WebSocket 文本帧**（服务端每秒广播一次快照）。
  它为 0 表示连接没真正收到数据，不是"没数据可发"。
- FPS 证据段分两问：
  - 死 PID 残留：按生产的"每 PID 状态"结构播种合成 PID，看是否被清理、是否每轮重复查询。
    探针按**成员形状**（`RecordFrame` / `AdvanceCycle` / `ProcessCount`）定位该结构，
    不依赖具体类型名；定位不到就输出 `skipped` 并说明，**不编造数字**。
  - Web 隐藏 FPS：分「无需求」与「有 Web 需求」两个场景，验证网页快照**是否真的去读 FPS**，
    需求信号用生产自己的需求状态对象登记（不建立真实网络连接）。
    ⚠ **"有需求"场景的期望值已过期**（见 §6.8）——生产加了 `hasFpsData` 守卫，
    合成状态没有可信数据时**正确地不读**，探针这一格会显示"不符合期望"。
    **场景①（无需求 → 不读）才是当前可用的行为级证据。**
- FPS 需求组（§6.2 的 I 组）**未实机验证**：启动真实 PresentMon 需要下载外部程序，本轮禁止。
  探针只验证不依赖外部服务的 FPS 状态行为。
- `--quick` 的数据**不能**当基线，只用于验证流程能跑通。
- 跨批次比较**必须**先看 `counterSemantics`（§6.1–§6.3）：同名字段在不同批次里含义可能不同。
- `*Recomputed` 系列是**旧取模语义推算值**，与生产侧真实计数**不可相减**（§6.1、§6.6）。
- 退出码：0 = 正常；1 = 探针异常；**2 = 硬件层不可用 / 驱动未就绪，没有产出硬件测量数据**。
  退出码 2 的结果文件不能当基线用，先看 `hardwareReady.ready`。

---

## 8. 外观探针入口（`--appearance-only`，A04）

> 这是**独立入口**，不是既有功能。默认路径（不传 `--appearance-only`）的行为**完全不变**。

### 8.1 它和默认路径的区别

| | 默认路径（采集探针） | `--appearance-only`（外观探针） |
|---|---|---|
| 创建 `HardwareMonitor` | ✅ | ❌ **完全不创建** |
| `Open()` 真实硬件 / 读驱动 | ✅ | ❌ |
| PawnIO 驱动就绪检查 / 装卸驱动 | ✅（只读检查） | ❌ **连检查都不做** |
| 启动 PresentMon / ETW / 下载外部程序 | ❌ | ❌ |
| 读 / 写用户 `settings.json` | ❌ | ❌ |
| 输出目录 | `--out` | `--out`（建议 `output\appearance-baseline`） |
| 退出码含义 | 0/1/2（见 §7） | 0 = 正常；1 = 外观探针异常 |

外观探针自己 `new Settings()` 并注入 `Settings._instance` 单例（复用
`ProbeEnvironment.InstallSettingsSingleton`），因此**不读也不写**用户常用配置目录；
所有产物都落在自己的 `--out` 目录里。

### 8.2 怎么跑

```powershell
# 构建
dotnet build tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj -c Release

# 完整外观基线（10 个旧主题 × 竖/横 × 1/4 卡 × 4 个 DPI × 2 个用户缩放 + 4 个边界场景）
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --appearance-only `
    --out tests/LiteMonitor.PerformanceProbe/output/appearance-baseline

# 与上一份基线逐像素对比（改完代码后复跑用）
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --appearance-only `
    --out tests/LiteMonitor.PerformanceProbe/output/appearance-after `
    --compare tests/LiteMonitor.PerformanceProbe/output/appearance-baseline
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `--appearance-only` | — | 走外观探针入口 |
| `--appearance-set` | `full` | `full` 完整叉乘 / `core` 只取 96+144 DPI / `edge` 只跑边界场景 / `quick` 只取 96 DPI+1.0（冒烟，**不可当基线**） |
| `--appearance-draws` | 20 | 每个夹具的测量绘制次数（指标值持续变化） |
| `--appearance-invalidation` | `all` | `all` / `sample`（只跑第一个主题）/ `none` |
| `--compare <dir>` | — | 与某份基线结果做逐像素对比（读 `<dir>\appearance-results.json`） |

### 8.3 产出物

```
<out>\
├── appearance-results.json   结果（夹具矩阵 + 每个夹具的指标 + 局部失效证据 + 可选的对比结果）
├── appearance-run.txt        运行日志
└── png\
    ├── <fixtureId>.png       每个夹具一张离屏位图
    └── ...
```

`fixtureId` 形如 `DarkFlat_Classic__vertical__gpu4__dpi144__scale2.0__w240`，
边界场景会再追加 `__<edgeId>`。

### 8.4 逐像素对比怎么用

每个夹具都有两个哈希：

- `pixelSha256` —— **原始 ARGB 像素**的哈希（未编码），**前后对比以此为准**；
- `pngSha256` —— PNG 文件哈希（附加证据）。

跑 `--compare <基线目录>` 后，结果 JSON 的 `comparison` 段会给出
`identical` / `changed` / `newInCurrent` / `missingInCurrent` 四个计数和逐夹具明细。

### 8.5 夹具数据

固定夹具数据在 `tests/AppearanceFixtures/appearance-fixtures.json`（唯一事实来源），
构建时复制到探针输出目录的 `appearance-fixtures\` 下。详见该目录的 README。

### 8.6 已知限制（必须随结果一起读）

- 离屏渲染**不包含** DWM / 窗口呈现 / 桌面合成 / 窗口透明度 / 任务栏。
  结果 JSON 的 `surfaceDeclaration` 段会显式声明这一点。
- `dpi` 与 `userScale` 都是**离屏模拟**（`dpiSource` / `userScaleSource` =
  `offscreen-simulated`）。布局尺寸走生产同一条 `Theme.Scale(dpiScale, userScale)` 路径，
  字形点值再乘 `dpiScale` 逼近真实高 DPI 窗口的 GDI 光栅化。
  **这不能替代 v4 §7.2 要求的真实 96/120/144/192 DPI 窗口测试。**
- 横屏的列间距由 `HorizontalLayout` 内部按**进程真实系统 DPI** 缩放
  （`Graphics.FromHwnd(IntPtr.Zero)`），离屏模拟改写不了。
- `--appearance-set quick` 的数据**不可当基线**。

