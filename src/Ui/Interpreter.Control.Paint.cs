namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public abstract partial class Control
    {
        private static void PaintTrackBar(TrackBar tb)
        {
            var hwnd = tb.handle;
            GetClientRect(hwnd, out var rc);
            var w = rc.Right - rc.Left;
            var h = rc.Bottom - rc.Top;
            var hdc = GetDC(hwnd);
            if (hdc == IntPtr.Zero) return;
            try
            {
                var bg = (uint)(tb._backgroundColor ?? 0x202020);
                var fg = (uint)(tb._textColor ?? 0xFFFFFF);
                if (!tb.Enabled) fg = 0x808080;
                if (!tb.Enabled) fg = 0x808080;
                var crbg = ToColorRef(bg);
                var crfg = ToColorRef(fg);
                var brush = CreateSolidBrush(crbg);
                if (brush != IntPtr.Zero) { FillRect(hdc, ref rc, brush); DeleteObject(brush); }
                // horizontal track
                var trackH = Math.Max(4, h / 8);
                var track = new Rect
                {
                    Left = 6,
                    Top = rc.Top + (h - trackH) / 2,
                    Right = rc.Right - 6,
                    Bottom = rc.Top + (h - trackH) / 2 + trackH
                };
                var lo = (double)tb.Minimum;
                var hi = (double)tb.Maximum;
                var frac = hi > lo ? (tb.Value - lo) / (hi - lo) : 0.0;
                if (frac < 0) frac = 0; if (frac > 1) frac = 1;
                var tBrush = CreateSolidBrush(crfg);
                if (tBrush != IntPtr.Zero)
                {
                    FillRect(hdc, ref track, tBrush);
                    DeleteObject(tBrush);
                }
                // thumb
                var tw = Math.Max(14, h * 2);
                var cx = track.Left + (int)((track.Right - track.Left) * frac);
                var th = Math.Max(16, h - 2);
                var thumb = new Rect
                {
                    Left = cx - tw / 2,
                    Top = rc.Top + (h - th) / 2,
                    Right = cx + tw / 2,
                    Bottom = rc.Top + (h - th) / 2 + th
                };
                var uBrush = CreateSolidBrush(0xFFFFFF);
                if (uBrush != IntPtr.Zero)
                {
                    FillRect(hdc, ref thumb, uBrush);
                    DeleteObject(uBrush);
                }
                // hairline above thumb for definition
                Interpreter.SetTextColor(hdc, crfg);
            }
            finally { ReleaseDC(hwnd, hdc); }
        }

        /// <summary>The face of a PRESSED ownerdraw button: each channel moves away from the colour's own average
        /// brightness, so a dark button lightens and a light one darkens, and either way the change is visible. A
        /// fixed delta would be invisible on one of the two.</summary>
        private static uint PressedFace(uint face)
        {
            var r = (int)((face >> 16) & 0xFF);
            var g = (int)((face >> 8) & 0xFF);
            var b = (int)(face & 0xFF);
            var avg = (r + g + b) / 3;
            var up = avg < 128;
            r = Step(r, up); g = Step(g, up); b = Step(b, up);
            return (uint)((r << 16) | (g << 8) | b);
        }

        private static int Step(int channel, bool up)
        {
            var v = up ? channel + 0x28 : channel - 0x28;
            if (v < 0) v = 0;
            if (v > 0xFF) v = 0xFF;
            return v;
        }

        // FrameRect: the 1px frame GDI already has, for the owner-drawn button's edge. A BRUSH, not a pen, which is why
        // it takes an HBRUSH: it fills the four one-pixel edges and leaves the middle alone. Declared beside the painter
        // that uses it, because this file holds several classes and the first user32 import belongs to another one.
        // LibraryImport, the pattern every other Windows call in this file uses, which is why this class is partial: the
        // generated half needs one. No EntryPoint: LibraryImport binds to the MEMBER's name, and the member is already
        // FrameRect, so naming it twice would be the redundant spelling the ClientToScreen note warns about.
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool FrameRect(nint hDC, ref Rect lprc, nint hbr);
        /// <summary>The side, in pixels, of the check box or radio button glyph this engine draws. DERIVED, the same
        /// way the text is: the control's point size through ITS OWN window DPI, which is why the box and the label
        /// scale together (19 px at 144 DPI with the 16 px default font, 13 at 96 with 11, the sizes Windows itself
        /// uses at those DPIs). A script's own FontSize is honoured, and the result is clamped to the client so a
        /// deliberately short control never paints outside itself.</summary>
        private static int GlyphSizeOf(Control ctrl)
        {
            var dpi = ctrl.Dpi;
            if (dpi <= 0) dpi = DesignDpi;
            var pts = ctrl.FontSize > 0 ? ctrl.FontSize : DefaultFontPixels * (double)PointsPerInch / DesignDpi;
            var px = (int)Math.Round(pts * dpi / (double)PointsPerInch);
            if (px <= 0) px = Math.Max(1, DefaultFontPixels * dpi / DesignDpi);
            var glyph = (int)Math.Round(px * 1.2);        // what Windows draws for an 11 px font is 13, for 16 is 19
            var room = ctrl.ClientRect.Bottom - ctrl.ClientRect.Top - 2;
            if (room > 6 && glyph > room) glyph = room;
            return glyph < 6 ? 6 : glyph;
        }
        private static nint DrawOwnerdrawButton(nint lParam)
        {
            var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
            var ctrl = FromHandle(dis.hwndItem);
            // NEVER DECLINE FOR A CONTROL THIS ENGINE OWNS. Declining was wrong the moment a button could be
            // ownerdraw WITHOUT a colour, which is exactly what happens when a style change lands BS_OWNERDRAW (the
            // style editor offers it, and setting a colour does it too): Windows sends WM_DRAWITEM and waits for the
            // app, so a declining painter leaves the control BLANK. Reported as "if I change any style the generated
            // code produces an empty button". The `?? 0xF0F0F0` / `?? 0x000000` defaults below already answer "no
            // colour was chosen" with the classic button, which is readable, so there is nothing to decline.
            if (ctrl is not ButtonBase)
                return IntPtr.Zero;
            // A CHECK BOX AND A RADIO BUTTON HAVE NO FACE AND NO FRAME: their label sits on the surface behind,
            // exactly as it did when USER32 drew them. Theirs is the glyph on the left, so with no colour of
            // their own they take the HOST's leg colours (Dark, Light or Custom), where a plain button keeps
            // the classic 0xF0F0F0 face it has always had.
            var isToggle = ctrl is CheckBox or RadioButton;
            var hostForm = Control.HostFormOf(ctrl);
            var bg = (uint)(ctrl._backgroundColor ?? (isToggle && hostForm != null ? hostForm.ThemeStateValue.Background() : 0xF0F0F0));
            var fg = (uint)(ctrl._textColor ?? (isToggle && hostForm != null ? hostForm.ThemeStateValue.Text() : 0x000000));
            if (!ctrl.Enabled) fg = 0x808080;
            // THE PRESSED STATE, which itemState was never read for, so a button painted the same whether it was
            // held down or at rest ("I don't see the button is pressed, it remains flat"). ODS_SELECTED is what a
            // button sends while the mouse is down. The face shifts AWAY from its own luminance, so the change is
            // visible on a dark face and on a light one, and the caption moves one pixel as well (below).
            var pressed = (dis.itemState & 0x0001) != 0;   // ODS_SELECTED
            // A TOGGLE'S FEEDBACK IS ITS GLYPH, NOT ITS PLATE. A check box and a radio button have no face (see above),
            // so tinting the plate for the pressed state paints a band across the whole control AND its caption: measured
            // on a dark panel whose colour is 0x202020, a held-down check box read 0x484848 over its full width, which is
            // the "greyed background over the check box and its text" a click shows. A push BUTTON keeps the shift: that
            // is the "I don't see the button is pressed" report this arm was added for, and it has a face to move.
            if (pressed && !isToggle) bg = PressedFace(bg);
            var crbg = ToColorRef(bg);
            var crfg = ToColorRef(fg);
            var br = CreateSolidBrush(crbg);
            if (br != IntPtr.Zero)
            {
                FillRect(dis.hDC, ref dis.rcItem, br);
                DeleteObject(br);
            }
            // A THIN BORDER, so an owner-drawn BUTTON reads as a button rather than as a coloured rectangle. A
            // check box and a radio button get none (see above): a frame around a label and its glyph is the
            // look of a button, not of a toggle. The colour is the host's palette border when the host states
            // one, and otherwise the face shifted a little darker. A disabled row keeps its frame, so it still
            // reads as buttons.
            if (!isToggle)
            {
                var borderColor = Control.HostFormOf(ctrl) is { Theme: ThemeMode.Custom } palette
                    ? palette.CustomBorder()
                    : PressedFace(bg);
                var brd = CreateSolidBrush(ToColorRef(borderColor));
                if (brd != IntPtr.Zero)
                {
                    FrameRect(dis.hDC, ref dis.rcItem, brd);
                    DeleteObject(brd);
                }
            }
            Interpreter.SetTextColor(dis.hDC, crfg);
            SetBkMode(dis.hDC, 1); // TRANSPARENT
            if (ctrl is CheckBox or RadioButton)
            {
                // THE GLYPH IS SIZED FROM THE CONTROL'S OWN FONT, which is what makes it follow the monitor: USER32
                // drew a 19 px box at both DPIs (see ButtonBase.CreateHandle), so on the lower-DPI screen it read
                // about 1.5x the label beside it. GlyphSizeOf derives the side the same way the text is derived
                // (the points through the window's own DPI), so the box and its label scale together.
                // colored glyph on the left + colored label on the right
                // The control's own state, not BM_GETCHECK: under BS_OWNERDRAW Windows keeps none (see CheckBox).
                var state = ctrl is CheckBox stateBox ? stateBox.Checked : SendMessageW(dis.hwndItem, BM_GETCHECK, 0, 0) != IntPtr.Zero;
                var g = GlyphSizeOf(ctrl);
                var unit = Math.Max(1, g / 10);                       // the mark's own unit, 1 for an 8 to 19 px glyph
                var gx = dis.rcItem.Left + 4;
                var gy = dis.rcItem.Top + (dis.rcItem.Bottom - dis.rcItem.Top - g) / 2;
                var glyph = new Rect { Left = gx, Top = gy, Right = gx + g, Bottom = gy + g };
                if (ctrl is RadioButton)
                {
                    var fr = CreateSolidBrush(crfg);
                    if (fr != IntPtr.Zero)
                    {
                        // outer ring: fill fg square, punch inner bg square
                        FillRect(dis.hDC, ref glyph, fr);
                        DeleteObject(fr);
                        var inset = Math.Max(2, g / 5);
                        var inner = new Rect { Left = gx + inset, Top = gy + inset, Right = gx + g - inset, Bottom = gy + g - inset };
                        var ib = CreateSolidBrush(crbg);
                        if (ib != IntPtr.Zero) { FillRect(dis.hDC, ref inner, ib); DeleteObject(ib); }
                        if (state)
                        {
                            var ring = Math.Max(1, g / 7);
                            var dot = new Rect
                            {
                                Left = gx + inset + ring,
                                Top = gy + inset + ring,
                                Right = gx + g - inset - ring,
                                Bottom = gy + g - inset - ring
                            };
                            var fb = CreateSolidBrush(crfg);
                            if (fb != IntPtr.Zero) { FillRect(dis.hDC, ref dot, fb); DeleteObject(fb); }
                        }
                    }
                }
                else
                {
                    var fr = CreateSolidBrush(crfg);
                    if (fr != IntPtr.Zero)
                    {
                        FillRect(dis.hDC, ref glyph, fr);              // square border area
                        DeleteObject(fr);
                        var edge = Math.Max(1, g / 8);
                        var inner = new Rect { Left = gx + edge, Top = gy + edge, Right = gx + g - edge, Bottom = gy + g - edge };
                        var ib = CreateSolidBrush(crbg);
                        if (ib != IntPtr.Zero) { FillRect(dis.hDC, ref inner, ib); DeleteObject(ib); }
                        if (state)
                        {
                            // diagonal check in the square, on the glyph's own unit so it scales with it
                            var cb2 = CreateSolidBrush(crfg);
                            if (cb2 != IntPtr.Zero)
                            {
                                var c1 = new Rect { Left = gx + 3 * unit, Top = gy + 5 * unit, Right = gx + 5 * unit, Bottom = gy + 7 * unit };
                                var c2 = new Rect { Left = gx + 4 * unit, Top = gy + 7 * unit, Right = gx + 6 * unit, Bottom = gy + 9 * unit };
                                var c3 = new Rect { Left = gx + 6 * unit, Top = gy + 4 * unit, Right = gx + 9 * unit, Bottom = gy + 6 * unit };
                                FillRect(dis.hDC, ref c1, cb2);
                                FillRect(dis.hDC, ref c2, cb2);
                                FillRect(dis.hDC, ref c3, cb2);
                                DeleteObject(cb2);
                            }
                        }
                    }
                }
                var tr = new Rect
                {
                    Left = glyph.Right + 4 + unit,
                    Top = dis.rcItem.Top,
                    Right = dis.rcItem.Right - 4,
                    Bottom = dis.rcItem.Bottom
                };
                if (!string.IsNullOrEmpty(ctrl.Text))
                    DrawTextW(dis.hDC, ctrl.Text, -1, ref tr, 0x01 | 0x04 | 0x20);
                return (nint)1;
            }
            var txt = ctrl.Text;
            if (!string.IsNullOrEmpty(txt))
            {
                var rc = dis.rcItem;
                if (pressed) { rc.Left += 1; rc.Top += 1; rc.Right += 1; rc.Bottom += 1; }
                DrawTextW(dis.hDC, txt, -1, ref rc, 0x01 | 0x04 | 0x20); // CENTER | VCENTER | SINGLELINE
            }
            return (nint)1;
        }


        /// <summary>Marks the control for REPAINTING, which is what a script that changes a drawing asks for. Nothing
        /// is painted here and nothing waits for one: the engine deliberately avoids the synchronous redraws that
        /// flicker (see ApplyWindowStyle&#39;s own note), so this is the door instead of reaching for WinApi.InvalidateRect.</summary>
        public void Invalidate()
        {
            if (handle != IntPtr.Zero) { InvalidateRect(handle, IntPtr.Zero, true); }
        }

        /// <summary>Invalidate JUST THIS RECTANGLE, which is what a DRAG wants: the whole-client form above repaints
        /// everything, and a form that paints a grid redraws every dot for it (reported by the maintainer: moving a
        /// control over another flickered the form&#39;s background). In client coordinates and a script&#39;s own numbers,
        /// rather than a Rect a script cannot construct. It erases, because a caller that moves something is filling
        /// what it vacates: that fill IS the erase, and the paint then covers the rest.</summary>
        public void Invalidate(int x, int y, int width, int height)
        {
            if (handle == IntPtr.Zero) return;
            var native = new RECT { left = x, top = y, right = x + width, bottom = y + height };
            // The interop takes a POINTER, so the rectangle is marshalled here rather than passed as managed memory:
            // nothing outside this method ever sees a raw address.
            var size = Marshal.SizeOf<RECT>();
            var mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(native, mem, false);
                InvalidateRect(handle, mem, true);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        /// <summary>Invalidate the UNION of two rectangles: the generic shape a DRAG wants, since the only pixels that
        /// can have changed are where the thing WAS and where it IS. In the engine rather than in a caller, because the
        /// arithmetic is the same every time and belongs next to the invalidate it feeds. Each rectangle is (x, y, width,
        /// height) in client coordinates, in a script's own numbers.</summary>
        public void InvalidateUnion(int x1, int y1, int w1, int h1, int x2, int y2, int w2, int h2)
        {
            var left = Math.Min(x1, x2);
            var top = Math.Min(y1, y2);
            var right = Math.Max(x1 + w1, x2 + w2);
            var bottom = Math.Max(y1 + h1, y2 + h2);
            Invalidate(left, top, right - left, bottom - top);
        }

        /// <summary>Invalidate the union of the rectangle a script NAMES as (x, y) and the one an OBJECT occupies, which
        /// is the drag's shape at its shortest: the caller knows where the thing WAS, and the thing itself knows its own
        /// place and size, so only the old origin has to be spelled out. X, Y, W and H are read off the object BY NAME
        /// through the engine's own member access, which is what the designer's model uses. A name that is not there reads
        /// as 0 rather than throwing, so a wrong object gives a rectangle at the origin: a harmless extra repaint rather
        /// than an error in the middle of a drag.</summary>
        public void InvalidateUnion(int x, int y, object sized)
        {
            var mi = Interpreter.Current;
            if (mi == null) { Invalidate(); return; }   // no interpreter: the whole client is the safe answer
            var w = MemberInt(mi, sized, "W");
            var h = MemberInt(mi, sized, "H");
            InvalidateUnion(x, y, w, h, MemberInt(mi, sized, "X"), MemberInt(mi, sized, "Y"), w, h);
        }
        protected virtual void OnPaint(PaintEventArgs e)
        {
            if (Paint != null)
            {
                InvokeSafely(this, nameof(Paint), Paint, this, e);
            }
            else
            {
            }
        }

        /// <summary>
        /// A CHECK BOX AND A RADIO BUTTON ARE SIZED FOR THEIR OWN TEXT ONLY, because this engine DRAWS their glyph now
        /// (see ButtonBase.CreateHandle) and sizes it from the same font, so the glyph cannot be clipped by the
        /// control. The history is worth keeping, because this method has been on both sides of the question: the
        /// glyph USER32 drew was 19 px at BOTH DPIs, so a 24 unit check box scaled to 16 px came out as a rectangle
        /// with its bottom cut off, and a first version of this floor raised the height to fit that glyph (the
        /// theme's part metric), which then made the box read about 1.5x too big on the lower-DPI monitor. What is
        /// left is the TEXT: a label wider than the control was truncated ("Top level on th"), so the width is raised
        /// to BCM_GETIDEALSIZE's answer, and only the WINDOW is resized, never the design size, so a round trip
        /// through another monitor cannot accumulate growth.
        /// </summary>
        internal void ApplyCheckGlyphFloor()
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return;
            if (this is not (CheckBox or RadioButton)) return;

            var size = Marshal.AllocHGlobal(sizeof(int) * 2);
            try
            {
                Marshal.WriteInt32(size, 0, 0);
                Marshal.WriteInt32(size, 4, 0);
                if (SendMessageW(handle, BCM_GETIDEALSIZE, 0, size) == IntPtr.Zero) return;
                var neededW = Marshal.ReadInt32(size, 0);
                var neededH = Marshal.ReadInt32(size, 4);
                if (neededW <= 0 && neededH <= 0) return;
                GetWindowRect(handle, out var wr);
                var width = wr.Right - wr.Left;
                var height = wr.Bottom - wr.Top;
                var newW = neededW + 4 > width ? neededW + 4 : width;
                var newH = neededH > height ? neededH : height;
                if (newH == height && newW == width) return;   // already fits its own label
                SetWindowPos(handle, IntPtr.Zero, 0, 0, newW, newH,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            finally
            {
                Marshal.FreeHGlobal(size);
            }
        }
        /// <summary>How many times this window has been asked to PAINT itself. It is the instrument a "it flickers" report
    /// needs and nothing else could give: a child's WM_PAINT is not forwarded to a script handler, so no script can count
    /// them from outside, and the number is what separates the three causes (a clear-then-fill seen empty, a rebuild that
    /// destroys a child and leaves its band painted, and a control that repaints once per appended item). Measured on the
    /// clipboard runner's Wrap toggle: 3 child paints per swap before the bracket below existed, and 0 after it, with the
    /// panel painting once at the end.</summary>
    public int Paints => _paints;

    /// <summary>Counted by the two places a paint arrives (the engine's own window procedure and the child subclass
    /// forward), so a control that paints natively is counted too.</summary>
    internal void NotePaint() => _paints++;

        /// <summary>
        /// <summary>Fills a client with the dark background and answers WM_ERASEBKGND's "handled" (1). FOUR sites
        /// used to spell this out: a Form, an MDI frame, an MDI client and a control whose class paints its whole
        /// surface (DarkBody). One implementation, so a change to how a dark client is painted is made once. The
        /// brush is passed by ref so each caller keeps its own cached handle and the four share the paint, not the
        /// allocation.</summary>
        // internal, NOT protected: the callers include MdiForm and MdiChild, which are not Control descendants, so
        // they reach it as Control.FillClientDark. A protected helper compiled for the derived classes only, and the
        // build said so.
        /// <summary>The LIGHT sibling of FillClientDark, and the other half of the same rule: the window class
        /// brush is deliberately NULL (hbrBackground = IntPtr.Zero, "to avoid flash"), so a client that is NOT
        /// dark was never erased at all and kept whatever the last erase left. Measured with GetPixel on a form:
        /// the client read 0x202020 at every step, including with DarkMode = false, so a form could never go
        /// back to light. One STATIC brush, because white is not a per-window colour and cannot go stale (the
        /// dark one is per caller because a caller may own several windows).</summary>
        /// <summary>Fills a client with a colour the CALLER owns, caching one brush per caller and recreating it
        /// when the colour changes (a cached brush is per window: two windows with different colours would fight
        /// over a shared one). A window with its own BackgroundColor erases with THAT, which is what makes the
        /// property reach the erase at all: measured before, a form's client colour came from the theme and a
        /// script's own BackgroundColor never showed, which a suite recorded as "ignored on a form".</summary>
        internal static nint FillClientColor(nint hwnd, nint hdc, ref nint cached, ref uint cachedColor, uint color)
        {
            if (cached == IntPtr.Zero || cachedColor != color)
            {
                if (cached != IntPtr.Zero) DeleteObject(cached);
                cached = CreateSolidBrush(ToColorRef(color));
                cachedColor = color;
            }
            GetClientRect(hwnd, out var ownRect);
            FillRect(hdc, ref ownRect, cached);
            return 1;
        }
        internal static nint FillClientLight(nint hwnd, nint hdc)
        {
            if (_lightClientBrush == IntPtr.Zero) _lightClientBrush = CreateSolidBrush(0xFFFFFF);
            GetClientRect(hwnd, out var lightRect);
            FillRect(hdc, ref lightRect, _lightClientBrush);
            return 1;
        }
        internal static nint FillClientDark(nint hwnd, nint hdc, ref nint cached)
        {
            if (cached == IntPtr.Zero) cached = CreateSolidBrush(0x202020);
            GetClientRect(hwnd, out var darkRect);
            FillRect(hdc, ref darkRect, cached);
            return 1;
        }
    }
}
