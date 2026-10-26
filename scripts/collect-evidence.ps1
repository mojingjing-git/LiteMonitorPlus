<#
.SYNOPSIS
    E08（完善计划 §12 第二条 / P-E08a）：验证证据收集工具。
    按 docs/verification/templates/evidence-template.md 的字段口径，收集"机器可推导"的证据字段，
    生成 Markdown 骨架供验收报告引用。

.DESCRIPTION
    只收集、不上传、不改 CI、不改产品源码；除 -OutFile 指定的文件外不写任何仓库内容。
    收集内容：
      §1 源 SHA + dirty 补丁摘要（git status --porcelain 逐行 + git diff 输出 SHA-256）
      §2 配置（构建配置、输出路径覆盖记录位、相关环境变量）
      §3 测试程序集 / 主程序 / Host / Contracts 的 SHA-256 摘要
      §4 依赖锁定情况（各工程 obj/project.assets.json 的依赖摘要；仓库未启用 packages.lock.json）
      §5 本次脚本的调用命令行（供报告"执行命令"段引用）
      §6 TRX 计数器（total/executed/passed/failed/notExecuted）+ 跳过用例逐条清单
      §7 机器环境（OS/CPU/内存/.NET SDK/PowerShell）
    §6 的逐项原因与报告解释属人工字段，脚本只列事实，不做结论。

.EXAMPLE
    pwsh -NoProfile -File scripts/collect-evidence.ps1 -RepoRoot . -Configuration Debug `
        -OutFile .tmp/evidence-e08a.md -TrxRoot .tmp/e08a/full-results

.NOTES
    退出码：0 成功；1 参数/IO 错误；2 部分字段缺失（仍会产出骨架，报告须注明缺失项）。
#>
[CmdletBinding()]
param(
    # 仓库根（默认当前目录；须含 LiteMonitor.sln）
    [string]$RepoRoot = '.',

    # 构建配置（决定去哪些 bin 目录找产物）
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # 输出 Markdown 路径（默认 <RepoRoot>\.tmp\evidence-<yyyyMMdd-HHmmss>.md）
    [string]$OutFile = '',

    # TRX 根目录：递归收集 *.trx 的计数器与跳过清单（可选）
    [string]$TrxRoot = '',

    # 附各工程 project.assets.json 依赖摘要（可选，默认收集）
    [switch]$SkipAssetsJson,

    # 实际使用的 Host 产物路径（默认 src/HardwareHost/bin/<配置>/net8.0-windows/LiteMonitor.HardwareHost.exe；
    # 自定义输出目录运行时按实际路径传入）
    [string]$HostExe = ''
)

$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '') } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
}

function Get-GitOutput {
    param([string]$RepoRoot, [string[]]$GitArgs)
    $spec = New-Object System.Diagnostics.ProcessStartInfo
    $spec.FileName = 'git'
    $spec.Arguments = ($GitArgs | ForEach-Object { $_ }) -join ' '
    $spec.WorkingDirectory = $RepoRoot
    $spec.UseShellExecute = $false
    $spec.RedirectStandardOutput = $true
    $spec.RedirectStandardError = $true
    $proc = [System.Diagnostics.Process]::Start($spec)
    $out = $proc.StandardOutput.ReadToEnd()
    $err = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "git $($GitArgs -join ' ') 失败（退出码 $($proc.ExitCode)）：$err" }
    return $out
}

# ── 解析参数 ────────────────────────────────────────────────────────────────
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
if (-not (Test-Path (Join-Path $RepoRoot 'LiteMonitor.sln'))) { throw "仓库根不含 LiteMonitor.sln：$RepoRoot" }
if (-not $OutFile) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $OutFile = Join-Path $RepoRoot (".tmp\evidence-$stamp.md")
}
$OutFile = [System.IO.Path]::GetFullPath($OutFile)
$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$netTfm = 'net8.0-windows'
$partial = $false

# ── §1 源状态 ────────────────────────────────────────────────────────────────
$headSha = (Get-GitOutput $RepoRoot @('rev-parse', 'HEAD')).TrimEnd()
$branch  = (Get-GitOutput $RepoRoot @('rev-parse', '--abbrev-ref', 'HEAD')).TrimEnd()
$statusRaw = Get-GitOutput $RepoRoot @('status', '--porcelain')
$diffText  = Get-GitOutput $RepoRoot @('diff', 'HEAD')

$diffSha = ''
if ($diffText) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($diffText)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $diffSha = [System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '' } finally { $sha.Dispose() }
}
$sourceSection = New-Object System.Text.StringBuilder
[void]$sourceSection.AppendLine("| 字段 | 值 |")
[void]$sourceSection.AppendLine("|---|---|")
[void]$sourceSection.AppendLine("| 分支 | $branch |")
[void]$sourceSection.AppendLine("| 源 SHA（HEAD） | $headSha |")
if ($statusRaw.Trim()) {
    [void]$sourceSection.AppendLine("| 工作树状态 | dirty |")
    [void]$sourceSection.AppendLine("")
    [void]$sourceSection.AppendLine("git status --porcelain：")
    [void]$sourceSection.AppendLine('```text')
    [void]$sourceSection.AppendLine($statusRaw.TrimEnd())
    [void]$sourceSection.AppendLine('```')
    if ($diffSha) { [void]$sourceSection.AppendLine("git diff HEAD 输出 SHA-256：``$diffSha``") }
    else { [void]$sourceSection.AppendLine("git diff HEAD：无已跟踪文件改动（diff SHA 不适用）") }
} else {
    [void]$sourceSection.AppendLine("| 工作树状态 | 干净（对 HEAD） |")
}

