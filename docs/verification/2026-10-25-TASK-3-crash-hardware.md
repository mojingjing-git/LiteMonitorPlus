# TASK-3 上游 issue 核实报告：崩溃、异常与硬件采集健壮性

- **任务单**：TASK-3（崩溃、异常与硬件采集健壮性），共 11 个 issue
- **检查日期**：2026-10-25
- **上游代码**：`upstream/master` = commit `3b91b5c`（"fix: 完成 1.3.7 发版审查并修复监控与网络边界问题"，csproj `Version=1.3.7`）
- **fork 代码**：本地 `master` = commit `5e756e7`（"feat(theme-market): M2+M3 主题索引解析/安装事务/状态与 D7 卸载守卫"）
  > 说明：取证期间并行的其他工作线把 `master` 推进到 `2ef66fc`（主题市场 M4/M5）。本报告的全部 fork 侧行号已在 `2ef66fc` 上**逐条复核**，结论不受影响；未复核前引用的行号仅 `src/UI/UIController.cs` 的 `1906 → 1909` 一处发生偏移，已更正。
- **取证方式**：只读 git 命令（`git show` / `git grep` / `git log` / `git diff` / `git ls-tree`）、`gh issue view` 拉取 issue 正文与评论、下载 issue 截图取证、2 组本机运行时实验。
- **未做**：未构建、未运行 LiteMonitor、未安装/卸载驱动、未做磁盘拔插或 GPU 切换实机实验、未取得崩溃转储。凡依赖实机条件的结论一律标注为「未复现 / 信息不足」。

> **方法学声明**：上游与 fork 分别取证、分别判定。凡 fork 侧结论，均以 `master` 工作树的实际源码为准，不从上游 issue 直接推断。凡结论为「未复现」，均在下文写明缺少的具体条件。

---

## 1. 汇总表

| Issue | 当前是否 open | 影响等级 | 上游结论 | fork 结论 | 证据位置 | 复现/验证结果 | 下一步 |
|---|---|---|---|---|---|---|---|
| #502 AccessViolation 崩溃（NvmlDeviceGetPowerUsage） | 是 | 程序崩溃（原生 AV，进程终止） | **部分确认**：根因机制在 LHM 0.9.6 库内（缓存 NVML 句柄），应用层根因未修；但现行 master 已有 GPU 分支 `catch (Exception)`，报告中的「静默退出 + 无错误日志」与现行代码不完全自洽 | **部分确认**：根因同样未修（仍用 LHM 0.9.6）；但异常可观测性优于上游（写 `HardwareErrorLog` 而非仅 `Debug.WriteLine`） | 上游 `src/System/HardwareMonitor.cs:203,213,258`；fork `src/System/HardwareMonitor.cs:485-493,560-569`；LHM v0.9.6 `NvidiaGpu.cs` `_nvmlDevice`/`Update()` | 静态确认 + 本机实验确认 .NET 8 下 `AccessViolationException` **可被** `catch (Exception)` 捕获；未取得崩溃转储 | P0：移植 LHM PR #2539（每轮重取句柄 + PCI 身份校验 + 关闭同步） |
| #579 报错闪退，流量和 GPU 无数据 | 是 | 程序崩溃（后台线程 NRE）+ 采集链路中断 | **确认（根因在依赖库内，应用层无法拦截）** | **确认（同上游，根因未修）** | `DiskInfoToolkit 1.1.2` `StorageManager.DevicesChangedListener()` → `HandleUnpartitionedDrive` → `new Storage(...)` → `IdentifyStorageController()` | 堆栈取自 issue 正文（第三方库内部线程），已核对调用链 | P1：库内补异常边界 + 迁移 LHM 0.9.7-pre767 / DiskInfoToolkit 2.x |
| #290 核显/独显切换时闪退 | 是 | 程序崩溃（设备移除后更新已失效硬件） | **部分确认**：上游 master 已有 `[Fix #290]` GPU 分支 try/catch + 失败重载，v1.3.6 亦有 | **部分确认**：恢复路径与上游同构（GPU 分支独立 catch → `needsReload` → 延迟重载）；但根因（NVML 句柄缓存 + 索引式 GPU Key）未修 | 上游 `HardwareMonitor.cs:199-213,260-280`；fork `HardwareMonitor.cs:477-495,571-590`；fork `SensorMap.cs:126-160` | 静态确认恢复链路存在；**未复现**（本机为单/多卡无独显热切换笔记本环境） | P1：GPU 身份改用稳定 PCI ID + 句柄重取 |
| #582 识别磁盘 SMART 时空引用崩溃 | 是 | 程序崩溃 | **信息不足**（正文仅日志附件，未取得堆栈） | **信息不足**（同上游） | issue 正文只有 `LiteMonitor_Error.log` 附件；fork `docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md:135-139` 记录「附件未能取得」 | 未取得附件内容，**未核对其堆栈** | P1：索取附件堆栈后再归类，不得按标题并案 |
| #574 突然报错 Parameter is not valid. | 是 | 程序崩溃（UI 线程异常，有全局兜底对话框） | **信息不足**：报告堆栈要求源码显式调用 `Font.GetHeight()`，上游 v1.3.6 与 master 的 `src/` 全树均无该调用 | **部分确认（存在同类结构风险）**：fork 在绘制路径重新引入 `Font.GetHeight(Graphics)`，调用点为 `HorizontalLayout` 构造 | 上游 `src/UI/UIRenderer.cs:63,86`（用 `Font.Height`）；v1.3.6 `git grep GetHeight -- src/` 无匹配；fork `src/UI/HorizontalLayout.cs:47,55-57` + `src/UI/UIController.cs:1906` | 本机实验：`Font.Height`=21（缓存整数）、`Font.GetHeight()`=20.32031（实时 GDI+ 查询）；`GetHeight(已释放 Graphics)` 抛 `ArgumentException` | P1：fork 侧把 `GetHeight(graphics)` 换成缓存 `Font.Height`；上游侧索取报告者二进制版本 |
| #601 与 HWiNFO 冲突 | 是 | 驱动/设备被占用 | **确认**（截图直接指名 LiteMonitor 持有共享同步对象） | **确认（同上游，根因未修）** | HWiNFO "ISA Lock" 告警截图指名 `LiteMonitor.exe [@ .NET TP Worker]`；两侧 `IsStorageEnabled = true` 且无 USB/SMART 访问策略 | 已取得截图取证；**未复现**（本机未同时运行 HWiNFO + SMART 采集） | P0：引入磁盘访问策略（默认不进 SMART 识别） |
| #577 两个机箱风扇只显示一个 | 是 | 单项读数缺失 | **确认**（单槽位 Key 设计） | **确认（根因未修）** | 上游/ fork `FanMapper.cs:139` `targetMap["CASE.Fan"] = caseFan;`（只有一个 CASE.Fan 槽） | 静态确认；**未复现**（本机无多机箱风扇硬件） | P2：风扇 Key 改为带实例序号的多槽位 |
| #514 CPU & GPU fans show 0 RPM | 是 | 单项读数缺失（显示语义误导） | **确认**（缺失传感器被渲染为 0） | **确认（根因未修）** | 上游 `MetricItem.cs:76-77` `DisplayValue` 默认 `0f` + `UIController.cs:326,451` 仅在 `HasValue` 时赋值 | 静态确认；**未复现**（本机无该机型风扇传感器） | P2：区分「无传感器 / 读取失败 / 真实 0」三种状态 |
| #538 2 块同型号 SSD 只显示第 1 块 | 是 | 单项读数缺失 + 无法区分设备 | **确认**（`Distinct()` 按显示名去重） | **确认（根因未修）** | 上游 `HardwareScanner.cs:77-79`、fork `HardwareScanner.cs:85` `.Select(h => h.Name).Distinct()`；另 `DiskManager.cs:89-102` 按名称等值选盘 | 截图取证：同型号两块 SSD，下拉框只有一个条目 | P1：磁盘身份改用唯一实例 ID（与 GPU 的 `ListAllGpuOptions` 做法对齐） |
| #466 占用可移动磁盘导致无法安全移除 | 是 | 驱动/设备被占用 | **确认**（无移除前的设备释放/排除机制） | **确认（根因未修）** | 上游 `HardwareMonitor.cs:75` / fork `:153` `IsStorageEnabled = true`；两侧均无 USB/可移动盘排除策略 | 静态确认 + issue 评论互证；**未复现**（本机无可移动盘热拔插条件） | P0：首次 `Computer.Open()` 前应用设备排除策略 |
| #245 影响 USB 设备识别（Keil DAPLink） | 是 | 驱动/设备被占用 / 设备枚举受阻 | **部分确认（已发布缓解，非根治）**：上游仅靠 `IsControllerEnabled = ShouldEnableController()` 避免扫描扩展控制器 | **部分确认（同上游，无额外缓解）** | 上游 `HardwareMonitor.cs:80,366-374`；fork `HardwareMonitor.cs:158`（同逻辑） | 作者 2026-01-29 已发布缓解（不开风扇/水泵则不扫扩展设备）；用户 2026-01-30 反馈 1.2.9 仍存在 | P1：磁盘/USB 访问策略落地后重测 |

