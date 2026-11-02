# GPU 与 legacy 运行时切换合同

本文描述 LiteMonitor++ 在 GPU 路线与 legacy（GDI+）路线之间切换的运行时合同：切换的架构根因、状态模型、固定事件顺序、错误码表、清理责任的归属与转交、句柄代次与判别位规格、legacy 回退样式字段规则、失败分级与恢复预算，以及与其它模块之间的一致性约束。

适用对象是在本仓库上开发、评审与排障的工程师。不在本文范围：三条渲染路线的判定顺序与材质参数体系（见 `docs/architecture/rendering.md`）、实时背景帧的帧源与所有权模型、主题 JSON 的业务字段语义、界面控件的绘制细节。

---

## 1. 切换的架构根因

### 1.1 GPU 呈现与 GDI 绘制不能在同一 HWND 上并存

GPU 路线的交换链以 `SwapEffect.FlipSequential`（flip model）创建在顶层窗口的 HWND 上——`GpuSceneRenderer` 经 `CreateSwapChainForHwnd` 与 `MakeWindowAssociation` 把交换链绑定到窗口句柄，呈现通过 `Present` 提交。

flip model 的语义决定了：**一个 HWND 一旦成功完成过 flip 呈现，该 HWND 就不再支持普通 GDI 绘制**。这是窗口合成方式本身的属性，不是本项目的实现选择；它也不随交换链释放而回退——销毁交换链只释放 GPU 侧资源，不改变该 HWND 已经交给 flip model 呈现这一事实。

⇒ 同一个 HWND 上，"GPU 出过帧"与"GDI 能画出东西"是互斥的。退出 GPU 路线后若沿用原 HWND，GDI+ 自绘的内容不会出现在桌面上，窗口保持最后一帧 GPU 画面。

### 1.2 释放渲染资源不等于恢复 GDI 能力

停止 GPU 呈现只做两件事：停帧泵、释放 renderer 与交换链。这两件事都不会把 HWND 交还给 GDI。

⇒ 从 GPU 回到 legacy 的动作序列中**必须包含一次窗口句柄重建**：销毁被呈现过的 HWND、创建新 HWND，让新窗口从一开始就没有 flip model 呈现历史。新旧 HWND 是两个不同的原生窗口对象；托管层的 `Form` 实例、托管状态、订阅关系与布局计算结果全部保留。

### 1.3 决策：保留 Form 对象，按需重建 HWND

| 决策点 | 结论 | 理由 |
| --- | --- | --- |
| 窗口对象 | 保留 `Form` 实例，只重建其原生句柄 | 托管业务对象（采样、插件、托盘、历史）不得随句柄销毁重建 |
| 重建入口 | `MainForm.TryRecreateHandleForTransition` 内部直接调用 `protected` 的 `RecreateHandle()` | 反射调用受保护成员不是生产的可靠路径 |
| 重建时机 | 仅在呈现历史证明必要时 | 未创建交换链、未尝试呈现就退出的场景，句柄没有 flip model 历史，重建没有收益 |
| 重建次数 | 每代次至多一次 | 重建会打断窗口状态并产生可见闪烁 |

### 1.4 是否需要重建的判据是呈现历史，不是句柄数值

判定依据是**该代次的呈现历史**（`PresentationBindingState`）：

| 呈现历史 | 是否需要受控重建 | 理由 |
| --- | --- | --- |
| `Unbound` | 否 | 未创建交换链，HWND 没有 flip model 历史 |
| `Bound` | 是 | 交换链已创建并绑定到该 HWND |
| `PresentAttempted` | 是 | 首次呈现调用期间发生异常，HWND 状态无法确定，保守重建 |
| `Presented` | 是 | 该代次成功完成过 flip 呈现 |

**必须**用呈现历史判定，不得用 `newHwnd != oldHwnd` 判定：Windows 会复用原生句柄数值，两个不同的窗口对象可以拿到相同的 HWND 数值。判定"是否发生了重建"的唯一可靠凭据是**句柄代次**（见第 7 节）。

---

## 2. 模块边界与依赖方向

| 角色 | 位置 | 职责 | 不得承担 |
| --- | --- | --- | --- |
| 路线判定 | `src/Rendering/Configuration/RenderingRoutePolicy.cs`（`Decide`） | 纯函数：由主题声明与用户开关算出期望路由 | 不读系统状态，不创建 GPU 资源 |
| 切换状态与判据 | `src/UI/RenderingTransitionCoordinator.cs` | 状态推进、允许 / 禁止判定、失败归码 | 不做窗口操作，不触碰 HWND |
| 清理责任账本 | `src/UI/CapturePolicyCleanupLedger.cs` | 持有未完成的 affinity 恢复责任、退避重试、终结迁移 | 不持有宿主引用 |
| 呈现宿主 | `src/Rendering/Presentation/GpuPresentationHostController.cs` | 呈现会话、停止流程、移交清理责任、故障上报 | 不决定清理责任由谁承担 |
| 故障调度 | `src/Rendering/Presentation/GpuFailureFallbackScheduler.cs` | 故障报告的单槽排队与执行许可 | 不做窗口操作 |
| 切换编排 | `src/UI/UIController.cs` | 执行窗口副作用、串联切换步骤 | 不另立状态口径 |
| 受控重建薄入口 | `src/UI/MainForm_Transparent.cs`（`TryRecreateHandleForTransition`） | 置位判别位并调用 `RecreateHandle()` | 不做路线判定与状态推进 |

依赖方向：

