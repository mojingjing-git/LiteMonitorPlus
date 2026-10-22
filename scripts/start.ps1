#requires -Version 7.0
<#
.SYNOPSIS
Launch the local LiteMonitor++ build, building it first when missing.
.EXAMPLE
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/start.ps1
.EXAMPLE
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/start.ps1 -Build
.EXAMPLE
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/start.ps1 -Build -Configuration Debug
.NOTES
Existing builds are reused by default. After changing source code, use -Build.
The application requests administrator rights through the normal UAC prompt.
No running instance is stopped. Settings and window visibility are preserved.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Build,

    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot "bin\$Configuration\net8.0-windows"
$exePath = Join-Path $outDir 'LiteMonitor++.exe'

try {
    if ($Build -or -not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        Write-Host "Building $Configuration before launch..." -ForegroundColor Cyan
        Write-Host 'If the application is running, exit it from the tray before rebuilding.' -ForegroundColor Yellow

        # Reuse the existing build, runtime resource setup and settings backup.
        # NoLaunch skips both GUI startup and the live acceptance checklist.
        $buildArgs = @{ Configuration = $Configuration; NoLaunch = $true }
        if ($Dotnet) { $buildArgs.Dotnet = $Dotnet }
        $LASTEXITCODE = 0
        & (Join-Path $PSScriptRoot 'run-livecheck.ps1') @buildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Build/setup failed (exit code $LASTEXITCODE)."
        }
    }

    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "Application executable was not found: $exePath"
    }

    Write-Host "Launching: $exePath" -ForegroundColor Cyan
    Write-Host 'Accept the UAC prompt to continue.'
    Start-Process -FilePath $exePath -WorkingDirectory $outDir -Verb RunAs -ErrorAction Stop
    Write-Host 'Launch requested. If the panel is hidden, use the tray icon to show it.' -ForegroundColor Green
    Write-Host 'Only one instance per output directory is allowed; an existing instance keeps running.'
    exit 0
} catch {
    Write-Host ("Launch failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}
