using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace Assistail
{
    /// <summary>
    /// B1: one buddy's art and traits. The lion is built into the app (embedded resources under lion/); other buddies
    /// are buddy packs installed from the extras catalog into %LOCALAPPDATA%\Assistail\buddies\&lt;id&gt;\ (buddy.json,
    /// one folder of frames per animation, and the paw reminders). Packs are data only. Every image is checked
    /// (PNG signature, size) before it is decoded; the full check of a pack happens when it is installed.
    /// </summary>
    public sealed class Buddy
    {
        public const int SupportedFormat = 1;
        public const long MaxManifestBytes = 512 * 1024;
        public const long MaxImageBytes = 2 * 1024 * 1024;
        public const int MaxImageSide = 1024;
        public const int MaxFramesPerAnim = 300;
        /// <summary>Animations the app relies on: a buddy pack must have all of them.</summary>
        public static readonly string[] RequiredStates =
        {
            "idle", "hover", "thinking", "talking", "happy", "dragged", "landing", "sleep", "turn", "stand_up", "walk",
            "shake_head", "jump", "stretch_yawn", "tail_chase", "wave", "look_roar", "look_up", "swat",
        };
        /// <summary>Traits a pack may declare (unknown ones are ignored, so older apps still load newer packs).</summary>
        public static readonly HashSet<string> KnownFeatures = new() { "cloth", "lick" };

        public string Id { get; private init; } = "lion";
        public string Name { get; private init; } = "Lion";
        public string Emoji { get; private init; } = "🦁";
        public string Version { get; private init; } = "";
        public string Description { get; private init; } = "";
        /// <summary>How the chat describes him ("a friendly cartoon lion with a curly mane").</summary>
        public string Persona { get; private init; } = "a friendly cartoon lion with a curly mane";
        /// <summary>How he says hello in his self-talk ("Rawr! (that's lion for hello)").</summary>
        public string Hello { get; private init; } = "Rawr! (that's lion for hello)";
        public HashSet<string> Features { get; private init; } = new();
        /// <summary>Menu labels that differ from the lion's (e.g. look_roar → "Look around && meow").</summary>
        public Dictionary<string, string> Labels { get; private init; } = new();
        /// <summary>The pack folder; null for the built-in lion.</summary>
        public string? Folder { get; private init; }
        /// <summary>Why the pack can't be used (null = fine).</summary>
        public string? Error { get; private set; }

        public bool IsBuiltIn => Folder == null;
        public bool IsUsable => Error == null;
        public bool Has(string feature) => Features.Contains(feature);
        public string Label(string id, string fallback) => Labels.TryGetValue(id, out var l) ? l : fallback;

        public static readonly Buddy Lion = new();

        /// <summary>Where buddy packs are installed.</summary>
        public static string PacksFolder => Path.Combine(App.DataDir, "buddies");

        // ------------------------------------------------------------------ finding

        /// <summary>The installed buddy packs (usable or not, so the menu can say why one doesn't work).</summary>
        public static List<Buddy> Installed()
        {
            var list = new List<Buddy>();
            try
            {
                if (!Directory.Exists(PacksFolder)) return list;
                foreach (var dir in Directory.GetDirectories(PacksFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    if (!Path.GetFileName(dir).StartsWith(".")) list.Add(Load(dir));   // ".staging" etc. are the installer's
            }
            catch (Exception ex) { App.Log("Buddy packs couldn't be listed: " + ex.Message); }
            return list;
        }

        /// <summary>The buddy with this id, or the lion if it isn't installed or can't be used.</summary>
        public static Buddy Find(string? id)
        {
            if (string.IsNullOrEmpty(id) || id == "lion") return Lion;
            string dir = Path.Combine(PacksFolder, id);
            if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,39}$") || !Directory.Exists(dir)) return Lion;
            var b = Load(dir);
            if (b.IsUsable) return b;
            App.Log($"Buddy '{id}' can't be used ({b.Error}); using the lion.");
            return Lion;
        }

        // ------------------------------------------------------------------ loading a pack

        public static Buddy Load(string folder)
        {
            string id = Path.GetFileName(folder);
            try
            {
                string file = Path.Combine(folder, "buddy.json");
                if (!File.Exists(file)) return Broken(id, folder, "buddy.json is missing.");
                if (new FileInfo(file).Length > MaxManifestBytes) return Broken(id, folder, "buddy.json is too big.");
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                int format = r.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
                if (format < 1) return Broken(id, folder, "\"format\" is missing.");
                if (format > SupportedFormat) return Broken(id, folder, $"Made for a newer Assistail (format {format}).");
                string pid = Text(r, "id");
                if (!string.Equals(pid, id, StringComparison.OrdinalIgnoreCase)) return Broken(id, folder, "The folder name doesn't match the buddy id.");
                string minApp = Text(r, "minApp");
                if (minApp.Length > 0 && System.Version.TryParse(minApp, out var min) && min > UpdateService.Current)
                    return Broken(id, folder, $"Needs Assistail {minApp} or newer.");
                var b = new Buddy
                {
                    Id = id,
                    Folder = folder,
                    Name = Limit(Text(r, "name"), 40, id),
                    Emoji = Limit(Text(r, "emoji"), 8, "🐾"),
                    Version = Text(r, "version"),
                    Description = Limit(Text(r, "description"), 300, ""),
                    Persona = Limit(Text(r, "persona"), 200, "a friendly cartoon animal"),
                    Hello = Limit(Text(r, "hello"), 120, "Hello!"),
                    Features = r.TryGetProperty("features", out var fe) && fe.ValueKind == JsonValueKind.Array
                        ? fe.EnumerateArray().Select(x => x.GetString() ?? "").Where(KnownFeatures.Contains).ToHashSet() : new(),
                    Labels = r.TryGetProperty("labels", out var lb) && lb.ValueKind == JsonValueKind.Object
                        ? lb.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                            .ToDictionary(p => p.Name, p => Limit(p.Value.GetString() ?? "", 40, p.Name)) : new(),
                };
                if (!r.TryGetProperty("anims", out var anims) || anims.ValueKind != JsonValueKind.Object) return Broken(id, folder, "\"anims\" is missing.");
                var names = new HashSet<string>(anims.EnumerateObject().Select(p => p.Name));
                foreach (var s in RequiredStates)
                    if (!names.Contains(s)) return Broken(id, folder, $"The animation \"{s}\" is missing.");
                foreach (var p in anims.EnumerateObject())
                {
                    if (!SafeName(p.Name)) return Broken(id, folder, $"Bad animation name \"{p.Name}\".");
                    int n = p.Value.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetInt32() : 0;
                    if (n < 1 || n > MaxFramesPerAnim) return Broken(id, folder, $"\"{p.Name}\" has a bad frame count.");
                    // Cheap presence check at start-up (first and last frame); every file was checked when it was installed.
                    foreach (int i in new[] { 1, n })
                        if (!File.Exists(Path.Combine(folder, p.Name, $"f_{i:D4}.png"))) return Broken(id, folder, $"Frames of \"{p.Name}\" are missing.");
                }
                return b;
            }
            catch (JsonException ex) { return Broken(id, folder, "buddy.json isn't valid JSON: " + ex.Message); }
            catch (Exception ex) { return Broken(id, folder, "Couldn't read the buddy: " + ex.Message); }
        }

        private static Buddy Broken(string id, string folder, string error) => new() { Id = id, Name = id, Folder = folder, Error = error };
        private static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static string Limit(string s, int max, string fallback) => s.Length == 0 ? fallback : s.Length > max ? s[..max] : s;
        internal static bool SafeName(string s) => Regex.IsMatch(s, "^[a-z0-9_]{1,40}$");

        // ------------------------------------------------------------------ reading files

        /// <summary>The animations manifest: the lion's lion.json, or the "anims" object of a pack's buddy.json.</summary>
        public JsonDocument OpenAnims()
        {
            if (IsBuiltIn)
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/lion/lion.json"))
                          ?? throw new InvalidOperationException("lion.json resource missing");
                return JsonDocument.Parse(res.Stream);
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder!, "buddy.json")));
            return JsonDocument.Parse(doc.RootElement.GetProperty("anims").GetRawText());
        }

        /// <summary>The paw reminders' manifest (paw.json), or null if this buddy has none.</summary>
        public Stream? OpenPawManifest()
        {
            if (IsBuiltIn) return Application.GetResourceStream(new Uri("pack://application:,,,/lion/paw.json"))?.Stream;
            string f = Path.Combine(Folder!, "paw.json");
            return File.Exists(f) && new FileInfo(f).Length <= MaxManifestBytes ? File.OpenRead(f) : null;
        }

        /// <summary>
        /// One image of this buddy ("idle/f_0001.png"), checked to be a sane PNG first. The caller disposes it.
        /// </summary>
        public Stream OpenImage(string relPath)
        {
            if (IsBuiltIn)
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/lion/" + relPath))
                          ?? throw new FileNotFoundException(relPath);
                return res.Stream;
            }
            var parts = relPath.Split('/');
            if (parts.Length != 2 || !SafeName(parts[0]) || !Regex.IsMatch(parts[1], @"^f_\d{4}\.png$"))
                throw new InvalidDataException("Bad image path " + relPath);
            string path = Path.Combine(Folder!, parts[0], parts[1]);
            var fs = File.OpenRead(path);
            try { CheckPng(fs); fs.Position = 0; return fs; }
            catch { fs.Dispose(); throw; }
        }

        /// <summary>Throws unless the stream starts like a reasonable PNG (signature, IHDR, size limits).</summary>
        public static void CheckPng(Stream s)
        {
            if (s.Length > MaxImageBytes) throw new InvalidDataException("Image too big.");
            Span<byte> h = stackalloc byte[24];
            if (s.Read(h) != 24) throw new InvalidDataException("Not a PNG.");
            ReadOnlySpan<byte> sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!h[..8].SequenceEqual(sig) || h[12] != (byte)'I' || h[13] != (byte)'H' || h[14] != (byte)'D' || h[15] != (byte)'R')
                throw new InvalidDataException("Not a PNG.");
            int w = (h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19];
            int ht = (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23];
            if (w < 1 || ht < 1 || w > MaxImageSide || ht > MaxImageSide) throw new InvalidDataException("Image size out of range.");
        }
    }
}
