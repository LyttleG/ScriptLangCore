namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

/// <summary>The one helper behind the `configure` callback every UI factory takes, declared as an EXTENSION so all three
/// hosts reach it by the same one-word spelling: `Form`, `MdiChild` and `MdiForm` are siblings here, and a static member
/// of one (or of the enclosing `Interpreter`) is not in scope unqualified from a nested type. Being a namespace-level
/// member also means the call site reads as what it does.
///
/// A factory hands it the control it just adopted, so the callback runs AFTER the control is parented and has its
/// window (anything needing a real window works) and BEFORE the caller sees the value. The callback's parameter is the
/// CONCRETE type, which is the point of the pattern: a script reaches `.Items`, `.Checked`, `.Text` or a handler in the
/// statement that creates the thing, with no cast. A null callback is the ordinary call, so every script written before
/// the callback existed is unchanged.</summary>
internal static class UiFactory
{
    internal static T WithInit<T>(this T subject, Action<T>? configure) where T : class
    {
        configure?.Invoke(subject);
        return subject;
    }
}

internal partial class Interpreter
{
    // ============================================================================
    // 11. INTERPRETER - PARTIE 3 : CLASSES UI (Control, Form, NativeApp, EventArgs...)
    // ============================================================================

    // Délégués pour callbacks
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint CallbackDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private static SubclassProcDelegate? _childProcDelegate;

    // Callback management for native function pointers 
    private readonly Dictionary<ScriptFunction, GCHandle> _callbackHandles = [];

    private static int Loword(IntPtr l) => (int)(l.ToInt64() & 0xFFFF);

    // Mouse coordinates are SIGNED, and Loword/Hiword above are not: a captured drag that moves left of or above the
    // window's client origin produces a negative coordinate, which the masks turn into ~65534. Win32's own contract
    // for a mouse message is GET_X_LPARAM/GET_Y_LPARAM, which sign-extend. Measured the cost before this existed: a
    // 6-pixel drag to the LEFT made a designer control jump to the surface's full width, because the delta arrived
    // as 65530 instead of -6. Loword/Hiword stay for a command id and a size, which really are unsigned.
    // MouseX/MouseY are SIGNED, not a duplicate of Loword/Hiword: a mouse coordinate outside the window is
    // negative and must stay negative, while a message's low word is read unsigned. Measured, the two pairs differ
    // exactly there, so neither may replace the other (a review proposed the merge; this is why not).
    private static int MouseX(IntPtr l) => (short)(l.ToInt64() & 0xFFFF);
    private static int MouseY(IntPtr l) => (short)((l.ToInt64() >> 16) & 0xFFFF);
    private static int Hiword(IntPtr l) => (int)((l.ToInt64() >> 16) & 0xFFFF);

    // ----------------------------- Constantes Win32 --------------------------
    #region Constantes pour les styles de fenêtres

    // =============================================
    // Win32 Window Management and Positioning
    // =============================================
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    // The hWndInsertAfter value that means "below every sibling". Its twin, HWND_TOP, is 0 and every call here
    // already passes IntPtr.Zero for it.
    private static readonly IntPtr HWND_BOTTOM = 1;

    // =============================================
    // Win32 Window Styles
    // =============================================
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int GW_CHILD = 5;                 // GetWindow: the first child of a window (a toolbar's own)
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const int SM_CXVSCROLL = 2;   // the width of a vertical scrollbar, at a given DPI
    private const int SM_CYHSCROLL = 3;   // the height of a horizontal one

    // A RichEdit's font goes through a char format (see Control.ApplyRichEditFont), because WM_SETFONT resets every
    // character format it has, colours included (measured).
    private const uint EM_SETCHARFORMAT = 0x0444;
    private const int SCF_ALL = 4;
    private const int SCF_SELECTION = 1;
    private const uint CFM_COLOR = 0x40000000;
    private const uint CFM_BOLD = 0x00000001;
    private const uint CFM_FACE = 0x20000000;
    private const uint CFM_SIZE = 0x80000000;
    private const int CFE_BOLD = 0x00000001;

    // UNITS, which are definitions rather than machine metrics: a point is 1/72 inch and a twip is 1/20 point, so
    // an inch is 1440 twips. The only MEASURED value in a conversion is the pixels-per-inch, and it is asked for.
    private const int PointsPerInch = 72;
    private const int TwipsPerInch = 1440;
    private const int LOGPIXELSY = 90;      // GetDeviceCaps: pixels per logical inch along the Y axis

    // The engine's default font: its height in pixels at the DESIGN DPI, which CreateDpiFont scales and which a
    // RichEdit's default point size is derived from (8.25 pt), so the two cannot disagree.
    private const int DefaultFontPixels = 11;
    private const int DesignDpi = 96;

    /// <summary>A theme switch or a DPI change, which is the one message that makes a THEMED control re-read the
    /// parts it draws (a scrollbar's arrows and thumb, a button's face): those come from a theme opened at the DPI
    /// the control was created at, and Windows itself sends this when the theme changes.</summary>
    private const uint WM_THEMECHANGED = 0x031A;

    /// <summary>What a CHILD window receives once its parent's DPI changed: the documented notification, and the
    /// only reliable moment for a control to fix its own non-client area and theme. The parent's own
    /// WM_DPICHANGED handler scales every child's SIZE while the child's DPI may still be the OLD one, so the
    /// non-client area (the scrollbars) is recomputed at the old metric and stays wrong after that: measured on a
    /// report of "the scrollbars are 1.5x the size on the 100% screen (144 / 96, exactly the DPI ratio)", and the
    /// timing is why a programmatic move can look right while a DRAG (a modal move loop) does not.</summary>
    private const uint WM_DPICHANGED_AFTERPARENT = 0x02E3;

    /// <summary>The frame re-apply and the theme reload a window needs after its DPI changed, which is the
    /// DRAWING half of a DPI change: the SIZE is scaled by the caller, and this is what makes the window recompute
    /// its non-client area and re-read its theme parts. Both halves are needed for a control whose non-client area
    /// is a scrollbar (a text box, a list, a tree): measured across a real move between two monitors (144 -> 96 ->
    /// 144), the scrollbar's own WIDTH already followed the monitor (26 px -> 17 -> 26, exactly what
    /// GetSystemMetricsForDpi answers for those DPIs), so the SIZE was never what a user sees wrong, while the
    /// theme parts stay cached at the old DPI until something tells the control to re-read them. MdiForm's handler
    /// already did this for its FRAME ("Rescale non-client area without fighting over size") and neither handler
    /// did it for the frame's CONTROLS.</summary>
    private static void RefreshFrameAndTheme(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SendMessageW(hwnd, WM_THEMECHANGED, 0, 0);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        // SWP_FRAMECHANGED recalcs the frame and repaints NOTHING (the same rule ApplyWindowStyle states for a
        // runtime style change), and a DPI change alters the frame MORE than a style change does: the bars in
        // it are measured for another DPI. Without this the window keeps the pixels of the bar it had, and the
        // report that produced it is exact about the shape: a WS_VSCROLL button moved to the 96 DPI monitor
        // showed its scrollbar "cut on the right edge", because the frame had been re-laid out to the new
        // metric while the wider bar's pixels were still what the screen held. Measured on the same move with a
        // screen read (the only way to see a non-client bar): the frame's own geometry follows either way, so
        // this is the DRAWING half, which is what the function's name promises. RDW_ALLCHILDREN covers a child
        // whose frame is what changed with its own parent (an EDIT inside a list view); no RDW_UPDATENOW, for
        // the reason stated in ApplyWindowStyle: the engine does not force synchronous paints.
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
    }

