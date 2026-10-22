# A15 —— 实时背景服务及后端（M2 捕获侧）验收

> 权威规格：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4，§3.4 帧所有权、§5 A15 交付要求）
> 冻结合同：[2026-10-20-appearance-m2-contract.md](2026-10-20-appearance-m2-contract.md)（帧来源 / 代次 / 所有权 / 取消 / 测量字段，§13 为 A15 实现裁定）
> 技术裁决：[2026-10-20-appearance-a06-glass-validation.md](2026-10-20-appearance-a06-glass-validation.md)（A06：唯一放行路线 = 屏幕捕获 + `WDA_EXCLUDEFROMCAPTURE`）
> 定位用 `文件路径 → 类型名 → 方法名`，**不引用行号**。
>
> 状态：**A15 交付完成，接线留待 A16**。存在 1 个**必须由用户拍板的前置决策**（DPI 感知，见 §6）。

---

## 1. 范围与基线

| 项 | 值 |
|---|---|
| 上游基线 | `02bc823`（N1/N2/加固项/M2 合同） |
| A15 提交 | `878d2cc`（后端 + 服务 + 适配器 + 合同 §13 裁定）、`215654d`（M2 合同探针） |
| A15 范围 | 捕获后端、图像处理、后台服务与帧交接、合同探针 |
| **不在 A15 范围** | 主面板接线、显式开关与首次提示、真实录屏验证（属 A16 / A17） |

A15 **不改动**任何既有渲染或 UI 文件：`src/UI/**`、`src/System/Backdrop/BackdropRenderer.cs`、
`src/System/Backdrop/IBackdropFrameSource.cs`、`src/System/Backdrop/BackdropStatus.cs`、
`src/System/Backdrop/BackdropModeResolver.cs` 均保持原样。

---

## 2. 交付文件与 API

### 2.1 新增文件（`src/System/Backdrop/`）

| 文件 | 类型 | 职责 |
|---|---|---|
| `BackdropCaptureNative.cs` | 静态互操作类 | 最小 Win32 层：显示亲和性读写、窗口几何、DPI 感知等级查询。**不含任何 DWM 材质入口**（A06 已实测 DWM 路线失败） |
| `BackdropImageProcessor.cs` | 静态类 + `BackdropBlurContract` / `BackdropProcessMetrics` | 模糊半径合同（物理 σ → 降采样缓冲上的 3× box 半径 r）、降采样/升采样、分段计量、缓冲预算前置校验 |
| `ScreenCaptureFrameSource.cs` | `ScreenCaptureFrameSource` + `BackdropCaptureRequest` / `BackdropFrameStamp` / `BackdropCaptureState` / `BackdropCaptureTiming` / `BackdropCaptureMetrics` | **捕获引擎**：桌面 `CopyFromScreen` 抓屏 + 瞬时排除；提供单次抓屏原语 `TryCaptureFrame` |
| `BackdropServiceRequest.cs` | `BackdropServiceRequest` / `BackdropServiceTiming` | 不可变捕获请求（物理像素 + 三个代次 + 帧率 + 降采样比例）与分段耗时结构 |
| `BackdropCaptureService.cs` | `BackdropCaptureService` / `BackdropFrameLease` / `BackdropCaptureStatus` / `BackdropServiceMetrics` / `IBackdropCaptureBackend` / `IBackdropFrameProcessor` | **后台服务**：租约式所有权转移、pending ≤ 1、代次校验、取消与亲和性还原、指标 |
| `BackdropCaptureAdapters.cs` | `ScreenCaptureBackendAdapter` / `BackdropBlurProcessorAdapter` | 把捕获引擎接到服务上的两个适配器（字段映射与所有权转移在注释中写明） |

### 2.2 A16 接线方式（唯一正确用法）

```
引擎（ScreenCaptureFrameSource，单次抓屏原语）
      │  ScreenCaptureBackendAdapter   ← 实现 IBackdropCaptureBackend
      ▼
服务（BackdropCaptureService，唯一的帧来源与唯一的后台循环）
      │  AppearanceRuntime(frameSource: captureService)
      ▼
面板（OnPaint：TryAcquireFrame → 绘制 → 释放【上一个】租约）
```

