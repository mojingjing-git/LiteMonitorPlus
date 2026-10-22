# 评审：GPU 与旧管线运行时切换修复计划

日期：2026-10-22。评审对象：`docs/superpowers/plans/2026-10-22-gpu-legacy-runtime-transition-plan.md`（28 765 B / 304 行，DRAFT / 待独立复审）。
评审方式：**逐条行号核验 + 微软原始文档比对 + 与仓库现有资产交叉核对**。所有结论附可复现命令与实际输出。

---

## 0. 结论

**NEEDS-REVISION —— 架构判断成立、事实层几乎全对，但存在 4 项中等缺口与 3 项低等缺口，需修订后方可进入 T00。**

- 文档 §2.1 列出的 **9 条代码事实断言，行号逐条命中**（见 §2），无一处虚构。
- 文档 §2.1 的**微软 DXGI 依据经原文比对逐字成立**（见 §3），这是整份计划最吃重的前提，且它同时**正确否决了 `GDI_COMPATIBLE` 捷径**（§3 末尾）。
- 文档声明的 HEAD `0f1177e0d005c2f5b6b68a81dfc29de9722ac263` 与仓库当前 HEAD **逐字一致**，说明计划确实基于同一基线撰写。
- 但计划在多处**只描述"应当如何"，未对齐仓库中已经存在的同类资产**，一旦 T02/T03/T05 按字面执行，会与既有实现产生第二套口径或直接撞上既有硬校验（见 §4 的 A1–A4）。

**没有发现需要推翻技术路线的错误。** T01 作为决策闸门、§9 备用路线、以及"证据不足即报告而非假装通过"的纪律都写得比多数计划严谨。

---

## 1. 复现命令总表

```powershell
cd 'G:\TOOL\LiteMonitor-fork'
git rev-parse HEAD
Select-String -Path 'src\Rendering\Presentation\GpuSceneRenderer.cs' -Pattern 'FlipSequential|CreateSwapChainForHwnd|\.Present\('
Select-String -Path 'src\UI\UIController.cs'   -Pattern 'Invalidate\(\)|ApplyGpuRenderingRoute|StopGpuPresentation|WindowCapturePolicyRestored|NeedsRestart'
Select-String -Path 'src\UI\MainForm_Transparent.cs' -Pattern 'HandleCreated|NotifyGpuHandleChanged'
Select-String -Path 'src\Rendering\Configuration\ThemeRenderingConfig.cs' -Pattern 'fallback|Fallback'
Select-String -Path 'src\UI\LiveGlassController.cs' -Pattern '_handleGeneration'
Get-ChildItem 'resources\themes' -Filter *.json -Recurse | Select-Object -ExpandProperty Name
```

---

## 2. §2.1 代码事实断言逐条核验 —— 9/9 命中

| 文档断言 | 实测 | 结论 |
|---|---|---|
| `GpuSceneRenderer.cs:164` `FlipSequential` | `:164` `SwapEffect = SwapEffect.FlipSequential,` | ✅ |
| `GpuSceneRenderer.cs:178` 交换链绑定主窗口 HWND | `:178` `_swapChain = _factory.CreateSwapChainForHwnd(`（`windowHandle` 为入参） | ✅ |
| `GpuSceneRenderer.cs:644` 调用 `Present` | `:644` `_swapChain.Present(1, PresentFlags.None).CheckError();` | ✅ 且**确为 `Present` 而非 `Present1`** |
| `UIController.cs:360` 回退执行宿主 Dispose | `:360` `public void StopGpuPresentation()` → `:362` `_gpuPresentation?.Dispose();` | ✅ |
| `UIController.cs:954` 回退改路由、无 HWND 重建 | `:954` `if (!decision.IsGpu) { StopGpuPresentation(); _gpuRoute = decision; …` | ✅ |
| `UIController.cs:209,215` 同步重绘早于路由切换 | `:209 _form.Invalidate();` `:210 _form.Update();` → `:215 ApplyGpuRenderingRoute(t);` | ✅ 顺序断言精确成立 |
| `UIController.cs:634` Tick 按 GPU 路由与 NeedsRestart 尝试启动 | `:634 if (_gpuRoute?.IsGpu == true && _gpuPresentation?.NeedsRestart == true && _form.IsHandleCreated) StartGpuPresentation();` | ✅ |
| `UIController.cs:363` WDA 恢复失败时保留已 Dispose 宿主 | `:363 if (_gpuPresentation?.WindowCapturePolicyRestored == false) { … return; }` —— 返回前**不置 null** | ✅ |
| `GpuPresentationHostController.cs:740` 再次 Dispose 可尝试清理 | `:740 public void Dispose() { if (_disposed) { if (_presentation is not null) StopCore(); return; }` | ✅ |
| `PresentationSession.cs:65` HWND 已销毁时按完成处理 | `:65 if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle)) { … return true; }` | ✅ |
| `MainForm_Transparent.cs:266` HandleCreated 触发 GPU 宿主重建 | `:266 this.HandleCreated += …` → `:273 _ui?.NotifyGpuHandleChanged();` | ✅ |

