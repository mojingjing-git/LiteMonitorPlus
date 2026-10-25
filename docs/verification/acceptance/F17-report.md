# F17 验收记录 —— 插件市场安装事务的并发替换冲突

## 1. 标识

- 工作包：**P-F17**
- 分支：`fix/F17-market-install-race`
- 基线 SHA（开工时 HEAD）：`dd04fdd`
- 提交 SHA 列表（`git log --oneline dd04fdd..fix/F17-market-install-race`）：

  | SHA | 说明 |
  |---|---|
  | `2f6e59a` | `fix(F17): 市场落盘原子替换收敛为共享读 + 有界重试` |
  | （本提交） | `docs(F17): F17 验收记录` |

- 立项依据：`docs/plans/dispatch/P-F17.md`（用户裁定：**根因 + 兜底都改**）
- 完成日期：2026-10-26
- 执行者：P-F17 外派执行会话（worktree `G:\TOOL\LiteMonitor-fork\.worktrees\f17`）

---

## 2. 覆盖与结论

- 覆盖范围：插件/主题市场**落盘原子替换**与**并发读取**之间的冲突。
  对应上游 issue：#521（部分）、#569（部分）—— 二者原本被判定为"上游不生效"，本包证明**在 fork 上会真实发生**。
- 结论：**☑ 代码层验收通过**（确定性用例 + 10 轮连跑全绿，见 §6）。
- 真实用户影响（修复前）：安装静默失败并留下事务日志 ⇒ 下次启动被回滚 ⇒ **用户的安装丢失且无明确报错**。
- 本包**不涉及** HTTP 协议面、UI 落点、共享热点文件（`Settings.cs` / `AppActions.cs` / `UIController.cs` / `HardwareMonitor.cs`），
  也未改动任何 CI 阈值。

---

## 3. 根因：Windows 文件替换语义（**实测**，非推测）

### 3.1 实测矩阵

在本机（Windows 10.0.26300 x64，.NET 8）以 `File.Move(src, dst, overwrite: true)` 实测：

| 场景 | 结果 | Win32 错误 | .NET 异常 |
|---|---|---|---|
| 源、目标都不被占用 | **成功** | — | — |
| **目标**被 `FileShare.Read` 占用 | **失败** | `ERROR_ACCESS_DENIED` (0x80070005) | `UnauthorizedAccessException` |
| **目标**被 `FileShare.ReadWrite` 占用 | **失败** | `ERROR_ACCESS_DENIED` | `UnauthorizedAccessException` |
| **目标**被 `FileShare.ReadWrite\|Delete` 占用 | **失败** | `ERROR_ACCESS_DENIED` | `UnauthorizedAccessException` |
| **目标**被 `FileShare.Delete` 占用 | **失败** | `ERROR_ACCESS_DENIED` | `UnauthorizedAccessException` |
| **源**被 `FileShare.Read` 占用 | **失败** | `ERROR_SHARING_VIOLATION` (0x80070070) | `IOException` |
| **源**被 `FileShare.ReadWrite` 占用 | **失败** | `ERROR_SHARING_VIOLATION` | `IOException` |

### 3.2 两条必须分清的语义

1. **replace-existing**（目标已存在）：`MoveFileEx(..., MOVEFILE_REPLACE_EXISTING)` 要求**目标文件当前没有任何打开的句柄**。
   对方即使以 `FILE_SHARE_DELETE` 共享，替换依然 `ERROR_ACCESS_DENIED`。
   ⇒ **读侧共享 `FileShare.Delete` 救不了 replace**；唯一出路是"等占用解除后重试"。
2. **rename-away**（改名挪走，目标不存在）：只要求源允许 delete 共享。
   ⇒ 读侧共享 `FileShare.Delete` **确实**能解锁。

> ⚠️ 这两条被刻意分开写进代码注释与用例注释（`MarketFileIo.cs` §类注释、`ReviewMarketInstallRaceRegressionTests.cs` §类注释），
> 避免后人把"共享读"误当成"replace 的根因修复"。

### 3.3 现场取证

执行期临时插桩（**已移除，不入库**）在替换失败的瞬间以 `FileShare.None` 复探目标文件，连得：

```
destination=…\review-market-<guid>\plugins\review_market_persistence.json
source=…\review_market_persistence.json.<guid>.bak
exception=System.UnauthorizedAccessException hresult=0x80070005
exclusiveOpen=FAILED IOException: … because it is being used by another process.
```

⇒ 失败发生在**回滚路径**（`.bak → .json`），且目标在 4 次尝试、跨约 175ms 的窗口内**持续**被占用，
形态是 `UnauthorizedAccessException` ⇒ 与 §3.1 的"目标被占用"一行完全吻合。

---

## 4. 证据锚点（真实文件:行号）