| 规则 | 原因 |
|---|---|
| **只**把 `BackdropCaptureService` 注册为 `IBackdropFrameSource` | 引擎与服务都实现该接口；注册错会出现**两个后台捕获循环**（重复抓屏，击穿 §6.3 的 CPU 预算） |
| **绝不**调用 `engine.Start(...)` | 同上；引擎只提供 `TryCaptureFrame` 单次原语 |
| `BackdropBlurProcessorAdapter` 作为 processor 传入服务 | 滤波（降采样 + 物理模糊）在服务侧发生，度量口径与服务一致 |
| 绘制时释放**上一个**租约，不是当前这一帧 | 合同 §4.2.2：旧帧释放必须发生在"没有任何 Paint 正在使用旧帧"之后 |
| 停止时调用 `Stop(timeout)` / `Dispose()` | 取消后**必须**恢复显示亲和性，否则面板会在录屏里永久消失（合同 §5.2.3） |

---

## 3. 合同逐条核对

| 合同条款 | 实现位置 | 判定 |
|---|---|---|
| §2.1 第一层：预览路径不访问后端 | `src/UI/AppearanceRuntime.cs` → `AppearanceRuntime.Update` → `ThemeAppearanceOptions.Create` | ✅ 探针 #2 实测后端调用次数 = 0 |
| §2.2.1 真实捕获后端 `IsPreview => false` | `ScreenCaptureFrameSource.IsPreview`、`BackdropCaptureService.IsPreview` | ✅ 探针 #1 |
| §3 三种代次只增不减、随请求携带、结果回带 | `BackdropServiceRequest`（主题/句柄/尺寸）、`BackdropCaptureRequest`、`BackdropFrameStamp` | ✅ 探针 #4 |
| §4.2.1 后台只发布 pending，绝不释放 UI 正在绘制的位图 | `BackdropCaptureService.PublishFrame` / `TryAcquireFrame` / `BackdropFrameLease.Dispose` | ✅ 探针 #4/#5 |
| §4.2.3 接管是一次性所有权转移 | `BackdropCaptureService.TryAcquireFrame` → `BackdropFrameLease` | ✅ 探针 #5（停止后存活位图归零） |
| §4.2.4 代次不匹配的帧由后台自己回收 | `BackdropCaptureService.PublishFrame` / `TryAcquireFrame` | ✅ 探针 #4（`FramesDroppedStale ≥ 1`） |
| §4.2.5 pending 最多一份 | `BackdropCaptureService.PublishFrame` | ✅ 探针 #5（260 次采样，峰值 = 1） |
| §4.2.6 失败路径必须释放中间位图 | `BackdropCaptureService.RunOneCapture` 各失败分支、`BackdropImageProcessor` 异常路径 | ✅ worker 行为断言 + 探针 #5 |
| §5.1 非 glass / 开关关闭 / 隐藏 / 关闭 ⇒ 不捕获 | 服务的 `Start` / `SetSuspended` / `Stop` 由**接线方**按 A16 规则驱动 | ⏳ **A16 实现**（A15 只提供能力） |
| §5.2.2 取消可等待但有上限 | `BackdropCaptureService.StopAsync` / `Stop` | ✅ 探针 #6（超时内完成） |
| §5.2.3 取消/失败后恢复显示亲和性 | `BackdropCaptureService.RestoreAffinitySafely` → `ScreenCaptureBackendAdapter.RestoreDisplayAffinity` → 引擎 | ✅ 探针 #6（后端 `RestoreDisplayAffinity` 调用 ≥ 1） |
| §5.2.4 失败按退避重试，不在 Paint 里重试 | `BackdropCaptureService.RecordFailure`；`TryGetFrame` 只读当前帧 | ✅ 探针 #3/#6 |
| §5.2.5 绘制路径绝不抓屏 | `BackdropCaptureService.TryGetFrame`（纯读） | ✅ 探针 #7（源码扫描，绘制路径零捕获调用） |
| §6.1 两种口径不混用 | `BackdropServiceMetrics`（CPU：`CpuMsTotal` / `CpuMsPerFrameAvg`）与经过时间（五段 + p95）分列 | ✅ 探针 #8 |
| §6.2 五段耗时并列记录 | `captureMs` / `filterMs` / `publishMs` / `waitUiMs` / `excludeWindowMs` | ✅ 探针 #8 |
| §6.3 缓冲预算有界 | `BackdropImageProcessor.MaxBufferPixels` / `MaxBufferBytes`（前置校验）+ **服务层闸门**（见 §5） | ✅ 探针 #9 |
| §7 降级不静默 | `BackdropCaptureStatus.DegradeReason` + `DegradeReasonText` + `LastFailureReason` | ✅ 探针 #3（降级场景带原因码与可读文本） |

