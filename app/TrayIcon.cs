using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Assistail
{
    /// <summary>Actions the menu can trigger (wired up in App).</summary>
    public sealed class MenuActions
    {
        public Action ShowChat = () => { };
        public Action OpenClaudeCode = () => { };
        public Func<string?> LastFolder = () => null;
        public Func<bool> LionIsAway = () => false;
        public Action SendLionAway = () => { };
        public Action CallLionBack = () => { };
        public Action Rename = () => { };
        public Action<string> SetLionSize = _ => { };
        public Action<string> DoTrick = _ => { };
        public Action NextTrick = () => { };
        public Action SaySomething = () => { };
        public Action OpenShortcuts = () => { };
        public Func<string> ChatHotkeyText = () => "";
        public Func<string> TrickHotkeyText = () => "";
        public Action Quit = () => { };
    }

    /// <summary>System tray icon. Its menu is also shown when you right-click the lion.</summary>
    public sealed class TrayIcon : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "Assistail";

        private readonly NotifyIcon _icon;
        private readonly Settings _settings;
        private readonly ContextMenuStrip _menu;

        public TrayIcon(Settings settings, MenuActions a)
        {
            _settings = settings;
            _menu = new ContextMenuStrip();

            var chatItem = new ToolStripMenuItem("Chat", null, (_, _) => a.ShowChat())
            {
                Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold),
            };
            var openItem = new ToolStripMenuItem("Open Claude Code here", null, (_, _) => a.OpenClaudeCode());
            var browseItem = new ToolStripMenuItem("Open Claude Code in folder…", null, (_, _) => BrowseAndOpen());

            var awayItem = new ToolStripMenuItem("Send away");
            awayItem.Click += (_, _) => { if (a.LionIsAway()) a.CallLionBack(); else a.SendLionAway(); };
            var renameItem = new ToolStripMenuItem("Rename…", null, (_, _) => a.Rename());
            var sizeMenu = new ToolStripMenuItem("Size");
            foreach (var (label, code) in new[] { ("Small", "S"), ("Medium", "M"), ("Large", "L") })
            {
                var item = new ToolStripMenuItem(label) { Tag = code };
                item.Click += (_, _) => { a.SetLionSize(code); };
                sizeMenu.DropDownItems.Add(item);
            }

            var tricksMenu = new ToolStripMenuItem("Tricks");
            var nextTrickItem = new ToolStripMenuItem("Next trick", null, (_, _) => a.NextTrick()) { Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold) };
            tricksMenu.DropDownItems.Add(nextTrickItem);
            tricksMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var (id, label) in LionWindow.Tricks)
                tricksMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => a.DoTrick(id)));
            var sayItem = new ToolStripMenuItem("Say something…", null, (_, _) => a.SaySomething());

            var quirksMenu = new ToolStripMenuItem("Quirks when idle");
            foreach (var (label, value) in new[] { ("Often", "Often"), ("Sometimes", "Sometimes"), ("Rarely", "Rarely"), ("Off", "Off") })
            {
                var item = new ToolStripMenuItem(label) { Tag = value };
                item.Click += (_, _) => { _settings.QuirkFrequency = value; _settings.Save(); };
                quirksMenu.DropDownItems.Add(item);
            }
            var selfTalkItem = new ToolStripMenuItem("Talks on his own");
            selfTalkItem.Click += (_, _) => { _settings.SelfTalk = !_settings.SelfTalk; _settings.Save(); };
            quirksMenu.DropDownItems.Add(new ToolStripSeparator());
            quirksMenu.DropDownItems.Add(selfTalkItem);

            var modelMenu = new ToolStripMenuItem("Chat model");
            foreach (var (label, alias) in new[] { ("Default", ""), ("Haiku (fastest)", "haiku"), ("Sonnet", "sonnet"), ("Opus (smartest)", "opus") })
            {
                var item = new ToolStripMenuItem(label) { Tag = alias };
                item.Click += (_, _) => { _settings.Model = alias; _settings.Save(); };
                modelMenu.DropDownItems.Add(item);
            }

            var startupItem = new ToolStripMenuItem("Start with Windows");
            startupItem.Click += (_, _) => SetStartup(!IsStartupEnabled());

            _menu.Items.AddRange(new ToolStripItem[]
            {
                chatItem, openItem, browseItem, new ToolStripSeparator(),
                tricksMenu, sayItem, new ToolStripSeparator(),
                awayItem, renameItem, sizeMenu, quirksMenu, new ToolStripSeparator(),
                modelMenu, new ToolStripMenuItem("Shortcuts…", null, (_, _) => a.OpenShortcuts()), startupItem, new ToolStripSeparator(),
                new ToolStripMenuItem("Help && user guide", null, (_, _) => OpenGuide()),
                new ToolStripMenuItem("Quit", null, (_, _) => a.Quit()),
            });

            // Refresh labels/checks every time the menu opens (name, folder, away state, size, model, startup).
            _menu.Opening += (_, _) =>
            {
                string name = _settings.NameOrDefault;
                chatItem.Text = $"Chat with {name}";
                chatItem.ShortcutKeyDisplayString = a.ChatHotkeyText();
                nextTrickItem.ShortcutKeyDisplayString = a.TrickHotkeyText();
                string? folder = a.LastFolder();
                openItem.Text = folder != null ? $"Open Claude Code in \"{DisplayName(folder)}\"" : "Open Claude Code (home folder)";
                openItem.ToolTipText = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                awayItem.Text = a.LionIsAway() ? $"Call {name} back" : $"Send {name} away";
                renameItem.Text = $"Rename {name}…";
                sizeMenu.Text = $"{name}'s size";
                foreach (ToolStripMenuItem m in sizeMenu.DropDownItems) m.Checked = (string)m.Tag! == _settings.LionSize;
                foreach (ToolStripItem m in quirksMenu.DropDownItems)
                    if (m is ToolStripMenuItem mi && mi.Tag is string v) mi.Checked = v == _settings.QuirkFrequency;
                selfTalkItem.Checked = _settings.SelfTalk;
                tricksMenu.Enabled = sayItem.Enabled = !a.LionIsAway();
                sayItem.Text = $"Make {name} say something…";
                foreach (ToolStripMenuItem m in modelMenu.DropDownItems) m.Checked = (string)m.Tag! == _settings.Model;
                startupItem.Checked = IsStartupEnabled();
                _menu.Items[_menu.Items.Count - 1].Text = "Quit Assistail";
            };

            _icon = new NotifyIcon { Icon = LoadIcon(), ContextMenuStrip = _menu, Visible = true };
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) a.ShowChat(); };
            UpdateName();
        }

        public void UpdateName()
        {
            string tip = $"{_settings.NameOrDefault} (Assistail)";
            _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        }

        /// <summary>Shows the same menu at the mouse pointer (used when right-clicking the lion).</summary>
        public void ShowMenuAtCursor() => _menu.Show(Cursor.Position);

        public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

        public static string DisplayName(string folder)
        {
            string name = Path.GetFileName(folder.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? folder : name;
        }

        private static void OpenGuide()
        {
            string guide = Path.Combine(AppContext.BaseDirectory, "Assistail Guide.html");
            if (File.Exists(guide))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(guide) { UseShellExecute = true });
            else
                MessageBox.Show("The user guide wasn't found next to Assistail.exe.\nReinstall Assistail to restore it.",
                                "Assistail", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void BrowseAndOpen()
        {
            using var dlg = new FolderBrowserDialog { Description = "Open Claude Code in…", UseDescriptionForTitle = true };
            if (dlg.ShowDialog() == DialogResult.OK) Launcher.OpenClaudeCode(dlg.SelectedPath);
        }

        private static Icon LoadIcon()
        {
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/assistail.ico"));
                if (res != null) using (res.Stream) return new Icon(res.Stream, SystemInformation.SmallIconSize);
            }
            catch (Exception ex) { App.Log("Tray icon load failed: " + ex.Message); }
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        }

        private static bool IsStartupEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }

        private static void SetStartup(bool enable)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
