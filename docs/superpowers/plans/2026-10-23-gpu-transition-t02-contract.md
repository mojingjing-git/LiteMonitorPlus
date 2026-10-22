# T02 合同冻结：GPU ↔ legacy 运行时切换的状态 / 清理 / 代次 / fallback 字段

> **任务**：T02 · 冻结状态、转交清理、generation 与 fallback 字段合同（GPU 计划 §5 / §4.2 / §4.3 / §4.4 / §4.5 / §4.6 / §7.1 / §11）
> **产出时间**：2026-10-23
> **上游**：`docs/superpowers/plans/2026-10-22-gpu-legacy-runtime-transition-plan.md`（REVISION-1，复审第 2 轮 PASS）
> **基线**：`docs/superpowers/plans/2026-10-23-gpu-transition-t00-baseline.md`（HEAD `e1bd587`）
> **性质**：**纯设计/合同**。本轮**不写生产代码、不跑 GUI、不做硬件实测**。
> **物化约束**：T01 正在跑，`src/Rendering/**` 聚合哈希冻结（`a39d49ce…`）⇒ **本轮未创建/修改 `src/` 下任何文件**。本文件第 3 节只给规格与签名草案；物化时点见第 12 节。

---

## 0. 标记约定与读法

| 标记 | 含义 |
| --- | --- |
| `【事实】` | 既有代码事实，**必须**附 `路径:行号`；可被任何人独立复核 |
| `【裁决】` | **T02 的新设计决策**（改变行为或新增契约）。评审时凡见此标记即为新决策 |
| `【推断】` | 由事实推出的结论，**未实测** |
| `NOT_VERIFIED` | 无法在不实测的前提下裁决；已注明需要什么证据（汇总见第 15 节） |

**既有事实 / 裁决 的分界在每张表里用独立列标出**，不混写。

---

## 1. ADR（小型）

### 1.1 背景

`【事实】` 计划 §4.1 主线要求"保留 Form 对象、按需重建 HWND"；T00 §6 登记了 5 项既有缺陷，其中 **GPU-001（高）直接卡死主线**。
`【事实】` GPU-001 的根因链条逐行可查：

| 环节 | 位置 | 事实 |
| --- | --- | --- |
| 恢复失败 | `src/Rendering/Presentation/GpuPresentationHostController.cs:702-709` | `CompleteStop` 失败 ⇒ 重试一次（`:707-708`）；两次都失败 ⇒ `_presentation` **不置 null** |
| 代理指标 | `src/Rendering/Presentation/GpuPresentationHostController.cs:93` | `WindowCapturePolicyRestored => _presentation is null` ⇒ 上一步后为 `false` |
| 保留宿主引用 | `src/UI/UIController.cs:360-369` | `:363` `if (_gpuPresentation?.WindowCapturePolicyRestored == false) { … return; }` ⇒ `:368` 的 `_gpuPresentation = null` **不执行** |
| 早退 1 | `src/UI/UIController.cs:280` | `if (_gpuPresentation is not null) return;`（`NotifyGpuHandleChanged`） |
| 早退 2 | `src/UI/UIController.cs:312` | `if (_gpuPresentation is not null) return;`（`EnsureGpuPresentationStarted`） |
| 早退 3 | `src/UI/UIController.cs:634-636` | Tick 依赖 `NeedsRestart`；`GpuPresentationHostController.cs:81-85` 的 `NeedsRestart` 含 `!_disposed` ⇒ 已 Dispose 的宿主恒为 `false` |

`【事实】` 现有代码把**"宿主引用非空"**当作**"清理责任仍在"**的代理，而计划 §4.5 要求二者**解耦**（"宿主释放后由 UI 生命周期所有者继续持有它（清理对象）"、"新代次必须独立确认原始策略并取得所有权"、"同一 HWND 上清理未完成时禁止叠加新的 affinity 所有者"）。
`【事实】` 计划 §4.2 明文要求：期望路由/有效路由**必须在** `src/Rendering/Configuration/RenderingRoutePolicy.cs:5/12/19/32` 的既有 `Request/Decision/Decide` 上扩展，消费点 `src/UI/UIController.cs:951` 不得分叉；句柄代次**二选一**（合并为同一真相源，或 GPU 侧另计但同一次 `HandleCreated` 同步递增并可互相校验）。

### 1.2 决策

| ID | 决策 | 状态 |
| --- | --- | --- |
| **D1** | **句柄代次：合并为同一真相源**（不是"GPU 侧另计"）。新类型 `WindowHandleGeneration`（owner A 新增文件）持有唯一单调计数器；`LiveGlassController._handleGeneration`（`src/UI/LiveGlassController.cs:87`）**移除**，改为只读消费该真相源 | 【裁决】 |
| **D2** | **清理责任与宿主解耦**：新增 `WindowCapturePolicyCleanup`（纯记录）+ `CapturePolicyCleanupLedger`（持有者）。宿主恢复失败时**移交**清理记录并**释放**宿主；`UIController._gpuPresentation` 无条件置 null | 【裁决】 |
| **D3** | **`PresentationSession.RecreateWindow(...)` 正式采用**，并补齐 6 项缺失语义（见 §7.3）。不废弃 | 【裁决】 |
| **D4** | **`WS_EX_LAYERED` 与 WDA 互斥**：`WS_EX_LAYERED` 置位 ⇒ 该代次**不得**申请 `WDA_EXCLUDEFROMCAPTURE`，降级为 `GpuFixedInput`（期望路由不变、有效路由变）。同 HWND 上**不**新增清除行为（避免改变既有透明度语义），只做诊断 | 【裁决】 |
| **D5** | **代次判据先于 `IsWindow`**：清理对象以 `Generation` 对比当前真相源；`cleanup.Generation < Current` ⇒ **禁止**对该 HWND 执行任何 Win32 调用，直接终结（reason=`superseded_by_new_generation`） | 【裁决】 |
| **D6** | **`GpuStartupGuard` pending 的精确定义**：pending ⟺ 存在"已开始但未被显式确认为干净"的启动尝试。清理只允许两种触发：**首帧成功**、**受控放弃且停止流程本身干净**。首帧失败且停止流程**不干净**（如 affinity 恢复失败）⇒ **pending 必须保留** | 【裁决】 |
| **D7** | **期望路由留在 `RenderingRouteDecision.Route`（既有语义不变），有效路由放入新的纯状态类型**。`RenderingRouteDecision` **不新增运行时语义成员** | 【裁决】 |
| **D8** | **唯一消费点 `src/UI/UIController.cs:951` 保持逐字不变**；有效路由只用于新消费点（设置页/诊断），不参与该判断 | 【裁决】 |
| **D9** | **`HandleCreated` 判别位 = 一次性计数令牌** `PendingControlledRecreateCount`（owner B 协调器），配"取消 + 陈旧清理"兜底 | 【裁决】 |
| **D10** | **fallback 新字段用纯 CLR 类型 + 新增独立属性 `rendering.fallbackStyle`**，不塞进 `rendering.fallback` 字典（该字典是 `Dictionary<string, JsonElement>`，与 §4.6 硬依赖 2 冲突）。`rendering.fallback` 的 `backend`/`material` 规则**一字不改** | 【裁决】 |

### 1.3 被否决的备选

| 备选 | 否决理由 |
| --- | --- |
| **GPU 侧另计一套句柄代次** | 会同时产生两套递增计数器；若不引入跨代次配对机制，则 GPU-001/004 的"旧清理命中新窗口"无法验证。合并方案消除了整个类别 |
| **保留 `_gpuPresentation` 引用作为清理责任的载体**（现实现） | 即 GPU-001 根因本身。计划 §4.5 明确要求解耦 |
| **废弃 `PresentationSession.RecreateWindow`，每次重建都新建 session** | 新 session 必须在**新 HWND 上先读原值**才能取得所有权，但此时**旧 HWND 的 affinity 尚未恢复** ⇒ 同一时刻出现两个 affinity 所有者，违反 §4.5"同一 HWND 上清理未完成时禁止叠加新的 affinity 所有者"的精神，且旧 HWND 的 `WDA_EXCLUDEFROMCAPTURE` 会被遗留 |
| **在 `SetClickThrough(false)` 里一并清 `WS_EX_LAYERED`** | `Form.Opacity < 1` 依赖 `WS_EX_LAYERED`（`src/UI/MainForm_Transparent.cs:48-52` 明确"必须配合才能实现完整穿透"）⇒ 清除会改变既有透明度产品语义，超出本计划范围；计划 §3 亦明文禁止"全局修改 `WS_EX_LAYERED` 作为未经验证的一行修复" |
| **把新 fallback 字段放进 `rendering.fallback` 字典** | 该字典是 `Dictionary<string, JsonElement>`（`src/Rendering/Configuration/ThemeRenderingConfig.cs:34`）⇒ 新字段必然是 `JsonElement`，直接违反 §4.6 硬依赖 2 |
| **把 `rendering.fallback` 整体改成 typed class** | 破坏 `ThemeRenderingConfig.Fallback` 的公开类型 ⇒ 连带改 `src/Rendering/Contracts/RenderContracts.cs:499` 的 `RenderingConfig.Fallback` ⇒ 该文件**未被计划 §6 归属任何 owner**，且改动面远超"可选样式字段"。列为被否决 + 备查 |
| **有效路由放进 `RenderingRouteDecision`** | `Decide(...)`（`RenderingRoutePolicy.cs:32`）是**纯函数**；有效路由依赖运行时事实（HWND、layered、恢复预算）⇒ 会把纯函数变成有副作用/不可测 |

### 1.4 后果

**正面**：
- GPU-001 被结构性消除：`UIController._gpuPresentation` 与清理责任不再共用一个引用，§4.3 主线依赖的 `NotifyGpuHandleChanged`（`src/UI/UIController.cs:272-292`）重新可达。
- T03–T08 可按本文件并行开工：每条规则都落到具体文件与字段（见第 11 节 owner 列）。

**负面 / 代价**：
- 新增 1 个跨 `src/Rendering/Configuration/` ↔ `src/UI/` 的共享计数器类型，D1 强制改动 `src/UI/LiveGlassController.cs`（owner B，同一 owner 内，无跨 owner 冲突）。
- `src/Rendering/Configuration/GpuStartupGuard.cs` **必须修改**（D6），但计划 §6 **未把它归属给任何 owner** ⇒ 见第 12.3 节的交接条目。
- `PresentationSession` 的复用语义被正式采纳（D3）⇒ `PresentationSession.cs` 的契约面扩大，owner C 需要按 §7.3 补 6 项语义 + 对应单测。

**中性**：
- `rendering.fallback` 的 JSON 形状与既有 15 份主题**逐字不变**；新增块缺省不写出（`JsonIgnoreCondition.WhenWritingNull`）⇒ 旧主题像素输出与保存结果不变。

### 1.5 状态

**提议（PROPOSED），待 T01 结论后定稿。** 本文件在 T01 给出"可安全使用 / 不可安全使用 / 不确定"结论前不得视为冻结；第 12 节列出随 T01 结论需要改写的条款。

---

## 2. 状态表（8 维度）

`【事实】` 维度划分来自计划 §4.2 的表格（`docs/superpowers/plans/2026-10-22-gpu-legacy-runtime-transition-plan.md:151-160`）。下表把每一维度落到**具体类型与字段**。

