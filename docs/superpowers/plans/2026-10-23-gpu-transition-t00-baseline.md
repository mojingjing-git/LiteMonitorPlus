# GPU 运行时切换 · T00 基线与冻结候选

> **任务**：T00 · 基线与所有权（GPU 计划 §5 / §8 第 2 条）
> **产出时间**：2026-10-23
> **上游计划**：`docs/superpowers/plans/2026-10-22-gpu-legacy-runtime-transition-plan.md`（REVISION-1，复审第 2 轮 PASS）
> **性质**：本文件是**只读基线**。B 期不写生产代码、不改既有测试；本文件只记录事实与哈希，供 T01/T02 绑定候选使用。

---

## 1. 冻结候选

### 1.1 仓库状态（采集时点）

| 项 | 值 |
|---|---|
| 仓库根 | `G:\TOOL\LiteMonitor-fork` |
| 分支 | `fix/audit-remediation-v3` |
| HEAD | `e1bd58772a42ac73053ebeff8c2cd82c95b583f1` |
| HEAD 摘要 | `e1bd587 fix(updater): 重建包内更新器使"内嵌构建提交"可核验（LM-038）` |
| `git status --porcelain` | 1 项：`M docs/superpowers/plans/2026-10-22-gpu-legacy-runtime-transition-plan.md`（本轮计划文本更正，非生产代码） |
| `master` | `9ffb4219`（未动） |

> 计划 §3 记录的 HEAD 为 `0f1177e0d005c2f5b6b68a81dfc29de9722ac263`，**已过时**；以本表为准。

### 1.2 候选输入哈希

聚合算法：对组内文件按**相对路径排序**，拼接 `"<相对路径>\0<小写SHA256>\n"`，对该 UTF-8 字节流取 SHA-256。与文件枚举顺序、换行风格无关，可复现。复现脚本：`.artifacts/t00-candidate-hashes.ps1`（工作区内，未入库）。

| 组 | 文件数 | 聚合 SHA-256 |
|---|---|---|
| 两个探针（`tools/GpuHostLifecycleProbe/**` + `tools/GpuUiProbe/**`，已排除 `bin/`、`obj/`） | 7 | `6c49338496b4295ff678c65f82eb3323d08758647d7d0b3a1a3e4bfe8a3b593c` |
| 生产 `src/Rendering/**` | 22 | `a39d49ce83d13a7bbba0bbe2536a40583831eb742bf5bef7f0d62e831ba8a8e2` |
| 生产 `src/UI/**` | 57 | `502629b54aded5d78c06540e83813fcceff54e19e80052f45969fe3d51dcb1e7` |
| 主题 JSON（`resources/themes/*.json`） | 15 | `76422a86cf1fe20a2b1fafacbfbd24834f201283f388f278fea85f465c483cf6` |
| 配置与 shader（`resources/*.json` + `resources/assets/*.json` + `resources/shaders/*`） | 8 | `1078b22e49b3642afa27bb0784790e3863a4a674a06ee8a102c2851dbb1d9e65` |

相关二进制：

| 路径 | 字节 | SHA-256 |
|---|---|---|
| `resources/LiteMonitor++.Updater.exe` | 185 906 | `5d1ae3fd45acc170df01e49334beeedb875024102a9b73670ba5a960d179a036` |
| `bin/Debug/net8.0-windows/LiteMonitor++.exe` | 528 896 | `069b3721507d6b5e49b08a9ed858dcc33558ea813640d9d86fadea511bf42bb0` |

### 1.3 候选有效性的边界（必须随 T01 报告一并声明）

1. **T01 会修改探针本身**（计划要求"扩展 `tools/GpuHostLifecycleProbe`"）。⇒ T01 报告必须给出**改后**的探针哈希，并声明"生产侧 `src/Rendering/**` 与 `src/UI/**` 聚合哈希未变"。仅探针哈希变化是预期的，不构成候选失效。
2. **C 期（T03–T08）必然改动 `src/UI/**` 与 `src/Rendering/**`** ⇒ 本候选只对 **B 期（T01/T02）** 有效；进入 C 期前必须重新冻结（计划 §8 第 8 条）。
3. **若运行期间检测到被测源文件哈希变化 ⇒ 结论作废重跑**（计划 §3 冻结候选要求 3）。T01 必须实现该检测，不能靠人工声明。
4. 本候选**不覆盖** `src/System/Backdrop/**`（live 捕获链）。T01 若触及 live 场景，须另行冻结该目录。

---

## 2. 验证环境

| 项 | 值 |
|---|---|
| OS | Windows 10.0.26300.0（`Microsoft Windows NT 10.0.26300.0`） |
| PowerShell | 7.6.6 |
| .NET SDK | 8.0.426 |
| **管理员** | **否**（主程序 `requireAdministrator`）⇒ 计划 §7.2 的 **G05 / G09 需人工在场** |

### 2.1 屏幕与 DPI

| 设备 | 显示器矩形 | 工作区 | DPI | 缩放 |
|---|---|---|---|---|
| `\\.\DISPLAY1`（主屏） | `0,0 2194x1234` | `0,0 2194x1186` | 96×96 | 100% |
| `\\.\DISPLAY2`（副屏） | `-1152,0 1152x2048` | `-1152,0 1152x2048` | 96×96 | 100% |

- 副屏 **X = −1152 < 0** ⇒ 计划 **G07（X<0 副屏跨屏）** 的硬件前提**存在**。
- 副屏为**竖屏**（1152×2048）⇒ 计划 **G06（横→竖→横）** 的几何条件**存在**。
- 两屏 DPI 均为 96 ⇒ 计划 **G08（不同 DPI 屏幕往返）** 的**实机部分无对应环境**，只能按计划 §7.2 记 `BLOCKED` 并给人工步骤；不得用数学夹具冒充跨 DPI 通过。

