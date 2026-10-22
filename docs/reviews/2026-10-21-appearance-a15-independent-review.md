# A15 独立复核：33245bc

日期：2026-10-21。固定提交：`33245bc`，包含 `ee20d4b`、`4693ce3`、`902f71d` 的本轮修改。

## 结论

**CPU 口径修复及记录的数据成立，PMv2 生成配置成立；A15 服务生命周期尚不能通过独立验收。** 本轮确认 **1 项 P1、5 项 P2**。其中四项服务缺陷由闸门假后端连续三次复现，两项脚本问题由源码和官方 API 定义确认。

建议先修复服务生命周期，再做 A16 的生产接线。A16 主题、开关和提示的独立准备工作可以并行；M1 已知超预算、保持画面的产品决定继续有效。

证据：[汇总](evidence/a15-33245bc/suite-summary.json)、[生命周期反例](evidence/a15-33245bc/lifecycle-repros.json)、[三次重复结果](evidence/a15-33245bc/lifecycle-repeat-summary.json)、[CPU 数据复算](evidence/a15-33245bc/cpu-data-recalculation.json)、[重跑说明](evidence/a15-33245bc/README.md)。

## 一、发现

### S1 · P1：Stop 超时后重启，新旧捕获线程重叠，旧线程向新生命周期发布

位置：[BackdropCaptureService.cs:810](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/src/System/Backdrop/BackdropCaptureService.cs:810)、同文件第 691、1274、1112 行。

`StopCore` 等待线程退出前就清空 `_worker/_cts`。若 Join 超时，`Start` 仍能启动下一线程，并重新将全实例共享的 `_stopRequested` 清零。旧请求与新请求代次相同的场景下，旧线程返回的结果通过发布检查；旧线程退出时还会覆盖新生命周期的状态。

**三次实测均相同**：

1. 第一次捕获停在闸门；`Stop(20ms)` 返回 false。
2. 使用完全相同请求重启，第二次捕获进入另一闸门；后端并发调用峰值为 **2**。
3. 只放行旧捕获，UI 成功接管其红色标记帧；新捕获仍阻塞，排除了新帧混淆。
4. 旧线程把状态改为 **Stopped**，此时 `IsRunning == true`，新线程仍在运行。

这违反单后台任务、停止后不得发布迟到帧和状态一致性合同。快速关闭/开启 glass、隐藏/恢复以及慢捕获超时均可触及该生命周期边界。

**修复方向**：尚未退出的 worker 保留引用并阻止重启；独立维护本轮运行标识/取消状态。发布、成功记账、退出状态及亲和性清理均要避免旧轮次影响新轮次。仅在发布处再检查请求代次不足以修复同代次重启，也不能消除并行捕获。

### S2 · P2：失效或已停止的 active 帧仍被借出，Dispose 后仍报告 Glass

位置：[BackdropCaptureService.cs:963](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/src/System/Backdrop/BackdropCaptureService.cs:963)，另见第 918、984 行。

`UpdateRequest` 与暂停/Dispose 会作废租约，但 `TryGetFrame`、`IsReady` 和 `Status` 只判断 Image 是否非空，没有判断租约有效性与服务生命周期。Stop 也没有可靠撤销 active 的可用性。

**三次实测**：UI 接管 G1 帧后更新为 G2，`lease.IsValid == false`，但 `TryGetFrame == true`、`IsReady == true`、`EffectiveMode == Glass`。随后依次暂停、Stop、Dispose，旧图仍可借出并继续报告 Glass。

**修复方向**：借用和状态共用可用性判定，检查租约有效性、当前代次和生命周期；停止/作废后不再借出。位图仍由 UI 按租约释放，不能以后台提前 Dispose 来掩盖这个问题。

### S3 · P2：暂停后，在途结果能重新发布并将 Suspended 改回 Running

位置：[BackdropCaptureService.cs:1274](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/src/System/Backdrop/BackdropCaptureService.cs:1274)，接管入口亦未拒绝暂停状态。

`SetSuspended(true)` 清空 pending，但发布检查没有判断 `_suspended`。暂停前已经进入后端的一次捕获，返回时会重新填入 pending，允许 UI 接管，并由成功路径覆盖状态。

**三次实测**：捕获入闸门 → 暂停（状态 Suspended）→ 放行捕获，均得到 `pendingWhileSuspended=true`、`acquiredWhileSuspended=true`、发布数 1，最终状态 **Running**。

