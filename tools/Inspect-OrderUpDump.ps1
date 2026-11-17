param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$GameFolder
)

$ErrorActionPreference = 'Stop'
$paramSfo = Join-Path $GameFolder 'PS3_GAME\PARAM.SFO'
if (-not (Test-Path -LiteralPath $paramSfo -PathType Leaf)) {
    throw "Could not find PS3_GAME\PARAM.SFO under: $GameFolder"
}

[byte[]]$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $paramSfo))
if ($bytes.Length -lt 20 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x53 -or $bytes[3] -ne 0x46) {
    throw 'PARAM.SFO has an invalid or unsupported header.'
}

$keyTable = [BitConverter]::ToUInt32($bytes, 8)
$dataTable = [BitConverter]::ToUInt32($bytes, 12)
$count = [BitConverter]::ToUInt32($bytes, 16)
$entriesStart = 20
$values = @{}

for ($i = 0; $i -lt $count; $i++) {
    $entry = $entriesStart + (16 * $i)
    if ($entry + 16 -gt $bytes.Length) { throw 'PARAM.SFO entry table is truncated.' }
    $keyOffset = [BitConverter]::ToUInt16($bytes, $entry)
    $valueLength = [BitConverter]::ToUInt32($bytes, $entry + 4)
    $valueOffset = [BitConverter]::ToUInt32($bytes, $entry + 12)
    $keyStart = [int]($keyTable + $keyOffset)
    $valueStart = [int]($dataTable + $valueOffset)
    if ($keyStart -ge $bytes.Length -or $valueStart + $valueLength -gt $bytes.Length) { throw 'PARAM.SFO contains an out-of-range field.' }

    $keyEnd = $keyStart
    while ($keyEnd -lt $bytes.Length -and $bytes[$keyEnd] -ne 0) { $keyEnd++ }
    $key = [Text.Encoding]::ASCII.GetString($bytes, $keyStart, $keyEnd - $keyStart)
    $value = [Text.Encoding]::UTF8.GetString($bytes, $valueStart, [int]$valueLength).TrimEnd([char]0)
    $values[$key] = $value
}

$titleId = $values['TITLE_ID']
$title = $values['TITLE']
$appVersion = $values['APP_VER']
$version = $values['VERSION']

if ($titleId -notin @('BLES01470', 'NPEB00949')) {
    Write-Warning "This does not look like the known European Order Up release (disc BLES01470 / digital NPEB00949)."
}

[pscustomobject]@{
    Title       = $title
    TitleId     = $titleId
    AppVersion  = $appVersion
    Version     = $version
    SfoPath     = (Resolve-Path -LiteralPath $paramSfo).Path
}
