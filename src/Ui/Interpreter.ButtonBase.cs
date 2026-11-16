namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>
    /// Provides a base class for button controls, encapsulating common functionality and properties shared by all
    /// button types.
    /// </summary>
    /// <remarks>Inherit from this class to implement custom button controls. This class manages basic button
    /// initialization and native control creation, but does not provide button-specific behavior such as click
    /// handling. Derived classes should implement additional functionality as needed.</remarks>
    public abstract class ButtonBase : Control
    {
        private readonly uint _buttonStyle;

        // THE STATE A CALLER WROTE BEFORE THIS BUTTON HAD A WINDOW. A control is created hidden and its handle
        // appears at the first Show, so `Checked = true` (from the factory's `isChecked`, or from a `configure`
        // callback) would otherwise reach SendMessage on a null handle and vanish. It is HERE rather than twice in
        // the two subclasses: the rule and the state it stores are the same for a check box and a radio button, and
        // the two copies had byte-identical bodies, differing only by the style each constructor passes up.
        private bool _pendingChecked;
        private bool _hasPendingChecked;

        protected ButtonBase(string text, int x, int y, int width, int height, uint style, bool isChecked = false)
        {
            Text = text;
            InitBounds(x, y, width, height);
            _buttonStyle = style;
            RememberChecked(isChecked);
        }

        /// <summary>Record <paramref name="value"/> as the state to apply when the window appears, which is what a
        /// setter does while there is no handle to send it to.</summary>
        protected void RememberChecked(bool value)
        {
            _pendingChecked = value;
            _hasPendingChecked = true;
        }

        /// <summary>Apply the state a caller wrote before this button had a window. A plain push button has no state
        /// to apply, so the base answers nothing and only the two toggles override this.</summary>
        protected virtual void ApplyChecked(bool value) { }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_hasPendingChecked) return;
            ApplyChecked(_pendingChecked);
            _hasPendingChecked = false;
        }

        public virtual void RaiseClick() => OnClick(EventArgs.Empty);

        public ScriptFunction? CheckedChanged { get; set; }

        /// <summary>True for a check box or radio button: the only buttons whose state a click changes.</summary>
        public bool IsToggle => (_buttonStyle & BS_TYPEMASK) == BS_AUTOCHECKBOX
            || (_buttonStyle & BS_TYPEMASK) == BS_CHECKBOX
            || (_buttonStyle & BS_TYPEMASK) == BS_AUTORADIOBUTTON
            || (_buttonStyle & BS_TYPEMASK) == BS_RADIOBUTTON;

        public void RaiseCheckedChanged() => InvokeSafely(this, nameof(CheckedChanged), CheckedChanged, this, EventArgs.Empty);

        protected override void CreateHandle()
        {
            // A CHECK BOX AND A RADIO BUTTON ARE PAINTED BY THIS ENGINE, so their glyph follows the monitor exactly
            // as the text does. USER32 draws their box at the SYSTEM DPI's size whatever the window's DPI is
            // (measured on a two-monitor box: 19 px wide on the 144 DPI monitor AND on the 96 DPI one, and a box
            // CREATED there was even clipped to its 16 px client), so on the lower-DPI screen the box read about
            // 1.5x the size of the label beside it, and the only alternative was to make the control as tall as the
            // oversized glyph. The type nibble becomes BS_OWNERDRAW and DrawOwnerdrawButton sizes the glyph from the
            // control's own font (19 px at 144 DPI with the 16 px default font, 13 at 96 with 11). _buttonStyle keeps
            // the LOGICAL type, which is what IsToggle and a saved document read.
            var style = _buttonStyle;
            if (IsToggle) style = (style & ~BS_TYPEMASK) | BS_OWNERDRAW;
            CreateNativeControl("BUTTON", Text,
                WS_CHILD | WS_VISIBLE | style,
                0,
                ParentHandle,
                IntPtr.Zero);
        }
    }
}

