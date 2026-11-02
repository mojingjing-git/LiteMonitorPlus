# 主题文件格式规格

本文描述 LiteMonitor++ 主题文件的完整格式：顶层段与字段语义、取值范围与缺省行为、颜色写法与解析规则、布局与字体的缩放规则、GPU 材质扩展段、主题目录与加载优先级、导出契约以及校验规则与错误码。

适用范围：随程序发布的内置主题、用户在主题工作室中保存的主题、主题市场安装的主题，三者使用同一份文件格式与同一套加载语义。本文规定的是**文件契约**（键名、取值、解析、校验、落盘行为）；渲染器内部的绘制实现不在本文范围内。

适用对象：主题文件作者、主题工作室与主题市场插件的维护者、以及需要在脚本中生成或校验主题文件的自动化流程。

---

## 1. 总览

### 1.1 主题文件是什么

主题文件是一个 UTF-8 编码的 JSON 文档，扩展名 `.json`，描述一份外观定义：面板几何、字体、颜色、阈值、背景，以及可选的 GPU 材质参数。

一个主题文件只描述一份外观，不含任何机器相关的运行时数据（窗口句柄、缓存、渲染状态）。运行时产生的对象（例如已创建的字体）不写回文件。

### 1.2 主题目录与命名空间

全系统只存在**一个**主题目录：

```
<AppContext.BaseDirectory>/resources/themes
```

它同时承担三种角色：

- 发布包携带的内置主题所在目录；
- 主题工作室保存主题时写回的目录；
- 主题市场安装主题时写入的目录。

⇒ 因此**不存在独立的"用户目录"或"市场安装目录"**，三类主题共享同一个平铺命名空间，同一目录下文件名互不相同即视为不同主题。

主题目录不存在时由 `ThemeManager` 创建。目录内只有扩展名为 `.json` 的文件会被枚举为可用主题；文件名去掉 `.json` 后的部分就是主题名（例如 `RefinedDark.json` 的主题名为 `RefinedDark`）。临时文件采用 `.litemonitor-tmp` 后缀，天然不会被 `*.json` 匹配。

主题工作室的草稿恢复目录 `%LocalAppData%\LiteMonitor++\theme-studio\drafts` 只保存未提交的编辑草稿，**不是主题来源**，加载器不读取它。

### 1.3 主题身份

主题以**文件名**为身份标识：文件名（不含扩展名）即主题 id，用于设置项 `Settings.Skin`、主题选择列表、市场安装记录与卸载流程。

⇒ 主题文件内部的 `name` 字段只是展示用标题，不参与身份判定，也不决定加载优先级。

---

## 2. 文件格式

### 2.1 编码与 JSON 方言

| 项目 | 规则 |
| --- | --- |
| 编码 | UTF-8。加载器读取时接受带 BOM 的文件；工作室与导出器写出的文件不带 BOM。 |
| JSON 方言 | 标准 JSON。加载路径允许尾随逗号（`AllowTrailingCommas`）；**市场校验路径不允许**尾随逗号、注释，嵌套深度上限 64。 |
| 缩进 | 不影响语义。工作室写出的文件为缩进格式。 |
| 根节点 | 必须是 JSON 对象；根节点为数组或标量时加载失败。 |

### 2.2 键名匹配与未知键

- **所有已知键名大小写不敏感**：`Background`、`background`、`BACKGROUND` 等价。
- 未知键**必须被保留**并原样往返：顶层以及 `layout` / `font` / `color` / `thresholds` / `backdrop` / `rendering` 各层都支持扩展数据，未知键在读取、保存、导出后依然存在。
- 当未知键与已知属性同名（忽略大小写）时，**已知属性获胜**：扩展数据中的同名键在解析后被剔除，避免出现"同一个键有两个值"的歧义。

### 2.3 顶层结构

一个完整主题文件的顶层段如下（各段都可缺省，缺省行为见第 3 章）：

```jsonc
{
  "version": 3,
  "name": "Refined Dark",
  "layout":   { "rowHeight": 40, "padding": 12, "cornerRadius": 12, "frameStyle": 2, "frameWidth": 1 },
  "font":     { "family": "Microsoft YaHei UI", "valueFamily": "Consolas", "title": 11.5, "bold": true },
  "thresholds": { "load": { "warn": 65, "crit": 85 }, "temp": { "warn": 50, "crit": 70 } },
  "color":    { "background": "#202225", "textTitle": "#FFFFFF", "valueSafe": "#66FF99" },
  "backdrop": { "mode": "gradient", "opacity": 0.75, "gradientStart": "#202225", "gradientEnd": "#2B2D31" },
  "rendering": {
    "version": 1,
    "backend": "gpu",
    "material": "liquid_glass",
    "parameters": { "cornerRadius": 26, "surfaceOpacity": 0.055, "glassStyle": "standard", "liveBackground": false },
    "motion": { "motionEnabled": true, "targetFps": 30 },
    "fallback": { "backend": "legacy", "material": "legacy" },
    "fallbackStyle": { "background": "#202225", "panelBackground": "#2B2D31", "text": "#EAEAEA" }
  }
}
```

---

## 3. 顶层段 schema

### 3.1 `name`

