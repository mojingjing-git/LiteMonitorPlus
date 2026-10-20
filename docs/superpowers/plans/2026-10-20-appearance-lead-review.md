# 主集成者运行日志 —— 外观改造

> 计划：[2026-10-20-appearance-agent-execution.md](2026-10-20-appearance-agent-execution.md)（v4）
> 基线：[2026-10-20-appearance-a00-baseline.md](2026-10-20-appearance-a00-baseline.md)
> 负责人：主集成者（总集成负责人）

---

## A01 / A03 审查结论 —— 通过，带 1 项整合待办

**交付物**：
`src/Core/ThemeManager.cs`（+346 / −13）、`src/Core/ThemeAppearanceOptions.cs`（新增）、
`tests/LiteMonitor.Tests/ThemeSchemaTests.cs`（23 用例）、`AppearanceOptionsTests.cs`（24 用例）

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 扩展数据槽覆盖 7 个节点 | grep `JsonExtensionData` | ✅ `Theme` / `LayoutConfig` / `FontConfig` / `ColorConfig` / `ThresholdConfig` / `ThresholdSet` / `BackdropConfig` 各一，类型 `Dictionary<string, JsonElement>` |
| `LayoutConfig.Scale` 未动旧字段行为 | 读实现 | ✅ 10 个旧字段的缩放公式**逐字未变**；7 个新字段**故意不缩放**（逻辑值/强度/枚举），由 `ThemeAppearanceOptions` 统一换算物理像素 → 满足 v4 §3.2「每个新尺寸只缩放一次」 |
| `Theme.Backdrop` 是可空且无 `= new()` | 读声明 | ✅ `public BackdropConfig? Backdrop { get; set; }` + `[JsonIgnore(WhenWritingNull)]` → 缺失即 null 且不写出 `backdrop` 键 |
| `ParseColor` 的 `rgba()` 分支 | 读实现 | ✅ r/g/b 用 `int.TryParse(..., NumberStyles.Integer, InvariantCulture)`，越界即 `Color.Transparent`；a 用 `double.TryParse(..., NumberStyles.Float, InvariantCulture)`；`NumberStyles.Float` 天然拒绝 `NaN`/`Infinity`；alpha×255 走 AwayFromZero |
| 旧六位/八位十六进制与颜色名行为不变 | 读分支结构 | ✅ `rgba(` 是独立 `else if` 分支，插在 `#` 分支之后、`Color.FromName` 之前，不影响旧路径 |
| `PruneExtensionDataConflicts` 防同名重复写出 | 读实现 | ✅ 用反射按"大小写不敏感"比对已知属性名，丢弃同名扩展键；`ThemeManager.Load` 内已调用（:499） |
| 10 个旧主题哈希未被改写 | 逐个比对 A00 基线 | ✅ **10/10 一致** |
| 47 个新测试 | 我自己跑 | ✅ `已通过! 失败:0 通过:47 总计:47`，EXIT=0 |

### 交接合同（已冻结，下游线照此消费）

1. `ThemeAppearanceOptions.Create(theme, 窗口DeviceDpi/96f, 用户缩放, 完整ClientRectangle, 主题代次)`
   —— **DPI 必须取当前窗口 `DeviceDpi`，禁止用 `UIUtils.ScaleFactor`**（那是进程级全局值）。
2. 装饰/背景线必须用 `FrameWidthPx` / `FrameLengthPx` / `BlurPx`（**物理像素**），
   **不得再乘一次缩放**；旧布局字段继续直接读 `theme.Layout.*`（已缩放）。
3. 消费 `BorderColor` / `ShadowColor` 前必须先看 `FrameEnabled` / `GlowEnabled`
   —— 这保证 7 个旧主题已有的 `border`/`shadow` 键**不会自动出现描边/辉光**（v4 §3.1 硬要求）。
4. `BackgroundFrame` / `PreviewSource` 是**借用**引用，options 不释放，由帧 owner 负责。
5. 归一化只作用于运行时副本，**不回写用户 JSON**。

### 待办 L-02：统一序列化选项（跨包整合缺口）

**问题**（A01 已识别并交给 A02，A02 未接；主集成者现在收口）：

| 路径 | 现状 |
|---|---|
| `ThemeManager.Load` | `PropertyNameCaseInsensitive = true` + **调** `PruneExtensionDataConflicts` ✅ |
| `ThemeFileService.LoadTheme` | `PropertyNameCaseInsensitive = true`，但**不调** `PruneExtensionDataConflicts` ❌ |
| `ThemeFileService.SaveTheme` | **只有** `{ WriteIndented = true }`（**大小写敏感**）❌ |

后果：编辑器的加载路径与 `ThemeManager.Load` 行为不一致 → 同名已知/扩展键在编辑器保存后可能重复写出，
违反 v4 §3.1「后续字段类型化后不能重复写出同名已知/扩展键」。

**处理**（主集成者，改 `src/ThemeEditor/ThemeFileService.cs`）：
1. `LoadTheme` / `SaveTheme` 统一改用 `ThemeManager.CreateSerializerOptions()`
2. `LoadTheme` 反序列化后调 `ThemeManager.PruneExtensionDataConflicts(theme)`

**对 A02 已有测试的影响**：**无**。`PropertyNameCaseInsensitive` 只影响**读取**，
`WriteIndented` 保持 true，序列化输出字节不变 → A02 的字节级断言（`CreateTheme_复制模板_字节完全一致` 等）不受影响。

**为什么延后执行**：与 L-01 同批做。理由是 A04 / A06 两个 worker 仍在跑
`dotnet build` / `dotnet test`，中途改主工程源码会让它们的构建出现**与它们无关的失败**，污染证据。
→ 等本波全部返回、主工程做过一次干净串行构建后，由主集成者一次性执行 L-01 + L-02，再复跑全量测试。

**执行单（主集成者已逐行复核，API 已确认存在，改动零编译风险）**：

| # | 文件 | 改动 |
|---|---|---|
| 1 | `src/ThemeEditor/ThemeFileService.cs` → `ThemeFileService.LoadTheme(string dir, string name)` | 第 121–125 行的 `new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true }` → 改为 `ThemeManager.CreateSerializerOptions()`（该方法**已存在且 public**，其默认 `WriteIndented=true` 与原行为一致，且已含 `AllowTrailingCommas=true`）→ 读取行为**完全不变** |
| 2 | 同上，第 130 行 `theme.BuildFonts();` 之后 | 追加 `ThemeManager.PruneExtensionDataConflicts(theme);`（该方法**已存在且 public**，A01 已实现并被 `ThemeManager.Load` 调用） |
| 3 | `src/ThemeEditor/ThemeFileService.cs` → `ThemeFileService.SaveTheme(string dir, string name, Theme theme)` | 第 156–159 行的 `new JsonSerializerOptions { WriteIndented = true }` → 改为 `ThemeManager.CreateSerializerOptions()` |

**对 A02 已有测试的影响**：**零**。`PropertyNameCaseInsensitive` 只影响**读取**；`WriteIndented` 保持 true → 序列化输出字节不变 → A02 的字节级断言（`CreateTheme_复制模板_字节完全一致` 等）不受影响。

**副作用（必须写进 A14 验收）**：改完后**主工程程序集哈希会变**，A04 记录的 `DE021E1A…B2F0` 不再对应新构建。
→ A14 必须在 L-01/L-02 之后、A13 接线之前，用新程序集**重新采集一份 appearance-baseline**，
作为"新能力全关"状态的正式基准。A04 现有基线保留为"改造前原始基线"，用于证明 L-01/L-02 是**行为修复而非外观变更**（两者像素应完全一致）。

### ✅ L-02 已执行（2026-10-20 17:35）

**实际改动**（`src/ThemeEditor/ThemeFileService.cs`，仅 2 处）：

| # | 方法 | 改动 |
|---|---|---|
| 1 | `LoadTheme(string dir, string name)` | `new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true }` → `ThemeManager.CreateSerializerOptions()`；并在 `BuildFonts()` 前插入 `ThemeManager.PruneExtensionDataConflicts(theme);` |
| 2 | `SaveTheme(string dir, string name, Theme theme)` | `new JsonSerializerOptions { WriteIndented = true }` → `ThemeManager.CreateSerializerOptions()` |

**执行前的复核**（确保零编译风险）：`using LiteMonitor.src.Core;` 已在文件头（第 6 行）→
`ThemeManager` 可直接访问；`ThemeManager.CreateSerializerOptions(bool writeIndented = true)` 与
`ThemeManager.PruneExtensionDataConflicts(Theme)` 均为 **`public static`**，已确认存在。

**执行后的验证**：

| 命令 | 结果 |
|---|---|
| `dotnet build LiteMonitor.csproj -c Release` | **0 错误 / 232 警告**，EXIT=0，耗时 4.30s（232 是"只构建主工程"口径；A00 的 242 是整解决方案口径，差异非回归） |
| `dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release` | **已通过! 失败: 0，通过: 544，已跳过: 0，总计: 544**，EXIT=0，48s |

→ **A02 的字节级断言（`CreateTheme_复制模板_字节完全一致` 等）全部保持通过**，
证明 `PropertyNameCaseInsensitive` 只影响读取、`WriteIndented` 保持 true → 序列化输出字节不变。

**由此解锁**：A10（编辑器接入）现已派发（其写集与本改动不重叠，且已被明确告知不得再改 `ThemeFileService.cs`）。

**仍未执行**：L-01（故障注入钩子 `public` → `internal` + 主工程 csproj 加 `InternalsVisibleTo`）。
**继续延后的理由**：它要改 `LiteMonitor.csproj`，会影响**所有**工程的项目文件/还原流程，
并行构建时的干扰面比 L-02 大得多，且**当前无人被它阻塞**。
→ 等 A12/A07/A09/A10 全部返回、主工程做过一次干净串行构建后再做。


### A01 声明的边界（本集成者认可）

- 未做任何真实渲染验证（绘制接线不在本线，画面由 A13/A14 验收）
- 3 位 `#RGB` 与 `rgb(r,g,b)`（无 alpha）**仍不支持**，按 v4「保留旧行为」刻意保留
- 透明兜底的 RGB 不统一（`Color.Transparent` 是透明白 `0x00FFFFFF`，`Color.FromName(未知名)` 是透明黑 `0x00000000`），
  两者 alpha 都是 0，渲染无差别；测试因此断言 `alpha == 0` 而非具体 ARGB