### 2.2 GPU 适配器

| 适配器 | 驱动 | 当前模式 |
|---|---|---|
| **NVIDIA GeForce RTX 3080** | 32.0.16.1088 | 3840×2160 ×32bpp |
| NVIDIA GeForce RTX 3080 Ti | 32.0.16.1088 | — |
| GameViewer Virtual Display Adapter | 15.6.5.199 | — |
| Meta Virtual Monitor | 5.3.57.114 | — |
| MuMu Virtual Display Adapter | 20.36.41.498 | — |
| Todesk Virtual Display Adapter | 16.44.2.509 | 2560×1440 ×32bpp |

> ⚠️ 本机存在 **4 个虚拟显示适配器**。flip-model 交换链在不同适配器上的行为可能不同（尤其 `MakeWindowAssociation` 与 WDA）。T01 报告**必须记录探针窗口实际落在哪块适配器上**（可用 `IDXGIAdapter1.EnumOutputs` 的桌面坐标与 `DXGI_OUTPUT_DESC.DesktopCoordinates` 判定），不得笼统写"NVIDIA 显卡"。

### 2.3 窗口纪律（T01 必须遵守，计划 §7.3）

- 默认**非置顶、不激活、不抢焦点**（现有探针 `ProbeForm` 已有 `ShowWithoutActivation => true` 与 `WS_EX_NOACTIVATE`，`:61-72`）。
- 单轮 ≤ 60 秒；10 次往返组 ≤ 120 秒；外部 watchdog 必须存在。
- 只清理本次记录的 PID 及对应启动身份。
- 窗口测试**串行**运行，避免彼此遮挡导致假失败。

---

## 3. §4.7 HWND 持有者清单

> 判定口径：「随 Form 保留」= 托管引用随对象存活；「自动恢复」= 新 HWND 上无需显式重绑即恢复；「需显式重绑」= 必须在重建路径中显式处理。
> **"不存在" 是有效结论**——计划 §4.7 要求"修改仅限真实依赖，不能为了清单新增不存在的机制"。

