#requires -Version 7.0
<#
.SYNOPSIS
    LiteMonitor++ 内存时间线采样器（P-F15 / 上游 issue #78 取证工具）。

.DESCRIPTION
    采集一条 **≥72h** 的进程内存时间线，供人工判断"私有字节是否随运行时长单调上升"。

    ★ 本工具**不下结论**。它只产出曲线数据与**测量值**（首末值、极值、线性斜率），
      "是否构成泄漏"由人判读。

    ★ 为什么以 **私有字节（Private Bytes）** 为主口径：
      LiteMonitor++ 自身会周期性调用 `EmptyWorkingSet`（src/System/HardwareServices/SystemOptimizer.cs:171-183，
      工作集 > 30MB 时每 300s 裁剪一次）⇒ 任务管理器里的"内存"（工作集）会被**主动压低**，
      呈现锯齿状，**不能**作为泄漏依据。
      ⇒ 本工具把 `working_set_bytes` 并行记录下来，但**仅作参考**，主口径是 `private_bytes`。

    ★ 隔离：若带 -LaunchSourceDir，程序从**沙箱目录**运行（settings.json / 日志 / 历史数据全部落在沙箱），
      绝不触碰用户目录下的 settings.json；沙箱副本与用户实例可并存（互斥体按目录路径生成）。

.PARAMETER ProcessName
    目标进程名（默认 LiteMonitor++）。当同名进程有多个时取最早启动的一个并记录其 PID。

.PARAMETER ProcessId
    直接指定 PID（与 -ProcessName 二选一；给了就以它为准）。

.PARAMETER IntervalSeconds
    采样间隔（秒）。默认 60。
    ★ 建议：72h 长跑用 60~300s。间隔越短，曲线越能看出"台阶"；间隔越长越省磁盘。
      内存泄漏取证**不需要**秒级间隔（工作集锯齿会被 EmptyWorkingSet 主导，见上）。

.PARAMETER DurationHours
    采样总时长（小时）。默认 72。

.PARAMETER OutputDir
    证据输出目录。默认 <仓库根>\docs\reviews\evidence\forensics\mem-timeline\<时间戳>。

.PARAMETER LaunchSourceDir
    主工程的**构建输出目录**（含 LiteMonitor++.exe）。给了就先把整个目录复制到沙箱再启动。

.PARAMETER SettingsJsonPath
    要写进**沙箱**的 settings.json 路径（可选）。★ 只写沙箱副本，绝不写用户目录。

.PARAMETER DryRun
    只做自检：校验参数、准备（不启动）沙箱、连采 2 个样本、写出 manifest 与空曲线头，然后退出。

.EXAMPLE
    # ① 自检（不启动 72h）
    pwsh tools/forensics/mem-timeline.ps1 -DryRun

.EXAMPLE
    # ② 对**用户正在运行**的实例采样 72h（只读观察，不改任何配置）
    pwsh tools/forensics/mem-timeline.ps1 -DurationHours 72 -IntervalSeconds 60

.EXAMPLE
    # ③ 用沙箱副本跑 72h（不碰用户 settings.json）
    pwsh tools/forensics/mem-timeline.ps1 `
        -LaunchSourceDir bin\Release\net8.0-windows `
        -DurationHours 72 -IntervalSeconds 60
