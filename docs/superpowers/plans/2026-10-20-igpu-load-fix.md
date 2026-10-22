# Intel 核显占用率恒为 0 修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 **Intel 核显**（`IntelIntegratedGpu`）机器上 GPU 占用率永远为 0 / 空白、且**无法自愈**的问题，让 `GPU.Load` / `GPU.0.Load` 键在启动后即可正常出现并取到值。

> ⚠️ **范围收窄说明（复审修正）**：本计划最初把 **AMD APU 一并纳入**，属于**事实错误**。经 IL 字节流实扫确认：`AmdGpu` 的构造函数末尾**同样自调 `Update()`**（与 NVIDIA / `IntelDiscreteGpu` 同构），**AMD APU 不是懒激活**。AMD 机器上占用率为 0 是**另一种失效模式**（键存在但值为 null，源于 ADL 的 PMLog/OD5 通路未提供 GFX activity 数据），**本计划的预热对它无效**。详见「AMD APU：另一种失效模式」。

> ℹ️ **本计划中"上游信息"的引用强度说明**：本计划的**根因判定与修复方案不依赖上游结论** —— 关键事实（`IntelIntegratedGpu` 构造函数不调 `Update()`、`IHardware.Sensors` 是 `ISensor[]`、`HardwareType` 无泛型 `Gpu` 成员等）均由**本机对 LibreHardwareMonitorLib 0.9.6 二进制的反射 + IL 字节流实扫**得出（证据见「API 事实核对」一节）。但文中引用**上游 PR / issue 编号与措辞**（PR #586、issue #86 / #185 / #434 / #450 / #463 / #521 / #569 等）**来自 `docs/UPSTREAM-FEEDBACK-2026-10-20.md` 的调研摘要，未逐条联网核对原文**。这些引用仅用于交代背景与对比，不构成方案的正确性前提。

**Architecture:** 两处独立缺陷叠加，需同时修。
**缺陷 A（数据层，仅 Intel 核显）**：LibreHardwareMonitor 对 **Intel 核显**的负载传感器是**懒激活**的 —— `IntelIntegratedGpu` 构造函数创建了 `_nodeUsage[]`（`SensorType.Load`，名称形如 `D3D 3D`）等传感器实例，但**只在 `Update()` 里调用 `ActivateSensor` 才把它们注册进 `Sensors` 集合**；构造完成后 `Sensors` 里只有 Temperature / Clock / Voltage / Power 四个，**没有 Load**。而 NVIDIA / AMD / Intel 独显（`IntelDiscreteGpu`）的构造函数末尾自调 `Update()`，传感器开箱即就绪。`SensorMap.Rebuild()` 建表时不触发任何 `Update()` → 建表瞬间核显没有 `GPU.Load` 传感器 → **键根本不存在**。
**缺陷 B（缓存层）**：`HardwareValueProvider.GetValue` 的通用兜底**只查静态快照** `_manualSensorCache`（`HardwareValueProvider.cs:503` 注释明写"移除锁和 _sensorMap 查找"），而该快照仅由 `PreCacheAllSensors()` 刷新。`SensorMap.EnsureFresh()` 返回 `void`、10 分钟兜底重建后**不刷新快照** → 即使键后来补进了 `_map`，取值层永远看不见。

**Tech Stack:** C# / .NET 8 (`net8.0-windows`) / WinForms / LibreHardwareMonitorLib 0.9.6 / xUnit（测试工程已存在）

---

## Global Constraints

- 目标框架固定 `net8.0-windows`；使用本地 SDK `G:\TOOL\dotnet-sdk\dotnet.exe`（8.0.426），**不得**安装到系统或修改系统 PATH。
- **不得新增任何 NuGet 依赖**；主工程依赖保持 `LibreHardwareMonitorLib 0.9.6` + `System.Text.Json 9.0.10` 不变。
- **不得引入任何 Mock 框架**。测试工程只有 xUnit，全部用例只测纯函数，不构造 `IHardware` / `ISensor` 桩（沿用 `tests/LiteMonitor.Tests/GpuVramTests.cs:9-11` 已声明的约定）。
- **不得改变独显机器的启动性能**。预热必须**条件触发**：已存在 Load 传感器的显卡一律跳过 `Update()`。
- **不得改变多显卡改造的任何语义**：`GpuKey` 命名规则、`MonitoredGpuCount`、`GpuRegistry` 全部保持原样。
- 提交粒度：**每个 Task 结束提交一次**，提交信息用中文正文 + 英文 type 前缀（如 `fix(hardware): ...`）。
- **`git add` 必须写显式文件路径**，禁止 `git add -A` / `git add .`（工作区有**其它计划线**的未跟踪文件，例如 `docs/superpowers/plans/2026-10-20-plugin-*.md`、`docs/UPSTREAM-FEEDBACK-2026-10-20.md`；`bin/`、`obj/` 也已被 `.gitignore` 覆盖）。
- 注释语言：`src/System/**` 用中文；新增文件统一中文注释。
- **不修改** `src/Plugins/**` 下任何文件（插件系统另有计划线）。
- 本计划与 `docs/superpowers/plans/2026-10-20-gpu-multi-card.md`（多显卡改造）**在 `SensorMap.cs` / `HardwareMonitor.cs` 上有重叠**，执行顺序见下方「与多显卡计划的关系」。

---

## 缺陷分析（已逐行核实，证据附后）

### 缺陷 A：建表时不预热 → Intel 核显的键根本不存在

| 证据 | 位置 |
|---|---|
| 建表入口从不触发 `Update()` | `src/System/HardwareServices/SensorMap.cs:171` — `//hw.Update();`（历史遗留的预热调用，被注释） |
| 全仓无任何"建表前给显卡预热"的调用 | `hw.Update()` 直调点全集：`HardwareMonitor.cs:197`（CPU）/`:204`（GPU）/`:213`（内存）/`:214`（电池）/`:388`（电池预热）/`:548`（`UpdateWithSubHardware` 递归体）；另 `:236` 与 `:374` 是 `UpdateWithSubHardware(hw)` 的间接调用。**其中 `:374`（`WarmUpMotherboardSensors`）与 `:388`（`WarmUpBatterySensors`）确实在建表 `:321` 之前执行，但只针对主板/电池/冷却器，不含任何 GPU 更新** → 建表前**没有** GPU 的 `Update()` |
| 匹配层**已**支持核显 | `src/System/HardwareServices/SensorMatcher.cs:139` — `if (sensorType == SensorType.Load && (Has(name, "core") \|\| Has(name, "d3d 3d"))) return "GPU.Load";`；`:167` 的注释明写 *"核显的 D3D Shared Memory Used 就是从这里进 GPU.VRAM.Used 的"*（`:168-169` 为对应代码） |

**关键推论**：匹配逻辑为核显写好了，**只是数据层永远不把传感器送上来**。这是"半成品"状态 —— 规则在，料没来。

> **核显负载传感器名的来源**（便于判断规则失效风险）：`IntelIntegratedGpu` 的节点用量名来自 `D3DDisplayDevice.GetNodeEngineTypeString`，其中 `DXGK_ENGINE_TYPE_3D → "D3D 3D"` —— 所以 `Has(name, "d3d 3d")` 确实能命中。

### 缺陷 B：取值层只看静态快照 → 键补上了也取不到

| 证据 | 位置 |
|---|---|
| 通用兜底显式移除了 `_sensorMap` 查找 | `src/System/HardwareServices/HardwareValueProvider.cs:502-504` — `// ★★★ [优化] 移除锁和 _sensorMap 查找，直接查静态缓存 ★★★` |
| 静态快照只在 2 处刷新 | `PreCacheAllSensors()` 调用点：`HardwareMonitor.cs:324`（初始化）、`:499`（重载）；另有 `HardwareValueProvider.cs:221`（配置变更时） |
| 10 分钟兜底重建不刷新快照 | `src/System/HardwareServices/SensorMap.cs:58` — `public void EnsureFresh(...)`，**返回 `void`**，内部只调 `Rebuild`（只更新 `_map`） |
| 注释里的假设已失效 | `src/System/HardwareMonitor.cs:186-188` — *"即使重建了，ValueProvider 会在下一次取值时懒加载或直接从 Map 取，也不需要手动 PreCache"* —— 与上一条矛盾 |

### 现象自洽性验证

