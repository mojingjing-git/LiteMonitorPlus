#requires -Version 7.0
<#
    LiteMonitor++ 取证工具集 —— 公共助手（P-F15）

    ★ 本模块只提供**工具能力**，不对任何 issue 下结论。
      它做的是：隔离运行、沙箱化配置、采样、收集、脱敏。

    ★ 隔离红线（硬约束）：
      长跑 harness **绝不能覆盖用户 settings.json**。
      LiteMonitor++ 的配置路径是 `AppContext.BaseDirectory`（见 src/Core/SettingsHelper.cs:22），
      所以"沙箱"的唯一可靠做法是：**把整个运行目录复制到别的目录再从那里启动**。
      （`SettingsHelper.PathOverride` 是进程内 internal 接缝，外部脚本无法使用。）

    ★ 换行的副作用：单实例互斥体是**按 exe 所在目录路径**生成的
      （见 src/System/Program.cs:43-65），因此沙箱副本与用户实例**可以并存**，
      沙箱 harness 不会顶掉用户正在跑的实例。
#>

Set-StrictMode -Version Latest

# ── 目录定位 ──────────────────────────────────────────────────────────────

function Get-ForensicsRoot {
    <#
        取本工具集所在目录（tools/forensics）。
        所有脚本都用它做相对定位，因此**可从干净检出的任意 cwd 运行**。
    #>
    [CmdletBinding()]
    param()
    return (Split-Path -Parent $PSScriptRoot)
}

function Get-RepoRoot {
    <#
        取仓库根（含 LiteMonitor.csproj 的那一层）。
    #>
    [CmdletBinding()]
    param()
    $root = Split-Path -Parent (Get-ForensicsRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $root 'LiteMonitor.csproj'))) {
        throw "未在 $root 找到 LiteMonitor.csproj —— 仓库根定位失败。"
    }
    return $root
}

function Assert-Pwsh7 {
    <#
        断言当前是 PowerShell 7+。
        ★ 5.1 会因 scripts/Resolve-Dotnet.ps1 的 `#requires -Version 7.0` 直接拒跑，
          所以这里提前给出可操作的报错，而不是让脚本在中途炸掉。
    #>
    [CmdletBinding()]
    param()
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw ("本工具集要求 PowerShell 7+（当前 {0}）。请用 pwsh.exe 重新执行，" +
               "不要退回 powershell.exe 5.1。") -f $PSVersionTable.PSVersion
    }
}

function New-ForensicsOutputDir {
    <#
        建一个证据输出目录并返回其绝对路径。
        默认落在 <仓库根>\docs\reviews\evidence\forensics\<name>-<时间戳>，
        该目录被 .gitignore 忽略（/.evidence/），**证据不进版本库**。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Name,
        [string] $OutputDir,
        [string] $Timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    )

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $OutputDir = Join-Path (Get-RepoRoot) (Join-Path 'docs\reviews\evidence\forensics' ("{0}-{1}" -f $Name, $Timestamp))
    }
    $OutputDir = [IO.Path]::GetFullPath($OutputDir)
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    return $OutputDir
}

# ── 脱敏 ──────────────────────────────────────────────────────────────────

function Protect-ForensicsText {
    <#
        对任意文本做**保守脱敏**：口令 / 票据 / CSRF / 会话 cookie / 机器 GUID /
        卷序列号 / 完整设备序列号。

        设计取舍：宁可多遮一点，也不漏一个凭据。
        本函数**不会**改变数值型取证数据（字节数、句柄数、时间戳），
        只动"看起来像凭据/标识符"的片段。

        用法：Protect-ForensicsText -Text $raw
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()][string] $Text
    )

    if ([string]::IsNullOrEmpty($Text)) { return $Text }

    $s = $Text

    # ① JSON / key=value 形态的凭据与标识符字段
    #    ★ 键名用「前后可带任意单词字符」的宽松匹配：
    #      否则 camelCase 的 WebServerPassword 匹配不到裸的 `password`
    #      （`\b` 在 `rP` 之间不成立），口令就会**漏网**。
    $s = [regex]::Replace(
        $s,
        '(?i)("?\w*(?:password|passwd|pwd|token|ticket|csrf|lmsid|secret|apikey|api_key|authorization|cookie|sessionid|session_id|serial|serialnumber|machineguid|productid)\w*"?)(\s*[:=]\s*)("?)([^"\s,;&}\]]+)',
        { param($m) $m.Groups[1].Value + $m.Groups[2].Value + $m.Groups[3].Value + '***REDACTED***' })

    # ② HTTP 头形态：Set-Cookie: lmsid=...
    $s = [regex]::Replace($s, '(?i)(Set-Cookie:\s*lmsid=)[^;\r\n]+', '$1***REDACTED***')

    # ③ URL 查询串里的票据
    $s = [regex]::Replace($s, '(?i)([?&](?:ticket|csrf|token)=)[^&\s"'']+', '$1***REDACTED***')

    # ④ Windows 机器 GUID（MachineGuid / 常规 GUID 形态）
    $s = [regex]::Replace(
        $s,
        '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b',
        { param($m) $m.Value.Substring(0, 8) + '-****-****-****-' + $m.Value.Substring($m.Value.Length - 4) })

    # ⑤ 卷序列号（Volume Serial Number is XXXX-XXXX）
    $s = [regex]::Replace(
        $s,
        '(?i)(Volume Serial Number is\s+)[0-9A-F]{4}-[0-9A-F]{4}',
        { param($m) $m.Groups[1].Value + '****-****' })

    # ⑥ 显式标注的机器标识
    $s = [regex]::Replace($s, '(?i)(MachineGuid\s*[:=]\s*)\S+', '$1***REDACTED***')

    return $s
}

