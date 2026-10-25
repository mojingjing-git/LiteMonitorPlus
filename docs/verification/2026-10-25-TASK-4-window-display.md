# TASK-4 核实报告：窗口、任务栏和多显示器生命周期

| 项 | 值 |
|---|---|
| 任务单 | TASK-4（窗口、任务栏和多显示器生命周期） |
| 检查日期 | 2026-10-25 |
| 上游仓库 / commit | `Diorser/LiteMonitor` @ `upstream/master` = **3b91b5c**（"fix: 完成 1.3.7 发版审查并修复监控与网络边界问题"） |
| fork 分支 / commit | 本地 `master` = **5e756e7**（"feat(theme-market): M2+M3 主题索引解析/安装事务/状态与 D7 卸载守卫"） |
| 本机环境 | Windows 11 专业版 26300（`10.0.26300`），.NET SDK 8.0.426，双屏（DISPLAY1 主屏 2194×1234 / DISPLAY2 副屏 1152×2048 竖屏，DPI 168） |
| 取证方式 | 纯静态代码取证（`git show/grep/diff/ls-tree`）+ 5 个**只读 / 自清理** Win32 探针（见 §8.4），未构建产品、未改产品代码 |
| 纪律声明 | 全程只读 git 命令；未执行 checkout/switch/stash/reset/commit/branch；未改动 `src/**`；未触碰工作树既有未提交改动；临时探针位于 `.tmp/verification/` 并在收尾前删除 |

> **引用约定**：文中 `↑` = 上游 `upstream/master`(3b91b5c) 的文件:行号；`⑂` = fork `master`(5e756e7) 的文件:行号。
> **重要前提**：fork 的四个任务栏核心文件与上游**逐字节相同**（见 §5），因此凡涉及 `TaskbarForm` / `TaskbarWinHelper` / `TaskbarBizHelper` / `TaskbarStrategyWin10` / `TaskbarStrategyWin11` 的结论，**上游与 fork 同等成立**；涉及 `MainForm*` / `UIController` 的结论两侧分别判定。

---

## 1. 汇总表

| Issue | 当前是否 open | 上游结论 | fork 结论 | 证据位置 | 复现/验证结果 | 下一步 |
|---|---|---|---|---|---|---|
| **#612** 「总是置顶」经常性失效 | ✅ OPEN（2026-10-22 创建，无评论） | **确认（代码级）**：置顶看门狗只检查 `WS_EX_TOPMOST` **样式位**，不检查真实 z-order；样式位在但被别的置顶窗口压住时永不修复；且主窗隐藏时看门狗直接 return | **确认（代码级）**：`RefreshTopMost` / `IsTopMostStyleApplied` / `TopMostTick` 与上游**完全一致**，fork 未修 | ① `MainFormBizHelper.cs:91-105` ↑ / `⑂:102-117`<br>② `MainFormWinHelper.cs:53-66, 68-92` ↑ / `⑂:69-106`<br>③ `MainForm_Transparent.cs:214-215, 336-344` ↑ | 静态确认；无法在本机复现"被置顶窗口压住"（需并发运行的置顶/全屏/UAC 场景） | 引入 z-order 探针（`GetWindow(GW_HWNDNEXT)` 遍历 + `WS_EX_TOPMOST` 双判）；或直接 cherry-pick 上游 PR #373（`MainFormWinHelper` + `MainForm_Transparent`，状态 CONFLICTING，需手工合并） |
| **#599** 驱动热更新后任务栏条被合成层盖住 | ✅ OPEN（2026-10-07 创建，无评论；正文含完整根因分析） | **确认（实测复现）**：面板挂在 `Shell_TrayWnd` 下，定位用 `SWP_NOZORDER`，**没有任何代码维护同级 z-order**；探针实测：新兄弟窗插到前面后，10 次上游 `SetPosition` 后面板仍在后面 | **确认（实测复现）**：任务栏 4 文件与上游逐字节相同，fork 无任何 z-order 维护 | ① `TaskbarStrategyWin11.cs:64` ↑⑂（`SWP_NOZORDER`）<br>② `TaskbarWinHelper.cs:165-177` ↑⑂（仅父句柄变化才重挂）<br>③ 探针 P1c：`<探针输出>` | **本机实测复现**（Windows 11 26300）：`Windows.UI.Composition.DesktopWindowContentBridge` 确实是 `Shell_TrayWnd` 的可见子窗；模拟驱动重置后上游 10 次定位**不恢复**，`HWND_TOP` 立即恢复 | cherry-pick 上游 **PR #600**（MERGEABLE，改 3 个文件：TaskbarWinHelper / MainForm_Transparent / TaskbarForm）——这是 #599 的现成修复 |
| **#553** 锁屏/关显示器后任务栏数据丢失 | ✅ OPEN（2026-08-19 创建，无评论） | **部分确认（机制确认，现象末端需用户环境）**：① 父窗口销毁 ⇒ 子窗口一并销毁（**本机实测**）；② 面板是 `WS_CHILD`，Explorer 销毁副屏 `Shell_SecondaryTrayWnd` ⇒ 面板 HWND 随之销毁；③ 全仓**无** `OnHandleDestroyed` 自愈、**无** `WM_POWERBROADCAST`、**无** 锁屏/唤醒处理；④ `TaskbarCreated` 只在 Explorer 重启时广播 | **部分确认（同上游）**：任务栏路径零改动；但 fork 为主窗补了 `OnHandleDestroyed`（停采集）与句柄代次协议，**不覆盖任务栏窗** | ① `TaskbarStrategyWin11.cs:26-31` ↑⑂（`SetParent` → WS_CHILD）<br>② `TaskbarForm.cs`（全文无 `OnHandleDestroyed`）↑⑂<br>③ `MainForm_Transparent.cs:239-279` ↑（WndProc 仅 TaskbarCreated/WM_DISPLAYCHANGE）<br>④ 探针 P5：`IsWindow(child)=False` | 探针 P5 **实测确认父子销毁语义**；"锁屏后数据丢失"的最终视觉态未在本机复现（需双屏 + 关屏 + 驱动降级） | 合并上游 **PR #555**（MERGEABLE，5 文件）：加 10 秒恢复期 + `WM_POWERBROADCAST` + 缓存键扩展 + Win10 内层句柄校验；并补 `TaskbarForm.OnHandleDestroyed` → 重建 |
| **#583** RDP 后任务栏面板位置错误 | ✅ OPEN（2026-09-21 创建，无评论） | **确认（代码级 + 本机算法实测）**：`FindHandles` 在目标设备不存在时**回退到主屏 `Shell_TrayWnd`**（`TaskbarWinHelper.cs:192, 218`）；回退后句柄**有效** ⇒ `IsTaskbarValid()` 恒真 ⇒ `FindHandles()` **永不重跑** ⇒ 双屏恢复后不回到 Display 2 | **确认（同上游）**：任务栏 4 文件与上游逐字节相同 | ① `TaskbarWinHelper.cs:187-219` ↑⑂<br>② `TaskbarBizHelper.cs:92-96` ↑⑂（`IsTaskbarValid` 只查 `IsWindow`）<br>③ `TaskbarForm.cs:176-183` ↑⑂（只在句柄失效时重查）<br>④ 探针 P1[C]：目标 `\\.\DISPLAY2` → 返回**主屏** `Shell_TrayWnd`（`onTargetScreen=False`） | **本机算法实测**：目标 DISPLAY2 无独立任务栏时，`FindHandles` 返回主屏任务栏且句柄有效 ⇒ 永不重解析。**RDP 接入/断开本身未在本机复现** | cherry-pick 上游 **PR #495**（MERGEABLE，3 文件）：`FindHandles` 找不到副屏任务栏时返回 `IntPtr.Zero`（不再回退主屏）+ `IsOnCorrectScreen()` 挂错屏时强制重查 |
| **#573** 开机后面板位置不固定 | ✅ OPEN（2026-09-09 创建；唯一评论 2026-09-10 "me too!"） | **确认（代码级 + 本机算法实测）**：与 #583 同根因。开机时副屏 `Shell_SecondaryTrayWnd` 可能尚未就绪 ⇒ `FindSecondaryTaskbar` 回退主屏 ⇒ 句柄有效 ⇒ 永不重解析 ⇒ 落在 Display 1 或 Display 2 取决于启动竞态 | **确认（同上游）**：任务栏 4 文件与上游逐字节相同 | 同 #583 的 ①～④ | **本机算法实测**确认回退与"句柄有效即锁定"链路；**开机竞态本身未复现**（需冷启动 + 副屏延迟就绪） | 同 #583：cherry-pick PR #495；另建议在 `TaskbarForm` 增加"启动后 N 秒内强制重解析目标设备" |
| **#568** 任务栏位置改变且重叠 | ✅ OPEN（2026-09-05 创建；2026-10-23 追加评论"Windows 11 也是一樣"） | **确认（代码级 + 算法实测，Win10 路径）**：① **无 Dock 方向枚举**，垂直/水平靠**宽高比**猜；② Win10 挤占记账 `isSqueezed` 以 `|rcMin.Width - _lastSqueezedWidth| <= 1` 判定，**系统任务列表宽度一变（开/关任务栏应用）即丢失已占用量并重复扣减**，任务列表被逐次压缩、图标被逐次右移 | **确认（同上游）**：任务栏 4 文件与上游逐字节相同 | ① `TaskbarStrategyWin10.cs:197-255` ↑⑂（`isSqueezed`/`originalWidth`/`targetRelLeft`）<br>② `TaskbarBizHelper.cs:111-114` ↑⑂（`IsVertical` 用宽高比）<br>③ `TaskbarWinHelper.cs:252-255, 258-279` ↑⑂（只认"底部锚定"，其余走 48px 猜测）<br>④ 探针 P2c：1200 → **640**，图标右移 **600px** | **本机算法实测复现**（忠实移植上游 `AdjustHorizontalLayout`）：tick3 任务列表 940px 时，`targetWidth=640`、`targetRelLeft=1200`（原 600）⇒ 图标被推 600px、列表从 1200 压到 640；面板 X 同时从 600 跳到 900 | cherry-pick **PR #555**（含"句柄变化时重置挤占跟踪状态"）；补 Dock 方向枚举（`ABOVE`/`BELOW`/`LEFT`/`RIGHT`）替换宽高比猜测 |
| **#539** 纯任务栏模式切分辨率后位置/布局乱跳 | ✅ OPEN（2026-08-03 创建，无评论） | **确认（代码级）**：① `WM_DISPLAYCHANGE` 只恢复**主窗**位置，**完全不碰任务栏窗**；② `GetTaskbarRect` 缓存键只有 `rectPhys`，**不含** HWND/目标设备/屏幕边界/工作区/DPI ⇒ 这些量变了仍复用旧坐标；③ Win10 挤占记账在分辨率变化后失准（见 #568） | **确认（同上游）**：任务栏 4 文件与上游逐字节相同。fork 主窗侧更强（`ReconcileDisplayChange` 不回放旧位置），但**任务栏侧无改善** | ① `MainForm_Transparent.cs:261-276` ↑（`WM_DISPLAYCHANGE` → 仅 `RestorePos()`）<br>② `MainForm_Transparent.cs:474-495` ⑂（→ `ReconcileDisplayChange()` + `NotifyGpuDisplayChanged()`，仍不碰任务栏）<br>③ `TaskbarWinHelper.cs:230` ↑⑂（缓存键仅 `rectPhys`）<br>④ `TaskbarStrategyWin10.cs:203-210` ↑⑂ | **静态确认 + 探针 P2b/P2c 算法实测**；**本机未做真实分辨率切换**（远程会话改分辨率有风险，且会改变用户桌面状态） | cherry-pick **PR #555**（缓存键扩展 + 恢复期）；再考虑 `WM_SETTINGCHANGE`/`WM_DPICHANGED` 处理（现全仓 0 处） |

