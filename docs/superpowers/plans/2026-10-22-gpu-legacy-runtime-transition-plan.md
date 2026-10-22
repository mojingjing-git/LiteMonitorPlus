# GPU 与旧管线运行时切换修复计划

日期：2026-10-22。状态：**REVISION-1 / 已按独立复审修订，待复审；仍未执行实现、构建或测试**。

> **修订记录（2026-10-22，独立复审第 1 轮）**：复审报告 `docs/reviews/2026-10-22-gpu-legacy-runtime-transition-plan-review.md` 判定 NEEDS-REVISION，列 4 中 + 3 低 + 2 信息共 9 项。本次已逐项落到条款：
> - **A1（HWND 代次缺单一真相源）** → §4.1 / §4.2 新增"句柄代次的唯一真相源"段，指名既有 `LiveGlassController._handleGeneration` 与其捕获请求链；§4.3 步骤 5 加同步约束。
> - **A2（T05 撞上既有主题硬校验）** → §4.6 新增"扩展 `rendering.fallback` 的三个硬依赖"；§5 T05 交付物补三项；§7.1 U12 补两条断言。
> - **A3（`MainForm` 非 partial）** → §4.3 步骤 4 加约束；§4.7 路径表列出 `:14`。
> - **A4（`HandleCreated` 两个来源未区分）** → §4.2 新增"`HandleCreated` 有两种来源"段；§4.3 步骤 4/5、§4.4、§7.1 U06/U06b 同步。
> - **A5（`RecreateHandle` 全仓无先例）** → §4.1 新增"T01 的判定规则"（可安全 / 不可安全 / 不确定三分支，不确定则记 `NOT_VERIFIED`）。
> - **A6（T02 应复用 `RenderingRoutePolicy`）** → §4.2 新增"必须在既有模型上扩展"段，指名 `RenderingRoutePolicy.cs:5/12/19/32` 与消费点 `UIController.cs:951`。
> - **A7（"旧 M2 捕获"未定义）** → §4.5 补定义（= `LiveGlassController` + `src/System/Backdrop/**`）；§7.1 U14 改名。
> - **A8（类型缺真实路径）** → §4.7 新增路径表。
> - **A9（计划未入库）** → 已随本轮修订一并提交。
>
> **下一轮复审重点**：A1–A4 是否已成为可执行条款；T00 的"候选构建输入"须绑定本修订后的提交。
>
> ---
>
> **复审第 2 轮结论（2026-10-22，编排方自审）：PASS（修订后可进入 A 期收尾）**
>
> 第 2 轮只做两件事：核对第 1 轮修订是否**逐条落地且引用准确**，以及**修订本身是否引入了新的不可执行点**。
> - 逐条核对：A1–A9 九项**全部落地**，且引用的行号经重新比对**全部正确**（`LiveGlassController.cs:87`/`:295-300`、`BackdropServiceRequest.cs:121`/`:143-147`、`ScreenCaptureFrameSource.cs:85-87`/`:184-186`、`BackdropCaptureAdapters.cs:274`、`RenderingRoutePolicy.cs:5/12/19/27/32`、`UIController.cs:951`/`:299-302`、`ThemeRenderingConfig.cs:53-57`/`:136-141`、`AppearanceFields.cs:256-260`、`ThemeManager.cs:563`、`MainForm_Transparent.cs:14`）。
> - **发现并修复 1 项新缺口（R2-1）**：第 1 轮的 A1/A4/A6/A2 引入了"必须修改 `LiveGlassController.cs`、`src/System/Backdrop/**`、`RenderingRoutePolicy.cs`、`ThemeRenderingConfig.cs`、`AppearanceFields.cs`、`ThemeManager.cs`"的要求，但 §6 所有权表**未授权任何 owner** 写这些文件 → 实现者必然撞越界或无人认领。**已修复**：§6 所有权表补齐四处归属（`LiveGlassController.cs` → B；`src/System/Backdrop/**` → C；`RenderingRoutePolicy.cs` + 新状态类型 → A；`ThemeRenderingConfig.cs`/`AppearanceFields.cs`/`ThemeManager.cs` → D），并加"所有权表必须覆盖条款中被修改的每一个既有文件"的完整性约束。
> - **补充 1 项启动条件（R2-2）**：§3.1 的 B 期原写"A 期通过"即可启动，但 T01 的结论**必须绑定冻结候选**才有可比性——探针引用生产工程，源码处于中间态时 T01 跑不起来、结论不可信。**已补充**：B 期启动条件改为"其它任务代码改动已全部提交 + 工作区干净 + 全量构建通过"，并要求 T01 报告绑定候选哈希、运行中检测到源文件哈希变化则结论作废重跑。
> - 第 2 轮**未发现**需要推翻路线的错误；§4.1 主方案、§9 备用路线、§7 验收矩阵保持不变。
>
> **A 期（计划修订 + 复审）至此完成。B 期（T00/T01/T02）待本批修复收敛、工作区冻结后启动。**

## 1. 目标与完成标准

修复“GPU 渲染启动后无法正常切回旧管线”，覆盖手动关闭 GPU、切换旧主题、运行中故障回退以及重新开启 GPU。用户无需重启程序，也无需通过移动窗口或额外换肤恢复画面。

完成必须同时满足：

1. GPU 已成功呈现后，切回 legacy 能真正显示并持续更新 GDI+ 画面。
2. 反向开启 GPU 能恢复出帧；连续往返不会卡在已释放宿主、重复捕获或恢复循环中。
3. 窗口位置、DPI、横竖布局、透明度、鼠标穿透、置顶、轮廓和交互保持正确。
4. 退出 GPU 实时捕获后恢复原始窗口捕获策略；尚未完成的清理有明确所有者和重试路径。
5. GPU 主题留在 legacy 时有可读回退画面；不自动改写用户选择的主题或开关。
6. 有针对实际画面和窗口行为的运行时证据，不能只凭路由枚举、对象为空或冷启动测试判定完成。

本计划承接现有 GPU 非性能收口计划，修复其运行时回退覆盖缺口。性能优化继续延期；本轮只检查生命周期和资源正确性，不跑长时间 CPU 配对、性能预算或压力跑批。

## 2. 已核实事实、推断与待验证项

### 2.1 已核实的代码事实

| 位置（行号为撰写时定位） | 事实 | 意义 |
| --- | --- | --- |
| `src/Rendering/Presentation/GpuSceneRenderer.cs:164,178,644` | `FlipSequential` 交换链绑定主窗口 HWND，并调用 `Present` | GPU 与 legacy 共用主窗口原生句柄 |
| `src/UI/UIController.cs:360,954` | 回退主要执行宿主 Dispose、修改路由，没有恢复 GDI 能力的 HWND 重建 | 停止 GPU 不等于原 HWND 可以继续显示 GDI |
| `src/UI/UIController.cs:209,215` | 同步重绘早于路由切换 | 重绘可能仍被 GPU 活跃判断跳过 |
| `src/UI/MainForm_Transparent.cs:266` | `HandleCreated` 会触发 GPU 宿主重建 | 重建 HWND 前必须冻结切换状态，避免反向启动 GPU |
| `src/UI/UIController.cs:634` | Tick 会按 GPU 路由与 NeedsRestart 尝试启动 | 故障回退也需阻断无意重启 |
| `src/UI/UIController.cs:363` | WDA 恢复失败时保留已 Dispose 宿主 | 渲染所有权与策略清理责任混在同一个引用中 |
| `src/Rendering/Presentation/GpuPresentationHostController.cs:740` | 再次 Dispose 可尝试清理 | “失败后只能重启程序”不是无条件成立 |
| `src/Rendering/Presentation/PresentationSession.cs:65` | HWND 已销毁时恢复 affinity 按完成处理 | 不能把所有恢复失败都归因于无效句柄 |
| `tools/GpuUiProbe/Program.cs` | 有冷启动 legacy、GPU 主题互换、隐藏显示、句柄重建入口 | 缺少实际 GPU 出帧后切回 legacy 再返回 GPU 的完整断言 |

微软的 [DXGI_SWAP_EFFECT 说明](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ne-dxgi-dxgi_swap_effect)明确描述：flip-model 首次成功呈现后，关联 HWND 不再支持普通 GDI 绘制，即使交换链已销毁。另见 [DXGI flip model](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model) 对同一 HWND 混用 GDI 的限制。

本项目使用的是 `Present`，不要在报告中误写为代码调用了 `Present1`。上述文档构成架构判断依据，实机结论仍由 T01 的探针取得。

### 2.2 当前判断

最优先验证和修复的是 HWND 的呈现生命周期。主题残缺、重绘顺序和 WDA 清理耦合是独立问题，均不能替代该修复。

本轮尚未实机复现，不能预先宣称所有用户现象已被单一根因解释；T01 如不支持预期，必须补充证据、修订技术决策，再进入生产接线。

## 3. 范围、约束与已有并发修改

