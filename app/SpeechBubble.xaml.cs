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
        /// <summary>Right-clicked by the user (raised before the bubble hides).</summary>
        public event Action? RightClicked;

        public SpeechBubble()
        {
            InitializeComponent();
            _hideTimer.Tick += (_, _) => Dismiss();
            MouseLeftButtonUp += (_, _) => { Clicked?.Invoke(); Dismiss(); };
            MouseRightButtonUp += (_, e) => { e.Handled = true; RightClicked?.Invoke(); Dismiss(); };
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
            Hint.Visibility = Visibility.Collapsed;
            SetAttention(0);
            Show();
            UpdateLayout();
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(seconds ?? Math.Clamp(2.5 + text.Length * 0.07, 2.5, 10));
            _hideTimer.Start();
        }

        /// <summary>A small grey line under the text (null hides it). Call after <see cref="Say"/>.</summary>
        public void SetHint(string? hint)
        {
            Hint.Text = hint ?? "";
            Hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
            UpdateLayout();
        }

        public double DurationSeconds => _hideTimer.Interval.TotalSeconds;

        // ------------------------------------------------------------------ B5 attention levels

        private readonly DispatcherTimer _bounceTimer = new();

        /// <summary>
        /// 0/1: plain bubble. 2: bounces in and again every 10 s while it shows. 3: bigger text, bounces every 7 s.
        /// Call after <see cref="Say"/>. The animation runs only for the bounce itself (a fraction of a second).
        /// </summary>
        public void SetAttention(int level)
        {
            _bounceTimer.Stop();
            bool big = level >= 3;
            Text.FontSize = big ? 17 : 14;
            Text.MaxWidth = Hint.MaxWidth = big ? 320 : 240;
            if (level < 2) { Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); return; }
            UpdateLayout();
            Bounce(big ? 0.55 : 0.7);
            if (_bounceHandler == null) { _bounceHandler = (_, _) => Bounce(0.88); _bounceTimer.Tick += _bounceHandler; }
            _bounceTimer.Interval = TimeSpan.FromSeconds(big ? 7 : 10);
            _bounceTimer.Start();
        }

        private EventHandler? _bounceHandler;

        private void Bounce(double from)
        {
            if (!IsVisible) { _bounceTimer.Stop(); return; }
            var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(from, System.Windows.Media.Animation.KeyTime.FromPercent(0)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.05, System.Windows.Media.Animation.KeyTime.FromPercent(0.55))
            { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
            anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromPercent(1))
            { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut } });
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        public void Dismiss()
        {
            _hideTimer.Stop();
            _bounceTimer.Stop();
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
