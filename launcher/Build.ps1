# Builds the single-file launcher/setup exe with the C# compiler that ships with Windows (.NET Framework 4.8, WPF).
# Nothing extra is needed on users' PCs: WPF and .NET Framework 4.8 are part of Windows 10 and 11.
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
$work = Join-Path $env:TEMP ('OrderUpVR_Launcher_build_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

try {
    # Sources with version and manifest URL filled in
    $sources = @()
    foreach ($f in Get-ChildItem $PSScriptRoot -Filter *.cs) {
        $src = (Get-Content $f.FullName -Raw -Encoding UTF8).Replace('@LAUNCHER_VERSION@', $Version).Replace('@MANIFEST_URL@', $ManifestUrl)
        $src = $src.Replace('[assembly: AssemblyVersion("1.0.0.0")]', "[assembly: AssemblyVersion(`"$Version.0`")]")
        $dst = Join-Path $work $f.Name
        Set-Content -Path $dst -Value $src -Encoding UTF8
        $sources += $dst
    }

    # App icon: drawn here, so no image files are needed in the repository
    Add-Type -AssemblyName System.Drawing
    $icoPath = Join-Path $work 'app.ico'
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $pngs = foreach ($n in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $n, $n
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([System.Drawing.Color]::Transparent)
        $r = [single]($n * 0.22); $d = $r * 2; $w = [single]($n - 1)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
        $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $n, $n), ([System.Drawing.Color]::FromArgb(255, 255, 162, 31)), ([System.Drawing.Color]::FromArgb(255, 255, 61, 110))
        $g.FillPath($brush, $path)
        $font = New-Object System.Drawing.Font 'Segoe UI', ([single]($n * 0.40)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
        $g.DrawString('VR', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ([single]($n * 0.03)), $n, $n), $fmt)
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        , $ms.ToArray()
    }
    $fs = [System.IO.File]::Create($icoPath); $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $n = $sizes[$i]; $b = if ($n -ge 256) { 0 } else { $n }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close()

    $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $wpf = Join-Path $fw 'WPF'
    $refs = @(
        "/r:$wpf\PresentationFramework.dll", "/r:$wpf\PresentationCore.dll", "/r:$wpf\WindowsBase.dll", "/r:$fw\System.Xaml.dll",
        '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll', '/r:System.Drawing.dll', '/r:System.Security.dll'
    )
    $res = foreach ($x in Get-ChildItem (Join-Path $PSScriptRoot 'ui') -Filter *.xaml) { "/resource:$($x.FullName),OrderUpVR.$($x.Name)" }
    & (Join-Path $fw 'csc.exe') /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ "/out:$Out" "/win32icon:$icoPath" @refs @res @sources
    if ($LASTEXITCODE) { throw 'Launcher build failed' }

    # Every XAML file must parse (catches markup mistakes at build time instead of on users' PCs)
    Add-Type -AssemblyName PresentationFramework
    $theme = [System.Windows.Markup.XamlReader]::Parse((Get-Content (Join-Path $PSScriptRoot 'ui\Theme.xaml') -Raw))
    Write-Host "Launcher: $Out ($([math]::Round((Get-Item $Out).Length / 1KB)) KB)"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
