# F06 验收记录 —— 同级 Z 序与主窗置顶分别维护

> 依据：`docs/plans/2026-10-25-verified-bugs-remediation-plan.md` §4 F06 / §5（母计划）与
> `docs/plans/2026-10-25-verified-bugs-remediation-subagent-plan.md` §4 P-F06（拆解计划）、
> 派发单 `docs/plans/dispatch/P-F06.md`。
> 原则：只记录**真实执行过**的命令与观察到的结果；未实机验证的场景逐条列入"待实机清单"，不得省略。
> **代码层验收通过 ≠ 可关闭 issue**；关闭条件见母计划 §7。

## 1. 标识

- 工作包：**P-F06**
- 分支：`fix/F06-zorder-topmost`（worktree：`G:\TOOL\LiteMonitor-fork\.worktrees\f06`）
- 基线 SHA（开工时 HEAD）：`19cc9ac`
- 完成日期：2026-10-25
- 执行者：P-F06 执行智能体（Worker 会话 `mvs_8cc697a8c99f4f5cac4b416d7ae4c75d`）
- 执行机：Windows 11（**单显示器**；多显示器 / 虚拟桌面 / 真实全屏均属"待实机"，见 §6）

### 1.1 提交列表

`git log --oneline 19cc9ac..fix/F06-zorder-topmost`：

| SHA | 说明 |
|---|---|
| `405b5f3` | `test(F06): 同级 Z 序与主窗置顶看门狗用例（新增 13 条）`（2 个新增测试文件，先写的红测试） |
| `ab2b5f5` | `fix(F06): 同级 Z 序与主窗置顶分别维护（#599、#612）`（6 个文件，+406 / −11） |

**未推送远端，未动 `master`**。

## 2. 覆盖与结论

- 覆盖 issue：**#599**（任务栏面板被同级合成层盖住后不恢复）、**#612**（主窗置顶看门狗在隐藏时停摆 / 只看样式位）
- 结论：**代码层验收通过**（两条 issue 各有红→绿证据，见 §4）
- 上游参考：PR **#600** head `e4ed1bd`（Open 非 Draft mergeable，直接修复，本包未按它落地而是按派发单重写）；
  PR **#373** head `1c279c6`（Open **CONFLICTING**，仅参考其"置顶恢复"意图，手工适配 —— 并且本包**额外**锁死了它的反面边界：
  用户主动关闭置顶后看门狗不得反向打开，见 §5 用例 8）
- 允许关闭的 issue 与条件：
  - **#599**：**代码层满足关闭条件** —— 真实 Win32 宿主下"新建可见合成兄弟窗口 → 下一维护周期恢复"已被锁死（§5 用例 1）。
    **真实显卡驱动热更新 / Explorer 重建子层仍需实机确认**（§6）。
  - **#612**：**代码层满足关闭条件（仅"隐藏时停摆"这一半）** —— `!_form.Visible` 早退已移除（§5 用例 7）。
    **"样式位在、实际层级已掉"这一半只有替身驱动证据，降级为待实机**（§3.3、§6）。
    **纯任务栏模式与虚拟桌面切换仍需实机确认**（§6）。

## 3. 改动清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/UI/Helpers/TaskbarStrategyWin11.cs` | 修改 | 新增 `MaintenanceFollowUps = 3`（:45）、`RequestDisplayChangeCheck`（:94）、`PumpZOrderMaintenance`（:105）、`EnsureSiblingZOrder`（:117）；`SetPosition` 末尾排定配额并按需维护（:166）。**定位仍恒带 `SWP_NOZORDER`** |
| `src/UI/Helpers/TaskbarWinHelper.cs` | 修改 | 新增 `GW_HWNDPREV` / `GetWindow` P/Invoke、`RaiseSiblingToTop`（:609）、`IsCoveredByVisibleSibling`（:622）、`RaiseSiblingToTopIfCovered`（:637）、`RequestDisplayChangeCheck` / `PumpZOrderMaintenance` 转发 |
| `src/UI/TaskbarForm.cs` | 修改 | 新增 `NotifyDisplayChanged`（:171）；`Tick()` 内在 `_recovery.Pump()` 之后调用 `PumpZOrderMaintenance()`（:387） |
| `src/UI/MainForm_Transparent.cs` | 修改 | `WM_DISPLAYCHANGE`（:573）/ `WM_SETTINGCHANGE`（:589）/ `OnDpiChanged`（:647）三处各加 `_taskbar?.NotifyDisplayChanged();`；新增 `IsAutoHidden` 转发（:100） |
| `src/UI/Helpers/MainFormBizHelper.cs` | 修改 | 新增 `ITopMostSuppressionProvider`（:20）/ `TopMostSuppressionProviderBase`（:32）/ `DefaultTopMostSuppressionProvider`（:48）；新增 `SetTopMostSuppressionProvider`（:172）与 `MaintainTopMost`（:189，**移除 `!_form.Visible` 早退**）；`TopMostTick`（:225）改为转调 |
| `src/UI/Helpers/MainFormWinHelper.cs` | 修改 | `IsTopMostStyleApplied` / `RefreshTopMost` 改 `virtual`；新增 `IsTopMostLayeringEffective`（:101）、`IsForegroundCoveringMonitor`（:140）、`LayeringScanLimit = 64`（:437）与相关 P/Invoke |
| `tests/LiteMonitor.Tests/TaskbarSiblingZOrderTests.cs` | **新增** | 6 条（真实 Win32 宿主） |
| `tests/LiteMonitor.Tests/TaskbarTopMostWatchdogTests.cs` | **新增** | 7 条（真实产品代码 + 可注入闸门） |

