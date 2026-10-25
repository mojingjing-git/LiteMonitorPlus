# P-B1B2 验收记录 —— C1 契约工程 + 进程内适配器 + 消费方迁移（B1+B2 ≙ Plan A I1）

## 1. 标识

- 工作包：[P-B1B2](../../plans/dispatch/P-B1B2.md)；批次：首批（B0～B2 / I0～I1 同批批准）
- 基线：`98c0799`（B0 盘点后）→ 本包收口 HEAD（见提交 `feat(B2)`）
- 执行者：主会话（契约与调用方迁移同一负责人条款）
- 日期：2026-10-26

## 2. 交付内容

### B1（契约与适配器）

1. `LiteMonitor.Contracts`（net8.0，无 WinForms/LHM/Vortice）：`IHardwareClient`、`SamplingConfig`、`SamplingDemand`、`HardwareSnapshot`、`HardwareCatalog(+Entry)`、`HardwareHealth`、`MetricSample`、`SampleQuality/HardwareState`。
2. 主工程手术：`Compile Remove` + `ProjectReference`，sln 挂接；`LiteMonitor.Contracts.dll` 独立程序集，测试工程为 ProjectReference 型无双编译。
3. `HardwareClientAdapter`（`src/System/HardwareServices/`，目标命名空间 `LiteMonitor.SystemServices` 落位）：六个成员 + `GetNetworkIP` 委托现有实现，零新增轮询。
4. `UIController` 构造组装并持有 `IHardwareClient`，Dispose 对应。

### B2（消费方迁移）

1. **采样入口统一**：`UIController` Tick（原 `_mon.UpdateIfDueAsync`）与 IP 读取走契约；Web 侧 `LiteWebServer` 本就以委托隔离（无 LHM 引用），默认委托暂保留（例外 #5）。
2. **LHM 泄漏清零（Plan B §6.2 检查 2 达成）**：
   - `SystemHardwarPage`：下拉枚举改走 `HardwareMonitor` 无参静态（新增 `ListAllDiskOptions()`），不再触碰 `ComputerInstance`/`SyncLock`；
   - `HardwareInfoForm`：整树改消费 `HardwareCatalog`（适配器锁内快照设备/传感器树，含值/峰值），节点 Tag 为不可变条目，1s 刷新原位换 Tag 不重建树（展开状态保持）；开窗登记 `SensorWindow` 需求、关窗释放；构造支持注入客户端；
   - `LiteTreeView`：去 LHM using，格式化/配色按 `SensorType` 字符串映射（大小写枚举名逐一对照原 switch）。
3. **适配器目录 v2**：`GetCatalogAsync` 在 `SyncLock` 内完整遍历 `ComputerInstance` 树（设备+子硬件+传感器，含 Value/Max）。
4. **需求语义修正**：`SubmitDemand` v1 只存储合并记录不驱动——Web 通道仍由 `LiteWebServer` 自己的需求回调持有；避免传感器窗口提交需求误关 Web 需求。各通道真实合流随 B3。

## 3. 行为等价证据

| 项 | 结果 |
|---|---|
| 构建 | 0 错误（290 警告均为存量 nullable 等，与迁移前一致） |
| 主测试工程 | **2134 通过 / 0 失败 / 7 跳过**（迁移前后各跑一次全绿） |
| architecture-assert | 12/12 全绿，基线零变化（含 W3 上调后） |
| 架构门禁 §6.2-2 | UI（含 ThemeEditor）与 Web **零** `LibreHardwareMonitor`/`ComputerInstance`/`SyncLock` 代码引用 |
| 行为 fixture | 待 P-B0b 合入后复跑（本包不触碰格式化行为，风险敞口为采样数据路径） |

实机项（UI 目视传感器窗口/设置页下拉/任务栏与主面板读数）留待与 P-B0b 性能基线同场执行。

## 4. 例外清单（`HardwareMonitor.Instance` 残留 10 文件；撤除阶段明确）

| 文件 | 用途 | 撤除阶段 |
|---|---|---|
| `AppActions` | GPU 计数静态、RefreshHardwareConfig（应用协调入口） | B3 模块服务 |
| `WebActions` / `InfoService` | GetNetworkIP（静态/单例上下文无注入缝） | B3 / B4 |
| `CleanMemoryForm` / `MainFormBizHelper` | CleanMemory、SmartCheckDriver 交互动作（计划明文留应用层） | B3 |
| `LiteWebServer` / `WebSessionManager` | 默认委托取 Instance（每取值时再取，注释说明） | B3 |
| `SpeedTestForm` | `Get("NET.Up/Down")`（快照 Key 覆盖面未验证前不冒进） | B3 |
| `HardwareInfoForm` | 默认构造就地组装适配器（支持注入，菜单链路注入随 B3） | B3 |
| `SystemHardwarPage` | DiskProbePolicy/流量排除状态等非 LHM 读取 | B3 |

`HardwareMonitor.Instance` 降级（internal/私有）与上表同批完成。快照占位字段（Unit/SampleAgeMs/ConfigRevision/稳定设备绑定=F07）在 B3/B4 随消费面扩大逐项做实，当前不进任何显示路径。

## 5. 后续触发

1. P-B0b 合入后复跑 fixture + 性能基线 → 首批（B0～B2 ≙ I0～I1）整体验收。
2. Plan A I2 可启动：契约已就位，HardwareHost 按同名接口提供管道实现。
3. CI：本包未新增测试（无门禁数字变化）；TRX 2233 口径待推送后在 CI 复核。
