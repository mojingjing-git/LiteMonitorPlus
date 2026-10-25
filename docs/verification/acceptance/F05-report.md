# F05 验收记录 —— 任务栏方向、坐标与占位幂等

> 依据：`docs/plans/2026-10-25-verified-bugs-remediation-plan.md` §4 F05（母计划）与
> `docs/plans/2026-10-25-verified-bugs-remediation-subagent-plan.md` §4 P-F05（拆解计划）、
> 派发单 `docs/plans/dispatch/P-F05.md`。
> 原则：只记录**真实执行过**的命令与观察到的结果；未实机验证的场景逐条列入"待实机清单"，不得省略。
> **代码层验收通过 ≠ 可关闭 issue**；关闭条件见母计划 §7。

## 1. 标识

- 工作包：**P-F05**
- 分支：`fix/F05-taskbar-geometry`（worktree：`G:\TOOL\LiteMonitor-fork\.worktrees\f05`）
- 基线 SHA（开工时 HEAD）：`2300af6`（`Merge branch 'integration/w1'`，含已合并的 P-F04）
- 提交 SHA 列表：见 §2.1（本文档随最后一次提交一起入库）
- 完成日期：2026-10-25
- 执行者：P-F05 执行智能体（Worker 会话 `mvs_8448dfc4ad5e4dac9d8541a77ab68c5d`）
- 执行机：Windows 11（本机**不是** Win10 ⇒ Win10 真机方向切换属"待实机"）

## 2. 覆盖与结论

- 覆盖 issue：**#568、#539、#573、#583**
- 结论：**代码层验收通过**（#568 / #539 有代码层证据；#573 / #583 的根因修复由 P-F04 落地，本包补齐其几何前提）
- 允许关闭的 issue 与条件：
  - **#568**（任务栏位置改变且重叠）：**代码层满足关闭条件** —— 显式方向 + 占位基线/占位量模型 + 坐标契约，
    1200→640 / 600→900 场景已被用例锁死（红→绿见 §4）。**真机 Win10 四方向切换仍需实机确认**（§6）。
  - **#539**（纯任务栏模式切分辨率后位置/布局乱跳）：**代码层满足关闭条件** —— 几何缓存键扩到 13 个分量，
    任一变化整体失效；方向也进入键内。**切分辨率真机复测仍需实机确认**（§6）。
  - **#573 / #583**：根因（目标缺失回退主屏）由 P-F04 关闭；本包负责"几何正确性"这一前提
    （方向/坐标/缓存键），**不单独关闭这两个 issue**，见 §7.2。

### 2.1 提交列表

`git log --oneline 2300af6..fix/F05-taskbar-geometry`：

| SHA | 说明 |
|---|---|
| `b5f7ade` | `fix(F05): 任务栏显式方向 + 坐标契约 + 占位幂等`（4 个授权文件 + 2 个新增生产文件） |
| `02d29f0` | `test(F05): 任务栏方向/坐标/占位幂等用例（新增 23 条）`（1 个新增测试文件 + 测试宿主增量） |
| `e5642b7` | `docs(F05): 任务栏方向/坐标/占位幂等验收记录`（本文件） |

**未推送远端，未动 `master`**（分支 `fix/F05-taskbar-geometry`，基线 `2300af6`）。

