# LiteMonitor++ 外观改造实施计划

> 目标：为 LiteMonitor 增加 **机能风（Cyberpunk）** 与 **液态玻璃（Liquid Glass）** 两套视觉能力，
> 同时保证主题系统向后兼容、对外观改动可插拔、与仓库内其他并行开发低冲突。
>
> 本文档**不引用具体行号**。所有定位点均以 `文件路径 → 类型名 → 方法名` 表示，
> 便于在其他分支/并行改动存在时仍然有效。
>
> **v4 —— 2026-10-20 基于 HEAD `97f793d` 再次审查。执行入口：[18 个工作包、Agent 分工与验收](superpowers/plans/2026-10-20-appearance-agent-execution.md)。**
> 本文保留设计背景及 v2/v3 历史记录；默认值、技术门槛、写集和验收以 v4 执行计划为准。当前只更新计划，未实现或实测新效果。历史工期、行数、性能数字不能视为当前结论。

---

## 0. 设计前提

### 0.1 当前架构关键事实（改动的依据）

| 事实 | 位置 | 对改造的含义 |
|---|---|---|
| 竖屏渲染入口 | `src/UI/UIRenderer.cs` → `UIRenderer.Render` | 背景/装饰的主战场 |
| 竖屏背景溢出策略 | `UIRenderer.Render` | 从 `(-5,-5)` 起填充（**溢出 5px，修物理像素死角**）。横屏**没有**这个策略，是两处独立实现 |
| 横屏渲染入口 | `src/UI/HorizontalRenderer.cs` → `HorizontalRenderer.Render` | 需与竖屏保持视觉一致 |
| 任务栏渲染入口 | `src/UI/TaskbarRenderer.cs` → `TaskbarRenderer.Render` | **只画文字、零背景绘制，且完全不消费 `Theme`**（颜色是硬编码常量 + `Settings.GetStyle()`）→ 任务栏连现有主题都不跟随 |
| 主窗体绘制 | `src/UI/MainForm_Transparent.cs` → `MainForm.OnPaint` / `OnPaintBackground` | `OnPaintBackground` 为空实现（不画系统背景） |
| 主题模型 | `src/Core/ThemeManager.cs` → `Theme` / `LayoutConfig` / `FontConfig` / `ThresholdConfig` / `ColorConfig` | 外观能力的唯一配置源 |
| 颜色解析 | `src/Core/ThemeManager.cs` → `ThemeManager.ParseColor` | 只支持 `#RRGGBB` / `#AARRGGBB` / 颜色名。**3 位 `#RGB` 不支持**（会退化为全透明）。**不支持 `rgba()`** |
| `rgba()` 的实际行为 | `ThemeManager.ParseColor` → `Color.FromName` | **不抛异常**，返回 ARGB=0 的全透明色 → **静默失效**，不是崩溃。排查时容易误判为"没设置" |
| 绘制工具 | `src/Core/UIUtils.cs` → `RoundRect` / `FillRoundRect` / `DrawBar` / `GetBrush` / `GetPen` | 圆角路径、进度条的改造点 |
| 画刷缓存 | `UIUtils.GetBrush` / `GetPen` | `GetBrush` 返回类型固定为 `SolidBrush`、key 是颜色字符串；`GetPen` 返回**共享实例**、key 是 `色值_宽度`。**两者都无法承载 `TextureBrush` / `LinearGradientBrush`** |
| 圆角/穿透/透明度 | `src/UI/Helpers/MainFormWinHelper.cs` → `ApplyRoundedCorners` / `SetClickThrough` / `StartFadeIn` | Win11 走 DWM 系统圆角，Win10 走 WinForms `Form.Region` 属性（**不是 `SetWindowRgn` P/Invoke**） |
| 主题文件读写 | `src/ThemeEditor/ThemeFileService.cs` → `LoadTheme` / `SaveTheme` | 序列化整个 `Theme` 对象，**未映射字段会被丢弃**；且 `SaveTheme` 的 options 无 `DefaultIgnoreCondition`，**`null` 会被写成 `"Prop": null`** |
| 主题编辑器 UI | `src/ThemeEditor/ThemeEditorForm.cs` | **纯代码构建 UI**（无 `.Designer.cs`），拆新文件到同目录是合适的 |
| 主题预览 | `src/ThemeEditor/ThemePreviewControl.cs` → `OnPaint` | 内部调用 `UIRenderer.Render` → 装饰层挂进 `UIRenderer` 后**预览自动生效** |
| 主题预览的主题拷贝 | `ThemePreviewControl` → `CreatePreviewTheme` / `CreateScaledTheme` | 两处**各自手写** `new LayoutConfig { ... }` 字段拷贝 → **新增的 `LayoutConfig` 字段会被静默丢成默认值** |
| 网页版面板 | `src/System/WebServer/WebPageContent.cs` | 599 行内联 HTML/CSS，自带 `--bg` / `--c-0..2` / `--card` / `--border` 等变量，**对 `ThemeManager` 引用数为 0 → 与主题完全零耦合** |
| **多显卡按卡拆组** | `src/Core/GpuGroupPolicy.cs`、`src/Core/GpuKey.cs`、`src/Core/GpuRegistry.cs` | `Settings.MonitoredGpuCount` 可 >1；每张卡**各自成为一个分组**（`GPU.0` / `GPU.1`）→ **组块数量随显卡数线性增长**，直接影响装饰层的绘制调用量与玻璃背景的抓屏面积 |
| 组标题统一入口 | `src/Core/GroupTitleResolver.cs` | 面板 / 设置页 / 右键菜单 / 网页端 4 处的组标题已收口到 `GroupTitleResolver.Resolve(cfg, groupKey)`。装饰层若需感知组标题，**应复用它**，不要自己再算一套 |
| 组标题动态刷新 | `src/UI/UIController.cs` → `RefreshPerCardGroupTitles` | 按卡分组后标题会随硬件变化而变，刷新走这条路径 |

### 0.2 已存在但被忽略的主题字段（当前丢失）

| 丢失位置 | 现象 |
|---|---|
| `color.border` / `color.shadow` / `color.textSecondary` | 10 个内置主题中有 **7 个**声明了（`AuroraDream_Purple`、`CyberBlue_Neon`、`DeepOcean_Blue`、`Dracula_Vampire`、`LightPure_Elegant`、`MatrixGreen_Terminal`、`Monokai_Pro`），但 `ColorConfig` **没有**对应属性 → 反序列化时丢弃 |
| `font.scale` | **全部 10 个**主题都声明了，但 `FontConfig` **没有** `Scale` 属性 → 同样被丢弃 |
| `color.shadow` 的值格式 | 7 个主题用的是 `rgba(0,255,255,0.2)` 这种写法，而 `ParseColor` **不识别 `rgba()`** → 即使属性补上了，值也会退化为全透明 |

**结论：机能风所需的 schema 已由作者预留，本次工作属于"补完半成品"，不是从零设计。**

### 0.3 外观消费点全景（明确范围，避免验收时被当成遗漏）

