namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>A prompt, in the shape the other dialogs already have: set what it asks, then ShowDialog. The
    /// one-shot Application.InputBox builds one of these; a script (or a suite) that has to reach the prompt WHILE
    /// it is up keeps the object rather than a handle, and answers it through the object's own two doors.</summary>
    public class InputBox
    {
        public string Title { get; set; } = "Input";
        public string Prompt { get; set; } = "";
        public string Value { get; set; } = "";

        /// <summary>Its size in design pixels, as a Form&#39;s is, so it scales with the DPI. The defaults are what
        /// the built-in prompt uses; a size too small to hold the buttons is raised to the minimum rather than
        /// opened as a sliver.</summary>
        public int Width { get; set; } = 384;
        public int Height { get; set; } = 200;

        /// <summary>What the prompt line, the input and the buttons are written in. Left alone they keep the
        /// engine&#39;s own font at ONE STEP UP from its default, which is what makes a prompt read like a message box
        /// rather than like a dense panel: the engine opens at roughly an 8pt equivalent, and this is 10.</summary>
        public string FontName { get; set; } = "";
        public int FontSize { get; set; } = 10;
        public bool FontBold { get; set; }

        /// <summary>The INPUT&#39;s metrics, so a longer prompt line or a bigger font has room instead of clipping: its
        /// height, and the insets the line and the input keep from the window&#39;s edges. The buttons follow the
        /// height, and the layout is built from these rather than from constants.</summary>
        public int TextHeight { get; set; } = 56;

        /// <summary>The space between the prompt line and the input, 16 by default: they read as one block when
        /// nothing separates them, which is what the first version of this layout did with its 6 pixel gap.</summary>
        public int TextPadding { get; set; } = 16;
        public int TextLeft { get; set; } = 12;
        public int TextRight { get; set; } = 12;

        private TextBox? _input;

        /// <summary>The input control itself, while the prompt is up: the same door every other control has, so a
        /// script can style or fill its own prompt rather than only being able to answer one. Null before the first
        /// ShowDialog.</summary>
        public TextBox? Input => _input;

        internal void AttachInput(TextBox input) => _input = input;

        /// <summary>Where it opens when <see cref="StartPosition"/> is 0, in screen pixels.</summary>
        public int Left { get; set; }
        public int Top { get; set; }

        /// <summary>Where it opens, numbered as a Form&#39;s StartPosition is: <code>0</code> where Left and Top say,
        /// <code>1</code> centred on the screen, <code>2</code> centred on the window it was opened over. The default
        /// is <code>2</code>, because a prompt belongs over the thing that asked for it.</summary>
        public int StartPosition { get; set; } = 2;

        /// <summary>Whether it carries a close box, which is the system menu and the X in it. A prompt without one
        /// is still cancellable with Escape, so nothing becomes unanswerable.</summary>
        public bool CloseBox { get; set; } = true;

        /// <summary>Whether the prompt OWNS the input while it is up, which it does by default: the window that was
        /// active is disabled until the prompt is answered. <code>false</code> leaves it enabled, so the application
        /// behind the prompt still takes clicks while the prompt waits for its answer.</summary>
        public bool Modal { get; set; } = true;

        /// <summary>Which monitor the prompt opens on, 0-based in the order Windows enumerates them, numbered and
        /// refused the way a Form&#39;s Screen is. Left alone it MIRRORS the window the prompt was opened over, which
        /// is what centring on the opener already does; assigned before ShowDialog it is remembered and applied as
        /// the prompt is created, and assigned while the prompt is up it moves it. Both go through the Form&#39;s own
        /// Screen, so the two cannot drift about what a screen number means.</summary>
        public int Screen
        {
            get
            {
                var form = Window != 0 ? Control.FromHandle((nint)Window) as Form : null;
                if (form != null) { return form.Screen; }
                if (_screenRequested >= 0) { return _screenRequested; }
                // The mirror: the screen of the window this prompt would be opened over, read through the same
                // getter a form answers with. Not a form open there means screen 0, which is the primary.
                var opener = Control.FromHandle(GetActiveWindow()) as Form;
                return opener?.Screen ?? 0;
            }
            set
            {
                _screenRequested = value;
                var form = Window != 0 ? Control.FromHandle((nint)Window) as Form : null;
                if (form != null) { form.Screen = value; }
            }
        }

        private int _screenRequested = -1;

        /// <summary>Whether a screen was asked for, which is what tells "leave it where the opener is" from
        /// "screen 0": an index has no spare value to mean unset, so the request needs its own flag.</summary>
        internal bool ScreenRequested => _screenRequested >= 0;

        /// <summary>Which button is the DEFAULT: 0 for OK (which is what it is) and 1 for Cancel. It is the one Enter
        /// activates and the one Windows DRAWS as the default (BS_DEFPUSHBUTTON), and the input keeps the caret
        /// either way, because a prompt that focused a button would be one a script could not type into. Escape
        /// cancels whatever this says.</summary>
        public int DefaultButton
        {
            get => _defaultButton;
            set { _defaultButton = value; ApplyDefaultButton(); }
        }

        /// <summary>The buttons themselves, so the default is applied as the prompt is created and can be moved while
        /// it is up, without the caller needing to know which style bit that is.</summary>
        internal void AttachButtons(Button ok, Button cancel)
        {
            _okButton = ok;
            _cancelButton = cancel;
            ApplyDefaultButton();
        }

        private void ApplyDefaultButton()
        {
            if (_okButton == null || _cancelButton == null) { return; }
            if (_defaultButton == 1)
            {
                _okButton.StyleRemove(BS_DEFPUSHBUTTON);
                _cancelButton.StyleAdd(BS_DEFPUSHBUTTON);
            }
            else
            {
                _cancelButton.StyleRemove(BS_DEFPUSHBUTTON);
                _okButton.StyleAdd(BS_DEFPUSHBUTTON);
            }
        }

        private int _defaultButton;

        /// <summary>WHICH BUTTON ANSWERS ON ITS OWN, and after how long, as a map of the code to the milliseconds:
        /// <code>{ 1: 2000 }</code> is "the OK button after two seconds" and <code>{ 2: 2000 }</code> is Cancel. The
        /// codes are the ones ShowDialog already answers with, so a script never has to know a control id. The earliest
        /// deadline wins when the map names both, and a missing or empty map means the prompt waits for a hand.</summary>
        public object? Timeouts { get; set; }

        private Button? _okButton;
        private Button? _cancelButton;
        /// <summary>ACCEPT the prompt, the way its OK button does, so the caller&#39;s ShowDialog answers with the text that
        /// is in the box. What it posts is the engine&#39;s OWN contract for that button: the prompt&#39;s loop reads WM_COMMAND
        /// with the button&#39;s handle in lParam, while a real click SENDS that message to the window rather than queueing
        /// it, which is why making the click (or posting BM_CLICK) left the loop waiting and the prompt up. Measured.</summary>
        public void Accept()
        {
            if (_okButton == null) { return; }
            PostMessage(0x0111 /* WM_COMMAND */, _okButton.GetId(), _okButton.Handle);
        }

        /// <summary>CANCEL the prompt, the way its Cancel button and the Escape key do: the caller&#39;s ShowDialog answers
        /// nothing. Posted for the same reason Accept is.</summary>
        public void Cancel()
        {
            if (_cancelButton == null) { return; }
            PostMessage(0x0111 /* WM_COMMAND */, _cancelButton.GetId(), _cancelButton.Handle);
        }

        /// <summary>The prompt window&#39;s STYLE word, read the way a control reads its own. The InputBox is not a
        /// Control, so it MIRRORS the doors a control has (PostMessage, SendMessage) rather than inheriting them, and
        /// reading the frame the window ENDED UP with is one of them.</summary>
        public long ReadStyle(bool bEx = false) => ReadWindowStyle((nint)Window, bEx);

        /// <summary>The prompt window&#39;s own rectangle, which is what it BECAME rather than what was asked for:
        /// Width and Height above are the REQUEST, and a prompt asked for 10x10 opens at the engine&#39;s minimum.
        /// That difference is the whole reason this exists beside them.</summary>
        public Rect WindowRect => WindowRectOf((nint)Window);

        private ThemeMode? _themeChoice;
        private ThemeMode _inheritedTheme = ThemeMode.System;

        /// <summary>The theme it opens with. Left UNSET it takes the theme of the window it was opened over, which is
        /// what makes a prompt in a dark or paletted application match it; ASSIGNING one overrides that. Until it has
        /// been shown, and with nothing assigned, this answers <c>System</c>, because the theme it will inherit is
        /// not known until then.</summary>
        public ThemeMode Theme { get => _themeChoice ?? _inheritedTheme; set => _themeChoice = value; }

        internal void InheritTheme(ThemeMode theme) => _inheritedTheme = theme;
        internal bool ThemeAssigned => _themeChoice.HasValue;

        /// <summary>Sets the prompt&#39;s icon, in the same shapes <see cref="Form.SetIcon"/> takes: a character to
        /// render, an icon handle, or 0 for none, with the optional size and colour. A prompt IS a form, but its
        /// form is the engine&#39;s own and a script never holds a handle to it, so this is the door: the request is
        /// REPLAYED through the Form&#39;s own SetIcon, which renders a glyph and holds the defaults.</summary>
        public long SetIcon(object? icon, int? size = null, long? color = null)
        {
            _iconRequest = (icon, size, color);
            var form = Window != 0 ? Control.FromHandle((nint)Window) as Form : null;
            return form != null ? ApplyIcon(form) : 0;
        }

        /// <summary>Replays the stored request through the Form&#39;s own SetIcon, so the two cannot drift. Nothing
        /// asked of it leaves the window with what it had, which is the executable&#39;s own icon.</summary>
        internal long ApplyIcon(Form form)
        {
            if (_iconRequest is not { } req) { return 0; }
            // The defaults stay in the Form: a colour cannot be given without a size, so the three shapes below are
            // exactly the Form&#39;s own overloads and no constant is repeated here.
            if (req.Color is { } color && req.Size is { } size) { return form.SetIcon(req.Icon, size, color); }
            if (req.Size is { } only) { return form.SetIcon(req.Icon, only); }
            return form.SetIcon(req.Icon);
        }

        private (object? Icon, int? Size, long? Color)? _iconRequest;

        /// <summary>The prompt&#39;s own window while it is up, and 0 the rest of the time.</summary>
        public long Window => (long)NativeApp.InputBoxWindow;

        /// <summary>Shows it and waits: 1 when it was answered, 2 when it was cancelled or closed. What was typed
        /// is in <see cref="Value"/>.</summary>
        public int ShowDialog() => NativeApp.ShowInputBox(this);

        // The two doors Control has, applied to the prompt's own window, and built on the engine's ONE message core
        // (the same calls WinApi.SendMessage/PostMessage and the control door make), so no handle appears in a call.
        public bool PostMessage(object msg, object wParam, object lParam) =>
            Window != 0 && PostMessageScript(Window, Convert.ToInt64(msg), Convert.ToInt64(wParam), ToNInt(lParam).ToInt64());

        public long SendMessage(object msg, object wParam, object lParam) =>
            Window == 0 ? 0L : SendMessageScript(Window, Convert.ToInt64(msg), Convert.ToInt64(wParam), ToNInt(lParam).ToInt64());
    }
}

