# 实时背景帧合同

本文描述 LiteMonitor++ 当前生效的实时背景帧合同：底图来源的判定规则与两层保险、代次的作用与推进时机、帧与位图的所有权归属、捕获的取消与生命周期、显示亲和性的作用域与 DPI 感知前置条件、测量口径与性能预算、降级链与状态可见性、默认关闭与首次提示，以及实时背景路线的成立条件、模糊半径合同与支持矩阵。

适用对象是在本仓库上开发、评审与排障的工程师。不在本文范围：面板几何与前景文字的绘制细节、主题 JSON 的业务字段语义、插件与 Web 服务协议、硬件采集算法。

---

## 1. 术语与角色

### 1.1 术语表

| 术语 | 含义 |
| --- | --- |
| **帧源**（frame source） | 提供"当前这一帧底图"的组件。仓库中存在两种接口形态：`IBackdropFrameSource`（借用语义）与 `IBackgroundFrameSource`（租约语义） |
| **宿主**（host） | 拥有帧源生命周期的一方。GDI+ 路线为 `LiveGlassController`，GPU 路线为 `GpuPresentationHostController` 与 `GpuSceneRenderer` |
| **代次**（generation） | 一组单调递增的计数器，标识"这份帧、这次会话属于哪一次主题修订、哪个窗口句柄、哪份客户区尺寸、哪块显示拓扑、哪个渲染设备" |
| **所有者**（owner） | 对某个资源负最终释放责任的一方 |
| **借用引用**（borrowed reference） | 可以读取但**绝不**释放的引用 |
| **租约**（lease） | 一次性的所有权凭证：`BackdropFrameLease`、`IBackgroundFrameLease`（实现为 `GpuDesktopFrameLease`） |
| **候选帧**（pending） | 后台已产出、尚未被接管的帧 |
| **活动帧**（active） | 当前被绘制路径借用的那一帧 |
| **显示亲和性**（display affinity） | 窗口的 `SetWindowDisplayAffinity` 属性，决定该窗口是否出现在屏幕捕获与录屏中 |

### 1.2 两条背景帧链

仓库中存在两条彼此独立的背景帧获取链。它们在命名空间、捕获 API、帧载荷类型、所有权模型与消费者上都不重合，**不共享帧缓冲，也不得互相回退到对方的帧类型**。

| 维度 | 链 A：`src/System/Backdrop/` | 链 B：`src/Rendering/Capture/` |
| --- | --- | --- |
| 命名空间 | `LiteMonitor.src.SystemServices.Backdrop` | `LiteMonitor.Rendering.Capture` |
| 捕获 API | GDI+ `Graphics.CopyFromScreen` | DXGI Desktop Duplication |
| 帧源抽象 | `IBackdropFrameSource` | `IBackgroundFrameSource` / `IBackgroundFrameLease` |
| 帧载荷 | `System.Drawing.Image` | `GpuDesktopFrameLease`（持有 `ID3D11Texture2D`） |
| 帧所有权 | **借用**引用；调用方与渲染器不得释放，释放责任在生产者一侧 | **单次所有权租约**；渲染器取得后自行释放 |
| 消费者 | `src/UI/LiveGlassController` → `src/UI/AppearanceRuntime` → `src/UI/Decor` | `src/Rendering/Presentation/GpuSceneRenderer` |

链 A 的**背景绘制部分**（`BackdropRenderer` 的 solid / gradient / glass 静态层、噪点层、边缘辉光层与降级链）承担 GDI+ 路线的底图职责；链 A 的**实时捕获部分**（`ScreenCaptureFrameSource`、`BackdropCaptureService`）在产品界面上没有开启入口——唯一开关 `Settings.LiveGlassEnabled` 在每次配置加载时被归零，界面不提供该开关。链 A 的背景绘制与降级契约因此仍然生效，其捕获与所有权代码构成链 B 之外的独立实现与契约参照。

链 B 承担 GPU 路线下唯一的实时桌面背景职责。

### 1.3 依赖方向

- **合同层不得携带可变对象。** `src/Rendering/Contracts/` 中的类型不得携带 `Control`、可变指标集合、GDI 画刷或捕获资源。
- **渲染层不得依赖 UI 层。** `src/Rendering` 只消费不可变数据与 Win32 / DXGI 句柄。
- **帧源不得被 UI 直接创建。** 帧源由宿主创建并持有，UI 只经由宿主读取诊断。

---

## 2. 帧来源规则

### 2.1 底图模式的取值域

链 B 的底图模式由 `SceneBackgroundMode` 表达，编码在 `MaterialFrameConstants.State.X`，**不得重排**：

| 值 | 含义 |
| --- | --- |
| `Procedural = 0` | 程序化背景：着色器自行合成渐变、极光、网格与噪声，**不涉及任何桌面捕获** |
| `FixedTexture = 1` | 固定纹理：由 `FixedBackgroundInput` 承载的确定性 BGRA8 输入 |
| `LiveCapture = 2` | 实时桌面捕获：DXGI Desktop Duplication 交付的桌面纹理 |

