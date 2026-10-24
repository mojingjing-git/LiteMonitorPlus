# C 组市场事务实现独立复审

日期：2026-10-24（Asia/Shanghai）。基线：`9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。

工作树：`C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork`。

最终结论：**REQUEST_CHANGES，剩余 1 项 P2（MKT-R02），无 P1。** 收到 C 组 ready／冻结通知后，已重新核对四份生产文件及测试文件 SHA-256，并静态复核 13 个测试方法展开的 31 个用例。MKT-R01 已在冻结版本修复并关闭。未运行的测试没有被表述为通过。

本次先阅读计划 v3 §7、R1-B01／R1-B03 及 R1 的 v2 PASS 结论，再检查以下四个文件相对基线的生产 diff，并追踪市场页面、Settings 单例和既有恢复测试的调用合同。收到 ready 后才完成新测试文件的最终复核。全程静态审查，未执行 `dotnet build/test/run`，未修改生产、测试或 CI，未提交。

## 生产发现

### MKT-R02 — [P2] 卸载可越过未决安装事务，导致 journal 永远无法对账并阻断重装

位置：`src/Plugins/PluginMarketUninstallCore.cs:33–57`，尤其 `:56–57`；关联 `src/Plugins/PluginMarket.cs:1160–1164` 与 `:988–990`。

卸载虽然使用了同一事务门，却只做所有权检查，没有在门内排除同一插件仍在 pending 的安装事务。公开入口的 `EnsureRecovered` 会在恢复仍失败时返回并继续卸载，因此在已修复 MKT-R01 的最终代码中依然可触发。Commit 已明确把 pending 对应文件和备份视为旧事务所有，卸载却能删除它的目标与持久化记录。

具体反例：

1. 从有旧记录的插件更新到新版，文件及新记录均已提交，journal 的 `PreviousRecordSha256` 是旧版 hash。
2. 备份删除暂时失败，journal 保留；一次恢复也因备份仍被占用而保持 pending。
3. 此时执行公开卸载入口。它先恢复、因备份仍被占用而留下 pending，然后继续进入卸载。`TryCreatePlan` 验证新版目标的 id 后通过；`:56–57` 删除目标并成功持久化移除记录，卸载返回成功。
4. 解除占用并重新运行生产恢复：磁盘记录现在为 null，既不匹配新版，也不匹配非 null 的 `PreviousRecordSha256`，因此 `:1164` 每次都抛出并永久保留该 pending。重启不能解决；后续重装又被 `:988–990` 拒绝。

最小修复：在持有 `TransactionGate`、接触卸载文件之前，确认同插件没有未决安装事务；可先尝试现有恢复，但仍 pending 或 journal 不可可靠读取时必须拒绝卸载，保留原材料。复用安装对 pending 的现有保护，不需要扩展新的事务类型或恢复功能。

对应回归可在已有 `PersistenceScope(update: true)` 中，于 afterRecord 回调取得真实 `.bak` 的 `FileShare.Read` 锁并正常返回，让提交后清理失败；接着走公开卸载入口。修复后应拒绝卸载且文件／记录保持新版；解除锁并完成恢复后，卸载和重装应能成功。当前 `:458–491` 的卸载用例只覆盖 Remove 保存失败和普通成功，尚未覆盖此交叉路径。本发现是生产反例，缺测不另计一个阻塞。

### MKT-R01 — 已关闭

冻结版本的 `src/Plugins/PluginMarket.cs:815–816` 已将 journal 不存在纳入缓存命中条件。目录后来出现 pending 时，普通市场入口会重新尝试恢复，首轮的缓存反例不再成立；不列入最终 P1／P2。

## 已核对的生产合同

- `Save`／`TrySave` 的 GlobalBlockSave 检查、序列化、写盘、失败 tmp 清理共用 `_ioLock`；市场当前条目的前态、变更与失败恢复也在该门内。恢复只改该 id 的市场条目，保留单例与无关字段。
- Commit／Recover 共用 `_commitLock`，卸载也取得同一事务门；生产记录访问在内层取得保存门，未发现保存门反向调用安装／恢复的生产路径。
- 严格记录初次写盘失败会恢复内存并抛出可识别异常；Commit 不再重复调用必然失败的记录补偿。一般“先变更后抛出”的回调会尝试补偿。
- afterRecord 回滚先补偿记录，补偿失败保留匹配新记录的新文件、backup、journal；补偿成功但必要 Move／Delete 失败时保留恢复材料，后续以独立磁盘记录和前态 hash 决定方向。
- 生产恢复读取主 settings 文件，读取／反序列化失败不借用默认配置或实时单例作为提交证据。必要文件恢复／删除使用能反馈异常的操作；id、事务 id、路径重建、hash 格式及目标重解析点检查仍在。
- 严格卸载 Remove 的单次保存失败已有文件补偿；上述 MKT-R02 是未决安装与卸载相交的问题。

## ready 后测试复核

静态计数为 13 个测试方法、31 个用例，与 ready 通知一致。没有发现另需登记的测试 P1／P2；上述 MKT-R02 需要加入针对性回归。

| 测试入口行号 | 展开用例数 | 核对内容 |
| --- | ---: | --- |
| `:24` | 4 | 新装／更新 × 真实独占 settings 文件锁／GlobalBlockSave；失败只撤销当前条目，保留单例、无关字段／记录，并断言没有重复补偿保存。 |
| `:65` | 2 | 新装／更新真实落盘、文件字节与记录读回、安装时间保留及丢弃内存后重载。 |
| `:82` | 1 | 通用 Save 静默及 TrySave 的 bool 合同；真实 IO、禁写及 NaN 序列化失败，tmp 清理。 |
| `:110` | 2 | 市场门内暂态屏障与真实 public Save；失败恢复后通用序列化才观察记录，并检查无关字段／条目的独立磁盘读回。 |
| `:192` | 4 | 新装／更新 × 记录补偿被禁写／真实文件锁拒绝；保留新文件和材料，丢弃内存后通过生产恢复入口以磁盘记录收尾。 |
| `:242` | 2 | 补偿记录已落盘，但必要 Move／Delete 因真实目标文件锁失败；失败不算 handled，解除锁后按磁盘旧记录恢复。 |
| `:283`、`:311` | 2 | 主 settings 损坏、journal 独占锁均保留 pending，不回退到实时／默认记录，不误清恢复材料。 |
| `:335` | 2 | 一般回调先修改市场内存后抛出，仍能补偿并恢复文件。 |
| `:364` | 4 | 新装／更新 × 成功／afterMove crash；确定性屏障确认恢复被同一事务门阻挡，随后按实际结果对账。 |
| `:434` | 5 | 非法恢复版本、前态 hash、矛盾 HadTarget 被拒绝；文件／备份不变、日志隔离。 |
| `:458`、`:482` | 3 | 严格 Remove 保存失败的卸载文件补偿及普通成功落盘。 |
| 合计 | **31** | 仅静态复审，尚无运行结果。 |

fixture 位于禁止并行的 `cfgfile` 集合。它确认 settings 位于测试输出目录，备份并恢复主／bak／tmp，恢复 Settings 单例、禁写 owner、目录 override、故障钩子和当前目录；测试插件目录位于隔离输出子目录，递归清理前验证绝对边界。并发用例的 finally 会放行屏障并等待 worker 收尾。既有 SettingsRecovery、市场 crash point／路径边界等套件的运行由主控按既定串行安排执行。

## 冻结快照（SHA-256）

| 文件 | SHA-256 |
| --- | --- |
| `src/Core/SettingsHelper.cs` | `2EBA893F0632ADA1F78C553E6AFA18A5BA0912856FE58A6364BAF047B2EFBED0` |
| `src/Plugins/PluginMarketState.cs` | `9A896A41FD23F8731630F1899A0CD5E9ABDC647F944EA2B3E9F3C347F5C90CD1` |
| `src/Plugins/PluginMarket.cs` | `D9EFBEAAD179E5926045F40DB1191229A505DAFBAB178F7610CBA1D8726ED09F` |
| `src/Plugins/PluginMarketUninstallCore.cs` | `3A7BCED6B49EDB458A98040C6147C6CEC00D224B178B9A14AD5C8E9520D329FD` |
| `tests/LiteMonitor.Tests/ReviewMarketPersistenceRegressionTests.cs` | `2DF72B78C27938362F7E0BC5A36F0A4EB8C6D6B3F054B9C4F730F4CA4788F1E3` |

行号对应冻结快照。上述五个哈希在 ready 后读取与完成静态复核时再次读取一致；修复 MKT-R02 后应重新核对相关 diff 与针对性用例。

## MKT-R02 修复后的定向复核（最新结论）

**PASS。MKT-R02 已关闭；结合此前已关闭的 MKT-R01，本次市场生产／测试静态复审无剩余 P1／P2。** 本节替代上文首轮冻结快照的 REQUEST_CHANGES 结论，不代表构建或测试已经运行通过。

按用户明确要求，本轮仅定向复核 `PluginMarket.cs`、`PluginMarketUninstallCore.cs` 和 `ReviewMarketPersistenceRegressionTests.cs` 中的 pending 防护及两个新用例，没有重审其他文件或扩展功能。

修复核对：

- `src/Plugins/PluginMarket.cs:1561–1570` 的 `EnsureNoPendingInstall` 在同一 `_commitLock` 中读取 journal，拒绝同插件 pending，以及不能可靠归属的空条目／非法插件 id。Commit 在 `:988` 复用此入口，保留原事务所有权保护。
- `src/Plugins/PluginMarketUninstallCore.cs:22` 持有事务门，`:42` 在建备份、删除目标和调用 Remove 之前执行同一 guard。未决事务或读取错误会在任何卸载写操作前传播。guard 的同门重入不引入新的锁序。
- `src/Plugins/PluginMarket.cs:1572–1581` 的 `ReadLog` 直接读取文件；仅 FileNotFound／DirectoryNotFound 返回空日志，其他 IO／访问异常传播，结构损坏也抛出。没有再用 `File.Exists == false` 将读取失败解释为不存在。

两个新回归的静态核对：

| 入口 | 核对结果 |
| --- | --- |
| `ReviewMarketPersistenceRegressionTests.cs:495`，`Uninstall_CannotConsumePayloadOwnedByPendingCommittedUpdate` | afterRecord 取得真实 `.bak` 的 FileShare.Read 锁后正常返回，让提交成功但清理失败；公开 Uninstall 的恢复仍 pending，随后的 guard 必须拒绝。断言新文件、新磁盘记录、journal／backup 保留，未创建 `.uninstall`。解除锁并恢复后，公开卸载成功，之后实际 Commit 可以再次安装并清理材料。 |
| `ReviewMarketPersistenceRegressionTests.cs:534`，`Uninstall_CannotTreatUnreadablePendingJournalAsAbsent` | afterRecord crash 留下已提交事务后，对真实 journal 施加 FileShare.None；公开 Uninstall 必须抛出 IOException，目标、新磁盘记录和原 journal 字节保留，未创建卸载备份。解除锁后恢复完成，公开卸载可成功。 |

新增用例复用已审过的隔离 fixture；文件锁均由 using／finally 释放。测试由原 31 个增加为 **33 个用例**；本轮仅静态检查新增两个，没有执行 `dotnet build/test/run`。动态验证仍由主控待插件补丁 ready 后统一串行执行。本 Agent 仅追加此报告，未修改生产／测试／CI，未提交。

本轮定向快照（SHA-256）：

| 文件 | SHA-256 |
| --- | --- |
| `src/Plugins/PluginMarket.cs` | `84E9FA65C25C3A696BBB43DD2046722B92FCF0B670E8CAD52062A81656815908` |
| `src/Plugins/PluginMarketUninstallCore.cs` | `8D23F36E6618FB2E5A5718A204459DF453E5802AA68170A8D89B526720FF2476` |
| `tests/LiteMonitor.Tests/ReviewMarketPersistenceRegressionTests.cs` | `C42DE3523B311B209C680487ECE01826C08A79A70BAF80070652A624FC995D2B` |
