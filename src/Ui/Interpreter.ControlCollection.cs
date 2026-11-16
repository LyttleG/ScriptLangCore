namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public class ControlCollection(Control owner) : IEnumerable<Control>
    {
        private readonly List<Control> _controls = [];

        public void Add(Control control)
        {
            control.Parent = owner;
            _controls.Add(control);
            if (owner.Handle == IntPtr.Zero)
                owner.CreateControl();
            control.CreateControl();
            // Console.WriteLine($"[DBG Controls.Add] {control.GetType().Name}: l={control.Left}, t={control.Top}, w={control.Width}, h={control.Height}, hwnd={control.Handle}");
        }

        /// <summary>How many controls this form holds. A property, which is what an MDI host's plain
        /// <code>List&lt;Control&gt;</code> answers with too, so both spell the count the same way.</summary>
        public int Count => _controls.Count;

        /// <summary>The control at that index, in the order it was added, or a refusal naming the range.</summary>
        public Control Get(long index)
        {
            if (index < 0 || index >= _controls.Count)
            {
                throw new ScriptException($"Controls.Get({index}) is out of range: this form holds {_controls.Count} control(s), so the indices are 0..{_controls.Count - 1}.");
            }
            return _controls[(int)index];
        }

        public void Remove(Control control)
        {
            if (_controls.Remove(control))
                control.Parent = null;
        }

        public IEnumerator<Control> GetEnumerator() => _controls.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

