namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
    public abstract partial class Control : IDisposable
    {
        // -------------------------------------------------------------
        // Static handle-to-instance map
        // -------------------------------------------------------------
        internal static readonly Dictionary<IntPtr, Control> HandleMap = [];
        private static readonly Lock MapLock = new();

        internal static IntPtr GetFirstWindowHandle()
        {
            foreach (var kv in HandleMap)
                return kv.Key;
            return IntPtr.Zero;
        }
        private IntPtr _toolTipHandle;
        private IntPtr _currentFont = IntPtr.Zero;
        private string _fontName = "";   // "" = default GUI font
        private int _fontSize = 0;       // 0 = engine default size for the DPI
        private bool _fontBold;
        private int _lastFontDpi = 0;
        private Interpreter? _interpreter;

        /// <summary>The interpreter that CREATED this control (null once it has been disposed). Asked by the engine's
        /// form index, which must never hand a later run a form belonging to a finished one.</summary>
        internal Interpreter? OwnerInterpreter => _interpreter;

        // Color fields (COLORREF format: 0x00BBGGRR)
        private uint? _textColor;
        protected uint? _backgroundColor;
        internal IntPtr TextColorBrush;   // Cached brush for background
        /// <summary>The colour TextColorBrush holds, so a palette or theme change rebuilds it: without this a
        /// re-themed form kept painting the previous background (the brush was cached by identity, not by colour).
        /// </summary>
        internal uint TextColorBrushColor;

        /// <summary>The form a control belongs to, walking up to it (a control can sit in a panel inside another
        /// panel). One lookup for both the palette readers, so they cannot disagree about who the host is.</summary>
        internal static Form? HostFormOf(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
                if (p is Form managed) return managed;
            // A control the engine created natively is themed through its HWND, and the colour messages travel along
            // the WINDOW chain (WM_CTLCOLOR* reaches the PARENT window), so a managed Parent that is not filled in
            // must not make the palette invisible: measured, a label under a Custom form kept the dark default.
            for (var h = GetParent(c.Handle); h != IntPtr.Zero; h = GetParent(h))
                if (FromHandle(h) is Form native) return native;
            return null;
        }

        /// <summary>Set when a THEME, not the script, put this control's colours on it, so the light leg can take
        /// them off again and leave a script's own colour alone.</summary>
        internal bool ThemeTinted;

        /// <summary>Set when the engine, not the class, fills this control's background: a TabControl's body is
        /// painted with the class's own face colour and NOTHING changes it (measured: setting BackgroundColor left
        /// its pixel at 0xF0F0F0 under a dark form), so the engine fills the client and lets the class draw its own
        /// tab strip on top, which is a light selected tab on a dark body: the normal dark-tab look.</summary>
        /// <summary>The dark client fill's brush, created on first use and kept for the process: a DarkBody control
        /// fills on every WM_ERASEBKGND, and this site used to allocate a brush per erase.</summary>
        private static nint _darkClientBrush;
        private static uint _darkClientColor;   // the colour that brush holds, so a palette change repaints
        internal bool DarkBody;

        /// <summary>A private check state for the OWNER-DRAWN case. Windows keeps no state for a BS_OWNERDRAW button:
        /// BM_SETCHECK is a no-op there and BM_GETCHECK answers the initial value for ever (measured: a click raised
        /// Click and the state never moved), so the engine holds it and the painter reads it from here. It lives on
        /// Control, which is also the class that seeds it when the owner-draw type lands.</summary>
        internal bool _ownerChecked;   // internal, not private: the two button classes read it, and the binder binds Public only

        /// <summary>The Windows class this control's window is created from, when it is not the engine's own wrapper
        /// class. Empty for a control that lets the engine choose (a Button, a TrackBar): those are never RichEdits, so
        /// the one predicate above can ask every control the same question.</summary>
        internal virtual string NativeClass => "";

        [StructLayout(LayoutKind.Sequential)]
        private struct DRAWITEMSTRUCT
        {
            public uint CtlType, CtlID, itemID, itemAction, itemState;
            public nint hwndItem, hDC;
            public Rect rcItem;
            public nint itemData;
        }

        // The drop-target switch `DropFiles` needs, and it belongs HERE rather than on Form so a CHILD can be a drop
        // target too: Windows sends WM_DROPFILES to the window the mouse is over and does NOT walk up to the parent
        // (measured), so a control that covers its host (a RichEdit filling a window) is the only window that sees it.
        [LibraryImport("shell32.dll", SetLastError = true)]
        private static partial void DragAcceptFiles(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

        internal static Control? FromHandle(IntPtr hwnd)
        {
            lock (MapLock) return HandleMap.GetValueOrDefault(hwnd);
        }

        internal void RegisterHandle()
        {
            AssignId();
            lock (MapLock) HandleMap[handle] = this;
        }

        /// <summary>The id, assigned ONCE per control and written into the window as its Win32 CHILD ID, which is what
        /// WM_COMMAND reports, what GetDlgItem looks a window up by, and what Id reads back out of the window. The guard
        /// makes it once-only because a control can be REBUILT, which the DPI path does on purpose.
        ///
        /// It has TWO call sites and that is the point: RegisterHandle covers every control, and a FORM must say it
        /// itself, because a form deliberately keeps its own registration (the StaticWndProc path) and never reaches
        /// RegisterHandle. Measured: while only RegisterHandle assigned an id, a form reported id 0 for ever.</summary>
        protected void AssignId()
        {
            if (_assignedId != 0 || handle == IntPtr.Zero) { return; }
            _assignedId = _nextControlId++;
            // WHERE IT STARTS IN THE TAB ORDER: the place it was created in, so a script that wants the natural order
            // sets nothing at all. That is the DEFAULT VALUE of an ordering, not a statement about the Z-order.
            _tabIndex = _parent is Form parent ? parent.Controls.Count - 1 : 0;   // added before it is created, so this IS its index
            SetWindowLongPtrW(handle, GWLP_ID, (nint)_assignedId);
        }

        private void UnregisterHandle()
        {
            lock (MapLock) HandleMap.Remove(handle);
        }

        // -------------------------------------------------------------
        // Instance fields
        // -------------------------------------------------------------
        protected IntPtr handle;
        private GCHandle _gcHandle;
        private Control? _parent;
        private IntPtr _forcedParentHwnd;
        internal IntPtr ParentHandle => _forcedParentHwnd != IntPtr.Zero ? _forcedParentHwnd : _parent?.Handle ?? IntPtr.Zero;
        internal void SetParentHwnd(IntPtr hwnd) { _forcedParentHwnd = hwnd; }
        private string _text = "";
        protected int left, top, width, height;
        protected bool _visible = true, _enabled = true;
        protected bool inSizeMove;

        // DPI scaling support
        private int _designLeft, _designTop, _designWidth, _designHeight;
        private bool _designBoundsStored;

        private int _margin;
        private int _marginLeft = -1, _marginTop = -1, _marginRight = -1, _marginBottom = -1;

        private DockStyle _dock = DockStyle.None;

        /// <summary>The tooltip for THIS control: created ONCE, then RE-TEXTED, and destroyed with the control
        /// (Dispose). Returns the tooltip's window, so a caller (and a suite) can see which window it is and read
        /// the tip back with TTM_GETTEXTW.
        /// ONE implementation, TWO spellings: `WinApi.SetToolTip(hwnd, text)` hands a window this engine wrapped
        /// to this method, and only a window nothing wraps gets a tooltip built beside it
        /// (SetToolTipForHandle). Measured before this: two calls through the WinApi spelling returned two
        /// DIFFERENT handles, i.e. a leftover tooltips_class32 window per call, and this method re-sent
        /// TTM_ADDTOOLW on every call, which is the message Win32 does NOT use to change a tip (that is
        /// TTM_UPDATETIPTEXTW), so each call ADDED a tool to the same tooltip.</summary>
        public long SetToolTip(string text)
        {
            if (_toolTipHandle == IntPtr.Zero)
            {
                _toolTipHandle = CreateWindowExW(0, "tooltips_class32", "",
                    WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
                    0, 0, 0, 0,
                    handle, IntPtr.Zero, GetModuleHandleW(null!), IntPtr.Zero);
                if (_toolTipHandle == IntPtr.Zero) return 0;
                SendMessageW(_toolTipHandle, WM_SETFONT, (nuint)GetStockObject(DEFAULT_GUI_FONT), 0);
            }
            else if (text == _toolTipText)
            {
                return _toolTipHandle.ToInt64();   // the tip already says this, so nothing is sent
            }

            // A control's tool is identified by the pair (its own window, its own window) because it is added
            // with TTF_IDISHWND: TTM_GETTEXTW only finds a tool when it is asked with the same pair.
            AddOrUpdateTool(_toolTipHandle, handle, handle, text, first: _toolTipText == null);
            _toolTipText = text;
            return _toolTipHandle.ToInt64();
        }

        /// <summary>Adds the tool the first time and RE-TEXTS it after that, which is the one difference between
        /// the two messages. The unmanaged copy of the text is freed here because the tooltip copies the string
        /// when the tool is added or updated (same contract as the point-freeze the two spellings share).</summary>
        private static void AddOrUpdateTool(nint hToolTip, nint hwndField, nint uIdField, string text, bool first)
        {
            var ti = new TOOLINFOW
            {
                cbSize = (uint)Marshal.SizeOf<TOOLINFOW>(),
                uFlags = TTF_SUBCLASS | TTF_IDISHWND,
                hwnd = hwndField,
                uId = uIdField,
                lpszText = Marshal.StringToHGlobalUni(text)
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(ti));
            try
            {
                Marshal.StructureToPtr(ti, ptr, false);
                SendMessageW(hToolTip, first ? TTM_ADDTOOLW : TTM_UPDATETIPTEXTW, 0, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
                Marshal.FreeHGlobal(ti.lpszText);
            }
        }

        /// <summary>The tooltips of RAW handles: windows nothing in this engine wraps, so there is no Control
        /// whose lifetime could destroy one. Created once per handle and kept for the process, and a repeat call
        /// re-texts it instead of standing up a second window (which is what the WinApi spelling did on every
        /// call before this). A wrapped window never lands here: the lookup finds its Control and hands over.</summary>
        private static readonly Dictionary<nint, (nint ToolTip, string? Text)> RawToolTips = [];
        private static readonly Lock RawToolTipLock = new();

        internal static nint SetToolTipForHandle(nint hwnd, string text)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (HandleMap.TryGetValue(hwnd, out var ctrl)) return (nint)ctrl.SetToolTip(text);
            lock (RawToolTipLock)
            {
                var first = !RawToolTips.TryGetValue(hwnd, out var known);
                var hToolTip = first
                    ? CreateWindowExW(0, "tooltips_class32", "", TTS_ALWAYSTIP | TTS_NOPREFIX, 0, 0, 0, 0,
                        GetParent(hwnd), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                    : known.ToolTip;
                if (hToolTip == IntPtr.Zero) return IntPtr.Zero;
                if (!first && known.Text == text) return hToolTip;
                // An unwrapped window's tool is added the way the WinApi spelling always did: the PARENT owns the
                // tooltip and the tool is identified by the window the mouse lands on.
                AddOrUpdateTool(hToolTip, GetParent(hwnd), hwnd, text, first);
                RawToolTips[hwnd] = (hToolTip, text);
                return hToolTip;
            }
        }

        internal const uint TTS_ALWAYSTIP = 0x01;
        internal const uint WS_POPUP = 0x80000000;
        internal const uint TTS_NOPREFIX = 0x02;
        internal const uint TTF_SUBCLASS = 0x0010;
        internal const uint TTF_IDISHWND = 0x0001;
        internal const uint TTM_ADDTOOLW = 0x0432;
        /// <summary>Changing the TIP of a tool that exists. `TTM_ADDTOOLW` above is the W form of a tooltip message
        /// (WM_USER + 50 rather than + 4), and the same offset applies to this one: WM_USER + 57.</summary>
        internal const uint TTM_UPDATETIPTEXTW = 0x0439;
        /// <summary>The control's current tip, so a repeated SetToolTip with the same text sends nothing.</summary>
        private string? _toolTipText;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct TOOLINFOW
        {
            public uint cbSize;
            public uint uFlags;
            public IntPtr hwnd;
            public IntPtr uId;
            public RECT rect;
            public IntPtr hinst;
            public IntPtr lpszText;
            public IntPtr lParam;
        }

        public IntPtr Handle => handle;

        private int _tabIndex;

        /// <summary>Paint the window NOW, synchronously, rather than letting the queue get to it: the FORCED
        /// repaint, where <see cref="Invalidate()"/> only MARKS the window dirty. This is the exception here and not
        /// the rule, for the reason the two members above give (synchronous redraws are the flicker the engine
        /// avoids), so reach for it when something has to be ON SCREEN before the next statement, which is what
        /// reading pixels back or the raw pixel probes need.</summary>
        public void UpdateWindow()
        {
            if (handle != IntPtr.Zero) { Interpreter.UpdateWindow(handle); }   // qualified: this method shadows the import by name
        }

        private static int MemberInt(Interpreter mi, object? target, string name)
        {
            if (target == null) return 0;
            try
            {
                var v = mi.GetMemberValue(target, name, 0, 0);
                return v == null ? 0 : Convert.ToInt32(v);
            }
            catch { return 0; }   // a name the object does not have
        }

        /// <summary>Pixels of a window at `hereDpi`, in the DESIGN units the engine scales from. ONE expression, two
        /// owners, and the owners are what differ: <see cref="RememberDesignBounds"/> converts a CHILD's rect against
        /// its parent form, while <see cref="ClientWidth"/> wants a FORM's OWN reference when the control IS the form
        /// (a top-level window has no parent to ask).</summary>
        internal static double ToRef(int hereDpi, int referenceDpi)
            => hereDpi > 0 && referenceDpi > 0 ? (double)referenceDpi / hereDpi : 1.0;
        /// <summary>This control's NAME, as its creator knows it: the designer's entry name, or whatever a script
        /// assigned. The engine had no other identity to report, because a window's Win32 text is the CAPTION
        /// (Text, which a user edits) and the design table's own Name row was a literal placeholder since nothing
        /// carried it. The designer's generator emits a Name assignment per control, so a handler can tell WHICH
        /// control it was handed, and PropList() reports the real value instead of the placeholder. An
        /// auto-property with a field initializer, which is fine here: the no-initializer rule is about SCRIPT
        /// class descriptors, not the engine's own classes.</summary>
        public string Name { get; set; } = "";

        public string Text
        {
            get
            {
                if (handle == IntPtr.Zero) return _text;
                var len = GetWindowTextLengthW(handle);
                if (len <= 0) return "";
                Span<char> buf = new char[len + 1];
                GetWindowTextW(handle, buf, len + 1);
                return new string(buf[..len]);
            }
            set { _text = value; if (handle != IntPtr.Zero) SetWindowTextW(handle, value); }
        }

        /// <summary>Append text to this control WITHOUT re-writing what is already there. Assigning Text sends
        /// WM_SETTEXT with the whole string, so every assignment clears and repaints the control: a log written
        /// that way flickers and costs O(n) per line. This moves the caret to the end and inserts, so only the new
        /// text is drawn. It targets text controls (an EDIT answers EM_*); a control that does not ignores it.</summary>
        public void AppendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _text += text;                 // the getter's fallback for a control that has no handle yet
            if (handle == IntPtr.Zero) return;
            SendMessageW(handle, EM_SETSEL, nuint.MaxValue, -1);   // caret to the end
            var ptr = Marshal.StringToHGlobalUni(text);
            try { SendMessageW(handle, EM_REPLACESEL, 0, ptr); }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        /// <summary>Append text in ONE colour, which is what a log needs: the run is inserted and then coloured by the
        /// same call <see cref="SetTextColor"/> uses, so a log's per-line colours and the theme's cannot drift apart.
        /// A control that is not a RichEdit appends the text and ignores the colour, because a plain EDIT has no
        /// per-run format at all. The argument is 0xRRGGBB like every other colour a script writes.</summary>
        public void AppendText(string text, long rgb)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (handle == IntPtr.Zero || !IsRichEditWindow()) { AppendText(text); return; }
            var before = SelectionRange().End;
            AppendText(text);
            var after = SelectionRange().End;
            if (after <= before) return;
            SendMessageW(handle, EM_SETSEL, (nuint)before, after);
            ApplyRichEditColour(ToColorRef((uint)(rgb & 0xFFFFFF)), all: false);
            SendMessageW(handle, EM_SETSEL, nuint.MaxValue, -1);   // the caret goes back to the end
        }

        /// <summary>The selection as a pair. A RichEdit is read with EM_EXGETSEL, whose range is two 32-bit fields: the
        /// 16-bit EM_GETSEL wraps past 65,536 characters, which is the size a log reaches (measured: a 4,000-line log
        /// coloured its runs wrongly from about line 1,300 because of it). A PLAIN edit does not answer EM_EXGETSEL at
        /// all, so it takes the 16-bit form, which is sound there because an EDIT cannot hold 65,536 characters.</summary>
        private (int Start, int End) SelectionRange()
        {
            if (handle == IntPtr.Zero) return (0, 0);
            if (!IsRichEditWindow())
            {
                var packed = (long)SendMessageW(handle, 0x00B0, 0, 0);      // EM_GETSEL
                return ((int)(packed & 0xFFFF), (int)((packed >> 16) & 0xFFFF));
            }
            var buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(buf, 0, 0);
                Marshal.WriteInt32(buf, 4, 0);
                SendMessageW(handle, EM_EXGETSEL, 0, buf);
                return (Marshal.ReadInt32(buf, 0), Marshal.ReadInt32(buf, 4));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Where the selection starts, in the control's own character positions. Assigning it MOVES the caret
        /// and keeps the selection's length (the same rule as a text box's SelStart).</summary>
        public int SelectionStart
        {
            get => SelectionRange().Start;
            set { var range = SelectionRange(); Select(value, value + (range.End - range.Start)); }
        }

        /// <summary>How many characters are selected. Assigning it keeps the start and moves the end.</summary>
        public int SelectionLength
        {
            get { var range = SelectionRange(); return range.End - range.Start; }
            set { var start = SelectionRange().Start; Select(start, start + Math.Max(0, value)); }
        }

        /// <summary>The selected text itself, which is what a Find feature reports: the range is read with
        /// EM_GETSELTEXT into a buffer sized from the SELECTION's own length, so no position is computed here. Asking
        /// the message for the length it needs (a null buffer) is not available on a RichEdit: it assumes the buffer is
        /// big enough and faults, which is measured.</summary>
        public string SelectedText
        {
            get
            {
                if (handle == IntPtr.Zero) return "";
                var chars = SelectionRange();
                var length = chars.End - chars.Start;
                if (length <= 0) return "";
                if (!IsRichEditWindow())
                {
                    // a plain EDIT keeps one line and answers WM_GETTEXT, so the range indexes Text directly
                    var whole = Text;
                    if (chars.Start >= whole.Length) return "";
                    return whole.Substring(chars.Start, Math.Min(length, whole.Length - chars.Start));
                }
                var bytes = (length + 1) * 2;
                var buf = Marshal.AllocHGlobal(bytes);
                try
                {
                    Marshal.WriteInt16(buf, 0, 0);
                    SendMessageW(handle, EM_GETSELTEXT, (nuint)bytes, buf);
                    return Marshal.PtrToStringUni(buf) ?? "";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            /// <summary>Replace the SELECTION with this text, which is how a range is rewritten or DELETED (an empty
            /// string deletes it), the shape WinForms gives this member. One EM_REPLACESEL: the text around the
            /// selection is untouched and no whole-document write happens, which is what a column of numbers trims with
            /// when the log it numbers gets shorter.</summary>
            set
            {
                if (handle == IntPtr.Zero) return;
                var replacement = value ?? "";
                var ptr = Marshal.StringToHGlobalUni(replacement);
                try { SendMessageW(handle, EM_REPLACESEL, 1, ptr); }
                finally { Marshal.FreeHGlobal(ptr); }
            }
        }

        /// <summary>Select [start, end) in the control's own positions, or the whole text with <see cref="SelectAll"/>.
        /// A NEGATIVE index is REFUSED rather than clamped, which is the difference between a wrong selection and the
        /// whole document: <see cref="LineIndex"/> answers -1 for a line the control does not know yet, and a
        /// <c>Select(-1, TextLength)</c> built from it once deleted an entire column (measured on a line-number gutter
        /// being trimmed mid-layout), because clamping the start to 0 turns it into "everything".</summary>
        public void Select(int start, int end)
        {
            if (handle == IntPtr.Zero) return;
            if (start < 0 || end < 0)
                throw new ScriptException($"Select takes two positions from 0 to the text length ({TextLength}), not ({start}, {end}). Use LineIndex to find a position and check it: it answers -1 for a line the control has no layout for yet.");
            SendMessageW(handle, EM_SETSEL, (nuint)start, end);
        }

        public void SelectAll()
        {
            if (handle == IntPtr.Zero) return;
            SendMessageW(handle, EM_SETSEL, 0, -1);
        }

        /// <summary>How many ROWS the control holds, counted the way a reader sees them, and that includes the rows a
        /// WRAPPED line takes, because the control counts what it displays. A RichEdit's own EM_GETLINECOUNT answers one
        /// MORE than that when the text ends with a line break (measured: 6 rows for a log of 5), which is why this
        /// correction is stated here once instead of in every script. THE LAST ROW IS ASKED WITH EM_GETLINE, not read
        /// out of Text: this is the member a per-pass caller asks, and a full text copy per pass is what that would
        /// cost (measured on a 1,400-character log: the copy dominated the pass).</summary>
        public int LineCount
        {
            get
            {
                if (handle == IntPtr.Zero) return _text.Length == 0 ? 0 : TextLines.Count(_text);
                var lines = (int)SendMessageW(handle, EM_GETLINECOUNT, 0, 0);
                // AN EMPTY DOCUMENT IS ZERO ROWS, not one: its single row has no characters, so the same correction
                // applies at lines == 1 (measured: guarding it with `lines > 1` left an empty log claiming one row).
                if (lines > 0 && LastRowIsEmpty(lines)) lines--;
                return lines;
            }
        }

        /// <summary>Whether the last row holds no characters, which is the phantom row a trailing line break creates.
        /// TWO MESSAGES and no buffer: EM_LINEINDEX says where the row starts and EM_LINELENGTH says how long the line
        /// holding that position is. EM_GETLINE was tried first and does NOT follow the classic protocol on a RichEdit
        /// (measured: an empty last row answered non-zero, so the correction never fired and every count was one too
        /// high), which is why this asks lengths instead of characters.</summary>
        private bool LastRowIsEmpty(int lines)
        {
            var start = (int)SendMessageW(handle, EM_LINEINDEX, (nuint)(lines - 1), 0);
            if (start < 0) return false;
            return SendMessageW(handle, EM_LINELENGTH, (nuint)start, 0) == IntPtr.Zero;
        }

        /// <summary>The character index the given line starts at, which is what turns a line number into a position
        /// for <see cref="Select"/>: a RichEdit counts a line break as ONE character while its Text reads two, so
        /// multiplying a line number by a line length is always wrong. It answers <b>-1</b> when the control has no such
        /// line: past the end, and also for a line it has not laid out yet. That -1 is a real answer to CHECK, because
        /// <see cref="Select"/> refuses a negative position rather than clamping it to 0.</summary>
        public int LineIndex(int line)
        {
            if (handle == IntPtr.Zero) return -1;
            return (int)SendMessageW(handle, EM_LINEINDEX, (nuint)Math.Max(0, line), 0);
        }

        /// <summary>Where a character sits in this control's CLIENT area, as [x, y]: the answer a second window
        /// beside a text control needs in order to line its own rows up with the text's.</summary>
        public long[] CharPosition(int index)
        {
            if (handle == IntPtr.Zero) return new[] { 0L, 0L };
            var pos = (long)SendMessageW(handle, EM_POSFROMCHAR, (nuint)Math.Max(0, index), 0);
            return new long[] { (short)(pos & 0xFFFF), (short)((pos >> 16) & 0xFFFF) };
        }

        /// <summary>The number of characters in the text, in ONE message and without building the string, which is what a
        /// check that runs on every pass wants (a log box asked this way never pays a 120k copy to learn one number). It
        /// counts a line break the way <c>Text</c> reads it, so it is NOT the character count
        /// <see cref="SelectionStart"/>, <see cref="Select"/> and <see cref="LineIndex"/> use: a RichEdit addresses its
        /// content with ONE character per break. The control's own end position is <c>SelectionStart</c> after an
        /// <see cref="AppendText(string)"/>.</summary>
        public int TextLength
        {
            get
            {
                if (handle == IntPtr.Zero) return _text.Length;
                return (int)SendMessageW(handle, WM_GETTEXTLENGTH, 0, 0);
            }
        }
        /// <summary>Whether the control refuses edits. On a text control this is the control's OWN state, which is why
        /// it is a member and not a style bit: an EDIT or a RichEdit answers EM_SETREADONLY, so the caret still moves,
        /// the text can still be selected and copied, and only typing does nothing. A control with no such notion
        /// remembers the value and ignores it, so one flag can be set on a panel without checking what it holds.</summary>
        public bool ReadOnly
        {
            get => _readOnly;
            set
            {
                _readOnly = value;
                if (handle != IntPtr.Zero) SendMessageW(handle, EM_SETREADONLY, value ? (nuint)1 : (nuint)0, 0);
            }
        }

        private bool _readOnly;

        /// <summary>The height the control's font is DRAWN at, in pixels for the control's own DPI: the number
        /// CreateFont was given. A script lining a companion window up with text needs it, and it is a member because
        /// the arithmetic (points, the monitor's DPI, the engine's default size) lives in ONE place and a script
        /// cannot reproduce it without knowing all three.</summary>
        public int TextHeightPx => FontPixelHeight();
        public object? Tag { get; set; }

        // ── Window styles at runtime, on THIS control's own handle ───────────────────────────────────────────
        // The same three operations the WinApi namespace exposes for an HWND, attached to every control the form
        // factories return (button, text box, check box, status bar, tab, and the form itself). They are the SAME
        // work rather than a second copy: both spellings call ApplyWindowStyle, which owns the OR/AND pair and the
        // SWP_FRAMECHANGED re-apply. A control has them because the engine reflects over this class's PUBLIC
        // members, and the [DynamicallyAccessedMembers] annotation on the class is what keeps them on the AOT
        // build, so nothing had to be registered for them to appear.
        // `bEx` selects the EXTENDED style word (GWL_EXSTYLE: WS_EX_*) instead of GWL_STYLE (WS_*), on all three.
        public long ReadStyle(bool bEx = false) => ReadWindowStyle(handle, bEx);
        /// <summary>Is the NAMED style flag set on this control? Every control answers, the same way every control
        /// has ModStyle/StyleAdd/StyleRemove, because the engine reflects over this class's public members. The names
        /// are the ones UiStyle.Flags lists ("WS_BORDER", "BS_PUSHBUTTON", "WS_EX_CLIENTEDGE"), so a caller asks about
        /// a flag the way a designer shows it instead of passing bits, so a caller asks
        /// Interpreter.StyleFlagIsSet rather than passing bits.</summary>
        public bool HasStyle(string name) => StyleFlagIsSet(this, name);

        /// <summary>Set or clear ONE named style flag, FIELD-aware: setting a value clears the rest of its field, so
        /// BS_CHECKBOX then BS_RADIOBUTTON cannot OR into 0x7, which is BS_GROUPBOX.</summary>
        public void SetStyle(string name, bool on) => StyleFlagSet(this, name, on);
        public void ModStyle(long add, long remove, bool bEx = false) => ApplyWindowStyle(handle, add, remove, bEx);
        public void StyleAdd(long style, bool bEx = false) => ApplyWindowStyle(handle, style, 0, bEx);
        public void StyleRemove(long style, bool bEx = false) => ApplyWindowStyle(handle, 0, style, bEx);

        // ---- the design metadata, the same rule as the styles above: the CONTROL is the door ----------------
        /// <summary>This control's editable properties, as the design table lists them: one [name, type, value]
        /// entry each. This IS the design table (Interpreter.BuildPropList), so
        /// the designer and a script read the same one.</summary>
        public List<ControlProp> PropList() => BuildPropList(this);

        /// <summary>The current value of one editable property. The names are the ones PropList lists (Text,
        /// Left, FontSize, BackColor, Items, Checked, Value, ...); the same table GetProp above lists. A name this
        /// kind does not carry throws the engine's own
        /// refusal naming it.</summary>
        public object? GetProp(string name) => GetDesignProp(this, name);

        /// <summary>Applies one editable property and returns 1, through the same validation as
        /// the designer's own apply: an unsupported name throws, and a refused value explains why.</summary>
        public long SetProp(string name, object? value) { SetDesignProp(this, name, value); return 1L; }

        /// <summary>The style-flag table for this control's kind: one [name, bits, extended, field, offered]
        /// entry each, where `offered` is false for a flag a style panel should not show. The kind-level query
        /// lives on the control, so a script asks the control it is editing rather than a namespace that takes
        /// the kind as a string.</summary>
        public List<StyleFlag> StyleFlags() => BuildStyleFlags(this);

        protected Control()
        {
            _gcHandle = GCHandle.Alloc(this);
            _interpreter = Current;
        }

        protected GCHandle GcHandle => _gcHandle;

        // -------------------------------------------------------------
        // Centralised window creation
        // -------------------------------------------------------------
        protected void CreateNativeControl(
            string className,
            string? windowName,
            uint style,
            uint exStyle,
            IntPtr parent,
            IntPtr menuOrChildId)
        {
            // Interactive generic controls are tab stops by default (Tab navigation). The creators that build their
            // style by hand below say the same thing, so this list and those lines are the ONE rule: a control a
            // script can focus carries WS_TABSTOP, and a label (STATIC) deliberately does not.
            if (className is "BUTTON" or "EDIT" or "LISTBOX" or "COMBOBOX"
                or "msctls_trackbar32" or "msctls_progress32" or "msctls_hotkey32" or "SysLink")
                style |= WS_TABSTOP;
            var hInstance = GetModuleHandleW(null!);
            var lpParam = GCHandle.ToIntPtr(_gcHandle);
            handle = CreateWindowExW(
                exStyle, className, windowName ?? "",
                style, left, top, width, height,
                parent, menuOrChildId, hInstance, lpParam);

            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for '{className}' failed: {Marshal.GetLastWin32Error()}");

            // Apply default font
            var dpi = (int)GetDpiForWindow(handle);
            _currentFont = CreateDpiFont(dpi);
            SendMessageW(handle, WM_SETFONT, (nuint)_currentFont, 1);

            // Store GCHandle in GWLP_USERDATA
            SetWindowLongPtrW(handle, GWLP_USERDATA, GCHandle.ToIntPtr(_gcHandle));

            // Register in static map (skip Form, which uses StaticWndProc registration)
            if (this is not Form)
                RegisterHandle();

            // Let script mouse events fire on native children (forwarding to the
            // original native proc afterwards).
            SubclassNativeChild();
        }

        private const int GWLP_ID = -12;
        private static int _nextControlId = 0x8000;

        /// <summary>This control&#39;s ID, read FROM THE WINDOW it belongs to: the Win32 child id, which is what
        /// WM_COMMAND reports, what <code>WinApi.GetDlgItem</code> looks a control up by, and what
        /// <code>WinApi.GetWindowLongPtr(h, GWLP_ID)</code> answers. Reading it LIVE rather than remembering it is the
        /// point: a script that assigns one itself with SetWindowLongPtr is then not a second mechanism, it is the same
        /// number seen from the other side. Before the window exists it is the id the engine assigned.</summary>
        public int GetId() => handle != IntPtr.Zero ? (int)GetWindowLongPtrW(handle, GWLP_ID) : _assignedId;

        private int _assignedId;

        /// <summary>The control behind a reference a script ALREADY HOLDS, and it takes EITHER one:
        /// <code>GetId(15)</code> for a number and <code>GetId(btn.Handle)</code> for a window, so a script never has
        /// to know which of the two it has. The ID is asked FIRST, of this window (GetDlgItem), which is what Win32
        /// itself does; a HANDLE is then answered by the engine&#39;s own registry, where a handle is unique in the whole
        /// process, so there is no scope to search and no need to know which form owns the control. A number no live
        /// child carries and a handle no control owns both answer null.</summary>
        public Control? GetId(object? reference)
        {
            if (reference == null) { return null; }
            // ToNInt is the family the native call sites convert with, and it takes exactly the three shapes a script
            // hands this: a window HANDLE (an IntPtr), a long, or an int id. Doing it this way rather than typing the
            // parameter is what lets one door take both references: a plain long parameter refuses a Handle, which is
            // an IntPtr and does not convert to it.
            var value = (long)ToNInt(reference);
            if (value == 0) { return null; }
            var child = GetDlgItem(handle, (int)value);
            if (child != IntPtr.Zero) { return FromHandle(child); }
            return FromHandle((nint)value);
        }

        /// <summary>Create this window if it does not exist yet, leaving it hidden. NOT A SCRIPT MEMBER ANY MORE: a form
        /// from `Application.CreateForm` already HAS its window (created, hidden), so the call could only ever be a
        /// no-op, and a no-op a script has to write is the kind this repository retires rather than documents (asked for
        /// directly). It is `internal` because the ENGINE still needs it: `Show` calls it for a window that does not exist
        /// yet, `InitHandle` is its public spelling, and the clipboard listener and the control factories use it to get a
        /// handle without showing anything. `control_events_test` 22f pins the retirement.</summary>
        internal void CreateControl()
        {
            if (handle != IntPtr.Zero) return;
            CreateHandle();
            if (handle == IntPtr.Zero) throw new ScriptException("CreateHandle failed.");
            if (_parent != null && _parent.handle != IntPtr.Zero)
                SetParent(handle, _parent.handle);
            ApplyProperties();
            OnHandleCreated(EventArgs.Empty);
        }

        protected abstract void CreateHandle();

        /// <summary>Whether a control of this class can be REBUILT (a fresh native window) when a DPI change left
        /// its non-client area at the old metric. False by default, because a rebuild carries over only what the
        /// ENGINE holds: a list, a tree, a grid or a combo would come back EMPTY, which is worse than a scrollbar
        /// drawn at the wrong size. A text box answers true, because its whole content is one string that can be
        /// read out of the old window before it goes.</summary>
        protected virtual bool CanRebuildOnDpiChange => false;

        /// <summary>Whether this window's non-client area disagrees with the metric for its CURRENT DPI, i.e. its
        /// scrollbars are still laid out for the DPI the window was created at. The base answers False: only a
        /// class that can be rebuilt asks, and each such class knows which bars and borders its own style implies
        /// (see TextBox).</summary>
        protected virtual bool NonClientAreaIsStale() => false;

        /// <summary>Rebuilds this control's native window on the current monitor, carrying over what the engine
        /// holds: the TEXT (read out of the old window first), the font, the visibility and the enabled state
        /// (CreateNativeControl re-applies both), the dark theming (OnHandleCreated does that), and, crucially,
        /// the DESIGN bounds a later DPI change scales from: OnHandleCreated re-stores those from the CURRENT size,
        /// which is already scaled, so without this the next monitor move would multiply the size twice.</summary>
        internal void RebuildHandleForDpi()
        {
            if (!CanRebuildOnDpiChange || handle == IntPtr.Zero) return;
            var text = Text;
            var (dl, dt, dw, dh, stored) = (_designLeft, _designTop, _designWidth, _designHeight, _designBoundsStored);
            // Runtime style changes live on the OLD window only (ModStyle/StyleAdd, say the horizontal bar a log
            // adds), and CreateHandle re-creates from the class's creation style, so the current words travel too.
            var styleNow = (uint)GetWindowLongPtrW(handle, GWL_STYLE);
            var exStyleNow = (uint)GetWindowLongPtrW(handle, GWL_EXSTYLE);
            DestroyWindow(handle);
            handle = IntPtr.Zero;
            CreateHandle();
            // add == the word and remove == ~the word: ApplyWindowStyle is the one core that writes a style word
            // and re-applies the frame, and this spelling sets it to exactly what the old window had.
            ApplyWindowStyle(handle, styleNow, ~(long)styleNow, false);
            ApplyWindowStyle(handle, exStyleNow, ~(long)exStyleNow, true);
            _designLeft = dl; _designTop = dt; _designWidth = dw; _designHeight = dh; _designBoundsStored = stored;
            Text = text;
            ApplyFont();
            if (Parent is Form f) f.PerformLayout();   // a docked control belongs in its band again
        }

        private void ApplyProperties()
        {
            SetWindowTextW(handle, _text);
            SetBounds();
            ShowWindow(handle, _visible ? 5 : 0);
            EnableWindow(handle, _enabled ? 1 : 0);
        }

        protected virtual void OnHandleCreated(EventArgs e)
        {
            StoreDesignBounds();

            // Created on a monitor whose DPI differs from the form's REFERENCE: the design bounds are reference
            // units, so this control scales itself here, once, before it is ever painted. Doing it in the form's
            // pass instead missed any control that already had a handle (measured: a button given a colour before
            // Show kept its reference x while its neighbours were scaled). Per control, at creation, nothing is
            // missed and there is one implementation for the window and for what it contains.
            // A THIN BAR SET BEFORE THE WINDOW EXISTED IS APPLIED HERE, which is the other order a script can write
            // (create then set, set then create), and the native bar has to go AFTER the window exists.
            if (_thinBar && handle != IntPtr.Zero) ApplyThinBar();
            // the two halves that need a WINDOW and can be set before it exists: a read-only text box and a RichEdit's
            // own background colour (a plain control's colour travels through the host's WM_CTLCOLOR answer instead)
            if (_readOnly && handle != IntPtr.Zero) SendMessageW(handle, EM_SETREADONLY, 1, 0);
            ApplyRichEditBackground();
            if (Parent is Form referenceForm && handle != IntPtr.Zero)
            {
                var referenceDpi = referenceForm.ReferenceDpiX;
                var hereDpi = (int)GetDpiForWindow(handle);
                if (referenceDpi > 0 && hereDpi > 0 && hereDpi != referenceDpi)
                    ScaleByDpi((float)hereDpi / referenceDpi, (float)hereDpi / referenceDpi);
            }
            if (this is ButtonBase && (_textColor.HasValue || _backgroundColor.HasValue))
                EnsureButtonOwnerdraw();

            // A control that appears under a form which is ALREADY dark is themed as it is created. DarkMode
            // themes the child windows that EXIST when it is assigned (EnumChildWindows over the form's handle),
            // so without this a form darkened before its controls were built leaves them light-themed, and a
            // light-themed check box draws BLACK text on the dark background. Measured on bc_compiler_ui.slc:
            // its DarkMode assignment had to be moved after the widgets for exactly that reason. This makes the
            // order stop mattering; assigning DarkMode later re-themes everything anyway, so this is the earlier half.
            if (handle != IntPtr.Zero && UnderDarkForm())
                ApplyThemeToSelf(true);
        }

        /// <summary>CLR_NONE (0xFFFFFFFF): the "no colour" sentinel a common control reads as UNSIGNED to hand one of
        /// its items back to the default theme. The cast is UNCHECKED because the constant does not fit a 32 bit nint,
        /// which is the CS8778 the build reported on these three sends; the value is a sentinel, not a number. It is
        /// <c>static readonly</c> rather than <c>const</c> only because a <c>nint</c> constant cannot be written this
        /// way (CS0133), and the VALUE is exactly what the three sends used to spell out inline.</summary>
        private static readonly nint CLR_NONE = unchecked((nint)0xFFFFFFFFu);

        private const uint BCM_GETIDEALSIZE = 0x1601;

        private bool _thinBar;
        private bool _barHot;
        private bool _barDragging;
        private int _barDragOffset;

        private int _lineHeightCached;
        private int _lineHeightDpi;
    private int _paints;
    /// <summary>Freeze this control's painting for a bulk change: call it before a fill or a swap and `EndUpdate`
    /// after, and the whole thing costs ONE repaint instead of one per appended item or per re-drawn child. It works
    /// for ANY control, a FORM included (a panel that rebuilds one of its children freezes ITSELF, so the empty band
    /// the old child leaves is never painted), which is why it lives here rather than on the two controls that happened
    /// to need it first.</summary>
    public void BeginUpdate()
    {
        if (handle == IntPtr.Zero) CreateControl();
        SendMessageW(handle, WM_SETREDRAW, 0, IntPtr.Zero);
    }

    /// <summary>Turns redraw back on and repaints ONCE. Safe without a matching `BeginUpdate`: the pair is a hint, not
    /// a lock, and an unmatched `EndUpdate` just repaints. The repaint is a `RedrawWindow` over the SUBTREE rather
    /// than an `InvalidateRect`, because a window left unpainted for the whole bracket shows stale pixels otherwise,
    /// and a child that changed while frozen is not covered by the parent's own invalid rectangle. No
    /// `RDW_UPDATENOW`: forcing a synchronous paint here is the flicker this pair exists to remove.</summary>
    public void EndUpdate()
    {
        if (handle == IntPtr.Zero) return;
        SendMessageW(handle, WM_SETREDRAW, 1, IntPtr.Zero);
        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_ERASE);
    }
    /// <summary>The watcher failure this control already reported (the same wording is one line).</summary>
    private string? _lastWatcherError;

    /// <summary>Run a `Clipboard.Watch` handler and REPORT its failure instead of letting it escape into the pump.
    /// This runs inside the window procedure, so a throw here is not a script's refusal: measured through the
    /// clipboard runner, a handler whose message building threw (`Brief` sliced past its own text) took the whole
    /// exception out through `Application.DoEvents()`, ended the tool's loop, and left its window on screen with
    /// nothing pumping, so every control stopped answering. The rule is the timer's (`ReportTickFailure`): say it
    /// ONCE per distinct wording, keep the watch registered, and let the run carry on.</summary>
    internal void RunClipboardWatcher(object watcher)
    {
        if (_interpreter is null || _interpreter.IsDisposed) return;
        try
        {
            _interpreter.InvokeScriptCallback(watcher, [this]);
            _lastWatcherError = null;                 // a notification that worked forgets the last failure
        }
        catch (ScriptException ex)
        {
            ReportWatcherFailure(ex.Message, ex.Line, ex.Column);
        }
        catch (ObjectDisposedException)
        {
            // the run ended under this notification: nothing left to report
        }
        catch (InvalidOperationException)
        {
            // the same shape one step further in (a released handle), also the run's end
        }
        catch (Exception ex)
        {
            ReportWatcherFailure(FriendlyExceptionMessage(ex));
        }
    }

    /// <summary>Say a watcher failure once, through the ENGINE's log, with the position when there is one (the
    /// timer's reporter, one class over: the position is part of the identity, so the same wording from another
    /// line is another failure).</summary>
    private void ReportWatcherFailure(string message, int line = 0, int column = 0)
    {
        var key = line > 0 ? $"{line}:{column}:{message}" : message;
        if (key == _lastWatcherError) return;
        _lastWatcherError = key;
        if (line > 0) Logging.Log.Error("Clipboard", message, line, column);
        else Logging.Log.Error("Clipboard", message);
    }

        private static nint _lightClientBrush;
        protected nint _ownClientBrush;   // this window's own BackgroundColor, if it has one
        protected uint _ownClientColor;

        /// Subclasses a NATIVE child window (BUTTON/EDIT/LISTBOX/... created via
        /// CreateWindowEx of a system class) so script-assignable mouse events
        /// (MouseDown/MouseMove/MouseUp/DoubleClick) fire, while every message is
        /// still forwarded to the original native procedure for normal behavior.
        /// </summary>
        internal bool _subclassed;                                    // this window is subclassed through comctl32
        private const nuint SubclassId = 0x5CC;                        // one id, installed once per window

        protected virtual void Dispose(bool disposing)
        {
            if (disposing) { _interpreter = null; }

            if (handle != IntPtr.Zero)
            {
                UnregisterHandle();
                DestroyWindow(handle);
                handle = IntPtr.Zero;
            }

            // Clean up color brush
            if (TextColorBrush != IntPtr.Zero)
            {
                DeleteObject(TextColorBrush);
                TextColorBrush = IntPtr.Zero;
                TextColorBrushColor = 0;
            }

            if (_currentFont != IntPtr.Zero)
            {
                DeleteObject(_currentFont);
                _currentFont = IntPtr.Zero;
            }

            if (_toolTipHandle != IntPtr.Zero)
            {
                DestroyWindow(_toolTipHandle);
                _toolTipHandle = IntPtr.Zero;
            }

            if (_gcHandle.IsAllocated)
                _gcHandle.Free();
        }

        ~Control() { Dispose(false); }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            // Do NOT call UnregisterHandle here
        }
    }
}

