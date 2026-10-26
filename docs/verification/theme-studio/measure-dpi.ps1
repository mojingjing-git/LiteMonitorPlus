Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class DPI {
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint f);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int t, out uint x, out uint y);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct POINT { public int x; public int y; }
}
"@
[DPI]::SetProcessDPIAware() | Out-Null
$p1 = New-Object DPI+POINT; $p1.x = 1000; $p1.y = 1000
$p2 = New-Object DPI+POINT; $p2.x = -1000; $p2.y = 1500
$m1 = [DPI]::MonitorFromPoint($p1, 2)
$m2 = [DPI]::MonitorFromPoint($p2, 2)
$x = [uint32]0; $y = [uint32]0
[DPI]::GetDpiForMonitor($m1, 0, [ref]$x, [ref]$y) | Out-Null; "PRIMARY effective DPI: $x"
[DPI]::GetDpiForMonitor($m2, 0, [ref]$x, [ref]$y) | Out-Null; "SECONDARY effective DPI: $x"
"Panel window DPI (GetDpiForWindow): " + [DPI]::GetDpiForWindow([IntPtr]3025132)
