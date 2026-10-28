# TASK-2 集显/多 GPU 与 GPU 传感器数据 —— 上游 issue 取证核实报告

| 项 | 值 |
|---|---|
| 任务单 | TASK-2（集显/多 GPU 与 GPU 传感器数据） |
| 检查日期 | 2026-10-25 |
| 仓库 | Diorser/LiteMonitor（上游） / mojingjing-git/LiteMonitor（本地 fork，LiteMonitor++） |
| 上游 commit | `3b91b5c`（2026-10-21，"fix: 完成 1.3.7 发版审查并修复监控与网络边界问题"），ref `upstream/master` |
| fork commit | `5e756e7`（分支 `master`），相对上游 src/ 共 172 文件改动（+52692 / -2984） |
| 本报告性质 | 只读取证。未修改任何 `src/**`；未执行 checkout/switch/stash/reset/clean/restore/commit/branch；未构建、未跑全量测试 |
| 结论强度约定 | 「代码级确认」= 由源码/字节码直接判定；「未复现」= 本机硬件不具备，缺件已列明 |

---

## 0. 核查方法与关键时间线

### 0.1 时间线（决定"issue 是否已被上游修复"）

```
2026-05-15  上游 6a15234 / 90d20e8   1.3.5 / 1.3.6
2026-05-15  issue #434 创建（RTX 5070 笔记本）
2026-05-19  issue #450 创建（B580）
2026-06-20  issue #497 创建（AMD 9070 GRE）
2026-06-21  issue #499 创建
2026-07-08  上游 a71ce75（1.3.7 之前的最后一次上游提交）
2026-07-15  issue #521 创建（U5 255 核显）
2026-09-05  issue #569 创建（i3-4130 核显）
2026-09-30  issue #592 创建（B580）
2026-10-05  issue #596 创建（U5 225H）
2026-10-08  issue #603 创建（小米 Book 14 Pro 核显）
2026-10-21  上游 3b91b5c  ★ 引入 GPU 建图预热 + 映射自愈
```

**关键事实：9 个 issue 全部创建于 `3b91b5c` 之前。** 因此"issue 提交时上游确实有问题"成立，但"上游当前代码（3b91b5c）仍然有问题"**不成立**——`3b91b5c` 的 diff 恰好包含针对这批报告的核心修复（见 §5.1）。这是本任务最重要的一条判定分界。

### 0.2 取证手段

1. **issue 原文与评论**：`gh issue view`（JSON 全量拉取，含 body/comments/createdAt/updatedAt/labels）。9 个 issue 的 state 在本次独立复核中**全部为 OPEN**（含此前瞬时失败的 #596，实为 OPEN + 1 评论，非 transient）。
2. **上游/fork 源码对照**：`git show upstream/master:<file>` / `git show master:<file>` / `git diff upstream/master master -- <path>` / `git grep`（全程只读）。
3. **依赖库取证（决定性）**：本机存在 `LibreHardwareMonitorLib 0.9.6`（上游与 fork 同一版本），我用 `System.Reflection.Metadata` 直接解码其 PE/IL：
   - 逐 `TypeDef` 提取 `IntelDiscreteGpu` / `IntelIntegratedGpu` / `AmdGpu` / `NvidiaGpu` 的 `ldstr` 与 `newobj Sensor(name, index, SensorType, ...)` 实参；
   - 并用 `LibreHardwareMonitorLib v0.9.6` 的上游源码（`IntelDiscreteGpu.cs`、`IntelGpuGroup.cs`，GitHub raw）交叉校验传感器命名与初始化时序。
4. **fork 既有回归测试**：`tests/LiteMonitor.Tests/GpuWarmupTests.cs`（覆盖 `GpuWarmup.NeedsWarmUp` 与 `SensorMatcher.MatchGpu`）。