| 消费点 | 是否跟随主题 | 本次是否改造 | 说明 |
|---|---|---|---|
| 主窗口竖屏 `UIRenderer` | ✅ | ✅ 阶段二/三 | 主战场 |
| 主窗口横屏 `HorizontalRenderer` | ✅ | ✅ 阶段二/三 | 与竖屏共用装饰逻辑 |
| 任务栏 `TaskbarRenderer` | ❌ **完全不跟随** | ❌ 排除 | 零背景绘制 + 硬编码颜色；且窗体被 `SetParent` 挂到 `Shell_TrayWnd` 并设为 `WS_CHILD`，**子窗口不可能有 DWM 材质** |
| 任务栏 tooltip `LiteTooltipForm` / `TaskbarTooltipHelper` | ✅ | ❌ 自动受益 | 跟随主题变色，但装饰层不覆盖（它不是主面板） |
| `SpeedTestForm` / `AboutForm` / `CleanMemoryForm` | ✅ | ❌ 自动受益 | 已消费 `ThemeManager` / `ParseColor` |
| `MenuManager` / `MainPanelPage` | ✅（各 1 处引用） | ❌ 自动受益 | 引用量极小 |
| 网页版 `WebPageContent` | ❌ **零耦合** | ⚠️ 阶段四决策点 | 见 §4.3 |
| `ThemePreviewControl` | ✅ | ✅ 阶段四 | 需补齐两处 `LayoutConfig` 拷贝（见 §4.2） |
| `HardwareTrendForm` / `HardwareInfoForm` | ❌ 硬编码色 | ❌ **明确不改** | 独立对话框/详情页，维持现状浅色风格 |
| `LiteUI.UIColors` / `LiteTreeView` / `TrendChartControl` | ❌ 硬编码色 | ❌ **明确不改** | 设置页/图表控件，独立于主面板视觉 |
| 托盘图标 `Properties.Resources.AppIcon` | ❌ 静态资源 | ❌ 明确不改 | 主题不涉及系统资源 |

### 0.4 与并行开发的协作守则

本计划假定仓库内存在其他并行开发（插件市场、设置项、菜单等）。遵守以下规则：

1. **新增优先，修改最少。** 绝大部分新能力放在**新文件**里，现有文件只做"挂载点"级别的改动。
2. **每个共享文件只有一个写入者。** 挂载点用方法名描述；完整背景、底纹、组框、外框和生命周期可能需要多处接线，不能为追求“一处修改”遗漏行为。两个 Renderer 和 UIController 由主集成者统一修改。
3. **不改现有公开方法的签名。** 需要扩展时用重载或可选参数，保留旧调用路径。
4. **新能力缺省关闭。** 可选颜色/对象可空，新增整数缺键与 0 等价并忽略默认值写出。旧主题已有 border/shadow 也不自动开启效果；主面板旧画面保持一致。
5. **主题未知字段保真。** 通过 `JsonExtensionData` 保留未映射字段，
   使**其他人正在加的主题字段不会因本功能上线而丢失**。
   ⚠️ **注意层级**：`[JsonExtensionData]` 只捕获**同一层级**的未知键。
   顶层未知键挂 `Theme`；嵌套未知键挂到 `ColorConfig` / `FontConfig` / `LayoutConfig` / `ThresholdConfig` / `ThresholdSet`，新增 `BackdropConfig` 同样处理。
   详见 §1.4。
6. **命名空间隔离。** 新代码放在 `LiteMonitor.src.UI.Decor` / `LiteMonitor.src.SystemServices.Backdrop` / `LiteMonitor.ThemeEditor`。
7. **每阶段独立可发布、可回滚。** 任一阶段不合预期，可单独回退。

> **命名空间约定（仓库本身不统一，以下为实测值 + 例外清单）**
> - `src/UI/*.cs` → `LiteMonitor`；**例外**：`CleanMemoryForm` / `HardwareInfoForm` / `SettingsForm` 是 `LiteMonitor.src.UI`
> - `src/UI/Controls/*.cs` → `LiteMonitor.src.UI.Controls`；**例外**：`TrendChartControl` 是 `LiteMonitor`
> - `src/UI/Helpers/*.cs` → `LiteMonitor.src.UI.Helpers`（10/10 一致）
> - `src/UI/Settings/*.cs` → `LiteMonitor.src.UI.SettingsPage`（**注意不是 `src.UI.Settings`**）
> - `src/Core/*.cs` → `LiteMonitor.src.Core`；**例外**：`MetricItem` / `NetworkSpeedTester` / `Settings` / `SettingsHelper` 是 `LiteMonitor`
> - `src/System/HardwareServices/*.cs`、`src/System/*.cs` → `LiteMonitor.src.SystemServices`
> - `src/System/WebServer/*.cs` → `LiteMonitor.src.WebServer`（**不是** `src.SystemServices.WebServer`）
> - `src/System/InfoService/*.cs` → `LiteMonitor.src.SystemServices.InfoService`
> - `src/Plugins/*.cs` → `LiteMonitor.src.Plugins`
> - `src/ThemeEditor/*.cs` → `LiteMonitor.ThemeEditor`（**不是** `src.ThemeEditor`）

---

## 1. 阶段一：主题系统补齐（地基）

**目标**：让主题成为外观能力的完整描述载体，并修复字段丢失。
**风险**：低。**工作量**：约 2–3 天。**可独立发布。**

### 1.1 扩展 `ColorConfig`

文件：`src/Core/ThemeManager.cs` → `ColorConfig`

新增三个可空属性（`string?`，默认 `null` 表示"未声明"）：

| 属性 | 用途 | 建议值 |
|---|---|---|
| `Border` | 面板/组块描边色 | `#AARRGGBB` 或 `rgba()` |
| `Shadow` | 阴影/辉光色 | 半透明色 |
| `TextSecondary` | 次级文字色 | `#RRGGBB` |

保留现有 12 个颜色属性不动。

### 1.2 扩展 `LayoutConfig`

文件：`src/Core/ThemeManager.cs` → `LayoutConfig`

| 属性 | 类型 | 默认 | 用途 | 是否参与缩放 |
|---|---|---|---|---|
| `FrameStyle` | `int` | `0` | 0=圆角(现状) 1=直角切角 2=圆角+描边 3=全息边框 | ❌ |
| `FrameWidth` | `int` | `0` | 描边宽度(px) | ✅ |
| `FrameLength` | `int` | `0` | 切角边长(px) | ✅ |
| `Scanline` | `int` | `0` | 扫描线不透明度 0–100，0=关闭 | ❌ |
| `Glow` | `int` | `0` | 辉光强度 0–100，0=关闭 | ❌ |

> `FrameWidth` / `FrameLength` 保存为 96 DPI 逻辑尺寸，由 v4 的外观 options 转换为物理像素；不再同时加入 `LayoutConfig.Scale`，避免重复缩放。
> `FrameStyle` / `Scanline` / `Glow` 属于枚举/比例，**不缩放**。

v4 补充已有视觉目标缺少的 `Grid`（0–100，默认0）与 `BarStyle`（0=旧条、1=渐变、2=纹理）。所有新增整数采用属性级 `WhenWritingDefault`；颜色/对象采用 `WhenWritingNull`，不改变旧属性的序列化规则。

