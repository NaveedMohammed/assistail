using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Assistail.Extras
{
    /// <summary>
    /// C9: the extras catalog on GitHub (NaveedMohammed/assistail-extras). Downloads index.json + index.json.sig, checks
    /// the signature against the public key built in here (the private key never leaves the maintainer's PC), and then
    /// each pack zip against the SHA-256 in the signed index. The last good index is cached for offline use.
    /// Only plain file downloads: no accounts, nothing about the user is sent.
    /// </summary>
    public sealed class ExtrasStore
    {
        /// <summary>The catalog's signing key (public half), from tools/ExtrasCatalog pubkey.</summary>
        private const string PublicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEku83YIWOPBdO6UnUbjMc1B8ZhwaExg7QMY0ug/3oAE2xJY5fq8oJQsqPYnrHiRrOFrJC3ZixADCCnbN46s+2NQ==";
        private const int IndexFormat = 1;
        private const long MaxZipBytes = 1024 * 1024;
        public const string CatalogPage = "https://github.com/NaveedMohammed/assistail-extras";

        /// <summary>Where the catalog is read from: GitHub, then jsDelivr (a mirror of the same repo) if GitHub can't be reached.</summary>
        private static readonly string[] Bases =
        {
            "https://raw.githubusercontent.com/NaveedMohammed/assistail-extras/main/",
            "https://cdn.jsdelivr.net/gh/NaveedMohammed/assistail-extras@main/",
        };

        /// <summary>Testing aid: ASSISTAIL_EXTRAS_CATALOG=D:\path\to\assistail-extras reads a local copy of the catalog.</summary>
        private static readonly string? LocalCatalog = Environment.GetEnvironmentVariable("ASSISTAIL_EXTRAS_CATALOG");

        public sealed record Entry(string Id, string Name, string Version, string Author, string Description, string Icon,
                                   string MinApp, string Kind, string File, long Size, string Sha256, string JsonSha256)
        {
            public string Title => (Icon.Length > 0 ? Icon + " " : "") + Name;
            public Version Ver => System.Version.TryParse(Version, out var v) ? v : new Version(0, 0);
            public bool NeedsNewerApp => System.Version.TryParse(MinApp, out var m) && m > UpdateService.Current;
        }

        private static string CacheFile => Path.Combine(App.DataDir, "extras-index.json");
        private static string CacheSig => CacheFile + ".sig";

        private static readonly Lazy<HttpClient> Http = new(() =>
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Assistail-extras");   // nothing personal
            return c;
        });

        /// <summary>B1: a buddy pack in the catalog. The zip is a GitHub Release asset (Url), checked against Size/Sha256.</summary>
        public sealed record BuddyEntry(string Id, string Name, string Version, string Author, string Description, string Icon,
                                        string MinApp, string Url, long Size, string Sha256)
        {
            public string Title => (Icon.Length > 0 ? Icon + " " : "") + Name;
            public Version Ver => System.Version.TryParse(Version, out var v) ? v : new Version(0, 0);
            public bool NeedsNewerApp => System.Version.TryParse(MinApp, out var m) && m > UpdateService.Current;
        }

        public IReadOnlyList<Entry> Entries { get; private set; } = Array.Empty<Entry>();
        public IReadOnlyList<BuddyEntry> Buddies { get; private set; } = Array.Empty<BuddyEntry>();
        /// <summary>When the entries were downloaded (from GitHub or the cache's time), or null if none yet.</summary>
        public DateTime? Fetched { get; private set; }
        public bool FromCache { get; private set; }
        private string _base = LocalCatalog ?? Bases[0];
        private static IEnumerable<string> Sources => LocalCatalog != null ? new[] { LocalCatalog } : Bases;

        public ExtrasStore()
        {
            // The last good index, so the store works offline (re-verified: the cache file isn't trusted either).
            try
            {
                if (File.Exists(CacheFile) && File.Exists(CacheSig))
                {
                    byte[] bytes = File.ReadAllBytes(CacheFile);
                    if (Verify(bytes, File.ReadAllText(CacheSig)) && Parse(bytes) is { } list)
                    {
                        Entries = list;
                        Buddies = ParseBuddies(bytes);
                        Fetched = File.GetLastWriteTime(CacheFile);
                        FromCache = true;
                    }
                }
            }
            catch (Exception ex) { App.Log("Extras store: cache unreadable: " + ex.Message); }
        }

        public Entry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);

        /// <summary>Downloads and checks the index. Returns null when it worked, else a short reason (the old list stays).</summary>
        public async Task<string?> RefreshAsync()
        {
            string? lastError = null;
            foreach (var b in Sources)
            {
                try
                {
                    byte[] index = await GetBytes(b, "index.json");
                    string sig = Encoding.ASCII.GetString(await GetBytes(b, "index.json.sig"));
                    if (!Verify(index, sig)) { lastError = "The catalog's signature didn't match, so it was ignored."; App.Log($"Extras store: bad signature from {b}"); continue; }
                    var list = Parse(index);
                    if (list == null) { lastError = "The catalog needs a newer Assistail."; continue; }
                    Entries = list;
                    Buddies = ParseBuddies(index);
                    Fetched = DateTime.Now;
                    FromCache = false;
                    _base = b;
                    try { File.WriteAllBytes(CacheFile, index); File.WriteAllText(CacheSig, sig); } catch { /* cache is optional */ }
                    App.Log($"Extras store: {list.Count} packs from {b}");
                    return null;
                }
                catch (Exception ex)
                {
                    lastError = "Couldn't reach the extras catalog. Check your connection and try again.";
                    App.Log($"Extras store: {b}: {ex.Message}");
                }
            }
            return lastError;
        }

        /// <summary>Downloads a pack and checks it against the signed index. Returns (extra.json text, null) or (null, reason).</summary>
        public async Task<(string? Json, string? Error)> DownloadAsync(Entry e)
        {
            try
            {
                if (e.Size > MaxZipBytes) return (null, "That pack is too big.");
                byte[] zip = await GetFromAnySource(e.File);
                if (zip.Length != e.Size || Hex(SHA256.HashData(zip)) != e.Sha256)
                    return (null, "The download didn't match the catalog, so it wasn't installed. Try again later.");
                using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                var entry = archive.GetEntry("extra.json") ?? throw new InvalidDataException("no extra.json in the zip");
                using var r = new StreamReader(entry.Open(), Encoding.UTF8);
                string json = r.ReadToEnd();
                if (JsonSha(json) != e.JsonSha256) return (null, "The pack didn't match the catalog, so it wasn't installed.");
                return (json, null);
            }
            catch (Exception ex)
            {
                App.Log($"Extras store: download {e.Id} failed: {ex.Message}");
                return (null, "The pack couldn't be downloaded. Check your connection and try again.");
            }
        }

        /// <summary>
        /// B1: downloads a buddy pack zip and checks it against the signed index (size and SHA-256). Returns (zip, null)
        /// or (null, reason). <paramref name="progress"/> gets 0..1.
        /// </summary>
        public async Task<(byte[]? Zip, string? Error)> DownloadBuddyAsync(BuddyEntry e, IProgress<double>? progress = null)
        {
            try
            {
                if (e.Size <= 0 || e.Size > BuddyInstaller.MaxZipBytes) return (null, "That buddy is too big.");
                byte[] zip;
                if (LocalCatalog != null)   // testing aid: the release asset is read from <catalog>/release-assets/<file name>
                    zip = await File.ReadAllBytesAsync(Path.Combine(LocalCatalog, "release-assets", Path.GetFileName(new Uri(e.Url).LocalPath)));
                else
                {
                    if (!e.Url.StartsWith("https://github.com/NaveedMohammed/assistail-extras/releases/download/", StringComparison.Ordinal))
                        return (null, "That buddy isn't hosted where the catalog keeps buddies, so it wasn't downloaded.");
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Assistail-extras");   // nothing personal
                    using var resp = await http.GetAsync(e.Url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    if (resp.Content.Headers.ContentLength is long len && len != e.Size) return (null, "The download didn't match the catalog, so it wasn't installed.");
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var ms = new MemoryStream();
                    var buf = new byte[81920];
                    int r;
                    while ((r = await stream.ReadAsync(buf)) > 0)
                    {
                        ms.Write(buf, 0, r);
                        if (ms.Length > e.Size) return (null, "The download didn't match the catalog, so it wasn't installed.");
                        progress?.Report((double)ms.Length / e.Size);
                    }
                    zip = ms.ToArray();
                }
                if (zip.Length != e.Size || Hex(SHA256.HashData(zip)) != e.Sha256)
                    return (null, "The download didn't match the catalog, so it wasn't installed. Try again later.");
                return (zip, null);
            }
            catch (Exception ex)
            {
                App.Log($"Extras store: buddy download {e.Id} failed: {ex.Message}");
                return (null, "The buddy couldn't be downloaded. Check your connection and try again.");
            }
        }

        /// <summary>B1: installed buddy packs with a newer version in the catalog (that this app can run).</summary>
        public List<(Buddy Installed, BuddyEntry Newer)> BuddyUpdates() =>
            Buddy.Installed().Where(b => b.IsUsable && System.Version.TryParse(b.Version, out _))
                 .Select(b => (b, Buddies.FirstOrDefault(x => x.Id == b.Id)))
                 .Where(x => x.Item2 is { NeedsNewerApp: false } e && e.Ver > System.Version.Parse(x.b.Version))
                 .Select(x => (x.b, x.Item2!)).ToList();

        /// <summary>Whether this extra.json is exactly a pack in the signed catalog (for files dropped on the buddy).</summary>
        public bool IsVerified(string json) => Entries.Any(e => e.JsonSha256 == JsonSha(json));

        /// <summary>Installed packs that have a newer version in the catalog (that this app can run).</summary>
        public List<(Pack Installed, Entry Newer)> Updates(IEnumerable<Pack> installed) =>
            installed.Where(p => p.IsUsable && System.Version.TryParse(p.Version, out _))
                     .Select(p => (p, Find(p.Id)))
                     .Where(x => x.Item2 is { NeedsNewerApp: false } e && e.Ver > System.Version.Parse(x.p.Version))
                     .Select(x => (x.p, x.Item2!)).ToList();

        // ------------------------------------------------------------------ helpers

        /// <summary>The source the index came from first, then the others (the zip is checked against the signed index anyway).</summary>
        private async Task<byte[]> GetFromAnySource(string rel)
        {
            Exception? last = null;
            foreach (var b in new[] { _base }.Concat(Sources.Where(x => x != _base)))
            {
                try { return await GetBytes(b, rel); }
                catch (Exception ex) { last = ex; App.Log($"Extras store: {b}{rel}: {ex.Message}"); }
            }
            throw last ?? new IOException("no source");
        }

        private static async Task<byte[]> GetBytes(string b, string rel)
        {
            if (!b.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return await File.ReadAllBytesAsync(Path.Combine(b, rel.Replace('/', Path.DirectorySeparatorChar)));
            using var resp = await Http.Value.GetAsync(b + rel, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > MaxZipBytes) throw new InvalidDataException("too big");
            return await resp.Content.ReadAsByteArrayAsync();
        }

        private static bool Verify(byte[] index, string sigBase64)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
                return key.VerifyData(index, Convert.FromBase64String(sigBase64.Trim()), HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }

        private static List<Entry>? Parse(byte[] index)
        {
            using var doc = JsonDocument.Parse(index);
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var f) || f.GetInt32() > IndexFormat) return null;
            var list = new List<Entry>();
            foreach (var e in root.GetProperty("packs").EnumerateArray())
            {
                string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                list.Add(new Entry(S("id"), S("name"), S("version"), S("author"), S("description"), S("icon"), S("minApp"), S("kind"),
                                   S("file"), e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0, S("sha256"), S("json_sha256")));
            }
            return list;
        }

        private static List<BuddyEntry> ParseBuddies(byte[] index)
        {
            var list = new List<BuddyEntry>();
            try
            {
                using var doc = JsonDocument.Parse(index);
                if (!doc.RootElement.TryGetProperty("buddies", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
                foreach (var e in arr.EnumerateArray())
                {
                    string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var b = new BuddyEntry(S("id"), S("name"), S("version"), S("author"), S("description"), S("icon"), S("minApp"),
                                           S("url"), e.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0, S("sha256"));
                    if (System.Text.RegularExpressions.Regex.IsMatch(b.Id, "^[a-z0-9][a-z0-9-]{1,39}$") && b.Id != "lion" && b.Sha256.Length == 64) list.Add(b);
                }
            }
            catch (Exception ex) { App.Log("Extras store: buddies unreadable: " + ex.Message); }
            return list;
        }

        /// <summary>SHA-256 of extra.json text with LF line endings (as the catalog stores it).</summary>
        private static string JsonSha(string json) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(json.Replace("\r\n", "\n"))));

        private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    }
}
