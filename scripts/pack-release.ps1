#requires -Version 7.0
<#
.SYNOPSIS
    拼装 LiteMonitor++ 的发布包（Windows x64，框架依赖）。

.DESCRIPTION
    上游的发布 ZIP 一直靠手工拼装，且仓库里的 resources/assets/ 不会被 csproj
    自动复制到输出目录，因此每次发布都可能漏文件。本脚本把这一步固化：

      1. 发布 Updater（单文件，框架依赖），并刷新仓库内的 resources\LiteMonitor++.Updater.exe
      2. 发布主程序到暂存目录
      3. 把 resources/assets/ 中「运行时需要」的资源平铺进 resources/
         （驱动包、FPS 组件、城市库），使发布包可离线完成驱动安装与 FPS 统计
      4. 把 LICENSE 与 NOTICE 打进包内（MIT 分发义务：每个分发副本都必须带许可声明）
      5. 压缩为 dist/LiteMonitorPlus_v{Version}-win-x64.zip 并输出 SHA256

    产物名必须与 src/System/UpdateChecker.cs 中的下载模板严格一致，
    否则「检查更新」会下载到不存在的文件。脚本启动时会自检该一致性。

    说明：app.ico 与 7 个 PNG 是编译期嵌入资源（Properties.Resources.*），
    运行时不需要从磁盘读取，因此不打进发布包。

.PARAMETER Version
    版本号，例如 1.0.0。必须与 LiteMonitor.csproj 的 <Version> 一致。

.PARAMETER Brand
    品牌前缀，用于生成包名，默认 LiteMonitorPlus。

.PARAMETER OutDir
    输出目录（相对仓库根），默认 dist。

.PARAMETER Dotnet
    dotnet 可执行文件路径。留空时依次尝试 $env:LITEMONITOR_DOTNET 与 PATH 上的 dotnet。

.EXAMPLE
    pwsh -File scripts/pack-release.ps1 -Version 1.0.0

.EXAMPLE
    pwsh -File scripts/pack-release.ps1 -Version 1.0.0 -Dotnet 'G:\TOOL\dotnet-sdk\dotnet.exe'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Brand = 'LiteMonitorPlus',
    [string]$OutDir = 'dist',
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$stageDir = Join-Path $repoRoot 'dist\stage'
$outDirFull = Join-Path $repoRoot $OutDir
$updaterOut = Join-Path $repoRoot 'dist\updater'

# 与 UpdateChecker.cs 的下载模板保持一致的包名
$zipName = "${Brand}_v${Version}-win-x64.zip"
$zipPath = Join-Path $outDirFull $zipName

# 运行时需要的资源（平铺进 resources/）
$runtimeAssets = @('driver.zip', 'LiteMonitorPlusFPS.exe', 'CityCode.json')

function Write-Step { param([string]$Text) Write-Host "==> $Text" -ForegroundColor Cyan }

function Resolve-Dotnet {
    param([string]$Explicit)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($Explicit) { $candidates.Add($Explicit) }
    if ($env:LITEMONITOR_DOTNET) { $candidates.Add($env:LITEMONITOR_DOTNET) }
    $candidates.Add('dotnet')

    foreach ($c in $candidates) {
        try {
            $sdks = & $c --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and $sdks) { return $c }
        }
        catch { }
    }
    throw "找不到可用的 .NET SDK。请用 -Dotnet 指定 dotnet.exe，或设置环境变量 LITEMONITOR_DOTNET。"
}

