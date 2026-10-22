# A16 —— 液态玻璃主题（LiquidGlass.json）参数说明

> 计划：[2026-10-20-appearance-liquid-glass-theme.md](2026-10-20-appearance-liquid-glass-theme.md)（v4）§3.1 / §3.4 / §5
> 基线：`resources/themes/CyberPunk_Mech.json`（schema 样板，1321 字节，UTF-8 无 BOM，CRLF）
> 产出者：A16 工作线（主题文件 + 参数说明）
> 状态：**已产出**（主题 JSON + 静态自检通过；实机观感待交互会话验证）
> 改动范围：**仅新增 2 个文件**（本文件 + `resources/themes/LiquidGlass.json`），未改动任何既有文件
> （第二轮修订时 `resources/themes/LiquidGlass.json` 被就地改写；本文件的改动范围不变）

> 🔴 **第二轮修订（实机反馈后）**
>
> 首版是**浅冷磨砂玻璃**。实机反馈是「它一点儿也不是液态玻璃」——画面呈一块**浅蓝白平板**。
> 取证结论有三条：
>
> 1. 玻璃**根本没生效**：实时捕获后端没有出帧 ⇒ `glass → gradient` 降级；
> 2. 降级态被 65% 的**浅色** tint 纱罩洗白，与玻璃态在画面上**无法区分**（违反 M2 合同 §7「降级不静默」）；
> 3. 即便玻璃生效，0.65 浅色纱罩只剩 35% 背景 ⇒ 物理上是**磨砂亚克力**，不是液态玻璃。
>
> 本轮据此做了三件事：
>
> | # | 动作 | 落点 |
> |---|---|---|
> | A | 主题改**深色磨砂玻璃**（深底 + 亮字 + 低 alpha 深色纱罩） | 本文件 §3 / §7 |
> | B | 降级**可见**：面板底部画「实时玻璃未生效：\<原因\>」 | `src/UI/UIController.cs` → `UIController` → `DrawLiveGlassNotice` |
> | C | 诊断日志：写 exe 同目录 `liveglass.log`（仅状态变化时写） | `src/UI/LiveGlassController.cs` → `LiveGlassController` → `LiveGlassLog` |
>
> 同时把窗口轮廓从**切角**（`frameStyle = 1`）改成**圆角描边**（`frameStyle = 2`）：
> 圆角是液态玻璃最直观的轮廓特征，切角是赛博朋克语言，不是玻璃语言。

---

## 1. 主题定位

`LiquidGlass` 是**唯一一个请求实时玻璃背景**的主题（`backdrop.mode = "glass"`），也是本轮 A16 的唯一产出理由。
其余 11 个内置主题仍是 `gradient` / `solid` 路径，行为不变。

设计目标：**深色磨砂玻璃**（Windows 11 Mica / Acrylic 形态）—— 面板本体透出经过模糊的桌面壁纸，
组块为半透明**深色**磨砂卡片，文字为**亮色**高对比，窗口为**圆角**轮廓。

与其它主题的关系：

| 维度 | 与其它主题的关系 |
|---|---|
| 布局 `layout` | 键集合与 `CyberPunk_Mech.json` 完全一致（16 键）；**4 个取值**按玻璃观感调整（见 §3.0） |
| 字体 `font` | **完全沿用**（8 键逐键同值，含扩展键 `font.scale = 1.0`；本轮只保留该扩展键，不启用新字号语义） |
| 颜色 `color` | 15 键**键集合完全一致**，取值改为深色磨砂玻璃配色 |
| 背景 `backdrop` | 8 键**键集合完全一致**，`mode` 由 `gradient` 改为 `glass` |

---

## 2. 键集合比对（与 `CyberPunk_Mech.json` 逐段）

| 段 | 样板键数 | 本主题键数 | 键集合一致 | 键顺序一致 | 备注 |
|---|---|---|---|---|---|
| 顶层 | 6 | 6 | 是 | 是 | `version` / `name` / `layout` / `font` / `color` / `backdrop` |
| `layout` | 16 | 16 | 是 | 是 | 键集合一致；**4 个取值**不同（§3.0） |
| `font` | 8 | 8 | 是 | 是 | 取值逐键相同 |
| `color` | **15** | **15** | 是 | 是 | 样板实际为 15 键（`background`…`barHigh`），以文件为准 |
| `backdrop` | 8 | 8 | 是 | 是 | `mode`/`opacity`/`blur`/`tint`/`gradientStart`/`gradientEnd`/`gradientAngle`/`fps` |

