#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputRoot = "docs/reviews/evidence/gpu-themes/694a8c5/R22/paired-v1",
    [ValidateRange(1, 5)][int]$Rounds = 3,
    [ValidateRange(1, 60)][int]$WarmupSeconds = 10,
    [ValidateRange(10, 300)][int]$SampleSeconds = 30,
    [ValidateRange(64, 4096)][int]$PhysicalWidth = 520,
    [ValidateRange(64, 4096)][int]$PhysicalHeight = 983,
    [ValidateRange(0, 15)][int]$ScreenIndex = 0,
    [switch]$SkipBuild,
    [switch]$AllowOtherGpuWindows
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$probeProject = Join-Path $repoRoot 'tools\ProductionRenderProbe\ProductionRenderProbe.csproj'
$probeExe = Join-Path $repoRoot 'tools\ProductionRenderProbe\bin\Release\net8.0-windows\ProductionRenderProbe.exe'
$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}
$repoFull = [IO.Path]::GetFullPath($repoRoot)
if (-not $resolvedOutput.StartsWith(
        $repoFull,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay under the repository: $resolvedOutput"
}

if (Test-Path -LiteralPath $resolvedOutput) {
    throw "OutputRoot already exists; choose a new evidence directory: $resolvedOutput"
}

$otherGpuWindows = Get-Process -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ProcessName -match '^(GlassToolbarPreview|GpuUiProbe|GpuHostLifecycleProbe|ProductionRenderProbe)$' -and
        -not $_.HasExited
    }
if ($otherGpuWindows -and -not $AllowOtherGpuWindows) {
    $names = ($otherGpuWindows |
        Select-Object -ExpandProperty ProcessName -Unique) -join ', '
    throw "GPU windows/probes are already running ($names). Close them or pass -AllowOtherGpuWindows when intentional."
}

if (-not $SkipBuild) {
    & dotnet build $probeProject -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "ProductionRenderProbe build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $probeExe)) {
    throw "ProductionRenderProbe executable does not exist: $probeExe"
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LiteMonitorGdiHandles
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetGuiResources(IntPtr process, uint flags);
}
'@

function Get-Percentile {
    param(
        [object[]]$Values,
        [double]$Percentile
    )

    $numbers = @($Values |
        Where-Object { $null -ne $_ } |
        ForEach-Object { [double]$_ } |
        Sort-Object)
    if ($numbers.Count -eq 0) {
        return $null
    }

    $rank = [Math]::Ceiling(($Percentile / 100.0) * $numbers.Count) - 1
    $rank = [Math]::Max(0, [Math]::Min($numbers.Count - 1, $rank))
    return [Math]::Round($numbers[$rank], 4)
}

function Get-GpuCounters {
    param([int]$ProcessId)

    $paths = @(
        "\GPU Engine(pid_${ProcessId}_*)\Utilization Percentage",
        "\GPU Process Memory(pid_${ProcessId}_*)\Local Usage",
        "\GPU Process Memory(pid_${ProcessId}_*)\Non Local Usage"
    )
    try {
        $sample = Get-Counter -Counter $paths -MaxSamples 1 -ErrorAction Stop
        $samples = @($sample.CounterSamples)
        $engine = @($samples | Where-Object {
            $_.Path -like '*GPU Engine*Utilization Percentage*'
        })
        $memory = @($samples | Where-Object {
            $_.Path -like '*GPU Process Memory*'
        })
        return [ordered]@{
            available = $true
            engineInstanceCount = $engine.Count
            utilizationPercentSum = if ($engine.Count -gt 0) {
                [Math]::Round(($engine | Measure-Object CookedValue -Sum).Sum, 4)
            } else {
                0
            }
            utilizationPercentMax = if ($engine.Count -gt 0) {
                [Math]::Round(($engine | Measure-Object CookedValue -Maximum).Maximum, 4)
            } else {
                0
            }
            processMemoryBytesSum = if ($memory.Count -gt 0) {
                [Math]::Round(($memory | Measure-Object CookedValue -Sum).Sum)
            } else {
                0
            }
        }
    }
    catch {
        return [ordered]@{
            available = $false
            error = $_.Exception.Message
            engineInstanceCount = 0
            utilizationPercentSum = $null
            utilizationPercentMax = $null
            processMemoryBytesSum = $null
        }
    }
}

