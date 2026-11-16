using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Assistail
{
    /// <summary>Lets the user pick the open-chat and next-trick shortcuts by pressing them.</summary>
    public partial class ShortcutsDialog : Window
    {
        /// <summary>Tries to apply (chatSetting, trickSetting); returns an error message or null.</summary>
        private readonly Func<string, string, string?> _apply;
        private string _chat, _trick;   // setting values: "" = default, "None" = off, or a combination

        public ShortcutsDialog(string buddyName, string chatSetting, string trickSetting,
                               string chatActive, string trickActive, Func<string, string, string?> apply)
        {
            InitializeComponent();
            _apply = apply;
            _chat = chatSetting;
            _trick = trickSetting;
            ChatLabel.Text = $"Chat with {buddyName}";
            TrickLabel.Text = $"{buddyName}'s next trick";
            ChatBox.Text = Describe(chatSetting, chatActive);
            TrickBox.Text = Describe(trickSetting, trickActive);
            MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !(Keyboard.FocusedElement is TextBox)) DialogResult = false; };
        }

        private static string Describe(string setting, string active) =>
            string.Equals(setting, "None", StringComparison.OrdinalIgnoreCase) ? "Off"
            : active.Length > 0 ? active
            : setting.Length > 0 ? setting
            : "(not set)";

        /// <summary>Captures the pressed combination into the focused box.</summary>
        private void KeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var box = (TextBox)sender;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;   // wait for the main key

            uint mods = 0;
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= Native.MOD_WIN;
            bool isFKey = key >= Key.F1 && key <= Key.F24;
            if (mods == 0 && !isFKey) { ShowStatus("Add Ctrl, Alt, Shift or Win, so normal typing isn't affected."); return; }
            if (mods == Native.MOD_SHIFT && !isFKey) { ShowStatus("Shift alone isn't enough; add Ctrl, Alt or Win."); return; }

            string text = Hotkey.Format(mods, key);
            box.Text = text;
            if (box == ChatBox) _chat = text; else _trick = text;
            Status.Visibility = Visibility.Collapsed;
        }

        private void Off_Click(object sender, RoutedEventArgs e)
        {
            if ((string)((Button)sender).Tag == "chat") { _chat = "None"; ChatBox.Text = "Off"; }
            else { _trick = "None"; TrickBox.Text = "Off"; }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _chat = ""; _trick = "";
            ChatBox.Text = Settings.ChatHotkeyDefaults[0] + " (default)";
            TrickBox.Text = Settings.TrickHotkeyDefaults[0] + " (default)";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_chat.Length > 0 && _chat == _trick && _chat != "None") { ShowStatus("The two shortcuts must be different."); return; }
            string? error = _apply(_chat, _trick);
            if (error != null) { ShowStatus(error); return; }
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ShowStatus(string message)
        {
            Status.Text = message;
            Status.Visibility = Visibility.Visible;
        }
    }
}