**修复方向**：在发布与接管的同步边界检查暂停/停止状态；暂停期间的在途结果丢弃并释放，不能更新为 Running。恢复只合并新请求的一帧。

### S4 · P2：捕获恢复成功后，StateText 仍显示之前的故障

位置：[BackdropCaptureService.cs:1379](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/src/System/Backdrop/BackdropCaptureService.cs:1379)。

`RecordSuccess` 更新 Running、清空降级原因，却没有更新 `_stateText`。本轮新增字段的失败转换已被加固，成功转换仍有遗漏。

**三次实测**：第一次捕获失败、第二次恢复，状态为 **Running / None**，`StateText` 仍为“捕获失败：后端没有给出可用帧。”，与失败时完全相同。

**修复方向**：成功转换时同步设置当前状态说明；历史故障继续留在 `LastFailureReason`，不要混入当前 StateText。补充失败→成功和首次成功两条断言。

### S5 · P2：启动脚本用 DPI 数值判断 PMv2，存在双向误报

位置：[run-livecheck.ps1:375](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/scripts/run-livecheck.ps1:375)。

脚本将 `DPI > 96` 判为 Per-Monitor 生效，将 `DPI == 96` 判为仍是系统感知。实际 `GetDpiForWindow` 返回缩放数值：SystemAware 返回系统 DPI，PerMonitor 返回所在显示器的 DPI。[微软定义](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)

- 正常 PMv2 窗口位于 100% 缩放屏，返回 96，会被误判失败。
- SystemAware 进程在系统 DPI 为 168 的环境返回 168，会被误判通过。
- DPI 数值也无法区分 PMv1 与 PMv2。

**修复方向**：通过 [GetWindowDpiAwarenessContext](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowdpiawarenesscontext) 读取目标 HWND 的上下文，再用 [AreDpiAwarenessContextsEqual](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-aredpiawarenesscontextsequal) 比较 PMv2；DPI 数值单独展示。不要直接按上下文指针的位值比较。

此项是源码/API 语义确认，未伪称进行了真实 UAC 或跨屏测试。主工程生成 PMv2 调用已经独立验证，不受脚本判定错误影响。

### S6 · P2：首个可见窗口可能是驱动安装对话框，未必是 MainForm

位置：[run-livecheck.ps1:212](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/scripts/run-livecheck.ps1:212)。启动路径见 [Program.cs:89](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/src/System/Program.cs:89)、[DriverInstaller.cs:889](G:/TOOL/LiteMonitor-fork/.review-scratch/appearance-a15-audit-33245bc/target-src/src/System/HardwareServices/DriverInstaller.cs:889)。

脚本虽按目标 PID 枚举并取最大客户区，但轮询发现任何可见窗口就立即返回。PawnIO 缺失/升级时，程序会在创建 MainForm **之前**显示同 PID 的 `UpdateDialog`。此时脚本即可把驱动对话框当作主窗口，报告其 DPI 和客户区。

因此“Explorer 中能命中最大窗口”只证明枚举可用，不能证明识别了 LiteMonitor 主窗口。

**修复方向**：等待明确的主窗体就绪信号或唯一窗口标识，并排除启动阶段的对话框；DPI 自检针对确认后的 MainForm HWND。此项由启动控制流确认，未在本轮人为触发驱动安装。

## 二、CPU 证据复核

### 可以认定的部分

- `PairedCpuMeasurement.MeasureBatch` 已使用 `Process.TotalProcessorTime` 的批次差值，另以 Stopwatch 记录经过时间；本轮 N1 的计量实现修复成立。
- 原始结果明确记录主题为 `CyberPunk_Mech`、轮数为 5；指定不存在主题会报错，未知参数也会失败。
- 独立短测确认 5 轮和主题参数实际生效；样本过小导致处于噪声内时，CPU 判定值返回 null，没有伪报为 0。
- 根据已提交的原始分轮数据重新核算，报告的数值和投影公式一致：

| 场景 | CPU 增量 ms/帧 | 同批经过时间增量 | 按 16 逻辑核、12.5Hz 投影的整机百分点 |
|---|---:|---:|---:|
| 普通档 | 2.0352 | 2.1205 | 0.1590 |
| 4 卡两倍档 | 8.7328 | 8.9677 | 0.6823 |

两组均为 5/5 轮超过探针自设噪声门槛。这里的“显著”是探针的噪声判据，不是另行执行了统计显著性检验。

