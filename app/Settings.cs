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

        public static readonly string[] ChatHotkeyDefaults = { "Ctrl+Alt+Space", "Win+Alt+C", "Ctrl+Shift+Space" };
        public static readonly string[] TrickHotkeyDefaults = { "Ctrl+Alt+T", "Ctrl+Alt+J", "Ctrl+Alt+K" };

        /// <summary>The "is here" notification is shown once (first launch), not on every start.</summary>
        public bool WelcomeShown { get; set; }

        /// <summary>Whether he occasionally says things on his own in a speech bubble.</summary>
        public bool SelfTalk { get; set; } = true;

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
}
