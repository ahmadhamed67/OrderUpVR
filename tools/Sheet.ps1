# Dev helper: tiles PNGs into one contact sheet. Usage: pwsh -File Sheet.ps1 -Files 'a.png;b.png' -Out sheet.png -Cols 3
param([string]$Files, [string]$Out, [int]$Cols = 3)
Add-Type -AssemblyName System.Drawing
$imgs = $Files.Split(';') | Where-Object { $_ } | ForEach-Object { [System.Drawing.Image]::FromFile($_) }
$w = $imgs[0].Width; $h = $imgs[0].Height
$rows = [math]::Ceiling($imgs.Count / $Cols)
$sheet = New-Object System.Drawing.Bitmap ($w * $Cols), ($h * $rows)
$g = [System.Drawing.Graphics]::FromImage($sheet)
for ($i = 0; $i -lt $imgs.Count; $i++) { $g.DrawImage($imgs[$i], ($i % $Cols) * $w, [math]::Floor($i / $Cols) * $h, $w, $h) }
$sheet.Save($Out); $g.Dispose(); $imgs | ForEach-Object { $_.Dispose() }