function Write-RedactedFile {
    <#
        把一段文本脱敏后落盘（UTF-8，无 BOM）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [AllowNull()][AllowEmptyString()][string] $Text
    )
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $safe = Protect-ForensicsText -Text $Text
    [IO.File]::WriteAllText($Path, $safe, (New-Object Text.UTF8Encoding($false)))
}

function Write-JsonFile {
    <#
        写 JSON（UTF-8 无 BOM，缩进 2）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)] $Value,
        [int] $Depth = 8
    )
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $json = $Value | ConvertTo-Json -Depth $Depth
    $json = Protect-ForensicsText -Text $json
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}

# ── 原生进程 ──────────────────────────────────────────────────────────────

function Invoke-NativeCapture {
    <#
        启动一个原生可执行文件并捕获 stdout/stderr/退出码。

        ★ 不用管道直接调用：被测程序在 stdout 被重定向时可能因
          Console.OutputEncoding 抛异常（scripts/run-probes.ps1 里有同样的坑）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory,
        [int] $TimeoutSeconds = 0
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    foreach ($a in $Arguments) { [void]$psi.ArgumentList.Add($a) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # ★ 显式按 UTF-8 解码：.NET 在重定向时本来就按 UTF-8 解，
    #   写出来就是一致；受管子进程需要自己把 Console.OutputEncoding 设成 UTF-8
    #   （见 LhmIntelReport/Program.cs）。
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }

    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()

    # ★ TimeoutSeconds = 0 表示"不设超时"（阻塞等待）。
    #   注意 WaitForExit(0) 的语义是"立即返回当前状态"⇒ 会被误判成超时。
    $exited = if ($TimeoutSeconds -gt 0) {
        $p.WaitForExit($TimeoutSeconds * 1000)
    }
    else {
        $p.WaitForExit()
        $true
    }
    if (-not $exited) {
        try { $p.Kill($true) } catch { }
        $p.WaitForExit(5000) | Out-Null
        return @{ Exit = 124; Out = $outTask.Result; Err = 'timeout'; TimedOut = $true }
    }

    return @{
        Exit     = $p.ExitCode
        Out      = $outTask.Result
        Err      = $errTask.Result
        TimedOut = $false
    }
}

# ── 沙箱化运行实例 ────────────────────────────────────────────────────────

