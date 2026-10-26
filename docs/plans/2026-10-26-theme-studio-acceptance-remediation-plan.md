# 主题工作室 N00–N10 独立验收整改计划

- 日期：2026-10-26。
- 状态：**独立审核「有条件通过」→ v2.1 修订版**（首轮必须修改 M-1…M-5 与次要修改 S-1…S-13 已全部吸收；v2 聚焦复审的 1 必须项 V2-M-1 与 6 次要项 V2-S-1…V2-S-6 亦已全部吸收；复审确认「M-1…M-5 / S-1…S-13 全部已正确吸收，无残留」）。
- 触发：[独立验收报告](../reviews/2026-10-26-theme-studio-next-stage-acceptance-review.md)结论为「**不通过**」——10 条问题 R1–R10 + 3 条证据缺口 G1–G3；非资源 ThemeStudio 专项 31/31 通过，但真实路径探针复现了失效；全量未取得有效完成结果。
- 观察基线：master @ `2527ae0`（可空引用治理 Phase 0–5 已合入）+ **未提交工作树**（theme-studio WIP）。开工时重新记录实际 SHA 与工作树差异。
- 衔接：[重做计划](2026-10-26-theme-studio-rebuild-plan.md)、[下一阶段计划](2026-10-26-theme-studio-next-stage-plan.md)、[并行计划](2026-10-26-theme-studio-parallel-plan.md)、[优化意图计划](2026-10-26-theme-studio-optimization-intent-plan.md)、[可空引用治理计划](2026-10-26-nullable-warnings-remediation-plan.md)（已 v2.4 实施完毕）。

### 修订记录（v2 · 独立审核意见吸收）

| 编号 | 审核意见 | 吸收位置 |
|---|---|---|
| **M-1** | B-3 修法 2「`route` 取 `preview.Route`」会破坏投稿合同（`ThemeMarket.cs:245-286` 要求 `route∈{gpu\|legacy}`）并打红既有测试 `ThemeExportServiceTests.cs:383` | §5.2 B-3 整体重写：`route` 保持**主题声明路线**，一致性自检写入既有 `marketValidation.reviewRequired` 列表 |
| **M-2** | R11 事实描述不成立（`route` 永不写字面量 "fallback"），定级偏高 | §3.1 R11 改写为**单向不一致**描述；级别 P1 → **P2** |
| **M-3** | A-4 三分支规则满足不了自身验收（经典主题下两码皆 null ⇒ 必落「GPU 场景生效」） | §5.1 A-4 修法 1 重写为四步判定，并给出 `RenderingTransitionCoordinator` 单一事实源方案 |
| **M-4** | C-1 修法 1「复用 `color.*`」前提不成立（`BuildClassic()` 的 `color.*` 全部 `Scope=Classic`） | §5.3 C-1 修法 1 改为「映射到 GpuMain 实际存在的 `rendering.parameters.*` 真实键；确无字段则移除该角色」 |
| **M-5** | C-1 修法 3「命中接线」是未估算的新功能 | §5.3 C-1 拆分为 **C-1a（映射/自检/焦点，Phase C 范围）** 与 **C-1b（命中接线，单列并给出方案选项与工期风险）**；§7 风险表补条目 |
| **S-1** | G-3/G-4 缺 `-p:NuGetAudit=false`（离线假红） | §6.3 G-3/G-4 命令补参数 |
| **S-2** | 三处行号偏差（`Save` 实为 `:469`；`AutoScaleMode.None` 实为 `:113`；`LITEMONITOR_PLUGIN_REPO` 实为 ci.yml `:144`） | §2、§3、§5.1 A-1、§5.5 E-1、§6.3、附录 A 全部同步 |
| **S-3** | `:681`（owner 窗体 Size）与 `:691`（画布 Bounds）并称「固定竖向」不准确 | §3 R5、§5.2 B-2 根因拆分表述 |
| **S-4** | B-2 修法 1「`UpdateTheme` 的 shape/horizontalCols 可直接复用」不成立——导出走**构造函数** | §5.2 B-2 修法 1 改为构造函数形参，并点名现成资产 `HorizontalPreviewControl` |
| **S-5** | 并行度表述与实际同文件冲突 | §5 相位表：Phase A「A-1+A-4 合一组，共 3 组」；Phase C 明确串行 |
| **S-6** | R9 presented frames 无可观测入口 | §6.1 R9 行补「新增可观测量」与 `GpuSceneRenderer.PresentedFrameCount` |
| **S-7** | G-6「重跑 10 条观测点」在仓库内不可执行（探针未入库） | §5.0 Phase 0 纳入探针入库义务；§6.3 G-6 口径改写 |
| **S-8** | G-5 `-MustExecuteTestClass` 未给类名 | §6.3 G-5 补齐 13 个类名与全部参数 |
| **S-9** | §3.1「收敛到单一事实源」与 B-3「新增第三处 route 写入」自相矛盾 | §3.1 显式区分「route=主题声明路线（合同字段）」与「previewRoute=实际出图路线」 |
| **S-10** | `UpdateTargetFramesPerSecond` 全仓 2 个调用点（生产 + 测试） | §3 R10、§5.4 D-2 同步 |
| **S-11** | Phase 0 清单遗漏已修改的 `ThemeStudioResourceTests.cs` 与 untracked 的 GPU 主题 JSON / 4 个新测试文件 | §5.0 新增「Phase 0 基线固化」，逐项列纳入/排除 |
| **S-12** | R7 性质与 R1–R6 不同类 | §3 R7 行补「性质」说明 |
| **S-13** | §6.2 命名建议自相矛盾 | §6.2 措辞修正 |
| 自查 1 | A-4 路线规则未考虑「该主题本身是否请求 GPU」 | 已并入 M-3 的四步判定第 1 步 |
| 自查 2 | 附录 A R2 行号 `:464` | 并入 S-2 |
| 自查 3 | `.cua-helper/`（约 40MB 截图产物）应 gitignore 不入库 | §5.0 Phase 0 排除项 + 义务 |
| 自查 4 | §6.1 R2 行缺可复现触发方式 | §6.1 R2 行补 `Settings.GlobalBlockSave=true` |
| 自查 5 | Phase B 未点名 `HorizontalPreviewControl.RenderPreview` | 并入 S-4 |
| 自查 6 | D-2 未给调度器同步调用点 | 并入 S-10 |

### 修订记录（v2.1 · v2 聚焦复审意见吸收）

v2 经同一审核 agent 聚焦复审，结论仍为「**有条件通过**」——M-1…M-5 与 S-1…S-13 **全部已正确吸收、无残留**；遗留 1 必须项 + 6 次要项，已于本版全部吸收。

| 编号 | 复审意见 | 吸收位置 |
|---|---|---|
| **V2-M-1** | A-4 第 4 步写「第 1–3 步全部由协调器结论替代」会丢掉第 3 步的**降级码**（协调器只有 `ExpectedRoute`/`EffectiveRoute`，码只在 `UIController.GpuFallbackCode`/`GpuRuntimeFailure`）⇒ 与 A-4 验收「降级必带码」冲突 | §5.1 A-4 第 4 步改为「**仅替代第 1–2 步**」，并显式写明第 3 步的降级码标注必须保留、协调器不提供降级码 |
| **V2-S-1** | `src/ThemeEditor/ThemeRenderingConfig.cs` 路径不存在（真实为 `src/Rendering/Configuration/ThemeRenderingConfig.cs`）；真实键不止所列 9 个 | §5.3 C-1a 根因 1 改用正确路径与 `:116-133` 全量键位 |
| **V2-S-2** | A-4 第 1 步「实际生效主题」与第 2 步 `Settings.Load().Skin`（磁盘持久化值）口径不明，可能在保存成功但运行时应用失败时混用 | §5.1 A-4 第 1 步写明取**运行时实际应用的主题对象**；第 2 点写明是**磁盘持久化值**，两口径各自标明来源、不得互相替代 |
| **V2-S-3** | GPU 横向列宽默认 320，导出 400×120 时不铺满 | §5.2 B-2 修法 1 补「列宽必须显式传 `width=400`」；附录 A R5 行同步 |
| **V2-S-4** | 附录 A R5 行仍列 `:681`、R4 行重复 `360` | 附录 A R4 行去重；R5 行只留 `:691` 并注「owner 窗体 Size 见 `:681`」 |
| **V2-S-5** | Phase 0 验收把「2686 条 / 通过 2685 / 跳过 1」写死为期望，与 ci.yml:112/:123 的表述不一致 | §5.0 验收改为「以**本次实测**数字为准，并确保不低于基线」 |
| **V2-S-6** | `Settings.GlobalBlockSave` 是**静态**属性，用例不复位会污染同进程后续用例 | §6.1 R2 行补「必须 try/finally 复位为 `false`」 |

---

## 1. 目标与范围

把「**说谎的工作室**」修成「**说真话的工作室**」。

独立验收报告的核心指控不是「功能没做」，而是「**界面/返回值/导出物在描述一个与实际不符的世界**」：切换目标画布不动、保存失败当成功、GPU 导出用错配色、导出形态与所选不符、点选定位从未接线、暗色模式下工作区仍是浅色、隐藏后仍在烧 GPU。这比缺功能更危险——用户会基于这些假象做决策。

本计划的目标：

