namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public abstract partial class Control
    {
        /// <summary>The four bounds a control is created with, stated ONCE. Every control's constructor
        /// wants these same four assignments, and two of them (ListBox and ComboBox) had them copied
        /// verbatim, which is what tools/check-dup-bodies.sh refuses by name.</summary>
        protected void InitBounds(int x, int y, int width, int height)
        {
            Left = x;
            Top = y;
            Width = width;
            Height = height;
        }
        /// <summary>Space, in design pixels, this control keeps between itself and the band the DOCK layout gives
        /// it, so a docked box can stand off the window's edge (or off the status bar below it) without the whole
        /// container being padded. Zero by default, which fills the band exactly. The four sides can differ:
        /// <code>Margin</code> sets all of them and a side set on its own wins, which is what "20 pixels on the
        /// LEFT only, and still Fill" needs. The value scales with the DPI like every other design measurement,
        /// and only the DOCK layout reads it: an absolutely positioned control already sits where it was put.</summary>
        public int Margin
        {
            get => _margin;
            set
            {
                if (value < 0) throw new ScriptException($"Margin cannot be negative (got {value}).");
                _margin = value;
                RelayoutParent();
            }
        }

        /// <summary>One side of <see cref="Margin"/>; -1 (the default) means "use Margin".</summary>
        public int MarginLeft
        {
            get => _marginLeft;
            set { CheckSideMargin(value, nameof(MarginLeft)); _marginLeft = value; RelayoutParent(); }
        }

        /// <inheritdoc cref="MarginLeft"/>
        public int MarginTop
        {
            get => _marginTop;
            set { CheckSideMargin(value, nameof(MarginTop)); _marginTop = value; RelayoutParent(); }
        }

        /// <inheritdoc cref="MarginLeft"/>
        public int MarginRight
        {
            get => _marginRight;
            set { CheckSideMargin(value, nameof(MarginRight)); _marginRight = value; RelayoutParent(); }
        }

        /// <inheritdoc cref="MarginLeft"/>
        public int MarginBottom
        {
            get => _marginBottom;
            set { CheckSideMargin(value, nameof(MarginBottom)); _marginBottom = value; RelayoutParent(); }
        }

        private static void CheckSideMargin(int value, string name)
        {
            if (value < -1) throw new ScriptException($"{name} cannot be negative (got {value}); -1 means 'use Margin'.");
        }

        private void RelayoutParent()
        {
            if (Parent is Form form && form.Handle != IntPtr.Zero) form.PerformLayout();
        }

        // The layout reads these, and it lives on Form, so the resolution of "a side or Margin" happens here.
        internal int ResolvedMarginLeft => _marginLeft >= 0 ? _marginLeft : (_margin > 0 ? _margin : -1);
        internal int ResolvedMarginTop => _marginTop >= 0 ? _marginTop : (_margin > 0 ? _margin : -1);
        internal int ResolvedMarginRight => _marginRight >= 0 ? _marginRight : (_margin > 0 ? _margin : -1);
        internal int ResolvedMarginBottom => _marginBottom >= 0 ? _marginBottom : (_margin > 0 ? _margin : -1);
        public DockStyle Dock
        {
            get => _dock;
            set
            {
                _dock = value;
                if (Parent is Form form)
                    form.PerformLayout(); // request layout update
            }
        }

        public virtual void OnParentResized() { }

        /// <summary>
        /// Native child controls (LISTBOX, trackbar, ...) deliver their notifications
        /// (LBN_*, WM_HSCROLL, ...) to the PARENT window, not to themselves. The parent
        /// Form's WndProc reflects those messages back here so each control's own
        /// WndProc override (e.g. TrackBar WM_HSCROLL, ListBox LBN_SELCHANGE) runs.
        /// </summary>
        internal IntPtr ReflectParentNotification(uint msg, IntPtr wParam, IntPtr lParam)
            => WndProc(handle, msg, wParam, lParam);

        public virtual void SetBounds(int left, int top, int width, int height)
        {
            // The FIELDS first, then the private SetBounds, which is the ONE window call. Assigning the four PROPERTIES
            // ran each setter, and each setter calls SetBounds() as well, so a single placement cost FIVE SetWindowPos
            // calls: the four setters plus the line that used to be here. The properties remain how a script sets ONE
            // of them; this is the door that does not pay for four.
            this.left = left; this.top = top; this.width = width; this.height = height;
            SetBounds();
        }

        /// <summary>Place and size this control in PIXELS, repainting it unless told not to: the twin of the native
        /// <c>MoveWindow</c>, for a layout computed from a window's own rectangle (the demos that fill a window from
        /// its client rect use it). <see cref="SetBounds"/> is the same job in DESIGN units, which is what the form
        /// factor's properties use. The rect here is PIXELS, and it is REMEMBERED as the control's place in design
        /// units, so a later scaling pass (a DPI change) keeps the layout a script computed rather than sending the
        /// control back to where it was created.</summary>
        public void MoveWindow(int left, int top, int width, int height, bool repaint = true)
        {
            if (handle != IntPtr.Zero) { Interpreter.MoveWindow(handle, left, top, width, height, repaint); }   // qualified: this method shadows the import by name
            // THE FIELDS A SCRIPT READS FOLLOW THE WINDOW. They are what Left/Top/Width/Height answer, and this call
            // used to leave them at whatever the control was CREATED or last assigned with: measured, a form moved to
            // x=3196 by the engine's own centring still reported Left=100, so a script that laid out another row from
            // it read a position the window had left behind. The DESIGN bounds are recorded separately (below), which
            // is what a later DPI pass scales from.
            this.left = left;
            this.top = top;
            this.width = width;
            this.height = height;
            RememberDesignBounds(left, top, width, height);
        }

        /// <summary>Where this control IS, in DESIGN units, recorded whenever a caller places it in PIXELS. This
        /// is what ScaleByDpi scales from, and without it a later scaling pass (the form landing on another-DPI
        /// monitor included) sent the control back to where it was CREATED. Measured on a two-monitor box: a label
        /// created at (0,0) and moved to (240,100) read back as 0,0, and a demo that creates every control at (0,0)
        /// then lays them out with this call came back ALL MERGED at the top-left when its window changed monitor.</summary>
        private void RememberDesignBounds(int pxLeft, int pxTop, int pxWidth, int pxHeight)
        {
            // The owner is the same one DesignUnits asks for: for a TOP-LEVEL window that is the window ITSELF, and
            // without it ToRef got a reference DPI of 0, which answers 1.0, so a form placed in pixels stored its
            // PIXELS as design units. Measured: a form moved to x=3500 on the 96 DPI monitor still reported Left=100
            // (its old design value) and a later scaling pass would have put it back on the first monitor.
            var owner = this as Form ?? Parent as Form;
            var toRef = ToRef(Dpi, owner?.ReferenceDpiX ?? 0);
            _designLeft = (int)Math.Round(pxLeft * toRef);
            _designTop = (int)Math.Round(pxTop * toRef);
            _designWidth = (int)Math.Round(pxWidth * toRef);
            _designHeight = (int)Math.Round(pxHeight * toRef);
            _designBoundsStored = true;
        }

        /// <summary>Raise this window to the TOP of its siblings, without moving, resizing or activating it. What a
        /// control needs when a sibling created LATER would otherwise cover it (a size handle under a newly placed
        /// control), and the pair of <see cref="ToBottom"/>: the Z-ORDER is the only thing either changes, so a
        /// script reading Left/Top/Width/Height back sees what it set.</summary>
        public void ToTop()
        {
            // hWndInsertAfter is HWND_TOP, which is 0, and 0 is what every other call here passes for it.
            if (handle != IntPtr.Zero) { SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); }
        }

        /// <summary>Send this window BELOW every sibling, which is what something drawn behind a control wants (an
        /// accent ring around a selected one), with the same three promises: no move, no resize, no activation.</summary>
        public void ToBottom()
        {
            if (handle != IntPtr.Zero) { SetWindowPos(handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); }
        }

        public void StoreDesignBounds()
        {
            if (!_designBoundsStored)
            {
                _designLeft = left;
                _designTop = top;
                _designWidth = width;
                _designHeight = height;
                _designBoundsStored = true;
            }
        }

        /// <summary>Give this control the focus, cue and all: FocusWithCue turns the focus cue back ON, which is the
        /// difference between being focused and being SEEN to be focused. With no argument it focuses this control, a
        /// form included, which is how a script puts a window back in front of the tab order. A control that cannot
        /// take the focus at all (a label) is left as it was, which is Win32&#39;s own answer, and Focused tells the
        /// truth about it afterwards.</summary>
        public void SetFocus() => FocusWithCue(handle);

        /// <summary>The same, for a control OF THIS WINDOW, named either way a script can hold it: an id, or a window
        /// handle. That is exactly what GetId takes, so form.SetFocus(btn.GetId()) and form.SetFocus(btn.Handle) are
        /// both this one call, and a form can focus something it only has a NUMBER for. A reference nothing carries is
        /// REFUSED rather than doing nothing in silence.</summary>
        public void SetFocus(object? reference)
        {
            var target = GetId(reference);
            if (target == null) { throw new ScriptException("SetFocus: nothing of this window carries that reference."); }
            target.SetFocus();
        }

        /// <summary>Where this control sits in its container&#39;s TAB ORDER, the VB property. It is an ORDERING VALUE
        /// and nothing more: not an id, not the handle, not the Z-order position, and two controls may share one. The
        /// walk sorts by it (ties broken by the order the controls were added, which is the Z-order) and takes the next
        /// one that can actually hold the focus, so nothing here moves a window or renumbers anything.</summary>
        public int TabIndex
        {
            get => _tabIndex;
            set => _tabIndex = value;
        }

        /// <summary>The window&#39;s own rectangle, which is what the control BECAME rather than what was asked for:
        /// Left/Top/Width/Height above are the REQUEST, and the two differ whenever the engine adjusted it (a size
        /// too small for its parts is raised to the minimum, a request off every monitor is moved onto one).</summary>
        public Rect WindowRect => WindowRectOf(handle);

        /// <summary>The CLIENT rectangle: the area this control can paint in, which is <see cref="WindowRect"/>
        /// minus the frame and any scrollbar the window reserved. A layout that wants to know how much room it
        /// actually has wants THIS one, and it is the twin of WindowRect so the two read alike.</summary>
        public Rect ClientRect => ClientRectOf(handle);

        /// <summary>The client area in DESIGN UNITS, which are the units of <see cref="Left"/> / <see cref="Top"/> /
        /// <see cref="Width"/> / <see cref="Height"/> and therefore of every child's geometry. It is the pair a layout
        /// checks itself against: how much room a window has, expressed in the same units as the controls placed in
        /// it, so a document that states it can be verified after a rebuild instead of assumed. 0 when there is no
        /// window yet, like <see cref="Dpi"/>.</summary>
        public int ClientWidth => DesignUnits(ClientRect.Right - ClientRect.Left);
        /// <inheritdoc cref="ClientWidth"/>
        public int ClientHeight => DesignUnits(ClientRect.Bottom - ClientRect.Top);

        /// <summary>Pixels in design units, for this control's own window.</summary>
        internal int DesignUnits(int px)
        {
            var owner = this as Form ?? Parent as Form;
            return (int)Math.Round(px * ToRef(Dpi, owner?.ReferenceDpiX ?? 0));
        }

        /// <summary>The DPI of the monitor this window is ON, read from the window rather than remembered (a form
        /// moved between two monitors answers differently, which is the whole point of it). 0 when there is no window
        /// yet, so a caller can tell "not created" from a real reading.</summary>
        public int Dpi => handle != IntPtr.Zero ? (int)GetDpiForWindow(handle) : 0;
        public Control? Parent
        {
            get => _parent;
            internal set
            {
                _parent = value;
                if (handle != IntPtr.Zero && value?.handle != IntPtr.Zero)
                    SetParent(handle, value?.handle ?? IntPtr.Zero);
            }
        }

        /// <summary>Whether the keyboard focus is on this control OR anywhere inside it, which is the question a panel
        /// asks before deciding that a copied value came from itself (a log window re-copying what its own child
        /// button just put on the clipboard sees the same notification the user's copy produces).</summary>
        public bool HasKeyboardFocus
        {
            get
            {
                if (handle == IntPtr.Zero) return false;
                var focused = GetFocus();
                if (focused == IntPtr.Zero) return false;
                return focused == handle || IsChild(handle, focused);
            }
        }

        public int Left { get => left; set { left = value; SetBounds(); } }
        public int Top { get => top; set { top = value; SetBounds(); } }

        public int Width
        {
            get => width;
            set
            {
                width = value;
                if (handle != IntPtr.Zero && !inSizeMove)
                    SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER);
                OnWidthWritten(width);
            }
        }

        public int Height
        {
            get => height;
            set
            {
                height = value;
                if (handle != IntPtr.Zero && !inSizeMove)
                    SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER);
                OnHeightWritten(height);
            }
        }

        public void Hide() => Visible = false;
        public bool Visible { get => _visible; set { _visible = value; if (handle != IntPtr.Zero) ShowWindow(handle, value ? 5 : 0); } }

        /// <summary>Called after `Width` was written, so a subclass can act on a SIZE STATEMENT. A plain control does
        /// nothing here; a FORM overrides it, because its size is a design measurement until it is shown (see
        /// <c>Form.OnWidthWritten</c>).</summary>
        protected virtual void OnWidthWritten(int writtenWidth) { }

        /// <summary>Called after `Height` was written. See <see cref="OnWidthWritten"/>.</summary>
        protected virtual void OnHeightWritten(int writtenHeight) { }

        /// <summary>Where a point in THIS control's client area lands on the screen, as [x, y]. A script working
        /// in screen coordinates (stacking a window on top of this one, reading its pixels) otherwise hand-rolls
        /// the frame arithmetic: window rect minus client rect, halved for the side borders and GUESSED for the
        /// caption, which is wrong whenever the caption is taller than the borders. This asks Windows.</summary>
        public long[] ClientToScreen(int x, int y)
        {
            if (handle == IntPtr.Zero) CreateControl();
            var pt = new Point { x = x, y = y };
            NativeClientToScreen(handle, ref pt);
            return new[] { (long)pt.x, (long)pt.y };
        }

        /// <summary>The colour at a point in THIS control's client area. CLIENT coordinates, unlike the WinApi
        /// member, which takes a window handle and therefore window coordinates: a screen point passed there
        /// answers CLR_INVALID for every point outside the window. This is how a test checks that something was
        /// really painted.</summary>
        public long GetPixel(int x, int y)
        {
            if (handle == IntPtr.Zero) CreateControl();
            var dc = GetDC(handle);
            if (dc == IntPtr.Zero) return -1;
            var c = Interpreter.GetPixel(dc, x, y);   // qualified: this method shadows the P/Invoke by name
            ReleaseDC(handle, dc);
            return (long)c;
        }
        public bool Enabled { get => _enabled; set { _enabled = value; if (handle != IntPtr.Zero) EnableWindow(handle, value ? 1 : 0); } }

        private void SetBounds()
        {
            if (handle != IntPtr.Zero)
                SetWindowPos(handle, IntPtr.Zero, left, top, width, height, SWP_NOZORDER);
        }

        public virtual void ScaleByDpi(float scaleX, float scaleY)
        {
            if (_designBoundsStored)
            {
                Left = (int)Math.Round(_designLeft * scaleX);
                Top = (int)Math.Round(_designTop * scaleY);
                Width = Math.Max(1, (int)Math.Round(_designWidth * scaleX));
                Height = Math.Max(1, (int)Math.Round(_designHeight * scaleY));
            }

            // THE FONT COMES WITH THE SCALING, and THEN the glyph floor: a check box's glyph is sized by the font it
            // draws with, so the font is re-applied first and the floor measures the NEW DPI's glyph instead of the
            // old one (measured: measuring first left the box 37 px tall on a 96 DPI monitor, where the new font needs
            // 26). The floor lives HERE and not in ApplyFont, deliberately: a DPI change is what can make a control
            // smaller than its own glyph, while an author who sets a 16 px check box means it (measured: reaching for
            // the floor on every font application broke the designer's byte-identical document round trip).
            if (handle != IntPtr.Zero)
            {
                ApplyFont();
                ApplyCheckGlyphFloor();
            }
        }

        /// <summary>This window's client origin in SCREEN pixels, which is the space a cursor position arrives in.</summary>
        private Point ClientToScreenOnScreen()
        {
            var p = new Point();
            NativeClientToScreen(handle, ref p);
            return p;
        }
    }
}
