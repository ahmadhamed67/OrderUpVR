# Builds everything a GitHub release needs into dist\release:
#   OrderUpVR-mod-<version>.zip   the mod package (custom RPCS3 build + per-game config)
#   OrderUpVR-Setup.exe           the launcher/setup exe
#   manifest.json                 what installed launchers read to find updates
# Usage: .\Release.ps1 -Version 0.1.0 [-Repo ahmadhamed67/OrderUpVR]
# The launcher has its own version (launcher\VERSION): it only changes when the launcher itself changes, so a mod
# update does not make installed launchers update themselves too.
# Channels: stable (full release, what launchers get by default; true VR shown as "Coming soon") and beta (prerelease
# tagged v<version>-beta; its manifest also goes to the rolling release tagged "beta", which beta launchers read).
param([Parameter(Mandatory = $true)][string]$Version, [ValidateSet('stable', 'beta')][string]$Channel = 'beta', [string]$LauncherVersion = '', [string]$Repo = 'ahmadhamed67/OrderUpVR')
$ErrorActionPreference = 'Stop'
if (-not $LauncherVersion) { $LauncherVersion = (Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'launcher\VERSION') -Raw).Trim() }
# Launcher code changed since the last release but its version did not: refuse (installed launchers would not update)
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $lastTag = (git tag --list "v*" --sort=-v:refname | Select-Object -First 1)
    if ($lastTag) {
        git diff --quiet $lastTag -- launcher ':!launcher/VERSION'; $codeChanged = $LASTEXITCODE -ne 0
        $oldVersion = (cmd /c "git show ${lastTag}:launcher/VERSION 2>nul") -join ''
        $versionChanged = $oldVersion.Trim() -ne $LauncherVersion
        if ($codeChanged -and -not $versionChanged) { throw "The launcher changed since ${lastTag}: raise launcher\VERSION" }
    }
} finally { Pop-Location }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "dist\release\$Channel"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$stage = Join-Path $out 'stage'
New-Item -ItemType Directory -Force (Join-Path $stage 'rpcs3'), (Join-Path $stage 'config') | Out-Null

$changelog = Get-Content (Join-Path $root 'release\changelog.json') -Raw -Encoding UTF8 | ConvertFrom-Json
# This channel's entries (entries without a channel belong to both), newest first
$changelog = @($changelog | Where-Object { -not $_.channel -or $_.channel -eq $Channel })
if ($changelog[0].version -ne $Version) { throw "release\changelog.json must have the $Channel entry for version $Version first" }
$tag = if ($Channel -eq 'stable') { "v$Version" } else { "v$Version-beta" }

# Emulator binaries only: no debug symbols, no test data, nothing from a run (config, logs, caches).
$bin = Join-Path $root 'rpcs3-src\bin'
$skip = 'test', 'config', 'log', 'cache', 'dev_hdd0', 'dev_hdd1', 'dev_flash', 'dev_usb000', 'games', 'captures', 'savestates', 'shaderlog', 'patches', 'sounds', 'ppu_progs', 'spu_progs'
Get-ChildItem $bin | Where-Object { $_.Name -notin $skip -and $_.Extension -notin '.pdb', '.exp', '.lib', '.buf', '.log' } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $stage 'rpcs3') -Recurse -Force }
# Microsoft Visual C++ runtime: RPCS3 refuses to start with app-local copies (module_verifier.cpp), so it is NOT shipped.
# Instead the manifest names the minimum runtime version and the launcher installs Microsoft's official package when needed.
$crt = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT' -Directory | Sort-Object FullName | Select-Object -Last 1
if (-not $crt) { throw 'Visual C++ redistributable files not found' }
$crtVer = (Get-Item (Join-Path $crt.FullName 'msvcp140.dll')).VersionInfo
$vcMin = '{0}.{1}.0.0' -f $crtVer.FileMajorPart, $crtVer.FileMinorPart

