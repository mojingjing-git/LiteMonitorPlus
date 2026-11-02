# 主题工作室架构

## 1. 适用范围

本文描述 LiteMonitor++ **主题工作室**（Theme Studio）的当前架构：主题的可视化编辑、属性检查、多路线预览、保存、应用与导出。

适用范围：

- 程序集 `src/ThemeEditor`：工作室窗体、属性目录、检查器、预览控件、编辑会话、导出服务；
- 它与 `src/Core`（主题模型、`Settings`、`AppActions`）、`src/Rendering`（渲染配置、材质、legacy 回退样式解析）、`src/UI`（工作区明暗配色、主窗体装配）的交界。

不在本文范围：主题 JSON 的字段 schema 规范、插件市场条目的发布契约、GPU 渲染器内部实现、主监控面板的运行时布局与采集链路。

阅读约定：文中出现的类型名、成员名、枚举值、诊断码字符串均为代码中的契约名，可直接对照源码检索；文中不引用行号。

---

## 2. 组件划分与依赖方向

### 2.1 组件职责

| 组件 | 职责 | 明确不承担 |
|---|---|---|
| `ThemeStudioForm` | 编辑器窗体。三栏布局（资料库 / 画布 / 检查器）、文档工具栏（撤销 / 重做 / 保存 / 另存为 / 应用 / 导出）、画布工具栏（编辑目标、预览形态）、状态区（保存状态 + 实际预览路线）。负责视图状态、会话生命周期编排与用户交互 | 不写主题文件、不改运行态主题、不做渲染 |
| `ThemeStudioLauncher` | 入口装配与生产应用网关实现。`Open` 构造网关并打开工作室；`AppActionsThemeApplyGateway` 实现运行态生效 | 不含编辑逻辑 |
| `ThemePropertyCatalog` | 属性目录。持有全部字段描述符，按「草稿 + 编辑目标」计算可见字段集；同时提供点选定位的路径解析 | 不含 UI 控件、不读写文件 |
| `PropertyInspector` | 检查器。按描述符分组渲染编辑行，把用户编辑提交给会话；维护「恢复默认」入口与共享标注 | 不直接读写 `Theme`、不碰文件 |
| `ThemePreviewControl` | 经典预览宿主。持有 `AppearanceRuntime`，按目标解析实际绘制主题，驱动经典竖向渲染，并托管 GPU 子控件 | 不写草稿、不释放源主题字体 |
| `GpuThemePreviewControl` | GPU 预览。建立 D3D 设备与 SwapChain，生成场景并出帧；支持参数热更新 | 不构造采集源、不访问桌面 |
| `ThemePreviewViews` | `ThemePreviewScene`（固定模拟场景）、`HorizontalPreviewControl`（GDI 横排）、`TaskbarPreviewControl`（任务栏） | 不建采集源、不读实时硬件、不写盘 |
| `PreviewFrameSource` | 固定样例图的帧来源实现，`IsPreview` 恒为 true，使预览帧在诊断中报 `PreviewFrame` | 不含任何抓屏调用 |
| `ThemeDocumentSession` | 编辑会话。源字节 + 覆盖层 + 草稿 + 撤销/重做 + 脏标记 + 保存 / 另存为 / 恢复 | 不触碰运行态主题与设置 |
| `ThemeDocumentHistory` | 事务化撤销 / 重做，容量与合并窗口有界 | 不持久化 |
| `ThemeDocumentStore` | 主题文档读写入口与恢复草稿目录管理，封装摘要与原子写调用 | 不做业务校验 |
| `ThemeExportService` | 导出服务。主题 JSON、效果图、投稿资料三条出口 | 只读会话，不写会话 |
| `ThemeFileService` | 主题文件的原子写核心（SafeWrite）与文件级操作（列表 / 复制 / 删除 / 改名） | 不含主题语义 |
| `ThemeStudioForm.IThemeApplyGateway` | 应用网关接口。工作室对运行态的唯一写入口 | 由外部实现，工作室不反射主窗体私有状态 |

