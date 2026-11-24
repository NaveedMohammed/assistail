# Builds the cat buddy pack (B1) from the rendered frames in design\cat-anim (tools\render_cat_anims.ps1):
# compresses every state to palette PNGs (tools\FrameQuantizer, same settings as the lion), merges the per-state
# manifests into buddy.json with the buddy's metadata, adds the paw-reminder frames if rendered, and zips it.
#   -Out     output folder (default release\buddies)  ->  <Out>\cat\ (unpacked) and <Out>\cat-<version>.zip
#   -Dither  0..1 palette dithering (default 0.5, like the lion)
param([string]$Out, [double]$Dither = 0.5, [string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot -Parent
$src   = Join-Path $root 'design\cat-anim'
if (-not $Out) { $Out = Join-Path $root 'release\buddies' }
$dest  = Join-Path $Out 'cat'
$stage = Join-Path $env:TEMP 'assistail-cat-stage'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

# Every state the cat has (from animate_cat.py) except the design-only ones
$states = @()
$inTable = $false
foreach ($line in Get-Content (Join-Path $PSScriptRoot 'animate_cat.py')) {
    if ($line -match '^STATES = \{') { $inTable = $true; continue }
    if ($inTable -and $line -match '^\}') { break }
    if ($inTable) { foreach ($m in [regex]::Matches($line, '"(\w+)":\s*\(')) { $states += $m.Groups[1].Value } }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$anims = [ordered]@{}
foreach ($s in $states) {
    $json = Join-Path $src "$s.json"
    if (-not (Test-Path $json)) { throw "Missing animation '$s' (render it with tools\render_cat_anims.ps1)" }
    Copy-Item (Join-Path $src $s) (Join-Path $stage $s) -Recurse
    $anims[$s] = Get-Content $json -Raw | ConvertFrom-Json
}
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
$qargs = @($stage, $dest)
if ($Dither -gt 0) { $qargs += @('--dither', $Dither.ToString([Globalization.CultureInfo]::InvariantCulture)) }
& $dotnet run --project (Join-Path $root 'tools\FrameQuantizer') -c Release -- @qargs
if ($LASTEXITCODE -ne 0) { throw "FrameQuantizer failed" }
Remove-Item $stage -Recurse -Force

# Paw reminders (B5 levels 4/5), rendered by tools\animate_paw.py with buddy=cat into design\cat-anim\paw_*.
# Same layout as the lion's (paw.json + paw_boop, paw_sign_in, paw_sign_wiggle, paw_print), so PawOverlay reads either.
$pawAnims = [ordered]@{ boop = 'paw_boop'; sign_in = 'paw_sign_in'; sign_wiggle = 'paw_sign_wiggle' }
$pawStage = Join-Path $env:TEMP 'assistail-catpaw-stage'
if (Test-Path $pawStage) { Remove-Item $pawStage -Recurse -Force }
$paw = [ordered]@{}
foreach ($name in $pawAnims.Keys) {
    $folder = $pawAnims[$name]
    $json = Join-Path $src "$folder\$folder.json"
    if (-not (Test-Path $json)) { throw "Missing paw animation '$folder' (tools\animate_paw.py with buddy=cat)" }
    New-Item -ItemType Directory -Force -Path (Join-Path $pawStage $folder) | Out-Null
    Copy-Item (Join-Path $src "$folder\f_*.png") (Join-Path $pawStage $folder)
    $paw[$name] = Get-Content $json -Raw | ConvertFrom-Json
    $paw[$name] | Add-Member -NotePropertyName folder -NotePropertyValue $folder
}
New-Item -ItemType Directory -Force -Path (Join-Path $pawStage 'paw_print') | Out-Null
Copy-Item (Join-Path $src 'paw_boop\print.png') (Join-Path $pawStage 'paw_print\f_0001.png')
$paw['print'] = [ordered]@{ frames = 1; fps = 15; loop = $false; folder = 'paw_print' }
& $dotnet run --project (Join-Path $root 'tools\FrameQuantizer') -c Release -- $pawStage $dest --dither $Dither.ToString([Globalization.CultureInfo]::InvariantCulture)
if ($LASTEXITCODE -ne 0) { throw "FrameQuantizer failed (paw)" }
Remove-Item $pawStage -Recurse -Force
$paw | ConvertTo-Json -Depth 5 -Compress | Set-Content (Join-Path $dest 'paw.json') -Encoding utf8

$catEmoji = [char]::ConvertFromUtf32(0x1F431)   # not a literal: PowerShell 5.1 reads scripts as ANSI
$buddy = [ordered]@{
    format      = 1
    id          = 'cat'
    name        = 'Calico cat'
    version     = $Version
    minApp      = '2.5.0'
    author      = 'Assistail'
    description = 'A calico kitten with a plaid cloth tied on his paw. He tries to pull it off, licks your pointer, and runs away when you try to put it back on.'
    icon        = $catEmoji
    emoji       = $catEmoji
    persona     = 'a cute calico kitten with big green eyes and a green plaid cloth tied on one paw'
    hello       = 'Mrrp! (that''s cat for hello)'
    labels      = [ordered]@{ look_roar = 'Look around && meow'; tail_chase = 'Chase his tail' }
    features    = @('cloth', 'lick')
    anims       = $anims
}
$buddy | ConvertTo-Json -Depth 6 -Compress | Set-Content (Join-Path $dest 'buddy.json') -Encoding utf8
Remove-Item (Join-Path $dest '*.json') -Exclude 'buddy.json', 'paw.json' -ErrorAction SilentlyContinue

$zip = Join-Path $Out "cat-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
# Not Compress-Archive: PowerShell 5.1 writes backslashes into zip paths (and the app refuses such packs).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [IO.File]::Open($zip, 'CreateNew')
$archive = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
foreach ($file in Get-ChildItem $dest -Recurse -File | Sort-Object FullName) {
    $name = $file.FullName.Substring($dest.Length + 1).Replace([IO.Path]::DirectorySeparatorChar, '/')
    $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)   # same bytes for the same pack
    $w = $entry.Open(); $bytes = [IO.File]::ReadAllBytes($file.FullName); $w.Write($bytes, 0, $bytes.Length); $w.Dispose()
}
$archive.Dispose(); $fs.Dispose()
$size = (Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$frames = (Get-ChildItem $dest -Recurse -Filter 'f_*.png').Count
Write-Host ("Cat pack: {0} states, {1} frames, {2:N1} MB unpacked, {3:N1} MB zipped -> {4}" -f $states.Count, $frames, $size, ((Get-Item $zip).Length / 1MB), $zip)
