#requires -Version 7.0
<#
    LiteMonitor++ —— 定位可用的 .NET SDK（供 scripts/ 下的入口脚本 dot-source）。

    ★ 为什么不能直接用 PATH 上的第一个 dotnet：
      Windows 上 PATH 里的 dotnet 经常只是**运行时**而不是完整 SDK —— 执行
      `dotnet build` 时报 "No .NET SDKs found"。直接信任 PATH 会让脚本在不同
      机器上随机失败，而这类失败很难定位（看起来像"代码坏了"）。
      所以这里对每个候选者都做一次**能力探测**：`dotnet --list-sdks` 必须
      真能列出至少一个 SDK，才认为它是可用的。

    ★ 解析顺序（先到先用）：
      1. 显式参数（各脚本的 -Dotnet）
      2. 环境变量 LITEMONITOR_DOTNET
      3. 环境变量 DOTNET_ROOT 指定的安装根
      4. 常见安装位置：%ProgramFiles%\dotnet、%LOCALAPPDATA%\Microsoft\dotnet
      5. PATH 上的 dotnet

    ★ 刻意**不**保留"某台开发机的绝对路径"作为首选：那是机器耦合，
      换台机器就静默失效。此前的硬编码路径已全部移除。

    用法：
      . (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1')
      $dotnetExe = Resolve-Dotnet $Dotnet
#>

function Test-DotnetSdk {
    <#
        能力探测：该 exe 是否是**带 SDK** 的 dotnet。
        · 能否执行是前提（坏掉的 shim 会抛）。
        · `--list-sdks` 必须能列出至少一行形如 `8.0.426 [path]` 的 SDK。
    #>
    param([Parameter(Mandatory = $true)][string]$Executable)

    try {
        $output = & $Executable --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) { return $false }
        return (@($output | Where-Object { $_ -match '^\d+\.\d+\.\d+' }).Count -gt 0)
    }
    catch {
        return $false
    }
}

function Resolve-Dotnet {
    <#
        返回一个**带 SDK** 的 dotnet 可执行文件路径；找不到就抛出可操作的错误。
        @param Explicit 调用方显式指定的路径（-Dotnet）。给了就只认它。
    #>
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) {
            throw "找不到 dotnet 可执行文件: $Explicit"
        }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    if ($env:LITEMONITOR_DOTNET) { $candidates.Add($env:LITEMONITOR_DOTNET) }
    if ($env:DOTNET_ROOT) { $candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add($onPath.Source) }

    $seen = @{}
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $key = $candidate.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true

        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        if (Test-DotnetSdk $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw ("找不到可用的 .NET SDK（需要能列出 SDK 的 dotnet）。" +
           "请用 -Dotnet 指定完整路径，或设置环境变量 LITEMONITOR_DOTNET 指向 SDK 的 dotnet.exe。")
}