> **物化位置**：类型签名草案见第 3 节；全部落在 owner A 的新增文件 `src/Rendering/Configuration/RenderingTransitionState.cs` + owner A 扩展 `src/Rendering/Configuration/RenderingRoutePolicy.cs`。**本轮不落盘。**

| # | 维度 | 类型 / 字段 | 取值域 | 谁写 | 谁读 |
| --- | --- | --- | --- | --- | --- |
| 1 | **用户选择** | `_cfg.GpuThemeEffectsEnabled` / `_cfg.GpuLiveBackgroundEnabled` / `_cfg.GpuWdaCapturePolicyAccepted` / `_cfg.Skin`（既有 `Settings`）<br>`【事实】` 消费点 `src/UI/UIController.cs:941-946` | bool / string | 用户（设置页）；`ApplyTheme`（`src/UI/UIController.cs:215`） | `RenderingRoutePolicy.Decide`（`RenderingRoutePolicy.cs:32`）；设置页（F） |
| 2 | **期望路由** | `RenderingRouteDecision.Route`（`RenderingRoutePolicy.cs:20`）**既有成员，语义不变**<br>派生：`RenderingRouteDecision.IsGpu`（`:27`） | `RenderingRoute`（`RenderingRoutePolicy.cs:5-10`）= `Legacy` / `GpuFixedInput` / `GpuLiveBackground` | `RenderingRoutePolicy.Decide`（纯函数，`:32`） | `UIController._gpuRoute`（`src/UI/UIController.cs:55`）；消费点 `:951`、`:954`、`:274`、`:311`、`:634`、`:1008` |
| 2b | **期望路由的决策输入（新增）** | `RenderingRouteRequest.WindowIsLayered`（**新增可选成员**，默认 `false`） | bool | 协调器（读 `GetWindowLong(GWL_EXSTYLE)`） | `Decide`（`:32`） |
| 3 | **有效路由** | `RenderingTransitionState.EffectiveRoute` | `RenderingRoute`（**复用既有枚举，不新增**） | 协调器（B） | 设置页状态（F）、诊断面板；**不参与** `src/UI/UIController.cs:951` |
| 4 | **切换阶段** | `RenderingTransitionState.Phase` | `RenderingTransitionPhase`：`Stable` / `Stopping` / `RecreatingHandle` / `Restoring` / `Starting` / `Failed`（**与计划 §4.2 `:156` 逐字一致**） | 协调器（B） | 协调器自身；Tick 守卫（`:634`）；设置页 |
| 5 | **身份** | `RenderingTransitionState.TransitionId`（long）<br>`RenderingTransitionState.HandleGeneration`（long）<br>`RenderingTransitionState.HostGeneration`（long）<br>`RenderingTransitionState.LastAppliedRequestVersion`（long） | long，单调递增 | 协调器（B） | 回调丢弃判据（§7.4） |
| 6 | **HWND 呈现历史** | `RenderingTransitionState.BindingState` | `PresentationBindingState`：`Unbound` / `Bound` / `PresentAttempted` / `Presented`（**与计划 §4.2 `:158` 逐字一致**） | 宿主（C）经协调器（B）回写 | 重建触发依据（计划 §4.1 `:123-126`） |
| 7 | **清理状态** | `RenderingTransitionState.CleanupState`<br>`CapturePolicyCleanupLedger.Entries`（`IReadOnlyList<WindowCapturePolicyCleanup>`）<br>`WindowCapturePolicyCleanup.State` | `CapturePolicyCleanupState`：`None` / `Pending` / `Completed` / `Failed`（**与计划 §4.2 `:159` 逐字一致**）<br>独立清理所有者 = ledger（UI 生命周期所有者，B 持有） | 宿主（C）移交 → 协调器/ledger（B）推进 | 设置页告警（F）；诊断 |
| 8 | **诊断** | `RenderingTransitionState.Trigger`（`RenderingTransitionTrigger` 枚举）<br>`RenderingTransitionState.FailureCode`（string?）<br>`RenderingTransitionState.CleanupErrorCode`（string?）<br>`RenderingTransitionState.FirstFrameOutcome`（枚举）<br>`RenderingTransitionState.CleanupWarningCode`（string?）<br>`RenderingTransitionState.PendingFirstFrame`（bool） | 见 §5 错误码表 | 协调器（B）+ 宿主（C）上报 | 设置页（F）；日志；T06 单测断言 |

**【裁决】"不混成一个字符串"的落地**：`FailureCode`（呈现失败根因）、`CleanupErrorCode`（清理失败根因）、`CleanupWarningCode`（清理告警）**三个独立字段**，互不覆盖；`Trigger` 与 `TransitionId` 分离。§5 错误码表列出每个字段可取的码值集合。

**【裁决】`HostGeneration` 不是句柄代次**：它计数"宿主实例"（每次新建 `GpuPresentationHostController` 递增），用于区分"同一 HWND 上的不同宿主会话"（U14）。计划 §4.2 的"唯一真相源"约束针对**句柄代次**，本字段不构成第二套句柄代次计数器。

---

## 3. 类型与签名草案（纯 CLR；**本轮不落盘到 `src/`**）

> 落点：`src/Rendering/Configuration/RenderingTransitionState.cs`（owner A **新增**文件）+ `src/Rendering/Configuration/RenderingRoutePolicy.cs`（owner A **扩展**既有文件）。
> 全部类型**不依赖 WinForms / DXGI**，可被 T06 直接单测（计划 §7.1 要求"确定性测试，无真实桌面依赖"）。

### 3.1 既有类型的扩展（唯一一处）

```csharp
// src/Rendering/Configuration/RenderingRoutePolicy.cs  —— owner A 扩展
public sealed record RenderingRouteRequest(
    ThemeRenderingConfig? Configuration,
    bool GpuEffectsEnabled,
    bool LiveBackgroundEnabled,
    bool WdaCapturePolicyAccepted,
    bool SafeMode,
    bool WindowIsLayered = false);        // ← 【裁决】新增可选成员，默认 false
```

- **既有消费点兼容**：`src/UI/UIController.cs:941-946` 用具名参数构造 `new(...)` ⇒ 新增尾部可选参数**源码兼容**，既有 4 处调用无需改动即可编译。
- **既有 `RenderingRouteDecision` 不新增成员**（D7）：`Route`（`:20`）即期望路由；`CaptureRequested`（`:23`）即"是否需要 WDA 所有权"的意图；`FallbackCode`（`:24`）与 `Errors`（`:25`）即诊断通道。新增的运行时语义一律放到 §3.2 的新类型，避免污染纯函数。

**`Decide(...)` 的新增分支（【裁决】）**，插在既有 `:104-113` 的 `WdaCapturePolicyAccepted` 判定**之后**：

```csharp
// 期望仍是 GPU live，但窗口处于分层窗口 ⇒ 不得申请 WDA 排除
// ⇒ 降级为 GpuFixedInput（既有路线，RenderingRoutePolicy.cs:95-102 同一形状），
//    并给出独立原因码；期望路由 = 有效路由中的"有效"侧由协调器承担。
if (request.WindowIsLayered)
{
    return new RenderingRouteDecision(
        RenderingRoute.GpuFixedInput,
        RenderBackendKind.Gpu,
        MaterialKind.LiquidGlass,
        CaptureRequested: false,
        FallbackCode: "wda_capture_policy_unsupported_on_layered_window",
        errors);
}
```

- **放置位置理由**：`request.WindowIsLayered` 只在**已经决定要 GPU** 的分支上有意义；放在 `:104` 之后保证 `safe_mode`（`:61`）、`gpu_effects_opt_in_required`（`:66`）等既有原因码**优先级不变**（既有测试的期望字符串不受影响）。
- **既有行为零变化**：`WindowIsLayered` 默认 `false` ⇒ 未显式传入时，`Decide` 的输出与今天**逐位相同**。

### 3.2 新增纯状态类型（草案签名）

```csharp
namespace LiteMonitor.Rendering.Configuration;

/// 【裁决】切换阶段。取值与计划 §4.2 :156 逐字一致，不增不减。
public enum RenderingTransitionPhase
{
    Stable = 0,
    Stopping = 1,
    RecreatingHandle = 2,
    Restoring = 3,
    Starting = 4,
    Failed = 5,
}

/// 【裁决】HWND 呈现历史。取值与计划 §4.2 :158 逐字一致。
public enum PresentationBindingState
{
    Unbound = 0,          // 未绑定
    Bound = 1,            // 已绑定
    PresentAttempted = 2, // 已尝试呈现
    Presented = 3,        // 已成功呈现
}

/// 【裁决】清理状态。取值与计划 §4.2 :159 逐字一致。
public enum CapturePolicyCleanupState
{
    None = 0,
    Pending = 1,
    Completed = 2,
    Failed = 3,
}

/// 【裁决】首帧结果。区分"未尝试 / 成功 / 失败 / 结果不明"——
/// "结果不明"是计划 §4.1 :124 保守重建的触发依据。
public enum FirstFrameOutcome
{
    NotAttempted = 0,
    Succeeded = 1,
    Failed = 2,
    Unknown = 3,
}

/// 【裁决】触发原因。用于诊断"为什么进入了这次切换"。
public enum RenderingTransitionTrigger
{
    UserThemeChange = 0,
    UserGpuToggle = 1,
    UserLiveToggle = 2,
    UserWdaAcceptance = 3,
    RuntimeFailure = 4,
    HandleRecreated = 5,
    VisibilityChanged = 6,
    StartupGuardConservative = 7,
    ExplicitRetry = 8,
    DpiChanged = 9,
    DisplayChanged = 10,
}

/// 【裁决】纯状态快照。全部为不可变 record；写入者 = 协调器（owner B）。
public sealed record RenderingTransitionState(
    long TransitionId,
    long HandleGeneration,
    long HostGeneration,
    long LastAppliedRequestVersion,
    RenderingRoute ExpectedRoute,
    RenderingRoute EffectiveRoute,
    RenderingTransitionPhase Phase,
    PresentationBindingState BindingState,
    CapturePolicyCleanupState CleanupState,
    RenderingTransitionTrigger Trigger,
    FirstFrameOutcome FirstFrameOutcome,
    bool PendingFirstFrame,
    string? FailureCode,
    string? CleanupErrorCode,
    string? CleanupWarningCode);

/// 【裁决】清理记录（GPU-001 的责任载体）。可变状态机，纯 CLR，可单测。
public sealed class WindowCapturePolicyCleanup
{
    public IntPtr WindowHandle { get; init; }          // 旧 HWND（仅在 Generation==Current 时允许触碰）
    public long Generation { get; init; }              // 【裁决】所属句柄代次（来自唯一真相源）
    public uint OriginalAffinity { get; init; }        // 原始 affinity（精确值，不写死 WDA_NONE）
    public bool AffinityChangedBySession { get; init; }// 是否由本程序修改过（未改过 ⇒ 无需恢复）
    public int Attempts { get; private set; }           // 已尝试次数（清理专用预算）
    public string? LastErrorCode { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public CapturePolicyCleanupState State { get; private set; }
    public string? TerminalReason { get; private set; } // superseded / window_destroyed / exhausted / form_closed
    public bool IsTerminal => State is CapturePolicyCleanupState.Completed
                                   or CapturePolicyCleanupState.Failed;
}

/// 【裁决】句柄代次唯一真相源（计划 §4.2 二选一的"合并"方案）。
/// 唯一递增点 = Advance()；唯一驱动者 = 协调器在 HandleCreated 时调用。
public sealed class WindowHandleGeneration
{
    public long Current { get; private set; } = 1;
    public long Advance() => ++Current;                 // 【裁决】唯一递增点
    public bool IsCurrent(long generation) => generation == Current;
    public bool IsOlder(long generation) => generation < Current;  // 【裁决】GPU-004 丢弃判据
}
```

