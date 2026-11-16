namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    // ─────────────────────────────────────────────────────────────
    // MDI Child window, document window inside an MDI frame
    // ─────────────────────────────────────────────────────────────
    public sealed class MdiChild : IDisposable, IMdiFactories, IControlHost
    {
        /// <summary>Register a control with this document window and hand it back, so each factory below is one
        /// line. An MDI child parents its controls to its own window explicitly, which is the difference from a
        /// plain form.</summary>
        private T Adopt<T>(T ctrl) where T : Control
        {
            Controls.Add(ctrl);
            ctrl.SetParentHwnd(_handle);
            ctrl.CreateControl();
            return ctrl;
        }

        private readonly IntPtr _hClient;
        private IntPtr _handle;
        private string _text;
        private bool _disposed;
        /// <summary>True when this document window takes the dark leg. Kept as a field-backed property so
        /// SetDarkMode and a script's assignment stay one path.</summary>
        internal bool DarkMode
        {
            get => _dark;
            set => _dark = value;
        }
        private bool _dark;

        /// <summary>Which look this child takes. A MODE, because the engine spells a theme once: Dark and Light set
        /// the leg directly, and System resolves Windows' own app-mode setting here (a child has no palette of its
        /// own; its colours come from the frame).</summary>
        public ThemeMode Theme
        {
            get => DarkMode ? ThemeMode.Dark : ThemeMode.Light;
            set => SetDarkMode(value == ThemeMode.Dark || (value == ThemeMode.System && ThemeState.SystemIsDark()));
        }

        /// <summary>This document window's state for a control that lives in it. A child has no palette of its
        /// own (its colours come from the frame), so this carries the MODE alone, and a control reads it through
        /// <see cref="Control.ThemeStateOf"/> rather than reaching for the frame behind it.</summary>
        internal ThemeState ThemeStateValue => new() { Mode = Theme };

        /// <summary>The window property carrying this instance, so its own message router and a control looking for
        /// its host both find it through the WINDOW. One spelling, because a lookup keyed on a string that another
        /// member sets is exactly what drifts.</summary>
        internal const string WindowProp = "MDICHILD_GC";

        internal int _creationDpiX = 96;
        private Interpreter? _interpreter;
        private IntPtr _darkBackgroundBrush, _darkEditBrush, _darkListBoxBrush, _darkButtonBrush;
        private static bool _classRegistered;
        private static readonly string ChildClassName = "ScriptMdiChild";
        private static IntPtr _childWndProcPtr;
        private static WndProcDelegate? _childWndProcDelegate;

        public IntPtr Handle => _handle;
        public string? Text
        {
            get => _text;
            set { _text = value ?? ""; SetWindowTextW(_handle, _text); }
        }
        public List<Control> Controls { get; }

        // ── IControlHost: a document walk reaches a document window through these four, explicitly, so none of them
        // is a script-visible member. The factories are the existing public ones: IMdiFactories adds no member. ──
        void IControlHost.SetHostRow(string name, object? value) => ApplyClrRow(this, name, value);
        IEnumerable<Control> IControlHost.HostChildren() => Controls;
        Control? IControlHost.HostFind(string name) => FindIn(Controls, name);
        Control IControlHost.CreateKind(in ControlSpec spec) => CreateKindOn(this, in spec);

        /// <summary>The frame this window belongs to, which is what keeps it ALIVE: a document window is parented to
        /// an MDI client and a client belongs to a frame, so a window read out of a document that has no frame of its
        /// own owns the one the reader made for it. Internal, so it is neither script-visible nor a row.</summary>
        internal MdiForm? Owner { get; set; }

        /// <summary>The rect this window was CREATED with. It went to CreateMdiChild and nothing kept it, which is
        /// why a document could not put a window back where it was: these are the same shape
        /// GenericControl.ClassName has, a creation fact a reader needs and a script can only read.</summary>
        public int Left => _left;
        public int Top => _top;
        public int Width => _width;
        public int Height => _height;
        private int _left, _top, _width, _height;

        /// <summary>Put this window on screen, which for a window means showing its FRAME: a document window is
        /// parented to an MDI client, and a client is shown by the frame that owns it. A window a frame made is
        /// reached through that frame's own Show, and the implicit frame a root MdiChild document gets is reached
        /// exactly here, since nothing else in a script holds it.</summary>
        public void Show() => Owner?.Show();

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

        public ScriptFunction? Load { get; set; }
        public ScriptFunction? Closing { get; set; }
        public ScriptFunction? Closed { get; set; }
        public ScriptFunction? ResizeEvent { get; set; }

        private static void RegisterChildClass()
        {
            if (_classRegistered) return;

            _childWndProcDelegate = new WndProcDelegate(ChildWndProcRouter);
            _childWndProcPtr = Marshal.GetFunctionPointerForDelegate(_childWndProcDelegate);

            var wc = new Wndclassexw
            {
                cbSize = (uint)Marshal.SizeOf<Wndclassexw>(),
                style = 0,
                lpfnWndProc = _childWndProcPtr,
                hInstance = GetModuleHandleW(null!),
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = 6,
                lpszClassName = Marshal.StringToHGlobalUni(ChildClassName)
            };
            if (RegisterClassExW(ref wc) == 0)
                throw new ScriptException($"RegisterClassExW for {ChildClassName} failed: {Marshal.GetLastWin32Error()}");
            _classRegistered = true;
        }

        private static IntPtr ChildWndProcRouter(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var prop = GetPropW(hwnd, WindowProp);
            if (prop != IntPtr.Zero)
            {
                var child = (MdiChild)GCHandle.FromIntPtr(prop).Target!;
                return child.WndProc(hwnd, msg, wParam, lParam);
            }
            return DefMDIChildProcW(hwnd, msg, wParam, lParam);
        }

        internal MdiChild(IntPtr hClient, string? title, int x, int y, int w, int h, int positionMode = 0, Interpreter? interpreter = null)
        {
            _hClient = hClient;
            _text = title ?? "Untitled";
            _left = x; _top = y; _width = w; _height = h;
            Controls = [];
            // Current as the fallback, for the same reason the frame captures it: a window the READER makes passes no
            // interpreter, and its events would otherwise be dropped in silence.
            _interpreter = interpreter ?? Interpreter.Current;

            RegisterChildClass();

            var mcs = new MDICREATESTRUCT
            {
                szClass = ChildClassName,
                szTitle = _text,
                hOwner = GetModuleHandleW(null!),
                x = x == -1 ? unchecked((int)0x80000000) : x,
                y = y == -1 ? unchecked((int)0x80000000) : y,
                cx = w == -1 ? unchecked((int)0x80000000) : w,
                cy = h == -1 ? unchecked((int)0x80000000) : h
            };

            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<MDICREATESTRUCT>());
            try
            {
                Marshal.StructureToPtr(mcs, ptr, false);
                _handle = SendMessageW(_hClient, WM_MDICREATE, 0, ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (_handle == IntPtr.Zero)
                throw new ScriptException($"WM_MDICREATE failed: {Marshal.GetLastWin32Error()}");

            // Store GCHandle for WndProc routing
            var gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            SetPropW(_handle, WindowProp, GCHandle.ToIntPtr(gcHandle));

            // Store creation DPI for proper design-bound scaling
            _creationDpiX = (int)GetDpiForWindow(_handle);

            // Apply position mode
            if (positionMode == 2) // Maximized
                SendMessageW(_hClient, WM_MDIMAXIMIZE, (nuint)_handle, 0);
            else if (positionMode == 1) // Tile (with existing children)
                SendMessageW(_hClient, WM_MDITILE, 0, 0);
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // C: WM_CTLCOLOR*, dark brush per control type
                if (DarkMode)
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
                    return DarkMode
                        ? Control.FillClientDark(hwnd, wParam, ref _darkBackgroundBrush)
                        : Control.FillClientLight(hwnd, wParam);
                }

                // D2: WM_CLOSE, fire Closing/Closed events
                if (msg == WM_CLOSE)
                {
                    var ce = new CancelEventArgs();
                    InvokeSafely(this, nameof(Closing), Closing, this, ce);
                    if (!ce.Cancel)
                    {
                        InvokeSafely(this, nameof(Closed), Closed, this, EventArgs.Empty);
                        return DefMDIChildProcW(hwnd, msg, wParam, lParam);
                    }
                    return IntPtr.Zero; // Cancelled
                }

                // D3: WM_SIZE, fire ResizeEvent
                if (msg == WM_SIZE)
                {
                    var w = (int)(lParam & 0xFFFF);
                    var h_ = (int)(lParam >> 16);
                    InvokeSafely(this, nameof(ResizeEvent), ResizeEvent, this, new ResizeEventArgs(w, h_));
                    return DefMDIChildProcW(hwnd, msg, wParam, lParam);
                }

                if (msg == WM_NCDESTROY)
                {
                    var prop = GetPropW(hwnd, WindowProp);
                    if (prop != IntPtr.Zero)
                    {
                        GCHandle.FromIntPtr(prop).Free();
                        RemovePropW(hwnd, WindowProp);
                    }
                    _handle = IntPtr.Zero;
                }
                else if (msg == WM_COMMAND)
                {
                    var code = Hiword(wParam);
                    var hwndCtrl = lParam;
                    if (code == BN_CLICKED && hwndCtrl != IntPtr.Zero && Control.HandleMap.TryGetValue(hwndCtrl, out var ctrl))
                    {
                        if (ctrl is ButtonBase btnBase)
                            btnBase.RaiseClick();
                        else if (ctrl is GenericControl genCtrl)
                            genCtrl.RaiseClick();
                    }
                }
                return DefMDIChildProcW(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MDIChild] EXCEPTION in WndProc (msg=0x{msg:X}): {ex}");
                return DefMDIChildProcW(hwnd, msg, wParam, lParam);
            }
        }

        // ── Event helper (same pattern as Form) ──
        private void InvokeSafely(object? owner, string? eventName, ScriptFunction? fn, params object?[] args)
        {
            Control.StampEventName(owner, eventName, args);
            if (fn == null || _interpreter == null) return;
            try { _interpreter.InvokeScriptFunction(fn, args, 0, 0); }
            catch
            {
                // ignored
            }
        }

        // ── Control factories ──
        // Every factory takes an optional trailing `configure` callback, the shape `Form`'s factories document
        // (`WithInit`): it runs AFTER the control is adopted, so the control is parented and has its window, and it
        // receives the CONCRETE type, so `.Items`, `.Checked`, `.Text` and a handler all work without a cast.
        public Label CreateLabel(string text, int x, int y, int width, int height, Action<Label>? configure = null) => Adopt(new Label(text, x, y, width, height)).WithInit(configure);

        public TextBox CreateTextBox(string text, int x, int y, int width, int height, Action<TextBox>? configure = null) => Adopt(new TextBox(text, x, y, width, height)).WithInit(configure);
        public RichTextBox CreateRichTextBox(string text, int x, int y, int width, int height, Action<RichTextBox>? configure = null) => Adopt(new RichTextBox(text, x, y, width, height)).WithInit(configure);

        public Button CreateButton(string text, int x, int y, int width, int height, Action<Button>? configure = null) => Adopt(new Button(text, x, y, width, height)).WithInit(configure);

        public CheckBox CreateCheckBox(string text, int x, int y, int width, int height, bool isChecked = false, Action<CheckBox>? configure = null) => Adopt(new CheckBox(text, x, y, width, height) { Checked = isChecked }).WithInit(configure);

        public ComboBox CreateComboBox(int x, int y, int width, int height, Action<ComboBox>? configure = null) => Adopt(new ComboBox(x, y, width, height)).WithInit(configure);

        public ListBox CreateListBox(int x, int y, int width, int height, Action<ListBox>? configure = null) => Adopt(new ListBox(x, y, width, height)).WithInit(configure);

        public TrackBar CreateTrackBar(int x, int y, int width, int height, Action<TrackBar>? configure = null) => Adopt(new TrackBar(x, y, width, height)).WithInit(configure);

        public RadioButton CreateRadioButton(string text, int x, int y, int width, int height, bool isChecked = false, Action<RadioButton>? configure = null) => Adopt(new RadioButton(text, x, y, width, height) { Checked = isChecked }).WithInit(configure);

        public ListView CreateListView(int x, int y, int width, int height, Action<ListView>? configure = null) => Adopt(new ListView(x, y, width, height)).WithInit(configure);

        public TreeView CreateTreeView(int x, int y, int width, int height, Action<TreeView>? configure = null) => Adopt(new TreeView(x, y, width, height)).WithInit(configure);

        public DateTimePicker CreateDateTimePicker(int x, int y, int width, int height, Action<DateTimePicker>? configure = null) => Adopt(new DateTimePicker(x, y, width, height)).WithInit(configure);

        internal void SetDarkMode(bool dark)
        {
            DarkMode = dark;
        }

        /// <summary>The WHOLE theme state, palette included: a document used to be told only DARK or not, so a Custom
        /// frame's palette never reached its documents (measured: a child's text box read 0x303030 in a frame whose
        /// palette was 0x3050A0). The leg is derived from the state, so the two cannot disagree.</summary>
        internal void SetThemeState(in ThemeState state)
        {
            _themeState = state;
            DarkMode = state.IsDark();
        }

        private ThemeState _themeState;
        private IntPtr _paletteBrush;
        private uint _paletteBrushColor;

        internal void ScaleControls(float scaleX, float scaleY)
        {
            foreach (var ctrl in Controls)
                ctrl.ScaleByDpi(scaleX, scaleY);
        }

        private IntPtr HandleCtlColor(IntPtr hdc, uint msg)
            => ThemedCtlColor(hdc, msg);

        /// <summary>This host's own state, so a Custom palette is served as well as the dark leg.</summary>
        private IntPtr ThemedCtlColor(IntPtr hdc, uint msg)
            => Control.HandleCtlColorThemed(hdc, msg, in _themeState,
                ref _darkBackgroundBrush, ref _darkEditBrush, ref _darkListBoxBrush, ref _darkButtonBrush,
                ref _paletteBrush, ref _paletteBrushColor);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Control.DisposeDarkBrushes(ref _darkBackgroundBrush, ref _darkEditBrush, ref _darkListBoxBrush, ref _darkButtonBrush);


            if (_handle != IntPtr.Zero)
            {
                var prop = GetPropW(_handle, WindowProp);
                if (prop != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(prop).Free();
                    RemovePropW(_handle, WindowProp);
                }
                SendMessageW(_hClient, WM_MDIDESTROY, (nuint)_handle, 0);
                RedrawWindow(_hClient, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                _handle = IntPtr.Zero;
            }
        }
    }
}

