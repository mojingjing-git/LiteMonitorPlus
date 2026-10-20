# A12 机能风主题与视觉样本（CyberPunk_Mech）

> 所属计划：`docs/superpowers/plans/2026-10-20-appearance-agent-execution.md`（v4）§3.1 / §3.3 / §5 的 A12 行 / §7
> 基线依据：`docs/superpowers/plans/2026-10-20-appearance-a00-baseline.md`
> 本文件只交付**参数说明**与**可判定判据**（参数组合 + 夹具），**不产出任何示意图 / PS 效果图**；
> 最终视觉证据由 A14 的真实渲染产生。

---

## 1. 交付物清单

| 类别 | 路径 | 说明 |
| --- | --- | --- |
| 主题 | `resources/themes/CyberPunk_Mech.json` | 唯一新增的**用户可见**主题（暗底 · 机能风 · Version 4） |
| 夹具 | `tests/AppearanceThemeCheck/fixtures/CyberPunk_Mech_Light.json` | 亮底夹具（只用于校验，不进用户主题目录） |
| 夹具 | `tests/AppearanceThemeCheck/fixtures/CyberPunk_Mech_Complex.json` | 复杂背景夹具（渐变 + 染色 + 噪点 + 边缘辉光） |
| 校验 | `tests/AppearanceThemeCheck/` | 独立 console 工程：加载 → 归一化 → 往返保存 → 对比度判据 |
| 文档 | 本文件 | 参数逐项说明 + 夹具说明 + 横竖屏/多卡注意事项 |

**为什么夹具不放进 `resources/themes/`**：放进主题目录会被 A04 探针的"运行时动态发现主题目录"逻辑自动纳入，
也会出现在用户的换肤下拉菜单里。夹具是**校验输入**，不是**发行物**，因此放在校验工程内，由校验程序按路径加载。

---

## 2. 主题文件结构总览

JSON 键名**大小写不敏感**（`src/Core/ThemeManager.cs → ThemeManager → CreateSerializerOptions` 设置
`PropertyNameCaseInsensitive = true`）。本主题沿用现有 10 个旧主题的 camelCase 写法。

```
{
  "version": 4,                 // 主题代次（不参与任何行为门控，仅诊断透传）
  "name": "CyberPunk_Mech",     // 主题名（显示用）
  "layout":  { ... },           // 布局与机能风开关
  "font":    { ... },           // 字体
  "color":   { ... },           // 颜色
  "backdrop":{ ... }            // 背景段（缺失 = 不启用任何背景效果）
}
```

| 段 | 缺失时的行为 |
| --- | --- |
| `layout` / `font` / `color` | 取模型默认值（不会崩，但视觉是"默认灰"） |
| `thresholds` | 取模型默认值：Load 65/85、Temp 50/70、Vram 65/85、Mem 65/85、NetKBps 2097152/8388608 |
| `backdrop` | `Theme.Backdrop` 为 null → **完全不启用背景效果**，走旧的实心背景路径 |

**未知键会被保留**：每层都有 `[JsonExtensionData]` 槽位，JSON 里模型不认识的键会被原样保留并在保存时写回
（键名大小写保持不变）。本主题用到的 `font.scale` 就属于这一类（`FontConfig` 没有 `Scale` 属性，
它只是历史主题遗留的扩展键，不改变字号算法）。

---

## 3. `layout` 段参数

### 3.1 布局尺寸（沿用旧字段）

单位一律是 **96 DPI 逻辑像素**（`LayoutConfig → Scale` 会按 `dpiScale × userScale` 缩放它们）。

