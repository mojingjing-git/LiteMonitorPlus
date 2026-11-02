# 低功耗模式规格

LiteMonitor++ 的低功耗模式是**呈现节奏策略**：它决定 GPU 路线在一段时间内允许出多少帧、是否暂停出帧，而不改写采集节拍与指标语义。本文描述该模式的档位定义与解析规则、各档对采集与显示的实际影响、Auto 档的触发条件与切换行为，以及策略必须遵守的边界与不变量。

适用对象是要调整呈现预算、接入供电状态、或在低功耗档位下扩展采集与插件行为的工程师。渲染路线的判定顺序与 GPU 渲染栈内部结构、采样节拍与缺失语义、插件与市场的信任边界不属于本文范围。文中出现的枚举值、原因字符串、字段名与方法名属于接口约定，必须按本文写法保持稳定。

---

## 1. 模块边界与职责

### 1.1 策略层

低功耗的全部判定规则集中在 `src/Rendering/Power/PowerPolicy.cs` 与 `src/Rendering/Power/PowerDisplayScheduler.cs`，命名空间 `LiteMonitor.Rendering.Power`。

| 类型 | 职责 |
| --- | --- |
| `PowerMode` | 档位枚举：`Off = 0`、`Auto = 1`、`Eco = 2` |
| `DisplayCadence` | 呈现预算结果：`(int Fps, bool Paused, string Reason)`。`Fps = 0` 只在 `Paused` 为真时出现，含义是「暂停呈现」 |
| `PowerPolicy` | 纯策略函数：档位 + 供电状态 + 可见性 ⇒ 呈现预算、动画帧率上限、采样周期下限 |
| `PowerDisplayScheduler` | 策略层与 `FrameScheduler` 之间的适配层：把预算落到帧调度器上 |

`PowerPolicy` 是**无状态纯函数集合**：它不读取系统状态、不持有窗口句柄、不持有计时器、不做 IO。供电状态与可见性由调用方作为参数注入 ⇒ 同一组输入必然得到同一份预算。

策略层的可执行契约是 `tests/LiteMonitor.Tests/PowerPolicyTests.cs` 与 `tests/LiteMonitor.Tests/PowerDisplaySchedulerTests.cs`。

### 1.2 依赖方向

- `src/Rendering/Power` 依赖 `src/Rendering/Gpu/Effects`（帧调度），**不**依赖 `src/Core`、`src/System`、`src/UI`。
- `src/Rendering/Presentation/GpuPresentationHostController` 依赖 `src/Rendering/Power`，是策略层的生产消费方。
- `src/Core`、`src/System`（采集层、网络流量、插件）**不**引用 `src/Rendering/Power` ⇒ 低功耗档位不进入采集层与插件层。

### 1.3 消费方：GPU 呈现宿主

`GpuPresentationHostController` 是策略层唯一的生产消费方：

- 宿主保存当前的档位与供电状态，作为呈现预算的输入。
- 注入入口是 `UpdatePowerContext(PowerMode mode, bool onBattery)`；它必须在宿主所属线程上调用，已释放的宿主调用会抛异常。
- 构造参数 `powerMode`（默认 `PowerMode.Off`）与 `onBattery`（默认 `false`）是同一份上下文的初始值。
- 应用点是 `ApplyPowerCadence()`，在窗口可见性变化（`Form.VisibleChanged`）与上下文注入之后调用；宿主未持有活动呈现会话或已释放时，该应用直接跳过。
- 生产宿主直接调用 `PowerPolicy.ResolveDisplay` 并自行维护 `FrameScheduler` 与呈现计时器；`PowerDisplayScheduler` 是同一层策略的另一种落地形式（只做目标帧率更新），没有生产调用方。

---

## 2. 档位定义与解析规则

### 2.1 三档语义

| 档位 | 枚举值 | 语义 |
| --- | --- | --- |
| `Off` | 0 | 不施加功耗预算。呈现节奏完全由主题目标帧率与窗口可见性决定 |
| `Auto` | 1 | 由供电状态决定是否施加预算：电池供电时压低帧率，非电池供电时按常规预算呈现 |
| `Eco` | 2 | 无条件压低帧率，与供电状态无关 |

