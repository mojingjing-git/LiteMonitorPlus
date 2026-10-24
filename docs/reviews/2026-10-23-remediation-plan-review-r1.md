# 修复计划独立复审 R1

结论：**REQUEST_CHANGES**。计划 v1 有 **3 项 P2 阻塞**；本次没有新增 P1 计划阻塞。修订并复核这些条目之前，不应派发实施 Agent。

审查日期：2026-10-23（Asia/Shanghai）。代码基线：`9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。

审查对象：`docs/superpowers/plans/2026-10-23-independent-review-remediation-plan.md` v1，重点为 §4～§7，并对照 `docs/reviews/2026-10-23-full-project-regression-and-upstream-review.md` 的 F01～F04。所审计划 SHA-256：`456356EE414EB67451AD0DABE8907A113F6132BF308C0532880130ACB27725DB`。以下代码锚点均为该基线的仓库相对路径及一基行号。

本次只读生产代码及现有测试，未运行构建、测试或运行时探针；未修改计划、实现、测试或用户配置。不能将本报告理解为修复代码已通过验收。F05～F08、T09 的深入审查由 R2 承担，本报告不重复裁决。

## 分项结论

| 范围 | 计划结论 | 依据 |
| --- | --- | --- |
| F01 / §4 | PASS，无新增阻塞 | 同 HWND 代次历史单调推进、仅新句柄代次重置、继续使用统一回退判据，能覆盖真实缺陷；注意下文 O1 的代次比较落点。 |
| F02 / §5 | PASS，无新增阻塞 | 排除已接管 Failed 条目，先 Add 新责任再标记旧责任，保留失败审计，能够修复 A、B 连续耗尽的问题。 |
| F03 / §6 | REQUEST_CHANGES | R1-B02：取消检查和代次检查不能代替发布与退役的原子性。 |
| F04 / §7 | REQUEST_CHANGES | R1-B01：市场局部锁挡不住通用保存；R1-B03：失败恢复材料及现有恢复算法不闭环。 |

## 阻塞发现

### R1-B01 — [P2] 市场记录事务必须与通用 Save 共用保存临界区

**计划锚点：**§7 步骤 1～2（第 110～111 行），Agent C 写入范围／协调约定（第 41 行）。

**代码证据：**

- `src/Core/SettingsHelper.cs:196–212`：`TrySave()` 先检查 GlobalBlockSave，随后在第 202 行序列化整个 Settings；只有 `AtomicWrite(json)` 在 `_ioLock` 内。失败后的 `CleanupTempFile()` 也在该锁外。
- `src/Core/SettingsHelper.cs:223–225`：全部通用 `Save()` 都进入该入口，不会取得未来市场层自己的锁。
- `src/Plugins/PluginMarketState.cs:55–65`：更新实时 Settings 的 MarketPlugins 后才保存。
- `src/Core/Settings.cs:535–553`、`src/System/WebServer/WebSessionManager.cs:1469`：网页口令迁移也会调用 TrySave，保存并非仅来自市场所在 UI 操作。

**具体反例：**市场线程把实时列表临时改成新记录；另一通用保存线程在锁外把这份暂态记录序列化为 JSON，尚未写盘；市场保存因独占文件锁失败，恢复内存和旧插件文件；文件解锁后，通用保存线程写入早已捕获的新记录。结果是旧插件文件／旧内存记录／新磁盘记录。仅对 Upsert 加市场锁、甚至只把 TrySave 的序列化移入 `_ioLock`，都不够：市场变更本身若仍发生在共享门外，通用序列化仍能看见它。

另一个同入口问题是：失败保存释放 `_ioLock` 后才清理共用的 `settings.json.tmp`，可能删掉下一保存线程刚创建的临时文件。新增事务入口不应继续留下这个锁外清理窗口。

**最小修订建议：**

1. 将共享保存协议列为本组必做事项，明确允许 Agent C 经主控协调修改 `src/Core/SettingsHelper.cs`，不再仅将其视为可能需要的测试接缝。
2. 市场前态快照、MarketPlugins 变更、序列化、写盘及失败时恢复，必须在与通用 Save／TrySave 相同的保存临界区内完成。所有保存入口先进入该门再序列化；GlobalBlockSave 判定及失败临时文件清理也纳入协议。只恢复当前事务触及的市场记录，不回写整个旧 Settings 快照。
3. 优先提供内部事务保存入口，保持 public Save 静默失败及 TrySave 返回 bool 的兼容性；不得用永久替换 Settings 单例解决。保存门内只执行受控的设置操作，不回调安装器、调度器或 UI。
4. 给出锁序，例如安装事务锁在外、共享保存锁在内；不得从持有保存锁的回调反向取得安装／恢复锁。记录读取采用相同的记录访问约定，避免换成另一把互不协作的列表锁。

**必须补充的验收：**用确定性屏障暂停“市场暂态记录已写入、事务尚未决定”的区间，同时发起真实通用 Save；市场保存失败并恢复后，通用 Save 才能序列化。最终内存、独立读回的 settings 与旧插件文件一致，且同期无关设置字段不丢失。保留真实 `FileShare.None` 和 GlobalBlockSave 用例；仅测单线程保存失败不覆盖本问题。

### R1-B02 — [P2] F03 的发布、缓存及调度收尾必须与代次退役原子化

**计划锚点：**§6 步骤 3～5（第 89～91 行）及迟到完成验收（第 99 行）。

**代码证据：**

- `src/Plugins/PluginManager.cs:420–432`：StopSchedule 在 `_scheduleLock` 内退役代次、移除表项，锁外停止计时器和取消 CTS。
- `src/Plugins/PluginManager.cs:624–643`：await 执行器之后才检查代次；随后增加失败计数、重置网络和清缓存都不与退役原子化。
- `src/Plugins/PluginManager.cs:696–706`：Rearm 的代次检查、表项核对、`timer.Start()` 分处不同临界区。
- `src/Plugins/PluginExecutor.cs:316–352`、`:1354–1445`：执行器在返回 Manager 之前已发布普通结果、api_text、颜色、单位及标签。
- `src/Plugins/PluginExecutor.cs:356–366`、`:1450–1515`：普通异常处理也直接发布错误和标签，不能只保护成功分支。
- `src/Plugins/PluginExecutor.cs:458–467`、`:576–592`：缓存过期删除、淘汰和写入都是共享副作用；仅保护最终 InfoService 写入仍会污染新缓存。

**具体反例：**旧执行通过一次 token／代次检查后暂停；热加载退役它并清缓存、建立新任务，新任务已发布新值；旧执行恢复，继续写数据或缓存。Manager 在 await 返回后即使正确丢弃收尾，也无法撤销已经发生的执行器写入。同理，旧心跳通过第 627 行后再发生退役，仍可进入第三次失败分支清掉新缓存；Rearm 核对表项后再退役，也仍可能触碰已释放计时器。

因此，第 91 行的“必要时增加……护栏”必须改成明确必需的原子发布协议；再多一次无锁 token 检查也不能关闭检查与写入之间的窗口。

**最小修订建议：**

1. 为每次执行携带内部生命周期身份／发布租约，校验“当前代次仍有效”与提交共享副作用必须在同一门内；退役、旧缓存／数据清理及新代次建立遵守同一顺序。Manager 收尾同样受该协议约束。门可以复用既有锁或新增，但实现前须明确谁持有它及锁序。
2. 覆盖成功、api_text、错误、标签／单位／颜色、KeyCache／步骤缓存写入与淘汰、失败计数、缓存重置决策及计时器重装。解析及网络工作在门外完成；通过门的应是已准备好的短提交动作。保持公开执行入口和现有私有测试接缝兼容。
3. 禁止持发布门或 `_scheduleLock` 等待网络、同步等待任务、取消 CTS、调用任意外部事件／观察器。退役身份先在门内提交，再在门外取消等待者、释放资源。网络重置等外部动作也须定义授权及执行顺序，不能把旧代次检查后的任意工作直接移入大锁。
4. 明确事件协议：`PluginManager.cs:132` 当前把执行器事件同步转发为 OnPluginSchemaChanged，不能在发布锁内直接 Invoke。若本轮触发／增加通知，应在门内形成带代次的通知意图，门外分发，并由消费端过滤失效代次，或与退役共用串行投递机制。当前执行器只有 OnSchemaChanged 声明，未发现生产触发点；本发现不是声称已有事件死锁，也不要求增加通知功能。
5. 继续保留 HTTP 共享请求与单个等待者取消的分离；不得将实例退役令牌绑到所有等待者共享的底层请求上。

**必须补充的验收：**

- 用真实执行器或其生产发布接缝，在旧执行“最后一次检查之后、共享写入之前”暂停，执行热加载／卸载并提交新值，再放行旧执行；分别验证成功与异常完成，数据、错误、标签／单位及缓存均不被旧代次修改。
- 在旧失败收尾的检查之后触发退役，验证旧回调不能清新缓存、修改新失败计数或发起失效的重置／重装。
- 强制调用旧计时器回调，而非只调用会因 disposed 自行返回的假计时器 FireAsync；若通知路径参与本轮修改，增加订阅者重入 Stop／Reload 的有界测试，并验证失效通知无效。
- 保留共享 HTTP 请求的另一等待者成功取得结果的断言。

现有 `tests/LiteMonitor.Tests/PluginSchedulingTests.cs:440–514` 分别观察 Manager 收尾和 HTTP 返回前取消；不能证明上述检查之后的发布窗口安全。该文件第 635～637 行的假计时器会直接跳过已释放回调。`PluginRequestIdentityTests.cs:355–400` 则是应保留的共享等待者合同。

### R1-B03 — [P2] “留下日志”不等于记录与文件回滚失败后仍可恢复

**计划锚点：**§7 步骤 3～4（第 112～113 行）及回滚失败后恢复验收（第 123 行）。

**代码证据：**

- `src/Plugins/PluginMarket.cs:980–1007`：记录回调已成功返回后，afterRecord 故障仍会进入 Rollback；因此必须处理“新记录已经持久化、补偿写盘又失败”。
- `src/Plugins/PluginMarket.cs:1162–1185`：现有顺序先用 Move 恢复目标文件，消耗备份，再恢复／删除安装记录，最后移除日志。
- `src/Plugins/PluginMarket.cs:850–857`：日志只有事务／插件身份、文件名与新内容 hash，不含旧记录前态或补偿阶段。
- `src/Plugins/PluginMarket.cs:1101–1123`：Reconcile 遇到 stage 存在或目标不再匹配新 hash 时，直接删 stage／backup 并返回，根本不再比较记录。
- `src/Plugins/PluginMarket.cs:1070–1086`：上述返回即算 handled，随后可删除整份日志。`:1133` 的必要目标删除又使用吞异常的 SafeDelete（定义在 `:1520–1523`）。

**具体反例：**更新的新文件和新记录都已提交，afterRecord 抛异常；Rollback 先恢复旧文件并消耗备份；旧记录补偿保存失败。即使按计划不删除 journal，下次恢复也会因“目标不是新 hash”直接完成收尾，留下旧文件／新磁盘记录。新安装的类似路径可留下“文件已删、磁盘记录仍在”。此外，目标文件删除失败若被 SafeDelete 吞掉，同样可能在文件仍在时删除最后的日志。

这不是单纯缺一条断言：现有 journal 与 Reconcile 的信息和操作方向不足以支持该失败状态，计划必须先选定可恢复的协议。

**最小修订建议：**

1. 明确持久化提交点、补偿顺序与后续恢复方向。较小的方案是：对已持久化的记录，先确认记录补偿，再恢复／删除目标文件；记录补偿失败时保持仍与磁盘记录匹配的新文件，保留 journal／backup，后续按已提交记录选择一致的收尾方向。若选择其他顺序，须同时说明恢复方如何可靠得到所需前态；不能仅保留当前日志并假设它足够。
2. 区分记录调用尝试、已持久化、已撤销内存、文件已确认恢复等事实。初次记录保存失败且市场写入入口已撤销内存时，不必为了“回滚”再次调用必然写盘失败的回调。
3. 作为一致性必要步骤的 Move／Delete 必须反馈结果；只有文件和持久化记录已确认一致后才算 handled／删除必要材料。可选垃圾清理与必要回滚不可共用“吞异常即成功”的判定。
4. 恢复依据须是已提交记录；当前生产传入 `PluginMarketState.Find`（`:819`），而 Find 读取 Settings.Load 的实时单例（`PluginMarketState.cs:38–45`），不能无条件当成独立磁盘证据。明确权威读取／提交快照协议，读取失败保留 pending，不把默认配置当成“无记录”，不永久替换 Settings 单例。
5. 提交、补偿与恢复的文件／journal 操作应串行化。目前 Commit 用 `_commitLock`（`:956`），公开恢复外层用另一把 `_recoveryLock`（`:813`），核心恢复本身不取得提交锁。增加协议时须统一这些操作或明确单向锁序，避免恢复与活动提交交错；共享保存门不得反向调用恢复。新增元数据若有必要，仍需维持现有 id／hash／路径重建／重解析点可信边界。

**必须补充的验收：**真实保存新记录后注入 afterRecord 故障，再分别令记录补偿保存失败、文件恢复／删除失败；释放故障并丢弃测试内存前态，使用重新读取的持久化记录调用生产恢复。最终只能得到文件／记录一致的旧版或按明确提交点确认的新版本；处理未完成时材料仍在，确认一致后才清理。新装、更新都覆盖；另保留回调先改内存再抛出用例及提交／恢复交错的屏障测试。

现有 `tests/LiteMonitor.Tests/PluginMarketPolicyTests.cs:358–389` 只使用不会保存失败的 InMemoryPluginRecordStore（`:766–778`）；`:453–560` 覆盖三个安装崩溃点，但没有覆盖补偿写盘失败。`PluginMarketRecoveryBoundaryTests.cs:512–540` 覆盖的是 afterMove 的正常回滚，也不能替代本项。

## 已核对的非阻塞边界与可选加强

### O1 — F01：以已记账代次识别重置，不要拿已推进的真相源作旧值

`RenderingTransitionCoordinator.cs:156` 的 HandleGeneration getter 直接读取真相源 Current；MainForm 已在 `MainForm_Transparent.cs:354` 推进真相源，随后第 369 行才通知协调器。因此实现 §4 的重置比较时，应保留／比较 `_state.HandleGeneration` 的旧快照，而不是把回调新值与 getter 比大小后误判成“同代”。新代次才清历史，重复及旧代次不得修改历史和阶段。

实际降级入口已核对：`UIController.cs:573`、`:1829`、`:1855` 都会记录宿主状态，最终 `RenderingTransitionCoordinator.cs:551–573` 决定是否重建。计划要求同时测试生产 ScheduleGpuFailureFallback，强于现有只检查 IL 调用存在的 `GpuFailureFallbackTests.cs:382–392`。

建议新增测试真正调用注入真相源 Advance，再通知 HandleCreated，包含 controlled／非 controlled、重复、旧代次及关闭路径。现有 `RenderingTransitionCoordinatorTests.cs:182–208` 直接传入 42，并未同步推进注入源，不能单独证明真实重置顺序。

ShouldDiscardCallback（`RenderingTransitionCoordinator.cs:588–591`）需要三项身份，而现有 GpuFailureReport（`GpuFailureFallbackScheduler.cs:40–45`）不携带它们，不能以“已有辅助判据”代替实际接线。当前宿主使用 UI 线程 WinForms.Timer（`GpuPresentationHostController.cs:101–103`）同步上报事件（`:804–816`），本次未发现必须额外扩张异步报告接口的证据；若实现引入排队回调，应从订阅／入队处捕获真实身份，不能在消费时用当前代次给旧报告补签身份。

### O2 — F02：保持 UI 串行账本及精确原值，不另造恢复真相源

已核对 `CapturePolicyCleanupLedger.cs:297–338` 的选择与登记顺序、`:237–245` 的阻塞判据及 `RenderingTransitionState.cs:235–241` 的接管标记。计划的最小修复可保留现有结构，不需要把 Failed 改成 Completed 或清空失败审计。

生产明确恢复安排仍应先于 StopGpuPresentation（`UIController.cs:1437–1442`），避免新宿主采集到残留 affinity。旧代次过滤先于窗口调用（`CapturePolicyCleanupLedger.cs:392–408`）也应保留。验收除 A→B→C 外，建议明确包括成功后再次请求恢复必须拒绝，以及多轮接管始终保留同一精确 OriginalAffinity／HWND；不要只断言最后一条 Completed。

现有 `CapturePolicyCleanupUnresolvedGateTests.cs:159–200`、`:257–296` 只覆盖 A 失败、B 成功；`:304–338` 覆盖恢复先登记和停止移交拒绝；`CapturePolicyCleanupRegistrationTests.cs:30–65` 覆盖同实例幂等与同代次冲突。计划要求的多轮链是有效的新覆盖。当前账本生产驱动保持 UI 串行即可，不建议为了此项无依据地引入另一套锁。

### O3 — F03：“不影响无关实例”加入 A／AB 前缀边界用例

§6 已规定实例隔离，但现有 `PluginManager.cs:304–310` 直接把裸 instanceId 用于 RemoveDataByPrefix；`InfoService.cs:187` 使用 StartsWith。清理 A 会连带删除 AB 的值与属性。热加载新增调用清理路径时不要直接复用这项宽前缀行为；建议明确精确键／点分隔的命名空间边界，并测试 A、AB 的数据、标签、单位与缓存互不干扰。步骤缓存已使用 `instanceId + "|"` 隔离（`PluginExecutor.cs:187–192`），可参考其边界原则。

### O4 — F04：卸载调用兼容风险已在计划中被识别

`PluginMarketUninstallCore.cs:31–35` 是先删文件再 Remove。若本轮改变公开 Remove 为可失败／抛异常入口，就必须同步提供卸载补偿；优先使用事务专用入口可控制本次修改范围。§7 步骤 5 已明确该风险，本报告不再将其单列阻塞，但不能在实现时删掉这条要求。

## 可执行性及复核要求

Agent A／B／C 的主要文件分工可执行。R1-B01 使 SettingsHelper 的生产协议修改成为明确工作项，主控应先落实 Agent C 的文件归属及内部接口；不需要让 C 修改 PluginManager，也不需要让 B 修改市场事务层。B 的发布身份和 C 的保存事务接口各自保持内部兼容，跨组调用约定由主控确认。

建议修订计划后先复核上述三个协议及新增反例，再派实施。实施后由主控按既定串行窗口运行新增 GPU／插件重载／市场持久化回归，并保留相邻的 PluginSchedulingTests、PluginRequestIdentityTests、CapturePolicyCleanup 系列、BatchD1MarketRecoveryOrderingTests、PluginMarketPolicyTests、PluginMarketRecoveryBoundaryTests；修改 SettingsHelper 后还应覆盖 SettingsRecoveryTests 及现有保存／备份相关套件。测试使用隔离输出目录，恢复 Settings 单例、GlobalBlockSave、插件目录覆盖及所有注入钩子。

本报告不提供任何新测试通过计数，也未复跑上轮缺陷探针。最终计划审批应在主控处置 R1-B01～B03、完成 R2 的独立复核后进行。

## v2 定向复核：R1-B01～B03

复核日期：2026-10-23（Asia/Shanghai）。结论：**PASS**。R1-B01、R1-B02、R1-B03 在计划层面均已关闭，本次限定范围内无剩余 P1／P2 阻塞。上文 REQUEST_CHANGES 保留为 v1 的首轮审查记录；本节是 v2 的最新 R1 结论。

本次仅复核原路径计划 v2 的 §6、§7 及相关分工，对照首轮已核实的生产代码和测试边界判断可执行性，未扩展重审项目。所审计划 SHA-256：`76985B6F4EFCFD4BFCF260BC6AD1A16E808D0E709B38190E3897305701CF6ED1`；代码基线仍为 `9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。以下计划行号属于该 v2 文本。

