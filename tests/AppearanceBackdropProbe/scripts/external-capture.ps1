<#
    A06 外部观察者：用独立进程抓取屏幕矩形并存 PNG。

    用途：
      1) 作为"旁观者"验证探针窗口背后到底呈现了什么（DWM 材质 / 自绘 / 抓屏结果）；
      2) 作为"用户录屏软件"的等价视角，判定 SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE)
         是否会让窗口从录屏/截图中消失。

    安全边界：
      - 只抓取调用方指定的矩形（试验窗口区域），不抓整块桌面；
      - 只写本地 PNG，不上传任何内容。
#>
param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$W,
    [Parameter(Mandatory = $true)][int]$H,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$DelayMs = 0
)

$ErrorActionPreference = 'Stop'

if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }

# 进程 DPI 感知，避免坐标被虚拟化（否则负坐标显示器会抓错位置）
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class A06Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
"@ -ErrorAction Stop
    [A06Native]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null   # PER_MONITOR_AWARE_V2
} catch {
    # 忽略：Win7 级别兼容路径，Win11 上正常可用
}

Add-Type -AssemblyName System.Drawing

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$bmp = [System.Drawing.Bitmap]::new($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.CopyFromScreen($X, $Y, 0, 0, [System.Drawing.Size]::new($W, $H))
} finally {
    $g.Dispose()
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "OK $Out"