---

## 2. 生命周期事件表（本任务核心交付）

> 「覆盖」列标注**哪个 issue 的场景被该行的事件响应覆盖**。上游与 fork 在任务栏列**完全一致**，差异只在主窗列。

| 事件 | 系统通知 / 触发 | 上游代码响应 (3b91b5c) | fork 代码响应 (5e756e7) | 预期恢复 | 是否覆盖（逐 issue 标注） |
|---|---|---|---|---|---|
| **窗口创建（任务栏面板）** | `TaskbarForm` 构造函数 | `TaskbarForm.cs:63-65`：`FindHandles()` → `AttachToTaskbar()` → `SetParent` + `ApplyChildWindowStyle`（WS_CHILD）。创建时是顶层窗，`SetParent` 后置于父窗 z-order 顶部 | 同上游（逐字节相同） | 面板挂到目标屏任务栏顶部 | ✅ 覆盖初始挂载；❌ **挂错屏无纠错**（#573 #583）—— 回退主屏后句柄有效即锁定 |
| **窗口创建（主面板）** | `MainForm` 构造函数 | `MainForm_Transparent.cs:150-174`：`RegisterTaskbarCreatedMessage`、`InitializeStyle(TopMost, ClickThrough)`、`CreateParams` 置 `WS_EX_TOPMOST` | ⑂ `MainForm_Transparent.cs:262-270` 同上；额外挂 `WindowAppearanceController`（DWM 角偏好 / Region 统一所有权） | 主面板置顶 + 圆角 + 可选 GPU/LiveGlass 路由 | ✅ 两者均覆盖 |
| **TopMost 定期维护** | 内部 10 秒定时器 | `MainFormBizHelper.cs:83-105`：`TopMostTick` → 仅当 `!IsTopMostStyleApplied()` 才 `RefreshTopMost(forceReinsert:true)`；且 `if (!_form.Visible) return;` | ⑂ `:94-117` **完全相同** | 周期性把置顶窗口抬回 | ⚠️ **仅覆盖"样式位丢失"**（句柄重建）；❌ **不覆盖"z-order 被压"**（#612）——样式位仍在时不修复；❌ 主窗隐藏（纯任务栏模式）时看门狗完全停摆（#612） |
| **TopMost 事件驱动重设** | `VisibleChanged` / `HandleCreated` / `OnShown`(+3s) / 菜单切换 / 设置应用 | `MainForm_Transparent.cs:214-215`（Visible/Handle）、`:336-344`（OnShown 后 3s 一次性）、`AppActions.cs:88-89`（`RefreshTopMost(forceReinsert:true)`）、`MenuManager.cs` → `ApplyWindowAttributes` | ⑂ `:338`、`:360`、`:440`、`:586-591` 同上 | 用户操作后立即恢复置顶 | ✅ **覆盖"关掉再打开"路径**（与 #612 用户描述一致：重开关能修好）；❌ 不覆盖自动恢复 |
| **Explorer 重启** | `TaskbarCreated`（`RegisterWindowMessage`） | `MainForm_Transparent.cs:241-259`：延迟 3s → `ToggleTaskbar(false)` + `ToggleTaskbar(true)` **整体重建 TaskbarForm** | ⑂ `:454-472` **完全相同** | 重建任务栏面板 | ✅ 覆盖 Explorer 重启；❌ 不覆盖"根 HWND 存活但子层重建"（#553 #599 #539，PR #555 指出的核心缺口） |
| **Explorer 重建子层但保留根 HWND** | 无独立通知 | ❌ **无任何响应**：`TaskbarForm.Tick` 仅在 `IsTaskbarValid()==false` 时重查（`TaskbarForm.cs:176-183`） | ❌ 同样无（任务栏文件逐字节相同） | 重新解析并重挂 | ❌ **未覆盖**：#553 #599 #539 —— 这正是 PR #555 / #600 / #495 的目标 |
| **合成层遮挡（驱动热更新）** | 无通知（Explorer 静默新建 `Windows.UI.Composition.DesktopWindowContentBridge` 并插到同级最前） | ❌ **无 z-order 维护**：`TaskbarStrategyWin11.cs:64` 定位恒带 `SWP_NOZORDER`；`TaskbarWinHelper.cs:167-177` 仅父句柄变化才重挂 | ❌ 同上（逐字节相同） | 每次刷新把面板抬回同级最前 | ❌ **未覆盖 #599**（**本机实测复现**：10 次上游定位不恢复） |
| **显示器拓扑变化（插拔/改分辨率）** | `WM_DISPLAYCHANGE` (0x007E) | `MainForm_Transparent.cs:261-276`：去抖 500ms → **仅** `_bizHelper.RestorePos()`（**主窗**） | ⑂ `:474-495`：去抖 500ms → `ReconcileDisplayChange()`（主窗，**不回放旧位置**，改夹取当前屏）+ `NotifyGpuDisplayChanged()`（GPU 表面重建） | 主窗位置恢复 + 任务栏重解析 + 缓存失效 | ⚠️ 主窗：上游直接回放保存位置（可能覆盖用户拖动）；**fork 更稳**。❌ 任务栏：**两侧都不覆盖**（#539 #553 #573 #583） |
| **显示设置变化（任务栏大小/位置/自动隐藏）** | `WM_SETTINGCHANGE` | ❌ **全仓 0 处处理** | ❌ 全仓 0 处处理 | 重算任务栏矩形与面板位置 | ❌ **未覆盖**（#568 顶部/左侧停靠；#539） |
| **DPI 变化** | `WM_DPICHANGED` / WinForms `OnDpiChanged` | `MainForm_Transparent.cs:289-295`：仅主窗 `ApplyTheme` + 圆角。**任务栏窗无 `OnDpiChanged`** | ⑂ `:523-533`：主窗额外 `NotifyLiveGlassSizeChanged()` / `NotifyGpuSizeChanged()` / `ApplyWindowAppearance()` | 任务栏面板按新 DPI 重算高度与字体 | ⚠️ 主窗：fork 更强；❌ 任务栏：**两侧都不覆盖**（`TaskbarForm` 无 `OnDpiChanged`） |
| **锁屏 / 唤醒** | `WM_POWERBROADCAST`（`PBT_APMRESUMESUSPEND` / `PBT_APMRESUMEAUTOMATIC`） | ❌ **全仓 0 处处理** | ❌ 全仓 0 处处理 | 唤醒后重建任务栏集成 | ❌ **未覆盖 #553**（PR #555 已实现） |
| **显示器关闭 / 驱动把副屏下线** | 显示器设备消失 → Explorer 销毁 `Shell_SecondaryTrayWnd` → **子窗口一并销毁**（**本机实测**） | ❌ 任务栏面板 HWND 被连带销毁；`TaskbarForm` **无 `OnHandleDestroyed`** 自愈；`Tick` 只重查**任务栏**句柄、不重建**面板** | ❌ 同上（任务栏文件逐字节相同） | 检测父窗销毁 → 重建面板 → 恢复目标设备 | ❌ **未覆盖 #553** |
| **句柄重建（主窗）** | WinForms 句柄销毁/重建（主题、DPI、GPU 路由切换） | `CreateParams` 置 `WS_EX_TOPMOST` 保住置顶**样式位**；`HandleCreated` → `RefreshTopMost(forceReinsert:true)` | ⑂ `:345-370`：`WindowHandleGeneration.Advance()` **句柄代次** + `TryConsumeControlledRecreate` + `NotifyLiveGlassHandleChanged()`；`OnHandleDestroyed`(:508-521) → 停采集、停 GPU 会话、Region 交还 WinForms | 句柄重建后所有窗口级资源按新代次重建 | ✅ **fork 明显更强**（代次协议杜绝旧句柄误用；上游无此机制）。⚠️ 但**置顶 z-order 仍不重建**（#612 的 `#612` 部分） |
| **设备热插拔 / 驱动重置** | `WM_DEVICECHANGE` | ❌ 全仓 0 处处理 | ❌ 全仓 0 处处理 | — | ❌ 未覆盖（与 #599 相关但非同一路径） |
| **坐标空间转换** | — | `TaskbarStrategyWin11.cs:50-62`：`GetWindowRect(parent)` 求 `left - parentRect.left`（屏幕→父客户区），失败回退 `ScreenToClient`；`TaskbarStrategyWin10.cs` 用 `rcMin/rcBar` 相对量 | ⑂ 逐字节相同 | 屏幕坐标 ↔ 父客户区坐标 | ⚠️ **覆盖基本换算**；❌ **DPI 混算未覆盖**：`Screen.Bounds`(物理) 与 `Screen.WorkingArea`(逻辑) 相减得 `reservedBottom`（`TaskbarWinHelper.cs:249`）——PR #495 指出副屏 150% 缩放时会算出 424px 而非 40px |
| **任务栏矩形缓存** | 内部 | `TaskbarWinHelper.cs:98-100, 230`：缓存键**只有 `rectPhys`** | ⑂ 逐字节相同 | 缓存键含 HWND/设备/屏幕边界/工作区/DPI | ❌ **未覆盖**（#539 分辨率切换后复用过期坐标；PR #555 已修） |
| **Dock 方向（顶/底/左/右）** | 任务栏位置设置变化 | ❌ **无方向枚举**：水平/垂直靠**宽高比**（`TaskbarBizHelper.cs:111-114`）；底部锚定靠 `reservedBottom > 2`（`TaskbarWinHelper.cs:252`）；其余按 48px 标准高度猜（`:258-279`） | ⑂ 逐字节相同 | 显式 Dock 枚举 + 各方向公式 | ❌ **未覆盖 #568**（Win10 侧/顶停靠坐标公式缺失） |
| **系统任务列表挤占（Win10）** | 每次 `Tick` | `TaskbarStrategyWin10.cs:197-255`：改 `MSTaskSwWClass` 尺寸/位置腾空间；`isSqueezed` 以 ±1px 容差判定"是否仍被挤占" | ⑂ 逐字节相同 | 挤占量可逆、任务列表不被逐次压缩 | ❌ **未覆盖 #568**（**本机算法实测**：1200→640，图标右移 600px） |