| 类型 | 缺省 | 语义 |
| --- | --- | --- |
| string | `"Default"` | 展示用标题。主题选择列表与导出元数据使用它。 |

`name` 与文件名互不约束：文件名决定身份，`name` 只影响显示。缺省即 `"Default"`，不会被自动改写为文件名。

### 3.2 `version`

| 类型 | 缺省 | 语义 |
| --- | --- | --- |
| number（整数） | `3` | 主题格式的作者声明版本。 |

`version` 目前只被记录并透出到诊断信息与外观快照中，**加载器不对它取值做校验，也不做版本迁移**（见第 10 章）。

### 3.3 `layout`

控制面板几何与装饰。缺失时整段使用缺省值。

| 字段 | 类型 | 缺省 | 取值范围 | 语义 |
| --- | --- | --- | --- | --- |
| `width` | number | `240` | 正数 | 面板逻辑宽度。**运行时由设置项 `PanelWidth` 覆盖**，文件里的取值不生效。 |
| `layoutScale` | number | `1.0` | 正数 | 运行期记录的布局缩放系数，由 `Theme.Scale` 写入，文件里的取值不生效。 |
| `rowHeight` | number | `40` | 正数 | 单行高度（逻辑像素）。 |
| `padding` | number | `12` | 正数 | 面板内边距。 |
| `cornerRadius` | number | `12` | ≥ 0 | 主面板圆角半径。 |
| `groupRadius` | number | `10` | ≥ 0 | 分组卡片圆角半径。 |
| `groupPadding` | number | `8` | ≥ 0 | 分组内部内边距。 |
| `groupSpacing` | number | `30` | ≥ 0 | 分组之间的间距。 |
| `groupBottom` | number | `0` | ≥ 0 | 分组区域底部留白。 |
| `itemGap` | number | `6` | ≥ 0 | 同组内条目之间的间距。 |
| `groupTitleOffset` | number | `6` | ≥ 0 | 分组标题与组内容之间的偏移。 |
| `frameStyle` | number | `0` | `0`/`1`/`2`/`3` | 外框样式：`0` 无外框、`1` 切角、`2` 圆角描边、`3` 静态全息边框。未知值按 `0` 处理。 |
| `frameWidth` | number | `0` | `0`–`8` | 外框线宽（逻辑单位）。 |
| `frameLength` | number | `0` | `0`–`64` | 切角长度，仅 `frameStyle = 1` 有意义。 |
| `scanline` | number | `0` | `0`–`100` | 扫描线强度。 |
| `glow` | number | `0` | `0`–`100` | 辉光强度。 |
| `grid` | number | `0` | `0`–`100` | 网格强度。 |
| `barStyle` | number | `0` | `0`/`1`/`2` | 条形图样式：`0` 单色、`1` 渐变、`2` 纹理。未知值按 `0` 处理。 |

`frameStyle`/`frameWidth`/`frameLength`/`scanline`/`glow`/`grid`/`barStyle` 的缺省值均为 `0`，写出时**不落键**（缺键与 `0` 等价）。

### 3.4 `font`

| 字段 | 类型 | 缺省 | 语义 |
| --- | --- | --- | --- |
| `family` | string | `"Microsoft YaHei UI"` | 标题与文本的字体族。 |
| `valueFamily` | string | `"Consolas"` | 数值列的等宽字体族；为空时回落到 `family`。 |
| `title` | number | `11.5` | 标题字号。 |
| `group` | number | `10.5` | 分组标题字号。 |
| `item` | number | `10.0` | 条目标题字号。 |
| `value` | number | `10.5` | 数值字号。 |
| `bold` | bool | `true` | 四类字体的统一加粗开关。 |

字号是逻辑单位（与 96 DPI 对应的点值）。字体创建失败时，四类字体统一回落到系统标题字体。

> `font.scale` **不是**本格式的字段：文件里出现该键时按未知扩展键保留，不参与任何字号计算（见第 10 章）。

### 3.5 `thresholds`

各指标的状态阈值，结构为 `warn` / `crit` 两个数字。缺失时使用缺省值。

| 子段 | `warn` 缺省 | `crit` 缺省 | 单位 |
| --- | --- | --- | --- |
| `load` | `65` | `85` | % |
| `temp` | `50` | `70` | ℃ |
| `vram` | `65` | `85` | % |
| `mem` | `65` | `85` | % |
| `netKBps` | `2097152` | `8388608` | KB/s |

该段被加载器读取并参与状态着色。主题工作室只提供颜色编辑，不持久化对该段的修改：构建保存/导出载荷时，未提交的 `thresholds` 覆盖会被清除并恢复为源文档值，源文件里已有的 `thresholds` 段按原样保留。

### 3.6 `color`

颜色字段。必需字段缺省为内置深色主题的取值；可选字段缺失即为"未声明"（写出时不落键）。

