# F08b 验收记录 —— 精细允许列表与控制器策略（含库能力 spike）

## 1. 标识

- 工作包：P-F08b（精细允许列表与控制器策略，含库能力 spike 决策门）
- 分支：`fix/F08b-disk-allowlist`
- 基线 SHA（开工时 HEAD）：`19cc9ac`
- 完成日期：2026-10-26
- 执行者：worker 子智能体（会话 `mvs_ec18da3107a04968bed7484f69b03c9b`），父会话 `mvs_0ba82a4a7ca540c89f735ce982267f77`
- 工作目录：`G:\TOOL\LiteMonitor-fork\.worktrees\f08b`（未触碰主仓库工作树，未推送远端，未动 master）
- 前置满足：P-F07 与 P-F09a 成果已在基线内（已读 `F08a-report.md` 与 `F09a-report.md`）

## 2. ★ Spike 结论（决策门 —— 本包最关键产出）

### 结论：**☒ Spike 未通过。暂不开放「仅内部盘 / 明确允许列表的 SMART 模式」。**

> 判定标准（来自派发单）：排除必须发生在库**创建设备对象与打开句柄之前**。
> 「构造完整设备列表后再过滤」视为**未隔离**（句柄已经开过了）。

**两版依赖都做不到**，且**升级到 F09a 推荐组合也做不到**：

| 变体 | 依赖 | 枚举入口 | 有允许列表/过滤形参？ |
|---|---|---|---|
| Baseline（生产） | LHM **0.9.6** / DiskInfoToolkit **1.1.2** | `StorageManager.ReloadStorages()`（无参）→ `get_Storages()` | **无**（连 USB 排除布尔都没有） |
| Latest（F09a 推荐） | LHM **0.9.7-pre771** / DiskInfoToolkit **2.2.2** | `Storage.GetDisks()`（无参） | **无**；只有全局布尔 `ExcludeUsbDevices` |

因此按派发单第 1 条，本包**只交付 controller 策略部分**，磁盘允许列表**未开放**。

### 2.1 证据 A —— 枚举入口没有过滤形参（判定核心）

Baseline `facts` 场景的判定行：

```
== 是否存在'允许列表/过滤委托'形参（这是本次 spike 的判定核心） ==
  结论：DiskInfoToolkit **没有任何** Predicate/Func<T,bool> 形式的过滤形参 ⇒ 无法在枚举前按允许列表裁剪。
```

Latest（2.2.2）`facts` 命中的 `Func` 形参逐条核查后**均不可用作允许列表**：

| 命中 | 可达性 | 为什么不可用 |
|---|---|---|
| `StorageDetectionEngine.GetDisks(Func<…>)` | 类 public，但**带委托的两个重载是 `internal`**，只有无参版是 public | fork 侧**调不到**；且 `Storage.GetDisks()` 内部走的就是无参版 |
| `LinuxStorageDeviceChangeMonitor.*(Predicate<string>)` | Linux 路径 | Windows 不适用 |
| `WindowsStorageSpaceVolumeReader.PopulateFromVolumeExtents(Func<…>)` | 内部实现细节 | 非枚举入口 |
| `StorageProbeDispatcher.TryProbeOperation(Func<…>)` | 内部探测调度 | 发生在**设备已构造之后** |

### 2.2 证据 B —— 句柄在**枚举期内**就被打开（所以"事后过滤"等于没隔离）

**Baseline**：`il DiskInfoToolkit.StorageManager!ReloadStorages` 里就有 `SafeFileHandler.OpenHandle`。
进一步下钻 `il DiskInfoToolkit.Storage!ctor`（**每个盘的构造体**）：

```
SmartInfo..ctor -> … -> Storage.set_StorageController -> StorageDevice.get_PhysicalPath
 -> SafeFileHandler.OpenHandle      ← ★ 句柄在这里就开了
 -> … -> Storage.IdentifyStorageController -> Storage.GetDiskGeometry -> Storage.GetDiskInformation
 -> Storage.UpdatePartitions -> Storage.IdentifyDisk
 -> SafeFileHandler.CloseHandle
```

调用链（调用方唯一能碰到的 `get_Storages()` 返回值**已是成品**）：

```
Computer.Open() → StorageGroup..ctor → AddHardware()
  → StorageManager.ReloadStorages() → CreateStorage() → Storage..ctor → OpenHandle/IOCTL …   ★ 句柄已开
  → StorageManager.get_Storages()     ← 调用方**这时**才拿到清单
  → Enumerable.Select(...new StorageDevice(...))
```

