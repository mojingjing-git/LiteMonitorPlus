# 派发单 P-F09a —— 原生依赖候选验证（只读研究包）

| 批次 | 前置 | 分支 | 覆盖 |
|---|---|---|---|
| W1 | 无（F00 已固化） | `research/F09a-dependency-candidate`（试验性改动不回合并 master） | #502、#579 的决策前置 |

## 启动指令（原样交给执行智能体）

> 你是工作包 P-F09a 的执行者，仓库 `G:\TOOL\LiteMonitor-fork`。先通读 `docs/plans/dispatch/README.md` 外派总则，再通读拆解计划 §4「P-F09a」。**本包不改产品源码（src/**、LiteMonitor.csproj 禁改）；产物是一份决策记录。**

## 证据锚点

- LHM 0.9.6 `_nvmlDevice` 为 readonly 缓存字段：`docs/verification/2026-10-25-TASK-2-gpu-sensors.md`（IL 级取证）。
- DiskInfoToolkit 1.1.2 自建线程 `DevicesChangedListener()`：`docs/verification/2026-10-25-TASK-3-crash-hardware.md`；应用层 catch 结构上覆盖不到。
- LHM 上游 [PR #2539](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/2539)（NVML 修复）已合并，merge SHA `3aa9c6c`——**已合并≠已发布进任何 NuGet 包**，这正是你要回答的。
- 本机 **双 NVIDIA（RTX 3080 / 3080 Ti）**：候选包可做受控真机冒烟。

## 要验证的候选

1. 固定源码补丁（针对 LHM 0.9.6 / DiskInfoToolkit 1.1.2）。
2. 包含目标修复的已发布 LHM 包（含 1.0.x 预发布，逐版本核对其发布说明/程序集是否真含 NVML 句柄重取与失效处理）。
3. DiskInfoToolkit 2.2.1（报告线索，仅为候选）。

## 产出（写入 `docs/verification/acceptance/F09a-report.md`，决策记录）

1. 每候选：实际程序集/源码是否含目标修复的**直接证据**（反编译/源码 diff，不凭版本号推断）；API 面差异（PawnIO、Intel GCL、磁盘访问行为）；可重现构建步骤。
2. 用小型受控验证工程（`.tmp/` 或独立目录，引用候选包，模拟句柄失效序列）在本机双卡上冒烟；可注入设备构造/回调异常。
3. 明确推荐组合 + 回退组合；主版本跨越/传递依赖覆盖风险逐条列出。

## 完成产物

分支（仅验证工程与报告）+ `F09a-report.md`。此报告直接决定 P-F09b 的实施路线，是 W3 的 P-F08b 与 W4 的 P-F09b 的前置输入。
