# 2026-10-24 候选工作树整改实现独立审查报告

> **审查对象**：候选工作树 `codex/review-remediation-20261023` @ `9238bb9` 的**全部未提交改动**
> **执行方批次 = 41 个已跟踪文件修改 + 29 个新文件（共 70 项）**；未提交、未推送、未发布、未签名。
> 本报告为审查方新增的第 71 项，未触碰任何候选生产代码。
> **审查人**：独立验收方（只读审查 + 独立复跑；**未修改候选生产代码**）
> **审查范围**：整改计划 `docs/superpowers/plans/2026-10-23-independent-review-remediation-plan.md` §4–§13（F01–F08、T09）
> ＋ R3 渲染路线 `docs/superpowers/plans/2026-10-24-rendering-route-plan-r3.md` §3–§7（步 0～步 6）
> **复跑证据**：`E:\litemonitor-acceptance\acceptance-20261023T193422Z-474953d8\r3-review\`
> **审查分工**：主审独立复跑与 R3 段；F01–F03 / F04 / F05 / F06–F08 四段并行只读审查（4 名审查 agent）

---

## 0. 结论

**不可按现状合入。** 四段审查的分项结论一致：**需修改后接受**（F01–F03、F04、F05、F06–F08 均无
"推翻设计"的 Critical，但均有 P2 级必修项）；而**主审独立复跑层面存在两项阻断问题**：

1. **测试套件非确定性失败**——同机同代码，Debug 失败 1 条、Release 失败 0 条；
   三次复现分别挂在**三条不同用例**上 ⇒ 执行方「1862 / 失败 0」不可复现。
2. **验收证据链已失效**——执行方的全量 TRX 跑于 **02:06 / 02:08**，
   而承载全部 F04 持久化实现的 `src/Core/SettingsHelper.cs` 最后修改时间是 **05:29**（晚 3 小时 23 分）；
   冻结哈希 `2EBA893F…` → 当前 `B77E3E14…`。⇒ **那次「0 失败」根本没有覆盖当前工作树。**

另有 **R3 改动三（移除 legacy 实时玻璃开关）** 是一项对既有用户的**静默不可逆降级**，
且其唯一依据（步 0 实测证据目录）被 `.gitignore` 排除、不随版本走。

| 分段 | 审查结论 | Critical | Important |
|---|---|---|---|
| F01–F03（呈现历史/恢复接管/插件热加载） | 需修改后接受 | 0 | 9 |
| F04（市场安装一致性） | 需修改后接受 | 2（均为流程/隔离层） | 9 |
| F05（插件主机规范化） | 需修改后接受 | 0 | 4 |
| F06–F08 + T09（指标/股票/单位/门禁） | **可接受**（附 2 项遗留项） | 0 | 2 |
| R3 渲染路线（主审） | 需修改后接受 | 3 | 8 |
| **主审独立复跑** | **阻断** | **2** | — |
| **合计** | — | **7** | **32** |

---

## 1. 主审独立复跑（硬证据）

复跑方式：在候选工作树上原样执行 `dotnet test`（三程序集，Debug + Release 双配置），
TRX 与控制台日志落 `E:\litemonitor-acceptance\acceptance-20261023T193422Z-474953d8\r3-review\results\`。
**未改动候选生产代码；未设 `LITEMONITOR_PLUGIN_REPO`（本地口径）。**

| 配置 | 程序集 | 失败 | 通过 | 跳过 | 总计 | 退出码 |
|---|---|---|---|---|---|---|
| Debug | LiteMonitor.Tests | **1** | 1762 | 7 | 1770 | **1** |
| Debug | LiteMonitor.Updater.Tests | 0 | 73 | 0 | 73 | 0 |
| Debug | LiteMonitor.GpuNonperformance.Tests | 0 | 19 | 0 | 19 | 0 |
| Release | LiteMonitor.Tests | 0 | 1763 | 7 | 1770 | 0 |
| Release | LiteMonitor.Updater.Tests | 0 | 73 | 0 | 73 | 0 |
| Release | LiteMonitor.GpuNonperformance.Tests | 0 | 19 | 0 | 19 | 0 |

- 三程序集合计 **1862** ⇒ 与 `MIN_TEST_TOTAL: 1862` 一致，**发现数门禁会过**（执行方在这点上没夸大）。
- 跳过 7 条 = 1 条联网 E2E + 6 条 `PluginRepoFact` 市场契约用例。后 6 条在 **CI 里会真跑**
  （`ci.yml:111` / `release.yml:72` 设了 `LITEMONITOR_PLUGIN_REPO` 指向 `tests/fixtures/plugin-repo`），
  **本地口径多出的 6 条跳过不构成门禁缺陷**（已核实，不计入问题清单）。
- **同机、同代码、同测试集：Debug 失败 1 条、Release 失败 0 条 ⇒ 非确定性失败成立。**

### 1.1 三次复现，三条不同用例

| # | 运行方式 | 失败的用例 |
|---|---|---|
| 1 | Debug 全量 | `Uninstall_CannotConsumePayloadOwnedByPendingCommittedUpdate` |
| 2 | 单类 33 条 | `ConcurrentPublicSave_SerializesAfterMarketRestoration(update: True)` |
| 3 | 单类 33 条（第 3 轮） | `ExtendedJournalMetadata_IsValidatedBeforeRecovery(field: "RecoveryVersion", valueJson: "2")` |

**形态一**（`ReviewMarketPersistenceRegressionTests.cs:527`）：

```
LiteMonitor.src.Plugins.MarketRecordPersistenceException :
  The market record was not saved; its in-memory change was restored.
  at PluginMarketState.Upsert(MarketPluginRecord record)
  at PluginMarketInstallCore.CommitInstall(...)
  at ReviewMarketPersistenceRegressionTests.PersistenceScope.Commit(...)  line 661
  at ReviewMarketPersistenceRegressionTests.Uninstall_CannotConsumePayloadOwnedByPendingCommittedUpdate()  line 527
```

**形态二**（`:158` → `:676`）：

```
Assert.Equal() Failure: Collections differ
  Expected: [···, 58, 34, 49, 46, 48, ···]   // "1.0"
  Actual:   [···, 58, 34, 50, 46, 48, ···]   // "2.0"
