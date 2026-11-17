using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace Assistail
{
    public partial class App : Application
    {
        private Mutex? _mutex;
        private TrayIcon? _tray;
        private HotkeyService? _hotkey;
        private ExplorerTracker? _tracker;
        private ChatWindow? _chat;
        private LionWindow? _lion;
        private Settings _settings = new();

        public static string DataDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assistail");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Testing aid: Assistail.exe --test-folder "open claude in the folder named x" → parse + search, log, exit.
            if (e.Args.Length >= 2 && e.Args[0] == "--test-folder")
            {
                Directory.CreateDirectory(DataDir);
                var req = FolderFinder.Parse(e.Args[1], "Leo");
                if (req == null) Log($"TEST “{e.Args[1]}” → not a folder command");
                else
                {
                    var hits = FolderFinder.Search(req, Settings.Load().RecentFolders);
                    Log($"TEST “{e.Args[1]}” → open={req.Open} here={req.Here} path={req.Path} name={req.Name} drive={req.Drive} → " +
                        (hits.Count == 0 ? "no matches" : string.Join(" | ", hits)));
                }
                Shutdown();
                return;
            }

            _mutex = new Mutex(true, @"Local\Assistail", out bool isNew);
            if (!isNew) { Shutdown(); return; }

            MigrateFromOldName();
            Directory.CreateDirectory(DataDir);
            DispatcherUnhandledException += (_, args) =>
            {
                Log(args.Exception.ToString());
                args.Handled = true;
            };

            _settings = Settings.Load();
            _tracker = new ExplorerTracker();

            _hotkey = new HotkeyService();
            _hotkey.Pressed += id => { if (id == HotkeyService.ChatId) OnHotkey(); else NextTrick(); };
            string? chatErr = _hotkey.Apply(HotkeyService.ChatId, _settings.ChatHotkey, Settings.ChatHotkeyDefaults);
            string? trickErr = _hotkey.Apply(HotkeyService.TrickId, _settings.TrickHotkey, Settings.TrickHotkeyDefaults);
            // A saved custom shortcut that another app has since taken falls back to the defaults.
            if (chatErr != null) { Log("Chat hotkey: " + chatErr); _hotkey.Apply(HotkeyService.ChatId, "", Settings.ChatHotkeyDefaults); }
            if (trickErr != null) { Log("Trick hotkey: " + trickErr); _hotkey.Apply(HotkeyService.TrickId, "", Settings.TrickHotkeyDefaults); }
            string hotkeyText = _hotkey.TextOf(HotkeyService.ChatId);

            _lion = new LionWindow(_settings);
            _lion.Clicked += () => ToggleChatFromLion();
            _lion.MenuRequested += () => _tray?.ShowMenuAtCursor();

            _tray = new TrayIcon(_settings, new MenuActions
            {
                NextTrick = NextTrick,
                OpenShortcuts = OpenShortcuts,
                ChatHotkeyText = () => _hotkey.TextOf(HotkeyService.ChatId),
                TrickHotkeyText = () => _hotkey.TextOf(HotkeyService.TrickId),
                ShowChat = () => ShowChat(_tracker.LastExplorerFolder()),
                OpenClaudeCode = () => Launcher.OpenClaudeCode(_tracker.LastExplorerFolder()),
                LastFolder = () => _tracker.LastExplorerFolder(),
                LionIsAway = () => _lion.IsAway,
                SendLionAway = () => _lion.Leave(),
                CallLionBack = () => _lion.Return(),
                Rename = () => AskForName(firstTime: false),
                SetLionSize = size => { _settings.LionSize = size; _settings.Save(); _lion.ApplySize(); },
                DoTrick = id => _lion.DoTrick(id),
                SaySomething = AskWhatToSay,
                Quit = Quit,
            });

            _lion.Start();

            // Testing aid: ASSISTAIL_TRICK=butterfly (any trick id, "say:text", or a comma list like
            // "chase,butterfly") runs them 3 s apart, starting ~3 s after launch.
            string? tricks = Environment.GetEnvironmentVariable("ASSISTAIL_TRICK");
            if (!string.IsNullOrEmpty(tricks))
            {
                var queue = new System.Collections.Generic.Queue<string>(tricks.Split(','));
                var tt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                tt.Tick += (_, _) =>
                {
                    if (queue.Count == 0) { tt.Stop(); return; }
                    string trick = queue.Dequeue().Trim();
                    Log("Test trick: " + trick);
                    if (trick == "shortcuts") { tt.Stop(); OpenShortcuts(); }
                    else if (trick.StartsWith("chat:")) { ShowChat(_tracker?.LastExplorerFolder()); _chat?.SubmitForTest(trick[5..]); }
                    else if (trick.StartsWith("say:")) _lion.Say(trick[4..]);
                    else _lion.DoTrick(trick);
                };
                tt.Start();
            }
            Log($"Started. Chat hotkey: '{hotkeyText}', trick hotkey: '{_hotkey.TextOf(HotkeyService.TrickId)}'. Buddy: '{_settings.BuddyName}'.");

            if (string.IsNullOrWhiteSpace(_settings.BuddyName))
            {
                // First launch: let the lion settle, then ask for a name.
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                t.Tick += (_, _) => { t.Stop(); AskForName(firstTime: true); ShowReadyNotification(hotkeyText); };
                t.Start();
            }
            else ShowReadyNotification(hotkeyText);
        }

        private void ShowReadyNotification(string hotkeyText)
        {
            string name = _settings.NameOrDefault;
            if (hotkeyText.Length == 0)
                _tray!.Notify($"{name} is here", "Click the lion to chat. (The hotkey couldn't be registered; another app uses it.)");
            else if (!_settings.WelcomeShown)
                _tray!.Notify($"{name} is here", $"Click the lion or press {hotkeyText} to chat. Right-click the lion for options.");
            _settings.WelcomeShown = true;
            _settings.Save();
        }

        private void AskForName(bool firstTime)
        {
            var dlg = new NameDialog(firstTime ? "Leo" : _settings.NameOrDefault, firstTime);
            if (_lion != null && !_lion.IsAway) dlg.PlaceAbove(_lion.BodyBoundsPx, _lion.MonitorWorkAreaPx);
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (dlg.ShowDialog() == true && dlg.ChosenName != null)
            {
                _settings.BuddyName = dlg.ChosenName;
                _settings.Save();
            }
            else if (firstTime && string.IsNullOrWhiteSpace(_settings.BuddyName))
            {
                _settings.BuddyName = "Leo";   // "Later" on first run keeps the default; rename any time
                _settings.Save();
            }
            _tray?.UpdateName();
            _lion?.UpdateName();
            _chat?.UpdateName();
        }

        private int _trickIndex = -1;

        /// <summary>Next-trick shortcut: each press runs the next trick in the list, wrapping around.</summary>
        private void NextTrick()
        {
            if (_lion == null || _lion.IsAway) return;
            _trickIndex = (_trickIndex + 1) % LionWindow.Tricks.Length;
            _lion.DoTrick(LionWindow.Tricks[_trickIndex].Id);
        }

        private void OpenShortcuts()
        {
            var dlg = new ShortcutsDialog(_settings.NameOrDefault, _settings.ChatHotkey, _settings.TrickHotkey,
                _hotkey!.TextOf(HotkeyService.ChatId), _hotkey.TextOf(HotkeyService.TrickId), ApplyShortcuts);
            dlg.ShowDialog();
        }

        /// <summary>Registers the new shortcuts; on failure restores the previous ones and returns the error.</summary>
        private string? ApplyShortcuts(string chat, string trick)
        {
            string oldChat = _settings.ChatHotkey, oldTrick = _settings.TrickHotkey;
            string? error = _hotkey!.Apply(HotkeyService.ChatId, chat, Settings.ChatHotkeyDefaults)
                            ?? _hotkey.Apply(HotkeyService.TrickId, trick, Settings.TrickHotkeyDefaults);
            if (error != null)
            {
                _hotkey.Apply(HotkeyService.ChatId, oldChat, Settings.ChatHotkeyDefaults);
                _hotkey.Apply(HotkeyService.TrickId, oldTrick, Settings.TrickHotkeyDefaults);
                return error;
            }
            _settings.ChatHotkey = chat;
            _settings.TrickHotkey = trick;
            _settings.Save();
            Log($"Shortcuts saved: chat '{_hotkey.TextOf(HotkeyService.ChatId)}', trick '{_hotkey.TextOf(HotkeyService.TrickId)}'.");
            return null;
        }

        private void AskWhatToSay()
        {
            if (_lion == null || _lion.IsAway) return;
            var dlg = NameDialog.ForSay(_settings.NameOrDefault);
            dlg.PlaceAbove(_lion.BodyBoundsPx, _lion.MonitorWorkAreaPx);
            if (dlg.ShowDialog() == true && dlg.ChosenName != null) _lion.Say(dlg.ChosenName);
        }

        private void OnHotkey()
        {
            if (_tracker == null) return;
            if (_chat != null && _chat.IsVisible && _chat.IsActive) { _chat.Hide(); return; }
            // Capture the folder from the window that was in front when the hotkey was pressed.
            ShowChat(_tracker.FolderOfForegroundOrLast());
        }

        private void ToggleChatFromLion()
        {
            if (_chat != null && _chat.IsVisible) { _chat.Hide(); return; }
            ShowChat(_tracker?.LastExplorerFolder());
        }

        // The chat window is created on first use to keep idle memory low.
        private void ShowChat(string? folder)
        {
            if (_chat == null)
            {
                _chat = new ChatWindow(_settings);
                _chat.ReplyStarted += () => _lion?.ChatThinking();
                _chat.TextStreaming += () => _lion?.ChatTalking();
                _chat.ReplyFinished += ok => _lion?.ChatDone(ok);
                _chat.LeaveRequested += () => _lion?.Leave();
                _chat.ReturnRequested += () => _lion?.Return();
                _chat.IsVisibleChanged += (_, _) =>
                {
                    if (_lion == null) return;
                    _lion.ChatOpen = _chat.IsVisible;
                    if (_chat.IsVisible) _lion.ChatOpened(); else _lion.ChatIdle();
                };
            }
            if (_lion != null && !_lion.IsAway)
                _chat.ShowFor(folder, _lion.BodyBoundsPx, _lion.MonitorWorkAreaPx);
            else
                _chat.ShowFor(folder);
        }

        private void Quit()
        {
            _chat?.Shutdown();
            _lion?.Close();
            _hotkey?.Dispose();
            _tracker?.Dispose();
            _tray?.Dispose();
            Shutdown();
        }

        /// <summary>The app used to be called "Claude Buddy": carry its settings over once.</summary>
        private static void MigrateFromOldName()
        {
            try
            {
                string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude" + "Buddy");
                string oldSettings = Path.Combine(oldDir, "settings.json");
                string newSettings = Path.Combine(DataDir, "settings.json");
                if (File.Exists(newSettings) || !File.Exists(oldSettings)) return;
                Directory.CreateDirectory(DataDir);
                File.Copy(oldSettings, newSettings);
                Directory.Delete(oldDir, recursive: true);
                Log("Migrated settings from the old app name.");
            }
            catch (Exception ex) { Log("Settings migration failed: " + ex.Message); }
        }

        public static void Log(string message)
        {
            try { File.AppendAllText(Path.Combine(DataDir, "log.txt"), $"[{DateTime.Now:u}] {message}{Environment.NewLine}"); }
            catch { /* logging must never crash the app */ }
        }
    }
}
