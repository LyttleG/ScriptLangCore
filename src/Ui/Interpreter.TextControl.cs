namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>The controls that are constructed with their TEXT, so the two things every one of them does
    /// with it (set Text, then InitBounds) happen once. Label and TextBox each stated both, identically, which
    /// tools/check-dup-bodies.sh reports at its floor of 4; a derived constructor here is one base call and is
    /// invisible to that checker by construction, because its collector wants a declaration ending in a paren
    /// and this one ends with an empty body.</summary>
    public abstract class TextControl : Control
    {
        protected TextControl(string text, int x, int y, int width, int height)
        {
            Text = text;
            InitBounds(x, y, width, height);
        }
    }
}