| # | 机制 | 真实路径:行号 | 绑定对象 | 随 Form 保留 | 新 HWND 自动恢复 | 结论 | 依据 |
|---|---|---|---|---|---|---|---|
| 1 | `MainFormWinHelper` | `src/UI/Helpers/MainFormWinHelper.cs:15`（类）、`:17` `_form` | 托管 `Form` | 是 | n/a | **保留** | 只持 `Form` 与 `WindowAppearanceController` 两个托管引用；**无缓存 HWND 字段**，全部 Win32 调用现取 `_form.Handle`（`:75/:98/:100/:104/:144/:146/:148/:197/:199`） |
| 2 | DWM 角偏好 / 边框色 | `src/UI/Helpers/WindowAppearanceController.cs:739/:740/:750/:754` | HWND 属性 | 否 | 是 | **自动恢复** | `MainForm_Transparent.cs:270` `_windowAppearance?.OnHandleCreated()` → `WindowAppearanceController.cs:437-442` → `Reapply()`（`:390`）→ 重取 `SnapshotProvider` 快照（`:397`）并重写 DWM/Region |
| 3 | 窗口 Region（`SetWindowRgn`） | 写 `WindowAppearanceController.cs:676`、`:702`；清 `:459`、`:515`、`:725` | HWND | 否 | 是 | **自动恢复** | `OnHandleDestroyed`（`:451-465`）把 `_form.Region = null`（`:459`）且 `_regionAssigned=false`（`:456`）；`MainForm_Transparent.cs:270` → `Reapply()` 重新赋值 |
| 4 | 穿透 `WS_EX_TRANSPARENT` / `WS_EX_LAYERED` | `MainFormWinHelper.cs:144/:146/:148`；常量 `:261/:262`；`CreateParams` `MainForm_Transparent.cs:48-52` | HWND ex-style | 否 | 是（按配置） | **自动恢复** | `CreateParams` 每次建窗按 `_cfg.ClickThrough` 重新置位。⚠ `SetClickThrough(false)`（`:148`）**只清 `WS_EX_TRANSPARENT`、不清 `WS_EX_LAYERED`**（见 §7 GPU-003） |
| 5 | 分层透明度 `Form.Opacity` | `MainFormWinHelper.cs:113/:128`、`MainForm_Transparent.cs:193`、`src/Core/Actions/AppActions.cs:99-100` | `Form` 托管属性 | 是 | 部分 | **保留（需实机确认）** | `Opacity` 是托管属性，随对象保留。**推断（未在代码中确认）**：分层 alpha 依赖 HWND 上的 `WS_EX_LAYERED`，重建后是否自动恢复需 T01/T03 实机 |
| 6 | 置顶（`WS_EX_TOPMOST` + Z 序） | `MainFormWinHelper.cs:75/:76/:90/:98/:100/:104`；`CreateParams` `MainForm_Transparent.cs:42-45` | HWND ex-style + Z 序 | 否 | 是 | **自动恢复** | `CreateParams:44` 注释原文「防止句柄重建后丢失置顶样式」；Z 序由 `MainForm_Transparent.cs:268` `RefreshTopMost(forceReinsert:true)` 重插 |
| 7 | `TaskbarCreated` 消息 ID | `MainFormWinHelper.cs:230-238`（`RegisterWindowMessage`）；调用 `MainForm_Transparent.cs:181`；字段 `:22` | **进程级**（消息 ID 非 HWND 作用域） | 是 | 是 | **保留** | 系统级 ID 缓存进 `readonly` 字段（`MainForm_Transparent.cs:22`），跨 HWND 重建不变；处理点 `MainForm_Transparent.cs:356` |
| 8 | `ChangeWindowMessageFilter` | `MainFormWinHelper.cs:235`（调用）、`:246`（签名）、`:248` `MSGFLT_ADD` | 非 HWND 作用域 | 是 | 是 | **保留** | 本仓库 P/Invoke 原型 `ChangeWindowMessageFilter(uint message, uint dwFlag)`（`:246`）**无 HWND 参数** ⇒ 不绑定 MainForm 句柄；只在构造函数调用一次 |
| 9 | 热键 / `NativeWindow` / hook | **不存在** | — | — | — | **不存在** | 全仓 `*.cs` 检索 `RegisterHotKey`/`UnregisterHotKey`/`SetWindowsHookEx`/`AddHook`/`RemoveHook`/`NativeWindow`/`AssignHandle`/`ReleaseHandle`/`CreateHandle` → **零命中**；`HotKey|HOTKEY`（忽略大小写，不限扩展名）→ **零命中**。唯一 `Microsoft.Win32` 系统事件：`src/System/HardwareServices/FpsCounter.cs:802` `SystemEvents.SessionEnding`（静态事件，非 HWND 绑定） |
| 10 | LiveGlass 捕获链 | `src/UI/LiveGlassController.cs:76` `_form`、`:384` 取句柄、`:295-300` `OnHandleChanged()` | 托管 `Form` | 是 | 是 | **自动恢复** | `BuildRequest()`（`:379-399`）每次现取 `_form.Handle`（`:384`）；重建 → `MainForm_Transparent.cs:272` `NotifyLiveGlassHandleChanged()` → `UIController.cs:258` → `LiveGlassController.cs:295-300` 递增代次 + `RefreshRequest()` |
| 11 | Backdrop 请求中的裸 `IntPtr` | `src/System/Backdrop/BackdropServiceRequest.cs:81` 赋值（`:50` 注释）；`BackdropCaptureAdapters.cs:266`；`ScreenCaptureFrameSource.cs:62`（`:49` 赋值） | 请求快照 | 否 | 是 | **自动恢复** | 请求对象被 `RefreshRequest()`（`LiveGlassController.cs:345`）整体替换，新请求带新 HWND；旧请求由代次判定丢弃 |
| 12 | Backdrop 瞬时 WDA（抓屏自排除） | `ScreenCaptureFrameSource.cs:829` 读、`:835` 写 `WDA_EXCLUDEFROMCAPTURE`、`:858` 恢复、`:1035` `RestoreAffinity` 写 `WDA_NONE` | **写在 HWND 上** | 否 | 是 | **自动恢复** | 每帧读写同一 HWND 的 affinity，抓完即恢复（`:855-859` `finally`） |
| 13 | GPU 交换链（flip-model） | `src/Rendering/Presentation/GpuSceneRenderer.cs:178` `CreateSwapChainForHwnd(..., windowHandle, ...)`；`:164` `SwapEffect = SwapEffect.FlipSequential`；`:644` `_swapChain.Present(1, PresentFlags.None)` | **绑定 HWND** | 否 | 是 | **自动恢复** | 旧链随 `GpuSceneRenderer.Dispose()`（`:1026`）释放；`GpuPresentationHostController.cs:713-714` 释放 renderer；重建后 `MainForm_Transparent.cs:273` `NotifyGpuHandleChanged()` → `UIController.cs:272-292` 以新句柄建宿主与新链（`GpuPresentationHostController.cs:178/:204`） |
| 14 | DXGI 窗口关联 | `GpuSceneRenderer.cs:184` `_factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter)` | **工厂级** | 否 | **不确定** | **需显式重绑（待证）** | 关联建立在**工厂**上，生命周期长于交换链；全仓 `MakeWindowAssociation` 仅此一处，**无任何解除/复位调用**。**推断（未在代码中确认）**：旧 HWND 销毁后残留行为需 T01/T04 实机判定（GPU-004） |
| 15 | `PresentationSession` 的 WDA 状态 | `src/Rendering/Presentation/PresentationSession.cs:169`/`:209` `_windowHandle`；`:110` `_originalAffinity`；`:358-368` 读原值；`:378-388` 写；`:408-418` 恢复 | HWND + **进程内缓存原值** | 否 | 是（经宿主重建） | **自动恢复（但有缺口）** | `GpuPresentationHostController.StopCore()`（`:699-711`）释放 `_presentation`；重建宿主时 `Start()`（`:201-213`）新建 `PresentationSession` 并在新 HWND 上重新 `TryGetDisplayAffinity`（`PresentationSession.cs:358-368`） |
| 16 | 尺寸 / 位置通知 | `MainForm_Transparent.cs:248-255` `this.Resize +=`；`:256` `this.LocationChanged +=` | `Form` 托管事件 | 是 | 是 | **保留** | WinForms 事件挂 Form 对象，非 HWND；回调体只调 `_winHelper.ApplyRoundedCorners()` + `NotifyLiveGlass/GpuSizeChanged()`（`:250-254`） |
| 17 | `WM_SIZE` / `WM_MOVE` / `WM_WINDOWPOSCHANGED` / `WM_EXITSIZEMOVE` / `WM_ENTERSIZEMOVE` | **不存在** | — | — | — | **不存在** | 全仓 `src/` 检索上述常量 → **零命中**；尺寸/位置一律走 #16 的托管事件 |
| 18 | `WM_DISPLAYCHANGE` | `MainForm_Transparent.cs:28`（常量）、`:376-397`（`WndProc` 内 500 ms 防抖 → `_bizHelper?.ReconcileDisplayChange()` `:392` + `_ui?.NotifyGpuDisplayChanged()` `:393`） | `WndProc` 覆写（挂 Form 对象） | 是 | 是 | **保留** | `protected override void WndProc`（`:354`）属 Form 实例，新 HWND 自动走同一覆写 |
| 19 | `WM_DPICHANGED` | 无显式消息处理；走 `MainForm_Transparent.cs:422-432` `OnDpiChanged`（`:426` ApplyTheme、`:428-429` 尺寸通知、`:430` ApplyWindowAppearance、`:431` Invalidate） | `Form` 覆写 | 是 | 是 | **保留** | WinForms 把 `WM_DPICHANGED` 派发到 `OnDpiChanged` |
| 20 | `WM_SETTINGCHANGE`（针对本窗） | **不存在**；唯一出现为**广播**：`src/UI/Helpers/SystemActions.cs:41`（常量）、`:59-66` `SendMessageTimeout((IntPtr)HWND_BROADCAST, …)`、`:40` `HWND_BROADCAST = 0xffff` | 无（广播） | n/a | n/a | **不存在** | 广播不绑定任何 HWND |
| 21 | `SystemEvents.DisplaySettingsChanged` | **不存在** | — | — | — | **不存在** | 全仓 `Microsoft\.Win32|DisplaySettingsChanged` 检索：`Microsoft.Win32` 仅用于 `Registry`（`SystemActions.cs:7`、`DriverInstaller.cs:1`、`GpuVramCapacity.cs:5`、`PerformanceCounterManager.cs:154`）与 `SystemEvents.SessionEnding`（`FpsCounter.cs:802`）；**无** `DisplaySettingsChanged`、**无** `UserPreferenceChanged` |
| 22 | 托盘图标 `NotifyIcon` | `src/UI/Helpers/MainFormBizHelper.cs:24` 字段、`:56` `new NotifyIcon()`、`:171-173` 初始化、`:209` `ShowBalloonTip`、`:212` `SetTrayVisible`、`:555-556` 释放 | WinForms 组件（非 HWND 持有者） | 是 | 是 | **保留** | `NotifyIcon` 用 Shell 图标 API，与 MainForm 的 HWND 无绑定 |
| 23 | 托盘右键菜单 `ContextMenuStrip` | `MainFormBizHelper.cs:177-184` `_tray.MouseUp` → `_form.ContextMenuStrip?.Show(Cursor.Position)`；`:196-205` `RebuildMenus()`；`src/UI/MenuManager.cs:51` `Build(MainForm form, …)` | 托管 `ToolStrip`；菜单项闭包**强引用 `MainForm` 对象** | 是 | 是 | **保留** | `ContextMenuStrip` 是托管 `ToolStrip`，`Show()` 时临时创建原生弹出窗，不绑定 MainForm 的 HWND。⚠ 存在 `MainForm → ContextMenuStrip → 闭包 → MainForm` 循环引用，**不阻碍** HWND 重建 |
| 24 | 托盘双击恢复 | `MainFormBizHelper.cs:186-193` `_tray.MouseDoubleClick` → `((MainForm)_form).HideMainWindow()` / `ShowMainWindow()` | 强引用 `MainForm` **对象** | 是 | 是 | **保留** | 引用的是 Form 对象不是 HWND；`ShowMainWindow()`（`MainForm_Transparent.cs:333-344`）在重建后仍可 `Show()` |
| 25 | `TaskbarForm`（任务栏子窗） | `src/UI/TaskbarForm.cs:12`（独立 `Form`）、`:16` `private readonly MainForm _mainForm`；`MainForm_Transparent.cs:100` `new TaskbarForm(_cfg, _ui, this)` | **独立 HWND**；强引用 `MainForm` 对象 | 是（对象） | 不适用 | **保留** | 它自己的 HWND 由 `TaskbarStrategyWin11.cs:29` `SetParent(_form.Handle, taskbarHandle)` / `TaskbarStrategyWin10.cs:51` 挂到 Shell 任务栏 —— 挂的是 **TaskbarForm 的** HWND，MainForm 重建不影响它 |
| 26 | `TaskbarForm` 分层/色键 | `src/UI/Helpers/TaskbarWinHelper.cs:57` `WS_EX_LAYERED`、`:59` `LWA_COLORKEY`、`:79` `SetLayeredWindowAttributes`、`:131/:134/:137` | TaskbarForm 自身 HWND | 不适用 | 不适用 | **保留** | 同 #25，属另一个 HWND |

