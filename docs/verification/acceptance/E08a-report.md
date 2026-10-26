# E08a 验收报告 —— 产物定位与证据模板（完善计划 §12 前两条 / 审查报告 G5 收口）

- 日期：2026-10-26。
- 工作包：P-E08a（W1 三路并行之一）。分支：`pkg/E08-artifacts`（独立工作树 `.worktrees/e08-art`，基于 master `18e63c9`）。
- 边界遵守：零产品源码改动（`src/**` 未触碰）；与 P-E00（回归测试文件）、P-E05（SettingsForm/主题）零交集。
- 验证口径：一律 TRX（total/executed/passed/failed + 逐用例 outcome）或裸命令退出码。

## 1. 交付物清单

| 交付物 | 路径 | 说明 |
|---|---|---|
| Host 产物定位改造 | `tests/LiteMonitor.Tests/I2HostBinaries.cs`（新，自 `I2HostSupervisionTests.cs` 抽出） | 环境变量优先 + 本轮构建清单 SHA-256 校验 |
| 定位器单元测试 | `tests/LiteMonitor.Tests/I2HostBinariesLocatorTests.cs`（新） | 12 例，含 G5 场景复现 |
| 证据记录模板 | `docs/verification/templates/evidence-template.md`（新） | §12 第二条全部字段 |
| 证据收集脚本 | `scripts/collect-evidence.ps1`（新） | 只收集，不上传/不改 CI |
| 验收报告 | `docs/verification/acceptance/E08a-report.md`（本文件） | |
| 证据工件 | `docs/verification/baseline/e08a-20261026/`（新） | locator.trx、g5-no-envvar.trx、g5-with-envvar.trx、whitespace-inventory.json |

`I2HostSupervisionTests.cs` 的唯一改动是把原内嵌的 `I2HostBinaries` 类定义原样迁出（删类 + 加指引注释），属"动 I2HostBinaries"的搬迁本身；该文件其余 21 个测试用例逐字未动。

## 2. Host 产物定位改造说明

### 2.1 改造前的问题（G5）

原 `I2HostBinaries.Locate` 固定查找 `src/HardwareHost/bin/{Debug|Release}/net8.0-windows/`：
当本轮构建使用 `-p:BaseOutputPath=bin/review-xxx/` 时，测试不跟随，而是静默使用默认目录里的**旧产物**；
DLL 摘要与独立输出目录版本不一致也不会被发现（2026-10-26 审查报告 §G5）。

### 2.2 改造后的解析与校验顺序

1. **显式路径优先**：环境变量 `LITEMONITOR_HOST_EXE`（绝对路径；相对路径按仓库根解析）指定的本轮 Host 产物。
   文件缺失、文件名不是 `LiteMonitor.HardwareHost.exe` 时**直接失败**，不回退默认目录。
2. **默认构建目录**：`<repoRoot>/src/HardwareHost/bin/<Configuration>/net8.0-windows/LiteMonitor.HardwareHost.exe`。
   缺失时直接失败，错误信息给出两条出路（重建默认目录 / 按自定义输出目录设置 `LITEMONITOR_HOST_EXE`）。
3. **本轮构建清单校验（两条路径都过）**：
   - 清单锚点 = 主工程输出目录 `<repoRoot>/bin/<profile>/<Config>/net8.0-windows/`——`LiteMonitor.csproj` 的
     `CopyHardwareHostToOutput` 目标把 `src/HardwareHost/$(BaseOutputPath)$(Configuration)` 下的
     `LiteMonitor.HardwareHost.*` 复制到主输出，与 `dotnet test` 同一轮构建、且随 `BaseOutputPath` 全局属性流动。
     锚点目录由测试输出目录布局推导（`…\tests\LiteMonitor.Tests\bin\<profile>\<Config>\net8.0-windows`
     ⇒ `<repoRoot>\bin\<profile>\<Config>\net8.0-windows`），自定义输出目录自动跟随。
   - 逐文件 SHA-256 必须一致：`LiteMonitor.HardwareHost.dll / .exe / .deps.json / .runtimeconfig.json / LiteMonitor.Contracts.dll`。
   - 第二锚点：测试输出目录内的 `LiteMonitor.Contracts.dll`（本轮构建随 ProjectReference 复制的引用副本）与 Host 目录内副本比对。
   - 清单锚点缺失、任何文件缺失或摘要不符 ⇒ **直接失败**，异常文本列出两侧 SHA-256 与三种处理方式，
     不在 Debug/Release/其他工作树之间静默挑旧产物。每个工作树只认自己的仓库根与自己的清单，跨工作树设 `LITEMONITOR_HOST_EXE` 同样会被摘要校验拦下。