#>
[CmdletBinding()]
param(
    [string] $ProcessName = 'LiteMonitor++',
    [int]    $ProcessId = 0,
    [int]    $IntervalSeconds = 60,
    [double] $DurationHours = 72,
    [string] $OutputDir,
    [string] $LaunchSourceDir,
    [string] $SettingsJsonPath,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
Assert-Pwsh7

if ($IntervalSeconds -lt 1) { throw "-IntervalSeconds 必须 ≥ 1" }
if ($DurationHours -le 0) { throw "-DurationHours 必须 > 0" }
if ($DurationHours -gt 24 * 30) { throw "-DurationHours 上限 720（30 天）" }

$outDir = New-ForensicsOutputDir -Name 'mem-timeline' -OutputDir $OutputDir

# ── GDI / USER 对象计数（GDI 句柄泄漏取证用）────────────────────────────
if (-not ('Forensics.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Forensics
{
    public static class NativeMethods
    {
        public const uint GR_GDIOBJECTS = 0;
        public const uint GR_USEROBJECTS = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        /// <summary>
        /// 取 GDI / USER 对象数。
        /// ★ MSDN：失败时**返回 0**（不是 -1）⇒ 0 既是合法值也是失败值，
        ///   只能靠 GetLastWin32Error 区分。这里在**同一次原生调用序列里**取错误码，
        ///   避免跨语言调用把 last-error 冲掉。
        /// </summary>
        public static int GetGuiResourcesChecked(IntPtr hProcess, uint uiFlags, out int win32Error)
        {
            uint value = GetGuiResources(hProcess, uiFlags);
            win32Error = Marshal.GetLastWin32Error();
            return (int)value;
        }
    }
}
'@
}

function Get-GuiResourceCount {
    <#
        返回 @{ Gdi; User; Error }。
        ★ 失败（win32Error != 0）时返回 $null —— 绝不把"读不到"伪装成"0 个对象"。
    #>
    param([System.Diagnostics.Process] $Proc)
    try {
        $errGdi = 0
        $errUser = 0
        $gdi = [Forensics.NativeMethods]::GetGuiResourcesChecked($Proc.Handle, [Forensics.NativeMethods]::GR_GDIOBJECTS, [ref]$errGdi)
        $usr = [Forensics.NativeMethods]::GetGuiResourcesChecked($Proc.Handle, [Forensics.NativeMethods]::GR_USEROBJECTS, [ref]$errUser)
        return @{
            Gdi   = if ($errGdi -eq 0) { [int64]$gdi } else { $null }
            User  = if ($errUser -eq 0) { [int64]$usr } else { $null }
            Error = if ($errGdi -ne 0 -or $errUser -ne 0) { "GetGuiResources 失败（win32 err gdi=$errGdi user=$errUser）" } else { $null }
        }
    }
    catch {
        return @{ Gdi = $null; User = $null; Error = $_.Exception.Message }
    }
}

function Resolve-TargetProcess {
    param([string] $Name, [int] $Id)

    if ($Id -gt 0) {
        try { return [System.Diagnostics.Process]::GetProcessById($Id) } catch { return $null }
    }
    $procs = @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Sort-Object StartTime)
    if ($procs.Count -eq 0) { return $null }
    return $procs[0]
}

function Get-ProcessSample {
    param([System.Diagnostics.Process] $Proc)

    $now = Get-Date
    $row = [ordered]@{
        timestamp_utc      = $now.ToUniversalTime().ToString('o')
        timestamp_local    = $now.ToString('o')
        elapsed_seconds    = 0
        pid                = $Proc.Id
        alive              = 1
        private_bytes      = $null
        working_set_bytes  = $null
        paged_bytes        = $null
        virtual_bytes      = $null
        handle_count       = $null
        thread_count       = $null
        gdi_objects        = $null
        user_objects       = $null
        cpu_total_seconds  = $null
        cpu_delta_seconds  = $null
        note               = ''
    }

    try {
        $row.private_bytes     = [int64]$Proc.PrivateMemorySize64
        $row.working_set_bytes = [int64]$Proc.WorkingSet64
        $row.paged_bytes       = [int64]$Proc.PagedMemorySize64
        $row.virtual_bytes     = [int64]$Proc.VirtualMemorySize64
        $row.handle_count      = [int64]$Proc.HandleCount
        $row.thread_count      = [int64]$Proc.Threads.Count
        $row.cpu_total_seconds = [math]::Round($Proc.TotalProcessorTime.TotalSeconds, 3)
        $gui = Get-GuiResourceCount -Proc $Proc
        $row.gdi_objects  = $gui.Gdi
        $row.user_objects = $gui.User
        if ($gui.Error) { $row.note = $gui.Error }
    }
    catch {
        $row.alive = 0
        $row.note = 'sample-failed: ' + $_.Exception.GetType().Name
    }

    return $row
}

function Get-LinearSlope {
    <#
        对 (x=小时, y=值) 做最小二乘，返回每小时变化量。
        ★ 这是**测量值**，不是"是否泄漏"的结论。
    #>
    param([double[]] $X, [double[]] $Y)

    $n = $X.Count
    if ($n -lt 2) { return $null }
    $sumX = 0.0; $sumY = 0.0; $sumXY = 0.0; $sumXX = 0.0
    for ($i = 0; $i -lt $n; $i++) {
        $sumX += $X[$i]; $sumY += $Y[$i]
        $sumXY += $X[$i] * $Y[$i]; $sumXX += $X[$i] * $X[$i]
    }
    $den = ($n * $sumXX) - ($sumX * $sumX)
    if ([math]::Abs($den) -lt 1e-9) { return $null }
    return [math]::Round((($n * $sumXY) - ($sumX * $sumY)) / $den, 4)
}

# ── 沙箱准备 ─────────────────────────────────────────────────────────────

$sandbox = $null
$settingsUsed = $null

if ($LaunchSourceDir) {
    $srcDir = if ([IO.Path]::IsPathRooted($LaunchSourceDir)) {
        $LaunchSourceDir
    }
    else {
        Join-Path (Get-RepoRoot) $LaunchSourceDir
    }
    $settings = $null
    if ($SettingsJsonPath) {
        $settings = Get-Content -LiteralPath $SettingsJsonPath -Raw -Encoding utf8 | ConvertFrom-Json
        $settingsUsed = $SettingsJsonPath
    }
    $sandbox = New-SandboxAppInstance `
        -SourceDir $srcDir `
        -SandboxRoot $outDir `
        -SettingsJson $settings `
        -Start:(-not $DryRun)
    Write-Host ("沙箱目录  : {0}" -f $sandbox.SandboxDir)
    Write-Host ("沙箱 exe  : {0}" -f $sandbox.ExePath)
    if ($settingsUsed) { Write-Host ("沙箱配置  : {0}（用户 settings.json 未被触碰）" -f $settingsUsed) }
}

# ── 采样循环 ─────────────────────────────────────────────────────────────

$csvPath = Join-Path $outDir 'timeline.csv'
$rows = New-Object System.Collections.Generic.List[object]
$header = @(
    'timestamp_utc', 'timestamp_local', 'elapsed_seconds', 'pid', 'alive',
    'private_bytes', 'working_set_bytes', 'paged_bytes', 'virtual_bytes',
    'handle_count', 'thread_count', 'gdi_objects', 'user_objects',
    'cpu_total_seconds', 'cpu_delta_seconds', 'note'
) -join ','

$startedAt = Get-Date
$deadline = $startedAt.AddHours($DurationHours)
$sampleCount = 0
$maxSamples = if ($DryRun) { 2 } else { [int]::MaxValue }
$lastCpu = $null
$targetPid = 0
$proc = $null
$exited = $false

Write-Host ("输出目录  : {0}" -f $outDir)
Write-Host ("目标进程  : {0}" -f $(if ($ProcessId -gt 0) { "PID $ProcessId" } else { $ProcessName }))
Write-Host ("采样间隔  : {0}s   总时长: {1}h" -f $IntervalSeconds, $DurationHours)
Write-Host ("主口径    : private_bytes（私有字节）")
Write-Host ("参考口径  : working_set_bytes（工作集，受 EmptyWorkingSet 压低，**不作依据**）")
Write-Host ''

while ($true) {
    if ($null -eq $proc) {
        $proc = Resolve-TargetProcess -Name $ProcessName -Id $ProcessId
        if ($null -eq $proc) {
            if ($sandbox -and $sandbox.Started) {
                $proc = Resolve-TargetProcess -Name 'LiteMonitor++' -Id $sandbox.ProcessId
            }
        }
        if ($null -eq $proc) {
            $note = 'process-not-found'
            $rows.Add([pscustomobject][ordered]@{
                    timestamp_utc = (Get-Date).ToUniversalTime().ToString('o')
                    timestamp_local = (Get-Date).ToString('o')
                    elapsed_seconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 3)
                    pid = 0; alive = 0
                    private_bytes = $null; working_set_bytes = $null; paged_bytes = $null
                    virtual_bytes = $null; handle_count = $null; thread_count = $null
                    gdi_objects = $null; user_objects = $null
                    cpu_total_seconds = $null; cpu_delta_seconds = $null
                    note = $note
                })
            if (-not $DryRun -and (Get-Date) -ge $deadline) { break }
            if ($exited) { break }
            $exited = $true
            Start-Sleep -Seconds ([math]::Min($IntervalSeconds, 30))
            continue
        }
        $exited = $false
        $targetPid = $proc.Id
        Write-Host ("已锁定进程 PID={0}（{1}）" -f $proc.Id, $proc.ProcessName)
    }

    $sample = Get-ProcessSample -Proc $proc
    $sample.elapsed_seconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 3)
    if ($null -ne $lastCpu -and $null -ne $sample.cpu_total_seconds) {
        $sample.cpu_delta_seconds = [math]::Round($sample.cpu_total_seconds - $lastCpu, 3)
    }
    if ($null -ne $sample.cpu_total_seconds) { $lastCpu = $sample.cpu_total_seconds }

    $rows.Add([pscustomobject]$sample)
    $sampleCount++

    if ($sampleCount % 10 -eq 0 -or $DryRun) {
        Write-Host ("[{0,8}] elapsed={1,8:N0}s  private={2,10:N0}  ws={3,10:N0}  handles={4,6}  threads={5,4}  gdi={6,6}" -f `
                $sampleCount, $sample.elapsed_seconds, ($sample.private_bytes ?? 0), ($sample.working_set_bytes ?? 0), `
                ($sample.handle_count ?? 0), ($sample.thread_count ?? 0), ($sample.gdi_objects ?? 0))
    }

    if ($sampleCount -ge $maxSamples) { break }
    if ((Get-Date) -ge $deadline) { break }

    $sleep = [math]::Min($IntervalSeconds, [math]::Max(1, [int](($deadline - (Get-Date)).TotalSeconds)))
    Start-Sleep -Seconds $sleep
}

