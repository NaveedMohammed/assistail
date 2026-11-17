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

        /// <summary>
        /// If Assistail was started from inside a Claude Code session (e.g. a terminal Claude opened), it inherits that
        /// session's marker variables and would pass them on to every `claude` it starts. The terminal from "Open Claude
        /// Code here" would then think it is a child session and not save its transcript. Drop only the per-session
        /// markers; user configuration (CLAUDE_CODE_USE_BEDROCK, CLAUDE_CODE_GIT_BASH_PATH, …) is kept.
        /// </summary>
        private static void ClearInheritedClaudeSession()
        {
            // Claude Code's tool shells also set NO_COLOR=1, which would make the opened Claude Code render without its
            // colours (white instead of orange). Drop it only when inherited from a session, not when the user set it.
            if (Environment.GetEnvironmentVariable("CLAUDECODE") != null
                && Environment.GetEnvironmentVariable("NO_COLOR", EnvironmentVariableTarget.User) == null
                && Environment.GetEnvironmentVariable("NO_COLOR", EnvironmentVariableTarget.Machine) == null)
                Environment.SetEnvironmentVariable("NO_COLOR", null);

            foreach (var name in new[] {
                "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID",
                "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
                "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "CLAUDE_EFFORT", "AI_AGENT" })
                Environment.SetEnvironmentVariable(name, null);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ClearInheritedClaudeSession();

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
            // Errors off the UI thread would otherwise end the app without a trace.
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("FATAL: " + args.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) => { Log("Task error: " + args.Exception); args.SetObserved(); };

            _settings = Settings.Load();
            _tracker = new ExplorerTracker();

            _hotkey = new HotkeyService();
            _hotkey.Pressed += id =>
            {
                switch (id)
                {
                    case HotkeyService.ChatId: OnHotkey(); break;
                    case HotkeyService.TrickId: NextTrick(); break;
                    case HotkeyService.ClipboardId: AskAboutClipboard(); break;
                    case HotkeyService.SnipId: AskAboutSnip(); break;
                }
            };
            foreach (var (id, _) in HotkeyIds)
            {
                // A saved custom shortcut that another app has since taken falls back to the defaults.
                string? err = _hotkey.Apply(id, HotkeySetting(id), HotkeyDefaults(id));
                if (err != null) { Log($"Hotkey {id:X}: {err}"); _hotkey.Apply(id, "", HotkeyDefaults(id)); }
            }
            string hotkeyText = _hotkey.TextOf(HotkeyService.ChatId);

            _lion = new LionWindow(_settings);
            _lion.Clicked += OnLionClicked;
            _lion.BubbleClicked += OnBubbleClicked;
            _lion.FilesDropped += OnFilesDropped;
            _lion.MenuRequested += () => _tray?.ShowMenuAtCursor();

            _tray = new TrayIcon(_settings, new MenuActions
            {
                NextTrick = NextTrick,
                OpenSettings = OpenSettings,
                ChatHotkeyText = () => _hotkey.TextOf(HotkeyService.ChatId),
                TrickHotkeyText = () => _hotkey.TextOf(HotkeyService.TrickId),
                ClipboardHotkeyText = () => _hotkey.TextOf(HotkeyService.ClipboardId),
                SnipHotkeyText = () => _hotkey.TextOf(HotkeyService.SnipId),
                AskClipboard = AskAboutClipboard,
                AskSnip = AskAboutSnip,
                InstallHooks = () => InstallHooks(null),
                RemoveHooks = () => RemoveHooks(null),
                PendingUpdate = () => _pendingUpdate?.Version.ToString(3),
                InstallUpdate = () => OfferUpdateInstall(),
                CheckForUpdates = async () =>
                {
                    string status = await CheckForUpdatesAsync(manual: true);
                    if (_pendingUpdate == null) _tray?.Notify("Assistail", status);
                },
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
            _alerts = new AlertCenter(_settings, _lion, _tray, _tracker);
            System.Threading.Tasks.Task.Run(Attachments.CleanupOld);
            StartUpdateChecks();
            OfferAlertsOnce();

            // Testing aid: ASSISTAIL_TRICK=butterfly (any trick id, "say:text", "settings", "clip", "snip", "update",
            // "drop:C:\path", or a comma list like "chase,butterfly") runs them 3 s apart, starting ~3 s after launch.
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
                    if (trick is "settings" or "shortcuts") OpenSettings();
                    else if (trick == "clip") AskAboutClipboard();
                    else if (trick == "snip") AskAboutSnip();
                    else if (trick == "update-dryrun")
                    {
                        UpdateService.DryRun = true;
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            var r = await UpdateService.CheckAsync();
                            Log("Test update check: " + (r == null ? "up to date" : $"{r.Version} {r.ZipName} sha={r.Sha256}"));
                            if (r != null) Log("Test update result: " + await UpdateService.DownloadAndInstallAsync(r, null));
                        });
                    }
                    else if (trick.StartsWith("drop:")) OnFilesDropped(trick[5..].Split('|'), null);
                    else if (trick.StartsWith("chat:")) { ShowChat(_tracker?.LastExplorerFolder()); _chat?.SubmitForTest(trick[5..]); }
                    else if (trick.StartsWith("say:")) _lion.Say(trick[4..]);
                    else _lion.DoTrick(trick);
                };
                tt.Start();
            }
            Log($"Started {UpdateService.Current.ToString(3)}. Hotkeys: " + string.Join(", ", Array.ConvertAll(HotkeyIds, h => $"{h.Name} '{_hotkey.TextOf(h.Id)}'")) + $". Buddy: '{_settings.BuddyName}'.");

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

        // ================================================================== shortcuts and settings (C5)

        private static readonly (int Id, string Name)[] HotkeyIds =
        {
            (HotkeyService.ChatId, "chat"), (HotkeyService.TrickId, "trick"),
            (HotkeyService.ClipboardId, "clipboard"), (HotkeyService.SnipId, "snip"),
        };

        private string HotkeySetting(int id) => id switch
        {
            HotkeyService.ChatId => _settings.ChatHotkey,
            HotkeyService.TrickId => _settings.TrickHotkey,
            HotkeyService.ClipboardId => _settings.ClipboardHotkey,
            _ => _settings.SnipHotkey,
        };

        private void SetHotkeySetting(int id, string value)
        {
            switch (id)
            {
                case HotkeyService.ChatId: _settings.ChatHotkey = value; break;
                case HotkeyService.TrickId: _settings.TrickHotkey = value; break;
                case HotkeyService.ClipboardId: _settings.ClipboardHotkey = value; break;
                default: _settings.SnipHotkey = value; break;
            }
        }

        private static string[] HotkeyDefaults(int id) => id switch
        {
            HotkeyService.ChatId => Settings.ChatHotkeyDefaults,
            HotkeyService.TrickId => Settings.TrickHotkeyDefaults,
            HotkeyService.ClipboardId => Settings.ClipboardHotkeyDefaults,
            _ => Settings.SnipHotkeyDefaults,
        };

        /// <summary>Registers new shortcuts; on failure restores the previous ones and returns the error.</summary>
        private string? ApplyShortcuts(System.Collections.Generic.Dictionary<int, string> wanted)
        {
            var old = new System.Collections.Generic.Dictionary<int, string>();
            foreach (var (id, _) in HotkeyIds) old[id] = HotkeySetting(id);
            // Free everything first, so two shortcuts can swap keys.
            foreach (var (id, _) in HotkeyIds) _hotkey!.Unregister(id);
            string? error = null;
            foreach (var (id, _) in HotkeyIds)
            {
                error = _hotkey!.Apply(id, wanted.TryGetValue(id, out var v) ? v : old[id], HotkeyDefaults(id));
                if (error != null) break;
            }
            if (error != null)
            {
                foreach (var (id, _) in HotkeyIds) _hotkey!.Unregister(id);
                foreach (var (id, _) in HotkeyIds) _hotkey!.Apply(id, old[id], HotkeyDefaults(id));
                return error;
            }
            foreach (var (id, _) in HotkeyIds) if (wanted.TryGetValue(id, out var v)) SetHotkeySetting(id, v);
            _settings.Save();
            Log("Shortcuts saved: " + string.Join(", ", Array.ConvertAll(HotkeyIds, h => $"{h.Name} '{_hotkey!.TextOf(h.Id)}'")));
            return null;
        }

        private SettingsWindow? _settingsWindow;

        private void OpenSettings()
        {
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
            _settingsWindow = new SettingsWindow(_settings, new SettingsHost
            {
                ActiveHotkey = id => _hotkey!.TextOf(id),
                ApplyShortcuts = ApplyShortcuts,
                Applied = () =>
                {
                    _lion?.ApplySize();
                    _tray?.UpdateName();
                    _lion?.UpdateName();
                    _chat?.UpdateName();
                    if (!_settings.AutoUpdateCheck) Log("Daily update check turned off.");
                },
                InstallHooks = owner => InstallHooks(owner),
                RemoveHooks = owner => RemoveHooks(owner),
                CheckForUpdates = async () =>
                {
                    string status = await CheckForUpdatesAsync(manual: true);
                    return status;
                },
            });
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        // ================================================================== A1: Claude Code alerts

        private AlertCenter? _alerts;

        private bool InstallHooks(Window? owner)
        {
            string message =
                "Your buddy can tell you when a Claude Code session finishes or needs you. For that, Assistail adds two hooks " +
                "to Claude Code's settings:\n\n" + ClaudeHooks.SettingsPath + "\n\n" + ClaudeHooks.PreviewText() + "\n\n" +
                "A backup of the file is kept, and \"Remove\" puts it back. Sessions that are already running pick the hooks up " +
                "when you restart them.\n\nAdd the hooks?";
            if (Ask(owner, message, "Set up Claude Code alerts") != MessageBoxResult.Yes) return false;
            string? error = ClaudeHooks.Install();
            if (error != null) { Tell(owner, "The hooks couldn't be added:\n" + error, MessageBoxImage.Warning); return false; }
            _settings.ClaudeAlerts = true;
            _settings.Save();
            if (owner == null && _lion != null && !_lion.IsAway)
                _lion.Say("All set! 🔔 I'll tell you when Claude Code finishes or needs you. (Restart running sessions first.)", 9);
            else Tell(owner, "Done! New Claude Code sessions will now report to your buddy.", MessageBoxImage.Information);
            return true;
        }

        private bool RemoveHooks(Window? owner)
        {
            if (Ask(owner, "Remove Assistail's hooks from Claude Code's settings?\n\n" + ClaudeHooks.SettingsPath, "Claude Code alerts") != MessageBoxResult.Yes)
                return false;
            string? error = ClaudeHooks.Remove();
            if (error != null) { Tell(owner, "The hooks couldn't be removed:\n" + error, MessageBoxImage.Warning); return false; }
            Tell(owner, "Removed. Claude Code's settings are back to how they were.", MessageBoxImage.Information);
            return true;
        }

        /// <summary>Once, a little while after starting: tell existing users about alerts (if not set up yet).</summary>
        private void OfferAlertsOnce()
        {
            if (_settings.AlertsIntroShown || ClaudeHooks.State() != ClaudeHooks.HookState.NotInstalled) return;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(string.IsNullOrWhiteSpace(_settings.BuddyName) ? 90 : 20) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (_lion == null || _lion.IsAway || _lion.BubbleTag != null || (_chat?.IsVisible ?? false)) return;
                _settings.AlertsIntroShown = true;
                _settings.Save();
                _lion.Say("New! 🔔 I can tell you when Claude Code finishes or needs you. Click here to set it up.", 25, "alerts-intro");
            };
            t.Start();
        }

        private static MessageBoxResult Ask(Window? owner, string text, string title) =>
            owner != null ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                          : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly);

        private static void Tell(Window? owner, string text, MessageBoxImage icon)
        {
            if (owner != null) MessageBox.Show(owner, text, "Assistail", MessageBoxButton.OK, icon);
            else MessageBox.Show(text, "Assistail", MessageBoxButton.OK, icon, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
        }

        // ================================================================== clicks on the buddy / his bubble

        private void OnLionClicked()
        {
            if (HandleBubbleAction()) return;
            ToggleChatFromLion();
        }

        private void OnBubbleClicked() => HandleBubbleAction();

        /// <summary>A click while an actionable bubble shows acts on it (alert, update, alerts tip). True if handled.</summary>
        private bool HandleBubbleAction()
        {
            if (_alerts?.HandleClick() == true) return true;
            switch (_lion?.BubbleTag)
            {
                case "update": _lion.DismissBubble(); OfferUpdateInstall(); return true;
                case "alerts-intro": _lion.DismissBubble(); InstallHooks(null); return true;
            }
            return false;
        }

        // ================================================================== A2/A3: drop, clipboard, snip

        private void OnFilesDropped(string[] paths, string? text)
        {
            var folders = Array.FindAll(paths, Directory.Exists);
            var files = Array.FindAll(paths, File.Exists);
            if (files.Length == 0 && folders.Length > 0)
            {
                // A folder dropped on him: open Claude Code there.
                Launcher.OpenClaudeCode(folders[0]);
                _settings.AddRecentFolder(folders[0]);
                _lion?.Say($"Opening Claude Code in {TrayIcon.DisplayName(folders[0])} 🚀", talk: false);
                return;
            }
            ShowChat(_tracker?.LastExplorerFolder());
            _chat?.AttachPaths(paths, text);
        }

        private void AskAboutClipboard()
        {
            ShowChat(_tracker?.FolderOfForegroundOrLast());
            _chat?.AttachClipboard();
        }

        private System.Threading.CancellationTokenSource? _snipCts;

        private async void AskAboutSnip()
        {
            _snipCts?.Cancel();
            var cts = _snipCts = new System.Threading.CancellationTokenSource();
            string? folder = _tracker?.FolderOfForegroundOrLast();
            _chat?.Hide();                                  // keep it out of the picture
            var image = await Attachments.SnipAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            if (image == null) { Log("Snip cancelled or timed out."); return; }
            ShowChat(folder);
            _chat?.AttachImage(image, "snip");
        }

        // ================================================================== C1: updates

        private UpdateService.Release? _pendingUpdate;
        private bool _updating;

        private void StartUpdateChecks()
        {
            // First check two minutes after start (not during login), then hourly ticks that only call GitHub when a day has passed.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            timer.Tick += async (_, _) =>
            {
                timer.Interval = TimeSpan.FromHours(1);
                if (!_settings.AutoUpdateCheck || DateTime.Now - _settings.LastUpdateCheck < TimeSpan.FromHours(24)) return;
                await CheckForUpdatesAsync(manual: false);
            };
            timer.Start();
        }

        /// <summary>Checks GitHub; announces a new version (bubble or tray). Returns a one-line status.</summary>
        private async System.Threading.Tasks.Task<string> CheckForUpdatesAsync(bool manual)
        {
            try
            {
                var release = await UpdateService.CheckAsync();
                _settings.LastUpdateCheck = DateTime.Now;
                _settings.Save();
                if (release == null)
                {
                    _pendingUpdate = null;
                    return $"You're up to date (v{UpdateService.Current.ToString(3)}).";
                }
                _pendingUpdate = release;
                string v = release.Version.ToString(3);
                Log($"Update available: {v}");
                if (manual) { OfferUpdateInstall(); return $"Version {v} is available."; }
                if (_settings.AnnouncedVersion != v)
                {
                    _settings.AnnouncedVersion = v;
                    _settings.Save();
                    if (_lion == null || !_lion.Alert("wave", $"Update available: v{v} 🎉\nClick me to install it.", 25, "update"))
                        _tray?.Notify("Assistail update available", $"Version {v} is ready. Click to install.", () => OfferUpdateInstall());
                }
                return $"Version {v} is available.";
            }
            catch (Exception ex)
            {
                Log("Update check failed: " + ex.Message);
                return "Couldn't reach GitHub to check for updates. Try again later.";
            }
        }

        private async void OfferUpdateInstall()
        {
            var r = _pendingUpdate;
            if (r == null || _updating) return;
            string v = r.Version.ToString(3);
            if (r.ZipUrl.Length == 0)
            {
                if (Ask(null, $"Assistail {v} is out, but its download isn't attached yet. Open the releases page?", "Assistail update") == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.PageUrl) { UseShellExecute = true });
                return;
            }
            string text = $"Assistail {v} is available (you have {UpdateService.Current.ToString(3)}).\n\n" +
                          $"Install it now? It downloads {Attachments.Size(r.Size)} from GitHub, checks it, and your buddy comes back " +
                          "in a few seconds with the same settings.\n\nWhat's new: " + r.PageUrl;
            if (Ask(_settingsWindow, text, "Assistail update") != MessageBoxResult.Yes) return;

            _updating = true;
            _lion?.Say($"Updating to v{v}… back in a moment! 👋", 60, "updating", talk: false);
            string? error = await UpdateService.DownloadAndInstallAsync(r, new Progress<string>(s => Log("Update: " + s)));
            if (error != null)
            {
                _updating = false;
                _lion?.DismissBubble();
                Tell(_settingsWindow, error, MessageBoxImage.Warning);
                return;
            }
            Quit();   // the installer replaces the exe and starts the new version
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
            // Like a taskbar button: chat in front → hide it; chat open but behind other windows → bring it up.
            bool hide = _chat != null && _chat.IsVisible && _chat.IsActive;
            Log("Buddy clicked: " + (hide ? "hide chat" : "show chat"));
            if (hide) { _chat!.Hide(); return; }
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
                _chat.SnipRequested += AskAboutSnip;
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
            _alerts?.Dispose();
            _snipCts?.Cancel();
            _settingsWindow?.Close();
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