**Latest（2.2.2）同样如此，且顺序更糟** —— `il …StorageDetectionEngine!GetDisks`：

```
… -> CreateBaseDevice -> … -> AttachStandardStorageProperties   ← ★ 这里就 OpenDevice
  -> UsbStorageDeviceIdentifier.IsUsbDevice
  -> ApplyDeviceFilters                                          ← 过滤器在**之后**
  -> … -> List`1.RemoveAll                                      ← USB 排除发生在这里
```

`il …!AttachStandardStorageProperties` 含 `IStorageIoControl.OpenDevice`。
⇒ **连 `ExcludeUsbDevices=true` 也是"开过句柄再丢弃"** —— 能减少后续暴露，但**不构成构造前隔离**，更不等于允许列表。

### 2.3 证据 C —— LHM 侧也没有"构造前钩子"

两变体的 `Computer` 成员取证**完全一致**：只有 `HardwareAdded` / `HardwareRemoved`（构造**之后**通知）
与 `Finalize/Equals/GetHashCode` 三个虚方法 —— **没有任何可覆写的枚举/过滤点**。
`IGroup` 抽象只有 `get_Hardware` / `GetReport` / `Close`：组**自己产出**设备清单，不提供注入已过滤清单的接缝。

### 2.4 决策

| 选项 | 结论 |
|---|---|
| 现有库直接做允许列表 | **否**（证据 A + B） |
| 升级到 F09a 推荐组合后做 | **否**（证据 A + B；2.2.2 的 USB 排除仍在开句柄之后） |
| 修库入口（私有 fork） | **技术上可行但本包不做**：超出授权（本包对 `HardwareMonitor.cs` 无改动额度），且与 F09b 依赖锁定决策冲突 ⇒ **交协调者裁决** |
| **暂不开放该 SMART 模式** | **✔ 采纳** |

证据全文与复现命令：`tools/F08bDiskAllowlistSpike/SPIKE-CONCLUSION.md`。

## 3. 顺带的取证发现（对 #245 有直接价值）

两个变体的 `Computer.AddGroups()` IL 显示：**`_controllerEnabled` 与 `_storageEnabled` 是同一套"构造前隔离"机制**，
读取字段 → `brfalse` → 跳过组构造。且**8 个控制器组全部受 `_controllerEnabled` 单一开关守护**：

```
[58]  ldfld _controllerEnabled
[59]    brfalse
[63]  newobj TBalancerGroup      ← 受 [58] 守护
[68]  newobj HeatmasterGroup  [73] AquaComputerGroup  [78] AeroCoolGroup
[83]  newobj NzxtGroup        [88] RazerGroup         [93] ArcticGroup   [98] MsiGroup
```

⇒ **`IsControllerEnabled=false` 是真的构造前隔离**：8 个控制器组根本不被 `new`，
因此**不碰任何 HID/USB 设备**。这是本包 controller 策略能站住的根本原因（有 IL 级保证，非推测）。

**反面事实（如实记录，决定本包的能力边界）**：控制器组一旦被构造，
`NzxtGroup..ctor` / `AquaComputerGroup..ctor` 会**立即** `DeviceList.GetHidDevices`
并为每个匹配设备构造驱动对象（打开 HID 句柄）。⇒ **单个 USB 设备的排除没有任何构造前入口**
（与磁盘允许列表同理）。

## 4. 覆盖与结论

- 覆盖 issue：**#466**、**#601**、**#245**
- 结论：☑ **代码层验收通过**（controller 策略部分）；**磁盘允许列表模式未开放**（spike 未通过）
- 允许关闭的 issue 与条件：**暂不允许关闭**。理由：
  1. 本包是**缓解**不是根治 —— 开关是构造前隔离（真隔离），但**单个设备排除作用在构造之后**；
  2. #466 / #601 的磁盘侧本轮**没有新交付**（spike 未通过，维持在 F08a 的 CountersOnly 止血）；
  3. DAPLink/HWiNFO/USB 移除/Process Monitor 四项均**未实机验证**（见 §8）。

### ★ 边界声明（派发单明确要求，不得省略）

**磁盘策略不冒充 #245 完整修复。**本包的 controller 部分对 #245 的作用仅限：
① 提供「彻底关闭控制器扫描」的明确开关（构造前隔离，不碰任何 HID/USB 设备）；
② 提供排除清单**降低**已构造设备的二次触碰频率。
**它不能阻止某一个 USB 设备被枚举/打开** —— 那需要库入口改造（已在 §2.4 交裁决）。

## 5. 用例与命令（可从干净检出复现）

环境：
```powershell
$env:NUGET_PACKAGES='G:\TOOL\LiteMonitor-fork\.nuget-packages'
$env:DOTNET_CLI_HOME='G:\TOOL\LiteMonitor-fork\.dotnet-cli-home'
```

| 命令 | 目的 | 结果 |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | 构建 | **0 错误** |
| `dotnet build LiteMonitor.sln -c Release --nologo` | Release 构建（查 CS8632/CS0738 陷阱） | **0 错误**；两个新测试文件**零 CS8632 警告** |
| `dotnet test tests/LiteMonitor.Tests/… -c Debug --no-build` | 主测试全量 | **2069 通过 / 0 失败 / 7 跳过 / 总计 2076** |
| `… --filter "FullyQualifiedName~ControllerProbe"` | 本包新增用例 | **28 通过 / 0 失败** |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/… -c Debug --no-build` | GPU 非性能契约 | **19 通过 / 0 失败 / 总计 19** |
| `dotnet test tests/LiteMonitor.Updater.Tests/… -c Debug --no-build` | 更新器 | **73 通过 / 0 失败 / 总计 73** |
| 三个测试项目合计 | 与 CI 阈值对照 | **总计 2168**（2069+19+73 通过，7 跳过） |

