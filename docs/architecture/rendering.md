# 渲染路线与 GPU 渲染架构

本文描述 LiteMonitor++ 当前生效的渲染架构：三条渲染路线的语义与判定顺序、主题声明与用户开关的双条件机制、Direct3D 11 / Direct2D GPU 渲染栈的分层与数据流、材质参数体系、跨模块共享的渲染合同、线程与所有权规则、性能测量口径、支持矩阵，以及两条彼此独立的背景捕获链。

适用对象是在本仓库上开发、评审与排障的工程师。不在本文范围：界面控件的具体绘制细节、单个传感器的采集算法、主题 JSON 的业务字段语义、插件与 Web 服务的协议细节。

---

## 1. 模块边界与依赖方向

### 1.1 渲染相关目录的职责归属

| 目录 | 职责 | 代表类型 |
| --- | --- | --- |
| `src/Rendering/Contracts/` | 渲染层的跨模块共享合同：版本号、枚举、场景快照、背景帧租约、输出描述 | `RenderContracts.cs` 中的 `RenderingContractVersion`、`SceneBackgroundMode`、`SceneSnapshot`、`IBackgroundFrameSource`、`IBackgroundFrameLease`、`FixedBackgroundInput` |
| `src/Rendering/Configuration/` | 主题渲染配置、路线判定、材质运行参数、句柄代次、启动守卫 | `ThemeRenderingConfig`、`RenderingRoutePolicy`、`MaterialRuntimeSettings`、`WindowHandleGeneration`、`GpuStartupGuard` |
| `src/Rendering/Gpu/Device/` | D3D11 设备生命周期、适配器选择、资源账本 | `D3D11DeviceService`、`GpuAdapterSelector`、`GpuResourceLedger`、`GpuDeviceLifecycle` |
| `src/Rendering/Gpu/Foreground/` | 前景绘制命令与 Direct2D 文本/图形绘制 | `ForegroundDrawList`、`Direct2DForegroundPainter` |
| `src/Rendering/Gpu/Geometry/` | 面板几何与颜色换算 | `PanelGeometry` |
| `src/Rendering/Gpu/Effects/` | 帧调度、最新帧邮箱、动效时间线 | `FrameScheduler`、`LatestFrameMailbox<T>`、`MaterialMotionTimeline` |
| `src/Rendering/Materials/` | 材质常量缓冲布局、主题前景样式派生、工具栏固定背景 | `MaterialFrameConstants`、`SceneMaterialLayoutBuilder`、`ThemeForegroundStyleFactory`、`ToolbarPreviewSurface` |
| `src/Rendering/Scene/` | UI 状态到场景快照的适配与快照构建 | `UiSceneSnapshotAdapter`、`SceneSnapshotBuilder` |
| `src/Rendering/Capture/` | GPU 路线的桌面捕获帧源 | `DesktopDuplicationFrameSource` |
| `src/Rendering/Presentation/` | 呈现宿主、呈现会话、渲染器、降级与清理策略 | `GpuPresentationHostController`、`PresentationSession`、`GpuPresentationSessionOwner`、`GpuSceneRenderer`、`GpuFailureFallbackScheduler`、`CapturePolicyCleanupPolicy` |
| `src/Rendering/Power/` | 显示节奏与采样节奏的纯策略 | `PowerPolicy`、`PowerDisplayScheduler` |
| `src/Rendering/LegacyFallbackStyleResolver.cs` | 回退到 GDI+ 时的样式解析与校验 | `LegacyFallbackStyleResolver` |
| `src/System/Backdrop/` | GDI+ 路线的背景捕获与背景绘制 | `ScreenCaptureFrameSource`、`BackdropCaptureService`、`BackdropModeResolver`、`BackdropRenderer` |

### 1.2 依赖方向

- **`src/Rendering` 不得依赖 `src/UI`。** 渲染层只消费不可变数据（`SceneSnapshot`、`RenderGenerations`、`MaterialRuntimeSettings`）与 Win32/DXGI 句柄，不得持有 `Control`、`MetricItem`、GDI 画刷或窗口对象。
- **路线判定是纯函数。** `RenderingRoutePolicy.Decide` 的输入是 `RenderingRouteRequest`（纯值），输出是 `RenderingRouteDecision`（纯值），不读系统状态、不创建 GPU 资源，可被确定性单测覆盖。
- **`src/UI` 是渲染路线的消费方与编排方。** `UIController` 组合主题配置、用户开关与安全模式，构造 `RenderingRouteRequest` 并驱动 `RenderingTransitionCoordinator` 完成路线切换。
- **`src/Core` 提供主题与设置，不感知渲染后端。** 主题的 `rendering` 段以 `ThemeRenderingConfig` 形式挂在 `Theme.Rendering` 上，是路线判定的唯一主题侧输入。
- **合同层禁止携带可变对象。** `src/Rendering/Contracts/` 中的类型不得携带 `Control`、可变指标集合、GDI 画刷或捕获资源。

---

## 2. 渲染路线模型

### 2.1 三条路线的语义