---

## 4. §9 十条验收门槛

探针工程：`tests/AppearanceM2Probe/`（**不进 sln**，无新 NuGet 依赖，不做真实屏幕捕获，全部用假后端驱动）。

| # | 门槛 | 判定方式 | 结果 |
|---|---|---|---|
| 1 | 捕获后端 `IsPreview => false` | 直接读 `BackdropCaptureService` / `ScreenCaptureFrameSource` / 探针自带的固定图适配器 | ✅ |
| 2 | 预览路径后端调用次数 = 0 | 计数假后端 + `AppearanceRuntime(previewSource: 固定图)` | ✅ 计数 = 0，Source = `PreviewFrame` |
| 3 | `Source` 三值语义 | 真实帧 / 预览固定图 / 后端未就绪 / 后端无有效帧 四种场景 | ✅ `LiveFrame` / `PreviewFrame` / `Gradient`×2（均带原因码与文本） |
| 4 | 代次不匹配的帧被丢弃且被回收 | 闸门卡住服务 → 换三个代次 → 放行 | ✅ `FramesDroppedStale ≥ 1`，未被 UI 接管，停止后存活位图归零 |
| 5 | pending 最多一份 | UI 不取帧时连续产帧 | ✅ 260 次采样，pending 峰值 = 1，存活位图 ≤ 2，停止后 = 0 |
| 6 | 取消后恢复显示亲和性 | 假后端计数 `RestoreDisplayAffinity` | ✅ 停止后调用 ≥ 1 次 |
| 7 | 绘制路径无捕获调用 | 剔除注释与字符串字面量后扫描 `src/` 全部 129 个 `.cs` | ✅ 命中 6 处（5 处在允许目录 `src/System/Backdrop`，1 处是既有白名单 `TaskbarPage.cs`），违规 = 0 |
| 8 | CPU 与经过时间分别计量 | 反射断言两个口径的字段**同时存在且互为不同字段** | ✅ 并给出实测值（CPU 口径因 15.6ms 时钟粒度可能为 0，证据中写明"不可判定，不得据此断言经过时间也为 0"） |
| 9 | 缓冲预算有界 | 断言常量 + 喂 2000×2000 超限源图 | ✅ 超限被拒绝，临时缓冲 0 增长 |
| 10 | 无新增 NuGet 依赖 | `PackageReference` 集合逐项比对 | ✅ 恰好等于基线两项 |

证据：`docs/reviews/evidence/a14-9c16ad3/a15-m2-probe/m2-source-gate-evidence.json`、
`docs/reviews/evidence/a14-9c16ad3/a15-m2-probe/m2-runtime-gate-evidence.json`

---

## 5. 本轮加固（复核发现 → 已修）

| 缺口 | 后果 | 修法 |
|---|---|---|
| 服务在 `processor == null` 时**不做缓冲预算检查** | 会照单全收并发布超限帧，击穿 §6.3 的帧缓冲预算 | 服务层新增**发布前**闸门（`BackdropCaptureService.RunOneCapture`）：超预算即释放该帧并记为 `BackendFrameInvalid`，带可读原因；闸门与是否配置 processor 无关 |
| `SetState` 覆盖降级原因文本但保留原因码 | 状态里出现「原因码 = `BackendFrameInvalid`、原因文本 = 捕获服务已停止。」的**自相矛盾**，会让 A17 的支持矩阵按错误原因归档 | `SetState` 在非"降级/故障"状态下把**原因码与原因文本成对清空**；状态说明改用新增的 `BackdropCaptureStatus.StateText` 分列承载 |
| `RecordFailure` 直接改 `_state`、**绕过** `SetState`，因此不同步 `_stateText` | 进入 `Faulted` / `Degraded` 后，`StateText` 仍停在上一条状态文案（如"正在启动实时捕获……"），A16 接线后 UI 会显示与真实故障不符的状态说明 | `RecordFailure` 在同一把 `_metricsGate` 锁内同步 `_stateText = text`（失败说明本身即状态说明）；`SetState` 改为**无条件**覆盖 `_stateText`，杜绝旧文案残留 |

