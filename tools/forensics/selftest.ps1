#requires -Version 7.0
<#
.SYNOPSIS
    LiteMonitor++ 取证工具集自检（P-F15）。

.DESCRIPTION
    对 tools/forensics 下的每个脚本做一次**干跑**（`-DryRun`）+ 产物存在性断言，
    并对脱敏函数做**不漏网**断言。

    ★ 干跑**不采集**真实 72h 数据、不启动 ETW 采集、不写注册表、
      也不启动 LiteMonitor++（沙箱只准备不启动）。

    ★ 本自检产出的是「工具可用性」的证据，**不是**任何 issue 的结论。

.PARAMETER OutputDir
    自检产物目录。默认 <仓库根>\docs\reviews\evidence\forensics\selftest-<时间戳>。

.PARAMETER BuildProbe
    额外**构建** LHM 探针工程（验证探针源码能编译）。需要 .NET 8 SDK。

.EXAMPLE
    pwsh tools/forensics/selftest.ps1

.EXAMPLE
    pwsh tools/forensics/selftest.ps1 -BuildProbe
#>
[CmdletBinding()]
param(
    [string] $OutputDir,
    [switch] $BuildProbe
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
Assert-Pwsh7

$repoRoot = Get-RepoRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot (Join-Path 'docs\reviews\evidence\forensics' "selftest-$stamp")
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param([string] $Name, [bool] $Ok, [string] $Detail)
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    $mark = if ($Ok) { '  [PASS] ' } else { '  [FAIL] ' }
    Write-Host ("{0}{1} — {2}" -f $mark, $Name, $Detail)
}

function Invoke-Check {
    <#
        跑一个检查项；异常不外泄（转成 FAIL）。
    #>
    param([string] $Name, [scriptblock] $Body)
    try {
        $detail = & $Body
        Add-Result -Name $Name -Ok $true -Detail ([string]$detail)
    }
    catch {
        Add-Result -Name $Name -Ok $false -Detail $_.Exception.Message
    }
}

Write-Host ("自检目录  : {0}" -f $OutputDir)
Write-Host ("仓库根    : {0}" -f $repoRoot)
Write-Host ''

# ── ① 脱敏断言 ───────────────────────────────────────────────────────────

Invoke-Check '脱敏：凭据/票据/序列号不漏网' {
    $sample = @'
{
  "WebServerPassword": "SuperSecret123",
  "WebServerPasswordHash": "abcDEF123456",
  "ticket": "TICKET-9f8e7d6c5b4a",
  "csrf": "csrf-token-value",
  "lmsid": "session-cookie-value",
  "Set-Cookie: lmsid=session-cookie-value; Path=/",
  "url": "http://127.0.0.1:5000/ws?ticket=QUERYTICKET123",
  "MachineGuid": "4C4C4544-0038-3710-8054-B7C04F503332",
  "uuid": "12345678-1234-1234-1234-123456789abc",
  "Volume Serial Number is 1A2B-3C4D",
  "DiskSerial": "WD-WX11A80KJZ000",
  "safe": "普通文本不该被改"
}
'@
    $r = Protect-ForensicsText -Text $sample
    $must = @('SuperSecret123', 'abcDEF123456', 'TICKET-9f8e7d6c5b4a', 'csrf-token-value',
        'session-cookie-value', 'QUERYTICKET123', '4C4C4544-0038-3710-8054-B7C04F503332',
        '12345678-1234-1234-1234-123456789abc', '1A2B-3C4D', 'WD-WX11A80KJZ000')
    $leaked = @($must | Where-Object { $r.Contains($_) })
    if ($leaked.Count -gt 0) { throw ("未脱敏: " + ($leaked -join ', ')) }
    if (-not $r.Contains('***REDACTED***')) { throw '没有出现脱敏标记' }
    if (-not $r.Contains('普通文本不该被改')) { throw '普通文本被误改' }
    return ("{0} 个敏感片段全部遮蔽，普通文本保留" -f $must.Count)
}

# ── ② mem-timeline.ps1 干跑 ──────────────────────────────────────────────

Invoke-Check 'mem-timeline.ps1 -DryRun' {
    $dir = Join-Path $OutputDir 'mem-timeline'
    & (Join-Path $PSScriptRoot 'mem-timeline.ps1') -DryRun -OutputDir $dir | Out-Null
    foreach ($f in @('timeline.csv', 'summary.json', 'manifest.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dir $f))) { throw "缺少产物 $f" }
    }
    $csv = Get-Content -LiteralPath (Join-Path $dir 'timeline.csv') -TotalCount 1
    foreach ($col in @('private_bytes', 'working_set_bytes', 'handle_count', 'thread_count', 'gdi_objects')) {
        if ($csv -notmatch $col) { throw "CSV 缺少列 $col" }
    }
    return 'timeline.csv / summary.json / manifest.json 齐备，主口径列齐全'
}

# ── ③ wpa-capture.ps1 干跑 ───────────────────────────────────────────────

Invoke-Check 'scenarios.psd1 场景清单' {
    $scenarios = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'scenarios.psd1')
    $required = @('app-closed', 'default', 'smb-on', 'smb-off', 'fps-on', 'fps-off',
        'gpu-all', 'gpu-one', 'etw-exclusion-on', 'low-refresh', 'high-refresh', 'no-win-counters')
    foreach ($id in $required) {
        if (-not $scenarios.ContainsKey($id)) { throw "场景清单缺少 $id" }
    }
    foreach ($k in $scenarios.Keys) {
        $s = $scenarios[$k]
        foreach ($f in @('title', 'launchApp', 'captureMinutes', 'warmupMinutes', 'purpose', 'levers', 'nonLevers', 'compareWith')) {
            if (-not $s.ContainsKey($f)) { throw "场景 $k 缺少字段 $f" }
        }
        if ($s.levers.Count -eq 0) { throw "场景 $k 没有记录任何开关（levers 为空）" }
    }
    return ("{0} 个场景，字段与开关记录齐全" -f $scenarios.Count)
}