- `PruneExtensionDataConflicts` 会丢弃一个字面键名为 `extensionData` 的未知键（病态边界）
- 未提供 `Theme.Clone()` —— 预览主题由 A10 自行组装（复用 `Layout.Clone()`，字体只借用）

---

## A02 审查结论 —— 通过，带 1 项待办

**交付物**：`src/ThemeEditor/ThemeFileService.cs`（+303 / −23）、`tests/LiteMonitor.Tests/ThemeSaveTests.cs`（新增 15 用例）

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| `SafeWrite` 全流程无"先删目标" | 读实现 + grep `File\.Delete\|File\.Replace\|File\.Move` | ✅ `File.Delete` 仅出现在 `DeleteTheme`(269)、`CleanupTempFiles`(304)、`TryDeleteFile`(425，且只作用于临时文件)。替换路径是 `File.Replace`（同卷原子，保留 ACL）→ 兜底 `File.Move(overwrite:true)`（`MoveFileEx + REPLACE_EXISTING`，仍是原子覆盖） |
| 失败不丢原文件 | 读 `ReplaceTargetCore` 异常路径 | ✅ 两条兜底路径都失败时抛 `IOException("替换主题文件失败，原文件保持不变：…")` 并 `AggregateException` 聚合两个异常 |
| 临时文件同目录 | 读实现 | ✅ `path + TempFileSuffix`，同目录 |
| 临时文件不被当成主题 | 读 `CleanupTempFiles` 的 `EndsWith(TempFileSuffix)` 判定 | ✅ 后缀是 `.litemonitor-tmp`，在扩展名**之后**，`GetFiles(dir,"*.json")` 不会匹配 |
| 公开签名兼容 | 列全部 public 成员 | ✅ 原有 7 个方法签名一个没删；新增的都是 `dir` 重载 |

### 加分项：变异测试

worker 把 `SafeWrite` 临时改回旧语义（删目标再移动）跑同一批用例 → **3 个失败 / 12 个通过**，
失败的是"替换失败_原文件逐字节不变"、"清理临时文件不残留"、"原主题仍可正常加载"，
随后已完整还原并复验。**这证明断言真的能抓到这个 bug，而不是空跑通过。**

这一点比"测试全绿"有价值得多 —— 我在 A14 独立审查时会重点看这类变异证据。

### 待办（主集成者，**延后到本波 worker 全部结束后再做**）

**L-01：把故障注入钩子从 `public` 收回 `internal`**

现状：`TempFileWriter` / `TargetReplacer` / `ResetTestHooks` 是 `public static`，
worker 的理由是"加 `InternalsVisibleTo` 要改 `LiteMonitor.csproj`，不在我写集内"。

处理：主工程 `LiteMonitor.csproj` 加

```xml
<ItemGroup>
  <InternalsVisibleTo Include="LiteMonitor.Tests" />
</ItemGroup>
```

然后把三个钩子改成 `internal static`，测试工程零改动（测试工程程序集名 = `LiteMonitor.Tests`，
已核实；主工程目前**没有**任何 `InternalsVisibleTo`）。

**为什么延后**：`internal` 化 + `InternalsVisibleTo` 必须同时落地，否则测试工程立刻编译失败。
A01/A03 与 A04 两个 worker 正在跑 `dotnet build`/`dotnet test`，
中途改 csproj 或改可见性会让它们的构建出现**与它们无关的失败**，干扰证据。
→ 等本波 4 条线全部返回、且主工程做过一次干净的串行构建后再做。

保留 `public` 的：`TempFileSuffix`（对外有用）、`CleanupTempFiles`（主集成者可能要在启动期调）、
各 `dir` 重载。

### 其它交接项（已记录，转交对应线）

| 项 | 转交 | 说明 |
|---|---|---|
| 编辑器失败提示文案 | A10 | 现在弹的是原始异常消息（"替换主题文件失败，原文件保持不变：…"）。A10 在 `ThemeEditorForm` 写集内，可改成友好文案。`ThemeEditorForm.SaveCurrentTheme` 本来就有 `try/catch`，"抛异常=明确失败"与现有调用方语义兼容，**A02 不需要回改** |
| 启动期残留清理时机 | 主集成者 | `ListThemes()` 会自动调 `CleanupTempFiles`（编辑器打开时触发）。若要在更早时机清，主集成者调 `ThemeFileService.CleanupTempFiles()` |
| 多进程并发保存同一主题 | 暂不修 | 固定名临时文件 + `FileShare.None` → 并发时后者**写临时文件失败并抛异常**，原文件保留。比改造前严格更好（改造前同样会失败，但那时会先删目标）。加 PID 后缀会破坏"固定名可断言"的测试设计，**本轮不做**，留待 A14 审查确认 |

### 顺带澄清一个非问题

worker 报告"警告数 228–231 波动，A00 基线 242"。
这不是异常：我的 242 是**整解决方案**构建（含测试工程），worker 的 231 是**只构建主工程**。
口径不同，非回归。

### 边界（worker 已诚实声明，本集成者认可）

- 断电原子性：保证"替换动作原子 + 替换前临时文件已 `Flush(true)` 落盘"，
  **未做目录项 fsync**（Windows 无等价语义）→ 掉电瞬间理论上有"新内容或旧内容"的不确定窗口，
  但**不会出现半截文件或文件消失**。未做真实掉电实测。
- 未实测杀软拦截 / 目标被独占打开时 `File.Replace` 的 `ERROR_UNABLE_TO_REMOVE_REPLACED` 分支。
- 非 NTFS（FAT32/exFAT）未实测，会走 `File.Move(overwrite:true)` 兜底。
- 未做 1000 次连续保存的句柄/碎片压力测试。

---

## A04 审查结论 —— 通过，带 3 项裁定

**交付物**：`tests/LiteMonitor.PerformanceProbe/AppearanceProbe.cs`、`AppearanceFixture.cs`（新增）、
`tests/AppearanceFixtures/appearance-fixtures.json` + `README.md`（新增）、
`tests/LiteMonitor.PerformanceProbe/Program.cs`、`ProbeEnvironment.cs`、`.csproj`、`README.md`（改）
+ 结果文档 `docs/superpowers/plans/2026-10-20-appearance-a04-probe.md`

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 旧探针采集语义不变 | `git diff tests/LiteMonitor.PerformanceProbe/Program.cs` | ✅ 旧分支**一行未改**；只在解析器尾部追加 5 个 `--appearance-*`/`--compare` 参数，并在 `Main` 最前面插入一个**独立的** `if (options.AppearanceOnly) return AppearanceProbeRunner.Run(options);` 早退分支（不创建 `HardwareMonitor`、不开硬件、不装卸驱动、不启 PresentMon/ETW） |
| 未引入新依赖 | 看 csproj diff | ✅ 只加了一条 `<Content Include="..\AppearanceFixtures\*.json">` 复制，**零程序集引用、零 NuGet** |
| 夹具规模 | 数结果 JSON | ✅ 336 个（核心 320 = 10 主题 × 竖/横 × 1/4 卡 × DPI 96/120/144/192 × 缩放 1.0/2.0；边界 16 = 长组名/自定义标签/零项/窄窗 × 2 模式 × 2 卡数） |
| 逐像素可复现 | 读 worker 二次独立全量跑结果 | ✅ **336/336 `identical`**、`allIdentical=true`；改用 200 帧窗口复跑，16 个夹具 `pixelSha256` 仍全同 → 帧数不影响像素哈希，可作 A14 的验收基准 |
| 主题动态发现 | 读夹具矩阵构造 | ✅ 运行时扫描主题目录；A12 交付新主题后**重建探针即自动纳入**，无需改代码（H-4 成立） |

### 裁定一：H-3「局部失效不重画底色」—— 我独立复核，**降级为潜伏缺陷 + 硬性接线约束**

worker 报的 H-3 是其最有价值的发现。我**没有照抄结论**，而是回到源码独立验证：

- `src/UI/UIRenderer.cs` → `UIRenderer.Render`：背景填充是
  `g.FillRectangle(brush, new Rectangle(-5, -5, (int)g.VisibleClipBounds.Width + 10, (int)g.VisibleClipBounds.Height + 10))`。
  **尺寸取自 `VisibleClipBounds`（已被失效区裁剪过），起点却固定在 `(-5,-5)`**。
  局部失效时 clip 被裁到一个小矩形 → 填充矩形整体落在 clip **之外** → 底色**根本没被重画**。
- `src/UI/HorizontalRenderer.cs` → `HorizontalRenderer.Render`：`panelHeight = (int)g.VisibleClipBounds.Height`，
  填充起点 `(0,0)`、宽度 `panelWidth`。局部失效时填充区与 clip 区同样**不重叠** → 同样漏。

**根因确认成立**（不是 worker 的推测）。但**严重性需要修正**：

> 我 grep 了生产代码的全部失效路径：`src/UI/UIController.cs` 的 4 处、`src/UI/MainForm_Transparent.cs` 的 1 处，
> **全部是无参 `Invalidate()` —— 整窗失效**。现有代码**没有任何局部失效路径**。

因此 **H-3 当前不会在生产中显现**，是**潜伏缺陷**。但它对 M1 是**硬约束**，理由是：

1. 机能风装饰（辉光/扫描线/网格）与液态玻璃都是**半透明**层 —— 底色不重画会直接**串色/残影**，比不透明色严重得多。
2. 一旦为了性能引入局部失效（这在 4 卡 2 倍尺寸档几乎必然被提出来，见裁定二），会立刻踩坑。

**裁定**：M1 **保持整窗 `Invalidate()`**，不引入局部失效；H-3 记为**已知限制**，并作为未来局部失效优化的**前置修复项**。
同时对 A13 增加一条接线要求：**背景底板必须锚定完整客户区**（用 `options.ClientBounds`，或 A08 的不透明底板层），
**不得**沿用 `VisibleClipBounds` / `(-5,-5)` 填充矩形定位外框 —— 现状恰好掩盖了这个隐患。

### 裁定二：性能基线与 v4 §7.4 预算的对照 —— **发现一处必须上报的风险**

v4 §7.4 的静态机能效果预算是**增量**预算：普通档 ≤1ms/帧、4卡+两倍物理尺寸档 ≤2ms/帧。
A04 的表正好覆盖了这两个档位（我按物理尺寸对齐口径后取用）：