> 复核由 A15 两个 worker 互审 + M2 合同探针发现；修复由主集成者完成。

**加固项的回归覆盖**（`tests/AppearanceM2Probe` → `RuntimeGateRunner.RunBufferBudgetGate`）：

| 加固项 | 覆盖方式 | 断言 |
|---|---|---|
| 服务层缓冲预算闸门（`processor == null` 路径） | 门槛 #9 的第 4 段：**无处理器直通**（`new BackdropCaptureService(backend, processor: null, ...)`）喂 2000×2000 超限帧 | 发布数 = 0、`CaptureFailures ≥ 1`、降级原因码 ≠ `None`、原因文本非空、停止后存活位图 = 0 |
| 原因码/文本成对 + `StateText` 一致性 | 门槛 #3 的「后端不可用 / 后端无有效帧」两段：断言降级时原因码与文本**同时存在** | 原因码 ≠ `None` 且文本非空 |

> ⚠️ 门槛 #9 的第 4 段在加固**之前**只是 `c.Detail`（把缺口记录下来供复核，并没有断言），
> 加固落地后已升级为 `c.Require` 硬断言 —— 证据里 `rawPathWithoutProcessor.PublishedFrames` 应为 **0**。

### 5.1 独立复核发现的生命周期缺陷（2026-10-21 → 已修）

> 来源：`docs/reviews/2026-10-21-appearance-a15-independent-review.md`（对 `33245bc` 的独立复核）。
> 复核结论：**1 项 P1 + 5 项 P2**；P1 与前 4 项 P2 均用可控假后端**连续复现三次**，
> 而现有 10 条合同门槛**没有覆盖这些时序** ⇒ 本轮补了 4 条独立断言（见 §5.1.1）。

| # | 级别 | 缺陷 | 后果 | 修法 |
|---|---|---|---|---|
| S1 | **P1** | `Stop` 超时后能启动第二个线程；旧线程仍发布帧，并把新服务状态覆盖为 `Stopped` | 两个捕获循环并存 ⇒ 重复抓屏击穿 §6.3 的 CPU 预算；旧线程把新生命周期的状态写坏 | 引入 **per-run 上下文** `BackdropCaptureService.CaptureRun`（`RunId` + 本轮私有的 `Cts` / `StopRequested` / `Worker`）：取消标记、CTS、线程引用**下沉到本轮**；`Start` 在上一轮线程仍存活时抛 `InvalidOperationException` **拒绝重启**；`StopCore` 超时未退出时**保留** `_currentRun` 引用并如实记为「未能在超时内退出」（**不谎报** `Stopped`）；`PublishFrame` / `RecordSuccess` / `RecordFailure` 一律先校验轮次身份，旧轮次**不得**发布帧、不得改写新生命周期的状态与指标 |
| S2 | P2 | 已失效的 active 帧仍被借出；`Dispose` 后仍报告 `Glass` | UI 会拿到已被作废的位图；状态自相矛盾（已停却显示玻璃） | 新增**唯一**判定 `IsFrameBorrowableUnderLock()`（租约有效 + 未 Dispose + 未暂停 + 当前轮未请求停止），由 `TryGetFrame` / `IsReady` / `Status.liveFrame` **共用**；作废 / 停止 / 暂停后不再借出、不再报 `Glass` |
| S3 | P2 | 暂停期间在途帧仍被发布，并把状态改回 `Running` | 窗口不可见时仍抓屏并交付陈旧画面 | `PublishFrame` 在暂停时**丢弃并释放**在途帧（计入 `FramesDroppedStale`）；`RecordSuccess` 在暂停期间**不**把状态改回 `Running`；`TryAcquireFrame` 拒绝在暂停期接管残留 pending |
| S4 | P2 | 捕获恢复成功后 `StateText` 仍显示旧故障 | 状态说明与真实状态不符，A16 接线后 UI 会一直显示过期故障文案 | `RecordSuccess` 在成功转换时**同步** `_stateText = "实时捕获运行中。"`；历史故障仍保留在 `LastFailureReason`（不混入状态说明） |
| S5 | P2 | 实机启动脚本用 `DPI > 96` 判断 Per-Monitor-V2 | **双向误报**：System-Aware 进程（缩放 168）会被误判成「Per-Monitor 生效」；100% 缩放屏上的 PMv2 窗口（缩放 96）会被误判成「非 Per-Monitor」 | 改用官方 API：`GetWindowDpiAwarenessContext` + `AreDpiAwarenessContextsEqual` 与 `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2`(-4) 比较。`GetDpiForWindow` 仍读、仍展示，但明确标注为「**缩放数值**，不是感知等级」，**不参与判定** |
| S6 | P2 | 启动阶段的驱动安装对话框可能被识别成 `MainForm` | DPI 自检读到的是对话框而非主窗体，结论无效 | 主窗体标题设为**精确** `"LiteMonitor++"`（`FormBorderStyle.None` + `ShowInTaskbar = false`，**视觉零影响**）；脚本优先按标题**精确相等**命中，回退到「客户区面积最大」时**明确警告**可能是驱动安装对话框 |