同一程序集内另有 `ThemeEditorForm`（基于 `AppearanceFields` 硬编码行面板的表单式编辑窗体，`ThemeEditHistory` 提供 32 个配置快照的无重做撤销）。它不在菜单入口上，与工作室的会话模型互不共享状态。

### 2.2 依赖方向

- `src/ThemeEditor` 依赖 `src/Core`、`src/Rendering`、`src/UI` 的公开类型。
- 工作室**不得**依赖 `MainForm` / `UIController` 的私有状态；运行态生效一律经 `IThemeApplyGateway` 由外部注入。
- `src/UI/MenuManager` 是主题编辑入口的唯一装配点，调用 `ThemeStudioLauncher.Open`。
- `src/Core/Actions/AppActions` 提供运行态生效与设置持久化的动作层，工作室只消费其结果。

### 2.3 窗口与 DPI 合同

- 工作室是标准 WinForms `Form`：使用系统标题栏与边框，非客户区不自绘。
- 缩放模式为 `AutoScaleMode.None`。句柄创建后读取 `DeviceDpi`，全部几何经唯一的 `Dip()` 入口换算；DPI 变化时按登记的 `_dpiResetters` 清单重算固定几何，检查器内部几何由 `PropertyInspector.ApplyDpi` 同步重算。
- 工作区配色读既有明暗引擎 `SettingsThemeManager.CurrentPalette`（`SettingsThemePalette`），并订阅主题变化事件跟随系统主题切换。
- 画布底色是**主题产物**，不参与工作区明暗：被工作区染色即等于伪造预览。

---

## 3. 编辑目标与视图状态

编辑目标 `ThemeEditTarget` 决定「用户想编辑哪条路线」，与「实际渲染路线」是两个独立概念。

| 取值 | 含义 | 适用文档 |
|---|---|---|
| `Classic` | 经典外观：配色、文字、布局 | 经典主题（唯一目标） |
| `GpuMain` | GPU 主效果：材质参数、动效，加上共享字段 | GPU 主题 |
| `GpuFallback` | GPU 兼容外观：`rendering.fallbackStyle` 六槽 | GPU 主题 |

不变量：

- 切换编辑目标**只改变视图**：绝不修改 `rendering.backend`、绝不写文件、绝不制造脏状态。
- 目标集合由草稿的 `rendering.IsGpuRequested` 决定；经典主题不提供 GPU 目标，GPU 主题默认目标是 `GpuMain`。
- `ThemePropertyScope.Shared` 描述符在 GPU 目标下依然可见，并在检查器中标注为共享项。布局项始终共享；字体与颜色项在命名预设（`nebulaAurora`、`nixieBrass`、`crtPhosphor`）或 `glassStyle == floatingtoolbar` 时由命名预设预览消费；其余默认材质路径使用固定前景。
- 预览形态 `PreviewShape`（`Vertical` / `Horizontal` / `Taskbar`）与编辑目标一样是纯视图状态。

---

## 4. 属性目录模型

### 4.1 字段描述符

`ThemePropertyDescriptor` 是 UI 与预览共用的单一事实来源。契约层字段：

| 字段 | 含义 |
|---|---|
| `DisplayName` | 检查器显示名 |
| `JsonPath` | 主题 JSON 路径，点分规范名（如 `color.background`、`rendering.parameters.glow`） |
| `Group` | 分组标题 |
| `Scope` | 作用域 `ThemePropertyScope`：`None` / `Classic` / `GpuMain` / `GpuFallback` / `Shared` |
| `EditorKind` | 控件形态 `ThemePropertyEditorKind`：`Text` / `Color` / `Number` / `Boolean` / `Choice` |
| `Invalidation` | 预览失效级别 `ThemePropertyInvalidation`：`Color` / `Layout` / `Font` / `Rendering` |
| `Minimum` / `Maximum` | 数值取值域（`Number` 形态） |
| `Choices` | 候选项集合（`Choice` 形态） |
| `Getter` | 从草稿读当前值 |
| `Setter` | 把值写回草稿 |
| `DefaultGetter` | 模型层缺省值 |
| `ToJson` | 值 → JSON 节点；返回 null 表示删除该键 |