- `src/Rendering` 不得依赖 `src/UI`；渲染层只消费不可变数据与 Win32 / DXGI 句柄。
- 协调器、账本、故障调度器都是**纯 CLR**（不引用 WinForms / DXGI），因此状态规则、判据与预算规则可被确定性单测覆盖，不依赖真实桌面与 GPU。
- Win32 与 WinForms 副作用只发生在 `UIController` 与 `MainForm` 上。
- 期望路由只有一个来源：纯函数 `RenderingRoutePolicy.Decide`。有效路由、切换阶段、诊断字段由协调器独占。

---

## 3. 状态模型

`RenderingTransitionState`（`src/Rendering/Configuration/RenderingTransitionState.cs`）是不可变 record，是切换状态的唯一落点。

### 3.1 状态维度

| 维度 | 字段 | 含义 |
| --- | --- | --- |
| 期望路由 | `ExpectedRoute` | 用户选择对应的路由（`RenderingRoute`） |
| 有效路由 | `EffectiveRoute` | 当前实际可呈现的后端 |
| 切换阶段 | `Phase` | `RenderingTransitionPhase` |
| 切换身份 | `TransitionId` | 每次进入一次切换递增 |
| 句柄代次 | `HandleGeneration` | 当前 HWND 代次 |
| 宿主代次 | `HostGeneration` | 每次新建呈现宿主递增；与句柄代次是两件事 |
| 请求版本 | `LastAppliedRequestVersion` | 已应用的路由请求版本 |
| 呈现历史 | `BindingState` | `PresentationBindingState` |
| 清理状态 | `CleanupState` | `CapturePolicyCleanupState` |
| 触发原因 | `Trigger` | `RenderingTransitionTrigger` |
| 首帧结果 | `FirstFrameOutcome` | `FirstFrameOutcome` |
| 首帧待定 | `PendingFirstFrame` | 隐藏窗口已完成切换但尚未拿到首帧证据 |
| 呈现根因 | `FailureCode` | GPU 呈现失败或切换失败的根因码 |
| 清理根因 | `CleanupErrorCode` | affinity 恢复失败的根因码 |
| 清理告警 | `CleanupWarningCode` | 信息级清理告警 |

### 3.2 切换阶段 `RenderingTransitionPhase`

| 取值 | 含义 |
| --- | --- |
| `Stable` | 无切换在进行；这是唯一允许拉起 GPU 呈现的阶段 |
| `Stopping` | 已进入切换，正在停止呈现与释放资源 |
| `RecreatingHandle` | 正在做受控 HWND 重建 |
| `Restoring` | 新句柄已就绪，正在恢复布局、窗口状态与首帧 |
| `Starting` | 回到 GPU 路线并启动呈现宿主的阶段 |
| `Failed` | 切换失败；失败原因由 `FailureCode` 给出 |

阶段只由协调器推进。`Stable` 之外的任何阶段都**不得**触发自动拉起 GPU 呈现。

### 3.3 呈现绑定状态 `PresentationBindingState`

| 取值 | 含义 |
| --- | --- |
| `Unbound` | 该代次未绑定交换链 |
| `Bound` | 交换链已创建并绑定到该 HWND |
| `PresentAttempted` | 已尝试呈现，结果尚未确认 |
| `Presented` | 已成功呈现 |

呈现历史**单调推进**：低级别状态不得覆盖高级别状态——宿主被替换或启动失败不代表 HWND 被替换。

### 3.4 捕获策略清理状态 `CapturePolicyCleanupState`

| 取值 | 含义 |
| --- | --- |
| `None` | 没有未完成的 affinity 恢复责任 |
| `Pending` | 有未完成责任，正在按退避表重试 |
| `Completed` | 责任已终结（原策略已写回，或该代次已失效） |
| `Failed` | 责任终结为失败（重试预算耗尽，或窗体关闭时仍未恢复） |

### 3.5 首帧结果 `FirstFrameOutcome`

| 取值 | 含义 |
| --- | --- |
| `NotAttempted` | 尚未尝试首帧 |
| `Succeeded` | 已拿到首帧证据 |
| `Failed` | 首帧失败 |
| `Unknown` | 结果不明（首次呈现调用期间发生异常的保守判定） |

`Unknown` 与 `PresentAttempted` 一样，属于"保守重建"的依据。

### 3.6 触发原因 `RenderingTransitionTrigger`

`UserThemeChange`、`UserGpuToggle`、`UserLiveToggle`、`UserWdaAcceptance`、`RuntimeFailure`、`HandleRecreated`、`VisibilityChanged`、`StartupGuardConservative`、`ExplicitRetry`、`DpiChanged`、`DisplayChanged`。

### 3.7 三个诊断字段互不覆盖

| 字段 | 承载什么 | 写入规则 |
| --- | --- | --- |
| `FailureCode` | 呈现根因（GPU 失败或切换失败） | 先到先得，不被后来的诊断顶替 |
| `CleanupErrorCode` | 清理根因（affinity 恢复失败） | 写它不得清空 `FailureCode` |
| `CleanupWarningCode` | 信息级清理告警（登记被拒、跨代次被取代、判别位残留等） | 写它不得清空 `FailureCode` |

**必须**：有效后端状态与清理告警要能同时呈现，故障根因不得被清理诊断顶替。

**必须**：非致命诊断不得进入主题校验的错误列表。`RenderingRoutePolicy.Decide` 在错误列表非空时会把路线整体降为 legacy，一条非致命诊断会让整个 GPU 路线失效。

