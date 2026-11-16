using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace Assistail
{
    /// <summary>A key combination such as "Ctrl+Alt+Space".</summary>
    public readonly struct Hotkey
    {
        public uint Modifiers { get; }
        public uint Vk { get; }
        public string Text { get; }

        public Hotkey(uint modifiers, uint vk, string text) { Modifiers = modifiers; Vk = vk; Text = text; }

        /// <summary>Parses "Ctrl+Alt+T", "Win+Shift+F5", "Ctrl+Alt+Space", "Ctrl+Alt+1".</summary>
        public static bool TryParse(string? text, out Hotkey hotkey)
        {
            hotkey = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            uint mods = 0;
            Key key = Key.None;
            foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                    case "alt": mods |= Native.MOD_ALT; break;
                    case "shift": mods |= Native.MOD_SHIFT; break;
                    case "win": case "windows": mods |= Native.MOD_WIN; break;
                    default:
                        string name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;   // "1" → Key.D1
                        if (!Enum.TryParse(name, true, out key)) return false;
                        break;
                }
            }
            if (key == Key.None) return false;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return false;
            hotkey = new Hotkey(mods, vk, Format(mods, key));
            return true;
        }

        public static string Format(uint mods, Key key)
        {
            var parts = new List<string>();
            if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
            if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
            string k = key.ToString();
            if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k[1..];   // D1 → 1
            parts.Add(k);
            return string.Join("+", parts);
        }
    }

    /// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
    public sealed class HotkeyService : IDisposable
    {
        public const int ChatId = 0xB0D1, TrickId = 0xB0D2;

        private readonly HwndSource _source;
        private readonly Dictionary<int, Hotkey> _active = new();
        public event Action<int>? Pressed;

        public HotkeyService()
        {
            var p = new HwndSourceParameters("AssistailHotkey")
            {
                Width = 0, Height = 0, WindowStyle = 0,
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        /// <summary>Text of the active hotkey for an id, or "" if none.</summary>
        public string TextOf(int id) => _active.TryGetValue(id, out var h) ? h.Text : "";

        /// <summary>Registers one hotkey for an id (replacing any previous one). False if another app owns it.</summary>
        public bool Register(int id, Hotkey hotkey)
        {
            Unregister(id);
            if (!Native.RegisterHotKey(_source.Handle, id, hotkey.Modifiers | Native.MOD_NOREPEAT, hotkey.Vk)) return false;
            _active[id] = hotkey;
            return true;
        }

        public void Unregister(int id)
        {
            if (_active.Remove(id)) Native.UnregisterHotKey(_source.Handle, id);
        }

        /// <summary>
        /// Applies a setting value: "" = first free default, "None" = disabled, otherwise the given combination.
        /// Returns an error message, or null on success.
        /// </summary>
        public string? Apply(int id, string setting, params string[] defaults)
        {
            if (string.Equals(setting, "None", StringComparison.OrdinalIgnoreCase)) { Unregister(id); return null; }
            if (string.IsNullOrWhiteSpace(setting))
            {
                foreach (var d in defaults)
                    if (Hotkey.TryParse(d, out var hk) && Register(id, hk)) return null;
                Unregister(id);
                return "All default shortcuts are taken by other apps.";
            }
            if (!Hotkey.TryParse(setting, out var custom)) return $"“{setting}” isn't a valid shortcut.";
            if (_active.TryGetValue(id, out var current) && current.Text == custom.Text) return null;
            return Register(id, custom) ? null : $"{custom.Text} is already used by another app.";
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _active.ContainsKey(wParam.ToInt32()))
            {
                handled = true;
                Pressed?.Invoke(wParam.ToInt32());
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in new List<int>(_active.Keys)) Unregister(id);
            _source.Dispose();
        }
    }
}
