using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Assistail
{
    public partial class ChatWindow : Window
    {
        private readonly Settings _settings;
        private readonly ClaudeCli _cli = new();
        private CancellationTokenSource? _cts;
        private string? _folder;
        private bool _shuttingDown;

        private static readonly Brush FgBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xEB, 0xE6));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82));
        private static readonly Brush UserBubbleBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x39, 0x36));

        /// <summary>Lion reactions: Claude started working / text began streaming / reply finished (true = ok).</summary>
        public event Action? ReplyStarted;
        public event Action? TextStreaming;
        public event Action<bool>? ReplyFinished;
        /// <summary>User typed "go away"/"bye" or "come back" — handled locally, no Claude call.</summary>
        public event Action? LeaveRequested;
        public event Action? ReturnRequested;

        public ChatWindow(Settings settings)
        {
            _settings = settings;
            InitializeComponent();
            UpdateName();
            FolderChip.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowFolderMenu(); };   // before the header's drag
            Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
            // Let the mouse wheel scroll the conversation even when hovering over a message.
            Messages.PreviewMouseWheel += (_, e) =>
            {
                Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 2.0);
                e.Handled = true;
            };
        }

        private bool Busy => _cts != null;

        public void UpdateName()
        {
            string name = _settings.NameOrDefault;
            HeaderName.Text = name;
            Placeholder.Text = $"Ask {name}…";
            EmptyHint.Text = $"Hi! I'm {name}. Ask me anything.\nEnter to send · Shift+Enter for a new line · Esc to hide";
            _cli.BuddyName = name;
        }

        /// <summary>
        /// Shows the chat with an optional folder context. With <paramref name="lionPx"/> (the lion's body in
        /// physical pixels) it opens beside the lion on the side with more room; otherwise centred on the mouse's monitor.
        /// </summary>
        public void ShowFor(string? folder, System.Drawing.Rectangle? lionPx = null, System.Drawing.Rectangle? workAreaPx = null)
        {
            // A folder chosen in the chat ("switch to …") sticks until "New chat"; otherwise follow Explorer.
            if (!_folderPinned) SetFolder(folder, remember: false);

            bool wasHidden = !IsVisible;
            Show();
            if (lionPx is { } lion && workAreaPx is { } work) PlaceBesideLion(lion, work);
            else if (wasHidden) PlaceOnMouseMonitor();
            WindowState = WindowState.Normal;
            Activate();
            Input.Focus();
        }

        private void PlaceBesideLion(System.Drawing.Rectangle lion, System.Drawing.Rectangle work)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)(ActualWidth * dpi.DpiScaleX), h = (int)(ActualHeight * dpi.DpiScaleY);
            const int gap = 4;
            bool lionOnLeftHalf = lion.Left + lion.Width / 2 < work.Left + work.Width / 2;
            int x = lionOnLeftHalf ? lion.Right + gap : lion.Left - w - gap;
            int y = work.Bottom - h;                                // bottom edge just above the taskbar, beside the lion
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE);
        }

        private static readonly System.Text.RegularExpressions.Regex Punct = new(@"[^\p{L}\p{N}\s]");

        /// <summary>Recognises "go away" / "come back" style messages aimed at the buddy.</summary>
        private bool HandleBuddyCommand(string text)
        {
            string name = _settings.NameOrDefault.ToLowerInvariant();
            string t = Punct.Replace(text.ToLowerInvariant(), "").Trim();
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
            string stripped = t.Replace(name, "").Replace("  ", " ").Trim();   // "leo go away" → "go away"
            string[] leave = { "go away", "leave", "bye", "goodbye", "bye bye", "see you", "see ya", "please leave", "go away please" };
            string[] back = { "come back", "come back please", "come here", "return" };
            if (Array.IndexOf(leave, stripped) >= 0)
            {
                EmptyHint.Visibility = Visibility.Collapsed;
                AddUserMessage(text);
                AddAssistantMessage("Okay, see you later! 👋 (Call me back from the tray icon or by typing “come back”.)");
                Input.Clear();
                LeaveRequested?.Invoke();
                return true;
            }
            if (Array.IndexOf(back, stripped) >= 0)
            {
                EmptyHint.Visibility = Visibility.Collapsed;
                AddUserMessage(text);
                AddAssistantMessage("I'm back! 🦁");
                Input.Clear();
                ReturnRequested?.Invoke();
                return true;
            }
            // "open claude in …", "switch to …", "cd …"
            if (FolderFinder.Parse(text, _settings.NameOrDefault) is FolderRequest req)
            {
                EmptyHint.Visibility = Visibility.Collapsed;
                AddUserMessage(text);
                Input.Clear();
                _ = HandleFolderRequestAsync(req);
                return true;
            }
            return false;
        }

        // ================================================================== folders (A6)

        private bool _folderPinned;
        private bool _searching;

        private void SetFolder(string? folder, bool remember)
        {
            _folder = folder;
            if (folder != null)
            {
                FolderText.Text = "📁 " + TrayIcon.DisplayName(folder);
                FolderChip.ToolTip = folder + "\nClick to choose another folder";
                OpenCodeButton.ToolTip = "Open Claude Code in " + folder;
                if (remember) _settings.AddRecentFolder(folder);
            }
            else
            {
                FolderText.Text = "📁 Choose folder";
                FolderChip.ToolTip = "Choose a folder (recent folders, Browse…)";
                OpenCodeButton.ToolTip = "Open Claude Code in your home folder";
            }
        }

        private async System.Threading.Tasks.Task HandleFolderRequestAsync(FolderRequest req)
        {
            if (req.Here)
            {
                string here = _folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ApplyFolder(req.Open, here);
                return;
            }
            if (_searching) { AddAssistantMessage("I'm still looking for the last one, one moment…"); return; }
            _searching = true;
            ReplyStarted?.Invoke();                     // the buddy "thinks" while searching
            ShowStatus($"Looking for {req.Describe()}…");
            var recent = _settings.RecentFolders.ToArray();
            try
            {
                var results = await System.Threading.Tasks.Task.Run(() => FolderFinder.Search(req, recent));
                if (results.Count == 0)
                {
                    // Couldn't work it out locally: ask Claude to pull the folder out of the sentence, then search again.
                    ShowStatus("Working out which folder you mean…");
                    var hint = await AskClaudeForFolderAsync(req.Raw);
                    if (hint != null)
                    {
                        hint.Open = req.Open;
                        results = await System.Threading.Tasks.Task.Run(() => FolderFinder.Search(hint, recent));
                        if (results.Count > 0) req = hint;
                    }
                }
                HideStatus();
                if (results.Count == 1) ApplyFolder(req.Open, results[0]);
                else if (results.Count > 1)
                {
                    AddAssistantMessage(req.Open ? "I found a few. Which one should I open Claude Code in?" : "I found a few. Which one?");
                    AddChoices(results, path => ApplyFolder(req.Open, path), withBrowse: true, open: req.Open);
                }
                else
                {
                    AddAssistantMessage($"I couldn't find {req.Describe()}. Try the full path (like C:\\Users\\you\\code), or browse for it:");
                    AddChoices(Array.Empty<string>(), _ => { }, withBrowse: true, open: req.Open);
                }
                ReplyFinished?.Invoke(results.Count > 0);
            }
            catch (Exception ex)
            {
                HideStatus();
                App.Log("Folder request failed: " + ex);
                AddAssistantMessage("⚠ Something went wrong while looking for that folder.", ErrorBrush);
                ReplyFinished?.Invoke(false);
            }
            finally { _searching = false; Input.Focus(); }
        }

        /// <summary>Opens Claude Code in the folder, or switches the chat to it.</summary>
        private void ApplyFolder(bool open, string folder)
        {
            _folderPinned = true;
            SetFolder(folder, remember: true);
            if (open)
            {
                Launcher.OpenClaudeCode(folder);
                AddAssistantMessage($"Opening Claude Code in {folder} 🚀");
            }
            else AddAssistantMessage($"Switched to {folder}. Say “open claude here” to start Claude Code there.");
        }

        /// <summary>Asks Claude (fast model) to extract the folder from a free-form sentence.</summary>
        private async System.Threading.Tasks.Task<FolderRequest?> AskClaudeForFolderAsync(string sentence)
        {
            try
            {
                var cli = new ClaudeCli { BuddyName = _settings.NameOrDefault };
                var sb = new System.Text.StringBuilder();
                string prompt =
                    "A user asked their Windows desktop assistant to open or switch to a folder. Their words: \"" + sentence + "\"\n" +
                    "Reply with ONLY a JSON object, no other text: {\"path\": exact Windows path if they gave one else null, " +
                    "\"name\": the folder name to search for (just the name, no extra words) else null, " +
                    "\"drive\": drive letter if mentioned else null}";
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                string? error = await cli.SendAsync(prompt, "haiku", chunk => sb.Append(chunk), cts.Token);
                if (error != null) return null;
                var m = System.Text.RegularExpressions.Regex.Match(sb.ToString(), @"\{[\s\S]*\}");
                if (!m.Success) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(m.Value);
                string? Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                var req = new FolderRequest { Raw = sentence, Path = Get("path"), Name = Get("name") };
                if (Get("drive") is string d && d.Length > 0 && char.IsLetter(d[0])) req.Drive = char.ToUpperInvariant(d[0]);
                App.Log($"Claude folder hint: path={req.Path} name={req.Name} drive={req.Drive}");
                return (req.Path ?? req.Name) != null ? req : null;
            }
            catch (Exception ex) { App.Log("Claude folder hint failed: " + ex.Message); return null; }
        }

        /// <summary>Adds clickable folder choices (and optionally Browse…) to the conversation.</summary>
        private void AddChoices(System.Collections.Generic.IEnumerable<string> folders, Action<string> choose, bool withBrowse, bool open)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 30, 8) };
            bool used = false;                                   // one choice per list; fade it afterwards
            var style = (Style)FindResource("ChoiceButton");
            void AddButton(string label, string? tooltip, Action onClick)
            {
                var b = new Button
                {
                    Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = FgBrush },
                    ToolTip = tooltip,
                    Style = style,
                };
                b.Click += (_, _) => { if (used) return; used = true; panel.Opacity = 0.5; onClick(); };
                panel.Children.Add(b);
            }
            foreach (var f in folders) AddButton("📁 " + f, f, () => choose(f));
            if (withBrowse) AddButton("Browse…", "Pick a folder", () =>
            {
                if (BrowseForFolder() is string picked) ApplyFolder(open, picked);
                else { used = false; panel.Opacity = 1; }        // cancelled: choices stay available
            });
            Messages.Children.Add(panel);
            Scroller.ScrollToEnd();
        }

        private static string? BrowseForFolder()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose a folder", UseDescriptionForTitle = true };
            return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }

        /// <summary>The 📁 chip menu: recent folders, Browse…, Open Claude Code here.</summary>
        private void ShowFolderMenu()
        {
            var menu = new ContextMenu();
            int added = 0;
            foreach (var f in _settings.RecentFolders)
            {
                if (added >= 8 || !System.IO.Directory.Exists(f)) continue;
                var item = new MenuItem { Header = f.Replace("_", "__"), IsChecked = string.Equals(f, _folder, StringComparison.OrdinalIgnoreCase) };
                string folder = f;
                item.Click += (_, _) => { _folderPinned = true; SetFolder(folder, remember: true); };
                menu.Items.Add(item);
                added++;
            }
            if (added > 0) menu.Items.Add(new Separator());
            var browse = new MenuItem { Header = "Browse…" };
            browse.Click += (_, _) => { if (BrowseForFolder() is string p) { _folderPinned = true; SetFolder(p, remember: true); } };
            menu.Items.Add(browse);
            var open = new MenuItem { Header = "Open Claude Code here", IsEnabled = true };
            open.Click += (_, _) => { Launcher.OpenClaudeCode(_folder); if (_folder != null) _settings.AddRecentFolder(_folder); };
            menu.Items.Add(open);
            menu.PlacementTarget = FolderChip;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>Testing aid (ASSISTAIL_TRICK=chat:…): types a message and sends it.</summary>
        public void SubmitForTest(string text) { Input.Text = text; Send(); }

        private void ShowStatus(string text) { StatusText.Text = text; StatusText.Visibility = Visibility.Visible; }
        private void HideStatus() { StatusText.Visibility = Visibility.Collapsed; StatusText.Text = "Thinking…"; }

        private void PlaceOnMouseMonitor()
        {
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea; // physical pixels
            var dpi = VisualTreeHelper.GetDpi(this);
            double left = area.Left / dpi.DpiScaleX, top = area.Top / dpi.DpiScaleY;
            double w = area.Width / dpi.DpiScaleX, h = area.Height / dpi.DpiScaleY;
            Left = left + (w - ActualWidth) / 2;
            Top = top + Math.Max(0, (h - ActualHeight) * 0.3);
        }

        private async void Send()
        {
            string text = Input.Text.Trim();
            if (text.Length == 0 || Busy) return;
            if (HandleBuddyCommand(text)) return;

            EmptyHint.Visibility = Visibility.Collapsed;
            AddUserMessage(text);
            Input.Clear();
            TextBox reply = AddAssistantMessage();
            SetBusy(true);
            ReplyStarted?.Invoke();
            bool streaming = false;

            _cts = new CancellationTokenSource();
            string? error;
            try
            {
                error = await _cli.SendAsync(text, _settings.Model, chunk =>
                {
                    if (!streaming) { streaming = true; TextStreaming?.Invoke(); }
                    StatusText.Visibility = Visibility.Collapsed;
                    bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 30;
                    reply.AppendText(chunk);
                    if (atBottom) Scroller.ScrollToEnd();
                }, _cts.Token);
            }
            catch (Exception ex)
            {
                App.Log("Send failed: " + ex);
                error = ex.Message;
            }

            bool cancelled = _cts.IsCancellationRequested;
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            ReplyFinished?.Invoke(error == null && !cancelled);

            if (cancelled) reply.AppendText(reply.Text.Length > 0 ? "  [stopped]" : "[stopped]");
            if (error != null)
            {
                if (reply.Text.Length == 0) Messages.Children.Remove(reply);
                AddAssistantMessage("⚠ " + error, ErrorBrush);
            }
            else if (reply.Text.Length == 0) Messages.Children.Remove(reply);

            Scroller.ScrollToEnd();
            Input.Focus();
        }

        private void SetBusy(bool busy)
        {
            StatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SendButton.Content = busy ? "■" : "↑";
            SendButton.ToolTip = busy ? "Stop" : "Send (Enter)";
        }

        private void AddUserMessage(string text)
        {
            var bubble = new Border
            {
                Background = UserBubbleBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(60, 8, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = MakeText(text, FgBrush),
            };
            Messages.Children.Add(bubble);
            Scroller.ScrollToEnd();
        }

        private TextBox AddAssistantMessage(string text = "", Brush? brush = null)
        {
            var box = MakeText(text, brush ?? FgBrush);
            box.Margin = new Thickness(0, 6, 30, 6);
            Messages.Children.Add(box);
            Scroller.ScrollToEnd();
            return box;
        }

        /// <summary>A read-only TextBox so replies can be selected and copied.</summary>
        private static TextBox MakeText(string text, Brush brush) => new()
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = brush,
            Padding = new Thickness(0),
            FontSize = 14,
        };

        private void NewChat()
        {
            _cts?.Cancel();
            _cli.Reset();
            _folderPinned = false;            // follow Explorer again
            Messages.Children.Clear();
            Messages.Children.Add(EmptyHint);
            EmptyHint.Visibility = Visibility.Visible;
            Input.Focus();
        }

        private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                Send();
            }
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e) =>
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            if (Busy) _cts!.Cancel();
            else Send();
        }

        private void NewChat_Click(object sender, RoutedEventArgs e) => NewChat();

        private void OpenCode_Click(object sender, RoutedEventArgs e)
        {
            Launcher.OpenClaudeCode(_folder);
            if (_folder != null) _settings.AddRecentFolder(_folder);
            Hide();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Hide();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_shuttingDown) { e.Cancel = true; Hide(); } // Alt+F4 just hides
            base.OnClosing(e);
        }

        public void Shutdown()
        {
            _shuttingDown = true;
            _cts?.Cancel();
            Close();
        }
    }
}