---

## 4. GPU → legacy 的固定事件顺序

### 4.1 全局约束

- **所有窗口操作在 UI 线程执行。**
- **不得**在 `OnPaint`、窗口消息的句柄销毁回调、GPU 帧绘制栈内执行本流程的任何一步。故障可能发生在帧绘制栈内，因此故障上报只允许记录与入队，真正的转换由 UI 定时器回调这一安全点执行。
- **关闭拥有最高优先级**：窗体进入关闭过程后，不得重建 HWND、不得恢复 affinity、不得重新创建呈现宿主、不得发起新的切换。
- **重入保护**：一次切换进行中，新的切换请求不得嵌套执行。
- **不得递归应用主题**：布局恢复会触发主题重放，主题重放不得再次发起一次路由切换。
- **窗口状态的快照字段**固定为 `Bounds`、`ClientSize`、`DeviceDpi`、`Visible`、`WindowState`、`TopMost`、`ClickThrough`、`Opacity`。

### 4.2 步骤表

| 步 | 动作 | 失败后果 |
| --- | --- | --- |
| 1 计算新请求并进入保护 | 调用 `RenderingRoutePolicy.Decide` 得出期望路由；推进 `TransitionId` 与 `LastAppliedRequestVersion`；`Phase = Stopping`；记录触发原因 | 判定抛异常 ⇒ 记 `gpu_transition_decide_failed`，**保持原状态**，不进入 `Stopping`；同一 legacy 请求重复到达 ⇒ 幂等短路（不重建、不进入切换、返回相同稳定状态） |
| 2 记录窗口状态并停呈现调度 | 快照窗口状态；停呈现调度（停帧泵、停 live 捕获）；本代次的呈现历史写回状态 | 快照失败 ⇒ 记 `gpu_transition_window_state_snapshot_failed`，`Phase = Failed`，**不重建** |
| 3 停止捕获、释放资源、转交清理 | 停止呈现宿主：释放 renderer 与交换链；若 affinity 恢复失败 ⇒ 生成 `WindowCapturePolicyCleanup` 并移交账本；宿主引用无条件置空 | 恢复失败 ⇒ 记 `gpu_capture_policy_restore_failed`（清理根因），生成清理责任并移交；**不阻塞**新代次取得所有权 |
| 4 受控 HWND 重建 | 仅当呈现历史要求重建时执行：进入 `Phase = RecreatingHandle`；置位判别位；调用 `MainForm.TryRecreateHandleForTransition` 直接调用 `RecreateHandle()` | 重建失败 ⇒ 记 `gpu_presentation_handle_recreate_failed`，`Phase = Failed`，保留可控重试入口；**禁止**在未恢复的情况下宣称"已恢复"；**必须**清除判别位计数 |
| 5 原生生命周期与代次推进 | 由 `HandleCreated` 处理器完成：推进句柄代次（唯一递增点）、消费判别位、通知 live 捕获链与 GPU 链、刷新置顶与窗口轮廓 | 代次推进失败 ⇒ 记 `gpu_transition_generation_advance_failed`；构造期首次创建（判别位未命中）**不得**拉起 GPU |
| 6 恢复大小、DPI 与布局 | 按当前显示器工作区重新夹紧窗口位置；尺寸保留重建后的当前值；清除画笔缓存、标脏布局；GPU 主题套用 legacy 回退派生样式 | 失败 ⇒ 记 `gpu_transition_layout_restore_failed`，`Phase = Failed`，保留窗口状态快照供重试 |
| 7 恢复透明度、穿透、置顶、可见性 | 刷新置顶（强制重插）；仅在确实分层时写回 `Opacity`；按配置写回穿透；隐藏窗口保持隐藏 | 失败 ⇒ 记 `gpu_transition_window_state_restore_failed`，`Phase = Failed` |
| 8 一次有效重绘与窗口轮廓 | 重放窗口轮廓；显式 `Invalidate` + `Update` 触发一次有效重绘，不依赖普通采样刷新周期 | 可见窗口未拿到首帧证据 ⇒ 记 `gpu_transition_legacy_first_frame_timeout`，`Phase = Failed`；隐藏窗口 ⇒ `PendingFirstFrame = true` 且 `FirstFrameOutcome = NotAttempted` |
| 9 发布完成状态 | `Phase = Stable`；`EffectiveRoute = Legacy`；`ExpectedRoute` 保持用户选择；按配置恢复实时玻璃服务 | 无失败态；**禁止**在未拿到 legacy 首帧证据时宣称"已成功恢复" |

### 4.3 两条路径共用同一条转换入口

"用户主动切回 legacy"与"GPU 故障回退"共用步骤 2–9 的实现，差别只有两点：

- 步骤 1 的来源不同：前者调用 `RenderingRoutePolicy.Decide`；后者不调用判定函数，**期望路由保持用户选择不变**，只把有效路由落到 legacy。
- 故障路径必须**保留失败原因**：呈现根因写入 `FailureCode`，不得退化成普通的 fallback 原因码。

### 4.4 重建跳过路径

呈现历史为 `Unbound` 时跳过步骤 4 与步骤 5，直接进入步骤 6。此时窗口句柄没有 flip model 历史，GDI+ 路线可以直接在原 HWND 上恢复。

---

## 5. 错误码表

码值是稳定的契约面，不得改名。字段归属见 3.7：`FailureCode` 是呈现根因，`CleanupErrorCode` 是清理根因，`CleanupWarningCode` 是信息级告警。

### 5.1 切换协调

