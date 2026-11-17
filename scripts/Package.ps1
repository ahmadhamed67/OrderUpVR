# Stages the mod into dist\OrderUpVR and zips it. Usage: .\Package.ps1 -Version 0.1
param([string]$Version = 'dev')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'dist\OrderUpVR'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'rpcs3') | Out-Null

$bin = Join-Path $root 'rpcs3-src\bin'
Get-ChildItem $bin | Where-Object { $_.Name -notin 'test', 'rpcs3.pdb', 'rpcs3.exp', 'rpcs3.lib', 'config', 'log', 'cache' -and $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $stage 'rpcs3') -Recurse -Force }

Copy-Item (Join-Path $root 'package\*') $stage -Recurse -Force
"Order Up VR $Version, built $(Get-Date -Format 'yyyy-MM-dd HH:mm')" | Set-Content (Join-Path $stage 'VERSION.txt')

$zip = Join-Path $root "dist\OrderUpVR_v$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