本轮核对了原始数据与计量代码，**没有重新运行约一小时的完整长测**。整机百分点来自测得的绘制 CPU 时间投影，不能冒充完整应用的任务管理器实测；不包含硬件采样、真实 DWM 呈现和 M2 捕获开销。

### 两处表述建议收窄

1. 原始数据是 **每条路径每轮 4,000 次，5 轮合计 20,000 次**，不是“5 轮 × 每轮 20,000 次”。
2. §6.4/§6.7 将结论延伸为“经过时间→CPU 占用换算方法本身成立，口径更正对增量不成立”，这个推论过强。对任意两路径都有 `Δ经过时间 = ΔCPU时间 + Δ等待/抢占等时间`；本批次两者接近，只说明该批次后一个差值较小，不能保证以后始终抵消，尤其不能用于有 UI 协调/捕获等待的 M2。

建议改成：“本批次 CPU 增量与耗时增量接近，支持之前的量级估算；CPU 预算及整机投影以 CPU 时间结果为准。”当前 CPU 实测已足够支持已知超预算的决定，无需通过宣称两种计量等价来加强结论。

## 三、本轮亲自运行的检查

全部在 `git archive 33245bc` 的隔离目录完成。

| 项目 | 结果 |
|---|---|
| 解决方案 Release 构建 | 0 错误；254 警告，包含测试工程及环境告警，不与主工程 234 直接混比 |
| 单元测试 | 583/583，0 跳过 |
| M2 合同探针 | 源码 2 条 + 运行时 8 条 = 10/10 |
| 接线探针 | 73/73 |
| PMv2 编译生成代码 | 确认 `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` |
| CLI 反例 | 未知参数非零退出、不存在主题非零退出、合法 5 轮短测成功 |
| 原 CPU 长测数据 | 重新计算分轮均值、总绘制次数与 CPU 投影，结果一致 |
| 新生命周期反例 | 四项均连续三次复现，见 S1–S4；不属于通过项 |
| 启动脚本语法 | AST 解析 0 错误；本轮没有执行脚本、启动真实 GUI 或触发 UAC |

本轮重跑 #9 的无 processor 路径结果为：发布 0、失败 1、原因 `BackendFrameInvalid`、原因文本非空、停止后存活位图 0。该加固有效。

### 为什么 10/10 没覆盖这些缺陷

- #4 检查捕获返回前换代，未检查 **已接管 active** 的作废与后续借用。
- #5 检查 pending 上限，未接管 active。
- #6 使用快速假后端测试正常 Stop，未测试超时后重启、暂停期间在途帧返回。
- 原因/文本检查没有覆盖服务的 **失败→恢复成功** 转换。
- SDK 生成调用与脚本语法通过，均不能证明脚本识别了正确 HWND 和实际 DPI 上下文。

## 四、建议修复与后续顺序

| 工作包 | 负责项 | 并行边界 |
|---|---|---|
| 服务生命周期 owner | S1–S4，并将本报告闸门反例加入合同探针硬断言 | 共享 `BackdropCaptureService.cs`，由同一人一次收口 |
| 启动验证 owner | S5–S6，核对 HWND 身份与 PMv2 上下文 | 脚本为主；主窗就绪标记的公共接口与集成者约定 |
| 文档/测量 owner | 绘制次数、CPU/经过时间解释、证据范围 | 可与前两项并行；不修改已接受的 M1 视觉取舍 |
| 最终复验 | 合并后构建、583 单测、合同/接线及新增反例 | 串行运行；通过后再接 A16 生产生命周期 |

A14 实机第 1–4、7 项仍应在新 PMv2 行为下重做。脚本 S5/S6 修复后再用于实机证据采集；真正的 UAC、跨屏、录屏、锁屏/RDP、捕获预算及 UI 响应留在明确的实机验收中。低整机 CPU 投影不等于 UI 线程必然无卡顿。

## 五、版本与证据

- 固定导出 SHA256：`B11E27CDEFBC554AB9AA2BC251A44EFE04BEA7F4CAF82C97EAFA6526BD393297`。
- 初始构建主 DLL SHA256：`2BE95E3DBF0AF90F8A6867B02154DFD0180EE553A5A83F2598F262C8977D10E2`。
- 完整日志、TRX、固定源码、生成代码和复现程序：`.review-scratch/appearance-a15-audit-33245bc/`。
- 精选证据：本文同级 `evidence/a15-33245bc/`。本轮仅新增审查文档与证据，没有修改产品实现。
