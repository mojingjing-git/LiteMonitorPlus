# A10 编辑器及预览接入 —— 实施结果

> 实施包：外观改造计划 v4 §5 **A10**（编辑器及预览接入）
> 权威计划：`docs/superpowers/plans/2026-10-20-appearance-agent-execution.md`（v4）
> 状态：代码与测试已落地；**完整预览效果待 A13 后验收**（v4 §5 A10 行原文要求）

---

## 1. 实际基线

| 项 | 值 |
|---|---|
| 仓库 | `G:\TOOL\LiteMonitor-fork` |
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da`（`97f793d merge: performance sampling hardening`） |
| 分支 | `master` |
| 主程序集 | `bin/Release/net8.0-windows/LiteMonitor++.dll`，SHA256 `1C19FB230425B7727A961D4EDF64480BD07FB9E0F822A58AB9BCA111073CA132` |
| 测试程序集 | `tests/LiteMonitor.Tests/bin/Release/net8.0-windows/LiteMonitor.Tests.dll`，SHA256 `2099E50DA165561170DEBA3EA276727AEC98755587EE438F8AFB345534BBD478` |
| 工作区状态 | 本任务开始前已存在 A01/A02/A08/A11/A12 等他人在途改动（含 `src/Core/ThemeManager.cs`、`src/ThemeEditor/ThemeFileService.cs`、新增 `resources/themes/CyberPunk_Mech.json`）。**本任务未改动这些文件** |

基线主题文件 SHA256（前 16 位，仓库 `resources/themes/`）：

```
0D098F3AA66EA7BC  AuroraDream_Purple.json
213D6F92AE3D0075  CyberBlue_Neon.json
F537514F2C75CFA3  CyberPunk_Mech.json      ← A12 在途新增（非本任务）
84B905210DE2FAE8  DarkFlat_Classic.json
619A4400D272CA74  DarkNeo_Modern.json
E5E3E7D0FFD38E76  DeepOcean_Blue.json
A365E91E2364B932  Dracula_Vampire.json
950DCCDEA5E3BA53  LightPure_Elegant.json
2E2038E6CE356D35  MatrixGreen_Terminal.json
7A347796A001B13F  Monokai_Pro.json
1D6182A337F22BCD  SolarAmber_Warm.json
```

---

## 2. 修改文件清单（严格限于独占写集）

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/ThemeEditor/AppearanceFields.cs` | **新增** | 外观字段编辑 UI + 取值/写回逻辑；alpha 语义；配置图深拷贝/恢复 |
| `src/ThemeEditor/ThemeEditorForm.cs` | 修改 | 接入"外观 / Appearance"页、取消语义、颜色页 alpha 编辑 |
| `src/ThemeEditor/ThemePreviewControl.cs` | 修改 | 改用 `CreatePreviewCopy`，按新字段渲染，固定样例图，零捕获 |
| `tests/LiteMonitor.Tests/ThemeEditorAppearanceTests.cs` | **新增** | 33 个自动化用例（xUnit，未改 csproj） |
| `docs/superpowers/plans/2026-10-20-appearance-a10-editor.md` | **新增** | 本文件 |

**未触碰**：`src/ThemeEditor/ThemeFileService.cs`（L-02 已落地的整合项）、`src/ThemeEditor/ThemeEditorForm.resx`（**未改**：新增页签与控件全部用代码创建，避开设计器/resx 风险）、`src/Core/**`、`src/UI/**`、`src/System/**`、`resources/themes/**`、`LiteMonitor.csproj`、`tests/LiteMonitor.Tests/` 其它文件、任何 NuGet 依赖。

源码行尾：三个 `.cs` 均为 **CRLF / 无 BOM**（与仓库既有文件一致）。

---

## 3. UI 交互说明（用户怎么用）

### 3.1 新增"外观 / Appearance"页签

编辑器的中间区域多了一个 **" 外观 / Appearance "** 页签，分四组：