# ── 落盘 ──────────────────────────────────────────────────────────────────

$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8

$aliveRows = @($rows | Where-Object { $_.alive -eq 1 -and $null -ne $_.private_bytes })
$xs = @($aliveRows | ForEach-Object { [double]$_.elapsed_seconds / 3600.0 })
$priv = @($aliveRows | ForEach-Object { [double]$_.private_bytes })
$handles = @($aliveRows | ForEach-Object { [double]$_.handle_count })
$threads = @($aliveRows | ForEach-Object { [double]$_.thread_count })

$summary = [ordered]@{
    schemaVersion = 1
    tool = 'tools/forensics/mem-timeline.ps1'
    purpose = '上游 issue #78（内存占用高）—— ≥72h 私有字节时间线'
    disclaimer = '本文件是**测量值**，不构成"存在泄漏"或"不存在泄漏"的结论。'
    primaryMetric = 'private_bytes'
    referenceOnlyMetrics = @('working_set_bytes')
    referenceMetricNote = '程序自身每 300s 对工作集 > 30MB 的进程调用 EmptyWorkingSet，工作集被主动压低，锯齿属预期，不可作泄漏依据。'
    startedAtUtc = $startedAt.ToUniversalTime().ToString('o')
    finishedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    elapsedHours = [math]::Round(((Get-Date) - $startedAt).TotalHours, 4)
    requestedDurationHours = $DurationHours
    intervalSeconds = $IntervalSeconds
    sampleCount = $rows.Count
    aliveSampleCount = $aliveRows.Count
    targetPid = $targetPid
    processName = $ProcessName
    sandbox = if ($sandbox) { $sandbox.SandboxDir } else { $null }
    measurements = [ordered]@{
        privateBytes = [ordered]@{
            first = if ($priv.Count) { [int64]$priv[0] } else { $null }
            last = if ($priv.Count) { [int64]$priv[-1] } else { $null }
            min = if ($priv.Count) { [int64](($priv | Measure-Object -Minimum).Minimum) } else { $null }
            max = if ($priv.Count) { [int64](($priv | Measure-Object -Maximum).Maximum) } else { $null }
            delta = if ($priv.Count) { [int64]($priv[-1] - $priv[0]) } else { $null }
            slopeBytesPerHour = (Get-LinearSlope -X $xs -Y $priv)
            slopeMiBPerDay = if ($priv.Count -gt 1) { [math]::Round(((Get-LinearSlope -X $xs -Y $priv) ?? 0) * 24 / 1MB, 3) } else { $null }
        }
        handleCount = [ordered]@{
            first = if ($handles.Count) { [int64]$handles[0] } else { $null }
            last = if ($handles.Count) { [int64]$handles[-1] } else { $null }
            slopePerHour = (Get-LinearSlope -X $xs -Y $handles)
        }
        threadCount = [ordered]@{
            first = if ($threads.Count) { [int64]$threads[0] } else { $null }
            last = if ($threads.Count) { [int64]$threads[-1] } else { $null }
            slopePerHour = (Get-LinearSlope -X $xs -Y $threads)
        }
    }
}