| 原阻塞 | v2 处置锚点 | 复核结论 |
| --- | --- | --- |
| R1-B01：市场暂态被通用保存捕获 | 分工第 41 行；§7 第 114～116、128、130 行 | PASS。C 已明确拥有 SettingsHelper；Save／TrySave 在共同保存门内检查禁写、序列化、写盘及清理，市场前态／变更／失败恢复使用同一门。仅恢复受影响记录，保留无关字段及单例身份，并规定事务门外、保存门内的锁序。并发真实 Save、文件锁和原保存备份套件均有验收落点。 |
| R1-B02：发布与代次退役缺少原子性 | §6 第 89、91～93、101～103 行 | PASS。原子发布租约已成为强制要求，覆盖成功／错误／api_text、属性、KeyCache、步骤缓存删除／淘汰、失败收尾及计时器重装；退役和清理与提交共同排序。外部重置明确要求有效授权／串行协议；取消、资源释放及外部观察器在门外。没有新增事件功能，触及通知时才执行代次过滤与重入验收。共享请求的其他等待者仍受原合同保护。 |
| R1-B03：补偿失败后的材料与恢复算法不闭环 | §7 第 116～120、129～130 行 | PASS。已明确记录补偿先于文件恢复；补偿失败保持匹配新磁盘记录的新文件与恢复材料，补偿成功但文件失败则保留向旧版恢复的材料。权威持久化读取、必要 Move／Delete 的结果、handled／清理条件、Commit／Recover 串行及 journal 信任边界均有规定。真实提交后故障、新装／更新、丢弃内存前态再恢复和交错屏障构成可执行的验证矩阵。 |