| 错误码 | 含义 | 落点 |
| --- | --- | --- |
| `gpu_transition_decide_failed` | 路线判定抛异常；保持原状态 | `FailureCode` |
| `gpu_transition_window_state_snapshot_failed` | 窗口状态快照失败；不重建 | `FailureCode` |
| `gpu_transition_generation_advance_failed` | 句柄代次推进失败 | `FailureCode` |
| `gpu_transition_layout_restore_failed` | 布局与 DPI 恢复失败 | `FailureCode` |
| `gpu_transition_window_state_restore_failed` | 透明度 / 穿透 / 置顶 / 可见性恢复失败 | `FailureCode` |
| `gpu_transition_legacy_first_frame_timeout` | 可见窗口未拿到 legacy 首帧证据 | `FailureCode` |
| `gpu_presentation_handle_recreate_failed` | 受控 HWND 重建失败 | `FailureCode` |
| `gpu_presentation_handle_recreate_not_observed` | 判别位超时未被消费 | `CleanupWarningCode` |
| `gpu_presentation_handle_recreate_stale_flag` | 判别位残留被清理 | `CleanupWarningCode` |

### 5.2 清理责任

| 错误码 | 含义 | 落点 |
| --- | --- | --- |
| `gpu_capture_policy_restore_failed` | 宿主停止时 affinity 恢复失败 | `CleanupErrorCode` |
| `gpu_capture_policy_restore_retry_exhausted` | 清理重试预算耗尽 | `CleanupErrorCode` |
| `gpu_capture_policy_cleanup_superseded` | 清理责任所属代次已被新代次取代 | `CleanupWarningCode`（信息级） |
| `gpu_capture_policy_cleanup_pending` | 同代次仍有未完成清理，正在按退避表重试 | 闸门阻断原因码 |
| `gpu_capture_policy_cleanup_unresolved` | 同代次清理已耗尽且原策略从未恢复 | 闸门阻断原因码 |
| `gpu_capture_policy_cleanup_registration_rejected` | 清理责任登记被拒（同代次已有非终态责任） | `CleanupWarningCode` |
| `gpu_capture_policy_cleanup_already_pending` | 明确恢复被拒：同代次已有非终态责任 | 明确恢复的拒绝码 |
| `gpu_capture_policy_cleanup_no_unresolved_entry` | 明确恢复被拒：该代次没有可重建的未解决条目 | 明确恢复的拒绝码 |
| `gpu_capture_policy_cleanup_rearm_failed` | 明确恢复被拒：新责任登记失败 | 明确恢复的拒绝码 |
| `gpu_presentation_capture_policy_restore_failed` | 呈现宿主侧的捕获策略恢复失败 | `CleanupErrorCode` |
| `presentation_capture_policy_failed` | 会话侧施加或恢复捕获策略失败 | `CleanupErrorCode` |
| `presentation_capture_policy_read_failed` | 读取窗口原始亲和性失败 | `FailureCode` |
| `presentation_capture_policy_restore_failed` | 会话侧恢复原始亲和性失败 | `CleanupErrorCode` |
| `presentation_window_invalid` | 目标 HWND 已失效 | `CleanupWarningCode` |

### 5.3 呈现会话生命周期

呈现会话有一个显式的生命周期状态机：停机 → 运行 → 挂起 → 停止中 → 停机完成。状态迁移顺序错误会产生下列错误码；它们描述的是**调用契约被违反**，不是渲染故障。

| 错误码 | 含义 |
| --- | --- |
| `presentation_session_already_active` | 会话尚未完全停止，不得启动第二个会话 |
| `presentation_session_not_active` | 会话不在活动状态，捕获策略不得变更 |
| `presentation_session_not_running` | 会话不在运行状态，操作被拒绝 |
| `presentation_session_not_suspended` | 会话不在挂起状态，恢复操作被拒绝 |
| `presentation_session_not_recreatable` | 会话状态不允许重建窗口 |
| `presentation_session_generation_not_advanced` | 新句柄不属于严格更新的句柄代次 |
| `presentation_stop_already_started` | 停止流程已经开始，不得重复发起 |
| `presentation_stop_not_started` | 未先发起停止流程，不得执行完成停止 |

### 5.4 呈现宿主与设备

| 错误码 | 含义 |
| --- | --- |
| `gpu_presentation_failed` | 呈现失败的兜底码（没有更具体的根因时使用） |
| `gpu_presentation_start_failed` | 呈现宿主启动失败 |
| `gpu_presentation_render_failed` | 帧渲染失败 |
| `gpu_presentation_resize_failed` | 交换链尺寸同步失败 |
| `gpu_presentation_recovery_exhausted` | 自动恢复预算耗尽；只有用户显式重试才能清除 |
| `gpu_presentation_handle_not_ready` | 主窗口句柄尚未就绪 |
| `gpu_presentation_suspended` | 处于挂起态的会话必须恢复而不是重新启动 |
| `gpu_presentation_not_reconfigurable` | 宿主当前不可就地重配置 |
| `gpu_presentation_invalid_material` | 材质无效 |
| `gpu_presentation_reconfigure_failed` | 就地重配置失败 |
| `gpu_presentation_fixed_background_failed` | 工具栏固定背景设置失败（装饰性失败，不触发降级） |
| `gpu_device_initialization_failed` | D3D11 设备创建失败 |
| `gpu_scene_renderer_initialization_failed` | 场景渲染器初始化失败 |
| `gpu_scene_renderer_invalid_request` | 场景渲染请求无效 |
| `gpu_capture_start_failed` | 实时背景捕获启动失败 |
| `gpu_fixed_background_failed` | 就地重配置时工具栏固定背景设置失败，重配置整体失败 |
| `gpu_resource_count_budget_exceeded` | 资源数量预算超限 |
| `gpu_resource_byte_budget_exceeded` | 资源字节预算超限 |
| `gpu_session_suspended` | 设备生命周期迁移到挂起态失败 |
| `gpu_session_stopping` | 设备生命周期迁移到停止态失败 |
| `gpu_startup_guard_previous_attempt_unclean` | 启动守卫发现上一次的启动尝试未被确认为干净 |
| `gpu_startup_guard_storage_unavailable` | 启动守卫的持久化位置不可用 |
| `gpu_startup_guard_marker_cleanup_failed` | 启动守卫标记清理失败 |

