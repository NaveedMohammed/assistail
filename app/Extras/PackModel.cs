using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assistail.Extras
{
    /// <summary>
    /// B5: an extras pack (<c>extra.json</c>). Packs are data only: reminders and routines built from a fixed set of
    /// actions (say, trick, count). Parsing is strict: anything unknown makes the pack "needs a newer Assistail"
    /// instead of being half-understood.
    /// </summary>
    public sealed class Pack
    {
        public const int SupportedFormat = 1;
        /// <summary>The version "minApp" is compared with (the catalog tool sets a high one so it can check any pack).</summary>
        public static Version? AppVersionOverride;
        public const long MaxFileBytes = 64 * 1024;

        public string Id = "", Name = "", Version = "", Author = "", Description = "", MinApp = "", Icon = "";
        public List<string> Topics = new();
        public List<PackSetting> Settings = new();
        public List<Reminder> Reminders = new();
        public Routine? Routine;

        /// <summary>The folder the pack was loaded from.</summary>
        public string Folder = "";
        /// <summary>Why the pack can't be used (null = fine).</summary>
        public string? Error;
        /// <summary>The pack is fine but made for a newer Assistail.</summary>
        public bool NeedsNewer;

        public bool IsUsable => Error == null;
        public string Title => (Icon.Length > 0 ? Icon + " " : "") + (Name.Length > 0 ? Name : Id);
        public PackSetting? Setting(string key) => Settings.FirstOrDefault(s => s.Key == key);

        // ------------------------------------------------------------------ loading

        public static Pack Load(string folder)
        {
            var pack = new Pack { Folder = folder, Id = Path.GetFileName(folder) };
            string file = Path.Combine(folder, "extra.json");
            try
            {
                if (!File.Exists(file)) { pack.Error = "extra.json is missing."; return pack; }
                if (new FileInfo(file).Length > MaxFileBytes) { pack.Error = "extra.json is too big."; return pack; }
                Parse(File.ReadAllText(file), pack);
                if (pack.Error == null && !string.Equals(pack.Id, Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
                    pack.Error = $"The folder name doesn't match the pack id \"{pack.Id}\".";
            }
            catch (PackException ex) { pack.Error = ex.Message; pack.NeedsNewer = ex.NeedsNewer; }
            catch (JsonException ex) { pack.Error = "extra.json isn't valid JSON: " + ex.Message; }
            catch (Exception ex) { pack.Error = "Couldn't read the pack: " + ex.Message; }
            return pack;
        }

        /// <summary>Parses pack JSON (throws <see cref="PackException"/> or <see cref="JsonException"/>).</summary>
        public static void Parse(string json, Pack p)
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PackException("extra.json must be an object.");

            // The format check comes first: a newer format may have fields this version can't read.
            int format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
            if (format < 1) throw new PackException("\"format\" is missing.");
            if (format > SupportedFormat) throw new PackException($"Made for a newer Assistail (pack format {format}).", needsNewer: true);

            Known(root, "pack", "format", "id", "name", "version", "author", "description", "minApp", "icon", "topics", "settings", "reminders", "routine");
            p.Id = Str(root, "id", required: true);
            if (!Regex.IsMatch(p.Id, "^[a-z0-9][a-z0-9-]{1,39}$")) throw new PackException("\"id\" must be 2–40 lowercase letters, digits or dashes.");
            p.Name = Text(root, "name", 60, required: true);
            p.Version = Str(root, "version", required: true);
            if (!System.Version.TryParse(p.Version, out _)) throw new PackException("\"version\" must look like 1.0.0.");
            p.Author = Text(root, "author", 60);
            p.Description = Text(root, "description", 300);
            p.Icon = Text(root, "icon", 8);
            p.MinApp = Str(root, "minApp");
            if (p.MinApp.Length > 0)
            {
                if (!System.Version.TryParse(p.MinApp, out var min)) throw new PackException("\"minApp\" must look like 2.4.0.");
                if (min > (AppVersionOverride ?? UpdateService.Current)) throw new PackException($"Needs Assistail {p.MinApp} or newer.", needsNewer: true);
            }
            if (root.TryGetProperty("topics", out var topics)) p.Topics = Strings(topics, "topics", 20);

            if (root.TryGetProperty("settings", out var settings))
            {
                if (settings.ValueKind != JsonValueKind.Object) throw new PackException("\"settings\" must be an object.");
                foreach (var s in settings.EnumerateObject()) p.Settings.Add(PackSetting.Parse(s.Name, s.Value));
            }
            if (root.TryGetProperty("reminders", out var reminders))
            {
                if (reminders.ValueKind != JsonValueKind.Array) throw new PackException("\"reminders\" must be a list.");
                foreach (var r in reminders.EnumerateArray()) p.Reminders.Add(Reminder.Parse(r, p, p.Reminders.Count));
            }
            if (root.TryGetProperty("routine", out var routine)) p.Routine = Routine.Parse(routine, p);
            if (p.Reminders.Count == 0 && p.Routine == null) throw new PackException("The pack has no reminders and no routine.");
        }

        // ------------------------------------------------------------------ JSON helpers (shared by the parts)

        internal static void Known(JsonElement e, string what, params string[] keys)
        {
            foreach (var prop in e.EnumerateObject())
                if (Array.IndexOf(keys, prop.Name) < 0)
                    throw new PackException($"Unknown field \"{prop.Name}\" in {what}. It may need a newer Assistail.", needsNewer: true);
        }

        internal static string Str(JsonElement e, string key, bool required = false)
        {
            if (!e.TryGetProperty(key, out var v)) { if (required) throw new PackException($"\"{key}\" is missing."); return ""; }
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => throw new PackException($"\"{key}\" must be text."),
            };
        }

        internal static string Text(JsonElement e, string key, int max, bool required = false)
        {
            string s = Str(e, key, required);
            if (s.Length > max) throw new PackException($"\"{key}\" is longer than {max} characters.");
            return s;
        }

        internal static bool Bool(JsonElement e, string key)
        {
            if (!e.TryGetProperty(key, out var v)) return false;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            throw new PackException($"\"{key}\" must be true or false.");
        }

        internal static List<string> Strings(JsonElement e, string key, int max)
        {
            var list = new List<string>();
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString() ?? "");
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray())
                    list.Add(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : throw new PackException($"\"{key}\" must be text or a list of text."));
            else throw new PackException($"\"{key}\" must be text or a list of text.");
            if (list.Count == 0 || list.Count > max) throw new PackException($"\"{key}\" needs 1–{max} entries.");
            return list;
        }

        /// <summary>A number, or "{setting}" naming a number setting of the pack.</summary>
        internal static string NumberRef(JsonElement e, string key, Pack p, bool required = false)
        {
            string s = Str(e, key, required);
            if (s.Length == 0) return s;
            var m = Regex.Match(s, @"^\{([a-zA-Z0-9_-]+)\}$");
            if (m.Success)
            {
                var st = p.Setting(m.Groups[1].Value) ?? throw new PackException($"\"{key}\" uses {s}, but there's no such setting.");
                if (st.Type is not ("minutes" or "number")) throw new PackException($"\"{key}\" uses {s}, which isn't a number setting.");
                return s;
            }
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) || d <= 0)
                throw new PackException($"\"{key}\" must be a positive number or a {{setting}}.");
            return s;
        }

        /// <summary>A literal value, or "{setting}" naming a setting of the given types.</summary>
        internal static string ValueRef(JsonElement e, string key, Pack p, Func<string, bool> literalOk, params string[] types)
        {
            string s = Str(e, key);
            if (s.Length == 0) return s;
            var m = Regex.Match(s, @"^\{([a-zA-Z0-9_-]+)\}$");
            if (m.Success)
            {
                var st = p.Setting(m.Groups[1].Value) ?? throw new PackException($"\"{key}\" uses {s}, but there's no such setting.");
                if (Array.IndexOf(types, st.Type) < 0) throw new PackException($"\"{key}\" uses {s}, which is the wrong kind of setting.");
                return s;
            }
            if (!literalOk(s)) throw new PackException($"\"{key}\" has an invalid value \"{s}\".");
            return s;
        }

        internal static bool IsTime(string s) => Regex.IsMatch(s, @"^([01]?\d|2[0-3]):[0-5]\d$");
    }

    public sealed class PackException : Exception
    {
        public bool NeedsNewer { get; }
        public PackException(string message, bool needsNewer = false) : base(message) => NeedsNewer = needsNewer;
    }

    public sealed class PackSetting
    {
        public string Key = "", Type = "", Label = "", Default = "", Unit = "";
        public double Min = 1, Max = 1440;
        public List<(string Value, string Label)> Choices = new();

        public static PackSetting Parse(string key, JsonElement e)
        {
            if (!Regex.IsMatch(key, "^[a-zA-Z0-9_-]{1,30}$")) throw new PackException($"Setting name \"{key}\" isn't allowed.");
            if (e.ValueKind != JsonValueKind.Object) throw new PackException($"Setting \"{key}\" must be an object.");
            Pack.Known(e, $"setting \"{key}\"", "type", "label", "default", "min", "max", "unit", "choices");
            var s = new PackSetting { Key = key, Type = Pack.Str(e, "type", required: true), Label = Pack.Text(e, "label", 40), Unit = Pack.Text(e, "unit", 20) };
            if (s.Label.Length == 0) s.Label = key;
            if (e.TryGetProperty("default", out var d))
                s.Default = d.ValueKind switch
                {
                    JsonValueKind.True => "true", JsonValueKind.False => "false",
                    JsonValueKind.Number => d.GetRawText(), JsonValueKind.String => d.GetString() ?? "",
                    _ => throw new PackException($"Setting \"{key}\" has an invalid default."),
                };
            if (e.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number) s.Min = mn.GetDouble();
            if (e.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number) s.Max = mx.GetDouble();
            switch (s.Type)
            {
                case "minutes" or "number":
                    if (!s.TryNumber(s.Default, out _)) throw new PackException($"Setting \"{key}\" needs a default between {s.Min} and {s.Max}.");
                    break;
                case "time":
                    if (!Pack.IsTime(s.Default)) throw new PackException($"Setting \"{key}\" needs a default time like 18:00.");
                    break;
                case "bool":
                    if (s.Default.Length == 0) s.Default = "false";
                    if (s.Default is not ("true" or "false")) throw new PackException($"Setting \"{key}\" needs true or false.");
                    break;
                case "choice":
                    if (!e.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array) throw new PackException($"Setting \"{key}\" needs \"choices\".");
                    foreach (var c in ch.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.String) { s.Choices.Add((c.GetString()!, c.GetString()!)); continue; }
                        Pack.Known(c, $"a choice of \"{key}\"", "value", "label");
                        string v = Pack.Str(c, "value", required: true);
                        string l = Pack.Text(c, "label", 30);
                        s.Choices.Add((v, l.Length > 0 ? l : v));
                    }
                    if (s.Choices.Count == 0 || !s.Choices.Exists(c => c.Value == s.Default)) throw new PackException($"Setting \"{key}\" needs a default that is one of its choices.");
                    break;
                default: throw new PackException($"Setting \"{key}\" has an unknown type \"{s.Type}\".", needsNewer: true);
            }
            return s;
        }

        public bool TryNumber(string text, out double value) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
            && value >= Min && value <= Max;

        public bool IsValid(string value) => Type switch
        {
            "minutes" or "number" => TryNumber(value, out _),
            "time" => Pack.IsTime(value),
            "bool" => value is "true" or "false",
            "choice" => Choices.Exists(c => c.Value == value),
            _ => false,
        };
    }

    public sealed class PackAction
    {
        /// <summary>Tricks a pack may use: the menu tricks plus a few short reactions.</summary>
        public static readonly HashSet<string> AllowedTricks = new(LionWindow.Tricks.Select(t => t.Id))
        { "happy", "look_up", "swat", "hover", "sleep" };

        public string Kind = "";            // say | trick | count
        public List<string> Lines = new();  // say
        public double? Seconds;             // say
        public string Value = "";           // trick id or counter name
        public int Attention;               // say: 1 subtle … 5 boop; 0 = inherit

        public static List<PackAction> ParseList(JsonElement? e, string what)
        {
            var list = new List<PackAction>();
            if (e is not { } arr) return list;
            if (arr.ValueKind != JsonValueKind.Array) throw new PackException($"\"{what}\" must be a list of actions.");
            foreach (var a in arr.EnumerateArray()) list.Add(Parse(a, what));
            if (list.Count > 10) throw new PackException($"\"{what}\" has more than 10 actions.");
            return list;
        }

        private static PackAction Parse(JsonElement e, string what)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new PackException($"Each action in \"{what}\" must be an object.");
            var a = new PackAction();
            if (e.TryGetProperty("say", out var say))
            {
                Pack.Known(e, $"a \"say\" action in \"{what}\"", "say", "seconds", "attention");
                a.Attention = Reminder.ParseAttention(e);
                a.Kind = "say";
                a.Lines = Pack.Strings(say, "say", 20);
                foreach (var l in a.Lines)
                    if (l.Length > 200) throw new PackException("A \"say\" line is longer than 200 characters.");
                if (e.TryGetProperty("seconds", out var sec))
                {
                    if (sec.ValueKind != JsonValueKind.Number || sec.GetDouble() < 2 || sec.GetDouble() > 120)
                        throw new PackException("\"seconds\" must be a number from 2 to 120.");
                    a.Seconds = sec.GetDouble();
                }
            }
            else if (e.TryGetProperty("trick", out _))
            {
                Pack.Known(e, $"a \"trick\" action in \"{what}\"", "trick");
                a.Kind = "trick";
                a.Value = Pack.Str(e, "trick");
                if (!AllowedTricks.Contains(a.Value)) throw new PackException($"Unknown trick \"{a.Value}\".", needsNewer: true);
            }
            else if (e.TryGetProperty("count", out _))
            {
                Pack.Known(e, $"a \"count\" action in \"{what}\"", "count");
                a.Kind = "count";
                a.Value = Pack.Str(e, "count");
                if (!Regex.IsMatch(a.Value, "^[a-z0-9-]{1,30}$")) throw new PackException($"Counter name \"{a.Value}\" isn't allowed.");
            }
            else
            {
                string first = e.EnumerateObject().Select(p => p.Name).FirstOrDefault() ?? "(empty)";
                throw new PackException($"Unknown action \"{first}\" in \"{what}\". It may need a newer Assistail.", needsNewer: true);
            }
            return a;
        }
    }

    public sealed class Reminder
    {
        public static readonly string[] QuietRules = { "fullscreen", "dnd", "focus", "chat" };

        public int Index;
        public string Every = "", At = "", Days = "";
        public bool ActiveTimeOnly;
        public List<string> QuietWhen = new();
        public double Snooze;
        /// <summary>How noticeable the reminder is (1 subtle, 2 normal, 3 noticeable).</summary>
        public int Attention = 2;
        /// <summary>If ignored, remind once more a little later, one level more noticeable.</summary>
        public bool Escalate;
        public List<PackAction> Do = new(), OnClick = new();

        /// <summary>1 subtle, 2 normal, 3 noticeable, 4 sign (paw with a board), 5 boop (paw on the glass).</summary>
        public const int MaxAttention = 5;

        /// <summary>"attention": 1–3 (0 when missing).</summary>
        internal static int ParseAttention(JsonElement e)
        {
            if (!e.TryGetProperty("attention", out var v)) return 0;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n)) throw new PackException("\"attention\" must be 1 to 5.");
            if (n > MaxAttention) throw new PackException($"Attention level {n} needs a newer Assistail.", needsNewer: true);
            if (n < 1) throw new PackException("\"attention\" must be 1 to 5.");
            return n;
        }

        public static Reminder Parse(JsonElement e, Pack p, int index)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new PackException("Each reminder must be an object.");
            Pack.Known(e, "a reminder", "every", "at", "days", "activeTimeOnly", "quietWhen", "snooze", "attention", "escalate", "do", "onClick");
            var r = new Reminder
            {
                Index = index,
                Every = Pack.NumberRef(e, "every", p),
                At = Pack.ValueRef(e, "at", p, Pack.IsTime, "time"),
                Days = Pack.ValueRef(e, "days", p, d => d is "daily" or "weekdays", "choice"),
                ActiveTimeOnly = Pack.Bool(e, "activeTimeOnly"),
                Escalate = Pack.Bool(e, "escalate"),
            };
            int att = ParseAttention(e);
            if (att > 0) r.Attention = att;
            if ((r.Every.Length > 0) == (r.At.Length > 0)) throw new PackException("A reminder needs either \"every\" or \"at\".");
            if (e.TryGetProperty("quietWhen", out var q))
            {
                r.QuietWhen = Pack.Strings(q, "quietWhen", 10);
                foreach (var rule in r.QuietWhen)
                    if (Array.IndexOf(QuietRules, rule) < 0) throw new PackException($"Unknown quiet rule \"{rule}\".", needsNewer: true);
            }
            if (e.TryGetProperty("snooze", out var sn))
            {
                if (sn.ValueKind != JsonValueKind.Number || sn.GetDouble() < 1 || sn.GetDouble() > 240) throw new PackException("\"snooze\" must be 1–240 minutes.");
                r.Snooze = sn.GetDouble();
            }
            r.Do = PackAction.ParseList(e.TryGetProperty("do", out var d) ? d : null, "do");
            r.OnClick = PackAction.ParseList(e.TryGetProperty("onClick", out var c) ? c : null, "onClick");
            if (r.Do.Count == 0) throw new PackException("A reminder needs at least one action in \"do\".");
            return r;
        }
    }

    public sealed class RoutineStep
    {
        public string Id = "", Name = "", Minutes = "", Pose = "", Every = "", ExceptEvery = "";
        public bool Focus, Confirm, Escalate;
        public List<PackAction> Start = new(), Nudge = new(), End = new();
    }

    public sealed class Routine
    {
        public string Label = "";
        public List<RoutineStep> Steps = new();

        public static Routine Parse(JsonElement e, Pack p)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new PackException("\"routine\" must be an object.");
            Pack.Known(e, "the routine", "label", "steps");
            var r = new Routine { Label = Pack.Text(e, "label", 30) };
            if (r.Label.Length == 0) r.Label = p.Name;
            if (!e.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) throw new PackException("The routine needs \"steps\".");
            foreach (var s in steps.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) throw new PackException("Each routine step must be an object.");
                Pack.Known(s, "a routine step", "id", "name", "minutes", "focus", "pose", "confirm", "escalate", "every", "exceptEvery", "start", "nudge", "end");
                var step = new RoutineStep
                {
                    Id = Pack.Str(s, "id", required: true),
                    Name = Pack.Text(s, "name", 30),
                    Minutes = Pack.NumberRef(s, "minutes", p, required: true),
                    Focus = Pack.Bool(s, "focus"),
                    Confirm = Pack.Bool(s, "confirm"),
                    Escalate = Pack.Bool(s, "escalate"),
                    Pose = Pack.Str(s, "pose"),
                    Every = Pack.NumberRef(s, "every", p),
                    ExceptEvery = Pack.NumberRef(s, "exceptEvery", p),
                    Start = PackAction.ParseList(s.TryGetProperty("start", out var a) ? a : null, "start"),
                    Nudge = PackAction.ParseList(s.TryGetProperty("nudge", out var n) ? n : null, "nudge"),
                    End = PackAction.ParseList(s.TryGetProperty("end", out var en) ? en : null, "end"),
                };
                if (step.Name.Length == 0) step.Name = step.Id;
                if (step.Pose is not ("" or "sleep")) throw new PackException($"Unknown pose \"{step.Pose}\".", needsNewer: true);
                if (r.Steps.Exists(x => x.Id == step.Id)) throw new PackException($"Two routine steps are called \"{step.Id}\".");
                r.Steps.Add(step);
            }
            if (r.Steps.Count == 0 || r.Steps.Count > 10) throw new PackException("The routine needs 1–10 steps.");
            return r;
        }
    }
}