# ── §2 配置 ────────────────────────────────────────────────────────────────
$nugetPackages = $env:NUGET_PACKAGES
if (-not $nugetPackages) { $nugetPackages = '（未设置，使用用户级默认缓存）'; $partial = $true }
$dotnetCliHome = $env:DOTNET_CLI_HOME
if (-not $dotnetCliHome) { $dotnetCliHome = '（未设置）' }
$hostExeEnv = $env:LITEMONITOR_HOST_EXE
if (-not $hostExeEnv) { $hostExeEnv = '（未设置，走默认构建目录）' }

# ── §3 产物摘要 ────────────────────────────────────────────────────────────
$testAssemblies = @('LiteMonitor.Tests', 'LiteMonitor.Updater.Tests', 'LiteMonitor.GpuNonperformance.Tests')
$artifactSection = New-Object System.Text.StringBuilder
[void]$artifactSection.AppendLine('#### 3.1 测试程序集')
[void]$artifactSection.AppendLine("")
[void]$artifactSection.AppendLine('| 程序集 | 路径 | SHA-256 | 修改时间(UTC) |')
[void]$artifactSection.AppendLine('|---|---|---|---|')
foreach ($name in $testAssemblies) {
    $dll = Join-Path $RepoRoot ("tests\$name\bin\$Configuration\$netTfm\$name.dll")
    if (Test-Path $dll) {
        [void]$artifactSection.AppendLine("| $name | ``$dll`` | $(Get-Sha256 $dll) | $((Get-Item $dll).LastWriteTimeUtc.ToString('u')) |")
    } else {
        [void]$artifactSection.AppendLine("| $name | ``$dll`` | 未找到（未构建/未运行） | — |")
        $partial = $true
    }
}
[void]$artifactSection.AppendLine("")
[void]$artifactSection.AppendLine('#### 3.2 主程序 / Host / Contracts')
[void]$artifactSection.AppendLine("")
[void]$artifactSection.AppendLine('| 产物 | 路径 | SHA-256 | 备注 |')
[void]$artifactSection.AppendLine('|---|---|---|---|')

$mainExe = Join-Path $RepoRoot ("bin\$Configuration\$netTfm\LiteMonitor++.exe")
$mainDll = Join-Path $RepoRoot ("bin\$Configuration\$netTfm\LiteMonitor++.dll")
foreach ($p in @(@('主程序 exe', $mainExe), @('主程序 dll', $mainDll))) {
    if (Test-Path $p[1]) { [void]$artifactSection.AppendLine("| $($p[0]) | ``$($p[1])`` | $(Get-Sha256 $p[1]) | 主输出目录 |") }
    else { [void]$artifactSection.AppendLine("| $($p[0]) | ``$($p[1])`` | 未找到 | 主输出目录 |"); $partial = $true }
}