### 5.5 路线判定与主题

| 错误码 | 含义 |
| --- | --- |
| `gpu_effects_opt_in_required` | 用户未开启 GPU 材质渲染开关 |
| `wda_capture_policy_not_accepted` | 用户未接受窗口捕获排除政策 |
| `gpu_fallback_style_invalid` | legacy 回退样式字段非法；该字段退回默认值 |
| `diagnostic_window_is_layered` | 窗口处于分层状态（诊断条目，不改变路线） |
| `safe_mode` | 安全模式下强制 legacy 路线 |
| `rendering_configuration_invalid` | 主题渲染配置校验失败 |
| `rendering_configuration_absent` | 主题没有渲染配置段 |

---

## 6. 清理责任转交规则

### 6.1 所有权形状

GPU 呈现宿主在停止时如果无法把窗口的原始捕获亲和性写回，**必须**把这份责任移交出去，并**释放**自己。清理责任绝不通过"宿主引用是否为空"表达。

```
UI 生命周期所有者（UIController / 协调器）
│   持有：呈现宿主引用              ← 只表示"宿主是否活着"
│   持有：CapturePolicyCleanupLedger ← 独立持有全部未完成的清理责任
└─ GpuPresentationHostController
      职责：停止时若亲和性恢复失败 ⇒ 移交一条 WindowCapturePolicyCleanup 给账本
```

清理记录 `WindowCapturePolicyCleanup` 至少持有：被恢复的 HWND、所属句柄代次、原始 affinity 的精确值、是否由本程序修改、已尝试次数、失败码与最后错误消息、终结原因与告警码。

### 6.2 不变量

| 不变量 | 内容 |
| --- | --- |
| 宿主引用只表达存活 | 宿主引用非空 ⇔ 宿主资源活着；清理责任不得通过它表达 |
| 停止后引用无条件释放 | 停止流程结束后宿主引用必定为空，不存在"保留引用以延续清理责任"的早退路径 |
| 失败必产生责任记录 | 每次 affinity 恢复失败都必定产生一条清理责任记录（宿主释放前已恢复完成则不产生） |
| 同代次至多一条未完成责任 | 同一代次上至多存在一条非终态清理责任；重复登记同一条记录是幂等的，且不产生"登记被拒"诊断 |
| 关闭后全部终结且幂等 | 窗体关闭时全部未终结责任终结为终态；重复关闭不改变结果 |
| 取得所有权前必须查账本 | 新代次取得窗口捕获排除所有权前必须查询账本；同代次存在未完成清理时拒绝叠加新的所有者 |

### 6.3 移交顺序

1. 宿主停止时先释放对呈现会话的引用；
2. 由会话执行亲和性恢复，首次失败立即再试一次（这两次尝试计入清理预算）；
3. 两次都失败 ⇒ 生成 `WindowCapturePolicyCleanup` 并移交；
4. 移交出口有两条并行路径：事件 `CapturePolicyCleanupRequired` 与宿主属性 `LastCapturePolicyCleanup`。两条路径登记的是**同一实例**，账本对同一实例的重复登记是幂等的；
5. 登记被拒时必须留痕（`gpu_capture_policy_cleanup_registration_rejected`），不得静默丢弃——被拒的那条常常是把残留值误当作原始值的记录；
6. 宿主随后释放 renderer 与交换链，并释放自身引用。

### 6.4 清理状态机与重试预算

```
[无清理责任]
    │ 宿主停止，恢复成功
    └──────────────────────────────► [无清理责任]

[Pending] ◄── 移交 ─── 宿主释放
    │  ▲
    │  └─ 恢复成功
    ├─ 预算耗尽 ──────────► [Failed]（终结原因 exhausted）
    ├─ 所属代次被新代次取代 ► [Completed]（终结原因 superseded_by_new_generation，告警 gpu_capture_policy_cleanup_superseded）
    ├─ 所属 HWND 已销毁 ───► [Completed]（终结原因 window_destroyed）
    └─ 窗体关闭 ──────────► [Failed]（终结原因 form_closed）
```

- 尝试预算总计 **6 次**：第 1、2 次是宿主停止流程中的首次恢复与内联重试；第 3–6 次是账本按退避表重试，间隔依次为 **250 / 500 / 1000 / 2000 ms**。
- 退避未到不尝试 ⇒ 天然满足"禁止每帧重试"。账本由 UI 定时器驱动，只在到期时尝试。
- 预算耗尽只意味着"停止重试"，**不等于**"已安全恢复"：原始策略没有被写回 ⇒ 同代次不得叠加新的捕获排除所有者。
- 终态（`Completed` / `Failed`）不可回退：此后不再重试、不再触碰该 HWND；所有状态迁移方法在已终结时返回失败且不改动状态（幂等）。
- 本程序没有改过亲和性的责任不需要恢复，直接终结（终结原因 `not_modified_by_session`）。
- 关闭窗体时未完成的责任终结为失败终态（终结原因 `form_closed`），默认不发起新的恢复尝试。
- 由明确恢复重建出来的责任拥有自己的尝试预算（次数从 0 开始），不适用"第 1、2 次属于宿主停止流程"的补记。

