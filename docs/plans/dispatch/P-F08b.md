# 派发单 P-F08b —— 精细允许列表与控制器策略（含库能力 spike 决策门）

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| W3 | **P-F07 与 P-F09a 已合并** | `fix/F08b-disk-allowlist` | #466、#601、#245 |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-F08b 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则，再通读拆解计划 §4「P-F08b」。开工前：读 `docs/verification/acceptance/F08a-report.md`（CountersOnly 现状）与 `F09a-report.md`（依赖决策）。

## 证据锚点

- P-F08a 已交付 `DiskProbePolicy` 与 CountersOnly；本包在其上加"仅内部盘/明确允许列表的 SMART 模式"。
- #245：`ShouldEnableController()` 缓解已发布但报告者 1.2.9 仍复现——控制器/HID/USB 枚举路径需独立审计（DAPLink 排除）。
- LHM 库行为以 P-F09a 决策记录中的实测证据为准，不凭版本号推断。

## 要点（完整步骤见拆解计划 §4 P-F08b）

1. **Spike 先行（决策门）**：验证 LHM 能否"仅创建/打开允许列表内的磁盘"——排除判定必须发生在库创建设备与打开句柄**之前**。若现有库做不到：修库入口（按 F09a 推荐路线）或**暂不开放该模式**（不得构造后过滤列表冒充隔离）。**结论必须写入报告**，spike 不通过则本包只交付 controller 策略部分并说明。
2. Spike 通过才实施：仅内部盘/明确允许列表的 SMART 模式；排除 USB、可移动盘及明确不允许设备。
3. #245：控制器探测开关 + 按可靠标识排除 DAPLink 等设备；磁盘策略不冒充 #245 完整修复（报告注明）。

## 完成产物

1. 分支 + 红→绿提交；假设备工厂证明"排除设备构造次数为 0"；允许列表增删实时生效。
2. 待实机：Process Monitor 三场景、可移动盘安全移除、HWiNFO 并行、DAPLink 连接/断开。
3. `docs/verification/acceptance/F08b-report.md`（**必须含 spike 结论**）。
