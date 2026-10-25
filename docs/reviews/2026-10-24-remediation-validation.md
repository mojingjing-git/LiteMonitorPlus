# LiteMonitor 独立审查修复与验收报告

日期：2026-10-24（Asia/Shanghai）。代码基线：`9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f`。

**本批完成 F01～F08、T09 及持续门禁修复。四份独立实现复审的最终结论均为 PASS，无未关闭的 P1/P2。Debug、Release 各实测 1831 条用例：1830 通过、0 失败、1 条指定联网 E2E 跳过。**

代码交付在托管隔离工作树 `C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork`，分支 `codex/review-remediation-20261023`，源文件和新增测试处于工作区修改状态。原项目目录 `G:\TOOL\LiteMonitor-fork` 的 docs 中保留计划和审查报告副本。

## 计划与执行流程

[详细修复计划](../superpowers/plans/2026-10-23-independent-review-remediation-plan.md) 明确了生产约束、文件分工、失败矩阵、串行构建、门禁和验收边界。计划先经两位独立 Agent 审查：R1 的保存／发布／恢复协议问题在 v2 关闭，R2 的 authority／scope／Release／跳过身份问题在 v3 关闭，随后才派四组 Agent 实施。

四组分别负责 GPU、插件调度与主机政策、市场持久化、磁盘／Stock／网页；主控处理 T09、流水线和集成。所有实施者停止写入后才统一构建。实施复审再次发现的点分 ID 误删、未决安装与卸载相交问题均已补修、独立复核并加入运行回归。

## 修复结果

| 项目 | 最终行为 | 主要实现 |
| --- | --- | --- |
| F01 / P1，新回归 | 同一 HWND 代次的呈现历史只前进；替换宿主绑定前失败不再抹去旧窗口的 Presented。真实新代次才重置，旧／重复通知不启动宿主。 | [RenderingTransitionCoordinator](../../src/UI/RenderingTransitionCoordinator.cs)、[UIController](../../src/UI/UIController.cs) |
| F02 / P2，新回归 | 明确恢复只接管尚未 Reclaimed 的失败责任；A 耗尽、B 耗尽、C 成功后正确解除闸门，保留精确 affinity／HWND 和失败审计。 | [CapturePolicyCleanupLedger](../../src/UI/CapturePolicyCleanupLedger.cs) |
| F03 / P2，历史调度与 fork 热加载交叉 | 模板与实例内容共同决定是否重启；更新执行新模板，卸载退役旧任务。执行快照、发布租约与退役共同排序，迟到成功／异常不能污染新值、属性、缓存或调度收尾。 | [PluginManager](../../src/Plugins/PluginManager.cs)、[PluginExecutor](../../src/Plugins/PluginExecutor.cs) |
| F04 / P2，fork 市场事务 | 所有保存和市场暂态变更共用保存门；失败如实报告并恢复当前记录。补偿记录先于文件，恢复以独立磁盘记录为据，未完成时保留必要材料；卸载尊重未决安装所有权。 | [SettingsHelper](../../src/Core/SettingsHelper.cs)、[PluginMarket](../../src/Plugins/PluginMarket.cs)、[卸载核心](../../src/Plugins/PluginMarketUninstallCore.cs) |
| F05 / P2，LM-094 残留及相关边界 | 完整静态 authority 先验证再提取规范主机；完整／压缩 IPv6 一致，非法 userinfo／端口不贡献声明。统一拒绝 scoped IPv6，检查早于缓存和在途复用，HTTP／凭据／redirect／native 使用一致政策。 | [PluginHostPolicy](../../src/Plugins/PluginHostPolicy.cs)、[PluginProcessor](../../src/Plugins/PluginProcessor.cs)、[CryptoNative](../../src/Plugins/Native/CryptoNative.cs) |
| F06 / P2，继承上游 | 合法逻辑盘 Used 先于物理缓存取值；冷／热／刷新缓存下都有实际使用率，保留物理盘休眠调度。 | [DiskManager](../../src/System/HardwareServices/DiskManager.cs) |
| F07 / P2，上游已有适用修复 | 精确移植上游 `a71ce7584d892230d157c02047bf052fad6a738f` 的 Stock 规则；先处理美股 `us.`，再清后缀，`us.NDX` 得到 `usNDX`。 | [Stock.json](../../resources/plugins/Stock.json) |
| F08 / P2，上游格式补丁未完整接入 | 真实网页快照把 UnitPanel 同时传给数值和显示单位；自定义 `%` 与桌面一致，进度和状态仍按原始值，FPS／DASH 合同保留。 | [LiteWebServer](../../src/System/WebServer/LiteWebServer.cs) |
| T09，Release 测试误报 | IL 检查关联真正的字段／局部槽接收者，兼容局部地址，拒绝静态同名 Dispose 和只释放其他对象的反例；原请求、响应、JsonDocument 检查保留。 | [HttpLifecycleTests](../../tests/LiteMonitor.Tests/HttpLifecycleTests.cs)、[IlInspector](../../tests/LiteMonitor.Tests/IlInspector.cs) |

