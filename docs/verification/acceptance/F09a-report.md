# F09a 验收记录 —— 原生依赖候选验证（#502 / #579 决策记录）

> 工作包 **P-F09a**（只读研究包）。本包**不改产品源码**（`src/**`、`LiteMonitor.csproj` 一字未动）。
> 产物是一份**决策记录** + 分支上的**受控验证工程**（`tools/F09aDependencyProbe/`）。
> 本报告是 P-F09b（依赖集成）、P-F08b（磁盘允许列表 spike）的直接前置输入。

## 1. 标识

- 工作包：P-F09a —— 原生依赖候选验证（只读研究）
- 分支：`research/F09a-dependency-candidate`（试验性，**不回合并 master**）
- 基线 SHA（开工时 HEAD）：`d52c0ab`（master）
- 完成日期：2026-10-25
- 执行者：worker 会话 `mvs_1eb45d65a62c4487b90d285ffb614bc3`（协调者：P-F09a 派发）
- 工作目录：`G:\TOOL\LiteMonitor-fork\.worktrees\f09a`（主仓库只读）

## 2. 覆盖与结论

- 覆盖 issue：**#502**（NVML `NvmlDeviceGetPowerUsage` AccessViolation）、**#579**（后台线程 NRE 导致闪退）——**决策前置，不关闭**。
- 结论：☒ **代码层验收通过**（研究包层面）
- 允许关闭的 issue 与条件：
  - **#502**：**不允许关闭**。本包只证明"候选包内**确实含**目标修复"且"在真机双卡上行为差异成立"；
    关闭仍需 P-F09b 在真实驱动重置/设备移除下实测通过（母计划 §6 要求 NVIDIA 实机）。
  - **#579**：**不允许关闭**。本包证明 1.1.2 库线程异常会带走进程、2.x 库内已隔离；
    关闭仍需 P-F09b 在真实磁盘热插拔事件下实测通过。
  - **#582**：**不声称覆盖**（仍缺原堆栈）。

### 2.1 一句话结论（给 P-F09b 的路线）

> **推荐组合**：`LibreHardwareMonitorLib 0.9.7-pre771`（传递 DiskInfoToolkit 2.2.2 / RAMSPDToolkit-NDD 1.6.4）
> —— 它是**唯一一个既含 PR #2539 NVML 句柄修复、又把 DiskInfoToolkit 升到 2.x（库内已有异常边界与可停止机制）**的已发布包。
> **回退组合**：`0.9.7-pre767`（DiskInfoToolkit 2.2.1）—— 若 pre771 的传递依赖升级面过大则退到它；
> **再回退**：保留 0.9.6 + 对 LHM 打固定源码补丁（仅解决 #502，**不解决 #579**），
> 或 0.9.6 + 固定源码补丁 DiskInfoToolkit 1.1.2 异常边界（需私有维护 fork）。

## 3. 判定方法（为什么这不是"凭版本号推断"）

四层互相独立的证据：

1. **构建 commit 可追溯**：LHM 程序集的 `AssemblyInformationalVersion` 内含**构建时的源码 commit SHA**
   （例：`0.9.7-pre746+3aa9c6c5bfc45fd15f4166bd35d73609b2bb126c`）。
2. **祖先关系判定**：用 GitHub compare API 判定该 commit 与 PR #2539 合并 commit
   `3aa9c6c5bfc45fd15f4166bd35d73609b2bb126c` 的关系（`behind` / `identical` / `ahead`）。
3. **包内程序集反编译**：用 ILSpy 内核（`ICSharpCode.Decompiler 11.1.0.9782`）**反编译包里实际分发的
   `runtimes/win-x64/lib/net8.0/LibreHardwareMonitorLib.dll`**，逐条比对 PR 的结构性指纹，
   并 dump `NvidiaGpu.Update()` 的 IL 调用序列。
4. **真机故障注入**：`tools/F09aDependencyProbe/` 在本机双 NVIDIA 上注入故障，
   比较两个候选的**可观测行为**（而不是版本号）。

> 指纹定义（来自 PR #2539 diff，`.tmp/evidence/pr2539.diff`）：
> `NvidiaML` 新增 `public static object SyncRoot`；`NvidiaGpu` 把 `readonly _nvmlDevice`
> 换成 `readonly string _nvmlPciBusId` + `_busId` + `_busIdKnown`，并新增私有方法
> `GetNvmlDevice(bool allowIndexWithoutBusId = false)`。

## 4. 逐候选判定

### 4.1 候选 A —— 固定源码补丁（LHM 0.9.6 / DiskInfoToolkit 1.1.2 自行打补丁）

