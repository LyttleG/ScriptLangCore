namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public class GenericControl : Control
    {
        private readonly string _className;
        private readonly uint _style;
        private readonly uint _exStyle;

        /// <summary>The window CLASS this control was created with ("RichEdit20W", "STATIC", ...), and the style
        /// words it was created with. They are here because they are the three things about a RAW control that
        /// nothing else answers: its Kind is the wrapper it happens to be, so a document that has to REBUILD it
        /// (Json.Serialize of a form, the designer's model) otherwise cannot say which class and which bits
        /// CreateWinControl must be asked for, and a rebuild that guesses the style gets a different window.
        /// Control.ReadStyle answers the CURRENT word; these are the creation ones.</summary>
        public string ClassName => _className;
        internal override string NativeClass => _className;
        public long Style => _style;
        public long ExStyle => _exStyle;

        public GenericControl(string className, string text, int x, int y, int width, int height, uint style = 0, uint exStyle = 0)
        {
            _className = className;
            Text = text;
            InitBounds(x, y, width, height);

            if (style == 0)
            {
                // The BASE every control window needs, then the class's own refinement: a caller that names no style
                // gets a CHILD, VISIBLE window, which is what the raw door this replaces (WinApi.CreateControl)
                // defaults to. Measured before this: the class default REPLACED the base, so a GenericControl for
                // "BUTTON" came out as BS_PUSHBUTTON with no WS_CHILD and no WS_VISIBLE, i.e. a top-level, invisible
                // window whose clicks were sent to the desktop rather than to its form.
                style = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
                style |= className.ToUpperInvariant() switch
                {
                    "EDIT" => ES_AUTOHSCROLL | WS_BORDER,
                    "BUTTON" => BS_PUSHBUTTON,
                    "LISTBOX" => LBS_NOTIFY | WS_VSCROLL | WS_BORDER,
                    "COMBOBOX" => CBS_DROPDOWNLIST | WS_VSCROLL,
                    _ => 0u
                };
            }
            _style = style;
            _exStyle = exStyle;
        }

        protected override void CreateHandle()
        {
            CreateNativeControl(
                _className,
                Text,
                WS_CHILD | WS_VISIBLE | _style,
                _exStyle,
                ParentHandle,
                IntPtr.Zero);
        }

        public void RaiseClick() => OnClick(EventArgs.Empty);
    }
}

