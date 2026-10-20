# A13 —— M1 主面板集成接线（结果文档）

> 本文是 v4 执行计划 `2026-10-20-appearance-agent-execution.md` 中 **A13** 包的结果记录。
> 定位用 `文件路径 → 类型名 → 方法名`，**不引用行号**。

---

## 1. 结论

A13 把 M0 组件层（装饰层 / 背景层 / 进度条层 / 窗口轮廓控制器）**接进主面板**，
并保证"新能力全关时画面与改造前逐像素一致"。

| 项 | 结果 |
|---|---|
| 主工程编译 | **0 错误** / 234 警告（警告全部为改动前既有代码，无新增） |
| 单元测试 | **577 / 577 通过** |
| A13 接线自证探针 | **69 / 69 通过** |
| 外观基线重采对比 | 原有 **336 个夹具逐像素 0 差异**；32 个新增来自 `CyberPunk_Mech` 主题 |
| 旧主题观感 | **逐像素不变**（新入口 vs 旧入口，60 组用例全部 0 差异） |

**接线过程中发现并修复了一个会让新背景完全失效的层次缺陷**（见 §4）。

---

## 2. 改动清单

### 2.1 新增

| 文件 | 类型 → 方法 | 职责 |
|---|---|---|
| `src/UI/AppearanceRuntime.cs` | `AppearanceRuntime` | **唯一**外观宿主：共享一个 `DecorResourceHost`；`Update` / `OnThemeChanged` / `OnSizeChanged` / `Dispose` |
| `tests/AppearanceWiringProbe/**` | 探针工程 | A13 接线自证探针（**不进 sln**） |

`AppearanceRuntime` 的关键语义：

- `Update(theme, dpiScale, userScale, clientBounds)` —— 主题 / DPI / 用户缩放 / 客户区**全都未变**时
  **复用上一次的快照**（热路径零分配）；任一项变了才重建。
- `OnThemeChanged(...)` —— 主题代次自增 + 各层缓存按代次失效 + 强制重建快照。
- 背景帧与预览源是**借用**引用，`Dispose` 不碰它们。

### 2.2 绘制层

| 文件 | 类型 → 方法 | 改动 |
|---|---|---|
| `src/UI/UIRenderer.cs` | `Render(Graphics, List<GroupLayoutInfo>, Theme)` | **旧入口保留原签名**，转调新重载并传 `appearance = null` |
| `src/UI/UIRenderer.cs` | `Render(Graphics, List<GroupLayoutInfo>, Theme, AppearanceRuntime?)` | 新入口，插入 v4 §3.3 固定的层次 |
| `src/UI/UIRenderer.cs` | `DrawGroupBackground` → 拆为 `DrawGroupFill` + `DrawGroupTitle` | 让装饰层能插在"组块填充"与"组标题"之间 |
| `src/UI/UIRenderer.cs` | `DrawStandardItem` | 进度条改走 `BarRenderer.RenderBar` |
| `src/UI/UIRenderer.cs` | `BuildGroupRegions`（新增） | 把分组布局转成装饰层需要的组块区域列表 |
| `src/UI/HorizontalRenderer.cs` | `Render(Graphics, Theme, List<Column>, int)` | **旧入口保留原签名** |
| `src/UI/HorizontalRenderer.cs` | `Render(Graphics, Theme, List<Column>, int, AppearanceRuntime?)` | 新入口：背景层 + 窗口底纹 + 外框 |
| `src/UI/HorizontalRenderer.cs` | — | 横屏**不**画组装饰（横屏模型是列，没有组块分组） |

**固定层次**（竖屏）：

```
背景层 → 底板 → 窗口底纹 → 主标题 → 组块填充 → 组底纹/组框 → 组标题 → item → 窗口边框/辉光
```

### 2.3 上下文与换肤

| 文件 | 类型 → 方法 | 改动 |
|---|---|---|
| `src/UI/UIController.cs` | `ApplyTheme` | 重建外观快照（代次自增）+ `WebThemeColorPublisher.PublishFromTheme(t)` + 触发 `AppearanceChanged` |
| `src/UI/UIController.cs` | `Render` | 每帧 `UpdateAppearanceContext`（客户区尺寸会变） |
| `src/UI/UIController.cs` | `UpdateAppearanceContext` | 每帧同步上下文（尺寸不变则复用快照） |
| `src/UI/UIController.cs` | `SyncAppearanceContext`（新增） | 用**当前**客户区尺寸同步一次并返回快照 |
| `src/UI/UIController.cs` | `AppearanceChanged`（新增事件） | 换肤 / DPI 变化后通知窗口轮廓重应用 |
| `src/UI/UIController.cs` | `Appearance` / `AppearanceOptions`（新增） | 对外暴露外观运行时与当前快照 |

