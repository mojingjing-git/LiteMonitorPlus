# 设置面板「渲染路线」独立化（R3 · 实机核查后修订版）

> 状态：**待执行**（R2 的改动一/二/四/五/六 已按 R3 修订；R2 的改动三降级为条件项）
> 日期：2026-10-24
> 基线：候选工作树 `codex/review-remediation-20261023` @ `9238bb9`（含未提交的修复批次）
> 前序：R2《设置面板「渲染路线」独立化 + 移除旧实时玻璃开关》（已按自审修订）
> 关联验收报告：`docs/reviews/2026-10-24-independent-acceptance-20261023T193422Z-474953d8.md` §11（P1-01）

---

## 0. 本版相对 R2 的变化（先看这段）

R2 的核心主张是：**「移除旧实时玻璃开关（GPU 实时背景为替代功能）」**。

R3 在实机核查后**推翻了这个前提**：

| 项 | R2 | R3 | 依据 |
| --- | --- | --- | --- |
| 改动三（移除 legacy 实时玻璃开关） | 主改动 | **降级为条件项**，必须等第 0 步实测结论 | 默认配置下 GPU 实时背景**不可达**（§1.3） |
| 新增第 0 步 | — | **打通 GPU 实时背景路线**（含实测判据与三分支决策树） | §1.3 + §1.4 |
| 两个 `_GPU` 主题 | 用户原诉求为「删掉两个假的」 | **保留不删**，改用路线标注区分 | 材质是真的（折射/厚度/高光），只是当前拿不到实时输入（§1.2） |
| P1-01 修复 | — | **已纳入本计划**（已在候选树完成并复跑全量回归） | 验收报告 §11 |
| 事实引用 | 多处行号有误 | 全部按实机核查订正 | §7 核实表 |

**一句话结论**：R2 想做的"设置面板重组"是对的；但它试图**删掉的那条路，恰好是目前唯一真跑通的真玻璃**，而它想用来替代的那条路**在默认配置下被一道已被证伪的硬闸堵死**。所以顺序必须倒过来。

---

## 1. 已确认事实（本机实测，非推断）

### 1.1 "液态玻璃"有两层含义，R2 把它们混成了一层

| | legacy `LiquidGlass` | `LiquidGlass_GPU` / `LiquidGlass_Toolbar_GPU` |
| --- | --- | --- |
| **材质** | 旧 GDI+ 路径，无材质 | **真液态玻璃材质**：折射 1.6 / 厚度 1 / 高光 1 / 模糊半径 8 / 圆角 26（`resources/themes/LiquidGlass_GPU.json`） |
| **透出实时桌面** | ✅ **唯一真跑通** | ❌ **默认配置下拿不到实时输入** |

`_GPU` 两个主题的 `backdrop.mode = gradient`、`blur = 0` 是**有意为之**（GPU 路线自己取背景输入），不是"假"；它们呈现为"固定背景 + 玻璃材质"，看着像玻璃但后面不是活的桌面。

### 1.2 `_GPU` 路线拿不到实时输入的直接原因

`src/Rendering/Configuration/RenderingRoutePolicy.cs`：

```
:85-94   material = cyber_punk          ⇒ GpuFixedInput（设计如此，零抓屏）
:99-109  liveBackground = false         ⇒ GpuFixedInput
:111-120 WDA 政策未接受                 ⇒ GpuFixedInput
:128-137 WindowIsLayered = true         ⇒ GpuFixedInput
         FallbackCode = "wda_capture_policy_unsupported_on_layered_window"
:139-145 以上都不命中                    ⇒ GpuLiveBackground（唯一能透出实时桌面的 GPU 路线）
```

唯一能走通 GPU 实时背景的路径必须同时满足：材质 = `liquid_glass`、GPU 效果开、实时背景开、WDA 政策已接受、**且窗口不是分层窗口**。

### 1.3 主面板在默认配置下**就是**分层窗口（实测）

主面板用 `Opacity` 做透明度动画，而 WinForms 中 `Opacity < 1` 会自动置 `WS_EX_LAYERED`：

| `Opacity` | `GWL_EXSTYLE` | `WS_EX_LAYERED` | GPU 实时背景路线 |
| ---: | --- | :-: | --- |
| **0.85（默认配置）** | `0x00090088` | **True** | ❌ 被 `:128` 硬闸挡死 |
| 1.0 | `0x00010088` | False | ✅ 可达 |

读取点：`src/UI/MainForm_Transparent.cs:96` → `src/UI/Helpers/MainFormWinHelper.cs:168-180`（读 `GWL_EXSTYLE`）。
消费点：`src/UI/UIController.cs:1350-1357`（`WindowIsLayered: MainForm.IsWindowLayered()`）。
宿主侧二次校验：`src/Rendering/Presentation/GpuPresentationHostController.cs:277-284` + `src/Rendering/Presentation/WindowExtendedStyle.cs:35-39`。

### 1.4 那道硬闸自己的前提**已被 legacy 路线证伪**

