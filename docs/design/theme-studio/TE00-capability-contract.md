# TE00 能力合同与基线：主题工作室重做的事实清单

- 日期：2026-10-26。
- 状态：已完成源码核实与测试基线；风险原型见第 8 节。
- 上游计划：[主题工作室重做计划](../../plans/2026-10-26-theme-studio-rebuild-plan.md)。
- 源码基线：master @ `035ccdf2d5c7561216f9a765db8a15ec53a9a55c`（merge P-E08a）。
  工作树差异：`docs/development-windows-firewall.md`、`docs/verification/acceptance/F00-baseline.md`（文档修改），新增 `docs/design/theme-studio/`、`docs/plans/2026-10-26-theme-studio-rebuild-plan.md`，无产品代码改动。
- 事实来源：本文所有"行为"条目均给出 `文件:行号`；行为描述以代码为准，不以注释或计划为准。

## 1. 三个独立状态的现状映射

| 状态 | 现状落点 | 事实 |
|---|---|---|
| 主题类型 | `ThemeRenderingConfig.IsGpuRequested`（ThemeRenderingConfig.cs:58）仅看 `rendering.backend`；校验强制 gpu 必须 GPU 材质、legacy 必须 legacy 材质（:95-103） | 类型由 backend 决定，无第三种 |
| 编辑目标 | 现编辑器**不存在**目标概念；只有 `IsGpuTheme`/`IsToolbarTheme` 布尔和只读提示（ThemeEditorForm.cs:50-64） | GPU 主题的"主效果／兼容外观"为新增概念 |
| 实际预览路线 | 主竖向 GPU：`GpuPresentationHostController`（UIController.cs:62）；**横向 GPU 同样存在**：GPU 激活时横屏走场景快照 `UiSceneSnapshotAdapter.BuildHorizontal`（UIController.cs:1950-1967），GPU 未激活才走 `HorizontalRenderer.Render`（:821）legacy 绘制；任务栏 `TaskbarRenderer.ReloadStyle`（:245）**始终常规绘制**（场景适配器无任务栏构建器） | "任务栏 · 常规绘制"标签有代码事实支撑；"横向"不能照搬该标签 |

生产消费点：`UIController.CurrentRenderTheme => _legacyFallbackTheme ?? ThemeManager.Current`（UIController.cs:1569）。

## 2. 逐字段能力矩阵

### 2.1 GPU 渲染参数（rendering.parameters）——按材质分列消费

事实来源：`MaterialRuntimeSettings.Create`（MaterialRuntimeSettings.cs:51-146）；校验范围：ThemeRenderingConfig.cs:116-138。

| 参数 | 校验范围 | liquid_glass 消费（默认值） | cyber_punk 消费（默认值） |
|---|---|---|---|
| cornerRadius | 0-64 | ✅ 26 / toolbar 30 | ❌ 固定 0 |
| thickness | 0-8 | ✅ 1 / toolbar 8 | ❌ 固定 0 |
| refraction | 0-8 | ✅ 1.6 / toolbar 8 | ❌ 固定 0 |
| blurRadius | 0-32 | ✅ 8 | ❌ 固定 0 |
| clearCenterRatio | 0-1 | ✅ 0 | ❌ 固定 0 |
| surfaceOpacity | 0-1 | ✅ 0.055 | ❌ 固定 1 |
| highlight | 0-1 | ✅ 1 / toolbar 0.72 | ❌ 固定 1 |
| counterHighlight | 0-1 | ✅ 1 | ❌ 固定 1 |
| shadow | 0-1 | ✅ 0.34 / toolbar 0.115 | ✅ 0.34 |
| fogStrength | 0-1 | ✅ 0.42 / toolbar 0.16 | ❌ 固定 0 |
| tintStrength | 0-1 | ✅ toolbar 0.038，否则取 surfaceOpacity | ❌ 固定 1 |
| shadowRadius | 0-64 | ✅ 0 / toolbar 30 | ❌ 固定 0 |
| primaryCut | 0-64 | ❌ 固定 0 | ✅ 14 |
| secondaryCut | 0-32 | ❌ 固定 0 | ✅ 9 |
| outlineWidth | 0-8 | ❌ 固定 0 | ✅ 1 |
| glow | 0-100 | ❌ 固定 0 | ✅ 12 |
| ambientRatio | 0-1 | ❌ 固定 0 | ✅ 0.12 |
| panelOpacity | 0-1 | ❌ 固定 1 | ✅ 0.965 |
| glassStyle | standard\|floatingtoolbar | ✅（ReadGlassStyle :49,74） | ❌ **强制 Standard**（:110），写了也不消费 |