- **Intel 核显机器**：启动建表时核显 `Sensors` 里没有 Load 传感器 → 无 `GPU.Load` 键 → 快照无此键 → 占用率空白/0，**且 10 分钟兜底也救不回来**（键进了 `_map`，取值层看不见）。
- **手动切换首选显卡后恢复**：`HardwareValueProvider.cs:198-222` 的 `gpuChanged || gpuCountChanged` → `Rebuild` + `PreCacheAllSensors`。此时硬件已被 `UpdateAll` 更新过，传感器已注册 → 键出现。
- **独显正常**：`NvidiaGpu` / `AmdGpu` / `IntelDiscreteGpu` 独显的构造函数末尾自调 `Update()`，传感器开箱即就绪。✓ 解释"为什么只有部分机器中招"

> ⚠️ **一个本计划覆盖不到的场景**：`IntelGpuGroup` 只在 `GetDeviceInfoByIdentifier` 成功**且** `deviceInfo.Integrated` 为真时才 `new IntelIntegratedGpu(...)`。若 `Computer.Open()` 时 D3D 枚举失败，**压根不存在核显硬件对象**，一次 `Update()` 补不回来 —— 只有 `ReloadComputerSafe` 重开 `Computer` 才行，而 **10 分钟兜底不会重开**。这种情形只能靠重载自愈，属于本计划的已知边界。

### AMD APU：另一种失效模式（本计划不覆盖）

AMD APU / 3A 平台同样有"GPU 占用率 0"的报告（据 `docs/UPSTREAM-FEEDBACK-2026-10-20.md` 的调研摘要，上游 issue #86 / #185 / #434 等提到过类似症状；**这些 issue 编号与具体内容未逐条联网核对**），但**根因不同**：

- `AmdGpu` 构造函数**已经**自调 `Update()` → 传感器**已注册** → `NeedsWarmUp` 返回 false → **本计划的预热对它是空操作**。
- AMD 侧占用率能否取到，取决于 ADL（AMD Display Library）是否提供 GFX activity 数据（`ADL_PMLOG_INFO_ACTIVITY_GFX` / OD5 通路）。拿不到时表现为**键存在但值为 null**（`GetValue` 返回 null → 显示空白/0），这是**数据通路问题**，不是"键不存在"问题。

**结论**：AMD APU 需要**另行诊断**（区分"键缺失"与"值为 null"），不在本计划范围内。若后续要处理，正确方向是查 `AmdGpu` 的 Load 传感器是否有值、以及 `SensorMatcher` 对其名称的匹配情况，而不是加预热。

---

## 与多显卡计划的关系

多显卡计划 `docs/superpowers/plans/2026-10-20-gpu-multi-card.md` 已落地部分（`GpuKey` / `GpuRegistry` / `MonitoredGpuCount` / `SelectMonitoredGpus` / `FindGpuIndex` 均已在代码中）。

| 文件 | 多显卡计划 | 本计划 | 说明 |
|---|---|---|---|
| `src/System/HardwareServices/SensorMap.cs` | Task 6 多卡映射核心 | **Task 1 只加"预热"，不改映射逻辑** | 本计划不碰 `SelectMonitoredGpus` / `RegisterTo` / 原子交换段（`:376-392`） |
| `src/System/HardwareMonitor.cs` | Task 12 硬件刷新层 | **Task 1 加 `WarmUpGpuSensors`，Task 2 改 `EnsureFresh` 调用** | 两处修改点互不重叠 |
| `src/System/HardwareServices/HardwareValueProvider.cs` | — | **不改**（见 Task 3 的取舍说明） | — |
| `src/Core/GpuKey.cs` | 已落地 | **不改** | — |

**执行顺序**：本计划可与多显卡计划并行，但两者都改 `HardwareMonitor.cs` / `SensorMap.cs` → **建议在多显卡计划合并后执行**，或至少串行执行、避免同文件并行编辑。

### 与 `2026-10-20-gpu-vram-d3d-fix.md`（显存 D3D 修复）的关系

该计划**亦已落地**（实测：`SensorMatcher.cs:149-151` 已存在 `GPU.VRAM.D3D.Used` / `.Total` / `.Free` 三个 Key；`MatchGpu` 已抽成独立方法）。核对该计划给出的 `MatchGpu` 签名与规则：

| | 仓库现状 | 显存 D3D 计划的目标 | 是否一致 |
|---|---|---|---|
| 签名 | `public static string? MatchGpu(string name, SensorType sensorType, bool preferShared)`（`SensorMatcher.cs:137`） | 同 | ✅ 完全一致 |
| 负载规则 | `sensorType == Load && (Has(name,"core") \|\| Has(name,"d3d 3d"))` → `"GPU.Load"`（`:139`） | 同（`:682`） | ✅ 完全一致 |
| 核显共享显存 | `preferShared` 且 `isShared && Has(name,"used")` → `"GPU.VRAM.Used"`（`:168`） | 同（`:711`） | ✅ 完全一致 |

→ **本计划 Task 4.1 的测试用例对显存 D3D 计划是前向兼容的**，不需要因为那条线而改写。

### 跨计划冲突核对结论（2026-10-20 实测）

| 计划 | 落地状态 | 与本计划的文件交集 | 结论 |
|---|---|---|---|
| `2026-10-20-gpu-multi-card.md` | **已落地**（`GpuKey` / `GpuRegistry` / `MonitoredGpuCount` / `SelectMonitoredGpus` 均在代码中） | `SensorMap.cs` / `HardwareMonitor.cs` | 无冲突 —— 它的改动已在基线里，本计划是在其之上叠加 |
| `2026-10-20-gpu-vram-d3d-fix.md` | **已落地**（`GPU.VRAM.D3D.*` 已在 `SensorMatcher.cs:149-151`） | 仅 `SensorMatcher.cs`（**本计划不改**） | 无冲突 |
| `2026-10-20-rebrand-compliance.md` | 计划阶段 | 无硬件层文件 | 无冲突（重品牌化走占位符） |
| `2026-10-20-gpu-per-card-group-titles.md` | — | 无 | 无冲突 |

> **本计划在当前基线上可直接执行**，无需等待其他计划。但**行号会随其他计划线的改动漂移** → 执行时以 `Step 2.3` 的 `Select-String` 校验命令为准，**不要盲信文档里的行号**。

---

## File Structure

### 新建文件

| 文件 | 职责 |
|---|---|
| `src/System/HardwareServices/GpuWarmup.cs` | 纯函数 `NeedsWarmUp`（判定某显卡是否需要补一次 `Update()`）+ 预热执行器。**无状态、可单测**。 |
| `tests/LiteMonitor.Tests/GpuWarmupTests.cs` | `GpuWarmup.NeedsWarmUp` 与 `SensorMatcher.MatchGpu` 的核显/独显回归测试。 |

### 修改文件

| 文件 | 改动 |
|---|---|
| `src/System/HardwareMonitor.cs` | 新增 `WarmUpGpuSensors()`；在 `:317-318` 与 `:492-493` 处调用；`EnsureFresh` 调用点改为按返回值刷新缓存 |
| `src/System/HardwareServices/SensorMap.cs` | `EnsureFresh` 返回值 `void` → `bool` |
| `src/System/HardwareServices/HardwareValueProvider.cs` | **不改**（Task 3 论证后否决） |

---

## 计划自验结果（2026-10-20 修订后）

本计划的**代码可编译性与测试用例正确性已在仓库外的副本上实测通过**，不是纸面推演：

| # | 验证动作 | 结果 |
|---|---|---|
| 1 | 把 `Task 1.1` 的 `GpuWarmup.cs` 代码块**原样**取出，编译进一个只引用 `LibreHardwareMonitorLib 0.9.6` 的临时 xUnit 工程 | ✅ **`GpuWarmup.cs` 本身 0 错误 0 警告** —— 证明 `.Length`（而非 `.Count`）、`SensorType.Load`、`Computer.Hardware` 遍历、`Action<IHardware>` 回调等 API 用法全部正确 |
| 2 | 把 `Task 4.1` 的 `GpuWarmupTests.cs` 代码块**原样**取出，与真实的 `SensorMatcher.cs` / `HardwareRules.cs` 一起编译运行 | ✅ **7/7 通过**（3 个 `Fact` + 1 个 `Theory`×3 + 1 个 `Fact`）。临时工程默认 `<Nullable>enable</Nullable>`，测试文件会报 2 条 CS8600；**仓库真实测试工程是 `<Nullable>disable</Nullable>`，不会有该警告**（计划 Step 4.1 的注释已说明） |
| 3 | 把仓库整体复制到仓库外的临时目录，按本计划 **Task 1 + Task 2 + Task 4** 完整改一遍（**7 处改动**：新文件 ×2、`SensorMap.EnsureFresh` 改签名 ×1、`HardwareMonitor` ×4〔新增 `WarmUpGpuSensors` 方法 + 2 处调用点 + `EnsureFresh` 调用点改写〕） | ✅ 主工程 `dotnet build -c Release` **0 错误**；`dotnet test` **146/146 通过**（基线 139 + 本计划新增 7） |
| 4 | IL 字节流复扫 LHM 0.9.6 的 `.ctor` | ✅ 确认 `NvidiaGpu` / `AmdGpu` / `IntelDiscreteGpu` 的构造函数**调用** `Update()`，`IntelIntegratedGpu` / `GenericGpu` **不调用** |

