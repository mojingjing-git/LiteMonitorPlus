#requires -Version 7.0
<#
.SYNOPSIS
    WPA / xperf 内核 CPU 取证采集器（P-F15 / 上游 issue #416 取证工具）。

.DESCRIPTION
    按**场景**采集 ETW 轨迹，产出可在 WPA 里打开的 .etl，用于：
      · CPU Usage (System) 火焰图
      · DPC / ISR 采样与归因
      · 与「应用完全关闭」基线做差值

    ★ 本工具**不下结论**。它只负责：按场景准备环境 → 起采集 → 停采集 → 留下可复现的元数据。

    ★ 采集后端优先级：
      1. `wpr.exe`（Windows 自带，`C:\Windows\System32\wpr.exe`）—— 用内置 `CPU` profile。
      2. `xperf.exe`（Windows Performance Toolkit / WDK，**本机未安装**）—— 用 PROC_THREAD+PROFILE。
      3. 两者都没有 ⇒ 生成本地「人工命令单」并以退出码 2 结束（**不静默成功**）。

    ★ 隔离：应用从**沙箱目录**运行（settings.json / 日志 / 历史数据全落沙箱），
      绝不覆盖用户 settings.json。

    ★ 权限：`wpr -start` / `xperf -on` 需要**管理员**。非管理员时脚本会明确报错并停下。

.PARAMETER Scenario
    场景 id，见 tools/forensics/scenarios.psd1。默认 `default`。
    可用：app-closed / default / smb-on / smb-off / fps-on / fps-off /
          gpu-all / gpu-one / etw-exclusion-on / low-refresh / high-refresh / no-win-counters

.PARAMETER Minutes
    采集时长（分钟）。不给则用场景自带的 captureMinutes。

.PARAMETER WarmupMinutes
    启动应用后到开始采集之间的静默期（分钟）。不给则用场景自带的 warmupMinutes。

.PARAMETER OutputDir
    证据输出目录。默认 <仓库根>\docs\reviews\evidence\forensics\wpa\<场景>-<时间戳>。

.PARAMETER LaunchSourceDir
    主工程的**构建输出目录**（含 LiteMonitor++.exe）。默认 <仓库根>\bin\Release\net8.0-windows。

.PARAMETER WprProfile
    wpr 内置 profile 名，默认 `CPU`（含 CPU Usage + 采样栈）。

.PARAMETER ListScenarios
    只打印场景清单，不采集。

.PARAMETER DryRun
    自检：校验场景/参数、探测 wpr/xperf 可用性、准备沙箱（不启动）、写 manifest 后退出。

.EXAMPLE
    pwsh tools/forensics/wpa-capture.ps1 -ListScenarios

.EXAMPLE
    pwsh tools/forensics/wpa-capture.ps1 -Scenario app-closed -Minutes 30

.EXAMPLE
    pwsh tools/forensics/wpa-capture.ps1 -Scenario default -Minutes 30 -DryRun