| 路线 | 枚举值 | 后端 | 材质 | 背景来源 | 语义 |
| --- | --- | --- | --- | --- | --- |
| GDI+ 路线 | `RenderingRoute.Legacy` | `RenderBackendKind.LegacyGdiPlus` | `MaterialKind.Legacy` | 主题自身的背景路径 | 面板由 GDI+ 自绘，主题外观与指标呈现完全由 UI 层决定。任何环境都可用，是兜底路线 |
| GPU 固定输入路线 | `RenderingRoute.GpuFixedInput` | `RenderBackendKind.Gpu` | `MaterialKind.LiquidGlass` 或 `MaterialKind.CyberPunk` | 程序化背景，或主题声明的固定纹理 | 面板材质由 D3D11 像素着色器合成，玻璃背后是程序化生成的背景（渐变、极光、网格、噪声），不涉及桌面捕获 |
| GPU 实时桌面背景路线 | `RenderingRoute.GpuLiveBackground` | `RenderBackendKind.Gpu` | `MaterialKind.LiquidGlass` | DXGI Desktop Duplication 捕获的实时桌面 | 玻璃背后是实时桌面画面，窗口整体从受支持的屏幕捕获中排除 |

三条路线共享同一套指标语义与场景构建结果，差异只在"背景从哪里来"和"面板如何合成"。

### 2.2 判定输入

`RenderingRouteRequest` 的字段及其来源：

| 字段 | 来源 | 含义 |
| --- | --- | --- |
| `Configuration` | `Theme.Rendering` | 主题的 `rendering` 段；缺省为 `null` |
| `GpuEffectsEnabled` | `Settings.GpuThemeEffectsEnabled` | 用户开关「GPU 材质渲染」 |
| `LiveBackgroundEnabled` | `Settings.GpuLiveBackgroundEnabled` | 用户开关「实时桌面背景」 |
| `WdaCapturePolicyAccepted` | `Settings.GpuWdaCapturePolicyAccepted` | 用户是否接受窗口捕获排除的产品政策 |
| `SafeMode` | `Program.SafeMode` | 进程启动参数派生的安全模式标记 |
| `WindowIsLayered` | `MainForm.IsWindowLayered()` | 窗口是否带 `WS_EX_LAYERED`（透明度 < 100% 时成立） |

### 2.3 判定顺序表

`RenderingRoutePolicy.Decide` 按下表**自上而下**判定，命中即返回，不再评估后续条件。

| 顺序 | 条件 | 结果路线 | 有效后端 | 有效材质 | 请求捕获 | 原因码 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 主题没有 `rendering` 段（`Configuration == null`） | `Legacy` | `LegacyGdiPlus` | `Legacy` | 否 | `rendering_configuration_absent` |
| 2 | `rendering` 段校验失败（`ThemeRenderingConfig.Validate()` 返回非空错误） | `Legacy` | `LegacyGdiPlus` | `Legacy` | 否 | `rendering_configuration_invalid` |
| 3 | `rendering.backend == "legacy"` | `Legacy` | `LegacyGdiPlus` | `Legacy` | 否 | 无 |
| 4 | `SafeMode == true` | `Legacy` | `LegacyGdiPlus` | `Legacy` | 否 | `safe_mode` |
| 5 | 「GPU 材质渲染」关闭 | `Legacy` | `LegacyGdiPlus` | `Legacy` | 否 | `gpu_effects_opt_in_required` |
| 6 | `rendering.material == "cyber_punk"` | `GpuFixedInput` | `Gpu` | `CyberPunk` | 否 | 无 |
| 7 | `rendering.material == "liquid_glass"` 且「实时桌面背景」关闭 | `GpuFixedInput` | `Gpu` | `LiquidGlass` | 否 | 无 |
| 8 | 「实时桌面背景」开启但 WDA 捕获政策未获用户接受 | `GpuFixedInput` | `Gpu` | `LiquidGlass` | 否 | `wda_capture_policy_not_accepted` |
| 9 | 以上均不命中 | `GpuLiveBackground` | `Gpu` | `LiquidGlass` | 是 | 无 |

判定顺序的三条硬约束：

- **原因 3 早于原因 4。** 主题本身声明 `backend = "legacy"` 时，无论是否处于安全模式，结果都是 `Legacy` 且原因码为空——安全模式不会给非 GPU 主题附加无意义的原因码。
- **原因 5 早于原因 6。** 用户开关是路线的前置闸，材质只决定 GPU 路线内部的形态。开关关闭时，`cyber_punk` 主题同样停在 `Legacy`。
- **原因 8 不改变材质。** WDA 政策未接受时降级的是"背景来源"，材质仍是 `LiquidGlass`，因此面板呈现的是固定/程序化背景上的玻璃，而不是把玻璃替换成赛博朋克或 GDI+。

### 2.4 主题声明与用户开关的双条件

进入 GPU 路线的充要条件是**两个独立条件同时成立**：

1. **主题声明**：主题 JSON 的 `rendering.backend == "gpu"`，且 `rendering` 段整体通过校验。
2. **用户开关**：设置中的「GPU 材质渲染」处于开启状态。

两个条件的关系是「与」而非「或」，缺失任一条件都保持 GDI+ 路线：

- 主题声明了 GPU 但用户开关关闭 ⇒ 原因码 `gpu_effects_opt_in_required`，面板按 GDI+ 外观呈现。
- 用户开关开启但主题未声明 GPU ⇒ 原因码 `legacy_theme_ignores_gpu_switch`，该主题按 GDI+ 外观呈现。

「实时桌面背景」是第三层开关，只在已经进入 GPU 路线后才参与判定：它决定背景是否来自实时桌面捕获，不决定是否使用 GPU 材质。

设置页与托盘菜单的主题条目按主题声明的路线加注标注，设置面板另有独立的「渲染路线」页给出完整状态行（含真实原因码）。状态行只陈述事实与原因码，不允许出现没有原因的"已回退"表述。

### 2.5 安全模式参数的作用

进程启动参数 `--safe`、`--safe-mode`、`--disable-gpu` 三个写法等价，共同把 `Program.SafeMode` 置为真。