| 项 | 判定 |
|---|---|
| 是否含目标修复 | **不适用**（补丁是自制的）；但**证明必要性**：0.9.6 程序集**确定不含**修复 |
| 直接证据 | `NvidiaGpu._nvmlDevice : Nullable<NvmlDevice>` 属性 = `Private, InitOnly`（readonly 缓存字段）；`NvidiaML` 无 `SyncRoot` 属性；`NvidiaGpu.Update()` 的 IL 调用序列为 `get_IsAvailable → NvmlDeviceGetPowerUsage → NvmlDeviceGetPcieThroughput×2`，**没有任何重新取句柄的调用** |
| #502 能否解决 | **能**（回移 PR #2539 的 2 个文件即可：`NvidiaGpu.cs` +143/−105、`NvidiaML.cs` +5） |
| #579 能否解决 | **能，但成本极高**：异常边界在 `DiskInfoToolkit` **库内部**（1.1.2 的 `StorageManager.DevicesChangedListener()` 循环体无 try/catch），应用层 catch 结构上覆盖不到 → 必须**私有 fork 整个 DiskInfoToolkit 1.1.2** 并长期自行维护 |
| 维护/升级风险 | **高**：私有 fork 与上游脱节；任何 NuGet 升级都会被覆盖；DiskInfoToolkit 2.x 的 `ExcludeUsbDevices`/`StopMonitoring` 等能力全部拿不到 |
| 结论 | **只作为回退手段**，不建议作为主路线 |

### 4.2 候选 B —— 已发布 LHM 包（含"1.0.x 预发布"这一问的直接回答）

> **答**：`nuget.org` 上 `LibreHardwareMonitorLib` **不存在任何 1.0.x**；版本序列止于 `0.9.7-pre771`。
> **最新稳定版仍是 0.9.6**（GitHub release `v0.9.6`，2026-02-14），**早于** PR #2539 的合并（2026-09-29）。
> ⇒ **没有任何稳定版包含该修复**；修复只存在于**预发布包**中。

**第一个含该修复的已发布包 = `0.9.7-pre746`**（关键证据）：

| 版本 | 程序集 InformationalVersion（内含 commit SHA） | GitHub compare vs `3aa9c6c5` | 程序集指纹 | 含修复 |
|---|---|---|---|---|
| `0.9.6` | `0.9.6+3d331e3370efb858411f19511373eff65a218701` | **behind**（merge_base=`3d331e3370`） | 无 `SyncRoot`；有 readonly `_nvmlDevice`；无 `GetNvmlDevice` | ❌ |
| `0.9.7-pre745` | `0.9.7-pre745+80e4d9d7264025f5e0ab5f289523e1bf1e0cd535` | **behind** | 同上 | ❌ |
| **`0.9.7-pre746`** | `0.9.7-pre746+3aa9c6c5bfc45fd15f4166bd35d73609b2bb126c` | **identical**（merge_base=`3aa9c6c5bf`） | 有 `SyncRoot`；有 `_nvmlPciBusId`/`_busId`/`_busIdKnown`；有 `GetNvmlDevice`；**无** `_nvmlDevice` | ✅ |
| `0.9.7-pre747` | `0.9.7-pre747+677a3a56abde9adff5abdb42db3a7638c9137572` | ahead | 同上 | ✅ |
| `0.9.7-pre767` | `0.9.7-pre767+e68a7ef7326fa875210e89e1402d93a45ffe4d3e` | ahead | 同上 | ✅ |
| `0.9.7-pre771` | `0.9.7-pre771+feb0e0ee33cbc32aba6606aea7d1e9bf5ab8d5fd` | ahead | 同上 | ✅ |

> `0.9.7-pre746` 的 commit **就是 PR #2539 的合并 commit 本身**（compare 判定 `identical`）——
> 这是"修复已进入某个已发布 NuGet 包"的最强证据。
>
> **对"已合并 ≠ 已发布"这一疑问的最终回答**：*已合并*（2026-09-29）确实*已发布*进 NuGet，
> 但**只发布在预发布包**（自 `0.9.7-pre746` 起），**任何稳定版都没有**。
> 因此 P-F09b 若走"包锁定"路线，**必须接受一个预发布版本号**（或自己 fork 构建）。

**IL 级直接证据（`NvidiaGpu.Update()` 调用序列）**：

