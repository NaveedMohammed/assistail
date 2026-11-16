using System;
using System.Runtime.InteropServices;

namespace Assistail
{
    /// <summary>
    /// A native per-pixel-alpha layered window (UpdateLayeredWindow). Callers draw premultiplied BGRA pixels
    /// into <see cref="Buffer"/> and call <see cref="Push"/>. Transparent pixels are click-through.
    /// A plain Win32 window on purpose: WPF strips WS_EX_LAYERED from its own windows.
    /// </summary>
    public sealed class LayeredSurface : IDisposable
    {
        public delegate IntPtr MessageHandler(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled);

        private static readonly System.Collections.Generic.HashSet<string> Registered = new();
        private readonly Native.WndProcDelegate _wndProc;   // kept alive for the window's lifetime
        private readonly MessageHandler? _handler;
        private IntPtr _memDc, _dib, _oldBmp, _bits;
        private bool _loggedError;

        public IntPtr Hwnd { get; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public uint[] Buffer { get; private set; } = Array.Empty<uint>();
        public bool Visible { get; private set; }

        public LayeredSurface(string className, MessageHandler? handler = null)
        {
            _handler = handler;
            _wndProc = WndProc;
            var hInstance = Native.GetModuleHandle(null);
            if (Registered.Add(className))
            {
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
                    lpfnWndProc = _wndProc,
                    hInstance = hInstance,
                    lpszClassName = className,
                };
                Native.RegisterClassEx(ref wc);
            }
            Hwnd = Native.CreateWindowEx((int)(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST),
                className, className, Native.WS_POPUP, -10000, -10000, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero) App.Log($"CreateWindowEx({className}) failed: {Marshal.GetLastWin32Error()}");
            // Route this window's messages to this instance (the class may be shared).
            Instances[Hwnd] = this;
        }

        private static readonly System.Collections.Generic.Dictionary<IntPtr, LayeredSurface> Instances = new();

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var target = Instances.TryGetValue(hwnd, out var s) ? s : this;
            if (target._handler != null)
            {
                bool handled = false;
                IntPtr r = IntPtr.Zero;
                try { r = target._handler(hwnd, (int)msg, wParam, lParam, ref handled); }
                catch (Exception ex) { App.Log("Layered window message error: " + ex); }
                if (handled) return r;
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        /// <summary>Allocates the drawing buffer for the given size (no-op if unchanged).</summary>
        public void EnsureSize(int w, int h)
        {
            if (w == Width && h == Height && _dib != IntPtr.Zero) return;
            Free();
            Width = Math.Max(1, w);
            Height = Math.Max(1, h);
            Buffer = new uint[Width * Height];
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = Width, biHeight = -Height,   // top-down
                biPlanes = 1, biBitCount = 32,
            };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            _memDc = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            _dib = Native.CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _oldBmp = Native.SelectObject(_memDc, _dib);
        }

        public void Clear() => Array.Clear(Buffer);

        /// <summary>Copies the buffer to the window at (x, y) physical pixels with an overall opacity.</summary>
        public void Push(int x, int y, byte alpha = 255)
        {
            if (_dib == IntPtr.Zero) return;
            Marshal.Copy((int[])(object)Buffer, 0, _bits, Buffer.Length);
            var pos = new Native.POINT { X = x, Y = y };
            var size = new Native.SIZE { cx = Width, cy = Height };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            bool ok = Native.UpdateLayeredWindow(Hwnd, screen, ref pos, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            Native.ReleaseDC(IntPtr.Zero, screen);
            if (!ok && !_loggedError)
            {
                _loggedError = true;
                App.Log($"UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
            }
        }

        public void Show()
        {
            Visible = true;
            Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
            KeepOnTop();
        }

        public void Hide()
        {
            Visible = false;
            Native.ShowWindow(Hwnd, Native.SW_HIDE);
        }

        public void KeepOnTop() =>
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        /// <summary>Alpha-blends a premultiplied sprite (srcW×srcH) into the buffer at (dx, dy), optionally mirrored.</summary>
        public void Blit(uint[] src, int srcW, int srcH, int dx, int dy, bool mirror = false, int mirrorAxis2 = 0)
        {
            int w = Width, h = Height;
            uint[] dst = Buffer;
            for (int sy = 0; sy < srcH; sy++)
            {
                int ty = sy + dy;
                if ((uint)ty >= (uint)h) continue;
                int srow = sy * srcW, trow = ty * w;
                for (int sx = 0; sx < srcW; sx++)
                {
                    uint s = src[srow + sx];
                    uint a = s >> 24;
                    if (a == 0) continue;
                    int tx = sx + dx;
                    if (mirror) tx = mirrorAxis2 - tx - 1;
                    if ((uint)tx >= (uint)w) continue;
                    int i = trow + tx;
                    dst[i] = a == 255 ? s : Over(s, dst[i], a);
                }
            }
        }

        private static uint Over(uint s, uint d, uint a)
        {
            uint inv = 255 - a;
            uint rb = ((d & 0x00FF00FF) * inv >> 8) & 0x00FF00FF;
            uint ag = ((d >> 8) & 0x00FF00FF) * inv & 0xFF00FF00;
            return s + rb + ag;
        }

        private void Free()
        {
            if (_memDc != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _oldBmp);
                Native.DeleteObject(_dib);
                Native.DeleteDC(_memDc);
            }
            _memDc = _dib = _oldBmp = _bits = IntPtr.Zero;
        }

        public void Dispose()
        {
            Free();
            Instances.Remove(Hwnd);
            if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
        }
    }
}
