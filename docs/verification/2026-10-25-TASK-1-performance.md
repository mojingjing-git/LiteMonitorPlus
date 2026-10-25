# TASK-1 性能与资源占用 —— 上游 issue 复核报告

## 0. 基本信息

| 项 | 值 |
|---|---|
| 任务单 | TASK-1（性能与资源占用） |
| 检查日期 | 2026-10-25 |
| 上游仓库 | `Diorser/LiteMonitor`，ref `upstream/master` = **3b91b5c**（"fix: 完成 1.3.7 发版审查并修复监控与网络边界问题"，2026-10-21；`LiteMonitor.csproj` 中 `<Version>1.3.7</Version>`） |
| fork | 工作树分支 `master` = **5e756e7**（"feat(theme-market): M2+M3 主题索引解析/安装事务/状态与 D7 卸载守卫"） |
| 取证方式 | 只读：`git show` / `git grep` / `git log` / `git diff` / `git ls-tree` + `gh issue view`。**未修改任何产品源码、未构建、未做长时间运行实测**。 |
| issue 状态复核 | 本人独立执行 `gh issue view <n> --json state,body,comments`，六个 issue **全部仍为 OPEN**；评论数 416:1 / 437:1 / 88:3 / 78:1 / 35:3 / 96:2，与主控数据一致。 |

**关于 issue 状态的独立结论**：六个 issue 均无作者"已修复/已解决"的正式标记（无 label、无关闭），但 **#88 的作者在评论中明确声明"新版已修复"**（`https://github.com/Diorser/LiteMonitor/issues/88#issuecomment-3693192649`，2025-12-26 18:09）。上游 commit 历史中**没有任何一个 commit 引用过这六个 issue 编号**（`git log upstream/master --grep="#416|#437|#88|#78|#35|#96"` 全部为空），上游 PR 列表中也无对应 PR。因此"代码里到底改没改"必须逐条按代码取证判定，不能依赖 issue 状态。

---

## 1. 汇总表

| Issue | 当前是否 open | 上游结论（1.3.7 / 3b91b5c） | fork 结论（5e756e7） | 证据位置 | 复现/验证结果 | 下一步 |
|---|---|---|---|---|---|---|
| #416 软件导致 System 进程 CPU 上升 | ✅ OPEN（1 条评论） | **部分确认（机制成立，归因无法在代码层定案）**：确实存在内核态硬件读取路径（PawnIO 驱动 + LHM + 默认开启的 SMB 计数器），但没有 LiteMonitor 自身产生 10% 内核 CPU 的代码证据；作者与评论者均指向 NVIDIA 驱动 | **部分确认 + 风险更高**：内核态来源在上游基础上**新增** ETW `Microsoft-Windows-Kernel-Network`（默认关闭）与 PresentMon ETW（FPS 开启时） | 上游 `src/System/HardwareServices/DriverInstaller.cs:18,30-34`；`src/System/HardwareMonitor.cs:76,191`；`src/System/HardwareServices/PerformanceCounterManager.cs:97-101,278-296`；`src/Core/Settings.cs:119`；fork `src/System/NetworkTraffic/EtwNetworkTrafficEventSource.cs:74,82,247`、`src/System/HardwareMonitor.cs:540,791,826` | **未复现**（无长时间运行 + 无内核态归因工具） | 用 WPA/xperf 抓 `CPU Usage (System)`/DPC-ISR 做归因；同时提供"关闭 FPS / 关闭 IP 排除"对照组 |
| #437 全局暗色模式 + 设置卡顿 | ✅ OPEN（1 条评论） | **确认（暗色模式未实现）**；卡顿部分**部分确认**（与 #88 同源，作者称已修） | **确认（暗色模式仍未实现）**；卡顿部分**部分确认** + fork 新增插件市场页每次进入全量重建 UI | 上游 `src/UI/Controls/LiteUI.cs:9-25`；`src/UI/SettingsForm.cs:166-205`；fork `src/UI/Controls/LiteUI.cs:10-26`、`src/UI/Settings/PluginMarketPage.cs:65-90` | **未复现（无 UI 计时实测）**；暗色模式缺失为**静态可判定** | 设置窗体配色主题化；给插件市场页加"内容签名"缓存 |
| #88 V1.2.0 面板设置明显卡顿 | ✅ OPEN（3 条评论，含作者"新版已修复"） | **已解决（作者声明 + 代码已具备修复）** | **已解决（同源修复代码在 fork 中逐字保留）** | 上游 `src/UI/SettingsForm.cs:26-32`（WS_EX_COMPOSITED）、`:166-205`（页面缓存 + SuspendLayout）；fork `src/UI/SettingsForm.cs:229-266` | **静态确认（不需复现）**：修复机制在两棵树中一致存在 | 关闭 issue；如需闭环请作者确认版本号 |
| #78 内存占用高 | ✅ OPEN（1 条评论） | **信息不足（环境已失效）**：报告环境为 Windows 7，官方明确低支持；无 1.3.x 复现证据；代码侧内存持有者均可判为**有界** | **信息不足**：同样无当前版本复现证据；fork 侧持有者同样有界，另有两处需长跑验证的项 | 上游 `src/Core/HardwareHistoryLogger.cs:43-45,257`（7 天保留 + 每分钟 1 点）；`src/System/HardwareServices/FpsCounter.cs:505-510`（PID 字典清理）；`src/Core/UIUtils.cs:19-22,51-53` | **未复现**（需 ≥72h 长跑 + 工作集/私有字节曲线） | 见 §5 复测环境要求 |
| #35 疑似内存泄露 | ✅ OPEN（3 条评论） | **未复现（报告者已自行撤回）**："应该不是内存泄露，我又观察了一天，会有浮动而已" | **未复现**（同上，且 fork 侧已就同类 PID 字典做了加固） | `https://github.com/Diorser/LiteMonitor/issues/35#issuecomment-3669023716`；上游 `src/System/HardwareServices/FpsCounter.cs:49-62,501-511`；fork `src/System/HardwareServices/FpsCounter.cs:728` | **未复现（且原报告已被撤回）** | 关闭 issue 或要求补充长跑曲线 |
| #96 网络数据卡住 & 网卡选择 | ✅ OPEN（2 条评论） | **部分确认（"卡住"显示机制确实存在）**；网卡切换功能**已实现**（设置窗口可切） | **部分确认**：同源冻结回退机制仍在（fork `NetworkManager.cs:463-485`）；另新增 ETW 排除后端为新的变量 | 上游 `src/System/HardwareServices/NetworkManager.cs:294-297,313-316`（lastValidMap 回退）、`:429-433`（10s 重匹配节流）；fork `src/System/HardwareServices/NetworkManager.cs:463-485` | **未复现（未能触发 LHM 网卡传感器失效）** | 见 §2.6 建议步骤 |

