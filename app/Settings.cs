using System;
using System.IO;
using System.Text.Json;

namespace Assistail
{
    public class Settings
    {
        /// <summary>Model alias passed to claude --model; empty = Claude Code's default.</summary>
        public string Model { get; set; } = "";

        /// <summary>The buddy's name, chosen by the user on first launch. Empty = not chosen yet.</summary>
        public string BuddyName { get; set; } = "";

        /// <summary>Whether the lion is on the taskbar (false = sent away).</summary>
        public bool LionVisible { get; set; } = true;

        /// <summary>Lion position along the taskbar, 0 = left edge, 1 = right edge.</summary>
        public double LionX { get; set; } = 0.22;

        /// <summary>"S", "M" or "L".</summary>
        public string LionSize { get; set; } = "M";

        /// <summary>How often he does a random quirk when idle: "Off", "Rarely", "Sometimes", "Often".</summary>
        public string QuirkFrequency { get; set; } = "Sometimes";

        /// <summary>Open-chat shortcut: "" = default (Ctrl+Alt+Space or a free fallback), "None" = off, or e.g. "Ctrl+Alt+C".</summary>
        public string ChatHotkey { get; set; } = "";

        /// <summary>Next-trick shortcut (cycles through the tricks): "" = default (Ctrl+Alt+T or a free fallback), "None" = off.</summary>
        public string TrickHotkey { get; set; } = "";

        /// <summary>"Ask about the clipboard" shortcut: "" = default, "None" = off.</summary>
        public string ClipboardHotkey { get; set; } = "";

        /// <summary>"Ask about a screen area" (snip) shortcut: "" = default, "None" = off.</summary>
        public string SnipHotkey { get; set; } = "";

        public static readonly string[] ChatHotkeyDefaults = { "Ctrl+Alt+Space", "Win+Alt+C", "Ctrl+Shift+Space" };
        public static readonly string[] TrickHotkeyDefaults = { "Ctrl+Alt+T", "Ctrl+Alt+J", "Ctrl+Alt+K" };
        // Three modifiers on purpose: Ctrl+Alt+V / Ctrl+Alt+S already mean something in Office and JetBrains IDEs.
        public static readonly string[] ClipboardHotkeyDefaults = { "Ctrl+Shift+Alt+V", "Win+Alt+V" };
        public static readonly string[] SnipHotkeyDefaults = { "Ctrl+Shift+Alt+S", "Win+Alt+S" };

        /// <summary>Show Claude Code activity alerts (needs the hooks, installed from the menu or Settings).</summary>
        public bool ClaudeAlerts { get; set; } = true;

        /// <summary>The "new: Claude Code alerts" tip is shown once.</summary>
        public bool AlertsIntroShown { get; set; }

        /// <summary>Check GitHub for a newer version once a day.</summary>
        public bool AutoUpdateCheck { get; set; } = true;

        public DateTime LastUpdateCheck { get; set; }

        /// <summary>The last version announced in a bubble, so the daily check doesn't repeat it every day.</summary>
        public string AnnouncedVersion { get; set; } = "";

        /// <summary>The "is here" notification is shown once (first launch), not on every start.</summary>
        public bool WelcomeShown { get; set; }

        /// <summary>Folders recently opened or switched to from the chat (most recent first, max 20).</summary>
        public System.Collections.Generic.List<string> RecentFolders { get; set; } = new();

        public void AddRecentFolder(string folder)
        {
            RecentFolders.RemoveAll(f => string.Equals(f.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            RecentFolders.Insert(0, folder);
            if (RecentFolders.Count > 20) RecentFolders.RemoveRange(20, RecentFolders.Count - 20);
            Save();
        }

        /// <summary>Whether the chat may read (never change) files in the chat's folder to answer questions.</summary>
        public bool ChatReadsFolder { get; set; } = true;

        /// <summary>Whether he occasionally says things on his own in a speech bubble.</summary>
        public bool SelfTalk { get; set; } = true;

        /// <summary>B5: per-pack state (enabled, setting values), keyed by pack id.</summary>
        public System.Collections.Generic.Dictionary<string, ExtraState> Extras { get; set; } = new();

        /// <summary>C9: download the extras catalog (when the Extras window opens, and once a day to look for pack updates).</summary>
        public bool ExtrasOnline { get; set; } = true;
        public DateTime LastExtrasCheck { get; set; }
        /// <summary>The pack updates last announced in a bubble ("id@version,…"), so the same ones aren't announced daily.</summary>
        public string AnnouncedExtrasUpdates { get; set; } = "";

        /// <summary>B5: today's tallies (🍅, 💧, Claude Code tasks…); reset when <see cref="CountersDate"/> isn't today.</summary>
        public System.Collections.Generic.Dictionary<string, int> Counters { get; set; } = new();
        public string CountersDate { get; set; } = "";

        public int Counter(string name)
        {
            ResetCountersIfNewDay();
            return Counters.TryGetValue(name, out int n) ? n : 0;
        }

        public int AddToCounter(string name)
        {
            ResetCountersIfNewDay();
            Counters[name] = Counter(name) + 1;
            Save();
            return Counters[name];
        }

        private void ResetCountersIfNewDay()
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            if (CountersDate == today) return;
            CountersDate = today;
            Counters.Clear();
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string NameOrDefault => string.IsNullOrWhiteSpace(BuddyName) ? "Leo" : BuddyName;

        private static string FilePath => Path.Combine(App.DataDir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception ex) { App.Log("Settings load failed: " + ex.Message); }
            return new Settings();
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { App.Log("Settings save failed: " + ex.Message); }
        }
    }

    /// <summary>B5: what the user chose for one extras pack.</summary>
    public class ExtraState
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Setting values the user changed (missing = the pack's default).</summary>
        public System.Collections.Generic.Dictionary<string, string> Values { get; set; } = new();
        /// <summary>Date (yyyy-MM-dd) each "at" reminder last fired, keyed by reminder index, so a restart doesn't repeat it.</summary>
        public System.Collections.Generic.Dictionary<string, string> FiredOn { get; set; } = new();
        /// <summary>How noticeable this pack's messages are: 0 = as the pack suggests, else 1 subtle / 2 normal / 3 noticeable.</summary>
        public int Attention { get; set; }
        /// <summary>Remind again, more noticeably, when ignored: null = as the pack suggests.</summary>
        public bool? Escalate { get; set; }
    }
}
