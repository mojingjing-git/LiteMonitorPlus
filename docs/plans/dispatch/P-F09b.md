# 派发单 P-F09b —— 原生依赖修复集成（硬件串行链）

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| W4 | **P-F09a 决策 + P-F08b 已合并** | `fix/F09b-native-dependency` | #502、#579；支持 #290/#582 诊断 |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-F09b 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则，再通读拆解计划 §4「P-F09b」。开工前必读 `docs/verification/acceptance/F09a-report.md`，严格按其推荐组合实施（含回退组合预案）。

## 证据锚点

- LHM 0.9.6 `_nvmlDevice` readonly 缓存字段 → `NvmlDeviceGetPowerUsage` AccessViolation（TASK-2 IL 级取证，#502）。
- DiskInfoToolkit 1.1.2 `DevicesChangedListener()` 自建线程异常，应用 catch 覆盖不到（TASK-3，#579）。
- 本机 **双 NVIDIA（RTX 3080 / 3080 Ti）**：NVML 句柄重取/失效处理可在本机部分实测（含双卡场景）；磁盘事件场景无对应硬件时标记待实机。

## 要点（完整步骤见拆解计划 §4 P-F09b）

1. 按 F09a 决策固定依赖（包锁定或源码补丁）；源码补丁须固定来源 SHA、补丁文件、许可证、构建步骤与产物摘要；更新 NOTICE（如适用）。
2. NVML 按设备身份重取有效句柄并处理驱动/设备变化；覆盖双卡/同型号卡，**不按枚举索引重取**；能力不支持/设备离线返回缺失状态。
3. DiskInfoToolkit 库侧逐项隔离设备事件/构造/回调边界；单设备失败有界退避、其他设备继续、退出可停止监听。无法库内隔离时**另立辅助进程隔离设计文档**，不在本轮引半套 IPC。
4. 日志含设备代次/操作/来源版本/错误类型，限频，不落设备序列号；原生崩溃需转储证据。

## 完成产物

1. 分支 + 受控子进程注入异常测试 + 红→绿提交；断言：注入设备构造/回调异常主程序存活；采样中移除/恢复 GPU 可恢复；监听停止后无残留线程。
2. #502 关闭条件：NVIDIA 实机通过；#579 关闭条件：磁盘事件场景通过；#582 缺原堆栈**不声称覆盖**。
3. `docs/verification/acceptance/F09b-report.md`。
