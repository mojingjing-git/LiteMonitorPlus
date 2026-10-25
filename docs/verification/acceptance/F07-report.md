# P-F07 验收记录 —— 稳定设备身份与兼容迁移

| 项 | 值 |
|---|---|
| 工作包 | P-F07（硬件实现串行链**起点**，F08b → F09b → F10 → F11 排在其后） |
| 分支 | `fix/F07-stable-device-identity`（基于 `2300af6` = `integration/w1` 合并点；master `af572c0` 仅多 F08a 报告/用例两条提交） |
| 覆盖 | #290（核显/独显切换闪退）、#538（同型号 SSD 只显示第一块）；为 #577（多风扇独立展示）打基础 |
| 代码层验收 | **通过**（0 错误构建；三个测试工程全量绿；新增 33 条用例） |
| 实机验收 | **部分待实机**（见 §8） |

---

## 1. 一句话结论

设备身份从「显示名 / LHM 枚举序号」换成「**可证明的稳定身份**（PnP 实例路径 → 序列号 → 总线位置），
拿不到就**显式标记为不稳定**而不是猜」；公开指标 Key（`GPU.{index}.*`）**格式不变**，
展示槽位改由**持久化绑定**指向物理设备；旧配置**仅唯一匹配才迁移**，歧义保留旧值 + 提示。

---

## 2. 证据锚点（实测，2026-10-26 本机）

### 2.1 LHM 0.9.6 的 `Identifier` **不是身份**（本包最关键的前置事实）

| 设备 | LHM `Identifier` | 可用的身份来源 | 证据 |
|---|---|---|---|
| 显卡 | `/gpu-nvidia/{枚举序号}` | **`DeviceId` = PnP 实例路径** | 离线 IL 探针（§2.3）+ 本机实测 |
| 存储 | `/nvme`（**媒体类型**）/ `/ssd` / `/hdd` | `StorageDevice.Storage` 的 `DeviceID` / `SerialNumber` / `PhysicalPath` | 离线 IL 探针（§2.3） |

离线 IL 探针读到的字节码（`LibreHardwareMonitorLib 0.9.6`，`runtimes/win-x64/lib/net8.0`）：

- `LibreHardwareMonitor.Hardware.Storage.StorageGroup::GetID`（rva=99955，35 字节）
  `ldarg.1 → callvirt get_IsNVMe → brfalse → ldstr "nvme" → ret`；
  `ldarg.1 → callvirt get_IsSSD → brfalse → ldstr "ssd" → ret`；末尾 `ldstr "hdd" → ret`
  ⇒ **存储 Identifier 只是"介质类型"**，所有 NVMe 盘共用同一个值。
- `LibreHardwareMonitor.Hardware.Gpu.NvidiaGpu::.ctor`（rva=288060）
  `ldstr "gpu-nvidia" → Int32::ToString(InvariantCulture) → Identifier::.ctor`
  ⇒ **显卡 Identifier 是枚举序号**。
- `LibreHardwareMonitor.Hardware.Storage.StorageDevice::get_Storage` **[Public]**
  ⇒ 包装的 `DiskInfoToolkit.Storage` 公开了 `DeviceID` / `SerialNumber` / `PhysicalPath` / `Model` / `TotalSize`（见 §2.2 静态扫描）。

本机实测（**只枚举 GPU，`IsStorageEnabled=false`，不触碰任何磁盘**）：

```
[GpuNvidia] name='NVIDIA GeForce RTX 3080'
    Identifier = '/gpu-nvidia/0'
    DeviceId   = 'PCI\VEN_10DE&DEV_2206&SUBSYS_146D10DE&REV_A1\4&19aa7bb9&0&0019'
[GpuNvidia] name='NVIDIA GeForce RTX 3080 Ti'
    Identifier = '/gpu-nvidia/1'
    DeviceId   = 'PCI\VEN_10DE&DEV_2208&SUBSYS_88021043&REV_A1\4&14ed3518&0&001A'
```