---

## 2. 逐 issue 取证

### 2.1 #416 软件导致 System 进程 CPU 使用率上升

**1）当前状态**
OPEN。创建 2026-04-26，最后更新 2026-05-18（仅 1 条评论，来自另一位用户 stz0117）。报告者**没有给出 LiteMonitor 版本号、没有持续时间、没有资源曲线、没有复现步骤**，仅附两张任务管理器截图（开启时 System ≈10%、退出后回归）。评论者 stz0117 提供了关键旁证：AMD 5800H + RTX 3050 笔记本，**MSI Afterburner 同样会引发，HWiNFO 不会**，并引用了 NVIDIA 驱动 NVML 写日志导致 System 占用异常的多个链接。

**2）问题摘要**
开启 LiteMonitor 后，Windows 任务管理器中 **`System` 进程** CPU 持续约 10%；退出软件后恢复。报告者未说明是哪个指标触发的，也未说明是否开启了 FPS 等可选功能。

**3）上游代码证据（文件:行号 + 数据流）**

- **硬件访问确实经由内核驱动**：上游依赖 `LibreHardwareMonitorLib 0.9.6`（`LiteMonitor.csproj`），并自带 **PawnIO 内核驱动**安装流程：`src/System/HardwareServices/DriverInstaller.cs:18`（`RequiredPawnIOVersion = 2.2.0.0`）、`:30-34`（驱动包下载地址）。LHM 读 SuperIO / 内存 / GPU 寄存器走的就是这个驱动。
- **主板/SuperIO 扫描常开**：`src/System/HardwareMonitor.cs:76` `IsMotherboardEnabled = true`（构造函数里恒为真，不可关闭）；`:80` `IsControllerEnabled = ShouldEnableController()`（仅在启用 CPU.Fan / CPU.Pump / CASE.Fan 时才开）。
- **SuperIO 更新节奏由"慢扫描"节流**：`src/System/HardwareMonitor.cs:191` `bool isSlowScanTick = (_secondsCounter % 3 == 0);`，命中时才递归更新主板子树（`HardwareMonitor.cs:233-245`）。
- **上游节流存在缺陷（与本 issue 直接相关）**：`_secondsCounter` 按真实秒推进，而 `UpdateAll()` 的调用频率由 UI 定时器决定（`src/UI/UIController.cs:47` `Interval = Math.Max(80, _cfg.RefreshMs)`，默认 `RefreshMs = 1000`，`src/Core/Settings.cs:16`）。当用户把刷新间隔调到 1 秒以下，**同一个"到期秒"内 UpdateAll 会被调用多次，`% 3` 会多次为真 → SuperIO/主板的内核态读取频率显著高于设计的 3 秒一次**。fork 侧源码注释明确记载了这一点：`src/System/HardwareMonitor.cs:462-465`（"旧实现是 `_secondsCounter % 3 == 0`：同一个到期秒里被调用 N 次就会消费 N 次"）。
- **默认开启、且开销较大的 SMB 内核计数器**：`src/Core/Settings.cs:119` `IgnoreSmbTraffic { get; set; } = true;`（默认开）→ `src/System/HardwareServices/NetworkManager.cs:397-415` 每次流量累计都调用 `GetEstimatedSmbBytes` → `src/System/HardwareServices/PerformanceCounterManager.cs:278-296` 读 4 个 SMB 共享类速率计数器（`SMB Client Shares` / `SMB Server Shares` 的 Read/Write/Sent/Received Bytes/sec，创建于 `:97-101`）。这类计数器需要重定向器/服务器端维护按共享、按文件的统计口径，是 Windows 中**单次读取代价明显偏高**的一类内核计数器，且**默认每轮采集都读**。
- **GPU 传感器每轮 Update**：`src/System/HardwareMonitor.cs:200-215`（`hw.Update()` on GPU），LHM 的 NVIDIA 路径经 NVML/驱动，可触发显示驱动的 DPC（内核态），DPC 在任务管理器中计入 `System`。
- **可选的 ETW 内核 provider（FPS 开启时）**：`src/System/HardwareServices/FpsCounter.cs:76` 会话名 `LiteMonitor_Golden_Session`，`:328` 启动 `LiteMonitorFPS.exe -session_name ... -output_stdout`（PresentMon）。PresentMon 用内核侧 provider（DxgKrnl）采 GPU present 事件，**内核侧事件生成与派发发生在 System 上下文**。但 FPS 默认关闭（`src/Core/SettingsHelper.cs` 默认项 `Key = "FPS" ... VisibleInPanel = false`），按需启动。

**4）fork 对照**

