# 硬件采样规格

LiteMonitor++ 的硬件数据来自三条通道：LibreHardwareMonitorLib 的传感器树、Windows 原生接口（提交内存）与 Windows 性能计数器（磁盘与网络吞吐量）。本文描述这些数据的采集规格：采样节拍与协调、采样批次快照的不可变语义、缺失与合法的零的严格区分、指标键命名与分组、多显卡分组标题规则、显存容量择源判定、核显与独显的口径区分、磁盘与控制器探测档位、设备选择，以及提交内存、CPU 电压、电池、主板温度、风扇等专项指标的数据来源与口径。

适用对象是在本仓库上扩展指标、接入新传感器，或排查「面板读数缺失 / 数值不对」问题的工程师。界面控件的绘制细节、主题与渲染路线、Web 服务的协议实现不属于本文范围。文中出现的配置键名、枚举值、状态名、缺失原因字符串与注册表路径属于接口约定，必须按本文写法保持稳定。

---

## 1. 采集层结构与唯一入口

### 1.1 唯一采集入口

- 硬件采样的唯一真实入口是 `src/System/HardwareServices/HardwareSamplingCoordinator.cs` 中的 `HardwareSamplingCoordinator`。界面、任务栏、设置页与 Web 服务都必须通过 `RequestCollectionAsync` / `RequestCollection` 请求采样，**不得**绕过协调器直接读取传感器树或直接调用 `IHardware.Update()`。
- 协调器用单调时钟（`TimeProvider`，默认 `TimeProvider.System`）判定采样是否到期 ⇒ 系统时间被调整时采样节拍不受影响。
- 一次采集在协调器内串行执行；硬件更新、传感器建图与采样代次推进都在这一边界内完成。

### 1.2 采样代次

- 每次采集开始时采样代次（generation）前进，`HardwareValueProvider` 的值缓存随之换代 ⇒ 上一代的中间值不得进入新一代的缓存。
- 采集落定（`HardwareMonitor.OnCollectionSettled`）必须先于完成信号发布 ⇒ 采集一结束就取值的消费方不会把上一代的过期值写进新一代的缓存。

### 1.3 硬件树开关

- 存储设备的构造由 `Computer.IsStorageEnabled` 控制，外部控制器的构造由 `Computer.IsControllerEnabled` 控制。两者的取值分别由 `DiskProbePolicy` 与 `ControllerProbePolicy` 决定，是「能不能碰某类设备」的唯一判定来源。
- `DiskProbeGate.Open(IHardwareTreeOpener)` 先把 `IsStorageEnabled` 落到硬件树上，再调用 `Open()` ⇒ 第一次打开硬件树时存储设备的开关已经生效。
- `ControllerProbeGate.Open(IControllerTreeOpener)` 同理，先落 `IsControllerEnabled` 再打开 ⇒ 关闭档位下不会构造任何控制器组。

---

## 2. 采样节拍

### 2.1 采样周期下限

- 普通硬件采集的最小周期是 **1000 毫秒**，由 `HardwareSamplingCoordinator.MinimumPeriodMs` 固定，任何调用方都不得低于这个值发起真实采集。
- 实际采集周期由 `HardwareMonitor.ComputeEffectivePeriodMs()` 计算：`max(MinimumPeriodMs, Settings.RefreshMs)`。`Settings.RefreshMs` 小于等于 0 时回落到 `HardwareMonitor.DefaultRefreshMs`（1000 毫秒）。
- `Settings.RefreshMs` 的取值范围是 **250–60000 毫秒**（由 `ScenarioFieldCatalog` 约束）。
- 存在已鉴权的 Web 数据需求时，采集周期强制为 `MinimumPeriodMs`（1000 毫秒）。

⇒ **把刷新间隔调到 1 秒以下不会提高硬件采样率**：界面可以刷新得更快，但硬件读数仍以 1 秒为下限更新。

### 2.2 界面刷新节拍与采样节拍解耦

| 节拍 | 取值来源 | 下限 |
| --- | --- | --- |
| 硬件采样 | `HardwareMonitor.ComputeEffectivePeriodMs()` | 1000 ms |
| 主界面刷新 | `UIController.ComputeUiRefreshIntervalMs(_cfg.RefreshMs)` = `max(80, RefreshMs)` | 80 ms |
| 任务栏刷新 | `max(RefreshMs, 60)` | 60 ms |

- 界面定时器只负责重绘与重新读取**已发布的快照**，它不驱动采集。
- 界面 Tick 早于采样到期时，协调器快速返回上一份已发布快照：不创建任务、不清空缓存、不推进采集时间 ⇒ 界面刷新再快也不会驱动额外的硬件访问。
- 界面上因此可能出现「数值每 1 秒才动一次」的现象，这是采样下限的直接结果，而不是刷新故障。

### 2.3 采样协调与在途请求共享

对同一目标指标的并发采集请求必须共享在途请求，**不得**重复发起：

- 已有采集在飞行中时到达的新请求，由 `TryBeginCollection` 判定为共享请求（`RecordSharedRequest`），直接返回同一个 Task ⇒ 并发的界面刷新、Web 轮询与后台任务只会产生**一次**真实采集。
- 采集未到期时到达的请求记为跳过（`RecordSkippedRequest`），直接返回已发布快照。
- 协调器未就绪或已释放时到达的请求记为拒绝（`RecordRejectedRequest`）。
- 强制采集：`Invalidate()` 与 `SetReady(true)` 会置位强制标记，使下一次请求立即执行真实采集，并计入 `ForcedCollections`。触发场景是初始化、配置重载与设备选择变更。

### 2.4 失败与取消

- 采集过程中的异常被捕获并写入 `HardwareErrorLog`，随后按正常周期重试 ⇒ 失败不会触发忙循环。
- `OperationCanceledException` 视为取消，不计入失败计数（`RecordCollectionCanceled`）。
- 关闭期跳过的采集不计入失败计数。
- 单次采集耗时超出预期时，不补跑错过的周期，而是以结束时间重新安排未来的截止时间。