### 1.3 `ParseColor` 支持 `rgba()`

文件：`src/Core/ThemeManager.cs` → `ThemeManager.ParseColor`

- 现有分支保持不变（`#` 开头 / 颜色名）
- 新增 `rgba(r,g,b,a)` 解析，a 统一为 `0–1`，`1` 表示不透明；字节 alpha 使用 `#AARRGGBB`。固定 InvariantCulture、范围检查及舍入规则，详见 v4 执行计划
- 解析失败仍回退全透明（保持现有容错行为）
- 该方法带颜色缓存，扩展时缓存 key 用原始字符串，无需改动

> 备选：把主题里的 `rgba(...)` 全部改写为 `#AARRGGBB`（解析器已支持）。
> 但 7 个主题文件已采用 `rgba()` 写法，**扩展解析器对用户更友好**，两者不冲突。

### 1.4 主题未知字段保真（并行开发关键项，⚠️ 挂错层级就不会生效）

文件：`src/Core/ThemeManager.cs`

**`[JsonExtensionData]` 只捕获同一层级的未知键。** 因此需要**多层级**挂载：

| 挂载位置 | 保护什么 |
|---|---|
| `Theme` | 尚未类型化的未来顶层字段；Backdrop类型化后由其已知属性承接 |
| `ColorConfig` | 未知未来颜色键；border/shadow/textSecondary由新增已知属性承接 |
| `FontConfig` | **全部 10 个主题的 `font.scale`** |
| `LayoutConfig` | 防御性，为并行开发预留的布局字段 |
| `ThresholdConfig` / `ThresholdSet` | thresholds 本层与各阈值子对象的未来键 |
| `BackdropConfig` | 引入类型化背景配置后的本层未来键 |

写法（以 `ColorConfig` 为例）：

```csharp
[JsonExtensionData] public Dictionary<string, JsonElement>? ExtraFields { get; set; }
```

**实测结论（必须知道的三点）：**
1. `JsonElement` **无生命周期风险** —— 强制 GC 后仍可读取，STJ 内部会 clone，无需额外保活 `JsonDocument`。
2. 扩展槽可正常写回 —— `SaveTheme` 序列化时会自动还原。
3. ⚠️ **键大小写会混杂** —— `SaveTheme` 的 options 只有 `WriteIndented = true`、**无 naming policy**，
   已知属性写成 PascalCase（`"Background"`），扩展数据保留原 camelCase（`"border"`），
   同一文件里两种风格并存。**可接受，但要知道**。

> `ThemeManager.cs` 已 `using System.Text.Json.Serialization;`，无需新增 using。

### 1.5 顺带修掉两个序列化副作用

文件：`src/ThemeEditor/ThemeFileService.cs` → `SaveTheme`；`src/Core/ThemeManager.cs` → `FontConfig`

| 问题 | 修法（选属性级，零侵入） |
|---|---|
| `SaveTheme` 无 `DefaultIgnoreCondition`，`null` 属性会被写成 `"Border": null`，污染 10 个主题文件 | 给新增的可空属性加 `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`（**不动 `SaveTheme` 的 options**，避免影响既有行为） |
| `FontConfig.ItemSize`（`{ get; internal set; }`）每次保存都被写进主题 JSON（实测输出含 `"ItemSize": 0`） | 加 `[JsonIgnore]` |

> 另一条可选修法是给 `SaveTheme` 的 options 加 `DefaultIgnoreCondition = WhenWritingNull`，
> 但那会改变**所有**现有属性的写出行为，与并行开发冲突面更大 → **不采用**。

### 1.6 验证方式

- 加载 `CyberBlue_Neon`，断言能读到 `Color.Border`、`Color.Shadow`、`Color.TextSecondary`
- 加载任意主题，通过扩展数据断言 `font.scale` 保留；加入值为1.5的样本。本次不新增 `Font.Scale` 属性或改变字体缩放语义
- 用 `ThemeEditorForm` 打开并保存主题副本，再次加载，断言**三个字段 + `font.scale` 均未丢失**，新增已知可空属性不凭空写出 null、不出现 `"ItemSize"`；原有未知 null 仍保留
- 渲染结果与改造前**逐像素一致**（新字段全部缺省关闭）
- **主题 JSON 文件内容在"只读打开不保存"路径下零变化**
- `ThemeFileService.SafeWrite` 改为同目录临时写入+安全替换，失败时原目标仍完整；禁止先删除旧文件再移动临时文件。按 v4 A02 独立验收

---

## 2. 阶段二：机能风（装饰层）

**目标**：实现切角边框、霓虹描边、角标、扫描线、网格、辉光、纹理进度条。
**风险**：中。**工作量**：基础版 2 天，完整版 4–5 天。**依赖阶段一。**

### 2.1 新增绘制工具

工具优先放入新增的 `src/UI/Decor/` 文件；`src/Core/UIUtils.cs` 只保留必要的兼容接线，由主集成者独占修改，不要求把全部缓存和算法塞入共享工具类。

| 方法 | 作用 |
|---|---|
| `CutCornerPath(Rectangle, int radius, int cut)` | 圆角+切角混合路径（`cut=0` 退化为纯圆角） |
| `FramePath(Rectangle, int radius, int cut)` | 按 `FrameStyle` 语义生成边框路径 |
| `GlowPath(Graphics, GraphicsPath, Color, int layers)` | 多层递减 alpha 描边，模拟辉光 |
| `GetTextureBrush(string key, ...)` | **新增独立缓存**，`TextureBrush` 无法用 `GetBrush` |
| `GetGradientBrush(string key, ...)` | **新增独立缓存**，`LinearGradientBrush` 无法用 `GetBrush` |

**⚠️ 三个必须遵守的约束：**

1. **`GlowPath` 不得 Dispose `UIUtils.GetPen` 的返回值。**
   `GetPen` 返回 `_penCache` 中的**共享实例**，`UIRenderer.DrawTextItem`（分割线）等调用方仍在使用。
   Dispose 掉会污染缓存 → 后续 `g.DrawLine(pen, ...)` 拿到已释放对象 → **GDI+ 通用错误**。
   → `GlowPath` 自行 `using var pen = new Pen(...)`，逐层创建逐层释放。
   计划中所有"缓存对象一律不 Dispose"。

2. **`TextureBrush` / `LinearGradientBrush` 需要新缓存。**
   `UIUtils.GetBrush` 返回类型固定为 `SolidBrush`、key 是颜色字符串，对渐变/纹理毫无意义。
   新缓存需：独立上限、随 `UIUtils.ClearBrushCache()` 一起清理、
   **纹理位图与渐变参数（起点/终点色）必须参与 key**，否则主题切换后会串色。

3. **现有缓存上限偏小。**
   `GetPen` 的 `MAX_BRUSH_CACHE = 32`，淘汰策略是**清空全部**。
   辉光 N 层 × M 色会很快撞上限触发"全清" → 缓存抖动。
   → 辉光的 `Pen` 走自建路径（见约束 1），不占用 `GetPen` 缓存。

