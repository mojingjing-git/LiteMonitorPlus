#requires -Version 7.0
<#
.SYNOPSIS
    LiteMonitor++ 性能基线采集（派工单 P-B0b，Plan B §6.3 / §8 性能口径）。

.DESCRIPTION
    同机同配置记录四类可自动化指标，输出可复跑对比的基线文件，供 B6（及任何
    采样/格式重构批次）前后对比：

      ① 启动时间：进程启动 → WaitForInputIdle 的毫秒数；
      ② 60 秒采样窗口：主进程 CPU 时间与私有字节（1 秒粒度采样，
         含工作集/线程/句柄，用于观察窗口内资源是否持续增长）；
      ③ 采集探针：tests/LiteMonitor.PerformanceProbe 现成的采样分组
         （采样次数/请求次数/整机 CPU%）+ 单设备 Update 微基准 + 兼容缓冲绘制；
      ④ 环境记录：机器/CPU/内存/GPU/OS/.NET/版本/HEAD。

    无法无侵入测量的指标（UI 刷新延迟、目录查询延迟等）写入报告的
    "无法自动采集"清单，绝不造数。

    ★ 隔离保证：GUI 跑在 bin\perf-run\ 的全新默认配置下（不碰用户 settings.json）；
      探针自带沙箱配置（注入 Settings 单例）。全程串行，不与其它构建并发。

    ★ 权限：app.manifest 为 requireAdministrator —— **请从管理员 PowerShell 运行
      本脚本**（否则 GUI 启动会弹 UAC，且对提权进程的 CPU/内存采样会被拒绝访问）。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER WindowSeconds
    采样窗口秒数，默认 60。

.PARAMETER Launches
    GUI 启动轮数（第 1 轮含驱动准备较慢，建议 ≥2 取稳态轮），默认 2。

.PARAMETER SkipGui
    跳过 GUI 实跑（①②不采集）。

.PARAMETER SkipProbe
    跳过采集探针（③不采集）。

.PARAMETER QuickProbe
    探针用 --quick 冒烟参数（3s/10s）。★ 不能作为基线，只用于流程验证。

.PARAMETER WarmupSeconds / SteadySeconds
    覆盖探针每组预热/稳态时长（默认用探针自己的 15s/60s）。

.PARAMETER OutRoot
    输出根目录，默认 docs\verification\baseline。

.PARAMETER LaunchArgs
    透传给 GUI 的启动参数。'--hardware-host' 时按主+Host 双进程合计统计 CPU/私有字节/
    线程/句柄，并采集客户端指标文件（首样延迟/接收延迟 p95）。

.EXAMPLE
    # 管理员 PowerShell 中：完整基线（约 20 分钟）
    pwsh -File scripts/perf-baseline.ps1

.EXAMPLE
    # 只采集元数据 + 构建（快速验证脚本本身）
    pwsh -File scripts/perf-baseline.ps1 -SkipGui -SkipProbe
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(10, 600)]
    [int]$WindowSeconds = 60,

    [ValidateRange(1, 10)]
    [int]$Launches = 2,

    [switch]$SkipGui,
    [switch]$SkipProbe,
    [switch]$QuickProbe,

    [int]$WarmupSeconds = 0,
    [int]$SteadySeconds = 0,

    [string]$OutRoot,
    [string]$Dotnet,

    # P-I2 §2.7：透传给 GUI 的启动参数（如 '--hardware-host' 开启采集进程隔离模式）。
    # 传入后采样窗口按「主进程 + HardwareHost」双进程合计统计（不影响既有单进程默认行为）。
    [string]$LaunchArgs = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $RepoRoot 'LiteMonitor.csproj'))) {
    throw "未在 $RepoRoot 找到 LiteMonitor.csproj —— 仓库根定位失败。"
}
if ([string]::IsNullOrWhiteSpace($OutRoot)) {
    $OutRoot = Join-Path $RepoRoot 'docs\verification\baseline'
}
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