> 验证 1~3 全部在**仓库外**的临时目录进行，**未改动本工作副本的任何源文件**；临时目录因运行时安全策略限制无法由本会话删除，留待系统清理。
>
> **仍然无法在开发机验证的部分**：核显机器上"修复后占用率随负载变化"这一**行为**结论 —— 本机是 `AMD Ryzen 7 5800X`（**无核显**），只能验独显零回归与缓存刷新路径。行为验证见 `Task 5.2`（需另一台有 Intel 核显的机器）。

---

## 执行前预检（Step 0）：重核全部行号

> ⚠️ **必做**。本计划里所有 `file:line` 引用在撰写时逐条核对过（2026-10-20 实测 24 处全部命中），但**仓库有并发的计划线在持续提交**（复审期间就新增了 `GpuGroupTests.cs`、`GpuGroup` 相关实现，HEAD 从 `81bd139` 前进到 `799d139`）→ **行号会漂移**。
> 执行前请跑一遍下面的校验，**若发现与本计划描述不符，以源码实际内容为准，并顺手修正本计划的行号**。

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
function Show([string]$rel, [int[]]$nums) {
  $l = Get-Content $rel -Encoding UTF8
  "===== $rel ====="
  foreach ($n in $nums) { "  {0,4}: {1}" -f $n, $l[$n-1] }
}
Show 'src\System\HardwareServices\SensorMap.cs'      @(50,57,58,171,228,236,376,377,392)
Show 'src\System\HardwareMonitor.cs'                   @(107,181,183,185,188,316,317,318,319,320,321,380,392,394,466,491,492,493,496,498,499,546,552)
Show 'src\System\HardwareServices\HardwareValueProvider.cs' @(54,132,143,198,199,200,203,221,503,504)
Show 'src\System\HardwareServices\SensorMatcher.cs'    @(74,137,139,167,168,169)
```

Expected（按本计划撰写时的基线）：

| 位置 | 应看到 |
|---|---|
| `SensorMap.cs:58` | `public void EnsureFresh(Computer computer, Settings cfg)` ← **Task 2.1 要改成 `bool`** |
| `SensorMap.cs:171` | `//hw.Update();` ← **缺陷 A 的现场**（被注释掉的预热） |
| `SensorMap.cs:376-377` | `// 2. 原子交换数据 (加锁)` / `lock (_lock)` |
| `HardwareMonitor.cs:107` | `_lock,`（传给 `HardwareValueProvider` 的第 8 个参数） |
| `HardwareMonitor.cs:188` | `_sensorMap.EnsureFresh(_computer, _cfg);` ← **Task 2.2 要改** |
| `HardwareMonitor.cs:316-321` | `_computer.Open(); WarmUpMotherboardSensors(); WarmUpBatterySensors();` … `_sensorMap.Rebuild(...)` ← **Task 1.3 要插入预热** |
| `HardwareMonitor.cs:380` / `:394` | `private void WarmUpBatterySensors()` / `private double UpdateTiming()` ← **Task 1.2 插入位置** |
| `HardwareMonitor.cs:466` / `:496` / `:498` | `lock (_lock)` / `}` / `_sensorMap.Rebuild(_computer, _cfg);` ← **`:498` 在锁外**，Task 1.2 的锁约束依据 |
| `HardwareMonitor.cs:491-493` | `_computer.Open(); WarmUpMotherboardSensors(); WarmUpBatterySensors();` ← **Task 1.4 要插入预热** |
| `HardwareValueProvider.cs:54` | `_lock = syncLock;` ← 锁同一对象的证据 |
| `HardwareValueProvider.cs:203` / `:221` | `_sensorMap.Rebuild(_computer, _cfg);` / `PreCacheAllSensors(_sensorMap);` |
| `HardwareValueProvider.cs:503-504` | `// ★★★ [优化] 移除锁和 _sensorMap 查找…` / `if (!skipGenericFallback && result == null && _manualSensorCache.TryGetValue(key, out var sGen))` |
| `SensorMatcher.cs:137` / `:139` | `public static string? MatchGpu(string name, SensorType sensorType, bool preferShared)` / `if (sensorType == SensorType.Load && (Has(name, "core") \|\| Has(name, "d3d 3d"))) return "GPU.Load";` |

> 若 `SensorMatcher.MatchGpu` 的签名变了（`2026-10-20-gpu-vram-d3d-fix.md` 那条线会动这个文件），**Task 4.1 的测试调用需按新签名校正** —— 目前核对下来两条线是兼容的（签名与 `GPU.Load` / `GPU.VRAM.Used` 规则完全一致）。

---

## Task 1: 显卡传感器预热（修复缺陷 A）

**Files:**
- Create: `src/System/HardwareServices/GpuWarmup.cs`
- Modify: `src/System/HardwareMonitor.cs`（新增方法 + 2 处调用）

**Interfaces:**
- Consumes: `LibreHardwareMonitor.Hardware.IHardware` / `ISensor` / `SensorType`
- Produces: `GpuWarmup.NeedsWarmUp(IEnumerable<SensorType>)` 纯函数；`HardwareMonitor.WarmUpGpuSensors()` 预热执行器

- [ ] **Step 1.1: 新建 `src/System/HardwareServices/GpuWarmup.cs`**

```csharp
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 显卡传感器预热辅助。
    ///
    /// 背景: LibreHardwareMonitor 对 Intel 核显的负载传感器是懒激活的。
    ///   IntelIntegratedGpu 的构造函数创建了 _nodeUsage[] (SensorType.Load,
    ///   名称形如 "D3D 3D") 与 _sharedMemoryUsage 等传感器实例, 但只在 Update() 里
    ///   调用 ActivateSensor 才把它们注册进 Sensors 集合 —— 构造完成后 Sensors 里
    ///   只有 Temperature / Clock / Voltage / Power, 没有 Load。
    ///   而 NvidiaGpu / AmdGpu / IntelDiscreteGpu 独显的构造函数末尾会自调 Update(),
    ///   传感器开箱即就绪, 不受影响。
    ///
    /// 因此若在 SensorMap 建表之前不补一次 Update(), 核显的负载传感器
    /// 根本不会出现在 Sensors 里, 导致 "GPU.Load" / "GPU.0.Load" 键缺失,
    /// 表现为占用率恒为 0 或空白。
    ///
    /// 注意: AMD APU 不属于此列 —— AmdGpu 构造函数同样自调 Update(), 预热对它无效。
    /// </summary>
    public static class GpuWarmup
    {
        /// <summary>
        /// 该显卡是否需要补一次 Update()。
        ///
        /// 判定依据: 硬件是否已暴露任意 Load 类型传感器。
        ///   - 独显 (NVIDIA / AMD / Intel 独显): 构造函数已自调 Update(), 有 Load 传感器 -> false (不重复 Update)
        ///   - Intel 核显: 构造后有 Temp/Clock/Voltage/Power, 但**没有 Load** -> true (需要预热)
        ///
        /// 说明: 即使某块卡预热后依然没有 Load 传感器 (硬件本身不提供, 如虚拟显示适配器),
        /// 最多多付一次 Update(), 发生在启动/重载这类低频时机, 可接受。
        /// </summary>
        public static bool NeedsWarmUp(IEnumerable<SensorType> sensorTypes)
        {
            foreach (var t in sensorTypes)
            {
                if (t == SensorType.Load) return false;
            }
            return true;
        }

        /// <summary>
        /// 对所有"传感器尚未上线"的显卡补一次 Update()。
        /// 纯 GPU 操作, 不触碰主板/电池/磁盘, 避免恢复启动时的全硬件 Update 卡顿。
        /// </summary>
        public static void WarmUpIfNeeded(Computer computer, System.Action<IHardware> updater)
        {
            if (computer == null || updater == null) return;
            foreach (var hw in computer.Hardware)
            {
                if (hw == null) continue;
                if (!HardwareScanner.IsGpuHardware(hw)) continue;

                if (!NeedsWarmUp(GetSensorTypes(hw))) continue;

                System.Diagnostics.Debug.WriteLine(
                    $"[GpuWarmup] 显卡传感器未上线, 补一次 Update: {hw.Name} ({hw.HardwareType})");
                try { updater(hw); }
                catch { /* 预热失败不能阻断启动; 后续 UpdateAll 仍会再试 */ }
            }
        }

        /// <summary>
        /// 取出该硬件的传感器类型序列。
        /// 注意: LibreHardwareMonitor 的 IHardware.Sensors 实际类型是 ISensor[] (数组),
        ///      因此用 .Length 而不是 .Count。
        /// </summary>
        private static IEnumerable<SensorType> GetSensorTypes(IHardware hw)
        {
            var sensors = hw.Sensors;
            if (sensors == null) return new List<SensorType>();
            var list = new List<SensorType>(sensors.Length);
            for (int i = 0; i < sensors.Length; i++)
                list.Add(sensors[i].SensorType);
            return list;
        }
    }
}
```

