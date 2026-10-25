# 架构债务清理需求文档（R06：Core 解耦 / 契约挂钩 / 命名空间统一）

- 日期：2026-10-24
- 类型：**需求文档**（非实施计划）—— 实施 plan 由主人另行转化；本文件只规定"要什么"和"怎样算做完"
- 基线：`70406b6`（master；工作区另含 2026-10-24 清理轮次的未提交改动）
- 适用范围：`src/` 的架构分层、契约可验证性、命名空间一致性
- 状态：**待主人拍板**（每节给出推荐档与影响面；标 ⚠️ 处需主人裁定）
- 来源：2026-10-24 工作目录清理轮次后的项目评价（实测数据见各节）

---

## 门禁基线（任何一项都不得回退）

| 项 | 基线值 | 判定方式 |
|---|---|---|
| 解决方案构建 | 0 错误 | `dotnet build LiteMonitor.sln -c Debug` |
| 测试发现数 | **1862**（通过 1855 / 跳过 7 / 失败 0） | `dotnet test`，且 `MIN_TEST_TOTAL: 1862` 门禁绿 |
| sln 外探针 | 22 个工程逐个构建 0 失败 | `scripts/run-probes.ps1` 或等效逐个 `dotnet build` |
| 空白 | `git diff --check` 无告警 | — |
| 换行 | 索引 `i/mixed`/`i/crlf` = 0 | `git ls-files --eol` |
| 主窗体启动 | 生产 `MainForm` 可真实构造 | `ci.yml` 中"R01：真实构造 MainForm"步骤 |

> **本需求全部为重构类，不改变任何用户可见行为。** 因此"测试发现数 = 1862"是硬约束：若某项改动导致用例数变化，须先说明是"删除冗余用例"还是"新增覆盖"，且删除冗余用例必须同步下调 CI 阈值并留档。

---

## 0. 需求总览

| 编号 | 需求 | 风险 | 触及文件 | 改行为? |
|---|---|---|---|---|
| **R06-A** | `Core` 层不再依赖 `UI` / `System` / `Plugins` | 中 | `src/Core/` 11 / 20 个文件 + 接收方 | 否 |
| **R06-B** | 契约编号（`LM-XXX`）挂上机器可执行的检查 | 中 | CI + 新增 registry | 否（新增断言） |
| **R06-C** | 命名空间统一为 `LiteMonitor.<区域>` | 低 | `src/` 122 + `tests/` 106 + `tools/` 5 个文件 | 否 |

**建议顺序：C → A → B**（C 是纯机械改名，先做完可让后续 diff 更易读；A 会动依赖方向，需要先有干净的命名空间；B 依赖 A 完成后契约落点稳定。）

---

## 1. R06-A —— `Core` 层去耦

### 1.1 问题陈述（实测）

`src/Core/` 名义上是"核心层"，实测却同时依赖 UI、系统服务、插件三层：

```
跨层 using 依赖（实测计数）
  UI        → Core 52 | System 20 | Rendering 11 | Plugins 2
  System    → Core 19
  Plugins   → Core 6 | System 5
  Rendering → Core 4
  Core      → System 8 | Plugins 3 | Rendering 1 | UI 1   ← 反向依赖
```

`Core` 的 20 个文件中 **11 个**存在跨层引用，其中：

- `Core → UI` 的**唯一来源**是 `src/Core/Actions/AppActions.cs`；
- `Core ↔ System`、`Core ↔ Plugins` 两个循环同样源于此文件与 `Settings*.cs`；
- `src/Core/` 下 **7 个文件**直接 `using System.Windows.Forms` / `System.Drawing`。

### 1.2 需求条目

#### A1 —— 三个文件整体迁出 `Core`（不做解耦，直接搬）

这些文件的职责本身就是 UI/适配层，放在 `Core` 是目录级误置：

