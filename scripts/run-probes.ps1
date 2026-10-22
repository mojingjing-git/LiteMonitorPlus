<#
.SYNOPSIS
    LiteMonitor++ 外观探针统一入口 —— 串行构建并运行全部外观探针（+ 单元测试）。

.DESCRIPTION
    外观探针一律**不进 sln**，所以 `dotnet build` / `dotnet test` 平时不会碰它们
    ⇒ 「防回退」只在有人想起来手动跑时成立。本脚本把这条门槛变成**一个入口**。

    ★ 全部**串行**执行：同机的正式打包、CPU 测量、录屏/屏幕捕获实验必须串行，
      并发跑会争抢主工程的 obj/。

    探针清单（命名与收录范围由外观线定）：

      | 探针 | 覆盖 | 默认 |
      |---|---|---|
      | AppearanceThemeCheck          | 主题一致性与对比度（442 项）        | 跑 |
      | AppearanceStaticBackdropProbe  | 静态背景降级（21 项）               | 跑 |
      | AppearanceM2Probe              | M2 合同验收门槛                     | 跑 |
      | AppearanceWiringProbe          | 主面板接线                         | 跑 |
      | AppearanceDecorProbe           | 装饰资源                           | 跑 |
      | AppearanceDecorRendererProbe   | 装饰渲染                           | 跑 |
      | AppearanceWindowShapeProbe     | 窗口轮廓                           | 跑 |
      | AppearanceSettingsProbe        | W3 设置页「意外回写」防回退（56 项）| 跑 |
      | AppearanceBackdropProbe        | 实时玻璃后端（**实机屏幕捕获**）    | 需 -IncludeBackdrop |
      | LiteMonitor.Tests              | 单元测试                           | 跑（-SkipUnitTests 可跳过） |

    AppearanceBackdropProbe 需要实机抓屏、耗时约 1 分钟，且会**真的捕获屏幕**
    ⇒ 默认不跑，要跑请显式加 -IncludeBackdrop。

.PARAMETER SkipUnitTests
    跳过单元测试（只跑探针）。

.PARAMETER IncludeBackdrop
    连同实机捕获的 AppearanceBackdropProbe 一起跑。

.PARAMETER OutDir
    证据输出根目录。默认 <仓库根>\docs\reviews\evidence\appearance-probes。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Groups
    选择要执行的组。默认 default 保持原入口行为。
    可用组：default/all、nonperformance、static、fixtures、appearance、
    settings、live、unit。

.PARAMETER ListGroups
    只列出可用组，不构建或运行任何项目。

.PARAMETER ResultPath
    可选；把本次选择和执行结果写成机器可读 JSON。

.EXAMPLE
    scripts/run-probes.ps1
    跑全部默认探针 + 单元测试。

.EXAMPLE
    scripts/run-probes.ps1 -IncludeBackdrop -OutDir D:\evidence
    连实机捕获一起跑，证据写到别处（不污染工作树）。
#>
[CmdletBinding()]
param(
    [switch]$SkipUnitTests,
    [switch]$IncludeBackdrop,
    [string]$OutDir,
    [string]$Configuration = 'Release',
    [string[]]$Groups = @('default'),
    [switch]$ListGroups,
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── 定位仓库根（本脚本在 <仓库根>\scripts\ 下）─────────────────────────────
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $RepoRoot 'LiteMonitor.csproj'))) {
    throw "未在 $RepoRoot 找到 LiteMonitor.csproj —— 仓库根定位失败。"
}
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $RepoRoot 'docs\reviews\evidence\appearance-probes'
}

$KnownGroups = @(
    'default',
    'all',
    'nonperformance',
    'static',
    'fixtures',
    'appearance',
    'settings',
    'live',
    'unit'
)
$RequestedGroups = @(
    $Groups |
        ForEach-Object { [string]$_ -split ',' } |
        ForEach-Object { $_.Trim().ToLowerInvariant() } |
        Where-Object { $_ } |
        Select-Object -Unique
)
if ($RequestedGroups.Count -eq 0) {
    $RequestedGroups = @('default')
}
$UnknownGroups = @($RequestedGroups | Where-Object { $KnownGroups -notcontains $_ })
if ($UnknownGroups.Count -gt 0) {
    throw "Unknown probe group(s): $($UnknownGroups -join ', '). Use -ListGroups."
}

