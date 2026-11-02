# LiteMonitor++ 取证工具集

> **这是工具包，不是修复包。**
> 本目录**只提供取证能力**：采样、采集、收集、脱敏。
> 它**不判定**任何上游 issue 是否成立、是否已修复、是否可关闭。
> 所有「是否泄漏 / 是否归因于本程序 / 是否 GCL 未初始化」的判断，都由拿到数据的人做。

关联上游 issue：**#416 / #78 / #582 / #574 / #592**

---

## 1. 工具清单

| 脚本 / 文件 | 一句话用法 | 覆盖 issue |
|---|---|---|
| `mem-timeline.ps1` | 采 ≥72h 的**私有字节**曲线（并行记录工作集但不作依据），输出 CSV + JSON | **#78** |
| `wpa-capture.ps1` | 按场景起 ETW 采集，产出可开 WPA 的 `.etl`（CPU Usage (System) / DPC / ISR） | **#416** |
| `scenarios.psd1` | #416 的**分场景开关清单**（每个场景只改一个变量，附代码位置） | **#416** |
| `collect-crash-evidence.ps1` | 收崩溃日志（**含旧名 `LiteMonitor_Error.log`**）、报告者二进制版本、事件日志，并可开启 WER LocalDumps | **#582 / #574** |
| `lhm-intel-report.ps1` | 导出 LHM `GetReport()` 与全部传感器值，区分「键不存在」与「键存在但值恒 null」 | **#592** |
| `LhmIntelReport/` | 上面那条命令背后的探针工程（只引用 LHM 0.9.6，零新增依赖） | **#592** |
| `B580-CHECKLIST.md` | B580 真机的人工检查清单（驱动/GCL/LHM/应用侧对照） | **#592** |
| `Common.psm1` | 公共助手：沙箱化、脱敏、原生进程、环境事实 | （全部） |
| `selftest.ps1` | **一条命令**自检整个工具集（脱敏断言 + 各脚本干跑 + 产物断言） | （全部） |

---

## 2. 前置条件

| 要求 | 说明 | 缺了会怎样 |
|---|---|---|
| **PowerShell 7+** | 必须 `pwsh.exe`。5.1 会因 `scripts/Resolve-Dotnet.ps1` 的 `#requires -Version 7.0` 拒跑 | 脚本启动即报错并提示 |
| **管理员权限** | `wpr -start` / `xperf -on` / LHM 打开硬件树 / 读 Application 事件日志 | 对应工具明确报错并停下，不静默成功 |
| **Windows Performance Toolkit** | `xperf.exe` / `wpa.exe`（随 WDK 提供）。`wpr.exe` 是 Windows 自带 | 无工具时生成《人工命令单》并以退出码 2 结束 |
| **.NET 8 SDK** | 仅 `lhm-intel-report.ps1` 需要（构建探针） | 该命令不可用，其余工具不受影响 |
| 目标机器 | #416/#78 要在**报告者机型**上跑；#592 要在 **Intel Arc B580 真机**上跑 | 见 §7 未覆盖场景 |

---

## 3. 隔离与安全保证（硬约束）

1. **绝不覆盖用户 `settings.json`。**
   LiteMonitor++ 的配置路径是 `AppContext.BaseDirectory`（`src/Core/SettingsHelper.cs:22`），
   所以沙箱的唯一可靠做法是：**把整个构建产物目录复制到别处再从那里启动**
   （`Common.psm1` 的 `New-SandboxAppInstance`）。
   ⇒ 沙箱副本的 `settings.json` / `TrafficHistory.json` / `LiteMonitor++_Error.log` **全部落在沙箱里**。
2. **沙箱副本与用户实例可以并存**：单实例互斥体按 exe 所在**目录路径**生成
   （`src/System/Program.cs:43-65`），不同目录 ⇒ 不同互斥体名。
3. **只按沙箱目录结束进程**（`Stop-SandboxAppInstance`），绝不按进程名批量杀，
   避免误伤用户正在跑的实例。
4. **输出脱敏**：口令 / 票据 / CSRF / 会话 cookie / 机器 GUID / 卷序列号 / 完整设备序列号
   一律替换（`Common.psm1` 的 `Protect-ForensicsText`）。