- 安全模式的唯一作用是**在路线判定层强制选择 `Legacy` 路线**，原因码 `safe_mode`。
- 安全模式**不修改任何用户设置**，也不写入配置文件；下一次不带该参数启动时，用户开关与主题声明照常生效。
- 安全模式**不阻止 GDI+ 路线自身的背景能力**（主题的 `backdrop.mode` 仍然按主题声明生效）。
- 安全模式**在判定顺序中位于 `backend == "legacy"` 之后**，因此对非 GPU 主题不产生原因码。

---

## 3. GPU 渲染栈

### 3.1 设备与适配器选择

- 设备由 `D3D11DeviceService.TryCreate` 创建，特性级别按 `11_1 → 11_0 → 10_1 → 10_0` 依次尝试，创建标志为 `BgraSupport`。设备创建失败的原因码为 `gpu_device_initialization_failed`。
- **适配器必须跟随窗口所在显示器选择**，由 `GpuAdapterSelector.SelectAdapterForMonitor` 完成：枚举 `IDXGIFactory1` 的适配器，跳过软件适配器（`AdapterFlags.Software`），取第一个其输出挂在目标显示器上的适配器；DXGI 的枚举顺序即性能序，独立显卡优先。
- 指定适配器创建设备时 `DriverType` 必须是 `Unknown`——驱动类型与显式适配器互斥，否则 `D3D11CreateDevice` 失败。
- 选适配器的动因：使用默认适配器时，混合显卡机器上通常拿到独立显卡，而窗口所在显示器可能并未接在该适配器上。此时交换链无法呈现到该屏，桌面复制也抓不到该屏，表现为"切到玻璃没反应，把窗口拖到另一块显示器才正常"。
- 找不到覆盖目标显示器的适配器时，调用方回退到默认适配器并给出原因码。
- 设备与适配器的选择结果（名称、特性级别、Vendor/Device ID、LUID）记录在 `GpuDeviceInfo` 中，并作为诊断数据对外暴露。

### 3.2 交换链与 flip model

`GpuSceneRenderer` 为被呈现的顶层窗口持有一个交换链：

| 属性 | 取值 |
| --- | --- |
| 格式 | `B8G8R8A8_UNorm`（SDR，8 bit UNorm） |
| 缓冲数 | 2 |
| `SwapEffect` | `FlipSequential`（flip model，顺序翻转） |
| `Scaling` | `Stretch` |
| `AlphaMode` | `Ignore` |
| 采样 | 1 sample，无 MSAA |
| `BufferUsage` | `RenderTargetOutput` |

- 交换链挂在窗口句柄上（`CreateSwapChainForHwnd`），工厂对窗口建立关联并忽略 Alt+Enter。
- **呈现与合成器同步**：`Present(1, PresentFlags.None)`，同步间隔为 1。因此呈现间隔、帧龄与帧率预算描述的是**已被显示**的帧，而不是仅仅提交成功的帧。
- 尺寸变更走**两段式**顺序：先 `ResizeBuffers` 成功，再销毁与重建尺寸相关的资源（场景纹理、前景纹理、渲染目标视图、Direct2D 画笔）。顺序颠倒会让一次可恢复的尺寸抖动把渲染器置于"资源已释放但交换链未改尺寸"的半残状态，下一帧必然抛错，整场降级到 GDI+。
- 场景先渲染到离屏纹理，再 `CopyResource` 到交换链后备缓冲。

### 3.3 帧内管线

每一帧的 GPU 侧工作是一条**单趟像素着色**：

```
场景快照 SceneSnapshot
   └─ SceneMaterialLayoutBuilder.Build
        └─ MaterialFrameConstants（29 × float4 常量缓冲）
   └─ 背景纹理（t0：程序化 / 固定纹理 / 实时捕获）
   └─ 前景纹理（t1：Direct2D 绘制的文本与图形，premultiplied）

顶点着色 VSMain：由 SV_VertexID 生成覆盖全屏的三角形（3 顶点）
像素着色 PSMain：
   1. 读取背景（程序化函数或采样 t0）
   2. 按材质计算表面：玻璃（GlassRoot）或赛博朋克（CyberRoot）
   3. 按分组框叠加装饰
   4. 合成前景：color = color * (1 - foreground.a) + foreground.rgb
   5. 输出 alpha 固定为 1
```

- 着色器以预编译字节码 `resources/shaders/SceneMaterialVS.cso`、`SceneMaterialPS.cso` 随程序分发，对应源码 `resources/shaders/SceneMaterial.hlsl`（含 `ToolbarGlass.hlsli`）。
- **单趟约束的后果**：折射与模糊是像素着色器内的多次采样近似，不是离屏多 pass 的真实折射；文字在前景通道绘制，**不参与折射与模糊**。
- 材质模式由 `viewport.z` 编码：`0` = 液态玻璃，`1` = 赛博朋克。
- 显示器旋转由 `state.w` 携带 DXGI 旋转码（identity = 1，90 = 2，180 = 3，270 = 4），着色器据此把显示空间 UV 换算回未旋转的桌面复制表面。
- `nebulaAurora`、`nixieBrass`、`crtPhosphor` 均只接受固定输入：即使全局实时背景开关为开，路由和宿主层也必须把捕获请求压回 `false`，且 `CaptureSessionsCreated` 保持为零。

### 3.4 前景纹理与 Direct2D