4. **证据记录**：定位成功后向测试输出写一行 `[I2HostBinaries] via=… exe=… SHA256(…)=… manifest=…`（随 xunit 进 TRX StdOut），
   并经 `I2HostBinaries.Artifact`（`I2HostArtifact` 记录：路径、来源、逐文件摘要）供报告/脚本取数——落实 G5 的"记录摘要"要求。

确定性前提已实测验证：.NET 8 确定性编译下，同一轮构建在 Host 目录与主输出目录副本的 5 个文件 SHA-256 全部一致
（不同轮次、不同 BaseOutputPath 的同版本构建也一致），正常增量/重复构建不会误报。

### 2.3 单元测试（注入式核心，不触环境变量、不触发构建）

`I2HostBinariesLocatorTests`（12 例，TRX：`docs/verification/baseline/e08a-20261026/locator.trx`，
total=12 / executed=12 / passed=12 / failed=0 / skipped=0，退出码 0）：

- 环境变量显式路径 + 清单一致 → 采用并记录摘要（G5 修复路径）；
- 环境变量相对路径按仓库根解析；
- 环境变量路径缺失 → 失败且不回退（即便默认目录合法也不回退）；
- 环境变量文件名不对 → 失败；
- **G5 复现**：默认目录旧产物与清单不符 → 失败（信息含两侧摘要与修复指引）；
- **G5 变体**：Host 程序集一致但 Contracts 陈旧 → 失败（依赖层漂移同样拦截）；
- 默认目录缺失 → 失败并给出构建指引；
- 清单锚点缺失 → 失败（不接受不可验证的产物）；
- 默认目录与清单一致 → 通过；
- 环境变量空白值视为未设置；
- 清单锚点推导：自定义/默认两种布局；
- 非标准布局返回 null。

## 3. G5 真实场景复现与修复证据

在真实工作树上按审查报告 G5 的场景完整重演（不用任何桩）：

**环境**：`.worktrees/e08-art`（master `18e63c9` + 本包改动）；Debug；`NUGET_PACKAGES=G:\TOOL\LiteMonitor-fork\.nuget-packages`；`DOTNET_CLI_HOME=G:\TOOL\LiteMonitor-fork\.dotnet-cli-home`。

**复现步骤与结果**：

| 步骤 | 命令 | 退出码 | 结果 |
|---|---|---|---|
| ① 基线构建 | `dotnet build tests/LiteMonitor.Tests/… -c Debug` | 0 | 默认目录 Host dll `8C40BD19…`＝主输出清单副本（同一轮构建） |
| ② 制造"旧产物" | `dotnet build src/HardwareHost/… -c Debug -p:Version=9.9.9-e08a-repro` | 0 | 默认目录变为非本轮产物：Host dll `21870F84…`（不动任何源码，仅产物代差） |
| ③ G5 场景：自定义输出目录跑 I2 自测，**不设变量** | `dotnet test tests/LiteMonitor.Tests/… -c Debug -p:BaseOutputPath=bin/review-e08a/ --filter FullyQualifiedName~I2HostSelftestTests`（TRX：`g5-no-envvar.trx`） | 测试失败（3/3 失败） | **直接失败，不再静默挑旧产物**。异常逐项列出：`LiteMonitor.HardwareHost.dll: Host 目录 21870F84… ≠ 本轮构建清单 8C40BD19…`，另含 exe/deps/Contracts 三项差异及测试输出引用副本差异 |
| ④ 修复验证：设 `LITEMONITOR_HOST_EXE` 指向自定义输出目录 Host | 同上 + `LITEMONITOR_HOST_EXE=…src\HardwareHost\bin\review-e08a\Debug\net8.0-windows\LiteMonitor.HardwareHost.exe`（TRX：`g5-with-envvar.trx`） | 0 | **3/3 通过**；TRX StdOut 记录 `[I2HostBinaries] via=LITEMONITOR_HOST_EXE … SHA256(LiteMonitor.HardwareHost.dll)=8C40BD19… manifest=…\bin\review-e08a\Debug\net8.0-windows`——摘要与该轮构建逐字节同源 |
| ⑤ 恢复现场 | `dotnet build src/HardwareHost/… -c Debug`（去掉版本戳） | 0 | 默认目录恢复为 `8C40BD19…`，与清单一致（确定性编译，字节级还原） |

