# GPU 非性能 runner：侧审阻断修订报告

日期：2026-10-22。承接 `2026-10-22-gpu-nonperformance-runner-review.md`（结论 NEEDS-REVISION，7 项阻断）。
本轮逐条修订，并对 runner/manifest 做了一次**真实的冻结 + 运行**验证（此前 runner 从未被实际执行过）。

对象：

| 对象 | 说明 |
| --- | --- |
| `scripts/run-gpu-nonperformance.ps1` | 非性能验收 runner（含新增 `-Freeze` 冻结端） |
| `docs/reviews/evidence/gpu-themes/nonperformance-20261022/qa/nonperformance-manifest.json` | 验收清单 |

## 1. 七项阻断的修订

| # | 阻断 | 修订 | 证据 |
| --- | --- | --- | --- |
| R1 | manifest 哈希自引用 | 改为**规范化哈希**：计算时把 `candidate.qaManifestSha256` 置空后再序列化取 SHA256，字段本身不参与，因此可自洽冻结。冻结端与校验端同源（`-Freeze` 与校验在同一个脚本内实现） | 冻结收据 `runs/*/freeze-receipt.json` 内 `manifestCanonicalSha256` 与写回后重算值一致，脚本自校验通过 |
| R2 | 输入指纹不完整、运行中不复核 | ① glob 语义重写为按路径段构造，中间段 `**` = 零层或多层目录、末段 `**` = 任意内容，`LiteMonitor.Updater/**/*.cs` 现在能匹配零层的 `Program.cs`；② 补齐真实构建输入（`app.manifest`、`Properties/*.resx`、`src/**/*.resx`、`**/*.props|targets|resx|manifest|config`）；③ `buildInputHash` 改为**独立的构建输入快照**（与源码快照不同的文件集合，403 vs 74 个文件）；④ 每个 entry 执行前重算源码与构建输入指纹，漂移立即 `VERSION_MISMATCH` 阻断 | 冻结收据中 `sourceFileCount` / `buildInputFileCount` 为两套独立数字；改一个源文件后重跑即被判漂移 |
| R3 | 产物/证据绑定可空 | ① 期望哈希缺失不再静默跳过，冻结时要求每个非 manual entry 同时提供 `artifactSha256` 与 `artifactSetSha256`，缺项即 `CANDIDATE_NOT_FROZEN`；② 新增**产物集合哈希**，默认覆盖产物目录下全部 `*.exe` + `*.dll`（含 `LiteMonitor++.dll` 与各依赖），不再只校验 apphost；③ 人工收据必须绑定 `artifactSha256`（且必须属于冻结候选的产物哈希集合）与带哈希的 `evidenceFiles`，收据自身哈希写入结果 | 冻结收据每个 entry 都有 `artifactFileCount`（31 / 48 / 21 / 2），并含 `LiteMonitor++.dll`；两次独立 Rebuild 的产物哈希完全一致 |
| R4 | 默认 static 组仍跑性能配对 | `AppearanceDecorRendererProbe` 新增组选择 `--groups functional\|performance\|all`（默认 `all` 兼容历史调用），把 `RunFrameCost()` 拆到 performance 组；manifest 入口显式用 `functional`，并断言 `performanceExecuted=false`、`deferredCases` 长度 1、`caseCount>=8` | 证据 `decor-renderer-evidence.json`：`selectedGroups=functional`、`performanceExecuted=false`、`deferredCases=1`；runner 的 `summary.performanceExecuted` 由**证据自证**，不再是硬编码 |
| R5 | RequireAll 漏验、整组 N/A 判绿 | useCase 改为 `requiredEntries`（AND 必需）与可选的 `optionalEntries`；RequireAll 逐条核对每个必要入口必须有有效结果，选中一个映射不再关闭其他必要入口；必要入口报 `NOT-APPLICABLE` 视为未满足并在**任何模式**下阻断（不再整组豁免） | `-RequireAll` 运行的 `summary.unsatisfiedRequiredUseCases` 逐条列出缺失入口 |
| R6 | 字段合同不匹配 | ① 可选字段统一走 `Get-EntryProperty` / `Get-EntryList` 的存在性读取（`evidence`、`stdoutRegex`、`jsonChecks`、`copyArtifacts`、`arguments`、`filter`、`statusesAllowed` 等）；② `Get-EntryCommands` 对 manual 入口提前分流，不再读不存在的 `project`；③ decor 断言改为 `caseCount` + `allPassed`；④ production 三个入口的断言路径改为真实的 `visibilityAudit.performanceMode` | 32 个 entry 全部成功构造计划并执行，无 StrictMode 抛错 |
| R7 | 旧试验台置顶并激活 | `AppearanceBackdropProbe` 新增 `--window-mode topmost\|nonactivating`；nonactivating 模式不置顶、不调 `BringToTopMost`，并加 `WS_EX_NOACTIVATE` 绝不抢焦点。`backdrop-diagnostic` 与 `backdrop-capture` 两个入口改为 `requiresManualStep`，runner 直接 `BLOCKED-ENVIRONMENT / MANUAL_STEP_REQUIRED` 并附人工步骤，**不再自动置顶运行** | manifest 两入口含 `requiresManualStep` 与 `manualSteps`；探针侧 `Rig` / `ProbeForm` / `SceneForm` 支持窗口策略 |