### 4.2 元数据取代硬编码行面板

不变量：

- 检查器的行必须由描述符渲染 ⇒ 增加一个可编辑字段只需在目录中增加一条描述符，检查器不新增控件代码。
- 检查器不得直接读写 `Theme`；一切改动经 `ThemeDocumentSession.ApplyEdit` 提交。
- 描述符的取值域必须与生产解析器一致（`ThemeRenderingConfig.Validate`）⇒ 检查器不得给出运行时非法的值。
- 字段可见性按目标过滤：只显示该材质运行时真正消费的参数。跨材质不消费的项不得出现，避免「能调但无效果」。
- 描述符集合的计算入口是 `ThemePropertyCatalog.GetProperties(theme, target)`，UI 与预览共用同一入口，不得各自判断能力。
- `thresholds` 不作为可编辑属性暴露：状态阈值来自当前生效设置，主题只负责颜色。主题 JSON 中已有的 `thresholds` 块按源文档原样读写。

### 4.3 分组

- 经典目标：`配色`、`文字`、`布局`。
- GPU 主效果：`GPU 材质`、`视觉预设`、`动效`，加上共享的布局项；字体与 `color.*` 项在命名预设下出现，字体项在 floatingtoolbar 样式下出现。
- GPU 兼容外观：`兼容外观配色`（六槽）。

GPU 材质组按材质分列：液态玻璃材质暴露圆角半径、玻璃厚度、折射强度、模糊半径、中心透明比、表面不透明度、高光强度、逆光高光、阴影强度、雾化强度、染色强度、阴影半径与玻璃样式；赛博朋克材质暴露主切角、次切角、描边宽度、辉光强度、环境光比、面板不透明度与阴影强度。动效组包含启用动效、目标帧率、状态过渡与扫描周期。

`rendering.parameters.visualPreset` 使用字符串 Choice，而不是整数索引：候选值只由当前材质决定，Getter 在键缺失时返回 `default`，提交时向 Setter 传选中字符串；恢复默认和选择 `default` 都删除该键。预设修改属于 `Rendering` 失效，`ThemeStudioForm` 必须立即重建检查器目录，确保字体、颜色和 `glassStyle` 行的出现或消失与草稿一致；撤销、重做同样重建。Nebula 不显示 `glassStyle` 的 floatingToolbar 选项，命名预设均不允许实时捕获。

### 4.4 取值域与缺省恢复

- 取值域由描述符的 `Minimum` / `Maximum` 承载，检查器以提示形式呈现，并在提交前夹紧。
- 缺省值取自模型层：`DefaultGetter` 读取一个干净实例的同一路径，不使用硬编码常量。
- 「恢复默认」= 删除该路径的覆盖（写回后该键从 JSON 中消失，由模型缺省接管）+ 把草稿字段写回模型缺省值。
- 覆盖标记由会话维护（`ThemeDocumentSession.HasOverride`）；检查器据此显示或隐藏「恢复默认」入口。
- 可空配色字段的 null 语义在目录中保持：删除覆盖即回到模型缺省，而不是写入一个空字符串。

### 4.5 点选定位

语义元素角色 `ThemePropertyCatalog.SemanticElementRole`：`PanelBackground` / `GroupHeader` / `ItemLabel` / `ItemValue` / `ProgressBar` / `Title`。

- `ResolvePropertyPath(theme, role, target)` 按角色候选路径依次解析，返回该目标下**真实存在**的描述符路径。
- 该角色在当前目标下没有可编辑字段时返回空串，调用方据此如实告知「无可编辑字段」，不得指向虚构路径。
- `PropertyInspector.FocusProperty` 把焦点落到可编辑控件上并滚动到可见区域。

---

## 5. 预览机制

### 5.1 形态与路线