### 3.3 物化时点（交接给 C 期）

- **物化前置条件**：T01 结论落地 + 计划 §6 的 `src/Rendering/**` 冻结哈希**重新冻结**（当前 `a39d49ce…` 对应 B 期候选，C 期改 `src/` 后必然变化）。
- **物化归属**：owner A（`src/Rendering/Configuration/RenderingTransitionState.cs` 新增 + `RenderingRoutePolicy.cs` 扩展）。
- **物化顺序建议**：先落 §3.2 的纯类型（无行为变更）⇒ 再落 §3.1 的 `WindowIsLayered` 分支（需同步改 `src/UI/UIController.cs:941-946` 的构造调用，owner B）⇒ 最后落 D1 的代次合并（owner B）。
- **测试接口**：计划 §5 要求 T02 交付"最小测试接口"。本文件把该接口定义为 §3.2 的全部 public 纯类型 —— T06 可直接构造 `RenderingTransitionState` / `WindowCapturePolicyCleanup` 并断言状态跃迁，无需真实桌面。

---

## 4. 事件顺序表（GPU → legacy 的 9 步）

`【事实】` 步骤划分来自计划 §4.3 `:166-177`（9 步）。下表补齐触发者 / UI 线程动作 / 禁止动作 / 失败语义。

**全局约束（对全部 9 步生效）**：
- 所有窗口操作在 UI 线程执行（计划 §4.2 `:162`）。
- **禁止**在 `OnPaint` / `WndProc` 的销毁回调 / GPU 帧绘制栈内递归执行本表任何一步（计划 §4.3 `:169`）。既有的帧绘制栈入口是 `GpuPresentationHostController.OnTimerTick`（`:613`）/ `RenderScene`（`:629-667`）；既有的销毁回调入口是 `MainForm.OnHandleDestroyed`（`src/UI/MainForm_Transparent.cs:410-420`）。
- 关闭窗体拥有最高优先级（计划 §4.2 `:162`）：关闭过程中不得重建 HWND、不得恢复 affinity、不得重新创建宿主。

| 步 | 触发者 | 必须在 UI 线程的动作 | 禁止动作 | 失败语义 |
| --- | --- | --- | --- | --- |
| **1 计算新请求 + 进入保护** | 设置变更 / 换肤（`src/UI/UIController.cs:215` `ApplyGpuRenderingRoute`）/ 用户开关 / 故障回调 | `RenderingRoutePolicy.Decide(...)`（`RenderingRoutePolicy.cs:32`）⇒ 写 `ExpectedRoute`、`LastAppliedRequestVersion++`、`TransitionId++` ⇒ `Phase=Stopping` ⇒ 置"切换保护位" | 不得在 `OnPaint` / GPU 帧栈内执行；不得递归 `ApplyTheme`（计划 §4.3 `:166`） | `Decide` 抛异常 ⇒ `gpu_transition_decide_failed`；**保持原状态**，不进入 `Stopping` |
| **2 记录窗口状态 + 停呈现调度** | 协调器（步 1 之后） | 快照 `Bounds / ClientSize / DeviceDpi / Visible / WindowState / TopMost / ClickThrough / Opacity` ⇒ 写 `BindingState` ⇒ `GpuPresentationHostController.Suspend()`（`:298-311`，停 `_timer` + 停 live 捕获）⇒ 旧场景回调由代次守卫丢弃（`UpdateScene` `:278-281`） | 不得触碰 HWND ex-style；不得调用 `SetWindowDisplayAffinity`；不得销毁业务对象（计划 §4.7） | 快照失败 ⇒ `gpu_transition_window_state_snapshot_failed` ⇒ `Phase=Failed`，**不重建** |
| **3 停止捕获 + 释放资源 + 转交清理** | 协调器 | `GpuPresentationHostController.StopCore()` 等价路径 ⇒ 释放 renderer/交换链（`:713-714`）⇒ **若 affinity 恢复失败 ⇒ 生成 `WindowCapturePolicyCleanup` 并移交 ledger** ⇒ **宿主 `_presentation` 必须置 null**（D2，修复 GPU-001） | **不得**以"宿主引用非空"代理"清理责任仍在"；**不得丢弃**清理记录；不得无上限重试 | 恢复失败 ⇒ `gpu_capture_policy_restore_failed`（既有码，`GpuPresentationHostController.cs:704`）⇒ 转交清理对象 ⇒ **不阻塞**新代次取得所有权 |
| **4 受控 HWND 重建** | 协调器，按计划 §4.1 `:123-127` 的重建触发依据 | `RequestControlledRecreate()`（置判别位，§8）⇒ `MainForm` 内部薄入口（`src/UI/MainForm_Transparent.cs`，**非 partial**，计划 §4.3 `:170`）⇒ `RecreateHandle()`（**非反射**）⇒ `Phase=RecreatingHandle` | **不得**用反射调 `RecreateHandle`（`Control.RecreateHandle` 是 `protected`）；不得在 `OnPaint` / `OnHandleDestroyed` / GPU 帧栈内递归执行；不得用 `newHwnd != oldHwnd` 作为唯一重建断言（计划 §4.1 `:129`） | 失败 ⇒ `gpu_presentation_handle_recreate_failed` ⇒ `Phase=Failed` + 保留可控重试入口（计划 §4.3 `:179`）；**必须清除判别位计数**（§8.3） |
| **5 原生生命周期 + 代次推进** | WinForms：`MainForm.OnHandleDestroyed`（`:410-420`）/ `HandleCreated`（`:266-274`） | `HandleDestroyed` ⇒ 既有停止（`:417-418`）+ **通知协调器"旧代次已销毁"** ⇒ 旧代次清理**同步终结**（计划 §4.5 `:200`）⇒ `HandleCreated` ⇒ `TryConsumeControlledRecreate()` 判别 ⇒ `WindowHandleGeneration.Advance()`（唯一递增点）⇒ 句柄代次对 live 捕获链与 GPU 清理链**同时**可见 | **构造期首次创建不得拉起 GPU**（计划 §4.2 `:147`，既有设计必须保持）；过渡期间 GPU 启动次数必须为零（计划 §4.3 `:172`）；不得在此步改 ex-style | 代次推进失败 ⇒ `gpu_transition_generation_advance_failed` ⇒ `Phase=Failed` |
| **6 恢复大小/DPI + 重算布局** | 协调器（步 5 之后） | 按快照 + 当前 `Screen.FromRectangle` 重新求几何 ⇒ 设 `Bounds`/`ClientSize` ⇒ 走既有 `ApplyTheme`（`MainForm_Transparent.cs:426`）⇒ 重建画笔缓存 | **不得**把旧显示器的已缩放像素布局直接当新布局使用（计划 §4.3 `:174`）；不得触发递归 `ApplyTheme` | 失败 ⇒ `gpu_transition_layout_restore_failed` ⇒ `Phase=Failed`，保留快照供重试 |
| **7 恢复透明度/穿透/置顶/可见性** | 协调器 | `RefreshTopMost(forceReinsert:true)`（既有 `MainForm_Transparent.cs:268`）/ `SetClickThrough`（既有 `src/UI/Helpers/MainFormWinHelper.cs:140-151`）/ `Form.Opacity` ⇒ 隐藏窗口保持隐藏 | **不得 `Show()`** 隐藏窗口；**不得抢焦点**；**不得重复启动淡入**（计划 §4.3 `:175`）。**D4 约束**：重建后新 HWND 的 `WS_EX_LAYERED` 由 `CreateParams`（`MainForm_Transparent.cs:48-52`）按配置决定 ⇒ 旧残留**不继承**；期望状态 = `Opacity<1 \|\| ClickThrough` | 失败 ⇒ `gpu_transition_window_state_restore_failed` ⇒ `Phase=Failed` |
| **8 一次有效重绘 + 轮廓** | 协调器 | `ApplyWindowAppearance()`（既有订阅 `MainForm_Transparent.cs:277`）⇒ 一次有效 `Invalidate()` ⇒ 可见窗口在**同一次 UI 处理流程或下一次专用 UI 调度**内完成 legacy 首帧 | **不得**依赖普通采样刷新周期（计划 §4.3 `:176`）；不得在 GPU 帧栈内执行 | 首帧超时 ⇒ `gpu_transition_legacy_first_frame_timeout` ⇒ `Phase=Failed`（**仅可见窗口**） |
| **9 发布完成状态** | 协调器 | `Phase=Stable`；`EffectiveRoute=Legacy`；`FirstFrameOutcome` 落值；隐藏窗口 ⇒ `PendingFirstFrame=true` + 报告"已切换、等待显示" | 不得在关闭过程中发布"已恢复" | 无失败态；**禁止**在未拿到 legacy 首帧证据时显示"已成功恢复"（计划 §4.3 `:177-179`） |

**【裁决】步 3 的幂等与顺序要求**：`StopCore()` 已有"失败后立即再试一次"（`GpuPresentationHostController.cs:707-708`）。**该内联重试保留**，它属于清理预算的第 1、2 次尝试；第 3 次起移交 ledger 的退避重试（§6.4）。

---

## 5. 错误码表

**命名沿用既有习惯**：`gpu_*` / `presentation_*` / `rendering_*` 小写下划线（既有样例：`gpu_capture_policy_restore_failed`、`gpu_presentation_recovery_exhausted`、`presentation_capture_policy_failed`）。

### 5.1 既有码（不改名，只补语义）

| 原因码 | 既有位置 | 可重试 | 上限 | 退避 | 呈现到哪个诊断字段 |
| --- | --- | --- | --- | --- | --- |
| `gpu_capture_policy_restore_failed` | `GpuPresentationHostController.cs:704` | 是 | 见 §6.4 清理预算 | 250/500/1000/2000 ms | `CleanupErrorCode`（**不是** `FailureCode`） |
| `gpu_presentation_capture_policy_restore_failed` | `GpuPresentationHostController.cs:233` | 是 | 同上 | 同上 | `CleanupErrorCode` |
| `gpu_presentation_recovery_exhausted` | `GpuPresentationHostController.cs:137` | 否（须用户显式重试） | 1 次自动（`:142`） | — | `FailureCode` |
| `gpu_presentation_render_failed` | `GpuPresentationHostController.cs:582` | 否（走统一 fallback） | — | — | `FailureCode` |
| `gpu_presentation_start_failed` | `GpuPresentationHostController.cs:265` | 有限 | 1（受 `_recoveryPending`/`_recoveryAttempted` 约束 `:133-144`） | — | `FailureCode` |
| `gpu_presentation_handle_not_ready` | `GpuPresentationHostController.cs:148` | 是（事件驱动） | 无上限，但只在句柄就绪时重试 | 无（事件驱动） | `FailureCode` |
| `gpu_presentation_resize_failed` | `GpuPresentationHostController.cs:534` | 有限 | 随宿主重建 | — | `FailureCode` |
| `gpu_startup_guard_previous_attempt_unclean` | `GpuStartupGuard.cs:55`/`:68` | 否（须 `AllowExplicitManualRetry` `:178-220`） | 1 | — | `FailureCode`（附 `Trigger=StartupGuardConservative`） |
| `presentation_capture_policy_failed` | `PresentationSession.cs:76` | 是 | 见 §6.4 | 同上 | `CleanupErrorCode` |
| `presentation_capture_policy_read_failed` | `PresentationSession.cs:49` | 是 | 同上 | 同上 | `FailureCode` |
| `presentation_window_invalid` | `PresentationSession.cs:41` | 否（HWND 已失效） | — | — | `CleanupWarningCode` |