**首版控件规则**：liquid_glass 检查器只显示上表"✅"项；cyber_punk 只显示 primaryCut/secondaryCut/outlineWidth/glow/ambientRatio/panelOpacity/shadow。校验允许但运行时不消费的交叉项（如 cyber_punk 的 cornerRadius）不得出现在对应材质的检查器，避免"能调但无效果"。

### 2.2 GPU 动效（rendering.motion）

| 参数 | 校验范围 | liquid_glass 默认 | cyber_punk 默认 | 消费 |
|---|---|---|---|---|
| motionEnabled | 必须布尔 | true | true | ✅ 两者 |
| targetFps | 1-240 | 30 | 30 | ✅ 两者 |
| stateTransitionMs | 0-5000 | 180 | 140 | ✅ 两者 |
| scannerPeriodMs | 0-60000 | 0 | 2800 | ✅ 两者 |

（ThemeRenderingConfig.cs:139-148；MaterialRuntimeSettings.cs:70-73, 106-109）

### 2.3 GPU 前景——floatingToolbar 消费的主题字段清单

事实来源：`MaterialRuntimeSettings.Create(configuration, material, theme)`（:148-157）仅在 `GlassStyle == FloatingToolbar` 时调 `ThemeForegroundStyleFactory.Create(theme, material)`；`ThemeForegroundStyleFactory.Create`（ThemeForegroundStyleFactory.cs:13-59）：

**字体（6 项）**：`Font.Family`→UiFontFamily/CaptionFontFamily；`Font.ValueFamily`→ValueFontFamily/DisplayFontFamily；`Font.Value`→DisplaySizeDip 与 BodySizeDip；`Font.Group`→TitleSizeDip；`Font.Item`→CaptionSizeDip；`Font.Bold`→Bold。

**颜色（12 项角色映射）**：TextPrimary→Primary；TextSecondary（空则回退 TextGroup）→Muted；ValueSafe→Success；ValueWarn→Warning；ValueCrit→Danger；BarLow→Accent 与 BarSafe；BarMid→BarWarn；BarHigh→BarCrit；Border→Rule；Shadow→Shadow；BarBackground→TrackColor；TextGroup→GroupTitleColor。

**限制事实**：standard 样式与 cyber_punk 使用 `ForegroundThemeStyle.Default(material)` 固定前景（MaterialRuntimeSettings.cs:87-88,114-116），**不消费主题 Font/Color** —— 检查器对这两种情况不得显示主题字体/颜色控件。

### 2.4 兼容外观独立配色（rendering.fallbackStyle）

事实来源：ThemeRenderingConfig.cs:263-302（类型化载体）、LegacyFallbackStyleResolver.cs:56-151（解析）。

- 六槽：`background`、`panelBackground`、`border`、`text`、`mutedText`、`accent`；均 `string?`，null 不写出（WriteStyleValue :432-437）。
- 默认值：`#1E2228 / #282D35 / #3C444F / #F2F4F7 / #B4BBC4 / #8FB4D9`（:56-61），正文对比度 ≥ 4.5 有测试锁定（ThemeFallbackStyleTests）。
- 非法值：**非致命**诊断 `gpu_fallback_style_invalid: rendering.fallbackStyle.<field>`（:52,184-185），字段退默认值，不影响整份主题可用性（ThemeRenderingConfig.cs:157-159）。
- 非字符串值/整块非对象：解析期忽略并记 `InvalidFields`（ThemeFallbackStyleJsonConverter :406-423），加载不失败。
- 块内未知键进 `ExtensionData` 原样保留（:377-379）。