### 2.5 截止时间推进规则

- 采集截止时间按固定网格推进，每次恰好推进一个周期 ⇒ 采样不会因为单次任务耗时而逐渐漂移。
- 采样代次、慢扫描触发、磁盘后台扫描触发分别由 `HardwareSamplingSchedule` 与 `HardwareSamplingStatistics` 记录。

### 2.6 子节奏

| 节奏 | 取值 | 用途 |
| --- | --- | --- |
| `HardwareSamplingSchedule.SlowScanInterval` | 3 秒 | 主板 / SuperIO 等慢速传感器的刷新 |
| `HardwareSamplingSchedule.DiskBackgroundScanInterval` | 10 秒 | 后台磁盘的周期扫描 |
| `CpuVoltageResolver.MotherboardSlowScanIntervalMs` | 3000 毫秒 | 主板来源 CPU 电压的刷新，与慢扫描一致 |

⇒ 主板来源的 CPU 电压刷新率是 3 秒，低于 CPU 节点的电压刷新率，界面必须按此披露刷新速度。

---

## 3. 采样批次快照与不可变语义

### 3.1 契约模型

`src/Contracts/SnapshotModels.cs`：

```
MetricSample(string Key, string? StableDeviceId, float? Value, string? Unit,
             SampleQuality Quality, string? MissingReason, double SampleAgeMs)

HardwareSnapshot(long BatchId, long CatalogVersion, int ConfigRevision,
                 IReadOnlyList<MetricSample> Samples, TimeSpan SamplingDuration,
                 double TimestampMonoMs, int SessionEpoch = 0)
```

`src/Contracts/Enums.cs`：

- `SampleQuality { Good, Stale, Missing, Error }`
- `HardwareState { Stopped, Starting, Ready, Degraded, Unresponsive, Stopping }`

### 3.2 批次号语义

- `BatchId` **只在真实采样完成时**递增：`SnapshotPublisher.Publish()` 与 `SnapshotPublisher.PublishFailure()` 内部用 `Interlocked.Increment` 推进。
- 读取已发布快照（`SnapshotPublisher.Current`）**不**推进批次号，**不**修改任何时效字段 ⇒ 同一份快照读多少次都返回同一份内容。
- 快照用 `Volatile.Write` 原子发布，读者不会看到半截状态。
- `SessionEpoch` 只在采集器重建时 +1 ⇒ 消费方可用它识别「采样器已重启」。
- `ConfigRevision` 由适配器 `ApplyConfig` 成功后回写。
- 首次读取时返回缓存中的空批次（`BatchId = 0`），批次级原因 `CurrentMissingReason` 为 `"initializing"`。

⇒ **批次号是采样事实的编号，不是读取次数的编号。** 以「批次号是否变化」判断数据是否更新的消费方是正确的；以「是否读过快照」判断数据是否更新的消费方是错误的。

### 3.3 失败批次

- 采集失败时调用 `PublishFailure(keys, ...)`，把全部已知 Key 置为 `Missing` 并附原因 `"collection-failed"`。
- 失败批次**不保活**上一批的有效值 ⇒ 一旦采集失败，面板立即显示缺失，而不是继续显示已经过期的数字。

### 3.4 缺失与合法的零严格分离

- `SnapshotSampleFactory.Create`：值为 `null`、`NaN` 或 `Infinity` 的样本一律记为 `Missing`，并附缺失原因（默认 `"no-value"`）。
- 缺失样本**不带单位** ⇒ 不得出现「-- V」这类自相矛盾的读数。
- 单位由 `MetricUtils.GetUnitStr(key, value, UnitContext.Panel)` 求出；单位求解抛异常时退化为无单位，但样本照常发布。
- `HardwareMonitor.BuildMetricSamples()` 额外做一次「伪造零」判定：数值为 0 且背后传感器本身没有读数 ⇒ 判为伪造零，转成 `Missing` + `"no-value"`；**真实的 0 保持 `Good` + 0**。
- 快照的 Key 集合由 `EnsureSnapshotKeys()` 决定，等于传感器映射表的 Key 与用户配置的监控项 Key 的并集 ⇒ 用户配了但采不到的项也会以缺失出现，而不是从快照里消失。
- 显示层由 `MetricUtils` 的 `MissingValueText`（`"--"`）渲染缺失。

⇒ **缺失绝不显示为 0，真实的 0 绝不显示为缺失。** 面板上的 `0` 与 `--` 是两种截然不同的语义：`0` 表示「设备报告了这个值」，`--` 表示「该次采样没有拿到值，或拿到的值不可信」。

### 3.5 缺失原因分类

批次级原因（`SnapshotPublisher`）：

| 字符串 | 含义 |
| --- | --- |
| `initializing` | 采集器尚未完成首次采样 |
| `no-value` | 该项在该次采样中没有有效读数（含被判定为伪造零的 0） |
| `collection-failed` | 该次采集整体失败，该项无值 |

CPU 电压的项级缺失原因（`CpuVoltageResolver`，字符串稳定）：

| 字符串 | 触发条件 |
| --- | --- |
| `指定来源不可用 (传感器标识已不存在)` | 手动指定的传感器标识在当前硬件树上找不到 |
| `指定来源不是 CPU 核心电压 (如 SoC / GT / DIMM / 输入供电)` | 手动指定的候选被分类为 `Excluded` |
| `指定来源的读数无效` | 手动指定的候选读数非有限值或为负 |
| `候选已过期或缺少时间戳, 无法确认新鲜度` | 自动模式下所有候选都缺少时间戳或超出允许的新鲜度窗口 |
| `候选读数无效 (非有限值或为负)` | 自动模式下所有候选的读数都无效 |
| `只有来源待确认的候选, 自动模式不做盲选` | 仅存在 `Ambiguous` 候选 |
| `没有可用的 CPU 电压候选` | 没有任何候选可用 |

---

## 4. 指标键命名规范

### 4.1 基础键与分卡键