| 现路径 | 行数 | 职责（实测公开面） | 目标路径 |
|---|---|---|---|
| `src/Core/UIUtils.cs` | 247 | DPI 缩放 `S(px)`/`S(Size)`/`S(Padding)`、`GetPen`/`GetBrush` 等绘图工具 | `src/UI/Utils/UIUtils.cs` |
| `src/Core/Actions/AppActions.cs` | 435 | `ApplyAllSettings` / `ApplyLanguage` / `ApplyWindowAttributes` / `ApplyVisibility` / `ApplyThemeAndLayout` / `ApplyMonitorLayout` / `ApplyTaskbarStyle` —— 全部以 `MainForm` / `UIController` 为入参 | `src/UI/Actions/AppActions.cs` |
| `src/Core/Actions/WebActions.cs` | — | 依赖 `SystemServices` + `WebServer` 的 Web 侧动作 | `src/System/WebServer/WebActions.cs` |

> ⚠️ **`WebActions.cs` 归属需主人裁定**：它也可以留在 `Core` 作为"跨端动作"，只要把对 `LiteMonitor.src.WebServer` 的依赖改为经由接口。本文件按"搬到 `src/System/WebServer/`"给默认档。

**验收**：迁移后原路径不存在；所有引用点（`src/` + `tests/` + `tools/`）改到新命名空间；行为零变化。

#### A2 —— 剩余跨层引用改为"经接口"或"下沉类型"

| 文件 | 当前依赖 | 需要消除的 | 建议方向 |
|---|---|---|---|
| `src/Core/MetricItem.cs` | `SystemServices.InfoService`、WinForms `Label`/`Panel` | 静态单例直调 + UI 控件 | 取值能力下沉为接口（如 `IMetricValueSource`），`InfoService` 在外层实现 |
| `src/Core/MetricLabelResolver.cs` | `SystemServices.InfoService` | 同上 | 同上 |
| `src/Core/MetricUtils.cs` | `SystemServices` + `InfoService` | 同上 | 同上 |
| `src/Core/Settings.cs` | `Plugins`、`System.Drawing`(`Control`/`Font`/`Label`) | 插件类型 + UI 控件 | 市场记录类型下沉到 `Core`（或以 DTO 隔离，插件层负责转换） |
| `src/Core/SettingsHelper.cs` | `Plugins`、`System.Drawing`(`Font`/`Panel`) | 同上 | 同上 |
| `src/Core/ThemeManager.cs` | `Rendering.Configuration` | 渲染层类型 | 渲染配置以 POCO/枚举下沉，或 `ThemeManager` 只暴露最小接口 |

#### A3 —— `System.Drawing` 按用法分级，不搞一刀切

实测 `Core` 里 `System.Drawing` 的真实用法分两类，**必须区别对待**：

- **值类型（允许保留）**：`Color` / `Point` / `Size` / `Rectangle` / `Font` —— Windows-only 桌面应用里这些是合法的领域值类型；为此自造颜色/矩形类型得不偿失。
- **UI 生命周期（必须消除）**：`Label` / `Panel` / `Control` / `Application` / `Timer` —— 这些是 WinForms 控件与生命周期入口，`Core` 不得触碰。
- **绘图层（建议迁出）**：`Graphics` / `Brush` / `Pen` / `Image` —— 出现在 `ThemeAppearanceOptions.cs` 与 `UIUtils.cs`；前者随 A1/A2 一并收敛。

> ⚠️ **需主人裁定**：是否把 `Color`/`Font` 也从 `Core` 剥离（例如引入自有 `RgbColor`）。**推荐不剥离** —— 收益是"纯度"，成本是全仓 184 个文件的类型转换噪声，与本次"不改行为"的约束冲突。

### 1.3 验收标准（机器可判定）

新增 CI 步骤"架构分层断言"，对 `src/Core/**` 扫描并断言：