$Ts = Get-Date -Format 'yyyyMMdd-HHmm'
$JsonPath = Join-Path $OutRoot "perf-baseline-$Ts.json"
$MdPath = Join-Path $OutRoot "perf-baseline-$Ts.md"
$ProbeDir = Join-Path $OutRoot "perf-probe-$Ts"

. (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
$dotnetExe = Resolve-Dotnet $Dotnet

# ----------------------------------------------------------------------
# 0. 环境元数据
# ----------------------------------------------------------------------
function Get-EnvMetadata {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $elevated = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    $gitSha = ''
    $gitBranch = ''
    $gitDirty = -1
    try {
        Push-Location $RepoRoot
        $gitSha = (git rev-parse HEAD 2>$null)
        $gitBranch = (git rev-parse --abbrev-ref HEAD 2>$null)
        $gitDirty = @(git status --porcelain 2>$null).Count
        Pop-Location
    } catch { try { Pop-Location } catch { } }

    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $cs = Get-CimInstance Win32_ComputerSystem
    $os = Get-CimInstance Win32_OperatingSystem
    $gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object {
        [ordered]@{
            name = $_.Name
            driverVersion = $_.DriverVersion
            adapterRAM_MB = if ($_.AdapterRAM) { [math]::Round($_.AdapterRAM / 1MB) } else { $null }
        }
    })

    [ordered]@{
        timestamp = (Get-Date).ToString('o')
        machine = $env:COMPUTERNAME
        model = $cs.Model
        manufacturer = $cs.Manufacturer
        cpu = $cpu.Name
        cores_physical = [int]$cpu.NumberOfCores
        cores_logical = [int]$cpu.NumberOfLogicalProcessors
        ram_GB = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
        gpus = $gpus
        os = ("{0} (build {1})" -f $os.Caption, $os.BuildNumber)
        os_version = $os.Version
        dotnet_sdk = (& $dotnetExe --version)
        pwsh = $PSVersionTable.PSVersion.ToString()
        shell_elevated = $elevated
        git_head = $gitSha
        git_branch = $gitBranch
        git_dirty_files = $gitDirty
        configuration = $Configuration
    }
}

Write-Host "==> 采集环境元数据..." -ForegroundColor Cyan
$meta = Get-EnvMetadata
if (-not $meta.shell_elevated) {
    Write-Warning '当前 shell 未提权：GUI 启动会弹 UAC，且对提权进程的采样可能被拒绝访问。建议从管理员 PowerShell 运行。'
}

# ----------------------------------------------------------------------
# 1. 构建主工程 → bin\perf-run\（独立运行目录，全新默认配置）
# ----------------------------------------------------------------------
$runDir = Join-Path $RepoRoot "bin\perf-run\$Configuration\net8.0-windows"
$exePath = Join-Path $runDir 'LiteMonitor++.exe'

