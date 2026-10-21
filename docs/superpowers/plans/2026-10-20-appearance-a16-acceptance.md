# A16 —— 主面板 M2 接线与玻璃主题验收

> 日期：2026-10-21
> 前置：A14（M0/M1 综合验收）✅、A15（实时背景服务及后端）✅
> 权威合同：`docs/superpowers/plans/2026-10-20-appearance-m2-contract.md`（已冻结）

---

## 1. A16 的验收要求（v4 §5 工作包表）

> A16 | 主面板 M2 接线与玻璃主题 | 前置 A14、A15 | **主集成者接启动/可见性/模式/尺寸/退出；主题 owner 产出 LiquidGlass.json；首帧有底色，无额外捕获/重复服务，配置和实际降级状态一致**

逐条核对：

| # | 要求 | 本轮实现 | 结果 |
|---|---|---|---|
| 1 | 主集成者接**启动** | `UIController` 构造时按 `Settings.LiveGlassEnabled` 决定是否启动；`LiveGlassController.StartService()` | ✅ |
| 2 | 主集成者接**可见性** | `MainForm.VisibleChanged` → `NotifyLiveGlassVisibility` → 服务 `SetSuspended(!visible)` | ✅ |
| 3 | 主集成者接**模式**（换肤） | `UIController.ApplyTheme` → `LiveGlassController.OnAppearanceChanged` → 主题代次随外观运行时自增并 `UpdateRequest` | ✅ |
| 4 | 主集成者接**尺寸** | `MainForm.Resize` / `OnDpiChanged` → 尺寸代次 +1 → `UpdateRequest` | ✅ |
| 5 | 主集成者接**退出** | `MainForm.OnFormClosed` → `StopLiveGlass()` → `UIController.Dispose` → `LiveGlassController.Dispose` | ✅ |
| 6 | 主题 owner 产出 `LiquidGlass.json` | `resources/themes/LiquidGlass.json`（详见 §3） | ✅ |
| 7 | **首帧有底色** | 接管不到帧时 `TryGetFrame` 返回 false ⇒ 背景层按 `glass → gradient → solid` 降级链回退 ⇒ 不会空洞/黑屏 | ✅ |
| 8 | **无额外捕获/重复服务** | 新增 **A16 门槛**（源码级硬断言，见 §5.1） | ✅ |
| 9 | **配置和实际降级状态一致** | 设置页状态行显示 `LiveGlassController.StatusText`（读服务的**真实** `Status`，不是回显配置值） | ✅ |
| 10 | 实时玻璃**默认关闭** + 显式开启 + 首次提示（M2 合同 §8） | `Settings.LiveGlassEnabled` 默认 `false`；设置页开关；首次开启弹副作用提示 | ✅ |

---

## 2. 接线点与生命周期映射

### 2.1 组件

| 组件 | 文件 | 职责 |
|---|---|---|
| `LiveGlassController` | `src/UI/LiveGlassController.cs`（新） | 按开关创建/销毁服务；窗口事件 → 服务调用；每帧租约交接；暴露实际降级状态 |
| `LiveGlassFrameSourceProxy` | 同上 | 帧来源代理（转发给"当前"服务）。**不是**第二个捕获循环 |
| `BackdropCaptureService` | `src/System/Backdrop/BackdropCaptureService.cs`（A15） | **唯一**的后台捕获循环 |
| `ScreenCaptureFrameSource` | `src/System/Backdrop/ScreenCaptureFrameSource.cs`（A15） | 捕获引擎，**只作单次抓屏原语**（其 `Start()` 永不被调用） |
| `ScreenCaptureBackendAdapter` / `BackdropBlurProcessorAdapter` | `src/System/Backdrop/BackdropCaptureAdapters.cs`（A15） | 引擎 → 服务 的适配器 |

### 2.2 生命周期映射