---

## 4. 句柄代次唯一真相源现状

计划 §4.2 要求"HWND generation"与 `LiveGlassController._handleGeneration` **合并或同步**，**不得**出现两套各自递增、互不知情的代次。**实测结论：当前生产代码中句柄代次确为唯一。**

| 角色 | 路径:行号 | 语义 |
|---|---|---|
| 声明 | `src/UI/LiveGlassController.cs:87` | `private long _handleGeneration = 1;`（long，64 位） |
| **唯一递增点** | `src/UI/LiveGlassController.cs:297` | 在 `OnHandleChanged()`（`:295-300`）内：`_handleGeneration++;` → `:298` `RefreshRequest()` → `:299` `ReleaseHeldFrame()` |
| 触发入口 | `MainForm_Transparent.cs:272` → `UIController.cs:258` → `LiveGlassController.cs:295` | `HandleCreated` 处理器（`MainForm_Transparent.cs:266-274`）**每次**建窗都调 `NotifyLiveGlassHandleChanged()`；**无「重建 vs 构造期首次创建」判别位** |
| 消费（写入请求） | `src/UI/LiveGlassController.cs:396` | 传给 `BackdropServiceRequest.FromOptions(options, handle, bounds, _handleGeneration, _sizeGeneration)` |
| 消费（请求属性） | `src/System/Backdrop/BackdropServiceRequest.cs:121` | `public long HandleGeneration { get; }` |
| 消费（请求等价） | `src/System/Backdrop/BackdropServiceRequest.cs:145` | `MatchesGeneration` 中 `&& HandleGeneration == other.HandleGeneration` |
| 消费（64→32 饱和投影） | `src/System/Backdrop/BackdropCaptureAdapters.cs:274` | `SaturateToInt(request.HandleGeneration)`（注释 `:278-284`「饱和，不回绕」） |
| 消费（引擎侧属性） | `src/System/Backdrop/ScreenCaptureFrameSource.cs:86` | `public int HandleGeneration { get; }`（文档行 `:85`） |
| 消费（旧帧丢弃） | `src/System/Backdrop/ScreenCaptureFrameSource.cs:185` | `BackdropFrameStamp.Matches`：`&& HandleGeneration == request.HandleGeneration` |
| 消费（帧戳构造） | `src/System/Backdrop/ScreenCaptureFrameSource.cs:764` | `new BackdropFrameStamp(request.ThemeGeneration, request.HandleGeneration, request.SizeGeneration, …)` |

