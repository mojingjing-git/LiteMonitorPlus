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
    5. 启动后自动取主窗口的 DPI **感知上下文**（GetWindowDpiAwarenessContext +
       AreDpiAwarenessContextsEqual），判定 Per-Monitor-V2 是否真的生效，
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
      ★ 同理：非提权会话**跨进程读它的窗口标题**也可能被拦掉（读到空串），
        此时按标题精确识别主窗体会失败，脚本会退回到"面积最大"并明确警告。
    * **主窗体识别约定**：主工程把主窗体标题固定成精确的 "LiteMonitor++"
      （src/UI/MainForm_Transparent.cs 里设置，无边框窗体不影响外观），
      而驱动安装对话框的标题是 "LiteMonitor++ - {说明}"。
      脚本按**精确相等**匹配，不会把启动阶段的驱动安装对话框误认成主窗体。
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
    /// <summary>
    /// DPI 感知等级 —— 由**感知上下文**归类得出。
    /// ★ 它与 GetDpiForWindow 返回的**缩放数值**是两回事，切勿混用。
    /// </summary>
    public enum DpiAwarenessKind
    {
        /// <summary>取不到感知上下文（API 缺失 / 返回 NULL / 未知取值）—— 无法判定，不猜。</summary>
        Unknown = 0,
        /// <summary>DPI-Unaware：玻璃不可用。</summary>
        Unaware = 1,
        /// <summary>System-Aware：玻璃不可用。</summary>
        SystemAware = 2,
        /// <summary>Per-Monitor-V1：可以抓屏，但不是 v2。</summary>
        PerMonitorAwareV1 = 3,
        /// <summary>Per-Monitor-V2：期望结果，玻璃抓屏可用。</summary>
        PerMonitorAwareV2 = 4
    }

    /// <summary>DPI 感知上下文的查询结果。</summary>
    public class DpiAwarenessInfo
    {
        /// <summary>GetWindowDpiAwarenessContext 返回的原始上下文（0 = 没取到）。</summary>
        public IntPtr Context = IntPtr.Zero;
        /// <summary>归类出来的感知等级。</summary>
        public DpiAwarenessKind Kind = DpiAwarenessKind.Unknown;
        /// <summary>感知等级的简短名称，仅用于打印。</summary>
        public string KindName = "未知";
        /// <summary>无法判定时的原因（可为空）。</summary>
        public string Detail = "";
        /// <summary>缩放数值（GetDpiForWindow）—— 只是参考信息，不是感知等级。</summary>
        public uint Dpi = 0;
        /// <summary>缩放数值的读取错误（可为空）。</summary>
        public string DpiError = "";
    }

    /// <summary>窗口查找结果。</summary>
    public class WindowLookup
    {
        /// <summary>窗口句柄（IntPtr.Zero = 没找到）。</summary>
        public IntPtr Handle = IntPtr.Zero;
        /// <summary>窗口标题（跨进程读不到时是空串）。</summary>
        public string Title = "";
        /// <summary>标题是否**精确等于**期望的主窗体标题。</summary>
        public bool TitleMatched = false;
        /// <summary>客户区面积（物理像素²）。</summary>
        public long ClientArea = 0;
        /// <summary>客户区宽（物理像素）。</summary>
        public int ClientWidth = 0;
        /// <summary>客户区高（物理像素）。</summary>
        public int ClientHeight = 0;
    }

    public static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        // ★ DPI_AWARENESS_CONTEXT 的四个常量（微软 API 定义的固定值）。
        //   官方要求用 AreDpiAwarenessContextsEqual 比较两个上下文是否等价，
        //   **不要**直接拿上下文位值去比。
        public static readonly IntPtr DpiAwarenessContextUnaware = new IntPtr(-1);
        public static readonly IntPtr DpiAwarenessContextSystemAware = new IntPtr(-2);
        public static readonly IntPtr DpiAwarenessContextPerMonitorAware = new IntPtr(-3);
        public static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        // ★ 取窗口的 DPI **感知上下文**（Win10 1607+）。返回的不是缩放数值。
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

        // ★ 比较两个感知上下文是否等价（Win10 1703+）。官方推荐的比较方式。
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AreDpiAwarenessContextsEqual(IntPtr dpiAwarenessContext1, IntPtr dpiAwarenessContext2);

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

        /// <summary>读窗口标题（读不到时返回空串）。</summary>
        public static string ReadWindowTitle(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(512);
                int n = GetWindowTextW(hwnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 找出目标进程的主窗体：**优先**取标题**精确等于** expectedTitle 的可见顶层窗口；
        /// 没有精确匹配时回退到客户区面积最大的可见顶层窗口（TitleMatched=false 表示走了回退）。
        /// </summary>
        /// <remarks>
        /// ★ 为什么必须用标题精确匹配：
        ///   启动顺序是 ApplicationConfiguration.Initialize() → DriverInstaller.CheckPawnIOBeforeHardware()
        ///   （内部用模态 UpdateDialog **阻塞**）→ Application.Run(new MainForm())。
        ///   PawnIO 缺失/升级时，驱动安装对话框**与主窗体同属一个进程**，
        ///   只按"面积最大"会把那个对话框当成主窗体，报出它的 DPI 与客户区。
        ///   主工程已把主窗体标题固定成精确的 "LiteMonitor++"（见 MainForm_Transparent.cs），
        ///   而 UpdateDialog 的标题是 "LiteMonitor++ - {说明}"，**精确比较**不会混淆。
        ///   ★ 比较必须是**精确相等**（Ordinal），不能用 StartsWith/Contains ——
        ///     否则 "LiteMonitor++ - 安装驱动" 会被误判成主窗。
        /// <para>
        /// 回退路径的取舍：主窗体是无边框窗体（FormBorderStyle.None），
        /// 而进程跑在**高完整性级别**（manifest 要求管理员），
        /// 非提权的 PowerShell 跨进程读它的标题可能被拦掉（返回空串）——
        /// 此时标题精确匹配注定失败，只能走面积最大回退，脚本会明确给出警告。
        /// </para>
        /// </remarks>
        public static WindowLookup FindMainWindow(uint targetProcessId, string expectedTitle)
        {
            WindowLookup exact = null;
            WindowLookup best = null;
            EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != targetProcessId) return true;
                if (!IsWindowVisible(hwnd)) return true;

                long area = 0;
                int cw = 0;
                int ch = 0;
                RECT rect;
                if (GetClientRect(hwnd, out rect))
                {
                    cw = rect.Right - rect.Left;
                    ch = rect.Bottom - rect.Top;
                    area = (long)cw * ch;
                }

                WindowLookup cand = new WindowLookup();
                cand.Handle = hwnd;
                cand.Title = ReadWindowTitle(hwnd);
                cand.ClientArea = area;
                cand.ClientWidth = cw;
                cand.ClientHeight = ch;

                // 精确相等（Ordinal）才算主窗体命中
                if (!string.IsNullOrEmpty(expectedTitle) && string.Equals(cand.Title, expectedTitle, StringComparison.Ordinal))
                {
                    cand.TitleMatched = true;
                    exact = cand;
                    return false;   // 停止枚举：主窗体已确定
                }

                if (best == null || area > best.ClientArea) best = cand;
                return true;
            }, IntPtr.Zero);

            if (exact != null) return exact;
            return best;   // 回退：面积最大（调用方必须据此发出警告）
        }

        /// <summary>
        /// 查询窗口的 DPI 感知上下文并归类。
        /// ★ 判定只看**感知上下文**；GetDpiForWindow 的缩放数值只作参考，不参与判定。
        /// </summary>
        public static DpiAwarenessInfo QueryDpiAwareness(IntPtr hwnd)
        {
            DpiAwarenessInfo info = new DpiAwarenessInfo();

            // 缩放数值：仍然读、仍然显示，但**只作参考**
            try
            {
                info.Dpi = GetDpiForWindow(hwnd);
            }
            catch (Exception ex)
            {
                info.DpiError = ex.Message;
            }

            try
            {
                IntPtr ctx = GetWindowDpiAwarenessContext(hwnd);
                info.Context = ctx;
                if (ctx == IntPtr.Zero)
                {
                    info.Kind = DpiAwarenessKind.Unknown;
                    info.KindName = "未知";
                    info.Detail = "GetWindowDpiAwarenessContext 返回 NULL：取不到该窗口的 DPI 感知上下文";
                    return info;
                }

                // ★ 必须用 AreDpiAwarenessContextsEqual 比较，不能直接比上下文位值
                if (AreDpiAwarenessContextsEqual(ctx, DpiAwarenessContextPerMonitorAwareV2))
                {
                    info.Kind = DpiAwarenessKind.PerMonitorAwareV2;
                    info.KindName = "Per-Monitor-V2";
                }
                else if (AreDpiAwarenessContextsEqual(ctx, DpiAwarenessContextPerMonitorAware))
                {
                    info.Kind = DpiAwarenessKind.PerMonitorAwareV1;
                    info.KindName = "Per-Monitor-V1";
                }
                else if (AreDpiAwarenessContextsEqual(ctx, DpiAwarenessContextSystemAware))
                {
                    info.Kind = DpiAwarenessKind.SystemAware;
                    info.KindName = "System-Aware";
                }
                else if (AreDpiAwarenessContextsEqual(ctx, DpiAwarenessContextUnaware))
                {
                    info.Kind = DpiAwarenessKind.Unaware;
                    info.KindName = "DPI-Unaware";
                }
                else
                {
                    info.Kind = DpiAwarenessKind.Unknown;
                    info.KindName = "未知";
                    info.Detail = "取到的感知上下文与四个已知常量都不等价";
                }
            }
            catch (EntryPointNotFoundException ex)
            {
                // 老系统（Win10 1703 之前）没有这两个 API：如实报告"无法判定"，不能让脚本报错退出
                info.Kind = DpiAwarenessKind.Unknown;
                info.KindName = "未知";
                info.Detail = "当前系统缺少 DPI 感知上下文 API（GetWindowDpiAwarenessContext / AreDpiAwarenessContextsEqual 需要 Win10 1703+）：" + ex.Message;
            }
            catch (Exception ex)
            {
                info.Kind = DpiAwarenessKind.Unknown;
                info.KindName = "未知";
                info.Detail = "读取 DPI 感知上下文失败：" + ex.Message;
            }

            return info;
        }
    }
}
'@
}

