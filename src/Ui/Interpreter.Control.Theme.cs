namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public abstract partial class Control
    {
        /// <summary>The custom palette this control should paint with: its host form's, or null when that form is
        /// not Custom. A control the script gave its own BackgroundColor/TextColor keeps them, because the palette is
        /// the FALLBACK here in the same way a form's Padding is the fallback for a margin.</summary>
        internal static ThemePalette? PaletteOfFor(Control c)
            => HostFormOf(c) is { Theme: ThemeMode.Custom } f ? f.Palette : null;

        /// <summary>The theme state a control paints from: the owning host's, whether that host is a Form, an MDI
        /// frame or an MDI document window, and a state with no palette when nothing owns the control. ONE
        /// accessor, so a per-kind arm reads the colours its host paints with instead of respelling the leg's own
        /// constants, which is what let a TreeView and a ListView disagree about a Custom form.
        ///
        /// An MDI frame and an MDI document window are not Control descendants, so neither is reachable through
        /// <see cref="HostFormOf"/>'s Parent walk: each publishes its instance as a window property (the same one
        /// its message router reads) and a control of theirs is parented to that WINDOW, so the window chain is
        /// the only walk that reaches them.
        ///
        /// The palette travels ONLY while Custom was asked for, which is the test PaletteOfFor makes: a palette
        /// member left over from a mode the host has since changed must not colour a Dark or Light window.</summary>
        internal static ThemeState ThemeStateOf(Control c)
        {
            var state = HostFormOf(c) is Form form ? form.ThemeStateValue : ThemeStateOfWindowChain(c);
            if (state.Mode != ThemeMode.Custom) state.Palette = default;
            return state;
        }

        private static ThemeState ThemeStateOfWindowChain(Control c)
        {
            for (var h = c.Handle; h != IntPtr.Zero; h = GetParent(h))
            {
                var child = GetPropW(h, MdiChild.WindowProp);
                if (child != IntPtr.Zero && GCHandle.FromIntPtr(child).Target is MdiChild mdiChild)
                    return mdiChild.ThemeStateValue;
                var frame = GetPropW(h, MdiForm.WindowProp);
                if (frame != IntPtr.Zero && GCHandle.FromIntPtr(frame).Target is MdiForm mdiForm)
                    return mdiForm.ThemeStateValue;
            }
            return default;
        }

        // Internal helper to get COLORREF as uint
        internal uint GetTextColorRef() => _textColor ?? 0;

        internal uint GetBackgroundColorRef() => _backgroundColor ?? 0;

        public long? TextColor
        {
            get => _textColor;
            set => SetTextColorCore(value.HasValue ? (uint?)value.Value : null, all: true);
        }

        /// <summary>One core for the two ways a script states a text colour: the PROPERTY means "this control's text
        /// colour", so a RichEdit takes it on the WHOLE text, while <see cref="SetTextColor"/> means "the text I have
        /// selected right now", so it takes the SELECTION only. Keeping the two apart here is what stops a log's
        /// per-run colours from being overwritten: measured, with both going through the whole text, every run of a
        /// reloaded entry came out the colour of the LAST one.</summary>
        private void SetTextColorCore(uint? value, bool all)
        {
            _textColor = value;
            ThemeTinted = false;   // a write through this door is the WRITER's colour, never the theme's
            if (handle != IntPtr.Zero)
                InvalidateRect(handle, IntPtr.Zero, true);
            if (this is ButtonBase) EnsureButtonOwnerdraw();
            // A RichEdit draws its own text, so the colour has to reach its CHAR FORMAT or the assignment would only
            // be remembered. ToColorRef is where the 0xRRGGBB a script writes becomes the COLORREF the message wants.
            if (handle != IntPtr.Zero && value.HasValue) ApplyRichEditColour(ToColorRef(value.Value & 0xFFFFFF), all);
        }

        /// <summary>The colour of the text this control holds, applied NOW, as one script-visible member instead of a
        /// hand-built CHARFORMATW. On a RichEdit it colours the SELECTION, which is how a log paints the run it just
        /// inserted (with nothing selected it colours the whole text); on any other control it sets
        /// <see cref="TextColor"/>, which is what the theme path and the plain controls read. The argument is
        /// 0xRRGGBB like every other colour a script writes, and the COLORREF the message needs is derived HERE, so
        /// the byte order never reaches a script: handing the raw value to EM_SETCHARFORMAT paints a red as violet
        /// (measured, and it is what made a clipboard log's error line look violet).</summary>
        public void SetTextColor(long rgb) => SetTextColorCore((uint)(rgb & 0xFFFFFF), all: false);

        /// <summary>One EM_SETCHARFORMAT carrying CFM_COLOR: the SELECTION (a log's run) or the whole text. The
        /// argument is already a COLORREF, because the two callers above derive it with ToColorRef.</summary>
        private void ApplyRichEditColour(uint colorRef, bool all)
        {
            var cf = Marshal.AllocHGlobal(92);
            try
            {
                for (var i = 0; i < 92; i++) Marshal.WriteByte(cf, i, 0);
                Marshal.WriteInt32(cf, 0, 92);                       // cbSize = sizeof(CHARFORMATW)
                Marshal.WriteInt32(cf, 4, unchecked((int)CFM_COLOR));
                Marshal.WriteInt32(cf, 8, 0);                        // no effect bits, and none cleared by accident
                Marshal.WriteInt32(cf, 20, unchecked((int)colorRef)); // crTextColor
                SendMessageW(handle, EM_SETCHARFORMAT, (nuint)(all ? SCF_ALL : SCF_SELECTION), cf);
            }
            finally
            {
                Marshal.FreeHGlobal(cf);
            }
        }

        /// <summary>The control's own background colour, 0xRRGGBB. Most controls take it through the host's
        /// WM_CTLCOLOR* answer and the engine's brush, but a RICHEDIT paints its own client and asks its host for
        /// nothing, so it takes the colour through its OWN message (EM_SETBKGNDCOLOR). Without that half an assignment
        /// was a SILENT NO-OP on a RichEdit: measured, the property read back the value and the client stayed white.</summary>
        public long? BackgroundColor
        {
            get => _backgroundColor;
            set
            {
                _backgroundColor = (uint?)value;
                ThemeTinted = false;   // a write through this door is the WRITER's colour, never the theme's
                if (TextColorBrush != IntPtr.Zero)
                {
                    DeleteObject(TextColorBrush);
                    TextColorBrush = IntPtr.Zero;
                    TextColorBrushColor = 0;
                }
                if (handle != IntPtr.Zero)
                {
                    if (IsRichEditWindow() && _backgroundColor.HasValue)
                        SendMessageW(handle, EM_SETBKGNDCOLOR, 0, (nint)ToColorRef(_backgroundColor.Value));
                    InvalidateRect(handle, IntPtr.Zero, true);
                }
                if (this is ButtonBase) EnsureButtonOwnerdraw();
            }
        }

        /// <summary>The RichEdit half, re-applied when the window is CREATED: a script (or a theme) can set the colour
        /// before the control has a handle, and the message needs one.</summary>
        private void ApplyRichEditBackground()
        {
            if (handle == IntPtr.Zero || !IsRichEditWindow() || !_backgroundColor.HasValue) return;
            SendMessageW(handle, EM_SETBKGNDCOLOR, 0, (nint)ToColorRef(_backgroundColor.Value));
        }

        /// <summary>True when the SCRIPT (or the theme's own tint) put a colour on this control. The theme walk
        /// reads it through this rather than the fields, which are private to Control: the owner-draw painter can
        /// read them because it is a Control member itself, while Form is a derived class and cannot.</summary>
        internal bool HasScriptColor => _backgroundColor.HasValue || _textColor.HasValue;

        /// <summary>One brush per colour, created on first use: a WM_CTLCOLOR* arrives per paint, and a brush per
        /// message is a GDI handle leaked per paint. Static because the two MDI hosts are not Control subclasses.</summary>
        internal static IntPtr GetOrCreateBrush(ref IntPtr brush, uint color)
        {
            if (brush == IntPtr.Zero)
                brush = CreateSolidBrush(color);
            return brush;
        }

        /// <summary>THE DARK HOST'S COLOUR MESSAGES, STATED ONCE. Three hosts (Form, MdiForm, MdiChild) each carried
        /// their own copy of this switch, of the brush fields and of the disposal, with the same four colours: one rule
        /// written three times is one edit away from drifting, and the pair that is NOT a Control subclass could not
        /// inherit a member anyway, which is why the brushes travel as refs. A control that states its own colour still
        /// wins, at the WM_CTLCOLOR* arm in the form's own handler.</summary>
        internal static IntPtr HandleCtlColorDark(IntPtr hdc, uint msg,
            ref IntPtr background, ref IntPtr edit, ref IntPtr listBox, ref IntPtr button)
        {
            Interpreter.SetTextColor(hdc, 0xFFFFFF);
            SetBkColor(hdc, 0x202020);
            return msg switch
            {
                WM_CTLCOLOREDIT => GetOrCreateBrush(ref edit, 0x303030),
                WM_CTLCOLORLISTBOX => GetOrCreateBrush(ref listBox, 0x282828),
                WM_CTLCOLORBTN => GetOrCreateBrush(ref button, 0x404040),
                _ => GetOrCreateBrush(ref background, 0x202020)
            };
        }

        /// <summary>The colour messages for a host that may be in ANY leg, so the two MDI hosts (which keep their own
        /// ThemeState and are not Control subclasses) serve a Custom palette as well as the dark leg: measured, an MDI
        /// document followed the leg but not the palette (its text box read 0x303030 in a CUSTOM frame whose palette was
        /// 0x3050A0), because a document was only ever told DARK or not. `state.IsDark()` alone cannot express a palette,
        /// so the state itself travels here.</summary>
        internal static IntPtr HandleCtlColorThemed(IntPtr hdc, uint msg, in ThemeState state,
            ref IntPtr background, ref IntPtr edit, ref IntPtr listBox, ref IntPtr button, ref IntPtr paletteBrush, ref uint paletteColor)
        {
            if (state.IsCustom())
            {
                var bg = state.Background();
                Interpreter.SetTextColor(hdc, ToColorRef(state.Text()));
                SetBkColor(hdc, ToColorRef(bg));
                if (paletteBrush == IntPtr.Zero || paletteColor != bg)
                {
                    if (paletteBrush != IntPtr.Zero) DeleteObject(paletteBrush);
                    paletteBrush = CreateSolidBrush(ToColorRef(bg));
                    paletteColor = bg;
                }
                return paletteBrush;
            }
            return HandleCtlColorDark(hdc, msg, ref background, ref edit, ref listBox, ref button);
        }
        /// <summary>The four brushes are GDI handles, so they go with the host that owns them.</summary>
        internal static void DisposeDarkBrushes(ref IntPtr background, ref IntPtr edit, ref IntPtr listBox, ref IntPtr button)
        {
            if (background != IntPtr.Zero) { DeleteObject(background); background = IntPtr.Zero; }
            if (edit != IntPtr.Zero) { DeleteObject(edit); edit = IntPtr.Zero; }
            if (button != IntPtr.Zero) { DeleteObject(button); button = IntPtr.Zero; }
            if (listBox != IntPtr.Zero) { DeleteObject(listBox); listBox = IntPtr.Zero; }
        }

        /// <summary>Undo EnsureButtonOwnerdraw for a button the THEME made owner-draw, which is the only case that
        /// may clear the style bit: a script can ask for BS_OWNERDRAW itself and keep it.</summary>
        internal void ClearThemeTint()
        {
            // Only a colour the THEME put there is the theme's to take off. Clearing unconditionally threw away the
            // SCRIPT's own colour, and that is what "statusBar.TextColor = ON_AMBER does not change anything" turned
            // into: the light leg of the theme pass wiped it, ApplyHostTheme then read HasScriptColor as false, and
            // the bar took the palette's ink instead (measured on the tool itself: the bar read back 9420016, which
            // is 0x8FBCF0, the palette's Text, with the script having set 15251293, ON_AMBER).

            if (ThemeTinted)
            {
                _backgroundColor = null;
                _textColor = null;
                ThemeTinted = false;
            }
            if (handle != IntPtr.Zero)
            {
                var st = (uint)GetWindowLongPtrW(handle, GWL_STYLE);
                if ((st & 0x0000000F) == BS_OWNERDRAW)
                    SetWindowLongPtrW(handle, GWL_STYLE, (nint)((st & ~0x0000000Fu) | BS_PUSHBUTTON));
                InvalidateRect(handle, IntPtr.Zero, true);
            }
        }
        internal void EnsureButtonOwnerdraw()
        {
            if (this is ButtonBase && handle != IntPtr.Zero)
            {
                var st = (uint)GetWindowLongPtrW(handle, GWL_STYLE);
                // BS_OWNERDRAW is a button TYPE, a VALUE in the low nibble (0x0B), NOT a bit: a CheckBox carries
                // BS_AUTOCHECKBOX (0x3), and 0x3 & 0x0B is non-zero, so testing it as a bit concluded owner-draw was
                // already set and never wrote it. Measured: a CheckBox stayed 0x50010003 after setting a colour,
                // while a Button (0x0 in that nibble, so the test passed) reached 0x5001000B.
                if ((st & 0x0000000F) != BS_OWNERDRAW)
                {
                    SetWindowLongPtrW(handle, GWL_STYLE, (nint)((st & ~0x0000000Fu) | BS_OWNERDRAW));
                    // A CheckBox or RadioButton that BECOMES owner-draw carries its current state over: Windows kept
                    // it until now, and the property reads the private one from this moment on.
                    if (this is CheckBox or RadioButton)
                        _ownerChecked = SendMessageW(handle, BM_GETCHECK, 0, 0) != IntPtr.Zero;
                    // A style change is NOT honoured until the frame is re-evaluated: measured, a button whose
                    // colour was just set still painted the CLASSIC face (0xF0F0F0) on its first paint and only
                    // became ownerdraw after the next one, so the colour appeared one interaction late. This is the
                    // documented way to make SetWindowLongPtr(GWL_STYLE) take effect at once.
                    SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0004 | 0x0020); // NOSIZE|NOMOVE|NOZORDER|FRAMECHANGED
                    InvalidateRect(handle, IntPtr.Zero, true);
                }
            }
        }

        /// <summary>The theme, applied to THIS control: the same work whether a DarkMode assignment reached it
        /// through the form's child walk or the control appeared under a form that was already dark. Measured, the
        /// second case is what a "dragged in while dark" control hit: the walk only runs on the CHANGE, so a button
        /// placed into a dark form got no colours (its painter declines without them, so it drew LIGHT) and a list
        /// view never got its LVM_SETBKCOLOR pair.
        ///
        /// The light leg passes NULL to SetWindowTheme, not "Explorer": the default theme is what a control started
        /// with, where the Explorer set left a Button and a ComboBox with a CLASSIC face after a round trip.</summary>
        internal void ApplyThemeToSelf(bool dark)
        {
            if (handle == IntPtr.Zero) return;
            if (this is StatusBar statusBar)
            {
                statusBar.ApplyHostTheme(dark);
                InvalidateRect(handle, IntPtr.Zero, true);
                return;
            }
            if (this is TreeView tree)
            {
                // A SysTreeView32 DRAWS ITS OWN ITEMS and never sends WM_CTLCOLOR*, so the form's palette never
                // reached it: it kept the control's own item colours, which inside a dark form is a LIGHT item
                // with dark text. Same family as the ListView beneath: these three messages are where its colours
                // live, and CLR_NONE (0xFFFFFFFF) in the light leg hands the item back to the default theme.
                // The colours come from the HOST's theme state, so the palette and the leg's own constants are
                // resolved in ONE place rather than respelled here.
                // And they are CONVERTED at this edge, because TVM_SET*COLOR take a COLORREF while a script colour
                // and every ThemeState value are 0xRRGGBB: passing them through unchanged handed the tree a colour
                // with R and B traded, which the symmetric dark and light constants hid (0x202020 and 0xFFFFFF are
                // their own swap) and the line colour did not (0x3F3F46 arrived as 0x463F3F). The ListView's own
                // member converts the same way, which is why that one always showed the palette correctly.
                var treeTheme = ThemeStateOf(tree);
                // A CUSTOM host's palette is a colour in EITHER leg, so it wins over CLR_NONE: handing the item
                // back to the default theme is right for a plain light form and wrong for a Custom one, and the
                // ListView beside it has always asked the palette that way. CLR_NONE therefore belongs to a
                // non-Custom light leg alone.
                var treeColored = dark || treeTheme.IsCustom();
                SendMessageW(handle, TVM_SETBKCOLOR, 0, treeColored ? (nint)ToColorRef(treeTheme.Background()) : CLR_NONE);
                SendMessageW(handle, TVM_SETTEXTCOLOR, 0, treeColored ? (nint)ToColorRef(treeTheme.Text()) : CLR_NONE);
                SendMessageW(handle, TVM_SETLINECOLOR, 0, treeColored ? (nint)ToColorRef(treeTheme.Border()) : CLR_NONE);
                InvalidateRect(handle, IntPtr.Zero, true);
                return;
            }
            if (this is ListView listView)
            {
                // A SysListView32 does not follow the theme message at all: its colours come from
                // LVM_SETBKCOLOR / LVM_SETTEXTBKCOLOR / LVM_SETTEXTCOLOR, which SetThemeColors sends.
                // A Custom host's PALETTE comes first, because this background is the engine's own call rather than
                // a themed face Windows draws: measured, a Custom form at 0x3050A0 left its list view on 0x202020,
                // the dark leg's constant. A Button deliberately keeps that leg; see its arm below.
                // Asking the host's resolvers rather than the `dark` argument leaves Dark, Light and System
                // EXACTLY as they were: with no palette set they fall back to the same leg default.
                // HostFormOf is the ONE walk up to the owning form, shared with the palette readers, and the Custom
                // test is what keeps a LEFTOVER palette member from colouring a Dark or Light form: the resolvers
                // fall back to the leg only while the palette is EMPTY, which is a different question.
                var hostForm = HostFormOf(this);
                var paletteHost = hostForm is { Theme: ThemeMode.Custom } f ? f : null;
                var listBg = paletteHost != null ? paletteHost.CustomBackground() : (dark ? 0x202020u : 0xFFFFFFu);
                var listFg = paletteHost != null ? paletteHost.CustomText() : (dark ? 0xFFFFFFu : 0x000000u);
                // SetThemeColors takes RGB and converts at its own Win32 edge, so the palette and the leg's
                // constants are both passed as the colours they are, with no second conversion here.
                listView.SetThemeColors(listBg, listFg);
                InvalidateRect(handle, IntPtr.Zero, true);
                return;
            }
            if (this is RichTextBox textControl)
            {
                // A TEXT CONTROL DRAWS ITS OWN CLIENT and never asks the host for a colour, so the fall-through below (a
                // sub-app plus WM_THEMECHANGED) leaves a RichEdit WHITE inside a dark form: measured 0xFFFFFF under a
                // 0x202020 form, and the same box was correct in a light form by luck rather than by following the
                // theme. A single-line EDIT was themed all along through WM_CTLCOLOREDIT, which is the difference that
                // hid this for a TextBox. The colours come from the HOST: a Custom palette first (its background and
                // text), else the leg's own pair, and a SCRIPT's colour always wins (the Button arm's rule, with the
                // same ThemeTinted flag, so the light leg can hand the control back).
                var controlHost = HostFormOf(this);
                var controlPalette = controlHost is { Theme: ThemeMode.Custom } controlForm ? controlForm : null;
                if (!textControl.HasScriptColor || textControl.ThemeTinted)
                {
                    if (dark || controlPalette != null)
                    {
                        textControl.BackgroundColor = controlPalette != null ? controlPalette.CustomBackground() : 0x202020;
                        textControl.TextColor = controlPalette != null ? controlPalette.CustomText() : 0xFFFFFF;
                        textControl.ThemeTinted = true;
                    }
                    else if (textControl.ThemeTinted)
                    {
                        // ClearThemeTint is the flag's OWNER: it takes the colours off only while the flag is set, so
                        // clearing it here first made the hand-back clear NOTHING and leave the dark tint in place.
                        textControl.ClearThemeTint();
                        // A RICHEDIT'S BACKGROUND IS A PROPERTY OF THE WINDOW, not a brush the host hands it at paint
                        // time, so clearing the member leaves the dark colour it was given: measured, the box stayed
                        // 0x202020 after the form went back to light. wParam 1 is the documented "use the system
                        // colour again", which is exactly what a light leg means here.
                        SendMessageW(handle, EM_SETBKGNDCOLOR, 1, IntPtr.Zero);
                        InvalidateRect(handle, IntPtr.Zero, true);
                    }
                }
                InvalidateRect(handle, IntPtr.Zero, true);
                return;
            }
            if (this is ToolBar or TabControl)
            {
                // A KIND THAT PAINTS ITS OWN FACE takes the engine's dark fill behind it and keeps the class drawing on
                // top: a ToolbarWindow32 (TBSTYLE_FLAT was removed so that it does, for the click fix) and a TabControl's
                // body. ONE arm for the two, because the technique is one thing, and `DarkBody` is the flag the erase
                // path reads. TWO KINDS ARE DELIBERATELY NOT HERE, each measured rather than assumed: a msctls_hotkey32
                // ignores both this fill AND the colour members (0xFFFFFF in a 0x202020 form in every state: it paints
                // its own field with the system window colour and asks the host for nothing), and a ReBarWindow32 is
                // unmeasurable without bands because its client is 0 px tall, so neither is claimed here.
                DarkBody = dark;   // this, not a per-kind field: the fill is Control's own
                InvalidateRect(handle, IntPtr.Zero, true);
                return;
            }
            if (this is CheckBox or RadioButton)
            {
                // A THEMED BUTTON PAINTS ITS OWN FACE while it is PRESSED (or hot), and that face is the theme's button
                // colour, not ours: measured on a dark form whose plate is 0x202020, a pressed check box came back
                // 0x484848 over its whole face AND its caption, which reads as "a grey band appears over the check box
                // and its text when I click it". With NO sub-app the class draws its classic face and takes the plate
                // from WM_CTLCOLORBTN, i.e. from the colour this panel already gives it, in every state. The light leg
                // hands the control back with the same NULL the theme walk passes everywhere else.
                if (!HasNonClientBar(handle))
                    SetWindowTheme(handle, SubAppForWindow(dark), "");
                SendMessageW(handle, WM_THEMECHANGED, 0, IntPtr.Zero);
                InvalidateRect(handle, IntPtr.Zero, true);
                return;
            }            if (this is Button button)
            {
                // A THEMED push button draws its own face, so the theme tints it and the engine's owner-draw painter
                // draws it. Only when the script chose no colour, and only a Button: a CheckBox or a RadioButton
                // follows the window theme already.
                var buttonPalette = HostFormOf(this) is { Theme: ThemeMode.Custom } buttonHost ? buttonHost : null;
                // ...and a colour the THEME itself put there does not count as the script's: without the ThemeTinted half, a
                // button tinted by the dark leg keeps that tint through a Custom leg (and reads the classic face after a
                // repaint), which is the stale-twin of the rule the RichEdit and HotKey arms already follow.
                if ((dark || buttonPalette != null) && (!button.HasScriptColor || button.ThemeTinted))
                {
                    button.BackgroundColor = buttonPalette != null ? buttonPalette.CustomBackground() : 0x202020;
                    button.TextColor = buttonPalette != null ? buttonPalette.CustomText() : 0xFFFFFF;
                    button.ThemeTinted = true;
                }
                else if (!dark && button.ThemeTinted)
                {
                    button.ClearThemeTint();   // the flag is the method's own: see the RichEdit arm above
                }
                InvalidateRect(handle, IntPtr.Zero, true);
                return;
            }
            if (!HasNonClientBar(handle))   // a bar keeps the flat one: see HasNonClientBar
                SetWindowTheme(handle, dark ? "DarkMode_Explorer" : null, dark ? "" : null);
            SendMessageW(handle, WM_THEMECHANGED, 0, IntPtr.Zero);
            InvalidateRect(handle, IntPtr.Zero, true);
        }

        /// <summary>True when the nearest enclosing Form, through any depth of parenting, is in dark mode.
        /// The chain is walked because a control can sit inside a panel rather than on the form itself.</summary>
        private bool UnderDarkForm()
        {
            for (var p = Parent; p != null; p = p.Parent)
                if (p is Form form) return form.DarkMode;
            return false;
        }
        /// <summary>A colour part way from one to another, in 0xRRGGBB: the thumb's shade in every palette without a
        /// second table to keep in step.</summary>
        internal static uint BlendColor(uint from, uint to, int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            var r = (int)((from >> 16) & 0xFF) + ((int)((to >> 16) & 0xFF) - (int)((from >> 16) & 0xFF)) * percent / 100;
            var g = (int)((from >> 8) & 0xFF) + ((int)((to >> 8) & 0xFF) - (int)((from >> 8) & 0xFF)) * percent / 100;
            var b = (int)(from & 0xFF) + ((int)(to & 0xFF) - (int)(from & 0xFF)) * percent / 100;
            return (uint)((r << 16) | (g << 8) | b);
        }
    }
}