| 时机 | 触发点 | 服务调用 | 依据 |
|---|---|---|---|
| 启动 | `UIController` 构造 | `service.Start(request)` | 合同 §3.4 |
| 首次显示 | `MainForm.VisibleChanged` | `SetSuspended(false)` | 合同 §5.1 |
| 隐藏 / 最小化 | `MainForm.VisibleChanged` | `SetSuspended(true)` | 合同 §5.1 |
| 句柄重建 | `MainForm.HandleCreated` | 句柄代次 +1 → `UpdateRequest` | 合同 §3.4 第 5 条 |
| 句柄销毁 | `MainForm.OnHandleDestroyed` | `StopLiveGlass()`（停止捕获） | 句柄失效后不可再抓屏 |
| 尺寸变化 | `MainForm.Resize` | 尺寸代次 +1 → `UpdateRequest` | 合同 §3.4 第 5 条 |
| DPI 变化 | `MainForm.OnDpiChanged` | 尺寸代次 +1 → `UpdateRequest` | 物理客户区矩形随之改变 |
| 换肤 | `UIController.ApplyTheme` | 主题代次 +1 → `UpdateRequest` | 主题代次在 `ThemeAppearanceOptions` 里 |
| 退出 | `MainForm.OnFormClosed` | `StopLiveGlass()` → `Dispose()` | 恢复显示亲和性 |

### 2.3 帧的租约交接（每帧）

```
UIController.Render(g)
  └─ LiveGlassController.BeginPaint()
       ├─ 释放**上一帧**的租约（那一帧已经画完）
       └─ TryAcquireFrame(out lease)  → 持有本帧租约
  └─ UIRenderer / HorizontalRenderer
       └─ BackdropRenderer → frameSource.TryGetFrame()（**借用**当前租约的位图）
```

★ 顺序不可颠倒：先释放上一帧再接管本帧 ⇒ 接管失败时服务没有 active 租约 ⇒
`TryGetFrame` 返回 false ⇒ 背景层按降级链回退 ⇒ **首帧一定有底色**（要求 #7）。

### 2.4 为什么用代理而不是把服务直接塞给外观运行时

实时玻璃**默认关闭**（合同 §8.1），而 `AppearanceRuntime` 在构造时就要拿到帧来源。
代理是**终身唯一**的中转对象：开关只切换它背后的服务，外观运行时不必重建，
装饰层 / 纹理缓存得以保留。代理本身不抓屏、不持有后台任务。

---

## 3. 主题：`resources/themes/LiquidGlass.json`

| 项 | 值 |
|---|---|
| `version` | 4 |
| `name` | `LiquidGlass` |
| `backdrop.mode` | **`glass`** |
| `backdrop.opacity` | 0.72（合同归一化范围 0–1） |
| `backdrop.blur` | 12（范围 0–32） |
| `backdrop.fps` | 2（范围 1–6；合同 §3.4「捕获上限 6fps、默认 2fps」） |
| `backdrop.tint` | `rgba(9,17,30,1.0)`（深色纱罩；有效 alpha = 255 × 0.72 ≈ 0.72） |
| 渐变回退 | `#1B2C44 → #0A1220`，角度 90° |
| `layout.frameStyle` | **2**（圆角描边 —— 液态玻璃的轮廓特征，替代首版的切角 1） |
| `layout.cornerRadius` | **16**（圆角半径；窗口轮廓与绘制外框同源同值） |

完整取值表与配色取舍见 `docs/superpowers/plans/2026-10-20-appearance-liquid-glass-theme.md`。

★ **可读性以主题探针的判据实测值为准**（不是手算）：纳入
`tests/AppearanceThemeCheck` 后，探针曾报出 **4 项不达标**
（`textGroup` 3.81 < 4.5；`barLow` 2.35 / `barMid` 2.14 / `barHigh` 2.59 < 3.0）——
这是**真实的可读性缺陷**。修法是**改配色**而不是放宽判据：进度条三档改为与
`valueSafe` / `valueWarn` / `valueCrit` 同色，分组标题压深 ⇒ 改后 **442 / 442 全部通过**。