| 档位 | 物理尺寸 | A04 实测基线 CPU/帧 | 整机 CPU @12.5Hz | v4 允许的装饰增量 |
|---|---|---|---|---|
| 竖屏 1 卡 @96/1.0（普通档） | 240×820 | **10.70 ms** | 0.84% | ≤1 ms |
| 竖屏 4 卡 @96/2.0（4卡+两倍） | 480×2708 | **70.86 ms** | 5.54% | ≤2 ms |
| 竖屏 4 卡 @192/2.0（极端档） | 960×5416 | **253.28 ms** | **19.79%** | — |

> **口径澄清**：v4 §7.4 那句"两倍竖屏物理尺寸为 480×1576"纠的是**旧基线把宽写窄**的错误（240×1576）。
> 对 **4 卡**档，4 卡比 1 卡多出 3 个组块（组块 7→10），正确的高度倍增值是 **480×2708**，A04 的表里有这一行 ✓。

**风险**：12.5Hz 的刷新周期是 **80 ms**。在"4 卡 + 两倍物理尺寸"档，**基线单帧就已经 70.86 ms，
占满 80 ms 预算的 89%**，v4 只留给装饰层 **2 ms**（2.8%）。到"4 卡 @192/2.0"档，基线 253 ms/帧 → 整机 19.79%，
**远超实时性**，此时任何装饰增量都是雪上加霜。

**这不阻塞 M1**，但它改变了两件事：
1. **A13 的验收方式必须是"改造后 vs 改造前"的同口径增量对比**（同一 `--appearance-draws`、同一夹具矩阵），
   绝不能只看绝对值 —— 绝对值里绝大部分是既有渲染成本，不是装饰的账。
2. **A05 的缓存机制不是可选项而是必需项**：v4 §3.3 已规定"每次 Paint 都绘制有效缓存，不能用跳过绘制来节流"，
   而 A04 的数据说明基线本身已经贴着预算线，装饰层**必须**是缓存命中 + 少量叠加绘制，
   不允许每帧重建渐变/纹理位图。

### 裁定三：A04 报告里的"20 帧窗口低报 4%–38%" —— 接受，但锁定对比口径

A04 自测出默认 20 帧测量窗口对"有内容"夹具**系统性低报 4%–38%** 的稳态成本（200 帧复测对比，14 个夹具有数据）。
这是诚实且必要的方法学披露。**裁定**：A14 验收一律**前后用同一 draw 数**（偏差相互抵消）；
需要绝对值时才用 `--appearance-draws 200`（全量 336 夹具约 20 分钟）。

### 交接项登记（转交对应线）

| 项 | 转交 | 说明 |
|---|---|---|
| **H-1** 保留探针 csproj 新增的 `<Content Include="..\AppearanceFixtures\*.json">` | A13/A14 | 整理 csproj 时不要删 |
| **H-2** 保留 `ProbeEnvironment.InstallSettingsSingleton` 的 `internal` 可见性 | A13/A14 | 探针自用 |
| **H-3** 局部失效不重画底色 | **A13**（接线约束）+ A07/A08 | 见裁定一：M1 保持整窗失效；底板必须锚定完整客户区 |
| **H-4** A12 交付新主题后**重建探针并复跑** | A12/A14 | 主题是运行时动态发现的，重建即自动纳入 |
| **H-5** A14 用 `--compare` 做"关闭新能力时像素应一致"验收 | A14 | 以 `pixelSha256` 为准，不是 PNG 字节（PNG 受压缩/元数据影响） |
| **H-7** 窄窗 160px 下标签压数值 | 记入已知限制 | 既有缺陷，非本轮引入；若修，A14 必须**显式声明为画面变更** |
| **新增**：A13 必须做改造前后增量对比 | A13/A14 | 见裁定二，绝对值不可直接判定 |
| **新增**：A05 缓存为必需项（非优化项） | A05 | 见裁定二第 2 点 |

### A04 边界（worker 已诚实声明，本集成者认可）

- DPI / 用户缩放是**离屏模拟**（`dpiSource=offscreen-simulated`）；字形光栅化只是**逼近**（GDI 的 LOGFONT 高度在字体创建时就按系统 DPI 定死，离屏画布没有 DC DPI 换算）→ **真实高 DPI 窗口测试仍未做**，且横屏列间距由 `HorizontalLayout` 按**进程真实系统 DPI** 缩放，离屏改写不了。
- 局部失效场景是 `Graphics.SetClip` 的**离屏模型**，与真实 WinForms Paint 失效区语义一致但不是真实窗口事件。
- 夹具项集是探针自定义的 14 项（1 卡）/ 23 项（4 卡），**不是用户配置**；边界场景只在 1 个主题 × 96DPI 跑过。
- 未覆盖：DWM/窗口呈现/桌面合成/透明度/任务栏渲染器/跨屏 DPI 往返/圆角↔切角/句柄重建/100 次换肤/10000 次进度更新的内存平台。

---

## A08 审查结论 —— 通过，带 3 项裁定

**交付物**：`src/System/Backdrop/`（`BackdropStatus.cs`、`IBackdropFrameSource.cs`、`BackdropModeResolver.cs`、
`BackdropGradient.cs`、`BackdropNoiseCache.cs`、`BackdropEdgeGlow.cs`、`BackdropRenderer.cs`，命名空间 `LiteMonitor.src.SystemServices.Backdrop`）、
`tests/AppearanceStaticBackdropProbe/**`、`docs/superpowers/plans/2026-10-20-appearance-a08-backdrop.md`

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 未越界 | grep `ThemeAppearanceModeNames` 全仓 | ✅ 它是 A08 **自己**在 `src/System/Backdrop/BackdropStatus.cs` 里定义的 `internal static class`，**没有**动 `ThemeManager.cs` 或任何他人文件 |
| solid 零副作用路径 | 读 `BackdropRenderer.Render` → `BackdropModeResolver.Resolve` | ✅ 模式为 solid / 未知 / `Backdrop` 缺失 → `EffectiveMode=Solid`、`Layers=None`，`Render` **根本不进绘制分支** → 不建画刷、不分配位图 |
| 幂等（v4 §3.3 硬要求） | 读 worker 的像素哈希证据 | ✅ 渐变+tint+噪点+辉光：单次 `1DB9ECA2…DC11` == 连续 100 次后同哈希；glass 降级路径同样一致 → **没有"在上一帧上叠半透明 tint 而累积变色"** |
| 降级不静默 | 读 `BackdropModeResolver.Resolve` | ✅ glass→gradient→solid 三级，每级都带 `ReasonCode` + 非空 `Reason`；`BackendMissing` / `BackendNotReady` / `BackendFrameInvalid` / `GradientUnavailable` 四种原因码齐备 |
| 坐标锚定 | 读 `BackdropRenderer` 各 `Draw*` 方法 | ✅ 全部用 `options.ClientBounds`；**未**使用 `VisibleClipBounds`，**未**使用 `(-5,-5)` 填充矩形 |
| 借用帧不释放 | 读 `DrawFrame` / `Dispose` | ✅ `options.BackgroundFrame` / `PreviewSource` 只借用；`Dispose` 只释放自有的噪点纹理 |
| 无抓屏、无计时器 | grep 目录内 `BitBlt`/`PrintWindow`/`DXGI`/`Timer` | ✅ 零命中；预览路径明确"固定源优先，绝不触碰实时后端"，探针实测**后端取帧次数 = 0** |
| tint alpha 语义 | 读 `ComputeTintAlpha` | ✅ `TintColor.A × Opacity`，`Opacity ≤ 0` 时返回 0 → 不建画刷。实测 `rgba(255,0,0,0.5)`×0.5 → `FF400000`，**相乘**正确 |
| 噪点有界且可复现 | 读 worker 证据 | ✅ `noise=0` → 0 条目 / 0 字节；`noise>0` → 1 条目 / 65,536 B，固定种子两帧哈希相同 `834C0834…73FE` |

### 裁定一：不透明底板**没有**违反 v4 §3.3（我原本担心，核实后不成立）

我读到 `BackdropRenderer.Render` 里 `DrawBasePlate` 在渐变/帧之前无条件执行，一度怀疑违反 v4 §3.3
「**不能用不透明底板把材质完全盖住**」。核实后确认**成立且合规**：

- gradient 路径：底板被随后绘制的渐变**完全覆盖**（`FillRectangle` 不透明）；
- glass 路径：底板在**下层**，帧由 `DrawImage(frame, options.ClientBounds)` **不透明覆盖**在其上 → 材质仍然透出。
- 底板的作用正是 v4 §3.3 要求的「每帧从**确定底板**或完整有效背景帧开始」→ 幂等的前提。

### 裁定二：默认 tint 会盖住 75% 渐变 —— v4 §3.1 的内在张力，转为主题编写指引

`ComputeTintAlpha = TintColor.A × Opacity`，而 v4 §3.1 规定 `Tint` 缺省取 `Background`（**不透明**）、
`Opacity` 缺省 **0.75** → tint 层 alpha = 191，**盖住 75% 的渐变**。玻璃同理。

A08 是**忠实执行** v4 字面规定，没有 bug。但后果是：任何只写 `"mode":"gradient"` 的主题，用户看到的是近乎纯色的背景。

**处置**（已执行）：
- 已 `task_append` 通知 **A12**：渐变/玻璃主题必须**显式**设 `opacity: 0`（或期望的低值），
  或把 `tint` 设成 alpha=0（同样让 tint 层完全不画、零开销）；
- 同时确认 `IsGradientUsable` 只看**渐变两端** alpha、不看 tint → 把 tint 设透明**不会**破坏降级链；
- 该规则写入 **A12 的参数说明**，并作为 A14 的验收判据之一。

### 裁定三：探针工程**不加入 sln**

A08 请求"接入 sln"。**我决定不接**，理由：既有 `tests/LiteMonitor.PerformanceProbe` 同样不在 sln 中
（A00 基线即如此），探针是**独立运行**的自证工具，不参与 `dotnet test`。
把它们加进 sln 只会让解决方案构建多编 3 个探针工程，**扩大并行构建冲突面**，无收益。
→ 约定：所有 `tests/Appearance*Probe/**` 探针工程**一律不进 sln**，由主集成者按需单独构建。

### 交接项登记

