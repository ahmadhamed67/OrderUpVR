# Dev helper: measures where the game's hand pointer lands for a list of mouse positions.
# It parks the pointer in the top-left corner for a reference shot, then for each point moves the mouse, waits,
# screenshots, and finds the hand as the centroid of pixels that differ from the reference.
# Usage: pwsh -File Probe.ps1 -Points '0.5,0.5;0.55,0.5' [-WaitMs 1500] [-Jump]
# Works on static screens only (the "Press ... to begin" text area is masked out).
param([string]$Points, [int]$WaitMs = 1500, [switch]$Jump, [string]$Park = '0.02,0.02', [switch]$Fake)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$io = Join-Path $PSScriptRoot 'GameIO.ps1'
$tmp = Join-Path $env:TEMP 'ouvr_probe'
New-Item -ItemType Directory -Force $tmp | Out-Null
$W = 240

function Shot([string]$keys, [string]$name) {
    $f = Join-Path $tmp $name
    pwsh -NoProfile -File $io -Keys $keys -Shot $f -ShotWidth $W | Out-Null
    $img = [System.Drawing.Bitmap]::FromFile($f)
    $bmp = New-Object System.Drawing.Bitmap $img
    $img.Dispose()
    return $bmp
}

$mv = if ($Fake) { 'PTR' } else { 'MOVE' }; $gl = if ($Fake) { 'PGLIDE' } else { 'GLIDE' }
$ref = Shot "${mv}:0.5,0.5;${gl}:$Park,400;WAIT:1500" 'ref.png'
$H = $ref.Height
$prev = $Park
$n = 0
foreach ($pt in $Points.Split(';') | Where-Object { $_ }) {
    $keys = if ($Jump) { "${mv}:$pt;WAIT:$WaitMs" } else { "${mv}:$prev;${gl}:$pt,300;WAIT:$WaitMs" }
    $cur = Shot $keys ("p{0}.png" -f $n)
    $sx = 0.0; $sy = 0.0; $cnt = 0
    for ($y = 0; $y -lt $H; $y++) {
        for ($x = 0; $x -lt $W; $x++) {
            if ($x -lt 0.12 * $W -and $y -lt 0.16 * $H) { continue }                              # parked hand
            if ($y -gt 0.80 * $H -and $y -lt 0.92 * $H -and $x -gt 0.36 * $W -and $x -lt 0.64 * $W) { continue } # blinking text
            $a = $ref.GetPixel($x, $y); $b = $cur.GetPixel($x, $y)
            if ([math]::Abs($a.R - $b.R) + [math]::Abs($a.G - $b.G) + [math]::Abs($a.B - $b.B) -gt 90) { $sx += $x; $sy += $y; $cnt++ }
        }
    }
    if ($cnt -ge 6) { '{0} -> pointer {1:0.000},{2:0.000}  (n={3})' -f $pt, ($sx / $cnt / $W), ($sy / $cnt / $H), $cnt }
    else { '{0} -> pointer not found (n={1}; in the parked corner, off-screen or under the text)' -f $pt, $cnt }
    $cur.Dispose(); $prev = $pt; $n++
}
$ref.Dispose()