🔴 **第二轮（实机反馈后）整体改深色**：首版浅冷玻璃在实机上呈**浅蓝白平板**，
且降级态与玻璃态在画面上**无法区分**（违反合同 §7「降级不静默」）。本轮把整套配色翻到
**深色磨砂玻璃**（深底 + 亮字 + 低 alpha 深色纱罩），并把窗口轮廓从切角改成圆角描边。
改后主题探针**重跑仍是 442 / 442**（12 项可读性判据实测值见主题说明文档 §7.3）。

---

## 4. 显式开关与首次提示（M2 合同 §8）

| 合同条款 | 实现 |
|---|---|
| §8.1 实时玻璃默认关闭 | `Settings.LiveGlassEnabled` 默认 `false`；旧 `settings.json` 无该字段时反序列化为 `false`（System.Text.Json 缺省行为）⇒ 保持关闭 |
| §8.2 显式开启 + 首次提示在 A16 同时提供 | 设置页「外观」卡片里的开关 + 首次开启时的确认提示 |
| §8.3 提示必须如实表述副作用 | 文案见语言资源 `Menu.LiveGlassHint`：「启用实时玻璃需要实时捕获窗口背后的画面。**捕获瞬间，面板可能从录屏或截图中短暂消失或闪动。**」 |
| §8.4 不得据 A06 观察保证所有录屏软件无感 | 文案**不**承诺"无感"，只陈述可能现象 |
| §8.5 编辑器固定预览不启动服务、也不弹提示 | 预览走 `PreviewFrameSource`（`AppearanceRuntime` 的 `previewSource` 参数），**不接触** `LiveGlassController`；提示只在设置页开关处触发 |

★ 提示是**确认式**的：用户点"取消" ⇒ 不开启（开关拨回原位），不得"先开后问"。
提示只弹一次（`Settings.LiveGlassHintShown`）。

---

## 5. 验证结果

### 5.1 A16 新增门槛：唯一捕获服务与唯一帧来源挂载点

`tests/AppearanceM2Probe` → `SourceGateRunner.RunSingleCaptureLoopGate`（源码级硬断言）：

| 断言 | 判据 |
|---|---|
| `new BackdropCaptureService(` 在 `src/` 中出现次数 | **恰好 1** |
| 类型为 `ScreenCaptureFrameSource` 的变量被调用 `.Start(` | **0 次**（引擎自带循环绝不能启动） |
| 带 `frameSource` 的 `new AppearanceRuntime(` | **恰好 1**（唯一挂载点） |

**已做反向验证**（回退即失败，非假阳性）：

| 注入的违规代码 | 门槛的判定 |
|---|---|
| `_engine.Start(default);` | ❌ 失败并指出 `src/UI/LiveGlassController.cs @135 → _engine.Start(` |
| 再加 `new BackdropCaptureService(null, null);` | ❌ 失败并指出「应恰好 1 处，实际 2 处」 |

注入已还原，临时证据目录已删除。

### 5.2 串行复验结果

| 命令 | 结果 |
|---|---|
| `dotnet build LiteMonitor.csproj -c Release` | **0 错误 / 234 警告**（与 A15 基线一致，无新增） |
| `dotnet test tests/LiteMonitor.Tests/... -c Release` | **583 / 583** 通过 |
| `tests/AppearanceM2Probe/output/AppearanceM2Probe.exe` | **15 / 15** 通过（10 条合同门槛 + 4 条 A15 复核断言 + 1 条 A16 门槛） |
| `tests/AppearanceThemeCheck` | **442 / 442** 通过（原 335 + LiquidGlass 的 107 项） |
| `tests/AppearanceWiringProbe` | 全部通过 |
| `tests/AppearanceDecorProbe` / `AppearanceDecorRendererProbe` / `AppearanceWindowShapeProbe` | 全部通过 |
| `tests/AppearanceStaticBackdropProbe` | **21 / 21** 通过 |

证据目录：`docs/reviews/evidence/a14-9c16ad3/a16-wiring/`