链 A 的底图由两组枚举表达：

- `BackdropRenderMode`：`Solid`（沿用调用方的实心背景填充）、`Gradient`（自绘渐变）、`Glass`（实时材质）。
- `BackdropFrameSource`：`None`、`Legacy`、`Gradient`、`PreviewFrame`（预览固定源）、`LiveFrame`（实时后端帧）。

### 2.2 程序化或固定背景与实时桌面捕获的选择条件

**链 B 的选择条件**（由 `GpuSceneRenderer` 在每次绘制时按自身持有的状态判定）：

| 条件 | 生效底图模式 |
| --- | --- |
| 材质为 `CyberPunk` | 恒为 `Procedural`。赛博朋克材质**不创建**桌面捕获源，`TryStartLiveCapture` 返回 `capture_material_forbidden` |
| 固定背景已装载且当前绘制未取得实时帧 | `FixedTexture` |
| 路线为 `GpuLiveBackground`、材质为 `LiquidGlass`、捕获会话存在且当次取帧取得租约 | `LiveCapture` |
| 其余 | `Procedural` |

两条硬约束：

- **实时捕获优先于固定纹理。** 固定纹理先被选为底图，随后的实时捕获成功时覆盖它。
- **固定纹理的装载是显式动作。** `IFixedBackgroundTarget.TrySetFixedBackground` 的契约规定实现只在该显式调用时上传像素，`ClearFixedBackground` 时释放。底图模式因此来自渲染器自身持有的状态，而**不是**从外部配置推断出来的。

**链 A 的选择条件**（由 `BackdropModeResolver.Resolve` 判定）：

| 主题请求的模式 | 底图 |
| --- | --- |
| `solid` | 无底图，沿用 solid 背景路径 |
| `gradient` | 自绘渐变 |
| `glass` 且底图可用 | 实时帧或预览固定源 |
| `glass` 且底图不可用 | 按 §7.1 的降级链回退 |

### 2.3 两层保险

底图来源的归属必须可被诊断证明，仓库用两层保险保证这一点。

**第一层 · 结构保险（首选）**：预览固定源通过 `ThemeAppearanceOptions.PreviewSource` 进入快照，背景层**根本不注册帧后端**。

- `options.PreviewSource != null` ⇒ 背景层**不访问后端**，后端调用次数为 0，来源报 `PreviewFrame`。
- 这是结构性保证，不依赖任何人记得填对字段。
- 编辑器的预览路径完全依赖这一层：`ThemePreviewControl` 以 `new AppearanceRuntime(previewSource: …)` 构造外观运行时，从不注入帧后端。
- 主窗口**不得**传入预览固定源——传入后玻璃背景将恒为固定图。

**第二层 · 元数据保险（兜底）**：`IBackdropFrameSource.IsPreview` 由帧提供者自己声明。

- 真实捕获后端必须返回 `false`；固定图提供者必须返回 `true`。
- 即使固定图被包装成帧后端注入，诊断也不会把它记成实时帧。
- 链 B 的对应形态是 `IBackgroundFrameLease.IsPreview`：`GpuDesktopFrameLease.IsPreview` 恒为 `false`。

两条链的共同结论：**"来源是实时帧"必须由帧提供方自己声明或由结构路径证明，绝不由第三方推断。**

### 2.4 禁止的捷径

- **不得**把来源直接写死为实时帧，或按"有没有后端"猜测来源。
- **不得**在编辑器预览中注册真实捕获后端——那会让预览变成隐式的桌面抓取。
- **不得**用"帧非空"作为"这是实时帧"的判据。
- **不得**用"帧年龄为零"或"本绘制周期刚取过帧"作为来源判据。
- **不得**让编辑器预览与主面板共享同一套来源判定：预览固定源与实时帧必须能被区分到诊断级别。
- **不得**用主窗口语境的原因码描述预览场景，反之亦然。

---

## 3. 代次规则

### 3.1 代次集合

| 链 | 代次载体 | 代次项 |
| --- | --- | --- |
| 链 A | `BackdropServiceRequest` | `ThemeGeneration`（主题修订）、`HandleGeneration`（窗口句柄）、`SizeGeneration`（客户区尺寸） |
| 链 B | `RenderGenerations` | `Theme`（主题修订）、`Window`（窗口句柄）、`Size`（客户区尺寸）、`Topology`（显示拓扑）、`Device`（渲染设备） |

`RenderGenerations.CanAccept` 要求五项**全部相等**，任一项不匹配则该帧不得被接受。`RenderGenerations.HasSameSessionIdentity` 只比较 `Window` / `Size` / `Topology` / `Device`——主题修订可以在同一呈现会话内变化，而窗口、尺寸、拓扑或设备变化必须走重建或 Resize，不得只改配置。

