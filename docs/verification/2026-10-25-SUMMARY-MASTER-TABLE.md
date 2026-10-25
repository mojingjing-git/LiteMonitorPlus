# 上游 Open Issue 核实 —— 总表（38 个唯一 issue）

| 项 | 值 |
|---|---|
| 检查日期 | 2026-10-25 |
| issue 来源 | `Diorser/LiteMonitor` Open Issues |
| 唯一 issue 数 | **38**（TASK-1~5 共列出 39 条，#96 在 TASK-1 与 TASK-5 各出现一次，去重后 38） |
| issue 状态复核 | **38/38 全部仍为 OPEN**（上游一个都没关闭）→ issue 状态不能作判据，判据只能是代码路径 |
| 上游基线 | `upstream/master` = `3b91b5c`（1.3.7） |
| fork 基线 | 工作树 `master` = `2a1c57d`（核实期间被并行的主题市场会话从 `5e756e7` 推进而来；上游基线未变） |
| 取证方式 | 5 个 Agent 并行只读取证 + Web/协议类真机端到端 A/B（fork 与 upstream 分别构建，打同一套 12 用例矩阵） |
| 引用抽查 | 抽查 10 余处 `文件:行号` 引用，全部逐字命中 |

---

## 0. 勘误（2026-10-25 晚，补于修复阶段）

**#574 的异常来源归因被实测推翻并修正。**

| 项 | 原表述（出自 `2026-10-25-TASK-3-crash-hardware.md`，本文 §2 沿用） | 实测结论（P-F12 用探针逐条验证） |
|---|---|---|
| `ArgumentException("Parameter is not valid.")` 的抛出者 | 已释放的 **Graphics**（`Font.GetHeight(已释放 Graphics)`） | 已释放的 **Font**（`Font.Height` / `Font.GetHeight` 在字体已释放时抛此异常）；`GetHeight(已释放 Graphics)` 抛的是**另一条** `ArgumentException`，消息为 `graphics` |
| 旧路径的 DPI 语义 | 暗示 `GetHeight(Graphics)` 与 `Font.Height` 语义相近 | `Font.Height` **恒为 96 DPI 基线**；`GetHeight(float dpi)` 与 `GetHeight(Graphics)` 在同 DPI 下**逐位相等** ⇒ 旧路径在 150%/200% 下**必然裁切行高** |

⇒ 结论不变（#574 的 fork 侧结构风险确认成立，修复由 P-F12 落地），但**触发条件从「Graphics 释放」改为「字体释放（切主题 / 换字体 / 字体资源被重建时）」**，因此关闭 #574 所需的日志证据也应指向字体生命周期而非 Graphics。

**基线 flaky 用例已定位**：`tests/LiteMonitor.Tests/ReviewMarketPersistenceRegressionTests.cs`（主题市场域，与本轮修复无关）。F15 与 F12 两个包独立撞上同一处偶发失败；该文件源码注释自陈「单跑全绿、全量间歇挂」。⇒ **门禁跑全量时若见此失败，属基线既有，不得归因于本轮任何包。**


---

## 1. 判定口径

| 标记 | 含义 |
|---|---|
| **✅ 生效** | fork 当前版本中该缺陷/退化仍然存在，用户仍会遇到 |
| **🟡 部分生效** | 主路径已修，但仍有未覆盖的场景或缺运行时自愈 |
| **❌ 不生效** | fork 当前版本已无此问题，可关闭 |
| **⚠️ 信息不足** | 缺堆栈/长跑数据/特定硬件，无法判定 |

---

## 2. 总表（按判定分组，编号升序）

### ✅ 生效 —— fork 侧需要处理（24 项）