- 硬件访问路径与上游**一致**：`src/System/HardwareMonitor.cs:154` `IsMotherboardEnabled = true`、`:158` `IsControllerEnabled = ShouldEnableController()`；同样使用 PawnIO（`src/System/HardwareServices/DriverInstaller.cs`）。
- **fork 修掉了上游的慢扫描节流缺陷**：`src/System/HardwareMonitor.cs:465` `bool isSlowScanTick = _schedule.TryConsumeSlowScan();`（单调截止时间，同一到期点只消费一次，见 `:1499-1529` 的 `HardwareSamplingSchedule`）→ **fork 不会因低刷新率而放大 SuperIO 内核读取频率**。这一点是 fork 优于上游的实质差异。
- **fork 新增两个内核态来源（均为 opt-in）**：
  - ETW `Microsoft-Windows-Kernel-Network`：`src/System/NetworkTraffic/EtwNetworkTrafficEventSource.cs:74`（provider 名）、`:82`（关键词 0x30）、`:247`（`EnableProvider(..., TraceEventLevel.Verbose, 0x30)`）。启用路径 `src/System/HardwareMonitor.cs:540` → `:791 UpdateTrafficExclusion()` → `:826 EnsureTrafficExclusion()` → `:836` 建会话。**但该后端是需求驱动**：`src/System/NetworkTraffic/NetworkTrafficExclusionService.cs` 明确"关闭或没有有效规则时不创建任何事件采集会话"，且需用户在设置中开启 `IpTrafficExclusionEnabled` 并配置规则 → **默认配置下不产生任何 ETW 会话**。
  - PresentMon ETW：fork 会话名 `LiteMonitor++_Golden_Session`（`src/System/HardwareServices/FpsCounter.cs:836`、`:1650`），同样仅在启用 FPS 时启动。
- **fork 保留 SMB 计数器默认开启**：`src/Core/Settings.cs:153` `IgnoreSmbTraffic = true`；`src/System/HardwareServices/NetworkManager.cs:545-547` 仍在流量累计路径读取 → 与上游同样存在默认的内核计数器读取开销。

**5）复现条件与验证步骤**

- 复现条件（基于代码推导）：Windows 10/11 + 独立显卡/核显 + 主板 SuperIO 芯片（几乎所有消费级主板）；LiteMonitor 常驻；可选放大项：① 刷新间隔调到 80–500 ms（放大上游慢扫描节流缺陷）；② 开启 FPS（引入 PresentMon 内核 ETW）；③ 开启 IP 流量排除（fork 独有，引入内核 ETW）。
- 验证步骤（本报告**未执行**，因需长时间运行 + 内核态分析工具）：
  1. 关闭所有 LiteMonitor 可选功能，仅开 CPU/GPU/MEM/NET 面板项，连续运行 30 min；
  2. 用 `wpr -start GeneralProfile` 或 WPA 抓 **CPU Usage (System) / DPC/ISR 时间**，确认内核态消耗落在哪个驱动栈；
  3. 对照组：关闭 LiteMonitor → 同机同驱动再抓 30 min；
  4. 逐项打开 FPS / IP 排除（fork）做差分。

**6）结论**

**部分确认（机制成立，归因无法在代码层定案）**。
- 成立的部分：LiteMonitor **确实**存在多处内核态来源（PawnIO 驱动下的 SuperIO/GPU 读取、默认开启的 4 个 SMB 共享计数器、GPU 每轮 Update 触发的显示驱动 DPC），因此"LiteMonitor 开启后 System 进程 CPU 上升"在机制上**说得通**。
- 不足的部分：代码中**没有**任何一处能让 LiteMonitor 自身产生 10% 持续内核 CPU 的量级证据；且报告者与评论者都指出**MSI Afterburner 同样触发、HWiNFO 不触发**——两者最显著的差别正是 **NVML / 驱动调用路径**。HWiNFO 用自己的内核驱动并直读传感器，不走 NVML；LiteMonitor 与 Afterburner 都走 GPU 驱动查询路径。这条旁证强烈指向**显卡驱动侧**而非 LiteMonitor 自身代码缺陷。

**7）风险**

- 上游：低刷新率（≤1 s）用户会同时踩到"慢扫描节流失效"（主板内核读取放大）与"每轮读 4 个 SMB 计数器"，两者叠加是当前代码里**唯一可指认的**、与"高频内核态"方向一致的上游缺陷。
- fork：节流缺陷已修复；但默认的 SMB 计数器读取仍在；一旦用户启用 FPS 或 IP 排除，会再叠加 1–2 个**内核 ETW 会话**（fork 独有），这是 fork 相对上游**新增**的内核态面。

**8）建议下一步**

1. 先做归因再谈修复：对报告者的机型请其提供 WPA 抓包（或 `xperf -on PROC_THREAD+LOADER+ DPC_ISR`）定位 System 时间归属；**在没有归因前不要按"LiteMonitor 性能缺陷"立项**。
2. 独立于 #416 立项一个小改进：把 `IgnoreSmbTraffic` 的 SMB 计数器读取改为**按需/低频**（例如 5 s 一次并线性插值），或默认关闭该计数器创建（`PerformanceCounterManager.cs:97-101`）——这是代码层可确证的、可降内核开销的改动，且不影响显示语义。
3. 上游：把慢扫描节流从 `% 3` 改为单调截止时间（fork 已有现成实现可直接参考），消除低刷新率下的放大效应。

---

### 2.2 #437 请求软件能加入全局暗色模式，优化软件设置的流畅度，软件设置卡顿

**1）当前状态**
OPEN。创建 2026-05-17，1 条评论（ROG-EX 追问作者是否会加暗色模式与优化设置流畅度），**作者未回复**。正文实质是"功能请求 + 性能抱怨"的混合体。

**2）问题摘要**
(a) 希望设置界面能跟随系统暗色（"夜间使用打开设置太刺眼"）；(b) 抱怨设置界面卡顿、拖动不顺。

**3）上游代码证据**

- **设置窗体的配色是硬编码浅色，完全不跟随主题或系统**：`src/UI/Controls/LiteUI.cs:9-25` `public static class UIColors { MainBg = Color.FromArgb(243,243,243); SidebarBg = ...(240,240,240); CardBg = Color.White; TextMain = ...(32,32,32); ... }`；`src/UI/SettingsForm.cs` 初始化时直接使用 `UIColors.MainBg` / `UIColors.SidebarBg` / `UIColors.CardBg`。**上游主题系统只作用于面板本体（`Skin` 默认 `DarkFlat_Classic`），设置对话框与主题是两套完全独立的配色** → "全局暗色模式"在 1.3.7 上**不存在**，请求未被实现。
- **设置页卡顿的修复机制确实存在**（与 #88 同一处）：`src/UI/SettingsForm.cs:166-205` `SwitchPage()` —— 页面实例在 `InitPages()` 里**一次性创建并缓存**（`:135-155`），切页时 `_pnlContent.SuspendLayout()` + 预设 `targetPage.Size = _pnlContent.ClientSize` + `Dock = Fill` + `ResumeLayout()`，配合 `:26-32` 的 `WS_EX_COMPOSITED` 防闪烁。这是作者在 #88 里所说的"新版已修复"的实体。

