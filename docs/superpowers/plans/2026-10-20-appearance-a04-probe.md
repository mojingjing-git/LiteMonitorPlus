# A04 —— 独立外观探针与截图夹具

> 计划：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4）
> 基线：[2026-10-20-appearance-a00-baseline.md](2026-10-20-appearance-a00-baseline.md)
> 执行人：A04 验证线 worker
> 状态：已完成

---

## 1. 实际基线（开始时的工作区状态）

| 项 | 值 |
|---|---|
| HEAD | `97f793dccac6d5ab37120602ecab4f6238a855da`（`merge: performance sampling hardening`） |
| 分支 | `master` |
| 已跟踪的改动（**非本线所改**） | `docs/APPEARANCE_PLAN.md`、`docs/superpowers/plans/2026-10-20-performance-sampling-verification.md` |
| 未跟踪文件（**非本线所改**） | `LiteMonitor-old-config-20261020.zip`（用户备份，**全程未读写**）、`docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md`、`docs/superpowers/plans/2026-10-20-appearance-a00-baseline.md`、`docs/superpowers/plans/2026-10-20-appearance-agent-execution.md`、`docs/superpowers/plans/2026-10-20-appearance-lead-review.md`、`docs/superpowers/plans/2026-10-20-hardware-stability-hardening.md`、`docs/superpowers/plans/2026-10-20-hardware-stability-parallel-dispatch.md`、`docs/superpowers/plans/2026-10-20-performance-sampling-merge.md` |

**本线工作期间其它线落地的改动（我未触碰，但影响基线口径）**：
`src/Core/ThemeManager.cs`（模型线 A01/A03，+346/−13）、`src/Core/ThemeAppearanceOptions.cs`（新增）、
`src/ThemeEditor/ThemeFileService.cs`（保存线 A02）、`tests/AppearanceBackdropProbe/`（背景线 A06）、
`tests/LiteMonitor.Tests/*.cs`（其它线）。

> **基线一致性已核对**：本线跑完基线后重新 `dotnet build`，主程序集哈希与结果 JSON 里记录的
> `mainAssemblySha256` **完全一致**（`DE021E1A…B2F0`），说明基线就是针对**当前（含模型线 A01/A03）**
> 的源码树采集的，不存在"跑到一半主工程被换掉"的情况。

**本线未做的事**：没有 `git add -A`、没有 `git commit`、没有修改 `src/**` 任何生产代码、
没有修改 `tests/LiteMonitor.Tests/**`、没有修改主工程/测试工程的 csproj 或 sln。

---

## 2. 新增 / 修改文件清单

| 文件 | 性质 | 说明 |
|---|---|---|
| `tests/LiteMonitor.PerformanceProbe/AppearanceProbe.cs` | **新增** | 外观探针主体：夹具上下文构建、离屏渲染、PNG 输出、像素哈希、局部失效场景、前后对比、结果 JSON |
| `tests/LiteMonitor.PerformanceProbe/AppearanceFixture.cs` | **新增** | 夹具数据模型、JSON 加载、夹具矩阵构建器 |
| `tests/AppearanceFixtures/appearance-fixtures.json` | **新增** | 固定夹具数据（**唯一事实来源**） |
| `tests/AppearanceFixtures/README.md` | **新增** | 夹具数据格式与已知边界 |
| `tests/LiteMonitor.PerformanceProbe/Program.cs` | 修改 | 新增 `--appearance-only` 入口与 4 个外观专用参数；**既有采集路径一行未改** |
| `tests/LiteMonitor.PerformanceProbe/ProbeEnvironment.cs` | 修改 | 仅 1 行：`InstallSettingsSingleton` 由 `private static` 改为 `internal static`（供外观探针复用同一套配置隔离） |
| `tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj` | 修改 | 新增 1 个 `<Content Include="..\AppearanceFixtures\*.json" …>`（把夹具数据复制到输出目录；**未新增任何程序集引用 / NuGet 依赖**） |
| `tests/LiteMonitor.PerformanceProbe/README.md` | 修改 | 新增 §8「外观探针入口」 |

**未新增任何 NuGet 依赖**；未新增任何 `ProjectReference` / `AssemblyReference`。

### 2.1 入口用法

```powershell
cd G:\TOOL\LiteMonitor-fork

# 构建
dotnet build tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj -c Release

# 完整外观基线
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --appearance-only `
    --out tests/LiteMonitor.PerformanceProbe/output/appearance-baseline

