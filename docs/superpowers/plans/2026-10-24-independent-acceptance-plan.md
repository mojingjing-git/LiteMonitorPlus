# LiteMonitor 修复批次独立验收计划

日期：2026-10-24（Asia/Shanghai）。版本：v1。状态：**待独立 Agent 执行，本文件不代表验收已通过。**

## 1. 验收目标与结论口径

验证本批 F01～F08、T09、持续门禁，以及实施复审补修的 P2-PLUG-01、MKT-R02：缺陷是否真正修复，原有行为是否保留，测试和发布门禁是否会对失败报红。验收者自行生成新证据；已有报告和旧 TRX 只用于核对交付范围、历史用例身份和预期结果。

分开给出三个结论，不能以一项代替另一项：

| 结论 | 必需条件 | 允许的表述 |
| --- | --- | --- |
| 代码验收 CODE | A00～A08 全部通过，关键反例能识别缺陷，冻结源码无漂移，无未关闭 P1/P2 | `CODE_PASS` |
| 实机验收 GPU | G01～G03 有符合条件的真实窗口、像素和 OS 回读证据 | `GPU_PASS`；条件不足为 `ENV_BLOCKED` / `NOT_VERIFIED` |
| 发布候选验收 PACKAGE | R01～R04 通过，明确新包来源、更新器字节和行为 | `PACKAGE_PASS`，只适用于验明的新包 SHA-256 |

只有三项全部通过，才可写“本计划范围内完整验收通过”。代码通过但实机或发布候选未验明，应写 **`CODE_PASS / RELEASE_NOT_CLEARED`**，逐项列明缺口。代码必验项缺失、失败或无法判断，均不能给 CODE_PASS。环境阻塞与产品缺陷分开登记，不降低标准。

本批不包含“旧明文备份一次性清理”“LAN 切仅本机并同时清口令的体验调整”“旧客户端升级提示策略重做”。这些既有待办不能被本次结论自动关闭。若打包作为正式发布候选，仍应核对旧客户端需手动下载的发布说明。

## 2. 候选位置、基线和参考材料

**实际修复源码在以下工作树，全部修复尚未提交：**

```text
C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork
branch: codex/review-remediation-20261023
HEAD:   9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f
```

`G:\TOOL\LiteMonitor-fork` 只有原始源码及报告副本。**不得在该目录直接验收修复，也不得只从分支名新建 worktree / clone 后声称在验收本批修复。** 仅检出 HEAD 会漏掉全部未提交实现和新增测试。

参考材料：

- [修复计划 v3](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/docs/superpowers/plans/2026-10-23-independent-review-remediation-plan.md)。
- [原始独立审查报告](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/docs/reviews/2026-10-23-full-project-regression-and-upstream-review.md)。
- [修复与既有验证报告](C:/Users/wrz20/.codex/worktrees/review-remediation-20261023/LiteMonitor-fork/docs/reviews/2026-10-24-remediation-validation.md)。
- 四份 `docs/reviews/2026-10-24-remediation-code-review-*.md`：关注末尾最终结论；市场和插件报告保留了早期 REQUEST_CHANGES，不应截取首轮结论代替最终复核。
- 交付的 31 文件清单：`docs/reviews/evidence/remediation-20261024-9238bb9/source-sha256.json`。清单本身 SHA-256 必须是 **`A33B8A76829C3381939FB19682E2C3D7FB29B6E9AE82209FC59DD2EAFAC4750F`**。

## 3. 执行纪律和分工

1. 验收 Agent 只写新验收脚本、临时夹具、证据和报告；不直接修改候选生产代码、既有测试、项目文件或 CI 阈值。发现缺陷，先保留失败证据并报告，不边修边把结果改成通过。
2. 所有构建、测试和运行探针在同一工作树内串行。可以并行安排只读代码复核，但只有一个执行者拥有构建输出和共享单例的运行时段。
3. 不执行 `git reset`、`git clean`、切换分支、stash、提交、推送、合并、发布或签名。保留原目录既有 `%SystemDrive%/`、`undefined/`；不删除旧证据。
4. 每次验收生成唯一运行目录，日志、TRX 和 JSON 均使用本次路径；不能复用旧结果文件。测试所需配置、插件安装和文件锁必须落在专用临时目录，不能使用用户正在运行的安装目录。
5. 失败反例的源码变异仅允许在完整导出的副本中进行。副本必须含当前未提交修改和新增文件，禁止对候选源码临时回退后再恢复。
6. NuGet 还原可联网；默认市场测试使用本地夹具。不得启动针对用户安装的更新器、安装驱动、禁用显示适配器或改变用户桌面的全局设置。
7. 长任务期间至少每 60 秒说明有无进展；单项超时保留日志，不能把超时、异常、没有发现用例当作通过。

推荐由一位验收主 Agent 顺序执行；若用户安排多位 Agent：GPU、插件、市场、指标/上游可以分别只读复核 A03，运行由主 Agent 排队，最后由主 Agent 汇总。不要让不同 Agent 同时运行同一工作树的测试。

## 4. A00：身份、交付完整性与冻结

使用 Windows x64、PowerShell 7、能构建 `net8.0-windows` 的 .NET SDK。记录 OS、SDK、运行时、当前用户是否拥有交互式桌面，以及实际工具路径。不要打印整份环境变量或用户凭据。

以下代码在 **同一个 PowerShell 7 会话**中顺序运行；后文沿用变量和函数。可由验收 Agent 保存为本次运行的驱动脚本。不得把运行目录改为旧验证目录。

```powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$Root = 'C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork'
Set-Location -LiteralPath $Root
$baseHead = '9238bb9961e6f1dc4aa9cfaf7c1311c360bf734f'
$head = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $baseHead) { throw '候选 HEAD 已改变，需重新界定基线' }
$branch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'codex/review-remediation-20261023') {
    throw '工作树或分支不符'
}
$Dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$Pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0,8)
$Run = Join-Path $Root ".review-scratch/acceptance-$runId"
if (Test-Path -LiteralPath $Run) { throw '运行目录已存在，禁止覆盖证据' }
New-Item -ItemType Directory -Path $Run | Out-Null

function Invoke-CheckedNative {
    param([string]$Exe, [string[]]$Arguments, [string]$Log)
    & $Exe @Arguments 2>&1 | Tee-Object -FilePath $Log
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "命令失败，exit=$code；日志：$Log" }
}

$manifestPath = Join-Path $Root 'docs/reviews/evidence/remediation-20261024-9238bb9/source-sha256.json'
$manifestDigest = 'A33B8A76829C3381939FB19682E2C3D7FB29B6E9AE82209FC59DD2EAFAC4750F'
if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $manifestDigest) {
    throw '交付清单缺失或变化，不能自行重写期望值'
}
$delivered = @(Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json)
if ($delivered.Count -ne 31 -or @($delivered.Path | Sort-Object -Unique).Count -ne 31) {
    throw '交付清单不是 31 个唯一文件'
}
foreach ($entry in $delivered) {
    $path = Join-Path $Root $entry.Path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.SHA256) {
        throw "交付源码不符：$($entry.Path)"
    }
}
$changed = @(& git -c core.quotepath=false diff --name-only HEAD --)
if ($LASTEXITCODE -ne 0) { throw '无法读取 tracked 差异' }
$untracked = @(& git -c core.quotepath=false ls-files --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw '无法读取新增文件' }
$changedCode = @(@($changed + $untracked) | Where-Object { $_ -notmatch '^docs/' } | Sort-Object -Unique)
if (@(Compare-Object ($delivered.Path | Sort-Object) $changedCode).Count -ne 0) {
    throw '非文档改动集合不再等于交付的 31 文件，停止套用旧验收范围'
}

function Get-CandidateInventory {
    $names = @(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法枚举候选文件' }
    @($names | Where-Object { $_ -notmatch '^docs/' } | Sort-Object -Unique | ForEach-Object {
        [pscustomobject]@{
            Path = $_
            SHA256 = (Get-FileHash -LiteralPath (Join-Path $Root $_) -Algorithm SHA256).Hash
        }
    })
}
$frozen = @(Get-CandidateInventory)
$frozen | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Run 'candidate-inventory.json') -Encoding utf8
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $Run 'delivered-source-sha256.json')
& git status --porcelain=v1 | Set-Content -LiteralPath (Join-Path $Run 'candidate-status.txt') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'git status 失败' }
& git diff --binary "--output=$Run/candidate-tracked.patch" HEAD --
if ($LASTEXITCODE -ne 0) { throw '导出 tracked 补丁失败' }
Invoke-CheckedNative -Exe $Dotnet -Arguments @('--info') -Log (Join-Path $Run 'dotnet-info.log')
Invoke-CheckedNative -Exe $Pwsh -Arguments @('--version') -Log (Join-Path $Run 'pwsh-version.log')
Invoke-CheckedNative -Exe 'git' -Arguments @('diff','--check') -Log (Join-Path $Run 'diff-check.log')
```

另存 `run-metadata.json`：运行 ID、开始/结束时间与时区、Root、HEAD、分支、工具绝对路径、交付清单摘要、完整清单摘要、环境说明。结束时重新枚举文件集合和 SHA-256，与 `$frozen` 按 Path/SHA256 逐项比较，并重新核对 HEAD、分支、31 文件清单。新增/删除文件也属于漂移。

只核对 HEAD、修改时间或 31 文件而忽略其他编译输入，不足以完成冻结。文档、日志和已忽略的构建输出允许变化；源码、项目、资源、脚本和夹具的变化必须重新评估并重跑受影响验收。

通过标准：身份正确、31 文件与交付一致、无其他未登记非文档改动、完整输入清单已冻结。若交付目录/清单不可访问，记 `SCOPE_BLOCKED`，不能改在主目录验收原版。

## 5. A01：全新构建与双配置全量测试

本节实际执行还原、重建，再测试，不能只对上次遗留 DLL 执行 `--no-build`。保持默认联网 E2E 关闭，并使用仓库夹具：

```powershell
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:LITEMONITOR_PLUGIN_REPO = Join-Path $Root 'tests/fixtures/plugin-repo'
Remove-Item Env:LITEMONITOR_E2E -ErrorAction SilentlyContinue
Invoke-CheckedNative -Exe $Dotnet -Arguments @('restore','LiteMonitor.sln','--nologo') -Log (Join-Path $Run 'restore.log')
$projects = @(
    'LiteMonitor.Tests',
    'LiteMonitor.Updater.Tests',
    'LiteMonitor.GpuNonperformance.Tests'
)
foreach ($config in @('Debug','Release')) {
    Invoke-CheckedNative -Exe $Dotnet -Arguments @(
        'build','LiteMonitor.sln','-c',$config,'--no-restore','-t:Rebuild','--nologo'
    ) -Log (Join-Path $Run "build-$config.log")
    foreach ($assembly in $projects) {
        $trxDir = Join-Path $Run "$config/$assembly"
        New-Item -ItemType Directory -Path $trxDir | Out-Null
        Invoke-CheckedNative -Exe $Dotnet -Arguments @(
            'test',"tests/$assembly/$assembly.csproj",'-c',$config,
            '--no-build','--no-restore','--nologo',
            '--results-directory',$trxDir,'--logger','trx;LogFileName=results.trx'
        ) -Log (Join-Path $Run "test-$config-$assembly.log")
    }
    Invoke-CheckedNative -Exe $Pwsh -Arguments @(
        '-NoProfile','-File','.github/workflows/assert-test-counts.ps1',
        '-TrxRoot',(Join-Path $Run $config),'-MinTotal','1831',
        '-ExpectedAssembly',($projects -join ','),'-MaxSkipped','1',
        '-MustExecuteTestClass','MarketIndexContractTests','-MustExecuteTestClassMinCount','6'
    ) -Log (Join-Path $Run "gate-$config.log")
}
```

