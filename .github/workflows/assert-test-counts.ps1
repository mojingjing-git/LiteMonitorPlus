#requires -Version 7.0
<#
.SYNOPSIS
    解析 dotnet test 产出的 TRX 报告，逐程序集统计用例数，并断言"测试真的被发现了"。

.DESCRIPTION
    ★ 为什么必须有这个脚本（审计项 BASE-004 / 假绿禁令 G3）：
      `dotnet test` 在"一个用例都没发现"时**照样返回退出码 0**。
      因此只盯退出码的流水线永远是绿的 —— 测试工程没进解决方案、用例被过滤掉、
      测试适配器没加载，绿灯依然亮着。本脚本从 VSTest 的结构化结果文件（TRX）
      里取真实计数，把"发现数"变成硬门禁。

    为什么解析 TRX 而不是控制台文本：
      控制台摘要行是**本地化**的（中文环境输出"已通过! - 失败: 0，通过: 13…"，
      英文环境输出"Passed! - Failed: 0, Passed: 13…"），按文本匹配必然在
      某一种 runner 语言下失配。TRX 是 XML，计数属性名固定，与语言无关。

    强制断言（任一不满足即以非零退出码结束）：
      1. 期望的每个测试程序集都出现了 —— 缺一即失败（某个工程根本没被测到）
      2. 任何一个程序集的"总计"为 0 即失败 —— 这就是"0 发现即 FAIL"
      3. 所有程序集的用例总数 >= -MinTotal（默认 1000，阈值由调用方给出）
      4. 存在失败用例即失败
      5. 全部用例都被跳过（实际执行数为 0）即失败 —— "跑了 1000 条其实一条没执行"
         与"没发现"同样是假绿
      6. ★ 跳过用例总数 <= -MaxSkipped（默认 1）—— 跳过必须**有上限**。
         允许跳过的只有联网 E2E（MarketE2ETests，门控 LITEMONITOR_E2E=1）。
         S4.1 之前这条是缺失的：插件市场的 6 条契约用例长期被记为 Skipped，
         流水线照样是绿的（ENV-003）。
      7. ★ -MustExecuteTestClass 指定的测试类必须**真正执行**：
         该类在 TRX 里必须有结果、条数 >= -MustExecuteTestClassMinCount、
         且**一条都不能是 NotExecuted**（被跳过）—— 门控失效时必须让流水线红。

    可见性要求：
      逐程序集的实际计数与**被跳过的用例名**一律打印到标准输出。
      跳过必须可见，不允许静默。

.PARAMETER TrxRoot
    TRX 报告的搜索根目录（递归查找 *.trx）。也可以直接传单个 .trx 文件。

.PARAMETER MinTotal
    用例总数下限阈值。默认 1000。调用方（CI）应在 env 里显式给出并加注释。

.PARAMETER ExpectedAssembly
    必须出现的测试程序集名（不含 .dll）。支持逗号分隔的单字符串。
    默认三个测试工程：LiteMonitor.Tests / LiteMonitor.Updater.Tests / LiteMonitor.GpuNonperformance.Tests。

.PARAMETER MaxSkipped
    全部程序集合计的**跳过用例数上限**。默认 1（只剩联网 E2E）。
    超过即失败 —— 跳过数只允许减少，不允许增加。

.PARAMETER MustExecuteTestClass
    必须真正执行的测试类名（可传多个）。默认空数组（不启用该断言）。
    匹配方式：TRX 的 testName 中出现 ".<类名>."。

.PARAMETER MustExecuteTestClassMinCount
    上述"必须执行"的测试类至少要被发现的用例条数。默认 1。

.EXAMPLE
    pwsh -File .github/workflows/assert-test-counts.ps1 -TrxRoot tests -MinTotal 1000

.EXAMPLE
    pwsh -File .github/workflows/assert-test-counts.ps1 -TrxRoot tests -MinTotal 1000 -ExpectedAssembly 'A.Tests,B.Tests'