```

形态二的语义最重：**市场记录写失败后，插件 payload 文件仍停在新版（"2.0"）而记录未更新**
—— 这正是 F04 要消除的"文件与记录脱节"。

**隔离复现**：单跑失败用例 → 通过；单跑「污染源 + 失败用例」→ 通过（3 条 Theory 全过）。
⇒ **不是单条用例污染下一条，而是集合级共享状态问题；缩小过滤条件反而让触发条件消失。**

### 1.2 根因判定（并行审查 agent 独立给出 + 主审核实）

F04 段审查 agent 用**执行方自己的 TRX** 做时间轴分析（`startTime`/`endTime` 两两区间重叠检测，
1739 条结果两两比对）⇒ **跨类重叠对 = 0，该次运行完全串行**
（`tests/LiteMonitor.Tests/xunit.runner.json:3-4` 设 `parallelizeTestCollections:false` + `parallelizeAssembly:false`）。
⇒ **排除「xUnit 集合并行污染」；真正的污染通道是进程级共享状态 + 固定文件路径：**

| 共享物 | 位置 | 谁会碰它 |
|---|---|---|
| 固定 `settings.json` / `.bak` / `.tmp` 路径（**全程序集只有一份**） | `src/Core/SettingsHelper.cs:15-17` | 约 20 个测试类；`SettingsRecoveryTests.cs:127-138` 独占锁住它并把 `GlobalBlockSave` 置真；`LiveGlassSettingMigrationTests.cs:67-77` **整体覆写**它 |
| 固定临时文件名 + 共享 tmp | `SettingsHelper.cs:481` 写、`:512` `File.Move`、`:529-536` `CleanupTempFile` 删除 | 任何紧邻的保存；残留即 "delete-pending" |
| `Settings._instance` 活单例 | `src/Core/Settings.cs:311,314-317` | `PluginMarketState.Find`、`MarketIndexContractTests.cs:125` → `PluginMarket.CanInstall` |
| `GlobalBlockSave` / `_saveBlockOwner` | `SettingsHelper.cs:22-23,49-59` | `SettingsRecoveryTests.cs:42,72`、`LiveGlassSettingMigrationTests.cs:38,45` |
| 4 个静态故障注入接缝 | `SettingsHelper.cs:228-230`、`PluginMarket.cs:927` | 任意测试类可互相覆盖 |

**关键盲点**：`MarketIndexContractTests` **不在** `"cfgfile"` 集合（`MarketIndexContractTests.cs:22` 无 `[Collection]`），
却在 CI 里被 `LITEMONITOR_PLUGIN_REPO` 显式打开（`ci.yml:111`），
而 `ci.yml:103` 的 `MUST_EXECUTE_TEST_CLASS` 恰恰就是它
⇒ **CI 里必然存在"集合外但共享同一份 settings.json 与同一个活单例"的市场测试**。

> **主审已独立核实并排除的一个假设**：`SettingsHelper` 的三个静态接缝在
> `PersistenceScope.Dispose()`（`ReviewMarketPersistenceRegressionTests.cs:726-729`）里**确实被完整还原**，
> 构造期（`:622-625`）也确实置 null ⇒ **不是"接缝泄漏"**。真正的通道是上面的固定路径 + 活单例 + 集合外测试类。

---

## 2. Critical

### C-1【阻断】非确定性失败：「1862 / 失败 0」不可复现，且流水线会随机变红

- **证据**：§1（Debug 失败 1 / Release 失败 0；三轮复现挂三条不同用例）。
- **放大因素**：`MIN_TEST_TOTAL: 1862` 与实测总数**完全相等（零余量）**；
  `MUST_EXECUTE_TEST_CLASS` 只钉了 `MarketIndexContractTests`，**没钉**本次新增的 `ReviewMarketPersistenceRegressionTests`
  ⇒ 间歇失败在下一次运行里自然消失，计数被当成新基线。
- **门禁还有一处真"假绿"入口**：`assert-test-counts.ps1:284-290` 在同一程序集有多个 TRX 时**只取最后一次**
  （按 LastWriteTime），重复只打印警告（`:319-325`）⇒ **一份失败的运行可以被同目录下更晚写入的一份通过运行覆盖**。
  CI 里靠"跑前删干净 `ci-artifacts/test-results`"规避，但本地与自托管 runner 没有这层保护。
- **必须同时做的三件事**（缺一不可）：
  1. **消除进程级共享**：把 `SettingsHelper.cs:15-17` 的三个固定路径改为**可被测试接缝覆盖**，
     让每个 `PersistenceScope` 用独立临时目录（断言一条不减，"真实独占锁住 settings.json"的验收语义仍然成立）；
     并把 `MarketIndexContractTests` 并入 `"cfgfile"` 集合（串行化，**不能替代**第 1 步）。
  2. **生产侧加固：唯一 tmp 文件名**（`settings.json.<pid>.<guid>.tmp` + `finally` 清理）。
     这同时修掉生产环境"连续两次保存，第二次因共享 tmp 处于 delete-pending 而失败、而 `Save()` 静默吞掉 ⇒ 用户改动丢失"的风险。
  3. **门禁收紧**：同一 TRX 根目录下出现多个同程序集 TRX 时改为 FAIL（或要求运行前根目录为空）；
     `MUST_EXECUTE_TEST_CLASS` 增加 `ReviewMarketPersistenceRegressionTests`（≥33 条且不得跳过）；
     新增 **flaky 台账**，任何被判为"间歇失败"的用例必须登记并限期收口，**不得用其结果抬高 `MIN_TEST_TOTAL`**。

### C-2【阻断】验收证据链失效：那次「0 失败」没有覆盖当前工作树

主审**独立核实**（不依赖任何 agent 的结论）：

| 事实 | 证据 |
|---|---|
| 执行方 Debug 全量运行 | `.review-scratch/remediation-20261023/validation/Debug/LiteMonitor.Tests/results.trx` LastWriteTime **2026-10-24 02:06:01** |
| 执行方 Release 全量运行 | 同上 Release 目录 **02:08:38** |
| **承载全部 F04 持久化实现的 `src/Core/SettingsHelper.cs` 最后修改** | **2026-10-24 05:29:00**（`Get-Item`） |
| `src/UI/Settings/RenderingRoutePage.cs` | 05:25:12（同样晚于取证） |
| `src/Rendering/Configuration/RenderingRoutePolicy.cs` | 05:12:56（同样晚于取证） |
| 市场复审报告冻结的 SettingsHelper 哈希 | `docs/reviews/2026-10-24-remediation-code-review-market.md:68` = `2EBA893F0632…` |
| **当前** SettingsHelper 哈希（主审实测） | **`B77E3E143CC5C3E4BBE29D1DE0534897D296B68929ADAE72672670DE400C33C8`** |

⇒ **「1831 / 1830 通过 / 0 失败」与「1862 / 1861 通过 / 0 失败」两次取证都不覆盖当前代码**；
且市场复审报告**只复审了 `PluginMarket.cs` / `PluginMarketUninstallCore.cs` / 测试三个文件**，
`SettingsHelper.cs` 在其冻结快照之后被改过且**未再复审**。
**必须以当前树重跑 Debug / Release 全量并重新冻结哈希、重新取证后才能谈验收。**

### C-3【阻断】R3 改动三（移除 legacy 实时玻璃开关）是对既有用户的静默不可逆降级

- **代码**：`src/Core/SettingsHelper.cs:176-186` **每次加载**都强制 `s.LiveGlassEnabled = false`；
  `src/UI/Settings/MainPanelPage.cs` 删除整张「实时玻璃」卡（开关 + 状态行 + 首次提示弹窗）；
  `src/UI/UIController.cs:300` 的 `SetLiveGlassEnabled(bool)` **已无任何调用点**（全仓 grep 仅定义处）。
- **问题**：
  1. R3 计划 §1.1 自己写明 legacy `LiquidGlass` 是「**唯一真跑通**」的路线；
     而 GPU 路线的「实时桌面背景」需要**三重开启**（`_GPU` 主题 + 「GPU 材质渲染」+ 「实时桌面背景」+ WDA 政策同意）
     ⇒ **legacy 主题用户失去该功能且没有等价替代**。
  2. 迁移**无任何用户可见告知**：不弹窗、不进状态页，README 只写"旧配置值会在加载时自动迁移为关闭"。
  3. 副作用：本会话此前交付并实测通过的 **P1-01 修复**（`src/UI/LiveGlassController.cs` 的
     `RefreshRequest()` → `RestoreIfEnabled()`）在正常 UI 路径下**再也无法被触发**（配置恒被置 false）。
     不是代码矛盾，但意味着"唯一真跑通的路"被自己关掉了。
- **建议**：至少在「渲染路线」页对 legacy 主题显式说明"legacy 实时玻璃已下线，替代路径需 `_GPU` 主题"；
  或把迁移改为**一次性 + 可回退**（写 `LiveGlassMigrationNote` 之类标记，首启提示一次），
  而不是每次加载硬置 false；或**保留开关但标注"实验性/将被 GPU 路线取代"**，避免不可逆删除。
  **这是产品决策，不是技术决策，需用户拍板。**

### C-4【F04】回滚失败被完全吞掉，可能留下"文件与记录脱节"

- `src/Plugins/PluginMarket.cs:1253-1254`：`if (!TryResolveTransactionPaths(...)) return;`
  —— **静默返回**，旧 payload 不还原、日志条目不清理；
- `src/Plugins/PluginMarket.cs:1272-1277`：`catch { }` —— 文件还原失败时**静默吞掉**，
  只留下"新 payload + 备份 + journal"三件套，用户与运维**无从知道存在一笔待对账的 pending**。
- 计划 §7 第 5 条要求"补偿失败时保留与新磁盘记录一致的新文件及 journal/backup" —— **行为是对的，但失败事实不外传**。
- 修法：`Rollback` 返回结构化结果（`compensated / payloadRestored / journalRetained`），
  由 `CommitInstall` 记入日志并映射为 UI 警告；并把"记录写失败 ⇒ 目标文件内容 == 旧内容"提升为**独立契约用例**。

---

## 3. Important（F01–F03 段）

> 依据：计划 §4（F01 按 HWND 代次累积呈现历史）/ §5（F02 明确恢复逐轮接管）/ §6（F03 热加载后对账）

| # | 问题 | 落点 |
|---|---|---|
| **A-1** | **计划 §4 步 3「跨代次回调按代次判据丢弃」完全无对应实现**：`RecordBindingState(PresentationBindingState)` 签名**不带任何代次/宿主身份**；判据 `ShouldDiscardCallback`（`RenderingTransitionCoordinator.cs:618`）**是生产死代码**（只有测试调用）；`GpuFailureReport`（`GpuFailureFallbackScheduler.cs:40-45`）不携带身份字段。**诚实评估**：审查 agent 未能构造出当前可达的生产触发路径（宿主用 UI 线程 `Timer` 同步上报，且 GPU→legacy 迁移先 `StopGpuPresentation()` 才推进代次），**今天实际污染概率很低**；风险是"计划明确要求的机制完全缺席"，一旦宿主改异步上报即立刻变成跨代次污染 | `RenderingTransitionCoordinator.cs:566`、`UIController.cs:560-577` |
| **A-2** | **F02 的失败码在生产接线被丢弃**（违反"降级不静默"）：`ScheduleExplicitCapturePolicyRecovery` 的 `out _` 把 `rearmErrorCode` 直接扔掉；而本轮刚在 `CapturePolicyCleanupLedger.cs:309-320` 细分出 `AlreadyPendingErrorCode` / `NoUnresolvedEntryErrorCode` / `RearmFailedErrorCode` ⇒ **重建被拒时用户以为"已安排恢复"，实际没有，界面/日志零反馈** | `UIController.cs:1525-1532` |
| **A-3** | **F03 `RestartInstance` 擅自扩大清理范围**（基线只做 Stop+Sync+Start，不清数据、不清缓存）：现在 `cleanup: true` → 清已发布数值 + 清步骤缓存。用户在插件页改**任意**实例参数即触发 ⇒ 面板出现"加载中"闪烁、HTTP 步骤缓存被丢弃 | `PluginManager.cs:390` → `:300` → `:212` |
| **A-4** | **F03 `ArmRuntime` 中途失败会让后续 runtime「已登记但从未武装」**：`:303-309` 的 `FinishChanges` 循环无 `try/finally`，`ArmRuntime` 的 catch 只退役当前这条并 throw ⇒ 后续 runtime 留在 `_runtimes`/`_configSnapshots` 但**没有计时器**；跳过判据（`:244-246`）**不检查 `_timers`** ⇒ 永久静默停摆 | `PluginManager.cs:303-309`、`:244-246` |
| **A-5** | **F03 禁用实例口径不一致**：`Reload` 清数据但**留**监控项（`:234`），`RestartInstance` **删**监控项（`:393-397`）⇒ 禁用后一次"应用设置"会出现永远空白的指标行。基线已存在，本轮更明显 | `PluginManager.cs:234` vs `:393-397` |
| **A-6** | `RenderingTransitionCoordinator.cs:441-446` 允许 `newHandleGeneration > _handleGeneration.Current` ⇒ 协调器状态会跑到真相源**前面**，此后真相源推进到同一值时被拒 ⇒ **真实新代次的历史不会被清零**（当前不可达，是给后人埋的坑） | `RenderingTransitionCoordinator.cs:441` |
| **A-7** | `CapturePolicyCleanupLedger.cs:91` 的 `_entries` 是**无锁 `List<>`**，而 ledger 以回调暴露给宿主（`UIController.cs:508,511`）却**无 owner-thread 断言** ⇒ 宿主改异步回调即退化为竞态，破坏 INV-4「同代次至多一条非终态责任」 | `CapturePolicyCleanupLedger.cs:91` |
| **A-8** | `PluginManager.cs:1` / `PluginExecutor.cs:1` / `PluginMonitorSyncService.cs:1` 相比基线**新增了 UTF-8 BOM**（`git diff` 首行 `-using System;` / `+﻿using System;`），与全仓风格不一致 | 3 个文件首行 |
| **A-9** | `PluginManager.cs:311-317` `ReleaseRuntime` 用 `catch { }` 吞掉 `CancellationTokenSource.Cancel()` 的 `AggregateException`；`ArmRuntime` 的 catch 内再调 `FinishChanges`，与外层语义重叠易误读 | `PluginManager.cs:311-317`、`:336-342` |

---

## 4. Important（F04 市场安装一致性段）

> 依据：计划 §7（F04 市场安装记录与文件提交保持一致）

| # | 问题 | 落点 |
|---|---|---|
| **B-1** | **失败原因不可识别**（违反"降级不静默"）：`MarketRecordPersistenceException` 只有一句固定文案，**不带 inner exception、不带原因码**；`PluginMarketPage.cs:440-443` 的 `DescribeError` 只特判 `PluginMarketException` ⇒ 用户分不清是"被禁写挡住"还是"settings.json 被占用"还是"序列化失败"。**本次实测失败之所以难定位，正是这个缺口** | `PluginMarketState.cs:75-79` |
| **B-2** | **卸载失败时原异常被文件恢复异常掩盖**：`catch { if (hadFile) File.Move(...); throw; }` —— 恢复失败时抛的是 `IOException`，**真正的 `MarketRecordPersistenceException` 被丢失**；"文件已删、记录仍在"的半状态无补偿 | `PluginMarketUninstallCore.cs:60-66` |
| **B-3** | **一条无法归属的 pending 条目会阻塞所有插件的安装/卸载**：`EnsureNoPendingInstall` 对 `pending == null` 或插件 id 非法也直接 throw ⇒ 别的插件的残留条目让**无关插件**全部失败，且报错文案主语与事实不符 | `PluginMarket.cs:1561-1570` |
| **B-4** | **pending 一旦进入不可自愈状态即永久封锁该插件**：要求"磁盘记录恰好等于旧记录或新记录"，否则 throw ⇒ 永远 `remaining` ⇒ **没有任何用户可见的"放弃事务"入口**（journal/backup 只能手工删） | `PluginMarket.cs:1156-1163`、`:1122-1126` |
| **B-5** | **测试断言没有钉住"失败原因"**：`Assert.Throws<MarketRecordPersistenceException>` 只区分"抛/不抛"，不区分"因为禁写"还是"因为文件锁" ⇒ 本次 flaky 无法被断言捕获（**加强方向：断言原因码，不是放宽**） | `ReviewMarketPersistenceRegressionTests.cs:41`、`:466` |
| **B-6** | `TryChangeMarketRecord` **只替换第一条**同 id 记录，其余同 id 条目原样留存并被持久化 ⇒ 磁盘上同时存在"新记录"与"陈旧同 id 记录"；卸载校验可能取到陈旧条目而**拒卸一个实际已装的插件**（基线同样是"只替换第一条"，属既有语义被原样搬入） | `SettingsHelper.cs:259-270` |
| **B-7** | **钩子位置不对称**：`BeforeSaveGateForTests` 在**锁外**、`BeforeSettingsSerializeForTests` 在**锁内** ⇒ 生产无偏差，但"市场失败恢复后通用 Save 才能序列化"这一验收语义**完全依赖内层钩子**；后续有人把它移出锁，该验收会**静默失效** | `SettingsHelper.cs:221` / `:238` |
| **B-8** | `SettingsHelper.cs:283` `settings.MarketPlugins = null!` —— 对可空编译器撒谎，该分支一旦可达，消费者会 NRE 而非编译报错 | `SettingsHelper.cs:283` |
| **B-9** | `EnsureRecovered` 的 `catch { }` 与逐笔 `catch` 把恢复失败完全静默 | `PluginMarket.cs:824-827`、`:1122-1126` |

---

## 5. Important（F05 插件主机规范化段）

> 依据：计划 §8（F05 插件主机表示统一规范化）

| # | 问题 | 落点 |
|---|---|---|
| **C-1'** | **沙箱原因码失去唯一可观测面**（LM-009 契约回归）：错误日志从 `HandleExecutionError` 搬到 `PublishExecutionErrorAsync` 时**连同 `[reason]` 前缀一起丢掉**，而被删注释本身写明这是 LM-009 契约；`PluginManager.cs:523` 新增 catch 同样只取 `ex.Message`。全仓 `src/` 内 `PluginSandboxException.Reason` **零消费者** ⇒ 命中沙箱上限时 UI 只有 `[ERROR]`、日志里没有 `LM009_*` ⇒ **无任何测试会发现** | `PluginExecutor.cs:1375`、`PluginManager.cs:523` |
| **C-2'** | **IDN 主机仍存在第二套规范形式**：`PluginHostPolicy.cs:82` 用 `parsed.IdnHost`（Punycode），`PluginExecutor.cs:1454` 的 `CanonicalizeUrl` 直接用 `uri.Host`（Unicode）⇒ 同一主机两种字符串形式 ⇒ **缓存键分裂**。计划 §8.1 要求"唯一主机规范化辅助过程"，这条没覆盖到 `CanonicalizeUrl` | `PluginHostPolicy.cs:82`、`PluginExecutor.cs:1454` |
| **C-3'** | **§8.4「在缓存查找及加入在途租约之前执行」的排序条款没有测试护栏**：被拒 URL `http://[fe80::1%12]/data` 与已缓存的 `http://[fe80::1]/data` 摘要**必然不同** ⇒ 把校验挪到缓存查找之后该断言**仍然通过**（代码顺序本身成立，但无判别力） | `ReviewPluginHostNormalizationTests.cs:132-138` |
| **C-4'** | `RequireHttpHost` 把"畸形 URL / scoped IPv6"标成 `CredentialTargetNotAllowed`（语义错位，指向错误排障方向），且**文案是英文**（仓内沙箱消息一律中文） | `PluginHostPolicy.cs:107-113` |

