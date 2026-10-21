# 性能改造安全合并计划

## 1. 推荐路线

**提交最后的 R1 修复 → 保存证据与备份 → 锁定主线和功能提交 → 独立集成分支生成合并提交 → 对最终提交验收 → 清理主检出的路径阻塞 → 主线快进到已验收提交 → 观察后续工作线。**

功能分支进入集成分支时使用 `--no-ff`，保留一个可整体回滚的合并提交；主线接收集成结果时使用 `--ff-only`，确保接收的就是已验收提交。

本计划针对本地 fork 的 `master`，远端为 `origin`（mojingjing-git/LiteMonitor）。上游 `upstream` 的更新另行集成。

## 2. 已核实的起点

| 项目 | 当前状态 |
|---|---|
| 主检出 | `G:/TOOL/LiteMonitor-fork` |
| 主线 | `master` @ `949835f7b0a7f8a2f888dd6965716b4a077b6d3f` |
| 本地缓存的 `origin/master` | 与本地 master 相同；本次未联网刷新，执行时必须 fetch |
| 性能工作树 | `G:/TOOL/LiteMonitor-fork/.worktrees/codex-performance-sampling` |
| 性能分支已提交 HEAD | `codex/performance-sampling` @ `15e1fc64f1b5ac740c41c0ab61f0d2d0c1b0c8a9` |
| 分叉情况 | 主线独有 0 提交，功能分支独有 42 提交；主线是功能分支祖先 |
| 尚未提交的最终修复 | `FpsCounter.cs`、`FpsStateTests.cs`，共 +82 / −14 |
| 最终修复验证 | 隔离副本强制重建后 467/467 通过；旧实现的新增用例 2 红 / 1 绿，修复后通过 |
| 主检出工作状态 | 已跟踪文件干净；核查时有 7 个未跟踪文件，新增本计划后为 8 个 |

两个待提交文件的当前 Git blob，用于确认提交的正是已验证补丁：

- `src/System/HardwareServices/FpsCounter.cs`：`e519409ca2e6157c5270c9a170b6203ada1e1692`
- `tests/LiteMonitor.Tests/FpsStateTests.cs`：`d0ffe74928fc1706d1f315a00af7262bf5fa3e0b`

详细证据见 [最终修复验证记录](G:/TOOL/LiteMonitor-fork/.review-scratch/performance-admission-fix/verification.md)。

### 主检出的同路径未跟踪文件

这些路径会被功能分支纳入跟踪，推进主线之前应先保全并移出原位置：

| 路径 | 当前大小 | 与功能分支关系 | 处理 |
|---|---:|---|---|
| `docs/superpowers/plans/2026-10-20-performance-sampling-hardening.md` | 35,574 B | Git blob 完全相同 | 备份后移出，由合并引入跟踪版本 |
| `src/System/HardwareServices/HardwareSamplingCoordinator.cs` | 0 B | 外部工作线的空占位；功能分支有正式实现 | 备份后移出，保留功能分支实现 |
| `tests/LiteMonitor.Tests/WebDemandTests.cs` | 0 B | 外部工作线的空占位；功能分支有正式测试 | 备份后移出，保留功能分支测试 |

以上是当前快照。移动前重新计算大小和哈希；若其他工作线已填入内容，停止自动处理，先由文件 owner 确认迁移方式。

## 3. 阶段 A：提交并冻结交付版本

1. 协调其他 Agent，暂停对性能工作树和共享交叉文件的写入，登记当前 owner。此时独立模块的只读分析可以继续。
2. 检查性能工作树只有上述两个预期修改，运行 `git diff --check`，比对文件 blob。
3. 只暂存并提交这两个文件，建议提交说明：`fix(fps): make startup admission atomic with lifecycle changes`。
4. 如需归档账本，另做文档提交：整理最终简报、复审结论、红绿验证命令、版本、结果及限制到 `docs/`。更新“42 提交 / 464 测试 / R1 尚未关闭”等过时表述。
5. 本地 `.superpowers/` 账本可保留完整副本；`.review-scratch/` 的克隆、bin/obj 和临时产物继续作为本地证据。若需入库原始日志，先选定必要文件并检查机器路径、配置和秘密信息。
6. 记录最终功能提交 SHA；归档提交完成后再冻结版本，后续验证和合并均引用该 SHA。

分步命令示例：

```powershell
$repoRoot = 'G:\TOOL\LiteMonitor-fork'
$performanceRoot = Join-Path $repoRoot '.worktrees\codex-performance-sampling'

git -C $performanceRoot status --short
git -C $performanceRoot diff --check
git -C $performanceRoot hash-object src/System/HardwareServices/FpsCounter.cs
git -C $performanceRoot hash-object tests/LiteMonitor.Tests/FpsStateTests.cs

git -C $performanceRoot add -- src/System/HardwareServices/FpsCounter.cs tests/LiteMonitor.Tests/FpsStateTests.cs
git -C $performanceRoot commit -m 'fix(fps): make startup admission atomic with lifecycle changes'
# 可选的证据归档提交在此完成。
$featureCommit = (git -C $performanceRoot rev-parse HEAD).Trim()
```

