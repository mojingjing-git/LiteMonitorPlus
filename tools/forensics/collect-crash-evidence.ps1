#requires -Version 7.0
<#
.SYNOPSIS
    崩溃取证收集器（P-F15 / 上游 issue #582、#574 取证工具）。

.DESCRIPTION
    在**报告者的机器**上一次性收齐 #582 / #574 缺的东西：

      ① 崩溃日志本体
         - `LiteMonitor++_Error.log`   —— 当前品牌名（src/System/Program.cs:149）
         - `LiteMonitor_Error.log`     —— **旧品牌名，兼容查找**（docs/REBRAND.md:62 B5）
         - `LiteMonitor++_HardwareError.log` —— 硬件层异常（src/System/HardwareServices/HardwareErrorLog.cs）
      ② 报告者的**二进制版本**（#574 明确要求：堆栈与上游源码不自洽，必须知道版本号）
      ③ Windows 事件日志里的 Application Error / .NET Runtime 记录
      ④ （可选）打开 WER **LocalDumps**，让下一次崩溃留下 .dmp

    ★ 本工具**不判定**任何 issue 的根因；它只把证据收齐、落盘、列清单。

    ★ 输出**经脱敏**：口令 / 票据 / CSRF / 会话 cookie / 机器 GUID / 卷序列号 / 完整设备序列号
      一律替换（见 Common.psm1 的 Protect-ForensicsText）。

    ★ 只读：默认**不改**任何产品配置，也不写用户 settings.json。
      只有显式加 -EnableLocalDumps 才会写 HKCU 的 WER 注册表项（可随时用 -DisableLocalDumps 撤销）。

.PARAMETER InstallDirs
    明确指定 LiteMonitor++ 的安装/运行目录（可多个）。给了就只查这些目录。

.PARAMETER SearchRoots
    自动查找的根目录（可多个）。默认覆盖 C:\TOOLS\LiteMonitor、%LOCALAPPDATA%、%APPDATA%、
    %ProgramFiles%、%ProgramFiles(x86)%、%USERPROFILE%\Desktop、当前目录。

.PARAMETER OutputDir
    证据输出目录。默认 <仓库根>\docs\reviews\evidence\forensics\crash\<时间戳>。

.PARAMETER EnableLocalDumps
    打开 WER LocalDumps（HKCU），让下一次崩溃生成 .dmp。

.PARAMETER DisableLocalDumps
    关闭（并删除）本工具创建的 WER LocalDumps 注册表项。

.PARAMETER LocalDumpDir
    .dmp 落地目录。默认 <OutputDir>\dumps。

.PARAMETER DumpType
    0=无 1=小型 2=完整（默认 2=完整，.dmp 较大）。

.PARAMETER EventHours
    回看多少小时的 Windows 事件日志（默认 168 = 7 天）。

.PARAMETER DryRun
    只做自检：打印将要查找的目录、将要读取的日志名、将要改动的注册表项，**不做任何改动**。

.EXAMPLE
    pwsh tools/forensics/collect-crash-evidence.ps1 -DryRun

.EXAMPLE
    # 报告者机器上：收齐现有证据 + 打开 LocalDumps 等下一次崩溃
    pwsh tools/forensics/collect-crash-evidence.ps1 -EnableLocalDumps
