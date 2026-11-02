#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResultsRoot,
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string] $Configuration,
    [Parameter(Mandatory)][ValidateRange(1,5)][int] $Tier,
    [string] $BaseOutputPath = 'bin/',
    [string[]] $RequiredCapabilities = @(),
    [switch] $ExactTier
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'scripts/Resolve-Dotnet.ps1')
$dotnet = Resolve-Dotnet
$BaseOutputPath = $BaseOutputPath.Replace('\','/').TrimEnd('/') + '/'
if ($BaseOutputPath -notmatch '^bin/(?:[A-Za-z0-9_-]+/)*$') { throw 'Invalid build output profile' }
$RequiredCapabilities = @($RequiredCapabilities | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | Sort-Object -Unique)
if (-not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) { throw 'No current test results' }
$catalogs = @(Get-ChildItem -LiteralPath $ResultsRoot -Filter catalog.json -File -Recurse)
if ($catalogs.Count -ne 1) { throw "Expected exactly one current run, found $($catalogs.Count)" }
$catalogPath = $catalogs[0].FullName
$run = Split-Path -Parent $catalogPath
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if ($catalog.configuration -cne $Configuration -or $catalog.tier -ne $Tier -or
    $catalog.exactTier -ne $ExactTier.IsPresent -or $catalog.baseOutputPath -cne $BaseOutputPath) {
    throw 'Test run belongs to another configuration, tier or build output profile'
}
foreach ($cap in $RequiredCapabilities) {
    if ($catalog.requiredCapabilities -cnotcontains $cap -or $catalog.capabilities -cnotcontains $cap -or
        @($catalog.tests | Where-Object { $_.selected -and $_.capabilities -ccontains $cap }).Count -eq 0) {
        throw "Required capability was not verified: $cap"
    }
}
$tool = Join-Path $root "tools/TestSuiteCatalog/$BaseOutputPath$Configuration/net8.0-windows/TestSuiteCatalog.dll"
& $dotnet $tool check-stamp $root $Configuration $BaseOutputPath
if ($LASTEXITCODE -ne 0) { throw 'Build identity verification failed' }
& $dotnet $tool verify $catalogPath (Join-Path $run 'results') $run
if ($LASTEXITCODE -ne 0) { throw 'Exact discovery/TRX verification failed' }
Write-Output "Verified $Configuration tier ${Tier}: $run"