```
0.9.6         : call NvidiaML::get_IsAvailable
                 call NvidiaML::NvmlDeviceGetPowerUsage
                 call NvidiaML::NvmlDeviceGetPcieThroughput  ×2          ← 复用构造期缓存的句柄
0.9.7-pre767  : call NvidiaML::get_SyncRoot                            ← 生命周期锁
                 call NvidiaGpu::GetNvmlDevice                         ← 每轮重新解析
                 call NvidiaML::NvmlDeviceGetPowerUsage
                 call NvidiaML::NvmlDeviceGetPcieThroughput  ×2
```

`GetNvmlDevice` 的 IL 调用序列（pre767）：
`get_IsAvailable → Initialize → String::IsNullOrEmpty → NvmlDeviceGetHandleByPciBusId
 → NvmlDeviceGetHandleByIndex → NvmlDeviceGetPciInfo` ⇒ **按 PCI 身份取句柄，取不到才按序号回退并校验 PCI bus**。

### 4.3 候选 C —— DiskInfoToolkit 2.2.1 / 2.2.2

| 项 | 1.1.2（现状） | 2.2.1 | 2.2.2 |
|---|---|---|---|
| API 形态 | `StorageManager`（静态）+ `Storage`（实例）+ `StoragesChanged` 事件 | `Storage`（**纯静态**）+ `DevicesChanged` 事件 | 同 2.2.1 |
| 设备枚举 | `StorageManager.ReloadStorages()`，并**逐个探测 `\\.\PhysicalDrive0..63`** | `Storage.GetDisks()` → `StorageDetectionEngine.GetDisks(excludeUsbDevices)` | 同 |
| 库自带线程 | `DevicesChangedListener` + `MessageLoop`（静态构造器启动） | `Storage.RescanLoop` + `Storage.MessageLoop` + `Storage.MediaWatchLoop`（`StartMonitoring` 启动） | 同 |
| **线程异常边界** | **无**：`DevicesChangedListener()` 的 while 循环体**没有 try/catch** | **有**：`RescanLoop` 的 `HandleStorageTopologyChanged()` / `HandlePartitionRefresh()` 均被 `try{...}catch{}` 包住；`MediaWatchLoop` 同 | 同（catch 仍是**空 catch，不写日志**） |
| 停止机制 | **无**（静态线程无法停） | `Storage.StopMonitoring()`（置位 `MonitoringStopSignal` + `Join(5000)`） | 同 |
| USB 排除 | 无 | `Storage.ExcludeUsbDevices`（默认 false，检测/刷新/监控三处均生效） | 同 |
| 失败隔离 | 无 | `MaxConsecutiveRefreshFailureCount`（默认 3） | 同 |
| 相对上一版新增 | — | — | **唯一公开面变化**：新增 `public static Logger Logger { get; }`（可用于把库内异常接出来做诊断） |

**与 LHM 的配套关系**：`0.9.7-pre767` 的 nuspec 声明 `DiskInfoToolkit >= 2.2.1`；`pre771` 声明 `>= 2.2.2`。
LHM 的适配代码也从 1.x 迁到 2.x：

```
0.9.6  StorageGroup: StorageManager.StoragesChanged -= …; StorageManager.ReloadStorages();
                    _hardware.AddRange(StorageManager.Storages.Select(s => new StorageDevice(s, GetID(s), settings)));
       （旧版 StorageGroup.Close() 为空）
pre767 StorageGroup: Storage.DevicesChanged -= …; List<StorageDevice> disks = Storage.GetDisks();
                    _hardware.AddRange(disks.Select(s => new StorageDevice(s, settings)));
       Close(): Storage.DevicesChanged -= …; foreach (… ) item.Close();
```

## 5. API 面差异（PawnIO / Intel GCL / 磁盘访问）

`0.9.6` vs `0.9.7-pre767` 的公开/内部 API 差异共 **+110 / −23** 条签名，摘要：

