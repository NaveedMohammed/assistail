using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Assistail
{
    /// <summary>Small "what would you like to call me?" box shown next to the lion.</summary>
    public partial class NameDialog : Window
    {
        public string? ChosenName { get; private set; }

        public NameDialog(string current, bool firstTime)
        {
            InitializeComponent();
            NameBox.Text = current;
            if (!firstTime)
            {
                Heading.Text = "Rename your buddy";
                Sub.Text = "The new name shows up everywhere right away.";
                CancelButton.Content = "Cancel";
            }
            Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }

        /// <summary>The same box reused for "Make … say something".</summary>
        public static NameDialog ForSay(string buddyName)
        {
            var d = new NameDialog("", firstTime: false);
            d.Title = $"Make {buddyName} say something";
            d.Heading.Text = $"What should {buddyName} say?";
            d.Sub.Text = "It appears in a speech bubble above him.";
            d.NameBox.MaxLength = 200;
            d.OkButton.Content = "Say it";
            d.CancelButton.Content = "Cancel";
            return d;
        }

        /// <summary>Positions the box just above the given rectangle (physical pixels), kept on screen.</summary>
        public void PlaceAbove(System.Drawing.Rectangle anchorPx, System.Drawing.Rectangle workAreaPx)
        {
            SourceInitialized += (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                double w = Width * dpi.DpiScaleX, h = 200 * dpi.DpiScaleY;
                double x = anchorPx.Left + anchorPx.Width / 2.0 - w / 2;
                double y = anchorPx.Top - h;
                x = Math.Clamp(x, workAreaPx.Left, workAreaPx.Right - w);
                y = Math.Clamp(y, workAreaPx.Top, workAreaPx.Bottom - h);
                Left = x / dpi.DpiScaleX;
                Top = y / dpi.DpiScaleY;
            };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (name.Length == 0) { NameBox.Focus(); return; }
            ChosenName = name;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