### 4.1 改造前（基线 `dd04fdd`）

| 事实 | 位置 |
|---|---|
| 读取侧用 `File.ReadAllBytes`（`FileShare.Read`，不共享 Delete） | `src/Plugins/PluginMarket.cs:522`、`:1542`、`:1652` |
| 读取侧用 `File.ReadAllText`（同上） | `src/Plugins/PluginMarket.cs:545`、`:1117`、`:1613` |
| 替换侧裸调 `File.Move(…, overwrite: true)`，无重试、无兜底 | `src/Plugins/PluginMarket.cs:1038`、`:1219`、`:1243`、`:1258`、`:1627`；`src/Plugins/PluginMarketUninstallCore.cs:86` |

### 4.2 改造后（本分支）

| 位置 | 内容 |
|---|---|
| `src/Plugins/MarketFileIo.cs:36` | `MarketFileReplaceBlockedException` —— **可识别**异常：只表示"重试预算耗尽、目标仍被占用" |
| `src/Plugins/MarketFileIo.cs:69` | `MarketFileIo` —— 市场落盘的文件 IO 边界 |
| `src/Plugins/MarketFileIo.cs:72` | `MaxAttempts = 4`（首次 + 3 次重试），**次数上限** |
| `src/Plugins/MarketFileIo.cs:75` | `DefaultBudget = 300ms`，**单调时钟预算上限**（硬上限，绝不无限循环） |
| `src/Plugins/MarketFileIo.cs:78` | `BaseDelay = 25ms`，退避 25→50→100ms，且被剩余预算截断 |
| `src/Plugins/MarketFileIo.cs:84` | `Policy`（`AsyncLocal`）—— 可注入时钟/等待/观测的测试接缝 |
| `src/Plugins/MarketFileIo.cs:103` / `:115` | `ReadAllBytesShared` / `ReadAllTextShared` —— 读侧 `FileShare.ReadWrite \| FileShare.Delete` |
| `src/Plugins/MarketFileIo.cs:132` | `MoveAtomic` —— 唯一的原子替换入口：共享读 + **有界重试** |
| `src/Plugins/MarketFileIo.cs:167` | 预算耗尽 ⇒ 抛 `MarketFileReplaceBlockedException`（**不吞掉、不伪装成成功**） |
| `src/Plugins/PluginMarket.cs:522/545/1117/1542/1613/1652` | 全部读取侧改走 `MarketFileIo.ReadAll*Shared` |
| `src/Plugins/PluginMarket.cs:1038/1219/1243/1258/1627` | 全部替换侧改走 `MarketFileIo.MoveAtomic` |
| `src/Plugins/PluginMarketUninstallCore.cs:86` | 卸载回滚同样改走 `MarketFileIo.MoveAtomic` |

> 替换点**一个不漏**：改造前全仓 `File.Move(…, overwrite: true)` 在 `src/Plugins/**` 的 6 处全部收敛到 `MarketFileIo.MoveAtomic`。

---

## 5. 改动清单

| 文件 | 性质 | 说明 |
|---|---|---|
| `src/Plugins/MarketFileIo.cs` | **新增** | 共享读 + 有界重试的 IO 边界 + 可识别异常 |
| `src/Plugins/PluginMarket.cs` | 修改 | 6 处读取 + 5 处替换改走 `MarketFileIo` |
| `src/Plugins/PluginMarketUninstallCore.cs` | 修改 | 1 处替换改走 `MarketFileIo` |
| `tests/LiteMonitor.Tests/ReviewMarketInstallRaceRegressionTests.cs` | **新增** | 5 条确定性回归用例 |
| `src/Plugins/MarketFileIoDiag.cs` | 临时插桩 | **已删除，不入库**（见 §8） |

授权边界核对：仅触碰派发单授权的 `src/Plugins/PluginMarket.cs`、`PluginMarketUninstallCore.cs` + 新建文件；
**未**触碰 `ThemeMarket.cs`、`PluginMarketState.cs`、四个共享热点文件、任何 CI 阈值文件。

---

## 6. 测试证据

### 6.1 新增确定性用例（5 条，全部**不依赖运气**）

| 用例 | 锚点 | 断的是什么 |
|---|---|---|
| `AtomicReplace_RetriesAndSucceedsOnceTheTransientLockIsReleased` | `ReviewMarketInstallRaceRegressionTests.cs:39` | 旧实现第一次替换即抛 ⇒ 必红；新实现占用解除后重试成功 |
| `AtomicReplace_AttemptBudgetExhausted_ThrowsIdentifiableErrorAndRollsBackCleanly` | `:86` | 次数预算耗尽 ⇒ 抛可识别异常 + 事务回滚干净（不留 stage/bak/journal） |
| `AtomicReplace_TimeBudgetExhausted_StopsRetryingAndRollsBackCleanly` | `:114` | 时间预算耗尽 ⇒ 立刻收手（`Attempts < MaxAttempts`）+ 回滚干净 |
| `InstallAndRecovery_RacingReplace_LeavesConsistentFinalState` | `:140` | 安装 × 恢复并发 ⇒ 最终状态必一致（不允许"文件换了记录没换"或反之） |
| `SharedRead_DoesNotBlockAWriterFromRenamingTheTargetAway` | `:209` | 读侧共享 Delete ⇒ 写侧可 **rename-away**；不共享 ⇒ 抛 `IOException` |