| 项 | 转交 | 说明 |
|---|---|---|
| **B-1** 背景层接线点 | **A13** | 在 `UIRenderer.Render` / `HorizontalRenderer.Render` 的背景层位置调用 `BackdropRenderer.Render(g, options)`；坐标用 `options.ClientBounds`；必须在标题/指标/组块**之前**；内部已 `Save/Restore` 隔离 |
| **B-2** 底板必须锚定完整客户区 | **A13** | 与 H-3 同源：背景层底板**不得**沿用 `VisibleClipBounds` / `(-5,-5)` 填充矩形 |
| **B-3** 实时后端接口 | **A15** | 实现 `IBackdropFrameSource`（`IsReady` / `UnavailableReason` / `TryGetFrame(blurPx, out frame)`），帧为**借用**，渲染器不释放 |
| **B-4** 每帧 CPU 预算**未测** | **A14** | A08 只测了**分配量**（noise=0 零分配 ✓），**未测每帧 CPU**。v4 §7.4「每帧额外 CPU ≤1ms/2ms」**必须由 A13/A14 在真实接线后补测** |
| **B-5** 16 MiB 位图预算未跨线核算 | A05 + A14 | A08 自报位图上限 256 KiB（16 MiB 共享预算的 1.6%），需与 A05 的纹理缓存**合并核算** |

### A08 边界（worker 已诚实声明，本集成者认可）

- 全为**离屏**渲染，**未在真实 MainForm** 上验证 DWM / 窗口呈现 / DPI。
- 只覆盖 96 DPI；`EffectiveScale` 缩放路径（120/144/192）未实测。
- 玻璃只接**假后端**（本线按职责不实现捕获），实时正确性与预算属 A06/A15。
- 横屏未接线（属 A13）；噪点/辉光观感未经真人眼评审。
- 实施期主工程两次被**他人在途文件**（`WebPageContent.cs`、`src/UI/Decor/**`）打断构建，
  worker **未改这些文件、未删 obj**，改用仓库外临时 MSBuild 覆盖验证自身代码，装饰线修复后已用**不带覆盖**的正式命令复跑通过 —— 处置得当。

---

## A11 审查结论 —— 通过，带 1 项交接

**交付物**：`src/System/WebServer/WebThemeCss.cs`（新增：`WebThemeColorSnapshot` / `WebThemeCss` / `WebThemeColorPublisher`）、
`src/System/WebServer/WebSessionManager.cs`（**仅挂载点**）、`src/System/WebServer/WebPageContent.cs`（**仅挂载点**）、
`tests/LiteMonitor.Tests/WebThemeCssTests.cs`（新增 15 用例）、`docs/superpowers/plans/2026-10-20-appearance-a11-web.md`

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 零注入（v4 §7.3 硬要求） | 读 worker 的 15 组载荷 × 8 色字段证据 | ✅ `</style>`、`</style >`、`"><script>alert(1)</script>`、`<!--`、`/*`、`url(javascript:…)`、含 `\r\n` 的闭合样式 —— **全部走写死回退色**；输出 CSS 不含 `< > " ' / *`、不含 `url(`/`javascript`；整页 `</style>`/`<script`/`<!--` 计数与基线一致 |
| 注入策略强度 | 读实现 | ✅ 是**回退**而非**转义** —— 主题原文**走不到输出**，不依赖转义兜底。**这比"转义后仍安全"更强** |
| ARGB 不搞反（v4 §7.3） | 读实测输出 | ✅ `#80112233` → `rgba(17,34,51,0.502)`（显式排除了 `rgba(128,17,34,0.2)` 那个错误解）；`#112233` → `rgba(17,34,51,1)`；输出中**不出现任何十六进制** |
| invariant 小数 | 读实测 | ✅ de-DE 下输出与 invariant **逐字相同** |
| HTML 请求不产生硬件需求 | 读真实回环 HTTP 证据 | ✅ `/`→200 含主题变量、`/favicon.ico`→404，**采样入口调用数 = 0**，`HasWebDataDemand`/连接数/HTTP 租期均为"无需求" |
| 不推送不轮询（方案 B） | 读源码级断言 | ✅ 去注释后 `WebThemeColorPublisher` 全文**仅 1 次引用**且在 HTML 分支、**不在** `/api/snapshot` 分支；新增文件无 `Timer`/`Task.Delay`/`Thread.Sleep` |
| 鉴权/会话/连接逻辑零改动 | 读 diff | ✅ `CheckAuth`/租期/共享采样/代次/连接登记回收**一行未改** |
| 全量测试 | 读退出码 | ✅ **544/544 通过**（529 + 15），EXIT=0 |

### 交接项登记

| 项 | 转交 | 说明 |
|---|---|---|
| **W-1** 启动时与换肤时各发布一次快照 | **A13** | `WebThemeColorPublisher` 是**进程级静态**。A13 必须在启动路径与每次换肤路径各调一次 `WebThemeColorPublisher.PublishFromTheme(ThemeManager.Current);`，否则网页沿用旧快照 |
| **W-2** 网页模板的**写死次级色** | **A13** | `.tag` 背景、`.ring-bg`/`.d-bar-bg`、`.inp`、`.btn`、`.dash-item`、`.modal-overlay` 目前**不跟随主题**。A11 按"仅挂载点"约束没改（正确）。A13 有 `WebPageContent.cs` 写集，可把其中**成组可换**的次级色改为 CSS 变量；**改动必须成组**（只换一半会产生半主题化，比不换更糟） |

### A11 边界（worker 已诚实声明，本集成者认可）

- **未做真实浏览器渲染验收**、未人工验证"按 F5 生效"、未做 10 主题批量遍历与对比度检查、未做性能基准 → 全部留给 A14。
- 未知颜色名：桌面=透明黑（旧行为），网页=明确回退色（v4 §7.3 要求，**有意差异**，非 bug）。
- `rgba()` 输入的 alpha 会量化到字节再回写（0.5 → 0.502），视觉无差别但非逐字。

### 裁定：W-2（网页模板写死次级色）**本轮不做**，记为已知限制

A11 指出网页模板里 `.tag` 背景、`.ring-bg`/`.d-bar-bg`、`.inp`、`.btn`、`.dash-item`、`.modal-overlay` 是**写死**的，不跟随主题。

**我的裁决：本轮不改。** 理由：

1. v4 §7.3 的要求是「**主色**随主题，浏览器刷新页面后生效」—— 主色已由 A11 的 CSS 变量覆盖，**要求已满足**；
2. 次级色要改必须**成组改**，只改一半会产生"半主题化"，视觉上比不改更糟；
3. 成组改引入对比度风险（某些主题下 `.btn`/`.inp` 与新底色撞色），而收益仅是观感；
4. 缩小 A13 的接线面，降低本轮收口风险。

→ 记为**已知限制**，在 A14 的验收报告里如实列出，不静默略过。

---

## A13 接线清单（主集成者预先固化，等 A05/A07/A08/A09 齐备后执行）

> A13 由**主集成者本人**执行（v4 §5 A13「同一 owner 接两个 Renderer、UIController、MainForm/helper 及必要 UIUtils」）。
> 前提：A05 + A07 + A08 + A09 全部交付并审查通过。

### 接线表（每项入口唯一，不允许出现第二处挂载）

| # | 位置（文件 → 类型 → 方法） | 接线内容 | 来自 |
|---|---|---|---|
| 1 | `src/UI/UIRenderer.cs` → `UIRenderer.Render` | **第一步**调 `BackdropRenderer.Render(g, options)` 画背景层；**最后**画向内的窗口边框/高光 | B-1 / A08 / A07 |
| 2 | `src/UI/HorizontalRenderer.cs` → `HorizontalRenderer.Render` | 同上；横屏**复用**窗口底板、底纹与外框（v4 §3.3） | B-1 / A08 / A07 |
| 3 | `src/UI/UIController.cs` → 换肤/尺寸/DPI 路径 | 构造 `ThemeAppearanceOptions.Create(theme, DeviceDpi/96f, 用户缩放, 完整 ClientRectangle, 主题代次)`；**DPI 取当前窗口 `DeviceDpi`，禁止 `UIUtils.ScaleFactor`** | 冻结合同 1 |
| 4 | 同上 | 主题代次自增 → 触发 A05 缓存失效、A08 `Invalidate()` | A05 / A08 |
| 5 | `src/UI/UIController.cs` → 刷新路径 | `options` **每帧重建**（因为 `ClientBounds` 会变），但装饰缓存**不得**每帧重建 | 冻结合同 3 |
| 6 | `src/UI/MainForm_Transparent.cs` → 窗口外观生命周期 | 初始化 / 同尺寸换主题 / Resize / DPI 变化 / 横竖变化 / HandleCreated / HandleDestroyed / 退出 —— **只保留一处 Resize 绑定**（现有 `MainFormWinHelper` 的 Resize 被绑了两次，需收敛） | A09 / v4 §3.4 |
| 7 | `src/UI/Helpers/MainFormWinHelper.cs` → `ApplyRoundedCorners` | 改为消费 A09 的统一轮廓几何；Win11 切角用 `DWMWCP_DONOTROUND=1`，回到圆角恢复 `ROUND=2` + 正确 `Region`；定义 Region/路径的**所有权与释放** | A09 |
| 8 | `src/System/WebServer/WebSessionManager.cs` → 首页 HTML 分支 | 启动时与**每次换肤**各调一次 `WebThemeColorPublisher.PublishFromTheme(ThemeManager.Current);` | W-1 |
| 9 | `src/Core/UIUtils.cs` | **只做确有必要的兼容接线**；`TextureBrush`/`LinearGradientBrush` **不进**现有 `GetBrush` 缓存（其 `GetBrush` 返回类型固定 `SolidBrush`，`GetPen` 返回**共享实例**） | v4 §3.3 |

### 公开 Render 签名的保留方式（v4 §3.2 硬要求，必须照此实现）

**必须原样保留**（一个参数都不能改、不能删、不能加）：

```
src/UI/UIRenderer.cs         public static void Render(Graphics g, List<GroupLayoutInfo> groups, Theme t)
src/UI/HorizontalRenderer.cs  public static void Render(Graphics g, Theme t, List<Column> cols, int panelWidth)
src/UI/TaskbarRenderer.cs    public static void Render(Graphics g, List<Column> cols, bool light)
```

**接线方式**：**加重载**，不改动原签名。旧的 3 参数版本保留为"无外观上下文"的兼容入口，
内部转调新重载并传 `options = null`；新重载多带一个 `ThemeAppearanceOptions` 参数。

