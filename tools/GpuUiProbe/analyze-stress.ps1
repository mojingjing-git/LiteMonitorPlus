#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [ValidateRange(1, 120)][int]$MinimumMinutes = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$diagnosticsPath = Join-Path $evidence 'stress.diagnostics.json'
$tracePath = Join-Path $evidence 'stress.resource-trace.json'
$watchdogPath = Join-Path $evidence 'watchdog.result.json'
foreach ($path in @($diagnosticsPath, $tracePath, $watchdogPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required evidence file is missing: $path"
    }
}

$diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw |
    ConvertFrom-Json
$trace = Get-Content -LiteralPath $tracePath -Raw |
    ConvertFrom-Json
$watchdog = Get-Content -LiteralPath $watchdogPath -Raw |
    ConvertFrom-Json
$samples = @($trace.samples | Sort-Object ElapsedMilliseconds)
if ($samples.Count -eq 0) {
    throw 'The resource trace contains no samples.'
}

$windowAuditRecords = if (
    $watchdog.PSObject.Properties.Name -contains 'windowPolicyAudits'
) {
    @($watchdog.windowPolicyAudits)
}
else {
    @()
}
$windowAuditIntervalSeconds = if (
    $watchdog.PSObject.Properties.Name -contains
        'windowPolicyAuditIntervalSeconds'
) {
    [double]$watchdog.windowPolicyAuditIntervalSeconds
}
else {
    300.0
}
$requiredWindowAuditCount =
    [Math]::Floor(
        (($MinimumMinutes * 60.0) - 1) /
            $windowAuditIntervalSeconds) + 1
$windowAuditDetails = @(
    foreach ($record in $windowAuditRecords) {
        $path = [string]$record.path
        $exists = -not [string]::IsNullOrWhiteSpace($path) -and
            (Test-Path -LiteralPath $path)
        $audit = if ($exists) {
            Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        }
        else {
            $null
        }
        [ordered]@{
            path = $path
            exitCode = $record.exitCode
            exists = $exists
            visibleWindowCount = if ($null -ne $audit) {
                $audit.visibleWindowCount
            }
            else {
                $null
            }
            allVisibleWindowsNotTopMost = if ($null -ne $audit) {
                $audit.allVisibleWindowsNotTopMost
            }
            else {
                $null
            }
            targetOwnsForeground = if ($null -ne $audit) {
                $audit.targetOwnsForeground
            }
            else {
                $null
            }
        }
    }
)

function Get-LinearSlope {
    param(
        [double[]]$X,
        [double[]]$Y
    )

    if ($X.Count -lt 2 -or $X.Count -ne $Y.Count) {
        return $null
    }

    $xMean = ($X | Measure-Object -Average).Average
    $yMean = ($Y | Measure-Object -Average).Average
    $numerator = 0.0
    $denominator = 0.0
    for ($index = 0; $index -lt $X.Count; $index++) {
        $xDelta = $X[$index] - $xMean
        $numerator += $xDelta * ($Y[$index] - $yMean)
        $denominator += $xDelta * $xDelta
    }

    if ($denominator -eq 0) {
        return $null
    }

    return $numerator / $denominator
}

$tailStartMilliseconds =
    [double]$diagnostics.actualElapsedMilliseconds - (10 * 60 * 1000)
$tail = @($samples |
    Where-Object {
        [double]$_.ElapsedMilliseconds -ge $tailStartMilliseconds
    })
if ($tail.Count -lt 2) {
    throw 'At least two resource samples are required in the last ten minutes.'
}

$tailMinutes = $tail |
    ForEach-Object {
        [double]$_.ElapsedMilliseconds / 60000.0
    }
$privateMiB = $tail |
    ForEach-Object {
        [double]$_.Process.PrivateMemoryBytes / 1MB
    }
$privateSlope = Get-LinearSlope -X $tailMinutes -Y $privateMiB

function Get-TailMetricSummary {
    param([scriptblock]$Selector)

    $values = @($tail | ForEach-Object { [double](& $Selector $_) })
    $minimum = ($values | Measure-Object -Minimum).Minimum
    $maximum = ($values | Measure-Object -Maximum).Maximum
    return [ordered]@{
        minimum = $minimum
        maximum = $maximum
        range = $maximum - $minimum
    }
}