### 5.2 新增码（【裁决】）

| 原因码 | 触发点 | 可重试 | 上限 | 退避 | 呈现字段 |
| --- | --- | --- | --- | --- | --- |
| `gpu_capture_policy_restore_retry_exhausted` | 清理预算耗尽（§6.4） | 否（须用户显式重试） | 见 §6.4 | — | `CleanupErrorCode` |
| `gpu_capture_policy_cleanup_superseded` | 旧代次被新代次取代（§6.3） | 不适用（终态） | — | — | `CleanupWarningCode`（**信息级，不是错误**） |
| `gpu_capture_policy_unsupported_on_layered_window` | 窗口处于 `WS_EX_LAYERED` ⇒ 不申请 WDA（D4） | 不适用（环境约束） | — | — | `FailureCode` + `Trigger` |
| `gpu_presentation_handle_recreate_failed` | 步 4 重建失败 | 是（受控重试入口） | 由协调器预算控制 | 由协调器控制 | `FailureCode` |
| `gpu_presentation_handle_recreate_not_observed` | 判别位超时未消费（§8.3） | 不适用 | — | — | `CleanupWarningCode` |
| `gpu_presentation_handle_recreate_stale_flag` | 判别位残留被清理（§8.3） | 不适用 | — | — | `CleanupWarningCode` |
| `gpu_transition_decide_failed` | 步 1 `Decide` 抛异常 | 是 | — | — | `FailureCode` |
| `gpu_transition_window_state_snapshot_failed` | 步 2 | 是 | — | — | `FailureCode` |
| `gpu_transition_generation_advance_failed` | 步 5 | 否 | — | — | `FailureCode` |
| `gpu_transition_layout_restore_failed` | 步 6 | 是 | — | — | `FailureCode` |
| `gpu_transition_window_state_restore_failed` | 步 7 | 是 | — | — | `FailureCode` |
| `gpu_transition_legacy_first_frame_timeout` | 步 8 | 是（用户显式重试） | — | — | `FailureCode` |
| `gpu_transition_request_superseded` | 旧请求被新请求合并（U05） | 不适用 | — | — | `CleanupWarningCode`（信息级） |
| `gpu_startup_guard_pending_preserved` | 首帧未成功且停止流程不干净 ⇒ pending 保留（D6） | 不适用 | — | — | `CleanupWarningCode` |
| `gpu_fallback_style_invalid` | fallback 字段非法（§9.5） | 不适用 | — | — | `FailureCode`（**不进** `Validate()` 错误列表） |

### 5.3 呈现规则（【裁决】）

1. **`Validate()` 的错误列表与诊断字段严格分离**：`ThemeRenderingConfig.Validate()`（`src/Rendering/Configuration/ThemeRenderingConfig.cs:60-144`）的返回值**只**用于"整份主题是否可用"；任何**非致命**问题（非法 fallback 样式值、layered 限制、清理告警）**不得**进入该列表。
   `【事实】` 理由：`RenderingRoutePolicy.Decide` 在 `errors.Count > 0` 时返回 `Legacy("rendering_configuration_invalid")`（`RenderingRoutePolicy.cs:42-52`）⇒ 一条非致命诊断会让整个 GPU 路由失效。
2. **三个诊断字段互不覆盖**：`FailureCode`（呈现根因）/ `CleanupErrorCode`（清理根因）/ `CleanupWarningCode`（告警与信息）。写 `CleanupErrorCode` **不得**清空 `FailureCode`（计划 §4.5 `:203`"有效后端状态与清理告警同时可见，不覆盖故障根因"）。
3. **用户可见文案不属于本文件**（T08 / owner F）。本文件只冻结**码值**与**字段归属**。

---

## 6. 清理责任转交规则（GPU-001 的解法）

### 6.1 问题复述（【事实】）

现实现用一个引用（`UIController._gpuPresentation`）同时表达两件事：宿主资源是否活着、清理责任是否还在。§1.1 已逐行给出链条。

### 6.2 所有权形状（【裁决】D2）

```
UI 生命周期所有者（UIController / 协调器，owner B）
│   持有：_gpuPresentation?                      ← 只表示"宿主是否活着"
│   持有：CapturePolicyCleanupLedger             ← 独立持有全部未完成的清理责任
│            └─ Entries: IReadOnlyList<WindowCapturePolicyCleanup>
│
└── GpuPresentationHostController（owner C）
      持有：_renderer / _presentation / _startupAttempt
      职责：停止时若 affinity 恢复失败 ⇒ 【移交】一份 WindowCapturePolicyCleanup 给 ledger
            并【释放】自己（_presentation = null），不得用"保留 _presentation"表达清理责任
```

**不变量（可测）**：

| # | 不变量 | 校验点 |
| --- | --- | --- |
| **INV-1** | `_gpuPresentation is not null` ⇔ 宿主资源活着。**清理责任永不通过它表达** | `UIController.StopGpuPresentation()`（`:360-369`）改写后恒等成立 |
| **INV-2** | `StopGpuPresentation()` 之后 `_gpuPresentation == null` **无条件成立**（不再有 `:363-367` 的早退 return） | 同上 |
| **INV-3** | 每次 affinity 恢复失败 **必定** 产生一条 ledger entry（或在宿主释放前已完成 ⇒ 无 entry） | `GpuPresentationHostController.StopCore()`（`:694-724`）改写后 |
| **INV-4** | ledger entry 数 ≤ 1 × 当前未终结的清理责任；同一 HWND（同一代次）**至多一条** `Pending` | ledger `Add` 时按 `Generation` 去重 |
| **INV-5** | 关闭窗体后 ledger 全部终结（`Completed` 或 `Failed`+`form_closed`），**幂等** | `UIController.Dispose()`（`:1313-1319` 既有清理链） |
| **INV-6** | 新代次取得 WDA 所有权前，**必须**检查 ledger：同一 HWND（同一代次）有未完成清理 ⇒ **拒绝**取得所有权 | `GpuPresentationHostController.Start` 之前（`:146` 附近） |

### 6.3 状态图（【裁决】）

```
        (宿主停止，affinity 恢复成功)
  Running ─────────────────────────────────────────► [无清理责任]
     │                                                     │
     │ 宿主停止，affinity 恢复失败                          │
     ▼                                                     ▼
  [Pending] ◄── 移交 ─── 宿主释放(_presentation=null)   (新代次可独立取得所有权)
     │  ▲
     │  │ 重试成功
     │  └──────────────
     │
     ├── 重试预算耗尽 ──► [Failed]（终态，terminalReason=exhausted）
     │
     ├── 收到"旧代次已销毁"通知 ──► [Completed]（terminalReason=window_destroyed）
     │
     ├── 当前代次已被新代次取代 ──► [Completed]（terminalReason=superseded）
     │
     └── 窗体关闭 ──► [Completed] 或 [Failed]（terminalReason=form_closed），幂等
```

**终态不可回退**：`Completed` / `Failed` 之后不再重试，不再触碰 HWND。

### 6.4 重试预算与退避（【裁决】）

| 项 | 值 | 依据 |
| --- | --- | --- |
| 尝试 1 | 宿主停止时的首次 `CompleteStop` | 既有 `GpuPresentationHostController.cs:702` |
| 尝试 2 | 宿主停止时的内联重试 | 既有 `:707-708`（"retry once while the HWND still exists"） |
| 尝试 3–6 | ledger 退避重试，间隔 **250 / 500 / 1000 / 2000 ms** | 计划 §4.5 `:199`"允许有限、事件驱动或带退避的重试，禁止每帧重试" |
| 总上限 | **6 次** | 计划 §4.5 `:199`"有限"；具体数值是【裁决】 |
| 耗尽后 | `State=Failed`，`terminalReason=exhausted`，`LastErrorCode=gpu_capture_policy_restore_retry_exhausted`；**不阻塞**新代次（INV-2） | D2 |
| 驱动源 | 既有 UI Tick（`UIController.Tick()`，`:627`），**仅当 `Now >= NextAttemptAt`** 才尝试 ⇒ 天然满足"禁止每帧重试" | 计划 §4.5 `:199` |
| 用户显式重试 | 立即执行一次并**重置** `NextAttemptAt`（不清除已记录的 `Attempts`） | 计划 §4.4 `:188`"用户显式重试…记录次数和原因" |
| 关闭时 | 最多再尝试 1 次；仍失败 ⇒ 终态 `Failed`+`form_closed`，**幂等**（重复 Dispose 安全，计划 §4.5 `:203`） | 计划 §4.7 U10 |

### 6.5 与计划 §4.5 三条款的对应（【裁决】）

| §4.5 条款 | 本合同的落实 |
| --- | --- |
| `:196` "清理对象至少持有：旧 HWND、所属代次、原始 affinity、是否由本程序修改、尝试次数、失败码及最后错误" | `WindowCapturePolicyCleanup` 的字段**逐项对应**（`WindowHandle` / `Generation` / `OriginalAffinity` / `AffinityChangedBySession` / `Attempts` / `LastErrorCode` / `LastErrorMessage`） |
| `:196` "宿主释放后由 UI 生命周期所有者继续持有它" | ledger 由 UI 生命周期所有者（B）持有；宿主（C）只负责**移交** |
| `:198` "恢复的是原始策略，不能固定写成 `WDA_NONE`" | `OriginalAffinity` 为**精确原值**；`PresentationSession.RestoreOriginalAffinityCore`（`:393-422`）写回 `_originalAffinity`（`:408-412`）而非常量 |
| `:199` "恢复失败后允许有限、事件驱动或带退避的重试，禁止每帧重试" | §6.4 |
| `:200` "明确收到旧代次销毁通知后…同步结束该代次清理。不要稍后仅凭旧 IntPtr 仍有效就操作它" | 步 5：`HandleDestroyed` ⇒ 通知 ⇒ 旧代次清理**同步终结**；**不**因 `IsWindow` 为真就继续操作 |
| `:201` "不能因旧 HWND 清理任务 Pending，就对新 HWND 直接沿用'已排除'的状态" | 新代次**必须**在新 HWND 上重新 `TryGetDisplayAffinity`（既有 `PresentationSession.ApplyCapturePolicyCore` `:356-369` 的 `!_hasOriginalAffinity` 分支）⇒ 绝不沿用旧值 |
| `:202` "同一 HWND 上清理未完成时禁止叠加新的 affinity 所有者；新代次必须独立确认原始策略并取得所有权" | INV-6：同代次有未完成清理 ⇒ 拒绝新所有权；不同代次（不同 HWND）⇒ 新代次独立读原值、独立取得所有权 |
| `:203` "有效后端状态与清理告警同时可见，不覆盖故障根因；关闭时清理必须幂等" | §5.3 规则 2 + INV-5 |
| `:204` "GPU legacy 回退时 GPU 捕获会话归零…整体仍不得出现两个捕获所有者" | 步 2 停止 live 捕获（`Suspend()` `:308`）+ 步 3 释放 renderer ⇒ GPU 捕获会话归零；旧捕获（live/backdrop）的所有权由 `LiveGlassController` 单独持有，转换期不得与 GPU live 重叠（U14） |