| 形态 | 经典路线 | GPU 路线 |
|---|---|---|
| `Vertical` | `UILayout` + `UIRenderer.Render` | `GpuSceneRenderer` + `UiSceneSnapshotAdapter.Build` |
| `Horizontal` | `HorizontalLayout` + `HorizontalRenderer.Render` | `UiSceneSnapshotAdapter.BuildHorizontal` + `GpuSceneRenderer` |
| `Taskbar` | `HorizontalLayout`（`LayoutMode.Taskbar`）+ `TaskbarRenderer.Render` | 无：任务栏恒为常规绘制 |

画布按形态路由到不同控件；状态区必须按**实际**渲染路线报告（GPU 场景 / 兼容外观 · 常规绘制 / 经典渲染），不得按主题声明路线报告。

### 5.2 经典预览

`ThemePreviewControl` 的驱动路径：

1. `SetTheme(theme, target, shape)` 解析实际绘制主题（`ResolveGdiTheme`）：兼容外观目标、以及主效果目标在 GPU 不可用时，改用 `LegacyFallbackStyleResolver.BuildDerivedTheme` 的派生主题。
2. `UILayout(CreatePreviewTheme(themeToRender, dpiScale))` 构建布局。
3. 固定模拟数据由 `CreateMockGroups` 提供。
4. `AppearanceRuntime` 以 `previewSource` 参数注入固定样例图，背景层走「预览固定源优先」分支，**完全不注册帧后端**。
5. 绘制走 `UIRenderer.Render(g, groups, theme, appearance)`，与主窗口同源：外框、辉光、窗口底纹与进度条样式均由正式渲染层绘制。

### 5.3 GPU 预览

`GpuThemePreviewControl` 的驱动路径：

1. `MaterialRuntimeSettings.Create(theme.Rendering, material, theme)` 解析材质与动效参数。
2. `GpuSceneRenderer.TryCreate` 建立 D3D 设备与 SwapChain。
3. 场景由 `UiSceneSnapshotAdapter.Build`（竖向）或 `BuildHorizontal`（横向）生成。
4. 材质未变时，`UpdateTheme` 走 `UpdateConfiguration` 热更新，复用既有设备与 SwapChain（`SessionUpdateCount` 自增）；材质变化才重建会话（`DeviceCreationCount` 自增）。
5. 目标帧率必须随主题的 `rendering.motion.targetFps` 同步到调度器，否则帧率与主题声明不一致。
6. 控件自身与整条祖先链不可见时不出帧，恢复可见时重置帧基准。

失败原因写入 `Error`，取值包括 `preview_not_ready`、`theme_rendering_not_gpu`、`theme_rendering_invalid: <明细>`、`theme_material_legacy`、以及启动保护返回的 `<guardCode>: <guardMessage>`。失败必须留下原因，宿主据此切换到同形态的常规绘制回退。

### 5.4 预览副本语义

不变量（预览不得修改真实主题）：

- 预览只读草稿，绝不修改草稿主题对象；主题被就地修改后必须重建预览副本，不能按引用复用。
- 预览绝不触碰运行态主题、设置与采集器。
- 布局使用独立副本 `LayoutConfig.CreatePreviewCopy(dpiScale)`，复制全部配置字段与扩展数据，且**只缩放一次**（源已缩放过时不得重复缩放）。
- 颜色、阈值、背景配置只读借用。
- 字体只借用不释放：预览主题绝不调用 `DisposeFonts`，派生主题共享源主题的运行期字体实例且不二次 `BuildFonts`。
- 预览不抓屏、不读实时硬件、不写盘；玻璃底图来自固定样例图。
- 实际 DPI 取控件自身的 `DeviceDpi`，不得使用进程级缩放因子。
- 归一化只发生在运行时副本，绝不回写用户 JSON。
- 横向与任务栏预览必须显式 `new Settings()`，不得读取用户磁盘配置。
- 任务栏预览必须使用自持的 `TaskbarRenderer.TaskbarStyleContext`，不得改写生产静态样式。
- 任务栏预览必须使用工作室传入的当前设置快照，而不是控件构造时的空设置。

### 5.5 GPU 启动保护隔离