- 保留 WinForms、现有 GPU 材质和旧 GDI+ 渲染器；优先修复切换边界。
- 生产继续采用单一顶层主窗口；不新建第二个置顶窗或桌面背景窗来掩盖恢复问题。
- 不以更换交换链为 BitBlt、全局修改 WS_EX_LAYERED 或增加 `GDI_COMPATIBLE` 作为未经验证的一行修复。它们均不是现有 WinForms OnPaint 的等价替代。
- 保留用户 `Skin`、GPU/live/WDA 接受选择；运行故障只改变本次有效状态，不把用户选择悄悄写回为关闭。
- 不重写 shader、调整雾量或改动现有 GPU 主题观感；legacy 回退画面单独定义。
- 硬件监控、统计、历史、任务栏、托盘和插件的托管业务对象不随 HWND 重建重新创建或销毁。
- 不停止日常运行实例，不改真实用户配置，不自动提权、不改显示器拓扑、系统 DPI 或分辨率。
- 不自动提交、推送、发布、关机或启动实现 Agent；本次交付仅为计划，实际调度属于后续执行阶段。
- 撰写时 HEAD 观测值为 `0f1177e0d005c2f5b6b68a81dfc29de9722ac263`，工作区存在并发修改。它不是固定验收基线，T00 必须重新记录。
- `Settings.cs`、`PluginManager.cs`、网页服务、`MainPanelPage.cs` 和相关测试存在其他任务修改；不得覆盖、整体回退或顺手纳入本任务。尤其设置页接线需与当前 owner 串行交接。

### 3.1 分期与启动门槛（2026-10-22 编排方裁决）

本计划**不得整体等待**其它修复批次结束，也**不得整体提前**。按下列四期推进：

| 期 | 内容 | 启动门槛 | 与其它批次的争用 |
| --- | --- | --- | --- |
| **A** | 计划修订（复审 A1–A9 落条款）+ 复审第 2 轮 | 无 | **零**：只改本计划与评审文档，不碰任何生产代码与测试工程 |
| **B** | T00 基线与所有权清单、T01 探针对照、T02 合同冻结 | A 期通过 **且** 工作区已冻结（见下方"冻结候选"要求） | **低**：只触及 `tools/GpuHostLifecycleProbe`、`tools/GpuUiProbe`、`src/Rendering/**`、`src/ThemeEditor/AppearanceFields.cs`、`resources/themes/*.json`——经实测这些路径当前**无其它任务占用**；T02 的"最小测试接口"若落 `tests/LiteMonitor.Tests` 则与其他任务共享该工程，仅有构建/测试互扰，无文件冲突 |
| **C** | T03–T08 生产接线与新增测试 | B 期完成，且 M2/M3 门禁通过 | **高**：T08 需接线 `src/UI/Settings/**`，与在途的网页服务任务的 `MainPanelPage.cs` **直接冲突**，必须串行交接；T06/T07 往 `tests/LiteMonitor.Tests` 加文件会与其他任务争用同一测试工程 |
| **D** | T09 实机验收 + T10 双人独立终审 | C 期完成且**候选冻结**（源码/配置/shader/二进制哈希入档） | 需人工在场 |

**为什么 C 期必须等门禁通过**：门禁要求"同一候选"上复核全量回归逐程序集计数并由独立 verifier 判定；若生产改动在门禁期间落地，"同一候选"前提即破，这正是 §8 第 8 条（冻结候选哈希）要防的情况。

**为什么 B 期可以提前**：B 期不写生产代码、不改既有测试，且其产出（T01 的路线结论、T02 的合同）正是 C 期的输入。**T01 尤其值得提前**——它以极低成本消除"整个方案是否成立"的不确定性，而 §9 的备用路线（独立 GPU 子 HWND）属架构变更，越晚知道代价越大。

**但 B 期仍需"冻结候选"（2026-10-22 编排方补充）**：T01 的结论（`RecreateHandle` 是否安全、HWND 重建后 GDI 是否恢复）**必须绑定一个确定的候选**，否则结论不可复现、不可比较。具体要求：

1. **工作区必须可编译**。T01 的探针（`tools/GpuHostLifecycleProbe`）引用生产工程 `LiteMonitor`；若其它任务正在改动 `src/` 且处于中间态（编译错误或鉴权半成品），T01 **跑不起来**，其结论也不可信。
2. **候选哈希必须入档**（§8 第 8 条）：两个探针、生产 `src/Rendering/**`、`src/UI/**`、主题 JSON、配置与 shader 的哈希，绑定到 T01 报告。
3. **T01 不得在"正在被修改"的源码上运行**。若探针在运行期间检测到被测源文件哈希变化，结论作废并重跑。
4. 因此 B 期的启动条件是：**其它任务的代码改动已全部提交、工作区干净、全量构建通过**。这与"这批收敛"是同一时点。

**T01 的运行影响**：T01 会启动真实窗口并产生 GPU 出帧。按 §7.3，默认**非置顶、不激活、不抢焦点**，单轮 ≤ 60 秒。执行前须知会使用者。

## 4. 技术方案

### 4.1 首选方案：保留 Form 对象，按需重建 HWND

先由短时探针对比"只释放交换链"和"释放后重建 HWND"。确认有效后，将受控 HWND 重建接入统一切换流程。

> **T01 的判定规则（必须产出明确结论，不得含糊）**：T01 需给出"`Control.RecreateHandle()` 在本项目 `MainForm` 上是否可安全使用"的二选一结论。
> - **可安全使用** → 走本节主线，并在报告中说明依据（句柄销毁/创建事件序列、窗口状态恢复结果、GDI 动态画面恢复证据）。
> - **不可安全使用** → **直接走 §9 备用路线**（独立 GPU 子 HWND），并按 §9 末尾要求重新复审与调整验收。
> - **不确定** → 结论记为 `NOT_VERIFIED`，**不得**先落主线再打补丁。
>
> 背景（**T00 更正，2026-10-23**）：生产代码 `src/` 搜索 `RecreateHandle` **零命中**，本项目**生产侧无先例**；但**工具与测试侧已有先例** —— `tools/GpuUiProbe/Program.cs:469-473` 用反射调用 `typeof(Form).GetMethod("RecreateHandle", …).Invoke(form, null)`，`tests/AppearanceWindowShapeProbe/WindowShapeProbeRunner.cs:1183-1197` 有 `TryRecreateHandle` 辅助。**这些先例只证明"反射调用受保护方法"在本仓库可行，不构成生产可用的依据** —— 生产路径按 §4.3 步骤 4 的要求，走 MainForm 自己提供的内部受控入口，不通过反射。`Control.RecreateHandle()` 为 `protected`，对 `Form` 重建顶层窗口并非官方常规路径（会销毁并重建顶层窗口，触发完整一轮 `HandleDestroyed`/`HandleCreated`，并丢失部分窗口状态）。本节对"换 BitBlt""改 `WS_EX_LAYERED`""加 `GDI_COMPATIBLE`"的克制态度，同样适用于主路线本身。

重建触发依据按 HWND 代次保存，不能存放在即将 Dispose 的 renderer 内而随之丢失：

- 该代次成功完成过 flip 呈现：退出到 legacy 必须重建。
- 首次呈现调用期间发生异常、是否已改变 HWND 状态无法确定：保守重建。
- 未创建交换链、未尝试呈现即初始化失败：证据支持时可省略重建。
- 旧句柄已由其他路径销毁：无需重复销毁，确认新代次状态后恢复。
- 连续应用同一 legacy 路由：不重复重建，不产生换肤循环。

不得用 `newHwnd != oldHwnd` 作为唯一断言，Windows 可能复用原生句柄数值；以销毁/创建事件及单调递增代次证明发生重建。

### 4.2 路由与切换状态合同

由纯状态模型描述意图，具体类型名可在 T02 冻结；不要求把现有类整体重写。

> **必须在既有模型上扩展，不得另立平行口径**：`src/Rendering/Configuration/RenderingRoutePolicy.cs` 已存在 `RenderingRoute` 枚举（`:5`）、`RenderingRouteRequest`（`:12`）、`RenderingRouteDecision`（`:19`，含 `:27 IsGpu`）与纯函数 `RenderingRoutePolicy.Decide(...)`（`:32`），这正是下表"期望路由：策略计算所得"的现成实现。
> **T02 的"期望路由 / 有效路由分离"必须在 `RenderingRoutePolicy` 的现有 `Request/Decision/Decide` 基础上扩展**，不得另建第二套期望路由枚举或决策函数。现有消费点是 `src/UI/UIController.cs:951`（`if (decision.IsGpu || _gpuRoute?.IsGpu == true)`），两套口径会在那里分叉。

> **句柄代次的唯一真相源（必须写死）**：仓库**已有**一套句柄代次机制，不得并行第二套：
> - `src/UI/LiveGlassController.cs:87` `private long _handleGeneration = 1;`
> - `src/UI/LiveGlassController.cs:295-300` `OnHandleChanged()` → `_handleGeneration++` → `RefreshRequest()` + `ReleaseHeldFrame()`
> - 该代次贯穿捕获请求链：`src/System/Backdrop/BackdropServiceRequest.cs:121`（`HandleGeneration`）、`:143-147`（请求等价性比较）、`src/System/Backdrop/ScreenCaptureFrameSource.cs:85-87`、`:184-186`（旧请求丢弃判定）、`src/System/Backdrop/BackdropCaptureAdapters.cs:274`。
>
> **T02 必须明确**：下表"HWND generation"与 `LiveGlassController._handleGeneration` 的关系——是**合并为同一真相源**，还是 GPU 侧另计但**在同一次 `HandleCreated` 中同步递增**并可互相校验。**不得**出现两套各自递增、互不知情的代次。若选择新计，必须给出旧请求/旧捕获回调的丢弃判据如何跨两套代次成立（对应 §4.3 步骤 5 的"更新捕获代次"）。