### 2.5 回退派生主题（生产侧消费 fallbackStyle 的方式）

事实来源：`LegacyFallbackStyleBridge`（RenderingTransitionCoordinator.cs:825-921）。

- `BuildDerivedTheme` 写 6 个色槽：Background/GroupBackground/Border/TextPrimary/TextSecondary + **Accent→TextTitle**（裁决 D-H，:881-889）。检查器把 fallbackStyle.accent 标为"标题强调色"，不冒充进度条色。
- 布局/阈值/背景/渲染**按引用共享**源主题（:907-921）；5 个运行期字体字段共享同一 Font 实例（:868-874），派生主题不 BuildFonts/不 Scale/不释放——由 5 个 DerivedThemeRuntimeFontTests 锁定。
- fallbackStyle 的**字体、布局、圆角、阈值不存在独立字段**：原型稿的"两套字体/圆角"无生产依据，首版不新增 fallbackFont/fallbackLayout。

### 2.6 经典主题字段

经典渲染读取既有 Theme 属性（配色、字体、布局、阈值、背景），由 ThemeEditorAppearanceTests 的 27 个用例锁定预览副本语义（布局逐字段相等、字体只借用、已缩放源不二次缩放）。现有 `AppearanceFields.cs`（1147 行）是**硬编码行**的外观编辑面板，不是元数据驱动的字段目录——TE03 需要新建 `ThemePropertyCatalog`，可复用其 `AppearanceColorText`（解析/alpha 冻结语义）、`AppearanceThemeConfig`（CloneConfig/RestoreConfig/CloneRendering/CloneFallbackStyle）、键名常量与 `_resetters` 缺省恢复模式。

## 3. 视图表与预览路线

| 视图 | 经典主题实际路线 | GPU 主题实际路线 | 现编辑器覆盖 |
|---|---|---|---|
| 主竖向面板 | `UILayout` + `UIRenderer.Render(g, groups, theme, appearance)`（ThemePreviewControl.cs:299） | `GpuPresentationHostController` + `GpuSceneRenderer`（UIController.cs:62,385） | ✅ 两者均有预览（ThemePreviewControl / GpuThemePreviewControl） |
| 横向面板 | `HorizontalRenderer.Render`（UIController.cs:821，GPU 未激活时） | **GPU 场景路线**：`HorizontalLayout` + `UiSceneSnapshotAdapter.BuildHorizontal`（UIController.cs:1950-1967）交 GPU 宿主渲染；UIController.cs:761 注释确认 legacy 与 GPU 两路共用同一布局构造入口 `CreateHorizontalLayout` | ❌ 无预览 |
| 任务栏 | `TaskbarRenderer`（UIController.cs:245） | 同左（常规绘制；`BuildColumnsCore(forTaskbar: true)` 仅产 GDI 列，:2011；场景适配器无任务栏构建器） | ❌ 无预览 |
| 回退显示 | 源主题本身（UIController.cs:1581-1595 清空派生覆盖） | `LegacyFallbackStyleBridge.BuildDerivedTheme` | ❌ 无同源预览 |

经典预览模拟输入为固定 Mock（CPU 23%/65°、GPU 90%/65°/41%、MEM 68%、DISK、NET；ThemePreviewControl.cs:223-258），背景为 96×96 固定样例图，绝不抓屏（"预览相关源码不含任何抓屏调用"测试锁定）。两个预览控件**均无命中测试**——点选定位是全新能力，应复用 UILayout 布局结果或场景节点标识，不建第二套坐标。

**TE04 需要新增的预览形态**：横向、任务栏预览（复用 HorizontalRenderer/TaskbarRenderer）；回退同源预览（复用 LegacyFallbackStyleResolver + BuildDerivedTheme 提取的纯函数）；GPU 会话复用（当前 `UpdateGpuPreview` 每次 SetTheme 都 Dispose+new，ThemePreviewControl.cs:133-163，是计划 §6.3 要求替换的高频路径）。

## 4. 文档、保存与应用现状