- 预览使用独立状态目录 `%LOCALAPPDATA%\LiteMonitor++\gpu-startup-preview`（`GpuThemePreviewControl.PreviewGuard`）。
- 预览不得读写生产的崩溃标记，也不得消费或清除生产的保守状态 ⇒ 预览异常只影响预览自身，不会把主窗体的下次启动打成保守模式。
- 导出链的 GPU 出图使用一次性临时目录守卫，进程退出即清理，不在 `%LOCALAPPDATA%` 留下状态。

---

## 6. 编辑态隔离

### 6.1 草稿态与运行态

- 工作室只持有 `ThemeDocumentSession`；不持有主窗体主题、运行期字体、GPU 会话或采集器。
- 运行态主题由 `ThemeManager` 与 `UIController` 持有，工作室对它的唯一写入口是应用网关。
- 编辑目标、预览形态、列表选中项、缩放都是**视图状态**：不写主题、不制造脏状态。

### 6.2 会话模型：源字节 + 覆盖层

会话同时保留两份信息：**源 JSON 字节**（打开时的原始内容）与**覆盖层**（路径 → 值或删除标记）。草稿对象供预览与检查器读取，覆盖层供保存写回。

不变量：

- 打开后未编辑就保存 ⇒ 写回内容与源文件逐字节相同。
- 编辑后保存 ⇒ 在源 JSON 上就地替换被编辑的键；未触碰的字段（含全部未知扩展键）原样保留。
- 保存绝不全量序列化 `Theme` 对象（全量序列化会把缺省物化进文件）。
- 键定位大小写不敏感，但写回时保留源文件里的原键名大小写。
- 覆盖值为 null 表示删除该键；删除一个本就不存在的键是无操作。
- 路径中段被非对象占据时，以规范名重建对象后写入新键，与解析器口径一致。
- 载荷构建前必须重新通过既有解析与 `rendering.Validate()`，校验不通过则不写盘。

### 6.3 撤销栈

- 撤销单位是**事务**：同一路径在 800 ms 合并窗口内的连续编辑并入同一事务（滑块拖动、连续输入）。
- 支持重做；同一事务内同一路径的多次编辑只保留「事务前 / 事务后」两端。
- 容量上限：100 个事务或 8 MiB 快照总量，任一触限淘汰最旧事务。
- 保存不清理历史 ⇒ 撤销跨越保存点是合法的；撤销回到保存点即重新变脏。
- 撤销 / 重做恢复配置快照并同步覆盖层变更；`thresholds` 的清理必须重申，以免快照复活已清理的编辑。

### 6.4 脏标记

- `Revision` 每次提交编辑自增；`SavedRevision` 记录保存基线。
- `IsDirty ⇔ Revision != SavedRevision`。
- 文档工具栏的脏标记、标题栏星号、状态区文案都由该事实派生。
- 保存期间继续的编辑只体现为 `Revision` 继续前进，不影响刚写入的基线。

### 6.5 恢复草稿

- 空闲 2 秒持久化恢复快照到 `%LOCALAPPDATA%\LiteMonitor++\theme-studio\drafts`。
- 目录上限 10 份 / 总量 8 MiB，超限按最旧淘汰（当前主题除外）。
- 恢复时源文件摘要一致 ⇒ 干净恢复；摘要不一致或源文件缺失 ⇒ 打开**恢复副本**，此时保存必须先另存为新主题。
- 恢复副本以草稿配置为新源，不触碰磁盘上的任何主题文件。
- 保存成功后删除对应的恢复草稿。

### 6.6 阈值覆盖清理

`thresholds` 块的覆盖不落盘。会话在构建载荷前清理这些覆盖，并把草稿的阈值恢复为源文档值（源文档没有该块时保持模型缺省实例，不合成）；同时从撤销 / 重做历史中移除触碰过这些路径的整个事务。

---

## 7. 导出契约

### 7.1 原子写