> **`HandleCreated` 有两种来源，必须区分（既有事实，勿忽略）**：`src/UI/UIController.cs:299-302` 注释原文记录——`Control.CreateGraphics()` 强制在 `MainForm` 构造体内创建句柄，等真正显示时句柄早已存在，`HandleCreated` 不再触发，`NotifyGpuHandleChanged` 于是永远不会被调用，GPU 宿主也就永远不会启动。
> 因此：
> 1. **构造期首次创建**（当前实际发生）：`HandleCreated` **不得**触发 GPU 宿主重建——这是既有设计，必须保持不变；
> 2. **重建后创建**（本计划 §4.3 引入）：这才是期望触发原生生命周期处理的时机。
> **受控重建入口必须携带"这是重建、不是首次创建"的判别位**（例如重建代次或一次性重建标志），`HandleCreated` 处理器据此决定是否走 GPU 重建路径。无判别位则两类来源无法区分，会同时破坏第 1 条（构造期被误拉起 GPU）与 §4.4 的"不得无条件拉起 GPU"。

| 状态维度 | 最少包含内容 |
| --- | --- |
| 用户选择 | 用户主题、GPU/live 开关、WDA 接受状态 |
| 期望路由 | 策略计算所得 Legacy / GPU Fixed / GPU Live |
| 有效路由 | 当前实际可呈现的后端；与期望路由独立 |
| 切换阶段 | Stable / Stopping / RecreatingHandle / Restoring / Starting / Failed |
| 身份 | transitionId、HWND generation、宿主 generation、最后已应用请求版本 |
| HWND 呈现历史 | 未绑定 / 已绑定 / 已尝试呈现 / 已成功呈现 |
| 清理状态 | None / Pending / Completed / Failed，含独立清理所有者 |
| 诊断 | 触发原因、故障码、清理错误、首帧结果；不混成一个字符串 |

所有窗口操作在 UI 线程执行。快速设置变化采用串行处理与最新请求合并；旧回调按 transitionId 和 generation 丢弃。关闭窗体拥有最高优先级，不允许关闭后恢复或重新创建 HWND。

### 4.3 GPU → legacy 的固定顺序

1. 计算新请求，进入切换保护，先设置目标路由。屏蔽本次切换引起的自动 GPU 启动、重复 Stop、捕获重启和递归 ApplyTheme。
2. 记录必要窗口状态及本代次呈现历史；取消/隔离旧捕获和场景回调，停止 GPU 呈现调度。
3. 停止捕获，按现有账本和租约规则归还帧、释放 renderer/交换链；策略恢复失败则转交独立清理对象，不能丢弃责任。
4. 必要时通过 MainForm 自己提供的内部受控入口调用 HWND 重建，避免生产通过反射调用受保护方法。在 UI 队列安全点进行，不在 OnPaint、WndProc 的销毁回调或 GPU 帧绘制栈内递归执行。
   > **`MainForm` 是非 partial 的单文件类**：`src/UI/MainForm_Transparent.cs:14` `public class MainForm : Form`，且全仓 `src/` 中**只有这一处** `MainForm` 声明（`MainFormBizHelper`、`MainFormWinHelper` 是独立类）。因此**不得**新建 `MainForm_*.cs` 作为 partial 扩展（会编译失败）：重建入口必须加在 `MainForm_Transparent.cs` 内，切换协调器另立独立类，MainForm 只保留一个薄入口。
   > 该入口必须**携带"受控重建"判别位**（见 §4.2 末段）：`HandleCreated` 依据它区分"重建后创建"与"构造期首次创建"。
5. 让 HandleDestroyed/HandleCreated 处理原生生命周期，切换协调器保证停止幂等、创建阶段不会启动错误后端。更新所有缓存 HWND 的组件、窗口消息注册、Region 和捕获代次。
   > 捕获代次的更新必须与 §4.2 规定的**句柄代次唯一真相源**一致（现有 `LiveGlassController._handleGeneration`），不得另起并行计数。
6. 在当前位置所属显示器上恢复大小与 DPI，重新求 legacy 外观和布局，重建所需画笔缓存。不要把旧显示器的已缩放像素布局直接当新布局使用。
7. 恢复透明度、穿透、置顶、可见性等属性。隐藏窗口保持隐藏，不因切换 Show 或抢焦点；不重复启动淡入动画。
8. 所有状态稳定后发出一次有效重绘并应用窗口轮廓。可见窗口在同一次 UI 处理流程或下一次专用 UI 调度完成首帧，不依赖普通采样刷新周期。
9. 发布完成状态；可见窗口须拿到 legacy 首帧证据。隐藏窗口报告“已切换、等待显示”，显示时再验证首帧。

HWND 重建失败时保留明确 Failed 状态及可控重试入口，禁止显示“已成功恢复”；不得自动无限重建。若当前窗体已进入关闭过程，直接执行清理而不恢复。

### 4.4 legacy → GPU 与运行故障

- 统一从协调器启动 GPU；GPU 初始化和首帧成功是不同节点，只有首帧成功后才能报告有效 GPU。
- 新宿主只使用当前 HWND、当前物理客户区和当前请求；旧异步结果不能发布到新宿主。
- 首帧前失败与首帧后失败都进入统一 fallback，按 HWND 历史决定是否重建。
- 设备丢失若保留现有有限恢复机制，恢复期间不得让 GDI 在同一 flip HWND 上假接管；最终失败才进入完整 legacy 切换。
- 临时 live 捕获失败优先沿既有政策退到 GPU fixed；真正离开 GPU 时才执行 HWND 恢复流程。
- 故障回退后，不允许普通 Tick 或 HandleCreated 无条件把 GPU 拉起来。用户显式重试或既有合同允许的有限恢复才可重试，记录次数和原因。
  > **必须区分 `HandleCreated` 的两个来源**（见 §4.2 末段）：构造期首次创建**保持现状**（不拉起 GPU，这是既有设计）；只有携带"受控重建"判别位的重建才进入 GPU 重建路径。没有判别位时，这一条与 §4.3 步骤 5 都无法成立。
- `GpuStartupGuard` 的 pending/完成语义保持；不得通过每次 Tick 清标记规避保护。

### 4.5 独立 WDA 清理所有权

将“渲染已经停止”与“旧 HWND 策略已恢复”分离。可复用/转交现有 PresentationSession 的清理部分，不为此建立另一套呈现循环。

清理对象至少持有：旧 HWND、所属代次、原始 affinity、是否由本程序修改、尝试次数、失败码及最后错误。宿主释放后由 UI 生命周期所有者继续持有它。

- 恢复的是原始策略，不能固定写成 `WDA_NONE`。
- 恢复失败后允许有限、事件驱动或带退避的重试，禁止每帧重试。
- 本程序明确收到旧代次销毁通知后，其 affinity 不再需恢复；同步结束该代次清理。不要稍后仅凭旧 IntPtr 仍有效就操作它，避免命中被复用的新窗口。
- 不能因旧 HWND 清理任务 Pending，就对新 HWND 直接沿用“已排除”的状态。
- 同一 HWND 上清理未完成时禁止叠加新的 affinity 所有者；新代次必须独立确认原始策略并取得所有权。
- 有效后端状态与清理告警同时可见，不覆盖故障根因；关闭时清理必须幂等。
- GPU legacy 回退时 GPU 捕获会话归零。若旧主题明确启用旧捕获，遵守其既有合同，整体仍不得出现两个捕获所有者。
  > **术语澄清（"旧 M2 捕获"）**：本计划中"旧 M2 捕获"指仓库既有的 **live/backdrop 背景捕获**那一条链路，即 `src/UI/LiveGlassController.cs` + `src/System/Backdrop/**`（`BackdropServiceRequest`、`ScreenCaptureFrameSource`、`BackdropCaptureAdapters`）。**注意**：该缩写此前在本计划中首次出现时**没有定义**，全仓 `src/` 与其它计划文档搜索 `\bM2\b` 零命中；此处补上定义，避免实施者另作解读。

### 4.6 GPU 主题的 legacy 回退外观

采用独立解析的、仅供 legacy 使用的回退样式，保留原主题对象与用户 Skin。样式字段**随主题走**，由 T02 冻结字段与校验规则，避免建立主题文件名映射表。

> **字段载体（2026-10-23 主审裁决，见 §12）**：本段原文为"优先扩展已有 `rendering.fallback` 的可选样式字段"。该字面要求与本段自己列出的硬依赖 2（新增字段必须用**纯 CLR 类型**）**不可同时满足** —— `rendering.fallback` 的类型是 `Dictionary<string, JsonElement>`（`ThemeRenderingConfig.cs:34`），放进去的新字段必然是 `JsonElement`。**裁决：改用加法式兄弟块 `rendering.fallbackStyle`**（typed class `ThemeFallbackStyle`，字段全为 `string?`），`rendering.fallback` 的类型、`backend`/`material` 语义与 `Validate()` 规则**一字不动**。理由：本段用的是"**优先**"（偏好而非绝对），与硬依赖冲突时**硬依赖优先**；且兄弟块同样满足本段意图（样式随主题走、不建文件名映射表、保留原主题与用户 Skin）且**零破坏性**。

