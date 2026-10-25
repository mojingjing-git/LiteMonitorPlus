# 执行 Plan：R06 架构债务清理（C → A → B）

- 日期：2026-10-24
- 依据：`docs/plans/2026-10-24-architecture-debt-requirements.md`（审核通过，附 3 处口径修正，见 §0）
- 基线：`70406b6` + 当前未提交改动
- 硬约束（每批结束都必须复验）：`dotnet build` 0 错误；`dotnet test` 发现数 = **1862**（**B6 起 = 1864**，LM-009 新增 2 条用例，`ci.yml` / `release.yml` 两处阈值逐字同步）；`git diff --check` 无告警；索引无 `i/mixed`/`i/crlf`；R01 MainForm 真实构造步骤绿。

---

## 0. 审核结论（对需求文档的 3 处口径修正，实施时以本 plan 为准）

1. **"Core 下 7 个文件直接 using System.Windows.Forms / System.Drawing" 口径含混**：实测 `using System.Windows.Forms` 在 `src/Core` 仅 **1 个文件**（`Actions/AppActions.cs`），随 A1 迁出即清零；`System.Drawing` 为 7 个文件（AppActions、MetricItem、Settings、SettingsHelper、ThemeAppearanceOptions、ThemeManager、UIUtils）。A-5 断言仍按需求原文执行（期望 0），达成路径以本修正为准。
2. **B2"架构断言先红后绿"与 CI 全绿门禁冲突**：CI 单 job `build-test-package` 同时服务 push/PR。plan 采用需求文档中的备选档：**新断言步骤先以 warning 档上线（B2 批），B6 结束时统一转 error**，避免主干出现红灯窗口。
3. **并发工作线确认**（2026-10-24 12:00 实测）：`Settings.cs` / `SettingsHelper.cs` / `MetricUtils.cs` / `HardwareValueProvider.cs` / `SystemHardwarPage.cs` 仍处于修改中（SYS.Power 工作线）。**A2 批次在这些文件收口前不得启动**；A2 启动前先执行 `git status --porcelain` 复核并请主人确认。

---

## 1. 批次 B1 —— R06-C 命名空间统一（纯机械，~233 文件）

### 步骤

1. **C1 去前缀**：全仓（`src` 122 / `tests` 106 / `tools` 5 文件）把 `LiteMonitor.src.X` → `LiteMonitor.X`。做法：脚本顺序替换（长前缀先替换，避免短前缀误伤）：`LiteMonitor.src.SystemServices` → `LiteMonitor.SystemServices`，其余 `LiteMonitor.src.` → `LiteMonitor.`。同时处理 `namespace` 声明、`using`、全限定名与注释中的字面量。
2. **C2 裸命名空间归位**（26 个文件，清单已实测锁定）：
   - `src/Core/*` 4 个 → `namespace LiteMonitor.Core;`
   - `src/UI/*` 20 个 → `namespace LiteMonitor.UI;`（子目录文件按目录，如 `Controls/TrendChartControl.cs` → `LiteMonitor.UI.Controls`）
   - `src/System/Program.cs`、`UpdateChecker.cs` → `LiteMonitor.SystemServices`
   - 全部同步在 `src`/`tests` 引用点补 `using` 或改全限定名。
3. **C3/C4 白名单**：新建 `scripts/architecture-assert.ps1` 时内置白名单，首条即 `src/System/** → LiteMonitor.SystemServices`（含 `InfoService` 子目录）。
4. 复验：`dotnet build -c Debug` → `dotnet test`（发现数 1862）→ `git grep -c "LiteMonitor\.src\."` = 0（注释/XML doc 里的字面量一并清理）→ `git ls-files --eol` 检查 → 提交（独立 commit：`refactor(R06-C): namespace unification`）。

### 验收（对应需求 C-1..C-5）
全部由 B2 批上线的断言脚本机器判定。

---

## 2. 批次 B2 —— 「架构断言」CI job（先 warning 档）

### 步骤

1. 新建 `scripts/architecture-assert.ps1`（单脚本承载 A、C 两组断言，输出违规明细 + 非零退出码；`-WarnOnly` 开关只打印不失败）：
   - **C 组**：C-1 `namespace LiteMonitor.src.` 计数 = 0；C-2 裸 `namespace LiteMonitor;` 计数 = 0；C-3 目录↔命名空间一致性（白名单外 = 0）；C-4 `LiteMonitor.src.*` 下 public 类型数 = 0。
   - **A 组**：A-1..A-7（`src/Core/**` 的跨层 using、WinForms、UI 控件与绘图类型计数均 = 0）、A-8（三个文件路径不存在）。