#>
[CmdletBinding()]
param(
    [string[]] $InstallDirs,
    [string[]] $SearchRoots,
    [string]   $OutputDir,
    [switch]   $EnableLocalDumps,
    [switch]   $DisableLocalDumps,
    [string]   $LocalDumpDir,
    [int]      $DumpType = 2,
    [int]      $EventHours = 168,
    [switch]   $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
Assert-Pwsh7

if ($EnableLocalDumps -and $DisableLocalDumps) {
    throw '-EnableLocalDumps 与 -DisableLocalDumps 不能同时给。'
}
if ($DumpType -lt 0 -or $DumpType -gt 2) { throw '-DumpType 取值范围 0..2' }

$outDir = New-ForensicsOutputDir -Name 'crash' -OutputDir $OutputDir
$logsDir = Join-Path $outDir 'logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

# ★ 崩溃日志名：**当前名 + 旧名兼容**（docs/REBRAND.md:62 的 B5 项）。
$logNames = @(
    'LiteMonitor++_Error.log',
    'LiteMonitor_Error.log',
    'LiteMonitor++_HardwareError.log'
)
$exeNames = @('LiteMonitor++.exe', 'LiteMonitor.exe')

# ── 目录候选 ─────────────────────────────────────────────────────────────

$defaultRoots = @(
    'C:\TOOLS\LiteMonitor',
    (Join-Path $env:LOCALAPPDATA 'LiteMonitor'),
    (Join-Path $env:APPDATA 'LiteMonitor'),
    (Join-Path $env:LOCALAPPDATA 'LiteMonitor++'),
    (Join-Path $env:APPDATA 'LiteMonitor++'),
    $env:ProgramFiles,
    ${env:ProgramFiles(x86)},
    (Join-Path $env:USERPROFILE 'Desktop'),
    (Get-Location).Path
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

$roots = if ($PSBoundParameters.ContainsKey('SearchRoots') -and $SearchRoots) { $SearchRoots } else { $defaultRoots }

Write-Host ("输出目录  : {0}" -f $outDir)
Write-Host ("查找根目录: {0}" -f ($roots -join '; '))
Write-Host ("日志名    : {0}" -f ($logNames -join ', '))
Write-Host ''

function Find-FileBounded {
    <#
        有深度上限的文件查找（避免在 Program Files 里无限递归）。
        返回匹配的完整路径。
    #>
    param([string] $Root, [string[]] $Names, [int] $MaxDepth = 3)

    $found = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Root)) { return $found }

    $rootFull = (Resolve-Path -LiteralPath $Root).Path
    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue(@{ Path = $rootFull; Depth = 0 })

    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $dir = $item.Path
        $depth = $item.Depth

        try {
            foreach ($f in Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue) {
                foreach ($n in $Names) {
                    if ($f.Name -ieq $n) { $found.Add($f.FullName) }
                }
            }
        }
        catch { }

        if ($depth -ge $MaxDepth) { continue }
        try {
            foreach ($d in Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue) {
                # 跳过大目录（缓存 / 包目录 / 系统目录），它们不可能是安装目录
                if ($d.Name -in @('node_modules', 'Windows', 'WinSxS', 'Installer', 'Temp', '$Recycle.Bin', 'System Volume Information')) { continue }
                $queue.Enqueue(@{ Path = $d.FullName; Depth = $depth + 1 })
            }
        }
        catch { }
    }
    return $found
}

# ── 收集日志 ─────────────────────────────────────────────────────────────

$collected = New-Object System.Collections.Generic.List[object]

$dirsToScan = New-Object System.Collections.Generic.List[string]
if ($PSBoundParameters.ContainsKey('InstallDirs') -and $InstallDirs) {
    foreach ($d in $InstallDirs) { $dirsToScan.Add([IO.Path]::GetFullPath($d)) }
}
else {
    foreach ($r in $roots) { $dirsToScan.Add([IO.Path]::GetFullPath($r)) }
}

Write-Host '── 查找崩溃日志 ──'
foreach ($d in $dirsToScan) {
    $hits = Find-FileBounded -Root $d -Names $logNames -MaxDepth 3
    foreach ($h in $hits) {
        if ($collected | Where-Object { $_.path -ieq $h }) { continue }
        $item = Get-Item -LiteralPath $h -ErrorAction SilentlyContinue
        if (-not $item) { continue }
        $hash = (Get-FileHash -LiteralPath $h -Algorithm SHA256).Hash
        $collected.Add([pscustomobject][ordered]@{
                path         = $h
                name         = $item.Name
                sizeBytes    = $item.Length
                lastWriteUtc = $item.LastWriteTimeUtc.ToString('o')
                sha256       = $hash
                legacyName   = ($item.Name -ieq 'LiteMonitor_Error.log')
            })
        Write-Host ("  [命中] {0}" -f $h)
    }
}

# ── 收集报告者二进制版本 ────────────────────────────────────────────────

Write-Host ''
Write-Host '── 查找报告者二进制版本 ──'
$binaries = New-Object System.Collections.Generic.List[object]
foreach ($d in $dirsToScan) {
    $hits = Find-FileBounded -Root $d -Names $exeNames -MaxDepth 3
    foreach ($h in $hits) {
        if ($binaries | Where-Object { $_.path -ieq $h }) { continue }
        $vi = (Get-Item -LiteralPath $h).VersionInfo
        $hash = (Get-FileHash -LiteralPath $h -Algorithm SHA256).Hash
        $binaries.Add([pscustomobject][ordered]@{
                path           = $h
                fileVersion    = $vi.FileVersion
                productVersion = $vi.ProductVersion
                productName    = $vi.ProductName
                fileNameRaw    = $vi.FileName
                lastWriteUtc   = (Get-Item -LiteralPath $h).LastWriteTimeUtc.ToString('o')
                sizeBytes      = (Get-Item -LiteralPath $h).Length
                sha256         = $hash
            })
        Write-Host ("  [命中] {0}  版本={1}" -f $h, $vi.FileVersion)
    }
}

# ── 复制（脱敏）日志 ─────────────────────────────────────────────────────

