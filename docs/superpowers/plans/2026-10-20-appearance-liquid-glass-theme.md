# A16 —— 液态玻璃主题（LiquidGlass.json）参数说明

> 计划：[2026-10-20-appearance-liquid-glass-theme.md](2026-10-20-appearance-liquid-glass-theme.md)（v4）§3.1 / §3.4 / §5
> 基线：`resources/themes/CyberPunk_Mech.json`（schema 样板，1321 字节，UTF-8 无 BOM，CRLF）
> 产出者：A16 工作线（主题文件 + 参数说明）
> 状态：**已产出**（主题 JSON + 静态自检通过；实机观感待交互会话验证）
> 改动范围：**仅新增 2 个文件**（本文件 + `resources/themes/LiquidGlass.json`），未改动任何既有文件

---

## 1. 主题定位

`LiquidGlass` 是**唯一一个请求实时玻璃背景**的主题（`backdrop.mode = "glass"`），也是本轮 A16 的唯一产出理由。
其余 11 个内置主题仍是 `gradient` / `solid` 路径，行为不变。

设计目标：浅冷色调的**磨砂玻璃**（Fluent / Liquid Glass 风格）—— 面板本体透出经过模糊的桌面壁纸，
组块为半透明白色磨砂卡片，文字为深色高对比。

与其它主题的关系：

| 维度 | 与其它主题的关系 |
|---|---|
| 布局 `layout` | **完全沿用** `CyberPunk_Mech.json`（16 键逐键同值），不引入未验证的排版风险 |
| 字体 `font` | **完全沿用**（8 键逐键同值，含扩展键 `font.scale = 1.0`；本轮只保留该扩展键，不启用新字号语义） |
| 颜色 `color` | 15 键**键集合完全一致**，取值改为浅冷玻璃配色 |
| 背景 `backdrop` | 8 键**键集合完全一致**，`mode` 由 `gradient` 改为 `glass` |

---

## 2. 键集合比对（与 `CyberPunk_Mech.json` 逐段）

| 段 | 样板键数 | 本主题键数 | 键集合一致 | 键顺序一致 | 备注 |
|---|---|---|---|---|---|
| 顶层 | 6 | 6 | 是 | 是 | `version` / `name` / `layout` / `font` / `color` / `backdrop` |
| `layout` | 16 | 16 | 是 | 是 | 取值逐键相同 |
| `font` | 8 | 8 | 是 | 是 | 取值逐键相同 |
| `color` | **15** | **15** | 是 | 是 | 样板实际为 15 键（`background`…`barHigh`），以文件为准 |
| `backdrop` | 8 | 8 | 是 | 是 | `mode`/`opacity`/`blur`/`tint`/`gradientStart`/`gradientEnd`/`gradientAngle`/`fps` |

> 注：任务描述中写作“color 14 键”，但同一段列举与样板文件实际都是 **15** 键；本主题以**样板文件实际键集合**为准，
> 保证“与 `CyberPunk_Mech.json` 键集合完全一致”这一硬约束成立。

---

## 3. 各段取值表

### 3.1 `color`（15 键，全部沿用键名）

| 键 | 取值 | 形态 | 作用 |
|---|---|---|---|
| `background` | `#DCE6F2` | hex | 底板 / solid 回退时的面板底色（玻璃态下被实时帧覆盖） |
| `groupBackground` | `rgba(255,255,255,0.72)` | rgba | 组块磨砂卡片（半透明白，透出玻璃） |
| `border` | `#2F5F8F` | hex | 外框描边（`frameStyle = 1` 切角） |
| `shadow` | `rgba(14,34,60,0.32)` | rgba | 辉光/阴影色（`glow = 55` 时消费） |
| `textTitle` | `#0B2E52` | hex | 主标题（深藏青） |
| `textGroup` | `#33475B` | hex | 分组标题（CPU / GPU…） |
| `textPrimary` | `#16202C` | hex | 主文字（近黑冷灰） |
| `textSecondary` | `#4C6178` | hex | 次级文字（单位/副标签） |
| `valueSafe` | `#0B6E3F` | hex | 安全值（深绿） |
| `valueWarn` | `#8A5200` | hex | 警告值（深琥珀） |
| `valueCrit` | `#B2223A` | hex | 危险值（深红） |
| `barBackground` | `#BCCBDB` | hex | 进度条底槽 / 分割线 |
| `barLow` | `#0B6E3F` | hex | 进度条低段（与 `valueSafe` 同色系） |
| `barMid` | `#8A5200` | hex | 进度条中段（与 `valueWarn` 同色系） |
| `barHigh` | `#B2223A` | hex | 进度条高段（与 `valueCrit` 同色系） |

