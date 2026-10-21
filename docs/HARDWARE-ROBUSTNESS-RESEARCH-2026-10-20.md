# GPU 原生崩溃、磁盘唤醒与磁盘识别异常：修复研究

日期：2026-10-20。研究基线：`master`，`949835f7b0a7f8a2f888dd6965716b4a077b6d3f`。

状态：方案研究，未修改生产代码或依赖版本，未执行驱动重启、磁盘拔插或故意触发原生崩溃。

## 1. 结论与需要纠正的判断

三类问题应分别处理：NVML 要修句柄生命周期；磁盘唤醒要控制首次识别入口；磁盘 NRE 要覆盖库自身的识别线程。进程隔离负责限制故障影响范围，不能代替前两类根因修复。

| 问题 | 当前项目的风险 | 建议修复 |
|---|---|---|
| NVML 原生访问违例，LiteMonitor #502 | 0.9.6 的 NVIDIA 实现长期保存原生句柄；应用层 catch 无法可靠恢复真实原生访问违例 | 优先移植已合并的 LHM PR #2539，或验证包含它的新依赖；需要防止任意驱动故障带走界面时，再隔离 GPU 采集进程 |
| 外置盘唤醒、掉盘，#455 / #132 | `Computer.Open()` 前已经启用 Storage；首选磁盘仅约束之后的应用逻辑 | 默认磁盘速度走系统计数器；禁用 SMART 时在首次 Open 前关闭 Storage。需要 SMART 时在库的识别前排除设备 |
| 磁盘 NRE，#196 / #579，以及待补日志的 #582 | 部分异常来自 DiskInfoToolkit 自建线程，不在 `hw.Update()` 调用栈上 | 修复或迁移库内部识别逻辑及工作线程边界；单设备失败后跳过、退避；必要时使用独立磁盘采集进程 |

需要纠正的细节：

- 没有生成应用日志，单独不能证明 catch 从未执行；应结合事件查看器、退出码和 dump。#502 提供的 `NvmlDeviceGetPowerUsage` 原生访问违例堆栈才是更强的证据。
- `hw.Update()` 外的 catch 能处理该调用栈中的普通托管异常，接不到另一个线程中的异常。#196 / #579 的堆栈明确包含 `StorageManager.DevicesChangedListener()`。
- 普通 `Thread` 上未处理的异常通常会终止整个 .NET 应用，不能一概描述为“只死掉一个后台线程”。`AppDomain.UnhandledException` 是最终通知入口，不是继续正常运行的恢复机制。
- #132 已被作者标为 1.2.2 解决；它不能单独证明目前版本仍然复现。#455 后续报告和当前首次全盘识别路径则说明外置盘问题仍需独立处理。
- `DiskManager.cs:155` 的遍历主要读取已存在的 LHM 对象和传感器，不能只凭这处遍历断定它发起了首次物理盘识别。首次扫描的更直接证据在下述 LHM/DiskInfoToolkit 源码链。