**附带核验（文档未给行号但被引用的事实）**：

- `tools/GpuHostLifecycleProbe`（12 049 B `Program.cs`）、`tools/GpuUiProbe/Program.cs`（53 455 B / 1 332 行）、`scripts/run-gpu-performance.ps1` —— **均存在**。
- `GpuUiProbe/Program.cs` 确有文档所述四类入口：`:28 --legacy`、`:30 --recreate-handle`、主题互换（`stressThemeSwitches`）、隐藏/显示（`stressHideCount/stressShowCount`）。**§2.1 的"缺少 GPU 出帧后切回 legacy 再返回 GPU 的完整断言"成立。**
- 三份 GPU 主题 `LiquidGlass_GPU.json` / `LiquidGlass_Toolbar_GPU.json` / `CyberPunk_GPU.json` 与两份对照主题 `LiquidGlass.json` / `CyberPunk_Mech.json` —— **全部存在**。
- `rendering.fallback` 确实存在：`src/Rendering/Configuration/ThemeRenderingConfig.cs:33-38`（`JsonPropertyName("fallback")`，默认 `backend`/`material` 均为 legacy）。**§4.6 "优先扩展已有 `rendering.fallback` 的可选样式字段"的事实前提成立。**
- §7.1 关于 `LiteMonitor.GpuNonperformance.Tests` 的陷阱提示**准确**：`tests/LiteMonitor.GpuNonperformance.Tests/LiteMonitor.GpuNonperformance.Tests.csproj:5` 确有 `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>`，且 `:12-14` 是逐条 `<Compile Include=…>`。**"文件写了但根本没跑"是真实风险。**
- 数量自洽：U01–U14 共 **14** 条（文档称"14 项逻辑测试"）、G01–G12 共 **12** 组（文档称"12 组实机验收"）。

---

## 3. §2.1 微软 DXGI 依据核验 —— 逐字成立

抓取 `https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ne-dxgi-dxgi_swap_effect`，原文：

> "…the bitblt model is the only way to mix GDI and DirectX presentation. In the flip model, the application must create the swap chain with `DXGI_SWAP_CHAIN_FLAG_GDI_COMPATIBLE`, and then must use `GetDC` on the back buffer explicitly. **After the first successful call to `IDXGISwapChain1::Present1` on a flip-model swap chain, GDI no longer works with the HWND that is associated with that swap chain, even after the destruction of the swap chain.** This restriction even extends to methods like `ScrollWindowEx`."

对照文档 §2.1 的转述："flip-model 首次成功呈现后，关联 HWND 不再支持普通 GDI 绘制，**即使交换链已销毁**" —— **与原文逐句对应，连"即使交换链已销毁"这一最关键限定都在**。

**推论（计划正确采用）**：销毁交换链本身**不足以**恢复 GDI，必须重建 HWND → §4.1 的首选方案成立。

**额外确认（计划正确否决捷径）**：同页说明 `GDI_COMPATIBLE` 路径要求"显式对背缓冲 `GetDC`"，这**不是** WinForms `OnPaint` 的绘制方式（`OnPaint` 画到窗口 DC）。因此文档 §3"不以……增加 `GDI_COMPATIBLE` 作为未经验证的一行修复。它们均不是现有 WinForms OnPaint 的等价替代"——**技术判断正确**。