| 领域 | 差异 | 对 fork 的影响 |
|---|---|---|
| **PawnIO** | 公共面**未变**（`PawnIo` / `IPawnIOModule` / `PawnIOSMBusIdentifier` / `LoadModule(...)` 一致）；**新增** NVIDIA 侧 `TryReadMemoryTemperatures`（48 路显存温度）、`TryReadThermalChannels`（6 路热通道）、`PawnIo.Nvidia` 由 class 变 sealed class | **无破坏**；新增能力（可用于 #502 的"降级模式"，本轮不用） |
| **Intel GCL** | `IntelGcl`（internal static）与 `IntelDiscreteGpu(IntelGcl.ctl_device_adapter_handle_t, ISettings)` **逐字一致** | **无破坏**；#592/#596/#569 的 IGCL 通路不受升级影响 |
| **NVML** | 新增 `NvidiaML.SyncRoot`；`NvidiaGpu` 内部字段/方法重构（见 §4.2） | 仅内部；fork 不直接使用 |
| **磁盘** | `StorageDevice` 构造签名 `StorageDevice(Storage, string, ISettings)` → `StorageDevice(StorageDevice, ISettings)`（**磁盘身份 ID 的生成方式改变**）；`SmartAttribute` 改包 `SmartAttributeEntry`；`SmartAttributeTranslator`（internal）被移除；`GetStorageDeviceSensorValue(Storage)` → `(StorageDevice)` | **不触及产品源码**（见下），但**影响磁盘传感器命名与身份**，需回归 |
| **电池** | `Battery(name, manufacturer, SafeFileHandle, BATTERY_INFORMATION, uint, ISettings)` → 多一个 `string devicePath` | 不触及产品源码 |
| **RAMSPD** | `DeviceConfigStructV2` → `DeviceConfigStructV3`（+ 3 个转换函数）；`UiConfigStruct` → `UiConfigStructV1/V2`；`ThrottleInterval` 移除；`AppedThread` → `AppendThread` | 不触及产品源码 |
| **GPU 组** | `NvidiaGroup : IGroup` → `NvidiaGroup : IGroup, **IHardwareChanged**`（GPU 组开始响应硬件变化） | 与 F10（GPU 运行时自愈）方向一致，是**加分项** |

**fork 源码兼容性（已核实）**：`src/**` 里所有 LHM 引用都是 `using LibreHardwareMonitor.Hardware;`，
实际用到的类型只有 `Computer` / `IHardware` / `ISensor` / `SensorType` / `HardwareType`。
上表中所有破坏性变更都在 `LibreHardwareMonitor.Hardware.Storage.*` / `Battery` / RAMSPD / PawnIO 内部，
**产品源码不需要改一行**。`Computer.Hardware` 两版都是 `IList<IHardware>`（产品 `HardwareMonitor.cs:1291`
的 `Hardware.Clear()` 仍然可用）。

## 6. 受控验证工程与真机结果

工程：`tools/F09aDependencyProbe/`（三个变体共用 `src/Probe.Shared/ProbeRunner.cs`）
运行器：`tools/F09aDependencyProbe/scripts/run-probe.ps1`（每个场景一个独立子进程）

### 6.1 环境

- OS：Windows 11（10.0.26300 x64）
- GPU：**双 NVIDIA** — GeForce RTX 3080（PCI `DEV_2206`，bus `0000:2D:00.0`）、GeForce RTX 3080 Ti（`DEV_2208`，bus `0000:2E:00.0`），驱动 32.0.16.1088
- 磁盘：3 块内置 NVMe（**无 USB/外置盘**）→ 磁盘探测场景不会唤醒外置设备
- .NET SDK 8.0.426

### 6.2 结果矩阵

| 场景 | Probe.Baseline（LHM 0.9.6 / DIT 1.1.2） | Probe.Fixed（0.9.7-pre767 / DIT 2.2.1） | Probe.FixedLatest（0.9.7-pre771 / DIT 2.2.2） |
|---|---|---|---|
| `fingerprint` | `SyncRoot=False`、`_nvmlDevice=True(InitOnly)`、`GetNvmlDevice=False`、`ExcludeUsbDevices=False` | `SyncRoot=True`、`_nvmlDevice=False`、`_nvmlPciBusId=True`、`GetNvmlDevice=True`、`ExcludeUsbDevices=True` | 同 Fixed |
| `nvml-smoke` | 2 张卡（3080 / 3080 Ti，bus `0000:2D:00.0` / `0000:2E:00.0`）；按序号取到的两个句柄**互不相同**（`0:140711849138024,1:140711849740624`）；按 PCI 身份取句柄成功；功耗 79.4 W / 8.7 W；伪造 PCI 身份 `0000:FF:00.0` → **取不到句柄** | 同左（80.0 W / 8.4 W），伪造身份同样取不到句柄 | 同左（76.3 W / 8.4 W） |
| `nvml-stale-handle`（句柄失效注入） | 传感器值清空 + 缓存句柄置空后，**3 轮全为 `(null)`，永不恢复**（`powerRecovered=False`） | 同样的注入后，**第 1 轮即恢复真实功耗**（75.2 W / 9.5 W，`powerRecovered=True`） | 同 Fixed（74.4 W / 9.1 W，`powerRecovered=True`） |
| `nvml-invalidate`（NVML 置不可用） | 读数**冻结在旧值**（78.4 / 10.0 三轮不变），`powerReadoutChanged=False` ⇒ **无失效处理，只会一直发布陈旧值** | 自动重新初始化并**继续刷新**（76.8 → 77.6），`powerReadoutChanged=True` | 同 Fixed（70.6 → 57.0，`powerReadoutChanged=True`） |
| `disk-thread-fault`（库线程故障注入） | **进程被带走**：exit `-1073741819`（`0xC0000005`），stderr：`Unhandled exception. System.NullReferenceException … at DiskInfoToolkit.StorageManager.HandleUnpartitionedDrive(DeviceChangedModel) at DiskInfoToolkit.StorageManager.DevicesChangedListener()` | 进程存活（exit 0），`RescanLoop` 线程存活（`modernRescanThreadAlive=True`） | 同 Fixed |
| `disk-callback-fault`（设备变更回调注入） | 见 §6.3（受控环境未能触发该事件） | 注入的回调异常**被库内吞掉**：回调确实触发（`modernCallbackInvocations=1`），进程存活、线程存活 | 同 Fixed |