🔴 **第二轮复验（深色改版 + 降级可见性 + 诊断日志）**：上表全部命令在第二轮改动后**重跑一遍**，
结果不变 —— 主工程仍 **0 错误 / 234 警告**（与基线一致）、单元测试仍 **583 / 583**、
M2 探针仍 **15 / 15**、主题探针仍 **442 / 442**、其余 5 个探针仍全部通过。

### 5.3 第二轮针对实机反馈的三处修复

| # | 实机症状 | 修复 | 落点 |
|---|---|---|---|
| 1 | 玻璃**没生效**但画面与正常态无法区分（静默降级） | 面板底部画「实时玻璃未生效：\<原因\>」。只在「开关已开 + 主题请求 glass + 服务确实在降级」三条件同时成立时出现，其余情况画面**零变化** | `src/UI/UIController.cs` → `UIController` → `DrawLiveGlassNotice`（竖屏与横屏两处都调） |
| 2 | 拿不到「服务为什么没出帧」 | 诊断日志写 exe 同目录 `liveglass.log`：**状态 / 生效模式 / 降级原因 / 后端原因 / 已发布帧数**，仅在状态变化时写（不每帧刷屏），任何写失败静默 | `src/UI/LiveGlassController.cs` → `LiveGlassController` → `LiveGlassLog` |
| 3 | 刷新请求打到了已停止的服务（死路径） | `RefreshRequest()` 先看 `svc.IsRunning`：在跑才 `UpdateRequest`，没在跑就 `Start` | `src/UI/LiveGlassController.cs` → `LiveGlassController` → `RefreshRequest` |

附带修掉两处隐患：降级提示的画刷在**换主题后不刷新**（现按颜色重建），
以及提示颜色取 `TextSecondary` 时可能为 null（现退回 `TextPrimary`，不再新增编译警告）。

---

## 6. 未验证项（不得被其它通过项覆盖）

| 条件 | 状态 | 归属 |
|---|---|---|
| **实机玻璃效果**（真实抓屏下的模糊强度、纱罩浓度、观感） | **未验证** —— 本代理会话不合成到交互桌面，窗口不出现在屏幕捕获里 | 用户（交互会话） |
| **实时玻璃到底有没有出帧**（后端原因） | **未验证** —— 已加 `liveglass.log` 诊断日志，但**还没跑过一次**拿到日志 | 用户（交互会话）跑一次并回传 `bin/Release/net8.0-windows/liveglass.log` |
| **深色磨砂玻璃的实机观感**（tint 浓度 / 模糊强度 / 圆角轮廓） | **未验证** —— 只有计算依据，没有实机像素依据 | 用户（交互会话） |
| **首次提示的实际弹出** | 代码路径已接（开关 → 确认 → 取消则复位），但**未在真实 UI 上点过** | 用户（交互会话） |
| **后台额外 CPU ≤ 40ms/秒、单次 p95 ≤ 30ms** | **无法测量** —— 本轮只接了主面板，预算需在真实运行中测 | A17 |
| 高饱和壁纸 / HDR 下的玻璃观感与色偏 | 未验证 | A17 |
| 真实录屏（OBS / 游戏栏）下瞬时排除是否闪动 | 未验证 | A17 |
| Win10 / 真实 RDP / 锁屏恢复 | 未验证 | A17 |
| 负坐标屏（DISPLAY2，X = −1440）实抓 | 环境已确认，**实抓未做** | A17 |
| 快速换肤 / 释放压力下的句柄与内存增长 | 未验证 | A17 |

---

## 7. 交接给 A17 的清单

1. **先在交互会话打开一次实时玻璃**，确认：① 提示弹出且可取消；② 打开后背景确实是实时画面；
   ③ 关闭后恢复渐变/纯色；④ 隐藏窗口时不再抓屏；⑤ 面板底部**没有**出现
   「实时玻璃未生效：…」这一行提示（出现了就说明还在降级）。
   跑完把 exe 同目录的 **`liveglass.log`** 回传 —— 这是确定「服务为什么没出帧」的确切依据。