1. 消灭全部 10 条 R，让「界面显示的状态」「接口返回的状态」「导出物的实际内容」三者**指向同一个事实**。
2. 补上报告漏掉的下游后果 R11（投稿包元数据与实际出图路线的**单向**不一致，见 §3.1）。
3. 关闭 3 条证据缺口 G1–G3，把「已验证 / 未验证」标回真实状态，**不允许用代码逻辑论证冒充实机验证**。
4. 把这些失效场景**固化进正式回归**，让下一次回归能真正拦住它们。
5. **不改动**：既有公开 API 的语义兼容性、主题市场投稿合同的字段结构、主题模型 schema、既有可空治理成果（主程序 0 警告门禁）。

### 明确不做

| 不做 | 理由 |
|---|---|
| N10 优化计划的细化与实施 | 验收未通过前不升级阶段，先修「说谎」问题 |
| N08 实机不可得项（250%/300%/4K/混合 DPI/拔屏） | 本机无实机条件，**只能标 NOT-VERIFIED**，不得用代码逻辑判 PASS |
| 修改 `SettingsHelper.Save` 的公开静默失败语义 | 它是既有多处依赖的公开 API（`src/UI/SettingsForm.cs:181` 等），语义变更属另一个批次 |
| 引入新依赖 / 迁移 UI 框架 | 与整改目标无关 |
| 改动主题模型 schema | R7 的映射修正是「改映射表指向已存在字段」，不是「新增字段」；若确需新增字段则单独立项 |
| **改写 `submission.json` 的 `route` 字段取值** | `route` 是**合同字段**（`src/Plugins/ThemeMarket.cs:245-286` 要求 `route∈{gpu\|legacy}`，`:261-265` 非法即拒收），既有测试 `tests/LiteMonitor.Tests/ThemeExportServiceTests.cs:383` 断言经典主题 `route=="legacy"`；改它会同时违反 §1 第 5 条与 §6.2（M-1） |

---

## 2. 基线声明（开工前必须复核）

| 事实 | 值 | 复核方式 |
|---|---|---|
| 仓库 HEAD | `2527ae0` | `git rev-parse HEAD` |
| 工作树 | **19 modified / 9 deleted / 20 untracked**（逐项清单见 §5.0） | `git status --porcelain` |
| 可空治理成果 | 主程序 0 警告；门禁 1 四生产工程 `-warnaserror` EXIT=0；门禁 2 EXIT=0 | 重建后跑一次 |
| 测试基线 | `LiteMonitor.Tests` 2593 通过 / 1 跳过 / 共 2594；Updater 73；GPU 19；合计 2686（= CI 阈值 `.github/workflows/ci.yml:115`） | 先 `dotnet build LiteMonitor.sln -c Debug` 再 `dotnet test` |

**执行环境硬约束（踩坑备忘）**：

- 本地跑测试前**必须**先 `dotnet build LiteMonitor.sln -c Debug` 重建整个解决方案。只构建 `LiteMonitor.csproj` 会让 30+ 条 I2/E02 用例失败，报 `Host 产物与本轮构建清单不符，拒绝执行 I2 测试（G5 收口…）`——这不是代码回归。
- 本地需设 `$env:LITEMONITOR_PLUGIN_REPO = (Join-Path $PWD 'tests\fixtures\plugin-repo')`，否则 `MarketIndexContractTests` 的 6 条 `[PluginRepoFact]` 用例被跳过（CI 在 `.github/workflows/ci.yml:144` 设同名变量）。
- 测试必须**串行**跑；发现他人会话遗留的 `testhost` 进程时**停止自己的会话**，不终止他人进程。
- 提交信息必须写进文件后用 `git commit -F <文件>`；`git commit -m` 且消息含中文引号/换行会触发 git `Invalid path … Filename too long`。
- **构建与测试不可并发**：实测并发跑 `dotnet build` 与 `dotnet test --no-build` 会因 `bin/Debug/net8.0-windows/LiteMonitor++.dll`、`src/Contracts/bin/Debug/net8.0/LiteMonitor.Contracts.dll` 被占用而报 `error MSB3021/MSB3027/MSB3061`——这是**环境性冲突，不是产品缺陷**，验收报告的 11 条 I2 失败即此类。

---

## 3. 复核结论：报告的 10 条 R 全部成立

以下 10 条我已**逐条回源码独立验证**，行号与报告完全一致，结论：**全部成立，无夸大**。独立审核 Agent 亦逐条复核通过。

| 编号 | 级别 | 落点 | 事实 | 危害 |
|---|---|---|---|---|
| **R1** | P1 | `src/ThemeEditor/ThemeStudioForm.cs:1147` | `OnTargetChanged` 只有 `_inspector.Bind(_session, target)` + `UpdateRouteStatus()`，**没有 `RefreshPreviewNow()`**；而 `:942`/`:982` 的渲染路由完全依赖 `_inspector.Target` | 切目标后画布仍是旧目标的内容；`SelectTargetForTesting`（`:1229-1235`）同样不刷新 ⇒ 测试钩子与真实下拉行为不一致 |
| **R2** | P1 | `src/ThemeEditor/ThemeStudioLauncher.cs:43` | `_cfg.Save()` 调的是 `src/Core/SettingsHelper.cs:469` 的 `Save`，它**丢弃 bool**（`:462-468` 注释明写「失败时**静默**，不抛异常…需要知道"写没写成"的调用方请改用 `TrySave`」）；外层 try/catch 抓不到失败 | 磁盘没写成功，界面照样报 `ThemeApplied`，内存与磁盘状态分叉 |
| **R3** | P1 | `src/ThemeEditor/ThemeStudioLauncher.cs:60/67/78` | route 由 `_cfg.GpuThemeEffectsEnabled && !Program.SafeMode` **推断**；`EffectiveTheme` 传的是**请求值** `themeName`；异常分支用已在 `:42` 被改写的 `_cfg.Skin` | 「生效主题」「生效路线」都不是实际生效的东西 |
| **R4** | P1 | `src/ThemeEditor/ThemeExportService.cs:330-335/359-360/589/602` | Fallback 分支直接传 `session.Draft`（`:298`），`RenderClassicImage` 直接 `CreatePreviewTheme(theme, 1.0f)`，**从不调用派生**；而画布侧 `ThemeStudioForm.cs:957-958` 已在 GpuFallback 目标下用 `LegacyFallbackStyleResolver.BuildDerivedTheme(_session.Draft)` | 画布显示回退色、导出图却是主效果色（实测 `bgPixel=FF112233` vs 期望 `#AABBCC`） |
| **R5** | P1 | `src/ThemeEditor/ThemeExportService.cs:293/337/653/681/691/606/617` | `TryRenderGpuImage(Theme theme, string path, out string? error)`（`:653`）**无 orientation 形参**，调用处 `:337` 也不传；GPU 分支画布 `Bounds`（`:691`）**固定为竖向 240×420**（owner 窗体 `Size`（`:681`）随方向调整，不是问题点）；经典分支虽按 `orientation` 算位图尺寸（`:299-301`），但**内容布局始终是竖向**（`:606` `new UILayout(previewTheme)` + `:617` `RenderPreview(...)`） | 横向主题导出：GPU 路线得到 240×420 竖图；经典路线得到 400×120 的位图里塞竖向内容（实测 vs 期望 400×120） |
| **R6** | P1 | `src/ThemeEditor/GpuThemePreviewControl.cs:130/147` + `src/ThemeEditor/ThemePreviewControl.cs:237/240-241` | `UpdateTheme` 校验失败 `return false` 且**不写 `_error`**（`:136-139`/`:141-144`/`:147-150` 三处）；父控件丢弃 bool，`:240-241` 照样 `Visible=true; BringToFront()` | 非法主题被拒后，界面仍把**旧帧**当作新主题展示，且 `GpuPreviewError` 为 null |
| **R7** | P1 | `src/ThemeEditor/ThemeStudioForm.cs:1100`、`src/ThemeEditor/ThemePropertyCatalog.cs:343-344`、`src/ThemeEditor/PropertyInspector.cs:79-88` | `SelectPropertyByRole` 全仓仅 2 个调用点，都在测试里；`src/ThemeEditor/*.cs` 中 `MouseDown/MouseClick/GetChildAtPoint/PointToClient` **零命中**；GPU 主效果映射指向 `rendering.parameters.panel_color`——该路径全仓**只在 ThemePropertyCatalog 出现**（`:343`/`:344`/`:349`），模型与检查器均无此字段；`FocusProperty` 落到 `rows[0]`（行内 Label）→ `targetRow.Focus()` | 「点选定位」功能不存在；映射表指向不存在的字段，聚焦落在不可编辑的 Label 上 |
| **R8** | P2 | `src/ThemeEditor/ThemeStudioForm.cs:31-32` | 硬编码浅色常量（`CanvasBack = 0xFA,0xFA,0xF8`、`PreviewBack = 0xFF,0xFF,0xFF`）；文件内 `IsDark`/`Palette` **零命中** | 暗色模式下工作区仍浅色（实测 `actualBack=FFFAFAF8`、`actualInspectorBack=FFFFFFFF`） |
| **R9** | P2 | `src/ThemeEditor/GpuThemePreviewControl.cs:403/405` | `OnTick` 只判 `_renderer is null \|\| _scene is null \|\| !_scheduler.TryTakeFrame(...)`，**无 Visible/最小化检查**；父控件切经典只置 `Visible=false`（`ThemePreviewControl.cs:245-248`） | 隐藏后仍在渲染（实测 250 ms 内 presented 帧 +6） |
| **R10** | P2 | `src/ThemeEditor/GpuThemePreviewControl.cs:110/166` | `_scheduler` 在构造时定死 FPS；`UpdateTheme` 重建 `_runtimeSettings` 却不同步调度器。`src/Rendering/Gpu/Effects/FrameScheduling.cs:37` 的 `UpdateTargetFramesPerSecond(int)` 全仓**2 个**调用点——生产 `GpuPresentationHostController.cs:632`、测试 `tests/LiteMonitor.Tests/MaterialSchedulingTests.cs:65`——**预览路径 0 调用** | 改目标 FPS 无效（实测 requested=2 / actual=30） |