⇒ **旧实现把 `/gpu-nvidia/0` 当成"首选卡"存进 `Settings.PreferredGpu`**：重启后枚举顺序一变，
用户的"首选卡"就会指向**另一块物理卡**（或核显）——这正是 #290「切换时闪退 / 选卡被静默改写」的结构性根因。

### 2.2 旧代码里的 #538 根因（已消除）

基线 `2300af6` 的 `src/System/HardwareServices/HardwareScanner.cs:85`：

```csharp
.Select(h => h.Name).Distinct().ToList();   // ★ 按显示名去重 ⇒ 同型号盘只剩第一块
```

⇒ 现改为按**身份**建目录（`HardwareScanner.cs:120` `ListAllDiskOptions(IEnumerable<IHardware>)`），
同名设备**不再合并**，标签按既有 GPU 范式加 `#序号` 后缀。

### 2.3 证据工具（离线、只读、不联网）

`.tmp/` 被 `.gitignore` 忽略，**未入库**；复现方式（把源码抄到任意目录即可）：

```powershell
# IL 探针（System.Reflection.Metadata，只读程序集）
dotnet run -- <LHM.dll> 'StorageGroup'      # 看 GetID 的 ldstr "nvme"/"ssd"/"hdd"
dotnet run -- <LHM.dll> 'Gpu.NvidiaGpu'     # 看 .ctor 的 ldstr "gpu-nvidia" + index

# 实机 GPU 探针（不启用存储 ⇒ 不做 SMART 探测）
dotnet run --   # Computer { IsStorageEnabled = false, IsGpuEnabled = true } → 打印 Identifier / DeviceId
```

### 2.4 基线测试数

master 基线：`LiteMonitor.Tests` 1936 条（1935 通过 + 1 联网 E2E 跳过）、Updater 73、GPU 非性能 19，合计 2028。
本包新增 **33** 条 ⇒ 合并后应为 **1969**（实测见 §7；跳过数差异是环境性的 Market 索引/联网用例，与本包无关）。

---

## 3. ★ 最小设计 + 迁移 fixture（供 F08b / F10 / F11 接力）

> 本节是下游包的**接口契约**。下游**直接消费**下列公开类型，不需要再各自发明身份口径。

### 3.1 身份约定（`src/System/HardwareServices/DeviceIdentity.cs`）

```
DeviceIdentity
  StableId         跨重启稳定标识；空串 = 无法证明（此时 Stability != Stable）
  RuntimeId        本次枚举的实例标识（永远非空）
  DisplayName      显示名（不可单独作身份）
  ParentId         父设备（风扇 → 主板）
  Kind             Gpu / Storage / Fan / Network / Motherboard
  IsPresent        本次枚举中是否存在
  Stability        Stable | Unstable | Unknown   ← 显式标记，不猜
  StabilityReason  判定依据（中文，可直接进日志）
  Attributes       佐证属性：serial / bus / size / model / name / lhmIdentifier
```

**取值优先级（单一落点：`DeviceIdentityResolver.Resolve(DeviceProbe)`，`DeviceIdentity.cs:342`）**

1. 设备实例 ID（PnP 实例路径）→ `StableId = "pnp:" + id`，`Stability = Stable`
   （`DeviceIdentity.cs:364`）
2. 序列号 → `StableId = "serial:" + serial`（`DeviceIdentity.cs:371`）
3. 总线位置 → `StableId = "bus:" + bus:device`（`DeviceIdentity.cs:378`）
4. 都没有 → `StableId = ""`、`Stability = Unstable`、原因写明"只有枚举序号 / 显示名"（`DeviceIdentity.cs:385`）

> **LUID / 枚举序号 / 显示名一律不单独作跨重启身份**（母计划 F07 第 3 条）。

