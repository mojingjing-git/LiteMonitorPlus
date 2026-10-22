#requires -Version 7.0
<#
  LiteMonitor++ —— 开发模式启动脚本。

  仓库里原本只有 make-icon.ps1 / pack-release.ps1 两个脚本，缺少一个
  「一键起 dev 版」的入口。本脚本补上这个缺口，做三件事：

    1. 用指定 dotnet SDK 构建 Debug（默认）或 Release 版本
    2. 把「只有发布包才需要」的运行时资源平铺到输出目录的 resources/ 下
       （driver.zip / LiteMonitorPlusFPS.exe）——否则 dev 版装驱动会走网络
       下载，而自有 GitHub 仓库上线前那个 URL 是取不到东西的
    3. 启动 LiteMonitor++.exe

  用法：
    pwsh -File scripts/run-dev.ps1                          # Debug 构建并启动
    pwsh -File scripts/run-dev.ps1 -Configuration Release   # Release 版
    pwsh -File scripts/run-dev.ps1 -NoLaunch                 # 只构建不启动
    pwsh -File scripts/run-dev.ps1 -NoFlatResources          # 不平铺驱动/FPS 资源
    pwsh -File scripts/run-dev.ps1 -Dotnet 'D:\sdk\dotnet.exe'

  注意：
    * app.manifest 声明了 requireAdministrator，启动必然弹 UAC。
      想免弹窗，请用「以管理员身份运行」的 PowerShell 执行本脚本。
    * 单实例互斥体按 exe 所在目录生成（Program.cs 里按 MainModule 路径取哈希），
      所以 dev 版与发布版可以同时跑，互不顶掉；但同一目录重复启动，
      第二个会立刻退出。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$Dotnet,

    [switch]$NoLaunch,

    [switch]$NoFlatResources
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# ----------------------------------------------------------------------
# 定位 dotnet
# ----------------------------------------------------------------------

function Resolve-Dotnet([string]$explicit) {
    if ($explicit) {
        if (-not (Test-Path -LiteralPath $explicit)) {
            throw "找不到 dotnet 可执行文件: $explicit"
        }
        return (Resolve-Path -LiteralPath $explicit).Path
    }
    # 本机 SDK 在 PATH 上的 dotnet 经常是残废的（报 No .NET SDKs found），优先用已知的完整 SDK
    $known = 'G:\TOOL\dotnet-sdk\dotnet.exe'
    if (Test-Path -LiteralPath $known) { return $known }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw '找不到 dotnet，请用 -Dotnet 指定完整路径'
}

$dotnetExe = Resolve-Dotnet $Dotnet
Write-Host "==> dotnet: $dotnetExe" -ForegroundColor Cyan
Write-Host "==> 配置  : $Configuration" -ForegroundColor Cyan

# ----------------------------------------------------------------------
# 构建
# ----------------------------------------------------------------------

$csproj = Join-Path $repoRoot 'LiteMonitor.csproj'
Write-Host "`n==> 构建 LiteMonitor.csproj" -ForegroundColor Cyan
& $dotnetExe build $csproj -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "构建失败（退出码 $LASTEXITCODE）"
}

$outDir = Join-Path $repoRoot "bin\$Configuration\net8.0-windows"
$exePath = Join-Path $outDir 'LiteMonitor++.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "构建产物不存在: $exePath"
}

# ----------------------------------------------------------------------
# 平铺运行时资源（与 scripts/pack-release.ps1 保持一致）
# ----------------------------------------------------------------------

if (-not $NoFlatResources) {
    Write-Host "`n==> 平铺运行时资源到输出目录 resources/" -ForegroundColor Cyan
    $resDir = Join-Path $outDir 'resources'
    $assetsDir = Join-Path $repoRoot 'resources\assets'
    foreach ($name in @('driver.zip', 'LiteMonitorPlusFPS.exe')) {
        $src = Join-Path $assetsDir $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $resDir $name) -Force
            Write-Host ("    + resources/{0}  ({1} 字节)" -f $name, (Get-Item -LiteralPath $src).Length)
        } else {
            Write-Host ("    - 跳过 resources/{0}：仓库 resources/assets/ 下不存在" -f $name) -ForegroundColor Yellow
        }
    }
}

# ----------------------------------------------------------------------
# 启动
# ----------------------------------------------------------------------

Write-Host "`n==> 输出目录: $outDir" -ForegroundColor Cyan

if ($NoLaunch) {
    Write-Host '已按 -NoLaunch 跳过启动。' -ForegroundColor Green
    exit 0
}

# 同一目录已有实例在跑的话，再启动一个会立刻退出，先提醒
$already = $false
try {
    $already = [bool](Get-Process -Name 'LiteMonitor++' -ErrorAction SilentlyContinue |
        Where-Object { try { $_.Path -eq $exePath } catch { $false } })
} catch { }

if ($already) {
    Write-Host '注意：该输出目录已有 LiteMonitor++ 在运行，新实例会立刻退出。' -ForegroundColor Yellow
}

Write-Host '启动中（app.manifest 要求管理员权限，会弹 UAC）...'
Start-Process -FilePath $exePath -WorkingDirectory $outDir
Write-Host '已启动。关闭主界面即可退出。' -ForegroundColor Green