---

## 6. Important / Suggestions（R3 渲染路线段，主审）

| # | 级别 | 问题 | 落点 |
|---|---|---|---|
| **R-1** | Important | **`diagnostic_window_is_layered` 是只写不读的死诊断**：policy 把它追加进 `errors`，但**全仓 `src/` 内没有任何一处读取 `RenderingRouteDecision.Errors`**（grep `.Errors` 在 src 下零命中），只有测试断言它存在。R3 计划 §10.1 与 `step0-summary.md` 都写"供「渲染路线」页状态行与排障读取" —— **失实**：状态行走的是另一条路（`RenderingRoutePage.cs:198` 直接读 `MainForm.IsWindowLayered()`） | `RenderingRoutePolicy.cs:128-131` |
| **R-2** | Important | **三处注释/文档仍宣称已被实测证伪的"分层 ⇄ WDA 互斥"前提**：`MainFormWinHelper.cs:159-163`（"★ `WS_EX_LAYERED` 与 `WDA_EXCLUDEFROMCAPTURE` **互斥**…原因码 `wda_capture_policy_unsupported_on_layered_window`"）、`WindowExtendedStyle.cs:6-11`（同文，并称本类是"宿主侧**二次校验**"）⇒ 代码已按方案 A 改掉、**文档没改** ⇒ 后续维护者极可能据此把闸"修回去" | 两处 XML doc |
| **R-3** | Important | **`WindowExtendedStyle` 整个类已成死代码**：唯一调用点是本批删除的 `GpuPresentationHostController.cs:277-283`；删除后 `IsLayered`/`GetExStyle` **零生产引用**。与 R-1 合起来 ⇒ "分层诊断"这条线索两头悬空 | `WindowExtendedStyle.cs` |
| **R-4** | Important | **「渲染路线」页整页绕过 `LanguageManager.T`**：自建 `Text(zh, en)` 二元 helper（`RenderingRoutePage.cs:32-35`），全页 15 处硬编码 ⇒ **de/es/fr/ja/ko/ru/zh-tw 七种语言用户看到英文**，且同一页的状态行反而九语齐全 ⇒ **同页中英混杂** | `RenderingRoutePage.cs:32-35` 及 15 处 |
| **R-5** | Important | **删除实时玻璃 UI 后留下 3 个死 key**：`Menu.LiveGlass` / `Menu.LiveGlassStatus` / `Menu.LiveGlassHint` 在九个语言文件中仍在、**全仓零引用** | `resources/lang/*.json` |
| **R-6** | Important | **R3 改动三的唯一依据目录被 `.gitignore` 排除**：`.gitignore:17` = `/docs/reviews/evidence/` ⇒ `docs/reviews/evidence/rendering-route/gpu-live-2026-10-24/`（`step0-summary.md`、`r3-execution-summary.md`、54 个原始文件）**不进版本库**。这是既有惯例，但改动三是**不可逆产品决策**，其唯一依据恰好在这个被忽略的目录里 | `.gitignore:17` |
| **R-7** | Important | **`scripts/add-rendering-route-i18n.py` 是一次性脚本且带机器绝对路径**：`:5` 硬编码 `C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\...` ⇒ 换机器/CI 即失效。副作用：`:145` 以 `newline=None` 读 + `:170` 以 `newline=""` 写 ⇒ **九个语言文件被整体 CRLF→LF**（`git ls-files --eol`：其余工作区文件均 `w/crlf`）；因 `core.autocrlf=true` 且无 `.gitattributes`，入库内容不受影响，但工作区换行风格与全仓不一致 | 脚本 `:5`、`:145`、`:170` |
| **R-8** | Important | **步 0 六项判据中第 6 项是空转的**：`step0-summary.md` 判据 6 写"✅ 无该日志（实验配置 `LiveGlassEnabled=false`，legacy 服务未启动）" —— 实验根本没启动 legacy 服务，"没有降级日志"是必然结果，**不构成任何证据**，却被计入"6/6 全过"。同页记录 Present 节奏实测 **P50 ≈ 70.8 ms（≈14 FPS）**，而判据集里**没有帧率项** ⇒ 结论只证明"可达"，**没有证明"够流畅"**，且 §10.2 的结论句没带上它 | `step0-summary.md` 判据表 |
| R-9 | Suggestion | `README.md:91-92` 的路线名 `GPU 固定背景` / `GPU 实时桌面背景` **不是代码里任何字面量**（全仓 0 命中）；`:94` 写半角 `(GPU 材质)`，而 `zh.json:85` 实现是全角 `（GPU 材质）` | `README.md:91-94` |
| R-10 | Suggestion | `Menu.RouteStatus` 是 `Menu` 段下**第一个嵌套对象**（基线时 0 个）⇒ 打破"扁平单层"惯例；缺失时回退文案会变成 `RouteStatus.NotStarted` | `resources/lang/*.json` |
| R-11 | Suggestion | `ThemeManager.IsGpuTheme` **每次调用读一次主题文件**，托盘每次右键、设置页每次打开都按主题数 N 次读盘；且它（文件读）与 `RenderingRoutePage.ThemeRouteText()`（读 `ThemeManager.Current.Rendering`）是**两个真相源**，主题加载失败时可能出现"下拉标注 GPU / 总览标注 GDI+" | `ThemeManager.cs:471-501` |
| R-12 | Suggestion | `assert-test-counts.ps1:403` 的 `$r.Skipped -ne $r.SkippedTests.Count` 混用两个来源（counter 推导 vs 节点扫描）；`notRunnable` 结局可能同时计入 `bad` 与 `NotExecuted` ⇒ 会误报"跳过计数与明细不符" | `assert-test-counts.ps1:403` |
| R-13 | Suggestion | `LiveGlassSettingMigrationTests` 直接读写**用户真实 `settings.json`**，`Dispose` 的 catch 吞异常 ⇒ 进程被强杀会残留测试配置。**它同时也是 C-1 的污染源之一** | `LiveGlassSettingMigrationTests.cs:50-53` |
| R-14 | Suggestion | **`AppearanceSettingsProbe` 的新用例没进 CI**（只有 `scripts/run-probes.ps1` 调它；`ci.yml` 只跑 `GpuHostLifecycleProbe`）⇒「渲染路线」页真实构造 + `OnShow` 不抛这条护栏**在 CI 里没有约束力** | `ci.yml` |
| R-15 | Suggestion | 脚本与九个语言文件构成**双份翻译真相源** | — |