该片段按首次失败立即中止依赖运行。保留失败日志、退出码、已产生 TRX；完成不依赖失败项的只读复核，最终给出失败或环境阻塞，不能补一份空白通过报告。

每配置预期结果如下。固定候选出现“比预期更多”也应调查文件/发现集合变化，不能仅因超过下限就忽略差异。

| 程序集 | 总计 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: | ---: |
| LiteMonitor.Tests | 1739 | 1738 | 0 | 1 |
| LiteMonitor.Updater.Tests | 73 | 73 | 0 | 0 |
| LiteMonitor.GpuNonperformance.Tests | 19 | 19 | 0 | 0 |
| **每配置合计** | **1831** | **1830** | **0** | **1** |

唯一允许跳过的身份必须同时满足主程序集 `LiteMonitor.Tests` 和完整用例名：

```text
LiteMonitor.Tests.MarketE2ETests.端到端_拉索引_安装_校验_卸载
```

`MarketIndexContractTests` 必须 6 条执行、0 跳过。默认环境下 E2E 意外执行，先确认为什么，再解释计数；单独开展联网 E2E 时，1831 通过/0 跳过可以通过，但必须另外留证，不能覆盖默认运行结果。不得增加 Skip、过滤全量用例或把阈值降回 1632。

保存本次主程序、更新器、三个测试程序集、GPU 启动探针的产物路径和 SHA-256，绑定到对应配置的构建日志。TRX 结果必须对应刚构建的本次产物。

## 6. A02：TRX 真实性、身份与新增用例对账

验收 Agent 在 `$Run` 下编写结果汇总器，直接解析 XML，不从中英文控制台摘要提取数字。用 `local-name()` 兼容 TRX 命名空间。

必须同时验证：

1. 本次 Debug/Release 各三个程序集、各一份结果，六份 TRX 均存在且来自本次运行。不得把两个配置混在一个门禁根目录后由“最新结果”代替另一份。
2. `Counters.total` 等于实际 `UnitTestResult` 条数；通过/失败/NotExecuted 明细与各计数一致，没有 Unknown、Aborted、Timeout 等被漏记结果。读取程序集定义，确认用例确属对应程序集，不能只信文件夹名。
3. 固定候选每配置精确 1831，失败 0，跳过身份符合 A01；同一程序集内用例身份无意外重复。
4. 以“程序集 + 完整用例名/参数化用例显示名”为身份，逐项核对 Debug/Release 集合相同。不要比较每次都变化的 executionId。
5. 从原始独立审查的 Debug TRX 读取 1632 个历史身份，确认在本次两个配置中均保留。旧 Release 有 T09 误报，不把其旧结果作为本次行为判据。

原始身份基线位置：

```text
G:\TOOL\LiteMonitor-fork\.review-scratch\review-20261023-independent\source\tests\<程序集>\TestResults\independent-review-debug.trx
```

若旧 TRX 缺失，保留“历史身份对账未完成”，不能只凭总数相等声称原 1632 条全部保留。已有 `.review-scratch/remediation-20261023/summarize-verification.ps1` 可读作参考；它有固定输出路径，应复制改成写入 `$Run`，不能原样覆盖旧汇总。

本批新增身份应为 199 条，以下是每配置的发现数：

| 来源 | 新增 | 当前该类总数 |
| --- | ---: | ---: |
| ReviewGpuRecoveryRegressionTests | 35 | 35 |
| ReviewMarketPersistenceRegressionTests | 33 | 33 |
| ReviewMetricPresentationRegressionTests | 56 | 56 |
| ReviewPluginHostNormalizationTests | 36 | 36 |
| ReviewPluginReloadRegressionTests | 15 | 15 |
| HttpLifecycleTests | 13 | 28 |
| ReviewReleaseValidationGateTests | 11 | 11 |
| **合计新增** | **199** | |

输出 `test-summary.json`、`test-identities.json`、`baseline-identity-diff.json`。过滤复测、独立探针、变异测试属于额外证据，不能再累加到 1831 或 199。

## 7. A03：逐缺陷行为验收矩阵

全量运行后，验收者阅读下表对应测试和生产调用链，将每行关联到本次 TRX 的具体用例身份。不能只写“该测试类全过”。需要补充独立探针时放在 `$Run` 的外部工程，引用候选生产工程，不改候选测试集合。

### 7.1 GPU 状态与责任清理

| 编号 | 操作/边界 | 必须观察到的结果 | 主要测试类 |
| --- | --- | --- | --- |
| F01-1 | 同 HWND 依次记录 Bound/PresentAttempted/Presented，再记录低状态 | 历史不倒退；生产 `RequiresControlledRecreate` 保持正确 | ReviewGpuRecoveryRegressionTests |
| F01-2 | 已 Presented，替换宿主绑定前失败，经生产 `ScheduleGpuFailureFallback` | 历史仍 Presented；回退已入队且需要受控重建 | 同上 |
| F01-3 | 冷启动始终 Unbound 就失败 | 不因修复而一律要求重建；保留原原因码 | 同上 |
| F01-4 | 先 Advance 真相源，再发新/旧/重复 HandleCreated | 仅真实新代次重置；旧/重复回调不重启宿主；不能只用直接传入 42 的接缝证明 | 同上 |
| F01-5 | 首次创建、隐藏、关闭、重建失败与滞后通知 | 不越过启动/关闭优先级；失败阶段和原因保留 | 同上及既有 GPU 回退类 |
| F02-1 | A 清理耗尽，明确恢复 B 再耗尽，C 最终成功 | 逐轮选中尚未 Reclaimed 的责任；成功后闸门解除，失败审计仍在 | ReviewGpuRecoveryRegressionTests |
| F02-2 | 多轮/多责任/登记冲突/成功后再次失败 | 精确原 affinity/HWND 保留；新责任先登记；冲突不提前接管；每代次最多一项 pending | 同上 |
| F02-3 | 接管期间或之后 HWND 换代 | 不探测旧 HWND，不借新代次抹掉未解责任 | 同上 |