---

## 3. 上游未合并但已存在的候选修复（关键发现）

上游 `master`（3b91b5c）**不包含**任何针对这 7 个 issue 的提交（`git log upstream/master --all --grep="#<n>"` 对 #539/#553/#555/#568/#573/#583/#599/#612 **全部无命中**）。但仓库中存在 **8 个仍处于 OPEN 的相关 PR**：

| PR | 标题 | 创建 | 可合并性 | 覆盖文件 | 对应 issue | 与本报告结论的对应 |
|---|---|---|---|---|---|---|
| **#600** | fix(taskbar): 驱动重置后把监控条抬回 Win11 合成层上面 | 2026-10-07 | **MERGEABLE** | `TaskbarWinHelper.cs` / `MainForm_Transparent.cs` / `TaskbarForm.cs` | **#599**（PR 正文写明 "Fixes #599"） | 新增 `EnsureAboveCompositionBridge()`：遍历同级兄弟，找到可见的 `Windows.UI.Composition.DesktopWindowContentBridge` 且排在自己前面时，用 `HWND_TOP` + `SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW` 抬回；`Tick` 每次刷新调一次，`WM_DISPLAYCHANGE` 额外立刻调一次。**本探针已实测该算法语义正确**（P1c） |
| **#555** | fix(taskbar): recover integration after resume and display changes | 2026-08-21 | **MERGEABLE** | `TaskbarBizHelper.cs` / `TaskbarStrategyWin10.cs` / `TaskbarWinHelper.cs` / `MainForm_Transparent.cs` / `TaskbarForm.cs` | Relates #512 #539 #487 #355 #344 #303（**含 #539**） | 10 秒恢复期 + 每 2s 重解析重挂；`WM_POWERBROADCAST` 恢复处理；`WM_DISPLAYCHANGE` → `RequestTaskbarRecovery()`；缓存键扩展（HWND/设备/屏幕边界/工作区/DPI）；Win10 内层句柄 `IsWindow` 校验；**句柄变化时重置挤占跟踪状态**（正是 #568 的修复点） |
| **#495** | fix: 修复副屏任务栏重启后不显示及高度异常问题 | 2026-06-19（08-07 更新） | **MERGEABLE** | `TaskbarBizHelper.cs` / `TaskbarWinHelper.cs` / `TaskbarForm.cs` | **#573 / #583 的正主**（副屏任务栏） | `FindHandles` 找不到副屏任务栏时**返回 `IntPtr.Zero` 而不回退主屏**；新增 `IsOnCorrectScreen()`；`Tick` 检测挂错屏时强制重查；句柄变化立即重挂；`GetTaskbarRect` 绕过 DPI 混算 |
| **#575** | fix: improve Win11 taskbar rendering and DPI handling | 2026-09-11 | UNKNOWN | 7 个文件（含 `TaskbarStrategyWin11.cs`） | #539 / #568（Win11 侧） | 改用 **top-level overlay** 渲染 Win11 任务栏面板，规避任务栏子窗的缩放/合成遮挡；开启 Per-Monitor V2 DPI。**注意：这是结构性改动，会改变挂载模型** |
| **#367** | Fix multi screen-dpi issue | 2026-03-18 | — | — | 多屏 DPI | — |
| **#364/#413/#300** | 多屏显示选择 / 自定义 Win11 任务栏高度 | — | — | — | 辅助 | — |
| **#373** | 修复全屏窗口置顶失效问题 | 2026-03-22 | **CONFLICTING** | `MainFormWinHelper.cs` / `MainForm_Transparent.cs` | **#612** | 全屏/虚拟桌面切换下置顶失效——与本报告 #612 的 z-order 结论直接相关，但需手工合并 |
| **#563** | 修复多屏自动隐藏、设置保存回退和缩放后文字挤压 | 2026-08-31 | UNKNOWN | `MainFormBizHelper.cs` 等 | 多屏 / 缩放 | 主窗侧，fork 已有类似改进 |

> **对 fork 的直接影响**：fork 的任务栏 4 文件与上游 master **逐字节相同** ⇒ **fork 同样不含 PR #495 / #555 / #600 / #575 的任何改动**（已用 `grep` 在 `src/**` 全量搜索 `CompositionBridge` / `EnsureAboveCompositionBridge` / `WM_POWERBROADCAST` / `IsOnCorrectScreen` / `PBT_APMRESUMEAUTOMATIC` / `恢复期` → **0 命中**）。

---

## 4. 逐 issue 细述

### 4.1 #612 「总是置顶」经常性失效

