# 派发单 P-B3 —— 设置投影与模块服务（B3，与 P-I2 并行）【v2，按评审返工】

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| B3 | 首批已验收（`fd3b425`）。**例外清偿（§3）动工前：`fix/autohide-window-loss` 须合入或明确搁置**（母计划 §7.1 口径；该分支重写 `MainFormBizHelper.cs` 197 行并改 `AppActions.cs`/`MainForm_Transparent.cs`，与本包 §3 文件直接交叠） | `pkg/B3-settings-services`（主工作树） | Plan B §4.2/§4.4/§5 B3 行 |

## 启动指令

> 本包由主会话执行。`src/Core/Settings*`、`SettingsChanger` 及 Instance 例外文件按下列阶段归属 B 线；与 P-I2 的边界见 §1，越界前先对交接 SHA。

## 1. 与 P-I2 并行的文件所有权（见 P-I2 §1 同表）

要点：`src/Core/**`、`src/UI/**`、`ThemeEditor`、`WebServer/InfoService` 内容归本包；`src/System/Pipe/**`、`src/HardwareHost/**`、`src/Contracts/**`、`Program.cs`、csproj/sln、`UIController.cs` 构造缝（±5 行）归 P-I2（本包 rebase 时知悉）；测试各自新增；CI 与架构基线双方均不动，集成人收口。

## 2. 阶段一（立即可做，与 autohide 零交叠）

1. **设置投影（纯函数）**：新增 `src/Core/SettingsProjection.cs`——从 `Settings` 派生不可变投影：`SamplingConfig`（复用契约类型：Revision=变更计数、探测策略 ModeRaw、设备选择）、`DisplayConfigProjection`（语言/皮肤/刷新率/用户单位与格式）、`WebConfigProjection`、`PluginConfigProjection`。派生函数无 IO、无单例读取；配 `SettingsProjectionTests`（含 f14b 主题字段透传、F08b 探测策略 Raw 映射）。
2. **模块服务（新类，不动既有行为）**：`src/Core/Actions/Services/` 下 `SamplingConfigApplier`（投影→`IHardwareClient.ApplyConfig`）、`WindowAppearanceApplier`、`TaskbarController`、`WebServerController`、`PluginRuntimeController`——首版为既有调用路径的薄委托封装，Start/Apply/Stop 职责按 §4.4，纯内部类不加接口。**约束**：新服务不得新增 `src/Core`→UI/WinForms 的 using 或类型引用（A-1/A-5/A-6/A-7 棘轮硬拦）；确需 UI 参与的以委托注入或把该服务落位 `src/UI`。
3. **SettingsChanger 差异清单**：候选验证→diff 列表→按归属分派模块服务。颜色/语言变更不触发硬件重建（以"ApplyConfig 未被调用"为观测面的测试断言）；设备访问策略变更走 `SamplingConfigApplier`；Web 端口单独重启监听。失败保留"已保存配置/实际生效状态/失败原因"三元组并落为可见产物（报告/日志字段）。
4. 验证：构建 0 错误、全量测试、fixture 复跑全绿。**架构基线**：若 A 组实际值低于基线（如改造后 `src/Core` 的跨层 using 减少），在报告中记录新数字即可，基线下调由集成人在两包落地后统一执行——本包不动 `architecture-baseline.json`。

## 3. 阶段二：Instance 例外清偿（对 B1B2 报告 §4 十文件清账）

- **2a Web 侧 4 文件（无前置，可与阶段一并行推进）**：`WebActions`、`InfoService`、`LiteWebServer`、`WebSessionManager`——P-I2 v2 对 WebServer 区域零改动，无对撞风险，不再等待 I2。
- **2b UI 侧 5 文件 + AppActions（前置：autohide 合入或明确搁置）**：`CleanMemoryForm`、`MainFormBizHelper`（SmartCheckDriver）、`SpeedTestForm`、`HardwareInfoForm`（默认组装改注入，`MenuManager` 传 UIController 持有的客户端）、`SystemHardwarPage`（DiskProbePolicy/流量排除状态走投影或客户端）改构造注入；交互动作（CleanMemory/驱动检查）按 §4.1 留应用层：组合根注入引用，不再读静态 Instance。`AppActions` 拆解为模块服务编排入口（autohide 落点文件，必须在其后）。

## 4. 阶段三：启动组装与 Instance 降级（B3 收尾）

启动组装收敛（`Program.cs`/`UIController` 组合根注入各服务——`Program.cs` 与 P-I2 协调：若 P-I2 已改启动入口，rebase 其后）。`HardwareMonitor.Instance` 降为 internal：P-I2 v2 不搬移该文件，降级在本包阶段三执行；若届时 P-I2 尚未落地，仅降级并修复主程序集内引用（无需跨程序集可见性）。

## 5. 验收（Plan B §6.1"设置"行 + §5 B3 完成条件）

草稿取消不生效、不覆盖运行统计；只重配受影响模块（颜色/语言不触发硬件重建有测试断言）；失败状态可解释（三元组可见）；`settings.json` 形状零变化；行为 fixture 复跑全绿。

## 6. 禁改

`MetricItem`/`MetricUtils`/`MetricLabelResolver`（B4）；§1 表中 P-I2 专属区域（含 `UIController` 构造缝）；CI 与断言脚本/基线（集成人收口）。