| 键 | 取值域 | 缺省 | 视觉含义 | 怎么调 |
| --- | --- | --- | --- | --- |
| `rowHeight` | ≥ 0 的整数 | 40 | 每个监控项的行高；也是主标题的占位高度 | 想要更紧凑的列表就调小；调小会让数值行挤在一起 |
| `padding` | ≥ 0 的整数 | 12 | 画布外边距。竖屏组块宽 = `PanelWidth − padding × 2` | 组块太窄就调小；太宽会让"组块面 vs 窗口底"的可分性变差 |
| `cornerRadius` | ≥ 0 的整数 | 12 | 窗体圆角（`src/UI/Helpers/MainFormWinHelper.cs → MainFormWinHelper → ApplyRoundedCorners`）。**0 = 直角**：Win10 走 `Region = null`，Win11 的 DWM 圆角属 A09/A13 接线范围 | 机能风想要硬边就写 0 |
| `groupRadius` | ≥ 0 的整数 | 10 | 组块圆角（`src/UI/UIRenderer.cs → UIRenderer → DrawGroupBackground`） | 想要直角面板就写 0~4 |
| `groupPadding` | ≥ 0 的整数 | 8 | 组块内边距。**`NET` / `DISK` / `DATA` 是双列组，内边距会被折半**（`src/UI/UILayout.cs → UILayout → Build`） | 组块内容发闷就调大 |
| `groupSpacing` | ≥ 0 的整数 | 30 | 组块之间的垂直间距 | 调小要同时看组标题是否与上一个组块相撞（见 §10） |
| `groupBottom` | ≥ 0 的整数 | 0 | 组块额外底部留白（叠加在 `groupSpacing` 之上） | 想让最后一个组离窗口底远一点就调大 |
| `itemGap` | ≥ 0 的整数 | 6 | 同一组内监控项之间的垂直间距 | 组内太挤就调大 |
| `groupTitleOffset` | 整数 | 6 | 组标题相对组块上沿的**上移量**（标题画在组块**上方**的背景面上） | 标题与组块贴太近就调大 |

> `layout.width` 与 `layout.layoutScale` **不写**：竖屏时 `src/UI/UIController.cs → UIController → ApplyTheme`
> 会用 `Settings.PanelWidth`（默认 240，设置页可选 180~1200）覆盖 `Layout.Width`，主题里写 `width` 不生效。

### 3.2 机能风新增开关

这 7 个字段是 A05/A08 引入的新能力。**缺键与 0 等价**（0 = 关闭该效果 / 走旧路径）。

| 键 | 取值域 | 缺省 | 视觉含义 | 怎么调 |
| --- | --- | --- | --- | --- |
| `frameStyle` | 枚举 | 0 | 外框样式：**0** = 旧路径，**1** = 切角，**2** = 圆角描边，**3** = 静态全息边框。未知值按 0 处理（关闭） | 机能风用 1；写 1 才会消费 `color.border` |
| `frameWidth` | 0 ~ 8 | 0 | 外框描边宽度（96 DPI 逻辑值）。物理像素 = `round(frameWidth × 合成比例)`，再按客户区二次夹紧 | 1~2 纤细，3~4 厚重，≥5 在小窗口会很笨重 |
| `frameLength` | 0 ~ 64 | 0 | 切角边长（96 DPI 逻辑值）。**仅 `frameStyle = 1`（或 3）有意义** | 8~16 明显但不夸张；≥ 32 会让窗口像"被削掉一块" |
| `scanline` | 0 ~ 100 | 0 | 扫描线强度 | 10~20 隐约；30~50 明显；> 60 会干扰读数 |
| `glow` | 0 ~ 100 | 0 | 外框辉光强度。**只有 `glow > 0` 才会消费 `color.shadow`** | 20~40 内敛；50~70 张扬；> 80 在暗底上会糊 |
| `grid` | 0 ~ 100 | 0 | 网格强度 | 8~20 微微可见；> 30 与扫描线叠加会显得脏 |
| `barStyle` | 枚举 | 0 | 进度条样式：**0** = 旧条，**1** = 渐变，**2** = 纹理。未知值按 0 处理 | 机能风用 1 |

### 3.3 归一化与夹紧规则（权威实现：`src/Core/ThemeAppearanceOptions.cs → ThemeAppearanceOptions → Create`）

- **归一化只作用于运行时副本，绝不回写用户 JSON**：`Create` 之前后的原 `Theme` 对象完全一致。
- 逻辑值域：`frameWidth` 夹到 0~8、`frameLength` 夹到 0~64、强度夹到 0~100、`opacity` 夹到 0~1、
  `blur` 夹到 0~32、`fps` 夹到 1~6；未知 `frameStyle` / `barStyle` / 背景模式回落为"关闭"。