---

## 2. A 组：崩溃风险

### 2.1 #502 [Bug] 任务栏运行时 AccessViolation 崩溃 — NvmlDeviceGetPowerUsage

1. **当前状态**：OPEN（2026-06-23 建，5 天内 4 次崩溃；报告者环境 Windows 11 26200 / RTX 4060 Laptop / 驱动 32.0.16.1062 / .NET 8.0.28）。
2. **问题摘要**：任务栏运行数小时后静默退出，无错误弹窗，`LiteMonitor_Error.log` 未生成；事件查看器记录 `System.AccessViolationException (0xc0000005)`，堆栈为 `NvmlDeviceGetPowerUsage(NvmlDevice)` → `NvidiaGpu.Update()` → `HardwareMonitor.UpdateAll()` → `ThreadPoolWorkQueue.Dispatch()`。
3. **上游代码证据**：
   - 根因在**依赖库内**。LHM v0.9.6 `NvidiaGpu.cs`：
     - 字段 `private readonly NvidiaML.NvmlDevice? _nvmlDevice;` —— **构造期一次性取回，之后永不刷新**；
     - 构造：` _nvmlDevice = NvidiaML.NvmlDeviceGetHandleByPciBusId(...) ?? NvidiaML.NvmlDeviceGetHandleByIndex(_adapterIndex);`
     - `Update()`：`if (NvidiaML.IsAvailable && _nvmlDevice.HasValue) { int? result = NvidiaML.NvmlDeviceGetPowerUsage(_nvmlDevice.Value); ... }` —— **每轮更新都用同一个缓存句柄**。
     - 该调用点与 issue 堆栈逐帧吻合。
   - 应用层：上游 `src/System/HardwareMonitor.cs:75` 固定 `LibreHardwareMonitorLib 0.9.6`；`LiteMonitor.csproj` 未引入任何本地补丁版。
   - 异常边界：`HardwareMonitor.cs:203` `ShouldUpdateGpuHardware(hw)` 过滤后，`:205-213` 为 GPU 分支的 `try { hw.Update(); } catch (Exception ex) { ...; needsReload = true; }`；`:258` 是包裹整个采集轮次的 `catch { }`。
4. **fork 对照**：
   - fork `LiteMonitor.csproj` 同样固定 `LibreHardwareMonitorLib 0.9.6` → **缓存句柄这一根因同样存在**。
   - fork `src/System/HardwareMonitor.cs:485-493` 保留 GPU 分支独立 `try/catch`，但把失败交给 `RecordCollectionFailure(...)`（写 `src/System/HardwareServices/HardwareErrorLog.cs:33`），**不再只写 `Debug.WriteLine`** → 可观测性优于上游（上游 `:207` 仅 `Debug.WriteLine`，用户看不到任何日志）。
   - fork `:560-569` 把上游的 `catch { }` 换成 `catch (Exception ex) { RecordCollectionFailure(..., "CollectOnce"); }`，并注明旧实现导致 `FailedCollections` 成为恒零死指标。
   - fork **未**实现 `docs/superpowers/plans/2026-10-20-hardware-stability-hardening.md` 里的 NVML 补丁（Task 5）与 GPU/SMART 工作进程隔离（Task 8–11）：`git ls-tree master` 中不存在 `src/System/HardwareSupervision`、`LiteMonitor.HardwareContracts`、`LiteMonitor.HardwareProbe`、`DiskProbePolicy.cs`。计划文档头部标注的「已实施」与工作树实际内容**不一致**（见 §5 风险项）。
5. **复现条件与验证步骤**：
   - 复现条件：NVIDIA 笔记本独显在驱动更新后、GPU 电源状态切换（插拔电源 / 独显休眠唤醒）导致 NVML 句柄失效。
   - 本次验证（静态 + 实验，非实机复现）：
     1. 拉取 LHM v0.9.6 `NvidiaGpu.cs` 源码，确认 `_nvmlDevice` 为 `readonly` 字段且 `Update()` 复用该句柄 —— 与 issue 堆栈逐帧吻合。
     2. 本机实验（.NET 8 / PowerShell 7）：在 C# 中执行原生非法写并用 `catch (Exception)` 捕获，**成功捕获** `System.AccessViolationException`。即：托管 catch **可以**拦截这类原生 AV。
   - **未复现**：本机无 RTX 4060 Laptop 独显热切换环境，且无崩溃转储。
6. **结论**：**部分确认（根因机制确认；报告的「静默退出」与现行代码不完全自洽）**。
   - 根因（NVML 句柄缓存）确认存在于 LHM 0.9.6，上游与 fork 均未修。
   - 但报告的现象是「进程终止 + 无错误日志」，而现行上游 master（`3b91b5c`）与 v1.3.6（`:199-211`）**都已有** GPU 分支 `catch (Exception)`，按本机实验该 AV 应被拦截。上游 master 的 catch 只写 `Debug.WriteLine`，不写 `LiteMonitor_Error.log`——这能解释「无错误日志」，但解释不了「进程被终止」。
   - 剩余不自洽项需转储才能定论：三种可能——(a) 报告者二进制早于 `[Fix #290]` 的 GPU try/catch；(b) AV 被捕获后进程状态已损坏，运行时随后终止进程；(c) AV 发生在 `catch` 覆盖不到的路径（`Computer.Open()` / 预热 / `ReloadComputerSafe`）。
7. **风险**：**高**。原生 AV 属进程级故障，catch 只能降级不能根治；fork 同样使用 LHM 0.9.6，用户侧同样暴露。
8. **建议下一步**：
   - P0：移植 LHM 已合并的 PR #2539（合并提交 `3aa9c6c5bfc45fd15f4166bd35d73609b2bb126c`，修改 `NvidiaGpu.cs` + `NvidiaML.cs`）到固定源码基线：每轮重取句柄、PCI 身份校验索引回退、查找/使用/关闭共享生命周期锁、查询失败即返回不可用。
   - 同步把上游 `HardwareMonitor.cs:207` 的 `Debug.WriteLine` 换成用户可见日志（fork 已通过 `HardwareErrorLog` 做到）。
   - 向 #502 报告者索取 **WER LocalDumps 转储**与精确 LiteMonitor 版本号。

### 2.2 #579 报错 闪退，流量和 GPU 没有数据

1. **当前状态**：OPEN（2026-09-15 建，1 条同症状评论 2026-09-16）。
2. **问题摘要**：连续三天在 `LiteMonitor_Error.log` 中出现同型 NRE，堆栈为
   `DiskInfoToolkit.Storage.IdentifyStorageController()` → `Storage..ctor(String, StorageDevice)` → `StorageManager.HandleUnpartitionedDrive(...)` → `StorageManager.DevicesChangedListener()`，`[Source]: Background_Thread`。
3. **上游代码证据**：
   - 异常发生在 **DiskInfoToolkit 1.1.2 自建的静态监听线程** `DevicesChangedListener()` 内，不在应用的 `hw.Update()` 调用栈上 → 上游 `src/System/HardwareMonitor.cs:227-231` 的 `_diskManager.ProcessUpdate(...)` 外层 `catch { }`（`:258`）**结构上不可能覆盖这条线程**。
   - 依赖来源：上游 `LiteMonitor.csproj` → `LibreHardwareMonitorLib 0.9.6` → 传递依赖 `DiskInfoToolkit 1.1.2`（fork 侧 `NOTICE:105-109` 明确记载该版本随包分发）。
   - 上游 `src/System/Program.cs:80` 注册了 `AppDomain.CurrentDomain.UnhandledException`（`:123-126` → `LogCrash`），但**普通 `Thread` 上未处理的异常会终止整个 .NET 进程**，`UnhandledException` 只是最终通知，不是恢复机制。