> 取证脚本曾临时写入 `G:\TOOL\LiteMonitor-fork\.tmp\verification\`，**已于结束前删除**（该目录下原有的 `artifacts/` 未触碰）。

---

## 1. 汇总表

| Issue | 当前是否 open | 上游结论（3b91b5c） | fork 结论（5e756e7） | 证据位置 | 复现/验证结果 | 下一步 |
|---|---|---|---|---|---|---|
| **#603** GPU显示为0%（小米 Book 14 Pro 核显） | OPEN（0 评论，2026-10-08） | **已修复**：`SensorMap.Rebuild` 建图期即 `hw.Update()` 显卡，核显懒激活的 Load 传感器在建表时已注册 | **已修复（独立路径）**：`GpuWarmup` 在建表前补一次 `Update()`；但**缺上游的运行时自愈** | 上游 `SensorMap.cs:126,141-143,326`；fork `GpuWarmup.cs:48-63`、`HardwareMonitor.cs:920` | 代码级确认；本机无 Intel 核显 → **未复现** | 补 `RepairGpuSensorMap` 等价机制（见 §7-P1） |
| **#596** U5 225H 需切换首选 GPU 才恢复 | OPEN（1 评论，2026-10-05） | **已修复**（同上）；且 `RepairGpuSensorMap` 提供运行时二次自愈 | **部分修复**：预热已覆盖建图期；但无"键缺失→重建映射"的运行时自愈 | 上游 `HardwareMonitor.cs:587-622`；fork `HardwareMonitor.cs:1009-1024`、`SensorMap.cs:191` | 代码级确认；**未复现**（缺 Core Ultra 5 225H / Arc 130T） | 同 #603 |
| **#592** Intel B580 使用率不显示 | OPEN（0 评论，2026-09-30） | **部分修复**：键可建（Arc 的 Load 传感器名为 `GPU Core`，能被匹配），但**值**依赖 Intel GCL 遥测；GCL 不可用时仍为 null→显示 0 | **部分修复**：同上游；fork 额外支持多卡并存 | LHM `IntelDiscreteGpu.cs`（`_loadGlobalActivity = new Sensor("GPU Core", 0, SensorType.Load, …)`）；`SensorMatcher.cs:169` | 代码级确认；**未复现**（缺 B580 + IGCL 驱动栈） | 见 §4-#592「需硬件验证」 |
| **#569** 无法读取集成显卡 GPU 使用率（i3-4130） | OPEN（1 评论，2026-09-05） | **已修复**（建图期预热） | **已修复 + 超出**：fork 支持 `MonitoredGpuCount ≥ 2` 同时监控核显+独显，正对应评论"甚至希望支持多显卡数据" | 上游 `SensorMap.cs:141-143`；fork `SensorMap.cs:126-146`、`Core/GpuKey.cs`、`Core/GpuRegistry.cs` | 代码级确认；**未复现**（缺 i3-4130） | 以多卡配置在真机验收 |
| **#521** U5 255 核显不能正确显示 GPU 使用率（频率正常、占用率恒 0） | OPEN（1 评论，2026-07-15） | **已修复**（同上） | **已修复**（同上） | 同 #603 | 代码级确认；**未复现**（缺 U5 255） | 与 #603 合并验收 |
| **#450** intel b580 不显示占用，必须手动切换首选显卡 | OPEN（3 评论，2026-05-19） | **已修复**（建图期预热 + `RepairGpuSensorMap`）；"必须手动切换"的成因是旧版建图早于传感器注册，切换触发一次 `Rebuild` 才补上键 | **部分修复**：fork 的 `GpuWarmup` 覆盖建图期；但**无 `RepairGpuSensorMap`**；且 `MonitoredGpuCount==1` 时自动模式只监控 1 张卡 | 上游 `SensorMap.cs:141-143`、`HardwareMonitor.cs:196-200,587-622`；fork `SensorMap.cs:126-146,191`、`HardwareMonitor.cs:456-458,1719-1739` | 代码级确认；**未复现**（缺 B580/B570） | 优先补自愈 + 复核自动模式的卡选择语义 |
| **#434** 不显示 gpu 占用率（含 B580/B570/RTX5070 评论） | OPEN（7 评论，2026-05-15） | **已修复**（同上）；上游作者在评论中给出的猜测"可能是没有匹配上"**部分正确**（真实成因是建图时机，匹配规则本身可命中） | **部分修复**（同 #450） | 上游 `SensorMap.cs:141-143`、`HardwareMonitor.cs:587-622`；fork `GpuWarmup.cs` + 缺 Repair | 代码级确认；**未复现**（缺 B580/B570/RTX5070） | 同 #450 |
| **#497** AMD 9070 GRE 温度/频率/功耗无数据（占用率与显存有） | OPEN（2 评论，2026-06-20） | **非代码缺陷**：LHM `AmdGpu` 的温度/频率/功耗传感器**存在且能被匹配**，但值依赖 ADL/OD5/OD6 通路；用户以 DDU 清理驱动后恢复（评论已自证） | **非代码缺陷**（同上）；fork 的匹配规则与上游等价 | LHM `AmdGpu` IL（`GPU Core`/`GPU Hot Spot`/`GPU SoC` = Temperature，`GPU Package`/`GPU PPT` = Power，`GPU Core`/`GPU Memory`/`GPU SoC` = Load）；`SensorMatcher.cs:169-170` | 代码级确认 + 用户评论佐证；**未复现**（缺 9070 GRE + ADL 26.3.1） | 见 §4-#497「区分不支持 vs 失败」 |
| **#499** 任务管理器独显占用 0，软件却一直显示显存占用 | OPEN（0 评论，2026-06-21） | **显示语义差异（非缺陷）**：应用显示的是"显存已用/总量"，任务管理器显示的是"引擎利用率"；**但**若应用监控的是核显，取的确实是恒非零的共享显存 | **部分缓解**：fork 增加 `GpuVramSource`（厂商通道串卡检测 + D3D 择优 + 注册表真实容量）与 `MarkStaleGpuValues`（换卡作废旧值）；**语义差异仍在** | 上游 `HardwareValueProvider.cs:393-406`、`SensorMatcher.cs:88-109`；fork `HardwareValueProvider.cs:958-1023`、`GpuVramSource.cs`、`GpuVramCapacity.cs` | **信息不足**（缺截图判读 + 缺具体机型）；代码路径已定位 | 见 §4-#499 |

---

## 2. 传感器问题矩阵（本任务核心交付）

| Issue | GPU 型号/类别 | 现象 | 对应代码路径（文件:行号） | 根因假设 | 是否同一根因 |
|---|---|---|---|---|---|
| #603 | Intel 核显（小米 Book 14 Pro / 未知具体型号） | GPU 占用率恒 0% | 上游 `src/System/HardwareServices/SensorMap.cs:141-143`→`:326` `TryUpdateGpuForMapping`；旧版为 `SensorMap.cs` 内被注释的 `//hw.Update();`<br>fork `src/System/HardwareServices/GpuWarmup.cs:48-63`、`src/System/HardwareMonitor.cs:920`→`SensorMap.cs:191` | **RC1**：Intel 核显 Load 传感器（`D3D 3D`）为**懒激活**，建表时未注册 → `GPU.Load` 键不存在 → 显示 0 | ✅ RC1（与 #596/#569/#521/#450/#434-B580/#592 同簇） |
| #596 | Intel Arc 130T 核显（Core Ultra 5 225H） | 每次开机 0%；切换一次首选显卡即恢复，下次开机又 0 | 同上；另含切换路径：上游 `HardwareValueProvider.cs:196-211`；fork `HardwareValueProvider.cs:378-429` | **RC1** 为主（键缺失 + 旧版无运行时自愈） | ✅ RC1 |
| #592 | Intel Arc B580 独显 | 使用率不显示（无任何数值） | `src/System/HardwareServices/SensorMatcher.cs:169` `MatchGpu(Load, "GPU Core")` → 命中；值源为 LHM `IntelDiscreteGpu.UpdateUtilizationFromActivityCounter(_telemetry.globalActivityCounter, …)` | **RC2**：键存在但值为 null（IGCL 遥测不可用）；**若 `IntelGcl.Initialize()` 失败则该卡根本不在硬件树** | ⚠️ RC2（与 RC1 同症状、不同根因） |
| #569 | Intel 核显（i3-4130，HD 4000） | 无法读取核显占用率；作者称"只支持独显数据" | 上游 `SensorMap.cs:141-143`；fork `SensorMap.cs:126-146`（`MonitoredGpuCount`）、`Core/GpuKey.cs`、`Core/GpuRegistry.cs` | **RC1** + 上游单卡模型限制 | ✅ RC1（fork 已超出） |
| #521 | Intel 核显（U5 255 / 255H） | GPU 频率正常，**占用率恒 0** | 频率走 `src/System/HardwareServices/ComponentProcessor.cs:171`（直接读 `hw.Sensors`，不经映射表）；占用率走 `SensorMatcher` 映射表 → 键缺失 | **RC1**：正是"频率走直连通道所以正常、占用率走映射表所以缺失"的典型指纹 | ✅ RC1 |
| #450 | Intel Arc B580 独显 | 不显示占用；必须手动切换首选显卡；"不手动改就是检测核显"；任务栏显示有时消失 | 上游 `SensorMap.cs:124-125,141-143`（`selectedGpu` 过滤 + 预热）、`HardwareMonitor.cs:572-585,587-622`；fork `SensorMap.cs:126-146,191`、`HardwareMonitor.cs:456-458,1719-1739` | **RC1 + RC2 叠加**：① 旧版建图早于注册；② 手动选择改变了被映射的卡集合/预热时机 | ✅ RC1（并可能叠加 RC2） |
| #434 | RTX 5070 笔记本 / B580 / B570（评论） | 不显示占用；B570 用户称"硬件详情里 GPU Load 一直在跳，但界面恒 0.0%" | 同 #450；上游作者回复"可以读到传感器数据但没显示出来，可能是没有匹配上" → **作者猜测部分正确** | **RC1**（键缺失）——"硬件详情有跳动的 GPU Load"恰是"传感器已注册但映射表里没有该键"的铁证 | ✅ RC1 |
| #497 | AMD Radeon 9070 GRE（独显，驱动 26.3.1） | 占用率+显存有数据；温度/频率/功耗无数据（硬件详情里也没有） | LHM `AmdGpu` IL：`GPU Core`/`GPU Hot Spot`/`GPU SoC`=Temperature、`GPU Package`/`GPU PPT`=Power、`GPU Core`/`GPU Memory`/`GPU SoC`=Load；应用侧 `SensorMatcher.cs:169-170` 均可命中 | **RC3**：ADL/OD5/OD6 通路未提供数据（驱动未干净安装）；**非匹配缺陷、非 NVML-only 误伤** | ✅ RC3（独立簇） |
| #499 | 未提供型号（推测为混合显卡笔记本） | 任务管理器"独显占用 0%"，软件"一直显示显存占用" | 上游 `HardwareValueProvider.cs:393-406`（used/total 百分比）、`SensorMatcher.cs:88-109`（`preferShared` 分支）；fork `HardwareValueProvider.cs:958-1023`（`GetGpuVramPercent`）、`GpuVramSource.cs`、`GpuVramCapacity.cs` | **RC4**：显示口径不同（利用率% vs 显存已用%）；若监控对象是核显，则取的是恒非零的共享显存；叠加 **M3**（换卡后旧值未清空） | ✅ RC4（独立簇） |

### 2.1 三种现象 ↔ 三条代码路径（核实任务第 3 问）

| 现象 | 代码路径 | 证据 | 显示结果 |
|---|---|---|---|
| **「0」**（值正常但恒为 0） | 传感器**存在**但 `ISensor.Value == null`（驱动/遥测不支持该字段） | LHM `AmdGpu`/`IntelDiscreteGpu` 的 `UpdateSensorFromTelemetry` 在 `bSupported == false` 时显式 `sensor.Value = null` | 上游 `src/Core/MetricUtils.cs:107` `float v = value ?? 0.0f;` → **0.0%** |
| **「缺字段」**（键不存在） | 映射表 `SensorMap` 中根本没有该 Key | 旧版上游 `SensorMap.cs` 内 `//hw.Update();` 被注释 → 核显 Load 传感器未注册 → 建表时无 `GPU.Load` | 取值层返回 null → 同样落到 `value ?? 0.0f` → **0.0%** |
| **「旧值未清空」** | 通用兜底回退到 `_lastValidMap` 的历史有效值 | 上游 `HardwareValueProvider.cs:502-514`（`else if (_lastValidMap.TryGetValue(key, out var last)) result = last;`） | 显示**上一块卡/上一次会话**的数值 |

> **核实结论（直接回答"是否把『不受支持』误判为『读取失败』"）**：
> **是，且上游与 fork 同样存在。** 应用层没有任何"传感器不受支持 / 无数据"的状态位：`ISensor.Value == null`（不受支持）与映射键缺失（读取/注册失败）在 `MetricUtils.GetValueStr` 处被统一折叠成 `0.0f`，**用户无法从界面区分二者**。
> fork 已在 **CPU 电压**上建立了正确范式（`src/Core/MetricUtils.cs:114-121` → 缺失时返回 `"--"` 而非 0），但**该范式未推广到 GPU 指标**。
> 同时需确认：**AMD GPU 路径没有被 NVIDIA-only 逻辑误伤**——`SensorMatcher.MatchGpu`（`src/System/HardwareServices/SensorMatcher.cs:167-222`）是厂商无关的纯名称/类型规则，AMD 的 `GPU Core`（Load/Temperature）、`GPU Hot Spot`、`GPU SoC`、`GPU Package`、`GPU PPT` 全部命中；应用**从不**使用 NVML。

---

## 3. 三类 GPU 的取数路径（上游 vs fork 对照）

### 3.1 设备枚举

| 维度 | 上游 3b91b5c | fork 5e756e7 |
|---|---|---|
| 枚举入口 | `HardwareScanner.IsGpuHardware`（`GpuNvidia/GpuAmd/GpuIntel`） | 同（`src/System/HardwareServices/HardwareScanner.cs`） |
| 首选卡持久化 | `Settings.PreferredGpu`（`src/Core/Settings.cs`），存**硬件 Identifier**（`HardwareScanner.FindGpuByConfig`），旧值（显卡名）兼容 | 同 + 新增 `Settings.MonitoredGpuCount`（默认 1） |
| 自动模式选卡 | `SensorMap.Rebuild`：`selectedGpu == null` 时**映射全部 GPU**，冲突按 `HardwareRules.GetHwPriority` 消解（强卡胜） | `SensorMap.SelectMonitoredGpus`（`SensorMap.cs:126-146`）：`MonitoredGpuCount==1` 时**只选 1 张**（首选卡，否则优先级最高者）；`≥2` 时取前 N 张 |
| 优先级规则 | `HardwareRules.GetHwPriority`：NVIDIA=0；AMD 独显=0 / `AMD Radeon(TM) Graphics`=2；Intel `Arc`+`IsDiscreteArc`=0 / Arc 核显=1 / Iris=2 / UHD=3 | 完全一致（`src/System/HardwareServices/HardwareRules.cs:24-64`） |
| Intel 核显/独显判定 | 由 **LHM 内部**决定：`IntelGpuGroup` 先 `IntelGcl.Initialize()`，成功则用 GCL 枚举**独显**（跳过 INTEGRATED 标志）；核显走 **D3D 枚举**且要求 `deviceInfo.Integrated == true` | 同（同一份 LHM 0.9.6） |