| 字段 | 类型 | 缺省 | 语义 |
| --- | --- | --- | --- |
| `background` | color | `#202225` | 窗口背景。 |
| `textTitle` | color | `#FFFFFF` | 标题文字。 |
| `textGroup` | color | `#B0B0B0` | 分组标题文字。 |
| `textPrimary` | color | `#EAEAEA` | 正文与条目标题。 |
| `textSecondary` | color | 未声明 | 次要文字。 |
| `valueSafe` | color | `#66FF99` | 正常状态数值。 |
| `valueWarn` | color | `#FFD666` | 警告状态数值。 |
| `valueCrit` | color | `#FF6666` | 严重状态数值。 |
| `barBackground` | color | `#1C1C1C` | 条形图轨道。 |
| `barLow` | color | `#00C853` | 条形图低段。 |
| `barMid` | color | `#FFAB00` | 条形图中段。 |
| `barHigh` | color | `#D50000` | 条形图高段。 |
| `groupBackground` | color | `#2B2D31` | 分组卡片背景。 |
| `border` | color | 未声明 | 描边颜色。 |
| `shadow` | color | 未声明 | 阴影颜色。 |

颜色的书写格式与解析规则见第 5 章。

### 3.7 `backdrop`

背景层配置。**整段缺失**表示使用内置背景绘制路径（不绘制额外背景层）；整段存在时按下表渲染。

| 字段 | 类型 | 缺省 | 取值范围 | 语义 |
| --- | --- | --- | --- | --- |
| `mode` | string | `"solid"` | `solid` / `gradient` / `glass` | 背景模式；`glass` 表示请求玻璃背景效果。 |
| `opacity` | number | `0.75` | `0`–`1` | 背景不透明度。 |
| `blur` | number | `12` | `0`–`32` | 背景模糊半径。 |
| `tint` | color | `background` 的取值 | 任意颜色 | 背景着色。 |
| `gradientStart` | color | `background` 的取值 | 任意颜色 | 渐变起点色。 |
| `gradientEnd` | color | `groupBackground` 的取值 | 任意颜色 | 渐变终点色。 |
| `gradientAngle` | number | `90` | 角度（度） | 渐变方向。 |
| `noise` | number | `0` | `0`–`100` | 噪点强度。 |
| `edgeGlow` | number | `0` | `0`–`100` | 边缘辉光强度。 |
| `fps` | number | `2` | `1`–`6` | 背景动画帧率。 |

`tint`、`gradientStart`、`gradientEnd` 缺省时**跟随其它字段**：`tint` 与 `gradientStart` 取 `color.background`，`gradientEnd` 取 `color.groupBackground`。

所有越界数值在运行时被夹取到区间边界；该归一化只作用于运行期副本，**不回写主题文件**。

---

## 4. `rendering`：GPU 材质扩展段

`rendering` 段声明渲染后端与材质。整段缺失表示使用内置（GDI+）渲染路径。

### 4.1 结构与字段

| 字段 | 类型 | 缺省 | 取值 | 语义 |
| --- | --- | --- | --- | --- |
| `version` | number | `1` | 必须为 `1` | 渲染配置版本。 |
| `backend` | string | `"legacy"` | `gpu` / `legacy` | 渲染后端。 |
| `material` | string | `"legacy"` | `liquid_glass` / `cyber_punk` / `legacy` | 材质。 |
| `parameters` | object | 空对象 | 键值见 4.3 | 材质参数。 |
| `motion` | object | 见 4.5 | 键值见 4.5 | 动效参数。 |
| `fallback` | object | `{"backend":"legacy","material":"legacy"}` | 必须选择 `legacy`/`legacy` | 回退目标。 |
| `fallbackStyle` | object | 未声明 | 见 4.6 | 内置回退外观的配色。 |

`backend`、`material`、`glassStyle` 的比较忽略大小写与首尾空白。

### 4.2 致命校验规则

下列规则不满足时，`rendering` 段被判定为无效（校验错误列表非空），主题不会被当作 GPU 主题使用，渲染路线回退到内置路径：

| 规则 | 错误文案 |
| --- | --- |
| `version` 必须为 `1` | `rendering.version must be 1.` |
| `backend` ∈ {`gpu`,`legacy`} | `rendering.backend must be gpu or legacy.` |
| `material` ∈ {`liquid_glass`,`cyber_punk`,`legacy`} | `rendering.material is not supported.` |
| `backend = legacy` ⇒ `material` 必须为 `legacy` | `rendering.backend=legacy requires rendering.material=legacy.` |
| `backend = gpu` ⇒ `material` 不得为 `legacy` | `rendering.backend=gpu requires a GPU material.` |
| `material = liquid_glass` ⇒ `parameters.liveBackground` 必须是**已存在的布尔值** | `liquid_glass requires parameters.liveBackground.` |
| `material = cyber_punk` ⇒ `parameters.liveBackground` 不得为 `true` | `cyber_punk must not request desktop capture.` |
| `fallback` 必须选择 `legacy`/`legacy` | `rendering.fallback must select legacy/legacy.` |
| `parameters.visualPreset` 存在时必须是字符串 | `rendering.parameters.visualPreset must be a string.` |
| `visualPreset` ∈ {`default`,`nebulaAurora`,`nixieBrass`,`crtPhosphor`} | `rendering.parameters.visualPreset is not supported.` |
| `visualPreset = default` ⇒ 材质必须为受支持的 GPU 材质 | `rendering.parameters.visualPreset=default requires a supported GPU material.` |
| `visualPreset = nebulaAurora` ⇒ 材质必须为 `liquid_glass`；其余命名预设 ⇒ 材质必须为 `cyber_punk` | `rendering.parameters.visualPreset=<value> requires <material>.` |
| `visualPreset = nebulaAurora` ⇒ `liveBackground = false` | `rendering.parameters.visualPreset=nebulaaurora requires liveBackground=false.` |
| `visualPreset = nebulaAurora` ⇒ `glassStyle` 不得为 `floatingtoolbar` | `rendering.parameters.visualPreset=nebulaaurora does not support floatingToolbar.` |