### 红→绿与可证伪性（已执行两次变异验证）

1. **变异 1**：把 `ControllerProbeGate.ApplyTo` 里落策略的一行注释掉（模拟"忘了设开关"）
   ⇒ **恰好 2 条护栏变红**（`关闭模式_打开硬件树_控制器设备构造次数为0`、`自动模式且无风扇需求_构造次数为0`），
   其余 21 条仍绿；随即恢复 ⇒ 28 条全绿。
2. **变异 2**：把 `Settings.ControllerProbeMode` 默认值从 `null` 改成 `1`
   ⇒ **恰好 3 条迁移护栏变红**；随即恢复 ⇒ 全绿。
   ⇒ 这些护栏**确实能证伪**，不是摆设。
3. **反向用例**：每条"必须是 0"的断言都配了反向用例
   （`自动模式且有风扇需求_允许构造控制器设备`、`强制开启模式_无风扇需求也构造控制器设备`），
   保证断言不是恒真。

## 6. 环境

- OS：Windows 11 专业版（10.0.26300）
- GPU：NVIDIA RTX 3080 + RTX 3080 Ti（双卡）；磁盘：**3 块内置 NVMe，无 USB/外置盘**
  ⇒ 磁盘相关的实机场景本机无法完成，全部列入待实机（与 F09a §6.1 同一限制）
- 依赖：`LibreHardwareMonitorLib 0.9.6`（产品）/ 0.9.7-pre771（spike 对照）；传递 DiskInfoToolkit 1.1.2 / 2.2.2

## 7. 新增测试清单

**新增发现数：28**（供协调者上调 `MIN_TEST_TOTAL`；本包**未**改动 CI 阈值）

### `tests/LiteMonitor.Tests/ControllerProbePolicyTests.cs`（23 条）