点分实例隔离采用[完整键归属](../../src/Plugins/PluginKeyOwnership.cs)与[精确删除入口](../../src/System/InfoService/InfoService.cs)，[MonitorSync](../../src/Plugins/PluginMonitorSyncService.cs)复用同一规则。测试覆盖 A／A.B 及 A／AB 的更新、输出替换和移除，检查邻居值、标签／单位、MonitorItems、缓存及原计时器。

市场交叉回归从公开 Uninstall 进入：afterRecord 后锁住真实备份，使恢复保持 pending；卸载必须拒绝并保留新版文件／记录及材料。解除锁并恢复后可卸载、重装。journal 独占锁也不能被当作“没有事务”。

## 独立实现复审

| 范围 | 最终结论与报告 |
| --- | --- |
| GPU／指标 | [PASS](./2026-10-24-remediation-code-review-gpu-metrics.md) |
| 市场／共享保存／恢复 | [PASS，MKT-R02 定向关闭](./2026-10-24-remediation-code-review-market.md) |
| 插件调度／主机／键归属 | [PASS，P2-PLUG-01 定向关闭](./2026-10-24-remediation-code-review-plugins.md) |
| T09／持续门禁 | [PASS](./2026-10-24-remediation-code-review-validation.md) |

报告保留首轮 REQUEST_CHANGES 作为审查过程记录，以各自末尾定向复核的最终 PASS 为准。复审者只写报告，实际构建／测试由主控统一执行；四份报告记录了所审文件哈希。

## 运行验收

证据来源为本地 Windows 工作树。以下为结构化 TRX 的实际计数：

| 配置 | 程序集 | 总计 | 通过 | 失败 | 跳过 |
| --- | --- | ---: | ---: | ---: | ---: |
| Debug | LiteMonitor.Tests | 1739 | 1738 | 0 | 1 |
| Debug | LiteMonitor.Updater.Tests | 73 | 73 | 0 | 0 |
| Debug | LiteMonitor.GpuNonperformance.Tests | 19 | 19 | 0 | 0 |
| **Debug 合计** | | **1831** | **1830** | **0** | **1** |
| Release | LiteMonitor.Tests | 1739 | 1738 | 0 | 1 |
| Release | LiteMonitor.Updater.Tests | 73 | 73 | 0 | 0 |
| Release | LiteMonitor.GpuNonperformance.Tests | 19 | 19 | 0 | 0 |
| **Release 合计** | | **1831** | **1830** | **0** | **1** |

补丁后的定向回归为 214 通过、0 失败、0 跳过，是上述全量的子集。最终仅调整两个工作流的实测阈值／说明后，受影响门禁测试又分别在 Debug、Release 各通过 14 条。

用例身份逐个对账：原 1632 条全部保留，缺失 0；Debug 与 Release 发现集合一致。新增 199 条的组成如下，避免把过滤复测计入新增数量：