> 完整矩阵（3 变体 × 6 场景 = 18 次独立子进程）的机器可读结果：`.tmp/probe-results/summary.json`
> （scratch，不入库；可用 `tools/F09aDependencyProbe/scripts/run-probe.ps1` 重现）。
> 唯一非零退出码是 `Probe.Baseline / disk-thread-fault = -1073741819`，其余 17 次均为 0。

### 6.3 关于 1.1.2 回调注入的说明（如实记录）

1.1.2 的 `StoragesChanged` 事件只在**真实设备变更**（盘符添加/移除、拓扑变化）时由库线程触发。
受控环境下两次尝试均未触发该事件：

- 触发"拓扑变化"路径（`DevicesChanged`）：库要先逐个探测 `\\.\PhysicalDrive0..63`
  并对每个设备做识别，**90 s 内事件未触发**（`_Storages` 计数一直为 0）。
- 触发"盘符移除"路径（注入 8 个假 `Storage` 记录 + 投递 `Removed` 事件，
  `_Storages` 计数确实变为 8）：事件仍未触发——`\\.\C:` 的打开在**非提权**会话下失败，
  `HandleDriveWithDriveLetterRemoved` 直接 return。

⇒ **但 #579 的关键机制已经被 `disk-thread-fault` 直接证实**：同一个库线程
（`StorageManager.DevicesChangedListener`）上出现的未捕获异常会**直接终结进程**（exit `0xC0000005`），
而应用层 `catch` 结构上覆盖不到该线程——堆栈形态与 #196/#579 报告一致。
1.1.2 的回调路径同样位于这个**无异常边界的线程**上（见 §4.3 的反编译证据），
因此"应用回调异常同样会终结进程"是**结构性结论**，本包把它标为**待实机**（需真实热插拔）。

### 6.4 本包执行的命令与结果

| 命令 | 目的 | 结果 |
|---|---|---|
| `dotnet build LiteMonitor.csproj -c Debug --nologo` | 确认产品工程未被验证工程污染 | **0 错误**（301 个既有警告，与基线一致） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug` | 产品回归（本研究包未改产品代码） | **失败 0 / 通过 1777 / 跳过 7 / 总计 1784** |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/… -c Debug` | 同上 | **失败 0 / 通过 19 / 跳过 0 / 总计 19** |
| `dotnet test tests/LiteMonitor.Updater.Tests/… -c Debug` | 同上 | **失败 0 / 通过 73 / 跳过 0 / 总计 73** |
| 三个测试项目合计 | 与 CI 阈值对照 | **总计 1876**（1777+19+73 通过，7 跳过）＝ CI 的 `MIN_TEST_TOTAL: 1876`，**无需上调** |
| `pwsh tools/F09aDependencyProbe/scripts/run-probe.ps1` | 受控验证矩阵（3 变体 × 6 场景） | 18 次独立子进程，**17 次 exit 0**；唯一非零：`Probe.Baseline / disk-thread-fault = -1073741819 (0xC0000005)` |
| `git status` | 禁改核对 | `src/**`、`LiteMonitor.csproj`、`tests/**`、`.github/workflows/*` 无改动 |

## 7. 传递依赖变化与风险逐条

### 7.1 传递依赖对照（net8.0）