结论：改造后，"自定义输出目录验证"要么用本轮产物并通过（附摘要），要么硬失败并给出两侧摘要与修复指引；
审查报告 G5 的"抽查 DLL 摘要不同，不能把它自动等同于新构建产物"的隐患已收口。

## 4. 全量回归（主测试工程，TRX 口径）

- 命令：`dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Debug --results-directory .tmp/e08a/full-results --logger "trx;LogFileName=full-main.trx"`，退出码 0。
- TRX 计数器：total=**2457**，executed=**2450**，passed=**2450**，failed=**0**；
  逐用例 outcome 统计：Passed=2450，**NotExecuted=7**（TRX 的 `notExecuted` 计数器显示 0——
  跳过数必须按逐用例 outcome 统计，此坑已写进证据模板与收集脚本）。
- 全程使用的 Host 产物（TRX StdOut 证据行）：`via=DefaultBuildOutput`，Host dll `8C40BD19…`、exe `F235914F…`、
  Contracts `2489AD2A…`、清单锚点 `…\bin\Debug\net8.0-windows`——测试测到的就是本轮构建。
- 7 项跳过逐项原因（均为环境门控，非本包引入）：
  6 项 `MarketIndexContractTests.*` + 1 项 `MarketE2ETests.端到端_拉索引_安装_校验_卸载`——本机未设
  `LITEMONITOR_PLUGIN_REPO` / `LITEMONITOR_E2E=1`（CI 中由 `tests/fixtures/plugin-repo` 夹具驱动真实执行，跳过上限 1）。
- 定位器测试 12/12 通过已含在上述总数内。
- **未执行项**：`LiteMonitor.Updater.Tests`（73 例）、`LiteMonitor.GpuNonperformance.Tests`（19 例）与 Release 配置本次未跑——
  本包未触及对应工程与代码路径，完整门禁仍以 CI（Debug+Release 三程序集）为准；本报告不声明完整发布门禁通过。
- 机器环境：Windows 11 专业版 10.0.26300（Build 26300）；AMD Ryzen 7 5800X；63.9 GB；.NET SDK 8.0.426。
- 完整证据骨架（脚本生成）：本机 `.tmp/e08a/evidence-e08a.md`（`.tmp/` 不入库）；`full-main.trx`（3.4 MB）留存本机同目录。

## 5. 证据模板与收集工具使用说明

### 5.1 `docs/verification/templates/evidence-template.md`

八段式字段：①源 SHA/dirty 补丁摘要（`git status --porcelain` 逐行 + `git diff HEAD` 输出 SHA-256）；
②配置（含输出路径覆盖与环境变量实际取值）；③产物摘要（测试程序集/主程序/Host/Contracts 的 SHA-256 与路径，
并要求与 TRX 内 `[I2HostBinaries]` 证据行对账）；④依赖锁定情况（本仓库未启用 packages.lock.json，
以各工程 `obj/project.assets.json` 为准）；⑤执行命令与退出码（原样记录）；⑥TRX 发现/执行/通过/失败/跳过计数
+ 跳过与失败逐项原因表 + 未执行项说明；⑦机器环境；⑧人工记录与解释。
标注约定：机器可推导字段由脚本生成，`【人工】`字段（跳过原因、失败处置、解释）必须手写。

### 5.2 `scripts/collect-evidence.ps1`

- 用法：`pwsh -NoProfile -File scripts/collect-evidence.ps1 -RepoRoot . -Configuration Debug -OutFile .tmp/evidence-e08a.md -TrxRoot <TRX 目录>`
  （可选 `-SkipAssetsJson`、`-HostExe`）。默认输出 `.tmp\evidence-<时间戳>.md`。
- 行为边界：**只收集**（git 只读命令 + 文件哈希 + TRX 解析），除 `-OutFile` 外不写任何内容；不上传、不改 CI、不改产品源码。
- TRX 口径：表内"跳过"按**逐用例 outcome=NotExecuted 统计**，并在输出中注明 TRX `notExecuted` 计数器可能为 0
  的坑（本次全量运行即为例证：计数器 0，实际跳过 7）；每个跳过用例单独列出待人工填写原因。
