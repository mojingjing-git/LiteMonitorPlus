[CmdletBinding()]
param(
    [switch]$IncludeGpu,
    [switch]$IncludeLiveCapture,
    [string]$EvidenceRoot = "docs/reviews/evidence/gpu-themes/694a8c5"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $head = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to resolve the current Git HEAD."
    }

    $testFilter = @(
        "FullyQualifiedName~RenderingContractTests",
        "FullyQualifiedName~RenderingConfigurationTests",
        "FullyQualifiedName~SceneSnapshotBuilderTests",
        "FullyQualifiedName~PanelGeometryTests",
        "FullyQualifiedName~PresentationSessionTests",
        "FullyQualifiedName~MaterialSchedulingTests",
        "FullyQualifiedName~ForegroundDrawListTests"
    )
    if ($IncludeGpu) {
        $testFilter += "FullyQualifiedName~GpuDeviceContractTests"
    }

    $resultsDirectory = Join-Path $EvidenceRoot "R08"
    New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
    $trxName = if ($IncludeGpu) { "device-resources.trx" } else { "contracts-no-device.trx" }

    dotnet test tests/LiteMonitor.Tests/LiteMonitor.Tests.csproj `
        -c Release `
        --no-restore `
        --nologo `
        --filter ($testFilter -join "|") `
        --results-directory $resultsDirectory `
        --logger "trx;LogFileName=$trxName" `
        --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) {
        throw "GPU contract tests failed."
    }

    $hashes = @(
        "resources/themes/CyberPunk_Mech.json",
        "resources/themes/LiquidGlass.json",
        "resources/themes/CyberPunk_GPU.json",
        "resources/themes/LiquidGlass_GPU.json",
        "resources/shaders/SceneMaterial.hlsl",
        "resources/shaders/SceneMaterialVS.cso",
        "resources/shaders/SceneMaterialPS.cso"
    ) | ForEach-Object {
        $fullPath = Join-Path $repoRoot $_
        [ordered]@{
            path = $_
            sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
            length = (Get-Item -LiteralPath $fullPath).Length
        }
    }

    $liveEvidence = $null
    if ($IncludeLiveCapture) {
        $liveDirectory = Join-Path $EvidenceRoot "R12/contract-script-live-v1"
        New-Item -ItemType Directory -Path $liveDirectory -Force | Out-Null
        dotnet run `
            --project tools/GpuUiProbe/GpuUiProbe.csproj `
            -c Release `
            -- LiquidGlass_GPU $liveDirectory --live
        if ($LASTEXITCODE -ne 0) {
            throw "The live MainForm GPU probe failed."
        }

        $liveDiagnosticsPath = Join-Path $liveDirectory "glass-live-mainform.diagnostics.json"
        $liveDiagnostics = Get-Content -LiteralPath $liveDiagnosticsPath -Raw -Encoding utf8 |
            ConvertFrom-Json
        if ($liveDiagnostics.captureSessionsCreated -ne 1 -or
            $liveDiagnostics.renderer.LiveCaptureRequested -ne $true -or
            $liveDiagnostics.presentation.EntireTopLevelWindowExcluded -ne $true) {
            throw "Live MainForm evidence did not confirm one WDA-excluded capture session."
        }

        $liveEvidence = [ordered]@{
            directory = "R12/contract-script-live-v1"
            captureSessionsCreated = $liveDiagnostics.captureSessionsCreated
            entireTopLevelWindowExcluded =
                $liveDiagnostics.presentation.EntireTopLevelWindowExcluded
            evidenceBounds = $liveDiagnostics.evidenceBounds
        }
    }

    $evidence = [ordered]@{
        schemaVersion = 1
        task = if ($IncludeGpu) { "R08-R13-contracts-and-device" } else { "R10-R17-contracts" }
        baselineCommit = "694a8c57d27b628e0edba803e5f24b3ca53dbc5f"
        observedHead = $head
        executionModel = "DeepSeek V4.1 Flash"
        checkedAt = (Get-Date).ToString("o")
        includeGpu = [bool]$IncludeGpu
        includeLiveCapture = [bool]$IncludeLiveCapture
        filter = ($testFilter -join "|")
        trx = $trxName
        hashes = $hashes
        liveEvidence = $liveEvidence
        limitations = @(
            "The live probe uses only the dedicated synthetic backdrop.",
            "Actual top-level handle recreation is represented by PresentationSession tests; automatic WinForms RecreateHandle disposes this MainForm and is not a valid runtime simulation.",
            "HDR, cross-adapter, multi-monitor, and all Windows 10/11 DPI combinations remain unverified."
        )
    }
    $evidence | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $resultsDirectory "latest-run.json") -Encoding utf8

    Write-Output "GPU contract evidence: $(Join-Path $repoRoot $resultsDirectory)"
}
finally {
    Pop-Location
}