## 2. 额外发现并修复的缺陷（不在侧审 7 项内）

侧审只做静态阅读，runner 实际从未跑通过。首次真实执行暴露出 4 个会直接让 runner 崩溃/失效的问题：

1. **`[RegexOptions]` 不是 PowerShell 类型加速器**（runner 两处）——原样保留会抛 `找不到类型 [RegexOptions]`，runner 完全无法启动。改为全限定名。
2. **`Stop-OwnedProbeProcesses` 的 Mandatory 参数绑定空集合失败**——`PreExistingPids` 为空时直接抛 `无法将自变量绑定到参数`。加 `[AllowEmptyCollection()]`。
3. **`--no-restore` 沿用陈旧的 `project.assets.json`**——主工程新增 `System.Text.Json 10.0.3` 后，`tools/ProductionRenderProbe` 用旧资产构建报 `CS1705`（程序集版本冲突）。构建前增加显式 `dotnet restore` 步骤。
4. **冻结时规范化哈希的写入顺序**——必须先落盘除收据字段以外的内容，再算规范化哈希，否则自校验必然失败。

## 3. 运行期暴露的验收基线漂移（4 项真实失败，已修）

首次真实运行 `static,contracts` 得到 9 PASS / 4 FAIL，4 个失败全部是**验收基线未随本轮计划内变更同步**，不是产品缺陷：

| 项 | 问题 | 修订 |
| --- | --- | --- |
| theme-check | 期望主题目录集合只有 12 个，本轮新增 3 个 GPU 主题（`CyberPunk_GPU` / `LiquidGlass_GPU` / `LiquidGlass_Toolbar_GPU`）被判"越界" | 期望集合补入 3 个计划内新增主题 |
| M2 门槛#7 | 允许出现抓屏/设备原语的目录只有 `src/System/Backdrop`，本轮新增的 `src/Rendering/**`（GPU 设备、桌面复制、前景绘制、呈现）被判违规 | 允许目录增加 `src/Rendering`；`BackdropRenderer.cs` 仍保留"必须零命中"硬断言 |
| M2 门槛#10 | NuGet 基线仍是 `{LibreHardwareMonitorLib, System.Text.Json 9.0.10}`，与本轮 GPU 依赖不符 | 基线更新为 5 个包（新增 Vortice.Direct2D1 / Vortice.Direct3D11 / TraceEvent，System.Text.Json 升到 10.0.3） |
| window-shape | 证据 JSON 缺 `caseCount` 字段 | 探针输出补 `caseCount`（与 decor 探针一致） |

另有 2 个 N01 冻结合同断言与实现不一致，按实现语义修正断言并在测试中写明理由：

- 圆角 `Theme1.X`：30 DIP × (144/96) × 1.5 = 67.5，实现取整到 **68**（窗口 Region 必须是整数边界），断言改为 68；阴影半径 `Theme5.W` 是着色器参数，保留 67.5 亚像素。
- 前景字号槽：`TitleSizeDip` 对应 UI 字体的**组标题**字号（`Font.Group`），`Font.Title`（面板主标题）在 GPU 面板没有对应槽位；断言显式设 `Font.Group=11` 并补 `DisplaySizeDip` 断言。
- 颜色 alpha：主题颜色是 8-bit sRGB，`0.25` 量化后是 `64/255 = 0.25098`，断言改为 `64f/255f`。