---

## 7. F06–F08 + T09 段（指标/股票/单位/门禁）

> 该段专职审查 agent 已回补完整结论，**四项整改实现可接受**（无 Critical），
> 但有 2 项 Important 遗留项须在验收记录中单列；主审另做交叉抽查，结论与之一致。

**主审与该段 agent 交叉核实的结论：**

- **F06（逻辑盘使用率不受物理盘缓存影响）——代码逻辑正确，但**目标键在当前产品中无任何生产者**。
  `src/System/HardwareServices/DiskManager.cs` 的 `GetBestValue` 把**逻辑盘分支整体前移到物理盘缓存之前**：
  基线是"先命中 `_cachedDiskHw` / `IsDiskScanFresh()` 就提前返回"，导致 `DISK.C.Used` 可能拿到物理盘传感器的值；
  现在 `DISK.<盘符>.Used`（`parts.Length == 3 && parts[0] == "DISK" && parts[2] == "Used"`，
  `char.IsAsciiLetter` 限定盘符）**先算 DriveInfo，再走物理盘缓存** ⇒ 逻辑盘使用率不再被物理盘缓存串扰。
  副作用面：非 `DISK.X.Used` 的键行为不变；代价是每次查询一次 DriveInfo（面板 2 fps，可接受）。
  **★ 但见 D-1：被修复的这个键今天没有生产者 ⇒ 该修复是"防御性"的，用户可见效果为零。**