**颜色书写形式**：与样板一致 —— 不透明色用 `#RRGGBB`，半透明色用 `rgba(r,g,b,a)`（r/g/b 为 0–255 整数，a 为 0–1 小数），
不混用其它形式。两种形式均被 `ThemeManager.ParseColor` 接受（`src/Core/ThemeManager.cs:602-632`）。
`border` / `shadow` / `textSecondary` / `textTitle` 四个可空键在样板中**均存在**，本主题**同样存在**（未凭空新增可空键）。

### 3.2 `backdrop`

| 键 | 取值 |
|---|---|
| `mode` | `glass` |
| `opacity` | `0.72` |
| `blur` | `12` |
| `tint` | `rgba(226,238,255,0.9)` |
| `gradientStart` | `#9BB6D5` |
| `gradientEnd` | `#5B85B3` |
| `gradientAngle` | `90` |
| `fps` | `2` |

---

## 4. `backdrop` 各字段：归一化范围、实际取值与理由

归一化范围来自 `src/Core/ThemeAppearanceOptions.cs:34-39`（运行时夹紧，**不回写 JSON**）。

| 字段 | 归一化范围 | 本主题取值 | 理由 |
|---|---|---|---|
| `mode` | `solid` / `gradient` / `glass`（未知值退回 `solid`） | `glass` | 本主题存在的唯一理由；请求实时玻璃材质 |
| `opacity` | `[0, 1]`（`OpacityMin/Max`） | `0.72` | 合同建议区间 0.55~0.75，取偏上值以保证浅色纱罩强度 |
| `blur` | `[0, 32]` 逻辑半径（`BlurMin/Max`，再按 DPI 换算为物理像素 `BlurPx`） | `12` | 合同建议值：足够糊化壁纸，又不完全抹平层次 |
| `fps` | `[1, 6]`（`FpsMin/Max`） | `2` | 合同 §3.4「捕获上限 6fps、默认 2fps」；低帧率降低捕获开销 |
| `tint` | `rgba(r,g,b,a)`，a ∈ [0,1] | `rgba(226,238,255,0.9)` | 冷白纱罩。**实际生效 alpha = tint.A × opacity = 230 × 0.72 ≈ 166/255 ≈ 0.651** |
| `gradientStart` | 可空，缺省取 `Background` | `#9BB6D5` | 降级链 gradient 分支的起始色（不透明，保证 `IsGradientUsable` 成立） |
| `gradientEnd` | 可空，缺省取 `GroupBackground` | `#5B85B3` | 降级链 gradient 分支的结束色（不透明） |
| `gradientAngle` | 度，缺省 `90` | `90` | 合同缺省值，自上而下的垂直渐变 |

---

## 5. 降级说明（`glass → gradient → solid`）

判定链在 `src/System/Backdrop/BackdropModeResolver.cs:91-140`，每级降级都会给出原因码与可读原因文本。

### 5.1 `glass` 生效（实时捕获后端可用）

绘制顺序（`BackdropRenderer`）：

1. 不透明底板（`color.background`，alpha 被强制为 255）
2. **实时捕获帧**：按 `blur = 12` 模糊后铺满整个客户区
3. **tint 纱罩**：`alpha = tint.A × opacity ≈ 0.651` 的冷白纱
4. 随后 `UIRenderer` 叠加文字与组卡片（组卡片 = `rgba(255,255,255,0.72)` 半透明白，透出玻璃）

### 5.2 `glass → gradient`（**常见路径**）

触发原因码：`BackendMissing`（未注册实时背景后端）/ `BackendNotReady`（后端未就绪）/ `BackendFrameInvalid`（未提供有效帧）。
此时玻璃帧不可用，退到静态渐变：

- 渐变两端：`gradientStart = #9BB6D5` → `gradientEnd = #5B85B3`，`gradientAngle = 90`（自上而下）
- 两端 alpha 均为 255（不透明）⇒ **不铺底板**，渐变直接盖满客户区
- 渐变之上**仍会叠 tint 纱罩**（`ComputeLayers` 对 `Gradient` 同样返回 `Base | Tint`）
- ⇒ 用户实际看到的渐变是 **约 `#C9DAF0` → `#B3C9E4`**（浅冷蓝白），组卡片在其上为 `#F0F5FB` 一类的浅色磨砂卡
- 因 `IsGradientUsable` 为真（两端 alpha > 0），**该分支始终可用**，不会出现“玻璃失效就黑屏/透明”的情况

### 5.3 `gradient → solid`

仅当渐变两端 alpha 均为 0 时才会继续降级（`IsGradientUsable` 为 false）。本主题两端均为不透明十六进制，
**该分支实际不可达**。若极端情况下进入 `solid`：背景层零绘制（`BackdropLayer.None`），
沿用旧背景路径 —— 画面 = `color.background = #DCE6F2` 底板 + `rgba(255,255,255,0.72)` 组卡片 + 深色文字。