### 2.2 新增装饰渲染器

文件：**新建** `src/UI/Decor/DecorRenderer.cs`
命名空间：`LiteMonitor.src.UI.Decor`

对外接口（全部静态方法，只依赖 `Graphics` + `Theme`）：

| 方法 | 作用 |
|---|---|
| `DrawWindowFrame(g, bounds, theme)` | 窗体外框：切角/描边/辉光/角标 |
| `DrawGroupFrame(g, bounds, theme)` | 分组块描边 + 辉光 |
| `DrawOverlay(g, bounds, theme)` | 全局覆盖层：扫描线 / 网格底纹 |

设计约束：
- 全部由主题字段驱动，字段缺省时**直接 return**，零视觉变化
- 不触碰 `MetricItem` / 布局计算，纯绘制层
- 不自行创建 `Font`（复用 `Theme` 已构建的字体对象）
- 画刷/画笔：`SolidBrush`/`Pen` 可用 `UIUtils.GetBrush`/`GetPen`（**只读不释放**）；
  渐变/纹理走 §2.1 的新缓存
- **⚠️ 组块数量不固定**：`Settings.MonitoredGpuCount` > 1 时每张显卡各成一组，
  组块数从 1–2 个涨到 N 个。`DrawGroupFrame` 的辉光是"多层描边 × N 层"，
  调用量按组块数线性放大 → **必须实测 4 卡场景的帧耗时**，
  对辉光层数和缓存重算设上限；每次 Paint 必须绘制有效装饰/缓存，不能跳过绘制而让背景把组框擦掉
- 若装饰需要感知组标题，**用 `GroupTitleResolver.Resolve(cfg, groupKey)`**，不要自己算

### 2.3 挂载点与绘制层次

| 文件 | 挂载位置 | 说明 |
|---|---|---|
| `src/UI/UIRenderer.cs` → `Render` | 背景之后、标题/指标之前 | 调窗口底纹；外框/高光最后向内描边 |
| `src/UI/UIRenderer.cs` → `DrawGroupBackground` | 组背景之后、组标题之前 | 调组底纹及 `DrawGroupFrame` |
| `src/UI/HorizontalRenderer.cs` → `Render` | 背景之后、列文字之前 | 调窗口底纹；列绘制完成后画外框，不改横屏分组结构 |

> 横竖屏共用装饰资源与窗口风格。调用方提供完整 ClientRectangle，不能用 VisibleClipBounds 或背景溢出矩形定位外框；保存/恢复 Graphics 状态，按 v4 验证局部重绘。

### 2.4 圆角改为主题驱动（⚠️ Win11 需要额外一步）

文件：`src/UI/Helpers/MainFormWinHelper.cs` → `ApplyRoundedCorners`

**现状问题：**
- Win11 分支用 DWM 系统圆角（`DWMWCP_ROUND`）→ **切角风格的尖角会被 DWM 裁掉**
- Win10 分支用的是 WinForms 的 `Form.Region` 属性（**不是 `SetWindowRgn` P/Invoke**）
  —— `Region` 接受任意 `GraphicsPath`，**完全能满足切角需求**，Win10/Win11 通用
- 该方法只绑定在 `Resize` 事件上，**主题切换时不重算**（`UIController.ApplyTheme` 全文无调用）
- `Resize` 事件被绑了**两次**（`MainFormWinHelper.InitializeStyle` + `MainForm.BindEvents`）→ 每次 resize 跑两遍

**改造要点：**

1. **统一为"先按 `FrameStyle` 决定 `Form.Region`，再按是否 Win11 决定 DWM 偏好"**，避免两套代码漂移：
   - 切角/直角样式：`Form.Region = new Region(cutCornerPath)` **且** 显式设 `DWMWCP_DONOTROUND`(=1) 禁用 DWM 系统圆角；0 是 DEFAULT，不是禁用
   - 圆角样式：维持现状（Win11 `DWMWCP_ROUND` + `Region = null`；Win10 仅 `Region`）
2. 新增常量 `DWMWCP_DONOTROUND = 1`，检查 DWM 返回值和实际轮廓
3. MainForm 在 helper 已创建、最终布局确定后统一响应主题/尺寸/DPI/句柄变化。UIController 构造期已经 ApplyTheme，不能直接调用尚未创建的 `_winHelper`
4. 顺手解绑重复的 `Resize` 绑定（保留一处）

### 2.5 进度条纹理化

文件：`src/Core/UIUtils.cs` → `DrawBar`

- 现状：底槽圆角实心 + 前景圆角实心，**无渐变、无纹理、无高光**
- 改造：底槽可叠加斜线纹理（`TextureBrush` + 斜线画刷，纹理位图**需缓存**避免每帧重建）；
  前景改 `LinearGradientBrush`；末端可加亮头。缓存按完整条槽尺寸建立，裁剪到当前进度，不按每帧变化的填充宽度建缓存
- 保持现有三色阈值取色逻辑（`UIUtils.GetStateColor`）不变
- 主题未开启纹理时走原有路径（性能无变化）
- `BarLow` 当前未被该安全态路径使用；本轮保留既有阈值取色，不顺手改变旧主题安全值颜色。新条样式以显式 BarStyle 开启。

### 2.6 任务栏模式的边界

`TaskbarRenderer` **零背景绘制**，且**完全不消费 `Theme`**（颜色硬编码），
背景靠 `TaskbarWinHelper.ApplyLayeredStyle` 的 `LWA_COLORKEY` 镂空，
窗体还被 `TaskbarStrategyWin11.Attach` 用 `SetParent` 挂到 `Shell_TrayWnd` 并设为 `WS_CHILD`
（本计划不为该任务栏子窗口接入系统材质）。

→ **阶段二的装饰效果仅作用于主窗口（竖屏/横屏）。**
→ 任务栏 tooltip 会跟随主题变色（自动受益），但不在装饰层覆盖范围内 —— 验收时需注意区分。
→ 任务栏模式的外观升级（放弃色键、改为自绘底块）列为**阶段四的可选子项**，不在本计划主线。

---

## 3. 阶段三：液态玻璃（背景层）

**目标**：窗口背后呈现模糊 + 折射 + 噪点质感的半透明玻璃。
**风险**：高。**工作量**：**1–2 周**（见 §7.3，原估 3–5 天偏乐观）。**依赖阶段一。**

### 3.1 技术路线：先验证，再选择后端

v4 不预先承诺抓屏方案可用。A06 对照真实 MainForm 样式、GDI 绘制、Opacity、穿透、Region 与实际桌面背景验证；M0/M1 不等待该验证结果。

| 路线 | v4 结论 |
|---|---|
| 文档化 Win32 系统材质 | Win11 22621+ 可通过 `DwmSetWindowAttribute` / `DWMWA_SYSTEMBACKDROP_TYPE` 验证，并非必须引入 Windows App SDK。成功返回值不等于当前客户区已显示材质；Mica 也不能充当实时背后模糊的证据 |
| 排除自身的背景捕获 | 只有能证明得到真实背景、无自反馈且通过性能预算的后端才放行；不能按 API 名称预先宣称 Win10/11 行为相同 |
| 自绘渐变+tint+noise | 作为可独立交付的静态质感与 glass 失败降级，不冒充实时背景采样 |
| 老 Aero blur / 未文档化接口 | 不作为本轮通用后端；Windows 8 起老 blur API 的行为已变化，未文档化路线不纳入实施范围 |