- **F07（股票代码规范化）——规则链自洽。**
  `resources/plugins/Stock.json` 的 `search_stock` 步骤新增一条 `regex_replace ^us\. → us`，
  与既有的 `^\?$ → ""`、`\.[A-Z]+$ → ""` 组成三段链：
  `"us.AAPL"` → `"usAAPL"`；`"usAAPL.OQ"` → 规则 1 不匹配 → 规则 3 去掉 `.OQ` → `"usAAPL"`；
  `"sh600519.SH"` → 规则 3 → `"sh600519"` ⇒ 最终都归一到腾讯行情接口期望的 `sh/sz/us+代码` 形态。
  同时修掉了文件末尾缺换行。
- **F08（网页与桌面同一项级单位）——值与单位同源，无二次换算。**
  `src/System/WebServer/LiteWebServer.cs` 新增共享格式化路径 `WriteSnapshotItemJson`，
  并把 `item.UnitPanel` 传进 `WebSnapshotValues.ResolveValue(...)` 与
  `MetricUtils.GetDisplayUnit(key, MetricUtils.GetUnitStr(key, rawValue, UnitContext.Panel), item.UnitPanel)`。
  **主审专门核实了"是否换算两次"**：`MetricUtils.GetDisplayUnit` **只做用户格式模板替换（`{u}`）或隐藏单位，不做量级换算**，
  真正的换算发生在 `GetValueStr(key, v, false, userFormat)` 内部（按 `userFormat` 决定容量/百分比口径），
  `pct` 仍按 `rawValue` 计算 ⇒ **网页数值与单位与桌面同口径，不存在 1000 倍量级错**。
  且该文件把快照路径与实时路径**收敛到同一函数**（原先两处各写一遍），属实质改进。