    /// <summary>Whether this window's style carries a non-client BAR (a vertical or horizontal scrollbar).
    /// Such a window is left with NO theme sub-app on purpose: with one, Windows draws the CLASSIC scrollbar
    /// (arrow boxes, 3D separators) instead of the flat, arrowless bar the window has without one, and the two
    /// swap under the user's eyes when the engine themes the window at a DPI change. MEASURED on a WS_VSCROLL
    /// button read from the screen dc: no sub-app gives `F25 S26 F248` (one solid band, no arrow runs), a
    /// sub-app gives `F9 G6 F8 S2 F24 S2 F232 G6 F8 S2`, and the swap happened about a second after the window
    /// appeared ("fine at the beginning, seconds later it turns redrawn and lost"). THE FLAT BAR IS WHAT THIS
    /// ENGINE KEEPS, asked for directly on 2026-09-26, accepting that its parts do not re-read the metric on a
    /// monitor move (the classic bar's do); do not "fix" that by handing a sub-app back.</summary>
    internal static bool HasNonClientBar(nint hwnd)
        => hwnd != IntPtr.Zero && ((uint)GetWindowLongPtrW(hwnd, GWL_STYLE) & (WS_VSCROLL | WS_HSCROLL)) != 0;

    /// <summary>A runtime window-style change: read the style word, OR the additions in, AND the removals out,
    /// write it back, and re-apply the frame so the change is honoured (SWP_FRAMECHANGED). ONE implementation,
    /// TWO spellings: the WinApi functions (ModStyle / StyleAdd / StyleRemove, which take an HWND) and the
    /// instance methods on Control (which use the control's own handle) both land here, so the two cannot
    /// diverge. `bEx` picks the EXTENDED word (GWL_EXSTYLE: WS_EX_*) instead of GWL_STYLE (WS_*).</summary>
    internal static void ApplyWindowStyle(nint hwnd, long dwAdd, long dwRemove, bool bEx)
    {
        if (hwnd == IntPtr.Zero) return;
        var index = bEx ? GWL_EXSTYLE : GWL_STYLE;
        var style = (uint)GetWindowLongPtrW(hwnd, index);
        var newStyle = (style | (uint)dwAdd) & ~(uint)dwRemove;
        SetWindowLongPtrW(hwnd, index, (nint)newStyle);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        // SWP_FRAMECHANGED recalcs the frame; it does not repaint anything, and InvalidateRect alone covers
        // the CLIENT area while a style change alters the FRAME as well (a border, a client edge, a caption).
        // No RDW_UPDATENOW: forcing a synchronous paint of the window and every child is the flicker this
        // engine deliberately avoids (see RepaintSurface).
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);