.EXAMPLE
    pwsh -File .github/workflows/assert-test-counts.ps1 -TrxRoot tests -MinTotal 1150 -MaxSkipped 1 `
          -MustExecuteTestClass 'MarketIndexContractTests' -MustExecuteTestClassMinCount 6

.NOTES
    退出码：0 全部断言通过；1 断言失败；2 用法/IO 错误（同样非零，阻断流水线）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TrxRoot,

    [int]$MinTotal = 1000,

    [string[]]$ExpectedAssembly = @(
        'LiteMonitor.Tests',
        'LiteMonitor.Updater.Tests',
        'LiteMonitor.GpuNonperformance.Tests'
    ),

    # ★ 跳过用例数上限。默认 1 = 只允许联网 E2E 被跳过。
    [int]$MaxSkipped = 1,

    # ★ 必须真正执行（不允许被跳过）的测试类。
    [string[]]$MustExecuteTestClass = @(),

    # ★ 上述测试类至少要被发现的用例条数。
    [int]$MustExecuteTestClassMinCount = 1
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 统一的失败出口。
# ★ 不用 Write-Error：本脚本设了 $ErrorActionPreference='Stop'，
#   Write-Error 会变成终止性错误，后面的 exit N 根本执行不到，退出码会退化成 1。
#   这里改成"打印到 stderr + 显式 exit N"，保证退出码就是文档里写的那个。
function Stop-WithError {
    param(
        [string]$Message,
        [int]$Code = 1
    )
    Write-Host "::error::$Message"
    [Console]::Error.WriteLine($Message)
    exit $Code
}

# ----------------------------------------------------------------------
# 0. 参数整理
# ----------------------------------------------------------------------
$expectedNames = New-Object System.Collections.Generic.List[string]
foreach ($item in $ExpectedAssembly) {
    foreach ($part in ($item -split ',')) {
        $t = $part.Trim()
        if ($t) { $expectedNames.Add($t) }
    }
}
if ($expectedNames.Count -eq 0) {
    Stop-WithError -Message 'ExpectedAssembly 不能为空：必须显式声明"必须出现"的测试程序集。' -Code 2
}

# "必须真正执行"的测试类：同样支持逗号分隔的单字符串
$mustExecClasses = New-Object System.Collections.Generic.List[string]
foreach ($item in $MustExecuteTestClass) {
    foreach ($part in ($item -split ',')) {
        $t = $part.Trim()
        if ($t) { $mustExecClasses.Add($t) }
    }
}

if ($MaxSkipped -lt 0) {
    Stop-WithError -Message "MaxSkipped 不能为负：$MaxSkipped" -Code 2
}
if ($MustExecuteTestClassMinCount -lt 1) {
    Stop-WithError -Message "MustExecuteTestClassMinCount 必须 >= 1：$MustExecuteTestClassMinCount" -Code 2
}

# ----------------------------------------------------------------------
# 1. 收集 TRX 文件
# ----------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $TrxRoot)) {
    Stop-WithError -Message "TRX 路径不存在：$TrxRoot" -Code 2
}

$trxFiles = @()
$rootItem = Get-Item -LiteralPath $TrxRoot
if ($rootItem.PSIsContainer) {
    $trxFiles = @(Get-ChildItem -LiteralPath $TrxRoot -Filter '*.trx' -Recurse -File -ErrorAction Stop |
        Sort-Object -Property FullName)
}
else {
    $trxFiles = @($rootItem)
}

if ($trxFiles.Count -eq 0) {
    Stop-WithError -Message "在 $TrxRoot 下没有找到任何 .trx 测试结果文件 —— 测试很可能压根没有执行。" -Code 1
}

# ----------------------------------------------------------------------
# 2. 解析
# ----------------------------------------------------------------------
function Get-IntAttribute {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$Name
    )
    if ($null -eq $Node) { return 0 }
    $raw = $Node.GetAttribute($Name)
    if ([string]::IsNullOrWhiteSpace($raw)) { return 0 }
    $value = 0
    if (-not [int]::TryParse($raw.Trim(), [ref]$value)) { return 0 }
    return $value
}

function Read-TrxSummary {
    param([string]$Path)

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.Load($Path)

    $counters = $doc.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters) {
        throw "TRX 里找不到 ResultSummary/Counters（文件可能不完整）：$Path"
    }

    $total     = Get-IntAttribute -Node $counters -Name 'total'
    $executed  = Get-IntAttribute -Node $counters -Name 'executed'
    $passed    = Get-IntAttribute -Node $counters -Name 'passed'
    $failed    = Get-IntAttribute -Node $counters -Name 'failed'
    $errorCnt  = Get-IntAttribute -Node $counters -Name 'error'
    $timeout   = Get-IntAttribute -Node $counters -Name 'timeout'
    $aborted   = Get-IntAttribute -Node $counters -Name 'aborted'
    $notRun    = Get-IntAttribute -Node $counters -Name 'notRunnable'

    # 不"通过、也不算失败"的其它坏结局一律计入失败侧，避免把它们藏进跳过里
    $bad = $failed + $errorCnt + $timeout + $aborted + $notRun
    $skipped = $total - $passed - $bad
    if ($skipped -lt 0) { $skipped = 0 }

    # 程序集名：优先取 TestMethod/@codeBase 里的 dll 路径（保留大小写），
    # 其次取 UnitTest/@storage（注意：TRX 会把它写成全小写），
    # 最后退回到 TRX 所在目录名。
    $assembly = $null
    $methodNode = $doc.SelectSingleNode("//*[local-name()='TestMethod']")
    if ($null -ne $methodNode) {
        $codeBase = $methodNode.GetAttribute('codeBase')
        if (-not [string]::IsNullOrWhiteSpace($codeBase)) {
            $assembly = [System.IO.Path]::GetFileNameWithoutExtension($codeBase)
        }
    }
    if ([string]::IsNullOrWhiteSpace($assembly)) {
        $unitNode = $doc.SelectSingleNode("//*[local-name()='UnitTest']")
        if ($null -ne $unitNode) {
            $storage = $unitNode.GetAttribute('storage')
            if (-not [string]::IsNullOrWhiteSpace($storage)) {
                $assembly = [System.IO.Path]::GetFileNameWithoutExtension($storage)
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($assembly)) {
        $dir = [System.IO.Path]::GetFileNameWithoutExtension([System.IO.Path]::GetDirectoryName($Path))
        $assembly = $dir
    }

    # 被跳过的用例名（outcome=NotExecuted）—— 跳过必须可见
    $skippedTests = New-Object System.Collections.Generic.List[object]
    # ★ 全部用例结果（名字 + 结果）—— "必须真正执行"的断言需要它
    $allTests = New-Object System.Collections.Generic.List[object]
    foreach ($node in @($doc.SelectNodes("//*[local-name()='UnitTestResult']"))) {
        $name = $node.GetAttribute('testName')
        $outcome = $node.GetAttribute('outcome')
        $allTests.Add([pscustomobject]@{
            Name    = $name
            Outcome = $outcome
        })
        if ($outcome -ne 'NotExecuted') { continue }
        $reasonNode = $node.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
        $reason = ''
        if ($null -ne $reasonNode) { $reason = $reasonNode.InnerText.Trim() }
        $skippedTests.Add([pscustomobject]@{
            Name   = $name
            Reason = $reason
        })
    }

    return [pscustomobject]@{
        Path         = $Path
        Assembly     = $assembly
        Total        = $total
        Executed     = $executed
        Passed       = $passed
        Failed       = $bad
        Skipped      = $skipped
        SkippedTests = $skippedTests
        AllTests     = $allTests
        LastWrite    = (Get-Item -LiteralPath $Path).LastWriteTime
    }
}

$reports = New-Object System.Collections.Generic.List[object]
$parseErrors = New-Object System.Collections.Generic.List[string]
foreach ($file in $trxFiles) {
    try {
        $reports.Add((Read-TrxSummary -Path $file.FullName))
    }
    catch {
        $parseErrors.Add("$($file.FullName): $($_.Exception.Message)")
    }
}

if ($reports.Count -eq 0) {
    Write-Host ''
    Write-Host '=== 测试发现数校验：失败 ===' -ForegroundColor Red
    foreach ($e in $parseErrors) { Write-Host "  [解析失败] $e" -ForegroundColor Red }
    exit 2
}

# 同一程序集有多个 TRX（重复跑过）时取**最后一次**运行的结果，
# 并明确告警 —— 绝不取"最大"或"最小"，免得旧的好结果盖住新的坏结果。
$byAssembly = @{}
foreach ($r in $reports) {
    $key = $r.Assembly.ToLowerInvariant()
    if (-not $byAssembly.ContainsKey($key) -or $r.LastWrite -gt $byAssembly[$key].LastWrite) {
        $byAssembly[$key] = $r
    }
}
$duplicated = $reports.Count -ne $byAssembly.Count

$selected = @($byAssembly.Values | Sort-Object -Property Assembly)

# ----------------------------------------------------------------------
# 3. 报告
# ----------------------------------------------------------------------
$sumTotal   = ($selected | Measure-Object -Property Total   -Sum).Sum
$sumPassed  = ($selected | Measure-Object -Property Passed  -Sum).Sum
$sumFailed  = ($selected | Measure-Object -Property Failed  -Sum).Sum
$sumSkipped = ($selected | Measure-Object -Property Skipped -Sum).Sum

Write-Host ''
Write-Host '=== 测试发现数校验 ===' -ForegroundColor Cyan
Write-Host ("{0,-34} {1,8} {2,8} {3,8} {4,8}" -f '程序集', '总计', '通过', '失败', '跳过')
Write-Host ('-' * 72)
foreach ($r in $selected) {
    Write-Host ("{0,-34} {1,8} {2,8} {3,8} {4,8}" -f $r.Assembly, $r.Total, $r.Passed, $r.Failed, $r.Skipped)
}
Write-Host ('-' * 72)
Write-Host ("{0,-34} {1,8} {2,8} {3,8} {4,8}" -f '合计', $sumTotal, $sumPassed, $sumFailed, $sumSkipped)
Write-Host ''
Write-Host ("阈值：每个期望程序集都要出现；任一程序集总计不得为 0；合计 >= $MinTotal；失败数必须为 0；跳过数 <= $MaxSkipped。")
Write-Host ("期望程序集：{0}" -f ($expectedNames -join ', '))
if ($mustExecClasses.Count -gt 0) {
    Write-Host ("必须真正执行的测试类：{0}（每类至少 {1} 条，且一条都不允许被跳过）" -f ($mustExecClasses -join ', '), $MustExecuteTestClassMinCount)
}

if ($duplicated) {
    Write-Host ''
    Write-Host '[警告] 发现同一程序集的多个 TRX，已采用最后一次运行的结果：' -ForegroundColor Yellow
    foreach ($r in $selected) {
        Write-Host ("    {0} -> {1} ({2})" -f $r.Assembly, $r.Path, $r.LastWrite.ToString('s')) -ForegroundColor Yellow
    }
}

# 被跳过的用例：必须逐条可见
Write-Host ''
Write-Host ("被跳过的用例（{0} 条）：" -f $sumSkipped) -ForegroundColor Cyan
if ($sumSkipped -eq 0) {
    Write-Host '    （无）'
}
else {
    foreach ($r in $selected) {
        foreach ($t in $r.SkippedTests) {
            Write-Host ("    [{0}] {1}" -f $r.Assembly, $t.Name) -ForegroundColor DarkYellow
            if ($t.Reason) {
                Write-Host ("        原因：{0}" -f $t.Reason) -ForegroundColor DarkYellow
            }
        }
    }
}

# ★ "必须真正执行"的测试类：逐类打印实际状态
$allTestResults = New-Object System.Collections.Generic.List[object]
foreach ($r in $selected) {
    foreach ($t in $r.AllTests) {
        $allTestResults.Add($t)
    }
}

if ($mustExecClasses.Count -gt 0) {
    Write-Host ''
    Write-Host '必须真正执行的测试类：' -ForegroundColor Cyan
    foreach ($cls in $mustExecClasses) {
        $pattern = '\.' + [regex]::Escape($cls) + '\.'
        $hits = @($allTestResults | Where-Object { $_.Name -match $pattern })
        $hitSkipped = @($hits | Where-Object { $_.Outcome -eq 'NotExecuted' })
        $hitPassed = @($hits | Where-Object { $_.Outcome -eq 'Passed' })
        Write-Host ("    {0}：发现 {1} 条，通过 {2} 条，被跳过 {3} 条" -f $cls, $hits.Count, $hitPassed.Count, $hitSkipped.Count)
        foreach ($t in $hitSkipped) {
            Write-Host ("        [跳过] {0}" -f $t.Name) -ForegroundColor DarkYellow
        }
    }
}

# ----------------------------------------------------------------------
# 4. 断言
# ----------------------------------------------------------------------
$problems = New-Object System.Collections.Generic.List[string]

foreach ($want in $expectedNames) {
    $hit = $selected | Where-Object { $_.Assembly -ieq $want } | Select-Object -First 1
    if ($null -eq $hit) {
        $problems.Add("缺少测试程序集 '$want' 的测试结果 —— 该工程没有出现在 TRX 里（没被测到）。")
    }
}

foreach ($r in $selected) {
    if ($r.Total -eq 0) {
        $problems.Add("程序集 '$($r.Assembly)' 发现 0 条用例 —— 0 发现即 FAIL。（TRX: $($r.Path)）")
    }
    if (($r.Passed + $r.Failed) -eq 0) {
        $problems.Add("程序集 '$($r.Assembly)' 实际执行了 0 条用例（全部被跳过）—— 等同于没跑。（TRX: $($r.Path)）")
    }
    if ($r.Failed -gt 0) {
        $problems.Add("程序集 '$($r.Assembly)' 有 $($r.Failed) 条失败用例。")
    }
}

if ($sumTotal -lt $MinTotal) {
    $problems.Add("用例总数 $sumTotal 低于阈值 $MinTotal —— 测试发现数不达标。")
}

# ★ 跳过总数上限（收紧项）。跳过数只允许减少，不允许增加。
if ($sumSkipped -gt $MaxSkipped) {
    $problems.Add("被跳过的用例共 $sumSkipped 条，超过上限 $MaxSkipped —— 允许跳过的只有联网 E2E（MarketE2ETests）；契约用例被跳过属于门禁失效。")
}

# ★ "必须真正执行"的测试类（收紧项）。门控失效时必须让流水线红，而不是静默跳过。
foreach ($cls in $mustExecClasses) {
    $pattern = '\.' + [regex]::Escape($cls) + '\.'
    $hits = @($allTestResults | Where-Object { $_.Name -match $pattern })
    if ($hits.Count -eq 0) {
        $problems.Add("测试类 '$cls' 在 TRX 里一条结果都没有 —— 该类用例没有被发现（可能整个类被删掉，或被过滤掉）。")
        continue
    }
    if ($hits.Count -lt $MustExecuteTestClassMinCount) {
        $problems.Add("测试类 '$cls' 只发现 $($hits.Count) 条用例，少于要求的 $MustExecuteTestClassMinCount 条 —— 契约用例可能被删减。")
    }
    $hitSkipped = @($hits | Where-Object { $_.Outcome -eq 'NotExecuted' })
    if ($hitSkipped.Count -gt 0) {
        $names = ($hitSkipped | ForEach-Object { $_.Name }) -join ', '
        $problems.Add("测试类 '$cls' 有 $($hitSkipped.Count) 条被跳过：$names —— 契约用例必须真正执行（ENV-003）。")
    }
    $hitFailed = @($hits | Where-Object { $_.Outcome -ne 'Passed' -and $_.Outcome -ne 'NotExecuted' })
    if ($hitFailed.Count -gt 0) {
        $names = ($hitFailed | ForEach-Object { $_.Name }) -join ', '
        $problems.Add("测试类 '$cls' 有 $($hitFailed.Count) 条结果异常（既非通过也非跳过）：$names")
    }
}

if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host '=== 测试发现数校验：失败 ===' -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  [FAIL] $p" -ForegroundColor Red }
    foreach ($e in $parseErrors) { Write-Host "  [解析失败] $e" -ForegroundColor Red }
    Write-Host ''
    exit 1
}

Write-Host ''
Write-Host '=== 测试发现数校验：通过 ===' -ForegroundColor Green
Write-Host ("发现 {0} 条用例（通过 {1} / 失败 {2} / 跳过 {3}），阈值 {4}。" -f $sumTotal, $sumPassed, $sumFailed, $sumSkipped, $MinTotal)
exit 0