⇒ `liveBackground` 取 `false` 同样满足 `liquid_glass` 的要求：该规则约束的是"必须显式声明是否为布尔"，不是"必须为真"。

数值参数与动效参数的越界规则见 4.3 与 4.5。

### 4.3 `parameters`：材质参数

参数**仅在被显式写出时才校验**；缺失即表示采用材质缺省值。写出的数值必须是有限数且落在取值域内，否则报参数错误（文案形如 `rendering.parameters.X must be a finite number in [min, max].`）。

下表同时给出各材质的缺省值。`floatingtoolbar` 列是 `glassStyle = floatingtoolbar` 时的缺省值；"跟随"表示缺省值等于同组的 `surfaceOpacity`；"—"表示该参数对相应材质不适用。

| 参数 | 类型 | 取值域 | liquid_glass | floatingtoolbar | cyber_punk |
| --- | --- | --- | --- | --- | --- |
| `liveBackground` | bool | 必须存在（`liquid_glass`）；`cyber_punk` 禁止为 `true` | — | — | — |
| `cornerRadius` | number | `0`–`64` | `26` | `30` | — |
| `thickness` | number | `0`–`8` | `1` | `8` | — |
| `refraction` | number | `0`–`8` | `1.6` | `8` | — |
| `blurRadius` | number | `0`–`32` | `8` | `8` | — |
| `clearCenterRatio` | number | `0`–`1` | `0` | `0` | — |
| `surfaceOpacity` | number | `0`–`1` | `0.055` | `0.055` | `1` |
| `highlight` | number | `0`–`1` | `1` | `0.72` | `1` |
| `counterHighlight` | number | `0`–`1` | `1` | `1` | `1` |
| `shadow` | number | `0`–`1` | `0.34` | `0.115` | `0.34` |
| `shadowRadius` | number | `0`–`64` | `0` | `30` | — |
| `fogStrength` | number | `0`–`1` | `0.42` | `0.16` | — |
| `tintStrength` | number | `0`–`1` | 跟随 `surfaceOpacity` | `0.038` | — |
| `glassStyle` | string | `standard` / `floatingtoolbar` | `standard` | — | — |
| `primaryCut` | number | `0`–`64` | `0` | `0` | `14` |
| `secondaryCut` | number | `0`–`32` | `0` | `0` | `9` |
| `outlineWidth` | number | `0`–`8` | `0` | `0` | `1` |
| `glow` | number | `0`–`100` | `0` | `0` | `12` |
| `ambientRatio` | number | `0`–`1` | `0` | `0` | `0.12` |
| `panelOpacity` | number | `0`–`1` | `1` | `1` | `0.965` |

`cyber_punk` 下玻璃族参数（`cornerRadius`、`thickness`、`refraction`、`blurRadius`、`clearCenterRatio`、`fogStrength`、`tintStrength`、`shadowRadius`）固定为 `0`；`legacy` 材质的所有材质参数为中性值，且动效关闭。

### 4.3.1 `visualPreset`：命名 GPU 视觉预设

`rendering.parameters.visualPreset` 是可选的字符串扩展参数。**键缺失时完全跳过预设校验**，旧主题和无预设 GPU 主题继续使用原材质算法；显式 `default` 只允许受支持的 GPU 材质，并等价于原默认外观。

| 取值 | 要求材质 | 视觉语义 | 固定输入 |
|---|---|---|---|
| `default` | `liquid_glass` / `cyber_punk` | 原材质默认外观；不得产生画面差异 | 由材质和全局策略决定 |
| `nebulaAurora` | `liquid_glass` | 深靛星云、青紫极光、稀疏星点与冷色玻璃分组框 | 必须为 `false`，且禁止 `floatingToolbar` |
| `nixieBrass` | `cyber_punk` | 暖白正文、黄铜拉丝分组框、横向铆钉板与橙色热光 | 必须为 `false` |
| `crtPhosphor` | `cyber_punk` | P1 绿磷光、固定 3 px 扫描线、终端框 | 必须为 `false` |

命名预设继续复用 `liquid_glass` / `cyber_punk` 材质路由，不新增 `MaterialKind`。预设从主题 `font.*` 与 `color.*` 构造前景样式；`default` 继续使用内置默认前景记录。路由和 GPU 宿主对三种命名预设都必须覆盖全局实时背景请求，不能创建桌面捕获会话。

### 4.4 `glassStyle` 语义

`glassStyle` 取 `standard` 或 `floatingtoolbar`（大小写不敏感，写成 `floatingToolbar` 等价）：