> **为什么用 `Action<IHardware>` 回调而不是直接调 `hw.Update()`**：`HardwareMonitor` 里已有 `UpdateWithSubHardware(IHardware)`（`HardwareMonitor.cs:546`）会递归更新子硬件；把具体更新动作作为回调注入，`GpuWarmup` 保持**无状态纯逻辑**，可被单测直接覆盖，也避免与 `HardwareMonitor` 产生循环依赖。
>
> **API 事实核对（已用反射验证 LibreHardwareMonitorLib 0.9.6）**：
> - `Computer.Hardware` → `IList<IHardware>` —— `foreach` 可用
> - `IHardware.Sensors` → **`ISensor[]`（数组）** —— 必须用 `.Length`，**用 `.Count` 编译不过**
> - `Hardware.Update()` → `void Update()` —— 无参
> - `SensorType` 枚举成员确认存在：`Load / Temperature / Fan / Power / SmallData / Throughput / Data / Control / Level / Clock / Voltage / Current / Energy`
> - `HardwareType` 枚举**只有** `Motherboard / SuperIO / Cpu / Memory / GpuNvidia / GpuAmd / GpuIntel / Storage / Network / Cooler / EmbeddedController / Psu / Battery / PowerMonitor` —— **没有泛型 `Gpu` 成员**
>   → 这一点很重要：`HardwareScanner.IsGpuHardware()` 只比对 `GpuNvidia / GpuAmd / GpuIntel` 三者，**因为枚举里不存在第四种 GPU 类型，所以该判定是完备的**，不会漏掉任何 GPU（含核显与虚拟显示适配器）。
>   → 核显 `IntelIntegratedGpu` 必然被归为 `HardwareType.GpuIntel`。
>
> **LHM 二进制里的字符串表同时证实了根因分析**。⚠️ **两类字符串位于不同的堆，必须分开扫**（初版把两者混在一起，复审时已更正）：
>
> | 类别 | 扫描方式 | 命中 |
> |---|---|---|
> | **传感器名字面量**（真正的 `string` 常量池，#US 堆，UTF-16） | 按 UTF-16 编码扫 | `D3D 3D` ×1、`D3D Shared Memory` ×3、`D3D Dedicated Memory` ×3、`GPU Core` ×2 |
> | **元数据名**（类型名 / 成员名，#Strings 堆，UTF-8） | 按 UTF-8 扫 | `IntelIntegratedGpu` ×1、`coreLoad` ×1 |
>
> → 关键在于**前一组**：核显的**负载传感器名字面量 `D3D 3D`** 与**显存字面量 `D3D Shared Memory` / `D3D Dedicated Memory`** 确实存在于 LHM 的常量池中，与 `SensorMatcher.cs:139`（`Has(name, "d3d 3d")` → `GPU.Load`）和 `:167-169`（`D3D Shared Memory Used` → `GPU.VRAM.Used`）的规则**一一对应**。
> → 后一组（`IntelIntegratedGpu` / `coreLoad`）是**类型名与成员名**，只说明 LHM 内部确实有"核显 GPU 类"与一个 `coreLoad` 成员，**不构成传感器名的证据**。
>
> **★ 根因的机器级铁证（IL 字节流实扫，非引用上游结论）**：
> 用 `System.Reflection.Metadata` 读 `LibreHardwareMonitorLib 0.9.6` 的元数据，取各显卡类 `.ctor` 的 IL 字节，扫描 `call` / `callvirt` 指令（`0x28` / `0x6F`）的 token 并解析回方法名，结果：
>
> | LHM 内部类 | 构造函数内是否调用 `Update()` | 含义 |
> |---|---|---|
> | `NvidiaGpu` | **是**（1 次） | 独显传感器开箱即就绪 → 本修复对它**零成本** |
> | `AmdGpu` | **是**（1 次） | AMD 独显同样开箱即就绪 |
> | `IntelDiscreteGpu` | **是**（1 次） | Intel 独显（Arc）同样开箱即就绪 |
> | `IntelIntegratedGpu` | **否** | **核显必须手动 `Update()` 才注册传感器** ← 缺陷 A 的直接成因 |
> | `GenericGpu` | 否（构造函数体极简） | 通用 GPU 路径，见下方注意事项 |
>
> （类名说明：LHM 0.9.6 里 Intel 独显的类名是 `IntelDiscreteGpu`，**没有**叫 `IntelArcGpu` 的类；核显是 `IntelIntegratedGpu`，两者同属 `IntelGpuGroup`。）
>
> 复现命令见本节末「如何复现 IL 实扫」。
>
> 这条证据把"核显传感器懒激活"从**上游 PR 的说法**升级为**从依赖二进制直接验证的结论**：修复方案的正确性不再依赖对上游的信任。
>
> **注意事项（虚拟显示适配器）—— 已实测更正，原担心不成立**：
> 初稿曾推测"4 个虚拟显示适配器会被 LHM 归入 GPU 类型、从而每次启动/重载都触发一次 `Update()`"。**2026-10-20 实施时的实测推翻了这一推测**：`Computer.Open()` 后 `Computer.Hardware` 中的 GPU 类硬件**只有 2 个**（两张 NVIDIA 卡），`GameViewer` / `Meta Virtual Monitor` / `MuMu` / `Todesk` 这些虚拟显示适配器**根本没有被 LHM 枚举为 GPU 硬件**，因此 `HardwareScanner.IsGpuHardware()` 也无从命中。
> → **"追加跳过虚拟适配器规则"这一备选项可以直接关闭**，不需要实现。实测证据见 `2026-10-20-igpu-load-fix-verification.md` §2。
> 保留下面这条一般性说明，供将来 LHM 版本变化后参考：若某版本的 LHM 开始把虚拟适配器枚举为 GPU 且它们没有 Load 传感器，才会发生"每次启动多一次 `Update()`"，届时再考虑在 `NeedsWarmUp` 里追加名称过滤。

>
> ⚠️ **注意措辞**：预热**只在启动与重载两个时机**触发（**不进 `SensorMap.Rebuild`**），所以这里说的是"**每次启动 / 重载**"，**不是**"每次 Rebuild"。