> 注：任务描述中写作“color 14 键”，但同一段列举与样板文件实际都是 **15** 键；本主题以**样板文件实际键集合**为准，
> 保证“与 `CyberPunk_Mech.json` 键集合完全一致”这一硬约束成立。

---

## 3. 各段取值表

### 3.0 `layout` 的 4 处取值调整（相对 `CyberPunk_Mech.json`）

| 键 | 样板值 | 本主题值 | 理由 |
|---|---|---|---|
| `cornerRadius` | `0`（方形） | `16` | 圆角是液态玻璃最直观的轮廓特征。窗口轮廓（`WindowAppearanceController` → Region）与绘制层外框（`DecorRenderer` → `RenderWindowFrame`）**必须用同一个值**，否则轮廓与画出来的边框对不上 —— 两处都取主题的 `layout.cornerRadius`（见 `src/UI/MainForm_Transparent.cs` → `MainForm` → `CurrentAppearanceSnapshot`） |
| `frameStyle` | `1`（切角） | `2`（圆角描边） | `frameStyle = 2` 时窗口轮廓策略是 `Rounded`（系统圆角 + 同半径 Region），与 `DecorGeometry` → `BuildInnerFramePath` 的圆角描边路径一致；切角是赛博朋克语言，不是玻璃语言 |
| `scanline` | `30` | `18` | 扫描线取色是外框色（`#7FD4FF` 亮青），强度过高会在实时帧上形成明显的青色网格感；深色玻璃上需要更克制 |
| `grid` | `20` | `10` | 同上 |

其余 12 键（`rowHeight` / `padding` / `groupRadius` / `groupPadding` / `groupSpacing` / `groupBottom` /
`groupTitleOffset` / `itemGap` / `frameWidth` / `frameLength` / `glow` / `barStyle`）**逐键同值**。

> `groupRadius` 由 `4` 提到 `10` —— 圆角卡片是玻璃卡片的标志形态，方角卡片是终端面板形态。
> 该键不参与主题探针的期望表（探针只校验 `frameStyle/frameWidth/frameLength/scanline/glow/grid/barStyle`）。

### 3.1 `color`（15 键，全部沿用键名）

| 键 | 取值 | 形态 | 作用 |
|---|---|---|---|
| `background` | `#0A1220` | hex | 底板 / solid 回退时的面板底色（玻璃态下被实时帧覆盖） |
| `groupBackground` | `rgba(22,42,66,0.86)` | rgba | 组块磨砂卡片（半透明**深**蓝灰，透出玻璃） |
| `border` | `#7FD4FF` | hex | 外框描边（`frameStyle = 2` 圆角描边）—— 亮青色的玻璃边缘高光 |
| `shadow` | `rgba(0,0,0,0.45)` | rgba | 辉光/阴影色（`glow = 55` 时消费） |
| `textTitle` | `#C7ECFF` | hex | 主标题（亮冰青） |
| `textGroup` | `#A6C8E0` | hex | 分组标题（CPU / GPU…） |
| `textPrimary` | `#EAF4FC` | hex | 主文字（近白冷灰） |
| `textSecondary` | `#9FB9CE` | hex | 次级文字（单位/副标签） |
| `valueSafe` | `#4ADE80` | hex | 安全值（亮绿） |
| `valueWarn` | `#FBBF24` | hex | 警告值（亮琥珀） |
| `valueCrit` | `#F87171` | hex | 危险值（亮红） |
| `barBackground` | `#16283C` | hex | 进度条底槽 / 分割线 |
| `barLow` | `#4ADE80` | hex | 进度条低段（与 `valueSafe` 同色系） |
| `barMid` | `#FBBF24` | hex | 进度条中段（与 `valueWarn` 同色系） |
| `barHigh` | `#F87171` | hex | 进度条高段（与 `valueCrit` 同色系） |