---

## 7. 句柄代次裁决

### 7.1 二选一裁决（【裁决】D1）

**选择：合并为同一真相源。**

**理由**：
1. `【事实】` T00 §4 已确认：全仓 `handleGeneration|HandleGeneration` 仅命中 `LiveGlassController` + `src/System/Backdrop/**` 的**一条链**，**GPU 侧无并行计数器** ⇒ "合并"是改动量最小、且**不新增口径**的路径。
2. GPU-004 要求"单调代次必须贯穿到 WDA 清理路径，不只是 live 捕获链" ⇒ 若两套代次并存，就必须额外引入跨套配对机制；合并方案下**清理对象直接持有同一个代次值**，判据退化为一次整数比较。
3. GPU-001 的"旧清理不得命中新窗口"需要**一个**可信的代次源；两个源意味着"GPU 认为旧、live 认为新"的不一致状态是可能的。

### 7.2 真相源形状（【裁决】）

- 新类型 `WindowHandleGeneration`（owner A 新增 `src/Rendering/Configuration/WindowHandleGeneration.cs`），`Current` 初始 `1`，`Advance()` 是**唯一递增点**。
- **唯一驱动者**：协调器（owner B）在 `HandleCreated` 时调用 `Advance()`。
- `【事实】` 既有 `LiveGlassController._handleGeneration`（`src/UI/LiveGlassController.cs:87`，`private long`，递增点 `:297`）**被移除**；`OnHandleChanged()`（`:295-300`）改为**只读** `WindowHandleGeneration.Current` 并继续执行 `RefreshRequest()` + `ReleaseHeldFrame()`。
- `【事实】` 既有消费点保持不变：`BackdropServiceRequest.HandleGeneration`（`src/System/Backdrop/BackdropServiceRequest.cs:121`）、等价性比较（`:145`）、`SaturateToInt`（`src/System/Backdrop/BackdropCaptureAdapters.cs:274`）、`ScreenCaptureFrameSource.HandleGeneration`（`src/System/Backdrop/ScreenCaptureFrameSource.cs:86`）、旧帧丢弃（`:185`）—— **一行都不改**，因为类型仍是 `long` 且语义仍是"句柄 epoch"。
- **递增顺序**：`src/UI/MainForm_Transparent.cs:266` 的 `HandleCreated` 处理器**第一行**必须先 `Advance()`，再调用既有的 `NotifyLiveGlassHandleChanged()`（`:272`）与 `NotifyGpuHandleChanged()`（`:273`）⇒ 保证两侧看到同一个新代次。
- **不受影响**：`_sizeGeneration`（`src/UI/LiveGlassController.cs:88`，递增点 `:305`/`:313`）是尺寸代次，**与句柄代次无关**，保持独立（T00 §4 已确认这是"并列的第二条代次"，计划 §4.2 只约束句柄代次）。

### 7.3 `PresentationSession.RecreateWindow(...)` 裁决（【裁决】D3）

**裁决：正式采用，不废弃。**

**理由**：`【事实】` 现有实现 `RecreateWindow`（`src/Rendering/Presentation/PresentationSession.cs:176-214`）的顺序已经是"先 `RestoreOriginalAffinityCore`（`:193`）→ 再 `ApplyCapturePolicyCore(newHandle)`（`:199`）"，而 `RestoreOriginalAffinityCore` 的成功路径（`:417-420`）会清空 `_hasOriginalAffinity` / `_affinityChangedBySession` / `_originalAffinity` ⇒ `ApplyCapturePolicyCore` 的 `!_hasOriginalAffinity` 分支（`:356-369`）**会在新句柄上重新读取原值**。
`【事实】` 这正是 GPU-002 要求的"先恢复旧句柄、再在新句柄重读原值" ⇒ **现有实现已满足该顺序**，缺的是**代次记账**与**失败时的责任归属**。

**必须补齐的 6 项语义（owner C 在 `PresentationSession.cs` 实现）**：

| # | 补齐项 | 具体要求 | 依据 |
| --- | --- | --- | --- |
| **R1** | **代次单调校验** | 新增参数或从 `RenderGenerations.Window` 读取句柄代次；若新代次 ≤ 当前代次 ⇒ 拒绝，`errorCode="presentation_session_generation_not_advanced"` | 计划 §4.1 `:129`（不得以 `newHwnd != oldHwnd` 断言重建） |
| **R2** | **恢复失败不得改句柄** | `RestoreOriginalAffinityCore` 失败 ⇒ `_windowHandle` 与 `_generations` **保持旧值**，返回失败并**移交清理责任**（§6） | GPU-002 |
| **R3** | **新句柄必须重读原值** | 复用现有 `:356-369` 分支，但**必须有断言**证明 `_originalAffinity` 来自**新**句柄；禁止任何路径把旧代次的 `_originalAffinity` 带到新句柄 | GPU-002 |
| **R4** | **同 HWND 清理未完成 ⇒ 拒绝** | 调用方（协调器）必须先查 ledger：同一代次有 `Pending` 清理 ⇒ 不得进入 `RecreateWindow` | §4.5 `:202`；INV-6 |
| **R5** | **代次可观测** | `PresentationSession` 需暴露当前句柄代次，供 `Diagnostics()` 与清理记录对齐 | §4.2 身份维度 |
| **R6** | **失败态可区分** | `RecreateWindow` 失败后 `_state` 置 `Fallback`（既有 `:195`/`:205`），但**必须**新增可区分的错误码（`presentation_capture_policy_restore_failed` vs `presentation_session_not_recreatable`），以便 ledger 区分"清理失败"与"状态不允许" | §5 |

### 7.4 跨代次丢弃判据（【裁决】D5，GPU-004）

**这是本合同的"可校验"判据**，替代 `PresentationSession.cs:39` / `:65` 的单一 `IsWindow` 判据：

```
判定顺序（不可交换）：
  1) cleanup.Generation <  WindowHandleGeneration.Current
       ⇒ 【禁止】对该 cleanup.WindowHandle 执行任何 Win32 调用
         （包括 IsWindow / SetWindowDisplayAffinity / GetWindowLong）
       ⇒ 直接终结：State=Completed, terminalReason="superseded_by_new_generation"
       ⇒ 记 CleanupWarningCode = "gpu_capture_policy_cleanup_superseded"
  2) cleanup.Generation == Current
       ⇒ 此时才允许用 IsWindow 判活：
         - IsWindow == false ⇒ State=Completed, terminalReason="window_destroyed"
         - IsWindow == true  ⇒ 仍属当前代次 ⇒ 允许按 §6.4 重试
```

**为什么必须先比代次**：`【事实】` `IsWindow` 只回答"这个数值当前是否是一个窗口"，**无法区分"旧 HWND 已销毁"与"句柄数值被复用"**（T00 GPU-004）。计划 §4.5 `:200` 明确禁止"仅凭旧 IntPtr 仍有效就操作它"。⇒ 代次是**先验**判据，`IsWindow` 只是**同代次内**的补充判据。

**对既有回调链的丢弃判据（U05/U14）**：三重丢弃，缺一不可：

| 判据 | 丢弃什么 |
| --- | --- |
| `callback.TransitionId != state.TransitionId` | 旧切换请求产生的任何回调 |
| `callback.HandleGeneration != state.HandleGeneration` | 旧 HWND 代次的捕获/场景回调 |
| `callback.HostGeneration != state.HostGeneration` | 旧宿主会话的回调（同一 HWND 上的重启） |

`【事实】` 既有 live 捕获链的同源判据：`BackdropFrameStamp.Matches`（`src/System/Backdrop/ScreenCaptureFrameSource.cs:185`）比较 `HandleGeneration` —— 与本判据同源，**不新增第二套**。

---

## 8. `HandleCreated` 判别位规格

### 8.1 载体（【裁决】D9）

| 项 | 规定 |
| --- | --- |
| **字段名** | `internal int PendingControlledRecreateCount`（owner B 的**新增切换协调器**类，独立类）<br>`internal long PendingControlledRecreateRequestId`（配对与诊断用） |
| **类型** | `int`（计数）+ `long`（请求号）。**不用 bool**：bool 无法表达"置位但重建未发生"的残留 |
| **生命周期** | 与**单次受控重建请求**同生命周期；不跨窗体生命周期（协调器随窗体创建/释放） |
| **置位者** | `MainForm` 的**内部受控重建薄入口**（`src/UI/MainForm_Transparent.cs` 内，**非 partial** —— 计划 §4.3 `:170` 明确 `MainForm` 非 partial，全仓唯一声明在 `:14`）在调用 `RecreateHandle()` **之前**调用 `RequestControlledRecreate(reason)` |
| **消费者** | `src/UI/MainForm_Transparent.cs:266-274` 的 `HandleCreated` 处理器，通过 `TryConsumeControlledRecreate(out long requestId)` 消费 |
| **清除规则** | ① 正常消费 ⇒ 计数 `--`；② `RecreateHandle()` 抛异常 ⇒ 立即 `CancelControlledRecreate(requestId)` 归零；③ **陈旧兜底**：下一次 `RequestControlledRecreate()` 之前若发现残留计数 > 0 ⇒ 先归零并记 `gpu_presentation_handle_recreate_stale_flag`；④ **超时兜底**：请求后 5 秒内未被消费 ⇒ 协调器归零并记 `gpu_presentation_handle_recreate_not_observed`（5 秒是【裁决】） |

### 8.2 消费规则（【裁决】）

```
HandleCreated 处理器（src/UI/MainForm_Transparent.cs:266）
  1. Advance() 句柄代次（D1：唯一递增点）
  2. bool controlled = TryConsumeControlledRecreate(out requestId)
  3. controlled == false ⇒ 这是"构造期首次创建"或"非受控创建"
       ⇒ 【禁止】拉起 GPU（计划 §4.2 :147；既有设计必须保持）
       ⇒ 仍然必须：RefreshTopMost / OnHandleCreated（既有 :268/:270）
                   NotifyLiveGlassHandleChanged（既有 :272，代次推进后）
  4. controlled == true ⇒ 这是"受控重建后创建"
       ⇒ 进入 GPU 重建路径（计划 §4.2 :148）
       ⇒ 但仍受 §4.4 约束：只有在【期望路由为 GPU】且【过渡阶段允许启动】时才拉起
```

### 8.3 为什么必须是"判别位"而不是"代次奇偶"（【裁决】理由）

`【事实】` 构造期首次创建（代次 1→2）与受控重建（代次 N→N+1）在事件形态上**完全相同**（都是 `HandleDestroyed` → `HandleCreated`）。⇒ 计划 §4.2 `:149` 的判断成立："无判别位则两类来源无法区分，会同时破坏第 1 条（构造期被误拉起 GPU）与 §4.4 的'不得无条件拉起 GPU'"。

### 8.4 与 §4.4 的关系（【裁决】）

`【事实】` 计划 §4.4 `:188`："故障回退后，不允许普通 Tick 或 HandleCreated 无条件把 GPU 拉起来。用户显式重试或既有合同允许的有限恢复才可重试，记录次数和原因。"

⇒ **裁决**：`HandleCreated` 的 GPU 重建路径还需**同时**满足两个条件才可拉起 GPU：
1. 判别位命中（`controlled == true`）；
2. `ExpectedRoute.IsGpu == true` **且** `Phase == Stable` **且** 无未终结的 `gpu_presentation_recovery_exhausted` 记录。

