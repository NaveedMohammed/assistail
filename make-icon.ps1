# Builds assets\assistail.ico (16-256 px) and assets\icon.png from the curly-tail speech-bubble design
# drawn by tools\make_bubble_icons.ps1.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot 'assets'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'assistail-icon'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
& (Join-Path $PSScriptRoot 'tools\make_bubble_icons.ps1') -OutDir $tmp | Out-Null
$source = [System.Drawing.Image]::FromFile((Join-Path $tmp 'bubble-tail.png'))
Copy-Item (Join-Path $tmp 'bubble-tail.png') (Join-Path $assets 'icon.png') -Force

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    ,$ms.ToArray()
}
$source.Dispose()

$out = Join-Path $assets 'assistail.ico'
$w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($out))
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $images[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$d.Length); $w.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $images) { $w.Write($d) }
$w.Close()
Remove-Item $tmp -Recurse -Force
Write-Host "Wrote $out"
