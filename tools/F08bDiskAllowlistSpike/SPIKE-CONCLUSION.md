# P-F08b Spike —— 「仅创建/打开允许列表内磁盘」能力取证

> 用途：**决策门证据**。判定 LibreHardwareMonitor / DiskInfoToolkit 是否提供
> 「在**创建设备对象与打开句柄之前**排除非允许列表磁盘」的入口。
> 判定标准来自派发单：排除必须发生在库创建设备对象与打开句柄**之前**；
> 「构造完整设备列表后再过滤」视为**未隔离**（句柄已经开过了）。

## 0. 结论（一句话）

**☒ Spike 未通过 —— 现有库（含 F09a 推荐的 0.9.7-pre771 / DiskInfoToolkit 2.2.2）都做不到。**

两个变体的磁盘枚举入口均为**无参数的整体枚举**：调用方拿到的是「库已经全部构造好、
且已经开过句柄」的磁盘清单。库侧**只提供 `ExcludeUsbDevices` 这一布尔开关**
（2.x 才有，1.1.2 连这个都没有），**不提供任何"允许列表 / 逐设备过滤委托"**。
因此「仅内部盘 / 明确允许列表的 SMART 模式」**本轮不开放**，
本包只交付 controller 策略部分（#245 缓解），并在验收报告中注明。

## 1. 取证方法

受控验证工程 `tools/F08bDiskAllowlistSpike/`（三个变体共用 `src/Probe.Shared/SpikeRunner.cs`）：

| 变体 | 依赖 | 作用 |
|---|---|---|
| `Probe.Baseline` | LHM **0.9.6** / DiskInfoToolkit **1.1.2** | 当前生产依赖 |
| `Probe.Latest` | LHM **0.9.7-pre771** / DiskInfoToolkit **2.2.2** | F09a 推荐组合（问：升级后能否拿到？） |

命令（每变体一个独立进程，避免同进程加载两版 DiskInfoToolkit）：

```powershell
$env:NUGET_PACKAGES='G:\TOOL\LiteMonitor-fork\.nuget-packages'
$env:DOTNET_CLI_HOME='G:\TOOL\LiteMonitor-fork\.dotnet-cli-home'
dotnet build tools\F08bDiskAllowlistSpike\src\Probe.Baseline -c Release
dotnet build tools\F08bDiskAllowlistSpike\src\Probe.Latest   -c Release
dotnet tools\F08bDiskAllowlistSpike\src\Probe.Baseline\bin\Release\net8.0\Spike.Baseline.dll facts
dotnet tools\F08bDiskAllowlistSpike\src\Probe.Latest\bin\Release\net8.0\Spike.Latest.dll facts
```

场景：`facts`（库公开面）／`handles`（原生句柄声明与调用点反查）／`il <Type>!<Method>`（调用序列）／`type`／`searchtype`。

## 2. 证据 A —— 枚举入口没有过滤形参（判定核心）

### 2.1 Baseline（生产依赖 0.9.6 / DIT 1.1.2）

`facts` 输出的判定行：

```
== 是否存在'允许列表/过滤委托'形参（这是本次 spike 的判定核心） ==
  结论：DiskInfoToolkit **没有任何** Predicate/Func<T,bool> 形式的过滤形参 ⇒ 无法在枚举前按允许列表裁剪。
```

枚举入口本体（`DiskInfoToolkit.StorageManager`）：

```
static? True List`1 get_Storages()
static? True Void ReloadStorages()
prop List`1 Storages { get; set; }
```

⇒ 唯一入口是「重载全部 → 取全部清单」，**没有任何入参**。

### 2.2 Latest（F09a 推荐 0.9.7-pre771 / DIT 2.2.2）

```
static? True List`1 GetDisks()
prop Boolean ExcludeUsbDevices { get; set; }
prop List`1 CurrentDisks { get; }
```

`Storage.GetDisks()` **无参数**；唯一的排除能力是全局布尔 `ExcludeUsbDevices`。
`facts` 里命中的 `Func` 形参逐条核查后**均不可用作允许列表**：

