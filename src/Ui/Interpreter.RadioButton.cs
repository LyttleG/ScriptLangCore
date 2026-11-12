namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class RadioButton : ButtonBase
    {
        public RadioButton(string text, int x, int y, int width, int height, bool isChecked = false)
            : base(text, x, y, width, height, BS_AUTORADIOBUTTON, isChecked)
        {
        }

        /// <summary>True when this control's type is BS_OWNERDRAW, read from the style's low NIBBLE because the
        /// button type is a VALUE there, not a bit.</summary>
        internal bool IsOwnerdrawType => handle != IntPtr.Zero && (GetWindowLongPtrW(handle, GWL_STYLE) & 0x0000000F) == BS_OWNERDRAW;

        /// <summary>A radio button's state is its own: the window's check bit, or the engine's own for an
        /// owner-draw button (which has no state to ask).</summary>
        protected override void ApplyChecked(bool value) => Checked = value;

        public bool Checked
        {
            get => IsOwnerdrawType ? _ownerChecked : SendMessageW(handle, BM_GETCHECK, 0, IntPtr.Zero) == (IntPtr)BST_CHECKED;
            set
            {
                if (IsOwnerdrawType)
                {
                    _ownerChecked = value;
                    InvalidateRect(handle, IntPtr.Zero, true);
                    // ONE RADIO PER GROUP, which Windows itself did for BS_AUTORADIOBUTTON and cannot do for an
                    // OWNERDRAW button, because it keeps no state at all for one (see CheckBox). The group is the
                    // PARENT radio buttons, which is Win32 own rule: a group is bounded by the parent, not by
                    // where the radios sit inside it.
                    if (value)
                        foreach (var sibling in Siblings())
                            if (sibling is RadioButton other && !ReferenceEquals(other, this)) other.Checked = false;
                }
                else if (handle != IntPtr.Zero && IsWindow(handle))
                    SendMessageW(handle, BM_SETCHECK, value ? BST_CHECKED : (nuint)BST_UNCHECKED, IntPtr.Zero);
                else
                    RememberChecked(value);
            }
        }

        private IEnumerable<Control> Siblings() => Parent is Form host ? host.Controls : [];
    }
}