- 基础键是**不带显卡序号**的指标名，例如 `CPU.Load`、`GPU.Temp`、`MEM.Load`、`DISK.Read`。
- 分卡键由 `GpuKey.Build(index, metric)` 生成，格式固定为 `GPU.{index}.{metric}`，例如 `GPU.0.Load`、`GPU.1.Temp`。
  - 前缀常量：`GpuKey.Prefix` = `"GPU."`。
  - 序号上限：`GpuKey.MaxGpuCount` = 8 ⇒ 最多同时监控 8 张卡。
  - 解析：`GpuKey.TryParse(key, out index, out metric)`。
- 归一：`GpuKey.BaseKey(metric)` 与 `GpuKey.ToBaseKey(key)` 把分卡键归到基础键（`GPU.1.Temp` → `GPU.Temp`）。语言包文案、阈值与峰值回退一律使用基础键 ⇒ 分卡键与基础键共用同一套文案与阈值。
- 被监控的卡数由 `Settings.MonitoredGpuCount` 决定，默认 1，最小 1。
- `GPU.0` 的若干指标同时写入不带序号的基础键（`GPU.Fan` 等），保证既有配置继续可用。

### 4.2 指标分组

分组键由 `Settings.MonitorItemConfig.CachedUIGroup` 推导：

| 指标键 | 分组键 |
| --- | --- |
| `MEM.Load`、`MEM.Commit.Load`、`MOBO.Temp`、`DISK.Temp`、`CASE.Fan`、`FPS`、`SYS.Power` | `HOST` |
| 带序号的 GPU 键（`GPU.{index}.*`） | `GPU.{index}` |
| 其余全部 | `Key.Split('.')[0]`（如 `CPU`、`GPU`、`MEM`、`DISK`、`NET`、`DATA`、`BAT`、`DASH`） |

### 4.3 默认监控项键全集

`src/Core/SettingsHelper.cs` 提供的默认监控项：

```
DASH.HOST  DASH.Time  DASH.Uptime  DASH.IP
CPU.Load   CPU.Temp   CPU.Clock    CPU.Power   CPU.VOLTAGE   CPU.Fan   CPU.Pump
GPU.Load   GPU.Temp   GPU.Clock    GPU.Power   GPU.Fan       GPU.VRAM
MEM.Load   MEM.Commit.Load   FPS   MOBO.Temp   DISK.Temp   CASE.Fan   SYS.Power
BAT.Percent  BAT.Power  BAT.Voltage  BAT.Current
DISK.Read  DISK.Write  DISK.Activity
NET.Up     NET.Down
DATA.DayUp  DATA.DayDown
```

其它相关键常量：`MetricUtils.CommittedMemoryKey` = `"MEM.Commit.Load"`，`MetricUtils.CommittedMemoryCapacityUnit` = `"GB"`，`MetricUtils.CpuVoltageKey` = `"CPU.Voltage"`，`SettingsHelper.CommittedMemoryKeyPrefix` = `"MEM.Commit."`。

### 4.4 指标类型与单位

`MetricUtils.GetType(key)` 按**子串**匹配判定类型 ⇒ 带序号的键与基础键得到相同类型（`GPU.1.Temp` 仍判为 `Temperature`）。

| 判定条件 | `MetricType` | 单位 |
| --- | --- | --- |
| 键精确等于 `FPS` | `FPS` | ` FPS`（任务栏 `F`） |
| 以 `BAT` 开头 | `Percent` / `Voltage` / `Current` / `Power`（按子串细分） | `%` / `V` / `A` / `W` |
| 含 `MEM` 或 `VRAM` | `Memory` | 容量模式下按容量自适应，否则 `%` |
| 含 `LOAD` | `Percent` | `%` |
| 含 `TEMP` | `Temperature` | `°C` |
| 含 `CLOCK` | `Frequency` | `GHz`（数值除以 1000，保留 1 位小数） |
| 含 `POWER` | `Power` | `W` |
| 含 `VOLTAGE` | `Voltage` | `V` |
| 含 `FAN` 或 `PUMP` | `RPM` | ` RPM`（任务栏 `R`） |
| 以 `NET` 或 `DISK` 开头 | `DataSpeed` | 动态单位（`/s`） |
| 含 `DATA` | `DataSize` | 动态单位 |

- 数值格式由 `MetricUtils.GetValueStr` 给出：频率除以 1000 保留 1 位小数；FPS 与 RPM 取整；其余按自适应小数位（小于 9.995 取 2 位、小于 99.95 取 1 位、不小于 100 取整数）。
- 阈值状态常量为 `MetricUtils.STATE_SAFE` = 0、`STATE_WARN` = 1、`STATE_CRIT` = 2。
- CPU 电压是特例：单位恒为 `V`，但必须附带来源标记（见 §10.2）；缺失时**不带单位**。

---

## 5. 多显卡分组与标题规则

### 5.1 按卡拆组

- `GpuGroupPolicy.SplitPerCard` 为 `true` ⇒ 按卡拆组恒定生效：每张被监控的显卡构成一个独立分组，分组键为 `GPU.{index}`。
- `GpuGroupPolicy.TryGetPerCardGroupKey("GPU.0.Load")` ⇒ `"GPU.0"`；不带序号的 `GPU.Load` 不拆组。
- `GpuGroupPolicy.TryGetCardIndex(groupKey)`：只接受 `GPU.` 加一段纯数字（`"GPU.0"` ⇒ `true, 0`）；`"GPU"`、`"GPU.0.Load"`、`"CPU"` 一律返回 `false`。
- 组内指标标签不再重复显卡简称 ⇒ 标题承担「这是哪张卡」的表达职责，组内只写指标本身。

### 5.2 卡序稳定

- 显卡顺序由**槽位绑定**决定：`DeviceSlotResolver` 把槽位（序号）绑定到设备的稳定身份。
- 已绑定的设备不在场时，槽位保持绑定并显示离线 ⇒ **不得**改绑到别的卡；原设备回到机器上时自动回到原槽位。
- 空槽位按 `HardwareRules.GetHwPriority` 的优先级补齐，且只对**新槽位**固化绑定，既有绑定不被覆盖。
- 单卡模式下若用户手动指定了 `PreferredGpu`，按稳定身份定位；设备不在场时保持绑定、显示离线。
- 优先级数值越小越优先：