`BackdropServiceRequest.MatchesGeneration` 比较三项代次，链 A 的请求在交接时携带其发起时的代次快照。

### 3.2 作用与推进时机

| 代次 | 推进时机 | 作用 |
| --- | --- | --- |
| 主题代次 | 换肤时自增（`AppearanceRuntime.OnThemeChanged`） | 装饰层与背景层纹理缓存失效；`AppearanceRuntime.Update` 在主题引用、DPI 比例、用户缩放、客户区矩形任一变化时统一触发背景层失效 |
| 窗口句柄代次 | 句柄被销毁并重建时由 `WindowHandleGeneration.Advance` 推进，**唯一递增点** | 捕获结果归属校验：前代句柄的结果必须丢弃；前代句柄上施加的显示亲和性不被新句柄继承 |
| 尺寸代次 | 客户区尺寸变化时自增，与句柄代次相互独立 | 前代尺寸的帧必须丢弃 |
| 显示拓扑代次 | 显示器接线或窗口跨屏时变化 | 捕获输出必须重新选择 |
| 设备代次 | 渲染设备重建时变化 | 前代设备的帧不得在新设备上呈现 |

**句柄代次必须是进程内唯一的。** `WindowHandleGeneration.Instance` 是唯一真相源，`Advance()` 是唯一递增点。生产路径**不得**另行构造 `WindowHandleGeneration` 实例——两份实例会让 GPU 清理链与实时捕获链读到不同的代次，跨代次丢弃判据恰好会在它要防的场景里失效。

### 3.3 失效场景

| 场景 | 链 A 的处置 | 链 B 的处置 |
| --- | --- | --- |
| 代次不匹配的候选帧 | 由发布方（后台）自行回收，**不交给 UI**，并计入 `FramesDroppedStale` | `Acquire` 直接返回 `StaleGeneration` 与 `capture_generation_mismatch`，不产生租约 |
| 代次变化时已接管的帧 | `BackdropFrameLease.Invalidate` 作废——**只作废、不释放**，位图所有权仍在 UI 手里 | 前代租约由渲染器在绘制结束时释放；捕获源的代次由 `UpdateGenerations` 更新 |
| 句柄重建 | 停止该句柄的捕获，恢复显示亲和性，在新代次上重新开始 | `PresentationSession.RecreateWindow` 要求新句柄属于**严格更新**的句柄代次，否则返回 `presentation_session_generation_not_advanced` |
| 尺寸变化 | 前代尺寸的帧被丢弃，按新尺寸重建请求 | 走 `GpuSceneRenderer.Resize`，尺寸相关资源在两段式流程中重建 |
| 显示拓扑变化 | — | 重新选择 DXGI 输出；同屏捕获失败后不重复尝试，换屏时自动放行再试一次 |

### 3.4 跨代次判定顺序

**代次判定必须先于窗口存活性判定。** `IsWindow` 只回答"这个数值当前是否是一个窗口"，无法区分"前代句柄已销毁"与"句柄数值被复用"。`CapturePolicyCleanupPolicy.Judge` 的判定顺序因此固定为：

1. 清理记录的代次**小于**当前代次 ⇒ 禁止对该 HWND 执行任何 Win32 调用（**包括 `IsWindow`**），直接终结（`SupersededByNewGeneration`）。
2. 清理记录的代次**大于**当前代次 ⇒ 同样禁止触碰（`GenerationMismatch`）。
3. 代次**相等** ⇒ 此时才允许用 `IsWindow` 判活：为假即 `WindowDestroyed`，为真则按重试预算继续。

---

## 4. 所有权

### 4.1 位图所有权表

**链 A**

| 对象 | 谁创建 | 谁释放 | 规则 |
| --- | --- | --- | --- |
| 候选帧（pending） | 后台服务（经 `IBackdropCaptureBackend`） | 后台服务（未被接管时） | 同一时刻**最多一份** |
| 活动帧（绘制中使用） | 后台服务创建 → UI 经 `TryAcquireFrame` 接管 | **UI**（`BackdropFrameLease.Dispose`） | 绘制期间任何人不得释放 |
| 被丢弃的过期帧 | 后台服务 | 后台服务（发布方） | 代次不匹配的帧由发布方回收，不交给 UI |
| 捕获后端产出的中间位图 | 捕获后端 | 随所有权转移交给调用方；失败路径必须一并交出 | 捕获后端创建位图，因此它把所有权交出去 |
| `options.BackgroundFrame` / `options.PreviewSource` | 外部 | 外部 | **纯借用**；options 与渲染器都不释放 |
| 噪点缓存与装饰缓存里的位图 | 缓存 | 缓存 | 见 §4.3 |

**链 B**