- 前景被绘制到一张与窗口等尺寸的 `B8G8R8A8_UNorm` 纹理上（`RenderTarget | ShaderResource`），再作为 `t1` 交给材质通道合成。
- 绘制由 `Direct2DForegroundPainter` 完成，纹理像素格式为 **premultiplied**，文本抗锯齿模式为 `Grayscale`。
- 字体与字号来自 `ForegroundThemeStyle`，单位是 DIP；`Direct2DForegroundPainter.ResolveFontScale` 依据 `DeviceDpi / 96 × 用户缩放` 计算缩放系数，**全链路只缩放一次**。
- 前景样式有两个来源：命名视觉预设（`nebulaAurora`、`nixieBrass`、`crtPhosphor`）或 `glassStyle == "floatingtoolbar"` 使用主题 `font.*` 与 `color.*` 派生样式；其余 `standard` / `cyber_punk` 路径使用 `ForegroundThemeStyle.Default(material)`。缺失或显式 `default` 时保持原默认记录与像素不变。
- 颜色在前景合同中是**非预乘 sRGB**（`RgbaColor`），预乘转换只在绘制边界发生。

### 3.5 背景源

背景有三种模式，编码在 `SceneBackgroundMode`：

| 模式 | 枚举值 | 纹理来源 | 使用场景 |
| --- | --- | --- | --- |
| 程序化 | `Procedural` = 0 | 着色器内解析计算：垂直渐变 + 极光带 + 网格线 + 哈希噪声 | `GpuFixedInput` 路线的默认背景 |
| 固定纹理 | `FixedTexture` = 1 | 由 `IFixedBackgroundTarget.TrySetFixedBackground` 显式上传的 `FixedBackgroundInput` | 主题工作室预览、编辑器固定样本、工具栏式玻璃的固定底图 |
| 实时捕获 | `LiveCapture` = 2 | `DesktopDuplicationFrameSource` 提供的 `ID3D11Texture2D` | `GpuLiveBackground` 路线 |

- `FixedBackgroundInput` 是不可变的 BGRA8 SDR 输入，持有私有像素副本；`TryCreate` 校验尺寸（1..16384）、行距（≥ 宽度 × 4）、DPI（> 0）、不透明区域边界（必须被图像包含）与 `contentVersion`（非空），失败时返回稳定错误码 `fixed_background_invalid_dimensions` / `fixed_background_invalid_stride` / `fixed_background_invalid_dpi` / `fixed_background_invalid_bounds` / `fixed_background_missing_version`。
- 固定纹理的上传**只能由显式调用触发**，替换、`ClearFixedBackground` 与 `Dispose` 必须释放对应 GPU 资源。
- 实时捕获纹理来自 DXGI Desktop Duplication，目标显示器全尺寸 BGRA8 UNorm，应用只采样窗口所需区域。

### 3.6 资源账本

`GpuResourceLedger` 是 GPU 资源的唯一登记处：

- 生产宿主的预算是 **1 GiB 字节 / 256 个资源**；超限时 `TryReserve` 拒绝并给出 `gpu_resource_byte_budget_exceeded` 或 `gpu_resource_count_budget_exceeded`。
- 每笔资源以 `GpuResourceLease` 持有，`GpuOwnedResource<T>` 把 COM 资源的释放与租约的归还绑定为一次原子动作。
- 资源种类 `GpuResourceKind` 覆盖 `Device`、`SwapChain`、`Texture`、`RenderTarget`、`Shader`、`Buffer`、`Query`、`GlyphCache`、`Capture`、`Host`。
- `GpuResourceSnapshot` 暴露活动/峰值资源数、活动/峰值字节数、按种类的字节分布、累计预留/释放/拒绝次数，供诊断与资源压力排查使用。

---

## 4. 材质参数体系

### 4.1 三种材质

| 材质 | `MaterialKind` | 外观 | 背景能力 |
| --- | --- | --- | --- |
| `legacy` | `Legacy` | GDI+ 路线的主题原生外观 | 由主题的 `backdrop` 段决定，与 GPU 材质无关 |
| `liquid_glass` | `LiquidGlass` | 圆角玻璃面板：折射位移、厚度、高光、反高光、内发光、阴影、色散与中心清晰区 | 支持程序化、固定纹理与实时桌面捕获 |
| `cyber_punk` | `CyberPunk` | 机械切角框体：面板底板、分段边框、扫描线、通风槽、内倒角、辉光、铆钉 | **零桌面捕获**；主题声明 `liveBackground = true` 属校验错误 |

### 4.2 参数与取值范围

参数写在主题的 `rendering.parameters` 段，动效参数写在 `rendering.motion` 段。

**液态玻璃参数**

| 参数 | 范围 | 默认（`standard`） | 默认（`floatingtoolbar`） | 作用 |
| --- | --- | --- | --- | --- |
| `cornerRadius` | [0, 64] | 26 | 30 | 玻璃根圆角半径 |
| `thickness` | [0, 8] | 1 | 8 | 材质厚度，缩放高光与内发光的强度 |
| `refraction` | [0, 8] | 1.6 | 8 | 边缘法线方向的背景采样位移量 |
| `blurRadius` | [0, 32] | 8 | 8 | 背景散射采样的半径 |
| `clearCenterRatio` | [0, 1] | 0 | 0 | 中心清晰区比例；增大则中心少受色调覆盖 |
| `surfaceOpacity` | [0, 1] | 0.055 | 0.055 | 表面色调覆盖强度 |
| `highlight` | [0, 1] | 1 | 0.72 | 高光强度 |
| `counterHighlight` | [0, 1] | 1 | 1 | 反高光（内发光）强度 |
| `shadow` | [0, 1] | 0.34 | 0.115 | 阴影强度 |
| `shadowRadius` | [0, 64] | 0 | 30 | 阴影扩散半径 |
| `fogStrength` | [0, 1] | 0.42 | 0.16 | 雾化强度 |
| `tintStrength` | [0, 1] | 取 `surfaceOpacity` | 0.038 | 色调强度 |