4. **fork 对照**：
   - fork 依赖链完全相同（`LibreHardwareMonitorLib 0.9.6` + `DiskInfoToolkit 1.1.2`，见 fork `LiteMonitor.csproj` 与 `NOTICE:105`）→ **同样暴露**。
   - fork 的 `HardwareErrorLog` / `RecordCollectionFailure` 只覆盖**应用自己的采集线程**，对依赖库自建线程**无效**。
   - fork 的 `docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md:110-133` 已独立确认同一调用链与「库内补异常边界」的两条路线，但对应实现（Task 6 库内部分、Task 9 SMART 工作进程）**未落地**。
5. **复现条件与验证步骤**：
   - 复现条件：插入/拔出未分区设备（如 U 盘、读卡器、外置盘）时，DiskInfoToolkit 的设备变更监听线程对缺失控制器/型号元数据的设备执行 `IdentifyStorageController()`。
   - 验证：静态核对 issue 提供的真实堆栈与 DiskInfoToolkit 1.1.2 的 `StorageManager.DevicesChangedListener` → `HandleUnpartitionedDrive` → `new Storage(...)` → `IdentifyStorageController()` 调用链一致。
   - **未复现**：本机无该 NRE 触发设备/驱动组合，且无法注入库内部状态。
6. **结论**：**确认（根因在依赖库内部线程，应用层无法拦截）**。上游与 fork 状态相同。
7. **风险**：**高**。进程级终止；且症状含「流量和 GPU 没有数据」，与 `Computer.Open()` 阶段就失败相符（此时磁盘组初始化中断可能连带影响后续映射）。
8. **建议下一步**：
   - P1：联合升级到 `LibreHardwareMonitorLib 0.9.7-pre767` + `DiskInfoToolkit >= 2.2.1`，在新库中确认设备排除与识别线程的停止语义。
   - 若暂不升级：在固定 0.9.6 源码基线上给 DiskInfoToolkit 的**每个设备事件、每台设备构造、回调**补普通托管异常边界，失败设备进入有界退避，禁止「失败即全盘重扫」。
   - 把 SMART 采集移入独立工作进程（fork 计划 Task 9 未实现），使库内线程崩溃不带走主进程。

### 2.3 #290 可多显卡（核显+独显）笔记本显卡切换时闪退

1. **当前状态**：OPEN（2026-02-09 建）。作者 2026-02-12 回复「已修复该问题」，2026-05-14 补充「v1.3.5 已优化多显卡支持，可选首选显卡并提升切换/插拔稳定性」。issue 未关闭。
2. **问题摘要**：荣耀 MagicBook 16 Pro 2025 独显版；插电时双卡工作、传感器正常，切换核显或拔电后独显休眠，LiteMonitor 闪退。
3. **上游代码证据**：
   - 上游 v1.3.6 `src/System/HardwareMonitor.cs:199-211` 与 master `:203-213` 均已加入 `[Fix #290] 显卡防闪退保护`：GPU 分支独立 `try { hw.Update(); } catch (Exception ex) { Debug.WriteLine(...); needsReload = true; }`。
   - `:260-280`：`needsReload` 时 `Task.Run` 延迟 2 秒后 `ReloadComputerSafe()`（`:398-399` 处 `catch { }` 兜底），重建硬件树。
   - `ReloadComputerSafe()` 内 `_computer.Close(); _computer.Hardware.Clear(); _computer.Open();` 后重建映射 —— 这是清理「已释放句柄 / 已移除传感器」的主路径。
   - **未修的根因**：底层 LHM 0.9.6 `NvidiaGpu` 缓存 `_nvmlDevice`，GPU 被关闭/重枚举后仍用旧句柄（见 §2.1）；`ReloadComputerSafe` 只在「`hw.Update()` 抛出异常」时才被触发，即**依赖异常作为设备变化的唯一信号**。
4. **fork 对照**：
   - fork `src/System/HardwareMonitor.cs:477-495` 结构同构（GPU 分支独立 catch → `RecordCollectionFailure` → `needsReload = true`），`:571-590` 同样是延迟 2 秒重载。
   - fork 的 GPU 身份：`src/Core/GpuKey.cs` 用**序号**（`GPU.{index}.{metric}`）而非稳定 PCI ID；`src/System/HardwareServices/SensorMap.cs:126-146` `SelectMonitoredGpus` 按 `HardwareRules.GetHwPriority` 排序取前 N 张，`:153-160` `FindGpuIndex` 按**引用**定位序号。
     - **后果**：独显消失后显卡集合成员变化 → 序号重新分配 → 用户已保存的 `GPU.1.*` 监控项会指向另一块物理卡或消失。`HardwareScanner.cs:136-148` `FindGpuByConfig` 在首选卡消失时回退到「按名匹配唯一命中，否则返回 null」→ `SelectMonitoredGpus` 再回退到 `all[0]`（自动切到核显），**用户的显卡选择被静默改写**。
   - fork `src/System/HardwareServices/SensorMap.cs:80-88` `EnsureFresh` 只在**距上次建图 > 10 分钟**时重建，不是设备变化事件驱动 → 设备移除后最长 10 分钟内映射表仍持有已移除硬件的 `ISensor` 引用。
   - fork 额外做了上游没有的 `WarmUpGpuSensors()`（`HardwareMonitor.cs:1204`）与逐阶段 try/catch（`:1163-1214`）→ 崩溃可见性更好。
5. **复现条件与验证步骤**：
   - 复现条件：双显卡笔记本 + 插拔电源适配器 / 手动切换核显独显 / 独显驱动重置。
   - 验证：静态确认恢复链路（GPU 分支 catch → `needsReload` → `ReloadComputerSafe`）在两侧都存在；确认 fork 的 GPU Key 为序号式、映射刷新为 10 分钟兜底。
   - **未复现**：本机不具备「独显热切换笔记本」硬件条件。
6. **结论**：**部分确认**。上游与 fork 都有恢复链路（因此上游作者称「已修复」是可信的），但底层 NVML 句柄缓存根因未修、GPU 身份为序号式两项残留问题在两侧同样存在；fork 额外存在「首选卡消失后静默改选」与「映射最长 10 分钟不刷新」的语义缺口。
7. **风险**：**中高**。残留为「恢复不及时 + 序号漂移」，极端情况下仍是进程级故障。
8. **建议下一步**：
   - P1：GPU 身份从序号改为稳定 PCI/Bus ID（与 `ListAllGpuOptions` 已使用的 `Identifier` 口径统一）。
   - P1：把设备变化从「异常驱动」改为**显式事件**：注册 `Computer.HardwareChanged` 或在每次采集轮比对硬件树指纹，触发重建，而不是等 10 分钟兜底或等异常。
   - P1：首选显卡消失时**显式提示**「所选显卡已离线」，而不是静默回退到自动。

### 2.4 #582 识别磁盘 SMART 属性时发生空引用异常，导致 LiteMonitor 崩溃

1. **当前状态**：OPEN（2026-09-20 建），**正文只有 `LiteMonitor_Error.log` 附件，无描述、无评论**。
2. **问题摘要**：标题声称 SMART 识别链上空引用导致崩溃；附件未取得。
3. **上游代码证据**：
   - 标题指向 DiskInfoToolkit 的 SMART 识别链（`Storage..ctor` / `IdentifyDisk`），但**没有堆栈**就不能确证。
   - 上游应用侧相关边界：`HardwareMonitor.cs:227-231`（`_diskManager.ProcessUpdate`）+ `:258` 的 `catch { }`；`src/System/Program.cs:76-80,118-155` 的全局兜底（UI 线程 → `LogCrash` → 写日志 + 弹「LiteMonitor Crash」对话框；后台线程 → `CurrentDomain_UnhandledException`）。
   - fork 侧同样具备这两层：`src/System/Program.cs:89-93,131-142`；`src/System/HardwareServices/HardwareErrorLog.cs:33,40`。