- 已实测：对本机 `.tmp/e08a/full-results/` 生成骨架成功，产物摘要/依赖摘要/TRX 计数与手工核对一致；
  部分字段缺失（如未构建的 Updater/GPU 测试 dll）时退出码 2 并给出警告，报告中须注明缺失原因。
- 运行时要求：PowerShell 7（pwsh）——脚本与仓库既有脚本一致采用 UTF-8 无 BOM，Windows PowerShell 5.1 会按 ANSI 误读中文。

## 6. 建议纳入 MUST_EXECUTE_TEST_CLASS 的测试类（仅建议，不改 CI 阈值）

依据：`.github/workflows/assert-test-counts.ps1` 按 TRX `testName` 含 `.<类名>.` 匹配、要求全部执行且 0 跳过。
以下类均无条件门控（不依赖 `LITEMONITOR_PLUGIN_REPO`/`LITEMONITOR_E2E`），在 CI Windows runner 上已随全量真实执行，
用例数即当前发现数。**阈值上调（`MUST_EXECUTE_TEST_CLASS_MIN_COUNT` 与 `MIN_TEST_TOTAL`）由集成人随实际发现数统一处理，本包未改任何 CI 文件。**

| 分类 | 测试类 | 用例数 | 纳入理由 |
|---|---|---|---|
| 回归包（R1～R9） | `ReviewFix20261026Tests` | 12 | R1 策略切换真实回调、R3 目录单飞/在途合并、R4 跨帧颜色、R5 泊出态退出、R6 同级 Z 序、R7 含凭据克隆比对、R8 有界窗口——修复包的真实接线回归，跳过即失守 |
| 回归包（R2/R9）+ 进程故障链 | `I2HostSupervisionTests` | 9 | FailFast/采样阻塞/心跳失联/crash-loop 暂停/Job kill-on-close/百次启停无孤儿 + R2 协议异常收口、R9 序号口径；进程故障链的主证据 |
| 进程故障链 | `I2PipeProtocolTests` | 16 | 合法握手后的协议异常矩阵与 IPC 语义（R2 的协议层证据） |
| 快照语义 | `I2SnapshotAtomicityTests` | 5 | 批次原子发布/不混代次——E01 接线前的快照合同基线 |
| 配置保存真实入口 | `SettingsProjectionTests` | 6 | 不可变投影比较（保存链差异计算的真实入口） |
| 配置保存真实入口 | `SettingsModuleServicesTests` | 9 | 选择性应用安全子集 + 模块服务（R7/E03 保存编排的现有真实测试） |

可选（下批再议）：`I2HostSelftestTests`（3，Host 自测冒烟）、`SettingsRecoveryTests`（4，备份恢复）、
`BatchD3SettingsBackupSanitizeTests`（4，备份脱敏）。合计若采纳前 6 类，新增钉死用例 57 条。

## 7. 行尾空白与噪音盘点（只列不改，改动随相关批次走）

全仓库 git 跟踪文件扫描（UTF-8 文本，排除二进制；区分 CRLF，只统计行尾空格/Tab）：
**96 个文件、1579 行**。完整机读清单：`docs/verification/baseline/e08a-20261026/whitespace-inventory.json`。

按目录汇总：`src/UI` 39 文件/946 行（大头，含审查点名的 LiteUI）、`src/System` 20/284、`src/Core` 11/186、
`src/ThemeEditor` 2/49、`Properties` 2/42、`docs` 13/31、`src/Plugins` 5/29、`resources` 2/7、其余 4。

Top 清单（完整 96 行见附录 A）：

| 文件 | 行尾空白行数 |
|---|---|
| src/UI/Controls/LiteUI.cs | 158 |
| src/System/WebServer/WebPageContent.cs | 63 |
| src/UI/Settings/MonitorPage.cs | 60 |
| src/UI/Controls/MonitorControls.cs | 59 |
| src/UI/Settings/PluginPage.cs | 53 |
| src/Core/Settings.cs | 43 |
| src/UI/Settings/TaskbarPage.cs | 43 |
| src/UI/SettingsForm.cs | 42 |
| src/UI/MenuManager.cs | 36 |
| src/UI/UIController.cs | 35 |
| src/UI/UpdateDialog.cs | 35 |
| src/Core/SettingsHelper.cs | 30 |
| src/System/HardwareMonitor.cs | 29 |
| src/UI/Controls/LiteTreeView.cs | 29 |
| Properties/Resources.resx | 27 |
| 其余 80 个文件 | 各 ≤27，见附录 A |