**这样做带来的三个好处**：
1. A04 的外观探针仍可用**旧签名**渲染 → 它采集的 baseline 就是"改造后旧路径"的基准，
   A14 拿它做"新能力全关时逐夹具像素一致"的对比才有意义；
2. 现有所有调用点（`UIController`、`MainForm_Transparent` 等）**一行不用改**即可继续工作；
3. 旧路径与新路径是**同一个入口**，不存在"两套渲染逻辑"的漂移风险。

**注意**：`TaskbarRenderer` 的 3 个签名**完全不动** —— 任务栏模式不纳入新外观主线。


### 接线硬约束（违反即返工）

1. **底板锚定完整客户区**：背景层底板用 `options.ClientBounds`，
   **不得**沿用 `g.VisibleClipBounds` 或 `(-5,-5)` 填充矩形定位外框（H-3 / B-2）。
2. **每个新尺寸只缩放一次**：直接用 `FrameWidthPx` / `FrameLengthPx` / `BlurPx`，**不得再乘一次缩放**。
3. **先判开关再消费颜色**：用 `BorderColor` / `ShadowColor` 前必须先判 `FrameEnabled` / `GlowEnabled`。
4. **不释放借用对象**：`BackgroundFrame` / `PreviewSource` 由帧 owner 释放。
5. **绘制入口 `Save()`/`Restore()`** 隔离状态。
6. **每个挂载点只设一处**：不新建第二份 MainForm 轮廓逻辑、不新建第二处背景层调用。
7. **不触碰** `src/UI/TaskbarRenderer.cs`（零背景绘制、硬编码颜色、子窗口不可能有 DWM 材质）与
   `src/UI/HardwareTrendForm.cs` / `HardwareInfoForm.cs` / `Controls/LiteTreeView.cs` / `Controls/TrendChartControl.cs`（本轮明确不改）。
8. **每项入口唯一**：`UIUtils` 只做兼容接线，新增几何/纹理资源全部在 `src/UI/Decor/**`。

### A13 必须交付的验收证据

- **改造前 vs 改造后的同口径增量对比**（用 A04 的 `--appearance-only` + `--compare`，**同一 `--appearance-draws`**）：
  - **新能力全关**时 → `pixelSha256` 必须与基线**逐夹具一致**（证明没有回归）；
  - **新能力开启**时 → 给出每档的**额外 CPU ms/帧**，对照 v4 §7.4（普通档 ≤1ms、4卡+两倍物理尺寸档 ≤2ms）。
- 窗口轮廓的实窗验证（圆角↔切角、Resize、DPI 变化、句柄重建）。
- 编辑器/网页的实窗截图。

---

## A05 审查结论 —— 通过

**交付物**：`src/UI/Decor/`（`DecorGeometry.cs`、`DecorCacheKey.cs`、`DecorResourceCache.cs`、`DecorResourceHost.cs`、
`DecorTextureFactory.cs`、`DecorPainter.cs`，命名空间 `LiteMonitor.src.UI.Decor`）、
`tests/AppearanceDecorProbe/**`、`docs/superpowers/plans/2026-10-20-appearance-a05-decor.md`

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 缓存键覆盖维度 | 读 `DecorCacheKey` 的字段与 `Equals`/`GetHashCode` | ✅ `Kind` + `ThemeGeneration` + 主/次色 ARGB + `WidthPx`/`HeightPx` + `Direction` + `DeviceDpi` + `UserScaleKey`（千分比量化，避免浮点抖动）+ `Seed` + `Variant` —— **完整覆盖 v4 §3.3 要求的全部维度** |
| 配额 | 读 `DecorResourceCache` 常量 + worker 实测 | ✅ `DefaultMaxEntries = 128`、`DefaultMaxBitmapBytes = 16 MiB`；实测 300 条不同尺寸 → 条目**正好 128**；64×4MiB → 条目 4、字节 **16,777,216（正好 16MiB）** |
| 淘汰可复核 | 读实现 | ✅ **LRU + 总量核算**，插入前按最久未使用淘汰；淘汰计数可读 |
| 借用者不得 Dispose/改 Transform | 读 `DecorResourceCache` 与 `DecorPainter.CloneTextureBrush` | ✅ 缓存是所有者；借用者注释明写"不要 Dispose、不要改 Transform"；需要变换时用 `CloneTextureBrush` 克隆独占对象，且**克隆件与源画刷共用同一张瓦片位图，释放克隆件不释放位图**（技术细节正确） |
| 10,000 次进度更新后平台化 | 读 worker 实测 | ✅ **30,000 次**持续变化进度值：缓存条目**全程恒为 4**、字节恒为 9,792、累计创建资源**仅 4 个**、淘汰 **0** 次。工作集增量 14.2MB→6.5MB→**0.2MB**；托管内存增量 846KB→1,152B→**1,088B** → 明确的有界平台 |
| 边界 | 读 worker 实测 | ✅ 1×1 / 0宽 / 0高 / 窄窗160 / `FrameWidth=8`+`FrameLength=64` / 切角超半宽（60×40 夹到 19）全部不抛异常、路径不越界；**客户区外被改动像素 = 0 / 0 / 0** |
| 关闭新能力零副作用 | 读 worker 实测 | ✅ 缓存条目 0、创建资源 0、位图字节 0、**画布 0 像素被绘**、无计时器 |
| 每帧成本 | 读 worker 实测 | ✅ 平均 **0.1366 ms/帧**、P95 0.2268 ms —— **在 v4 §7.4 静态效果预算 ≤1ms/帧 内** |
| 未越界 | `git status` + 看文件清单 | ✅ 6 个新文件 + 探针工程 + 1 份文档，**未改动任何既有文件** |
| 行尾 | 读 worker 报告 | ✅ 源码统一 **CRLF**（对齐 `src/UI/` 100% 惯例），文档 **LF**（对齐 `docs/superpowers/plans/`）—— 细节到位 |

### worker 自陈并修正的实质问题（说明它真在跑实测，不是在空写）

1. **GDI+ 画笔沿路径居中 → 描边必然溢出客户区** → 改为"内描边带"填充；
2. 抗锯齿 1 像素溢出 → 外轮廓默认内缩 1px；
3. 圆角路径**原缺 4 条直线边**；
4. 切角正好等于半边长时产生**重合顶点自交**；
5. 缓存键**漏了渐变角度与路径带宽** → 补 `Variant`；
6. 局部坐标路径的平移曾漏做（只在缓存分支生效）。

这些是**自测发现的真实缺陷**，不是事后补的说辞。

### 交接项登记

| 项 | 转交 | 说明 |
|---|---|---|
| **D-1** | **A07** | `host.GetOrAddTiledBrush` / `GetOrAddGradientSlot` / `GetOrAddBandPath` + `DecorPainter.FillTiled` / `FillGradientSlot` / `FillInnerFrame`。**渐变按完整条槽缓存后裁剪**；路径为**局部坐标**（窗口移动不失效） |
| **D-2** | **A09** | `DecorGeometry.BuildFramePath(bounds, …)` 产出**绝对坐标**路径，直接喂 `Region` |
| **D-3** | **A13** | 生命周期四入口即全部钩子：`new DecorResourceHost()` → 每次 Paint `UpdateContext(options)` → 尺寸变化 `OnSizeChanged()` → 销毁 `Dispose()`。**四个入口就是完整生命周期，不需要猜** |
| **D-4** | **A07/A13** | 渐变角度约定已实测取证：**0° = 左→右，90° = 上→下**（GDI+ 实测，非假设）。A08 的 `BackdropGradient` 另有自己的角度实现 —— **A13 接线时必须核对两者是否一致**，不一致会导致同一主题在背景层与装饰层角度不同 |

---

## A06 审查结论 —— 路线裁决：**系统材质否决，捕获路线有条件放行**

**交付物**：`tests/AppearanceBackdropProbe/**`（探针 + README + 128 个证据文件，含 `output/final-neg` 与 `output/final-primary` 两套最终证据）、
`docs/superpowers/plans/2026-10-20-appearance-a06-glass-validation.md`（472 行）

### 环境事实更正（重要）

我此前的记录写的是"单显示器"—— **这是错的**。实测：

| 项 | 实测值 |
|---|---|
| 显示器 | **双屏**：DISPLAY1 主屏 2194×1234 逻辑（物理 3840×2160 @168 DPI，**HDR 10bpc 启用**）；DISPLAY2 **1152×2048 逻辑，X = −1152（负坐标）**，物理 1440×2560 @120 DPI |
| 系统 DPI | 96（96 DPI 下 `Screen.AllScreens` 报逻辑尺寸，实际 DPI 差异只在 DPI-aware 进程里体现） |
| OS / 硬件 | Windows 11 26H2 build 26300.9457；AMD 5800X / 16 逻辑核；DWM 合成开启；**非 RDP** |
| 权限 | asInvoker / Medium（未提权）；生产为 `requireAdministrator` → 差异已记录 |

→ **A06 是在真实双屏 + 跨屏 DPI（168 vs 120）+ 负坐标显示器上完成的验证**，不是单屏理想环境下的偶然结果。这**显著提高**了它的结论可信度。

### 五项验证的实测结论（我核对了证据与报告数字）

| # | 结论 | 关键数据 |
|---|---|---|
| 1 DWM 文档化系统材质 | **失败** | 8 个变体 `DwmSetWindowAttribute` **全部 S_OK**，但**无一呈现"背后内容实时响应"**。分层生产族（`layered-mica`/`layered-acrylic`）与同族基线画面差仅 **2.78 / 2.38**（近乎为零）；非分层+扩展边框族差 240.79 但**时间差 0.00** → 只是**壁纸派生静态材质**；不扩展边框时差 0.01（完全无效） |
| 2 排除自身 + 背景正确 | **通过** | `EXCLUDEFROMCAPTURE` + BitBlt/CAPTUREBLT/CopyFromScreen：自身像素 **0.0000**、场景色块命中 **1.0000**、与同源参考图**像素级差 0.00**；30 秒 60 帧：背景重绘 **618** 次、帧间差 39.9、色相跟踪误差 4.9°、窗口位移 **0px**、无递归变暗 |
| 3 EXCLUDEFROMCAPTURE 副作用 | **通过（含副作用）** | 外部观察者（≈录屏）：`WDA_NONE` 可见（标记 2209px）→ 开启后**标记 0px、窗口完全消失**。缓解方案"瞬时排除"实测：排除窗口 7.5–15.0 ms，2fps 占空比 **1.5%–3.0%**，清除后立即恢复可见 |
| 4 模糊半径合同 | **通过** | 合同 = Blur(96DPI 逻辑像素) 的等效 σ，3× 可分离 box（σ=√(r(r+1))）。Blur=0/12/24 上升沿 **0/33/65 px**，比值 1.97；降采样 0.25 路径仍保持。**"仅缩放"对照组恒为 4px、完全不响应 Blur** → 证明其无半径合同 |
| 5 支持矩阵 | **部分通过** | Win11 ✅｜Win10 ❓未验证｜负坐标屏 ✅（X=−1440，像素级正确）｜跨屏 DPI 168 vs 120 ✅｜RDP ❓未验证｜锁屏恢复 ❓未验证｜动态高面板 ⚠️几何等价通过、真实多卡未验证｜HDR 主屏 ✅（有 SDR 色调映射色偏限制） |

