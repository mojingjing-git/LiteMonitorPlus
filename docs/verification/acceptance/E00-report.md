# E00：回归包独立复验与启动基线（P-E00）

- 日期：2026-10-26。
- 执行分支：`pkg/E00-verify`（独立工作树 `.worktrees/e00-verify`，与主会话隔离）。
- 复验对象：修复包 `5ef523b`（R1～R9，19 例专项回归）。
- 独立性声明：本报告**不采信**提交说明与审查报告修复记录注记中的"已修复"表述；R1～R9 每项的结论均来自本次独立构建与运行的实际结果（TRX 口径）。发现的未覆盖路径只补针对性测试，未做无边界重构。
- 依据：[完善计划 §2/§4](../../plans/2026-10-26-post-review-engineering-improvement-plan.md)、[审查报告（含修复记录注记）](../../reviews/2026-10-26-post-plan-full-review.md)、[派工单 P-E00](../../plans/dispatch/P-E00.md)。

## 1. 固定基线

| 项 | 值 |
|---|---|
| 源 SHA（复验起点） | `18e63c9236b8d1ecb6388c11b738399e84ccc980`（master：`5ef523b` 修复 + `4a529dc` 归档 + `098020a` 派工 + `18e63c9` E05） |
| 补丁 SHA（R1～R9） | `5ef523ba4a5f578e4a26c6e976d19669335e18e0`（12 文件，+580/−58） |
| 审查报告归档 | `4a529dc2afa642d7b737b902160fb0430b3abcd8` |
| 本分支新增 | R5/R7 两个测试接缝 + `tests/LiteMonitor.Tests/E00Verification20261026Tests.cs`（22 例）+ 本报告 |
| .NET SDK | 8.0.426 |
| 主要依赖 | LibreHardwareMonitorLib 0.9.6（PackageReference 固定版本；未启用 lock 文件，NuGet 共享缓存 `G:\TOOL\LiteMonitor-fork\.nuget-packages`）、xunit 2.9.2 / Microsoft.NET.Test.Sdk 17.11.1 |
| 配置 | Debug、net8.0-windows、x64、PerMonitorV2（`dotnet build LiteMonitor.sln -c Debug` 0 错误） |
| 构建产物 SHA-256（Debug，含两个零行为测试接缝） | `LiteMonitor++.dll` `d0df701f950d5b7c64e7b1c05ca87fba6550f7a93306eac32b39a9d7f5b07250`；`LiteMonitor++.exe` `10b145cdfd6ae1dc8bc9dec3af31cc9d36d5b7ab0e3b7dbf2c2b593b7a8221d1` |
| 产物 SHA-256（不受接缝影响） | `LiteMonitor.HardwareHost.dll` `8c40bd19c61c636d19198c7db249a0f33ee0fa427316980d0007d7cd18a00fce`；`LiteMonitor.HardwareHost.exe` `f235914f24427a3cc27a43933a753ac590a31e3d902c935da2cae6465347b301`；`LiteMonitor.Contracts.dll` `2489ad2a7b8f022c02632b6bb074915d3990797b921156e8d137bd2bca6b4c15` |
| 测试程序集 | `LiteMonitor.Tests.dll`（net8.0-windows）`2b3da90856be1bafa2fd56f30bf1c22ac86ea6b417d7638f1b162613c68c9f33` |
| 架构棘轮 | `scripts/architecture-assert.ps1` → A 组 8/8 + C 组 4/4，失败 0（接缝未移动任何棘轮指标） |

主工作树的两份既有 dirty 文档与 `.cua-helper/` 未纳入本分支（完善计划 §2.8）。

## 2. 测试接缝（本分支唯一的产品代码改动，均零行为变化）

| 接缝 | 文件 | 内容 | 行为零变化的理由 |
|---|---|---|---|
| R5 | `src/UI/Helpers/MainFormBizHelper.cs` | 新增 `internal MainFormBizHelper(Form, Settings, MainFormWinHelper)` 构造（委托既有构造、`_ui` 传 `null!`） | 只加注入口；状态迁移路径（CheckAutoHide/RestorePos/DisableAutoHide/RestoreFromParkIfParked）不触碰 `_ui`，需要 UIController 的路径（RebuildMenus/布局切换/设置窗）不经此构造 |
| R7 | `src/Core/Actions/AppActions.cs` | `ApplySettingsSelectively` 的"差异 + 凭据比对 → 计划"前缀原样抽为 `internal ResolveSaveApplyPlan(before, cfg)`，原方法改调它 | 语句逐字搬移，无逻辑改动；`ApplySettingsSelectively` 对外行为与返回值不变 |