Write-Host "==> 构建 $Configuration → bin\perf-run\$Configuration\" -ForegroundColor Cyan
& $dotnetExe build (Join-Path $RepoRoot 'LiteMonitor.csproj') `
    -c $Configuration -p:BaseOutputPath='bin/perf-run/' -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "主工程构建失败（退出码 $LASTEXITCODE）" }
if (-not (Test-Path -LiteralPath $exePath)) { throw "构建产物不存在：$exePath" }

# 平铺驱动/FPS 资源（与 start-current.ps1 同口径）
$resDir = Join-Path $runDir 'resources'
$assetsDir = Join-Path $RepoRoot 'resources\assets'
foreach ($name in @('driver.zip', 'LiteMonitorPlusFPS.exe')) {
    $src = Join-Path $assetsDir $name
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $resDir $name) -Force
    }
}
$appVersion = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion

# ----------------------------------------------------------------------
# 2. GUI 实跑：启动时间 + 60 秒采样窗口
# ----------------------------------------------------------------------
$launchRecords = @()
$guiSkipReason = $null
$hostMode = $LaunchArgs -match 'hardware-host'
$preExistingHostPids = @()
$i2MetricsPath = $null

if ($SkipGui) {
    $guiSkipReason = '命令行 -SkipGui'
} else {
    $existing = @(Get-Process -Name 'LiteMonitor++' -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        $guiSkipReason = ("已有 LiteMonitor++ 实例在跑（PID {0}），无法归属本次测量" -f (($existing | ForEach-Object Id) -join ','))
        Write-Warning $guiSkipReason
    }
    if ($hostMode) {
        $preExistingHostPids = @(Get-Process -Name 'LiteMonitor.HardwareHost' -ErrorAction SilentlyContinue | ForEach-Object Id)
        if ($preExistingHostPids.Count -gt 0) {
            $guiSkipReason = ("已有 HardwareHost 实例在跑（PID {0}），Host 资源无法归属本次测量" -f ($preExistingHostPids -join ','))
            Write-Warning $guiSkipReason
        }
    }
}

function Get-HostSamples {
    # 采样启动之后出现的 HardwareHost 进程（监督器重启会换 PID，逐 tick 动态发现）。
    param([int[]]$ExcludePids)
    $found = @(Get-Process -Name 'LiteMonitor.HardwareHost' -ErrorAction SilentlyContinue |
        Where-Object { $ExcludePids -notcontains $_.Id })
    foreach ($hp in $found) {
        try { $hp.Refresh() } catch { }
    }
    return , $found
}

if (-not $guiSkipReason -and -not $SkipGui) {
    for ($i = 1; $i -le $Launches; $i++) {
        Write-Host ("==> GUI 第 {0}/{1} 轮：启动并测启动时间..." -f $i, $Launches) -ForegroundColor Cyan
        if ($hostMode -and $i -eq 1) {
            # 客户端指标（首样延迟/接收延迟）落盘：子进程继承本环境变量。
            $i2MetricsPath = Join-Path $OutRoot ("i2-metrics-{0}.json" -f $Ts)
            $env:LITEMONITOR_I2_METRICS = $i2MetricsPath
        }
        $launchSplat = @{ FilePath = $exePath; WorkingDirectory = $runDir; PassThru = $true }
        if ($LaunchArgs) { $launchSplat.ArgumentList = $LaunchArgs }
        $p = Start-Process @launchSplat
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $startupMs = $null
        $startupNote = ''
        try {
            $ok = $p.WaitForInputIdle(90000)
            $sw.Stop()
            if ($ok) { $startupMs = $sw.ElapsedMilliseconds }
            else { $startupNote = 'WaitForInputIdle 90s 超时' }
        } catch {
            $sw.Stop()
            $startupNote = "WaitForInputIdle 异常: $($_.Exception.GetType().Name)"
        }

        if ($p.HasExited) {
            Write-Warning ("第 {0} 轮进程已退出（退出码 {1}），跳过采样窗口" -f $i, $p.ExitCode)
            $launchRecords += [ordered]@{ launch = $i; startupMs = $startupMs; note = "进程提前退出（退出码 $($p.ExitCode)）$startupNote"; samples = @() }
            continue
        }

        Write-Host ("    启动耗时: {0} ms {1}" -f ($startupMs ?? '超时'), $startupNote)
        Write-Host ("==> 采样窗口 {0}s（1s 粒度：CPU 时间 / 私有字节 / 工作集 / 线程 / 句柄）..." -f $WindowSeconds) -ForegroundColor Cyan

        $samples = @()
        $winStart = [DateTime]::UtcNow
        $deadline = $winStart.AddSeconds($WindowSeconds)
        $tick = 0
        $winError = $null
        try {
            while ([DateTime]::UtcNow -lt $deadline) {
                $tick++
                try {
                    $p.Refresh()
                    $row = [ordered]@{
                        t = [math]::Round(([DateTime]::UtcNow - $winStart).TotalSeconds, 1)
                        cpuSeconds = [math]::Round($p.TotalProcessorTime.TotalSeconds, 3)
                        privateBytes = $p.PrivateMemorySize64
                        workingSetBytes = $p.WorkingSet64
                        threads = $p.Threads.Count
                        handles = $p.HandleCount
                    }
                    if ($hostMode) {
                        $hosts = Get-HostSamples -ExcludePids @($preExistingHostPids)
                        $hostCpu = 0.0; $hostPriv = [long]0; $hostWs = [long]0; $hostThreads = 0; $hostHandles = 0
                        foreach ($hp in $hosts) {
                            try {
                                $hostCpu += $hp.TotalProcessorTime.TotalSeconds
                                $hostPriv += $hp.PrivateMemorySize64
                                $hostWs += $hp.WorkingSet64
                                $hostThreads += $hp.Threads.Count
                                $hostHandles += $hp.HandleCount
                            } catch { }
                        }
                        $row.hostCount = $hosts.Count
                        $row.hostCpuSeconds = [math]::Round($hostCpu, 3)
                        $row.hostPrivateBytes = $hostPriv
                        $row.hostWorkingSetBytes = $hostWs
                        $row.hostThreads = $hostThreads
                        $row.hostHandles = $hostHandles
                        $row.cpuSecondsTotal = [math]::Round($row.cpuSeconds + $hostCpu, 3)
                        $row.privateBytesTotal = $row.privateBytes + $hostPriv
                        $row.threadsTotal = $row.threads + $hostThreads
                        $row.handlesTotal = $row.handles + $hostHandles
                    }
                    $samples += $row
                } catch {
                    $samples += [ordered]@{ t = [math]::Round(([DateTime]::UtcNow - $winStart).TotalSeconds, 1); error = "采样被拒绝: $($_.Exception.GetType().Name)" }
                }
                # 对齐 1s 网格，避免漂移
                $next = $winStart.AddSeconds($tick)
                $sleepMs = ($next - [DateTime]::UtcNow).TotalMilliseconds
                if ($sleepMs -gt 0) { Start-Sleep -Milliseconds $sleepMs }
            }
        } catch {
            $winError = "采样窗口异常: $($_.Exception.GetType().Name): $($_.Exception.Message)"
        } finally {
            # 无论成败都必须回收自己启动的实例（含 Host：Job Object 兜底之外再显式回收）
            try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction Stop } } catch { Write-Warning "结束进程失败（请手动退出）：$($_.Exception.Message)" }
            try { $p.WaitForExit(15000) | Out-Null } catch { }
            if ($hostMode) {
                $leftovers = Get-HostSamples -ExcludePids @($preExistingHostPids)
                foreach ($hp in $leftovers) {
                    try { Stop-Process -Id $hp.Id -Force -ErrorAction Stop } catch { }
                }
                if ($leftovers.Count -gt 0) { Start-Sleep -Seconds 2 }
            }
        }
        if ($winError) { Write-Warning $winError }

        $winElapsed = ([DateTime]::UtcNow - $winStart).TotalSeconds
        $okSamples = @($samples | Where-Object { $_.Contains('cpuSeconds') })
        $stat = $null
        if ($okSamples.Count -ge 2) {
            $cpuDelta = $okSamples[-1].cpuSeconds - $okSamples[0].cpuSeconds
            $privVals = @($okSamples | ForEach-Object { $_.privateBytes })
            $steady = @($okSamples | Select-Object -Skip 5)   # 去掉前 5s 爬坡
            $steadyPriv = @($steady | ForEach-Object { $_.privateBytes })
            $stat = [ordered]@{
                windowActualSeconds = [math]::Round($winElapsed, 1)
                cpuSecondsDelta = [math]::Round($cpuDelta, 3)
                cpuPercentAllCores = [math]::Round(100 * $cpuDelta / $winElapsed / [Environment]::ProcessorCount, 3)
                privateBytesMin = ($privVals | Measure-Object -Minimum).Minimum
                privateBytesAvg = [math]::Round(($privVals | Measure-Object -Average).Average, 0)
                privateBytesMax = ($privVals | Measure-Object -Maximum).Maximum
                privateBytesSteadyAvg = if ($steadyPriv.Count -gt 0) { [math]::Round(($steadyPriv | Measure-Object -Average).Average, 0) } else { $null }
                threadsFirst = $okSamples[0].threads
                threadsLast = $okSamples[-1].threads
                handlesFirst = $okSamples[0].handles
                handlesLast = $okSamples[-1].handles
                sampleCount = $okSamples.Count
            }
            if ($hostMode -and $okSamples.Count -ge 2 -and $okSamples[0].Contains('cpuSecondsTotal')) {
                $cpuDeltaTotal = $okSamples[-1].cpuSecondsTotal - $okSamples[0].cpuSecondsTotal
                $privTotalVals = @($okSamples | ForEach-Object { $_.privateBytesTotal })
                $steadyTotal = @($okSamples | Select-Object -Skip 5)
                $steadyPrivTotal = @($steadyTotal | ForEach-Object { $_.privateBytesTotal })
                $stat += [ordered]@{
                    combinedCpuSecondsDelta = [math]::Round($cpuDeltaTotal, 3)
                    combinedCpuPercentAllCores = [math]::Round(100 * $cpuDeltaTotal / $winElapsed / [Environment]::ProcessorCount, 3)
                    combinedPrivateBytesAvg = [math]::Round(($privTotalVals | Measure-Object -Average).Average, 0)
                    combinedPrivateBytesMax = ($privTotalVals | Measure-Object -Maximum).Maximum
                    combinedPrivateBytesSteadyAvg = if ($steadyPrivTotal.Count -gt 0) { [math]::Round(($steadyPrivTotal | Measure-Object -Average).Average, 0) } else { $null }
                    hostPrivateBytesSteadyAvg = if ($steadyPrivTotal.Count -gt 0) { [math]::Round(($steadyTotal | ForEach-Object { $_.privateBytesTotal - $_.privateBytes } | Measure-Object -Average).Average, 0) } else { $null }
                    combinedThreadsLast = $okSamples[-1].threadsTotal
                    combinedHandlesLast = $okSamples[-1].handlesTotal
                    hostCountLast = $okSamples[-1].hostCount
                }
            }
        }

        $launchRecords += [ordered]@{
            launch = $i
            note = ("第 {0} 轮{1}" -f $i, $(if ($i -eq 1) { '（含驱动准备，偏慢）' } else { '（稳态轮）' })) + $startupNote
            startupMs = $startupMs
            stats = $stat
            samples = $samples
        }
        Start-Sleep -Seconds 3
    }
}

# ----------------------------------------------------------------------
# 3. 采集探针（PerformanceProbe：采样分组 + 微基准）
# ----------------------------------------------------------------------
$probeRecord = [ordered]@{ skipped = $true; reason = '未执行' }
if (-not $SkipProbe) {
    Write-Host '==> 构建采集探针（Release → tests\LiteMonitor.PerformanceProbe\output\）...' -ForegroundColor Cyan
    & $dotnetExe build (Join-Path $RepoRoot 'tests\LiteMonitor.PerformanceProbe\LiteMonitor.PerformanceProbe.csproj') `
        -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "探针构建失败（退出码 $LASTEXITCODE）" }

    $probeDll = Join-Path $RepoRoot 'tests\LiteMonitor.PerformanceProbe\output\LiteMonitor.PerformanceProbe.dll'
    if (-not (Test-Path -LiteralPath $probeDll)) { throw "探针产物不存在：$probeDll" }

    New-Item -ItemType Directory -Force -Path $ProbeDir | Out-Null
    $probeArgs = @('--out', $ProbeDir)
    if ($QuickProbe) { $probeArgs += '--quick' }
    if ($WarmupSeconds -gt 0) { $probeArgs += @('--warmup', "$WarmupSeconds") }
    if ($SteadySeconds -gt 0) { $probeArgs += @('--steady', "$SteadySeconds") }

    Write-Host ("==> 运行采集探针（输出 {0}）...（完整基线约 13-15 分钟）" -f $ProbeDir) -ForegroundColor Cyan
    & $dotnetExe $probeDll @probeArgs
    $probeExit = $LASTEXITCODE

    $probeJson = Join-Path $ProbeDir 'probe-results.json'
    $groupSummary = @()
    $probeFlags = @()
    if (Test-Path -LiteralPath $probeJson) {
        try {
            $probe = Get-Content -LiteralPath $probeJson -Raw | ConvertFrom-Json
            # StrictMode 下 PSCustomObject 缺失属性会抛错：统一走 PSObject 安全取值
            function Get-Prop($obj, [string]$name) {
                $p = $obj.PSObject.Properties[$name]
                if ($p) { $p.Value } else { $null }
            }
            $skipReason = Get-Prop $probe 'skippedReason'
            $fatalErr = Get-Prop $probe 'fatalError'
            if ($skipReason) { $probeFlags += "skippedReason: $skipReason" }
            if ($fatalErr) { $probeFlags += "fatalError: $fatalErr" }
            foreach ($g in @($probe.groups)) {
                if ($null -eq $g) { continue }
                $groupSummary += [ordered]@{
                    id = Get-Prop $g 'group'
                    purpose = Get-Prop $g 'purpose'
                    collectionRequests = Get-Prop $g 'collectionRequests'
                    collectionRounds = Get-Prop $g 'collectionRounds'
                    wholeMachineCpuPercent = Get-Prop $g 'wholeMachineCpuPercent'
                }
            }
        } catch { $probeFlags += "结果解析失败: $($_.Exception.Message)" }
    } else {
        $probeFlags += 'probe-results.json 未生成'
    }

    $probeRecord = [ordered]@{
        skipped = $false
        args = ($probeArgs -join ' ')
        exitCode = $probeExit
        outDir = $ProbeDir
        resultsJson = $probeJson
        flags = $probeFlags
        groups = $groupSummary
    }
}