`MainFormWinHelper.cs:159` 与 `RenderingRoutePolicy.cs:122` 的注释都写着「`WS_EX_LAYERED` 与 `WDA_EXCLUDEFROMCAPTURE` 互斥 ⇒ 分层窗口上不得申请 WDA 排除」。

**实测反例**（同一块分层窗口，`Opacity=0.85`，`WS_EX_LAYERED=True`）：

```text
[2026-10-24 04:51:03.370] 状态=Running 生效模式=Glass 后端就绪=True 已发布帧=8  连续失败=0
[2026-10-24 04:51:08.381] 状态=Running 生效模式=Glass 后端就绪=True 已发布帧=13 连续失败=0
```

legacy 路线在同一块分层窗口上**照样**成功申请 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`（窗口亲和性轮询实测出现过 `0x11`）并抓到帧。

⇒ 该互斥前提**在本机不成立**。真实约束可能来自 DirectComposition 与分层窗口的合成冲突（代码注释未写这层），需要第 0 步实测区分。**在区分清楚之前，不能基于这条闸做减法。**

### 1.5 P1-01：真实程序里真液态玻璃 100% 失效（静默）

- **现象**：显式打开「实时玻璃」后，真实程序里玻璃仍从不生效。
- **根因三段**：
  1. `src/UI/MainForm_Transparent.cs:521` `OnHandleDestroyed` → `_ui.StopLiveGlassForHandleRecreate()` → `LiveGlassController.StopService()` → **`_service = null`**；
  2. `src/UI/LiveGlassController.cs:463-466` `RefreshRequest()` 首行 `if (svc is null) return;` —— 服务已被整停时直接返回，**不重启**；
  3. 唯一能重启的 `RestoreIfEnabled()` 只在 `LiveGlassController.HandleRecreateFailed()` 与 `src/UI/UIController.cs:1736-1739`（**GPU 受控重建收尾**）被调用 —— **纯 legacy 路径无任何调用点**，尽管 `MainForm_Transparent.cs:521` 的注释承诺"句柄重建后由 UIController 按配置恢复"。
- **后果**：`BeginPaint()`（`LiveGlassController.cs:535-557`）首行永远命中 `if (svc is null) return;` → 从不取帧 → `BackdropModeResolver` 永远走"降级 1：glass → gradient" → **背景恒为静态渐变（假玻璃），且全程零报错**。
- **单测为何全绿**：测试直接构造 `MainForm` 并驱动绘制，窗口句柄从不被替换 ⇒ 服务不被整停 ⇒ 同一条链路每次都写出 `状态=Running 生效模式=Glass 已发布帧=1`。
- **定级 P1**；**已修复**：见 §2。

---

## 2. 已完成并验证的工作（不再重复做）

### 2.1 P1-01 修复

**改动**：`src/UI/LiveGlassController.cs` 的 `RefreshRequest()`，1 个文件 / +15 −1 行。

```csharp
BackdropCaptureService? svc = _service;
if (svc is null)
{
    // ★【缺陷修复】服务已经被**整停**时（句柄重建 ⇒ OnHandleDestroyed
    //   ⇒ StopForHandleRecreate ⇒ StopService ⇒ _service = null），这里**不能**直接返回。
    //   ... 旧行为 ⇒ 永不取帧 ⇒ 永不写「生效模式=」⇒ 恒为渐变假玻璃，且零报错。
    RestoreIfEnabled();
    return;
}
```

**落点理由**：`RefreshRequest()` 是"让捕获请求与窗口现状对齐"的**统一入口**（句柄 / 尺寸 / 位置 / 主题四类变更都走它），一处修复覆盖全部路径；`RestoreIfEnabled()` 内部已有"用户显式关过就不拉起""服务还在就不重复启动"两道闸，不会产生重复抓屏循环。

**文件 SHA-256**：`797C85B6…FDE1FE0` → `092D5102…16F05A8`

**修复前后实测对照**：

| 观测项 | 修复前 | 修复后 |
| --- | --- | --- |
| `StartService` 行数 | 1 | **2**（第二次即句柄重建后的恢复） |
| `状态=Running 生效模式=Glass … 已发布帧=N` | **无** | **有**，N 持续增长 |
| 窗口显示亲和性（40 ms 轮询 12 s，覆盖 2 fps 抓屏节奏） | 恒 `0x0`（**从未抓过一帧**） | **出现 `0x11`** |

**回归**：Debug / Release 双配置各 **1831 条**（通过 1830 / 失败 0 / 跳过 1），两个门禁均通过；失败用例 **0**；测试程序集哈希与修复前完全一致（未动任何测试）。证据：`E:\litemonitor-acceptance\acceptance-20261023T193422Z-474953d8\post-fix\`。

### 2.2 验收报告 §11

已补入验收报告并同步 G 盘副本（字节一致，sha256 `CA923D36…813CC119`）。

---

## 3. 第 0 步（前置，必须先做）：打通 GPU 实时背景路线

> **这是 R3 新增的前置步骤。没有它的实测结论，改动三不许执行，改动二里"实际生效路线"的文案也写不对。**

### 3.1 目标

让 `RenderingRoute.GpuLiveBackground` 在**默认配置（`Opacity=0.85`、主窗口分层）**下可达，并证明画面合成正常。

### 3.2 做法（临时改动，仅用于实验）

1. `src/Rendering/Configuration/RenderingRoutePolicy.cs:128-137`：把 layered 判定**降级为"仅诊断"** —— 不再改变 `Route`，但把 `WindowIsLayered` 记入决策（原因码/诊断字段）。
2. `src/Rendering/Presentation/GpuPresentationHostController.cs:283-284`：同步放开宿主侧二次校验（否则宿主仍会拒绝申请 WDA 排除）。
3. 编译 Debug + Release，启动实机，配置：
   `Skin=LiquidGlass_GPU`、`GpuThemeEffectsEnabled=true`、`GpuLiveBackgroundEnabled=true`、`GpuWdaCapturePolicyAccepted=true`、`Opacity=0.85`。
4. 用 `tools/GpuUiProbe`（`--live`）跑一次拿结构化诊断：输出目录里的 `*.diagnostics.json` 含 `captureSessionsCreated` 与 `renderer.LiveCaptureRequested`。

### 3.3 判据（全部满足才算通过）

- [ ] GPU 实时捕获会话创建数 ≥ 1（`captureSessionsCreated ≥ 1`）
- [ ] `renderer.LiveCaptureRequested == true`
- [ ] 面板画面**随桌面变化**（人工目视 + 两次截图内容不同）
- [ ] 无 DComp 合成异常（无撕裂 / 黑块 / 闪烁 / 整块透明）
- [ ] 窗口亲和性轮询能观测到 `0x11`
- [ ] `liveglass.log` 无新增 `Faulted` / 降级原因行

### 3.4 分支决策

| 实测结果 | 处置 |
| --- | --- |
| ✅ 全部通过 | **方案 A**：保留放开闸的改动；`WindowIsLayered` 保留为**诊断字段**（仍写入状态行与原因码，便于现场排障，但不再阻断）；补单测：分层窗口 + WDA 已接受 + live 开 ⇒ 期望路由 = `GpuLiveBackground` |
| ⚠️ 捕获会话能建但**画面不动**（DComp 无法把分层窗口的实时帧合成进来） | **方案 C**：GPU 路线下主面板强制 `Opacity = 1.0`（放弃透明度动画）后重测；若合成正常则保留 C，并在路线总览卡上提示"GPU 路线不支持透明度" |
| ❌ 出现**合成异常**（撕裂/黑块/闪烁） | **方案 B**：GPU 呈现改为**复用 legacy 的 `BackdropCaptureService`** 取帧（架构改动，工作量最大，但语义最干净 —— legacy 那条路已证明在分层窗口上可行）。方案 B 需单独出计划，**本计划不含** |

### 3.5 第 0 步的产出

- 一份 `docs/reviews/evidence/rendering-route/gpu-live-<日期>/` 目录：诊断 JSON、截图、亲和性观测记录、决策结论
- 一条单测（若走方案 A/C）
- 一句结论：**"GPU 实时背景在默认配置下 {可达 / 不可达}"**，作为改动三的前置判据写进本计划 §5

---

## 4. 改动一～六（修订版）

> R2 的改动一/二/四/五/六 **全部保留**，下面只写**相对 R2 的修订点**与**必须新增的约束**。

### 4.1 改动一：新增「渲染路线」页 `src/UI/Settings/RenderingRoutePage.cs`

沿用 R2 描述：路线总览卡（只读）+ GPU 路线卡（从 `MainPanelPage.CreateGpuMaterialCard`（`:168`）迁入）+ `RenderingRouteStatusBuilder` 纯函数。

**R3 修订 / 必增约束**：

| # | 修订点 | 理由 |
| --- | --- | --- |
| 1.1 | **禁止**写 `ThemeManager.Current.Rendering.Backend` | `Theme.Rendering` 是 `ThemeRenderingConfig?`（`src/Core/ThemeManager.cs:363`），legacy 主题 `Rendering = null` → **直接 NRE**。legacy 主题是常态不是边角，必须显式判空并归类为"GDI+" |
| 1.2 | 状态文案**必须可参数化本地化** | 状态行有 4 类以上（GPU 运行中 / 已回退+原因 / 未启动 / 实时背景激活），不能只有 3 个 nav/卡片 key |
| 1.3 | 卡片里必须显示**当前路线为何被降级** | `RenderingRoutePolicy` 有 5 个降级原因码（`rendering_configuration_absent` / `invalid` / `safe_mode` / `gpu_effects_opt_in_required` / `wda_capture_policy_not_accepted` / `wda_capture_policy_unsupported_on_layered_window`），用户看到的应当是**原因**，不是"已回退"三个字 |
| 1.4 | 新增页属**构造期代码** | 参照 A06 事故（主窗体构造即抛 NRE 穿过 8 次绿 CI），新页必须进实机构造探针，见 §6 |
| 1.5 | 「渲染路线」页必须能显示**第 0 步的结论** | 若走了方案 C（GPU 路线强制 Opacity=1.0），用户需要在该页看到这条限制 |

### 4.2 改动二：主题路线标注（设置页 + 托盘菜单）

沿用 R2：`ThemeManager` 新增路线判定 helper；设置页 `MainPanelPage.CreateAppearanceCard`（`:86`）的主题下拉改用 `AddComboPair`；托盘菜单 `src/UI/MenuManager.cs:391` 同步后缀。

**R3 修订 / 必增约束**：

| # | 修订点 | 理由 |
| --- | --- | --- |
| 2.1 | helper **首选 `JsonDocument` 轻读** `rendering.backend`，**不要**逐主题调 `ThemeManager.Load(name)` | `Load`（`src/Core/ThemeManager.cs:487`）有全局副作用：设 `ThemeManager.Current`、构建字体、缺文件抛 `FileNotFoundException`、可能写盘。设置页**打开下拉就逐个 Load = 把当前主题换掉**。R2 把这条写成了"退路"，R3 倒过来：**轻读是首选，Load 只作兜底** |
| 2.2 | helper 必须**自带容错** | 坏 JSON / 缺文件 / `rendering` 缺失 ⇒ 一律归类为 legacy，**不得抛到 UI 层** |
| 2.3 | `AddCombo` → `AddComboPair` 时**第二个参数语义不同** | `AddCombo`（`src/UI/Settings/SettingsUIBuilder.cs:118`）第二参是 **titleKey**（内部会 `T()`）；`AddComboPair`（`:192`）第二参是**已本地化文本**（参见 `SystemHardwarPage.cs:481` 传 `LanguageManager.T(...)` 的结果）。直接替换会**丢掉本地化** |
| 2.4 | `AddComboPair` 的 options 是 `IEnumerable<dynamic>` | 每项需具备 `Label` / `Value` 属性（照 `SystemHardwarPage.cs:482` 的 `new { Label = …, Value = … }` 形状） |
| 2.5 | 后缀文案应区分**三种**情况 | 至少：`GPU 材质` / `GDI+` / **`GPU 材质（当前无实时背景）`** —— 后者对应 §1.3 的默认状态，否则标注会误导用户以为 `_GPU` 主题也能透出桌面 |
| 2.6 | 托盘菜单后缀必须与设置页**共用同一 helper** | 避免两处文案漂移 |

### 4.3 改动三：移除旧实时玻璃开关 → **条件项**

**执行前置条件（全部满足才做）**：

- [ ] 第 0 步完成，且结论为「GPU 实时背景在默认配置下**可达**」
- [ ] 第 0 步的实机判据（§3.3）全部勾选
- [ ] 路线总览卡已能显示 GPU 实时背景状态与降级原因
- [ ] `LiveGlassEnabled` 的迁移落点已改到**每次加载都会执行**的路径

**R3 修订 / 必增约束**：

| # | 修订点 | 理由 |
| --- | --- | --- |
| 3.1 | **迁移落点必须换** | `SettingsHelper.RebuildAndMigrateSettings` 定义在 `src/Core/SettingsHelper.cs:681`（**不是 R2 写的 :551**），唯一调用点在 `:156`，条件是 `s.MonitorItems.All(x => x.TaskbarSortIndex == 0)` —— **只对"遗留配置"执行**。放这里 ⇒ **普通用户根本不会被迁移**（UI 开关已删、配置里仍是 `true` ⇒ 实时玻璃继续跑且无法关闭）。改为：每次加载都走的规范化路径，或加显式 schema 版本号 |
| 3.2 | 迁移后**必须验证"无法再开启"** | 迁移写 `false` 后，用户没有任何 UI 入口能把它改回 `true`（字段保留、schema 兼容） |
| 3.3 | **不得**删除 `UIController` 中的 `get_LiveGlassEnabled` 调用点 | `tests/LiteMonitor.Tests/LiveGlassRebuildWiringTests.cs:92` 用 IL **调用顺序**断言它们存在且位于 `RestoreIfEnabled` 之前 |
| 3.4 | `LiveGlassController` / `UIController` 的机器**全部保留** | 结论同 R2，但**理由要改**：句柄代次的真相源是 `WindowHandleGeneration.Advance()`（`src/Rendering/Configuration/WindowHandleGeneration.cs:37`，唯一驱动点 `src/UI/MainForm_Transparent.cs:354`）；`LiveGlassController` 只是**消费方**（其自身注释 `LiveGlassController.cs:411-415` 写明"只读消费句柄代次唯一真相源"）。R2 的表述"它是句柄代次真相源"**不属实**，别让错误理由进下一版 |
| 3.5 | 迁移后 P1-01 修复是否仍有效，必须**明写** | 若 `LiveGlassEnabled` 恒为 `false`，则 `RestoreIfEnabled()` 恒早退 ⇒ §2.1 的修复对全体用户变成死代码。必须在路线总览卡上把这件事说清楚 |
| 3.6 | 移除后必须**保留 legacy 兜底的可达性** | `BackdropModeResolver` 的 glass→gradient 降级链仍在；若 GPU 路线在用户机器上不可用，用户至少还有 legacy 主题可选 |

**若第 0 步结论为"不可达"** → **改动三整条不执行**，改为：保留 legacy 开关但**降级到「渲染路线」页内**（不删，只搬家），并在页面顶部用一句话说明"GPU 实时背景在当前窗口状态下不可用，legacy 实时玻璃仍是真玻璃"。

### 4.4 改动四：i18n

沿用 R2（9 个语言文件：`resources/lang/{de,en,es,fr,ja,ko,ru,zh-tw,zh}.json`）。

**R3 修订**：

- key 数不止 3 个：除 `Menu.RenderingRoute` / `Menu.RouteOverview` / `Menu.GpuRoute` 外，**状态行文案必须各有一条可参数化 key**（如 `Menu.RouteStatus.GpuRunning` / `.Fallback` / `.NotStarted` / `.LiveBackgroundOn` / `.LiveBackgroundUnavailable`）。
- 9 个语言文件全部要补（`LanguageManager.T` 缺 key 时的回退行为必须在实现前确认，不要假设）。
- 路线后缀（如 `（GPU 材质）`）也要本地化。

### 4.5 改动五：测试

沿用 R2 的测试清单（`RenderingRouteStatusBuilderTests`、helper 测试、迁移测试）。

**R3 修订 / 必增约束**：

| # | 修订点 | 理由 |
| --- | --- | --- |
| 5.1 | **"修复受影响断言"要划红线** | `LiveGlassRebuildWiringTests` 用 `CallOffsetOf` 做 IL **调用顺序**断言（`:76` / `:92`）。改 `UIController` / `MainPanelPage` 会让偏移漂移。**正确做法是把这类断言改成按成员引用**（调用了哪个方法、顺序如何），**不是**改偏移数字迁就实现。偏移数字一改，护栏就退化成"实现快照" |
| 5.2 | 门禁阈值**只许上不许下** | 新增用例后上调阈值是合理的（R2 方向对），但要在 `ci.yml` / `release.yml` 的历史注释里写清"本轮新增 N 条"，与门禁既有约定一致（`assert-test-counts.ps1` 注释明确禁止为了变绿而调低） |
| 5.3 | 新增一条**路由降级原因码**的单测 | 5 个降级原因码必须各有断言，避免原因码被改掉而无人察觉 |
| 5.4 | `BatchB1WebSessionCredentialTests.cs:208` 直接 `new MainPanelPage(...)` | 拆卡后仍会实例化 `MainPanelPage`，必须确认它不再依赖被移走的 live-glass 控件；**这条要在实现前先跑一遍基线**，不要等到实现后才发现 |
| 5.5 | helper 的坏 JSON / 缺文件容错要有独立用例 | R2 只写了"坏 JSON/缺文件"，要明确断成品类与期望归类（legacy） |

### 4.6 改动六：文档

沿用 R2（`README.md` 设置路径 + 支持矩阵"用户开关"行）。

**R3 修订**：矩阵必须**同时列出三条路线**（legacy GDI+ / GPU 固定背景 / GPU 实时背景），并标注各自的**前置条件**（WDA 政策、窗口是否分层），否则文档会继续误导。

---

## 5. 执行顺序（**必须按序**）

| 步 | 内容 | 阻塞关系 |
| ---: | --- | --- |
| **0** | 打通 GPU 实时背景路线（§3）+ 拿实测结论 | 阻塞改动三 |
| **1** | 改动一：新增「渲染路线」页 | 依赖步 0 的**文案**（是否显示"实时背景不可用"） |
| **2** | 改动二：主题路线标注（设置页 + 托盘） | 与步 1 可并行（不同文件） |
| **3** | 改动四：i18n | 依赖步 1/2 的最终 key 清单 |
| **4** | 改动五：测试 | 依赖步 1/2/3 |
| **5** | 改动六：文档 | 依赖步 4 的最终行为 |
| **6** | **改动三：移除 legacy 实时玻璃开关（条件项）** | **必须在步 0 结论为"可达"后才做**；且步 3.1/3.5 的修订必须已落地 |

> **红线**：步 6 未满足前置条件时**不许执行**。R2 的顺序（先删后补）会直接删掉当前唯一真跑通的真玻璃。

---

## 6. 验收（机检优先，R2 的"手动"清单不够）

### 6.1 构建与回归

- [ ] Debug / Release 双配置**真还原 + 真重建**（`-t:Rebuild`，不得对遗留 DLL 跑 `--no-build`）
- [ ] 全量测试通过，**失败 0**，跳过数不变（当前基线：1831 条 / 通过 1830 / 跳过 1，唯一跳过身份 = `LiteMonitor.Tests.MarketE2ETests.端到端_拉索引_安装_校验_卸载`）
- [ ] 计数门禁（`.github/workflows/assert-test-counts.ps1`）双配置均通过，阈值 = 新增后的实际总数
- [ ] `MarketIndexContractTests` 发现 6 / 通过 6 / 跳过 0

### 6.2 实机判据（沿用本次已跑通的判据）

- [ ] **真实 MainForm 构造探针**（A06 式）通过 —— 新增设置页属构造期代码，必须真构造一次
- [ ] `liveglass.log` 出现 `状态=Running 生效模式=Glass … 已发布帧=N` 且 **N 持续增长**
- [ ] 窗口显示亲和性轮询（40 ms / ≥10 s）能观测到 `0x11`
- [ ] 步 0 的 GPU 实时背景判据（§3.3）全部勾选
- [ ] 无新增 `Faulted` / 降级原因行

### 6.3 行为判据

- [ ] 设置页与托盘菜单两处主题标注**完全一致**
- [ ] WDA 政策弹窗行为不变
- [ ] （若执行改动三）旧配置 `LiveGlassEnabled=true` 启动后**实时玻璃不启动**，保存后 JSON 为 `false`，且**无任何 UI 入口能再把它改回 true**
- [ ] （若执行改动三）路线总览卡明确显示"legacy 实时玻璃已成为兜底 / 不再可选"之类的真实状态

### 6.4 产物与证据

- [ ] 产物 SHA-256 记入 `docs/reviews/evidence/rendering-route/`
- [ ] 步 0 的诊断 JSON / 截图 / 亲和性观测记录留证
- [ ] 验收报告追加对应章节，并同步 G 盘副本

---

## 7. R2 事实引用核实表（订正）

> R2 里的行号与表述有若干处不准，本表是本计划引用的**权威口径**。

| R2 的说法 | 核实结果 | 正确口径 |
| --- | --- | --- |
| `SettingsHelper.RebuildAndMigrateSettings`（:551） | ❌ 行号错 | 定义在 `src/Core/SettingsHelper.cs:681`；调用点 `:156`，**条件执行**（仅遗留配置） |
| `ThemeManager.Current.Rendering.Backend`（:434/:363 现成入口） | ⚠️ 会 NRE | `Theme.Rendering` 是 `ThemeRenderingConfig?`（`src/Core/ThemeManager.cs:363`），必须判空 |
| `AddComboPair`（主题下拉改用） | ✅ 存在 | `src/UI/Settings/SettingsUIBuilder.cs:192`；但**第二参是已本地化文本**，`AddCombo`（`:118`）才是 titleKey |
| `MainPanelPage.CreateGpuMaterialCard`（:168-255） | ✅ 属实 | `:168` 起，提示文本在 `:236-237` |
| 实时玻璃 toggle / 状态 label / hint / status（:122-166、:307 附近） | ✅ 基本属实 | toggle `:124-145`、状态 label `:149-159`、`ShowLiveGlassHint` `:290`、`UpdateLiveGlassStatus` `:304-307` |
| `MainPanelPage.CreateAppearanceCard`（:91-93 主题下拉） | ✅ 属实 | 方法在 `:86`，`AddCombo` 在 `:91-93` |
| `SettingsForm.cs:206` 之后插入 nav | ⚠️ 未逐字确认 | 实现时以 `AddNav` 实际调用点为准 |
| `UIController.cs:160/289/300/864/1732` 的 `get_LiveGlassEnabled` 调用点 | ⚠️ 未逐个确认 | 实现时以实际调用点为准；**但 `LiveGlassRebuildWiringTests.cs:92` 断言的 `CompleteLegacyTransition` 必须保留** |
| 「`LiveGlassController` 是句柄代次真相源（合同 A1）」 | ❌ 不属实 | 真相源 = `WindowHandleGeneration.Advance()`（`src/Rendering/Configuration/WindowHandleGeneration.cs:37`），唯一驱动点 `src/UI/MainForm_Transparent.cs:354`；`LiveGlassController` 是消费方 |
| 9 个语言文件 | ✅ 属实 | `resources/lang/{de,en,es,fr,ja,ko,ru,zh-tw,zh}.json` |
| 门禁阈值"更新到新增后实际用例数" | ✅ 方向对 | 只能**上调**，不得下调；须在注释里写清新增条数 |
| 「GPU 实时背景为 legacy 实时玻璃的替代功能」 | ❌ **实测推翻** | 默认 `Opacity=0.85` ⇒ 主窗口分层 ⇒ `RenderingRoutePolicy.cs:128` 硬闸 ⇒ `GpuFixedInput`；且该硬闸的"分层与 WDA 互斥"前提被 legacy 路线实测证伪（§1.4） |

---

## 8. 风险与红线

| 风险 | 后果 | 缓解 |
| --- | --- | --- |
| 按 R2 原序执行改动三 | **删掉唯一真跑通的真玻璃**，用户只剩固定背景；P1-01 修复变死代码 | 步 0 前置 + §5 红线 |
| 迁移落点错误（只在遗留配置执行） | 普通用户开关被删但值仍为 true ⇒ 实时玻璃继续跑且**无法关闭** | §4.3 第 3.1 条 |
| 为迁就实现改 IL 偏移断言 | 护栏退化成实现快照，下次真回归抓不住 | §4.5 第 5.1 条 |
| 用 `Load(name)` 标注主题 | 打开下拉就换主题 | §4.2 第 2.1 条 |
| 门禁阈值下调 | 假绿 | §4.5 第 5.2 条 |
| 步 0 走方案 B（架构改动） | 工作量超出本计划 | 方案 B 单独出计划，本计划不含 |
| 候选树与 G 盘 master 不同步 | 修复只存在于候选树、未提交；master 仍是旧代码 | 执行前确认同步策略（本计划默认在候选树执行，提交前再同步 master） |

---

## 9. 附：本计划依赖的实测证据位置

```text
E:\litemonitor-acceptance\acceptance-20261023T193422Z-474953d8\
  p1-01-liveglass\p1-01-liveglass.json   P1-01 根因、修复前后观测值与日志切片
  p1-01-liveglass\liveglass.log            实机日志原件（修复前后同文件）
  post-fix\                                修复后双配置全量回归完整证据
  screenshots\panel-*.png / capregion-*.png / fullscreen-*.png
