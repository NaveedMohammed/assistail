using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assistail
{
    /// <summary>
    /// C1: checks GitHub Releases for a newer version and installs it. The only network call Assistail makes itself;
    /// it can be turned off (Settings, or the tray menu). The download is checked against the SHA-256 digest GitHub
    /// publishes for every release asset (or a "&lt;zip&gt;.sha256" asset) before anything runs.
    /// </summary>
    public static class UpdateService
    {
        private const string LatestUrl = "https://api.github.com/repos/NaveedMohammed/assistail/releases/latest";
        public const string ReleasesPage = "https://github.com/NaveedMohammed/assistail/releases";

        public sealed record Release(Version Version, string Tag, string ZipUrl, string ZipName, long Size, string? Sha256, string? Sha256Url, string PageUrl);

        // Testing aid: ASSISTAIL_VERSION=2.0.0 pretends to be an older version (to test the update flow).
        public static Version Current { get; } = ParseVersion(
            Environment.GetEnvironmentVariable("ASSISTAIL_VERSION") ??
            FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? "0.0.0") ?? new Version(0, 0, 0);

        /// <summary>Testing aid: download and verify, but don't run the installer.</summary>
        public static bool DryRun { get; set; }

        private static readonly Lazy<HttpClient> Http = new(() =>
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Assistail-updater");   // GitHub requires a User-Agent; nothing personal
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        });

        public static Version? ParseVersion(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '+', '-', ' ' });
            if (cut >= 0) s = s[..cut];
            return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
        }

        /// <summary>The latest release if it's newer than this copy; null if up to date. Throws on network/parse errors.</summary>
        public static async Task<Release?> CheckAsync(CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            using var resp = await Http.Value.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no releases yet
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag);
            if (version == null || version <= Current) return null;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
            JsonElement? zip = null; string? shaUrl = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("Assistail-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip = a;
            }
            if (zip is not JsonElement z) return new Release(version, tag, "", "", 0, null, null, page);   // no zip: offer the page
            string zipName = z.GetProperty("name").GetString()!;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), zipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                    shaUrl = a.GetProperty("browser_download_url").GetString();
            string? digest = z.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
            string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToUpperInvariant() : null;
            return new Release(version, tag, z.GetProperty("browser_download_url").GetString()!, zipName,
                               z.GetProperty("size").GetInt64(), sha, shaUrl, page);
        }

        /// <summary>
        /// Downloads and verifies the release, then starts its installer quietly. The installer stops this copy,
        /// replaces it and starts the new one (settings are kept). Returns an error message, or null when the
        /// installer has started and the app should quit.
        /// </summary>
        public static async Task<string?> DownloadAndInstallAsync(Release r, IProgress<string>? progress, CancellationToken ct = default)
        {
            if (r.ZipUrl.Length == 0) return "This release has no download attached yet.";
            string work = Path.Combine(Path.GetTempPath(), "Assistail-update");
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(work);
                string zipPath = Path.Combine(work, r.ZipName);

                progress?.Report($"Downloading {r.ZipName} ({Attachments.Size(r.Size)})…");
                using (var resp = await Http.Value.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(zipPath);
                    await resp.Content.CopyToAsync(fs, ct);
                }

                progress?.Report("Checking the download…");
                string? expected = r.Sha256;
                if (expected == null && r.Sha256Url != null)
                {
                    string text = await Http.Value.GetStringAsync(r.Sha256Url, ct);
                    expected = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
                }
                if (expected == null) return "The download couldn't be verified (the release has no SHA-256 checksum), so it wasn't installed.";
                string actual;
                await using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return "The download is damaged (its checksum doesn't match), so it wasn't installed. Try again later.";

                progress?.Report("Unpacking…");
                string dir = Path.Combine(work, "unpacked");
                ZipFile.ExtractToDirectory(zipPath, dir);
                string? installer = Directory.GetFiles(dir, "install.ps1", SearchOption.AllDirectories).FirstOrDefault();
                string? exe = installer == null ? null : Path.Combine(Path.GetDirectoryName(installer)!, "Assistail.exe");
                if (installer == null || exe == null || !File.Exists(exe)) return "The download doesn't look like an Assistail release.";
                var newVersion = ParseVersion(FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "");
                if (newVersion != r.Version) return $"The download is version {newVersion}, not {r.Version}, so it wasn't installed.";

                if (DryRun) { App.Log($"Update {r.Version}: dry run, verified {actual} and unpacked; installer not started."); return "dry run"; }
                progress?.Report("Installing…");
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
                foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", installer, "-Quiet" })
                    psi.ArgumentList.Add(a);
                Process.Start(psi);
                App.Log($"Update {r.Version}: installer started.");
                return null;
            }
            catch (OperationCanceledException) { return "The update was cancelled."; }
            catch (Exception ex)
            {
                App.Log("Update failed: " + ex);
                return "The update failed: " + ex.Message;
            }
        }
    }
}