1. **当前状态**：OPEN，2026-10-22 20:14:15 创建，0 评论，无 label。
2. **问题摘要**：开启「总是置顶」后经常失效，必须关掉再打开才能正确置顶。
3. **上游代码证据（窗口/句柄生命周期）**：
   - `MainFormBizHelper.cs:83-88`（↑）：`StartTopMostTimer()`，`Interval = 10000`，**唯一**的周期性置顶维护。
   - `MainFormBizHelper.cs:91-105`（↑）：
     ```csharp
     private void TopMostTick(object? sender, EventArgs e)
     {
         if (!_form.Visible) return;                       // ← 主窗隐藏（纯任务栏模式）时看门狗完全停摆
         if (_cfg.TopMost) {
             if (!_winHelper.IsTopMostStyleApplied())      // ← 只看样式位
                 _winHelper.RefreshTopMost(true, forceReinsert: true);
         }
         ...
     }
     ```
   - `MainFormWinHelper.cs:53-66`（↑）`IsTopMostStyleApplied()`：读 `GWL_EXSTYLE & WS_EX_TOPMOST`。**`WS_EX_TOPMOST` 是样式位，只要窗口还在 topmost band 就一直置 1**；被别的置顶窗口 / 全屏应用 / UAC 框压住时该位**不会**清零 ⇒ 看门狗认为"一切正常"⇒ **永不修复 z-order**。
   - `MainFormWinHelper.cs:68-92`（↑）`RefreshTopMost`：注释明写「**仅在明确恢复/用户操作时重插 Z 序，避免定时器频繁抢其它置顶窗口**」——这是**有意的设计取舍**，也正是本 issue 的成因。`forceReinsert=true` 时才做 `HWND_NOTOPMOST` → `HWND_TOPMOST` 的重插。
   - `MainForm_Transparent.cs:214-215`（↑）`VisibleChanged` / `HandleCreated` → `RefreshTopMost(forceReinsert:true)`；`:336-344` `OnShown` 后 3s **一次性**重插；`AppActions.cs:88-89`（↑）菜单/设置切换 → `RefreshTopMost(forceReinsert:true)`。⇒ **"关掉再打开"恰好命中 `forceReinsert` 路径**，与用户描述完全吻合。
4. **fork 对照**：`MainFormBizHelper.cs:94-117`（⑂）、`MainFormWinHelper.cs:69-106`（⑂）、`MainForm_Transparent.cs:338/360/440/586-591`（⑂）与上游**逐行一致**；`RefreshTopMost` / `IsTopMostStyleApplied` 的实现**完全未改**。fork 新增的 `WindowAppearanceController` 明确声明「**不碰**置顶（TopMost）」（`WindowAppearanceController.cs:178`）。⇒ **fork 未修复，缺陷等价存在**。
5. **复现条件与验证步骤**：
   - 步骤 1：启用「总是置顶」，另开一个同为置顶的窗口（如资源管理器「始终置顶」），或进入全屏应用 / 触发 UAC 提权框。
   - 步骤 2：观察 LiteMonitor 主面板被压到下面（样式位仍为 1）。
   - 步骤 3：等待 ≥10s 观察看门狗是否恢复 → **预期不恢复**（`TopMostTick` 走 `IsTopMostStyleApplied()==true` 分支，什么都不做）。
   - 步骤 4：把「总是置顶」关掉再打开 / 打开再关闭主面板 → **预期立即恢复**（走 `forceReinsert`）。
   - 步骤 5（纯任务栏模式）：`HideMainForm=true` 时，观察看门狗是否运行 → **预期完全不运行**（`!_form.Visible` 提前 return）。
   - 本机验证：**未复现**（需并发置顶/全屏/UAC 场景，且不改变用户桌面状态的前提下难以自动化）。
6. **结论**：**确认（代码级）**。上游与 fork **同等存在**。
7. **风险**：中。用户可见的"置顶失效"会让面板被其他应用遮挡，且自愈完全依赖用户手动操作；纯任务栏模式下自愈窗口不存在。
8. **建议下一步**：
   - 短期：cherry-pick 上游 PR #373（CONFLICTING，需手工把 `MainFormWinHelper.cs` / `MainForm_Transparent.cs` 的冲突解掉）。
   - 中期：把 `IsTopMostStyleApplied()` 升级为**双判**——样式位 **且** z-order 位置（用 `GetWindow(GW_HWNDNEXT)` 在 topmost band 内遍历，判断自己是否被压）；或引入"用户不活跃 N 秒后温和重插"策略，避免与上游注释里担心的"抢其他置顶窗口"行为冲突。
   - 另一处需一并处理的点：`TopMostTick` 的 `!_form.Visible` 早退，使**纯任务栏模式**（`HideMainForm=true`）完全没有置顶看门狗——需确认该模式下主面板是否还应保持置顶语义。

### 4.2 #599 显卡驱动更新后任务栏监控条被合成层盖住，进程仍在运行

1. **当前状态**：OPEN，2026-10-07 05:27:05 创建，0 评论，正文含完整根因分析并给出实测数据。
2. **问题摘要**：Win11 纯任务栏模式下，驱动热更新后 Explorer 新建铺满任务栏的 `Windows.UI.Composition.DesktopWindowContentBridge` 并插到同级最前；面板是 `Shell_TrayWnd` 的子窗，被整层盖住，进程仍在运行，只能杀进程重开。
3. **上游代码证据（窗口/句柄生命周期）**：
   - `TaskbarStrategyWin11.cs:26-31`（↑）：`Attach` = `SetParent(_form.Handle, taskbarHandle)` + `ApplyChildWindowStyle`（加 `WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS`）。
   - `TaskbarStrategyWin11.cs:33-72`（↑）`SetPosition`：
     ```csharp
     SetWindowPos(_form.Handle, IntPtr.Zero, finalX, finalY, w, h, SWP_NOZORDER | SWP_NOACTIVATE);  // :64
     ```
     **`SWP_NOZORDER` ⇒ 每次定位都不改 z-order**；仅当父句柄不符时走 `:67-71` 的 `HWND_TOPMOST` 兜底。
   - `TaskbarWinHelper.cs:165-177`（↑）`SetPosition`：**只有 `currentParent != expectedParent` 才重新 `Attach`**。驱动热更新时父句柄 `Shell_TrayWnd` 通常不变 ⇒ 重挂逻辑不跑。
   - `TaskbarForm.cs:172-230`（↑）`Tick()`：全部逻辑里**没有任何一处**检查同级 z-order。
   ⇒ **上游不存在任何维护面板在兄弟窗中 z-order 的代码路径**。
   - 上游 PR #600 的作者与 #599 的报告者给出了相同诊断（根句柄不变 + `SWP_NOZORDER`）。
4. **fork 对照**：`TaskbarStrategyWin11.cs`、`TaskbarWinHelper.cs` 与上游**逐字节相同**（`git diff upstream/master master -- <file>` 为空）⇒ **fork 无任何 z-order 维护，缺陷等价存在**。
5. **复现条件与验证步骤（本机已实测部分）**：
   - 本机环境：Win11 26300，`Shell_TrayWnd` = 0x10288；实测其子窗枚举为
     `[0] Windows.UI.Composition.DesktopWindowContentBridge(visible) [1] Windows.UI.Core.CoreWindow [2] Start [3] TrayDummySearchControl [4] TrayNotifyWnd [5] ReBarWindow32`
     ⇒ **该合成层在本机确实存在且可见**。
   - **探针 P1b（忠实复刻 LiteMonitor 挂载路径：顶层创建 → `SetParent` → `ApplyChildWindowStyle`）**：面板被放到 z-order **index 0（最前）**，位于合成层之上 ⇒ 正常挂载时可见。
   - **探针 P1c（模拟驱动重置）**：在面板已挂载后，向 `Shell_TrayWnd` 插入一个新的可见子窗并置于最前 ⇒ 面板从 index 0 掉到 index 1，**被新兄弟窗遮住**；随后**连续 10 次上游 `SetPosition(SWP_NOZORDER)` 后面板仍在 index 1 —— 上游完全不恢复**；改用 `HWND_TOP`（PR #600 的做法）后立刻回到 index 0。
   - 实机完整复现（真实驱动热更新）**未执行**（会改动本机显卡驱动，超出取证边界）。
   - 用户侧完整复现步骤：① 纯任务栏模式 + 隐藏主窗 + 隐藏托盘；② 执行驱动热更新（或用 `SetWindowPos` 把合成层抬到同级最前来模拟）；③ 观察监控条消失、进程仍在任务管理器；④ 杀进程重开才恢复。
6. **结论**：**确认（代码级 + 本机实测复现 z-order 机制）**。上游与 fork **同等存在**。
7. **风险**：高。用户可见"监控条凭空消失"，且唯一的恢复手段是杀进程。
8. **建议下一步**：**cherry-pick 上游 PR #600**（MERGEABLE，改 3 文件，与 fork 冲突面为 0——因为这 3 个文件在 fork 侧除 `TaskbarForm.cs` 的主题节流改动外与上游相同）。合并后必须补一条回归测试或手工验收：`Shell_TrayWnd` 子窗枚举顺序 + `EnsureAboveCompositionBridge` 返回值。

### 4.3 #553 多个显示器，锁屏/关闭显示器按钮后任务栏显示数据会丢失

