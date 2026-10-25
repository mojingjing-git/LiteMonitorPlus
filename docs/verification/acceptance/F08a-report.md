# F08a 验收记录 —— 磁盘探测 CountersOnly 止血模式

## 1. 标识

- 工作包：P-F08a（磁盘探测 CountersOnly 止血模式）
- 分支：`fix/F08a-disk-counters-only`
- 提交 SHA 列表（`git log --oneline d52c0ab..HEAD`）：
  - `ae1a440` `fix(F08a): 磁盘探测 CountersOnly 止血模式（#466/#601/#245）`
  - `e173e78` `test(F08a): 磁盘探测策略护栏（假设备工厂/状态机/迁移/库前提）`
  - `（docs）` `docs(F08a): 验收记录`
- 基线 SHA（开工时 HEAD）：`d52c0ab`（master）
- 完成日期：2026-10-25
- 执行者：worker 子智能体（会话 `mvs_368012d2b2f9478cb6147c35e8f0f5b5`），父会话 `mvs_0ba82a4a7ca540c89f735ce982267f77`
- 工作目录：`G:\TOOL\LiteMonitor-fork\.worktrees\f08a`（未触碰主仓库工作树，未推送远端，未动 master）

## 2. 覆盖与结论

- 覆盖 issue：**#466**（占用可移动磁盘导致无法安全移除）、**#601**（与 HWiNFO 同时运行冲突）、**#245**（影响 Keil/DAPLink USB 设备识别 —— 仅"降低暴露面"，不宣称根治）
- 结论：**☑ 代码层验收通过**（策略层 + 取值层 + 设置页 + 迁移均已实现并有护栏）；
  □ 实机验收未做（全部列入 §6 待实机清单）
- 允许关闭的 issue 与条件：**暂不允许关闭**。理由：
  1. 本包是**止血**（不再构造存储设备），不是 #466/#601/#245 的完整修复；精细允许列表属 P-F08b；
  2. Process Monitor / 可移动盘 / HWiNFO / DAPLink 四项均**未实机验证**（见 §6）；
  3. 遗留 1 项清单外改动建议（Legacy 面板路线下 `DISK.Temp` 缺失占位，见 §7）。
- 本包做了什么（一句话）：新增 `DiskProbePolicy` + `DiskProbeGate`，让**第一次 `Computer.Open()` 之前**就把
  `IsStorageEnabled` 落成 `false` ⇒ LHM 0.9.6 **不构造任何存储设备**（不打开设备句柄、不做 SMART 探测）；
  磁盘速度/活动率改走 Windows 计数器，温度/SMART 明确缺失（`null` → 界面 `--`，**不是 0**）。

### 关键前提（IL 级取证，非推测）

LHM 0.9.6 `Computer.Open()` → `AddGroups()`：
`ldfld _storageEnabled` → `brfalse.s`（跳过）→ `newobj StorageGroup..ctor` ⇒ **开关为 false 时根本不构造存储设备组**。
本包用一条 IL 断言用例把这条前提钉住（`LHM仅在开关打开时才构造存储设备组`）：若上游改了这条语义，
本包的止血结论就不再成立，必须重新评估。

## 3. 用例与命令（可从干净检出复现）