其余文件零改动（`git diff --stat`：两文件 +25/−3）。

## 3. R1～R9 逐项复验证据

测试口径：`dotnet test --logger trx`；TRX 逐用例 outcome 解释（notExecuted 计数器恒 0，跳过按 NotExecuted 用例数计，同既有口径）。证据文件在 `.tmp/e00/`（构建/测试日志与 TRX），TRX 同时在 `tests/LiteMonitor.Tests/TestResults/`。

### R1 · 控制器切换（P1）——复验通过

- **修复内容核对（源码）**：`HardwareMonitor.ApplyControllerProbeMode` 按目标模式落状态（`ControllerTargetEnabled` 纯映射）、verify 读真实 `IsControllerEnabled`、HotSwap latch 显式 Off 放行（`src/System/HardwareMonitor.cs`）。
- **本次运行证据**（`ReviewFix20261026Tests` 3 例 + 新增 `E00Verification20261026Tests` 3 例，全绿）：
  1. 目标模式→启用状态纯映射（6 组 InlineData）；
  2. 政策级回调钉子：apply/verify 收到的一定是 target；
  3. latch 源码护栏（Off 例外 + 按目标 apply + verify 读真实状态）；
  4. **新增** 政策序列 On→Off→On：每次 apply/verify 收到目标、Off→Off 重入幂等（不触发 apply）、校验拒绝时回滚到原模式 + 降级态 + TryRevert 恰好一次（回退有界）；
  5. **新增** 未 Open 的真实 LHM `Computer` 探针复现（原探针等价物）：按修复接线走 On→Off→On，`IsControllerEnabled` 随目标翻转（true→false→true），Off 后 `MarkOpened` 落 `Disabled`——原探针坏状态 `controller-off: applied=True mode=Off enabled=True` 不再可复现；运行态（State）与设备开关位一致。
- **剩余边界**：`HardwareMonitor.ApplyControllerProbeMode` 端到端需要真实硬件构造（构造即 Open），单测不可达；真机开关切换、设备释放（HID 句柄关闭）、#245 USB 冲突复现 → **待实机清单**（§7）。

### R2 · 监督器会话收口（P1）——复验通过

- **修复内容核对（源码）**：`RunSingleSessionAsync` 把 endpoint 创建/SpawnHost/会话全程纳入收口；一切未收口异常转 `session-error:*` 失败会话；finally 确认子进程终止；`spawn-error:*` 失败会话；Host 侧 `--fault protocol-violation` 故障注入。
- **本次运行证据**（I2HostSupervisionTests 1 例 + 新增 1 例，全绿）：
  1. 协议异常路径（真实子进程）：合法握手后非法帧 → 子进程回收 → 退避重试 → 3 败 crash-loop 暂停（§6.2 有界行为），无遗留活子进程（`HostProcessCount <= 1`）；
  2. **新增** SpawnHost 缺文件路径（`HostExePathOverride` 指向不存在文件）：3 次失败会话全部收口为 `spawn-error:*`、监督循环不退出、无 Ready 误报、退避确实发生（状态轨迹含 `backoff-*`）、3 败后 `crash-loop-paused`、SpawnCount/SessionCount 为 0（无任何子进程被创建）、Dispose 干净。
- **剩余边界**：无（协议异常与启动失败两条路径均已自动化）。

### R3 · 目录查询不再阻塞调用线程（P1）——复验通过

- **修复内容核对（源码）**：`HardwareClientAdapter.GetCatalogAsync` 改线程池单飞构建（`CatalogSnapshotCache`），Dispose 时在飞构建自然完成、新调用被拒。
- **本次运行证据**（`ReviewFix20261026Tests` 3 例 + 新增 1 例，全绿）：
  1. 构建慢（400ms）时 `GetAsync` 返回 <150ms（调用线程不等锁）；
  2. 在途合并（构建期间新请求共享同一 Task，BuildCount=1）；
  3. 完成后下一次调用触发新构建（轮询方取到最近完成目录）；
  4. **新增** 适配器关闭边界（未初始化 HardwareMonitor 空壳 + 注入空壳协调器，不触碰硬件）：Dispose 后在飞任务自然完成（空目录、不失败）、新调用 `ObjectDisposedException`。
- **剩余边界**：目录生命周期仍在消费侧（每秒轮询触发重建）——"后台化 ≠ 目录生命周期已解决"，生产者发布的目录缓存属 E01-B，按计划不在本包处理。

### R4 · 数值未变丢插件色（P2）——复验通过

