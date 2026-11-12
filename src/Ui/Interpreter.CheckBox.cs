namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class CheckBox : ButtonBase
    {
        public CheckBox(string text, int x, int y, int width, int height, bool isChecked = false)
            : base(text, x, y, width, height, BS_AUTOCHECKBOX, isChecked)
        {
        }

        /// <summary>True when this control's type is BS_OWNERDRAW, read from the style's low NIBBLE because the
        /// button type is a VALUE there, not a bit.</summary>
        internal bool IsOwnerdrawType => handle != IntPtr.Zero && (GetWindowLongPtrW(handle, GWL_STYLE) & 0x0000000F) == BS_OWNERDRAW;

        /// <summary>A check box's state is its own: the window's check bit, or the engine's own for an owner-draw
        /// button (which has no state to ask).</summary>
        protected override void ApplyChecked(bool value) => Checked = value;

        public bool Checked
        {
            get => IsOwnerdrawType ? _ownerChecked : SendMessageW(handle, BM_GETCHECK, 0, IntPtr.Zero) == (IntPtr)BST_CHECKED;
            set
            {
                if (IsOwnerdrawType) { _ownerChecked = value; InvalidateRect(handle, IntPtr.Zero, true); }
                else if (handle != IntPtr.Zero && IsWindow(handle))
                    SendMessageW(handle, BM_SETCHECK, value ? BST_CHECKED : (nuint)BST_UNCHECKED, IntPtr.Zero);
                else
                    RememberChecked(value);
            }
        }
    }
}

