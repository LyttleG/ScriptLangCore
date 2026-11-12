namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>
    /// ListBox control with support for adding/removing items and getting/setting the selected index and item text.
    /// </summary>
    public sealed class ListBox : Control
    {
        private static readonly string WindowClass = "LISTBOX";
        private int _pendingSelectedIndex = -1;

        // Add this event
        // Settable, not read-only: the raise below has always been here, but a get-only property can
        // never be non-null, so the event could not fire for any script.
        public ScriptFunction? SelectedIndexChanged { get; set; }

        public ListBox(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(WS_EX_CLIENTEDGE, WindowClass, "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | LBS_NOTIFY | WS_VSCROLL | LBS_DISABLENOSCROLL | LBS_NOINTEGRALHEIGHT,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for ListBox failed: {Marshal.GetLastWin32Error()}");
            // Register in the static map so the parent Form can reflect LBN_*
            // notifications (selection / double-click) back to this control.
            RegisterHandle();
            // Subclass so script mouse events (double-click etc.) fire on the native listbox.
            SubclassNativeChild();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_pendingSelectedIndex != -1)
            {
                var count = (int)SendMessageW(handle, LB_GETCOUNT, 0, IntPtr.Zero);
                if (count > _pendingSelectedIndex)
                {
                    SendMessageW(handle, LB_SETCURSEL, (nuint)_pendingSelectedIndex, IntPtr.Zero);
                    RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
                }
                _pendingSelectedIndex = -1;
            }
        }

        // Override WndProc to handle selection change notifications
        protected override IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_COMMAND)
            {
                var code = Hiword(wParam);
                var hwndCtrl = lParam;

                // LBN_SELCHANGE = 1 - Selection changed notification
                if (code == LBN_SELCHANGE && hwndCtrl == handle)
                {
                    InvokeSafely(this, nameof(SelectedIndexChanged), SelectedIndexChanged, this, EventArgs.Empty);
                }
                // LBN_DBLCLK = 2 - Double-click on an item (reflected from the parent)
                else if (code == LBN_DBLCLK && hwndCtrl == handle)
                {
                    OnDoubleClick(EventArgs.Empty);
                }
            }

            return base.WndProc(hwnd, msg, wParam, lParam);
        }

        /// <summary>The item index at a CLIENT point, or -1 for the empty space below the last row

        /// (LB_ITEMFROMPOINT answers the index in its low word, and a 1 in its high word when the point is outside).

        /// SelectedIndex cannot answer this: a click's row is what a list-of-choices needs, and in a multi-select

        /// list LB_GETCURSEL has no single answer, while the point always does.</summary>

        public int ItemIndexAt(int x, int y)

        {

            if (handle == IntPtr.Zero) return -1;
            var r = (long)SendMessageW(handle, 0x01A9u, 0, (nint)(((long)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF)));
            // LB_ITEMFROMPOINT answers the index in its LOW word and a 1 in its HIGH word when the point is not on
            // an item, in which case the index is merely the NEAREST row. Masking that away, as this did, made a click
            // past the last row resolve to a real row: the style editor then moved a flag the user never aimed at, and
            // at the end of a family it appeared to add other values by itself.
            if (((r >> 16) & 0xFFFF) != 0) return -1;
            return (int)(r & 0xFFFF);

        }

        public int SelectedIndex
        {
            get => (int)SendMessageW(handle, LB_GETCURSEL, 0, IntPtr.Zero);
            set
            {
                if (handle != IntPtr.Zero && IsWindow(handle))
                {
                    var count = (int)SendMessageW(handle, LB_GETCOUNT, 0, IntPtr.Zero);
                    if (count > value)
                    {
                        SendMessageW(handle, LB_SETCURSEL, (nuint)value, IntPtr.Zero);
                        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
                    }
                }
                else
                {
                    _pendingSelectedIndex = value;
                }
            }
        }

        // Items property for easier manipulation
        private ItemsCollection Items => new(this);

        public class ItemsCollection(ListBox owner)
        {
            public void Clear()
            {
                if (owner.handle != IntPtr.Zero && IsWindow(owner.handle))
                {
                    SendMessageW(owner.handle, LB_RESETCONTENT, 0, IntPtr.Zero);
                    RedrawWindow(owner.handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
                }
            }

            public void Add(string item)
            {
                if (owner.handle != IntPtr.Zero && IsWindow(owner.handle))
                {
                    var ptr = Marshal.StringToHGlobalUni(item);
                    SendMessageW(owner.handle, LB_ADDSTRING, 0, ptr);
                    Marshal.FreeHGlobal(ptr);
                    RedrawWindow(owner.handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_UPDATENOW);
                }
            }

            public int Count => (int)SendMessageW(owner.handle, LB_GETCOUNT, 0, IntPtr.Zero);

            public string this[int index]
            {
                get
                {
                    var len = (int)SendMessageW(owner.handle, LB_GETTEXTLEN, (nuint)index, IntPtr.Zero);
                    if (len <= 0) return "";
                    var buffer = Marshal.AllocHGlobal((len + 1) * 2);
                    SendMessageW(owner.handle, LB_GETTEXT, (nuint)index, buffer);
                    var result = Marshal.PtrToStringUni(buffer) ?? "";
                    Marshal.FreeHGlobal(buffer);
                    return result;
                }
            }
        }

        public void AddItem(string item) => Items.Add(item);
        public void RemoveItem(int index) => SendMessageW(handle, LB_DELETESTRING, (nuint)index, IntPtr.Zero);
        public void Clear() => Items.Clear();

        public string SelectedItem
        {
            get
            {
                var idx = SelectedIndex;
                if (idx < 0) return "";
                return Items[idx];
            }
        }
    }
}