### 2.2 取值来源与注入路径

- 档位是**进程内状态**：`src/Core/Settings.cs` 没有低功耗字段，情景字段白名单 `ScenarioFieldCatalog` 也不含该字段 ⇒ `settings.json` 没有对应键，设置界面也没有档位入口。
- 组合根是唯一的注入方。`UIController.CreateGpuPresentationHost` 构造 `GpuPresentationHostController` 时不传档位与供电状态 ⇒ **生产路径当前的档位恒为 `PowerMode.Off`，供电状态恒为非电池供电**。
- 预算随上下文变化即时重算：可见性变化与 `UpdatePowerContext` 调用都会触发一次重算。

### 2.3 未定义值与非法参数的回落

- 档位判定以兜底情形收口：未在枚举中定义的取值（`Off` / `Auto` / `Eco` 之外）按常规预算处理，既不降帧也不暂停，且不会抛异常 ⇒ 非法档位**绝不会**被解释成更激进的降频。
- 参数域是显式校验的：
  - 目标帧率必须落在 **1–240**，越界抛 `ArgumentOutOfRangeException`；
  - 采样周期必须为正值，越界抛 `ArgumentOutOfRangeException`。
- 帧调度器与策略层使用同一套帧率域（`FrameScheduler` 的目标帧率同样是 1–240）⇒ 策略产出的预算永远是合法输入。
- 工程约定：用户可编辑的 JSON 枚举（`Settings.SettingThemeMode` 的 `Coerce` 模式）一律在写入侧夹回合法值，非法值不得原样写回磁盘。档位若接入持久化配置，同样必须走这条路径。

### 2.4 预算原因字符串

`DisplayCadence.Reason` 是稳定的诊断字符串：

| 字符串 | 含义 |
| --- | --- |
| `hidden` | 窗口不可见，呈现暂停 |
| `eco` | Eco 档预算 |
| `auto-battery` | Auto 档且电池供电 |
| `normal` | 常规预算 |

---

## 3. 档位影响矩阵

### 3.1 呈现节奏（GPU 路线）

这是当前唯一被档位影响的层。

- 目标帧率来源是主题渲染配置的 `rendering.motion.targetFps`（`MaterialRuntimeSettings`，取值域 1–240，默认 30）。
- `PowerPolicy.ResolveDisplay(mode, onBattery, windowVisible, normalFps)` 的判定顺序：
  1. 窗口不可见 ⇒ 暂停（`Fps = 0`、`Paused = true`、`Reason = "hidden"`）。该规则**先于**档位判定且与档位无关 ⇒ Off 档同样在窗口隐藏时暂停呈现。
  2. `Eco` ⇒ `min(normalFps, 10)`（`PowerPolicy.EcoFps`）。
  3. `Auto` 且电池供电 ⇒ `min(normalFps, 15)`（`PowerPolicy.AutoBatteryFps`）。
  4. 其余情形（`Off`、`Auto` 且非电池供电、未定义档位）⇒ `normalFps`。
- 预算落地：`ApplyPowerCadence` 在预算为暂停时停止宿主自己的 8 毫秒 WinForms 计时器；恢复时先 `FrameScheduler.Reset()` 再启动计时器；预算帧率变化时调用 `FrameScheduler.UpdateTargetFramesPerSecond`（该调用会重置调度基准）。
- 暂停**不**销毁呈现会话：`Suspend` / `Resume` 生命周期、桌面捕获所有权与窗口句柄代次由宿主独立管理，与帧率预算无关。

### 3.2 硬件采样周期

当前不受档位影响。

- 实际采集周期由 `HardwareMonitor.ComputeEffectivePeriodMs()` 计算：`max(HardwareSamplingCoordinator.MinimumPeriodMs, Settings.RefreshMs)`；`HardwareSamplingCoordinator.MinimumPeriodMs` = 1000 毫秒，`Settings.RefreshMs` 小于等于 0 时按 `HardwareMonitor.DefaultRefreshMs`（1000 毫秒）处理；存在已鉴权的 Web 数据需求时周期强制为 1000 毫秒。
- 策略层给出了采样周期下限 `PowerPolicy.ResolveSampleInterval(mode, onBattery, normalInterval)`：`Eco` 档周期不得短于 **2 秒**；`Auto` 档且电池供电时不得短于 **1.5 秒**；其余情形保持原周期（用户设定的慢周期不被缩短）。**采集层当前不消费该结果** ⇒ 三档对真实采样周期没有任何影响。
- 跨进程口径：`SamplingConfig.RefreshMs` 是主进程向采集子进程下发的唯一周期输入；`SamplingConfig` 没有低功耗字段，IPC 协议 `IpcProtocol` 主版本 1、次版本 1。