⇒ 既有 Tick 兜底（`src/UI/UIController.cs:634-638`）**必须**加同样的守卫：`Phase == Stable` 且 `ExpectedRoute.IsGpu`（而不是既有的 `_gpuRoute?.IsGpu == true`）。**注意**：这不改变 `:951` 的口径（D8），只收紧**启动**条件。

---

## 9. fallback 字段规则

### 9.1 既有硬依赖（【事实】）

| # | 硬依赖 | 位置 | 含义 |
| --- | --- | --- | --- |
| HD-1 | `Validate()` 强制 `rendering.fallback` 为 `legacy`/`legacy` | `src/Rendering/Configuration/ThemeRenderingConfig.cs:136-141` | 新字段**不得**削弱这条；`backend`/`material` 规则**一字不改** |
| HD-2 | 克隆路径对 `Fallback` 走字典拷贝 | `ThemeRenderingConfig.cs:51-57`（`Fallback` 拷贝在 `:57`） | 新字段若以 `JsonElement` 承载 ⇒ 克隆后访问可能抛 `ObjectDisposedException` ⇒ **必须用纯 CLR 类型** |
| HD-3 | 未知字段保留 / 裁剪 | `src/Core/ThemeManager.cs:563` `PruneNode(theme.Rendering, typeof(ThemeRenderingConfig))` | 新字段必须被 `ThemeRenderingConfig` 声明，否则保存时被裁掉 |

**【事实】HD-2 的机制更正（供主审复核）**：计划 §4.6 `:213` 把风险归因于 `ThemeRenderingConfig.ToContract()` 的 `CopyDictionary` 是"字典浅拷贝"。**实测不准确**：`CopyDictionary`（`ThemeRenderingConfig.cs:48-49`）对每个值调用 `JsonElement.Clone()` ⇒ **是深脱离，安全**。
真正的浅拷贝在**编辑器克隆路径**：`src/ThemeEditor/AppearanceFields.cs:292-318` 的 `CloneValue` 在 `:297` 判定 `t.IsValueType` ⇒ `JsonElement`（struct）**原值返回、不脱离**；`:320-326` 的 `CloneDictionary` 同样按值复制。而 `CloneRendering`（`:256-260`）走 `CloneObject` → `CopyInto` → `CloneValue` ⇒ **命中这条路径**。
`【事实】` 当前**尚未触发** `ObjectDisposedException`：主题加载走 `JsonSerializer.Deserialize<Theme>(json, …)`（`src/Core/ThemeManager.cs:496`）⇒ STJ 对 `Dictionary<string, JsonElement>` 的元素已做脱离克隆 ⇒ 风险是**潜伏的**，不是现存的。
⇒ **结论不变但理由更准**：HD-2 要求的"纯 CLR 类型"必须遵守；**新字段用纯 CLR 类型后，克隆路径彻底无风险**（`CloneValue` 对 `string`/`int`/`bool` 直接按值复制，`:297`）。

### 9.2 字段载体（【裁决】D10）

| 项 | 规定 |
| --- | --- |
| **新增属性** | `public ThemeFallbackStyle? FallbackStyle { get; set; }` on `ThemeRenderingConfig` |
| **JSON 名** | `fallbackStyle` ⇒ 线上形状为 `rendering.fallbackStyle` |
| **类型位置** | **同一个文件** `src/Rendering/Configuration/ThemeRenderingConfig.cs`（owner D 独占）⇒ **不新增** `src/Rendering/Configuration/` 下的新文件（避免与 owner A 的新增文件在同目录撞所有权，§6 `:278` 已警示同目录改动需留意） |
| **序列化缺省** | `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` ⇒ 旧主题保存时**不写出**新键 ⇒ 旧主题像素输出与保存结果不变 |
| **`rendering.fallback` 字典** | **完全不动**：类型仍是 `Dictionary<string, JsonElement>`，`backend`/`material` 语义与 `Validate()` 规则不变（HD-1） |
| **`RenderingConfig` 运行时契约** | **不扩展**。`src/Rendering/Contracts/RenderContracts.cs:493-499` 的 `RenderingConfig.Fallback` 保持不变（该文件**未被计划 §6 归属任何 owner**）⇒ fallback resolver 直接读 `ThemeRenderingConfig` |

**为什么是兄弟块而不是塞进 `rendering.fallback`（【裁决】理由）**：`rendering.fallback` 是 `Dictionary<string, JsonElement>`（`ThemeRenderingConfig.cs:34`）⇒ 放进去的新字段必然是 `JsonElement` ⇒ **直接违反 HD-2**。两个约束不可同时按字面满足 ⇒ 选**加法式**兄弟块：既满足 HD-2（纯 CLR），又满足 §4.6 的**意图**（"避免建立主题文件名映射表"，样式随主题走），且**零破坏性**（既有 `Fallback` 类型与全部消费点不动）。
**这是对计划 §4.6 `:209`"优先扩展已有 `rendering.fallback` 的可选样式字段"的**显式偏离**，已列入第 12 节的待主审确认项。

### 9.3 字段清单（【裁决】；**取值/配色属 T05，本文件不定**）

`ThemeFallbackStyle`（纯 CLR，全部可空；`null` = 未声明）：

| 字段名 | JSON 名 | 类型 | 缺省值 | 语义（对应 §4.6 `:216`） |
| --- | --- | --- | --- | --- |
| `Background` | `background` | `string?` | `null` | 背景色（页面底色） |
| `PanelBackground` | `panelBackground` | `string?` | `null` | 分组底色 |
| `Border` | `border` | `string?` | `null` | 边框色 |
| `Text` | `text` | `string?` | `null` | 主前景色 |
| `MutedText` | `mutedText` | `string?` | `null` | 次要前景色 |
| `Accent` | `accent` | `string?` | `null` | 强调色（告警语义之外的装饰强调） |

- **颜色格式**：`string`，沿用 `ThemeManager.ParseColor`（`src/Core/ThemeManager.cs:596`）可解析的既有色值字符串格式（**T05 确认具体格式**，本文件只锁定"是 string 且走既有解析器"）。
- **未知字段保留**：`ThemeFallbackStyle` 声明 `[JsonExtensionData] public Dictionary<string, JsonElement> ExtensionData { get; set; }`（`System.Text.Json.Serialization` 已在 `ThemeRenderingConfig.cs:2` 导入）⇒ `rendering.fallbackStyle` 内的未知键在保存后保留。
- **`rendering.fallback` 内的未知键**：既有字典**原样保留**（`ToContract()` 的 `CopyDictionary` 复制全部条目，`ThemeRenderingConfig.cs:57`）⇒ 无需额外处理。
- **纯 CLR 硬约束**：`ThemeFallbackStyle` 的 6 个业务字段**不得**改用 `JsonElement`（`ExtensionData` 是唯一的 `JsonElement` 容器，且它只装未知键）。

### 9.4 三个硬依赖的满足方式（逐条）

| 硬依赖 | 满足方式 | 落点 |
| --- | --- | --- |
| **HD-1** `Validate()` 接受新字段且仍拒绝非法 `backend`/`material` | ① `ThemeRenderingConfig.cs:136-141` **一字不改**；② 新增字段**永不**进入 `Validate()` 的错误列表（§5.3 规则 1）⇒ `Validate()` 对新字段"接受"是自动成立的 | owner D |
| **HD-2** 克隆后访问新字段不抛 `ObjectDisposedException` | 新字段是 `string?` ⇒ `AppearanceFields.CloneValue`（`:297`）按值复制 ⇒ 无 `JsonElement` 脱离问题。`ThemeFallbackStyle` 本身是 class ⇒ `CloneValue` 走 `:315` `CloneObject` ⇒ 逐属性复制 ✓ | owner D |
| **HD-3** 未知字段保留 / 不被裁掉 | ① 6 个字段声明在 `ThemeRenderingConfig` 上 ⇒ `PruneNode`（`ThemeManager.cs:563`）不会裁掉（它只裁 `ExtensionData` 中与已知属性名冲突的键，`:574-584`）；② `ThemeFallbackStyle.ExtensionData` 承接块内未知键 | owner D |

### 9.5 非法值与缺省处置（【裁决】）

| 情形 | 处置 |
| --- | --- |
| 字段缺失（`null`） | 使用**中性可读默认值**（默认值内容由 T05 定）；**不**报错 |
| 字段存在但不是 string（如数字/对象） | **诊断** `gpu_fallback_style_invalid` + **忽略该字段**用默认值；**不**进 `Validate()` 错误列表（否则整份主题被拒，§5.3 规则 1） |
| 字段是 string 但颜色无法解析 | **诊断** `gpu_fallback_style_invalid` + 用默认值；同上 |
| 第三方 GPU 主题完全未声明 `fallbackStyle` | 使用中性可读默认值（计划 §4.6 `:220`） |
| `rendering.fallback.backend` / `material` 非法 | **仍然整份主题报 `rendering.fallback must select legacy/legacy.`**（HD-1，`:140` 不动） |

### 9.6 克隆 / 编辑器往返（【裁决】）

| 路径 | 结论 |
| --- | --- |
| `AppearanceFields.CloneRendering`（`:256-260`） | **自动覆盖**：`CloneObject` → `CopyInto`（`:281-290`）遍历全部 public 可写属性 ⇒ `FallbackStyle` 被复制；`ThemeFallbackStyle` 的 6 个 string 字段按值复制 ✓ |
| `ThemeRenderingConfig.ToContract()`（`:46-58`） | **不涉及** `FallbackStyle`（契约不扩展）⇒ `RenderingConfig` 不变 ⇒ `src/Rendering/Contracts/RenderContracts.cs` 无需改动 ✓ |
| 加载 / 保存 / 另存 / 取消 | `ThemeRenderingConfig` 的 public 属性由 STJ 自动读写 ⇒ 新块参与往返；旧主题因 `[JsonIgnore(WhenWritingNull)]` 不产生新键 ✓ |
| `ThemeManager.PruneNode` | 见 HD-3 ✓ |
| **禁止** | 不得在自动回退时保存/覆盖原 JSON（计划 §4.6 `:219`）；不得修改 `ThemeManager.Current` 的共享已缩放对象 |

---

## 10. GPU-001 ~ GPU-005 逐条处置