这组证明状态机与调度判据；实机像素和 Windows affinity 写回另见 G01～G03。

### 7.2 插件热加载、迟到回调与键归属

| 编号 | 操作/边界 | 必须观察到的结果 | 主要测试类 |
| --- | --- | --- | --- |
| F03-1 | 同版本改 URL/body/parser/interval/输出/实例输入后 Load | 内容变化只替换受影响实例，执行新快照；邻居任务不重启 | ReviewPluginReloadRegressionTests |
| F03-2 | Start 前 Load、Stop 后 Load、计时器创建中 Stop | 不提前开调度，不复活已停管理器，不安装过时代次计时器 | 同上 |
| F03-3 | 旧请求停在最终发布前，更新/卸载，放行旧成功及异常 | 旧代次不写值/错误/label/unit/color/KeyCache；不能只有 token 预检查 | 同上 |
| F03-4 | 旧缓存过期读取、旧写入驱逐、旧第三次失败、旧调度收尾 | 不删/覆盖/驱逐新缓存，不重置新网络客户端或新失败计数，不 rearm | 同上 |
| F03-5 | Web 始终可见，卸载后强制触发已 Dispose 计时器回调 | 调度归零，不能发布；重新安装维持预期启用状态 | 同上 |
| F03-6 | 两实例共享在途 HTTP，其中一个退役 | 另一等待者成功，共享 transport 不被单方取消；最终等待者离开才释放 | 同上及既有 native 去重测试 |
| PLUG-01 | 合法 ID A/A.B 与 A/AB；分别更新、换输出 schema、移除 A | 仅删 A 的完整归属键；邻居数据/属性/MonitorItems/缓存/原计时器保持；不能仅证明 StartsWith(A+".") | `ParentHotReloadAndRemovalOnlyTouchOwnedCompleteKeysWhileNeighborStaysUnchanged` 的四组参数及生产调用链 |

额外检查发布门与退役共用同步边界，网络 await、CTS.Cancel 和外部观察回调不在该门内。复核 ResetNetworkClients 的旧客户端释放未引入门内阻塞或锁序反转。

### 7.3 市场持久化、补偿与恢复

| 编号 | 操作/边界 | 必须观察到的结果 | 主要测试类 |
| --- | --- | --- | --- |
| F04-1 | 首装/更新时用真实独占文件锁令 settings 写入失败，及 GlobalBlockSave | 公共安装返回失败，内存恢复；磁盘记录和插件文件一致，无孤儿；不能静默成功 | ReviewMarketPersistenceRegressionTests |
| F04-2 | 市场暂态变更期间并发普通 SettingsHelper.Save | 普通保存等在共享保存门外；最终只看见已提交或已恢复状态，其他设置不丢 | 同上 |
| F04-3 | afterRecord 后补偿记录再次写盘失败 | 保留已提交新版文件/记录与 journal/backup，不能先回滚文件制造错配；之后可恢复 | 同上 |
| F04-4 | 补偿记录成功，但必需 Move/Delete 被真实锁阻止 | 不假装回滚完成，不删除重试材料；解锁后可完成，重复恢复幂等 | 同上 |
| F04-5 | 崩溃点/缓存目录已恢复标记后再出现 journal/主记录损坏 | 恢复基于独立磁盘记录，不用活单例、默认值或 backup 冒充提交依据；存在 journal 不能直接跳过 | 同上 |
| F04-6 | journal 字段篡改、ID/事务/哈希不一致、重解析点/路径边界 | 沿用既有拒绝/逐字节隔离规则；不采信日志提供的任意路径 | 同上及既有 PluginMarketRecoveryBoundaryTests |
| F04-7 | 卸载时严格记录移除失败或禁存 | 如实失败并恢复 payload，记录与文件一致 | 同上 |
| MKT-R02-1 | 更新提交后锁住备份，使恢复 pending，再调用公开 Uninstall | 卸载拒绝；新版文件、记录、journal/backup 均保留；解锁恢复后可卸载、重装 | `Uninstall_CannotConsumePayloadOwnedByPendingCommittedUpdate` |
| MKT-R02-2 | 独占锁令 pending journal 不可读，再公开 Uninstall | 不把读失败视作“无事务”；拒绝且不损坏文件/记录；解锁后可恢复 | `Uninstall_CannotTreatUnreadablePendingJournalAsAbsent` |

至少保存一次真实失败样例的临时目录状态清单、文件 SHA-256、磁盘 settings 内容的脱敏摘要、异常/返回值与恢复前后变化。不能只用委托返回 false 替代全部持久化验证。

### 7.4 主机政策与上游行为

| 编号 | 操作/边界 | 必须观察到的结果 | 主要测试类 |
| --- | --- | --- | --- |
| F05-1 | 完整/压缩/前导零/大小写 IPv6；静态 authority 搭配动态 path/query/fragment | 声明、凭据、HTTP、native、redirect 采用同一规范主机；合法目标获准 | ReviewPluginHostNormalizationTests |
| F05-2 | 非法双 @、端口、括号；真正动态 authority | 完整原始 authority 先验证，非法输入不能贡献声明 | 同上 |
| F05-3 | raw/percent-encoded scoped IPv6，预热普通地址缓存/在途项后再请求 | 在 Uri.Host 丢失 zone 前拒绝；HTTP/native/redirect/凭据一致；不得命中缓存或触发下一跳 | 同上 |
| F05-4 | path/query 中普通 % 编码，原有方法/地址沙箱 | 不误判为 zone，不扩展 DNS/通配符/方法/协议权限 | 同上及原沙箱测试 |
| F06 | 合法 `DISK.<盘符>.Used`，物理缓存冷/热/10 秒刷新，及非法/未就绪盘 | 逻辑盘一直返回可用使用率；物理盘缓存/休眠行为保留；不借机扫描或唤醒真实物理盘 | ReviewMetricPresentationRegressionTests |
| F07 | 用随包真实 Stock.json，经生产 transforms 与 ResolveTemplate | `us.NDX` → `usNDX` → `http://qt.gtimg.cn/q=usNDX`；A/HK/其他原规则保留 | 同上 |
| F08-1 | 真实快照行，RAM/Committed/各卡 VRAM，UnitPanel 为 null/空/%/{u}/自定义 | v 与 u 同时遵循配置；示例内存为 `v=50.0,u=%`；容量按对应卡，缺容量维持合同 | 同上 |
| F08-2 | 自定义单位下进度/status、FPS 无需求/无数据/可信零值、DASH 文本和颜色 | 进度/status 仍按原值；无需求不读取 FPS；插件文本/颜色不被数字格式覆盖 | 同上 |
| F08-3 | 生产 WriteSnapshotJson 入口接线 | 实际使用已行为测试的 WriteSnapshotItemJson，而不是只有未被调用的新辅助方法 | 同上 |