### 3.3 界面刷新与任务栏

当前不受档位影响。

- 主界面刷新节拍：`UIController.ComputeUiRefreshIntervalMs(RefreshMs)` = `max(80, RefreshMs)`。
- 任务栏刷新节拍：`max(RefreshMs, 60)`。
- 界面计时器只负责重绘与重读**已发布的快照**，不驱动硬件访问 ⇒ 调整呈现帧率不改变读数的更新频率，也不会造成额外的硬件读取。

### 3.4 磁盘与网络采集

当前不受档位影响。

- 磁盘与控制器的探测档位分别由 `Settings.DiskProbeMode`（`DiskProbePolicy`）与 `Settings.ControllerProbeMode`（`ControllerProbePolicy`）决定，两者的非法取值各自按自身的回落规则处理。
- 后台磁盘扫描节奏为 `HardwareSamplingSchedule.DiskBackgroundScanInterval` = 10 秒，慢速传感器节奏为 `HardwareSamplingSchedule.SlowScanInterval` = 3 秒。
- 网络样本的保留窗口由 `NetworkSampleFreshnessPolicy` 给出：最多保留 3 个采样周期，上限 15 秒；采样周期由 `NetworkSampleFreshnessPolicy.ResolveSampleCycleSeconds(RefreshMs)` 从刷新周期推导 ⇒ 与档位无关。

### 3.5 插件调度

当前不受档位影响。

- 插件周期由 `PluginManager.GetEffectiveInterval` 计算：取用户自定义周期与模板 `execution.interval` 的较大者，且不低于模板 `execution.min_interval` 与 `PluginConstants.DEFAULT_INTERVAL`（1 秒）。
- 失败重试按 `PluginConstants.RETRY_INTERVAL_MS`（5000 毫秒）退避，上限 60000 毫秒。

### 3.6 总表

| 维度 | `Off` | `Auto` | `Eco` |
| --- | --- | --- | --- |
| GPU 呈现帧率 | 主题目标帧率（默认 30） | 电池供电 `min(目标帧率, 15)`；非电池供电同 `Off` | `min(目标帧率, 10)` |
| 窗口隐藏时的呈现 | 暂停 | 暂停 | 暂停 |
| 硬件采样周期 | 不变（`max(1000, RefreshMs)`） | 不变 | 不变 |
| 主界面 / 任务栏刷新 | 不变 | 不变 | 不变 |
| 磁盘与网络采集 | 不变 | 不变 | 不变 |
| 插件调度周期 | 不变 | 不变 | 不变 |
| 装饰动画帧率上限 | 调用方给定的 `normalFps` | 电池供电 `min(normalFps, 15)`；非电池供电 `normalFps` | `min(normalFps, 10)` |
| 采样周期下限 | 不施加 | 电池供电不短于 1.5 秒 | 不短于 2 秒 |

⇒ 表中「装饰动画帧率上限」与「采样周期下限」两行是策略层已定义、**尚无生产调用方**的规则（`PowerPolicy.ResolveAnimationFps` 与 `PowerPolicy.ResolveSampleInterval` 只有单元测试覆盖）。三档在生产中当前只作用于 GPU 路线的呈现帧率与窗口隐藏暂停。

---

## 4. Auto 档的触发条件与切换行为

### 4.1 判定输入

`Auto` 的唯一判定输入是注入的 `onBattery` 布尔值：

- 电池供电 ⇒ 呈现预算压到 `min(normalFps, 15)`，原因字符串 `auto-battery`。
- 非电池供电 ⇒ 回落到常规预算，原因字符串 `normal`，呈现节奏与 `Off` 档一致。