### 2.4 窗口外观生命周期

| 文件 | 类型 → 方法 | 改动 |
|---|---|---|
| `src/UI/MainForm_Transparent.cs` | 构造函数 | 创建 `WindowAppearanceController` 并挂到 helper |
| `src/UI/MainForm_Transparent.cs` | `BindEvents` | Resize 绑定**收敛为唯一一处**；`HandleCreated` → `OnHandleCreated()`；订阅 `AppearanceChanged` |
| `src/UI/MainForm_Transparent.cs` | `ApplyWindowAppearance`（新增） | 用当前快照重新应用轮廓（DWM 角偏好 + Region） |
| `src/UI/MainForm_Transparent.cs` | `OnDpiChanged` | 换肤后重应用轮廓 |
| `src/UI/MainForm_Transparent.cs` | `OnShown` | 首次显示时正式应用轮廓 |
| `src/UI/MainForm_Transparent.cs` | `OnHandleDestroyed`（新增 override） | 句柄销毁后清窗口级状态 |
| `src/UI/MainForm_Transparent.cs` | `OnFormClosed` | 释放窗口外观控制器 |
| `src/UI/Helpers/MainFormWinHelper.cs` | `AttachWindowAppearance`（新增） | 挂上 A09 控制器 |
| `src/UI/Helpers/MainFormWinHelper.cs` | `ApplyRoundedCorners` | **改为转发**给 A09 控制器（不再自己写 DWM / Region） |
| `src/UI/Helpers/MainFormWinHelper.cs` | `ApplyLegacyRoundedCorners`（新增） | 控制器未挂上时的兼容回退 |
| `src/UI/Helpers/MainFormWinHelper.cs` | `InitializeStyle` | **删除**其中的 Resize 绑定 |

### 2.5 公开签名保留

```
src/UI/UIRenderer.cs        public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t)
src/UI/HorizontalRenderer.cs public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth)
src/UI/TaskbarRenderer.cs   public static void Render(Graphics g, List<Column> cols, bool light)
```

三者签名**一字未动**，只做重载。旧签名转调新重载并传 `appearance = null` → 逐像素等同改造前。

---

## 3. 硬约束落实对照

| # | 冻结合同 | 落实方式 | 证据 |
|---|---|---|---|
| 1 | `dpiScale` = 当前窗口 `DeviceDpi/96`，禁止 `UIUtils.ScaleFactor` | `UIController.GetCurrentDpiScale()`（`g.DpiX/96f`）；`AppearanceRuntime` 的入参即此值 | 探针用例 ⑥ |
| 2 | `FrameWidthPx`/`FrameLengthPx`/`BlurPx` 是物理像素，**不得再乘缩放** | 绘制层与轮廓层都直接取 `options.FrameLengthPx`；`ThemeAppearanceOptions` 用 `effective = dpi×user` 一次性换算 | 探针用例 ⑧（切角边长 == `options.FrameLengthPx`） |
| 3 | 消费 `BorderColor`/`ShadowColor` 前先判 `FrameEnabled`/`GlowEnabled` | 由 A07 `DecorRenderer` 内部保证；旧主题下这些颜色**根本不会被读** | 探针用例 ③⑤ |
| 4 | `BackgroundFrame`/`PreviewSource` 是**借用**引用，双方都不释放 | `AppearanceRuntime.Dispose` 只释放装饰资源；`BackdropRenderer` 不碰借用帧 | 探针用例 ⑤（旧主题 `BackgroundFrame` 为空） |
| 5 | 归一化只作用于运行时副本，**绝不回写用户 JSON** | `AppearanceRuntime` 不修改传入的 `Theme`；`ThemeAppearanceOptions.Create` 只读 | 旧主题 JSON 的 SHA256 未变（基线对比 10/10） |
| 6 | 绘制入口用 `Graphics.Save()/Restore()` 隔离状态 | 由 A07 / A08 内部保证 | 探针 69/69 |

### 缩放语义复核（无双重缩放）

