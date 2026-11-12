namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class ListView : Control
    {
        private static readonly string WindowClass = "SysListView32";

        public ListView(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(WS_EX_CLIENTEDGE, WindowClass, "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for ListView failed: {Marshal.GetLastWin32Error()}");

            // Register in the static map so the parent form can reflect WM_NOTIFY
            // (NM_DBLCLK etc.) back to this control, and sub-class the native window
            // so script mouse/keyboard events fire, same as ListBox/TreeView-less
            // common controls must do (ListBox does this in its own CreateHandle).
            RegisterHandle();
            SubclassNativeChild();

            // Apply default font
            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);

            // Enable extended styles: grid lines, full row select, and DOUBLE BUFFERING.
            // SysListView32 paints a cleared-and-refilled list straight to the screen, so
            // every refill showed the empty list for a frame (the designer's two panels
            // flickered on every click); LVS_EX_DOUBLEBUFFER composites the whole paint
            // into a memory DC and blits it once.
            SendMessageW(handle, LVM_SETEXTENDEDLISTVIEWSTYLE, 0,
                (IntPtr)(LVS_EX_GRIDLINES | LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER));
        }

        public int InsertColumn(int index, string text, int width)
        {
            if (handle == IntPtr.Zero) CreateControl();   // window must exist before column insert
            var lvc = new LVCOLUMNW
            {
                mask = LVCF_TEXT | LVCF_WIDTH,
                pszText = Marshal.StringToHGlobalUni(text),
                cx = width
            };
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(lvc));
                Marshal.StructureToPtr(lvc, ptr, false);
                var result = (int)SendMessageW(handle, LVM_INSERTCOLUMNW, (nuint)index, ptr);
                Marshal.FreeHGlobal(ptr);
                return result;
            }
            finally { Marshal.FreeHGlobal(lvc.pszText); }
        }

        /// <summary>Renames a column&#39;s header (LVM_SETCOLUMNW), the one part of a column a script can change
        /// after InsertColumn. GetColumnText reads it back, and the header&#39;s text is also one of the two things
        /// FitColumnsToContent sizes a column to, so a rename widens the column as well.</summary>
        public void SetColumnText(int index, string text)
        {
            if (handle == IntPtr.Zero) return;
            var lvc = new LVCOLUMNW
            {
                mask = LVCF_TEXT,
                pszText = Marshal.StringToHGlobalUni(text)
            };
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(lvc));
                Marshal.StructureToPtr(lvc, ptr, false);
                SendMessageW(handle, LVM_SETCOLUMNW, (nuint)index, ptr);
                Marshal.FreeHGlobal(ptr);
            }
            finally { Marshal.FreeHGlobal(lvc.pszText); }
        }

        /// <summary>A column&#39;s header text: the read that pairs with SetColumnText. The buffer discipline is the
        /// one GetItemText states once, and for the same reason: a message that writes NOTHING for an index that
        /// does not exist would otherwise hand back whatever the heap held, read possibly past the allocation.</summary>
        public string GetColumnText(int index)
        {
            if (handle == IntPtr.Zero) return "";
            var chars = MaxCellText;
            var bytes = (chars + 1) * sizeof(char);
            var buf = Marshal.AllocHGlobal(bytes);
            var lvcPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVCOLUMNW>());
            try
            {
                Marshal.Copy(new byte[bytes], 0, buf, bytes);
                var lvc = new LVCOLUMNW
                {
                    mask = LVCF_TEXT,
                    pszText = buf,
                    cchTextMax = chars
                };
                Marshal.StructureToPtr(lvc, lvcPtr, false);
                SendMessageW(handle, LVM_GETCOLUMNW, (nuint)index, lvcPtr);
                var text = Marshal.PtrToStringUni(buf, chars) ?? "";
                var nul = text.IndexOf('\0');
                return nul >= 0 ? text.Substring(0, nul) : text;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Marshal.FreeHGlobal(lvcPtr);
            }
        }

        /// <summary>Removes a column (LVM_DELETECOLUMN). The columns to its right keep their order and move one index
        /// left, which is how that it happened is observable: the row&#39;s text then reads from the next column.</summary>
        public void DeleteColumn(int index)
        {
            if (handle == IntPtr.Zero) return;
            SendMessageW(handle, LVM_DELETECOLUMN, (nuint)index, IntPtr.Zero);
        }

        public int InsertItem(int index, string text)
        {
            if (handle == IntPtr.Zero) CreateControl();
            var lvi = new LVITEMW
            {
                mask = LVIF_TEXT,
                iItem = index,
                pszText = Marshal.StringToHGlobalUni(text)
            };
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(lvi));
                Marshal.StructureToPtr(lvi, ptr, false);
                var result = (int)SendMessageW(handle, LVM_INSERTITEMW, 0, ptr);
                Marshal.FreeHGlobal(ptr);
                return result;
            }
            finally { Marshal.FreeHGlobal(lvi.pszText); }
        }

        public void SetItemText(int row, int col, string text)
        {
            if (handle == IntPtr.Zero) CreateControl();
            var lvi = new LVITEMW
            {
                mask = LVIF_TEXT,
                iItem = row,
                iSubItem = col,
                pszText = Marshal.StringToHGlobalUni(text)
            };
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(lvi));
                Marshal.StructureToPtr(lvi, ptr, false);
                SendMessageW(handle, LVM_SETITEMTEXTW, (nuint)row, ptr);
                Marshal.FreeHGlobal(ptr);
            }
            finally { Marshal.FreeHGlobal(lvi.pszText); }
        }

        /// <summary>The text of one cell: the read that pairs with SetItemText. A SysListView32 keeps its own text, so
        /// getting it back means LVM_GETITEMTEXTW into a buffer of ours, bounded at MaxCellText characters. An empty
        /// string for a cell that does not exist, and no window is created by asking.</summary>
        public string GetItemText(int row, int col)
        {
            if (handle == IntPtr.Zero) return "";
            var chars = MaxCellText;
            var bytes = (chars + 1) * sizeof(char);
            var buf = Marshal.AllocHGlobal(bytes);
            var lviPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LVITEMW>());
            try
            {
                // Zeroed before the call, and read BOUNDED. A row that does not exist leaves the buffer untouched,
                // so an unzeroed one hands back whatever the heap held there (measured: a wall of glyphs for a
                // missing cell), and reading it to the first NUL can then walk past the allocation. Both halves are
                // needed: the zeroing makes a miss EMPTY, the bound makes it SAFE, and the trim drops the padding.
                Marshal.Copy(new byte[bytes], 0, buf, bytes);
                var lvi = new LVITEMW
                {
                    mask = LVIF_TEXT,
                    iSubItem = col,
                    pszText = buf,
                    cchTextMax = chars
                };
                Marshal.StructureToPtr(lvi, lviPtr, false);
                SendMessageW(handle, LVM_GETITEMTEXTW, (nuint)row, lviPtr);
                var text = Marshal.PtrToStringUni(buf, chars) ?? "";
                var nul = text.IndexOf('\0');
                return nul >= 0 ? text.Substring(0, nul) : text;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Marshal.FreeHGlobal(lviPtr);
            }
        }

        public int SelectedIndex
        {
            get => (int)SendMessageW(handle, LVM_GETNEXTITEM, unchecked((nuint)(-1)), (IntPtr)LVNI_SELECTED);
            set
            {
                // Clear selection
                SendMessageW(handle, LVM_SETITEMSTATE, unchecked((nuint)(-1)), IntPtr.Zero);
                // Select new item
                var lvi = new LVITEMW { state = LVIS_SELECTED, stateMask = LVIS_SELECTED };
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(lvi));
                Marshal.StructureToPtr(lvi, ptr, false);
                SendMessageW(handle, LVM_SETITEMSTATE, (nuint)value, ptr);
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void RaiseDoubleClick() => OnDoubleClick(EventArgs.Empty);

        /// <summary>Raised when the selected ROW changes, whether by a click in the list or by an assignment to
        /// <see cref="SelectedIndex"/>. The engine raises it from LVN_ITEMCHANGED, the notification a list view sends
        /// its parent, which means it is the item-state change rather than a tidied "the selection is now N": it can
        /// arrive more than once for one move, and <see cref="SelectedIndex"/> is where the answer is read.</summary>
        public ScriptFunction? SelectedIndexChanged { get; set; }

        /// <summary>The same raise, for a script or a test that has to drive the handler without a mouse.</summary>
        public void RaiseSelectedIndexChanged() =>
            InvokeSafely(this, nameof(SelectedIndexChanged), SelectedIndexChanged, this, EventArgs.Empty);

        // Cell rectangle in the ListView's OWN client coordinates (no conversion to
        // the parent). Used to position an overlay editor that is a CHILD of the
        // listview, so coordinates can never drift from DPI/moves.
        public Rect GetCellClientRect(int row, int col)
        {
            if (handle == IntPtr.Zero) return new Rect { Left = -1, Top = -1, Right = 0, Bottom = 0 };
            var rc = new RECT { left = LVIR_BOUNDS, top = col };
            var got = false;
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(rc));
                try
                {
                    Marshal.StructureToPtr(rc, ptr, false);
                    SendMessageW(handle, LVM_GETSUBITEMRECT, (nuint)row, ptr);
                    rc = Marshal.PtrToStructure<RECT>(ptr);
                    got = true;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { }
            if (!got) return new Rect { Left = -1, Top = -1, Right = 0, Bottom = 0 };
            return new Rect { Left = rc.left, Top = rc.top, Right = Math.Max(1, rc.right - rc.left), Bottom = Math.Max(1, rc.bottom - rc.top) };
        }

        // A reusable EDIT overlay that is a child of this SysListView32 (same
        // technique as a classic Win32 cell editor), created once at load, then
        // moved/shown over the cell being edited.
        public TextBox CreateOverlayTextBox(string text)
        {
            var tb = new TextBox(text ?? "", 0, 0, 60, 18);
            tb.SetParentHwnd(handle);
            tb.CreateControl();
            return tb;
        }

        public void Clear()
        {
            SendMessageW(handle, LVM_DELETEALLITEMS, 0, IntPtr.Zero);
        }


        /// <summary>The rows the list currently holds. An incremental fill reads it to know which rows to
        /// OVERWRITE instead of clearing first, which is the only way to avoid the cleared state being
        /// painted: a composed host (WS_EX_COMPOSITED) repaints a child mid-fill through WM_PRINTCLIENT, and
        /// WM_SETREDRAW does not suppress that, so a clear-then-fill list is seen empty for a frame.</summary>
        public int ItemCount => handle == IntPtr.Zero ? 0 : (int)SendMessageW(handle, LVM_GETITEMCOUNT, 0, IntPtr.Zero);

        /// <summary>How many COLUMNS the table has: the header count, which is where it is counted. Paired with
        /// GetColumnText and the existing GetColumnWidth, this is what lets a document carry a whole table.</summary>
        public int ColumnCount
        {
            get
            {
                if (handle == IntPtr.Zero) return 0;
                var hdr = SendMessageW(handle, LVM_GETHEADER, 0, IntPtr.Zero);
                return hdr == IntPtr.Zero ? 0 : (int)SendMessageW(hdr, HDM_GETITEMCOUNT, 0, IntPtr.Zero);
            }
        }

        /// <summary>Removes one row (LVM_DELETEITEM): the tail of an incremental fill.</summary>
        public void DeleteItem(int index)
        {
            if (handle == IntPtr.Zero) return;
            SendMessageW(handle, LVM_DELETEITEM, (nuint)index, IntPtr.Zero);
        }


        public Rect GetCellRect(int row, int col)
        {
            // Bounds of a subitem cell, converted to the PARENT form's client
            // coordinates so a sibling overlay editor can be positioned directly.
            // LVM_GETSUBITEMRECT input convention: RECT.left = LVIR_* part code,
            // RECT.top = subitem index (verified empirically by the designer).
            if (handle == IntPtr.Zero) return new Rect { Left = -1, Top = -1, Right = 0, Bottom = 0 };
            var rc = new RECT { left = LVIR_BOUNDS, top = col };
            var got = false;
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(rc));
                try
                {
                    Marshal.StructureToPtr(rc, ptr, false);
                    SendMessageW(handle, LVM_GETSUBITEMRECT, (nuint)row, ptr);
                    rc = Marshal.PtrToStructure<RECT>(ptr);
                    got = true;
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { }
            if (!got) return new Rect { Left = -1, Top = -1, Right = 0, Bottom = 0 };
            // Convert the listview-client cell rect into the PARENT form's client
            // coordinates using the LIVE window rectangles (not the stored Left/Top,
            // which can go stale after a DPI change / move): child-client origin in
            // screen = window origin + ((window - client) / 2), then subtract the
            // parent's client origin in screen.
            GetWindowRect(handle, out var lw);
            GetClientRect(handle, out var lc);
            GetWindowRect(ParentHandle, out var pw);
            GetClientRect(ParentHandle, out var pc);
            var lvCx = lw.Left + ((lw.Right - lw.Left) - (lc.Right - lc.Left)) / 2;
            var lvCy = lw.Top + ((lw.Bottom - lw.Top) - (lc.Bottom - lc.Top)) / 2;
            var pCx = pw.Left + ((pw.Right - pw.Left) - (pc.Right - pc.Left)) / 2;
            var pCy = pw.Top + ((pw.Bottom - pw.Top) - (pc.Bottom - pc.Top)) / 2;
            return new Rect { Left = lvCx + rc.left - pCx, Top = lvCy + rc.top - pCy, Right = Math.Max(1, rc.right - rc.left), Bottom = Math.Max(1, rc.bottom - rc.top) };
        }

        public int GetColumnWidth(int col)
        {
            if (handle == IntPtr.Zero) return 0;
            return (int)SendMessageW(handle, LVM_GETCOLUMNWIDTH, (nuint)col, IntPtr.Zero);
        }

        public void SetColumnWidth(int col, int width)
        {
            // LVM_SETCOLUMNWIDTH, used to rescale the grid columns on DPI change.
            if (handle == IntPtr.Zero) return;
            if (width < 1) width = 1;
            SendMessageW(handle, LVM_SETCOLUMNWIDTH, (nuint)col, (IntPtr)width);
        }

        // Auto-size every column to its content + header (LVSCW_AUTOSIZE_USEHEADER),
        // then clamp the total to the client width so no horizontal scrollbar
        // appears. Call after filling the rows and again on DPI change.
        public void FitColumnsToContent()
        {
            if (handle == IntPtr.Zero) return;
            var hdr = SendMessageW(handle, LVM_GETHEADER, 0, IntPtr.Zero);
            if (hdr == IntPtr.Zero) return;
            var n = (int)SendMessageW(hdr, HDM_GETITEMCOUNT, 0, IntPtr.Zero);
            if (n <= 0 || n > 64) return;
            var widths = new int[n];
            var total = 0;
            for (var i = 0; i < n; i++)
            {
                SendMessageW(handle, LVM_SETCOLUMNWIDTH, (nuint)i, (IntPtr)LVSCW_AUTOSIZE_USEHEADER);
                var w = (int)SendMessageW(handle, LVM_GETCOLUMNWIDTH, (nuint)i, IntPtr.Zero);
                if (w < 24) w = 24;
                widths[i] = w;
                total += w;
            }
            GetClientRect(handle, out var rc);
            var cw = rc.Right - rc.Left;
            if (total > cw)
            {
                var excess = total - cw;
                for (var i = n - 1; i >= 0 && excess > 0; i--)
                {
                    var cut = widths[i] - 24;
                    if (cut > excess) cut = excess;
                    if (cut < 0) cut = 0;
                    widths[i] -= cut;
                    excess -= cut;
                }
            }
            for (var i = 0; i < n; i++)
                SendMessageW(handle, LVM_SETCOLUMNWIDTH, (nuint)i, (IntPtr)widths[i]);
        }

        public void SetThemeColors(long? background, long? text)
        {
            // LVM_SETBKCOLOR / LVM_SETTEXTCOLOR / LVM_SETTEXTBKCOLOR for the dark
            // designer palette (the base Control color properties only repaint,
            // they do not tell SysListView32 which colors to draw).
            if (handle == IntPtr.Zero) return;
            // Both arguments are RGB, the colour a script writes everywhere else (0xRRGGBB), and they are converted
            // at this Win32 edge exactly as every other script colour is: LVM_* take a COLORREF, so passing them
            // through unchanged showed the colour SWAPPED (a 0x3050A0 palette arrived as 0xA05030). The dark and
            // light constants the engine and the designer pass are symmetric, which is what kept it invisible.
            if (background.HasValue)
            {
                var c = (IntPtr)ToColorRef((uint)(background.Value & 0xFFFFFFFFL));
                SendMessageW(handle, LVM_SETBKCOLOR, 0, c);
                SendMessageW(handle, LVM_SETTEXTBKCOLOR, 0, c);
            }
            if (text.HasValue)
                SendMessageW(handle, LVM_SETTEXTCOLOR, 0, (IntPtr)ToColorRef((uint)(text.Value & 0xFFFFFFFFL)));
        }

        // Event: ItemClick (handled in parent form's WndProc via LVN_ITEMCHANGED)
    }
}