## 3. 改动清单

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/UI/Helpers/TaskbarGeometry.cs` | **新增** | 显式 `TaskbarDockDirection`（Top/Bottom/Left/Right/Unknown）、`TaskbarCompatibility`（原生 / 第三方待验 / 未知）、`TaskbarGeometry` 快照、**13 分量** `TaskbarGeometryKey`、纯函数 `TaskbarDockResolver.Resolve`（按任务栏相对所属显示器的物理矩形解析方向） |
| `src/UI/Helpers/TaskbarReservationModel.cs` | **新增** | 「基线 + 本次占位量」占位模型（`TaskbarReservationModel`）+ 基线身份 `TaskbarReservationToken`；幂等 / 不得二次扣减 / 撤销语义全部集中在此，**纯逻辑可单测** |
| `src/UI/Helpers/TaskbarWinHelper.cs` | 修改 | 几何缓存从"一个矩形"升级为"一份快照"（键含根/子 HWND、显示器、屏幕/工作区、DPI、方向、面板尺寸、任务栏设置）；新增 `GetGeometry` / `SetPlacementContext` / `TryScreenToClient` / `TryScreenRectToClient`（**唯一坐标转换出口**）；删除 48px 高度启发式 |
| `src/UI/Helpers/TaskbarStrategyWin10.cs` | 修改（重写） | 方向改为显式枚举传入（不再宽高比猜）；占位改为基线模型；坐标全部在父（ReBar）客户区；回退定位路径显式 `ScreenToClient`；撤销旧占位（禁用/退出/重挂/对齐切换） |
| `src/UI/Helpers/TaskbarStrategyWin11.cs` | 修改 | 方向显式化 + 第三方方向标注"兼容性待验"；坐标转换改用显式 `ScreenToClient`（不再"屏幕坐标 − 父窗口矩形原点"近似换算） |
| `src/UI/Helpers/TaskbarBizHelper.cs` | 修改 | `IsVertical()` 改用显式方向；**有效对齐只算一次**（修 #568 的"分支与策略标志相反"根因）；Win10 水平分支**两种对齐都走策略内部占位**；时钟预留区按任务栏 DPI 换算物理像素 |
| `tests/LiteMonitor.Tests/TaskbarGeometryTests.cs` | **新增** | 23 条用例（纯逻辑 + 真实 Win32 宿主） |
| `tests/LiteMonitor.Tests/TaskbarWin32TestHost.cs` | 修改（**纯增量**） | 新增 `CreateSlotChild`（模拟 `Shell_TrayWnd → ReBarWindow32 → MSTaskSwWClass` 两级结构）；**未改动** F04 已有方法 |

**未改动（禁改清单核对）**：`src/UI/TaskbarForm.cs`（归 F04 已合并区域）、`src/UI/Helpers/MainFormBizHelper.cs`（归 F06）、
`.github/workflows/ci.yml`、`release.yml`、`src/Core/Settings.cs`、`src/Core/Actions/AppActions.cs`、`src/UI/UIController.cs`、
`src/System/HardwareMonitor.cs`、`resources/lang/*.json`、`src/UI/Helpers/TaskbarRecoveryCoordinator.cs`（F04 新增文件，**只读引用**）。

## 4. 证据锚点（真实文件:行号）

### 4.1 显式方向（替代宽高比猜）

| 事实 | 锚点 |
|---|---|
| 方向枚举 `TaskbarDockDirection`（Top/Bottom/Left/Right/Unknown） | `src/UI/Helpers/TaskbarGeometry.cs:15` |
| 方向解析纯函数（几何 → 工作区 → Unknown 三级，**不含任何宽高比启发式**） | `src/UI/Helpers/TaskbarGeometry.cs:300` |
| 兼容性判定（原生只提供 Bottom；Top/Left/Right = 第三方待验） | `src/UI/Helpers/TaskbarGeometry.cs:355`（`ResolveCompatibility`） |
| 业务层不再用"任务栏矩形宽高比"判垂直 | `src/UI/Helpers/TaskbarBizHelper.cs:262`（`IsVertical()`） |
| Win10 策略不再用 `rcBar.Height > rcBar.Width` 猜方向 | `src/UI/Helpers/TaskbarStrategyWin10.cs:146`（`TryAdjustLayout`，方向来自入参） |
| 旧实现的宽高比猜测位置（对照） | 改前 `TaskbarStrategyWin10.cs:117`（`bool isVertical = rcBar.Height > rcBar.Width;`）、`TaskbarBizHelper.cs:212`（`_taskbarRect.Height > _taskbarRect.Width`）——**均已删除** |

### 4.2 坐标契约

| 事实 | 锚点 |
|---|---|
| 坐标转换唯一出口（屏幕物理像素 → 父客户区） | `src/UI/Helpers/TaskbarWinHelper.cs:591`（`TryScreenToClient`）、`:609`（`TryScreenRectToClient`） |
| 策略接口注释标注坐标空间 | `src/UI/Helpers/TaskbarWinHelper.cs:29-36`（`ITaskbarStrategy.SetPosition` 的 `<param>`/`<para>`） |
| Win10 策略把几何换算到父客户区 | `src/UI/Helpers/TaskbarStrategyWin10.cs:164`（父）、`:192`（任务列表） |
| Win10 回退定位路径显式转换（改前直接把屏幕坐标当客户区坐标） | `src/UI/Helpers/TaskbarStrategyWin10.cs:99` |
| Win11 策略显式转换 | `src/UI/Helpers/TaskbarStrategyWin11.cs:76` |
| 业务层产出的仍是**物理屏幕坐标**，转换责任在策略 | `src/UI/Helpers/TaskbarBizHelper.cs:350-373` |

### 4.3 几何缓存键（13 分量，任一变化整体失效）

| 分量 | 锚点 |
|---|---|
| 键定义与相等性 | `src/UI/Helpers/TaskbarGeometry.cs:156`（`TaskbarGeometryKey`） |
| 根 HWND + 策略内部子宿主 HWND（代次标识） | `src/UI/Helpers/TaskbarGeometry.cs:176-190`；子宿主由 `TaskbarWinHelper.cs:52`（接口）/ `TaskbarStrategyWin10.cs:49`（ReBar）/ `TaskbarStrategyWin11.cs:41`（任务栏根）提供 |
| 目标显示器 / 屏幕矩形 / 工作区矩形 / DPI / 方向 | `src/UI/Helpers/TaskbarGeometry.cs:192-210` |
| 面板尺寸 / 有效对齐 / 手动偏移 / 系统居中 / 小组件宽 | `src/UI/Helpers/TaskbarGeometry.cs:212-226` |
| 缓存读写处 | `src/UI/Helpers/TaskbarWinHelper.cs:393`（`GetGeometry`）、`:437-455`（建键 + 比键）、`:459-474`（建快照 + 写缓存） |
| 定位上下文统一维护（避免两个入口互相顶掉缓存） | `src/UI/Helpers/TaskbarWinHelper.cs:256`（`SetPlacementContext`）+ `TaskbarBizHelper.cs:235`/`:285` |
| 缓存失效入口（F04 已有） | `src/UI/Helpers/TaskbarWinHelper.cs:246`（`InvalidateTaskbarRectCache`） |

### 4.4 占位幂等（基线 + 本次占位量）

| 事实 | 锚点 |
|---|---|
| 占位模型（基线 / 本次占位量 / 已施加） | `src/UI/Helpers/TaskbarReservationModel.cs:116` |
| 计算应施加矩形（幂等 + 系统改动时**先扣除占位量**） | `src/UI/Helpers/TaskbarReservationModel.cs:158`（`TryCompute`）、`:249`（`DeriveBaseline`） |
| 记录已施加（供下一次 Tick 比较） | `src/UI/Helpers/TaskbarReservationModel.cs:219`（`Commit`） |
| 撤销占位（交回基线） | `src/UI/Helpers/TaskbarReservationModel.cs:230`（`TryTakeBaseline`） |
| 基线身份（句柄/方向/父客户区长度/模式） | `src/UI/Helpers/TaskbarReservationModel.cs:42`、`:83`（`IsSameLayout`） |
| 策略侧：令牌变化 ⇒ 撤销旧占位 / 重取基线 | `src/UI/Helpers/TaskbarStrategyWin10.cs:169-187` |
| 策略侧：**先撤销、后读任务列表几何**（读早了就拿到旧值） | `src/UI/Helpers/TaskbarStrategyWin10.cs:189-193` |
| 策略侧：施加（一致则不动窗口） | `src/UI/Helpers/TaskbarStrategyWin10.cs:201-207` |
| 策略侧：撤销（禁用/退出/重挂） | `src/UI/Helpers/TaskbarStrategyWin10.cs:226`（`UndoReservation`）、`:108`（`Restore`） |
| 旧实现的 ±1px 宽度猜测（对照，已删除） | 改前 `TaskbarStrategyWin10.cs:203`（`isSqueezed = ... <= GAP_TOLERANCE`）——**已删除** |

### 4.5 #568 的另一半根因（对齐标志前后不一致）

| 事实 | 锚点 |
|---|---|
| 有效对齐只算一次 | `src/UI/Helpers/TaskbarBizHelper.cs:225`（`EffectiveAlignLeft`）、`:279`（调用点） |
| 同一份对齐值同时用于"上层分支"与"策略占位方向" | `src/UI/Helpers/TaskbarBizHelper.cs:279`（分支）、`:304`/`:334`/`:370`（传给策略） |
| Win10 水平分支两种对齐都走策略内部占位（改前只有"居右"走 ⇒ "居左"完全不挤占 ⇒ 面板压在任务按钮上） | `src/UI/Helpers/TaskbarBizHelper.cs:329-337` |

## 5. 用例与命令（可从干净检出复现）

工作目录：`G:\TOOL\LiteMonitor-fork\.worktrees\f05`（分支 `fix/F05-taskbar-geometry`）

```powershell
$env:NUGET_PACKAGES = 'G:\TOOL\LiteMonitor-fork\.nuget-packages'
$env:DOTNET_CLI_HOME = 'G:\TOOL\LiteMonitor-fork\.dotnet-cli-home'
Set-Location 'G:\TOOL\LiteMonitor-fork\.worktrees\f05'
dotnet build LiteMonitor.sln -c Debug --nologo
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --no-build --nologo --logger "console;verbosity=minimal"
dotnet test tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj -c Debug --no-build --nologo
dotnet test tests/LiteMonitor.Updater.Tests/LiteMonitor.Updater.Tests.csproj -c Debug --no-build --nologo
```

| 命令 | 目的 | 结果（发现数/通过/失败/跳过） |
|---|---|---|
| `dotnet build LiteMonitor.sln -c Debug --nologo` | 构建 | **0 个错误**；**本次改动文件 0 条新警告**（主工程 warning 列表里无任何 `Taskbar*` 条目） |
| `dotnet test tests/LiteMonitor.Tests/…` | 主测试 | **发现 1959 / 通过 1952 / 失败 0 / 跳过 7**（跳过 7 条 = `MarketIndexContractTests` 6 + `MarketE2ETests` 1，均需外网，与 F04 记录一致）；基线 1936 ⇒ **新增 23 条** |
| `dotnet test tests/LiteMonitor.GpuNonperformance.Tests/…` | GPU 非性能 | **发现 19 / 通过 19 / 失败 0 / 跳过 0** |
| `dotnet test tests/LiteMonitor.Updater.Tests/…` | 更新器 | **发现 73 / 通过 73 / 失败 0 / 跳过 0** |

### 5.1 观察到的偶发项（如实记录）

全量运行期间出现 **`ReviewMarketPersistenceRegressionTests` 类的间歇失败**（第 1 轮 2 条、第 2 轮 1 条；
失败形态为 `UnauthorizedAccessException` / 插件市场持久化的文件移动冲突），**第 3 轮与最终轮全绿**；
单独重跑该类 **33/33 全通过**：

```
> dotnet test … --filter "FullyQualifiedName~ReviewMarketPersistenceRegressionTests"
已通过! - 失败: 0，通过: 33，已跳过: 0，总计: 33
```

该类与本包**无任何调用关系**（本包只触及任务栏路径），属派发单中已告知的基线 flaky；**本包未修它**。

## 6. 红→绿证明（临时改回旧行为 ⇒ 用例必须变红）

在同一分支上把三处关键实现临时改回旧行为，重跑新增用例（改完即回滚）：

| # | 临时改回的旧行为 | 预期变红的用例 | 实测 |
|---|---|---|---|
| A | 占位基线推导**不扣除**本次占位量（旧行为：把"已缩短的任务列表"当成原始宽度继续扣） | `任务列表1200变640且面板600变900的场景不再复现` | **变红 ✔**（本轮 22 条中仅此 1 条失败） |
| B | 方向解析改回**宽高比猜测**（`Height > Width ⇒ 左，否则底`） | 方向解析 4 条 + 负原点 4 条 + 浮动任务栏 1 条 + DPI 矩阵 1 条 | **变红 ✔**（5 条失败：Top / Right / 负原点 Top / 浮动任务栏 / DPI 矩阵） |
| C | Win10 回退定位路径**不做** `ScreenToClient`（直接拿屏幕坐标当客户区坐标） | `Win10策略在无法内部布局时把屏幕坐标显式转换到父客户区坐标` | **变红 ✔**（该条失败） |

回滚后再次全量：**23/23 通过**。

## 7. 验收矩阵逐项结论

| 母计划 F05 验收项 | 结论 | 证据 |
|---|---|---|
| Win10 四方向 | **代码层通过** | `停靠方向按任务栏相对显示器的物理矩形解析`（4 个方向参数化）；`混合缩放与负屏幕原点与非默认工作区下无重叠且不越界`（3 缩放 × 3 原点 × 4 方向） |
| 连续方向切换不累计漂移 | **代码层通过** | `四方向连续切换不产生累计漂移`（4 方向 × 25 轮）；真实窗口 `真实窗口上方向切换会撤销旧占位并重新取基线` |
| 重复 Tick 不累计漂移 | **代码层通过** | `重复Tick一百次不产生累计漂移`；`真实窗口上重复Tick不压缩任务列表且退出后恢复原始布局`（100 次 Tick，窗口矩形逐次相等） |
| 重复启停 100 次不缩窄任务列表 | **代码层通过** | `重复启停一百次不缩窄任务列表`（基线矩形逐次等于自然矩形） |
| 100/150/200% 混合 DPI | **代码层通过** | `混合缩放与负屏幕原点与非默认工作区下无重叠且不越界`（DPI 96/144/192） |
| 负屏幕原点 | **代码层通过** | 同上 + `负屏幕原点下停靠方向解析依然正确`（4 个参数化组合） |
| 非默认工作区 | **代码层通过** | 同上（工作区在对面额外预留 24px）+ `任务栏未贴边时由工作区预留条带判定方向` |
| 无重叠 / 无越界 | **代码层通过** | 同上（面板 ∩ 任务列表 = 空；两者均落在父客户区内） |
| 复测 1200→640 / 600→900 | **通过（缺陷不再复现）** | `任务列表1200变640且面板600变900的场景不再复现`：图标停在 900（不再右移到 1200）、列表宽 940（不再被压到 640）、面板停在 600（不再跳到 900） |
| 分辨率变化后恢复正确占位 | **代码层通过** | `面板尺寸变化会让几何缓存整体失效`；父客户区长度进入基线身份（`TaskbarReservationToken.ParentExtent`）⇒ 分辨率变化 ⇒ 撤销旧占位 + 重取基线 |
| 退出恢复 Explorer 布局 | **代码层通过** | `真实窗口上重复Tick不压缩任务列表且退出后恢复原始布局`（`Restore()` 后任务列表矩形 == 原始矩形） |
| 第三方任务栏标注"兼容性待验" | **代码层通过** | `原生系统只提供底部停靠其余方向标注为第三方兼容性待验`；`Win11策略把屏幕坐标显式转换到父客户区坐标` 内的 `IsThirdPartyDirection` 断言 |
| 原生 Win11 不声称支持系统不支持的方向 | **代码层通过** | `TaskbarDockResolver.ResolveCompatibility`：Bottom ⇒ Native；Top/Left/Right ⇒ ThirdPartyPending；Unknown ⇒ Unknown |
| 几何缓存键完整性（13 分量） | **代码层通过** | `几何缓存键在任一分量变化时整体失效`（13 个变体逐一断言 `!=`） |
| 未采用 PR #495 的 20～100px 高度启发式 | **通过（未采用）** | 旧的 48px 高度收敛逻辑已删除（改前 `TaskbarWinHelper.cs:402-444`）；现 `ComputeTaskbarRect`（`TaskbarWinHelper.cs:486`）只用**方向 + 工作区预留条带**，不含任何高度阈值 |

## 8. 新增测试清单

文件：`tests/LiteMonitor.Tests/TaskbarGeometryTests.cs`（**新增发现数 = 23**）
辅助：`tests/LiteMonitor.Tests/TaskbarWin32TestHost.cs`（新增 `CreateSlotChild`，不含用例）

| # | 用例名 | 验证了什么 |
|---|---|---|
| 1 | `停靠方向按任务栏相对显示器的物理矩形解析`（4 参数） | 顶/底/左/右四个方向由"任务栏矩形 vs 所属显示器"解析 |
| 2 | `负屏幕原点下停靠方向解析依然正确`（4 参数） | 负原点（副屏在左/上）下方向仍正确 |
| 3 | `浮动任务栏且工作区无预留时方向为未知而不是靠宽高比猜` | 判不出时返回 `Unknown`（0 是有效数据），不退回宽高比 |
| 4 | `任务栏未贴边时由工作区预留条带判定方向` | 工作区回退判定 ⇒ Bottom |
| 5 | `左侧停靠由工作区预留条带判定方向` | 工作区回退判定 ⇒ Left |
| 6 | `原生系统只提供底部停靠其余方向标注为第三方兼容性待验` | 兼容性枚举语义 |
| 7 | `几何缓存键在任一分量变化时整体失效` | 13 个分量逐一变化 ⇒ 键不等 |
| 8 | `任务列表1200变640且面板600变900的场景不再复现` | **#568 核心场景回归**（探针 P2c 输入序列） |
| 9 | `重复Tick一百次不产生累计漂移` | Tick 幂等 |
| 10 | `重复启停一百次不缩窄任务列表` | 启停 100 次后基线不变 |
| 11 | `四方向连续切换不产生累计漂移` | 4 方向 × 25 轮切换，基线每次回到自然矩形 |
| 12 | `混合缩放与负屏幕原点与非默认工作区下无重叠且不越界` | 96/144/192 DPI × 3 原点 × 4 方向 × 2 工作区：面板与列表不重叠、都不越界、尺寸仍为正 |
| 13 | `真实窗口上重复Tick不压缩任务列表且退出后恢复原始布局` | **真实 Win32**：100 次 Tick 窗口矩形逐次相等（1200 → 900，不再变 640）；`Restore()` 后回到原始矩形 |
| 14 | `真实窗口上方向切换会撤销旧占位并重新取基线` | **真实 Win32**：底 → 左切换后宽度回到 1200、高度被垂直挤占 200 |
| 15 | `Win10策略在无法内部布局时把屏幕坐标显式转换到父客户区坐标` | 回退定位路径的坐标契约 |
| 16 | `Win11策略把屏幕坐标显式转换到父客户区坐标` | Win11 定位的坐标契约 + 第三方方向标注 |
| 17 | `面板尺寸变化会让几何缓存整体失效` | 面板尺寸进入缓存键 |

（xUnit 的 `[Theory]` 参数化会展开为多条发现：第 1 条 4 条、第 2 条 4 条 ⇒ 17 个方法展开为 **23 条发现**。）

## 9. 环境

- OS / 版本：**Windows 11**（本机**非 Win10** ⇒ Win10 真机路径无法实机验证）
- DPI / 显示器拓扑：测试全部使用**注入的假显示器拓扑**（`ITaskbarScreenProvider`）+ **真实 Win32 窗口**，
  覆盖 96/144/192 DPI 与负原点；真实 Win32 窗口一律创建在**屏幕外**（如 `-3000,-2000`），不干扰用户桌面
- GPU / 驱动：本包不涉及 GPU 路径
- 关键依赖版本：LibreHardwareMonitorLib 0.9.6（传递依赖 DiskInfoToolkit 1.1.2）；.NET SDK 8.0.x
- NuGet 走本地缓存 `G:\TOOL\LiteMonitor-fork\.nuget-packages`

## 10. 未覆盖场景（待实机清单）

| 场景 | 需要的环境/动作 | 责任人 |
|---|---|---|
| **Win10 任务栏四方向真机切换**（底→左→上→右） | Win10 机器；系统任务栏设置里切换位置；观察图标/面板是否错位、是否重叠 | 用户 |
| **Win10 重复 Tick / 重复启停 100 次** 真机复现 | Win10 机器；反复开关任务栏面板、反复开/关任务栏应用 | 用户 |
| **切分辨率 / 改缩放（100↔150↔200%）后占位与位置恢复** | 多屏机器 + 显卡控制面板改缩放；观察面板与图标 | 用户 |
| **副屏在左侧（负屏幕原点）+ 任务栏在副屏** | 双屏，副屏摆左边 | 用户 |
| **第三方任务栏（StartAllBack / ExplorerPatcher）左/右/顶停靠** | 安装第三方任务栏；本包只标注"兼容性待验"，**未验证** | 用户 |
| **多行任务栏 / 超大任务栏**（不采用高度启发式后的真实表现） | 系统设置里开两行任务栏 | 用户 |
| 锁屏 / 睡眠 / Explorer 重启 / RDP 断开重连 | P-F04 已列为待实机；本包不重复 | 用户 |

## 11. 偏离与遗留

### 11.1 与派发单的偏离

1. **新增 2 个生产文件**（`TaskbarGeometry.cs` / `TaskbarReservationModel.cs`）：派发单的"改动文件"清单列了 4 个既有文件，
   未显式允许新建文件。按外派总则 §1.2「只改派发单『改动文件』清单内文件 + **新建文件**」，
   两个新文件均属允许范围；**清单内的 4 个文件之外未改任何生产文件**。
2. **改动了 `tests/LiteMonitor.Tests/TaskbarWin32TestHost.cs`**（F04 新增的测试宿主）：仅**追加**一个方法
   `CreateSlotChild`（`TaskbarWin32TestHost.cs:107-126`），用于模拟 `ReBarWindow32 → MSTaskSwWClass` 两级结构；
   **未改动** F04 的任何既有方法或断言。若协调者认为该文件应保持 F04 原样，可把该方法内联到 F05 的测试文件里。
3. **删除了 Win11 的 48px 任务栏高度收敛**（改前 `TaskbarWinHelper.cs:402-444` 的"场景 B：悬浮模式"分支）：
   母计划 F05 第 2 步要求"不要不经验证采用 PR #495 的 20～100px 高度启发式，避免高 DPI/多行任务栏误判"，
   而旧的 48px 收敛正是同类高度启发式（多行任务栏会被裁成 48px）⇒ 按母计划删除。
   **行为影响**：悬浮/自动隐藏任务栏下矩形改用物理真值（不再被裁到 48*dpi/96）。
   待实机确认（多行任务栏 / 自动隐藏）。
4. **`ITaskbarStrategy` 接口签名变更**（`SetPosition` 增加 `TaskbarDockDirection` 参数、新增 `GetInnerHostHandle()`）：
   属公开接口，但仓内**只有** `TaskbarStrategyWin10/11` 两个实现（已 grep 确认测试与工具工程均未实现该接口）⇒ 影响面可控。
5. **`TaskbarBizHelper.UpdatePlacement` 的 Win10 水平分支改为"两种对齐都走策略内部占位"**：
   改前只有"居右"分支委托策略，"居左"分支**完全不挤占**（面板直接压在系统任务按钮上 ⇒ #568 的重叠现象之一）。
   本改动让 Win10 两种对齐的行为一致。**行为影响**：Win10 + 居左时，面板位置从"任务栏最左端 +6"改为
   "任务列表自然左边界"，且任务列表会被相应挤占。**待实机确认观感**。

### 11.2 遗留（需要协调者裁定或后续包处理）

| 项 | 说明 | 归属建议 |
|---|---|---|
| `TaskbarForm.OnDpiChanged` 缺失 | 任务栏面板在 DPI 变化时**不重算字体/尺寸**（只重挂）。`TaskbarForm.cs` 属 F04 已合并区域 ⇒ 本包**禁改** | 需 F04/协调者补（本包已在几何层把 DPI 纳入缓存键，尺寸变化会触发重算） |
| Win10 挤占在 **FromEnd（居右）** 模式下的基线语义 | FromEnd 模式下列表末端锚定到面板起点 ⇒ 面板宽度变化会改变占位量（基线仍是自然矩形，语义正确，但"占位量"不是常数） | 已按契约实现并有单测覆盖；若 Win10 真机表现异常，需按实机反馈调整 |
| 第三方任务栏（左/右/顶）的**实际**兼容性 | 本包只做标注，**未验证**任何第三方任务栏 | 待实机（§10） |
| Win10 真实 Explorer 的任务列表重排语义 | 本包按"系统在已挤压窗口上追加变化"建模（探针 P2c 的数据形态）；真实 Explorer 可能直接重排（走"全量重排"分支） | 待实机；两条分支都已在 `DeriveBaseline` 内处理 |
| `timeWidth` 时钟预留区 | 本包已从硬编码 90 改为按**任务栏 DPI** 换算（100% 下与旧值一致） | 已完成（§4 锚点 `TaskbarBizHelper.cs:345`） |

### 11.3 发现的相邻问题（不属于本包）

| 问题 | 锚点 | 归属建议 |
|---|---|---|
| `TaskbarStrategyWin11.SetPosition` 恒带 `SWP_NOZORDER`，同级 z-order 无维护（#599） | `src/UI/Helpers/TaskbarStrategyWin11.cs:78` | **F06**（派发单已明确） |
| 主窗置顶恢复只看样式位、`!_form.Visible` 早退（#612） | `src/UI/Helpers/MainFormBizHelper.cs` | **F06**（派发单已明确） |
| 目标屏"暂不可用"没有托盘状态承载（#583 报告者希望看到"不可用"提示） | `src/UI/TaskbarForm.cs` 可见性闸门 | 需新增托盘状态文案（超本包范围，未做） |

## 12. 建议协调者处理

1. **CI 阈值**：本包新增 **23** 条用例 ⇒ 批次集成时把 `.github/workflows/ci.yml` / `release.yml` 的
   `MIN_TEST_TOTAL` 从当前值按**实际发现数**统一上调（**本包未改 CI 文件**）；`MAX_SKIPPED_TESTS` 不得放宽。
   本包合并后的 `LiteMonitor.Tests` 发现数 = **1959**（另两个工程 19 / 73 不变）。
2. **合并冲突预期**：`TaskbarWinHelper.cs` / `TaskbarBizHelper.cs` 与 F06 有重叠面
   （`UpdatePlacement` / `GetTaskbarRect`）；建议合并序**先并 F05，再并 F06**。
3. **Win10 真机**：§10 的 6 项需要 Win10 / 多屏 / 第三方任务栏环境，本机（Win11）无法覆盖。