4. **fork 对照**：同上游。fork 的 `docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md:135-139` 已明确记载：「#582 正文只有日志附件……附件未能成功取得，尚未核对其异常堆栈。**不能将它直接归并为已证实的 `DevicesChangedListener` 同一故障**。」本次核实沿用该结论。
5. **复现条件与验证步骤**：需要 #582 的 `LiteMonitor_Error.log` 附件原文（堆栈 + `[Source]` 行）才能定位。
6. **结论**：**信息不足**。上游与 fork 均无法在缺附件的情况下判定。
7. **风险**：**未知（潜在高）**。若与 #579 同源（库内线程 NRE），则进程级终止风险成立。
8. **建议下一步**：向 issue 报告者/作者索取附件内容；取得后按「`[Source]` 是 Background_Thread 还是 UI_Thread」分流到 §2.2（库内线程）与 §2.5（UI 线程）。

### 2.5 #574 突然报错 Parameter is not valid.

1. **当前状态**：OPEN（2026-09-10 建），无评论。截图确认是上游 `Program.cs` 的「LiteMonitor Crash」对话框（"程序遇到致命错误！错误日志已保存至：C:\TOOLS\LiteMonitor\LiteMonitor_Error.log / 原因：Parameter is not valid."）。
2. **问题摘要**：Windows 11 25H2 26200.9445 上突发 `ArgumentException: Parameter is not valid.`。日志堆栈：
   `System.Drawing.Font.GetHeight(Graphics)` → `System.Drawing.Font.GetHeight()` → `LiteMonitor.UIRenderer.Render(Graphics g, List\`1 groups, Theme t)` → `UIController.Render(Graphics g)` → `MainForm.OnPaint` → `Control.WmPaint` → `WndProc`。
3. **上游代码证据**：
   - `Parameter is not valid.` 是 GDI+ `Status.InvalidParameter` 的托管映射（`Gdip.CheckStatus`），典型来源是 `GdipGetFontHeight` 传入无效原生 DC / 无效字体对象。
   - **上游源码与报告堆栈不自洽**：
     - `git grep 'GetHeight' upstream/master -- src/` → **零匹配**；`git grep 'GetHeight' v1.3.6 -- src/` → **零匹配**。
     - 上游 `src/UI/UIRenderer.cs:63,86` 使用的是 `t.FontTitle.Height` / `t.FontGroup.Height`（`Font.Height` 属性），不是 `Font.GetHeight()` 方法。
     - `git log --all -S 'GetHeight' -- src/` 显示 `GetHeight` 只由 **fork 侧**提交 `7c6b903 feat(rendering): add GPU glass and cyberpunk themes with display-aware presentation` 引入，上游历史从未出现。
   - 本机实验（.NET 8 / PowerShell 7）：
     - `new Font("Microsoft YaHei UI", 12)` → `.Height` = **21**（缓存的整数值），`.GetHeight()` = **20.32031**（每次调用的 GDI+ 实时查询）→ 两者是不同量，`Height` 不做实时 GDI+ 调用。
     - `GetHeight(已 Dispose 的 Graphics)` → 抛 `ArgumentException`（"Value does not fall within the expected range. (Parameter 'graphics')"）。
   - 因此**该堆栈要求源码显式调用 `Font.GetHeight()`，而上游 v1.3.6 与 master 都不含该调用** → 报告者运行的二进制不是由上游 v1.3.6/master 源码构建的。
4. **fork 对照**：
   - fork **在绘制路径重新引入了 `Font.GetHeight(Graphics)`**：
     - `src/UI/HorizontalLayout.cs:47` `using var measurementGraphics = measurementControl?.CreateGraphics() ?? Graphics.FromHwnd(IntPtr.Zero);`
     - `src/UI/HorizontalLayout.cs:55-57`：`if (measurementControl is not null) { _rowH = (int)Math.Ceiling(Math.Max(t.FontItem.GetHeight(measurementGraphics), t.FontValue.GetHeight(measurementGraphics))); }`
     - 调用方 `src/UI/UIController.cs:1909` 传入了 `measurementControl: _form` → **该分支在横屏布局重建时会被走到**；`src/UI/UIController.cs:754` 与 `src/UI/TaskbarForm.cs:107` 未传 `measurementControl` → 走 `.Height` 缓存路径。
   - 触发条件与 #574 报告高度同构：绘制期间 `CreateGraphics()` 拿到的 DC 无效（窗口句柄正在重建 / DPI 切换 / 控件销毁）→ `Font.GetHeight(Graphics)` 抛 `ArgumentException`。
   - fork 的 `src/UI/UIRenderer.cs:32,47,146,185` 使用 `t.FontTitle.Height` / `t.FontGroup.Height`（缓存路径），与上游一致。
5. **复现条件与验证步骤**：
   - #574 复现条件：需要报告者二进制（版本未知）+ 触发绘制期的字体度量异常。
   - 本次验证：(a) 静态核对上游/ fork 的 `GetHeight` 使用点；(b) 本机实验确认 `Font.Height` 与 `Font.GetHeight()` 的语义差异与 `GetHeight(无效 Graphics)` 的异常类型；(c) 截图确认报告者运行的是上游 `LogCrash` 的对话框（`src/System/Program.cs:148-149` 文案逐字一致）。
   - **未复现**：无报告者二进制，无法复现其确切调用链。
6. **结论**：
   - **上游：信息不足**。报告堆栈与上游 v1.3.6/master 源码不自洽（上游从不调用 `Font.GetHeight()`），无法判定是哪个上游版本的哪条路径；需索取精确版本号与 `LiteMonitor_Error.log` 全文。
   - **fork：部分确认（存在同类结构风险）**。fork 在绘制路径存在 `Font.GetHeight(Graphics)` 调用点，异常类型（`ArgumentException`）与位置（绘制期）都与 #574 同构；属**潜在**风险，未在 fork 上实际触发。
7. **风险**：**中**。UI 线程异常 → fork 与上游都有全局兜底（`Application.ThreadException` + `CatchException`），表现为「弹窗 + 写日志」，通常**不终止进程**，但会打断当帧绘制。
8. **建议下一步**：
   - P1（fork，代码侧，成本低）：把 `src/UI/HorizontalLayout.cs:57` 的 `GetHeight(measurementGraphics)` 换成缓存的 `t.FontItem.Height` / `t.FontValue.Height`（与 `:54` 的既有写法一致），消除绘制期的 GDI+ 实时查询；同时给 `measurementGraphics` 的来源加有效性保护。
   - P1（上游侧）：向报告者索取精确 LiteMonitor 版本与 `LiteMonitor_Error.log` 全文。

---

## 3. B 组：设备数据错误（读数缺失 / 设备身份混淆）

### 3.1 #577 有两个机箱风扇，但只显示一个

1. **当前状态**：OPEN（2026-09-13 建），无评论、无截图。正文仅一句「希望可以自动检测电脑的风扇，显示其信息。」
2. **问题摘要**：用户有两个机箱风扇，LiteMonitor 只显示一个。
3. **上游代码证据**：
   - `src/System/HardwareServices/FanMapper.cs:138-140`：
     ```
     if (cpuFan != null) targetMap["CPU.Fan"] = cpuFan;
     if (caseFan != null) targetMap["CASE.Fan"] = caseFan;
     if (cpuPump != null) targetMap["CPU.Pump"] = cpuPump;
     ```
     → **机箱风扇只有一个单槽位 Key `CASE.Fan`**。第 5 步（`:119-135`）在所有候选里只挑一个 `caseFan`（优先名字含 Rear/Chassis/Sys/Case，否则取 RPM 最小的一个）。第二个机箱风扇**必然**被丢弃。
   - 对比同文件 `:16` `ScanAndMapFans(Computer, Settings, Dictionary<string, ISensor> targetMap)` 的目标字典本身按**指标槽位**（`CPU.Fan` / `CASE.Fan` / `CPU.Pump`）而非按**传感器实例**组织 → 单槽位是设计使然。
   - 设置页候选列表 `src/System/HardwareServices/HardwareScanner.cs:180-212` `ListAllFans` 用 `GenerateSmartName`（`:41-51`，`"{sensor.Name} [{hwName}]"`）+ `Distinct()`，**能列出两个风扇**；但列表 ≠ 可显示的监控项。
4. **fork 对照**：
   - fork `src/System/HardwareServices/FanMapper.cs:20,56,119-140` **与上游逐行同构**（`caseFan` 单变量 + `targetMap["CASE.Fan"] = caseFan`）→ **fork 同样只显示一个机箱风扇**。
   - fork `src/System/HardwareServices/HardwareScanner.cs:173,202` 的 `ListAllFans` 同样 `Distinct()`，候选列表行为一致。
   - fork 的 GPU 侧已有多卡序号化改造（`src/Core/GpuKey.cs`、`SensorMap.cs:244-257` 的 `GPU.{index}.Fan`），**风扇侧没有对应改造**。