if (-not $HostExe) {
    $HostExe = Join-Path $RepoRoot ("src\HardwareHost\bin\$Configuration\$netTfm\LiteMonitor.HardwareHost.exe")
}
$hostExeFull = [System.IO.Path]::GetFullPath($HostExe)
$hostDir = Split-Path $hostExeFull -Parent
if (Test-Path $hostExeFull) {
    [void]$artifactSection.AppendLine("| Host exe | ``$hostExeFull`` | $(Get-Sha256 $hostExeFull) | 测试实际定位目录 |")
    $hostDll = Join-Path $hostDir 'LiteMonitor.HardwareHost.dll'
    if (Test-Path $hostDll) { [void]$artifactSection.AppendLine("| Host dll | ``$hostDll`` | $(Get-Sha256 $hostDll) | 与测试清单校验同源 |") }
    $contractsInHost = Join-Path $hostDir 'LiteMonitor.Contracts.dll'
    if (Test-Path $contractsInHost) { [void]$artifactSection.AppendLine("| Contracts（Host 目录副本） | ``$contractsInHost`` | $(Get-Sha256 $contractsInHost) | |") }
    $testOutDir = Join-Path $RepoRoot ("tests\LiteMonitor.Tests\bin\$Configuration\$netTfm")
    $contractsInTestOut = Join-Path $testOutDir 'LiteMonitor.Contracts.dll'
    if (Test-Path $contractsInTestOut) { [void]$artifactSection.AppendLine("| Contracts（测试输出引用副本） | ``$contractsInTestOut`` | $(Get-Sha256 $contractsInTestOut) | |") }
} else {
    [void]$artifactSection.AppendLine("| Host exe | ``$hostExeFull`` | 未找到 | 检查 -HostExe / BaseOutputPath |")
    $partial = $true
}

# ── §4 依赖锁定情况 ────────────────────────────────────────────────────────
$assetsSection = New-Object System.Text.StringBuilder
if (-not $SkipAssetsJson) {
    [void]$assetsSection.AppendLine('| 工程 | project.assets.json | 依赖库数量 | 直接依赖 |')
    [void]$assetsSection.AppendLine('|---|---|---|---|')
    # 只枚举已知工程的 obj/（避免全仓库递归扫到 .worktrees/.nuget-packages 等本机目录）
    $objDirs = @((Join-Path $RepoRoot 'obj')) + @(
        (Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'obj' }),
        (Get-ChildItem -Path (Join-Path $RepoRoot 'tests') -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'obj' }),
        (Get-ChildItem -Path (Join-Path $RepoRoot 'tools') -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'obj' }),
        (Join-Path $RepoRoot 'LiteMonitor.Updater\obj'))
    $assetsFiles = @($objDirs | Where-Object { Test-Path (Join-Path $_ 'project.assets.json') } | ForEach-Object { Join-Path $_ 'project.assets.json' })
    foreach ($assetsPath in $assetsFiles) {
        try {
            $json = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $projectName = $json.project.restore.projectName
            if (-not $projectName) { $projectName = (Split-Path (Split-Path $assetsPath -Parent) -Parent) }
            $libCount = @($json.libraries.PSObject.Properties).Count
            $deps = @()
            foreach ($fw in $json.project.frameworks.PSObject.Properties) {
                foreach ($d in $fw.Value.dependencies.PSObject.Properties) {
                    $ver = $d.Value.version
                    if (-not $ver) { $ver = $d.Value.requested }
                    $deps += ("$($d.Name)/$ver")
                }
            }
            $depText = (@($deps | Select-Object -Unique) -join ', ')
            if (-not $depText) { $depText = '（无直接依赖声明）' }
            if ($depText.Length -gt 220) { $depText = $depText.Substring(0, 217) + '…' }
            [void]$assetsSection.AppendLine("| $projectName | ``$assetsPath`` | $libCount | $depText |")
        } catch {
            [void]$assetsSection.AppendLine("| ? | ``$assetsPath`` | 解析失败：$($_.Exception.Message) | |")
            $partial = $true
        }
    }
    if ($assetsFiles.Count -eq 0) { [void]$assetsSection.AppendLine('| — | 未找到 project.assets.json（未 restore？） | — | — |'); $partial = $true }
}

