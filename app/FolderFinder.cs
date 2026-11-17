using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Assistail
{
    /// <summary>What the user asked for: open Claude Code in, or switch the chat to, some folder.</summary>
    public sealed class FolderRequest
    {
        public bool Open;          // true = open Claude Code there; false = switch the chat's folder
        public bool Here;          // "open claude here": use the chat's current folder
        public string? Path;       // an explicit path, if given
        public string? Name;       // a folder name to search for
        public char? Drive;        // drive hint ("in C drive")
        public string Raw = "";

        public string Describe() =>
            Path ?? (Name != null ? $"“{Name}”" + (Drive != null ? $" on {char.ToUpperInvariant(Drive.Value)}:" : "") : "that folder");
    }

    /// <summary>
    /// Understands chat requests like "open claude in downloads", "switch to C:\code\app",
    /// "open claude in the folder in C drive with a name naveed", and finds the folder on disk.
    /// </summary>
    public static class FolderFinder
    {
        private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // "open claude [code] <rest>"  /  "open <loc> in claude"  /  "switch to <loc>"  /  "cd <loc>"
        private static readonly Regex OpenClaude = new(@"^(?:open|launch|start|run|fire\s+up)\s+(?:up\s+)?(?:claude(?:\s*code)?|cc)\b\s*(?<rest>.*)$", Opt);
        private static readonly Regex OpenInClaude = new(@"^(?:open|launch|start)\s+(?<rest>.+?)\s+(?:in|with)\s+claude(?:\s*code)?$", Opt);
        private static readonly Regex Switch = new(@"^(?:switch|change)\s+(?:the\s+)?(?:folder\s+|directory\s+|location\s+)?(?:to|into)\s+(?<rest>.+)$", Opt);
        private static readonly Regex GoTo = new(@"^(?:go|move|jump|navigate)\s+(?:in)?to\s+(?<rest>.+)$", Opt);
        private static readonly Regex Cd = new(@"^cd\s+(?<rest>.+)$", Opt);

        private static readonly Regex PathRx = new(@"(?<p>[a-z]:\\[^""<>|?*\r\n]*|~[\\/][^""<>|?*\r\n]*|%[a-z_]+%[^""<>|?*\r\n]*|\\\\[^""<>|?*\r\n]+)", Opt);
        private static readonly Regex Quoted = new(@"[""“”'‘’](?<q>[^""“”'‘’]+)[""“”'‘’]", Opt);
        private static readonly Regex DriveRx = new(@"\b(?<d>[a-z])\s*:?\s*(?:drive|disk)\b|\b(?:drive|disk)\s+(?<d>[a-z])\b|(?<![\w\\])(?<d>[a-z]):(?![\\/\w])", Opt);
        private static readonly Regex NamedRx = new(@"(?:named|called|titled|with\s+(?:a\s+|the\s+)?name(?:\s+of)?|name(?:d)?(?:\s+is)?|whose\s+name\s+is)\s+(?<n>.+)$", Opt);

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "my", "our", "this", "that", "folder", "folders", "directory", "dir", "location", "place",
            "project", "repo", "repository", "in", "on", "at", "inside", "of", "with", "into", "to", "from", "drive", "disk",
            "called", "named", "name", "please", "thanks", "thank", "you", "there", "is", "which", "whose", "for", "me",
            "open", "claude", "can", "could", "would", "will", "hey", "hi", "and", "it", "up", "pls",
        };
        // "claude code" / "cc" mean the app, not a folder; a lone "code" is a real folder name ("the code folder").
        private static readonly Regex AppName = new(@"(?<![\w-])(?:claude(?:\s*code)?|cc)(?![\w-])", Opt);

        private static readonly HashSet<string> HereWords = new(StringComparer.OrdinalIgnoreCase)
        { "", "here", "this folder", "current folder", "this location", "there", "in here", "the current folder", "this one" };

        /// <summary>Parses a chat message. Returns null if it isn't a folder command.</summary>
        public static FolderRequest? Parse(string text, string buddyName)
        {
            string t = Regex.Replace(text.Trim(), @"\s+", " ");
            t = Regex.Replace(t, @"[.!?]+$", "");
            // Drop polite openers and the buddy's own name: "hey Leo, please open claude here"
            t = Regex.Replace(t, $@"^(?:(?:hey|hi|ok|okay)\s+)?(?:{Regex.Escape(buddyName)}\s*,?\s*)?(?:(?:please|can you|could you|would you)\s+)*", "", Opt).Trim();

            Match m;
            var req = new FolderRequest { Raw = text };
            if ((m = OpenClaude.Match(t)).Success || (m = OpenInClaude.Match(t)).Success) req.Open = true;
            else if ((m = Switch.Match(t)).Success || (m = GoTo.Match(t)).Success || (m = Cd.Match(t)).Success) req.Open = false;
            else return null;

            string loc = m.Groups["rest"].Value.Trim();
            loc = Regex.Replace(loc, @"\s+(?:please|pls|thanks|thank you)$", "", Opt);
            loc = Regex.Replace(loc, @"^(?:(?:in|at|on|inside|for|to|into)\s+)+", "", Opt).Trim();
            if (HereWords.Contains(loc)) { req.Here = true; return req; }

            ParseLocation(loc, req);
            if (req.Path == null && req.Name == null && req.Drive == null) req.Here = true;
            return req;
        }

        /// <summary>Fills Path / Name / Drive from a free-form location phrase.</summary>
        public static void ParseLocation(string loc, FolderRequest req)
        {
            var pm = PathRx.Match(loc);
            if (pm.Success) { req.Path = pm.Groups["p"].Value.Trim().TrimEnd('.', ',', ' '); return; }

            var qm = Quoted.Match(loc);
            if (qm.Success)
            {
                string q = qm.Groups["q"].Value.Trim();
                if (PathRx.IsMatch(q)) req.Path = q; else req.Name = q;
            }

            var dm = DriveRx.Match(loc);
            if (dm.Success) { req.Drive = char.ToUpperInvariant(dm.Groups["d"].Value[0]); loc = DriveRx.Replace(loc, " "); }
            if (req.Name != null) return;

            var nm = NamedRx.Match(loc);
            string phrase = AppName.Replace(nm.Success ? nm.Groups["n"].Value : loc, " ");
            var words = Regex.Split(phrase, @"[\s,]+").Where(w => w.Length > 0 && !StopWords.Contains(w)).ToList();
            if (words.Count > 0) req.Name = string.Join(" ", words);
        }

        /// <summary>Known folders by everyday name.</summary>
        private static string? KnownFolder(string name)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return name.ToLowerInvariant() switch
            {
                "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "documents" or "docs" or "my documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "downloads" or "download" => Path.Combine(home, "Downloads"),
                "pictures" or "photos" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "home" or "user" or "user folder" or "profile" or "home folder" => home,
                _ => null,
            };
        }

        private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "program files", "program files (x86)", "programdata", "$recycle.bin", "system volume information",
            "recovery", "perflogs", "msocache", "$windows.~bt", "$winreagent", "windowsapps", "appdata",
            "node_modules", ".git", "bin", "obj", ".vs", ".idea", "__pycache__", "packages", ".nuget", ".cache",
        };

        private static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"[\s\-_.]+", "");

        /// <summary>
        /// Finds candidate folders for a request, best first (max 6). Searches recent folders, then a breadth-first
        /// scan (shallow folders first) of the hinted drive or the user's common places and fixed drives, within a time limit.
        /// </summary>
        public static List<string> Search(FolderRequest req, IReadOnlyList<string> recent, int timeLimitMs = 2500, CancellationToken ct = default)
        {
            // 1. Explicit path
            if (req.Path != null)
            {
                string p = Environment.ExpandEnvironmentVariables(req.Path);
                if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
                try { p = System.IO.Path.GetFullPath(p); } catch { }
                if (Directory.Exists(p)) return new List<string> { p.TrimEnd('\\') + (p.Length <= 3 ? "\\" : "") };
                // Not found as typed: search for its last part instead (typo-tolerant-ish).
                req.Name = System.IO.Path.GetFileName(p.TrimEnd('\\'));
                if (p.Length >= 2 && p[1] == ':') req.Drive = char.ToUpperInvariant(p[0]);
                req.Path = null;
                if (string.IsNullOrEmpty(req.Name)) return new List<string>();
            }
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                if (req.Drive is char d && Directory.Exists($"{d}:\\")) return new List<string> { $"{d}:\\" };
                return new List<string>();
            }

            // 2. Known folders
            if (req.Drive == null && KnownFolder(req.Name) is string known && Directory.Exists(known)) return new List<string> { known };

            string key = Key(req.Name);
            var found = new Dictionary<string, (int score, int depth)>(StringComparer.OrdinalIgnoreCase);
            void Consider(string path, string dirName, int depth, int bonus = 0)
            {
                string k = Key(dirName);
                int score = k == key ? 100 : k.StartsWith(key) ? 70 : (key.Length >= 3 && k.Contains(key)) ? 50 : 0;
                if (score == 0) return;
                if (req.Drive is char dr && !path.StartsWith($"{dr}:", StringComparison.OrdinalIgnoreCase)) return;
                score += bonus;
                if (!found.TryGetValue(path, out var old) || old.score < score) found[path] = (score, depth);
            }

            // 3. Recent folders (and their parents' names) get a bonus
            foreach (var r in recent)
                if (Directory.Exists(r)) Consider(r.TrimEnd('\\'), System.IO.Path.GetFileName(r.TrimEnd('\\')), 0, 20);

            // 4. Breadth-first scan
            var sw = Stopwatch.StartNew();
            var roots = new List<(string path, int maxDepth)>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // The user's usual places first (on the hinted drive, if any), then whole drives a bit deeper.
            foreach (var r in new[] { home, Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                      Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                      System.IO.Path.Combine(home, "code"), System.IO.Path.Combine(home, "source", "repos"),
                                      System.IO.Path.Combine(home, "projects") })
                if (Directory.Exists(r) && (req.Drive == null || r.StartsWith($"{req.Drive}:", StringComparison.OrdinalIgnoreCase)))
                    roots.Add((r, 5));
            if (req.Drive is char drive) roots.Add(($"{drive}:\\", 6));
            else
                foreach (var dinfo in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
                    roots.Add((dinfo.RootDirectory.FullName, 5));

            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true, RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string path, int depth, int max)>();
            foreach (var (p, max) in roots)
            {
                string rp = p.TrimEnd('\\');
                if (visited.Add(rp)) queue.Enqueue((p, 0, max));
            }
            int exact = 0;
            while (queue.Count > 0 && sw.ElapsedMilliseconds < timeLimitMs && !ct.IsCancellationRequested && exact < 8)
            {
                var (dir, depth, max) = queue.Dequeue();
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(dir, "*", opts); } catch { continue; }
                foreach (var sub in subs)
                {
                    string name = System.IO.Path.GetFileName(sub);
                    if (SkipDirs.Contains(name) || name.StartsWith("$") || name.StartsWith(".")) continue;
                    int before = found.Count;
                    Consider(sub, name, depth + 1);
                    if (found.TryGetValue(sub, out var hit) && hit.score >= 100) exact++;
                    if (depth + 1 < max && visited.Add(sub)) queue.Enqueue((sub, depth + 1, max));
                }
            }
            App.Log($"Folder search “{req.Name}”{(req.Drive != null ? $" on {req.Drive}:" : "")}: {found.Count} match(es) in {sw.ElapsedMilliseconds} ms");

            // If there are exact name matches, drop partial ones ("repos" shouldn't list "Pre_Post_…").
            bool anyExact = found.Values.Any(v => v.score >= 100);
            return found.Where(kv => !anyExact || kv.Value.score >= 100)
                        .OrderByDescending(kv => kv.Value.score).ThenBy(kv => kv.Value.depth).ThenBy(kv => kv.Key.Length)
                        .Select(kv => kv.Key).Take(6).ToList();
        }
    }
}