> **R7 的性质说明（与 R1–R6 不同类）**：R7 本质是「**功能从未接线** + **映射表指向虚构路径**」，而不是「状态说谎」。它与 R1–R6 同为 P1 成立，但 Phase C 的处置形态不同——R1–R6 是「把已有但错误的行为改对」，R7 是「先补数据与接线，再谈功能」。执行时不得按小修对待。

### 3.1 我补充的一条：R11（报告未点名，但已由 R4/R5 传导）——级别 P2

**事实（v2 改写，按审核意见订正）**：`src/ThemeEditor/ThemeExportService.cs:450` `PrepareSubmissionMaterials` 用 `ExportEffectImage(session, previewPath, ImageRoutePreference.Auto)` 生成**主题市场投稿包**的效果图；`:465` 写入 `["route"] = gpuTheme ? ThemeRenderingConfig.GpuBackend : ThemeRenderingConfig.LegacyBackend` —— 这是**主题声明路线**；而 `:471-473` 的 `previewRoute`/`previewIsFallback` 是**实际出图路线**。

**`route` 永远不会写字面量 `"fallback"`**（取值只有 `gpuTheme ? GpuBackend : LegacyBackend` 两支），因此「元数据写 fallback、图却是主效果配色」这一形态**不可能出现**。真实存在的不一致**只有单向一种**：

- **GPU 主题 + 强制经典偏好**（`:331-335`）**或 GPU 出图失败回退**（`:359-360`）⇒ 实际路线为 `Route.Fallback`、图用主效果配色，而 `route` 仍写 `"gpu"`。
- 经典主题在 `ExportEffectImage` 只走经典路线（`:309-328`）⇒ 不会出现反向不一致。

**性质**：这是「**已披露**的不一致」（`previewRoute`/`previewIsFallback` 如实记录了实际路线），不是「自相矛盾」。因此定级由 P1 **降为 P2**——但它仍会产出**误导性的产品图**（图与主题声明路线不符），故仍需修。

**R11 与 R4/R5 同源**：只要「派生规则」和「路线判定」有两份实现，就必然漂移。修复的正确形态**不是新增第三处判断**，而是**收敛到单一事实源**（见 §4 铁律 T3）。

> **单一事实源的边界（S-9 澄清）**：本计划明确区分两个不同语义的量，**不合并**：
> - `submission.json` 的 `route` = **主题声明路线**（合同字段，取值域 `gpu|legacy`，由主题自身 `rendering.backend` 决定）；
> - `previewRoute` / `previewIsFallback` = **实际出图路线**（由导出执行结果决定）。
>
> 单一事实源指的是「**实际出图路线**只有一个决定点」（`ExportEffectImage` 的返回值），以及「**派生规则**只有一份实现」（`LegacyFallbackStyleResolver.BuildDerivedTheme`）。**不允许**新增第三处独立推断 `route` 的代码。

### 3.2 三条证据缺口 G1–G3（可整改的部分）

| 编号 | 缺口 | 可整改动作 |
|---|---|---|
| **G1** | N08 DPI 矩阵中 M-03/M-04/M-05/M-06/M-12/M-13 标 PASS，但证据说明全是「软件 DPI 缩放换算逻辑」「控件基准坐标使用 Dip 缩放适配函数计算」「逻辑布局换算通过」；M-07/M-08/M-09/M-10/M-11 本身是 NOT-VERIFIED；表内无暗色组合行 | ① 把 DPI 变化后的**重布局接线**补上（`ThemeStudioForm.cs:18/113` 明确 `AutoScaleMode.None`，几何在 BuildUi 时算死）；② 按 N08 矩阵自己的硬原则（「坚决杜绝把不可得条件合并宣称已通过」）把上述 6 行**降级为 NOT-VERIFIED（代码逻辑已核对，实机待验证）**；③ 补一行暗色组合行（与 R8 联动） |
| **G2** | `tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs:352` 场景 4 打开 `ClassicThemeName`（**无 GPU rendering 块**）再调 `form.SelectTargetForTesting(target)`（不刷新画布，见 R1）⇒ 从未发生主效果↔回退的**渲染**往返；且断言只看净增上限（句柄 ≤400 / GDI ≤200 / USER ≤200），**不验证斜率** ⇒ 实测句柄 352→552（净增 +200 = 200 次切换 × +1/次）这类线性累积在该阈值下仍然合规 | ① 场景 4 改用**真实 GPU 主题**（`tests/LiteMonitor.Tests/FlagshipGpuThemeTests.cs` 有现成样本）并走**真实目标事件**（真实下拉事件，非测试钩子单独调用）；② 断言增加**斜率校验**（首 20 次与末 20 次的句柄增量之差），使「1:1 线性累积」与「稳定平台」可区分 |
| **G3** | N09 报告的「全量回归基线」写「**2686+**（主工程 2594 + Updater 73 + GPU 19 + ThemeStudio 新增 10+ 测试）」——非具体数字，不可追溯；`main-full.trx` 1322 执行 / 1311 通过 / **11 失败**（11 条全是 I2「无法从测试输出目录 … 推导主工程输出目录（清单锚点）…请改用常规 dotnet test / dotnet build 流程」的环境性失败），`full_acceptance.accepted=false` | 按 §2 的环境约束**串行重跑一次完整全量**，记录**具体**的执行/通过/失败/跳过数与产物摘要（artifact sha256），替换「2686+」；环境性失败不得计入产品缺陷，但必须与产品缺陷分开记账 |

---

## 4. 整改铁律

继承[可空引用治理计划](2026-10-26-nullable-warnings-remediation-plan.md) §4.3 的**七条铁律**（含铁律五「禁 `?.` 静默降级」、铁律七「条件访问污染 null-state」），并新增本批专用四条：

| 编号 | 铁律 | 反例（本次真实踩到） |
|---|---|---|
| **T1** | **禁止用状态文案冒充状态**：任何显示「已保存 / 已生效 / GPU 活跃 / 已切换」的界面或返回值，必须有**真实状态源**支撑；禁止由全局开关、请求参数、默认值推断。 | R3 用 `_cfg.GpuThemeEffectsEnabled` 推断路线 |
| **T2** | **禁止保留误导性成功外观**：失败后不得继续展示旧内容并声明成功；失败必须有可见的错误面。 | R6 拒绝新主题后仍 `BringToFront()` 旧帧 |
| **T3** | **单一事实源**：渲染目标、导出派生、投稿元数据必须复用**同一套规则函数**；禁止第二份实现，禁止「这里补一个 if 那里补一个 else」。**注意**：合同字段 `route`（主题声明路线）与 `previewRoute`（实际出图路线）语义不同，不得合并，也不得为 `route` 新增第三处推断。 | R4/R5/R11 三处各写各的 |
| **T4** | **降级优于虚报**：无法实机验证的项一律标 NOT-VERIFIED；不得以「代码逻辑推导」判 PASS。 | G1 的 M-03/M-04/M-05/M-06/M-12/M-13 |

---

## 5. 分批路线

| 批次 | 范围 | 消除 | 主要落点 | 并行性 |
|---|---|---|---|---|
| **Phase 0** | 基线固化 | —— | 工作树 WIP + 验收报告 + 证据 + 探针脚本入库；`.cua-helper/` 入 gitignore | 串行（先于一切） |
| **Phase A** | 状态可信 | R2、R1、R6、R3 | `ThemeStudioLauncher.cs`（A-1+A-4）、`ThemeStudioForm.cs`（A-2）、`ThemePreviewControl.cs` + `GpuThemePreviewControl.cs`（A-3） | **3 组可并行**：{A-1 + A-4 同改 `ThemeStudioLauncher.cs` ⇒ 合一组}、{A-2 `ThemeStudioForm.cs`}、{A-3 `ThemePreviewControl.cs` + `GpuThemePreviewControl.cs`} |
| **Phase B** | 导出正确性 | R4、R5、R11 | `ThemeExportService.cs`（同文件，**必须串行**） | 串行 |
| **Phase C** | 交互与外观 | R7（映射/自检/焦点 = C-1a）、R8 | `ThemePropertyCatalog.cs` + `PropertyInspector.cs`；`ThemeStudioForm.cs` | **串行**（R7 与 R8 同改 `ThemeStudioForm.cs`；R7 的映射部分与 R8 可在不同时段做，但不得并行 agent 同时改同一文件） |
| **Phase D** | 资源与调度 | R9、R10 | `GpuThemePreviewControl.cs`（同文件，串行） | 串行，且必须在 Phase A 的 R6 之后 |
| **Phase E** | 证据与门禁 | G1、G2、G3 | `ThemeStudioForm.cs`（DPI）、`tests/.../ThemeStudioResourceTests.cs`、`docs/verification/theme-studio/*` | 依赖 A–D |
| **Phase F**（可选单独立项） | R7 命中接线 | R7 的交互部分 | `ThemeStudioForm.cs` + `GpuThemePreviewControl.cs` + `ThemePreviewControl.cs` | 见 §5.3 C-1b 的工期风险 |

