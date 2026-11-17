# Composes the README demo as PNG frames (mock desktop + taskbar + the real lion animation frames).
# Uses only project assets, so no real desktop content ends up in the GIF.
# Usage: .\tools\make_demo_frames.ps1 -OutDir C:\temp\demo   then   .\tools\encode_gif.ps1 -FramesDir C:\temp\demo -Out docs\demo.gif
param([Parameter(Mandatory)][string]$OutDir)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lionDir = Join-Path $root 'assets\lion'
$manifest = Get-Content (Join-Path $lionDir 'lion.json') -Raw | ConvertFrom-Json
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$W = 640; $H = 330; $barH = 40; $groundY = $H - $barH
$lionSize = 210                      # drawn size of the 360 px lion frames
$lionCx = 190                        # lion body centre x on the mock desktop
$font = New-Object System.Drawing.Font('Segoe UI', 11)
$fontB = New-Object System.Drawing.Font('Segoe UI Semibold', 11)
$capFont = New-Object System.Drawing.Font('Segoe UI Semibold', 13)
$cache = @{}
function Frame($state, $i) {
    $key = "$state/$i"
    if (-not $cache.ContainsKey($key)) { $cache[$key] = [System.Drawing.Image]::FromFile((Join-Path $lionDir ("{0}\f_{1:D4}.png" -f $state, ($i + 1)))) }
    $cache[$key]
}
function C($hex, $a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }

# Timeline: list of (state, frames, caption, extra) steps; one GIF frame per animation frame.
$steps = @(
    @{ s = 'idle'; n = 24; cap = 'A buddy who lives on your taskbar' },
    @{ s = 'wave'; n = 24; cap = 'Click him to chat' ; chat = 'open' },
    @{ s = 'thinking'; n = 18; cap = 'He thinks while Claude works...'; chat = 'thinking' },
    @{ s = 'talking'; n = 30; cap = '...and nods along as the answer streams in'; chat = 'typing' },
    @{ s = 'happy'; n = 24; cap = '...and nods along as the answer streams in'; chat = 'done' },
    @{ s = 'look_up'; n = 30; cap = 'Plays with butterflies'; fly = 'in' },
    @{ s = 'swat'; n = 14; cap = 'Plays with butterflies'; fly = 'near' },
    @{ s = 'happy'; n = 24; cap = 'Plays with butterflies'; fly = 'away' },
    @{ s = 'jump'; n = 22; cap = 'Tricks and quirks: Ctrl+Alt+T' },
    @{ s = 'tail_chase'; n = 36; cap = 'Tricks and quirks: Ctrl+Alt+T' },
    @{ s = 'idle'; n = 12; cap = 'Assistail: an assistant with a tail' }
)
$answer = 'Use git reset --soft HEAD~1 to undo the last commit and keep your changes staged.'
$bfFrames = $manifest.butterfly.frames
$out = 0; $typed = 0; $bfX = 700.0; $bfY = 40.0