| # | 标题 | 类别 | 上游 1.3.7 | fork 当前版本 | 关键代码证据 | 建议下一步 |
|---|---|---|---|---|---|---|
| 96 | 网络检测一段时间后数据卡住 & 网卡选择 | 性能/刷新 | 部分确认 | 部分确认 | 上游 `NetworkManager.cs:294-297,313-316`；fork `:463-485`；`lastValidMap` 冻结回退**永不失效** | 给冻结值加过期语义（**P0**） |
| 245 | 影响 USB 设备识别（Keil/DAPLink） | 设备占用 | 部分确认 | 部分确认 | `ShouldEnableController()` 缓解已发布，作者确认；报告者反馈 1.2.9 仍有 | 追加设备隔离规则 |
| 290 | 核显/独显切换时闪退 | 崩溃 | 部分确认 | 部分确认 | 上游已有 `[Fix #290]` catch + 延迟重载；fork 同构，残留「GPU Key 为序号式」「映射 10 分钟兜底」「首选卡消失被静默改选」 | 用唯一实例 ID 替代序号式 Key |
| 416 | System 进程 CPU 使用率上升 | 性能 | 部分确认（内核态来源真实存在，但无代码证据指向 LiteMonitor 自身；作者与评论者均指向 NVIDIA 驱动） | 部分确认，**内核态面更大**（新增 ETW 内核 provider，均 opt-in） | 上游 `DriverInstaller.cs:18,30-34`、`HardwareMonitor.cs:76,191`、`Settings.cs:119`（默认开 SMB 计数器）；fork `EtwNetworkTrafficEventSource.cs:74,82,247` | **先做 WPA 内核态归因再决定是否立项**；SMB 计数器按需/低频 |
| 434 | GPU 使用率不显示 | GPU | 部分解决（RC1 已修 / RC2 未消） | 部分解决 | 上游 `SensorMap.cs:141-143` + `HardwareMonitor.cs:587-622`（`RepairGpuSensorMap`）；fork 有 `GpuWarmup.cs` 但**无 `RepairGpuSensorMap`** | 移植运行时自愈（**P1**） |
| 437 | 设置 UI 卡顿 + 请求全局暗色模式 | 性能/UI | 确认（暗色未实现 `LiteUI.cs:9-25`） | 确认（暗色仍未实现；**新增插件市场页每次 `OnShow` 全量重建 UI**） | 上游 `SettingsForm.cs:166-205`；fork `PluginMarketPage.cs:65-90` | 配色主题化 + 插件市场页内容签名缓存 |
| 450 | B580 需手动切换首选显卡才显示 | GPU | 已解决（RC1）+ RC2 未消 | 部分解决 | 同 #434；另 fork `MonitoredGpuCount==1` 时自动模式只监控 1 张卡 | 补自愈 + 复核自动模式选卡语义 |
| 499 | 任务管理器独显占用 0，软件仍显示显存占用 | 显示语义 | 语义差异（非缺陷） | 语义差异**仍在**，旧值已消 | fork `HardwareValueProvider.cs:958-1023`、`GpuVramSource.cs`、`GpuVramCapacity.cs` | 需截图判读 + 明确显示口径文案 |
| 502 | AccessViolation — NvmlDeviceGetPowerUsage | 崩溃 | 部分确认（根因在 LHM 0.9.6 库内） | 部分确认（根因未修，但改用 `HardwareErrorLog` **可观测性更好**） | `LibreHardwareMonitorLib 0.9.6` `_nvmlDevice` 为 `readonly` 缓存字段 | 升级/锁定 LHM + 保留错误日志 |
| 538 | 同型号 SSD 只显示第一块 | 读数缺失/设备不可区分 | 确认 | 确认（未修） | 上游 `HardwareScanner.cs:79` / fork `:85` 均 `.Select(h=>h.Name).Distinct()` → **按显示名去重** | 改用唯一实例 ID（同仓库 `ListAllGpuOptions` 已有正确范式） |
| 553 | 锁屏/关屏后任务栏数据丢失 | 窗口生命周期 | 部分确认 | 部分确认（同上游） | 父销毁⇒子销毁**本机实测**；面板是 WS_CHILD；`TaskbarForm` 无 `OnHandleDestroyed`；全仓 `WM_POWERBROADCAST`=0 处 | cherry-pick 上游 PR #555 |
| 568 | 任务栏位置改变后错乱且重叠 | 窗口/坐标 | 确认（算法实测，Win10 路径） | 确认（等价存在） | `TaskbarStrategyWin10.cs` 忠实移植实测：任务列表 1200→640，图标右移 600px，面板 X 600→900；**无 Dock 方向枚举**（靠宽高比猜） | cherry-pick PR #555 + 补 Dock 方向枚举 |
| 573 | 多屏开机后面板位置不固定 | 窗口/坐标 | 确认（代码级+算法实测） | 确认（等价存在） | `FindHandles` 目标设备缺失即回退主屏（`:192`/`:218`）；回退句柄有效 ⇒ `IsTaskbarValid` 恒真 ⇒ **永不重解析** | cherry-pick PR #495（#573/#583 正主） |
| 574 | `Parameter is not valid.` 异常 | 崩溃/UI | 信息不足 | 部分确认（结构风险） | 上游 v1.3.6 与 master 全树零匹配 `Font.GetHeight()`（报告二进制非上游源码构建）；**fork 在绘制路径重新引入** `HorizontalLayout.cs:57` + `UIController.cs:1909` | 补 fork 侧的 `GetHeight` 消除；索要崩溃日志 |
| 577 | 两个机箱风扇只显示一个 | 读数缺失 | 确认 | 确认（未修） | `FanMapper.cs:139` `if (caseFan != null) targetMap["CASE.Fan"] = caseFan;` → **单槽位**，上下游逐行相同 | 风扇按唯一标识分组 |
| 579 | 闪退并伴随网络/GPU 数据缺失 | 崩溃 | 确认 | 确认（未修） | 根因在 `DiskInfoToolkit 1.1.2` 自建线程 `DevicesChangedListener()`，**应用 catch 结构上覆盖不到** | 升级依赖 + 库侧异常边界 |
| 580 | Cloudflare Tunnel 代理访问异常 | Web/反代 | 部分确认（原根因已修，`wss://` 由 `location.protocol` 支持） | **确认存在且更严重**（**真机实测**） | fork `WebSessionManager.cs:1937-2008` Host/Origin 白名单 + 端口强校验 ⇒ 反代必 **403**；上游**完全不做 Host 校验** ⇒ 同场景 `101` | 实现 `WebServerTrustProxyHeaders`（当前是**死设置**：全仓只声明+赋值+字段声明，无任何读取点）或提供 `WebServerAllowedHosts`（**P0**） |
| 583 | 远程桌面后副屏任务栏面板位置错误 | 窗口/坐标 | 确认（代码级+算法实测） | 确认（等价存在） | 探针实测目标 DISPLAY2 → **返回主屏任务栏** | cherry-pick PR #495 |
| 599 | 驱动更新后监控条被合成层遮挡 | 窗口/Z序 | 确认（**本机实测复现**） | 确认（等价存在） | `TaskbarStrategyWin11.cs:64` 恒带 `SWP_NOZORDER`，全仓无同级 z-order 维护；探针：新兄弟窗插到前面后 10 次定位**不恢复**，`HWND_TOP` 立即恢复 | cherry-pick PR #600（**直接修复**） |
| 601 | 与 HWiNFO 同时运行冲突 | 设备占用 | 确认（截图：HWiNFO ISA Lock 点名 `LiteMonitor.exe [@ .NET TP Worker]`） | 确认（未修，无任何磁盘访问策略） | `IsStorageEnabled=true` 无条件 | 引入磁盘访问策略（fork 计划里的 `DiskProbePolicy` **未落地**） |
| 612 | Always-on-top 间歇失效 | 窗口/Z序 | 确认（代码级） | 确认（等价存在） | 置顶看门狗 `MainFormBizHelper.cs:91-105` 只查 `WS_EX_TOPMOST` **样式位**、不查真实 z-order；`!_form.Visible` 早退使纯任务栏模式完全停摆 | cherry-pick PR #373（CONFLICTING，需手工合并）+ 样式位与 z-order 双判 |
| 614※ | **Upgrade 头冒号后空格容忍**（本次 A/B 新发现，无对应 issue） | Web/协议 | 容忍 0..N 空格（`TryGetHeaderValue`，`upstream:117-122`） | **不识别升级**：`Regex.IsMatch(req, "Upgrade: websocket")`（fork `:1323-1327`） | 实测：`Upgrade:websocket` / `Upgrade:  websocket` 在 fork 返回 **200 OK + 登录页 HTML**（升级被静默降级），上游返回 `101`。RFC 7230 §3.2.4 允许 OWS | 改用正规头解析（**P0/P1**，否则 #589 类现象会以另一种形态复现） |
| 245※ | 多个机箱风扇 / 同型号盘 / 可移动盘等设备侧群体问题 | 设备占用/数据 | 见上 | 见上 | 见上 | 见上 |