### 6.5 闸门与明确恢复

| 闸门阻断原因码 | 含义 | 能否叠加新所有者 |
| --- | --- | --- |
| `gpu_capture_policy_cleanup_pending` | 同代次仍有未完成责任，正在按退避表重试 | 否 |
| `gpu_capture_policy_cleanup_unresolved` | 同代次责任已耗尽且原策略从未恢复（HWND 上仍可能残留排除标志） | 否 |

"明确恢复"是解除闸门的产品路径：它针对已耗尽且从未恢复的条目**重建一条新的清理责任**——沿用精确的原始亲和性、同一句柄、同一代次，尝试次数归零，立即到期。被接管的条目**保持失败终态**（如实记账：原策略始终没有被写回），只打上"已被接管"标记，因此它不再占用该代次的闸门，此后的写回由新责任完成。

明确恢复**只对 `gpu_capture_policy_cleanup_unresolved` 动作**，其它失败码一律不触发；同代次已有非终态责任时拒绝重建，避免条目堆积；恢复本身只做记账，不触碰 HWND，真正的写回由账本的退避重试完成。明确恢复由事件驱动的取得所有权尝试触发（用户切换主题、开关实时背景、DPI 变化等），**不得**在定时器回调或账本泵里自动触发。

---

## 7. 句柄代次规则

### 7.1 唯一真相源

`WindowHandleGeneration`（`src/Rendering/Configuration/WindowHandleGeneration.cs`）是进程内唯一的句柄代次真相源。

| 规格项 | 规定 |
| --- | --- |
| 实例 | `WindowHandleGeneration.Instance`，由类型自身持有；生产路径**不得**另行构造实例 |
| 初值 | `Current` 初始为 1 |
| 唯一递增点 | `Advance()` |
| 唯一驱动者 | `MainForm` 的 `HandleCreated` 处理器第一行 |
| 递增顺序 | 代次推进必须**先于**通知 live 捕获链与 GPU 链，保证两条链读到同一个新代次 |
| 只读消费方 | 协调器、呈现宿主、清理账本、live 捕获链都只读消费该实例，不得各自维护计数器 |

两份实例会让 GPU 清理链与实时捕获链读到不同的代次，跨代次丢弃判据恰好会在它要防的场景里失效。

### 7.2 事件顺序与单调性

- **一次受控重建必然先产生句柄销毁事件、再产生句柄创建事件**，两者严格有序，不得交叠。
- **句柄代次在句柄创建事件上单调递增，永不回退**：每个句柄创建事件对应且只对应一次 `Advance()`。
- 协调器接受新的句柄代次时要求它严格大于已记账的代次，且不得落后于真相源的当前读数；重复、过期或关闭期间的通知一律拒绝。
- 呈现会话在重建窗口时同样校验代次：新句柄必须属于严格更新的代次，否则拒绝并返回 `presentation_session_generation_not_advanced`，且不改写句柄与代次。

### 7.3 跨代次丢弃判据：代次先于窗口存活性

判定顺序不可交换：

1. 清理责任的代次**小于**当前代次 ⇒ 禁止对该句柄执行任何 Win32 调用（**包括** `IsWindow`）⇒ 直接终结（终结原因 `superseded_by_new_generation`，告警 `gpu_capture_policy_cleanup_superseded`）。
2. 清理责任的代次**大于**当前代次 ⇒ 同样禁止触碰（防御性判定）。
3. 只有代次**相同**时，才允许用 `IsWindow` 判断句柄是否仍然有效：无效 ⇒ 终结（终结原因 `window_destroyed`）；有效 ⇒ 允许按退避表重试。

理由：`IsWindow` 只回答"这个数值当前是否是一个窗口"，无法区分"HWND 已销毁"与"句柄数值被复用"。

### 7.4 回调丢弃判据

旧回调一律丢弃，三项判据缺一不可：

| 判据 | 丢弃什么 |
| --- | --- |
| 回调的 `TransitionId` ≠ 当前 `TransitionId` | 旧切换请求产生的回调 |
| 回调的句柄代次 ≠ 当前句柄代次 | 旧 HWND 代次的捕获与场景回调 |
| 回调的宿主代次 ≠ 当前宿主代次 | 旧宿主会话的回调（同一 HWND 上的重启） |

### 7.5 HandleCreated 判别位规格

`HandleCreated` 有两个来源，事件形态完全相同（都是句柄销毁后创建）：**构造期首次创建**与**受控重建后创建**。前者不得拉起 GPU 呈现，后者才允许走 GPU 重建路径，因此必须由一个判别位区分二者。

判别位载体在 `RenderingTransitionCoordinator`：

| 规格项 | 规定 |
| --- | --- |
| 载体 | 未消费请求**计数** + 请求号（`long`）；不使用布尔量 |
| 为何是计数 | 布尔量无法表达"已置位但重建未发生"的残留 |
| 置位者 | `MainForm.TryRecreateHandleForTransition`，先于 `RecreateHandle()` 调用置位 |
| 消费者 | `MainForm` 的 `HandleCreated` 处理器 |
| 请求号 | 每次置位自增，供配对与诊断使用 |
| 关闭优先 | 关闭过程中置位直接失败（返回 0），不得重建 HWND |