2. **看一次深色玻璃观感**：tint 浓度、模糊强度、圆角轮廓目前**只有计算依据**，需要实机观感来定标。
3. **测后台 CPU 预算**：按 §6.1 口径分别记录 CPU 与经过时间；**CPU 预算只认 CPU 口径**。
4. **录屏下验证瞬时排除**：确认面板是否"短暂消失或闪动"，并把结论写进支持矩阵。
5. **释放压力**：反复开关实时玻璃、换肤、隐藏/显示，确认位图与句柄不持续增长
   （`BackdropCaptureService.Metrics` 的 `FramesDroppedStale` 与存活位图数是现成的观测量）。
6. **Win10 / RDP / 锁屏**按支持矩阵逐项记录"通过 / 失败 / 未验证"，不外推。
7. 未通过的后端**不发布**（保留 `gradient → solid` 降级链与可读原因）。

---

## 8. 本轮改动清单

| 文件 | 性质 | 说明 |
|---|---|---|
| `src/UI/LiveGlassController.cs` | 新增 | 接线控制器 + 帧来源代理 |
| `src/UI/UIController.cs` | 修改 | 创建控制器、`AppearanceRuntime` 接入代理、`Render` 前租约交接、公开状态接口、`Dispose` |
| `src/UI/MainForm_Transparent.cs` | 修改 | 可见性 / 句柄 / 尺寸 / DPI / 退出 五个事件的接线 |
| `src/UI/Settings/MainPanelPage.cs` | 修改 | 开关 + 首次提示 + 实际状态行 |
| `src/Core/Settings.cs` | 修改 | `LiveGlassEnabled` / `LiveGlassHintShown` |
| `resources/lang/*.json`（9 个） | 修改 | `Menu.LiveGlass` / `Menu.LiveGlassHint` / `Menu.LiveGlassStatus` |
| `resources/themes/LiquidGlass.json` | 新增 | 液态玻璃主题 |
| `tests/AppearanceM2Probe/SourceGateRunner.cs` | 修改 | A16 唯一性门槛 |
| `tests/AppearanceThemeCheck/{Program,ThemeSpec}.cs` | 修改 | 期望集合纳入 LiquidGlass + 它的完整期望表 |
| `docs/superpowers/plans/2026-10-20-appearance-liquid-glass-theme.md` | 新增 | 主题参数说明与配色取舍 |

### 8.1 第二轮（实机反馈后）追加改动

| 文件 | 性质 | 说明 |
|---|---|---|
| `src/UI/LiveGlassController.cs` | 修改 | 诊断日志 `LiveGlassLog`（exe 同目录 `liveglass.log`，仅状态变化时写）；`RefreshRequest` 死路径修复；暴露 `LastDegradeReason` / `PublishedFrames`；`StartService` / `BeginPaint` 记日志 |
| `src/UI/UIController.cs` | 修改 | 新增 `DrawLiveGlassNotice`（降级可见性提示行，竖屏 + 横屏两处调用）；提示画刷按颜色重建（换主题不残留旧色）；`TextSecondary` 为空时退回 `TextPrimary` |
| `resources/themes/LiquidGlass.json` | 修改 | 整体改**深色磨砂玻璃**配色；`frameStyle` 1 → 2（圆角描边）、`cornerRadius` 0 → 16、`scanline` 30 → 18、`grid` 20 → 10、`groupRadius` 4 → 10 |
| `tests/AppearanceThemeCheck/ThemeSpec.cs` | 修改 | `ThemeSpecs.LiquidGlass` 的期望表同步到深色配色（15 个颜色 + 布局 4 项 + backdrop 5 项） |
| `docs/superpowers/plans/2026-10-20-appearance-liquid-glass-theme.md` | 修改 | 颜色表 / backdrop 表 / §5 降级说明 / §7 配色取舍与实测对比度 / §8 未验证项 全部同步到深色版 |
| `docs/superpowers/plans/2026-10-20-appearance-a16-acceptance.md` | 修改 | §3 主题取值表、§5.3 三处修复、§6 未验证项、§7 交接清单、§8.1 追加改动清单 |