| 对象 | 谁创建 | 谁释放 | 规则 |
| --- | --- | --- | --- |
| 捕获纹理 `ID3D11Texture2D` 与其 SRV | `DesktopDuplicationFrameSource` | 捕获源（`Dispose`、尺寸变化时重建） | 纹理是捕获源的私有资源，租约不转移其所有权 |
| 租约 `GpuDesktopFrameLease` | 捕获源 | 渲染器（绘制路径的 `finally` 中释放） | 释放租约只解除占用，不释放纹理 |
| 固定背景纹理 | `GpuSceneRenderer` | 渲染器（`ClearFixedBackground` 或被替换时） | 与 `FixedBackgroundInput` 的像素副本分离，输入的像素归输入所有 |
| GPU 资源账本条目 | `GpuResourceLedger.Reserve` | `GpuResourceLease.Dispose` | 记账与释放指向同一份资源 |

### 4.2 核心规则

1. **后台只发布候选帧，绝不释放 UI 正在绘制的帧。**
2. **接管是一次性的所有权转移。** 转移之后后台对该帧**无任何权利**，永远不得释放它。
3. **UI 只在没有绘制使用前一帧之后才释放过期租约**，不得在绘制过程中释放。
4. **代次不匹配的帧由发布方自行回收**，不得丢给 UI；UI 也不得接收。
5. **候选帧最多一份。** 新帧产生时若上一份候选帧尚未被接管，发布方先释放它再发布新的；帧堆积会让内存占用随帧率线性增长。
6. **失败路径必须释放。** 捕获失败、滤波失败、取消、句柄失效、超时都要释放"已创建但未发布"的中间位图。
7. **渲染器绝不释放借用资源。** 借用清单见 §4.4。
8. **链 B 同一时刻只允许存在一个活动租约。** 捕获源在已有活动租约时拒绝新的取帧请求，返回 `capture_lease_in_use`。
9. **同一时刻只允许存在一个捕获所有者。** GPU 路线退出时必须先停止，再恢复 GDI+ 路线的背景捕获；两个捕获所有者并存会重复抓屏，击穿 CPU 预算。

### 4.3 缓存资源所有权

- 装饰层缓存 `DecorResourceCache` 同时**拥有**源位图与画刷，淘汰时两者一起释放。
- 背景层噪点缓存 `BackdropNoiseCache` 同样拥有其位图。
- **尚未入表时**，待接管的位图与画刷由当次调用以 `try/finally` 兜底释放；**只有入表成功才提交所有权与字节计数**。记账与释放必须指向同一份图像。
- GPU 路线的资源由 `GpuResourceLedger` 统一记账：字节预算 1 GiB、资源条数预算 256。超预算的预留被拒绝并计入 `RejectedReservations`。帧率达标但账本持续逼近预算属于不可接受的结果。

### 4.4 借用引用清单

下列引用可被读取，**绝不**由消费方释放：

- `ThemeAppearanceOptions.BackgroundFrame`
- `ThemeAppearanceOptions.PreviewSource`
- `IBackdropFrameSource.TryGetFrame` 返回的帧
- `IBackdropFrameProcessor.TryProcess` 的 `source` 参数（借用输入；输出 `processed` 才是调用方的新所有权，除非与输入是同一实例）
- `IBackgroundFrameLease` 所引用的捕获纹理

---

## 5. 取消与生命周期

### 5.1 何时不捕获

| 条件 | 行为 |
| --- | --- |
| 主题不是 glass（gradient / solid） | 不启动服务 |
| 玻璃开关未开启（默认关闭） | 不启动服务 |
| 窗口隐藏 / 最小化 | 暂停捕获 |
| 窗口关闭 / 应用退出 | 停止服务并释放全部原生资源 |
| 句柄失效 | 停止该句柄的捕获，恢复显示亲和性 |
| 暂停后恢复可见 | **合并为一帧**：立即捕获一次，不补历史帧、不追帧 |

链 B 的对应机制是呈现会话的 `Suspend` / `Resume`：窗口隐藏时 `PowerPolicy.ResolveDisplay` 把显示帧率降为 0，呈现暂停而不销毁会话。

### 5.2 取消协议