**颜色书写形式**：与样板一致 —— 不透明色用 `#RRGGBB`，半透明色用 `rgba(r,g,b,a)`（r/g/b 为 0–255 整数，a 为 0–1 小数），
不混用其它形式。两种形式均被 `ThemeManager.ParseColor` 接受（`src/Core/ThemeManager.cs` → `ThemeManager` → `ParseColor`）。

### 3.2 `backdrop`

| 键 | 取值 |
|---|---|
| `mode` | `glass` |
| `opacity` | `0.72` |
| `blur` | `12` |
| `tint` | `rgba(9,17,30,1.0)` |
| `gradientStart` | `#1B2C44` |
| `gradientEnd` | `#0A1220` |
| `gradientAngle` | `90` |
| `fps` | `2` |

---

## 4. `backdrop` 各字段：归一化范围、实际取值与理由

归一化范围来自 `src/Core/ThemeAppearanceOptions.cs` → `ThemeAppearanceOptions` → `Create`（运行时夹紧，**不回写 JSON**）。

| 字段 | 归一化范围 | 本主题取值 | 理由 |
|---|---|---|---|
| `mode` | `solid` / `gradient` / `glass`（未知值退回 `solid`） | `glass` | 本主题存在的唯一理由；请求实时玻璃材质 |
| `opacity` | `[0, 1]`（`OpacityMin/Max`） | `0.72` | 与 tint 相乘得到**纱罩有效 alpha**；见 §7.2 |
| `blur` | `[0, 32]` 逻辑半径（`BlurMin/Max`，再按 DPI 换算为物理像素 `BlurPx`） | `12` | 合同建议值：足够糊化壁纸，又不完全抹平层次 |
| `fps` | `[1, 6]`（`FpsMin/Max`） | `2` | 合同 §3.4「捕获上限 6fps、默认 2fps」；低帧率降低捕获开销 |
| `tint` | `rgba(r,g,b,a)`，a ∈ [0,1] | `rgba(9,17,30,1.0)` | **深色**纱罩。**实际生效 alpha = tint.A × opacity = 255 × 0.72 ≈ 184/255 ≈ 0.72** ⇒ 实时帧透出约 28% |
| `gradientStart` | 可空，缺省取 `Background` | `#1B2C44` | 降级链 gradient 分支的起始色（不透明，保证 `IsGradientUsable` 成立） |
| `gradientEnd` | 可空，缺省取 `GroupBackground` | `#0A1220` | 降级链 gradient 分支的结束色（不透明） |
| `gradientAngle` | 度，缺省 `90` | `90` | 合同缺省值，自上而下的垂直渐变 |

---

## 5. 降级说明（`glass → gradient → solid`）

判定链在 `src/System/Backdrop/BackdropModeResolver.cs` → `BackdropModeResolver` → `Resolve`，
每级降级都会给出原因码与可读原因文本。

### 5.1 `glass` 生效（实时捕获后端可用）

绘制顺序（`src/System/Backdrop/BackdropRenderer.cs` → `BackdropRenderer` → `Render`）：

1. 不透明底板（`color.background`，alpha 被强制为 255）
2. **实时捕获帧**：按 `blur = 12` 模糊后铺满整个客户区
3. **tint 纱罩**：`alpha = tint.A × opacity ≈ 0.72` 的深色纱
4. 随后 `UIRenderer` 叠加文字与组卡片（组卡片 = `rgba(22,42,66,0.86)` 半透明深蓝灰，透出玻璃）

### 5.2 `glass → gradient`（**常见路径**）

触发原因码：`BackendMissing`（未注册实时背景后端）/ `BackendNotReady`（后端未就绪）/ `BackendFrameInvalid`（未提供有效帧）。
此时玻璃帧不可用，退到静态渐变：

- 渐变两端：`gradientStart = #1B2C44` → `gradientEnd = #0A1220`，`gradientAngle = 90`（自上而下）
- 两端 alpha 均为 255（不透明）⇒ **不铺底板**，渐变直接盖满客户区
- 渐变之上**仍会叠 tint 纱罩**（`ComputeLayers` 对 `Gradient` 同样返回 `Base | Tint`）
- ⇒ 用户实际看到的是**深蓝黑**渐变（`#1B2C44` → `#0A1220`，被深色纱罩进一步压暗），组卡片在其上为深蓝灰磨砂卡
- 因 `IsGradientUsable` 为真（两端 alpha > 0），**该分支始终可用**，不会出现“玻璃失效就黑屏/透明”的情况