> ※ 614 为本次实机 A/B 新发现的独立缺陷，编号为便于排序临时标注，**不对应任何上游 issue**。上表其余行中 #245 的标题栏按原单保留。

### 🟡 部分生效 —— 主路径已修，剩余场景未覆盖（2 项）

| # | 标题 | 上游 1.3.7 | fork 当前版本 | 说明 |
|---|---|---|---|---|
| 592 | Intel B580 使用率不显示 | 部分解决：键可建（Arc Load 传感器名 `GPU Core` 可命中），**值**依赖 Intel GCL 遥测 | 部分解决（同上游，fork 额外支持多卡并存） | **RC2：GCL 不可用 ⇒ 值恒 null ⇒ 显示 0。上下游均未消解，预热/重建映射对其无效**；需 B580 真机 + LHM `GetReport()` |
| 596 | U5 225H 需切换首选 GPU 才恢复 | 已解决（+ `RepairGpuSensorMap` 二次自愈） | 部分解决：预热覆盖建图期，**无「键缺失→重建映射」运行时自愈** | 移植自愈机制即可对齐上游 |

### ❌ 不生效 —— 可关闭（9 项）

| # | 标题 | 上游 1.3.7 | fork 当前版本 | 依据 |
|---|---|---|---|---|
| 35 | 疑似内存泄漏 | 未复现（**报告者已自行撤回**「应该不是内存泄露…会有浮动而已」） | 未复现 | 报告者撤回；fork 已就同类 PID 字典加固（`FpsCounter.cs:728`） |
| 88 | v1.2.0 面板设置卡顿 | 已解决（作者声明 + 代码已有页面缓存/SuspendLayout） | 已解决（同源修复逐字保留） | `SettingsForm.cs` 页面缓存 + `WS_EX_COMPOSITED` |
| 235 | 启动时节点不可达，恢复后插件不刷新 | 已解决（`4036cee` 线性退避） | 已解决（等价） | `PluginManager.cs:505-534` |
| 287 | 切换 Wi-Fi 后局域网 IP 未更新 | 已解决（完整 `[Fix #287]` 标记链） | 已解决**且强于上游**（额外有 `InfoService.cs:71-75` 网络变更即刷 UI + 单调时钟缓存窗） | 上游 `NetworkManager.cs:35,45-51,88-105,190-196` |
| 497 | AMD 9070 GRE 温度/频率/功耗缺失 | **非代码缺陷**：LHM 传感器存在且可匹配，值依赖 ADL/OD5/OD6 通路；用户以 DDU 清理驱动后恢复 | 非代码缺陷（匹配规则等价） | 用户评论已自证 |
| 521 | U5 255 核显使用率异常 | 已解决（建图期预热） | 已解决 | `SensorMap.cs:141-143` |
| 569 | 集成显卡使用率读取不到 | 已解决 | 已解决**且超出**（支持核显+独显同时监控，正对应评论诉求） | fork `GpuKey.cs`、`GpuRegistry.cs` |
| 589 | 反代下 WS 握手失败（头名大小写） | 已解决（`3b91b5c` 已重构该文件） | 已解决且更严（重复键拒绝 + RFC6455 形态校验） | **真机实测**：4 种大小写全部 `101`；fork `WebSessionManager.cs:2616-2678` |
| 603 | 核显 GPU 使用率为 0 | 已解决 | 已解决（独立路径 `GpuWarmup`；但缺运行时自愈） | 上游 `SensorMap.cs:330` `hw.Update()`；fork `SensorMap.cs:191` 为注释态，走独立预热 |