# ----------------------------------------------------------------------
# 4. 汇总输出 md + json
# ----------------------------------------------------------------------
# P-I2：Host 模式客户端指标（若 GUI 阶段启用且产物存在）
$i2Metrics = $null
if ($i2MetricsPath -and (Test-Path -LiteralPath $i2MetricsPath)) {
    try { $i2Metrics = Get-Content -LiteralPath $i2MetricsPath -Raw | ConvertFrom-Json } catch { $i2Metrics = $null }
}
Remove-Item Env:LITEMONITOR_I2_METRICS -ErrorAction SilentlyContinue

$record = [ordered]@{
    kind = 'perf_baseline'
    createdAt = (Get-Date).ToString('o')
    script = 'scripts/perf-baseline.ps1'
    meta = $meta
    appVersion = $appVersion
    windowSeconds = $WindowSeconds
    launchArgs = $LaunchArgs
    i2ClientMetrics = $i2Metrics
    gui = [ordered]@{
        skipped = [bool]$guiSkipReason
        reason = $guiSkipReason
        launches = $launchRecords
    }
    probe = $probeRecord
    notAutomatable = @(
        'UI 刷新延迟（帧节奏/单帧绘制耗时）：产品无内部计时发布点；无侵入外部测量需高速抓屏+图像差分，误差不可控，不纳入自动基线。',
        '设备目录查询延迟（目录重扫/热插拔识别时长）：无生产侧计时点，只能靠专用探针场景（DeviceCatalog 契约化后补）。',
        'GPU 宿主渲染路径：属 tools/GpuHostLifecycleProbe 独立口径，不在本脚本范围。',
        '多展示端一致性耗时（GDI+/GPU/横排/任务栏/Web 各端格式化差异）：仅兼容缓冲绘制被 renderBench 部分覆盖，完整多端计时无发布点。'
    )
}
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $JsonPath -Encoding utf8