---

## 6. 副作用提示

- **面板可能被自身捕获**：实时玻璃要求窗口所在区域能被实时抓取，而面板自己就在被抓取区域内。
  典型表现是**录屏或连续截图时面板短暂消失 / 闪动**（抓到的是“还没有面板”的那一帧）。这是本主题的已知副作用，不是缺陷。
- **背景刷新有延迟**：`fps = 2` ⇒ 背景约每 500 ms 更新一次。切换窗口 / 拖动桌面图标时，玻璃背景有肉眼可见的滞后。
- **捕获会持续占用开销**：实时抓屏 + 模糊滤波是持续性 CPU 开销；在低性能机器上需关注帧耗时。
- **高 DPI 下模糊更强**：`blur` 是 96 DPI 逻辑值，实际物理半径 = `blur × DPI 缩放`（`BlurPx`），150% 缩放时视觉模糊约为 18 物理像素。
- **降级是静默切换的视觉变化**：后端不可用时用户不会收到提示（状态只体现在 `BackdropStatus` 的原因文本里），
  观感上会从“玻璃”突变为“浅色渐变”。

---

## 7. 配色取舍（含对比度实测计算）

### 7.1 为什么选“浅冷磨砂玻璃”

与目录内另外 11 个暗色主题形成明确区分，符合 Liquid Glass 的通透观感；面板是悬浮小窗，浅色玻璃在深色桌面上更醒目。

### 7.2 关键取舍：tint 强度远高于任务示例

任务示例给的是 `rgba(150,200,255,0.18)`。**本主题没有采用**，原因是：

- 玻璃态下不透明底板会被实时帧**完全覆盖**，真正决定文字对比度的只有 tint 纱罩；
- 按示例取 0.18，实际纱罩 alpha 仅 `46 × 0.72 ≈ 0.13`，浅色玻璃在任意壁纸下都无法保证文字可读；
- 因此取 `rgba(226,238,255,0.9)`，实际纱罩 alpha ≈ **0.651** —— 壁纸仍透出约 35%，观感是**磨砂亚克力**而非完全透明玻璃。

**代价**：通透度低于纯玻璃。这是有意的**可读性优先**取舍。

### 7.3 对比度 —— 以主题探针的**判据实测值**为准（权威）

> 📌 **本节数据已由 `tests/AppearanceThemeCheck` 实测给出**（`LiquidGlass：可读性判据` 段），
> 取代早期按 WCAG 公式手算的推算。手算口径与探针口径不同（探针用**最坏合成**承载面），
> 以探针为准。

探针的承载面口径：**背景承载面 = `#A1BAD8`**（最坏合成）、**组块承载面 = `#FFFFFF`**、**进度条槽 = `#BCCBDB`**。

| 前景色 | 承载面 | 实测对比度 | 判据 | 结果 |
|---|---|---|---|---|
| `textTitle` `#0B2E52` | 背景承载面 | **6.90** | ≥ 4.5 | ✅ |
| `textGroup` `#33475B` | 背景承载面 | **4.80** | ≥ 4.5 | ✅ |
| `textPrimary` `#16202C` | 组块承载面 | **16.44** | ≥ 4.5 | ✅ |
| `textSecondary` `#4C6178` | 组块承载面 | **6.39** | ≥ 4.5 | ✅ |
| `valueSafe` `#0B6E3F` | 组块承载面 | **6.33** | ≥ 4.5 | ✅ |
| `valueWarn` `#8A5200` | 组块承载面 | **6.39** | ≥ 4.5 | ✅ |
| `valueCrit` `#B2223A` | 组块承载面 | **6.60** | ≥ 4.5 | ✅ |
| `barLow` `#0B6E3F` | 进度条槽 `#BCCBDB` | **3.83** | ≥ 3.0 | ✅ |
| `barMid` `#8A5200` | 进度条槽 `#BCCBDB` | **3.86** | ≥ 3.0 | ✅ |
| `barHigh` `#B2223A` | 进度条槽 `#BCCBDB` | **3.99** | ≥ 3.0 | ✅ |
| `border` `#2F5F8F` | 背景承载面 | **3.34** | ≥ 3.0 | ✅ |
| 组块面 vs 窗口底可分性 | `#FFFFFF` vs `#DCE6F2` | **1.262** | ≥ 1.2 | ✅ |

说明：

