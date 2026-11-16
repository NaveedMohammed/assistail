# Copies rendered lion animations (design\anim) into assets\lion for embedding in the app,
# and merges the per-state manifests into assets\lion\lion.json.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src  = Join-Path $root 'design\anim'
$dest = Join-Path $root 'assets\lion'
$states = 'idle','hover','thinking','talking','happy','dragged','landing','sleep','turn','stand_up','walk',
          'shake_head','jump','stretch_yawn','tail_chase','wave','look_roar','look_up','swat','butterfly'

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$manifest = [ordered]@{}
foreach ($s in $states) {
    $json = Join-Path $src "$s.json"
    if (-not (Test-Path $json)) { throw "Missing animation '$s' (render it with tools\animate_lion.py)" }
    Copy-Item (Join-Path $src $s) (Join-Path $dest $s) -Recurse
    $manifest[$s] = Get-Content $json -Raw | ConvertFrom-Json
}
$manifest | ConvertTo-Json -Depth 5 -Compress | Set-Content (Join-Path $dest 'lion.json') -Encoding utf8
$size = (Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Lion assets: {0} states, {1:N1} MB -> {2}" -f $states.Count, $size, $dest)