> **关键外部事实（本次直接核实的 LHM 0.9.6 源码）**：
> - `IntelGpuGroup.cs`：若 `IntelGcl.Initialize()` 失败 → **一个 `IntelDiscreteGpu` 都不会被创建**（Arc 独显彻底不在硬件树里）；核显走的是**另一条 D3D 路径**，与 GCL 无关。
> - `IntelIntegratedGpu` 的 **Load 传感器不在构造函数里创建**，只在 `Update()` 里 `ActivateSensor` 注册 → **懒激活**（这正是 fork 计划 `2026-10-20-igpu-load-fix.md` 的立论，本次独立复核**证实该立论成立**）。
> - `IntelDiscreteGpu` 的 Load 传感器名为 **`GPU Core`**（全局活动计数器）、`GPU Render/Compute`、`GPU Media`；显存占用率传感器名为 **`GPU Memory`**（Load 型）。**`GPU Core` 含 "core" → 应用的 `MatchGpu` 能命中**。

### 3.2 传感器 Map 建立与重建时机

| 维度 | 上游 3b91b5c | fork 5e756e7 |
|---|---|---|
| 建表期是否预热显卡 | **是**。`SensorMap.cs:126` `shouldWarmGpuForMapping = cfg.IsAnyEnabled("GPU") \|\| cfg.WebServerEnabled`；`:141-143` → `:326` `TryUpdateGpuForMapping(hw)`（`hw.Update()`，异常吞掉） | **否（表内）**。`SensorMap.cs:191` 仍是 `//hw.Update();`；改为**外部**预热：`HardwareMonitor.cs:920`（init）与 `:1204`（reload）调用 `WarmUpGpuSensors()` → `GpuWarmup.WarmUpIfNeeded` |
| 预热策略 | 对**所有** GPU `Update()` 一次 | **只对"没有任何 Load 传感器"的 GPU** `Update()` 一次（`GpuWarmup.NeedsWarmUp`，`GpuWarmup.cs:35-42`）→ 独显机器零额外开销 |
| 10 分钟兜底 | `EnsureFresh` 返回 `bool`，重建后 `PreCacheAllSensors`（`HardwareMonitor.cs:187-188`） | 同（`HardwareMonitor.cs:456-458`） |
| 运行时自愈 | **有**。`ShouldRepairGpuSensorMap`（`HardwareMonitor.cs:587-604`）：已启用的 `GPU.Load/Temp/Fan/VRAM.*` 有键缺失 → `RepairGpuSensorMap()`（`:616-622`）重建映射 + 重取缓存（每次会话一次，`_gpuMapRepairAttempted`） | **无**。`grep 'Repair'` 在 `HardwareMonitor.cs` / `HardwareValueProvider.cs` / `SensorMap.cs` 中命中 **0** 次 |
| 换卡（首选/数量变更）后刷新 | `HardwareValueProvider.OnUpdateTickStarted`（`:196-211`）：`gpuChanged` → `Rebuild` + `PreCacheAllSensors` | `HardwareValueProvider.OnCollectionStarted`（`:378-429`）：`gpuChanged \|\| gpuCountChanged` → `Rebuild` + 清 `DetectedGpuVramTotalGB(ByKey)` + **`MarkStaleGpuValues()`**（作废旧卡最后有效值）+ `PreCacheAllSensors` + `_onDeviceSelectionChanged` 触发立即重采 |
| 多卡支持 | **无**。单套 `GPU.*` 键，冲突按优先级消解 | **有**。`GPU.{index}.{Metric}`（`Core/GpuKey.cs`、`Core/GpuRegistry.cs`、`Core/GpuItemSync.cs`、`Core/GpuGroupPolicy.cs`），第 0 卡额外写旧格式键兼容 |
| 显卡刷新范围 | `ShouldUpdateGpuHardware`（`:572-585`）：手动选中 → 只刷该卡；自动 → 刷 `CachedGpu` **或任何承载 `GPU.*` 键的硬件**（`IsMappedGpuSensorHardware`） | `GpuUpdateSelection.ShouldUpdate`（`HardwareMonitor.cs:1719-1739`）：只刷**监控集合内**的卡；映射未建好时（两者皆空）才全刷 |

### 3.3 显存取数来源（#499 相关）

| | 上游 3b91b5c | fork 5e756e7 |
|---|---|---|
| `GPU.VRAM` 计算 | `HardwareValueProvider.cs:393-406`：`used/total*100`，无则退 `GPU.VRAM.Load` | `HardwareValueProvider.cs:958-1023` `GetGpuVramPercent(key)`：同时取 `VRAM.Used/Total`（厂商通道）与 `VRAM.D3D.Used/Total`（D3D 通道） |
| 厂商通道 vs D3D 通道 | 冲突规则在 `SensorMap`："厂商优先"，D3D 只在厂商缺失时兜底；**无串卡检测** | `GpuVramSource.IsVendorChannelForeign`（`GpuVramSource.cs:44-69`）：厂商容量命中**另一块适配器**容量 ⇒ 判为串卡 ⇒ 改用 D3D 通道；`GpuVramCapacity` 从注册表取每卡真实容量 |
| 核显共享显存 | `SensorMatcher.cs:88-97`：`preferShared`（Intel 核显）时只认 `D3D Shared Memory *` | 同（`SensorMatcher.cs:194-204`），且 D3D 专用显存另开独立键 `GPU.VRAM.D3D.*`（`SensorMatcher.cs:176-182`） |
| 换卡后旧值 | **不处理**（`_lastValidMap` 仍可回退旧卡数值） | `MarkStaleGpuValues()`（`HardwareValueProvider.cs:463-467`）+ `_staleValuePrefixes` 机制**显式作废** |
| 显存占用率匹配规则 | `SensorMatcher.cs:109` `Load && Has(name,"memory")` → `GPU.VRAM.Load`。**注释说"不要 GPU Memory Controller"，但代码里没有 `!Has(name,"controller")`——注释与代码不一致** | `SensorMatcher.cs:218` `Load && Has(name,"memory") && !Has(name,"controller")` → **已修正上游这处不一致** |

---

## 4. 逐 issue 细述

### 4.1 #603 —— GPU 显示为 0%（Intel 核显，小米 Book 14 Pro）

1. **当前状态**：OPEN，0 评论，创建 2026-10-08，最后更新 2026-10-08。**创建于上游 3b91b5c（2026-10-21）之前。**
2. **问题摘要**：核显笔记本，监控面板 GPU 占用率恒为 0。作者猜测"可能是核显原因"。
3. **上游代码证据**：
   - `src/System/HardwareServices/SensorMap.cs:126` `shouldWarmGpuForMapping = cfg.IsAnyEnabled("GPU") || cfg.WebServerEnabled`；
   - `:141-143` → `:326` `TryUpdateGpuForMapping(hw)` → `hw.Update()`，异常吞掉（不影响其他硬件）；
   - `:124-125` 手动首选卡时，非选中卡**在预热之前**就被 `return` 跳过（`:130-137`）；
   - `src/System/HardwareMonitor.cs:587-622`：`ShouldRepairGpuSensorMap` 在键缺失时触发一次 `Rebuild` + `PreCacheAllSensors`。
   - 依赖层证据：LHM `IntelIntegratedGpu` 的 Load 传感器（`D3D 3D`）**不在构造函数创建**，只在 `Update()` 中 `ActivateSensor` 注册 ⇒ 建表早于注册 ⇒ `GPU.Load` 键缺失。
4. **fork 对照**：
   - `src/System/HardwareServices/GpuWarmup.cs:35-42`（`NeedsWarmUp`：无任何 Load 传感器 ⇒ true）、`:48-63`（`WarmUpIfNeeded`，逐卡 `Update()`，异常吞掉）；
   - `src/System/HardwareMonitor.cs:920`（init，**在** `:923` `Rebuild` **之前**）、`:1009-1024`（方法与约束说明）、`:1204`（reload 路径）；
   - `src/System/HardwareServices/SensorMap.cs:191` 仍为 `//hw.Update();`（表内不预热，靠外部预热）；
   - `tests/LiteMonitor.Tests/GpuWarmupTests.cs:33-43`（"有非 Load 传感器但无 Load ⇒ true"，即核显形态）与 `:47-57`（`D3D 3D`/`GPU Core` → `GPU.Load`）。
   - **差异（fork 弱于上游）**：fork **没有** `RepairGpuSensorMap` 等价的运行时自愈。