| 用例 | 验证了什么 |
|---|---|
| 关闭模式_打开硬件树_控制器设备构造次数为0 | ★ 核心：闸门在打开**之前**落策略 ⇒ 假设备工厂构造的"控制器设备数 = 0"（工厂预置 `true`，只有真落策略才会变 0） |
| 自动模式且无风扇需求_构造次数为0 | 自动模式 + 无需求 ⇒ 同样不构造（既有行为） |
| 自动模式且有风扇需求_允许构造控制器设备 | **反向用例**：证明上一条可证伪 |
| 强制开启模式_无风扇需求也构造控制器设备 | On 模式无条件构造 |
| 关闭模式_即使有风扇需求_构造次数仍为0 | ★ #245 核心语义：用户明确关闭 ⇒ **任何**需求都打不开 |
| 旧配置缺字段_按自动运行且进入迁移状态 | 缺字段 ⇒ Auto + 迁移状态；有需求才开 ⇒ **不回退已有能力** |
| 显式选择关闭_无迁移提示 | 显式选择 ⇒ 迁移状态消失 |
| 非法配置值_退化为自动 | 脏配置不得放开硬件访问（退到 Auto，不是 Off，避免"配置写错就彻底读不到风扇"） |
| 切换到同一模式_不重建硬件树 | 幂等：同模式不空转重载 |
| 切换成功_进入新模式状态并清除迁移状态 | 模式/状态/迁移状态三处同时正确 |
| 切换时抛异常_保持原模式并进入降级状态 | 失败**不改模式**、进 Degraded、记录原因 |
| 切换后校验不通过_保持原模式并进入降级状态 | 校验失败同失败处理（不伪装成成功） |
| 切换失败后的回退有界_最多再落地一次 | 恢复有界：1 次失败 + 1 次回退，不递归 |
| 排除清单_默认包含DAPLink模式 | 默认清单含 daplink / cmsis-dap / 0d28 |
| 排除清单_追加后立即生效 | ★ 实时生效（不需要重建硬件树、不缓存历史结果） |
| 排除清单_移除后立即生效 | 同上（移除侧） |
| 排除清单_整体替换后立即生效 | SetPatterns 后旧规则立即失效 |
| 排除清单_为空时不排除任何设备 | 空清单 = 什么都不排除（不是什么都排除） |
| 排除清单_匹配大小写不敏感 | OrdinalIgnoreCase |
| 排除清单_按显示名也能命中 | Identifier 无特征时显示名兜底 |
| 排除清单_恢复默认 | ResetToDefaults |
| 排除清单_忽略空白项 | 空白模式不参与匹配 |
| LHM仅在控制器开关打开时才构造控制器组 | ★ **库前提守卫**（IL）：`_controllerEnabled` 读取 → 分支 → 8 个控制器组构造的先后与分支顺序；若上游改语义，本包结论即失效 |

### `tests/LiteMonitor.Tests/ControllerProbeSettingsMigrationTests.cs`（5 条，挂 `cfgfile` 集合，真实读写 settings.json）

| 用例 | 验证了什么 |
|---|---|
| 旧配置缺字段_进入显式迁移状态且按自动运行 | 真实加载路径：缺字段 ⇒ `null` + 迁移状态 + **按自动运行（与改动前一致）** |
| 未设置策略时_不往旧配置里写任何新键 | 用户没做选择前，旧 settings.json **一个字节都不被改写**（两个新键都不出现） |
| 旧配置缺字段_风扇偏好与监控项原样保留 | 迁移**不清空** `PreferredCpuFan`/`PreferredCpuPump`/`PreferredCaseFan`，不删 `CPU.Fan`/`CASE.Fan` 监控项、可见性保留、排序位不清零 |
| 用户明确选择后_持久化并能回读 | Draft-Commit 落盘：两个键保留、值正确、回读一致 |
| 排除清单从配置加载_实时生效 | 配置里的清单直接喂给排除器 ⇒ 立即生效 |

## 8. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| **Process Monitor 冷启动无探测** | 运行 LiteMonitor，过滤 `LiteMonitor.exe` 抓句柄/IOCTL：确认**没有** `\\.\PhysicalDrive*` 打开、没有 SMART/IOCTL_DISK 类请求 | 用户 |
| **Process Monitor 设置页扫描无探测** | 打开设置→系统/硬件页，确认下拉不枚举存储设备，且抓包无新的设备访问 | 用户 |
| **Process Monitor 定时维护无探测** | 跑 ≥10 分钟（覆盖后台扫描/慢扫描/维护），全程过滤，确认无周期性存储设备访问 | 用户 |
| **可移动盘安全移除** | 插 U 盘/移动硬盘 → 运行 LiteMonitor → "安全删除硬件"弹出：应不再提示"设备正在使用" | 用户 |
| **HWiNFO 并行** | 同时运行 HWiNFO（开 SMART/传感器）与 LiteMonitor：确认 ISA Lock 告警不再点名 LiteMonitor | 用户 |
| **DAPLink 连接** | 接 Keil DAPLink，确认枚举识别正常；LiteMonitor 切到"关闭控制器"后不再影响 USB/HID 枚举 | 用户 |
| **DAPLink 断开** | 断开 DAPLink，确认程序不闪退、不残留线程 | 用户 |
| **DAPLink 排除串核对** | ★ 默认排除串 `daplink`/`cmsis-dap`/`0d28` 是**按行业通用 CMSIS-DAP VID（0x0D28）推断**的；**必须在目标机核对真实 VID/PID 是否命中**（LHM 不暴露 VID/PID，只能按显示名/Identifier 匹配） | 用户 |
| **控制器关闭模式实机** | 设置页切到"关闭控制器"：确认风扇/水泵读数明确缺失（`--`，不是 0），且 Process Monitor 无 HID 设备访问 | 用户 |