1. **单一取消令牌。** 链 A 的每一段运行周期持有一个 `CancellationTokenSource`（`CaptureRun`）。取消标记、CTS 与线程引用必须**属于该段运行**，不得挂在服务实例上——否则新一轮的启动会清零标记，上一段运行的线程便畅通无阻地继续发布帧并覆盖新生命周期的状态。
2. **取消必须可等待但有上限。** 链 A 的 `StopAsync(timeout)` 有等待上限，UI 侧的 `LiveGlassController` 使用 2 秒上限；退出超时必须如实报告"尚未退出"，**不得**谎报已停止。
3. **同一服务实例内不得出现两个捕获循环。** 上一段运行的后台任务尚未退出时，启动必须**拒绝**并抛出 `InvalidOperationException`，不得"再起一个线程让它自己退出"。
4. **取消或失败后必须恢复显示亲和性。** `SetWindowDisplayAffinity` 的排除属性必须还原，否则窗口在录屏中持续缺席。
5. **失败后受控重试。** 重试带退避（起始 500 ms，上限 5000 ms），**不在绘制路径里重试**。
6. **绘制路径绝不启动抓屏。** 链 A 的 `TryGetFrame` 只读当前帧；绘制路径中出现 `CopyFromScreen`、`BitBlt` 一类调用即为设计错误。链 B 的绘制路径调用 `DesktopDuplicationFrameSource.Acquire` 取帧（DXGI `AcquireNextFrame` 加 GPU 拷贝），而**捕获会话的创建**只发生在 `TryStartLiveCapture`（首次或显示器变化时），不在每帧路径上。

### 5.3 显示亲和性的作用域

`WDA_EXCLUDEFROMCAPTURE`（`0x00000011`）是**窗口级**的：它排除的是**整个顶层窗口**，不是窗口内的某个矩形。

- 链 A 在抓屏期间**瞬时**施加该属性、抓完立即还原，以避免面板把自身捕获进去形成自反馈。
- 链 B 由 `PresentationSession` **持续**持有该属性，停止时恢复**精确原值**；原值与"读到它的那个句柄"必须绑定，禁止把前代次的原值带到新句柄。
- 排除范围**不得**由本程序自行扩大到其它窗口。
- 需要"只排除面板背景、保留文字"的产品形态，当前 Win32 API 做不到，必须另有方案。

恢复责任的所有权形状：

1. `PresentationSession.CreateCapturePolicyCleanup` 产出一条 `WindowCapturePolicyCleanup` 记录，代表存在未完成的清理责任。
2. `GpuPresentationSessionOwner.Stop` **无条件**释放宿主对会话的引用——释放自己是义务而非可选项；会话引用非空**绝不**用于表达清理责任。
3. 恢复第一次失败时立即再试一次；两次都失败时，清理责任被**移交**为 `WindowCapturePolicyCleanup` 记录。
4. 移交后宿主**不得**再触碰该 HWND；后续恢复、退避重试与终结迁移由清理账本 `CapturePolicyCleanupLedger` 负责。
5. 清理重试预算为 **6 次**，退避间隔依次为 **250 / 500 / 1000 / 2000 ms**；预算耗尽的原因码为 `gpu_capture_policy_restore_retry_exhausted`。
6. 同一代次仍有未完成的清理时，宿主不得叠加新的捕获排除所有者。

### 5.4 DPI 感知前置条件

链 A 走 GDI+ `Graphics.CopyFromScreen` 按**物理坐标**取屏。`ClientToScreen` 在 DPI-unaware 进程返回被系统虚拟化的坐标，而 `CopyFromScreen` 按物理坐标取屏，两种口径不一致会导致抓到的区域与窗口错位。