5. **复现条件与验证步骤**：见 §6 组合 A/B。需 Intel 核显机器（本机为 AMD Ryzen 7 5800X，无核显）→ **未复现**。
6. **结论**：**上游 = 已解决**（代码级确认，修复提交晚于 issue）；**fork = 已解决（独立路径），但自愈弱于上游**。
7. **风险**：若首次 `Update()` 时 DXGI/D3D 枚举尚未就绪（开机竞态），核显可能连硬件对象都不存在 → fork 与上游均**无法**靠一次 `Update()` 补回，只能 `ReloadComputerSafe` 重开 `Computer`（fork `HardwareMonitor.cs:1133`）。
8. **建议下一步**：把上游的 `RepairGpuSensorMap` 等价机制移植进 fork（见 §7-P1）。

### 4.2 #596 —— Intel Ultra 5 225H 使用率需切换首选 GPU 才恢复

1. **当前状态**：OPEN，1 评论（linzeer，2026-10-23："一样的问题，出bug了 / 只能自己让AI修了"），创建 2026-10-05。**创建于 3b91b5c 之前；最后评论晚于 3b91b5c（10-21），但评论内容仍是同一现象，未附新证据。**
2. **问题摘要**：redmibook pro 16，U5 225H。每次开机 GPU 恒 0；需在设置里切换首选显卡一次才恢复；下次开机又 0。作者描述了"自动 ↔ Arc 130T ↔ 自动"循环。
3. **上游代码证据**：同 #603（`SensorMap.cs:141-143,326`）；另外 `HardwareMonitor.cs:587-622` 提供**运行时**二次自愈；`HardwareValueProvider.cs:196-211` 提供切换后的重建+重取缓存。
4. **fork 对照**：`GpuWarmup` 覆盖建图期；`HardwareValueProvider.cs:394-417` 在 `gpuChanged` 时 `Rebuild` + `MarkStaleGpuValues()` + `PreCacheAllSensors`，并通过 `_onDeviceSelectionChanged` 触发立即重采 —— **fork 的"切换后立即恢复"路径比上游更完整**（上游切换后只重建映射，不作废旧值、不主动重采）。但 fork **缺运行时自愈**。
5. **复现条件与验证步骤**：见 §6 组合 A/E。需 Core Ultra 5 225H（Arc 130T 核显）→ **未复现**。
6. **结论**：**上游 = 已解决**；**fork = 部分解决**（建图期已修复；缺运行时自愈；切换后恢复路径优于上游）。
7. **风险**：fork 的 `GpuWarmup.NeedsWarmUp` 以"是否存在任意 Load 传感器"为判据。若某块 Intel 核显因驱动异常**已经暴露了一个 Load 传感器但其值恒为 null**，则 `NeedsWarmUp` 返回 false（不预热），而 `MatchGpu` 又能命中该名字 → 键存在、值为 null → 仍显示 0，且**预热与重建映射都无效**（与 RC2 同型）。此路径需要真机确认。
8. **建议下一步**：§7-P1；在真机上把"键存在/键缺失"两种状态分别记录（见 §6 观测点）。

### 4.3 #592 —— Intel B580 使用率不显示

1. **当前状态**：OPEN，0 评论，创建 2026-09-30。**创建于 3b91b5c 之前。**
2. **问题摘要**：英特尔 B580（Arc 独显）不能显示使用率。正文仅一句，无型号/驱动/版本信息。
3. **上游代码证据**：
   - **匹配规则可命中**：LHM `IntelDiscreteGpu` 的 Load 传感器名为 **`GPU Core`**（`_loadGlobalActivity`）、`GPU Render/Compute`、`GPU Media`；`SensorMatcher.cs:74` `Load && Has(name,"core")` → **`GPU.Load` 命中**。
   - **值可能恒为 null**：其值由 `UpdateUtilizationFromActivityCounter(_telemetry.globalActivityCounter, …)` 写入；`UpdateTelemetry()` 失败或 `globalActivityCounter.bSupported == false` ⇒ `sensor.Value = null` ⇒ 应用显示 0（`MetricUtils.cs:107`）。
   - **更严重的分支**：LHM `IntelGpuGroup.cs` 中 `IntelGcl.Initialize()` 失败 ⇒ **不创建任何 `IntelDiscreteGpu`** ⇒ B580 **根本不在硬件树** ⇒ 应用侧只能看到核显（与 #450 评论"不手动改一下就是检测核显"高度吻合）。
4. **fork 对照**：匹配规则与上游等价（`SensorMatcher.cs:169`）；fork 额外支持"核显+独显并存"的多卡监控，可在 GCL 失败时至少仍显示核显数据——**这是 fork 相对上游的实质优势**（上游单卡模型下 GCL 失败即该卡彻底消失）。
5. **复现条件与验证步骤**：见 §6 组合 B/D。需 B580 + Intel 驱动/IGCL 正常工作的环境 → **未复现**。**关键待验证项**：应用"硬件详情"里是否存在名为 `GPU Core` 的 Load 传感器，以及其值是否为空。
6. **结论**：**上游 = 部分解决**（键可建；GCL/遥测失败时仍显示 0，且上游无该卡的降级显示）；**fork = 部分解决**（同上游 + 多卡降级能力）。**不能判定为"已完全修复"**——根因 RC2 未被应用层消解。
7. **风险**：RC2 与 RC1 症状完全相同（都显示 0），若真机验证时只看界面数字而**不区分"键是否存在"**，会把 RC2 误判为 RC1 已修复，从而错误地认为"预热/重建映射"是有效修复。
8. **建议下一步**：§7-P1（先加"键存在/值缺失"的区分与诊断日志），再在 B580 真机上确认 RC2。

### 4.4 #569 —— 无法读取集成显卡 GPU 使用率（i3-4130）

1. **当前状态**：OPEN，1 评论（oliverance，2026-09-16："同问题，只支持独显数据，望后续能支持核显数据，甚至是多显卡数据"），创建 2026-09-05。**创建于 3b91b5c 之前。**
2. **问题摘要**：i3-4130（HD 4000 核显），无法读取核显 GPU 使用率。
3. **上游代码证据**：同 #603 的建图预热修复（`SensorMap.cs:141-143`）。
4. **fork 对照**：
   - 建表期预热：`GpuWarmup` + `HardwareMonitor.cs:920`；
   - **多卡能力直接回应评论诉求**：`Settings.MonitoredGpuCount`（`src/Core/Settings.cs:291`，默认 1）、`SensorMap.SelectMonitoredGpus`（`SensorMap.cs:126-146`）、`GpuKey`/`GpuRegistry`/`GpuItemSync`/`GpuGroupPolicy`、`src/UI/Settings/SystemHardwarPage.cs:410-444,503-514`（"监控显卡数量"下拉，按实际检测卡数夹紧）；
   - `tests/LiteMonitor.Tests/GpuKeyTests.cs`、`GpuItemSyncTests.cs`、`GpuGroupTests.cs` 提供回归覆盖。
5. **复现条件与验证步骤**：见 §6 组合 A/B/C。需 i3-4130 或任一 Intel 核显 → **未复现**。
6. **结论**：**上游 = 已解决**（核显负载键）；**fork = 已解决且超出**（额外交付评论要求的"多显卡数据"）。
7. **风险**：fork 多卡模型下，"GPU.{n}.Load" 的序号对应 `MonitoredGpuCount` 集合内下标；**用户改数量会改变序号语义**（`GpuItemSync.Ensure` 负责迁移，`HardwareValueProvider.cs:394-412` 负责作废旧值）。若迁移有遗漏，会出现"序号 0 指向另一块卡"的错位显示——这正是 M3 类现象的 fork 侧风险点。
8. **建议下一步**：在双卡（核显+独显）真机上验收"监控显卡数量=1/2"的序号稳定性与历史数据连续性。

### 4.5 #521 —— U5 255 核显不能正确显示 GPU 使用率（频率正常，占用率恒 0）

1. **当前状态**：OPEN，1 评论（piqiuni，2026-07-15："同样问题，但是打开系统硬件详情后又正常显示"），创建 2026-07-15。**创建于 3b91b5c 之前。**
2. **问题摘要**：Win11 25H2，U5 255；**GPU 频率能正常显示，GPU 使用率一直显示 0**。
3. **上游代码证据**：
   - **频率正常 / 占用率为 0 的结构性原因**：GPU **频率/功耗**走 `ComponentProcessor`（直接遍历 `hw.Sensors` 按名匹配，不经映射表），而 GPU **占用率**走 `SensorMap` 映射表（`SensorMatcher.cs:74`）。**两条通道不同 ⇒ 频率可用并不能证明映射表健康**。
   - 建图期预热修复：`SensorMap.cs:141-143,326`。
   - "打开系统硬件详情后正常显示"与"切换首选显卡后正常显示"**同源**：二者都会导致一次映射重建（硬件详情页触发重扫/重建），而此时硬件对象已被常规刷新循环 `hw.Update()` 过 ⇒ 懒激活传感器已注册 ⇒ 键补上 ⇒ 恢复正常。这与 `SensorMap.cs:47-56`（`EnsureFresh` 10 分钟兜底）+ `HardwareValueProvider.cs:196-211`（切换首选卡触发重建）完全吻合。