## 8. A04：关键失败反例与防“假绿”验证

要求证明验收能抓住错误。先在正确副本中通过定向测试，再每次只引入一项有意义的缺陷；保存变异补丁、构建日志、失败用例身份与断言。**编译失败、依赖找不到、0 用例或环境超时不算“杀死变异”。** 不要求每个变异重跑全套 1831，但必须重建受影响生产工程和定向测试，不能对旧 DLL 执行 --no-build。

副本制作规则：按 A00 `$frozen` 的每个相对路径复制实际工作树文件，含未跟踪新增源码/测试和二进制资源；按完整清单复核副本。放在 `$Run/mutants/<编号>/source`，不复制 bin/obj/.git/旧日志；文档可按需要另拷。不得仅用 `git archive HEAD`。副本测试依赖仓库根定位时先检查定位结果确为副本，不能误跑回原工作树。

| 反例 | 在副本内恢复的错误行为 / 注入 | 必须失败的证据 |
| --- | --- | --- |
| N01 / F01 | RecordBindingState 改为无条件覆盖 | Presented→Unbound 及生产故障调度断言失败 |
| N02 / F02 | 明确恢复不排除已 Reclaimed 的 Failed 责任 | A/B 连续耗尽后 C 成功的闸门解除断言失败 |
| N03 / F03 | 绕过一个最终发布/缓存提交租约判定，允许退役任务提交 | 有屏障控制的迟到成功/异常或缓存污染断言失败；只改取消 token 不算该反例 |
| N04 / PLUG-01 | 删除路径退回按 A+"." 做宽泛前缀删除 | A/A.B 邻居被误删；A/AB 单独通过不能掩盖该失败 |
| N05 / F04 | 严格市场保存退回吞失败的 Save 成功语义 | 真实文件锁导致公开安装错误成功/文件记录不一致的断言失败 |
| N06 / MKT-R02 | 从卸载流程移除 pending-install 防护 | 备份锁或不可读 journal 下卸载破坏事务的断言失败 |
| N07 / F05 | 把 scoped IPv6 检查移到 Uri.Host 规范化/缓存访问后，或去掉原始 scope 拒绝 | 预热缓存/在途/redirect 的 scope 拒绝断言失败 |
| N08 / T09 | 只按 Dispose 名字/数量认定释放，或只检查字段而忽略局部槽 | 实际局部释放正例、未释放目标/只释放其他对象/静态同名负例至少分别被正确区分；Release 误报会被抓住 |
| N09 / 门禁 | 构造反例 TRX 和工作流 dotnet 返回码，不改候选脚本 | 非 E2E 单跳过、错误程序集同名 E2E、计数无明细、市场契约缺失/跳过、Release 失败而 Debug 更新，都必须非零；三个程序集分别失败，实际工作流执行块都应中止 |

N08 可直接调用识别器的独立正/负夹具，再用错误识别策略运行同一断言；两种错误策略应各有可见失败。N09 的合规“0 跳过”和“仅指定 E2E 跳过”对照组必须通过，避免写成“一律拒绝”。工作流需要执行提取出的真实脚本块，单纯正则匹配字符串不是退出码验证。

为避免验收者完全复用实现者的思路，额外编写至少两项独立时序探针：一项跨代次迟到发布/收尾，一项跨安装-恢复-卸载。调用真实生产入口/辅助协议，使用独立临时目录和确定性屏障，写出事前预期与实际观测；不能只重新实现一份相同算法并测试自己的算法。

输出 `negative-controls.json`，逐项记录：正确候选结果、注入内容、变异副本摘要、失败用例与实际断言、还原/新建正确副本后的对照结果。未能产生有效反例记 `NOT_VERIFIED`，不计为通过。

## 9. A05：独立正向探针重跑

参考已有 `.review-scratch/remediation-20261023/positive-probes` 和 `verify-fixed-probe-results.ps1`，**把源码复制到本次运行目录并重建**。原 csproj 用相对 ProjectReference/IlInspector 链接，移动后必须改为指向本次冻结候选的正确绝对路径；记录探针源码和项目文件 SHA-256。

至少独立观察以下八项，写入新的 `positive-probe-results.json`：

1. 多轮清理恢复最终 Completed、remainingBlock 为 null，失败历史仍在。
2. 生产故障调度后 binding 仍 Presented，RequiresControlledRecreate 为 true，fallback pending。
3. 非 scoped 等价 IPv6 获准；scope 目标在访问缓存/传输前被拒绝。
4. 热加载实际执行新 URL，卸载实例和调度数量归零。
5. 锁住真实 settings 文件时公开安装失败，内存/重载记录/插件文件没有孤儿。
6. 逻辑盘 Used 在物理缓存前后都有值，范围/误差与实际可用逻辑盘一致。
7. 真实 Stock 模板得到 `usNDX` 和正确 fetch URL。
8. 生产网页行与桌面相同输入下得到 `50.0` 和 `%`。