# Every DLL the shipped binaries import must be shipped or be part of Windows / the GPU driver.
# Developer-only files (Vulkan SDK layers, VC++ runtime, debug runtimes) must never be assumed present.
$dumpbin = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\*\bin\Hostx64\x64\dumpbin.exe' | Select-Object -Last 1
$shipped = @{}; Get-ChildItem (Join-Path $stage 'rpcs3') -Recurse -Include *.dll, *.exe | ForEach-Object { $shipped[$_.Name.ToLower()] = $true }
$fromDriver = 'vulkan-1.dll'   # installed by every Vulkan-capable GPU driver; RPCS3 itself needs it
$fromRedist = '^(msvcp140|vcruntime140)(_\w+)?\.dll$'   # installed by the launcher (Microsoft's VC++ package)
$neverSystem = '^(msvcp|vcruntime|concrt|vccorlib|ucrtbased|vcomp|mfc)\d*'
# RPCS3 aborts if these are found anywhere but the Windows folder.
foreach ($bad in 'vulkan-1.dll', 'msvcp140.dll', 'vcruntime140.dll', 'msvcp140_1.dll', 'vcruntime140_1.dll') {
    if ($shipped[$bad]) { throw "$bad must not be shipped: RPCS3 refuses to start when it is next to rpcs3.exe" }
}
$missing = @()
foreach ($f in Get-ChildItem (Join-Path $stage 'rpcs3') -Recurse -Include *.dll, *.exe) {
    foreach ($line in & $dumpbin.FullName /nologo /dependents $f.FullName) {
        if ($line -notmatch '^\s+(\S+\.dll)\s*$') { continue }
        $dll = $Matches[1].ToLower()
        if ($shipped[$dll] -or $dll -eq $fromDriver -or $dll -match $fromRedist -or $dll -like 'api-ms-win-*' -or $dll -like 'ext-ms-*') { continue }
        if ($dll -match $neverSystem -or -not (Test-Path (Join-Path $env:WINDIR "System32\$dll"))) { $missing += "$($f.Name) -> $dll" }
    }
}
if ($missing) { throw "Package would depend on files users may not have:`n" + ($missing | Sort-Object -Unique | Out-String) }
Write-Host 'Dependency check passed: everything the mod needs is shipped or part of Windows.'

Copy-Item (Join-Path $root 'package\config\*.yml') (Join-Path $stage 'config')
Copy-Item (Join-Path $root 'package\LICENSE-NOTES.txt') $stage

Set-Content (Join-Path $stage 'config\orderupvr_channel.txt') ("channel=$Channel`ntruevr=" + $(if ($Channel -eq 'beta') { '1' } else { '0' })) -Encoding ASCII -NoNewline
$zipName = if ($Channel -eq 'stable') { "OrderUpVR-mod-$Version.zip" } else { "OrderUpVR-mod-$Version-beta.zip" }
$zip = Join-Path $out $zipName
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force

$base = "https://github.com/$Repo/releases"
& (Join-Path $root 'launcher\Build.ps1') -Version $LauncherVersion -ManifestUrl "$base/latest/download/manifest.json" -Out (Join-Path $out 'OrderUpVR-Setup.exe')

$manifest = [ordered]@{
    version   = $Version
    channel   = $Channel
    url       = "$base/download/$tag/$zipName"
    sha256    = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    size      = (Get-Item $zip).Length
    launcher  = [ordered]@{
        version = $LauncherVersion
        url     = "$base/download/$tag/OrderUpVR-Setup.exe"
        sha256  = (Get-FileHash (Join-Path $out 'OrderUpVR-Setup.exe') -Algorithm SHA256).Hash.ToLower()
    }
    vcredistMin = $vcMin
    vcredistUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
    changelog = @($changelog)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $out 'manifest.json') -Encoding UTF8

$notes = "## Order Up VR $Version" + $(if ($Channel -eq 'beta') { " (beta: updated often, may break; to just play use Stable)" } else { " (stable)" }) + "`n`n" + (($changelog[0].notes | ForEach-Object { "- $_" }) -join "`n") +
    "`n`n**Install:** download `OrderUpVR-Setup.exe` below and run it. It updates itself and the mod from then on."
Set-Content (Join-Path $out 'release-notes.md') $notes -Encoding UTF8
Get-ChildItem $out | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