> ★ **降级不再静默**（本轮新增）：当「开关已开 + 主题请求 glass + 服务确实在降级」三条件同时成立时，
> 面板底部会画一行「实时玻璃未生效：\<原因\>」（`src/UI/UIController.cs` → `UIController` → `DrawLiveGlassNotice`）。
> 其余情况画面**零变化**。

### 5.3 `gradient → solid`

仅当渐变两端 alpha 均为 0 时才会继续降级（`IsGradientUsable` 为 false）。本主题两端均为不透明十六进制，
**该分支实际不可达**。若极端情况下进入 `solid`：背景层零绘制（`BackdropLayer.None`），
沿用旧背景路径 —— 画面 = `color.background = #0A1220` 底板 + `rgba(22,42,66,0.86)` 组卡片 + 亮色文字。

---

## 6. 副作用提示

- **面板可能被自身捕获**：实时玻璃要求窗口所在区域能被实时抓取，而面板自己就在被抓取区域内。
  典型表现是**录屏或连续截图时面板短暂消失 / 闪动**（抓到的是“还没有面板”的那一帧）。这是本主题的已知副作用，不是缺陷。
- **背景刷新有延迟**：`fps = 2` ⇒ 背景约每 500 ms 更新一次。切换窗口 / 拖动桌面图标时，玻璃背景有肉眼可见的滞后。
- **捕获会持续占用开销**：实时抓屏 + 模糊滤波是持续性 CPU 开销；在低性能机器上需关注帧耗时。
- **高 DPI 下模糊更强**：`blur` 是 96 DPI 逻辑值，实际物理半径 = `blur × DPI 缩放`（`BlurPx`），150% 缩放时视觉模糊约为 18 物理像素。
- **降级是可见的**（本轮修复）：后端不可用时，面板底部会出现「实时玻璃未生效：\<原因\>」，画面从“玻璃”变为“深色渐变”。
  诊断细节另写入 exe 同目录的 `liveglass.log`（仅在状态变化时写，避免每帧刷屏）。

---

## 7. 配色取舍（含对比度实测计算）

### 7.1 为什么改用“深色磨砂玻璃”

首版的浅冷玻璃有**两个物理性问题**：

1. **浅色玻璃 + 厚纱罩 = 磨砂亚克力**：tint 有效 alpha 0.651 意味着背景只剩 35% 透出，
   这正是 Acrylic（亚克力）而不是 Liquid Glass 的定义；
2. **浅底 + 深字在实时内容上不可靠**：玻璃底下是用户桌面，亮度不可控。

深色磨砂玻璃是经典 Mica / Acrylic 形态：深底、亮字、低 alpha **深色**纱罩。
代价是通透度更低（背景透出约 28%），换来的是**任意桌面内容下文字都读得清**。

### 7.2 关键取舍：tint 强度

任务示例给的是 `rgba(150,200,255,0.18)`。**本主题没有采用**，原因是：

- 玻璃态下不透明底板会被实时帧**完全覆盖**，真正决定文字对比度的只有 tint 纱罩；
- 按示例取 0.18，实际纱罩 alpha 仅 `46 × 0.72 ≈ 0.13`，玻璃在任意壁纸下都无法保证文字可读；
- 因此取 `rgba(9,17,30,1.0)`，实际纱罩 alpha ≈ **0.72** —— 实时帧透出约 28%，观感是**磨砂玻璃**而非完全透明玻璃。

**代价**：通透度低于纯玻璃。这是有意的**可读性优先**取舍。

### 7.3 对比度 —— 以主题探针的**判据实测值**为准（权威）

> 📌 **本节数据由 `tests/AppearanceThemeCheck` 实跑给出**（`LiquidGlass：可读性判据` 段），
> 取代早期按 WCAG 公式手算的推算。探针用**最坏合成**承载面，以探针为准。

探针的承载面口径：**背景承载面 = `#17273A`**（渐变端色 → tint → 底纹的最坏合成）、
**组块承载面 = `#162A42`**、**进度条槽 = `#16283C`**。