- `standard`：标准玻璃卡片。圆角按逻辑半径换算为物理像素。
- `floatingtoolbar`：悬浮工具栏玻璃。圆角按工具栏自身尺寸换算并夹到 `min(宽, 高) / 2`；`thickness`、`refraction`、`blurRadius`、`shadowRadius` 额外按缩放系数放大，以匹配工具栏的视觉厚度。

非字符串或未知取值在运行时回落为 `standard`；在 `Validate` 中属于非法取值（错误文案 `rendering.parameters.glassStyle must be one of: standard, floatingtoolbar.`）。

### 4.5 `motion`：动效参数

| 参数 | 类型 | 取值域 | liquid_glass 缺省 | cyber_punk 缺省 |
| --- | --- | --- | --- | --- |
| `motionEnabled` | bool | 必须为布尔 | `true` | `true` |
| `targetFps` | number（整数） | `1`–`240` | `30` | `30` |
| `stateTransitionMs` | number | `0`–`5000` | `180` | `140` |
| `scannerPeriodMs` | number | `0`–`60000` | `0` | `2800` |

`motion` 段缺失即全部采用缺省值。非法取值分别报 `rendering.motion.X must be an integer in [min, max].` 与 `rendering.motion.motionEnabled must be a boolean.`。

### 4.6 `fallback` 与 `fallbackStyle`

`fallback` 声明渲染能力不足时的回退目标，当前只允许 `{"backend":"legacy","material":"legacy"}`。

`fallbackStyle` 是**兄弟块**（不是 `fallback` 的子对象），声明内置回退外观使用的六个配色槽：

| 字段 | 类型 | 语义 |
| --- | --- | --- |
| `background` | string（颜色） | 背景色。 |
| `panelBackground` | string（颜色） | 面板底色。 |
| `border` | string（颜色） | 描边色。 |
| `text` | string（颜色） | 正文色。 |
| `mutedText` | string（颜色） | 次要文字色。 |
| `accent` | string（颜色） | 标题强调色。 |

解析规则：

- `fallbackStyle` 为 `null` ⇒ 未声明该块。
- 整块不是对象 ⇒ 整块被忽略。
- 已知键只接受字符串；非字符串（数字、对象、数组、布尔）⇒ 该键被忽略。
- 未知键原样保留为扩展数据。

⇒ `fallbackStyle` 的非法取值**只产生诊断码 `gpu_fallback_style_invalid`**，不进入致命错误列表，因此不会让整份主题被判为渲染配置无效。

该诊断码由主程序侧的渲染配置校验产出；面向命令行与市场校验的共享编译配置不产出此诊断（但同样不会把它升级为致命错误）。

### 4.7 运行时归一化与非法值回退

材质参数在进入渲染前会被再归一化一次（与 4.2 的致命校验是两级不同口径）：

| 情况 | 运行时行为 |
| --- | --- |
| 参数缺失 | 采用材质缺省值。 |
| 参数不是数值或不是有限数 | 采用材质缺省值。 |
| 数值超出取值域 | 夹取到区间边界。 |
| `glassStyle` 未知 | 回落到 `standard`。 |

⇒ 通过致命校验的 `rendering` 段，其材质参数最终一定落在取值域内。

### 4.8 渲染路线裁决

主题只声明"期望的路线"，实际路线由 `RenderingRoutePolicy` 裁决：

| 条件 | 结果路线 | 记录的回退码 |
| --- | --- | --- |
| `rendering` 段缺失 | 内置路径 | `rendering_configuration_absent` |
| `rendering` 校验失败 | 内置路径 | `rendering_configuration_invalid` |
| `backend = legacy` | 内置路径 | 无 |
| 安全模式 | 内置路径 | `safe_mode` |
| 用户未开启 GPU 效果 | 内置路径 | `gpu_effects_opt_in_required` |
| `material = cyber_punk` | GPU（固定输入） | 无 |
| `material = liquid_glass`，用户未开启实时背景 | GPU（固定输入） | 无 |
| `visualPreset = nebulaAurora` / `nixieBrass` / `crtPhosphor` | GPU（固定输入） | 全局实时背景开关被预设策略覆盖 |
| 开启实时背景但未接受屏幕捕获策略 | GPU（固定输入） | `wda_capture_policy_not_accepted` |
| 开启实时背景且策略已接受 | GPU（实时背景） | 无 |

⇒ **主题声明 `backend = gpu` 不等于启用 GPU 渲染**：GPU 效果与实时背景都需要用户在设置中独立开启。

---

## 5. 颜色格式与解析

### 5.1 支持的写法

| 写法 | 形式 | 说明 |
| --- | --- | --- |
| 十六进制短式 | `#RRGGBB` | 6 位，alpha 隐含为 `FF`。 |
| 十六进制长式 | `#AARRGGBB` | 8 位，**alpha 在最前两位**。 |
| 函数式 | `rgba(r, g, b, a)` | `r`/`g`/`b` 为 `0`–`255` 整数，`a` 为 `0`–`1` 实数。 |
| 命名色 | 颜色名 | 使用系统已知的命名色（含 `transparent`）。 |

解析前的空白会被去除。空字符串与纯空白按"全透明"处理（合法结果，用于可选颜色槽）。

### 5.2 解析规则