**放行条件：** 最终 R1 修复已进入提交，预期文件和归档均可追溯，性能工作树已跟踪文件干净。每个原生命令都检查退出码；失败即停，不能把整份命令当作无条件连续脚本。

## 4. 阶段 B：刷新基线并建立恢复点

1. 记录主检出未跟踪文件清单，按原路径复制备份，保存 SHA-256 清单。包括配置备份 ZIP、其他工作线的研究/计划文件。
2. 如需实机启动应用，另备份当前配置和历史数据；验收使用独立输出目录及配置副本。
3. 执行 `git fetch origin`，检查 `master...origin/master`：
   - 相同：继续。
   - 本地仅落后：先处理会阻塞更新的本地文件，再通过 `--ff-only` 更新 master，重新确定集成基线。
   - 本地领先或分叉：列出提交并确定需要接收的主线版本，停止自动合并流程。
4. 记录 `$mainCommit`，给它和 `$featureCommit` 建立唯一命名的本地安全 tag，例如 `safety/master-before-performance-<时间>`、`safety/performance-ready-<时间>`。
5. 恢复点记录中同时写入：提交 SHA、tag、未跟踪文件备份位置、配置备份位置。Git tag 本身不包含未提交/未跟踪/ignored 文件。

**放行条件：** 主线基线来自刷新后的判断，有恢复点及文件备份；没有把其他工作线的代码夹带进性能提交。

## 5. 阶段 C：在独立集成分支生成最终提交

从锁定的主线提交建立 `codex/integrate-performance-sampling` 和独立工作树。目录/分支若已存在，先检查用途，不能覆盖或重置。

```powershell
$mainCommit = (git -C $repoRoot rev-parse master).Trim()
$integrationRoot = Join-Path $repoRoot '.worktrees\codex-integrate-performance-sampling'

git -C $repoRoot worktree add -b codex/integrate-performance-sampling $integrationRoot $mainCommit
git -C $integrationRoot merge --no-ff $featureCommit -m 'merge: performance sampling hardening'
$mergeCommit = (git -C $integrationRoot rev-parse HEAD).Trim()
```

核对：

- 合并提交第一父提交必须是 `$mainCommit`，第二父提交必须是 `$featureCommit`。
- 按当前无分叉状态，合并结果树应与功能提交完全相同。用 `git diff $featureCommit $mergeCommit --` 核对。
- 若 fetch 后主线已前进，逐项审查差异，任何冲突在集成分支解决；解决后的实际合并提交必须重新验收。
- 不使用整文件 ours/theirs 来覆盖 `Settings.cs`、语言包或 Web 目录的功能。按文件 owner 逐项合并设置字段、UI 入口、文案和生命周期接线。
- 若冲突超出预定范围，使用 `git merge --abort` 返回集成起点，保留原性能分支和主检出。

**放行条件：** 合并提交及两个父提交明确，差异范围符合预期，集成工作树干净，主检出此时仍未接收性能代码。

## 6. 阶段 D：验收最终合并提交

### 自动化验收

在独立集成工作树或从其提交建立的纯净 clone 中执行，避免与其他 Agent 共用 bin/obj：

```powershell
dotnet build tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-incremental
dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj -c Release --no-build
git diff --check $mainCommit $mergeCommit
```

每个命令单独检查退出码。构建成功后才运行 `--no-build`；测试后记录被测 SHA、工作树状态、DLL SHA-256 和测试结果。若文件有任何变化，重新强制构建。

当前预期是 **467/467，0 失败、0 跳过**；如果新基线增加了合法测试，以明确的新总数为准，不能通过少跑测试满足数量。

另核查最终提交仍包含原先 R1/R2/R3 的回归，以及最后 3 个准入时序测试。

### 实机冒烟

安排一个验收进程独占实机采集资源，使用独立目录和配置副本，避免污染用户日常运行配置：

| 场景 | 验收点 |
|---|---|
| 普通启动、默认 1000 ms | 面板与任务栏正常，数据持续更新，关闭能退出 |
| UI 100 ms | UI 刷新设置有效，普通硬件稳态采集约 1 次/秒 |
| Web 开启但无人访问 | 不因为开启服务器就强制全量采集 |
| 已鉴权网页、HTTP 快照 | 隐藏普通指标仍可显示，UI/Web 共用采集入口 |
| Web Stop/Start、断线重连 | 重连恢复，旧广播不向新连接发送 |
| FPS 本地/网页开关 | 需求独立生效，网页开关默认关闭；无可信数据时占位合理 |
| FPS 开启后关闭程序 | 在环境具备现成组件和权限时检查实际进程退出、无遗留 |
| 多卡、网络与磁盘 | 已配置 GPU 均更新，设备切换正常，流量没有明显突增或负增量 |