| 分组 | 字段 | 缺省 |
|---|---|---|
| 窗口外框 / Frame | `FrameStyle`（无 / 切角 / 圆角描边 / 静态全息边框）、`FrameWidth`(0–8)、`FrameLength`(0–64) | 0 / 0 / 0 |
| 纹理与强度 | `Scanline`(0–100)、`Glow`(0–100)、`Grid`(0–100)、`BarStyle`（旧条 / 渐变 / 纹理） | 0 / 0 / 0 / 0 |
| 描边 / 辉光 / 次级文字色 | `Color.Border`、`Color.Shadow`、`Color.TextSecondary` | `null`（不写出该键） |
| 背景 / Backdrop | `Mode`（无 / solid / gradient / glass）、`Opacity`(0–1)、`Blur`(0–32)、`GradientAngle`、`Noise`、`EdgeGlow`、`Fps`、`Tint`、`GradientStart`、`GradientEnd` | 整个 `Backdrop` 缺省 = `null` |

- **每一行右侧都有 "缺省 / Default" 按钮**：点它只把该字段恢复缺省，其它字段不受影响。
- 背景模式选 **"无 / None"** 时，`Backdrop` 整个对象被移除（对应"可选对象缺失不自动保存一个默认实例"）；选 solid/gradient/glass 才创建 `Backdrop` 对象。
- 未知值（例如主题里写了 `frameStyle: 7` 或 `backdrop.mode: "liquid"`）会被原样保留，界面上以"未知"项列出，**不会被静默改写**。

### 3.2 颜色透明度（alpha）编辑

- **颜色页**每一行现在多了两个控件：颜色块右侧的 **`α` 数值框**（0–1）与 **"缺省"** 按钮。
- 改 `α` → 颜色文本按 `rgba(r,g,b,a)` 写回（`a=1` 时写回 `#RRGGBB`）；RGB 保持不变。
- 用颜色对话框挑新颜色时，**当前 alpha 会被保留**（只换 RGB）。
- 非法 alpha（越界 / NaN / Infinity）或颜色文本本身写坏时 → **拒绝并回退**，颜色文本保持原样，不会写出半合法的颜色。
- 外观页的 `Border` / `Shadow` / `TextSecondary` / `Tint` / `GradientStart` / `GradientEnd` 同样可编辑 alpha。

### 3.3 取消

- 左栏底部新增 **"取消 / Cancel"** 按钮：内存中的主题恢复到进入编辑器前的状态，然后关闭编辑器。
- 直接点窗口关闭按钮（X）**等价于取消**：走同一条恢复路径。
- 两条路径都**不写盘**：磁盘上的主题文件一个字节都不变。

### 3.4 保存

- 仍用各页原有的 **"保存主题 / Save Theme"** 按钮（唯一落盘入口）。
- 保存成功后会把"进入编辑器时的快照"更新为已保存内容（此后再点取消 = 恢复成已保存状态）。
- **恢复缺省后的文件表现**：因为模型层用了 `WhenWritingDefault` / `WhenWritingNull`，恢复缺省后这些键**不会再出现在 JSON 里**（`frameStyle` / `frameWidth` / `frameLength` / `scanline` / `glow` / `grid` / `barStyle` / `backdrop` / `border` / `shadow` / `textSecondary` 全部消失）。**这是正确行为**，不是丢数据。

### 3.5 预览

- 预览使用**独立布局副本**（`original.Layout.CreatePreviewCopy(dpiScale)`），主窗口/主题对象不受影响。
- 玻璃（glass）预览使用**固定样例图**（96×96 静态图案），画面左上角标注 **"预览样例图 / Preview sample（非实时桌面捕获）"**。
- 预览**不启动任何桌面捕获**。

---

## 4. API 与接线说明

### 4.1 新增公开类型（`namespace LiteMonitor.ThemeEditor`）

| 类型 | 成员 | 说明 |
|---|---|---|
| `AppearanceColorText`（static） | `TryParse(text, out r,g,b, out alpha)` | 支持 `#RRGGBB` / `#AARRGGBB` / `rgba(r,g,b,a)` / 颜色名；a 为 0–1 |
| | `TryGetAlpha(text, out alpha)` | 读当前 alpha |
| | `TrySetAlpha(current, alpha, out newText)` | 改写 alpha；**非法值返回 false 且不改文本**（拒绝并回退） |
| | `Format(r,g,b,alpha)` | `alpha>=1` → `#RRGGBB`；否则 → `rgba(r,g,b,a)`（InvariantCulture） |
| | `ToAlphaByte(alpha)` | alpha×255，`AwayFromZero` 舍入（0.5 → 128） |
| `AppearanceThemeConfig`（static） | `CloneConfig(theme)` | 深拷贝配置图（Layout/Font/Thresholds/Color/Backdrop/扩展数据），**不含运行期字体** |
| | `RestoreConfig(target, snapshot)` | 就地恢复（对象引用不变）→ 取消语义 |
| | `CloneColor(color)` | 深拷贝颜色配置（预览"透明背景变体"用） |
| `AppearanceAlphaEditor` | `Alpha`、`HasValidColor`、`SetColorText(text)`、`AlphaChanged` 事件 | alpha 输入控件 |
| `AppearanceFields`（Panel） | `Bind(theme)` / `ApplyToTheme()` / `ResetField(key)` / `ResetAllToDefaults()` / `ValueChanged` 事件 | 外观字段面板；缺省键名见 `KeyFrameStyle`…`KeyTextSecondary` 常量 |
| `AppearanceComboItem`（internal） | — | 组合项（显示文本 + 内部值） |