5. **证据不进版本库**：默认输出在 `.evidence/forensics/`，该路径被 `.gitignore` 忽略。
6. **除 `-EnableLocalDumps` 外不改系统状态**；WER 注册表项写在 `HKCU` 下，可随时用
   `-DisableLocalDumps` 撤销。

---

## 4. 各工具详细用法

### 4.1 `mem-timeline.ps1` —— #78 私有字节时间线

**为什么主口径是「私有字节」**：程序自身每 300s 会对工作集 > 30MB 的进程调用
`EmptyWorkingSet`（`src/System/HardwareServices/SystemOptimizer.cs:171-183`），
任务管理器里的「内存」被**主动压低**成锯齿 ⇒ **不能**作泄漏依据。
工作集会被并行记录，但列在 `referenceOnlyMetrics` 里。

```powershell
# ① 自检（不采真实数据）
pwsh tools/forensics/mem-timeline.ps1 -DryRun

# ② 观察用户正在运行的实例 72h（只读，不改任何配置）
pwsh tools/forensics/mem-timeline.ps1 -DurationHours 72 -IntervalSeconds 60

# ③ 用沙箱副本跑 72h（完全不碰用户 settings.json）
pwsh tools/forensics/mem-timeline.ps1 `
    -LaunchSourceDir bin\Release\net8.0-windows `
    -DurationHours 72 -IntervalSeconds 60

# ④ 带自定义配置（只写沙箱）
pwsh tools/forensics/mem-timeline.ps1 `
    -LaunchSourceDir bin\Release\net8.0-windows `
    -SettingsJsonPath .\my-settings.json
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `-ProcessName` | `LiteMonitor++` | 目标进程名（同名多个取最早启动的） |
| `-ProcessId` | — | 直接指定 PID |
| `-IntervalSeconds` | `60` | 采样间隔。72h 建议 60~300 |
| `-DurationHours` | `72` | 总时长（上限 720） |
| `-LaunchSourceDir` | — | 主工程**构建输出目录**；给了就起沙箱副本 |
| `-SettingsJsonPath` | — | 写进**沙箱**的 settings.json |
| `-OutputDir` | `docs\reviews\evidence\forensics\mem-timeline\<时间戳>` | 证据目录 |
| `-DryRun` | — | 只自检：校验参数、准备沙箱、连采 2 个样本、写 manifest |

**时间线格式**：`timeline.csv`（UTF-8，首行表头）

| 列 | 含义 |
|---|---|
| `timestamp_utc` / `timestamp_local` | 采样时刻（ISO-8601） |
| `elapsed_seconds` | 距采样开始的秒数（单调墙钟） |
| `pid` / `alive` | 进程号；`alive=0` 表示进程已退出或采样失败 |
| **`private_bytes`** | **私有字节 —— 主口径（#78 的依据）** |
| `working_set_bytes` | 工作集 —— **仅参考**，被 `EmptyWorkingSet` 压低，不作依据 |
| `paged_bytes` | 分页提交量 |
| `virtual_bytes` | 虚拟地址空间大小 |
| `handle_count` | 句柄数 |
| `thread_count` | 线程数 |
| `gdi_objects` / `user_objects` | GDI / USER 对象数（GDI 泄漏取证） |
| `cpu_total_seconds` / `cpu_delta_seconds` | 进程累计 CPU 时间 / 本次增量 |
| `note` | 事件备注（如 `process-not-found`） |

`summary.json` 另给出**测量值**：首末值、极值、差值、以及最小二乘**线性斜率**
（`slopeBytesPerHour` / `slopeMiBPerDay`）。
⚠ **斜率只是测量值，不是「存在泄漏」的结论**；是否为泄漏由人判读。

> **配套但本工具不覆盖**：`poolmon` 的**非分页池**是**系统级**指标，不属于进程私有字节，
> 需另行用 `poolmon /n` 采样（需要 Windows Driver Kit）。

### 4.2 `wpa-capture.ps1` + `scenarios.psd1` —— #416 内核 CPU 归因

```powershell
# ① 看场景清单（含每个场景实际动到的开关与代码位置）
pwsh tools/forensics/wpa-capture.ps1 -ListScenarios

