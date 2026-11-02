# B580 / Intel 核显检查清单（上游 issue #592 取证清单）

> **本清单只收集事实，不下结论。**
> 「#592 是否成立」「GCL 是否初始化成功」「是不是应用的映射问题」——由拿到数据后的人判读。
>
> 关联：#592（Intel B580 使用率不显示）、#450、#596、#569、#603、#521、#434（同族 RC2）。

---

## 0. 前置条件

| 项 | 要求 | 怎么确认 |
|---|---|---|
| 机器 | Intel Arc B580（或 B570）**真机**；核显机可覆盖同族 RC2 | — |
| 系统 | Windows 11 | `winver` |
| 驱动 | Intel 显卡驱动（Arc 系列），记录**完整版本号** | 设备管理器 → 显示适配器 → 属性 → 详细信息 |
| 权限 | **管理员** PowerShell（LHM 打开硬件树需要） | `whoami /groups \| findstr S-1-16-12288` |
| PawnIO | 已安装（LiteMonitor++ 启动时会自检） | 应用能正常读到硬件即可 |
| 工具 | 本目录的 `lhm-intel-report.ps1` | 见下面步骤 2 |

> 本仓库的开发机是 **AMD Ryzen 7 5800X + 双 NVIDIA**，**没有 Intel 显卡** ⇒
> 本清单的**实机部分无法在开发机执行**，属于「待实机」项。

---

## 1. 记录驱动与机型（先做，最便宜）

在目标机执行，粘贴结果：

```powershell
Get-CimInstance Win32_VideoController |
    Select-Object Name, DriverVersion, DriverDate, AdapterCompatibility |
    Format-List
```

再记录 CPU 与整机：

```powershell
Get-CimInstance Win32_Processor | Select-Object Name
Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model
```

**填写表**

| 项 | 值 |
|---|---|
| 整机型号 | |
| CPU | |
| 显卡型号 | |
| **显卡驱动版本**（必填，#592 的关键上下文） | |
| 驱动安装日期 | |
| 系统版本 / build | |
| 是否混合显卡（核显 + 独显） | |

---

## 2. 导出 LHM 报告（核心动作）

```powershell
# 以管理员身份运行
pwsh tools/forensics/lhm-intel-report.ps1 -Verbose
```

产出（默认在 `docs\reviews\evidence\forensics\lhm\<时间戳>\`）：

| 文件 | 内容 |
|---|---|
| `lhm-report.txt` | 人读的完整报告：**每个显卡的 `GetReport()` 原文** + 分组报告 + 全部传感器 |
| `lhm-report.json` | 同上的结构化版本 |
| `sensors.csv` | 全部传感器的 硬件 / 类型 / 名称 / 值 / 文本值 |
| `manifest.json` | 机器事实（OS / GPU / 驱动 / 权限） |

> 该探针只引用仓库已有的 `LibreHardwareMonitorLib 0.9.6`，**不新增依赖**；
> 离线机器请设置 `NUGET_PACKAGES` / `DOTNET_CLI_HOME` 指向已缓存的包目录。

---

## 3. 判读要点（**如何读**，不是结论）

### 3.1 GCL 初始化

**★ 关键：这一行在 `lhm-report.txt` 的「整机报告（IComputer.GetReport）」段里，
不在单个显卡的 `GetReport()` 段里。** （已在本机 AMD + 双 NVIDIA 机器上实测：
整机报告的 `Intel GPU Detection` 段里确实有一行 `Intel GCL Initialized: False`，
而两个 NVIDIA 显卡各自的 `GetReport()` 原文里**没有** GCL 字样。）

在 `lhm-report.txt` 里搜 `Intel GCL Initialized`（或直接看 `Intel GPU Detection` 段）：

- `Intel GCL Initialized: True`  ⇒ **把这一行原文抄下来**；
- `Intel GCL Initialized: False` ⇒ **把这一行原文抄下来**；
- 整个报告里**没有**这一行 ⇒ 记为「本 LHM 版本未输出该字段」，
  **不要据此判定"未初始化"**。

> ⚠ LHM 0.9.6 的 `Computer` **没有公开 `Groups` 属性** ⇒ 分组级 `GetReport()` 取不到；
> 探针因此改用**整机报告**（含全部分组的传感器树）作为等价替代。
> 报告里同时给出 `computerGclMentions`（整机报告中的 GCL 行）与每个显卡的 `gclMentions`。
> **判据请以「整机报告」那一段为准。**

### 3.2 ★ 区分「键不存在」与「键存在但值恒 null」

这是 #592 的核心判据（**RC2**）：

| 现象 | 在 `sensors.csv` / `lhm-report.txt` 里的表现 | 含义（**待人工确认**） |
|---|---|---|
| 传感器**根本不在**列表里 | 找不到 `GPU Core` 之类的名字 | 键建不出来 |
| 传感器**在**，但 `value` 列为空 | `sensors.csv` 里该行 value 为空 | **键可建但值恒 null** ← #592 描述的现象 |
| 传感器在且有值 | value 有数字 | 取数正常 |

**填写表**

| 传感器名 | 是否出现在列表 | value 值 | 备注 |
|---|---|---|---|
| `GPU Core` | | | Intel Arc Load 传感器的 LHM 名称 |
| `GPU Package` | | | |
| `GPU Hot Spot` | | | |
| `D3D 3D` | | | WDDM 通用负载通道 |
| （其它 Arc 相关） | | | |

### 3.3 应用侧对照

在同一台机器、同一时刻记录：

1. LiteMonitor++ 面板上的 GPU 占用率数值（截图）。
2. 任务管理器 → 性能 → GPU → 3D / 复制 / 计算引擎 的占用（截图）。
3. 两者是否一致？**不一致时把两个数都记下来**（不要求解释）。

---

## 4. 混合显卡 / 多卡补充

若机器是「核显 + 独显」：

| 项 | 值 |
|---|---|
| 核显型号 + 驱动版本 | |
| 独显型号 + 驱动版本 | |
| LiteMonitor++ 的「首选显卡」当前值 | |
| LiteMonitor++ 的「监控显卡数量」当前值 | |
| 首选核显时面板占用率 | |
| 首选独显时面板占用率 | |

> 依据：`src/System/HardwareServices/SensorMap.cs:126-146` 决定被监控的显卡集合；
> `src/Core/Settings.cs:291` 的 `MonitoredGpuCount` 是该集合的大小（被夹在 1..实际卡数）。
> **切换首选卡后需等一次真实采集**（生产侧有采集门限），
> 否则会看到"切换后数值没变"的假象。

---

## 5. 收尾：把证据打包给分析者

```powershell
# 崩溃/异常侧一起收（#582/#574 的收集器，与本清单无关但可同批执行）
pwsh tools/forensics/collect-crash-evidence.ps1 -DryRun
```

把下列目录压缩后回传：

- `docs\reviews\evidence\forensics\lhm\<时间戳>\`
- `docs\reviews\evidence\forensics\crash\<时间戳>\`（若同批执行）

---

## 6. 未覆盖 / 仍需人工判断的部分

| 项 | 为什么本清单覆盖不了 |
|---|---|
| 本机实测 | 开发机是 AMD + 双 NVIDIA，**无 Intel 显卡** ⇒ 步骤 2 之后的全部判读只能在 B580 真机完成 |
| GCL 未初始化时的**产品侧兜底** | 属于修复范畴，不在本工具包职责内 |
| 「应用是否应该把无值显示为 `--` 而不是 `0`」 | 属显示口径决策 |
| 驱动版本与 GCL 的因果关系 | 需要驱动版本对照实验（换驱动版本重测），单次观察不足以下结论 |