Release JsonDocument 局部槽和 Dispose 调用布局另外记录为诊断。编译器布局并非稳定产品合同，不能把“恰好 6 次 Dispose”作为正确释放的充分证明；真正的三类资源释放正/负断言必须在 A01、N08 中成立。旧探针若有不同预期，先读断言和生产路径，不改产品去迎合旧探针。

## 10. A06：真实 MainForm 构造与进程退出码

确认使用隔离的构建输出配置，不读取用户运行目录。以下只运行 `startup`，不进入 OnShown；构造期托盘的短暂出现按既有行为记录，不能假称整个 UI 已运行。

```powershell
Invoke-CheckedNative -Exe $Dotnet -Arguments @(
    'build','tools/GpuHostLifecycleProbe/GpuHostLifecycleProbe.csproj',
    '-c','Debug','--no-restore','-t:Rebuild','--nologo'
) -Log (Join-Path $Run 'build-startup-probe.log')
$startupOut = Join-Path $Run 'startup'
New-Item -ItemType Directory -Path $startupOut | Out-Null
$probe = Join-Path $Root 'tools/GpuHostLifecycleProbe/bin/Debug/net8.0-windows/GpuHostLifecycleProbe.exe'
$proc = Start-Process -FilePath $probe -ArgumentList @(
    '--scenario=startup', ('"{0}"' -f $startupOut)
) -PassThru -WindowStyle Hidden
if (-not $proc.WaitForExit(45000)) {
    $proc.Kill()
    throw '启动探针超时，不能判为通过'
}
$proc.Refresh()
$startupCode = $proc.ExitCode
if ($null -eq $startupCode -or $startupCode -ne 0) {
    throw "启动探针非成功退出：$startupCode"
}
$startupJson = Join-Path $startupOut 'startup-ok.json'
$startup = Get-Content -LiteralPath $startupJson -Raw -Encoding utf8 | ConvertFrom-Json
if ($startup.scenario -ne 'startup' -or $startup.verdict -ne 'PASS' -or
    $startup.webServerRunning -ne $false -or $startup.webServerPort -ne -1) {
    throw '启动退出码与结构化证据不满足合同'
}
```

保存实际进程退出码、探针 exe 哈希、参数、JSON、错误日志和前后输出目录变化。不能用 `$LASTEXITCODE` 判断 WinExe 的启动结果；不能丢失带空格输出路径的引号。退出码 1 是失败；环境不具备条件的非零结果为 NOT_VERIFIED/ENV_BLOCKED，不能作为成功。

`hardwareSamplerCreated=true` 只说明构造完成，不证明硬件采样正确；该场景不证明 GPU 送帧、首帧显示、故障回退或桌面像素正确。

## 11. A07：持续门禁、依赖与更新器现状取证

逐一核对 `.github/workflows/ci.yml`、`release.yml` 和 `assert-test-counts.ps1`：

- 双工作流都实际执行 Debug 和 Release 的三程序集；两个配置使用分开的 TRX 根目录，各自捕获命令退出码并执行计数门禁。
- 两者 `MIN_TEST_TOTAL=1831`、三程序集名单、`MAX_SKIPPED_TESTS=1`、市场契约至少 6 条完全一致，没有 `continue-on-error`、`|| true`、只运行过滤集等绕过。
- A04/N09 对真实脚本的失败注入通过；A01 对新生成的两组 TRX 门禁均通过。
- 执行依赖检查，日志必须包含直接/传递依赖的缺失、版本不符、多余项数量，不能只看命令返回 0。

```powershell
Invoke-CheckedNative -Exe $Pwsh -Arguments @(
    '-NoProfile','-File','scripts/generate-dependency-manifest.ps1','-Mode','Check'
) -Log (Join-Path $Run 'dependency-check.log')
$bundledUpdater = Join-Path $Root 'resources/LiteMonitor++.Updater.exe'
Get-FileHash -LiteralPath $bundledUpdater -Algorithm SHA256 |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Run 'bundled-updater-hash.json') -Encoding utf8
(Get-Item -LiteralPath $bundledUpdater).VersionInfo |
    Select-Object FileVersion,ProductVersion |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Run 'bundled-updater-version.json') -Encoding utf8
```

交付时的预构建更新器：SHA-256 `5D1AE3FD45ACC170DF01E49334BEEEDB875024102A9B73670BA5A960D179A036`；FileVersion `1.2.5.0`；ProductVersion 含 `366bf9c3d53ffc7f16c5c97d6360c4c4293e301c`。源码存在差异，既有结论是 **NOT_VERIFIED**。

依赖/NOTICE 检查成功与“旧更新器来源已验证”是两个判断。重建候选后哈希不同，不能据此反推旧二进制正确或错误。原产物保持原状态，新产物的闭环见 R01～R04。A07 通过意味着准确检查并披露现状，不意味着发布放行。

## 12. A08：上游来源和修复接线复核

保存本地 remotes、相关 refs/commit、对比 diff。必要时只读查看提交对象，不能以两仓库没有直系祖先就认定“没有上游问题”。仓库存在改写历史，按真实代码语义和调用接线判断。

| 项目 | 要核实的来源/接线 | 验收证据 |
| --- | --- | --- |
| F01 / F02 | 分别是近期 GPU 回退/明确恢复修复中的新回归 | 对照基线调用链，A03/A04 证明失败被消除且启动/关闭边界保留 |
| F03 | 历史调度逻辑与 fork 市场热加载相交 | 内容变化、退役发布和点分键归属的生产调用链及本次运行结果 |
| F04 | fork 市场事务及保存协议 | 严格持久化、补偿次序、pending 卸载两项复审问题的失败证据与恢复结果 |
| F05 | LM-094 残留与 URI scope 相关边界 | 完整 authority 验证与缓存前拒绝的实际调用顺序 |
| F06 | 继承的逻辑盘/物理缓存顺序问题 | 基线相关代码与候选差异；冷/热缓存行为验证 |
| F07 | 仅移植适用上游提交 `a71ce7584d892230d157c02047bf052fad6a738f` | 上游 Stock transform 与候选一致；用真实 bundled JSON 验证，未无关整仓合并 |
| F08 | 上游格式规则已有，但 fork 网页真实路径未完整接入 | 原路径与当前快照入口对比；真实序列化 v/u/pct/sts 断言 |