顺序与独立验收报告 §15 的建议顺序一致（R1–R3 → R4–R6 → R7/R8 + R9/R10 → 固化回归 → 重记 N08/N09），额外把 R6 从 Phase B 提到 Phase A（它与 R1 同属「说谎」，且是 R9/R10 的前置），并把 G1–G3 单列为 Phase E。

### 5.0 Phase 0 —— 基线固化（先于一切）

theme-studio WIP 尚未提交，是本批的隐含基线。**必须先把它与验收证据单独固化为基线提交**，否则后续批次无法区分「本批改动」与「前序 WIP」。

**纳入基线提交（逐项确认，全部来自实测 `git status --porcelain`）**：

| 类别 | 清单 | 理由 |
|---|---|---|
| 修改（19 项） | `.github/workflows/ci.yml`、`.gitignore`、`docs/design/theme-studio/implementation-log.md`、`docs/development-windows-firewall.md`、`docs/plans/2026-10-26-nullable-warnings-remediation-plan.md`、`docs/plans/2026-10-26-theme-studio-parallel-plan.md`、`docs/plans/2026-10-26-theme-studio-rebuild-plan.md`、`docs/verification/acceptance/F00-baseline.md` | 已验证的文档与门禁现状 |
| 修改（10 个 src） | `src/Rendering/LegacyFallbackStyleResolver.cs`、`src/ThemeEditor/{GpuThemePreviewControl,PropertyInspector,ThemePreviewControl,ThemePreviewViews,ThemePropertyCatalog,ThemeStudioForm,ThemeStudioLauncher}.cs`、`src/UI/{MenuManager,TaskbarRenderer}.cs` | theme-studio WIP 主体 |
| 修改（1 个测试） | `tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs` | **E-2 会直接改它**，若不入库则与既有未提交改动混在一起无法区分 |
| 新增（4 个测试） | `tests/LiteMonitor.Tests/{FlagshipGpuThemeTests,ThemeStudioGpuSessionAndFallbackTests,ThemeStudioNextStageWorkflowTests,ThemeStudioTaskbarIsolationTests}.cs` | **E-2 依赖 `FlagshipGpuThemeTests`**；缺它则新克隆的 CI 无 GPU 主题夹具 |
| 新增（3 个主题夹具） | `resources/themes/{CosmicAurora_Nebula,NixieTube_Brass,RetroCRT_Phosphor}_GPU.json` | 同上，GPU 主题夹具 |
| 新增（证据） | `docs/reviews/2026-10-26-theme-studio-next-stage-acceptance-review.md`、`docs/verification/theme-studio/{N00-baseline-freeze,N08-matrix,N09-perf-report,flagship-gpu-themes-verification}.md`、`docs/verification/theme-studio/independent-review/` | 验收证据 |
| 新增（文档） | `CHANGELOG.md`、`docs/README.md`、`docs/plans/2026-10-26-theme-studio-{acceptance-remediation,next-stage,optimization-intent}-plan.md`、`docs/plans/2026-10-26-three-flagship-gpu-themes-plan.md` | 本批计划与既有计划 |

**排除（不入库）**：

| 排除项 | 处置 |
|---|---|
| `.cua-helper/`（约 40MB 截图与量测产物） | **加入 `.gitignore`**（现 `.gitignore` 改动已含 `/docs/archive/`），绝不提交 |
| 9 个 `docs/*.md` 删除（`docs/HARDWARE-ROBUSTNESS-RESEARCH-2026-10-20.md`、`docs/plans/2026-10-23-updater-legacy-bridge-design.md`、`docs/plans/2026-10-24-architecture-debt-{plan,requirements}.md`、`docs/plans/2026-10-25-verified-bugs-remediation-subagent-plan.md`、`docs/UPSTREAM-{FEEDBACK-2026-10-20,ISSUE-VERIFICATION-TASKS-2026-10-25,ISSUES-2026-10-25,OPEN-BUGS-PERFORMANCE-2026-10-25}.md`） | **不并入基线提交**——它们是另一条文档线的工作，与本批无关；保持未提交状态，避免污染本批 diff |
| **验收探针脚本** | ⚠️ `docs/verification/theme-studio/independent-review/` 当前只有 `observations.json`、`source-manifest.json`、`test-summary.json` 与 2 张 PNG，**没有探针脚本**（脚本仅存在于未入库的 `.tmp/acceptance-theme-studio-20261026`）。**Phase 0 必须把探针入库**为 `tools/ThemeStudioAuditProbe/`（C# 控制台探针工程：`<OutputType>Exe</OutputType>`、net8.0-windows、UseWindowsForms、`<AssemblyName>ThemeStudioAuditProbe</AssemblyName>`、`ProjectReference` → `LiteMonitor.csproj`；配套在 `LiteMonitor.csproj` 补 `<InternalsVisibleTo Include="ThemeStudioAuditProbe" />`，与既有 `AppearanceWindowShapeProbe`/`GpuHostLifecycleProbe` 同款；**不加入 sln**，避免与 `LiteMonitor.Tests` 程序集名冲突），否则 G-6「重跑观测点」在仓库内不可执行（S-7） |

**验收**：基线提交后 `git status --porcelain` 仅剩 9 个 docs 删除；`dotnet build LiteMonitor.csproj --no-incremental` 仍 0 警告 0 错误；`dotnet test` 的**实测**用例数/通过数/跳过数与基线一致（V2-S-5：不把「2686 条 / 通过 2685 / 跳过 1」写死为门禁期望——`.github/workflows/ci.yml:112` 注释写的是「主工程 2594（2587 执行全通 + 7 跳过）」，`:123` 才要求「接入夹具后必须 1 条跳过」；以**本次实测数字**记录，并确保不低于基线）。

### 5.1 Phase A —— 状态可信

#### A-1 R2：持久化结果必须被消费（P1）

- **落点**：`src/ThemeEditor/ThemeStudioLauncher.cs:43`。
- **根因**：`SettingsHelper.Save`（`src/Core/SettingsHelper.cs:469`）**故意静默**（丢弃 `TrySave` 的 bool），而网关在 try/catch 里调它 ⇒ 失败既不抛也不返回。
- **修法**：
  1. 改调 `src/Core/SettingsHelper.cs:248` 的 `internal static bool TrySave(this Settings settings)`（同程序集可直接调用；`:265` 在 `GlobalBlockSave` 为真时返回 false，`:255` 捕获异常后返回 false）。
  2. 记录持久化前的 `string previousSkin = _cfg.Skin;`，`if (!_cfg.TrySave())` 时：把 `_cfg.Skin` 回滚为 `previousSkin`，立即返回 `ThemeApplyResult(Success: false, Stage: ThemeApplyStage.SettingsPersisted, Error: "配置持久化失败（可能被安全策略或磁盘写入阻止）。")`，**不得进入 `ThemeApplied` 阶段**。
  3. **禁止**修改 `Save` 的公开静默语义。
- **验收**：置 `Settings.GlobalBlockSave = true`（`src/Core/Settings.cs:422-425`，public 可写）后走网关 → 返回 `Success=false, Stage=SettingsPersisted`；磁盘 `Skin` 与内存 `Skin` 都保持旧值；**不出现** `ThemeApplied`。

#### A-2 R1：目标切换必须刷新画布（P1）

- **落点**：`src/ThemeEditor/ThemeStudioForm.cs:1147` `OnTargetChanged`、`:1229-1235` `SelectTargetForTesting`。
- **根因**：`RefreshPreviewNow()`（`:924` 起）是唯一把 `_inspector.Target` 翻译成画布内容的入口（`:942` GPU 主效果、`:957-958` 回退派生、`:976` 任务栏、`:982` 竖向预览），而目标切换只调了 `Bind` + `UpdateRouteStatus`。
- **修法**：
  1. `OnTargetChanged` 在 `_inspector.Bind(_session, target)` 之后调用 `RefreshPreviewNow()`（目标切换是低频离散操作，**立即刷新**，不做防抖）。
  2. `SelectTargetForTesting` 改为**复用同一入口**（调用与 `OnTargetChanged` 等价的方法），保证测试钩子与真实下拉行为一致——这是 R7 与 G2 能修好的前提。
  3. 画布侧提供可观测量（当前渲染目标 / 当前渲染主题代次），供测试与 UI 断言；代次标识可复用 `GpuThemePreviewControl.CurrentGeneration`（`:92`）。
- **验收**：真实下拉切到「兼容外观」→ 画布渲染目标变为 `GpuFallback` 且背景为 `fallbackStyle.background`（`#AABBCC`），不是 `#112233`。

#### A-3 R6：GPU 更新失败不得保留「成功」外观（P1）

- **落点**：`src/ThemeEditor/GpuThemePreviewControl.cs:130/147`、`src/ThemeEditor/ThemePreviewControl.cs:237/240-241`。
- **根因**：`UpdateTheme` 校验失败 `return false` 且不写 `_error`；父控件丢弃 bool 并照常显示。
- **修法**：
  1. `UpdateTheme` 校验失败时写 `_error`（如「主题校验未通过：…」）并**保持旧 renderer 不动**（不伪造新帧）；成功时清空 `_error`。
  2. `ThemePreviewControl` 消费 bool：失败时不得把旧子控件当作新主题展示——将 `_gpuPreview.Visible = false`（或保留可见但 `IsGpuPreviewActive` 明确为 false 且 `GpuPreviewError` 非空），**严禁** `BringToFront()` 旧内容冒充新主题。
  3. 父控件暴露「当前渲染的是哪个主题」的**代次标识**（`renderedThemeToken`），供 UI 区分旧帧与新主题。
- **验收**：合法 → 非法 → 修复 三步会话：非法步 `GpuPreviewError` 非空；`IsGpuPreviewActive` 不得被用来表示「新主题已渲染」。