| 命中 | 可达性 | 为什么不可用 |
|---|---|---|
| `StorageDetectionEngine.GetDisks(Func<…>)` | `StorageDetectionEngine` 是 **public** 类，但三个 `GetDisks` 重载中**只有无参版是 public**，带委托的两个重载是 **internal** | fork 侧**调不到**（internal 不可访问；且 `Storage.GetDisks()` 内部走的就是无参版） |
| `LinuxStorageDeviceChangeMonitor.*(Predicate<string>)` | Linux 路径 | Windows 不适用 |
| `WindowsStorageSpaceVolumeReader.PopulateFromVolumeExtents(Func<…>)` | 内部实现细节 | 非枚举入口，是卷/存储池解析的内部参数 |
| `StorageProbeDispatcher.TryProbeOperation(Func<…>)` | 内部探测调度 | 发生在**设备已构造之后** |

⇒ **升级到 F09a 推荐组合也拿不到"允许列表式"枚举**。

## 3. 证据 B —— 句柄在**枚举期内**就被打开（所以"事后过滤"等于没隔离）

### 3.1 Baseline：`ReloadStorages()` 里就有 `SafeFileHandler.OpenHandle`

`il DiskInfoToolkit.StorageManager!ReloadStorages`：

```
List`1..ctor -> StorageDetector.GetStorageDevices -> … -> StorageManager.CreateStorage -> List`1.Add
 -> … -> SafeFileHandler.OpenHandle -> SafeFileHandler.IsHandleValid -> SafeFileHandler.CloseHandle
 -> … -> LogSimple.LogDebug -> StorageDevice..ctor -> … -> StorageManager.set_Storages
```

进一步下钻 `il DiskInfoToolkit.Storage!ctor`（**每个盘的构造体**）：

```
SmartInfo..ctor -> List`1..ctor -> Object..ctor -> ArgumentNullException..ctor
 -> Storage.set_StorageController -> StorageDevice.get_PhysicalPath
 -> SafeFileHandler.OpenHandle      ← ★ 句柄在这里就开了
 -> SafeFileHandler.IsHandleValid -> LogSimple.LogTrace -> Storage.set_IsValid
 -> Storage.IdentifyStorageController -> Storage.GetDiskGeometry -> Storage.GetDiskInformation
 -> Storage.UpdatePartitions -> Storage.IdentifyDisk
 -> SafeFileHandler.CloseHandle
```

⇒ **每个磁盘在库内被构造的同时就已经 `CreateFile` 打开并做 IOCTL 识别**。

### 3.2 调用链（谁在调用方有机会介入之前就完成了这一切）

```
LHM Computer.Open()
  → StorageGroup..ctor(ISettings)
    → StorageGroup.AddHardware()                        ← LHM 侧唯一的组装配点
      → StorageManager.ReloadStorages()                 ← 库内整体枚举（DIT 1.1.2）
        → StorageDetector.GetStorageDevices()
        → StorageManager.CreateStorage() → Storage..ctor → OpenHandle/IOCTL …   ★ 句柄已开
      → StorageManager.get_Storages()                   ← 调用方**这时**才拿到清单
      → Enumerable.Select(...new StorageDevice(...))    ← LHM 包装对象
```

IL 实证（`il LibreHardwareMonitor.Hardware.Storage.StorageGroup!AddHardware`）：

```
StorageManager.remove_StoragesChanged -> StorageManager.ReloadStorages -> StorageManager.get_Storages
 -> <>c__DisplayClass11_0.<AddHardware>b__0 -> Func`2..ctor -> Enumerable.Select -> List`1.AddRange
```

⇒ 调用方唯一能碰到的东西（`get_Storages()` 的返回值）**已经是构造完成的设备清单**。
在此之后做任何过滤，**句柄都已经开过** —— 正是派发单明令禁止的冒充隔离。

### 3.3 Latest（2.2.2）同样如此，且顺序更糟

`il DiskInfoToolkit.Core.StorageDetectionEngine!GetDisks`：

```
List`1..ctor -> Func`1.Invoke -> … -> CreateBaseDevice -> MapParentControllerProperties
 -> ApplyControllerIdNames -> ClassifyController -> UsbStorageDeviceIdentifier.IsUsbDevice
 -> AttachStandardStorageProperties                     ← ★ 这里就 OpenDevice
 -> UsbStorageDeviceIdentifier.IsUsbDevice
 -> ApplyDeviceFilters                                  ← 过滤器在**之后**
 -> SelectProbeStrategy -> List`1.Add
 -> … -> UsbStorageDeviceIdentifier.IsUsbDevice -> Predicate`1..ctor -> List`1.RemoveAll   ← USB 排除发生在这里
```