> **扩展 `rendering.fallback` 的三个硬依赖（既有约束，T05 必须同步处理）**：
> 1. **既有校验器会拒绝**：`src/Rendering/Configuration/ThemeRenderingConfig.cs:136-141` 当前**强制** `rendering.fallback` 的 `backend`/`material` 必须是 `legacy`/`legacy`，否则整份主题报 `rendering.fallback must select legacy/legacy.`。T05 新增样式字段时**必须同步扩展 `Validate()`**，否则新字段不受校验，且实现者一旦给 `backend` 赋非 legacy 值会被校验器直接拒绝整份主题。
> 2. **克隆路径是浅拷贝**（**2026-10-23 主审更正归因，结论不变**）：原归因" `ThemeRenderingConfig.cs:53-57` 的拷贝路径对 `Fallback` 走 `CopyDictionary`（字典浅拷贝）"**不准确** —— 实测 `CopyDictionary`（`ThemeRenderingConfig.cs:48-49`）对每个值调用 `JsonElement.Clone()` ⇒ **是深脱离，安全**。**真正的浅拷贝在编辑器克隆路径**：`src/ThemeEditor/AppearanceFields.cs:292-318` 的 `CloneValue` 在 `:297` 判定 `t.IsValueType` ⇒ `JsonElement`（struct）**原值返回、不脱离**；`:320-326` 的 `CloneDictionary` 同样按值复制；而 `CloneRendering`（`:256-260`）走 `CloneObject` → `CopyInto` → `CloneValue` ⇒ **命中这条路径**。
>    **结论不变但理由更准**：新增字段**必须用纯 CLR 类型**（如 `string`/`int`/`bool`），而非 `JsonElement` —— 用纯 CLR 类型后，`CloneValue`（`:297`）按值复制，克隆路径**彻底无风险**。
>    **当前尚未触发** `ObjectDisposedException`：主题加载走 `JsonSerializer.Deserialize<Theme>(json, …)`（`src/Core/ThemeManager.cs:496`）⇒ STJ 对 `Dictionary<string, JsonElement>` 的元素已做脱离克隆 ⇒ 风险是**潜伏的**，不是现存的。
> 3. **未知字段保留**：`ThemeManager` 的 `PruneNode(theme.Rendering, typeof(ThemeRenderingConfig))`（`src/Core/ThemeManager.cs:563`）负责裁剪未声明字段；新增字段必须被 `ThemeRenderingConfig` 声明，否则保存时会被裁掉。

- 为 `LiquidGlass_GPU`、`LiquidGlass_Toolbar_GPU`、`CyberPunk_GPU` 提供背景、分组底色、边框与必要前景色回退。
- 回退保留指标选择、标签、字体、用户缩放、告警语义和进度值；不重新建立另一套业务布局。
- 先在未缩放配置上解析，再沿现有入口只缩放一次；切回 GPU 时仍用原始 GPU 配置。
- 不修改 `ThemeManager.Current` 的共享已缩放对象，不在自动回退时保存覆盖原 JSON。
- 第三方 GPU 主题未声明回退时使用明确的中性可读默认值；未知字段保留，非法值产生诊断并用默认值。
- 编辑器加载/保存/另存/取消保持可选字段；旧主题缺省行为与像素输出保持。
- 回退画面只承诺可读和可操作，不宣称 GDI 能呈现同样的折射效果。

### 4.7 HWND 重建兼容清单

T00/T03 必须搜索真实 HWND 持有者，而不是只处理 GPU 文件。逐项确认：MainFormWinHelper、窗口 Region、透明度/穿透、任务栏消息、热键/NativeWindow/hook（如实际存在）、捕获策略、大小位置通知、显示器/DPI 通知，以及托盘菜单对 Form 的引用。

每项登记"随 Form 对象保留 / 新 HWND 自动恢复 / 需显式重新绑定"，附代码位置。业务采样、插件调度和历史状态不得误绑到 HWND 销毁事件中被终止。修改仅限真实依赖，不能为了清单新增不存在的机制。

**本计划点名的类型，其真实路径如下（避免实施者误判文件位置）**：

| 类型 | 真实路径 |
| --- | --- |
| `MainForm`（非 partial，唯一声明） | `src/UI/MainForm_Transparent.cs:14` |
| `MainFormWinHelper` | `src/UI/Helpers/MainFormWinHelper.cs:15` |
| `GpuStartupGuard` | `src/Rendering/Configuration/GpuStartupGuard.cs:7` |
| `ThemeRenderingConfig` | `src/Rendering/Configuration/ThemeRenderingConfig.cs:7` |
| `RenderingRoutePolicy` | `src/Rendering/Configuration/RenderingRoutePolicy.cs:30` |
| `LiveGlassController`（既有句柄代次宿主） | `src/UI/LiveGlassController.cs:87` |
| `PresentationSession` | `src/Rendering/Presentation/PresentationSession.cs` |
| `GpuPresentationHostController` | `src/Rendering/Presentation/GpuPresentationHostController.cs` |
| `GpuSceneRenderer` | `src/Rendering/Presentation/GpuSceneRenderer.cs` |
| 主题 JSON | `resources/themes/*.json`（`LiquidGlass_GPU.json`、`LiquidGlass_Toolbar_GPU.json`、`CyberPunk_GPU.json` 为 GPU 主题；`LiquidGlass.json`、`CyberPunk_Mech.json` 为对照旧主题） |

## 5. 任务拆分与完成条件

| ID | 任务与建议文件 | 依赖 | 必须交付 |
| --- | --- | --- | --- |
| T00 | 基线与所有权：源码、工作区状态、候选构建输入、现有 HWND 依赖、屏幕条件；新任务证据目录 | 计划复审通过 | baseline、文件所有者表、验证环境；不覆盖其他任务修改 |
| T01 | 扩展 `tools/GpuHostLifecycleProbe` 或新增定向探针，比较直接停止与 HWND 重建；正式 renderer 与 MainForm 场景均覆盖 | T00 | 首次真实 Present、重建事件/代次、GDI 动态画面恢复的对照；结论支持首选方案或给出 ADR 修订 |
| T02 | 冻结状态、转交清理、generation 和 fallback 字段合同；纯状态类型及最小测试接口 | T00；最终采用路线需 T01 | 小型 ADR、状态表、事件顺序、错误语义与字段规则；并行 Agent 可按合同工作 |
| T03 | 生产切换协调、MainForm 受控重建、UIController 路由/重绘、Tick/HandleCreated 重入保护、显示/DPI恢复 | T01、T02；最终接线依赖 T04 | 实际 GPU→legacy→GPU 可恢复，关闭与重入安全，业务对象不重建 |
| T04 | `PresentationSession` 与 `GpuPresentationHostController`：清理责任转交、代次、有限重试、呈现历史暴露 | T02 | 两次失败后仍持有清理责任；旧 HWND 销毁可完成；宿主资源释放；新宿主不继承旧 affinity |
| T05 | 新增 legacy fallback resolver、`ThemeRenderingConfig` 可选字段、三份 GPU 主题回退配置及编辑器字段保留 | T02 | 回退可读，不改 Skin 或原 GPU 外观，缺省/非法/保存往返明确；**必须同时交付**：扩展 `ThemeRenderingConfig.Validate()`（既有 `:136-141` 强制 `rendering.fallback` 为 legacy/legacy）、扩展 `CloneRendering` 的字段保留路径（`src/ThemeEditor/AppearanceFields.cs:256-260`，注意 `ThemeRenderingConfig.cs:53-57` 为字典浅拷贝）、新增字段使用纯 CLR 类型而非 `JsonElement` |
| T06 | 定向单测：切换状态、首帧前后失败、generation、WDA、fallback；接入正确测试项目 | T02；集成断言等 T03–05 | 确定性测试，无真实桌面依赖，覆盖 §7 |
| T07 | `tools/GpuUiProbe` 的 runtime transition 入口、结果格式、可取消与超时；独立定向 manifest | T02；正式运行等 T03–06 | 通过生产设置/换肤入口切换；真实画面、代次、策略、资源均有断言；冷启动 legacy 不能冒充 |
| T08 | 设置状态文案与重试接线，适配现有语言系统；协调 `MainPanelPage.cs` 并发 owner | T02；接线等 T03/T04 | 分清期望/实际/过渡/失败/清理告警；用户显式重试有效；设置不被自动改写 |
| T09 | 固定候选、单测、定向 GUI、可用双屏实机与既有相关回归；完整结果索引 | T03–08 | 同一候选的 pass/fail/blocked 记录，无未解决非性能 P0/P1/P2；缺环境不记通过 |
| T10 | 两位独立 reviewer：生命周期/资源与产品/验收；整改后复验、交付说明 | T09 | 代码与证据一致，明确未验项目；最终说明可交付，自动发布不在范围内 |

本轮不创建笼统的“完成所有性能测试”任务。资源泄漏、重复会话与无界重试属于正确性，仍阻断完成。

