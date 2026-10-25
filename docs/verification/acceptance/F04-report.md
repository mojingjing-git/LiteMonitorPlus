# F04 验收记录 —— 任务栏宿主生命周期与有界恢复

> 工作包：P-F04（覆盖 #553 / #573 / #583 / #539）
> 原则：只记录真实执行过的命令与观察到的结果；未执行的场景逐条列入"未覆盖场景"。
> "代码层验收通过" ≠ "可关闭 issue"（真实锁屏/睡眠/RDP/开机竞态仍待实机）。

## 1. 标识

- 工作包：P-F04
- 分支：`fix/F04-taskbar-host-lifecycle`
- 基线 SHA（开工时 HEAD）：`d52c0ab`（master）
- 提交 SHA 列表：见 §1.1
- 完成日期：2026-10-25
- 执行者：P-F04 执行智能体（worktree `G:\TOOL\LiteMonitor-fork\.worktrees\f04`）

### 1.1 提交 SHA 列表

```
bd0e353 test(F04): 真实 Win32 任务栏宿主三场景与有界恢复回归
8041df8 fix(F04): 任务栏宿主生命周期与有界恢复
```

（另有本文件自身的文档提交 `docs(F04): P-F04 验收记录`，位于上述两条之后；基线 `d52c0ab`。）

### 1.2 改动文件清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/UI/Helpers/TaskbarRecoveryCoordinator.cs` | **新增** | 唯一的生产新增文件：恢复状态机 + 有界调度 + 显示器拓扑/任务栏窗口来源接缝 + 面板句柄受控重建器 + 面板可见性闸门 |
| `src/UI/TaskbarForm.cs` | 修改 | 实现 `ITaskbarHostBridge`；`OnHandleDestroyed` 区分外部失效/受控重建/应用关闭；句柄代次；Tick 驱动恢复；可见性闸门；关闭优先 |
| `src/UI/Helpers/TaskbarBizHelper.cs` | 修改 | 目标解析（**不回退主屏**）、完整挂载校验（根句柄+内层子句柄+父子关系+目标显示器+句柄代次）、按解析结果重挂 |
| `src/UI/Helpers/TaskbarWinHelper.cs` | 修改 | `FindHandles` 不再回退主屏；`FindSecondaryTaskbar` 缺失时返回 Zero；任务栏矩形**缓存键扩展**（HWND/设备/屏幕边界/工作区/DPI） |
| `src/UI/MainForm_Transparent.cs` | 修改 | 合并恢复触发源：`TaskbarCreated` / `WM_POWERBROADCAST` / `WM_WTSSESSION_CHANGE` / `WM_SETTINGCHANGE` / `WM_DISPLAYCHANGE` / `OnDpiChanged`；关闭期收掉任务栏面板 |
| `tests/LiteMonitor.Tests/TaskbarHostLifecycleTests.cs` | **新增** | 12 条用例（真实 Win32 宿主 + 假时钟） |
| `tests/LiteMonitor.Tests/TaskbarWin32TestHost.cs` | **新增** | 真实 Win32 测试宿主（父窗口/内层子窗口/面板挂载与销毁） |