**4）fork 对照**

- **暗色模式同样未实现**：`src/UI/Controls/LiteUI.cs:10-26` 的 `UIColors` 与上游**逐行相同**（同为硬编码浅色）。fork 新增的外观体系（`src/Core/ThemeAppearanceOptions.cs`、`src/UI/AppearanceRuntime.cs`、`src/UI/WindowAppearanceController.cs`）针对的是**面板/任务栏的主题渲染**，不覆盖设置对话框 → **#437 的暗色诉求在 fork 中同样未满足**。
- **切页机制与上游一致**（`src/UI/SettingsForm.cs:229-266` `SwitchPage()` 与上游同构），上游的 #88 修复在 fork 中保留。
- **fork 独有的新增卡顿风险点**：`src/UI/Settings/PluginMarketPage.cs:65-75` `OnShow()` 每次进入都 `RebuildUI()`（`:77-90` 逐个 `Dispose()` 旧控件再重建），随后异步 `LoadIndexAsync()`；`src/UI/Settings/PluginPage.cs:81-87` 同样每次 `OnShow` 全量 `RebuildUI()`。这两页在 fork 中是新增页（上游无插件市场页），**每次切换到该页都要做一次控件树销毁 + 重建** → 这是 fork 里最可能复现"设置卡顿"的路径（上游不存在对应页面）。
- fork 另有 `src/UI/Settings/RenderingRoutePage.cs:47-58` `OnShow()` 带 `_isLoaded` 一次性守卫，不会重复重建。

**5）复现条件与验证步骤**

- 暗色模式：静态可判定——打开设置窗口，观察到浅色背景（`UIColors.MainBg` = RGB 243,243,243）即成立；无需性能实测。
- 设置卡顿复测步骤（本报告未执行）：
  1. 打开设置 → 依次切换 面板/任务栏/监控项/阈值/系统/插件/插件市场 各页，**用 Stopwatch 记录每次 `SwitchPage` 进入到首帧绘制完成**；
  2. 重点测「插件市场」页（fork 独有）：连续进入 10 次，观察单次耗时是否随进入次数上升（重建 + 网络索引刷新）；
  3. 上游对照：同样步骤在 upstream 1.3.7 上跑（插件市场页不存在，只测 6 页）。

**6）结论**

- 暗色模式：**确认（未实现）** —— 上游与 fork 均未提供全局暗色（跟随系统）选项；这是静态可判定的功能缺口，不是性能问题。
- 设置卡顿：**部分确认** —— 通用切页卡顿在上游已由 #88 的修复覆盖（页面缓存 + SuspendLayout），当前代码层面无明显"必卡"路径；但 fork 新增的插件市场页每次进入全量重建 UI，是 fork 侧**最可疑的新增卡顿点**，需要实测计时确认。

**7）风险**

- fork：插件市场页重建成本随页内条目数线性增长；若索引里有几十个插件/主题，每次进入该页都要重建整个列表控件树。
- 两棵树共有：设置窗体配色硬编码，若将来要支持暗色，需要把 `UIColors` 改成主题驱动（涉及 `LiteUI.cs` 全量控件，改造面不小）。

**8）建议下一步**

1. 若确认要做暗色：先给 `UIColors` 增加"从当前主题/系统主题派生"的单一入口，而不是逐控件改造；fork 可复用已有 `AppearanceRuntime`。
2. fork：给 `PluginMarketPage`（与 `PluginPage`）加"内容签名"缓存，签名未变时不重建 UI —— 与 `src/UI/Settings/MonitorPage.cs:186-194` 已有的脏检查思路一致，属于低风险局部改动。

---

### 2.3 #88 V1.2.0 的面板设置明显感觉有些卡顿

**1）当前状态**
OPEN（3 条评论）。作者 Diorser 在最后一条评论中明确写"**新版已修复**"（2025-12-26 18:09，https://github.com/Diorser/LiteMonitor/issues/88#issuecomment-3693192649）。因此"issue 仍 open"只是**未做关闭操作**，不代表问题仍存在。

**2）问题摘要**
v1.2.0 切换设置面板页面明显卡顿；另有评论者反馈设置窗体在系统缩放 ≠100% 时布局异常、无法放大。

**3）上游代码证据**

- **切页卡顿的修复就在当前代码里**：
  - `src/UI/SettingsForm.cs:27-33`：恢复 `WS_EX_COMPOSITED`（注释直接写"恢复 WS_EX_COMPOSITED 以防止闪烁，**同时配合页面卸载机制解决卡顿**"）；
  - `src/UI/SettingsForm.cs:139-154` `InitPages()`：**构造时一次性实例化所有页面并缓存**（`_pages` 字典），不在切页时重建；
  - `src/UI/SettingsForm.cs:166-205` `SwitchPage()`：`SuspendLayout` → 清内容 → 预设尺寸 → `Dock = Fill` → `ResumeLayout`，并用 `Application.DoEvents()` 收敛导航按钮刷新。
  - 作者在评论中给出的第二条解释"卡是因为界面使用的实时渲染的画面，不是原生界面，所以首次打开渲染时会有一点闪"同样被当前代码采纳（面板为自绘渲染，`src/UI/UIRenderer.cs`）。
- **DPI/缩放问题**：作者回复"暂时把 Windows 缩放设为 100% 看看，下个版本解决这个问题"，当前代码有 DPI 感知处理（`src/UI/UIController.cs:54-57` `GetCurrentDpiScale()`，主题 `Scale(dpiScale, userScale)`）。
- 另有一条上游 commit 直接针对同类问题：`43ffd95 (2026-02-05) fix(硬件监控): 修复主板传感器在ForceAll时的高频更新问题`。