- **T09 / 门禁部分——已在上文核实**：阈值只升不降（1632 → 1862）、两工作流逐字一致、
  账目可独立复算（1632 + 199 + 31 = 1862）、`RenderingTransitionStateTests` 是收紧而非放松断言。
  另核实 **T09 的 IL 识别器是真阳性检测而非换一种误报**：只认紧邻接收者（跳过 `Nop`/`Constrained`），
  字段读取 `Field.FieldType`、局部读取 `Local.LocalType`，排除 static / 非 void / 带参 `Dispose`；
  正反例齐备（字段释放=真、局部释放=真、同类型局部未释放=假、只释放其他对象=假、同名 static 方法=假）；
  `IlInspector.cs:166-245` 的局部槽解析正确（`ShortInlineVar` 1 字节 / `InlineVar` 2 字节 / `Ldloc_0..3`），
  且**生产 `src/System/UpdateChecker.cs` 未被修改**，三处 `using var` 原样保留 ⇒ 计划 §12 第 1、4 条成立。

### 7.1 F06–F08 + T09 段的 Important（该段审查 agent 结论）

| # | 问题 | 落点 |
|---|---|---|
| **D-1** | **F06 修的键在当前产品中无任何生产者 ⇒ 修复今天没有用户可见效果**：唯一调用点是 `HardwareValueProvider.cs:828,849`（`key.StartsWith("DISK")`），但全仓 grep `DISK\.` **没有任何代码构造 `DISK.<盘符>.Used`**；默认监控项只有 `DISK.Temp` / `DISK.Read` / `DISK.Write`（`SettingsHelper.cs:606,616,617`）；**上游 `a71ce75` 同样没有生产者**——"key 格式如: DISK.C.Used" 只是一句注释（`DiskManager.cs:220`）。⇒ 修复本身正确且被新用例覆盖（用假物理盘 + 真实就绪盘 + 容差比较驱动了生产 `GetBestValue`），但它是**防御性修复**，"用户可见缺陷已闭环"在现有代码上**不可证** | `DiskManager.cs:219-246` |
| **D-2** | **DASH 插件项的单位在桌面端与网页端读的是两个不同的键，且值来源不同（既存，非本次引入）**：桌面 `MetricItem.cs:139` → `MetricUtils.cs:267-272` 读 `InfoService.GetValue("DASH.<raw>.Unit")`（**带前缀**、值为**未解析**的 `output.Unit`）；网页 `LiteWebServer.cs:581-583` 读 `readInfo("<raw>.Unit")`（**去前缀**、值为 `ResolveTemplate` **已解析**文本）。随包插件的单位全是字面量 ⇒ **当前不可见**，但第三方插件的单位含模板占位符时两端显示不同；且新测试 `ReviewMetricPresentationRegressionTests.cs:280-293` **只建模了网页约定**，等于把该差异固化 | `MetricItem.cs:139`、`LiteWebServer.cs:581-583` |

### 7.2 F06–F08 + T09 段的 Suggestions

