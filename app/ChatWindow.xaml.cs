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
        /// <summary>📎 → "Snip an area of the screen": the app hides the chat, runs the snip and attaches the result.</summary>
        public event Action? SnipRequested;

        // ================================================================== attachments (A2/A3)

        private readonly System.Collections.Generic.List<Attachment> _pending = new();
        private string? _attachDir;            // this conversation's attachments folder (created on first use)
        private bool _attachDirUsed;           // some attachment was copied there, so Claude may read the folder

        private string AttachDir => _attachDir ??= Attachments.NewConversationDir();

        /// <summary>Attaches dropped/pasted files. A folder (with no files) switches the chat to it instead.</summary>
        public void AttachPaths(System.Collections.Generic.IEnumerable<string> paths, string? droppedText = null)
        {
            var list = new System.Collections.Generic.List<string>(paths);
            var folders = list.FindAll(System.IO.Directory.Exists);
            var files = list.FindAll(System.IO.File.Exists);
            if (files.Count == 0 && folders.Count > 0)
            {
                EmptyHint.Visibility = Visibility.Collapsed;
                ApplyFolder(false, folders[0]);
                return;
            }
            foreach (var f in files)
            {
                var (a, err) = Attachments.FromFile(f, AttachDir);
                if (a != null) AddPending(a);
                else if (err != null) ShowAttachError(err);
            }
            if (folders.Count > 0) ShowAttachError("Folders can't be attached; I added the files only. (Drop a folder by itself to switch to it.)");
            if (files.Count == 0 && !string.IsNullOrWhiteSpace(droppedText)) AddPending(Attachments.FromText(droppedText, AttachDir));
            FocusInput();
        }

        /// <summary>Attaches whatever is on the clipboard (files, a picture or text).</summary>
        public void AttachClipboard()
        {
            var (items, errors) = Attachments.FromClipboard(AttachDir);
            foreach (var a in items) AddPending(a);
            foreach (var e in errors) ShowAttachError(e);
            if (items.Count == 0 && errors.Count == 0) ShowAttachError("The clipboard is empty. Copy some text, a picture or files first.");
            FocusInput();
        }

        public void AttachImage(System.Windows.Media.Imaging.BitmapSource image, string baseName)
        {
            AddPending(Attachments.FromImage(image, AttachDir, baseName));
            FocusInput();
        }

        private void AddPending(Attachment a)
        {
            _pending.Add(a);
            RefreshChips();
        }

        private void ShowAttachError(string message)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            AddAssistantMessage("⚠ " + message, ErrorBrush);
        }

        private void FocusInput()
        {
            if (IsVisible) { Activate(); Input.Focus(); }
        }

        private void RefreshChips()
        {
            AttachmentChips.Children.Clear();
            foreach (var a in _pending)
            {
                var item = a;
                var label = new TextBlock
                {
                    Text = $"{a.Icon} {a.Name}" + (a.Note != null ? $"  ({a.Note})" : ""),
                    Foreground = FgBrush, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var remove = new Button { Content = "✕", Style = (Style)FindResource("HeaderButton"), FontSize = 11, Padding = new Thickness(0), ToolTip = "Remove" };
                remove.Click += (_, _) => { _pending.Remove(item); RefreshChips(); Input.Focus(); };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(label);
                row.Children.Add(remove);
                AttachmentChips.Children.Add(new Border
                {
                    Background = UserBubbleBrush, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 0, 1),
                    Margin = new Thickness(0, 0, 6, 4), Child = row, ToolTip = a.CopyPath ?? a.Name,
                });
            }
            AttachmentChips.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePlaceholder();
        }

        private void UpdatePlaceholder()
        {
            Placeholder.Text = _pending.Count switch
            {
                0 => $"Ask {_settings.NameOrDefault}…",
                1 => "Ask about this… (e.g. “what failed?”)",
                _ => "Ask about these files…",
            };
            Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Attach_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var files = new MenuItem { Header = "Attach files…" };
            files.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files" };
                if (dlg.ShowDialog(this) == true) AttachPaths(dlg.FileNames);
            };
            var clip = new MenuItem { Header = "Paste from the clipboard" };
            clip.Click += (_, _) => AttachClipboard();
            var snip = new MenuItem { Header = "Snip an area of the screen…" };
            snip.Click += (_, _) => SnipRequested?.Invoke();
            menu.Items.Add(files);
            menu.Items.Add(clip);
            menu.Items.Add(snip);
            menu.PlacementTarget = AttachButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) AttachPaths(files);
            else if (e.Data.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0) AttachPaths(Array.Empty<string>(), text);
        }

        public ChatWindow(Settings settings)
        {
            _settings = settings;
            InitializeComponent();
            UpdateName();
            FolderChip.MouseLeftButtonDown += (_, e) => { e.Handled = true; ShowFolderMenu(); };   // before the header's drag
            Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
            // Clicking another window brings that window to the front instead of the chat staying on top.
            Deactivated += (_, _) => Topmost = false;
            DragOver += OnDragOver;
            DragEnter += OnDragOver;
            Drop += OnDrop;
            // The input box handles text drops itself; send files to the chat instead.
            Input.PreviewDragOver += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) OnDragOver(s, e); };
            Input.PreviewDrop += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) OnDrop(s, e); };
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

        /// <summary>B1: the buddy changed (his description for Claude, his emoji).</summary>
        public void SetBuddy(Buddy buddy)
        {
            _cli.BuddyPersona = buddy.Persona;
            _emoji = buddy.Emoji;
        }

        private string _emoji = Buddy.Lion.Emoji;

        /// <summary>
        /// Shows the chat with an optional folder context. With <paramref name="lionPx"/> (the lion's body in
        /// physical pixels) it opens beside the lion on the side with more room; otherwise centred on the mouse's monitor.
        /// </summary>
        public void ShowFor(string? folder, System.Drawing.Rectangle? lionPx = null, System.Drawing.Rectangle? workAreaPx = null)
        {
            // A folder chosen in the chat ("switch to …") sticks until "New chat"; otherwise follow Explorer.
            if (!_folderPinned) SetFolder(folder, remember: false);

            bool wasHidden = !IsVisible;
            Topmost = true;                    // pops up above everything; drops back once you click elsewhere
            Show();
            if (lionPx is { } lion && workAreaPx is { } work) PlaceBesideLion(lion, work);
            else if (wasHidden) PlaceOnMouseMonitor();
            WindowState = WindowState.Normal;
            if (!Activate()) App.Log("Chat could not take focus (Windows kept another app in front).");
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
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
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
                AddAssistantMessage($"I'm back! {_emoji}");
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
                FolderChip.ToolTip = folder +
                    (_settings.ChatReadsFolder ? $"\n{_settings.NameOrDefault} can read (not change) files here to answer you." : "") +
                    "\nClick to choose another folder";
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
            else AddAssistantMessage(_settings.ChatReadsFolder
                ? $"Switched to {folder}. Ask me about the files here (I can read them, not change them), or say “open claude here” to start Claude Code there."
                : $"Switched to {folder}. Say “open claude here” to start Claude Code there.");
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
                string? error = await cli.SendAsync(prompt, "haiku", null, chunk => sb.Append(chunk), null, cts.Token);
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
            if (Busy || (text.Length == 0 && _pending.Count == 0)) return;
            if (_pending.Count == 0 && HandleBuddyCommand(text)) return;

            // Attachments go with this message; the files stay readable for follow-up questions in this chat.
            var attached = _pending.ToArray();
            _pending.Clear();
            RefreshChips();
            if (attached.Length > 0 && text.Length == 0) text = attached.Length == 1 ? "Have a look at this." : "Have a look at these.";
            if (Array.Exists(attached, a => a.CopyPath != null)) _attachDirUsed = true;
            string prompt = Attachments.BuildPrompt(attached, text);

            EmptyHint.Visibility = Visibility.Collapsed;
            AddUserMessage(attached.Length == 0 ? text
                : string.Join("  ", Array.ConvertAll(attached, a => $"{a.Icon} {a.Name}")) + "\n" + text);
            Input.Clear();
            TextBox reply = AddAssistantMessage();
            SetBusy(true);
            ReplyStarted?.Invoke();
            bool streaming = false;

            _cts = new CancellationTokenSource();
            string? error;
            try
            {
                string? folder = _settings.ChatReadsFolder ? _folder : null;
                error = await _cli.SendAsync(prompt, _settings.Model, folder, _attachDirUsed ? _attachDir : null, chunk =>
                {
                    if (!streaming) { streaming = true; TextStreaming?.Invoke(); }
                    StatusText.Visibility = Visibility.Collapsed;
                    bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 30;
                    reply.AppendText(chunk);
                    if (atBottom) Scroller.ScrollToEnd();
                }, status => { App.Log("Chat tool: " + status); ShowStatus("🔍 " + status); }, _cts.Token);
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
            _pending.Clear();
            RefreshChips();
            Attachments.DeleteDir(_attachDir);   // the old chat's copies aren't needed any more
            _attachDir = null;                // a new chat gets a new attachments folder
            _attachDirUsed = false;
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
            // Ctrl+V with a picture or files (no text) on the clipboard attaches them.
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                try
                {
                    if (!Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()))
                    {
                        e.Handled = true;
                        AttachClipboard();
                    }
                }
                catch { /* clipboard busy: let the normal paste run */ }
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
            Attachments.DeleteDir(_attachDir);
            Close();
        }
    }
}