1. **当前状态**：OPEN，2026-08-19 00:45:42 创建，0 评论，含 3 张截图。
2. **问题摘要**：面板设在第二显示器任务栏；锁屏或用"关闭显示器"按钮（不关物理电源）后一段时间，驱动只上报 1 个显示器；显示器重新点亮后任务栏数据不再出现。
3. **上游代码证据（窗口/句柄生命周期）**：
   - **父窗口销毁 ⇒ 子窗口一并销毁**（Windows 语义，**本机探针 P5 实测**：合成父窗销毁后 `IsWindow(child) == False`）。
   - `TaskbarStrategyWin11.cs:29`（↑）：面板通过 `SetParent` 成为任务栏的 **WS_CHILD** ⇒ 副屏 `Shell_SecondaryTrayWnd` 被 Explorer 销毁时，面板 HWND **随之销毁**。
   - `TaskbarForm.cs`（↑，全文 265 行）：**只有** `Dispose` / `WndProc`（仅处理右键/双击）/ `OnMouseUp` / `OnMouseDoubleClick` / `OnPaintBackground` / `OnPaint` / `CreateParams` —— **没有 `OnHandleDestroyed`、没有 `OnHandleCreated`、没有任何自愈逻辑**。
   - `MainForm_Transparent.cs:239-279`（↑）`WndProc`：只处理 `TaskbarCreated` 与 `WM_DISPLAYCHANGE`。
   - **全仓 `grep` 结果：`WM_POWERBROADCAST` = 0 处，`PBT_APMRESUME*` = 0 处，`WM_DEVICECHANGE` = 0 处，`WM_SETTINGCHANGE` = 0 处** ⇒ **锁屏 / 唤醒 / 热插拔完全无处理**。
   - `TaskbarCreated` 只在 **Explorer 重启**时广播，显示器下线不会触发 ⇒ 不走重建路径。
   - `TaskbarForm.cs:176-183`（↑）`Tick`：`isHandleInvalid = !_bizHelper.IsTaskbarValid()`（`TaskbarBizHelper.cs:92-96` 只查 `IsWindow(_hTaskbar)`）⇒ 面板 HWND 被连带销毁**不会**触发任何重建；即便任务栏句柄失效后重查（`TaskbarWinHelper.cs:187-206`），目标设备已消失 ⇒ **回退主屏**（见 §4.4/#583），面板被挂到主屏而非原设备，且**永不自动回到原设备**。
   - `TaskbarForm.cs:190-191`（↑）：`nextCols.Count == 0` 时 `return`，会跳过 `UpdateTaskbarRect`/`UpdatePlacement` ⇒ 面板在数据异常期间既不重定位也不刷新。
4. **fork 对照**：任务栏 4 文件与上游逐字节相同 ⇒ **任务栏侧零改善**。fork 在**主窗**侧确有更强生命周期：⑂ `MainForm_Transparent.cs:508-521` `OnHandleDestroyed` → 停 LiveGlass 采集、停 GPU 会话、Region 交还 WinForms；⑂ `:345-370` `HandleCreated` → `WindowHandleGeneration.Advance()` **句柄代次** + `NotifyLiveGlassHandleChanged()`。**但这些只作用于 MainForm，TaskbarForm 完全没有对等机制。**
5. **复现条件与验证步骤**：
   - 步骤 1：双屏，面板放副屏任务栏；进入"关闭显示器"（Win+Ctrl+Shift+B）或锁屏。
   - 步骤 2：等待驱动只上报 1 个显示器（用设备管理器/多显示器工具确认）。
   - 步骤 3：重新点亮显示器 → 观察副屏任务栏面板。
   - 步骤 4（诊断）：用 Spy++/Process Explorer 查看 `Shell_SecondaryTrayWnd` 是否被销毁重建、LiteMonitor 进程是否存活。
   - 本机验证：**父子销毁语义已实测（P5）**；"锁屏后数据丢失"的**最终视觉态未复现**（本机副屏无独立任务栏、且未执行锁屏/关屏与驱动降级）。
6. **结论**：**部分确认**。根因链路（父子销毁 → 无自愈 → 无电源/锁屏通知 → 目标设备不回填）**已由代码与实测闭合**；但"数据丢失"在用户机上的**最终形态**（是彻底消失、还是被挪到主屏、还是留下无父的游离窗口）**未在本机复现**，需用户环境确认。
7. **风险**：高。面板是任务栏子窗，父窗一销毁就跟着消失，且没有任何事件能让它回来。
8. **建议下一步**：
   - 合并上游 **PR #555**（MERGEABLE）：`WM_POWERBROADCAST` + 10 秒恢复期 + `RequestTaskbarRecovery()`。
   - 额外补 fork 侧对等件：`TaskbarForm.OnHandleDestroyed` → 通知 `MainForm` 重建任务栏面板（fork 已有 `WindowHandleGeneration` 协议可复用）。
   - 补 `TaskbarForm.OnDpiChanged`（当前缺失）。

### 4.4 #583 两个显示器，远程桌面后任务栏面板位置错误

1. **当前状态**：OPEN，2026-09-21 01:38:09 创建，0 评论。报告者另提"优先模式 / 锁定模式"需求（Display 2 断开时宁可不显示，也不要回退 Display 1）。
2. **问题摘要**：面板设 Display 2；RDP 单屏接入时回退 Display 1；本机重新登入后面板**仍留在 Display 1**，必须重启软件才恢复。
3. **上游代码证据（窗口/句柄生命周期）**：
   - `TaskbarWinHelper.cs:187-206`（↑）`FindHandles(targetDevice)`：
     ```csharp
     target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == targetDevice) ?? Screen.PrimaryScreen;  // :192
     ```
     目标设备不在 `Screen.AllScreens` ⇒ **静默回退主屏**。
   - `TaskbarWinHelper.cs:208-219`（↑）`FindSecondaryTaskbar`：**找不到匹配副屏的 `Shell_SecondaryTrayWnd` 时 `return FindWindow("Shell_TrayWnd", null)`（:218）—— 同样是回退主屏**。
   - `TaskbarBizHelper.cs:92-96`（↑）`IsTaskbarValid()`：**只查 `IsWindow(_hTaskbar)`**。回退得到的主屏任务栏**是有效窗口** ⇒ 恒为 true。
   - `TaskbarForm.cs:176-183`（↑）`Tick`：**只有 `isHandleInvalid` 才重跑 `FindHandles()`** ⇒ 回退后**永不重解析目标设备** ⇒ 双屏恢复后不回到 Display 2。
   - `TaskbarWinHelper.cs:221-290`（↑）`GetTaskbarRect(hTaskbar, targetDevice)`：目标设备找不到时 `screen = Screen.FromHandle(hTaskbar)`（:243）⇒ **按主屏算坐标**，面板落在主屏任务栏。
   - 没有任何"目标设备是否恢复"的检测（`IsOnCorrectScreen` 是 PR #495 才引入的）。
4. **fork 对照**：任务栏 4 文件与上游逐字节相同 ⇒ **同等存在**。fork 主窗侧的 `ReconcileDisplayChange`（⑂ `MainFormBizHelper.cs:39-48`）只处理主窗位置，与任务栏无关。
5. **复现条件与验证步骤**：
   - 步骤 1：双屏，面板设 Display 2，任务栏设备 = `\\.\DISPLAY2`。
   - 步骤 2：RDP 接入（单屏）→ 观察面板落到 Display 1。
   - 步骤 3：断开 RDP，本机双屏恢复 → 观察面板**是否自动回到 Display 2**（预期：不回）。
   - 步骤 4（诊断）：Process Explorer 观察 `Shell_TrayWnd` 与 `Shell_SecondaryTrayWnd` 的创建/销毁时刻；确认 LiteMonitor 进程未重启。
   - **本机验证（算法层）**：探针 P1[C] 实测矩阵——目标 `\\.\DISPLAY2` ⇒ `FindHandles` 返回**主屏** `Shell_TrayWnd`（0x10288）、`onTargetScreen=False`、`tray=0x0`；且该句柄 `IsWindow==true` ⇒ 触发"永不重解析"。**RDP 接入/断开本身未在本机复现**（会中断用户会话）。
6. **结论**：**确认（代码级 + 本机算法实测）**。RDP 会话切换这一具体触发未复现，但**触发后的状态（回退主屏 + 句柄有效 + 永不重解析）已被实测证明必然成立**。上游与 fork **同等存在**。
7. **风险**：中高。用户体验为"配置被无视"，且必须重启软件。
8. **建议下一步**：
   - 合并上游 **PR #495**（MERGEABLE）：`FindHandles` 找不到副屏任务栏时返回 `IntPtr.Zero`（**不回退主屏**）⇒ 面板暂时隐藏而不是乱跑；新增 `IsOnCorrectScreen()`，挂错屏时 `Tick` 强制重查。
   - 报告者提出的"优先模式 / 锁定模式"属于**产品需求**，当前 `Settings.TaskbarMonitorDevice`（`Settings.cs:122`）只有单一取值，无"锁定"语义；如要实现需新增配置项，**超出本次取证范围**。

### 4.5 #573 两个显示器，开机后面板位置不固定