**赛博朋克参数**

| 参数 | 范围 | 默认 | 作用 |
| --- | --- | --- | --- |
| `primaryCut` | [0, 64] | 14 | 主切角尺寸 |
| `secondaryCut` | [0, 32] | 9 | 次切角尺寸 |
| `outlineWidth` | [0, 8] | 1 | 描边宽度 |
| `glow` | [0, 100] | 12 | 辉光强度 |
| `ambientRatio` | [0, 1] | 0.12 | 环境反射比例 |
| `panelOpacity` | [0, 1] | 0.965 | 面板底板不透明度 |

**动效参数（两种 GPU 材质共用）**

| 参数 | 范围 | 默认（玻璃 / 赛博朋克） | 作用 |
| --- | --- | --- | --- |
| `motionEnabled` | 布尔 | `true` | 动效总开关；关闭时动画时间为 0，材质呈静态 |
| `targetFps` | [1, 240] | 30 | 目标呈现帧率 |
| `stateTransitionMs` | [0, 5000] | 180 / 140 | 状态切换过渡时长 |
| `scannerPeriodMs` | [0, 60000] | 0 / 2800 | 扫描线周期；0 表示不启用 |

### 4.3 `glassStyle` 语义

`glassStyle` 只对 `liquid_glass` 有意义，取值为 `standard` 或 `floatingtoolbar`（JSON 中写作 `floatingtoolbar`，枚举 `GlassSurfaceStyle.FloatingToolbar`）。它**先于其它参数被读取**，因为它决定各参数的默认值。

| 取值 | 语义 | 差异 |
| --- | --- | --- |
| `standard` | 面板式玻璃 | 整块面板是一层玻璃材质；根矩形内缩 1 物理像素；每个分组绘制一条分隔线与轻微标题色；圆角、折射、模糊、厚度按原始参数值使用 |
| `floatingtoolbar` | 悬浮工具栏式玻璃 | 根矩形铺满窗口（无内缩）；圆角按 `logicalRadius × dpi / 96 × 用户缩放` 换算并夹紧到 `min(宽,高) / 2`；折射、模糊、厚度、阴影半径统一乘以 `dpi/96 × 用户缩放`；**不绘制分组框**；前景样式改由主题派生 |

### 4.4 非法值的回退规则

参数读取集中在 `MaterialRuntimeSettings.Create`，其回退规则与主题级校验是**两层独立的机制**：

- **主题级校验（`ThemeRenderingConfig.Validate`）**：越界的数值、类型不符的字段、非法枚举一律判为**致命错误**。整份主题因此不可用，路线判定落到原因 2（`rendering_configuration_invalid`），面板按 GDI+ 路线呈现。校验涵盖 `version`、`backend`、`material` 的取值域与组合约束（`backend = "legacy"` 必须配 `material = "legacy"`；`backend = "gpu"` 不得配 `material = "legacy"`）、`liquid_glass` 必须声明布尔 `parameters.liveBackground`、`cyber_punk` 不得声明 `liveBackground = true`、`rendering.fallback` 必须选择 `legacy / legacy`，以及 §4.2 列出的全部数值范围。
- **运行时读取层**：字段缺失、类型不是数值、不是有限数、或不是对应整数类型时，返回该参数的默认值；数值越界时用 `Math.Clamp` 夹紧到声明区间。这一层是 GPU 路径的安全网。
- **未知扩展字段不被认领。** `rendering.parameters`、`rendering.motion` 与主题的扩展字段以 `JsonElement` 字典保留在 `ThemeRenderingConfig` 中，运行时参数对象不声明对它们的任何语义。
- **`rendering.fallbackStyle` 的非法值属于非致命问题。** 它只产生诊断码 `gpu_fallback_style_invalid`，不影响整份主题是否可用——一条样式笔误不得让 GPU 路由整体失效。

---

## 5. 共享合同

### 5.1 合同版本与核心对象

`RenderingContractVersion` 固定 `Core = 1`、`Capture = 1`，schema 标识为 `litemonitor.gpu-render/1`。

| 对象 | 语义 |
| --- | --- |
| `RenderGenerations` | 五个代次：`Theme`、`Window`、`Size`、`Topology`、`Device`。五项必须全部相等才可接受一帧 |
| `SceneSnapshot` | 只读布局与指标快照，含分组、指标、通知、DPI 与逻辑尺寸 |
| `SceneMetric` | 稳定 ID、显示值/单位、可选归一化值、状态（`Normal` / `Warning` / `Danger` / `Missing`）、缺失标记、趋势副本 |
| `GpuOutputDescriptor` | 适配器 LUID、宽高、像素格式、颜色空间、单调时间戳、内容矩形 |
| `IBackgroundFrameLease` | 单次所有权租约；`Dispose` 只能释放一次；预览帧必须显式声明 `IsPreview = true` |
| `IBackgroundFrameSource` | 按请求代次获取最新帧租约；**不得**在绘制路径中启动后台任务 |
| `CaptureCapabilities` | 捕获后端的能力与限制声明 |
| `CaptureDiagnostic` | 捕获状态、稳定错误码、可读消息、末帧龄、恢复尝试次数 |

### 5.2 `SceneBackgroundMode`

| 取值 | 语义 |
| --- | --- |
| `Procedural` = 0 | 背景由着色器程序化生成，不采样任何纹理 |
| `FixedTexture` = 1 | 背景为显式上传的固定纹理 |
| `LiveCapture` = 2 | 背景为实时桌面捕获帧 |

