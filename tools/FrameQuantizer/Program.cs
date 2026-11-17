// FrameQuantizer: shrinks rendered animation frames for embedding.
//
//   FrameQuantizer <srcDir> <dstDir> [--colors 256] [--dither 0..1] [--lossless] [--states a,b,c]
//
// srcDir holds one sub-folder per animation state (f_0001.png …, 32-bit RGBA). For each state, one shared palette
// is built from all its frames (so colours don't shimmer from frame to frame), and every frame is written as an
// 8-bit palette PNG with per-entry alpha (tRNS). Quantization works on premultiplied colour so soft, semi-transparent
// edges keep their shape. --lossless instead re-encodes the 32-bit frames with maximum compression (no quality loss).
// Other files in srcDir (lion.json) are copied as they are.

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

int colors = 256;
double dither = 0;
bool lossless = false;
HashSet<string>? only = null;
if (args.Length < 2) { Console.Error.WriteLine("usage: FrameQuantizer <srcDir> <dstDir> [--colors N] [--dither 0..1] [--lossless] [--states a,b]"); return 2; }
string src = args[0], dst = args[1];
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--colors": colors = Math.Clamp(int.Parse(args[++i]), 2, 256); break;
        case "--dither": dither = Math.Clamp(double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture), 0, 1); break;
        case "--lossless": lossless = true; break;
        case "--states": only = new HashSet<string>(args[++i].Split(',')); break;
        default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
    }
}

Directory.CreateDirectory(dst);
foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);

long before = 0, after = 0;
var sw = System.Diagnostics.Stopwatch.StartNew();
foreach (var stateDir in Directory.GetDirectories(src).OrderBy(d => d))
{
    string state = Path.GetFileName(stateDir);
    if (only != null && !only.Contains(state)) continue;
    var files = Directory.GetFiles(stateDir, "*.png").OrderBy(f => f).ToArray();
    string outDir = Path.Combine(dst, state);
    Directory.CreateDirectory(outDir);

    var frames = files.Select(Load).ToArray();
    long b = files.Sum(f => new FileInfo(f).Length), a = 0;
    if (lossless)
    {
        for (int i = 0; i < frames.Length; i++)
        {
            string o = Path.Combine(outDir, Path.GetFileName(files[i]));
            File.WriteAllBytes(o, Png.EncodeRgba(frames[i]));
            a += new FileInfo(o).Length;
        }
    }
    else
    {
        var palette = Quantizer.BuildPalette(frames, colors);
        var mapper = new Quantizer.Mapper(palette);
        for (int i = 0; i < frames.Length; i++)
        {
            byte[] idx = mapper.Map(frames[i], dither);
            string o = Path.Combine(outDir, Path.GetFileName(files[i]));
            File.WriteAllBytes(o, Png.EncodeIndexed(frames[i].W, frames[i].H, idx, palette));
            a += new FileInfo(o).Length;
        }
    }
    before += b; after += a;
    Console.WriteLine($"{state,-14} {frames.Length,3} frames  {b / 1024.0 / 1024:N2} MB -> {a / 1024.0 / 1024:N2} MB");
}
Console.WriteLine($"TOTAL {before / 1024.0 / 1024:N1} MB -> {after / 1024.0 / 1024:N1} MB ({100.0 * after / Math.Max(1, before):N0}%) in {sw.Elapsed.TotalSeconds:N0} s");
return 0;

static Img Load(string path)
{
    var dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
    int w = conv.PixelWidth, h = conv.PixelHeight;
    var px = new byte[w * h * 4];
    conv.CopyPixels(px, w * 4, 0);
    return new Img(w, h, px);
}

/// <summary>Straight (non-premultiplied) BGRA pixels.</summary>
record Img(int W, int H, byte[] Bgra);

static class Quantizer
{
    /// <summary>Palette entry in premultiplied space (r,g,b ≤ a). Entry 0 is fully transparent.</summary>
    public record struct Pm(double R, double G, double B, double A);

    static Pm ToPm(byte[] p, int i)
    {
        double a = p[i + 3];
        return new Pm(p[i + 2] * a / 255, p[i + 1] * a / 255, p[i] * a / 255, a);
    }

    // Alpha differences matter more: they move edges.
    public static double Dist(Pm x, Pm y)
    {
        double dr = x.R - y.R, dg = x.G - y.G, db = x.B - y.B, da = x.A - y.A;
        return dr * dr * 0.30 + dg * dg * 0.59 + db * db * 0.11 + da * da * 1.0;
    }