1. **当前状态**：OPEN，2026-09-09 11:29:34 创建；唯一评论 2026-09-10 12:08:49 `YCTS-otree: me too!`（确认复现）。
2. **问题摘要**：开机后面板有时在显示器 1、有时在显示器 2，不固定。
3. **上游代码证据**：与 #583 **同一根因链**：
   - 开机瞬间副屏 `Shell_SecondaryTrayWnd` 可能尚未就绪 ⇒ `FindSecondaryTaskbar`（`TaskbarWinHelper.cs:208-219`）遍历不到 ⇒ `:218` **回退主屏**。
   - 回退得到的句柄**有效** ⇒ `TaskbarForm.cs:176-183` 的重查逻辑**不触发** ⇒ 位置被永久锁死在回退结果上。
   - 「有时 1 有时 2」= **启动竞态**（副屏任务栏就绪快慢）→ 不同次启动落到不同屏。
   - 上游 PR #495 的作者独立得出同一结论：「**FindHandles fallback 到主屏**：启动时副屏任务栏（`Shell_SecondaryTrayWnd`）可能尚未就绪，原代码会 fallback 到主屏 `Shell_TrayWnd`，且 Tick 重试逻辑无法检测到这个错误，导致永远停留在主屏。」
4. **fork 对照**：任务栏 4 文件与上游逐字节相同 ⇒ **同等存在**。
5. **复现条件与验证步骤**：
   - 步骤 1：双屏，面板设 Display 2；**冷启动**（重启系统，不是重启 LM）。
   - 步骤 2：多次冷启动，每次记录面板落在哪个显示器（预期：随机）。
   - 步骤 3（诊断）：在 LM 启动瞬间用 Process Explorer 观察 `Shell_SecondaryTrayWnd` 的创建时刻 vs `LiteMonitor.exe` 启动时刻；用 Spy++ 观察 LM 是否调用 `FindWindow("Shell_SecondaryTrayWnd")` 时该窗尚不存在。
   - 本机验证：**回退与"句柄有效即锁定"链路已实测（P1[C]）**；**开机竞态本身未复现**（需冷启动 + 副屏任务栏延迟就绪的特定驱动/硬件组合）。
6. **结论**：**确认（代码级 + 本机算法实测；冷启动竞态未复现）**。上游与 fork **同等存在**。
7. **风险**：中。开机即错位，且必须重启 LM 才能纠正（与 #583 同源）。
8. **建议下一步**：同 #583 —— cherry-pick PR #495；另建议在 `TaskbarForm` 增加"启动后固定 N 秒内持续重解析目标设备"的恢复窗（PR #555 的 `_recoveryEndTime` 机制正好可以复用，二者可叠加）。

### 4.6 #568 任务栏位置改变且重叠

1. **当前状态**：OPEN，2026-09-05 02:30:48 创建；2026-10-23 08:35:19 评论 `minjiu: Windows 11 也是一樣`（附图）。报告环境：**Windows 10**。
2. **问题摘要**：程序图标移动到中间并被覆盖；用 WinSnap 截图时还会动态移动。
3. **上游代码证据（窗口/句柄生命周期）**：
   - **无 Dock 方向枚举**：上游 `MainFormBizHelper.cs:32` 虽有 `DockEdge` 枚举，但那是**自动隐藏**用的，与任务栏停靠无关。任务栏方向靠：
     - `TaskbarBizHelper.cs:111-114`（↑）`IsVertical()` = `_taskbarRect.Height > _taskbarRect.Width`（**宽高比猜测**）；
     - `TaskbarWinHelper.cs:249`（↑）`reservedBottom = screenBounds.Bottom - workArea.Bottom`，`:252` `if (reservedBottom > 2)` ⇒ **只认"底部锚定"**；
     - `:258-279`（↑）其余情况按 `48 * dpi / 96` 的"标准高度"猜一个底部横条。**顶部停靠、左侧停靠、右侧停靠没有专门公式。**
   - **Win10 挤占记账失准（本 issue 的主因）**，`TaskbarStrategyWin10.cs:197-255`：
     ```csharp
     bool isSqueezed = _lastSqueezedWidth > 0 && Math.Abs(rcMin.Width - _lastSqueezedWidth) <= GAP_TOLERANCE; // :203, GAP_TOLERANCE=1
     if (isSqueezed) { originalWidth = rcMin.Width + _lastUsedWidth; ... }   // :205-210
     ...
     if (alignLeft) {
         targetRelLeft  += effectiveWidth + manualOffset;   // :224
         targetWidth    = originalWidth - effectiveWidth - manualOffset;  // :226
         x = originalRelLeft + manualOffset;               // :228
     }
     ...
     MoveWindow(_hMin, targetRelLeft, 0, targetWidth, rcMin.Height, true);  // :242
     ```
     **`isSqueezed` 只在任务列表宽度与上次记录相差 ≤1px 时成立**。一旦系统任务列表宽度变化 >1px（打开/关闭任务栏应用、任务栏工具栏更新、分辨率变化后 Explorer 重排），`isSqueezed == false` ⇒ `originalWidth = rcMin.Width`（**已经被挤过的宽度**）⇒ **已消耗的 `w + manualOffset` 被遗忘又重新扣一次**；同时 `originalRelLeft = currentRelLeft`（未回退 `_lastUsedWidth`）⇒ **任务列表被再推右一个面板宽**。
     `_lastSqueezedWidth/_lastUsedWidth` **只在垂直/水平方向切换时重置**（`:120-125`），分辨率变化、句柄变化都不重置。
   - `RestoreLayout`（`:257-295`）只按**最后一次**记录恢复一次，**无法还原多次累积的挤占**。
   - **本机算法实测（探针 P2c，忠实移植上游 `AdjustHorizontalLayout`）**：
     | tick | 系统任务列表宽度 | 结果 `targetWidth` | 结果 `targetRelLeft` | 面板 X |
     |---|---|---|---|---|
     | 1 | 1200（relLeft 600） | **900** | 900 | 600 |
     | 2 | 900（稳定） | 900 | 900 | 600（稳定） |
     | 3 | **940**（开了一个任务栏应用） | **640** | **1200** | **900** |
     | 4 | 980 | 680 | 1200 | 900 |
     ⇒ 原本 1200px 的系统任务列表被压到 **640px**，图标整体**右移 600px**（600→1200），面板自身 X 也从 600 跳到 900 ⇒ **"图标移动到中间并被覆盖"+"动态移动"** 与报告完全一致。
   - **Win11 侧**：`TaskbarStrategyWin11.HasInternalLayout == false`（`:19`），不挤压任务列表，因此 Win11 上的重叠更可能来自"面板与居中图标区/时钟区重叠"（`TaskbarBizHelper.cs:166` 硬编码 `timeWidth = 90` 预留时钟宽度，不随 DPI/系统字体变化）+ 无 Dock 方向公式。
4. **fork 对照**：任务栏 4 文件与上游逐字节相同 ⇒ **同等存在**。
5. **复现条件与验证步骤**：
   - 步骤 1（Win10 复现主线）：Win10 + 底边任务栏 + 面板靠左（`TaskbarAlignLeft=true`）；记录 `MSTaskSwWClass` 的宽度与左边界。
   - 步骤 2：打开/关闭任意任务栏应用（使任务列表宽度变化 >1px）→ 观察图标整体右移、任务列表被压缩。
   - 步骤 3（分辨率复现）：改变分辨率（Explorer 重排任务列表）→ 观察同样现象。
   - 步骤 4（Win11 复现）：Win11 居中任务栏 → 观察面板是否与居中图标区/时钟重叠；再用 StartAllBack 之类把任务栏移到顶部/左侧，观察定位公式是否失效。
   - 本机验证：**Win10 挤占算法已实测复现（P2c）**；**真实 Win10 环境未复现**（本机是 Win11）；**Dock 方向变化未复现**。
6. **结论**：**确认（代码级 + 算法实测，Win10 路径）**。上游与 fork **同等存在**。
7. **风险**：中高。变形会**修改系统任务列表窗口的尺寸与位置**（`MoveWindow(_hMin, ...)`），属于对系统 UI 的侵入式操作，恢复不彻底时会长期留痕。
8. **建议下一步**：
   - 合并 **PR #555** 中的「句柄变化时重置挤占跟踪状态」+ 把 `isSqueezed` 判定改为**基于已记录位移量**而非 ±1px 宽度猜测。
   - 补 Dock 方向枚举（`ABOVE`/`BELOW`/`LEFT`/`RIGHT`），用 `WorkingArea` 与 `Bounds` 的**四边差值**判定方向，替换宽高比与 `reservedBottom` 猜测。
   - Win11 侧：把硬编码 `timeWidth = 90` 改为按任务栏字体/DPI 实测的时钟宽度。

### 4.7 #539 [Bug] 纯任务栏模式下，切换分辨率后显示位置/布局乱跳

