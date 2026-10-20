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
| 每卡 Update 次数 | 通过 LHM 硬件对象代理，统计应用**真实采集路径**里的 `IHardware.Update()` 调用次数 | §4.1 / §6.1 |
| 采集代次 | 统计 `UpdateAll()` 轮数（当前实现中 1 轮 = 1 个采集代次） | §4.2 |
| 慢扫描 / 保存次数 | 读应用自己的 `_secondsCounter`，统计慢扫描、后台磁盘扫描、流量保存、GC 请求、工作集裁剪的**到期轮数**，并观测流量文件真实重写次数 | §4.3 |
| 单设备 Update 成本 | 对每个真实硬件对象直接调用 `Update()`，测墙钟与进程 CPU 时间 | §2 |
| 兼容缓冲绘制 | 用真实 `UILayout`/`UIRenderer`/`HorizontalRenderer` 在 WinForms 兼容缓冲画布上测绘制成本 | §2 / §6.2 |
| FPS 状态 | 失效 PID 残留、Web 快照读取隐藏 FPS（**不启动任何外部服务**） | §4.4 / §4.5 |

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

1. `HardwareMonitor.UpdateAll()` —— 一次真实硬件采集轮；
2. 依次读取当前启用的监控项 —— 界面/Web 的取值；
3. 等待到**周期网格上的下一个刻度**再开始下一轮。

第 3 点与 WinForms 定时器一致：忙碌时丢帧、不会补跑错过的轮次。
所以"100 ms 刷新"在这里就是"每 100 ms 尝试采集一轮"，采集本身耗时超过周期时轮数会自然下降。

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
安装后立刻跑一轮验证计数是否真的被应用调用，结果写在 `updateCounterDiagnostics` 里。

因此**显卡（NVIDIA/Intel）与内存的每卡 Update 次数是直接实测值**；
CPU / 网卡 / 磁盘 / 主板在进程外无法直接计数，报告里用「轮数 + 每轮耗时 + 组间差分」说明，
并明确标注为推算。

> **后续 Task 2 必须做的事**：把这个计数点接进 `HardwareMonitor.UpdateAll()` 内部，
> 才能得到应用真实采集路径里全部设备的直接计数。计划 §4.1/§4.2 落地后，
> 探针应改为调用新的共享采集入口，而不是直接调 `UpdateAll()`。

---

## 6. 已知限制

- 探针驱动的是 `UpdateAll()`，**不是**完整的 WinForms 窗口：不含窗口呈现、桌面合成、
  透明效果、任务栏嵌入。完整窗口测量属于 Task 6。
- Web 快照/广播的取值发生在应用进程内，**不计入** `valueReads`；只有进程 CPU 总量包含它们。
- FPS 需求组（§6.2 的 I 组）**未实机验证**：启动真实 PresentMon 需要下载外部程序，本轮禁止。
  探针只验证不依赖外部服务的 FPS 状态行为。
- `--quick` 的数据**不能**当基线，只用于验证流程能跑通。