function Get-ProcessSample {
    param([System.Diagnostics.Process]$Process)

    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            return $null
        }

        $handle = $Process.Handle
        return [ordered]@{
            sampledAt = (Get-Date).ToString('o')
            cpuMilliseconds = [Math]::Round($Process.TotalProcessorTime.TotalMilliseconds, 3)
            workingSetBytes = $Process.WorkingSet64
            privateMemoryBytes = $Process.PrivateMemorySize64
            handleCount = $Process.HandleCount
            threadCount = $Process.Threads.Count
            gdiObjects = [LiteMonitorGdiHandles]::GetGuiResources($handle, 0)
            userObjects = [LiteMonitorGdiHandles]::GetGuiResources($handle, 1)
            gpu = Get-GpuCounters -ProcessId $Process.Id
        }
    }
    catch {
        return $null
    }
}

function Convert-Diagnostics {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        return [ordered]@{ parseError = $_.Exception.Message }
    }
}

function Invoke-ProbeRun {
    param(
        [string]$Mode,
        [int]$Round,
        [int]$Order
    )

    $runDirectory = Join-Path $resolvedOutput (
        "round-{0:D2}/{1:D2}-{2}" -f $Round, $Order, $Mode)
    if (Test-Path -LiteralPath $runDirectory) {
        throw "Run directory already exists: $runDirectory"
    }
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

    # Per-sample Windows GPU counters can take longer than one wall-clock
    # second. Keep the bounded probe alive long enough for the process sampler
    # to finish all requested samples and read the diagnostics file.
    $exitAfter = $WarmupSeconds + $SampleSeconds + 8
    $evidenceAt = [Math]::Max(
        250,
        ($WarmupSeconds + $SampleSeconds) * 1000)
    $arguments = @(
        $Mode,
        $runDirectory,
        "--width=$PhysicalWidth",
        "--height=$PhysicalHeight",
        "--screen-index=$ScreenIndex",
        "--performance-start=$($WarmupSeconds * 1000)",
        "--exit-after=$($exitAfter * 1000)",
        "--evidence-at=$evidenceAt",
        "--no-error-dialog"
    )
    $process = Start-Process `
        -FilePath $probeExe `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path -Parent $probeExe) `
        -WindowStyle Normal `
        -PassThru

    try {
        Start-Sleep -Seconds $WarmupSeconds
        $start = Get-ProcessSample -Process $process
        if ($null -eq $start) {
            throw "Probe process exited before the $Mode sample started."
        }

        $samples = [System.Collections.Generic.List[object]]::new()
        $samples.Add($start)
        $samplingClock = [System.Diagnostics.Stopwatch]::StartNew()
        for ($second = 1; $second -le $SampleSeconds; $second++) {
            $targetMilliseconds = $second * 1000
            $remainingMilliseconds =
                $targetMilliseconds - $samplingClock.ElapsedMilliseconds
            if ($remainingMilliseconds -gt 0) {
                Start-Sleep -Milliseconds $remainingMilliseconds
            }

            $sample = Get-ProcessSample -Process $process
            if ($null -eq $sample) {
                throw "Probe process exited during the $Mode sample."
            }
            $samples.Add($sample)
        }
        $end = $samples[$samples.Count - 1]

        if (-not $process.WaitForExit(10000)) {
            throw "Probe process did not exit after the bounded run."
        }
        if ($process.ExitCode -ne 0) {
            throw "Probe process exited with code $($process.ExitCode)."
        }

        $stem = $Mode.ToLowerInvariant() -replace '-', ''
        $diagnostics = Convert-Diagnostics (
            Join-Path $runDirectory "$stem.diagnostics.json")
        $sampleSecondsActual =
            ([DateTimeOffset]::Parse($end.sampledAt) -
             [DateTimeOffset]::Parse($start.sampledAt)).TotalSeconds
        $cpuDelta = [Math]::Max(
            0,
            $end.cpuMilliseconds - $start.cpuMilliseconds)
        $gpuValues = @($samples | ForEach-Object {
            if ($_.gpu.available) {
                $_.gpu.utilizationPercentSum
            }
        })

        $renderer = if (
            $null -ne $diagnostics -and
            $null -ne $diagnostics.PSObject.Properties['renderer']
        ) {
            $diagnostics.renderer
        } else {
            $null
        }
        $presentP95 = $null
        $presentP99 = $null
        $presentSyncInterval = $null
        $renderDurationP95 = $null
        $gpuTimestampQueriesCreated = $false
        $gpuTimestampStatus = $null
        $gpuTimestampUnavailableReason = $null
        $gpuPassSampleCount = 0
        $captureFrameAgeP95 = $null
        $captureTimestampAvailable = $false
        $gpuPassP95 = $null
        $gpuPassP99 = $null
        $compositorAgeP95 = $null
        $ledgerResourceBytes = $null
        $ledgerResourceCount = $null
        $presentation = $null
        $visibilityAudit = $null
        $windowPolicy = $null
        if ($Mode -eq 'glass' -and $null -ne $renderer) {
            $presentP95 = $renderer.PresentIntervals.P95Milliseconds
            $presentP99 = $renderer.PresentIntervals.P99Milliseconds
            $presentSyncInterval = $renderer.PresentSyncInterval
            $renderDurationP95 = $renderer.RenderDurations.P95Milliseconds
            $gpuTimestampQueriesCreated =
                [bool]($renderer.GpuTimestampQueriesCreated ?? $false)
            $gpuTimestampStatus = $renderer.GpuTimestampStatus
            $gpuTimestampUnavailableReason =
                $renderer.GpuTimestampUnavailableReason
            $gpuPassSampleCount =
                [int]($renderer.GpuPassDurations.SampleCount ?? 0)
            $gpuPassP95 = $renderer.GpuPassDurations.P95Milliseconds
            $gpuPassP99 = $renderer.GpuPassDurations.P99Milliseconds
            $compositorAgeP95 =
                $renderer.CompositorFrameAges.P95Milliseconds
            $captureFrameAgeP95 =
                $renderer.CaptureFrameAges.P95Milliseconds
            $captureTimestampAvailable =
                [bool]($renderer.CaptureSupportsTimestamp ?? $false)
            $ledgerResourceBytes = $renderer.ResourceBytes
            $ledgerResourceCount = $renderer.ResourceCount
            $presentation = $diagnostics.presentation
            $visibilityAudit = $diagnostics.visibilityAudit
            $windowPolicy = $diagnostics.windowPolicy
        }
        elseif ($Mode -eq 'legacy-glass' -and $null -ne $diagnostics) {
            $presentP95 = $diagnostics.presentIntervals.P95Milliseconds
            $presentP99 = $diagnostics.presentIntervals.P99Milliseconds
            $renderDurationP95 =
                $diagnostics.renderDurations.P95Milliseconds
            $visibilityAudit = $diagnostics.visibilityAudit
            $windowPolicy = $diagnostics.windowPolicy
        }

        $record = [ordered]@{
            task = 'R22-paired-render-sample'
            mode = $Mode
            round = $Round
            order = $Order
            command = "$probeExe $($arguments -join ' ')"
            warmupSeconds = $WarmupSeconds
            requestedSampleSeconds = $SampleSeconds
            actualSampleSeconds = [Math]::Round($sampleSecondsActual, 3)
            clientWidth = $diagnostics.clientSize.Width
            clientHeight = $diagnostics.clientSize.Height
            deviceDpi = $diagnostics.clientSize.DeviceDpi
            screenIndex = $diagnostics.screen.index
            screenDeviceName = $diagnostics.screen.deviceName
            screenBounds = $diagnostics.screen.bounds
            cpuMilliseconds = [Math]::Round($cpuDelta, 3)
            cpuMillisecondsPerSecond = if ($sampleSecondsActual -gt 0) {
                [Math]::Round($cpuDelta / $sampleSecondsActual, 3)
            } else {
                $null
            }
            cpuPercentOfOneLogicalCore = if ($sampleSecondsActual -gt 0) {
                [Math]::Round(
                    $cpuDelta / ($sampleSecondsActual * 1000) * 100,
                    3)
            } else {
                $null
            }
            workingSetBytesStart = $start.workingSetBytes
            workingSetBytesEnd = $end.workingSetBytes
            workingSetDeltaBytes = $end.workingSetBytes - $start.workingSetBytes
            privateMemoryBytesStart = $start.privateMemoryBytes
            privateMemoryBytesEnd = $end.privateMemoryBytes
            privateMemoryDeltaBytes =
                $end.privateMemoryBytes - $start.privateMemoryBytes
            gdiObjectsStart = $start.gdiObjects
            gdiObjectsEnd = $end.gdiObjects
            userObjectsStart = $start.userObjects
            userObjectsEnd = $end.userObjects
            handleCountStart = $start.handleCount
            handleCountEnd = $end.handleCount
            threadCountStart = $start.threadCount
            threadCountEnd = $end.threadCount
            gpuEngineUtilizationP95 = Get-Percentile $gpuValues 95
            gpuEngineUtilizationMax = Get-Percentile $gpuValues 100
            gpuProcessMemoryMaxBytes = Get-Percentile @(
                $samples | ForEach-Object {
                    $_.gpu.processMemoryBytesSum
                }) 100
            presentIntervalP95Milliseconds = $presentP95
            presentIntervalP99Milliseconds = $presentP99
            presentSyncInterval = $presentSyncInterval
            presentTimingMode =
                if ($presentSyncInterval -eq 1) {
                    'synchronized_to_vblank'
                }
                else {
                    'not_synchronized_or_unknown'
                }
            renderDurationP95Milliseconds = $renderDurationP95
            gpuTimestampQueriesCreated = $gpuTimestampQueriesCreated
            gpuTimestampStatus = $gpuTimestampStatus
            gpuTimestampUnavailableReason = $gpuTimestampUnavailableReason
            gpuPassSampleCount = $gpuPassSampleCount
            gpuPassP95Milliseconds = $gpuPassP95
            gpuPassP99Milliseconds = $gpuPassP99
            compositorFrameAgeP95Milliseconds = $compositorAgeP95
            captureFrameAgeP95Milliseconds = $captureFrameAgeP95
            captureTimestampAvailable = $captureTimestampAvailable
            ledgerResourceBytes = $ledgerResourceBytes
            ledgerResourceCount = $ledgerResourceCount
            presentation = $presentation
            windowPolicy = $windowPolicy
            visibilityAudit = $visibilityAudit
            performanceVisibilityValid =
                $null -ne $visibilityAudit -and
                $visibilityAudit.performanceMode -eq $true -and
                $visibilityAudit.sampleCount -gt 0 -and
                $visibilityAudit.visibleAndUnoccludedThroughout -eq $true -and
                $null -ne $windowPolicy -and
                $windowPolicy.topMost -eq $false -and
                $windowPolicy.foregroundActivation -eq $false
            samples = $samples
            processExitedCleanly = $true
            exitCode = $process.ExitCode
        }
        $recordPath = Join-Path $runDirectory 'performance-sample.json'
        $record |
            ConvertTo-Json -Depth 12 |
            Set-Content -LiteralPath $recordPath -Encoding UTF8
        return $record
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor |
    Select-Object -First 1
$adapters = @(Get-CimInstance Win32_VideoController |
    Select-Object Name, AdapterCompatibility, DriverVersion)
$environment = [ordered]@{
    checkedAt = (Get-Date).ToString('o')
    baselineCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    os = [ordered]@{
        caption = $os.Caption
        version = $os.Version
        build = $os.BuildNumber
        architecture = $os.OSArchitecture
    }
    cpu = [ordered]@{
        name = $cpu.Name
        cores = $cpu.NumberOfCores
        logicalProcessors = $cpu.NumberOfLogicalProcessors
    }
    adapters = $adapters
    processorCount = [Environment]::ProcessorCount
    protocol = [ordered]@{
        warmupSeconds = $WarmupSeconds
        sampleSeconds = $SampleSeconds
        rounds = $Rounds
        physicalWidth = $PhysicalWidth
        physicalHeight = $PhysicalHeight
        screenIndex = $ScreenIndex
        order = 'alternating legacy/gpu and gpu/legacy'
        cpuCounter = 'Process.TotalProcessorTime delta / wall time'
        gpuCounter = 'sum of Windows GPU Engine process instances; diagnostic only'
        gpuPassCounter = 'D3D11 timestamp queries around Direct2D foreground plus material draw'
        presentCounter = 'production swap-chain Present cadence; not a display-scan counter'
        compositorCounter = 'DXGI FrameStatistics SyncQPCTime age'
        visibilityCounter = 'periodic top-level window visibility and occlusion audit; any minimized, hidden or overlapped sample invalidates visible-performance acceptance'
    }
}
$environment |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (
        Join-Path $resolvedOutput 'environment.json') -Encoding UTF8

$runs = [System.Collections.Generic.List[object]]::new()
for ($round = 1; $round -le $Rounds; $round++) {
    $modes = if ($round % 2 -eq 1) {
        @('legacy-glass', 'glass')
    } else {
        @('glass', 'legacy-glass')
    }
    for ($order = 0; $order -lt $modes.Count; $order++) {
        $record = Invoke-ProbeRun `
            -Mode $modes[$order] `
            -Round $round `
            -Order ($order + 1)
        $runs.Add($record)
        Write-Host (
            "round {0} {1}: {2:N3} CPU ms/s, present p95 {3}" -f
            $round,
            $record.mode,
            $record.cpuMillisecondsPerSecond,
            $record.presentIntervalP95Milliseconds)
    }
    Start-Sleep -Seconds 3
}

$pairs = for ($round = 1; $round -le $Rounds; $round++) {
    $legacy = $runs | Where-Object {
        $_.round -eq $round -and $_.mode -eq 'legacy-glass'
    } | Select-Object -First 1
    $gpu = $runs | Where-Object {
        $_.round -eq $round -and $_.mode -eq 'glass'
    } | Select-Object -First 1
    [ordered]@{
        round = $round
        legacyWidth = $legacy.clientWidth
        legacyHeight = $legacy.clientHeight
        legacyDpi = $legacy.deviceDpi
        gpuWidth = $gpu.clientWidth
        gpuHeight = $gpu.clientHeight
        gpuDpi = $gpu.deviceDpi
        legacyCpuMillisecondsPerSecond = $legacy.cpuMillisecondsPerSecond
        gpuCpuMillisecondsPerSecond = $gpu.cpuMillisecondsPerSecond
        cpuDeltaMillisecondsPerSecond =
            [Math]::Round(
                $gpu.cpuMillisecondsPerSecond -
                $legacy.cpuMillisecondsPerSecond,
                3)
        gpuPresentP95Milliseconds =
            $gpu.presentIntervalP95Milliseconds
        gpuPassP95Milliseconds =
            $gpu.gpuPassP95Milliseconds
        compositorAgeP95Milliseconds =
            $gpu.compositorFrameAgeP95Milliseconds
    }
}

$gpuRuns = @($runs | Where-Object mode -eq 'glass')
$checks = [ordered]@{
    pairedGeometryMatchesRequested = @(
        $runs | Where-Object {
            $_.clientWidth -ne $PhysicalWidth -or
            $_.clientHeight -ne $PhysicalHeight -or
            $_.deviceDpi -le 0
        }).Count -eq 0
    pairedGeometryAndDpiUsable = @(
        $pairs | Where-Object {
            $_.legacyWidth -ne $_.gpuWidth -or
            $_.legacyHeight -ne $_.gpuHeight -or
            $_.legacyDpi -le 0 -or
            $_.legacyDpi -ne $_.gpuDpi
        }
    ).Count -eq 0
    pairedScreenMatchesConfigured = @(
        $runs | Where-Object {
            $_.screenIndex -ne $ScreenIndex
        }).Count -eq 0
    cpuAtMost40MillisecondsPerSecond = @(
        $gpuRuns | Where-Object {
            $_.cpuMillisecondsPerSecond -gt 40
        }).Count -eq 0
    presentP95AtMost40Milliseconds = @(
        $gpuRuns | Where-Object {
            $null -eq $_.presentIntervalP95Milliseconds -or
            $_.presentIntervalP95Milliseconds -gt 40 -or
            $_.presentSyncInterval -ne 1
        }).Count -eq 0
    gpuPassP95AtMost4Milliseconds = @(
        $gpuRuns | Where-Object {
            -not $_.gpuTimestampQueriesCreated -or
            $_.gpuTimestampStatus -ne 'samples_available' -or
            $_.gpuPassSampleCount -le 0 -or
            $null -eq $_.gpuPassP95Milliseconds -or
            $_.gpuPassP95Milliseconds -gt 4
        }).Count -eq 0
    compositorAgeP95AtMost100Milliseconds = @(
        $gpuRuns | Where-Object {
            $null -eq $_.compositorFrameAgeP95Milliseconds -or
            $_.compositorFrameAgeP95Milliseconds -gt 100
        }).Count -eq 0
    residentGpuLedgerAtMost128MiB = @(
        $gpuRuns | Where-Object {
            $_.ledgerResourceBytes -gt 128MB
        }).Count -eq 0
    privateMemoryDeltaAtMost64MiB = @(
        $gpuRuns | Where-Object {
            $_.privateMemoryDeltaBytes -gt 64MB
        }).Count -eq 0
    allPerformanceSamplesVisibleAndUnoccluded = @(
        $runs | Where-Object {
            $_.performanceVisibilityValid -ne $true
        }).Count -eq 0
}

$summary = [ordered]@{
    task = 'R22-fixed-glass-paired-performance'
    status = if (($checks.Values -contains $false)) {
        'measured; one-or-more-fixed-budget-checks-failed-or-missing'
    } else {
        'fixed-paired-budget-met; live-capture-frame-age-and-long-stress-remain-separate'
    }
    note = 'This result does not close live-capture frame age, multi-monitor/HDR, long stress, or R25.'
    environment = 'environment.json'
    runs = $runs
    pairs = $pairs
    checks = $checks
    gpuPassLimitation = 'GPU pass time is accepted only when timestamp queries were created, completed with disjoint/valid data, and produced samples. Creation alone is not treated as support, and CPU render duration is not substituted for GPU time.'
    finishedAt = (Get-Date).ToString('o')
}
$summary |
    ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath (
        Join-Path $resolvedOutput 'paired-summary.json') -Encoding UTF8

Write-Host "Paired evidence: $(Join-Path $resolvedOutput 'paired-summary.json')"
