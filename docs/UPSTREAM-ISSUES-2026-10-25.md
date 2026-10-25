# LiteMonitor 项目梳理与原上游 Issue 摘要

调研日期：2026-10-25  
原上游： [Diorser/LiteMonitor](https://github.com/Diorser/LiteMonitor)  
当前 fork： [mojingjing-git/LiteMonitorPlus](https://github.com/mojingjing-git/LiteMonitorPlus)

> 本文是调研快照。Issue 的标题、状态和讨论会持续变化；上游状态不代表 LiteMonitor++ 中同一问题仍然存在。

## 项目定位

LiteMonitor 是一个面向 Windows 的桌面硬件监控工具。上游以 WinForms / .NET 8 为基础，使用 LibreHardwareMonitor 采集 CPU、GPU、内存、磁盘、网络、电池和传感器数据，并提供桌面悬浮窗与任务栏显示。它的产品重点是低干扰常驻监控、主题外观、可定制的指标与插件扩展。

当前仓库是 LiteMonitor++，README 与 NOTICE 明确说明它基于 Diorser 的 MIT 项目修改，且与原作者无官方关联。仓库配置的 `upstream` remote 指向原始仓库，`origin` 指向 LiteMonitorPlus。当前检出 `master` 相对 `origin/master` ahead 7 commits；工作区有一处用户未提交的主题市场计划文档改动，调研没有改动它。

## 当前 fork 的代码轮廓

| 区域 | 位置 | 职责 |
|---|---|---|
| 主程序与设置 | `src/System/`, `src/Core/` | 启动、设置持久化、硬件数据、指标模型、历史记录、语言和主题管理 |
| 传感器与系统集成 | `src/System/HardwareServices/`, `src/System/WebServer/` | LibreHardwareMonitor 传感器映射、系统计数器、网络与 Web 服务 |
| UI | `src/UI/` | 透明悬浮窗、任务栏窗口、横向布局、设置页、历史图表 |
| 渲染 | `src/Rendering/`, `resources/shaders/` | GDI+ 路径及可选 Direct3D 11 / DXGI GPU 材质渲染 |
| 插件 | `src/Plugins/`, `resources/plugins/` | JSON 声明式数据源、解析和展示；市场插件不运行第三方脚本 |
| 更新器 | `LiteMonitor.Updater/` | 独立更新器、文件事务、摘要校验及旧版更新桥接 |
| 主题与本地化 | `resources/themes/`, `resources/lang/` | JSON 主题和多语言资源 |
| 验证设施 | `tests/`, `tools/` | 单元测试、渲染探针、窗口行为和性能探针 |

项目目前不只是上游的轻量监控窗：fork 已发展为一套自定义皮肤/主题系统、插件市场、网络流量历史和速度测试、独立更新器，以及用户显式启用的 GPU 材质/实时桌面背景路线。主应用目标框架为 `net8.0-windows`、WinForms、x64；GPU 路线使用 Vortice Direct3D 组件，硬件采集使用 LibreHardwareMonitor。

## Issue 快照与范围

GitHub 原上游的 Issues 页面在调研时显示 **451 个 open issues**，最新编号到 **#615**。上游 Issues 页面默认按最新创建时间展示；下表是从近期条目与本地既有上游调研中挑出的代表性问题，按主题归类，不是对 451 条 open issue 的逐条复述。GitHub 搜索接口返回的 issue 状态字段为空，因此状态以链接指向的上游页面实时状态为准。

- [上游所有 Open Issues](https://github.com/Diorser/LiteMonitor/issues?q=is%3Aissue%20is%3Aopen)
- [上游所有 Closed Issues](https://github.com/Diorser/LiteMonitor/issues?q=is%3Aissue%20is%3Aclosed)
- [上游 Pull Requests](https://github.com/Diorser/LiteMonitor/pulls)

## 近期上游问题（2026-10 快照）

### 传感器与硬件兼容

- [#607 CPU 项检测不到](https://github.com/Diorser/LiteMonitor/issues/607)：CPU 温度/监控项不可用。
- [#603 GPU 使用率为 0](https://github.com/Diorser/LiteMonitor/issues/603)：小米 Book 14 Pro 核显案例。
- [#596 Intel Ultra 5 225H GPU 使用率重启后归零](https://github.com/Diorser/LiteMonitor/issues/596)：切换自动/指定显卡可暂时恢复，疑似初始化/设备选择状态问题。
- [#592 Intel B580 使用率无法显示](https://github.com/Diorser/LiteMonitor/issues/592)。
- [#569 集成显卡使用率无法读取](https://github.com/Diorser/LiteMonitor/issues/569)，与 [#521](https://github.com/Diorser/LiteMonitor/issues/521)、[#434](https://github.com/Diorser/LiteMonitor/issues/434) 等类似反馈相关。
- [#582 SMART 属性读取空引用导致崩溃](https://github.com/Diorser/LiteMonitor/issues/582)。
- [#577 多个机箱风扇只显示一个](https://github.com/Diorser/LiteMonitor/issues/577)。
- [#559 主板温控数值有误](https://github.com/Diorser/LiteMonitor/issues/559)；[#578 CPU 频率异常](https://github.com/Diorser/LiteMonitor/issues/578)。
- [#601 与 HWiNFO 冲突](https://github.com/Diorser/LiteMonitor/issues/601)：需要结合传感器驱动/共享访问方式判断。

### 窗口、任务栏、多屏和 DPI

- [#612 “总是置顶”间歇失效](https://github.com/Diorser/LiteMonitor/issues/612)。
- [#599 显卡驱动热更新后任务栏监控条被合成层盖住](https://github.com/Diorser/LiteMonitor/issues/599)：进程仍运行，窗口层级恢复是重点。
- [#598 希望支持任务栏放在左侧](https://github.com/Diorser/LiteMonitor/issues/598)。
- [#583 远程桌面后副屏任务栏面板位置错误](https://github.com/Diorser/LiteMonitor/issues/583)；[#573 多屏开机后面板位置不固定](https://github.com/Diorser/LiteMonitor/issues/573)。
- [#568 任务栏位置改变后显示重叠](https://github.com/Diorser/LiteMonitor/issues/568)；[#539 切换分辨率后纯任务栏布局错乱](https://github.com/Diorser/LiteMonitor/issues/539)。
- [#608 希望增加任务栏字体与间距设置](https://github.com/Diorser/LiteMonitor/issues/608)，包括小任务栏下文字垂直裁切/不居中。
- [#605 横条模式需要透明背景主题](https://github.com/Diorser/LiteMonitor/issues/605)；[#590 主窗口固定位置选项](https://github.com/Diorser/LiteMonitor/issues/590)。

### 插件、Web 与数据展示

- [#611 放假倒计时插件数据源失效](https://github.com/Diorser/LiteMonitor/issues/611)：接口返回 `null` 后任务栏只显示错误标记。
- [#597 插件支持不发请求、只处理数据](https://github.com/Diorser/LiteMonitor/issues/597)：扩展插件表达能力的需求。
- [#589 反向代理下 WebSocket 握手头大小写导致网页无数据](https://github.com/Diorser/LiteMonitor/issues/589)：报告指出 WebSocket 头字段匹配区分大小写。
- [#580 Cloudflare Tunnel 代理访问网页异常](https://github.com/Diorser/LiteMonitor/issues/580)。
- [#585 Web 页面增加历史记录展示](https://github.com/Diorser/LiteMonitor/issues/585)。
- [#610 排除指定内网 IP 流量](https://github.com/Diorser/LiteMonitor/issues/610)：现有 SMB 排除不能覆盖局域网音频流量。
- [#584 CPU 利用率与任务管理器差异](https://github.com/Diorser/LiteMonitor/issues/584)：核心是统计口径、聚合方式和用户解释。

### 产品能力与兼容边界

- [#615 倒计时插件/展示需求](https://github.com/Diorser/LiteMonitor/issues/615)、[#614 教室里查看时间](https://github.com/Diorser/LiteMonitor/issues/614)。
- [#610 指定 IP 流量排除](https://github.com/Diorser/LiteMonitor/issues/610)。
- [#591 总功耗用于估算电费](https://github.com/Diorser/LiteMonitor/issues/591)；[#567 增加 GPU 热点温度](https://github.com/Diorser/LiteMonitor/issues/567)。
- [#595 老 Windows 10 兼容/WinRing0 支持建议](https://github.com/Diorser/LiteMonitor/issues/595)：与 PawnIO 的最低系统版本要求有关。
- [#546 Linux 版本建议](https://github.com/Diorser/LiteMonitor/issues/546)：与当前 Windows/WinForms 产品边界差异较大。

## 适合 fork 优先复核的事项

1. **集显/多 GPU 使用率初始化**：连续出现 #603、#596、#592、#569、#521 一类反馈。应区分“传感器不存在”“首次 Update 前未注册传感器”“GPU 选择持久化/启动顺序”几类根因。当前 fork 已有额外 GPU 枚举与注册逻辑，但不能仅凭上游 issue 标题断言修复完整。
2. **窗口生命周期与层级恢复**：#612、#599、#583、#573、#539 涉及置顶状态、任务栏子窗口、显示器拓扑和 Explorer/驱动重置。fork 有独立任务栏窗口和多显示器处理，需针对这些生命周期事件做回归核对。
3. **传感器读取健壮性与驱动冲突**：#582、#601 以及温度/风扇异常反馈，适合审查空值边界、传感器唯一标识、共享驱动冲突和错误降级。
4. **插件可恢复性**：#611、#597 指向外部 API 不稳定和数据转换能力。fork 已有声明式插件系统与市场，需核对空值链式解析、超时/错误呈现和无需请求的数据源表达能力。
5. **WebSocket 协议兼容**：#589 的报告提供了清晰复现方向，可对照 fork Web 服务头解析实现检查大小写不敏感要求。

上述是工程复核优先级建议，不等同于确认 fork 中存在同一缺陷。特别是本仓库与上游分叉较深，任何 issue 都应先映射到 fork 当前代码和实际复现条件。

## 本地已有的深入 issue/PR 调研

仓库还保留了 [2026-10-20 上游 issue 与 PR 调研](./UPSTREAM-FEEDBACK-2026-10-20.md)，包含更多历史 issue、未合并 PR、fork 受影响情况和优先级记录。本文补充了 2026-10-25 的近期 issue 索引与代码结构摘要；若要逐条追踪 issue 状态，应以 GitHub 上游链接为准。
