# 主题工作室能力边界

本文规定主题工作室能够编辑、预览与应用哪些内容，以及哪些内容不可编辑。

工作室的组件划分与数据模型见同目录 [architecture.md](architecture.md)；GPU 材质的视觉规范见 [../gpu-themes/visual-spec.md](../gpu-themes/visual-spec.md)。

---

## 1. 三个状态的归属

| 状态 | 归属方式 |
|---|---|
| 主题类型 | 由 `rendering.backend` 决定：值为 `gpu` 时必须搭配 GPU 材质，值为 `legacy` 时必须搭配 legacy 材质。没有第三种类型。 |
| 编辑目标 | 当前编辑器不存在「编辑目标」概念，只有 `IsGpuTheme` / `IsToolbarTheme` 两个布尔标记与只读提示。GPU 主题的「主效果 / 兼容外观」不是独立的编辑目标，而是同一份主题内的两个渲染面。 |
| 实际预览路线 | 主竖向面板：GPU 激活时走 `GpuPresentationHostController`，否则走 `HorizontalRenderer` 一类的常规绘制。横向面板：GPU 激活时走 GPU 场景快照（`UiSceneSnapshotAdapter.BuildHorizontal`），与竖向共用同一布局构造入口。任务栏：**始终常规绘制**，场景适配器没有任务栏构建器。 |

生产消费点：`UIController.CurrentRenderTheme` 返回 `_legacyFallbackTheme ?? ThemeManager.Current`。

---

## 2. GPU 渲染参数的能力矩阵

参数来源是主题 JSON 的 `rendering.parameters`；运行时由 `MaterialRuntimeSettings.Create` 归一化，缺键或非数值回落默认值，超出取值域夹取到边界。

### 2.1 按材质分列的参数

| 参数 | 取值域 | liquid_glass | cyber_punk |
|---|---|---|---|
| `cornerRadius` | 0-64 | 26 / 工具条 30 | 固定 0 |
| `thickness` | 0-8 | 1 / 工具条 8 | 固定 0 |
| `refraction` | 0-8 | 1.6 / 工具条 8 | 固定 0 |
| `blurRadius` | 0-32 | 8 | 固定 0 |
| `clearCenterRatio` | 0-1 | 0 | 固定 0 |
| `surfaceOpacity` | 0-1 | 0.055 | 固定 1 |
| `highlight` | 0-1 | 1 / 工具条 0.72 | 固定 1 |
| `counterHighlight` | 0-1 | 1 | 固定 1 |
| `shadow` | 0-1 | 0.34 / 工具条 0.115 | 0.34 |
| `fogStrength` | 0-1 | 0.42 / 工具条 0.16 | 固定 0 |
| `tintStrength` | 0-1 | 工具条 0.038，否则取 `surfaceOpacity` | 固定 1 |
| `shadowRadius` | 0-64 | 0 / 工具条 30 | 固定 0 |
| `primaryCut` | 0-64 | 固定 0 | 14 |
| `secondaryCut` | 0-32 | 固定 0 | 9 |
| `outlineWidth` | 0-8 | 固定 0 | 1 |
| `glow` | 0-100 | 固定 0 | 12 |
| `ambientRatio` | 0-1 | 固定 0 | 0.12 |
| `panelOpacity` | 0-1 | 固定 1 | 0.965 |
| `glassStyle` | `standard` \| `floatingToolbar` | 消费 | **强制 Standard** |

**控件暴露规则**：检查器只显示该材质实际消费的参数。校验允许但运行时不消费的交叉项不得出现在对应材质的检查器里，避免「能调但无效果」。

### 2.2 GPU 动效

| 参数 | 取值域 | liquid_glass 默认 | cyber_punk 默认 | 消费 |
|---|---|---|---|---|
| `motionEnabled` | 布尔 | true | true | 两者 |
| `targetFps` | 1-240 | 30 | 30 | 两者 |
| `stateTransitionMs` | 0-5000 | 180 | 140 | 两者 |
| `scannerPeriodMs` | 0-60000 | 0 | 2800 | 两者 |

### 2.3 工具条样式消费的主题字段

`glassStyle` 为 `floatingToolbar` 时，前景由 `ThemeForegroundStyleFactory.Create` 从主题派生；其余样式使用固定前景。

**字体（6 项映射）**：`Font.Family` → UI 字体族与说明文字字体族；`Font.ValueFamily` → 数值字体族与展示字号字体族；`Font.Value` → 展示字号与正文字号；`Font.Group` → 标题字号；`Font.Item` → 说明字号；`Font.Bold` → 粗体开关。

**颜色（12 项角色映射）**：`TextPrimary` → 前景主色；`TextSecondary`（为空时回退 `TextGroup`）→ 次要色；`ValueSafe` → 正常值色；`ValueWarn` → 警告色；`ValueCrit` → 危险值色；`BarLow` → 强调色与安全进度色；`BarMid` → 警告进度色；`BarHigh` → 危险进度色；`Border` → 描边色；`Shadow` → 阴影色；`BarBackground` → 轨道色；`TextGroup` → 分组标题色。

**限制**：`standard` 样式与 `cyber_punk` 使用固定前景，不消费主题的字体与颜色字段。检查器在这两种情况下不得显示主题字体与颜色控件。

### 2.4 兼容外观配色

`rendering.fallbackStyle` 提供六个槽位：`background`、`panelBackground`、`border`、`text`、`mutedText`、`accent`。槽位值为 `null` 时不写出。