### ⚠️ 信息不足 —— 需补数据才能判定（3 项）

| # | 标题 | 缺什么 |
|---|---|---|
| 78 | 内存占用高 | ≥72h 长跑的**私有字节**曲线（工作集会被程序自身 `EmptyWorkingSet` 压低而制造锯齿）；原环境为 Win7，官方低支持 |
| 416※ | System 进程 CPU 上升（归因部分） | WPA/xperf 的 `CPU Usage (System)` 火焰图 + DPC/ISR 采样；报告者机型/驱动版本/是否开 FPS |
| 582 | 读取磁盘 SMART 空引用崩溃 | `LiteMonitor_Error.log` 附件 / 崩溃堆栈（issue 仅附件未取得） |

> ※ #416 同时出现在「生效」与「信息不足」两处：*机制层面确认存在*（PawnIO 驱动 + 默认开启的 4 个 SMB 共享计数器 + GPU 每轮 Update 触发的显示驱动 DPC），但*「LiteMonitor 自身产生 10% 内核 CPU」在代码层无证据*，作者与评论者均指向 NVIDIA 驱动。**不建议在拿到内核态归因前立项为 LiteMonitor 缺陷。**

---

## 3. 统计

| 判定 | 数量 | 说明 |
|---|---|---|
| ✅ 生效（需处理） | 24 | 含 1 项本次新发现（Upgrade 头空格容忍） |
| 🟡 部分生效 | 2 | #592 #596（均为 Intel 独显 GCL 遥测 / 缺运行时自愈） |
| ❌ 不生效（可关闭） | 9 | #35 #88 #235 #287 #497 #521 #569 #589 #603 |
| ⚠️ 信息不足 | 3 | #78 #416（归因部分） #582 |
| **合计（唯一 issue）** | **38** | #96 跨 TASK-1/5 只计一次；#416 跨两类只计一次 |

