namespace ScriptLangCore;

/// <summary>The Clipboard namespace: the DESKTOP's text clipboard. It is a namespace of its own rather than a Form
/// member, because the clipboard belongs to the machine and not to a window: a script copies from a console tool as
/// readily as from a form, and the handle an owner would pass to OpenClipboard is not needed for that.
///
/// The four rules a correct CF_UNICODETEXT write obeys, each of which is its own failure when missed, are the ones
/// this implementation exists to keep in ONE place:
///   * the block comes from GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT), because the clipboard takes OWNERSHIP of a
///     MOVABLE global block and frees it itself, which a process-heap pointer (Marshal.AllocHGlobal) is not;
///   * it is NOT freed once SetClipboardData has SUCCEEDED, and is freed only when it did not;
///   * the payload is NUL-terminated UTF-16, so the block is (length + 1) * 2 bytes;
///   * a READ locks the handle it is given: reading the returned HGLOBAL as if it were a pointer can answer
///     somebody else's characters while the block really holds the text.
/// Both halves also answer a FAILURE rather than throwing, because another process holding the clipboard open is
/// normal on a busy desktop, not an error in the script.
internal partial class Interpreter
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    /// <summary>The handlers registered with <c>Clipboard.Watch</c>, keyed by the WINDOW the notification goes to.
    /// The notification is per window (AddClipboardFormatListener takes a handle), which is why this is the key, and it
    /// is keyed by handle rather than by Control so a window the engine does not wrap could be watched too.</summary>
    private static readonly Dictionary<IntPtr, object> ClipboardWatchers = [];

    /// <summary>The handler watching a window, or null. Read by the window procedure, which is why it is a method
    /// rather than a field lookup: every read takes the same lock the two writers take.</summary>
    internal static object? ClipboardWatchHandler(IntPtr hwnd)
    {
        lock (ClipboardWatchers) return ClipboardWatchers.TryGetValue(hwnd, out var handler) ? handler : null;
    }

    /// <summary>Removes the registration for a window, at the same time the listener goes. Called by
    /// <c>Clipboard.Unwatch</c> and by the window's own destruction, so a dead handle never keeps a script closure.</summary>
    internal static void DropClipboardWatcher(IntPtr hwnd)
    {
        lock (ClipboardWatchers) ClipboardWatchers.Remove(hwnd);
        RemoveClipboardFormatListener(hwnd);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint hMem);

    /// <summary>Registers the Clipboard namespace. The five user32 calls it uses are declared beside the WinApi
    /// namespace's own clipboard members (Ui.cs), which is the same partial class, so there is one declaration of
    /// each rather than a second copy here.</summary>
    private void RegisterClipboardFunctions()
    {
        var clipNs = new Namespace(_comparer);

        clipNs.AddMethod("SetText", args =>
        {
            if (args.Length < 1 || args[0] is not string text)
                throw new ScriptException("Clipboard.SetText expects the text to put on the clipboard.");
            return SetClipboardText(text);
        });

        clipNs.AddMethod("GetText", args => GetClipboardText());

        clipNs.AddMethod("Clear", args =>
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try { return EmptyClipboard(); }
            finally { CloseClipboard(); }
        });

        // ── the doors for ANOTHER format, which is what a script assembles itself ──
        // The text pair above is the whole recipe for CF_UNICODETEXT; these four are the same five user32 calls the
        // WinApi namespace used to expose, moved here so the clipboard has ONE home. `Open` holds the clipboard for
        // the calls that follow and must be paired with `Close` in a finally, which is why they are spelled as a
        // pair rather than hidden: a format the engine does not know takes blocks the script owns and frees.
        clipNs.AddMethod("Open", args =>
        {
            var owner = args.Length > 0 ? ToNInt(args[0]) : IntPtr.Zero;
            return OpenClipboard(owner);
        });

        clipNs.AddMethod("Close", args => CloseClipboard());
        clipNs.AddMethod("Empty", args => EmptyClipboard());

        clipNs.AddMethod("SetData", args =>
        {
            if (args.Length < 2) throw new ScriptException("Clipboard.SetData expects (format, hMem).");
            // The clipboard takes OWNERSHIP of the block: the caller must not free one it handed over.
            return SetClipboardData(Convert.ToUInt32(args[0]), ToNInt(args[1])).ToInt64();
        });

        clipNs.AddMethod("GetData", args =>
        {
            if (args.Length < 1) throw new ScriptException("Clipboard.GetData expects the format.");
            // The handle is the CLIPBOARD's: lock it to read it, and never free it.
            return GetClipboardData(Convert.ToUInt32(args[0])).ToInt64();
        });

        // ── WATCHING for changes: the notification Windows already sends, instead of a timer comparing text ──
        // A window registered with AddClipboardFormatListener receives WM_CLIPBOARDUPDATE, which the engine's window
        // procedure hands to the handler registered here. Before this, the whole recipe was a script's job: a hidden
        // form, SetWindowHandler, the raw message number and the two user32 calls (it is still readable in
        // docs/language/index.html's Clipboard entry, which is what this member retires).
        clipNs.AddMethod("Watch", args =>
        {
            if (args.Length < 2)
                throw new ScriptException("Clipboard.Watch expects a control to be told on and the function to run.");
            if (args[0] is not Control control)
                throw new ScriptException($"Clipboard.Watch expects a control as its first argument, not '{args[0]?.GetType().Name ?? "null"}'.");
            if (!IsScriptCallable(args[1]))
                throw new ScriptException("Clipboard.Watch expects a function to run when the clipboard changes.");
            control.CreateControl();                 // the listener needs a WINDOW, not just an object
            if (control.Handle == IntPtr.Zero) return false;
            lock (ClipboardWatchers) ClipboardWatchers[control.Handle] = args[1]!;
            if (AddClipboardFormatListener(control.Handle)) return true;
            lock (ClipboardWatchers) ClipboardWatchers.Remove(control.Handle);
            return false;
        });

        clipNs.AddMethod("Unwatch", args =>
        {
            if (args.Length < 1 || args[0] is not Control control)
                throw new ScriptException("Clipboard.Unwatch expects the control that was watched.");
            if (control.Handle == IntPtr.Zero) return false;
            var known = ClipboardWatchHandler(control.Handle) != null;
            DropClipboardWatcher(control.Handle);
            return known;
        });

        CurrentFrame.Scopes.Define("Clipboard", clipNs);
    }

    /// <summary>Puts TEXT on the clipboard, answering whether it landed. An empty string is legal and means the
    /// clipboard holds an empty string rather than nothing.</summary>
    private static bool SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        var hMem = IntPtr.Zero;
        try
        {
            EmptyClipboard();
            hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)((text.Length + 1) * (long)sizeof(char)));
            if (hMem == IntPtr.Zero) return false;

            var target = GlobalLock(hMem);
            if (target == IntPtr.Zero) return false;
            try { Marshal.Copy(text.ToCharArray(), 0, target, text.Length); }
            finally { GlobalUnlock(hMem); }

            if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
            hMem = IntPtr.Zero;   // the clipboard owns the block from here, so the `finally` must not free it
            return true;
        }
        finally
        {
            if (hMem != IntPtr.Zero) { GlobalFree(hMem); }   // only ever the failure path
            CloseClipboard();
        }
    }

    /// <summary>Reads the clipboard's text, or "" when there is none: an empty clipboard and one another process
    /// holds both answer an empty string to a reader. The block the clipboard hands out is NOT freed here, since
    /// the clipboard owns it.</summary>
    private static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return "";
        try
        {
            var hMem = GetClipboardData(CF_UNICODETEXT);
            if (hMem == IntPtr.Zero) return "";

            var source = GlobalLock(hMem);
            if (source == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(source) ?? ""; }
            finally { GlobalUnlock(hMem); }
        }
        finally { CloseClipboard(); }
    }
}