#### A-4 R3：生效结果必须来自实际状态（P1）

- **落点**：`src/ThemeEditor/ThemeStudioLauncher.cs:60/67/68/78`。
- **根因**：路线由开关推断、生效主题用请求值、异常分支用已被改写的 `_cfg.Skin`。
- **修法**：
  1. **路线来源（重写，M-3）**：`AppActions.ApplyThemeAndLayout`（`src/Core/Actions/AppActions.cs:219`）返回 `void`，内部只有 `ui?.ApplyTheme(cfg.Skin, retainData)`（`:233`），**不产出路线信息** ⇒ 路线必须从 `UIController` 读。**判定按下列四步顺序执行**（v1 的三分支规则满足不了自身验收，已废止）：
     - **第 1 步 · 主题是否请求 GPU**：取**运行时实际应用的主题对象**（即 `ApplyThemeAndLayout` 实际使用的 `cfg.Skin` 主题；与第 2 点的**磁盘持久化值**是两个口径——保存成功但运行时应用失败时二者会不同，**不得混用**）读 `theme.Rendering?.IsGpuRequested == true`（全仓既有写法：`src/ThemeEditor/ThemeEditorForm.cs:53`、`src/ThemeEditor/ThemeExportService.cs:568`、`src/ThemeEditor/ThemePropertyCatalog.Gpu.cs:41`；属性定义在 `src/Rendering/Configuration/ThemeRenderingConfig.cs:58`）。**不请求 GPU ⇒ 路线直接判为「经典渲染」，与全局开关无关**（这正是 v1 规则的漏洞：经典主题 + GPU 开关开启时两码皆 null，必落「GPU 场景生效」分支，与验收矛盾）。
     - **第 2 步 · 运行时是否真的在跑 GPU**：`ui.IsGpuPresentationActive`（`src/UI/UIController.cs:124`）为真 ⇒ 主效果路线；为假 ⇒ 经典渲染。
     - **第 3 步 · 降级标注**：`ui.GpuFallbackCode`（`:127`）/ `ui.GpuRuntimeFailure`（`:130`）非空 ⇒ 路线文案带降级码（如「回退绘制（<code>）」）。**降级码只用于标注，不得用于推断路线**（铁律 T1）。
     - **第 4 步 · 单一事实源（优选）**：改为暴露 `RenderingTransitionCoordinator` 的 `ExpectedRoute`（`src/UI/RenderingTransitionCoordinator.cs:149`，用户选择）与 `EffectiveRoute`（`:152`，当前实际可呈现的后端；`PublishingTransition` 的 `PublishStable`/`PublishLegacyFallback` 在 `:542`/`:552`）。该协调器目前是 `UIController` 的 private 字段（`src/UI/UIController.cs:73`）⇒ 需在 `UIController` 上新增 `internal RenderingRoute CurrentExpectedRoute / CurrentEffectiveRoute` 只读访问器。**若采用本步，则仅替代第 1–2 步**（`RenderingRoute` 枚举 = `Legacy | GpuFixedInput | GpuLiveBackground`，见 `src/Rendering/Configuration/RenderingRoutePolicy.cs:5-10`）——**第 3 步的降级码标注必须保留**：协调器只提供 `ExpectedRoute`/`EffectiveRoute`，**不提供降级码**，码源仍是 `UIController.GpuFallbackCode`（`:127`）/ `GpuRuntimeFailure`（`:130`）（V2-M-1）。
     - `ui` 不可用 → `EffectiveRoute = "未应用（无运行时宿主）"`，`Success = false`。
  2. **生效主题（磁盘口径）**：读**实际生效的持久化值** —— `Settings.Load().Skin`（`src/Plugins/ThemeMarket.cs:192` 同款取法：`string currentSkin = Settings.Load().Skin ?? "";`），不用请求值 `themeName`。需要主题渲染配置时用 `src/Core/ThemeManager.cs:531` 的 `public static Theme Load(string name)`（文件不存在抛 `FileNotFoundException`，解析失败抛 `Exception("Theme parse failed.")`，调用方需处理）。**口径提示**：此值是**磁盘持久化值**，与第 1 步的**运行时主题对象**可能不同（运行时应用失败时）——两处必须各自标明来源，不得互相替代。
  3. **异常分支**：`EffectiveTheme` 用**回滚后真正仍生效**的主题。
- **验收**：经典主题 + GPU 开关开启 → 路线**不得**报 GPU 生效（应报经典渲染）；GPU 初始化失败降级 → 路线必须带降级码；GPU 主题 + GPU 正常运行 → 报主效果路线。

### 5.2 Phase B —— 导出正确性

#### B-1 R4：兼容导出必须复用画布的派生规则（P1）

- **落点**：`src/ThemeEditor/ThemeExportService.cs:330-335/359-360/589/602`。
- **根因**：画布侧已在 GpuFallback 目标下调用 `LegacyFallbackStyleResolver.BuildDerivedTheme(_session.Draft)`（`src/Rendering/LegacyFallbackStyleResolver.cs:229`），导出侧没有复用。
- **修法**：导出侧**调用同一个函数**（`BuildDerivedTheme(Theme source)` 或 `BuildDerivedTheme(Theme source, LegacyFallbackStyle style)`，`:241`）。禁止在 `ThemeExportService` 内写第二份派生规则。
  - 具体落点：`:334`（preference=Classic 的 GPU 主题分支）与 `:360`（GPU 不可用回退分支）都应传 `BuildDerivedTheme(theme)` 而非原始 `theme`；`:327` 的经典主题分支保持传原始 theme（正确）。
- **验收**：`fallbackStyle.background = #AABBCC` 时，导出图背景采样必须为 `#AABBCC`（当前为 `#112233`）。

#### B-2 R5：导出形态必须与所选一致（P1）

- **落点**：`src/ThemeEditor/ThemeExportService.cs:293/337/653/681/691/606/617`、`src/ThemeEditor/ThemeStudioForm.cs:808-810`。
- **根因**：`TryRenderGpuImage`（`:653`）无 orientation 形参，调用处（`:337`）也不传 ⇒ GPU 分支的画布 `Bounds`（`:691`）固定为竖向 240×420；经典分支虽按 `orientation` 算位图尺寸（`:299-301`），但**内容布局始终是竖向**（`:606` `new UILayout(previewTheme)` + `:617` `RenderPreview(...)`）。`src/UI/UILayout.cs:29` 的 `UILayout` 是**纯竖向**布局引擎（无横向模式）。
- **修法**：
  1. `TryRenderGpuImage` 增加 orientation 形参，按方向设置画布 `Bounds`（横向 400×120 / 竖向 240×420）。
     - ⚠️ **入口更正（S-4）**：导出路径走的是**构造函数** `new GpuThemePreviewControl(theme, CreateMockGroups(), guard)`（`:686-689`）后 `owner.CreateControl()` / 取 Handle（`:695-700`），**从不调用 `UpdateTheme`** ⇒ 方向与横向列必须经**构造函数形参**传入：`src/ThemeEditor/GpuThemePreviewControl.cs:95-100` `public GpuThemePreviewControl(Theme theme, List<GroupLayoutInfo> groups, GpuStartupGuard? startupGuard = null, PreviewShape shape = PreviewShape.Vertical, IReadOnlyList<Column>? horizontalCols = null)`。
     - 横向列来源：`HorizontalPreviewMock.BuildColumns`（`src/ThemeEditor/GpuThemePreviewControl.cs:27/31/47`，构造内部 `:348-349` 亦有兜底调用）。⚠️ **列宽必须显式传 `width=400`**（V2-S-3）：`BuildColumns` 默认 `width=320`（`:35/:47`），控件内部兜底也是 `Width > 0 ? Width : 320`（`:348-352`）⇒ 若按 320 排布，导出的 400×120 位图**不会铺满**，验收「400×120 且横向布局」会落空。
  2. 经典分支：横向必须走**横向布局**，**明确复用现成资产** `src/ThemeEditor/ThemePreviewViews.cs:112` `public class HorizontalPreviewControl : Panel`（`:188` `SetTheme(Theme)`、`:225` `RenderPreview(Graphics)` → `:234` `HorizontalRenderer.Render(g, theme, _columns, layout.PanelWidth, appearance: null)`，与生产同源；`:253-256` `new HorizontalLayout(theme, InitialWidth, LayoutMode.Horizontal, settings, null)`；`:267` `InitialWidth`；`:269-281` `GetDpiScale()` 无句柄时 catch 退回 1.0f ⇒ 可在无句柄环境导出）。**不得**把竖向 `UILayout` 塞进 400×120 位图。
  3. `ThemeStudioForm.cs:808-810` 的形态映射只认 Horizontal/Vertical，**Taskbar 会落到 Vertical** ⇒ 补 Taskbar 分支，或对不支持的组合**显式报错**（禁止静默换方向）。
- **验收**：GPU + Horizontal → 实际 PNG 尺寸 400×120；经典 + Horizontal → 400×120 且**内容为横向布局**（非竖向压扁）；Taskbar 导出保持所选形态，不被静默改成竖向。

#### B-3 R11：投稿包的路线与图像内容必须一致（P2，本计划补充；v2 已按 M-1/M-2 重写）