- **新尺寸只缩放一次**：`frameWidth` / `frameLength` 在 `LayoutConfig → Scale` 里**故意不缩放**，
  由 `Create` 换算成物理像素 `FrameWidthPx` / `FrameLengthPx`：
  `FrameWidthPx = round(frameWidth × 合成比例)`，`FrameLengthPx = round(frameLength × 合成比例)`，
  合成比例 = `dpiScale × userScale`。
- **再按客户区二次夹紧**：`FrameWidthPx ≤ 客户区宽 / 2`，`FrameLengthPx ≤ 客户区高 / 2`。
  这属于**正常行为**（窄窗、小窗时切角自动变小），不是 bug。
  渲染侧还有一层几何保护：`src/UI/Decor/DecorGeometry.cs → DecorGeometry → ClampCornerLengthPx`
  把切角边长夹到 `min(宽, 高) / 2`（正好等于一半时再收 1 像素），`ClampFrameWidthPx` 把描边宽度夹到 `min(宽, 高) / 2`。
- **消费颜色的前置条件**（必须先判开关再取色）：
  - `FrameEnabled = FrameStyle != 0` → 只有为真才允许消费 `BorderColor`
    （`src/UI/Decor/DecorRenderer.cs → DecorRenderer → ResolveFrameColor`）；
  - `GlowEnabled = Glow > 0` → 只有为真才允许消费 `ShadowColor`
    （同文件 `ResolveGlowColor`）。
  这两条保证"旧主题里已经存在 `border` / `shadow` 键，但不会因此自动冒出描边 / 辉光"。

### 3.4 强度 → 实际 alpha 的换算公式

调参时直接用这几个公式反推"想要的浓度"，不必反复试：

| 效果 | alpha 公式 | 取色 |
| --- | --- | --- |
| 扫描线 | `round(80 × scanline/100 × BorderColor.A/255)` | 外框色（外框未开启时退回次级文字色，再退回白色） |
| 网格 | `round(60 × grid/100 × BorderColor.A/255)` | 同上 |
| 外框辉光 | `round(200 × glow/100 × ShadowColor.A/255)` | `color.shadow` |
| 背景噪点 | `round(255 × noise/100 × 0.35)` | 黑白噪点（最坏情况取白像素） |
| 背景边缘辉光 | `round(255 × edgeGlow/100 × TintColor.A/255)` | `backdrop.tint`；带宽 = `round(24 × edgeGlow/100 × 合成比例)` 物理像素 |

本主题的实测值：`scanline = 30` → alpha 24（≈9.4%）；`grid = 20` → alpha 12（≈4.7%）；
`glow = 55` 且 `shadow` 的 A=89 → alpha 38（≈15%）。

> 扫描线与网格**只画在"客户区减去不透明组块"的区域**
> （`src/UI/Decor/DecorRenderer.cs → DecorRenderer → BuildGroupExclusion`，要求 `GroupBackground` 不透明）。
> 因此正文、数值、进度条所在的组块面**不受底纹干扰**；落在背景面上的主标题与组标题则会。

---

## 4. `color` 段参数

| 键 | 视觉含义 | 画在哪一层 | 消费条件 |
| --- | --- | --- | --- |
| `background` | 窗口底色（背景底板） | 最底层 | 总是消费；同时是 `backdrop.tint` / `gradientStart` 的缺省来源 |
| `groupBackground` | 组块底色 | 组块层 | 总是消费；**建议不透明**（A=255），否则底纹排除逻辑失效、组块会与背景糊在一起 |
| `textTitle` | 主标题 | **背景/渐变面** | 总是消费（`src/UI/UIRenderer.cs → UIRenderer → DrawMainTitle`） |
| `textGroup` | 组标题 | **背景/渐变面**（画在组块上方） | 总是消费（同文件 `DrawGroupBackground`） |
| `textPrimary` | 正文（项目名） | 组块面 | 总是消费（同文件 `DrawStandardItem`） |
| `textSecondary` | 次级文字 | 组块面 / Web 端 `--text-sub` | 总是消费；另外在**外框未开启**时会被当作描边回退色（`DecorRenderer → ResolvePatternBase`） |
| `valueSafe` / `valueWarn` / `valueCrit` | 正常 / 警告 / 严重时的数值 | 组块面 | 总是消费；切换阈值由 `thresholds` 决定 |
| `barBackground` | 进度条槽 | 组块面 | 总是消费 |
| `barLow` / `barMid` / `barHigh` | 进度条三段色 | 组块面 | 总是消费 |
| `border` | 外框描边色 | 窗口最外圈 | **仅 `frameStyle != 0` 时消费**（`DecorRenderer → ResolveFrameColor`） |
| `shadow` | 外框辉光色 | 外框外侧 | **仅 `glow > 0` 时消费**（`DecorRenderer → ResolveGlowColor`） |

