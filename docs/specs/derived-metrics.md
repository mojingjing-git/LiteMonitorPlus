# 派生指标规格

LiteMonitor++ 的一批监控项并不直接对应某个传感器读数，而是由已有的原始样本按纯规则推导出来。本文描述这些派生指标的定义方式、取值口径、量程上限、阈值与进度条规则、更新时机与缓存策略。

派生指标当前包括：

| 指标键 | 显示名（长 / 短） | 推导来源 |
| --- | --- | --- |
| `SYS.Power` | 总功耗 / `SPWR` | `CPU.Power` + Σ `GPU.{index}.Power` |
| `MEM.Commit.Load` | 内存已提交 / 提交 | Windows 提交信息（`GetPerformanceInfo`） |
| `CPU.VOLTAGE` | CPU 电压 | CPU 电压候选集的选择结果 |

适用对象是在本仓库上扩展监控项、调整口径或排查「面板读数不对 / 读数缺失」问题的工程师。硬件采集节拍、传感器树构建、显存择源、Web 服务协议与渲染路线不属于本文范围；采样层与专项指标数据源的完整口径见 `docs/specs/hardware-sampling.md`。

文中出现的配置键名、指标键名、枚举值、状态名、缺失原因字符串与常量属于接口约定，必须按本文写法保持稳定。

---

## 1. 派生指标的定义方式

### 1.1 三条硬约束

- 派生指标必须由**原始样本 + 纯规则**得出 ⇒ 推导函数不得触发任何新的硬件访问、不得调用 `IHardware.Update()`、不得发起新的系统调用。
- 推导必须发生在**取值层**（`src/System/HardwareServices/HardwareValueProvider.cs` 的 `HardwareValueProvider`）或**纯计算 / 选择层**（`src/Core/MetricUtils.cs` 的 `MetricUtils`、`CommittedMemoryCalculator`、`CpuVoltageResolver`），**不得**散落到界面绘制代码里。
- 同一项指标在面板、任务栏、Web 服务三处必须给出**完全一致**的数值与口径 ⇒ 消费方只允许读取已发布的不可变结果，不得各自去读原始来源。

⇒ 界面刷新频率再高也不会变成硬件访问频率：派生指标的重算频率由采集代次决定，与界面 Tick 解耦。

### 1.2 三种派生形态

| 形态 | 实现位置 | 特征 |
| --- | --- | --- |
| 求和型 | `HardwareValueProvider.GetValue` 的 `SYS.Power` 分支 | 读取若干已存在的分量键，套用纯算术规则 |
| 口径换算型 | `CommittedMemoryCalculator`（`src/System/HardwareServices/CommittedMemoryReader.cs`） | 把原始计数按 Windows 口径换算成字节与比例 |
| 选择型 | `CpuVoltageResolver`（`src/System/HardwareServices/CpuVoltageResolver.cs`） | 从候选集中按可信度规则选出唯一来源，并附来源类型 |

### 1.3 缺失与零的分离

派生指标必须遵守全项目的缺失语义：

- 缺失必须渲染为 `--`（`MetricUtils` 的 `MissingValueText`），**绝不**用 `0` 顶替。
- 合法的 `0` 必须原样保留为 `0` ⇒ 休眠状态下的 0 V 核心轨读数是有效值，不是缺失。
- 缺失读数**不得**附带单位 ⇒ 界面上不得出现 `-- V`、`-- GB` 这类自相矛盾的组合。

---

## 2. 整机总功耗（`SYS.Power`）

### 2.1 求和口径

`SYS.Power` 的求和口径固定为：

```
SYS.Power = CPU.Power + Σ(GPU.{index}.Power, index = 0 .. Settings.MonitoredGpuCount - 1)
```

- `CPU.Power` 取自 CPU 硬件节点上名字含 `package` 或 `cores` 的功耗传感器（`SensorMatcher` 的 CPU 匹配规则），即处理器的封装功耗口径。
- `GPU.{index}.Power` 取自该卡的板级 / 封装功耗传感器（`SensorMatcher.MatchGpu` 与 `ComponentProcessor.GetCompositeValue` 的 `Power` 分支，匹配名字含 `package` / `ppt` / `board` / `core` / `gpu` 的功耗传感器）。
- 求和范围严格等于 `Settings.MonitoredGpuCount` 覆盖的显卡前缀 ⇒ 未纳入监控的显卡**不参与**求和。
- 分量键通过 `GpuKey.Build(index, "Power")` 构造，格式固定为 `GPU.{index}.Power`。

