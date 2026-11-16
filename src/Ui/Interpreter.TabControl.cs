namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class TabControl : Control
    {
        private static readonly string WindowClass = "SysTabControl32";
        private int _pendingSelectedIndex = -1;
        private bool _isHandleReady;

        public TabControl(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(0, WindowClass, "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | TCS_FIXEDWIDTH,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for TabControl failed: {Marshal.GetLastWin32Error()}");

            SetWindowTheme(handle, "Explorer", "");

            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);

            // Register in the static map, and subclass the native window so script mouse events fire: without this
            // a StatusBar (like a TabControl or a ToolBar) receives no MouseDown at all, so a designer cannot drag
            // it. Measured with a placed control of each kind and a posted click, the same way ComboBox, TreeView
            // and DateTimePicker were found. Same pair ListBox and ListView already call.
            RegisterHandle();
            SubclassNativeChild();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _isHandleReady = true;

            if (_pendingSelectedIndex != -1)
            {
                // Post a custom message to apply selection after all pending messages
                PostMessageW(handle, WM_APP_TAB_SETCURSEL, _pendingSelectedIndex, IntPtr.Zero);
                _pendingSelectedIndex = -1;
            }
        }

        protected override IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_APP_TAB_SETCURSEL)
            {
                if (IsWindow(handle))
                    SendMessageW(handle, TCM_SETCURSEL, (nuint)wParam, IntPtr.Zero);
                return IntPtr.Zero;
            }
            return base.WndProc(hwnd, msg, wParam, lParam);
        }

        public int AddTab(string text)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle))
                return -1;

            TCITEMW item = new()
            {
                mask = TCIF_TEXT,
                pszText = Marshal.StringToHGlobalUni(text)
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(item));
            try
            {
                var index = (int)SendMessageW(handle, TCM_GETITEMCOUNT, 0, IntPtr.Zero);
                Marshal.StructureToPtr(item, ptr, false);
                SendMessageW(handle, TCM_INSERTITEMW, (nuint)index, ptr);
                return index;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
                Marshal.FreeHGlobal(item.pszText);
            }
        }

        /// <summary>How many tabs the control holds, and one tab's TEXT: the read that pairs with AddTab, over the
        /// TCITEMW the insert already fills. The buffer discipline is GetColumnText's, for the same reason: a message
        /// that writes nothing for an index that does not exist must not hand back what the heap held.</summary>
        public int TabCount
            => handle == IntPtr.Zero || !IsWindow(handle) ? 0 : (int)SendMessageW(handle, TCM_GETITEMCOUNT, 0, IntPtr.Zero);

        public string GetTabText(int index)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return "";
            var chars = MaxCellText;
            var bytes = (chars + 1) * sizeof(char);
            var buf = Marshal.AllocHGlobal(bytes);
            var itemPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TCITEMW>());
            try
            {
                Marshal.Copy(new byte[bytes], 0, buf, bytes);
                var item = new TCITEMW { mask = TCIF_TEXT, pszText = buf, cchTextMax = chars };
                Marshal.StructureToPtr(item, itemPtr, false);
                SendMessageW(handle, TCM_GETITEMW, (nuint)index, itemPtr);
                var text = Marshal.PtrToStringUni(buf, chars) ?? "";
                var nul = text.IndexOf('\0');
                return nul >= 0 ? text.Substring(0, nul) : text;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Marshal.FreeHGlobal(itemPtr);
            }
        }

        public int SelectedIndex
        {
            get
            {
                if (handle == IntPtr.Zero || !IsWindow(handle))
                    return -1;
                return (int)SendMessageW(handle, TCM_GETCURSEL, 0, IntPtr.Zero);
            }
            set
            {
                if (handle != IntPtr.Zero && IsWindow(handle) && _isHandleReady)
                {
                    PostMessageW(handle, WM_APP_TAB_SETCURSEL, value, IntPtr.Zero);
                }
                else
                {
                    _pendingSelectedIndex = value;
                }
            }
        }
    }
}