具体文档与支持门槛见 [v4 §2/§4](superpowers/plans/2026-10-20-appearance-agent-execution.md)。去掉原稿“必须引入 App SDK”“未文档化调用必然触发杀软”“自绘必然跨版本一致”等未经验证的推断。

### 3.2 新增背景服务

新增 `src/System/Backdrop/BackdropService.cs`，仅 glass 且后端放行时运行。

- 单个后台任务处理自己的位图，最多一个在途请求、一个待发布结果；使用标量/Color/窗口代次快照，不持有 Theme/Font/Control。
- UI 线程接管并替换 active 帧，在不再绘制旧帧时释放。后台只能释放尚未被 UI 接管的帧，不能因一次 Interlocked.Exchange 就释放可能正在 DrawImage 的旧对象。
- 主题/尺寸/句柄代次变化、隐藏/最小化、模式关闭及退出都执行失效/暂停/清理；迟到结果不能复活已取消服务。
- 每次 Paint 读取缓存，不同步抓屏/模糊；不调用硬件采样。静态效果不新增动画计时器。

完整所有权、取消与预算合同见 v4 §3.4。

### 3.3 新增背景渲染器

新增 `src/System/Backdrop/BackdropRenderer.cs`：根据 resolved mode 绘制 solid / gradient / 已验证玻璃帧，再叠加 tint、固定噪点与边缘高光。未就绪首帧始终有背景底色；预览用固定样例源，不启动桌面捕获。

### 3.4 捕获与模糊验证

- 正式方案不允许周期性移动、隐藏主窗或更改 Opacity 来抓屏；不能用上一帧自身区域回填或强 tint 作为“真实背景已恢复”的证明。
- `WDA_EXCLUDEFROMCAPTURE` 的实际图像结果按捕获后端/系统版本实测，不能从文档一句“不出现”推断一定是洞或一定露出背景。记录录屏副作用，不默默永久设置。
- 使用移动棋盘/文字作背景连续观察，确认无自身标题/数值轮廓、递归变暗、拖影或窗口抖动；负坐标、跨屏 DPI、高面板与 RDP 单列。
- 缩小再放大只是近似缩放。捕获路线须定义 Blur 半径及实际滤波，比较 Blur=0/12/24 的边缘变化；不能把 SmoothingMode 当模糊算法。
- 仅处理所需区域，默认2fps、上限6fps；处理中合并请求。捕获/滤波/呈现分别计时，不能把经过时间当 CPU 时间。
- 不可用或超预算时降级 gradient→solid，保留原因；降级不计为实时玻璃目标完成。

### 3.5 主题字段

以 v4 的 JSON 合同为准：Mode（solid/gradient/glass）、Opacity=0.75、Blur=12、Tint、GradientStart/End、GradientAngle=90、Noise=0、EdgeGlow=0、Fps=2。缺失 Backdrop 不创建服务，未知 Mode 走旧背景；只创建了对象不代表启用。

新数值先在运行时副本归一化，不自动回写 JSON；Frame/Blur 逻辑尺寸只转换一次。保留 Backdrop 自身层级的未知字段。

### 3.6 挂载与坐标

两个 Renderer 通过兼容重载接收完整客户区 bounds 和 per-window 上下文，Paint 不从全局服务临时取桌面帧。背景、位图采样、轮廓使用一致的物理坐标与裁剪。

保留旧主题现有竖屏覆盖策略及横屏行为；新背景单独验证边缘覆盖。不能把竖屏 `(-5,-5)` 的背景矩形直接用于外框，也不盲目把它复制到旧横屏路径。主窗和编辑器不得共享一个实时捕获服务。

### 3.7 失败、首帧与资源

不支持的系统/窗口样式、RDP/捕获失败、无效尺寸及超预算均有明确降级；窗口全局 Opacity 继续保留用户设置，不以 tint 掩盖未验证的 HDR/色彩差异。

服务未就绪用背景色/渐变，保持原有启动与淡入时序；不让 UI 同步等首帧。100次换肤/尺寸变化后无新增资源持续增长，隐藏后无持续捕获，退出后无迟到写入。具体量化门槛见 v4 §7。

---

## 4. 主题编辑器与完整配置入口

v4 将编辑器/预览并入 M1，不等实时玻璃全部完成后才提供配置入口。

### 4.1 编辑器字段与绑定

新增 `src/ThemeEditor/AppearanceFields.cs`，由 `ThemeEditorForm.LoadSelectedTheme` 每次加载主题时重建绑定，或通过当前主题访问器取值；初始化、打开页面和取消选择不写回模型。

颜色支持 alpha 与恢复未设置；未编辑保留原字符串，不能复用只写 `#RRGGBB` 的选色逻辑处理半透明阴影。新增整数显示关闭/0，并按属性级 WhenWritingDefault 保持缺省。模式、几何、Grid、BarStyle、背景参数及合法范围按 v4 合同提供。

现有 UI 为代码构建，不预设需要修改 resx；仅在实际有资源变化时修改。

### 4.2 预览副本与背景

合并两份手写布局复制路径：副本独立、字段完整、LayoutScale 正确、只缩放一次，不改原模型。增加未来字段覆盖测试，不以再列一份手写清单充当永久保证。

预览字体只借用；装饰使用同一真实 renderer。玻璃用固定样例背景并注明预览，不启动/复用主窗桌面捕获服务。

### 4.3 Web 主色适配

采用原建议 B：页面刷新后跟随当前主题主色，不复现桌面 DWM/抓屏效果。实际 HTML 组装点是 `src/System/WebServer/WebSessionManager.cs` 的 `WebPageContent.IndexHtml.Replace(...)`，不是 LiteWebServer 的快照写入方法。

新增 CSS 构造器读取 UI 发布的不可变颜色快照；规范输出 rgba 或 RGB，不直接插入用户字符串，也不把主题 ARGB 当作 CSS 八位 RGBA。保持仅取 HTML/图标不产生硬件需求，保留现有鉴权、连接代次、FPS和采样协调规则。

### 4.4 兼容、缓存与性能

- 主面板旧主题关闭新能力时逐像素一致；只读打开不改文件，保存按 JSON 语义保真。
- 统一缓存清理入口：ParseColor 以色值字符串为 key，不因不清缓存而自然串色；新派生资源须有代次和预算。ClearCaches 已会清 UIUtils，避免重复接线。
- 生产 UI 最短80ms（12.5Hz）；旧探针固定旧主题、依赖硬件就绪，必须增加独立外观入口。离屏结果与真实窗口/DWM成本分别记录。
- 静态装饰不新增动画；真实背景未放行、失败或超预算时降级。资源压力、真实 DPI、局部重绘和保存失败按 v4 验收。

