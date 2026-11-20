using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Assistail.Extras
{
    /// <summary>
    /// B5 levels 4 and 5: a big paw on the screen, in its own see-through window (like the butterfly).
    /// 4 "sign": the arm slides in from the right holding a wooden board with the reminder written on it, wiggles,
    /// holds, slides out. 5 "boop": the paw rises from the bottom of the screen and boops the glass, leaving a paw
    /// print with the message under it that fades away. Only the paw, board, print and message catch clicks
    /// (fully transparent pixels pass through), it never takes focus, and it only animates while it's on screen.
    /// The frames come from the buddy's asset folder (lion/paw.json), so another buddy can bring its own paw.
    /// </summary>
    public sealed class PawOverlay : IDisposable
    {
        private const string Buddy = "lion";            // asset folder of the current buddy (B1: per buddy)
        private const double SignHoldSeconds = 9, BoopHoldSeconds = 6, FadeSeconds = 1.2;
        /// <summary>Where the pads touch the glass in the boop frames (fraction of the frame height, from paw_boop/print.png).</summary>
        private const double BoopPadsY = 0.32;

        private sealed class Anim { public string Folder = ""; public int Frames; public int PressFrame, ReleaseFrame; public Rect TextArea; }
        private readonly Dictionary<string, Anim> _anims = new();
        private readonly LayeredSurface _surface;
        private readonly DispatcherTimer _timer;
        private readonly IntPtr _handCursor = Native.LoadCursor(IntPtr.Zero, 32649);

        private enum Phase { None, SignIn, SignWiggle, SignHold, SignOut, BoopPlay, BoopHold, Fade }
        private Phase _phase;
        private int _frame;
        private DateTime _phaseStart;
        private string _tag = "";
        private bool _acted;
        private int _x, _y, _w, _h;     // the window (physical px)
        private int _fw, _fh;           // the frames drawn in it (the boop window is taller: the arm reaches the screen's edge)
        private uint[]? _overlay;       // sign text / boop print + message, composited over (sign) or under (boop) the paw
        private uint[]? _print;
        private bool _showPrint, _showMessage;
        // Decoded frames, keyed "anim/index". A background thread decodes a few frames ahead of the one on screen;
        // the boop's frames are dropped once shown (a 740 px boop is 90 MB fully decoded), the sign's stay while it's up.
        private readonly ConcurrentDictionary<string, uint[]> _frames = new();
        private System.Threading.CancellationTokenSource? _prefetch;
        private volatile int _playhead;
        private const int Lookahead = 6;
        private uint[]? _printFaded;
        private string _text = "", _hint = "";

        public bool Available => _anims.Count > 0;
        public bool Busy => _phase != Phase.None;

        /// <summary>The paw was clicked (left: done, right: snooze); passes the message tag.</summary>
        public event Action<string>? Clicked, RightClicked;
        /// <summary>The paw is gone; passes the tag and whether the user clicked it.</summary>
        public event Action<string, bool>? Finished;
        /// <summary>The boop touched the glass (the buddy can react).</summary>
        public event Action? Booped;

        public PawOverlay()
        {
            _surface = new LayeredSurface("AssistailPaw", WndProc);
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(1000.0 / 15) };
            _timer.Tick += (_, _) => Tick();
            try
            {
                var res = Application.GetResourceStream(new Uri($"pack://application:,,,/{Buddy}/paw.json"));
                if (res == null) return;
                using var doc = JsonDocument.Parse(res.Stream);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var e = p.Value;
                    var a = new Anim { Folder = e.GetProperty("folder").GetString() ?? "", Frames = e.GetProperty("frames").GetInt32() };
                    if (e.TryGetProperty("press_frame", out var pf)) a.PressFrame = pf.GetInt32();
                    if (e.TryGetProperty("release_frame", out var rf)) a.ReleaseFrame = rf.GetInt32();
                    if (e.TryGetProperty("text_area", out var ta))
                        a.TextArea = new Rect(ta.GetProperty("x").GetDouble(), ta.GetProperty("y").GetDouble(), ta.GetProperty("w").GetDouble(), ta.GetProperty("h").GetDouble());
                    _anims[p.Name] = a;
                }
            }
            catch (Exception ex) { App.Log("Paw assets couldn't be loaded: " + ex.Message); _anims.Clear(); }
        }

        // ================================================================== showing

        /// <summary>Level 4: the sign. <paramref name="screen"/> and <paramref name="work"/> are the monitor's bounds and work area (physical px).</summary>
        public bool ShowSign(string text, string? hint, string tag, System.Drawing.Rectangle screen, System.Drawing.Rectangle work)
        {
            if (!_anims.ContainsKey("sign_in") || !_anims.ContainsKey("sign_wiggle")) return false;
            Stop(raise: true);
            _w = (int)(screen.Width * 0.40);
            _h = _w / 2;
            _x = screen.Right - (int)(_w * 0.93);                   // part of the arm stays off-screen
            _y = screen.Top + (int)(screen.Height * 0.34);
            _y = Math.Min(_y, work.Bottom - _h);
            _fw = _w; _fh = _h;
            Begin(text, hint, tag);
            _overlay = RenderSignText(_anims["sign_in"].TextArea);
            StartPrefetch(new[] { "sign_in", "sign_wiggle" }, dropShown: false);
            SetPhase(Phase.SignIn);
            return true;
        }

        /// <summary>Level 5: the boop, centred above the taskbar of that monitor.</summary>
        public bool ShowBoop(string text, string? hint, string tag, System.Drawing.Rectangle screen, System.Drawing.Rectangle work)
        {
            if (!_anims.ContainsKey("boop")) return false;
            Stop(raise: true);
            _fw = _fh = _w = (int)Math.Min(screen.Height * 0.62, 820);
            _x = screen.Left + (screen.Width - _w) / 2;
            _y = screen.Top + screen.Height / 2 - (int)(BoopPadsY * _fh);   // the pads press at the centre of the screen
            _h = screen.Bottom - _y;                                // the arm reaches down to the screen's bottom edge
            Begin(text, hint, tag);
            _print = _anims.ContainsKey("print") ? Decode(Uri("print", 0), _fw, _fh) : null;
            _printFaded = _print != null ? Faded(_print, 0.5) : null;
            _overlay = RenderBoopMessage();
            StartPrefetch(new[] { "boop" }, dropShown: true);
            _showPrint = _showMessage = false;
            SetPhase(Phase.BoopPlay);
            return true;
        }

        private void Begin(string text, string? hint, string tag)
        {
            _text = text.Trim();
            _hint = hint ?? "";
            _tag = tag;
            _acted = false;
            CancelPrefetch();
            _frames.Clear();
            _surface.EnsureSize(_w, _h);
            _surface.Show();
            _timer.Start();
            App.Log($"Paw: {tag} at {_x},{_y} {_w}x{_h}");
        }

        private void SetPhase(Phase p)
        {
            _phase = p;
            _frame = 0;
            _phaseStart = DateTime.Now;
        }

        /// <summary>Takes the paw away now (another message, quitting). Raises Finished if <paramref name="raise"/>.</summary>
        public void Stop(bool raise)
        {
            if (_phase == Phase.None) return;
            string tag = _tag;
            bool acted = _acted;
            _phase = Phase.None;
            _timer.Stop();
            _surface.Hide();
            CancelPrefetch();
            _frames.Clear();
            _overlay = _print = _printFaded = null;
            if (raise) Finished?.Invoke(tag, acted);
        }

        // ================================================================== animation

        private void Tick()
        {
            double t = (DateTime.Now - _phaseStart).TotalSeconds;
            int at = (int)(t * 15);                              // frames follow the clock: a slow moment skips, never drags
            switch (_phase)
            {
                case Phase.SignIn:
                {
                    int n = _anims["sign_in"].Frames;
                    _playhead = Math.Min(at, n - 1);
                    Draw("sign_in", Math.Min(at, n - 1), text: false);
                    if (at >= n - 1) SetPhase(Phase.SignWiggle);
                    break;
                }
                case Phase.SignWiggle:
                {
                    int n = _anims["sign_wiggle"].Frames;
                    _playhead = _anims["sign_in"].Frames + Math.Min(at, n - 1);
                    Draw("sign_wiggle", Math.Min(at, n - 1), text: false);
                    if (at >= n - 1) { SetPhase(Phase.SignHold); Draw("sign_in", _anims["sign_in"].Frames - 1, text: true); }
                    break;
                }
                case Phase.SignHold:                                // nothing moves: no redraw until it's time to go
                    if (t >= SignHoldSeconds) { SetPhase(Phase.SignOut); _outFrom = _anims["sign_in"].Frames - 1; }
                    break;
                case Phase.SignOut:
                {
                    int n = _anims["sign_in"].Frames;
                    int f = Math.Max(0, _outFrom - at);
                    Draw("sign_in", f, text: false);
                    if (f <= 0) Stop(raise: true);
                    break;
                }
                case Phase.BoopPlay:
                {
                    var boop = _anims["boop"];
                    int f = Math.Min(at, boop.Frames - 1);
                    for (; _frame <= f; _frame++)                   // events of every frame reached, even skipped ones
                    {
                        if (_frame + 1 == boop.PressFrame) { _showPrint = true; Booped?.Invoke(); }
                        if (_frame + 1 == boop.ReleaseFrame) _showMessage = true;
                    }
                    _playhead = f;
                    Draw("boop", f, text: false);
                    if (at >= boop.Frames - 1) { SetPhase(Phase.BoopHold); DrawBoopRest(); }
                    break;
                }
                case Phase.BoopHold:
                    if (t >= BoopHoldSeconds) SetPhase(Phase.Fade);
                    break;
                case Phase.Fade:
                    double u = Math.Clamp(t / FadeSeconds, 0, 1);
                    _surface.Push(_x, _y, (byte)(255 * (1 - u)));
                    if (u >= 1) Stop(raise: true);
                    break;
            }
        }

        private int _outFrom;   // the sign slides out from this sign_in frame (the last one, or where it was when clicked)

        private void Draw(string anim, int frame, bool text)
        {
            _surface.Clear();
            bool boop = anim == "boop";
            if (boop) DrawUnder();
            var px = Frame(anim, frame);
            _surface.Blit(px, _fw, _fh, 0, 0);
            if (boop && _h > _fh) ExtendArm(px);
            if (text && _overlay != null) _surface.Blit(_overlay, _w, _h, 0, 0);
            _surface.Push(_x, _y);
            _surface.KeepOnTop();
        }

        /// <summary>Below the frame, repeats its bottom row so the arm reaches the bottom of the screen.</summary>
        private void ExtendArm(uint[] frame)
        {
            uint[] dst = _surface.Buffer;
            int last = (_fh - 1) * _fw;
            for (int x = 0; x < _fw && x < _w; x++)
            {
                uint c = frame[last + x];
                if ((c >> 24) < 8) continue;
                for (int y = _fh; y < _h; y++) dst[y * _w + x] = c;
            }
        }

        /// <summary>The boop's print (half see-through) and message sit under the paw.</summary>
        private void DrawUnder()
        {
            if (_showPrint && _printFaded != null) _surface.Blit(_printFaded, _fw, _fh, 0, 0);
            if (_showMessage && _overlay != null) _surface.Blit(_overlay, _w, _h, 0, 0);
        }

        private void DrawBoopRest()
        {
            _surface.Clear();
            _showPrint = _showMessage = true;
            DrawUnder();
            _surface.Push(_x, _y);
        }

        private static uint[] Faded(uint[] src, double opacity)
        {
            uint k = (uint)(opacity * 256);
            var tmp = new uint[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                uint s = src[i];
                if (s == 0) continue;
                uint rb = ((s & 0x00FF00FF) * k >> 8) & 0x00FF00FF;
                uint ag = (((s >> 8) & 0x00FF00FF) * k) & 0xFF00FF00;
                tmp[i] = rb | ag;
            }
            return tmp;
        }

        // ================================================================== frames

        private string Uri(string anim, int index) => $"pack://application:,,,/{Buddy}/{_anims[anim].Folder}/f_{index + 1:D4}.png";

        private uint[] Frame(string anim, int index)
        {
            index = Math.Clamp(index, 0, _anims[anim].Frames - 1);
            string key = anim + "/" + index;
            if (_frames.TryGetValue(key, out var px)) return px;
            px = Decode(Uri(anim, index), _fw, _fh);              // not decoded ahead yet: do it now
            _frames[key] = px;
            return px;
        }

        /// <summary>Decodes the animations' frames in order on a background thread, staying a few frames ahead.</summary>
        private void StartPrefetch(string[] anims, bool dropShown)
        {
            CancelPrefetch();
            var cts = _prefetch = new System.Threading.CancellationTokenSource();
            var jobs = new List<(string Key, string Uri, int Seq)>();
            int seq = 0;
            foreach (var a in anims)
                for (int i = 0; i < _anims[a].Frames; i++) jobs.Add((a + "/" + i, Uri(a, i), seq++));
            int w = _fw, h = _fh;
            _playhead = 0;
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var (key, uri, s) in jobs)
                {
                    while (!cts.IsCancellationRequested && s > _playhead + Lookahead) System.Threading.Thread.Sleep(15);
                    if (cts.IsCancellationRequested) return;
                    if (_frames.ContainsKey(key)) continue;
                    try { _frames[key] = Decode(uri, w, h); }
                    catch (Exception ex) { App.Log("Paw frame decode failed: " + ex.Message); return; }
                    if (dropShown)   // the boop only plays forwards: forget frames already shown
                        foreach (var k in _frames.Keys)
                            if (int.TryParse(k[(k.IndexOf('/') + 1)..], out int i) && i < _playhead - 1) _frames.TryRemove(k, out _);
                }
            });
        }

        private void CancelPrefetch()
        {
            _prefetch?.Cancel();
            _prefetch = null;
        }

        private static uint[] Decode(string uri, int w, int h)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(uri);
            bmp.DecodePixelWidth = w;
            bmp.DecodePixelHeight = h;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            var pbgra = new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
            pbgra.Freeze();
            var px = new uint[w * h];
            int pw = Math.Min(w, pbgra.PixelWidth), ph = Math.Min(h, pbgra.PixelHeight);
            var tmp = new uint[pbgra.PixelWidth * pbgra.PixelHeight];
            pbgra.CopyPixels(tmp, pbgra.PixelWidth * 4, 0);
            for (int y = 0; y < ph; y++) Array.Copy(tmp, y * pbgra.PixelWidth, px, y * w, pw);
            return px;
        }

        // ================================================================== text (drawn with WPF, then blended in)

        private uint[] RenderSignText(Rect area)
        {
            var box = new Rect(area.X * _fw, area.Y * _fh, area.Width * _fw, area.Height * _fh);
            return RenderVisual(dc =>
            {
                var ink = new SolidColorBrush(Color.FromRgb(0x4A, 0x2E, 0x15));
                var soft = new SolidColorBrush(Color.FromRgb(0x6B, 0x4A, 0x2A));
                double size = box.Height * 0.2;
                FormattedText main, small;
                do   // shrink until the text fits the board
                {
                    main = Text(_text, size, ink, FontWeights.Bold, box.Width);
                    small = Text(_hint, size * 0.6, soft, FontWeights.Normal, box.Width);
                    if (main.Height + (_hint.Length > 0 ? small.Height + size * 0.25 : 0) <= box.Height) break;
                    size *= 0.9;
                } while (size > 9);
                double total = main.Height + (_hint.Length > 0 ? small.Height + size * 0.25 : 0);
                double y = box.Y + (box.Height - total) / 2;
                dc.DrawText(main, new Point(box.X, y));
                if (_hint.Length > 0) dc.DrawText(small, new Point(box.X, y + main.Height + size * 0.25));
            });
        }

        private uint[] RenderBoopMessage()
        {
            return RenderVisual(dc =>
            {
                double size = Math.Max(14, _fh * 0.03);
                var main = Text(_text, size, new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), FontWeights.SemiBold, _w * 0.82);
                var small = Text(_hint, size * 0.72, new SolidColorBrush(Color.FromRgb(0x8A, 0x86, 0x7E)), FontWeights.Normal, _w * 0.82);
                double pad = size * 0.7;
                double tw = Math.Max(main.WidthIncludingTrailingWhitespace, _hint.Length > 0 ? small.WidthIncludingTrailingWhitespace : 0);
                double th = main.Height + (_hint.Length > 0 ? small.Height + size * 0.2 : 0);
                var rect = new Rect((_w - tw) / 2 - pad, _fh * 0.56, tw + pad * 2, th + pad * 1.4);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, new Rect(rect.X + 2, rect.Y + 4, rect.Width, rect.Height), size, size);
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xDF, 0xD5)), 1), rect, size, size);
                main.TextAlignment = TextAlignment.Center;
                small.TextAlignment = TextAlignment.Center;
                dc.DrawText(main, new Point(_w / 2.0 - main.MaxTextWidth / 2, rect.Y + pad * 0.7));
                if (_hint.Length > 0) dc.DrawText(small, new Point(_w / 2.0 - small.MaxTextWidth / 2, rect.Y + pad * 0.7 + main.Height + size * 0.2));
            });
        }

        private static FormattedText Text(string s, double size, Brush brush, FontWeight weight, double maxWidth)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Segoe UI Emoji"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1.0)
            { MaxTextWidth = Math.Max(10, maxWidth), TextAlignment = TextAlignment.Center };
            return ft;
        }

        /// <summary>Draws with WPF at physical pixels (96 dpi = 1:1) into a premultiplied buffer the size of the window.</summary>
        private uint[] RenderVisual(Action<DrawingContext> draw)
        {
            var dv = new DrawingVisual();
            TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Grayscale);
            using (var dc = dv.RenderOpen()) draw(dc);
            var rtb = new RenderTargetBitmap(_w, _h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new uint[_w * _h];
            rtb.CopyPixels(px, _w * 4, 0);
            return px;
        }

        // ================================================================== clicks

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SETCURSOR = 0x20, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    handled = true;
                    return new IntPtr(MA_NOACTIVATE);       // never takes focus from what you're doing
                case WM_SETCURSOR:
                    Native.SetCursor(_handCursor);
                    handled = true;
                    return new IntPtr(1);
                case WM_LBUTTONUP:
                case WM_RBUTTONUP:
                    handled = true;
                    if (_phase == Phase.None) break;
                    _acted = true;
                    string tag = _tag;
                    if (msg == WM_LBUTTONUP) Clicked?.Invoke(tag); else RightClicked?.Invoke(tag);
                    GoAway();
                    break;
            }
            return IntPtr.Zero;
        }

        /// <summary>After a click: the sign slides out, the boop's print fades.</summary>
        private void GoAway()
        {
            switch (_phase)
            {
                case Phase.SignIn or Phase.SignWiggle or Phase.SignHold:
                    int n = _anims["sign_in"].Frames;
                    int from = _phase == Phase.SignIn ? Math.Min(n - 1, (int)((DateTime.Now - _phaseStart).TotalSeconds * 15)) : n - 1;
                    SetPhase(Phase.SignOut);
                    _outFrom = from;                                // slide out from where it is
                    break;
                case Phase.BoopPlay or Phase.BoopHold:
                    SetPhase(Phase.Fade);
                    break;
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _surface.Dispose();
        }
    }
}