该取值被编码进常量缓冲的 `state.x`，**不得重排枚举顺序**。

### 5.3 常量缓冲契约

常量缓冲 `SceneParameters : register(b0)` 由 `MaterialFrameConstants` 结构体一一对应，结构体以 `Marshal.SizeOf<MaterialFrameConstants>()` 决定创建尺寸，因此**结构体字段顺序、类型与 HLSL 声明顺序必须完全一致**。

| 序 | 字段 | 类型 | 语义 |
| --- | --- | --- | --- |
| 1 | `viewport` | float4 | `(宽, 高, 材质模式, 动画秒数)`；材质模式 0 = 液态玻璃，1 = 赛博朋克 |
| 2 | `rootRect` | float4 | 玻璃根矩形 `(x, y, 宽, 高)`（物理像素） |
| 3 | `panel0` … `panel11` | float4 × 12 | 各分组框 `(x, y, 宽, 高)`（物理像素），超出 12 个的分组不参与材质渲染 |
| 4 | `theme0` … `theme5` | float4 × 6 | 材质相关参数组，语义随材质变化 |
| 5 | `motion` | float4 | `(动效开关, 目标帧率, 状态过渡毫秒, 扫描线周期毫秒)` |
| 6 | `desktop` | float4 | 捕获区域在显示空间的 `(x, y, 宽, 高)` |
| 7 | `windowRect` | float4 | 客户区在显示空间的 `(x, y, 宽, 高)` |
| 8 | `state` | float4 | `(背景模式, 分组数, DPI 缩放, 显示器旋转码)` |
| 9 | `presetStyle` | float4 | `(预设 ID, 星云边缘尺度, CRT 扫描线尺度, 结构尺度)`；`default=0`、`nebulaAurora=1`、`nixieBrass=2`、`crtPhosphor=3` |
| 10 | `paletteBase` | float4 | 主题 `color.background` 的 RGBA |
| 11 | `palettePanel` | float4 | 主题 `color.groupBackground` 的 RGBA |
| 12 | `paletteBorder` | float4 | 主题 `color.border` 的 RGBA |
| 13 | `paletteAccent` | float4 | 主题 `color.barLow` 的 RGBA |

- `state` 位于第 24 个 `float4`（偏移 368），五个预设字段从偏移 384 连续追加到 448；结构总大小为 **29 × float4 = 464 字节**。字段增减必须同时更新 C# 结构体、HLSL 声明和 CSO，否则常量缓冲与着色器期望不一致。
- `presetStyle.x` 选择着色器分支。缺键或显式 `default` 继续走原 `FixedBackdrop` / `GlassRoot` / `CyberRoot` / `AddGroup` 路径，以保持既有主题逐像素兼容。
- `theme` 组在两种材质下的角色：`theme0` 色调与不透明度，`theme1` 几何与光学参数，`theme2` 阴影基色与强度，`theme3` 高光色与中心清晰度，`theme4` 高光 / 反高光 / 描边 / 环境比例，`theme5` 玻璃样式、雾强度、染色强度与阴影半径。赛博朋克材质的 `theme5` 为零向量。

### 5.4 SDR BGRA8 合成语义

- 交换链、场景纹理、前景纹理、捕获纹理与固定输入统一使用 `B8G8R8A8_UNorm`，**全程 SDR**；颜色空间标识固定为 `SDR-sRGB`。
- 前景纹理为 **premultiplied** alpha，材质通道按 `color = color * (1 - foreground.a) + foreground.rgb` 合成，这是预乘 alpha 的标准 "over" 运算。
- 材质通道的输出 alpha 固定为 1（不透明呈现）；窗口的整体透明度由 WinForms 窗口属性承担，而不是由 GPU 通道输出 alpha 承担。
- **整条 GPU 路线不产生 HDR 输出，也不做 sRGB↔线性转换。**

### 5.5 代次语义

`RenderGenerations` 的五项代次分别覆盖主题修订、窗口句柄、尺寸、显示拓扑与设备：

- `CanAccept` 要求五项全部相等。任一项不匹配，该帧不得发布。
- `HasSameSessionIdentity` 只比较 `Window` / `Size` / `Topology` / `Device`——**主题修订可以在同一呈现会话内变化**，而窗口、尺寸、拓扑或设备变化必须走重建或 Resize，不得只改配置。

---

## 6. 线程与所有权规则

### 6.1 UI 线程独占

- **GPU 呈现的整个生命周期由 UI 线程独占。** `GpuPresentationHostController` 在构造时记录当前托管线程 ID，所有改变状态的公开入口都先做属主线程校验，不匹配即抛异常。`D3D11DeviceService` 同样记录属主线程，并在每次渲染前做属主探测。
- **不存在后台渲染线程。** 帧泵是 WinForms 计时器（间隔 8 ms）驱动的，绘制、场景构建、常量上传与提交都在 UI 线程完成。
- `FrameScheduler` 以目标帧率决定是否取帧，命中后**以当前时刻为基准重排下一帧的到期点**，不做累积式对齐——UI 计时器的节拍粗且依平台而异，累积对齐会让隔帧错过一个计时周期。
- `PowerPolicy.ResolveDisplay` 可以在窗口隐藏时暂停呈现（帧率 0），或在低功耗模式下压低帧率（Eco 10 帧、Auto 且使用电池 15 帧，取 `min(目标帧率, 上限)`）。暂停态不把 0 传给 `FrameScheduler`。

### 6.2 帧与场景的所有权

