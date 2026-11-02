#requires -Version 7.0
<#
.SYNOPSIS
    Run audited test tiers. Tier N includes 1..N; -ExactTier selects only N.
    Defaults to tier 2, Release, isolated bin/test-tiers outputs.
.EXAMPLE
    pwsh -File scripts/run-tests.ps1
.EXAMPLE
    pwsh -File scripts/run-tests.ps1 -Tier 3 -Configuration Debug
.EXAMPLE
    pwsh -File scripts/run-tests.ps1 -Tier 5 -Capabilities Desktop,Gpu,Hardware -RequiredCapabilities Desktop,Gpu
#>
[CmdletBinding()]
param(
    [ValidateRange(1,5)][int] $Tier = 2,
    [switch] $ExactTier,
    [ValidateSet('Debug','Release')][string] $Configuration = 'Release',
    [string[]] $Capabilities = @(),
    [string[]] $RequiredCapabilities = @(),
    [string] $BaseOutputPath = 'bin/test-tiers/',
    [string] $ResultsDirectory,
    [switch] $NoBuild,
    [switch] $BuildOnly,
    [switch] $ListOnly,
    [switch] $UpdateBaseline,
    [string] $Dotnet
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
$dotnetExe = Resolve-Dotnet $Dotnet
$Capabilities = @($Capabilities | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | Sort-Object -Unique)
$RequiredCapabilities = @($RequiredCapabilities | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | Sort-Object -Unique)
$knownCapabilities = @('Desktop','Gpu','Hardware','Network')
foreach ($cap in @($Capabilities) + @($RequiredCapabilities)) {
    if ($knownCapabilities -cnotcontains $cap) { throw "Unknown capability: $cap" }
}
foreach ($cap in $RequiredCapabilities) {
    if ($Capabilities -cnotcontains $cap) { throw "Required capability was not enabled: $cap" }
}
if (($BuildOnly -and ($ListOnly -or $UpdateBaseline)) -or ($ListOnly -and $UpdateBaseline)) { throw 'Choose only one of BuildOnly, ListOnly and UpdateBaseline' }
# Keep the same relative profile in every ProjectReference. Existing Host identity checks
# derive the main output from tests/LiteMonitor.Tests/bin/<profile>/<configuration>.
$BaseOutputPath = $BaseOutputPath.Replace('\','/').TrimEnd('/') + '/'
if ($BaseOutputPath -notmatch '^bin/(?:[A-Za-z0-9_-]+/)*$') { throw 'BaseOutputPath must be a relative bin/<profile>/ directory' }
$catalogExe = Join-Path $repoRoot "tools/TestSuiteCatalog/$BaseOutputPath$Configuration/net8.0-windows/TestSuiteCatalog.dll"
$manifest = Join-Path $repoRoot 'tests/test-tiers.json'
$baseline = Join-Path $repoRoot 'tests/test-tier-baseline.json'
$environmentNames = @('LITEMONITOR_PLUGIN_REPO','LITEMONITOR_THEME_REPO','LITEMONITOR_GPU_TESTS','LITEMONITOR_E2E','LITEMONITOR_HOST_EXE','LITEMONITOR_C03_CHILD','LITEMONITOR_C03_SENTINEL')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    $env:LITEMONITOR_PLUGIN_REPO = Join-Path $repoRoot 'tests/fixtures/plugin-repo'
    $env:LITEMONITOR_THEME_REPO = Join-Path $repoRoot 'tests/fixtures/theme-repo'
    $env:LITEMONITOR_GPU_TESTS = if ($Capabilities -ccontains 'Gpu') { '1' } else { '' }
    $env:LITEMONITOR_E2E = if ($Capabilities -ccontains 'Network') { '1' } else { '' }
    $env:LITEMONITOR_C03_CHILD = ''
    $env:LITEMONITOR_C03_SENTINEL = ''
    $env:LITEMONITOR_HOST_EXE = Join-Path $repoRoot "src/HardwareHost/$BaseOutputPath$Configuration/net8.0-windows/LiteMonitor.HardwareHost.exe"
    if (-not $NoBuild) {
        & $dotnetExe build (Join-Path $repoRoot 'LiteMonitor.sln') -c $Configuration "-p:BaseOutputPath=$BaseOutputPath" -p:NuGetAudit=false --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Build failed: exit $LASTEXITCODE" }
        & $dotnetExe $catalogExe stamp $repoRoot $Configuration $BaseOutputPath
        if ($LASTEXITCODE -ne 0) { throw 'Build identity recording failed' }
    }
    & $dotnetExe $catalogExe check-stamp $repoRoot $Configuration $BaseOutputPath
    if ($LASTEXITCODE -ne 0) { throw 'Missing/stale build; rerun without -NoBuild' }
    if ($BuildOnly) { Write-Output "Build verified: $Configuration / $BaseOutputPath"; return }

    $runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $resultBase = if ($ResultsDirectory) { [IO.Path]::GetFullPath($ResultsDirectory) } else { Join-Path $repoRoot 'TestResults/test-tiers' }
    $runDir = Join-Path $resultBase "$Configuration-$runId"
    New-Item -ItemType Directory -Path $runDir | Out-Null
    $operation = if ($UpdateBaseline) { 'update-baseline' } else { 'prepare' }
    & $dotnetExe $catalogExe $operation $repoRoot $Configuration $BaseOutputPath $manifest $baseline $runDir $Tier ($ExactTier.IsPresent.ToString()) ($Capabilities -join ',') ($RequiredCapabilities -join ',')
    if ($LASTEXITCODE -ne 0) { throw 'Test inventory/classification validation failed' }
    if ($UpdateBaseline) { Write-Output "Baseline explicitly updated: $baseline"; return }
    $catalog = Get-Content -LiteralPath (Join-Path $runDir 'catalog.json') -Raw | ConvertFrom-Json
    $excluded = @($catalog.tests | Where-Object { -not $_.selected } | Group-Object exclusion | ForEach-Object { "$($_.Name)=$($_.Count)" })
    Write-Output ('Excluded: ' + ($excluded -join '; '))
    Write-Output "Current run: $runDir"
    if ($ListOnly) { Write-Output 'Discovery only; no test execution claimed.'; return }
    $resultRoot = Join-Path $runDir 'results'
    New-Item -ItemType Directory -Path $resultRoot | Out-Null
    $started = [Diagnostics.Stopwatch]::StartNew()
    $failedAssemblies = [Collections.Generic.List[string]]::new()
    foreach ($artifact in $catalog.artifacts) {
        $settings = Join-Path $runDir ($artifact.assembly + '.runsettings')
        if (-not (Test-Path -LiteralPath $settings)) { continue }
        $assemblyResult = Join-Path $resultRoot $artifact.assembly
        $project = Join-Path $repoRoot ("tests/{0}/{0}.csproj" -f $artifact.assembly)
        & $dotnetExe test $project -c $Configuration "-p:BaseOutputPath=$BaseOutputPath" --no-build --nologo --settings $settings --logger 'trx;LogFileName=results.trx' --results-directory $assemblyResult
        if ($LASTEXITCODE -ne 0) { $failedAssemblies.Add($artifact.assembly) }
    }
    $started.Stop()
    & $dotnetExe $catalogExe verify (Join-Path $runDir 'catalog.json') $resultRoot $runDir
    $verificationExit = $LASTEXITCODE
    [ordered]@{ configuration=$Configuration; tier=$Tier; exactTier=$ExactTier.IsPresent; seconds=$started.Elapsed.TotalSeconds; failedAssemblies=@($failedAssemblies) } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDir 'execution.json') -Encoding utf8
    if ($failedAssemblies.Count -gt 0 -or $verificationExit -ne 0) { throw "Test run failed; inspect $runDir" }
    Write-Output ("Verified tier {0}{1}: {2:N2}s. Report: {3}" -f $Tier, $(if ($ExactTier) { ' only' } else { ' cumulative' }), $started.Elapsed.TotalSeconds, (Join-Path $runDir 'summary.md'))
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    Set-Location -LiteralPath $originalLocation
}