# ── §6 TRX 计数器 ──────────────────────────────────────────────────────────
$trxSection = New-Object System.Text.StringBuilder
$trxFiles = @()
if ($TrxRoot) {
    $trxRootFull = [System.IO.Path]::GetFullPath($TrxRoot)
    if (Test-Path $trxRootFull) { $trxFiles = @(Get-ChildItem -Path $trxRootFull -Recurse -Filter '*.trx' -File) }
    else { Write-Warning "TRX 根目录不存在：$trxRootFull"; $partial = $true }
}
if ($trxFiles.Count -gt 0) {
    [void]$trxSection.AppendLine('| TRX 文件 | total(计数器) | executed(计数器) | passed(逐用例) | failed(逐用例) | 跳过(逐用例 NotExecuted) |')
    [void]$trxSection.AppendLine('|---|---|---|---|---|---|')
    foreach ($trx in $trxFiles) {
        try {
            [xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw -Encoding UTF8
            $counters = $doc.DocumentElement.SelectNodes("//*[local-name()='Counters']")[0]
            # ★ 逐用例 outcome 统计：xunit 的跳过在 TRX 里体现为 outcome=NotExecuted 的用例，
            #   而 Counters 的 notExecuted 计数器可能为 0（不能把计数器为 0 误当成没有跳过）。
            $outcomes = @{}
            foreach ($r in $doc.DocumentElement.SelectNodes("//*[local-name()='UnitTestResult']")) {
                $o = $r.GetAttribute('outcome')
                if (-not $outcomes.ContainsKey($o)) { $outcomes[$o] = 0 }
                $outcomes[$o]++
            }
            $passedCount = if ($outcomes.ContainsKey('Passed')) { $outcomes['Passed'] } else { 0 }
            $failedCount = if ($outcomes.ContainsKey('Failed')) { $outcomes['Failed'] } else { 0 }
            $skippedCount = if ($outcomes.ContainsKey('NotExecuted')) { $outcomes['NotExecuted'] } else { 0 }
            [void]$trxSection.AppendLine(
                ("| ``{0}`` | {1} | {2} | {3} | {4} | {5} |" -f $trx.FullName,
                 $counters.total, $counters.executed, $passedCount, $failedCount, $skippedCount))
            $skipped = @($doc.DocumentElement.SelectNodes("//*[local-name()='UnitTestResult'][@outcome='NotExecuted']"))
            foreach ($s in $skipped) {
                $n = $s.GetAttribute('testName')
                [void]$trxSection.AppendLine("")
                [void]$trxSection.AppendLine("- 跳过用例（NotExecuted）：``$n``——原因【人工填写】")
            }
        } catch {
            Write-Warning "TRX 解析失败：$($trx.FullName)：$($_.Exception.Message)"
            $partial = $true
        }
    }
    [void]$trxSection.AppendLine("")
    [void]$trxSection.AppendLine('> 跳过数以逐用例 outcome 统计为准；TRX Counters 的 notExecuted 计数器在 xunit 下可能为 0，不作为跳过证据。')
} else {
    [void]$trxSection.AppendLine('（未提供 -TrxRoot 或目录内无 TRX——测试结果数字留待人工按 TRX 填写）')
}

# ── §7 机器环境 ────────────────────────────────────────────────────────────
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$ramGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
$dotnetVersion = '（dotnet 不可用）'
try { $v = & dotnet --version 2>$null; if ($LASTEXITCODE -eq 0 -and $v) { $dotnetVersion = "$v" } } catch { }

# ── 组装 Markdown ──────────────────────────────────────────────────────────
$invocationParts = @($PSCommandPath)
foreach ($entry in $PSBoundParameters.GetEnumerator()) {
    if ($entry.Value -is [switch]) { $invocationParts += "-$($entry.Key)" }
    else { $invocationParts += "-$($entry.Key) '$($entry.Value)'" }
}
$invocation = $invocationParts -join ' '

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# 验证证据记录（collect-evidence.ps1 生成）')
[void]$md.AppendLine("")
[void]$md.AppendLine("> 生成时间：$((Get-Date).ToString('u'))；生成工具：scripts/collect-evidence.ps1（只收集，不上传/不改 CI）。")
[void]$md.AppendLine("> ⚠ 标注【人工】的段落（跳过原因、失败处置、未执行项说明、结论解释）必须人工补齐后方可作为验收证据。")
[void]$md.AppendLine("")
[void]$md.AppendLine('## 1. 源状态')
[void]$md.AppendLine("")
[void]$md.AppendLine($sourceSection.ToString())
[void]$md.AppendLine('## 2. 配置')
[void]$md.AppendLine("")
[void]$md.AppendLine("| 字段 | 值 |")
[void]$md.AppendLine('|---|---|')
[void]$md.AppendLine("| 构建配置 | $Configuration |")
[void]$md.AppendLine("| TargetFramework | $netTfm |")
[void]$md.AppendLine("| NUGET_PACKAGES | $nugetPackages |")
[void]$md.AppendLine("| DOTNET_CLI_HOME | $dotnetCliHome |")
[void]$md.AppendLine("| LITEMONITOR_HOST_EXE | $hostExeEnv |")
[void]$md.AppendLine("| 输出路径覆盖 | 【人工】记录本次 -p:BaseOutputPath 等覆盖（如有） |")
[void]$md.AppendLine("")
[void]$md.AppendLine('## 3. 产物摘要（SHA-256）')
[void]$md.AppendLine("")
[void]$md.AppendLine($artifactSection.ToString())
[void]$md.AppendLine('## 4. 依赖锁定情况')
[void]$md.AppendLine("")
[void]$md.AppendLine('> 仓库未启用 packages.lock.json，以各工程 obj/project.assets.json 为准。')
[void]$md.AppendLine("")
if ($SkipAssetsJson) { [void]$md.AppendLine('（-SkipAssetsJson：未收集）') } else { [void]$md.AppendLine($assetsSection.ToString()) }
[void]$md.AppendLine('## 5. 执行命令')
[void]$md.AppendLine("")
[void]$md.AppendLine('```text')
[void]$md.AppendLine($invocation)
[void]$md.AppendLine('```')
[void]$md.AppendLine('【人工】补齐本轮全部测试/构建命令与退出码。')
[void]$md.AppendLine("")
[void]$md.AppendLine('## 6. 测试结果（TRX 口径）')
[void]$md.AppendLine("")
[void]$md.AppendLine($trxSection.ToString())
[void]$md.AppendLine('## 7. 机器环境')
[void]$md.AppendLine("")
[void]$md.AppendLine("| 字段 | 值 |")
[void]$md.AppendLine('|---|---|')
[void]$md.AppendLine("| OS | $($os.Caption) $($os.Version)（Build $($os.BuildNumber)） |")
[void]$md.AppendLine("| CPU | $cpu |")
[void]$md.AppendLine("| 可见内存 | $ramGb GB |")
[void]$md.AppendLine("| .NET SDK | $dotnetVersion |")
[void]$md.AppendLine("| PowerShell | $($PSVersionTable.PSVersion) |")
[void]$md.AppendLine("")
[void]$md.AppendLine('## 8. 人工记录与解释')
[void]$md.AppendLine("")
[void]$md.AppendLine('- 结论：')
[void]$md.AppendLine('- 跳过/未执行项逐条原因：')
[void]$md.AppendLine('- 与既有报告（审查报告 G1～G5、R1～R9）条目对应关系：')

[System.IO.File]::WriteAllText($OutFile, $md.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Host "证据骨架已写入：$OutFile"
if ($partial) {
    Write-Warning "部分字段缺失（见输出中的 '未找到/解析失败/（未设置）' 标记）——报告中必须注明缺失原因。"
    exit 2
}
exit 0
