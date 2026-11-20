using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Assistail.Extras
{
    /// <summary>B5/C9: installed packs (on/off, settings), the catalog's packs ("Available") and pack updates.</summary>
    public partial class ExtrasWindow : Window
    {
        private readonly ExtrasEngine _engine;
        private readonly ExtrasStore _store;
        private readonly Settings _settings;
        private readonly Action _installFromFile;
        private bool _busy;                 // a download or install is running

        private sealed class Edit
        {
            public Pack Pack = null!;
            public CheckBox Enabled = null!;
            public readonly Dictionary<string, Func<string>> Values = new();
            public int Attention;           // 0 = as the pack suggests
            public CheckBox Escalate = null!;
        }
        private readonly List<Edit> _edits = new();

        public ExtrasWindow(ExtrasEngine engine, ExtrasStore store, Settings settings, Action installFromFile, string tab = "installed")
        {
            InitializeComponent();
            _engine = engine;
            _store = store;
            _settings = settings;
            _installFromFile = installFromFile;
            OnlineBox.IsChecked = settings.ExtrasOnline;
            // The content grows when a tab fills in: keep the whole window (and its Save button) on the screen.
            SizeChanged += (_, _) =>
            {
                var wa = SystemParameters.WorkArea;
                if (Top + ActualHeight > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - ActualHeight);
            };
            Scroller.MaxHeight = Math.Max(380, SystemParameters.WorkArea.Height - 190);
            TitleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            _engine.Changed += OnEngineChanged;
            Closed += (_, _) => _engine.Changed -= OnEngineChanged;
            Build();
            UpdateTabTitles();
            if (tab == "available") TabAvailable.IsChecked = true;
            else if (tab == "updates") TabUpdates.IsChecked = true;
        }

        // ------------------------------------------------------------------ tabs

        private string Tab => TabAvailable.IsChecked == true ? "available" : TabUpdates.IsChecked == true ? "updates" : "installed";

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (InstalledPanel == null) return;   // during InitializeComponent
            bool store = Tab != "installed";
            InstalledPanel.Visibility = store ? Visibility.Collapsed : Visibility.Visible;
            StorePanel.Visibility = store ? Visibility.Visible : Visibility.Collapsed;
            Status.Visibility = Visibility.Collapsed;
            if (store)
            {
                BuildStore();
                // Fresh list when the store is opened (at most every 10 minutes).
                if (_settings.ExtrasOnline && (_store.Fetched == null || _store.FromCache || DateTime.Now - _store.Fetched > TimeSpan.FromMinutes(10)))
                    _ = RefreshAsync();
            }
        }

        private void UpdateTabTitles()
        {
            int n = _store.Updates(_engine.Packs).Count;
            TabUpdates.Content = n > 0 ? $"Updates ({n})" : "Updates";
        }

        // ------------------------------------------------------------------ the catalog

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            RefreshButton.IsEnabled = false;
            StoreStatus.Text = "Getting the list of extras…";
            try
            {
                string? error = await _store.RefreshAsync();
                _settings.LastExtrasCheck = DateTime.Now;
                _settings.Save();
                BuildStore(error);
            }
            finally { _busy = false; RefreshButton.IsEnabled = true; }
        }

        private void BuildStore(string? error = null)
        {
            StoreList.Children.Clear();
            UpdateTabTitles();
            bool updates = Tab == "updates";
            var list = updates
                ? _store.Updates(_engine.Packs).ConvertAll(u => u.Newer)
                : new List<ExtrasStore.Entry>(_store.Entries);

            string when = _store.Fetched is { } t ? (_store.FromCache ? $"saved list from {t:d MMM, HH:mm}" : "up to date") : "";
            if (_store.Entries.Count == 0)
                StoreStatus.Text = error ?? (_settings.ExtrasOnline ? "Getting the list of extras…" : "Turn on “Check for new and updated extras online” below to see the packs you can add.");
            else if (updates)
                StoreStatus.Text = (list.Count == 0 ? "All your extras are up to date." : $"{list.Count} of your extras can be updated. Your settings for them are kept.")
                                   + (error != null ? "  (" + error + ")" : "");
            else
                StoreStatus.Text = "Packs from the official extras catalog. They're checked against the catalog's signature before they install."
                                   + (error != null ? $"\n{error} Showing the {when}." : when.Length > 0 && _store.FromCache ? $" (Showing the {when}.)" : "");

            foreach (var e in list) StoreList.Children.Add(StoreCard(e));
            UpdateAllButton.Visibility = updates && list.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private UIElement StoreCard(ExtrasStore.Entry e)
        {
            var installed = _engine.Packs.Find(p => p.Id == e.Id && p.IsUsable);
            var body = new StackPanel();
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2B)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x3C, 0x38)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 10), Child = body,
            };
            var header = new DockPanel { LastChildFill = true };
            var button = new Button { Style = S("Flat"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Top, MinWidth = 96 };
            bool newer = installed != null && Version.TryParse(installed.Version, out var iv) && e.Ver > iv;
            if (e.NeedsNewerApp) { button.Content = $"Needs Assistail {e.MinApp}"; button.IsEnabled = false; }
            else if (installed == null) { button.Content = "Install"; button.Style = S("Primary"); }
            else if (newer) { button.Content = $"Update to {e.Version}"; button.Style = S("Primary"); }
            else { button.Content = "Installed ✓"; button.IsEnabled = false; }
            button.Click += async (_, _) => await InstallAsync(e, button);
            DockPanel.SetDock(button, Dock.Right);
            header.Children.Add(button);

            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = e.Title, Style = S("Label"), FontWeight = FontWeights.SemiBold, FontSize = 14.5 });
            string by = $"Version {e.Version}" + (e.Author.Length > 0 ? $" · by {e.Author}" : "") + (e.Kind == "routine" ? " · timer you start from the menu" : "")
                        + (newer ? $" · you have {installed!.Version}" : "");
            titles.Children.Add(new TextBlock { Text = by, Style = S("Hint"), Margin = new Thickness(0, 1, 0, 0) });
            header.Children.Add(titles);
            body.Children.Add(header);
            if (e.Description.Length > 0)
                body.Children.Add(new TextBlock { Text = e.Description, Style = S("Hint"), FontSize = 13, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 0) });
            return card;
        }

        private async System.Threading.Tasks.Task<bool> InstallAsync(ExtrasStore.Entry e, Button? button)
        {
            if (_busy) return false;
            _busy = true;
            if (button != null) { button.IsEnabled = false; button.Content = "Installing…"; }
            try
            {
                var (json, error) = await _store.DownloadAsync(e);
                if (json != null) error = PackInstaller.InstallJson(json, out _);
                if (error != null) { ShowStatus($"{e.Name}: {error}"); BuildStore(); return false; }
                _engine.Reload();
                return true;
            }
            finally
            {
                _busy = false;
                if (IsLoaded) { Build(); BuildStore(); }
            }
        }

        private async void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            UpdateAllButton.IsEnabled = false;
            foreach (var (_, newer) in _store.Updates(_engine.Packs))
                if (!await InstallAsync(newer, null)) break;
            UpdateAllButton.IsEnabled = true;
        }

        private void Online_Click(object sender, RoutedEventArgs e)
        {
            _settings.ExtrasOnline = OnlineBox.IsChecked == true;
            _settings.Save();
            if (_settings.ExtrasOnline) _ = RefreshAsync(); else BuildStore();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (!_settings.ExtrasOnline) { OnlineBox.IsChecked = true; _settings.ExtrasOnline = true; _settings.Save(); }
            await RefreshAsync();
        }

        private void OpenCatalog_Click(object sender, RoutedEventArgs e) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExtrasStore.CatalogPage) { UseShellExecute = true });

        private bool _building;
        private void OnEngineChanged() { if (!_building) Build(); }

        // ------------------------------------------------------------------ cards

        private void Build()
        {
            _building = true;
            try
            {
                PackList.Children.Clear();
                _edits.Clear();
                foreach (var p in _engine.Packs) PackList.Children.Add(Card(p));
                EmptyText.Visibility = _engine.Packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _building = false; }
        }

        private Style S(string key) => (Style)FindResource(key);

        private UIElement Card(Pack p)
        {
            var body = new StackPanel();
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2B)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x3C, 0x38)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 10), Child = body,
            };

            var header = new DockPanel { LastChildFill = true };
            var remove = new Button { Content = "Remove", Style = S("Flat"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Top };
            remove.Click += (_, _) => RemovePack(p);
            DockPanel.SetDock(remove, Dock.Right);
            header.Children.Add(remove);

            var titles = new StackPanel();
            if (p.IsUsable)
            {
                var edit = new Edit { Pack = p };
                edit.Enabled = new CheckBox { IsChecked = _engine.IsEnabled(p), Margin = new Thickness(0), FontWeight = FontWeights.SemiBold, Content = p.Title, FontSize = 14.5 };
                titles.Children.Add(edit.Enabled);
                _edits.Add(edit);
                string by = $"Version {p.Version}" + (p.Author.Length > 0 ? $" · by {p.Author}" : "");
                titles.Children.Add(new TextBlock { Text = by, Style = S("Hint"), Margin = new Thickness(22, 1, 0, 0) });
                header.Children.Add(titles);
                body.Children.Add(header);
                if (p.Description.Length > 0)
                    body.Children.Add(new TextBlock { Text = p.Description, Style = S("Hint"), FontSize = 13, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(22, 6, 0, 0) });
                if (p.Settings.Count > 0) body.Children.Add(SettingsGrid(p, edit));
                body.Children.Add(AttentionRow(p, edit));
            }
            else
            {
                titles.Children.Add(new TextBlock { Text = "⚠ " + (p.Name.Length > 0 ? p.Name : p.Id), Style = S("Label"), FontWeight = FontWeights.SemiBold });
                titles.Children.Add(new TextBlock
                {
                    Text = (p.NeedsNewer ? "" : "Can't be used: ") + p.Error,
                    Style = S("Hint"), Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82)), Margin = new Thickness(0, 2, 0, 0),
                });
                header.Children.Add(titles);
                body.Children.Add(header);
            }
            return card;
        }

        private Grid SettingsGrid(Pack p, Edit edit)
        {
            var grid = new Grid { Margin = new Thickness(22, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var s in p.Settings)
            {
                int row = grid.RowDefinitions.Count;
                if (row > 0) { grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) }); row++; }
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = s.Label, Style = S("Label"), FontSize = 13 };
                Grid.SetRow(label, row);
                grid.Children.Add(label);
                var control = Control(p, s, edit);
                Grid.SetRow(control, row); Grid.SetColumn(control, 1);
                grid.Children.Add(control);
            }
            return grid;
        }

        /// <summary>"How noticeable": as the pack suggests / subtle / normal / noticeable, plus "remind again if I don't react".</summary>
        private FrameworkElement AttentionRow(Pack p, Edit edit)
        {
            var st = _settings.Extras.TryGetValue(p.Id, out var x) ? x : null;
            edit.Attention = st?.Attention ?? 0;
            var panel = new StackPanel { Margin = new Thickness(22, 10, 0, 0) };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = "How noticeable", Style = S("Label"), FontSize = 13 });
            var seg = new WrapPanel();
            foreach (var (label, level, tip) in new[]
            {
                ("Default", 0, "As the pack suggests for each message"),
                ("Subtle", 1, "A small bubble and a gesture"),
                ("Normal", 2, "The bubble bounces to catch your eye"),
                ("Noticeable", 3, "He walks toward the middle of the taskbar with a bigger bubble"),
                ("Sign", 4, "His paw slides in from the side holding a sign with the reminder"),
                ("Boop", 5, "His paw boops your screen and leaves a paw print with the reminder"),
            })
            {
                var rb = new RadioButton { Content = label, GroupName = p.Id + ".attention", IsChecked = edit.Attention == level, Style = S("Seg"), ToolTip = tip, Margin = new Thickness(0, 0, 0, 4) };
                rb.Checked += (_, _) => edit.Attention = level;
                seg.Children.Add(rb);
            }
            Grid.SetColumn(seg, 1);
            row.Children.Add(seg);
            panel.Children.Add(row);
            edit.Escalate = new CheckBox
            {
                Content = new TextBlock { Text = "If I don't react, remind me again a bit louder", TextWrapping = TextWrapping.Wrap },
                IsChecked = st?.Escalate ?? ExtrasEngine.PackEscalates(p),
                FontSize = 13, Margin = new Thickness(150, 4, 0, 0),
            };
            panel.Children.Add(edit.Escalate);
            return panel;
        }

        private FrameworkElement Control(Pack p, PackSetting s, Edit edit)
        {
            string value = _engine.Value(p, s.Key);
            switch (s.Type)
            {
                case "bool":
                    var cb = new CheckBox { IsChecked = value == "true", Margin = new Thickness(0) };
                    edit.Values[s.Key] = () => cb.IsChecked == true ? "true" : "false";
                    return cb;
                case "choice":
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };
                    string chosen = value;
                    foreach (var (v, l) in s.Choices)
                    {
                        var rb = new RadioButton { Content = l, GroupName = $"{p.Id}.{s.Key}", IsChecked = v == value, Style = S("Seg") };
                        rb.Checked += (_, _) => chosen = v;
                        panel.Children.Add(rb);
                    }
                    edit.Values[s.Key] = () => chosen;
                    return panel;
                default:   // minutes, number, time
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    var box = new TextBox { Text = value, Style = S("Box"), Width = 70, FontSize = 13, HorizontalContentAlignment = HorizontalAlignment.Center };
                    row.Children.Add(box);
                    string unit = s.Type == "minutes" ? "minutes" : s.Unit;
                    string range = s.Type == "time" ? "(HH:mm)" : $"({s.Min:0.#}–{s.Max:0.#})";
                    row.Children.Add(new TextBlock { Text = $"{unit} {range}".Trim(), Style = S("Hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                    edit.Values[s.Key] = () => box.Text.Trim();
                    return row;
            }
        }

        // ------------------------------------------------------------------ actions

        private void RemovePack(Pack p)
        {
            if (MessageBox.Show(this, $"Remove {p.Title}?", "Assistail extras", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            string? error = PackInstaller.Remove(p);
            if (error != null) { ShowStatus("Couldn't remove it: " + error); return; }
            _settings.Extras.Remove(p.Id);
            _settings.Save();
            _engine.Reload();
        }

        private void Install_Click(object sender, RoutedEventArgs e) => _installFromFile();

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(ExtrasEngine.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExtrasEngine.Folder) { UseShellExecute = true });
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Check every value first, so nothing is half-saved.
            foreach (var edit in _edits)
                foreach (var (key, get) in edit.Values)
                {
                    var s = edit.Pack.Setting(key)!;
                    string v = get();
                    if (s.Type == "time" && v.Contains(':') && TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var t)) v = t.ToString(@"hh\:mm");
                    if (!s.IsValid(v))
                    {
                        ShowStatus(s.Type == "time" ? $"{edit.Pack.Name}: “{s.Label}” needs a time like 18:00."
                                                    : $"{edit.Pack.Name}: “{s.Label}” must be between {s.Min:0.#} and {s.Max:0.#}.");
                        return;
                    }
                }

            _building = true;
            try
            {
                foreach (var edit in _edits)
                {
                    var st = _engine.StateOf(edit.Pack);
                    foreach (var (key, get) in edit.Values)
                    {
                        var s = edit.Pack.Setting(key)!;
                        string v = get();
                        if (s.Type == "time" && v.Contains(':') && TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var t)) v = t.ToString(@"hh\:mm");
                        if (v == s.Default) st.Values.Remove(key); else st.Values[key] = v;
                    }
                    st.Attention = edit.Attention;
                    bool esc = edit.Escalate.IsChecked == true;
                    st.Escalate = esc == ExtrasEngine.PackEscalates(edit.Pack) ? null : esc;
                    bool on = edit.Enabled.IsChecked == true;
                    if (on != _engine.IsEnabled(edit.Pack)) _engine.SetEnabled(edit.Pack, on);
                }
                _settings.Save();
            }
            finally { _building = false; }
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