| # | 断言 | 期望值 |
|---|---|---|
| A-1 | `src/Core/**` 中 `using LiteMonitor.src.UI*` 命中数 | **0** |
| A-2 | `src/Core/**` 中 `using LiteMonitor.src.SystemServices*` / `LiteMonitor.src.WebServer*` 命中数 | **0** |
| A-3 | `src/Core/**` 中 `using LiteMonitor.src.Plugins*` 命中数 | **0** |
| A-4 | `src/Core/**` 中 `using LiteMonitor.src.Rendering*` 命中数 | **0** |
| A-5 | `src/Core/**` 中 `using System.Windows.Forms` 命中数 | **0** |
| A-6 | `src/Core/**` 中 `System.Drawing` 的 UI 控件类型（`Label`/`Panel`/`Control`/`Application`/`Timer`）命中数 | **0** |
| A-7 | `src/Core/**` 中 `Graphics`/`Brush`/`Pen`/`Image` 命中数 | **0** |
| A-8 | `src/Core/UIUtils.cs`、`src/Core/Actions/AppActions.cs`、`src/Core/Actions/WebActions.cs` 是否存在 | **均不存在** |

**断言方式要求**：必须是**可执行的 CI 步骤**（脚本或测试），不得只写在文档里。本仓已有先例可参照：`ci.yml:184` 的"校验第三方组件声明与实际依赖一致（LM-024 护栏）"。

**非功能性验收**：门禁基线全绿；`dotnet test` 发现数仍为 1862；`ci.yml` 的"R01：真实构造 MainForm"步骤通过。

### 1.4 非目标（明确不做）

- 不重写 `UI` / `System` / `Plugins` 层的内部结构；
- 不引入 DI 框架（当前项目无 IoC 容器，引入会改变启动路径，风险与收益不成比例）；
- 不动 `src/Rendering/` 的 GPU 渲染架构（该层已自洽，且是本项目最有价值的部分）；
- 不因本需求调整任何产品行为、默认值或配置格式。

---

## 2. R06-B —— 契约编号挂上机器可执行的检查

### 2.1 问题陈述（实测）

`src/` 中引用了 **31 个不同的 `LM-XXX` 契约编号**，"合同"一词出现 **525 次、分布 45 个文件**。可追溯性是本项目最有价值的工程资产。

但契约与实现之间**没有强制绑定**，已实测到反例：

- **`LM-009`**（沙箱原因码，45 次引用 / 2 文件）声明了"沙箱失败必须暴露机器可读原因码"，但 `PluginSandboxException.Reason` 在 `src/` 内**零消费者**，`src/Plugins/PluginExecutor.cs:1373` 的 `PublishExecutionErrorAsync` 只写 `error.Message` ⇒ 命中沙箱上限时日志里**没有 `LM009_*`**，且**没有任何测试会发现**。
- **`RenderingRouteDecision.Errors`** 被计划文档描述为"供渲染路线页状态行与排障读取"，实际全仓无消费者。

同时，31 个编号中 **10 个只出现 1 次**（`LM-001` / `LM-005` / `LM-006` / `LM-034` / `LM-040` / `LM-047` / `LM-063` / `LM-078` / `LM-080` / `LM-097`）——这类引用是**孤立的追溯桩**，既没有配套检查，也没有被后续代码呼应。

CI 中已存在"契约挂钩"的**唯一先例**：`ci.yml:184` 的 LM-024 护栏。模式是被接受的，只是覆盖面为 1 / 31。

### 2.2 需求条目

- **B1 —— 建立契约登记表。** 新增一份机器可读的契约清单（建议 `docs/contracts/registry.json`，或 `.csproj` 的 `EmbeddedResource`）。每条记录至少包含：`id`（`LM-009`）、`title`、`observable`（bool：是否为**可观察行为**）、`check`（对应的机器检查标识：测试全名 / CI 步骤名 / 断言脚本路径）、`status`。
- **B2 —— 可观察契约必须挂检查。** `observable = true` 的契约，`check` 字段不得为空，且该检查必须在 CI 中真实执行。**`LM-009` 是首个必须补齐的**（补一条"沙箱上限触发时错误日志包含 `LM009_*`"的断言用例即可闭合）。
- **B3 —— 不可观察契约必须降级。** `observable = false` 的契约（如纯内部不变量、已下线功能的遗留编号）必须在登记表里写明理由，且**代码注释中不再单独出现编号**，避免制造"有编号就有保护"的错觉。
- **B4 —— 孤立编号清零。** 10 个只出现 1 次的编号，要么在登记表中登记并挂检查，要么按 B3 降级。**判据：登记表中不存在、代码中也不再出现的编号 = 0 个。**
- **B5 —— CI 强制闭环。** 新增 CI 步骤"契约登记表一致性断言"：扫描 `src/` 中出现的全部 `LM-XXX` 编号，与登记表做双向比对 —— 代码里有而登记表无 ⇒ 失败；登记表有而代码里无 ⇒ 失败（除非标记为 `status: retired`）。