**未改动（禁改清单核对）**：`src/UI/Helpers/TaskbarStrategyWin10.cs`（派发单禁改）、
`.github/workflows/ci.yml`、`release.yml`、`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、
`src/UI/UIController.cs`、`src/System/HardwareMonitor.cs`、网络 / 硬件相关文件、
`src/UI/Helpers/TaskbarRecoveryCoordinator.cs`（F04 文件，只读引用）、
`tests/LiteMonitor.Tests/PluginMarketRecoveryBoundaryTests.cs`（见 §7.2）。

### 3.1 #599 的修法与为什么不写成 TOPMOST

| 决策 | 理由 |
|---|---|
| Z 序维护**只用 `HWND_TOP`** | 面板是任务栏的**子窗口**，`HWND_TOP` 的语义就是"同级最前"。`HWND_TOPMOST` 会把面板从任务栏层级里拔出来，变成压住整个桌面的顶级置顶窗口 —— 派发单明确禁止。用例 2 直接断言面板仍是任务栏子窗口且无 `WS_EX_TOPMOST` |
| 定位**仍** `SWP_NOZORDER`，Z 序在定位之后**按需**单独维护 | 不能因为移动就把面板乱插（会与任务栏自己的合成层互相抢占）。先判 `IsCoveredByVisibleSibling` 再动 |
| 遮挡判定只沿 `GW_HWNDPREV` 链 | 这是"同级"Z 序的真实定义；**不遍历系统窗口、不每帧无条件置顶、不反复重挂钩子**（母计划 F06 第 2 步 / 拆解计划 §1.2） |
| 后续检查做成**有界配额** `MaintenanceFollowUps = 3` | 合成层可能**迟到**（驱动热更新、Explorer 异步重建子层）。配额由定位 / 显示变化排定，`PumpZOrderMaintenance()` 消费，用完即止 ⇒ 不会退化成无限重试 |

### 3.2 #612 的修法与三条边界

| 决策 | 理由 |
|---|---|
| `MaintainTopMost` **移除 `!_form.Visible` 早退** | 这是 #612 的根因：置顶是**窗口属性**，不是可见性属性。纯任务栏模式（用户隐藏主面板）下旧看门狗完全停摆，等用户重新显示主窗时置顶已经是错的 |
| 改为**双判**：样式位 + 实际可见层级 | 旧实现只读 `WS_EX_TOPMOST` 样式位，漏掉"样式位在、但层级已经掉下去"。新增 `IsTopMostLayeringEffective` 沿 `GW_HWNDPREV` 链扫（上限 64），遇到**可见且非置顶**的窗口即判失效 |
| 链上其它**置顶**窗口不算问题 | 强行把它们压下去就是"抢其它置顶程序的 Z 序"，规格不允许 |
| **让路**规则（菜单 / 自动隐藏 / 前景全屏） | 规格要求"尊重自动隐藏、菜单与全屏行为，不压安全桌面"。收起状态下重插还会把本该藏起来的边缘露出来 |
| **单向** | `cfg.TopMost == false` 时只摘残留样式位，绝不反向打开 —— 恢复的是"用户要的置顶"，不是"看门狗想要的置顶" |

### 3.3 关键限制：为什么"层级异常"这一支只能用替身驱动

本包开工时写了 8 个临时 Win32 探针（已全部删除），实测到两条无法绕过的 Windows 事实：

1. **`WS_EX_TOPMOST` 不能用 `SetWindowLong(GWL_EXSTYLE)` 写回**（实测写后样式位仍为 False），只能靠 `SetWindowPos(HWND_TOPMOST)`。
2. `SetWindowPos(A, hNormal)` 会**顺带清掉** A 的置顶样式位 ⇒ 无法用普通窗口制造"样式位在、但层级却掉下去"。

即：Windows 会同步维护"样式位"与"实际层级"二者，**在自动化测试里无法构造出二者的不一致态**。
因此 `Watchdog_RestoresLayering_WhenStyleSetButRealOrderLost` 用可替换的 `virtual IsTopMostLayeringEffective` 驱动，
断言的是"看门狗**确实调用**了层级判定、且判定失效时**确实会**重插"这一分派逻辑，
而**不是**复制算法自证。**真实驱动热更新导致层级掉落的场景降级为待实机**（§6）。

## 4. 红→绿证明（临时回退实现 → 用例必须变红）

做法同 F04：在**同一分支**上保留 API 外壳、临时把内部行为改回旧实现（源码里打 `RED-STATE` 标记），
重跑新增用例；取证完成后逐项改回（已用 `Select-String` 确认 `RED-STATE` 残留数 = 0）。

| # | 临时回退的修复 | 预期变红的用例 | 实测 |
|---|---|---|---|
| R1 | `MaintainTopMost` 恢复 `if (!_form.Visible) return;`（#612 早退） | `Watchdog_RestoresTopMost_WhenMainFormHidden` | 变红 ✔（7 条中 1 红 / 6 绿） |
| R2 | 双判退回"只看样式位"（去掉 `IsTopMostLayeringEffective`） | `Watchdog_RestoresLayering_WhenStyleSetButRealOrderLost` | 变红 ✔（7 条中 2 红 / 5 绿） |
| R3 | 去掉让路规则（屏蔽 `_suppression.ShouldSuppressZOrderReinsert`） | `Watchdog_SkipsReinsert_WhenAutoHidden` / `WhenMenuOpen` / `WhenForegroundFullscreen` | 3 条全变红 ✔（7 条中 5 红 / 2 绿） |
| R4 | `EnsureSiblingZOrder` 整体空转（等价旧实现"全仓无同级 Z 序维护"） | `Maintenance_RestoresZOrder_AfterVisibleSiblingCoversPanel`、`Maintenance_FollowUpChecks_CoverLateCompositionLayer`、`Maintenance_DisplayChangeCheck_IsImmediate` | 3 条变红 ✔（6 条中 3 红 / 3 绿） |
| R5 | `PumpZOrderMaintenance` 去掉配额（改成无限后续检查） | `Maintenance_FollowUpChecks_AreBounded` | 变红 ✔（6 条中 1 红 / 5 绿） |

**红态汇总**：13 条中 **8 条**在至少一轮回退里变红。仍全绿的 5 条是护栏型用例
（`Maintenance_NeverPromotesPanelToTopMost`、`Maintenance_DoesNothing_WhenPanelDetached`、
`Watchdog_NeverReenablesTopMost_AfterUserTurnedItOff`、`Watchdog_IsIdempotent_WhenStateAlreadyCorrect`
—— 以及 R1/R2 轮次下尚未被触动的 `Maintenance_*`），它们锁定的是"不得越界"的边界，
本来就不依赖上述修复是否存在。

**恢复实现后重跑**：`TaskbarSiblingZOrderTests` **6/6 通过**，`TaskbarTopMostWatchdogTests` **7/7 通过**。

### 4.1 取证过程中的一处自我纠错

R5 轮次首次运行时，`Maintenance_RestoresZOrder_AfterVisibleSiblingCoversPanel` **意外**也变红。
排查发现不是实现问题，而是我在插入 `RED-STATE-R5` 标记时**误删**了 `SetPosition` 末尾的
`_followUpChecks = MaintenanceFollowUps; EnsureSiblingZOrder();` 两行。
已恢复这两行后重跑，R5 只剩 `Maintenance_FollowUpChecks_AreBounded` 一条红 ——
这才是"去掉有界配额"的**精确**影响面。如实记录，避免把本轮数据误读成"R5 影响两条"。

## 5. 新增测试清单

### 5.1 `tests/LiteMonitor.Tests/TaskbarSiblingZOrderTests.cs`（6 条）

★ 取证方式：**真实 Win32 窗口**。根窗口由 `TaskbarWin32TestHost` 创建（模拟任务栏），面板是真实 WinForms 窗体，
合成兄弟窗口是真实的 `WS_CHILD` HWND；Z 序真值一律用 `GetWindow(GW_HWNDPREV)` 读取，**不**复制产品算法自证。

| # | 用例 | 验证了什么 |
|---|---|---|
| 1 | `Maintenance_RestoresZOrder_AfterVisibleSiblingCoversPanel` | **#599 主证**：迟到的可见合成层压到面板前之后，下一次维护把面板恢复到同级最前，且**矩形与前台窗口都不变**（不移动 / 不改大小 / 不激活） |
| 2 | `Maintenance_NeverPromotesPanelToTopMost` | 维护后面板**仍是任务栏子窗口**且无 `WS_EX_TOPMOST` —— 同级 Z 序不得写成顶级窗口 TOPMOST |
| 3 | `Maintenance_FollowUpChecks_AreBounded` | 配额耗尽后连跑 50 次维护也不再有任何 Z 序动作（**有界性**：不无限重试、不每帧无条件置顶） |
| 4 | `Maintenance_FollowUpChecks_CoverLateCompositionLayer` | 合成层在定位**之后**才出现时，配额内的下一次维护仍把它压回去 |
| 5 | `Maintenance_DisplayChangeCheck_IsImmediate` | 显示变化**立即**检查一次，不等下一个定位周期 |
| 6 | `Maintenance_DoesNothing_WhenPanelDetached` | 面板脱离任务栏（父窗口不是任务栏）时 Z 序维护停手 —— 此时它是顶层窗口，重排会误伤 |

### 5.2 `tests/LiteMonitor.Tests/TaskbarTopMostWatchdogTests.cs`（7 条）

★ 取证方式：**真实产品代码** `MainFormBizHelper` / `MainFormWinHelper`，只把"是否允许重排 Z 序"的闸门做成可注入
（全屏 / 安全桌面 / 菜单 / 自动隐藏无法在测试进程里稳定复现）。

| # | 用例 | 验证了什么 |
|---|---|---|
| 7 | `Watchdog_RestoresTopMost_WhenMainFormHidden` | **#612 主证**：主窗 `Visible == false`（纯任务栏模式）下看门狗**照常**把样式位补回来。用反射驱动**现有的**私有 `TopMostTick`，因此它能在改动**之前**编译运行并如实变红 |
| 8 | `Watchdog_NeverReenablesTopMost_AfterUserTurnedItOff` | 用户关闭置顶后，跑 10 个维护周期也只摘不重开（PR #373 意图最容易写成的那个反面 bug） |
| 9 | `Watchdog_RestoresLayering_WhenStyleSetButRealOrderLost` | 样式位在、层级失效时看门狗会重插（双判的那一半）。**替身驱动**，理由见 §3.3 |
| 10 | `Watchdog_SkipsReinsert_WhenAutoHidden` | 自动隐藏收起时让路；**让路解除后照常恢复**（让路是暂时的，不是永久停摆） |
| 11 | `Watchdog_SkipsReinsert_WhenMenuOpen` | 真实 `ContextMenuStrip` 打开时不重插（真实菜单，非替身） |
| 12 | `Watchdog_SkipsReinsert_WhenForegroundFullscreen` | 前景整屏（含安全桌面）时让路。**替身驱动**：测试进程里 `Form.Activate()` 不改变系统前台窗口（实测 `GetForegroundWindow()` 不指向被激活窗体） |
| 13 | `Watchdog_IsIdempotent_WhenStateAlreadyCorrect` | 状态已正确时跑 20 个周期不抖动、不翻转 |

**统计**：新增 13 条（供协调者上调 `MIN_TEST_TOTAL`）。
全程无 `Thread.Sleep` —— 维护动作由测试直接驱动，不依赖 WinForms 定时器。

## 6. 未覆盖场景（待实机清单）

| # | 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|---|
| 1 | **真实驱动热更新**（显卡驱动升级 / 重启合成器）后面板被盖住 ⇒ 是否恢复 | 真实多显卡环境 + 触发驱动热更新 | 用户 |
| 2 | **Explorer 重建子层**（重启 Explorer / 任务栏崩溃恢复）后的同级 Z 序 | 真实环境重启 Explorer | 用户 |
| 3 | **纯任务栏模式**（隐藏主面板）下置顶是否长期保持 | 真实开启纯任务栏模式并跨锁屏 | 用户 |
| 4 | **虚拟桌面切换**后任务栏面板 Z 序与置顶 | 多虚拟桌面 | 用户 |
| 5 | **全屏进出**（全屏游戏 / 视频 / 锁屏 / 安全桌面）时是否压住前者 | 真实全屏程序；UAC 安全桌面 | 用户 |
| 6 | "样式位在、实际层级已掉"的**真实**复现与恢复 | 见 §3.3 —— 自动化里无法构造该状态 | 用户 |
| 7 | **多显示器 / 混合 DPI**下的 Z 序与置顶 | 多显示器且 DPI 不同 | 用户 |
| 8 | **第三方任务栏**（StartAllBack / ExplorerPatcher 等）下的同级 Z 序 | 安装第三方任务栏 | 用户 |

> 说明：本包执行机为**单显示器**，且自动化测试无法获得真实系统前台焦点与真实驱动事件，
> 因此上表 8 项全部未能实机验证 —— 按总则"未实机验证的场景逐条列入，不得省略"如实登记。

## 7. 偏离与遗留

### 7.1 与派发单的偏离

- **无禁改清单例外申请**。所有改动落在派发单授权范围内：`TaskbarStrategyWin11.cs`、
  `TaskbarWinHelper.cs`、`MainFormBizHelper.cs`、`MainFormWinHelper.cs`，以及
  `TaskbarForm.cs` / `MainForm_Transparent.cs` 的**挂钩点**（派发单允许的最小编辑）。
- 派发单禁改的 `TaskbarStrategyWin10.cs`、网络 / 硬件文件**未触碰**。
- 上游 PR #600 未按原样落地：本包按派发单第 1–4 点重写，并把"有界配额"（第 2 点）做成常量
  `MaintenanceFollowUps = 3` 以便测试直接引用，避免"测试复制一份配置、两边悄悄跑偏"。

### 7.2 工作区的一处**非本包**改动（如实记录）

`tests/LiteMonitor.Tests/PluginMarketRecoveryBoundaryTests.cs` 在本包开工时显示 `M`。
核查结论：**内容无实质改动**，是行尾差异（该文件索引入库为 LF、工作区为 CRLF，
`git diff --ignore-cr-at-eol` 与 `git diff --ignore-all-space` 均为空）。
该文件**未**纳入本包的两个提交。开工时曾用 `git checkout --` 还原过一次，还原后仍显示 `M`，
属仓库 `.gitattributes`（`*.cs text eol=crlf`）下的预期表现。

### 7.3 临时探针的处置

本包为摸清 Win32 事实写了 8 个临时探针文件（`F06TempProbe.cs` … `F06TempProbe8.cs`），
全部位于 `tests/LiteMonitor.Tests/`。**已全部删除**（用 `mavis-trash` 按绝对路径回收），
`git status` 确认无残留。它们不参与任何提交。
（记录一条环境事实：`mavis-trash` 用**相对路径**会被本地安全策略拒绝，必须用绝对路径。）

### 7.4 验证命令与实测结果

| 命令 | 结果 |
|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | **0 个错误**（`error CS` 计数 = 0） |
| `dotnet test … --filter "FullyQualifiedName~TaskbarSiblingZOrderTests"` | 通过 6 / 失败 0 / 跳过 0 |
| `dotnet test … --filter "FullyQualifiedName~TaskbarTopMostWatchdogTests"` | 通过 7 / 失败 0 / 跳过 0 |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build`（**全量**） | **通过 2054 / 失败 0 / 跳过 7**，耗时 2 m 33 s |

全量的 7 条跳过全部是 `MarketIndexContractTests`（6 条）+ `MarketE2ETests`（1 条），
均需外网，与本包改动无调用关系 —— 与基线一致。

### 7.5 发现的相邻问题（不属于本包，建议归属）

- `TaskbarStrategyWin11.SetPosition` 的 `else` 分支（面板意外脱离时）用的是
  `SetWindowPos(..., HWND_TOPMOST, ...)`（现 `TaskbarStrategyWin11.cs:158`）。
  这是**既有**行为、不在本包改动范围，但它与本包"面板不得被写成顶级 TOPMOST"的语义**相反**
  —— 脱离态下面板会变成压住整个桌面的顶级置顶窗口。建议协调者裁决是否归入后续工作包
  （可能的修法：脱离时改为先尝试重挂，重挂失败再隐藏而非置顶）。
- `MainFormWinHelper.IsTopMostLayeringEffective` 的扫描上限 `LayeringScanLimit = 64` 是经验值；
  若系统 Z 序链上有很多可见非置顶窗口（罕见），理论上存在"扫到上限就判有效"的边界。
  当前按"无法证明异常 ⇒ 按有效处理"兜底，避免误判导致反复重插。