# ② 基线：应用完全关闭（System 进程的机器本底）
pwsh tools/forensics/wpa-capture.ps1 -Scenario app-closed -Minutes 30

# ③ 默认配置
pwsh tools/forensics/wpa-capture.ps1 -Scenario default -Minutes 30

# ④ 逐个开关
pwsh tools/forensics/wpa-capture.ps1 -Scenario smb-on            -Minutes 30
pwsh tools/forensics/wpa-capture.ps1 -Scenario fps-on            -Minutes 30
pwsh tools/forensics/wpa-capture.ps1 -Scenario gpu-all           -Minutes 30
pwsh tools/forensics/wpa-capture.ps1 -Scenario etw-exclusion-on  -Minutes 30

# ⑤ 自检（不实际采集）
pwsh tools/forensics/wpa-capture.ps1 -Scenario default -DryRun
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `-Scenario` | `default` | 场景 id，见 `scenarios.psd1` |
| `-Minutes` | 场景自带（30） | 采集时长 |
| `-WarmupMinutes` | 场景自带 | 启动后到开始采集之间的静默期 |
| `-LaunchSourceDir` | `bin\Release\net8.0-windows` | 主工程构建输出目录 |
| `-WprProfile` | `CPU` | wpr 内置 profile |
| `-ListScenarios` | — | 只打印场景清单 |
| `-DryRun` | — | 自检：探测 wpr/xperf、准备沙箱、写 manifest |

**采集后端**：`wpr.exe`（Windows 自带）优先，其次 `xperf.exe`（需 WDK）。
两者都没有时，脚本会生成 `MANUAL-COMMANDS.md`（含可直接复制的命令）并以**退出码 2** 结束。

**场景矩阵摘要**（每个场景只改一个变量；`nonLevers` 里诚实标注了产品**没有**的开关）

| 场景 | 动的开关 | 隔离什么 |
|---|---|---|
| `app-closed` | 不启动应用 | 机器本身的 System 进程内核 CPU 基线 |
| `default` | 无（走默认值） | 与 `app-closed` 的差 = 程序造成的**全部**系统侧增量 |
| `smb-on` / `smb-off` | `IgnoreSmbTraffic`（`src/Core/Settings.cs:153`） | 每轮读 SMB 共享速率计数器的成本 |
| `fps-on` / `fps-off` | `WebFpsEnabled`（`src/Core/Settings.cs:269`） | FPS 采集服务（外部 ETW）的成本 |
| `gpu-all` / `gpu-one` | `MonitoredGpuCount`（`src/Core/Settings.cs:291`） | 多一张卡的显示驱动调用成本 |
| `etw-exclusion-on` | `IpTrafficExclusionEnabled` + `ExcludedTrafficIps` | ETW 网络事件源的内核成本 |
| `low-refresh` / `high-refresh` | `RefreshMs`（`src/Core/Settings.cs:24`） | 采集频率的放大效应 |
| `no-win-counters` | `UseWinPerCounters`（`src/Core/Settings.cs:150`） | Windows 性能计数器路径 vs LHM 直读 |

> ★ **产品没有「完全关闭 GPU 传感器采样」的开关**（`MonitoredGpuCount` 被夹到 ≥1，
> 见 `src/Core/GpuItemSync.cs:43-48`）；`--safe` 只关 GPU **渲染**路线、**不**关传感器采样。
> ⇒ 「GPU 采样为零」只能用 `app-closed` 近似。

**WPA 里看什么**：`CPU Usage (System)` 火焰图 ⇒ DPC/ISR 占比与栈；
`CPU Usage (Precise)` ⇒ 具体驱动/内核函数。
**必须与 `app-closed` 并排比较**——单看一张图无法归因。

### 4.3 `collect-crash-evidence.ps1` —— #582 / #574 崩溃取证