- 所有写盘走 `ThemeFileService` 的共用 SafeWrite 核心：先写**同目录**临时文件并确保落盘，再用它替换目标。
- 替换**绝不先删除目标文件**；任何一步失败都保留原文件并把异常上抛，调用方绝不会收到「保存成功」。
- 目标存在 ⇒ `File.Replace`（同卷原子替换，保留目标文件元数据）；目标不存在 ⇒ 直接移动；`File.Replace` 不可用或失败 ⇒ 退到「移动并覆盖」，同样不先删目标。
- 临时文件必须与目标同目录，否则最后一步会退化为跨卷移动。
- 写入编码统一为无 BOM 的 UTF-8。

### 7.2 语义等价

- 导出载荷与会话当前修订**逐字节一致**（导出复用会话内部的载荷构建入口，含未知扩展键与缺省不物化语义）。
- 导出是**只读**操作：不写入、不保存、不改写会话身份。
- 导出的主题 JSON 必须能被既有解析器接受；`rendering.Validate()` 的问题只进诊断通道，不改变「文件已写出」这一事实。
- SHA-256 与字节数对**写入文件字节**计算。

### 7.3 外部修改检测

- 会话记录源文件 SHA-256；保存前比对磁盘摘要，不一致 ⇒ 结果标记 `ExternalConflict`。
- 冲突时提供三路选择：覆盖外部更改 / 另存副本 / 保留草稿并取消。覆盖是显式授权的。
- 外部删除文件不算冲突。
- 恢复副本不得直接保存 ⇒ 必须先另存为。
- 另存为在同名主题已存在时失败，不覆盖既有文件；新路径写入成功后才切换文档身份。

### 7.4 效果图

- 实际渲染路线枚举 `ThemeExportImageRoute`：`Classic` / `Gpu` / `Fallback`；路线偏好 `ImageRoutePreference`：`Auto` / `Classic` / `Gpu`。
- 画幅固定：竖向 240×420、横向 400×120、任务栏 400×32 ⇒ 导出结果可复现。
- 诚实性硬规则：只有真正由 GPU 渲染器产出的图才允许标记为 `Gpu`；GPU 主题在 GPU 不可用时产出的是 `Fallback`。
- 强制 GPU 路线而 GPU 不可用 ⇒ 导出失败，绝不产出经典图冒充 GPU 图。
- 任务栏形态由 GDI 任务栏渲染器产出 ⇒ 按 GPU 路线导出时报失败。
- 效果图的路线标注写进结果与诊断，提交方据此判断。

### 7.5 投稿资料

- 产出四个文件：`theme.json`、`submission.json`、`README.txt`、`preview.png`。
- 本地只做结构与语义校验（JSON 可解析、`rendering.Validate()`、颜色字段统一校验）与远程引用自检；**不**产出市场校验结论，资料状态为「待市场审核」。
- `submission.json` 的 `route` 字段是**主题声明路线**（`gpu` | `legacy`），与 `rendering.backend` 互验；实际出图路线由 `previewRoute` 与 `previewIsFallback` 承载。
- 声明路线与实际出图路线不一致 ⇒ 记入 `ReviewRequired`，交人工确认。
- `ThemeSubmissionMetadata.MarketVersion` 是市场条目版本串，与主题格式的整数 `version` 互不推导。
- 主题包含 `http/https` 引用 ⇒ 记入 `ReviewRequired`。

---

## 8. 保存与应用

### 8.1 两条独立路径

保存与应用是两条独立路径，任何一条都不隐含另一条。

**保存**（写文件）：

1. 关闭打开中的撤销事务；
2. 构建载荷（先清理阈值覆盖；无覆盖时载荷即源字节）；
3. 载荷自检：可解析 + `rendering.Validate()`；不通过则不写盘；
4. 写盘：`ThemeDocumentStore.WriteThemeJson` → `ThemeFileService` 的 SafeWrite 核心；
5. 成功后更新源字节、源摘要与保存基线，并删除恢复草稿。

失败时原文件与草稿保持原样，保存基线不前进。

**应用**（让运行态生效）：工作室只把 `themeName` 与 `revision` 交给 `IThemeApplyGateway.ApplySavedTheme`，自己不写文件、不触碰运行时对象。