### 2.2 分量缺失的处理

- 分量缺失时**跳过**该分量，不按 0 计入，也不因此让整项缺失。
- 至少一个分量有效 ⇒ `SYS.Power` 有值（有效分量之和）。
- 所有分量都缺失 ⇒ `SYS.Power` 缺失，显示 `--`。

⇒ **部分可用的总和是一个偏小的真实值，不是缺失。** 界面不得把「只有 CPU 分量」的总功耗与「CPU + 双卡」的总功耗混为一谈。

### 2.3 量程上限

`SYS.Power` 与其它功耗类指标的量程上限**互不相同**，必须分别对待：

| 指标 | 熔断上限（超出即判缺失） | 峰值记录上限 | 默认校准量程 |
| --- | --- | --- | --- |
| `CPU.Power` | 600 W | 1000 W | `RecordedMaxCpuPower` = 65 W |
| `GPU.Power` | 1200 W | 1000 W | `RecordedMaxGpuPower` = 100 W |
| `SYS.Power` | 2000 W | 2000 W | `RecordedMaxSysPower` = 800 W |
| `BAT.Power` | 无熔断 | 不参与峰值记录 | 固定量程 100 W |

- 熔断在**取值层**生效：超限读数直接返回缺失（`ComponentProcessor.GetCompositeValue` 对 `CPU.Power` 用 600 W、对 `GPU.Power` 用 1200 W；`HardwareValueProvider` 对 `SYS.Power` 用 2000 W 的 sanity ceiling）。
- 峰值记录闸门在 `SettingsHelper.UpdateMaxRecord` 生效：含 `Power` 且不以 `SYS.` 开头的键在 1000 W 处被拒；以 `SYS.` 开头的键在 2000 W 处被拒。
- `SYS.Power` 允许超过 1000 W ⇒ 高端台式机的整机功耗落在 1000–2000 W 区间时必须照常显示与记峰值，不得按 `CPU.Power` / `GPU.Power` 的量程处理。

### 2.4 峰值记录

- `SYS.Power` 的峰值记录到 `Settings.RecordedMaxSysPower`，供进度条自适应与三色判定使用。
- 只有在拿到有效值时才调用 `UpdateMaxRecord` ⇒ 缺失值不参与峰值记录。
- 超限（大于 2000 W）的读数在更早一步已被熔断成缺失，因而不会刷新峰值。
- 峰值只在**变大**时更新；更新后 30 秒内不重复自动落盘。

### 2.5 重复计入风险

AMD APU（核显与封装在同一硅片上）的封装功耗已经包含核显部分。若该平台的核显同时以 `GPU.{index}.Power` 的形式被采集到，则同一份能量会在求和里出现两次 ⇒ `SYS.Power` 偏高。

- 当前实现**不做** APU 核显去重：缺少可靠判据（无法从传感器层稳定区分「核显功耗」与「独立显卡功耗」）。
- 该风险在文档与界面上必须按「口径已知偏高」处理，不得把求和结果宣称为精确的整机功率。

---

## 3. 已提交内存（`MEM.Commit.Load`）

### 3.1 指标含义

`MEM.Commit.Load` 表示 Windows 的**提交量**（commit charge），与任务管理器「内存 → 已提交」同口径：

- 已提交量由两部分构成：**已提交页**（已提交且驻留在物理内存中的页面）与**已提交交换**（已提交且被换出到页面文件的页面）。
- 提交上限（commit limit）是**物理内存 + 页面文件**共同提供的提交额度，随系统页面文件设置变化。

计算口径（`CommittedMemoryCalculator.Compute`）：

```
CommittedBytes = CommitTotal  × PageSize
LimitBytes     = CommitLimit  × PageSize
LoadPercent    = 100 × CommittedBytes / LimitBytes
```

- 分母**必须**是提交上限，**绝不**使用物理内存总量 ⇒ 提交上限通常大于物理内存，用物理内存当分母会算出远超 100% 的假象。物理内存总量只作为诊断信息保留在快照里，不参与任何比例计算。
- 数据源是 Windows 原生的 `GetPerformanceInfo` / `K32GetPerformanceInfo`（`WindowsCommittedMemorySource`），不依赖性能计数器、不需要管理员权限 ⇒ 关闭「优先使用系统计数器」或计数器尚未初始化时本项依然可用。