4. **fork 对照**：同 #603/#569。fork 额外优势：`ComponentProcessor.cs:155-182` 按**序号**取对应卡的频率/功耗，多卡下不会串卡。
5. **复现条件与验证步骤**：见 §6 组合 A/B/E。需 U5 255 核显 → **未复现**。
6. **结论**：**上游 = 已解决**；**fork = 已解决**。该 issue 同时提供了 **RC1 最清晰的判别指纹**（频率正常 + 占用率 0 ⇒ 映射表缺键，而非驱动数据缺失）。
7. **风险**：低。剩余风险仅为"开机竞态导致核显硬件对象都没建出来"，需 `ReloadComputerSafe` 自愈。
8. **建议下一步**：§6 观测点 3（频率有值 + 占用率为 0 ⇒ 判定为"键缺失"，可与 RC2 的"键存在但值空"区分）。

### 4.6 #450 —— intel b580 显卡不显示占用多少，必须手动切换首选显卡

1. **当前状态**：OPEN，3 评论，创建 2026-05-19，最后更新 2026-06-06。**创建于 3b91b5c 之前。**
   - 评论 1（azhuge233，2026-05-20）："每次启动都需要手动切换一次首选显卡才能正常显示 GPU 相关信息；显卡：Intel 核显 Iris Plus Graphic 655；PawnIO 版本 2.2.0"。
   - 评论 2（vcxe，2026-05-20）："是的"。
   - 评论 3（Paroyo，2026-06-06）："+1，我是 B580 也是一样，不手动改一下就是检测核显，而且有时候任务栏的显示会消失"。
2. **问题摘要**：Intel B580 独显不显示占用率；**必须手动切换一次首选显卡**才正常；有时任务栏显示消失。
3. **上游代码证据**：
   - "必须手动切换"的机制解释（**代码级确认**）：旧版上游 `SensorMap.Rebuild` 内建图期不 `Update()` 显卡 ⇒ 核显 Load 传感器未注册 ⇒ 无 `GPU.Load` 键 ⇒ 面板 0；用户切换首选卡触发 `HardwareValueProvider.cs:196-211` 的 `Rebuild`，此时硬件已被刷新循环 `Update()` 过 ⇒ 键补上 ⇒ 恢复。
   - "不手动改就是检测核显"：LHM `IntelGpuGroup.cs` 中 `IntelGcl.Initialize()` 失败 ⇒ **不创建 `IntelDiscreteGpu`** ⇒ B580 不在硬件树 ⇒ 只剩核显。**与 RC2 强相关。**
   - "任务栏的显示会消失"：属于 UI/渲染路径，本任务单（TASK-2 传感器数据）不覆盖；建议转交 TASK-3/TASK-4 复核。
   - 3b91b5c 的修复：`SensorMap.cs:141-143`（建图期 `Update()`）+ `HardwareMonitor.cs:587-622`（运行时自愈）。
4. **fork 对照**：
   - fork 的 `GpuWarmup` 在 init/reload 时预热 ⇒ 首屏即有键，**不需要**用户手动切换；
   - fork 的 `SensorMap.SelectMonitoredGpus`（`SensorMap.cs:126-146`）在 `MonitoredGpuCount==1` 时**自动模式只监控 1 张卡**（优先级最高者）。**若该卡恰是 GCL 失效的 Arc 独显（键存在、值恒 null），则显示 0，且用户切到核显反而会"变好"**——这与 #450 的"手动切换才正常"体验一致，也解释了为何用户会认为"必须切换"。
   - fork **缺** `RepairGpuSensorMap`；fork 的自动模式也**不**像上游那样刷新"承载 `GPU.*` 键的所有硬件"（上游 `HardwareMonitor.cs:584`）。
5. **复现条件与验证步骤**：见 §6 组合 B/C/D。需 B580（+ IGCL 正常）+ Iris Plus 655 混合机型 → **未复现**。
6. **结论**：**上游 = 已解决**（RC1 部分完全修复；RC2 部分未消解）；**fork = 部分解决**（RC1 已覆盖；RC2 未消解；且缺运行时自愈与"刷新所有承载 GPU.* 的硬件"）。
7. **风险**：
   - fork 的自动模式在 `MonitoredGpuCount==1` 时只看 1 张卡 ⇒ **多卡机型上，用户若期望"同时看核显+独显"必须显式把数量调成 2**，否则会得到"只有独显数据"（正是 #569 评论抱怨的"只支持独显数据"）。
   - "任务栏显示消失"未在本任务单定位。
8. **建议下一步**：
   - §7-P1：移植 `RepairGpuSensorMap`；
   - §7-P2：复核 fork 自动模式在多卡机型上的默认选卡语义（是否应在 `MonitoredGpuCount==1` 且存在"键缺失/值为空"的卡时自动降级到有值的卡）；
   - "任务栏消失"转交 TASK-3/TASK-4。

### 4.7 #434 —— 不显示 gpu 占用率

1. **当前状态**：OPEN，**7 评论**（本组最多），创建 2026-05-15，最后更新 2026-06-07。**创建于 3b91b5c 之前。**
   - 作者报告：RTX 5070 笔记本，Win11 24H2，软件 1.3.5；**1.3.5 之前 GPU 占用率就不显示**；1.3.5 后电池电量也不显示。
   - 上游作者 Diorser（2026-05-15）："电池问题已经修复了，更新到 1.3.6。然后那个 GPU 占有率的话，你查看一下硬件详情，看传感器有没有数据。如果没有数据的话，可能就是等硬件核心库更新了"。
   - 用户贴图（2026-05-16）→ 作者："你这个显卡名称叫什么？目前看**是可以读到传感器数据的，但是没有显示出来，可能是没有匹配上**"（2026-05-16）。
   - vcxe（2026-05-20）："我的显卡是 intel b580 也是同样的问题 必须要切换下首选显卡才行"。
   - zelotoj（2026-06-07）："Intel ARC B570 也是这样，**硬件详情里面能看到 GPU Load 数字不断的跳动，但是界面上 GPU 负载一直为 0.0%**，并且把'自动'切换成 B570 也不起作用"。
2. **问题摘要**：GPU 占用率不显示（多型号：RTX 5070 / B580 / B570）。**"硬件详情里 GPU Load 在跳动、界面恒 0.0%" 是本组最有价值的一句证词**——它证明**传感器对象存在且有值**，但**映射表里没有对应的键** ⇒ 直接指向 RC1（键缺失），而非 RC2（值为空）。
3. **上游代码证据**：
   - `SensorMatcher.cs:74` 对 RTX 5070（NVIDIA）的 Load 传感器名能命中（NVIDIA 的 Load 传感器在 LHM 中名为 `GPU Core`，含 "core"）⇒ **RTX 5070 的 0% 同样是"键缺失"而非"匹配不上"**；
   - B570/B580 同 §4.3/§4.6（键可建，值依赖 GCL）；
   - 3b91b5c 修复：`SensorMap.cs:141-143,326` + `HardwareMonitor.cs:587-622`。
   - **对上游作者"可能是没有匹配上"这一猜测的核实结论**：对 RTX 5070/B570/B580 而言，**匹配规则本身能命中**（NVIDIA/AMD/Intel 独显的 Load 传感器都含 "core"，核显是 "D3D 3D"）；真实成因是**建图时机早于传感器注册**。上游作者的猜测方向（"能读到但没显示"）正确，但归因（"没匹配上"）不准确。
   - **注意 zelotoj 的补充**："把'自动'切换成 B570 也不起作用" —— 说明对 **Intel 独显**，单纯切换映射**无法**恢复（因为其值来自 GCL 遥测而非注册时机）⇒ **再次佐证 RC2 的存在**。
4. **fork 对照**：同 §4.3 / §4.6。
5. **复现条件与验证步骤**：见 §6 组合 B/D（并区分"键缺失"与"值为空"）。
6. **结论**：
   - **上游 = 部分解决**：RC1（键缺失）已修复；RC2（Intel 独显 GCL）未消解。
   - **fork = 部分解决**：同上游；另有多卡降级优势。
   - 该 issue 的**电池子问题**已由上游 1.3.6 修复，不属本任务单范围。
7. **风险**：把 RC1 与 RC2 混为一谈会导致"修复无效"的误判（预热/重建映射对 RC2 无效）。**必须在验证时区分键存在性**。
8. **建议下一步**：§7-P1（区分键/值状态 + 诊断日志）→ 这是**所有 GPU 占用率类 issue 能否被真正关闭的前置条件**。

### 4.8 #497 —— amd 9070gre，界面 GPU 温度频率功耗没数据，只有 GPU 使用率和显存的数据

1. **当前状态**：OPEN，2 评论，创建 2026-06-20，最后更新 2026-07-01。**创建于 3b91b5c 之前。**
   - 评论 1（QM32768，2026-06-30）："蹲"；
   - 评论 2（RAINYYYYYYYYYYYY，2026-07-01）："好了，我之前用官方驱动卸载不够干净还是什么的，**用 DDU 把 N,I,A 的驱动卸载流程都过一遍，再重装驱动（勾选恢复出厂设置）就行了**。这个过程让我识别不出显存厂商的问题也解决了。"