参考：[.NET AccessViolationException](https://learn.microsoft.com/en-us/dotnet/api/system.accessviolationexception)、[托管线程异常策略](https://learn.microsoft.com/en-us/dotnet/standard/threading/exceptions-in-managed-threads)、[#502](https://github.com/Diorser/LiteMonitor/issues/502)、[#196](https://github.com/Diorser/LiteMonitor/issues/196)、[#579](https://github.com/Diorser/LiteMonitor/issues/579)。

## 2. 已核对的依赖和版本边界

本地 `LiteMonitor.csproj` 固定 `LibreHardwareMonitorLib 0.9.6`；`obj/project.assets.json` 实际解析到 `DiskInfoToolkit 1.1.2`。

NuGet 在研究日提供的 LHM 稳定版仍为 0.9.6，也提供 2026-10-18 发布的候选预览版 `0.9.7-pre767`。该预览版的 net8.0 依赖中包含 DiskInfoToolkit >= 2.2.1。它是联合验证候选，不代表本项目已验证可以直接迁移。

旧库中 `StorageManager.Storages`、实例 `Storage.Update()` 等接口，与 DiskInfoToolkit 2.2.1 的静态 `Storage.GetDisks()`、`Storage.Refresh(...)` 不同。LHM 当前开发源码已经配套迁移。不能仅将本项目传递依赖强行提升到 2.x 或手工覆盖 DLL，便假定旧 LHM 二进制仍兼容。

候选版本还改变了其他传递依赖，应检查完整发布输出，而不是只更换一个 DLL。现有多 GPU 映射、D3D 显存和 Intel 传感器预热都需要回归。

参考：[LHM 0.9.6](https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6)、[LHM 0.9.7-pre767](https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.7-pre767)、[DiskInfoToolkit 2.2.1](https://www.nuget.org/packages/DiskInfoToolkit/2.2.1)、[旧版 LHM 存储适配](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Storage/StorageGroup.cs)、[开发版 LHM 存储适配](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Storage/StorageGroup.cs)。

## 3. NVML：已有可移植的根因修复

### 3.1 当前代码路径

`HardwareMonitor.UpdateAll()` 在 GPU 分支包住 `hw.Update()`，并记录普通异常。初始化、GPU 预热、重载和关闭也涉及 LHM，不能只保护定时更新这一处。

LHM v0.9.6 的 `NvidiaGpu` 在构造时保存只读 `_nvmlDevice`，后续每轮用它查询功率、PCIe 吞吐。驱动重启、设备移除和某些电源切换期间，先前句柄可能失效。

真实原生访问违例可能表示进程状态已经损坏；现代 .NET 不支持通过旧的 `HandleProcessCorruptedStateExceptions` 特性恢复。增加 `catch (AccessViolationException)`、把调用放进 `Task.Run` 或添加全局异常回调，都不能形成可靠的隔离边界。

### 3.2 首选：采用上游 PR #2539

该 PR 已在 2026-09-29 合并，合并提交为 `3aa9c6c5bfc45fd15f4166bd35d73609b2bb126c`，修改两个文件：`NvidiaGpu.cs` 和 `NvidiaML.cs`。

关键策略是每轮重新查找 NVML 句柄、使用 PCI 身份校验索引回退，并让查找、使用和关闭共享生命周期锁。查询失败时传感器返回不可用。这覆盖了长期缓存句柄、卡序变化及关闭竞态的已知风险。

这里的锁只能约束本进程的库调用，无法阻止驱动在外部发生重置，因此仍不能将补丁描述为对所有原生故障的绝对保证。

实施选择：

1. 在隔离验证环境试用新 LHM 依赖，确认所选构建实际含该补丁，运行现有 GPU 回归和实机验证。
2. 如果联合升级影响过大，在固定的 0.9.6 源码基础移植这两个文件涉及的必要逻辑，维护可复现的构建版本。
3. 为问题机器提供兼容模式时，应在采集实现中禁用具体高风险 NVML 查询；只隐藏功率行并不会阻止 LHM 的整卡 Update 查询它。不要未经验证就关闭全部 NVML，当前 NVIDIA 的 D3D 适配器匹配也使用其 PCI 信息。

参考：[LHM #2523](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/2523)、[已合并 PR #2539](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/2539)、[0.9.6 NvidiaGpu](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs)、[0.9.6 NvidiaML](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Interop/NvidiaML.cs)。

## 4. 磁盘唤醒：过滤必须先于首次识别

### 4.1 为什么首选磁盘不够

当前启动链为：

```text
HardwareMonitor: IsStorageEnabled = true
  → Computer.Open()
  → new StorageGroup(...)
  → StorageManager.ReloadStorages()
  → 枚举设备并构造 Storage
  → 识别控制器、盘信息和 SMART 等
```

DiskInfoToolkit 1.1.2 的 `ReloadStorages()` 还会尝试未识别的 `PhysicalDrive0..63`。应用在之后按名称选首选盘，已经无法撤销识别阶段的设备访问。热插拔识别也需要使用相同的排除策略。

旧版 `StorageGroup.Close()` 为空，DiskInfoToolkit 又有静态后台线程，因此事后把 `IsStorageEnabled` 切为 false，不能自动等同于停止了所有旧库活动。关闭必须在首次初始化前生效；旧版已经初始化后的严格关闭，需要可靠的停止机制或重启采集进程。

参考：[当前问题 #455 及评论](https://github.com/Diorser/LiteMonitor/issues/455)、[已关闭问题 #132](https://github.com/Diorser/LiteMonitor/issues/132)、[LHM Computer](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Computer.cs)、[StorageGroup](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Storage/StorageGroup.cs)、[DiskInfoToolkit 1.1.2 StorageManager](https://github.com/Blacktempel/DiskInfoToolkit/blob/1.1.2/DiskInfoToolkit/StorageManager.cs)。

### 4.2 建议定义明确的磁盘采集模式

| 模式 | 行为 | 功能边界 |
|---|---|---|
| 基础模式，建议默认 | 首次 Open 前设置 `IsStorageEnabled = false`；读写速度、活动率走 Windows 计数器 | 不主动走 SMART 识别；温度、健康度显示不可用 |
| 排除外置设备的 SMART 模式 | 在库首次检测前应用 USB/设备排除规则，才允许 Storage 初始化 | 只能访问允许的设备；未知设备不自动放行 |
| 完整 SMART 模式 | 用户明确启用后允许完整识别 | 不承诺对所有 USB 桥、RAID 和休眠盘都无影响 |

本项目已经具有 `PhysicalDisk` 的总读、总写和活动时间计数器，基础模式可复用。当前单盘首选会绕开总计数器，因此如果要在基础模式保留“指定盘速度”，需要新增单盘计数器实例与系统设备身份映射。

基础模式必须覆盖所有入口：Web、传感器浏览、温度告警、自动选盘、旧设置迁移和热插拔。计数器不可用时返回不可用，不能悄悄回退到 LHM/SMART。`DriveInfo.IsReady`、容量/分区查询也应服从设备排除规则。

承诺应限定为“应用不会主动对排除设备执行这些识别/SMART/容量查询”；操作系统和其他程序仍可能唤醒磁盘，不能承诺整机物理上永远不唤醒。

### 4.3 新库中可利用的能力

已核对 **2.2.1 标签源码**：`DiskInfoToolkit.Storage.ExcludeUsbDevices` 能排除检测、刷新及监控中的 USB 设备，默认 false；它还跳过可能提前打开 USB 卷的相关映射路径。必须在 LHM 第一次创建存储组之前设置。

它解决的是 USB 排除，不能直接保证排除所有外置形态、RAID 桥或特定内置机械盘。按设备白名单过滤仍需在库真正打开/识别设备前实施，不能等 `GetDisks()` 返回后才筛选。

参考：[2.2.1 Storage.cs](https://github.com/Blacktempel/DiskInfoToolkit/blob/2.2.1/DiskInfoToolkit/Storage.cs)。

## 5. 磁盘 NRE：覆盖识别线程和单设备失败

### 5.1 已确认的异常边界

#196 / #579 的调用链为：

```text
StorageManager.DevicesChangedListener()
  → HandleUnpartitionedDrive(...)
  → new Storage(...)
  → IdentifyStorageController() / IdentifyDisk()
```

在 1.1.2 源码中，该监听线程由静态构造器创建，处理循环没有包住每个设备事件的异常边界。`StorageController.Contains(...)`、`storage.Model.Contains(...)` 等位置直接使用字符串，也确实存在空值触发点。

仅在应用的 `hw.Update()` 周围补 catch，覆盖不到这条独立线程的调用链。普通更新异常还会被当前 `UpdateAll()` 最外层空 catch 吞掉，并提前结束当轮剩余模块，建议同时修复日志与单设备隔离。

### 5.2 可实施的两条路线

**保留 1.1.2 的最小补丁**：在库内部规范化或拒绝缺失的控制器/型号元数据；识别失败的对象不得继续发布为有效设备；使用 SafeHandle 或 try/finally 释放句柄；为每个识别事件、每台设备构造及回调添加普通托管异常边界。失败设备进入有界退避或隔离状态，其余设备继续处理。

**联合迁移 LHM 与 DiskInfoToolkit**：使用 LHM 对 2.x 的配套适配，在相同故障样本上验证。新版具有设备排除、显式停止等能力，但不能仅凭版本号或旧 issue 关闭就宣称全部 NRE 已修复。

特别要避免：坏盘每次失败后立即重新全盘识别，或无上限重启磁盘采集器。这会加剧访问次数及外置设备扰动。发生重复磁盘识别失败后，宜停止该模块，等待冷却或用户明确重试。

参考：[上游 DiskInfoToolkit #5](https://github.com/Blacktempel/DiskInfoToolkit/issues/5)、[#6](https://github.com/Blacktempel/DiskInfoToolkit/issues/6)、[1.1.2 Storage.cs](https://github.com/Blacktempel/DiskInfoToolkit/blob/1.1.2/DiskInfoToolkit/Storage.cs)。其中 #6 最后按 `not_planned` 关闭，不应当作已修复证明。

### 5.3 #582 的证据限制

#582 正文只有日志附件。本次确认了 issue 标题，但附件未能成功取得，尚未核对其中的异常堆栈。不能将它直接归并为已证实的 `DevicesChangedListener` 同一故障。

参考：[#582](https://github.com/Diorser/LiteMonitor/issues/582)。

## 6. 对原生故障和硬阻塞增加进程隔离

如果目标是“采集库崩溃时面板、网速和设置仍可用”，应将 GPU 原生查询、磁盘 SMART 分别放到采集子进程。主进程里也必须禁用对应的 LHM 模块，避免初始化阶段再次触发同一库路径。

```text
UI / Windows 性能计数器 / 快照缓存
  ├─ Named Pipe → GPU 采集进程
  └─ Named Pipe → SMART 采集进程
```

隔离边界应覆盖 Open、设备发现、预热、Update、重载和 Close。只把周期性 Update 移出去仍然可能在主进程初始化时崩溃。GPU 与 SMART 分开，可以避免一个坏盘反复拖断 GPU 数据。

IPC 只传数值、设备身份、时间戳和有效性，不传 `IHardware` / `ISensor`。工作进程退出时主进程标记数据过期或不可用，记录退出码和最近阶段；重启有退避与上限。超时后可结束自有工作进程，不能用超时 Task 加新 Task 的方式留下仍在原生调用中的旧任务。

设计 GPU 独立进程时还要保留 Intel 核显初始化条件：当前 LHM 的 Intel GPU 分组依赖 CPU 枚举。不能简单把 worker 的 `IsCpuEnabled` 关掉后宣称兼容所有 GPU。

进程隔离防止用户态采集故障带走主进程，不会让危险磁盘探测本身变得无害；设备访问策略仍须先落实。

可选为工作进程配置按应用生效的 WER LocalDumps，保存真实崩溃现场。它需要管理员配置，不作为本次研究的自动操作。参考：[Microsoft WER LocalDumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)。

## 7. 建议实施顺序

1. **P0：磁盘基础模式和真正的初始化前开关。** 保留系统计数器速度统计，阻断非必要 SMART 首次识别；Web 不能覆盖用户禁用策略。
2. **P0：处理已知 NVML 句柄缺陷。** 优先验证上游 #2539 的回移或对应新构建，回归双 NVIDIA / Intel 核显 / 显存映射。
3. **P1：完善托管异常边界。** 同步更新按设备隔离；磁盘识别线程的异常处理必须在库内部或其独立进程内完成；记录失败和停止原因。
4. **P1：验证依赖联合迁移。** 使用固定版本或提交；检查输出依赖、磁盘排除和生命周期，不直接跟随 master。
5. **P1：按稳定性目标引入采集进程。** 这是防止任意用户态原生库故障带走主界面的后续工程；测量新增进程的内存、CPU 和启动成本。

这些工作与已有 `2026-10-20-performance-sampling-hardening.md` 有交集但范围不同。已有性能计划明确不升级依赖；上述依赖修复和进程隔离应独立实施并各自验收，避免悄悄扩大原计划。

## 8. 验收要求

| 场景 | 必须证明的结果 |
|---|---|
| SMART 关闭，冷启动 | 不创建 StorageGroup、不触发 DiskInfoToolkit 初次识别；速度计数器仍工作 |
| SMART 关闭，打开 Web/告警/传感器页面 | 不隐式开启 SMART 或对排除盘执行容量查询 |
| 排除 USB，冷启动和热插拔 | 策略在库第一次检测前生效；选择首选 SSD 不触碰被排除 USB 设备 |
| 型号/控制器/SMART 信息缺失 | 单设备失败被记录，其他设备仍更新，不产生循环重试风暴 |
| 停止 SMART | 旧后台监听与设备句柄确实停止，或相应工作进程已退出 |
| GPU 恢复、切换和卡序变化 | 无陈旧句柄使用；按 PCI 身份绑定；多卡不串卡 |
| 采集子进程真实原生故障或永久阻塞 | 主进程仍响应，数据有明确失效状态，恢复次数受限 |
| 性能 | 与现有基线使用同等硬件、模式和时长复测，记录 CPU、分配量与启动成本 |

原生故障验证应在自有测试工作进程或专用测试环境中进行；手工 `throw new AccessViolationException()` 会被普通 catch 捕获，不能替代真实原生访问违例测试。本次没有在用户机器上重启 GPU 或刺激外置阵列复现。