if ($ListGroups) {
    [ordered]@{
        schemaVersion = 1
        groups = [ordered]@{
            default = 'Compatibility mode: all non-live appearance probes and unit tests.'
            all = 'Alias for default.'
            nonperformance = 'Non-live probes; AppearanceM2Probe excludes deferred cases #8 and #9; unit tests omitted.'
            static = 'All non-live appearance probes with their original default case selection.'
            fixtures = 'Fixed-input theme, backdrop, wiring, decor, shape, and M2 probes.'
            appearance = 'Alias for fixtures.'
            settings = 'AppearanceSettingsProbe only.'
            live = 'AppearanceBackdropProbe only; requires -IncludeBackdrop.'
            unit = 'LiteMonitor.Tests only.'
        }
    } | ConvertTo-Json -Depth 5
    exit 0
}

# ── 定位 dotnet ────────────────────────────────────────────────────────────
$Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if ($Dotnet) {
    $DotnetExe = $Dotnet.Source
}
elseif (Test-Path 'G:\TOOL\dotnet-sdk\dotnet.exe') {
    $DotnetExe = 'G:\TOOL\dotnet-sdk\dotnet.exe'
}
else {
    throw '找不到 dotnet。请把 dotnet 加入 PATH，或安装到 G:\TOOL\dotnet-sdk。'
}

Write-Host "仓库根    : $RepoRoot"
Write-Host "dotnet    : $DotnetExe"
Write-Host "配置      : $Configuration"
Write-Host "证据目录  : $OutDir"
Write-Host ''

# ── 探针清单 ───────────────────────────────────────────────────────────────
# ExePath 为相对仓库根；OutArg 表示该探针是否需要 --out <目录>。
$Probes = @(
    @{ Name = 'AppearanceThemeCheck';        Project = 'tests\AppearanceThemeCheck';        Exe = 'output\AppearanceThemeCheck.exe';        NeedsOut = $false; Groups = @('fixtures') },
    @{ Name = 'AppearanceStaticBackdropProbe'; Project = 'tests\AppearanceStaticBackdropProbe'; Exe = 'output\AppearanceStaticBackdropProbe.exe'; NeedsOut = $false; Groups = @('fixtures') },
    @{ Name = 'AppearanceM2Probe';            Project = 'tests\AppearanceM2Probe';            Exe = 'output\AppearanceM2Probe.exe';            NeedsOut = $true; Groups = @('fixtures') },
    @{ Name = 'AppearanceWiringProbe';        Project = 'tests\AppearanceWiringProbe';        Exe = 'output\AppearanceWiringProbe.exe';        NeedsOut = $true; Groups = @('fixtures') },
    @{ Name = 'AppearanceDecorProbe';         Project = 'tests\AppearanceDecorProbe';         Exe = 'output\AppearanceDecorProbe.exe';         NeedsOut = $true; Groups = @('fixtures') },
    @{ Name = 'AppearanceDecorRendererProbe'; Project = 'tests\AppearanceDecorRendererProbe'; Exe = 'output\AppearanceDecorRendererProbe.exe'; NeedsOut = $true; Groups = @('fixtures') },
    @{ Name = 'AppearanceWindowShapeProbe';   Project = 'tests\AppearanceWindowShapeProbe';   Exe = 'output\AppearanceWindowShapeProbe.exe';   NeedsOut = $true; Groups = @('fixtures') },
    @{ Name = 'AppearanceSettingsProbe';      Project = 'tests\AppearanceSettingsProbe';      Exe = 'output\AppearanceSettingsProbe.exe';      NeedsOut = $true; Groups = @('settings', 'fixtures') },
    @{ Name = 'AppearanceBackdropProbe';      Project = 'tests\AppearanceBackdropProbe';      Exe = 'bin\Release\net8.0-windows\AppearanceBackdropProbe.exe'; NeedsOut = $true; NeedsLive = $true; Groups = @('live') }
)

$RunUnitTests = -not $SkipUnitTests -and (
    $RequestedGroups -contains 'default' -or
    $RequestedGroups -contains 'all' -or
    $RequestedGroups -contains 'unit'
)
$IncludeStatic = $RequestedGroups -contains 'default' -or
    $RequestedGroups -contains 'all' -or
    $RequestedGroups -contains 'nonperformance' -or
    $RequestedGroups -contains 'static' -or
    $RequestedGroups -contains 'fixtures' -or
    $RequestedGroups -contains 'appearance' -or
    $RequestedGroups -contains 'settings'