- **本次运行证据**（`ReviewFix20261026Tests` 1 例 + 新增 2 例，全绿）：
  1. 同数值跨帧覆盖 0→2→0，颜色随覆盖更新与清除、文本缓存确实未刷新（原探针 `expected=2 actual=0` 不再现）；
  2. **新增** 横向布局（isHorizontal=true）缓存帧同口径（颜色更新、横向缓存文本返回）；
  3. **新增** 数值变化帧（refresh 路径）新颜色与新文本同步写入。
- **剩余边界**：无。

### R5 · RestorePos 泊出态残留（P2）——复验通过（本次补真实状态迁移测试）

- **修复内容核对（源码）**：`ExitParkedState()` 四字段一体复位（`_isHidden`/`_dock`/`_parkedArea`/`_parkedScreenDevice`），RestorePos 复用。
- **本次运行证据**：
  1. `ReviewFix20261026Tests` 源码护栏（ExitParkedState 定义与复用），全绿；
  2. **新增（本包核心交付）** 真实 `MainFormBizHelper` 状态迁移测试（经 internal 构造接缝，免 UIController，不启动完整采集器）：真实窗体 + 真实主屏 + 250ms Timer 真实 tick 驱动——泊出（光标远离 + 贴右缘 → `IsHidden=true`、窗体挪到泊出坐标）→ `RestorePos`（`IsHidden=false`、位置回到保存点）→ **重新泊出成功**（R5 回归核心：旧缺陷下 `_parkedArea` 残留 + dock=None ⇒ 纯决策恒 None，永久卡死；修复后必须能再次泊出）→ 关闭自动隐藏（恢复泊出前位置、持续 tick 保持可见）。
- **测试副作用披露**：该用例为触发泊出判定会短暂移动真实光标（保存并在 finally 恢复）；窗口为测试自建，不操纵产品窗体。
- **剩余边界**：多屏对角布局下的泊出屏记忆、看门狗拉回路径仍属 GUI/多屏实机验收（§6 清单）。

### R6 · 同级 Z 序漏判（P2）——复验通过

- **本次运行证据**（`ReviewFix20261026Tests` 1 例 + 新增 2 例，全绿）：
  1. 真实 Win32 三窗链（可见遮挡 → 隐藏占位 → 面板）：修复后判覆盖；销毁唯一遮挡后不再判（原探针 `detectedCovered=False` 不再现）；
  2. **新增** 多层隐藏（可见遮挡 → 隐藏×2 → 面板，≥2 层中间隐藏窗）仍判覆盖；
  3. **新增** 70 层隐藏前驱（> MaxSiblingHops=64）：有界返回 false 且 <1s 完成（不拖死每秒轮询调用方）。
- **剩余边界**：真实 Explorer 任务栏不参与测试（屏外测试窗口，结束后销毁）；更晚出现的合成层低频巡检时间维度未做真实驱动更新实验（与审查报告同口径，维持待实机）。

### R7 · 口令幻影差异（P2）——复验通过，并发现 F1（见 §4）

- **修复内容核对（源码）**：`WebServerPassword` 入 ComputeDiff 运行时黑名单；`ResolveApplyPlan(diffs, credentialChanged)` 显式凭据比对；`ApplySettingsSelectively` 在快照外比对明文。
- **本次运行证据**（`ReviewFix20261026Tests` 2 例 + 新增 5 例，全绿）：
  1. 含口令设置 CloneForDiff 零差异；credentialChanged 单独触发 Web 计划；
  2. **新增** 完整保存编排四态（复刻 SettingsForm 链 `before=CloneForDiff → Merge(live,draft) → ResolveSaveApplyPlan`，决策前缀为真实产品代码）：
     - **有内存口令 + 无改动保存**：零差异、计划为零（不触发 Web、不重建界面）；口令仍可校验（有效哈希经 Merge 回填、指纹不变）——原探针 `clone-diff-with-password: count=1 fields=WebServerPassword` 幻影在编排层不复现；
     - **仅持久化哈希 + 无改动保存**：克隆保留有效哈希，零差异零计划；
     - **真实改口令**：明文差异被黑名单挡住（不再入 diffs），哈希差异 + 显式凭据比对双路径触发 Web 应用，`VerifyWebPassword("new-pass")` 通过 / 旧口令失效；
     - **无改动保存**：零差异零计划；
     - **（补充态）清除口令**：哈希/盐差异路径接住凭据清除（拿明文比较发现不了哈希被清——R03 口径），Web 应用触发。
- **剩余边界**：`ApplySettingsSelectively` 后缀（MainForm/UIController 副作用体）不可单测——决策前缀由接缝覆盖，后缀行为由 §6 GUI 清单覆盖；这与派工单允许的口径一致。