### 2.3 验收标准

| # | 断言 | 期望值 |
|---|---|---|
| B-1 | `src/` 中出现的 `LM-XXX` 编号集合 ≡ 登记表中 `status != retired` 的编号集合 | 双向差集均为 **0** |
| B-2 | 登记表中 `observable = true` 的条目，其 `check` 对应的检查在 CI 中真实执行 | 无空 `check` |
| B-3 | 只出现 1 次的 10 个编号 | 全部已登记或已降级 |
| B-4 | `LM-009` 的可观察性断言存在 | 有，且**能失败**（红→绿验证过） |

> **"能失败"是硬要求**：新增的断言必须先在破坏实现时红一次，证明它有判别力 —— 这是本仓既有做法（多处基线会失败的断言），不接受恒真断言。

### 2.4 非目标

- 不要求把全部 31 个契约一次性挂钩；本轮只要求 **登记表 + B4 清零 + `LM-009` 闭环**，其余按 `observable` 分级排期。
- 不改动契约本身的语义（编号的含义、重构判据、错误码词表都保持现状）。
- 不引入外部契约管理平台（离线仓库 + JSON 足够）。

---

## 3. R06-C —— 命名空间统一

### 3.1 问题陈述（实测）

`src/` 的命名空间存在**三套并存**的约定（按文件计）：

| 类别 | 数量 | 样例 | 问题 |
|---|---|---|---|
| A 干净式 `LiteMonitor.<区域>` | 36 | `LiteMonitor.Rendering.Presentation`、`LiteMonitor.ThemeEditor` | 符合预期 |
| B VS 路径式 `LiteMonitor.src.*` | **122** | `LiteMonitor.src.Core`、`LiteMonitor.src.UI.Settings` | Visual Studio 按目录自动生成，**把文件夹结构泄漏进 API** |
| C 裸 `LiteMonitor` | **26** | `namespace LiteMonitor;` | 命名空间与目录完全脱钩，散落在全部 28 个子目录 |

**外部可见代价**：`LiteMonitor.src.*` 命名空间下共有 **237 个 `public` 类型**（`LiteMonitor.src.Core.Settings`、`LiteMonitor.src.Core.ColorConfig`、`LiteMonitor.src.Core.GpuKey` …）。这类名字对外而言是把仓库布局当成 API 契约。

**改造可行性已核实**：

- `LiteMonitor.Updater` 是**独立程序集且不引用主程序集**（无 `ProjectReference`）⇒ 改名**不会**破坏外部消费者；
- 主程序集仅通过 `InternalsVisibleTo` 向 `LiteMonitor.Tests` / `LiteMonitor.GpuNonperformance.Tests` / `AppearanceWindowShapeProbe` 三个测试程序集开放 internal ⇒ 这些程序集需要同步改引用，但**不受 public 面约束**；
- 影响面：`src/` 122 个文件 + `tests/` 106 个文件 + `tools/` 5 个文件。

### 3.2 需求条目