**按领域**：窗口/任务栏 7 项全生效且零缓解 · GPU 9 项中 5 生效/2 部分/2 不生效 · 崩溃与设备 11 项中 8 生效 · Web/插件 6 项中 2 生效/1 新发现/3 不生效 · 性能 6 项中 3 生效/2 不生效/1 信息不足

---

## 4. 最高杠杆的三件事

1. **cherry-pick 上游 4 个未合并 PR**（fork 任务栏侧与上游**逐字节相同**，可直接吃）：#600（#599 直接修复）、#495（#573/#583 正主）、#555（覆盖 #553/#539/#568，含 10s 恢复期 + `WM_POWERBROADCAST` + 缓存键扩展）、#373（#612，CONFLICTING 需手工合并）→ 一次覆盖 7 项「生效」中的 6 项。
2. **修两个 Web 回归**：`WebServerTrustProxyHeaders` 是死设置导致反代必 403（#580）；`IsWebSocketUpgrade` 正则要求恰好一个空格导致合法请求被静默降级（新发现）。两者都是**改几行**的量级，但都直接决定网页端能不能用。
3. **把「无值」与「真实 0」在渲染层分开**：这一处同时影响 #514（风扇 0 RPM）、#499（显存旧值）、以及 GPU 全部 RC2 类（核显 0%）——用户目前无法从界面区分「硬件不支持」「读取失败」「真的是 0」。这也是关闭这批 issue 的前置条件。

---

## 5. 交叉发现（跨任务单的横向问题）