## 6. 并行分工与串行边界

建议最多 6 个执行 owner，按合同与文件拆分；没有合适工具或模型额度时由主 Agent 接手，不创建用户侧新聊天冒充 subagent。

| Owner | 独占写入范围 | 工作 |
| --- | --- | --- |
| A：主审/集成 | 本计划、ADR、最终报告；**T02 产出的纯状态类型**（`src/Rendering/Configuration/` 下**新增**文件）、`src/Rendering/Configuration/RenderingRoutePolicy.cs` 的扩展、**`src/Rendering/Configuration/GpuStartupGuard.cs`（2026-10-23 主审补齐，见 §12.3）**、合同文档 | T00/T02、集成排程、最后审查；合同文件先集中冻结再交接 |
| B：窗口切换 | `src/UI/UIController.cs`、`src/UI/MainForm_Transparent.cs`、`src/UI/Helpers/MainFormWinHelper.cs`、**`src/UI/LiveGlassController.cs`（句柄代次同步点）**、新增切换协调器（**独立类**；`MainForm` 非 partial，薄入口只能加在 `MainForm_Transparent.cs`） | T03；其他 owner 只提供接口，不同时写这些文件 |
| C：宿主与清理 | `src/Rendering/Presentation/PresentationSession.cs`、`GpuPresentationHostController.cs`、必要的 `GpuSceneRenderer.cs` 诊断字段、清理类型、**`src/System/Backdrop/**`（捕获代次消费点，见 §4.2 唯一真相源）** | T04、T01 的底层接口 |
| D：回退外观 | fallback resolver、**`src/Rendering/Configuration/ThemeRenderingConfig.cs`（回退样式字段）**、**`src/ThemeEditor/AppearanceFields.cs`**、**`src/Core/ThemeManager.cs`（仅 `PruneNode` 的字段保留路径；不得改 `ThemeManager.Current` 的共享已缩放对象）**、三份 GPU 主题 | T05；对 UIController/ThemeManager 的消费接口交 B 接入 |
| E：测试与探针 | 指定新增测试文件、两个 probe、定向 manifest、runner 必要接线 | T01/T06/T07；测试文件再按具体文件细分，防止多人改 Program.cs |
| F：设置与独立验收 | 设置状态适配、文案、用户验收表；不得审查自己写的功能来代替独立终审 | T08；T09 中未参与实现部分的验证 |

> **所有权表的完整性约束（复审第 2 轮补入）**：上表必须覆盖 §4/§5 条款中**要求被修改的每一个既有文件**。每个条款提到的文件都必须在某个 owner 的独占范围内，否则实现者会撞上越界或"无人认领"。
> 本轮补入的四处即由此而来：`src/UI/LiveGlassController.cs`（A1/A4 要求同步句柄代次）→ B；`src/System/Backdrop/**`（U14 与捕获代次消费点）→ C；`src/Rendering/Configuration/RenderingRoutePolicy.cs`（A6 要求在其上扩展期望/有效路由）→ A；`src/Rendering/Configuration/ThemeRenderingConfig.cs`、`src/ThemeEditor/AppearanceFields.cs`、`src/Core/ThemeManager.cs`（A2 要求同步校验器、克隆与字段保留路径）→ D。
> **A 与 D 都写 `src/Rendering/Configuration/`，但文件不重叠**（A 写新状态类型与 `RenderingRoutePolicy.cs`，D 写 `ThemeRenderingConfig.cs`）——不构成冲突，但同目录改动需在合并时留意。
> `src/Rendering/Presentation/**` 全部属 C，`src/UI/UIController.cs`、`src/UI/MainForm_Transparent.cs`、`src/UI/Helpers/MainFormWinHelper.cs`、`src/UI/LiveGlassController.cs` 全部属 B，**B 与 C 之间不得出现同一文件**。

依赖关系：

```text
计划复审 → T00 → T01 → 确认 HWND 技术路线
             └→ T02 合同草稿 ─┘
                         ↓ 合同冻结
          ┌──────────┬──────────┬──────────┬──────────┐
         T03        T04        T05      T06/07     T08
     窗口切换     宿主清理    回退外观   测试探针   设置状态
          └──────────┴──────────┴──────────┴──────────┘
                         ↓ 串行集成
                        T09 → T10
```

必须串行的边界：T01 技术结论、T02 合同冻结、同文件接线、最终候选冻结、GUI 窗口验收。编写纯测试和预置夹具可提前并行；生产集成必须等其依赖实现。

有两位 reviewer 时，先分别复审本计划的架构/资源与验收合同，再执行实现。当前只落计划，不在本轮自动派发。

## 7. 验收矩阵

### 7.1 纯逻辑与注入测试

| ID | 条件 | 断言 |
| --- | --- | --- |
| U01 | 未创建交换链即启动失败 | 进入 legacy，无多余窗口重建和 GPU 重启 |
| U02 | 已成功 Present 后退出 | 触发一次受控代次更新，旧渲染与捕获资源释放 |
| U03 | 首次 Present 抛错，结果不明确 | 保守执行句柄恢复，不因成功计数为零跳过 |
| U04 | 同一 legacy 请求重复到达 | 不再次重建；返回相同稳定状态 |
| U05 | 快速开关、换肤、DPI 通知交错 | 串行收敛到最新请求；旧回调不改变新状态 |
| U06 | HWND 重建触发 HandleCreated/Tick | 过渡期间 GPU 启动次数为零；完成后仅按目标启动；**构造期首次创建不被误判为重建**（无"受控重建"判别位时本条与 U05 一起失效） |
| U06b | `HandleCreated` 两个来源分别触发 | 构造期首次创建 ⇒ 不拉起 GPU（保持既有设计）；带判别位的受控重建 ⇒ 进入 GPU 重建路径 |
| U07 | WDA 连续恢复失败 | renderer 释放，清理对象保留，错误不丢失，无每帧重试 |
| U08 | 原始 affinity 非零 | 恢复精确原值，不写死 WDA_NONE |
| U09 | 旧代次销毁且 IntPtr 被复用 | 旧清理不操作新窗口；新代次独立获取所有权 |
| U10 | 切换期间关闭、重复 Dispose | 不重新创建窗口；重复清理安全；最终资源归零 |
| U11 | GPU 故障回退 | 走统一转换；用户开关保留；普通 Tick 不自动起 GPU |
| U12 | 回退字段缺省、非法、未知、克隆保存取消 | 可读默认、诊断明确、未知字段保留，原 GPU 对象与文件不被污染；**`Validate()` 接受新字段且仍拒绝非法 `backend`/`material`**；**克隆后访问新字段不抛 `ObjectDisposedException`**（新字段为纯 CLR 类型，不持 `JsonElement`） |
| U13 | 已隐藏或最小化时切换 | 不 Show、不激活，恢复显示后才完成可见首帧检查 |
| U14 | 旧捕获（live/backdrop，即 `LiveGlassController` + `src/System/Backdrop/**`）与 GPU live 转换 | 捕获所有者至多一个，旧会话回调不能发布到新代次 |

新增测试默认放在 `tests/LiteMonitor.Tests` 的独立文件；如需纳入 `LiteMonitor.GpuNonperformance.Tests`，它使用 `EnableDefaultCompileItems=false`，必须显式加入 Compile，防止“文件写了但根本没跑”。

### 7.2 实际窗口与实际画面

固定输入使用可识别合成背景及连续变化的测试指标，避免“旧 GPU 冻结帧看上去像 legacy”的误判。必须等待 GPU 首次成功呈现后再操作；不是创建宿主即算 GPU 已运行。

| ID | 操作 | 必须取得的证据 |
| --- | --- | --- |
| G01 | 三个 GPU 主题分别出帧 → 保持主题并关闭 GPU | legacy 真实画面、连续两次不同指标帧、代次变化、GPU capture=0、原 affinity 恢复 |
| G02 | GPU 出帧 → 选择 `LiquidGlass.json` / `CyberPunk_Mech.json` | 自动切回旧管线，外框/分组/数据完整，与相同输入旧入口对照 |
| G03 | legacy → GPU → legacy，短时往返 10 次 | 每轮目标正确；活跃宿主与捕获数量正确；全部退出后账本归零 |
| G04 | 注入首帧前失败、首帧后故障、停止恢复失败 | 实际回退与诊断一致；清理责任不丢；显式重试后 GPU 可恢复 |
| G05 | 隐藏/最小化切换 → 显示；切换过程中关闭 | 不抢焦点、不被主动显示；显示后首帧正确；关闭无残留窗口或进程 |
| G06 | 单屏横→竖→横，每段包含 GPU↔legacy | 布局和窗口轮廓恢复，无多余圆角、旧尺寸或冻结 |
| G07 | 可用的 X<0 / Y<0 副屏，跨屏往返并切换管线 | 保持目标屏幕物理位置，采样背景属于当前屏幕，DPI/Region 一致，不自动跳主屏 |
| G08 | 现有不同 DPI 屏幕往返；固定几何 96/144/168/192 DPI | 实机与夹具分开报告；位置、客户区、字体、圆角不重复缩放 |
| G09 | 透明度、穿透、置顶、拖动、右键、双击、托盘恢复 | 行为与用户设置一致；原来非置顶就保持非置顶；设置入口仍可操作 |
| G10 | live→legacy、GPU fixed↔live、显式重试 | 原始 affinity 真实读取核对；捕获会话/租约无重叠；实时/固定状态显示准确 |
| G11 | 保存、重启 legacy/GPU 及安全启动 | 设置持久化正确；不混淆 startup guard 与反向切换；冷启动作为独立回归 |
| G12 | 选定旧主题固定数据回归；GPU 正常路径固定输入对比 | 旧 GDI+ 输出保持；GPU 材质观感未被回退配置影响；数据/告警语义保持 |