**颜色格式**（`src/Core/ThemeManager.cs → ThemeManager → ParseColor`）：
`#RRGGBB`、`#AARRGGBB`、`rgba(r,g,b,a)`（a ∈ [0,1]，越界回退全透明）、或 .NET 颜色名。
非法值一律回退成**全透明**，不会抛异常 —— 所以写错颜色不会报错，只会"那个颜色不出现"。

---

## 5. `backdrop` 段参数

背景段的权威实现：`src/System/Backdrop/BackdropModeResolver.cs → BackdropModeResolver`、
`src/System/Backdrop/BackdropRenderer.cs → BackdropRenderer → Render`。

绘制顺序（`BackdropRenderer → Render`）：**底板（`background`，不透明） → 渐变 → 染色层 → 噪点 → 边缘辉光**。

| 键 | 取值域 | 缺省 | 视觉含义 | 怎么调 |
| --- | --- | --- | --- | --- |
| `mode` | `"solid"` / `"gradient"` / `"glass"` | `"solid"` | `solid` = 旧背景路径（零绘制零分配）；`gradient` = 静态渐变；`glass` = 请求实时材质（后端不可用时降级） | 想要稳定的赛博底就 `gradient` |
| `opacity` | 0 ~ 1 | **0.75** | 染色层的额外 alpha 系数，与 `tint` 自身 alpha **相乘** | **见 §7，必读** |
| `blur` | 0 ~ 32（逻辑像素） | 12 | 模糊半径，**只对实时捕获后端有效**；`gradient` 模式下无意义 | 用 `gradient` 时写 0 表明"不依赖捕获后端" |
| `tint` | 颜色 | 缺省取 `background` | 染色色；同时是**边缘辉光的取色** | 写不透明色 = 一旦 `opacity>0` 就会强烈染色 |
| `gradientStart` | 颜色 | 缺省取 `background` | 渐变起始色 | 两端都应是**不透明**色，否则渐变不可用 |
| `gradientEnd` | 颜色 | 缺省取 `groupBackground` | 渐变结束色 | 同上 |
| `gradientAngle` | 度 | 90 | 渐变角度 | 135 是常见的"斜切"方向；0 = 从左到右 |
| `noise` | 0 ~ 100 | 0 | 背景噪点强度 | 8~15 轻微颗粒；20~30 明显；> 40 会在浅色底上发灰 |
| `edgeGlow` | 0 ~ 100 | 0 | 背景边缘辉光强度（只在窗口最外一圈） | 20~40 轻微；> 60 会干扰贴边的文字 |
| `fps` | 1 ~ 6 | 2 | 捕获帧率，**只对实时捕获后端有意义** | `gradient` 模式用不到 |

### 5.1 降级链

`glass → gradient → solid`（`BackdropModeResolver → Resolve`）：

- `glass` 拿到底图 → 画实时材质；
- `glass` 拿不到底图且**渐变可用** → 降级为 `gradient`；
- 渐变**不可用** → 降级为 `solid`（旧背景路径）。

**渐变是否可用只看渐变两端颜色的 alpha**（`BackdropModeResolver → IsGradientUsable`：
两端至少一端 A > 0 即可），**不看 tint**。所以把 `tint` 设成透明、或把 `opacity` 设成 0，
**都不会破坏降级链**，只影响有没有染色层。

---