> **如何复现 IL 实扫**（无需联网，纯本地）：
> ```powershell
> $dll = "$env:USERPROFILE\.nuget\packages\librehardwaremonitorlib\0.9.6\runtimes\win-x64\lib\net8.0\LibreHardwareMonitorLib.dll"
> $ms  = [System.IO.MemoryStream]::new([System.IO.File]::ReadAllBytes($dll))
> $pe  = [System.Reflection.PortableExecutable.PEReader]::new($ms)
> $md  = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
> foreach ($th in $md.TypeDefinitions) {
>     $td = $md.GetTypeDefinition($th)
>     if ($md.GetString($td.Name) -notmatch 'Gpu$') { continue }
>     foreach ($mh in $td.GetMethods()) {
>         $mdf = $md.GetMethodDefinition($mh)
>         if ($md.GetString($mdf.Name) -ne '.ctor') { continue }
>         $il = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe, $mdf.RelativeVirtualAddress).GetILBytes()
>         for ($i = 0; $i -lt $il.Length - 4; $i++) {
>             if ($il[$i] -ne 0x28 -and $il[$i] -ne 0x6F) { continue }   # call / callvirt
>             $tok = [System.BitConverter]::ToInt32($il, $i + 1)
>             $h   = [System.Reflection.Metadata.Ecma335.MetadataTokens]::EntityHandle($tok)
>             if ($h.Kind -eq 'MethodDefinition' -and $md.GetString($md.GetMethodDefinition($h).Name) -eq 'Update') {
>                 "{0}.ctor -> 调用 Update()" -f $md.GetString($td.Name)
>             }
>         }
>     }
> }
> ```
> 注意两个坑：① 取方法体要用 `[System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody(...)`，**不是** `PEReader.GetMethodBody`；`GetMetadataReader` 同样是 `PEReaderExtensions` 的扩展方法；② 目标方法可能是 `MemberReference`（接口调用），上面脚本只处理了 `MethodDefinition`，结论已足以区分本计划涉及的四个类。
>
> **如何复现字符串表证据**（两类堆分开扫，**注意字节对齐**）：
> ```powershell
> $dll = "$env:USERPROFILE\.nuget\packages\librehardwaremonitorlib\0.9.6\runtimes\win-x64\lib\net8.0\LibreHardwareMonitorLib.dll"
> $b   = [System.IO.File]::ReadAllBytes($dll)
> function Count-Bytes([byte[]]$hay, [byte[]]$pat) {
>     $c = 0
>     for ($off = 0; $off -le 1; $off++) {            # UTF-16 串可能落在奇数偏移
>         for ($i = $off; $i -le $hay.Length - $pat.Length; $i++) {
>             $ok = $true
>             for ($k = 0; $k -lt $pat.Length; $k++) { if ($hay[$i+$k] -ne $pat[$k]) { $ok = $false; break } }
>             if ($ok) { $c++ }
>         }
>     }
>     $c
> }
> $u16 = [System.Text.Encoding]::Unicode
> $u8  = [System.Text.Encoding]::UTF8
> # 字面量池 (#US, UTF-16)
> foreach ($s in @('D3D 3D','D3D Shared Memory','D3D Dedicated Memory','GPU Core')) {
>     "UTF16  {0,-22} x{1}" -f $s, (Count-Bytes $b ($u16.GetBytes($s)))
> }
> # 元数据名 (#Strings, UTF-8)
> foreach ($s in @('IntelIntegratedGpu','coreLoad')) {
>     "UTF8   {0,-22} x{1}" -f $s, (Count-Bytes $b ($u8.GetBytes($s)))
> }
> ```
> **第三个坑**：直接用 `[System.Text.Encoding]::Unicode.GetString($b)` 扫整个 DLL **会漏匹配** —— UTF-16 串可能不是从偶数偏移开始，必须像上面那样**双偏移逐字节比对**。

- [ ] **Step 1.2: 在 `HardwareMonitor` 中新增预热方法**

在 `src/System/HardwareMonitor.cs` 的 `WarmUpBatterySensors()`（`:380-392`）之后、`UpdateTiming()`（`:394`）之前插入：

```csharp
private void WarmUpGpuSensors()
{
    // Intel 核显的负载/显存传感器是懒激活的: 硬件对象构造时只创建传感器实例,
    // 只在 Update() 里才调用 ActivateSensor 注册进 Sensors 集合; 而 NVIDIA / AMD /
    // Intel 独显 (IntelDiscreteGpu) 的构造函数末尾都自调 Update(), 传感器开箱即就绪。
    // 若不补这一次 Update, SensorMap.Rebuild 建表时拿不到核显的负载传感器,
    // "GPU.Load" / "GPU.0.Load" 键直接缺失 -> 占用率恒为 0 且无法自愈。
    //
    // 只对"没有 Load 传感器"的显卡预热 (GpuWarmup.NeedsWarmUp 判定),
    // 独显机器零额外开销, 不会拖慢启动。
    GpuWarmup.WarmUpIfNeeded(_computer, UpdateWithSubHardware);
}
```

> ⚠️ **锁的约束**：本方法**必须在 `HardwareMonitor._lock` 内调用**。
> `SensorMap.Rebuild` 在 `HardwareMonitor.cs:498`（`ReloadComputerSafe` 内）是在锁**外**调用的 —— 若把预热放进 `SensorMap.Rebuild`，就会在锁外触发 `hw.Update()`，可能与 `UpdateAll` 持锁做的 `hw.Update()` 竞争同一硬件对象。
> 因此预热**只放在 `HardwareMonitor` 的两个 warm-up 调用点**（两处都在锁内），**不进 `SensorMap`**。

- [ ] **Step 1.3: 启动流程接入预热**

`src/System/HardwareMonitor.cs:316-321` 现状：

```csharp
_computer.Open();
WarmUpMotherboardSensors();
WarmUpBatterySensors();

// 先建立映射，再由正常刷新循环更新数值，避免启动时预热所有 GPU 拖慢 CPU/MEM 展示。
_sensorMap.Rebuild(_computer, _cfg);
```

改为：

```csharp
_computer.Open();
WarmUpMotherboardSensors();
WarmUpBatterySensors();
WarmUpGpuSensors();          // ★★★ [新增] Intel 核显负载传感器懒激活预热

// 先建立映射，再由正常刷新循环更新数值，避免启动时预热所有 GPU 拖慢 CPU/MEM 展示。
_sensorMap.Rebuild(_computer, _cfg);
```

> ⚠️ **原注释"避免启动时预热所有 GPU 拖慢 CPU/MEM 展示"依然成立** —— 新的预热是**条件触发**的：独显机器上 `NeedsWarmUp` 返回 false，**一次 `Update()` 都不会多调**；只有"传感器未上线"的设备（Intel 核显，以及可能的虚拟显示适配器）才多一次 `Update()`。
>
> **关于耗时**："一次 `Update()` 约 7ms" 这一数字**来自上游 PR #586 作者的描述**，**本仓库未做本地实测**（开发机无核显，无法测量核显路径）。实施后应在真机上用 Stopwatch 复核；即便实际耗时高于 7ms，它也只发生在**启动 / 重载**这类低频时机，且**只在"传感器未上线"的设备上**发生（Intel 核显，以及可能的虚拟显示适配器）。
>
> 上下文：`WarmUpBatterySensors()`（`:382-383`）的注释明确说 *"只预热 Battery，避免恢复启动时全硬件 Update 带来的多显卡/磁盘卡顿"* —— 本预热严格限定在 GPU 且条件触发，不会退化成全硬件 Update。

- [ ] **Step 1.4: 重载流程接入预热**

`src/System/HardwareMonitor.cs:491-493` 现状：

```csharp
_computer.Open();
WarmUpMotherboardSensors();
WarmUpBatterySensors();
```

改为：

```csharp
_computer.Open();
WarmUpMotherboardSensors();
WarmUpBatterySensors();
WarmUpGpuSensors();          // ★★★ [新增] 热插拔显卡后同样需要预热
```

> 这一处在 `lock (_lock)` 内（`ReloadComputerSafe` 的锁块 `:466-496`），符合锁约束。
> 热插拔场景：显卡重新插入后 LHM 重建硬件对象，传感器同样回到"未上线"状态，必须重新预热。

- [ ] **Step 1.5: 编译验证**

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
& $dotnet build 'LiteMonitor.csproj' -c Release
```

Expected: `生成成功`，0 错误。

- [ ] **Step 1.6: 提交**

```powershell
git add src/System/HardwareServices/GpuWarmup.cs src/System/HardwareMonitor.cs
git commit -m "fix(hardware): 补显卡传感器预热, 修复 Intel 核显占用率恒为 0

LHM 对 Intel 核显 (IntelIntegratedGpu) 的负载/显存传感器是懒激活的:
硬件对象构造时只创建传感器实例, 只在 Update() 里才调用 ActivateSensor
注册进 Sensors 集合; NVIDIA / AMD / Intel 独显的构造函数末尾都自调
Update(), 故传感器开箱即就绪。

SensorMap.Rebuild 建表前不触发 Update, 导致核显的 \"GPU.Load\" 键缺失,
占用率恒为 0 且 10 分钟兜底重建也救不回来。

