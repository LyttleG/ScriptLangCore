namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class TreeView : Control
    {
        private static readonly string WindowClass = "SysTreeView32";

        public TreeView(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(WS_EX_CLIENTEDGE, WindowClass, "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | TVS_HASLINES | TVS_HASBUTTONS | TVS_LINESATROOT,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for TreeView failed: {Marshal.GetLastWin32Error()}");

            // Without these a TreeView receives no script mouse events at all (measured: the click reached the
            // handler for every other placed control and reached NOTHING here), so it cannot be dragged.
            RegisterHandle();
            SubclassNativeChild();

            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);
        }

        public long InsertItem(string text, long parent, long insertAfter)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle))
                return 0;

            var tvis = new TVINSERTSTRUCTW
            {
                hParent = checked((IntPtr)parent),
                hInsertAfter = checked((IntPtr)insertAfter),
                union1 = new TV_UNION
                {
                    item = new TVITEMW
                    {
                        mask = TVIF_TEXT,
                        pszText = Marshal.StringToHGlobalUni(text)
                    }
                }
            };
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(tvis));
                Marshal.StructureToPtr(tvis, ptr, false);
                var result = SendMessageW(handle, TVM_INSERTITEMW, 0, ptr);
                // Measured: comctl32 converts an item's text when it later needs it (an expand reads it), and that
                // text pointer lives INSIDE this struct. Keeping the struct alive past the send changes nothing, so
                // the control copies what it needs and the free belongs here.
                Marshal.FreeHGlobal(ptr);
                return result;
            }
            finally { Marshal.FreeHGlobal(tvis.union1.item.pszText); }
        }

        public void Expand(long item, bool expand)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle))
                return;

            // SENT, not posted. TVM_EXPAND carries the item in lParam, and a POSTED one is dereferenced later, when
            // the tree may no longer be in a state that item belongs to. Sending it keeps the call and the item's
            // lifetime together, and the native control answers synchronously, which is what an expand is.
            //
            // The comment here used to describe the fault as "msg=0x110E (TVM_EXPAND)", which was the bug talking:
            // 0x110E is not TVM_EXPAND, and reading its lParam as a pointer is what crashed. The real id is TV_FIRST
            // + 2, and `treeview_expand_test.slc` is where that contract is pinned.
            SendMessageW(handle, TVM_EXPAND, (nuint)(expand ? TVE_EXPAND : TVE_COLLAPSE), checked((IntPtr)item));
        }

        public long SelectedItem => SendMessageW(handle, TVM_GETNEXTITEM, TVGN_CARET, IntPtr.Zero);

        public void SetItemText(long item, string text)
        {
            var tvitem = new TVITEMW
            {
                mask = TVIF_TEXT,
                hItem = checked((IntPtr)item),
                pszText = Marshal.StringToHGlobalUni(text)
            };
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(tvitem));
                Marshal.StructureToPtr(tvitem, ptr, false);
                SendMessageW(handle, TVM_SETITEMW, 0, ptr);
                Marshal.FreeHGlobal(ptr);
            }
            finally { Marshal.FreeHGlobal(tvitem.pszText); }
        }

        public string GetItemText(long item)
        {
            var buffer = Marshal.AllocHGlobal(512);
            var tvitem = new TVITEMW
            {
                mask = TVIF_TEXT,
                hItem = checked((IntPtr)item),
                pszText = buffer,
                cchTextMax = 255
            };
            try
            {
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(tvitem));
                Marshal.StructureToPtr(tvitem, ptr, false);
                SendMessageW(handle, TVM_GETITEMW, 0, ptr);
                Marshal.FreeHGlobal(ptr);
                return Marshal.PtrToStringUni(buffer) ?? "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>The whole tree as a NESTED list, which is what a document needs to carry a TreeView's contents:
        /// TVM_GETNEXTITEM walks it, with TVGN_CHILD from an item and TVGN_NEXT along its siblings, and the nesting
        /// is kept as nesting because that is the one thing a flat list of texts would lose. The walk is depth
        /// first and bounded by the tree itself, so no cycle can be constructed for it to follow.</summary>
        public List<TreeItemProp> Items()
        {
            var roots = new List<TreeItemProp>();
            if (handle == IntPtr.Zero) return roots;
            var node = (long)SendMessageW(handle, TVM_GETNEXTITEM, TVGN_ROOT, IntPtr.Zero);
            while (node != 0)
            {
                roots.Add(Walk(node));
                node = (long)SendMessageW(handle, TVM_GETNEXTITEM, TVGN_NEXT, (IntPtr)node);
            }
            return roots;

            TreeItemProp Walk(long item)
            {
                var prop = new TreeItemProp { Text = GetItemText(item) };
                var child = (long)SendMessageW(handle, TVM_GETNEXTITEM, TVGN_CHILD, (IntPtr)item);
                while (child != 0)
                {
                    prop.Children.Add(Walk(child));
                    child = (long)SendMessageW(handle, TVM_GETNEXTITEM, TVGN_NEXT, (IntPtr)child);
                }
                return prop;
            }
        }
    }
}