## 6. 关键规则：渐变 / 玻璃主题必须显式声明 `opacity`

### 6.1 事实

- 染色层 alpha = `round(TintColor.A × Opacity)`（`BackdropModeResolver → ComputeTintAlpha`）。
- `Tint` **缺省取 `background`**（不透明，A = 255）；`Opacity` **缺省 0.75**。
- 因此一个只写了 `"mode": "gradient"` 的主题，实际有效染色 alpha = `round(255 × 0.75) = 191`，
  也就是**约 75% 的不透明底色盖在渐变上**——渐变基本看不见。

### 6.2 规则

| 规则 | 内容 |
| --- | --- |
| **R1** | `backdrop.mode` 不是 `solid` 时，JSON **必须显式写 `opacity`**（不能依赖缺省 0.75） |
| **R2** | `opacity = 0` ⇒ 不产生染色层，渐变完整可见（`tint` 此时仅供边缘辉光取色） |
| **R3** | `opacity > 0` ⇒ **必须显式写 `tint`**，并按 `期望有效 alpha = round(Tint.A × opacity)` 自行核算合成后的对比度 |

### 6.3 对照示例

| 目标 | 写法 | 有效染色 alpha | 效果 |
| --- | --- | --- | --- |
| **要渐变** | `"opacity": 0`（`tint` 可留可删） | 0 | 渐变 100% 可见。**本主题与亮底夹具都这样写** |
| **要轻染色** | `"opacity": 0.2`，`"tint": "rgba(0,229,255,1)"` | `round(255×0.2) = 51`（20%） | 渐变仍占 80%，只带一点色偏 |
| **要中染色** | `"opacity": 0.35`，`"tint": "rgba(255,46,151,0.25)"` | `round(64×0.35) = 22`（8.6%） | **复杂背景夹具的写法**：tint 自身只有 25% 不透明，再乘 0.35，实际染色很轻 |
| **要重染色** | `"opacity": 0.6`，`"tint": "rgba(255,46,151,1)"` | `round(255×0.6) = 153`（60%） | 渐变基本被盖住，接近纯色 |
| **透明 tint** | `"opacity": 1`，`"tint": "rgba(255,46,151,0)"` | `round(0×1) = 0` | 等价于不染色；**且不影响降级链**（`IsGradientUsable` 不看 tint） |

反推公式：`opacity = 期望有效 alpha ÷ (Tint.A ÷ 255)`。

---

## 7. 可读性判据

### 7.1 判据表

| 承载面 | 承载对象 | 判据 |
| --- | --- | --- |
| 背景承载面 | `textTitle`、`textGroup` | ≥ 4.5 : 1 |
| 组块承载面 | `textPrimary`、`textSecondary`、`valueSafe/Warn/Crit` | ≥ 4.5 : 1 |
| 进度条槽 | `barLow/Mid/High` | ≥ 3 : 1 |
| 背景承载面 | `border`（非文本图形） | ≥ 3 : 1 |
| 组块面 vs 窗口底 | 可分性 | ≥ 1.2 : 1 |

### 7.2 承载面模型（保守）

标题类文字画在背景 / 渐变面上，那一面的颜色不是单一值。本项目按**渲染管线的叠加顺序**复刻出**最坏像素**：

```
最坏背景承载面 =
    ① 渐变两端之一（对给定文字色更不利的那一端）
  → ② 染色层：alpha = round(Tint.A × Opacity)，色 = Tint
  → ③ 噪点层：alpha = round(255 × noise/100 × 0.35)，色 = 白（最亮噪点像素）
  → ④ 底纹层：扫描线 alpha = round(80 × scanline/100 × Border.A/255)，色 = 外框色
                网格   alpha = round(60 × grid/100 × Border.A/255)，色 = 外框色
  → ⑤ 边缘辉光层：alpha = round(255 × edgeGlow/100 × Tint.A/255)，色 = Tint
```

这个模型**刻意保守**：它假设所有图层在同一像素上完全重叠。真实渲染里底纹被排除在不透明组块之外、
边缘辉光只在窗口最外一圈，因此真实对比度只会**高于**模型值。