新增 GpuWarmup.NeedsWarmUp 纯函数 + WarmUpIfNeeded 执行器, 在启动与重载
两个 warm-up 调用点(均在 _lock 内)条件触发预热: 已有 Load 传感器的显卡
零额外开销, 核显多一次 Update()。预热开销需在真机上实测 (Task 5.3)。"
```

---

## Task 2: 兜底重建后刷新取值缓存（修复缺陷 B）

**Files:**
- Modify: `src/System/HardwareServices/SensorMap.cs:55-64`
- Modify: `src/System/HardwareMonitor.cs:185-189`

**Interfaces:**
- Consumes: Task 1 的成果
- Produces: `SensorMap.EnsureFresh(...)` 返回 `bool`（是否发生了重建）

- [ ] **Step 2.1: `EnsureFresh` 改为返回是否重建**

`src/System/HardwareServices/SensorMap.cs:55-64` 现状：

```csharp
// [Fix] 回归 v1.2.9 的简单逻辑
// 只有在初始化时才需要 build，之后除非手动触发或 10 分钟兜底，否则不需要任何检测。
// EnsureFresh 的返回值改为 void，因为不需要外部感知是否重建。
public void EnsureFresh(Computer computer, Settings cfg) 
{
    if ((DateTime.Now - _lastMapBuild).TotalMinutes > 10)
    {
        Rebuild(computer, cfg);
    }
}
```

改为：

```csharp
// [Fix] 回归 v1.2.9 的简单逻辑
// 只有在初始化时才需要 build，之后除非手动触发或 10 分钟兜底，否则不需要任何检测。
//
// 返回值语义: true = 本次发生了重建。
// 调用方必须据此刷新 HardwareValueProvider 的静态取值缓存 ——
// GetValue 的通用兜底只查 _manualSensorCache (已移除对 _sensorMap 的直接查找),
// 重建后若不刷新缓存, 映射表里补上的键依然取不到值。
public bool EnsureFresh(Computer computer, Settings cfg) 
{
    if ((DateTime.Now - _lastMapBuild).TotalMinutes > 10)
    {
        Rebuild(computer, cfg);
        return true;
    }
    return false;
}
```

- [ ] **Step 2.2: `UpdateAll` 按返回值刷新缓存**

`src/System/HardwareMonitor.cs:185-189` 现状：

```csharp
// [Optimization] 极简维护
// 仅每10分钟做一次兜底检查 (内部自动 Rebuild)，无需关心返回值。
// 即使重建了，ValueProvider 会在下一次取值时懒加载或直接从 Map 取，也不需要手动 PreCache。
_sensorMap.EnsureFresh(_computer, _cfg);
```

改为：

```csharp
// [Optimization] 极简维护
// 仅每10分钟做一次兜底检查 (内部自动 Rebuild)。
// ★ 重建后必须刷新 ValueProvider 的静态缓存: GetValue 的通用兜底只查
//   _manualSensorCache (已移除对 _sensorMap 的直接查找), 不刷新则
//   "映射表里补上了键" 与 "取值层能读到该键" 是两回事。
if (_sensorMap.EnsureFresh(_computer, _cfg))
{
    _valueProvider.PreCacheAllSensors(_sensorMap);
}
```

> ⚠️ **锁安全性**：`UpdateAll` 在 `:183` 已持有 `lock (_lock)`（`HardwareMonitor._lock`），而 `PreCacheAllSensors` 在 `HardwareValueProvider.cs:132` 会再次 `lock (_lock)`。**C# 的 `lock`（`Monitor`）是同线程可重入的**，且两者是**同一个对象**（`HardwareMonitor.cs:107` 把 `_lock` 传给 `HardwareValueProvider` 构造函数）→ **不会死锁**。
>
> ⚠️ `PreCacheAllSensors` 内部会调 `FindSensorReverse`（`HardwareValueProvider.cs:143`）遍历 `_computer.Hardware`。这发生在硬件更新循环**之前**（`:181` 的 `OnUpdateTickStarted` 与 `:188` 之间），此时传感器对象已被前一次 tick 更新过，读取安全。
>
> ⚠️ **性能**：10 分钟才触发一次，耗时 = 一次建表遍历 + 一次缓存重建，可忽略。

- [ ] **Step 2.3: 检查 `EnsureFresh` 的其它调用点**

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'G:\TOOL\LiteMonitor-fork'
Get-ChildItem -Path 'src' -Recurse -Filter '*.cs' |
  Select-String -Pattern 'EnsureFresh' |
  ForEach-Object { "{0}:{1}: {2}" -f $_.Path.Replace('G:\TOOL\LiteMonitor-fork\',''), $_.LineNumber, $_.Line.Trim() }
```

Expected（**当前基线实测结果**）：

```
src\System\HardwareMonitor.cs:188: _sensorMap.EnsureFresh(_computer, _cfg);
src\System\HardwareServices\SensorMap.cs:57: // EnsureFresh 的返回值改为 void，因为不需要外部感知是否重建。
src\System\HardwareServices\SensorMap.cs:58: public void EnsureFresh(Computer computer, Settings cfg)
```

→ **只有 1 个调用点**（`HardwareMonitor.cs:188`）+ 1 处定义（`SensorMap.cs:58`）+ 1 条注释（`SensorMap.cs:57`）。**若发现第 2 个调用点，签名变更会导致编译错误 —— 需同步处理。**

> ⚠️ **不要用 `Select-String -Path 'src\**\*.cs'`** —— PowerShell 的 `-Path` 通配符里 `**` **不是**"递归任意层"，实测它只跨一层目录，会漏掉 `src\System\HardwareServices\` 下的全部文件（本机实测：该写法只命中 `HardwareMonitor.cs` 一处，漏掉 `SensorMap.cs` 的定义与注释）。必须用上面的 `Get-ChildItem -Recurse` 写法。

- [ ] **Step 2.4: 编译验证**

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
& $dotnet build 'LiteMonitor.csproj' -c Release
```

Expected: `生成成功`，0 错误。

- [ ] **Step 2.5: 提交**

```powershell
git add src/System/HardwareServices/SensorMap.cs src/System/HardwareMonitor.cs
git commit -m "fix(hardware): 10 分钟兜底重建后同步刷新取值缓存

EnsureFresh 改为返回 bool(是否重建), UpdateAll 据此调用 PreCacheAllSensors。
此前 HardwareValueProvider.GetValue 的通用兜底已移除对 _sensorMap 的直接查找,
只查静态快照 _manualSensorCache; 重建把键补进 _map 之后, 若不刷新快照,
取值层永远读不到该键 —— 这正是核显占用率\"无法自愈\"的第二层原因。"
```

---

## Task 3: 取值层双保险 —— 经论证后**不做**

**结论：不改 `HardwareValueProvider.cs`。**

曾考虑在 `HardwareValueProvider.cs:502-504` 的通用兜底里恢复 `_sensorMap.TryGetSensor(key, ...)` 查找（上游 PR #586 的第三种修法思路是让取值层直接读活表）。**经评估否决**，理由：

1. **热路径代价**：该兜底对**所有未命中静态缓存的 key** 都会触发。硬件本身不支持的指标（如无风扇的核显的 `GPU.Fan`）会**每 tick 都 miss**，等于每 tick 多一次 `SensorMap._lock` 的锁获取 —— 恰好是 `:503` 那句"移除锁和 _sensorMap 查找"当初想消除的开销。
2. **锁序风险**：`GetValue` 持 `HardwareMonitor._lock` 后再取 `SensorMap._lock`。
   - **启动路径**（`HardwareMonitor.cs:321` 的 Rebuild，在 `lock (_lock)` 内）：`HardwareMonitor._lock` → `SensorMap._lock`，与 `GetValue` **同序**，无死锁。
   - **重载路径**（`HardwareMonitor.cs:498` 的 Rebuild，**在锁外**）：**只持 `SensorMap._lock`**，不构成嵌套，不影响 `GetValue` 的加锁顺序。
   - **切换显卡路径**（`HardwareValueProvider.cs:203` 的 Rebuild，在该文件自己的 `lock (_lock)` 内，而该 `_lock` 就是 `HardwareMonitor._lock`）：同样是 `HardwareMonitor._lock` → `SensorMap._lock`。
   - **当前所有路径的加锁顺序一致、无死锁**；但引入新的嵌套路径后需要持续维护这个隐式约定 —— 这正是"不要在取值层随手加 `SensorMap` 查找"的额外理由。
3. **不必要**：Task 1 + Task 2 已覆盖**全部** Rebuild 路径：

   | Rebuild 调用点 | 位置 | 预热? | 快照刷新? | 结果 |
   |---|---|---|---|---|
   | 启动初始化 | `HardwareMonitor.cs:321` | ✅ Task 1.3 | ✅ `:324` | ✓ |
   | 硬件重载 / 热插拔 | `HardwareMonitor.cs:498` | ✅ Task 1.4 | ✅ `:499` | ✓ |
   | 10 分钟兜底 | `HardwareMonitor.cs:188` | 传感器早已由 `UpdateAll` 多次更新上线 | ✅ **Task 2.2** | ✓ |
   | 切换首选显卡 / 改显卡数量 | `HardwareValueProvider.cs:203` | 传感器早已上线 | ✅ `:221` | ✓ |

4. **可测性**：Task 1 的判定逻辑已被抽成纯函数 `GpuWarmup.NeedsWarmUp`，可在无 Mock 的前提下被单测覆盖；而取值层兜底是带状态的分支，测不到也不必测。

