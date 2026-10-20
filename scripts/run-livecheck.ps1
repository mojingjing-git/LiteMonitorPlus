#requires -Version 5.1
<#
  LiteMonitor++ —— 实机验收启动脚本（构建 + 备份配置 + 启动 + DPI 生效自检）。

  背景：2026-10-21 起主工程改为 Per-Monitor-V2 感知（LiteMonitor.csproj 的
  <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>），
  验收需要「跑最新代码 + 不污染用户配置 + 一眼看出 DPI 感知是否真的生效」。

  本脚本做五件事：

    1. 用指定 dotnet SDK 构建 Release（默认）或 Debug
    2. 把「只有发布包才需要」的运行时资源平铺到输出目录 resources/
       （driver.zip / LiteMonitorPlusFPS.exe）——否则 dev 版装驱动会走网络下载
    3. 备份**当前输出目录**里的 settings.json（★ 见下方「配置在哪」）
    4. 启动 LiteMonitor++.exe
    5. 启动后自动取主窗口的 GetDpiForWindow，判定 Per-Monitor-V2 是否真的生效，
       并把实机验收清单的 8 项打出来供逐项核对

  用法：
    pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/run-livecheck.ps1 -StopExisting   # ★ 推荐
    pwsh -File scripts/run-livecheck.ps1                       # Release 构建 + 启动 + DPI 自检
    pwsh -File scripts/run-livecheck.ps1 -Configuration Debug
    pwsh -File scripts/run-livecheck.ps1 -NoLaunch              # 只构建 + 备份，不启动
    pwsh -File scripts/run-livecheck.ps1 -NoBuild               # 不构建，直接用现有产物
    pwsh -File scripts/run-livecheck.ps1 -NoBackup              # 不备份 settings.json
    pwsh -File scripts/run-livecheck.ps1 -StopExisting          # 构建前先结束同目录的旧实例
    pwsh -File scripts/run-livecheck.ps1 -Dotnet 'D:\sdk\dotnet.exe'

  注意：
    * **推荐用 -ExecutionPolicy Bypass**：若脚本文件带 Zone.Identifier（从网上下载/复制来的），
      PowerShell 的执行策略会直接拒绝运行，报 "无法加载文件 ... 因为在此系统上禁止运行脚本"。
      这与脚本本身无关，加 Bypass 即可。-NoProfile 则是为了避免你的 profile 干扰。
    * app.manifest 声明了 requireAdministrator，启动必然弹 UAC。
      想免弹窗，请用「以管理员身份运行」的 PowerShell 执行本脚本。
    * **强烈建议用管理员 PowerShell 跑**：LiteMonitor++ 自己也跑在高完整性级别上，
      非提权的 PowerShell 读不到它的 exe 路径、也结束不了它，
      遇到"文件被占用"时 -StopExisting 会失效（脚本会给出提示）。
    * 单实例互斥体按 exe 所在目录生成（Program.cs 里按 MainModule 路径取哈希），
      所以同目录重复启动时第二个会立刻退出 —— 脚本会先提示。
    * **配置在哪**：settings.json 放在 **exe 同目录**（SettingsHelper 用
      AppContext.BaseDirectory），**不是** %APPDATA%\LiteMonitor\。
      本脚本备份的正是输出目录里的那一份。
    * 若上一轮实例还在跑，exe/dll 被占用会导致构建失败 —— 用 -StopExisting 先结束它。
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Dotnet,

    [switch]$NoLaunch,

    [switch]$NoBuild,

    [switch]$NoBackup,

    [switch]$NoDpiCheck,

    [switch]$StopExisting
)

# ----------------------------------------------------------------------
# PowerShell 7 引导：本项目统一用 pwsh 7；若被 5.1 启动，自动改用 pwsh 重跑
# ----------------------------------------------------------------------

if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($pwsh) {
        Write-Host "当前是 PowerShell $($PSVersionTable.PSVersion)，改用 pwsh 7 重新执行..." -ForegroundColor Yellow
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
        foreach ($k in $PSBoundParameters.Keys) {
            if ($k -eq 'Configuration') { $argList += @('-Configuration', $Configuration) }
            elseif ($k -eq 'Dotnet') { $argList += @('-Dotnet', $Dotnet) }
            elseif ($PSBoundParameters[$k] -is [switch] -and $PSBoundParameters[$k].IsPresent) { $argList += "-$k" }
        }
        & $pwsh.Source @argList
        exit $LASTEXITCODE
    }
    Write-Host '需要 PowerShell 7（pwsh.exe）。本机 PATH 上找不到 pwsh，请用 pwsh 7 执行本脚本。' -ForegroundColor Red
    exit 2
}

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