组块承载面 = `groupBackground` 本身（不透明，且底纹已被排除）。

实现见 `tests/AppearanceThemeCheck/ContrastModel.cs → ContrastModel`。

### 7.3 实测值（校验程序输出，`AppearanceThemeCheck.exe` 退出码 0）

**暗底主主题 `CyberPunk_Mech`** —— 最坏背景承载面 `#152F3F`，组块承载面 `#1A2230`，进度条槽 `#0E1520`

| 判据项 | 实测 | 结论 |
| --- | --- | --- |
| textTitle `#5CF3FF` vs 背景承载面 | **10.41** | 通过 |
| textGroup `#A6BED4` vs 背景承载面 | **7.24** | 通过 |
| textPrimary `#E9F1F8` vs 组块承载面 | **13.99** | 通过 |
| textSecondary `#A9BDD0` vs 组块承载面 | **8.27** | 通过 |
| valueSafe `#3AE08D` vs 组块承载面 | **9.30** | 通过 |
| valueWarn `#FFC24B` vs 组块承载面 | **9.94** | 通过 |
| valueCrit `#FF5C7A` vs 组块承载面 | **5.37** | 通过 |
| barLow `#5CF3FF` / barMid `#FFC24B` / barHigh `#FF5C7A` vs 进度条槽 | **13.71 / 11.40 / 6.16** | 通过 |
| border `#5CF3FF` vs 背景承载面 | **10.41** | 通过 |
| 组块面 `#1A2230` vs 窗口底 `#070A0F` | **1.242** | 通过 |

**亮底夹具 `CyberPunk_Mech_Light`** —— 最坏背景承载面 `#C2D6E2`，组块承载面 `#FFFFFF`，进度条槽 `#D3DCE6`

| 判据项 | 实测 | 结论 |
| --- | --- | --- |
| textTitle `#0B4A63` vs 背景承载面 | **6.44** | 通过 |
| textGroup `#33475B` vs 背景承载面 | **6.39** | 通过 |
| textPrimary `#16202B` vs 组块承载面 | **16.46** | 通过 |
| textSecondary `#43566A` vs 组块承载面 | **7.56** | 通过 |
| valueSafe `#0A6E3C` / valueWarn `#7A4E00` / valueCrit `#A8102C` vs 组块承载面 | **6.35 / 7.20 / 7.57** | 通过 |
| barLow `#0B6E8C` / barMid `#8A5500` / barHigh `#A8102C` vs 进度条槽 | **4.18 / 4.48 / 5.46** | 通过 |
| border `#1C6E8A` vs 背景承载面 | **3.84** | 通过 |
| 组块面 `#FFFFFF` vs 窗口底 `#E0E8F1` | **1.237** | 通过 |

**复杂背景夹具 `CyberPunk_Mech_Complex`** —— 最坏背景承载面 `#514568`，组块承载面 `#18202E`，进度条槽 `#0E1520`

| 判据项 | 实测 | 结论 |
| --- | --- | --- |
| textTitle `#7DF9FF` vs 背景承载面 | **7.02** | 通过 |
| textGroup `#B9D2E8` vs 背景承载面 | **5.60** | 通过 |
| textPrimary `#E9F1F8` vs 组块承载面 | **14.32** | 通过 |
| textSecondary `#A9BDD0` vs 组块承载面 | **8.47** | 通过 |
| valueSafe `#3AE08D` / valueWarn `#FFC24B` / valueCrit `#FF6B85` vs 组块承载面 | **9.52 / 10.17 / 5.99** | 通过 |
| barLow `#7DF9FF` / barMid `#FFC24B` / barHigh `#FF6B85` vs 进度条槽 | **14.71 / 11.40 / 6.71** | 通过 |
| border `#7DF9FF` vs 背景承载面 | **7.02** | 通过 |
| 组块面 `#18202E` vs 窗口底 `#06080D` | **1.226** | 通过 |

> 复杂夹具的 `valueCrit` 从主主题的 `#FF5C7A` **提亮**到 `#FF6B85`：在复杂背景下
> `#FF5C7A` 的对比度会掉到 5.86 附近（虽仍过 4.5，但余量太小），提亮后余量明显变好。