### 3.2 与内存占用（`MEM.Load`）的口径差异

| | `MEM.Load` | `MEM.Commit.Load` |
| --- | --- | --- |
| 物理含义 | 物理内存被占用的比例 | 提交额度被占用的比例 |
| 分母 | 物理内存总量（`Used + Available`） | 提交上限（物理内存 + 页面文件的提交额度） |
| 是否含已换出页面 | 否 | 是（已提交交换计入分子） |
| 数据来源 | 内存硬件节点传感器 / Windows 性能计数器 | `GetPerformanceInfo` |
| 与性能计数器的关系 | 受 `Settings.UseWinPerCounters` 影响 | 完全不受影响 |

⇒ **两个百分比的分母不同，绝对值不可直接比较。** 同一时刻 `MEM.Commit.Load` 高于 `MEM.Load` 是正常现象（提交额度包含页面文件），不构成数据错误。

### 3.3 显示口径

- 百分比口径（`Settings.MemoryDisplayMode != 1`，或该项的单位格式显式要求 `%`）：显示提交比例百分数。
- 容量口径（`Settings.MemoryDisplayMode == 1`，默认值）：显示 `提交量 / 提交上限`，单位固定为 `GB`（`MetricUtils.CommittedMemoryCapacityUnit`）。
  - 分子与分母取自**同一个不可变状态对象**（`CommittedMemoryPublisher.CommittedMemoryDisplay`）⇒ 必然同代次、同倍率，不会出现 `76 GB / 98304 MB`。
  - 单位固定为 `GB` 而不随数值自动换算，是为了避免数值与单位在两次独立取值的间隙里落到不同倍率。
- 缺失（快照未就绪 / API 失败 / 提交上限为 0 / 换算溢出 / 结构体大小不符）⇒ 显示 `--` 且**不带单位**，**绝不**回退成物理内存占用率。

### 3.4 阈值标定

- 三色状态：两项内存指标都解析为 `MetricType.Memory` ⇒ 面板与任务栏的颜色状态统一取 `Settings.Thresholds.Load` 的 `Warn` / `Crit`（默认 60 / 85）。
- 但两项的百分比分母不同 ⇒ **同一个阈值数值在两项上的物理含义不同**，阈值标定必须按各自口径独立进行：把提交比例按物理内存占用率的直觉来标定会得到错误的三色分布。
- 告警规则按 `MetricKey` 独立配置（`AlertRule.TriggerThreshold` 与 `AlertRule.RecoveryThreshold`）⇒ `MEM.Load` 与 `MEM.Commit.Load` 是两条互不影响的规则，触发阈值与恢复阈值分别标定。

---

## 4. CPU 电压（`CPU.VOLTAGE`）

### 4.1 首选来源：稳定传感器标识

CPU 电压的**首选**来源是 LHM 的稳定传感器标识（`ISensor.Identifier`），形如 `/amdcpu/0/voltage/0`。

- 配置键：`Settings.PreferredCpuVoltageSensor`（字符串）。
  - 非空 ⇒ 手动模式：按标识精确匹配候选（大小写不敏感），该标识是唯一被接受的来源。
  - 空串 ⇒ 自动模式：标识缺省，按来源可信度自动挑选。
  - 缺省值是空串 ⇒ 未显式配置该项时按自动模式处理，不视为错误。
- 标识不随传感器枚举顺序变化 ⇒ 手动指定的选择在硬件重建后依然可定位。
- 设置页的候选项**只来自已发布的候选快照**；填写该处不会触发额外的硬件扫描。

### 4.2 手动指定的失败行为

手动指定的标识在当前硬件树上不存在时：

- 立即返回缺失，缺失原因 `指定来源不可用 (传感器标识已不存在)`。
- **绝不**改选其它电压候选，也**绝不**沿用该来源消失前的旧电压或旧来源标签。
- 该状态切换后，旧的解析结果立即作废（解析器缓存被清空、已发布状态被清除）。