- **落点**：`src/ThemeEditor/ThemeExportService.cs:450/465/471-473/478/538`。
- **根因（单向）**：`submission.json` 的 `route`（`:465`）是**主题声明路线**；`previewRoute`/`previewIsFallback`（`:471-473`）是**实际出图路线**。GPU 主题在强制经典（`:331-335`）或出图失败回退（`:359-360`）时，实际路线为 Fallback 而 `route` 仍写 `"gpu"` ⇒ 图与主题声明路线不符。
- **修法（M-1 采纳的方案）**：
  1. B-1 修好后，投稿包预览图的**配色**自然等于其路线的派生色。
  2. **`route` 保持为主题声明路线**（`gpuTheme ? GpuBackend : LegacyBackend`），**不改取值域、不改字段结构**——它是合同字段（`src/Plugins/ThemeMarket.cs:245-286` 要求 `route∈{gpu|legacy}`，`:261-265` 非法即拒收），既有测试 `tests/LiteMonitor.Tests/ThemeExportServiceTests.cs:383` 断言经典主题 `route=="legacy"`。
  3. **一致性自检写入既有字段**：把「实际出图路线 ≠ 主题声明路线」这一事实追加进**已有的** `marketValidation.reviewRequired` 列表（`ThemeExportService.cs:146` `public IReadOnlyList<string> ReviewRequired { get; init; } = Array.Empty<string>();`；`:382` `List<string> reviewRequired = new();`；`:478` `["reviewRequired"] = reviewRequired`；`:538` `ReviewRequired = reviewRequired`）。既有条目形如 `:444`「远程引用需人工确认…」与 `:454`「效果图未产出：…」⇒ 本项按同一格式追加，**不新增合同字段**。
  4. **禁止**为 `route` 新增第三处推断代码（铁律 T3）；实际出图路线的唯一决定点是 `ExportEffectImage` 的返回值。
- **验收**：构造一个走回退的 GPU 主题 → `submission.json` 的 `route` 仍为 `"gpu"`（合同合法），`previewRoute` 为实际回退路线，且 `marketValidation.reviewRequired` **含一条**说明「实际出图路线与主题声明路线不一致」；既有 `ThemeExportServiceTests.cs:383` 仍通过。

### 5.3 Phase C —— 交互与外观

#### C-1a R7（映射 + 自检 + 焦点）：让「可编辑属性映射」指向真字段（P1）

- **落点**：`src/ThemeEditor/ThemePropertyCatalog.cs:343-344`、`src/ThemeEditor/PropertyInspector.cs:79-88`、`src/ThemeEditor/ThemeStudioForm.cs:1100`。
- **根因**：
  1. **映射指向虚构路径**：`ResolvePropertyPath(role, target)`（`ThemePropertyCatalog.cs:327-362`，枚举 `SemanticElementRole` 定义在 `:316-324`）共 18 个映射，其中**仅 2 个虚构**——GpuMain 的 `rendering.parameters.panel_color`（`:343`）与 `rendering.parameters.card_color`（`:344`）。`rendering.parameters` 实为 `Dictionary<string, JsonElement>`，**真实键清单见 `src/Rendering/Configuration/ThemeRenderingConfig.cs:116-133`**（校验文案在同文件 `:206`/`:249`；命名示例：`glassStyle / liveBackground / cornerRadius / refraction / primaryCut / outlineWidth / panelOpacity / glow / surfaceOpacity` 等，此外还有 `thickness / blurRadius / secondaryCut / ambientRatio / clearCenterRatio / highlight / counterHighlight / shadow / shadowRadius / fogStrength / tintStrength`）；另见 `src/ThemeEditor/ThemePropertyCatalog.Gpu.cs:188`。
  2. **前提更正（M-4）**：v1 写的「复用 `color.*` 家族」**不成立**——`color.*` 全部出自 `BuildClassic()`（`ThemePropertyCatalog.cs:120-176`），其 `Scope = ThemePropertyScope.Classic`（`:186`）非 Shared ⇒ **GPU 两个目标下没有任何 `color.*` 描述符**。
  3. **焦点落错**：`FocusProperty`（`PropertyInspector.cs:79-88`）聚焦 `rows[0]`（行内 Label）⇒ 焦点落在不可编辑的标签上。
- **修法**：
  1. **映射修正**：为 GpuMain 角色定义**真实存在**的可编辑属性——只能取自 GpuMain 的实际 descriptor 族：`rendering.parameters.*`（真实键见上）/ `rendering.motion.*`（`ThemePropertyCatalog.Gpu.cs:228`）/ shared 的 `font.*` / `layout.*` / `thresholds.*`。若某角色确实没有可编辑字段，则**从映射表中移除该角色**，而不是保留一个指向虚构路径的条目（属产品范围决策，须在 §8 执行记录中标注）。
  2. **自检测试**：遍历「全部 `SemanticElementRole` × 3 个编辑目标」，断言每个可映射角色在 `ThemePropertyCatalog` 的 descriptor 注册表中都能解析到**真实字段**；解析不到 ⇒ 测试失败（这会直接抓住 `rendering.parameters.panel_color` / `card_color`）。
  3. **焦点修正**：`PropertyInspector.FocusProperty` 聚焦该行的**可编辑控件**（输入框/下拉），而非 `rows[0]` 标签；同时保留键盘等价路径（无障碍）。
- **验收**：自检测试在「全部角色 × 3 目标」上通过；从检查器侧点选一个角色 → `ActiveControl` 是 `TextBox`/`ComboBox`，**不是** `Label`；属性路径可解析到 `Theme` 模型的真实节点。

#### C-1b R7（命中接线）：点选定位交互（**未估算的新功能，单列并可独立立项**）

- **落点**：`src/ThemeEditor/ThemeStudioForm.cs:1100` `SelectPropertyByRole`。
- **根因（M-5）**：`src/ThemeEditor/*.cs` 中 `MouseDown/MouseClick/GetChildAtPoint/PointToClient` **零命中**；全仓唯一 HitTest 在 `src/UI/Controls/TrendChartControl.cs:94/454`，与主题无关 ⇒ **命中流程从未编写**，GPU 侧需要「像素→语义角色」的拾取能力，经典侧需要控件命中映射。
- **方案选项（须先选型再排期）**：
  - **方案甲 · 布局反查（推荐）**：不读像素，而是由渲染布局反推「(x, y) → 语义角色」映射表。GPU 侧在构建场景时同步产出每个可拾取矩形的角色表，命中时做矩形包含判定。优点：不依赖 D3D 读回、确定性可测；缺点：需与布局共用一份角色定义。
  - **方案乙 · D3D 拾取**：用 GPU 拾取缓冲/ID buffer 反查角色。优点：与所见一致；缺点：需要新的 GPU 管线能力，成本与风险显著更高。
  - **经典侧**：无论选哪个方案，都需在 `ThemePreviewControl` / `HorizontalPreviewControl` 侧建立控件→角色的命中映射。
- **范围与风险**：本项是**新增功能**，v1 计划未估算工期，且 §7 风险表未列。**处置**：Phase C 的 R7 范围**只含 C-1a**（映射修正 + 自检 + 焦点修正）；**C-1b 默认不纳入本批执行**，除非用户在 Phase C 开始前明确批准并接受其工期。批准后须作为 **Phase F** 单独立项，并在 §8 记录选型结论。
- **验收（若批准执行）**：从画布真实点击 → 检查器定位到正确属性，且 `ActiveControl` 是可编辑控件（`TextBox`/`ComboBox`），**不是** `Label`。

#### C-2 R8：工作区必须跟随明暗策略（P2）

- **落点**：`src/ThemeEditor/ThemeStudioForm.cs:31-32`。
- **根因**：硬编码浅色常量，文件内 `IsDark`/`Palette` 零命中。
- **修法**：复用**既有**明暗引擎，不另造一套：
  - `src/UI/Settings/SettingsThemeManager.cs:79` `IsDarkNow`、`:82` `CurrentPalette`、`:85` `internal static event EventHandler? ThemeChanged`、`:167` `NotifyThemeChanged()`；
  - `src/UI/Settings/SettingsThemePalette.cs:67` `IsDark`；
  - `src/UI/Controls/LiteUI.cs:103` `public static SettingsThemePalette? CurrentPalette;`；
  - 既有应用范式见 `src/UI/Controls/SettingsThemeApplier.cs:126`（`bool dark = UIColors.CurrentPalette?.IsDark ?? false;`）。
  - ThemeEditor 与 UI 同属 `LiteMonitor.csproj` 单程序集 ⇒ 可访问 `internal` 成员。
  - **排除项**：预览画布与色样是**主题产物**，不得被工作区明暗污染。
- **验收**：设置切 Dark → 工作区背景与检查器背景变暗色；窗口已打开时跟随系统主题切换同步；高对比度走原生控件样式。

### 5.4 Phase D —— 资源与调度

#### D-1 R9：隐藏/最小化时暂停渲染（P2）

- **落点**：`src/ThemeEditor/GpuThemePreviewControl.cs:403/405`。
- **根因**：`OnTick` 无可见性检查；父控件切经典仅置 `Visible=false`（`ThemePreviewControl.cs:245-248`）。
- **修法**：**资源保留与调度暂停分离**——保留 D3D 设备与 SwapChain 复用，只在 `OnTick` 开头 `if (!Visible || (FindForm()?.WindowState ?? FormWindowState.Normal) == FormWindowState.Minimized) return;`。
- **可观测量（S-6）**：`presented frames` 当前**无预览侧入口**——真计数器是 `src/Rendering/Presentation/GpuSceneRenderer.cs:304 public int PresentedFrameCount => _presentedFrames;`（生产用法 `GpuPresentationHostController.cs:885`）。须像 A-2/A-3 那样**在 `GpuThemePreviewControl` 上新增一个只读可观测属性**（如 `public int PresentedFrameCount`），由其内部持有的 renderer 转发，供 §6.1 的 R9 断言使用。
- **验证点（必须实测）**：暂停 250 ms 后恢复可见，`FrameScheduler.TryTakeFrame` 在长间隔后**不得**一次性补多帧，也**不得**长时间不吐帧（需读 `src/Rendering/Gpu/Effects/FrameScheduling.cs` 的时间基准实现后确认，必要时补一个「长间隔后正常恢复」的回归用例）。
- **验收**：隐藏 250 ms → presented frames 增量 = 0（当前为 +6）；恢复可见后 1 帧内恢复出帧。