    public static Pm[] BuildPalette(Img[] frames, int colors)
    {
        // Weighted histogram of premultiplied colours (alpha > 0).
        var hist = new Dictionary<uint, int>();
        foreach (var f in frames)
        {
            var p = f.Bgra;
            for (int i = 0; i < p.Length; i += 4)
            {
                if (p[i + 3] == 0) continue;
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(i));
                hist[key] = hist.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        }
        var pts = new List<(Pm c, int w)>(hist.Count);
        var tmp = new byte[4];
        foreach (var (key, w) in hist)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, key);
            pts.Add((ToPm(tmp, 0), w));
        }

        // Median cut into (colors - 1) boxes.
        int k = colors - 1;
        var boxes = new List<List<(Pm c, int w)>> { pts };
        while (boxes.Count < k)
        {
            int best = -1; double bestScore = 0; int bestAxis = 0;
            for (int bi = 0; bi < boxes.Count; bi++)
            {
                var box = boxes[bi];
                if (box.Count < 2) continue;
                var (axis, score) = WidestAxis(box);
                if (score > bestScore) { bestScore = score; best = bi; bestAxis = axis; }
            }
            if (best < 0) break;
            var bx = boxes[best];
            bx.Sort((x, y) => Axis(x.c, bestAxis).CompareTo(Axis(y.c, bestAxis)));
            long total = bx.Sum(e => (long)e.w), acc = 0;
            int cut = 1;
            for (int i = 0; i < bx.Count - 1; i++) { acc += bx[i].w; if (acc * 2 >= total) { cut = i + 1; break; } }
            boxes[best] = bx.GetRange(0, cut);
            boxes.Add(bx.GetRange(cut, bx.Count - cut));
        }
        var pal = boxes.Select(Mean).ToList();

        // A few rounds of weighted k-means on a sample to settle the colours.
        var rng = new Random(1);
        var sample = pts.Count > 120_000 ? pts.OrderBy(_ => rng.Next()).Take(120_000).ToList() : pts;
        for (int iter = 0; iter < 6; iter++)
        {
            var sum = new (double r, double g, double b, double a, double w)[pal.Count];
            foreach (var (c, w) in sample)
            {
                int n = Nearest(pal, c);
                sum[n].r += c.R * w; sum[n].g += c.G * w; sum[n].b += c.B * w; sum[n].a += c.A * w; sum[n].w += w;
            }
            for (int i = 0; i < pal.Count; i++)
                if (sum[i].w > 0) pal[i] = new Pm(sum[i].r / sum[i].w, sum[i].g / sum[i].w, sum[i].b / sum[i].w, sum[i].a / sum[i].w);
        }
        pal.Insert(0, new Pm(0, 0, 0, 0));
        // Round to what the PNG can store (straight 8-bit colour + 8-bit alpha), so mapping sees the real colours.
        return pal.Select(Stored).ToArray();
    }

    /// <summary>The colour as it will decode from PLTE/tRNS, in premultiplied space.</summary>
    public static Pm Stored(Pm p)
    {
        var (r, g, b, a) = Straight(p);
        return new Pm(r * a / 255.0, g * a / 255.0, b * a / 255.0, a);
    }

    public static (byte r, byte g, byte b, byte a) Straight(Pm p)
    {
        byte a = (byte)Math.Clamp(Math.Round(p.A), 0, 255);
        if (a == 0) return (0, 0, 0, 0);
        byte C(double v) => (byte)Math.Clamp(Math.Round(v * 255 / a), 0, 255);
        return (C(p.R), C(p.G), C(p.B), a);
    }

    static double Axis(Pm c, int axis) => axis switch { 0 => c.R, 1 => c.G, 2 => c.B, _ => c.A };

    static (int axis, double score) WidestAxis(List<(Pm c, int w)> box)
    {
        double[] mean = new double[4], m2 = new double[4]; double tw = 0;
        foreach (var (c, w) in box)
        {
            tw += w;
            for (int a = 0; a < 4; a++) { double v = Axis(c, a); mean[a] += v * w; m2[a] += v * v * w; }
        }
        double[] weight = { 0.30, 0.59, 0.11, 1.0 };
        int best = 0; double bestVar = -1;
        for (int a = 0; a < 4; a++)
        {
            double mu = mean[a] / tw, var = (m2[a] / tw - mu * mu) * weight[a];
            if (var > bestVar) { bestVar = var; best = a; }
        }
        return (best, bestVar * tw);   // split boxes with the most total error first
    }

    static Pm Mean(List<(Pm c, int w)> box)
    {
        double r = 0, g = 0, b = 0, a = 0, tw = 0;
        foreach (var (c, w) in box) { r += c.R * w; g += c.G * w; b += c.B * w; a += c.A * w; tw += w; }
        return new Pm(r / tw, g / tw, b / tw, a / tw);
    }

    static int Nearest(List<Pm> pal, Pm c)
    {
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i < pal.Count; i++) { double d = Dist(pal[i], c); if (d < bd) { bd = d; best = i; } }
        return best;
    }

    public sealed class Mapper
    {
        private readonly Pm[] _pal;
        private readonly Dictionary<uint, byte> _cache = new();
        public Mapper(Pm[] pal) { _pal = pal; }

        byte Nearest(Pm c)
        {
            uint key = (uint)Math.Clamp((int)Math.Round(c.R), 0, 255) | (uint)Math.Clamp((int)Math.Round(c.G), 0, 255) << 8 |
                       (uint)Math.Clamp((int)Math.Round(c.B), 0, 255) << 16 | (uint)Math.Clamp((int)Math.Round(c.A), 0, 255) << 24;
            if (_cache.TryGetValue(key, out byte hit)) return hit;
            int best = 1; double bd = double.MaxValue;
            for (int i = 1; i < _pal.Length; i++) { double d = Dist(_pal[i], c); if (d < bd) { bd = d; best = i; } }
            _cache[key] = (byte)best;
            return (byte)best;
        }

        /// <summary>Palette index per pixel; optional Floyd–Steinberg error diffusion (strength 0..1) inside the shape.</summary>
        public byte[] Map(Img img, double dither)
        {
            int w = img.W, h = img.H;
            var p = img.Bgra;
            var idx = new byte[w * h];
            var err = dither > 0 ? new double[2, w + 2, 4] : null;
            for (int y = 0; y < h; y++)
            {
                // Row y reads the errors pushed into slot (y & 1) by the row above; clear the other slot for row y + 1.
                if (err != null) for (int x = 0; x < w + 2; x++) for (int c = 0; c < 4; c++) err[(y + 1) & 1, x, c] = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (p[i + 3] == 0) { idx[y * w + x] = 0; continue; }
                    var c = ToPm(p, i);
                    if (err != null)
                    {
                        int row = y & 1;
                        double a = Math.Clamp(c.A + err[row, x + 1, 3], 1, 255);
                        c = new Pm(Math.Clamp(c.R + err[row, x + 1, 0], 0, a), Math.Clamp(c.G + err[row, x + 1, 1], 0, a),
                                   Math.Clamp(c.B + err[row, x + 1, 2], 0, a), a);
                    }
                    byte n = Nearest(c);
                    idx[y * w + x] = n;
                    if (err != null)
                    {
                        var q = _pal[n];
                        double[] e = { (c.R - q.R) * dither, (c.G - q.G) * dither, (c.B - q.B) * dither, (c.A - q.A) * dither };
                        int cur = y & 1, nxt = (y + 1) & 1;
                        for (int ch = 0; ch < 4; ch++)
                        {
                            err[cur, x + 2, ch] += e[ch] * 7 / 16;
                            err[nxt, x, ch] += e[ch] * 3 / 16;
                            err[nxt, x + 1, ch] += e[ch] * 5 / 16;
                            err[nxt, x + 2, ch] += e[ch] * 1 / 16;
                        }
                    }
                }
            }
            return idx;
        }
    }
}