2. `ci.yml` 在"构建（Debug）"步骤后新增步骤 **"架构断言（R06）"**：`pwsh scripts/architecture-assert.ps1 -WarnOnly`；`release.yml` 同步加同一步骤（error 档在 B6 才启用，两处逐字一致）。
3. 验收：本批结束时 C 组断言全绿（B1 已完成）、A 组断言全红（列表与需求 1.3 表一致，红即证明判别力——本仓"基线会失败"手法）；CI 整体仍绿。

---

## 3. 批次 B3 —— R06-A1 三个文件迁出

> 与并发工作线零交集（三文件均不在修改列表中），可先行。

1. `src/Core/UIUtils.cs` → `src/UI/Utils/UIUtils.cs`，命名空间 `LiteMonitor.UI.Utils`。
   **★ 实施修正（与原计划不同，必须知道）**：只搬文件是不够的 —— UIUtils 里的
   「字符串驻留」和「状态色解析」是 Core/Plugins/System/UI 共同依赖的**纯数据能力**，
   留在 `LiteMonitor.UI.Utils` 会让 Core 反向依赖 UI，A-1 断言永远不可能为 0。
   ⇒ 拆分：`src/Core/StringPool.cs`（Intern / ClearStringPool）、
   `src/Core/MetricColors.cs`（GetStateColor）留在 Core，
   UIUtils 只保留 DPI 缩放 / GDI+ 资源缓存 / 绘制助手三节。
2. `src/Core/Actions/AppActions.cs` → `src/UI/Actions/AppActions.cs`，命名空间 `LiteMonitor.UI.Actions`（WinForms/Drawing using 随之迁走，Core 的 WinForms 清零在本批达成）。
   `src/Core/Actions/SettingsChanger.cs` **不迁**（A1 只点名 3 个文件）⇒ 引用它的
   `src/UI/Settings/MonitorPage.cs`、`src/UI/Settings/PluginPage.cs`、`src/UI/SettingsForm.cs`
   保留 `using LiteMonitor.Core.Actions;`。
3. `src/Core/Actions/WebActions.cs` → `src/System/WebServer/WebActions.cs`，命名空间 `LiteMonitor.WebServer`。⚠️ 默认按需求文档档位执行；若主人裁定留 Core，改为经接口，此步替换为抽取 `IWebServerHandle`。
4. **★ 附加修正**：`src/Core/ThemeManager.cs` 的 `ClearCaches()` 里调 `UIUtils.ClearBrushCache()`
   是 Core→UI 的反向依赖。实测 `ThemeManager.ClearCaches()` **无任何调用方**（死代码），
   且 UI 侧已有释放点（`src/UI/UIController.cs:182/1660`、`src/UI/UIRenderer.cs:22`、
   `src/UI/Helpers/MainFormBizHelper.cs:206`）⇒ 删除该调用，Core 不再引用 UI。
5. 全仓引用点（src/tests/tools）同步改；`dotnet build` + `dotnet test`（1862）+ `scripts/architecture-assert.ps1 -WarnOnly` 查看 A-8 转绿。
6. 独立提交：`refactor(R06-A1): Core 三文件迁出（UIUtils / AppActions / WebActions）`（已落地：`91f5ae3`）。

---

## 4. 批次 B4 —— R06-A2 六处跨层引用解耦

> **前置硬门禁**：SYS.Power 工作线已收口（`git status` 干净 + 主人确认）。否则本批挂起。

按文件逐个独立小步提交，每步全量测试 + R01：

1. **`MetricItem.cs` / `MetricLabelResolver.cs` / `MetricUtils.cs`**：新增 `src/Core/IMetricValueSource.cs`（接口：取值 + 颜色覆盖所需最小面），`InfoService` 侧实现并注入；Core 内静态单例直调全部改走接口。**只换调用方式，不动调用时机**。同时把 `Label`/`Panel` 控件字段改为由外层持有的布局参数（`MetricItem` 保留 `Color`/`Point`/`Size` 值类型，见 A3 分级）。
2. **`Settings.cs` / `SettingsHelper.cs`**：插件市场记录类型下沉 `src/Core`（POCO/DTO），插件层负责从自身类型转换；`Font`/`Panel` 引用改为 `Font` 值引用合法保留（A3 值类型白名单）或迁至调用方。
3. **`ThemeManager.cs`**：渲染配置下沉为 POCO/枚举（`ThemeAppearanceOptions.cs` 的 `Graphics`/`Brush`/`Pen` 用法收敛）。
4. 每步复验门禁基线；断言脚本 A-1..A-7 逐项转绿。
5. 独立提交：`refactor(R06-A2): decouple Core from System/Plugins/Rendering`。