#### D-2 R10：目标 FPS 必须热更新到调度器（P2）

- **落点**：`src/ThemeEditor/GpuThemePreviewControl.cs:110/166`。
- **根因**：`_scheduler` 构造时定死 FPS；`UpdateTheme` 重建 `_runtimeSettings` 却不同步调度器。`FrameScheduler.UpdateTargetFramesPerSecond(int)`（`src/Rendering/Gpu/Effects/FrameScheduling.cs:37`）全仓 **2 个**调用点——生产 `src/Rendering/Presentation/GpuPresentationHostController.cs:632`、测试 `tests/LiteMonitor.Tests/MaterialSchedulingTests.cs:65`——**预览路径 0 调用**（S-10）。
- **修法**：`UpdateTheme` 中重建 `_runtimeSettings` 之后调用 `_scheduler.UpdateTargetFramesPerSecond(_runtimeSettings.TargetFramesPerSecond);`（与生产路径 `GpuPresentationHostController.cs:632` 同一调用形态）。
- **验收**：同一活动会话把 targetFps 改为 2 → 调度器实际 2（当前 30）。

### 5.5 Phase E —— 证据与门禁

#### E-1 G1：DPI 重布局接线 + 矩阵降级

- **落点**：`src/ThemeEditor/ThemeStudioForm.cs:18/113`（`AutoScaleMode.None`，几何在 BuildUi 时算死）、`docs/verification/theme-studio/N08-matrix.md`。
- **修法**：① 处理 DPI 变化（`WM_DPICHANGED` 或 `DpiChangedAfterParent`），按新 DeviceDpi 重算几何（分栏宽度、检查器固定像素布局）；② 把 M-03/M-04/M-05/M-06/M-12/M-13 六行**降级为 NOT-VERIFIED（代码逻辑已核对，实机待验证）**，与 M-07~M-11 口径一致；③ 补一行暗色组合行（与 C-2 联动）。
- **验收**：N08 矩阵中不再有「仅凭代码逻辑判定 PASS」的行；DPI 变化后布局重排有测试或实机记录。

#### E-2 G2：资源测试必须走真实 GPU 渲染往返

- **落点**：`tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs:352` 场景 4。
- **修法**：① 场景 4 改用**真实 GPU 主题**（`tests/LiteMonitor.Tests/FlagshipGpuThemeTests.cs` 有现成样本，夹具为 `resources/themes/*_GPU.json`，**依赖 Phase 0 已入库**）并走**真实目标事件**；② 断言增加**斜率校验**（首 20 次 vs 末 20 次的句柄增量之差），使「1:1 线性累积」与「稳定平台」可区分；③ 依赖 A-2（R1 让测试钩子也刷新画布）——两者必须同批落地，否则场景 4 仍不产生渲染往返。
- **验收**：场景 4 在真实 GPU 主题下运行；新增斜率断言能识别线性累积。

#### E-3 G3：全量结果可追溯

- **修法**：按 §2 环境约束**串行重跑**一次完整全量，记录**具体**执行/通过/失败/跳过数、产物摘要（artifact sha256）、以及失败项的性质分类（产品缺陷 vs 环境性失败），替换 N09 报告中的「2686+」。环境性失败不得混入产品缺陷，但必须单独列出。
- **验收**：N09 报告含具体数字与可复现命令；`full_acceptance.accepted` 由**实际**结果决定，不得预填。

---

## 6. 测试与回归策略

### 6.1 新增回归用例（每条 R 至少一条「打在产物上」的断言）

| 用例 | 断言对象 | 禁止的断言方式 | 备注 |
|---|---|---|---|
| R1 | 画布当前渲染目标/主题代次 | 断言「函数被调用过」 | 代次可复用 `GpuThemePreviewControl.CurrentGeneration`（`:92`） |
| R2 | 磁盘 `Skin` + 内存 `Skin` + 返回的 `Stage` | 只断言返回值 | 触发方式：置 `Settings.GlobalBlockSave = true`（`src/Core/Settings.cs:422-425`）⇒ `TrySave` 在 `src/Core/SettingsHelper.cs:265` 返回 false。⚠️ `GlobalBlockSave` 是**静态**属性，用例必须 **try/finally 复位**为 `false`，否则污染同进程后续用例（V2-S-6） |
| R3 | `EffectiveRoute` 来自 `UIController` 的真实状态 | 只断言非空 | 经典主题 + GPU 开关开启 ⇒ 不得报 GPU 生效 |
| R4 | **导出图像素采样值** | 断言「调用了 BuildDerivedTheme」 | 期望 `#AABBCC`（当前 `#112233`） |
| R5 | **导出 PNG 的实际宽高 + 内容布局方向** | 断言参数值 | GPU+Horizontal ⇒ 400×120；经典+Horizontal ⇒ 400×120 且横向布局 |
| R6 | `GpuPreviewError` 非空 + 代次未更新 | 只断言 bool 返回 | |
| R7 | `ActiveControl` 类型 + 属性路径解析成功 | 断言映射表含某字符串 | 覆盖「全部角色 × 3 目标」 |
| R8 | 工作区实际背景色 | 断言「切换了主题」 | |
| R9 | presented frames 增量 | 断言「不再调用 Tick」 | ⚠️ **需新增可观测量**：`GpuThemePreviewControl` 上暴露 `PresentedFrameCount`（转发自 `src/Rendering/Presentation/GpuSceneRenderer.cs:304`），见 D-1 |
| R10 | 调度器实际目标 FPS | 断言「调用了 UpdateTargetFramesPerSecond」 | |
| R11 | 投稿包 `route`（合同值仍合法）+ `previewRoute` 实际值 + `marketValidation.reviewRequired` 含不一致说明 | 断言 JSON 字段存在 | **不断言** `route` 被改写（M-1） |

### 6.2 既有测试不得回归

- `tests/LiteMonitor.Tests/ThemeStudio*Tests.cs`（6 个文件）、`ThemePropertyCatalog*Tests.cs`、`ThemeExportServiceTests.cs`、`ThemeFallbackStyleTests.cs`、`LegacyFallbackThemeRoutingTests.cs`、`FlagshipGpuThemeTests.cs` 等全部保持通过。
- 特别地，`tests/LiteMonitor.Tests/ThemeExportServiceTests.cs:383` 断言经典主题 `route=="legacy"` —— **M-1 采纳后该断言必须原样通过**（这正是不得改写 `route` 取值的原因）。
- 新增用例文件**按主题分文件**（避免污染既有文件）；首个建议命名：`tests/LiteMonitor.Tests/ThemeStudioAcceptanceRemediationTests.cs`。

### 6.3 验收门禁（全部必须通过）

| 门禁 | 命令 | 期望 |
|---|---|---|
| G-1 主程序零警告 | `dotnet build LiteMonitor.csproj -c Debug --no-incremental` | 0 警告 0 错误（保持可空治理成果） |
| G-2 解决方案零新增 | `dotnet build LiteMonitor.sln -c Debug --no-incremental` | 仅剩既有 xUnit 42 条 + CS0168 1 + CS0649 1，无任何 CS86xx |
| G-3 门禁 1（硬门禁） | 四个生产工程 `dotnet build <proj> -c Release --no-incremental -warnaserror -p:NuGetAudit=false` | 全部 EXIT=0 |
| G-4 门禁 2 | `dotnet build LiteMonitor.sln -c Release --no-incremental -p:NuGetAudit=false -p:TreatWarningsAsErrors=true '-p:WarningsNotAsErrors=CS0168%3BCS0649%3BCS8632%3BxUnit2031%3BxUnit1030%3BxUnit1031%3BxUnit2013%3BxUnit1026%3BxUnit2000%3BxUnit2012%3BxUnit2017%3BxUnit2020'` | EXIT=0（分号必须 `%3B`；**必须带 `-p:NuGetAudit=false`**，否则离线环境 NU1900 被升为 error 造成假红） |
| G-5 用例数门槛 | `pwsh -File .github/workflows/assert-test-counts.ps1 -TrxRoot '<trx目录>' -MinTotal 2686 -ExpectedAssembly 'LiteMonitor.Tests, LiteMonitor.Updater.Tests, LiteMonitor.GpuNonperformance.Tests' -MaxSkipped 1 -MustExecuteTestClass 'MarketIndexContractTests, ReviewMarketPersistenceRegressionTests, ReviewMarketInstallRaceRegressionTests, ThemeMarketContractTests, ThemeMarketPolicyTests, ReviewFix20261026Tests, E00Verification20261026Tests, I2HostSupervisionTests, I2PipeProtocolTests, I2SnapshotAtomicityTests, SettingsProjectionTests, SettingsModuleServicesTests, E05ThemeWiringGuardTests' -MustExecuteTestClassMinCount 5` | EXIT=0，且用例数 ≥ 2686（新增用例后应更高）。参数与 `.github/workflows/ci.yml:115/118/124/135/136` 完全一致 |
| G-6 验收报告复验 | **前提**：Phase 0 已把探针入库为 `tools/ThemeStudioAuditProbe/`（用法：`dotnet run --project tools/ThemeStudioAuditProbe -- <输出目录>`，产出 `observations.json`）。据此重跑 `observations.json` 的 10 条观测点 + 新增 R11 观测点 | 每条观测变为报告期望值；R11 观测点 =「`route` 保持合同值 + `previewRoute` 为实际值 + `reviewRequired` 含不一致说明」。**若探针未能入库，则本门禁降级为「人工逐条复核 + 记录截图/像素证据」，不得伪称已重跑** |
| G-7 计划回填 | 本文件追加「执行进展」节 | 逐条 R/G 的落点、提交号、验收结果 |