```powershell
# ① 自检（不做任何改动）
pwsh tools/forensics/collect-crash-evidence.ps1 -DryRun

# ② 收齐现有证据
pwsh tools/forensics/collect-crash-evidence.ps1

# ③ 同时打开 WER LocalDumps，等下一次崩溃留下 .dmp
pwsh tools/forensics/collect-crash-evidence.ps1 -EnableLocalDumps

# ④ 报告者机器上：明确指定安装目录
pwsh tools/forensics/collect-crash-evidence.ps1 -InstallDirs 'C:\TOOLS\LiteMonitor'

# ⑤ 撤销 LocalDumps
pwsh tools/forensics/collect-crash-evidence.ps1 -DisableLocalDumps
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `-InstallDirs` | 自动搜索 | 明确指定运行目录 |
| `-SearchRoots` | 见下 | 自动搜索的根目录 |
| `-EnableLocalDumps` / `-DisableLocalDumps` | — | 开关 WER LocalDumps（`HKCU`） |
| `-LocalDumpDir` | `<OutputDir>\dumps` | `.dmp` 落地目录 |
| `-DumpType` | `2`（完整） | 0=无 1=小型 2=完整 |
| `-EventHours` | `168`（7 天） | 回看多少小时的事件日志 |

**默认搜索根目录**：`C:\TOOLS\LiteMonitor`、`%LOCALAPPDATA%`/`%APPDATA%` 下的 `LiteMonitor(++)`、
`%ProgramFiles%`、`%ProgramFiles(x86)%`、`%USERPROFILE%\Desktop`、当前目录。
（深度上限 3 层，跳过 `Windows`/`Installer`/`Temp` 等大目录。）

**日志名兼容查找**（#582/#574 都要）：

| 文件名 | 来源 | 说明 |
|---|---|---|
| `LiteMonitor++_Error.log` | 当前品牌名（`src/System/Program.cs:149`） | 崩溃日志 |
| `LiteMonitor_Error.log` | **旧品牌名**（`docs/REBRAND.md` B5） | 老用户目录里可能还是这个名字 ⇒ 兼容查找 |
| `LiteMonitor++_HardwareError.log` | 硬件层异常（`HardwareErrorLog.cs`） | 硬件树报错 |

**产出**：`summary.txt`（人读汇总）、`manifest.json`（机读）、`application-events.txt`
（Application Error / .NET Runtime 事件）、`logs/`（**脱敏**后的日志副本）。

**报告者二进制版本**（#574 明确要求）：每个找到的 `LiteMonitor++.exe` 都会记录
`FileVersion` / `ProductVersion` / `ProductName` / 文件 SHA256。

### 4.4 `lhm-intel-report.ps1` —— #592 LHM 报告

```powershell
# ① 自检
pwsh tools/forensics/lhm-intel-report.ps1 -DryRun