docs/reviews/2026-10-24-independent-acceptance-20261023T193422Z-474953d8.md §11
```

---

## 10. 执行记录

### 10.1 步 0 结论（2026-10-24）：GPU 实时背景在默认配置下**可达**，走**方案 A**

R3 §3.3 六项判据全部通过（证据：`docs/reviews/evidence/rendering-route/gpu-live-2026-10-24/step0-summary.md`）：

- `captureSessionsCreated = 1`；`LiveCaptureRequested = true`，捕获帧龄 P50 ≈ 2.2ms。
- 两次 GPU 帧缓冲抓帧采样像素差异 63.4%，第二帧可见身后窗口内容折射；无合成异常。
- 真实程序（隔离 `bin/step0` 运行）亲和性轮询全程 `0x11`；面板区域屏截透出身后桌面（WDA 生效的视觉证明）。
- 无 `Faulted`/降级原因行。

处置：保留放开闸改动（policy 分层判定诊断化 + 宿主侧二次校验放开）；`diagnostic_window_is_layered` 记入决策 `Errors`；守卫用例按方案 A 语义改写（`LayeredWindowStillRequestsLiveBackgroundWithDiagnostic` + `NonLayeredWindowDoesNotCarryLayeredDiagnostic`）。**步 6 前置条件满足。**

评审批准的修订同步并入：§4.1-1.3 原因码数量 5→6；§7 补两树行号差异说明（执行按当前工作树实际行号定位）；§3.4 方案 C 强制 `Opacity=1.0` 的范围收窄为"仅 `GpuLiveBackground` 路由激活时"。

同步策略（已确认）：候选树执行全部步骤，步 0~6 完成并取证后按批次整理提交合入 master。

### 10.2 执行记录（步 1~6 全部完成，2026-10-24）

| 步 | 状态 | 交付 |
| --- | --- | --- |
| 0 | ✅ 方案 A | 见 §10.1；实验改动**保留为正式改动**（policy 诊断化 + 宿主放开），守卫用例按方案 A 改写 |
| 1 | ✅ | `src/UI/Settings/RenderingRoutePage.cs`（路线总览卡 + GPU 路线卡迁入，OnShow 惰性创建对齐 MainPanelPage 模式）、`RenderingRouteStatusBuilder.cs`（纯函数聚合）、`SettingsForm` nav + Apply 重试申请改读新页；MainPanelPage 的 GPU 卡/`GpuStartupRetryRequested` 移除 |
| 2 | ✅ | `ThemeManager.IsGpuTheme`（JsonDocument 轻读 + 全路径容错归 legacy）；设置页主题下拉改 `AddComboPair` 带 `(GPU 材质)/(GDI+)` 后缀（Value 不变）；托盘主题项同后缀（同 helper 同 key） |
| 3 | ✅ | 9 个语言文件各 +10 key（`Menu.RenderingRoute` / `Menu.RouteSuffixGpu|Gdi` / `Menu.RouteStatus.*` 7 条），文本级插入，逐文件 JSON 解析验证通过（`scripts/add-rendering-route-i18n.py`） |
| 4 | ✅ | 新增 31 条用例：StatusBuilder 10 + 原因码 8（含"分层码不得复活"）+ IsGpuTheme 9 + LiveGlass 迁移 3；守卫改写 1 + 新增 1；门禁阈值 1831→**1862**（ci.yml + release.yml 逐字一致）；`AppearanceSettingsProbe` 增加新页真实构造 + OnShow 用例 |
| 5 | ✅ | README：三条渲染路线矩阵 + 设置路径改为「渲染路线」页 + 旧开关迁移说明 |
| 6 | ✅ | 旧实时玻璃开关移除；迁移落点 = `SettingsHelper.Load` **每次加载路径**（强制 `LiveGlassEnabled=false`，字段保留）；`LiveGlassController`/`UIController` 机器全保留（`get_LiveGlassEnabled` 调用点未动，IL 护栏原样通过） |

**门禁实测（2026-10-24，本机）**：Debug 与 Release 双配置——LiteMonitor.Tests 1770（通过 1769 / 失败 0 / 跳过 1=指定 E2E）+ Updater.Tests 73 + GpuNonperformance.Tests 19 = **1862 / 1861 / 1**，阈值 1862 双工作流同步。`AppearanceSettingsProbe` 退出码 0（含新页构造用例）。成品冒烟：`bin/rel-gate/Release` 启动 8s 存活、零错误日志。

**已知事项（2026-10-24 评审后订正——原归因有误，见下）**：本批验证期间观察到的
`ReviewMarketPersistenceRegressionTests` 间歇失败**不是**"与本批无关的环境抖动"。独立审查（同机三次复现三条不同用例）
坐实根因：`SettingsHelper` 的**全程序集唯一一份** settings.json/.tmp + `Settings._instance` 活单例，与 env 激活的
`MarketIndexContractTests`（不在 cfgfile 集合）串台 ⇒ "单跑全绿、全量间歇挂"。此外门禁对同一程序集多份 TRX 只告警不失败，
且持久化护栏类未钉入 MUST_EXECUTE。**C-1 修复已按评审处方落地**：① 路径三件套可注入（`SettingsHelper.PathOverride`）；
② tmp 带进程号 + Load 时清扫崩溃残留；③ `PersistenceScope` 重定向到每用例沙箱（不再共享真实三件套）；
④ `ReviewMarketPersistenceRegressionTests` 钉进 MUST_EXECUTE_TEST_CLASS（ci/release 双工作流）；
⑤ 门禁遇同程序集多 TRX 直接 FAIL（原为取最后一次+警告）；⑥ 环回测试服务器对端口 TOCTOU 重试。
验收口径：**双配置连续 3 次全量零失败**（结果见 §10.3）。

证据目录：`docs/reviews/evidence/rendering-route/gpu-live-2026-10-24/`（步 0 全套 + `r3-execution-summary.md`）。

### 10.3 C-1 修复记录（评审阻断 1，2026-10-24）

独立评审坐实"1862/失败 0 不可复现"（同机三复现三条不同用例、单跑全绿）后，按评审处方落地五项修复：

1. `SettingsHelper`：路径三件套可注入（`internal static string? PathOverride`，null = 生产路径逐字节不变）；tmp 带进程号（`settings.json.{pid}.tmp`）+ Load 时清扫崩溃残留 tmp。
2. `PersistenceScope` 重定向到每用例沙箱目录（`PathOverride = _root`），不再与 env 激活的 `MarketIndexContractTests` 共享真实三件套；Dispose 恢复 override（原四接缝恢复不变，现为五接缝）。
3. `TrySaveLocked` 对 `IOException/UnauthorizedAccessException` 做**两次小步重试**（25/50ms）——AtomicWrite 整体幂等；这是"单跑全绿、全量间歇挂"的另一半根因（杀软/索引器对刚落盘文件的短时持有被严格写入器如实放大）。**注意：这是生产行为的小幅健壮化变更，已如实登记。**
4. `ReviewMarketPersistenceRegressionTests` 钉进 MUST_EXECUTE_TEST_CLASS（ci/release 双工作流，每类 ≥6）。
5. 门禁遇同程序集多份 TRX **直接 FAIL**（原为"取最后一次 + 警告"）；环回测试服务器对端口 TOCTOU 重试 5 次（HttpListener 拒绝访问类抖动）。

**验收结果**：Debug **3 连续全绿**（1770/1769/1，`/tmp/lm-c1-x1..x3`）+ Release **首次 3 连续全绿**（`/tmp/lm-c1-rl1..rl3`）；Updater.Tests 与 GpuNonperformance.Tests 双配置 73/0、19/0 复跑通过。交付文件 SHA-256 重新冻结（71 项，`evidence/rendering-route/gpu-live-2026-10-24/delivered-source-sha256-c1.json`；`SettingsHelper.cs` = `e39ea806…`）。

### 10.4 C-2 决议（评审阻断 2 之产品项，2026-10-24 用户拍板）

**旧实时玻璃开关：完全移除**（用户原话"完全移除算了"）。即维持 §10.2 改动三的现状：
UI 开关已删、`LiveGlassEnabled` 每次加载强制 `false`（字段保留 schema 兼容）、**不追加**首次启动告知；
`LiveGlassController` / `UIController` 的玻璃机器按 §4.3-3.4 继续保留（GDI+ 兜底路径 + 句柄代次消费方，
`LiveGlassRebuildWiringTests` 护栏不变）。评审"静默不可逆降级"的披露缺口由本条决议记录补齐：
这是经用户知情拍板的产品决策，依据 = 步 0 实测 GPU 实时背景在默认配置可达（§10.1）。

**合入 master 的剩余前置（不因 C-1/C-2 通过而自动放行）**：评审清单中的 Critical
（`PluginMarket.cs` 回滚失败被 `catch { }` 吞、`return` 静默跳过）与 F01–F05 段 30 项 Important
（含 F02 失败码被 `out _` 丢弃、LM-009 沙箱原因码失去观测面、`diagnostic_window_is_layered` 只写不读、
两处 XML doc 仍宣称被证伪的"分层⇄WDA 互斥"、`WindowExtendedStyle` 死代码、新页 7 语言回退英文等）——
待下一批次按评审报告逐项处置。
