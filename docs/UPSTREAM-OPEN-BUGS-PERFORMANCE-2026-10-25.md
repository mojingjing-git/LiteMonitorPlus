# 原上游未关闭的 Bug 与性能反馈

核对日期：2026-10-25  
来源：[Diorser/LiteMonitor Issues](https://github.com/Diorser/LiteMonitor/issues)  
筛选方式：仅纳入 GitHub 当前 `is:issue is:open` 的 issue；已关闭/已解决项不列。将标题或描述是功能建议、使用咨询的条目排除。相似的硬件反馈按问题簇合并，但保留 issue 编号，避免重复罗列。

> 这是按“用户报告的问题”整理的清单，不代表维护者已确认根因，也不表示这些问题在 LiteMonitor++ 中仍然存在。需逐项对照 fork 当前代码和复现环境。

## 性能与资源占用

| 优先级 | Issue | 用户报告 |
|---|---|---|
| 高 | [#416](https://github.com/Diorser/LiteMonitor/issues/416) | 软件运行后 Windows `System` 进程 CPU 使用率升高。直接性能回归报告。 |
| 中 | [#437](https://github.com/Diorser/LiteMonitor/issues/437) | 设置界面操作卡顿；报告同时提到希望增加全局暗色模式，后者是功能请求，不在此处统计。 |
| 中 | [#88](https://github.com/Diorser/LiteMonitor/issues/88) | v1.2.0 面板设置明显卡顿。旧版本报告仍处于 open，需先确认当前版本能否复现。 |
| 中 | [#78](https://github.com/Diorser/LiteMonitor/issues/78) | 内存占用偏高；标题没有给出版本、基线或增长曲线，属于待复核的资源问题。 |
| 中 | [#35](https://github.com/Diorser/LiteMonitor/issues/35) | 疑似内存泄漏；需通过长时间运行的内存曲线验证，不能仅凭标题认定为泄漏。 |

**性能类结论：**明确提到 CPU、内存或 UI 响应问题的 open 反馈共 5 条。#416 最直接；#78/#35 的证据描述较少，优先级应低于可稳定复现的 CPU 或界面卡顿问题。#96 是网络监测数据运行一段时间后停止刷新，归入功能性故障，不计作性能问题。

## 仍开放的 Bug / 功能故障

### 硬件传感器、读数错误或数据缺失

- **CPU/GPU 使用率和传感器初始化**：[ #607 CPU 项检测不到](https://github.com/Diorser/LiteMonitor/issues/607)、[#603 GPU 使用率为 0](https://github.com/Diorser/LiteMonitor/issues/603)、[#596 Intel Ultra 5 225H GPU 使用率启动后异常](https://github.com/Diorser/LiteMonitor/issues/596)、[#592 Intel B580 使用率无法显示](https://github.com/Diorser/LiteMonitor/issues/592)、[#569 集成显卡 GPU 使用率无法读取](https://github.com/Diorser/LiteMonitor/issues/569)、[#521 U5 255 核显使用率错误](https://github.com/Diorser/LiteMonitor/issues/521)、[#450 Intel B580 需切换首选显卡才能恢复](https://github.com/Diorser/LiteMonitor/issues/450)、[#434 GPU 使用率不显示](https://github.com/Diorser/LiteMonitor/issues/434)。多个反馈指向集显/多 GPU 传感器发现、更新顺序或选择状态，建议作为一个复现矩阵处理。
- **温度、频率、电压、功耗读数不正确/缺失**：[#578 CPU 频率显示 8.2G](https://github.com/Diorser/LiteMonitor/issues/578)、[#559 主板温控数值异常](https://github.com/Diorser/LiteMonitor/issues/559)、[#584 CPU 使用率与任务管理器差异大](https://github.com/Diorser/LiteMonitor/issues/584)、[#531 CPU 使用率约为任务管理器两倍](https://github.com/Diorser/LiteMonitor/issues/531)、[#511 CPU 温度不准](https://github.com/Diorser/LiteMonitor/issues/511)、[#507 Intel Core Ultra 7 356H 无 CPU 温度](https://github.com/Diorser/LiteMonitor/issues/507)、[#497 AMD 9070 GRE 多项 GPU 数据缺失](https://github.com/Diorser/LiteMonitor/issues/497)、[#499 任务管理器独显占用为 0 但程序仍显示显存占用](https://github.com/Diorser/LiteMonitor/issues/499)、[#514 CPU/GPU 风扇显示 0 RPM](https://github.com/Diorser/LiteMonitor/issues/514)。部分是硬件适配报告，需区分传感器本身不可用与映射/单位错误。
- **风扇、磁盘及硬件兼容**：[#577 多个机箱风扇只显示一个](https://github.com/Diorser/LiteMonitor/issues/577)、[#582 SMART 属性空引用导致程序崩溃](https://github.com/Diorser/LiteMonitor/issues/582)、[#601 与 HWiNFO 冲突](https://github.com/Diorser/LiteMonitor/issues/601)。

### 崩溃、卡死和后台恢复

- [#502](https://github.com/Diorser/LiteMonitor/issues/502)：任务栏模式运行时 `NvmlDeviceGetPowerUsage` 引发 AccessViolation 崩溃。
- [#579](https://github.com/Diorser/LiteMonitor/issues/579)：报告闪退，并伴随流量和 GPU 数据缺失。
- [#290](https://github.com/Diorser/LiteMonitor/issues/290)：笔记本在核显/独显切换时闪退。
- [#96](https://github.com/Diorser/LiteMonitor/issues/96)：网络检测运行一段时间后数据卡住。
- [#611](https://github.com/Diorser/LiteMonitor/issues/611)：假期倒计时插件接口返回空值后只显示错误标记，缺少可用降级结果。
- [#574](https://github.com/Diorser/LiteMonitor/issues/574)：运行中出现 `Parameter is not valid` 异常。

### 窗口、任务栏和多显示器故障

- [#612](https://github.com/Diorser/LiteMonitor/issues/612)：“总是置顶”间歇失效，需要手动关闭再打开恢复。
- [#599](https://github.com/Diorser/LiteMonitor/issues/599)：显卡驱动更新后任务栏监控条被系统合成层遮住，程序仍在运行。
- [#553](https://github.com/Diorser/LiteMonitor/issues/553)：多屏环境锁屏/关闭显示器后任务栏数据丢失。
- [#583](https://github.com/Diorser/LiteMonitor/issues/583)、[#573](https://github.com/Diorser/LiteMonitor/issues/573)：远程桌面或多屏启动后任务栏面板位置错误/不固定。
- [#568](https://github.com/Diorser/LiteMonitor/issues/568)、[#539](https://github.com/Diorser/LiteMonitor/issues/539)：任务栏位置或显示分辨率变化后出现重叠、位置/布局跳动。
- [#538](https://github.com/Diorser/LiteMonitor/issues/538)：两块同型号 SSD 只显示第一块。

### 网络/Web 和外围设备

- [#589](https://github.com/Diorser/LiteMonitor/issues/589)：反向代理改变 `Sec-WebSocket-Key` 大小写后，WebSocket 握手失败，网页无实时数据。
- [#580](https://github.com/Diorser/LiteMonitor/issues/580)：Cloudflare Tunnel 代理访问 Web 页面异常。
- [#287](https://github.com/Diorser/LiteMonitor/issues/287)：切换 Wi-Fi 后局域网 IP 未更新。
- [#466](https://github.com/Diorser/LiteMonitor/issues/466)：程序占用可移动磁盘，导致设备无法安全移除。
- [#245](https://github.com/Diorser/LiteMonitor/issues/245)：报告软件影响 USB 设备识别（Keil/DAPLink）。

## 建议先处理的复核顺序

1. **崩溃和资源占用**：#502、#579、#290、#416、#35。优先取得日志、版本与硬件环境，区分可稳定复现的回归和单机环境问题。
2. **集显/多 GPU 数据缺失**：#603、#596、#592、#569、#521、#450、#434。按 GPU 型号、首轮传感器更新时间、自动/手动选择、重启后状态建立矩阵。
3. **窗口恢复与多屏状态**：#612、#599、#553、#583、#573、#568、#539。覆盖驱动重置、锁屏唤醒、显示器拓扑变化和远程桌面连接。
4. **硬件读取健壮性及 Web 兼容**：#582、#601、#589、#580。重点检查异常隔离、协议字段解析和外部工具并行运行。

## 不纳入范围

已关闭 issue 不计入；纯功能建议（例如新增 GPU 热点温度、总功耗、Linux 版本、显示选项）不计入；已关闭后又有相似新反馈时，只列仍开放的新 issue。GitHub 上游状态持续变化，后续复查应从 [Open Issues 筛选页](https://github.com/Diorser/LiteMonitor/issues?q=is%3Aissue%20is%3Aopen)重新确认。