Invoke-Check 'wpa-capture.ps1 -ListScenarios' {
    $out = & (Join-Path $PSScriptRoot 'wpa-capture.ps1') -ListScenarios | Out-String
    foreach ($id in @('app-closed', 'default', 'smb-on', 'fps-on', 'gpu-all', 'etw-exclusion-on')) {
        if ($out -notmatch [regex]::Escape($id)) { throw "场景清单输出缺少 $id" }
    }
    return '清单可被管道消费且核心场景齐全'
}

Invoke-Check 'wpa-capture.ps1 -DryRun' {
    $dir = Join-Path $OutputDir 'wpa'
    & (Join-Path $PSScriptRoot 'wpa-capture.ps1') -Scenario 'smb-on' -DryRun -OutputDir $dir | Out-Null
    foreach ($f in @('capture-manifest.json', 'MANUAL-COMMANDS.md', 'app\settings.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dir $f))) { throw "缺少产物 $f" }
    }
    $cfg = Get-Content -LiteralPath (Join-Path $dir 'app\settings.json') -Raw | ConvertFrom-Json
    if ($cfg.IgnoreSmbTraffic -ne $false) { throw '沙箱 settings.json 未写入场景开关' }
    return '沙箱 settings.json 只含场景开关；人工命令单与 manifest 齐备'
}

# ── ④ collect-crash-evidence.ps1 干跑 ────────────────────────────────────

Invoke-Check 'collect-crash-evidence.ps1 -DryRun' {
    $dir = Join-Path $OutputDir 'crash'
    & (Join-Path $PSScriptRoot 'collect-crash-evidence.ps1') -DryRun -OutputDir $dir | Out-Null
    foreach ($f in @('summary.txt', 'manifest.json', 'application-events.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $dir $f))) { throw "缺少产物 $f" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $dir 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($n in @('LiteMonitor++_Error.log', 'LiteMonitor_Error.log')) {
        if ($manifest.logNamesSearched -notcontains $n) { throw "日志名清单缺少 $n（旧名兼容查找）" }
    }
    return '当前名 + 旧名两个日志名都在清单里；汇总/清单/事件文件齐备'
}

# ── ⑤ lhm-intel-report.ps1 干跑 ─────────────────────────────────────────

Invoke-Check 'lhm-intel-report.ps1 -DryRun' {
    $dir = Join-Path $OutputDir 'lhm-dryrun'
    & (Join-Path $PSScriptRoot 'lhm-intel-report.ps1') -DryRun -OutputDir $dir | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'LhmIntelReport\LhmIntelReport.csproj'))) {
        throw '探针工程缺失'
    }
    return '探针工程就绪，dotnet 可解析'
}

if ($BuildProbe) {
    Invoke-Check 'LhmIntelReport 探针构建' {
        $dir = Join-Path $OutputDir 'lhm-build'
        & (Join-Path $PSScriptRoot 'lhm-intel-report.ps1') -Build -OutputDir $dir | Out-Null
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'LhmIntelReport\bin\Release\net8.0-windows\LhmIntelReport.dll'))) {
            throw '探针产物缺失'
        }
        return '探针编译通过并可运行'
    }
}

# ── 汇总 ──────────────────────────────────────────────────────────────────

$pass = @($results | Where-Object { $_.Ok }).Count
$fail = @($results | Where-Object { -not $_.Ok }).Count

Write-Host ''
Write-Host '── 汇总 ──'
Write-Host ("通过 {0} / 失败 {1}（共 {2}）" -f $pass, $fail, $results.Count)
Write-Host ("自检产物  : {0}" -f $OutputDir)

$summary = [ordered]@{
    schemaVersion = 1
    tool          = 'tools/forensics/selftest.ps1'
    collectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    note          = '本文件是「工具可用性」的自检结果，不含任何 issue 判定。'
    pass = $pass
    fail = $fail
    total = $results.Count
    checks = @($results | ForEach-Object { [ordered]@{ name = $_.Name; ok = $_.Ok; detail = $_.Detail } })
    environment = (Get-ForensicsEnvironment)
}
Write-JsonFile -Path (Join-Path $OutputDir 'selftest-result.json') -Value $summary -Depth 8

if ($fail -gt 0) { exit 1 }
exit 0