**4）fork 对照**

- `src/UI/SettingsForm.cs:229-266` `SwitchPage()` 与上游**同构**（同样的 SuspendLayout + 预设尺寸 + 缓存页面）；`src/UI/SettingsForm.cs` 同样保留 `WS_EX_COMPOSITED`。
- fork 额外新增两页（`RenderingRoutePage`、`PluginMarketPage`），其中插件市场页**未使用**上游的"页面缓存 + 只建一次"策略（每次 `OnShow` 全量重建）→ fork 的"设置页卡顿"在**插件市场页**上有一处与上游不同的新风险。

**5）复现条件与验证步骤**

- 原条件为 v1.2.0（2025-12 前后）。当前 1.3.7 与 fork 均已包含修复代码，**复现的前提（v1.2.0 的旧实现）已不存在**。
- 如需闭环验证：装 v1.2.0 与 1.3.7 各跑一次切页计时对比（需旧版本安装包，本次未做）。

**6）结论**

**已解决或替代**。
- 上游：作者声明已修复，且修复机制在 1.3.7（3b91b5c）中确实存在（页面缓存 + 布局挂起 + WS_EX_COMPOSITED）。
- fork：同一修复原样保留（`src/UI/SettingsForm.cs:229-266`）。
- 残留差异：fork 新增的插件市场页不遵循"只建一次"策略，属**新功能带来的新风险**，不是 #88 本身的未修复。

**7）风险**

- 低。唯一风险是 fork 新增页面的重建开销（见 #437）。
- issue 仍为 OPEN 会持续误导后续排查，建议关闭。

**8）建议下一步**

1. 上游侧：建议作者关闭 #88（或标注 resolved in v1.2.1+），避免重复排查。
2. fork 侧：把插件市场页纳入"页面只建一次"的模式（配合签名失效重建）。

---

### 2.4 #78 内存占用高

**1）当前状态**
OPEN（1 条评论，作者回复）。作者回复："目前对 win7 的支持较低，可以下载新版试试，做了部分优化。"

**2）问题摘要**
报告环境为 **Windows 7**、8 GB 内存。声称开 LiteMonitor 后系统总内存占用从 20% 升到 70%（约 4 GB），但任务管理器里看不到对应进程，且伴随"图标不显示"、"网速测试换算错误"三个现象。

**3）上游代码证据（内存持有者排查）**

- **历史采样数据（有界）**：`src/Core/HardwareHistoryLogger.cs:43-45` —— 采样间隔 10 s、保留期 7 天、保存间隔 5 min；`:257 TrimUnsafe()` 每次落盘前按保留期裁剪；每分钟只产生 1 个聚合点 → 最多约 7×24×60 = 10080 点 × 约 13 个键，**有界**。保存走 `SaveAsync()`（`:289-306`，内部 `Task.Run`），**不在 UI 线程做 IO**。
- **流量历史（有界）**：`src/Core/TrafficLogger.cs` —— `History` 是"按天"一个条目，文件级保存，无逐点累积。
- **PID 维度缓存（有清理）**：`src/System/HardwareServices/FpsCounter.cs:49-62` 四个 `ConcurrentDictionary<int, ...>`（帧计数 / 滑窗历史 / 平滑历史 / 进程名）；`:501-511` 在 FPS < 1.0 时 `TryRemove` 对应条目（进程退出后 FPS 归零 → 被清）。`_processFrameCounts` 的键只置 0 不删除（`:434`），每条目仅几十字节，**增长极慢但无上界**（唯一可指认的上游无界结构）。
- **UI 缓存（有界）**：`src/Core/UIUtils.cs:19-22` 字符串驻留池（键数量 = 指标键数，几十）、`:51-53` 画刷/画笔/字体缓存（键 = 颜色字符串数，主题内几十）、`:127 ClearBrushCache()` 在换肤时清空。
- **硬件树**：LHM 的 `Computer` 固定硬件节点，且 `src/System/HardwareMonitor.cs:550-552` 有"关闭 LHM 24 小时内存缓存"的处理（`DisableSensorHistory()`，反射关闭 LHM 的历史保留）→ 作者"做了部分优化"指的就是这类。
- **Win7 特有问题**：net8.0-windows 目标框架 + LHM 0.9.6 + PawnIO 驱动在 Win7 上不受支持（PawnIO 需要较新内核），驱动装不上时可能走降级路径；但这是"环境不受支持"，不是内存泄漏。

**4）fork 对照**

- fork 的历史采样与流量历史**保持有界**：`src/Core/HardwareHistoryLogger.cs:44` 保留期仍为 7 天；fork 仅改进了 `GetEnabledSupportedKeys`（`:188-210`，去重 + 支持多显卡键），未改保留策略。
- fork 的 FPS 缓存**已加固**：`src/System/HardwareServices/FpsCounter.cs:728` `_entries.TryRemove(pid, out _)`，且有整表清理（`:601` `_entries.Clear()`、`:307-308` 清累加器/平滑队列）→ 比上游的"仅在 FPS<1 时清"更彻底。
- fork 新增的可疑增长点（**需长跑验证，本次无法定论**）：
  - `src/UI/CapturePolicyCleanupLedger.cs:91` `private readonly List<WindowCapturePolicyCleanup> _entries = new();` —— 全文件只有 `:180 _entries.Add(cleanup)`，**没有任何删除路径**；只在 GPU 呈现路由（opt-in）下累积，每个条目是一次清理责任记录。
  - `src/System/HardwareServices/HardwareErrorLog.cs:22` 错误日志文件有 512 KB 上限并滚动归档（`:53-58`）→ 有界。
  - `src/UI/Decor/DecorResourceCache.cs:44,64-65` LRU + `DefaultMaxEntries = 128` + 字节上限 → 有界。

**5）复现条件与验证步骤**

