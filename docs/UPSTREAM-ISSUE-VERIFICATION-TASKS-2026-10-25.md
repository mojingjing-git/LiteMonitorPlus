# 上游 Open Issue 核实任务单

基线日期：2026-10-25  
Issue 来源：[Diorser/LiteMonitor Open Issues](https://github.com/Diorser/LiteMonitor/issues?q=is%3Aissue%20is%3Aopen)  
核实范围说明：仅核实任务单列出的上游开放 issue。此任务是调查与验证，不要求修改产品代码。每个 Agent 负责一张任务单；可以并行执行。

## 所有任务的共同要求

1. **先确认 issue 仍开放。** 阅读 issue 正文和评论；若已关闭、作者确认已解决或被其他 issue/PR 取代，标为“已解决/已替代”，附证据后停止该项核实。
2. **对照两个代码库。** 上游结论以 `Diorser/LiteMonitor` 当前代码为准；再检查本地 LiteMonitor++ fork 是否继承同一代码路径或已有独立实现。不要把上游问题直接推断为 fork 缺陷。
3. **以证据支撑判断。** 记录版本/commit、文件与方法位置、数据流或窗口生命周期；硬件/系统条件无法复现时明确写“未复现”，不可写成已确认缺陷。
4. **只调查，不改源码。** 不提交修复、不改设置/用户数据。必要时可以增加临时、可丢弃的本地诊断脚本，但不要把它混入产品代码。
5. **统一交付格式。** 每个 issue 给出：当前状态、问题摘要、上游代码证据、fork 对照、复现条件/验证步骤、结论（确认 / 部分确认 / 未复现 / 信息不足 / 已解决或替代）、风险与建议下一步。提供文件路径和行号或 GitHub 链接。

---

## TASK-1：性能与资源占用

**目标**：判断性能反馈是否存在可定位的高 CPU、内存增长或界面阻塞路径，并区分当前版本问题与旧版本遗留。

**Issues**

- [#416](https://github.com/Diorser/LiteMonitor/issues/416) — LiteMonitor 导致 Windows System 进程 CPU 使用率上升
- [#437](https://github.com/Diorser/LiteMonitor/issues/437) — 设置 UI 操作卡顿
- [#88](https://github.com/Diorser/LiteMonitor/issues/88) — v1.2.0 面板设置卡顿
- [#78](https://github.com/Diorser/LiteMonitor/issues/78) — 内存占用高
- [#35](https://github.com/Diorser/LiteMonitor/issues/35) — 疑似内存泄漏
- [#96](https://github.com/Diorser/LiteMonitor/issues/96) — 网络检测运行一段时间后数据卡住（作为刷新阻塞检查，不计入纯性能问题）

**核实重点**

- 读取 issue 的环境、版本、评论和作者是否给出持续时间/资源曲线；确认旧版本反馈是否仍有当前版本复现证据。
- 追踪硬件轮询、传感器更新、网络采集、UI 刷新和日志/历史写入是否可能造成高频 CPU 或 UI 线程阻塞。
- 若能运行应用，记录空闲/稳定负载下 CPU、工作集/私有字节及 UI 响应；长时间运行观察内存是否持续增长。不可用单次内存值判定泄漏。
- 解释 #416 的 `System` 进程归因是否可能来自驱动/硬件轮询，而不是直接归因于主进程。

**交付**：逐 issue 结论；如无法复现，给出最低限度复测环境和需要用户补充的数据。

---

## TASK-2：集显/多 GPU 与 GPU 传感器数据

**目标**：整理相似报告的共同根因，确认设备发现、传感器注册、首次更新和显卡切换流程是否可靠。

**Issues**

- [#603](https://github.com/Diorser/LiteMonitor/issues/603) — 核显 GPU 使用率为 0
- [#596](https://github.com/Diorser/LiteMonitor/issues/596) — Intel Ultra 5 225H 使用率需切换首选 GPU 才恢复
- [#592](https://github.com/Diorser/LiteMonitor/issues/592) — Intel B580 使用率不显示
- [#569](https://github.com/Diorser/LiteMonitor/issues/569) — 集成显卡使用率读取不到
- [#521](https://github.com/Diorser/LiteMonitor/issues/521) — U5 255 核显使用率异常
- [#450](https://github.com/Diorser/LiteMonitor/issues/450) — B580 需手动切换首选显卡才能显示
- [#434](https://github.com/Diorser/LiteMonitor/issues/434) — GPU 使用率不显示
- [#497](https://github.com/Diorser/LiteMonitor/issues/497) — AMD 9070 GRE 温度/频率/功耗数据缺失
- [#499](https://github.com/Diorser/LiteMonitor/issues/499) — 任务管理器显示独显占用为 0，但 LiteMonitor 仍显示显存占用

**核实重点**

- 将 issue 按 Intel 核显、Intel 独显、AMD GPU、多 GPU/混合显卡分类，找出同类问题和重复报告。
- 检查上游及 fork 的 GPU 枚举、传感器 Map 建立/重建、`Update()` 时机、GPU 首选项持久化和切换后的刷新。
- 判断 0、缺字段、旧值未清空分别对应什么代码路径；确认没有把“不支持的传感器”误判为读取失败。
- 记录可执行的验证组合：冷启动、自动选择、手动选择、切回自动、睡眠唤醒（若适用）。

**交付**：一张传感器问题矩阵（Issue × GPU 型号/状态/现象/代码路径），并指出哪些报告可由同一潜在根因解释。

---

## TASK-3：崩溃、异常与硬件采集健壮性

**目标**：判断已报告的崩溃/异常是否能映射到明确的空值、原生调用、传感器冲突或设备生命周期路径。

**Issues**

- [#502](https://github.com/Diorser/LiteMonitor/issues/502) — NVML `NvmlDeviceGetPowerUsage` AccessViolation
- [#579](https://github.com/Diorser/LiteMonitor/issues/579) — 闪退并伴随网络/GPU 数据缺失
- [#290](https://github.com/Diorser/LiteMonitor/issues/290) — 笔记本核显/独显切换时闪退
- [#582](https://github.com/Diorser/LiteMonitor/issues/582) — 读取磁盘 SMART 属性时空引用导致崩溃
- [#574](https://github.com/Diorser/LiteMonitor/issues/574) — `Parameter is not valid` 异常
- [#601](https://github.com/Diorser/LiteMonitor/issues/601) — 与 HWiNFO 同时运行冲突
- [#577](https://github.com/Diorser/LiteMonitor/issues/577) — 多个机箱风扇只显示一个
- [#514](https://github.com/Diorser/LiteMonitor/issues/514) — CPU/GPU 风扇显示 0 RPM
- [#538](https://github.com/Diorser/LiteMonitor/issues/538) — 同型号 SSD 只显示第一块
- [#466](https://github.com/Diorser/LiteMonitor/issues/466) — 占用可移动磁盘导致无法安全移除
- [#245](https://github.com/Diorser/LiteMonitor/issues/245) — 报告影响 Keil/DAPLink USB 设备识别

**核实重点**

- 追查 native interop/NVML 生命周期、异常边界、磁盘/传感器身份键以及资源句柄释放。
- 区分“程序崩溃”“单项读数缺失”“驱动/设备被占用”三种影响等级。
- 检查与其他硬件监控工具并行时是否有共享驱动/独占访问约束，以及程序退出后是否释放句柄。
- 以 issue 讨论中的日志、转储或复现说明为先；缺少材料时列出需要的最小诊断信息。

**交付**：按崩溃风险、设备数据错误、资源/设备占用分组；对每个高风险项说明代码证据与复现可行性。

---

## TASK-4：窗口、任务栏和多显示器生命周期

**目标**：核实窗口层级、任务栏嵌入和显示器拓扑变化后，是否存在不能自动恢复或布局计算不一致的问题。

**Issues**

- [#612](https://github.com/Diorser/LiteMonitor/issues/612) — Always-on-top 间歇失效
- [#599](https://github.com/Diorser/LiteMonitor/issues/599) — 显卡驱动更新后监控条被合成层遮挡
- [#553](https://github.com/Diorser/LiteMonitor/issues/553) — 锁屏/关闭显示器后任务栏数据丢失
- [#583](https://github.com/Diorser/LiteMonitor/issues/583) — 远程桌面后副屏任务栏面板位置错误
- [#573](https://github.com/Diorser/LiteMonitor/issues/573) — 多屏启动后面板位置不固定
- [#568](https://github.com/Diorser/LiteMonitor/issues/568) — 改变任务栏位置后位置错乱/重叠
- [#539](https://github.com/Diorser/LiteMonitor/issues/539) — 纯任务栏模式切换分辨率后位置/布局跳动

**核实重点**

- 梳理窗口创建、父子关系、Z-order、句柄重建、Explorer/显示器变化通知、DPI 与坐标空间转换。
- 说明每类故障触发事件及现有恢复机制；针对驱动热更新、锁屏唤醒、RDP 接入/断开、分辨率变化设计验证步骤。
- 对照 fork 的任务栏窗口实现，检查是否已有独立修复或更强的恢复策略。

**交付**：生命周期事件表（事件 → 系统通知 → 代码响应 → 预期恢复），逐 issue 标注是否覆盖。

---

## TASK-5：Web、插件与数据刷新

**目标**：核实协议兼容、外部数据源失败恢复和插件解析边界导致的页面无数据/插件错误。

**Issues**

- [#589](https://github.com/Diorser/LiteMonitor/issues/589) — 反向代理规范化 WebSocket 头大小写后握手失败
- [#580](https://github.com/Diorser/LiteMonitor/issues/580) — Cloudflare Tunnel 代理访问 Web 异常
- [#611](https://github.com/Diorser/LiteMonitor/issues/611) — 倒计时插件 API 返回 null 后只显示错误标记
- [#96](https://github.com/Diorser/LiteMonitor/issues/96) — 网络监测一段时间后数据卡住
- [#287](https://github.com/Diorser/LiteMonitor/issues/287) — 切换 Wi-Fi 后局域网 IP 未更新
- [#235](https://github.com/Diorser/LiteMonitor/issues/235) — 启动时网络节点不可达，恢复连接后插件数据未刷新

**核实重点**

- Web：检查 HTTP 头字段解析是否符合大小写不敏感要求；比较直连与反向代理的握手、超时和重连路径。
- 插件：检查 null/缺字段、提取链中间节点为空、数据源暂时失败及后续恢复时的容错行为。
- 网络刷新：检查接口/网卡变化后缓存、轮询任务和 UI 更新是否仍运行；区分“停止采集”与“采集有值但未刷新显示”。
- 检查 fork 的 WebServer 与声明式插件执行器是否已偏离上游；分别给出代码映射。

**交付**：每项至少给出一个失败注入或协议级验证办法，以及恢复成功的判据。

---

## 汇总回报模板

```text
任务单：TASK-N
检查日期：
检查的上游/fork commit：

| Issue | 当前是否 open | 上游结论 | fork 结论 | 证据位置 | 复现/验证结果 | 下一步 |
|---|---|---|---|---|---|---|

共性发现：
未能验证及原因：
建议优先级：
```