| 包 | 0.9.6（现状） | 0.9.7-pre767 | 0.9.7-pre771 | 风险 |
|---|---|---|---|---|
| DiskInfoToolkit | 1.1.2 | **2.2.1** | **2.2.2** | **大**：API 断裂（见 §5）；磁盘枚举/识别行为改变 |
| HidSharp | 2.6.4 | 2.6.4 | 2.6.4 | 无 |
| Mono.Posix.NETStandard | 1.0.0 | 1.0.0 | 1.0.0 | 无 |
| RAMSPDToolkit-NDD | 1.4.2 | **1.6.2** | **1.6.4** | 中：`DeviceConfigStructV2→V3`，`UiConfigStruct` 换名 |
| System.IO.Ports | 10.0.3 | **10.0.13** | **10.0.13** | 低 |
| System.Management | 10.0.2 | **10.0.13** | **10.0.13** | 低 |
| System.Threading.AccessControl | 10.0.3 | **10.0.13** | **10.0.13** | 低 |

### 7.2 风险清单

1. **主版本跨越（0.9 → 0.9.7 预发布）**：语义化上仍属 0.9.x，但预发布号意味着
   **上游可随时重发/撤回**（NuGet 允许同一版本号重发布）。必须**锁定版本 + 记录 commit SHA**
   （`0.9.7-pre771+feb0e0ee33cbc32aba6606aea7d1e9bf5ab8d5fd`），并把 SHA 写进锁文件/文档。
2. **磁盘 API 断裂**：`StorageManager` 在 2.x **不复存在**。任何"只把 DiskInfoToolkit 提升到 2.x、
   保留 LHM 0.9.6"的做法都会在运行期 `MissingMethodException/TypeLoadException` —— **禁止**手工覆盖 DLL。
3. **传递依赖覆盖**：产品 `LiteMonitor.csproj` 目前固定 `System.Text.Json 10.0.3`
   （被 `Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0` 强制）；新 LHM 要求 `System.* 10.0.13`。
   还原期需确认 **无 NU1605 降级**；若出现，优先升 `System.Text.Json` 而不是钉死旧 `System.*`。
4. **磁盘传感器语义变化**：SMART 属性 → 传感器的映射从 LHM 内部 `SmartAttributeTranslator`
   换成 DiskInfoToolkit 2.x 的 `SmartAttributeEntry` 目录，**磁盘传感器名/数量可能变化**
   （fork 的 `DiskManager` 按名字取 `Temperature` / 读写速率）。**必须做磁盘传感器名回归**。
5. **磁盘身份语义变化**：`StorageDevice` 的 id 生成方式从 `GetID(s)` 变为 2.x `StorageDevice` 自带身份
   ⇒ 影响 F07（设备身份目录）与 F08b（允许列表）的键设计；`HardwareScanner` 用的是 `hw.Name`，
   需确认 `hw.Name` 是否稳定。
6. **磁盘探测行为变化**（与 #455/#132 相关）：1.1.2 逐个探测 `PhysicalDrive0..63`；
   2.x 改用 `StorageDetectionEngine` 枚举。**对"外置盘唤醒"是否有改善，本包未验证**（本机无外置盘）⇒ 标"待实机"。
7. **2.x 的异常被静默吞掉**：`RescanLoop` / `MediaWatchLoop` 的 catch 是**空 catch**。
   ⇒ 库不再崩，但**故障不可见**。必须由 fork 侧补可观测性（2.2.2 新增 `Storage.Logger` 是接入点）。
8. **未覆盖的原生故障**：PR #2539 的锁只约束本进程的库调用，**无法阻止驱动在外部重置**；
   真实 `AccessViolationException` 仍然可能发生（进程隔离设计不在本轮，见母计划 §6）。
9. **预发布依赖的下游一致性**：`0.9.7-pre746..pre771` 之间的 DiskInfoToolkit 从 2.2.1 到 2.2.2、
   RAMSPD 1.6.2 → 1.6.4，**每个预发布号都可能带不同的传递依赖** ⇒ 锁版本时必须同时记录 nuspec 依赖表。
10. **Intel GCL / PawnIO**：本次比对未发现破坏性变更（§5），但 IGCL 遥测不可用（#592）等问题
    **与本次升级无关**，升级后仍需按 F09b 的回归清单复测。

## 8. 推荐组合与回退组合

### 8.1 推荐组合（Primary）

```
LibreHardwareMonitorLib 0.9.7-pre771
  ├─ DiskInfoToolkit 2.2.2
  ├─ RAMSPDToolkit-NDD 1.6.4
  └─ System.IO.Ports / System.Management / System.Threading.AccessControl 10.0.13
```
理由：
- **含 PR #2539**（commit `feb0e0ee…` 相对 `3aa9c6c5` 为 ahead，程序集指纹已验证）→ #502 根因修复在位；
- **DiskInfoToolkit 2.2.2** → #579 的库线程异常边界 + `StopMonitoring` + `ExcludeUsbDevices` 全部到位；
- 2.2.2 额外提供 `Storage.Logger` → fork 可把库内被吞掉的异常接出来做可观测性（补齐 §7.2 第 7 条）。