G07/G08 的实机部分以真实现有设备为准，不自动重排屏幕。无对应环境标为 BLOCKED，并给人工步骤；不能拿数学夹具或单屏截图代替跨屏通过。

单轮 GUI 探针应在 60 秒内退出；10 次往返组可设置不超过 120 秒的外部截止时间。按场景拆进程运行，不放任长时间置顶或循环恢复。时间值是测试执行边界，不是性能指标。

### 7.3 测试窗口与证据纪律

- 默认非置顶、不激活、不抢焦点、不铺整屏背景。普通验收不得开启 `--topmost` / `--foreground`。
- 使用独立配置、临时目录、可关闭窗口、取消文件和外部 watchdog；只清理本次记录的 PID 及对应启动身份。
- live 场景使用已有授权范围内的短时捕获与专用背景；不上传或保存无关桌面内容。
- WDA 排除可能导致普通截图看不到窗口，不得为了拍图擅自关闭 WDA 后宣称原路径通过。前景/输出回读与 UI 首帧事件作为辅助，实际桌面是否显示用受控观察补证；输出纹理正确不能单独证明 GDI 已可见。
- `DrawToBitmap`、`PrintWindow`、GDI 函数被调用或 offscreen render 成功，均不能单独证明 flip 后的同一 HWND 已恢复桌面呈现。
- 先短时 T01，再纯测试，再 fixed GUI，最后 live 和真实多屏。窗口测试串行，避免彼此遮挡导致假失败。
- 新的定向 manifest 放在可跟踪位置（建议 `tools/GpuUiProbe/runtime-transition-manifest.json`），日志/图片放证据目录。现有 runner 的默认 manifest 位于忽略的 evidence 树，T07 不得将其当作干净 checkout 必然存在的前提；不修改 `.gitignore` 或 force-add 历史证据。

## 8. 实施与验证操作清单（后续阶段执行）

1. 独立复审本计划，修订合同缺口，记录结论；未通过前不开始生产修改。
2. T00 记录 HEAD、dirty 文件与全部相关输入哈希。工作区并发变化时复核 owner，不把 HEAD 等同完整源码快照。
3. T01 建立最小对照，证据支持后冻结路线与 T02 合同。
4. T03–08 按所有权并行实现，小步集成；主审统一合入 UI 接线，不让两个 Agent 同写 UIController。
5. 先跑新增纯测试，再执行 Release 构建与相关既有测试。命令、版本、退出码和测试发现数量入档，不沿用历史 583/583 或警告数。
6. 运行 T07 的 fixed 定向场景，再运行已授权 live 和可用实机场景；不运行 `run-gpu-performance.ps1` 或长时压力模式。
7. 复核生产设置入口真实路径至少一次，探针不得只反射改 `_gpuRoute` 或调用内部 Stop 冒充用户操作。
8. 冻结候选源码/配置/shader/二进制哈希；全部结果绑定该候选。候选变化后重跑受影响组，保留旧记录。
9. 两位 reviewer 独立检查，修复问题并复验；报告未完成环境和人工项。

本计划不保证预先列出的新探针参数已经存在。T07 落地后把实际 CLI 和可复制命令写入运行说明，未知参数必须报错，不能静默按冷启动模式运行。

## 9. 风险与备用路线

| 风险 | 处理 |
| --- | --- |
| HWND 重建引起淡入、跳屏、重新置顶或焦点变化 | 显式恢复状态，抑制淡入/激活，按 generation 应用；G05/G07/G09 必验 |
| DPI/布局事件触发递归 ApplyTheme | 协调器串行与请求版本合并，尺寸事件只标脏，最终统一布局和重绘 |
| 重建令异步硬件更新回调撞到过渡状态 | 保留业务对象，窗口操作按过渡状态推迟；恢复时消费最新数据，不销毁采样器 |
| WDA 恢复失败或 HWND 数值复用 | 独立清理所有者和明确销毁通知；不得用旧 IntPtr 去清理新代次 |
| 假通过：路由变了，屏幕仍冻结 | 首次 GPU Present 前置条件 + 实际桌面观察 + legacy 动态画面证据 |
| 测试再次长期盖住用户桌面 | 默认非置顶/不激活，串行短时运行，watchdog 与取消机制必须先具备 |
| 主窗体 HWND 重建牵涉较多绑定或 T01 显示无法可靠恢复 | 暂停该实现路线并形成 ADR；评估独立 GPU 子 HWND，让主窗体 GDI HWND 不绑定 flip。需验证子窗尺寸、输入、Region、WDA 顶层策略和捕获自排除；不直接扩大为全渲染器重写 |

备用路线触及现有“单顶层窗口”限制时仍可使用同一顶层中的子 HWND，但它是独立架构变更，必须重新复审与调整验收后执行，不能在主线失败时偷偷加入第二个顶层窗口。

## 10. 提交建议、回滚与最终交付

建议按职责形成可独立审阅的提交（实际提交需后续明确任务授权）：

1. `test(rendering): reproduce flip HWND legacy transition failure` — T01 与证据索引。
2. `refactor(rendering): separate transition and affinity cleanup ownership` — T02/T04 与定向单测。
3. `fix(rendering): restore legacy presentation after GPU shutdown` — T03 与接线测试。
4. `fix(themes): provide readable legacy fallback for GPU themes` — T05 与格式/像素回归。
5. `fix(settings): report effective rendering and cleanup state` — T08 与文案。
6. `test(rendering): cover runtime GPU legacy round trips` — T06/T07/T09 与最终结果。

不提交其他任务文件，不通过删除 GPU 路由测试或修改旧期望图让结果变绿。证据索引可提交，是否跟踪原始图片沿现有仓库规则处理。

若需撤回本修复，使用职责提交定位回滚；不丢弃整个工作区，也不永久把所有用户强制为 legacy。故障现场保留用户配置和失败原因，明确重启作为临时人工兜底，不能将它计作修复通过。

最终交付包括：

- 实际变更清单、候选版本与源码/二进制哈希。
- T00–T10、U01–U14、G01–G12 的逐项状态及证据索引。
- HWND 技术路线实测结论、状态时序、WDA 失败重试记录。
- 可复制的定向验证命令、人工多屏步骤和短时退出方式。
- 未验证环境、明确已知限制；性能延期单独保留。

**收口门槛：同一候选上，GPU 已实际出帧后能恢复 legacy 动态画面，能够重新开启 GPU，窗口状态和捕获策略均正确，且没有未解决的非性能 P0/P1/P2。缺少必要实机证据时只能报告实现完成、验收待补，不能写“全部通过”。**

---

## 11. T00 基线结论与新发现缺陷（2026-10-23）

### 11.1 T00 交付

T00 已完成。完整交付见 **`docs/superpowers/plans/2026-10-23-gpu-transition-t00-baseline.md`**（基线、冻结候选、§4.7 HWND 持有者清单 26 项、句柄代次真相源现状、计划行号复核更正表、验证环境与窗口纪律）。

| 项 | 值 |
|---|---|
| 冻结候选 HEAD | `e1bd58772a42ac73053ebeff8c2cd82c95b583f1` |
| `src/Rendering/**` 聚合 SHA-256（22 文件） | `a39d49ce83d13a7bbba0bbe2536a40583831eb742bf5bef7f0d62e831ba8a8e2` |
| `src/UI/**` 聚合 SHA-256（57 文件） | `502629b54aded5d78c06540e83813fcceff54e19e80052f45969fe3d51dcb1e7` |
| 两个探针 聚合 SHA-256（7 文件，已排除 `bin/`/`obj/`） | `6c49338496b4295ff678c65f82eb3323d08758647d7d0b3a1a3e4bfe8a3b593c` |
| 主题 JSON 聚合 SHA-256（15 文件） | `76422a86cf1fe20a2b1fafacbfbd24834f201283f388f278fea85f465c483cf6` |
| 配置与 shader 聚合 SHA-256（8 文件） | `1078b22e49b3642afa27bb0784790e3863a4a674a06ee8a102c2851dbb1d9e65` |
| 屏幕 | `DISPLAY1` `0,0 2194x1234` 96dpi 主屏；`DISPLAY2` `-1152,0 1152x2048` 96dpi **竖屏**副屏 |
| 权限 | **非管理员** ⇒ §7.2 的 **G05 / G09 需人工在场** |
| 显示适配器 | 2 × NVIDIA（RTX 3080 / 3080 Ti）+ **4 个虚拟显示适配器**（GameViewer / Meta / MuMu / Todesk）⇒ T01 报告须记录窗口实际落在哪块适配器上 |