**S1 修法的关键取舍**：Stop 超时后**拒绝重启**，而不是强制终止旧线程 ——
强制终止无法保证后端（`BitBlt` / `WDA` 调用）已经返回，强杀会留下不完整状态；
「拒绝重启 + 如实报 Degraded」让上层可以重试或提示用户，而不会静默地跑出两个捕获循环。

**S5 的判定依据**：`GetWindowDpiAwarenessContext` 返回的是**上下文句柄**（实测原始值 34 / 24592 / 43025 / 18），
**不是**枚举值 ⇒ 只能与常量做**等价比较**，不能直接比大小。

#### 5.1.1 新增的 4 条回归断言（探针）

新增用例**不占用**合同 §9 的 1–10 编号，以「A15 复核 S1–S4」独立命名（`tests/AppearanceM2Probe` → `RuntimeGateRunner`）：

| 用例 | 断言 | 反向验证 |
|---|---|---|
| `RunRunIsolationGate`（S1） | `Stop` 超时后 `Start` 抛 `InvalidOperationException`；旧轮次**不得**发布帧（`FramesPublished` 不增）、**不得**把状态改回 `Stopped`；假后端统计的**并发峰值必须 = 1** | ✅ 回退实现后**如期失败**（并发峰值 = 2、旧轮次 `FramesPublished = 1`） |
| `RunInvalidatedLeaseGate`（S2） | 租约作废 / 暂停 / 停止 / `Dispose` 之后：`TryGetFrame` 返回 false、`IsReady` = false、`Status.LiveFrameAvailable` = false、`EffectiveMode` ≠ `Glass` | ✅ 回退实现后**如期失败** |
| `RunSuspendedInFlightGate`（S3） | 暂停期间在途帧**不得**发布（`FramesPublished` 不增、pending 不残留）、状态**不得**被改回 `Running` | ✅ 回退实现后**如期失败**（pending = 1 且状态被改回 `Running`） |
| `RunRecoveryStateTextGate`（S4） | 失败后恢复成功 ⇒ `StateText` 必须是运行中文案，且 `LastFailureReason` **仍保留**历史故障 | ✅ 回退实现后**如期失败**（`StateText` 仍是失败文案） |

> ⚠️ **反向验证是必须的**：把实现回退到 `33245bc` 的版本后重跑探针，
> S1 / S2 / S3 / S4 **四条全部失败**，且复现了复核报告描述的全部现象 ⇒ 这些断言**不是假阳性**。
> 回退验证还额外暴露了复核报告未提及的连锁缺陷：旧实现下「上一轮未退出时再 `Start`」
> 会把服务**永久锁死**在「捕获服务已在运行」。

**探针总用例数：14** = 合同 §9 的 10 条门槛 + 4 条复核加固断言。

---

## 6. ⚠️ 必须由用户拍板的前置决策：生产进程的 DPI 感知

### 6.1 事实