$tailHandle = Get-TailMetricSummary {
    param($sample)
    $sample.Process.HandleCount
}
$tailThread = Get-TailMetricSummary {
    param($sample)
    $sample.Process.ThreadCount
}
$tailGdi = Get-TailMetricSummary {
    param($sample)
    $sample.Process.GdiObjects
}
$tailUser = Get-TailMetricSummary {
    param($sample)
    $sample.Process.UserObjects
}

$tailStart = [double]($tail | Select-Object -First 1).ElapsedMilliseconds
$handleHighWaterBins = @(
    $tail |
        Group-Object {
            [Math]::Floor(
                ([double]$_.ElapsedMilliseconds - $tailStart) / 120000.0)
        } |
        Sort-Object { [int]$_.Name } |
        ForEach-Object {
            $binSamples = @($_.Group)
            [ordered]@{
                bin = [int]$_.Name
                firstElapsedMilliseconds =
                    ($binSamples | Measure-Object ElapsedMilliseconds -Minimum).
                        Minimum
                handlesMaximum =
                    ($binSamples | ForEach-Object {
                        [double]$_.Process.HandleCount
                    } | Measure-Object -Maximum).Maximum
                gdiMaximum =
                    ($binSamples | ForEach-Object {
                        [double]$_.Process.GdiObjects
                    } | Measure-Object -Maximum).Maximum
                userMaximum =
                    ($binSamples | ForEach-Object {
                        [double]$_.Process.UserObjects
                    } | Measure-Object -Maximum).Maximum
                threadMaximum =
                    ($binSamples | ForEach-Object {
                        [double]$_.Process.ThreadCount
                    } | Measure-Object -Maximum).Maximum
            }
        }
)
$binMinutes = $handleHighWaterBins |
    ForEach-Object {
        [double]$_.firstElapsedMilliseconds / 60000.0
    }
$handleHighWaterSlope = Get-LinearSlope `
    -X $binMinutes `
    -Y @($handleHighWaterBins | ForEach-Object {
        [double]$_.handlesMaximum
    })
$handleHighWaterValues =
    @($handleHighWaterBins | ForEach-Object { $_.handlesMaximum })
$threadHighWaterValues =
    @($handleHighWaterBins | ForEach-Object { $_.threadMaximum })
$handleHighWaterGrowth = if ($handleHighWaterValues.Count -gt 0) {
    $handleHighWaterValues[-1] - $handleHighWaterValues[0]
} else {
    $null
}
$threadHighWaterGrowth = if ($threadHighWaterValues.Count -gt 0) {
    $threadHighWaterValues[-1] - $threadHighWaterValues[0]
} else {
    $null
}
$handleHighWaterIncreases = @(
    for ($index = 1; $index -lt $handleHighWaterValues.Count; $index++) {
        $handleHighWaterValues[$index] -gt
            $handleHighWaterValues[$index - 1]
    }
)
$handleMonotonicIncrease = (
    $handleHighWaterValues.Count -ge 3 -and
    @($handleHighWaterIncreases | Where-Object { $_ }).Count -eq
        ($handleHighWaterValues.Count - 1)
)

