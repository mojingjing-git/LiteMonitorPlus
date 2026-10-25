# B0/I0 基线盘点

- 日期：2026-10-26。
- 对应阶段：[Plan B](2026-10-26-architecture-decoupling-plan.md) 的 B0、[Plan A](2026-10-26-hardware-process-isolation-plan.md) 的 I0（两计划同批批准，盘点合一，避免重复劳动）。
- 盘点基线：master `9da07af`（W3 合并后：F08b 含接线、F14b 主题引擎、F06 Z序；`MIN_TEST_TOTAL` 2233）。
- 状态：**静态盘点完成**；行为 fixture 与性能基线为待办（见 §7），不阻塞 B1/I1 设计启动。

## 1. 调用点盘点

### 1.1 采集创建与所有权

| 事实 | 位置 | 影响 |
|---|---|---|
| UI 直接构造采集器 | `src/UI/UIController.cs:134` `_mon = new HardwareMonitor(cfg)` | B1 改为启动入口组装、UI 收 `IHardwareClient` |
| 采集器自我注册全局单例 | `src/System/HardwareMonitor.cs:21` `public static HardwareMonitor? Instance` | 存在第二条所有权路径，B1 需收敛 |

### 1.2 `HardwareMonitor.Instance` 消费面（11 个文件，B2/I1 迁移清单）

`src/Core/Actions/WebActions.cs`、`src/Core/Actions/AppActions.cs`、`src/System/InfoService/InfoService.cs`、`src/System/WebServer/LiteWebServer.cs`、`src/System/WebServer/WebSessionManager.cs`、`src/UI/CleanMemoryForm.cs`、`src/UI/HardwareInfoForm.cs`、`src/UI/SpeedTestForm.cs`、`src/UI/UIController.cs`、`src/UI/Helpers/MainFormBizHelper.cs`、`src/UI/Settings/SystemHardwarPage.cs`。

注意：比两份计划行文假设的"UI+Web"宽——Core/Actions 与 InfoService 也直接取单例。B2 迁移范围按此 11 文件计，不按计划文本里的子集。

### 1.3 采样入口（全仓仅 2 处，利好 B1）

| 调用方 | 位置 |
|---|---|
| UI 刷新 Tick | `src/UI/UIController.cs:1056` `await _mon.UpdateIfDueAsync()` |
| Web 限频共享入口 | `src/System/WebServer/LiteWebServer.cs:224` `hw.UpdateIfDueAsync(cancellationToken)` |

单一协调器目标已有事实基础：不存在第三条独立采样循环。

### 1.4 LHM 对象泄漏面（B2/I1 禁改清单）

| 泄漏 | 文件 |
|---|---|
| `ComputerInstance` | `HardwareMonitor.cs`（属主）、`UI/HardwareInfoForm.cs`、`UI/Settings/SystemHardwarPage.cs` |
| `SyncLock` | `HardwareMonitor.cs`（属主）、`UI/Settings/SystemHardwarPage.cs` |
| `using LibreHardwareMonitor` | `UI/HardwareInfoForm.cs`、`UI/Controls/LiteTreeView.cs` |

### 1.5 配置单例读取

`Settings.Load()` 全仓 40 处调用；热路径包含 `Core/MetricItem.cs:127`（属性 getter 内逐次查 `MonitorItems`）与 `Core/MetricUtils.cs:136`。B3 投影的首要受益点。

### 1.6 指标对象消费面

`MetricItem` 被 11 个文件消费：UIController、UIRenderer、HorizontalLayout/Renderer、TaskbarRenderer、BarRenderer、UiSceneSnapshotAdapter（Rendering）、ThemePreviewControl（ThemeEditor）、UIUtils、MetricItem 自身。B4 拆分时 UI 兼容包装的委托面按此计。

## 2. 静态对象/单例清单

| 单例/静态 | 位置 | 消费面 | 归属阶段 |
|---|---|---|---|
| `Settings._instance`（可变） | `src/Core/Settings.cs:373` | 40 处调用 | B3 投影收口 |
| `HardwareMonitor.Instance` | `src/System/HardwareMonitor.cs:21` | 11 文件（§1.2） | B1→契约、B2 迁移 |
| `InfoService.Instance` | `src/System/InfoService/InfoService.cs` | 插件/MetricItem 颜色等 | B4（样式输入显式化） |
| `ThemeManager.Current` | `src/Core/ThemeManager.cs` | UI/渲染/主题编辑器 | B3 显示配置投影 |
| `HardwareHistoryLogger`（static class） | `src/Core/HardwareHistoryLogger.cs:30` | 历史记录 | I0 写盘点/I3 |
| `GpuRegistry` 等 GPU 静态 | `src/Core/GpuRegistry.cs`、`GpuKey.cs`、`GpuItemSync.cs`、`GpuGroupPolicy.cs` | `GPU.{index}.*` 指标 | I3 快照字段化 |

## 3. 生命周期与定时器（拟定所有者）

| 定时器/循环 | 位置 | 现属 | 拟定 |
|---|---|---|---|
| UI 刷新 Tick（含采样触发） | `UIController.cs:138` | UI | UI 保留，采样触发改走契约 |
| GPU 呈现 8ms 循环 ×2 | `Rendering/Presentation/GpuPresentationHostController.cs:102`、`ThemeEditor/GpuThemePreviewControl.cs:55` | 渲染 | 留主程序（Plan A §3 明确渲染不属采集） |
| 传感器窗口 1s | `UI/HardwareInfoForm.cs:91` | UI | B2 显式登记临时需求 |
| 趋势 15s / 流量 1s / 测速 500ms | `HardwareTrendForm.cs:115`、`TrafficHistoryForm.cs:89`、`SpeedTestForm.cs:236` | UI | 消费方自持，读快照 |
| 自动隐藏 250ms | `UI/Helpers/MainFormBizHelper.cs:135` | UI | 留 UI（autohide 分支合入后核对） |