| 项 | 实测/事实 |
|---|---|
| A06 验证捕获路线时用的探针 | `tests/AppearanceBackdropProbe` 的 csproj 写的是 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` |
| 生产主工程 | `LiteMonitor.csproj` **没有**该属性；`app.manifest` 的 `<windowsSettings>`（含 `dpiAware`）**整段被注释**；入口 `src/System/Program.cs` 调 `ApplicationConfiguration.Initialize()` ⇒ 实际感知等级 = **SystemAware(1)** |
| 捕获引擎的前置守卫 | 只接受 `PerMonitor(2)` / `PerMonitorV2(3)`；感知等级不足时**每一帧都放弃**并给出可读原因 |
| 本机显示器 | DISPLAY1 3840×2160 **@168 DPI**（系统 DPI 96），DISPLAY2 1440×2560 @120 DPI（X = −1440） |

⇒ **在当前生产配置下，液态玻璃一帧也抓不到**，面板会按合同降级为渐变/纯色并显示可读原因。

### 6.2 三个选项

| 选项 | 做法 | 收益 | 代价 / 风险 |
|---|---|---|---|
| **A（推荐）** | 主工程改为 Per-Monitor-V2：`LiteMonitor.csproj` 增 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`，`app.manifest` 的 `<windowsSettings>` 段启用 `<dpiAwareness>PerMonitorV2</dpiAwareness>` | 与 A06 的验证环境**完全一致**；抓屏坐标与物理像素严格对齐；面板在高 DPI 屏上按原生分辨率渲染（不再被系统拉伸变糊） | **全应用渲染行为变化**：窗口/字体/布局会按 168 DPI 重算，视觉上会明显变化；需要重跑 A14 实机清单（尤其第 4 项跨屏 DPI 往返）与全部外观探针 |
| **B** | 保持 SystemAware，放宽引擎守卫允许感知等级 = 1 | 改动面最小，不动全局渲染 | 只在**系统 DPI = 100%** 的机器上坐标与物理像素一致（SystemAware 按系统 DPI 缩放）；系统缩放非 100% 时抓到的是被系统缩放后的画面（清晰度下降），且**不满足 A06 的验证前提**；本机（系统 DPI 96）可正常出图 |
| **C** | 保持现状 | 零风险 | 玻璃在生产环境**永远不工作**，M2 的后续（A16/A17）无法做真实验收 |

> 三选一属于**产品级决策**（会改变用户看得见的画面），按约定不擅自替用户拍板。

### 6.3 ✅ 决策结果（用户，2026-10-21）：**选 A**

**决定：改为 Per-Monitor-V2。**

落地方式（与 A06 探针**完全同构**，只改一处、避免双机制打架）：