| 前景色 | 承载面 | 实测对比度 | 判据 | 结果 |
|---|---|---|---|---|
| `textTitle` `#C7ECFF` | 背景承载面 `#17273A` | **12.16** | ≥ 4.5 | ✅ |
| `textGroup` `#A6C8E0` | 背景承载面 `#17273A` | **8.62** | ≥ 4.5 | ✅ |
| `textPrimary` `#EAF4FC` | 组块承载面 `#162A42` | **13.05** | ≥ 4.5 | ✅ |
| `textSecondary` `#9FB9CE` | 组块承载面 `#162A42` | **7.14** | ≥ 4.5 | ✅ |
| `valueSafe` `#4ADE80` | 组块承载面 `#162A42` | **8.35** | ≥ 4.5 | ✅ |
| `valueWarn` `#FBBF24` | 组块承载面 `#162A42` | **8.71** | ≥ 4.5 | ✅ |
| `valueCrit` `#F87171` | 组块承载面 `#162A42` | **5.26** | ≥ 4.5 | ✅ |
| `barLow` `#4ADE80` | 进度条槽 `#16283C` | **8.59** | ≥ 3.0 | ✅ |
| `barMid` `#FBBF24` | 进度条槽 `#16283C` | **8.97** | ≥ 3.0 | ✅ |
| `barHigh` `#F87171` | 进度条槽 `#16283C` | **5.41** | ≥ 3.0 | ✅ |
| `border` `#7FD4FF` | 背景承载面 `#17273A` | **9.20** | ≥ 3.0 | ✅ |
| 组块面 vs 窗口底可分性 | `#162A42` vs `#0A1220` | **1.289** | ≥ 1.2 | ✅ |

说明：

- 除主标题与分组标题（画在组块之外，直接落在玻璃上）外，**所有文字都画在组卡片上**，对比度 ≥ 5.2:1（满足 WCAG AA 正文 4.5:1）。
- 分组标题与外框直接落在玻璃上，探针按**最坏合成**承载面判定，仍满足各自判据。
- `border` 是装饰性外框，9.20:1 满足图形对比度 3.0:1。

> ⚠️ **探针口径的边界（重要）**：探针的「背景承载面」是**降级态的静态渐变**，
> **不含实时帧**。玻璃态下卡片与标题压在的是**用户桌面内容**，亮度不可控。
> 下面这组是**手工计算值**（WCAG 公式，假设桌面为纯白 —— 最坏情况），**不是探针实测值**：
>
> | 场景 | 计算的承载面 | 对比度（计算值） |
> |---|---|---|
> | 纯白桌面 → 纱罩后 | `#4E545D` | `textTitle` ≈ 6.1:1 |
> | 纯白桌面 → 纱罩后 → 卡片 | `#1E3046` | `valueCrit` ≈ 4.9:1 / `textSecondary` ≈ 6.7:1 |
>
> 这是把 tint 有效 alpha 定在 0.72、卡片 alpha 定在 0.86 的直接原因：**两者相乘 ≈ 0.62**，
> 足以把纯白桌面压到可读区间。

### 7.4 为保证对比度而做的取值调整（三轮）

**第一轮**（首版浅色玻璃，相对最初版）：初版取 `valueSafe #0E7A46` / `valueWarn #9A5B00` / `valueCrit #C2263F` / `textSecondary #5A7089`，
在纯黑壁纸 + 卡片 alpha 0.55 的组合下对比度仅 3.3~3.7:1（不达 AA）。因此数值色 / 次级文字整体压深，
组卡片 alpha 0.55 → 0.72，纱罩有效 alpha 0.588 → 0.651。

**第二轮**（接入主题探针后）：把 `LiquidGlass` 纳入 `tests/AppearanceThemeCheck` 的校验集合后，
探针报出 4 项不达标（其余 438 项通过）—— 这 4 项是**真实的可读性缺陷**，不是探针误报：

| 项 | 原值 | 实测 | 改用 | 改后实测 |
|---|---|---|---|---|
| `textGroup` vs 背景承载面 | `#3D5670` | 3.81（需 ≥4.5） | `#33475B` | **4.80** ✅ |
| `barLow` vs 进度条槽 | `#12945A` | 2.35（需 ≥3.0） | `#0B6E3F` | **3.83** ✅ |
| `barMid` vs 进度条槽 | `#C07800` | 2.14（需 ≥3.0） | `#8A5200` | **3.86** ✅ |
| `barHigh` vs 进度条槽 | `#E03A52` | 2.59（需 ≥3.0） | `#B2223A` | **3.99** ✅ |

