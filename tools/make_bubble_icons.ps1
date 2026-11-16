# Draws speech-bubble icon ideas (256 px PNGs) into design\icons\bubble-*.png
param([string]$OutDir = "")
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = if ($OutDir) { $OutDir } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'design\icons' }
New-Item -ItemType Directory -Force -Path $out | Out-Null

function New-Canvas { $b = New-Object System.Drawing.Bitmap 256, 256; $g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent); return $b, $g }
function C($hex, $a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }
function Brush($hex, $a = 255) { New-Object System.Drawing.SolidBrush (C $hex $a) }
function P([float]$x, [float]$y) { [System.Drawing.PointF]::new($x, $y) }
function Save($b, $name) { $b.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose() }

function Draw-Paw($g, $brush, [float]$cx, [float]$cy, [float]$s, [float]$tilt = -8) {
    $state = $g.Save()
    $g.TranslateTransform($cx, $cy); $g.RotateTransform($tilt); $g.ScaleTransform($s, $s)
    $g.FillEllipse($brush, -46, 2, 92, 70); $g.FillEllipse($brush, -58, 18, 52, 50); $g.FillEllipse($brush, 6, 18, 52, 50)
    foreach ($t in @(@(-72, -34, 34, 44, -20), @(-34, -66, 34, 46, -6), @(4, -66, 34, 46, 6), @(42, -34, 34, 44, 20))) {
        $st = $g.Save(); $g.TranslateTransform($t[0] + $t[2] / 2, $t[1] + $t[3] / 2); $g.RotateTransform($t[4])
        $g.FillEllipse($brush, -$t[2] / 2, -$t[3] / 2, $t[2], $t[3]); $g.Restore($st)
    }
    $g.Restore($state)
}

# Rounded speech bubble: body path + tail path (filled separately so their overlap never cancels out)
function Bubble-Path([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddArc($x, $y, $r, $r, 180, 90); $body.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $body.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $body.AddArc($x, $y + $h - $r, $r, $r, 90, 90); $body.CloseFigure()
    $tail = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tail.AddPolygon([System.Drawing.PointF[]]@((P ($x + 30) ($y + $h - 20)), (P ($x + 14) ($y + $h + 46)), (P ($x + 96) ($y + $h - 20))))
    return ,@($body, $tail)
}
function Fill-Bubble($g, $brush, $paths) { foreach ($p in $paths) { $g.FillPath($brush, $p) } }
function Transform-Bubble($paths, $matrix) { foreach ($p in $paths) { $p.Transform($matrix) } }

$orange = '#E8833A'; $deep = '#D9622B'

# B1. Critter bubble: the bubble IS a little face, with ears on top and eyes inside (works for any animal)
$b, $g = New-Canvas
foreach ($ear in @(@(40, 54), @(176, 54))) {
    $tri = [System.Drawing.PointF[]]@((P $ear[0] ($ear[1] + 30)), (P ($ear[0] + 20) ($ear[1] - 34)), (P ($ear[0] + 52) ($ear[1] + 22)))
    $g.FillPolygon((Brush $orange), $tri)
    $inner = [System.Drawing.PointF[]]@((P ($ear[0] + 12) ($ear[1] + 22)), (P ($ear[0] + 22) ($ear[1] - 14)), (P ($ear[0] + 40) ($ear[1] + 16)))
    $g.FillPolygon((Brush '#F6B48A'), $inner)
}
Fill-Bubble $g (Brush $orange) (Bubble-Path 16 44 224 158 90)
$g.FillEllipse((Brush '#FFFFFF'), 76, 92, 34, 42); $g.FillEllipse((Brush '#FFFFFF'), 146, 92, 34, 42)
$g.FillEllipse((Brush '#2B1E16'), 84, 102, 22, 28); $g.FillEllipse((Brush '#2B1E16'), 154, 102, 22, 28)
$g.FillEllipse((Brush '#FFFFFF'), 92, 106, 8, 8); $g.FillEllipse((Brush '#FFFFFF'), 162, 106, 8, 8)
$g.FillEllipse((Brush '#7A3A1C'), 119, 140, 18, 12)
Save $b 'bubble-critter'

# B2. Paw-shaped bubble: the main pad is the bubble (with a tail), toes float above
$b, $g = New-Canvas
$g.FillEllipse((Brush $orange), 38, 104, 180, 118)
$g.FillPolygon((Brush $orange), [System.Drawing.PointF[]]@((P 66 192), (P 44 250), (P 124 206)))
foreach ($t in @(@(26, 54, 44, 56, -20), @(78, 18, 46, 58, -6), @(132, 18, 46, 58, 6), @(186, 54, 44, 56, 20))) {
    $st = $g.Save(); $g.TranslateTransform($t[0] + $t[2] / 2, $t[1] + $t[3] / 2); $g.RotateTransform($t[4])
    $g.FillEllipse((Brush $orange), -$t[2] / 2, -$t[3] / 2, $t[2], $t[3]); $g.Restore($st)
}
foreach ($dx in 88, 128, 168) { $g.FillEllipse((Brush '#FFFFFF'), $dx - 11, 152, 22, 22) }   # typing dots
Save $b 'bubble-pawshape'

# B3. Bubble with "…" typing dots and a paw peeking over its edge
$b, $g = New-Canvas
Fill-Bubble $g (Brush $orange) (Bubble-Path 16 70 224 136 80)
foreach ($dx in 82, 128, 174) { $g.FillEllipse((Brush '#FFFFFF'), $dx - 14, 124, 28, 28) }
Draw-Paw $g (Brush $deep) 196 56 0.5 18
Save $b 'bubble-peek'

# B4. Two chat bubbles (conversation) with a paw on the front one
$b, $g = New-Canvas
$back = Bubble-Path 86 20 156 108 56
$m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate(328, 0); $m.Scale(-1, 1); Transform-Bubble $back $m   # mirror: tail on the right
Fill-Bubble $g (Brush "#F6B48A") $back
Fill-Bubble $g (Brush $orange) (Bubble-Path 14 92 176 118 64)
Draw-Paw $g (Brush '#FFFFFF') 102 150 0.55
Save $b 'bubble-duo'

# B5. Outline style: white bubble with orange border and an orange paw (light, friendly)
$b, $g = New-Canvas
$paths = Bubble-Path 18 22 220 170 92
$pen = New-Object System.Drawing.Pen (C $orange), 16; $pen.LineJoin = 'Round'
foreach ($p in $paths) { $g.DrawPath($pen, $p) }          # outline both shapes…
Fill-Bubble $g (Brush '#FFFFFF') $paths                    # …then fill white, hiding the inner seam: one clean outline
Draw-Paw $g (Brush $orange) 128 110 0.82
Save $b 'bubble-outline'

# B6. Bubble whose tail is a curly animal tail
$b, $g = New-Canvas
$body = New-Object System.Drawing.Drawing2D.GraphicsPath
$body.AddEllipse(18, 16, 220, 172)
$g.FillPath((Brush $orange), $body)
$tailPen = New-Object System.Drawing.Pen (C $orange), 22; $tailPen.StartCap = 'Round'; $tailPen.EndCap = 'Round'
$g.DrawBezier($tailPen, (P 70 172), (P 40 224), (P 6 196), (P 26 168))
$g.FillEllipse((Brush $deep), 12, 152, 30, 30)            # tail tuft
Draw-Paw $g (Brush '#FFFFFF') 128 104 0.8
Save $b 'bubble-tail'

Write-Host "Bubble icons -> $out"
