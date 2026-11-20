using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Assistail
{
    /// <summary>What the settings window needs from the app (wired up in App).</summary>
    public sealed class SettingsHost
    {
        /// <summary>Text of the shortcut currently registered for a hotkey id ("" if none).</summary>
        public Func<int, string> ActiveHotkey = _ => "";
        /// <summary>Registers new shortcut settings (id → "" default / "None" / combination); returns an error or null.</summary>
        public Func<Dictionary<int, string>, string?> ApplyShortcuts = _ => null;
        /// <summary>Called after Save, so the buddy, menus and chat pick up the new name/size/etc.</summary>
        public Action Applied = () => { };
        /// <summary>Asks for confirmation and adds the Claude Code hooks; true if added.</summary>
        public Func<Window, bool> InstallHooks = _ => false;
        public Func<Window, bool> RemoveHooks = _ => false;
        /// <summary>Checks for an update now and returns a one-line status ("You're up to date").</summary>
        public Func<Task<string>> CheckForUpdates = () => Task.FromResult("");
    }

    /// <summary>C5: one window for every setting. The tray menu keeps the quick toggles.</summary>
    public partial class SettingsWindow : Window
    {
        private readonly Settings _settings;
        private readonly SettingsHost _host;

        private sealed class ShortcutRow
        {
            public int Id; public string Setting = ""; public string[] Defaults = Array.Empty<string>(); public TextBox Box = null!;
        }
        private readonly List<ShortcutRow> _shortcuts = new();

        private string _size, _quirks, _model;

        public SettingsWindow(Settings settings, SettingsHost host)
        {
            InitializeComponent();
            _settings = settings;
            _host = host;
            _size = settings.LionSize;
            _quirks = settings.QuirkFrequency;
            _model = settings.Model;
            string name = settings.NameOrDefault;

            VersionText.Text = "Assistail " + UpdateService.Current.ToString(3);
            NameBox.Text = name;
            AddChoices(SizePanel, new[] { ("Small", "S"), ("Medium", "M"), ("Large", "L") }, _size, v => _size = v);
            AddChoices(QuirksPanel, new[] { ("Often", "Often"), ("Sometimes", "Sometimes"), ("Rarely", "Rarely"), ("Off", "Off") }, _quirks, v => _quirks = v);
            AddChoices(ModelPanel, new[] { ("Default", ""), ("Haiku", "haiku"), ("Sonnet", "sonnet"), ("Opus", "opus") }, _model, v => _model = v);
            SelfTalkBox.IsChecked = settings.SelfTalk;
            ReadsFolderBox.IsChecked = settings.ChatReadsFolder;

            AddShortcut(HotkeyService.ChatId, $"Chat with {name}", "Shows or hides the chat", settings.ChatHotkey, Settings.ChatHotkeyDefaults);
            AddShortcut(HotkeyService.TrickId, "Next trick", "Each press does the next trick", settings.TrickHotkey, Settings.TrickHotkeyDefaults);
            AddShortcut(HotkeyService.ClipboardId, "Ask about the clipboard", "Opens the chat with what you copied", settings.ClipboardHotkey, Settings.ClipboardHotkeyDefaults);
            AddShortcut(HotkeyService.SnipId, "Ask about a screen area", "Snip part of the screen, then ask", settings.SnipHotkey, Settings.SnipHotkeyDefaults);

            AlertsBox.IsChecked = settings.ClaudeAlerts;
            RefreshHooks();
            AutoUpdateBox.IsChecked = settings.AutoUpdateCheck;
            ExtrasOnlineBox.IsChecked = settings.ExtrasOnline;
            StartupBox.IsChecked = TrayIcon.IsStartupEnabled();

            Scroller.MaxHeight = Math.Max(380, SystemParameters.WorkArea.Height - 190);   // no scrolling on most screens
            TitleBar.MouseLeftButtonDown +=(_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox { IsReadOnly: true }) Close(); };
        }

        private static void AddChoices(StackPanel panel, (string Label, string Value)[] choices, string current, Action<string> set)
        {
            string group = panel.Name;
            foreach (var (label, value) in choices)
            {
                var rb = new RadioButton
                {
                    Content = label, GroupName = group, IsChecked = value == current,
                    Style = (Style)panel.FindResource("Seg"),
                };
                rb.Checked += (_, _) => set(value);
                panel.Children.Add(rb);
            }
        }

        // ------------------------------------------------------------------ shortcuts

        private void AddShortcut(int id, string label, string hint, string setting, string[] defaults)
        {
            int row = ShortcutGrid.RowDefinitions.Count;
            if (row > 0) { ShortcutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); row++; }
            ShortcutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
            text.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Hint") });
            Grid.SetRow(text, row);
            ShortcutGrid.Children.Add(text);

            var r = new ShortcutRow { Id = id, Setting = setting, Defaults = defaults };
            r.Box = new TextBox { Style = (Style)FindResource("KeyBox"), Text = Describe(setting, _host.ActiveHotkey(id)), VerticalAlignment = VerticalAlignment.Center };
            r.Box.PreviewKeyDown += (s, e) => CaptureKey(r, e);
            Grid.SetRow(r.Box, row); Grid.SetColumn(r.Box, 1);
            ShortcutGrid.Children.Add(r.Box);

            var off = new Button { Content = "Off", Style = (Style)FindResource("Flat"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Turn this shortcut off" };
            off.Click += (_, _) => { r.Setting = "None"; r.Box.Text = "Off"; };
            Grid.SetRow(off, row); Grid.SetColumn(off, 2);
            ShortcutGrid.Children.Add(off);
            _shortcuts.Add(r);
        }

        private static string Describe(string setting, string active) =>
            string.Equals(setting, "None", StringComparison.OrdinalIgnoreCase) ? "Off"
            : active.Length > 0 ? active
            : setting.Length > 0 ? setting
            : "(not set)";

        private void CaptureKey(ShortcutRow r, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key == Key.Tab) { r.Box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); return; }
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

            r.Setting = Hotkey.Format(mods, key);
            r.Box.Text = r.Setting;
            Status.Visibility = Visibility.Collapsed;
        }

        private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _shortcuts) { r.Setting = ""; r.Box.Text = r.Defaults[0] + " (default)"; }
        }

        // ------------------------------------------------------------------ alerts / updates (act straight away)

        private void RefreshHooks()
        {
            var state = ClaudeHooks.State();
            HooksStatus.Text = state switch
            {
                ClaudeHooks.HookState.Installed => "✓ Set up. New Claude Code sessions will report to your buddy.",
                ClaudeHooks.HookState.InstalledElsewhere => "⚠ Set up for another copy of Assistail. Click Update to point the hooks here.",
                _ => "Not set up yet.",
            };
            InstallHooksButton.Content = state switch
            {
                ClaudeHooks.HookState.Installed => "Reinstall",
                ClaudeHooks.HookState.InstalledElsewhere => "Update",
                _ => "Set up…",
            };
            RemoveHooksButton.IsEnabled = state != ClaudeHooks.HookState.NotInstalled;
            AlertsBox.IsEnabled = state != ClaudeHooks.HookState.NotInstalled;
        }

        private void InstallHooks_Click(object sender, RoutedEventArgs e)
        {
            if (_host.InstallHooks(this)) AlertsBox.IsChecked = true;
            RefreshHooks();
        }

        private void RemoveHooks_Click(object sender, RoutedEventArgs e)
        {
            _host.RemoveHooks(this);
            RefreshHooks();
        }

        private async void CheckNow_Click(object sender, RoutedEventArgs e)
        {
            CheckNowButton.IsEnabled = false;
            UpdateStatus.Text = "Checking…";
            try { UpdateStatus.Text = await _host.CheckForUpdates(); }
            finally { CheckNowButton.IsEnabled = true; }
        }

        // ------------------------------------------------------------------ save

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (name.Length == 0) { ShowStatus("Give your buddy a name."); NameBox.Focus(); return; }

            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<int, string>();
            foreach (var r in _shortcuts)
            {
                string effective = r.Setting.Length == 0 ? "" : r.Setting;
                if (effective.Length > 0 && !string.Equals(effective, "None", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.ContainsKey(effective)) { ShowStatus($"{effective} is used twice. Each shortcut must be different."); return; }
                    seen[effective] = "";
                }
                map[r.Id] = r.Setting;
            }
            string? error = _host.ApplyShortcuts(map);
            if (error != null) { ShowStatus(error); return; }

            _settings.BuddyName = name;
            _settings.LionSize = _size;
            _settings.QuirkFrequency = _quirks;
            _settings.SelfTalk = SelfTalkBox.IsChecked == true;
            _settings.Model = _model;
            _settings.ChatReadsFolder = ReadsFolderBox.IsChecked == true;
            _settings.ClaudeAlerts = AlertsBox.IsChecked == true;
            _settings.AutoUpdateCheck = AutoUpdateBox.IsChecked == true;
            _settings.ExtrasOnline = ExtrasOnlineBox.IsChecked == true;
            _settings.Save();
            bool startup = StartupBox.IsChecked == true;
            if (startup != TrayIcon.IsStartupEnabled()) TrayIcon.SetStartup(startup);
            _host.Applied();
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowStatus(string message)
        {
            Status.Text = message;
            Status.Visibility = Visibility.Visible;
        }
    }
}