- 全仓 `src/` 检索 `handleGeneration|HandleGeneration` 仅命中上表路径 ⇒ **GPU 侧无并行句柄代次计数器**，计划 §4.2 的"唯一真相源"前提成立。
- **并列的第二条代次**：`LiveGlassController._sizeGeneration`（`:88`，递增点 `:305`/`:313`）与句柄代次是**两套独立计数**。计划 §4.2 只约束句柄代次，尺寸代次不受影响。

---

## 5. 计划行号复核更正表

| 计划声称 | 当前真实 | 一致 | 实际代码 |
|---|---|---|---|
| `MainForm` 唯一声明 `src/UI/MainForm_Transparent.cs:14` | `:14` | ✅ | `public class MainForm : Form` |
| `MainForm` 全仓唯一声明 | 仅此 1 处 | ✅ | 检索 `class MainForm\b\|partial class MainForm` 全仓 → 1 处 |
| `LiveGlassController.cs:87` `_handleGeneration` | `:87` | ✅ | `private long _handleGeneration = 1;` |
| `LiveGlassController.cs:295-300` `OnHandleChanged()` | `:295-300` | ✅ | `_handleGeneration++;` / `RefreshRequest(); ReleaseHeldFrame();` |
| `BackdropServiceRequest.cs:121` | `:121` | ✅ | `public long HandleGeneration { get; }` |
| `BackdropServiceRequest.cs:143-147` | **`:141-148`**（比较在 `:145`） | ⚠ 区间偏移 | `return ThemeGeneration == other.ThemeGeneration` / `&& HandleGeneration == other.HandleGeneration` |
| `ScreenCaptureFrameSource.cs:85-87` | 属性在 `:86`（`:85` 为文档行） | ✅ | `public int HandleGeneration { get; }` |
| `ScreenCaptureFrameSource.cs:184-186` | **`:182-187`**（比较在 `:185`） | ⚠ 区间偏移 | `&& HandleGeneration == request.HandleGeneration` |
| `BackdropCaptureAdapters.cs:274` | `:274` | ✅ | `SaturateToInt(request.HandleGeneration), // → HandleGeneration（饱和，不回绕）` |
| `GpuStartupGuard.cs:7` | `:7` | ✅ | `public sealed class GpuStartupGuard` |
| `RenderingRoutePolicy.cs:5` `RenderingRoute` | `:5` | ✅ | `public enum RenderingRoute` |
| `RenderingRoutePolicy.cs:12` `RenderingRouteRequest` | `:12` | ✅ | `public sealed record RenderingRouteRequest(` |
| `RenderingRoutePolicy.cs:19` `RenderingRouteDecision` | `:19` | ✅ | `public sealed record RenderingRouteDecision(` |
| `RenderingRoutePolicy.cs:27` `IsGpu` | `:27` | ✅ | `public bool IsGpu => Route != RenderingRoute.Legacy;` |
| `RenderingRoutePolicy.cs:30`（类）/ `:32`（`Decide`） | `:30` / `:32` | ✅ | `public static class RenderingRoutePolicy` / `public static RenderingRouteDecision Decide(...)` |
| `UIController.cs:951` 消费点 | `:951` | ✅ | `if (decision.IsGpu \|\| _gpuRoute?.IsGpu == true)` |
| `UIController.cs:299-302` `HandleCreated` 来源注释 | `:297-302` | ✅ | 注释体「`Control.CreateGraphics()` 强制创建句柄…`HandleCreated` 不再触发…」 |
| `UIController.cs:360` / `:954` | `:360` / `:954` | ✅ | `public void StopGpuPresentation()` / `if (!decision.IsGpu)` |
| `UIController.cs:209/:215` | `:209` / `:215` | ✅ | `_form.Invalidate(); _form.Update();` … `ApplyGpuRenderingRoute(t);` |
| `UIController.cs:363` WDA 失败保留已 Dispose 宿主 | `:363` | ✅ | `if (_gpuPresentation?.WindowCapturePolicyRestored == false)` |
| `UIController.cs:634` Tick 启动 GPU | `:634-638` | ✅ | `if (_gpuRoute?.IsGpu == true && _gpuPresentation?.NeedsRestart == true && _form.IsHandleCreated)` |
| `MainForm_Transparent.cs:266` `HandleCreated` | `:266` | ✅ | `this.HandleCreated += (_, __) =>` |
| `GpuSceneRenderer.cs:164` `FlipSequential` | `:164` | ✅ | `SwapEffect = SwapEffect.FlipSequential,` |
| `GpuSceneRenderer.cs:178` 交换链绑主窗口 HWND | `:178-183` | ✅ | `_swapChain = _factory.CreateSwapChainForHwnd(deviceService.Device, windowHandle, …)` |
| `GpuSceneRenderer.cs:644` `Present` | `:644` | ✅ | `_swapChain.Present(1, PresentFlags.None).CheckError();` —— **确认是 `Present`（同步间隔=1），不是 `Present1`**；全仓 `Present1` 检索 → **零命中** |
| `PresentationSession.cs:65` HWND 已销毁按完成处理 | `:65-71` | ✅ | `if (windowHandle == IntPtr.Zero \|\| !IsWindow(windowHandle))` → `errorCode = null; return true;` |
| `GpuPresentationHostController.cs:740` 再次 Dispose 可清理 | `:740-753` | ✅ | `public void Dispose()` → `if (_disposed) { if (_presentation is not null) StopCore(); return; }` |
| `ThemeRenderingConfig.cs:136-141` 强制 legacy/legacy | `:136-141` | ✅ | `errors.Add("rendering.fallback must select legacy/legacy.");` |
| `ThemeRenderingConfig.cs:53-57` `Fallback` 走 `CopyDictionary` | 拷贝在 **`:57`**（块 `:51-57`） | ⚠ 行号偏移 | `CopyDictionary(Fallback ?? new Dictionary<string, JsonElement>());` |
| `AppearanceFields.cs:256-260` `CloneRendering` | `:256-260` | ✅ | `public static ThemeRenderingConfig CloneRendering(ThemeRenderingConfig source)` |
| `ThemeManager.cs:563` `PruneNode` | `:563` | ✅ | `PruneNode(theme.Rendering, typeof(ThemeRenderingConfig));` |