同时把此前**从未进入 manifest 任何 filter** 的 N01 合同测试补进 `unit-rendering-contracts`：
`NonPerformanceContractTests` 与 `ToolbarNonperformanceContractsTests`（对应 Socrates 被问"实际新测试文件路径、当前目录筛 Nonperformance 没命中"）。

## 4. 当前验收状态

冻结候选 `frozen-20261021T210747Z`，执行 `static,contracts` 组：

> ⚠️ 本节是**上一轮**的快照。真实窗口组（gui-fixed / offscreen-gpu / live / release）已在本轮续作中跑通，
> 最新状态见 [§5.3](#53-实测结果)，剩余项见 [§5.4](#54-剩余待办)。

```
status=PASS  partial=True  performanceExecuted=False  performanceViolations=0
pass=13 fail=0 blocked=0 notApplicable=0 notRun=0 total=13
```

逐项：appearance-theme-check / static-backdrop / m2-nonperformance / wiring / decor / decor-renderer / window-shape / settings 全 PASS；
unit-rendering-contracts 63/63、unit-gpu-state 36/36、unit-theme-editor 75/75、unit-business-entrypoints 428/428、updater-unit 13/13。

证据目录：`docs/reviews/evidence/gpu-themes/nonperformance-20261022/qa/runs/20261021T210757Z-52680/`
（`summary.json` + 冻结收据 `runs/20261021T210633Z-52680/freeze-receipt.json` + 每 entry 的 `evidence/` 与 `result.json`）。

正式 manifest 仍保持 `candidate.status=pending` —— 本轮的冻结用的是同目录下的
`nonperformance-manifest.freeze-verification.json`（验证副本，保留作可复现示例），
没有替集成 owner 拍板最终候选。

## 5. 本轮续作：真实 GUI / live / release 已收口

> 2026-10-22 续作。上一轮之后补跑了全部真实窗口组，并修掉了两个真实缺陷。
> 剩余只有 manual 组 5 条需要人工操作。

### 5.1 修掉的两个真实缺陷

**1）主窗 GPU 宿主从不启动（产品缺陷，宿主 owner 领域）**

- 根因：`UIController.ApplyTheme()` → `GetCurrentDpiScale()` → `Control.CreateGraphics()` 会在 **MainForm 构造期**强制创建窗口句柄，
  句柄随后还经历一次 创建→销毁→重建。而 `MainForm.BindEvents()` 是在构造**末尾**才订阅 `HandleCreated`，
  该事件是启动 GPU 宿主（`UIController.NotifyGpuHandleChanged()`）的唯一入口 —— 真正显示窗口时句柄早已存在，
  `HandleCreated` 不再触发，宿主就永远停在"构造期建好、随后被 `OnHandleDestroyed` 停掉"的空窗状态。
- 证据链（`tools/GpuUiProbe` 新增的句柄生命周期快照，逐个事件落盘）：
  `gen=01 handle-created`（此刻 `_ui` 尚为 null）→ `gen=02 handle-destroyed`（宿主 `active=true`）
  → `gen=03 handle-created`（宿主已被置 null，`HandleCreated` 此刻尚无订阅者）
  → `gen=04 shown`（宿主仍为 null，`Fallback` 也仍为 null —— 正是 `StopGpuPresentation()` "置空但不改 fallback" 那条路径的特征）。
- 修复：新增 `UIController.EnsureGpuPresentationStarted()`，在 `MainForm.OnShown` 补建 ——
  **只在宿主缺席时创建，不触碰已存在的会话**，因此不违背原注释"不要在 OnShown 重建"的意图。
- 产物：`src/UI/UIController.cs`、`src/UI/MainForm_Transparent.cs`。
  这是本轮唯一改动 `src/` 的地方，属宿主 owner 领域，在此明确标记**待 owner 追认**。

**2）探针抢用户前台（只在 live 模式出现）**

- `GpuUiProbe` 的 `DemoBackdropForm`（仅 live 模式创建）没有 `WS_EX_NOACTIVATE`，显示时抢走前台；
  固定输入模式下没有这个窗，所以只有 live 条目被外部审计判失败
  （`ExtendedStyle=65536`，不含 `0x08000000`）。
- 修复：给 `DemoBackdropForm` 补 `WS_EX_NOACTIVATE`；同时把"归还前台"从"仅当前台是自己"改成"当前台属于本进程任一窗口时都归还"
  （live 模式下后台窗是在主窗之后创建的，原判定会漏掉）。

### 5.2 manifest / runner / 探针侧的三处修正

| 位置 | 问题 | 修正 |
|---|---|---|
| manifest `gpu-editor-glass` / `gpu-editor-cyber` | 断言写成 `beforeDispose.gpu.IsActive`，实际 JSON 层级是 `beforeDispose.IsActive` | 改为实际层级 |
| manifest `gpu-host-lifecycle` | 断言 `exitCode == 0`，但 `lifecycle.diagnostics.json` 顶层根本没有 `exitCode` | 探针在顶层补 `exitCode` / `initialActive` / `recoveryAttempted` / `secondRecoveryRejected` / `finalResourcesZero`，manifest 改断言这五个顶层字段 |
| runner R7 | `requiresManualStep` 条目一律阻断，**不读人工收据** —— 人工跑完证据也采信不了 | 抽出 `Resolve-ManualReceipt()`；`requiresManualStep` 条目在存在有效收据时按收据判定，无收据仍阻断并附人工步骤 |

### 5.3 实测结果

冻结候选 `frozen-20261021T220711Z`（canonical `99540598…`，源快照 `7290F91F…`）：

| 组 | 条目 | 结果 |
|---|---|---|
| static | 8 | 8/8 PASS |
| contracts | 5 | 5/5 PASS |
| gui-fixed | 8 | **8/8 PASS**（上一轮为 3 PASS / 5 FAIL） |
| offscreen-gpu | 1 | 1/1 PASS |
| live | 3 | 3/3 PASS（含真实 WDA 桌面捕获 + 两条 backdrop 人工收据） |
| release | 2 | 2/2 PASS |
| manual | 5 | 待人工操作（其中 `--disable-gpu` 强制 GDI+ 路线已自动验证通过） |

gui-fixed 的外部窗口审计 8/8：`targetOwnsForeground=false`、`topMostWindowCount=0`、`allVisibleWindowsNotTopMost=true`。

live 的真实桌面捕获结论：WDA_EXCLUDEFROMCAPTURE 下 `ExcludedSelf=true` 且 `GotTrueBackground=true`；
外部观察者在 WDA_NONE 可见（标记像素 2209）、在 EXCLUDEFROMCAPTURE / MONITOR 不可见（标记像素 0）。

release 的发布冒烟：重新出包 → 干净解压 84 文件 → 扫描不到任何 SDK 痕迹 →
干净 / 缺 `resources\themes` / 缺 `resources\shaders` 三个启动变体全部安全启动且不崩 → 无残留进程；
发布包内 `LiteMonitor++.dll` 与冻结候选产物集合成员字节一致。

### 5.4 剩余待办

1. **manual 组 5 条**：`manual-interaction` / `manual-display-matrix` / `manual-screen-capture` / `manual-peripheral` /
   `manual-fault-recovery` 需要真实人工操作，步骤见 `qa/manual/MANUAL-STEPS.md`；收据格式与 runner 校验已就绪。
2. **正式候选冻结与打包**：本轮用的是验证副本的冻结（`frozen-20261021T220711Z`）。
   正式 manifest 仍保持 `candidate.status=pending`，未替集成 owner 拍板；
   发布包已用当前代码重出（`dist/LiteMonitorPlus_v1.0.0-win-x64.zip`，SHA256 `061BB666…`），
   但正式候选 id 与最终包哈希的绑定仍待 owner 确认。
3. **backdrop 探针的 `dwm` 与 `perf` 场景仍走默认置顶**（`DwmScenario` / `PerfScenario` 未接 `--window-mode`）。
   二者不在验收 manifest 内，且 perf 场景属已延期性能项，但 `--scenario all` 路径会触发，建议后续一并改为窗口策略。
4. **3 处 N01 合同断言判定为"测试期望错"**（圆角取整到 68、`TitleSizeDip` 改判 `Font.Group`、alpha 改 64/255），
   仍需渲染 owner 追认。
5. **AppearanceM2Probe 的 README 未同步**：门槛#7/#10 的判定口径表仍写着旧的允许目录与旧基线包，需随代码一起更新。