> 前 4 条用「显式文件句柄 + 显式重试闸门」制造交错，手动单调时钟推进预算 ⇒ **确定性**，不靠真实等待、不靠运气。
> 第 5 条按 §3.2 的语义边界锁定 **rename-away**（目标不存在），**不是** replace-existing。

### 6.2 复测结果

- **10 轮连跑全绿**（`ReviewMarketPersistenceRegressionTests` 33 条 + 新增 5 条 = 38 条/轮）：

  | 轮次 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
  |---|---|---|---|---|---|---|---|---|---|---|
  | 结果 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

- 修复前对照（基线 `d52c0ab` 纯净基线同样复现 ⇒ **非 W1/W2 引入**）：
  整类连跑约 **2/3** 概率失败；最新 master 实测 **1/3**；失败形态为
  `UnauthorizedAccessException: Access to the path is denied.` @ `FileSystem.MoveFile` @ `CommitInstall`。
  ⇒ 修复后 10/10 全绿即为直接对照。

### 6.3 未在本包内完成、移交集成阶段验收的项

- **整个测试工程全量连跑 5 次**（flaky 需全量并行才高概率触发，只跑这两个类不足以证明 CI 稳定）。
  该项在 F17 合入 `integration/w1` 后统一执行，见母计划 §7.2。
- Release 配置构建、web 探针（`run-probes.ps1 -Groups web`）同批次统一执行。

---

## 7. 修复后的产品行为（要点）

1. **能等到**：读者只是短暂持有目标文件（插件加载器、恢复对账、可用性校验）⇒ 有界重试通常在 300ms 预算内等到位、替换成功。
   ⇒ 用户感知：安装不再"静默失败"。
2. **等不到**：占用持续超过预算 ⇒ 抛 `MarketFileReplaceBlockedException`（可识别）⇒ 由既有回滚路径清理，
   **不留半成品、不留事务日志残留**，也不会把"重试耗尽"吞掉伪装成成功。
3. **读者不再挡住改名/删除**：读侧共享 Delete ⇒ 写侧可把被读者持有的文件改名挪走。

---

## 8. 协调者补充说明（执行体未覆盖的部分）

1. **根因表述订正**：执行体初稿把"读侧共享 Delete"写成了"并发替换被阻塞的根因修复点"。
   经 §3.1 实测证明该表述**不成立**（replace 要求目标无任何句柄，共享 Delete 无效）。
   已订正代码注释与用例注释，明确区分 replace-existing 与 rename-away。
2. **临时诊断插桩已移除**：`MarketFileIoDiag.cs` 自带"不提交"标注，且它会向 `%TEMP%` 写盘 ⇒ 已用可恢复删除移除，
   `MarketFileIo.MoveAtomic` 中对它的调用同步删除（已核对无残留引用）。
3. **行尾**：执行体新建的 3 个 `.cs` 文件工作区为**纯 LF**，与 `.gitattributes` 的"工作区 CRLF"不符
   ⇒ 已在集成期统一为 CRLF（入库仍为 LF），避免 `git status` 假改动。

---

## 9. 未验证 / 待实机

| 项 | 说明 |
|---|---|
| 真实用户场景：插件 DLL 被插件加载器长期占用 | Windows 固有限制（replace 要求无任何句柄），F17 只能给出**明确失败 + 干净回滚**，无法真正替换。需在真机上确认错误提示对用户可理解。 |
| 杀软/索引服务等外部进程占用目标文件 | 本机 Defender 实时保护已确认关闭（`AntivirusEnabled=False`），未覆盖第三方杀软场景。 |
| 主题市场落盘（`ThemeMarket.cs`） | 本包未触碰主题侧；主题侧同样存在 `File.Move` 替换点，如需一致处理应另立包。 |

---

## 10. 关联

- 派发单：`docs/plans/dispatch/P-F17.md`
- 母计划：`docs/plans/2026-10-25-verified-bugs-remediation-plan.md`（§3 批次表、§7 状态表 `MARKET-RACE`、§7.2 W2 集成记录）
- 相关包：F02（HTTP 面）、F14a（市场页缓存）—— 与本包无文件重叠，可独立合入。