四条清除规则：

| 规则 | 触发条件 | 处理 | 记录 |
| --- | --- | --- | --- |
| 正常消费 | `HandleCreated` 到达 | 计数减一 | 记录被消费的请求号 |
| 异常取消 | `RecreateHandle()` 抛异常 | 计数与请求号一并归零 | — |
| 陈旧兜底 | 新一次置位时发现残留计数大于 0 | 先归零再置位 | `gpu_presentation_handle_recreate_stale_flag` |
| 超时兜底 | 置位后 **5 秒**内未被消费 | 归零 | `gpu_presentation_handle_recreate_not_observed` |

超时兜底由 UI 定时器驱动，只读时钟，不做任何 Win32 调用。

### 7.6 消费规则与拉起 GPU 的条件

`HandleCreated` 处理器的顺序：

1. 推进句柄代次（唯一递增点）；
2. 尝试消费判别位，得到 `controlled`；
3. `controlled == false`（构造期首次创建或非受控创建）⇒ **禁止**拉起 GPU 呈现；置顶刷新、窗口轮廓重放、live 捕获链通知照旧执行；
4. `controlled == true`（受控重建后创建）⇒ 允许进入 GPU 重建路径，但仍受启动条件约束。

拉起 GPU 呈现必须**同时**满足：

- 判别位命中（即这次创建来自受控重建）；
- `Phase == Stable`；
- 期望路由是 GPU 路线；
- 没有未终结的恢复预算耗尽记录（`gpu_presentation_recovery_exhausted`）。

定时器回调的启动兜底走同一组条件。

---

## 8. fallback 字段规则

### 8.1 载体

| 项 | 规定 |
| --- | --- |
| 位置 | `rendering.fallbackStyle`，是 `rendering` 段的**兄弟块** |
| 类型 | `ThemeFallbackStyle`（`src/Rendering/Configuration/ThemeRenderingConfig.cs`），六个业务字段全部是字符串（主题原生色值文本），**不得**改成 `JsonElement` |
| 保留 | 块内未知键由扩展数据承载，保存后保留 |
| 缺省写出 | 整块缺省时序列化不写出该键，旧主题的保存结果不变 |
| 既有 `rendering.fallback` | 类型与语义不变，`backend` / `material` 的校验规则一字不动 |
| 运行时契约 | 渲染契约层不扩展；解析器直接读主题配置 |

选择兄弟块而不是写进 `rendering.fallback` 的理由：`rendering.fallback` 是字典载体，新增项必然带 `JsonElement` 语义，克隆路径存在脱离风险；兄弟块让新增字段是纯 CLR 类型，同时保持既有字典与全部消费点不变。

### 8.2 字段清单

| 字段 | JSON 名 | 语义 | 缺省值 |
| --- | --- | --- | --- |
| `Background` | `background` | 背景色（页面底色） | `#1E2228` |
| `PanelBackground` | `panelBackground` | 分组底色 | `#282D35` |
| `Border` | `border` | 边框色 | `#3C444F` |
| `Text` | `text` | 主前景色 | `#F2F4F7` |
| `MutedText` | `mutedText` | 次要前景色 | `#B4BBC4` |
| `Accent` | `accent` | 强调色 | `#8FB4D9` |

缺省值是中性可读配色，不带主题色相，保证正文对比度不低于 4.5、次要文字不低于 3.0。

### 8.3 缺省与非法值

| 情形 | 处置 |
| --- | --- |
| 字段缺省 | 使用中性可读默认值，不产生诊断 |
| 字段存在但不是字符串 | 记诊断 `gpu_fallback_style_invalid`，该字段退回默认值 |
| 字符串无法被统一颜色解析器接受 | 记诊断 `gpu_fallback_style_invalid`，该字段退回默认值 |
| 整块不是 JSON 对象 | 记诊断 `gpu_fallback_style_invalid`，整块退回默认值 |
| 第三方 GPU 主题未声明该块 | 使用中性可读默认值 |

非法值诊断是**非致命**的：不进入主题校验的错误列表（否则整份主题被判不可用），也不覆盖已有的呈现根因。

### 8.4 派生主题

回退样式只用于 legacy 路线，落在**派生主题副本**上：源主题对象、用户 Skin、共享字体实例都不被修改，派生副本与源主题共享同一批 `Font` 实例（派生主题不负责释放共享字体）。

- 主题本身不是 GPU 主题 ⇒ **不得**套用任何回退配色，直接清空派生覆盖；
- 主题是 GPU 主题 ⇒ 解析回退样式并生成派生副本；
- 回退只承诺可读与可操作，不追求与 GPU 路线一致的折射效果；
- 指标选择、标签、字体、用户缩放、告警语义与进度值保持不变；
- 回退流程不得写盘，不得修改当前主题的共享已缩放对象。

---

## 9. 失败分级与恢复预算

### 9.1 分级

| 阶段 `GpuFailureStage` | 触发点 | 语义 |
| --- | --- | --- |
| `Startup` | 宿主 `Start()` 直接失败 | 尚未呈现过任何一帧 |
| `FirstFrame` | 首次呈现调用期间失败 | 呈现历史不明确 ⇒ 保守重建 |
| `Runtime` | 已成功呈现之后的渲染失败 | 已呈现过 ⇒ 退出必须重建 HWND |
| `RecoveryExhausted` | 运行故障的自动恢复预算耗尽 | 最终失败 |

