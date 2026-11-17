using System;

namespace Assistail
{
    /// <summary>
    /// Entry point. The command-line helper modes run without starting WPF, so they stay fast:
    ///   --notify stop|notification   called by Claude Code hooks (A1), forwards the event to the running app
    ///   --remove-hooks               used by the uninstaller to take the hooks out of Claude Code's settings
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--notify") return ClaudeHooks.RunNotifier(args);
            if (args.Length > 0 && args[0] == "--remove-hooks")
            {
                if (ClaudeHooks.State() != ClaudeHooks.HookState.NotInstalled) ClaudeHooks.Remove();
                return 0;
            }
            // Testing aid: --test-hooks install|remove|state (use with CLAUDE_CONFIG_DIR pointing at a scratch folder).
            if (args.Length > 1 && args[0] == "--test-hooks")
            {
                string? err = args[1] switch { "install" => ClaudeHooks.Install(), "remove" => ClaudeHooks.Remove(), _ => null };
                App.Log($"TEST hooks {args[1]}: error={err ?? "none"} state={ClaudeHooks.State()} file={ClaudeHooks.SettingsPath}");
                return 0;
            }

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