### 执行分工复核

上述修改可由现有 B／C 分工完成：B 持有 PluginManager 与 PluginExecutor，能统一调度身份和发布租约；C 持有 PluginMarketState、PluginMarket 与 SettingsHelper，能将记录保存和恢复协议完整落在本组，不需要 C 修改 PluginManager。第 45 行的跨组接口协调仍适用，未发现这三项修订造成新的文件归属冲突。公开 Save／TrySave、执行入口和现有测试接缝的兼容要求均已保留。

### 验收屏障的实施说明（非新增阻塞）

§6 第 101 行描述的是要排除的迟到写入时序。测试应将可放行的旧任务屏障放在门外准备完成、进入最终原子提交之前，再让其经历退役／新值发布并尝试提交；最终身份复核仍在门内。若专门在“门内身份已确认、尚未写入”处暂停，则应断言退役／新代次建立被串行阻挡，放行该次提交后才能推进退役，不能同时要求退役先完成，否则测试会因正确的互斥而自身死锁。两种安排均验证 v2 已规定的原子顺序，不增加审批前置条件或实现范围。

本次只追加本节，未改计划、生产实现或测试，也未运行构建／测试／探针。三项阻塞的计划关闭不代表实现或回归已完成；R2 复核及实施后的独立代码复审、串行验收仍依原计划执行。