**未改动**：`TaskbarStrategyWin10.cs` / `TaskbarStrategyWin11.cs`（派发单**禁改**，归 F05）、`src/UI/Helpers/MainFormBizHelper.cs`（派发单**禁改**，归 F06）、`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、`src/UI/UIController.cs`、`src/System/HardwareMonitor.cs`、`.github/workflows/ci.yml`、`release.yml`、`resources/lang/*.json`。

## 2. 覆盖与结论

- 覆盖 issue：#553（锁屏/关屏后任务栏数据丢失）、#573（多屏开机后面板位置不固定）、#583（RDP 后副屏面板位置错误）、#539（纯任务栏模式切分辨率后位置/布局跳动）
- 结论：☑ **代码层验收通过**　□ 部分通过　□ 阻塞
- 允许关闭的 issue 与条件：
  - **#583**：可关闭（前提是实机确认"目标屏不可用期间面板不再静默落到主屏"这一**用户可见行为变更**可接受）。
  - **#573**：可关闭（开机竞态的**代码根因**已消除；仍建议按 §6 做 ≥5 次冷启动抽样）。
  - **#553**：**代码层**通过，但"锁屏/睡眠/Explorer 重启/RDP 后自动恢复"的**实机观感**未验证 ⇒ 建议按 §6 逐项实机后再关闭。
  - **#539**：**部分**覆盖 —— 缓存键与重挂路径已修；纯任务栏模式下的"布局不跳"还依赖 F05 的方向/坐标/占位修复 ⇒ **建议随 F05 合并后再关闭**。

## 3. 用例与命令（可从干净检出复现）

| 命令 | 目的 | 结果（发现数/通过/失败） |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | 构建 | **0 个错误**（294 警告，与基线同量级） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build` | 主测试 | 见 §3.1 |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/… --no-build` | GPU 非性能测试 | 见 §3.1 |
| `dotnet test tests/LiteMonitor.Updater.Tests/… --no-build` | 更新器测试 | 见 §3.1 |
| 新增用例 `TaskbarHostLifecycleTests`（12 条） | 失败→通过证明 | 见 §3.2 |

### 3.1 全量结果

| 测试工程 | 总数 | 通过 | 跳过 | 失败 |
|---|---|---|---|---|
| `LiteMonitor.Tests` | 1796 | 1789 | 7 | 0 |
| `LiteMonitor.GpuNonperformance.Tests` | 19 | 19 | 0 | 0 |
| `LiteMonitor.Updater.Tests` | 73 | 73 | 0 | 0 |

- 跳过数 7 与基线一致（`MarketIndexContractTests` 6 条 + `MarketE2ETests` 1 条，均为需要外网的用例）。
- **观察到的偶发项（如实记录）**：早一轮全量运行出现过 **1 条**失败（1786 通过 / 1 失败 / 7 跳过），
  随后的三轮全量运行（含两次带 TRX 的运行）**连续全绿**且 TRX 里 `outcome="Failed"` 为 0；
  该偶发项的用例名未能在当轮捕获，且**与本包改动无调用关系**
  （本包只触及任务栏路径，仓内除 `ThemeCheckScheduleTests` 外无任何既有用例调用任务栏代码）。

### 3.2 红→绿证明（临时回退实现 → 用例必须变红）

在**同一分支**上把六处修复临时改回旧行为（并在源码里打 `RED-STATE` 标记），重跑新增用例：

| # | 临时回退的修复 | 预期变红的用例 | 实测 |
|---|---|---|---|
| 1 | `TaskbarWinHelper.FindHandles` 恢复"目标缺失回退主屏" | `目标设备不存在时解析结果为空而不是回退到主屏` | 变红 ✔ |
| 2 | `TaskbarBizHelper.IsPanelAttached` 去掉"内层子句柄比对" | `保留根窗口但重建子层时挂载被判定为过期并重新挂载` | 变红 ✔ |
| 3 | `TaskbarWinHelper.GetTaskbarRect` 恢复"只看物理矩形"的缓存键 | `任务栏矩形缓存在显示器与工作区变化后不得复用旧坐标` | 变红 ✔ |
| 4 | `TaskbarRecoveryCoordinator.Pump` 屏蔽（等价"没有恢复调度"） | 父销毁恢复 / 目标屏等待 / 关闭不重建 / 100 次重建 / 退避节奏 / 事件合并 | 全部变红 ✔ |
| 5 | `EnsureRecoveryScheduled` 去掉"不抢占已排定退避"的守卫 | `定时巡检不得把退避节奏拉回高频` | 变红 ✔ |
| 6 | `TaskbarPanelVisibilityGate.ShouldSuppressShow` 恒为 false | `目标屏未就绪时面板的显示请求被推迟到挂载成功之后` | 变红 ✔ |

**红态汇总：12 条中 11 条变红**（唯一仍然通过的是结构性护栏 `任务栏窗体只持有一个定时器字段`，它本来就不依赖上述行为）。
恢复实现后重跑：**12/12 通过**。

## 4. 环境

- OS / 版本：Windows 11 专业版 26300（10.0.26300），x64
- DPI / 显示器拓扑：本机含虚拟显示器拓扑（多屏 + 非 100% 缩放）；**多屏/DPI 场景的判定用夹具（假显示器拓扑）+ 真实 Win32 窗口验证**，未在真实副屏上做端到端
- GPU / 驱动：双 NVIDIA（RTX 3080 / 3080 Ti）；本包不涉及 GPU 路径
- 关键依赖版本：LibreHardwareMonitorLib 0.9.6；.NET SDK 8.0.426
- .NET / 构建：`dotnet build -c Debug`，NuGet 走本地缓存 `G:\TOOL\LiteMonitor-fork\.nuget-packages`

## 5. 新增测试清单

文件：`tests/LiteMonitor.Tests/TaskbarHostLifecycleTests.cs`（**新增发现数 = 12**）
辅助：`tests/LiteMonitor.Tests/TaskbarWin32TestHost.cs`（真实 Win32 测试宿主，不含用例）

| # | 用例名 | 验证了什么 |
|---|---|---|
| 1 | `父任务栏窗口被销毁后面板被连带销毁且可恢复到新根窗口` | 真实 `DestroyWindow` 销毁父窗口 ⇒ 面板 HWND **一并销毁**（Windows 语义）；协调器在新根窗口出现后把面板重建并重新挂载（#553） |
| 2 | `保留根窗口但重建子层时挂载被判定为过期并重新挂载` | 根 HWND 仍有效但内层子句柄换了一个 ⇒ "只看根 HWND"会误判为正常；新判定必须识别过期并重挂（#553/#599 路径） |
| 3 | `目标屏未就绪时保持等待且不绑定主屏目标出现后自动挂载` | 目标设备缺失时进入 `WaitingForTaskbar`、**没有**被挂到主屏；目标屏出现后自动回到目标屏（#573/#583） |
| 4 | `恢复期间关闭则不再重建面板` | 关闭后恢复请求被丢弃、句柄不再重建（关闭优先） |
| 5 | `重复一百次宿主重建后面板单份且窗口句柄不增长` | 100 次宿主重建后面板仍是同一个窗体、进程窗口数不持续增长（无句柄泄漏） |
| 6 | `恢复期两秒一次持续十秒之后按低频退避等待且不永久停止` | 恢复期 2 秒×10 秒 → 期后退避且**永不停止**；用假时钟驱动（无 `Thread.Sleep`） |
| 7 | `多个来源的恢复事件被合并成一次恢复` | 显示/设置/电源/会话四类事件合并为**一次**恢复；之后按低频退避而非高频轮询 |
| 8 | `定时巡检不得把退避节奏拉回高频` | Tick 里的廉价存活检查**不得**抢占已排定的退避（否则"恢复有界"退化成高频轮询） |
| 9 | `目标屏未就绪时面板的显示请求被推迟到挂载成功之后` | 目标屏未就绪时面板**不得**以顶层窗口形态浮在桌面上；显示请求被推迟到挂载成功后兑现一次 |
| 10 | `目标设备不存在时解析结果为空而不是回退到主屏` | 复现 #573/#583 的同根因：旧实现回退主屏 ⇒ 句柄有效 ⇒ 永不重解析 |
| 11 | `任务栏矩形缓存在显示器与工作区变化后不得复用旧坐标` | 缓存键必须包含工作区/设备/HWND/DPI，否则切分辨率后复用过期坐标（#539） |
| 12 | `任务栏窗体只持有一个定时器字段` | 结构性护栏：面板与定时器保持**单份**（宿主重建不得产生第二份） |

## 6. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| 锁屏 → 解锁后面板自动回到任务栏（含数据恢复） | 真实 Win11 锁屏/解锁；需肉眼确认面板位置与数据 | 用户 |
| 睡眠/唤醒（含 Modern Standby） | 真实睡眠/唤醒循环 | 用户 |
| Explorer 重启（任务管理器 → 重启资源管理器） | 真实 Explorer 重启；确认面板在无 3 秒重建空档下恢复 | 用户 |
| RDP 接入/断开（副屏任务栏出现与消失） | 真实 RDP 会话；确认断开后自动回到副屏、接入期间面板**不落到主屏** | 用户 |
| 开机竞态（副屏任务栏延迟就绪） | ≥5 次冷启动，记录面板落屏 | 用户 |
| 真实分辨率切换（纯任务栏模式） | 改分辨率前后面板矩形/布局快照；确认不乱跳 | 用户 |
| 副屏**独立任务栏**（`Shell_SecondaryTrayWnd`）真实存在时的挂载 | 需要一台"副屏自带任务栏"的机器（本机副屏无独立任务栏） | 用户 |
| 真实 Win10 任务栏路径（挤占/方向/占位） | Win10 实机；且该路径归 F05 | 用户/F05 |
| 纯任务栏模式（主窗隐藏）下的恢复 | 真机：隐藏主窗后触发锁屏/解锁或 Explorer 重启，确认面板仍自动恢复<br>（代码侧已结构性保证：恢复调度在 `TaskbarForm` 自己的定时器里，与主窗可见性无关） | 用户 |
| 真实用户桌面上的视觉确认（面板不浮在桌面上、不闪、不跳位、不遮挡） | 真机目视（尤其"目标屏未就绪"时的面板消失行为） | 用户 |

## 7. 偏离与遗留

### 7.1 与派发单的偏离

1. **未移植上游 PR #495 的 `GetTaskbarRect` 几何修正**（`physHeightIsReasonable` 启发式、绕过 DPI 混算）。
   该改动属于"方向/坐标/占位"范畴，母计划把它划给 **F05**；本包只扩展了缓存键，**保留**原有坐标公式，避免与 F05 抢同一段公式。
2. **未移植上游 PR #555 的 `TaskbarStrategyWin10` 挤占跟踪状态重置** —— 该文件派发单**禁改**（归 F05）。
3. **上游 PR #495/#555 使用 `DateTime.Now` 判定恢复期**，违反拆解计划 §1.2 第 6 条（时间一律 `TimeProvider`/单调可注入）；
   本包改写为 `TimeProvider` + 假时钟驱动，**语义等价但可测**。
4. **Explorer 重启路径由"销毁 + 重建 TaskbarForm（延迟 3 秒）"改为"投递恢复请求"**：
   面板与定时器保持**单份**，不再有 3 秒空档。属计划第 4 步"合并 Explorer 重启事件"的直接要求。
5. **任务栏单文件列表**：派发单 §4 的「改动」清单只列了 4 个文件，本包另**新建** 1 个生产文件
   （`TaskbarRecoveryCoordinator.cs`，派发单明确"可新建单一任务栏恢复协调器文件"）与 2 个测试文件（新建文件允许）。
6. **交接说明中的 `src/UI/MainFormBizHelper.cs`**：拆解计划 §4 P-F04 明确把它列为**禁改**（归 F06），
   本包**未改**该文件；若协调者希望任务栏侧也动它，需在 F06 批次处理。

### 7.2 用户可见行为变更（需要写进发版说明）

1. **目标屏不可用时不再静默落到主屏**：指定了副屏而副屏任务栏未就绪时，面板进入"等待"（**不显示**，也不会浮在桌面上），
   副屏就绪后自动回到副屏。旧行为是悄悄跑到主屏并锁死（#573/#583 的根因）。
   ⇒ 若用户配置的副屏**本来就没有独立任务栏**（Windows 11 默认副屏没有独立任务栏），
   面板将不再出现在主屏 —— 这是 #583 报告者明确要求的行为，但属于**用户可感知**的变化，发版说明必须写。
2. **Explorer 重启不再重建任务栏窗体**（不再有 3 秒空档）。

### 7.3 自审中发现并修复的缺陷（各带一条回归用例）

| 缺陷 | 后果 | 回归用例 |
|---|---|---|
| Tick 里的存活检查用"普通恢复请求"，每个 Tick 都会刷新恢复窗口 | 恢复期 2 秒×10 秒结束后会被拉回高频，退避失效（违反"恢复必须有界"） | 用例 8 |
| 目标屏未就绪时面板未被任何任务栏持有，却仍被 `Show()` | 面板以**顶层窗口**形态浮在桌面上 | 用例 9 + 可见性闸门 `TaskbarPanelVisibilityGate` |
| 面板 HWND 被外部销毁后重建 | 新句柄上的**分层样式（颜色键）与扩展样式随旧句柄一起丢失** ⇒ 面板变黑/穿透异常 | `AttachToTarget` 中重放 `ApplyLayeredStyle` |

### 7.4 发现的相邻问题（不属于本包，建议归属）

| 问题 | 建议归属 |
|---|---|
| `TaskbarStrategyWin11.SetPosition` 恒带 `SWP_NOZORDER`，同级 z-order 无维护（#599） | **F06**（本包只在**重挂**时抬一次，不做周期维护） |
| Win10 挤占记账 ±1px 宽度猜测导致任务列表被逐次压缩（#568） | **F05** |
| `TaskbarBizHelper.UpdatePlacement` 的 `timeWidth = 90` 硬编码不随 DPI/字体变化 | **F05** |
| 任务栏窗无 `OnDpiChanged`（DPI 变化只重挂，不重算字体/尺寸） | **F05**（本包只在 DPI 变化时投递恢复请求） |
| 目标屏"暂不可用"没有托盘状态承载（#583 报告者希望能看到"不可用"状态） | 需新增托盘状态文案（超本包范围，未做） |

### 7.5 建议协调者处理

1. **CI 阈值**：本包新增 **12** 条用例 ⇒ 批次集成时请把 `.github/workflows/ci.yml` / `release.yml` 的
   `MIN_TEST_TOTAL` 按实际发现数统一上调（**本包未改 CI 文件**）；`MAX_SKIPPED_TESTS` 不得放宽。
2. **合并冲突预期**：`TaskbarWinHelper.cs` / `TaskbarBizHelper.cs` / `TaskbarForm.cs` 三处与 F05/F06 有重叠面
   （本包新增了显示器拓扑/任务栏窗口来源两个接缝、缓存键扩展、`FindHandles` 不回退主屏）。
   建议合并序：**先并 F04，再并 F05/F06**；冲突点集中在 `GetTaskbarRect` 与 `UpdatePlacement`。
3. **F05 需接手的坐标修正**：PR #495 的 DPI 混算规避（`GetTaskbarRect` 场景 A/B/C 判定）尚未移植。
4. **`MainFormBizHelper.cs` 归属澄清**：见 §7.1 第 6 条。
5. **发版说明需写入 §7.2 的两条用户可见行为变更**（目标屏不可用时面板不显示；Explorer 重启不再重建任务栏窗体）。
