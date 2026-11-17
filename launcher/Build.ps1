# Builds the single-file launcher/setup exe with the C# compiler that ships with Windows.
# Usage: .\Build.ps1 -Version 1.0.0 [-ManifestUrl <url or local path>] [-Out <exe path>]
param(
    [string]$Version = '1.0.0',
    [string]$ManifestUrl = 'https://github.com/ahmadhamed67/OrderUpVR/releases/latest/download/manifest.json',
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Out) { $Out = Join-Path $root 'dist\release\OrderUpVR-Setup.exe' }
New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null

$src = (Get-Content (Join-Path $PSScriptRoot 'Launcher.cs') -Raw).Replace('@LAUNCHER_VERSION@', $Version).Replace('@MANIFEST_URL@', $ManifestUrl)
$src = $src.Replace('[assembly: AssemblyVersion("1.0.0.0")]', "[assembly: AssemblyVersion(`"$Version.0`")]")
$tmp = Join-Path $env:TEMP 'OrderUpVR_Launcher_build.cs'
Set-Content -Path $tmp -Value $src -Encoding UTF8

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ "/out:$Out" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    $tmp
if ($LASTEXITCODE) { throw 'Launcher build failed' }
Remove-Item $tmp
Write-Host "Launcher: $Out ($([math]::Round((Get-Item $Out).Length / 1KB)) KB)"