### 4.1 实施修正（实测，与上文步骤的差异）

- **取值能力下沉按"接口 + 静态接线点"实现**：新增 `src/Core/IMetricValueSource.cs`（`IMetricValueSource.GetValue(key)` + 静态 `MetricValueSource.Current/Register/GetValue`，未注册返回 `""`，与 `InfoService.GetValue` 缺键行为一致）。`InfoService` 自身实现该接口；接线点放在 `src/System/InfoService/InfoServiceMetricValueSource.cs`（`internal sealed` + `[ModuleInitializer]` 引导类），**刻意惰性**取 `InfoService.Instance`，因此 `InfoService` 的创建时机完全不变（避免本批引入行为变化）。
- **Core 侧不再直接引用 System/Plugins/Rendering 的任何类型**，改为数据契约下沉：`CommittedMemoryStatus/Snapshot/Display/Publisher` → `src/Core/CommittedMemoryContracts.cs`；`CpuVoltageSourceKind/Resolution/SourcePublisher` → `src/Core/CpuVoltageContracts.cs`；`MarketPluginRecord` → `src/Core/MarketPluginRecord.cs`；`PluginOutputKeys` → `src/Core/PluginOutputKeys.cs`；`ThemeRenderingConfig/ThemeFallbackStyle/ThemeFallbackStyleJsonConverter` → `src/Core/ThemeRenderingConfig.cs`。
- **渲染配置的方法留在 Rendering 层**：下沉到 Core 的 `ThemeRenderingConfig` 只保留数据属性与无参 `Validate()`；依赖 `LiteMonitor.Rendering.Contracts` 的 `ToContract()`、以及依赖 `LegacyFallbackStyleResolver` 的 `Validate(out diagnostics)` / `ValidateFallbackStyle()` 改为 `src/Rendering/Configuration/ThemeRenderingConfigExtensions.cs` 里的扩展方法（`namespace LiteMonitor.Rendering.Configuration`），`LegacyFallbackStyleResolver` 留在 Rendering 层不动，只把参数类型换成 Core 的 `ThemeRenderingConfig`。这样既满足 A-4（Core 不再 `using LiteMonitor.Rendering`），又不破坏 `RenderingRoutePolicy` 的调用面。
- **`MetricItem` 的 `Label`/`Panel` 控件字段未在本批处理**：需求 A3 分级把 WinForms 控件列为"必须消除"，但这属于 B5 批次范围（A-7 断言）；本批只做 A-2/A-3/A-4 三条跨层解耦，断言 A-2/A-3/A-4 已由 4/2/1 归零。


---

## 5. 批次 B5 —— R06-A3 收尾

1. 复跑断言脚本确认 A-6/A-7 = 0（B4 完成后应自然达成）；残余 `Graphics`/`Brush`/`Pen`/`Image` 用法若仍存于 `ThemeAppearanceOptions.cs`，随该文件一并迁出/收敛。
2. `Color`/`Point`/`Size`/`Rectangle`/`Font` **保留**（需求推荐档，不自造值类型）。
3. 断言脚本转 **error 档**（本批即可先转，A 组应全绿）。

### 5.1 实施修正（实测，与上文步骤的差异）

1. **A-7 的 6 处命中全部来自 `src/Core/ThemeAppearanceOptions.cs` 的 `Image`**（`BackgroundFrame` / `PreviewSource` 两个属性 + 私有构造两参 + `Create` 两参）。其余 `System.Drawing` 用法（`ThemeManager` 的 `Font`/`Color`、`MetricColors` 的 `Color`、`MetricItem` 的 `Color`/`Rectangle`、`Settings` 的 `Point`/`Size`）属需求"值类型保留"档 ⇒ **不动**。
2. **"迁出 `Image`" 的落地方式：预览固定源从「快照（options）」下沉到「渲染器（BackdropRenderer）」**，而不是另造一个 Core 侧数据类型（那会重新引入 Core→System 的反向依赖，违反 A2）：
   - `BackdropRenderer` 新增 `previewSource` 构造参数与 `Render` 可选参数，生效值 = 本次参数 ?? 构造值；非空即走"预览固定源优先"分支（后端调用次数 = 0，来源报 `PreviewFrame`）—— 语义与迁移前完全一致。
   - 新增诊断属性 `HasFrameSource` / `HasPreviewSource`，供探针断言"旧主题没有底图源"这类不变量。
   - `AppearanceRuntime` 把 `previewSource` 交给自己持有的 `BackdropRenderer`，并删除自己的 `_previewSource` 字段（否则 CS0414）。
   - `ThemePreviewControl` 兼容重载把样例图通过 `Render(gOff, options, previewSource)` 传给**调用方给的** backdrop —— 这一步是必需的：否则 `ThemeEditorAppearanceTests` 的 `EffectiveMode == Glass` 断言会退化失败。