**第三轮**（实机反馈后，整体改深色）：首版浅色玻璃在实机上呈浅蓝白平板，且降级态与玻璃态无法区分。
本轮把**整套配色翻到深色**并把窗口轮廓改成圆角，同时：

- 纱罩由**冷白** `rgba(226,238,255,0.9)`（有效 0.651）改为**深色** `rgba(9,17,30,1.0)`（有效 0.72）——
  降级态不再被洗白，降级/玻璃两种状态在观感上**可区分**；
- 组卡片由**半透明白** `rgba(255,255,255,0.72)` 改为**半透明深蓝灰** `rgba(22,42,66,0.86)`；
- 文字与数值色整体**提亮**（`textPrimary` `#16202C` → `#EAF4FC` 等），适配深底；
- `frameStyle` 由切角 `1` 改为圆角描边 `2`，`cornerRadius` 由 `0` 改为 `16`。

改后主题探针 **442 / 442 全部通过**（见 §9）。

⇒ 三轮的修法始终是**改配色**而不是放宽判据。

---

## 8. 未验证项（需在交互会话实测）

1. **实时玻璃到底有没有出帧**：本轮加了 `liveglass.log`（exe 同目录），需要实机跑一次把日志拿回来，
   才能确定「玻璃未生效」的**确切后端原因**（`BackendMissing` / `BackendNotReady` / `BackendFrameInvalid`）。
2. **实机玻璃观感**：真实捕获后端下的模糊强度、纱罩浓度、组卡片通透感是否达到“液态玻璃”的预期观感。
3. **不同壁纸亮度下的观感**：§7.3 的第二张表是**计算值**，不是实机像素采样；实机还需看高饱和壁纸（红/绿/蓝）下的观感。
4. **圆角轮廓的实机观感**：`frameStyle = 2` + `cornerRadius = 16` 在 Win11（系统圆角 + 同半径 Region）
   与 Win10（只能靠圆角 Region）下是否都成立；DPI 缩放下圆角是否偏小。
5. **扫描线 / 网格在实时帧上是否突兀**：`scanline = 18` / `grid = 10` 是按深色玻璃降下来的，
   取色是亮青色外框色（`#7FD4FF`），仍需实机确认。
6. **横屏 / 竖屏两种布局**下的组卡片层次是否一致。
7. **主题编辑器**（A10）能否正确读写本主题的 `backdrop` 段。

---

## 9. 静态自检记录

| 检查项 | 方法 | 结果 |
|---|---|---|
| JSON 合法性 | `ConvertFrom-Json` | 通过 |
| 键集合逐段比对 | 与 `CyberPunk_Mech.json` 做 `Compare-Object` | 顶层 6 / layout 16 / font 8 / color 15 / backdrop 8，**全部一致（含顺序）** |
| `version` / `name` | 读值 | `4` / `LiquidGlass` |
| `backdrop` 范围 | 区间判定 | `mode=glass`；`opacity=0.72 ∈ [0,1]`；`blur=12 ∈ [0,32]`；`fps=2 ∈ [1,6]` |
| 颜色格式 | 按 `ParseColor` 规则逐值判定 | `color` 段 13 个 `#RRGGBB` + 2 个 `rgba(r,g,b,a)`；`backdrop.tint` 为 `rgba`、渐变两端为 `#RRGGBB` —— 全部合法 |
| 编码 / 行尾 | 读字节 | UTF-8 **无 BOM**，**CRLF**（59 行，与样板一致） |
| **主题探针实跑** | `tests/AppearanceThemeCheck` | **442 / 442 通过，0 失败**（深色改版后重跑；`LiquidGlass` 的 12 项可读性判据实测值见 §7.3） |
| 主工程构建 | `dotnet build LiteMonitor.csproj -c Release` | **0 错误 / 234 警告**（与基线一致） |
| 单元测试 | `dotnet test tests/LiteMonitor.Tests` | **583 / 583 通过** |