2. **问题摘要**：AMD 9070 GRE，驱动 26.3.1。**硬件详情里**显卡传感器没有 GPU 温度/频率/功耗；软件界面也一直显示 0；使用率与显存有数据。
3. **上游代码证据**：
   - LHM `AmdGpu` 的传感器**在构造函数中无条件创建**（本次 IL 解码确认）：
     - Temperature：`GPU Core`、`GPU Hot Spot`、`GPU SoC`、`GPU VR VDDC/MVDD/SoC`、`GPU Liquid`、`GPU PLX`
     - Clock：`GPU Core`、`GPU Memory`、`GPU SoC`
     - Power：`GPU Package`、`GPU PPT`、`GPU SoC`
     - Load：`GPU Core`、`GPU Memory`、`GPU SoC`
     - SmallData：`GPU Memory Used/Free/Total`、`D3D Dedicated/Shared Memory *`
   - 应用匹配规则**全部命中**：`SensorMatcher.cs:169`（`GPU Core`→`GPU.Load`）、`:170`（`GPU Core`/`GPU Hot Spot`/`GPU SoC`→`GPU.Temp`）；频率/功耗走 `ComponentProcessor.cs:171,182` 直读 `hw.Sensors`（"core"/"package"/"ppt"/"board"/"gpu"）。
   - ⇒ **匹配层无缺陷**；**不存在 NVIDIA-only 逻辑误伤 AMD**（应用全程不使用 NVML，NVML 只在 LHM 内部的 `NvidiaGpu` 中使用）。
   - ⇒ 用户"硬件详情里也没有温度/频率/功耗" ⇒ **LHM 的 AMD 传感器对象本身没有值**（ADL/OD5/OD6 通路未提供数据），即 **M2（键存在、值为 null）**。
4. **fork 对照**：匹配规则与上游等价（`SensorMatcher.cs:169-170`、`ComponentProcessor.cs:171,182`）；**无 NVML 依赖**；fork 额外提供 `GPU.VRAM.D3D.*` 独立键与 `GpuVramSource` 串卡检测，与本 issue 无关但可改善显存口径。
5. **复现条件与验证步骤**：见 §6 组合 F（AMD 独显机）。需 9070 GRE + 驱动 26.3.1 + 可对比的干净安装 → **未复现**。
6. **结论**：**上游 = 非代码缺陷**（驱动/环境问题，用户已用 DDU 自行解决）；**fork = 非代码缺陷**（同上）。
   **但存在一个可改进点（应用侧）**：当前应用把 M2（传感器不受支持）渲染成 0，与 RC1 的 0 **在界面上不可区分**（§2.1）。这会让 #497 这类"驱动不支持"的报告无法被用户自行判别，只能来提 issue。
7. **风险**：低（无代码缺陷），但**诊断体验差**：建议在硬件详情/日志中区分"传感器不存在"与"传感器值为空"。
8. **建议下一步**：§7-P3（区分"无该传感器"与"该传感器无值"并给出 N/A 展示）。**不建议**为 #497 改动取数逻辑。

### 4.9 #499 —— 为什么任务管理器独显占用为 0，但软件一直显示显存占用

1. **当前状态**：OPEN，0 评论，创建 2026-06-21。**创建于 3b91b5c 之前。**
2. **问题摘要**：两张截图（未判读，见 §7「未能验证」）。作者观察到：任务管理器里"独显占用"为 0，而软件一直显示显存占用。
3. **上游代码证据**：
   - 应用显示的"显存占用"是 **百分比**：`HardwareValueProvider.cs:393-406` `result = used / total * 100f`；
   - 任务管理器的"独显占用"是 **GPU 引擎利用率（3D/Copy/Video 等）百分比**；
   - ⇒ **二者是不同物理量**：独显 0% 利用率时，显存仍可能有驱动/桌面合成常驻占用（几百 MB / 8 GB ≈ 数个百分点），**因此"任务管理器 0%"与"软件显存占用非 0"完全可以同时成立**；
   - 但存在**第二条可能路径**：若用户在设置里把首选显卡选为**核显**，则 `SensorMatcher.cs:88-97`（`preferShared`）取的是 **`D3D Shared Memory Used`**——**共享显存天然恒非零**（UMA 窗口常驻），于是"软件一直显示显存占用"更加成立；
   - 第三条路径（**M3**）：`HardwareValueProvider.cs:502-514` 的通用兜底在值为空时回退 `_lastValidMap` 的历史有效值 ⇒ **换卡后可能显示上一块卡的显存占用**。
4. **fork 对照**：
   - `HardwareValueProvider.cs:958-1023` `GetGpuVramPercent`：`VRAM.Used/Total`（厂商通道）与 `VRAM.D3D.Used/Total`（D3D 通道）**双通道**，`GpuVramSource.IsVendorChannelForeign` 判定厂商通道是否在报告别的卡，串卡则改用 D3D 通道，分母用 `GpuVramCapacity` 的注册表真实容量 ⇒ **fork 显著降低"取到另一块卡的显存"的可能**；
   - `HardwareValueProvider.cs:394-412` `MarkStaleGpuValues()`：换卡后**作废**旧卡的最后有效值 ⇒ **fork 消除了 M3**（上游无此处理）；
   - fork 仍**未**改变显示口径（利用率% vs 显存已用%），也**未**为"显存无数据"提供 N/A 态。
5. **复现条件与验证步骤**：见 §6 组合 D/E（混合显卡机）。需具体机型 + 截图判读 → **信息不足**。
6. **结论**：**上游 = 显示语义差异（非缺陷），但存在 M3 旧值风险；fork = 语义差异仍在，但已消除 M3 并降低串卡风险。**
   **不能判定为"已解决"**——因为用户抱怨的**观感**（两个数字对不上）源于口径差异，代码侧无法让二者相等；能做的是让软件侧口径更明确（例如区分"显存占用%"与"GPU 利用率%"，或在硬件详情中标明取数来源卡）。
7. **风险**：
   - fork 的 `MarkStaleGpuValues` 只在 `gpuChanged || gpuCountChanged` 时触发（`HardwareValueProvider.cs:394`）；若用户**未改设置**而系统层面发生了显卡热插拔/驱动重载，旧的 `_lastValidMap` 仍可能被兜底回退 → M3 残留窗口。
   - `GpuVramSource.Select`（`GpuVramSource.cs:75-98`）在 D3D 缺失时**宁可无值**（返回 null）——正确，避免了"用别的卡的 used 除本卡的 total"这类更离谱的数值。
8. **建议下一步**：
   - §7-P3：在硬件详情/日志中显示"当前显存取数来源（哪块卡 / 厂商通道还是 D3D 通道）"；
   - §7-P4：补热插拔/驱动重载路径的旧值作废（把 `MarkStaleGpuValues` 的触发条件从"设置变更"扩展到"硬件树变化"）；
   - **需 issue 补充截图与机型**（见 §7）。

---

## 5. 根因聚类

### 5.1 RC1 —— 建图时机早于传感器懒激活注册（**键缺失**）

**代码证据**
- LHM 侧：`IntelIntegratedGpu` 的 Load 传感器**不在构造函数创建**，只在 `Update()` 中经 `ActivateSensor` 注册（本次 IL/源码双证）。
- 上游旧版：`SensorMap.Rebuild` 内 `//hw.Update();` 被注释 ⇒ 建表时核显无 `GPU.Load` 键。
- 上游当前（3b91b5c）：`src/System/HardwareServices/SensorMap.cs:126,141-143,326` → `TryUpdateGpuForMapping` 在建表前 `hw.Update()`；`src/System/HardwareMonitor.cs:587-622` 追加运行时自愈。
- fork：`src/System/HardwareServices/GpuWarmup.cs:48-63` + `src/System/HardwareMonitor.cs:920,1204`（建表前预热）；`src/System/HardwareServices/SensorMap.cs:191` 表内仍不预热。

**可解释的 issue**：#603、#596、#521、#569、#450、#434（RTX 5070 / B580 / B570 三个评论者中至少部分）、#592（键层面）。

**"必须手动切换首选显卡"的统一解释（代码级确认）**：切换 → `gpuChanged` → `Rebuild`（上游 `HardwareValueProvider.cs:196-211`；fork `:394-412`）→ 此时硬件对象已被常规刷新循环 `Update()` 过 ⇒ 懒激活传感器已注册 ⇒ 键补上 ⇒ 面板恢复正常。"打开系统硬件详情后正常显示"（#521 评论）同理。**上游 3b91b5c 把这个"用户手动 workaround"变成了开机即生效。**

### 5.2 RC2 —— Intel 独显（Arc）GCL 遥测不可用（**键存在但值为 null**）

**代码证据**
- LHM `IntelGpuGroup.cs`：`IntelGcl.Initialize()` 失败 ⇒ **不创建 `IntelDiscreteGpu`** ⇒ Arc 独显**彻底不在硬件树**（只剩核显 → 与 #450 评论"不手动改就是检测核显"吻合）。
- LHM `IntelDiscreteGpu.cs`：Load 传感器名 **`GPU Core`**（应用可匹配）；值由 `UpdateUtilizationFromActivityCounter(_telemetry.globalActivityCounter, …)` 写入，`bSupported == false` 或 `UpdateTelemetry()` 失败 ⇒ `Value = null` ⇒ `MetricUtils.cs:107` → **0.0%**。
- 应用侧无"值缺失"状态 ⇒ 与 RC1 在界面上不可区分。