$SelectLiveProbe = $RequestedGroups -contains 'default' -or
    $RequestedGroups -contains 'all' -or
    $RequestedGroups -contains 'live'
$IncludeLive = $SelectLiveProbe -and $IncludeBackdrop

$SelectedProbes = @(
    $Probes | Where-Object {
        $probe = $_
        if ($probe.ContainsKey('NeedsLive') -and $probe.NeedsLive) {
            return $SelectLiveProbe
        }
        if (-not $IncludeStatic) {
            return $false
        }
        if ($RequestedGroups -contains 'settings') {
            return $probe.Name -eq 'AppearanceSettingsProbe'
        }
        return $true
    }
)

if ($SelectedProbes.Count -eq 0 -and -not $RunUnitTests) {
    throw "The requested group selection produced no runnable projects: $($RequestedGroups -join ', ')"
}

$Results = New-Object System.Collections.Generic.List[object]

function Invoke-Native {
    <# 用 Process 启动并捕获输出。
       ★ 不用管道直接调用：探针在 stdout 被重定向时会因 Console.OutputEncoding 抛异常。 #>
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory = $RepoRoot
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $WorkingDirectory

    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    return @{ Exit = $p.ExitCode; Out = $stdout; Err = $stderr }
}

function Add-Result {
    param([string] $Name, [string] $Status, [int] $Exit, [string] $Detail)
    $Results.Add([pscustomobject]@{ Name = $Name; Status = $Status; Exit = $Exit; Detail = $Detail })
    $mark = switch ($Status) { 'PASS' { '  [PASS] ' } 'FAIL' { '  [FAIL] ' } default { '  [SKIP] ' } }
    Write-Host ("{0}{1}{2}" -f $mark, $Name, $(if ($Detail) { " — $Detail" } else { '' }))
}

