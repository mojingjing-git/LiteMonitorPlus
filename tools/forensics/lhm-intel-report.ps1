#requires -Version 7.0
<#
.SYNOPSIS
    LHM 显卡报告导出（P-F15 / 上游 issue #592 取证工具的包装脚本）。

.DESCRIPTION
    构建并运行 `tools/forensics/LhmIntelReport`（一个只引用 LibreHardwareMonitorLib 0.9.6 的
    小控制台程序），把 LHM 看到的显卡报告导出来：

      · `IHardware.GetReport()` 原文（每个显卡的驱动版本、传感器明细）
      · **整机报告**（`IComputer.GetReport()`）—— ★「Intel GCL Initialized: True/False」
        这一行就出现在这里的 `Intel GPU Detection` 段里，**不在**单个显卡的 GetReport() 里
        （已在本机 AMD + 双 NVIDIA 机器上实测确认）
      · 全部传感器的 名称 / 类型 / 值 —— 用来区分
            「传感器键不存在」        → 报告里根本没有该传感器名
            「键存在但值恒 null」      → 传感器在、Value 为 null（★ #592 的现象）
      · 含 GCL 字样的行（整机报告 + 每个显卡各一份）

    ★ LHM 0.9.6 的 `Computer` **没有公开 `Groups` 属性** ⇒ 分组级 `GetReport()` 取不到；
      探针改用整机报告作为等价替代（它包含全部分组的传感器树）。

    ★ 本工具**不下结论**：它导出 LHM 的原始观察结果。

    ★ 硬件前置：能打开 LHM 硬件树通常需要**管理员权限**；部分传感器还需要 PawnIO 驱动
      （LiteMonitor++ 启动时也会检查这一项）。

.PARAMETER OutputDir
    证据输出目录。默认 <仓库根>\docs\reviews\evidence\forensics\lhm\<时间戳>。

.PARAMETER Build
    强制重新构建探针（默认：产物不存在或比源码旧时才构建）。

.PARAMETER ShowDetail
    把探针的显卡明细打到标准输出。
    ★ 不叫 -Verbose：那是 PowerShell 的公共参数（[CmdletBinding()] 自带），重名会直接报错。

.PARAMETER DryRun
    自检：校验 dotnet / 探针工程是否就绪，**不**构建、**不**运行硬件探测。

.EXAMPLE
    pwsh tools/forensics/lhm-intel-report.ps1 -DryRun

.EXAMPLE
    # 在 Intel Arc B580 真机上以管理员身份运行
    pwsh tools/forensics/lhm-intel-report.ps1 -ShowDetail
#>
[CmdletBinding()]
param(
    [string] $OutputDir,
    [switch] $Build,
    [switch] $ShowDetail,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force
Assert-Pwsh7

$repoRoot = Get-RepoRoot
$probeDir = Join-Path $PSScriptRoot 'LhmIntelReport'
$proj = Join-Path $probeDir 'LhmIntelReport.csproj'
$binDir = Join-Path $probeDir 'bin\Release\net8.0-windows'
$dll = Join-Path $binDir 'LhmIntelReport.dll'

if (-not (Test-Path -LiteralPath $proj)) { throw "找不到探针工程：$proj" }

# ── 定位 dotnet ──────────────────────────────────────────────────────────

$resolveScript = Join-Path $repoRoot 'scripts\Resolve-Dotnet.ps1'
if (-not (Test-Path -LiteralPath $resolveScript)) {
    throw "找不到共享的 dotnet 定位脚本：$resolveScript"
}
. $resolveScript
$dotnetExe = Resolve-Dotnet $null

Write-Host ("探针工程  : {0}" -f $proj)
Write-Host ("dotnet    : {0}" -f $dotnetExe)

# ── 构建 ─────────────────────────────────────────────────────────────────

$needBuild = $Build -or (-not (Test-Path -LiteralPath $dll))
if (-not $needBuild) {
    $newestSource = Get-ChildItem -LiteralPath $probeDir -File -Recurse -Include '*.cs', '*.csproj' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newestSource -and $newestSource.LastWriteTimeUtc -gt (Get-Item -LiteralPath $dll).LastWriteTimeUtc) {
        $needBuild = $true
    }
}

if ($DryRun) {
    Write-Host ("需要构建  : {0}" -f $needBuild)
    Write-Host ("产物      : {0}" -f $dll)
    Write-Host '★ 自检模式：未构建、未运行硬件探测。'
    exit 0
}

if ($needBuild) {
    Write-Host ''
    Write-Host '── 构建探针 ──'
    $r = Invoke-NativeCapture -FilePath $dotnetExe -Arguments @('build', $proj, '-c', 'Release', '--nologo') -TimeoutSeconds 900
    Write-Host ($r.Out + $r.Err)
    if ($r.Exit -ne 0) {
        throw "探针构建失败（exit=$($r.Exit)）。离线环境请确认 NUGET_PACKAGES 指向已缓存的包目录。"
    }
}

if (-not (Test-Path -LiteralPath $dll)) { throw "构建成功但找不到产物：$dll" }

# ── 运行 ─────────────────────────────────────────────────────────────────

$outDir = New-ForensicsOutputDir -Name 'lhm' -OutputDir $OutputDir
Write-Host ''
Write-Host ("输出目录  : {0}" -f $outDir)
Write-Host '正在导出 LHM 显卡报告（若需要管理员权限，请以管理员身份重跑）…'

$runArgs = @($dll, '--out', $outDir)
if ($ShowDetail) { $runArgs += '--verbose' }
$r = Invoke-NativeCapture -FilePath $dotnetExe -Arguments $runArgs -TimeoutSeconds 600
Write-Host ($r.Out + $r.Err)

# ── 清单 ─────────────────────────────────────────────────────────────────

$manifest = [ordered]@{
    schemaVersion = 1
    tool          = 'tools/forensics/lhm-intel-report.ps1'
    probe         = 'tools/forensics/LhmIntelReport'
    collectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    purpose       = '上游 issue #592（Intel B580 使用率不显示）—— LHM GetReport() 与传感器值取证'
    disclaimer    = '本包是 LHM 的原始观察结果，不含任何根因判定。'
    exitCode      = $r.Exit
    outputs       = @('lhm-report.txt', 'lhm-report.json', 'sensors.csv')
    environment   = (Get-ForensicsEnvironment)
}
Write-JsonFile -Path (Join-Path $outDir 'manifest.json') -Value $manifest -Depth 8

Write-Host ''
Write-Host ("报告 TXT  : {0}" -f (Join-Path $outDir 'lhm-report.txt'))
Write-Host ("报告 JSON : {0}" -f (Join-Path $outDir 'lhm-report.json'))
Write-Host ("传感器 CSV : {0}" -f (Join-Path $outDir 'sensors.csv'))

if ($r.Exit -eq 2) {
    Write-Host ''
    Write-Host '★ 探针退出码 2 = LHM 硬件树打开失败（多为缺管理员权限 / PawnIO 未装）。' -ForegroundColor Yellow
    exit 2
}
exit 0