$script:LiveCheckNativeReady = $false

# ★ 主窗体的**精确**窗口标题。
#   主工程在 src/UI/MainForm_Transparent.cs 的 MainForm 构造函数里把它设成这个精确值；
#   驱动安装对话框的标题是 "LiteMonitor++ - {说明}"，两者精确比较不会混淆。
$script:LiveCheckMainWindowTitle = 'LiteMonitor++'

function Get-AppMainWindow([int]$processId, [int]$timeoutSec = 40) {
    <#
        等待主窗体出现，返回 [LiveCheck.WindowLookup]（找不到时返回 $null）。

        ★ 为什么要"先等精确标题、最后才回退面积最大"：
          启动阶段 DriverInstaller 会用模态 UpdateDialog **阻塞在** MainForm 创建之前，
          那个对话框与主窗体同属一个进程。若一发现可见窗口就返回，
          脚本会把驱动安装对话框当成主窗体，报出它的 DPI 与客户区。
          所以这里**优先等**标题精确等于 "LiteMonitor++" 的窗口；
          只有等到超时仍没出现，才回退到"面积最大"的那个，并在报告里明确警告。
    #>
    if (-not $script:LiveCheckNativeReady) {
        Initialize-DpiInterop
        $script:LiveCheckNativeReady = $true
    }
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $fallback = $null
    while ((Get-Date) -lt $deadline) {
        $r = [LiveCheck.Native]::FindMainWindow([uint32]$processId, $script:LiveCheckMainWindowTitle)
        if ($r -ne $null -and $r.Handle -ne [IntPtr]::Zero) {
            if ($r.TitleMatched) { return $r }   # 标题精确命中：确定是主窗体
            $fallback = $r                        # 回退候选：可能是驱动安装对话框，继续等
        }
        Start-Sleep -Milliseconds 400
    }
    # 最后再枚举一次，避免把已经关闭的对话框句柄报出去
    $r = [LiveCheck.Native]::FindMainWindow([uint32]$processId, $script:LiveCheckMainWindowTitle)
    if ($r -ne $null -and $r.Handle -ne [IntPtr]::Zero) {
        if ($r.TitleMatched) { return $r }
        $fallback = $r
    }
    return $fallback
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
        $win = Get-AppMainWindow $proc.Id
        if ($null -eq $win -or $win.Handle -eq [IntPtr]::Zero) {
            Write-Host '    ? 40 秒内没找到可见主窗口（程序可能没起来，或被单实例挡掉了）。' -ForegroundColor Yellow
            Write-Host '      排查：① UAC 提示是否被取消 ② 同一输出目录是否已有实例在跑（加 -StopExisting）' -ForegroundColor Yellow
        } else {
            $hwnd = $win.Handle
            $title = Get-WindowTitle $hwnd
            if ([string]::IsNullOrEmpty($title)) { $title = $win.Title }
            $rect = New-Object LiveCheck.Native+RECT
            [void][LiveCheck.Native]::GetClientRect($hwnd, [ref]$rect)
            $cw = $rect.Right - $rect.Left
            $ch = $rect.Bottom - $rect.Top

            Write-Host ("    窗口句柄 = {0}" -f $hwnd) -ForegroundColor DarkGray
            Write-Host ("    窗口标题 = {0}" -f $(if ([string]::IsNullOrEmpty($title)) { '(空 —— 跨进程读不到标题，通常是没提权)' } else { $title })) -ForegroundColor DarkGray
            Write-Host ("    客户区   = {0}x{1} 物理像素" -f $cw, $ch) -ForegroundColor DarkGray

            if (-not $win.TitleMatched) {
                Write-Host '    ⚠ 没找到标题**精确等于** "LiteMonitor++" 的窗口 —— 下面这些数据**可能不是主窗体**。' -ForegroundColor Yellow
                Write-Host '      可能是启动阶段的驱动安装/更新对话框（它与主窗体同属一个进程，且模态阻塞在主窗体创建之前）。' -ForegroundColor Yellow
                Write-Host '      若程序正在提示安装/更新驱动，说明主窗体尚未创建，请等待该对话框关闭后再读 DPI。' -ForegroundColor Yellow
            }

            # ★ 判定只看 DPI **感知上下文**；GetDpiForWindow 的缩放数值仅作参考展示。
            $info = [LiveCheck.Native]::QueryDpiAwareness($hwnd)
            Write-Host ("    缩放数值 GetDpiForWindow = {0}   ← 这是**缩放数值**，不是感知等级，仅作参考" -f $info.Dpi)
            Write-Host ("    DPI 感知上下文 = {0}   归类 = {1}" -f $info.Context, $info.KindName)

            switch ($info.Kind) {
                ([LiveCheck.DpiAwarenessKind]::PerMonitorAwareV2) {
                    Write-Host '    ✔ Per-Monitor-V2 感知已生效（玻璃抓屏可用）。' -ForegroundColor Green
                }
                ([LiveCheck.DpiAwarenessKind]::PerMonitorAwareV1) {
                    Write-Host '    △ Per-Monitor-V1 感知（不是 v2）：可抓屏，但非 v2。' -ForegroundColor Yellow
                }
                ([LiveCheck.DpiAwarenessKind]::SystemAware) {
                    Write-Host '    ✘ System-Aware 感知：Per-Monitor-V2 未生效，玻璃不可用（抓屏会错位）。' -ForegroundColor Red
                }
                ([LiveCheck.DpiAwarenessKind]::Unaware) {
                    Write-Host '    ✘ DPI-Unaware：Per-Monitor-V2 未生效，玻璃不可用（抓屏会错位）。' -ForegroundColor Red
                }
                default {
                    Write-Host '    ? 无法判定 DPI 感知等级 —— 如实报告"无法判定"，不猜。' -ForegroundColor Yellow
                    if ($info.Detail) {
                        Write-Host ("      原因：{0}" -f $info.Detail) -ForegroundColor Yellow
                    }
                }
            }
            if ($info.DpiError) {
                Write-Host ("      (缩放数值读取失败：{0})" -f $info.DpiError) -ForegroundColor Yellow
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