> 若未来新增了**未在上表中的** Rebuild 调用点，应先考虑给那个路径补 `PreCacheAllSensors`，而不是回退到"取值层直接读活表"。

---

## Task 4: 单元测试

**Files:**
- Create: `tests/LiteMonitor.Tests/GpuWarmupTests.cs`

**Interfaces:**
- Consumes: `GpuWarmup.NeedsWarmUp`、`SensorMatcher.MatchGpu`
- Produces: 核显/独显两类机器的判定逻辑回归保护

- [ ] **Step 4.1: 写测试文件**

新建 `G:\TOOL\LiteMonitor-fork\tests\LiteMonitor.Tests\GpuWarmupTests.cs`：

```csharp
using System.Collections.Generic;
using LiteMonitor.src.SystemServices;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace LiteMonitor.Tests
{
    /// <summary>
    /// Intel 核显占用率恒为 0 修复的回归测试。
    ///
    /// 本测试工程只有 xunit, 没有 Mock 框架, 所以全部用例只测纯函数
    /// (GpuWarmup.NeedsWarmUp / SensorMatcher.MatchGpu), 不构造 IHardware / ISensor 桩。
    /// </summary>
    public class GpuWarmupTests
    {
        // ---------- GpuWarmup.NeedsWarmUp ----------

        [Fact]
        public void NeedsWarmUp_传感器集合为空时为true()
        {
            // 完全未初始化的极端情况
            Assert.True(GpuWarmup.NeedsWarmUp(new List<SensorType>()));
        }

        [Fact]
        public void NeedsWarmUp_存在Load传感器时为false()
        {
            // 独显 (NVIDIA / AMD / Intel 独显) 构造函数已自调 Update(), 传感器开箱即就绪
            var types = new List<SensorType> { SensorType.Load, SensorType.Temperature, SensorType.Power };
            Assert.False(GpuWarmup.NeedsWarmUp(types));
        }

        [Fact]
        public void NeedsWarmUp_有非Load传感器但无Load时为true()
        {
            // ★ Intel 核显的真实形态: 构造完成后 Sensors 里已经有
            //   Temperature / Clock / Voltage / Power, 唯独没有 Load。
            var types = new List<SensorType>
            {
                SensorType.Temperature, SensorType.Clock, SensorType.Voltage, SensorType.Power
            };
            Assert.True(GpuWarmup.NeedsWarmUp(types));
        }

        // ---------- SensorMatcher.MatchGpu: 核显负载传感器命名 ----------

        [Theory]
        [InlineData("D3D 3D")]        // Intel 核显的负载传感器
        [InlineData("GPU Core")]       // NVIDIA / AMD 的核心负载
        [InlineData("GPU D3D 3D")]     // 独显的 D3D 3D 通道
        public void MatchGpu_负载传感器映射到GpuLoad(string sensorName)
        {
            // 注意: 测试工程 csproj 是 <Nullable>disable</Nullable>,
            //       这里不要用 string? (会产生 CS8632 警告), 直接用 string 接收 null 即可。
            string key = SensorMatcher.MatchGpu(sensorName, SensorType.Load, true);
            Assert.Equal("GPU.Load", key);
        }

        [Fact]
        public void MatchGpu_核显共享显存映射到GpuVramUsed()
        {
            // 核显的 "D3D Shared Memory Used" 是 GPU.VRAM.Used 的来源
            string key = SensorMatcher.MatchGpu("D3D Shared Memory Used", SensorType.SmallData, true);
            Assert.Equal("GPU.VRAM.Used", key);
        }
    }
}
```

> ⚠️ `SensorMatcher.MatchGpu` 的第三个参数 `preferShared` 传 `true` 表示核显口径（`SensorMatcher.cs:74` 由 `HardwareRules.ShouldUseSharedMemory(hw)` 决定）。核显的 `D3D Shared Memory Used` 在 `preferShared: true` 时才映射为 `GPU.VRAM.Used`（`:167-169`）。
>
> ⚠️ 若 `MatchGpu` 的签名与本计划引用不符，先读 `src/System/HardwareServices/SensorMatcher.cs:137` 校正后再写测试。

- [ ] **Step 4.2: 运行测试**

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
& $dotnet test 'tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 全部通过。
> 测试类：原有 6 个（`GpuItemSyncTests` / `GpuKeyTests` / `GpuVramTests` / `HardwareRulesGpuNameTests` / `MetricUtilsGpuKeyTests` / `GpuGroupTests`）+ 新增 1 个 `GpuWarmupTests` = **共 7 个测试类**（另有 `CfgFileCollection` 是 `[CollectionDefinition]` 集合夹具，不是测试类）。
> 用例数：**实施时的实际基线是 221 个 → 改后 228 个**（新增 7 个 = 3 个 `Fact` + 1 个 `Theory`×3 + 1 个 `Fact`）。**已实测**：改后 `228/228` 通过。
> （计划初稿写的基线 139 是更早的快照；仓库在此期间已并入插件市场、语言包等测试。判断标准始终是"**基线 + 7**"，不要死抠绝对数。）


- [ ] **Step 4.3: 提交**

```powershell
git add tests/LiteMonitor.Tests/GpuWarmupTests.cs
git commit -m "test(hardware): 补核显预热判定与核显传感器命名的回归测试

覆盖 GpuWarmup.NeedsWarmUp 的三种状态(空集合/有Load/只有非Load),
以及 SensorMatcher.MatchGpu 对核显 'D3D 3D' 与 'D3D Shared Memory Used'
的映射规则。"
```

---

## Task 5: 真机验证

**Files:** 无（只验证）
**Interfaces:** Consumes: Task 1~4 全部成果；Produces: 可发布结论

- [ ] **Step 5.1: 干净构建 + 全量测试**

```powershell
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\TOOL\dotnet-sdk\dotnet.exe'
Set-Location 'G:\TOOL\LiteMonitor-fork'
& $dotnet build 'LiteMonitor.csproj' -c Release --no-incremental
& $dotnet test 'tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj' -v minimal
```

Expected: 编译 **0 错误**（仓库基线本身有 232 条既有 warning，与本计划无关）；测试全通过。