- **场景与背景各保留一个最新待处理项。** `LatestFrameMailbox<T>` 容量为 1，新项覆盖旧项并统计丢弃数；队列有界，绝不允许堆积。
- **帧租约是单次所有权。** `IBackgroundFrameLease.Dispose` 由 `Interlocked.Exchange` 保护，只能释放一次。渲染器取得租约后，在 `finally` 中释放；source 在所有权转移后不得再释放该帧。
- **捕获帧与预览帧不得混用所有权。** 预览路径不得创建桌面捕获会话。
- **GPU 热路径不得同步回读位图。** 证据截图是显式的冷路径：读回经由独立的 staging 纹理完成。

### 6.3 捕获排除的所有权与清理

窗口捕获排除由 `PresentationSession` 通过 `IPresentationWindowAdapter` 操作 `SetWindowDisplayAffinity`：

- 会话启动实时桌面背景时施加 `WDA_EXCLUDEFROMCAPTURE`（`0x00000011`），并记录原始亲和性；停止时恢复原始值。
- **停止流程先释放宿主对会话的引用**，再执行恢复：会话引用不再持有是义务而不是可选项。
- 恢复第一次失败时立即再试一次；两次都失败时，清理责任被**移交**为一条 `WindowCapturePolicyCleanup` 记录，由清理账本按退避表重试。
- 清理重试预算为 **6 次**，退避间隔依次为 **250 / 500 / 1000 / 2000 ms**；预算耗尽的原因码为 `gpu_capture_policy_restore_retry_exhausted`。
- **跨代次判定必须先于窗口存活性判定。** 清理记录的代次早于当前代次时，禁止对该 HWND 执行任何 Win32 调用（包括 `IsWindow`），直接终结；代次大于当前代次同样禁止触碰。`IsWindow` 只回答"这个数值当前是否是一个窗口"，无法区分"旧句柄已销毁"与"句柄数值被复用"。
- 同一代次仍有未完成的清理时，宿主不得叠加新的捕获排除所有者。

### 6.4 句柄代次

- `WindowHandleGeneration.Instance` 是**进程内唯一**的句柄代次真相源，`Advance()` 是唯一递增点。
- 生产路径不得另行构造 `WindowHandleGeneration` 实例——两份实例会让 GPU 清理链与实时捕获链读到不同的代次，跨代次丢弃判据恰好会在它要防的场景里失效。

### 6.5 单一捕获所有者

**同一时刻只允许存在一个捕获所有者。** GPU 路线退出时，GPU 宿主必须先停止，再恢复 GDI+ 路线的背景捕获。两个捕获所有者并存会重复抓屏，击穿 CPU 预算。

---

## 7. 性能预算与测量口径

### 7.1 帧率构成

呈现帧率由三段串接决定：

| 环节 | 决定因素 |
| --- | --- |
| 目标帧率 | `rendering.motion.targetFps`（1..240，默认 30），再由 `PowerPolicy` 按功耗模式与电池状态压低 |
| 提交节拍 | WinForms 计时器间隔 8 ms，`FrameScheduler` 按目标周期取帧 |
| 显示节拍 | `Present(1)` 等待合成器同步 ⇒ 实际显示帧率不超过 `min(目标帧率, 显示器刷新率)` |

### 7.2 需要测量的耗时段

GPU 路线的耗时必须分段归因，不能只报一个总帧时间。`GpuSceneRendererDiagnostics` 提供的分段与统计口径：

| 指标 | 含义 | 口径 |
| --- | --- | --- |
| `PresentIntervals` | 相邻两次成功呈现之间的间隔 | 墙钟；决定有效帧率 |
| `RenderDurations` | 从渲染开始到 `Present` 返回 | 墙钟；含场景构建、常量上传、绘制调用与同步呈现等待 |
| `GpuPassDurations` | 像素着色通道的 GPU 侧耗时 | D3D11 timestamp query；设备不支持时 `GpuTimestampStatus` 说明原因 |
| `CompositorFrameAges` | 合成器扫描输出到应用呈现调用的时延 | 由 `IDXGISwapChain.GetFrameStatistics` 的 `SyncQPCTime` 推导 |
| `CaptureFrameAges` | 捕获源交付的桌面帧的帧龄 | 来自 `CaptureDiagnostic.LastFrameAgeMilliseconds` |
| `LastPresentCount` / `LastSyncRefreshCount` | DXGI 帧统计 | 区分"从未尝试呈现"与"呈现了但被别的层盖住" |
| `PresentAttemptCount` / `PresentedFrameCount` | 呈现尝试数与成功呈现数 | 两者不等即为呈现停滞 |

补充测量要求：

- **统计窗口有界。** 每一路耗时都写入容量 2048 的滚动窗口，输出 `P50 / P95 / P99 / Max` 与样本数（`RollingTimingWindow` / `TimingPercentiles`）。诊断只报平均值或末值不得作为性能结论。
- **CPU 时间与墙钟时间不得混用。** 描述延迟用墙钟口径；推算 CPU 占用必须用 CPU 时间口径。`RenderDurations` 与 `PresentIntervals` 是墙钟量，不能直接换算成 CPU 占用率。
- **资源压力与时间指标同等重要。** 帧率达标但资源账本持续逼近预算，属于不可接受的结果；`GpuResourceSnapshot` 必须与时间指标一并采集。
- **证据采集是冷路径。** 场景截图会触发 GPU 读回，其耗时不得计入帧时间统计。

---

## 8. 支持矩阵

