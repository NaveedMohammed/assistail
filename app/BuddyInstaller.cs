using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assistail
{
    /// <summary>
    /// B1: installs a buddy pack zip (already checked against the signed catalog's SHA-256 by the store). Every entry
    /// is checked before anything is written: only buddy.json, paw.json and &lt;animation&gt;/f_####.png, no paths that
    /// climb out, PNG headers and sizes within limits, every frame the manifests promise present. It is unpacked into a
    /// staging folder, loaded with the same validator the app uses at start-up, and only then swapped into place.
    /// </summary>
    public static class BuddyInstaller
    {
        public const long MaxZipBytes = 64L * 1024 * 1024;
        private const long MaxUnpackedBytes = 160L * 1024 * 1024;
        private const int MaxEntries = 4000;
        private static readonly Regex FramePath = new(@"^([a-z0-9_]{1,40})/f_(\d{4})\.png$", RegexOptions.Compiled);

        /// <summary>Installs (or replaces) a buddy. Returns null when it worked, else the reason.</summary>
        public static string? Install(byte[] zipBytes, string expectedId)
        {
            if (!Regex.IsMatch(expectedId, "^[a-z0-9][a-z0-9-]{1,39}$") || expectedId == "lion") return "Bad buddy id.";
            if (zipBytes.Length > MaxZipBytes) return "The buddy pack is too big.";
            string root = Buddy.PacksFolder;
            string staging = Path.Combine(root, ".staging", expectedId);
            try
            {
                Directory.CreateDirectory(root);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);

                using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
                {
                    string? err = CheckEntries(zip);
                    if (err != null) return err;
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;                     // folder entries
                        string target = Path.GetFullPath(Path.Combine(staging, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            return "The buddy pack has a bad file path.";
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var src = e.Open();
                        using var buf = new MemoryStream();
                        CopyLimited(src, buf, e.Name.EndsWith(".png") ? Buddy.MaxImageBytes : Buddy.MaxManifestBytes);
                        if (e.Name.EndsWith(".png")) { buf.Position = 0; Buddy.CheckPng(buf); }
                        File.WriteAllBytes(target, buf.ToArray());
                    }
                }

                // The same checks as at start-up, plus: every frame of every animation (and of the paw) is there.
                var b = Buddy.Load(staging);
                if (!b.IsUsable) return b.Error;
                string? missing = CheckAllFrames(staging);
                if (missing != null) return missing;

                string dest = Path.Combine(root, expectedId);
                string old = Path.Combine(root, ".staging", expectedId + ".old");
                if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
                if (Directory.Exists(dest)) Directory.Move(dest, old);
                Directory.Move(staging, dest);
                if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
                App.Log($"Buddy installed: {expectedId} {b.Version}");
                return null;
            }
            catch (InvalidDataException ex) { return "The buddy pack isn't valid: " + ex.Message; }
            catch (Exception ex) { App.Log("Buddy install failed: " + ex); return "The buddy couldn't be installed: " + ex.Message; }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* next install cleans up */ }
            }
        }

        /// <summary>Names, count and declared sizes, before anything is unpacked.</summary>
        private static string? CheckEntries(ZipArchive zip)
        {
            if (zip.Entries.Count > MaxEntries) return "The buddy pack has too many files.";
            long total = 0;
            bool manifest = false;
            foreach (var e in zip.Entries)
            {
                string n = e.FullName;
                if (n.EndsWith("/")) { if (!Regex.IsMatch(n, "^[a-z0-9_]{1,40}/$")) return $"Unexpected folder \"{n}\" in the buddy pack."; continue; }
                if (n == "buddy.json") manifest = true;
                else if (n != "paw.json" && !FramePath.IsMatch(n)) return $"Unexpected file \"{n}\" in the buddy pack.";
                long limit = n.EndsWith(".png") ? Buddy.MaxImageBytes : Buddy.MaxManifestBytes;
                if (e.Length > limit) return $"\"{n}\" is too big.";
                total += e.Length;
                if (total > MaxUnpackedBytes) return "The buddy pack is too big unpacked.";
            }
            return manifest ? null : "buddy.json is missing.";
        }

        private static void CopyLimited(Stream src, Stream dst, long limit)
        {
            var buf = new byte[81920];
            long n = 0;
            int r;
            while ((r = src.Read(buf, 0, buf.Length)) > 0)
            {
                n += r;
                if (n > limit) throw new InvalidDataException("a file is bigger than it says");
                dst.Write(buf, 0, r);
            }
        }

        /// <summary>Every frame buddy.json and paw.json promise must exist (start-up only checks the first and last).</summary>
        private static string? CheckAllFrames(string folder)
        {
            var need = new List<(string Folder, int Frames)>();
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "buddy.json"))))
                foreach (var p in doc.RootElement.GetProperty("anims").EnumerateObject())
                    need.Add((p.Name, p.Value.GetProperty("frames").GetInt32()));
            string paw = Path.Combine(folder, "paw.json");
            if (File.Exists(paw))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(paw));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    string f = p.Value.TryGetProperty("folder", out var fo) ? fo.GetString() ?? "" : "";
                    int n = p.Value.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Number ? fr.GetInt32() : 0;
                    if (!Buddy.SafeName(f) || n < 1 || n > Buddy.MaxFramesPerAnim) return "paw.json isn't valid.";
                    need.Add((f, n));
                }
            }
            foreach (var (f, n) in need)
                for (int i = 1; i <= n; i++)
                    if (!File.Exists(Path.Combine(folder, f, $"f_{i:D4}.png"))) return $"Frame {i} of \"{f}\" is missing.";
            return null;
        }

        /// <summary>Removes an installed buddy pack (never the lion). Returns null when it worked, else the reason.</summary>
        public static string? Remove(string id)
        {
            try
            {
                if (id == "lion" || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,39}$")) return "That buddy can't be removed.";
                string dir = Path.Combine(Buddy.PacksFolder, id);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                App.Log($"Buddy removed: {id}");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Validates a buddy pack zip without installing it (for the catalog tool). Returns null if fine.</summary>
        public static string? Validate(byte[] zipBytes, out string id, out string version, out string name, out string description, out string minApp)
        {
            id = version = name = description = minApp = "";
            try
            {
                using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                string? err = CheckEntries(zip);
                if (err != null) return err;
                var entry = zip.GetEntry("buddy.json")!;
                using var r = new StreamReader(entry.Open());
                using var doc = JsonDocument.Parse(r.ReadToEnd());
                string S(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                id = S("id"); version = S("version"); name = S("name"); description = S("description"); minApp = S("minApp");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
