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

        /// <summary>Sends a prompt and streams text back through <paramref name="onText"/>. Returns an error message, or null on success.</summary>
        public async Task<string?> SendAsync(string prompt, string model, Action<string> onText, CancellationToken ct)
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

            foreach (var a in new[] { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                                      "--tools", "", "--append-system-prompt", SystemPrompt })
                psi.ArgumentList.Add(a);
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
                                if (ev.TryGetProperty("delta", out var delta) &&
                                    delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta")
                                {
                                    onText(delta.GetProperty("text").GetString() ?? "");
                                    gotText = true;
                                }
                                break;

                            case "assistant" when !gotText: // fallback if partial messages aren't streamed
                                foreach (var block in root.GetProperty("message").GetProperty("content").EnumerateArray())
                                    if (block.TryGetProperty("text", out var txt)) { onText(txt.GetString() ?? ""); gotText = true; }
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