| 命令 | 目的 | 结果（发现数/通过/失败） |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | 构建 | 0 错误；300 警告（全部为既有警告，本包未新增） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build` | 主测试（全量，**连续 2 次全绿**） | 1808 项：1801 通过 / **0 失败** / 7 跳过 |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --filter "FullyQualifiedName~DiskProbe"` | 本包新增用例 | 24 项：24 通过 / 0 失败 |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/… -c Debug --no-build` | GPU 非性能契约 | 19 项：19 通过 / 0 失败 |
| `dotnet test tests/LiteMonitor.Updater.Tests/… -c Debug --no-build` | 更新器 | 73 项：73 通过 / 0 失败 |

### 红→绿（可证伪性验证）

1. **先跑断言**：新增 24 条用例在实现之前即以"假设备工厂断言禁止设备构造次数为 0"为目标编写。
2. **变异验证（已执行）**：把 `DiskProbeGate.ApplyTo` 里的
   `target.IsStorageEnabled = Policy.StorageProbeEnabled;` 临时改成"不落策略"，
   重跑 ⇒ **恰好 2 条护栏变红**（`仅计数器模式_打开硬件树_禁止设备构造次数为0`、
   `旧配置缺字段_首次打开前禁止设备构造次数为0`），其余 22 条仍绿；
   随即恢复实现 ⇒ 24 条全绿。⇒ 这两条护栏**确实能证伪**，不是摆设。
3. **反向用例**：每条"必须是 0/必须是 null"的断言都配了一条反向用例
   （`兼容模式_打开硬件树_允许构造存储设备`、`兼容模式_仍然更新存储设备`、`兼容模式_磁盘温度仍走传感器路径`），
   保证断言不是恒真。

## 4. 环境

- OS / 版本：Windows 11 专业版（10.0.26300）
- DPI / 显示器拓扑：未改动（本次未做 DPI 相关工作）
- GPU / 驱动：NVIDIA GeForce RTX 3080 + GeForce RTX 3080 Ti（双卡）；另有若干虚拟显示适配器
- 关键依赖版本：`LibreHardwareMonitorLib 0.9.6`（`LiteMonitor.csproj`）、传递依赖 `DiskInfoToolkit 1.1.2`；
  NuGet 缓存 `G:\TOOL\LiteMonitor-fork\.nuget-packages`
- 构建/测试并发：同机 8 个执行智能体并行构建（`dotnet build/test` 明显变慢属正常，已耐心等待）

## 5. 新增测试清单

**新增发现数：24**（供协调者上调 `MIN_TEST_TOTAL`；本包**未**改动 CI 阈值）

`tests/LiteMonitor.Tests/DiskProbePolicyTests.cs`（19 条）

| 用例 | 验证了什么 |
|---|---|
| 仅计数器模式_打开硬件树_禁止设备构造次数为0 | 闸门在打开**之前**落策略 ⇒ 假设备工厂构造的"存储设备数 = 0"（工厂预置 `IsStorageEnabled=true`，只有真落策略才会变 0） |
| 兼容模式_打开硬件树_允许构造存储设备 | 反向用例：证明上一条可证伪 |
| 旧配置缺字段_首次打开前禁止设备构造次数为0 | 迁移场景下首次打开同样不构造存储设备 |
| 未配置_按仅计数器生效且进入迁移状态 | 缺字段 ⇒ 生效模式=仅计数器、迁移状态=Unconfigured、SMART/温度禁用、计数器仍允许 |
| 显式选择仅计数器_无迁移提示 | 显式 0 ⇒ 无迁移提示 |
| 显式选择兼容模式_允许存储探测 | 显式 1 ⇒ 允许存储探测/温度 |
| 非法配置值_退化为仅计数器 | 脏配置不得放开存储设备 |
| 切换到同一模式_不重建硬件树 | 幂等：同模式不触发落地动作（不空转重载） |
| 切换成功_进入新模式状态并清除迁移状态 | 成功迁移：模式/状态/迁移状态三处同时正确 |
| 切换时抛异常_保持原模式并进入降级状态 | 失败**不改模式**、进 Degraded、记录原因 |
| 切换后校验不通过_保持原模式并进入降级状态 | 校验失败同失败处理（不伪装成成功） |
| 切换失败后的回退有界_最多再落地一次 | 恢复有界：1 次失败 + 1 次回退，不递归 |
| 仅计数器模式_不更新存储设备 | `ProcessUpdate` 一次 `Update` 都不发起（不触碰设备句柄） |
| 兼容模式_仍然更新存储设备 | 反向用例：证明上一条可证伪 |
| 仅计数器模式_磁盘温度缺失而不是0且不回退旧值 | 温度缺失 = `null`（不是 0），且**不回退**上一代的旧温度 |
| 仅计数器模式_速度与活动率来自Windows计数器 | DISK.Read/Write/Activity 走 Windows 计数器 |
| 仅计数器模式_关闭Windows计数器时速度缺失而不是0 | 用户关掉计数器 ⇒ 缺失（不伪造 0） |
| 兼容模式_磁盘温度仍走传感器路径 | 兼容模式不改变既有取值路径（null 策略 = 不施加限制） |
| LHM仅在开关打开时才构造存储设备组 | **库前提守卫**（IL）：`_storageEnabled` 读取 → 条件分支 → `StorageGroup..ctor` 的先后与分支顺序 |

`tests/LiteMonitor.Tests/DiskProbeSettingsMigrationTests.cs`（5 条，挂 `cfgfile` 集合，真实读写测试目录下的 `settings.json`）

| 用例 | 验证了什么 |
|---|---|
| 旧配置缺字段_进入显式迁移状态且首次按仅计数器运行 | 真实加载路径：缺字段 ⇒ `null`（键不存在）+ 迁移状态 + 首次按仅计数器 |
| 未设置策略时_不往旧配置里写任何新键 | 迁移只发生在内存里：用户没做选择前，旧 settings.json **一个字节都不被改写**（不凭空多出 `DiskProbeMode` 键） |
| 旧配置缺字段_磁盘偏好与监控项原样保留 | 迁移**不清空** `PreferredDisk`/`LastAutoDisk`，不删 `DISK.Temp`/`DISK.Read` 监控项、可见性保留、排序位不清零 |
| 用户明确选择后_持久化并能回读 | Draft-Commit 落盘：键保留、值正确、回读一致 |
| 显式选择仅计数器_持久化后不再显示迁移提示 | 显式选择后迁移状态消失 |

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| **Process Monitor 冷启动无探测** | 运行 LiteMonitor，用 Process Monitor 过滤 `LiteMonitor.exe` 抓句柄/IOCTL：确认**没有** `\\.\PhysicalDrive*` 打开、没有 `SMART_READ_DATA`/`IOCTL_DISK` 类请求 | 用户 |
| **设置页扫描无探测** | 打开设置→系统/硬件页，确认磁盘下拉不枚举存储设备（应只剩"自动"+原值只读提示项），且抓包无新的设备访问 | 用户 |
| **定时维护无探测** | 让程序跑 ≥10 分钟（覆盖磁盘后台扫描/慢扫描/维护任务），Process Monitor 全程过滤，确认无周期性存储设备访问 | 用户 |
| **可移动盘安全移除** | 插 U 盘/移动硬盘 → 运行 LiteMonitor → 在"安全删除硬件"里弹出该盘：应不再提示"设备正在使用" | 用户 |
| **HWiNFO 共存** | 同时运行 HWiNFO（开 SMART/传感器）与 LiteMonitor：确认 HWiNFO 的 "ISA Lock" 告警不再点名 LiteMonitor | 用户 |
| **DAPLink / Keil USB 设备识别** | 接 Keil DAPLink，确认枚举识别正常、LiteMonitor 不再影响 USB/HID 枚举 | 用户 |
| **兼容模式实机验证** | 在设置页切到"兼容模式"：确认磁盘温度重新出现（说明模式切换的停采样→释放→重建→恢复链路在真机可用） | 用户 |
| **模式切换失败态** | 真实失败场景（打开存储设备失败）下确认 UI 显示"降级"状态且计数器数据仍在 | 用户 |

## 7. 偏离与遗留

### 与派发单的偏离

1. **`src/System/HardwareMonitor.cs` 改动 25 增 / 4 删 = 29 行（≤30 ✓）**，全部落在存储启用策略区域：
   - 字段 `_diskProbePolicy` / `_diskProbeGate`、公开属性 `DiskProbePolicy`；
   - 构造函数：策略先于 `Computer` 构造、`IsStorageEnabled = _diskProbePolicy.StorageProbeEnabled`、
     `new DiskManager(policy:…, counters:…)`；
   - 两处 `Computer.Open()` 改为经闸门 `DiskProbeGate.Open(new LhmHardwareTree(_computer))`（启动 + 重载）；
   - 新增 `ApplyDiskProbeMode(DiskProbeMode)`（切换入口）。
   - **未触碰网络取值分支**（F03 区域）、未触碰任务栏/UI 区域、未触碰 `SensorMap.cs` / `FanMapper.cs` / GPU 文件。
2. **本包未修改 `HardwareScanner.cs` 与 `DriverInstaller.cs`**（虽在改动清单内，但无必要改动）：
   - `HardwareScanner.ListAllDisks` 在仅计数器模式下自然返回空（硬件树里没有存储设备）；
     设置页的 `FillSync` 会把用户原值保留成"只读提示项"（**不被清空**）——已由迁移用例守住。
   - `DriverInstaller` 的职责是 PawnIO / PresentMon 组件，与存储探测策略无关；驱动安装策略属 F08b/F09 决策范围。
3. **迁移提示用"设置页常驻状态行"实现**（不是弹窗）：避免在 UI 线程引入新的模态流程；
   状态行同时显示当前模式、迁移提示与降级原因。语言键 9 语种同步（只新增 key，不改既有语义）。

### 清单外的必要改动 —— **建议协调者处理**（本包**未**自行修改）

1. **`src/Core/MetricUtils.cs`（建议）**：面板 Legacy 路线下，缺失读数的 `"--"` 占位目前只在
   `CPU.Voltage` / `MEM.Commit.Load` 的特判分支里实现；`DISK.Temp` 在仅计数器模式下取值层已返回
   `null`（缺失、**不是 0**），但 Legacy 面板会保留上一帧数值（首次为 0）。
   建议按同范式加一条"`DISK.Temp` 缺失 ⇒ `--`"分支，或统一交给 **F11（缺失/过期状态）** 处理。
   （网页端与新渲染场景已能正确显示 `--`：`LiteWebServer` 对空值输出 `--`，`UiSceneSnapshotAdapter` 对缺失项输出 `--`。）
2. **`src/System/HardwareServices/HardwareValueProvider.cs`（建议，F08b 一并收敛）**：
   该文件按"用户指定了特定磁盘"跳过 Windows 计数器分支，因此本包把"仅计数器模式下的计数器兜底"
   放在了 `DiskManager.GetBestValue`（在改动清单内）。建议 F08b 把这条规则收敛到 ValueProvider 的单一判定点。
3. **`src/Core/SettingsHelper.cs`（可选）**：本包用 `Settings.DiskProbeMode == null`（键不存在）表达"未设置"，
   并给该字段加了 `[JsonIgnore(Condition = WhenWritingNull)]` ⇒ **用户没做选择前，旧配置文件一个字节都不被改写**。
   若协调者希望进一步区分"全新安装"与"旧配置迁移"（让新装用户不看到迁移提示），
   可在 `SettingsHelper.Load` 里按"文件是否存在/是否含该键"再细分一个字段（本包未做，避免扩大改动面）。

### 发现的相邻问题（不属于本包，建议归属）

- **`ReviewMarketPersistenceRegressionTests` 全量跑时 flaky —— 既有缺陷；本包已把它的触发概率降到 0（见下）**：
  - 症状：`Assert.Throws<MarketRecordPersistenceException>` 实际收到
    `System.UnauthorizedAccessException: Access to the path is denied.`（或同类的记录集合断言不一致）。
  - 根因（读代码定位）：`ReviewMarketPersistenceRegressionTests.PersistenceScope` 在每条用例期间把**全局**
    `SettingsHelper.PathOverride` 指向自己的沙箱目录；而 `MarketE2ETests`（**不在** `cfgfile` 集合，
    属默认并行集合）会调用 `Settings.Load()` + `cfg.Save()` ⇒ 两者并发时，市场用例的落盘被写进
    沙箱、与市场用例自己的写撞车 ⇒ 文件被独占 ⇒ UnauthorizedAccessException。
    仓库自己记录过这一现象（`CfgFileCollection.cs` 与 `PersistenceScope` 构造函数注释：
    "单跑全绿、全量间歇挂（评审同机三次复现三条不同用例）"）。
  - **对照证据**（同一台机器、同一时段）：

    | 运行方式 | 结果 |
    |---|---|
    | 基线 `d52c0ab`（干净检出 + 强制重建）全量 ×2 | 第 1 次**红**（同一个类、同一条断言）、第 2 次绿（1777/0/7，1784 项） |
    | 本分支早期版本（策略用 `-1` 哨兵 ⇒ settings.json 多出一个键）全量 ×6 | 每次 1 失败（同一类），其余 1799 通过、7 跳过、1807 项 |
    | 本分支**仅产品改动**（临时移除本包 24 条新用例）全量 ×2 | 2 次全红 ⇒ 触发与本包的新测试文件无关 |
    | 只跑两个既有测试类（**不含**本包任何测试文件）×N | 早期版本 2/13 失败；基线 2/21 失败 ⇒ 失败率同量级 |
    | **本分支最终版**（策略改用 `null` + `WhenWritingNull`）全量 ×2 | **两次全绿**（1801/0/7，1808 项） |

  - 结论与处置：这是**时序余量极小**的既有竞态 —— 任何扰动（哪怕 settings.json 多写 30 字节）都会翻转结果。
    本包最终版把 `Settings.DiskProbeMode` 改成 `int?` + `[JsonIgnore(WhenWritingNull)]`
    ⇒ **用户没做选择前，旧配置文件一个字节都不被改写**，负载不再扰动那个竞态 ⇒ 全量连续 2 次全绿。
    ★ 该竞态本身**未被修复**（本包未改动相关测试与实现）。
  - 建议协调者处理（**一行**，建议在批次集成前顺手做掉）：
    给 `tests/LiteMonitor.Tests/MarketE2ETests.cs` 加上 `[Collection("cfgfile")]`
    （该类会写 settings.json，按本仓约定本就该进 `cfgfile` 集合）。
    ★ 该测试文件不在本包改动清单内，故**未自行修改**。
- **`HardwareMonitor.ApplyDiskProbeMode` 的校验依赖 `LastHardwareError`**：该字段是**静态**的，
  理论上可能被其它路径的失败"沾上"（误判为降级）。已在报告中标出，若协调者要求更严格，可改为比较
  硬件树指纹（仅计数器模式下树内不得有 Storage 设备 —— 该判定已同时存在）。