配套噪音项（同属"只列不改"）：

- Markdown 文件末尾多余空行 7 处：`docs/reviews/2026-10-24-independent-acceptance-….md`、
  `docs/reviews/2026-10-24-remediation-code-review-gpu-metrics.md`、`docs/superpowers/plans/2026-10-20-gpu-multi-card.md`、
  `docs/superpowers/plans/2026-10-21-appearance-a17-release-pressure.md`、
  `docs/superpowers/plans/2026-10-22-v3-s2-web-session-protocol-contract.md`、
  `docs/verification/baseline/perf-baseline-20261026-0345.md`（2 行）、`tests/LiteMonitor.PerformanceProbe/README.md`。
- 信息项（不列入本次清理建议）：841 个跟踪文件为 CRLF 行尾（含全部 .cs/.md），属仓库级行尾策略问题，
  与行尾空白清理分开处理，避免一次全量重排版。

## 8. 遗留事项

1. **CI 接线归后续批次**：`MUST_EXECUTE_TEST_CLASS`/阈值未改（§6 仅建议）；证据模板未接入门禁；
   CI/Release 的 TRX 收集与 `collect-evidence.ps1` 的对接由集成人后续收口。
2. **阈值口径**：CI `MIN_TEST_TOTAL=2504` 随新增用例（本包 +12）与后续批次增长，由集成人按实际发现数统一上调。
3. **产物确定性边界**：apphost exe/deps/runtimeconfig 纳入了硬校验，其跨目录确定性已实测成立；
   若未来 SDK/打包方式变化导致字节漂移，需复核 `I2HostBinaries.ManifestFileNames`（单一修改点）。
4. **perf-baseline.ps1**（`-p:BaseOutputPath=bin/perf-run/`）当前只构建不跑 I2 测试，不本包处理；
   E06 性能批次若在自定义输出目录下跑 I2，须按 §2.2 设置 `LITEMONITOR_HOST_EXE`。
5. **严格失败的取舍**：按派工单要求"清单不可验证即失败"，无绕过开关；如后续出现合法的跨构建 A/B 对比需求，
   建议另立显式开关并记录，而不是放松现有校验。
6. **未执行项**：Updater/GPU 测试与 Release 配置本次未跑（§4）；F 系列目标硬件项维持待验。

## 附录 A：行尾空白完整清单（96 文件/1579 行）