- 默认值：`#1E2228` / `#282D35` / `#3C444F` / `#F2F4F7` / `#B4BBC4` / `#8FB4D9`。正文对比度不低于 4.5，由测试锁定。
- 非法值不致命：产生诊断 `gpu_fallback_style_invalid: rendering.fallbackStyle.<field>`，该字段回落默认值，不影响整份主题的可用性。
- 非字符串值或整块非对象：解析期忽略并记入 `InvalidFields`，加载不失败。
- 块内未知键进 `ExtensionData` 原样保留。

### 2.5 回退派生主题

`LegacyFallbackStyleBridge.BuildDerivedTheme` 把兼容外观的六槽写入六个色槽：`Background` / `GroupBackground` / `Border` / `TextPrimary` / `TextSecondary`，以及 `Accent` → `TextTitle`。检查器把 `fallbackStyle.accent` 标为「标题强调色」，不冒充进度条色。

布局、阈值、背景与渲染配置按引用共享源主题；运行期字体字段共享同一 `Font` 实例，派生主题不重建字体、不缩放、不释放。

兼容外观**没有独立的字体、布局、圆角与阈值字段**——它只有六槽配色。

### 2.6 经典主题字段

经典渲染读取既有 `Theme` 属性（配色、字体、布局、阈值、背景）。现有的外观编辑面板是**硬编码行**的实现，不是元数据驱动的字段目录；`AppearanceFields.cs` 中的 `AppearanceColorText`（解析与 alpha 冻结语义）、`AppearanceThemeConfig`（`CloneConfig` / `RestoreConfig` / `CloneRendering` / `CloneFallbackStyle`）、键名常量与缺省恢复模式可作为字段目录的实现参考。

---

## 3. 视图表与预览路线

| 视图 | 经典路线 | GPU 路线 | 预览覆盖 |
|---|---|---|---|
| 主竖向面板 | `UILayout` + `UIRenderer.Render` | `GpuPresentationHostController` + `GpuSceneRenderer` | 两者均有预览 |
| 横向面板 | `HorizontalRenderer.Render`（GPU 未激活时） | GPU 场景路线：`HorizontalLayout` + `UiSceneSnapshotAdapter.BuildHorizontal` | 无预览 |
| 任务栏 | `TaskbarRenderer` | 同左（始终常规绘制） | 无预览 |
| 回退显示 | 源主题本身 | `LegacyFallbackStyleBridge.BuildDerivedTheme` | 无同源预览 |

经典预览使用固定模拟输入（CPU 23% / 65°、GPU 90% / 65° / 41%、内存 68%、磁盘、网络），背景是固定样例图，**不抓屏**——预览相关源码不含任何抓屏调用。两个预览控件均无命中测试：点选定位若要实现，应复用 `UILayout` 的布局结果或场景节点标识，不建第二套坐标系统。

GPU 预览目前每次换主题都会释放并重建会话。

---

## 4. 保存与应用

| 项 | 现状 |
|---|---|
| 撤销 | 最多 32 个配置快照，无重做；比较整份 JSON 字符串签名 |
| 原子写入 | 临时文件（`.litemonitor-tmp` 后缀）→ `File.Replace`（目标不存在则 `File.Move`）→ 失败删除临时文件 |
| 故障注入 | `TempFileWriter` / `TargetReplacer` 为 internal 委托，配 `ResetTestHooks` |
| 外部修改冲突 | **无** mtime 或哈希检测 |
| 另存为 | `File.Move(overwrite: false)`，不覆盖同名文件并校验保留名 |
| 默认值物化 | 保存时全量序列化 `Theme` 对象，靠属性级 `WhenWritingDefault` / `WhenWritingNull` 控制省略；草稿必须保留「源字段是否存在」的信息，不能靠全量序列化还原 |
| 保存后应用 | 保存成功后询问是否立即应用；应用路径通过**反射**取主窗体的控制器与设置对象，再 `settings.Save()` + `ApplyTheme` |
| 同名重载 | `UIController.ApplyTheme` → `ThemeManager.Load` → 缩放 → 主题变更通知 → 任务栏样式重载 → 新建 `UILayout` → 刷新回退主题 → 释放旧字体 |

---

## 5. GPU 启动保护归属

`GpuStartupGuard` 的 `Shared` 是进程级单例，状态目录 `%LocalAppData%\LiteMonitor++\gpu-startup`，按安装目录的 SHA-256 命名标记文件。

- pending 标记在**首次**尝试开始时写入，最后一个尝试终结且集合清空时删除。
- 启动时发现 pending 即进入保守模式，此后**拒绝一切新尝试**。
- `AbandonAttemptPreservingPending` 只注销尝试、**故意保留 pending**，使下一次进程启动进入保守模式。
- 生产宿主 `GpuPresentationHostController` 与编辑器预览 `GpuThemePreviewControl` **共用同一个 `Shared`**。

由此得到一条约束：**预览不得写 pending，不得消费或清除生产崩溃标记**。当前预览在生产共享状态上调用 `MarkFirstSuccessfulFrame()`，这会让预览异常退出把主窗体下一次启动打入保守模式，主窗体一旦进入保守模式又会连坐拒绝预览的 GPU 尝试。预览必须建立独立的保护归属。

---

## 6. 缩放约束

.NET 8 WinForms 在 PerMonitorV2 下，`AutoScaleMode.Dpi` 配合 `AutoScaleDimensions=(96,96)` **不会**重缩放代码里显式设置的 `ClientSize`。窗口缩放必须采用在句柄创建后读取 `DeviceDpi`、经唯一换算入口折算全部几何的方式（`AutoScaleMode.None`）。

窗口自摄用运行期 `DrawToBitmap`，不用屏幕拷贝——混合 DPI 多显示器下 `CopyFromScreen` 的坐标会错位，且会摄入窗口外的桌面内容。