5. **复现条件与验证步骤**：
   - 复现条件：主板/机箱暴露 ≥2 个 `SensorType.Fan` 且用户开启「机箱风扇」监控项。
   - 验证：静态确认单槽位写入（上游/ fork `FanMapper.cs:139`）；静态确认候选列表可枚举多个（`HardwareScanner.ListAllFans`）。
   - **未复现**：本机无多机箱风扇硬件，无法给出运行期截图。
6. **结论**：**确认（上游与 fork 根因一致，均未修）**。影响等级为「单项读数缺失」——第二个机箱风扇的转速不会出现在面板/任务栏/网页。
7. **风险**：**中**。数据丢失而非崩溃；用户可感。
8. **建议下一步**：
   - P2：把机箱风扇/水泵改为**多槽位 Key**（`CASE.Fan.{index}`），沿用 fork 已有的 `GpuKey.Build(index, metric)` 风格与旧 Key 兼容策略（首槽同时写旧 Key）。
   - `FanMapper` 需要保留全部候选而非只挑一个；`ScanAndMapFans` 的 200 RPM 底噪过滤（`:44`）会先剔除 0 RPM 风扇，需与 #514 一并处理。

### 3.2 #514 CPU & GPU fans show 0 RPM

1. **当前状态**：OPEN（2026-07-11 建），无评论。用户以管理员运行，CPU 与 GPU 风扇均显示 0 RPM，而本机控制中心显示正常。
2. **问题摘要**：风扇转速恒为 0，需区分「硬件不提供该传感器」「读取失败」「真实 0」。
3. **上游代码证据**：
   - **CPU 风扇**：`FanMapper.cs:41-47` 的候选收集条件是 `s.SensorType == SensorType.Fan && s.Value.HasValue` 且 `s.Value.Value > 200`（底噪过滤）。**0 RPM 的风扇在映射阶段就被排除**，`CPU.Fan` 键根本不存在 → 取值层拿不到值。
   - **GPU 风扇**：上游 `src/System/HardwareServices/SensorMap.cs:185` `var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);` —— 若 GPU 不暴露风扇传感器（多数笔记本 GPU 无独立风扇），则无 `GPU.Fan` 键。
   - **显示语义（关键）**：上游 `src/Core/MetricItem.cs:76-77` `public float? Value { get; set; } = null;` / `public float DisplayValue { get; set; } = 0f;`；`src/UI/UIController.cs:326,451` 只在 `val.HasValue` 时才 `item.DisplayValue = val.Value;` → **缺失的传感器保持默认 `0f`** → 面板显示「0 RPM」。这与 #514 的报告文字（"Both report 0 RPMs"）完全一致。
4. **fork 对照**：
   - fork `src/System/HardwareServices/FanMapper.cs` 与上游同构（底噪过滤、`CASE.Fan` 单槽位）→ **未修**。
   - fork 的显示语义：`src/Core/MetricItem.cs:77` 同样是 `public float DisplayValue { get; set; } = 0f;`；`src/UI/UIController.cs:1197,2071` 同样只在 `val.HasValue` 时赋值 → **缺失风扇仍显示 0**。
   - fork 确实建立了「缺失 ≠ 0」的语义，但**只对两个键生效**：`src/Core/MetricUtils.cs:106-108` `GetValueStr(string key, float? value, ...)` 的 `float v = value ?? 0.0f;` 之后，仅 CPU 电压键（`:114-121`）与提交内存键（`:128-130`）走 `MissingValueText`（`:356` = `"--"`）。**风扇键不在其中**。
   - 与 fork 计划 `docs/superpowers/plans/2026-10-20-hardware-stability-hardening.md:31` 的目标（「传感器不可用时显示不可用，不删除配置或固定显示 0」）对照：风扇项**尚未落实**。
5. **复现条件与验证步骤**：
   - 复现条件：设备不暴露风扇传感器，或风扇处于 0 RPM（停转/待机），或映射被底噪过滤排除。
   - 验证：静态确认（上游/ fork 的 `FanMapper` 过滤条件 + `MetricItem`/`UIController` 的 0 默认值 + `MetricUtils` 的 `?? 0.0f` 与 `MissingValueText` 适用范围）。
   - **未复现**：本机无法提供该机型（CPU 与 GPU 风扇均 0 RPM）的对照环境；也无法区分用户报告的「0 RPM」是「真实 0」还是「缺失被渲染成 0」——**这恰恰是 #514 真正的诉求**。
6. **结论**：**确认（上游与 fork 根因一致：缺失传感器被渲染为 0，无法区分三种状态）**。影响等级「单项读数缺失」+「显示语义误导」。
7. **风险**：**中**。不崩溃，但会让用户误判硬件故障，也让「真实 0」与「无传感器」无法分辨。
8. **建议下一步**：
   - P2：为风扇键引入与 CPU 电压同款的 `MissingValueText` 分支（`MetricUtils.GetValueStr`），并把「无传感器」「读取失败」「真实 0 RPM」三态分别表达。
   - P2：`FanMapper` 的 200 RPM 底噪过滤只应用于**挑选**首选风扇，不应用于**决定该键是否存在**——只要硬件暴露了 `SensorType.Fan`，就应建键并允许其值为 0。

### 3.3 #538 【Bug 1.3.6】2 块固态硬盘型号相同时只显示了第 1 块硬盘

1. **当前状态**：OPEN（2026-08-01 建），无评论。截图已取得。
2. **问题摘要**：两块 Samsung SSD 970 EVO Plus 500GB（DiskGenius 显示为 HD0 / HD1），LiteMonitor「首选磁盘」下拉框里只有一个 `Samsung SSD 970 EVO Plus 500GB` 条目（另有「自动模式」与 `WDC WD30EZRZ-22Z5HB0`）。
3. **上游代码证据**：
   - `src/System/HardwareServices/HardwareScanner.cs:72-83` `ListAllDisks`：
     ```
     var list = computer.Hardware
         .Where(h => h.HardwareType == HardwareType.Storage)
         .Select(h => h.Name).Distinct().ToList();
     ```
     → **按显示名去重**，同型号两块盘塌缩成一条。截图与此逐字对应。
   - 数据消费点：`src/UI/Settings/SystemHardwarPage.cs:55` `Task.Run(() => HardwareScanner.ListAllDisks(...))` → `:115` `FillSync(_cbDisk, taskDisks.Result, Config.PreferredDisk)`。
   - **第二重根因**：`src/System/HardwareServices/DiskManager.cs:34-35,46` 按**名称等值**选首选盘（`hw.Name.Equals(cfg.PreferredDisk, ...)`）→ 即使下拉框能列出两条同名盘，按名匹配仍会命中**第一块**，无法区分。
   - **对照证据（同一文件内的正确做法）**：`HardwareScanner.cs:96-116` 的 `ListAllGpuOptions` **已经**为显卡解决了同类问题——`Value` 用硬件 `Identifier`，标签在同名时加 `#index` 后缀。**磁盘侧未做同样处理**。
4. **fork 对照**：
   - fork `src/System/HardwareServices/HardwareScanner.cs:78-85` `ListAllDisks` **仍是** `.Select(h => h.Name).Distinct().ToList()` → **fork 同样只显示一块**。
   - fork `src/System/HardwareServices/DiskManager.cs:89-90,102` 同样按名称等值选盘 → 同第二重根因。
   - fork 的显卡侧同样有 `ListAllGpuOptions` 的 Identifier 方案（`HardwareScanner.cs:100,109,159-160`）→ **同一仓库内存在可直接复用的正确范式**。
   - 消费点：fork `src/UI/Settings/SystemHardwarPage.cs:242`（异步取列表）→ `:377` `FillSync(_cbDisk, taskDisks.Result, Config.PreferredDisk)`。
5. **复现条件与验证步骤**：
   - 复现条件：两块及以上 `HardwareType.Storage` 硬件的 `Name` 相同（同一型号两块 SSD 是最常见形态）。
   - 验证：截图取证（#538 正文图）已确认「两块同型号盘 + 下拉框只有一个条目」；静态确认 `Distinct()` 去重点与按名选盘点。
   - **未复现**：本机无两块同型号 SSD，未做运行期复现。