**身份来源按硬件形态分流**（`DefaultDeviceProbeFactory`，`DeviceIdentity.cs:178`）：
显卡取 `DeviceId`（PnP 路径）+ AMD 的 `BusNumber`/`DeviceNumber`；存储经 `StorageDevice.Storage`
（**反射**读取，避免编译期绑定 DiskInfoToolkit 这个传递依赖）取 `DeviceID`/`SerialNumber`/`PhysicalPath`。
**枚举只读属性，不调用 `Update()`** ⇒ 不会顺带做 SMART 探测（#466/#601 止血口径，有用例守着）。

### 3.2 展示槽位 vs 设备身份（分离）

- 公开指标 Key **仍是** `GPU.{index}.{指标}`，**裸 PCI 路径不拼进 Key**（用例 `公开指标Key格式不变_仍是GPU序号形式`）。
- 槽位 → 设备的映射放在**绑定仓库**：`device-bindings.json`（与 `settings.json` 同目录）+ `.bak`。
- 绑定条目 `DeviceBindingStore.DeviceBinding`：`Kind / Slot / StableId / RuntimeId / DisplayName / Manual / Attributes / UpdatedTimestamp`。
  - `Slot = "preferred"` = 用户手动首选；`Slot = "0".."7"` = 显卡槽位。
- 写入语义：**原子写（tmp + 覆盖移动）+ 覆盖前留 `.bak` + 幂等（内容不变不重写）**
  （`DeviceBindingStore.Save()`，`DeviceBindingStore.cs:155`；时钟用可注入 `TimeProvider`）。

### 3.3 迁移 fixture（`src/System/HardwareServices/DeviceMigration.cs`）

```csharp
DeviceMigration.Migrate(currentValue, options, kind) -> DeviceMigrationResult
```

| 结论 | 含义 | 动作 |
|---|---|---|
| `NotNeeded` | 没有旧值（自动模式） | 不动 |
| `AlreadyCurrent` | 已是稳定 ID | 不动（**幂等**） |
| `MigratedUnique` | **唯一**命中 | 改写为新值 |
| `AmbiguousPreserved` | 命中多个（同型号） | **保留旧值 + 提示**，候选列进 `Candidates`，**不随机选第一块** |
| `UnresolvedPreserved` | 找不到 | **保留旧值 + 提示** |

匹配顺序：稳定 ID（Value）→ 旧 LHM Identifier（RuntimeId）→ 旧显示名（Name）。
`DeviceMigrationResult.ValueToPersist` 永远给出"应当被持久化的值"（歧义/未解析时 = 原值）。

### 3.4 槽位解析器（`src/System/HardwareServices/DeviceSlotResolver.cs`）

```csharp
DeviceSlotResolver.ResolvePreferred(kind, persistedValue, candidates) -> DeviceSlotResolution
DeviceSlotResolver.ResolveSlot(kind, slot, candidates)              -> DeviceSlotResolution
DeviceSlotResolver.PersistBinding(kind, slotKey, identity, manual)   -> bool
```

`DeviceSlotResolution.State`：`NewSlot / Bound / ResolvedByCorroboration / Offline / Unresolved / AutoFallback`。

- 首选卡不在场 ⇒ `Offline`：**保持绑定、公开离线**，`cfg.PreferredGpu` 原样不动（**不改绑**）。
- 稳定 ID 变了（驱动更新）⇒ 只有**序列号 / 总线**这类强佐证**唯一**命中才判 `ResolvedByCorroboration`；
  否则 `Unresolved` ⇒ **不复用历史绑定**。
- 自动模式的临时回退用 `AutoFallback` 表达：**原绑定保持不变**（设备回来后自动回到原槽位），
  当前选择通过 `SensorMap.GpuSlotResolutions`（`SensorMap.cs:144`）与 `SensorMap.IsPreferredGpuOffline`（`SensorMap.cs:149`）**公开**。

### 3.5 下游包怎么用