## 4. I0 专项：写盘点（单一所有者核对）

| 写点 | 位置 | 机制 | 拟定所有者 |
|---|---|---|---|
| settings.json | `src/Core/Settings.cs:714` | 直接 WriteAllText | 主程序设置仓库（Plan A §3） |
| 历史记录 json | `src/Core/HardwareHistoryLogger.cs:325-333` | 暂存+`File.Move(overwrite)` 原子替换 | 主程序（I3 保持单写） |
| 插件市场记录 | `src/Plugins/MarketFileIo.cs:147` | 同目录暂存+备份+原子替换（F17 加固） | 主程序 |
| GPU 路由 pending 配置 | `src/Rendering/Configuration/GpuStartupGuard.cs:305-306` | 暂存+原子 Move | 主程序渲染侧 |
| 流量日志 | `src/Core/TrafficLogger.cs` | 见文件 | 主程序；I3 检查点化（Plan A §3 连续性策略） |

## 5. I0 专项：静态发布器与辅助进程

**静态发布器**（Plan A §2 列名对象，进程隔离后必须改快照字段）：`CommittedMemoryPublisher/Reader`、`CpuVoltageResolver`（`src/System/HardwareServices/`）、`GpuRegistry/GpuKey/GpuItemSync`（`src/Core/`）。
注意：封存的 r06c 分支曾为电压/提交内存建 `CpuVoltageContracts.cs`、`CommittedMemoryContracts.cs`——master 无此二文件，B1 建 `LiteMonitor.Contracts` 时可参考其字段口径但不直接搬（其基于旧代码）。

**辅助进程/外部会话**（I3/I4 的 Job Object 与清理范围）：

| 项 | 位置 | 说明 |
|---|---|---|
| FPS 辅助进程 | `src/System/HardwareServices/FpsCounter.cs`（Process.Start） | 采集侧，归 HardwareHost 会话 |
| 驱动安装 | `src/System/HardwareServices/DriverInstaller.cs` | 交互动作留应用层（Plan B §4.1 不弹窗原则的例外面） |
| 更新器/自启 | `src/System/UpdateChecker.cs`、`AutoStartProcess.cs` | 主程序侧 |
| ETW 网络流量 | `src/System/NetworkTraffic/EtwNetworkTrafficEventSource.cs` | 采集侧会话，退出清理纳入 I3 |
| 其余 Process.Start | WebActions、AboutForm、UpdateDialog、MainFormBizHelper、TaskbarBizHelper、SystemActions、PluginMarketPage、PluginPage、MainPanelPage | 逐项判归属（多数留 UI/应用层） |

## 6. 并行状态核对（Plan B §7.1 执行情况）

- F08b：已合入（**含接线**，`HardwareMonitor.cs` 构造前隔离开关可达）；ControllerProbePolicy 语义并入 B1 `SamplingConfig` 清单。✅
- F14b / F06：已合入；`SettingThemeMode.cs` 落入 `src/Core` 且无 UI 引用（A 组断言零变化）。✅
- F17：按裁决放弃合入（分支保留待工作树清理）。✅
- **autohide：未合入**（3 提交在分支，待实机冒烟 4 步）。其落点为 `AppActions.cs`/`MainForm_Transparent.cs`/`AutoHideLogic.cs`——均不在 §7.1 冻结清单（属 B3 落点），**不阻塞 B0~B2/I0~I1**；B3 启动前必须合入或明确搁置。
- r06c：封存；断言脚本已摘取，棘轮基线随 W3 合并上调（C-1 142→147、C-3 167→172、C-4 338→353，+5 文件/+15 public 类型，均为已验收代码；A 组零变化），历史记录在 `scripts/architecture-baseline.json`。✅
- 遗留脏工作树：f06（1 文件）、f08b（1 文件）、r06-namespace（5 个未跟踪 docs/scripts）——不阻塞，随工作树清理处理。

## 7. B0/I0 剩余待办

| 项 | 方法 | 责任 |
|---|---|---|
| 行为 fixture | 按 Plan B §6.1：数值/单位/阈值/多语言的输入输出对，先于 B4 拆分采集 | B0 执行者（可用测试工程落 `tests/LiteMonitor.Tests` fixture 文件） |
| 性能基线（§6.3/§8） | 同机同配置记录：采样次数/秒、CPU 时间、私有字节、UI 刷新延迟、目录查询延迟、启动时间；I0 另需两模式 A/B 口径 | 需实机运行，命令与表格另附 |
| 实机项（前置合入验收） | autohide 冒烟 4 步；F08b 的 Process Monitor 6 场景 | 用户 |

## 8. B1/I1 交接最小边界（首批契约的既知输入）

1. 契约消费者 = §1.2 的 11 文件；采样入口 = §1.3 的 2 处；LHM 泄漏面 = §1.4 的 3 文件。
2. `SamplingConfig` 语义清单：现有设置 + F08b `ControllerProbePolicy`（探测策略/排除清单/构造前开关）+ 既有 `DiskProbePolicy`。
3. `DeviceCatalog` 需承载 F07 稳定设备身份语义；`HardwareSnapshot` 需承载 F03/F03b 网络样本时效与缓存窗口语义。
4. 静态发布器（§5）首版留在进程内适配器后，I3 再字段化——B1 不强制搬。