---

## 8. 夹具清单

| 夹具 | 文件 | 背景配置 | 用途 |
| --- | --- | --- | --- |
| 暗底（主） | `resources/themes/CyberPunk_Mech.json` | `gradient` + `opacity: 0` | 发行主题本身 |
| 亮底 | `tests/AppearanceThemeCheck/fixtures/CyberPunk_Mech_Light.json` | `gradient` + `opacity: 0` | 同一套布局参数在浅底下的可读性 |
| 复杂背景 | `tests/AppearanceThemeCheck/fixtures/CyberPunk_Mech_Complex.json` | `gradient` + `opacity: 0.35` + `tint` + `noise: 22` + `edgeGlow: 45` | 渐变 / 染色 / 噪点 / 边缘辉光同时开启时的最坏情况 |

三套夹具的 `layout` 与 `font` **完全相同**（只换配色与背景参数），这样对比度差异只来自配色，
不会混入布局变量；`opacity` 一律显式写出（符合 §6 的 R1），`opacity > 0` 的复杂夹具也显式写了 `tint`（R3）。

---

## 9. 横竖屏 / 多卡形态组合表

组合表（**参数组合**，不是渲染图）：

| 形态 | 建议参数 | 组块宽 / 面板宽 | 组标题与数值的可读性判据 |
| --- | --- | --- | --- |
| 竖屏 · 单卡 · 240px | 本主题默认参数（`padding 10`、`groupPadding 8`、`rowHeight 40`、`itemGap 6`） | 组块宽 = `PanelWidth − 20` = 220 | 标题面判据 = §7.3 暗底表；正文/数值面判据 = 组块面表 |
| 竖屏 · 单卡 · 180px（最窄预设） | `padding` 保持 10；`groupSpacing` 保持 28 | 组块宽 = 160 | 组标题仍落在背景面（判据不变）；**`NET/DISK/DATA` 双列组在 160px 内会很挤**，必要时把 `groupPadding` 降到 4 |
| 竖屏 · 4 卡 | 参数不变，但**面板总高按组数线性增长** | 高 = `Σ(组高) + 组间距 + padding` | 4 卡 ⇒ 约 7 个组纵向堆叠；组标题与上一组块的净空 = `groupSpacing − groupTitleOffset − 组标题行高` ≈ `28 − 6 − 18 = 4px`，偏紧 |
| 横屏 · 单卡 | 本主题默认参数 | 面板宽 = 各列实测宽累加 + 列间距（`src/UI/HorizontalLayout.cs → HorizontalLayout → Build`） | 横屏是"标签 + 数值"同排，标签与数值都落在组块面上，判据 = 组块面表 |
| 横屏 · 4 卡 | 参数不变 | 面板宽随列数**线性增长** | 宽度不是风险，**风险在列内文字被压窄**：值列不足时应把 `font.value` 从 14 降到 12 |

### 9.1 多卡的具体机制

- 按卡分组：`src/Core/Settings.cs → Settings → MonitorItemConfig.UIGroup` 里 `GPU.N.*` 会按卡拆组
  （`GpuGroupPolicy.SplitPerCard = true`），所以 **N 张卡 = N 个组**。
- 竖屏：组块**纵向堆叠**（`src/UI/UILayout.cs → UILayout → Build`：每组 `y += 组高 + groupSpacing + groupBottom`，
  总高 `= y + padding`）。**组数越多面板越高**，这是正常行为。
- 横屏：`PanelWidth` = 各列实测宽累加 + 列间距（`src/UI/HorizontalLayout.cs → HorizontalLayout → Build`），
  所以 4 卡横屏的面板会明显变宽。

### 9.2 注意事项

1. **组标题净空**：组标题画在组块**上方**的背景面上，占位高度 ≈ `font.group` 行高（10.5pt ≈ 18px），
   上移 `groupTitleOffset`（6px）。因此组间距必须 ≥ `groupTitleOffset + 组标题行高 + 余量`，
   本主题取 28 → 余量约 4px。**若把 `groupSpacing` 调到 22 以下，组标题会贴到上一个组块**。