### 我特别认可的一点：它推翻了自己一批假阴性

A06 明确记录：过程中因试验装置自身缺陷（`System.Windows.Forms.Timer` **未 `Start()`**，背景根本没动）得到过全静止的 `temporalDiff=0` 数据，**该批结果已作废删除**；现所有结论均附"背景重绘次数"守卫（每段 26–27 次 / 30 秒 617–623 次），**背景没动时结论标记为无效而非"无响应"**。

这正是 v4 §4 第 1 条「不能凭成功返回值或 Mica 壁纸色判断为实时模糊」的最好示范 —— 它先踩了同样的坑（我先前读到的 `run-diag2` 那批 `HoleIsLive:false` 就是这批作废数据），然后**修好装置重跑**，而不是拿坏数据下结论。这个行为我会在 A14 独立审查时作为正面证据记录。

### 裁决

1. **DWM 系统材质（Mica/Acrylic）路线否决** —— API 成功 ≠ 材质呈现；分层生产窗口下完全无效果。
2. **`PrintWindow` 否决** —— 只能拿到窗口自身内容，永远看不到背后。
3. **唯一可行路线 = 屏幕捕获 + `WDA_EXCLUDEFROMCAPTURE`** —— 实测实时、像素级正确、无自反馈。
4. **M2 有条件放行**，但带两个硬约束：
   - **录屏副作用**：开启后面板从用户录屏/截图中消失。必须**显式设置 + 提示**，不得作为无提示的永久附作用（v4 §4 第 3 条明文要求）。
   - **性能超标**：480×1576 时 2fps = **40.6 ms/s**，超 v4 §7.4 的 40 ms/s 预算；480×3152 为 **50.0 ms/s**，明确超标 → **A15 必须自行降级**（推荐 `captureScale = 0.25`：处理 CPU 12.5 ms/帧、墙钟 p95 27.4 ms）。

### 未决事项 → 需产品决策

玻璃的**默认启用策略**属于产品取舍，不是技术事实：默认开启会让不知情的用户遇到"面板凭空消失"，与 v4 §4 第 3 条"不能无提示"相悖；默认关闭则需要用户主动打开才能看到玻璃。

### ✅ 产品决策（用户已拍板，2026-10-20）

> **液态玻璃默认关闭；在设置里显式开启；首次开启时明确提示副作用。**

这是用户对上面那个取舍的明确选择，**本轮所有实现以此为准**：

| 落点 | 要求 |
|---|---|
| 默认状态 | `BackdropConfig.Mode` 默认仍是 `solid`；**不存在**任何"全局开启玻璃"的默认项。`Theme.Backdrop` 缺失或 `Mode != "glass"` → 完全走旧路径 |
| 开启入口 | 必须**显式**设置（主题 JSON 写 `"mode": "glass"`，或后续编辑器里的开关）。**不得**提供"默认所有主题开启"的路径 |
| 首次提示 | 首次开启玻璃时，必须**一次性**明确告知副作用：*开启后 LiteMonitor 面板将从录屏/截图中消失*。提示文案要写清这是 `EXCLUDEFROMCAPTURE` 的必然结果，而非故障 |
| 提示时机 | 在**用户主动开启的那一刻**，不是每次启动，也不是后台静默 |
| 实现约束 | 提示必须可被关闭/记住选择；不得每次切主题重复弹；不得在无用户意图时弹 |
| 与 M2 的关系 | 这**不改变** A15/A16 的技术路线（仍是捕获 + 瞬时排除），只决定**默认是否开启**。A15 仍须实现 G-2 的瞬时排除缓解，把副作用压到 1.5%–3% 占空比 |
| A17 验收 | 必须补一条：默认安装下**不得**出现任何捕获行为（`EXCLUDEFROMCAPTURE` 未设置、无抓屏线程）；首次开启提示真实可用；用户拒绝开启时 M1 功能完全不受影响 |

**这条决策同时意味着**：M2 的实时玻璃是**用户主动开启的可选增强**，不是默认体验。M1（机能风静态装饰）仍是本轮的主交付。


### 交接项登记

| 项 | 转交 | 说明 |
|---|---|---|
| **G-1** | **A15** | 可直接移植的参考实现：`Capture/BlurPipeline.cs`（BlurContract + box 模糊）、`Capture/CaptureBackends.cs`、`Native/Interop.cs`、`Probe/ProbeForm.cs`（生产窗口条件复刻：`WS_EX_LAYERED`、`Form.Opacity=0.85`、GDI+ 文本、`OnPaintBackground` 空实现、Win11 DWM 圆角） |
| **G-2** | **A15** | **必须实现"瞬时排除"**（每帧短暂设置 → 抓完立即清除），把录屏副作用压到 1.5%–3% 占空比；不得长期保持排除态 |
| **G-3** | **A15** | 捕获分辨率按 §7.4 自动选择，生产建议 `captureScale = 0.25`；超预算时按 v4 §3.4 降级并**记录生效状态**（不得静默降级） |
| **G-4** | **A15/A16** | **不带 `CAPTUREBLT` 的 `BitBlt` 同样会抓到分层窗口**（本机实测，与常见假设相反）→ 排除自身**只能**靠 `EXCLUDEFROMCAPTURE` |
| **G-5** | **A17** | 需人工复测：Win10 实机、真实 RDP、锁屏-解锁恢复、**真实 OBS/游戏栏录屏下是否闪动**（瞬时排除缓解方案**尚未在真实录屏软件下验证**） |
| **G-6** | 主集成者 | `tests/AppearanceBackdropProbe/AppearanceBackdropProbe.csproj` **不入 sln**（同 A08 裁定：所有 `tests/Appearance*Probe/**` 一律不入 sln） |

---

## A09 审查结论 —— 通过，**并带出 3 条 A13 接线硬要求**

**交付物**：`src/UI/Helpers/WindowAppearanceController.cs`（741 行，新组件，命名空间 `LiteMonitor.src.UI.Helpers`）、
`tests/AppearanceWindowShapeProbe/**`、`docs/superpowers/plans/2026-10-20-appearance-a09-window-shape.md`

### 我亲自复核过的点（逐行读实现 + 核对探针数据）

| 项 | 复核方式 | 结果 |
|---|---|---|
| 未越界 | `git status` + 文件清单 | ✅ 只新增 1 个源文件 + 探针 + 文档；`MainForm_Transparent.cs`、`MainFormWinHelper.cs`、`LiteMonitor.csproj`、`app.manifest` **均未改动** |
| DWM 属性值 | 读实现 + 探针实测 | ✅ 属性号 `DwmwaWindowCornerPreference = 33`；切角恒传 **1**（并**断言 ≠ 0**）；`DwmGetWindowAttribute` 读回：切角=**1**、圆角=**2**、无框=**2** |
| **Region 所有权**（最关键） | 读实现 + 查 .NET 8 `Control.Region` setter 源码 | ✅ `GraphicsPath` 由组件创建、构造出 `Region` 后**立即释放**（Region 复制路径内容）；`Region` 赋给 `Control.Region` 后**移交给 WinForms 持有并释放**，组件**绝不 Dispose** → `RegionsDisposedByController` **恒为 0** |
| 不新增 Resize 绑定 | grep 组件文件 | ✅ 组件**不绑定任何事件**，只暴露 `Reapply()`；注释明写"由 A13 保留**一处** Resize 绑定并在其中调用" |
| 可逆性 | 探针 11/11 | ✅ 初始 `SystemRounded/Region=null` → 切角 → 圆角 → 无框，**最终状态与初始完全一致**，`form.Region == null` 无残留 |
| 句柄无泄漏 | 探针两轮分测 | ✅ 200 轮切换：GDI 16→16→**16**、USER 16→16→**16**、内核句柄 260→261→**261**（第 2 轮不再涨）；`GraphicsPath` 创建 407 = 释放 **407**；`Region` 创建 407 / 组件释放 **0** |
| 物理像素只用一次 | 探针扫描线读数 | ✅ 192 DPI 下 `FrameLengthPx=16` → 轮廓切角正好在 **16.0** 物理像素（若二次缩放会是 32）；96 DPI 下为 **8.0** |
| 边界 | 探针 7 尺寸 × 4 样式 | ✅ **28 组合全部无异常、无非法 Region**：0×0 / 0×120 / 240×0 一律不设 Region；1×1 切角得到 1×1 合法 Region；1×1 圆角因半径被夹紧为 0 → 交回系统 |
| 线程与销毁 | 探针 | ✅ 非 UI 线程调用抛 `InvalidOperationException`；窗体 `Dispose` 后再调 `Apply` 不抛异常且 DWM 调用数保持 2→**2** 不变 |

### ⚠️ 它顺带发现的**既有隐患**（A13 必须处理）

`MainFormWinHelper.ApplyRoundedCorners` 里的 `_form.Region?.Dispose()` **之后再赋新值** ——
赋给 `Control.Region` 的实例是**由 WinForms 持有**的，组件主动 Dispose 它会造成**悬空引用**。
A09 明确按正确语义实现（置空即释放、绝不主动 Dispose），并把这个隐患写进了自己的文档。

### 🔴 A13 接线清单补充（3 条硬要求，覆盖原接线表第 6/7 项）