function FmtMB([double]$b) { return ('{0:N1} MB' -f ($b / 1MB)) }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# LiteMonitor++ 性能基线 $Ts")
[void]$sb.AppendLine()
[void]$sb.AppendLine('- 生成：`scripts/perf-baseline.ps1`（P-B0b，供 B6 前后对比；同机同配置复跑即得对照轮）')
[void]$sb.AppendLine(("- HEAD：`{0}`（{1}，脏文件 {2}）；应用版本 {3}；配置 {4}" -f $meta.git_head, $meta.git_branch, $meta.git_dirty_files, $appVersion, $Configuration))
[void]$sb.AppendLine(("- 机器：{0}（{1} {2}）/ CPU：{3}（{4}C/{5}T）/ 内存 {6} GB" -f $meta.machine, $meta.manufacturer, $meta.model, $meta.cpu, $meta.cores_physical, $meta.cores_logical, $meta.ram_GB))
[void]$sb.AppendLine(("- OS：{0} / .NET SDK {1} / pwsh {2} / shell 提权：{3}" -f $meta.os, $meta.dotnet_sdk, $meta.pwsh, $meta.shell_elevated))
[void]$sb.AppendLine('- GPU：')
foreach ($g in $meta.gpus) { [void]$sb.AppendLine(("    - {0}（驱动 {1}）" -f $g.name, $g.driverVersion)) }
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 1. 启动时间（进程启动 → WaitForInputIdle）')
[void]$sb.AppendLine()
if ($guiSkipReason) {
    [void]$sb.AppendLine(("- 未采集：{0}" -f $guiSkipReason))
} else {
    [void]$sb.AppendLine('| 轮 | 启动 (ms) | 备注 |')
    [void]$sb.AppendLine('|---|---|---|')
    foreach ($l in $launchRecords) {
        [void]$sb.AppendLine(("| {0} | {1} | {2} |" -f $l.launch, ($l.startupMs ?? '超时/失败'), $l.note))
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine(("## 2. 采样窗口（{0}s，1s 粒度）" -f $WindowSeconds))
[void]$sb.AppendLine()
if ($guiSkipReason) {
    [void]$sb.AppendLine(("- 未采集：{0}" -f $guiSkipReason))
} else {
    [void]$sb.AppendLine('| 轮 | CPU 秒 (Δ) | CPU%（全核均摊） | 私有字节 min/avg/max | 稳态均值(去前5s) | 线程 首→末 | 句柄 首→末 |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
    foreach ($l in $launchRecords) {
        if ($null -eq $l.stats) {
            [void]$sb.AppendLine(("| {0} | — | — | 采样不足/失败 | — | — | — |" -f $l.launch))
            continue
        }
        $s = $l.stats
        [void]$sb.AppendLine(("| {0} | {1} | {2} | {3} / {4} / {5} | {6} | {7}→{8} | {9}→{10} |" -f `
            $l.launch, $s.cpuSecondsDelta, $s.cpuPercentAllCores,
            (FmtMB $s.privateBytesMin), (FmtMB $s.privateBytesAvg), (FmtMB $s.privateBytesMax),
            ($(if ($s.privateBytesSteadyAvg) { FmtMB $s.privateBytesSteadyAvg } else { '—' })),
            $s.threadsFirst, $s.threadsLast, $s.handlesFirst, $s.handlesLast))
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('每秒原始采样（CPU 时间/私有字节/工作集/线程/句柄）见同名 `.json` 文件 `gui.launches[].samples`。')
    if ($LaunchArgs) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine(("启动参数：`{0}`（HardwareHost 双进程合计）" -f $LaunchArgs))
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('| 轮 | 合计 CPU 秒 (Δ) | 合计 CPU% | 合计私有字节 avg/max | 合计稳态均值 | Host 稳态增量 | 末线程/句柄 |')
        [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
        foreach ($l in $launchRecords) {
            if ($null -eq $l.stats -or -not $l.stats.Contains('combinedCpuSecondsDelta')) {
                [void]$sb.AppendLine(("| {0} | — | — | 采样不足/失败 | — | — | — |" -f $l.launch))
                continue
            }
            $s = $l.stats
            [void]$sb.AppendLine(("| {0} | {1} | {2} | {3} / {4} | {5} | {6} | {7}/{8} |" -f `
                $l.launch, $s.combinedCpuSecondsDelta, $s.combinedCpuPercentAllCores,
                (FmtMB $s.combinedPrivateBytesAvg), (FmtMB $s.combinedPrivateBytesMax),
                ($(if ($s.combinedPrivateBytesSteadyAvg) { FmtMB $s.combinedPrivateBytesSteadyAvg } else { '—' })),
                ($(if ($s.hostPrivateBytesSteadyAvg) { FmtMB $s.hostPrivateBytesSteadyAvg } else { '—' })),
                $s.combinedThreadsLast, $s.combinedHandlesLast))
        }
        if ($i2Metrics) {
            [void]$sb.AppendLine()
            [void]$sb.AppendLine('客户端 IPC 指标（PipeHardwareClient，采集方法=客户端接收循环时间戳）：')
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine(("| 首样延迟 (ms) | 快照数 | 发布→接收 p50/p95/max (ms) | 样本→接收 p95 (ms) | 序号缺口 | 迟到响应 | 会话数/重启数 |" -f ''))
            [void]$sb.AppendLine('|---|---|---|---|---|---|---|')
            [void]$sb.AppendLine(("| {0} | {1} | {2} / {3} / {4} | {5} | {6} | {7} | {8}/{9} |" -f `
                ($i2Metrics.FirstSnapshotDelayMs ?? '—'), $i2Metrics.SnapshotCount,
                ($i2Metrics.PublishToReceiveP50Ms ?? '—'), ($i2Metrics.PublishToReceiveP95Ms ?? '—'), ($i2Metrics.PublishToReceiveMaxMs ?? '—'),
                ($i2Metrics.SampleToReceiveP95Ms ?? '—'), $i2Metrics.SequenceGaps, $i2Metrics.LateResponses,
                $i2Metrics.SessionCount, $i2Metrics.RestartCount))
        }
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 3. 采集探针（tests/LiteMonitor.PerformanceProbe）')
[void]$sb.AppendLine()
if ($probeRecord.skipped) {
    [void]$sb.AppendLine(("- 未执行：{0}" -f $probeRecord.reason))
} else {
    [void]$sb.AppendLine(("- 结果目录：`{0}`（含 probe-results.json 原始数据与 probe-run.txt 日志），探针退出码 {1}" -f $probeRecord.outDir, $probeRecord.exitCode))
    foreach ($f in $probeRecord.flags) { [void]$sb.AppendLine(("- ⚠ {0}" -f $f)) }
    if ($probeRecord.groups.Count -gt 0) {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('| 组 | 场景 | 采集请求 | 实际采集轮 | 整机 CPU% |')
        [void]$sb.AppendLine('|---|---|---|---|---|')
        foreach ($g in $probeRecord.groups) {
            [void]$sb.AppendLine(("| {0} | {1} | {2} | {3} | {4} |" -f $g.id, $g.purpose, $g.collectionRequests, $g.collectionRounds, $g.wholeMachineCpuPercent))
        }
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('单设备 Update 成本（deviceUpdateCosts）与兼容缓冲绘制（renderBench）见 probe-results.json。')
    }
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 4. 无法自动采集项（不造数，待契约化后补）')
[void]$sb.AppendLine()
foreach ($n in $record.notAutomatable) { [void]$sb.AppendLine(("- {0}" -f $n)) }

Set-Content -LiteralPath $MdPath -Value $sb.ToString() -Encoding utf8

Write-Host ''
Write-Host '==> 完成：' -ForegroundColor Green
Write-Host ("    {0}" -f $MdPath)
Write-Host ("    {0}" -f $JsonPath)
if (-not $probeRecord.skipped) { Write-Host ("    {0}" -f (Join-Path $ProbeDir 'probe-results.json')) }