# ── 1) 构建全部探针（串行）─────────────────────────────────────────────────
Write-Host '── 构建探针（串行）──'
$BuildFailed = @()
foreach ($probe in $SelectedProbes) {
    if ($probe.ContainsKey('NeedsLive') -and
        $probe.NeedsLive -and
        -not $IncludeBackdrop) {
        continue
    }
    $proj = Join-Path $RepoRoot ($probe.Project + '\' + (Split-Path -Leaf $probe.Project) + '.csproj')
    if (-not (Test-Path -LiteralPath $proj)) {
        $BuildFailed += $probe.Name
        Add-Result -Name $probe.Name -Status 'FAIL' -Exit 1 -Detail "Missing project: $proj"
        continue
    }

    $r = Invoke-Native -FilePath $DotnetExe -Arguments @('build', $proj, '-c', $Configuration, '--nologo')
    if ($r.Exit -ne 0) {
        $BuildFailed += $probe.Name
        Write-Host ("  [BUILD FAIL] {0}" -f $probe.Name)
        $tail = ($r.Out -split "`r?`n" | Where-Object { $_ -match ': error ' } | Select-Object -First 3) -join ' | '
        if ($tail) { Write-Host "             $tail" }
        if ($r.Err) { Write-Host ("             " + ($r.Err -split "`r?`n" | Select-Object -First 2)) }
        Write-Host '             ★ 若报「文件被占用」：主工程 exe 正被运行中的实例锁住，先关掉它。'
    }
}
Write-Host ''

# ── 2) 逐个运行 ───────────────────────────────────────────────────────────
Write-Host '── 运行探针（串行）──'
foreach ($probe in $SelectedProbes) {
    if ($probe.ContainsKey('NeedsLive') -and -not $IncludeBackdrop) {
        Add-Result -Name $probe.Name -Status 'SKIP' -Exit 0 -Detail '实机捕获，默认跳过（加 -IncludeBackdrop）'
        continue
    }
    if ($BuildFailed -contains $probe.Name) {
        Add-Result -Name $probe.Name -Status 'FAIL' -Exit 1 -Detail '构建失败，未运行'
        continue
    }

    $exe = Join-Path $RepoRoot ($probe.Project + '\' + $probe.Exe)
    if (-not (Test-Path $exe)) {
        Add-Result -Name $probe.Name -Status 'FAIL' -Exit 1 -Detail "找不到可执行文件：$exe"
        continue
    }

    $args = @()
    if ($probe.NeedsOut) {
        $args += @('--out', (Join-Path $OutDir $probe.Name))
    }
    if ($RequestedGroups -contains 'nonperformance' -and
        $probe.Name -eq 'AppearanceM2Probe') {
        $args += @('--groups', 'nonperformance')
    }

    $r = Invoke-Native -FilePath $exe -Arguments $args
    $summary = ($r.Out -split "`r?`n" |
        Where-Object { $_ -match '合计|结果:|用例 \d+ 个|通过 \d+ 项' } |
        Select-Object -Last 1)
    if (-not $summary) { $summary = ($r.Out -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 1) }

    if ($r.Exit -eq 0) {
        Add-Result -Name $probe.Name -Status 'PASS' -Exit 0 -Detail $summary
    }
    else {
        Add-Result -Name $probe.Name -Status 'FAIL' -Exit $r.Exit -Detail $summary
        $tail = ($r.Out -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 12)
        foreach ($line in $tail) { Write-Host "             $line" }
        if ($r.Err) {
            $errTail = ($r.Err -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 5)
            foreach ($line in $errTail) { Write-Host "             $line" }
        }
    }
}
Write-Host ''

# ── 3) 单元测试 ───────────────────────────────────────────────────────────
if ($RunUnitTests) {
    Write-Host '── 单元测试 ──'
    $testProj = Join-Path $RepoRoot 'tests\LiteMonitor.Tests\LiteMonitor.Tests.csproj'
    $r = Invoke-Native -FilePath $DotnetExe -Arguments @('test', $testProj, '-c', $Configuration, '--nologo')
    $summary = ($r.Out -split "`r?`n" | Where-Object { $_ -match '已通过|失败!' } | Select-Object -Last 1)
    if ($r.Exit -eq 0) {
        Add-Result -Name 'LiteMonitor.Tests' -Status 'PASS' -Exit 0 -Detail $summary
    }
    else {
        Add-Result -Name 'LiteMonitor.Tests' -Status 'FAIL' -Exit $r.Exit -Detail $summary
        $tail = ($r.Out -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -Last 12)
        foreach ($line in $tail) { Write-Host "             $line" }
    }
    Write-Host ''
}
else {
    $skipDetail = if ($SkipUnitTests) {
        '-SkipUnitTests'
    }
    else {
        "unit group not selected ($($RequestedGroups -join ','))"
    }
    Add-Result -Name 'LiteMonitor.Tests' -Status 'SKIP' -Exit 0 -Detail $skipDetail
}

# ── 4) 汇总 ───────────────────────────────────────────────────────────────
$pass = @($Results | Where-Object { $_.Status -eq 'PASS' }).Count
$fail = @($Results | Where-Object { $_.Status -eq 'FAIL' }).Count
$skip = @($Results | Where-Object { $_.Status -eq 'SKIP' }).Count

Write-Host '── 汇总 ──'
Write-Host ("通过 {0} / 失败 {1} / 跳过 {2}（共 {3}）" -f $pass, $fail, $skip, $Results.Count)
Write-Host ("证据目录：{0}" -f $OutDir)

if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
    $resolvedResultPath = if ([IO.Path]::IsPathRooted($ResultPath)) {
        [IO.Path]::GetFullPath($ResultPath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $RepoRoot $ResultPath))
    }
    $resultDirectory = Split-Path -Parent $resolvedResultPath
    if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    }
    [ordered]@{
        schemaVersion = 1
        generatedAt = (Get-Date).ToString('o')
        repository = $RepoRoot
        configuration = $Configuration
        requestedGroups = @($RequestedGroups)
        outDir = $OutDir
        probes = @($Results)
        pass = $pass
        fail = $fail
        skip = $skip
        total = $Results.Count
    } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $resolvedResultPath -Encoding utf8
}

if ($fail -gt 0) {
    Write-Host ''
    Write-Host '★ 有门槛未通过 —— 对应防回退门槛被触发，请按上面的失败输出定位。'
    exit 1
}

exit 0