| # | 补充项 | 说明 |
|---|---|---|
| **7a** | **窗口形状只能有一个写入者** | `MainFormWinHelper.ApplyRoundedCorners` 与 `WindowAppearanceController` **都会写窗口形状** → 必须收敛为唯一写入者，**不能让两个写入者打架**（否则一边设 Region、一边设 DWM 角偏好，互相覆盖） |
| **7b** | **移除 `_form.Region?.Dispose()`** | 该写法是悬空引用隐患。改由 `WindowAppearanceController` 接管后，置空即释放，组件不主动 Dispose |
| **7c** | **Resize 绑定收敛为唯一一处** | 现状 `MainFormWinHelper` 的 `Resize` 事件被绑了**两次**；收敛后在该唯一入口调用 `Reapply()` |
| **7d** | 旧主题观感必须不变 | `WindowAppearanceController` 的"旧路径"= DWM=2 + `Region=null`，与既有 `ApplyRoundedCorners` 在 Win11 上**完全一致** → 旧主题观感不变（这条已由 A09 实现保证，A13 接线时不要改坏） |
| **7e** | 测试接缝收敛 | `DwmSetWindowAttributeInterceptor` / `DwmCornerSupportOverride` 是测试接缝，生产保持 null。**并入 L-01 批次**改为 `internal` + `InternalsVisibleTo` |

---

## A12 审查结论 —— 通过

**交付物**：`resources/themes/CyberPunk_Mech.json`（新）、`tests/AppearanceThemeCheck/**`（新，含 2 个夹具）、
`docs/superpowers/plans/2026-10-20-appearance-a12-theme.md`（新）

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 10 个旧主题未被动 | 读 worker 的复算表 + `git diff --stat -- resources/themes` | ✅ **10/10 与 A00 基线一致**，且 diff 为空（字节未变） |
| tint/Opacity 指引被采纳 | 读主题 JSON | ✅ `backdrop` 段显式写 `"opacity": 0` —— **这正是我追加的指引**（否则默认 tint 会盖掉 75% 渐变）；同时保留 `"tint": "rgba(0,229,255,0.25)"` 供 `edgeGlow` 取色 |
| 未知键语义正确 | 读主题 JSON | ✅ `"font": { "scale": 1.0 }` —— v4 §3.1 规定 `font.scale` **只作为扩展数据保留**，不宣称存在 `Font.Scale` 属性。它按扩展键写入，往返后原样保留 ✅ |
| 往返保存 | 读 worker 实测 | ✅ `ThemeFileService.SaveTheme → LoadTheme` 后所有字段值不变；显式写出的键全部保留且值相等；**无重复键**；`noise`/`edgeGlow=0` 按 `WhenWritingDefault` 省略（正确行为） |
| 可读性判据 | 读 worker 的 WCAG 实测表 | ✅ 三个色板（暗底 / 亮底 / 复杂背景）**全部按程序化 WCAG 相对亮度计算**后选定，正文与数值 ≥ 4.5:1、非文本 ≥ 3:1、组块可分 ≥ 1.2:1。复杂夹具的 `valueCrit` 由 `#FF5C7A` 提亮为 `#FF6B85`（余量不足 4.5）—— **它自己发现并修正了余量问题** |
| 未越界 | 看文件清单 | ✅ 只动独占写集；10 个旧主题、`ThemeManager.cs`、`ThemeAppearanceOptions.cs`、`UIUtils.cs`、`ThemeFileService.cs`、`src/UI/**`、`src/System/**`、`LiteMonitor.csproj`、`.gitignore` 全部未碰 |
| 夹具不进用户主题列表 | 读它的假设说明 | ✅ 夹具放在 `tests/AppearanceThemeCheck/fixtures/` 而**不是** `resources/themes/` → 不会出现在用户的换肤下拉菜单里。**这个判断是对的** |

### 它把四条规则变成了可执行断言（值得作为方法论记录）

它没有只写文档，而是把四条易漂移的规则做成了 **330 项可执行判定**：
① 非 solid 模式必须显式写 `opacity`；② `opacity > 0` 必须显式写 `tint`；
③ 透明 tint 不破坏 glass→gradient→solid 降级链（`IsGradientUsable` 只看渐变两端 alpha）；
④ 往返保存不丢字段、不产生重复键。
→ 全部通过、0 项失败。

### 强度→alpha 公式（已核对 A07 的实现，与它写入文档的一致）

| 效果 | 公式 | 主主题取值 → 实测 alpha |
|---|---|---|
| 扫描线 | `round(80 × Scanline/100 × BaseColor.A/255)` | 30 → **24** |
| 网格 | `round(60 × Grid/100 × BaseColor.A/255)` | 20 → **12** |
| 外框辉光 | `round(200 × Glow/100 × ShadowColor.A/255)` | 55 → **38** |
| 噪点 | `round(255 × Noise/100 × 0.35)` | 0 → 不产生 |

### 主集成者处理的一个遗留项

A12 报告 `tests/AppearanceThemeCheck/output/` 等新探针的 output 目录**不在 `.gitignore` 覆盖范围内**（原 `.gitignore` 只忽略了 `tests/LiteMonitor.PerformanceProbe/output/`）。
**我已扩充 `.gitignore`**，加入 `tests/Appearance*/output/`。

- **依据**：这些目录合计约 **5×17 MB**（各探针的构建产物与运行期副本），且**可由探针工程重新生成**；
  各包的**结论数据已全部写入 `docs/superpowers/plans/` 下的结果文档**（含全部数据表与支持矩阵）。
  这与既有 `tests/LiteMonitor.PerformanceProbe/output/` 的处理方式一致。
- **不影响审查**：A14 的独立审查在**同一工作区**进行，忽略只影响提交，不影响现场取证。
- **注意**：`tests/AppearanceFixtures/appearance-fixtures.json` **不在** output 目录里 → 仍会被跟踪，
  它才是夹具的**唯一事实来源**（A04 探针的运行时主题发现依赖它）。

### A12 边界（worker 已诚实声明，本集成者认可）

- **无真实渲染证据** —— 本次只交付参数与判据，"像不像机能风"必须由 A14 的真实渲染裁决。
- 切角外框 / 外发光的实际观感未截图验证（alpha 公式可解释渲染结果，但**不替代**截图）。
- `glass` 模式未纳入夹具（实时捕获后端未就绪）。
- Win10/Win11 圆角差异未处理（`cornerRadius 0` 仅在 Win10 生效；Win11 的 DWM 圆角属 A09/A13）。
- 首次全量重编主工程会暴露 234 条既有警告，与 A12 无关。

---

## A10 审查结论 —— 通过，**并由主集成者裁决了一处跨包测试失效**

**交付物**：`src/ThemeEditor/AppearanceFields.cs`（新，46KB）、`src/ThemeEditor/ThemeEditorForm.cs`（改）、
`src/ThemeEditor/ThemePreviewControl.cs`（改，9.2KB → 20.9KB）、`tests/LiteMonitor.Tests/ThemeEditorAppearanceTests.cs`（新，33 用例）、
`docs/superpowers/plans/2026-10-20-appearance-a10-editor.md`（新）

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 未越界 | 看文件清单 | ✅ 未碰 `ThemeFileService.cs`（L-02）、`ThemeEditorForm.resx`、`src/Core|UI|System/**`、`resources/**`、csproj、其他测试文件 |
| **未改 resx** | 同上 | ✅ 控件全部**代码创建** → 避开 WinForms 设计器/resx 的风险。**这个选择是对的**（resx 手改容易埋设计器打开即报错） |
| 预览副本改用 `CreatePreviewCopy` | 读 `ThemePreviewControl` | ✅ 已从"逐字段手写 `new LayoutConfig {...}`"改为 `CreatePreviewCopy` → 不再漏新字段与扩展数据 |
| alpha 语义 | 读 worker 实测 | ✅ `0.5 → 128`、`1 → 255`、`0 → 0`、`0.25 → 64`、`0.1 → 26` —— **AwayFromZero 舍入**，与 v4 §3.1 一致；用颜色对话框挑色时**保留当前 alpha**；非法 alpha/坏颜色 → **拒绝并回退，文本不变** |
| 取消 / 初始化零写盘 | 读 worker 实测 | ✅ 默认主题目录**全目录 `文件名|SHA256` 快照前后一致**；A→B 后 **A 的 SHA256 逐字节不变**、B 变化。这是强证据，不是"看起来没写" |
| 恢复缺省 | 读 worker 实测 | ✅ 恢复后 JSON 里对应键**消失**（`WhenWritingDefault/Null`）——**这是正确行为**，不是 bug |
| 预览不启动捕获 | 读 worker 报告 | ✅ 玻璃用**固定样例图**并在界面上标注"预览样例图 / Preview sample（非实时桌面捕获）" |
| 未知值不静默改写 | 读 worker 报告 | ✅ 未知值原样保留 |

### 🔧 主集成者裁决的一处跨包测试失效（A10 发现，非 A10 之过）

A10 报全量测试 **575 通过 / 1 失败**，失败用例是 A01 写的
`ThemeSchemaTests` → `旧主题逐个往返_已知语义与未知值都保持`。

**根因**：该测试遍历 `resources/themes/*.json` 的**所有**文件，并断言"每个主题的新外观开关都是 0、没有 backdrop"。
这个假设在 A12 新增 `CyberPunk_Mech.json`（`frameStyle=1`、带 `backdrop`）后**不再成立** —— 新主题本来就应该带新字段。

**裁决**：**改测试，不改主题**。新逻辑分两层：

1. **通用层（对目录里的所有主题）**：新外观开关 7 项、`backdrop` 的 10 个字段、有无 backdrop 的 null 语义、
   `border`/`shadow`/`textSecondary` —— 往返后**必须保持原值**。
2. **严格层（仅 A00 基线锁定的 10 个旧主题）**：额外断言"新开关全为 0"、"无 backdrop"、
   "`font.scale` 作为扩展数据保留" —— 把 v4 §3.1「旧主题不会自动出现新效果」这条硬要求**继续锁死**。
3. **新增守卫**：断言这 10 个基线旧主题**一个都不少** → 防止有人删掉旧主题导致上述严格断言空转。

**为什么不能用"按内容判定旧主题"**（比如"没有新字段就算旧主题"）：那样一来，
将来有人**误把新外观开关加进某个旧主题**，测试反而会静默通过 —— 正好丢掉它本该抓的东西。

**修复后验证**：`ThemeSchemaTests` + `ThemeEditorAppearanceTests` → **56/56 通过**，EXIT=0。

### 交接项登记

