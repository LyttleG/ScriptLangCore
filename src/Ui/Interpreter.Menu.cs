namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>A WINDOW MENU as a script names it: a bar, a popup, or a submenu of either. The ids and the Win32
    /// flags are the engine&#39;s and the WORDS are the script&#39;s, the division every other door here makes. An item is
    /// added with the id the script sees again, and a click arrives as the form&#39;s Command event carrying it in
    /// CommandId, which is the WM_COMMAND-with-no-lParam arm beside the control one.</summary>
    public sealed partial class Menu : IDisposable
    {
        internal nint Handle;
        internal Menu(nint h) { Handle = h; }

        /// <summary>An empty MENU, for a window&#39;s menu bar.</summary>
        public static Menu Create() => new(CreateMenu());
        /// <summary>An empty POPUP, for a context menu. AddPopup makes one for a submenu.</summary>
        public static Menu CreatePopup() => new(CreatePopupMenu());

        /// <summary>Add an item, with the id a click reports through the form&#39;s Command event.</summary>
        public void Add(string text, int id) { AppendMenuW(Handle, MF_STRING, (nint)id, text); }
        /// <summary>A SEPARATOR: what a menu wants between groups, rather than a blank item.</summary>
        public void AddSeparator() { AppendMenuW(Handle, MF_SEPARATOR, 0, ""); }

        /// <summary>Add a SUBMENU and answer it, so its items are added on the object that owns them.</summary>
        public Menu AddPopup(string text)
        {
            var sub = CreatePopupMenu();
            AppendMenuW(Handle, MF_POPUP, sub, text);
            return new Menu(sub);
        }

        /// <summary>Show it as a CONTEXT menu at the CURSOR, which is what a right-click wants.</summary>
        public void Popup(Form owner) { PopupAt(owner, int.MinValue, int.MinValue); }
        /// <summary>...or at a POINT the caller names, which is what a keyboard menu wants.</summary>
        public void Popup(Form owner, int x, int y) { PopupAt(owner, x, y); }
        private void PopupAt(Form owner, int x, int y)
        {
            var px = x; var py = y;
            if (px == int.MinValue || py == int.MinValue) { var cur = new Point(); GetCursorPos(ref cur); px = cur.x; py = cur.y; }
            // A point the caller NAMES is in the owner's CLIENT coordinates, the same numbers every other control member
            // takes, so it is converted here: TrackPopupMenu wants SCREEN coordinates, and a script passing its MouseDown
            // point straight through got a menu that appeared somewhere off the window, which is how this was reported.
            else
            {
                var pt = new Point(); pt.x = px; pt.y = py;
                _ = NativeClientToScreen(owner.Handle, ref pt);
                px = pt.x; py = pt.y;
            }
            // No TPM_RETURNCMD: a context menu reports through the SAME Command event a menu bar&#39;s item does, so there
            // is one implementation and nothing here reads a return value.
            // THE OWNER IS BROUGHT FORWARD FIRST, a documented requirement rather than a nicety: without it the menu
            // does not take the input, so it does not dismiss when the user clicks away and the same popup cannot be
            // shown a second time. Reported from a script whose popup could never be made to reappear.
            NativeSetForegroundWindow(owner.Handle);
            TrackPopupMenu(Handle, TPM_LEFTALIGN | TPM_RIGHTBUTTON, px, py, 0, owner.Handle, IntPtr.Zero);
            // The POSTED null is the other half of the recipe: it leaves the owner's queue in a state where the next
            // click is delivered normally instead of being eaten by the menu that has just closed.
            NativePostMessage(owner.Handle, WM_NULL, 0, 0);
        }

        /// <summary>The menu a window is wearing, read from the WINDOW rather than remembered.</summary>
        public static Menu? Of(Form f) { var h = GetMenu(f.Handle); return h == IntPtr.Zero ? null : new Menu(h); }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { DestroyMenu(Handle); Handle = IntPtr.Zero; }
        }

        private const uint WM_NULL = 0x0000;
        private const uint MF_STRING = 0x00000000;
        private const uint MF_POPUP = 0x00000010;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        [LibraryImport("user32.dll")] private static partial nint GetMenu(nint hWnd);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyMenu(nint hMenu);
        [LibraryImport("user32.dll")] private static partial nint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(ref Point p);
        // THE POSTED NULL a popup owes its owner, and it lives HERE because a LibraryImport is visible to the class
        // that uses it: PostMessage exists elsewhere in this file, in another class, and would not resolve from the
        // Menu class. A bool return needs the explicit marshalling, the same as the three above it.
        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool NativePostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);
        // EntryPoint is stated and the MEMBER is named around the collision, the rule this file already records for
        // ClientToScreen: a member cannot be called SetForegroundWindow here, because a script-visible one answers
        // that name, and binding the wrong thing is what made a popup throw "missing an expected entry point".
        [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool NativeSetForegroundWindow(nint hWnd);
    }
}