$resourcesAfter = $diagnostics.resourcesAfterDispose
$resourcesBefore = $diagnostics.resourcesBeforeDispose
$checks = [ordered]@{
    probeStatusCompleted = $diagnostics.status -eq 'completed'
    runnerStatusCompleted = $watchdog.status -eq 'completed'
    probeExitCodeZero = $watchdog.exitCode -eq 0
    runnerExitCodeZero = $watchdog.runnerExitCode -eq 0
    noForcedTermination = $watchdog.forcedTermination -eq $false
    actualElapsedAtLeastTarget =
        $diagnostics.actualElapsedMilliseconds -ge
            $diagnostics.targetStressMilliseconds
    targetAtLeastRequestedMinutes =
        $diagnostics.targetStressMilliseconds -ge ($MinimumMinutes * 60000)
    hideShowAndThemeCycleMinimums =
        $diagnostics.stressHideCount -ge 100 -and
        $diagnostics.stressShowCount -ge 100 -and
        $diagnostics.stressThemeSwitches -ge 30
    nonTopMostAndNonActivating =
        $diagnostics.windowPolicy.topMost -eq $false -and
        $diagnostics.windowPolicy.foregroundActivation -eq $false -and
        $diagnostics.windowPolicy.periodicShowActivates -eq $false -and
        $diagnostics.windowPolicy.periodicShowBringsToFront -eq $false
    independentWindowAuditCount =
        $windowAuditDetails.Count -ge $requiredWindowAuditCount
    independentWindowAuditsValid =
        @($windowAuditDetails | Where-Object {
            $_.exitCode -ne 0 -or
            -not $_.exists -or
            $_.visibleWindowCount -lt 1 -or
            $_.allVisibleWindowsNotTopMost -ne $true -or
            $_.targetOwnsForeground -ne $false
        }).Count -eq 0
    gpuLedgerZeroAfterDispose =
        $resourcesAfter.ActiveResourceCount -eq 0 -and
        $resourcesAfter.ActiveBytes -eq 0
    gpuLedgerBalanced =
        $null -ne $resourcesBefore -and
        $resourcesBefore.TotalReservations -eq
            $resourcesBefore.TotalReleases +
                $resourcesBefore.ActiveResourceCount
    resourceTraceCadence =
        $trace.sampleIntervalMilliseconds -eq 5000 -and
        $samples.Count -ge (
            [Math]::Floor(
                [double]$diagnostics.actualElapsedMilliseconds / 5000) - 12)
    privateMemorySlopeAtMostOneMiBPerMinute =
        $null -ne $privateSlope -and $privateSlope -le 1
    handleHighWaterGrowthAtMost64 =
        $null -ne $handleHighWaterGrowth -and
        $handleHighWaterGrowth -le 64
    handleHighWaterSlopeAtMostTwoPerMinute =
        $null -ne $handleHighWaterSlope -and
        $handleHighWaterSlope -le 2
    handleHighWaterNotMonotonic =
        -not $handleMonotonicIncrease
    threadHighWaterGrowthAtMostEight =
        $null -ne $threadHighWaterGrowth -and
        $threadHighWaterGrowth -le 8
    gdiUserTailRangesAtMostEight =
        $tailGdi.range -le 8 -and
        $tailUser.range -le 8
}

$passed = -not ($checks.Values -contains $false)
$summary = [ordered]@{
    task = 'R22-gpu-mainform-stress-analysis'
    status = if ($passed) { 'stress-resource-budget-met' } else {
        'stress-resource-budget-failed'
    }
    note = 'This analysis covers lifecycle/resource stress only; fixed and live performance require separate evidence.'
    evidenceDirectory = $evidence
    analyzedAt = (Get-Date).ToString('o')
    diagnosticElapsedMilliseconds = $diagnostics.actualElapsedMilliseconds
    resourceTraceSampleCount = $samples.Count
    windowPolicyAudits = [ordered]@{
        requiredCount = $requiredWindowAuditCount
        recordedCount = $windowAuditDetails.Count
        records = $windowAuditDetails
    }
    lastTenMinutes = [ordered]@{
        sampleCount = $tail.Count
        privateMemorySlopeMiBPerMinute = $privateSlope
        handles = $tailHandle
        gdiObjects = $tailGdi
        userObjects = $tailUser
        threads = $tailThread
        handleHighWaterBins = $handleHighWaterBins
        handleHighWaterSlopePerMinute = $handleHighWaterSlope
        handleHighWaterGrowth = $handleHighWaterGrowth
        handleHighWaterMonotonicIncrease = $handleMonotonicIncrease
        threadHighWaterGrowth = $threadHighWaterGrowth
    }
    resourcesBeforeDispose = $resourcesBefore
    resourcesAfterDispose = $resourcesAfter
    processAfterDispose = $diagnostics.processAfterDispose
    checks = $checks
}

$summaryPath = Join-Path $evidence 'stress.summary.json'
$temporaryPath = $summaryPath + '.tmp'
$summary | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $temporaryPath -Encoding UTF8
Move-Item -LiteralPath $temporaryPath -Destination $summaryPath -Force
$summary | ConvertTo-Json -Depth 8
exit $(if ($passed) { 0 } else { 1 })