3. **断言档位偏离**：本批**不**提前把 `architecture-assert.ps1` 转 error 档，仍按 §0 修正②在 B6 结束时统一转（提前转会与"CI 必须全绿"的门禁语义冲突）。B5 结束时 C-1..C-4 与 A-1..A-8 已全部 PASS。
4. 探针/测试同步点：`AppearanceBackdropSourceTests`（断言改为 `runtime.Backdrop.HasPreviewSource`）、`AppearanceM2Probe`（`RenderWith` 改传参 + 4 处规则文案）、`AppearanceStaticBackdropProbe`（`MakeOptions` 去掉两个 `Image` 参数，`Program.cs` 改 `new BackdropRenderer(source, preview)`）、`AppearanceWiringProbe`（旧主题不变量改用 `HasFrameSource`/`HasPreviewSource`）。

---

## 6. 批次 B6 —— R06-B 契约登记表 + LM-009 闭环

1. 新建 `docs/contracts/registry.json`：31 个编号逐条登记（`id`/`title`/`observable`/`check`/`status`）。
2. **LM-009 闭环**（首个 observable 契约）：`PluginExecutor.cs:1373` 的错误发布路径补 `Reason` 透出（error 为 `PluginSandboxException` 时拼入 `LM009_` 前缀原因码）；新增测试断言"触发沙箱上限时错误日志含 `LM009_`"。**红→绿**：先在未修实现上跑一次确认红，再修实现转绿（过程写入提交说明）。
3. **B4 孤立编号清零**：10 个单次编号（LM-001/005/006/034/040/047/063/078/080/097）逐个裁定：登记挂检查或 `status: retired` + 从代码注释移除编号。
4. 新增 CI 步骤 **"契约登记表一致性断言（R06-B）"**：`scripts/contracts-assert.ps1` 双向比对（`--update` 仅显式重生成，默认失败）；`release.yml` 同步。发现数变化须同步两处 `MIN_TEST_TOTAL`（LM-009 新用例属"新增覆盖"，阈值 +N，两处逐字一致）。
5. **断言全部转 error 档**：`architecture-assert.ps1` 去掉 `-WarnOnly`，两 workflow 同步。

### 6.1 实施修正（实测，与上文步骤的差异）

1. **登记表落在 `docs/contracts/registry.json`**（31 条：**29 条 `status: active`** + **2 条 `status: retired`**），共 51 个 `check`。`check` 三类前缀：`test:<程序集>.<类>.<方法>`（程序集段 = `tests/` 下目录名）、`rule:<规则名>`（脚本内建）、`ci:<步骤名>`。
2. **降级口径**：`observable: false` 的两条是 **LM-063 / LM-070** —— 它们的护栏是 **IL 级断言**（`RenderingTransitionCoordinatorTests` 用 `IlInspector` 数 `endfinally` 次数；`LegacyFallbackThemeRoutingTests` 用 IL 比对 `IsGpuTheme` 与 `BuildDerivedTheme` 的调用偏移），属**结构不变量**而非运行时可观察行为。按需求 B3，这两条登记为 `status: retired` + 写明 `reason`，并从 `src` 注释中移除独立编号（`src/UI/UIController.cs:698/1546/1554/1664`、`src/UI/RenderingTransitionCoordinator.cs:846`；注释行数不变，登记表 `source` 行号仍有效）。其余 29 条 `observable: true`。
3. **LM-009 闭环（红→绿已实测）**：`src/Plugins/PluginExecutor.cs` 新增 `internal Action<string>? ErrorLogObserver { get; set; }`（生产默认 `null` ⇒ **不改变发布载荷与 UI 展示**）与 `internal static string ResolveSandboxReason(Exception error)`（沿 `InnerException` 链取 `PluginSandboxException.Reason`，非沙箱异常返回 `""`）；错误发布路径把 `" [LM009_*]"` 拼进日志行并经观测出口抛出。**RED**（只接观测出口、日志行暂不带原因码）：`沙箱上限触发时错误日志必须包含LM009_原因码` 失败（失败 1 / 通过 21 / 总计 22）；**GREEN**（补上原因码拼接）：22/22 全过。
4. **`scripts/contracts-assert.ps1`** 承载 B-1/B-5（双向差集）、B-2（每条 `check` 可执行：`test:` 指向真实测试方法、`rule:` 命中内建规则、`ci:` 命中 CI 步骤名）、B-3（降级口径）、B-4（孤立编号，含需求点名的 10 个一次性编号锚点）。`-Update` 只用于显式重生成登记表，默认模式必须失败。**判别力实测**：在 `src` 注入一个未登记编号 ⇒ 退出码 1（B-5 + B-4 两条命中）；把 `LM009_` 全部改成 `LMxxx_` ⇒ 退出码 1（B-2 内建规则命中）。
5. **CI 接入**：两个 workflow 各加同名步骤 **「契约登记表一致性断言（R06-B）」**（`& pwsh -NoProfile -File 'scripts/contracts-assert.ps1'` + `throw`），**插在 LM-024 护栏步骤之后** ⇒ 两处步骤块逐字一致（已用 `BatchEReleaseWorkflowGateTests.ExtractStep` 口径核对）。架构断言步骤同步去掉 `-WarnOnly`、步骤名改「架构断言（R06）」⇒ **error 档**（§0 修正②在 B6 收口）。
6. **发现数阈值调整**：`ci.yml` 与 `release.yml` 的 `MIN_TEST_TOTAL` 由 **1862 → 1864**（LM-009 新增 2 条用例属"新增覆盖"，按需求 §4 跨需求约束第 4 条两处**逐字一致**）。
7. **`ErrorLogObserver` 声明为 `Action<string>?`**：若声明为非空 `Action<string>` 会新增 1 条 `CS8618`（基线 537 警告）⇒ 改为可空后**零新增警告**（实测 build 0 错误 / 537 警告）。

