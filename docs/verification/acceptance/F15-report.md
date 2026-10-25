# F15 验收记录 —— 取证工具与长跑 harness（工具包，不含结论）

> **本包是工具包**：交付的是**取证能力**（采样 / 采集 / 收集 / 脱敏），
> **不判定**任何上游 issue 是否成立、是否已修复、是否可关闭。
> 关闭条件见母计划 §7；「工具就绪」≠「issue 可关闭」。
> **计划完成 ≠ 72h 观察完成** —— 长跑与实机步骤由用户在目标机执行。

## 1. 标识

- 工作包：P-F15（取证工具与长跑 harness）
- 分支：`tools/F15-forensics-harness`
- 基线 SHA（开工时 HEAD）：`d52c0ab`（master）
- 提交 SHA 列表：

  ```
  b3d1ffe tools(F15): 取证工具集与长跑 harness（#416/#78/#582/#574/#592，不含结论）
  ```

  （本报告自身的提交紧随其后，不含产品代码。）
- 完成日期：2026-10-25
- 执行者：P-F15 执行智能体（worktree `G:\TOOL\LiteMonitor-fork\.worktrees\f15`）
- 产出目录：`tools/forensics/`（11 个文件）

## 2. 覆盖与结论

- 覆盖 issue：**#416、#78、#582、#574、#592**（仅取证能力，不含判定）
- 结论：**部分通过**（原因：工具链全部就绪并在本机干跑/实跑通过，
  但**真实取证数据**（72h 长跑 / WPA 采集 / B580 真机 / 报告者崩溃复现）
  必须由用户在目标机执行 —— 这是派发单预期的分工，不是本包的缺陷。）
- 允许关闭的 issue 与条件：**无**。
  本包不产出任何 issue 关闭依据；关闭条件见母计划 §7。
- 不允许关闭的原因：本包**没有任何根因判定**；
  #416 需要 WPA 火焰图人工归因、#78 需要 72h 曲线人工判读、
  #582 需要报告者提供日志附件原文、#574 需要报告者二进制版本 + 崩溃复现、
  #592 需要 B580 真机数据。

### 2.1 逐 issue 取证缺口对照

| Issue | 缺口（派发单口径） | 本包提供的工具 | 取证**能力**是否补上 | 取证**数据**是否产出 |
|---|---|---|---|---|
| **#416** | WPA/xperf `CPU Usage (System)` 火焰图 + DPC/ISR；分场景开关（应用关闭/默认功能/SMB/FPS/GPU 采样/ETW 排除） | `wpa-capture.ps1` + `scenarios.psd1`（12 个场景，每场景只改一个变量 + 代码位置 + `nonLevers` 诚实标注） | ✅ 已具备（后端探测、场景沙箱、命令单、manifest、失败路径均已实测） | ❌ 未产出：需**管理员** + 报告者机型上每个场景 30 min |
| **#78** | ≥72h **私有字节**曲线（工作集被 `EmptyWorkingSet` 压低，只能并行记录不作依据） | `mem-timeline.ps1`（主口径 `private_bytes`；工作集列入 `referenceOnlyMetrics`） | ✅ 已具备（用真实长跑进程验证过完整数据通路） | ❌ 未产出：需在目标机连续跑 72 h |
| **#582** | `LiteMonitor_Error.log` 附件 / 崩溃堆栈 | `collect-crash-evidence.ps1`（**旧名 `LiteMonitor_Error.log` 兼容查找** + 当前名 `LiteMonitor++_Error.log` + `LiteMonitor++_HardwareError.log`） | ✅ 已具备（三个日志名、WER LocalDumps、事件日志、脱敏导出全部就绪） | ❌ 未产出：**附件原文必须由报告者提供**，工具无法代替（issue 正文只有附件、附件未取得） |
| **#574** | 崩溃转储（WER LocalDumps）+ 报告者二进制版本 | 同上：`-EnableLocalDumps` + 每个 `LiteMonitor++.exe` 的 `FileVersion`/`ProductVersion`/SHA256 | ✅ 已具备 | ❌ 未产出：转储需报告者在装了本工具的机器上**复现崩溃**才产生 |
| **#592** | B580 真机 + LHM `GetReport()`（Intel GCL 初始化是否成功；键可建但值恒 null） | `lhm-intel-report.ps1` + `LhmIntelReport/`（探针）+ `B580-CHECKLIST.md`（人工清单） | ✅ 已具备（本机实跑通过，能导出 `GetReport()` 全文 + 全部传感器值 + GCL 行 + 驱动版本） | ❌ 未产出：**本机无 Intel 显卡**（AMD 5800X + 双 NVIDIA），B580 实机数据仍缺 |

