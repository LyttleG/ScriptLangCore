namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public abstract partial class Control
    {
        /// <summary>The window's LONG_PTR slot at an index, which is where Win32 keeps the window's own state: GWL_STYLE
        /// is -16, GWL_EXSTYLE -20, GWL_ID -12, GWL_USERDATA -21. The typed way to ask about a STYLE is ReadStyle or
        /// HasStyle; this is the raw slot, for an index those do not name.
        ///
        /// It is a receiver form the engine CAN offer, and the reason is worth stating: this native has no callers inside
        /// the engine, while SetWindowPos and Send DO, so a member of that name captures them and the build fails. That is
        /// the rule a receiver form follows, and these two are its recorded exceptions.</summary>
        public long GetWindowLongPtr(int index) => GetWindowLongPtrW(handle, index).ToInt64();

        /// <summary>The WRITE half of <see cref="GetWindowLongPtr"/>, which is what a script needs to hand-assign
        /// an id or flip a style WORD by its index (GWL_STYLE, GWLP_ID). Returns what the field held, as the Win32
        /// call does, and 0 when there is no window yet.</summary>
        public long SetWindowLongPtr(int index, long value)
            => handle != IntPtr.Zero ? SetWindowLongPtrW(handle, index, (nint)value).ToInt64() : 0L;

        /// <summary>SAY SOMETHING, on THIS window. WinApi.MessageBox wants the owner&#39;s handle, and a control already
        /// holds one, so the receiver IS the hwnd: <code>form.MessageBox("text", "caption")</code> is the same call with
        /// the plumbing done here. The typing matches the namespace spelling
        /// (<code>WinApi.MessageBox(hwnd, text, caption, type)</code>), including the TYPE flags, which are the SAME values the
        /// namespace takes (<code>1024</code> is MB_SYSTEMMODAL, for instance), and the return is the button the user chose.</summary>
        public int MessageBox(string text, string caption = "", uint type = 0) => Interpreter.MessageBoxW(handle, text ?? "", caption ?? "", type);

        /// <summary>WEAR THIS MENU: the bar the window shows. The handle stays the engine&#39;s, which is the point of the
        /// Menu door, and null takes the menu away.</summary>
        public void SetMenu(object menu)
        {
            // The parameter is OBJECT on purpose: the engine's overload picker matches CLR types, and a parameter
            // typed as a script type (Menu) was refused as "Method 'SetMenu' not found", measured on JIT and AOT
            // alike. The cast is where the typing lives now, and anything else clears the bar.
            var m = menu as Menu;
            Interpreter.SetMenu(handle, m == null ? IntPtr.Zero : m.Handle);   // qualified: this method shadows the import by name
        }

        /// <summary>Send THIS window's messages to a script function: <code>fun(hwnd, msg, wParam, lParam)</code>
        /// answers the result, and a message the handler cannot take falls back to <code>DefWindowProc</code>. The
        /// handler is kept alive for the window's lifetime and freed by Windows' own WM_NCDESTROY.</summary>
        public long SetWindowHandler(ScriptFunction fn)
        {
            if (fn == null) { throw new ScriptException("SetWindowHandler expects a function."); }
            WndProcDelegate del = (h, msg, w, l) =>
            {
                // Self-cleanup on window destruction: the GCHandle goes whether the window was a Form or a raw one.
                if (msg == 0x0082 /*WM_NCDESTROY*/)
                {
                    var cleanupPtr = RemovePropW(h, SCRIPT_WND_PROC);
                    if (cleanupPtr != nint.Zero)
                    {
                        GCHandle.FromIntPtr(cleanupPtr).Free();
                        RemovePropW(h, OLD_WND_PROC);
                    }
                    return 0;
                }
                var interpreter = Current;
                if (interpreter == null) { return DefWindowProcW(h, msg, w, l); }
                try
                {
                    var result = interpreter.InvokeScriptFunction(fn, [h, msg, w, l], 0, 0);
                    return result is nint r ? r : (nint)Convert.ToInt64(result);
                }
                catch
                {
                    return DefWindowProcW(h, msg, w, l);
                }
            };
            var handle = GCHandle.Alloc(del);
            SetPropW(Handle, SCRIPT_WND_PROC, GCHandle.ToIntPtr(handle));
            var oldWndProc = SetWindowLongPtrW(Handle, -4 /*GWLP_WNDPROC*/, Marshal.GetFunctionPointerForDelegate(del));
            SetPropW(Handle, OLD_WND_PROC, oldWndProc);
            return (long)Marshal.GetFunctionPointerForDelegate(del);
        }

        /// <summary>Windows' own handling of a message, which a handler calls for the ones it does not take.</summary>
        public long DefWindowProc(long msg, long wParam, long lParam) => DefWindowProcW(Handle, (uint)msg, (nint)wParam, (nint)lParam).ToInt64();

        /// <summary>The window procedure this window's handler DISPLACED, so a chain can be kept going.</summary>
        public long CallWindowProc(long previous, long msg, long wParam, long lParam) => CallWindowProcW((nint)previous, Handle, (uint)msg, (nint)wParam, (nint)lParam).ToInt64();

        // ---- the raw window message, the same rule as the styles above: the CONTROL is the door ----------------
        /// <summary>Send a window message to THIS control, so a script does not have to fetch the handle and hand it
        /// back. WinApi.SendMessage stays for a window nothing wraps, and both spellings go through one core.</summary>
        public long SendMessage(object msg, object wParam, object lParam)
            => SendMessageScript((long)handle, Convert.ToInt64(msg), Convert.ToInt64(wParam), ToNInt(lParam).ToInt64());

        /// <summary>Post a window message to THIS control and answer whether it was queued, which is what the Win32
        /// call's BOOL means and what an `if` wants to read.</summary>
        public bool PostMessage(object msg, object wParam, object lParam)
            => PostMessageScript((long)handle, Convert.ToInt64(msg), Convert.ToInt64(wParam), ToNInt(lParam).ToInt64());
    /// <summary>Which theme SUB-APP this window should carry. ONE statement of it, because two places apply it (the
    /// theme walk and the frame/theme refresh a DPI change performs) and the second used to overwrite the first: a check
    /// box that had just been given NO sub-app was handed `DarkMode_Explorer` again by the refresh, and its pressed face
    /// came back as the theme's grey band (measured 0x484848 over the plate and the caption on a 0x202020 panel, which is
    /// the "grey background over the check box and its text" a click shows). A CHECK BOX or RADIO BUTTON under a dark or
    /// Custom host therefore takes "" (no theme: the class draws its classic face and takes the plate from
    /// WM_CTLCOLORBTN, i.e. from the colour the host already gives it, in every state), and everything else takes the
    /// leg's own name, and the light leg passes "Explorer" rather than NULL: measured, NULL here LEFT the dark sub-app in
    /// place, so every child of a form switched back to light stayed dark while the form itself went light.</summary>
    private string? SubAppForWindow(bool dark)
        => this is CheckBox or RadioButton && (dark || HostFormOf(this) is { Theme: ThemeMode.Custom })
            ? ""
            : (dark ? "DarkMode_Explorer" : "Explorer");
    protected virtual IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_DPICHANGED_AFTERPARENT:
                    // The parent has already scaled this control's SIZE (its WM_DPICHANGED handler walks them),
                    // and at that moment this window's DPI could still have been the OLD one, which is how a
                    // scrollbar came out 1.5x too thick on the 100% screen of a 150% + 100% pair. THIS message is
                    // the reliable moment (it says the DPI really changed), so the control fixes ITSELF here: its
                    // font, its frame, and its theme. SetWindowTheme again is what makes a themed control re-open
                    // the theme it draws with at the current DPI; WM_THEMECHANGED alone only tells it to reload.
                    ApplyFont();
                    RefreshFrameAndTheme(hwnd);
                    // A window with a BAR keeps no sub-app (see HasNonClientBar): handing it one here is what
                    // flipped its scrollbar from the flat bar to the classic one a second after it appeared.
                    if (!HasNonClientBar(hwnd))
                        SetWindowTheme(hwnd, SubAppForWindow(UnderDarkForm()), "");
                    if (NonClientAreaIsStale())
                    {
                        // The metric PROVABLY did not follow the DPI, and no message Windows knows will lay a
                        // window out again: only a fresh window, created in the current DPI context, will.
                        // RebuildHandleForDpi destroys hwnd, so it must not reach DefWindowProc afterwards.
                        RebuildHandleForDpi();
                        return IntPtr.Zero;
                    }
                    break;   // ...and Windows still gets its say (the engine's WM_DPICHANGED case falls through too)

                case WM_NCDESTROY:
                    var handlePtr = GetPropW(hwnd, SCRIPT_WND_PROC);
                    if (handlePtr != IntPtr.Zero)
                    {
                        GCHandle.FromIntPtr(handlePtr).Free();
                        RemovePropW(hwnd, SCRIPT_WND_PROC);
                        RemovePropW(hwnd, OLD_WND_PROC);
                    }
                    // A WATCHER DOES NOT OUTLIVE ITS WINDOW: the listener is registered per window, so it goes with
                    // the handle, and a handler kept in the table for a dead handle would be memory nobody can reach.
                    DropClipboardWatcher(hwnd);
                    // ... existing WM_NCDESTROY logic ...
                    break;

                case WM_CLIPBOARDUPDATE:
                    // Windows telling the window that the clipboard changed (see Clipboard.Watch): one script call
                    // per notification, with the watched control as its argument, so a handler can read the text.
                    // The interpreter is the one that CREATED this control (the same capture the event raisers use),
                    // because a notification arrives on the message pump, outside any script statement.
                    if (ClipboardWatchHandler(hwnd) is { } watcher)
                        RunClipboardWatcher(watcher);
                    break;

                case WM_PAINT:
                    NotePaint();
                    var hdc = BeginPaint(hwnd, out var ps);
                    try
                    {
                        // Convert Win32 RECT (in ps.rcPaint) to our Rect struct
                        Rect clipRect = new()
                        {
                            Left = ps.rcPaint.Left,
                            Top = ps.rcPaint.Top,
                            Right = ps.rcPaint.Right,
                            Bottom = ps.rcPaint.Bottom
                        };
                        // The designer grid goes down first, so a script's Paint handler draws over it.
                        if (this is Form gridForm && gridForm.GridSize > 1)
                            gridForm.FillGrid(hdc, clipRect);
                        OnPaint(new PaintEventArgs(Handle, hdc, clipRect));
                    }
                    finally
                    {
                        EndPaint(hwnd, ref ps);
                    }
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                    OnMouseDown(new MouseEventArgs(MouseX(lParam), MouseY(lParam), MouseButtons.Left, 1, 0));
                    break;
                case WM_LBUTTONUP:
                    OnMouseUp(new MouseEventArgs(MouseX(lParam), MouseY(lParam), MouseButtons.Left, 1, 0));
                    // A left release on the WINDOW ITSELF is a click on it, the same shape a button reports
                    // through BN_CLICKED: measured, a form raised MouseDown and MouseUp and NEVER Click, so a
                    // generated skeleton's `form.Click` was dead code (the vocabulary emitted it, nothing fired
                    // it).
                    //
                    // A Form always, and now every control whose class does NOT report a click to its parent,
                    // because those were dead too: measured with a real press and release, MouseDown reached a
                    // Button, a Label, a CheckBox, a TextBox and a StatusBar, while Click reached only the Button
                    // and the CheckBox. Raising it here for the rest cannot double-fire, because the two that DO
                    // report are exactly the pair the WM_COMMAND arm dispatches on (ButtonBase and GenericControl),
                    // and a control outside that pair has its notification IGNORED there, so nothing else raises.
                    if (this is Form || this is not (ButtonBase or GenericControl)) OnClick(EventArgs.Empty);
                    break;
                case WM_LBUTTONDBLCLK:
                    OnDoubleClick(EventArgs.Empty);
                    break;
                case WM_MOUSEMOVE:
                    OnMouseMove(new MouseEventArgs(MouseX(lParam), MouseY(lParam), MouseButtons.None, 0, 0));
                    break;
                case WM_RBUTTONDOWN:
                    OnMouseDown(new MouseEventArgs(MouseX(lParam), MouseY(lParam), MouseButtons.Right, 1, 0));
                    break;
                case WM_RBUTTONUP:
                    OnMouseUp(new MouseEventArgs(MouseX(lParam), MouseY(lParam), MouseButtons.Right, 1, 0));
                    break;
                case WM_MBUTTONDOWN:
                    OnMouseDown(new MouseEventArgs(MouseX(lParam), MouseY(lParam), MouseButtons.Middle, 1, 0));
                    break;
                case WM_MBUTTONUP:
                    OnMouseUp(new MouseEventArgs(MouseX(lParam), MouseY(lParam), MouseButtons.Middle, 1, 0));
                    break;
                case WM_KEYDOWN:
                    OnKeyDown(MakeKeyArgs(wParam, lParam));
                    break;
                case WM_KEYUP:
                    OnKeyUp(MakeKeyArgs(wParam, lParam));
                    break;
                case WM_SYSKEYDOWN:
                    OnKeyDown(MakeKeyArgs(wParam, lParam));
                    break;
                case WM_SYSKEYUP:
                    OnKeyUp(MakeKeyArgs(wParam, lParam));
                    break;
                case WM_SIZE:
                    OnResize(EventArgs.Empty);
                    break;

                case WM_DRAWITEM:
                    return DrawOwnerdrawButton(lParam);

                case WM_CTLCOLORMSGBOX:
                case WM_CTLCOLOREDIT:
                case WM_CTLCOLORLISTBOX:
                case WM_CTLCOLORBTN:
                case WM_CTLCOLORDLG:
                case WM_CTLCOLORSCROLLBAR:
                case WM_CTLCOLORSTATIC:
                    {
                        var hdcx = wParam;
                        var hwndCtrl = lParam;
                        var ctrl = FromHandle(hwndCtrl);
                        // A control's OWN colours win; a Custom host's palette is the fallback, so one palette
                        // colours every control the script has not styled by hand.
                        var pal = ctrl != null ? PaletteOfFor(ctrl) : null;
                        var textC = ctrl == null ? null : (ctrl._textColor ?? (ctrl.Enabled ? pal?.Text : (pal?.Disabled ?? pal?.Text)));
                        var bgC = ctrl == null ? null : (ctrl._backgroundColor ?? pal?.Background);
                        if (ctrl != null && (textC.HasValue || bgC.HasValue))
                        {
                            if (textC.HasValue)
                                Interpreter.SetTextColor(hdcx, ToColorRef(textC.Value));

                            if (bgC.HasValue)
                            {
                                SetBkColor(hdcx, ToColorRef(bgC.Value));
                                SetBkMode(hdcx, 1); // TRANSPARENT: brush provides the bg, no raw band behind text
                                // Create or reuse a solid brush. A palette or a script colour both land here, and the
                                // brush is rebuilt when the wanted colour is not the one the cache holds, or a
                                // re-themed form would keep painting the previous colour.
                                if (ctrl.TextColorBrush == IntPtr.Zero || ctrl.TextColorBrushColor != bgC.Value)
                                {
                                    if (ctrl.TextColorBrush != IntPtr.Zero) DeleteObject(ctrl.TextColorBrush);
                                    ctrl.TextColorBrush = CreateSolidBrush(ToColorRef(bgC.Value));
                                    ctrl.TextColorBrushColor = bgC.Value;
                                }
                                return ctrl.TextColorBrush;
                            }
                            else
                            {
                                SetBkMode(hdcx, 1); // TRANSPARENT
                                return GetStockObject(5); // NULL_BRUSH
                            }
                        }
                        break;
                    }
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowSubclass(nint hWnd, SubclassProcDelegate pfnSubclass, nuint uIdSubclass, nuint dwRefData);

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RemoveWindowSubclass(nint hWnd, SubclassProcDelegate pfnSubclass, nuint uIdSubclass);

        [LibraryImport("comctl32.dll")]
        private static partial nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

        /// <summary>The six-parameter callback comctl32 calls. It forwards to the engine's own handling; the two extra
        /// arguments are comctl32's bookkeeping and the engine does not need them.</summary>
        private static nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
            => ChildSubclassProc(hwnd, msg, wParam, lParam);

        protected void SubclassNativeChild()
        {
            if (handle == IntPtr.Zero) return;
            if (_subclassed) return;   // already subclassed
            _childProcDelegate ??= SubclassProc;
            // comctl32's OWN api. Replacing GWLP_WNDPROC makes a v6 common control lose its character mode:
            // measured, comctl32!TV_WndProc fell into comctl32!ProduceWFromA on a TVM_EXPAND (a message carrying no
            // string) with a garbage code page and a null pointer, and killed the process. DefSubclassProc chains
            // the way the control expects, so its ANSI/Unicode state survives.
            if (SetWindowSubclass(handle, _childProcDelegate, SubclassId, 0))
                _subclassed = true;
        }

        private static nint ChildSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            // A managed callback reached FROM native code must not let a CLR exception escape: it surfaces
            // to the OS as an access violation rather than as an error, which is what a 0xC0000005 with the
            // fault inside a runtime helper looks like when nothing managed is left to see it. Logged, and
            // the message handed to the native proc, so a script fault is a message and not a crash.
            try
            {
                if (msg == WM_NCDESTROY)
                {
                    Control? dying; lock (MapLock) HandleMap.TryGetValue(hwnd, out dying);
                    if (dying is not null && dying._subclassed && _childProcDelegate is not null)
                    {
                        RemoveWindowSubclass(hwnd, _childProcDelegate, SubclassId);
                        dying._subclassed = false;
                    }
                    return DefSubclassProc(hwnd, msg, wParam, lParam);
                }

                // A CHILD CAN BE A DROP TARGET, and the ENGINE answers it here: Windows sends WM_DROPFILES to the window
                // under the cursor and NOT to its parent, so a control that covers its host (a RichEdit filling a form is
                // the shape this exists for) is the only window that ever sees the drop. Answering it in this proc is
                // what keeps the control's painting, theme and colours intact, which replacing the proc with a script
                // handler destroys (measured: a white box with no visible text).
                if (msg == WM_DROPFILES)
                {
                    Control? dropped;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out dropped);
                    if (dropped != null && dropped.DropFiles != null)
                    {
                        dropped.OnDropFiles(wParam);      // reads the list and releases it (DragFinish)
                        return IntPtr.Zero;
                    }
                    return DefSubclassProc(hwnd, msg, wParam, lParam);
                }

                // Color engine-managed EDIT/LISTBOX/... children regardless of the parent
                // window type. WM_CTLCOLOR* is sent to the PARENT; when that parent is a
                // system control (e.g. the EDIT overlay inside SysListView32) its native
                // proc forwards nothing to our WndProc, so handle it here in the subclass
                // proc of the child whose colors are set.
                // A control whose CLASS paints its whole surface, filled by the engine instead.
                if (msg == WM_ERASEBKGND)
                {
                    Control? darkBody;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out darkBody);
                    if (darkBody != null && darkBody.DarkBody)
                    {
                        // A Custom host colours this body from its palette. Without this the class-derived fill ignored
                        // the palette, so a custom background stopped at the form's own client (measured: a label read
                        // 0x202020 under a 0x3050A0 form while the form itself was right).
                        if (Control.HostFormOf(darkBody) is { Theme: ThemeMode.Custom } host)
                            return Control.FillClientColor(hwnd, wParam, ref _darkClientBrush, ref _darkClientColor,
                                                           host.CustomBackground() & 0xFFFFFF);
                        return Control.FillClientDark(hwnd, wParam, ref _darkClientBrush);
                    }
                }


                if (msg is >= WM_CTLCOLORMSGBOX and <= WM_CTLCOLORSTATIC)
                {
                    Control? col;
                    lock (MapLock) HandleMap.TryGetValue(lParam, out col);
                    if (col != null && (col._textColor.HasValue || col._backgroundColor.HasValue))
                    {
                        var hdcCol = wParam;
                        if (col._textColor.HasValue)
                            Interpreter.SetTextColor(hdcCol, ToColorRef(col._textColor.Value));
                        if (col._backgroundColor.HasValue)
                        {
                            SetBkColor(hdcCol, ToColorRef(col._backgroundColor.Value));
                            SetBkMode(hdcCol, 1);   // TRANSPARENT: brush paints the background
                            if (col.TextColorBrush == IntPtr.Zero)
                                col.TextColorBrush = CreateSolidBrush(ToColorRef(col._backgroundColor.Value));
                            return col.TextColorBrush;
                        }
                        SetBkMode(hdcCol, 1);
                        return GetStockObject(5);   // NULL_BRUSH
                    }
                }

                // Raise managed mouse events, then forward to the native proc so the
                // control keeps its normal behavior (button press, edit caret, ...).
                if (msg == WM_NCHITTEST)
                {
                    // A managed control with script mouse handlers MUST be a hit target: the window class can say
                    // otherwise and several do. STATIC (Label) windows are hit-transparent by design, and a TabControl
                    // answers HTTRANSPARENT outright: measured, the engine's own hit test returned -1 for a placed
                    // TabControl and a click on it reached the FORM, so it could neither be selected nor dragged. This
                    // rule was written for labels alone; every managed control with a handler now answers HTCLIENT, and
                    // a control WITHOUT handlers still gets whatever its class says.
                    Control? hit;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out hit);
                    if (hit != null
                        && (hit.MouseDown != null || hit.MouseMove != null || hit.MouseUp != null || hit.DoubleClick != null))
                        return (IntPtr)HTCLIENT;
                }
                if (msg == WM_PAINT)
                {
                    Control? pb;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out pb);
                    pb?.NotePaint();
                    if (pb is TrackBar tb && (tb._backgroundColor.HasValue || tb._textColor.HasValue))
                    {
                        PaintTrackBar(tb);
                        ValidateRect(hwnd, IntPtr.Zero);
                        return IntPtr.Zero;   // fully custom-painted: do not forward to native
                    }
                    // A status bar in a DARK host paints itself for the same reason: the native control draws its
                    // text with the theme's own colour, black on our dark bar, and never asks the host for one. A
                    // light host is left to the native painting, so the stock look is untouched.
                    if (pb is StatusBar sb && sb.DarkHost)
                    {
                        sb.PaintDark();
                        return IntPtr.Zero;   // BeginPaint/EndPaint validated the update region
                    }
                    // A THIN ENGINE BAR IS THE LAST THING PAINTED (see Control.ScrollBar): the strip sits inside the
                    // client, so the class draws its text first and the engine puts the band and the thumb over the
                    // edge the text has been kept away from (EM_SETRECT).
                    if (pb != null && pb.ScrollBar)
                    {
                        var nativeThin = DefSubclassProc(hwnd, msg, wParam, lParam);
                        pb.PaintThinBar();
                        return nativeThin;
                    }

                }
                // THE POINTER'S SHAPE OVER THE STRIP (see Control.ThinBarSetCursor): a RichEdit sets an I-beam over its
                // whole client, and the strip lives inside it, so a bar that looks like a scrollbar still showed a text
                // caret. Answered TRUE here, which tells Windows the message is handled and the class never sets its own.
                if (msg == WM_SETCURSOR)
                {
                    Control? cursorCtrl;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out cursorCtrl);
                    if (cursorCtrl != null && cursorCtrl.ThinBarSetCursor()) return 1;
                }

                // THE THIN BAR TAKES ITS OWN MESSAGES, before the class and before a script handler: a press in the
                // strip pages or starts a drag, a move while dragging scrolls, and the wheel is the engine's because the
                // native bar (which would otherwise answer it) is gone. Everything is a CLIENT message here, which is
                // the reason the strip needs no non-client handling at all.
                if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_MOUSEMOVE or WM_MOUSEWHEEL)
                {
                    Control? barCtrl2;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out barCtrl2);
                    if (barCtrl2 != null && barCtrl2.ScrollBar)
                    {
                        var barX = (int)(short)(lParam.ToInt64() & 0xFFFF);
                        var barY = (int)(short)((lParam.ToInt64() >> 16) & 0xFFFF);
                        if (msg == WM_MOUSEMOVE)
                        {
                            barCtrl2.ThinBarHover(barX >= barCtrl2.ThinBarLeftEdge());
                            // A DRAG IS THE BAR'S, so the message is SWALLOWED: forwarded, the class reads a move with
                            // the button down as a text selection and scrolls back to the selection (measured: the drag
                            // ended with the view at line 0).
                            if (barCtrl2.ThinBarIsDragging())
                            {
                                barCtrl2.ThinBarDrag(barY);
                                return IntPtr.Zero;
                            }
                        }
                        else if (msg == WM_LBUTTONDOWN)
                        {
                            if (barCtrl2.ThinBarPress(barX, barY))
                            {
                                SetCapture(hwnd);   // the drag's, so a pointer outside the window still moves the thumb
                                return IntPtr.Zero;
                            }
                        }
                        else if (msg == WM_LBUTTONUP)
                        {
                            if (barCtrl2.ThinBarIsDragging()) ReleaseCapture();
                            barCtrl2.ThinBarRelease();
                        }
                        else
                        {
                            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);   // WM_MOUSEWHEEL's delta is in wParam
                            barCtrl2.ScrollLines((delta > 0 ? -ThinBarWheelLines : ThinBarWheelLines));
                            return IntPtr.Zero;   // the wheel is the engine's: no double scroll
                        }
                    }
                }
                if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP
                    or WM_MBUTTONDOWN or WM_MBUTTONUP or WM_MOUSEMOVE
                    or WM_LBUTTONDBLCLK)
                {
                    Control? ctrl;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out ctrl);
                    // A ListBox used to be skipped here on the grounds that LBN_DBLCLK reflection covers it, but that
                    // reflection reaches the form that owns the list, not a list on a SECONDARY form: measured, a
                    // style window's list never raised DoubleClick at all. The dispatcher is forwarded the message
                    // like every other control now, and a handler that could be raised twice re-arms on the next press.
                    // NOT FORWARDED for a control whose class owns its mouse messages: see ClassHandlesMouse.
                    if (ctrl != null && !ctrl.ClassHandlesMouse)
                        ctrl.WndProc(hwnd, msg, wParam, lParam);
                }

                // A DPI change reaches the CHILD, not the form, and this forwarded set is mouse and keyboard only:
                // without this line Control.WndProc's own handling of it never runs for a child at all, which is how
                // a scrollbar stayed 1.5x too thick on the 100% screen of a 150% + 100% pair (measured: the child's
                // WM_DPICHANGED_AFTERPARENT handler was never entered, so its font, frame and theme were never
                // re-applied). The engine's WndProc answers with the native result, so nothing is skipped.
                if (msg == WM_DPICHANGED_AFTERPARENT)
                {
                    Control? dpiCtrl;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out dpiCtrl);
                    if (dpiCtrl != null) return dpiCtrl.WndProc(hwnd, msg, wParam, lParam);
                }
                // Keys land on the FOCUSED window. Native children take focus when clicked,
                // so dispatch keyboard messages to the child's engine control as well, or a
                // KeyDown attached to the form would never see them while a child is focused.
                if (msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
                {
                    Control? ck;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out ck);
                    if (ck != null)
                    {
                        if (msg is WM_KEYDOWN or WM_SYSKEYDOWN) ck.OnKeyDown(MakeKeyArgs(wParam, lParam));
                        else ck.OnKeyUp(MakeKeyArgs(wParam, lParam));
                    }
                }

                // Forward FIRST so native controls process the press/release in their own
                // capture context (a BUTTON must see WM_LBUTTONUP while it still has
                // capture to emit BN_CLICKED), then manage our extra capture afterwards.
                // comctl32's own continuation: it reaches whatever was there before this subclass, keeps the control's
                // character mode, and cannot go stale the way a cached proc pointer can. The depth guard that used to
                // sit here is gone with it, because DefSubclassProc cannot re-enter the engine's own proc.
                var res = DefSubclassProc(hwnd, msg, wParam, lParam);

                // A control that manages its own mouse capture must be left alone: taking capture (before or after
                // forwarding WM_LBUTTONDOWN) injects a WM_CAPTURECHANGED, which cancels whatever the control had
                // pending. A BUTTON loses its pending click (a single click never emits BN_CLICKED), and a control
                // with a DROPDOWN loses the dropdown itself: it opens by holding the capture, so the click that opens
                // it used to close it again immediately (measured with CB_GETDROPPEDSTATE on a ComboBox: 0 right
                // after the press, 1 once this exemption covered it). Keep the extra capture for the children that
                // need it (the EDIT family, an adopted COM child). A LISTBOX does NOT need the engine's capture, and
                Control? captCtrl;
                lock (MapLock) HandleMap.TryGetValue(hwnd, out captCtrl);
                // A LISTBOX belongs in this list and was missing from it, which the comment above named all along: taking
                // capture on the press sends it WM_CAPTURECHANGED, which CANCELS the pending selection, so the list box
                // selected and immediately cancelled. Measured on the dpi-probe's styles list, clicking a row: the form
                // received LBN_SETFOCUS (4) and LBN_SELCANCEL (3) and never LBN_SELCHANGE (1), which is the only code the
                // reflection below matches, so neither SelectedIndexChanged nor Click ever fired for any row.
                // Every control that manages its OWN capture belongs here, and the ListBox taught the lesson: missing from
                // this list, a control has capture taken from it on the press, gets WM_CAPTURECHANGED, and CANCELS whatever
                // it had pending (a selection, a drag). A ListBox lost its selection that way on every click. These are the
                // ones Windows gives their own capture semantics to: a TrackBar's drag, a ListView's and a TreeView's
                // selection and drag, a ToolBar's and Rebar's item drags and a TabControl's item drag. A control that does
                // not take capture is unaffected either way, so the list is deliberately generous rather than minimal.
                var ownCapture = captCtrl is ButtonBase or ComboBox or DateTimePicker or ListBox
                    or TrackBar or ListView or TreeView or ToolBar or Rebar or TabControl
                    || (captCtrl?.ClassHandlesMouse ?? false);   // the list is the engine's measurement, the property is a script's
                if (msg == WM_LBUTTONDOWN && !ownCapture) { try { SetCapture(hwnd); } catch { } }
                else if (msg == WM_LBUTTONUP && !ownCapture) { try { if (GetCapture() == hwnd) ReleaseCapture(); } catch { } }
                // Text edits (typing / paste / clear) on a managed EDIT must be visible
                // immediately: if the native invalidation is suppressed by our subclass
                // chain, force a repaint of the edit control after the key is applied.
                if (msg is 0x0102 or 0x000C or 0x0300 or 0x0302 or 0x0303 or 0x0100)   // WM_CHAR/WM_SETTEXT/WM_CUT/WM_PASTE/WM_CLEAR/WM_KEYDOWN
                {
                    Control? edit;
                    lock (MapLock) HandleMap.TryGetValue(hwnd, out edit);
                    if (edit is TextBox)
                    {
                        InvalidateRect(hwnd, IntPtr.Zero, true);
                        Interpreter.UpdateWindow(hwnd);   // qualified: the member of this name shadows the import
                    }
                }
                return res;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[subclass] {ex.GetType().Name} on msg=0x{msg:X} hwnd={hwnd:X}: {ex.Message}");
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        protected static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // THE WHOLE PROCEDURE IS GUARDED, not only the dispatch, and the crash that proved it named the lines BELOW
            // the try the first version had: `GCHandle.FromIntPtr(userData).Target` throws
            // `InvalidOperation_HandleIsNotInitialized` when the GC handle a window carries has ALREADY been FREED (a run
            // that ended and released its controls, while a message was still in flight), and that dereference sat
            // OUTSIDE the guard. user32 turns anything that escapes a window procedure into PROCESS DEATH, so this is the
            // repo's "the shipped binary must never be killed by script input" applied where the process was dying:
            // measured twice from a runner clicking Repeat, `#1`/`#2`/`#3` ok and then
            // `Unhandled exception. System.InvalidOperationException: InvalidOperation_HandleIsNotInitialized`.
            try
            {
                if (msg == WM_NCCREATE)
                {
                    var createStruct = Marshal.PtrToStructure<CREATESTRUCT>(lParam);
                    var createHandle = GCHandle.FromIntPtr(createStruct.lpCreateParams);
                    var created = (Control)createHandle.Target!;
                    SetWindowLongPtrW(hwnd, GWLP_USERDATA, GCHandle.ToIntPtr(createHandle));
                    // Register immediately so FromHandle works
                    lock (MapLock) HandleMap[hwnd] = created;
                    created.handle = hwnd;
                }

                // Dispatch WM_HOTKEY to GlobalHotKey (no userData required)
                if (msg == 0x0312) // WM_HOTKEY
                {
                    GlobalHotKey.DispatchHotKey((int)wParam);
                    return IntPtr.Zero;
                }

                var userData = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
                if (userData == IntPtr.Zero)
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
                var handle = GCHandle.FromIntPtr(userData);
                // A FREED HANDLE REPORTS NO TARGET and throws, which is the case above, so the window simply stops
                // answering as an engine control and hands the message to the default proc.
                if (!handle.IsAllocated)
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
                if (handle.Target is Control ctrl)
                {
                    var result = ctrl.WndProc(hwnd, msg, wParam, lParam);
                    if (msg == WM_NCDESTROY)
                    {
                        SetWindowLongPtrW(hwnd, GWLP_USERDATA, IntPtr.Zero);
                        lock (MapLock) HandleMap.Remove(hwnd);
                        ctrl.handle = IntPtr.Zero;
                        if (handle.IsAllocated) { handle.Free(); }
                    }
                    return result;
                }
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                // Reported through the engine's ONE message map, naming the message that failed, and the window keeps
                // answering instead of taking the application down.
                try
                {
                    Logging.Log.Error("WndProc", $"message 0x{msg:X4} failed: {FriendlyExceptionMessage(ex)}", 0, 0);
                }
                catch { /* a failing log must not resurrect the crash it is reporting */ }
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }
    }
}