**一处需要保留的限定**：微软原文写的是 `Present1`，而本项目调用的是 `Present`（`GpuSceneRenderer.cs:644`）。文档 §2.1 末尾已经点明"本项目使用的是 `Present`，不要在报告中误写为代码调用了 `Present1`"——**这个提醒是对的，应当保留**。

---

## 4. 发现（按严重度）

### 🟠 A1 · 中 · HWND 代次缺少"单一真相源"，将与既有 `LiveGlassController` 代次漂移

**既有资产（计划未提及）**：

- `src/UI/LiveGlassController.cs:87` `private long _handleGeneration = 1;`
- `src/UI/LiveGlassController.cs:295-300`
  ```csharp
  internal void OnHandleChanged()
  {
      _handleGeneration++;
      RefreshRequest();
      ReleaseHeldFrame();
  }
  ```
- 该代次贯穿捕获请求链：`src/System/Backdrop/BackdropServiceRequest.cs:121`（`HandleGeneration`）、`:143-147`（请求等价性比较）、`src/System/Backdrop/ScreenCaptureFrameSource.cs:85-87`、`:184-186`（旧请求丢弃判定）、`src/System/Backdrop/BackdropCaptureAdapters.cs:274`。

**问题**：计划 §4.1 要求"重建触发依据按 HWND 代次保存，不能存放在即将 Dispose 的 renderer 内而随之丢失"，§4.2 的状态表另列"HWND generation / 宿主 generation"，但**全文未点名 `LiveGlassController` 的既有代次**。而 §4.3 步骤 5 又要求"更新所有缓存 HWND 的组件、窗口消息注册、Region 和**捕获代次**"，§4.5 要求新旧代次严格隔离。**若 T02/T03 新建第二套代次，两套代次会各自递增、互相不知情**——而 §4.7 的兼容清单里恰好列着"捕获策略""大小位置通知"这些依赖既有代次的部件。

**要求**：T02 必须显式写明"句柄代次的唯一真相源"，或与 `LiveGlassController._handleGeneration` 合并，或明确说明两者的对应关系与何时同步。§4.2 状态表的"HWND generation"行应注明与既有实现的关系。

### 🟠 A2 · 中 · T05 会撞上 `ThemeRenderingConfig.Validate()` 的既有硬校验，计划未提

**既有约束**（`src/Rendering/Configuration/ThemeRenderingConfig.cs:136-141`）：

```csharp
string fallbackBackend = GetString(Fallback, "backend");
string fallbackMaterial = GetString(Fallback, "material");
if (fallbackBackend != LegacyBackend || fallbackMaterial != LegacyMaterial)
{
    errors.Add("rendering.fallback must select legacy/legacy.");
}
```

即 `rendering.fallback` 下的 `backend`/`material` **当前被强制**为 legacy/legacy。§4.6 要在 `rendering.fallback` 里追加背景、分组底色、边框、前景色等样式字段——**必须同步扩展 `Validate()`**，否则新字段虽能写入却不受校验，且一旦实现者顺手给 `backend` 赋非 legacy 值，校验器会直接拒绝整个主题。

**连带项**：`ThemeEditor/AppearanceFields.cs:256-260` 的 `CloneRendering` 依赖 `ThemeRenderingConfig` 的拷贝路径（`ThemeRenderingConfig.cs:53-57`，其中 `Fallback` 走 `CopyDictionary`）。**字典是浅拷贝**；若新增字段以 `JsonElement` 承载，则克隆对象与源文档共享生命周期，可能在源 `JsonDocument` 释放后触发 `ObjectDisposedException`。这直接命中 §7.1 U12（"克隆保存取消 → 原 GPU 对象与文件不被污染"）。

**要求**：§5 的 T05 交付物应补上三项——扩展 `Validate()`、扩展 `CloneRendering` 的字段保留、明确新增字段的存储形态（建议用纯 CLR 类型而非 `JsonElement`）。§4.6 目前只写"优先扩展已有 `rendering.fallback` 的可选样式字段"，不足以让实现者意识到校验器与克隆路径是**硬依赖**。

