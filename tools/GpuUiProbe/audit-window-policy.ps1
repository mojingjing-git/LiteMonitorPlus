#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LiteMonitorWindowPolicyAudit
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(
        EnumWindowsProc callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr64(
        IntPtr window,
        int index);
}
'@

$deadline = (Get-Date).AddSeconds(8)
$windows = @()
do {
    $collected = [System.Collections.ArrayList]::new()
    [LiteMonitorWindowPolicyAudit]::EnumWindows(
        {
            param($window, $parameter)
            [uint32]$processId = 0
            [void][LiteMonitorWindowPolicyAudit]::GetWindowThreadProcessId(
                $window,
                [ref]$processId)
            if ($processId -eq $TargetProcessId -and
                [LiteMonitorWindowPolicyAudit]::IsWindowVisible($window)) {
                $extendedStyle =
                    [LiteMonitorWindowPolicyAudit]::GetWindowLongPtr64(
                        $window,
                        -20).ToInt64()
                [void]$collected.Add([pscustomobject]@{
                    Handle = '0x{0:X}' -f $window.ToInt64()
                    Visible = $true
                    TopMost = (($extendedStyle -band 0x8) -ne 0)
                    ExtendedStyle = $extendedStyle
                })
            }

            return $true
        },
        [IntPtr]::Zero) | Out-Null
    $windows = @($collected)
    if ($windows.Count -gt 0) {
        break
    }

    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

$foreground = [LiteMonitorWindowPolicyAudit]::GetForegroundWindow()
[uint32]$foregroundProcessId = 0
[void][LiteMonitorWindowPolicyAudit]::GetWindowThreadProcessId(
    $foreground,
    [ref]$foregroundProcessId)
$topMostWindows = @($windows | Where-Object { $_.TopMost })
$target = Get-Process -Id $TargetProcessId -ErrorAction Stop
$result = [pscustomobject]@{
    sampledAt = (Get-Date).ToString('o')
    targetProcessId = $TargetProcessId
    elapsedSeconds = [Math]::Round(
        ((Get-Date) - $target.StartTime).TotalSeconds,
        1)
    foregroundHandle = '0x{0:X}' -f $foreground.ToInt64()
    foregroundProcessId = [int]$foregroundProcessId
    targetOwnsForeground =
        [int]$foregroundProcessId -eq $TargetProcessId
    visibleWindowCount = $windows.Count
    topMostWindowCount = $topMostWindows.Count
    allVisibleWindowsNotTopMost = $topMostWindows.Count -eq 0
    visibleWindows = $windows
}

$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$temporaryPath = $fullOutputPath + '.tmp'
$result | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $temporaryPath -Encoding UTF8
Move-Item `
    -LiteralPath $temporaryPath `
    -Destination $fullOutputPath `
    -Force
$result | ConvertTo-Json -Depth 5