### 8.2 回退组合（Fallback）

```
LibreHardwareMonitorLib 0.9.7-pre767   （DiskInfoToolkit 2.2.1 / RAMSPD 1.6.2）
```
触发条件：pre771 的传递依赖升级（RAMSPD 1.6.4 / System.* 10.0.13）在 CI 或实机引发回归。
pre767 同样含 PR #2539（commit `e68a7ef7…`），#502/#579 的核心能力不打折，
仅少一个 `Storage.Logger` 接入点。

### 8.3 二次回退（Patch-only，不推荐作为主路线）

```
LHM 0.9.6 + 回移 PR #2539（NvidiaGpu.cs / NvidiaML.cs 两个文件，来源 SHA 3aa9c6c5）
  → 只解决 #502
  → #579 需私有 fork DiskInfoToolkit 1.1.2 加异常边界（长期维护成本高）
```

### 8.4 给 P-F09b / F08b 的配套要求

1. 锁版本时同时写死 **版本号 + commit SHA**（例如 `0.9.7-pre771+feb0e0ee33cbc32aba6606aea7d1e9bf5ab8d5fd`）。
2. fork 侧仍需自建"应用层诊断"（设备代次/操作/来源版本/错误类型，限频、不落序列号）——
   库内 catch 是空的，**不能把"库不崩了"当成"故障可观测"**。
3. F08b 的磁盘允许列表 spike 需在 **2.x 的 `Storage.GetDisks()` 之前**生效
   （`ExcludeUsbDevices` 必须在首次枚举前设置），并注意 2.x 已不再逐个探测 `PhysicalDrive0..63`。
4. #502 关闭条件仍是 **NVIDIA 实机**（本包只证明包内含修复 + 双卡行为差异成立）。
5. #579 关闭条件仍是 **真实磁盘事件场景**（本包证明的是"注入故障下的隔离能力"）。

## 9. 可重现构建步骤

> ★ **验证工程放在 `tools/F09aDependencyProbe/`（不是 `research/`）**：
> 根 `LiteMonitor.csproj` 的 `Compile` 默认通配是 `**/*.cs`，只排除了
> `LiteMonitor.Updater/**`、`LiteMonitor.Wpf/**`、`tests/**`、`tools/**`、`docs/**`、
> `.artifacts/**`、`.worktrees/**`、`.review-scratch/**`、`undefined/**`。
> 早期把探针放在 `research/` 下时，**产品工程会把探针的 `Program.cs` / `ProbeRunner.cs`
> 一起编译进去**，实测报 `CS8802 顶级语句只能有一个` / `CS0579 特性重复`，
> 直接**打断产品构建**。`tools/**` 已被排除，因此放在 `tools/` 下与仓库既有探针
> （`tools/*Probe`）同一族，且不需要改动被禁改的 `LiteMonitor.csproj`。

```powershell
$env:NUGET_PACKAGES='G:\TOOL\LiteMonitor-fork\.nuget-packages'
$env:DOTNET_CLI_HOME='G:\TOOL\LiteMonitor-fork\.dotnet-cli-home'

# 1) 受控验证工程（三个候选各一个工程）
cd G:\TOOL\LiteMonitor-fork\.worktrees\f09a\tools\F09aDependencyProbe
dotnet build src\Probe.Baseline\Probe.Baseline.csproj     -c Release   # LHM 0.9.6
dotnet build src\Probe.Fixed\Probe.Fixed.csproj             -c Release   # LHM 0.9.7-pre767
dotnet build src\Probe.FixedLatest\Probe.FixedLatest.csproj -c Release   # LHM 0.9.7-pre771

# 2) 全场景矩阵（每场景一个独立子进程）
pwsh -NoProfile -Command "& '.\scripts\run-probe.ps1'"

# 3) 单场景
dotnet src\Probe.Fixed\bin\Release\net8.0\Probe.Fixed.dll nvml-stale-handle --cycles 3
```

包内程序集取证（不依赖本工程）：

```powershell
# 取包并解出实际分发的 net8.0 程序集
Invoke-WebRequest https://api.nuget.org/v3-flatcontainer/librehardwaremonitorlib/0.9.7-pre746/librehardwaremonitorlib.0.9.7-pre746.nupkg -OutFile lhm.nupkg
# 用 ICSharpCode.Decompiler 11.1.0.9782 反编译 runtimes/win-x64/lib/net8.0/LibreHardwareMonitorLib.dll
# 判定：是否有 NvidiaML.SyncRoot / NvidiaGpu.GetNvmlDevice / NvidiaGpu._nvmlDevice
```