| 项目 | 要求 | 不满足时 |
| --- | --- | --- |
| 操作系统 | Windows 10 / 11 | 不支持 |
| 运行时 | .NET 8 桌面运行时（发布包为框架依赖） | 无法启动 |
| 显卡 | 提供 DXGI 硬件适配器且支持 DirectX 11 | 回退 `Legacy`（GDI+）路线，面板正常显示 |
| 主题 | 主题 JSON 的 `rendering.backend = "gpu"` | 该主题保持 GDI+ 外观 |
| 用户开关 | 设置 → 主面板 →「GPU 材质渲染」 | 不进入 GPU 路线，原因码 `gpu_effects_opt_in_required` |
| 实时桌面背景 | 设置 → 主面板 →「实时桌面背景」且 WDA 捕获政策获用户接受 | 停留在 `GpuFixedInput`，原因码 `wda_capture_policy_not_accepted` |
| 捕获后端 | DXGI Desktop Duplication | 停留在 `GpuFixedInput`，呈现固定或程序化背景 |
| 多屏 / 混合 DPI | 支持，窗口可拖到副屏（含负坐标原点） | — |
| 色彩 | SDR BGRA8（`SDR-sRGB`） | HDR 显示按 SDR 输出 |

---

## 9. 两条背景捕获链并存

仓库中存在两条彼此独立的背景帧获取链。它们在命名空间、捕获 API、帧载荷类型、所有权模型和消费者上都不重合，**不共享帧缓冲，也不得互相回退到对方的帧类型**。

| 维度 | 链 A：`src/System/Backdrop/` | 链 B：`src/Rendering/Capture/` |
| --- | --- | --- |
| 命名空间 | `LiteMonitor.src.SystemServices.Backdrop` | `LiteMonitor.Rendering.Capture` |
| 捕获 API | GDI+ `Graphics.CopyFromScreen` | DXGI Desktop Duplication |
| 帧源抽象 | `IBackdropFrameSource` | `IBackgroundFrameSource` / `IBackgroundFrameLease` |
| 帧载荷 | `System.Drawing.Image` | `GpuDesktopFrameLease`（持有 `ID3D11Texture2D`） |
| 帧所有权 | **借用**引用；调用方与渲染器不得释放，释放责任在生产者一侧 | **单次所有权租约**；渲染器取得后自行释放 |
| 目标帧率 | 默认 2 帧/秒，夹紧到 1..6 | 跟随 GPU 路线的呈现节奏 |
| 捕获降采样 | 默认 0.25 | 无（目标显示器全尺寸 BGRA8） |
| 自身排除 | 抓屏期间**瞬时**施加 `WDA_EXCLUDEFROMCAPTURE`，抓完立即还原，避免自反馈 | 窗口级**持续**排除，由 `PresentationSession` 持有，停止时恢复 |
| 排除失败的处理 | 取消或失败路径必须恢复显示亲和性 | 两次内联恢复失败后移交清理账本（6 次预算 + 250/500/1000/2000 ms 退避） |
| 降级模型 | `BackdropModeResolver`：glass → gradient → solid，每一级降级都带原因码 | `RenderingRoutePolicy`：`GpuLiveBackground` → `GpuFixedInput` → `Legacy` |
| 消费者 | `src/UI/LiveGlassController` → `src/UI/AppearanceRuntime` | `src/Rendering/Presentation/GpuSceneRenderer` |

两条链的差异是**架构决策而非重复实现**：链 A 服务于 GDI+ 路线的位图背景，链 B 服务于 GPU 路线的纹理背景，二者的帧载荷类型与所有权模型不可互换。

链 B 的结果枚举 `DesktopCaptureResult`：`FrameAvailable`、`LastFrameRetained`、`NoFrameYet`、`AccessLost`、`Failed`、`StaleGeneration`、`Disposed`。无新帧时**保留最后一帧有效帧并记录其年龄**，等待超时**不得**记为故障。

---

## 10. 已知限制

- **HDR 未实现。** 交换链、场景纹理、前景纹理与捕获纹理统一为 `B8G8R8A8_UNorm`，颜色空间固定 `SDR-sRGB`。
- **跨适配器时间戳未验证。** DXGI Desktop Duplication 的帧时间戳仅在输出与渲染适配器一致时可靠；`CaptureCapabilities` 以 `Limitation` 字段明示该边界。
- **跨适配器显示器无法捕获实时桌面。** 窗口所在显示器不在渲染适配器的输出列表时，捕获必然失败，原因码 `capture_output_not_on_render_adapter`；此时渲染以固定或程序化背景继续，不出现空洞。
- **捕获失败后不重复重试同一块屏幕。** 渲染器记住失败时的显示器句柄，同屏不再重建捕获对象；换屏或显示设置变更后允许再试一次。这避免了每帧重建捕获对象把设备拖入 `DEVICE_REMOVED`。
- **WDA 排除作用于整个顶层窗口。** 开启实时桌面背景后，受支持的截图与录屏工具看不到该窗口区域；该行为由设置项与首次开启时的产品政策确认显式承接。
- **玻璃折射与模糊是单趟近似。** 材质通道为一次像素着色器调用，折射与模糊由多次采样近似合成，不是离屏多 pass 的真实折射。
- **分组数量上限为 12。** 超出 `SceneMaterialLayoutBuilder.MaximumGroups` 的分组不参与材质渲染。
- **赛博朋克材质不支持任何桌面捕获。** 该材质的背景始终是程序化生成，主题声明 `liveBackground = true` 会被判为校验错误。
- **尺寸变更期间的帧可能短暂沿用旧尺寸。** 交换链改尺寸成功后才重建尺寸相关资源，改尺寸失败时渲染器保持按旧尺寸继续绘制。