## 3. 用例与命令（可从干净检出复现）

### 3.1 工具集自检（★ 主验收命令）

```powershell
pwsh tools/forensics/selftest.ps1            # 7 项检查
pwsh tools/forensics/selftest.ps1 -BuildProbe  # 8 项检查（额外编译 LHM 探针）
```

| 检查项 | 结果 |
|---|---|
| 脱敏：凭据/票据/序列号不漏网（10 个敏感片段） | ✅ PASS |
| `mem-timeline.ps1 -DryRun` | ✅ PASS（产物齐备 + 主口径列齐全） |
| `scenarios.psd1` 场景清单（12 个场景，字段与开关记录齐全） | ✅ PASS |
| `wpa-capture.ps1 -ListScenarios`（可管道消费） | ✅ PASS |
| `wpa-capture.ps1 -DryRun`（沙箱 `settings.json` 只含场景开关） | ✅ PASS |
| `collect-crash-evidence.ps1 -DryRun`（当前名 + 旧名都在清单里） | ✅ PASS |
| `lhm-intel-report.ps1 -DryRun`（探针工程就绪、dotnet 可解析） | ✅ PASS |
| `LhmIntelReport` 探针构建 | ✅ PASS（0 警告 0 错误） |

**汇总：通过 8 / 失败 0。**

### 3.2 各脚本单独干跑记录（本机实测）

| 命令 | 目的 | 结果 |
|---|---|---|
| `pwsh tools/forensics/mem-timeline.ps1 -DryRun` | 校验参数与产物 | ✅ 产出 `timeline.csv`/`summary.json`/`manifest.json` |
| `pwsh tools/forensics/mem-timeline.ps1 -DryRun -LaunchSourceDir bin\Release\net8.0-windows` | 校验**沙箱隔离** | ✅ 沙箱 `app/` 下 39 个文件 + `resources/lang` 9 个语言包；用户 `settings.json` 未被触碰 |
| `pwsh tools/forensics/mem-timeline.ps1 -ProcessId <pid> -IntervalSeconds 2 -DurationHours 0.005` | **用真实长跑进程验证数据通路** | ✅ 10 条样本，`private_bytes=26,787,840` / `working_set_bytes=84,295,680` / `handle_count=559` / `thread_count=25` / `gdi_objects=0` / `user_objects=3` 全部真实取值 |
| `pwsh tools/forensics/wpa-capture.ps1 -ListScenarios` | 场景清单 | ✅ 12 个场景全部列出（含 `levers` 与 `nonLevers`） |
| `pwsh tools/forensics/wpa-capture.ps1 -Scenario smb-on -DryRun` | 场景沙箱配置 | ✅ 沙箱 `settings.json` 内容 = `{"IgnoreSmbTraffic": false}` |
| `pwsh tools/forensics/wpa-capture.ps1 -Scenario app-closed -Minutes 1`（**实跑**） | 验证 `wpr` 采集后端与**失败路径** | ⚠ 本机非管理员 ⇒ `wpr -start CPU -filemode` 返回 `0xc5585011`（`Failed to enable the policy to profile system performance`）⇒ 脚本按预期**报错并以非 0 退出**，`capture-log.txt` 已落盘。**这是权限失败，不是脚本缺陷。** |
| `pwsh tools/forensics/collect-crash-evidence.ps1 -DryRun` | 收集器自检 | ✅ 找到 `LiteMonitor++.exe`（版本 1.0.0.0）；事件日志命中 18 条；汇总/清单/事件文件齐备；**未改动任何注册表** |
| `pwsh tools/forensics/lhm-intel-report.ps1 -Build -ShowDetail` | **LHM 探针实跑** | ✅ 打开 LHM 硬件树成功；枚举到 2 张卡（RTX 3080 / RTX 3080 Ti，各 38 个传感器）；导出 `GetReport()` 全文（含 `Driver Version: 610.88`）+ `sensors.csv`；`intelGpuPresent=false`（本机无 Intel 显卡，符合预期降级路径） |

