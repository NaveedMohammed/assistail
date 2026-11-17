using System;
using System.Runtime.InteropServices;

namespace Assistail
{
    /// <summary>
    /// A2: lets files (or dragged text) be dropped on the buddy. His window is a raw Win32 layered window, so this is
    /// a plain OLE drop target registered with RegisterDragDrop; the data object is read through WPF's DataObject.
    /// </summary>
    [ComVisible(true)]
    internal sealed class DropTarget : IOleDropTarget
    {
        private const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1;
        private bool _accept;

        /// <summary>Something droppable entered (true) or left (false).</summary>
        public event Action<bool>? Hover;
        /// <summary>Files/folders, or dragged text when there are no files.</summary>
        public event Action<string[], string?>? Dropped;

        public static DropTarget? Register(IntPtr hwnd)
        {
            var target = new DropTarget();
            int hr = Native.RegisterDragDrop(hwnd, target);
            if (hr != 0) { App.Log($"RegisterDragDrop failed: 0x{hr:X8}"); return null; }
            return target;
        }

        private static System.Windows.IDataObject? Wrap(object data)
        {
            try { return new System.Windows.DataObject(data); }
            catch (Exception ex) { App.Log("Drop data unreadable: " + ex.Message); return null; }
        }

        public int DragEnter(object dataObject, int keyState, POINTL pt, ref int effect)
        {
            var d = Wrap(dataObject);
            _accept = d != null && (d.GetDataPresent(System.Windows.DataFormats.FileDrop) || d.GetDataPresent(System.Windows.DataFormats.UnicodeText));
            effect = _accept ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            if (_accept) Hover?.Invoke(true);
            return 0;
        }

        public int DragOver(int keyState, POINTL pt, ref int effect)
        {
            effect = _accept ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            return 0;
        }

        public int DragLeave()
        {
            if (_accept) Hover?.Invoke(false);
            _accept = false;
            return 0;
        }

        public int Drop(object dataObject, int keyState, POINTL pt, ref int effect)
        {
            effect = DROPEFFECT_NONE;
            if (!_accept) return 0;
            _accept = false;
            Hover?.Invoke(false);
            try
            {
                var d = Wrap(dataObject);
                if (d == null) return 0;
                string[] files = d.GetData(System.Windows.DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
                string? text = files.Length == 0 ? d.GetData(System.Windows.DataFormats.UnicodeText) as string : null;
                if (files.Length == 0 && string.IsNullOrWhiteSpace(text)) return 0;
                effect = DROPEFFECT_COPY;
                // Let the drag source finish first (Explorer waits for Drop to return).
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => Dropped?.Invoke(files, text));
            }
            catch (Exception ex) { App.Log("Drop failed: " + ex); }
            return 0;
        }
    }
}
