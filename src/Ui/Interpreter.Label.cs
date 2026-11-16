namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class Label : TextControl
    {
        public Label(string text, int x, int y, int width, int height)
            : base(text, x, y, width, height) { }
        protected override void CreateHandle()
        {
            CreateNativeControl("STATIC", Text,
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                0,
                ParentHandle,
                IntPtr.Zero);
        }
    }
}