Write-Host ''
Write-Host '── 导出日志副本（脱敏）──'
$exported = New-Object System.Collections.Generic.List[object]
foreach ($c in $collected) {
    $dest = Join-Path $logsDir ([IO.Path]::GetFileName($c.path))
    if ($exported | Where-Object { $_.dest -ieq $dest }) { $dest = Join-Path $logsDir ("{0}.{1}" -f [IO.Path]::GetFileNameWithoutExtension($c.path), $c.sha256.Substring(0, 8) + '.log') }
    try {
        $raw = Get-Content -LiteralPath $c.path -Raw -Encoding utf8 -ErrorAction Stop
        Write-RedactedFile -Path $dest -Text $raw
        $exported.Add([pscustomobject]@{ source = $c.path; dest = $dest; sha256 = $c.sha256; sizeBytes = $c.sizeBytes })
        Write-Host ("  [导出] {0} → {1}" -f $c.path, $dest)
    }
    catch {
        $exported.Add([pscustomobject]@{ source = $c.path; dest = $null; sha256 = $c.sha256; sizeBytes = $c.sizeBytes; error = $_.Exception.Message })
        Write-Host ("  [失败] {0} —— {1}" -f $c.path, $_.Exception.Message)
    }
}

# ── Windows 事件日志 ─────────────────────────────────────────────────────

Write-Host ''
Write-Host '── 收集 Windows 事件日志（Application Error / .NET Runtime）──'
$eventsText = ''
$eventCount = 0
try {
    $start = (Get-Date).AddHours(-$EventHours)
    $evts = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $start; Id = 1000, 1001, 1002, 1005, 1026 } -ErrorAction Stop |
        Where-Object { $_.Message -match 'LiteMonitor' })
    $eventCount = $evts.Count
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("事件查询窗口：最近 $EventHours 小时（自 $start）")
    [void]$sb.AppendLine("过滤：LogName=Application, Id ∈ {1000,1001,1002,1005,1026}, Message 含 'LiteMonitor'")
    [void]$sb.AppendLine("命中：$eventCount 条")
    [void]$sb.AppendLine('')
    foreach ($e in $evts) {
        [void]$sb.AppendLine('================================================================')
        [void]$sb.AppendLine("TimeCreated : $($e.TimeCreated.ToString('o'))")
        [void]$sb.AppendLine("Id          : $($e.Id)")
        [void]$sb.AppendLine("Provider    : $($e.ProviderName)")
        [void]$sb.AppendLine("Level       : $($e.LevelDisplayName)")
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine($e.Message)
        [void]$sb.AppendLine('')
    }
    $eventsText = $sb.ToString()
    Write-Host ("  命中 {0} 条事件" -f $eventCount)
}
catch {
    $eventsText = "事件日志读取失败：$($_.Exception.Message)`r`n（需要管理员权限；也可能是本机没有 Application 日志）"
    Write-Host ("  {0}" -f $eventsText)
}
Write-RedactedFile -Path (Join-Path $outDir 'application-events.txt') -Text $eventsText

# ── WER LocalDumps ───────────────────────────────────────────────────────

$werBase = 'HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$werApp = Join-Path $werBase 'LiteMonitor++.exe'
$werState = [ordered]@{
    registryBase = $werBase
    registryApp  = $werApp
    changed      = $false
    dumpFolder   = $null
    dumpType     = $null
    dumpCount    = $null
    note         = $null
}

if ($EnableLocalDumps -or $DisableLocalDumps) {
    Write-Host ''
    Write-Host '── WER LocalDumps ──'
    $dumpDir = if ($LocalDumpDir) { [IO.Path]::GetFullPath($LocalDumpDir) } else { Join-Path $outDir 'dumps' }

    if ($EnableLocalDumps) {
        if ($DryRun) {
            Write-Host ("  [DRY-RUN] 将新建 {0}" -f $werApp)
            Write-Host ("  [DRY-RUN] DumpFolder={0}  DumpType={1}" -f $dumpDir, $DumpType)
        }
        else {
            New-Item -ItemType Directory -Path $dumpDir -Force | Out-Null
            if (-not (Test-Path -LiteralPath $werBase)) {
                New-Item -Path $werBase -Force | Out-Null
            }
            if (-not (Test-Path -LiteralPath $werApp)) {
                New-Item -Path $werApp -Force | Out-Null
            }
            New-ItemProperty -Path $werApp -Name 'DumpFolder' -Value $dumpDir -PropertyType ExpandString -Force | Out-Null
            New-ItemProperty -Path $werApp -Name 'DumpType' -Value $DumpType -PropertyType DWord -Force | Out-Null
            New-ItemProperty -Path $werApp -Name 'DumpCount' -Value 10 -PropertyType DWord -Force | Out-Null
            $werState.changed = $true
            $werState.dumpFolder = $dumpDir
            $werState.dumpType = $DumpType
            $werState.dumpCount = 10
            Write-Host ("  已开启：DumpFolder={0}  DumpType={1}（2=完整）  DumpCount=10" -f $dumpDir, $DumpType)
            Write-Host '  ★ 下次崩溃后 .dmp 会落在上面这个目录；用 WinDbg / dumpchk 打开。'
        }
    }
    else {
        if ($DryRun) {
            Write-Host ("  [DRY-RUN] 将删除 {0}" -f $werApp)
        }
        else {
            if (Test-Path -LiteralPath $werApp) {
                Remove-Item -LiteralPath $werApp -Recurse -Force
                Write-Host '  已删除本工具创建的 LocalDumps 应用级项。'
            }
            else {
                Write-Host '  没有找到本工具创建的应用级项（无需处理）。'
            }
            $werState.changed = $true
        }
    }
}
else {
    $werState.note = '本次未改动 WER 注册表（未加 -EnableLocalDumps）。需要崩溃转储时，请加 -EnableLocalDumps 重跑一次。'
}

