# Headset simulator driver (developer testing without a headset). The mod's simulator (ORDERUP_VR_SIM) renders the
# game for a virtual head and saves each eye's picture; this script starts it and steers it.
#   Sim.ps1 start                      start the test install in simulator mode (new rpcs3.exe copied in)
#   Sim.ps1 look <yaw> <pitch> [x z]   turn the virtual head (degrees; positive yaw = left; write m30 for -30), optionally step (metres)
#   Sim.ps1 shot <name>                save <name>_eye0/eye1/flat.png and <name>_state.txt into testinstall\sim
#   Sim.ps1 flat|vr                    big screen / inside the game
#   Sim.ps1 io '<GameIO items>'        pointer and buttons (PTR:x,y;BTN:mask;WAIT:ms), mask 2=Move 4=Cross 8=Circle 16=Square 64=Start
#   Sim.ps1 stop
[CmdletBinding(PositionalBinding = $true)]
param([Parameter(Position = 0)][string]$Cmd, [Parameter(Position = 1)][string]$A = '', [Parameter(Position = 2)][string]$B = '', [Parameter(Position = 3)][string]$C = '', [Parameter(Position = 4)][string]$D = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$inst = Join-Path $root 'testinstall'
$dir = Join-Path $inst 'sim'
$ctl = Join-Path $dir 'sim.txt'
$state = Join-Path $dir 'sim.state'
New-Item -ItemType Directory -Force $dir, (Join-Path $inst 'log') | Out-Null

function Get-S { if (Test-Path $state) { (Get-Content $state -Raw).Trim().Split(' ') } else { @('0', '0', '0', '0', '1', 'none') } }
function Set-S($s) { Set-Content $state ($s -join ' ') -NoNewline; for ($i = 0; $i -lt 50; $i++) { try { [IO.File]::WriteAllText($ctl, ($s -join ' ')); return } catch { Start-Sleep -Milliseconds 20 } } }

switch ($Cmd) {
    'start' {
        Get-Process rpcs3 -ErrorAction SilentlyContinue | Stop-Process -Force
        for ($i = 0; $i -lt 30 -and (Get-Process rpcs3 -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
        Start-Sleep 1
        Copy-Item (Join-Path $root 'rpcs3-src\bin\rpcs3.exe') $inst -Force
        Set-S @('0', '0', '0', '0', '1', 'none')
        $ptr = Join-Path $inst 'log\ouvr_pointer.txt'
        Set-Content $ptr '0.5 0.5 0' -NoNewline
        $env:ORDERUP_VR_SIM = $dir
        $env:ORDERUP_VR_FAKE_FILE = $ptr
        $env:ORDERUP_VR_MODVERSION = 'sim'
        foreach ($kv in $A.Split(';') | Where-Object { $_ }) { $k, $v = $kv.Split('='); Set-Item "env:$k" $v }
        Start-Process (Join-Path $inst 'rpcs3.exe') -ArgumentList '--no-gui', '"C:\Emulator\Games\Order Up!! (Europe) (En,Fr,De,Es,It)\PS3_GAME\USRDIR\EBOOT.BIN"' -WorkingDirectory $inst -WindowStyle Minimized
        'started'
    }
    'look' { $A = $A -replace '^m', '-'; $B = $B -replace '^m', '-'; $C = $C -replace '^m', '-'; $D = $D -replace '^m', '-'; $s = Get-S; $s[0] = $A; $s[1] = $B; if ($C) { $s[2] = $C }; if ($D) { $s[3] = $D }; Set-S $s; 'ok' }
    'flat' { $s = Get-S; $s[4] = '0'; Set-S $s; 'ok' }
    'vr' { $s = Get-S; $s[4] = '1'; Set-S $s; 'ok' }
    'shot' {
        $s = Get-S; $s[5] = $A; Set-S $s
        $t0 = Get-Date
        while (((Get-Date) - $t0).TotalSeconds -lt 10 -and -not (Test-Path (Join-Path $dir "$($A)_state.txt"))) { Start-Sleep -Milliseconds 200 }
        Start-Sleep -Milliseconds 400
        Get-Content (Join-Path $dir "$($A)_state.txt") -ErrorAction SilentlyContinue
    }
    'io' { pwsh -NoProfile -File (Join-Path $PSScriptRoot 'GameIO.ps1') -Keys $A; 'ok' }
    'quick' {
        # From a fresh start: calibrate, title screen, Quick Play, then restaurant $A (1 Burger Face, 2 Gravy Chug,
        # 3 El Fuego, 4 Stuffolini's, 5 Kung Fusion, 6 Chez Haute)
        $ys = @(0, 0.12, 0.22, 0.34, 0.44, 0.55, 0.66)
        $t0 = Get-Date
        while (((Get-Date) - $t0).TotalSeconds -lt 240 -and -not (Get-Process rpcs3 -EA SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'FPS:*' })) { Start-Sleep 2 }
        Start-Sleep 50
        $io = Join-Path $PSScriptRoot 'GameIO.ps1'
        pwsh -NoProfile -File $io -Keys 'PTR:0.5,0.5;WAIT:300;BTN:16;WAIT:600;BTN:0;WAIT:3000;BTN:16;WAIT:600;BTN:0;WAIT:3000;BTN:2;WAIT:500;BTN:0;WAIT:4000;BTN:2;WAIT:500;BTN:0;WAIT:5000' | Out-Null
        pwsh -NoProfile -File $io -Keys ('PTR:0.74,0.505;WAIT:600;BTN:2;WAIT:400;BTN:0;WAIT:4000;PTR:0.71,' + $ys[[int]$A] + ';WAIT:600;BTN:2;WAIT:400;BTN:0;WAIT:2000') | Out-Null
        'restaurant chosen'
    }
    'drawlog' { Set-Content (Join-Path $dir 'drawlog') '1'; Start-Sleep 2; Get-Content (Join-Path $dir 'drawlog.txt') -ErrorAction SilentlyContinue | Measure-Object -Line }
    { $_ -in 'peek', 'find', 'poke' } {
        # peek <hexaddr> <hexlen> | find <lo> <hi> | poke <hexaddr> <float>: game memory (see the mod's simulator)
        $out = Join-Path $dir "$Cmd.txt"; Remove-Item $out -ErrorAction SilentlyContinue
        Set-Content (Join-Path $dir $Cmd) "$A $B" -NoNewline
        if ($Cmd -ne 'poke') { $t0 = Get-Date; while (((Get-Date) - $t0).TotalSeconds -lt 60 -and -not (Test-Path $out)) { Start-Sleep -Milliseconds 300 }; Start-Sleep 1; Get-Content $out } else { Start-Sleep 1; 'ok' }
    }
    'stop' { Get-Process rpcs3 -ErrorAction SilentlyContinue | Stop-Process -Force; 'stopped' }
}
