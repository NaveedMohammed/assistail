using System;
using System.Linq;
using System.Windows.Forms;

namespace Assistail.Extras
{
    /// <summary>B5: the Extras ▸ submenu (tray and right-click on the buddy), rebuilt each time the menu opens.</summary>
    public static class ExtrasMenu
    {
        public static void Fill(ToolStripMenuItem menu, ExtrasEngine engine, Action<string> open, Action installFromFile, Func<int> updates)
        {
            var items = menu.DropDownItems;
            items.Clear();
            var bold = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);

            // Routines: start, or control the one that's running.
            var running = engine.RunningRoutine;
            foreach (var p in engine.Packs.Where(p => p.IsUsable && p.Routine != null && engine.IsEnabled(p)))
            {
                if (p == running)
                {
                    items.Add(new ToolStripMenuItem(engine.RoutineStatus ?? p.Routine!.Label) { Enabled = false, Font = bold });
                    string? waiting = engine.WaitingStepName;
                    if (waiting != null)
                        items.Add(new ToolStripMenuItem($"Start {waiting.ToLowerInvariant()}", null, (_, _) => engine.SkipStep()));
                    else
                    {
                        items.Add(engine.RoutinePaused
                            ? new ToolStripMenuItem("Resume", null, (_, _) => engine.ResumeRoutine())
                            : new ToolStripMenuItem("Pause", null, (_, _) => engine.PauseRoutine()));
                        items.Add(new ToolStripMenuItem("Skip to the next step", null, (_, _) => engine.SkipStep()));
                    }
                    items.Add(new ToolStripMenuItem($"Stop {p.Routine!.Label}", null, (_, _) => engine.StopRoutine()));
                }
                else
                    items.Add(new ToolStripMenuItem($"{(p.Icon.Length > 0 ? p.Icon + " " : "")}Start {p.Routine!.Label}", null, (_, _) => engine.StartRoutine(p)));
            }
            if (items.Count > 0) items.Add(new ToolStripSeparator());

            // Every installed pack with an on/off check.
            if (engine.Packs.Count == 0)
                items.Add(new ToolStripMenuItem("No extras installed yet") { Enabled = false });
            foreach (var p in engine.Packs)
            {
                if (!p.IsUsable)
                {
                    items.Add(new ToolStripMenuItem($"⚠ {p.Id} ({(p.NeedsNewer ? "needs a newer Assistail" : "broken")})")
                    { Enabled = false, ToolTipText = p.Error });
                    continue;
                }
                var item = new ToolStripMenuItem(p.Title) { Checked = engine.IsEnabled(p), ToolTipText = p.Description };
                item.Click += (_, _) => engine.SetEnabled(p, !engine.IsEnabled(p));
                items.Add(item);
            }

            items.Add(new ToolStripSeparator());
            int n = updates();
            items.Add(new ToolStripMenuItem("Get more extras…", null, (_, _) => open("available")));
            if (n > 0) items.Add(new ToolStripMenuItem($"Update extras ({n})…", null, (_, _) => open("updates")) { Font = bold });
            items.Add(new ToolStripMenuItem("Manage extras…", null, (_, _) => open("installed")));
            items.Add(new ToolStripMenuItem("Install pack from file…", null, (_, _) => installFromFile()));
            items.Add(new ToolStripMenuItem("Open extras folder", null, (_, _) =>
            {
                System.IO.Directory.CreateDirectory(ExtrasEngine.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExtrasEngine.Folder) { UseShellExecute = true });
            }));
            menu.Text = running != null ? $"Extras · {engine.RoutineStatus}" : "Extras";
        }
    }
}