| ID | 级别 | 根因（【事实】） | 本合同的处置（【裁决】） | 落点 owner |
| --- | --- | --- | --- | --- |
| **GPU-001** | 🔴 高 | 宿主引用被当作清理责任的代理（§1.1 六行链条） | **D2 所有权解耦**：新增 `WindowCapturePolicyCleanup`（纯记录）+ `CapturePolicyCleanupLedger`（UI 生命周期所有者持有）。宿主恢复失败 ⇒ **移交**清理记录并**释放**自己（`_presentation = null`）；`UIController.StopGpuPresentation()` **无条件** `_gpuPresentation = null`（删除 `:363-367` 的早退）。⇒ 三条重启路径（`:280`/`:312`/`:634`）重新可达，新代次可独立取得所有权（INV-6）。详见 §6 | A（类型）+ B（ledger/持有/接线）+ C（移交） |
| **GPU-002** | 🟡 中 | `_originalAffinity`（`PresentationSession.cs:110`）是进程内缓存，跨代次不可复用 | **D3 正式采用 `RecreateWindow`**，并补齐 R1–R6（§7.3）。关键点：**先恢复旧句柄、再在新句柄重读原值** —— 现有实现顺序已满足（`:193` → `:199` → `:356-369`），需补的是**代次记账**（R1/R5）与**失败归属**（R2/R6） | C（`PresentationSession.cs`）+ B（调用方） |
| **GPU-003** | 🟡 中 | `SetClickThrough(false)`（`MainFormWinHelper.cs:148`）只清 `WS_EX_TRANSPARENT`；全仓无 `WS_EX_LAYERED` 清除点。而 `SetWindowDisplayAffinity` 在 layered 窗口上会失败（外部权威，T00 GPU-003） | **D4**：(a) **期望状态** = `WS_EX_LAYERED` 由 `Opacity<1 \|\| ClickThrough` 决定；重建路径上由 `CreateParams`（`MainForm_Transparent.cs:48-52`）按配置决定 ⇒ **旧残留不继承到新 HWND**。(b) **同 HWND 上不校正**（避免改变既有透明度产品语义），改为**在 GPU 侧读 `GWL_EXSTYLE` 并诊断**，**不申请** WDA 排除 ⇒ 期望路由不变（`GpuLiveBackground`），**有效路由**降为 `GpuFixedInput`，原因码 `wda_capture_policy_unsupported_on_layered_window`。(c) **WDA 与 layered 互斥** ⇒ 同一 HWND 上不得同时存在 layered 与 WDA 所有权 | A（`WindowIsLayered` 分支）+ B（ex-style 读取 / 期望状态）+ C（宿主侧二次校验） |
| **GPU-004** | 🟡 中 | `IsWindow`（`PresentationSession.cs:39`/`:65`）无法区分"旧 HWND 已销毁"与"句柄值被复用" | **D5 代次判据先于 `IsWindow`**（§7.4）：`cleanup.Generation < Current` ⇒ **禁止任何 Win32 调用**、直接终结；仅同代次才用 `IsWindow`。代次来自**合并后**的唯一真相源（D1），因此 GPU 清理路径与 live 捕获链**共用**同一个代次 | A（真相源类型）+ B（ledger 判据）+ C（session 侧） |
| **GPU-005** | 🟡 低 | `GpuStartupAttempt.Dispose()`（`GpuStartupGuard.cs:316-324`）**无论首帧是否成功**都调 `CompleteAttempt` ⇒ 删 pending；而 `StopCore()`（`GpuPresentationHostController.cs:715`）正是 `_startupAttempt?.Dispose()` ⇒ 首帧失败但正常停止时 pending 也会被清除 | **D6 精确定义**（§6.5 之外的专条）：pending ⟺ 存在"已开始但未被显式确认为干净"的启动尝试。**清除只允许两种**：① 首帧成功（`MarkFirstSuccessfulFrame`）；② **受控放弃且停止流程本身干净**（含 affinity 恢复成功）。**首帧失败且停止流程不干净 ⇒ pending 必须保留** ⇒ 下次启动进入 conservative ⇒ 禁用自动 GPU 启动，等待用户显式重试（`AllowExplicitManualRetry`，`:178-220`）。**禁止**任何"仅为了重试而清除标记"的操作 ⇒ 这就是 §4.4 `:190`"不得通过每次 Tick 清标记规避保护"的可操作判据 | A（`GpuStartupGuard.cs`，**需 §6 归属，见 §12.3**）+ C（`StopCore` 的终结时机） |

---

## 11. U01–U14 → 状态/字段/事件 → owner 映射

`【事实】` U01–U14 来自计划 §7.1 `:305-319`。owner 缩写：**A** = 主审/集成（`src/Rendering/Configuration/` 新增 + `RenderingRoutePolicy.cs`）、**B** = 窗口切换（`UIController` / `MainForm_Transparent` / `MainFormWinHelper` / `LiveGlassController` / 新协调器）、**C** = 宿主与清理（`src/Rendering/Presentation/**` / `src/System/Backdrop/**`）、**D** = 回退外观（`ThemeRenderingConfig` / `AppearanceFields` / `ThemeManager` / 主题 JSON）、**E** = 测试与探针（T06/T07）、**F** = 设置与验收（T08/T09）。

| ID | 条件（计划 §7.1） | 映射到哪个状态/字段/事件 | 需要的 owner 文件改动 |
| --- | --- | --- | --- |
| **U01** | 未创建交换链即启动失败 ⇒ 进 legacy，无多余重建与 GPU 重启 | `BindingState` 保持 `Unbound`（不得置 `PresentAttempted`/`Presented`）；`Phase` 不得进入 `RecreatingHandle`；`FirstFrameOutcome=NotAttempted` | 事件：步 1 直接决定 `ExpectedRoute=Legacy` ⇒ 跳过步 4。**A**（状态类型）+ **C**（`TryCreate` 失败路径不得置 `Presented`）+ **B**（协调器跳过重建） |
| **U02** | 已成功 Present 后退出 ⇒ 一次受控代次更新，资源释放 | `BindingState=Presented` ⇒ 命中计划 §4.1 `:123` 重建依据 ⇒ `RecreatingHandle` **恰好一次**；`HostGeneration` 递增；资源账本归零 | 事件：步 3 → 步 4。**B**（重建判定）+ **C**（账本归零） |
| **U03** | 首次 Present 抛错，结果不明确 ⇒ 保守句柄恢复 | `FirstFrameOutcome=Unknown` ⇒ 命中计划 §4.1 `:124` ⇒ **保守重建**（不得因"成功计数为零"跳过） | 事件：步 4 的重建触发依据表。**B** + **C**（首帧结果上报） |
| **U04** | 同一 legacy 请求重复到达 ⇒ 不再次重建，返回相同稳定状态 | `LastAppliedRequestVersion` 不变 且 `EffectiveRoute==Legacy` 且 `Phase==Stable` ⇒ 幂等短路 | 事件：步 1 的幂等判定。**B**（协调器）+ **A**（状态字段） |
| **U05** | 快速开关/换肤/DPI 交错 ⇒ 串行收敛到最新请求；旧回调不改新状态 | 三重丢弃判据（§7.4）：`TransitionId` / `HandleGeneration` / `HostGeneration`；`LastAppliedRequestVersion` 单调 | 事件：步 1 的请求合并。**A**（纯状态）+ **B**（协调器串行化） |
| **U06** | HWND 重建触发 HandleCreated/Tick ⇒ 过渡期 GPU 启动次数为零；构造期首次创建不被误判为重建 | `Phase ∈ {Stopping, RecreatingHandle, Restoring, Starting}` ⇒ 禁止启动；判别位 `TryConsumeControlledRecreate` | 事件：步 5 + Tick 启动守卫（§8.4）。**B** |
| **U06b** | `HandleCreated` 两个来源分别触发 | 判别位 `PendingControlledRecreateCount` + `TryConsumeControlledRecreate`（§8.1/§8.2） | 事件：步 5。**B**（`MainForm_Transparent.cs` + 协调器） |
| **U07** | WDA 连续恢复失败 ⇒ renderer 释放、清理对象保留、错误不丢失、无每帧重试 | `CleanupState=Pending/Failed`；`CleanupErrorCode`；`Attempts` + `NextAttemptAt` 退避；ledger entry 存在 | 事件：步 3 移交 + ledger 退避重试。**A**（记录类型）+ **B**（ledger）+ **C**（移交） |
| **U08** | 原始 affinity 非零 ⇒ 恢复精确原值，不写死 `WDA_NONE` | `WindowCapturePolicyCleanup.OriginalAffinity` 精确值；恢复写回该值 | 事件：步 3。**C**（`PresentationSession.cs:408-412` 保持写 `_originalAffinity`） |
| **U09** | 旧代次销毁且 IntPtr 被复用 ⇒ 旧清理不操作新窗口；新代次独立获取所有权 | 代次判据（§7.4 第 1 条）⇒ 旧清理终结；新代次在**新 HWND** 上 `TryGetDisplayAffinity` 重读原值（`PresentationSession.cs:356-369`） | 事件：步 5 + ledger 判据。**A** + **B** + **C** |
| **U10** | 切换期间关闭、重复 Dispose ⇒ 不重新创建窗口；重复清理安全；资源归零 | 关闭优先（计划 §4.2 `:162`）；清理幂等（INV-5）；`Phase=Failed` 且不重建 | 事件：关闭路径（`UIController.Dispose` `:1313-1319` 既有清理链）。**B** + **C** |
| **U11** | GPU 故障回退 ⇒ 走统一转换；用户开关保留；普通 Tick 不自动起 GPU | `ExpectedRoute` 保持用户选择（计划 §3 `:76`"不把用户选择悄悄写回为关闭"）；`EffectiveRoute=Legacy`；`FailureCode` 保留根因；Tick 启动守卫（§8.4） | 事件：步 1（`Trigger=RuntimeFailure`）。**B** + **C** |
| **U12** | 回退字段缺省、非法、未知、克隆保存取消 | `ThemeFallbackStyle` 纯 CLR 字段（§9.3）；`Validate()` **不**报错（§5.3 规则 1）；`gpu_fallback_style_invalid` 诊断 | **D**（`ThemeRenderingConfig.cs` + `AppearanceFields.cs`）+ **A**（诊断字段） |
| **U13** | 已隐藏或最小化时切换 ⇒ 不 Show、不激活，恢复显示后才完成可见首帧检查 | `PendingFirstFrame=true` + `FirstFrameOutcome=NotAttempted`；步 7 禁止 Show/激活；步 9 报告"已切换、等待显示" | 事件：步 7/8/9。**B** + **F**（显示文案） |
| **U14** | 旧捕获（live/backdrop）与 GPU live 转换 ⇒ 捕获所有者至多一个，旧会话回调不能发布到新代次 | 合并后的 `HandleGeneration` 贯穿捕获请求链（`BackdropServiceRequest.cs:121/:145`、`ScreenCaptureFrameSource.cs:86/:185`）；`HostGeneration` 区分宿主会话；`TransitionId` 丢弃旧回调 | 事件：步 2/5。**B**（`LiveGlassController.cs` 代次消费）+ **C**（`src/System/Backdrop/**` 消费点）+ **A**（状态字段） |

**新增纯测试建议归属（owner E，T06）**：`tests/LiteMonitor.Tests/` 下新增独立文件，覆盖：§7.4 的三重丢弃判据、§6.4 的退避与预算、§4 的阶段跃迁非法性、§9.5 的 fallback 非法值处置、§8.1 的判别位生命周期。**不得**把新文件放进 `LiteMonitor.GpuNonperformance.Tests` 而不加 `Compile`（计划 §7.1 `:321` 警示）。

---

## 12. 与 T01 的交接条目

### 12.1 依赖 T01 最终路线结论的条款

`【事实】` 计划 §4.1 `:114-117` 的三分支判定规则：可安全使用 ⇒ 走主线；不可安全使用 ⇒ **直接走 §9 备用路线**（独立 GPU 子 HWND）并重新复审；不确定 ⇒ 记 `NOT_VERIFIED`，**不得**先落主线再打补丁。
`【事实】` 计划 §5 `:251`：T02 依赖 T00，**最终采用路线需 T01**。

