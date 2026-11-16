namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
    public sealed partial class Form : Control, IControlHost
    {
        /// <summary>Register a control with this form and hand it back, so each factory below is one line. A plain
        /// form needs nothing more than the add: the control is a child of this window already.</summary>
        private T Adopt<T>(T ctrl) where T : Control
        {
            Controls.Add(ctrl);
            return ctrl;
        }

        private int _initialDpiX = 96;
        /// <summary>WEAR THIS MENU: the bar the window shows. The handle stays the engine&#39;s, which is the point of the
        /// Menu door, and null takes the bar away. It is HERE rather than on Control because a menu bar belongs to a
        /// top-level window, which is what the script&#39;s lookup on Form expects to find.</summary>
        public new void SetMenu(object menu)
        {
            // The parameter is OBJECT on purpose: the engine's overload picker matches CLR types, and a parameter
            // typed as a script type (Menu) was refused as "Method 'SetMenu' not found", measured on JIT and AOT
            // alike. The cast is where the typing lives now, and anything else clears the bar.
            var m = menu as Menu;
            Interpreter.SetMenu(handle, m == null ? IntPtr.Zero : m.Handle);   // qualified: this method shadows the import by name
        }

        private int _initialDpiY = 96;
        private int _currentDpiX = 96, _currentDpiY = 96;
        private bool _initialDpiStored;
        private int _startPosition;
        // THE SIZE THE LAST CENTRING USED, in the WINDOW's pixels (the same units `CenterToScreen` measures), plus
        // whether this window has ever been shown. `StartPosition = 1` is a POLICY rather than an action, so a size
        // written AFTER the assignment is still honoured: the setter centres at once, so "create it, centre it, size it"
        // left the window where the DEFAULT size was centred and then grew past the monitor's right and bottom edges.
        // Measured on clipboard_runner: centred at 3282,433 for a 427x213 window, then sized 2160x1030 (1440x687 pixels
        // on that 96 dpi monitor) and never moved, which was reported as "not centring any more, and bigger than before".
        // Only the FIRST Show re-centres, so a window a script resizes later stays where it put it.
        private int _centredWidth;
        private int _centredHeight;
        private bool _shownOnce;
        // The size the SCRIPT wrote, in design units, kept apart from `width`/`height` because a scaling pass writes those
        // in pixels (see `ApplyWrittenSize`).
        private int _writtenDesignWidth;
        private int _writtenDesignHeight;
        private bool _maximizeBox = true;
        private bool _minimizeBox = true;
        private bool _resizable = true;
        /// <summary>Whether this form has already raised `Load`, which happens on the FIRST `Show()` (see there: the
        /// window exists from `Application.CreateForm`, so raising it at handle creation would fire before the script
        /// could attach a handler).</summary>
        private bool _loadRaised;

        private void ApplyStyleBit(uint bit, bool on)
        {
            var style = GetWindowLongPtrW(Handle, GWL_STYLE).ToInt64();
            style = on ? (style | bit) : (style & ~bit);
            SetWindowLongPtrW(Handle, GWL_STYLE, (nint)style);
            // Repaint the caption/frame so the change takes effect immediately.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        /// <summary>Whether the window shows a maximize box / can be maximized (default true). The window exists from
        /// `Application.CreateForm`, so the style bit applies at once; set it before `Show()`.</summary>
        public bool MaximizeBox
        {
            get => _maximizeBox;
            set { _maximizeBox = value; if (Handle != IntPtr.Zero) ApplyStyleBit(WS_MAXIMIZEBOX, value); }
        }

        private bool _closeBox = true;

        /// <summary>Whether the window shows a close box, which is the system menu and the X in it (default true).
        /// It applies a style bit to a window that exists, and the window exists from `Application.CreateForm`; a form
        /// without one is still closable from a script through <see cref="Close"/>.</summary>
        public bool CloseBox
        {
            get => _closeBox;
            set { _closeBox = value; if (Handle != IntPtr.Zero) ApplyStyleBit(WS_SYSMENU, value); }
        }

        /// <summary>Whether the window shows a minimize box (default true). The window exists from
        /// `Application.CreateForm`, so the style bit applies at once; set it before `Show()`.</summary>
        public bool MinimizeBox
        {
            get => _minimizeBox;
            set { _minimizeBox = value; if (Handle != IntPtr.Zero) ApplyStyleBit(WS_MINIMIZEBOX, value); }
        }

        /// <summary>Whether the window has a resizable thick frame (default true). The window exists from
        /// `Application.CreateForm`, so the style bit applies at once; set it before `Show()`.</summary>
        public bool Resizable
        {
            get => _resizable;
            set { _resizable = value; if (Handle != IntPtr.Zero) ApplyStyleBit(WS_THICKFRAME, value); }
        }

        private IntPtr _adoptedChild;
        private int _adoptLeft, _adoptTop, _adoptRight, _adoptBottom;

        /// <summary>Parents a foreign child HWND (e.g. an ActiveX child from Com.HostActiveX)
        /// into this form and keeps it sized to the client area minus the given insets on
        /// every WM_SIZE. Call after the form handle exists. Takes the hwnd as a long,
        /// Com.HostActiveX returns an Int64 handle.</summary>
        public void AdoptChild(long foreignHwnd, int insetLeft, int insetTop, int insetRight, int insetBottom)
        {
            _adoptedChild = (nint)foreignHwnd;
            _adoptLeft = insetLeft;
            _adoptTop = insetTop;
            _adoptRight = insetRight;
            _adoptBottom = insetBottom;
            if (Handle != IntPtr.Zero && _adoptedChild != IntPtr.Zero)
            {
                SetParent(_adoptedChild, Handle);
                // SetParent does not adjust window styles: make it a true child window.
                var style = GetWindowLongPtrW(_adoptedChild, GWL_STYLE).ToInt64();
                style |= WS_CHILD;
                style &= ~(long)WS_POPUP;
                SetWindowLongPtrW(_adoptedChild, GWL_STYLE, (nint)style);
                SizeAdoptedChild();
            }
        }

        private void SizeAdoptedChild()
        {
            if (_adoptedChild == IntPtr.Zero) return;
            GetClientRect(Handle, out var rc);
            var w = rc.Right - _adoptLeft - _adoptRight;
            var h = rc.Bottom - _adoptTop - _adoptBottom;
            if (w < 0) w = 0;
            if (h < 0) h = 0;
            SetWindowPos(_adoptedChild, IntPtr.Zero, _adoptLeft, _adoptTop, w, h, SWP_NOZORDER);
        }

        public int StartPosition
        {
            get => _startPosition;
            set
            {
                _startPosition = value;
                // A WINDOW THAT ALREADY EXISTS IS CENTRED NOW. `Application.CreateForm` creates the window, so a script
                // assigns this AFTER it exists, and honouring the request only at creation (which is what
                // `OnHandleCreated` does) made `form.StartPosition = 1;` a NO-OP for every form a script holds: reported
                // on music_player, where the 780x640 window opened at the engine default instead of the centre. It is
                // the rule `Screen`'s setter already follows (it re-places the window when assigned), and the creation
                // path is untouched, so a form whose StartPosition is set BEFORE it has a window keeps centring there.
                if (value == 1 && handle != IntPtr.Zero) { CenterToScreen(); }
            }
        }

        // ── A DESIGNER GRID, on the form itself ──────────────────────────────────────────────────────────
        // GridSize is the dot spacing in client pixels (0 or 1 = no grid), GridColor the dot colour: a designer
        // sets two properties instead of driving GetDC/CreatePen/SelectObject/MoveToEx/LineTo by hand.
        // The dots are DOTTED LINES from a PS_DOT pen, which is what VB's designer shows. A monochrome pattern
        // brush was tried first and does NOT work: it paints its 0 bits in the DC's background colour, so the
        // first FillRect floods the client solid (measured, and the transparent background mode does not apply
        // to FillRect). One pen is cached per colour.
        private int _gridSize;
        private long _gridColor = 0xA0A0A0;
        private nint _gridPen;
        private long _gridPenColor = -1;

        public int GridSize
        {
            get => _gridSize;
            set
            {
                _gridSize = value;
                if (Handle != IntPtr.Zero) InvalidateRect(Handle, IntPtr.Zero, false);
            }
        }

        public long GridColor
        {
            get => _gridColor;
            set
            {
                _gridColor = value;
                if (Handle != IntPtr.Zero) InvalidateRect(Handle, IntPtr.Zero, false);
            }
        }

        /// <summary>Paints the grid inside the update rect, BEFORE the script's own Paint handler, which is
        /// then free to draw over it.</summary>
        internal void FillGrid(nint hdc, in Rect update)
        {
            if (_gridSize < 2 || hdc == IntPtr.Zero) return;
            if (_gridPen == IntPtr.Zero || _gridPenColor != _gridColor)
            {
                if (_gridPen != IntPtr.Zero) DeleteObject(_gridPen);
                _gridPen = CreatePen(2, 1, ToColorRef((uint)(_gridColor & 0xFFFFFFFFL)));   // 2 = PS_DOT, 1 px
                _gridPenColor = _gridColor;
                if (_gridPen == IntPtr.Zero) return;
            }
            var oldPen = SelectObject(hdc, _gridPen);
            var x0 = ((update.Left - _gridSize) / _gridSize) * _gridSize;   // the line left of the update rect
            for (var x = x0; x <= update.Right; x += _gridSize)
            {
                MoveToEx(hdc, x, update.Top, IntPtr.Zero);
                LineTo(hdc, x, update.Bottom);
            }
            var y0 = ((update.Top - _gridSize) / _gridSize) * _gridSize;
            for (var y = y0; y <= update.Bottom; y += _gridSize)
            {
                MoveToEx(hdc, update.Left, y, IntPtr.Zero);
                LineTo(hdc, update.Right, y);
            }
            if (oldPen != IntPtr.Zero) SelectObject(hdc, oldPen);
        }
        public ScriptFunction? Load { get; set; }
        public ScriptFunction? Closing { get; set; }
        public ScriptFunction? Closed { get; set; }
        public ScriptFunction? Command { get; set; }
        // DropFiles is Control's now, so a child and a form answer ONE event (and both take a drop).
        public ScriptFunction? Timer { get; set; }
        public ScriptFunction? ResizeEvent { get; set; }
        public new ScriptFunction? Paint
        {
            get => base.Paint;
            set =>
                base.Paint = value;
        }
        public ScriptFunction? EraseBackground { get; set; }
        public ControlCollection Controls { get; }

        // -------------------------------------------------------------
        // Control factory methods (instance methods on Form)
        // -------------------------------------------------------------
        // EVERY factory takes an optional trailing `configure` callback (`WithInit`), which runs after the control is
        // adopted (parented and created, so anything needing a window already works) and receives the CONCRETE type.
        // The pattern: one statement creates a control and finishes it, and the form is built while it is still hidden.
        public TrackBar CreateTrackBar(int x, int y, int width, int height, Action<TrackBar>? configure = null) => Adopt(new TrackBar(x, y, width, height)).WithInit(configure);

        public Label CreateLabel(string text, int x, int y, int width, int height, Action<Label>? configure = null) => Adopt(new Label(text, x, y, width, height)).WithInit(configure);

        public Button CreateButton(string text, int x, int y, int width, int height, Action<Button>? configure = null) => Adopt(new Button(text, x, y, width, height)).WithInit(configure);

        public TextBox CreateTextBox(string text, int x, int y, int width, int height, Action<TextBox>? configure = null) => Adopt(new TextBox(text, x, y, width, height)).WithInit(configure);
        public RichTextBox CreateRichTextBox(string text, int x, int y, int width, int height, Action<RichTextBox>? configure = null) => Adopt(new RichTextBox(text, x, y, width, height)).WithInit(configure);

        public CheckBox CreateCheckBox(string text, int x, int y, int width, int height, bool isChecked = false, Action<CheckBox>? configure = null) => Adopt(new CheckBox(text, x, y, width, height, isChecked)).WithInit(configure);

        public RadioButton CreateRadioButton(string text, int x, int y, int width, int height, bool isChecked = false, Action<RadioButton>? configure = null) => Adopt(new RadioButton(text, x, y, width, height, isChecked)).WithInit(configure);

        public ListBox CreateListBox(int x, int y, int width, int height, Action<ListBox>? configure = null) => Adopt(new ListBox(x, y, width, height)).WithInit(configure);

        public ComboBox CreateComboBox(int x, int y, int width, int height, Action<ComboBox>? configure = null) => Adopt(new ComboBox(x, y, width, height)).WithInit(configure);

        // Generic control creation for any Win32 window class
        public GenericControl CreateWinControl(string className, string text, int x, int y, int width, int height, uint style = 0, uint exStyle = 0, Action<GenericControl>? configure = null) => Adopt(new GenericControl(className, text, x, y, width, height, style, exStyle)).WithInit(configure);

        public ListView CreateListView(int x, int y, int width, int height, Action<ListView>? configure = null) => Adopt(new ListView(x, y, width, height)).WithInit(configure);

        public TreeView CreateTreeView(int x, int y, int width, int height, Action<TreeView>? configure = null) => Adopt(new TreeView(x, y, width, height)).WithInit(configure);

        public DateTimePicker CreateDateTimePicker(int x, int y, int width, int height, Action<DateTimePicker>? configure = null) => Adopt(new DateTimePicker(x, y, width, height)).WithInit(configure);

        public StatusBar CreateStatusBar(int x, int y, int width, int height, Action<StatusBar>? configure = null) => Adopt(new StatusBar(x, y, width, height)).WithInit(configure);

        public TabControl CreateTabControl(int x, int y, int width, int height, Action<TabControl>? configure = null) => Adopt(new TabControl(x, y, width, height)).WithInit(configure);

        public ToolBar CreateToolBar(int x, int y, int width, int height, Action<ToolBar>? configure = null) => Adopt(new ToolBar(x, y, width, height)).WithInit(configure);

        public SysLink CreateSysLink(string text, int x, int y, int width, int height, Action<SysLink>? configure = null) => Adopt(new SysLink(text, x, y, width, height)).WithInit(configure);

        public HotKey CreateHotKey(int x, int y, int width, int height, Action<HotKey>? configure = null) => Adopt(new HotKey(x, y, width, height)).WithInit(configure);

        public Rebar CreateRebar(int x, int y, int width, int height, Action<Rebar>? configure = null) => Adopt(new Rebar(x, y, width, height)).WithInit(configure);

        // Original bounds at design DPI (96)
        private int _designLeft, _designTop, _designWidth, _designHeight;
        private bool _designBoundsStored;

        private new void StoreDesignBounds()
        {
            if (_designBoundsStored) return;
            _designLeft = left;
            _designTop = top;
            _designWidth = width;
            _designHeight = height;
            _designBoundsStored = true;
        }

        public void PerformLayout()
        {
            GetClientRect(handle, out var client);
            // Sequential docking: each docked control takes a band off the REMAINING client rect, in the order
            // the controls were created, so a Bottom bar and a Fill box coexist. Laying every control out against
            // the whole client, which this did, made a Fill cover the bar.
            // Padding insets that rect, which is what keeps a docked control's own edges (and so its scrollbars)
            // off the window's edge: measured, a Fill-docked log box put its vertical bar's outer column exactly
            // on the client's right edge, and its horizontal bar on the client's bottom, with no gap at all.
            var pad = PaddingPixels();
            // A control's MARGIN insets THAT control inside the band it is given, which is how one docked box
            // stands 20 px off the left while everything else keeps the padding. It scales with the DPI like the
            // padding and the control's own bounds do, so one scale factor serves the whole pass.
            var scale = _initialDpiX > 0 ? (double)_currentDpiX / _initialDpiX : 1.0;
            int Scaled(int value) => (int)Math.Round(value * scale);
            // A MARGIN is the distance from the edge the control FACES, so what a script types is what it gets:
            // on the window's client edge it is that distance (and Padding is only the fallback for a side with no
            // margin), and on another control's edge it is the gap between the two. The band starts as the WHOLE
            // client rect and each docked control takes its band off the side it docks to, which is why the
            // padding is applied as a fallback inset rather than to the band up front.
            var left = client.Left;
            var top = client.Top;
            var right = client.Right;
            var bottom = client.Bottom;
            var leftIsWindow = true;
            var topIsWindow = true;
            var rightIsWindow = true;
            var bottomIsWindow = true;
            int Inset(int margin, bool windowEdge) => margin >= 0 ? Scaled(margin) : (windowEdge ? pad : 0);

            foreach (var ctrl in Controls)
            {
                var ml = Inset(ctrl.ResolvedMarginLeft, leftIsWindow);
                var mt = Inset(ctrl.ResolvedMarginTop, topIsWindow);
                var mr = Inset(ctrl.ResolvedMarginRight, rightIsWindow);
                var mb = Inset(ctrl.ResolvedMarginBottom, bottomIsWindow);
                switch (ctrl.Dock)
                {
                    case DockStyle.Bottom:
                        {
                            var h = ctrl.Height;
                            var y = bottom - mb - h;
                            var w = (right - mr) - (left + ml);
                            ctrl.SetBounds(left + ml, y, w < 0 ? 0 : w, h);
                            bottom = y - mt;      // this control's TOP margin is the gap above it, for what comes next
                            bottomIsWindow = false;
                            break;
                        }
                    case DockStyle.Top:
                        {
                            var h = ctrl.Height;
                            var y = top + mt;
                            var w = (right - mr) - (left + ml);
                            ctrl.SetBounds(left + ml, y, w < 0 ? 0 : w, h);
                            top = y + h + mb;
                            topIsWindow = false;
                            break;
                        }
                    case DockStyle.Left:
                        {
                            var w = ctrl.Width;
                            var x = left + ml;
                            var h = (bottom - mb) - (top + mt);
                            ctrl.SetBounds(x, top + mt, w, h < 0 ? 0 : h);
                            left = x + w + mr;
                            leftIsWindow = false;
                            break;
                        }
                    case DockStyle.Right:
                        {
                            var w = ctrl.Width;
                            var x = right - mr - w;
                            var h = (bottom - mb) - (top + mt);
                            ctrl.SetBounds(x, top + mt, w, h < 0 ? 0 : h);
                            right = x - ml;
                            rightIsWindow = false;
                            break;
                        }
                    case DockStyle.Fill:
                        {
                            var w = (right - mr) - (left + ml);
                            var h = (bottom - mb) - (top + mt);
                            ctrl.SetBounds(left + ml, top + mt, w < 0 ? 0 : w, h < 0 ? 0 : h);
                            break;
                        }
                }
            }
        }


        /// <summary>The padding converted from DESIGN pixels to this window's DPI, the way the font size and the
        /// control bounds already are, so a margin that looks right at 100% does not shrink at 150%.</summary>
        private int PaddingPixels()
        {
            if (_padding == 0) return 0;
            if (_initialDpiX <= 0 || _currentDpiX <= 0) return _padding;
            return (int)((long)_padding * _currentDpiX / _initialDpiX);
        }

        private static bool _classRegistered;
        private static readonly string WindowClassName = "ScriptFormClass";

        // Dark mode support
        private bool _darkMode;
        private ThemeMode _theme = ThemeMode.Light;
        /// <summary>The custom palette, read only while <c>Theme == ThemeMode.Custom</c>. A null field means
        /// "derive from the effective leg", so a script sets the colours it cares about and nothing else.</summary>
        internal ThemePalette Palette;
        private IntPtr _darkBackgroundBrush;
        private IntPtr _darkEditBrush;
        private IntPtr _darkButtonBrush;
        private IntPtr _darkListBoxBrush;

        /// <summary>The DPI the design bounds are written against (the system's): a control scales itself to the monitor it is created on relative to this.</summary>
        internal int ReferenceDpiX => _initialDpiX;

        /// <summary>The DPI the form's own design measurements are written against, which is the SYSTEM's (the
        /// primary monitor's), as opposed to <see cref="Dpi"/>, the DPI of the monitor the window is on right now. A
        /// script that lays controls out itself in PIXELS needs both: the constants it authored (a 10 px gap, a 56 px
        /// row offset) are design units, while a control's own Width/Height come back in pixels, so a row laid out at
        /// 96 DPI with the raw constants keeps 144-dpi gaps between shrunken controls, which is what "many spaces
        /// between controls" looked like after a drag to the other screen. The whole conversion is
        /// <c>px = round(design * form.Dpi / form.ReferenceDpi)</c>, and a form on the reference monitor answers the
        /// design value unchanged.</summary>
        public int ReferenceDpi => _initialDpiX > 0 ? _initialDpiX : 96;

        private int _requestedScreen = -1;
        private int _padding;
        private long _icon = -1;   // -1 = the script has not chosen, so the executable's own icon applies
        private string? _iconText; // a character to render instead (form.Icon = "✅" or form.SetIcon("✅"))
        private int _iconSize = 32;
        private long _iconColor = -1;   // -1 = the colour the HOST needs, so a theme switch re-renders
        private IntPtr _renderedGlyph = IntPtr.Zero;
        private IntPtr _renderedGlyphSmall = IntPtr.Zero;

        /// <summary>Set the window's icon, in ONE spelling for all three cases.
        /// <code>form.SetIcon("✅")</code> renders that CHARACTER into an icon (see <see cref="IconFromGlyph"/>)
        /// at 32 px in the colour the host needs, white on a dark form and near-black on a light one, re-rendered
        /// if <code>DarkMode</code> changes afterwards; <code>form.SetIcon("✅", 48)</code> picks the size and
        /// <code>form.SetIcon("✅", 32, 0x1F7A1F)</code> the colour as well, written 0xRRGGBB the way a script
        /// writes every other colour. A NUMBER is an icon HANDLE the script already has
        /// (<code>Application.ExeIcon</code>, or <code>WinApi.IconFromGlyph</code> for a size or a colour of its
        /// own), with <code>0</code> meaning no icon at all: for a handle, the size and colour arguments do not
        /// apply. A form nobody sets one on takes the RUNNING EXECUTABLE's own icon, in both sizes. The size is
        /// checked BEFORE anything changes, so a refused call leaves the window as it was, a character no font
        /// covers leaves it with the icon it had rather than a blank one, and assigning before <code>Show()</code>
        /// is applied where the window is created, so nothing flickers. Returns the handle the window now holds,
        /// which is how a script reads the icon back.</summary>
        public long SetIcon(object? icon, int size = 32, long color = -1)
        {
            switch (icon)
            {
                case string text:
                    if (text.Length == 0)
                        throw new ScriptException("SetIcon: the text is empty, so there is no character to render.");
                    if (size < 8 || size > 256)
                        throw new ScriptException($"SetIcon: the size must be between 8 and 256 (got {size}).");
                    _iconText = text;
                    _iconSize = size;
                    _iconColor = color;
                    break;
                case null:
                    _iconText = null;
                    _icon = 0;
                    break;
                case int or long or short or byte:
                    _iconText = null;
                    _icon = Convert.ToInt64(icon);
                    break;
                default:
                    // An ICON handle is not any handle, and WINDOWS WILL NOT CATCH THE DIFFERENCE: measured,
                    // WM_SETICON accepts a WINDOW handle as an icon and reports it straight back, so a script that
                    // passed one would simply lose its icon in silence. That is why the refusal names the two sources
                    // an icon actually comes from, rather than widening the case to take a handle it must not take.
                    throw new ScriptException(
                        "SetIcon takes a character to render (e.g. \"✅\"), an ICON handle from Application.ExeIcon or "
                        + "WinApi.IconFromGlyph, or 0 for no icon: a WINDOW handle is not an icon handle.");
            }
            ApplyIcon();
            if (handle == IntPtr.Zero) return _icon < 0 ? ExeIconHandle().ToInt64() : _icon;
            return SendMessageW(handle, WM_GETICON, ICON_BIG, 0).ToInt64();
        }

        /// <summary>Puts the icon on the window, in BOTH sizes: the title bar and Alt+Tab ask for the small one,
        /// the task switcher's larger view for the big one, and setting only one is how a form ends up with two
        /// different icons. A TEXT is rendered here (at 32 and at 16 px, each in the host's colour) rather than on
        /// assignment, so it can follow a DarkMode change, and each render replaces the last one's handle instead
        /// of leaking it. A glyph no font covers leaves the window with what it had, rather than a blank icon.</summary>
        private void ApplyIcon()
        {
            if (handle == IntPtr.Zero) return;
            IntPtr big;
            IntPtr small;
            if (_iconText != null)
            {
                var color = _iconColor < 0 ? (_darkMode ? 0xFFFFFFu : 0x1E1E1Eu) : (uint)_iconColor;
                var smallSize = _iconSize > 16 ? 16 : _iconSize;
                var rendered = IconFromGlyph(_iconText, _iconSize, color);
                var renderedSmall = smallSize == _iconSize ? rendered : IconFromGlyph(_iconText, smallSize, color);
                if (rendered == 0) return;
                if (_renderedGlyph != IntPtr.Zero) DestroyIcon(_renderedGlyph);
                if (_renderedGlyphSmall != IntPtr.Zero) DestroyIcon(_renderedGlyphSmall);
                _renderedGlyph = new IntPtr(rendered);
                _renderedGlyphSmall = renderedSmall != 0 && renderedSmall != rendered ? new IntPtr(renderedSmall) : IntPtr.Zero;
                big = _renderedGlyph;
                small = _renderedGlyphSmall != IntPtr.Zero ? _renderedGlyphSmall : big;
            }
            else
            {
                big = _icon < 0 ? ExeIconHandle() : new IntPtr(_icon);
                small = _icon < 0 ? ExeSmallIconHandle() : big;
            }
            SendMessageW(handle, WM_SETICON, ICON_BIG, big);
            SendMessageW(handle, WM_SETICON, ICON_SMALL, small);
        }

        /// <summary>Space, in design pixels, kept between the window's edge and the controls the DOCK layout
        /// places (Padding, Left, Right, Top, Bottom, Fill). Zero by default, which puts a docked control's edges
        /// on the client's edges; a few pixels is what keeps a docked log box's scrollbars off the window's
        /// border. Absolute positions ignore it. The value scales with the DPI like every other design
        /// measurement, so it looks the same on a 100% and a 150% monitor.</summary>
        public int Padding
        {
            get => _padding;
            set
            {
                if (value < 0) throw new ScriptException($"Padding cannot be negative (got {value}).");
                if (_padding == value) return;
                _padding = value;
                if (handle != IntPtr.Zero) PerformLayout();
            }
        }

        /// <summary>Which monitor this form is on, 0-based in the order Windows enumerates them (the one Windows
        /// calls DISPLAY1 is 0), and the monitor it OPENS on when assigned before Show: the window does not exist
        /// yet, so the request is remembered and honoured where the window is created, which is better than
        /// creating it somewhere else and jumping. Assigning it later moves the form, centred in that monitor's
        /// work area with its size untouched. An index with no monitor behind it is refused, naming how many there
        /// are, so a script never has to guess.</summary>
        public int Screen
        {
            get
            {
                if (handle == IntPtr.Zero) return _requestedScreen < 0 ? 0 : _requestedScreen;
                var areas = MonitorWorkAreas();
                if (areas.Count == 0) return 0;
                GetWindowRect(handle, out var wr);
                var cx = (wr.Left + wr.Right) / 2;
                var cy = (wr.Top + wr.Bottom) / 2;
                for (var i = 0; i < areas.Count; i++)
                {
                    var a = areas[i];
                    if (cx >= a.left && cx < a.right && cy >= a.top && cy < a.bottom) return i;
                }
                // Off every work area (unplugged monitor, a stored position): answer with the NEAREST, so the
                // value a script reads is still a monitor it could set.
                var best = 0;
                long bestDistance = long.MaxValue;
                for (var i = 0; i < areas.Count; i++)
                {
                    var a = areas[i];
                    long dx = cx < a.left ? a.left - cx : (cx >= a.right ? cx - a.right : 0);
                    long dy = cy < a.top ? a.top - cy : (cy >= a.bottom ? cy - a.bottom : 0);
                    var d = (dx * dx) + (dy * dy);
                    if (d < bestDistance) { bestDistance = d; best = i; }
                }
                return best;
            }
            set
            {
                var areas = MonitorWorkAreas();
                if (areas.Count == 0)
                    throw new ScriptException("Screen: no monitor could be enumerated.");
                if (value < 0 || value >= areas.Count)
                    throw new ScriptException(
                        $"Screen {value} does not exist: this machine has {areas.Count} monitor(s), so the screens are 0..{areas.Count - 1}.");
                _requestedScreen = value;
                if (handle == IntPtr.Zero) return;

                // A SCRIPTED MOVE MUST LEAVE WHAT A DRAG LEAVES. This used to only re-place the window, so a move to
                // a monitor of another DPI left every child at the OLD one (measured: a check box kept 37 px on a 96
                // DPI screen while the same move by hand rescaled it), and it also made the reported check-box
                // rectangle impossible to reproduce from a script. The steps are the WM_DPICHANGED arm's, in its
                // order. They are written here again rather than shared: that arm's body is interleaved with state
                // only the message carries (Windows' suggested rect, the COM child's re-host), so it is a recorded
                // twin to collapse in a round of its own.
                var dpiBefore = _currentDpiX;
                PlaceOnScreen(areas[value]);
                var dpiHere = (int)GetDpiForWindow(handle);
                if (dpiHere > 0 && dpiBefore > 0 && dpiHere != dpiBefore && _initialDpiX > 0 && _initialDpiY > 0)
                {
                    var scaleX = (float)dpiHere / _initialDpiX;
                    var scaleY = (float)dpiHere / _initialDpiY;
                    _currentDpiX = dpiHere;
                    _currentDpiY = dpiHere;
                    ScaleByDpi(scaleX, scaleY);            // the form's own size, from its design units
                    PlaceOnScreen(areas[value]);           // ...and the placement again for the new size
                    foreach (var ctrl in Controls)
                        ctrl.ScaleByDpi(scaleX, scaleY);    // each child, which re-applies its font and its floor
                    ApplyFont();
                    EnumChildWindows(handle, static (child, _) =>
                    {
                        FromHandle(child)?.ApplyFont();
                        return true;
                    }, IntPtr.Zero);
                    PerformLayout();
                    foreach (var ctrl in Controls)
                        RefreshFrameAndTheme(ctrl.Handle);
                    RefreshFrameAndTheme(handle);
                    // A CENTRED FORM STAYS CENTRED ON THE MONITOR IT WAS SENT TO: the scaling above changed the
                    // window's size, and Windows resizes it again when the DPI message arrives, so a placement made
                    // before that is centred for a size the window no longer has (reported as "the form does not
                    // appear centered" together with `Screen = 1`).
                    if (_startPosition == 1) CenterToScreen();
                    RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero,
                                 RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
                }
            }
        }

        /// <summary>Puts the window in the middle of a work area without changing its size. The placement is done in
        /// PIXELS on both sides: a work area is a pixel rect and so is the window's own rect, while Width/Height are
        /// DESIGN units (they multiply by here/reference when the window is placed). Mixing the two put a LARGE
        /// window back on the monitor it started on, because its design width overflowed the target work area:
        /// measured with the tool's own panel (1920 design units), <c>panel.Screen = 1</c> left it on screen 0 for
        /// that reason alone. MoveWindow is the pixel door and it records the design bounds, so a later scaling pass
        /// agrees with where the window now is.</summary>
        private void PlaceOnScreen(RECT work)
        {
            if (handle == IntPtr.Zero)
            {
                // NO WINDOW YET: this is the creation path, where Left/Top ARE the numbers CreateWindowEx receives,
                // so they are pixels as well and there is nothing to convert. Width/Height are the same story here
                // (the window is born at them), which is what keeps a Screen assigned BEFORE Show working.
                var w0 = Math.Max(1, Width);
                var h0 = Math.Max(1, Height);
                var x0 = work.left + (((work.right - work.left) - w0) / 2);
                var y0 = work.top + (((work.bottom - work.top) - h0) / 2);
                Left = x0 < work.left ? work.left : x0;
                Top = y0 < work.top ? work.top : y0;
                return;
            }
            GetWindowRect(handle, out var wr);
            var w = wr.Right - wr.Left;
            var h = wr.Bottom - wr.Top;
            if (w <= 0) w = Math.Max(1, Width);
            if (h <= 0) h = Math.Max(1, Height);
            var x = work.left + (((work.right - work.left) - w) / 2);
            var y = work.top + (((work.bottom - work.top) - h) / 2);
            if (x < work.left) x = work.left;
            if (y < work.top) y = work.top;
            MoveWindow(x, y, w, h);
        }

        /// <summary>The look this form uses. Setting it re-applies the theme to the form and to every control: Light
        /// and Dark are the native legs, System reads Windows' own app-mode setting, and Custom paints the palette
        /// members below on the parts the engine draws. Reading <c>DarkMode</c> afterwards answers the EFFECTIVE
        /// leg, which for Custom is derived from the palette background's luminance.</summary>
        public ThemeMode Theme
        {
            get => _theme;
            set
            {
                if (_theme == value) return;
                _theme = value;
                ApplyThemeMode();
            }
        }

        /// <summary>The dark leg, kept as the shorthand it always was: <c>form.DarkMode = true</c> is
        /// <c>form.Theme = ThemeMode.Dark</c>, so nothing that used it changed meaning. Reading it answers the
        /// effective leg, so a Custom form reports whichever leg its palette implies.</summary>
        internal bool DarkMode
        {
            get => _darkMode;
            set => Theme = value ? ThemeMode.Dark : ThemeMode.Light;
        }
        private IntPtr _paletteBrush;
        private uint _paletteBrushColor;


        /// <summary>Resolves the mode to the leg the native parts use, then re-themes the form and its controls.
        /// Custom derives that leg from its own background so the parts the engine cannot paint (a scrollbar, a
        /// themed button face) still match the palette instead of fighting it.</summary>
        private void ApplyThemeMode()
        {
            var dark = ThemeStateValue.IsDark();
            _darkMode = dark;
            if (handle == IntPtr.Zero) return;
            // The theme is re-applied to this window and to every control on EVERY mode change, not only on one
            // that moved the LEG: a kind whose colours the ENGINE SETS (a ListView's, a TreeView's, a status bar's,
            // the caption and frame Windows draws) paints from the value it was given, so a Custom palette on a
            // form whose leg was already right left every one of them on the previous look.
            ApplyDarkModeAttributes();
            // A glyph icon is drawn in the host's colour, so a theme switch re-renders it: white text on a dark
            // title bar and near-black on a light one are the same character. The palette changes what the engine
            // paints, so the window is invalidated either way.
            if (_iconText != null) ApplyIcon();
            InvalidateRect(handle, IntPtr.Zero, true);
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        /// <summary>This form's state as the shared ThemeState sees it: the maths and the leg resolution live there
        /// ONCE, so Form, MdiForm and MdiChild cannot drift apart on what Custom or System means. A control reads
        /// it through <see cref="Control.ThemeStateOf"/>, which is also where the Custom test is applied.</summary>
        internal ThemeState ThemeStateValue => new() { Mode = _theme, Palette = Palette };

        private bool PaletteIsDark() => ThemeStateValue.PaletteIsDark();

        /// <summary>The colour a custom form paints a part with, falling back to the effective leg's own default
        /// when the script left that field null. Readers: the client fill, the control-owner colour messages, the
        /// status bar and the TrackBar, which are the surfaces the engine draws.</summary>
        internal uint CustomBackground() => ThemeStateValue.Background();
        internal uint CustomText() => ThemeStateValue.Text();
        internal uint CustomBorder() => ThemeStateValue.Border();
        internal uint CustomSelection() => ThemeStateValue.Selection();
        internal uint CustomDisabled() => ThemeStateValue.Disabled();

        /// <summary>The five colours ThemeMode.Custom paints with, read back as they were set (null while unset, in
        /// which case the effective leg supplies the value). Setting one repaints the form; setting all five is the
        /// usual shape, and a part the engine does not draw follows the palette's own luminance.</summary>
        public uint? ThemeBackground { get => Palette.Background; set { Palette.Background = value; RepaintForTheme(); } }
        public uint? ThemeText { get => Palette.Text; set { Palette.Text = value; RepaintForTheme(); } }
        public uint? ThemeBorder { get => Palette.Border; set { Palette.Border = value; RepaintForTheme(); } }
        public uint? ThemeSelection { get => Palette.Selection; set { Palette.Selection = value; RepaintForTheme(); } }
        public uint? ThemeDisabled { get => Palette.Disabled; set { Palette.Disabled = value; RepaintForTheme(); } }

        /// <summary>Repaints after a palette change, and re-applies the leg when the change alters the palette's
        /// luminance (a background that turned dark also turns the scrollbars dark, which is the point of deriving
        /// that leg from the palette rather than asking the script for a second setting).</summary>
        private void RepaintForTheme()
        {
            // The leg is resolved BEFORE the handle check, which is what ApplyThemeMode does and the reason a
            // palette assigned before Show() still decides the leg: returning early first left _darkMode at the
            // pre-palette value, so a DARK palette drew LIGHT common-control faces, the opposite of what deriving
            // the leg from the palette is for. Measured: a Custom form at 0x3050A0, palette set before Show,
            // read its ListView and TreeView at 0xFFFFFF, the light leg's own constant.
            var dark = PaletteIsDark();
            // Only Custom takes its leg from the palette: a palette member is not a mode, so a Dark or Light form
            // keeps the leg it was given however its palette fields read.
            if (_theme == ThemeMode.Custom) _darkMode = dark;
            if (handle == IntPtr.Zero) return;
            // Same reason as ApplyThemeMode: a palette change is not always a leg flip, and the kinds whose colours
            // the engine SETS draw from the value they were given.
            ApplyDarkModeAttributes();
            InvalidateRect(handle, IntPtr.Zero, true);
            // A CHILD is recoloured only while it paints, because its colour is asked for in the WM_CTLCOLOR*
            // the child sends as it draws. Invalidating the form alone therefore left every child on the
            // PREVIOUS palette until something else happened to repaint it, and only a change that flipped the
            // leg hid that (the leg walk invalidates the children on its own). A same-leg change, dark palette
            // to dark palette, showed it: a text box kept the old colour until it was invalidated by hand.
            // ALLCHILDREN is the idiom the other re-theme paths in this file use.
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        public Form(string title)
        {
            Text = title;
            Left = 100; Top = 100; Width = 640; Height = 320;
            Controls = [with(this)];
            _visible = false;
            RegisterWindowClass();  // Must be called before any window creation
        }

        protected override void CreateHandle()
        {
            var hInstance = GetModuleHandleW(null!);
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            // A Screen assigned BEFORE the window existed applies here, so the form is BORN on that monitor:
            // creating it at the default position and moving it afterwards would be a visible jump.
            if (_requestedScreen >= 0)
            {
                var areas = MonitorWorkAreas();
                if (_requestedScreen < areas.Count) PlaceOnScreen(areas[_requestedScreen]);
            }
            // NO WS_EX_COMPOSITED. It used to be set here so that a child control repainting would not flash the
            // window around it, and its motivating case is fixed at its source instead: the compiler UI's log box
            // (Control.AppendText appends rather than rewriting the whole text). Measured on a form hosting two
            // dark ListViews plus a dozen other children: with the style ON, an IDLE form repainted BOTH lists 170
            // times in 2.2 seconds, because every child invalidation recomposites the whole window and the common
            // controls re-invalidate, so it never settles; with the style OFF the same run is 22 child paints in
            // total, one per child. WS_CLIPCHILDREN stays: that is what keeps a parent from painting over its
            // children, and it is the part compositing was never needed for.
            handle = CreateWindowExW(0, WindowClassName, Text,
                WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                Left, Top, Width, Height,
                IntPtr.Zero, IntPtr.Zero, hInstance, lpParam);

            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

            // A FORM assigns its id HERE, at its own creation point, because it deliberately keeps its own registration
            // and never reaches RegisterHandle (AssignId says the rest). Doing it anywhere else in the form's life
            // misses, which is exactly what a script's Id check caught.
            AssignId();
        }

        private void DumpMetrics(string label)
        {
            Console.WriteLine($"[DBG {label}] Form: left={Left}, top={Top}, w={Width}, h={Height}");
            GetWindowRect(handle, out var wr);
            Console.WriteLine($"[DBG {label}]   actual rect: l={wr.Left}, t={wr.Top}, r={wr.Right}, b={wr.Bottom} (w={wr.Right - wr.Left}, h={wr.Bottom - wr.Top})");
            var dpi = (int)GetDpiForWindow(handle);
            Console.WriteLine($"[DBG {label}]   DPI: {dpi}, initialDpiX: {_initialDpiX}, initialDpiStored: {_initialDpiStored}");
            var idx = 0;
            foreach (var ctrl in Controls)
            {
                var typeName = ctrl.GetType().Name;
                Console.Write($"[DBG {label}]   Ctrl[{idx}] {typeName}: left={ctrl.Left}, top={ctrl.Top}, w={ctrl.Width}, h={ctrl.Height}");
                if (ctrl.Handle != IntPtr.Zero)
                {
                    GetWindowRect(ctrl.Handle, out var cr);
                    var cw = cr.Right - cr.Left;
                    var ch = cr.Bottom - cr.Top;
                    // Compute client-relative position using parent window rect
                    GetWindowRect(handle, out var pr);
                    // Approximate: client top-left ≈ window top-left + non-client border
                    var relLeft = cr.Left - pr.Left;
                    var relTop = cr.Top - pr.Top;
                    Console.Write($"  actual: l={cr.Left}, t={cr.Top}, w={cw}, h={ch}"
                                 + $"  rel: l={relLeft}, t={relTop}"
                                 + $"  delta: w={ctrl.Width - cw}, h={ctrl.Height - ch}");
                }
                Console.WriteLine();
                idx++;
            }
        }

        /// <summary>A FORM'S WIDTH AND HEIGHT ARE DESIGN UNITS UNTIL IT IS SHOWN. A script writes them once and means one
        /// physical size, so on a monitor whose DPI differs from the reference the window takes `written * here / ref`
        /// while the FIELDS keep the number the script wrote (which is what `PlaceOnScreen` and `DesignUnits` convert
        /// from). After the first `Show` a write is a LIVE size and is applied as pixels, unchanged.
        /// WHY IT LIVES HERE: `Application.CreateForm` creates the window at once, so the birth pass in `OnHandleCreated`
        /// scaled the size the form had THEN (the engine's default), and every size written afterwards became raw pixels.
        /// Measured: `Width = 900` rendered 900 px on the 96 dpi monitor where the rule says 600, which made
        /// clipboard_runner's panel (created, then sized) 1.5x its former physical size and wider than that monitor.
        /// The two design values are kept APART from the fields because a scaling pass writes those in pixels
        /// (`MoveWindow` inside `PlaceOnScreen`), which is what made a first attempt scale the already-scaled size.</summary>
        protected override void OnWidthWritten(int writtenWidth)
        {
            _writtenDesignWidth = writtenWidth;
            ApplyWrittenSize();
        }

        protected override void OnHeightWritten(int writtenHeight)
        {
            _writtenDesignHeight = writtenHeight;
            ApplyWrittenSize();
        }

        private void ApplyWrittenSize()
        {
            if (_shownOnce || !_initialDpiStored || _currentDpiX <= 0 || _initialDpiX <= 0 || handle == IntPtr.Zero) return;
            if (_currentDpiX == _initialDpiX) return;   // the reference monitor: the written units ARE pixels
            var scale = (double)_currentDpiX / _initialDpiX;
            SetWindowPos(handle, IntPtr.Zero, 0, 0,
                Math.Max(1, (int)Math.Round(_writtenDesignWidth * scale)),
                Math.Max(1, (int)Math.Round(_writtenDesignHeight * scale)),
                SWP_NOMOVE | SWP_NOZORDER);
        }

        public override void ScaleByDpi(float scaleX, float scaleY)
        {
            // Do NOT call base.ScaleByDpi(), that would reposition the form to
            // (_designLeft*scaleX, _designTop*scaleY), overwriting the lParam-
            // suggested rect that WM_DPICHANGED already applied via SetWindowPos.
            // The form position/size is handled by lParam; only child controls
            // need bounds-scaling here.
            foreach (var ctrl in Controls)
            {
                ctrl.ScaleByDpi(scaleX, scaleY);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // The REFERENCE DPI is the SYSTEM's, not the one this window happens to be born at: a script writes
            // Width/Height ONCE, so 1320 has to mean the same physical size whatever monitor the form opens on.
            // With the birth DPI as the reference, a form opened on the 96 dpi monitor came out 1320 PIXELS (1.5x
            // the physical size of the same form on the 144 dpi primary) and then GREW to 1980 px when dragged to
            // the primary, because the move scaled 144/96 from that wrong origin.
            if (!_initialDpiStored)
            {
                var referenceDpi = (int)GetDpiForSystem();
                if (referenceDpi <= 0) referenceDpi = (int)GetDpiForWindow(handle);
                _initialDpiX = referenceDpi;
                _initialDpiY = referenceDpi;   // DPI is uniform in X and Y for modern Windows
                _currentDpiX = (int)GetDpiForWindow(handle);
                _currentDpiY = _currentDpiX;
                _initialDpiStored = true;
                // the DESIGN size starts as the one the form was created with, so a script that writes only one of the
                // two still has a design value for the other (see `ApplyWrittenSize`)
                _writtenDesignWidth = width;
                _writtenDesignHeight = height;

            }

            // Store design bounds for DPI scaling
            StoreDesignBounds();
            foreach (var ctrl in Controls)
                ctrl.StoreDesignBounds();

            // Born away from the reference DPI: apply the design bounds at THIS monitor's scale, before the window
            // is ever shown, so nothing is seen growing or shrinking. The CONTROLS are in reference-DPI units as
            // well, so an absolutely positioned button is scaled WITH the window: sizing only the window left a
            // form 880 px wide on the 96 dpi monitor with its buttons still at their 144 dpi positions, which put
            // the last two past the right edge of a window no wider than they were.
            if (_initialDpiStored && _currentDpiX > 0 && _currentDpiX != _initialDpiX)
            {
                var birthScale = (float)_currentDpiX / _initialDpiX;
                SetWindowPos(handle, IntPtr.Zero, 0, 0,
                    (int)Math.Round(Width * (double)birthScale),
                    (int)Math.Round(Height * (double)birthScale),
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                foreach (var ctrl in Controls)
                    ctrl.ScaleByDpi(birthScale, birthScale);
            }

            // Apply centering if requested
            if (_startPosition == 1)
            {
                CenterToScreen();
                if (handle != IntPtr.Zero)
                    SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
            }

            if (_darkMode)
                ApplyDarkModeAttributes();
            else
                ApplyNonClientColors(handle, ThemeStateValue);   // a LIGHT Custom palette has a caption colour too

            ApplyIcon();   // the executable's icon unless the script assigned one, in both sizes

            // LOAD IS NOT RAISED HERE. The window now exists from `Application.CreateForm` on (it is created, hidden),
            // so raising Load at handle creation would fire it BEFORE a script could attach a handler, which is the
            // reverse of what the event is for. It is raised on the FIRST `Show()`, which is what WinForms means by
            // Load and what makes "build everything, then show" work: measured, a form whose handler is assigned after
            // CreateForm used to lose it entirely.

            // DumpMetrics("OnHandleCreated");
        }

        private void ApplyDarkModeAttributes()
        {
            var dark = _darkMode ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            // The caption is the one surface a script's palette could not reach, because Windows draws it: the leg
            // above only says dark or light, and Custom has a COLOUR. Both are applied, so a Custom form's caption
            // is the palette's while its glyphs keep the contrast the leg implies.
            ApplyNonClientColors(handle, ThemeStateValue);

            // Apply / revert the theme on all child windows. A STATUS BAR is the exception, and it is the one the
            // comment here used to CLAIM was excluded while the code themed it anyway: DarkMode_Explorer draws its
            // text black on the dark bar. It styles itself from the host instead (StatusBar.ApplyHostTheme), which
            // is also what keeps a status bar in a LIGHT form light.
            EnumChildWindows(handle, _darkMode
                ? static (hwnd, _) =>
                {
                    // One implementation per control: a StatusBar, a ListView and a Button each need their own arm,
                    // and a child the engine does not own (a foreign window, an ActiveX host) only gets the theme.
                    if (FromHandle(hwnd) is Control known) known.ApplyThemeToSelf(true);
                    else if (!HasNonClientBar(hwnd))
                    {
                        // A foreign window with a BAR keeps its flat scrollbar here too (see HasNonClientBar):
                        // this arm is the one a child the engine does not wrap reaches, and it was the hole
                        // that left SOME of a form's bars classic and others flat ("changed to classical, but
                        // not all", reported on the designer's style window, whose lists are dark).
                        SetWindowTheme(hwnd, "DarkMode_Explorer", "");
                        SendMessageW(hwnd, WM_THEMECHANGED, 0, IntPtr.Zero);
                        InvalidateRect(hwnd, IntPtr.Zero, true);
                    }
                    return true;
                }
            : static (hwnd, _) =>
            {
                if (FromHandle(hwnd) is Control known) known.ApplyThemeToSelf(false);
                else
                {
                    SetWindowTheme(hwnd, null, null);
                    SendMessageW(hwnd, WM_THEMECHANGED, 0, IntPtr.Zero);
                    InvalidateRect(hwnd, IntPtr.Zero, true);
                }
                return true;
            }, IntPtr.Zero);
        }

        private void CenterToScreen()
        {
            var hMonitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
            };
            // IN PIXELS, WITH THE WINDOW'S OWN RECT, placed through MoveWindow. Two faults were measured here.
            // Width/Height are the engine's own fields, which a DPI rescale applied through SetWindowPos does NOT
            // update, so the arithmetic centred a size the window no longer had (the reported "does not appear
            // centered": a 900-unit form whose window was already 600 px wide was centred as if it were 900 px). And
            // Left/Top are the fields a later scaling pass reads, so centring by writing them let the next pass move
            // the window again. MoveWindow is the pixel door and it records the design bounds with it.
            GetWindowRect(handle, out var wr);
            var w = wr.Right - wr.Left > 0 ? wr.Right - wr.Left : Math.Max(1, Width);
            var h = wr.Bottom - wr.Top > 0 ? wr.Bottom - wr.Top : Math.Max(1, Height);
            if (GetMonitorInfoW(hMonitor, ref mi))
            {
                var screenWidth = mi.rcWork.right - mi.rcWork.left;
                var screenHeight = mi.rcWork.bottom - mi.rcWork.top;
                MoveWindow(mi.rcWork.left + ((screenWidth - w) / 2), mi.rcWork.top + ((screenHeight - h) / 2), w, h);
            }
            else
            {
                MoveWindow((GetSystemMetrics(0) - w) / 2, (GetSystemMetrics(1) - h) / 2, w, h);
            }
            // Recorded so the first `Show()` can tell a window whose size changed since (see `Show` and the fields' note).
            _centredWidth = w;
            _centredHeight = h;
        }

        public void InitHandle()
        {
            if (handle != IntPtr.Zero) return;
            _visible = false;
            CreateControl();
        }

        /// <summary>Whether showing this form OWNS the input until it closes, the way a dialog does. It does NOT run
        /// a pump: the owner is disabled for as long as the form is up and re-enabled where the window is destroyed,
        /// and the loop that runs meanwhile is the SCRIPT&#39;s own. That is FBSL&#39;s ShowModal/EndModal shape, and it is
        /// also the only shape that survives here: measured, a pump inside Show either recursed (Application.Run
        /// calls Show itself) or re-entered the script mid-statement through a timer.</summary>
        public bool Modal { get; set; }

        // The three a window needs to become the one the user is looking at, which is what a click was supplying.
        // They live HERE, beside Show, because a LibraryImport belongs to the class that uses it: put them in the outer
        // class and the compiler answers "the name does not exist in the current context" at the line that names them.
        [LibraryImport("user32.dll")]
        private static partial nint SetActiveWindow(nint hWnd);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BringWindowToTop(nint hWnd);
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetForegroundWindow(nint hWnd);

        // DragAcceptFiles and AcceptDroppedFiles are Control's: ONE import and ONE rule for a form and a child.


        /// <summary>BE THE WINDOW THE USER IS LOOKING AT, which is what a click was supplying. A child can only take
        /// the focus while its window is the ACTIVE one of this thread, and SetFocus on a child of an inactive window
        /// fails SILENTLY: measured, Tab did nothing on a form until it was clicked. SetActiveWindow is the
        /// thread-local half, and it works even where Windows refuses the FOREGROUND to a process that did not start
        /// the window; BringWindowToTop and SetForegroundWindow are the z-order and desktop halves, which is why they
        /// are together rather than one alone. ONE implementation: `Show` calls this, and so does a script raising a
        /// window it already has. A window that does not exist yet is left alone rather than given a null handle.</summary>
        public void Activate()
        {
            if (handle == IntPtr.Zero) { return; }
            SetActiveWindow(handle);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }

        // ── IControlHost: a document walk reaches a form through these four, and they are implemented EXPLICITLY so
        // none of them becomes a script-visible member of Form ──
        void IControlHost.SetHostRow(string name, object? value) => ApplyDocumentRow(this, name, value);
        IEnumerable<Control> IControlHost.HostChildren() => Controls;
        Control? IControlHost.HostFind(string name) => FindIn(Controls, name);
        Control IControlHost.CreateKind(in ControlSpec spec) => CreateKindOn(this, in spec);

        public void Show()
        {
            if (handle == IntPtr.Zero)

                CreateControl();

            // The form's ctor starts with _visible = false (a form must not appear before Show), so showing it has
            // to say so: without this `form.Visible` answered FALSE for a window that was on screen, which is what a
            // Json.Serialize(form) of a shown form reported and what a script asking "is it up" got.
            _visible = true;
            // A SHOWN form is what a blocking wait counts (see `NativeApp.ShownForms`): recorded here, where the window
            // goes on screen, not at creation, where it may stay hidden for ever.
            NativeApp.MarkFormShown(handle);
            // `StartPosition = 1` IS HONOURED FOR THE SIZE THE WINDOW ACTUALLY HAS when it goes on screen, on the FIRST
            // Show only: the setter centres the moment it is assigned (so a form is placed even if it is never shown),
            // and a size written after that assignment would otherwise be centred nowhere. Measured on clipboard_runner,
            // whose panel is created, centred, and THEN sized: it opened 240 px past the monitor's right edge and below
            // its bottom, reported as "not centring any more and bigger than before". A window the script resizes after
            // the first Show keeps the place it was given, which is why the guard is `_shownOnce`.
            if (_startPosition == 1 && !_shownOnce)
            {
                GetWindowRect(handle, out var preShow);
                if (preShow.Right - preShow.Left != _centredWidth || preShow.Bottom - preShow.Top != _centredHeight)
                    CenterToScreen();
            }
            _shownOnce = true;
            ShowWindow(handle, 5);
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);

            // LOAD, ONCE, ON THE FIRST SHOW. This is where WinForms raises it, and it is the only place that works
            // now that the window exists from `Application.CreateForm`: raised at handle creation it fired before a
            // script could attach a handler, so `var f = CreateForm(..); f.Load = (s, e) => { ... }; f.Show();` lost
            // the handler entirely. It runs AFTER the window is visible, so a handler can measure and lay out against
            // real client sizes, and a form that is created and never shown never raises it.
            if (!_loadRaised) { _loadRaised = true; OnLoad(EventArgs.Empty); }


            // BE THE WINDOW THE USER IS LOOKING AT, which is what a click was supplying. A child can only take the
            // focus while its window is the ACTIVE one of this thread, and SetFocus on a child of an inactive window
            // fails SILENTLY: measured, Tab did nothing on a form until it was clicked. SetActiveWindow is the
            // thread-local half, and it works even where Windows refuses the FOREGROUND to a process that did not start
            // the window; BringWindowToTop and SetForegroundWindow are the z-order and desktop halves, which is why
            // they are together rather than one alone. A modal form reaches this too, since it is before that return.
            Activate();
            // The owner is read the way FBSL reads it, from the WINDOW itself, with the active window as the fallback
            // an ownerless window needs, and a window never disables ITSELF: Show on an already-active form would
            // otherwise take its own input away.
            if (!Modal) { return; }
            var owner = GetWindow(handle, GW_OWNER);
            if (owner == IntPtr.Zero) { owner = GetActiveWindow(); }
            if (owner != IntPtr.Zero && owner != handle)
            {
                _modalOwner = owner;
                EnableWindow(owner, 0);
            }
        }

        private IntPtr _modalOwner;

        public void Close() => SendMessageW(handle, WM_CLOSE, 0, IntPtr.Zero);

        /// <summary>ENDS THE RUN rather than the window, which is the difference a script needs when several forms are
        /// up: Close() closes this one and leaves the others running (and closing the LAST one is what ends the loop),
        /// while this ends it either way. It closes the form exactly as Close() does and then tells the message loop to
        /// stop, which is WM_QUIT through the engine&#39;s own Exit, so the two cannot disagree about either half.</summary>
        public void Quit()
        {
            Close();
            NativeApp.Exit();
        }

        /// <summary>Quit carrying a CODE, for a script that knows how it wants to leave. The code travels in the
        /// WM_QUIT the loop honours, so this is Application.Quit(code) reached from a form the script is holding.</summary>
        public void Quit(int code)
        {
            Close();
            NativeApp.Exit(code);
        }

        private void OnLoad(EventArgs e) => InvokeSafely(this, nameof(Load), Load, this, e);
        private void OnClosing(CancelEventArgs e) => InvokeSafely(this, nameof(Closing), Closing, this, e);
        private void OnClosed(EventArgs e) => InvokeSafely(this, nameof(Closed), Closed, this, e);
        private void OnCommand(int id, IntPtr hwndCtrl) => InvokeSafely(this, nameof(Command), Command, this, new CommandEventArgs(id, hwndCtrl, NativeApp.CurrentMsg?.Time ?? 0));
        // OnDropFiles is Control's (it releases the list there), so a form and a child behave alike.
        private void OnTimer(int timerId) => InvokeSafely(this, nameof(Timer), Timer, this, new TimerEventArgs(timerId));
        private void OnResizeEvent(int width, int height) => InvokeSafely(this, nameof(ResizeEvent), ResizeEvent, this, new ResizeEventArgs(width, height));

        protected override IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // --------------------------------------------------------------------
            // WM_CTLCOLOR* messages: apply child control custom colors or dark mode
            // --------------------------------------------------------------------
            switch (msg)
            {
                case WM_CTLCOLORMSGBOX:
                case WM_CTLCOLOREDIT:
                case WM_CTLCOLORLISTBOX:
                case WM_CTLCOLORBTN:
                case WM_CTLCOLORDLG:
                case WM_CTLCOLORSCROLLBAR:
                case WM_CTLCOLORSTATIC:
                    {
                        var hdc = wParam;
                        var hwndCtrl = lParam;
                        var ctrl = FromHandle(hwndCtrl);

                        if (ctrl != null && (ctrl.TextColor.HasValue || ctrl.BackgroundColor.HasValue))
                        {
                            if (ctrl.TextColor.HasValue)
                                Interpreter.SetTextColor(hdc, ToColorRef(ctrl.GetTextColorRef()));

                            // Use form's dark mode background when TextColor is set but BackgroundColor is not
                            if (ctrl.BackgroundColor.HasValue)
                            {
                                SetBkColor(hdc, ToColorRef(ctrl.GetBackgroundColorRef()));
                                SetBkMode(hdc, 1); // TRANSPARENT: brush provides the bg, no raw band behind text
                                if (ctrl.TextColorBrush == IntPtr.Zero)
                                    ctrl.TextColorBrush = CreateSolidBrush(ToColorRef(ctrl.GetBackgroundColorRef()));
                                return ctrl.TextColorBrush;
                            }
                            else if (_darkMode)
                            {
                                // Use the default dark mode background (0x202020)
                                SetBkColor(hdc, 0x202020);
                                if (ctrl.TextColorBrush == IntPtr.Zero)
                                    ctrl.TextColorBrush = CreateSolidBrush(0x202020);
                                return ctrl.TextColorBrush;
                            }
                            else
                            {
                                SetBkMode(hdc, 1); // TRANSPARENT
                                return GetStockObject(5); // NULL_BRUSH
                            }
                        }

                        if (Theme == ThemeMode.Custom)
                        {
                            // A Custom form's controls take the palette here, and THIS arm is the one that matters:
                            // WM_CTLCOLOR* is delivered to the PARENT window, which is this form, so the child's own
                            // arm never decides. Measured: a label under a 0x3050A0 form stayed 0x202020 because this
                            // path hardcoded the dark constant, while the palette lookup in the child's proc resolved
                            // correctly and was simply never consulted.
                            var palBg = CustomBackground() & 0xFFFFFF;
                            SetBkColor(wParam, ToColorRef(palBg));
                            SetBkMode(wParam, 1);
                            if (ctrl != null)
                                Interpreter.SetTextColor(wParam, ToColorRef(ctrl.Enabled ? CustomText() : CustomDisabled()));
                            // A BRUSH, not a fill. WM_CTLCOLOR* is answered with the brush the control paints its
                            // own background from, and FillClientColor FILLS the rect and answers 1, which is not a
                            // brush handle: five kinds then fell back to their class background, so a Label read
                            // 0xC8C8C8 and a CheckBox and a RadioButton 0xFFFFFF under a 0x3050A0 palette, while an
                            // explicit BackgroundColor worked because that branch returns a real brush. Measured
                            // after this reply: the same kinds read the palette, and the DateTimePicker is the one
                            // kind that still ignores it, which is its own class drawing the face.
                            if (_paletteBrush == 0 || _paletteBrushColor != palBg)
                            {
                                if (_paletteBrush != 0) DeleteObject(_paletteBrush);
                                _paletteBrush = CreateSolidBrush(ToColorRef(palBg));
                                _paletteBrushColor = palBg;
                            }
                            return _paletteBrush;
                        }
                        if (_darkMode)
                        {
                            return HandleCtlColor(wParam, msg);
                        }
                        break;
                    }
            }

            switch (msg)
            {
                case WM_CREATE:
                    NativeApp.RegisterForm(hwnd);
                    break;

                case WM_DESTROY:
                    NativeApp.UnregisterForm(hwnd);
                    // Do NOT call Dispose – cleanup is handled in WM_NCDESTROY
                    return IntPtr.Zero;

                case WM_NCDESTROY:
                    // The other half of Modal: the owner gets its input back HERE, whichever way the window died.
                    // FBSL's EndModal does this and destroys the window, and this arm IS the end of that destruction,
                    // so the two halves cannot be skipped independently.
                    if (FromHandle(hwnd) is Form dyingForm && dyingForm._modalOwner != IntPtr.Zero)
                    {
                        EnableWindow(dyingForm._modalOwner, 1);
                        dyingForm._modalOwner = IntPtr.Zero;
                    }
                    var handlePtr = GetPropW(hwnd, SCRIPT_WND_PROC);
                    if (handlePtr != IntPtr.Zero)
                    {
                        GCHandle.FromIntPtr(handlePtr).Free();
                        RemovePropW(hwnd, SCRIPT_WND_PROC);
                        RemovePropW(hwnd, OLD_WND_PROC);
                    }
                    // ... existing WM_NCDESTROY logic ...
                    break;

                case WM_CLOSE:
                    {
                        var ce = new CancelEventArgs();
                        OnClosing(ce);
                        if (!ce.Cancel)
                        {
                            OnClosed(EventArgs.Empty);
                            DestroyWindow(hwnd); // Direct call – do not call Dispose here
                        }
                        else
                        {
                        }
                        return IntPtr.Zero;
                    }

                case WM_GETMINMAXINFO:
                    {
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        mmi.ptMinTrackSize.x = 200;
                        mmi.ptMinTrackSize.y = 150;
                        Marshal.StructureToPtr(mmi, lParam, false);
                        return IntPtr.Zero;
                    }

                case WM_COMMAND:
                    {
                        // THE MENU HALF of this same message. A menu item sends WM_COMMAND with lParam ZERO and the id in
                        // the LOW word of wParam, where a control's notification carries its window in lParam. Without this
                        // the message was ignored, so a menu click went nowhere even once a menu could be built: the arm
                        // below is the child one, and the two shapes do not overlap.
                        var menuCmdId = Loword(wParam);
                        var code = Hiword(wParam);
                        var hwndCtrl = lParam;
                        if (code == BN_CLICKED && hwndCtrl != IntPtr.Zero)
                        {
                            var ctrl = FromHandle(hwndCtrl);
                            if (ctrl is ButtonBase btnBase)
                            {
                                btnBase.RaiseClick();
                                // A check box or radio button changed its state with that click; a plain
                                // button has no state to change, so its CheckedChanged never fires.
                                if (btnBase.IsToggle) btnBase.RaiseCheckedChanged();
                            }
                            else if (ctrl is GenericControl genCtrl)
                                genCtrl.RaiseClick();
                        }
                        else if (code == EN_CHANGE && hwndCtrl != IntPtr.Zero)
                        {
                            // An EDIT reports its text changes to its PARENT, the way a list box reports a
                            // selection: the parent looks the control up and raises the script event.
                            if (FromHandle(hwndCtrl) is TextBox tbEdit) tbEdit.RaiseTextChanged();
                        }
                        else if (hwndCtrl != IntPtr.Zero && (code == LBN_SELCHANGE || code == LBN_DBLCLK))
                        {
                            // LISTBOX sends selection/double-click notifications to the parent,
                            // reflect them into the child's own WndProc handler.
                            FromHandle(hwndCtrl)?.ReflectParentNotification(WM_COMMAND, wParam, lParam);
                        }
                        OnCommand(Loword(wParam), hwndCtrl);
                        return IntPtr.Zero;
                    }

                case WM_HSCROLL:
                case WM_VSCROLL:
                    {
                        // TrackBar/scrollbar send scroll notifications to the parent; reflect
                        // them into the child's WndProc (TrackBar updates Value + fires Scroll).
                        var hwndCtrl = lParam;
                        if (hwndCtrl != IntPtr.Zero)
                            FromHandle(hwndCtrl)?.ReflectParentNotification(msg, wParam, lParam);
                        break;
                    }

                case WM_NOTIFY:
                    {
                        if (lParam != IntPtr.Zero)
                        {
                            var nmhdr = Marshal.PtrToStructure<NMHDR>(lParam);
                            // Common controls (SysListView32, SysTreeView32) deliver a real
                            // double-click as WM_NOTIFY/NM_DBLCLK to the parent, reflect it
                            // into the child so script DoubleClick handlers fire (the child
                            // itself may never see a WM_LBUTTONDBLCLK message).
                            if (nmhdr.hwndFrom != IntPtr.Zero && FromHandle(nmhdr.hwndFrom) is ListView listChild)
                            {
                                // The two notifications a LIST sends its parent that a script has a typed member
                                // for: a double-click (the child may never see the mouse message itself, so this is
                                // the only route) and a selection change. Both still reach Command below, with the
                                // code in the id, which stays the escape hatch for what the engine does not name.
                                if ((uint)nmhdr.code == NM_DBLCLK) listChild.RaiseDoubleClick();
                                else if ((uint)nmhdr.code == LVN_ITEMCHANGED) listChild.RaiseSelectedIndexChanged();
                            }
                            OnCommand(0x10000 + nmhdr.code, nmhdr.hwndFrom);
                        }
                        return IntPtr.Zero;
                    }

                case WM_DROPFILES:
                    OnDropFiles(wParam);
                    return IntPtr.Zero;

                case WM_TIMER:
                    OnTimer(checked((int)wParam));
                    return IntPtr.Zero;

                case WM_SIZE:
                    if (inSizeMove) return IntPtr.Zero;
                    inSizeMove = true;
                    try
                    {
                        // WM_SIZE's lParam carries the CLIENT size, and these two fields are the WINDOW's: they are what Width/Height
                        // report, and what SetBounds and the Width/Height setters hand back to SetWindowPos as the window size. Assigning
                        // the client size here made the window lose its own frame the first time WM_SIZE fired (which ShowWindow does), and
                        // every later SetBounds re-applied the shrunken value. Measured with the dpi probe: a form asking for 1240x860 came
                        // out 1218x804 with a zero frame, and on REPEATED moves between two monitors it collapsed a little each time
                        // (1263x575, 810x305, 1171x346, 749x153) because the design bounds were stored off the clobbered size. The WINDOW
                        // rect is what these fields want; the resize EVENT still carries the client size, which is what a resize event means
                        // and what the layout below works in.
                        var clientW = Loword(lParam);
                        var clientH = Hiword(lParam);
                        if (GetWindowRect(handle, out var windowRect))
                        {
                            width = windowRect.Right - windowRect.Left;
                            height = windowRect.Bottom - windowRect.Top;
                        }
                        else
                        {
                            width = clientW;
                            height = clientH;
                        }
                        OnResizeEvent(clientW, clientH);
                        PerformLayout();   // a Dock is applied when one is SET, so a resize has to re-apply it
                        OnResize(EventArgs.Empty);
                        // Notify all children of parent resize
                        foreach (var ctrl in Controls)
                            ctrl.OnParentResized();
                        // Keep an adopted foreign child (e.g. ActiveX host) fitted to the client area
                        SizeAdoptedChild();
                        // REPAINT, which is the half a resize was missing: moving and resizing the children leaves what the
                        // PREVIOUS geometry painted, because nothing invalidates it. Measured on the compiler UI, whose log
                        // box kept a cascade of stale scrollbars after every drag, which is exactly "the controls are not
                        // repainted once WM_SIZE ended".
                        //
                        // The flags are the engine's own shape for this (see Control.RefreshFrameAndTheme): ALLCHILDREN is
                        // what matters for a docked child, whose bars are its own, and FRAME covers the non-client area a
                        // resize recomputes. UPDATENOW is deliberately NOT here: the two places that record that decision
                        // say forcing a synchronous paint of the window and every child is the flicker they exist to avoid,
                        // and an invalidation queued for the next paint is enough to clear what the old geometry left.
                        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN);
                    }
                    finally { inSizeMove = false; }
                    return IntPtr.Zero;

                case WM_PAINT:
                    base.WndProc(hwnd, msg, wParam, lParam);
                    return IntPtr.Zero;

                case WM_ERASEBKGND:
                    // 1. If script has attached an EraseBackground handler, invoke it.
                    if (EraseBackground != null)
                    {
                        // The HDC is the erase's whole input, so it travels as the engine's own PaintEventArgs, the
                        // same shape the Paint event already passes, instead of a bare IntPtr: a handler declared
                        // (Control s, EventArgs e) took the raw handle as `e` and the engine warned about the type.
                        GetClientRect(hwnd, out var eraseRect);
                        var result = InvokeSafelyWithResult(this, nameof(EraseBackground), EraseBackground, this,
                            new PaintEventArgs(hwnd, wParam, new Rect
                            {
                                Left = eraseRect.Left,
                                Top = eraseRect.Top,
                                Right = eraseRect.Right,
                                Bottom = eraseRect.Bottom
                            }));
                        if (IsTruthy(result))
                            return 1;
                    }

                    // 2. Fill the client with the window's own background. The class brush is NULL
                    //    (hbrBackground = IntPtr.Zero, to avoid flash), so THIS handler is the only erase a
                    //    client gets, and returning 1 keeps DefWindowProc off the null brush. It used to fill
                    //    dark whatever the theme was, so a window whose DarkMode is false kept a dark client for
                    //    good: measured with GetPixel on a form, the client read 0x202020 at every step,
                    //    including after DarkMode = false. A dark window keeps the dark fill; a light one gets
                    //    its own background, white when it has none (the same neutral a stock light list uses).
                    var ownBg = _backgroundColor;   // this window's own colour wins: BackgroundColor reaches the erase
                    if (ownBg.HasValue)
                        return Control.FillClientColor(hwnd, wParam, ref _ownClientBrush, ref _ownClientColor,
                                                       ownBg.Value & 0xFFFFFF);
                    // A Custom form fills its client from the palette, so the window and every control the engine
                    // colours agree without the script setting a colour on anything.
                    if (this is Form formPalette && formPalette.Theme == ThemeMode.Custom)
                        return Control.FillClientColor(hwnd, wParam, ref _ownClientBrush, ref _ownClientColor,
                                                       formPalette.CustomBackground() & 0xFFFFFF);
                    if (this is Form formTheme && formTheme.DarkMode)
                        return Control.FillClientDark(hwnd, wParam, ref _darkBackgroundBrush);
                    return Control.FillClientLight(hwnd, wParam);

                case WM_DPICHANGED:
                    {
                        var newDpiX = Loword(wParam);
                        var newDpiY = Hiword(wParam);
                        var scaleX = (float)newDpiX / _initialDpiX;
                        var scaleY = (float)newDpiY / _initialDpiY;
                        _currentDpiX = newDpiX;
                        _currentDpiY = newDpiY;

                        // Resize form to the suggested rect from lParam
                        if (lParam != IntPtr.Zero)
                        {
                            var rect = Marshal.PtrToStructure<Rect>(lParam);
                            SetWindowPos(hwnd, IntPtr.Zero,
                                rect.Left, rect.Top,
                                rect.Right - rect.Left, rect.Bottom - rect.Top,
                                SWP_NOZORDER | SWP_NOACTIVATE);
                        }

                        // Scale controls from stored design bounds (absolute)
                        foreach (var ctrl in Controls)
                            ctrl.ScaleByDpi(scaleX, scaleY);

                        // Update fonts for the new DPI
                        // Each control re-applies ITS OWN font rather than being handed the DPI default: a control with a
                        // FontSize/FontName/FontBold set (a script's log box, say) otherwise loses it the moment the window
                        // moves to a monitor of another DPI, which is what made a dragged window revert to the size it was
                        // never told to use. A control that set none falls back to the DPI default inside ApplyFont, so the
                        // result is unchanged for it.
                        ApplyFont();
                        EnumChildWindows(hwnd, static (child, _) =>
                        {
                            FromHandle(child)?.ApplyFont();
                            return true;
                        }, IntPtr.Zero);

                        // The frame is part of what a DPI change alters (its bars are measured for another
                        // DPI), so RDW_FRAME belongs here with the children: without it this redraw covers the
                        // client areas only, and the form's own frame kept the pixels it had.
                        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                                     RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);

                        // ScaleByDpi derives each control's bounds from its DESIGN bounds, and PerformLayout is not
                        // run by anything but the Dock setter, so a docked control used to be left at its design-scaled
                        // size after a monitor move: a Bottom status bar came out 200 x scale wide by 24 x scale tall
                        // instead of spanning the window, and the scaling overwrote a script's own layout of an
                        // undocked control. Re-running the layout after the scaling is what keeps both honest.
                        PerformLayout();

                        // ...and the DRAWING half: each child's non-client area (the scrollbars of a text box, a
                        // list, a tree) and its theme parts, which the size scaling above cannot reach.
                        foreach (var ctrl in Controls)
                            RefreshFrameAndTheme(ctrl.Handle);
                        RefreshFrameAndTheme(hwnd);

                        // NO RE-CENTRING HERE, DELIBERATELY, and it cost a round to learn: this arm runs INSIDE a
                        // modal move loop when the user drags a window across a boundary, and moving the window from
                        // here fights the OS's own drag bookkeeping, so the pointer and the window come apart
                        // ("the dragged form appears on screen 1 but the mouse pointer appears 400 px after the end
                        // of the form"). The suggested rect above already places the window where the drag wants it,
                        // and a form that must be centred is centred where a SCRIPT asks for it: the Screen setter
                        // re-centres, and a requested screen is honoured at creation.

                        // A system-DPI-aware COM/ActiveX child (e.g. WMP) does not
                        // rescale with the host, per-monitor hosts must RE-HOST it on
                        // a DPI change so it lays out at the new monitor's DPI.
                        if (_adoptedChild != IntPtr.Zero)
                        {
                            GetClientRect(Handle, out var cr);
                            var cw = cr.Right - cr.Left;
                            var chh = cr.Bottom - cr.Top;
                            var w2 = cw - _adoptLeft - _adoptRight;
                            var h2 = chh - _adoptTop - _adoptBottom;
                            if (w2 < 0) w2 = 0;
                            if (h2 < 0) h2 = 0;
                            var newChild = OleEmbedRehost.RehostChild(_adoptedChild, Handle,
                                _adoptLeft, _adoptTop, w2, h2);
                            if (newChild != IntPtr.Zero)
                            {
                                _adoptedChild = newChild;
                            }
                            else
                            {
                                // Re-host refused, at least tell the control and refit it.
                                SendMessageW(_adoptedChild, WM_DPICHANGED, (nuint)wParam, lParam);
                                SizeAdoptedChild();
                            }
                        }
                        return new IntPtr(-1);
                    }
            }

            try
            {
                return base.WndProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Form] EXCEPTION in WndProc (msg=0x{msg:X}): {ex}");
                // Do not rethrow – allow default processing to keep window alive
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        private IntPtr HandleCtlColor(IntPtr hdc, uint msg)
            => Control.HandleCtlColorDark(hdc, msg,
                ref _darkBackgroundBrush, ref _darkEditBrush, ref _darkListBoxBrush, ref _darkButtonBrush);

        public new void Dispose()
        {
            if (_paletteBrush != 0) { DeleteObject(_paletteBrush); _paletteBrush = 0; }
            Control.DisposeDarkBrushes(ref _darkBackgroundBrush, ref _darkEditBrush, ref _darkListBoxBrush, ref _darkButtonBrush);

            base.Dispose();
        }

        [ThreadStatic]
        private static WndProcDelegate? _wndProcDelegate;

        private static void RegisterWindowClass()
        {
            if (_classRegistered) return;

            var hInstance = GetModuleHandleW(null!);
            if (hInstance == IntPtr.Zero)
                throw new ScriptException("GetModuleHandle failed.");

            _wndProcDelegate = StaticWndProc;
            var wc = new Wndclassexw
            {
                cbSize = (uint)Marshal.SizeOf<Wndclassexw>(),
                // Remove CS_HREDRAW/CS_VREDRAW to avoid full-window redraw on resize
                // Only invalidate newly exposed areas (less flicker)
                style = CS_DBLCLKS,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = hInstance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                // The running executable's own icon, so every form carries the application's face without the
                // script asking. `form.Icon` overrides it per window; a build with no icon resource leaves both
                // zero and Windows falls back to its default, which is the old behaviour.
                hIcon = ExeIconHandle(),
                hIconSm = ExeIconHandle(),
                // Null background brush, we handle WM_ERASEBKGND ourselves
                // to avoid the default system-color flash during resize
                hbrBackground = IntPtr.Zero,
                lpszClassName = Marshal.StringToHGlobalUni(WindowClassName)
            };
            var atom = RegisterClassExW(ref wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                throw new ScriptException($"RegisterClassEx failed: {err}");
            }
            _classRegistered = true;
        }
    }
}

