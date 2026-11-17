# Order Up VR installer.
# Creates a separate "RPCS3-OrderUpVR" folder next to your existing RPCS3. Your original RPCS3 is not modified:
# the VR copy only links to its firmware and save folders and gets its own copy of the settings.
param([string]$Rpcs3Dir = '', [string]$GameDir = '', [string]$Target = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Ask-Folder([string]$prompt, [scriptblock]$valid) {
    Add-Type -AssemblyName System.Windows.Forms
    while ($true) {
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = $prompt
        $dlg.UseDescriptionForTitle = $true
        if ($dlg.ShowDialog() -ne 'OK') { throw 'Cancelled.' }
        if (& $valid $dlg.SelectedPath) { return $dlg.SelectedPath }
        [System.Windows.Forms.MessageBox]::Show("That folder does not look right.`n$prompt", 'Order Up VR') | Out-Null
    }
}

Write-Host '=== Order Up VR installer ===' -ForegroundColor Cyan

if (Get-Process rpcs3 -ErrorAction SilentlyContinue) { throw 'RPCS3 is running. Close it and run the installer again.' }

$isRpcs3 = { param($p) (Test-Path (Join-Path $p 'rpcs3.exe')) -and (Test-Path (Join-Path $p 'dev_flash')) }
if (-not $Rpcs3Dir) {
    foreach ($guess in 'C:\Emulator\RPCS3', 'C:\RPCS3', "$env:USERPROFILE\RPCS3") { if (& $isRpcs3 $guess) { $Rpcs3Dir = $guess; break } }
}
if (-not $Rpcs3Dir -or -not (& $isRpcs3 $Rpcs3Dir)) {
    $Rpcs3Dir = Ask-Folder 'Select your existing RPCS3 folder (the one that contains rpcs3.exe)' $isRpcs3
}
Write-Host "RPCS3 folder: $Rpcs3Dir"

$isGame = { param($p) Test-Path (Join-Path $p 'PS3_GAME\USRDIR\EBOOT.BIN') }
if (-not $GameDir) {
    $parent = Split-Path $Rpcs3Dir -Parent
    $found = Get-ChildItem -Path $parent, (Join-Path $Rpcs3Dir 'games') -Directory -Recurse -Depth 2 -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Order Up*' -and (& $isGame $_.FullName) } | Select-Object -First 1
    if ($found) { $GameDir = $found.FullName }
}
if (-not $GameDir -or -not (& $isGame $GameDir)) {
    $GameDir = Ask-Folder 'Select the Order Up!! game folder (the one that contains PS3_GAME)' $isGame
}
Write-Host "Game folder:  $GameDir"

$target = if ($Target) { $Target } else { Join-Path (Split-Path $Rpcs3Dir -Parent) 'RPCS3-OrderUpVR' }
Write-Host "Installing to: $target"
New-Item -ItemType Directory -Force $target | Out-Null

# 1. Emulator binaries
Copy-Item (Join-Path $here 'rpcs3\*') $target -Recurse -Force

# 2. Link firmware and saves from the existing install (junctions: no admin rights needed, nothing is copied)
foreach ($dir in 'dev_flash', 'dev_flash2', 'dev_flash3', 'dev_hdd0', 'dev_hdd1', 'dev_bdvd', 'dev_usb000') {
    $src = Join-Path $Rpcs3Dir $dir
    $dst = Join-Path $target $dir
    if ((Test-Path $src) -and -not (Test-Path $dst)) { New-Item -ItemType Junction -Path $dst -Target $src | Out-Null }
}

# 3. Settings: start from a copy of the existing ones, then apply what the mod needs for this game only
$cfgDst = Join-Path $target 'config'
if (-not (Test-Path $cfgDst)) { Copy-Item (Join-Path $Rpcs3Dir 'config') $cfgDst -Recurse }
$guiSrc = Join-Path $Rpcs3Dir 'GuiConfigs'
if ((Test-Path $guiSrc) -and -not (Test-Path (Join-Path $target 'GuiConfigs\CurrentSettings.ini'))) {
    Copy-Item $guiSrc (Join-Path $target 'GuiConfigs') -Recurse -Force
}

$custom = Join-Path $cfgDst 'custom_configs'
New-Item -ItemType Directory -Force $custom | Out-Null
Copy-Item (Join-Path $here 'config\config_BLES01470.yml') (Join-Path $custom 'config_BLES01470.yml') -Force
$inputDir = Join-Path $cfgDst 'input_configs\BLES01470'
New-Item -ItemType Directory -Force $inputDir | Out-Null
Copy-Item (Join-Path $here 'config\input_BLES01470.yml') (Join-Path $inputDir 'Default.yml') -Force

# 4. Launcher
$eboot = Join-Path $GameDir 'PS3_GAME\USRDIR\EBOOT.BIN'
$bat = @"
@echo off
rem Starts Order Up!! in VR. Put the headset on and start Quest Link / Air Link / Virtual Desktop / SteamVR first.
cd /d "%~dp0"
start "" "%~dp0rpcs3.exe" --no-gui "$eboot"
"@
Set-Content -Path (Join-Path $target 'Play Order Up VR.bat') -Value $bat -Encoding ASCII
Copy-Item (Join-Path $here 'Collect-Logs.ps1') $target -Force
Copy-Item (Join-Path $here 'README.txt') $target -Force

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "To play: connect the headset to this PC, then double-click:`n  $target\Play Order Up VR.bat"
Write-Host 'The first start takes a few minutes while RPCS3 prepares the game.'