另两种手动指定的失败：候选被分类为 `Excluded`（原因 `指定来源不是 CPU 核心电压 (如 SoC / GT / DIMM / 输入供电)`）、候选读数无效（原因 `指定来源的读数无效`）。

### 4.3 无稳定标识时的回退（自动模式）

自动模式在没有可用的指定标识时按固定顺序挑选：

1. 来源可信度（`CpuVoltageSourceKind` 升序）：`MeasuredCoreRail` > `MotherboardVcore` > `RequestedVid` > `Ambiguous`。
2. CPU 归属：CPU 硬件节点上的候选优先于主板 / SuperIO 上的候选。
3. 粒度：整条轨（`Rail`）优先于每核心（`PerCore`）。
4. 稳定标识按 `StringComparer.Ordinal` 升序 ⇒ 同优先级候选的选择与枚举顺序无关。

回退过程中遵循的规则：

- `Excluded` 候选（SoC / GT / SA / AUX / DIMM / 输入供电 / +5V / +12V / 电池 / CMOS 等）一律排除。
- `Ambiguous` 候选在自动模式下**不盲选**；若只剩 `Ambiguous` 候选 ⇒ 缺失，原因 `只有来源待确认的候选, 自动模式不做盲选`。
- 读数无效（非有限值或为负）⇒ 该候选出局；全部候选无效 ⇒ 缺失，原因 `候选读数无效 (非有限值或为负)`。
- 候选缺少时间戳或超出新鲜度窗口 ⇒ 该候选出局；全部过期 ⇒ 缺失，原因 `候选已过期或缺少时间戳, 无法确认新鲜度`。
- 读数为 `0` 属于合法值（休眠 / 门控期间真实读数就是 0 V）⇒ 不得据此判定失败。
- 每核心多值时取最高值并在标签注明（`最高核心电压` / `最高核心 VID (N 个核心候选)`），**不得**任取第一核。
- 同一条轨出现多个来源（例如双路 CPU）时按稳定标识择一，注明 `同源多候选 (N 个), 按稳定标识择一`。
- 自动模式允许退回到 VID 参考值，但结果必须被明确标注为参考值。
- 没有任何候选可用 ⇒ 缺失，原因 `没有可用的 CPU 电压候选`。

### 4.4 来源标签、参考值标记与缺失

来源标签（`CpuVoltageResolution.SourceLabel`）：

| 来源类型 | 标签 |
| --- | --- |
| `MeasuredCoreRail` | `CPU 核心电压 (处理器上报)` |
| `MotherboardVcore` | `CPU 核心电压 (主板 Vcore 实测)` |
| `RequestedVid` | `CPU VID (参考值, 非实测供电电压)` |
| `Ambiguous` | `CPU 电压 (来源待确认)` |

- `RequestedVid` 与 `Ambiguous` 属于参考值（`CpuVoltageResolution.IsReferenceOnly`）⇒ 单位后附加语言包标记 `CpuVoltage.MarkerReferenceOnly`。
- 标记走**独立于用户标签与自定义单位格式**的通道 ⇒ 用户把监控项改名成自定义名称、或把单位整个隐藏，都不能抹掉这条提示。
- 缺失时显示 `--` 且不带单位。

---

## 5. 三色阈值、进度条比例与峰值记录

### 5.1 三色状态判定

`MetricUtils.GetState` 给出三色状态，取值为 `MetricUtils.STATE_SAFE` = 0、`STATE_WARN` = 1、`STATE_CRIT` = 2。

- **自适应量程类**（`Frequency` / `Power` / `RPM` / `FPS` / `Voltage` / `Current`）：按「当前值 ÷ 该指标的量程上限」得到的百分比判定 ⇒ 百分比 ≥ 90 为 `STATE_CRIT`，≥ 80 为 `STATE_WARN`，其余为 `STATE_SAFE`。
- **绝对阈值类**（其余类型）：按 `MetricUtils.GetThresholds` 取到的 `Warn` / `Crit` 绝对值判定 ⇒ 值 ≥ `Crit` 为 `STATE_CRIT`，值 ≥ `Warn` 为 `STATE_WARN`。
- 电池电量例外：`BAT.Percent` 反向判定（≤ `Crit` 为 `STATE_CRIT`，≤ `Warn` 为 `STATE_WARN`），且该固定阈值对电池电量为 60 / 20。