### 8.2 应用结果编排

应用网关按三个阶段编排，阶段枚举 `ThemeApplyStage`：`ThemeSaved` / `SettingsPersisted` / `ThemeApplied`。

1. **文件预检（`ThemeSaved`）**：用 `ThemeManager.Inspect` 预检主题文件。非法文件不得先持久化再静默套用缺省主题。
2. **Settings 持久化（`SettingsPersisted`）**：写入 `Settings.Skin` 并消费 `TrySave()` 的返回值。持久化失败 ⇒ 回滚内存值并失败返回，绝不进入运行时应用；持久化未落盘时不得报告「已保存并应用」。
3. **运行时应用（`ThemeApplied`）**：调用 `AppActions.ApplyThemeAndLayoutWithResult`。同名文件变更强制重载，不按 Skin 名跳过加载。

结果语义（`ThemeApplyResult`）：

- `Success = false` 且 `Partial = true` ⇒ 主题已生效但部分模块失败。应用**不是原子事务**，不存在自动回滚，必须如实表达。
- 运行时未真正加载请求主题 ⇒ 失败并报告当前实际主题。
- 无运行时宿主 ⇒ 不得报告「已生效」。
- 运行时抛异常但主题已加载 ⇒ 报 `Partial`，不得回滚设置并声称旧主题仍在生效。
- 生效路线由「主题是否请求 GPU」→「运行时是否真的在跑 GPU」→「降级标注」三级判定；降级码只用于标注，不参与路线判定。

### 8.3 两路径的耦合点

- 文件写入只发生在保存、另存为与导出三处；应用链路不写主题文件。
- 应用请求携带 `revision`，运行时结果携带生效修订；工作室在状态区报告修订号与生效路线。
- 工作站在有未保存修改时先保存再应用；保存失败则不进入应用。
- 应用结果以状态区文案呈现：成功、部分失败、按阶段失败三类分别给出可区分的描述。

---

## 9. 关键不变量汇总

1. 预览不得修改草稿主题，不得触碰运行态主题、设置与采集器。
2. 预览的布局必须是独立副本且只缩放一次；字体只借用不释放。
3. 工作室对运行态的唯一写入口是应用网关；不得反射主窗体私有字段。
4. 编辑目标与预览形态是视图状态：不得写主题、不得制造脏状态。
5. 检查器的行由描述符渲染；检查器不得直接读写 `Theme`。
6. 描述符取值域必须与生产解析器一致；不可消费的字段不得出现。
7. `thresholds` 不落盘、不可编辑。
8. 未编辑的文档保存必须与源文件逐字节相同；保存不得全量序列化主题对象。
9. 写盘绝不先删除目标文件；失败必须保留原文件并上抛异常。
10. 导出载荷必须与会话当前修订逐字节一致；导出不得修改会话。
11. 效果图必须标注实际渲染路线；GPU 不可用时不得产出经典图冒充。
12. 脏状态只由 `Revision != SavedRevision` 决定。
13. 恢复副本必须另存为，不得写回原文件。
14. 预览的 GPU 启动保护与生产隔离，不得读写生产的崩溃标记。

---

## 10. 已知限制

- 状态阈值来自当前生效设置，主题 JSON 中的 `thresholds` 块不参与编辑也不落盘。
- 任务栏颜色由 `Settings` 驱动，不随主题变化；主题只影响任务栏的列配对、内边距与行模式。
- GPU 主效果与兼容外观的进度条颜色由材质着色器决定，这两个目标下没有对应的可编辑字段。
- `standard` 与 `cyber_punk` 的默认材质路径使用固定前景，不消费主题字体；命名预设是例外，检查器显示主题字体与颜色项。
- GPU 预览不可用时，主效果目标退化为兼容外观的常规绘制；该退化只影响预览呈现，不改动文档内容。
- 应用不是原子事务：部分模块失败时主题可能已生效，且不提供自动回滚。
- 恢复副本无法直接写回原文件，必须另存为新主题。
- 预览模拟数据是固定常量，不反映运行机器的实时硬件状态。
