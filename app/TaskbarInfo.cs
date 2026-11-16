using System;

namespace Assistail
{
    /// <summary>
    /// Where the lion's "ground" is: the top edge of a bottom taskbar, otherwise the bottom of the screen.
    /// All values in physical pixels.
    /// </summary>
    internal readonly struct Ground
    {
        public int Y { get; init; }            // ground line (paws rest here)
        public int Left { get; init; }         // horizontal range the lion can sit in
        public int Right { get; init; }
        public IntPtr Monitor { get; init; }
        public double Scale { get; init; }     // DPI scale of that monitor (1.0 = 100%)
        public Native.RECT WorkArea { get; init; }
        public Native.RECT MonitorRect { get; init; }
        public bool OnTaskbar { get; init; }
    }

    internal static class TaskbarInfo
    {
        public static Ground GetGround()
        {
            var abd = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
            bool found = Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero;
            bool autoHide = (Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd).ToInt64() & 1) != 0;

            // Monitor that holds the taskbar (or the primary monitor).
            var probe = found
                ? new Native.POINT { X = (abd.rc.Left + abd.rc.Right) / 2, Y = (abd.rc.Top + abd.rc.Bottom) / 2 }
                : new Native.POINT { X = 0, Y = 0 };
            IntPtr mon = Native.MonitorFromPoint(probe, found ? Native.MONITOR_DEFAULTTONEAREST : Native.MONITOR_DEFAULTTOPRIMARY);
            var mi = Native.MonitorInfo(mon);
            double scale = Native.ScaleForMonitor(mon);

            bool bottomBar = found && abd.uEdge == Native.ABE_BOTTOM && !autoHide;
            int groundY = bottomBar ? abd.rc.Top : (autoHide && found && abd.uEdge == Native.ABE_BOTTOM ? mi.rcMonitor.Bottom - 2 : mi.rcWork.Bottom);
            return new Ground
            {
                Y = groundY,
                Left = mi.rcWork.Left,
                Right = mi.rcWork.Right,
                Monitor = mon,
                Scale = scale,
                WorkArea = mi.rcWork,
                MonitorRect = mi.rcMonitor,
                OnTaskbar = bottomBar,
            };
        }

        /// <summary>True when the foreground window covers its whole monitor (video, game, slideshow).</summary>
        public static bool IsFullscreenAppActive()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;
            string cls = Native.ClassOf(fg);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            if (!Native.GetWindowRect(fg, out var r)) return false;
            var mi = Native.MonitorInfo(Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST));
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                   r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
    }
}