### 5.1 已被 T00 推翻的计划断言

| 计划断言 | 实测结论 |
|---|---|
| §4.1：「全仓 `src/` 与 `tools/` 搜索 `RecreateHandle` **零命中**，本项目无先例」 | **已过时**。`src/` 确实零命中，但 **工具与测试侧已有先例**：`tools/GpuUiProbe/Program.cs:469-473` 用反射 `typeof(Form).GetMethod("RecreateHandle", …).Invoke(form, null)`；`tests/AppearanceWindowShapeProbe/WindowShapeProbeRunner.cs:1183-1197` 有 `TryRecreateHandle` 辅助。**这些先例只证明"反射调用受保护方法"可行，不构成生产可用的依据** —— 生产仍按 §4.3 步骤 4 走 MainForm 内部受控入口。计划文本已在 §4.1 就地更正。 |
| §4.7 未点名但**已存在**的重绑机制 | `PresentationSession.RecreateWindow(IntPtr, RenderGenerations, WindowCapturePolicy, out string?, out string?)`（`src/Rendering/Presentation/PresentationSession.cs:176-214`）：先 `RestoreOriginalAffinityCore` → 再 `ApplyCapturePolicyCore(newHandle)` → 更新 `_windowHandle`/`_generations`。**全仓无生产调用点**，仅 `tests/LiteMonitor.Tests/PresentationSessionTests.cs:161` 调用。T02 必须决定它是被正式采用还是废弃。 |

---

## 6. 新发现缺陷登记（GPU-001 ~ GPU-005）

> 编号独立于 V3 修复批次的 `LM-` 序列。全部为**既有生产代码**的问题，不是本轮新引入。

### GPU-001 · 🔴 高 · WDA 恢复失败后 GPU 呈现**永久不可重启**

**已独立复核的完整调用链**（主审逐行核对，非采信单一 agent 自述）：

1. `GpuPresentationHostController.StopCore()`（`src/Rendering/Presentation/GpuPresentationHostController.cs:694-724`）：`_presentation.CompleteStop(...)` 失败 ⇒ 记录 `_lastErrorCode` ⇒ **再重试一次**（`:707-708`）；**两次都失败 ⇒ `_presentation` 不置 null**（`:708` 的条件不成立），即"清理责任仍被持有"。
2. `WindowCapturePolicyRestored => _presentation is null;`（`GpuPresentationHostController.cs:93`）⇒ 上一步之后它为 **false**。
3. `UIController.StopGpuPresentation()`（`src/UI/UIController.cs:360-369`）：`_gpuPresentation?.Dispose()` 后，`if (_gpuPresentation?.WindowCapturePolicyRestored == false) { _gpuFallbackCode = …; return; }` ⇒ **`_gpuPresentation` 不置 null**（保留已 Dispose 的宿主引用）。
4. 三条重启路径**全部被同一个早退挡住**：
   - `UIController.NotifyGpuHandleChanged()`（`:272-292`）：`:280` `if (_gpuPresentation is not null) return;`
   - `UIController.EnsureGpuPresentationStarted()`（`:309-322`）：`:312` `if (_gpuPresentation is not null) return;`
   - Tick 兜底（`:634-638`）：`_gpuPresentation?.NeedsRestart == true`，而 `NeedsRestart => !_disposed && …`（`GpuPresentationHostController.cs:81-85`）⇒ **已 Dispose 的宿主 `NeedsRestart` 恒为 false**。

**后果**：一旦 WDA（显示亲和性）恢复连续失败两次，**GPU 呈现在整个进程生命周期内永久不可用** —— 换主题、改设置、切管线、重建 HWND 都不行，只有重启进程能恢复。而 §4.1 主路线（HWND 重建）恰好依赖 `NotifyGpuHandleChanged()` 重建宿主 ⇒ **该缺陷直接卡死主线**。

**与计划 §4.5 的冲突**：§4.5 要求"宿主释放后由 UI 生命周期所有者继续持有它（清理对象）"且"新代次必须独立确认原始策略并取得所有权"。当前代码把**"宿主引用非空"**当成了**"清理责任仍在"**的代理 ⇒ 清理责任被保留的同时，**新代次的宿主也被永久禁止创建**。二者必须解耦。