| 合同条款 | 依赖 T01 的哪一部分 | 若 T01 = "可安全使用" | 若 T01 = "不可安全使用"（走 §9 备用路线） | 若 T01 = "不确定" |
| --- | --- | --- | --- | --- |
| **§8 `HandleCreated` 判别位** | 主窗口 HWND 是否会被重建 | 判别位按 §8 规格实现 | **判别位的"重建"分支作废**：主窗口 HWND 永不重建 ⇒ `TryConsumeControlledRecreate` 恒为 false ⇒ U06b 的一半变成 N/A；GPU 宿主改绑**子 HWND** | 不得实现主线分支；只允许实现"不重建"的保守形态 |
| **§4 步 4/5/6/7** | 同上 | 步 4（受控重建）成立；步 5 推进代次；步 6/7 恢复主窗口几何与外观 | 步 4 **删除**（主窗口不重建）；步 5 只做"子 HWND 生命周期"；步 6/7 改为"子 HWND 的尺寸/位置跟随主窗口"（新子 HWND 的尺寸、输入、Region、WDA 顶层策略需重新验证 —— 计划 §9 `:380`） | 同"不确定"列 |
| **§7.3 `RecreateWindow` 采用** | 同上 | 采用，补齐 R1–R6 | **不采用**：主窗口 HWND 不重建 ⇒ 无跨代次复用需求；但 GPU 的 affinity 所有者改为**子 HWND** ⇒ `PresentationSession` 仍需"在新 HWND 上重读原值"的语义（GPU-002 仍成立，只是对象变了） | 暂缓 R1–R6 的实现，保留 GPU-002 的"重读原值"要求 |
| **§6 清理责任转交（GPU-001）** | **不依赖**（与路线无关） | 照常实现 | 照常实现（ledger / 移交 / 代次判据完全不变） | 照常实现 |
| **§7 句柄代次合并（D1）** | **不依赖**（代次是窗口 epoch，与是否重建无关） | 照常实现 | 照常实现（子 HWND 也需要 epoch 记账） | 照常实现 |
| **§9 fallback 字段规则** | **不依赖** | 照常实现 | 照常实现 | 照常实现 |
| **§2 8 维度状态表** | 仅"有效路由"的取值域可能扩展（子 HWND 场景下"GPU Fixed"对应子 HWND） | 照常 | 需加一条：`EffectiveRoute` 的 `GpuFixedInput`/`GpuLiveBackground` 语义指向**子 HWND**；`BindingState` 改为描述子 HWND | 暂缓 |
| **§5 错误码表** | 仅"重建类"码在备用路线下不使用 | 全用 | `gpu_presentation_handle_recreate_failed` / `gpu_presentation_handle_recreate_not_observed` / `gpu_presentation_handle_recreate_stale_flag` / `gpu_transition_generation_advance_failed` **不适用** | 暂缓 |
| **§11 U01–U14** | U02 / U03 / U06 / U06b 依赖"是否重建" | 全用 | **U02、U03、U06b 的"重建"分支改判**（应为 N/A 或改为"子 HWND 重绑"）；**U12/U14 不变** | 暂缓 |

### 12.2 备用路线下**必须新增**的合同条目（若 T01 判为不可安全使用）

1. **子 HWND 的呈现历史**：`BindingState` 的绑定对象从主窗口 HWND 改为子 HWND；`PresentationBindingState` 的取值域**不变**（仍四态）。
2. **子 HWND 的 affinity 所有权**：WDA 排除作用在**子 HWND** 还是主窗口顶层？计划 §9 `:380` 明文要求验证"WDA 顶层策略和捕获自排除" ⇒ 需**新增**一条清理所有权规则：**主窗口顶层**与**子 HWND** 各自独立持有 affinity 所有权，二者不得互相沿用原值。
3. **新的期望/有效路由差异**：子 HWND 场景下"GPU 渲染在子窗、主窗做 GDI"⇒ `ExpectedRoute`/`EffectiveRoute` 的语义需在 `rendering.fallbackStyle` 之外再补一条"呈现载体"维度（主窗 / 子窗）。**本文件不预设**，留给 §9 复审。

### 12.3 需主审裁决的**所有权缺口**（非 T01 依赖，但阻塞物化）

`【事实】` 计划 §6 `:276` 有一条完整性约束："所有权表必须覆盖条款中被修改的每一个既有文件"。本合同的 **D6（GPU-005）** 要求修改 `src/Rendering/Configuration/GpuStartupGuard.cs`，但 §6 的表格（`:269-274`）**未把它归属给任何 owner**（A 只拥有该目录下的**新增**文件与 `RenderingRoutePolicy.cs`；D 只拥有 `ThemeRenderingConfig.cs`）。

⇒ **建议裁决**：`src/Rendering/Configuration/GpuStartupGuard.cs` ⇒ **owner A**（与其在 `src/Rendering/Configuration/` 的既有归属一致，且不与 D 的 `ThemeRenderingConfig.cs` 重叠，符合 §6 `:278` 的"文件不重叠"约定）。
⇒ 若主审选择归属 C，则 C 同时拥有 `src/Rendering/Configuration/GpuStartupGuard.cs` 与 `src/Rendering/Presentation/**`，同样不冲突。

### 12.4 物化交接（C 期）

| 序 | 物化内容 | owner | 前置 |
| --- | --- | --- | --- |
| 1 | `src/Rendering/Configuration/RenderingTransitionState.cs`（§3.2 全部纯类型） | A | T01 结论落地 + `src/Rendering/**` 重新冻结哈希 |
| 2 | `src/Rendering/Configuration/WindowHandleGeneration.cs`（§7.2） | A | 同上 |
| 3 | `src/Rendering/Configuration/RenderingRoutePolicy.cs` 扩展（§3.1 `WindowIsLayered`） | A | 序 1 完成 |
| 4 | `src/UI/LiveGlassController.cs` 代次改为只读消费（D1） | B | 序 2 完成 |
| 5 | `src/Rendering/Configuration/GpuStartupGuard.cs` 的 pending 语义（D6，§10 GPU-005） | A（待 §12.3 裁决） | — |
| 6 | `src/Rendering/Presentation/PresentationSession.cs` R1–R6（§7.3）+ `GpuPresentationHostController.cs` 的移交（D2） | C | 序 1、2 完成 |
| 7 | `src/UI/` 的协调器 + `MainForm_Transparent.cs` 薄入口 + 判别位（D9、§8） | B | 序 2、6 完成 |
| 8 | `src/Rendering/Configuration/ThemeRenderingConfig.cs` 的 `fallbackStyle`（§9.2–§9.6） | D | — |

**并行约束**：序 3、5、8 与序 1、2 无依赖 ⇒ 可与 T03/T05 并行开工；序 4、6、7 必须等序 1、2 落地。

---

## 13. 与既有口径不分叉的自检

| 检查项 | 结论 |
| --- | --- |
| 是否新建了第二套期望路由枚举？ | **否** —— 复用 `RenderingRoute`（`RenderingRoutePolicy.cs:5-10`），未新增枚举成员 |
| 是否新建了第二套决策函数？ | **否** —— 期望路由仍由唯一纯函数 `RenderingRoutePolicy.Decide`（`:32`）产出 |
| 是否新建了第二个句柄代次计数器？ | **否** —— D1 选择"合并为同一真相源"，`LiveGlassController._handleGeneration` 被移除 |
| 唯一消费点 `src/UI/UIController.cs:951` 是否被改动？ | **否** —— D8 要求逐字不变；有效路由只用于新消费点 |
| 是否建立了主题文件名 → 样式映射表？ | **否** —— 样式随主题的 `rendering.fallbackStyle` 走（§9.2） |
| 是否新增了不存在的机制？ | **否** —— T00 §3 已确认热键 / `NativeWindow` / hook / `WM_SIZE` 等在本仓库**根本不存在**，本合同不为其新建绑定 |
| 是否在 `src/` 下产生了任何文件？ | **否** —— 本轮仅产出本文件；§12.4 给出物化时点 |

---

## 14. 本合同**未**裁决的项（`NOT_VERIFIED` 汇总）

| # | 项 | 为什么无法在本轮裁决 | 需要什么证据 | 归属 |
| --- | --- | --- | --- | --- |
| 1 | 重建后 `Form.Opacity` 的分层 alpha 是否自动恢复 | `【事实】` T00 §7 已标为"推断（未在代码中确认）"；本轮不跑 GUI | T01/T03 实机：重建前后读回 `GetWindowLong(GWL_EXSTYLE)` 的 `WS_EX_LAYERED` + 实际分层 alpha | T03（B） |
| 2 | DXGI 工厂级 `MakeWindowAssociation` 在旧 HWND 销毁后的残留行为 | `【事实】` T00 §7 已标为"推断（未在代码中确认）"；`GpuSceneRenderer.cs:184` 全仓无解除/复位调用 | T01/T04 实机：旧 HWND 销毁后在新 HWND 上观察 `MakeWindowAssociation` 残留是否影响呈现 | T04（C） |
| 3 | 本仓库的 `WS_EX_LAYERED` 窗口上 `SetWindowDisplayAffinity` 是否确实失败（error 8） | 外部权威已记载，但**本仓库未实测**（T00 GPU-003 明确要求 T01/T09 实测确认） | T09/G10：读回 `GWL_EXSTYLE` 与 `GetWindowDisplayAffinity`，对照开关穿透前后的行为 | T09（F） |
| 4 | `Control.RecreateHandle()` 在本项目 `MainForm` 上是否可安全使用 | **这正是 T01 的判定对象**（计划 §4.1 `:114-117`） | T01 探针结论（三分支） | T01（E） |
| 5 | 清理预算 6 次 / 退避 250→2000 ms 是否足够覆盖真实窗口存活时间 | 需要真实窗口生命周期时长分布 | T09/G04：注入 WDA 恢复失败并记录窗口存活时长与恢复成功率 | T09（F） |
| 6 | §9.3 六个字段的**具体颜色取值**与像素外观 | 属 T05 范围（配色），本文件明文不设计 | — | T05（D） |
| 7 | §5 全部码值的**用户可见文案** | 属 T08 范围（文案），本文件明文不设计 | — | T08（F） |
| 8 | 清理责任转交后"新代次独立取得所有权"在实机上是否无 WDA 副作用 | 需实机交叉验证 GPU-001 修复 | T09/G10：失败注入后切回 GPU，确认 affinity 读回原值且无叠加 | T09（F） |
| 9 | `rendering.fallbackStyle` 作为**兄弟块**（而非塞进 `rendering.fallback`）是否被主审接受 | §4.6 `:209` 的字面要求与硬依赖 2 冲突（§9.2 已说明） | 主审裁决；若否决，则改走 §1.3 的"typed `rendering.fallback`"备选（需同时处理 `RenderContracts.cs` 的归属） | 主审（A） |
| 10 | §3.1 的 `WindowIsLayered` 落在 `Decide` 的具体位置是否影响既有单测期望 | 既有 `Decide` 的既有分支顺序已核对（`:61`/`:66`/`:104`），但**未跑测试** | T06 跑既有 `RenderingRoutePolicy` 单测，确认零回归 | T06（E） |

---

## 15. 交付自检

- 本文件是本轮**唯一**新增文件；`src/**`、`tests/**`、`tools/**`、`resources/**`、其它 `docs/**`、`.gitignore` **均未改动**。
- **未** git 提交、**未**改 master、**未**推送。
- 全部【事实】均附 `路径:行号`；全部新行为均标记为【裁决】；§14 列出 10 项 `NOT_VERIFIED`。
- T03–T08 可按本文件并行开工：每条规则都落到具体文件与字段（§11 的 owner 列），物化顺序见 §12.4。
