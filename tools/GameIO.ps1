# Test helper: screenshot the screen and send held key presses to the RPCS3 game window.
# Usage:  pwsh -File GameIO.ps1 -Keys 'X:150;WAIT:1000;D:800' -Shot out.png   (items separated by ';')
# Each key item is NAME:milliseconds (key held that long). WAIT:ms pauses. Names: A-Z, ENTER, SPACE, UP/DOWN/LEFT/RIGHT.
# Mouse: MOVE:x,y (screen fractions 0-1), GLIDE:x,y,ms (smooth move from current target), LMB/RMB/MMB/X1/X2:ms (button held),
# LDOWN:0 / LUP:0 (hold left button across other items). KD:key / KU:key press and release a key separately.
param([string]$Keys = '', [string]$Shot = '', [int]$ShotWidth = 960, [string]$PtrFile = 'C:/ClaudeProject/OrderUpVR/testinstall/log/ouvr_pointer.txt')
$KeyList = @($Keys.Split(';') | Where-Object { $_ })

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class OuvrIO {
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    public static void Key(ushort vk, bool up) {
        var i = new INPUT[1]; i[0].type = 1; i[0].ki.wVk = vk; i[0].ki.wScan = (ushort)MapVirtualKey(vk, 0);
        i[0].ki.dwFlags = up ? 2u : 0u;
        if (vk >= 0x21 && vk <= 0x28) i[0].ki.dwFlags |= 1u; // extended (arrows, home/end)
        SendInput(1, i, 40);
    }
}
'@

$vkMap = @{ ENTER = 0x0D; SPACE = 0x20; LEFT = 0x25; UP = 0x26; RIGHT = 0x27; DOWN = 0x28; ESC = 0x1B }

function WritePtr($x, $y, $m) {
    $text = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0000} {1:0.0000} {2}', $x, $y, $m)
    for ($try = 0; $try -lt 20; $try++) { try { [IO.File]::WriteAllText($PtrFile, $text); return } catch { Start-Sleep -Milliseconds 3 } }
}

if ($KeyList.Count) {
    $p = Get-Process rpcs3 -ErrorAction Stop | Where-Object { $_.MainWindowTitle -like 'FPS:*' } | Select-Object -First 1
    if (-not $p) { throw 'RPCS3 game window not found' }
    if ([OuvrIO]::GetForegroundWindow() -ne $p.MainWindowHandle) {
        (New-Object -ComObject WScript.Shell).SendKeys('%')  # lets SetForegroundWindow succeed
        [void][OuvrIO]::SetForegroundWindow($p.MainWindowHandle)
        Start-Sleep -Milliseconds 400
    }
    [void][OuvrIO]::SetProcessDPIAware()
    $sw = [OuvrIO]::GetSystemMetrics(0); $sh = [OuvrIO]::GetSystemMetrics(1)
    $mb = @{ LMB = @(2, 4, 0); RMB = @(8, 16, 0); MMB = @(32, 64, 0); X1 = @(128, 256, 1); X2 = @(128, 256, 2) }
    $cx = 0.5; $cy = 0.5
    $px = 0.5; $py = 0.5; $pm = 0
    if (Test-Path $PtrFile) { $t = (Get-Content $PtrFile -Raw).Trim().Split(' '); if ($t.Count -ge 3) { $px = [double]$t[0]; $py = [double]$t[1]; $pm = [int]$t[2] } }
    foreach ($k in $KeyList) {
        $name, $arg = $k.Split(':')
        if ($name -eq 'MOVE') { $cx, $cy = $arg.Split(',') | ForEach-Object { [double]$_ }; [void][OuvrIO]::SetCursorPos([int]($cx * $sw), [int]($cy * $sh)); Start-Sleep -Milliseconds 80; continue }
        if ($name -eq 'GLIDE') {
            $tx, $ty, $gms = $arg.Split(',') | ForEach-Object { [double]$_ }
            $steps = [math]::Max(2, [int]($gms / 16))
            for ($i = 1; $i -le $steps; $i++) { [void][OuvrIO]::SetCursorPos([int](($cx + ($tx - $cx) * $i / $steps) * $sw), [int](($cy + ($ty - $cy) * $i / $steps) * $sh)); Start-Sleep -Milliseconds 16 }
            $cx = $tx; $cy = $ty; continue
        }
        if ($name -eq 'PTR' -or $name -eq 'PGLIDE' -or $name -eq 'BTN') {
            # Fake VR pointer file (see ORDERUP_VR_FAKE_FILE): PTR:x,y  PGLIDE:x,y,ms  BTN:mask
            if ($name -eq 'BTN') { $pm = [int]$arg; WritePtr $px $py $pm; Start-Sleep -Milliseconds 30; continue }
            $v = $arg.Split(',') | ForEach-Object { [double]$_ }
            if ($name -eq 'PTR') { $px = $v[0]; $py = $v[1]; WritePtr $px $py $pm; Start-Sleep -Milliseconds 30; continue }
            $steps = [math]::Max(2, [int]($v[2] / 16)); $x0 = $px; $y0 = $py
            for ($i = 1; $i -le $steps; $i++) { $px = $x0 + ($v[0] - $x0) * $i / $steps; $py = $y0 + ($v[1] - $y0) * $i / $steps; WritePtr $px $py $pm; Start-Sleep -Milliseconds 16 }
            continue
        }
        if ($name -eq 'LDOWN') { [OuvrIO]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); continue }
        if ($name -eq 'LUP') { [OuvrIO]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); continue }
        if ($mb.ContainsKey($name)) { $f = $mb[$name]; [OuvrIO]::mouse_event($f[0], 0, 0, $f[2], [UIntPtr]::Zero); Start-Sleep -Milliseconds ([int]$arg); [OuvrIO]::mouse_event($f[1], 0, 0, $f[2], [UIntPtr]::Zero); Start-Sleep -Milliseconds 120; continue }
        if ($name -eq 'KD' -or $name -eq 'KU') { $v = if ($vkMap.ContainsKey($arg)) { $vkMap[$arg] } else { [int][char]$arg.ToUpper() }; [OuvrIO]::Key([uint16]$v, ($name -eq 'KU')); Start-Sleep -Milliseconds 60; continue }
        $ms = [int]$arg
        if ($name -eq 'WAIT') { Start-Sleep -Milliseconds $ms; continue }
        $vk = if ($vkMap.ContainsKey($name)) { $vkMap[$name] } else { [int][char]$name.ToUpper() }
        [OuvrIO]::Key([uint16]$vk, $false); Start-Sleep -Milliseconds $ms; [OuvrIO]::Key([uint16]$vk, $true)
        Start-Sleep -Milliseconds 120
    }
}

if ($Shot) {
    [void][OuvrIO]::SetProcessDPIAware()
    $b = New-Object System.Drawing.Rectangle 0, 0, ([OuvrIO]::GetSystemMetrics(0)), ([OuvrIO]::GetSystemMetrics(1))
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $h = [int]($ShotWidth * $b.Height / $b.Width)
    (New-Object System.Drawing.Bitmap $bmp, $ShotWidth, $h).Save($Shot)
    $g.Dispose(); $bmp.Dispose()
}
