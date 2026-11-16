# Draws flat paw-print icon options (256 px PNGs) into design\icons\.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'design\icons'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-Canvas { $b = New-Object System.Drawing.Bitmap 256, 256; $g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent); return $b, $g }
function C($hex, $a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }

# Paw shape: big rounded main pad + 4 toe beans on an arc. Scale/offset in a 256 box.
function Draw-Paw($g, $brush, [float]$cx, [float]$cy, [float]$s, [float]$tilt = -8) {
    $state = $g.Save()
    $g.TranslateTransform($cx, $cy); $g.RotateTransform($tilt); $g.ScaleTransform($s, $s)
    # main pad (rounded heart-ish: union of three ellipses)
    $g.FillEllipse($brush, -46, 2, 92, 70)
    $g.FillEllipse($brush, -58, 18, 52, 50)
    $g.FillEllipse($brush, 6, 18, 52, 50)
    # toes
    foreach ($t in @(@(-72, -34, 34, 44, -20), @(-34, -66, 34, 46, -6), @(4, -66, 34, 46, 6), @(42, -34, 34, 44, 20))) {
        $st = $g.Save()
        $g.TranslateTransform($t[0] + $t[2] / 2, $t[1] + $t[3] / 2); $g.RotateTransform($t[4])
        $g.FillEllipse($brush, -$t[2] / 2, -$t[3] / 2, $t[2], $t[3])
        $g.Restore($st)
    }
    $g.Restore($state)
}

function Save($b, $name) { $b.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose() }

# 1. Flat orange paw
$b, $g = New-Canvas
Draw-Paw $g (New-Object System.Drawing.SolidBrush (C '#E8833A')) 128 136 1.25
Save $b 'paw-flat'

# 2. White paw on an orange rounded square (app-icon style)
$b, $g = New-Canvas
$path = New-Object System.Drawing.Drawing2D.GraphicsPath; $r = 56
$path.AddArc(8, 8, $r, $r, 180, 90); $path.AddArc(248 - $r, 8, $r, $r, 270, 90); $path.AddArc(248 - $r, 248 - $r, $r, $r, 0, 90); $path.AddArc(8, 248 - $r, $r, $r, 90, 90); $path.CloseFigure()
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(256, 256)), (C '#F2A04A'), (C '#D9622B')
$g.FillPath($grad, $path)
Draw-Paw $g (New-Object System.Drawing.SolidBrush (C '#FFFFFF')) 128 138 0.98
Save $b 'paw-tile'

# 3. Paw inside a speech bubble (buddy + chat)
$b, $g = New-Canvas
$g.FillEllipse((New-Object System.Drawing.SolidBrush (C '#E8833A')), 10, 14, 236, 196)
$g.FillPolygon((New-Object System.Drawing.SolidBrush (C '#E8833A')), [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(50, 160), [System.Drawing.PointF]::new(30, 246), [System.Drawing.PointF]::new(116, 190)))
Draw-Paw $g (New-Object System.Drawing.SolidBrush (C '#FFFFFF')) 128 118 0.88
Save $b 'paw-bubble'

# 4. Paw in a circle badge with a sparkle
$b, $g = New-Canvas
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, 256)), (C '#7B6CF6'), (C '#5B47D6')
$g.FillEllipse($grad, 8, 8, 240, 240)
Draw-Paw $g (New-Object System.Drawing.SolidBrush (C '#FFD27A')) 122 140 0.95
$spark = New-Object System.Drawing.Drawing2D.GraphicsPath
$cx = 196; $cy = 62; $R = 26; $rr = 7
$pts = for ($i = 0; $i -lt 8; $i++) { $a = [Math]::PI / 4 * $i - [Math]::PI / 2; $len = if ($i % 2 -eq 0) { $R } else { $rr }; [System.Drawing.PointF]::new($cx + [Math]::Cos($a) * $len, $cy + [Math]::Sin($a) * $len) }
$spark.AddPolygon([System.Drawing.PointF[]]$pts)
$g.FillPath((New-Object System.Drawing.SolidBrush (C '#FFFFFF')), $spark)
Save $b 'paw-badge'

Write-Host "Flat paw icons -> $out"