### 🟠 A3 · 中 · `MainForm` 不是 partial，计划未点明，影响 T03 的文件落点

**实测**：`src/UI/MainForm_Transparent.cs:14` 是 `public class MainForm : Form`；全仓 `src/` 搜索 `class\s+MainForm` **只有这一处**（另两个是 `MainFormBizHelper`、`MainFormWinHelper`）。

**问题**：§4.3 步骤 4 要求"通过 MainForm 自己提供的内部受控入口调用 HWND 重建"。由于 `MainForm` **非 partial**，实现者无法新建 `MainForm_GpuTransition.cs` 作为 partial 扩展（会编译失败），只能把入口加进 `MainForm_Transparent.cs`；而 §6 又让 owner B 负责"新增切换协调器"。**正确做法是：协调器为独立类，MainForm 只保留一个薄入口**——这与 §6 的分工一致，但文档没有点明约束，实现者很可能先试 partial 路线。

**要求**：§4.3 步骤 4 或 §6 owner B 行补一句：`MainForm` 为非 partial 单文件类（`src/UI/MainForm_Transparent.cs:14`），重建入口须加在该文件，协调器独立成类。

### 🟠 A4 · 中 · "句柄在构造期被提前创建"这一既有事实未纳入契约，会导致 HandleCreated 语义歧义

**实测**（`src/UI/UIController.cs:299-302` 注释原文）：

> `Control.CreateGraphics()` 强制创建句柄，而这个过程发生在 `MainForm` 构造体内。等到真正显示窗口时句柄早已存在，`HandleCreated` 不再触发，`NotifyGpuHandleChanged` 于是永远不会被调用，GPU 宿主也就永远不会启动。

**问题**：这意味着 `HandleCreated` 在本项目有两种截然不同的来源：

1. **构造期首次创建**（当前实际发生的）：GPU 宿主因此**不会**被自动拉起——这是既有设计；
2. **重建后创建**（§4.3 步骤 4/5 新引入的）：这正是计划期望触发"原生生命周期处理"的时机。

§4.3 步骤 5"让 HandleDestroyed/HandleCreated 处理原生生命周期"与 §4.4"不允许普通 Tick 或 HandleCreated 无条件把 GPU 拉起来"，**只有在明确区分这两类来源后才成立**。§4.7 只要求"搜索真实 HWND 持有者"，没有把这条既有事实列为必须复用的约束。

**要求**：§4.2 的"切换阶段"或 §4.3 步骤 4/5 应显式规定：重建入口触发的是**受控重建**，必须携带一个"这是重建、不是首次创建"的判别位（例如重建代次或一次性重建标志），`HandleCreated` 处理器据此决定是否走 GPU 重建路径；构造期的首次创建行为**保持不变**。

### 🟡 A5 · 低 · `RecreateHandle` 全仓无先例，主路线的信心表述应下调

**实测**：全仓 `src/` 与 `tools/` 搜索 `RecreateHandle` → **零命中**。`Control.RecreateHandle()` 在 WinForms 中是 `protected`，对 `Form` 重建顶层窗口句柄**并非官方常规路径**：它会销毁并重建顶层窗口，触发完整一轮 `HandleDestroyed`/`HandleCreated`，并丢失部分窗口状态（这正是计划 §4.3 步骤 6/7 要逐项恢复的原因）。

**问题**：计划 §3 对"换交换链为 BitBlt""改 `WS_EX_LAYERED`""加 `GDI_COMPATIBLE`"都写了"不作为未经验证的一行修复"，这种克制是对的；但对**主路线本身**（重建 HWND）没有同等克制。§9 已备有"独立 GPU 子 HWND"备用路线，T01 也被设计成决策闸门——**结构是对的**，只是文档没有明确说明"T01 若证明 `RecreateHandle` 在本项目 MainForm 上不安全，就直接走 §9，而不是继续在主线打补丁"。

**要求**：§4.1 或 §9 补一条判定规则——T01 必须给出"`RecreateHandle` 在 MainForm 上是否可安全使用"的明确结论；不可安全使用时**不得**先落主线再改。

### 🟡 A6 · 低 · T02 应显式复用 `RenderingRoutePolicy`，避免第二套"期望路由"口径