| # | 问题 |
|---|---|
| **D-3** | F06 的键匹配相对基线**收紧**（基线 `parts.Length >= 3 && char.IsLetter(parts[1][0])`；现为 `parts.Length == 3 && parts[0] == "DISK" && parts[2] == "Used"`）⇒ `"OTHER.C.Used"`、小写前缀 `"disk.c.Used"` 基线会返回真实使用率、现不再进入 DriveInfo 分支。当前无生产者 ⇒ 纯理论，但计划 §9 第 2 条"保持既有返回合同"在**可达键集**上才成立 |
| **D-4** | 网页端"未启用"分支额外套了 `GetDisplayUnit`（`LiteWebServer.cs:616-617`），**超出上游 `a71ce75`**（上游只在"有数据"分支加）；后果是未启用的 FPS 项在 `UnitPanel=""` 时单位由 `" FPS"` 变为 `""`。行为更符合"项级单位"口径且被新用例固定，但计划 §11 未显式授权此扩展 |
| **D-5** | **桌面端用平滑值 `DisplayValue`、网页端用原始 `rawValue`**（`MetricItem.cs:175` vs `LiteWebServer.cs:594`）⇒ 同一时刻两端数字可能不同。属既存差异（上游同样如此），但"网页与桌面一致"的表述**不宜扩大到数值** |
| **D-6** | `LiteWebServer.cs` 的 +187 行经完整 diff 核对为**纯重构**（抽出 `internal static WriteSnapshotItemJson`、给 `ResolveValue` 增加 `userFormat` 重载、把 `continue` 改写为等价 `return`）⇒ **未新增线程/定时器/HttpListener，未改路径拼接**（审查任务书里"新增后台线程"的前提与实际 diff 不符，据此不结论） |
| **D-7** | T09 识别器两个已知盲区（可接受）：① 接收者是属性/方法返回值时（`call get_X` → `callvirt Dispose`）紧邻指令既非字段加载也非局部加载 ⇒ 不识别（当前被断言的三个类型均为局部/字段，不受影响）；② 接收者先拷到临时局部再释放时，临时局部的 `LocalType` 仍是目标类型 ⇒ **仍能识别，不构成漏判** |
| **D-8** | `ReviewMetricPresentationRegressionTests.cs` 同时承载 F06 / F07 / F08 三组用例，与计划 §13 第 1 条"各组回归以测试类独立命名"存在张力（若 F06–F08 属同一编辑组则合规）⇒ 建议记录原因或拆分 |

- **门禁阈值只升不降**：1632 → 1862；`ci.yml` 与 `release.yml` **逐字一致**（LM-056 教训已被遵守）。
- **阈值账目可独立复算，无数字造假**：`1632 + 199 + 31 = 1862`。
  本次新增 6 个 `Review*.cs` + `HttpLifecycleTests` 静态计数实测
  `35 + 33 + 56 + 36 + 15 + 11 = 199`；R3 批次 `10 + 8 + 9 + 3 + 1 = 31` —— 与 CI 注释**逐项吻合**。
- **新增 31 条用例无恒真断言、无弱断言**；R3 计划 §5 的场景（GPU 运行中 / 已回退+原因 / 未启动 /
  实时背景激活 / helper 的 GPU·legacy·坏 JSON·缺文件 / 迁移 true→false / 5 个降级原因码各一条）**全覆盖**。
- **`RenderingTransitionStateTests` 的守卫改写是收紧而非放松**：从"必须降级"改为
  "必须不降级 + 必须带诊断码 + 必须 null 原因码"，并补了一条反向用例防止诊断码误报。
- **i18n 九语齐全且是真翻译**：新增 10 个 key 在 9 个文件中全部存在、全部被代码引用、无重复 key、JSON 合法；
  de/ja 等均为对应语言译文，无中文占位（孤儿 key 3 个见 R-5）。
- **T09 的 `HttpLifecycleTests` / `IlInspector` diff 不含任何插件热加载内容**，属 T09 自身范围。

---

## 8. 已验证正确的点（避免重复审）

1. **F01 核心语义与验收矩阵全部落地且有判别力**：单调 rank 表（显式按枚举含义排序，不依赖枚举数值）、
   同代次只前进、仅新代次清零、中间代次被拒、生产入口锁住原缺陷（**基线会失败** ⇒ 判别力成立）、
   冷启动失败不强制重建、保留新宿主原始状态、统一回退仍用原判据、首次/非受控 HandleCreated 不启动 GPU、
   关闭/重建失败状态保留。
2. **F02 修掉了基线真缺陷**：选择条件加入 `!ReclaimedByExplicitRecovery` ⇒ A→B 后再次恢复不再重新选中已接管的 A；
   登记成功后才标记接管；不改写旧审计状态；多个未解决责任只接管一条、其余保持闸门；
   无未解决条目拒绝凭空恢复；已有 pending 不叠加；测试钩子不绕过不变量。
3. **F03 对账覆盖两个方向**（运行态残留但配置禁用 ⇒ 退役；配置已启用但运行态缺失或指纹变化 ⇒ 重建）；
   组合指纹含实例参数 + 完整模板语义；稳定实例不重启；未启用的新插件保持禁用；
   退役与发布门原子化（新代次在旧代次释放后才武装）；共享写入全部纳入同一门；
   旧等待者取消不传播到共享 HTTP；清理键匹配完整实例名 + 点分命名空间；锁序一致；未新增通知路径。
4. **F04 事务语义主体正确且与计划 §7 一致**：锁序单向无死锁；"先补偿记录、再动文件"顺序正确；
   初次严格写失败不重复补偿；恢复以**磁盘记录**为权威、不借用活单例；
   恢复失败保持 pending、不误清材料；journal 扩展字段按不可信处理；卸载不再越过 pending 事务；
   `EnsureRecovered` 缓存已把"journal 是否存在"纳入命中条件；回滚的内存还原在 1/2/3 条同 id 记录、
   `originalList` 为 null、`replacement` 为 null 四种组合下均能还原原顺序与原内容；
   `TryReadPersistedMarketRecord` 的"主文件不存在且备份不存在 / 主文件不存在但备份存在 / 主文件损坏 / 主文件被独占锁"
   四种情形返回值与调用方期望**一致**（未发现"返回 true 但记录其实没落盘"的误报）。
5. **F05 主体达成**：三处 `StripPort` 口径确已收敛到唯一助手（4 份重复实现全删，
   且无任何测试反射调用被删成员）；四个新类型（`PluginHostPolicy` / `PluginKeyOwnership` /
   `PluginPublicationLease` / `PluginRuntimeSnapshot`）**全部被生产路径消费**（测试引用数为 0），不是死代码；
   §8.2「不能先剥 userinfo/端口把非法输入洗成合法声明」已真正修好；
   scoped IPv6 拒绝在 raw 与编码两种形式下都生效且不误杀路径/查询中的 `%`；
   校验严格在缓存读与在途加入之前（顺序成立，只是测试不判别，见 C-3'）；
   凭据安全面无缺陷（只记录主机名）；发布租约获取/释放配对、无泄漏、无死锁；
   `AsyncLocal` 发布上下文的传播与恢复经实测正确；
   内置插件模板未因 `EmbeddedHttpUrls` 改写而失效。
6. **R3 步 0 实验确有原始证据**：`docs/reviews/evidence/rendering-route/gpu-live-2026-10-24/` 下 54 个文件，
   含 `captureSessionsCreated=1` / `LiveCaptureRequested=true` / 两次帧缓冲采样像素差异 63.4% /
   亲和性轮询 12 s 全程 `0x11` 的原始记录 ⇒ **"GPU 实时背景在默认配置下可达"这一结论有据**（判据强度见 R-8）。
