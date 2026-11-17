using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Assistail
{
    /// <summary>
    /// A file, clipboard text or picture attached to a chat message (A2 drop on the buddy, A3 clipboard / screen snip).
    /// Small text goes into the prompt as it is. Pictures, PDFs and bigger text files are copied into the conversation's
    /// own attachments folder, which the chat may read with the read-only Read tool (nothing else on disk).
    /// </summary>
    public sealed class Attachment
    {
        public string Name { get; init; } = "";
        /// <summary>Text included in the prompt (small text files and clipboard text).</summary>
        public string? InlineText { get; init; }
        /// <summary>Copy inside the attachments folder, read by Claude with the Read tool.</summary>
        public string? CopyPath { get; init; }
        /// <summary>Shown on the chip, e.g. "large: read in parts".</summary>
        public string? Note { get; init; }
        public string Icon { get; init; } = "📄";
    }

    public static class Attachments
    {
        public const int InlineLimit = 100 * 1024;           // bytes of text put straight into the prompt
        public const long MaxFileBytes = 20L * 1024 * 1024;  // bigger files are refused
        private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        public static string Root => Path.Combine(App.DataDir, "attachments");

        /// <summary>A fresh folder for one conversation's attachments.</summary>
        public static string NewConversationDir() => Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);

        /// <summary>Deletes attachment folders older than a day (they are only useful while the chat lasts).</summary>
        public static void CleanupOld()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var d in Directory.GetDirectories(Root))
                    if (Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-1)) Directory.Delete(d, recursive: true);
            }
            catch (Exception ex) { App.Log("Attachment cleanup failed: " + ex.Message); }
        }

        /// <summary>Deletes one conversation's attachment copies (New chat, quit). Never throws.</summary>
        public static void DeleteDir(string? dir)
        {
            try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { App.Log("Attachment delete failed: " + ex.Message); }
        }

        /// <summary>Makes an attachment from a file on disk. Returns (attachment, null) or (null, friendly error).</summary>
        public static (Attachment?, string?) FromFile(string path, string dir)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return (null, $"I can't find {path}.");
                string name = fi.Name;
                if (fi.Length > MaxFileBytes)
                    return (null, $"{name} is too big ({Size(fi.Length)}). I can read files up to {Size(MaxFileBytes)}.");
                string ext = fi.Extension.ToLowerInvariant();

                if (ImageExt.Contains(ext))
                    return (new Attachment { Name = name, CopyPath = CopyInto(path, dir), Icon = "🖼️" }, null);
                if (ext == ".pdf")
                    return (new Attachment { Name = name, CopyPath = CopyInto(path, dir), Icon = "📕" }, null);
                if (!LooksLikeText(path))
                    return (null, $"I can't read {name}: I understand text files, pictures (PNG, JPG, GIF, WebP) and PDFs.");
                if (fi.Length <= InlineLimit)
                    return (new Attachment { Name = name, InlineText = File.ReadAllText(path) }, null);
                return (new Attachment { Name = name, CopyPath = CopyInto(path, dir), Note = $"{Size(fi.Length)}, read in parts" }, null);
            }
            catch (Exception ex)
            {
                App.Log("Attach failed: " + ex.Message);
                return (null, $"I couldn't open {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        public static Attachment FromText(string text, string dir)
        {
            if (Encoding.UTF8.GetByteCount(text) <= InlineLimit)
                return new Attachment { Name = "Clipboard text", InlineText = text, Icon = "📋" };
            Directory.CreateDirectory(dir);
            string path = Unique(Path.Combine(dir, "clipboard.txt"));
            File.WriteAllText(path, text);
            return new Attachment { Name = "Clipboard text", CopyPath = path, Icon = "📋", Note = $"{Size(new FileInfo(path).Length)}, read in parts" };
        }

        public static Attachment FromImage(BitmapSource image, string dir, string baseName)
        {
            Directory.CreateDirectory(dir);
            string path = Unique(Path.Combine(dir, baseName + ".png"));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using (var fs = File.Create(path)) enc.Save(fs);
            return new Attachment { Name = Path.GetFileName(path), CopyPath = path, Icon = "🖼️" };
        }

        /// <summary>
        /// What's on the clipboard right now: files, a picture or text. Each file that can't be attached adds an error.
        /// </summary>
        public static (List<Attachment> items, List<string> errors) FromClipboard(string dir)
        {
            var items = new List<Attachment>();
            var errors = new List<string>();
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string? f in Clipboard.GetFileDropList())
                    {
                        if (f == null || Directory.Exists(f)) continue;
                        var (a, err) = FromFile(f, dir);
                        if (a != null) items.Add(a); else if (err != null) errors.Add(err);
                    }
                }
                else if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource img)
                    items.Add(FromImage(img, dir, "clipboard"));
                else if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text && text.Trim().Length > 0)
                    items.Add(FromText(text, dir));
            }
            catch (Exception ex) { App.Log("Clipboard read failed: " + ex.Message); errors.Add("I couldn't read the clipboard (another app may be using it). Try again."); }
            return (items, errors);
        }

        /// <summary>
        /// Opens the Windows snipping overlay (ms-screenclip:) and waits until the snip lands on the clipboard.
        /// Returns null if the user cancels (nothing new within 2 minutes) or another snip starts.
        /// </summary>
        public static async Task<BitmapSource?> SnipAsync(CancellationToken ct)
        {
            uint before = Native.GetClipboardSequenceNumber();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
            catch (Exception ex) { App.Log("Snip launch failed: " + ex.Message); return null; }
            var until = DateTime.Now.AddMinutes(2);
            while (DateTime.Now < until && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return null; }
                if (Native.GetClipboardSequenceNumber() == before) continue;
                try { if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource img) return img; }
                catch { /* clipboard busy: try again */ }
            }
            return null;
        }

        /// <summary>The prompt text that introduces the attachments, followed by the user's question.</summary>
        public static string BuildPrompt(IReadOnlyList<Attachment> items, string question)
        {
            if (items.Count == 0) return question;
            var sb = new StringBuilder("I'm attaching:\n");
            foreach (var a in items)
            {
                if (a.InlineText != null)
                    sb.Append($"<attachment name=\"{a.Name}\">\n{a.InlineText.TrimEnd()}\n</attachment>\n");
                else
                    sb.Append($"- {a.Name}: {a.CopyPath} (read it with the Read tool)\n");
            }
            sb.Append('\n').Append(question);
            return sb.ToString();
        }

        private static bool LooksLikeText(string path)
        {
            var buf = new byte[8192];
            int n;
            using (var fs = File.OpenRead(path)) n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;   // UTF-16 BOM
            for (int i = 0; i < n; i++) if (buf[i] == 0) return false;
            return true;
        }

        private static string CopyInto(string path, string dir)
        {
            Directory.CreateDirectory(dir);
            string target = Unique(Path.Combine(dir, Path.GetFileName(path)));
            File.Copy(path, target);
            return target;
        }

        private static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++) { string p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!File.Exists(p)) return p; }
        }

        public static string Size(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}