### R8 · 延迟统计无界（P2）——复验通过

- **本次运行证据**（`ReviewFix20261026Tests` 1 例 + 新增 3 例，全绿）：
  1. 100 次写入容量 8 窗口 → Count=8 且按插入序保留最新；
  2. **新增** 容量 0 钳制为 1（保留最新）；
  3. **新增** 未满窗口按插入序返回部分样本（容量边界前的追加）；
  4. **新增** `DefaultCapacity=512` 常量钉住（防误改 A/B 指标口径）。
- **剩余边界**：真实 Host 长跑（数日级内存占用）未做，维持审查报告"静态确认 + 单元有界证明"的证据级别。

### R9 · 心跳误计缺口（P2）——复验通过，并记录 F2（见 §4）

- **修复内容核对（源码）**：帧序号在 `OnMessage` 统一入口跟踪；批次缺口按 BatchId 另行统计（`SnapshotBatchGaps`）；`OnSessionClosed` 重置帧序号基准（`_lastSequence = -1`）。
- **本次运行证据**（I2HostSupervisionTests 1 例（真实 Host，心跳 120ms 穿插下两类缺口均为 0）+ 新增 4 例（合成信封直灌接收入口））：
  1. **新增** 帧序号缺口单独计数：心跳 seq 1→5（丢 3 帧）⇒ SequenceGaps=3、SnapshotBatchGaps=0、SnapshotCount=0；
  2. **新增** 批次缺口单独计数：帧连续（seq 10→11）而批次 1→4（槽位合并形态）⇒ SnapshotBatchGaps=2、SequenceGaps=0；
  3. **新增** 会话切换帧序号重基准化：seq 1,2 → 会话关闭 → seq 5 ⇒ 缺口为 0（未重置则误计 2）；
  4. **新增** 会话切换批次重新起算：batch 1 → 会话关闭 → batch 1 ⇒ 无幻影批次缺口。
- **剩余边界**：真实网络丢包/跨会话缺口的全链路实验未做（与审查报告同口径）。

## 4. 本次新发现（不属 R1～R9 回归，只记录不修——产品改动超出本包边界）

### F1 · WebServerPasswordHash/Salt 未归入 WebFields 分类表 ⇒ 凭据变更触发整组界面重建（P3，既有缺口）

`SettingsChanger.WebFields = { WebServerEnabled, WebServerPort, WebServerPassword, WebServerPasswordSalt, WebServerTrustProxyHeaders }` **不含 `WebServerPasswordHash`** ⇒ `Classify("WebServerPasswordHash")` 落入 Other ⇒ `ResolveApplyPlan` 的 default 分支置 `UiGroup=true`。因此"只改口令/清除口令"的保存会同时触发整组界面重建（ApplyLanguage/ApplyWindowAttributes/ApplyThemeAndLayout/ApplyMonitorLayout/ApplyTaskbarStyle/ApplyVisibility），影响面大于必要。R7 幻影修复未引入此行为（修复前口令差异同样落 Other），属 E03"字段归属表"的既有工作范围。已由 `R7_编排_真实改口令_*` 与 `R7_编排_清除口令_*` 钉住现状并在断言注明"E03 修正后翻转"。

### F2 · 会话切换只重置帧序号基准，不重置批次基准（P3，5ef523b 引入特性的口径缝隙）

`PipeHardwareClient.OnSessionClosed` 显式重置 `_lastSequence = -1`，但 `_lastBatchId` 不重置。当前 Host 重启语义下批次号回退到 1，`batchId > lastBatch+1` 恒假 ⇒ **无幻影缺口**（已由测试钉住）；但新会话的批次号低于上一会话高水位期间，真实丢批**少计**（计数器把两代会话的批次轴混在一起），直到新会话批次号追平高水位。帧序号轴经重置无此问题。若未来 Host 实现"跨会话延续批次号"（会话接管），现口径退化为与帧序号一致的少计方向，仍不误报。建议 E01/E02 快照合同落地时把批次基准纳入会话切换重置（一行改动），本包不做产品改动。

## 5. 实际门禁口径（发现/执行/通过/失败/跳过）

命令均为 `dotnet test <project> -c Debug --no-build --logger trx`（串行执行）：

| 测试工程 | 发现(total) | 执行(executed) | 通过 | 失败 | 跳过(NotExecuted 逐例) | TRX |
|---|---|---|---|---|---|---|
| LiteMonitor.Tests | 2467 | 2460 | 2460 | 0 | 7 | `e00-full-main.trx` |
| LiteMonitor.GpuNonperformance.Tests | 19 | 19 | 19 | 0 | 0 | `e00-full-gpu.trx` |
| LiteMonitor.Updater.Tests | 73 | 73 | 73 | 0 | 0 | `e00-full-updater.trx` |
| **合计** | **2559** | **2552** | **2552** | **0** | **7** | |