- `Theme.Scale(dpiScale, userScale)` → `LayoutScale = dpi × user`；10 个**旧**字段被缩放，
  7 个**新**字段（`frameStyle`/`frameWidth`/`frameLength`/`scanline`/`glow`/`grid`/`barStyle`）**故意不缩放**。
- `ThemeAppearanceOptions.Create` 用**同一系数** `effective = dpi × user` 把新字段换算成物理像素。
- `UIController.ApplyTheme` 用 `dpiScale = GetCurrentDpiScale()` + `userScale = (float)_cfg.UIScale`
  调用 `t.Scale(dpiScale, userScale)`，并把**同一对值**存进 `_appearanceDpiScale` / `_appearanceUserScale`，
  交给 `AppearanceRuntime` → **不存在两次缩放**。

### 圆角半径同源（v4 §3.4「切角使用与画框相同几何」）

| 用途 | 取值 |
|---|---|
| 绘制层外框 | `UIRenderer.Render` → `Decor.RenderWindowFrame(g, options, t.Layout.CornerRadius)` |
| 窗口轮廓 | `MainForm_Transparent.ApplyWindowAppearance` → `controller.Apply(options, t.Layout.CornerRadius)` |
| 切角边长 | 两处都用 `options.FrameLengthPx`（物理像素，**不再乘缩放**） |

`layout.cornerRadius` 已被 `Theme.Scale` 按 `LayoutScale` 缩放，**已是物理像素**，与 `FrameLengthPx` 同单位。

---

## 4. 接线中发现并修复的缺陷：底板没有让位

### 现象

背景层画完之后，**旧的实心底板填充**紧接着又把整块客户区涂了一遍：

- 竖屏 `UIRenderer.Render`：`FillRectangle((-5, -5, VisibleClipBounds+10))`
- 横屏 `HorizontalRenderer.Render`：`FillRectangle((0, 0, panelWidth, panelHeight))`

结果：**渐变 / 玻璃帧被整个盖掉，等于新背景白画**。

### 修复

读取背景层返回的 `BackdropStatus.EffectiveMode`：

- `Solid`（旧主题 / 未开启新背景）→ 背景层**零绘制**，底板仍走旧路径 → **旧主题像素不变**；
- 非 `Solid`（渐变 / 玻璃）→ 背景层自己已铺了**不透明底板**（A08 的 `DrawBasePlate`），
  旧的实心底板填充**必须让位**。

### 像素证据（探针用例 ③）

| 档位 | 客户区像素 | 旧画面底板色残留 | 新画面底板色残留 |
|---|---|---|---|
| 96 DPI / 1× | 197280 | 53892 | **0** |
| 120 DPI / 1× | 306900 | 84759 | **0** |
| 144 DPI / 2× | 1775520 | 508320 | **0** |
| 192 DPI / 2× | 3156480 | 911236 | **0** |

新画面**一个底板色像素都不剩** → 渐变确实铺满了整个客户区。
旧画面那一大片底板色是对照组，说明断言不是恒真的。

---

## 5. 接线不变量（探针用例 ⑤–⑧）

| 用例 | 断言 | 结果 |
|---|---|---|
| ⑤ 旧主题外观快照不变量 | 10 个旧主题：所有新开关关、`BarStyle` 为旧条、`BackdropMode` 为 `solid`、`BackgroundFrame` 为空、`ClientBounds` 与传入一致 | ✅ |
| ⑥ 快照复用与失效 | 参数不变时连续 100 次 `Update` 返回**同一个**快照对象；尺寸 / DPI 变化时**必须重建**；尺寸变回来也不复用旧快照 | ✅ |
| ⑦ 换肤代次自增 | 每次 `OnThemeChanged` 代次 +1 | ✅ |
| ⑧ 窗口轮廓接线行为 | 旧主题 → `DWM 角偏好 = 2 (ROUND)`；切角 → `1 (DONOTROUND)` 且 **Region 已设置**、切角边长 == `options.FrameLengthPx`；尺寸变化后重应用保持切角；切回旧主题恢复 `ROUND(2)` | ✅ |

### 窗口轮廓的两处结构性修复

1. **写入权收敛**：`MainFormWinHelper.ApplyRoundedCorners` 从"自己写 DWM / Region"改为**转发**给
   A09 的 `WindowAppearanceController`。此前两者并存会互相打架。
2. **Resize 绑定收敛为唯一一处**：删掉 `MainFormWinHelper.InitializeStyle` 里的
   `_form.Resize += (_, __) => ApplyRoundedCorners();`，只保留 `MainForm_Transparent.BindEvents` 里的那一处
   （此前两处绑定 → 轮廓被重建两遍）。