| 硬件 | 优先级 |
| --- | --- |
| `Microsoft Basic Render Driver` | 100 |
| NVIDIA | 0 |
| AMD 通用名 `AMD Radeon(TM) Graphics` | 2 |
| AMD 其它型号 | 0 |
| Intel Arc 独显（A / B / Pro 系列） | 0 |
| Intel Arc 核显 | 1 |
| Intel Iris | 2 |
| Intel UHD | 3 |
| 其它 | 4 |

### 5.3 标题解析与回退

- `GroupTitleResolver.Resolve(cfg, groupKey)` 的优先级依次是：
  1. 用户自定义别名 `Settings.GroupAliases`；
  2. 按卡分组的显卡简称（`GpuGroupPolicy.GetDefaultTitle`）；
  3. 语言包 `Groups.{key}`（取 `LanguageManager.GetOriginal` 的原值，不取含用户覆盖的 `T`）；
  4. 分组键本身。
- **标题永不为空**：`GpuGroupPolicy.GetDefaultTitle(index)` 取 `GpuRegistry.GetShortName(index)`；取不到时回退为 `GPU.{index}`。
- 简称生成规则（`HardwareRules.GetShortGpuName`）：剥离 `(R)` `(TM)` `®` `™` → 压缩空白 → 剥离厂商前缀（`NVIDIA GeForce` / `NVIDIA` / `AMD Radeon` / `AMD` / `Intel` / `Apple` / `Microsoft`）→ 剥离系列前缀（`RTX` / `GTX` / `RX` / `Radeon` / `Arc`）。例如 `NVIDIA GeForce RTX 3080 Ti` ⇒ `3080 Ti`。
- `GpuRegistry` 以整体替换（`ReplaceAll`）的方式更新，读侧不会看到半更新的注册表。
- 网页端 DOM 标识由 `GroupTitleResolver.ToSafeGroupId` 生成：非 `[A-Za-z0-9_-]` 的字符替换为下划线（`GPU.0` ⇒ `GPU_0`），空键 ⇒ `OTHER` ⇒ 含点号的分组键不得直接拼进 CSS 选择器。

---

## 6. 显存容量择源判定

### 6.1 双通道

显存有两条互相独立的数据通道：

| 通道 | 键 | 特点 |
| --- | --- | --- |
| 厂商通道 | `VRAM.Used` / `VRAM.Total` | NVAPI / ADL 上报；在部分多卡实现下会被全局化，多张卡可能拿到同一份数据 |
| D3D 通道 | `VRAM.D3D.Used` / `VRAM.D3D.Total` | 按适配器统计，每张卡各读各的 |

单位换算由 `HardwareValueProvider.ToMB` 统一处理：大于 10485760 的数值视为字节，除以 1048576 后按 MB 使用。

### 6.2 真实容量来源

`src/System/HardwareServices/GpuVramCapacity.cs` 从注册表读取每块显示适配器的驱动可见容量：

- 注册表路径：`HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}`。
- 只读取**纯数字**的子键（该类键下还有 `Configuration` / `Properties` 等系统子键，后者受保护、打开会抛异常）。
- 数值项：`DriverDesc`（适配器名）与 `HardwareInformation.qwMemorySize`（字节）。
- 小于 `MinPlausibleBytes`（64 MB）的容量按噪声丢弃。
- 名字归一化 `NormalizeName`：剥离 `(R)` `(TM)` `®` `™` → 压缩空白 → 转大写。
- 同名多条记录只有在容量差小于 0.5 MB 时才可用；同名但容量不一致时返回空值 ⇒ **宁可没有也不猜**。
- `GetAllTotalsMB()` 返回全部适配器的容量，作为串卡的交叉证据 ⇒ 必须取全部适配器，只取被监控的卡会失去证据。

### 6.3 串卡判定

`GpuVramSource.IsVendorChannelForeign(vendorTotal, realTotal, allTotals)` 判定厂商通道是否在报告别的卡，必须**同时**满足三条：

1. 本卡真实容量存在且不小于 `MinPlausibleTotalMB`（512 MB）；
2. 厂商容量与本卡真实容量对不上（容差 = `max(1 MB, max(a, b) × 1%)`，对应 `AbsToleranceMB` = 1 与 `RelTolerance` = 0.01）；
3. 厂商容量命中了**另一块适配器**的容量。

任何一条证据缺失 ⇒ 判为「厂商通道可信」，维持厂商通道 ⇒ 单卡、AMD、Intel 场景的行为不发生变化。

参与判定的资格：

- 卡的名称必须走专用显存口径（`HardwareRules.UsesDedicatedVramName`）：Intel 只认 Arc A / B / Pro 独显，AMD 排除 `AMD Radeon(TM) Graphics` 与 `AMD Radeon Graphics`，NVIDIA 一律按独显处理。
- 该卡的显存传感器名不得含 `Shared`（`HardwareValueProvider.IsSharedMemoryCard`）⇒ 核显一律不参与串卡判定。

### 6.4 择源与冲突裁决

`GpuVramSource.Select(usedVendor, totalVendor, usedD3D, totalD3D, realTotal, useD3D)`：

| 情形 | used | total |
| --- | --- | --- |
| 判为串卡（`useD3D` = true） | `d3dUsed` | `realTotal` ?? `d3dTotal` ?? `vendorTotal` |
| 未判为串卡 | `vendorUsed` ?? `d3dUsed` | `vendorTotal` ?? `d3dTotal` ?? `realTotal` |

裁决规则：