其中本次新增 22 例（`E00Verification20261026Tests`）与修复包既有 19 例专项（`ReviewFix20261026Tests`）+ I2 监督 7 例（含 R2/R9 新增）均单独复跑确认全绿（`e00-new-tests.trx`、`e00-reviewfix-i2.trx`）。

7 个跳过的逐项原因（TRX outcome=NotExecuted，需要完整 CI 市场条件 `LITEMONITOR_PLUGIN_REPO`）：

- `MarketIndexContractTests` 6 例：索引/目录/哈希/大小/下载域/重名/安装校验契约，需要真实插件仓库环境变量；
- `MarketE2ETests.端到端_拉索引_安装_校验_卸载` 1 例：同上。

**门禁结论**：本机 Debug 全绿（0 失败）。按既有口径，"本地测试退出码 0 + 7 项合法跳过"**不等于**完整发布门禁通过——Release 构建、完整 CI 市场/插件仓库条件、真实驱动/目标设备场景未在本机闭合，均如实留待（§6/§7）。总数演变：修复包时点发现 2440 → `18e63c9`（E05，5 例接线护栏）2445 → 本包 +22 = 2467。

架构棘轮 12/12（接缝不移动指标）。

## 6. GUI 验收清单（待用户执行；自动化不可替代的交互/多屏/真实服务项）

| # | 场景 | 期望 | 关联 |
|---|---|---|---|
| G1 | 设置**空保存**：打开设置不做任何修改点"应用/保存" | 无闪烁、无 Web 重启（网页服务不断）、无硬件重建、无插件重载 | R7 |
| G2 | 设置**修改保存**（颜色/语言/皮肤） | 界面重应用，采样与网页服务不中断 | R7/计划 §7 |
| G3 | 修改 Web 端口保存 | 仅网页服务重启，界面不重建 | R7/E03 |
| G4 | 修改口令保存 | 网页会话/票据按指纹撤销，界面不整组重建（F1 修正后） | R7/F1 |
| G5 | 清除口令保存 | 局域网模式给出明确后果提示；服务按预期停止 | R7 |
| G6 | 面板显隐：托盘双击/隐藏主面板/纯任务栏模式来回切换 | 显隐正确、任务栏模式切换无残留窗口 | — |
| G7 | 自动隐藏：贴边泊出、光标移开收起、边缘弹出；"关闭自动隐藏"后保持可见 | 泊出/弹出正常；关闭后不再自动收起 | R5（自动化已覆盖核心迁移，GUI 复核手感） |
| G8 | 多屏恢复：拔插显示器/改分辨率/锁屏-RDP 后 | 面板回到可见区或按保存位置恢复；看门狗气泡至多一次 | R5 边界 |
| G9 | 控制器开关（**待目标设备**）：设置 Off/On 真机切换 | Off 后 LHM 不再构造控制器组、HID 句柄释放（可对照 #245 USB 冲突是否消失）；On/Auto 行为对照 | R1 |
| G10 | 驱动更新/USB 拔插/长跑内存 | F 系列既有待验项，不在本包重开 | — |

## 7. 待实机清单（本机环境分界，如实记录）

1. R1 真机：`ApplyControllerProbeMode` 端到端（Off 真正停止设备访问 + 设备释放核对）；需要带外部控制器的目标设备（含 #245 DAPLink 场景）。
2. R6 时间维度：更晚出现的合成层在真实任务栏上的低频巡检表现（未做真实驱动更新实验）。
3. R8 长跑：数日级延迟窗口内存与分位数表现（单元级有界已证）。
4. 完整 CI 条件：插件仓库环境变量 + 市场端到端（7 项跳过的闭合条件）。
5. Release 配置构建与测试（本次仅 Debug，同既有基线口径）。

## 8. 结论

- R1～R9 修复**逐项有本次独立运行证据**，原探针的 7 个坏状态在对应自动化路径下均不可再现；
- 派工单要求的 R5 真实 Helper 状态迁移测试与 R7 完整保存编排四态验证均已落地（经两个零行为测试接缝）；
- 新发现 F1（凭据字段归类缺口）/F2（批次基准未随会话重置）已钉住现状并单列，修复归属 E03/E01-E02，不在本包改动；
- 实际门禁口径：发现 2559 / 执行 2552 / 通过 2552 / 失败 0 / 跳过 7（原因逐项列出）；棘轮 12/12；完整发布门禁未宣称通过。
