# GPU 材质主题视觉规范

本文规定 GPU 渲染路线下的材质语义、配色、几何参数、排版阶梯与动效参数。

材质参数由主题 JSON 的 `rendering.parameters` 与 `rendering.motion` 提供，运行时由 `MaterialRuntimeSettings` 归一化为带取值域的设定值：**缺键、非数值、非有限值一律回落到默认值；超出取值域的值被夹取到边界**。主题文件里写了多少参数不重要——只有能被 `MaterialRuntimeSettings` 识别的键才生效。

显式字符串 `rendering.parameters.visualPreset` 选择命名外观分支，但它不替代材质：`nebulaAurora` 使用 `liquid_glass`，`nixieBrass` 与 `crtPhosphor` 使用 `cyber_punk`。键缺失时保持原材质行为；显式 `default` 也必须逐像素保持原材质结果。

材质参数与渲染路线的整体契约见 [../../architecture/rendering.md](../../architecture/rendering.md)。

---

## 1. 设计与素材边界

GPU 主题共用同一套场景、几何、文字与资源合同，但材质、色板、层级与动效分别实现。参考公开设计文档只用于方向，不复制品牌控件、图标或截图；原型背景、纹理与状态图由程序生成或使用原创图形。

不采用的默认表现：

- 所有线条同时发光或所有文字加辉光。
- 大量色散、彩虹边、水波和持续抖动。
- 大面积扫描线、网格、噪点铺满整个面板。
- 用静态效果图冒充实时 GPU 结果。
- 用缩小字号把多卡内容塞进窄面板。

---

## 命名视觉预设

命名预设与 CPU 兼容外观共用同一份主题文件。GPU 分支读取主题 `color.*` 的基色、面板、边框与强调色，并从 `font.*` 构造正文样式；CPU 分支读取 `rendering.fallbackStyle` 的六槽。固定输入是硬约束：实时捕获会话数必须为 0，Nebula 还禁止 `floatingToolbar`。

| 预设 | 适用材质 | GPU 结构门 | CPU 兼容外观 |
|---|---|---|---|
| `nebulaAurora` | `liquid_glass` | 内容区同时存在青色与紫色极光色带；空白带可见稀疏星点；玻璃分组框带冷色边缘 | 深靛背景、蓝紫面板、青色边框、薄荷绿强调与粉橙状态色 |
| `nixieBrass` | `cyber_punk` | 深炭底、暖白正文；至少三条横向黄铜带；四角铆钉；橙色热光与固定 3 px 水平线 | 暖黑背景、棕色面板、黄铜边框、暖白正文与橙红状态色 |
| `crtPhosphor` | `cyber_punk` | P1 绿主色；纵向 3 px 扫描周期；中心比角落更亮；终端框和低圆角 | 墨绿背景、深绿面板、绿边框与正文、琥珀警告、红色危险 |

`default` 分支必须继续调用原 `FixedBackdrop`、`GlassRoot`、`CyberRoot` 与 `AddGroup` 算法。验收使用同设备、固定输入、`animationSeconds=0` 的 decoded ARGB 哈希与逐像素比较；显式 `default` 与旧主题的差异必须为 0。

---

## 2. Liquid Glass

### 2.1 视觉意图

面板像一块有厚度和边缘曲率的透明玻璃。背景保持可辨认，中心只做轻度散射；靠近轮廓的位置产生连续透镜位移和强度不同的边缘高光。文字与告警完全位于折射材质之外，不闪烁、不重影。

### 2.2 色板

| Token | 值 | 用途 |
|---|---|---|
| `glass.surface` | `#B8D9F5 @ 8%` | 极轻冷色表面 |
| `glass.edgeCool` | `#D8F2FF` | 左上冷高光 |
| `glass.edgeWarm` | `#FFF2D9` | 右下暖反射 |
| `glass.shadow` | `#07111C @ 38%` | 外轮廓阴影 |
| `glass.text` | `#F7FBFF` | 主文字 |
| `glass.textMuted` | `#B9C8D7` | 单位、次级文字 |
| `glass.normal` | `#58E5C2` | 正常值 |
| `glass.warning` | `#FFC857` | 警告 |
| `glass.danger` | `#FF6B73` | 危险 |

### 2.3 几何与材质参数

默认值分为「标准玻璃」与「浮动工具条」两种 `glassStyle`：

| 参数 | 标准玻璃 | 浮动工具条 | 取值域 | 含义 |
|---|---|---|---|---|
| `cornerRadius` | 26 | 30 | 0 – 64 | 圆角半径（logical px） |
| `thickness` | 1 | 8 | 0 – 8 | 材质厚度，影响边缘透镜位移强度 |
| `refraction` | 1.6 | 8 | 0 – 8 | 折射位移，边缘最大位移量 |
| `blurRadius` | 8 | 8 | 0 – 32 | 模糊核半径 |
| `clearCenterRatio` | 0 | 0 | 0 – 1 | 中心保留清晰度的比例，0 表示中心也参与模糊 |
| `surfaceOpacity` | 0.055 | 0.055 | 0 – 1 | 表面层不透明度 |
| `highlight` | 1 | 0.72 | 0 – 1 | 主高光强度 |
| `counterHighlight` | 1 | 1 | 0 – 1 | 反向高光强度 |
| `shadow` | 0.34 | 0.115 | 0 – 1 | 轮廓阴影强度 |
| `fogStrength` | 0.42 | 0.16 | 0 – 1 | 雾化强度 |
| `tintStrength` | 跟随 `surfaceOpacity` | 0.038 | 0 – 1 | 色调强度；标准玻璃下缺省时取 `surfaceOpacity` |
| `shadowRadius` | 0 | 30 | 0 – 64 | 阴影扩散半径 |