foreach ($st in $steps) {
    $m = $manifest.($st.s)
    $idle = $manifest.idle
    for ($k = 0; $k -lt $st.n; $k++) {
        $f = [Math]::Min($k, $m.frames - 1)
        $bmp = New-Object System.Drawing.Bitmap $W, $H
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'ClearTypeGridFit'
        # Desktop + taskbar
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($W, $H)), (C '#CFE3F7'), (C '#E9D8F2')
        $g.FillRectangle($bg, 0, 0, $W, $H)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#F3F3F3')), 0, $groundY, $W, $barH)
        $g.DrawLine((New-Object System.Drawing.Pen (C '#D6D6D6')), 0, $groundY, $W, $groundY)
        $icons = '#0078D4', '#F2B600', '#E3412B', '#2F9E44', '#7048E8'
        for ($i = 0; $i -lt $icons.Count; $i++) { $g.FillRectangle((New-Object System.Drawing.SolidBrush (C $icons[$i])), 250 + $i * 34, $groundY + 9, 22, 22) }
        $g.DrawString('3:25 PM', $font, [System.Drawing.Brushes]::DimGray, $W - 78, $groundY + 10)

        # Ground shadow + lion (aligned to the idle paw line like the app does)
        $lift = $m.lift[$f] * $m.lift_per_unit * $lionSize
        $sw = $m.shadow_w * $lionSize * [Math]::Max(0.45, 1 - $lift / ($lionSize * 0.6))
        $sh = New-Object System.Drawing.Drawing2D.GraphicsPath; $sh.AddEllipse([float]($lionCx - $sw / 2), [float]($groundY + 3 - $sw * 0.11), [float]$sw, [float]($sw * 0.22))
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $sh; $pgb.CenterColor = (C '#000000' 90); $pgb.SurroundColors = @((C '#000000' 0))
        $g.FillPath($pgb, $sh)
        $img = Frame $st.s $f
        $x = $lionCx - $m.center_x * $lionSize; $y = $groundY + 3 - $m.baseline_y * $lionSize
        $g.DrawImage($img, [float]$x, [float]$y, [float]$lionSize, [float]$lionSize)

        # Chat window beside the lion
        if ($st.chat) {
            $cx = 300; $cy = 60; $cw = 316; $ch = 170
            $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#262624')), $cx, $cy, $cw, $ch)
            $g.DrawString('Leo', $fontB, (New-Object System.Drawing.SolidBrush (C '#EDEBE6')), $cx + 12, $cy + 8)
            $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#3A3936')), $cx + 98, $cy + 38, 206, 26)
            $g.DrawString('how do I undo a git commit?', $font, (New-Object System.Drawing.SolidBrush (C '#EDEBE6')), $cx + 104, $cy + 41)
            if ($st.chat -eq 'thinking') { $g.DrawString('Thinking...', $font, (New-Object System.Drawing.SolidBrush (C '#9B978F')), $cx + 12, $cy + 76) }
            if ($st.chat -in 'typing', 'done') {
                if ($st.chat -eq 'typing') { $typed = [Math]::Min($answer.Length, $typed + 3) } else { $typed = $answer.Length }
                $rect = New-Object System.Drawing.RectangleF ([float]($cx + 12)), ([float]($cy + 76)), ([float]($cw - 24)), 80
                $g.DrawString($answer.Substring(0, $typed), $font, (New-Object System.Drawing.SolidBrush (C '#EDEBE6')), $rect)
            }
            $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#30302E')), $cx + 10, $cy + $ch - 34, $cw - 20, 26)
            $g.DrawString('Ask Leo...', $font, (New-Object System.Drawing.SolidBrush (C '#77736C')), $cx + 16, $cy + $ch - 31)
        }

        # Butterfly
        if ($st.fly) {
            $hx = $lionCx + 10; $hy = $groundY - 165
            switch ($st.fly) {
                'in'   { $tx = $hx + 40 + 45 * [Math]::Cos($k / 4.0); $ty = $hy - 10 + 18 * [Math]::Sin($k / 4.0) }
                'near' { $tx = $hx + 30; $ty = $hy + 10 }
                'away' { $tx = $W + 80; $ty = -60 }
            }
            $ease = if ($st.fly -eq 'away') { 0.1 } else { 0.25 }
            $nx = $bfX + ($tx - $bfX) * $ease; $flip = $nx -lt $bfX; $bfX = $nx; $bfY = $bfY + ($ty - $bfY) * $ease + [Math]::Sin($out / 1.5) * 1.5
            $bf = [System.Drawing.Image]::FromFile((Join-Path $lionDir ("butterfly\f_{0:D4}.png" -f (($out % $bfFrames) + 1))))
            if ($flip) { $bf.RotateFlip('RotateNoneFlipX') }
            $g.DrawImage($bf, [float]($bfX - 22), [float]($bfY - 22), 44, 44); $bf.Dispose()
        }

        # Caption
        $capSize = $g.MeasureString($st.cap, $capFont)
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#000000' 150)), 14, 12, $capSize.Width + 16, $capSize.Height + 8)
        $g.DrawString($st.cap, $capFont, [System.Drawing.Brushes]::White, 22, 16)

        $g.Dispose()
        $bmp.Save((Join-Path $OutDir ("f_{0:D4}.png" -f $out)), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose(); $out++
    }
}
foreach ($img in $cache.Values) { $img.Dispose() }
Write-Host "$out frames -> $OutDir"
