using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assistail
{
    /// <summary>
    /// Talks to Claude through the Claude Code CLI in headless mode (`claude -p`), so it uses your
    /// existing Claude login. Follow-up messages continue the same conversation via --resume.
    /// </summary>
    public sealed class ClaudeCli
    {
        /// <summary>The buddy's name; Claude introduces itself with it.</summary>
        public string BuddyName { get; set; } = "Leo";

        private string SystemPrompt =>
            $"Your name is {BuddyName}. You are the user's desk buddy: a friendly cartoon lion with a curly mane who " +
            $"sits on their Windows taskbar, powered by Claude. If asked your name, you are {BuddyName}. " +
            "Keep answers short and direct. The chat box shows plain text, so avoid heavy markdown " +
            "(no tables or headings); simple lists and code are fine.";

        private static readonly Lazy<string?> ClaudeExe = new(FindClaudeExe);

        public string? SessionId { get; private set; }

        public void Reset() => SessionId = null;

        /// <summary>Read-only tools the chat may use inside the chosen folder. Nothing that edits files or runs commands.</summary>
        private const string ReadOnlyTools = "Read,Glob,Grep";

        private static string FolderPrompt(string folder) =>
            $" The user's current folder is {folder}. You can look at files in it with the Read, Glob and Grep tools " +
            "(always use absolute paths under that folder). Only read what you need to answer. You cannot edit files or " +
            "run commands here: for that, tell the user to say \"open claude here\" to start Claude Code in this folder.";

        /// <summary>
        /// Sends a prompt and streams text back through <paramref name="onText"/>. With <paramref name="folder"/>, Claude
        /// may read (never change) files there; <paramref name="onTool"/> reports each look ("Reading App.cs…").
        /// Returns an error message, or null on success.
        /// </summary>
        public async Task<string?> SendAsync(string prompt, string model, string? folder, Action<string> onText,
                                             Action<string>? onTool, CancellationToken ct)
        {
            string workDir = Path.Combine(App.DataDir, "chat"); // fixed folder: keeps --resume working and avoids loading project files
            Directory.CreateDirectory(workDir);

            var psi = new ProcessStartInfo
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            string? exe = ClaudeExe.Value;
            if (exe != null) psi.FileName = exe;
            else { psi.FileName = "cmd.exe"; psi.ArgumentList.Add("/c"); psi.ArgumentList.Add("claude"); }

            // The chat always runs in its own folder (so --resume keeps working when the user switches folders);
            // the chosen folder is added with --add-dir and only the read-only tools are available.
            if (folder != null && !Directory.Exists(folder)) folder = null;
            foreach (var a in new[] { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages" })
                psi.ArgumentList.Add(a);
            if (folder != null)
            {
                foreach (var a in new[] { "--tools", ReadOnlyTools, "--allowedTools", ReadOnlyTools, "--add-dir", folder,
                                          "--append-system-prompt", SystemPrompt + FolderPrompt(folder) })
                    psi.ArgumentList.Add(a);
            }
            else
            {
                foreach (var a in new[] { "--tools", "", "--append-system-prompt", SystemPrompt })
                    psi.ArgumentList.Add(a);
            }
            if (!string.IsNullOrEmpty(model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
            if (SessionId != null) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(SessionId); }

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception ex) { return "Couldn't start Claude Code (is `claude` installed and on PATH?)\n" + ex.Message; }

            using (proc)
            using (ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } }))
            {
                // The prompt goes over stdin, which avoids command-line quoting problems.
                await proc.StandardInput.WriteAsync(prompt);
                proc.StandardInput.Close();

                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                bool gotText = false;
                bool newMessage = false;   // a later message (after a tool call) starts on a new paragraph
                string? error = null;

                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (line.Length == 0 || line[0] != '{') continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                            SessionId = sid.GetString();

                        switch (type)
                        {
                            case "stream_event":
                                var ev = root.GetProperty("event");
                                if (ev.TryGetProperty("type", out var et) && et.GetString() == "message_start" && gotText)
                                    newMessage = true;
                                if (ev.TryGetProperty("delta", out var delta) &&
                                    delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta")
                                {
                                    string chunk = delta.GetProperty("text").GetString() ?? "";
                                    if (newMessage) { chunk = "\n\n" + chunk.TrimStart(); newMessage = false; }
                                    onText(chunk);
                                    gotText = true;
                                }
                                break;

                            case "assistant":
                                foreach (var block in root.GetProperty("message").GetProperty("content").EnumerateArray())
                                {
                                    string btype = block.TryGetProperty("type", out var bt) ? bt.GetString() ?? "" : "";
                                    if (btype == "tool_use") onTool?.Invoke(DescribeTool(block));
                                    else if (!gotText && block.TryGetProperty("text", out var txt))   // fallback if partial messages aren't streamed
                                    { onText(txt.GetString() ?? ""); gotText = true; }
                                }
                                break;

                            case "result":
                                bool isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                                string? result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                                if (isError) error = result ?? "Claude returned an error.";
                                else if (!gotText && !string.IsNullOrEmpty(result)) { onText(result); gotText = true; }
                                break;
                        }
                    }
                    catch (Exception ex) { App.Log("Bad stream line: " + ex.Message); }
                }

                await proc.WaitForExitAsync(CancellationToken.None);
                string stderr = (await stderrTask).Trim();
                if (ct.IsCancellationRequested) return null;
                if (error == null && proc.ExitCode != 0 && !gotText)
                    error = stderr.Length > 0 ? stderr : $"Claude Code exited with code {proc.ExitCode}.";
                if (error != null) App.Log("Claude error: " + error + (stderr.Length > 0 ? "\n" + stderr : ""));
                return error;
            }
        }

        /// <summary>A short status line for a tool call: "Reading App.xaml.cs…", "Searching for “Login”…".</summary>
        private static string DescribeTool(JsonElement block)
        {
            string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            JsonElement input = block.TryGetProperty("input", out var i) ? i : default;
            string? Arg(string key) =>
                input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return name switch
            {
                "Read" => "Reading " + (Path.GetFileName(Arg("file_path") ?? "") is { Length: > 0 } f ? f : "a file") + "…",
                "Glob" => "Looking for " + (Arg("pattern") ?? "files") + "…",
                "Grep" => "Searching for “" + (Arg("pattern") ?? "") + "”…",
                _ => "Looking around…",
            };
        }

        /// <summary>Finds claude.exe directly (faster and cleaner than going through cmd.exe).</summary>
        private static string? FindClaudeExe()
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in dirs)
            {
                string dir = raw.Trim().Trim('"');
                try
                {
                    string exe = Path.Combine(dir, "claude.exe");
                    if (File.Exists(exe)) return exe;
                    if (File.Exists(Path.Combine(dir, "claude.cmd")))
                    {
                        string npmExe = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                        if (File.Exists(npmExe)) return npmExe;
                    }
                }
                catch { /* bad PATH entry */ }
            }
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            return File.Exists(local) ? local : null;
        }
    }
}