| 文件 | 行数 |
|---|---|
| src/UI/Controls/LiteUI.cs | 158 |
| src/System/WebServer/WebPageContent.cs | 63 |
| src/UI/Settings/MonitorPage.cs | 60 |
| src/UI/Controls/MonitorControls.cs | 59 |
| src/UI/Settings/PluginPage.cs | 53 |
| src/Core/Settings.cs | 43 |
| src/UI/Settings/TaskbarPage.cs | 43 |
| src/UI/SettingsForm.cs | 42 |
| src/UI/MenuManager.cs | 36 |
| src/UI/UIController.cs | 35 |
| src/UI/UpdateDialog.cs | 35 |
| src/Core/SettingsHelper.cs | 30 |
| src/System/HardwareMonitor.cs | 29 |
| src/UI/Controls/LiteTreeView.cs | 29 |
| Properties/Resources.resx | 27 |
| src/Core/MetricUtils.cs | 27 |
| src/ThemeEditor/ThemeEditorForm.resx | 27 |
| src/UI/MainForm_Transparent.resx | 27 |
| src/UI/SpeedTestForm.resx | 27 |
| src/UI/TaskbarForm.resx | 27 |
| src/System/HardwareServices/SensorMap.cs | 26 |
| src/System/HardwareServices/ComponentProcessor.cs | 24 |
| src/System/HardwareServices/DriverInstaller.cs | 24 |
| src/UI/HardwareInfoForm.cs | 24 |
| src/Plugins/PluginExecutor.cs | 23 |
| src/ThemeEditor/ThemeEditorForm.cs | 22 |
| src/UI/Helpers/MenuMonitorHelper.cs | 21 |
| src/Core/Actions/AppActions.cs | 20 |
| src/Core/MetricItem.cs | 20 |
| src/UI/Settings/MainPanelPage.cs | 20 |
| src/UI/Helpers/LiteTooltipForm.cs | 18 |
| src/UI/Settings/SystemHardwarPage.cs | 18 |
| src/System/UpdateChecker.cs | 17 |
| src/UI/Helpers/TaskbarBizHelper.cs | 17 |
| src/UI/Helpers/TaskbarTooltipHelper.cs | 17 |
| src/UI/HorizontalLayout.cs | 17 |
| src/Core/NetworkSpeedTester.cs | 16 |
| src/System/HardwareServices/HardwareValueProvider.cs | 16 |
| src/UI/UILayout.cs | 16 |
| src/UI/UpdateDialog.Designer.cs | 16 |
| Properties/Resources.Designer.cs | 15 |
| src/UI/MainForm_Transparent.cs | 15 |
| src/System/HardwareServices/FanMapper.cs | 14 |
| src/UI/Settings/SettingsUIBuilder.cs | 14 |
| src/Core/Actions/SettingsChanger.cs | 13 |
| src/System/InfoService/InfoService.cs | 12 |
| src/Core/MetricLabelResolver.cs | 11 |
| src/UI/TaskbarForm.cs | 11 |
| src/UI/UIRenderer.cs | 11 |
| src/System/HardwareServices/PerformanceCounterManager.cs | 10 |
| src/System/Program.cs | 10 |
| src/UI/CleanMemoryForm.cs | 10 |
| src/UI/Helpers/SystemActions.cs | 10 |
| src/UI/Settings/ThresholdPage.cs | 9 |
| src/UI/SpeedTestForm.cs | 9 |
| src/UI/Helpers/MainFormBizHelper.cs | 8 |
| src/System/HardwareServices/DiskManager.cs | 7 |
| src/System/HardwareServices/SensorMatcher.cs | 7 |
| src/UI/Helpers/TaskbarWinHelper.cs | 7 |
| src/UI/Settings/SettingsBase.cs | 7 |
| resources/plugins/PLUGIN_DEV_GUIDE.md | 6 |
| src/System/HardwareServices/BatteryService.cs | 6 |
| src/UI/TrafficHistoryForm.cs | 6 |
| src/UI/Helpers/MainFormWinHelper.cs | 5 |
| LiteMonitor.csproj | 4 |
| docs/plans/2026-10-25-verified-bugs-remediation-plan.md | 4 |
| docs/superpowers/plans/gpu-themes/2026-10-21-execution-baseline.md | 4 |
| docs/superpowers/plans/gpu-themes/2026-10-21-rendering-contracts-v1.md | 4 |
| src/Core/UIUtils.cs | 4 |
| src/System/HardwareServices/HardwareReadme.md | 4 |
| src/System/HardwareServices/HardwareRules.cs | 4 |
| src/System/HardwareServices/NetworkManager.cs | 4 |
| src/System/WebServer/LiteWebServer.cs | 4 |
| src/UI/TaskbarRenderer.cs | 4 |
| src/UI/Settings/SETTINGS_README_DEV.md | 3 |
| docs/UPSTREAM-ISSUE-VERIFICATION-TASKS-2026-10-25.md | 2 |
| docs/UPSTREAM-ISSUES-2026-10-25.md | 2 |
| docs/UPSTREAM-OPEN-BUGS-PERFORMANCE-2026-10-25.md | 2 |
| docs/design/gpu-themes/visual-polish-round2.md | 2 |
| docs/design/gpu-themes/visual-spec.md | 2 |
| docs/reviews/2026-10-23-full-project-regression-and-upstream-review.md | 2 |
| docs/superpowers/plans/2026-10-20-appearance-a11-web.md | 2 |
| docs/superpowers/plans/2026-10-20-gpu-per-card-group-titles.md | 2 |
| docs/superpowers/plans/gpu-themes/2026-10-21-presentation-capture-adr.md | 2 |
| src/Plugins/Native/CityCodeResolver.cs | 2 |
| src/Plugins/PluginMonitorSyncService.cs | 2 |
| src/System/HardwareServices/HardwareScanner.cs | 2 |
| src/UI/AboutForm.cs | 2 |
| app.manifest | 1 |
| docs/reviews/2026-10-23-remediation-plan-review-r2.md | 1 |
| resources/plugins/GitHub.json | 1 |
| src/Core/Actions/WebActions.cs | 1 |
| src/Core/LanguageManager.cs | 1 |
| src/Plugins/Native/CryptoNative.cs | 1 |
| src/Plugins/PluginModels.cs | 1 |
| src/System/HardwareServices/SystemOptimizer.cs | 1 |