6. **结论**：**确认（上游与 fork 根因一致，均未修）**。影响等级「单项读数缺失 + 设备无法区分」——不仅只显示第 1 块，用户还**无法选中第 2 块**，速度统计目标只能落在第一块上。
7. **风险**：**中高**。用户可见的数据错误与配置能力缺失；且若「首选磁盘」被指到第一块，会持续轮询第一块设备。
8. **建议下一步**：
   - P1：新增磁盘稳定身份目录（设备 ID / 序列号 / `Identifier`），`ListAllDisks` 返回 `Id + Label`（同名加 `#index` 或序列号后缀），并把 `PreferredDisk` 的匹配口径从**名称**改为**ID**（保留旧名称值的一次性迁移，与 fork 计划 §5.1「旧配置迁移」一致）。
   - 该改动同时是 §4 中 #466/#601 的前置（设备排除规则也需要唯一身份）。

---

## 4. C 组：资源与设备占用

> 本组三个 issue 的共同机制：`Computer.Open()` 在 `IsStorageEnabled = true` 下会创建 StorageGroup，触发 DiskInfoToolkit 1.1.2 的**物理盘全量识别**（含 `PhysicalDrive0..63` 兜底尝试）与 SMART 读取；识别发生在**应用层任何过滤之前**，因此事后的「按名称选盘」无法撤销设备访问。

### 4.1 #601 和 HWiNFO 监控软件冲突了

1. **当前状态**：OPEN（2026-10-07 建），无评论。截图已取得。
2. **问题摘要**：截图是 HWiNFO 的 **ISA Lock** 告警框：「另一个应用程序通过长时间持有共享同步对象来阻止 HWiNFO。这会导致 HWiNFO 启动非常缓慢……有问题的应用程序是：`A:\F\Program Files\LiteMonitor_v1.3.6-win-x64\LiteMonitor.exe` `[@ .NET TP Worker]`」。
3. **上游代码证据**：
   - 「`[@ .NET TP Worker]`」明确指出**持有者是 .NET 线程池工作线程** → 对应 LiteMonitor 的后台采集线程，即 `HardwareMonitor.UpdateAll()` 内的磁盘采集路径（`HardwareMonitor.cs:227-231` `_diskManager.ProcessUpdate(...)`）。
   - 依赖：上游 `LiteMonitor.csproj` → `LibreHardwareMonitorLib 0.9.6` → `DiskInfoToolkit 1.1.2`；SMART 读取在 Windows 上通过直接打开物理盘句柄下发 IOCTL 完成，句柄在存储对象生命周期内长期持有 → 与 HWiNFO 的共享同步对象/设备访问互斥。
   - 上游 `src/System/HardwareMonitor.cs:75` `IsStorageEnabled = true` **无条件开启**；`src/System/HardwareServices/DiskManager.cs` 的首选盘逻辑在识别之后才生效，**无法撤销识别阶段的设备访问**。
4. **fork 对照**：
   - fork 依赖链、fork `src/System/HardwareMonitor.cs:153` `IsStorageEnabled = true`、fork `DiskManager.cs` 的事后按名选盘 —— **与上游完全相同**，同样长期持有 SMART/物理盘句柄。
   - fork **未**实现 fork 计划 `2026-10-20-hardware-stability-hardening.md` §5.1 的 `DiskProbeMode` / `SmartDiskDeviceIds` / USB 排除规则：`git grep -n 'DiskProbeMode|DiskProbePolicy|ExcludeUsb|IsRemovable|SmartDiskDeviceIds' master -- src/` → **零匹配**；`git ls-tree master` 无 `src/System/HardwareSupervision`、无 `LiteMonitor.HardwareContracts`、无 `LiteMonitor.HardwareProbe`。
   - fork 的 `src/System/HardwareMonitor.cs:158` `IsControllerEnabled = ShouldEnableController()` 只控制**控制器/扩展设备**扫描，与物理盘/SMART 无关。
5. **复现条件与验证步骤**：
   - 复现条件：LiteMonitor 开启 SMART 采集（默认）且持续运行，同时启动 HWiNFO；HWiNFO 报 ISA Lock。
   - 验证：截图取证确认冲突对象与持锁线程类型（`[@ .NET TP Worker]`）；静态确认 fork/上游均无设备访问策略。
   - **未复现**：本机未安装 HWiNFO，且未做 SMART 采集压测。
6. **结论**：**确认（上游与 fork 根因一致，均未修）**。影响等级「驱动/设备被占用」——不仅自身受影响，还**拖慢并可能阻塞其他监控软件**（截图原文："会导致 HWiNFO 启动非常缓慢，并且需要过多的时间访问硬件"）。
7. **风险**：**高（对第三方软件的影响面）**。属于「独占访问」类问题，会外溢到其他工具。
8. **建议下一步**：
   - P0：实现磁盘采集模式（fork 计划 Task 1/2/4 未落地）：默认 `CountersOnly`——**在 `Computer.Open()` 之前**把 `IsStorageEnabled` 置 false，速度走 Windows 性能计数器；需要 SMART 时由用户显式开启并给出允许列表。
   - P0：配合 #466/#245 的设备排除规则（同一次改动落地）。
   - 补充验证口径：用 Process Monitor 观测冷启动与打开设置页时是否出现目标盘的探测。

### 4.2 #466 [BUG] 占用可移动磁盘，导致无法正常移除可移动磁盘

1. **当前状态**：OPEN（2026-05-29 建）；评论 2026-07-20 同症状（"插入U盘时，资源管理器页面也会黑屏很久"）。
2. **问题摘要**：拔出 U 盘始终失败，火绒显示被 LiteMonitor 占用，且火绒无法解除占用，必须退出 LiteMonitor 才能安全移除。截图已取得（火绒「U 盘保护」弹窗：「正在被程序占用 / 占用程序：`LiteMonitor.exe` / 文件或者文件夹：`L:` / 解除占用并安全弹出」）→ 直接指名 LiteMonitor 持有可移动盘 `L:` 的句柄。
3. **上游代码证据**：
   - 与 §4.1 同源：`HardwareMonitor.cs:75` `IsStorageEnabled = true` → `Computer.Open()` → StorageGroup → DiskInfoToolkit 1.1.2 `ReloadStorages()` 全量枚举（含 `PhysicalDrive0..63` 兜底）→ 对**可移动盘也建立对象并持有句柄**。
   - 识别发生在应用层过滤之前，`DiskManager` 的按名选盘（`DiskManager.cs:34-35,46`）**无法撤销**已发生的访问。
   - 补充：LHM v0.9.6 的 `StorageGroup.Close()` 为空实现、DiskInfoToolkit 1.1.2 又有静态后台线程 → 事后再关 `IsStorageEnabled` 也不能保证停止占用；上游 `HardwareMonitor.cs:517,545`（`ReloadComputerSafe` / `ReleaseComputerForDriverInstall` 末尾的 `catch { }`）在关闭路径上也没有针对可移动盘的排除。
4. **fork 对照**：
   - fork `src/System/HardwareMonitor.cs:153` `IsStorageEnabled = true` 无条件；`src/System/HardwareServices/DiskManager.cs:89-102` 事后按名选盘；**无 USB/可移动盘排除规则**（grep 零匹配）→ **fork 同样存在**。
   - fork 计划的应对措施（`DiskProbeMode` + 排除规则 + `SelectedSmart` 允许列表）**未落地**。
5. **复现条件与验证步骤**：
   - 复现条件：LiteMonitor 运行中插拔 U 盘/移动硬盘；识别线程或缓存对象持有物理盘句柄。
   - 验证：issue 正文 + 评论双份报告互证；静态确认无排除策略；fork 侧 `docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md:65-84` 已独立复核同一调用链（`Computer.Open()` → `new StorageGroup(...)` → `StorageManager.ReloadStorages()` → 枚举并构造 Storage）。
   - **未复现**：本机无可移动盘热拔插条件；未做 Process Monitor 观测。
6. **结论**：**确认（上游与 fork 根因一致，均未修）**。影响等级「驱动/设备被占用」——且属于**用户无法自救**型（必须退出程序）。
7. **风险**：**高**。直接阻断用户移除外置存储介质，Windows 上「设备正在使用中」对话框会造成数据安全顾虑。
8. **建议下一步**：
   - P0：同 §4.1 的磁盘采集模式改造；**默认模式必须保证不打开可移动/USB 盘**（`DiskInfoToolkit 2.2.1` 的 `Storage.ExcludeUsbDevices` 可用，但必须**在 LHM 第一次创建 StorageGroup 之前**设置——fork 研究文档 §4.3 已核对过该开关，且强调它不能替代白名单）。
   - P0：补充「排除必须在库打开设备/读 SMART/识别型号/容量扫描之前生效」的验证口径（假设备访问记录 + Process Monitor 观测）。
   - 注意「设备身份无法确认时拒绝识别」的边界：不能靠 `DriveInfo.IsRemovable` 或名称猜测连接类型。

