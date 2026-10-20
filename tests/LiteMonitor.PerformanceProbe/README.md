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
| 慢扫描 / 保存次数 | **直接读生产侧触发计数**（慢扫描/后台磁盘扫描/流量保存/GC 请求/工作集裁剪），并与探针按取模复算的值交叉验证 | §4.3 |
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
├── WebActivity.cs        最小 WebSocket / HTTP 客户端（只用 BCL）
├── MicroBenchmarks.cs    单设备 Update 成本 + 兼容缓冲绘制
├── FpsStateProbe.cs      失效 PID 残留 + Web 隐藏 FPS
├── output\               ★ 隔离运行期输出目录（已 git 忽略）
└── baseline\             ★ 已提交的基线原始数据
    ├── probe-results.json   第 1 轮完整基线
    ├── probe-run.txt        第 1 轮运行日志
    └── repeat\              第 2 轮独立重复（用于量化单轮波动）
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

## 6. 计数口径的时效性（改调度之后会失效的列）

| 字段 | 口径 | 什么时候失效 |
|---|---|---|
| `collectionRounds` | 生产侧**实际采集**轮数（`CollectionRounds` 增量） | 不失效 |
| `collectionRequests` | 探针发起的采集请求次数 | 不失效 |
| `productionDeviceUpdates` | 生产侧每设备 Update 次数 | 生产改设备 Key 规则时 |
| `deviceUpdateCounters` | LHM 代理直接数的 `IHardware.Update()` 调用 | 改硬件重载/代理注入路径时 |
| `valueReads` | 探针自己的 `Get()` 调用 | 不失效 |
| `slowScanTriggerRounds` / `trafficSaveDueRounds` / `gcRequestDueRounds` / `workingSetTrimDueRounds` | **直接读生产侧触发计数**（`SamplingStatistics`） | 不失效（生产改调度语义时数值自动跟着变） |
| `*Recomputed` 系列 | ⚠ 探针按当前生产实现的整数秒取模语义**复算**（`%3` / `%10` / `%60==5` / `%180==30` / `%300==45`），只用于交叉验证 | **计划 §4.3 把生产改成单调截止时间之后立即失效** |
| `dueSemanticsRecomputationAgrees` | 复算值与生产计数是否逐项相等 | 生产改调度语义后会变 false —— 那就是要改口径的信号 |
| `trafficFileObservedRewrites` | 轮询文件 `LastWriteTime` 变化得到的**下界** | 不失效，但永远 ≤ 真实写入次数 |

取模判定集中放在 `SamplingRunner.MaintenanceDueSemantics`，常量来源在文件头的注释里。
交叉验证字段 `trafficRewriteAgreesWithDueRounds`：
"生产侧实际触发的流量保存次数"与"实际观测到的文件重写次数"差值 > 1 时为 false。

---

## 7. 已知限制

- 探针驱动的是 `UpdateAll()`，**不是**完整的 WinForms 窗口：不含窗口呈现、桌面合成、
  透明效果、任务栏嵌入。完整窗口测量属于 Task 6。
- Web 快照/广播的取值发生在应用进程内，**不计入** `valueReads`；只有进程 CPU 总量包含它们。
- `webSocketFramesReceived` 数的是**收到的 WebSocket 文本帧**（服务端每秒广播一次快照）。
  它为 0 表示连接没真正收到数据，不是"没数据可发"。
- FPS 证据段分两问：
  - 死 PID 残留：按生产的"每 PID 状态"结构播种合成 PID，看是否被清理、是否每轮重复查询。
    探针按**成员形状**（`RecordFrame` / `AdvanceCycle` / `ProcessCount`）定位该结构，
    不依赖具体类型名；定位不到就输出 `skipped` 并说明，**不编造数字**。
  - Web 隐藏 FPS：分「无需求」与「有 Web 需求」两个场景，验证网页快照**是否真的去读 FPS**，
    需求信号用生产自己的需求状态对象登记（不建立真实网络连接）。
- FPS 需求组（§6.2 的 I 组）**未实机验证**：启动真实 PresentMon 需要下载外部程序，本轮禁止。
  探针只验证不依赖外部服务的 FPS 状态行为。
- `--quick` 的数据**不能**当基线，只用于验证流程能跑通。
- 退出码：0 = 正常；1 = 探针异常；**2 = 硬件层不可用 / 驱动未就绪，没有产出硬件测量数据**。
  退出码 2 的结果文件不能当基线用，先看 `hardwareReady.ready`。