- 原条件（Win7 + 8 GB）**已失效**：当前版本不承诺 Win7 支持，无法在 1.3.7 上复现原场景。
- 最小复测环境（见 §5）：Windows 11 24H2 / 16 GB / 独立显卡 + 主板 SuperIO / 面板开启 CPU+GPU+MEM+NET+DISK 默认项 / FPS 关闭 / 连续运行 ≥72 h。
- 观测项：工作集、私有字节、句柄数、线程数、非分页池/分页池（RAMMap 或 poolmon）、每 6 h 一次快照。

**6）结论**

**信息不足（环境已失效）**。
- 上游：无法在 1.3.7 上复现原报告环境（Win7 不受支持）。代码侧所有主要内存持有者（历史数据、流量历史、UI 缓存、FPS 进程字典）**均为有界或带清理**，**未发现可判定为泄漏的结构**。唯一无界结构是 `FpsCounter._processFrameCounts` 的 PID 键集，但每条目仅几十字节，且仅在启用 FPS 时增长。
- fork：继承上述结构并已加固；新增两处需长跑验证（清理台账 `_entries` 无删除路径、错误日志滚动）。

**7）风险**

- 中低。#78 的"4 GB 占用"与任务管理器对不上，更像是 **Win7 上的内存统计口径问题（standby cache / 提交量 / 驱动未装导致的异常）**，不是 LiteMonitor 进程占用 4 GB。
- fork 的清理台账无界增长仅在 GPU 路由下触发，单条目很小，风险低但应纳入长跑观测。

**8）建议下一步**

1. 不要基于 #78 在 fork 上做内存"优化"改造——原报告环境已失效，且证据不足。
2. 若要结论，请按 §5 做一次 72 h 长跑并提供曲线；在此之前，所有内存结论保持"未复现"。
3. fork：给 `CapturePolicyCleanupLedger` 增加终态条目的裁剪（与已有 `IsTerminal` 语义一致），属低风险加固。

---

### 2.5 #35 疑似内存泄露

**1）当前状态**
OPEN（3 条评论）。**报告者 zzugyl 在第二天自行撤回**："应该不是内存泄露，我又观察了一天，会有浮动而已"（2025-12-18），并补充"也只有几十 MB 占用，非常优秀了"。作者回复："这么点内存应该没事吧，因为有比较复杂的界面实时渲染，内存是会多一点。"

**2）问题摘要**
v1.1.2 下观察到"昨天"与"今天早上"的内存占用截图不同，怀疑泄露。对比基准是 TrafficMonitor（连续多天 ≤10 MB）。

**3）上游代码证据**

- 见 §2.4 的持有者排查：1.3.7 中历史数据 7 天有界、流量按天、UI 缓存按主题键、FPS 进程字典带清理、且 `src/System/HardwareServices/SystemOptimizer.cs:79-107` 维护任务每 180 s `GC.Collect(2, Optimized)`、每 300 s 在工作集 > 30 MB 时 `EmptyWorkingSet`（`:94-106`）——**上游主动做内存收缩**，这与"内存有浮动"的观感一致。
- 注意 `EmptyWorkingSet` 会**主动把进程工作集降到 30 MB 以内**，在任务管理器里表现为"内存占用忽高忽低"——这可能正是 #35 截图里"占用变化"的成因。

**4）fork 对照**

- fork 用**单调触发器**替换了上游的取模节流：`src/System/HardwareServices/MaintenanceScheduler.cs:218-228`（流量保存 60 s / GC 请求 180 s / 工作集 300 s），语义与上游一致但不再有"同一秒多次触发"的缺陷。
- fork 的 GC / 工作集修剪实现同样存在（`src/System/HardwareServices/SystemOptimizer.cs`）。
- fork 的 FPS 字典清理更彻底（`src/System/HardwareServices/FpsCounter.cs:728`）。
- fork 另新增 `src/System/HardwareServices/CommittedMemoryReader.cs`（提交内存读取）与 `src/System/HardwareServices/CpuVoltageResolver.cs`（电压解析）——均为**每次采集读一次并发布单值**，不累积。

**5）复现条件与验证步骤**

- 原报告已被作者/报告者双方视为非泄露（用户自己撤回）。
- 若要彻底关闭：按 §5 环境做 72 h 长跑；**关键判据是"曲线是否单调上升"**，而不是单点内存值。

**6）结论**

**未复现**。
- 上游：无任何指向泄漏的代码证据；报告者本人已撤回；作者已做过内存优化（禁 LHM 历史 + 定期 GC + 工作集修剪）。
- fork：继承上游的有界结构并做了两项改进（单调维护调度、FPS 字典清理），**比上游更不容易出现"看起来在涨"的现象**。

**7）风险**

- 低。但需注意：`EmptyWorkingSet` 制造的工作集"锯齿"会让人误判为泄露；向用户解释时应一并说明。

**8）建议下一步**

1. 建议作者关闭 #35（报告者已撤回）。
2. 若保留观测，应记录**私有字节（Private Bytes）**而非工作集——工作集被程序主动修剪，私有字节才是泄漏判据。

---

### 2.6 #96 网络检测开启一段时间后数据卡住 & 网卡选择

**（本项按任务单要求作为「刷新阻塞」检查，不计入纯性能问题）**

**1）当前状态**
OPEN（2 条评论）。作者回复："设置窗口里可以切换网卡，切换网卡或者重启软件看看是否正常？"；报告者回复"**重启软件正常了**"，并再次请求右键菜单增加网卡选择。

**2）问题摘要**
开启网络检测后运行一段时间，上传/下载数值会**卡住不动**（截图显示停滞）；附带需求：右键菜单选择网卡。

**3）上游代码证据（"卡住"的显示机制确实存在）**