- **D3D 通道的 used 缺失时，used 直接为空** ⇒ 绝不拿别卡的 used 去除本卡的 total，避免算出超过 100% 的占用率。
- 分母优先使用注册表读到的真实容量（串卡时它是唯一可信的分母），其次是 D3D 通道，最后是厂商通道。
- 占用率 = `used / total × 100`，仅在 `used` 与 `total` 都存在且 `total > 0` 时成立；否则回退到 `GPU.VRAM.Load` 传感器值，再否则为缺失。
- 校正后的每卡总容量写入 `Settings.DetectedGpuVramTotalGBByKey`，键为 `GPU` / `GPU.0` / `GPU.1` ⇒ 容量模式的显示与占用率始终同源。

---

## 7. 核显与独显的口径区分

### 7.1 共享显存口径的判定

`HardwareRules.ShouldUseSharedMemory(hw)` 为真当且仅当：该硬件是 Intel 显卡，且不是独显版 Arc（`IsDiscreteArc` 通过名称中的 ` A` / ` B` / ` Pro` 判定独显系列）。

⇒ Intel 核显（UHD / Iris / Arc 核显）走共享显存口径；Intel 独显与 NVIDIA / AMD 走专用显存口径。

### 7.2 传感器匹配口径

`SensorMatcher.MatchGpu(name, sensorType, preferShared)`：

| 传感器 | 条件 | 目标键 |
| --- | --- | --- |
| 负载 | 名称含 `core` 或 `d3d 3d` | `GPU.Load` |
| 温度 | 名称含 `core` / `hot spot` / `soc` / `vr` | `GPU.Temp` |
| 小数据 | 名称含 `d3d` 且含 `dedicated`（分别含 `used` / `total` / `free`） | `GPU.VRAM.D3D.Used` / `.Total` / `.Free` |
| 小数据 | 核显（`preferShared`）：名称含 `shared` | `GPU.VRAM.Used` / `GPU.VRAM.Total` |
| 小数据 | 核显：没有 `shared` 时回退到通用 `memory`（专用内存一律不采纳） | `GPU.VRAM.Used` / `GPU.VRAM.Total` |
| 小数据 | 独显：名称含 `dedicated` 或通用 `memory`，排除 `shared` 与 `d3d dedicated` | `GPU.VRAM.Used` / `GPU.VRAM.Total` |
| 负载 | 名称含 `memory` 且不含 `controller` | `GPU.VRAM.Load` |

- 名称含 `memory controller` 的传感器是显存控制器利用率，**不得**当作显存占用率。
- `D3D Dedicated Memory *` 单独建键，由取值层按 §6 的规则择源，而不是让厂商值直接顶掉 D3D 值。
- 同一硬件节点上多个传感器命中同一个键时按先到先得处理；显卡键发生冲突时的规则是：优先级更高的卡（`GetHwPriority` 数值更小）**不得**被覆盖，优先级相同时 `Vendor` 优于 `D3D`。

### 7.3 口径本身的边界

- `GPU.Load` 是驱动上报的 3D 引擎利用率。核显与独显走同一套匹配规则，但两者的负载含义不同（核显的利用率同时受显示输出与合成器影响），**跨卡横向比较 `GPU.Load` 没有意义**。
- 核显没有独立显存，`GPU.VRAM.*` 描述的是共享显存占用，其分母是共享显存上限而不是显存颗粒容量；与独显的显存占用率不是同一口径。
- 核显不参与串卡判定 ⇒ 核显的显存占用率恒取自身通道，不受其它显卡数据串扰的影响。
- `GPU.Temp` 的匹配接受 `hot spot`、`soc`、`vr` 等命名 ⇒ 同一硬件节点上的多个温度传感器只有首个被认领，其余被丢弃。
- 显存容量模式依赖 `Settings.DetectedGpuVramTotalGBByKey`；在总容量未知时，显存指标回退为百分比显示。

---

## 8. 磁盘探测与控制器探测档位

### 8.1 磁盘探测档位

`DiskProbeMode`（`src/System/HardwareServices/DiskProbePolicy.cs`）：

| 枚举值 | 数值 | 行为 |
| --- | --- | --- |
| `CountersOnly` | 0 | **不构造任何存储设备**：不打开设备句柄、不做 SMART 探测。磁盘速度与活动率来自 Windows 性能计数器；磁盘温度与 SMART 明确缺失（显示 `--`，不是 0） |
| `SmartAllowed` | 1 | 允许 LHM 打开存储设备，可获得 SMART 与磁盘温度；代价是可能占用可移动磁盘并与其它硬件工具冲突 |

哨兵与非法值：

- `DiskProbePolicy.UnconfiguredModeValue` = -1：配置里用 `null` 表示该键不存在，归一到 -1 ⇒ 按 `CountersOnly` 生效，同时进入 `DiskProbeMigrationState.Unconfigured`，设置页据此提示用户做明确选择。
- 其它非法整数值一律退化为 `CountersOnly`，且不进入迁移状态。
- `AllowsDiskCounters` 恒为 `true`（两种档位都允许计数器，计数器不碰设备句柄）。
- `StorageProbeEnabled` / `AllowsSmart` / `AllowsDiskTemperature` 均等价于「模式是 `SmartAllowed`」。

状态机 `DiskProbeState`：`Uninitialized` / `CountersOnly` / `SmartAllowed` / `Transitioning` / `Degraded`。切换由 `ApplyMode(target, apply, verify)` 执行，**幂等**：目标模式与当前模式相同且处于稳定态时不重建硬件树。切换失败时记录 `LastError`、模式回退、状态置 `Degraded`，`TryRevert` 最多回退一次 ⇒ 切换失败不得被伪装成成功。

### 8.2 档位选择的判据

| 需求 | 建议档位 |
| --- | --- |
| 只关心读写速度与活动率；机器上有 USB / HID 设备冲突；或经常挂载可移动磁盘 | `CountersOnly` |
| 需要 `DISK.Temp`；或需要 SMART 级别的磁盘健康信息 | `SmartAllowed` |
| 配置里没有该字段 | 生效值为 `CountersOnly`，并提示用户选择 |

### 8.3 仅计数器档位下的行为