派生指标落在哪一侧：

| 指标 | 判定方式 | 量程 / 阈值来源 |
| --- | --- | --- |
| `SYS.Power` | 自适应量程类 | `Settings.RecordedMaxSysPower` |
| `CPU.VOLTAGE` | 自适应量程类 | 固定 1.6 V（不可校准） |
| `MEM.Commit.Load` | 绝对阈值类 | `Settings.Thresholds.Load` |

### 5.2 进度条比例

`MetricUtils.GetProgressValue` 给出 0.0–1.0 的进度条比例：

- 自适应量程类：比例 = 当前值 ÷ 量程上限，上限截到 1.0。
- 其余类型：比例 = 当前值 ÷ 100。
- 负值先取绝对值再绘制（电池充电时放电功率为负；满条只能看出「正在充电」，看不出充得多快）。
- 最终钳制到 `[0.05, 1.0]` ⇒ 即使数值极小也保留一条 5% 的细线，保证该项可见。

⇒ 同一项指标的**颜色**与**进度条**使用同一套量程口径：颜色判定看的是自适应百分比，进度条画的是同一个百分比。

### 5.3 峰值记录规则

峰值记录统一由 `SettingsHelper.UpdateMaxRecord` 承担：

| 规则 | 内容 |
| --- | --- |
| 非正值不记 | 值 ≤ 0、`NaN`、`Infinity` 一律不记 |
| 缺失值不记 | 只有在拿到有效值时才调用峰值记录 ⇒ 缺失不参与最大值记录 |
| 越界值不记 | 含 `Clock` 的键超过 10000 不记；含 `Power` 且不以 `SYS.` 开头的键超过 1000 不记；以 `SYS.` 开头的键超过 2000 不记；含 `Fan` / `Pump` 的键超过 10000 不记 |
| 越界值不刷新峰值 | 超出熔断上限的读数在取值层已被丢弃成缺失 ⇒ 根本不会进入峰值记录路径 |
| 只增不减 | 只有超过已记录峰值时才更新 |
| 落盘节流 | 峰值变化后 30 秒内不重复自动保存 |

分卡键 `GPU.{index}.*` 归一化到基础键后参与基础峰值的比较，同时在 `Settings.RecordedMaxGpuDynamic` 里各自记一份 ⇒ 每张卡的进度条独立自适应。

### 5.4 校准项

可在设置页「硬件参数校准」区调整的量程（`Settings` 的 `RecordedMax*` 属性）：

| 配置项 | 默认值 | 单位 |
| --- | --- | --- |
| `RecordedMaxCpuPower` | 65 | W |
| `RecordedMaxGpuPower` | 100 | W |
| `RecordedMaxSysPower` | 800 | W |
| `RecordedMaxCpuClock` | 4200 | MHz |
| `RecordedMaxGpuClock` | 1800 | MHz |
| `RecordedMaxFps` | 144 | — |
| `RecordedMaxCpuFan` | 4000 | RPM |
| `RecordedMaxCpuPump` | 5000 | RPM |
| `RecordedMaxGpuFan` | 3500 | RPM |
| `RecordedMaxChassisFan` | 3000 | RPM |

- `CPU.VOLTAGE` 的量程固定为 1.6 V，**不**提供校准项。
- `MEM.Commit.Load` 的量程是百分比，不涉及校准项。
- `SYS.Power` 的校准项在设置页标题后缀为 ` (最大值)`。

---

## 6. 更新时机与缓存策略

### 6.1 更新时机

- 派生指标随**采集代次**更新，不随界面 Tick 更新。硬件采集的最小周期为 1000 毫秒（`HardwareSamplingCoordinator.MinimumPeriodMs`），实际周期由 `HardwareMonitor.ComputeEffectivePeriodMs()` 计算 ⇒ 派生指标的数值刷新率上限是每秒一次。
- 整机总功耗在每个采集代次内**只求和一次**；其分量 `CPU.Power` 与 `GPU.{index}.Power` 同样按代次取值。
- 已提交内存每个采集代次采集一次（`GetPerformanceInfo`），随后原子发布。
- CPU 电压的候选读数每个采集代次刷新一次，随后解析并原子发布。
- 主板 / SuperIO 来源的 CPU 电压只能随 3 秒慢扫描刷新（`CpuVoltageResolver.MotherboardSlowScanIntervalMs` = 3000 毫秒），其刷新率低于 CPU 节点上的电压。