### 4.2 `ThemePreviewControl` 新增/变更

| 成员 | 变更 |
|---|---|
| `static Theme CreatePreviewTheme(Theme original, float dpiScale)` | **新增**。布局 = `original.Layout.CreatePreviewCopy(dpiScale)`（独立副本 + 只缩放一次）；Color/Thresholds/Backdrop/FontConfig 只读借用；运行期字体**只借用不释放**；主题级扩展数据一并复制 |
| `static void RenderPreview(Graphics, List<GroupLayoutInfo>, Theme, Rectangle, BackdropRenderer?, Image?, float)` | **新增**。层次：背景层 → 底纹 → 内容 → 向内外框 → 预览提示。静态可测 |
| `static List<GroupLayoutInfo> CreateMockGroups()` | **新增**（原 `BuildMockData` 的静态版） |
| `static Bitmap CreatePreviewSample()` | **新增**。玻璃预览用固定样例图（静态图案，非捕获） |
| `SetTheme(Theme)` | 改为走 `CreatePreviewTheme`；DPI 取 `DeviceDpi` |
| `Dispose(bool)` | 只释放自己创建的 `_cachedBitmap` / `_previewSample` / `_backdrop`；**绝不释放借用的原主题字体** |
| 已删除 | `CreateScaledTheme` / 实例版 `CreatePreviewTheme`（手写 `new LayoutConfig {...}` 逐字段复制）—— **按合同移除** |

### 4.3 `ThemeEditorForm` 变更

| 位置 | 变更 |
|---|---|
| `BuildUI` | 新增"外观 / Appearance"页签 + `AppearanceFields`；左栏底部新增"取消 / Cancel"；`appearanceFields.ValueChanged` → 刷新预览 |
| `LoadSelectedTheme` | 载入后立即 `_pristineTheme = AppearanceThemeConfig.CloneConfig(_theme)`（内存快照）并 `appearanceFields.Bind(_theme)` |
| `CancelEditing` / `RestorePristineTheme` / `OnFormClosing` | 取消路径：内存恢复 + 不写盘 |
| `SaveCurrentTheme` | 保存成功后刷新快照（后续取消 = 恢复成已保存状态） |
| `BuildColorsPage` | 每个颜色行增加 `α` 输入框与"缺省"按钮；挑色时保留 alpha；新增 Border / Shadow / TextSecondary 三行 |

### 4.4 A13 之后"完整预览"还需要补什么（交接项）

1. **辉光（Glow）**：目前外观页可编辑、可保存，但**预览不绘制**（A07/A13 的正式辉光渲染器尚未接入）。预览只画了外框。
2. **新进度条样式（BarStyle=1/2）**：目前**预览不反映**（`UIUtils.DrawBar` 仍在 `src/UI/**`，非本任务写集；A07 完成后接入）。
3. **扫描线 / 网格**：预览目前是**假绘制**（`ThemePreviewControl.DrawPreviewTexture`，水平线 + 网格线），A07 的 `DecorRenderer` 完成后应替换为正式实现（层次要求：底纹必须早于文字，不能盖住数值）。
4. **字体 DPI**：预览**借用**原主题字体，不按预览 DPI 重建字号（合同要求"字体只借用，不释放"）。跨 DPI 时预览字号与主窗口不一致，属已知限制。
5. **背景层与内容的合成**：预览用"透明背景变体"（`Color.Background` 置空 → `UIUtils.GetBrush("")` 返回透明画刷 → `UIRenderer` 的背景铺底变空操作）让背景层/底纹透出。A13 接线主面板时应在 `UIRenderer` 侧正式处理"背景由 BackdropRenderer 负责"，而不是靠这个变体。
6. **玻璃实时效果**：预览恒为固定样例图；真实实时材质仍待 A06/A15/A16。