预算由 `(档位, 供电状态, 窗口可见性)` 三元组共同决定 ⇒ 任意一项变化都会改变结果。未知供电状态没有独立表示，一律按非电池供电处理 ⇒ 未知状态**不会**被误判为电池供电而误降频。

### 4.2 供电状态的现有来源

- `MetricUtils.GetPowerStatus()` 通过 `SystemInformation.PowerStatus` 读取交流电与充电状态；读操作以 3000 毫秒为节流窗口，读取失败时保留上一次的结果 ⇒ 供电状态自带一个采样级的稳定窗口。
- 策略层不读取该状态：`PowerPolicy` 的供电输入只能由调用方注入。

### 4.3 迟滞与切换

- 策略层**不实现**迟滞：`Resolve*` 是无状态函数，每次调用都按传入的当前状态直接给出预算，既没有稳定时间，也没有状态记忆。
- 切换的实际时点由调用方决定：宿主只在可见性变化与 `UpdatePowerContext` 调用时重算；策略层不订阅电源事件，也不驱动轮询。
- 预算切换不涉及设备开关：`ApplyPowerCadence` 只改帧率与计时器状态，不触发设备 Open/Close、不请求显式重扫、不改变探测档位。

---

## 5. 策略边界与不变量

### 5.1 采样语义不被改写

- 档位只作用于呈现节奏：它**不得**改变指标键、单位、采样口径、缺失原因字符串与分组规则。
- 缺失与真实的零严格分离：缺失样本仍以 `SampleQuality.Missing` 与缺失原因发布，显示层渲染 `--`；真实的 0 仍是 `Good` 与 0。**低功耗档位不得让缺失值变成 0，也不得让 0 变成缺失。**
- 样本时效语义不被改写：样本年龄由该样本所属采集轮次（`BatchId`）的时间戳决定；降帧**不得**改变 `BatchId`、**不得**改变样本时间戳、**不得**补采错过的周期。
- 连续性不被伪造：降频期间**不得**插值、**不得**补跑、**不得**以旧样本充当新样本。

### 5.2 呈现与资源

- 0 FPS 只存在于策略结果中，**不得**传给 `FrameScheduler`（其构造与目标帧率更新都拒绝小于 1 的值）。
- 暂停呈现**不得**销毁呈现会话，**不得**改变捕获所有权与窗口句柄代次。
- 降帧**不得**触发设备重开、显式重扫或探测档位变化。

### 5.3 纯函数与注入

- 策略函数**必须**是纯函数：不读系统状态、不持窗口或计时器、不做 IO、不写盘。
- 供电状态、可见性与档位**必须**由组合根注入；策略层**不得**自行订阅系统事件。
- 运行时预算**不得**写回 `Settings`、主题文件或情景档案：用户配置是持久化的期望值，运行时预算是进程内的有效值，两者是不同状态。

### 5.4 声明边界

帧率与采样节拍的下降**不得**换算成瓦时、续航百分比或其它未测量的功耗结论。

---

## 6. 已知限制

- 三档模式没有持久化配置项与设置界面入口：`src/Core/Settings.cs` 无对应字段，情景字段白名单 `ScenarioFieldCatalog` 也不含该字段，`settings.json` 因此没有对应键。生产路径恒为 `PowerMode.Off` 与非电池供电 ⇒ 当前在生产中生效的只有「窗口隐藏即暂停呈现」这一条。
- 采样侧与动画侧的策略规则没有生产调用方：`PowerPolicy.ResolveSampleInterval` 与 `PowerPolicy.ResolveAnimationFps` 只被单元测试覆盖 ⇒ 三档对采样周期与装饰动画帧率都没有实际影响。
- 没有供电状态订阅与迟滞：策略层无状态、无去抖，供电状态变化要等外部调用 `UpdatePowerContext` 才会反映到呈现预算。
- `PowerDisplayScheduler` 没有生产调用方：生产宿主直接调用 `PowerPolicy.ResolveDisplay` 并自行维护帧调度器与呈现计时器。
- GDI+ 路线没有帧率预算：GDI+ 路线随界面刷新计时器重绘，档位对其无效果。