---

## 6.5 执行记录（实测）

| 批次 | 提交 | 门禁结果 |
| --- | --- | --- |
| B1 (R06-C) | `d05a684` | build 0/537；发现 1862（通过 1861/跳过 1）；R01=0；C-1..C-4 全绿 |
| B2 (断言脚本) | `3169dc0` | 脚本 warning 档接入 ci.yml / release.yml |
| B3 (R06-A1) | `91f5ae3` | build 0/537；发现 1862（通过 1861/跳过 1）；R01=0；探针 21/22（AppearanceBackdropProbe 基线即坏）；A-1/A-5/A-6/A-8=0 |
| B4 (R06-A2) | `d4500c2` | build 0/537；发现 1862（通过 1861/跳过 1）；R01=0；探针 7/1/2（M2 门槛#7 基线即红，非本次回归）；A-2/A-3/A-4=0，A-7 仍 6（属 B5） |
| B5 (R06-A3) | `f69f74d` | build 0/537（与基线逐条对齐，仅 3 条 CS8604 行号平移，零新增）；发现 1862（通过 1861/跳过 1）；R01=0；探针 7/1/2（M2 门槛#7 基线即红，非本次回归；StaticBackdrop 21/21、Wiring 全通过、ThemeCheck 442 通过）；**A-6/A-7=0 ⇒ A 组 8/8 全绿**（断言仍为 warning 档，按 §0 修正②在 B6 统一转 error） |
| B6 (R06-B) | `65a8b94` | build 0/537（`ErrorLogObserver` 改为可空后零新增警告）；发现 **1864**（通过 1863/跳过 1，`assert-test-counts.ps1` 通过）；R01=0；探针 7/1/2（M2 门槛#7 基线即红，非本次回归）；**架构断言转 error 档全绿（C-1..C-4 + A-1..A-8）**；契约断言 exit 0（31 条登记 / 29 个 src 编号 / 29 active / 2 retired） |

---

## 7. 每批通用收尾清单

- [ ] `dotnet build LiteMonitor.sln -c Debug` 0 错误
- [ ] `dotnet test` 发现数 1864（B6 起；有变化走 §6.4 流程）
- [ ] `scripts/run-probes.ps1` 22 探针全过（B1 大改名后必跑；其余批次跑即可）
- [ ] `git diff --check` 无告警；`git ls-files --eol` 无 `i/mixed`/`i/crlf`
- [ ] `scripts/architecture-assert.ps1`（对应档位）+ `contracts-assert.ps1`（B6 起）
- [ ] 独立 commit，diff 只含本批内容

## 8. 挂起条件与回退

- B4 启动前 SYS.Power 工作线未收口 → 先跳到 B3/B6，B4/B5 顺延。
- 任一批门禁失败且 30 分钟内无法定位 → `git reset --hard` 该批起点，问题写入 `docs/reviews/` 后再试。
- WebActions 归属 / Color·Font 是否剥离 / B2 是否接受红灯窗口 —— 三个 ⚠️ 项若主人改判，只影响对应批次局部，plan 其余不变。