`il …!AttachStandardStorageProperties`：

```
StorageDevice.get_DevicePath -> String.IsNullOrWhiteSpace -> ShouldSkipStandardPropertyProbing
 -> Stopwatch.Start -> IStorageIoControl.OpenDevice      ← ★ 每盘开句柄
 -> Stopwatch.Stop -> SafeHandle.get_IsInvalid -> … -> IStorageIoControl.TryGetStorageDeviceDescriptor -> …
```

⇒ 2.2.2 的 USB 排除（`RemoveAll`）发生在 **`OpenDevice` 之后**。
即**连 `ExcludeUsbDevices=true` 也是"开过句柄再丢弃"** —— 它能减少后续暴露，
但**不构成"构造前隔离"**，更不等于允许列表。

### 3.4 原生句柄入口声明（两个变体一致）

| 变体 | 声明点 |
|---|---|
| Baseline | `[KERNEL32.dll!CreateFileW] Windows.Win32.PInvoke.CreateFile`；`[KERNEL32.dll!DeviceIoControl] Windows.Win32.PInvoke.DeviceIoControl`；`[kernel32.dll!DeviceIoControl] DiskInfoToolkit.Interop.Kernel32.DeviceIoControl` |
| Latest | `[KERNEL32.dll!CreateFileW] …`；`[KERNEL32.dll!DeviceIoControl] …`；`[kernel32.dll!CreateFile] DiskInfoToolkit.Native.Kernel32Native.CreateFile` |

Baseline 的 `handles` 反查显示：句柄/IOCTL 调用点分布在
`Storage..ctor` 链路上的 `GetDiskGeometry` / `GetDiskInformation` / `IdentifyDisk` /
`SmartAttributeHandler.*` / `NVMeSmartIdentifier.*` / `DeviceIdentifier.DoIdentifyDevice*`
等 —— 全部属于**识别期**，不是取样期。

## 4. 证据 C —— LHM 侧也没有"构造前钩子"

两个变体的 `Computer` 成员取证结果**完全一致**：

```
event HardwareAdded : HardwareEventHandler     ← 构造**之后**通知
event HardwareRemoved : HardwareEventHandler
virtual Finalize() / Equals(Object) / GetHashCode()     ← 没有任何可覆写的枚举/过滤点
```

`IGroup` 抽象只有 `get_Hardware` / `GetReport` / `Close` —— 组是**自己产出**设备清单，
不提供"外部注入已过滤清单"的接缝。

⇒ **修库入口**是可行的技术路线（给枚举加过滤形参 / 把过滤提到 `OpenDevice` 之前），
但那是**上游改动 + 私有 fork 维护**，超出本包授权（本包对 `src/System/HardwareMonitor.cs`
**没有**改动额度，且禁止改 CI / 依赖锁定）。按派发单第 1 条，
「修库入口」与「暂不开放」二选一 ⇒ 本包选**暂不开放**。

## 5. 决策

| 选项 | 结论 |
|---|---|
| 现有库直接做允许列表 | **否**（证据 A + B） |
| 升级到 F09a 推荐组合后做 | **否**（证据 A + B，2.2.2 的 USB 排除仍在开句柄之后） |
| 修库入口（私有 fork） | **技术上可行但不做**：超出本包授权范围，且与 F09b 的依赖锁定决策冲突，需协调者裁决 |
| **暂不开放该 SMART 模式** | **✔ 采纳** —— 本包只交付 controller 策略部分 |

## 6. 未做的事（如实记录）

- **未**真实挂载 U 盘/移动硬盘做句柄抓包（本机无外置盘；判定已由 IL 级证据闭环，
  不需要靠实机来补）。Process Monitor 三场景仍列入报告「待实机」。
- **未**评估"另立辅助进程隔离"路线（母计划已明确不在本轮引半套 IPC）。