| # | 横向问题 | 涉及 issue | 说明 |
|---|---|---|---|
| A | **设备身份用显示名而非唯一实例 ID** | #538 #577 | 同仓库 `ListAllGpuOptions` 已有正确范式（`Identifier` 作值 + `#index` 后缀），磁盘/风扇侧未对齐 |
| B | **原生句柄生命周期由依赖库单方面决定** | #502 #290 #466 #601 #579 | 两侧均固定 `LibreHardwareMonitorLib 0.9.6` + `DiskInfoToolkit 1.1.2` ⇒ 同一缺陷必然同时存在 |
| C | **异常边界只在应用自己的线程上** | #579 #582 | 跨不过依赖库自建线程 |
| D | **缺失与「真实 0」在显示层不可区分** | #514 #499 #603 | `value ?? 0.0f` |
| E | **上游慢扫描节流缺陷** | #416 #437 | 上游 `_secondsCounter % 3 == 0` 在低刷新率下同一到期秒被消费 N 次；**fork 已用单调截止时间修掉，可回馈上游** |
| F | **fork 相对上游的三处能力净减少** | #611 | `continue_on_error` 语义删除 → `native://newyear` 删除 → `Holiday.json` 降级 v1.0（叠加 `https`→`http`）⇒ 不止 Holiday，任何「主源失败→备用源」模板在 fork 上都无法工作 |
| G | **计划标「已实施」但代码未落地** | #601 #466 | fork 的 `hardware-stability-hardening.md` / `parallel-dispatch` 标注已实施，但 `git ls-tree` 无 `HardwareSupervision`/`HardwareContracts`/`HardwareProbe`/`DiskProbePolicy`，grep 零匹配 |

---

## 6. 未能验证的部分（诚实清单）

| 范围 | 原因 |
|---|---|
| 全部行为级复现（硬件类） | 本机为单卡 AMD 台式机，无 Intel 核显/独显、双同型号 SSD、多机箱风扇、可移动盘 |
| #580 端到端真实隧道 | 无对外实例，且按约束不对外部生产服务发请求；已用等价 Host 改写命中同一 403 分支 |
| #599 真实驱动热更新 | 会改本机驱动，超出边界；已用模拟 z-order 插入实测同一机制 |
| #553 锁屏后副屏下线 | 本机副屏无独立任务栏，无法复现父任务栏被销毁 |
| #583 / #573 / #568 / #539 | 需 RDP 接入断开、冷启动竞态、真实 Win10 + Dock 方向改顶/左/右、真实分辨率切换 |
| #582 / #574 | 缺崩溃转储与日志附件 |
| #78 / #35 内存曲线 | 需 ≥72h 长跑（工作集不可用，须私有字节） |
| #416 内核归因 | 需 WPA/xperf 火焰图 |
| #611 / #235 / #287 / #96 端到端 | 未构建完整产品二进制；#96 需制造链路抖动 |
| 运行时行为差异 | 除 Web/协议类外，**未构建、未运行**任一版本 |

---

## 7. 关联交付物

| 文件 | 内容 |
|---|---|
| `2026-10-25-TASK-1-performance.md` | 性能与资源占用逐 issue 取证 |
| `2026-10-25-TASK-2-gpu-sensors.md` | GPU 传感器矩阵 + LHM 0.9.6 IL 级解码取证 |
| `2026-10-25-TASK-3-crash-hardware.md` | 崩溃/设备占用分组 + 影响等级 |
| `2026-10-25-TASK-4-window-display.md` | 窗口生命周期事件表 + 真实 Win32 探针实验 |
| `2026-10-25-TASK-5-web-plugin.md` | Web/插件代码映射表 |
| `2026-10-25-WEB-PROTOCOL-LIVE-VERIFICATION.md` | **真机端到端 A/B**：fork ↔ upstream 12 用例矩阵 |
| `.tmp/verification/`（gitignored） | 探针源码 + 两次运行原始输出；构建产物已清理 |