聚合算法：组内文件按相对路径排序 → `"<相对路径>\0<小写SHA256>\n"` → 对 UTF-8 字节流取 SHA-256（与枚举顺序、换行风格无关，可复现）。

### 11.2 新发现缺陷

> 编号独立于 V3 修复批次的 `LM-` 序列。全部为**既有生产代码**的问题，非本轮新引入。

| ID | 级别 | 摘要 | 归属 |
|---|---|---|---|
| **GPU-001** | 🔴 高 | **WDA 恢复失败后 GPU 呈现永久不可重启** —— `GpuPresentationHostController.StopCore()`（`src/Rendering/Presentation/GpuPresentationHostController.cs:699-711`）连续两次恢复失败时**不置空** `_presentation` ⇒ `WindowCapturePolicyRestored`（`:93`）为 false ⇒ `UIController.StopGpuPresentation()`（`src/UI/UIController.cs:363-367`）**保留已 Dispose 的宿主引用** ⇒ 三条重启路径全部被同一个早退挡住：`NotifyGpuHandleChanged()`（`:280` `if (_gpuPresentation is not null) return;`）、`EnsureGpuPresentationStarted()`（`:312` 同形早退）、Tick 兜底（`:634` 依赖 `NeedsRestart`，而 `NeedsRestart => !_disposed && …`（宿主 `:81-85`）对**已 Dispose** 的宿主恒为 false）。⇒ **GPU 呈现在整个进程生命周期内永久不可用**，只有重启进程能恢复。**直接卡死 §4.1 主线**（HWND 重建依赖 `NotifyGpuHandleChanged` 重建宿主）。根因是把"**宿主引用非空**"当成"**清理责任仍在**"的代理，二者必须解耦 | **T02 合同必须解决**，落地 **T03/T04**。**B 期不修**（B 期不写生产代码） |
| **GPU-002** | 🟡 中 | `PresentationSession._originalAffinity`（`src/Rendering/Presentation/PresentationSession.cs:110`）是**进程内缓存**，跨 HWND 代次不可复用（它描述的是旧窗口）。现有路径之所以安全，是因为宿主**整体重建**（`GpuPresentationHostController.cs:201-213` 新建 session ⇒ `PresentationSession.cs:358-368` 在新句柄上重读原值）。若 T02 选择复用同一 session（`RecreateWindow`，`:176-214`），必须**先恢复旧句柄、再在新句柄重读原值** | T02 / T04 |
| **GPU-003** | 🟡 中 | 开启鼠标穿透后窗口**永久**保持 `WS_EX_LAYERED` —— `MainFormWinHelper.SetClickThrough(false)`（`src/UI/Helpers/MainFormWinHelper.cs:148`）只清 `WS_EX_TRANSPARENT`、**不清 `WS_EX_LAYERED`**；全仓无 `WS_EX_LAYERED` 清除点。而 `SetWindowDisplayAffinity` 在 `WS_EX_LAYERED` 窗口上**会失败**（微软社区支持明确："Windows currently does not support monitor affinity for this type of layered window."；StackOverflow 记 `GetLastError()=8` / `ERROR_NOT_ENOUGH_MEMORY`），且**事后 `SetWindowLong` 置位正是已知复现条件** —— 本仓库正是事后置位（`:146`）⇒ 可能把 GPU-001 从边缘情形变成常见路径 | T02 / T03；**T01/T09 实测确认** |
| **GPU-004** | 🟡 中 | `IsWindow` 无法区分"旧 HWND 已销毁"与"句柄值被复用"（`PresentationSession.cs:39` / `:65`）⇒ 旧清理可能命中新窗口。计划 §4.1 禁止以 `newHwnd != oldHwnd` 断言重建，但**单调代次必须贯穿 WDA 清理路径**，而不只是 live 捕获链 | T04 |
| **GPU-005** | 🟡 低 | `GpuStartupGuard` 的 pending 语义比 §4.4 描述**更弱** —— `GpuStartupAttempt.Dispose()`（`src/Rendering/Configuration/GpuStartupGuard.cs:316-324`）**无论首帧是否成功**都会调用 `CompleteAttempt`，而 `GpuPresentationHostController.StopCore()`（`:715`）正是 `_startupAttempt?.Dispose()` ⇒ 首帧失败但宿主正常停止时 pending 也会被清除；保守保护只在**进程非正常退出留下文件残留**时生效（`:52-62`）⇒ §4.4「不得通过每次 Tick 清标记规避保护」**缺少可操作判据** | T02（合同澄清） |

### 11.3 计划文本更正（已就地落笔）

1. §4.1「全仓 `src/` 与 `tools/` 搜索 `RecreateHandle` **零命中**，本项目无先例」**已被 T00 推翻**：`src/` 确实零命中，但 **工具与测试侧已有先例** —— `tools/GpuUiProbe/Program.cs:469-473`（反射 `typeof(Form).GetMethod("RecreateHandle", …).Invoke(form, null)`）与 `tests/AppearanceWindowShapeProbe/WindowShapeProbeRunner.cs:1183-1197`（`TryRecreateHandle`）。**这些先例只证明"反射调用受保护方法"在本仓库可行，不构成生产可用的依据** —— 生产仍按 §4.3 步骤 4 走 MainForm 内部受控入口。已就地更正。
2. §4.7 未点名但**已存在**的重绑机制：`PresentationSession.RecreateWindow(IntPtr, RenderGenerations, WindowCapturePolicy, out string?, out string?)`（`src/Rendering/Presentation/PresentationSession.cs:176-214`）—— 先 `RestoreOriginalAffinityCore` → 再 `ApplyCapturePolicyCore(newHandle)` → 更新 `_windowHandle`/`_generations`。**全仓无生产调用点**，仅 `tests/LiteMonitor.Tests/PresentationSessionTests.cs:161` 调用。**T02 必须决定它被正式采用还是废弃。**
3. 行号区间偏移（以实测为准）：`BackdropServiceRequest.cs:143-147` → **`:141-148`**；`ScreenCaptureFrameSource.cs:184-186` → **`:182-187`**；`ThemeRenderingConfig.cs:53-57` → 块 **`:51-57`**、`Fallback` 拷贝在 **`:57`**。
4. §7.2 **G08（不同 DPI 屏幕往返）的实机环境在本机不存在**（两屏均 96 dpi）⇒ 按 §7.2 记 `BLOCKED` + 人工步骤，**不得用数学夹具冒充跨 DPI 通过**。**G07**（X<0 副屏）与 **G06**（横→竖→横）的硬件前提**存在**（副屏 X=−1152、竖屏 1152×2048）。
5. §4.7 盘点结论：热键 / `NativeWindow` / hook（#9）、`WM_SIZE`/`WM_MOVE`/`WM_WINDOWPOSCHANGED`/`WM_EXITSIZEMOVE`/`WM_ENTERSIZEMOVE`（#17）、针对本窗的 `WM_SETTINGCHANGE`（#20）、`SystemEvents.DisplaySettingsChanged`（#21）在本仓库**根本不存在** —— 按 §4.7「不能为了清单新增不存在的机制」，实现者**不得**为其新建绑定。

### 11.4 T01 启动授权

T00 完成后，B 期启动门槛（§3 冻结候选要求 4：其它任务代码改动已全部提交、工作区干净、全量构建通过）**已满足**。

- **T01** 按 §4.1 的**三分支规则**产出明确结论（「可安全使用」/「不可安全使用」→ 走 §9 备用路线 /「不确定」→ 记 `NOT_VERIFIED`），**不得含糊**。
- T01 报告必须绑定 §11.1 的候选哈希，并实现「**运行期间检测到被测源文件哈希变化 ⇒ 结论作废重跑**」（§3 冻结候选要求 3）。
- T01 会修改探针本身 ⇒ 报告须给出**改后**的探针哈希，并声明生产侧 `src/Rendering/**` 与 `src/UI/**` 聚合哈希未变。
- §7.3 证据纪律：**不得**以 `DrawToBitmap` / `PrintWindow` / "GDI 函数被调用" / "offscreen render 成功" 单独证明 GDI 已恢复桌面呈现；需要**实际桌面观察**（受控截取窗口所在屏幕区域并回读像素）。
- §7.2 固定输入：必须使用**可识别合成背景与连续变化的测试指标**，避免"旧 GPU 冻结帧看上去像 legacy"的误判；必须等待 **GPU 首次成功呈现**后再操作。
- 窗口纪律（§7.3）：默认非置顶 / 不激活 / 不抢焦点；单轮 ≤ 60 秒；外部 watchdog 与取消机制必须先具备；窗口测试串行。

---

## 12. T02 合同冻结与主审裁决（2026-10-23）

### 12.1 交付物

T02 合同已冻结：**`docs/superpowers/plans/2026-10-23-gpu-transition-t02-contract.md`**（743 行）。含 10 条裁决 D1–D10、6 条被否决备选、8 维度状态表、§4.3 的 9 步事件顺序、11 个既有错误码 + 16 个新码、清理责任转交规则、句柄代次裁决、`HandleCreated` 判别位规格、fallback 字段规则、U01–U14 → owner 映射、物化顺序，以及 **10 项 `NOT_VERIFIED`**。

**本轮未在 `src/` 下产生任何文件**（T01 在跑、`src/Rendering/**` 聚合哈希冻结）⇒ 纯状态类型与合同扩展**只落规格**，物化交接见 §12.5。