- **C1 —— 去除 `src.` 前缀。** `LiteMonitor.src.Core` → `LiteMonitor.Core`；`LiteMonitor.src.UI.Settings` → `LiteMonitor.UI.Settings`；`LiteMonitor.src.SystemServices` → `LiteMonitor.SystemServices`；`LiteMonitor.src.WebServer` → `LiteMonitor.WebServer`；`LiteMonitor.src.Plugins` → `LiteMonitor.Plugins`。**保留原有子层结构**（如 `LiteMonitor.UI.SettingsPage`、`LiteMonitor.Rendering.Gpu.Device`）。
- **C2 —— 裸命名空间归位。** 26 个 `namespace LiteMonitor;` 文件按其目录归入对应区域命名空间（如 `src/Core/MetricItem.cs` → `namespace LiteMonitor.Core;`）。
- **C3 —— 命名空间与目录一一对应。** 改造后，`src/<区域>/<子目录>/<文件>.cs` 的命名空间必须等于 `LiteMonitor.<区域>[.<子目录>]`。**例外白名单**需在脚本里显式列出（如 `src/System/` 下的文件统一叫 `LiteMonitor.SystemServices`）。
- **C4 —— 目录与命名空间的历史差异需显式记录。** 当前 `src/System/` 对应命名空间是 `SystemServices`（而非 `System`），这是**有意为之**（避免与 `System.*` 冲突）；`src/System/InfoService/` 对应 `LiteMonitor.SystemServices.InfoService`。这类差异必须写进白名单与文档，不能靠"约定俗成"。

### 3.3 验收标准

| # | 断言 | 期望值 |
|---|---|---|
| C-1 | 全仓 `namespace LiteMonitor.src.` 命中数 | **0** |
| C-2 | 全仓 `^namespace LiteMonitor;`（裸）命中数 | **0** |
| C-3 | 文件所在目录与声明命名空间不一致的文件数（白名单外） | **0** |
| C-4 | `LiteMonitor.src.*` 下的 `public` 类型数 | **0**（全部已迁至干净命名空间） |
| C-5 | 门禁基线 | 全绿，发现数仍为 **1862** |

> C-1 ~ C-4 均应做成 **CI 断言步骤**（脚本扫描），与 R06-A-3 的分层断言可合并到同一个"架构断言"CI job 中。

### 3.4 非目标

- **不改** `LiteMonitor.Rendering.*` 与 `LiteMonitor.ThemeEditor`（已符合目标约定）。
- **不合并** `System` → `SystemServices`（目录与命名空间的差异已存在且无害，改它会制造无谓 diff）。
- 不借改名之机调整任何类的可见性或拆分文件（那属于 R06-D，另行立项）。

---

## 4. 跨需求约束

1. **可独立交付**：R06-C / R06-A / R06-B 互不依赖，任一项单独完成都必须保持门禁基线全绿。
2. **不改行为**：三项均为重构。任何行为差异（哪怕是"更合理"）都不在本需求范围内，须另立条目并单独拍板。
3. **CI 断言优先于文档描述**：每条验收标准要么是一条 CI 步骤，要么是一条可在本机执行的脚本命令。**不接受"约定应该不会违反"**。
4. **发现数 1862 是硬约束**：重构不得增删用例。确需调整时，先在登记表里说明，再同步 `ci.yml` / `release.yml` 两处阈值（两处必须逐字一致）。
5. **不引入新依赖**：`PackageReference` 不新增；如需分析器须先评估离线还原可行性（CI 在 `windows-latest` 上执行，联网可用，但仍应最小化）。

---

## 5. 风险登记