Write-JsonFile -Path (Join-Path $outDir 'summary.json') -Value $summary -Depth 8

$manifest = [ordered]@{
    schemaVersion = 1
    tool = 'tools/forensics/mem-timeline.ps1'
    mode = if ($DryRun) { 'dry-run' } else { 'sample' }
    startedAtUtc = $startedAt.ToUniversalTime().ToString('o')
    finishedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    parameters = [ordered]@{
        processName = $ProcessName
        processId = $ProcessId
        intervalSeconds = $IntervalSeconds
        durationHours = $DurationHours
        launchSourceDir = $LaunchSourceDir
        settingsJsonPath = $settingsUsed
    }
    outputs = @('timeline.csv', 'summary.json', 'manifest.json')
    environment = (Get-ForensicsEnvironment)
}
Write-JsonFile -Path (Join-Path $outDir 'manifest.json') -Value $manifest -Depth 8

# ── 收尾 ──────────────────────────────────────────────────────────────────

if ($sandbox -and $sandbox.Started) {
    Stop-SandboxAppInstance -SandboxDir $sandbox.SandboxDir
    Write-Host '沙箱实例已结束。'
}

Write-Host ''
Write-Host ("样本数    : {0}（存活样本 {1}）" -f $rows.Count, $aliveRows.Count)
Write-Host ("曲线 CSV  : {0}" -f $csvPath)
Write-Host ("汇总 JSON : {0}" -f (Join-Path $outDir 'summary.json'))
Write-Host ("清单 JSON : {0}" -f (Join-Path $outDir 'manifest.json'))
if ($DryRun) {
    Write-Host ''
    Write-Host '★ 自检模式：未采集真实长跑数据。'
}
