# Gathers the logs from the last Order Up VR run into one zip on the Desktop, to send back for diagnosis.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) ("OrderUpVR_logs_{0:yyyyMMdd_HHmmss}.zip" -f (Get-Date))
$tmp = Join-Path $env:TEMP ("ouvr_logs_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null

foreach ($f in 'log\orderup_vr.log', 'log\RPCS3.log', 'config\custom_configs\config_BLES01470.yml') {
    $p = Join-Path $root $f
    if (Test-Path $p) { Copy-Item $p $tmp }
}
$runtime = (Get-ItemProperty 'HKLM:\SOFTWARE\Khronos\OpenXR\1' -ErrorAction SilentlyContinue).ActiveRuntime
$gpu = (Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }) -join '; '
"Active OpenXR runtime: $runtime`nGPU: $gpu`nWindows: $([Environment]::OSVersion.VersionString)" | Set-Content (Join-Path $tmp 'system.txt')

Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $out -Force
Remove-Item $tmp -Recurse -Force
Write-Host "Logs saved to: $out"
