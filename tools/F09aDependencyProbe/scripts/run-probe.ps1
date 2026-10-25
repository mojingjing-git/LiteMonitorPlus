#requires -Version 7
<#
.SYNOPSIS
    P-F09a 受控验证工程运行器。

.DESCRIPTION
    为每个候选包构建一个独立探针工程，然后**用独立子进程**逐场景执行，
    以便观察"库自带后台线程上的未处理异常"是否会把进程带崩（应用层 catch 覆盖不到）。

    每个场景一个子进程的原因：
      · disk-thread-fault / disk-callback-fault 会故意在库线程上制造异常；
        基线（DiskInfoToolkit 1.1.2）预期进程被带走，必须由父进程观察退出码。
      · nvml-invalidate 会临时把 NVML 置为不可用，必须与其它场景隔离。

.PARAMETER OutputDir
    结果输出目录（默认 <仓库>/.tmp/probe-results）。

.EXAMPLE
    pwsh research/F09a/DependencyProbe/scripts/run-probe.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string[]]$Scenarios = @('fingerprint', 'nvml-smoke', 'nvml-stale-handle', 'nvml-invalidate', 'disk-thread-fault', 'disk-callback-fault'),
    [string[]]$Variants = @('Probe.Baseline', 'Probe.Fixed', 'Probe.FixedLatest')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$projRoot = Join-Path $repoRoot 'tools\F09aDependencyProbe'
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot '.tmp\probe-results' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$env:NUGET_PACKAGES = 'G:\TOOL\LiteMonitor-fork\.nuget-packages'
$env:DOTNET_CLI_HOME = 'G:\TOOL\LiteMonitor-fork\.dotnet-cli-home'

Write-Host "== 构建候选探针 =="
foreach ($variant in $Variants) {
    $proj = Join-Path $projRoot "src\$variant\$variant.csproj"
    Write-Host "  build $variant"
    & dotnet build $proj -c Release --nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$variant" }
}

$summary = @()
foreach ($variant in $Variants) {
    $dll = Join-Path $projRoot "src\$variant\bin\Release\net8.0\$variant.dll"
    foreach ($scenario in $Scenarios) {
        $outFile = Join-Path $OutputDir "$variant.$scenario.out.txt"
        $errFile = Join-Path $OutputDir "$variant.$scenario.err.txt"

        Write-Host "== 运行 $variant / $scenario =="
        $proc = Start-Process -FilePath 'dotnet' `
            -ArgumentList @($dll, $scenario) `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile

        $stdout = (Get-Content $outFile -Raw -ErrorAction SilentlyContinue)
        $stderr = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
        $exitCode = $proc.ExitCode

        Write-Host "   exit=$exitCode"
        ($stdout -split "`r?`n") | Where-Object { $_ -like 'PROBE|*' } | ForEach-Object { Write-Host "   $_" }
        if ($exitCode -ne 0) {
            $firstError = ($stderr -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 4)
            $firstError | ForEach-Object { Write-Host "   ! $_" }
        }

        $summary += [pscustomobject]@{
            Variant = $variant
            Scenario = $scenario
            ExitCode = $exitCode
            ProcessCrashed = ($exitCode -ne 0)
            Stdout = $stdout
            StderrHead = (($stderr -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6) -join ' // ')
        }
    }
}

$summary | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDir 'summary.json') -Encoding utf8
Write-Host ''
Write-Host "== 汇总 =="
$summary | Select-Object Variant, Scenario, ExitCode, ProcessCrashed | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "结果目录：$OutputDir"