- `DiskManager.ProcessUpdate` 在仅计数器档位直接返回 ⇒ 不对存储设备发起任何 `Update`。
- `DiskManager.GetValue` 在仅计数器档位：`DISK.Temp` ⇒ 空值（缺失）；`DISK.Read` / `DISK.Write` / `DISK.Activity` ⇒ 走 Windows 计数器（`IDiskCounterSource`）。
- `IDiskCounterSource.GetDiskRead` / `GetDiskWrite` / `GetDiskActive` 在计数器不可用时返回空值 ⇒ 绝不返回 0。`PerfCounterDiskSource` 把活动率钳制到 0–100。
- 关闭「优先使用系统计数器」时，计数器路径同样返回空值（缺失），不得伪造 0。

### 8.4 控制器探测档位

`ControllerProbeMode`（`src/System/HardwareServices/ControllerProbePolicy.cs`）是真正的三档：

| 枚举值 | 数值 | 行为 |
| --- | --- | --- |
| `Auto` | 0 | 仅当监控项需要风扇 / 水泵时才开启控制器扫描（`ShouldProbe(fanMetricsNeeded)` 返回 `fanMetricsNeeded`） |
| `Off` | 1 | **永不**构造任何控制器组；依赖外部控制器的风扇 / 水泵读数缺失（显示 `--`，不是 0） |
| `On` | 2 | 无条件构造控制器组 |

- `ControllerProbePolicy.UnconfiguredModeValue` = -1 ⇒ 归一到 `Auto` 并进入 `ControllerProbeMigrationState.Unconfigured`；其它非法整数值退化为 `Auto`。
- 状态机 `ControllerProbeState`：`Uninitialized` / `Disabled` / `Enabled` / `Transitioning` / `Degraded`；切换语义与磁盘档位一致（幂等、失败可回退、不伪装成功）。
- 风扇 / 水泵的识别需求由 `SensorMap` 在映射时按 `cfg.IsAnyEnabled("CPU.Fan") / ("CPU.Pump") / ("CASE.Fan")` 判定 ⇒ 没有风扇类监控项时不扫描控制器。

### 8.5 控制器允许列表

`ControllerDeviceExclusion` 提供一个「跳过 Update」的匹配清单：

- 默认模式 `DefaultPatterns` = `{ "daplink", "cmsis-dap", "0d28" }`。
- 匹配语义：对 `IHardware.Identifier` 或 `IHardware.Name` 做**大小写不敏感的字符串包含匹配**（`StringComparison.OrdinalIgnoreCase`）；两个字段都为空时不算命中；空清单 ⇒ 一律不排除。
- 清单变更实时生效：`SetPatterns` / `Add` / `Remove` / `ResetToDefaults` 立即改变下一次 `IsExcluded` 的判定，不需要重建硬件树，也不缓存判定结果。`Remove` 按大小写不敏感的全等比较删除。
- 边界：LHM 的 `IHardware` 不暴露 USB VID / PID 或 PnP 硬件 ID，只有 `Identifier` 与 `Name`；这两个字段都不是跨重启的稳定身份 ⇒ 该清单**只能**用于跳过 `Update`，无法在设备构造前阻止某个设备被创建，也无法区分「同名不同设备」⇒ 宁可漏排除也不误排除。

---

## 9. 设备选择

### 9.1 身份模型

`src/System/HardwareServices/DeviceIdentity.cs` 定义统一的设备身份：

- `DeviceKind { Unknown, Gpu, Storage, Fan, Network, Motherboard }`。
- `DeviceIdentity` 携带 `StableId`（跨重启稳定标识）、`RuntimeId`（当前会话内的实例标识）、`DisplayName`、`ParentId`、`Kind`、`IsPresent`、`Stability`。
- `IdentityStability { Unknown, Unstable, Stable }`：处于 `Unstable` 的身份**不得**复用到先前的绑定。
- 公开指标键始终是 `GPU.{index}.*` 一类的槽位形式，稳定身份只用于把槽位绑到物理设备，裸 PCI 路径**不得**拼进公开键。

配置匹配口径 `DeviceIdentityMatcher.Matches`：稳定 ID → 运行时标识 → 显示名，三级比较均**大小写不敏感**；配置值为空时视为全部命中。

### 9.2 网卡

- 配置键：`Settings.PreferredNetwork`（用户指定）与 `Settings.LastAutoNetwork`（自动模式记住的设备）。
- 手动模式：按网卡名称**精确匹配**（`OrdinalIgnoreCase`）。设备消失时立即作废该设备的样本，返回保留期内的样本（`Stale`），并**保留用户的选择** ⇒ 不得静默改成自动选卡。
- 自动模式的取用顺序：
  1. 运行时缓存的网卡（先做存活检查：硬件对象不在当前硬件列表中即丢弃并作废其样本缓存与差分基线）；
  2. `LastAutoNetwork` 记忆的设备；
  3. 全量扫描评分。
- 只有**读取失败**才触发有限重选（受重选窗口约束）；空闲（0 流量）**不算**坏网卡 ⇒ 空闲的网卡不会反复换卡。
- 样本保活有期限：回退只允许使用保留期内的样本（`Stale`），到期后一律 `Missing`。

### 9.3 磁盘

- 配置键：`Settings.PreferredDisk`（用户指定）与 `Settings.LastAutoDisk`（自动模式的热缓存线索，存显示名）。
- 手动模式：用 `DeviceIdentityMatcher.Matches` 锁定 ⇒ 同型号的两块盘可以分别选中；未被选中的盘不发起 `Update`。
- 自动模式的取用顺序：
  1. 运行时缓存的磁盘（先做存活检查）；
  2. `LastAutoDisk`（`FindDiskByConfig` 口径：稳定 ID → 运行时标识 → 显示名；只剩显示名这一条路时**不猜**，同名多盘返回空值）；
  3. 全盘扫描评分：读写吞吐之和，系统盘额外加 `1e9` 分 ⇒ 系统盘优先。
- 自动模式选出设备后把显示名写入 `LastAutoDisk`，仅作为自动模式的线索，不覆盖用户的 `PreferredDisk`。