static class Png
{
    public static byte[] EncodeIndexed(int w, int h, byte[] idx, Quantizer.Pm[] pal)
    {
        var plte = new byte[pal.Length * 3];
        var trns = new List<byte>();
        for (int i = 0; i < pal.Length; i++)
        {
            var (r, g, b, a) = Quantizer.Straight(pal[i]);
            plte[i * 3] = r; plte[i * 3 + 1] = g; plte[i * 3 + 2] = b;
            trns.Add(a);
        }
        while (trns.Count > 0 && trns[^1] == 255) trns.RemoveAt(trns.Count - 1);

        var raw = new byte[(w + 1) * h];
        for (int y = 0; y < h; y++) { raw[y * (w + 1)] = 0; Array.Copy(idx, y * w, raw, y * (w + 1) + 1, w); }
        return Assemble(w, h, 3, raw, plte, trns.ToArray());
    }

    /// <summary>Lossless 32-bit RGBA with a per-row filter choice and maximum compression.</summary>
    public static byte[] EncodeRgba(Img img)
    {
        int w = img.W, h = img.H, stride = w * 4;
        var rgba = new byte[stride * h];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = img.Bgra[i + 2]; rgba[i + 1] = img.Bgra[i + 1]; rgba[i + 2] = img.Bgra[i]; rgba[i + 3] = img.Bgra[i + 3]; }
        var raw = new byte[(stride + 1) * h];
        var cand = new byte[5][];
        for (int f = 0; f < 5; f++) cand[f] = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int best = 0; long bestSum = long.MaxValue;
            for (int f = 0; f < 5; f++)
            {
                long sum = 0;
                for (int x = 0; x < stride; x++)
                {
                    int cur = rgba[y * stride + x];
                    int left = x >= 4 ? rgba[y * stride + x - 4] : 0;
                    int up = y > 0 ? rgba[(y - 1) * stride + x] : 0;
                    int ul = x >= 4 && y > 0 ? rgba[(y - 1) * stride + x - 4] : 0;
                    int pred = f switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, ul) };
                    byte v = (byte)(cur - pred);
                    cand[f][x] = v;
                    sum += v < 128 ? v : 256 - v;
                }
                if (sum < bestSum) { bestSum = sum; best = f; }
            }
            raw[y * (stride + 1)] = (byte)best;
            Array.Copy(cand[best], 0, raw, y * (stride + 1) + 1, stride);
        }
        return Assemble(w, h, 6, raw, null, null);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static byte[] Assemble(int w, int h, byte colorType, byte[] raw, byte[]? plte, byte[]? trns)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = colorType;
        Chunk(ms, "IHDR", ihdr);
        if (plte != null) Chunk(ms, "PLTE", plte);
        if (trns is { Length: > 0 }) Chunk(ms, "tRNS", trns);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, td, 0);
        data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(td));
        s.Write(crc);
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