#>
[CmdletBinding()]
param(
    [string] $Scenario = 'default',
    [int]    $Minutes = 0,
    [int]    $WarmupMinutes = -1,
    [string] $OutputDir,
    [string] $LaunchSourceDir,
    [string] $WprProfile = 'CPU',
    [switch] $ListScenarios,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
Assert-Pwsh7

$scenarioPath = Join-Path $PSScriptRoot 'scenarios.psd1'
$allScenarios = Import-PowerShellDataFile -LiteralPath $scenarioPath

if ($ListScenarios) {
    # ★ 用 Write-Output（而不是 Write-Host）⇒ 清单可以被 `| ConvertTo-Json` 之类管道消费。
    Write-Output '可用场景（tools/forensics/scenarios.psd1）：'
    Write-Output ''
    foreach ($k in ($allScenarios.Keys | Sort-Object)) {
        $s = $allScenarios[$k]
        Write-Output ("  {0,-20} {1}" -f $k, $s.title)
        Write-Output ("  {0,-20} 采集 {1} min / 预热 {2} min / 启动应用={3}" -f '', $s.captureMinutes, $s.warmupMinutes, $s.launchApp)
        Write-Output ("  {0,-20} 目的：{1}" -f '', $s.purpose)
        foreach ($l in $s.levers) { Write-Output ("  {0,-20}   · {1}" -f '', $l) }
        foreach ($l in $s.nonLevers) { Write-Output ("  {0,-20}   ! {1}" -f '', $l) }
        Write-Output ''
    }
    exit 0
}

if (-not $allScenarios.ContainsKey($Scenario)) {
    throw ("未知场景 '{0}'。可用：{1}（-ListScenarios 查看详情）" -f $Scenario, (($allScenarios.Keys | Sort-Object) -join ', '))
}
$sc = $allScenarios[$Scenario]

if ($Minutes -le 0) { $Minutes = [int]$sc.captureMinutes }
if ($WarmupMinutes -lt 0) { $WarmupMinutes = [int]$sc.warmupMinutes }

if ($Minutes -lt 1) { throw "-Minutes 必须 ≥ 1" }
if ($WarmupMinutes -lt 0) { throw "-WarmupMinutes 必须 ≥ 0" }

# ── 采集后端探测 ─────────────────────────────────────────────────────────

function Get-CaptureBackend {
    $wpr = Get-Command 'wpr.exe' -ErrorAction SilentlyContinue
    $xperf = Get-Command 'xperf.exe' -ErrorAction SilentlyContinue
    if ($wpr) { return @{ kind = 'wpr'; exe = $wpr.Source } }
    if ($xperf) { return @{ kind = 'xperf'; exe = $xperf.Source } }
    return @{ kind = $null; exe = $null; wprPath = 'C:\Windows\System32\wpr.exe' }
}

$backend = Get-CaptureBackend
$outDir = New-ForensicsOutputDir -Name ("wpa-" + $Scenario) -OutputDir $OutputDir
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$etlPath = Join-Path $outDir ("{0}-{1}.etl" -f $Scenario, $stamp)

Write-Host ("场景      : {0} —— {1}" -f $Scenario, $sc.title)
Write-Host ("采集时长  : {0} min（预热 {1} min）" -f $Minutes, $WarmupMinutes)
Write-Host ("输出目录  : {0}" -f $outDir)
Write-Host ("采集后端  : {0}" -f $(if ($backend.kind) { "$($backend.kind) → $($backend.exe)" } else { '**未找到 wpr.exe / xperf.exe**' }))
Write-Host ("应用启动  : {0}" -f $sc.launchApp)
Write-Host ''

# 写一份"人工命令单"，无论有没有工具都留下（工具缺失时它是唯一可执行路径）
$manual = @"
# 人工采集命令单 —— 场景 $Scenario

生成时间：$((Get-Date).ToString('o'))
机器：$env:COMPUTERNAME

## 1) 准备应用（沙箱隔离，不碰用户 settings.json）

``````powershell
pwsh tools/forensics/wpa-capture.ps1 -Scenario $Scenario -Minutes $Minutes
``````

## 2) 采集（管理员 PowerShell）

**wpr（Windows 自带，推荐）**

``````powershell
wpr.exe -status
wpr.exe -start $WprProfile -filemode
# ... 让 LiteMonitor++ 在这段时间里运行 ...
wpr.exe -stop "$etlPath" "F15 scenario=$Scenario"
``````

**xperf（Windows Performance Toolkit，需安装 WDK）**

``````powershell
xperf.exe -on PROC_THREAD+PROFILE+LOADER -stackwalk Profile
# ... 同上 ...
xperf.exe -stop "$etlPath"
``````

## 3) 在 WPA 里看什么

1. 打开 .etl → **CPU Usage (System)** 火焰图 ⇒ 观察 `System` 进程下 DPC / ISR 的占比与栈。
2. 打开 **CPU Usage (Precise)** ⇒ 定位到具体驱动/内核函数。
3. 把本场景的火焰图与 **app-closed** 场景的火焰图**并排比较**，
   差值才是 LiteMonitor++ 带来的系统侧增量（单看一张图无法归因）。

## 4) 场景开关清单

$(($sc.levers | ForEach-Object { "- $_" }) -join "`n")

### 无法用配置表达的部分（诚实标注）

$(($sc.nonLevers | ForEach-Object { "- $_" }) -join "`n")
"@
Write-RedactedFile -Path (Join-Path $outDir 'MANUAL-COMMANDS.md') -Text $manual

if (-not $backend.kind) {
    Write-Host '★ 本机既没有 wpr.exe 也没有 xperf.exe —— 无法自动采集。' -ForegroundColor Yellow
    Write-Host ("  已生成人工命令单：{0}" -f (Join-Path $outDir 'MANUAL-COMMANDS.md'))
    Write-Host '  wpr.exe 通常位于 C:\Windows\System32\wpr.exe（Windows 10/11 自带）；'
    Write-Host '  若确实缺失，请安装 "Windows Performance Toolkit"（随 WDK 提供 xperf.exe / wpa.exe）。'
    $manifest = [ordered]@{
        schemaVersion = 1
        tool          = 'tools/forensics/wpa-capture.ps1'
        mode          = 'no-backend'
        scenario      = $Scenario
        scenarioTitle = $sc.title
        parameters    = [ordered]@{ minutes = $Minutes; warmupMinutes = $WarmupMinutes; wprProfile = $WprProfile }
        outputs       = @('MANUAL-COMMANDS.md')
        environment   = (Get-ForensicsEnvironment)
    }
    Write-JsonFile -Path (Join-Path $outDir 'capture-manifest.json') -Value $manifest -Depth 8
    exit 2
}

# ── 应用准备 ─────────────────────────────────────────────────────────────

$sandbox = $null
$appPid = 0
$settingsUsed = $null

if ($sc.launchApp) {
    $srcDir = if ($LaunchSourceDir) {
        if ([IO.Path]::IsPathRooted($LaunchSourceDir)) { $LaunchSourceDir } else { Join-Path (Get-RepoRoot) $LaunchSourceDir }
    }
    else {
        Join-Path (Get-RepoRoot) 'bin\Release\net8.0-windows'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $srcDir 'LiteMonitor++.exe'))) {
        throw ("在 {0} 找不到 LiteMonitor++.exe。请先构建：dotnet build LiteMonitor.csproj -c Release" -f $srcDir)
    }
    $settings = $null
    if ($null -ne $sc.settings) {
        $settings = @{}
        foreach ($k in $sc.settings.Keys) { $settings[$k] = $sc.settings[$k] }
    }
    $sandbox = New-SandboxAppInstance -SourceDir $srcDir -SandboxRoot $outDir -SettingsJson $settings -Start:(-not $DryRun)
    $appPid = $sandbox.ProcessId
    $settingsUsed = if (Test-Path -LiteralPath $sandbox.SettingsPath) { $sandbox.SettingsPath } else { $null }
    Write-Host ("沙箱目录  : {0}" -f $sandbox.SandboxDir)
    if ($settingsUsed) { Write-Host ("沙箱配置  : {0}（用户 settings.json 未被触碰）" -f $settingsUsed) }
    else { Write-Host '沙箱配置  : （本场景不覆盖任何字段，走产品默认值）' }
}
else {
    Write-Host '本场景不启动应用 —— 请确认目标机器上**没有**正在运行的 LiteMonitor++ 实例。'
}

