# Starts RPCS3 with the Order Up VR layer injected for this process only.
# Nothing is installed system-wide: the layer is enabled through environment
# variables that exist only for the launched RPCS3 process.
param(
    [string]$Rpcs3 = 'C:\Emulator\RPCS3\rpcs3.exe',
    [string]$Game  = 'C:\Emulator\Games\Order Up!! (Europe) (En,Fr,De,Es,It)\PS3_GAME\USRDIR\EBOOT.BIN',
    [switch]$NoGame
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin  = Join-Path $root 'bin'
if (-not (Test-Path (Join-Path $bin 'VkLayer_OrderUpVR.dll'))) { throw 'Layer not built. Run scripts\Build.ps1 first.' }

$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null

$env:VK_ADD_LAYER_PATH   = $bin
$env:VK_INSTANCE_LAYERS  = 'VK_LAYER_ORDERUP_vr'
$env:ORDERUPVR_LOG       = Join-Path $logDir ("layer_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))

$rpcsArgs = @()
if (-not $NoGame) { $rpcsArgs += "`"$Game`"" }
Write-Host "Layer log: $env:ORDERUPVR_LOG"
Start-Process -FilePath $Rpcs3 -ArgumentList $rpcsArgs -WorkingDirectory (Split-Path $Rpcs3)