1. **当前状态**：OPEN，2026-08-03 02:06:20 创建，0 评论。环境：Win10 LTSC 21H2，单屏 4K，串流到 Surface 或本机改分辨率；"每次切换都乱跳，切换回去也不行，重启 LM 后恢复正常"。
2. **问题摘要**：切换分辨率后监控面板位置偏移、布局错乱或跳动，无法固定回任务栏区域。
3. **上游代码证据（窗口/句柄生命周期）**：
   - `MainForm_Transparent.cs:261-276`（↑）`WM_DISPLAYCHANGE` 处理：**去抖 500ms → `_bizHelper?.RestorePos()`** ⇒ **只恢复主窗位置，完全不触碰 TaskbarForm**（无重建、无重解析、无缓存失效）。
   - `TaskbarWinHelper.cs:98-100, 230, 285-289`（↑）任务栏矩形缓存：缓存键**只有 `rectPhys`**（任务栏窗口的物理矩形）：
     ```csharp
     if (_isCacheValid && rectPhys == _lastWindowRect) return _cachedResult;   // :230
     ```
     而 `finalRect` 的计算还依赖 `screen.WorkingArea`（:247）、`screen.Bounds`（:248）、`reservedBottom`（:249）、`GetTaskbarDpi()`（:267）——**这些量全部不在缓存键里** ⇒ 屏幕边界、工作区、DPI、目标设备任一变化而任务栏物理矩形恰好不变时，**持续复用过期坐标直到进程重启**。
   - `TaskbarStrategyWin11.cs:47-62`（↑）坐标换算：`finalX = left - parentRect.left`（屏幕→父客户区），`GetWindowRect` 失败才回退 `ScreenToClient`。
   - `TaskbarStrategyWin10.cs:203-210, 238-251`（↑）挤占记账在分辨率变化后失准（见 §4.6/#568）。
   - `TaskbarForm.cs:206-222`（↑）布局重建条件：`isUninitialized || isContentChanged`（签名 = `_layout.GetLayoutSignature(_cols) + "_" + _bizHelper.Height`）⇒ **任务栏高度 `Height` 变化会触发重建**；而 `Height` 来自 `_bizHelper.Height` = `Math.Max(24, _taskbarRect.Height)`（`TaskbarBizHelper.cs:108`）⇒ 分辨率变化导致任务栏矩形变化 ⇒ 布局重建 ⇒ **"布局乱跳"**。
   - `TaskbarForm.cs:225`（↑）每 tick 都 `UpdatePlacement(Width)`；`TaskbarBizHelper.cs:187-197`（↑）水平定位用 `tray.Left - panelWidth - 6` 或 `_taskbarRect.Right - panelWidth - 10 - rightModeTotalOffset - timeWidth`（`timeWidth` 硬编码 90）。
   - **全仓无 `WM_SETTINGCHANGE`、无 `WM_DPICHANGED` 处理**（`OnDpiChanged` 只重写主窗）。
4. **fork 对照**：
   - 任务栏 4 文件与上游逐字节相同 ⇒ **任务栏侧完全相同**。
   - ⑂ `MainForm_Transparent.cs:474-495` 的 `WM_DISPLAYCHANGE` 分支改为 `ReconcileDisplayChange()` + `NotifyGpuDisplayChanged()` ⇒ **主窗侧比上游更稳**（上游 `RestorePos()` 会把**保存过的旧位置**盖回用户在显示变化后拖动过的位置；fork 改为：当前窗口仍与任一屏幕工作区相交 ⇒ `ClampToScreen()`，否则才 `RestorePos()`，见 ⑂ `MainFormBizHelper.cs:39-48`）。**但仍不触碰任务栏窗** ⇒ 对 #539 **无实质帮助**。
   - ⑂ fork 额外有 `NotifyGpuDisplayChanged()`（GPU 表面随显示变化重建）—— 与任务栏定位无关。
5. **复现条件与验证步骤**：
   - 步骤 1：纯任务栏模式，记录面板矩形（可用 Spy++/Process Explorer 的窗口属性，或 LiteMonitor 自身日志）。
   - 步骤 2：改分辨率（Win10 LTSC：设置 → 显示 → 改分辨率；或串流端改）。
   - 步骤 3：观察面板位置与布局；**在改分辨率后立刻、延迟 1s、再延迟 5s 各观察一次**（用于判断是"永久错位"还是"过渡抖动"）。
   - 步骤 4（针对性验证缓存问题）：改分辨率后**不动**任务栏窗口大小只改分辨率（若任务栏物理矩形未变），观察是否复用旧坐标。
   - 步骤 5：改分辨率后**重启 LM** → 确认恢复正常（与报告一致，可反证是**进程内缓存/状态**问题而非配置问题）。
   - 本机验证：**未在本机做真实分辨率切换**（远程会话改分辨率有中断风险，且会改变用户桌面状态）。**算法层已验证**：探针 P2b 显示 Win10 挤占记账在分辨率变化后仍能自洽地重算；真正的风险点是 `GetTaskbarRect` 的缓存键缺项（代码级确认）。
6. **结论**：**确认（代码级）**。上游与 fork **同等存在**（fork 仅主窗侧更优）。
7. **风险**：中。表现为"位置乱跳/错位"，且用户报告"重启才恢复"⇒ 指向进程内缓存/状态未失效。
8. **建议下一步**：合并 **PR #555**（缓存键扩展 + `WM_DISPLAYCHANGE` → `RequestTaskbarRecovery()`）；建议额外补 `WM_SETTINGCHANGE`（`SPI_SETWORKAREA`）与 `WM_DPICHANGED` 处理——目前这两类通知全仓 0 处。

---

## 5. fork 与上游在"窗口/任务栏生命周期"路径上的差异

### 5.1 事实：任务栏侧**零差异**

以下文件在 fork `master`(5e756e7) 与上游 `upstream/master`(3b91b5c) 之间 **`git diff` 为空（逐字节相同）**：

- `src/UI/Helpers/TaskbarWinHelper.cs`（358 行）
- `src/UI/Helpers/TaskbarBizHelper.cs`（275 行）
- `src/UI/Helpers/TaskbarStrategyWin10.cs`（297 行）
- `src/UI/Helpers/TaskbarStrategyWin11.cs`（84 行）

唯一有差异的是 `src/UI/TaskbarForm.cs`（+33/−4），且**只改了一处**：

```
-  if (Math.Abs(Environment.TickCount) % 5000 < _cfg.RefreshMs) _bizHelper.CheckTheme();
+  if (_themeCheckTrigger.TryConsume()) _bizHelper.CheckTheme();     // 5 秒单调截止时间触发器
```
（⑂ `TaskbarForm.cs:29-50`、`:209-212`）——这是**性能节流修复**（避免高刷新率下反复读系统主题），**与窗口/句柄/显示器生命周期无关**。

⇒ **结论：#599 / #553 / #583 / #573 / #568 / #539 在 fork 上的状态与上游完全相同，不存在"fork 已修"的缓解因素。**

### 5.2 fork 确实更强的地方（**主窗**侧，与 #612/#553/#539 部分相关）

| 能力 | 上游 (3b91b5c) | fork (5e756e7) | 对本任务单的意义 |
|---|---|---|---|
| **句柄代次协议** | 无 | ⑂ `MainForm_Transparent.cs:345-370`：`WindowHandleGeneration.Instance.Advance()` 为**唯一**递增点；`TryConsumeControlledRecreate` 区分"受控重建"与"首次创建" | 杜绝"旧代次 HWND 数值被复用"造成的误判（⑂ `MainFormWinHelper.cs` 新增 `IsWindowAlive` 且要求同代次） |
| **句柄销毁时的资源回收** | 无 | ⑂ `MainForm_Transparent.cs:508-521` `OnHandleDestroyed`：停 LiveGlass 采集、停 GPU 会话、把 Region 交还 WinForms 释放（修掉上游"先 Dispose 再赋值"造成的悬空引用） | **#553 的"句柄重建"语义在主窗侧已被 fork 正确处理**；任务栏窗仍无对等件 |
| **窗口轮廓唯一写入者** | `MainFormWinHelper` 自己写 DWM + Region，且 `Resize` 被**绑定两次**（`MainFormWinHelper.cs:50` + `MainForm_Transparent.cs:213`）⇒ 轮廓重建两遍 | ⑂ 新增 `WindowAppearanceController`（821 行）为唯一写入者；`MainFormWinHelper.ApplyRoundedCorners` 改为转发 | 与本任务单无直接关系，但说明 fork 在窗口生命周期上做了系统性重构 |
| **显示变化时主窗位置语义** | `WM_DISPLAYCHANGE` → `RestorePos()`，**无条件回放保存位置**（会覆盖用户拖动结果） | ⑂ `MainFormBizHelper.cs:39-48` `ReconcileDisplayChange()`：当前窗口仍与任一屏工作区相交 ⇒ `ClampToScreen()`；否则才 `RestorePos()`；拖拽中直接 return | **fork 在 #539 的"主窗"部分更稳**（不回放旧位置）；**任务栏部分无差异** |
| **GPU/LiveGlass 渲染路由随显示变化重建** | 无 | ⑂ `NotifyGpuDisplayChanged()` / `NotifyGpuSizeChanged()` / `NotifyLiveGlass*` 系列 | fork 新增能力的自洽性，**不改变任务栏定位** |

### 5.3 fork 未覆盖的（与上游同病）

