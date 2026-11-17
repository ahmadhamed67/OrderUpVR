# Removes the RPCS3-OrderUpVR folder. Your original RPCS3, firmware, saves and game are not touched.
param([string]$Target = '')
$ErrorActionPreference = 'Stop'
if (-not $Target) {
    foreach ($guess in 'C:\Emulator\RPCS3-OrderUpVR', 'C:\RPCS3-OrderUpVR', "$env:USERPROFILE\RPCS3-OrderUpVR") { if (Test-Path (Join-Path $guess 'Play Order Up VR.bat')) { $Target = $guess; break } }
}
if (-not $Target -or -not (Test-Path (Join-Path $Target 'Play Order Up VR.bat'))) { throw 'Could not find the RPCS3-OrderUpVR folder. Run: Uninstall.ps1 -Target <folder>' }
if (Get-Process rpcs3 -ErrorAction SilentlyContinue) { throw 'RPCS3 is running. Close it first.' }

# Remove the links first so that nothing they point to can be deleted.
Get-ChildItem $Target -Directory -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object {
    cmd /c rmdir "$($_.FullName)"
}
if (Get-ChildItem $Target -Directory -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Some links could not be removed; stopping without deleting anything else.' }
Remove-Item $Target -Recurse -Force
Write-Host "Removed $Target"