| 包 | 直接消费 |
|---|---|
| **F08b**（精细允许列表） | `DeviceIdentityResolver` 的存储身份来源（`StorageDevice.Storage.DeviceID`/`SerialNumber`）已就位；允许列表键用 `StableId`（`"pnp:…"`），**不要**用显示名；枚举仍走 `DiskProbePolicy`（本包未触碰其策略） |
| **F09b**（原生依赖） | NVML 句柄按身份重取时，用 `DeviceIdentity.StableId` 而不是枚举序号；`AmdGpu.BusNumber/DeviceNumber` 与 `NvidiaGpu.DeviceId` 已可用 |
| **F10**（GPU 映射自愈） | `SensorMap.GpuSlotResolutions` 直接给出每个槽位的解析状态（`Bound`/`Offline`/`Unresolved`/`AutoFallback`）——自愈时**不要**改绑未解析的槽位 |
| **F11**（缺失语义 / 多风扇） | 风扇身份 = **父设备身份 + 传感器名**：`HardwareScanner.ListAllFanOptions`（`HardwareScanner.cs:280`）已产出 `Value = "fan:{父设备Id}/{传感器名}"`；`CASE.Fan` 兼容别名与多槽位展示仍归 F11 |
| **F03/F04/F05/F06** | 本包**未触碰** `HardwareMonitor.cs` / `NetworkManager.cs` / 任务栏路径 |

---

## 4. 改动清单（全部在授权范围内）

### 新建

| 文件 | 内容 |
|---|---|
| `src/System/HardwareServices/DeviceIdentity.cs` | 身份契约 + 解析器 + 目录 + 匹配器（§3.1） |
| `src/System/HardwareServices/DeviceBindingStore.cs` | 槽位绑定仓库（原子写 / 备份 / 幂等） |
| `src/System/HardwareServices/DeviceMigration.cs` | 迁移夹具（§3.3） |
| `src/System/HardwareServices/DeviceSlotResolver.cs` | 槽位解析器（§3.4） |
| `tests/LiteMonitor.Tests/F07FakeHardware.cs` | 假硬件（不接触驱动，记录 Update 次数） |
| `tests/LiteMonitor.Tests/DeviceIdentityTests.cs` | 9 条 |
| `tests/LiteMonitor.Tests/DeviceMigrationTests.cs` | 8 条 |
| `tests/LiteMonitor.Tests/DeviceSlotBindingTests.cs` | 9 条 |
| `tests/LiteMonitor.Tests/DeviceDiskSelectionTests.cs` | 5 条 |

### 修改

| 文件 | 关键改动 |
|---|---|
| `src/System/HardwareServices/HardwareScanner.cs` | `ListAllDiskOptions`（身份化，`:120`）、`ListAllGpuOptions`（值改稳定身份，`:157`）、`FindGpuByConfig`（稳定 ID → 运行时 ID → 唯一同名，`:193`）、`ListAllFanOptions`（父设备 + 传感器名，`:280`）；**`ListAllDisks` 的按名去重已删除** |
| `src/System/HardwareServices/SensorMap.cs` | `SelectMonitoredGpus` 改为槽位绑定（`:162`）、`PersistPreferredGpu` 迁移回写（`:279`）、`Rebuild` 增加可注入重载（`:333`）、公开 `GpuSlotResolutions` / `IsPreferredGpuOffline` |
| `src/System/HardwareServices/DiskManager.cs` | 首选磁盘按**身份**匹配（`:111`）、`FindDiskByConfig` 身份优先且同名不猜（`:385`）、构造函数可注入身份解析器 |
| `src/System/HardwareServices/FanMapper.cs` | 扫描入口改为接受硬件集合（可注入），行为不变 |
| `src/UI/Settings/SystemHardwarPage.cs` | 磁盘下拉改 **Label + Value**（`FillDiskSync`，`:408`），同名多盘分别可选；原值不可用时保留 + 提示（`:527` 调用点） |
| `tests/LiteMonitor.Tests/GpuItemSyncTests.cs` | 扩展 2 条（身份变化不影响 Key/排序；首选卡离线仍保留该槽位监控项） |