`glassStyle` 取 `standard` 或 `floatingToolbar`；非字符串或未知取值回落到 `standard`。

### 2.4 布局

- 标题行保持单一基线；CPU/GPU 主数值尺度最高。
- 分组标题与数值之间至少保留 8 logical px 间隔。
- 圆角片、完整监控分组、真实数值面板三块内容必须同屏出现。
- 窄面板先隐藏低优先级单位或折行，不整体缩小字体。

### 2.5 动效

| 参数 | 默认值 | 取值域 | 含义 |
|---|---|---|---|
| `motionEnabled` | `true` | — | 材质动效总开关 |
| `targetFps` | 30 | 1 – 240 | 目标帧率 |
| `stateTransitionMs` | 180 | 0 – 5000 | 状态变化进入稳定值的时长 |

- 边缘折射随窗口位置与背景采样变化，材质本身不循环闪烁。
- 状态变化高光在 `stateTransitionMs` 内进入稳定值。
- 拖动期间保持同一材质代次，只更新裁切与位置。
- 关闭动效后，静态画面仍必须能辨认玻璃、文字与告警。

---

## 3. Cyberpunk

### 3.1 视觉意图

深色工业仪表面板，主框体、连接件、分组槽与数据区形成明确结构。冷青主灯承担信息层级，琥珀用于焦点与警告，红色仅用于危险。霓虹是局部材质层，正文使用干净前景。

### 3.2 色板

| Token | 值 | 用途 |
|---|---|---|
| `cyber.base` | `#0A1017` | 工业底板 |
| `cyber.panel` | `#121B24` | 次级槽体 |
| `cyber.line` | `#345064` | 非发光结构线 |
| `cyber.cyan` | `#48E6F2` | 冷色主灯、正常状态 |
| `cyber.amber` | `#FFB84D` | 焦点、警告 |
| `cyber.red` | `#FF4967` | 危险 |
| `cyber.text` | `#E8F7FA` | 主文字 |
| `cyber.textMuted` | `#91A7B2` | 单位、辅助文字 |

### 3.3 几何与材质参数

| 参数 | 默认值 | 取值域 | 含义 |
|---|---|---|---|
| `primaryCut` | 14 | 0 – 64 | 主框切角（logical px） |
| `secondaryCut` | 9 | 0 – 32 | 次级切角 |
| `outlineWidth` | 1 | 0 – 8 | 结构线宽度 |
| `glow` | 12 | 0 – 100 | 近光晕半径 |
| `ambientRatio` | 0.12 | 0 – 1 | 环境反射强度上限 |
| `panelOpacity` | 0.965 | 0 – 1 | 底板不透明度 |
| `shadow` | 0.34 | 0 – 1 | 轮廓阴影强度 |

材质为 `cyber_punk` 时，玻璃族的参数（`cornerRadius`、`thickness`、`refraction`、`blurRadius`、`clearCenterRatio`、`fogStrength`、`tintStrength`、`shadowRadius`）固定为 0，不随主题配置变化。

### 3.4 布局

- 面板至少含 CPU、GPU、双列 NET/DISK、进度条，以及正常与警告状态。
- 单卡与多卡共享同一视觉规则，多卡不得把文字压缩到不可读。
- 窄面板删减低优先级单位与装饰，不用统一缩放塞入。
- 关闭动效后仍保留完整框体、分级与状态区分。

### 3.5 动效

| 参数 | 默认值 | 取值域 | 含义 |
|---|---|---|---|
| `motionEnabled` | `true` | — | 材质动效总开关 |
| `targetFps` | 30 | 1 – 240 | 目标帧率 |
| `stateTransitionMs` | 140 | 0 – 5000 | 数值变化的亮度反馈时长 |
| `scannerPeriodMs` | 2800 | 0 – 60000 | 扫光周期 |

- 扫光只沿局部进度条或状态槽运动，禁止全屏扫光。
- 数值变化使用一次亮度反馈，文字位置不变。
- 告警使用有限频率脉冲，静帧仍可辨识。
- 不使用持续文本抖动、随机故障剪切或无意义闪烁。

---

## 4. 共用排版

| 角色 | 首选字体 | 回退 | 许可/备注 |
|---|---|---|---|
| UI 正文 | Segoe UI Variable | Segoe UI | Windows 随附；缺字体时回退 |
| 数值等宽 | Cascadia Mono | Consolas | 回退 Consolas |

字号阶梯（100% DPI logical px）：

| 级别 | 字号 | 用途 |
|---|---|---|
| Display | 34 | GPU/CPU 主数值 |
| Title | 18 | 分组标题 |
| Body | 14 | 标签 |
| Caption | 11 | 单位、状态 |

100%/150%/175%/200% DPI 使用同一逻辑阶梯，只在提交到物理像素时统一映射一次。

---

## 5. Legacy 材质

`legacy` 材质不使用 GPU 参数：所有材质参数固定为 0（`surfaceOpacity`、`highlight`、`counterHighlight`、`panelOpacity` 固定为 1），动效关闭，目标帧率固定 30。主题声明 `backend = "gpu"` 但回退到 legacy 时，使用主题的 `rendering.fallbackStyle` 提供配色。

---

## 6. 固定场景

用于材质回归对比的固定输入包含：

- 深色渐变、浅色渐变、彩色块、细字与棋盘格五类背景区域。
- CPU 62%、GPU 78%、NET 24.8 MB/s、DISK 41%。
- 正常、警告、危险三种值，以及缺失传感器状态。
- 单卡与多卡两种场景。
- 1280x800、720x800、1280x420 三种窗口形态。
- 时间与随机种子固定，重复渲染的哈希必须可比较。

机器可读的令牌定义见同目录 [tokens.json](tokens.json)。