不为这次合并自动下载组件或安装驱动。无法实测的 PresentMon/ETW 场景如实记录为未验证；它是实际 FPS 验收的剩余限制，不能由假启动器测试替代。若此轮就发布并宣传 FPS 生命周期完全验证，应先补齐该实测。

之前 87.29% 的 CPU 降幅来自提交内历史采集实验，不自动变成本次合并提交的实测结论。没有代码冲突及采样逻辑变化时，本次无需机械重跑全部三轮性能矩阵；若冒烟出现异常或冲突修改了采样路径，再复跑相关组。

**放行条件：** 自动化全部通过、关键冒烟通过、未验证边界已登记；只验收最终提交，不能用功能分支旧版本的绿灯代替。

## 7. 阶段 E：主线接收已验收提交

1. 再次确认主线 HEAD 仍等于 `$mainCommit`，集成分支 HEAD 仍等于 `$mergeCommit`。主线若有新提交，返回阶段 C，在新基线上重新集成和验收。
2. 再次计算“主线到集成提交新增路径”与“主检出未跟踪路径”的交集。当前至少需要处理第 2 节的 3 个路径；归档可能增加新交集。
3. 对照已保存的大小/哈希清单，将这 3 个文件移动到备份目录并保持相对路径。移动源和目标均应解析为绝对路径，确认源属于主检出、目标属于本次备份目录，保留可恢复副本。
4. 其他工作线的研究和计划文件、用户 ZIP 留存；不要为通过 merge 批量清空工作目录。
5. 在主检出执行：

```powershell
git -C $repoRoot merge --ff-only $mergeCommit
git -C $repoRoot rev-parse HEAD
git -C $repoRoot status --short --branch
```

6. 核对 master 精确指向 `$mergeCommit`，已跟踪文件干净。对先前 3 个冲突路径执行 `git ls-files --`，确认现在是功能分支带入的跟踪版本；核对非预期文件没有被暂存/提交。

**放行条件：** 主线就是已验收的合并提交，文件备份仍在，其他工作线内容没有丢失。

## 8. 远端、发布与其他工作线

- 本地合并、推送、发布分别记录完成状态。需要推送时使用正常 `git push origin master`；若远端拒绝推进，fetch 并重新检查，不能强推覆盖。
- 如果仓库要求 PR，则推送集成分支，经 PR 保留 merge commit 合并；重新记录平台实际生成的主线 SHA，确认其内容与验收树一致。平台生成不同代码树时重新验证。
- 计划不把合并等同于发版。打包脚本会构建 Updater 并写入相应资源，执行时使用独立打包工作树并核对是否产生已跟踪变更，再单独审查发布产物。
- 验收稳定后，再通知后续工作线使用新的 master SHA。新任务从新主线开始；已有提交的工作线合并新 master，按实际情况处理冲突；已有未提交改动的先保存并确认 owner。
- 核心交叉文件由单一 owner 接线：`HardwareMonitor.cs`、采集协调器、缓存与生命周期；`Settings.cs`；`SystemHardwarPage.cs` 和 9 个语言包；`src/System/WebServer/`。对照 P00/P03/P07/P11/P12/P13 的真实写入范围安排先后。
- 三个原占位文件的备份不应整体覆盖回新主线。若外部工作线已有新增内容，作为独立补丁合入正式实现。

## 9. 回滚

### 主线接收前

主检出仍在旧主线，失败的集成结果留在独立工作树。修复后重新生成/验收提交；进行中的冲突可 `git merge --abort`。恢复主检出被移走的未跟踪文件前，确认对应路径尚未成为跟踪文件且没有其他人的新内容。

### 主线接收后

以合并提交为单位回滚：

```powershell
git revert -m 1 <本次合并提交SHA>
```

`-m 1` 的前提是已确认第一父提交为旧主线。先在恢复分支完成 revert、构建及回归，再将恢复提交推进主线。该恢复提交回到旧主线的代码行为，测试数量应按恢复后的版本确定。

若其他工作线已经合入并依赖新接口，先评估依赖，选择修复或连同依赖一起恢复，不能直接回滚底层后继续发布。恢复 Git 代码也不等于恢复用户配置，因此配置备份单独管理。

## 10. 执行完成清单

- [ ] 最终 R1 补丁已提交，功能提交 SHA 已锁定。
- [ ] 主线已 fetch 并确认基线，恢复 tag 和文件备份可用。
- [ ] 三个同路径未跟踪文件已保全，外部工作线 owner 已登记。
- [ ] 最终合并提交父提交、代码树、差异范围正确。
- [ ] 强制重建成功，当前全量至少包含预期 467 条测试且全通过。
- [ ] 关键冒烟完成，真实 FPS/ETW 等缺口据实披露。
- [ ] master 精确指向已验收提交，工作树无非预期跟踪修改。
- [ ] 推送/PR/发版状态分别登记，后续工作线的新基线明确。
- [ ] 观察期结束前保留性能分支、集成分支、工作树和恢复证据。