### 9.2 处置

| 阶段 | 处置 |
| --- | --- |
| `Startup` | 交给统一调度入口排队；先给一次**延迟重试**机会（延迟数百毫秒后重建宿主再启动一次，覆盖切主题时旧设备尚未释放干净造成的瞬态失败）；重试预算用掉后仍失败 ⇒ 进入完整 legacy 转换 |
| `FirstFrame` | 同 `Startup`：先走一次延迟重试，再进入完整 legacy 转换 |
| `Runtime` | **不立即降级**。宿主保留既有的一次自动恢复：下一次启动尝试会消耗这一次恢复机会；恢复仍失败 ⇒ 报 `gpu_presentation_recovery_exhausted`，阶段判为 `RecoveryExhausted`，此时才进入完整 legacy 转换 |
| `RecoveryExhausted` | 记 `gpu_presentation_recovery_exhausted`，标记恢复预算耗尽（定时器回调与句柄创建事件都不得再自动拉起 GPU，只有用户显式重试才能清除），再进入完整 legacy 转换 |

### 9.3 恢复预算

| 预算 | 额度 | 消耗方 |
| --- | --- | --- |
| 宿主自动恢复 | 1 次 | 宿主在运行故障后的下一次启动尝试中消耗；耗尽后再失败即 `gpu_presentation_recovery_exhausted` |
| 延迟启动重试 | 1 次（用户主动切到 GPU 路线时授予） | 启动失败或首帧失败后延迟数百毫秒重建宿主再启动一次；用掉后失败即进入 legacy 转换 |
| 清理重试 | 6 次（250 / 500 / 1000 / 2000 ms 退避） | affinity 恢复，见 6.4 |

### 9.4 故障调度的三条规则

- 故障报告只允许**记录与入队**，不得在故障回调里直接做窗口操作——故障可能发生在 GPU 帧绘制栈内。
- 调度器是**单槽**结构：先到先得，后到的请求被合并（被合并次数单独计数）；最早的根因不得被后来的请求顶替。
- 执行许可：不在 `OnPaint`、句柄销毁回调、GPU 帧绘制栈内，且当前没有正在执行的转换。

### 9.5 故障回退的路由语义

故障回退**不得**把用户的 GPU 选择悄悄改写为关闭：期望路由保持用户选择不变，有效路由落到 legacy，故障根因承载在 fallback 原因码与 `FailureCode` 上。故障回退走的是与用户主动关闭 GPU 相同的步骤 2–9，因此两条路径的重建决策天然对称。

---

## 10. 一致性约束

| 约束 | 说明 |
| --- | --- |
| 不另立期望路由枚举 | 期望路由复用 `RenderingRoute`，判定复用唯一的纯函数 `RenderingRoutePolicy.Decide` |
| 不另立决策函数 | 故障回退不调用判定函数，而是直接进入一次切换并保持期望路由不变 |
| 不另立句柄代次计数器 | 全进程只有 `WindowHandleGeneration.Instance` |
| 路线判定的消费口径不变 | 期望路由的消费点保持"判定结果或当前期望路由为 GPU"的既有口径；有效路由只用于状态展示与诊断 |
| 诊断字段不混写 | 呈现根因、清理根因、清理告警三个字段互不覆盖 |
| 非致命问题不进主题校验错误列表 | 非法回退样式值、分层窗口诊断、清理告警都只进诊断通道 |
| 单一捕获所有者 | 同一时刻只允许一个捕获所有者：GPU 路线退出时先停 GPU 宿主，再恢复 legacy 路线的背景捕获 |
| 单一窗口轮廓写入者 | 窗口轮廓（DWM 角偏好与 Region）由窗口外观控制器唯一写入；切换流程只负责在正确的时机调用它 |
| 托管业务对象不随句柄重建重建 | 采样、插件、托盘、历史等托管对象与 HWND 生命周期解耦 |
| 主题对象不被回退流程写盘 | 回退只生成派生副本，绝不修改主题对象、当前主题的共享实例或任何写盘路径 |
| 渲染层不依赖 UI 层 | `src/Rendering` 不得引用 `src/UI`；状态与判据类型保持纯 CLR |

---

## 11. 已知限制

- **回退外观只承诺可读**。legacy 路线是 GDI+ 自绘，无法呈现 GPU 路线的折射与模糊效果；回退样式保证可读与可操作。
- **句柄重建会丢失部分原生窗口状态**。重建后必须按快照恢复位置、透明度、穿透、置顶与可见性；不属于快照语义的原生状态由各组件在 `HandleCreated` 之后重新绑定。
- **窗口尺寸不按快照还原**。切换可能伴随显示缩放变化，尺寸保留重建后的当前值，位置按当前显示器工作区夹紧，避免内容溢出窗口底部。
- **隐藏窗口的首帧证据延后**。窗口隐藏时切换完成但不宣称首帧成功，状态为"已切换、等待显示"。
- **清理预算耗尽不等于已恢复**。重试预算耗尽时原始捕获亲和性可能仍未写回，窗口上可能残留捕获排除标志；该状态由闸门码 `gpu_capture_policy_cleanup_unresolved` 表达，需要明确恢复来解除。
- **关闭过程不发起恢复尝试**。窗体关闭时未完成的清理责任直接终结为失败终态。
- **重建失败不自动无限重试**。重建失败后保留可控重试入口，需要显式重试才能再次尝试。