---

## 5. 测试命令与退出码

```
# 1) 主工程构建
dotnet build LiteMonitor.csproj -c Release --nologo -v minimal
EXIT=0    （0 个错误；警告为仓库既有 nullable 警告）

# 2) A10 测试类
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --filter "FullyQualifiedName~ThemeEditorAppearanceTests"
EXIT=0    已通过! - 失败: 0，通过: 33，已跳过: 0，总计: 33，持续时间: 2 s

# 3) 全量测试（并行环境下执行一次）
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release
EXIT=1    失败: 1，通过: 575，已跳过: 0，总计: 576
```

**全量里唯一的失败与本任务无关**（详见 §7 未验证项 / 已知限制）：
`ThemeSchemaTests.旧主题逐个往返_已知语义与未知值都保持` —— 该用例遍历 `resources/themes/*.json` 并断言"旧主题一律没有新的效果开关"（`Assert.Equal(0, again.Layout.FrameStyle)`）。A12 在途新增的 `resources/themes/CyberPunk_Mech.json` 带 `frameStyle=1`，因此该断言失败（实际值 1）。该用例遍历到新主题时失败，与 ThemeEditor 无关；`ThemeSchemaTests.cs` 与 `resources/themes/**` 均不在本任务写集，未做改动。逐文件核对：10 个原始主题 `frameStyle` 均为空（=0），只有 `CyberPunk_Mech.json` = 1。

---

## 6. 用例覆盖与实测数字

| # | 验收项 | 用例 | 结果 |
|---|---|---|---|
| 1 | 预览副本逐字段相等（含 7 个新字段 + Backdrop），扩展数据不丢 | `预览副本_布局逐字段相等且含全部新外观字段`（**反射逐属性比对** LayoutConfig 全部公开属性）、`预览副本_扩展数据不丢` | ✅ |
| 2 | 已缩放源不二次缩放 | `预览副本_已缩放的源不会被二次缩放`（LayoutScale 1.5 保持 1.5，RowHeight/Padding 不再乘 2.0）、`预览副本_未缩放的源只缩放一次`（1.0→1.5 只乘一次） | ✅ |
| 3 | 字体只借用、不释放 | `预览副本_字体只借用不释放`（`Assert.Same` + 原字体 `Height>0` 仍可用）、`预览控件_释放后不释放原主题字体`（释放预览控件后原主题字体仍可用） | ✅ |
| 4 | alpha 语义 | `alpha_写回后ParseColor得到的alpha字节正确`（0.5→**128**、1→**255**、0→**0**、0.25→64、0.1→26）、`alpha_1写成六位十六进制_0写成rgba()`、`alpha_非法值被拒绝并回退`（-0.1/1.1/NaN/±Infinity）、`alpha_颜色文本本身非法时同样被拒绝`、`alpha_保存后读回仍是128` | ✅ |
| 5 | 取消 | `取消_磁盘文件逐字节不变且内存主题恢复原状`（**SHA256 逐字节比对** + 内存字段复原）、`取消_编辑器关闭后内存主题恢复原状` | ✅ |
| 6 | 恢复缺省 | `恢复缺省_写回值与规范一致且默认键不再写出`（写回值全为 0 / `Backdrop=null` / 三色 `null`；JSON 里 11 个默认键**全部不出现**）、`恢复缺省_单字段恢复只影响该字段` | ✅ |
| 7 | A→B 只改 B | `从A换到B_只修改B_A的文件逐字节不变`（A 的 SHA256 前后一致；B 的 SHA256 变化且内容是 B 自己的）、`从A换到B_保存A不会影响B` | ✅ |
| 8 | 初始化不写回 | `打开编辑器并切换预览主题后关闭_所有主题文件逐字节不变`（真实构造 `ThemeEditorForm` → Show → 切主题 → Close，比对 `ThemeManager.ThemeDir` 全目录 `文件名\|SHA256` 快照）、`列出主题与加载主题_不会改动任何主题文件` | ✅ |
| 9 | 预览零捕获 | `预览相关源码不含任何抓屏调用`（对 `ThemePreviewControl.cs` + `AppearanceFields.cs` 扫描 `SetWindowDisplayAffinity` / `EXCLUDEFROMCAPTURE` / `WDA_*` / `CopyFromScreen` / `BitBlt` / `PrintWindow` / `Graphics.FromHwnd` / `Timer` / `Task.Run` / `Thread(`）、`预览渲染_不注册实时背景后端`（`BackdropRenderer` 无实时帧来源，glass 走预览固定源，`LastStatus.EffectiveMode == Glass`） | ✅ |
| 10 | 旧主题不被破坏 | `现有旧主题在预览里能正常渲染`（遍历内置主题逐个渲染，断言画面有可见像素）、`旧主题缺少新字段时预览副本补缺省值` | ✅ |