**归属**：**T02 合同必须解决**（"清理责任转交"的字段与所有权形状），落地在 **T03/T04**。**B 期不修**（B 期不写生产代码）。

### GPU-002 · 🟡 中 · `_originalAffinity` 是进程内缓存，跨 HWND 代次不可复用

`PresentationSession` 把原 affinity 缓存在 `_originalAffinity`（`:110`），读写都针对 `_windowHandle`（`:169`、`:378`、`:408`）。HWND 重建后该缓存描述的是**旧窗口**，必须在新 HWND 上重新 `GetWindowDisplayAffinity`。现有路径之所以安全，是因为宿主整体重建（`GpuPresentationHostController.cs:201-213` 新建 `PresentationSession` → `PresentationSession.cs:358-368` 在新句柄上读原值）。**若 T02 选择复用同一 `PresentationSession`（用 `RecreateWindow`），必须先恢复旧句柄、再在新句柄重读原值**，不得沿用 `_originalAffinity`。

### GPU-003 · 🟡 中 · 开启鼠标穿透后窗口**永久**保持 `WS_EX_LAYERED`

`MainFormWinHelper.SetClickThrough(false)`（`src/UI/Helpers/MainFormWinHelper.cs:140-151`）在关闭穿透时**只清 `WS_EX_TRANSPARENT`**（`:148` `ex & ~WS_EX_TRANSPARENT`），**不清 `WS_EX_LAYERED`**；全仓 `WS_EX_LAYERED` 只有 `|=` 写入点，无清除点（`MainForm_Transparent.cs:51` `CreateParams` 与 `MainFormWinHelper.cs:146`）。

**为什么重要（外部权威，多源一致）**：`SetWindowDisplayAffinity` 在 `WS_EX_LAYERED` 窗口上**会失败**（返回 FALSE、`GetLastError() = 8` / `ERROR_NOT_ENOUGH_MEMORY`，这是一个误导性的错误码）。微软社区支持明确答复："Windows currently does not support monitor affinity for this type of layered window."；StackOverflow 同样记载 "That style makes SetWindowDisplayAffinity fails with code 8"。已知细节：以 `CreateWindow` 时就带 `WS_EX_LAYERED` 未必复现，而**事后用 `SetWindowLong` 加上 `WS_EX_LAYERED` 会复现** —— 而本仓库正是**事后**通过 `SetWindowLong` 置位（`MainFormWinHelper.cs:146`）。

**推断（未在本仓库实测）**：上述两点结合 ⇒ 一旦用户启用过鼠标穿透，GPU 的 WDA 排除（`WDA_EXCLUDEFROMCAPTURE`）可能失效，恢复路径可能失败 ⇒ 命中 **GPU-001** 的触发条件，把一个边缘情形变成常见路径。**需在 T01/T09 实测确认**（用 `GetWindowLong(hwnd, GWL_EXSTYLE)` 读回 ex-style + `GetWindowDisplayAffinity` 读回 affinity）。

> 引用来源：
> - Microsoft Learn 论坛「[Win32] SetWindowDisplayAffinity」（含微软社区支持 Fei 的答复）：https://learn.microsoft.com/zh-tw/archive/msdn-technet-forums/7ce400f0-ebda-4b95-869c-85b5b93f972d
> - StackOverflow「call Win32 API in flex to set Window Display Affinity」：https://stackoverflow.com/questions/30723579/call-win32-api-in-flex-to-set-window-display-affinity

### GPU-004 · 🟡 中 · `IsWindow` 无法区分"旧 HWND 已销毁"与"句柄值被复用"

`PresentationSession.cs:39` 与 `:65` 只用 `IsWindow(hwnd)` 判活。计划 §4.1 已禁止以 `newHwnd != oldHwnd` 断言重建，但此处是**同一问题的具体落点**：句柄值被系统复用时，旧清理可能命中新窗口。计划 §4.2/§4.5 要求的"单调递增代次"必须**贯穿到 WDA 清理路径**，而不只是 live 捕获链。

### GPU-005 · 🟡 低 · `GpuStartupGuard` 的 pending 语义比计划描述的更弱

pending 标记写在 `TryBeginAttempt`（`GpuStartupGuard.cs:152` → `WritePendingMarker` `:276-287`），删除在 `CompleteAttemptCore`（`:258-261`，最后一个 attempt 结束即删）。关键点：`GpuStartupAttempt.Dispose()`（`:316-324`）**无论首帧是否成功都会**调用 `CompleteAttempt`；而 `GpuPresentationHostController.StopCore()`（`:715`）正是 `_startupAttempt?.Dispose()`。⇒ **首帧失败但宿主正常停止时，pending 也会被清除**；保守保护只在**进程非正常退出留下文件残留**时生效（`:52-62` 检出 → `_conservative = true`）。`MarkFirstSuccessfulFrame` 调用点：`GpuPresentationHostController.cs:661`（成功 Present 之后）。

⇒ 计划 §4.4 的「`GpuStartupGuard` 的 pending/完成语义保持；不得通过每次 Tick 清标记规避保护」**缺少可操作判据**。T02 必须把 pending 的准确定义写进合同。

---

## 7. 其它观察（信息项，不登记为缺陷）