| 项 | 现状 | 事实 | 重做处理 |
|---|---|---|---|
| 撤销 | 最多 32 个配置快照，无重做；整份 JSON 字符串签名对比 | ThemeEditHistory.cs:12-30 | 补重做、事务合并、100 上限 + 8 MiB |
| 原子写入 | 临时文件 `path + ".litemonitor-tmp"`（SaveThemeAs 用 guid 名）→ `File.Replace`（不存在则 `File.Move`；NotSupported/IOException 兜底 Move overwrite）→ 失败删临时文件 | ThemeFileService.cs:331-353, 377-416 | 保留共用核心，新增载荷入口与冲突检测 |
| 故障注入 | `TempFileWriter`/`TargetReplacer` internal 委托 + `ResetTestHooks` | :52-67 | 保留 |
| 外部修改冲突 | **无** mtime/哈希检测 | 全文仅 CleanupTempFiles 用过 GetLastWriteTime（:302） | 新增源摘要 |
| 另存为 | 已有，`File.Move(overwrite: false)` 不覆盖 + 保留名校验 | :434-460 | 复用 |
| 默认值物化 | SaveTheme 全量序列化 Theme 对象（:160）；无 options 级 DefaultIgnoreCondition，靠属性级 WhenWritingDefault/WhenWritingNull（ThemeManager.cs:509-517） | 已知历史问题（null 属性曾污染 10 个主题文件，修法是属性级注解） | 草稿必须保留"源字段存在性"，用显式补丁写回，不能全量序列化 |
| 保存→应用 | 保存成功后弹"是否立即应用"；应用走**反射**取 `MainForm._ui`（UIController）与 `MainForm._cfg`（Settings），再 `settings.Save()` + `ApplyTheme` | ThemeEditorForm.cs:749-753, 787-816 | 拆开保存与应用；注入 ThemeApplyGateway，消灭反射 |
| 同名重载 | `UIController.ApplyTheme` → `ThemeManager.Load` → `Scale` → `OnThemeChanged` → `TaskbarRenderer.ReloadStyle` → `new UILayout` → `RefreshLegacyFallbackTheme` → 释放旧字体 | UIController.cs:194-279 | 应用结果编排复用此链路 |

## 5. GPU 启动保护归属（TE00 关键风险）

事实来源：GpuStartupGuard.cs。

- `Shared` 为进程级单例（:86），状态目录 `%LocalAppData%\LiteMonitor++\gpu-startup`，按安装目录 SHA256 命名标记文件（:33-40）。
- pending 标记在**首个** `TryBeginAttempt` 写入（:296-307），最后一个尝试终结且集合清空时删除（CompleteAttemptCore :244-274）。
- 启动发现 pending → `_conservative=true`（:52-62），此后**拒绝一切新尝试**（:138-146）。
- `AbandonAttemptPreservingPending`（:287-294）只注销尝试、**故意保留 pending**，令下次进程启动进保守模式（合同 GPU-005/裁决 D6）。
- 生产宿主 `GpuPresentationHostController`（GpuPresentationHostController.cs:97）与编辑器预览 `GpuThemePreviewControl`（:107-114）**共用同一个 Shared**。

**结论（证实计划 §6.3 担忧）**：预览复用 Shared 会双向污染——预览异常退出可把主窗体下次启动打入保守模式；主窗体一旦保守，预览 GPU 也被连坐拒绝。TE04 前置条件：为预览建立独立的 guard 归属（隔离验证后再开放），预览不得写 pending、不得消费/清除生产崩溃标记。当前 `GpuThemePreviewControl` 的 `_startupAttempt?.MarkFirstSuccessfulFrame()`（:216）已在生产共享状态上打标记，重做时必须替换。

## 6. 旧回归测试基线

七套指定套件全部位于 `tests/LiteMonitor.Tests/`：ThemeEditorAppearanceTests（27 用例）、GpuThemeEditorPreflightRegressionTests（4）、ThemeSaveTests（15）、ThemeFallbackStyleTests（12）、LegacyFallbackThemeRoutingTests（4+辅助）、DerivedThemeRuntimeFontTests（5+辅助）、RenderingRouteStatusBuilderTests（10）。