# ----------------------------------------------------------------------
# Win32 互操作：只为读主窗口的 DPI
#   ★ 类型必须是 public —— Add-Type 生成的 internal 类型在 PowerShell 里访问不到
#     （会报「找不到类型 [LiveCheck.Native+RECT]」）
# ----------------------------------------------------------------------

function Initialize-DpiInterop {
    if ('LiveCheck.Native' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveCheck
{
    public static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>找出属于指定进程、可见、且**客户区面积最大**的顶层窗口。</summary>
        /// <remarks>
        /// ★ 为什么不能靠标题找：
        ///   1. LiteMonitor++ 的主窗体是 <c>FormBorderStyle.None</c> + <c>ShowInTaskbar=false</c>
        ///      的分层透明窗口，而且项目里**没有 MainForm.Designer.cs** ——
        ///      <see cref="System.Windows.Forms.Form.Text"/> 保持 WinForms 默认的**空字符串**，
        ///      主窗体根本没有标题；
        ///   2. 进程跑在**高完整性级别**（manifest 要求管理员），非提权的 PowerShell
        ///      连别的进程的窗口标题都读不到（会返回空串）。
        ///   ⇒ 只要按"有标题"筛，就永远找不到主窗口，DPI 自检必然失败。
        /// <para>
        /// 改为按**客户区面积**取最大者：主面板是 480×2660 物理像素，
        /// 远大于任何辅助窗口；不可见的辅助窗口会被 <c>IsWindowVisible</c> 直接滤掉。
        /// </para>
        /// </remarks>
        public static IntPtr FindVisibleWindow(uint targetProcessId)
        {
            IntPtr found = IntPtr.Zero;
            long bestArea = -1;
            EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != targetProcessId) return true;
                if (!IsWindowVisible(hwnd)) return true;

                long area = 0;
                RECT rect;
                if (GetClientRect(hwnd, out rect))
                {
                    long w = rect.Right - rect.Left;
                    long h = rect.Bottom - rect.Top;
                    area = w * h;
                }

                if (area > bestArea)
                {
                    bestArea = area;
                    found = hwnd;
                }
                return true;    // 枚举全部，取面积最大
            }, IntPtr.Zero);
            return found;
        }
    }
}
'@
}

$script:LiveCheckNativeReady = $false

function Get-AppWindowHandle([int]$processId, [int]$timeoutSec = 40) {
    if (-not $script:LiveCheckNativeReady) {
        Initialize-DpiInterop
        $script:LiveCheckNativeReady = $true
    }
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $h = [LiveCheck.Native]::FindVisibleWindow([uint32]$processId)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 400
    }
    return [IntPtr]::Zero
}

function Get-WindowTitle([IntPtr]$hwnd) {
    try {
        $sb = New-Object System.Text.StringBuilder 256
        [void][LiveCheck.Native]::GetWindowTextW($hwnd, $sb, $sb.Capacity)
        return $sb.ToString()
    } catch {
        return ''
    }
}

# ----------------------------------------------------------------------
# 主流程
# ----------------------------------------------------------------------

Write-Host "==> PowerShell : $($PSVersionTable.PSVersion)" -ForegroundColor Cyan
Write-Host "==> 配置      : $Configuration" -ForegroundColor Cyan

$dotnetExe = Resolve-Dotnet $Dotnet
Write-Host "==> dotnet    : $dotnetExe" -ForegroundColor Cyan

$csproj = Join-Path $repoRoot 'LiteMonitor.csproj'
$outDir = Join-Path $repoRoot "bin\$Configuration\net8.0-windows"
$exePath = Join-Path $outDir 'LiteMonitor++.exe'