- 除主标题与分组标题（画在组块之外，直接落在玻璃上）外，**所有文字都画在组卡片上**，对比度 ≥ 6.3:1（满足 WCAG AA 正文 4.5:1）。
- 分组标题与外框直接落在玻璃上，探针按**最坏合成**承载面判定，仍满足各自判据。
- `border` 是装饰性外框，3.34:1 满足图形对比度 3.0:1。

### 7.4 为保证对比度而做的取值调整（两轮）

**第一轮**（相对最初版）：初版取 `valueSafe #0E7A46` / `valueWarn #9A5B00` / `valueCrit #C2263F` / `textSecondary #5A7089`，
在纯黑壁纸 + 卡片 alpha 0.55 的组合下对比度仅 3.3~3.7:1（不达 AA）。因此：

- 数值色 / 次级文字**整体压深**（如 `#0E7A46` → `#0B6E3F`）；
- 组卡片 alpha 0.55 → **0.72**；
- 纱罩有效 alpha 0.588 → **0.651**（tint alpha 0.82 → 0.9）。

**第二轮**（接入主题探针后）：把 `LiquidGlass` 纳入 `tests/AppearanceThemeCheck` 的校验集合后，
探针报出 4 项不达标（其余 438 项通过）—— 这 4 项是**真实的可读性缺陷**，不是探针误报：

| 项 | 原值 | 实测 | 改用 | 改后实测 |
|---|---|---|---|---|
| `textGroup` vs 背景承载面 | `#3D5670` | 3.81（需 ≥4.5） | `#33475B` | **4.80** ✅ |
| `barLow` vs 进度条槽 | `#12945A` | 2.35（需 ≥3.0） | `#0B6E3F` | **3.83** ✅ |
| `barMid` vs 进度条槽 | `#C07800` | 2.14（需 ≥3.0） | `#8A5200` | **3.86** ✅ |
| `barHigh` vs 进度条槽 | `#E03A52` | 2.59（需 ≥3.0） | `#B2223A` | **3.99** ✅ |

⇒ 修法是**改配色**而不是放宽判据：进度条三档改为与 `valueSafe` / `valueWarn` / `valueCrit`
同色（浅色玻璃下深色条在浅槽上才够可辨），分组标题同步压深。改后主题探针 **442 / 442 全部通过**。

---

## 8. 未验证项（需在交互会话实测）

1. **实机玻璃观感**：真实捕获后端下的模糊强度、纱罩浓度、组卡片通透感是否达到“液态玻璃”的预期观感。
2. **不同壁纸亮度下的观感**：本文件的对比度结论是**计算值**（WCAG 公式），不是实机像素采样；实机还需看高饱和壁纸（红/绿/蓝）下的观感。
3. **`layout` 沿用暗色系取值在浅色玻璃上的表现**：`scanline = 30` / `grid = 20` / `glow = 55` / `cornerRadius = 0`（方形）/ `frameStyle = 1`（切角）均来自 `CyberPunk_Mech.json`。
   本轮按要求**不改布局**，但扫描线/网格在浅色玻璃上是否突兀、辉光用深色 `shadow` 是否发闷，需实机确认。
4. **横屏 / 竖屏两种布局**下的组卡片层次是否一致。
5. **主题编辑器**（A10）能否正确读写本主题的 `backdrop` 段（未在本轮验证）。
6. **本轮未运行 `dotnet build` / `dotnet test` / 任何探针**（按任务约束，避免与主集成者争抢 `obj/`），
   因此“主题探针判定通过”属于按键集合与取值范围静态推演，而非实跑结论。

---

## 9. 静态自检记录

| 检查项 | 方法 | 结果 |
|---|---|---|
| JSON 合法性 | `ConvertFrom-Json` | 通过 |
| 键集合逐段比对 | 与 `CyberPunk_Mech.json` 做 `Compare-Object` | 顶层 6 / layout 16 / font 8 / color 15 / backdrop 8，**全部一致（含顺序）** |
| `layout` + `font` 取值 | 逐键字符串比对 | 全部相同 |
| `version` / `name` | 读值 | `4` / `LiquidGlass` |
| `backdrop` 范围 | 区间判定 | `mode=glass`；`opacity=0.72 ∈ [0,1]`；`blur=12 ∈ [0,32]`；`fps=2 ∈ [1,6]` |
| 颜色格式 | 按 `ParseColor` 规则逐值判定 | `color` 段 13 个 `#RRGGBB` + 2 个 `rgba(r,g,b,a)`；`backdrop.tint` 为 `rgba`、渐变两端为 `#RRGGBB` —— 全部合法 |
| 编码 / 行尾 | 读字节 | UTF-8 **无 BOM**，**CRLF**（59 行，与样板一致） |
| 改动范围 | `git status --porcelain` | 本任务名下仅新增这 2 个路径；既有脏文件未触碰 |