> ⚠️ **不要用 `Remove-Item -Recurse -Force bin, obj` 做"干净构建"** —— 本机运行时安全策略会拦截 PowerShell 的递归删除（提示改用可恢复删除），会导致步骤中断。用 `--no-incremental` 强制全量重编译即可达到同样效果（**已实测：0 错误**）。
> （若确实要删产物，注意 `bin` / `obj` 在**根目录和 `tests\LiteMonitor.Tests\` 下各有一份**，只删根目录那份不算干净；`--no-incremental` 对主工程与测试工程都生效，不受此影响。）

- [ ] **Step 5.2: Intel 核显机器验证（必测，需有 Intel 核显的机器）**

**开发机实况（本仓库工作副本所在机器）**：`AMD Ryzen 7 5800X`（**无核显**）+ `NVIDIA GeForce RTX 3080` + `NVIDIA GeForce RTX 3080 Ti`，另挂 4 个虚拟显示适配器（GameViewer / Meta Virtual Monitor / MuMu / Todesk）。

→ **开发机无法直接验证核显路径**，必须另找一台**有 Intel 核显**的机器（或在真机反馈时补测）。**AMD APU 机器不适用本步骤** —— 它的失效模式不同（见「AMD APU：另一种失效模式」）。开发机上能验证的是 Step 5.3（独显零回归）与 Step 5.4/5.5（缓存刷新路径）。

| # | 验证项 | 期望 |
|---|---|---|
| 1 | 启动后**不切换任何设置**，直接看 GPU 占用率 | 核显（0 号卡）占用率**立即有值且随负载变化**，不是空白、不是恒 0 |
| 2 | 多卡模式（监控显卡数量 = 2） | 两张卡各自的占用率都有值，标签能区分 |
| 3 | 显存 | 核显的 `GPU.VRAM.Used` 有值（来自 `D3D Shared Memory Used`） |
| 4 | 任务管理器对照 | 跑一段负载，占用率与任务管理器同量级 |

- [ ] **Step 5.3: 独显机器验证（防回归，开发机即可测）**

**前置**：本机为双 NVIDIA 3080/3080Ti + 4 个虚拟显示适配器。

| # | 验证项 | 期望 |
|---|---|---|
| 1 | 启动耗时 | 与改动前**基本一致**（预热对独显是零成本：传感器已上线 → 跳过 `Update()`） |
| 2 | GPU 占用率 | 正常有值，无变化 |
| 3 | 任务管理器 / 任务栏 | 无异常 |
| 4 | **统计预热次数** | 用 Debug 输出（Task 1.2 的 `[GpuWarmup]` 日志）确认：两张 NVIDIA 卡**不应**出现预热日志；若 4 个虚拟显示适配器出现预热日志，说明它们确实被 LHM 归入 GPU 类型且无 Load 传感器 —— 此时评估是否需要追加"跳过虚拟适配器"的规则（见 Task 1.2 备注） |
| 5 | 多卡模式（监控显卡数量 = 2） | 两张 NVIDIA 卡各自的占用率都有值，标签能区分，且与改动前一致 |

> 验证启动耗时的方式：改动前后各测 5 次冷启动到"主窗口出现"的耗时，取中位数对比。**预期差异 < 50ms**。
> 若实测超出，优先怀疑虚拟显示适配器触发的额外 `Update()`（见上表第 4 行）—— 先记录实际值，再决定是否追加"跳过虚拟适配器"规则，而不是直接回退整个修复。
>
> ⚠️ 预热日志用 `System.Diagnostics.Debug.WriteLine` 输出，**Release 构建下该输出会被完全编译掉**。要看日志必须用 **Debug 配置**构建：`& $dotnet build 'LiteMonitor.csproj' -c Debug`。
>
> **归因说明（实测更正）**：真正的原因是 **`System.Diagnostics.Debug.WriteLine` 带 `[Conditional("DEBUG")]`** —— Release 配置**不定义** `DEBUG` 条件符号，调用点在编译期就被剔除（`.csproj` 里没有任何 `DefineConstants` 覆盖）。
> **与 `DebugType=none` 无关**：`LiteMonitor.csproj:12-13` 的 `<DebugType>none</DebugType>` / `<DebugSymbols>false</DebugSymbols>` 位于**唯一一个无条件的 `PropertyGroup`** 里，**对 Debug 与 Release 两种配置同样生效**，而 Debug 配置下日志字符串依然存在。
> **实测证据**（扫 `bin\Debug\net8.0-windows\LiteMonitor++.dll` vs `bin\Release\net8.0-windows\LiteMonitor++.dll` 中的 `HardwareMonitor.cs:206` 同类日志 `"[GPU Error] Update failed: ..."`）：Debug ×2、Release ×0 —— 两种配置的 `DebugType` 完全相同，差异只来自 `DEBUG` 符号。

- [ ] **Step 5.4: 兜底重建验证（10 分钟）**

```powershell
# 启动程序, 保持运行 11 分钟以上, 观察 GPU 占用率在第 10 分钟的兜底重建后
# 是否出现数值 (修复前: 永不出现; 修复后: 正常有值)
```

Expected: 占用率**全程有值**，第 10 分钟兜底重建时**不闪烁、不归零**（重建后 `PreCacheAllSensors` 会重新填充快照）。

- [ ] **Step 5.5: 切换显卡验证（原有自愈路径不应被破坏）**

1. 设置 → 系统与硬件 → 首选显卡，从 A 切到 B
2. 切回 A

Expected: 切换后占用率立即更新，**无空白、无归零**。

- [ ] **Step 5.6: 热插拔验证（可选）**

拔出/插入显卡（或用硬件开关禁用独显）→ 程序应自动重载硬件。

Expected: 重载后 GPU 列表正确，占用率恢复有值。**这条直接验证 `Task 1.4` 的重载预热。**

- [ ] **Step 5.7: 写验证记录**

在 `docs/superpowers/plans/` 下新建 `2026-10-20-igpu-load-fix-verification.md`，写入 Task 5.2~5.6 的**实际观察值**（尤其是核显占用率随负载的变化曲线、独显启动耗时对比数据），然后：

```powershell
git add docs/superpowers/plans/2026-10-20-igpu-load-fix-verification.md
git commit -m "docs(hardware): 记录核显占用率修复的真机验证结果"
```

---

## 回滚方案

每个 Task 一次独立提交，若某步出问题：

```powershell
git log --oneline
git revert <commit-sha>          # 保留历史，安全回退
# 或
git reset --hard <commit-sha>    # 仅限未推送的本地分支
```

**功能开关式回退**：核显占用率只影响显示，不影响其他功能。若发现预热在某些机器上引入卡顿，把 `GpuWarmup.WarmUpIfNeeded` 的调用临时注释掉即可回到改动前行为（核显占用率会再次失效，其余功能不受影响）。

---

## 附录 A：本计划**没有**做的事（YAGNI）

- **不改 `HardwareValueProvider.GetValue` 的兜底逻辑** —— 理由见 Task 3 的完整论证（热路径锁开销 + 锁序隐式约定 + Task 1/2 已覆盖全部路径）。
- **不在 `SensorMap.Rebuild` 内预热** —— `HardwareMonitor.cs:498` 的 Rebuild 在锁外调用，在那里触发 `hw.Update()` 会与 `UpdateAll` 持锁的 `hw.Update()` 竞争同一硬件对象。
- **不加"已预热过"的 HashSet 幂等保护** —— 条件判定本身已幂等：预热后传感器上线 → `NeedsWarmUp` 返回 false → 下次不再 `Update()`。唯一例外是硬件本身不提供 Load 传感器的卡（虚拟显示适配器等），最多多一次 `Update()`，发生在启动/重载这类低频时机，可忽略。（上游 PR #586 称单次 `Update()` 约 7ms，此数字**未经本仓库实测**。）
- **不改 `SensorMatcher` 的任何命名规则** —— 核显的 `D3D 3D` / `D3D Shared Memory *` 规则**已经是对的**（`SensorMatcher.cs:139` / `:167-169`），问题在数据层不送传感器过来。
- **不改 `GpuKey` / `GpuRegistry` / `MonitoredGpuCount`** —— 多显卡改造的语义保持原样。
- **不引入 Mock 框架** —— 沿用现有测试工程的"只测纯函数"约定。
- **不处理 `GPU.Fan` 在核显上的缺失** —— 核显通常没有风扇传感器，`SensorMap.cs:228-236` 的风扇映射找不到时本就不写键，属预期行为，与本 bug 无关。

---

## 附录 B：与上游 PR #586 的差异

> ⚠️ **引用说明**：本附录依据 `docs/UPSTREAM-FEEDBACK-2026-10-20.md` 中对上游 PR #586 的**摘要**整理，**未逐条核对 PR 原始 diff**。上游方案若被采纳到本 fork，需要先拉取 PR 原文复核下表各项。上游 PR 合并率为 0（18 open + 12 关闭未合并），因此**本 fork 不会自动继承该修复**。

上游 `Diorser/LiteMonitor` 的 PR #586（`MoonAsked`，OPEN，+31/-5）修的是同一个问题，但方案与本计划不同：

| | 上游 PR #586 | 本计划 |
|---|---|---|
| 预热位置 | `SensorMap.Rebuild` 内 | `HardwareMonitor` 的两个 warm-up 调用点（锁内） |
| 判定依据 | "负载/显存传感器尚未激活"（更细） | "是否存在任意 Load 类型传感器"（更粗、更保守） |
| 兜底重建 | `EnsureFresh` 返回 bool → 调用方刷新缓存 | 同（Task 2） |
| 取值层兜底 | 保留 `_sensorMap` 查找（第 3 种修法） | **不做**（Task 3 已论证） |
| 测试 | 无 | 有（Task 4） |

**两者的取舍不同，不是简单的优劣之分**：

- 本计划把预热放在 `HardwareMonitor` 的 warm-up 调用点（**锁内**），是因为 `HardwareMonitor.cs:498` 的 `Rebuild` 在**锁外**执行 —— 在那里触发 `hw.Update()` 会与 `UpdateAll` 持锁的 `hw.Update()` 竞争同一硬件对象。上游方案把预热放进 `Rebuild`，若被采纳到本 fork，需先评估这一竞争风险。
- 本计划的判定更**粗**（只看有没有 `SensorType.Load`），换来的是**更少的误判**与**更低的耦合**（不依赖 `SensorMatcher` 的具体命名规则）；上游判定更细，能区分"负载已激活但显存未激活"，但对 LHM 内部传感器名称的假设更强。
- 本计划**带单元测试**，上游 PR 无测试。**本计划不做取值层兜底**（保留 `_sensorMap` 查找），因为 Task 3 已论证该兜底会给热路径加锁，恰好是 `HardwareValueProvider.cs:503` 当初移除的开销。

**参考来源**：`https://github.com/Diorser/LiteMonitor/pull/586`（关联 issue #569 / #521 / #463 / #434 / #450）
