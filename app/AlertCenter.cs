using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace Assistail
{
    /// <summary>
    /// A1: turns Claude Code hook events (from <see cref="ClaudeHooks"/>) into buddy alerts. Several sessions can
    /// alert at once: the newest shows first and the rest follow as each bubble goes away. An alert is dropped when
    /// you click it (its terminal comes to the front), when you switch to that terminal yourself, or when it times out.
    /// Nothing shows while you're already looking at that terminal.
    /// </summary>
    public sealed class AlertCenter : IDisposable
    {
        private sealed record Alert(string Session, string Kind, string Text, IntPtr Hwnd, DateTime Time);

        public const string Tag = "alert";
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

        private readonly Settings _settings;
        private readonly LionWindow _lion;
        private readonly TrayIcon _tray;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Alert> _queue = new();   // newest first
        private Alert? _shown;
        private bool _replacing;

        public AlertCenter(Settings settings, LionWindow lion, TrayIcon tray, ExplorerTracker tracker)
        {
            _settings = settings;
            _lion = lion;
            _tray = tray;
            _lion.BubbleHidden += OnBubbleHidden;
            tracker.ForegroundChanged += OnForegroundChanged;
            _ = ClaudeHooks.ListenAsync(OnMessage, _cts.Token);
        }

        // ------------------------------------------------------------------ incoming (worker thread)

        private void OnMessage(JsonNode msg)
        {
            string kind = msg["kind"]?.GetValue<string>() ?? "";
            IntPtr hwnd = new IntPtr(msg["hwnd"]?.GetValue<long>() ?? 0);
            var p = msg["payload"];
            string session = Str(p, "session_id") ?? Guid.NewGuid().ToString();
            string? cwd = Str(p, "cwd");
            string folder = cwd != null ? TrayIcon.DisplayName(cwd.TrimEnd('\\', '/')) : "your project";
            App.Log($"Claude Code hook: {kind} in {cwd ?? "?"} (window {hwnd})");

            string text;
            if (kind == "stop")
            {
                string? summary = Summarize(Str(p, "last_assistant_message") ?? LastAssistantText(Str(p, "transcript_path")));
                text = $"Done! ✅ {folder}" + (summary != null ? "\n" + summary : "");
            }
            else if (kind == "notification")
            {
                string type = Str(p, "notification_type") ?? "";
                string message = Str(p, "message") ?? "";
                // "Waiting for your input" comes a minute after a finished reply: the "Done!" alert already covered it.
                if (type == "idle_prompt" || message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase)) return;
                if (type == "auth_success") return;
                text = $"🔔 Claude needs you in {folder}" + (message.Length > 0 ? "\n" + Shorten(message, 110) : "");
            }
            else return;

            var alert = new Alert(session, kind, text, hwnd, DateTime.Now);
            _dispatcher.BeginInvoke(() => Add(alert));
        }

        private static string? Str(JsonNode? n, string key)
        {
            try { return n?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null; } catch { return null; }
        }

        // ------------------------------------------------------------------ queue (UI thread)

        private void Add(Alert a)
        {
            if (!_settings.ClaudeAlerts) return;
            if (a.Hwnd != IntPtr.Zero && RootOf(Native.GetForegroundWindow()) == a.Hwnd)
            {
                App.Log("Alert skipped: that terminal is already in front.");
                return;
            }
            _queue.RemoveAll(x => x.Session == a.Session);
            _queue.Insert(0, a);
            ShowNewest();
        }

        private void ShowNewest()
        {
            _queue.RemoveAll(x => DateTime.Now - x.Time > MaxAge);
            if (_queue.Count == 0) return;
            var a = _queue[0];
            bool attention = a.Kind != "stop";
            _replacing = true;
            bool shown;
            try { shown = _lion.Alert(attention ? "jump" : "happy", a.Text, attention ? 45 : 15, Tag); }
            finally { _replacing = false; }
            if (shown) { _shown = a; return; }

            // He's away or hidden behind a full-screen app: use a tray notification instead.
            _queue.Remove(a);
            int nl = a.Text.IndexOf('\n');
            _tray.Notify(nl > 0 ? a.Text[..nl] : a.Text, nl > 0 ? a.Text[(nl + 1)..] : "Click to go there.", () => Focus(a));
            if (_queue.Count > 0) _dispatcher.BeginInvoke(ShowNewest);
        }

        private void OnBubbleHidden(string? tag)
        {
            if (_replacing || tag != Tag) return;
            if (_shown != null) { _queue.Remove(_shown); _shown = null; }
            if (_queue.Count > 0) _dispatcher.BeginInvoke(DispatcherPriority.Background, ShowNewest);
        }

        /// <summary>A click on the buddy or his bubble while an alert shows: go to that terminal. True if handled.</summary>
        public bool HandleClick()
        {
            if (_lion.BubbleTag != Tag || _shown == null) return false;
            var a = _shown;
            Focus(a);
            _lion.DismissBubble();
            return true;
        }

        private void OnForegroundChanged(IntPtr hwnd)
        {
            if (_queue.Count == 0) return;
            IntPtr root = RootOf(hwnd);
            if (root == IntPtr.Zero) return;
            bool shownGone = _shown != null && _shown.Hwnd == root;
            _queue.RemoveAll(x => x.Hwnd == root && x != _shown);
            if (shownGone) _lion.DismissBubble();   // OnBubbleHidden removes it and shows the next one
        }

        private static IntPtr RootOf(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hwnd, Native.GA_ROOTOWNER);

        private static void Focus(Alert a)
        {
            IntPtr h = a.Hwnd;
            if (h == IntPtr.Zero || !Native.IsWindow(h)) { App.Log("Alert: terminal window unknown or closed."); return; }
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            if (!Native.SetForegroundWindow(h)) App.Log("Alert: Windows didn't let the terminal come to the front.");
        }

        // ------------------------------------------------------------------ summary of the last reply

        /// <summary>The text of the last assistant message in a Claude Code transcript (JSONL), reading only the end.</summary>
        private static string? LastAssistantText(string? path)
        {
            try
            {
                if (path == null || !File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, fs.Length - 256 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string tail = reader.ReadToEnd();
                var lines = tail.Split('\n');
                for (int i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || !line.Contains("\"assistant\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") continue;
                        if (!root.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var content)) continue;
                        if (content.ValueKind == JsonValueKind.String) return content.GetString();
                        string? last = null;
                        foreach (var block in content.EnumerateArray())
                            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" && block.TryGetProperty("text", out var tx))
                                last = tx.GetString();
                        if (!string.IsNullOrWhiteSpace(last)) return last;
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex) { App.Log("Transcript read failed: " + ex.Message); }
            return null;
        }

        /// <summary>First sentence of a reply, without markdown, at most ~100 characters.</summary>
        private static string? Summarize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = Regex.Replace(text, @"```[\s\S]*?```", " ");
            t = Regex.Replace(t, @"[*_`#>|]+", "");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            if (t.Length == 0) return null;
            var m = Regex.Match(t, @"^.{20,}?[.!?](?=\s|$)");
            return Shorten(m.Success ? m.Value : t, 100);
        }

        private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

        public void Dispose() => _cts.Cancel();
    }
}