## 9. 偏离与遗留

### 与派发单的偏离

1. **磁盘允许列表未实施**（spike 未通过 ⇒ 派发单第 2 条不执行）。这是**遵守**派发单而非偏离；
   派发单已预判该分支：「spike 不通过则本包只交付 controller 策略部分，并在报告明确说明」。
2. **`src/System/HardwareMonitor.cs` 一字未改**（本包无授权额度）。
   ★ 因此**产品路径尚未接线**：新增的 `ControllerProbePolicy` / `ControllerProbeGate` /
   `ControllerDeviceExclusion` 目前是**独立新文件 + 配置字段**，
   `HardwareMonitor` 仍在用既有的 `ShouldEnableController()`。
   接线需要改动 `HardwareMonitor.cs` 的构造函数与 `RefreshHardwareConfig`（约 10～20 行），
   **未获授权，故未做** ⇒ 见 §10 需裁决事项。
3. **未修改 `HardwareScanner.cs` / `DriverInstaller.cs` / `DiskManager.cs`**（无必要改动）：
   spike 未通过 ⇒ 磁盘侧无新策略需要下沉。
4. **改了 `src/Core/Settings.cs`（+23 行，仅新增两个字段 + 注释）**：派发单「与 F08a 同一文件集」内，
   且沿用 F08a 已验证的 `int?` + `[JsonIgnore(WhenWritingNull)]` 迁移范式
   ⇒ 旧配置**一个字节都不被改写**（已有用例守住）。
5. **验证工程落点 `tools/F08bDiskAllowlistSpike/`**：与 F09a 同族（`tools/**` 已被产品 csproj 排除，
   放 `research/` 会打断产品构建 —— 见 F09a §9）。**未改动 `LiteMonitor.csproj`。**

### 禁改清单核对

- `.github/workflows/ci.yml`、`release.yml`：**一字未动**；`MIN_TEST_TOTAL` / `MAX_SKIPPED_TESTS` 保持原值（未上调，见 §10）。
- GPU 映射文件、SensorMap.cs、FanMapper.cs：**未触碰**。
- 未推送远端、未动 master。
- `tests/LiteMonitor.Tests/PluginMarketRecoveryBoundaryTests.cs` 的改动**不是本包做的**
  （开工前 `git status` 即已存在该项修改，属基线自带的未提交改动），本包**未**修改它、也未 revert。

### 发现的相邻问题（建议归属）

1. **`SettingsHelper.Load` 会规范化重排 `MonitorItems` 的 SortIndex**
   （本包用例里写入 205，加载后变成 214）。本包已把断言改成"排序位未被清零"这一**真正要守的语义**。
   若其它包依赖精确 SortIndex，需注意这一既有行为。
2. **默认排除串是按行业通用 VID 推断的**（见 §8「DAPLink 排除串核对」）——这是本包**唯一**带推断成分的产物，
   已在代码注释与本报告同时标注，不声称已验证。
3. **F09b 的决策依赖本次 spike 结论**：若协调者最终选择「修库入口」路线来解锁磁盘允许列表，
   需重写本报告的 §2 决策行，并重新评估 §4 的边界声明。

## 10. 需要协调者裁决的事项

1. **【最重要】磁盘允许列表的路线**：本包证明「现有库 + F09a 推荐组合都做不到构造前过滤」。
   三选一请裁决：
   - (a) **维持不开放**（本包现状）—— 磁盘侧继续只有 F08a 的 CountersOnly 止血；
   - (b) **修库入口**（私有 fork LHM/DiskInfoToolkit，给枚举加过滤形参或把过滤提到 `OpenDevice` 之前）
     —— 需授权新包、并与 F09b 的依赖锁定决策协调；
   - (c) **另立辅助进程隔离**（母计划已明确不在本轮引半套 IPC）—— 若选它，需单独立项。
2. **产品路径接线授权**：是否授权本包（或后续包）在 `src/System/HardwareMonitor.cs` 改动约 10～20 行，
   把 `ControllerProbePolicy` / `ControllerProbeGate` 接进构造函数与 `RefreshHardwareConfig`？
   在接线完成前，**新增策略对最终用户是不可达的**（只有测试覆盖）。
3. **`MIN_TEST_TOTAL` 上调**：本包新增 28 条用例（主项目 2071 → 2076 中的 28 条在本包两个新文件里），
   三个项目合计由 2139 增至 **2168**。需由协调者在批次集成时
   按「逐字一致」原则上调两个 workflow 的阈值（本包**未**擅自改动）。