**实测**：`src/Rendering/Configuration/RenderingRoutePolicy.cs` 已有
`public enum RenderingRoute`（`:5`）、`public sealed record RenderingRouteRequest`（`:12`）、`public sealed record RenderingRouteDecision`（`:19`，含 `:27 public bool IsGpu => Route != RenderingRoute.Legacy;`）、`public static class RenderingRoutePolicy`（`:30`）+ `public static RenderingRouteDecision Decide(RenderingRouteRequest request)`（`:32`）——**这正是 §4.2 表中"期望路由：策略计算所得 Legacy / GPU Fixed / GPU Live"的现成纯函数实现**。

**问题**：§4.2 只写"由纯状态模型描述意图，具体类型名可在 T02 冻结；不要求把现有类整体重写"，**从未点名 `RenderingRoutePolicy`**。实现者很可能另建一套期望路由枚举与决策函数，产生两个口径——而 `UIController.cs:951`（`if (decision.IsGpu || _gpuRoute?.IsGpu == true)`）正是消费这个决策的地方。

**要求**：§4.2 明确"T02 在 `RenderingRoutePolicy` 现有 `RenderingRouteRequest/Decision/Decide` 基础上扩展期望路由与有效路由的分离，不另立平行模型"。

### 🟡 A7 · 低 · "旧 M2 捕获"是未定义缩写

§4.5"若旧主题明确启用**旧 M2 捕获**"、§7.1 U14"**旧 M2** 与 GPU live 转换"——实测 `src/` 与其余计划文档中搜索 `\bM2\b` **零命中**，即该缩写**仅存在于本文档**，无定义。实施者无从判断它指 `LiveGlassController` / backdrop capture 那一套，还是别的东西。

**要求**：首次出现处给出明确定义与代码位置（推测指 `src/UI/LiveGlassController.cs` + `src/System/Backdrop/**` 这一套旧捕获，但需作者确认）。

### 🔵 A8 · 信息 · 建议为 §4.7 的类型名补真实路径

§4.7 点名 `MainFormWinHelper`、`GpuStartupGuard` 等但不给路径，实施者可能误判文件位置。实测真实路径：

| 类型 | 真实路径 |
|---|---|
| `MainFormWinHelper` | `src/UI/Helpers/MainFormWinHelper.cs:15` |
| `GpuStartupGuard` | `src/Rendering/Configuration/GpuStartupGuard.cs:7` |
| `ThemeRenderingConfig` | `src/Rendering/Configuration/ThemeRenderingConfig.cs:7` |
| `RenderingRoutePolicy` | `src/Rendering/Configuration/RenderingRoutePolicy.cs:30` |
| `LiveGlassController`（句柄代次宿主） | `src/UI/LiveGlassController.cs:87` |

### 🔵 A9 · 信息 · 计划文件当前**未纳入版本控制**

`git status` 显示 `docs/superpowers/plans/2026-10-22-gpu-legacy-runtime-transition-plan.md` 为 **未跟踪（`??`）**。文档 §3 声明"不自动提交、推送、发布"，与"不 push"一致，但**计划本身应当入库**——否则 T00 的"候选构建输入"与"HEAD 观测值"无法绑定到可追溯的文档版本。

**要求**：复审通过后与本评审文档一并提交。

---

## 5. 已明确无需修订的强项（应保留）

1. **T01 作为技术路线的决策闸门**：先由短时探针对比"只释放交换链"与"释放后重建 HWND"，证据不支持就出 ADR 修订，而不是先落实现再补证。
2. **§2.2 的诚实声明**："本轮尚未实机复现，不能预先宣称所有用户现象已被单一根因解释"。
3. **§7.3 的证据纪律**：`DrawToBitmap`/`PrintWindow`/offscreen render 成功**都不能**单独证明 flip 后的同一 HWND 已恢复桌面呈现；WDA 排除导致截图看不到窗口时**不得**擅自关 WDA 再宣称通过。这两条直接针对本项目最容易出现的假绿。
4. **§7.2 的"必须等待 GPU 首次成功呈现后再操作；不是创建宿主即算 GPU 已运行"**——与仓库现状一致（`GpuSceneRenderer.cs:644` 才是真正的呈现点）。
5. **§9 的备用路线与"不得偷偷加入第二个顶层窗口"的纪律**。
6. **§5 末句**："资源泄漏、重复会话与无界重试属于正确性，仍阻断完成"——把资源正确性与性能预算解耦，定义清晰。
7. **§10 的回滚纪律**：按职责提交定位回滚，不丢工作区、不永久强制 legacy、不把"重启"计作修复通过。

