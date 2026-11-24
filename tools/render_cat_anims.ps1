# Renders the cat's animations (tools\animate_cat.py) one state at a time into design\cat-anim\<state>\f_####.png
# + <state>.json. One at a time on purpose: parallel EEVEE renders can crash Blender (out of GPU memory).
#   -States  which states (default: all of them, read from animate_cat.py)
#   -Size    frame size in px (default 360, like the lion)
#   -Skip    skip states that are already rendered (have a .json and all their frames)
param([string[]]$States, [int]$Size = 360, [switch]$Skip)
$ErrorActionPreference = 'Stop'
$root    = Split-Path $PSScriptRoot -Parent
$out     = Join-Path $root 'design\cat-anim'
$blender = Join-Path $env:LOCALAPPDATA 'Programs\blender-4.5.16-windows-x64\blender.exe'
$script  = Join-Path $PSScriptRoot 'animate_cat.py'
$tmp     = Join-Path $env:TEMP 'assistail-cat-render'
New-Item -ItemType Directory -Force -Path $out, $tmp | Out-Null

# State names and frame counts straight from the STATES table in animate_cat.py
$table = @{}
$inTable = $false
foreach ($line in Get-Content $script) {
    if ($line -match '^STATES = \{') { $inTable = $true; continue }
    if ($inTable -and $line -match '^\}') { break }
    if ($inTable) { foreach ($m in [regex]::Matches($line, '"(\w+)":\s*\((\d+),')) { $table[$m.Groups[1].Value] = [int]$m.Groups[2].Value } }
}
if (-not $States) { $States = $table.Keys | Sort-Object }

$sw = [Diagnostics.Stopwatch]::StartNew()
foreach ($s in $States) {
    if (-not $table.ContainsKey($s)) { throw "Unknown state '$s'" }
    $dir = Join-Path $out $s
    if ($Skip -and (Test-Path (Join-Path $out "$s.json")) -and (Test-Path $dir) -and
        @(Get-ChildItem $dir -Filter 'f_*.png').Count -eq $table[$s]) { Write-Host "skip $s"; continue }
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    $args = Join-Path $tmp "$s.json"
    @{ state = $s; outdir = $out; size = $Size } | ConvertTo-Json -Compress | Set-Content $args -Encoding ascii
    $log = & $blender -b -P $script -- $args 2>&1
    $n = @(Get-ChildItem $dir -Filter 'f_*.png' -ErrorAction SilentlyContinue).Count
    if ($n -ne $table[$s]) {
        $log | Select-String 'Error|Traceback|line \d' | Select-Object -Last 8 | ForEach-Object { Write-Warning $_ }
        throw "State '$s' rendered $n of $($table[$s]) frames"
    }
    Write-Host ("{0,-16} {1,3} frames  ({2:mm\:ss})" -f $s, $n, $sw.Elapsed)
}
Write-Host "Done: $($States.Count) states in $($sw.Elapsed.ToString('mm\:ss')) -> $out"
