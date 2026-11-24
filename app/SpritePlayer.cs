using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Assistail
{
    public class LionAnim
    {
        public string State { get; set; } = "";
        public int Frames { get; set; }
        public int Fps { get; set; } = 15;
        public bool Loop { get; set; }
        public double BaselineY { get; set; }   // paw line, fraction of image height
        public double CenterX { get; set; }     // body centre, fraction of image width
        public double ShadowW { get; set; }     // shadow width, fraction of image width
        public double LiftPerUnit { get; set; }
        public double[] Lift { get; set; } = Array.Empty<double>();
    }

    /// <summary>
    /// Loads a buddy's pre-rendered frames (the built-in lion or a buddy pack, see <see cref="Buddy"/>) and decodes
    /// them at display size. Only "idle" plus the current animation stay decoded, to keep memory low.
    /// </summary>
    public sealed class SpritePlayer
    {
        private readonly Dictionary<string, LionAnim> _anims = new();
        private readonly Dictionary<string, uint[][]> _decoded = new();
        private int _decodeWidth;
        public Buddy Buddy { get; }

        public SpritePlayer(Buddy buddy)
        {
            Buddy = buddy;
            using var doc = buddy.OpenAnims();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var e = p.Value;
                _anims[p.Name] = new LionAnim
                {
                    State = p.Name,
                    Frames = e.GetProperty("frames").GetInt32(),
                    Fps = e.GetProperty("fps").GetInt32(),
                    Loop = e.GetProperty("loop").GetBoolean(),
                    BaselineY = e.GetProperty("baseline_y").GetDouble(),
                    CenterX = e.GetProperty("center_x").GetDouble(),
                    ShadowW = e.GetProperty("shadow_w").GetDouble(),
                    LiftPerUnit = e.GetProperty("lift_per_unit").GetDouble(),
                    Lift = JsonSerializer.Deserialize<double[]>(e.GetProperty("lift").GetRawText()) ?? Array.Empty<double>(),
                };
            }
        }

        public LionAnim this[string state] => _anims[state];
        public bool Has(string state) => _anims.ContainsKey(state);

        /// <summary>Sets the pixel width frames are decoded at (display size × DPI); clears the cache when it changes.</summary>
        public void SetDecodeWidth(int px)
        {
            px = Math.Clamp(px, 64, 512);
            if (px == _decodeWidth) return;
            _decodeWidth = px;
            _decoded.Clear();
        }

        public int DecodeWidth => _decodeWidth > 0 ? _decodeWidth : 256;

        /// <summary>Premultiplied BGRA pixels (DecodeWidth × DecodeWidth) of one frame.</summary>
        public uint[] Frame(string state, int index)
        {
            if (!_decoded.TryGetValue(state, out var frames))
            {
                // Keep idle + the requested state only.
                foreach (var key in new List<string>(_decoded.Keys))
                    if (key != "idle") _decoded.Remove(key);
                frames = new uint[_anims[state].Frames][];
                _decoded[state] = frames;
            }
            index = Math.Clamp(index, 0, frames.Length - 1);
            return frames[index] ??= Load(state, index);
        }

        private uint[] Load(string state, int index)
        {
            var bmp = new BitmapImage();
            try
            {
                using var stream = Buddy.OpenImage($"{state}/f_{index + 1:D4}.png");
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.DecodePixelWidth = DecodeWidth;
                bmp.DecodePixelHeight = DecodeWidth;
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // decoded now, so the stream can close
                bmp.EndInit();
            }
            catch (Exception ex)
            {
                App.Log($"Frame {state}/{index + 1} of {Buddy.Id} couldn't be loaded: {ex.Message}");
                return new uint[DecodeWidth * DecodeWidth];   // a blank frame rather than a crash
            }
            var pbgra = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            int w = pbgra.PixelWidth, h = pbgra.PixelHeight;
            var px = new uint[DecodeWidth * DecodeWidth];
            var tmp = new uint[w * h];
            pbgra.CopyPixels(tmp, w * 4, 0);
            for (int y = 0; y < Math.Min(h, DecodeWidth); y++)
                Array.Copy(tmp, y * w, px, y * DecodeWidth, Math.Min(w, DecodeWidth));
            return px;
        }
    }
}
