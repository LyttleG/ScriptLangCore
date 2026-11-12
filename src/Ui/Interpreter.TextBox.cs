namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class TextBox : TextControl
    {
        public ScriptFunction? TextChanged { get; set; }

        /// <summary>Whether the text WRAPS at the control&#39;s edge instead of running on past it. Off is what an
        /// EDIT does on its own (ES_AUTOHSCROLL, the caret scrolls sideways); on takes that bit away so the lines
        /// wrap, and a box that wraps can still be given a visible bar with <code>StyleAdd(WS_VSCROLL)</code> if a
        /// script wants one, which is the general door for a style bit.</summary>
        public bool WordWrap
        {
            get => _wordWrap;
            set
            {
                _wordWrap = value;
                // The control's OWN door (the same one a script uses) rather than the Form's private style helper:
                // an ES_ bit lives in the same style word, so StyleAdd/StyleRemove is the general route for it.
                if (Handle != IntPtr.Zero)
                {
                    if (value) StyleRemove(ES_AUTOHSCROLL);
                    else StyleAdd(ES_AUTOHSCROLL);
                }
            }
        }

        private bool _wordWrap;

        /// <summary>Raised when the native EDIT reports EN_CHANGE to its parent.</summary>
        public void RaiseTextChanged() => InvokeSafely(this, nameof(TextChanged), TextChanged, this, EventArgs.Empty);

        public TextBox(string text, int x, int y, int width, int height)
            : base(text, x, y, width, height) { }        protected override void CreateHandle()
        {
            // WS_VSCROLL is deliberately NOT here: it makes the bar PERMANENT whatever the box holds, while
            // ES_AUTOVSCROLL beside it is the automatic one that appears when the text actually needs it. Measured
            // before: every TextBox came up with the bar showing, i.e. a one-line field wearing a scrollbar.
            CreateNativeControl("EDIT", Text,
                WS_CHILD | WS_VISIBLE | ES_LEFT | ES_AUTOHSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | WS_BORDER,
                WS_EX_CLIENTEDGE,
                ParentHandle,
                IntPtr.Zero);

            // A log is APPENDED, and an EDIT's text limit applies to an insert where WM_SETTEXT is exempt from it,
            // so a log written by AppendText stops growing at the control's default cap (measured: 30,000 characters
            // against 144,000 the whole-string way). 0 lifts it to the maximum the control accepts.
            SendMessageW(handle, EM_SETLIMITTEXT, 0, 0);
        }

        /// <summary>A text box is the class that CAN be rebuilt on a DPI change: its whole content is one string,
        /// so a fresh window loses nothing.</summary>
        protected override bool CanRebuildOnDpiChange => true;

        /// <summary>The measurement a rebuild is decided on, and it is exact rather than a guess: a text box's
        /// non-client area is its borders plus one bar per direction whose style bit is set. The borders are
        /// WS_BORDER (1 px per side) + WS_EX_CLIENTEDGE (2 px per side) = 6 px and do NOT follow the DPI (measured
        /// 32 reserved against a 26 px metric at 144 DPI, and 23 against 17 at 96), while a bar is
        /// GetSystemMetricsForDpi at the window's DPI. Anything else means the window is still laid out for the DPI
        /// it was created at, which is the case a frame re-apply and a theme reload cannot fix.</summary>
        protected override bool NonClientAreaIsStale()
        {
            if (handle == IntPtr.Zero) return false;
            if (!GetWindowRect(handle, out var wr) || !GetClientRect(handle, out var cr)) return false;
            var dpi = (uint)GetDpiForWindow(handle);
            var style = (uint)GetWindowLongPtrW(handle, GWL_STYLE);
            var reservedW = (wr.Right - wr.Left) - (cr.Right - cr.Left);
            var reservedH = (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top);
            var expectedW = 6 + ((style & WS_VSCROLL) != 0 ? (int)GetSystemMetricsForDpi(SM_CXVSCROLL, dpi) : 0);
            var expectedH = 6 + ((style & WS_HSCROLL) != 0 ? (int)GetSystemMetricsForDpi(SM_CYHSCROLL, dpi) : 0);
            return reservedW != expectedW || reservedH != expectedH;
        }
    }
}