2. **多卡时优先调 `groupSpacing` / `rowHeight`，不要先动 `padding`**：
   `padding` 同时影响左右两侧，改它会让窄面板（180px）更挤。
3. **切角边长与窄窗**：`frameLength` 会被 `ThemeAppearanceOptions → Create` 与
   `DecorGeometry → ClampCornerLengthPx` 两次夹紧，窄窗下切角自动变小，属正常。
4. **横屏没有组块底色问题**：横屏的列在 `GroupBackground` 上承载文字，判据与竖屏一致。
5. **`cornerRadius = 0` 的影响面**：`cornerRadius` 只影响 Win10 的 `Region`（`MainFormWinHelper → ApplyRoundedCorners`，
   为 0 时 `Region = null`，即直角）；Win11 固定走 DWM `DWMWCP_ROUND`，属 A09/A13 的接线范围，
   **A12 不改这条**。
6. **用户缩放（`Settings.UIScale`）只影响字号**：`Theme → Scale` 里字号按用户缩放放大，
   而 `frameWidth` / `frameLength` 按 `dpiScale × userScale` 的**合成比例**换算，二者不要混淆。

---

## 10. 校验方式与复现命令

校验工程：`tests/AppearanceThemeCheck/`（独立 console，ProjectReference 指向主工程，**不加任何 NuGet 依赖**）。

```powershell
dotnet build tests\AppearanceThemeCheck\AppearanceThemeCheck.csproj -c Release
& '.\tests\AppearanceThemeCheck\output\AppearanceThemeCheck.exe'
```

退出码：`0` = 全部通过；`1` = 有失败项；`2` = 找不到仓库根目录。

校验内容：

1. **基线**：10 个旧主题的 SHA256 前 16 位与 A00 基线表逐一对齐；主题目录文件集合恰好是
   "10 个旧主题 + A12 主主题"。
2. **参数**：磁盘文件里的参数与 `tests/AppearanceThemeCheck/ThemeSpec.cs → ThemeSpecs` 里的期望表逐项相等
   （期望表与本文件同源，改一边必须改另一边）。
3. **归一化**：`ThemeAppearanceOptions → Create` 在 4 组（DPI × 用户缩放 × 客户区）下无意外夹紧，
   物理像素换算符合公式；**归一化不回写原 `Theme` 对象**。
4. **背景规则**：R1 / R2 / R3 三条规则；`ComputeTintAlpha` 与「Tint alpha × Opacity」一致；
   `IsGradientUsable` 为真（透明 tint 不破坏降级链）；`ComputeLayers` 与 `opacity` 的一致性。
5. **往返**：`ThemeFileService → SaveTheme` 后再 `LoadTheme`，所有字段值不变；
   源文件**显式写出的键**在保存结果中全部保留且值相等；保存结果没有重复键。
6. **可读性**：按 §7.2 的承载面模型计算对比度并对照 §7.1 的判据表。

---

## 11. 未验证项与风险

- **没有真实渲染证据**：本交付物只做参数层面的判定。最终"看起来像不像机能风"必须由 A14 的真实渲染裁决；
  本文件的 alpha 公式与承载面模型可用于解释渲染结果，但不能替代它。
- **切角 / 辉光的观感未经验证**：`frameStyle = 1` 的切角路径、`glow` 的外发光在真实 DPI 下的观感未截图验证。
- **Windows 10 / 11 的圆角差异未处理**：`cornerRadius = 0` 在 Win10 是直角，在 Win11 仍受 DWM 圆角影响（A09/A13 范围）。
- **`glass` 模式未纳入夹具**：实时捕获后端尚未就绪，本主题只用 `gradient`，`glass` 的行为留给 A15。
- **主工程的既有编译警告**：首次全量重编主工程时会暴露 234 条既有警告
  （集中在 `src/UI/Settings/MonitorPage.cs`、`src/Plugins/PluginExecutor.cs`、
  `src/UI/MainForm_Transparent.cs`、`src/UI/Settings/PluginPage.cs`），**与 A12 无关**（A12 未触碰这些文件）。
- **校验工程的 sln 接入**：本工程尚未加入 `LiteMonitor.sln`，由主集成者接入。