<!-- TEST_BASELINE_START -->
全量 `dotnet test tests/LiteMonitor.Tests`（2026-10-26，Debug，net8.0）：**失败 0，通过 2472，跳过 7，总计 2479，耗时 4 m 23 s**。基线全绿；重做过程中该结果不得回退。
<!-- TEST_BASELINE_END -->

已知环境注意：残留 testhost.exe 会锁 `tests/LiteMonitor.Tests/bin` 下 DLL 导致 MSB3027 构建失败；先确认无 testhost 进程再跑基线（本轮已遇到一次）。

## 7. 与计划表格的差异修正

重做计划按代码事实修正三处：

1. "GPU 回退强调色映射至 Color.TextTitle"——确认无误，且派生主题的布局/阈值/背景/字体同样按引用共享，"共享设置"范围比计划表格写的更大：**兼容外观除六槽配色外没有任何独立样式字段**。
2. "GPU 标准样式前景可能使用固定前景"——确认为**事实**：standard 与 cyber_punk 均固定前景（MaterialRuntimeSettings.cs:87-88,114-116），且 cyber_punk 的 glassStyle 参数被强制忽略（:110）。计划 §4.2 表格的"可能"应去掉。
3. 横向面板在 GPU 主题下**有独立 GPU 路线**（液态玻璃横向生效，`UiSceneSnapshotAdapter.BuildHorizontal`，UIController.cs:1950-1967），与任务栏"始终常规绘制"不同；画布工具栏的"预览形态"标签须按此区分：竖向/横向可为"GPU 场景"，任务栏恒为"常规绘制"。横向预览（TE04）应复用 `BuildHorizontal` + `GpuSceneRenderer` 与生产同源，不需要新渲染器。

## 8. 风险原型与待办

- 原生标题栏 + 三栏最小布局原型：`tools/ThemeStudioLayoutProbe/`（TE00 交付物 2），截图 `layout-default.png` / `layout-compact.png`（本目录 probe-screenshots/）。
  - 本机为 4K 主屏、系统 175%（DeviceDpi=168）。两档均已验证：默认 1120×760 DIP → 1960×1330 物理像素；紧凑 720×480 DIP（资料库折叠、检查器收窄）→ 1260×840 物理像素。
  - **新事实（缩放）**：.NET 8 WinForms + PerMonitorV2 下，`AutoScaleMode.Dpi` + `AutoScaleDimensions=(96,96)` **不会**重缩放代码里显式设置的 `ClientSize`（实测窗口停在 1120×760 物理像素）。TE02 的窗口缩放必须采用探针验证过的模式：句柄创建后取 `DeviceDpi`，经唯一 `Dip()` 入口换算全部几何（`AutoScaleMode.None`）。这正面排除计划 §7 第 2 条的"依赖 AutoScale 隐式缩放"路径。
  - 截图为运行期 `DrawToBitmap` 窗口自摄，不使用屏幕拷贝（多显示器混合 DPI 下 CopyFromScreen 坐标会错位，且会摄入窗口外桌面内容）。
  - 真实 100%/200% 档位矩阵需人工改显示缩放后重跑探针，列为实机验收项（TE05），不在自动化范围。
- GPU 同源证明（交付物 3）：`tools/GpuEditorPreviewProbe` 已具备固定输入 GPU 预览与 `TryCaptureOnce` 回读能力，主效果/回退同源证明延至 TE04 一并交付（预览 guard 隔离先行）。
- 克隆/未知字段/原子保存/旧回归基线（交付物 4）：由第 2、4、6 节事实 + 第 6 节测试运行记录构成。

## 9. TE01/TE02/TE03 开工结论

- 事实基础足够，无需再扩展估算；TE00 未发现"缺少可复用布局/命中边界"以外的硬阻塞——命中测试是零起点新能力，TE04 按计划走"复用布局结果"路线。
- TE01 文档模型与 TE02 视觉壳按文件边界可并行；属性描述符 `ThemePropertyCatalog` 以本文第 2 节矩阵为冻结输入。
- 预览 guard 隔离（第 5 节结论）是 TE04 的硬前置，先行排入 TE04 任务卡。