- 以 `#` 开头：长度必须为 6 或 8，否则报错「十六进制颜色长度必须是 6 或 8 位…」；含非十六进制字符则报错。
- 以 `rgba(` 开头：必须恰好 4 段。`r`/`g`/`b` 必须是 `0`–`255` 的整数，`a` 必须是 `0`–`1` 的实数；非数字、`NaN`、无穷或越界都报错。alpha 字节由 `a × 255` 四舍五入（远离零方向）后夹取到 `0`–`255` 得到。
- 其余：按命名色查找，系统已知色名直接采用；未知名称报错「未知命名色：…」。

数值一律按不变文化（InvariantCulture）解析，不受运行环境区域设置影响。

### 5.3 非法颜色的行为

- 运行时解析单个颜色失败时，结果为**全透明**（不抛异常），保证界面不会因为一处颜色写错而中断。
- 需要报告颜色问题的入口是校验接口：它对已知颜色字段逐个检查，返回"字段路径: 诊断"形式的列表。

### 5.4 颜色校验覆盖范围

校验只覆盖**已声明了颜色值的已知字段**：

- `color` 段：`background`、`textTitle`、`textGroup`、`textPrimary`、`textSecondary`、`valueSafe`、`valueWarn`、`valueCrit`、`barBackground`、`barLow`、`barMid`、`barHigh`、`groupBackground`、`border`、`shadow`。
- `backdrop` 段：`tint`、`gradientStart`、`gradientEnd`。
- `rendering.fallbackStyle` 段：`background`、`panelBackground`、`border`、`text`、`mutedText`、`accent`。

字段缺失不算错误；未知扩展字段不按颜色解析。

---

## 6. 缩放规则

### 6.1 缩放的两个系数

| 系数 | 来源 | 含义 |
| --- | --- | --- |
| `dpiScale` | 窗口 `Graphics.DpiX / 96` | 系统 DPI 比例。 |
| `userScale` | 设置项 `UIScale` | 用户选择的界面缩放倍率。 |

### 6.2 布局缩放

布局几何按 `dpiScale × userScale` 缩放，参与缩放的字段：

`width`、`rowHeight`、`padding`、`cornerRadius`、`groupRadius`、`groupPadding`、`groupSpacing`、`groupBottom`、`groupTitleOffset`、`itemGap`。

缩放守卫：系数 ≤ 0 或与 1 的差小于 `0.01` 时不缩放。

**不参与布局缩放**的字段：`frameWidth`、`frameLength`、`scanline`、`glow`、`grid`、`barStyle`、`frameStyle`。它们的物理尺寸由外观选项按统一系数单独换算（见 6.4），避免同一尺寸被缩放两次。

### 6.3 字体缩放

字号只按 `userScale` 缩放，**不乘 DPI 比例**。

⇒ 因此**字体与布局是各自独立缩放的**：布局尺寸随 DPI 变化，字号只随用户倍率变化。

字体缩放时先释放已创建的字体对象再重建，避免句柄泄漏。

### 6.4 装饰尺寸的物理像素换算

`frameWidth` 与 `frameLength` 是逻辑尺寸，换算规则为：

1. 先按取值域夹取：`frameWidth` → `0`–`8`，`frameLength` → `0`–`64`，`scanline`/`glow`/`grid` → `0`–`100`；未知 `frameStyle` 归为 `0`，未知 `barStyle` 归为 `0`。
2. 再乘 `dpiScale × userScale` 得到物理像素。
3. 最后按客户区尺寸二次夹紧：`frameWidthPx` 不超过客户区宽度的一半，`frameLengthPx` 不超过客户区高度的一半。

⇒ 装饰尺寸在任何缩放倍率下都不会超出窗口客户区。

### 6.5 调用约定

主题加载完成后必须调用一次 `Theme.Scale(dpiScale, userScale)`：它把 `layout.layoutScale` 置为 `dpiScale × userScale`、对布局几何缩放、并按 `userScale` 重建四类字体。

外观选项（`ThemeAppearanceOptions`）**必须使用同一对系数**，否则装饰尺寸与布局尺寸的比例会不一致。

在竖排布局下，主题中的 `layout.width` 不参与窗体宽度计算：窗体宽度由设置项 `PanelWidth` 与同一缩放系数决定。

---

## 7. 加载、优先级与同名处理

### 7.1 加载流程

1. 校验主题名（见 7.5）；非法名直接走失败回退。
2. 读取主题目录下的 `<名称>.json` 字节，计算 SHA-256 作为来源标识。
3. 反序列化（已知键大小写不敏感，允许尾随逗号），随后剔除与已知属性同名的扩展键。
4. 创建字体（失败时回落到系统标题字体）。
5. 主题生效，随后按第 6 章执行缩放。

### 7.2 失败回退

任何一步失败（文件不存在、JSON 非法、主题名非法、读取异常）时：

- 主题被替换为缺省主题（模型缺省值）；
- 加载结果被标记为"使用了缺省主题"，并记录失败原因；
- 加载结果（请求名、来源路径、来源摘要、是否成功、是否使用缺省、错误信息）保存在最近一次加载结果中，供诊断中心与状态行读取。