| 风险 | 触发条件 | 影响 | 缓解 |
|---|---|---|---|
| **R-高** 命名空间改名漏改 | 233 个文件机械替换，个别 `using` 在字符串/注释/反射里 | 编译失败或运行期 `Type.GetType` 失配 | 依赖 `dotnet build` 全量兜底；改造后全仓 grep `LiteMonitor.src.` 复验 |
| **R-中** `Core` 解耦引发行为漂移 | `InfoService` / `Settings` 的调用时机被改动 | 面板取值时机变化 | A2 只允许"换调用方式"，不改调用时机；每步跑全量测试 + R01 主窗体检查 |
| **R-中** 契约断言写成恒真 | 新增断言没有判别力 | 制造"假绿"，比不写更糟 | 强制"红→绿"验证；复用本仓既有的 IL 级断言手法 |
| **R-低** 登记表与代码互相漂移 | 契约编号增删未同步 | CI 断言误报 | 断言脚本提供 `--update` 模式（一次性重生成登记表），但**默认模式必须失败** |
| **R-低** 三项同时进行导致 diff 不可读 | 叠加改动 | 复审成本上升 | 按 C → A → B 顺序分批，每批独立提交、独立复审 |
| **R-中** **目标文件正被另一条工作线并发修改** | 2026-10-24 11:40–11:46 观测到 `src/Core/Settings.cs`、`src/Core/SettingsHelper.cs`、`src/Core/MetricUtils.cs`、`src/System/HardwareServices/HardwareValueProvider.cs`、`src/UI/Settings/SystemHardwarPage.cs` 被同时修改（新增 `SYS.Power` 整机功耗派生项，对应 `docs/plans/2026-10-24-total-system-power-design.md`）。这 5 个文件中 **3 个正是 R06-A2 的解耦目标** | 两条工作线同时改同一文件 → 冲突、丢改动、或把对方的半成品带进提交 | **R06-A 实施前必须先确认该工作线已落地**；若仍在进行，先做 R06-C（纯改名、不碰这些文件的逻辑）或先做 A1（三个纯迁移文件），把 A2 留到最后 |

> **并发期间的工作区现状（2026-10-24 11:50 实测，供参考）**：`src/Core/Settings.cs` 处于 `w/mixed`（编辑器写入导致部分行为 LF），且该文件第 874 行有一处行尾空白 —— 均来自上述并发工作线，**不属本需求范围**，实施 R06-A 前应由其归属方先收口。

---

## 6. 建议实施顺序（供转化为 plan 时参考）

| 批次 | 内容 | 前置 | 可独立验收 |
|---|---|---|---|
| B1 | R06-C 命名空间统一（纯机械，233 文件） | 无 | ✅ |
| B2 | 「架构断言」CI job 上线（此时全部为**红**，作为后续批次的守门） | B1 | ✅（预期红） |
| B3 | R06-A1 三个文件迁出 | B1 | ✅ |
| B4 | R06-A2 六处跨层引用解耦 | B3 | ✅ |
| B5 | R06-A3 `System.Drawing` UI 控件清零 | B4 | ✅ |
| B6 | R06-B 契约登记表 + `LM-009` 闭环 + B4 孤立编号清零 | B2 | ✅ |

> B2 的"预期红"是刻意的：把守门先立起来，再逐项转绿。若无法接受 CI 出现红灯阶段，可把断言先设为 `warning` 一档，但**必须在 B6 结束时全部转为 `error`**。

---

## 7. 明确不做的事

1. **不删除任何现有文档**（`docs/reviews/` 49 份、`docs/superpowers/plans/` 55 份等）——那些是本项目的资产，不是负债；本轮评价中关于"文档债务"的讨论指的是**交叉引用失真**，不是文档数量。
2. **不合并 `LiteMonitor.Updater`** 进主程序集（它当前独立运行是有意设计）。
3. **不重写渲染层**（`src/Rendering/` 8885 行、GPU 路线自洽）。
4. **不碰 `secrets/`**（签名私钥，与本需求无关）。
5. **不改变 CI 触发条件**（`push` / `pull_request` 的分支规则保持现状）。

---

## 附：需求来源与实测口径

本文所有数字均来自 2026-10-24 的实测（`Get-ChildItem` + `git ls-files --eol` + `git diff HEAD --name-only` + 正则扫描），非估算。口径：

- **代码行**：`Get-Content` 逐行统计，注释行以 `///` / `//` / `/*` / `*` 开头计，空白行不计。
- **跨层依赖**：`^using\s+(LiteMonitor[...];)` 正则，按 `src/` 六个区域目录映射到命名空间前缀后统计；区域映射表见 R06-A 与 R06-C 的实测表。
- **契约编号**：`LM-\d{3}` 正则全文扫描 `src/**`，按编号聚合出现次数与涉及文件数。
- **门禁基线**：`dotnet build` / `dotnet test`（Debug，22 个 sln 外探针逐个构建）在基线 `70406b6` + 2026-10-24 清理轮次未提交改动上实测。