### 未改动（遵守禁改清单）

`src/System/HardwareMonitor.cs`（0 行）、`src/Core/Settings.cs`（0 行）、
`.github/workflows/ci.yml` / `release.yml`（阈值 2028/1 未动）、`NetworkManager.cs`、CI 门禁。

---

## 5. 红 → 绿证明

**红**：临时把两处行为还原成旧实现（保留新 API 以便编译），跑 F07 用例：

```
dotnet build LiteMonitor.sln -c Debug --nologo
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo `
  --filter "FullyQualifiedName~DeviceIdentityTests|FullyQualifiedName~DeviceMigrationTests|`
            FullyQualifiedName~DeviceSlotBindingTests|FullyQualifiedName~DeviceDiskSelectionTests"

失败 LiteMonitor.Tests.DeviceIdentityTests.枚举磁盘身份_不触发任何设备Update
失败 LiteMonitor.Tests.DeviceDiskSelectionTests.同型号双盘_选中第二块时第一块不被更新
失败 LiteMonitor.Tests.DeviceDiskSelectionTests.同型号双盘_选中第一块时第二块不被更新
失败 LiteMonitor.Tests.DeviceSlotBindingTests.双同型号显卡_枚举顺序互换_槽位仍指向同一块物理卡
失败 LiteMonitor.Tests.DeviceSlotBindingTests.驱动更新导致标识变化_无法证明时不复用历史绑定
失败 LiteMonitor.Tests.DeviceSlotBindingTests.驱动更新导致标识变化_有序列号佐证_判定为同一台设备
失败 LiteMonitor.Tests.DeviceSlotBindingTests.手动首选卡消失_保持绑定并标记离线_不改绑
失败!  - 失败: 7，通过: 24，已跳过: 0，总计: 31
```

**绿**：`git checkout --` 还原修复后重跑（同一命令）：

```
已通过! - 失败: 0，通过: 45，已跳过: 0，总计: 45      （含 GpuItemSyncTests 14 条）
```

---

## 6. 验收矩阵逐项结论

| # | 验收项 | 结论 | 证据（用例 / 文件:行） |
|---|---|---|---|
| 1 | 双同型号 SSD 可分别选择 | ✅ | `同型号两块盘_选择项值不同且标签可区分`；`SystemHardwarPage.cs:408` |
| 2 | 双同型号 SSD 迁移不误绑定 | ✅ | `旧LHM标识命中多个_歧义保留`（"/nvme" 命中两块 ⇒ 保留旧值） |
| 3 | 磁盘选择行为：选中一块不影响另一块 | ✅ | `DeviceDiskSelectionTests.同型号双盘_选中第一块时第二块不被更新`（旧行为下红） |
| 4 | 双同型号 GPU 枚举顺序互换 | ✅ | `双同型号显卡_枚举顺序互换_槽位仍指向同一块物理卡`（旧行为下红） |
| 5 | 设备短暂消失 | ✅ | `手动首选卡消失_保持绑定并标记离线_不改绑`；`首选卡回来_恢复原身份` |
| 6 | 重启（顺序变化）后身份保持 | ✅ | 同 #4（绑定持久化在 `device-bindings.json`） |
| 7 | 驱动更新导致标识变化 | ✅ | `驱动更新导致标识变化_无法证明时不复用历史绑定`（不复用）；`…_有序列号佐证_判定为同一台设备`（唯一命中才复用） |
| 8 | 无法证明同一设备时不复用历史绑定 | ✅ | 断言 `State != Bound && != ResolvedByCorroboration`，且原绑定 `StableId` 不变 |
| 9 | 旧配置仅唯一匹配迁移 | ✅ | `旧名称唯一匹配_迁移到稳定ID` |
| 10 | 歧义保留旧值 + 提示，不随机选第一块 | ✅ | `同名歧义_保留旧值并提示_不选第一块`（断言 `ValueToPersist` 仍为旧值、提示含两个候选） |
| 11 | 迁移幂等 / 原子 / 备份 | ✅ | `已经是稳定ID_幂等不重复迁移`、`绑定仓库_写入幂等_覆盖时保留备份`、`绑定仓库_主文件损坏_回落到备份` |
| 12 | 重复加载不重复新增指标 | ✅ | `重复加载不重复新增指标_排序阈值不漂移`（数量与 Key 集合不变） |
| 13 | 排序阈值不漂移 | ✅ | 同上（`SortIndex` 逐项相等）；`Ensure_设备身份变化_不影响监控项Key与排序` |
| 14 | 公开指标 Key 格式不变 | ✅ | `公开指标Key格式不变_仍是GPU序号形式`（`GPU.0.Load`；无 `PCI`/`pnp:` 片段） |
| 15 | 枚举不为取 ID 做 SMART 探测 | ✅ | `枚举磁盘身份_不触发任何设备Update`（假盘 `UpdateCount == 0`）；产品路径只读属性 |
| 16 | 手动首选卡消失保持绑定 + 离线 | ✅ | `手动首选卡消失_保持绑定并标记离线_不改绑`（`cfg.PreferredGpu` 不变、绑定不变、`IsPreferredGpuOffline == true`） |
| 17 | 自动模式可重选但公开选择 | ✅ | `AutoFallback` 状态 + `SensorMap.GpuSlotResolutions` 公开；原绑定**不**被覆盖 |
| 18 | `MonitoredGpuCount=1` 语义不变 | ✅ | 未改数量语义（仍 = 只显示一张）；`Ensure_实际卡数不足时_夹紧并回写数量` 等既有用例保持绿 |
| 19 | 风扇身份按父设备区分（#577 基础） | ✅ | `风扇身份_按父设备区分_同名传感器不互相覆盖`；**多槽位展示仍归 F11** |
| 20 | 不把裸 PCI 路径拼进 Key | ✅ | 同 #14 |
| 21 | 时间用可注入时钟 / 测试无 `Thread.Sleep` | ✅ | `DeviceBindingStore` 用 `TimeProvider`；新增用例零 `Sleep` |
| 22 | 应用层 catch 不冒充原生边界修复 | ✅ | 本包只做身份/迁移，**不**碰 NVML / SMART 边界（F09b / F08b 负责） |

---

## 7. 实测结果（本 worktree，`G:\TOOL\LiteMonitor-fork\.worktrees\f07`）

```
$ dotnet build LiteMonitor.sln -c Debug --nologo
  0 个错误（529 warnings，均为既有 xUnit 分析器告警）

$ dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo
  已通过! - 失败: 0，通过: 1962，已跳过: 7，总计: 1969，持续时间: 2 m 34 s

$ dotnet test tests/LiteMonitor.GpuNonperformance.Tests/... -c Debug --no-build --nologo
  已通过! - 失败: 0，通过: 19，已跳过: 0，总计: 19

$ dotnet test tests/LiteMonitor.Updater.Tests/... -c Debug --no-build --nologo
  已通过! - 失败: 0，通过: 73，已跳过: 0，总计: 73
```

- **新增用例数 = 1969 − 1936 = 33**（9 + 8 + 9 + 5 + 2）。
- 跳过数 7 vs 基线记录的 1：差的是 `MarketIndexContractTests`（6）+ `MarketE2ETests`（1）——**联网/索引环境性跳过**，与本包无关（协调者基线环境里这些是跑通的）。

### 7.1 基线已知 flaky（如实记录，未修）

最终验收那一轮全量跑出现过 **1 条**间歇失败（随后重跑为 0 失败）。定位结论：**基线已知 flaky，与本包无关**。

- 复现（只跑该类，连跑 6 次）：第 4 次 `SuccessfulInstallAndUpdate_PersistRecordAndPayload(update: True)` 失败、
  第 6 次 `ExtendedJournalMetadata_IsValidatedBeforeRecovery(...)` 两条失败 —— **每次失败的用例都不同**，
  是典型的**类内跨用例状态污染**。
- 本包**未触碰**该用例涉及的任何文件（`src/Core/Settings.cs`、`src/Core/SettingsHelper.cs`、插件/市场持久化代码），
  `git diff af572c0 HEAD -- src/Core/Settings.cs src/Core/SettingsHelper.cs tests/LiteMonitor.Tests/ReviewMarketPersistenceRegressionTests.cs` 输出为空。
- 按派发单「坑 #1」处理：**不修**，仅在此注明（协调者已在纯净基线 d52c0ab 上复现过同一现象）。

---

## 8. 待实机清单（mock 结果**不得**用于关闭硬件特有问题）

| # | 场景 | 为什么待实机 | 关闭条件 |
|---|---|---|---|
| 1 | 两块**同型号** SSD 在真机上分别可选 | 假盘无法证明 LHM 真实 `StorageDevice.Storage.DeviceID` 的取值形态 | 设置页出现两条带 `#序号` 的同型号项，分别选中后温度/速率不串盘 |
| 2 | 真机重启后同型号盘仍绑定到同一块 | 需要真实 PnP 实例路径跨重启稳定 | 重启后 `PreferredDisk` 命中的仍是同一块盘 |
| 3 | 双 3080 / 3080 Ti 枚举顺序互换 | 需要 NVML 顺序真的会变（无法人为制造） | 交换 PCIe 插槽/驱动重装后 `GPU.0` 仍是原来那块卡 |
| 4 | 核显/独显切换（#290 现场） | 需要 Intel 核显 + 独显混合机型（本机为 AMD 平台 + 双 NVIDIA） | 切显/独显不崩；首选卡消失时显示离线而非改绑 |
| 5 | 驱动更新后 PnP 实例路径变化 | 本机无驱动重装环境 | 佐证命中则判同一台，否则提示重新选择（**不静默改绑**） |
| 6 | 风扇多槽位（#577） | 本包只提供身份，**展示归 F11** | 见 F11 验收 |
| 7 | `DeviceID` 为空的盘（部分 USB/盒装盘无实例路径） | 需要真实外置盘 | 降级到序列号；都没有则显式标记不稳定并提示手动选择 |