### 4.3 #245 影响 USB 设备的识别（keil daplink）

1. **当前状态**：OPEN（2026-01-29 建），5 条评论。作者 2026-01-29 给出缓解（延迟自启动 10 秒），同日 17:18 发布优化（「如果没有开启 cpu 风扇和水冷，不会再扫描扩展设备」）；报告者 2026-01-30 反馈 **1.2.9 现象仍存在**，且重新打开 LiteMonitor 后有一两分钟设备不可用。
2. **问题摘要**：LiteMonitor 开机自启与 Keil 的 DAPLink 调试器 USB 枚举冲突；LiteMonitor 保持运行时插拔该 USB 设备，Keil 识别异常；退出 LiteMonitor 即恢复。DAPLink 支持虚拟串口 + 虚拟 USB。
3. **上游代码证据**：
   - 作者给出的缓解已落地为代码：`src/System/HardwareMonitor.cs:80` `IsControllerEnabled = ShouldEnableController()`，`:366-374` 的实现为「仅当开启 `CPU.Fan` / `CPU.Pump` / `CASE.Fan` 时才扫描扩展设备（Controller/SuperIO/AIO）」，注释明写「默认关闭以避免 USB 冲突」。
   - 另一条相关路径：`HardwareMonitor.cs:398-399` 的 `WarmUpMotherboardSensors()` 对 Motherboard/SuperIO 执行 `hw.Update()`（`catch { }` 兜底）——即使 `IsControllerEnabled` 为 false，SuperIO 仍可能随主板硬件树被枚举。
   - 磁盘侧仍无排除：`:75` `IsStorageEnabled = true` → 可移动/USB 存储设备的识别与 SMART 访问照旧发生（与 §4.2 同源）。
4. **fork 对照**：
   - fork `src/System/HardwareMonitor.cs:156-158` 保留了同一套 `ShouldEnableController()` 逻辑（`:258` 起 `RefreshHardwareConfig` 的热切换），**缓解手段与上游等价**。
   - fork **未**新增任何 USB 层面的隔离：grep `usb|removable` 于 fork `src/` 只命中注释与无关的 `RenderingRoute*` 文件，无任何设备排除实现。
   - fork 同样未落地 `DiskProbeMode` / `ExcludeUsbDevices`。
5. **复现条件与验证步骤**：
   - 复现条件：LiteMonitor 开机自启或运行中 + DAPLink（虚拟串口 + 虚拟 USB）设备枚举。
   - 验证：issue 评论链取证（作者已确认缓解措施在 1.2.9 仍不足）。
   - **未复现**：本机无 DAPLink / ARM 调试器，无法验证。
6. **结论**：**部分确认（上游与 fork 均为「已发布缓解、未根治」）**。缓解（不开风扇/水泵则不扫扩展设备）已存在于两侧；但报告者明确反馈 1.2.9 下**仍有冲突**，且报告者观察到「退出后重新打开 LiteMonitor，仍有一两分钟不可用」——说明**磁盘/USB 存储识别路径**才是主要冲突源，而这一侧两侧都未处理。影响等级「驱动/设备被占用 / 设备枚举受阻」。
7. **风险**：**中高**。影响其他软件的 USB 设备首次枚举，开发工具（调试器）不可用对用户是硬阻塞。
8. **建议下一步**：
   - P1：§4.1/§4.2 的磁盘访问策略落地后，**重新验证** DAPLink 场景（这是该策略是否有效的天然验收用例）。
   - P1：把 `IsControllerEnabled` 的缓解扩展为「按需扫描」而非「按监控项扫描」——例如提供「扩展设备扫描」总开关，默认关闭。
   - P2：收集 DAPLink 的设备 ID 与 USB 枚举日志，确认冲突是来自存储识别、控制器识别，还是 LHM 的 `D3DDisplayDevice` 设备枚举。

---

## 5. 共性根因

### 根因 A：设备身份用「显示名」而非「唯一实例 ID」作为键（#538 主因、#577 同族）

| 维度 | 证据 |
|---|---|
| 表现 | 同型号两块 SSD 塌缩为一条（`HardwareScanner.ListAllDisks` 的 `.Distinct()`）；机箱风扇只有单槽位 `CASE.Fan` |
| 上游 | `src/System/HardwareServices/HardwareScanner.cs:77-79`、`:63`（网卡同样按名去重）、`src/System/HardwareServices/FanMapper.cs:138-140` |
| fork | `src/System/HardwareServices/HardwareScanner.cs:85`、`:202`、`:239`、`src/System/HardwareServices/FanMapper.cs:138-140`（与上游同构） |
| 仓库内已有的正确范式 | **同一文件**的 `ListAllGpuOptions`（上游 `:96-116`、fork `:100-160`）用 `Identifier` 作 `Value`、同名加 `#index` 后缀——**磁盘/风扇侧未对齐** |
| 附带影响 | `DiskManager` 按名称等值选盘（上游 `:34-35,46`、fork `:89-90,102`）→ 同名设备无法区分、无法真正选到第 2 块 |

### 根因 B：原生句柄生命周期由依赖库单方面决定，应用层没有接管（#502、#290、#466、#601、#579）

| 维度 | 证据 |
|---|---|
| 机制 | LHM v0.9.6 `NvidiaGpu` 把 NVML 设备句柄存为 **`readonly` 字段**，构造期取一次，`Update()` 每轮复用；DiskInfoToolkit 1.1.2 自建静态监听线程并长期持有物理盘句柄 |
| 上游 | `LiteMonitor.csproj` 固定 `LibreHardwareMonitorLib 0.9.6`；`HardwareMonitor.cs:75` `IsStorageEnabled = true`；GPU 分支 catch 只写 `Debug.WriteLine`（`:207`） |
| fork | `LiteMonitor.csproj` 同样 0.9.6；`NOTICE:105-109` 明确记载随包分发 `DiskInfoToolkit 1.1.2`；`HardwareMonitor.cs:485-493` GPU 分支 catch 走 `HardwareErrorLog`（可观测性更好）；但**根因未动** |
| 已有但未落地的修复 | LHM PR #2539（合并提交 `3aa9c6c5bfc45fd15f4166bd35d73609b2bb126c`）：每轮重取句柄、PCI 身份校验、查找/使用/关闭共享生命周期锁 |
| 计划与实现的偏差 | fork `docs/superpowers/plans/2026-10-20-hardware-stability-hardening.md` 与 `2026-10-20-hardware-stability-parallel-dispatch.md` 头部标注「**已实施**」，但 `git ls-tree master` 中**不存在** `src/System/HardwareSupervision`、`LiteMonitor.HardwareContracts`、`LiteMonitor.HardwareProbe`、`src/System/DiskProbePolicy.cs`；`git grep 'DiskProbeMode|DiskProbePolicy|ExcludeUsb|IsRemovable|SmartDiskDeviceIds' master -- src/` 零匹配 |

> ⚠️ 这条「计划标已实施、代码未落地」的偏差本身是**需要主控知晓的独立风险**：后续任何以「硬件稳定性计划已实施」为前提的结论都会失真。

### 根因 C：异常边界只在应用自己的线程上，跨不过依赖库的自建线程（#579、#582）

| 维度 | 证据 |
|---|---|
| 机制 | DiskInfoToolkit 的 `DevicesChangedListener()` 是库内静态线程，其异常**不经过**应用的 `hw.Update()` 调用栈；`AppDomain.UnhandledException` 只是终止通知，无法恢复 |
| 上游 | `HardwareMonitor.cs:227-231` + `:258` 的 `catch { }`；`Program.cs:80,123-126` |
| fork | 同上；fork 额外有 `HardwareErrorLog`，但**只覆盖应用线程** |
| 含义 | 应用层加 catch 对 #579 无效；必须在库内补边界，或把 SMART 采集移入独立进程 |

### 根因 D：缺失与「真实 0」在显示层不可区分（#514，兼及 #577）