### Region 悬空引用修复

旧代码是：

```
_form.Region?.Dispose();   // ← 提前释放
_form.Region = new Region(gp);
```

赋给 `Control.Region` 的实例由 **WinForms 持有**，提前释放会造成悬空引用。
现在主路径由 A09 控制器接管（Region 所有权归 WinForms，组件主动释放数恒为 0），
兼容回退路径也改成了"先赋值，让 WinForms 在赋值时释放它自己持有的旧实例"。

---

## 6. 验证结果

### 6.1 A13 接线自证探针（`tests/AppearanceWiringProbe`，69/69）

```
① 旧主题竖屏像素一致   40/40   （10 主题 × 4 档位）
② 旧主题横屏像素一致   20/20   （10 主题 × 2 档位）
③ 机能主题竖屏装饰生效   4/4
④ 机能主题横屏装饰生效   2/2
⑤ 旧主题外观快照不变量   1/1
⑥ 外观运行时快照复用与失效 1/1
⑦ 换肤时代次自增        1/1
⑧ 窗口轮廓接线行为       1/1
```

探针**不进 sln**（与 `LiteMonitor.PerformanceProbe` 惯例一致），不创建 `HardwareMonitor`、
不 Open 硬件、不做屏幕捕获，`Settings` 由探针自己注入单例（不读也不写用户配置目录）。

### 6.2 全量单元测试

`577 / 577` 通过（`tests/LiteMonitor.Tests`）。

### 6.3 外观基线重采对比

用 `5d5201f` 重建的探针重采 368 个夹具，与 A04 原基线逐像素对比：

| 指标 | 值 |
|---|---|
| `identical` | **336** |
| `changed` | **0** |
| `missingInCurrent` | 0 |
| `newInCurrent` | 32（全部来自 `CyberPunk_Mech` 新主题） |

⇒ **L-02 的序列化器改动 + A12 的新主题，没有改变任何旧主题的渲染像素。**

---

## 7. 已知限制与未验证项

| 项 | 说明 |
|---|---|
| 真实窗口呈现 | 探针为**离屏**渲染，**不含** DWM / 窗口呈现 / 桌面合成 / 窗口透明度 / 任务栏 |
| 实机 DPI 往返 | 96/120/144/192 真实窗口、100%→200%→100% 跨屏往返、句柄重建、100 次换肤**未实机验证** |
| A08 每帧 CPU 预算 | 此前只测了分配量，**每帧 CPU 尚未测量**，须在 A13 接线后补测 |
| A07 性能超预算 | 4 卡 + 两倍档装饰层 **2.40 ms/帧** > v4 §7.4 的 2 ms；已裁决走"给出数据和原因"路径，A14 须用 A04 同口径（480×2708）复测 |
| 液态玻璃后端 | **尚未接入**（A15–A17）；请求 glass 的主题当前按 A08 的降级链回退到 gradient |
| Web 端次级色 | 网页模板写死次级色，本轮按 v4 §7.3 只做主色，成组改留待后续（对比度风险） |
| A10 预览 | 预览的扫描线 / 网格仍是假绘制，应在 A13 完成后换正式 `DecorRenderer`（下一包处理） |

---

## 8. 交接给 A14

1. 用 **A04 同口径矩阵**（4 卡 @96/2.0 = 480×2708）复测装饰层 CPU，把"装饰层额外 CPU"与"基线 70.86 ms"**分开报告**。
2. 补测 **A08 每帧 CPU 预算**（接线后才有真实意义）。
3. 实机验证：真实 DPI 往返、句柄重建、100 次换肤、圆角→切角→圆角切换、窗口尺寸不变的轮廓切换。
4. 真实桌面合成截图**单列**，不与离屏可控图混作同一个指标（v4 §7.2）。
5. A15–A17（M2 实时玻璃）在 A14 通过后启动。

---

## 9. 探针使用说明

```powershell
dotnet build tests/AppearanceWiringProbe/AppearanceWiringProbe.csproj -c Release
tests/AppearanceWiringProbe/output/AppearanceWiringProbe.exe
```

退出码：`0` = 全部通过；`1` = 有用例失败；`2` = 探针自身异常。
证据 JSON：`tests/AppearanceWiringProbe/output/evidence/wiring-evidence.json`（产物目录已 gitignore）。
