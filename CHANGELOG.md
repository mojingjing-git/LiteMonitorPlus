# LiteMonitor++ 变更日志 (Changelog)

本项目基于开源项目 [LiteMonitor](https://github.com/Diorser/LiteMonitor)（原作者 Diorser，MIT 协议）派生。  
本文档完整记录 LiteMonitor++ 相比上游（以 v1.3.7 基线为准）的架构重构、功能新增、底层加固与上游 Issue 修复。  
格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 原则。

---

## [未发布 / 当前开发中] (Unreleased)

### 🎨 主题工作室（Theme Studio）全新重做
- **现代化视觉与原生交互**：采用苹果风格三栏工作区，保留 Windows 原生窗口操作（最小化、最大化、关闭）。
- **双管线可视化调参**：同时支持经典 GDI+ 属性检查器与 DirectX 11 / Direct2D GPU 液态玻璃材质参数调优。
- **同源真实渲染预览**：集成竖向悬浮窗、横向布局与任务栏模式的同源实时预览，编辑所见即所得。
- **安全事务与导出服务**：支持补丁式保存、事务历史回滚、恢复草稿与一键打包导出，符合主题市场投稿规范。

### ⚙️ 架构解耦与可靠性治理
- **Plan A：硬件采样子进程隔离（`LiteMonitor.HardwareHost`）**：
  - 新增独立采集进程与数据契约库 `LiteMonitor.Contracts`。
  - 将底层 `LibreHardwareMonitorLib`、PawnIO、NVML 驱动及高危硬件传感器采样完全移至子进程，主程序通过本地命名管道（Named Pipe）消费不可变快照。
  - **故障完全隔离**：底层驱动死锁、蓝屏边缘或 Native 崩溃时，主程序 UI、任务栏与托盘依然保持响应，支持一键热重载与有界重试。
- **Plan B：应用分层架构解耦**：
  - 解除 UI 层对硬件传感器、全局 `Settings` 单例的可变强耦合，引入数据投影模型与模块隔离门禁。

### 🖥️ UI 视觉与交互优化（2026-10-28）
- **新出厂默认主题 RefinedDark**：精致暗色风格（渐变卡片底、细描边、垂直渐变进度条），替代 DarkFlat_Classic 成为出厂默认与插件市场卸载回退。
- **新增 CyberHud_Neon 主题**：赛博 HUD 风格（切角外框、扫描线、网格、霓虹辉光）。
- **LiquidGlass_GPU 材质参数打磨**：折射 1.8 / 模糊 14 / 表面不透明度 0.085 / 阴影 0.42，打包基线测试同步更新。
- **卡片渲染增强**：分组卡片底改为垂直微渐变 + 顶部 1px 内侧高光（`UIRenderer.DrawGroupFill`），GPU 回退外观同源受益。
- **渲染路线交互优化**：GPU 材质渲染出厂默认开启；原独立「渲染路线」设置页撤销，GPU 开关并入主面板页、路线状态行随主题设置展示、诊断卡迁至「系统与硬件」页；GPU 启动手动重试按钮重放到主面板页。

### 🧰 系统优化与工程卫生（2026-10-27 / 10-28）
- **系统优化批次**：规则化报警（`Core/Alerts`）、情景模式（`Core/Scenarios`）、趋势统计（`Core/Trends`）、诊断中心（`src/Diagnostics`）、整机功耗、插件安全体系（SSRF 地址策略、市场投稿校验），并新增 `LiteMonitor.Cli` 命令行工程。
- **防御编码修复**：设置原子写入补 fsync、持久化失败记日志、主题名消毒接入全部文件服务入口、任务栏恢复吞异常加日志与重试、`Settings` 单例加载加锁。
- **工程卫生**：清理孤儿资源与过期脚本、`.gitignore` 修正（`/NuGet/`、`/.agents/`）、移除幽灵排除条目。

### 🩺 修复
- `LiteMonitor.Cli`（THEME_MARKET_CLI 共享编译）构建失败：`ThemeMarketValidator` 以条件编译隔离主程序专用 API。

---

## [1.0.0] - 2026-10-26 (Fork 重大里程碑交付)

### 🚀 核心新增特性 (Added)

#### 1. 品牌解耦与更新基础设施
- **去中心化与品牌独立**：程序集、命名空间与自制专属应用图标全面重命名为 LiteMonitor++，完全解绑上游私有服务器。
- **安全更新系统**：独立自动更新器 `LiteMonitor.Updater`，支持国内镜像与 GitHub 双源；引入下载产物 SHA-256 完整性哈希校验，杜绝中间人攻击。

#### 2. 多独立显卡监控（Multi-GPU Telemetry）
- **真·多显卡支持**：支持同时监控多张独立 GPU（NVIDIA / AMD / Intel），各项指标（核心负载、显存占用、温度、频率、功耗、风扇）分卡独立罗列。
- **设备身份与持久化**：引入跨会话稳定的设备指纹，拔插显卡或系统更新后不丢失监控项配置与自定义排序。

#### 3. GPU 液态玻璃渲染管线（Direct3D 11 & Direct2D）
- **真实折射材质**：通过 `Vortice.Windows` 引入 Direct3D 11 与 Direct2D 硬件加速层，渲染高光、景深与边缘折射的高级玻璃质感。
- **实时桌面背景穿透**：基于 Windows Desktop Duplication 与 `WDA_EXCLUDEFROMCAPTURE` API 实现无递归伪影的实时桌面模糊背景（`GpuLiveBackground`）。
- **平滑自适应回退**：内置三级渲染管线动态自检（GDI+ 兼容路线、GPU 固定材质、GPU 实时桌面背景），显卡不支持或进入安全模式时无感回退。

#### 4. 高 DPI 架构（Per-Monitor-V2）
- 全链路升级为 **Per-Monitor-V2** 物理像素感知。
- 解耦字体测量与 `Graphics` 瞬态上下文，修复跨不同 DPI 屏幕拖拽时界面整体发虚、字体测量异常与 GDI 对象泄露。

#### 5. 任务栏模式与桌面交互
- **任务栏原生嵌入**：支持将监控看板嵌入 Windows 任务栏通知区，自动适配任务栏高度与系统暗色/亮色风格。
- **生命周期韧性监控**：通过 Win32 Hook 监听 Shell 状态，在 Windows 资源管理器（Explorer）崩溃重启后自动重连并恢复附着。
- **桌面贴边与交互防呆**：靠边自动贴边隐藏、多屏幕边缘精确判断、鼠标穿透模式、防止拖拽移出可视区域。

#### 6. 插件与主题生态
- **声明式无代码插件**：提供安全沙箱化的 JSON 插件机制，原生支持天气预报、加密货币、实时汇率、代理延迟测试等扩展。
- **市场安全事务（WAL）**：插件与主题下载引入写前日志与严格路径隔离，杜绝路径遍历（Path Traversal）安全漏洞。

#### 7. Web 看板与网络诊断
- **Web 实时监控大屏**：内置轻量 Web 服务器，局域网手机/浏览器可直接访问监控看板。
- **网络测速与历史统计**：内置网络测速模块，每日上传/下载流量历史趋势图表。

---

### 🛡️ 上游 Bug 与性能缺陷彻底修复 (Fixed)

本项目系统化验证并修复了原上游 38 项处于 Open 状态的痛点 Bug：

| 模块 | Issue / 编号 | 核心修复措施与成效 |
|---|---|---|
| **性能** | [#416](https://github.com/Diorser/LiteMonitor/issues/416) | 重构采样时钟，消除后台无休止自旋导致的 Windows `System` 进程 CPU 占用高问题 |
| **性能** | [#437](https://github.com/Diorser/LiteMonitor/issues/437) / [#88](https://github.com/Diorser/LiteMonitor/issues/88) | 优化设置面板树形构建与控件初始化逻辑，消除界面切换严重卡顿 |
| **性能** | [#78](https://github.com/Diorser/LiteMonitor/issues/78) / [#35](https://github.com/Diorser/LiteMonitor/issues/35) | 修复主题渲染时的字体重复分配与句柄未回收，消除常驻内存渐进式泄漏 |
| **磁盘** | [#466](https://github.com/Diorser/LiteMonitor/issues/466) / [#601](https://github.com/Diorser/LiteMonitor/issues/601) | 新增 `DiskProbePolicy` 白名单与止血模式，消除磁盘 SMART 探测引发的界面卡死及与 HWiNFO 互斥死锁 |
| **磁盘** | [#582](https://github.com/Diorser/LiteMonitor/issues/582) | 增加空值安全护栏，彻底根治驱动获取不到 SMART 属性时的空引用异常（NullReferenceException）崩溃 |
| **网络** | [#96](https://github.com/Diorser/LiteMonitor/issues/96) | 重构网络样本时效机制（`NetworkSampleFreshness`），解决网络监测运行一段时间后数据冻结、拔卡/换卡出现负流量的问题 |
| **显卡** | [#603](https://github.com/Diorser/LiteMonitor/issues/603) / [#434](https://github.com/Diorser/LiteMonitor/issues/434) | 修复多 GPU 初始化竞争导致的显卡使用率恒为 0% 问题 |
| **显卡** | [#596](https://github.com/Diorser/LiteMonitor/issues/596) / [#521](https://github.com/Diorser/LiteMonitor/issues/521) | 适配 Intel Core Ultra（Meteor Lake / Arrow Lake）系列核显传感器映射与动态时钟读取 |
| **显卡** | [#499](https://github.com/Diorser/LiteMonitor/issues/499) | 修复独显空闲但程序仍显示旧显存读数的问题，建立显存释放状态同步机制 |
| **Web** | [#580](https://github.com/Diorser/LiteMonitor/issues/580) / F01 | 重构 WebSocket 升级握手协议，严格遵循 RFC 6455 规范，修复 Nginx/Caddy 等反向代理穿透失败 |
| **窗口** | F04 / F05 | 修复全屏游戏退出、DPI 动态调整后主悬浮窗 Z 序混乱与任务栏丢失问题 |
| **窗口** | `autohide-loss` | 修复副屏靠边自动隐藏后，鼠标无法再次唤醒呼出的窗口位置计算缺陷 |