### 9.4 显卡

- 配置键：`Settings.PreferredGpu`（稳定身份）、`Settings.MonitoredGpuCount`。
- 单卡 + 手动指定：按稳定身份定位；设备不在场时保持绑定、显示离线，不得改绑到别的卡。
- 多卡：按槽位绑定解析，空槽位按硬件优先级补齐（见 §5.2）。

### 9.5 风扇、水泵、主板温度与 CPU 电压的手动指定

| 配置键 | 指向 |
| --- | --- |
| `Settings.PreferredCpuFan` | CPU 风扇传感器 |
| `Settings.PreferredCpuPump` | 水泵传感器 |
| `Settings.PreferredCaseFan` | 机箱风扇传感器 |
| `Settings.PreferredMoboTemp` | 主板温度传感器（取值含 `自动` 或 `Auto` 时视为自动模式） |
| `Settings.PreferredCpuVoltageSensor` | CPU 电压候选的传感器标识 |

风扇类的匹配串形态为 `[硬件名] 传感器名` 或纯传感器名（`FanMapper.FindFanByConfig`）。

---

## 10. 专项指标的数据来源与口径

### 10.1 提交内存

- 指标键：`MEM.Commit.Load`；容量口径单位固定为 `GB`（`MetricUtils.CommittedMemoryCapacityUnit`）。
- 数据源：`src/System/HardwareServices/CommittedMemoryReader.cs` 的 `WindowsCommittedMemorySource`，调用 Windows 原生的 `GetPerformanceInfo` / `K32GetPerformanceInfo` 读取 `PERFORMANCE_INFORMATION`。
  - **不依赖**性能计数器，不需要管理员权限，也不受性能计数器被禁用或重置的影响 ⇒ 关闭「优先使用系统计数器」时该指标仍然可用。
  - 期望结构体大小：64 位 104 字节、32 位 56 字节；不匹配时状态为 `StructureSizeMismatch`。
- 计算口径：
  - `CommittedBytes` = `CommitTotal` × `PageSize`；
  - `LimitBytes` = `CommitLimit` × `PageSize`；
  - `LoadPercent` = 100 × `CommittedBytes` / `LimitBytes`。
  - **分母必须是提交上限**，绝不用物理内存总量（那会算出远超 100% 的比例）。物理内存总量只作为诊断信息保留。
- 状态枚举 `CommittedMemoryStatus`：`Ok` / `ApiFailed` / `InvalidPageSize` / `InvalidLimit` / `Overflow` / `StructureSizeMismatch` / `StaleGeneration`。
- 发布：`CommittedMemoryPublisher.CommittedMemoryDisplay.IsMissing` ⇔ 状态不是 `Ok`。钳制只发生在显示层，`CommittedBytes` 与 `LimitBytes` 保持真实值。
- 分子与分母取自**同一个不可变状态对象** ⇒ 不跨代次错配。缺失时显示 `--` 且不带单位。

### 10.2 CPU 电压

- 指标键：`CPU.Voltage`（`MetricUtils.CpuVoltageKey`）。
- 传感器映射层只**登记候选**，不选择：`SensorMatcher` 不再产出 `CPU.Voltage` 键 ⇒ 由「先到先得」决定口径的路径不存在。
- 候选分类 `CpuVoltageResolver.Classify` ⇒ `CpuVoltageSourceKind`：

| 枚举值 | 数值 | 含义 |
| --- | --- | --- |
| `Unknown` | 0 | 未分类 |
| `MeasuredCoreRail` | 1 | CPU 硬件节点上语义已确认的核心供电遥测（不等于 VRM 的 ADC 实测值） |
| `MotherboardVcore` | 2 | 主板 / SuperIO 明确命名为 Vcore 的核心供电电压（随 3 秒慢扫描刷新） |
| `RequestedVid` | 3 | 处理器**请求**的电压（VID / SVI / 电压码），只能当参考值 |
| `Ambiguous` | 4 | 来源含糊（如只叫 `CPU`），语义无法确认 |
| `Excluded` | 5 | 明确不是核心电压的其它轨（SoC / GT / SA / AUX / DIMM / 输入供电 / +5V / +12V / 电池 / CMOS 等） |

- 候选粒度 `CpuVoltageGranularity`：`Rail`（整条核心轨一个读数）/ `PerCore`（每核心一个读数）。硬件层级 `CpuVoltageHardwareLevel`：`Unknown` / `Cpu` / `SuperIO` / `Motherboard` / `Other`。
- 自动模式的排序与选择：来源可信度（枚举值升序）→ CPU 归属（CPU 节点优先）→ 粒度（`Rail` 优先）→ 稳定标识（`StringComparer.Ordinal`）⇒ 同优先级候选的选择稳定，不依赖枚举顺序。
  - 每核心多值时取最高值，并在标签注明（`最高核心电压` / `最高核心 VID (N 个核心候选)`），不得任取第一核。
  - 同一条轨出现多个来源时按稳定标识择一，注明 `同源多候选 (N 个), 按稳定标识择一`。
- 手动模式（`PreferredCpuVoltageSensor`）以传感器标识匹配；标识不存在、被分类为 `Excluded` 或读数无效时一律缺失，原因见 §3.5。
- 口径标记：`RequestedVid` 与 `Ambiguous` 属于参考值（`IsReferenceOnly`），单位后附加语言包标记 `CpuVoltage.MarkerReferenceOnly` ⇒ 用户把监控项改名也抹不掉这条提示。
- 来源标签：`CPU 核心电压 (处理器上报)` / `CPU 核心电压 (主板 Vcore 实测)` / `CPU VID (参考值, 非实测供电电压)` / `CPU 电压 (来源待确认)`。
- 缺失时显示 `--`，**绝不**用 0 顶替，**绝不**沿用来源消失前的过期电压。

### 10.3 电池