| 项 | 内容 |
|---|---|
| `Form.Opacity` 的分层 alpha 在 `RecreateHandle()` 后是否自动恢复 | **推断（未在代码中确认）**，需 T01/T03 实机 |
| `MakeWindowAssociation` 在旧 HWND 销毁后的残留行为 | **推断（未在代码中确认）**，需 T01/T04 实机（与 GPU-004 相关） |
| `ChangeWindowMessageFilter`（2 参原型）是否真实存在于 `user32.dll` | 未在本机验证（未执行运行时探针）。代码在 `MainForm` 构造函数（`MainForm_Transparent.cs:181`）无 try/catch 直接调用且产品可运行 ⇒ 不构成启动阻塞 |
| 业务对象未绑定 HWND（**正面结论**） | 硬件采样、插件调度、历史记录均未出现在任何 HWND 销毁回调中；`MainForm.OnHandleDestroyed`（`:410-420`）只停 LiveGlass/GPU，`OnFormClosed`（`:495-511`）才 `TrafficLogger.Save()` / `HardwareHistoryLogger.Save()` / `_ui?.Dispose()` / `_bizHelper.Dispose()` / `_windowAppearance?.Dispose()`。符合计划 §4.7「业务采样、插件调度和历史状态不得误绑到 HWND 销毁事件」 |
| `MainForm.OnHandleDestroyed` 会停掉 GPU 与 LiveGlass | `MainForm_Transparent.cs:410-420`：`:417` `_ui?.StopLiveGlass()`、`:418` `_ui?.StopGpuPresentation()`。即 HWND 销毁本身已触发停止 ⇒ 重建后需 `HandleCreated` 路径重新拉起 |
| Region 重建的边界 | `WindowAppearanceController.Reapply()`（`:390`）优先用 `SnapshotProvider` 当前快照（`:397`），取不到时回退 `_lastOptions`（`:404-411`）；**两者都空则不重算形状**（`:406-407`）⇒ 此时新 HWND 可能既无 Region 也无 DWM 角偏好 |
| `HandleCreated` 无判别位（**既有行为，非缺陷**） | `MainForm_Transparent.cs:266-274` 的处理器**每次**建窗都调 `NotifyGpuHandleChanged()`，无法区分"构造期首次创建"与"重建后创建"。这正是计划 §4.2 末段要求引入判别位的原因 —— T02/T03 必须引入，现有行为即"无条件" |
| 托盘菜单持有 Form 强引用 | `MainFormBizHelper.cs:203` `_form.ContextMenuStrip = MenuManager.Build((MainForm)_form, _cfg, _ui)`；`MenuManager.cs:51` 菜单项闭包捕获 `form` ⇒ `MainForm → ContextMenuStrip → 闭包 → MainForm` 循环引用。不阻碍 HWND 重建，但"菜单重建"与"HWND 重建"是两条独立生命周期 |

---

## 8. T01 的输入、判定规则与纪律

### 8.1 T01 必须回答的问题（计划 §4.1 的判定规则，不得含糊）

> `Control.RecreateHandle()` 在本项目 `MainForm` 上是否可安全使用？

| 分支 | 条件 | 后续 |
|---|---|---|
| **可安全使用** | 句柄销毁/创建事件序列完整、窗口状态恢复、GDI 动态画面恢复证据成立 | 走 §4.1 主线，报告须给出三条依据 |
| **不可安全使用** | 重建后 GDI 仍无法在桌面呈现 | **直接走 §9 备用路线**（独立 GPU 子 HWND），并按 §9 末尾要求重新复审与调整验收 |
| **不确定** | 证据不足以判定 | 结论记 `NOT_VERIFIED`，**不得**先落主线再打补丁 |

### 8.2 对照设计要求

- 必须覆盖**两个场景**：(A) **只释放交换链**（保持 HWND）；(B) **释放后重建 HWND**。两者在同一进程、同一窗口、连续输入下对照。
- 必须记录：**首次真实 Present** 成功与否、**HandleDestroyed/HandleCreated 事件序列**、**单调递增代次**、**GDI 动态画面是否恢复**。
- **不得**用 `newHwnd != oldHwnd` 作为唯一重建断言（计划 §4.1）。
- **不得**以 `DrawToBitmap` / `PrintWindow` / "GDI 函数被调用" / "offscreen render 成功" 单独证明 GDI 已恢复桌面呈现（计划 §7.3）。需要**实际桌面观察**（受控截取窗口所在屏幕区域并回读像素）。
- 固定输入必须使用**可识别合成背景与连续变化的测试指标**，避免"旧 GPU 冻结帧看上去像 legacy"的误判（计划 §7.2）。
- 必须等待 **GPU 首次成功呈现**后再操作；创建宿主 ≠ GPU 已运行。

### 8.3 纪律

- 单轮 ≤ 60 秒；外部 watchdog + 取消机制必须**先具备**再跑。
- 默认非置顶、不激活、不抢焦点（现有 `ProbeForm` 已具备）。
- 运行期间**检测被测源文件哈希变化**；变化 ⇒ 结论作废重跑（计划 §3 冻结候选要求 3）。
- 报告必须记录：探针实际落在哪块显示适配器、候选哈希、GDI 恢复判据的具体像素判据、以及所有 `NOT_VERIFIED` 项。
- **不得**为让结论好看而删用例、降断言或扩大异常吞噬。

---

## 9. 复现命令

```powershell
# 冻结候选哈希与环境事实
cd 'G:\TOOL\LiteMonitor-fork'
pwsh -NoProfile -File '.artifacts\t00-candidate-hashes.ps1'   # 工作区内脚本，未入库

# 仓库状态
git rev-parse HEAD
git log --oneline -1
git status --porcelain

# 依赖清单护栏（顺带验证包内更新器判定仍为"与本次源码构建一致"）
pwsh -NoProfile -File 'scripts/generate-dependency-manifest.ps1' -Mode Check
```

---

**T00 结论**：基线、冻结候选、§4.7 HWND 持有者清单、句柄代次真相源现状、行号复核表已完成；新增 5 项缺陷（GPU-001 ~ GPU-005），其中 **GPU-001 为高严重级别且直接卡死 §4.1 主线**，须由 T02 的"清理责任转交"合同解决。计划 §4.1 的 `RecreateHandle` 零命中断言已被推翻并就地更正。
