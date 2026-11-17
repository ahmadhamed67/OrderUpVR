# Dev helper: start the test install with optional environment variables, get past Move calibration with the mouse,
# and stop on the main menu. Usage: pwsh -File Run-Test.ps1 -Vars 'ORDERUP_VR_PTR_MODE=1;ORDERUP_VR_CONE_H=20'
param([string]$Vars = '', [int]$BootWait = 40, [switch]$NoCalibrate, [switch]$Fake)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$inst = Join-Path $root 'testinstall'
Get-Process rpcs3 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
Copy-Item (Join-Path $root 'rpcs3-src\bin\rpcs3.exe') $inst -Force

$env:ORDERUP_VR_TRACE = '1'
$ptrFile = Join-Path $inst 'log\ouvr_pointer.txt'
if ($Fake) { New-Item -ItemType Directory -Force (Join-Path $inst 'log') | Out-Null; Set-Content $ptrFile '0.5 0.5 0' -NoNewline; $env:ORDERUP_VR_FAKE_FILE = $ptrFile } else { Remove-Item env:ORDERUP_VR_FAKE_FILE -ErrorAction SilentlyContinue }
foreach ($kv in $Vars.Split(';') | Where-Object { $_ }) { $k, $v = $kv.Split('='); Set-Item "env:$k" $v }

Start-Process (Join-Path $inst 'rpcs3.exe') -ArgumentList '--no-gui', '"C:\Emulator\Games\Order Up!! (Europe) (En,Fr,De,Es,It)\PS3_GAME\USRDIR\EBOOT.BIN"' -WorkingDirectory $inst
$deadline = (Get-Date).AddSeconds(240)
while ((Get-Date) -lt $deadline) { if (Get-Process rpcs3 -EA SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'FPS:*' }) { break }; Start-Sleep 2 }
Start-Sleep $BootWait
if ($Fake -and -not $NoCalibrate) {
    # Square (16) calibrates, Move (2) continues
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'GameIO.ps1') -Keys 'PTR:0.5,0.5;WAIT:500;BTN:16;WAIT:300;BTN:0;WAIT:4000;BTN:2;WAIT:300;BTN:0;WAIT:4000;BTN:2;WAIT:300;BTN:0;WAIT:5000'
}
elseif (-not $NoCalibrate) {
    # Square (middle button) calibrates, Move (right button) continues twice -> main menu
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'GameIO.ps1') -Keys 'MOVE:0.45,0.5;GLIDE:0.5,0.5,300;WAIT:800;MMB:300;WAIT:4000;RMB:300;WAIT:4000;RMB:300;WAIT:5000'
}
'ready'
