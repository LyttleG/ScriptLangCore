namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public abstract partial class Control
    {
        /// <summary>The first ROW the control shows, and the way to move the view: assigning it scrolls by the
        /// difference, so the control's own scroll position stays the truth. This is the pair a second window beside
        /// a log (a line-number column) needs to follow it.</summary>
        public int FirstVisibleLine
        {
            get { if (handle == IntPtr.Zero) return 0; return (int)SendMessageW(handle, EM_GETFIRSTVISIBLELINE, 0, 0); }
            set
            {
                if (handle == IntPtr.Zero) return;
                var current = FirstVisibleLine;
                if (value == current) return;
                SendMessageW(handle, EM_LINESCROLL, 0, value - current);
            }
        }

        /// <summary>Bring the caret into view. It is computed from the caret's own LINE and the control's row height,
        /// and NOT from EM_SCROLLCARET: that message needs a caret that holds the FOCUS, so on a read-only log nobody
        /// has clicked it scrolls nothing at all (measured on the log box this member exists for).</summary>
        public void ScrollToCaret()
        {
            if (handle == IntPtr.Zero) return;
            var caret = SelectionRange().End;
            var line = caret > 0 ? (int)SendMessageW(handle, EM_EXLINEFROMCHAR, 0, caret) : 0;
            var first = FirstVisibleLine;
            var rowPx = 0;
            if (LineCount > 1)
            {
                var lower = CharPosition(LineIndex(1));
                var upper = CharPosition(LineIndex(0));
                rowPx = (int)(lower[1] - upper[1]);
            }
            var boxPx = ClientRect.Bottom - ClientRect.Top;
            var rows = rowPx > 0 && boxPx > rowPx ? boxPx / rowPx : 1;
            if (line < first) FirstVisibleLine = line;
            else if (line >= first + rows) FirstVisibleLine = line - rows + 1;
            else SendMessageW(handle, EM_SCROLLCARET, 0, 0);   // already visible: let the control be exact
        }

        /// <summary>A NARROW, engine-drawn scroll bar instead of the native one. Windows paints a native scrollbar in
        /// the theme's own colours and offers no supported way to change them, and overpainting it fights the class's
        /// own hot state (measured: the two paints alternate under the pointer, which is a visible flicker). So the
        /// native bar is REMOVED and the engine draws a strip of its own: 45% of the metric wide, in the host's
        /// background with a blended thumb, and it drives the control itself (a page click, a thumb drag and the wheel
        /// all call <code>EM_LINESCROLL</code>). Supported on a class with the EDIT line model (a multiline EDIT and a
        /// RichEdit); other classes are REFUSED by name rather than given a bar that cannot scroll them.
        /// <para>The strip lives INSIDE the client, so the control's own text is kept off it with <code>EM_SETRECT</code>,
        /// which is what makes the wrap identical to a native bar's.</para></summary>
        public bool ScrollBar
        {
            get => _thinBar;
            set
            {
                if (value == _thinBar) return;
                if (value && !SupportsThinBar())
                    throw new ScriptException($"A thin scroll bar is drawn by the engine for a multiline EDIT or a RichEdit, and this is {DescribeKind()}: it keeps its native bar.");
                // THE CREATION STYLE IS THE HONEST QUESTION, not the live word: a RichEdit clears its own bar bit while the
                // text fits and sets it again when it overflows (measured), so a live-word test would accept a window that
                // was asked for a bar and end up with two of them.
                var creationStyle = this is GenericControl gc ? (uint)gc.Style : (uint)GetWindowLongPtrW(handle, GWL_STYLE);
                if (value && handle != IntPtr.Zero && (creationStyle & (WS_VSCROLL | WS_HSCROLL)) != 0)
                {
                    // A class PUTS ITS BAR BIT BACK (measured on a RichEdit: the bit was cleared and the style read
                    // 0x50201844 again as soon as the text overflowed, and EM_SHOWSCROLLBAR did not help), so a window
                    // that was created WITH a bar can never be given the engine's. The refusal names the fix rather
                    // than leaving two bars side by side.
                    throw new ScriptException("This window was created WITH a native scroll bar, and a window's class puts that bit back once it has one: create it without WS_VSCROLL (and without WS_HSCROLL) and set ScrollBar instead, which is how the engine's own thin bar is used.");
                }
                _thinBar = value;
                if (handle != IntPtr.Zero) ApplyThinBar();
            }
        }

        /// <summary>True for a class the engine's thin bar knows how to drive: the EDIT line model (a RichEdit is one
        /// too), which is <code>EM_GETLINECOUNT</code>, <code>EM_GETFIRSTVISIBLELINE</code> and
        /// <code>EM_LINESCROLL</code>.</summary>
        /// <summary>Whether this control is the model the strip can drive, asked of the WINDOW CLASS rather than of the
        /// wrapper: a TextBox and a RichTextBox are the engine's own shapes of it, and a GenericControl made with an EDIT
        /// or a RichEdit class is the same thing. A RichTextBox was REFUSED here before, which is how the factory's own
        /// control came to keep a native bar the engine draws for every other log.</summary>
        private bool SupportsThinBar()
            => this is TextBox || this is RichTextBox
               || NativeClass.StartsWith("EDIT", StringComparison.OrdinalIgnoreCase)
               || NativeClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);

        /// <summary>The class name a refusal names, so a script is told what it actually has.</summary>
        private string DescribeKind() => NativeClass.Length > 0 ? $"a '{NativeClass}' window" : GetType().Name;

        /// <summary>The strip's width in pixels at this window's DPI, which is what the paint, the hit test and the
        /// text inset all use, so the three cannot disagree.</summary>
        private int ThinBarWidth()
        {
            var dpi = handle != IntPtr.Zero ? GetDpiForWindow(handle) : 96;
            if (dpi == 0) dpi = 96;
            var native = GetSystemMetricsForDpi(SM_CXVSCROLL, dpi);
            var w = (int)Math.Round(native * ThinBarRatio);
            return w < 4 ? 4 : w;
        }

        /// <summary>Take the native bar off and keep the text off the strip. Called when the property is set and when
        /// the window is created, so the two orders (set then create, create then set) end in the same state.</summary>
        private void ApplyThinBar()
        {
            if (handle == IntPtr.Zero || !_thinBar) return;
            ApplyWindowStyle(handle, 0, WS_VSCROLL | WS_HSCROLL, false);   // a no-op when the bar was never asked for
            InsetTextForThinBar();
            InvalidateRect(handle, IntPtr.Zero, true);
        }

        /// <summary>The control's text stops where the strip starts, exactly as a native bar would shorten the client:
        /// <code>EM_SETRECT</code> for the EDIT family. A control that has no such message is left alone (its text may
        /// run under the strip, which the support check above keeps from happening for the classes the bar serves).</summary>
        private void InsetTextForThinBar()
        {
            if (handle == IntPtr.Zero) return;
            GetClientRect(handle, out var client);
            var strip = ThinBarWidth();
            var margin = 2;
            var rc = new Rect { Left = client.Left + margin, Top = client.Top + margin, Right = client.Right - strip - margin, Bottom = client.Bottom - margin };
            if (rc.Right <= rc.Left) return;
            var size = Marshal.SizeOf<Rect>();
            var p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(rc, p, false);
                SendMessageW(handle, EM_SETRECT, 0, p);
                InvalidateRect(handle, IntPtr.Zero, true);   // EM_SETRECT does not repaint by itself
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>The scroll model, read from the CONTROL rather than tracked here: total lines, the first visible
        /// one, and how many fit. Everything the bar draws and every move it makes comes out of these three.</summary>
        private (int Total, int First, int Visible) ThinBarModel()
        {
            if (handle == IntPtr.Zero) return (0, 0, 0);
            var total = (int)SendMessageW(handle, EM_GETLINECOUNT, 0, IntPtr.Zero);
            var first = (int)SendMessageW(handle, EM_GETFIRSTVISIBLELINE, 0, IntPtr.Zero);
            GetClientRect(handle, out var client);
            var height = client.Bottom - client.Top;
            var lineHeight = LineHeightPixels();
            var visible = lineHeight > 0 ? Math.Max(1, height / lineHeight) : 1;
            if (visible > total) visible = Math.Max(1, total);
            return (total, first, visible);
        }

        /// <summary>One line's height, asked of the control's own font and DC, cached per DPI: a bar whose thumb was
        /// computed from an assumed height would be a different length after a monitor move.</summary>
        private int LineHeightPixels()
        {
            int dpi = (int)GetDpiForWindow(handle);
            if (dpi == 0) dpi = 96;
            if (_lineHeightDpi == dpi && _lineHeightCached > 0) return _lineHeightCached;
            var hdc = GetDCEx(handle, IntPtr.Zero, DCX_WINDOW | DCX_CACHE);
            if (hdc == IntPtr.Zero) return 0;
            var previous = _currentFont != IntPtr.Zero ? SelectObject(hdc, _currentFont) : IntPtr.Zero;
            try
            {
                var size = new TextSize();
                if (!GetTextExtentPoint32(hdc, "Xg", 2, ref size) || size.Height <= 0) return 0;
                _lineHeightCached = size.Height;
                _lineHeightDpi = (int)dpi;
                return _lineHeightCached;
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(hdc, previous);
                ReleaseDC(handle, hdc);
            }
        }

        /// <summary>The pointer over the strip is the ARROW, not the class's own cursor: measured, a RichEdit sets an
        /// I-beam over its whole client and the strip lives inside it, so a bar that looks like a scrollbar still
        /// showed a text caret. Answers true when it took the message, so the caller returns TRUE and the class never
        /// sets its own. Called from WM_SETCURSOR, and while a drag is in progress it answers TRUE wherever the pointer
        /// is, because the capture is the bar's then.</summary>
        internal bool ThinBarSetCursor()
        {
            if (!_thinBar || handle == IntPtr.Zero) return false;
            var overThumb = _barDragging;   // a grabbed thumb keeps the grab cursor wherever the pointer is
            if (!_barDragging)
            {
                var pc = new Point();
                if (!GetCursorPos(ref pc)) return false;
                var origin = ClientToScreenOnScreen();
                Interpreter.GetClientRect(handle, out var client);
                var strip = ThinBarWidth();
                var localX = pc.x - origin.x;
                var localY = pc.y - origin.y;
                if (localX < client.Right - strip || localX > client.Right) return false;
                if (localY < client.Top || localY > client.Bottom) return false;
                var thumb = ThinBarThumb(out var hasThumb);
                overThumb = hasThumb && localY >= thumb.Top && localY < thumb.Bottom;
            }
            // THE THUMB IS THE PART YOU CAN GRAB, so it takes the hand; the track keeps the plain arrow a scrollbar
            // shows. Asked for directly after the arrow landed, and it is what makes the bar read as draggable.
            SetCursor(LoadCursorW(IntPtr.Zero, overThumb ? IDC_HAND : IDC_ARROW));
            return true;
        }
        /// <summary>The client x where the strip starts, which is what a hover test asks.</summary>
        internal int ThinBarLeftEdge() => ThinBarRect().Left;

        /// <summary>True while a drag of the thumb is in progress, which keeps the hover shade lit.</summary>
        internal bool ThinBarIsDragging() => _barDragging;
        /// <summary>The strip's rectangle: the client's right edge, the control's full height.</summary>
        private Rect ThinBarRect()
        {
            GetClientRect(handle, out var client);
            var strip = ThinBarWidth();
            return new Rect { Left = client.Right - strip, Top = client.Top, Right = client.Right, Bottom = client.Bottom };
        }

        /// <summary>The thumb's rectangle inside the strip, from the model. Empty when everything fits.</summary>
        private Rect ThinBarThumb(out bool hasThumb)
        {
            var (total, first, visible) = ThinBarModel();
            var bar = ThinBarRect();
            var height = bar.Bottom - bar.Top;
            hasThumb = total > visible && height > 0;
            if (!hasThumb) return bar;
            var len = (int)((long)height * visible / Math.Max(1, total));
            if (len < 12) len = 12;
            var travel = height - len;
            var span = total - visible;
            var pos = span <= 0 ? 0 : (int)((long)travel * Math.Min(first, span) / span);
            return new Rect { Left = bar.Left, Top = bar.Top + pos, Right = bar.Right, Bottom = bar.Top + pos + len };
        }

        /// <summary>Paint the strip: the band in the host's background (so it reads as an edge, not a white stripe) and a
        /// blended thumb, brighter while the pointer is over it or it is being dragged.</summary>
        internal void PaintThinBar()
        {
            if (!_thinBar || handle == IntPtr.Zero) return;
            var bar = ThinBarRect();
            if (bar.Right - bar.Left <= 0) return;
            var theme = ThemeStateOf(this);
            var band = theme.Background();
            var thumb = BlendColor(band, theme.Text(), _barHot || _barDragging ? 45 : 26);
            var hdc = GetDCEx(handle, IntPtr.Zero, DCX_WINDOW | DCX_CACHE);
            if (hdc == IntPtr.Zero) return;
            var bandBrush = CreateSolidBrush(ToColorRef(band));
            var thumbBrush = CreateSolidBrush(ToColorRef(thumb));
            try
            {
                FillRect(hdc, ref bar, bandBrush);
                var thumbRect = ThinBarThumb(out var hasThumb);
                if (hasThumb) FillRect(hdc, ref thumbRect, thumbBrush);
            }
            finally
            {
                DeleteObject(bandBrush);
                DeleteObject(thumbBrush);
                ReleaseDC(handle, hdc);
            }
        }

        /// <summary>What a press at a point in the strip does: on the thumb it starts a drag, above it a page up, below
        /// it a page down. Answers true when the message was the bar's, so the caller swallows it.</summary>
        internal bool ThinBarPress(int x, int y)
        {
            if (!_thinBar) return false;
            var bar = ThinBarRect();
            if (x < bar.Left) return false;
            var thumbRect = ThinBarThumb(out var hasThumb);
            if (hasThumb && y >= thumbRect.Top && y < thumbRect.Bottom)
            {
                _barDragging = true;
                _barDragOffset = y - thumbRect.Top;
                return true;
            }
            var (_, _, visible) = ThinBarModel();
            ScrollLines(y < thumbRect.Top ? -visible : visible);
            return true;
        }

        /// <summary>The drag: the pointer's position maps to a first visible line, and only the DIFFERENCE is sent, so the
        /// control's own scroll position stays the truth.</summary>
        internal void ThinBarDrag(int y)
        {
            if (!_thinBar || !_barDragging) return;
            var bar = ThinBarRect();
            var height = bar.Bottom - bar.Top;
            var (total, first, visible) = ThinBarModel();
            var thumbLen = (int)((long)height * visible / Math.Max(1, total));
            if (thumbLen < 12) thumbLen = 12;
            var travel = height - thumbLen;
            if (travel <= 0) return;
            var span = total - visible;
            if (span <= 0) return;
            var pos = y - _barDragOffset - bar.Top;
            if (pos < 0) pos = 0;
            if (pos > travel) pos = travel;
            var target = (int)((long)span * pos / travel);
            var delta = target - first;
            if (delta != 0) ScrollLines(delta);
        }

        internal void ThinBarRelease()
        {
            if (!_barDragging) return;
            _barDragging = false;
            InvalidateRect(handle, IntPtr.Zero, false);
        }

        /// <summary>Set the hover shade, which is the ONE visual state the engine owns here (there is no native bar to
        /// fight, so no flicker).</summary>
        internal void ThinBarHover(bool hot)
        {
            if (!_thinBar || _barHot == hot) return;
            _barHot = hot;
            if (handle != IntPtr.Zero) InvalidateRect(handle, IntPtr.Zero, false);
        }

        /// <summary>Move the control's own scroll position, in lines: the EDIT message, so the control keeps whatever
        /// else it does (caret visibility, wrapping) and the engine keeps no position of its own.</summary>
        internal void ScrollLines(int lines)
        {
            if (handle == IntPtr.Zero || lines == 0) return;
            SendMessageW(handle, EM_LINESCROLL, 0, (nint)lines);
        }
    }
}
