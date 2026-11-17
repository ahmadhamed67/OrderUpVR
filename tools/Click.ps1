# Dev helper: optionally activates a process window, clicks at screen fractions, then screenshots.
# Usage: pwsh -File Click.ps1 [-Process MetaXRSimulator] [-At '0.38,0.83'] [-Shot out.png] [-WaitMs 1500]
param([string]$Process = '', [string]$At = '', [string]$Shot = '', [int]$WaitMs = 1500, [int]$ShotWidth = 960)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class OuvrClick {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
'@
[void][OuvrClick]::SetProcessDPIAware()
$sw = [OuvrClick]::GetSystemMetrics(0); $sh = [OuvrClick]::GetSystemMetrics(1)
if ($Process) {
    $p = Get-Process $Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { [void](New-Object -ComObject WScript.Shell).AppActivate($p.Id); Start-Sleep -Milliseconds 800 }
}
if ($At) {
    $x, $y = $At.Split(',') | ForEach-Object { [double]$_ }
    [void][OuvrClick]::SetCursorPos([int]($x * $sw), [int]($y * $sh)); Start-Sleep -Milliseconds 250
    [OuvrClick]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [OuvrClick]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
Start-Sleep -Milliseconds $WaitMs
if ($Shot) {
    $bmp = New-Object System.Drawing.Bitmap $sw, $sh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size $sw, $sh))
    (New-Object System.Drawing.Bitmap $bmp, $ShotWidth, ([int]($ShotWidth * $sh / $sw))).Save($Shot)
    $g.Dispose(); $bmp.Dispose()
}