# 改造后复跑 + 与基线逐像素对比
dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll `
    --appearance-only `
    --out tests/LiteMonitor.PerformanceProbe/output/appearance-after `
    --compare tests/LiteMonitor.PerformanceProbe/output/appearance-baseline
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `--appearance-only` | — | 走外观探针入口（独立入口，不是既有功能） |
| `--appearance-set` | `full` | `full` 完整叉乘 / `core` 只取 96+144 DPI / `edge` 只跑边界场景 / `quick` 只取 96 DPI+1.0（冒烟，**不可当基线**） |
| `--appearance-draws` | 20 | 每个夹具的测量绘制次数（指标值持续变化） |
| `--appearance-invalidation` | `all` | `all` / `sample`（只跑第一个主题）/ `none` |
| `--compare <dir>` | — | 与某份基线结果做逐像素对比 |

---

## 3. 夹具矩阵

### 3.1 维度

| 维度 | 取值 | 来源 | 覆盖情况 |
|---|---|---|---|
| 主题 | 10 个旧主题 | 运行时发现 `resources/themes/*.json` | ✅ 10/10（A00 冻结的 10 个旧主题**一个不缺**） |
| 布局 | `vertical` / `horizontal` | `axes.modes` | ✅ 2/2 |
| 显卡卡数 | 1 / 4 | `axes.gpuCounts` | ✅ 2/2（组块数 7 → 10） |
| DPI | 96 / 120 / 144 / 192 | `axes.dpis` | ✅ 4/4（**离屏模拟**，见 §7） |
| 用户缩放 | 1.0 / 2.0 | `axes.userScales` | ✅ 2/2（**离屏模拟**） |
| 边界输入 | 长组名 / 自定义标签 / 零项 / 窄窗 | `edgeScenarios` | ✅ 4/4 |
| 变化的进度 | 指标值逐帧变化 | `valueModel` | ✅ 每个夹具测量期内每帧都改值（走缓存失效 + 重绘路径） |

**夹具总数 = 336**（核心 320 = 10 主题 × 2 模式 × 2 卡数 × 4 DPI × 2 缩放；边界 16 = 4 场景 × 2 模式 × 2 卡数）。

### 3.2 夹具标识与产出路径

`fixtureId` 规则：`{主题}__{模式}__gpu{卡数}__dpi{DPI}__scale{缩放}__w{逻辑宽}[__{边界场景ID}]`

产出：`<out>\png\{fixtureId}.png`（每夹具 1 张）+ `<out>\appearance-results.json` + `<out>\appearance-run.txt`。

**边界场景的 16 个夹具（全部为 `AuroraDream_Purple` 主题，dpi 96 / scale 1.0）**：

| fixtureId | 宽×高 | item / group |
|---|---|---|
| `AuroraDream_Purple__vertical__gpu1__dpi96__scale1.0__w240__edge-long-group-name` | 240×820 | 14 / 7 |
| `AuroraDream_Purple__vertical__gpu4__dpi96__scale1.0__w240__edge-long-group-name` | 240×1354 | 23 / 10 |
| `AuroraDream_Purple__horizontal__gpu1__dpi96__scale1.0__w240__edge-long-group-name` | 1310×56 | 14 / 7 |
| `AuroraDream_Purple__horizontal__gpu4__dpi96__scale1.0__w240__edge-long-group-name` | 2892×56 | 23 / 10 |
| `AuroraDream_Purple__vertical__gpu1__dpi96__scale1.0__w240__edge-custom-labels` | 240×820 | 14 / 7 |
| `AuroraDream_Purple__vertical__gpu4__dpi96__scale1.0__w240__edge-custom-labels` | 240×1354 | 23 / 10 |
| `AuroraDream_Purple__horizontal__gpu1__dpi96__scale1.0__w240__edge-custom-labels` | 837×56 | 14 / 7 |
| `AuroraDream_Purple__horizontal__gpu4__dpi96__scale1.0__w240__edge-custom-labels` | 1599×56 | 23 / 10 |
| `AuroraDream_Purple__vertical__gpu1__dpi96__scale1.0__w240__edge-zero-items` | 240×76 | 0 / 0 |
| `AuroraDream_Purple__vertical__gpu4__dpi96__scale1.0__w240__edge-zero-items` | 240×76 | 0 / 0 |
| `AuroraDream_Purple__horizontal__gpu1__dpi96__scale1.0__w240__edge-zero-items` | 240×1 | 0 / 0 |
| `AuroraDream_Purple__horizontal__gpu4__dpi96__scale1.0__w240__edge-zero-items` | 240×1 | 0 / 0 |
| `AuroraDream_Purple__vertical__gpu1__dpi96__scale1.0__w160__edge-narrow-window` | 160×820 | 14 / 7 |
| `AuroraDream_Purple__vertical__gpu4__dpi96__scale1.0__w160__edge-narrow-window` | 160×1354 | 23 / 10 |
| `AuroraDream_Purple__horizontal__gpu1__dpi96__scale1.0__w160__edge-narrow-window` | 828×56 | 14 / 7 |
| `AuroraDream_Purple__horizontal__gpu4__dpi96__scale1.0__w160__edge-narrow-window` | 1605×56 | 23 / 10 |

> 全部 336 条明细（逐夹具宽高、item/group 数、三项成本、PNG 路径、PNG 哈希、像素哈希、主题文件哈希）
> 见 `tests/LiteMonitor.PerformanceProbe/output/appearance-baseline/appearance-results.json` 的 `fixtures` 数组。

### 3.3 夹具的可复现性设计

- 指标值由 `valueModel` 纯函数推导（`base + ((i*itemOffset + frame*step) mod (max-base))`），无随机、无硬件。
- 显卡简称是夹具里写死的字符串（`gpus[].shortName`），通过 `GpuRegistry.ReplaceAll` 注入，**不枚举真实显卡**。
- Dashboard 文本（Hostname / Uptime / Time / IP）由 `dashValues` 固定注入 —— 否则会随机器名 / 当前时间 / 开机时长变化，
  破坏"同一夹具两次跑出同一像素"的前提。
  （实现细节：`InfoService` 构造时会异步跑一次 `UpdateTimeInfo()` 把 Uptime 写成真实值，探针因此先等它跑完再重新注入固定值。）

---

## 4. 本轮实测数据

> 全部为**本轮实跑数据**（2026-10-20 16:31:47 起，501.6 s），**没有引用任何历史基线**。
> 运行环境：Windows 10.0.26300 x64，16 逻辑核，进程真实系统 DPI = 96。
> 主程序集 SHA256 `DE021E1A…B2F0`；探针程序集 SHA256 `02105B3C…8F86E`；夹具数据 SHA256 `F307099E…6DAF`；git HEAD `97f793d`。

### 4.1 夹具渲染成本（10 个主题平均值，每夹具 20 次测量绘制）

**竖屏（vertical）**

| 卡数 | DPI | 用户缩放 | 物理尺寸 | wallMs/帧 | cpuMs/帧 | 分配 B/帧 | 整机 CPU @12.5Hz |
|---|---|---|---|---|---|---|---|
| 1 | 96 | 1.0 | 240×820 | 11.39 | 10.70 | 6508 | 0.84% |
| 1 | 96 | 2.0 | 480×1640 | 32.50 | 31.64 | 6509 | 2.47% |
| 1 | 120 | 1.0 | 300×1019 | 15.69 | 15.47 | 6511 | 1.21% |
| 1 | 120 | 2.0 | 600×2050 | 45.76 | 45.08 | 6508 | 3.52% |
| 1 | 144 | 1.0 | 360×1230 | 20.41 | 19.69 | 6507 | 1.54% |
| 1 | 144 | 2.0 | 720×2460 | 65.44 | 62.73 | 6506 | 4.90% |
| 1 | 192 | 1.0 | 480×1640 | 33.32 | 31.33 | 6506 | 2.45% |
| 1 | 192 | 2.0 | 960×3280 | 107.97 | 105.39 | 6506 | 8.23% |
| 4 | 96 | 1.0 | 240×1354 | 25.02 | 23.67 | 13994 | 1.85% |
| 4 | 96 | 2.0 | 480×2708 | 72.50 | 70.86 | 13994 | 5.54% |
| 4 | 120 | 1.0 | 300×1682 | 33.95 | 32.03 | 13994 | 2.50% |
| 4 | 120 | 2.0 | 600×3385 | 107.27 | 104.22 | 13994 | 8.14% |
| 4 | 144 | 1.0 | 360×2031 | 44.47 | 42.50 | 13994 | 3.32% |
| 4 | 144 | 2.0 | 720×4062 | 152.22 | 148.05 | 13994 | 11.57% |
| 4 | 192 | 1.0 | 480×2708 | 72.25 | 69.61 | 13994 | 5.44% |
| 4 | 192 | 2.0 | 960×5416 | 259.28 | 253.28 | 13994 | 19.79% |

**横屏（horizontal）**

| 卡数 | DPI | 用户缩放 | 物理尺寸 | wallMs/帧 | cpuMs/帧 | 分配 B/帧 | 整机 CPU @12.5Hz |
|---|---|---|---|---|---|---|---|
| 1 | 96 | 1.0 | 828×56 | 3.68 | 3.67 | 4282 | 0.29% |
| 1 | 96 | 2.0 | 1538×112 | 5.89 | 5.55 | 4282 | 0.43% |
| 1 | 120 | 1.0 | 1002×70 | 3.92 | 4.06 | 4282 | 0.32% |
| 1 | 120 | 2.0 | 1883×140 | 8.83 | 8.75 | 4282 | 0.68% |
| 1 | 144 | 1.0 | 1148×84 | 4.46 | 4.22 | 4282 | 0.33% |
| 1 | 144 | 2.0 | 2213×168 | 11.63 | 11.48 | 4282 | 0.90% |
| 1 | 192 | 1.0 | 1538×112 | 5.90 | 5.86 | 4282 | 0.46% |
| 1 | 192 | 2.0 | 2908×224 | 17.90 | 17.42 | 4282 | 1.36% |
| 4 | 96 | 1.0 | 1605×56 | 7.33 | 6.88 | 9514 | 0.54% |
| 4 | 96 | 2.0 | 2982×112 | 17.76 | 16.80 | 9514 | 1.31% |
| 4 | 120 | 1.0 | 1934×70 | 8.69 | 8.28 | 9514 | 0.65% |
| 4 | 120 | 2.0 | 3657×140 | 23.45 | 22.89 | 9514 | 1.79% |
| 4 | 144 | 1.0 | 2221×84 | 10.19 | 10.16 | 9514 | 0.79% |
| 4 | 144 | 2.0 | 4298×168 | 31.47 | 31.09 | 9514 | 2.43% |
| 4 | 192 | 1.0 | 2982×112 | 17.12 | 16.80 | 9514 | 1.31% |
| 4 | 192 | 2.0 | 5656×224 | 51.57 | 50.16 | 9514 | 3.92% |

> ⚠ **与既有 `baseline/probe-results.json` 的 `renderBench` 不可直接相减**：那一份走
> **WinForms 兼容缓冲**画布、13 个可见项、宽度取自 `Settings.PanelWidth`；
> 本探针走**直接离屏位图**、14 个固定项（1 卡）/ 23 个（4 卡）、宽度取自夹具。
> 两者是**不同的测量口径**，只能各自与"改造后同口径复测"比较。
> 既有基线的对照值（仅作口径说明，非本轮结果）：vertical 240×788 → 4.56/4.44/6776 B；
> horizontal 768×56 → 2.37/2.35/4640 B；vertical scale2 **480×1576** → 5.71/5.52/6776 B。
>
> ⚠ 本表是**默认 20 帧窗口**的数，对"有内容"的夹具系统性低报 4%–38% 的稳态成本，
> 且同组 10 个主题之间还有约 10% 的离散度 —— **只用于同口径的前后对比**。口径校准见 §4.5。

### 4.2 完整失效 / 局部失效场景（v4 §7.2）

40 个场景（10 主题 × 2 模式 × 1/4 卡，dpi 96 / scale 1.0），每个场景 4 个变体，
每变体连续 **100 次重绘**。

| 变体 | 逐像素一致 | 不一致 | 说明 |
|---|---|---|---|
| `itemDriven` | **0 / 40** | 40 / 40 | 失效区 = 被改动指标的绘制矩形（外扩 3 px 覆盖字形抗锯齿） |
| `itemDrivenWithBackgroundPrefill` | **40 / 40** | 0 / 40 | 同上，但**先在失效区补一层背景底色**再局部重绘 |
| `fixedRect` | **40 / 40** | 0 / 40 | v4 §7.2 点名的固定失效矩形 `Rectangle(37,43,20,20)` |
| `fixedRectWithBackgroundPrefill` | **40 / 40** | 0 / 40 | 同上 + 背景底色预填 |

**已定位的根因（改造前就存在）**：

> **渲染器在局部失效时没有重画失效区的底色。**
> `UIRenderer.Render` 的背景填充是
> `Rectangle(-5, -5, g.VisibleClipBounds.Width + 10, g.VisibleClipBounds.Height + 10)`
> —— 也就是「**以窗口原点为锚点**、按失效区**尺寸**」。当失效区不在窗口原点时，
> 这块底色根本不会落在失效区上：
>  - **竖屏**：组块圆角是 GDI+ 抗锯齿路径，叠在旧像素上 → 圆角边缘的抗锯齿被二次叠加，
>    差异集中在组块圆角（实测 diff 837–1674 像素，差异包围盒正好是组块区）。
>  - **横屏**：`HorizontalRenderer` 的背景填充是 `Rectangle(0, 0, panelWidth, panelHeight)`，
>    而 `panelHeight` 取自 `VisibleClipBounds.Height`（即失效区高度），锚点却是 y=0
>    → 第二行的失效区底色没被重画；指标文本变短时旧文本的尾巴露出来
>    （实测 diff 487–929 像素，差异是一条 y=33..45 的横带）。
>
> **证据**：`itemDrivenWithBackgroundPrefill` 变体（只在失效区补一层背景底色，其余完全不改）
> 在**全部 40 个场景**都与「一次完整绘制」逐像素一致 —— 证明问题出在
> 「底色没被重画」，而不是装饰层画错。
>
> 影响：新增的半透明装饰 / 液态玻璃背景会把这个已有缺陷**放大**（半透明层叠在陈旧像素上必然串色）。
> 因此 **A07 / A08 / A13 必须保证失效区的底色被重画**（例如背景填充改为覆盖
> `VisibleClipBounds.Right/Bottom` 而不是 `Width/Height`）。
> 本线**不修改生产代码**，只提供证据与夹具。

`fixedRect` 变体本来就一致，是因为 v4 §7.2 点名的 `Rectangle(37,43,20,20)` 落在
组标题所在的空白处，那一块没有抗锯齿形状会被二次叠加。

### 4.3 边界场景的画面观察（改造前的既有行为，已被基线固定下来）

| 场景 | 观察 | 性质 |
|---|---|---|
| **窄窗（160 逻辑像素）** | 竖屏 160 宽时**标签与数值互相压字**：`Hostname` 压住 `LITEBENCH-HOST`、`CPU Usage` 压住 `67.4%`、`CPU Temp` 压住 `81.1°C` | **改造前就存在**（`UIRenderer.DrawStandardItem` 左对齐画标签、右对齐画数值，两者共用同一条窄矩形，没有溢出避让）。已被本基线固定为"改造前画面"，改造后必须逐像素对比确认它**没有变差** |
| **长组名** | 组标题被面板宽度裁断（`Central Processing Unit Pack…`、`NVIDIA GeForce RTX 4090 F…`） | 既有行为（`TextRenderer` 单行不换行） |
| **自定义标签** | 中文标签（`处理器总占用率`）与长 ASCII 标签正常渲染；`TaskbarLabel = " "` 正确隐藏短标签 | 符合预期 |
| **零项** | 竖屏 240×76（只有标题 + 内边距）；横屏 240×1（`HorizontalLayout.Build` 返回 0，高度被夹到 1 px 以保证可产出 PNG） | 不崩溃、不越界 |

### 4.4 可复现性（同一夹具两次跑 → 同一像素）

第二次**独立完整跑**（`appearance-baseline-r2`，531.9 s，退出码 0）用 `--compare` 与第一份基线逐像素对比：

| 指标 | 值 |
|---|---|
| `identical` | **336 / 336** |
| `changed` | 0 |
| `newInCurrent` / `missingInCurrent` | 0 / 0 |
| `allIdentical` | **true** |
| 主程序集 SHA256（两次相同） | `DE021E1A…B2F0` |
| 探针程序集 SHA256（两次相同） | `02105B3C…8F86E` |
| 局部失效 4 个变体的结论 | 两次**完全一致**（`itemDriven` 0/40 一致；其余三个变体 40/40 一致） |

→ **同一套夹具在同一台机器上，两次独立跑出的 336 张画面逐像素完全相同。**
这是"改造前后必须能逐像素对比"这条要求的**前提**：基线是可复现的锚点，
改造后只要有任何像素变化都会被 `--compare` 立刻抓出来。

补充：把测量窗口从 20 帧改成 200 帧（`--appearance-draws 200`，仅跑 16 个边界夹具）后，
16 个夹具的 `pixelSha256` 与 20 帧那次**依然全部相同** → 测量帧数**不影响**画面输出，
只影响时间/分配两项的统计口径。

### 4.5 测量窗口的口径偏差（必须随数据一起读）

同一批边界夹具，20 帧窗口 vs 200 帧窗口的 `wallMsPerDraw`：

| 夹具 | 20 帧 | 200 帧 | 偏差 |
|---|---|---|---|
| vertical gpu1 w240 long-group-name | 12.20 | 14.86 | **+21.8%** |
| vertical gpu4 w240 long-group-name | 24.31 | 33.64 | **+38.4%** |
| horizontal gpu1 w240 long-group-name | 4.08 | 5.59 | **+36.9%** |
| horizontal gpu4 w240 long-group-name | 10.43 | 13.80 | **+32.3%** |
| vertical gpu1 w240 custom-labels | 12.32 | 15.69 | +27.4% |
| vertical gpu4 w240 custom-labels | 24.56 | 32.27 | +31.4% |
| horizontal gpu1 w240 custom-labels | 3.34 | 4.60 | +37.7% |
| horizontal gpu4 w240 custom-labels | 8.59 | 9.79 | +13.9% |
| vertical gpu1 w160 narrow-window | 10.47 | 11.78 | +12.5% |
| vertical gpu4 w160 narrow-window | 22.69 | 23.58 | +4.0% |
| horizontal gpu1 w160 narrow-window | 4.19 | 4.81 | +14.7% |
| horizontal gpu4 w160 narrow-window | 9.20 | 9.63 | +4.6% |
| vertical gpu1 w240 zero-items | 0.49 | 0.28 | −42.8%（每帧成本太小，统计分辨率主导） |

**结论**：20 帧窗口对"有内容"的夹具**系统性低报 4%–38%** 的稳态绘制成本
（帧数少 → 窗口内摊到的 GC / 资源增长更少）；零项夹具因为每帧成本太小，方向相反。

**因此**：
- §4.1 的表**只能与同一口径的复测比**（改造前后都用默认 20 帧），偏差会**相互抵消**；
- 需要绝对值时，用 `--appearance-draws 200` 复跑（代价：全量 336 夹具约需 20 分钟）；
- 帧数**不影响** `pixelSha256`，所以逐像素对比不受这个偏差影响。

### 4.6 隔离声明（写在结果 JSON 的 `isolation` 段）

| 项 | 值 | 证据 |
|---|---|---|
| `hardwareMonitorCreated` | `false` | `AppearanceProbe.cs` 中**不存在** `new HardwareMonitor` / `Computer.Open` / `DriverInstaller` 的任何调用（可 grep 验证：`new HardwareMonitor` 只出现在 `ProbeEnvironment.cs:38` 的旧采集路径，`new DriverInstaller` 只出现在 `Program.cs:177` 的旧驱动只读检查） |
| `hardwareOpened` | `false` | 同上；探针从不构造 LHM `Computer`，不会触发任何 ring0 驱动 |
| `driverChecked` | `false` | 外观入口**连 PawnIO 只读检查都不做** |
| `driverInstalledOrRemoved` | `false` | 全流程无装卸驱动调用 |
| `externalToolStarted` | `false` | 不启动 PresentMon / ETW，不下载任何外部程序 |
| `userSettingsRead` / `userSettingsWritten` | `false` | 探针自己 `new Settings()` 并注入 `Settings._instance` 单例，`Settings.Load()` 命中单例后直接返回内存对象 |
| 输出目录 | `--out` 指定 | 全部产物在 `tests/LiteMonitor.PerformanceProbe/output/appearance-baseline/`（已 git 忽略） |

> `isolation` 段是探针的**自述声明**；真正的证据是上面那一列的"可 grep 验证的代码路径"。

---

## 5. 测试命令与退出码

| # | 命令 | 退出码 | 备注 |
|---|---|---|---|
| 1 | `dotnet build tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj -c Release` | **0** | 0 错误 / 0 警告。基线采集前后各跑一次，第二次主程序集哈希与基线记录一致 |
| 2 | `dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll --appearance-only --out tests/LiteMonitor.PerformanceProbe/output/appearance-baseline` | **0** | 336 夹具全部成功（336 succeeded / 0 failed），耗时 501.6 s |
| 3 | `dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll --appearance-only --out tests/LiteMonitor.PerformanceProbe/output/appearance-baseline-r2 --compare tests/LiteMonitor.PerformanceProbe/output/appearance-baseline` | **0** | 复跑 + 逐像素对比，结果见 §4.3 |
| 4 | `dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll --out tests/LiteMonitor.PerformanceProbe/output/entry-check --groups A --quick` | **0** | **回归验证：既有采集入口行为不变** —— 驱动只读检查 → 硬件就绪 → A 组采集 → 单设备 Update 成本 → 兼容缓冲绘制基准 → FPS 证据，输出仍是原来的 `probe-results.json` / `probe-run.txt`（31.2 s） |
| 5 | `dotnet tests/LiteMonitor.PerformanceProbe/output/LiteMonitor.PerformanceProbe.dll --appearance-only --appearance-set edge --appearance-draws 200 --appearance-invalidation none --out tests/LiteMonitor.PerformanceProbe/output/appearance-noisecheck` | **0** | 测量窗口口径偏差核对（20 帧 vs 200 帧），结果见 §4.5（37.9 s） |

> 没有出现 obj/bin 占用错误，无需 30 秒重试。
> 命令 4 是**旧路径**，因此**会**接触硬件（那是它本来的语义）；命令 2/3（外观路径）完全不会。

---

## 6. 交接需求

| # | 需求 | 面向 | 说明 |
|---|---|---|---|
| H-1 | 保留 `LiteMonitor.PerformanceProbe.csproj` 里新增的 `<Content Include="..\AppearanceFixtures\*.json" …>` | 主集成者 | 本线按授权改了**探针自己的** csproj（只加内容复制，**没加任何程序集引用 / NuGet 包**）。若你统一整理 csproj，请保留这一条，否则夹具数据无法随构建复制到输出目录 |
| H-2 | 保留 `ProbeEnvironment.InstallSettingsSingleton` 的 `internal` 可见性 | 主集成者 | 外观探针复用同一套配置隔离，改回 `private` 会让探针编译不过 |
| H-3 | **A07 / A08 / A13 必须让失效区底色被重画** | 装饰线 / 背景线 / 主集成者 | §4.2 已定位根因：`UIRenderer` / `HorizontalRenderer` 的背景填充以**窗口原点**为锚点，局部失效时底色不落在失效区。新增的半透明装饰 / 玻璃背景会把这个缺陷放大（串色） |
| H-4 | A12 交付新主题后**重新 build 探针并复跑** | 视觉主题线 / 主集成者 | 探针**动态发现**主题，不需要改代码；重建后 `resources/themes/*.json` 会被复制到输出目录，新主题自动进入夹具矩阵 |
| H-5 | A14 用 `--compare` 做「关闭新能力时像素应一致」的验收 | 独立审查 | 改造后复跑并 `--compare tests/LiteMonitor.PerformanceProbe/output/appearance-baseline`，`comparison.allIdentical` 为 true 即达标。**比较以 `pixelSha256`（原始 ARGB 像素哈希）为准**，`pngSha256` 只作附加证据 |
| H-6 | 新基线目录不要提交 | 主集成者 | `tests/LiteMonitor.PerformanceProbe/output/` 已被 git 忽略；基线 PNG/JSON 体积大，留在工作区即可，必要时按需归档 |
| H-7 | **窄窗（160 逻辑像素）下标签与数值压字**，是**改造前就存在**的缺陷 | 主集成者 / A13 | §4.3：`UIRenderer.DrawStandardItem` 左对齐标签、右对齐数值共用同一条窄矩形，没有溢出避让。已被本基线固定为"改造前画面"；改造后若要修，必须**显式**在验收里说明这属于画面变更（`--compare` 会报 `changed`），不能默默发生 |

---

## 7. 未验证项与已知限制

### 7.1 明确**没有**验证的（必须由实窗/其它线补）

1. **真实 96/120/144/192 DPI 窗口测试** —— 探针的 DPI 是**离屏模拟**。
   v4 §7.2 的「真实 96/120/144/192 DPI 与用户缩放分别记录」**未满足**，必须由实窗补。
2. **DWM / 窗口呈现 / 桌面合成 / 窗口透明度 / 任务栏** —— 离屏渲染**全部不含**。
   结果 JSON 的 `surfaceDeclaration` 段显式声明了这一点，**不得当成真实窗口证据**。
3. **100%→200%→100% 跨屏往返、窗口尺寸不变的圆角→切角→圆角、句柄重建、100 次换肤** —— 本线**未做**。
4. **新主题（CyberPunk_Mech）** —— A12 尚未交付，本轮基线只有 10 个旧主题。
5. **真实窗口进程的 CPU / 句柄对比** —— v4 §7.4 要求最终补实窗进程 CPU；本线只有离屏数据。
6. **任务栏渲染器（`TaskbarRenderer`）** —— 本探针只覆盖主面板竖屏 / 横屏两个渲染器。
7. **10,000 次进度更新后的缓存 / 原生内存平台** —— v4 §7.4 属于 A05 的验收项，本线只提供夹具。

### 7.2 探针自身的已知限制

| # | 限制 | 影响 |
|---|---|---|
| L-1 | DPI / 用户缩放都是**离屏模拟**（`dpiSource` / `userScaleSource` = `offscreen-simulated`）。布局尺寸走生产同一条 `Theme.Scale(dpiScale, userScale)`；字形点值**额外**乘 `dpiScale`（GDI 的 LOGFONT 高度在字体创建时就按系统 DPI 定死，离屏画布没有 DC DPI 换算）。这是**逼近**，不是等价 | 模拟 DPI 与真实系统 DPI 不同时，字形光栅化只是近似；必须靠实窗补 |
| L-2 | 横屏的**列间距**由 `HorizontalLayout` 内部按**进程真实系统 DPI** 缩放（`Graphics.FromHwnd(IntPtr.Zero)`），离屏模拟改写不了 | 模拟 DPI ≠ 真实系统 DPI 时，横屏列间距与真实高 DPI 窗口不一致（竖屏不受影响） |
| L-3 | 默认测量窗口只有 20 次绘制 | 对"有内容"的夹具**系统性低报 4%–38%** 的稳态绘制成本（实测见 §4.5）。前后对比必须用**同一个 draw 数**（偏差会抵消）；要绝对值用 `--appearance-draws 200`。帧数**不影响** `pixelSha256` |
| L-4 | 测量是**离屏直接画位图**，既有 `renderBench` 是**兼容缓冲**画布 | 两者口径不同，**不可相减**（见 §4.1 末尾） |
| L-5 | `InfoService` 构造时会异步写一次 Uptime / Time（真实值） | 探针用「等它跑完 → 重新注入固定值 → 每个夹具再注入一次」消除；夹具仍**不是**纯函数式隔离（InfoService 是生产单例，探针复用了它） |
| L-6 | 局部失效场景的「合成 vs 一次完整绘制」是**离屏模型**（用 `Graphics.SetClip` 模拟 WinForms 的失效区） | 与真实 WinForms Paint 的失效区语义一致，但不是真实窗口事件；实窗仍需复验 |
| L-7 | 边界场景只在 `AuroraDream_Purple` 主题、dpi 96 / scale 1.0 下跑 | 长组名 / 自定义标签 / 零项 / 窄窗**没有**在全部 10 个主题 × 全部 DPI 下重复（成本权衡） |
| L-8 | 夹具的可见项是探针自己定的 14 / 23 项，**不是**生产 `Settings.InitDefaultItems()` 的集合 | 夹具稳定但与用户实际配置不同；目的是让前后对比不受用户配置影响 |

### 7.3 逐条对照 A04 验收要求

| 要求 | 状态 | 证据 |
|---|---|---|
| 新增明确标注的 `--appearance-only` 入口 | ✅ | `Program.cs:26-30`；README §8；`isolation` 段 |
| 不创建 HardwareMonitor / 不 Open 硬件 / 不装卸驱动 / 不启动 PresentMon·ETW / 不下载外部程序 | ✅ | §4.4 的 grep 可验证证据 |
| 输出到独立目录 | ✅ | `output/appearance-baseline/`（git 忽略） |
| **旧采集语义不变** | ✅ | 命令 4 实跑通过；`Program.cs` 旧分支一行未改 |
| 主题：10 个旧主题 + （新主题就绪后）新主题 | ✅（10/10 旧主题） / ⏳（新主题未交付，但**自动发现已就绪**） | §3.1、§4.1 `themes` 段 |
| 布局：竖屏 / 横屏 | ✅ | 2/2 |
| 显卡卡数：1 卡 / 4 卡（组块数变化，不依赖真实显卡） | ✅ | 组块 7 → 10；`GpuRegistry.ReplaceAll` 注入假简称 |
| DPI 元数据（96/120/144/192）与用户缩放（1.0/2.0） | ✅ **离屏模拟**（已如实标注） | 每夹具 `dpi` / `userScale` / `dpiSource` / `userScaleSource` |
| 变化的进度（指标值持续变化） | ✅ | 每夹具测量期 20 帧全部改值 |
| 长组名 / 自定义标签 / 零项 / 窄窗 | ✅ | 4 个边界场景 × 16 个夹具（§3.2） |
| 每个夹具一张 PNG | ✅ | 336 张 |
| JSON 结果，风格与 `probe-results.json` 一致 | ✅ | `appearance-results.json`：夹具标识、主题名、模式、DPI、用户缩放、卡数、group 数、item 数、宽高、draws、wallMsPerDraw、cpuMsPerDraw、allocatedBytesPerDraw、程序集哈希（+ 主题文件哈希 + PNG 哈希 + 像素哈希） |
| 程序集哈希用于前后对比 | ✅ | `metadata.mainAssemblySha256` / `probeAssemblySha256`，另加逐夹具 `themeFileSha256` |
| 区分"真实 DPI 窗口测试"与"离屏模拟" | ✅ | `surfaceDeclaration.dpiSource = offscreen-simulated` / `realWindowTestRequired = true`；§7.1 第 1 条 |
| 装饰开启时覆盖完整/局部失效：`Rectangle(37,43,20,20)` + 连续 100 次重绘；合成结果与一次完整绘制一致 | ⚠ **已覆盖并逐像素验证**；`fixedRect` 40/40 一致；`itemDriven` 40/40 **不一致**（已定位为改造前就存在的"底色未重画"，见 §4.2 / H-3） | §4.2 |
| 离屏渲染不含 DWM / 窗口呈现 —— 显式声明 | ✅ | `surfaceDeclaration` 段 + §4.6 + §7.1 第 2 条 |
| 同一夹具两次跑出同一像素（逐像素对比的前提） | ✅ | §4.4：336/336 `identical`，`allIdentical = true` |
| 不引入新 NuGet 依赖 | ✅ | csproj 只加了一条 `Content` 复制 |
| 不读不写用户常用配置目录 | ✅ | 自建 `Settings` + 注入单例 |
| 不得把"历史测量"当成本轮结果 | ✅ | §4 全部为 16:31:47 起的本轮实跑；历史基线只在 §4.1 末尾作为"口径说明"引用并明确标注 |
