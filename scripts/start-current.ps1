#requires -Version 7.0
<#
  LiteMonitor++ —— 启动**当前源码**的构建（而不碰被占用的 bin\Release）。

  背景：bin\Release\net8.0-windows 常被一个正在运行的实例锁定（提权进程从普通
  shell 停不掉），常规构建会卡在 MSB3027。本脚本把构建输出重定向到独立运行目录
  bin\run\{配置}\，与被锁目录、与旧实例完全隔离，因此**任何时候都可用**：

    - 构建当前源码（默认每次构建，保证跑的是最新代码）
    - 平铺驱动/FPS 资源（与 run-dev.ps1 同口径）
    - 可选：把 bin\Release 里那份旧配置（settings.json）导入新运行目录
    - 可选：先结束同一运行目录里的旧实例（不同目录的实例互不干扰，无需结束）
    - 启动（app.manifest 要求管理员权限，会弹 UAC）

  用法：
    pwsh -File scripts/start-current.ps1                      # 构建并启动 Release
    pwsh -File scripts/start-current.ps1 -Configuration Debug
    pwsh -File scripts/start-current.ps1 -NoBuild             # 直接启动已有构建
    pwsh -File scripts/start-current.ps1 -Restart             # 先结束同目录旧实例再启动
    pwsh -File scripts/start-current.ps1 -ImportSettings      # 首次可带上旧配置
    pwsh -File scripts/start-current.ps1 -Dotnet 'C:\Program Files\dotnet\dotnet.exe'

  说明：
    * settings.json 位于各运行目录自身（SettingsHelper 按 AppContext.BaseDirectory 解析），
      所以新目录首次启动是全新默认配置；想沿用旧配置加 -ImportSettings（只拷一次，不覆盖已有）。
    * 单实例互斥体按 exe 所在目录生成：同目录重复启动会立刻退出（用 -Restart）；
      不同目录可并存（旧实例 + 新实例同时跑互不顶掉）。
    * 想停止**别的目录**的实例（如 bin\Release 里的旧实例）：提权实例从非管理员
      shell 停不掉，请从托盘退出，或用管理员 PowerShell 执行。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoBuild,

    [switch]$Restart,

    [switch]$ImportSettings,

    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $repoRoot "bin\run\$Configuration\net8.0-windows"
$exePath = Join-Path $runDir 'LiteMonitor++.exe'

# ----------------------------------------------------------------------
# 定位 dotnet（共享助手：按能力探测挑一个**带 SDK** 的 dotnet）
# ----------------------------------------------------------------------
. (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')

function Get-InstancePath([System.Diagnostics.Process]$p) {
    # 提权实例从非管理员进程读不到 Path（返回空）——尽力而为，读不到就按未知处理。
    try { return $p.Path } catch { return $null }
}
# （-Restart 现按程序名结束全部实例并带提权兜底，路径仅作展示用途保留。）

# ----------------------------------------------------------------------
# -Restart：结束已有 LiteMonitor++ 实例。
#   ★ 提权实例从非管理员 shell 读不到 Path 也 Stop 不掉 —— 先按路径精确匹配，
#     匹配不到/停不掉时**自动提权**（弹一次 UAC）兜底。-Restart 明确给出了
#     "要重启"的意图，因此按程序名结束全部实例是可预期行为。
# ----------------------------------------------------------------------
$runningHere = @()
try {
    $runningHere = @(Get-Process -Name 'LiteMonitor++' -ErrorAction SilentlyContinue)
} catch { }

if ($runningHere.Count -gt 0) {
    if ($Restart) {
        foreach ($p in $runningHere) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction Stop
                Write-Host ("==> 已结束旧实例 PID {0}" -f $p.Id) -ForegroundColor Cyan
            } catch {
                Write-Host ("==> PID {0} 需要管理员权限，弹 UAC 提权重试..." -f $p.Id) -ForegroundColor Yellow
                try {
                    Start-Process pwsh -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList @(
                        '-NoProfile', '-Command', "Stop-Process -Id $($p.Id) -Force -ErrorAction SilentlyContinue"
                    )
                } catch {
                    Write-Warning ("提权结束 PID {0} 未成功（UAC 被取消？）：请从托盘退出后重跑。" -f $p.Id)
                }
            }
        }
        Start-Sleep -Milliseconds 800
    } else {
        Write-Warning ("已有 LiteMonitor++ 实例在跑（PID {0}）。" -f (($runningHere | ForEach-Object Id) -join ', '))
        Write-Warning '本次不结束（未加 -Restart）；若新实例与旧实例同目录会立刻退出。'
    }
}

# ----------------------------------------------------------------------
# 构建（输出重定向到 bin\run\，绝不碰可能被锁的 bin\Release）
# ----------------------------------------------------------------------
if (-not $NoBuild) {
    $dotnetExe = Resolve-Dotnet $Dotnet
    Write-Host "==> dotnet: $dotnetExe" -ForegroundColor Cyan
    Write-Host "==> 构建 $Configuration → bin\run\$Configuration\" -ForegroundColor Cyan
    & $dotnetExe build (Join-Path $repoRoot 'LiteMonitor.csproj') `
        -c $Configuration -p:BaseOutputPath='bin/run/' -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "构建产物不存在：$exePath（先去掉 -NoBuild 构建一次）"
}

# ----------------------------------------------------------------------
# 平铺驱动/FPS 资源（dev 版装驱动走本地包，不依赖网络下载；与 run-dev.ps1 同口径）
# ----------------------------------------------------------------------
$resDir = Join-Path $runDir 'resources'
$assetsDir = Join-Path $repoRoot 'resources\assets'
foreach ($name in @('driver.zip', 'LiteMonitorPlusFPS.exe')) {
    $src = Join-Path $assetsDir $name
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $resDir $name) -Force
    }
}

# ----------------------------------------------------------------------
# 可选：导入旧配置（只在目标目录还没有 settings.json 时做，绝不覆盖）
# ----------------------------------------------------------------------
$settingsPath = Join-Path $runDir 'settings.json'
if ($ImportSettings -and -not (Test-Path -LiteralPath $settingsPath)) {
    $old = Join-Path $repoRoot "bin\Release\net8.0-windows\settings.json"
    if (Test-Path -LiteralPath $old) {
        Copy-Item -LiteralPath $old -Destination $settingsPath
        Write-Host '==> 已导入 bin\Release 的旧配置（主题/监控项/语言随行）。' -ForegroundColor Cyan
    } else {
        Write-Host '==> bin\Release 下没有旧配置，跳过导入。' -ForegroundColor Yellow
    }
}

# ----------------------------------------------------------------------
# 启动
# ----------------------------------------------------------------------
Write-Host "==> 输出目录: $runDir" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $settingsPath)) {
    Write-Host '    （该目录还没有 settings.json：将按全新默认配置启动；要沿用旧配置，下次加 -ImportSettings）' -ForegroundColor DarkGray
}

Write-Host '==> 启动中（app.manifest 要求管理员权限，会弹 UAC）...'
Start-Process -FilePath $exePath -WorkingDirectory $runDir
Write-Host '已启动。' -ForegroundColor Green