### 12.2 主审对合同的事实复核

主审**未直接采信**合同自述，逐条复核了它提出的 3 处事实更正，**全部成立**：

| 合同主张 | 主审复核 | 结论 |
|---|---|---|
| `rendering.fallback` 类型是 `Dictionary<string, JsonElement>` | `src/Rendering/Configuration/ThemeRenderingConfig.cs:34` | ✅ 属实 |
| 计划 §4.6 把浅拷贝归因于 `ThemeRenderingConfig.cs:53-57` 的 `CopyDictionary`（字典浅拷贝）**不准确** | `ThemeRenderingConfig.cs:48-49` 的 `CopyDictionary` 对每个值调用 `JsonElement.Clone()` ⇒ **深脱离**；真正的浅拷贝在 `src/ThemeEditor/AppearanceFields.cs:297`（`t.IsValueType` ⇒ `JsonElement` 原值返回）与 `:320-326` | ✅ 合同更正正确；已回写 §4.6 |
| `PruneNode` 只裁 `ExtensionData` 中与已知属性名冲突的键（`:574-584`） | `src/Core/ThemeManager.cs:566-585` 属实；未知键由 STJ 落到 `ThemeFallbackStyle.ExtensionData` ⇒ 保留 | ✅ 合同对 HD-3 的分析成立 |

另核实：`src/Rendering/Contracts/RenderContracts.cs:499` 的 `RenderingConfig.Fallback` 确为 `IReadOnlyDictionary<string, JsonElement>`。

### 12.3 主审裁决记录

| # | 裁决 | 理由 |
|---|---|---|
| **D-A** | **批准** fallback 字段载体改为**加法式兄弟块 `rendering.fallbackStyle`**（typed class `ThemeFallbackStyle`，6 个 `string?` 字段 + `[JsonExtensionData]` 承接未知键），**偏离** §4.6 `:209` 的"优先扩展已有 `rendering.fallback`"字面表述 | ① §4.6 用的是"**优先**"（偏好非绝对），而 HD-2（纯 CLR 类型）是该段自己列出的**硬依赖**；`rendering.fallback` 是 `Dictionary<string, JsonElement>`（`:34`）⇒ 塞进去的新字段必然是 `JsonElement` ⇒ **二者不可同时按字面满足，硬依赖优先**。② 兄弟块同样满足 §4.6 的**意图**（样式随主题走、不建文件名映射表、保留原主题与用户 Skin）且**零破坏性**（`rendering.fallback` 类型与全部消费点不动，`Validate()` 一字不改）。③ 就地扩展会把样式字段混入"backend/material 语义"的字典，职责混淆。已回写 §4.6 |
| **D-B** | **更正** §4.6 硬依赖 2 的**风险归因**（结论不变）：从 `ThemeRenderingConfig.cs:53-57`（实为深脱离）改为 `AppearanceFields.cs:297`/`:320-326`（编辑器克隆路径的浅拷贝）；并补记"当前尚未触发 `ObjectDisposedException`，风险潜伏而非现存" | 主审已逐行复核属实。**纯 CLR 类型的硬约束不变** |
| **D-C** | **批准** `src/Rendering/Configuration/GpuStartupGuard.cs` 归 **owner A**，补齐 §6 所有权表缺口（合同 D6/GPU-005 需改它，但原表未归属任何 owner） | 与 A 已有的 `src/Rendering/Configuration/` 归属一致，且与 owner D 的 `ThemeRenderingConfig.cs` **文件不重叠**，符合 §6 `:278` 约定。已回写 §6 |
| **D-D** | **登记** `src/Rendering/Contracts/RenderContracts.cs` 在 §6 表中**未归属任何 owner**（信息项，**不阻塞**） | 在 D-A 方案下该文件**无需改动**（fallback resolver 直接读 `ThemeRenderingConfig`）⇒ 不阻塞物化；但缺口客观存在，后续若改 `RenderingConfig` 契约须先补归属 |

### 12.4 合同给出的 GPU-001 ~ GPU-005 处置

| ID | 处置（合同 §6/§7/§10） |
|---|---|
| **GPU-001** | 新增 `WindowCapturePolicyCleanup` 记录 + `CapturePolicyCleanupLedger`；宿主恢复失败时**移交**清理责任并**释放自己**，`StopGpuPresentation()` **无条件置 null** ⇒ 三条重启路径重新可达 |
| **GPU-002** | **正式采用** `PresentationSession.RecreateWindow`（`:176-214`），确认其"先恢复旧句柄 → 新句柄重读原值"顺序已满足，补齐 R1–R6（代次单调、失败不改句柄、同 HWND 清理未完成则拒绝等） |
| **GPU-003** | `WS_EX_LAYERED` 与 WDA **互斥**：期望状态由 `CreateParams` 按配置决定（重建后**不继承**残留），同 HWND 上不新增清除行为；改为 GPU 侧读 ex-style 并**不申请** WDA，有效路由降为 `GpuFixedInput` |
| **GPU-004** | **代次判据先于 `IsWindow`**：`cleanup.Generation < Current` ⇒ 禁止任何 Win32 调用并直接终结；仅同代次才用 `IsWindow` |
| **GPU-005** | pending 精确定义为"已开始但未被显式确认为干净"的尝试；清除只允许"首帧成功"或"受控放弃且停止流程干净"，**首帧失败且停止不干净 ⇒ pending 保留** |

### 12.5 句柄代次与判别位裁决摘要（合同 §7/§8）

- **句柄代次：合并为同一真相源**（计划 §4.2 二选一的"合并"方案）—— 新增 `WindowHandleGeneration`，唯一递增点 `Advance()`；`LiveGlassController._handleGeneration`（`src/UI/LiveGlassController.cs:87`）移除改为只读消费；GPU 清理链与 live 捕获链**共用同一代次** ⇒ GPU-004 的判据退化为一次整数比较。**满足计划 §4.2 "不得出现两套各自递增、互不知情的代次"。**
- **判别位**：`internal int PendingControlledRecreateCount` + `long PendingControlledRecreateRequestId`（owner B 协调器）；受控重建入口在 `RecreateHandle()` **之前**置位、`HandleCreated` 消费一次，配"取消 + 陈旧清理 + 5 秒超时"三重兜底。
- **错误码**：11 个既有码沿用原命名 + 16 个新码；`FailureCode` / `CleanupErrorCode` / `CleanupWarningCode` 三字段**互不覆盖**（诊断不混成一个字符串，满足 §4.2）。
- **不分叉自检**：复用 `RenderingRoute` 枚举与唯一 `Decide` 纯函数；**`src/UI/UIController.cs:951` 逐字不动**；不新建第二套期望路由枚举 / 决策函数 / 句柄代次计数器；不为主题文件名建映射表；不为不存在的机制（热键 / `NativeWindow` / hook / `WM_SIZE` 等）新建绑定。
- **物化顺序**（合同 §12.4）：序 3、5、8 可与 T03/T05 并行；序 4、6、7 必须等序 1、2 落地。物化前置 = **T01 结论落地 + `src/Rendering/**` 重新冻结哈希**。

### 12.6 fallback 字段名清单（合同 §9.3；取值与配色属 T05）

`ThemeFallbackStyle`（纯 CLR，全部可空，`null` = 未声明）：`Background`（背景）/ `PanelBackground`（分组底色）/ `Border`（边框）/ `Text`（主前景）/ `MutedText`（次要前景）/ `Accent`（强调）。颜色格式沿用 `ThemeManager.ParseColor`（`src/Core/ThemeManager.cs:596`）可解析的既有色值字符串。未知键由 `[JsonExtensionData]` 承接保留。

### 12.7 与 T01 的交接（待定项，**T01 结论落地后本节必须回填**）

| T01 结论 | 合同条款的变动 |
|---|---|
| **可安全使用** | §8 判别位、§4 步 4–7、§7.3 `RecreateWindow` 全量生效 |
| **不可安全使用**（走 §9 备用路线） | **步 4 删除**、判别位的"重建"分支作废、`RecreateWindow` **不采用**、4 个重建类错误码不适用、**U02 / U03 / U06b 改判**；需新增"子 HWND 呈现历史"与"子 HWND / 顶层各自持有 affinity 所有权"两条规则 |
| 路线无关、照常实现 | §6 清理转交（GPU-001）、§7 代次合并、§9 fallback 规则 |

### 12.8 合同登记的 10 项 `NOT_VERIFIED`

含 T00 已标的两项（重建后 `Form.Opacity` 分层 alpha 是否自动恢复；DXGI 工厂级 `MakeWindowAssociation` 在旧 HWND 销毁后的残留），以及：本仓库 layered 窗口上 `SetWindowDisplayAffinity` 是否确实失败（error 8，**T00 GPU-003 要求 T01/T09 实测确认**）、清理预算 6 次 / 退避 250→2000 ms 是否足够、6 个字段的具体配色（T05）、错误码的用户可见文案（T08）、清理转交后"新代次独立取得所有权"在实机上是否无 WDA 副作用（T09/G10）、`WindowIsLayered` 落在 `Decide` 的具体位置是否影响既有单测期望（T06）。
