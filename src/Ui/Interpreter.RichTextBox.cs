namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>A multi-line text control that formats each RUN (a colour per line, a stated row height, an exact
    /// selection), which is what a log or an editor needs and what a plain EDIT cannot do at all.
    ///
    /// IT PICKS ITS OWN WINDOWS CLASS, once per process: msftedit's RICHEDIT50W when that library loads (the generation
    /// with PARAFORMAT2 and the fuller character format), RichEdit20W from riched20 when it does not, and EDIT as the
    /// last resort. That ladder used to be a script's job, together with LoadLibrary and a CreateWinControl call, and
    /// two boxes choosing differently is the one failure a script cannot see.
    ///
    /// The library is loaded and KEPT: unloading a native while the process might still call into it is the crash this
    /// repository records elsewhere, and the class it registers is not unregistered by freeing it either.</summary>
    public sealed class RichTextBox : TextControl
    {
        public ScriptFunction? TextChanged { get; set; }

        private static string _className = "";

        /// <summary>The window class, resolved once. RICHEDIT50W first, RichEdit20W second, EDIT last: the members
        /// that depend on a real RichEdit (per-run colour, CharHeight, LineSpacing) answer 0 or ignore the colour on
        /// the fallback, which is the same contract a plain TextBox has.</summary>
        internal static string ClassName
        {
            get
            {
                if (_className.Length > 0) return _className;
                if (NativeLibrary.TryLoad("msftedit.dll", out _)) _className = "RICHEDIT50W";
                else if (NativeLibrary.TryLoad("riched20.dll", out _)) _className = "RichEdit20W";
                else _className = "EDIT";
                return _className;
            }
        }

        internal override string NativeClass => ClassName;

        /// <summary>The Windows class this box really got, for a script that has to KNOW: "RICHEDIT50W" when msftedit
        /// is there, "RichEdit20W" on the riched20 fallback, and "EDIT" when neither could be created, where only the
        /// per-run formatting is missing. A tool that reports its engine, or that decides whether it can colour a run,
        /// asks this rather than loading the libraries itself and repeating the ladder.</summary>
        public string WindowClass => ClassName;

        /// <summary>Whether the text WRAPS at the control's edge. TRUE is the default here, because a rich text box is
        /// read rather than typed into: a long line in a log has no horizontal bar to reach its end.
        /// A RICHEDIT TAKES ITS WRAP WHEN ITS WINDOW IS CREATED, which is measured: turning wrapping ON later works
        /// (the style bit plus EM_SETTARGETDEVICE), turning it OFF again does not, so this is set BEFORE the window
        /// exists and a value written afterwards only restates the style bit. A script that must switch at run time
        /// rebuilds the window, which is one call to the factory.</summary>
        public bool WordWrap
        {
            get => _wordWrap;
            set
            {
                _wordWrap = value;
                if (Handle != IntPtr.Zero)
                {
                    if (value) StyleRemove(ES_AUTOHSCROLL);
                    else StyleAdd(ES_AUTOHSCROLL);
                }
            }
        }

        private bool _wordWrap = true;

        /// <summary>Raised when the class reports EN_CHANGE to its parent, like a TextBox's.</summary>
        public void RaiseTextChanged() => InvokeSafely(this, nameof(TextChanged), TextChanged, this, EventArgs.Empty);

        public RichTextBox(string text, int x, int y, int width, int height)
            : base(text, x, y, width, height) { }

        protected override void CreateHandle()
        {
            // THE WRAP IS DECIDED HERE, because a RichEdit fixes it at creation (see WordWrap): ES_AUTOHSCROLL is the
            // "one row per line, the caret scrolls sideways" bit, so it is added only when a script asked for it.
            var style = WS_CHILD | WS_VISIBLE | ES_LEFT | ES_MULTILINE | ES_AUTOVSCROLL | WS_BORDER;
            if (!_wordWrap) style |= ES_AUTOHSCROLL;
            CreateNativeControl(ClassName, Text, style,
                WS_EX_CLIENTEDGE,
                ParentHandle,
                IntPtr.Zero);
            // A log is APPENDED, and the class's default text cap applies to an insert: 0 lifts it, exactly as the
            // same line does for a TextBox (measured there: 30,000 characters against 144,000 the whole-string way).
            SendMessageW(handle, EM_SETLIMITTEXT, 0, 0);
        }

        // NO REBUILD ON A DPI CHANGE, deliberately, where a TextBox takes one: a rebuilt window is created from `Text`
        // alone, so it would come back with the WHOLE DOCUMENT in the default ink and the view at its first row, which
        // is a far worse outcome than a non-client area laid out for the previous DPI (the frame re-apply and the
        // repaint cover that). Measured on a monitor move: this control keeps its per-run colours and its place, so it
        // is not rebuilt anyway.
    }
}