---

## 5. 里程碑与验收

| 里程碑 | 交付 | 状态/门槛 |
|---|---|---|
| M0 | schema、颜色、未知字段、安全保存、预览副本合同、无硬件依赖的探针 | 待实施；旧主题主面板不变、往返与故障写入验证通过 |
| M1 | 机能装饰、静态背景、窗口轮廓、编辑器/预览、Web主色、新机能主题 | 待实施；关闭/开启效果、横竖/DPI/多卡/局部重绘及量化预算通过 |
| M2 | 经A06放行的实时玻璃后端、帧服务、新玻璃主题、降级与发布 | 待实施；无自反馈/移窗抓屏，资源与性能可复核，支持矩阵如实列限制 |

具体任务 A00–A17 与量化标准见 [v4 执行计划](superpowers/plans/2026-10-20-appearance-agent-execution.md)。历史描述的“Win10/11表现一致”改为支持的后端正确、其余明确降级；不能用降级完成实时目标。

---

## 6. 文件与修改归属

以 v4 的独占写集为派发依据，新增文件也可能依赖共享接口和项目配置，不能称为“零冲突”。

| 工作线 | 主要文件/目录 |
|---|---|
| 模型与纯合同 | `src/Core/ThemeManager.cs`；新增 options、布局复制/颜色快照及测试 |
| 保存可靠性 | `src/ThemeEditor/ThemeFileService.cs` 及独立临时目录测试 |
| 装饰/窗口策略 | 新增 `src/UI/Decor/` 与窗口外观组件；既有 UIUtils/helper 接线交主集成者 |
| 主窗口集成 | `UIRenderer`、`HorizontalRenderer`、`UIController`、`MainForm_Transparent`、`MainFormWinHelper`、必要 `UIUtils` |
| 背景 | `src/System/Backdrop/`、`tests/AppearanceBackdropProbe/`；仅放行后端进入实时服务 |
| 编辑器 | `ThemeEditorForm`、`ThemePreviewControl`、新增 `AppearanceFields`；必要语言 key |
| Web | `WebSessionManager`、`WebPageContent`、新 CSS 构造器及测试 |
| 主题与证据 | 两个新主题；PerformanceProbe 的外观入口、夹具、截图/测量结果与支持矩阵 |

所有项目/发布配置由主集成者统一修改。不新增全局 Settings 外观覆盖，不批量改10个旧主题，不改硬件采样或GPU身份逻辑。

---

## 7. 执行顺序与已定边界

1. A00 固定实际基线与合同；A01模型、A02安全保存、A04探针、A06玻璃验证可并行。
2. 合同就绪后推进机能装饰、静态背景、编辑器、Web和成品主题；共享主窗口文件由同一人接线。
3. 先验收 M0/M1；玻璃后端通过 A06 才能正式接入M2。验证失败继续完成其他工作并记录实时部分未完成。
4. 验证与打包使用隔离输出；同机 CPU/捕获实验串行，避免污染测量。

v4 的确定选择：主题 JSON 管理外观，不引入全局覆盖；效果缺省关闭；Web只跟主色并在刷新后生效；任务栏/硬件详情/控件整体换肤排除；字体算法不顺手调整；本轮保留原有MIT来源/许可，不新增外部字体资产要求。

建议1名主集成者+3–5名工作agent按就绪任务补位。可直接派发的提示词、完整依赖与交接模板见 v4 §5–§8。

---

## 8. 历史工作量估算（v3，保留参考）

> 以下是旧稿记录，本轮未复跑构建/测量；新增保存失败、完整窗口生命周期、独立探针与玻璃门槛后，文件数/行数和8–11天不再作为v4承诺。实际按工作包验收，不能从代码行数推算多agent工期。

### 8.1 当时的环境与测量（本轮未复测）

| 条件 | 状态 |
|---|---|
| .NET SDK | ✅ 8.0.426（系统 + `G:\TOOL\dotnet-sdk`） |
| 主工程构建 | ✅ `dotnet build -c Release` **3.48 s，0 错误**（242 个既有警告） |
| 单元测试工程 | ✅ `tests/LiteMonitor.Tests/`（xUnit，25 个测试文件） |
| 性能探针 | ✅ `tests/LiteMonitor.PerformanceProbe/`，**已含"兼容缓冲绘制"基准**（用真实 `UILayout`/`UIRenderer`/`HorizontalRenderer` 测绘制成本）→ 阶段二/三的开销可**量化**而非靠感觉 |
| 渲染性能基线 | 历史提交 `5a8bb4c`：竖屏240×788 / 13项，4.56ms墙钟 / 4.44msCPU / 6776B每帧；横屏768×56，2.37ms / 2.35ms / 4640B；scale2时物理尺寸480×1576。此处为用户缩放模拟，不是真实DPI验证 |
| 与改名合规工作流冲突 | ✅ **无**。rebrand 已合入 HEAD，但**未触碰** `ThemeManager.cs` / `UIRenderer.cs` / `HorizontalRenderer.cs` / `MainFormWinHelper.cs` / `ThemeFileService.cs`；仅 `UIUtils.cs`（品牌注释）与 `WebPageContent.cs`（标题）被碰过，位置可错开 |

### 8.2 逐阶段改动量

| 阶段 | 改动文件 | 新增行 | 修改行 | 新增文件 | 编码 | 验证/调优 | 小计 |
|---|---|---|---|---|---|---|---|
| **一** 主题系统补齐 | `ThemeManager.cs` | ~155 | ~3 | 1（`ThemeSerializationTests.cs`） | 0.5 天 | 0.5 天 | **1 天** |
| **二** 机能风 | `UIUtils.cs`、`UIRenderer.cs`、`HorizontalRenderer.cs`、`MainFormWinHelper.cs`、`UIController.cs` | ~640 | ~90 | 2（`DecorRenderer.cs` + 1 个机甲主题） | 1.5 天 | 1–1.5 天 | **2.5–3 天** |
| **三** 液态玻璃 | `ThemeManager.cs`、`UIRenderer.cs`、`HorizontalRenderer.cs` | ~950 | ~55 | 2（`BackdropService.cs` / `BackdropRenderer.cs` + 1 个玻璃主题） | 2.5 天 | 2–4.5 天 | **4.5–7 天** |
| **合计** | **6 个文件改** | **~1750** | **~150** | **6** | ~4.5 天 | ~4–6.5 天 | **8–11 天** |

（阶段三的"新增行"含一次性技术验证程序 ~200 行与视觉对比截图工具 ~120 行，验证后是否保留取决于是否固化为回归工具。）

### 8.3 阶段三的不确定性来源

1. **视觉主观** —— "液态玻璃"没有客观验收标准，迭代次数不可预测。
2. **抓屏面积比预估大** —— 竖屏面板实测已 **788 px 高**（scale 2 时 **1576 px**），
   多显卡下更高 → 抓屏与模糊成本按面积上升，1/4 降采样可能需降到 1/6。