- **冻结回退**：`src/System/HardwareServices/NetworkManager.cs:294-297` 与 `:313-316` —— `ReadNetworkSensor`/`GetBestValue` 在 LHM 传感器值为 `NaN`（或传感器对象失效）时，**回退返回 `lastValidMap[key]`（上一次的有效值）且该值永不失效**。这正是"数值卡住"的**显示层机制**：底层停更、界面保持旧值。`lastValidMap` 在上游是 `HardwareMonitor._lastValidMap`（进程级 `Dictionary<string,float>`，无过期策略）。
- **网卡匹配的 10 秒节流**：`src/System/HardwareServices/NetworkManager.cs:429-433` `MatchNativeNetworkAdapter` —— 原生 `NetworkInterface` 失效后最多 10 s 才重试匹配；`:511-521` `SetNativeAdapter` 在 `GetIPStatistics()` 抛异常时把适配器置空。
- **网卡切换功能已存在**：设置窗口可切换（作者回复）；`src/Core/Settings.cs` 有 `PreferredNetwork` / `LastAutoNetwork`，`src/UI/Settings/SystemHardwarPage.cs:170-171` 是对应下拉框（fork 侧同功能在 `src/UI/Settings/SystemHardwarPage.cs:488-489`）。
- **右键菜单选网卡**：上游无此入口（需求未实现）。

**4）fork 对照**

- **同源冻结机制仍在**：`src/System/HardwareServices/NetworkManager.cs:463-466` 与 `:482-485` 仍是 `lastValidMap` 回退（同样无过期）。
- fork 把网卡状态从墙上时间改为**单调时钟**（`TrafficAccumulator` + `TimeProvider`），并新增了 ETW 排除后端接线（`:96` `AttachTrafficExclusion`、`:104-140` `UpdateExclusionTarget`）。
- **fork 的新增变量**：`src/System/NetworkTraffic/NetworkTrafficExclusionService.cs` 在窗口未打开/被丢弃时会发布"无数据"快照；若用户开启 IP 排除，排除后的实时网速来自 ETW 窗口（默认窗口 1 s，`WarmupGuardSeconds = 2.0`，`MaxWindowSeconds = 30.0`），在预热期/超长间隔窗口会走 `Degraded`/`Failed` 状态 → 这是一条**新的**可能"看起来卡住"的路径（但状态是显式的，不会伪装成 0）。

**5）复现条件与验证步骤**

- 复现步骤：开启网络监控 → 让网卡经历一次**链路变化**（WiFi 断开重连 / 网线拔插 / VPN 建立），观察面板 NET.Up/NET.Down 是否停在旧值。
- 判别方法：在 LHM 侧确认传感器是否真的停止更新（对照任务管理器/资源监视器的网络速率）；若任务管理器也在跳而面板不动，则命中 `lastValidMap` 冻结路径。
- 建议最小修复验证：把 `lastValidMap` 的回退加**过期**（例如超过 N 秒无更新则返回 null 显示 `--`），即可把"假死"变成"明确的缺失"。

**6）结论**

**部分确认**。
- "卡住"的**显示机制在代码中确实存在且可解释**（冻结回退 + 永不失效的 `lastValidMap`），与报告者"重启后恢复"的描述一致（重启清空 `lastValidMap` 与 LHM 缓存）。
- 但"底层为什么会停更"（LHM 网卡传感器在链路变化后失效 / `GetIPStatistics` 抛异常）在本次静态取证中**无法定案**，故只给"部分确认"。
- 网卡选择：**功能已存在于设置窗口**（作者回复属实），但**右键菜单入口上游没有**，该子需求**未实现**（fork 亦未见）。

**7）风险**

- 上游：低（显示误导，非数据损坏）。
- fork：若启用 IP 排除，排除后网速来自 ETW 窗口，其"预热 2 s / 丢弃超长窗口"的语义会让数值在特定时刻缺失；与冻结回退叠加时，用户体验是"数字不动"。

**8）建议下一步**

1. 上游：为 `lastValidMap` 增加过期语义（建议 N 秒），让停更表现为 `--` 而非旧值冻结；这是小改动、收益明确。
2. 上游/均可：右键菜单增加"网卡选择"子菜单（`src/UI/MenuManager.cs` 已有菜单结构）。
3. fork：把 ETW 排除窗口的 `Degraded/Failed` 状态透传到面板提示，避免与"冻结"混淆。

---

## 3. 共性发现（跨 issue 的同一根因）

**A. 「内核态成本」是六个 issue 中三个（#416、#78、#35）的共同交汇点。**
LiteMonitor 的硬件采集天然包含内核态成分：PawnIO 驱动下的 SuperIO/GPU 寄存器读取、默认开启的 4 个 SMB 共享速率计数器、GPU 每轮 `hw.Update()` 触发的显示驱动调用、PresentMon 的内核 ETW（FPS 开启时）。这些成本在任务管理器里表现为 `System` 进程（内核线程/DPC）或被计入驱动栈。**因此 #416 与 #78/#35 的"占用/内存"叙述在"内核态"这一层上是同一族问题**，但各自的量级证据都只到"机制存在"，没有到"足以解释 10% CPU / 4 GB 内存"。

**B. 「同一到期时间被消费多次」的节流缺陷（上游有，fork 已修）。**
上游 `HardwareMonitor.cs:191` 用 `_secondsCounter % 3 == 0` 判断慢扫描，fork 的源码注释明确记载该写法会在同一到期秒内被消费 N 次（fork `HardwareMonitor.cs:462-465`）。上游同样用取模实现维护任务（`SystemOptimizer.cs:82,88,94`：`% 60`、`% 180`、`% 300`），fork 改成了单调截止时间（`MaintenanceScheduler.cs:218-251`）。**这是本次取证中唯一一条"上游确有缺陷、fork 已修复、且与 CPU 开销直接相关"的证据链**，同时也是 #416 在低刷新率场景下的放大器。

**C. 「冻结值回退」是 #96 的机制，也是 #416 类"看起来不动"现象的共性。**
`lastValidMap`（上游 `NetworkManager.cs:294-297,313-316`；fork `:463-466,482-485`）在传感器失效时返回**永不失效的旧值**。它同时作用于 NET 指标，也可在 LHM 重载期间让其它指标"看起来卡住"。**上游与 fork 完全同源，fork 未修。**