# ---- 0. 旧实例检测（exe 被占用会让构建失败） ----
# ★ 为什么要分两段：LiteMonitor++ 的 app.manifest 要求管理员权限，它跑在高完整性级别上。
#   **非提权**的 PowerShell 读高完整性进程的 Process.Path 会拿到 null，
#   于是按目录过滤时会把"其实在跑"判成"没在跑" → 构建失败，而加 -StopExisting 也同样无效
#   （Stop-Process 结束不了高完整性进程）。所以必须把"路径拿不到"的同名进程单独列出来提醒。
function Get-SameDirInstances([string]$exe) {
    $same = @()
    $unknown = @()
    try {
        foreach ($p in @(Get-Process -Name 'LiteMonitor++' -ErrorAction SilentlyContinue)) {
            $path = $null
            try { $path = $p.Path } catch { $path = $null }
            if ([string]::IsNullOrWhiteSpace($path)) {
                $unknown += $p
            } elseif ([string]::Equals($path, $exe, [System.StringComparison]::OrdinalIgnoreCase)) {
                $same += $p
            }
        }
    } catch { }
    return [pscustomobject]@{ Same = @($same); Unknown = @($unknown) }
}

$instances = Get-SameDirInstances $exePath
$running = @($instances.Same)
if ($instances.Unknown.Count -gt 0) {
    $pids = ($instances.Unknown | ForEach-Object { $_.Id }) -join ', '
    Write-Host ("==> 另有 {0} 个 LiteMonitor++ 进程在运行（PID: {1}）——当前 PowerShell 无权读取它们的路径，无法判断是否同一个输出目录。" -f $instances.Unknown.Count, $pids) -ForegroundColor Yellow
    Write-Host '    若构建报"文件被占用"，请用**管理员** PowerShell 重跑本脚本，或手动关闭这些进程后再试。' -ForegroundColor Yellow
}
if ($running.Count -gt 0) {
    Write-Host ("==> 同一输出目录已有 {0} 个实例在运行（exe/dll 被占用，构建会失败）" -f $running.Count) -ForegroundColor Yellow
    if ($StopExisting) {
        foreach ($p in $running) {
            try {
                Stop-Process -Id $p.Id -Force -ErrorAction Stop
                Write-Host ("    - 已结束 PID {0}" -f $p.Id) -ForegroundColor Yellow
            } catch {
                Write-Host ("    ! 结束 PID {0} 失败：{1}" -f $p.Id, $_.Exception.Message) -ForegroundColor Red
            }
        }
        Start-Sleep -Milliseconds 800
        $running = @()
    } else {
        Write-Host '    继续构建可能会失败；如需先结束旧实例，请加 -StopExisting 重跑。' -ForegroundColor Yellow
    }
}

# ---- 1. 构建 ----
if ($NoBuild) {
    Write-Host "`n==> 已按 -NoBuild 跳过构建" -ForegroundColor Cyan
} else {
    Write-Host "`n==> 构建 LiteMonitor.csproj" -ForegroundColor Cyan
    & $dotnetExe build $csproj -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host '构建失败。常见原因与对策：' -ForegroundColor Red
        Write-Host '  · 同一输出目录的 LiteMonitor++ 还在运行（exe/dll 被占用）→ 加 -StopExisting 重跑' -ForegroundColor Red
        Write-Host '  · obj/ 被其它构建占用 → 关闭其它构建窗口后重试' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "构建产物不存在: $exePath"
}

# ---- 2. 平铺运行时资源（与 scripts/run-dev.ps1、pack-release.ps1 保持一致） ----
$resDir = Join-Path $outDir 'resources'
if (-not (Test-Path -LiteralPath $resDir)) {
    New-Item -ItemType Directory -Force -Path $resDir | Out-Null
}
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

# ---- 3. 备份当前配置（settings.json 在 exe 同目录） ----
if (-not $NoBackup) {
    $cfgPath = Join-Path $outDir 'settings.json'
    if (Test-Path -LiteralPath $cfgPath) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backup = Join-Path $outDir "settings.json.bak-$stamp"
        Copy-Item -LiteralPath $cfgPath -Destination $backup -Force
        Write-Host "`n==> 已备份配置: $backup" -ForegroundColor Cyan
    } else {
        Write-Host "`n==> 输出目录里没有 settings.json（首次运行），无需备份" -ForegroundColor Cyan
    }
}

Write-Host "`n==> 输出目录: $outDir" -ForegroundColor Cyan