- 任务栏面板的 **z-order 维护**（#599）
- 任务栏面板的 **父窗销毁自愈**（#553）
- **目标设备 → 屏幕的重解析**（#573 / #583）
- **任务栏矩形缓存键完整性**（#539）
- **Dock 方向枚举**（#568）
- **锁屏/唤醒（`WM_POWERBROADCAST`）、显示设置（`WM_SETTINGCHANGE`）、热插拔（`WM_DEVICECHANGE`）** —— 上游与 fork **均为 0 处处理**
- **置顶 z-order 的自动恢复**（#612）—— 上游与 fork 的 `RefreshTopMost` / `IsTopMostStyleApplied` / `TopMostTick` 逐行相同

---

## 6. 未能验证项及原因

| # | 未能验证的内容 | 原因（缺少的环境条件） | 需要用户/环境补充的数据 |
|---|---|---|---|
| 1 | **#599 真实驱动热更新**后的合成层遮挡 | 会改动本机显卡驱动，超出取证边界 | 驱动热更新（或用 `SetWindowPos` 把 `Windows.UI.Composition.DesktopWindowContentBridge` 抬到同级最前）后的监控条截图 + 合成层与面板的兄弟 z-order 快照 |
| 2 | **#553 锁屏/关闭显示器**后副屏下线 → 面板消失 → 重新点亮后不恢复 | 本机副屏**没有独立任务栏**（`Shell_SecondaryTrayWnd` 不存在），无法复现"父任务栏被销毁"；且未执行锁屏/关屏/驱动降级 | 锁屏/关屏前后副屏任务栏面板的截图；`Shell_SecondaryTrayWnd` 的创建/销毁时刻；LiteMonitor 进程 PID 是否不变 |
| 3 | **#583 RDP 接入/断开** | RDP 接入会中断本机会话/占用输入，属破坏性操作 | RDP 接入前后 + 断开后的面板位置截图；RDP 期间 `Screen.AllScreens` 的设备列表；LM 是否重启过 |
| 4 | **#573 开机竞态** | 需冷启动 + 副屏任务栏延迟就绪的特定硬件/驱动组合，且需多次冷启动统计 | 连续 ≥5 次冷启动的面板落屏记录（哪块屏）；副屏任务栏创建时刻 vs LM 启动时刻 |
| 5 | **#568 Win10 真实环境**（本机为 Win11）+ **Dock 方向改变**（顶/左/右） | 本机是 Win11 26300，任务栏只在下边；Win11 24H2+ 原生不支持侧边停靠（需第三方工具） | Win10 上「图标移动到中间」的复现录屏；任务栏移到顶部/左侧后的面板截图；`MSTaskSwWClass` 宽度/左边界的前后对比 |
| 6 | **#539 真实分辨率切换** | 远程会话改分辨率有中断风险且改变用户桌面状态 | 切分辨率前/中/后各一张面板矩形快照（位置+宽度+高度）；「重启 LM 恢复」的确认记录 |
| 7 | **#612 被置顶窗口/全屏/UAC 压住的观感** | 需要并发运行的置顶/全屏/UAC 场景 | 置顶失效时的面板 z-order 快照；纯任务栏模式（主窗隐藏）下看门狗是否运行的日志 |
| 8 | LiteMonitor **进程内**的真实行为（句柄重建后的重挂、面板最终落到哪块屏） | 未构建/运行产品（本次为静态取证，且主控要求不跑长构建/全量测试） | 若需要动态确认，需授权构建并在多屏机器上跑一次带日志的构建 |

---

## 7. 建议优先级

| 优先级 | 动作 | 依据 | 冲突风险 |
|---|---|---|---|
| **P0** | cherry-pick 上游 **PR #600**（MERGEABLE，3 文件） | 直接修复 #599；本探针已实测其 z-order 算法语义正确（父兄弟插入 → 检出 → `HWND_TOP` 修复）；改动面极小 | 与 fork 冲突面 ≈0（涉及文件在 fork 侧除主题节流外未改） |
| **P1** | cherry-pick 上游 **PR #495**（MERGEABLE，3 文件） | 修复 #573 / #583 的根因（`FindHandles` 回退主屏 + 永不重解析）；顺带修副屏 DPI 混算 | 需检查与 fork `TaskbarForm.cs` 主题节流改动的同文件冲突 |
| **P1** | cherry-pick 上游 **PR #555**（MERGEABLE，5 文件） | 覆盖 #553 / #539 / #568 的主修复点：10s 恢复期、`WM_POWERBROADCAST`、缓存键扩展、Win10 内层句柄校验、**挤占跟踪状态重置** | 5 文件冲突面较大（`TaskbarWinHelper` / `TaskbarBizHelper` / `TaskbarStrategyWin10` / `TaskbarForm` / `MainForm_Transparent`）；`MainForm_Transparent` 在 fork 有 296 行改动，需手工合并 |
| **P2** | 人工合并上游 **PR #373**（CONFLICTING，2 文件） | #612 的上游候选修复；`MainFormWinHelper.cs` fork 侧改动大（外观控制器转发），冲突需手工解 | 高（`MainFormWinHelper.cs` fork 侧 +116 行） |
| **P2** | fork 自研：**置顶 z-order 探针**（样式位 + 同 band 内 z-order 双判），并去掉 `TopMostTick` 的 `!_form.Visible` 早退（或按模式分别处理） | #612 在 fork 无可用上游成品 PR（#373 冲突）；且纯任务栏模式的看门狗停摆是 fork 侧必须自己决策的产品问题 | 低（新增文件 + 小改） |
| **P2** | fork 自研：**Dock 方向枚举**（用 `WorkingArea`/`Bounds` 四边差值判定，替换宽高比与 `reservedBottom` 猜测）+ Win11 `timeWidth` 按 DPI 实测 | #568 的 Win11 侧与 Dock 方向覆盖；上游无对应 PR | 中（`TaskbarBizHelper` / `TaskbarWinHelper` 定位公式重写，需回归 Win10/Win11 双路径） |
| **P3** | fork 自研：**任务栏窗对等 fork 主窗的生命周期能力** —— `TaskbarForm.OnHandleDestroyed` → 通知 MainForm 重建；`TaskbarForm.OnDpiChanged`；补 `WM_SETTINGCHANGE` / `WM_DEVICECHANGE` | fork 已有 `WindowHandleGeneration` 协议与 `OnHandleDestroyed` 范式，可直接复用到任务栏窗（这是 fork 相对上游的差异化优势点） | 中 |
| **P3** | 评估上游 **PR #575**（Win11 改用 top-level overlay 渲染） | 从结构上绕开 Win11 任务栏子窗的合成/缩放遮挡（同时触及 #599 / #568 / #539） | **高**：改变任务栏挂载模型，会与 fork 已有的 #600 / #555 方案**互相冲突**，需先定方向 |
| **P3** | 评估上游 **PR #563**（主窗多屏自动隐藏 / 设置回退 / 缩放文字挤压） | 与 fork 主窗侧改进部分重叠，需先做差异比对再决定是否 cherry-pick | 低 |

---

## 8. 取证方法与可复现步骤

1. **Issue 状态核对**：`gh issue view <n> --repo Diorser/LiteMonitor --json number,state,title,body,comments,createdAt,updatedAt,labels`（7/7 全部 `OPEN`，状态与主控 2026-10-25 的批量核对一致）。
2. **上游/双侧代码取证**：`git -C G:/TOOL/LiteMonitor-fork show upstream/master:<file>`、`git grep -n <pattern> upstream/master -- src/`、`git diff upstream/master master -- src/UI/...`（全部只读）。
3. **上游候选修复检索**：`gh pr list --repo Diorser/LiteMonitor --state open` + `gh pr view/diff`。
4. **本机 Win32 探针**（4 个独立小程序，`.tmp/verification/tbprobe{,2,3,4,5}`，**只读或自清理**，收尾前已删除）：
   - **P1[A-C]**：复刻上游 `FindHandles` / `FindSecondaryTaskbar` / `GetTaskbarRect` 逐字算法 → 输出设备解析矩阵；复刻 LiteMonitor 挂载路径（顶层创建 → `SetParent` → `ApplyChildWindowStyle`）→ 观察 `Shell_TrayWnd` 子窗 z-order；插入新兄弟窗模拟驱动重置 → 验证上游 `SWP_NOZORDER` 不恢复、`HWND_TOP` 立即恢复。
   - **P2[B-C]**：忠实移植上游 `TaskbarStrategyWin10.AdjustHorizontalLayout` → 测量分辨率变化 / 任务列表宽度变化下的挤占记账。
   - **P5**：合成父子窗口 → 验证「父销毁 ⇒ 子销毁」（#553 的机制基石）。
   - 探针**只创建并立即销毁自己的测试窗口**，从未修改真实 `Shell_TrayWnd`（`Shell_TrayWnd` 本身也从未被销毁）；结束后已确认子窗枚举回到初始状态。
5. **未执行**：产品构建、全量测试、真实分辨率切换、RDP 接入、锁屏/关屏、显卡驱动热更新（均超出取证边界或破坏用户环境）。

---

*报告完 —— TASK-4 / 2026-10-25 / 上游 3b91b5c / fork 5e756e7*