| 新增来源 | 数量 |
| --- | ---: |
| ReviewGpuRecoveryRegressionTests | 35 |
| ReviewMarketPersistenceRegressionTests | 33 |
| ReviewMetricPresentationRegressionTests | 56 |
| ReviewPluginHostNormalizationTests | 36 |
| ReviewPluginReloadRegressionTests | 15 |
| HttpLifecycleTests 新增接收者／槽号用例 | 13 |
| ReviewReleaseValidationGateTests | 11 |
| **合计** | **199** |

## 门禁、探针和启动

- CI／release 均实际执行 Debug 和 Release，按配置／程序集隔离 TRX，分别处理测试与门禁的失败退出码。
- 两工作流 `MIN_TEST_TOTAL` 同步从 1632 提高为 **1831**，其余必需程序集、`MAX_SKIPPED_TESTS=1`、市场契约至少 6 条保持收紧。两配置的计数门禁均实际通过，市场契约均为 6 通过、0 跳过。
- 跳过身份精确限定为 `LiteMonitor.Tests.MarketE2ETests.端到端_拉索引_安装_校验_卸载`。单个非 E2E、错误程序集同名项、跳过计数／明细不一致、市场契约跳过或缺失、Release 单独失败均有拒绝反例。
- 独立正向探针重新调用生产方法：多轮清理闸门解除；GPU 失败保留 Presented／重建判据；IPv6 等价地址获准；更新执行新 URL、卸载调度归零；真实 settings 文件锁使安装失败且无孤儿文件／记录；逻辑盘使用率非空；Stock 得到 `q=usNDX`；真实网页行得到 `50.0`／`%`。八项均通过。
- Release 探针另外记录了 6 处 Dispose 与 JsonDocument 局部变量布局；真正的三类资源释放断言在完整 Debug／Release 中通过，未以单纯 Dispose 数量替代该断言。
- 生产 MainForm 构造检查实际退出码 **0**，`startup-ok.json` 为 PASS、`webServerRunning=false`；该场景不运行 OnShown。
- `git diff --check` 通过。最终 31 个代码／测试／配置文件的 SHA-256 已留存，原项目的既有未跟踪 `%SystemDrive%/`、`undefined/` 保留。

## 验证边界

1. 本轮覆盖状态机、WinForms 构造、真实文件锁、确定性并发屏障及受控传输边界；真实 GPU 像素、设备移除／驱动故障和实网 IPv6 通信仍需相应实机验收。市场联网 E2E 是唯一按合同跳过的用例。
2. NOTICE／实际依赖检查通过，直接和传递依赖均缺失 0、版本不符 0、多余 0。**随包预构建更新器的新鲜度仍为 NOT_VERIFIED**：`resources/LiteMonitor++.Updater.exe` 的 SHA-256 为 `5D1AE3FD45ACC170DF01E49334BEEEDB875024102A9B73670BA5A960D179A036`，内嵌提交 `366bf9c` 与当前 updater 源码存在差异，不能由时间戳证明内容一致。发布前应通过既有打包流程重建并重新核对；Updater 源码测试 73 通过不等于该二进制已验明。
3. 原报告的存量明文备份一次性清理、LAN 同时清口令体验及旧客户端手动下载说明仍属于既有待办，本批范围和通过结论不包含这些策略变更。

## 证据入口

汇总与精简证据位于 `docs/reviews/evidence/`（随仓库保留的证据区；本轮对应的 `remediation-20261024-9238bb9` 快照位于当时的独立复核工作树，未随仓库入库）。

> ★ **证据可及性说明（2026-10-24 工作目录清理时补记）**
>
> 本报告原引用的两处证据目录都属于**本地临时工作区**，从未进入版本库：
>
> - `docs/reviews/evidence/remediation-20261024-9238bb9/` —— 位于当时的独立复核工作树；
> - `.review-scratch/remediation-20261023/validation/` —— 六份完整 TRX 与构建／测试日志，
>   位于按项目规则**本地忽略**的临时目录。
>
> 两者已随工作目录清理移除，无法再从仓库内直接取到。保留在仓库里的证据区
> （`docs/reviews/evidence/`）包含同期的 `full-project-20261023-9238bb9/` 快照
> （门禁计数、探针结果、启动自检与测试汇总），可作为同批次的旁证；
> 结论以本报告正文为准。