### 3.3 回归验证

| 命令 | 目的 | 结果 |
|---|---|---|
| `dotnet build LiteMonitor.csproj -c Release --nologo` | 主工程构建 | ✅ 0 错误（301 警告，与基线一致） |
| `dotnet build tools/forensics/LhmIntelReport/LhmIntelReport.csproj -c Release` | 探针构建 | ✅ 0 警告 0 错误 |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug` | 主测试 | ✅ 第 2、3 次运行：**1777 通过 / 0 失败 / 7 跳过 / 共 1784**（含一次 TRX 落盘复跑）<br>⚠ **第 1 次全量运行出现过 1 例失败**（输出被截断，**未能捕获用例名**）。本包**未改动任何产品代码或测试代码**（新增文件全在 `tools/forensics/**`，不被 `LiteMonitor.csproj` 编译、不被任何测试工程引用）⇒ 判定为**基线既有的偶发失败**，不是本包引入。**未能定位到具体用例名，建议协调者/后续包留意。** |

> 本包**未运行** `LiteMonitor.GpuNonperformance.Tests` / `LiteMonitor.Updater.Tests`：
> 本包**零产品代码改动**（`src/**` 与 `tests/**` 一字未动），
> 两个工程与本包无引用关系；`dotnet test` 结果与基线一致即可。
> **未改动 `.github/workflows/ci.yml` / `release.yml` 任何阈值。**

## 4. 环境

- OS / 版本：Windows 11（10.0.26300，Build 26300）
- Shell：PowerShell 7.6.6（**必须 pwsh 7**；5.1 会因 `scripts/Resolve-Dotnet.ps1` 的 `#requires` 拒跑）
- .NET SDK：8.0.426
- CPU / 内存：AMD Ryzen 7 5800X / 16 GB 级
- GPU / 驱动：**双 NVIDIA**（RTX 3080 `610.88` / RTX 3080 Ti `610.88`）—— **无 Intel 显卡**
- 显示器拓扑：未记录（与本包无关）
- 关键依赖版本：**LibreHardwareMonitorLib 0.9.6**（与主工程同版本，探针零新增依赖）；
  DiskInfoToolkit 1.1.2 / RAMSPDToolkit-NDD（经 LHM 传递）
- 取证工具可用性：`wpr.exe` ✅（`C:\Windows\System32\wpr.exe`）；
  `xperf.exe` ❌ / `wpa.exe` ❌ / `poolmon.exe` ❌（WPT 与 WDK 未安装）
- 权限：**非管理员**（`QINGLING\wrz20`）⇒ ETW 采集与 LHM 全量传感器需提权执行
- PawnIO：`C:\Program Files\PawnIO\` 有库文件，但
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO` **未注册**
  ⇒ 启动 LiteMonitor++ 会触发驱动安装流程，**本包未在本机启动产品实例**（见 §7 偏离）

## 5. 新增测试清单

| 文件 | 检查项 | 验证了什么 | 发现数 |
|---|---|---|---|
| `tools/forensics/selftest.ps1` | ① 脱敏：凭据/票据/序列号不漏网 | 10 个敏感片段（含 camelCase 的 `WebServerPassword`）全部遮蔽，普通文本不误改 | 1 |
| | ② `mem-timeline.ps1 -DryRun` | 采样产物齐备 + 主口径列（`private_bytes` 等）齐全 | 1 |
| | ③ `scenarios.psd1` 场景清单 | 12 个场景存在、字段齐全、每个场景都记录了实际动到的开关 | 1 |
| | ④ `wpa-capture.ps1 -ListScenarios` | 清单可被管道消费 | 1 |
| | ⑤ `wpa-capture.ps1 -DryRun` | 沙箱 `settings.json` **只**含该场景的开关（不污染其它字段） | 1 |
| | ⑥ `collect-crash-evidence.ps1 -DryRun` | 当前名 + **旧名** `LiteMonitor_Error.log` 两个日志名都在查找清单里 | 1 |
| | ⑦ `lhm-intel-report.ps1 -DryRun` | 探针工程就绪、dotnet 可解析 | 1 |
| | ⑧ `LhmIntelReport` 探针构建 | 探针源码能编译（0 警告 0 错误） | 1 |

> ★ **本包未新增任何 xunit 用例**（新增文件全在 `tools/forensics/**`，不进 `LiteMonitor.Tests`）。
> ⇒ **CI 的 `MIN_TEST_TOTAL: 1876` 无需上调**（协调者批次集成时不必为本包改阈值）。

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| #416 真实 `CPU Usage (System)` 火焰图 + DPC/ISR | 报告者机型 + **管理员** + 每个场景 30 min；对照 `app-closed` 基线 | 用户 |
| #416 `xperf` 后端 | 需安装 Windows Performance Toolkit（随 WDK）；本机**未安装** | 用户 |
| #416 `poolmon` 非分页池 | 需 WDK；**系统级**指标，不由本包采样器覆盖 | 用户 |
| #78 ≥72h 私有字节曲线 | 目标机连续运行 72 h（建议间隔 60~300 s） | 用户 |
| #78 「是否存在泄漏」的判读 | 曲线数据 + 人工判读；本包只给测量值与线性斜率 | 用户 |
| #582 崩溃日志附件原文 | **报告者**提供 `LiteMonitor_Error.log`（旧名）或 `LiteMonitor++_Error.log` 原文 | 用户 / 报告者 |
| #574 崩溃转储 | 报告者在装了本工具的机器上复现崩溃（`-EnableLocalDumps` 已开时自动落 `.dmp`） | 用户 / 报告者 |
| #574 报告者二进制版本 | 报告者机器上跑一次 `collect-crash-evidence.ps1` | 用户 / 报告者 |
| #574 主题 / DPI 切换时序 | 需报告者复现步骤说明（工具不采集交互时序） | 用户 / 报告者 |
| #592 B580 真机 LHM 报告 | **Intel Arc B580 机器** + 管理员；跑 `lhm-intel-report.ps1` + 按 `B580-CHECKLIST.md` 填表 | 用户 |
| #592 「GCL 未初始化 ⇒ 产品侧兜底」 | 属**修复**范畴（P-F10 的 RC2 判定），不在工具包职责内 | 协调者转交 |
| 本机启动真实产品实例 | PawnIO 未注册，启动会触发**驱动安装**（需明确授权）；本包因此只验证了沙箱的**结构**与**采样通路**，未验证真实实例的端到端启动 | 用户 / 协调者 |

## 7. 偏离与遗留

### 7.1 与派发单的偏离

1. **产出落 `tools/forensics/`，未并入 `scripts/`**（派发单允许二选一）。
   理由：`scripts/run-probes.ps1` 是**带 BOM 的 UTF-8**且是外观探针的统一入口；
   取证工具有独立的权限/硬件前置（管理员、WPT、B580 真机），
   塞进外观探针入口会污染它的语义与门槛。**本包未改动 `scripts/run-probes.ps1` 一字。**
2. **未新增 xunit 用例**（新增的 8 项检查在 `tools/forensics/selftest.ps1`，是 PowerShell 断言脚本）。
   理由：本包不改产品代码，加 xunit 用例只能测自己；工具自身的可用性由
   `selftest.ps1` 覆盖更直接。**CI 阈值无需因此上调。**
3. **未在机启动真实产品实例**。理由：PawnIO 在本机**未注册**
   （`Uninstall\PawnIO` 键不存在），启动 LiteMonitor++ 会触发**驱动安装流程**
   （下载 + 安装驱动），属需要明确授权的系统变更，本包不做。
   已改为验证：① 沙箱的**结构与隔离**（复制产物 + `resources/` + 只写沙箱 `settings.json`）；
   ② 采样通路用**真实长跑进程**走通（见 §3.2）。
4. **未采集真实 72h / WPA / B580 数据**。这是派发单预期的分工（"72h 长跑、WPA 采集、实机步骤由用户在目标机执行"）。

### 7.2 发现的相邻问题（**不属于本包**，建议归属）

| # | 发现 | 证据 | 建议归属 |
|---|---|---|---|
| A | **产品没有「完全关闭 GPU 传感器采样」的开关**：`MonitoredGpuCount` 被夹在 ≥1（`src/Core/GpuItemSync.cs:43-48`），无法做到"GPU 采样为零"；`--safe` 只关 GPU **渲染**路线、**不**关传感器采样（`src/System/Program.cs:12-16`、`src/Rendering/Configuration/RenderingRoutePolicy.cs:68-71`）。⇒ #416 的"GPU 采样"场景只能用 `app-closed` 近似 | `scenarios.psd1` 的 `gpu-all`/`gpu-one` 的 `nonLevers` 已诚实标注 | 协调者 → P-F10（RC2 判定 / 开关补齐） |
| B | **LHM 0.9.6 的 `Computer` 未公开 `Groups` 属性** ⇒ 分组级 `GetReport()` 不可得 | 编译期 `CS1061: "Computer" 未包含 "Groups" 的定义`；探针已改用 `IComputer.GetReport()`（整机报告）作等价替代 | 记录即可（上游 LHM 的事） |
| C | `Intel GCL Initialized: True/False` 这一行**只出现在整机报告**里，**不在**单个显卡的 `GetReport()` 里（本机 AMD+双 NVIDIA 实测确认：整机报告有该行，两个 NVIDIA 硬件各自的 GetReport 里没有） | `lhm-report.txt` 的 `Intel GPU Detection` 段 | 记录即可；已写进 `B580-CHECKLIST.md §3.1` |
| D | **基线偶发测试失败**：首轮 `LiteMonitor.Tests` 全量出现 1 例失败（未能捕获用例名），重跑 1777/0 全绿。本包零产品/测试改动 ⇒ 判定为基线既有的 flaky | §3.3 | 协调者 / 后续包留意（可考虑重跑确认） |
| E | `GetGuiResources` **失败时返回 0**（不是 -1），无法只用返回值区分"0 个对象"与"调用失败" | MSDN 行为；工具已改用 `GetLastWin32Error` 判定 | 记录即可（产品代码目前不用 GDI 计数） |
| F | fork 的 `WebServerPassword` 是**内存态、永不落盘**（`src/Core/Settings.cs:199` 注释 + `WebPasswordJsonConverter`）⇒ 磁盘 `settings.json` 本就不含明文口令 | 源码 | 记录即可（工具的脱敏仍覆盖日志/事件里的其它凭据形态） |

### 7.3 遗留说明

- **工具用法已写入 `tools/forensics/README.md`**（精确命令行 + 参数表 + 时间线格式定义），
  其它包随时可补工具/改用法。
- `tools/forensics/README.md §6` 的"干跑记录"表已同步本报告 §3.2 的实测结果。