- 指标键：`BAT.Percent` / `BAT.Power` / `BAT.Voltage` / `BAT.Current`。
- 电量数据源：电池硬件的 `Level` 类型传感器，排除含 `Degradation` 或 `Wear` 的健康度传感器，优先选择名称含 `Charge` 的传感器；数值钳制到 0–100。
- 功率与电流的**符号**由 `MetricUtils.GetPowerStatus().AcOnline` 强制决定，与驱动原始符号无关：插电为正（充电 / 输入），电池供电为负（放电 / 输出）。
- 机器没有电池硬件时传感器缺失 ⇒ 取值为空 ⇒ 显示 `--`。

### 10.4 主板温度

- 指标键：`MOBO.Temp`。
- 候选数据源：主板与 SuperIO 硬件节点的温度传感器。
- 自动选择的优先级：名称含 `System` → 名称含 `Motherboard` → 名称含 `Chipset` 或 `PCH` → 有效值中取合理范围（15–68 ℃）内的最大值；仍无结果时取宽范围（0–95 ℃）内的最大值；超过 95 ℃ 的读数按异常值丢弃。
- 读取时（`HardwareValueProvider.ReadMoboTemperature`）：读数为空、`NaN`、`Infinity` 或小于等于 0 ⇒ 回退到上一代有效值；自动模式下读数不小于 95、手动指定模式下读数大于 125 ⇒ 同样回退到上一代有效值；上一代有效值本身越界或不存在 ⇒ 缺失。

### 10.5 风扇与水泵

- 指标键：`CPU.Fan` / `CPU.Pump` / `CASE.Fan`。
- 候选收集：遍历全部硬件，**排除** GPU、CPU、存储、内存、网络硬件节点上的风扇；只收 `Fan` 类型且读数大于 200 RPM 的传感器（底噪过滤）。
- 分配优先级：
  1. 用户手动指定（`PreferredCpuFan` / `PreferredCpuPump` / `PreferredCaseFan`）；
  2. CPU 风扇：散热器类硬件上的风扇 → 名称含 `CPU` 的风扇 → 首个未被占用的风扇；
  3. 水泵：散热器类硬件上的泵 → 名称含 `Pump` / `Speed` / `Water` / `AIO` 的传感器 → 转速大于 3000 RPM 的最高者；
  4. 机箱风扇：名称含 `Rear` → `Chassis` → `Sys` → `Case`；都不命中时取剩余中转速最低者（若此时水泵仍未定，取剩余中转速最高者作为水泵）。
- 已被占用的传感器不得重复分配。

### 10.6 内存

- 指标键：`MEM.Load`（占用率）、`MEM.Used`、`MEM.Available`。
- 数据源：内存硬件节点的传感器；排除虚拟内存（名称含 `virtual`）。名称含 `used` 的数据型传感器对应 `MEM.Used`，名称含 `available` 或 `free` 的对应 `MEM.Available`。
- `MEM.Load` 兼容只叫 `Load` 的负载传感器。

---

## 11. 采样统计与可观测性

`HardwareSamplingStatistics` 提供采样层的运行期计数：

| 计数器 | 含义 |
| --- | --- |
| `CollectionRounds` | 实际执行的采集轮数 |
| `SkippedRequests` | 未到期被跳过的请求数 |
| `RejectedRequests` | 协调器未就绪或已释放时被拒绝的请求数 |
| `SharedRequests` | 共享在途请求的请求数 |
| `FailedCollections` | 采集失败次数 |
| `ForcedCollections` | 强制采集次数 |
| `ValueComputations` | 指标取值计算次数 |
| `SlowScanTriggers` | 慢扫描触发次数 |
| `DiskBackgroundScanTriggers` | 磁盘后台扫描触发次数 |
| `TrafficSaveTriggers` / `GcRequestTriggers` / `WorkingSetTrimTriggers` | 流量落盘、GC 请求、工作集裁剪的触发次数 |
| `Generation` | 当前采样代次 |
| `LastCollectionDurationMs` / `TotalCollectionDurationMs` | 单次与累计采集耗时 |

- 设备级更新计数由 `SnapshotDeviceUpdates()` 给出（键如 `GPU.0` / `Cpu:xxx`）。
- 分类汇总由 `SnapshotCategoryTotals()` 给出（`Cpu` / `Gpu` / `Memory` / `Network` / `Storage` / `Motherboard` / `Battery`）。

---

## 12. 已知限制

- 硬件采样周期下限为 1 秒 ⇒ 高频变化剧烈的指标（瞬时吞吐、瞬时利用率）在 1 秒以下的波动不可见。
- 仪表盘主节奏由 `RefreshMs` 决定，但受 1 秒采样下限约束 ⇒ 把 `RefreshMs` 调到 250 毫秒只会让界面重绘变快，读数仍每秒更新一次。
- 仅计数器档位下没有 `DISK.Temp`，也没有 SMART 数据；磁盘温度显示为 `--`。
- `ControllerDeviceExclusion` 的匹配依据只有 `Identifier` 与 `Name`，两者都不是跨重启的稳定身份 ⇒ 该清单只能跳过 `Update`，不能在设备构造前排除设备。
- 厂商显存通道在多卡场景下可能被全局化；串卡判定依赖注册表容量作为交叉证据，注册表读不到时判定退化为「厂商通道可信」。
- 注册表记录的是驱动可见容量，不保证等于物理容量（实测偏差可达约 1%），因此容量判定必须配合交叉证据，不能只看「对不上」。
- 同名多块显卡的注册表容量不一致时，真实容量视为不可判定，显存择源退化为默认通道。
- 核显与独显的 `GPU.Load` 口径不同，横向对比无意义；核显的显存指标是共享显存占用，与独显的专用显存占用不是同一口径。
- 同一硬件节点上的多个同语义传感器只有首个被认领，其余被丢弃（先到先得），除非显卡键冲突时的显式优先级规则介入。
- 主板来源的 CPU 电压随 3 秒慢扫描刷新，其更新频率低于 CPU 节点来源。
- 电池功率与电流的符号由是否接外部电源决定，无法反映驱动上报的原始方向。
