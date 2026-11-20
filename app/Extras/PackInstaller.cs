using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;

namespace Assistail.Extras
{
    /// <summary>
    /// B5: installs a pack from a file (a pack zip or an extra.json), e.g. dropped on the buddy or picked from the
    /// Extras menu. Only extra.json is ever written: a pack can't bring files of its own.
    /// </summary>
    public static class PackInstaller
    {
        private const long MaxZipBytes = 1024 * 1024;

        /// <summary>True if the file looks like a pack (an extra.json, or a zip with one inside).</summary>
        public static bool LooksLikePack(string path)
        {
            try
            {
                if (Path.GetFileName(path).Equals("extra.json", StringComparison.OrdinalIgnoreCase)) return true;
                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > MaxZipBytes) return false;
                using var zip = ZipFile.OpenRead(path);
                return FindManifest(zip) != null;
            }
            catch { return false; }
        }

        private static ZipArchiveEntry? FindManifest(ZipArchive zip) =>
            zip.Entries.FirstOrDefault(e => e.FullName.Equals("extra.json", StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries.FirstOrDefault(e => e.Name.Equals("extra.json", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/') == 1);

        private static string ReadManifest(string path)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > MaxZipBytes) throw new PackException("The pack file is bigger than 1 MB.");
                using var zip = ZipFile.OpenRead(path);
                var entry = FindManifest(zip) ?? throw new PackException("There's no extra.json in this zip.");
                if (entry.Length > Pack.MaxFileBytes) throw new PackException("extra.json is too big.");
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            }
            if (new FileInfo(path).Length > Pack.MaxFileBytes) throw new PackException("extra.json is too big.");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        /// <summary>
        /// Checks and writes a pack's extra.json to the extras folder (only that file). Returns null when installed,
        /// else the reason. Used by the store (C9) and by installing from a file.
        /// </summary>
        public static string? InstallJson(string json, out Pack pack)
        {
            pack = new Pack();
            try { Pack.Parse(json, pack); }
            catch (Exception ex) when (ex is PackException or System.Text.Json.JsonException) { return ex.Message; }
            try
            {
                string dir = Path.Combine(ExtrasEngine.Folder, pack.Id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "extra.json"), json, new UTF8Encoding(false));
                App.Log($"Extras: installed {pack.Id} {pack.Version}");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Asks, then installs. Returns the pack id, or null if cancelled or it failed (the user was told).</summary>
        /// <param name="isVerified">Whether the text is exactly a pack from the signed catalog (then no "unverified" warning).</param>
        public static string? InstallWithPrompt(string path, Window? owner, Func<string, Pack?> installed, Func<string, bool>? isVerified = null)
        {
            string json;
            var pack = new Pack();
            try
            {
                json = ReadManifest(path);
                Pack.Parse(json, pack);
            }
            catch (Exception ex) when (ex is PackException or System.Text.Json.JsonException or IOException or InvalidDataException)
            {
                Show(owner, "This isn't a pack Assistail can install:\n\n" + ex.Message, MessageBoxImage.Warning);
                return null;
            }

            var existing = installed(pack.Id);
            string what = existing != null
                ? $"Replace {existing.Title} {existing.Version} with version {pack.Version}?"
                : "Install this extras pack?";
            string text = $"{what}\n\n{pack.Title} {pack.Version}" + (pack.Author.Length > 0 ? $"  by {pack.Author}" : "") +
                          (pack.Description.Length > 0 ? "\n" + pack.Description : "") +
                          ((isVerified?.Invoke(json) ?? false)
                              ? "\n\n✓ This is the same pack as in the official extras catalog."
                              : "\n\n⚠ This pack isn't from the official extras catalog. Packs only contain reminders and lines for " +
                                "your buddy and can't run programs, but only install packs from people you trust.");
            var answer = owner != null
                ? MessageBox.Show(owner, text, "Assistail extras", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(text, "Assistail extras", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No, MessageBoxOptions.DefaultDesktopOnly);
            if (answer != MessageBoxResult.Yes) return null;

            string? error = InstallJson(json, out _);
            if (error != null) { Show(owner, "The pack couldn't be installed:\n" + error, MessageBoxImage.Warning); return null; }
            return pack.Id;
        }

        /// <summary>Deletes an installed pack's folder.</summary>
        public static string? Remove(Pack pack)
        {
            try
            {
                string full = Path.GetFullPath(pack.Folder);
                if (!full.StartsWith(Path.GetFullPath(ExtrasEngine.Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "That pack isn't in the extras folder.";
                Directory.Delete(full, recursive: true);
                App.Log($"Extras: removed {pack.Id}");
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static void Show(Window? owner, string text, MessageBoxImage icon)
        {
            if (owner != null) MessageBox.Show(owner, text, "Assistail extras", MessageBoxButton.OK, icon);
            else MessageBox.Show(text, "Assistail extras", MessageBoxButton.OK, icon, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
        }
    }
}