记录本地上游引用的取得时间/当前提交；若未更新远端，只能说明“对本批审查所用上游基线完成复核”，不能宣称检查了互联网当前全部上游修改。若追踪期间发现额外缺陷，单独报告复现和影响范围，不改写成本批已修复事项。

## 13. G01～G03：实机 GPU 与 Windows 清理责任验收

本节要求 Windows 交互式测试桌面、实际可用的 GPU/驱动、专用配置/输出路径，且能观察窗口而不采集用户其他应用内容。优先使用事先准备好的测试机/VM。软件渲染或远程会话不能冒充相同硬件条件；记录实际适配器、驱动、远程状态、DPI 和探针选择的设备。

环境不足则记录具体缺口，继续完成代码验收和包取证。不禁用用户显示适配器，不重启驱动，不绕过既有隔离门。

已核对探针实际支持的参数只有：`--scenario=lifecycle|s1|s2|mainform|startup`、`--topmost`、`--click-through`、`--timeout-ms=<整数>` 和输出目录。**没有**通用 `--device-lost` 参数，不要编造命令。运行前阅读 `tools/GpuHostLifecycleProbe/Program.cs`、`ProbeForm.cs`、`RecreateHandleScenario.cs`、`RealMainFormScenario.cs`。

| 编号 | 执行内容 | 通过标准/证据 |
| --- | --- | --- |
| G01 | lifecycle 的真实 GPU 初始化、首帧、暂停/恢复、两次宿主故障注入 | 记录实际 GPU 路径；PNG/诊断一致；首次有限恢复成功、第二次拒绝重复恢复，最终资源归零；进程退出与诊断均成功 |
| G02 | s1/s2 的已呈现→解除 GPU→受控 RecreateHandle→动态 GDI；再用生产窗口路径验证 F01 的替换宿主绑定前失败 | 时间线含 HWND 代次和呈现历史；在安全 UI 点完成回退；至少多帧桌面可见区域像素/帧标记持续变化，不能用离屏 CaptureScenePng 代替可见画面；关闭中不再回退，重复通知不重启 |
| G03 | 测试窗口上读取原 affinity，用现有注入委托使责任 A/B 失败，最终 C 使用真实 Win32 恢复；另测句柄换代 | 记录 GetWindowDisplayAffinity 原值、目标 HWND/代次、每次失败和真实最终回读；精确恢复原值，闸门解除且失败审计保留；旧 HWND 不被重试写入 |

`mainform` arm 的既有隔离门要求托盘隐藏、运行前无 Web 监听、配置 Web 禁用、运行后仍无监听；任一不能证明必须退出并记 NOT_VERIFIED。不得为了使探针通过去改生产构造行为或直接删门。若该 arm 被拒绝，只能报告对应生产窗口证据缺失，不能由 s1/s2 合成窗口替代。

现有探针不能表达 G02/G03 全部组合时，可在验收专用外部探针中调用既有生产入口、故障接缝和真实 Win32 委托；不得更改冻结生产文件。受控宿主异常注入只证明该故障通道，不能声称真实物理设备移除/驱动崩溃已复现。

每个场景使用独立新目录、独立进程、超时看门狗；只终止自己启动且记录 PID 的探针。保存开始/结束源码哈希、适配器信息、场景参数、实际退出码、诊断 JSON、带时间的帧证据、资源计数。没有源码哈希或 hash guard 不生效，不能记录“未漂移”。

## 14. R01～R04：发布候选与更新器取证

这部分只在**完整导出的发布副本**中构建本地候选包，不覆盖原工作树资源，不发布。`scripts/pack-release.ps1` 会刷新它所在仓库的 `resources/LiteMonitor++.Updater.exe`，并递归清理该副本的 `dist/stage` 和 `dist/updater`；因此禁止直接对原候选工作树调用打包脚本。

| 编号 | 操作 | 通过标准 |
| --- | --- | --- |
| R01 | 按完整冻结清单导出 `$Run/release-source`，逐文件核对；另存原随包更新器元数据与字节副本 | 完整包含未提交修复和新增代码；除文档外输入与 A00 一致；原二进制证据未被覆盖 |
| R02 | 在副本运行既有 pack-release，版本从 LiteMonitor.csproj 读取并与 resources/version.json 核对 | Updater 与主程序都从该快照重新 publish；退出码为 0；留存 SDK、命令、完整日志和构建前后资源差异 |
| R03 | 检查 ZIP、许可、运行时资源与更新器 | ZIP 命名匹配 UpdateChecker；LICENSE/NOTICE、driver.zip、FPS、CityCode 和 shaders 等必需资产按打包合同齐全；包内更新器哈希等于本次生成并被主程序实际随附的更新器字节 |
| R04 | 核对新产物来源、Updater 关键拒绝行为和候选包运行边界 | 新包 SHA-256/尺寸、全输入摘要、更新器 hash/元数据/行为证据可追溯；原产物 NOT_VERIFIED 状态与新包验明状态分开 |

导出副本时每个源路径和目标路径都使用完整路径及 LiteralPath 操作，检查目标在 `$Run/release-source` 内且没有越界重解析点。运行 pack-release 前确认它将清理的 `dist/stage`、`dist/updater` 和生成 ZIP 的绝对路径全部属于这次新建副本。无需删除原目录任何文件。

打包调用模板（只改本次副本路径，不能擅自改版本）：

