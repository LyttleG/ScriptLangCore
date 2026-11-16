namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>
    /// Represents a Windows-style drop-down list control that enables users to select a single item from a list of
    /// options.
    /// </summary>
    /// <remarks>The ComboBox control displays a list of items in a drop-down format, allowing users to select
    /// one item at a time. Items can be added, removed, or cleared programmatically. The control does not support
    /// direct text entry; users must select from the provided list. The selected item and its index can be accessed or
    /// modified through the SelectedItem and SelectedIndex properties. The control is not thread-safe and should be
    /// accessed only from the UI thread.</remarks>
    public sealed class ComboBox : Control
    {
        private const string WindowClass = "COMBOBOX";
        private int _pendingSelectedIndex = -1;

        public ComboBox(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(0, WindowClass, "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL | CBS_NOINTEGRALHEIGHT,
                Left, Top, Width, Height * 10,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for ComboBox failed: {Marshal.GetLastWin32Error()}");
            // Register in the static map so the parent Form can reflect CBN_* notifications, and subclass the
            // native window so script mouse events fire. Without the second call a ComboBox (like a TreeView or a
            // DateTimePicker) never receives MouseDown/MouseMove/MouseUp at all, so a designer cannot drag it:
            // measured, the click reached the script handler for ListBox/ListView/TrackBar/Button/Label/TextBox
            // and reached NOTHING for these three. Same pair ListBox and ListView already do.
            RegisterHandle();
            SubclassNativeChild();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_pendingSelectedIndex != -1)
            {
                var count = (int)SendMessageW(handle, CB_GETCOUNT, 0, IntPtr.Zero);
                if (count > _pendingSelectedIndex)
                {
                    SendMessageW(handle, CB_SETCURSEL, (nuint)_pendingSelectedIndex, IntPtr.Zero);
                    RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
                }
                _pendingSelectedIndex = -1;
            }
        }

        public int SelectedIndex
        {
            get => (int)SendMessageW(handle, CB_GETCURSEL, 0, IntPtr.Zero);
            set
            {
                if (handle != IntPtr.Zero && IsWindow(handle))
                {
                    var count = (int)SendMessageW(handle, CB_GETCOUNT, 0, IntPtr.Zero);
                    if (count > value)
                    {
                        SendMessageW(handle, CB_SETCURSEL, (nuint)value, IntPtr.Zero);
                        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
                    }
                }
                else
                {
                    _pendingSelectedIndex = value;
                }
            }
        }

        public void AddItem(string item)
        {
            if (handle != IntPtr.Zero && IsWindow(handle))
            {
                var ptr = Marshal.StringToHGlobalUni(item);
                SendMessageW(handle, CB_ADDSTRING, 0, ptr);
                Marshal.FreeHGlobal(ptr);
                RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
            }
        }

        public void RemoveItem(int index)
        {
            if (handle != IntPtr.Zero && IsWindow(handle))
            {
                SendMessageW(handle, CB_DELETESTRING, (nuint)index, IntPtr.Zero);
                RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
            }
        }

        public void Clear()
        {
            if (handle != IntPtr.Zero && IsWindow(handle))
            {
                SendMessageW(handle, CB_RESETCONTENT, 0, IntPtr.Zero);
                RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
            }
        }

        public string SelectedItem
        {
            get
            {
                var idx = SelectedIndex;
                if (idx < 0) return "";
                var len = (int)SendMessageW(handle, CB_GETLBTEXTLEN, (nuint)idx, IntPtr.Zero);
                if (len <= 0) return "";
                var buffer = Marshal.AllocHGlobal((len + 1) * 2);
                SendMessageW(handle, CB_GETLBTEXT, (nuint)idx, buffer);
                var result = Marshal.PtrToStringUni(buffer) ?? "";
                Marshal.FreeHGlobal(buffer);
                return result;
            }
        }
    }
}

