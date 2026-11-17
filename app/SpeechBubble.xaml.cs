using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Assistail
{
    /// <summary>Comic-style speech bubble shown above the lion's head. Click it to dismiss.</summary>
    public partial class SpeechBubble : Window
    {
        private readonly DispatcherTimer _hideTimer = new();
        public event Action? Hidden;
        /// <summary>Clicked by the user (raised before the bubble hides).</summary>
        public event Action? Clicked;

        public SpeechBubble()
        {
            InitializeComponent();
            _hideTimer.Tick += (_, _) => Dismiss();
            MouseLeftButtonUp += (_, _) => { Clicked?.Invoke(); Dismiss(); };
            Cursor = Cursors.Hand;
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            };
        }

        /// <summary>Shows text for <paramref name="seconds"/>, or a duration based on its length (2.5–10 s).</summary>
        public void Say(string text, double? seconds = null)
        {
            Text.Text = text;
            Show();
            UpdateLayout();
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(seconds ?? Math.Clamp(2.5 + text.Length * 0.07, 2.5, 10));
            _hideTimer.Start();
        }

        public double DurationSeconds => _hideTimer.Interval.TotalSeconds;

        public void Dismiss()
        {
            _hideTimer.Stop();
            if (IsVisible) { Hide(); Hidden?.Invoke(); }
        }

        /// <summary>Places the bubble so its tail points at (headX, headTop) in physical pixels.</summary>
        public void PlaceAt(int headX, int headTop, System.Drawing.Rectangle work)
        {
            if (!IsVisible) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)(ActualWidth * dpi.DpiScaleX), h = (int)(ActualHeight * dpi.DpiScaleY);
            int tailX = (int)(30 * dpi.DpiScaleX);
            int x = Math.Clamp(headX - tailX, work.Left, Math.Max(work.Left, work.Right - w));
            int y = Math.Max(work.Top, headTop - h);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }
}