7. **WDA 政策弹窗已完整迁移**：`RenderingRoutePage.cs:126-146` 完整保留 `GpuWdaCapturePolicyAccepted`
   的首次同意弹窗与取消回退 ⇒ **没有出现"悄悄把窗口排除出截图"**。
8. **托盘后缀与设置页下拉共用同一 helper 与同一 key**，主题写回仍是纯主题名 ⇒ 无配置污染。
9. **步 0 的改动本身忠实于计划**：policy 分层分支改诊断、宿主侧二次校验移除、守卫按方案 A 改写。
10. **执行方的自审快照确实覆盖了当时的代码**：两份自审声明的 16 个 SHA-256 冻结值与当前工作树一致
    —— **但 `SettingsHelper.cs` 例外**（见 C-2）。
11. **F06 / F07 / F08 / T09 四项实现本身正确**：
    - F06 的 DriveInfo 分支确已前置到物理缓存之前，**基线缺陷机制确证**
      （`ReadDiskSensor` 只识别 `DISK.Read/Write/Temp`，对 `DISK.C.Used` 返回 null；
      基线在物理缓存新鲜期（10 s）直接 `return cachedVal`（null）⇒ DriveInfo 分支永不可达）；
      未就绪驱动器与异常返回合同保持（`IsReady` false → 落 `lastValidMap` 回退；`catch { return 0f; }` 语义未变）；
      `_driveInfoCache` 按 root 分键、`lastValidMap` 按完整键分键、不混盘；
      **未把物理盘休眠/扫描节奏改动**（`_lastDiskScan` / `IsDiskScanFresh` / `MarkDiskScanned` 未进 diff）。
    - F07 与**上游 `a71ce75` 逐字一致**（`git show` 对比：仅新增 `^us\.` → `us` 规则 + 文件末尾换行），
      规则顺序正确（`^us\.` 在 `\.[A-Z]+$` 之前）；13 组用例覆盖 `us.NDX→usNDX`、`us.AAPL`、`usAAPL.OQ`、
      A 股（`sh600519.SH`/`sz000001.SZ`）、港股（`hk00700.HK`）、无后缀、空串、`?`、null，
      空串与 null 均正确回落到 `query`；**未联网、未改 sandbox、未覆盖用户自定义插件**。
    - F08 网页与桌面现在是**同一套口径**（网页 `GetValueStr(key,v,false,item.UnitPanel)` + `GetUnitStr` + `GetDisplayUnit`；
      桌面 `MetricItem.cs:175-178` 完全对应），**未发现双重换算或量级错配**；
      计划 §11 第 3 条各条断言（32GB/50%/容量模式、自定义 `%`、`{u}`、空串隐藏、未知容量回退 `%`、
      提交内存独立分支、按卡 VRAM、FPS 占位不读值、`pct`/`status` 基于原始值）**均有对应用例**；
      两个既有 public 重载保留（`:685-694`、`:714-723`，后者转调 `userFormat: null`）⇒ 兼容性成立；
      测试**不是**"直接喂百分号给 MetricUtils"的假测试——生产链路被 **IL 断言**钉住
      （断言 `WriteSnapshotJson` 实际调用 `WriteSnapshotItemJson`），接缝为 `internal static`，
      由 `LiteMonitor.csproj:142` 的 `InternalsVisibleTo` 授权。
    - T09 三处释放检查**一处未删**（`Assert.True(DisposesType(...))`，未跳过 Release、未改成搜源码），
      生产 `UpdateChecker.cs` 未被修改 ⇒ 计划 §12 第 1、4 条成立。

---

## 9. 处置建议（按优先级）

1. **先修 C-1**：让 settings 路径可注入（消除进程级共享）＋ 生产侧唯一 tmp 文件名 ＋ 门禁把
   `ReviewMarketPersistenceRegressionTests` 钉进 `MUST_EXECUTE_TEST_CLASS` ＋ 多 TRX 时 FAIL。
   目标：**连续 3 次全量（Debug + Release）零失败**。
2. **C-2 重新取证**：以当前树重跑双配置全量、重新冻结哈希、**补审 `SettingsHelper.cs` 的市场相关部分**。
3. **C-3 需要产品拍板**：legacy 实时玻璃是"保开关 + 标注"还是"真下线 + 一次性告知"。
4. **C-4** 让回滚失败可观测，并把"记录写失败 ⇒ payload 还原"提升为独立契约用例。
5. 收 A-1～A-5、B-1～B-7、C-1'～C-4'、R-1～R-8（多为小改动，可分两批）。
6. **D-1 单独处理**：要么补一条经 `HardwareValueProvider` DISK 分发的端到端用例，
   要么在缺陷报告里明确写"该键当前无生产入口，修复是防御性的"——**不要让验收记录继续声称
   "用户可见缺陷已闭环"**。D-2（DASH 单位两端读不同键）作为遗留项登记即可。
7. R-6 / R-7（证据入版本库、脚本去绝对路径）随批次一并处理。

---

## 10. 审查边界与未验证项

- 本轮**未修改候选生产代码**；仅新增本报告与 `E:\litemonitor-acceptance\...\r3-review\` 下的复跑证据。
- 复跑为**本地口径**（未设 `LITEMONITOR_PLUGIN_REPO`）；**CI 口径下 6 条市场契约用例会真跑，其结果本轮未复验**。
- §1.2 的"具体肇事者"（哪一条外部写入者在那一刻打断了记录写入）**未坐实**：
  审查 agent 在只读约束下无法执行用例指认；已排除"xUnit 集合并行"与"静态接缝泄漏"两个假设，
  确认通道是**固定路径 + 活单例 + 集合外测试类**的组合，但精确肇事者需在 C-1 修完后复跑确认。
- 未做：发布包构建与签名、包内资产核对、多显示器/DPI/多显卡实测、长时间性能实测、真实 GPU 设备故障注入。
- **F06–F08 + T09 段审查 agent 按只读约束未执行 build/test**，其"新增用例可编译、可通过"这一
  §13 必需门禁**未经运行验证**（该边界由授权约束造成，非产品失败）；
  主审已用自己的双配置全量复跑间接覆盖了这些用例（LiteMonitor.Tests 1770 条中含 F06/F07/F08 新增 56 条，
  本次 Debug 通过 1762 / 失败 1 —— **失败的是市场持久化用例，不在 F06–F08 段**）。
- 全部改动**仍未提交**（41 个已跟踪文件修改 + 29 个新文件 = 执行方 70 项）；G 盘 master 上**没有** P1-01 修复与本批任何改动。
