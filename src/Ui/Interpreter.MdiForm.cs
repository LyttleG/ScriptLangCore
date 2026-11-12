namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    // ─────────────────────────────────────────────────────────────
    // MDI Frame, container with MDIClient area
    // ─────────────────────────────────────────────────────────────
    public sealed class MdiForm : IDisposable, IMdiFactories, IControlHost
    {
        /// <summary>Register a control with this MDI frame and hand it back, so each factory below is one line. A
        /// control made before the frame has a handle makes it appear first, the frame's own window is the parent,
        /// and the child is then placed without moving or resizing it.</summary>
        private T Adopt<T>(T ctrl) where T : Control
        {
            if (_handle == IntPtr.Zero) InitHandle();
            Controls.Add(ctrl);
            ctrl.SetParentHwnd(_handle);
            ctrl.CreateControl();
            SetWindowPos(ctrl.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            return ctrl;
        }

        private IntPtr _handle;
        private IntPtr _hClient;
        private IntPtr _parentHwnd;
        private string _text;
        private int _width = 640, _height = 320;
        private int _left = 100, _top = 100;
        private bool _visible;
        private bool _disposed;
        private int _initialDpiX = 96, _initialDpiY = 96;
        private bool _initialDpiStored;
        private int _currentDpiX = 96, _currentDpiY = 96;
        private int _designWidth, _designHeight;
        private bool _darkMode;
        /// <summary>The frame's theme state, the SAME struct Form uses, so Custom and System mean one thing across the
        /// engine rather than one thing per class.</summary>
        private ThemeState _themeState = new() { Mode = ThemeMode.Light };
        private IntPtr _darkBackgroundBrush, _darkEditBrush, _darkListBoxBrush, _darkButtonBrush;
        /// <summary>Which look this frame takes: Light, Dark, System (follows Windows' app-mode setting) or Custom
        /// (paints the palette below). Reading DarkMode afterwards answers the effective leg.</summary>
        public ThemeMode Theme
        {
            get => _themeState.Mode;
            set
            {
                if (_themeState.Mode == value) return;
                _themeState.Mode = value;
                ApplyThemeState();
            }
        }

        /// <summary>The dark leg, the same shorthand Form has: DarkMode = true is Theme = ThemeMode.Dark.</summary>
        internal bool DarkMode
        {
            get => _darkMode;
            set => Theme = value ? ThemeMode.Dark : ThemeMode.Light;
        }
        private IntPtr _paletteBrush;
        private uint _paletteBrushColor;


        /// <summary>Resolves the mode to the leg the native parts use, then re-themes the frame and its children.</summary>
        private void ApplyThemeState()
        {
            var dark = _themeState.IsDark();
            _darkMode = dark;
            if (_handle == IntPtr.Zero) return;
            // Same rule as the Form's: every mode change re-applies the theme to the frame and to its controls, not
            // only one that moved the leg.
            ApplyDarkModeAttributes();
            // AND ITS DOCUMENT WINDOWS, which carry their OWN dark flag and their own CTLCOLOR palette: `CreateMdiChild`
            // sets it one way only, so a frame switched back to light left every document (and every control inside one)
            // serving the dark brush (measured: a child's text box read 0x303030 in a LIGHT frame, where a plain form's
            // read 0xFFFFFF). One rule for the frame and its documents, applied on the way to either leg.
            foreach (var document in DocumentWindows())
                document.SetThemeState(in _themeState);   // the state, not just the leg: a palette reaches a document too
            InvalidateRect(_handle, IntPtr.Zero, true);
            RedrawWindow(_handle, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        public uint? ThemeBackground { get => _themeState.Palette.Background; set { _themeState.Palette.Background = value; ApplyThemeState(); } }
        public uint? ThemeText { get => _themeState.Palette.Text; set { _themeState.Palette.Text = value; ApplyThemeState(); } }
        public uint? ThemeBorder { get => _themeState.Palette.Border; set { _themeState.Palette.Border = value; ApplyThemeState(); } }
        public uint? ThemeSelection { get => _themeState.Palette.Selection; set { _themeState.Palette.Selection = value; ApplyThemeState(); } }
        public uint? ThemeDisabled { get => _themeState.Palette.Disabled; set { _themeState.Palette.Disabled = value; ApplyThemeState(); } }

        /// <summary>This frame's state for a control that lives in it, read through
        /// <see cref="Control.ThemeStateOf"/> so the palette and the leg resolve in ONE place.</summary>
        internal ThemeState ThemeStateValue => _themeState;

        /// <summary>The window property carrying this instance, so its own message router, its MDI client and a
        /// control looking for its host all find it through the WINDOW rather than through a table. One spelling,
        /// because a lookup keyed on a string that another member sets is exactly what drifts.</summary>
        internal const string WindowProp = "MDIFRAME_GC";

        private Interpreter? _interpreter;
        private static bool _classRegistered;
        private static readonly string FrameClassName = "ScriptMdiFrame";
        private static WndProcDelegate? _frameWndProcDelegate;
        private static IntPtr _frameWndProcPtr;
        private static WndProcDelegate? _mdiClientWndProcDelegate;
        private static IntPtr _mdiClientWndProcPtr;
        private static int _newMdiDpiX, _newMdiDpiY;
        private IntPtr _oldMdiClientProc;

        private static bool ScaleMdiChildrenEnum(IntPtr child, IntPtr _)
        {
            var prop = GetPropW(child, MdiChild.WindowProp);
            if (prop != IntPtr.Zero)
            {
                var mdiChild = (MdiChild)GCHandle.FromIntPtr(prop).Target!;
                var sx = (float)_newMdiDpiX / mdiChild._creationDpiX;
                var sy = (float)_newMdiDpiY / mdiChild._creationDpiX;
                mdiChild.ScaleControls(sx, sy);
            }
            return true;
        }

        public IntPtr Handle => _handle;

        public string? Text
        {
            get => _text;
            set
            {
                _text = value ?? "";
                if (_handle != IntPtr.Zero) SetWindowTextW(_handle, _text);
            }
        }

        public int Width { get => _width; set => _width = value; }
        public int Height { get => _height; set => _height = value; }
        public int Left { get => _left; set => _left = value; }
        public int Top { get => _top; set => _top = value; }
        public bool Visible => _visible;
        internal Interpreter? ScriptHost { set => _interpreter = value; }

        /// <summary>The WINDOW MESSAGES this host takes, in the same shape a Control answers them and over the SAME
        /// one implementation (SendMessageScript/PostMessageScript), which a Control's own SendMessage already
        /// calls: an MDI host is not a Control, so it needs the door, and it must not have a second core.</summary>
        public long SendMessage(object msg, object wParam, object lParam)
            => _handle == IntPtr.Zero ? 0L
               : SendMessageScript((long)_handle, Convert.ToInt64(msg), Convert.ToInt64(wParam), ToNInt(lParam).ToInt64());

        public bool PostMessage(object msg, object wParam, object lParam)
            => _handle != IntPtr.Zero
               && PostMessageScript((long)_handle, Convert.ToInt64(msg), Convert.ToInt64(wParam), ToNInt(lParam).ToInt64());

        /// <summary>Place this host's window, in PIXELS, over the same core Control.MoveWindow uses. Unlike a
        /// control it records nothing afterwards: its creation facts are what a DOCUMENT restores from, while this
        /// says where the window is now.</summary>
        public void MoveWindow(int left, int top, int width, int height, bool repaint = true)
        {
            if (_handle != IntPtr.Zero) Interpreter.MoveWindow(_handle, left, top, width, height, repaint);
        }
        public List<Control> Controls { get; }
        public ScriptFunction? Load { get; set; }
        public ScriptFunction? Closing { get; set; }
        public ScriptFunction? Closed { get; set; }
        public ScriptFunction? ResizeEvent { get; set; }

        public MdiForm(string? title)
        {
            _text = title ?? ""; Controls = []; RegisterFrameClass();
            // The interpreter is captured HERE as well as through ScriptHost, because ScriptHost is only set by the
            // create path a script calls: a frame built from a DOCUMENT had none, and InvokeSafely drops every event
            // of a host with no interpreter, in silence. Measured: a rebuilt frame's ResizeEvent fired 0 times while
            // the same handler on a script-created frame fired, which is a dead event rather than a broken handler.
            _interpreter = Interpreter.Current;
        }

        internal MdiForm(string? title, IntPtr parentHwnd)
        {
            _text = title ?? ""; _parentHwnd = parentHwnd; Controls = []; RegisterFrameClass();
        }

        private static void RegisterFrameClass()
        {
            if (_classRegistered) return;
            _frameWndProcDelegate = FrameWndProcRouter;
            _frameWndProcPtr = Marshal.GetFunctionPointerForDelegate(_frameWndProcDelegate);
            var wc = new Wndclassexw
            {
                cbSize = (uint)Marshal.SizeOf<Wndclassexw>(),
                style = 0,
                lpfnWndProc = _frameWndProcPtr,
                hInstance = GetModuleHandleW(null!),
                hIcon = IntPtr.Zero,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = 16,
                lpszClassName = Marshal.StringToHGlobalUni(FrameClassName)
            };
            if (RegisterClassExW(ref wc) == 0) throw new ScriptException($"RegisterClassExW for {FrameClassName} failed: {Marshal.GetLastWin32Error()}");
            _classRegistered = true;
        }

        private static IntPtr FrameWndProcRouter(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var prop = GetPropW(hwnd, WindowProp);
            if (prop != IntPtr.Zero && GCHandle.FromIntPtr(prop).Target is MdiForm frame) { return frame.WndProc(hwnd, msg, wParam, lParam); }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private static IntPtr MdiClientWndProcRouter(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var prop = GetPropW(hwnd, "MDIFRAME_MDICL_GC");
            if (prop != IntPtr.Zero && GCHandle.FromIntPtr(prop).Target is MdiForm frame)
            {
                if (msg == WM_NCDESTROY)
                {
                    var result = CallWindowProcW(frame._oldMdiClientProc, hwnd, msg, wParam, lParam);
                    RemovePropW(hwnd, "MDIFRAME_MDICL_GC"); ;
                    return result;
                }

                if (msg == WM_ERASEBKGND)
                {
                    return frame._darkMode
                        ? Control.FillClientDark(hwnd, wParam, ref frame._darkBackgroundBrush)
                        : Control.FillClientLight(hwnd, wParam);
                }
                return CallWindowProcW(frame._oldMdiClientProc, hwnd, msg, wParam, lParam);
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        public void InitHandle() { if (_handle != IntPtr.Zero) return; _visible = false; CreateHandle(); }
        public void Show() { if (_handle == IntPtr.Zero) CreateHandle(); _visible = true; ShowWindow(_handle, 5); RedrawWindow(_handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW); }

        private void CreateHandle()
        {
            var hInstance = GetModuleHandleW(null!);
            var style = _parentHwnd != IntPtr.Zero
                ? (WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN)
                : (WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN);
            _handle = CreateWindowExW(0, FrameClassName, _text, style, _left, _top, _width, _height, _parentHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_handle == IntPtr.Zero) throw new ScriptException($"CreateWindowEx MdiFrame failed: {Marshal.GetLastWin32Error()}");
            var gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            SetPropW(_handle, WindowProp, GCHandle.ToIntPtr(gcHandle));
            // Store initial DPI and design size
            if (!_initialDpiStored)
            {
                _initialDpiX = (int)GetDpiForWindow(_handle);
                _initialDpiY = _initialDpiX;
                _currentDpiX = _initialDpiX;
                _currentDpiY = _initialDpiY;
                _initialDpiStored = true;
                _designWidth = _width;
                _designHeight = _height;
            }
            GetClientRect(_handle, out var rc);
            var cc = new CLIENTCREATESTRUCT { hWindowMenu = IntPtr.Zero, idFirstChild = 16566 };
            var ccPtr = Marshal.AllocHGlobal(Marshal.SizeOf<CLIENTCREATESTRUCT>());
            try { Marshal.StructureToPtr(cc, ccPtr, false); _hClient = CreateWindowExW(WS_EX_CLIENTEDGE, "MDIClient", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | WS_VSCROLL | WS_HSCROLL, 0, 0, rc.Right - rc.Left, rc.Bottom - rc.Top, _handle, IntPtr.Zero, hInstance, ccPtr); }
            finally { Marshal.FreeHGlobal(ccPtr); }
            if (_hClient == IntPtr.Zero) { DestroyWindow(_handle); _handle = IntPtr.Zero; throw new ScriptException($"CreateWindowEx MDIClient failed: {Marshal.GetLastWin32Error()}"); }
            // Subclass MDIClient for dark background
            SetPropW(_hClient, "MDIFRAME_MDICL_GC", GCHandle.ToIntPtr(gcHandle));
            if (_mdiClientWndProcDelegate == null)
            {
                _mdiClientWndProcDelegate = MdiClientWndProcRouter;
                _mdiClientWndProcPtr = Marshal.GetFunctionPointerForDelegate(_mdiClientWndProcDelegate);
            }
            _oldMdiClientProc = SetWindowLongPtrW(_hClient, GWLP_WNDPROC, _mdiClientWndProcPtr);
            NativeApp.RegisterForm(_handle);
            if (_darkMode) ApplyDarkModeAttributes();
            else ApplyNonClientColors(_handle, ThemeStateValue);   // a LIGHT Custom palette has a caption colour too
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // C: WM_CTLCOLOR*, dark brush per control type
                if (_darkMode)
                {
                    switch (msg)
                    {
                        case WM_CTLCOLORMSGBOX:
                        case WM_CTLCOLOREDIT:
                        case WM_CTLCOLORLISTBOX:
                        case WM_CTLCOLORBTN:
                        case WM_CTLCOLORDLG:
                        case WM_CTLCOLORSCROLLBAR:
                        case WM_CTLCOLORSTATIC:
                            return HandleCtlColor(wParam, msg);
                    }
                }

                // D: WM_ERASEBKGND, fill dark background
                if (msg == WM_ERASEBKGND)
                {
                    return _darkMode
                        ? Control.FillClientDark(hwnd, wParam, ref _darkBackgroundBrush)
                        : Control.FillClientLight(hwnd, wParam);
                }

                switch (msg)
                {
                    case WM_DPICHANGED:
                        {
                            var newDpiX = Loword(wParam);
                            var newDpiY = Hiword(wParam);
                            var scaleX = (float)newDpiX / _initialDpiX;
                            var scaleY = (float)newDpiY / _initialDpiY;
                            _currentDpiX = newDpiX;
                            _currentDpiY = newDpiY;

                            // Apply lParam rect for new DPI window size
                            if (lParam != IntPtr.Zero)
                            {
                                var rect = Marshal.PtrToStructure<Rect>(lParam);
                                SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top,
                                             rect.Right - rect.Left, rect.Bottom - rect.Top,
                                             SWP_NOZORDER | SWP_NOACTIVATE);
                            }

                            // Let DefFrameProcW handle DPI transition for frame + MDI children
                            DefFrameProcW(hwnd, _hClient, msg, wParam, lParam);

                            // Scale frame controls AFTER DefFrameProcW (it repositions them)
                            foreach (var ctrl in Controls)
                                ctrl.ScaleByDpi(scaleX, scaleY);

                            // Now scale MDI children controls to match their new window size
                            _newMdiDpiX = newDpiX;
                            _newMdiDpiY = newDpiY;
                            if (_hClient != IntPtr.Zero)
                                EnumChildWindows(_hClient, ScaleMdiChildrenEnum, IntPtr.Zero);

                            // Update fonts
                            // The MDI child window itself takes the DPI default; its CONTROLS re-apply their own font, so a
                            // control with a FontSize/FontName/FontBold set keeps it across a DPI change, exactly as on the Form.
                            var newFont = CreateDpiFont(newDpiX);
                            SendMessageW(hwnd, WM_SETFONT, (nuint)newFont, 1);
                            EnumChildWindows(hwnd, static (child, _) =>
                            {
                                Control.FromHandle(child)?.ApplyFont();
                                return true;
                            }, IntPtr.Zero);

                            // Rescale non-client area without fighting over size
                            RefreshFrameAndTheme(hwnd);
                            foreach (var ctrl in Controls)
                                RefreshFrameAndTheme(ctrl.Handle);

                            // RDW_FRAME for the same reason the Form's arm states: the frame's bars are
                            // measured for the DPI that just changed, and a recalc does not repaint them.
                            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                                         RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
                            return 0; // Handled, prevent DefFrameProcW double-scaling
                        }

                    case WM_NCDESTROY: { var prop = GetPropW(hwnd, WindowProp); if (prop != IntPtr.Zero) { if (_hClient != IntPtr.Zero) RemovePropW(_hClient, "MDIFRAME_MDICL_GC"); GCHandle.FromIntPtr(prop).Free(); RemovePropW(hwnd, WindowProp); } _handle = IntPtr.Zero; _hClient = IntPtr.Zero; break; }
                    case WM_SIZE: { var cw = (int)(lParam & 0xFFFF); var ch = (int)(lParam >> 16); _width = cw; _height = ch; OnResizeEvent(cw, ch); if (_hClient != IntPtr.Zero) { var topOffset = 0; foreach (var ctrl in Controls) { var ctrlBottom = ctrl.Top + ctrl.Height; if (ctrlBottom > topOffset) topOffset = ctrlBottom; } var mdiTop = topOffset > 0 ? topOffset + 5 : 0; var mdiHeight = ch - mdiTop; if (mdiHeight < 0) mdiHeight = 0; SetWindowPos(_hClient, IntPtr.Zero, 0, mdiTop, cw, mdiHeight, SWP_NOZORDER); } return IntPtr.Zero; }
                    case WM_COMMAND: { var code = Hiword(wParam); var hwndCtrl = lParam; if (code == BN_CLICKED && hwndCtrl != IntPtr.Zero && Control.HandleMap.TryGetValue(hwndCtrl, out var ctrl)) { if (ctrl is ButtonBase btnBase) btnBase.RaiseClick(); else if (ctrl is GenericControl genCtrl) genCtrl.RaiseClick(); } break; }
                    case WM_DESTROY: NativeApp.UnregisterForm(hwnd); return IntPtr.Zero;
                    case WM_CLOSE: { var ce = new CancelEventArgs(); InvokeSafely(this, nameof(Closing), Closing, this, ce); if (ce.Cancel) return IntPtr.Zero; DestroyWindow(hwnd); InvokeSafely(this, nameof(Closed), Closed, this, EventArgs.Empty); return IntPtr.Zero; }
                }
                return DefFrameProcW(hwnd, _hClient, msg, wParam, lParam);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[MdiForm] EXCEPTION in WndProc (msg=0x{msg:X}): {ex}"); return DefFrameProcW(hwnd, _hClient, msg, wParam, lParam); }
        }

        private void OnResizeEvent(int w, int h) => InvokeSafely(this, nameof(ResizeEvent), ResizeEvent, this, new ResizeEventArgs(w, h));
        private void InvokeSafely(object? owner, string? eventName, ScriptFunction? fn, params object?[] args)
        {
            Control.StampEventName(owner, eventName, args);
            if (fn == null || _interpreter == null) return;
            try { _interpreter.InvokeScriptFunction(fn, args, 0, 0); } catch { }
        }

        private void ApplyDarkModeAttributes()
        {
            var dark = _darkMode ? 1 : 0;
            DwmSetWindowAttribute(_handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            // The frame's own caption, the same rule as the Form's: the leg says dark or light, and a Custom
            // palette is a COLOUR, so both travel.
            ApplyNonClientColors(_handle, ThemeStateValue);
            if (_darkMode)
            {
                // Frame controls only, MDI children use DefMDIChildProcW
                // which paints its own background and conflicts with DarkMode_Explorer.
                foreach (var ctrl in Controls)
                {
                    // Same rule as the Form's pass: a control whose style carries a BAR keeps the flat bar.
                    if (ctrl.Handle != IntPtr.Zero && !HasNonClientBar(ctrl.Handle))
                        SetWindowTheme(ctrl.Handle, "DarkMode_Explorer", "");
                }
            }
        }

        // ── IControlHost: a document walk reaches a frame through these four, explicitly, so none of them is a
        // script-visible member. The factories are the existing public ones: IMdiFactories adds no member. ──
        void IControlHost.SetHostRow(string name, object? value) => ApplyClrRow(this, name, value);
        IEnumerable<Control> IControlHost.HostChildren() => Controls;
        Control? IControlHost.HostFind(string name) => FindIn(Controls, name);
        Control IControlHost.CreateKind(in ControlSpec spec) => CreateKindOn(this, in spec);

        /// <param name="configure">Runs on the new document window before it is returned, the same trailing callback the
        /// factories take. It does NOT run when the frame has no client window and there is no child to configure
        /// (that call answers null), so a script tests the answer rather than relying on the callback.</param>
        public MdiChild? CreateMdiChild(string title, int x = -1, int y = -1, int w = -1, int h = -1, int positionMode = 0, Action<MdiChild>? configure = null) { if (_hClient == IntPtr.Zero) return null; var child = new MdiChild(_hClient, title, x, y, w, h, positionMode, _interpreter) { Owner = this }; child.SetThemeState(in _themeState); return child.WithInit(configure); }

        /// <summary>This frame's DOCUMENT WINDOWS, which nothing tracks: CreateMdiChild hands one back and the frame
        /// keeps no list, so the only walk that reaches them is the one the DPI path already uses, over the window
        /// property each child's own proc reads (MdiChild.WindowProp). The order is the MDI client's, which is what a
        /// document wants: document windows have no script-visible order of their own.</summary>
        internal List<MdiChild> DocumentWindows()
        {
            var found = new List<MdiChild>();
            if (_hClient == IntPtr.Zero) return found;
            EnumChildWindows(_hClient, (child, _) =>
            {
                var prop = GetPropW(child, MdiChild.WindowProp);
                if (prop != IntPtr.Zero && GCHandle.FromIntPtr(prop).Target is MdiChild window) found.Add(window);
                return true;
            }, IntPtr.Zero);
            // REVERSED, and that is what makes the document stable under a round trip: the client lists its windows
            // with the MOST RECENT first, so a document written in that order would be rebuilt with the newest
            // created first, which puts the OLDEST on top, and the order would then flip on every round trip.
            // Oldest first is the order a reader recreates them in, and the order the export keeps writing.
            found.Reverse();
            return found;
        }
        public void TileChildren(bool horizontally) { if (_hClient == IntPtr.Zero) return; SendMessageW(_hClient, WM_MDITILE, horizontally ? MDITILE_HORIZONTAL : (nuint)MDITILE_VERTICAL, 0); }
        public void CascadeChildren() { if (_hClient == IntPtr.Zero) return; SendMessageW(_hClient, WM_MDICASCADE, 0, 0); }
        public void ArrangeIcons() { if (_hClient == IntPtr.Zero) return; SendMessageW(_hClient, WM_MDIICONARRANGE, 0, 0); }
        public IntPtr GetActiveChild() { if (_hClient == IntPtr.Zero) return IntPtr.Zero; return SendMessageW(_hClient, WM_MDIGETACTIVE, 0, 0); }

        // ── Frame-level control factories ──
        // Same trailing `configure` callback as `Form`'s factories (`WithInit`), so a control is created and finished in
        // one statement here too.
        public Label CreateLabel(string text, int x, int y, int width, int height, Action<Label>? configure = null) => Adopt(new Label(text, x, y, width, height)).WithInit(configure);

        public Button CreateButton(string text, int x, int y, int width, int height, Action<Button>? configure = null) => Adopt(new Button(text, x, y, width, height)).WithInit(configure);

        public TextBox CreateTextBox(string text, int x, int y, int width, int height, Action<TextBox>? configure = null) => Adopt(new TextBox(text, x, y, width, height)).WithInit(configure);
        public RichTextBox CreateRichTextBox(string text, int x, int y, int width, int height, Action<RichTextBox>? configure = null) => Adopt(new RichTextBox(text, x, y, width, height)).WithInit(configure);

        public CheckBox CreateCheckBox(string text, int x, int y, int width, int height, bool isChecked = false, Action<CheckBox>? configure = null) => Adopt(new CheckBox(text, x, y, width, height) { Checked = isChecked }).WithInit(configure);

        public RadioButton CreateRadioButton(string text, int x, int y, int width, int height, bool isChecked = false, Action<RadioButton>? configure = null) => Adopt(new RadioButton(text, x, y, width, height) { Checked = isChecked }).WithInit(configure);

        public ListBox CreateListBox(int x, int y, int width, int height, Action<ListBox>? configure = null) => Adopt(new ListBox(x, y, width, height)).WithInit(configure);

        public ComboBox CreateComboBox(int x, int y, int width, int height, Action<ComboBox>? configure = null) => Adopt(new ComboBox(x, y, width, height)).WithInit(configure);

        public ListView CreateListView(int x, int y, int width, int height, Action<ListView>? configure = null) => Adopt(new ListView(x, y, width, height)).WithInit(configure);

        public TreeView CreateTreeView(int x, int y, int width, int height, Action<TreeView>? configure = null) => Adopt(new TreeView(x, y, width, height)).WithInit(configure);

        public DateTimePicker CreateDateTimePicker(int x, int y, int width, int height, Action<DateTimePicker>? configure = null) => Adopt(new DateTimePicker(x, y, width, height)).WithInit(configure);

        public TrackBar CreateTrackBar(int x, int y, int width, int height, Action<TrackBar>? configure = null) => Adopt(new TrackBar(x, y, width, height)).WithInit(configure);

        private IntPtr HandleCtlColor(IntPtr hdc, uint msg)
            => ThemedCtlColor(hdc, msg);

        /// <summary>This host's own state, so a Custom palette is served as well as the dark leg.</summary>
        private IntPtr ThemedCtlColor(IntPtr hdc, uint msg)
            => Control.HandleCtlColorThemed(hdc, msg, in _themeState,
                ref _darkBackgroundBrush, ref _darkEditBrush, ref _darkListBoxBrush, ref _darkButtonBrush,
                ref _paletteBrush, ref _paletteBrushColor);

        public void Close() => SendMessageW(_handle, WM_CLOSE, 0, IntPtr.Zero);
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Control.DisposeDarkBrushes(ref _darkBackgroundBrush, ref _darkEditBrush, ref _darkListBoxBrush, ref _darkButtonBrush);

            if (_handle != IntPtr.Zero)
            {
                var prop = GetPropW(_handle, WindowProp);
                if (prop != IntPtr.Zero) { GCHandle.FromIntPtr(prop).Free(); RemovePropW(_handle, WindowProp); }
                DestroyWindow(_handle); _handle = IntPtr.Zero;
            }
        }
    }
}

