using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Assistail
{
    /// <summary>Remembers the last File Explorer window (or the desktop) you used, and resolves its folder.</summary>
    public sealed class ExplorerTracker : IDisposable
    {
        private static readonly IntPtr DesktopMarker = new IntPtr(-1);
        private readonly Native.WinEventDelegate _callback; // kept in a field so the GC doesn't collect it
        private readonly IntPtr _hook;
        private readonly uint _ownPid = (uint)Environment.ProcessId;
        private IntPtr _lastExplorer = IntPtr.Zero;

        public ExplorerTracker()
        {
            _callback = OnForegroundChanged;
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            Remember(Native.GetForegroundWindow());
        }

        /// <summary>Any window came to the front (used to clear a Claude Code alert once you look at that terminal).</summary>
        public event Action<IntPtr>? ForegroundChanged;

        private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            Remember(hwnd);
            ForegroundChanged?.Invoke(hwnd);
        }

        private void Remember(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return;
            string cls = Native.ClassOf(hwnd);
            if (cls == "CabinetWClass") _lastExplorer = hwnd;
            else if (cls == "Progman" || cls == "WorkerW") _lastExplorer = DesktopMarker;
        }

        /// <summary>Folder of the current foreground window if it's Explorer/desktop, else the last one used.</summary>
        public string? FolderOfForegroundOrLast()
        {
            Remember(Native.GetForegroundWindow());
            return LastExplorerFolder();
        }

        public string? LastExplorerFolder()
        {
            if (_lastExplorer == DesktopMarker) return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (_lastExplorer == IntPtr.Zero || !Native.IsWindow(_lastExplorer)) return null;
            return FolderOf(_lastExplorer);
        }

        private static string? FolderOf(IntPtr explorerHwnd)
        {
            string? firstMatch = null;
            try
            {
                Type? t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return null;
                dynamic shell = Activator.CreateInstance(t)!;
                IntPtr activeTab = Native.FindWindowEx(explorerHwnd, IntPtr.Zero, "ShellTabWindowClass", null);

                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        if (new IntPtr((long)w.HWND) != explorerHwnd) continue;
                        string path = w.Document.Folder.Self.Path;
                        if (!Directory.Exists(path)) continue; // skips virtual folders like "This PC"
                        firstMatch ??= path;
                        if (activeTab != IntPtr.Zero && TabHandleOf((object)w) == activeTab) return path;
                    }
                    catch { /* window closed or not a folder view */ }
                }
            }
            catch (Exception ex) { App.Log("FolderOf failed: " + ex.Message); }
            return firstMatch;
        }

        private static IntPtr TabHandleOf(object shellWindow)
        {
            try
            {
                var sp = (IServiceProvider)shellWindow;
                Guid sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                Guid iidShellBrowser = typeof(IShellBrowser).GUID;
                if (sp.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser, out IntPtr ptr) != 0 || ptr == IntPtr.Zero)
                    return IntPtr.Zero;
                try
                {
                    var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(ptr);
                    return browser.GetWindow(out IntPtr tab) == 0 ? tab : IntPtr.Zero;
                }
                finally { Marshal.Release(ptr); }
            }
            catch { return IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        }
    }
}
