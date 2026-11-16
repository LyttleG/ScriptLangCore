namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>
    /// Inherited by Button, CheckBox, and RadioButton to share common button styles and click handling.
    /// </summary>
    public sealed class Button : ButtonBase
    {
        public Button(string text, int x, int y, int width, int height)
            : base(text, x, y, width, height, BS_PUSHBUTTON)
        {
        }

        public override void RaiseClick() => OnClick(EventArgs.Empty);
    }
}