# ② 在 B580 真机上以管理员身份运行
pwsh tools/forensics/lhm-intel-report.ps1 -ShowDetail
```

产出 `lhm-report.txt` / `lhm-report.json` / `sensors.csv` / `manifest.json`。
详见 [`B580-CHECKLIST.md`](B580-CHECKLIST.md)。

> ★ **判据位置**：`Intel GCL Initialized: True/False` 这一行出现在
> **整机报告**（`IComputer.GetReport()`）的 `Intel GPU Detection` 段里，
> **不在**单个显卡的 `GetReport()` 里 —— 本机（AMD + 双 NVIDIA）实测确认。
> 探针把整机报告与每个显卡的报告**都**导出，并把含 `GCL` 的行单独汇总。
>
> ★ LHM 0.9.6 的 `Computer` **未公开 `Groups`** ⇒ 分组级 `GetReport()` 取不到，
> 探针改用整机报告作等价替代（它含全部分组的传感器树）。

---

## 5. 场景开关清单的完整口径

`scenarios.psd1` 里每个场景都有：

| 字段 | 含义 |
|---|---|
| `launchApp` | 是否启动程序（`false` = 应用关闭基线） |
| `settings` | 写进**沙箱** `settings.json` 的覆盖项（`$null` = 全默认） |
| `captureMinutes` / `warmupMinutes` | 采集时长 / 启动后的静默期 |
| `purpose` | 这个场景要隔离什么 |
| `levers` | 实际动到的开关 **+ 代码位置** |
| `nonLevers` | **产品没有**、无法用配置表达的部分（诚实标注） |
| `compareWith` | 建议与之成对比较的场景 |

---

## 6. 干跑记录（本机自检）

本机：Windows 11（10.0.26300）· AMD Ryzen 7 5800X · 双 NVIDIA（RTX 3080 / 3080 Ti）·
PowerShell 7.6.6 · .NET SDK 8.0.426 · `wpr.exe` 存在 · `xperf.exe` / `wpa.exe` / `poolmon.exe` **不存在** ·
**非管理员** · PawnIO **未注册**。

> **一条命令复现全部自检**：

```powershell
pwsh tools/forensics/selftest.ps1 -BuildProbe
```

> 该用例集当前**通过 8 / 失败 0**。

| 工具 | 干跑命令 | 实测结果 |
|---|---|---|
| `selftest.ps1` | `-BuildProbe` | ✅ 8/8 通过 |
| `mem-timeline.ps1` | `-DryRun` | ✅ `timeline.csv`/`summary.json`/`manifest.json` 齐备，主口径列齐全 |
| `mem-timeline.ps1` | `-DryRun -LaunchSourceDir bin\Release\net8.0-windows` | ✅ 沙箱 `app/` 下 39 个文件 + 9 个语言包；用户 `settings.json` 未被触碰 |
| `mem-timeline.ps1` | `-ProcessId <pid> -IntervalSeconds 2`（真实长跑进程） | ✅ 10 条样本，`private_bytes` / `working_set_bytes` / `handle_count` / `thread_count` / `gdi_objects` / `user_objects` 全部真实取值 |
| `wpa-capture.ps1` | `-ListScenarios` | ✅ 12 个场景全部列出（含 `levers` / `nonLevers`） |
| `wpa-capture.ps1` | `-Scenario smb-on -DryRun` | ✅ 沙箱 `settings.json` = `{"IgnoreSmbTraffic": false}` |
| `wpa-capture.ps1` | `-Scenario app-closed -Minutes 1`（实跑） | ⚠ 非管理员 ⇒ `wpr -start` 返回 `0xc5585011` ⇒ 脚本**按预期报错并以非 0 退出**（权限失败，非脚本缺陷） |
| `collect-crash-evidence.ps1` | `-DryRun` | ✅ 找到 `LiteMonitor++.exe`（1.0.0.0）；事件日志命中 18 条；**未改动注册表** |
| `lhm-intel-report.ps1` | `-Build -ShowDetail` | ✅ 打开 LHM 硬件树；枚举 2 张 NVIDIA 卡（各 38 个传感器）；导出 `GetReport()` 全文（含 `Driver Version: 610.88`）+ `sensors.csv`；`intelGpuPresent=false`（本机无 Intel 显卡，属预期降级路径） |

> 干跑**不采集**真实 72h 数据，也不产出任何 issue 判定。
>
> ⚠ **本机未启动真实产品实例**：PawnIO 未注册，启动会触发**驱动安装**（需明确授权）。
> 沙箱的**结构与隔离**、采样**数据通路**已分别验证（见上表第 2、3 行）。

---

## 7. 本工具包**不**覆盖的事

| 事项 | 原因 / 归属 |
|---|---|
| 72h 长跑的**真实数据** | 需在目标机连续运行 72h（用户/责任人执行） |
| #416 的**归因结论** | 需要 WPA 火焰图人工判读；本工具集只提供采集能力 |
| #78 是否**构成泄漏** | 需要长跑曲线人工判读；本包只给测量值与斜率 |
| #582 的**根因** | 需要报告者提供 `LiteMonitor_Error.log` 附件原文（issue 里附件未取得） |
| #574 的**异常源** | 需要报告者二进制版本 + 日志全文 + 主题/DPI 切换时序 |
| #592 的 **B580 实机数据** | 开发机无 Intel 显卡；需 B580 真机执行本目录工具 |
| 任何 issue 的**关闭判定** | 关闭条件见母计划 §7，**不在工具包职责内** |