function New-SandboxAppInstance {
    <#
        把主工程的**构建产物**复制成一个沙箱运行目录，并从那里启动进程。

        ★ 为什么必须"复制整个目录"：
          LiteMonitor++ 的配置、日志、历史数据全部写在 **exe 所在目录**
          （AppContext.BaseDirectory）。只复制 exe 会缺 resources/lang、resources/themes，
          只改工作目录也不起作用（程序不读 CWD）。
          ⇒ 唯一可靠且**不触碰用户 settings.json** 的隔离方式就是换一个目录整体运行。

        ★ 副本与用户实例**可以并存**：单实例互斥体按 exe 目录路径生成
          （src/System/Program.cs:43-65），不同目录 = 不同互斥体名。

        参数：
          -SourceDir      主工程构建输出目录（含 LiteMonitor++.exe）
          -SandboxRoot    沙箱根目录（本次运行的证据目录）
          -ExeName        主 exe 名，默认 LiteMonitor++
          -SettingsJson   要写进沙箱的 settings.json 内容（hashtable）；$null = 不写
          -Arguments      传给 exe 的命令行参数
          -Start          是否真的启动进程（$false = 只准备沙箱）

        返回：@{ SandboxDir; ExePath; SettingsPath; ProcessId; Started }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $SourceDir,
        [Parameter(Mandatory)][string] $SandboxRoot,
        [string] $ExeName = 'LiteMonitor++',
        [hashtable] $SettingsJson,
        [string[]] $Arguments = @(),
        [bool] $Start = $true
    )

    $SourceDir = [IO.Path]::GetFullPath($SourceDir)
    if (-not (Test-Path -LiteralPath $SourceDir)) {
        throw "构建输出目录不存在：$SourceDir"
    }
    $sourceExe = Join-Path $SourceDir "$ExeName.exe"
    if (-not (Test-Path -LiteralPath $sourceExe)) {
        throw "在 $SourceDir 找不到 $ExeName.exe"
    }

    $appDir = Join-Path $SandboxRoot 'app'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null

    # exe + 同目录依赖（*.dll / *.json / *.exe）
    Get-ChildItem -LiteralPath $SourceDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.exe', '.dll', '.json') } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $appDir $_.Name) -Force
        }

    # resources 目录（语言包 / 主题 / 插件 / 着色器）
    $srcResources = Join-Path $SourceDir 'resources'
    if (Test-Path -LiteralPath $srcResources) {
        Copy-Item -LiteralPath $srcResources -Destination (Join-Path $appDir 'resources') -Recurse -Force
    }

    # 沙箱配置：只写**沙箱内**的 settings.json，用户目录的 settings.json 一概不碰。
    $settingsPath = Join-Path $appDir 'settings.json'
    if ($null -ne $SettingsJson) {
        Write-JsonFile -Path $settingsPath -Value $SettingsJson -Depth 8
    }

    $result = @{
        SandboxDir  = $appDir
        ExePath     = (Join-Path $appDir "$ExeName.exe")
        SettingsPath = $settingsPath
        ProcessId   = 0
        Started     = $false
    }

    if ($Start) {
        $proc = Start-Process -FilePath $result.ExePath -ArgumentList $Arguments -PassThru -WorkingDirectory $appDir
        $result.ProcessId = $proc.Id
        $result.Started = $true
    }

    return $result
}

function Stop-SandboxAppInstance {
    <#
        结束沙箱实例（连同它拉起的子进程）。
        只对**沙箱目录**下的 exe 动手，绝不按进程名批量杀（那会误伤用户实例）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $SandboxDir,
        [int] $TimeoutSeconds = 15
    )

    $exePath = Join-Path $SandboxDir 'LiteMonitor++.exe'
    if (-not (Test-Path -LiteralPath $exePath)) { return }

    $procs = @(Get-CimInstance Win32_Process -Filter "Name='LiteMonitor++.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($SandboxDir, [StringComparison]::OrdinalIgnoreCase) })

    foreach ($p in $procs) {
        try { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($procs.Count -gt 0) { Start-Sleep -Seconds 2 }
}

# ── 环境事实 ──────────────────────────────────────────────────────────────

function Get-ForensicsEnvironment {
    <#
        采集一次"机器事实"，写进每个证据包的 manifest.json。
        这是**取证背景**，不是结论。
    #>
    [CmdletBinding()]
    param()

    $env = [ordered]@{
        collectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        os             = $null
        osBuild        = $null
        machineName    = $env:COMPUTERNAME
        cpuModel       = $null
        logicalCores   = $null
        totalPhysicalMemoryMB = $null
        gpuAdapters    = @()
        pwshVersion    = $PSVersionTable.PSVersion.ToString()
        admin          = $false
        probes         = [ordered]@{}
    }

    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $env.os = $os.Caption
        $env.osBuild = $os.BuildNumber
        $env.totalPhysicalMemoryMB = [math]::Round($os.TotalVisibleMemorySize / 1024, 0)
    }
    catch { $env.os = [Environment]::OSVersion.VersionString }

    try {
        $cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
        $env.cpuModel = $cpu.Name
        $env.logicalCores = $cpu.NumberOfLogicalProcessors
    }
    catch { }

    try {
        $gpus = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object {
            [ordered]@{
                name         = $_.Name
                driverVersion = $_.DriverVersion
                driverDate   = if ($_.DriverDate) { $_.DriverDate.ToString('o') } else { $null }
                videoMode    = if ($_.VideoModeDescription) { $_.VideoModeDescription } else { $null }
            }
        })
        $env.gpuAdapters = $gpus
    }
    catch { }

    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $env.admin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    catch { }

    foreach ($t in @('wpr.exe', 'xperf.exe', 'wpa.exe', 'poolmon.exe', 'dumpchk.exe')) {
        $cmd = Get-Command $t -ErrorAction SilentlyContinue
        $env.probes[$t] = if ($cmd) { $cmd.Source } else { $null }
    }

    return $env
}

Export-ModuleMember -Function @(
    'Get-ForensicsRoot', 'Get-RepoRoot', 'Assert-Pwsh7', 'New-ForensicsOutputDir',
    'Protect-ForensicsText', 'Write-RedactedFile', 'Write-JsonFile',
    'Invoke-NativeCapture', 'New-SandboxAppInstance', 'Stop-SandboxAppInstance',
    'Get-ForensicsEnvironment'
)