---

## 9. 偏离与遗留

### 9.1 对派发单 / 母计划的偏离（均已评估，无阻塞）

| # | 偏离 | 理由 |
|---|---|---|
| D1 | **未改 `src/Core/Settings.cs`**（新增字段被禁） | 槽位绑定改用旁挂文件 `device-bindings.json`（与 `settings.json` 同目录）：Settings 是共享热点且本包禁改；用户可见的选择仍留在 `Settings.PreferredGpu` / `PreferredDisk`（值升级为稳定 ID） |
| D2 | 改了 `src/UI/Settings/SystemHardwarPage.cs` | 属于"磁盘选择项相关文件"：磁盘下拉必须改成 Label+Value 才能让同型号盘分别可选（母计划 F07 步骤 4 要求） |
| D3 | 改了 `src/System/HardwareServices/FanMapper.cs` | 只把扫描入口参数从 `Computer` 换成硬件集合（可注入），**行为逐字不变**；风扇**多槽位展示**留给 F11 |
| D4 | `SensorMap.Rebuild` 增加一个重载、`FanMapper.ScanAndMapFans` 参数类型改变 | 测试接缝（LHM 的 `Computer.Hardware` 是按 group 计算出来的，**无法注入假硬件**）。原 `Rebuild(Computer, Settings)` 签名与产品调用点不变 |
| D5 | `LastAutoDisk` **仍存显示名**（未升级为稳定 ID） | 既有回归护栏 `ReviewMetricPresentationRegressionTests.F06_LogicalUsageSurvivesColdHotAndRefreshedPhysicalCache` 明确断言 `cfg.LastAutoDisk == 盘名`；该字段只是自动模式的热缓存线索（用户选择走 `PreferredDisk`，按身份匹配）。读回路径已升级为"身份优先、名称唯一命中才认" |