**可解释的 issue**：#592、#450、#434（zelotoj 的 B570 案例：切换到 B570 也不起作用 ⇒ 排除 RC1，指向 RC2）、#596（部分）、#603（若该机被 LHM 归为独显则适用）。

**与 RC1 的关键区分**：RC1 对"预热/重建映射"有响应；**RC2 无响应**。因此**任何只按界面数字做的验证都会混淆二者**。

### 5.3 RC3 —— AMD 驱动/ADL 通路不完整（**传感器无值**）

**代码证据**：LHM `AmdGpu` 的温度/频率/功耗传感器**存在且能被应用匹配**（IL 解码 + `SensorMatcher.cs:169-170`）；值依赖 ADL/OD5/OD6（`ADL2_Overdrive5_Temperature`、`ADL2_Overdrive6_CurrentPower`、`IsSensorSupportedByPMLog`）。用户以 DDU 清理驱动后恢复（issue 内评论自证）。

**可解释的 issue**：#497。**非应用代码缺陷**；且**不涉及 NVML**（应用与 AMD 路径均无 NVML 依赖）。

### 5.4 RC4 —— 显示口径差异 + M3 旧值残留

**代码证据**：上游 `HardwareValueProvider.cs:393-406`（显存=used/total%）vs 任务管理器引擎利用率%；`SensorMatcher.cs:88-97`（核显取共享显存）；上游 `HardwareValueProvider.cs:502-514`（`_lastValidMap` 兜底 = M3）；fork `HardwareValueProvider.cs:394-412` `MarkStaleGpuValues` 已消 M3（设置变更路径）、`GpuVramSource.cs`/`GpuVramCapacity.cs` 已降低串卡。

**可解释的 issue**：#499。

### 5.5 聚类总表

| 根因 | issue 归属 | 上游当前状态 | fork 当前状态 | 应用层是否可完全消解 |
|---|---|---|---|---|
| **RC1** 键缺失 | #603 #596 #521 #569 #450 #434(部分) #592(键层面) | **已修复** | **已修复**（路径不同）；缺运行时自愈 | 是（已） |
| **RC2** Intel 独显 GCL | #592 #450 #434(B570) #596(部分) | 部分（未消解） | 部分（未消解，但多卡可降级） | **否**——需驱动/遥测支持 |
| **RC3** AMD ADL | #497 | 非缺陷 | 非缺陷 | **否**——需驱动环境 |
| **RC4** 显示口径 | #499 | 语义差异 | 语义差异（M3 已消） | **否**（口径本就不同），但可改善表达 |
| **M2 展示层缺陷**（把"不支持"显示成 0） | 横向影响 #497 #592 #434 | 存在 | 存在（fork 仅在 CPU 电压上修正） | **是**（可引入 N/A 态） |

---

## 6. 可执行验证组合与预期观测点

> **前置条件**：一台同时具备 **Intel 核显 + Intel Arc 独显** 的混合笔记本（最能同时覆盖 RC1/RC2），或至少一台 Intel 核显机（覆盖 RC1）与一台 AMD 独显机（覆盖 RC3）。
> **必装的诊断手段**：在应用"硬件详情"页同时记录 **① 传感器原始名**、**② 传感器是否存在**、**③ 传感器值是否为空**。**只看面板数字无法区分 RC1/RC2。**

### 6.1 观测点定义

| 编号 | 观测点 | 判定含义 |
|---|---|---|
| **O1** | 硬件详情里是否存在名为 `D3D 3D`（核显）或 `GPU Core`（独显，Load 型）的传感器 | 存在 ⇒ 键可建；不存在 ⇒ 键缺失 |
| **O2** | 该传感器的值是否为空/为 null | 空 ⇒ M2（RC2/RC3）；非空 ⇒ 数据通路正常 |
| **O3** | GPU **频率**是否有值 | 有值 + 占用率为 0 ⇒ **RC1（键缺失）** 的确证指纹（频率走直连通道） |
| **O4** | 硬件树中是否存在该独显 | 不存在 ⇒ GCL 初始化失败（RC2 分支 1） |
| **O5** | 面板显示的是"显存占用%"还是"GPU 利用率%" | 与任务管理器口径不同 ⇒ RC4 |
| **O6** | 换卡后旧值是否被作废 | fork 应作废；上游会残留（M3） |

### 6.2 验证矩阵

| 组合 | 步骤 | 预期观测 | 预期结论 |
|---|---|---|---|
| **A. 冷启动（自动模式）** | 1) 确认"首选显卡 = 自动"、"监控显卡数量 = 1"<br>2) **冷启动**（关机 → 开机，不进设置页）<br>3) 立即记录 O1/O2/O3/O4 | 上游 3b91b5c：`D3D 3D` 应在**首屏建表时即已注册**（`SensorMap.cs:141-143`）⇒ 占用率直接有值<br>fork：同理（`GpuWarmup` → `HardwareMonitor.cs:920`）⇒ 占用率直接有值<br>**若 O3 有值而 O1 为空 ⇒ RC1 未修复** | RC1 是否已消 |
| **B. 冷启动（手动指定核显）** | 在 A 之后设置"首选显卡 = Intel 核显"，**冷启动**再次观察 | 核显被唯一映射 ⇒ 占用率应来自 `D3D 3D`；若为 0，检查 O1/O2 | 手动选择路径 |
| **C. 自动 ↔ 手动 往返** | 1) 记录当前占用率<br>2) 首选显卡：自动 → 指定某卡 → 自动<br>3) 每步观察 O1/O2 与面板 | 上游/均应**立即**变化（`HardwareValueProvider` 检测 `gpuChanged` → `Rebuild` → `PreCache`）<br>fork 额外作废旧值（`MarkStaleGpuValues`）<br>**若往返后数值与切换前完全相同 ⇒ 疑似旧值残留（M3）** | M3 / 切换刷新机制 |
| **D. 独显优先 vs 核显优先（多卡）** | 若机器有核显+Arc 独显：<br>1) 首选显卡 = Arc 独显 → 记录 O1/O2/O4 与占用率<br>2) 首选显卡 = 核显 → 记录同上<br>3) 监控显卡数量 = 2 → 观察两组独立指标 | Arc 独显：键应为 `GPU Core`（**O1 存在**）；若 O2 为空 ⇒ **RC2**（GCL 遥测不支持），预热/重建映射**无效**<br>核显：键应为 `D3D 3D`<br>数量=2 时 fork 应出现 `GPU.0.*` / `GPU.1.*` 两组 | RC2 判定 + 多卡完整性 |
| **E. 睡眠唤醒 / 显卡热切换** | 1) 睡眠 5 分钟 → 唤醒<br>2) 切换"首选显卡"后立即睡眠唤醒<br>3) 观察 O6 与面板数值 | 唤醒后若占用率变 0 且 O1 消失 ⇒ 硬件树在唤醒后重建失败 ⇒ 需 `ReloadComputerSafe`<br>上游有 `RepairGpuSensorMap` 自愈；**fork 无** ⇒ 观察是否出现"面板持续 0 超过 10 分钟"（fork 的 10 分钟兜底 `EnsureFresh`→`Rebuild` 应能补键，但**只补键不保证值**） | 唤醒鲁棒性 / fork 自愈缺口 |
| **F. AMD 独显（对照）** | AMD 机上记录 O1/O2：是否存在 `GPU Core`(Temp)、`GPU Hot Spot`、`GPU Package`、`GPU PPT` 及其值 | 传感器**存在**（LHM 无条件创建）但**值为空** ⇒ RC3（ADL 通路），**不是匹配缺陷**<br>以 DDU 清理驱动后应恢复 ⇒ 佐证结论 | RC3 判定 |
| **G. 冷启动 10 分钟观察（无操作）** | 冷启动后**完全不碰设置**，每 2 分钟记录一次占用率，持续 15 分钟 | 若占用率在 T+10 附近**自行从 0 变正常** ⇒ 说明命中的是"键缺失 + 10 分钟兜底重建"（RC1 未在首屏修复）；<br>若始终为 0 ⇒ 键缺失且兜底无效（需查 `EnsureFresh` 是否被跳过） | 判定首屏修复是否真正生效 |

### 6.3 建议的取证顺序（成本最低、区分度最高）

1. **先做 G**（零操作冷启动 15 分钟）→ 判定 RC1 是否已在首屏修复。
2. **再做 A/B**（记录 O1/O2/O3/O4）→ 把 RC1 与 RC2 分开。
3. **再做 C/E**（切换、唤醒）→ 判定 fork 相对上游的自愈缺口是否实际发生。
4. **F** 可延后（结论已由 issue 评论佐证）。

---

## 7. 未能验证项及原因 / 假设 / 优先级

### 7.1 未能验证项