### 6.2 代次值缓存

`HardwareValueProvider` 使用 `GenerationValueCache`：

- 同一代次内，同一个指标键只计算一次；面板、任务栏、Web 服务共享这一结果。
- 合法的 `0` **被缓存**（0 是有效值，不得每次重算）。
- 本代次「不可用」的结果**同样被缓存** ⇒ 多个消费方不会反复触发昂贵的失败回退路径。
- 采集代次前进时整体作废，旧代次的中间值不得进入新一代的缓存。
- 采集落定时作废本代次已算出的值，但**不**推进代次 ⇒ 采集期间被读到的更新前旧值不得留在缓存里。
- 代次前进是单调的：更旧的代次被忽略，既不回写代次也不清空缓存。

⇒ 整机总功耗的求和递归读取分量键时，**复用同一代次的缓存** ⇒ 分量不会被重复计算，求和本身也不引入额外的硬件访问。

### 6.3 发布态（单一发布点）

已提交内存与 CPU 电压各有且只有一个发布点：

| 发布点 | 发布内容 | 读取方 |
| --- | --- | --- |
| `CommittedMemoryPublisher` | `CommittedMemoryDisplay`（代次、状态、提交比例、提交字节数、上限字节数） | 面板、任务栏、Web 服务 |
| `CpuVoltageSourcePublisher` | `CpuVoltageResolution`（代次、可用性、有效性、是否参考值、数值、来源类型、来源标签、聚合说明、来源标识、读数时刻、缺失原因） | 面板、任务栏、Web 服务 |

- 发布是**原子替换**（`Volatile.Write`）⇒ 读者要么看到完整旧状态，要么看到完整新状态，不会看到半截数据。
- 消费方**只读**发布态，**不得**自己去调用 `GetPerformanceInfo` 或去读 LHM 传感器 ⇒ 绘制频率不会变成系统调用频率，三处消费方的口径必然一致。
- 数值与说明必须来自**同一份**已发布对象：CPU 电压的数值与来源类型、已提交内存的分子与分母都不得跨代次错配（不得出现「旧值配新来源标签」或「新分子配旧分母」）。
- 硬件重载或释放时必须清空发布态 ⇒ 硬件树换机后界面不得继续显示上一台机器的数据。
- 已提交内存与 CPU 电压**不参与**通用「最后有效值」兜底：来源消失时必须显示缺失，**不得**继续显示旧读数。

---

## 7. 已知限制

- **整机总功耗天然低估。** `SYS.Power` 只累加 CPU 封装功耗与被监控显卡的板级功耗，**不包含**主板 VRM 转换损耗、内存与存储功耗、风扇与水泵功耗、主板其它负载、电源本身的转换损耗 ⇒ 读数系统性低于插座处的实测功率。最准的口径是电源直读（PSU 的 AC 输入功率），本产品不提供该数据源 ⇒ `SYS.Power` 只能作为「主要发热部件功耗之和」的参考，不得当作整机功率。
- **APU 场景可能重复计入。** AMD APU 的封装功耗已含核显部分，核显功耗若同时以 `GPU.{index}.Power` 出现则被计入两次；当前实现不做去重（缺少可靠判据）。
- **求和范围受监控配置约束。** 只有 `Settings.MonitoredGpuCount` 覆盖的显卡参与求和；未纳入监控的显卡功耗不计入。
- **分量缺失会低估总和。** 任一分量缺失时该项被跳过（不按 0 计入，但也不提示），因此部分可用的总和低于真实值，且界面上不额外标注缺项。
- **提交比例不是内存占用率。** `MEM.Commit.Load` 的分母是提交上限且包含已提交交换，与 `MEM.Load` 的物理内存占用率不可直接比较；两者也不共享同一套告警阈值。
- **CPU 电压在多数平台只能取到参考值。** AMD 平台通常只提供处理器请求的电压码（VID / SVI）⇒ 数值被标注为参考值，不等于实测核心供电电压；主板 Vcore 来源的刷新率受 3 秒慢扫描限制。
- **采样下限限制可见精度。** 硬件采集周期下限为 1 秒 ⇒ 派生指标在 1 秒以下的波动不可见。