| 维度 | 证据 |
|---|---|
| 机制 | `MetricItem.DisplayValue` 默认 `0f`，仅在 `val.HasValue` 时赋值；`MetricUtils.GetValueStr` 开头 `float v = value ?? 0.0f;` |
| 上游 | `src/Core/MetricItem.cs:76-77`、`src/UI/UIController.cs:326,451` |
| fork | `src/Core/MetricItem.cs:77`、`src/UI/UIController.cs:1197,2071`、`src/Core/MetricUtils.cs:106-108`；fork 的 `MissingValueText = "--"`（`:356`）**只对 CPU 电压键与提交内存键生效**，风扇键不在其中 |
| 与计划目标的差距 | fork 计划 §5.2 明确要求「传感器不可用时显示不可用，不删除配置或固定显示 0」——风扇/温度类尚未落实 |

---

## 6. 未能验证项与需要的最小诊断信息

| 项 | 缺少什么 | 为什么无法在本机补齐 |
|---|---|---|
| #502 的「静默退出」机制 | 崩溃转储（WER LocalDumps）、精确 LiteMonitor 版本、事件查看器完整条目（含 .NET Runtime 的进程退出码） | 本机无独显热切换笔记本；且现有 catch 理论上能拦截 AV，需转储判定是「catch 未覆盖」还是「catch 后进程被运行时终止」 |
| #574 的确切调用链 | 报告者二进制版本号 + `LiteMonitor_Error.log` 全文 | 上游 v1.3.6 与 master 的 `src/` 全树均无 `Font.GetHeight()`，报告堆栈与上游源码不自洽，无法定位 |
| #582 的根因 | `LiteMonitor_Error.log` 附件原文（堆栈 + `[Source]` 行） | issue 正文只有附件，无正文描述；附件未取得 |
| #579 的触发设备 | 触发 NRE 的具体设备类型（U 盘 / 读卡器 / 外置盘型号）、DiskInfoToolkit 版本、驱动版本 | 库内线程状态无法从外部注入 |
| #290 的实机复现 | 双显卡笔记本 + 插拔电源时的崩溃转储与切换前后 `HardwareMonitor` 日志 | 本机无独显热切换硬件 |
| #601 的冲突复现 | HWiNFO 版本、同时运行的监控软件清单、Process Monitor 的文件/设备访问轨迹 | 本机未安装 HWiNFO |
| #466 的占用复现 | 可移动盘型号、热拔插时序、Process Monitor 句柄轨迹 | 本机无可移动盘热拔插条件 |
| #245 的 USB 冲突复现 | DAPLink 设备 ID、USB 枚举日志、冲突发生时的 LiteMonitor 采集栈 | 本机无 DAPLink / ARM 调试器 |
| #538 / #577 | 两块同型号 SSD 的双卡实机、≥2 机箱风扇主板 | 本机硬件不具备 |
| #514 | 「0 RPM」是真实 0 还是缺失被渲染成 0 —— 需要能提供对照风扇转速的机型 | 本机无法区分这两种情况 |
| 通用 | 两侧均未构建/运行 LiteMonitor（本次只做静态取证 + 2 组本机实验） | 任务约束禁止长时间构建/全量测试 |

---

## 7. 建议优先级（按对 fork 的实际影响排序）

### P0 — 崩溃与外溢影响最大，且两侧同源

1. **磁盘访问策略落地**（覆盖 #466、#601、#245，并为 #579/#582 的库内修复铺路）：
   - 在 `Computer.Open()` **之前**按模式设置 `IsStorageEnabled`（`fork src/System/HardwareMonitor.cs:153` 当前无条件为 true）；
   - 默认 `CountersOnly`：速度走 Windows 性能计数器，**不创建 StorageGroup、不触发 DiskInfoToolkit 初次识别**；
   - 需要 SMART 时用户显式开启 + 允许列表 + USB 排除规则（`DiskInfoToolkit 2.2.1` 的 `ExcludeUsbDevices` 必须在 LHM 首次创建 StorageGroup 之前设置）；
   - 验证口径：Process Monitor 观测冷启动/打开设置页**无**目标盘探测；假设备访问记录证明被排除设备在构造前已被拒绝。
2. **NVML 句柄生命周期修复**（覆盖 #502 根因、#290 根因）：
   - 在固定 LHM 0.9.6 源码基线上移植 PR #2539（`NvidiaGpu.cs` + `NvidiaML.cs`），或验证 `0.9.7-pre767` 构建确实含该补丁；
   - 回归：双 NVIDIA / 同型号双卡 / 卡序变化 / 不支持功率查询 / Intel 核显预热 / D3D 显存映射。
3. **补齐 fork 的磁盘身份目录**（磁盘稳定 ID + 计数器实例映射），它是 #538 的直接修复，也是 #466/#601 排除规则的前置。

### P1 — 数据正确性与崩溃可观测性

4. **库内异常边界**（#579、#582）：升级到 `DiskInfoToolkit >= 2.2.1` + LHM 配套版本，或在固定源码上给每个设备事件/设备构造/回调补异常边界，失败设备有界退避、禁止「失败即全盘重扫」。
5. **GPU 身份与设备变化**（#290）：GPU Key 从序号改为稳定 PCI/Bus ID；设备变化从「异常驱动」改为显式事件；首选卡消失时显式提示而非静默改选。
6. **绘制路径去 GDI+ 实时查询**（#574 的 fork 侧风险）：`src/UI/HorizontalLayout.cs:57` 的 `GetHeight(measurementGraphics)` → 缓存 `Font.Height`。
7. **异常可观测性对齐**：上游 GPU catch 只写 `Debug.WriteLine`（`HardwareMonitor.cs:207`），用户看不到任何日志；fork 已用 `HardwareErrorLog`，建议把这条经验回写成上游 issue 反馈。

### P2 — 显示语义与多实例支持

8. **风扇多槽位化**（#577）：`CASE.Fan` → `CASE.Fan.{index}`，沿用 `GpuKey.Build` 风格与旧 Key 兼容。
9. **缺失 ≠ 0**（#514）：把 `MetricUtils.MissingValueText` 的适用范围从「CPU 电压 + 提交内存」扩展到风扇/温度类，并区分「无传感器 / 读取失败 / 真实 0」三态。
10. **同型号设备在设置页可区分**（#538）：磁盘/风扇/网卡的下拉项改为「Label + 唯一 ID」，与 `ListAllGpuOptions` 的既有范式对齐。

---

## 8. 取证命令与产出清单

**只读 git 命令**（全程未执行 checkout / switch / stash / reset / clean / restore / commit / branch）：

```
git -C G:/TOOL/LiteMonitor-fork log --oneline -3 master / upstream/master
git -C G:/TOOL/LiteMonitor-fork show upstream/master:<path>
git -C G:/TOOL/LiteMonitor-fork show master:<path>
git -C G:/TOOL/LiteMonitor-fork show v1.3.6:<path>
git -C G:/TOOL/LiteMonitor-fork grep -n <pattern> <ref> -- src/
git -C G:/TOOL/LiteMonitor-fork ls-tree -r --name-only <ref> -- src/
git -C G:/TOOL/LiteMonitor-fork log --oneline -S <string> <ref> -- <path>
```

**issue 取证**：`gh issue view <n> --repo Diorser/LiteMonitor --json ...`（11 个 issue 的正文与评论）；下载 #601 / #538 / #574 / #466 四张附件截图到 `.tmp/verification/` 后读取（结束前已清理）。

**本机运行时实验（2 组，均在 PowerShell 7 / .NET 8 下）**：
1. `AccessViolationException` 可捕获性：在 C# 中执行原生非法写并用 `catch (Exception)` 捕获 → **成功捕获** `System.AccessViolationException`。
2. `Font.Height` vs `Font.GetHeight()`：`Height` = 21（缓存整数）、`GetHeight()` = 20.32031（实时 GDI+ 查询）；`GetHeight(已 Dispose 的 Graphics)` 抛 `ArgumentException`。

**外部源码核对**：
- LHM v0.9.6 `NvidiaGpu.cs`（确认 `_nvmlDevice` 为 `readonly` 缓存字段、`Update()` 复用该句柄）
- .NET 运行时行为由本机实验确定（未依赖二手结论）

**产出**：本文件 `docs/verification/2026-10-25-TASK-3-crash-hardware.md`（唯一新增文件；未修改任何 `src/**`、未提交、未改设置与用户数据）。