## 10. 新增用例清单

本包是研究包，**不新增产品测试**（`tests/LiteMonitor.Tests/` 未改动，`MIN_TEST_TOTAL` 无需调整）。
新增的是受控验证工程中的场景（非 xUnit，用例即下表 6 个）：

| 文件 | 场景 | 验证内容 |
|---|---|---|
| `tools/F09aDependencyProbe/src/Probe.Shared/ProbeRunner.cs` | `fingerprint` | 运行时结构指纹（句柄缓存字段 / SyncRoot / GetNvmlDevice / DiskInfoToolkit API 形态） |
| 同上 | `nvml-smoke` | 真机双卡枚举 + 按 PCI 身份取 NVML 句柄 + 逐轮功耗 + 伪造身份必须失败 |
| 同上 | `nvml-stale-handle` | 句柄失效注入后的恢复能力（基线不恢复 / 修复版第 1 轮恢复） |
| 同上 | `nvml-invalidate` | NVML 不可用时的读数行为（基线冻结陈旧值 / 修复版重新初始化并刷新） |
| 同上 | `disk-thread-fault` | 库自带线程上的未处理异常是否终结进程 |
| 同上 | `disk-callback-fault` | 设备变更回调异常是否被库内隔离 |

## 11. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| **真实驱动重置 / GPU 设备移除下的 #502 复现与验证**（PR #2539 的锁只能约束本进程调用，无法阻止驱动外部重置） | 能安全重启显卡驱动 / 热拔插 GPU 的机器 | 用户 + P-F09b |
| **真实磁盘热插拔 / 掉盘下的 #579 复现与验证**（1.1.2 的事件只在真实设备变更时触发） | 可热插拔的 USB/外置盘 | 用户 + P-F09b |
| **真实 `AccessViolationException` 的原生隔离**（需要进程隔离设计，不在本轮） | 专用测试机 + WER LocalDumps | P-F09b 后续设计 |
| **2.x 枚举行为对"外置盘唤醒"（#455/#132）的改善** | 本机无外置盘，未测 | 用户 |
| **磁盘 SMART 传感器名/数量回归**（2.x 属性目录变化） | 在带 SMART 信息的机器上对比 0.9.6 与 2.x 的传感器清单 | P-F09b |
| **Intel GCL 遥测（#592/#596/#569）与 PawnIO 新能力** | Intel 核显/独显机器 | 用户 |
| **RAMSPD 1.6.x 行为**（内存条 SPD 读取） | 带 SPD 的内存条机器 | P-F09b |
| **多预发布号之间的传递依赖漂移**（pre746→pre771） | 已记录 nuspec 对照表，但未逐号验证 | P-F09b |

## 12. 偏离与遗留

- **与派发单的偏离**：产物落点从"`.tmp/` 或独立目录"改为 **`tools/F09aDependencyProbe/`**
  ——原因见 §9 首段（放在 `research/` 会打断产品构建，而 `LiteMonitor.csproj` 是禁改文件）。
  该目录与仓库既有 `tools/*Probe` 同族，且已被根 csproj 排除在编译之外。
  `src/**`、`LiteMonitor.csproj`、`tests/**`、`.github/workflows/*` 一字未动；
  新增文件仅在 `tools/F09aDependencyProbe/`（验证工程）与本报告。
- **禁改清单核对**：`.github/workflows/ci.yml`、`release.yml` 未改；`MIN_TEST_TOTAL: 1876`、
  `MAX_SKIPPED_TESTS: 1` 保持原值（研究包未新增产品测试，无需上调）。
- **未推送远端、未动 master、未合并其他分支**；所有改动只在 `research/F09a-dependency-candidate`。
- **发现的相邻问题（建议归属）**：
  1. DiskInfoToolkit 2.x 的库线程 `catch` 是**空 catch**，库内故障完全不可见 →
     建议 F09b 用 2.2.2 的 `Storage.Logger` 做接缝补可观测性。
  2. LHM 预发布包把 commit SHA 写进 `AssemblyInformationalVersion`，
     建议 P-F09b 在锁依赖时把该 SHA 一并记录进版本表（便于事后追溯"包里到底有没有那个修复"）。
  3. `NvidiaGroup` 已实现 `IHardwareChanged`（pre767 起），
     建议 F10（GPU 运行时映射自愈）评估是否可直接利用，而不是另建机制。
- **临时产物**（未入库，`.tmp/`）：反编译产物、nupkg、`asmprobe` 取证工具、探针运行结果。