        // A style bit on its own has nothing to SEE. A control that has just become a TAB STOP is therefore given the
        // focus, cue and all, which is the visible half of what WS_TABSTOP means, and it is the same door the tab
        // navigation uses so the two cannot disagree about what focusing a control looks like.
        if (!bEx && ((uint)dwAdd & WS_TABSTOP) != 0) { FocusWithCue(hwnd); }
    }

    /// <summary>The style word a runtime change reads and writes: GWL_STYLE, or GWL_EXSTYLE when `bEx`.
    /// Read here so the panel that shows the flags and the call that sets them cannot disagree.</summary>
    internal static long ReadWindowStyle(nint hwnd, bool bEx)
        => hwnd == IntPtr.Zero ? 0L : (long)(uint)GetWindowLongPtrW(hwnd, bEx ? GWL_EXSTYLE : GWL_STYLE);

    /// <summary>A window&#39;s own rectangle, or an empty one when there is no window. One implementation, because the
    /// Form (which is a Control) and the prompt (which is NOT, so it mirrors its doors) both answer with it.</summary>
    internal static Rect WindowRectOf(nint hwnd)
    {
        var rect = new Rect();
        if (hwnd != IntPtr.Zero) { GetWindowRect(hwnd, out rect); }
        return rect;
    }

    /// <summary>A window&#39;s CLIENT rectangle, or an empty one when there is no window: the area a control can
    /// paint in, which is the window&#39;s own rect minus its frame and its scrollbars. It sits beside WindowRectOf
    /// for the same reason that one exists, so `ctrl.ClientRect` and `ctrl.WindowRect` are one shape.</summary>
    internal static Rect ClientRectOf(nint hwnd)
    {
        var rect = new Rect();
        if (hwnd != IntPtr.Zero) { GetClientRect(hwnd, out rect); }
        return rect;
    }

    /// <summary>RGB (0xRRGGBB as a script writes it) to the COLORREF GDI wants (swap R and B). ONE implementation, on
    /// the outer class so that both users reach it: a control&#39;s colours, and the glyph renderer that used to keep its
    /// own copy of the expression because this was private to Control.</summary>
    internal static uint ToColorRef(uint rgb)
    {
        // COLORREF is 0x00BBGGRR, so R and B trade places.
        return ((rgb & 0x00FF0000) >> 16) | (rgb & 0x0000FF00) | ((rgb & 0x000000FF) << 16);
    }

    private const uint GW_OWNER = 4;

    /// <summary>The colours Windows draws for the CAPTION: its face and its text. They take the palette while the
    /// host asked for Custom and go back to the system's own when it did not, which is what the
    /// DWMWA_COLOR_DEFAULT sentinel is for: a window that leaves Custom hands them back rather than keeping the
    /// last palette it was given.
    ///
    /// Without this a Custom form's caption is Windows' own DARK or LIGHT caption, which follows the LEG the
    /// palette implies and is not the palette itself. The leg still travels separately
    /// (DWMWA_USE_IMMERSIVE_DARK_MODE), because it is what the close and minimize GLYPHS contrast against.
    ///
    /// ONE helper for the form and the MDI frame, so the two cannot drift into different rules.
    ///
    /// NOT SET HERE, and measured rather than forgotten: DWMWA_BORDER_COLOR (34) is the attribute for the frame's
    /// 1 px outline, and on this box it changes nothing. assets/language/probes/probe-caption.slc walks inward from the window's
    /// left edge and answers 0x000000 then 0x2F2F2F, IDENTICALLY for a Custom palette that asks for 0x00C0FF and
    /// for ThemeMode.Dark, while the caption beside it follows the palette exactly. So the outline stays Windows'
    /// own, and the attribute is left out rather than shipped unverifiable.</summary>
    private static void ApplyNonClientColors(nint hwnd, ThemeState theme)
    {
        var custom = theme.IsCustom();
        var caption = custom ? (int)ToColorRef(theme.Background()) : DWMWA_COLOR_DEFAULT;
        var text = custom ? (int)ToColorRef(theme.Text()) : DWMWA_COLOR_DEFAULT;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private const uint WM_CHANGEUISTATE = 0x0127;
    private const int UIS_CLEAR = 0x0002;
    private const int UISF_HIDEFOCUS = 0x0001;

    /// <summary>Give a window the FOCUS and make the focus cue VISIBLE. The second half is not decoration: Windows
    /// hides the cue after mouse input (the window&#39;s UI state carries UISF_HIDEFOCUS), so a control can hold the
    /// focus and draw nothing at all, which is what &quot;a style bit with no visual effect&quot; turns out to be.
    /// WM_CHANGEUISTATE clears that flag for the window being focused, which is the documented way back.</summary>
    internal static void FocusWithCue(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) { return; }
        SetFocus(hwnd);
        // MAKELONG(UIS_CLEAR, UISF_HIDEFOCUS): the low word is the ACTION, the high word the flag it acts on.
        SendMessageW(hwnd, WM_CHANGEUISTATE, (nuint)((long)UIS_CLEAR | ((long)UISF_HIDEFOCUS << 16)), IntPtr.Zero);
    }
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_SYSMENU = 0x00080000;   // the system menu and the close box in it

    // =============================================
    // Win32 Window Messages
    // =============================================
    private const uint WM_SETREDRAW = 0x000B;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const int SB_HORZ = 0;
    private const int SB_VERT = 1;
    private const uint SIF_RANGE = 0x0001;
    private const uint SIF_PAGE = 0x0002;
    private const uint SIF_POS = 0x0004;
    private const uint DCX_WINDOW = 0x00000001;
    private const uint DCX_CACHE = 0x00000002;
    private const uint DCX_INTERSECTRGN = 0x00000080;

    /// <summary>What a scrollbar's own state IS, read from the control rather than remembered: the thumb's position
    /// and length both come out of this, so the engine paints the bar the control reports rather than a guess.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScrollInfo
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    // The Alt variants. Windows sends THESE, not the plain pair, for a key pressed while Alt is held,
    // so a handler never saw Alt+C: the letters arrive as WM_SYSKEYDOWN/UP and the arms did not match.
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint BS_OWNERDRAW = 0x0000000B;
    private const uint BS_DEFPUSHBUTTON = 0x00000001;   // the type bits a button reads to draw itself as the default
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_CREATE = 0x0001;
    private const uint WM_NCCREATE = 0x0081;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_NOTIFY = 0x004E;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_HSCROLL = 0x0114;
    private const uint WM_VSCROLL = 0x0115;
    private const uint WM_CTLCOLORMSGBOX = 0x0132;
    private const uint WM_CTLCOLOREDIT = 0x0133;
    private const uint WM_CTLCOLORLISTBOX = 0x0134;
    private const uint WM_CTLCOLORBTN = 0x0135;
    private const uint WM_CTLCOLORDLG = 0x0136;
    private const uint WM_CTLCOLORSCROLLBAR = 0x0137;
    private const uint WM_CTLCOLORSTATIC = 0x0138;
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_TIMER = 0x0113;

    // =============================================
    // Win32 MDI (Multiple Document Interface)
    // =============================================
    private const uint WM_MDICREATE = 0x0220;
    private const uint WM_MDIDESTROY = 0x0221;
    private const uint WM_MDIMAXIMIZE = 0x0225;
    private const uint WM_MDITILE = 0x0226;
    private const uint WM_MDICASCADE = 0x0227;
    private const uint WM_MDIICONARRANGE = 0x0228;
    private const uint WM_MDIGETACTIVE = 0x0229;

    private const int MDITILE_VERTICAL = 0x0000;
    private const int MDITILE_HORIZONTAL = 0x0001;

    // =============================================
    // Win32 Window Styles
    // =============================================
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint WS_HSCROLL = 0x00100000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_EX_CLIENTEDGE = 0x00000200;
    private const uint WS_EX_COMPOSITED = 0x02000000;
    // The drop-target bit shell32's DragAcceptFiles sets and clears, which is why a form can read its own
    // AcceptDroppedFiles back off the window instead of remembering it.
    private const uint WS_EX_ACCEPTFILES = 0x00000010;

    // =============================================
    // Win32 Control Styles
    // =============================================
    private const uint ES_AUTOHSCROLL = 0x0080;
    private const uint ES_LEFT = 0x0000;
    private const uint ES_MULTILINE = 0x0004;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint EM_SETSEL = 0x00B1;
    private const uint EM_REPLACESEL = 0x00C2;
    private const uint EM_SCROLLCARET = 0x00B7;
    private const uint EM_LINESCROLL = 0x00B6;
    // A THIN ENGINE BAR (see Control.ScrollBar): the native bar is removed and the engine paints and drives a narrow
    // strip of its own, because Windows offers no supported way to recolour a native one and overpainting it fights the
    // class's own hot state (measured: the two paints alternate, which is the flicker).
    private const uint EM_GETRECT = 0x00B2;
    private const uint EM_SETRECT = 0x00B3;
    private const uint EM_GETLINECOUNT = 0x00BA;
    private const uint EM_GETFIRSTVISIBLELINE = 0x00CE;
    // THE TEXT SURFACE (see the members further down): a caret range as two 32-bit fields, a line's own character
    // index, a character's client position, and the two format messages. `EM_GETSEL` is NOT used for the range: it
    // packs the two positions into one 32-bit answer with 16 bits each, so it wraps past 65,536 characters, which is
    // the size a log reaches (measured: a 4,000-line log coloured its runs wrongly from about line 1,300).
    private const uint EM_EXGETSEL = 0x0434;
    private const uint EM_GETSELTEXT = 0x043E;
    private const uint EM_LINEINDEX = 0x00BB;
    private const uint EM_EXLINEFROMCHAR = 0x0436;   // the LINE a character index sits on (WM_USER + 54)
    private const uint EM_POSFROMCHAR = 0x00D6;
    private const uint EM_GETCHARFORMAT = 0x043A;
    private const uint EM_SETPARAFORMAT = 0x0447;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_LINELENGTH = 0x00C1;
    private const uint EM_SETREADONLY = 0x00CF;
    private const uint EM_SETBKGNDCOLOR = 0x0443;
    /// <summary>WM_CLIPBOARDUPDATE: what a window registered with AddClipboardFormatListener receives.</summary>
    private const uint WM_CLIPBOARDUPDATE = 0x031D;

    // EM_GETPARAFORMAT is WM_USER + 75 (0x043D), NOT 0x043F: that one is EM_SETTARGETDEVICE, and asking the wrong
    // message answers 0 with every field left at zero, which read back as "LineSpacing stayed 0" after a successful set.
    private const uint EM_GETPARAFORMAT = 0x043D;
    /// <summary>The CHARFORMATW size, and where its height sits: 4 cbSize, 4 dwMask, 4 dwEffects, 4 yHeight.</summary>
    private const int CharFormatSize = 92;
    private const int CharFormatYHeight = 12;
    /// <summary>The PARAFORMAT2 size, and where its line spacing and rule sit (after 32 tab stops).</summary>
    private const int ParaFormat2Size = 188;
    private const int ParaFormatLineSpacing = 164;
    private const int ParaFormatSpacingRule = 168;
    /// <summary>CFM_SIZE alone, as a signed int because a literal above 0x7FFFFFFF does not compile: setting the height
    /// must NOT restate the face, or a script that changed the font would get the engine's face back with it.</summary>
    private const int CfmSize = unchecked((int)0x80000000);
    private const uint PfmLineSpacing = 0x00000100;
    /// <summary>SCF_ALL formats the whole text (SCF_SELECTION is the colour core's own, one property down).</summary>
    private const int ScfAll = 4;
    /// <summary>The thin bar's width as a fraction of the metric a native bar would take: measured against a 17 px
    /// native bar at 96 DPI, 45% is 8 px, and at 144 DPI it is 11 against 26.</summary>
    private const double ThinBarRatio = 0.45;
    /// <summary>How many lines one wheel notch moves when the engine owns the scroll, which is Windows' own default
    /// (SPI_GETWHEELSCROLLLINES reads 3 here) and what a RichEdit does with a native bar.</summary>
    private const int ThinBarWheelLines = 3;
    private const uint EM_SETLIMITTEXT = 0x00C5;
    private const uint BS_PUSHBUTTON = 0x00000000;
    private const uint BS_AUTOCHECKBOX = 0x0003;
    private const uint BS_AUTORADIOBUTTON = 0x0009;
    private const uint BS_CHECKBOX = 0x0002;
    private const uint BS_RADIOBUTTON = 0x0004;
    private const uint BS_TYPEMASK = 0x000F;
    private const uint LBS_NOTIFY = 0x0001;
    // 0x1000 is the real LBS_DISABLENOSCROLL; 0x0008 is LBS_MULTIPLESEL, using 0x0008
    // here made every ListBox multi-select (Ctrl/Shift could select several rows).
    private const uint LBS_DISABLENOSCROLL = 0x1000;
    private const uint CBS_DROPDOWNLIST = 0x0003;
    private const uint TBS_HORZ = 0x0000;
    private const uint SS_LEFT = 0x00000000;
    private const uint LVS_REPORT = 0x0001;
    private const uint LVS_SINGLESEL = 0x0004;
    private const uint LVS_SHOWSELALWAYS = 0x0008;
    private const uint TVS_HASLINES = 0x0002;
    private const uint TVS_HASBUTTONS = 0x0001;
    private const uint TVS_LINESATROOT = 0x0004;
    private const uint TCS_FIXEDWIDTH = 0x0400;
    private const uint CCS_TOP = 0x00000001;
    private const uint CCS_NORESIZE = 0x00000004;      // a toolbar that honours the rect it was given
    private const uint CCS_NOPARENTALIGN = 0x00000008;
    private const uint CCS_NODIVIDER = 0x0040;
    private const uint TBSTYLE_FLAT = 0x0800;
    private const uint TBSTYLE_TOOLTIPS = 0x0100;
    private const uint BTNS_BUTTON = 0x00;

    // =============================================
    // Win32 Control Messages
    // =============================================
    // TrackBar (msctls_trackbar32): empirically on this OS the 0x408 message stores
    // its lParam as the MAXIMUM VALUE (plain), see TrackBar.ApplyNativeRange.
    // TBM_GETPOS=0x0400 · TBM_GETRANGEMIN=0x0401 · TBM_GETRANGEMAX=0x0402 · TBM_SETPOS=0x0405
    private const uint TBM_GETPOS = 0x0400;
    private const uint TBM_SETRANGE = 0x0408;
    private const uint TBM_SETPOS = 0x0405;
    private const uint BM_SETCHECK = 0x00F1;
    private const uint BST_CHECKED = 1;
    private const uint BST_UNCHECKED = 0;
    private const uint BN_CLICKED = 0;
    private const uint LB_ADDSTRING = 0x0180;
    private const uint LB_DELETESTRING = 0x0182;
    private const uint LB_RESETCONTENT = 0x0184;
    private const uint LB_GETCURSEL = 0x0188;
    private const uint LB_SETCURSEL = 0x0186;
    private const uint LB_GETTEXTLEN = 0x018A;
    private const uint LB_GETTEXT = 0x0189;
    private const uint LB_GETCOUNT = 0x018B;
    private const uint LBN_SELCHANGE = 1;
    // An EDIT reports its text changes to its parent, a COMBO box its selection and its edit text.
    private const uint EN_CHANGE = 0x0300;
    private const uint LBN_DBLCLK = 2;
    private const uint CB_ADDSTRING = 0x0143;
    private const uint CB_DELETESTRING = 0x0144;
    private const uint CB_RESETCONTENT = 0x014B;
    private const uint CB_GETCURSEL = 0x0147;
    private const uint CB_SETCURSEL = 0x014E;
    private const uint CB_GETLBTEXTLEN = 0x0149;
    private const uint CB_GETLBTEXT = 0x0148;
    private const uint CB_GETCOUNT = 0x0146;
    private const uint LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036;
    private const uint LVM_GETNEXTITEM = 0x100C;
    private const uint LVM_SETITEMSTATE = 0x102B;
    private const uint LVM_DELETEALLITEMS = 0x1009;
    private const uint LVM_SETBKCOLOR = 0x1001;
    private const uint LVM_SETTEXTCOLOR = 0x1024;
    private const uint LVM_SETCOLUMNWIDTH = 0x101E;
    private const uint LVM_GETCOLUMNWIDTH = 0x101D;
    private const uint LVM_GETHEADER = 0x101F;
    private const uint HDM_GETITEMCOUNT = 0x1200;
    private const int LVSCW_AUTOSIZE_USEHEADER = -2;
    private const uint LVM_GETITEMCOUNT = 0x1004;
    private const uint LVM_DELETEITEM = 0x1008;
    private const uint LVM_SETTEXTBKCOLOR = 0x1026;
    private const uint LVM_GETSUBITEMRECT = 0x1038;
    private const int LVIR_BOUNDS = 0x0000;
    private const uint LVS_EX_GRIDLINES = 0x00000001;
    private const uint LVS_EX_FULLROWSELECT = 0x00000020;
    private const uint LVS_EX_DOUBLEBUFFER = 0x00010000;
    private const uint LVNI_SELECTED = 0x0002;
    private const uint LVIS_SELECTED = 0x0002;
    private const uint TVN_SELCHANGED = 0xFFFFFE6E; // (unsigned)-402
    private const uint NM_DBLCLK = 0xFFFFFFFD; // (unsigned)-3
    private const uint LVN_ITEMCHANGED = 0xFFFFFF9B; // LVN_FIRST - 1, LVN_FIRST being (0U - 100U)
    // A SysTreeView32 draws its OWN items and never sends WM_CTLCOLOR*, so its colours are these three
    // messages, the way a SysListView32 takes the LVM_SET* ones.
    private const uint TVM_SETBKCOLOR = 0x111D;
    private const uint TVM_SETTEXTCOLOR = 0x111E;
    private const uint TVM_SETLINECOLOR = 0x1128;
    private const uint TVM_GETNEXTITEM = 0x110A;
    private const uint TVM_SETITEMW = 0x113F;
    private const uint TVM_GETITEMW = 0x113E;
    private const uint TVE_COLLAPSE = 0x0001;
    private const uint TVGN_CARET = 0x0009;
    // The WALK a TreeView's contents needs: the first ROOT, the next SIBLING, and the first CHILD of an item.
    private const uint TVGN_ROOT = 0x0000;
    private const uint TVGN_NEXT = 0x0001;
    private const uint TVGN_CHILD = 0x0004;
    private const uint DTS_SHORTDATEFORMAT = 0x0000;
    private const uint DTM_GETSYSTEMTIME = 0x1001;
    private const uint DTM_SETSYSTEMTIME = 0x1002;
    private const uint SB_SETTEXTA = 0x0401;
    private const uint SBARS_SIZEGRIP = 0x0100;
    // A list box and a combo box otherwise snap their HEIGHT to a whole number of items (or to the font), so a
    // control placed at a chosen size came out a different one: measured, a ListBox asked for 100x80 was 100x64,
    // and a ComboBox asked for 100x24 was 100x28. These two styles are what make the requested height stick.
    private const uint LBS_NOINTEGRALHEIGHT = 0x0100;
    private const uint CBS_NOINTEGRALHEIGHT = 0x0400;
    private const uint SB_SETPARTS = 0x0404;
    // SB_SETBKCOLOR is CCM_SETBKCOLOR (CCM_FIRST + 1), NOT WM_USER + 3: the engine sent 0x0403 for years, which
    // is SB_GETTEXTLENGTHA, so the background colour was never set and the status bar kept painting its own.
    private const uint SB_SETBKCOLOR = 0x2001;
    private const uint TCM_GETITEMCOUNT = 0x1304;
    private const uint TCM_GETCURSEL = 0x130B;
    private const uint TB_BUTTONSTRUCTSIZE = 0x041E;
    private const uint TB_INSERTBUTTONW = 0x0443;
    private const uint HKM_GETHOTKEY = 0x0402;

    // =============================================
    // Win32 Window Class Styles
    // =============================================
    private const int CS_HREDRAW = 0x0001;
    private const int CS_VREDRAW = 0x0002;
    private const int CS_DBLCLKS = 0x0008;

    // =============================================
    // Win32 System Constants
    // =============================================
    private const int GWLP_USERDATA = -21;
    private const int GWLP_WNDPROC = -4;
    private const int DEFAULT_GUI_FONT = 17;
    private const int IDC_ARROW = 32512;
    private const int IDC_IBEAM = 32513;   // the text cursor a RichEdit sets over its whole client
    private const int IDC_HAND = 32649;    // over the THUMB: it is the part you can actually grab
    private const uint WM_SETCURSOR = 0x0020;
    private const int COLOR_WINDOW = 5;
    private const int BLACK_BRUSH = 4;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    // The CAPTION's own colours, Windows 11 (build 22000) and later: the caption and its text, both taking a
    // COLORREF. Windows draws the caption, so an attribute is the only way a script's colour can reach it, and an
    // older Windows refuses these and keeps its own caption rather than failing, which is why there is no version
    // test at the call.
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    // DWMWA_COLOR_DEFAULT is the sentinel that puts a part back to the colour the SYSTEM chose, which is what a
    // form leaving Custom needs: a reset, not "no colour".
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
    private const int TRANSPARENT = 1;

    // =============================================
    // Win32 Redraw Window Flags
    // =============================================
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_ERASE = 0x0004;

    // =============================================
    // Win32 Custom Message Constants
    // =============================================
    private const uint WM_APP_TAB_SETCURSEL = 0x8000 + 200;

    // =============================================
    // Win32 Control States
    // =============================================
    private const uint TBSTATE_ENABLED = 0x04;

    // =============================================
    // Win32 Common Controls - General
    // =============================================
    private const uint ICC_LISTVIEW_CLASSES = 0x00000001;
    private const uint ICC_TREEVIEW_CLASSES = 0x00000002;
    private const uint ICC_BAR_CLASSES = 0x00000004;
    private const uint ICC_TAB_CLASSES = 0x00000008;
    private const uint ICC_UPDOWN_CLASS = 0x00000010;
    private const uint ICC_PROGRESS_CLASS = 0x00000020;
    private const uint ICC_HOTKEY_CLASS = 0x00000040;
    private const uint ICC_ANIMATE_CLASS = 0x00000080;
    private const uint ICC_WIN95_CLASSES = 0x000000FF;
    private const uint ICC_DATE_CLASSES = 0x00000100;
    private const uint ICC_USEREX_CLASSES = 0x00000200;
    private const uint ICC_COOL_CLASSES = 0x00000400;
    private const uint ICC_INTERNET_CLASSES = 0x00000800;
    private const uint ICC_PAGESCROLLER_CLASS = 0x00001000;
    private const uint ICC_NATIVEFNTCTL_CLASS = 0x00002000;
    private const uint ICC_ALL_CLASSES = ICC_LISTVIEW_CLASSES | ICC_TREEVIEW_CLASSES | ICC_BAR_CLASSES
        | ICC_TAB_CLASSES | ICC_UPDOWN_CLASS | ICC_PROGRESS_CLASS | ICC_HOTKEY_CLASS
        | ICC_ANIMATE_CLASS | ICC_WIN95_CLASSES | ICC_USEREX_CLASSES | ICC_COOL_CLASSES
        | ICC_INTERNET_CLASSES | ICC_PAGESCROLLER_CLASS | ICC_NATIVEFNTCTL_CLASS | ICC_DATE_CLASSES;

    // =============================================
    // Win32 Tab Control
    // =============================================
    private const uint TCIF_TEXT = 0x0001;
    private const uint TCM_INSERTITEMW = 0x133E;
    // The read that pairs with TCM_INSERTITEMW: TCM_FIRST (0x1300) + 60, and the tab-text reader beside AddTab uses it.
    private const uint TCM_GETITEMW = 0x133C;
    private const uint TCM_SETCURSEL = 0x130D;

    // =============================================
    // Win32 ListView
    // =============================================
    private const uint LVM_INSERTCOLUMNW = 0x1061;
    private const uint LVM_INSERTITEMW = 0x104D; // LVM_FIRST + 77
    private const uint LVM_SETITEMTEXTW = 0x1074;
    private const uint LVM_GETITEMTEXTW = 0x1073;  // LVM_FIRST + 115, the W form beside LVM_SETITEMTEXTW
    private const uint LVM_GETCOLUMNW = 0x105F;    // LVM_FIRST + 95, the W form beside LVM_SETCOLUMNW
    private const uint LVM_SETCOLUMNW = 0x1060;    // LVM_FIRST + 96, the W form beside LVM_INSERTCOLUMNW
    private const uint LVM_DELETECOLUMN = 0x101C;  // LVM_FIRST + 28
    private const int MaxCellText = 1024;          // the buffer a text READ fills, for a cell or a header
    private const uint LVIF_TEXT = 0x0001;
    private const uint LVCF_TEXT = 0x0004;
    private const uint LVCF_WIDTH = 0x0002;

    // =============================================
    // Win32 TreeView
    // =============================================
    private const uint TVIF_TEXT = 0x0001;
    private const uint TVM_INSERTITEMW = 0x1132;
    private const uint TVM_EXPAND = 0x1102;   // TV_FIRST (0x1100) + 2. NOT 0x110E: that is a different message whose
                                              // TVM_EDITLABELA (TV_FIRST + 14), whose lParam is a POINTER to a label
                                              // buffer, so comctl32 treated our item handle as an ANSI
                                              // string (comctl32!ProduceWFromA -> MultiByteToWideChar with a null
                                              // pointer) and killed the process. Measured: 0x1102 and 0x1103 survive,
                                              // 0x110E faults in every form, with an item, without one, subclassed or not.
    private const uint TVE_EXPAND = 0x0002;
    #endregion

    // ----------------------------- Structures Win32 --------------------------
    #region Structures pour les fonctions Win32
    [StructLayout(LayoutKind.Sequential)]
    private struct TBBUTTON
    {
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        public byte bReserved1;
        public byte bReserved2;
        public IntPtr dwData;
        public IntPtr iString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left, top, right, bottom;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NMHDR
    {
        public nint hwndFrom;
        public nint idFrom;     // UINT_PTR, 8 bytes on x64, 4 bytes on x86
        public int code;        // INT, notification code (can be negative like TVN_SELCHANGED = -402)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Wndclassexw
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    /// <summary>A rectangle, as a script reads it: PascalCase members, because every script name is. The LAYOUT is
    /// still the Win32 RECT (four ints in order), and the marshalling that LVM_GETSUBITEMRECT needs goes through
    /// the internal RECT above, whose lowercase members are the Win32 spelling.</summary>
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int x, y; }

    // EntryPoint is required: the member cannot be CALLED ClientToScreen because the script-visible Control
    // method owns that name, and LibraryImport binds to the member's name unless told otherwise.

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeClientToScreen(nint hWnd, ref Point lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TCITEMW
    {
        public uint mask;
        public uint dwState;
        public uint dwStateMask;
        public nint pszText;
        public int cchTextMax;
        public int iImage;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVCOLUMNW
    {
        public uint mask;
        public int fmt;
        public int cx;
        public nint pszText;
        public int cchTextMax;
        public int iSubItem;
        public int iImage;
        public int iOrder;
        public int cxMin;
        public int cxDefault;
        public int cxIdeal;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public nint pszText;
        public int cchTextMax;
        public int iImage;
        public nint lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public nint puColumns;
        public nint piColFmt;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TVITEMW
    {
        public uint mask;
        public nint hItem;
        public uint state;
        public uint stateMask;
        public nint pszText;
        public int cchTextMax;
        public int iImage;
        public int iSelectedImage;
        public int cChildren;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TVITEMEXW
    {
        public uint mask;
        public nint hItem;
        public uint state;
        public uint stateMask;
        public nint pszText;
        public int cchTextMax;
        public int iImage;
        public int iSelectedImage;
        public int cChildren;
        public nint lParam;
        public int iIntegral;
        public uint uStateEx;
        public nint hwnd;
        public int iExpandedImage;
        public int iReserved;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct TV_UNION
    {
        [FieldOffset(0)]
        public TVITEMW item;

        [FieldOffset(0)]
        public TVITEMEXW itemex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TVINSERTSTRUCTW
    {
        public nint hParent;
        public nint hInsertAfter;
        public TV_UNION union1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public Rect rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public unsafe fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    protected struct CREATESTRUCT
    {
        public IntPtr lpCreateParams;
        public IntPtr hInstance;
        public IntPtr hMenu;
        public IntPtr hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public IntPtr lpszName;
        public IntPtr lpszClass;
        public uint dwExStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CLIENTCREATESTRUCT
    {
        public IntPtr hWindowMenu;
        public uint idFirstChild;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MDICREATESTRUCT
    {
        [MarshalAs(UnmanagedType.LPTStr)] public string szClass;
        [MarshalAs(UnmanagedType.LPTStr)] public string szTitle;
        public IntPtr hOwner;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint style;
        public IntPtr lParam;
    }
    #endregion

    // ----------------------------- P/Invoke Win32 ----------------------------
    #region P/Invoke pour les fonctions Win32

    // =============================================
    // user32.dll – Window Management
    // =============================================
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    /// <summary>Whether a window is a CHILD (a descendant) of another: the question "is the keyboard focus anywhere
    /// inside this panel" needs it, and the engine had no way to ask it from a script.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    /// <summary>Registers a window to receive WM_CLIPBOARDUPDATE whenever the clipboard's content changes, which is
    /// Windows TELLING a script instead of a timer comparing strings (see Clipboard.Watch).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;          // a BOOL, not a C# bool: LibraryImport cannot marshal a bool inside a struct
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial uint GetGlyphIndicesW(nint hdc, [MarshalAs(UnmanagedType.LPWStr)] string text, int count, [Out] ushort[] indices, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateIconIndirect(ref ICONINFO piconinfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    private const uint DT_CENTER = 0x0001;
    private const uint DT_VCENTER = 0x0004;
    private const uint DT_SINGLELINE = 0x0020;
    private const uint DT_NOCLIP = 0x0100;
    private const uint GGI_MARK_NONEXISTING_GLYPHS = 0x0001;
    private const ushort GlyphMissing = 0xFFFF;

    /// <summary>Turn a character into a window icon, so a script can say `form.Icon = WinApi.IconFromGlyph("✅",
    /// 32, 0x1F7A1F)` instead of shipping an .ico. An icon is a bitmap plus a mask and no message takes text, so
    /// the glyph is RENDERED: a 32-bit top-down DIB, the character drawn into it with the first font that actually
    /// covers the codepoint (checked with `GetGlyphIndicesW`, since a font without the glyph would silently draw a
    /// box), then `CreateIconIndirect`. The glyph is drawn in ONE colour (`colorRgb`, white by default), because
    /// GDI draws monochrome and a colour emoji would come out as a box. Returns 0 when no font here has the
    /// codepoint, and 0 for a glyph that painted nothing, so a script never gets an invisible icon it believes
    /// in; a size outside 8..256 is refused by name. The icon is the caller's, and `DestroyIcon` frees it.</summary>
    internal static long IconFromGlyph(string text, int size, uint colorRgb)
    {
        if (string.IsNullOrEmpty(text))
            throw new ScriptException("IconFromGlyph: the text is empty, so there is no glyph to draw.");
        if (size < 8 || size > 256)
            throw new ScriptException($"IconFromGlyph: size must be between 8 and 256 (got {size}).");
        var hdcScreen = GetDC(IntPtr.Zero);
        var hdc = CreateCompatibleDC(hdcScreen);
        if (hdc == IntPtr.Zero)
        {
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
            throw new ScriptException("IconFromGlyph: could not create a drawing surface.");
        }
        var mask = IntPtr.Zero;
        var dib = IntPtr.Zero;
        try
        {
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size,       // top-down: the icon's rows read the way the mask expects
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0       // BI_RGB
                }
            };
            dib = CreateDIBSection(hdc, ref bmi, 0 /*DIB_RGB_COLORS*/, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new ScriptException("IconFromGlyph: could not create the icon's bitmap.");

            var previousBitmap = SelectObject(hdc, dib);
            var chosenFont = IntPtr.Zero;
            var previousFont = IntPtr.Zero;
            var indices = new ushort[1];
            var pt = size * 3 / 4;              // the point size that fills a `size`-pixel box at 96 DPI
            foreach (var face in new[] { "Segoe UI Symbol", "Segoe UI Emoji", "Segoe UI", "Arial" })
            {
                var candidate = CreateFont(-pt, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, face);
                if (candidate == IntPtr.Zero) continue;
                var previous = SelectObject(hdc, candidate);
                if (GetGlyphIndicesW(hdc, text, 1, indices, GGI_MARK_NONEXISTING_GLYPHS) == 1
                    && indices[0] != GlyphMissing)
                {
                    chosenFont = candidate;
                    previousFont = previous;
                    break;
                }
                // This face does not cover the codepoint: put the old font back and try the next one.
                SelectObject(hdc, previous);
                DeleteObject(candidate);
            }
            if (chosenFont == IntPtr.Zero)
            {
                SelectObject(hdc, previousBitmap);
                return 0;                        // no font here has it: an honest "no icon" beats a box
            }

            SetBkMode(hdc, 1);                   // TRANSPARENT
            // COLORREF is 0x00BBGGRR while a script writes 0xRRGGBB: the swap is the shared ToColorRef&#39;s, which used to
            // live on Control and was copied here because it was private there. It is on the outer class now, so the
            // control colours and this renderer share ONE expression.
            var cr = ToColorRef(colorRgb);
            Interpreter.SetTextColor(hdc, cr);
            var box = new Rect { Left = 0, Top = 0, Right = size, Bottom = size };
            DrawTextW(hdc, text, text.Length, ref box, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);

            // Did it actually paint, and GIVE THE PIXELS AN ALPHA: GDI does not write the alpha channel of a
            // 32-bit DIB, so a glyph drawn here arrives with A=0 everywhere, which is an icon Windows draws as
            // fully transparent (measured: both "✅" and "★" came back as 0 icons). Every pixel the glyph touched
            // (any non-zero channel) becomes opaque; the pixels it did not are left clear, which is the shape.
            var painted = 0;
            var bytes = new byte[size * size * 4];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            for (var i = 0; i < bytes.Length; i += 4)
            {
                if (bytes[i] != 0 || bytes[i + 1] != 0 || bytes[i + 2] != 0)
                {
                    bytes[i + 3] = 255;
                    painted++;
                }
            }
            // A glyph that barely painted (a single stray pixel) is not an icon either: a percent of the box is
            // the floor, which a real glyph clears by an order of magnitude and a dot does not.
            if (painted < (size * size) / 100)
            {
                SelectObject(hdc, previousFont);
                DeleteObject(chosenFont);
                SelectObject(hdc, previousBitmap);
                return 0;                        // a glyph that painted nothing is not an icon
            }
            Marshal.Copy(bytes, 0, bits, bytes.Length);

            mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);   // 32-bit alpha carries the shape; the mask stays empty
            var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = dib };
            var icon = CreateIconIndirect(ref info);

            SelectObject(hdc, previousFont);
            DeleteObject(chosenFont);
            SelectObject(hdc, previousBitmap);
            if (mask != IntPtr.Zero) DeleteObject(mask);
            return icon.ToInt64();
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(hdc);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private const uint WM_SETICON = 0x0080;
    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    private static IntPtr _exeIcon = IntPtr.Zero;
    private static IntPtr _exeSmallIcon = IntPtr.Zero;

    /// <summary>The RUNNING executable's own icon, out of its resource (the project's `ApplicationIcon`, so
    /// `forward.ico` for this engine), in BOTH sizes. Extraction goes by INDEX rather than a resource id, because
    /// the .NET apphost does not put its icon at the id a hand-written resource would (measured: index 0 finds
    /// the icon where `LoadImage(…, 1, …)` answers zero). Loaded once for the process's life, so the two handles
    /// are never freed, and a build with no icon simply answers zero rather than failing.</summary>
    private static void LoadExeIcons()
    {
        if (_exeIcon != IntPtr.Zero) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            // shell32's ExtractIconEx, the id-agnostic route: the large icon first, then the small one.
            var count = ExtractIconEx(exe, 0, out var large, out var small, 1);
            if (count > 0)
            {
                _exeIcon = large;
                _exeSmallIcon = small;
            }
        }
        catch (Exception)
        {
            // A module without icons (or a shell32 that refuses) is not an error: the window then keeps the
            // system's default, which is what every form did before this existed.
        }
    }

    internal static IntPtr ExeIconHandle()
    {
        LoadExeIcons();
        return _exeIcon;
    }

    internal static IntPtr ExeSmallIconHandle()
    {
        LoadExeIcons();
        return _exeSmallIcon != IntPtr.Zero ? _exeSmallIcon : _exeIcon;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>Every monitor's WORK AREA (the part a window may use, so the taskbar is excluded), in the order
    /// Windows enumerates them, which is the order the Screen property is numbered in. One enumeration is the
    /// source for both the count and the placement, so the two can never disagree: a count taken from
    /// SM_CMONITORS would be a second opinion about a machine whose monitors are hot-pluggable.</summary>
    internal static List<RECT> MonitorWorkAreas()
    {
        var areas = new List<RECT>();
        MonitorEnumProc collect = (IntPtr hMonitor, IntPtr hdc, ref RECT monitor, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(hMonitor, ref info)) areas.Add(info.rcWork);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return areas;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetActiveWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hWnd, out Rect lpRect);

    /// <summary>A text extent, which is how the thin bar learns ONE line's height from the control's own font rather
    /// than assuming it (a bar sized from a guess is a different length on another DPI).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TextSize
    {
        public int Width;
        public int Height;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextExtentPoint32(nint hdc, string lpString, int c, ref TextSize psizl);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetScrollInfo(nint hWnd, int nBar, ref ScrollInfo lpsi);

    [LibraryImport("user32.dll")]
    private static partial nint GetDCEx(nint hWnd, nint hrgnClip, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out Rect lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ValidateRect(nint hWnd, nint lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateWindow(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    // =============================================
    // user32.dll – Window Properties
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPropW(IntPtr hWnd, string lpString, IntPtr hData);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetPropW(IntPtr hWnd, string lpString);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr RemovePropW(IntPtr hWnd, string lpString);

    // =============================================
    // user32.dll – Window Class and Creation
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial short RegisterClassExW(ref Wndclassexw param);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    // =============================================
    // user32.dll – Window Long Pointers
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetParent(nint hWnd);

    // =============================================
    // user32.dll – Message Handling
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(ref Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(ref Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>The message loop's wait when a RUN CAN BE CANCELLED: it blocks until a message is queued OR the run's
    /// token is signalled, which is what lets a kill reach a script that is sitting in `Application.Run` (`GetMessageW`
    /// alone would never return, so the run could only be stopped by ending the process).</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint MsgWaitForMultipleObjects(uint nCount, IntPtr[] pHandles,
        [MarshalAs(UnmanagedType.Bool)] bool fWaitAll, uint dwMilliseconds, uint dwWakeMask);

    /// <summary>Posts a message to a THREAD rather than to a window, which is the only way to wake a `GetMessageW`
    /// that has nothing to dispatch. `Events.Create({ mode: "runThread" })` needs it: its deliveries ride the
    /// engine's ONE main-thread queue, and that queue is drained when a message arrives, so an idle window would hold
    /// a value until the user moved the mouse. `WM_NULL` is a no-op, so the wake cannot change anything a script sees.
    /// A thread with no message queue (a console run) answers false, and delivery then happens at the next statement,
    /// `Task.Wait` or `sleep`, which is why the return value is not checked.</summary>
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    /// <summary>The thread's NATIVE id, which is what `PostThreadMessageW` takes. `Environment.CurrentManagedThreadId`
    /// is a managed id (1, 2, 3...) and posting to it fails while `GetLastError` stays 0, which reads as success:
    /// measured, the delivery the wake exists for arrived 169 ms late instead of at once. `NativeApp`'s own copy of
    /// this import is private to that nested type (a containing type cannot see a nested type's privates), which is
    /// why this second declaration exists rather than a call across.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static partial uint KernelCurrentThreadId();

    /// <summary>Wakes the run thread's message loop so a queued main-thread delivery is drained now. Answers whether
    /// the post was accepted (a thread with no message queue refuses it), which only a diagnostic reads: a false here
    /// is harmless, since the next statement boundary drains the same queue.</summary>
    internal static bool WakeRunThread(int threadId)
    {
        if (threadId <= 0) return false;
        const uint WM_NULL = 0x0000;
        return PostThreadMessageW((uint)threadId, WM_NULL, 0, 0);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int TranslateMessage(ref Msg lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint DispatchMessageW(ref Msg lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SendMessageW(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SendMessageA(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    // One spelling of the script-side coercion for a window message, shared by the namespace members
    // (WinApi.SendMessage / WinApi.PostMessage, which take an hwnd) and the control members
    // (Control.SendMessage / Control.PostMessage, which use the control's own handle). A script's two ways of saying
    // the same thing therefore cannot drift on sign, width or the uint conversion of the message id.
    internal static long SendMessageScript(long hwnd, long msg, long wParam, long lParam)
        => (long)SendMessageW((nint)hwnd, Convert.ToUInt32(msg), (nuint)wParam, (nint)lParam);

    internal static bool PostMessageScript(long hwnd, long msg, long wParam, long lParam)
        => PostMessageW((nint)hwnd, Convert.ToUInt32(msg), (nint)wParam, (nint)lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint GetMessagePos();

    // =============================================
    // user32.dll – Default Window Procedure
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    // =============================================
    // user32.dll – MDI Window Procedures
    // =============================================

    [LibraryImport("user32.dll")]
    private static partial nint DefFrameProcW(nint hWnd, nint hWndMDIClient, uint uMsg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefMDIChildProcW(nint hWnd, uint uMsg, nint wParam, nint lParam);

    // =============================================
    // user32.dll – Focus and Input
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint SetFocus(nint hWnd);

    // =============================================
    // user32.dll – System Metrics
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    // =============================================
    // user32.dll – DPI Scaling
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool SetProcessDPIAware();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool AdjustWindowRectExForDpi(ref Rect lpRect, uint dwStyle, [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle, uint dpi);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    // =============================================
    // user32.dll – Timers
    // =============================================

    // =============================================
    // user32.dll – Clipboard
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("user32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint GetClipboardData(uint uFormat);

    // =============================================
    // user32.dll – Device Context
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int vKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    /// <summary>The pixels per inch a window's DC reports, which is the scale a RichEdit uses when it turns a char
    /// format's TWIPS into pixels (see <see cref="Control.ApplyRichEditFont"/>). It is ASKED rather than assumed: a
    /// test on this box answered 144 even for a box born on the 96 DPI monitor, so a conversion that hardcoded the
    /// system DPI would be right here and wrong elsewhere.</summary>
    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(nint hdc, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    // =============================================
    // user32.dll – Window Procedures
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetCapture(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetCapture();

    // =============================================
    // user32.dll – UI Controls
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint CreateMenu();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool AppendMenuW(nint hMenu, uint uFlags, nint uIdNewItem, string lpNewItem);

    [LibraryImport("user32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool SetMenu(nint hWnd, nint hMenu);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(nint hWnd, int bEnable);

    [LibraryImport("user32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint GetDlgItem(nint hDlg, int nIdDlgItem);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTextW(nint hWnd, string lpString);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint hWnd, out char lpString, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint hWnd, Span<char> lpString, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowTextLengthW(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial int MessageBoxW(nint hWnd, [MarshalAs(UnmanagedType.LPWStr)] string lpText, [MarshalAs(UnmanagedType.LPWStr)] string lpCaption, uint uType);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint LoadCursorW(nint hInstance, int lpCursorName);

    /// <summary>Set the pointer's shape, which is what the engine's thin bar does over its strip: a class gives its
    /// whole client its own cursor (a RichEdit an I-beam), and the strip is inside that client.</summary>
    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint hCursor);

    /// <summary>The pointer's position, which the thin bar needs to know whether it is being pointed at. Declared HERE
    /// because a LibraryImport is visible to its own class only, which is this file's recorded rule (the Menu class has
    /// its own copy for the popup it tracks).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(ref Point p);

    // =============================================
    // user32.dll – GDI
    // =============================================

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int FillRect(IntPtr hDC, ref Rect lprc, IntPtr hbr);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int DrawTextW(nint hdc, [MarshalAs(UnmanagedType.LPWStr)] string lpchText, int cchText, ref Rect lprc, uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool DrawFrameControl(nint hdc, ref Rect lprc, uint uType, uint uState);

    private const uint BM_GETCHECK = 0x00F0;

    // =============================================
    // dwmapi.dll – DWM
    // =============================================

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    // =============================================
    // kernel32.dll – Console
    // =============================================

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    internal static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", EntryPoint = "Sleep")]
    public static partial void Sleep(uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string lpModuleName);

    // =============================================
    // gdi32.dll – GDI Objects
    // =============================================

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint GetStockObject(int fnObject);

    // LibraryImport, like every other Windows call here. CharSet.Unicode meant the runtime appended W to the member
    // name, so the export is CreateFontW and Utf16 is the marshalling that replaces it; naming the entry point is the
    // part LibraryImport cannot infer, since it binds to the MEMBER's name (CreateFont).
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFont(
        int nHeight,
        int nWidth,
        int nEscapement,
        int nOrientation,
        int fnWeight,
        uint fdwItalic,
        uint fdwUnderline,
        uint fdwStrikeOut,
        uint fdwCharSet,
        uint fdwOutputPrecision,
        uint fdwClipPrecision,
        uint fdwQuality,
        uint fdwPitchAndFamily,
        string lpszFace);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateSolidBrush(uint crColor);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreatePen(int iStyle, int cWidth, uint crColor);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);

    // =============================================
    // gdi32.dll – GDI Drawing
    // =============================================

    [LibraryImport("gdi32.dll", EntryPoint = "Rectangle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool RectangleW(nint hdc, int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool Ellipse(nint hdc, int x1, int y1, int x2, int y2);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveToEx(nint hdc, int x, int y, nint lppt);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LineTo(nint hdc, int x, int y);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool TextOutW(nint hdc, int x, int y, [MarshalAs(UnmanagedType.LPWStr)] string lpString, int c);

    // =============================================
    // gdi32.dll – GDI Text and Colors
    // =============================================

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial uint SetBkColor(nint hdc, uint color);

    // =============================================
    // comctl32.dll – Common Controls
    // =============================================

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX picce);

    // =============================================
    // uxtheme.dll – Visual Styles / Theming
    // =============================================

    // ── the System leg: Windows' own app-mode setting ────────────────────────────────────────────────
    // RegGetValue rather than the Registry class because every other Windows call in this file is a
    // LibraryImport, and because it reads one value without opening a key this process must remember to close.
    private static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)0x80000001);
    private const uint RRF_RT_REG_DWORD = 0x00000010;

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValueW(IntPtr hkey, string lpSubKey, string lpValue, uint dwFlags,
        IntPtr pdwType, out uint pvData, ref uint pcbData);
    [LibraryImport("uxtheme.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial nint OpenThemeData(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszClassList);

    [LibraryImport("uxtheme.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial int CloseThemeData(nint hTheme);

    [LibraryImport("uxtheme.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial int DrawThemeBackground(nint hTheme, nint hdc, int iPartId, int iStateId, ref Rect pRect, nint pClipRect);

    [LibraryImport("uxtheme.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial int DrawThemeText(nint hTheme, nint hdc, int iPartId, int iStateId, [MarshalAs(UnmanagedType.LPWStr)] string pszText, int cchText, uint dwTextFlags, uint dwTextFlags2, ref Rect pRect);

    [LibraryImport("uxtheme.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial bool IsThemeActive();

    [LibraryImport("uxtheme.dll", SetLastError = true)]
    private static partial int SetWindowTheme(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszSubAppName, [MarshalAs(UnmanagedType.LPWStr)] string? pszSubIdList);

    // =============================================
    // user32.dll – Window Enumeration
    // =============================================
    private delegate bool EnumWindowProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint hWndParent, EnumWindowProc lpEnumFunc, nint lParam);

    // =============================================
    // shell32.dll – Drag and Drop
    // =============================================

    // DragAcceptFiles used to be declared here with no caller. It is Control's now, where AcceptDroppedFiles calls it, so
    // this duplicate declaration is gone rather than left as a second entry point nothing in the engine reaches.
    [LibraryImport("shell32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial uint DragQueryFileW(nint hDrop, uint iFile, nint lpszFile, uint cch);

    [LibraryImport("shell32.dll", SetLastError = true)]
    // no caller in the engine: this P/Invoke is declared and never called from anywhere in Ui.cs, and
    // a script cannot reach it (it is not public). Kept rather than deleted because removing it is a
    // separate decision; in a NativeAOT image each one is an entry point with nothing calling it.
    private static partial void DragFinish(nint hDrop);

    #endregion

    internal enum DockStyle { None, Left, Top, Right, Bottom, Fill }

    /// <summary>How a form gets its look. <c>Light</c> and <c>Dark</c> are the two native legs, <c>System</c>
    /// follows Windows' own "app mode" setting, and <c>Custom</c> makes the form paint from the palette below
    /// (<c>ThemeBackground</c>, <c>ThemeText</c>, <c>ThemeBorder</c>, <c>ThemeSelection</c>, <c>ThemeDisabled</c>).
    /// A value's REPORTED name is always a name a script can write, so this one is registered in BOTH halves (the
    /// scope binding and the type registry), which is what makes <c>ThemeMode.Dark</c> and <c>x is ThemeMode</c>
    /// agree: measured for DockStyle, a scope binding alone answered <c>is</c> with a silent FALSE.</summary>
    public enum ThemeMode { Light, Dark, System, Custom }

    /// <summary>The five colours a <c>Custom</c> form paints with. A part the engine does not draw keeps the leg the
    /// palette's own luminance implies, so a dark palette still gets dark scrollbars and themed button faces rather
    /// than a half-converted window. Null means "derive it from that leg".</summary>
    internal struct ThemePalette
    {
        public uint? Background;
        public uint? Text;
        public uint? Border;
        public uint? Selection;
        public uint? Disabled;
    }
    /// <summary>The theme state one window keeps: which mode it was asked for, the palette, and the leg its native
    /// parts use. Form, MdiForm and MdiChild each hold one of these, so the resolution (Dark and Light are the two
    /// native looks, System follows Windows' own app-mode setting, Custom follows the palette's brightness) exists
    /// ONCE. Two copies would drift the way the friendly-message maps and the reflection paths did.</summary>
    internal struct ThemeState
    {
        public ThemeMode Mode;
        public ThemePalette Palette;

        /// <summary>The leg the parts the engine cannot paint (a scrollbar, a themed button face) follow.</summary>
        public bool IsDark()
        {
            switch (Mode)
            {
                case ThemeMode.Dark: return true;
                case ThemeMode.Light: return false;
                case ThemeMode.System: return SystemIsDark();
                default: return PaletteIsDark();
            }
        }

        /// <summary>Luminance (0.299 R + 0.587 G + 0.114 B) rather than a per-channel test, so a saturated colour
        /// lands on the leg people would call it. A palette with no background is assumed light.</summary>
        public bool PaletteIsDark()
        {
            var bg = Palette.Background ?? 0xF0F0F0u;
            var r = (bg >> 16) & 0xFF; var g = (bg >> 8) & 0xFF; var b = bg & 0xFF;
            return (0.299 * r + 0.587 * g + 0.114 * b) < 128;
        }

        // THE PALETTE DECIDES ONLY IN `Custom`. Reading Palette.* whenever a field happened to be set made a LEFTOVER
        // palette colour a form that had been switched back to Light or Dark: measured, after a Custom leg, a Light
        // form's ownerdraw check boxes and radio buttons kept painting 0xA05030 (the palette's background) while every
        // other kind was back to 0xF0F0F0, because the ownerdraw painter asks these accessors. Setting the mode is
        // also what a reader expects: `Theme = Light` means the palette is out of the picture, not that it is hidden.
        public uint Background() => (IsCustom() ? Palette.Background : null) ?? (IsDark() ? 0x202020u : 0xFFFFFFu);
        public uint Text() => (IsCustom() ? Palette.Text : null) ?? (IsDark() ? 0xFFFFFFu : 0x000000u);
        public uint Border() => (IsCustom() ? Palette.Border : null) ?? (IsDark() ? 0x3F3F46u : 0xC8C8C8u);
        public uint Selection() => (IsCustom() ? Palette.Selection : null) ?? (IsDark() ? 0x2D5B8Cu : 0x0A66C2u);
        public uint Disabled() => (IsCustom() ? Palette.Disabled : null) ?? (IsDark() ? 0x7A7A7Au : 0x6D6D6Du);

        /// <summary>True when Custom was asked for, which is when the palette (rather than the leg) decides.</summary>
        public bool IsCustom() => Mode == ThemeMode.Custom;

        /// <summary>Windows' own app-mode setting: HKCU\...\Themes\Personalize\AppsUseLightTheme, where 0 means the
        /// apps are DARK. Any failure (no such value, no key, a refusing policy) reads as Light, which is Windows'
        /// default and the leg that renders correctly everywhere.</summary>
        public static bool SystemIsDark()
        {
            try
            {
                var size = 4u;
                var rc = RegGetValueW(HKEY_CURRENT_USER,
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", RRF_RT_REG_DWORD, IntPtr.Zero, out var value, ref size);
                return rc == 0 && value == 0;
            }
            catch { return false; }
        }
    }

}