| 项 | 转交 | 说明 |
|---|---|---|
| **E-1** 预览的扫描线/网格是**假绘制** | **A13** | A10 已明确标注"待 A07 交付正式装饰渲染器后替换 `ThemePreviewControl.DrawPreviewTexture` 的假底纹，并接入辉光与 BarStyle" |
| **E-2** 预览**不按 DPI 重建字体** | **A13** | 合同要求字体只借用不释放 → 跨 DPI 时预览字号与主窗不一致。记为已知限制 |
| **E-3** 预览靠"透明背景变体"让背景层透出 | **A13** | **背景归属必须在 `UIRenderer` 侧正式收口**，不能靠预览侧变通 |
| **E-4** 首次开启玻璃的提示 | **A16** | 按用户决策，编辑器/设置里开启玻璃时必须给出一次性副作用提示 |

---

## 提交清单（显式路径，**禁止 `git add -A`**）

工作区里同时存在**用户自己**的备份与**另一条线**（硬件稳定性）的未提交改动。
提交时**只**加下面列出的路径，其余一律保留在工作区不动。

### 必须排除（重要）

| 路径 | 原因 |
|---|---|
| `LiteMonitor-old-config-20261020.zip` | **用户自己的备份**，全程未读写，**绝不进版本库** |
| `docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md` | 硬件稳定性线（非本计划） |
| `docs/superpowers/plans/2026-10-20-hardware-stability-hardening.md` | 硬件稳定性线 |
| `docs/superpowers/plans/2026-10-20-hardware-stability-parallel-dispatch.md` | 硬件稳定性线 |
| `docs/superpowers/plans/2026-10-20-performance-sampling-merge.md` | 性能采样线 |
| `docs/superpowers/plans/2026-10-20-performance-sampling-verification.md` | 性能采样线（**已跟踪文件的修改**，不 `git add` 它即可保留在工作区） |

### 本次提交应包含的路径

**已跟踪文件的修改**：
`.gitignore`、`docs/APPEARANCE_PLAN.md`、`src/Core/ThemeManager.cs`、`src/ThemeEditor/ThemeFileService.cs`、
`src/ThemeEditor/ThemeEditorForm.cs`、`src/ThemeEditor/ThemePreviewControl.cs`、
`src/System/WebServer/WebPageContent.cs`、`src/System/WebServer/WebSessionManager.cs`、
`tests/LiteMonitor.Tests/ThemeSchemaTests.cs`、
`tests/LiteMonitor.PerformanceProbe/LiteMonitor.PerformanceProbe.csproj`、
`tests/LiteMonitor.PerformanceProbe/ProbeEnvironment.cs`、
`tests/LiteMonitor.PerformanceProbe/Program.cs`、`tests/LiteMonitor.PerformanceProbe/README.md`

**新增文件 / 目录**：
`src/Core/ThemeAppearanceOptions.cs`、`src/System/Backdrop/`、`src/System/WebServer/WebThemeCss.cs`、
`src/UI/Decor/`、`src/UI/Helpers/WindowAppearanceController.cs`、`src/ThemeEditor/AppearanceFields.cs`、
`tests/LiteMonitor.Tests/{ThemeSaveTests,ThemeSchemaTests,AppearanceOptionsTests,WebThemeCssTests,ThemeEditorAppearanceTests}.cs`、
`tests/LiteMonitor.PerformanceProbe/{AppearanceFixture,AppearanceProbe}.cs`、
`tests/AppearanceFixtures/`、`tests/AppearanceBackdropProbe/`、`tests/AppearanceDecorProbe/`、
`tests/AppearanceDecorRendererProbe/`、`tests/AppearanceStaticBackdropProbe/`、`tests/AppearanceThemeCheck/`、
`tests/AppearanceWindowShapeProbe/`、
`docs/superpowers/plans/2026-10-20-appearance-*.md`

> `tests/Appearance*/output/` 已被 `.gitignore` 覆盖，**不会**进提交。

---

## A07 审查结论 —— 通过，**并由主集成者裁决了一处性能超支**

**交付物**：`src/UI/Decor/DecorRenderer.cs`（新，29.9KB）、`src/UI/Decor/BarRenderer.cs`（新，14.7KB）、
`tests/AppearanceDecorRendererProbe/**`（新，7 个文件）、`docs/superpowers/plans/2026-10-20-appearance-a07-decor-renderer.md`（新）

### 我亲自复核过的点

| 项 | 复核方式 | 结果 |
|---|---|---|
| 三个入口**刻意分离** | 读 `DecorRenderer` 的公开方法 | ✅ `RenderWindowTexture` / `RenderGroupDecor` / `RenderWindowFrame` 各自独立，**不提供合并方法** → 接线时无法破坏固定层次顺序。这是对 v4 §3.3 的结构性保护 |
| 关闭态早退 | 读三个入口的早退条件 | ✅ 底纹：`!ScanlineEnabled && !GridEnabled → return false`（不进缓存、不建画刷）；组装饰：三开关全关 → `return false`；窗口边框：`!FrameEnabled → return false`（**辉光依附外框**，没外框就没有可依附的边） |
| 旧条完全走旧逻辑 | 读 `BarRenderer.RenderBar` | ✅ `BarStyle == BarStyleLegacy` 时**只**调 `UIUtils.DrawBar(g, item, theme)`，一个像素都不改 |
| 阈值取色不变 | 读 `BarRenderer` | ✅ 复用 `UIUtils.GetStateColor(item.CachedColorState, theme, false)` → **不借机启用旧 `BarLow`、不改安全值颜色** |
| 渐变不按百分比建缓存 | 读 `DrawGradientBar` | ✅ `GetOrAddGradientSlot`（完整条槽）+ `FillGradientSlot`，再按 `ratio = fillWidth / slotWidth` 裁剪 |
| 不凭空加条 | 读 `RenderBar` | ✅ `item.BarRect` 无效宽高 → `return false` |
| 性能工程 | 读其自陈 | ✅ 把 scanline+grid **合并成一个缓存 tile**（一次 alpha 混合代替两次）；用**矩形补集扫描**代替逐组 `Region` 排除（省掉每帧 GDI 句柄）；组纹理直接走组的圆角路径填充 |

### worker 实测数据（我核对了结论方向）

- **关闭态零副作用**：缓存条目 / 创建资源 / 位图字节 / 可见像素 / 计时器全部 **0** ✅
- **幂等**：100 次 Paint 哈希完全一致 ✅
- **层次正确**：文字**实心笔画像素 2,984 个被底纹改变 0**（AA 边缘 474 个属 GDI 字形与底色混合的固有行为，已如实标注）✅
- **边界**：8 个场景客户区外被改动像素**全 0** ✅
- **旧主题一致**：10 个旧主题新旧路径哈希逐一相同、差异 0；旧条哈希相同 ✅
- **横屏**：条数 0，新路径与"旧横屏+底纹+外框"参考哈希相同 ✅
- **缓存平台**：10,000 次变化进度值 → 缓存条目恒 **3**、淘汰 0 ✅

### 🔧 裁决一：4 卡 + 两倍物理尺寸档超预算 —— **接受超支、给出数据与原因，不引入节流**

实测：普通档 240×788 全开 **0.70 ms/帧**（≤1 ms ✅）；4 卡档 **2.40 ms/帧**（超 2 ms 预算 0.40）。
层次分解：窗口底纹 0.51 + 组底纹/组框 1.36 + 窗口边框/辉光 0.23 ms。

A07 提出的候选方案是"**底纹低频重铺**"（不每帧重铺，靠跳过绘制节流）。
**我否决这个方案**：v4 §3.3 明文规定「**每次 Paint 都绘制有效缓存，不能用跳过绘制来节流**」。

**我的裁决（按 v4 §7.4「达不到时减少效果/降级**或给出数据和原因**，不能自行放宽后仍写『通过』」）**：走"给出数据和原因"这条路径。理由：

1. 超支幅度 0.40 ms，而该档**基线**单帧就是 70.86 ms → 装饰层占比 **3.4%**；
2. 这是**离屏下界**（不含 DWM / 窗口呈现 / 桌面合成）→ 实窗只会更高，不会更低；
3. "减少效果"意味着砍掉机能风的核心三要素（扫描线 / 网格 / 辉光），而这三项在 v4 §3.1 里本来就是**用户可调的独立强度开关**（`scanline` / `glow` / `grid` 各 0–100）→ **降级路径已经由用户侧开关提供**，不必由实现方偷偷削弱；
4. 因此**不写"通过"**，按 v4 如实记为 **M1 已知超预算项**，并交给 A14 复测。

### ⚠️ 裁决二：口径问题 —— A07 的"4 卡 + 两倍物理尺寸档"**比 A04 的同名档小 42%**

| 来源 | 档位定义 | 物理尺寸 | 说明 |
|---|---|---|---|
| A04（基线） | 4 卡竖屏 @96DPI，缩放 2.0 | **480×2708** | 4 卡比 1 卡多 3 个组块（组块 7→10），高度是 1354 的两倍 |
| A07（成本） | 标称"4 卡 + 两倍物理尺寸" | **480×1576** | 1576 = 788×2，是 **1 卡**高度的两倍 |

→ A07 测的档位**高度只有 4 卡两倍档的 58%**。也就是说，**在真正的 4 卡 + 两倍物理尺寸档（480×2708），装饰层成本会高于 2.40 ms**。

**处置**：A14 **必须**用 A04 的同口径矩阵（4 卡 @96/2.0 = 480×2708）在 A13 接线后复测，
并把"装饰层额外 CPU"与"基线 70.86 ms"分开报告。**不得**用 480×1576 的数字代替。

### 其他已记录的边界

- 抗锯齿边缘像素会被底纹改变（GDI 字形边缘与底色混合的固有行为，**不是**"底纹覆盖文字"）；文字实心笔画像素已逐像素一致。
- 窗口底纹按组块**包围矩形**排除 → 组块圆角外 4 个小角不铺底纹（肉眼不可见）。
- **半透明组块背景**时窗口底纹铺满整个客户区 → 该场景成本回到"整屏一遍"。
- 离屏渲染**不含 DWM / 窗口呈现**，所有性能数字为**下界**。
- worker 自己修正了交付前的表述不准确：原写"关辉光档稳在预算内"，复跑实测 1.97↔2.10 ms 压在预算线上，
  已改为"**压线**"口径，并让探针 JSON 的 `budgetNote` 改为内嵌实测值（原为固定文案，与实测不符）。
  **这种"自己推翻自己"的据实修正，比数字本身更值得记录。**