# ── 汇总 ─────────────────────────────────────────────────────────────────

$summaryLines = @(
    'LiteMonitor++ 崩溃取证汇总（P-F15 / #582 / #574）',
    ('生成时间：' + (Get-Date).ToString('o')),
    ('机器：' + $env:COMPUTERNAME),
    '',
    '【崩溃日志】'
)
if ($collected.Count -eq 0) {
    $summaryLines += '  （未找到任何崩溃日志文件）'
    $summaryLines += '  ★ 请确认程序目录，必要时用 -InstallDirs <目录> 显式指定。'
}
else {
    foreach ($c in $collected) {
        $flag = if ($c.legacyName) { ' [旧名]' } else { '' }
        $summaryLines += ("  {0}{1}`n    大小={2} 字节  最后写入={3}`n    sha256={4}" -f $c.path, $flag, $c.sizeBytes, $c.lastWriteUtc, $c.sha256)
    }
}
$summaryLines += ''
$summaryLines += '【报告者二进制版本】'
if ($binaries.Count -eq 0) {
    $summaryLines += '  （未找到 LiteMonitor++ 可执行文件）'
}
else {
    foreach ($b in $binaries) {
        $summaryLines += ("  {0}`n    FileVersion={1}  ProductVersion={2}  Product={3}`n    最后写入={4}  sha256={5}" -f `
                $b.path, $b.fileVersion, $b.productVersion, $b.productName, $b.lastWriteUtc, $b.sha256)
    }
}
$summaryLines += ''
$summaryLines += ("【Windows 事件】命中 {0} 条（详见 application-events.txt）" -f $eventCount)
$summaryLines += ''
$summaryLines += ('【WER LocalDumps】' + $(if ($werState.changed) { '已改动' } else { '未改动' }))
$summaryLines += ('  ' + $werState.note)
$summaryLines += ''
$summaryLines += '★ 本汇总只是证据清单，不含任何根因判定。'

Write-RedactedFile -Path (Join-Path $outDir 'summary.txt') -Text ($summaryLines -join "`r`n")

$manifest = [ordered]@{
    schemaVersion = 1
    tool          = 'tools/forensics/collect-crash-evidence.ps1'
    mode          = if ($DryRun) { 'dry-run' } else { 'collect' }
    collectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    purposes      = @(
        '#582：崩溃堆栈 / LiteMonitor_Error.log 附件原文（issue 正文只有附件，未取得）',
        '#574：报告者二进制版本 + 当前 LiteMonitor++_Error.log + 异常源 + 主题/DPI 切换时序'
    )
    logNamesSearched = $logNames
    legacyLogNameNote = 'LiteMonitor_Error.log 是旧品牌名（docs/REBRAND.md B5）；老用户目录里可能仍是这个名字，本工具两个名字都找。'
    logsFound    = $collected.ToArray()
    logsExported = $exported.ToArray()
    binaries     = $binaries.ToArray()
    events       = [ordered]@{ windowHours = $EventHours; count = $eventCount; file = 'application-events.txt' }
    werLocalDumps = $werState
    outputs      = @('summary.txt', 'manifest.json', 'application-events.txt', 'logs/*')
    environment  = (Get-ForensicsEnvironment)
}
Write-JsonFile -Path (Join-Path $outDir 'manifest.json') -Value $manifest -Depth 10

Write-Host ''
Write-Host ("汇总      : {0}" -f (Join-Path $outDir 'summary.txt'))
Write-Host ("清单      : {0}" -f (Join-Path $outDir 'manifest.json'))
Write-Host ("日志副本  : {0}" -f $logsDir)
if ($DryRun) { Write-Host '★ 自检模式：未做任何改动。' }