- `BackdropCaptureNative.HasPhysicalScreenCoordinates` 只接受 `DPI_AWARENESS_PER_MONITOR`（2）与 `DPI_AWARENESS_PER_MONITOR_V2`（3）。其它感知等级下捕获守卫逐帧放弃捕获，玻璃路径不产出帧。
- 生产进程的感知等级由 `LiteMonitor.csproj` 的 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` 决定；WinForms SDK 生成的 `ApplicationConfiguration.Initialize()` 在创建任何窗口前调用 `Application.SetHighDpiMode(PerMonitorV2)`。
- **`app.manifest` 中的 `<dpiAware>` 段必须保持注释状态。** 清单声明优先于运行期的 `SetHighDpiMode`，一旦启用 `<dpiAware>true</dpiAware>`（系统感知）会把进程的感知等级压回 SystemAware，使抓屏路线一帧也拿不到。
- 客户区在屏幕坐标系里的矩形**必须**是物理像素，且**允许为负**——副屏位于主屏左侧时客户区原点为负是真实场景。

---

## 6. 测量口径

### 6.1 两种耗时口径

| 口径 | 测什么 | 用什么测 | 判据用途 |
| --- | --- | --- | --- |
| **CPU 时间** | 处理器时间（不含等待与抢占） | `Process.TotalProcessorTime` 差值 | **CPU 预算**（≤ 40 ms/秒） |
| **经过时间** | 墙钟（等待与抢占都算在内） | `Stopwatch` | **延迟判据**（p95 ≤ 30 ms） |

**两种口径不得混用。** CPU 预算只认 CPU 口径，延迟判据只认经过时间口径。

### 6.2 必录的分段耗时

链 A 的 `BackdropServiceMetrics` 提供：

| 分段 | 含义 |
| --- | --- |
| `CaptureMs` | 捕获（含后端自身开销） |
| `FilterMs` | 降采样与物理模糊半径滤波 |
| `PublishMs` | 发布候选帧（含代次校验） |
| `WaitUiMs` | **等待 UI 接管**的时长——后台阻塞的根源 |
| `ExcludeWindowMs` | **瞬时排除窗口**的时长——排除属性生效期间无法捕获 |
| `CpuMsTotal` / `CpuMsPerFrameAvg` | 处理器时间 |
| `ElapsedMsLast` / `ElapsedMsP95` / `ElapsedMsMax` | 端到端经过时间；p95 的样本窗口为最近 128 次 |

`WaitUiMs` 与 `ExcludeWindowMs` 必须**并列记录**：两者是后台占用中最容易被漏掉、也最容易失控的两段。

链 B 的 `GpuSceneRendererDiagnostics` 提供：

| 指标 | 含义 | 口径 |
| --- | --- | --- |
| `PresentIntervals` | 相邻两次成功呈现之间的间隔 | 墙钟 |
| `RenderDurations` | 从渲染开始到 `Present` 返回 | 墙钟 |
| `GpuPassDurations` | 像素着色通道的 GPU 侧耗时 | D3D11 timestamp query；设备不支持时 `GpuTimestampStatus` 说明原因 |
| `CompositorFrameAges` | 合成器扫描输出到应用呈现调用的时延 | 由 `IDXGISwapChain.GetFrameStatistics` 的 `SyncQPCTime` 推导 |
| `CaptureFrameAges` | 捕获源交付的桌面帧的帧龄 | 来自 `CaptureDiagnostic.LastFrameAgeMilliseconds` |
| `PresentAttemptCount` / `PresentedFrameCount` | 呈现尝试数与成功呈现数 | 两者不等即为呈现停滞 |

补充约束：

- **统计窗口有界。** 每一路耗时写入容量 2048 的滚动窗口（`RollingTimingWindow`），输出 `P50 / P95 / P99 / Max` 与样本数（`TimingPercentiles`）。
- **资源账本必须与时间指标一并采集。**

### 6.3 性能预算

| 判据 | 值 | 口径 | 归属 |
| --- | --- | --- | --- |
| 后台 CPU | ≤ **40 ms/秒** | CPU 时间 | 链 A |
| 单次处理延迟 p95 | ≤ **30 ms** | 经过时间 | 链 A |
| 捕获帧率 | 默认 2 fps，下限 1 fps，上限 6 fps | — | 链 A |
| 单帧像素上限 | 2,000,000 物理像素 | — | 链 A |
| 单帧字节上限 | 32 MiB | — | 链 A |
| 捕获降采样比例 | 默认 0.25 | — | 链 A |
| GPU 资源字节预算 | 1 GiB | — | 链 B |
| GPU 资源条数预算 | 256 | — | 链 B |

### 6.4 不得这样测量

- **不得**用 `Stopwatch` 的墙钟结果表述为"额外 CPU"，也**不得**据此推算整机 CPU 百分点。
- **不得**只报平均值或末值作为性能结论。
- **不得**把 GPU 证据截图的读回耗时计入帧时间统计——证据采集是显式的冷路径。
- **不得**用链 A 的墙钟分段耗时替代链 B 的墙钟呈现耗时，或反之。
- **不得**把单次 CPU 差值当作结论：Windows 进程 CPU 时钟粒度约 15.6 ms，单次操作无法直接测。CPU 口径必须**批量执行**（建议每次不少于 2000 次）后取每操作均值；必须同进程同刻做前后配对；必须交替执行顺序；必须分成至少 3 组独立测量并逐组记录。差值落在噪声内时**如实报"不可判定"，不得报 0**。
- 折算 CPU 占用时使用 `CpuMsTotal / (ElapsedSinceStartMs / 1000)` 与 40 ms/秒预算比对；`CpuMsTotal` 落在时钟粒度以内时记 0 表示**不可判定**，不表示零开销。

---

## 7. 降级链与状态可见

### 7.1 GDI+ 路线的降级链

降级链为 `glass → gradient → solid`。每一级降级都必须带原因码与可读原因文本。

`BackdropDegradeReason` 的取值：

| 值 | 含义 |
| --- | --- |
| `None` | 未发生降级 |
| `BackendMissing` | 主题请求了 glass，但没有注册任何实时背景后端 |
| `BackendNotReady` | 已注册后端，但后端报告自己未就绪 |
| `BackendFrameInvalid` | 后端声称就绪，但没有给出可用帧 |
| `GradientUnavailable` | 后端不可用，且渐变两端颜色均不可用，只能沿用 solid 背景路径 |
| `InvalidBounds` | 客户区没有有效宽高，已跳过全部绘制 |

`BackdropStatus` 必须同时暴露：主题请求的模式、实际生效的模式、是否降级、原因码、原因文本、底图来源、当次实际绘制的层、要求的模糊半径。

### 7.2 GPU 路线的降级链

降级链为 `GpuLiveBackground → GpuFixedInput → Legacy`，由 `RenderingRoutePolicy.Decide` 判定，原因码包括：`rendering_configuration_absent`、`rendering_configuration_invalid`、`safe_mode`、`gpu_effects_opt_in_required`、`wda_capture_policy_not_accepted`。

**捕获层失败与路线降级是两件事。** 捕获失败后仍走 GPU 路线并呈现程序化或固定背景，**不回退到 legacy 路线**。捕获层的原因码包括：`capture_material_forbidden`、`capture_output_not_on_render_adapter`、`capture_output_selection_failed`、`desktop_capture_initialization_failed`、`capture_generation_mismatch`、`capture_not_running`、`capture_lease_in_use`、`capture_wait_timeout`、`capture_access_lost`、`capture_recovery_deferred`、`capture_acquire_failed`、`capture_format_unsupported`、`capture_texture_missing`、`capture_source_disposed`、`capture_started`、`capture_stopped`、`capture_frame_available`、`capture_last_frame_retained`。

链 B 的取帧结果枚举 `DesktopCaptureResult`：`FrameAvailable`、`LastFrameRetained`、`NoFrameYet`、`AccessLost`、`Failed`、`StaleGeneration`、`Disposed`。

- **等待超时不得记为故障。** 无新帧时保留最后一帧有效帧并记录其年龄，返回 `LastFrameRetained`。
- **访问丢失后的恢复有节流。** 进入 `AccessLost` 后，恢复尝试以 250 ms 为间隔延后，期间取帧返回 `capture_recovery_deferred`。
- **同一显示器上的失败不重复尝试。** 渲染器记住失败时的显示器句柄，同一显示器上的失败不得重复重建捕获对象；换屏或显示设置变更后自动放行再试一次。抑制的是"重复重建捕获对象"，不是"放弃液态玻璃"。
- 用户主动操作（切主题、重新配置、显式重试）后允许在同一显示器上再试一次。

### 7.3 状态可见性约束

- **降级不得静默。** 每一次降级都必须有可读原因，能进入日志与界面。`LiveGlassController.StatusText` 反映的是**实际**状态而不是配置值：配置为开但服务未启动时，状态必须说明"停了 + 为什么"。
- **原因码不得用主窗口语境描述预览场景，反之亦然。** 编辑器预览没有样例图时报"预览没有固定样例图"，而不是"未注册实时背景后端"。
- **捕获诊断必须可读。** `CaptureDiagnostic` 携带状态、原因码、说明、最后一帧年龄与恢复尝试次数。
- **呈现停滞与从未呈现必须可区分。** `PresentAttemptCount` 与 `PresentedFrameCount` 分别为 0 时表示从未尝试呈现；两者不等表示呈现停滞。

---

## 8. 默认关闭与首次提示

- **实时桌面背景默认关闭。** `Settings.GpuLiveBackgroundEnabled` 默认 `false`，`Settings.GpuWdaCapturePolicyAccepted` 默认 `false`。
- **链 A 的实时玻璃开关同样默认关闭**，且产品界面不提供开启入口。
- **首次开启必须弹出确认。** 设置页「实时桌面背景」由未勾选变为勾选且 `GpuWdaCapturePolicyAccepted` 仍为 `false` 时，弹出确认框，文本必须如实表述副作用：实时玻璃使用 Desktop Duplication，并通过 `WDA_EXCLUDEFROMCAPTURE` 排除**整个顶层窗口**；开启后受支持的系统截图和录屏会看不到 LiteMonitor++；既有的非玻璃主题与固定输入模式不受影响。
- **用户取消确认时开关必须回退。** 复选框恢复为未勾选，且配置值不得写入。
- **提示只出现一次语义**：确认结果记入 `GpuWdaCapturePolicyAccepted`，再次开启时不得重复弹框。
- **编辑器固定预览不启动捕获服务，也不弹该提示。**
- **主题声明与用户开关相互独立。** 主题的 `rendering` 段声明材质与后端，用户开关决定是否进入 GPU 路线与是否启用实时背景；打包默认值不得让设置项失效。

---

## 9. 路线裁决

### 9.1 系统材质路线不采用

DWM 系统材质（Mica / Acrylic）不是本仓库的实时背景实现路径，`BackdropCaptureNative` 与 `ScreenCaptureFrameSource` **不提供任何 DWM 材质入口**。

裁决理由：

- `DwmSetWindowAttribute` 的 `DWMWA_SYSTEMBACKDROP_TYPE` 调用**成功**不代表材质生效。返回值 `S_OK` 不能作为材质呈现的判据；材质是否生效只能由"背后内容是否实时响应"判定。
- Mica 与 Acrylic 的多个变体都不呈现"背后内容实时响应"：非分层窗口虽然画面有差别，但呈静态，对应的时间差为零。
- 实时玻璃要求底图**逐帧更新**且**能把窗口自身排除出捕获**。系统材质路线既不提供逐帧底图，也不提供窗口级排除。

`MainFormWinHelper` 与 `WindowAppearanceController` 中的 `DwmSetWindowAttribute` 调用只用于圆角偏好与边框色，与背景材质无关。

### 9.2 捕获路线的成立条件

GPU 路线的实时桌面背景要求下列条件**同时成立**：

1. 主题 `rendering.backend == "gpu"` 且 `rendering.material == "liquid_glass"`；
2. 用户开关「GPU 材质渲染」开启；
3. 用户开关「实时桌面背景」开启；
4. 用户接受 WDA 捕获政策；
5. 进程不处于安全模式；
6. 窗口所在显示器在渲染适配器的输出列表中；
7. 渲染适配器覆盖窗口所在显示器。

链 A 的抓屏路线额外要求进程为 DPI 感知（见 §5.4）。

### 9.3 模糊半径合同

**链 A 的模糊半径以物理像素 σ 表达。** `options.BlurPx` 已缩放一次，捕获与滤波环节**不得再乘缩放**。合同由 `BackdropBlurContract` 表达：

| 量 | 定义 |
| --- | --- |
| `SigmaPhysicalPx` | 物理标准差 σ（物理像素），直接取 `options.BlurPx` |
| `SigmaDownsampledPx` | 降采样缓冲上的目标标准差 = `SigmaPhysicalPx × CaptureScale` |
| `BoxRadius` | 3 次可分离 box blur 的半径 `r = round((√(1+4σ²) − 1) / 2)` |
| `SigmaEffectiveDownsampledPx` | 实际等效标准差（降采样缓冲像素）= `√(r(r+1))` |
| `SigmaEffectiveDisplayPx` | 折算回显示尺寸的等效标准差（物理像素）= `σ_eff_down / CaptureScale` |

两条硬约束：

- **σ = 0 ⇒ 不做滤波，也不做重采样。** 此时 `RequiresBlur` 与 `RequiresResample` 均为 false，画面保证零软化。
- **单纯的缩小再放大只是缩放近似，不算模糊。** 因此只有在真正施加了 box blur（`r ≥ 1`）时才允许降采样与升采样。

链 B 的模糊在材质通道内表达，取值来自 `MaterialRuntimeSettings.BlurRadius`，并按 `styleScale`（DPI 比例 × 用户缩放）缩放一次。它是单趟像素着色器中的多次采样近似，不是离屏多趟的真实模糊。

### 9.4 支持矩阵

| 项目 | 要求 | 不满足时 |
| --- | --- | --- |
| 捕获后端（链 B） | DXGI Desktop Duplication | 停留在 `GpuFixedInput`，呈现固定或程序化背景 |
| 输出归属 | 窗口所在显示器在渲染适配器的输出列表中 | 原因码 `capture_output_not_on_render_adapter`，呈现固定或程序化背景 |
| 像素格式 | `B8G8R8A8_UNorm` | 原因码 `capture_format_unsupported` |
| 色彩 | SDR，颜色空间 `SDR-sRGB` | HDR 显示按 SDR 输出；`CaptureCapabilities.SupportsHdr` 为 false |
| 帧时间戳 | 捕获输出与渲染适配器一致 | `CaptureCapabilities.SupportsTimestamp` 为 false，`Limitation` 字段明示该边界 |
| 自身排除 | 系统支持 `WDA_EXCLUDEFROMCAPTURE` | 排除调用失败，捕获走失败路径并给出原因码 |
| DPI 感知（链 A） | 进程感知等级为 Per-Monitor 或 Per-Monitor-V2 | 捕获守卫逐帧放弃，玻璃路径不产出帧 |
| 多屏 / 混合 DPI | 支持，窗口可拖到含负坐标原点的副屏 | — |

---

## 10. 已知限制

- **HDR 未实现。** 交换链、场景纹理、前景纹理与捕获纹理统一为 `B8G8R8A8_UNorm`，颜色空间固定 `SDR-sRGB`。
- **跨适配器时间戳未验证。** DXGI Desktop Duplication 的帧时间戳仅在输出与渲染适配器一致时可靠。
- **跨适配器显示器无法捕获实时桌面。** 窗口所在显示器不在渲染适配器的输出列表时，捕获必然失败，渲染以固定或程序化背景继续，不出现空洞。
- **玻璃折射与模糊是单趟近似。** 材质通道为一次像素着色器调用，折射与模糊由多次采样近似合成。
- **排除作用于整个顶层窗口。** 开启实时桌面背景后，受支持的截图与录屏工具看不到该窗口区域；该行为由设置项与首次开启时的政策确认显式承接。
- **链 A 的实时捕获没有产品入口。** 其代码与契约保持完整，承担 GDI+ 路线的背景绘制与降级职责，并作为链 B 之外的独立实现与契约参照。