**实测数字**

- A10 测试：**33 / 33 通过**（退出码 0），耗时 ~2 s。
- 全量：**575 通过 / 1 失败**（失败项归属他人，见 §5）。
- 预览渲染实测像素（证据图采样）：
  - 外观全开 + gradient：组块填充 `R43 G45 B49`（= `#2B2D31`，**不透明，扫描线未穿透到文字区**）；外框像素 `R64 G255 B128`（= `#40FF80`，即 `Color.Border`）；组块间空隙显示渐变 + 扫描线。
  - glass：固定样例图的色块出现在组块之间的空隙处，左上角有"预览样例图 / Preview sample（非实时桌面捕获）"提示。
- 内置主题数：**11**（10 个旧主题 + A12 在途新增的 `CyberPunk_Mech.json`）。

---

## 7. 证据路径

| 证据 | 路径 |
|---|---|
| 预览证据图（3 张 PNG，由 `预览渲染_产出证据图` 生成） | `%TEMP%\LiteMonitor.Tests\A10-evidence\` → `preview-legacy.png` / `preview-full-appearance.png` / `preview-glass.png`（实测位于 `C:\Users\wrz20\AppData\Local\Temp\LiteMonitor.Tests\A10-evidence\`） |
| A10 测试源码 | `tests/LiteMonitor.Tests/ThemeEditorAppearanceTests.cs` |
| 主工程二进制 | `bin/Release/net8.0-windows/LiteMonitor++.dll`（SHA256 见 §1） |
| 测试工程二进制 | `tests/LiteMonitor.Tests/bin/Release/net8.0-windows/LiteMonitor.Tests.dll`（SHA256 见 §1） |

---

## 8. 未验证项与已知限制

1. **全量测试里 `ThemeSchemaTests.旧主题逐个往返_已知语义与未知值都保持` 失败**：由 A12 在途新增的 `CyberPunk_Mech.json`（`frameStyle=1`）触发，用例把"目录里所有主题"当成"旧主题"。该文件与该测试都不在本任务写集，**未做改动**，需由主集成者决定（把新主题排除在该用例外，或把断言改成"旧主题子集"）。
2. **未做真机 UI 走查**：本任务在无头/自动化环境完成，编辑器的页签布局、按钮位置、滚动条表现**未做人工视觉确认**（只做了逻辑与渲染层验证）。建议主集成者在有界面环境打开编辑器人工过一遍"外观"页与"取消"按钮。
3. **预览的辉光与新进度条样式不反映**（A07/A13 待接），扫描线/网格为假绘制（见 §4.4）。
4. **预览不重建字体**：跨 DPI 时预览字号与主窗口不一致（合同要求"字体只借用，不释放"，故未重建）。
5. **初始化阶段存在一次"非主题文件"的磁盘动作**：`ThemeFileService.ListThemes()` 会清理**超过 10 分钟未更新的 `*.litemonitor-tmp` 残留临时文件**（A02 的设计）。它**不会改动任何 `.json` 主题文件**（已用全目录 SHA256 快照断言），但严格来说是"打开编辑器时的一次目录写动作"。该逻辑在 `ThemeFileService`（非本任务写集），未做改动。
6. **未验证**：真实玻璃（实时材质）、跨屏 DPI 切换后的预览刷新、编辑器的键盘/高 DPI 布局表现。完整预览效果按 v4 §5 A10 要求**留待 A13 后验收**。
7. **构建并发**：本任务在同机并行 `dotnet build`/`dotnet test` 环境下执行；期间未出现 obj/ 占用导致的失败（首次构建 2 个编译错误为本次自身笔误，已修复）。未删除任何 `obj/`、`bin/`。
