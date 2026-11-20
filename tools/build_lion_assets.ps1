# Copies rendered lion animations (design\anim) into assets\lion for embedding in the app, compressing each
# animation to a 256-colour palette PNG set (tools\FrameQuantizer), and merges the per-state manifests into
# assets\lion\lion.json.
#   -Dither 0..1   error diffusion for the palette step (default 0.5, the approved look; 0 = none)
#   -Lossless      keep full 32-bit colour (bigger download)
param([double]$Dither = 0.5, [switch]$Lossless)
$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot -Parent
$src   = Join-Path $root 'design\anim'
$dest  = Join-Path $root 'assets\lion'
$stage = Join-Path $env:TEMP 'assistail-lion-stage'
$states = 'idle','hover','thinking','talking','happy','dragged','landing','sleep','turn','stand_up','walk',
          'shake_head','jump','stretch_yawn','tail_chase','wave','look_roar','look_up','swat','butterfly'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$manifest = [ordered]@{}
foreach ($s in $states) {
    $json = Join-Path $src "$s.json"
    if (-not (Test-Path $json)) { throw "Missing animation '$s' (render it with tools\animate_lion.py)" }
    Copy-Item (Join-Path $src $s) (Join-Path $stage $s) -Recurse
    $manifest[$s] = Get-Content $json -Raw | ConvertFrom-Json
}

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
$qargs = @($stage, $dest)
if ($Lossless) { $qargs += '--lossless' } elseif ($Dither -gt 0) { $qargs += @('--dither', $Dither.ToString([Globalization.CultureInfo]::InvariantCulture)) }
& $dotnet run --project (Join-Path $root 'tools\FrameQuantizer') -c Release -- @qargs
if ($LASTEXITCODE -ne 0) { throw "FrameQuantizer failed" }
Remove-Item $stage -Recurse -Force

$manifest | ConvertTo-Json -Depth 5 -Compress | Set-Content (Join-Path $dest 'lion.json') -Encoding utf8
$size = (Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Lion assets: {0} states, {1:N1} MB -> {2}" -f $states.Count, $size, $dest)
# The paw reminders (B5 levels 4/5) live in the same folder: rebuild them too (the folder was just replaced).
if (Test-Path (Join-Path $src 'paw_boop')) { & (Join-Path $PSScriptRoot 'build_paw_assets.ps1') -Dither $Dither }
else { Write-Warning "designnim\paw_* not rendered: the paw reminders won't be in assets\lion (run toolsnimate_paw.py)" }