3. **边界场景未实测** —— 跨显示器 / 负坐标 / 异构 DPI / RDP 下抓屏行为未知。
   → 阶段三的"前置技术验证"不可跳过，且**必须包含多显卡高面板**这一档。

### 8.4 当时的配置入口缺口（v4已前移编辑器）

阶段一、二的新字段（`FrameStyle` / `Scanline` / `Glow` / `Backdrop.*`）在**阶段四编辑器完成前**
只能靠手改主题 JSON。

**建议的缓解做法**：由实现方直接产出 2 个成品主题文件 ——
`CyberPunk_Mech.json`（阶段二）与 `LiquidGlass.json`（阶段三），
用户切换主题即可看到效果，无需等编辑器。

### 8.5 建议的提交粒度

沿用仓库既有约定（每个 Task 结束提交一次、中文正文 + 英文 type 前缀、显式 `git add` 不用 `-A`）：

```
feat(theme): 补齐主题 schema 与未知字段保真
feat(ui): 新增机能风装饰层
feat(ui): 新增液态玻璃背景层
```

---

## 附录 A：关键 API 参考（v4修正）

| 用途 | API/规则 | 约束 |
|---|---|---|
| 系统材质 | `DwmSetWindowAttribute` + `DWMWA_SYSTEMBACKDROP_TYPE=38` | Win11 22621+；官方Win32接口不以Windows App SDK为必选；实际layered/GDI效果须验证 |
| 系统圆角 | `DWMWA_WINDOW_CORNER_PREFERENCE=33` | DEFAULT=0、DONOTROUND=1、ROUND=2；禁用不能用0 |
| 自绘区域 | WinForms `Form.Region` + `GraphicsPath` | 与渲染共用几何；定义旧/新Region与路径的释放 |
| 背景捕获 | 经A06验证的后端 | 不把BitBlt名称或提权状态当作所有桌面场景可用的证明 |
| 排除自身 | 后端实测；可验证EXCLUDEFROMCAPTURE | Win10 2004起支持该值；不预设露背景或留洞，记录录屏影响 |
| 模糊 | 明确半径与滤波算法 | DrawImage缩放不是完整Blur合同；SmoothingMode不是模糊算法 |
| 噪点与渐变 | 缓存Bitmap/TextureBrush/LinearGradientBrush | 稳定尺寸与主题/DPI键，有条目和字节预算 |
| 分层/穿透 | 保留现有窗口行为 | 不为截图周期性移动/隐藏主窗或改Opacity |

文档来源及具体实验门槛见 v4 §2/§4。API返回成功不能替代画面与生命周期验收。

---

## 附录 B：调研中发现的既有缺陷（与外观改造相邻，建议一并修）

| 缺陷 | 位置 | 说明 | 处理阶段 |
|---|---|---|---|
| 主题嵌套字段被丢弃 | `src/ThemeEditor/ThemeFileService.cs` → `SaveTheme` | 序列化 `Theme` 对象，未映射键直接丢 | 阶段一 §1.4 |
| `font.scale` 被丢弃 | `src/Core/ThemeManager.cs` → `FontConfig` | 全部 10 个主题都声明了，但无对应属性 | 阶段一 §1.4 |
| 保存时写出 `null` | `src/ThemeEditor/ThemeFileService.cs` → `SaveTheme` | 无 `DefaultIgnoreCondition` | 阶段一 §1.5 |
| 写出 `ItemSize` | `src/Core/ThemeManager.cs` → `FontConfig` | 内部 setter 泄漏到 JSON | 阶段一 §1.5 |
| 换主题不刷新圆角 | `src/UI/Helpers/MainFormWinHelper.cs` → `ApplyRoundedCorners` | 只绑 `Resize`；`ApplyTheme` 无调用 | 阶段二 §2.4 |
| `Resize` 重复绑定 | `MainFormWinHelper.InitializeStyle` + `MainForm.BindEvents` | 每次 resize 跑两遍 | 阶段二 §2.4 |
| 字体DPI行为需取证 | `src/Core/ThemeManager.cs` → `Theme.Scale` | 字体为Point单位，不能从没有显式乘DPI就断定字号错误或再乘一次 | 本轮不改字体算法 |
| 缓存清理入口重复/未统一 | `src/Core/ThemeManager.cs` → `ThemeManager.ClearCaches` | 颜色字符串缓存不因换肤自然串色；新派生资源须有代次/预算，现有ClearCaches已清UIUtils | v4 A13统一接线 |
| `ApplyTheme` 重复清缓存 | `src/UI/UIController.cs` → `ApplyTheme` | `UIRenderer.ClearCache()` 与 `UIUtils.ClearBrushCache()` 最终落到同一方法 | 阶段四 §4.4 |
| 预览丢新字段 | `src/ThemeEditor/ThemePreviewControl.cs` → `CreatePreviewTheme` / `CreateScaledTheme` | 两处手写 `LayoutConfig` 拷贝 | 阶段四 §4.2 |
| 网页版与主题零耦合 | `src/System/WebServer/WebPageContent.cs` | 切主题后网页版不变 | 阶段四 §4.3 |

## 附录 C：复审修订记录（v2）

> 本节为历史记录；圆角常量、捕获排除与并发结论已由v4再次纠正，以执行稿为准。

独立复审发现 19 项问题，已全部处理。要点：

| 级别 | 问题 | 处理 |
|---|---|---|
| **P0** | `[JsonExtensionData]` 挂 `Theme` 上对嵌套字段无效 → 计划宣称的 bug 修复不会发生 | §1.4 改为**多层级挂载**（`Theme` + `ColorConfig` + `FontConfig` + `LayoutConfig`） |
| **P0** | `GlowPath` "用完即 Dispose" 与 "复用 `GetPen` 缓存" 自相矛盾 → 会释放共享 Pen 导致 GDI+ 错误 | §2.1 约束 1：`GlowPath` 自建 `Pen`，**禁止 Dispose 缓存对象** |
| **P0** | `TextureBrush`/`LinearGradientBrush` 无法进入 `GetBrush`（返回 `SolidBrush`） | §2.1 约束 2：新增独立缓存 |
| P1 | Win11切角需显式禁用系统圆角 | v4修正为 `DWMWCP_DONOTROUND=1`，0是DEFAULT |
| P1 | 圆角实现是 `Form.Region` 而非 `SetWindowRgn`，措辞错误 | §2.4 / 附录 A 更正 |
| P1 | `SetWindowDisplayAffinity` 会留洞、"偏移抓取"几何不成立 | §3.4 改为三方案决策表 |
| P1 | 模糊缺 GDI+ 关键设置与中间位图 | §3.4 补全 |
| P1 | `SaveTheme` 会写出 `null` | §1.5 改用属性级 `JsonIgnore(WhenWritingNull)` |
| P1 | `BackdropConfig` 的 "null=关闭" 与仓库 `= new()` 风格冲突 | §3.5 改用 Mode 值判据 |
| P1 | `(-5,-5)` 溢出只存在于竖屏，横屏是新增 | §3.6 拆分说明 |
| P1 | 玻璃首帧与 `Opacity` 渐入会跳变 | §3.7 补风险行 |
| P2 | 网页版与主题零耦合（完全遗漏） | §0.3 补入 + §4.3 决策点 |
| P2 | 预览的两处 `LayoutConfig` 拷贝会丢新字段 | §4.2 补修法 |
| P2 | 8+ 个主题消费点未列出 | §0.3 全景表 |
| P2 | 命名空间表不完整/有例外 | §0.4 补例外清单 |
| P2 | "颜色缓存无失效机制"表述有误 | 附录 B 更正为"死代码，没接线" |
| P2 | 阶段三并发模型未定义 | §3.2 写死 5 条约束 |
| P2 | 工作量偏乐观 | 阶段三改为 1–2 周，阶段四 3–4 天；阶段三增设前置技术验证门禁 |
| P3 | 3 位 `#RGB` 不支持 / `rgba()` 静默失效 / `BarLow` 死字段 / 托盘图标 / `ItemSize` | §0.1、§2.5、附录 B 分别记录 |