---

## 7. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| R1/R6/R7/R9/R10 都动 `ThemeStudioForm.cs` / `GpuThemePreviewControl.cs` | 并行 agent 会互相冲突 | Phase A 的 R6 与 Phase D 串行；若用并行 agent，每个 agent 一个独立 git worktree（避免 `bin/obj` 锁冲突），合并前逐批跑 G-1/G-2。**Phase A 的 A-1 与 A-4 同改 `ThemeStudioLauncher.cs` ⇒ 必须合为同一组** |
| 主工作树还有**无关的**未提交改动（9 个 `docs/*.md` 删除、`.gitignore` 等） | 混进本批提交 | 每批**只 `git add` 明确路径**；提交前用 `git diff --cached --stat` 核对；9 个 docs 删除明确排除在基线提交外（§5.0） |
| 主题工作室 WIP 尚未提交，是本批的隐含基线 | 基线丢失或与后续提交混淆 | Phase 0 先把 WIP 与验收报告/证据**单独提交为基线提交**（含来源说明），本批改动在其之上 |
| **新克隆的 CI 缺少 GPU 主题夹具与新测试文件** | E-2 在 CI 上不可用 | Phase 0 必须纳入 `resources/themes/*_GPU.json`（3 个）与 4 个新测试文件；验收检查项已写入 §5.0 |
| **验收探针脚本未入库** | G-6「重跑观测点」在仓库内不可执行 | Phase 0 义务：探针脚本入库；未入库则 G-6 降级为人工复核并如实标注 |
| R2 触及 `SettingsHelper` 语义 | 影响 `src/UI/SettingsForm.cs:181` 等既有调用方 | 只改**调用方**（改调 `TrySave`），不改 `Save` 本身；G-2/G-3 覆盖既有调用方回归 |
| **B-3 改写 `route` 会破坏投稿合同与既有测试** | 市场拒收 + 测试打红 | **禁止**改写 `route`（M-1）；一致性信息走既有 `marketValidation.reviewRequired` 列表 |
| R7 映射修正可能暴露「GPU 主效果确实没有可编辑属性」 | 范围可能超出「改映射表」 | 若确认无字段，则**移除该角色**并记录（属产品范围决策，需在 §8 执行记录中标注），不擅自新增模型字段 |
| **C-1b 命中接线是未估算的新功能** | 工期不可控、可能拖累整批交付 | 默认**排除**在本批之外（Phase F 单独立项）；若批准，须先出选型结论（方案甲 布局反查 / 方案乙 D3D 拾取）并重估工期 |
| A-4 若采用 `RenderingTransitionCoordinator` 方案，需在 `UIController` 加 internal 访问器 | 触碰 UI 核心类 | 只加**只读**访问器，不改协调器行为；G-2/G-3 覆盖回归 |
| R9 的「长间隔后恢复」行为未验证 | 可能在恢复瞬间补帧或卡住 | D-1 内置一项实测 + 必要时补 `FrameScheduler` 侧回归用例 |
| 全量测试耗时与并发限制 | 门禁 G-5 跑不完 | 串行跑；`testhost` 冲突时停止自己的会话；**构建与测试不得并发**（§2） |
| 离线环境 NuGet 审计误报 | G-3/G-4 假红 | 命令行统一加 `-p:NuGetAudit=false`（S-1） |

---

## 8. 执行记录（执行时回填）

| 批次 | 提交 | 消除项 | 验收结果 | 备注 |
|---|---|---|---|---|
| Phase 0（基线固化） | | WIP + 报告 + 证据 + 探针 + 夹具入库；`.cua-helper/` 入 gitignore | | |
| Phase A | | R2 / R1 / R6 / R3 | | |
| Phase B | | R4 / R5 / R11 | | |
| Phase C | | R7（C-1a：映射/自检/焦点）/ R8 | | C-1b 默认不在本批 |
| Phase D | | R9 / R10 | | |
| Phase E | | G1 / G2 / G3 | | |
| Phase F（可选） | | R7 命中接线（C-1b） | | 需用户先批准选型 |

---

## 附录 A：R–代码落点速查

| 项 | 文件 | 关键行 | 现有可复用资产 |
|---|---|---|---|
| R1 | `src/ThemeEditor/ThemeStudioForm.cs` | 1147、1229-1235、924/942/957-958/976/982 | `RefreshPreviewNow()`、`GpuThemePreviewControl.CurrentGeneration`（`:92`） |
| R2 | `src/ThemeEditor/ThemeStudioLauncher.cs`；`src/Core/SettingsHelper.cs`；`src/Core/Settings.cs` | 43；248、265、469；422-425 | `TrySave`（internal static bool） |
| R3 | `src/ThemeEditor/ThemeStudioLauncher.cs`；`src/UI/UIController.cs`；`src/UI/RenderingTransitionCoordinator.cs`；`src/Core/Actions/AppActions.cs`；`src/Plugins/ThemeMarket.cs`；`src/Core/ThemeManager.cs` | 60/67/68/78；124/127/130；149/152；219/233；192；531 | `IsGpuPresentationActive`、`GpuFallbackCode`、`GpuRuntimeFailure`、`ExpectedRoute`/`EffectiveRoute`、`Settings.Load().Skin`、`ThemeManager.Load(name)` |
| R4 | `src/ThemeEditor/ThemeExportService.cs`；`src/Rendering/LegacyFallbackStyleResolver.cs` | 298/327/330-335/334/359-360/589/602；229、241 | `BuildDerivedTheme` |
| R5 | `src/ThemeEditor/ThemeExportService.cs`；`src/ThemeEditor/GpuThemePreviewControl.cs`；`src/ThemeEditor/ThemePreviewViews.cs`；`src/UI/UILayout.cs`；`src/ThemeEditor/ThemeStudioForm.cs` | 293/299-301/337/653/686-689/**691（画布 Bounds 240×420）**/606/617；95-100、27/31/47、348-349；112/188/225/234/253-256/267；29；808-810 | `GpuThemePreviewControl` 构造函数形参（shape/horizontalCols）、`HorizontalPreviewMock.BuildColumns`（**须传 `width=400`**）、`HorizontalPreviewControl.RenderPreview`（owner 窗体 Size 见 `:681`） |
| R6 | `src/ThemeEditor/GpuThemePreviewControl.cs`；`src/ThemeEditor/ThemePreviewControl.cs` | 130/136-139/141-144/147-150；237/240-241 | `UpdateTheme` 的 bool 返回值、`IsActive`（`:123`）、`Error`（`:125`） |
| R7 | `src/ThemeEditor/ThemeStudioForm.cs`；`src/ThemeEditor/ThemePropertyCatalog.cs`；`src/ThemeEditor/ThemePropertyCatalog.Gpu.cs`；`src/ThemeEditor/PropertyInspector.cs` | 1100；316-324、327-362、343-344、186；41、163-175、188、228；79-88 | `ThemePropertyCatalog` descriptor 注册表、`rendering.parameters` 真实键 |
| R8 | `src/ThemeEditor/ThemeStudioForm.cs`；`src/UI/Settings/SettingsThemeManager.cs`；`src/UI/Settings/SettingsThemePalette.cs`；`src/UI/Controls/LiteUI.cs`；`src/UI/Controls/SettingsThemeApplier.cs` | 31-32；79/82/85/167；67；103；126 | `UIColors.CurrentPalette?.IsDark`、`ThemeChanged` 事件 |
| R9 | `src/ThemeEditor/GpuThemePreviewControl.cs`；`src/ThemeEditor/ThemePreviewControl.cs`；`src/Rendering/Presentation/GpuSceneRenderer.cs` | 403/405；245-248；304 | `FrameScheduler.TryTakeFrame`、`PresentedFrameCount`（**须在预览控件上新增转发**） |
| R10 | `src/ThemeEditor/GpuThemePreviewControl.cs`；`src/Rendering/Gpu/Effects/FrameScheduling.cs`；`src/Rendering/Presentation/GpuPresentationHostController.cs`；`tests/LiteMonitor.Tests/MaterialSchedulingTests.cs` | 110/166；37；632；65 | `FrameScheduler.UpdateTargetFramesPerSecond` |
| R11 | `src/ThemeEditor/ThemeExportService.cs`；`src/Plugins/ThemeMarket.cs`；`tests/LiteMonitor.Tests/ThemeExportServiceTests.cs` | 146/382/450/465/471-473/478/538；245-286、261-265；383 | `ThemeImageExportResult`（含 route / isFallback）、`marketValidation.reviewRequired` |
| G2 | `tests/LiteMonitor.Tests/ThemeStudioResourceTests.cs`；`tests/LiteMonitor.Tests/FlagshipGpuThemeTests.cs` | 352、78-87、600-602；31/63/87/146-154 | `SlopePerIteration`（`:600-602`）、`FlagshipThemes`（`:31`） |