⇒ 主题加载失败不会导致程序无法启动，界面以缺省外观继续运行。

`IsGpuTheme(name)` 只读取文件中的 `rendering.backend`，不触发完整加载，也不改变当前主题；文件缺失、JSON 非法、缺少 `rendering` 或该字段不是字符串时一律返回 `false`。

### 7.3 优先级

由于三类主题共享同一个目录（1.2），**不存在目录级优先级**；生效规则是文件级唯一的：目录下每个文件名对应一份主题，磁盘上存在的那份即生效。

实际的优先级体现在三处：

| 场景 | 规则 |
| --- | --- |
| 程序升级 | 更新器**不得覆盖**主题目录下的任何文件 ⇒ 用户改过的主题、市场安装的主题优先于发布包携带的内置版本。 |
| 主题工作室保存 | 直接写回主题目录下的同名文件，覆盖该主题。 |
| 主题市场安装 | 目标文件已存在且不是本市场安装的（内置主题、手动放入的文件、其它来源的残留）时**拒绝安装**，提示先移除该文件；目标是本市场安装的、且安装后未被改动时允许更新覆盖；安装后被用户改动过则拒绝覆盖，需先卸载。 |

⇒ 同名主题不会"后加载者胜出"，而是**由写入规则决定谁有资格占用这个文件名**。

### 7.4 市场安装的事务与校验

市场安装的主题必须满足：

- 下载路径在允许的前缀白名单内；
- 载荷 SHA-256 与市场索引声明一致；
- 通过市场主题校验（第 9 章）；
- 安装采用事务方式落盘：暂存 → 原子替换 → 写安装记录 → 清理暂存；中断的安装在下次启动时会被恢复处理。

卸载一个当前正在使用的主题时，必须先切换到其它主题。

### 7.5 主题名合法性

主题名（文件名去扩展名）必须满足：

- 非空，且不含首尾空白；
- 不以 `.` 结尾；
- 不含文件系统非法字符，也不含路径分隔符；
- 不是 `.` 或 `..`；
- 不是 Windows 保留名（`CON`、`PRN`、`AUX`、`NUL`、`COM1`–`COM9`、`LPT1`–`LPT9`）。

违反任一条时，主题名非法，对应操作以参数错误终止。

---

## 8. 导出契约

### 8.1 JSON 导出的语义等价

导出 JSON 时：

- 导出载荷与工作室会话当前修订**同源**：会话载荷由源文件字节与未提交的路径补丁构建，导出结果与该载荷逐字节一致。
- 未知扩展键被保留；模型缺省值**不会被物化**（源文件里没有的 `rendering`、`backdrop` 等块不会被写出）。
- 导出不改变会话状态，也不写入源主题文件。
- 导出的文件是符合本规格的主题文件：把它以合法文件名放入主题目录后，加载器与工作室都能读取，且与导出时的会话语义等价（同一套反序列化口径、同样的未知键保留与缺省不物化规则）。
- 导出前只做结构校验与 `rendering` 段校验；校验问题以诊断形式随导出结果返回，不改变导出本身是否成功。

导出结果包含：成功标志、错误信息、写入路径、写入文件字节的 SHA-256 与字节数、诊断列表。SHA-256 与字节数都基于**实际写入磁盘的字节**计算。

### 8.2 原子写要求

主题文件的所有写入（工作室保存、导出、市场安装提交）必须走同一个原子写核心：

1. 在目标同目录写临时文件，并用 `WriteThrough` 选项 + 落盘刷新确保数据真正写盘；
2. 目标不存在 ⇒ 原子移动就位；目标存在 ⇒ 使用同卷原子替换（保留 ACL）；替换能力不可用时退回到带覆盖的移动；
3. **任何情况下都不得先删除目标文件**；
4. 失败时删除临时文件并原样抛出异常。

⇒ 写入失败时目标文件内容保持不变，调用方不会收到"保存成功"的结论。

另存为新主题走同目录暂存文件 + 不覆盖落位：目标文件已存在时该操作失败，不产生覆盖。

临时文件使用 `.litemonitor-tmp` 后缀，不会被当作主题枚举；目录中残留超过 10 分钟的临时文件会被清理。

### 8.3 效果图与投稿资料

- 效果图必须携带出图路线标记（内置路线 / GPU 路线 / 回退路线）。GPU 不可用时，输出失败或输出**明确标记为回退**的兼容外观图，**绝不**把内置路线渲染结果标记为 GPU 路线。
- 经典路线按画幅分派到同源渲染器，使用固定输入与固定画幅，结果可复现，不抓取真实桌面内容。
- 主题为 GPU 路线但实际以回退路线出图时，渲染的是由 `fallbackStyle` 派生的回退外观。
- 投稿资料包含 `theme.json`、`submission.json`、`README.txt`、`preview.png`。`submission.json` 中的 `route` 字段记录主题**声明的**路线，不改写；实际出图路线由预览图的路线与回退标记承载。
- 颜色校验不通过时，投稿资料生成被阻断。

---

## 9. 校验规则与错误码

主题的结构与市场合规校验集中在 `ThemeMarketValidator`；命令行工具 `theme-validate` 与市场安装共用同一套规则与错误码。

### 9.1 校验顺序与规则

