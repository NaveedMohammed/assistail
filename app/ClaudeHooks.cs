using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Assistail
{
    /// <summary>
    /// A1: Claude Code activity alerts. Claude Code runs <c>Assistail.exe --notify stop|notification</c> from its
    /// Stop and Notification hooks. That short-lived process reads the hook's JSON from stdin, works out which
    /// terminal window the session runs in, hands both to the running app over a named pipe and exits at once
    /// (always with exit code 0, so a hook can never block or fail Claude Code).
    /// This class also adds and removes those hooks in the user's Claude Code settings.json.
    /// </summary>
    public static class ClaudeHooks
    {
        /// <summary>Set on every `claude` the chat starts, so the chat's own replies don't raise alerts.</summary>
        public const string ChatMarkerEnv = "ASSISTAIL_CHAT";

        public static string PipeName { get; } =
            "Assistail-" + (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

        // ================================================================== notifier (runs inside the hook)

        /// <summary>Entry point for <c>--notify &lt;kind&gt;</c>. Never throws, always returns 0.</summary>
        public static int RunNotifier(string[] args)
        {
            try
            {
                if (Environment.GetEnvironmentVariable(ChatMarkerEnv) == "1") return 0;
                string kind = args.Length > 1 ? args[1] : "stop";

                // The hook JSON arrives on stdin; don't wait long for it.
                string input = "";
                var read = Task.Run(() => { using var r = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8); return r.ReadToEnd(); });
                if (read.Wait(1500)) input = read.Result;

                JsonNode? payload = null;
                try { payload = JsonNode.Parse(input); } catch (Exception ex) { payload = new JsonObject { ["parse_error"] = ex.Message, ["raw_length"] = input.Length }; }
                long hwnd = FindTerminalWindow().ToInt64();
                var msg = new JsonObject { ["kind"] = kind, ["hwnd"] = hwnd, ["payload"] = payload };

                // CurrentUserOnly: only talk to a pipe owned by this Windows user (another user can't squat the name and read the event).
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(300);                                  // the app isn't running → give up quickly
                byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString() + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
            catch { /* never fail the hook */ }
            return 0;
        }

        /// <summary>
        /// The top-level window of the terminal this process (a hook of a Claude Code session) belongs to, or zero.
        /// Hooks usually run without a console, so walk up the parent processes (shell → claude → terminal shell) and
        /// borrow the first console found. With Windows Terminal the console window is a hidden stand-in owned by the
        /// real terminal window; with VS Code and others, fall back to the nearest ancestor that has a visible window.
        /// </summary>
        public static IntPtr FindTerminalWindow()
        {
            try
            {
                var parents = ParentMap();
                uint pid = (uint)Environment.ProcessId;
                var chain = new List<uint>();
                for (int i = 0; i < 12 && parents.TryGetValue(pid, out uint ppid) && ppid != 0 && !chain.Contains(ppid); i++)
                {
                    chain.Add(ppid);
                    pid = ppid;
                }

                Native.FreeConsole();
                foreach (uint p in chain)
                {
                    if (!Native.AttachConsole(p)) continue;
                    IntPtr console = Native.GetConsoleWindow();
                    Native.FreeConsole();
                    if (console == IntPtr.Zero) continue;
                    IntPtr root = Native.GetAncestor(console, Native.GA_ROOTOWNER);
                    if (root == IntPtr.Zero) root = console;
                    if (Native.IsWindowVisible(root) || Native.IsIconic(root)) return root;
                    break;                                          // a hidden pseudo-console: look for a window instead
                }
                foreach (uint p in chain)
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById((int)p);
                        IntPtr h = proc.MainWindowHandle;
                        if (h != IntPtr.Zero && Native.IsWindowVisible(h)) return h;
                    }
                    catch { /* exited */ }
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        private static Dictionary<uint, uint> ParentMap()
        {
            var map = new Dictionary<uint, uint>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new Native.PROCESSENTRY32 { dwSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.PROCESSENTRY32>() };
                if (Native.Process32FirstW(snap, ref e))
                    do map[e.th32ProcessID] = e.th32ParentProcessID; while (Native.Process32NextW(snap, ref e));
            }
            finally { Native.CloseHandle(snap); }
            return map;
        }

        // ================================================================== app side: receive alerts

        /// <summary>Listens on the pipe and calls <paramref name="onMessage"/> (on a worker thread) for each hook message.</summary>
        public static async Task ListenAsync(Action<JsonNode> onMessage, System.Threading.CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { App.Log("Alert pipe failed: " + ex.Message); await Task.Delay(5000, ct).ContinueWith(_ => { }); continue; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (server)
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                            if (!string.IsNullOrWhiteSpace(line) && JsonNode.Parse(line) is JsonNode node) onMessage(node);
                        }
                    }
                    catch (Exception ex) { App.Log("Alert message failed: " + ex.Message); }
                });
            }
        }

        // ================================================================== install / remove the hooks

        public enum HookState { NotInstalled, Installed, InstalledElsewhere }

        /// <summary>Claude Code's user settings file (honours CLAUDE_CONFIG_DIR).</summary>
        public static string SettingsPath
        {
            get
            {
                string? dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return Path.Combine(dir, "settings.json");
            }
        }

        // One backup + marker per settings file (CLAUDE_CONFIG_DIR can point elsewhere), so installing for one location
        // can never overwrite or delete the backup of another.
        private static string PathKey =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(SettingsPath).ToLowerInvariant())))[..12];
        private static string BackupPath => Path.Combine(App.DataDir, $"claude-settings.{PathKey}.backup.json");
        private static string MarkerPath => Path.Combine(App.DataDir, $"claude-settings.{PathKey}.hooks.json");

        /// <summary>
        /// The exe path written into the hook command. Hooks run through a shell (Git Bash on Windows, or PowerShell/cmd),
        /// so use forward slashes and the 8.3 short form when the path has spaces: then it needs no quoting in any shell.
        /// </summary>
        private static string ExeForShell
        {
            get
            {
                string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Assistail.exe");
                if (exe.Contains(' '))
                {
                    var sb = new StringBuilder(520);
                    if (Native.GetShortPathName(exe, sb, (uint)sb.Capacity) > 0 && !sb.ToString().Contains(' ')) exe = sb.ToString();
                    else return "\"" + exe.Replace('\\', '/') + "\"";
                }
                return exe.Replace('\\', '/');
            }
        }

        public static string Command(string kind) => $"{ExeForShell} --notify {kind}";

        private static bool IsOurs(JsonNode? hook) =>
            hook?["command"]?.GetValue<string>() is string cmd &&
            cmd.Contains("--notify", StringComparison.OrdinalIgnoreCase) && cmd.Contains("assistail", StringComparison.OrdinalIgnoreCase);

        private static readonly (string Event, string Kind)[] Events = { ("Stop", "stop"), ("Notification", "notification") };

        public static HookState State()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return HookState.NotInstalled;
                var root = Parse(File.ReadAllText(SettingsPath));
                bool any = false, current = true;
                foreach (var (ev, kind) in Events)
                {
                    var ours = (root?["hooks"]?[ev] as JsonArray)?.SelectMany(g => (g?["hooks"] as JsonArray) ?? new JsonArray())
                               .Where(IsOurs).ToList() ?? new List<JsonNode?>();
                    if (ours.Count == 0) { current = false; continue; }
                    any = true;
                    if (!ours.Any(h => h?["command"]?.GetValue<string>() == Command(kind))) current = false;
                }
                return !any ? HookState.NotInstalled : current ? HookState.Installed : HookState.InstalledElsewhere;
            }
            catch (Exception ex) { App.Log("Hook state check failed: " + ex.Message); return HookState.NotInstalled; }
        }

        private static JsonNode? Parse(string text) =>
            text.Trim().Length == 0 ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        private static readonly JsonSerializerOptions Indented = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>What Install adds, for the confirmation prompt.</summary>
        public static string PreviewText()
        {
            var hooks = new JsonObject();
            foreach (var (ev, kind) in Events) hooks[ev] = NewGroup(kind);
            return new JsonObject { ["hooks"] = hooks }.ToJsonString(Indented);
        }

        private static JsonArray NewGroup(string kind) => new()
        {
            new JsonObject
            {
                ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = Command(kind), ["timeout"] = 10 } },
            },
        };

        /// <summary>Adds (or refreshes) the hooks. Returns an error message, or null on success.</summary>
        public static string? Install()
        {
            try
            {
                string path = SettingsPath;
                bool existed = File.Exists(path);
                byte[] original = existed ? File.ReadAllBytes(path) : Array.Empty<byte>();
                if (Parse(Encoding.UTF8.GetString(original)) is not JsonObject root)
                    return "Claude Code's settings.json isn't a JSON object, so it was left alone.";

                RemoveOurs(root);
                var hooks = root["hooks"] as JsonObject;
                if (hooks == null) { hooks = new JsonObject(); root["hooks"] = hooks; }
                foreach (var (ev, kind) in Events)
                {
                    if (hooks[ev] is not JsonArray arr) { arr = new JsonArray(); hooks[ev] = arr; }
                    foreach (var g in NewGroup(kind).ToList()) arr.Add(g!.DeepClone());
                }

                // Keep the untouched original only on the first install, so "Remove" can put it back exactly.
                if (!File.Exists(MarkerPath))
                {
                    Directory.CreateDirectory(App.DataDir);
                    File.WriteAllBytes(BackupPath, original);
                }
                byte[] updated = new UTF8Encoding(false).GetBytes(root.ToJsonString(Indented) + "\n");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteSafely(path, updated);
                var marker = File.Exists(MarkerPath) ? JsonNode.Parse(File.ReadAllText(MarkerPath)) as JsonObject : null;
                marker ??= new JsonObject { ["existed"] = existed };
                marker["path"] = path;
                marker["sha256"] = Convert.ToHexString(SHA256.HashData(updated));
                File.WriteAllText(MarkerPath, marker.ToJsonString());
                App.Log("Claude Code hooks installed in " + path);
                return null;
            }
            catch (Exception ex) { App.Log("Hook install failed: " + ex); return ex.Message; }
        }

        /// <summary>
        /// Removes the hooks. If settings.json hasn't changed since we wrote it, the original file is restored byte for
        /// byte; otherwise only our entries are taken out. Returns an error message, or null on success.
        /// </summary>
        public static string? Remove()
        {
            try
            {
                string path = SettingsPath;
                var marker = File.Exists(MarkerPath) ? JsonNode.Parse(File.ReadAllText(MarkerPath)) : null;
                if (marker != null && File.Exists(path) && File.Exists(BackupPath) &&
                    string.Equals(marker["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase) &&
                    marker["sha256"]?.GetValue<string>() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
                {
                    if (marker["existed"]?.GetValue<bool>() == false) File.Delete(path);
                    else WriteSafely(path, File.ReadAllBytes(BackupPath));
                    App.Log("Claude Code hooks removed (original settings restored).");
                }
                else if (File.Exists(path))
                {
                    if (Parse(File.ReadAllText(path)) is JsonObject root && RemoveOurs(root))
                    {
                        WriteSafely(path, new UTF8Encoding(false).GetBytes(root.ToJsonString(Indented) + "\n"));
                        App.Log("Claude Code hooks removed (settings had changed, so only the hooks were taken out).");
                    }
                }
                try { File.Delete(MarkerPath); File.Delete(BackupPath); } catch { }
                return null;
            }
            catch (Exception ex) { App.Log("Hook removal failed: " + ex); return ex.Message; }
        }

        /// <summary>
        /// Writes a file so it's never left half-written: write a temp file next to it, then swap it in (Claude Code may be
        /// reading settings.json at any moment, and a crash mid-write must not corrupt it).
        /// </summary>
        private static void WriteSafely(string path, byte[] bytes)
        {
            string tmp = path + ".assistail-tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Takes our hook entries out, dropping groups/events/"hooks" that end up empty. True if anything changed.</summary>
        private static bool RemoveOurs(JsonObject root)
        {
            if (root["hooks"] is not JsonObject hooks) return false;
            bool changed = false;
            foreach (var ev in hooks.Select(p => p.Key).ToList())
            {
                if (hooks[ev] is not JsonArray groups) continue;
                bool touched = false;
                for (int gi = groups.Count - 1; gi >= 0; gi--)
                {
                    if (groups[gi]?["hooks"] is not JsonArray list) continue;
                    int before = list.Count;
                    for (int i = list.Count - 1; i >= 0; i--)
                        if (IsOurs(list[i])) list.RemoveAt(i);
                    if (list.Count == before) continue;
                    touched = changed = true;
                    if (list.Count == 0) groups.RemoveAt(gi);
                }
                if (touched && groups.Count == 0) hooks.Remove(ev);
            }
            if (changed && hooks.Count == 0) root.Remove("hooks");
            return changed;
        }
    }
}
