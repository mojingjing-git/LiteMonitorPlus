# 派发单 P-F08a —— 磁盘探测 CountersOnly 止血模式

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| W1 | 无（F00 已固化） | `fix/F08a-disk-counters-only` | #466、#601（止血）、#245（降低暴露面） |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-F08a 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则（注意 `HardwareMonitor.cs` ≤30 行限域、集成时 F03 先并后 rebase 本包），再通读拆解计划 §4「P-F08a」。

## 证据锚点

- 取证：`docs/verification/2026-10-25-TASK-3-crash-hardware.md` —— `IsStorageEnabled=true` 无条件；HWiNFO ISA Lock 截图点名 LiteMonitor；USB 盘占用/DAPLink。
- 交叉发现 G：旧计划标注 `DiskProbePolicy`「已实施」但 `git ls-tree`/grep 零匹配——**从未落地**，本包新建。
- 依赖：LHM 0.9.6（LiteMonitor.csproj:49）。

## 要点（完整步骤见拆解计划 §4 P-F08a；仅做 CountersOnly，不做允许列表——那是 P-F08b）

1. 新建 `DiskProbePolicy`；`CountersOnly` 模式在第一次 `Computer.Open()` 前 `IsStorageEnabled=false`；速度/活动率走 Windows 计数器；温度/SMART 策略禁用且**不显示 0**。
2. 设置页扫描、后台维护、重载全走同一策略；新装默认 CountersOnly；旧配置缺字段→显式迁移状态+提示（兼容模式与 SMART 影响说明），首次打开硬件前仍 CountersOnly，保留原磁盘偏好与监控项；用户选择后持久化（Draft-Commit）。
3. 模式变更经采样协调器：停采样→释放旧对象→重建代次→恢复；失败保持计数器数据并显示状态；关闭/移除释放句柄。

## 特别限制

- `src/System/HardwareMonitor.cs` 编辑 **≤30 行**且仅存储启用策略区域；`SensorMap.cs`/`FanMapper.cs`/GPU 文件禁改（P-F07 排队中）。

## 完成产物

1. 分支 + 红→绿提交；假设备工厂断言"禁止设备构造次数为 0"；策略状态机用例。
2. 待实机清单：Process Monitor 冷启动/设置页/定时维护无探测；可移动盘安全移除；HWiNFO 共存；DAPLink。
3. `docs/verification/acceptance/F08a-report.md`。
