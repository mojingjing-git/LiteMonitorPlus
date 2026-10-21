#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(1, 120)][int]$Minutes = 30,
    [ValidateRange(3000, 60000)][int]$IntervalMilliseconds = 6000,
    [ValidateRange(30, 600)][int]$TargetGraceSeconds = 120,
    [ValidateRange(10, 300)][int]$ExitGraceSeconds = 45,
    [ValidateRange(0, 15)][int]$ScreenIndex = 0,
    [ValidateRange(10, 1800)][int]$WindowAuditIntervalSeconds = 300,
    [string]$Theme = 'LiquidGlass_GPU',
    [switch]$Live
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$probeRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $probeRoot
$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\GpuUiProbe.exe'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$repoFull = [IO.Path]::GetFullPath($repoRoot)
if (-not $output.StartsWith($repoFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay under the repository: $output"
}
if (-not (Test-Path -LiteralPath $exe)) {
    throw "GpuUiProbe executable is missing: $exe"
}
if (Test-Path -LiteralPath $output) {
    if (@(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
        throw "OutputDirectory must be new or empty: $output"
    }
}
else {
    New-Item -ItemType Directory -Path $output | Out-Null
}

$cancelFile = Join-Path $output 'cancel.signal'
$watchdogPath = Join-Path $output 'watchdog.json'
$watchdogResultPath = Join-Path $output 'watchdog.result.json'
$windowAuditScript = Join-Path $PSScriptRoot 'audit-window-policy.ps1'
$pwshExecutable = (Get-Process -Id $PID).Path
$windowAuditRecords = [Collections.Generic.List[object]]::new()
$arguments = @(
    $Theme,
    $output,
    "--stress-minutes=$Minutes",
    "--stress-interval=$IntervalMilliseconds",
    "--cancel-file=$cancelFile",
    "--screen-index=$ScreenIndex"
)
if ($Live) {
    $arguments += '--live'
}

function Write-JsonAtomic {
    param(
        [string]$Path,
        [object]$Value
    )

    $temporaryPath = $Path + '.tmp'
    $Value | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Invoke-WindowPolicyAudit {
    param(
        [DateTime]$At,
        [int]$ProcessId
    )

    $elapsedMinutes = [int][Math]::Floor(
        ($At - $startedAt).TotalMinutes)
    $suffix = if ($elapsedMinutes -eq 0) {
        'start'
    }
    else {
        '{0:D2}m' -f $elapsedMinutes
    }
    $auditPath = Join-Path $output "window-policy-audit-$suffix.json"
    if (Test-Path -LiteralPath $auditPath) {
        $collision = 2
        do {
            $auditPath = Join-Path $output (
                "window-policy-audit-$suffix-$collision.json")
            $collision++
        } while (Test-Path -LiteralPath $auditPath)
    }
    $auditExitCode = 0
    try {
        & $pwshExecutable `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $windowAuditScript `
            -TargetProcessId $ProcessId `
            -OutputPath $auditPath | Out-Null
        $auditExitCode = $LASTEXITCODE
    }
    catch {
        $auditExitCode = 1
    }

    $windowAuditRecords.Add([ordered]@{
        sampledAt = $At.ToString('o')
        path = $auditPath
        exitCode = $auditExitCode
    })
}

$startedAt = Get-Date
$process = Start-Process `
    -FilePath $exe `
    -ArgumentList $arguments `
    -WorkingDirectory (Split-Path -Parent $exe) `
    -PassThru

$watchdogState = [ordered]@{
    task = 'R22-gpu-stress-external-watchdog'
    status = 'running'
    pid = $process.Id
    command = "$exe $($arguments -join ' ')"
    startedAt = $startedAt.ToString('o')
    targetWallClockSeconds = $Minutes * 60
    targetGraceSeconds = $TargetGraceSeconds
    exitGraceSeconds = $ExitGraceSeconds
    screenIndex = $ScreenIndex
    cancelFile = $cancelFile
    cancelReason = $null
    cancelRequestedAt = $null
    forcedTermination = $false
    windowPolicyAuditIntervalSeconds = $windowAuditIntervalSeconds
    windowPolicyAudits = @()
}
Write-JsonAtomic -Path $watchdogPath -Value $watchdogState

$targetDeadline = $startedAt.AddMinutes($Minutes).
    AddSeconds($TargetGraceSeconds)
$cancelDeadline = $null
$nextWindowAuditAt = $startedAt

while (-not $process.HasExited) {
    $process.Refresh()
    $now = Get-Date
    if ($now -ge $nextWindowAuditAt) {
        Invoke-WindowPolicyAudit -At $now -ProcessId $process.Id
        $watchdogState.windowPolicyAudits = @($windowAuditRecords)
        $nextWindowAuditAt = $now.AddSeconds($windowAuditIntervalSeconds)
    }

    if ($null -eq $cancelDeadline) {
        $reason = $null
        if (Test-Path -LiteralPath $cancelFile) {
            $reason = 'operator_cancel_file'
        }
        elseif ($now -ge $targetDeadline) {
            $reason = 'target_plus_grace_expired'
            Set-Content `
                -LiteralPath $cancelFile `
                -Value $reason `
                -Encoding UTF8
        }

        if ($null -ne $reason) {
            $cancelDeadline = $now.AddSeconds($ExitGraceSeconds)
            $watchdogState.status = 'cancel_requested'
            $watchdogState.cancelReason = $reason
            $watchdogState.cancelRequestedAt = $now.ToString('o')
        }
    }
    elseif ($now -ge $cancelDeadline) {
        $watchdogState.status = 'forced_termination'
        $watchdogState.forcedTermination = $true
        $watchdogState.forcedAt = $now.ToString('o')
        Write-JsonAtomic -Path $watchdogPath -Value $watchdogState
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        $process.WaitForExit()
        break
    }

    $watchdogState.elapsedSeconds =
        [Math]::Round(($now - $startedAt).TotalSeconds, 3)
    $watchdogState.lastCheckedAt = $now.ToString('o')
    $watchdogState.progressExists =
        Test-Path -LiteralPath (Join-Path $output 'stress.progress.json')
    Write-JsonAtomic -Path $watchdogPath -Value $watchdogState
    Start-Sleep -Seconds 10
}

$process.Refresh()
$exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
$diagnosticsPath = Join-Path $output 'stress.diagnostics.json'
$diagnosticStatus = $null
$diagnostic = $null
if (Test-Path -LiteralPath $diagnosticsPath) {
    $diagnostic = Get-Content -LiteralPath $diagnosticsPath -Raw |
        ConvertFrom-Json
    $diagnosticStatus = $diagnostic.status
}

if ($null -ne $diagnostic) {
    $progressPath = Join-Path $output 'stress.progress.json'
    $progress = if (Test-Path -LiteralPath $progressPath) {
        Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
    }
    else {
        [pscustomobject]@{}
    }
    $progress | Add-Member `
        -NotePropertyName finalizedFromDiagnostics `
        -NotePropertyValue $true `
        -Force
    $progress | Add-Member `
        -NotePropertyName diagnosticStatus `
        -NotePropertyValue $diagnosticStatus `
        -Force
    $progress | Add-Member `
        -NotePropertyName diagnosticFile `
        -NotePropertyValue 'stress.diagnostics.json' `
        -Force
    Write-JsonAtomic -Path $progressPath -Value $progress
}

$runnerStatus = if ($watchdogState.forcedTermination) {
    'watchdog_forced'
}
elseif ($exitCode -eq 0 -and $diagnosticStatus -eq 'completed') {
    'completed'
}
elseif ($diagnosticStatus) {
    $diagnosticStatus
}
else {
    'failed_without_diagnostics'
}
$runnerExitCode = if ($runnerStatus -eq 'completed') {
    0
}
elseif ($runnerStatus -eq 'watchdog_forced') {
    124
}
elseif ($runnerStatus -eq 'cancelled') {
    2
}
elseif ($runnerStatus -eq 'aborted') {
    3
}
else {
    1
}

$result = [ordered]@{
    task = 'R22-gpu-stress-external-watchdog-result'
    status = $runnerStatus
    exitCode = $exitCode
    runnerExitCode = $runnerExitCode
    diagnosticStatus = $diagnosticStatus
    forcedTermination = $watchdogState.forcedTermination
    windowPolicyAuditIntervalSeconds = $windowAuditIntervalSeconds
    windowPolicyAudits = @($windowAuditRecords)
    screenIndex = $ScreenIndex
    startedAt = $startedAt.ToString('o')
    finishedAt = (Get-Date).ToString('o')
    elapsedSeconds =
        [Math]::Round(((Get-Date) - $startedAt).TotalSeconds, 3)
    evidenceDirectory = $output
}
Write-JsonAtomic -Path $watchdogResultPath -Value $result
$result | ConvertTo-Json -Depth 5
exit $runnerExitCode