function Remove-Tree {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

# ----------------------------------------------------------------------
# 0. 前置检查
# ----------------------------------------------------------------------

Write-Step "检查发布包名与 UpdateChecker 下载模板的一致性"
$checker = Get-Content -LiteralPath (Join-Path $repoRoot 'src\System\UpdateChecker.cs') -Raw -Encoding UTF8
$expectedTemplate = "${Brand}_v{0}-win-x64.zip"
if (-not $checker.Contains($expectedTemplate)) {
    throw "UpdateChecker.cs 中找不到下载模板 '$expectedTemplate'，发布包名 '$zipName' 会导致更新下载失败。请先修正 UpdateChecker.cs。"
}
Write-Host "    模板匹配: $expectedTemplate"

$csprojPath = Join-Path $repoRoot 'LiteMonitor.csproj'
$csproj = Get-Content -LiteralPath $csprojPath -Raw -Encoding UTF8
if ($csproj -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    throw "LiteMonitor.csproj 的 <Version> 与传入的 -Version $Version 不一致，请先同步版本号。"
}
$versionJson = Get-Content -LiteralPath (Join-Path $repoRoot 'resources\version.json') -Raw -Encoding UTF8
$versionPattern = '"version"\s*:\s*"' + [regex]::Escape($Version) + '"'
if ($versionJson -notmatch $versionPattern) {
    throw "resources/version.json 的 version 与 -Version $Version 不一致，请先同步版本号。"
}
Write-Host "    版本号一致: $Version"

$dotnetCmd = Resolve-Dotnet -Explicit $Dotnet
Write-Host "    dotnet : $dotnetCmd"

# ----------------------------------------------------------------------
# 1. 发布 Updater 并刷新仓库内副本
# ----------------------------------------------------------------------

Write-Step "发布 Updater（单文件 / 框架依赖）"
Remove-Tree $updaterOut
& $dotnetCmd publish (Join-Path $repoRoot 'LiteMonitor.Updater\LiteMonitor.Updater.csproj') `
    -c Release -r win-x64 --self-contained false -o $updaterOut --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Updater 发布失败（exit $LASTEXITCODE）" }

$updaterExe = Join-Path $updaterOut 'LiteMonitor++.Updater.exe'
if (-not (Test-Path -LiteralPath $updaterExe)) { throw "Updater 产物缺失: $updaterExe" }
$updaterRepo = Join-Path $repoRoot 'resources\LiteMonitor++.Updater.exe'
Copy-Item -LiteralPath $updaterExe -Destination $updaterRepo -Force
Write-Host ("    已刷新 {0} ({1} 字节)" -f 'resources\LiteMonitor++.Updater.exe', (Get-Item -LiteralPath $updaterRepo).Length)

# ----------------------------------------------------------------------
# 2. 发布主程序到暂存目录
# ----------------------------------------------------------------------

Write-Step "发布主程序"
Remove-Tree $stageDir
& $dotnetCmd publish $csprojPath `
    -c Release -r win-x64 --self-contained false -o $stageDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败（exit $LASTEXITCODE）" }

# ----------------------------------------------------------------------
# 3. 平铺运行时资源
# ----------------------------------------------------------------------

Write-Step "平铺运行时资源到 resources/"
$assetsDir = Join-Path $repoRoot 'resources\assets'
$stageResources = Join-Path $stageDir 'resources'
if (-not (Test-Path -LiteralPath $stageResources)) {
    New-Item -ItemType Directory -Force -Path $stageResources | Out-Null
}
foreach ($name in $runtimeAssets) {
    $src = Join-Path $assetsDir $name
    if (-not (Test-Path -LiteralPath $src)) { throw "缺少运行时资源: resources/assets/$name" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $stageResources $name) -Force
    Write-Host ("    + resources/{0} ({1} 字节)" -f $name, (Get-Item -LiteralPath $src).Length)
}

# csproj 会把 CityCode.json 复制到 resources/assets/，与上面平铺出来的副本重复，
# 删掉 assets 子目录，保证运行时只存在 resources/CityCode.json 一份
$stageAssets = Join-Path $stageResources 'assets'
if (Test-Path -LiteralPath $stageAssets) {
    Remove-Item -LiteralPath $stageAssets -Recurse -Force
    Write-Host "    - resources/assets/（与平铺副本重复，已移除）"
}

# ----------------------------------------------------------------------
# 4. 许可证与致谢（MIT 分发义务）
# ----------------------------------------------------------------------

Write-Step "打包 LICENSE / NOTICE"
foreach ($name in @('LICENSE', 'NOTICE')) {
    $src = Join-Path $repoRoot $name
    if (-not (Test-Path -LiteralPath $src)) { throw "缺少 $name，MIT 要求每个分发副本都携带许可声明" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $stageDir $name) -Force
}

# ----------------------------------------------------------------------
# 5. 压缩
# ----------------------------------------------------------------------

Write-Step "压缩发布包"
if (-not (Test-Path -LiteralPath $outDirFull)) {
    New-Item -ItemType Directory -Force -Path $outDirFull | Out-Null
}
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$sha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLower()
$size = (Get-Item -LiteralPath $zipPath).Length

Remove-Tree $stageDir
Remove-Tree $updaterOut

Write-Host ""
Write-Host "发布包已生成" -ForegroundColor Green
Write-Host "  路径   : $zipPath"
Write-Host ("  大小   : {0:N2} MB" -f ($size / 1MB))
Write-Host "  SHA256 : $sha"
Write-Host ""
Write-Host "  上传到 GitHub Release 时请使用同名资产，否则 UpdateChecker 下载不到：" -ForegroundColor Yellow
Write-Host "    $zipName"
