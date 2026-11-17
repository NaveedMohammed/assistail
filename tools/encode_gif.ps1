# Encodes PNG frames (f_0000.png …) into a looping GIF using only Windows' built-in WPF encoder.
# WPF's GifBitmapEncoder doesn't write frame delays or looping, so the result is post-processed:
# a NETSCAPE2.0 loop block is added and every frame gets a Graphic Control Extension with the delay.
# Usage: .\tools\encode_gif.ps1 -FramesDir C:\temp\demo -Out docs\demo.gif [-Fps 15] [-Width 560]
param([Parameter(Mandatory)][string]$FramesDir, [Parameter(Mandatory)][string]$Out, [int]$Fps = 15, [int]$Width = 0)
Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System; using System.IO; using System.Collections.Generic;
public static class GifFix {
    // Bounding box of pixels that differ between two same-size 32bpp bitmaps (Rectangle.Empty if identical).
    public static System.Drawing.Rectangle DiffRect(System.Drawing.Bitmap a, System.Drawing.Bitmap b) {
        var r = new System.Drawing.Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var db = b.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int w = a.Width, h = a.Height, x0 = w, y0 = h, x1 = -1, y1 = -1;
        var ra = new int[w]; var rb = new int[w];
        for (int y = 0; y < h; y++) {
            System.Runtime.InteropServices.Marshal.Copy(da.Scan0 + y * da.Stride, ra, 0, w);
            System.Runtime.InteropServices.Marshal.Copy(db.Scan0 + y * db.Stride, rb, 0, w);
            for (int x = 0; x < w; x++) {
                int pa = ra[x], pb = rb[x];
                if (pa == pb) continue;
                int d = Math.Abs(((pa >> 16) & 255) - ((pb >> 16) & 255)) + Math.Abs(((pa >> 8) & 255) - ((pb >> 8) & 255)) + Math.Abs((pa & 255) - (pb & 255));
                if (d < 6) continue;                      // ignore invisible noise
                if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        }
        a.UnlockBits(da); b.UnlockBits(db);
        return x1 < 0 ? System.Drawing.Rectangle.Empty : new System.Drawing.Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    // Rewrites a GIF: keeps header/global table, adds a loop block, puts a delay before every image,
    // and places each (cropped) image at its offset.
    public static byte[] AddTiming(byte[] g, int delayCs, int[] lefts, int[] tops, int screenW, int screenH) {
        g[6] = (byte)(screenW & 255); g[7] = (byte)(screenW >> 8); g[8] = (byte)(screenH & 255); g[9] = (byte)(screenH >> 8);
        int frame = 0;
        var o = new List<byte>();
        int p = 13;
        int flags = g[10];
        if ((flags & 0x80) != 0) p += 3 * (1 << ((flags & 7) + 1));
        for (int i = 0; i < p; i++) o.Add(g[i]);
        o.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
        o.AddRange(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        o.AddRange(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
        while (p < g.Length) {
            byte b = g[p];
            if (b == 0x3B) { o.Add(0x3B); break; }
            if (b == 0x21) {                       // extension: drop it (we write our own timing/loop)
                p += 2;
                while (g[p] != 0) p += g[p] + 1;
                p++;
                continue;
            }
            if (b == 0x2C) {                       // image: write a Graphic Control Extension first
                o.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x04, (byte)(delayCs & 0xFF), (byte)(delayCs >> 8), 0x00, 0x00 });
                int start = p;
                if (frame < lefts.Length) {
                    g[p + 1] = (byte)(lefts[frame] & 255); g[p + 2] = (byte)(lefts[frame] >> 8);
                    g[p + 3] = (byte)(tops[frame] & 255);  g[p + 4] = (byte)(tops[frame] >> 8);
                }
                frame++;
                int lflags = g[p + 9];
                p += 10;
                if ((lflags & 0x80) != 0) p += 3 * (1 << ((lflags & 7) + 1));
                p++;                               // LZW minimum code size
                while (g[p] != 0) p += g[p] + 1;
                p++;
                for (int i = start; i < p; i++) o.Add(g[i]);
                continue;
            }
            throw new InvalidDataException("Unexpected GIF block 0x" + b.ToString("X2") + " at " + p);
        }
        return o.ToArray();
    }
}
'@ -ReferencedAssemblies System.Drawing

Add-Type -AssemblyName System.Drawing
$enc = New-Object System.Windows.Media.Imaging.GifBitmapEncoder
$files = Get-ChildItem $FramesDir -Filter 'f_*.png' | Sort-Object Name
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('gifcrop_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$lefts = New-Object System.Collections.Generic.List[int]; $tops = New-Object System.Collections.Generic.List[int]
$prev = $null; $screenW = 0; $screenH = 0; $i = 0
foreach ($f in $files) {
    $orig = [System.Drawing.Bitmap]::FromFile($f.FullName)
    $cur = if ($Width -gt 0) { New-Object System.Drawing.Bitmap $orig, $Width, ([int]($orig.Height * $Width / $orig.Width)) } else { New-Object System.Drawing.Bitmap $orig }
    $orig.Dispose()
    if ($i -eq 0) { $screenW = $cur.Width; $screenH = $cur.Height; $rect = New-Object System.Drawing.Rectangle 0, 0, $cur.Width, $cur.Height }
    else {
        $rect = [GifFix]::DiffRect($prev, $cur)
        if ($rect.IsEmpty) { $rect = New-Object System.Drawing.Rectangle 0, 0, 1, 1 }   # unchanged frame: tiny no-op patch
    }
    $crop = $cur.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $path = Join-Path $tmp ("c_{0:D4}.png" -f $i); $crop.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $crop.Dispose()
    $lefts.Add($rect.X); $tops.Add($rect.Y)
    $src = New-Object System.Windows.Media.Imaging.BitmapImage
    $src.BeginInit(); $src.UriSource = [Uri]$path; $src.CacheOption = 'OnLoad'; $src.EndInit()
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($src))
    if ($prev) { $prev.Dispose() }; $prev = $cur; $i++
}
if ($prev) { $prev.Dispose() }
$ms = New-Object System.IO.MemoryStream
$enc.Save($ms)
Remove-Item $tmp -Recurse -Force
$fixed = [GifFix]::AddTiming($ms.ToArray(), [int][Math]::Round(100 / $Fps), $lefts.ToArray(), $tops.ToArray(), $screenW, $screenH)
[IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $Out -Parent)).Path + '\' + (Split-Path $Out -Leaf), $fixed)
Write-Host ("{0} frames -> {1} ({2:N1} MB)" -f $files.Count, $Out, ($fixed.Length / 1MB))