**保留的优点**（复审确认值得坚持）：新增优先与单挂载点策略、任务栏能力边界的主动划定、
`JsonExtensionData` 保护并行开发者字段的动机、阶段三先做技术验证的判断、
拒绝未文档化 `SetWindowCompositionAttribute`、`不启用新外观 = 零变化`的验收底线。

---

## 附录 D：代码变更影响评估（v3，2026-10-20）

> 本节保留v3当时的变更对照；“锚点未修改”不足以证明设计正确，也不能排除共享UIController/Web入口的集成风险。

基线 `169e90a` → `97f793d`，**109 个提交 / 73 个文件 / +8884 −1024 行**。
主题外观相关的**核心锚点文件几乎未被触碰**，计划主体成立。

### D.1 锚点文件变更情况

| 锚点文件 | 变更 | 对计划的影响 |
|---|---|---|
| `src/Core/ThemeManager.cs` | **未改动** | ✅ 阶段一（`ColorConfig`/`FontConfig`/`LayoutConfig`/`ParseColor`/扩展槽）**完全不受影响** |
| `src/UI/UIRenderer.cs` | **未改动** | ✅ 阶段二/三竖屏挂载点安全 |
| `src/UI/HorizontalRenderer.cs` | **未改动** | ✅ 阶段二/三横屏挂载点安全 |
| `src/UI/Helpers/MainFormWinHelper.cs` | 当时未改动 | v4仍需验证 `DWMWCP_DONOTROUND=1`、初始化顺序、Form.Region和句柄重建 |
| `src/ThemeEditor/ThemeFileService.cs` | **未改动** | ✅ 阶段一序列化修复安全 |
| `src/UI/UILayout.cs` / `src/UI/HorizontalLayout.cs` | **未改动** | ✅ 布局计算不受影响 |
| `src/Core/MetricItem.cs` | **未改动** | ✅ 数值格式化/动画不受影响 |
| `src/UI/TaskbarRenderer.cs` / `src/UI/Helpers/TaskbarWinHelper.cs` | **未改动** | ✅ §2.6 任务栏边界判断仍成立 |
| `src/Core/UIUtils.cs` | 1 行（注释改名） | ✅ 阶段二追加工具函数安全 |
| `src/ThemeEditor/ThemePreviewControl.cs` | 1 行（注释） | ✅ §4.2 的字段拷贝问题仍在 |
| `src/ThemeEditor/ThemeEditorForm.cs` | 2 行（窗口标题） | ✅ §4.1 拆 `AppearanceFields.cs` 策略仍适用 |
| `src/System/WebServer/WebPageContent.cs` | 3 行（标题/品牌） | ⚠️ §4.3 网页版决策点**仍未解决** |
| `src/UI/UIController.cs` | +182 | ⚠️ `Render` 方法**结构未变**（竖屏/横屏分支一字未动）→ 挂载点安全；但新增 `RefreshPerCardGroupTitles` / `ComputeUiRefreshIntervalMs` |
| `src/Core/Settings.cs` | +43 | ⚠️ 新增 `WebFpsEnabled` / `MonitoredGpuCount` / `MarketPlugins` / `RecordedMaxGpuDynamic` → 冲突面变大 |

### D.2 新增的重大事实

| 事实 | 影响 |
|---|---|
| **产品已改名 LiteMonitor++**（`UIUtils` 注释、`WebPageContent` 标题、`ThemeEditorForm` 窗口标题） | 本计划不涉及品牌文字，**无需改动**；但后续若要加品牌化标题，注意用新名 |
| **多显卡真的实现了**：`Settings.MonitoredGpuCount` + `GpuKey` / `GpuRegistry` / `GpuGroupPolicy` / `GpuItemSync`，且**按卡拆组**（`GPU.0`/`GPU.1` 各自一组） | ⚠️ **组块数量随显卡数线性增长** → 装饰层绘制调用量、玻璃背景抓屏面积都随之上升。已在 §2.2 / §3.4 补性能约束 |
| **插件市场落地**：`PluginMarket.cs` + `PluginMarketPage.cs`(439 行) + `PluginSelfCheck.cs` + `PluginMarketState.cs` + `PluginI18n.cs` | 与本计划**无交集**；设置页在并行扩张 → 阶段四碰设置页要更谨慎 |
| **`GroupTitleResolver`** 统一了 4 个界面的组标题 | 装饰层若需感知组标题，**复用它**，不要自己算 |
| **硬件侧大重构**：`HardwareSamplingCoordinator` / `FpsCounter` / `HardwareValueProvider` / `MaintenanceScheduler` | 外观不得改变采集语义；共享UI/Web入口仍需回归，不能简单断言无交集 |

### D.3 附录 B 的v3历史复核（v4已再次校正）

| 缺陷 | 复核结果 |
|---|---|
| `ThemeManager.ClearCaches()` 零调用 | ✅ 仍然只定义、**全仓无调用点** |
| `ApplyTheme` 不调 `ApplyRoundedCorners` | ✅ 仍然没有 |
| `ApplyTheme` 重复清缓存 | ✅ `UIRenderer.ClearCache()` 与 `UIUtils.ClearBrushCache()` 仍在同一方法里各调一次 |
| 预览丢新字段 | ✅ `ThemePreviewControl` 的两处手写 `LayoutConfig` 拷贝仍在 |

### D.4 结论

v3曾根据核心锚点未改动判断原设计可沿用；v4实际复审发现圆角常量、绘制层次、窗口初始化、探针、序列化和玻璃路线仍需修订。当前不再以“文件没动”作100%正确或兼容的结论。

需要调整的只有三处性能/范围类问题，已在 v3 修入正文：
1. §2.2 —— 装饰层绘制量随组块数线性增长，需实测 4 卡场景
2. §3.4 —— 玻璃抓屏面积随面板高度增长，1/4 降采样可能不够
3. §6 / §7 —— 阶段三的"全局覆盖（碰 `Settings.cs`）"**建议取消**

另有一项**新增的产品决策**需拍板：网页版监控面板（`WebPageContent`）仍与主题零耦合，
切到 CyberBlue 主题后网页纹丝不动 —— 见 §4.3 选项 A / B。