```powershell
$releaseRoot = Join-Path $Run 'release-source' # 先按 R01 导出并验证，不能是空目录
[xml]$releaseProject = Get-Content -LiteralPath (Join-Path $releaseRoot 'LiteMonitor.csproj') -Raw
$version = @($releaseProject.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ([string]::IsNullOrWhiteSpace($version)) { throw '无法读取候选版本' }
Invoke-CheckedNative -Exe $Pwsh -Arguments @(
    '-NoProfile','-File',(Join-Path $releaseRoot 'scripts/pack-release.ps1'),
    '-Version',$version,'-Dotnet',$Dotnet,'-OutDir','dist'
) -Log (Join-Path $Run 'pack-release.log')
```

旧更新器的时间戳、FileVersion 和 embedded commit 不能单独证明字节对应当前源码。新构建即便继承旧 HEAD 的提交标签，也必须绑定未提交输入的冻结清单，不能声称仅由 9238bb9 构建。

候选更新器的黑盒检查只在专用沙箱中进行。先读 `LiteMonitor.Updater/Program.cs` 的 RunFromArgs、RejectMissingDigest 和 LegacyBridgeMarker，实现真实命令行与预期路径的对应关系。把新 updater 放进专用临时安装结构，周围放 sentinel，不能指向用户主程序目录。至少验证：

- 无参调用按 usage 合同拒绝；旧式单 ZIP 参数缺可信摘要时退出码 6，生成约定的手动升级标记/诊断，不修改主程序和 sentinel。
- 非法或不匹配摘要被拒绝；没有成功标记和越界写入；ZIP 路径、目标和标记都在沙箱内。
- 不把源码工程的 73 条 Updater 测试直接写成“随包二进制黑盒 73 条通过”。成功替换、回滚和桥接协议可结合源码测试、构建来源与额外沙箱验证分别列证。

需等待/重启主程序的成功升级场景只有在独立测试安装中执行，先确认其进程查找和目标定位不会触及用户真实实例。条件不足则保留未验证，不通过私自关闭用户进程来完成。不得签名、使用私钥、上传资产或执行真实客户端升级。

在发布副本重跑依赖检查并保留原始输出。若脚本的新鲜度子报告仍为 NOT_VERIFIED，原样记录；可另附本次完整输入→publish 日志→新资源→包内字节的直接来源证据，但不能把脚本输出改写成 PASS。若来源或关键黑盒行为仍缺失，PACKAGE 不放行。

## 15. 报告、证据结构与退出条件

验收结束后在工作树 `docs/reviews/` 新建 `2026-10-24-independent-acceptance-<runId>.md`，必要时复制一份到 `G:\TOOL\LiteMonitor-fork\docs\reviews\`。报告复制允许新增文档，不能把修复代码散拷进主目录。

建议证据目录结构：

```text
.review-scratch/acceptance-<runId>/
  run-metadata.json
  candidate-inventory.json / delivered-source-sha256.json / candidate-status.txt
  candidate-tracked.patch
  restore.log / build-Debug.log / build-Release.log
  Debug/<assembly>/results.trx
  Release/<assembly>/results.trx
  gate-Debug.log / gate-Release.log
  test-summary.json / test-identities.json / baseline-identity-diff.json
  negative-controls.json / mutants/<编号>/...
  positive-probe-results.json / independent-probes/...
  startup/...
  dependency-check.log / bundled-updater-*.json
  upstream-comparison/...
  gpu/<scenario>/...
  release-source/... / package-evidence/...
  final-source-comparison.json
```

报告必须包含：

1. 首段给出 CODE / GPU / PACKAGE 三个独立结论及是否具备发布放行条件。
2. 候选路径、HEAD、分支、**未提交源码清单摘要**、运行环境、开始/结束时间；最终源码与文件集合比较结果。
3. Debug/Release 各程序集实测总计、通过、失败、跳过完整身份；历史 1632 保留数、缺失名单、199 新增身份对账。
4. A00～A08、G01～G03、R01～R04 每项 PASS/FAIL/ENV_BLOCKED/NOT_RUN/NOT_VERIFIED、证据链接；A03 每行关联具体用例/探针，N01～N09 给出有效反例。
5. 新发现按 P1/P2/P3 记录：触发条件、最小复现、期望/实际、生产位置、影响与证据。区分本批新引入、历史残留、上游继承或环境问题。
6. 每个未验证项的具体原因和闭环所需条件；已有待办不能被默认为已修复。
7. 命令非零时的真实退出码、失败日志和 TRX。允许的联网 E2E 跳过之外，不得隐藏任何失败或跳过。

**停止放行的条件：** 任何必验项失败、源码漂移未解释、用例丢失、门禁反例未被拒绝、真实持久化不一致、迟到任务污染新代次、上游修复接线错误，均不能给 CODE_PASS。无法安全运行 GPU 或验明旧/新更新器不妨碍交付代码验收报告，但必须保留 RELEASE_NOT_CLEARED。

修复需要另开执行批次；修复完成后重新冻结，重跑相关失败反例、受影响的行为测试及双配置全量/门禁，不得复用旧源码哈希下的总通过结论。

## 16. 可直接交给验收 Agent 的任务指令

> 请严格执行 `G:\TOOL\LiteMonitor-fork\docs\superpowers\plans\2026-10-24-independent-acceptance-plan.md`，对本批 LiteMonitor 修复做独立验收。实际候选位于 `C:\Users\wrz20\.codex\worktrees\review-remediation-20261023\LiteMonitor-fork`，修复尚未提交，不能只检出分支或在 G 盘原始源码上测试。先验证 31 文件交付清单并冻结完整输入，再串行重建/运行 Debug、Release 三程序集，核对每配置 1831 条、原 1632 身份保留和唯一 E2E 跳过；执行逐缺陷行为矩阵、独立时序探针、有效失败反例、真实 MainForm 构造、依赖/门禁和上游接线复核。GPU 实机与发布候选按计划单独给结论，条件不足如实记未验证。只在新证据目录和完整导出副本写验收材料，不改候选生产代码、既有测试或门禁，不提交、推送、发布，不执行针对用户安装的升级。发现缺陷保留最小复现后报告，不自行修复。最终输出带绝对路径证据链接的验收报告，明确 CODE / GPU / PACKAGE 结论和所有放行缺口。