# ── 采集 ─────────────────────────────────────────────────────────────────

$commands = New-Object System.Collections.Generic.List[string]
$log = New-Object System.Collections.Generic.List[string]

function Add-Log {
    param([string] $Text)
    $log.Add("[$((Get-Date).ToString('o'))] $Text")
    Write-Host $Text
}

$statusBefore = $null
if ($backend.kind -eq 'wpr') {
    $r = Invoke-NativeCapture -FilePath $backend.exe -Arguments @('-status') -TimeoutSeconds 60
    $statusBefore = ($r.Out + $r.Err)
    Add-Log ("wpr -status（采集前）：`n" + $statusBefore)
}

if ($DryRun) {
    Add-Log 'DRY-RUN：不实际启动/停止采集。'
    if ($backend.kind -eq 'wpr') {
        $commands.Add("$($backend.exe) -start $WprProfile -filemode")
        $commands.Add("$($backend.exe) -stop `"$etlPath`" `"F15 scenario=$Scenario`"")
    }
    else {
        $commands.Add("$($backend.exe) -on PROC_THREAD+PROFILE+LOADER -stackwalk Profile")
        $commands.Add("$($backend.exe) -stop `"$etlPath`"")
    }
}
else {
    if ($WarmupMinutes -gt 0) {
        Add-Log ("预热 {0} min（不采集）…" -f $WarmupMinutes)
        Start-Sleep -Seconds ($WarmupMinutes * 60)
    }

    if ($backend.kind -eq 'wpr') {
        $startArgs = @('-start', $WprProfile, '-filemode')
        $commands.Add(($backend.exe + ' ' + ($startArgs -join ' ')))
        $r = Invoke-NativeCapture -FilePath $backend.exe -Arguments $startArgs
        Add-Log ("wpr -start $WprProfile -filemode → exit=" + $r.Exit + "`n" + $r.Out + $r.Err)
        if ($r.Exit -ne 0) {
            Write-RedactedFile -Path (Join-Path $outDir 'capture-log.txt') -Text ($log -join "`n")
            $tail = (($r.Out + $r.Err) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6) -join ' | '
            throw ("wpr -start 失败（exit={0}）。★ 需要**管理员**权限；同一时刻只能有一个 wpr 会话。" -f $r.Exit) +
                  "`n  wpr 输出：$tail" +
                  "`n  完整日志：$(Join-Path $outDir 'capture-log.txt')"
        }
    }
    else {
        $startArgs = @('-on', 'PROC_THREAD+PROFILE+LOADER', '-stackwalk', 'Profile')
        $commands.Add(($backend.exe + ' ' + ($startArgs -join ' ')))
        $r = Invoke-NativeCapture -FilePath $backend.exe -Arguments $startArgs
        Add-Log ("xperf -on … → exit=" + $r.Exit + "`n" + $r.Out + $r.Err)
        if ($r.Exit -ne 0) {
            Write-RedactedFile -Path (Join-Path $outDir 'capture-log.txt') -Text ($log -join "`n")
            $tail = (($r.Out + $r.Err) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6) -join ' | '
            throw ("xperf -on 失败（exit={0}）。★ 需要**管理员**权限。" -f $r.Exit) +
                  "`n  xperf 输出：$tail" +
                  "`n  完整日志：$(Join-Path $outDir 'capture-log.txt')"
        }
    }

    Add-Log ("采集 {0} min …" -f $Minutes)
    Start-Sleep -Seconds ($Minutes * 60)

    if ($backend.kind -eq 'wpr') {
        $stopArgs = @('-stop', $etlPath, "F15 scenario=$Scenario")
        $commands.Add(($backend.exe + ' -stop "' + $etlPath + '" "F15 scenario=' + $Scenario + '"'))
        $r = Invoke-NativeCapture -FilePath $backend.exe -Arguments $stopArgs -TimeoutSeconds 600
        Add-Log ("wpr -stop → exit=" + $r.Exit + "`n" + $r.Out + $r.Err)
    }
    else {
        $stopArgs = @('-stop', $etlPath)
        $commands.Add(($backend.exe + ' -stop "' + $etlPath + '"'))
        $r = Invoke-NativeCapture -FilePath $backend.exe -Arguments $stopArgs -TimeoutSeconds 600
        Add-Log ("xperf -stop → exit=" + $r.Exit + "`n" + $r.Out + $r.Err)
    }
}

$statusAfter = $null
if ($backend.kind -eq 'wpr') {
    $r = Invoke-NativeCapture -FilePath $backend.exe -Arguments @('-status') -TimeoutSeconds 60
    $statusAfter = ($r.Out + $r.Err)
    Add-Log ("wpr -status（采集后）：`n" + $statusAfter)
}

Write-RedactedFile -Path (Join-Path $outDir 'capture-log.txt') -Text ($log -join "`n")

# ── 清单 ──────────────────────────────────────────────────────────────────

$manifest = [ordered]@{
    schemaVersion  = 1
    tool           = 'tools/forensics/wpa-capture.ps1'
    mode           = if ($DryRun) { 'dry-run' } else { 'capture' }
    scenario       = $Scenario
    scenarioTitle  = $sc.title
    purpose        = $sc.purpose
    parameters     = [ordered]@{
        minutes        = $Minutes
        warmupMinutes  = $WarmupMinutes
        wprProfile     = $WprProfile
        launchSourceDir = if ($LaunchSourceDir) { $LaunchSourceDir } else { 'bin\Release\net8.0-windows' }
    }
    backend        = [ordered]@{ kind = $backend.kind; exe = $backend.exe }
    commands       = $commands.ToArray()
    app            = [ordered]@{
        launched = [bool]$sc.launchApp
        pid      = $appPid
        sandbox  = if ($sandbox) { $sandbox.SandboxDir } else { $null }
        settings = if ($sc.settings) { $sc.settings } else { @{} }
    }
    levers         = @($sc.levers)
    nonLevers      = @($sc.nonLevers)
    compareWith    = @($sc.compareWith)
    etl            = if (Test-Path -LiteralPath $etlPath) { $etlPath } else { $null }
    outputs        = @('capture-manifest.json', 'capture-log.txt', 'MANUAL-COMMANDS.md')
    environment    = (Get-ForensicsEnvironment)
}
Write-JsonFile -Path (Join-Path $outDir 'capture-manifest.json') -Value $manifest -Depth 10

# ── 收尾 ──────────────────────────────────────────────────────────────────

if ($sandbox -and -not $DryRun) {
    Stop-SandboxAppInstance -SandboxDir $sandbox.SandboxDir
    Add-Log '沙箱实例已结束。'
}

Write-Host ''
Write-Host ("轨迹文件  : {0}" -f $(if (Test-Path -LiteralPath $etlPath) { $etlPath } else { '（未生成，见 capture-log.txt）' }))
Write-Host ("清单 JSON : {0}" -f (Join-Path $outDir 'capture-manifest.json'))
Write-Host ("人工命令单: {0}" -f (Join-Path $outDir 'MANUAL-COMMANDS.md'))
if ($DryRun) { Write-Host '★ 自检模式：未实际采集。' }
