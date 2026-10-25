# P-B3 验收记录 —— 设置投影、模块服务与 Instance 例外清偿

## 1. 标识

- 工作包：[P-B3](../../plans/dispatch/P-B3.md)（v2，经两轮评审）；基线 `c912e83`（阶段一）→ 本记录收口 HEAD
- 日期：2026-10-26；执行者：主会话；autohide 前置已满足（实机冒烟全过后合入）

## 2. 交付

### 阶段一（`2cb32b2`/`335df6c`/`c912e83`）
设置投影四件（纯函数）、模块服务五件套（Sampling 做实、四件委托壳）、差异清单 `SettingsChanger.ComputeDiff` 与分派器；核心断言落测：外观变更不触发硬件重配（ApplyConfig 零调用）。

### 阶段二+三（本提交）：Instance 例外清偿与网关收口
1. **应用层网关 `src/System/AppHardwareGateway.cs`**（B3 领地，目标命名空间落位）：组合根（UIController 构造）注册一次、Dispose 注销；数据面（NetworkIP/取值/HasFpsData）、交互动作（CleanMemory/驱动检查）、采样协调（限频请求/Web 需求写入）、策略面（探测策略读写/流量排除）统一取用点。
2. **十文件例外清账**（B1B2 报告 §4 全部）：WebActions、AppActions、InfoService、LiteWebServer×3 处（含 WriteSnapshotJson）、WebSessionManager（仅文档注释）、CleanMemoryForm、MainFormBizHelper、SpeedTestForm、SystemHardwarPage×3 处、HardwareInfoForm（客户端改必传注入，默认组装例外撤销）、MenuManager（经网关传客户端）。`src/` 对 `HardwareMonitor.Instance` 的代码引用**清零**（余量均为注释或 HardwareMonitor 内部）。
3. **`HardwareMonitor.Instance` 降 internal**（B5 拆程序集前的先行收口）；IVT 补 `GpuHostLifecycleProbe`（与其他测试工程同待遇）。
4. **棘轮改善**：A-2 8→6（WebActions/AppActions 移除 SystemServices using，改全限定引用）。
5. 源码级护栏（WebSamplingDemandTests ③）跟随链路更新：LiteWebServer 锚定网关入口 + 网关源码锚定 `UpdateIfDueAsync`，护栏意图（共享限频采样）不变。

## 3. 验证

- 构建 0 错误；全量 **2362 执行 / 0 失败**（发现 2369）；`architecture-assert` 12/12 全绿（基线带史下调 A-2=6）。
- 行为等价面：网关转发全部一一对应原 Instance 调用（含 Web 需求写入的"未注册返回 false 重试"语义、WriteSnapshotJson 空对象兜底）。

## 4. 遗留与收尾记录

1. ~~dispatcher 接线进 ApplyAllSettings~~ → **已落地（安全子集形态）**：`SettingsForm` 保存路径改为 `AppActions.ApplySettingsSelectively`（合并前 `CloneForDiff` 快照 → `ComputeDiff` → `ResolveApplyPlan` 纯函数划分）：**空差异零扰动**；**纯 Web/纯插件变更只应用对应模块、不重建界面**；其余（含主题扇出面与采样/布局联动）走与 `ApplyAllSettings` 同序的界面重建组——零回归面。`ApplyPlan`/`ResolveApplyPlan` 为 internal（IVT 供测试），11 个计划用例 + 端到端克隆比对用例全绿。`SettingsApplyDispatcher`+`SamplingConfigApplier` 保持"已就绪已测、未接进保存路径"状态（保存路径的采样重配仍由 `ApplyMonitorLayout` 内既有 `RefreshHardwareConfig` 承担），是否切换到契约式 ApplyConfig 随 B4/B5 消费面扩大再定。
2. 模块服务四件委托壳的组合根接线：与 1 的取舍合并——保存路径直接调用 AppActions 既有方法（同序），委托壳保留给后续真正的跨模块编排场景。
3. 实机目视（待用户）：设置保存/取消（含"不动任何东西直接保存"应零扰动、只改 Web 开关应不闪界面）、测速窗口、清理内存、驱动检查。