---

## 6. 修订建议汇总（可直接执行）

| 编号 | 落点 | 动作 |
|---|---|---|
| A1 | §4.1 / §4.2 状态表 / §4.3 步骤 5 | 写明句柄代次的唯一真相源，与 `LiveGlassController._handleGeneration` 对齐或说明关系 |
| A2 | §4.6 / §5 T05 交付物 / §7.1 U12 | T05 补交付项：扩展 `ThemeRenderingConfig.Validate()`、扩展 `CloneRendering` 字段保留、明确新字段存储形态（避免 `JsonElement` 浅拷贝） |
| A3 | §4.3 步骤 4 / §6 owner B | 点明 `MainForm` 非 partial（`src/UI/MainForm_Transparent.cs:14`），入口加在该文件，协调器独立成类 |
| A4 | §4.2 切换阶段 / §4.3 步骤 4-5 / §4.4 | 规定重建入口携带"受控重建"判别位；`HandleCreated` 必须区分构造期首次创建与重建后创建 |
| A5 | §4.1 / §9 | 明确 T01 必须给出"`RecreateHandle` 在 MainForm 上是否安全"的结论，不安全则直接走备用路线 |
| A6 | §4.2 | 明确在 `RenderingRoutePolicy` 现有模型上扩展，不另立平行期望路由 |
| A7 | §4.5 / §7.1 U14 | 定义"旧 M2 捕获"并给出代码位置 |
| A8 | §4.7 | 补类型真实路径表（§4.8 附表已备） |
| A9 | 提交 | 计划与本评审一并入库 |

---

## 7. 本次评审**未**验证的项

1. **未做任何构建、测试或实现修改**——本评审为纯只读核验，符合文档 §3"不停止日常运行实例、不改真实用户配置"。
2. **未实机验证 flip→GDI 的实际行为**：§3 的结论是**文档依据**，不是本机实测。文档 §2.2 已声明"实机结论仍由 T01 的探针取得"，本评审维持该定性。
3. **未审查 `docs/superpowers/plans/2026-10-22-performance-sampling-verification.md`**（GPU 非性能收口计划）——文档 §1 称"承接现有 GPU 非性能收口计划"，二者的一致性未核。
4. **未评估该计划与当前在途修复（S2.x / S3.x）的交叉影响**：本次在途任务不触碰 `src/Rendering/**`、`src/UI/UIController.cs`、`src/UI/MainForm_Transparent.cs`，故**当前无文件所有权冲突**；但 `src/UI/Settings/MainPanelPage.cs` 是文档 §5 T08 与 S2.2 双方都需要接线的地方，**T08 启动前必须与 S2.2 的 owner 串行交接**（文档 §3 已提示"尤其设置页接线需与当前 owner 串行交接"，此处予以确认）。

---

## 8. 判定

**结论：NEEDS-REVISION。**

- §2.1 的 9 条代码事实断言 **9/9 命中**，§2.1 的微软依据**逐字成立**，HEAD 与声明值一致——**事实层可信度高，可作为后续复核的基准**。
- A1–A4 为**中等缺口**，共同指向同一类问题：计划描述的是"应当如何"，但未充分对齐仓库**已经存在的同类资产与硬约束**。按字面执行会产生第二套代次口径、撞上既有主题校验器、误判 `HandleCreated` 语义。这些不需要改变技术路线，但需要把"现有资产"写进契约。
- A5–A9 为**低等缺口**，属精度与可追溯性，不阻断路线。
- 建议：按 §6 逐条修订后**重新复审一轮**（重点复核 A1–A4 是否落到可执行条款），再进入 T00。**T00 之前不需要任何实现改动。**
