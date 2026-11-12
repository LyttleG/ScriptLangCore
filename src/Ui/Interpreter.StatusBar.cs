namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class StatusBar : Control
    {
        private const string WindowClass = "msctls_statusbar32";
        private int[] _partWidths = [-1];  // -1 = single part fills the entire width
        private readonly Dictionary<int, IntPtr> _stringPtrs = [];
        // The text is kept MANAGED as well as handed to the control, because a dark host paints the parts itself
        // (the native control draws its own text with the theme's colour and never asks the host for one).
        private readonly Dictionary<int, string> _partText = [];
        private bool _darkHost;

        public StatusBar(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            // CreateNativeControl, not a raw CreateWindowExW: that is what installs the engine's window proc and
            // registers the handle in HandleMap. Created raw, this control had NO window proc at all, so nothing
            // the engine does to a child could reach it: its WM_PAINT never arrived (the dark paint below never
            // ran), its colours could not be applied, and `Control.FromHandle(hwnd) is StatusBar` was false for
            // every lookup (which is why the dark host's walk could not treat it specially).
            CreateNativeControl(WindowClass, null,
                WS_CHILD | WS_VISIBLE | SBARS_SIZEGRIP,
                0, ParentHandle, IntPtr.Zero);

            // A status bar follows its HOST's theme rather than being dark on principle: see ApplyHostTheme.
            ApplyHostTheme(Parent is Form { DarkMode: true });
            // NO font is sent from here. CreateNativeControl already put the engine's font on the window, and this
            // used to send DEFAULT_GUI_FONT on top of it, which is why a FontSize assigned BEFORE Show was lost:
            // `_currentFont` was right but the window held the stock font, and nothing re-applied the engine's own.

            // Register in the static map, and subclass the native window so script mouse events fire: without this
            // a StatusBar (like a TabControl or a ToolBar) receives no MouseDown at all, so a designer cannot drag
            // it. Measured with a placed control of each kind and a posted click, the same way ComboBox, TreeView
            // and DateTimePicker were found. Same pair ListBox and ListView already call.
            RegisterHandle();
            SubclassNativeChild();

            // A msctls_statusbar32 fits its HOST's client area by CLASS, so the rect it was created with is not what
            // it ends up with: measured, a placed status bar's model rect was 1066x96 at (0,1153) while its window
            // was 1066x30 at (1161,1321), so a click at the model's centre reached the form. Re-assert the rect the
            // engine was given, which is what an absolutely placed control means; the Dock layout calls SetWindowPos
            // for a docked one itself.
            SetWindowPos(handle, IntPtr.Zero, Left, Top, Width, Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Style for a dark or light host. A status bar in dark mode gets NO theme: `DarkMode_Explorer`
        /// draws its text BLACK on the dark background (measured in the compiler tool: 0x000000 glyphs on a
        /// 0x303030 bar, unreadable), which is the one control that theme does not serve and the reason the dark
        /// walk used to claim it was excluded while the code applied the theme anyway. With the theme off, the
        /// status bar asks its host for colours, and TextColor/BackgroundColor are applied by the SAME
        /// `WM_CTLCOLOR*` path every other control uses, so the text comes out white on 0x202020. A light host
        /// gets the stock look back, which also stops a status bar in a light form from being dark.</summary>
        internal void ApplyHostTheme(bool dark)
        {
            if (handle == IntPtr.Zero) return;
            _darkHost = dark;
            SetWindowTheme(handle, "", "");
            // The bar reads the SAME theme state every other arm reads, so a Custom host's palette and the leg's own
            // constants resolve in one place, and an MDI host is reached through that state rather than through a
            // Parent test that cannot see it.
            var theme = Control.ThemeStateOf(this);
            if (theme.IsCustom())
            {
                var pBg = theme.Background();
                var pFg = theme.Text();
                // A colour the SCRIPT set on the bar WINS, which is the rule the palette follows for every other
                // control: without this the theme pass overwrote it, so `statusBar.TextColor = ...` left the colour
                // unchanged (reported). The pass runs more than once (creation, show, a resize, a DPI change), so an
                // overwrite here is not a one-off: it takes the script's colour away every time.
                // ThemeTinted is what makes the test mean that, because HasScriptColor reads the fields the THEME's
                // own writes fill in: on that alone a bar could never take the palette of a host that had been dark
                // first, which is how a dark form switched to a light Custom palette kept the engine's dark colours.
                var scriptColor = HasScriptColor && !ThemeTinted;
                if (!scriptColor) { TextColor = pFg; BackgroundColor = pBg; ThemeTinted = true; }
                SendMessageW(handle, SB_SETBKCOLOR, 0, (nint)(uint)ToColorRef((uint)(BackgroundColor ?? pBg)));
                InvalidateRect(handle, IntPtr.Zero, true);
                Interpreter.UpdateWindow(handle);   // qualified: the member of this name shadows the import
                return;
            }
            if (dark)
            {
                SendMessageW(handle, SB_SETBKCOLOR, 0, 0x00202020);

                if (!HasScriptColor || ThemeTinted) { TextColor = theme.Text(); BackgroundColor = theme.Background(); ThemeTinted = true; }
            }
            else
            {
                SendMessageW(handle, SB_SETBKCOLOR, 0, 0x00FFFFFF);
                if (!HasScriptColor || ThemeTinted) { TextColor = null; BackgroundColor = null; ThemeTinted = false; }
                BackgroundColor = null;
            }
            InvalidateRect(handle, IntPtr.Zero, true);
            Interpreter.UpdateWindow(handle);   // qualified: the member of this name shadows the import
        }

        /// <summary>Whether this status bar paints its own surface (a dark host). The child window proc asks, and
        /// paints it there, because a control's WM_PAINT is not forwarded to its WndProc otherwise.</summary>
        internal bool DarkHost => _darkHost;

        /// <summary>Paint the whole bar: 0x202020 with the parts' text in white. A status bar never asks its host
        /// for colours (measured on a themed one: 0x000000 glyphs on our 0x202020 background, and setting
        /// TextColor/BackgroundColor changed nothing on screen), and the WM_CTLCOLOR* path the engine colours
        /// every other control with goes to the PARENT, which cannot be received by the control's own proc.</summary>
        internal void PaintDark()
        {
            var hdc = BeginPaint(handle, out var ps);
            if (hdc == IntPtr.Zero) return;
            GetClientRect(handle, out var client);
            // The colours ApplyHostTheme resolved (a custom host's palette, or the leg's pair), so the bar's own
            // paint and its reported colours cannot disagree. They are read from the SAME state that wrote them
            // rather than respelled, which is why the two cannot drift.
            var theme = ThemeStateOf(this);
            var brush = CreateSolidBrush(ToColorRef((uint)(BackgroundColor ?? theme.Background())));
            if (brush != IntPtr.Zero)
            {
                FillRect(hdc, ref client, brush);
                DeleteObject(brush);
            }
            SetBkMode(hdc, 1);            // TRANSPARENT
            Interpreter.SetTextColor(hdc, ToColorRef((uint)(TextColor ?? theme.Text())));
            // Select the control's OWN font: a DC from BeginPaint carries the SYSTEM font, so without this every
            // status bar drew its text at the default size and a FontSize had no visible effect (measured: the
            // window's font and the engine's `_currentFont` were right, and the drawn glyphs were the system's).
            // The old object goes back, as a DC's font must.
            var previousFont = CurrentFontHandle != IntPtr.Zero ? SelectObject(hdc, CurrentFontHandle) : IntPtr.Zero; const uint DT_LEFT = 0x0000, DT_VCENTER = 0x0004, DT_SINGLELINE = 0x0020;
            var left = client.Left;
            for (var i = 0; i < _partWidths.Length; i++)
            {
                // SB_SETPARTS semantics: an entry is the part's RIGHT edge, and -1 means the client's edge.
                var edge = _partWidths[i];
                var right = edge < 0 ? client.Right : edge;
                if (_partText.TryGetValue(i, out var text) && text.Length > 0)
                {
                    var cell = new Rect
                    {
                        Left = left + 4,
                        Top = client.Top + 2,
                        Right = right - 2,
                        Bottom = client.Bottom - 2
                    };
                    DrawTextW(hdc, text, text.Length, ref cell, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
                }
                left = right;
            }
            if (previousFont != IntPtr.Zero) SelectObject(hdc, previousFont);
            // The SIZE GRIP, which the engine's own paint would otherwise lose: the native status bar draws it,
            // and this path returns without forwarding to the native painting, so the grip disappeared the moment
            // the engine took the bar over (reported: no more grip at the bottom right of the status bar).
            // DrawFrameControl is what the control itself uses, so the grip looks native. The style is read
            // rather than assumed, because a script can take SBARS_SIZEGRIP off with ModStyle, and then the grip
            // must go too.
            if (((uint)GetWindowLongPtrW(handle, GWL_STYLE) & SBARS_SIZEGRIP) != 0)
            {
                var grip = (int)GetSystemMetrics(SM_CXVSCROLL);
                // The dots are drawn HERE rather than with DrawFrameControl(DFCS_SCROLLSIZEGRIP), and the reason is
                // colour: that call keeps the theme's own light pair whatever the DC holds, so it came back WHITE on a
                // navy bar (reported: the grip reappeared with a white caret inside instead of an adapted colour). The
                // engine paints the rest of this bar, so it paints the grip too, in the bar's own text colour.
                var gripBrush = CreateSolidBrush(ToColorRef((uint)(TextColor ?? theme.Text())));
                if (gripBrush != IntPtr.Zero)
                {
                    // The native grip, MEASURED off a light host status bar rather than guessed: three pixel wide
                    // diagonal bands, three apart, running from the bottom left up to the top right, clipped into the
                    // corner triangle. Two attempts before this one were wrong in opposite directions (2x2 squares
                    // with a 2 px step read as solid stripes, 1 px dots as a hollow outline), which is why the pattern
                    // is now copied from the native control pixel for pixel instead of reasoned about.
                    // The grip's size, and the two things it must NOT be: the SYSTEM DPI's metric (GetSystemMetrics
                    // answers for the primary monitor, which on a 144 DPI box is 26 px, drawn on a 16 px tall bar and
                    // reported as "thick triangle lines"), and taller than the bar it sits in. So: the metric for THIS
                    // window's DPI, clamped to the bar's height, which keeps the pattern and makes the grip smaller.
                    var barHeight = client.Bottom - client.Top;
                    var size = (int)GetSystemMetricsForDpi(SM_CXVSCROLL, GetDpiForWindow(handle));
                    if (size > barHeight - 3) size = barHeight - 3;
                    if (size < 6) size = 6;
                    for (var row = 0; row < size; row++)
                    {
                        for (var band = 0; band < 4; band++)
                        {
                            var from = 2 + row + band * 6;
                            if (from + 3 > size) { continue; }   // the triangle: a band past the corner is not drawn
                            var dot = new Rect
                            {
                                Left = client.Right - size + from,
                                Top = client.Bottom - 1 - row,
                                Right = client.Right - size + from + 3,
                                Bottom = client.Bottom - row
                            };
                            FillRect(hdc, ref dot, gripBrush);
                        }
                    }
                    DeleteObject(gripBrush);
                }
            }
            EndPaint(handle, ref ps);
        }

        public override void OnParentResized()
        {
            if (Parent == null) return;
            // A DOCKED status bar belongs to the dock layout, which is also the only thing that knows the host's
            // Padding: positioning it here put it back on the client's edge and against the FULL width, so a
            // docked bar came out wider than the box above it (measured in the compiler tool: 1588 against 1580)
            // and it sat on the frame. Undocked keeps the old behaviour, which is what an absolute position wants.
            if (Dock != DockStyle.None)
            {
                SetParts(_partWidths);
                return;
            }
            Left = 0;
            Top = Parent.Height - Height;
            Width = Parent.Width;
            SetBounds(Left, Top, Width, Height);
            // Reapply parts so the single pane fills the new width
            SetParts(_partWidths);
        }

        public void SetText(int part, string text)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return;

            // Reapply parts to ensure they are set
            var partsPtr = Marshal.AllocHGlobal(_partWidths.Length * sizeof(int));
            Marshal.Copy(_partWidths, 0, partsPtr, _partWidths.Length);
            SendMessageW(handle, SB_SETPARTS, (nuint)_partWidths.Length, partsPtr);
            Marshal.FreeHGlobal(partsPtr);
            _partText[part] = text;   // the dark paint draws from here, since the control's own text cannot be coloured

            // Free old ANSI string pointer
            if (_stringPtrs.TryGetValue(part, out var oldPtr))
            {
                Marshal.FreeHGlobal(oldPtr);
                _stringPtrs.Remove(part);
            }

            // Use ANSI version (SB_SETTEXTA) for compatibility
            var ptr = Marshal.StringToHGlobalAnsi(text);
            _stringPtrs[part] = ptr;
            SendMessageA(handle, SB_SETTEXTA, (nuint)part, ptr);
            InvalidateRect(handle, IntPtr.Zero, true);
            Interpreter.UpdateWindow(handle);   // qualified: the member of this name shadows the import
        }

        private void SetParts(int[] widths)
        {
            _partWidths = widths;
            if (handle == IntPtr.Zero || !IsWindow(handle))
                return;

            var ptr = Marshal.AllocHGlobal(widths.Length * sizeof(int));
            Marshal.Copy(widths, 0, ptr, widths.Length);
            _ = SendMessageW(handle, SB_SETPARTS, (nuint)widths.Length, ptr);
            Marshal.FreeHGlobal(ptr);
            InvalidateRect(handle, IntPtr.Zero, true);
            Interpreter.UpdateWindow(handle);   // qualified: the member of this name shadows the import
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetParts(_partWidths);
            OnParentResized();
        }

        protected override IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg != WM_CTLCOLORSTATIC) return base.WndProc(hwnd, msg, wParam, lParam);
            if (Parent is not Form { DarkMode: true }) return base.WndProc(hwnd, msg, wParam, lParam);
            Interpreter.SetTextColor(wParam, 0xFFFFFF);
            SetBkColor(wParam, 0x202020);
            return GetStockObject(BLACK_BRUSH);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { /* managed cleanup, nothing yet */ }

            foreach (var ptr in _stringPtrs.Values)
                Marshal.FreeHGlobal(ptr);
            _stringPtrs.Clear();
            base.Dispose(disposing);
        }
    }
}