| # | 项目 | 原因 | 需要什么 |
|---|---|---|---|
| U1 | 全部 9 个 issue 的**行为级复现** | 本机为 **AMD Ryzen 7 5800X，无任何 Intel 显卡**；无 AMD 独显；无混合显卡笔记本 | ① Intel 核显机（覆盖 RC1：#603/#596/#521/#569）② Intel Arc B580/B570 机（覆盖 RC2：#592/#450/#434）③ AMD 独显机（覆盖 RC3：#497）④ 混合机（覆盖 RC4：#499） |
| U2 | **issue 截图内容判读** | 截图托管在 GitHub user-attachment，本次**未下载、未做图像判读**；#497/#499/#603/#450/#596/#592/#521/#434 的正文均依赖截图补充信息 | 下载 9 个 issue 的全部图片并做 OCR/视觉判读（尤其 #499 的两张图：一张任务管理器、一张应用面板） |
| U3 | #499 的**具体机型与 GPU 选择状态** | issue 正文未给出型号，也未说明应用当时监控的是核显还是独显 | 用户补充机型 + 应用设置截图（"首选显卡"下拉的当前值） |
| U4 | **Intel GCL 初始化是否成功** | 需 B580 真机 + LHM 的 `IntelGpuGroup` 报告（`GetReport()` 含 "Intel GCL Initialized: True/False"） | B580 真机 + LHM 报告导出 |
| U5 | #434 的**电池子问题** | 已由上游 1.3.6 修复，属其它任务单范围 | — |
| U6 | #450 评论中的**"任务栏显示会消失"** | 属 UI/渲染路径，不在 TASK-2（传感器数据）范围 | 转交 TASK-3/TASK-4 |
| U7 | **fork 与上游的实际运行对比** | 本次为纯静态取证，按约束未构建、未跑全量测试、未运行应用 | 若需行为对比，须在有硬件的机器上构建双方版本 |

### 7.2 假设与澄清（与任务单描述的偏差）

1. **计划文档文件名与实际不符**：任务单提到 `docs/superpowers/plans/2026-07-20-gpu-multi-card.md`（月份笔误）等，仓库实际为 **`2026-10-20-gpu-multi-card.md`、`2026-10-20-igpu-load-fix.md`、`2026-10-22-gpu-nonperformance-completion-plan.md`、`2026-10-22-gpu-legacy-runtime-transition-plan.md`、`2026-10-20-gpu-vram-d3d-fix.md`**（月份为 10 月）。
2. **测试/探针工程名与实际不符**：任务单提到 `tests/LiteMonitor.GpuNonperformance.Tests` 与 `tools/GpuHostLifecycleProbe`、`GpuEditorPreviewProbe`；仓库中**不存在**这些路径。GPU 相关的实际回归测试是 **`tests/LiteMonitor.Tests/GpuWarmupTests.cs`**、`GpuKeyTests.cs`、`GpuItemSyncTests.cs`、`GpuGroupTests.cs`、`GpuVramTests.cs`、`HardwareRulesGpuNameTests.cs`、`MetricUtilsGpuKeyTests.cs`；`tests/LiteMonitor.Tests/ReviewGpuRecoveryRegressionTests.cs` 属**渲染/呈现宿主**恢复，**与传感器数据无关**（勿据此认为 GPU 取数已覆盖）。
3. **`src/Rendering` 下的 GPU 文件不承载传感器数据**：`src/Rendering/Gpu/Device/GpuDeviceLifecycle.cs`、`GpuResourceLedger.cs`、`src/Rendering/Presentation/GpuSceneRenderer.cs` 等属**渲染/窗口呈现**路径；**传感器取数的真实落点是 `src/System/HardwareServices/`（SensorMap / SensorMatcher / HardwareValueProvider / GpuWarmup / GpuVramSource / GpuVramCapacity）与 `src/Core/`（GpuKey / GpuRegistry / GpuItemSync / GpuGroupPolicy / Settings）**。任务单第 2 问"找出 fork 中 src/Rendering 或 src/Core 下的 GPU host/provider 实现文件"→ 结论：**src/Rendering 下没有 GPU host/provider；provider 语义落在 src/Core + src/System/HardwareServices**。
4. **fork 的 `SensorMap.cs:191` 仍保留 `//hw.Update();`**：这是**有意为之**（`HardwareMonitor.cs:1020-1022` 注释说明：预热必须在 `_lock` 内、`Rebuild` 在 reload 路径上是锁外执行，塞进去会与刷新循环竞争同一硬件对象）。因此**不能**按"上游把 `hw.Update()` 写进了 `Rebuild`，fork 没写"就判定 fork 缺陷——两者的等价性由"外部预热"保证。

### 7.3 建议优先级（按对 fork 的实际影响排序）

| 优先级 | 事项 | 依据 | 影响面 | 建议动作 |
|---|---|---|---|---|
| **P1** | **移植上游 `RepairGpuSensorMap` 等价的运行时自愈**（`HardwareMonitor.cs:587-622`）到 fork | 上游有、fork 无（fork 侧 `Repair` 命中 0 次）；fork 现有自愈只有"10 分钟兜底 `EnsureFresh`"，且**只补键不保证值** | #603/#596/#521/#569/#450/#434 全部依赖"键能及时补上" | 在 fork 的 `HardwareMonitor` 更新循环内，复用上游判定（已启用的 `GPU.Load/Temp/Fan/VRAM.*` 键缺失 → 重建映射 + 重取缓存），并把 fork 已有的 `GpuWarmup` 作为重建的前置预热 |
| **P2** | **区分"键缺失"与"值缺失"并落日志/N/A 态** | §2.1：上游与 fork 都把二者渲染成 0（`MetricUtils.cs:107` / 上游 `:107`）；fork 已在 CPU 电压上建立了 `"--"` 范式（`MetricUtils.cs:114-121`），可直接推广 | 决定 #497/#592/#434 能否被用户自行判别；也是**验证 RC1/RC2 的前置** | ① GPU 指标缺失时显示 `"--"` 而非 `0`；② 硬件详情标注"来源卡 / 通道（厂商 vs D3D）/ 原始传感器名"；③ 日志区分"无此传感器"与"有传感器但值为空" |
| **P3** | **复核 fork 自动模式在混合显卡机上的默认选卡语义** | fork `SensorMap.cs:138-143`：`MonitoredGpuCount==1` 且自动模式时只选优先级最高的 1 张卡；若该卡恰是 GCL 失效的 Arc 独显 ⇒ 键存在、值恒 null ⇒ 恒 0，而用户切到核显反而"变好" ⇒ 正是 #450/#596 的体感来源 | 直接影响 #450/#596/#569（"只支持独显数据"） | 明确产品口径：自动模式是否应在"首选卡无有效值"时降级/并列展示；并给出面向用户的说明文案 |
| **P4** | **扩展旧值作废的触发条件到硬件树变化** | fork `HardwareValueProvider.cs:394` 仅在 `gpuChanged \|\| gpuCountChanged` 时 `MarkStaleGpuValues()`；上游无此机制但有 `_lastValidMap` 兜底（`:502-514`）⇒ 换卡/驱动重载后可能残留旧值（M3） | #499 | 把"硬件树变化/驱动重载/唤醒后重建"纳入作废触发 |
| **P5** | **上游 `SensorMatcher.cs:109` 的注释/代码不一致**（`GPU Memory Controller` 未被排除） | 上游注释声称排除但代码没有；**fork 已修正**（`SensorMatcher.cs:218`） | 仅影响上游；fork 无需动作 | 建议向上游反馈（可选） |
| **P6** | 采集 issue 截图做视觉判读（尤其 #499） | U2 | #499 的定性 | 由主控决定是否追加 mcode-tools 图像判读 |
| **P7** | #450 评论"任务栏显示会消失" | U6 | 跨任务单 | 转交 TASK-3/TASK-4 |

---

## 8. 结论摘要

1. **9 个 issue 全部为 OPEN**（本次独立复核，含此前"瞬时失败"的 #596，实为 OPEN + 1 评论）。
2. **所有 9 个 issue 都创建于上游 `3b91b5c`（2026-10-21）之前**；而 `3b91b5c` 恰好包含针对本组报告的核心 GPU 修复（建图期显卡预热 + 映射运行时自愈）。**因此"issue 提交时上游有问题"成立，"上游当前代码仍有问题"不成立**。
3. **fork 已独立解决 RC1（Intel 核显占用率键缺失）**，路径为 `GpuWarmup`（仅对无 Load 传感器的卡补一次 `Update()`）+ `WarmUpGpuSensors()` 在建表前调用，**优于上游的"对所有显卡无条件 Update"**（零额外开销）。fork 额外交付上游没有的**多卡监控**（`GPU.{n}.{Metric}`），正面回应 #569 评论诉求。
4. **fork 相对上游 3b91b5c 存在一处实质缺口**：**缺少 `RepairGpuSensorMap` 等价的运行时自愈**（P1）。
5. **RC2（Intel 独显 GCL 遥测）、RC3（AMD ADL 通路）不是应用代码能修的**，但当前应用把"传感器无值"渲染成 0，使它们与 RC1 在界面上不可区分（P2）。
6. **#499 是显示口径差异**（利用率% vs 显存已用%），非缺陷；fork 已消除"换卡后旧值残留"（M3）并降低"取到另一块卡显存"的风险，但口径本身不会变得与任务管理器一致。
7. **本次为纯静态取证**：无硬件复现（U1）、无截图判读（U2）、未构建未运行（U7）。所有"行为级"结论均标注了代码依据与待验证项。

---

*报告生成：2026-10-25 ｜ 只读取证，未修改 `src/**`、未改动工作树/索引、未提交 ｜ 临时脚本 `G:\TOOL\LiteMonitor-fork\.tmp\verification\lhm-strings.ps1`、`lhm-sensors.ps1`、`lhm-sensors2.ps1` 已删除*