if ($NoLaunch) {
    Write-Host '已按 -NoLaunch 跳过启动。' -ForegroundColor Green
    exit 0
}

# ---- 4. 启动 ----
if ($running.Count -gt 0) {
    Write-Host '注意：该输出目录已有 LiteMonitor++ 在运行，新实例会立刻退出。' -ForegroundColor Yellow
}

Write-Host '启动中（app.manifest 要求管理员权限，会弹 UAC，请点"是"）...'
try {
    $proc = Start-Process -FilePath $exePath -WorkingDirectory $outDir -PassThru
} catch {
    Write-Host ("启动失败：{0}" -f $_.Exception.Message) -ForegroundColor Red
    Write-Host '如果 UAC 提示被取消，程序不会启动（app.manifest 要求管理员权限）。' -ForegroundColor Yellow
    exit 1
}

# ---- 5. DPI 生效自检 + 打印验收清单 ----
if (-not $NoDpiCheck) {
    Write-Host "`n==> 等待主窗口并检查 DPI 感知..." -ForegroundColor Cyan
    try {
        $hwnd = Get-AppWindowHandle $proc.Id
        if ($hwnd -eq [IntPtr]::Zero) {
            Write-Host '    ? 40 秒内没找到可见主窗口（程序可能没起来，或被单实例挡掉了）。' -ForegroundColor Yellow
            Write-Host '      排查：① UAC 提示是否被取消 ② 同一输出目录是否已有实例在跑（加 -StopExisting）' -ForegroundColor Yellow
        } else {
            $dpi = [LiveCheck.Native]::GetDpiForWindow($hwnd)
            $rect = New-Object LiveCheck.Native+RECT
            [void][LiveCheck.Native]::GetClientRect($hwnd, [ref]$rect)
            $cw = $rect.Right - $rect.Left
            $ch = $rect.Bottom - $rect.Top
            $title = Get-WindowTitle $hwnd
            Write-Host ("    窗口句柄 = {0}   标题 = {1}" -f $hwnd, $(if ([string]::IsNullOrEmpty($title)) { '(空 —— 主面板本来就是无标题窗口，属正常)' } else { $title })) -ForegroundColor DarkGray
            Write-Host ("    主窗口 DPI = {0}   客户区 = {1}x{2} 物理像素" -f $dpi, $cw, $ch)
            if ($dpi -gt 96) {
                Write-Host '    ✔ Per-Monitor 感知已生效（窗口所在屏 DPI > 96；系统感知下只会报 96）' -ForegroundColor Green
            } elseif ($dpi -eq 96) {
                Write-Host '    ✘ 仍是系统感知（DPI=96）：Per-Monitor-V2 未生效，抓屏会错位，玻璃不可用。' -ForegroundColor Red
            } else {
                Write-Host '    ? 拿到非预期的 DPI 值，请人工确认。' -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host ("    ? DPI 自检失败（不影响程序运行）: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    }
}

Write-Host @'

==> 实机验收清单（逐项给"通过/不通过"，不通过附截图）
    1. 旧主题（DarkFlat_Classic）仍是圆角、无描边/辉光/扫描线/网格、纯色背景、纯色进度条
    2. CyberPunk_Mech 六项全在：切角外框 / 描边 / 辉光 / 扫描线 / 网格 / 渐变背景
    3. 轮廓往返：圆角 ↔ 切角 切换干净，不残留
    4. 跨屏 DPI 往返：DISPLAY1(168 DPI) ↔ DISPLAY2(120 DPI)，装饰按比例变化、文字不糊、不溢出
       （★ 本次改了 DPI 感知，这一项的含义从"被系统拉伸"变成"按目标屏 DPI 重新渲染"）
    5. 句柄重建（最小化/恢复、锁屏解锁、换显示器）后窗口形状不丢
    6. 连续换肤 100 次：任务管理器里内存进入平台期，不持续增长
    7. 亮色背景下文字/告警数值可读，扫描线与辉光没糊住数字
    8. 当前不应出现任何实时玻璃效果（M2 未接入主面板）

    完整清单：docs/superpowers/plans/2026-10-20-appearance-a14-live-checklist.md
'@ -ForegroundColor Cyan

Write-Host '已启动。关闭主界面即可退出。' -ForegroundColor Green
