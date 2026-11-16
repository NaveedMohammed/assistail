using System;
using System.Diagnostics;
using System.IO;

namespace Assistail
{
    public static class Launcher
    {
        /// <summary>Opens Windows Terminal running `claude` in the folder (home folder if none).</summary>
        public static void OpenClaudeCode(string? folder)
        {
            string dir = !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WindowsApps\wt.exe");
                ProcessStartInfo psi;
                if (File.Exists(wt))
                {
                    psi = new ProcessStartInfo(wt) { UseShellExecute = false };
                    // "\." avoids a trailing backslash (e.g. C:\) breaking wt's argument parsing.
                    foreach (var a in new[] { "-d", Path.Combine(dir, "."), "cmd.exe", "/k", "claude" }) psi.ArgumentList.Add(a);
                }
                else
                {
                    psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = dir, Arguments = "/k claude" };
                }
                Process.Start(psi);
            }
            catch (Exception ex) { App.Log("OpenClaudeCode failed: " + ex); }
        }
    }
}