**D. 「UI 线程取值带 10 ms 等待」是所有"卡顿"投诉的放大器。**
`src/System/HardwareServices/HardwareValueProvider.cs:266`（上游）与 `:596`（fork）都是 `Monitor.TryEnter(_lock, 10, ...)`。UI Tick 里每个可见指标都要取一次值（上游 `UIController.cs:186-233`，fork `:1024-1103`），历史采样还会额外取一遍（上游 `:233`，fork `:1103`）。一旦硬件锁被重载/采集长时间持有，**每个键最坏 10 ms × 键数（10–20）= 100–200 ms 的 UI 停顿**。这是上游与 fork **共有**的卡顿放大器，也是 #437 "设置卡顿"与 #88 的共同背景噪声。

**E. 「设置对话框与主题系统两套配色」是 #437 的根因。**
上游 `LiteUI.cs:9-25` 与 fork `LiteUI.cs:10-26` 的 `UIColors` 完全相同且硬编码浅色；两棵树的"暗色"能力都只作用在面板本体。

---

## 4. 未能验证及原因

| 未验证项 | 原因 | 最低限度复测环境 | 需要用户补充的数据 |
|---|---|---|---|
| #416 的 `System` 进程 CPU 归因（到底是不是 LiteMonitor 造成） | 本次为**静态代码取证**，未运行程序；且内核态归因需要 WPA/xperf/ETW 级别的跟踪，任务管理器截图不足以定案 | Windows 11 24H2 + 独立显卡笔记本；LiteMonitor 常驻 30 min；同机对照（开/关）各抓 30 min | WPA 的 **CPU Usage (System)** 火焰图；`xperf` 的 DPC/ISR 采样；报告者机型 + 显卡驱动版本；是否开启 FPS |
| #416 中「PresentMon ETW」与「fork ETW 排除」各自的实际内核开销 | 两者均为 opt-in，本次未启用实测 | 需安装 PresentMon（FPS 开启）与配置排除规则（fork 独有） | 每个开关状态下的 System CPU 差值 |
| #78 / #35 的内存曲线（是否随运行时长单调上升） | 需要 ≥72 h 连续运行；单次内存值**不能**判定泄漏（且程序自身 `EmptyWorkingSet` 会主动压低工作集） | Windows 11 24H2 / 16 GB；默认面板项；FPS 关；每 6 h 采样一次 | **私有字节（Private Bytes）**曲线、句柄数、线程数、poolmon 非分页池 |
| #437 / #88 的设置页切换耗时 | 需要 UI 级计时；本次未构建运行 | 各自版本各跑一次切页计时 | 每页 `SwitchPage` → 首帧耗时（尤其插件市场页连续进入 10 次） |
| #96 的"数据卡住"实机触发 | 需要制造网卡链路变化（拔线/WiFi 断开/VPN 上下线）并同时观察 LHM 传感器是否停更 | 双网卡机器 + 制造链路抖动 | 面板数值 + 任务管理器网络速率的同步截图；`NetworkManager` 的 `lastValidMap` 日志 |
| fork 特有增长点（`CapturePolicyCleanupLedger._entries` 无删除路径） | 需要启用 GPU 呈现路由并反复触发路由切换 | 启用 GPU 路由 + 反复切主题/重建窗口 | 台账条目数随时间的变化 |
| 上游 1.3.7 与 fork 的**运行时行为差异** | 本次**未构建、未运行**任一版本（只读取证，且任务单要求不做长时间构建/测试） | — | — |

---

## 5. 建议优先级（按对 fork 的实际影响排序）

| 序 | 事项 | 关联 issue | 影响面 | 建议动作 | 风险 |
|---|---|---|---|---|---|
| **P0** | `lastValidMap` 冻结回退加**过期语义**（超过 N 秒无更新返回 null / 显示 `--`） | #96（并缓解 #437/#88 的"看起来卡住"） | 上下游共有；改动小、语义收益明确 | 上游与 fork **分别**改（fork 已有 `GpuKey`/单调时钟基础） | 低 |
| **P1** | **SMB 共享速率计数器改按需/低频读取**（默认关闭时甚至不创建计数器） | #416、#78、#35 | 默认配置下每轮采集都读 4 个高价内核计数器，是**唯一可指认的默认内核开销** | 在 `PerformanceCounterManager` 增加"需要时才建/读"，或把读取降到 5 s 一次并插值 | 低（需保证扣除量仍然准确） |
| **P1** | 上游慢扫描/维护节流从取模改为单调截止时间 | #416（低刷新率放大）、共性 B | 上游明确缺陷；**fork 已修**，可作为上游 PR 的参考实现 | 若 fork 要回馈上游，直接复用 `HardwareSamplingSchedule` / `MaintenanceScheduler` | 低 |
| **P2** | 插件市场页 / 插件页按"内容签名"缓存，避免每次 `OnShow` 全量重建 | #437、#88（fork 特有） | 只影响 fork 的设置页体感 | 在 `PluginMarketPage.OnShow` 加签名判空 | 低 |
| **P2** | 设置对话框配色主题化（`UIColors` 改为从主题/系统主题派生） | #437 | 功能缺口，改造面较大（`LiteUI.cs` 全量控件） | 先做单一派生入口，再逐控件替换 | 中 |
| **P3** | `CapturePolicyCleanupLedger` 终态条目裁剪 | #78（内存观察项） | 仅 GPU 路由下累积，单条目很小 | 依据 `IsTerminal` 语义加裁剪 | 低 |
| **P3** | 关闭/标注 upstream #35、#88（报告者已撤回 / 作者已声明修复） | #35、#88 | 仅影响排查效率，无代码影响 | 请上游作者处理 | 无 |
| **—** | #416 本身 | #416 | **不建议在归因前立项为 LiteMonitor 缺陷** | 先向报告者索取 WPA/xperf 归因；在此之前按 P1 的计数器优化即可 | — |

---

*本报告仅做取证，未修改任何产品源码（src/**）、未提交、未改动设置与用户数据；所有 git 操作均为只读命令。报告文件本身为本次任务唯一产出。*