### 9.2 遗留

| # | 遗留 | 归属建议 |
|---|---|---|
| L1 | 存储的 `DeviceID` 在真机上的实际形态未实测（离线只能确认属性存在） | §8-1；F08b 允许列表键设计需复核 |
| L2 | NVIDIA 的 PCI bus id 在 LHM 0.9.6 **没有公开出口**（`NvApi.GPU_GetBusId` 只在 ctor 内部用）⇒ NVIDIA 显卡的身份依赖 `DeviceId`（PnP 路径）。若驱动重装导致该路径变化，只能靠"同型号 + 显存 + 名称"弱佐证 —— 本包**不猜** | F09b（NVML 按 PCI 身份取句柄时可顺带暴露 bus id） |
| L3 | 风扇 `Value` 格式（`fan:{父设备Id}/{传感器名}`）**尚未**接入 `PreferredCpuFan/PreferredCaseFan/PreferredCpuPump`（仍是旧显示名格式），避免在本包里做半截迁移 | F11：切换时用 `DeviceMigration.Migrate` 迁移旧值 |
| L4 | 旧配置里的磁盘显示名在**同型号**场景下仍会命中两块盘（兼容旧值，未破坏既有语义） | 迁移夹具已给出"歧义保留 + 提示"结论；设置页保存后即升级为稳定 ID |
| L5 | `device-bindings.json` 与 `settings.json` 是两个文件，理论上可能不同步（例如用户手动删 settings.json） | 绑定仓库可独立重建（首轮枚举即重建）；必要时可由 F10/F11 合并到 Settings（需协调者放开 `Settings.cs`） |
| L6 | 硬盘温度/活动率的 **Windows 计数器实例**映射未做（母计划 F07 步骤 4 后半句） | F08b：计数器实例与 `DeviceID` 的对应关系要靠允许列表一起定 |

---

## 10. 相邻问题（发现但未修）

| # | 现象 | 建议归属 |
|---|---|---|
| A1 | `HardwareScanner.ListAllFans` 仍按显示名 `Distinct()`（`HardwareScanner.cs:354`）——同主板上同名风扇仍会塌缩 | F11（多风扇槽位） |
| A2 | `SensorMap.SelectMonitoredGpus` 的旧实现在 `FindGpuByConfig` 落空时会**静默回退 `all[0]`**（用户选择被改写）—— 本包已改掉；`HardwareValueProvider`/`UIController` 里仍有"按序号取卡"的零散假设 | F10（GPU 映射自愈） |
| A3 | LHM 0.9.6 的 `Computer.Hardware` 是**每次按 group 重新计算**的列表 ⇒ 任何"把硬件塞进 Computer"的测试写法都不成立 | 已在 F07 用可注入集合替代；其它包若要测硬件路径需照此办理 |
| A4 | `DiskInfoToolkit 1.1.2` 的存储枚举会**逐个探测 `\\.\PhysicalDrive0..63`**（F09a 报告 §4.3 已记录） | F08b（允许列表必须发生在探测之前） |

---

## 11. 提交

| SHA | 说明 |
|---|---|
| `f776435` | `fix(F07): 稳定设备身份目录 + 槽位绑定 + 兼容迁移（#290/#538，#577 基础）` |
| `dc3b273` | `test(F07): 双同型号设备/枚举互换/设备消失/驱动更新的身份与迁移用例` |
| （本文件） | `docs(F07): 验收记录（最小设计 + 迁移 fixture 先行）` |

分支 `fix/F07-stable-device-identity`，**未推送远端、未动 master**；CI 阈值文件未改。