| 序号 | 检查 | 失败错误码 |
| --- | --- | --- |
| 1 | 文件大小 ≥ 1 字节 | `theme_empty` |
| 2 | 文件大小 ≤ 262144 字节（256 KiB） | `theme_too_large` |
| 3 | 声明大小（若提供）等于实际字节数 | `theme_size_mismatch` |
| 4 | 严格 UTF-8 解码成功（接受前导 BOM） | `theme_utf8_invalid` |
| 5 | JSON 解析成功（无注释、无尾随逗号、深度 ≤ 64） | `theme_json_invalid` |
| 6 | 根节点为对象 | `theme_root_not_object` |
| 7 | 属性名与字符串值都不含 `http://` / `https://` | `theme_remote_reference` |
| 8 | 已知颜色字段全部可解析 | `theme_color_invalid` |
| 9 | 索引声明的路线为 `gpu` 或 `legacy` | `theme_route_invalid` |
| 10 | 索引声明的材质为 `liquid_glass`/`cyber_punk`/`legacy` | `theme_material_invalid` |
| 11 | `rendering` 段为对象或缺省 | `theme_rendering_invalid` |
| 12 | `rendering.backend` 存在且为字符串 | `theme_rendering_backend_missing` |
| 13 | `rendering.backend` 与索引路线一致 | `theme_route_mismatch` |
| 14 | `rendering.material` 与索引材质一致 | `theme_material_mismatch` |
| 15 | `rendering` 段通过渲染配置校验 | `theme_rendering_invalid` |
| 16 | 文件可读 | `theme_read_failed` |

关于第 13 条：`rendering` 段缺省时按 `legacy` 解释，与索引声明的路线不一致时报 `theme_route_mismatch`。

全部通过时错误码为 `ok`。

颜色检查（`theme_color_invalid`）只在主程序侧参与，命令行与市场校验器共用的共享编译配置不执行该检查，其余规则一致。

### 9.2 错误码表

| 错误码 | 含义 |
| --- | --- |
| `ok` | 校验通过。 |
| `theme_empty` | 文件为空。 |
| `theme_too_large` | 超过 256 KiB 上限。 |
| `theme_size_mismatch` | 索引声明大小与实际字节数不符。 |
| `theme_utf8_invalid` | 不是合法 UTF-8。 |
| `theme_json_invalid` | JSON 语法非法。 |
| `theme_root_not_object` | 根节点不是对象。 |
| `theme_remote_reference` | 含远程引用。 |
| `theme_color_invalid` | 颜色取值非法。 |
| `theme_route_invalid` | 索引路线非法。 |
| `theme_material_invalid` | 索引材质非法。 |
| `theme_rendering_invalid` | `rendering` 段非法或未通过渲染配置校验。 |
| `theme_rendering_backend_missing` | 缺少 `rendering.backend`。 |
| `theme_route_mismatch` | 主题声明路线与索引不一致。 |
| `theme_material_mismatch` | 主题声明材质与索引不一致。 |
| `theme_read_failed` | 文件读取失败。 |

### 9.3 命令行校验工具

命令行只提供一条命令：

```
theme-validate --path=<file> [--route=gpu|legacy] [--material=liquid_glass|cyber_punk|legacy] [--declared-size=<bytes>]
```

- `--path` 必填；`--route` 默认 `gpu`；`--material` 默认 `liquid_glass`；`--declared-size` 可选。
- 标准输出恰好一个 JSON 对象，字段为：`ok`、`command`、`path`、`route`、`material`、`error_code`、`message`。
- 标准错误输出一行人类可读摘要。

| 退出码 | 含义 |
| --- | --- |
| `0` | 校验通过。 |
| `1` | 校验失败。 |
| `2` | 环境不具备（文件不存在或不可读）。 |
| `64` | 参数错误。 |
| `70` | 未处理异常。 |

---

## 10. 已知限制

以下是当前客观存在的限制：

1. **顶层 `version` 尚未形成校验与迁移合同。** 该字段被记录并透出到诊断信息，但不参与任何校验，也没有按版本迁移的机制；不同主题文件里的取值可以不同，加载器一律按同一套字段语义解释。
2. **`font.scale` 不参与字号计算。** 字号只由 `font` 段内的四个字号字段与用户缩放倍率决定；文件中出现 `font.scale` 时按未知扩展键保留。
3. **`layout.width` 与 `layout.layoutScale` 不生效。** 面板宽度由设置项 `PanelWidth` 决定，布局缩放系数由运行时写入。
4. **主题目录唯一且位于程序目录。** 内置主题、用户主题与市场主题平铺在同一目录；自动更新不刷新该目录下的文件，需要新版内置资源时须全新安装。
5. **运行时颜色解析失败回退为透明。** 运行时路径不因单个颜色非法而中断；需要诊断颜色问题时必须使用校验接口。
6. **`fallbackStyle` 的非法取值只产生诊断码。** 它不会让主题失效，也不会阻止主题被使用。
7. **数值参数存在两级口径。** 渲染配置校验对越界值报错并导致路线回退，而运行时归一化对越界值执行夹取；两者对同一字段的处理结果不同。主题作者应保证数值始终落在取值域内。
