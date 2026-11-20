# B5 levels 4/5: copies the rendered paw animations (design\anim\paw_*) into assets\lion next to the lion's own
# animations, compressed the same way (tools\FrameQuantizer), and writes assets\lion\paw.json.
# Only the paw folders are replaced; the lion's frames are left alone. build_lion_assets.ps1 calls this too.
#   -Dither 0..1   error diffusion for the palette step (default 0.5, like the lion)
param([double]$Dither = 0.5)
$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot -Parent
$src   = Join-Path $root 'design\anim'
$dest  = Join-Path $root 'assets\lion'
$stage = Join-Path $env:TEMP 'assistail-paw-stage'
$out   = Join-Path $env:TEMP 'assistail-paw-out'
$anims = [ordered]@{ boop = 'paw_boop'; sign_in = 'paw_sign_in'; sign_wiggle = 'paw_sign_wiggle' }
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

foreach ($d in $stage, $out) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$manifest = [ordered]@{}
foreach ($name in $anims.Keys) {
    $folder = $anims[$name]
    $json = Join-Path $src "$folder\$folder.json"
    if (-not (Test-Path $json)) { throw "Missing paw animation '$folder' (render it with tools\animate_paw.py)" }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage $folder) | Out-Null
    Copy-Item (Join-Path $src "$folder\f_*.png") (Join-Path $stage $folder)
    $manifest[$name] = Get-Content $json -Raw | ConvertFrom-Json
    $manifest[$name] | Add-Member -NotePropertyName folder -NotePropertyValue $folder
}
# The paw print gets its own palette (it's a single flat-coloured image).
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'paw_print') | Out-Null
Copy-Item (Join-Path $src 'paw_boop\print.png') (Join-Path $stage 'paw_print\f_0001.png')
$manifest['print'] = [ordered]@{ frames = 1; fps = 15; loop = $false; folder = 'paw_print' }

$qargs = @($stage, $out)
if ($Dither -gt 0) { $qargs += @('--dither', $Dither.ToString([Globalization.CultureInfo]::InvariantCulture)) }
& $dotnet run --project (Join-Path $root 'tools\FrameQuantizer') -c Release -- @qargs
if ($LASTEXITCODE -ne 0) { throw "FrameQuantizer failed" }

foreach ($folder in @($anims.Values) + 'paw_print') {
    $target = Join-Path $dest $folder
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item (Join-Path $out $folder) $target -Recurse
}
$manifest | ConvertTo-Json -Depth 5 -Compress | Set-Content (Join-Path $dest 'paw.json') -Encoding utf8
Remove-Item $stage, $out -Recurse -Force
$size = (Get-ChildItem $dest -Directory -Filter 'paw_*' | Get-ChildItem -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Paw assets: {0} animations + print, {1:N2} MB -> {2}" -f $anims.Count, $size, $dest)