| 文件 | 改动 |
|---|---|
| `LiteMonitor.csproj` | 新增 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`（附完整理由注释）。WinForms SDK 生成的 `ApplicationConfiguration.Initialize()` 会在**创建任何窗口之前**调用 `Application.SetHighDpiMode(PerMonitorV2)`；`src/System/Program.cs` 的 `ApplicationConfiguration.Initialize()` 位于 `Application.Run(new MainForm())` 之前，前提满足 |
| `app.manifest` | **不动**那段注释掉的 `<windowsSettings>`，只加警告注释：旧的 `<dpiAware>true</dpiAware>` 是"系统感知"，且**清单声明优先于运行期 `SetHighDpiMode`**，一旦启用就会把感知等级压回 SystemAware ⇒ 玻璃再次失效 |

**预期画面变化**（供实机清单对照）：

| 项 | 改动前（SystemAware） | 改动后（Per-Monitor-V2） |
|---|---|---|
| 进程 DPI 感知 | SystemAware(1) | PerMonitorV2(3) |
| 168 DPI 主屏上的面板 | 系统整体拉伸 1.75 倍 ⇒ 偏糊 | 按 168 DPI 重新渲染 ⇒ 清晰，**物理尺寸基本不变**（`UIUtils.ScaleFactor` 随 `DeviceDpi` 变成 1.75，布局等比放大而不再被系统拉伸） |
| 外观层描边/切角/扫描线 | 跟随系统拉伸 | 按 `DeviceDpi/96` 重新换算的**物理像素**（A12 合同本就这么设计），逻辑尺寸不变 |
| 跨屏往返（清单第 4 项） | 换屏后仍是"被系统拉伸"的效果 | 换屏后**按目标屏 DPI 重新渲染**（DISPLAY2 @120 DPI） |

**由此产生的复验义务**（A15 收口时一并完成）：全部外观探针 + 单元测试复跑；A14 实机清单第 1~4、7 项需在**新 DPI 行为下**重做。

**复验完成情况（2026-10-21）**：

| 复验项 | 结果 |
|---|---|
| 主工程 Release 重建 | ✅ **0 错误 / 234 警告**（均为既有警告） |
| 源生成器产物 | ✅ 确认产出 `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)`，未被 `app.manifest` 压回 |
| 单元测试 | ✅ **583 / 583** 通过 |
| M2 合同探针 | ✅ **14 / 14** 通过（10 条合同门槛 + 4 条复核加固断言 S1–S4，见 §5.1.1） |
| 静态背景探针 | ✅ 21 / 21 |
| 主题探针 | ✅ 335 / 335 |
| 装饰 / 装饰渲染 / 窗口轮廓 / 接线探针 | ✅ 全部通过 |
| A06 环境探针 | ✅ DISPLAY1 3840×2160@168、DISPLAY2 X=−1440@120、HDR 10bpc 开启、非 RDP |
| A14 实机清单第 1~4、7 项 | ⏳ **需用户在交互会话重做**（本代理会话不合成到交互桌面） |

### 6.4 与其它未决项的关系

A14 实机清单第 4 项（跨屏 DPI 往返：装饰是否随 DPI 变化）在 SystemAware 下其实是"被系统整体拉伸"，
在 Per-Monitor-V2 下才是"按目标屏 DPI 重新渲染"——**选 A 会顺带改变这一项的验收含义**，建议一并复验。

---

## 7. 验证命令与退出码（主集成者串行执行）

| 命令 | 结果 |
|---|---|
| `dotnet build LiteMonitor.csproj -c Release` | 0 错误 / 234 警告（均为既有代码） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release` | 583 / 583 通过 |
| `dotnet build tests/AppearanceM2Probe/AppearanceM2Probe.csproj -c Release` | 0 错误 / 0 警告 |
| `tests/AppearanceM2Probe/output/AppearanceM2Probe.exe` | **14 / 14** 通过（10 条合同门槛 + 4 条复核加固断言 S1–S4），退出码 0 |
| `tests/AppearanceStaticBackdropProbe` | 21 / 21 通过 |
| `tests/AppearanceThemeCheck` | 335 / 335 通过 |
| `tests/AppearanceDecorProbe` | 全部通过（含纹理接管失败清理用例） |
| `tests/AppearanceDecorRendererProbe` | 全部通过 |
| `tests/AppearanceWindowShapeProbe` | 全部通过 |
| `tests/AppearanceWiringProbe` | 全部通过（回归证据：`docs/reviews/evidence/a14-9c16ad3/a15-regression/`） |
| `tests/AppearanceBackdropProbe --scenario env` | 环境证据已生成（`a15-regression/backdrop-env/environment.json`） |
| `tests/AppearanceBackdropProbe --out <目录>`（全场景） | 捕获后端 / 瞬时排除 / 模糊 / 阶跃边缘 / 性能场景全部完成，退出码 0（`a15-regression/backdrop/`） |
| `pwsh -File scripts/run-livecheck.ps1 -NoLaunch` | exit 0（构建 → 资源平铺 → 配置备份 → 输出目录，全链路通） |
| `[Parser]::ParseFile('scripts/run-livecheck.ps1')` | 语法错误数 = 0（S5/S6 改写后复测仍为 0） |
| 源生成器产物导出（`-p:EmitCompilerGeneratedFiles=true`） | 确认 `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` |

> **2026-10-21 复核修复的复验证据**：
> `docs/reviews/evidence/a14-9c16ad3/a15-lifecycle-regression/`（M2 14/14 的源码级 + 运行时门槛）
> 与 `docs/reviews/evidence/a14-9c16ad3/a15-regression/`（接线 / 装饰 / 装饰渲染 / 窗口轮廓 / A06 背景）。

---

## 8. 未验证项（不得被其它通过项覆盖）

| 条件 | 状态 | 归属 |
|---|---|---|
| **真实抓屏**（本代理会话不合成到交互桌面，窗口不出现在屏幕捕获里） | **未在本会话验证**；引擎侧已由 worker 在交互会话做过冒烟（自排除生效、亲和性还原、p95 达标） | A16 接线后由用户在交互会话复验 |
| **生产进程的 DPI 感知** | ✅ **已拍板并落地**（用户 2026-10-21 选 A，见 §6.3）：感知等级已由**生成代码实测确认** —— 源生成器产出 `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)`；但**实机行为**（高 DPI 屏上按 168 DPI 重绘、跨屏往返）需用户在交互会话看 | 用户（实机） |
| **实机启动脚本的「启动 + DPI 自检」段** | 脚本的**构建 → 资源平铺 → 备份配置**全链路已实测通过（`pwsh -File scripts/run-livecheck.ps1 -NoLaunch` → exit 0）。**S5/S6 改写后**已在替代场景实测：四种感知等级（DPI-Unaware / System-Aware / PMv1 / PMv2）逐一读对；同一进程跨屏的缩放数值（168 / 120）直接证明「缩放数值 ≠ 感知等级」；精确标题命中主窗、面积回退命中驱动安装对话框两条路径都验证过（脚本语法错误数 = 0）。但**真实 UAC 弹窗 + 真实 LiteMonitor 主窗 + 提权会话下的跨进程标题读取**这一段在本代理会话**无法完成**（需交互桌面 + 用户点 UAC） | 用户（交互会话） |
| **真实抓屏后端下 `Stop` 超时的行为** | 未在实机验证（需要后端真正卡住才能复现）；S1 的拦截与轮次隔离已由假后端探针覆盖（并发峰值 = 1、重启被拒、旧轮次不发布） | A16 接线后由用户复验 |
| 真实录屏（OBS / 游戏栏）下瞬时排除是否闪动 | 未验证 | A17 |
| Win10（任意版本）/ 真实 RDP / 锁屏恢复 | 未验证 | A17 |
| 负坐标屏（DISPLAY2，X = −1440）实抓 | 环境已确认（X = −1440、1440×2560 @120 DPI、`HasNegativeOrigin = true`），**实抓未做** | A17 |
| 玻璃后端 CPU 预算（≤40ms/秒）与 p95（≤30ms） | **M2 未接入主面板，无法测量** | A16 |
| 480×1576 / 480×3152 大档位的滤波 CPU 与 2fps 合计占用 | 未测（A06 §7 已标注该档位超预算） | A16 / A17 |
| 旧主题（10 个）逐像素一致 | ✅ 已由 A14 接线探针覆盖，本轮回归复跑通过 | — |

---

## 9. 交接给 A16 的清单

1. **先拿到 §6 的 DPI 决策**，否则真实验收无从谈起（已于 2026-10-21 拍板：选 A）。
2. 接线点：`AppearanceRuntime(frameSource: captureService)`；**只**注册服务，不注册引擎。
3. 生命周期：主题切到 glass 且开关打开 ⇒ `Start`；非 glass / 开关关闭 ⇒ `Stop`；
   窗口隐藏或最小化 ⇒ `SetSuspended(true)`；句柄重建 ⇒ 句柄代次 +1 并 `UpdateRequest`；
   尺寸变化 ⇒ 尺寸代次 +1 并 `UpdateRequest`；退出 ⇒ `Stop` + `Dispose`。
4. 绘制：`TryAcquireFrame` → 渲染 → 释放**上一个**租约。
5. 显式开关 + 首次提示：玻璃**默认关闭**；首次开启必须提示"捕获瞬间面板可能从录屏/截图中短暂消失或闪动"。
6. 预算验证：按 §6.2 口径分别记录 CPU 与经过时间；CPU 预算只认 CPU 口径。
7. **（S1 新增）`Start` 会抛 `InvalidOperationException`**：上一轮的后台任务尚未退出时（`Stop` 超时）不允许重启。
   A16 接线时**必须捕获这个异常**并给出可读提示（例如"上一次捕获任务仍在退出中，请稍候"），
   **不要**当成致命错误，也不要无脑重试 —— 反复重启正是 S1 要杜绝的。
   正常路径（上一轮已退出）不受影响。
